import { useEffect, useRef, useState } from 'react'
import { ArrowClockwise, DownloadSimple, FilePdf, Stack } from '@phosphor-icons/react'
import { useQueryClient } from '@tanstack/react-query'
import { api, apiBlob, describeError, get } from '../api/client'
import { useDepartments, useLibrary } from '../api/hooks'
import type { Flow, GuideChoice, Job, LibraryDocument } from '../api/types'
import { ChooseContent } from './ChooseContent'
import { FinalControls } from './FinalMark'
import { saveCopyEnabled, saveCopyOnDevice } from '../lib/deviceCopy'
import { downloadFile, openPdf } from '../lib/files'
import { formatWhen } from '../lib/format'
import { Alert, Pill, useToast } from './ui'

const PROJECT = 'project'
const path = (d: LibraryDocument) => `documents/${d.id}/versions/${d.versionNo}/download`

/**
 * One big PDF of the whole project, and a smaller PDF for each module. They are written from the requests that have been analysed (a request
 * without an analysis is listed in the PDF as not included), use no AI, and add a new version each time they are made again.
 */
export function ProjectPdfs({ projectId, flows, canMakeProject, canMakeModule }: { projectId: string; flows: Flow[]; canMakeProject: boolean; canMakeModule: boolean }) {
  const qc = useQueryClient()
  const toast = useToast()
  const modules = useDepartments(projectId)
  const library = useLibrary()
  const [jobs, setJobs] = useState<Record<string, Job>>({})
  const [error, setError] = useState<string | null>(null)
  /** The PDF the person is choosing the content of. */
  const [choosing, setChoosing] = useState<{ key: string; name: string; url: string; update: boolean } | null>(null)
  const done = useRef(new Set<string>())

  const mine = (library.data ?? []).filter(d => d.projectId === projectId)
  const projectDoc = mine.find(d => d.kind === 'ProjectGuide')
  const moduleDoc = (id: string) => mine.find(d => d.kind === 'ModuleGuide' && d.moduleId === id)
  const analysedIn = (moduleId?: string) => flows.filter(f => f.currentVersionNo > 0 && (!moduleId || f.projectDepartmentId === moduleId)).length
  const totalIn = (moduleId?: string) => flows.filter(f => !moduleId || f.projectDepartmentId === moduleId).length
  const active = (key: string) => jobs[key] && (jobs[key].state === 'Queued' || jobs[key].state === 'Running')
  const anyActive = Object.keys(jobs).some(active)

  // while a PDF is being made, ask how far it is
  useEffect(() => {
    if (!anyActive) return undefined
    const t = setInterval(() => {
      for (const [key, job] of Object.entries(jobs)) {
        if (job.state !== 'Queued' && job.state !== 'Running') continue
        void get<Job>(`jobs/${job.jobId}`).then(s => {
          if (s.state === 'Queued' || s.state === 'Running') return
          setJobs(j => ({ ...j, [key]: s }))
          if (done.current.has(s.jobId)) return
          done.current.add(s.jobId)
          void qc.invalidateQueries({ queryKey: ['library'] })
          if (s.state === 'Succeeded') {
            toast('The PDF is ready.', 'good')
            void saveCopy(s)
          } else setError(s.message ?? 'The PDF could not be made.')
        }).catch(() => undefined)
      }
    }, 2000)
    return () => clearInterval(t)
  }, [anyActive, jobs])   // eslint-disable-line react-hooks/exhaustive-deps

  /** The new PDF is also kept on this device (the computer or the phone), like a request's own document. */
  async function saveCopy(s: Job) {
    if (!saveCopyEnabled() || !s.resultId) return
    try {
      const fresh = await get<LibraryDocument[]>('library')
      const d = fresh.find(x => x.id === s.resultId)
      if (!d) return
      const { blob } = await apiBlob(path(d), { format: 'pdf' })
      await saveCopyOnDevice(blob, `${d.docNumber}.pdf`)
    } catch { /* the PDF is made and on the server; the Download button still works */ }
  }

  async function make(key: string, url: string, choice: GuideChoice) {
    setChoosing(null)
    setError(null)
    try {
      const j = await api<Job>(url, { method: 'POST', body: choice, idempotent: true })
      setJobs(prev => ({ ...prev, [key]: j }))
    } catch (e) { setError(describeError(e)) }
  }

  async function run(action: () => Promise<void>) { try { await action() } catch (e) { toast(describeError(e), 'error') } }

  const row = (key: string, name: string, doc: LibraryDocument | undefined, analysed: number, total: number, allowed: boolean, url: string) => (
    <li key={key} className="pdf-row">
      <div className="pdf-main">
        <b>{name}</b>
        <span className="muted small">
          {analysed} of {total} request{total === 1 ? '' : 's'} analysed
          {doc ? ` · ${doc.docNumber} · version ${doc.versionNo} · ${formatWhen(doc.createdAt)}` : ''}
        </span>
        {doc && <FinalControls compact docId={doc.id} versionNo={doc.versionNo} stage={doc.stage ?? 'Working'} finalizedByName={doc.finalizedByName} finalizedAt={doc.finalizedAt} finalNote={doc.finalNote}
          reviewNote={doc.reviewNote} verifiedByName={doc.verifiedByName} canAct={allowed} />}
      </div>
      <div className="pdf-actions">
        {doc && <button className="btn" onClick={() => void run(() => openPdf(path(doc), { format: 'pdf' }))}><FilePdf size={18} aria-hidden />Open</button>}
        {doc && <button className="btn" aria-label={`Download the PDF of ${name}`} onClick={() => void run(() => downloadFile(path(doc), { format: 'pdf' }, `${doc.docNumber}.pdf`))}><DownloadSimple size={18} aria-hidden />Download</button>}
        {allowed && (
          <button className={`btn ${doc ? '' : 'primary'}`} disabled={analysed === 0 || !!active(key)} onClick={() => setChoosing({ key, name, url, update: !!doc })}
            title={analysed === 0 ? 'Analyse at least one request first' : undefined} aria-label={`${doc ? 'Update' : 'Make'} the PDF of ${name}`}>
            {active(key) ? <>Making…</> : doc ? <><ArrowClockwise size={18} aria-hidden />Update</> : <><Stack size={18} aria-hidden />Make PDF</>}
          </button>
        )}
      </div>
    </li>
  )

  const list = modules.data ?? []
  if (flows.length === 0) return null
  return (
    <section className="panel" aria-labelledby="h-pdfs">
      <h2 id="h-pdfs">PDFs of the whole project and of each module</h2>
      <p className="muted small" style={{ marginTop: 4 }}>One big PDF with every request, and a smaller PDF for each module. They are written from the requests that are analysed, use no AI, and a new version is added each time you update them.</p>
      {error && <Alert tone="error">{error}</Alert>}
      <ul className="pdf-list" aria-label="PDFs">
        {row(PROJECT, 'Whole project', projectDoc, analysedIn(), totalIn(), canMakeProject, `projects/${projectId}/guide`)}
        {list.filter(m => totalIn(m.id) > 0).map(m => row(m.id, m.name, moduleDoc(m.id), analysedIn(m.id), totalIn(m.id), canMakeModule, `projects/${projectId}/modules/${m.id}/guide`))}
      </ul>
      {anyActive && <div role="status" className="muted small">The PDF is being made. A large project can take a minute.</div>}
      {choosing && (
        <ChooseContent title={`What should the PDF of ${choosing.name} hold?`} outlinePath={`${choosing.url}-outline`} confirmLabel={choosing.update ? 'Update the PDF' : 'Make the PDF'}
          onChoose={c => void make(choosing.key, choosing.url, c)} onClose={() => setChoosing(null)} />
      )}
      {mine.length > 0 && !canMakeProject && !canMakeModule && <Pill>Read only</Pill>}
    </section>
  )
}
