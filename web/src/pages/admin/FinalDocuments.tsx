import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ArrowClockwise, ArrowUUpLeft, CheckCircle, Copy, DownloadSimple, FilePdf, Info, MagnifyingGlass, TreeStructure } from '@phosphor-icons/react'
import { api, describeError, get } from '../../api/client'
import { useLibrary } from '../../api/hooks'
import type { DocumentStage, GeneratedDocument, LibraryDocument } from '../../api/types'
import { ActionMenu } from '../../components/ActionMenu'
import { Drawer } from '../../components/Drawer'
import { ErrorState } from '../../components/ErrorState'
import { StagePill, stageOf, useRefreshDocuments } from '../../components/FinalMark'
import { Pager, pageOf } from '../../components/Pager'
import { Alert, Dialog, Field, Pill, Skeletons, useToast } from '../../components/ui'
import { downloadFile, openPdf, useApiImage } from '../../lib/files'
import { formatWhen } from '../../lib/format'
import { downloadCsv, stamp } from '../../ux/csv'
import { usePref } from '../../ux/prefs'

const KIND: Record<string, string> = { Guide: 'Implementation document', ProjectGuide: 'Whole project PDF', ModuleGuide: 'Module PDF', Requirements: 'Requirements' }
const ORDER: Record<DocumentStage, number> = { Final: 0, Returned: 1, Verified: 2, Working: 3 }
type StageFilter = 'all' | 'Final' | 'Verified' | 'Returned'
const STAGES: StageFilter[] = ['all', 'Final', 'Verified', 'Returned']

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

/** The administrator decides on a final document: verifies it, or returns it to its maker with the reason. */
function Decide({ doc, approve, onClose }: { doc: LibraryDocument; approve: boolean; onClose: () => void }) {
  const toast = useToast()
  const refresh = useRefreshDocuments()
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const needsReason = !approve && note.trim().length === 0

  async function save() {
    if (busy) return
    setBusy(true); setError(null)
    try {
      await api(`documents/${doc.id}/versions/${doc.versionNo}/approve`, { method: 'POST', body: { approved: approve, note: note.trim() || null } })
      toast(approve ? `${doc.docNumber} was verified.` : `${doc.docNumber} was returned to ${doc.finalizedByName ?? 'its maker'}.`, 'good')
      void refresh()
      onClose()
    } catch (e) { setError(describeError(e)); setBusy(false) }
  }

  return (
    <Dialog title={approve ? 'Verify this document?' : 'Return this document?'} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className={`btn ${approve ? 'primary' : ''}`} onClick={() => void save()} disabled={busy || needsReason}>{busy ? 'Saving…' : approve ? 'Verify' : 'Return it'}</button>
    </>}>
      <p><b>{doc.title}</b> · {doc.docNumber} · version {doc.versionNo}<br /><span className="muted small">{doc.clientName ?? doc.projectName} · marked final by {doc.finalizedByName ?? 'its maker'}</span></p>
      <p className="muted small">{approve ? 'It is then verified and can be given to the team. Its maker is told.' : 'Its maker is told why. They update the document and mark the new version as final again.'}</p>
      <Field label={approve ? 'Note (optional)' : 'Why is it returned?'} required={!approve} help={approve ? undefined : 'Say what is missing or wrong, so it can be fixed.'}>
        {(id, aria) => <textarea id={id} className="input" rows={3} maxLength={1000} value={note} onChange={e => setNote(e.target.value)} {...aria} />}
      </Field>
      {error && <Alert tone="error">{error}</Alert>}
    </Dialog>
  )
}

