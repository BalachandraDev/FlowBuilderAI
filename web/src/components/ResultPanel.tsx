import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowsOut, DownloadSimple, FilePdf, PencilSimple, Sparkle, ArrowClockwise } from '@phosphor-icons/react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, apiBlob, describeError, get } from '../api/client'
import { useContent, useFlowDocuments } from '../api/hooks'
import type { Flow, GuideChoice, Job } from '../api/types'
import { ChooseContent } from './ChooseContent'
import { FinalControls, stageOf } from './FinalMark'
import { saveCopyEnabled, saveCopyOnDevice } from '../lib/deviceCopy'
import { downloadFile, openPdf } from '../lib/files'
import { DeviceCopy } from './DeviceCopy'
import { formatWhen } from '../lib/format'
import { FlowDiagramView } from './FlowDiagramView'
import { Alert, Dialog, useToast } from './ui'

const count = (content: Record<string, unknown> | undefined, key: string) => (Array.isArray(content?.[key]) ? (content![key] as unknown[]).length : 0)

/** What the notes turned into: the workflow diagram, a few numbers, and the implementation document to give to the developers. */
export function ResultPanel({ flow, canCapture }: { flow: Flow; canCapture: boolean }) {
  const toast = useToast()
  const qc = useQueryClient()
  const content = useContent(flow.id)
  const [job, setJob] = useState<Job | null>(null)
  const [full, setFull] = useState(false)
  const [choosing, setChoosing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const active = job !== null && (job.state === 'Queued' || job.state === 'Running')
  const docs = useFlowDocuments(flow.id, active)
  const guide = docs.data?.find(d => d.scope === 'Guide')
  const latest = guide?.versions[0]

  // a document made while this page is open is also saved on the device (the computer, or the phone), unless the person turned that off
  const seenVersion = useRef<number | null | undefined>(undefined)       // undefined: the list has not loaded yet
  useEffect(() => {
    if (!docs.isSuccess || !guide) { if (docs.isSuccess && seenVersion.current === undefined) seenVersion.current = null; return }
    const now = latest?.versionNo ?? 0
    const before = seenVersion.current
    seenVersion.current = now
    if (before === undefined || (before !== null && now <= before) || now === 0 || !saveCopyEnabled()) return      // the page was just opened on a document that already existed
    void apiBlob(`documents/${guide.id}/versions/${now}/download`, { format: 'pdf' })
      .then(({ blob }) => saveCopyOnDevice(blob, `${guide.docNumber}.pdf`))
      .then(r => toast(r.where === 'folder' ? `A copy of the PDF was saved in the folder “${r.folder}”.` : 'A copy of the PDF was saved in the Downloads of this device.', 'good'))
      .catch(() => toast('The document is ready, but a copy could not be saved on this device. Use Download.', 'error'))
  }, [docs.isSuccess, guide?.id, latest?.versionNo])   // eslint-disable-line react-hooks/exhaustive-deps

  const status = useQuery({ queryKey: ['guide-job', job?.jobId], queryFn: () => get<Job>(`jobs/${job!.jobId}`), enabled: active, refetchInterval: 2000 })
  const announced = useRef<string | null>(null)
  useEffect(() => {
    const s = status.data
    if (!s || s.state === 'Queued' || s.state === 'Running' || announced.current === s.jobId) return
    announced.current = s.jobId
    setJob(s)
    void qc.invalidateQueries({ queryKey: ['flow-documents', flow.id] })
    void qc.invalidateQueries({ queryKey: ['library'] })
    if (s.state === 'Succeeded') toast('The implementation document is ready.', 'good')
    else setError(s.message ?? 'The document could not be made.')
  }, [status.data, qc, flow.id, toast])

  async function makeGuide(choice: GuideChoice) {
    setChoosing(false)
    setError(null)
    try {
      const j = await api<Job>(`flows/${flow.id}/guide`, { method: 'POST', body: choice, idempotent: true })
      announced.current = null
      setJob(j)
    } catch (e) { setError(describeError(e)) }
  }

  async function run(action: () => Promise<void>) {
    try { await action() } catch (e) { toast(describeError(e), 'error') }
  }

  const c = content.data?.content as Record<string, unknown> | undefined
  const stale = !!latest && !!guide && (latest.sourceFlowVersionNos[0] ?? 0) < flow.currentVersionNo
  const pdfPath = guide && latest ? `documents/${guide.id}/versions/${latest.versionNo}/download` : ''

  return (
    <div className="result">
      <div className="stats3" aria-label="What was found">
        <div className="stat"><div className="n">{count(c, 'steps')}</div><div className="l">steps</div></div>
        <div className="stat"><div className="n">{count(c, 'businessRules')}</div><div className="l">rules</div></div>
        <div className="stat"><div className="n">{count(c, 'requirements')}</div><div className="l">requirements</div></div>
        <div className="stat"><div className="n">{flow.openQuestionCount}</div><div className="l">open questions</div></div>
      </div>

      <section className="diagram-card" aria-label="Workflow diagram">
        <div className="diagram-bar">
          <h3>Workflow diagram</h3>
          <div className="row">
            <button className="btn" onClick={() => setFull(true)}><ArrowsOut size={18} aria-hidden />Full screen</button>
            <button className="btn" onClick={() => void run(() => downloadFile(`flows/${flow.id}/diagram`, { format: 'png' }, `${flow.menuName}.png`))}><DownloadSimple size={18} aria-hidden />PNG picture</button>
          </div>
        </div>
        <FlowDiagramView flowId={flow.id} versionNo={flow.currentVersionNo} />
        <p className="muted small" style={{ marginTop: 8 }}>Follow the arrows from the top. Each box is numbered and says who does it and on which screen. Diamonds are questions with a yes or no answer.</p>
      </section>

      <section className="guide-card" aria-label="Implementation document">
        <div className="guide-icon" aria-hidden><FilePdf size={30} weight="duotone" /></div>
        <div className="guide-main">
          <h3>Implementation document</h3>
          {guide && latest ? (
            <>
              <p className="muted">{guide.docNumber} · version {latest.versionNo} · made by {latest.generatedByName} on {formatWhen(latest.generatedAt)}</p>
              {stale && <Alert tone="warn">The workflow changed after this document was made. Update it to include the changes.</Alert>}
              <div className="row">
                <button className="btn primary" onClick={() => void run(() => openPdf(pdfPath, { format: 'pdf' }))}><FilePdf size={18} aria-hidden />Open the PDF</button>
                <button className="btn" onClick={() => void run(() => downloadFile(pdfPath, { format: 'pdf' }, `${guide.docNumber}.pdf`))}><DownloadSimple size={18} aria-hidden />Download</button>
                {canCapture && <button className="btn" onClick={() => setChoosing(true)} disabled={active}><ArrowClockwise size={18} aria-hidden />{active ? 'Updating…' : 'Update the document'}</button>}
              </div>
              <DeviceCopy />
              <FinalControls docId={guide.id} versionNo={latest.versionNo} stage={stageOf(latest)} finalizedByName={latest.finalizedByName} finalizedAt={latest.finalizedAt} finalNote={latest.finalNote}
                reviewNote={latest.approvalNote} verifiedByName={latest.approvedByName} canAct={canCapture} />
            </>
          ) : (
            <>
              <p className="muted">One PDF that explains the flow: the diagram, who does what, every step and decision, and where the flow touches the system.</p>
              {canCapture
                ? <button className="btn primary lg" onClick={() => setChoosing(true)} disabled={active}><Sparkle size={20} weight="fill" aria-hidden />{active ? 'Writing the document…' : 'Create the implementation document'}</button>
                : <Alert>The document has not been made yet.</Alert>}
            </>
          )}
          {active && <div role="status" style={{ marginTop: 12 }}><div className="meter"><span style={{ width: '45%', animation: 'slide 1.4s ease-in-out infinite alternate' }} /></div><div className="muted small">The diagram is being drawn and the PDF is being made. This usually takes a few seconds.</div></div>}
          {error && <Alert tone="error">{error}</Alert>}
        </div>
      </section>

      {canCapture && (
        <p className="muted small">Something wrong in the workflow? <Link to={`/flow/${flow.id}?tab=review`}><PencilSimple size={14} aria-hidden /> Review and edit it</Link>, then update the document.</p>
      )}
      {choosing && (
        <ChooseContent title="What should the document hold?" outlinePath={`flows/${flow.id}/guide-outline`} confirmLabel={guide ? 'Update the PDF' : 'Make the PDF'}
          onChoose={c => void makeGuide(c)} onClose={() => setChoosing(false)} />
      )}
      {full && (
        <Dialog title="Workflow diagram" onClose={() => setFull(false)} actions={<button className="btn" onClick={() => setFull(false)}>Close</button>}>
          <div className="diagram-full"><FlowDiagramView flowId={flow.id} versionNo={flow.currentVersionNo} full /></div>
        </Dialog>
      )}
    </div>
  )
}
