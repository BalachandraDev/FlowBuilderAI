import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { ArrowClockwise, Copy, DownloadSimple, FilePdf, FileDoc, MagnifyingGlass, TreeStructure } from '@phosphor-icons/react'
import { describeError } from '../api/client'
import { useLibrary } from '../api/hooks'
import type { LibraryDocument } from '../api/types'
import { ErrorState } from '../components/ErrorState'
import { Pager, pageOf } from '../components/Pager'
import { Alert, Dialog, Pill, Skeletons, useToast } from '../components/ui'
import { usePref } from '../ux/prefs'
import { downloadFile, openPdf, useApiImage } from '../lib/files'
import { formatWhen } from '../lib/format'
import { FinalControls } from '../components/FinalMark'
import { useSession } from '../state/session'
import { FinalDocuments } from './admin/FinalDocuments'

function DiagramPreview({ doc, onClose }: { doc: LibraryDocument; onClose: () => void }) {
  const { url, failed } = useApiImage(`library/${doc.id}/diagram`, { format: 'png' })
  return (
    <Dialog title={doc.title} onClose={onClose} actions={<button className="btn" onClick={onClose}>Close</button>}>
      {failed && <Alert tone="warn">The diagram could not be loaded.</Alert>}
      {!url && !failed && <Skeletons count={1} />}
      {url && <div className="diagram-full"><img src={url} alt={`Workflow diagram of ${doc.title}`} style={{ maxWidth: '100%' }} /></div>}
    </Dialog>
  )
}

/** The administrator sees only the documents users marked as final; a user sees the documents of their clients. */
export function Documents() {
  const { isAdmin } = useSession()
  return isAdmin ? <FinalDocuments /> : <UserDocuments />
}

type StageFilter = 'all' | 'Working' | 'Final' | 'Verified' | 'Returned'