/** Verifies several final documents in one go, one after another, saying how far it is and which ones could not be done. */
function BulkVerify({ docs, onClose, onDone }: { docs: LibraryDocument[]; onClose: () => void; onDone: (failed: string[]) => void }) {
  const toast = useToast()
  const refresh = useRefreshDocuments()
  const [done, setDone] = useState(0)
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState<{ doc: LibraryDocument; why: string }[]>([])

  async function run() {
    if (busy) return
    setBusy(true); setFailed([]); setDone(0)
    const bad: { doc: LibraryDocument; why: string }[] = []
    for (const [i, d] of docs.entries()) {
      try { await api(`documents/${d.id}/versions/${d.versionNo}/approve`, { method: 'POST', body: { approved: true, note: null } }) }
      catch (e) { bad.push({ doc: d, why: describeError(e) }) }
      setDone(i + 1)
    }
    setFailed(bad); setBusy(false)
    void refresh()
    const ok = docs.length - bad.length
    toast(bad.length === 0 ? `${ok} document${ok === 1 ? ' was' : 's were'} verified.` : `${ok} verified, ${bad.length} could not be.`, bad.length === 0 ? 'good' : 'error')
    if (bad.length === 0) onDone([]); else onDone(bad.map(b => b.doc.id))
  }

  return (
    <Dialog title={`Verify ${docs.length} document${docs.length === 1 ? '' : 's'}?`} onClose={busy ? () => undefined : onClose} actions={<>
      <button className="btn" onClick={onClose} disabled={busy}>{failed.length > 0 ? 'Close' : 'Cancel'}</button>
      <button className="btn primary" onClick={() => void run()} disabled={busy || (done === docs.length && failed.length === 0 && done > 0)}>{busy ? `Verifying ${done} of ${docs.length}…` : failed.length > 0 ? 'Try again' : `Verify ${docs.length}`}</button>
    </>}>
      <p className="muted small">Each of these is marked verified and its maker is told. This cannot be undone. To return a document, open it and use Return, which needs a reason.</p>
      <ul className="bulk-list" aria-label="Documents to verify">
        {docs.map(d => <li key={d.id}><b>{d.docNumber}</b> <span className="muted">{d.title}</span><span className="muted small"> · {d.finalizedByName ?? 'unknown'}</span></li>)}
      </ul>
      {busy && <div className="meter" role="progressbar" aria-valuemin={0} aria-valuemax={docs.length} aria-valuenow={done} aria-label="Progress"><span style={{ width: `${(done / docs.length) * 100}%` }} /></div>}
      {failed.length > 0 && <Alert tone="error"><div><b>{failed.length} could not be verified:</b><ul>{failed.map(f => <li key={f.doc.id}>{f.doc.docNumber}: {f.why}</li>)}</ul></div></Alert>}
    </Dialog>
  )
}

/** The details of one final document: who made it final and when, the notes, and its versions, with what can be done to it. */
function Details({ doc, onClose, onDecide, onOpen }: { doc: LibraryDocument; onClose: () => void; onDecide: (d: LibraryDocument, approve: boolean) => void; onOpen: (d: LibraryDocument) => void }) {
  const detail = useQuery({ queryKey: ['document', doc.id], queryFn: () => get<GeneratedDocument>(`documents/${doc.id}`) })
  const toast = useToast()
  const copy = (text: string, what: string) => void navigator.clipboard?.writeText(text).then(() => toast(`${what} copied.`, 'good'), () => toast('Could not copy. Select the text and copy it.', 'error'))
  return (
    <Drawer title={doc.title} subtitle={`${doc.docNumber} · version ${doc.versionNo}`} onClose={onClose}
      footer={<>
        <button className="btn" onClick={() => onOpen(doc)}><FilePdf size={16} aria-hidden />Open PDF</button>
        {doc.stage === 'Final' && <button className="btn" onClick={() => onDecide(doc, false)}><ArrowUUpLeft size={16} aria-hidden />Return</button>}
        {doc.stage === 'Final' && <button className="btn primary" onClick={() => onDecide(doc, true)}><CheckCircle size={16} aria-hidden />Verify</button>}
      </>}>
      <div className="row" style={{ marginBottom: 12 }}><StagePill stage={doc.stage ?? 'Final'} /><Pill tone="progress">{KIND[doc.kind] ?? doc.kind}</Pill></div>
      <dl>
        <dt>Client</dt><dd>{doc.clientName ?? 'No client'}</dd>
        <dt>Project</dt><dd>{doc.projectName}</dd>
        <dt>Document number</dt><dd>{doc.docNumber} <button className="btn link" onClick={() => copy(doc.docNumber, 'The document number')}><Copy size={14} aria-hidden />Copy</button></dd>
        <dt>Marked final by</dt><dd>{doc.finalizedByName ?? 'unknown'}{doc.finalizedAt ? ` · ${formatWhen(doc.finalizedAt)}` : ''}</dd>
        {doc.finalNote && <><dt>Their note</dt><dd>{doc.finalNote}</dd></>}
        {doc.stage === 'Verified' && <><dt>Verified by</dt><dd>{doc.verifiedByName ?? 'the administrator'}{doc.verifiedAt ? ` · ${formatWhen(doc.verifiedAt)}` : ''}</dd></>}
        {doc.stage === 'Returned' && <><dt>Returned because</dt><dd>{doc.reviewNote || 'No reason given'}</dd></>}
      </dl>
      <h3>History of this document</h3>
      {detail.isPending && <Skeletons count={2} />}
      {detail.isError && <ErrorState error={detail.error} onRetry={() => void detail.refetch()} retrying={detail.isFetching} />}
      {detail.data && (
        <ol className="timeline" aria-label="Versions marked final">
          {detail.data.versions.map(v => {
            const stage = stageOf(v)
            return (
              <li key={v.versionNo} className={stage === 'Verified' ? 'good' : stage === 'Returned' ? 'warn' : 'progress'}>
                <div className="row"><b>Version {v.versionNo}</b><StagePill stage={stage} /></div>
                <div className="muted small">Made by {v.generatedByName} · {formatWhen(v.generatedAt)}</div>
                {v.finalizedAt && <div className="small">Marked final by {v.finalizedByName ?? 'its maker'} · {formatWhen(v.finalizedAt)}{v.finalNote ? ` — “${v.finalNote}”` : ''}</div>}
                {stage === 'Verified' && <div className="small">Verified by {v.approvedByName ?? 'the administrator'}{v.approvalNote ? ` — “${v.approvalNote}”` : ''}</div>}
                {stage === 'Returned' && <div className="small">Returned by {v.approvedByName ?? 'the administrator'}: {v.approvalNote || 'no reason given'}</div>}
              </li>
            )
          })}
        </ol>
      )}
    </Drawer>
  )
}

