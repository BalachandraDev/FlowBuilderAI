import { useEffect, useMemo, useRef, useState } from 'react'
import { SealCheck } from '@phosphor-icons/react'
import { ApiError, api, describeError } from '../../api/client'
import { useCategories, useContent, useRefreshFlow } from '../../api/hooks'
import type { Flow, FlowContentDto } from '../../api/types'
import { EditProvider, ItemList, ObjectEditor, type EditContext } from '../../components/ContentEditor'
import { MermaidView } from '../../components/MermaidView'
import { Alert, ConfirmDialog, Skeletons, useToast } from '../../components/ui'
import { IdIssuer, OVERVIEW, SECTIONS, normalize, type Item } from '../../lib/flowContent'
import { statusText } from '../../lib/format'
import { useSession } from '../../state/session'

const isDark = () => document.documentElement.dataset.theme === 'dark' || (document.documentElement.dataset.theme === 'system' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches)

/** Review and edit the flow: the diagram, every section as a form, a padlock on each item, then submit, confirm or re-open. */
export function ReviewTab({ flow }: { flow: Flow }) {
  const { canCaptureIn, isLeadIn } = useSession()
  const toast = useToast()
  const refresh = useRefreshFlow()
  const content = useContent(flow.id)
  const categories = useCategories()
  const [draft, setDraft] = useState<Item | null>(null)
  const [locked, setLocked] = useState<Set<string>>(new Set())
  const [baseline, setBaseline] = useState('')
  const [loaded, setLoaded] = useState<FlowContentDto | null>(null)
  const [note, setNote] = useState('')
  const [errors, setErrors] = useState<string[]>([])
  const [conflict, setConflict] = useState(false)
  const [busy, setBusy] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const ids = useRef(new IdIssuer())

  const snapshot = (d: Item | null, l: Set<string>) => JSON.stringify(d) + '|' + [...l].sort().join(',')
  const load = (dto: FlowContentDto) => {
    const d = structuredClone(dto.content) as Item
    const l = new Set(dto.lockedIds)
    ids.current.reset(d)
    setLoaded(dto); setDraft(d); setLocked(l); setBaseline(snapshot(d, l)); setErrors([]); setConflict(false)
  }
  const dirty = draft !== null && snapshot(draft, locked) !== baseline
  const dirtyRef = useRef(false)
  dirtyRef.current = dirty
  // a newer version arrived (an analysis finished, someone else saved): show it, unless there are unsaved edits that must not be lost
  useEffect(() => { if (content.data && (!loaded || (!dirtyRef.current && content.data.versionNo !== loaded.versionNo))) load(content.data) }, [content.data])

  const canEdit = canCaptureIn(flow.projectId)
  const ctx = useMemo<EditContext>(() => ({
    readOnly: !canEdit,
    locked,
    toggleLock: id => setLocked(prev => { const n = new Set(prev); if (!n.delete(id)) n.add(id); return n }),
    categories: categories.data?.filter(c => c.isActive).map(c => c.code) ?? [],
    ids: ids.current,
  }), [canEdit, locked, categories.data])

  async function save() {
    if (!draft || !loaded) return
    setBusy(true); setErrors([]); setConflict(false)
    try {
      const dto = await api<FlowContentDto>(`flows/${flow.id}/content`, {
        method: 'PUT', body: { baseVersionNo: loaded.versionNo, content: normalize(draft), lockedIds: [...locked], changeNote: note.trim() || null },
      })
      load(dto)
      setNote('')
      toast(`Saved as version ${dto.versionNo}.`, 'good')
      refresh(flow.id, flow.projectId)
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) { setConflict(true); setErrors([e.userMessage]) }
      else if (e instanceof ApiError && e.errors) setErrors([...new Set(Object.values(e.errors).flat())])
      else setErrors([describeError(e)])
    } finally { setBusy(false) }
  }

  async function move(path: string, body: unknown, done: string) {
    setBusy(true)
    try {
      await api(`flows/${flow.id}/${path}`, { method: 'POST', body })
      toast(done, 'good')
      refresh(flow.id, flow.projectId)
      await content.refetch()
    } catch (e) { toast(describeError(e), 'error') }
    finally { setBusy(false) }
  }

  if (content.isError) return <Alert tone="warn">{describeError(content.error)}</Alert>
  if (!draft || !loaded) return <Skeletons count={3} />
  const lead = isLeadIn(flow.projectId)
  const root = draft as Item
  const setRoot = (next: Item) => setDraft(next)
  const list = (key: string) => (root[key] as Item[] | undefined) ?? []

  return (
    <EditProvider value={ctx}>
      <p className="muted small">Version {loaded.versionNo} ({loaded.source.toLowerCase()}) by {loaded.createdByName}</p>
      {canEdit && <Alert>Change anything that is wrong. Tap the padlock on an item to lock it: the AI will never change locked items when you analyse again.</Alert>}

      <details className="section" open>
        <summary>Flow diagram</summary>
        <MermaidView source={String(root.mermaid ?? '')} dark={isDark()} />
        <p className="muted small">The diagram is redrawn from the steps and decisions when you save.</p>
      </details>
      <details className="section"><summary>Overview</summary><ObjectEditor value={root} only={OVERVIEW} onChange={setRoot} /></details>
      <details className="section"><summary>Use case summary</summary>
        <ObjectEditor value={(root.useCase as Item) ?? {}} parent="useCase" onChange={x => setRoot({ ...root, useCase: x })} />
      </details>
      {SECTIONS.map(s => (
        <details className="section" key={s.key}>
          <summary>{s.title} ({list(s.key).length})</summary>
          <ItemList listKey={s.key} title={s.title} value={list(s.key)} onChange={x => setRoot({ ...root, [s.key]: x })} />
        </details>
      ))}

      {errors.length > 0 && (
        <Alert tone="error">
          <div>
            <b>The changes could not be saved:</b>
            {errors.slice(0, 8).map((e, i) => <div key={i}>{e}</div>)}
            {conflict && <button className="btn outline" style={{ marginTop: 8 }} onClick={() => { void content.refetch().then(r => r.data && load(r.data)) }}>Reload the latest version</button>}
          </div>
        </Alert>
      )}

      <div className="actionbar">
        {canEdit && dirty && (
          <>
            <div className="field" style={{ marginBottom: 8 }}>
              <label htmlFor="change-note">What did you change? (optional)</label>
              <input id="change-note" className="input" value={note} onChange={e => setNote(e.target.value)} />
            </div>
            <div className="row">
              <button className="btn primary" onClick={() => void save()} disabled={busy}>{busy ? 'Saving…' : 'Save changes'}</button>
              <button className="btn" onClick={() => { if (content.data) { load(content.data); toast('Your unsaved changes were discarded.') } }} disabled={busy}>Discard</button>
            </div>
          </>
        )}
        {dirty ? <div className="muted small" style={{ marginTop: 8 }}>Save or discard your edits before moving the flow forward.</div> : (
          <div className="row">
            {flow.status === 'Analysed' && canEdit && <button className="btn primary" disabled={busy} onClick={() => void move('submit-review', {}, 'Submitted for review.')}>Submit for review</button>}
            {flow.status === 'UnderReview' && lead && <button className="btn primary" disabled={busy} onClick={() => setConfirming(true)}><SealCheck size={18} weight="bold" aria-hidden />Confirm this version</button>}
            {(flow.status === 'Confirmed' || flow.status === 'DocumentsGenerated') && lead && <button className="btn outline" disabled={busy} onClick={() => void move('reopen', {}, 'Re-opened. It is under review again.')}>Re-open for changes</button>}
            {flow.status === 'UnderReview' && !lead && <span className="muted small">Waiting for the Implementation Lead to confirm.</span>}
            {(flow.status === 'Confirmed' || flow.status === 'DocumentsGenerated') && <span className="muted small">{statusText(flow.status)}: confirmed version {flow.confirmedVersionNo}. Any change moves the flow back to review.</span>}
          </div>
        )}
      </div>

      {confirming && (
        <ConfirmDialog title="Confirm this flow?" okText="Confirm"
          message={`Version ${loaded.versionNo} becomes the confirmed version used for the requirement document. Any later change moves the flow back to review.`}
          onClose={() => setConfirming(false)}
          onConfirm={() => { setConfirming(false); void move('confirm', { versionNo: loaded.versionNo }, 'Confirmed.') }} />
      )}
    </EditProvider>
  )
}
