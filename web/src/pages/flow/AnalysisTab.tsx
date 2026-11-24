import { useEffect, useRef, useState } from 'react'
import { Sparkle } from '@phosphor-icons/react'
import { api, ApiError, describeError } from '../../api/client'
import { isBeingRead, useClarifications, useEntries, useRefreshFlow, useRuns } from '../../api/hooks'
import type { AnalysisRun, Flow } from '../../api/types'
import { Alert, Skeletons, useToast } from '../../components/ui'
import { flushOutbox } from '../../offline/outbox'
import { useQueued } from '../../offline/useQueued'
import { useSession } from '../../state/session'
import { QuestionCard } from './QuestionCard'

const active = (r?: AnalysisRun) => !!r && (r.status === 'Queued' || r.status === 'Running')

/** Turn the captured notes into a structured flow, then answer the questions the AI could not settle. */
export function AnalysisTab({ flow, canCapture }: { flow: Flow; canCapture: boolean }) {
  const { isLeadIn } = useSession()
  const toast = useToast()
  const refresh = useRefreshFlow()
  const runs = useRuns(flow.id)
  const questions = useClarifications(flow.id)
  const entries = useEntries(flow.id)
  const queued = useQueued(flow.id)
  const [starting, setStarting] = useState(false)
  const [nothingNew, setNothingNew] = useState<string | null>(null)       // the server's words when there is nothing new to analyse
  const watching = useRef<string | null>(null)       // the run this person started, so the result is announced once when it finishes

  const latest = runs.data?.[0]
  const running = active(latest) || starting
  const waiting = queued.filter(q => !q.rejected).length
  const refused = queued.filter(q => q.rejected).length
  const input = (entries.data ?? []).filter(e => !e.isDeleted).length
  const reading = (entries.data ?? []).filter(isBeingRead).length
  const unreadable = (entries.data ?? []).filter(e => !e.isDeleted && e.kind !== 'Text' && e.transcriptState === 'Failed').length
  const blocker =
    waiting > 0 ? `${waiting} note${waiting === 1 ? ' has' : 's have'} not reached the server yet. Wait for the sync to finish.`
    : refused > 0 ? `${refused} note${refused === 1 ? ' was' : 's were'} refused by the server. Remove ${refused === 1 ? 'it' : 'them'} in the notes list first.`
    : reading > 0 ? `${reading} recording${reading === 1 ? '' : 's'} or photo${reading === 1 ? ' is' : 's are'} still being turned into text. This takes a few seconds.`
    : unreadable > 0 ? `${unreadable} recording${unreadable === 1 ? '' : 's'} or photo${unreadable === 1 ? '' : 's'} could not be read. Use "Try again" on ${unreadable === 1 ? 'it' : 'them'} in the notes list, or remove ${unreadable === 1 ? 'it' : 'them'}.`
    : entries.isSuccess && input === 0 ? 'Add some input first.'
    : null

  // when the run being watched finishes, refresh everything that shows the flow and say how it went
  useEffect(() => {
    if (!latest || latest.id !== watching.current || active(latest)) return
    watching.current = null
    refresh(flow.id, flow.projectId)
    if (latest.status === 'Succeeded') toast(`Done: version ${latest.resultVersionNo} created.`, 'good')
    else toast(latest.status === 'Cancelled' ? 'The analysis was cancelled.' : 'The analysis did not finish.', 'error')
  }, [latest?.id, latest?.status])                                                       // eslint-disable-line react-hooks/exhaustive-deps

  async function run(force = false) {
    setStarting(true)
    setNothingNew(null)
    try {
      await flushOutbox()                                                                // everything captured must have reached the server first
      const run = await api<AnalysisRun>(`flows/${flow.id}/analyses`, {
        method: 'POST', idempotent: true, body: { mode: flow.currentVersionNo === 0 ? 'Initial' : 'Reanalyse', entryIds: null, ...(force ? { force: true } : {}) },
      })
      watching.current = run.id
      await runs.refetch()
    } catch (e) {
      // the AI would only be paid (in free tokens) to give the same result: say so, and let the person decide
      if (e instanceof ApiError && e.code === 'nothing_new') setNothingNew(e.userMessage)
      else toast(describeError(e), 'error')
    }
    finally { setStarting(false) }
  }

  async function cancel() {
    if (!latest) return
    try { await api(`analysis-runs/${latest.id}/cancel`, { method: 'POST', body: {} }); await runs.refetch() }
    catch (e) { toast(describeError(e), 'error') }
  }

  const open = questions.data?.filter(q => q.status === 'Open') ?? []
  const first = flow.currentVersionNo === 0

  return (
    <>
      <div className="card" style={{ display: 'block' }}>
        <h3>{first ? 'Analyse the captured input' : 'Analyse again'}</h3>
        <p className="muted">
          {first
            ? 'The AI turns what was captured into a structured flow with steps, rules and requirements. Nothing is invented: unknown details become questions.'
            : 'Analysing again keeps your edits and answers, and uses any new input you captured since.'}
        </p>
        {blocker && <Alert tone="warn">{blocker}</Alert>}
        {!canCapture && <Alert>Only the people who capture for this department can run an analysis.</Alert>}
        {nothingNew && (
          <Alert tone="info">
            {nothingNew}{' '}
            <button className="btn text" style={{ minHeight: 32, padding: 0 }} disabled={running} onClick={() => void run(true)}>Analyse anyway</button>
          </Alert>
        )}
        <div className="row">
          <button className="btn primary lg" disabled={!canCapture || running || !!blocker} onClick={() => void run(false)}>
            <Sparkle size={20} weight="fill" aria-hidden />{running ? 'Analysing…' : first ? 'Run analysis' : 'Analyse again'}
          </button>
          {active(latest) && <button className="btn" onClick={() => void cancel()}>Cancel</button>}
        </div>
        {running && (
          <div role="status" style={{ marginTop: 12 }}>
            <div className="meter"><span style={{ width: '40%', animation: 'slide 1.4s ease-in-out infinite alternate' }} /></div>
            <div className="muted small">{latest?.status === 'Queued' ? 'Waiting in the queue' : 'The AI is working'}. This usually takes under a minute but can take a few.</div>
          </div>
        )}
        {latest && !active(latest) && (
          <div style={{ marginTop: 12 }}>
            {latest.status === 'Succeeded'
              ? <Alert tone="good">Last analysis: version {latest.resultVersionNo} created.</Alert>
              : <Alert tone={latest.status === 'Cancelled' ? 'info' : 'error'}>The last analysis {latest.status === 'Cancelled' ? 'was cancelled' : `did not finish: ${latest.errorMessage ?? 'unknown reason'}`}</Alert>}
            {latest.warnings.map((w, i) => <Alert key={i}>{w}</Alert>)}
          </div>
        )}
      </div>

      <h2>Questions to answer ({open.length} open)</h2>
      {questions.isPending && <Skeletons count={2} />}
      {questions.data?.length === 0 && <p className="muted">{first ? 'Questions appear after the first analysis.' : 'No questions. Everything the AI needed was in the input.'}</p>}
      {questions.data?.map(q => <QuestionCard key={q.id + q.rowVersion} question={q} canAnswer={canCapture} isLead={isLeadIn(flow.projectId)} projectId={flow.projectId} />)}
      {questions.data?.some(q => q.status === 'Answered') && !first && <p className="muted small">Answers are used the next time you run the analysis.</p>}
    </>
  )
}
