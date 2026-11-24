import { useEffect, useRef, useState } from 'react'
import { Check, Lightning, Sparkle } from '@phosphor-icons/react'
import { useQueryClient } from '@tanstack/react-query'
import { api, describeError, get } from '../api/client'
import { isBeingRead, useRefreshFlow, useSystemInfo } from '../api/hooks'
import { renewsIn } from '../pages/AiUsage'
import type { AnalysisRun, Entry, Flow, GuideChoice, Job } from '../api/types'
import { flushOutbox } from '../offline/outbox'
import { ChooseContent } from './ChooseContent'
import { Alert, useToast } from './ui'

type Step = 'notes' | 'analyse' | 'document'
const STEPS: { id: Step; label: string }[] = [
  { id: 'notes', label: 'Reading your notes' },
  { id: 'analyse', label: 'Analysing the workflow' },
  { id: 'document', label: 'Making the document' },
]

const sleep = (ms: number) => new Promise<void>(r => setTimeout(r, ms))

/**
 * The fast path: one button that goes from the captured notes to the finished implementation document. It waits until voice and photos have
 * been read, analyses, then makes the PDF, and shows which of the three steps it is on. Everything it does is also available step by step below.
 */
export function GenerateCard({ flow, hasDocument, canCapture, pollMs = 2000 }: { flow: Flow; hasDocument: boolean; canCapture: boolean; pollMs?: number }) {
  const toast = useToast()
  const qc = useQueryClient()
  const refresh = useRefreshFlow()
  const [step, setStep] = useState<Step | null>(null)
  const capacity = useSystemInfo().data?.capacity
  const [error, setError] = useState<string | null>(null)
  const alive = useRef(true)
  useEffect(() => () => { alive.current = false }, [])
  /** Set while the person is choosing what the document should hold; answered with their choice, or null when they cancel. */
  const [asking, setAsking] = useState<((c: GuideChoice | null) => void) | null>(null)
  const ask = () => new Promise<GuideChoice | null>(resolve => setAsking(() => (c: GuideChoice | null) => { setAsking(null); resolve(c) }))

  const analysed = flow.currentVersionNo > 0
  if (!canCapture || flow.entryCount === 0 || (analysed && hasDocument && step === null)) return null

  /** Checks every couple of seconds until `done` returns a value; gives up after `maxMs`. */
  async function until<T>(check: () => Promise<T | null>, maxMs: number, what: string): Promise<T> {
    const stop = Date.now() + maxMs
    for (;;) {
      const v = await check()
      if (v !== null) return v
      if (!alive.current) throw new Error('stopped')
      if (Date.now() > stop) throw new Error(`${what} is taking longer than expected. It may still finish: check the sections below.`)
      await sleep(pollMs)
    }
  }

  async function go() {
    setError(null)
    try {
      // 1. every note must have reached the server and every photo and recording must have been turned into text
      setStep('notes')
      await flushOutbox()
      await until(async () => {
        const entries = await get<Entry[]>(`flows/${flow.id}/entries`)
        const failed = entries.filter(e => !e.isDeleted && e.kind !== 'Text' && e.transcriptState === 'Failed').length
        if (failed > 0) throw new Error(`${failed} recording${failed === 1 ? '' : 's'} or photo${failed === 1 ? '' : 's'} could not be read. Use "Try again" on ${failed === 1 ? 'it' : 'them'} in the notes list, or remove ${failed === 1 ? 'it' : 'them'}.`)
        return entries.some(isBeingRead) ? null : true
      }, 5 * 60_000, 'Reading the notes')

      // 2. the analysis (skipped when the workflow was already analysed and only the document is missing)
      if (!analysed) {
        setStep('analyse')
        const run = await api<AnalysisRun>(`flows/${flow.id}/analyses`, { method: 'POST', idempotent: true, body: { mode: 'Initial', entryIds: null } })
        const finished = await until(async () => {
          const runs = await get<AnalysisRun[]>(`flows/${flow.id}/analyses`)
          const mine = runs.find(r => r.id === run.id)
          return mine && mine.status !== 'Queued' && mine.status !== 'Running' ? mine : null
        }, 10 * 60_000, 'The analysis')
        refresh(flow.id, flow.projectId)
        if (finished.status !== 'Succeeded') throw new Error(finished.status === 'Cancelled' ? 'The analysis was cancelled.' : `The analysis did not finish: ${finished.errorMessage ?? 'unknown reason'}`)
      }

      // 3. the document: the person first sees its headings and chooses which to print
      setStep('document')
      const choice = await ask()
      if (choice === null) { toast('Nothing was made. Your notes are analysed: press "Make the document" when you are ready.'); return }
      const job = await api<Job>(`flows/${flow.id}/guide`, { method: 'POST', body: choice, idempotent: true })
      const result = await until(async () => {
        const j = await get<Job>(`jobs/${job.jobId}`)
        return j.state === 'Queued' || j.state === 'Running' ? null : j
      }, 10 * 60_000, 'The document')
      void qc.invalidateQueries({ queryKey: ['flow-documents', flow.id] })
      void qc.invalidateQueries({ queryKey: ['library'] })
      void qc.invalidateQueries({ queryKey: ['ai-usage'] })
      if (result.state !== 'Succeeded') throw new Error(result.message ?? 'The document could not be made.')
      toast('The implementation document is ready.', 'good')
      document.getElementById('result')?.scrollIntoView?.({ behavior: 'smooth' })
    } catch (e) {
      if (alive.current) setError(e instanceof Error && !('status' in e) ? e.message : describeError(e))
    } finally {
      if (alive.current) setStep(null)
    }
  }

  const at = step ? STEPS.findIndex(s => s.id === step) : -1
  const skipAnalysis = analysed
  return (
    <section className="generate" aria-label="Make the document in one step">
      <div className="generate-main">
        <div>
          <h2><Lightning size={22} weight="fill" aria-hidden />Ready? Make the document in one step</h2>
          <p className="muted">{analysed ? 'The workflow is analysed. This makes the implementation document.' : 'Reads your notes, analyses the workflow and makes the implementation document. You do not need to do anything in between.'}</p>
          {capacity && !capacity.reached && capacity.requestsLeft !== null && <p className="muted small" data-testid="ai-left">Free AI left today: {capacity.requestsLeft.toLocaleString('en-IN')} request{capacity.requestsLeft === 1 ? '' : 's'} (an analysis uses 1 or 2). Renews in {renewsIn(capacity.resetsAt)}.</p>}
        </div>
        <button className="btn primary lg" onClick={() => void go()} disabled={step !== null}>
          <Sparkle size={20} weight="fill" aria-hidden />{step ? 'Working…' : analysed ? 'Make the document' : 'Analyse and make the document'}
        </button>
      </div>
      {step && (
        <ol className="gen-steps" role="status" aria-label="Progress">
          {STEPS.filter(s => !(skipAnalysis && s.id === 'analyse')).map(s => {
            const i = STEPS.indexOf(s)
            return (
              <li key={s.id} className={i < at ? 'done' : i === at ? 'now' : ''}>
                <span className="tick" aria-hidden>{i < at ? <Check size={13} weight="bold" /> : null}</span>{s.label}{i === at ? '…' : ''}
              </li>
            )
          })}
        </ol>
      )}
      {error && <Alert tone="error">{error}</Alert>}
      {asking && <ChooseContent title="What should the document hold?" outlinePath={`flows/${flow.id}/guide-outline`} confirmLabel="Make the PDF" onChoose={c => asking(c)} onClose={() => asking(null)} />}
    </section>
  )
}