/** The documents of the clients a user is assigned to: open them, and mark the one that is to go to the team as final. */
function UserDocuments() {
  const library = useLibrary()
  const toast = useToast()
  const { roleIn } = useSession()
  const [params] = useSearchParams()
  const [search, setSearch] = useState(params.get('q') ?? '')
  const [stage, setStage, resetStage] = usePref<StageFilter>('documents.stage', 'all')
  const [size, setSize] = usePref<number>('documents.size', 25)
  const [page, setPage] = useState(0)
  const [preview, setPreview] = useState<LibraryDocument | null>(null)
  const all = library.data ?? []
  const stageOf = (d: LibraryDocument) => d.stage ?? 'Working'
  const shown = useMemo(() => {
    const words = search.trim().toLowerCase().split(/\s+/).filter(Boolean)
    return all.filter(d => (stage === 'all' || stageOf(d) === stage) && words.every(w => [d.title, d.projectName, d.docNumber, d.createdByName, d.clientName ?? ''].join(' ').toLowerCase().includes(w)))
  }, [all, search, stage])
  const filtered = search.trim() !== '' || stage !== 'all'
  const count = (s: StageFilter) => (s === 'all' ? all.length : all.filter(d => stageOf(d) === s).length)

  async function run(action: () => Promise<void>) { try { await action() } catch (e) { toast(describeError(e), 'error') } }
  const path = (d: LibraryDocument) => `documents/${d.id}/versions/${d.versionNo}/download`
  const copy = (text: string, what: string) => void navigator.clipboard?.writeText(text).then(() => toast(`${what} copied.`, 'good'), () => toast('Could not copy. Select the text and copy it.', 'error'))

  return (
    <>
      <div className="page-title">
        <div><h1>Documents</h1><p className="muted">The documents of your clients. Open the PDF, look at the diagram, or download it. When one is ready to give to the team, mark it as final: the administrator then verifies it.</p></div>
        <div className="row">
          <label className="field-inline"><span>Show</span>
            <select className="input" value={stage} onChange={e => { setStage(e.target.value as StageFilter); setPage(0) }}>
              <option value="all">All</option><option value="Working">Working copies</option><option value="Final">Final, waiting</option><option value="Verified">Verified</option><option value="Returned">Returned</option></select></label>
          <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search documents</span>
            <input className="input" type="search" placeholder="Search by name, project or person" value={search} onChange={e => { setSearch(e.target.value); setPage(0) }} /></label>
          <button className="btn" onClick={() => void library.refetch()} disabled={library.isFetching} aria-label="Refresh the list"><ArrowClockwise size={16} aria-hidden />{library.isFetching ? 'Refreshing…' : 'Refresh'}</button>
        </div>
      </div>

      {all.length > 0 && (
        <div className="chips quick" role="group" aria-label="Quick filters">
          {([['all', 'All'], ['Working', 'Working copies'], ['Final', 'Waiting for the administrator'], ['Returned', 'Returned to me'], ['Verified', 'Verified']] as [StageFilter, string][]).map(([s, label]) => (
            <button key={s} className="chip" aria-pressed={stage === s} onClick={() => { setStage(s); setPage(0) }}>{label}<span className="n">{count(s)}</span></button>
          ))}
          {filtered && <button className="btn link" onClick={() => { resetStage(); setSearch(''); setPage(0) }}>Clear filters</button>}
        </div>
      )}

      {library.isPending && <Skeletons count={3} />}
      {library.isError && <ErrorState error={library.error} onRetry={() => void library.refetch()} retrying={library.isFetching} />}
      {library.data && library.data.length === 0 && <div className="empty" role="status"><b>No documents yet</b><div>When you or a colleague on your client creates an implementation document, it appears here.</div></div>}
      {library.data && library.data.length > 0 && shown.length === 0 && <div className="empty" role="status"><b>{search.trim() ? `Nothing matches “${search}”` : 'No document is in this state'}</b><button className="btn link" onClick={() => { resetStage(); setSearch('') }}>Show everything</button></div>}

      <ul className="doc-list">
        {pageOf(shown, page, size).map(d => (
          <li key={d.id} className="doc">
            <div className="doc-icon" aria-hidden><FilePdf size={30} weight="duotone" /></div>
            <div className="doc-main">
              <div className="doc-title"><h3>{d.title}</h3><Pill tone="progress">{d.kind === 'Guide' ? 'Implementation document' : d.kind === 'ProjectGuide' ? 'Whole project PDF' : d.kind === 'ModuleGuide' ? 'Module PDF' : 'Requirements'}</Pill>{d.versionNo > 1 && <Pill>Version {d.versionNo}</Pill>}</div>
              <div className="muted small">{d.clientName ? d.clientName + ' · ' : ''}{d.projectName} · {d.docNumber} · by {d.createdByName} · {formatWhen(d.createdAt)}</div>
              <FinalControls compact docId={d.id} versionNo={d.versionNo} stage={d.stage ?? 'Working'} finalizedByName={d.finalizedByName} finalizedAt={d.finalizedAt} finalNote={d.finalNote}
                reviewNote={d.reviewNote} verifiedByName={d.verifiedByName} canAct={roleIn(d.projectId) !== 'Reviewer'} />
              <div className="row doc-actions">
                <button className="btn primary" onClick={() => void run(() => openPdf(path(d), { format: 'pdf' }))}><FilePdf size={18} aria-hidden />Open PDF</button>
                {d.hasDiagram && <button className="btn" onClick={() => setPreview(d)}><TreeStructure size={18} aria-hidden />Diagram</button>}
                <button className="btn" onClick={() => void run(() => downloadFile(path(d), { format: 'pdf' }, `${d.docNumber}.pdf`))}><DownloadSimple size={18} aria-hidden />PDF</button>
                {d.formats.includes('docx') && <button className="btn" onClick={() => void run(() => downloadFile(path(d), { format: 'docx' }, `${d.docNumber}.docx`))}><FileDoc size={18} aria-hidden />Word</button>}
                <button className="btn" aria-label={`Copy the number ${d.docNumber}`} onClick={() => copy(d.docNumber, 'The document number')}><Copy size={18} aria-hidden />Copy number</button>
              </div>
            </div>
          </li>
        ))}
      </ul>
      <Pager total={shown.length} page={page} size={size} onPage={setPage} onSize={setSize} />
      {preview && <DiagramPreview doc={preview} onClose={() => setPreview(null)} />}
    </>
  )
}