/**
 * What the administrator sees of the documents: only the ones users marked as final (never working copies). Filter by client, by person and by where it
 * stands (the choice is remembered), search, open the PDF or its details, then verify it or return it with a reason. Several waiting documents can be
 * ticked and verified together, and the list can be taken away as a CSV file.
 */
export function FinalDocuments() {
  const library = useLibrary()
  const toast = useToast()
  const [params] = useSearchParams()
  const fromLink = params.get('status') as StageFilter | null
  const [savedStage, setSavedStage, resetStage] = usePref<StageFilter>('final.stage', 'all')
  const stage: StageFilter = fromLink && STAGES.includes(fromLink) ? fromLink : savedStage
  const [savedClient, setClient, resetClient] = usePref<string>('final.client', '')
  const [savedPerson, setPerson, resetPerson] = usePref<string>('final.person', '')
  const [search, setSearch] = useState(params.get('q') ?? '')
  const [size, setSize] = usePref<number>('final.size', 25)
  const [page, setPage] = useState(0)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [preview, setPreview] = useState<LibraryDocument | null>(null)
  const [deciding, setDeciding] = useState<{ doc: LibraryDocument; approve: boolean } | null>(null)
  const [details, setDetails] = useState<LibraryDocument | null>(null)
  const [bulk, setBulk] = useState(false)

  const all = library.data ?? []
  const count = (s: DocumentStage) => all.filter(d => d.stage === s).length
  const clients = useMemo(() => [...new Set(all.map(d => d.clientName ?? '').filter(Boolean))].sort(), [all])
  const people = useMemo(() => [...new Set(all.map(d => d.finalizedByName ?? '').filter(Boolean))].sort(), [all])
  // a remembered client or person who has no final documents any more is not applied
  const client = clients.includes(savedClient) ? savedClient : ''
  const person = people.includes(savedPerson) ? savedPerson : ''
  const shown = useMemo(() => {
    const words = search.trim().toLowerCase().split(/\s+/).filter(Boolean)
    return all
      .filter(d => (stage === 'all' || d.stage === stage) && (!client || d.clientName === client) && (!person || d.finalizedByName === person)
        && words.every(w => [d.title, d.projectName, d.docNumber, d.clientName ?? '', d.finalizedByName ?? ''].join(' ').toLowerCase().includes(w)))
      .sort((a, b) => ORDER[a.stage ?? 'Final'] - ORDER[b.stage ?? 'Final'] || (b.finalizedAt ?? '').localeCompare(a.finalizedAt ?? ''))
  }, [all, stage, client, person, search])
  const filtered = stage !== 'all' || !!client || !!person || !!search.trim()
  const pageRows = pageOf(shown, page, size)
  const waitingShown = shown.filter(d => d.stage === 'Final')
  const picked = shown.filter(d => selected.has(d.id) && d.stage === 'Final')
  // what is no longer on the list cannot stay ticked
  useEffect(() => { setSelected(s => { const keep = new Set([...s].filter(id => waitingShown.some(d => d.id === id))); return keep.size === s.size ? s : keep }) }, [shown])   // eslint-disable-line react-hooks/exhaustive-deps

  const copy = (text: string, what: string) => void navigator.clipboard?.writeText(text).then(() => toast(`${what} copied.`, 'good'), () => toast('Could not copy. Select the text and copy it.', 'error'))
  async function run(action: () => Promise<void>) { try { await action() } catch (e) { toast(describeError(e), 'error') } }
  const path = (d: LibraryDocument) => `documents/${d.id}/versions/${d.versionNo}/download`
  const openIt = (d: LibraryDocument) => void run(() => openPdf(path(d), { format: 'pdf' }))
  const clearFilters = () => { resetStage(); resetClient(); resetPerson(); setSearch(''); setPage(0) }
  const exportCsv = () => downloadCsv(`final-documents-${stamp()}`, [
    ['Document number', 'Title', 'Type', 'Client', 'Project', 'Version', 'Status', 'Marked final by', 'Marked final at', 'Their note', 'Verified by', 'Verified at', 'Returned because'],
    ...shown.map(d => [d.docNumber, d.title, KIND[d.kind] ?? d.kind, d.clientName ?? '', d.projectName, d.versionNo, d.stage === 'Final' ? 'Waiting' : d.stage, d.finalizedByName ?? '', d.finalizedAt ?? '', d.finalNote ?? '',
      d.verifiedByName ?? '', d.verifiedAt ?? '', d.stage === 'Returned' ? d.reviewNote ?? '' : '']),
  ])
  const toggle = (id: string) => setSelected(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  const tile = (s: StageFilter, n: number, label: string, tone: string) => (
    <button className={`final-tile ${tone} ${stage === s ? 'on' : ''}`} aria-pressed={stage === s} onClick={() => { setSavedStage(stage === s ? 'all' : s); setPage(0) }}><span className="n">{n}</span><span className="l">{label}</span></button>
  )

  return (
    <>
      <div className="page-title">
        <div><h1>Final documents</h1><p className="muted">The documents your users marked as final, to give to the team. Working copies are not shown: they are the users' own. Open one, then verify it or return it with a reason.</p></div>
        <div className="row">
          <button className="btn" onClick={() => void library.refetch()} disabled={library.isFetching} aria-label="Refresh the list"><ArrowClockwise size={16} aria-hidden />{library.isFetching ? 'Refreshing…' : 'Refresh'}</button>
          {library.dataUpdatedAt > 0 && <span className="updated" role="status">Updated {new Date(library.dataUpdatedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>}
        </div>
      </div>

      <div className="final-tiles" aria-label="Where the documents stand">
        {tile('Final', count('Final'), 'waiting for you', 'wait')}
        {tile('Verified', count('Verified'), 'verified', 'ok')}
        {tile('Returned', count('Returned'), 'returned', 'back')}
      </div>

      <div className="filter-bar" role="search" aria-label="Filter the final documents">
        <label className="field-inline"><span>Client</span>
          <select className="input" value={client} onChange={e => { setClient(e.target.value); setPage(0) }}><option value="">All clients</option>{clients.map(c => <option key={c} value={c}>{c}</option>)}</select></label>
        <label className="field-inline"><span>Made final by</span>
          <select className="input" value={person} onChange={e => { setPerson(e.target.value); setPage(0) }}><option value="">Everybody</option>{people.map(p => <option key={p} value={p}>{p}</option>)}</select></label>
        <label className="field-inline"><span>Status</span>
          <select className="input" value={stage} onChange={e => { setSavedStage(e.target.value as StageFilter); setPage(0) }}>
            <option value="all">All</option><option value="Final">Waiting for you</option><option value="Verified">Verified</option><option value="Returned">Returned</option></select></label>
        <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search the final documents</span>
          <input className="input" type="search" placeholder="Search by name, client or person" value={search} onChange={e => { setSearch(e.target.value); setPage(0) }} /></label>
        {filtered && <button className="btn link" onClick={clearFilters}>Clear filters</button>}
        <button className="btn" onClick={exportCsv} disabled={shown.length === 0}><DownloadSimple size={16} aria-hidden />Export CSV</button>
      </div>

      {picked.length > 0 && (
        <div className="bulkbar" role="region" aria-label="Selected documents">
          <b>{picked.length}</b><span>selected</span>
          <button className="btn primary" onClick={() => setBulk(true)}><CheckCircle size={16} aria-hidden />Verify selected</button>
          <button className="btn link" onClick={() => setSelected(new Set())}>Clear selection</button>
        </div>
      )}
      {waitingShown.length > 1 && (
        <label className="check selectall"><input type="checkbox" checked={picked.length === waitingShown.length} ref={el => { if (el) el.indeterminate = picked.length > 0 && picked.length < waitingShown.length }}
          onChange={e => setSelected(e.target.checked ? new Set(waitingShown.map(d => d.id)) : new Set())} /><span>Select all {waitingShown.length} waiting</span></label>
      )}

      {library.isPending && <Skeletons count={3} />}
      {library.isError && <ErrorState error={library.error} onRetry={() => void library.refetch()} retrying={library.isFetching} />}
      {library.data && all.length === 0 && <div className="empty" role="status"><b>No final documents yet</b><div>When a user marks a document as final, it appears here for you to verify.</div></div>}
      {all.length > 0 && shown.length === 0 && <div className="empty" role="status"><b>Nothing matches these filters</b><button className="btn link" onClick={clearFilters}>Clear filters</button></div>}

      <ul className="doc-list" aria-label="Final documents">
        {pageRows.map(d => (
          <li key={d.id} className="doc">
            {d.stage === 'Final'
              ? <div className="rowcheck"><input type="checkbox" checked={selected.has(d.id)} onChange={() => toggle(d.id)} aria-label={`Select ${d.docNumber}`} /></div>
              : <div className="doc-icon" aria-hidden><FilePdf size={30} weight="duotone" /></div>}
            <div className="doc-main">
              <div className="doc-title"><h3>{d.title}</h3><Pill tone="progress">{KIND[d.kind] ?? d.kind}</Pill>{d.versionNo > 1 && <Pill>Version {d.versionNo}</Pill>}<StagePill stage={d.stage ?? 'Final'} /></div>
              <div className="muted small">{d.clientName ?? 'No client'} · {d.projectName} · {d.docNumber}</div>
              <div className="muted small">Marked final by <b>{d.finalizedByName ?? 'unknown'}</b>{d.finalizedAt ? ` · ${formatWhen(d.finalizedAt)}` : ''}</div>
              {d.finalNote && <div className="small final-note">“{d.finalNote}”</div>}
              {d.stage === 'Verified' && <div className="muted small">Verified by {d.verifiedByName ?? 'the administrator'}{d.verifiedAt ? ` · ${formatWhen(d.verifiedAt)}` : ''}</div>}
              {d.stage === 'Returned' && <div className="small final-note back">Returned: {d.reviewNote || 'no reason given'}</div>}
              <div className="row doc-actions">
                <button className="btn primary" onClick={() => openIt(d)}><FilePdf size={18} aria-hidden />Open PDF</button>
                <button className="btn" aria-label={`Details of ${d.docNumber}`} onClick={() => setDetails(d)}><Info size={18} aria-hidden />Details</button>
                {d.stage === 'Final' && <button className="btn primary" aria-label={`Verify ${d.docNumber}`} onClick={() => setDeciding({ doc: d, approve: true })}><CheckCircle size={18} aria-hidden />Verify</button>}
                {d.stage === 'Final' && <button className="btn" aria-label={`Return ${d.docNumber}`} onClick={() => setDeciding({ doc: d, approve: false })}><ArrowUUpLeft size={18} aria-hidden />Return</button>}
                <ActionMenu label={`More actions for ${d.docNumber}`} items={[
                  ...(d.hasDiagram ? [{ label: 'View the diagram', icon: <TreeStructure size={18} aria-hidden />, onSelect: () => setPreview(d) }] : []),
                  { label: 'Download the PDF', icon: <DownloadSimple size={18} aria-hidden />, onSelect: () => void run(() => downloadFile(path(d), { format: 'pdf' }, `${d.docNumber}.pdf`)) },
                  { label: 'Copy the document number', icon: <Copy size={18} aria-hidden />, onSelect: () => copy(d.docNumber, 'The document number') },
                  { label: 'Copy number and title', icon: <Copy size={18} aria-hidden />, onSelect: () => copy(`${d.docNumber} ${d.title}`, 'The number and title') },
                ]} />
              </div>
            </div>
          </li>
        ))}
      </ul>
      <Pager total={shown.length} page={page} size={size} onPage={setPage} onSize={setSize} />

      {preview && <DiagramPreview doc={preview} onClose={() => setPreview(null)} />}
      {deciding && <Decide doc={deciding.doc} approve={deciding.approve} onClose={() => setDeciding(null)} />}
      {details && <Details doc={details} onClose={() => setDetails(null)} onOpen={openIt} onDecide={(d, approve) => { setDetails(null); setDeciding({ doc: d, approve }) }} />}
      {bulk && <BulkVerify docs={picked} onClose={() => setBulk(false)} onDone={failedIds => { if (failedIds.length === 0) { setBulk(false); setSelected(new Set()) } else setSelected(new Set(failedIds)) }} />}
    </>
  )
}
