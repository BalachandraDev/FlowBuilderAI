import { useEffect, useMemo, useState } from 'react'
import { CaretRight, FolderOpen, Folder, Plus, Trash } from '@phosphor-icons/react'
import { useQueryClient } from '@tanstack/react-query'
import { describeError, post } from '../api/client'
import { keys, useCreateFlow, useDeleteFlow, useDepartments, useMasterDepartments } from '../api/hooks'
import type { Flow, ProjectDepartment, RequestKind } from '../api/types'
import { statusText, statusTone } from '../lib/format'
import { Alert, ConfirmDialog, Pill, useToast } from './ui'

type Kind = RequestKind
/** The three kinds of request. A bug is filed, numbered, noted (typed, spoken, photographed, attached) and documented exactly like the other two. */
const KINDS: { kind: Kind; title: string; singular: string; label: string; hint: string }[] = [
  { kind: 'NR', title: 'New requirements', singular: 'new requirement', label: 'New requirement (NR)', hint: 'What is new? For example: Bed allocation' },
  { kind: 'CR', title: 'Change requests', singular: 'change request', label: 'Change request (CR)', hint: 'What changes? For example: Discharge summary print' },
  { kind: 'BUG', title: 'Bugs', singular: 'bug', label: 'Bug (BUG)', hint: 'What is broken? For example: Discharge summary shows the wrong ward' },
]

/** The request number as people say it: NR-003. Requests made before numbering existed show the number they were given when the app moved over. */
export const requestCode = (f: Pick<Flow, 'kind' | 'requestNo'>) => `${f.kind ?? 'NR'}-${String(f.requestNo ?? 0).padStart(3, '0')}`

/**
 * How the company files its work: a project has modules (Ward, Nurse...), each module has New requirements (NR) and Change requests (CR),
 * and every request has its own notes, workflow diagram and implementation document. Picking a request opens it.
 */
export function RequestFolders({ projectId, flows, selectedId, onSelect, canCapture, isLead }: {
  projectId: string; flows: Flow[]; selectedId: string; onSelect: (id: string) => void; canCapture: boolean; isLead: boolean
}) {
  const departments = useDepartments(projectId)
  const createFlow = useCreateFlow(projectId)
  const deleteFlow = useDeleteFlow(projectId)
  const [deleting, setDeleting] = useState<Flow | null>(null)
  const qc = useQueryClient()
  const toast = useToast()
  const selected = flows.find(f => f.id === selectedId)
  const [open, setOpen] = useState<Set<string>>(() => new Set(selected ? [selected.projectDepartmentId] : []))
  const [adding, setAdding] = useState<{ dept: string; kind: Kind } | null>(null)
  const [name, setName] = useState('')
  const [addingModule, setAddingModule] = useState(false)
  const [pick, setPick] = useState('')                                    // a module of the administrator's list, or nothing yet
  const [quick, setQuick] = useState<{ dept: string; kind: Kind; name: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const master = useMasterDepartments()

  // the module of the request being worked on is always open
  useEffect(() => { if (selected) setOpen(o => (o.has(selected.projectDepartmentId) ? o : new Set(o).add(selected.projectDepartmentId))) }, [selected?.projectDepartmentId])   // eslint-disable-line react-hooks/exhaustive-deps

  const modules: ProjectDepartment[] = useMemo(() => departments.data ?? [], [departments.data])
  const toggle = (id: string) => setOpen(o => { const n = new Set(o); if (n.has(id)) n.delete(id); else n.add(id); return n })

  // Modules are created by the administrator (Admin > Modules). A project can only take one of them, so this is a list to pick from and never a box to type in:
  // the ones the project does not have yet, and are not stopped.
  const choices = useMemo(() => {
    const have = new Set(modules.flatMap(m => [m.moduleCode.toUpperCase(), m.name.toLowerCase()]))
    return (master.data ?? []).filter(d => d.isActive && !have.has(d.code.toUpperCase()) && !have.has(d.name.toLowerCase())).sort((a, b) => a.name.localeCompare(b.name))
  }, [master.data, modules])
  const canAddModule = pick !== '' && choices.some(c => c.id === pick)

  async function createRequest(dept: string, kind: Kind, requestName: string) {
    setBusy(true); setError(null)
    try {
      const made = await createFlow.mutateAsync({ projectDepartmentId: dept, menuName: requestName.trim(), kind })
      toast(`${requestCode(made)} was created.`, 'good')
      setName(''); setAdding(null); setQuick(null)
      setOpen(o => new Set(o).add(dept))
      onSelect(made.id)
    } catch (e) { setError(describeError(e)) } finally { setBusy(false) }
  }
  const addRequest = () => { if (adding && name.trim()) void createRequest(adding.dept, adding.kind, name) }

  async function addModule() {
    if (!canAddModule) return
    setBusy(true); setError(null)
    try {
      const made = await post<ProjectDepartment>(`projects/${projectId}/departments`, { departmentId: pick })
      await qc.invalidateQueries({ queryKey: keys.departments(projectId) })
      toast(`The module ${made.name} was added.`, 'good')
      setPick(''); setAddingModule(false)
      setOpen(o => new Set(o).add(made.id))
    } catch (e) { setError(describeError(e)) } finally { setBusy(false) }
  }

  function openQuick() {
    if (quick) { setQuick(null); return }
    setError(null); setAdding(null)
    setQuick({ dept: selected?.projectDepartmentId ?? modules[0]?.id ?? '', kind: 'NR', name: '' })
  }
  const quickKind = quick ? KINDS.find(k => k.kind === quick.kind)! : null

  return (
    <section className="panel folders" aria-labelledby="h-folders">
      <div className="folders-head">
        <h2 id="h-folders" style={{ margin: 0 }}>Modules and requests</h2>
        <div className="row">
          {canCapture && modules.length > 0 && <button className="btn text" onClick={openQuick} aria-expanded={!!quick}><Plus size={16} aria-hidden />New request</button>}
          {isLead && <button className="btn text" onClick={() => { setAddingModule(a => !a); setError(null) }} aria-expanded={addingModule}><Plus size={16} aria-hidden />Add a module</button>}
        </div>
      </div>
      <p className="muted small" style={{ marginTop: 4 }}>Each module has its new requirements (NR), change requests (CR) and bugs (BUG). Every request has its own notes, workflow and document.</p>
      {quick && quickKind && (
        <form className="folder-form" aria-label="New request" onSubmit={e => { e.preventDefault(); if (quick.name.trim() && quick.dept) void createRequest(quick.dept, quick.kind, quick.name) }}>
          <label className="sr-only" htmlFor="quick-module">Module of the new request</label>
          <select id="quick-module" className="input" style={{ flex: '0 1 200px' }} value={quick.dept} onChange={e => setQuick({ ...quick, dept: e.target.value })}>
            {modules.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}
          </select>
          <label className="sr-only" htmlFor="quick-kind">Type of the new request</label>
          <select id="quick-kind" className="input" style={{ flex: '0 1 210px' }} value={quick.kind} onChange={e => setQuick({ ...quick, kind: e.target.value as Kind })}>
            {KINDS.map(k => <option key={k.kind} value={k.kind}>{k.label}</option>)}
          </select>
          <label className="sr-only" htmlFor="quick-name">Name of the new request</label>
          <input id="quick-name" className="input" autoFocus value={quick.name} onChange={e => setQuick({ ...quick, name: e.target.value })} placeholder={quickKind.hint} maxLength={200} />
          <button className="btn primary" type="submit" disabled={busy || !quick.name.trim() || !quick.dept}>Create {quick.kind}</button>
          <button className="btn" type="button" onClick={() => setQuick(null)}>Cancel</button>
        </form>
      )}
      {addingModule && (
        <form className="folder-form" onSubmit={e => { e.preventDefault(); void addModule() }}>
          {master.isPending && <div className="muted small" role="status">Loading the list of modules…</div>}
          {master.isError && <Alert tone="error">{describeError(master.error)}</Alert>}
          {master.data && choices.length === 0 && (
            <div className="muted small" role="status" style={{ flex: '1 1 100%' }}>
              {(master.data ?? []).some(d => d.isActive) ? 'This project already has every module on the list.' : 'There are no modules yet.'} Modules are created by the administrator (Admin, then Modules); ask them to add the one you need.
            </div>
          )}
          {choices.length > 0 && (
            <>
              <label className="sr-only" htmlFor="module-pick">Module to add</label>
              <select id="module-pick" className="input" autoFocus style={{ flex: '1 1 220px' }} value={pick} onChange={e => setPick(e.target.value)}>
                <option value="">Choose a module…</option>
                {choices.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
              </select>
            </>
          )}
          <button className="btn primary" type="submit" disabled={busy || !canAddModule}>Add module</button>
          <button className="btn" type="button" onClick={() => setAddingModule(false)}>Cancel</button>
        </form>
      )}
      {error && <Alert tone="error">{error}</Alert>}

      {deleting && (
        <ConfirmDialog
          title={`Delete ${requestCode(deleting)}?`}
          message={`"${deleting.menuName}" with its notes, workflow and document disappears from the lists and nobody can open it. Nothing is erased from the database, so an administrator can bring it back. The number ${requestCode(deleting)} is not used again.`}
          okText="Delete request" danger onClose={() => setDeleting(null)}
          onConfirm={() => {
            const gone = deleting
            setDeleting(null)
            deleteFlow.mutate(gone.id, { onSuccess: () => { toast(`${requestCode(gone)} was deleted.`, 'good'); if (gone.id === selectedId) onSelect(flows.find(f => f.id !== gone.id)?.id ?? '') }, onError: e => toast(describeError(e), 'error') })
          }} />
      )}
      <ul className="tree" aria-label="Modules">
        {modules.map(m => {
          const mine = flows.filter(f => f.projectDepartmentId === m.id)
          const isOpen = open.has(m.id)
          return (
            <li key={m.id} className="tree-module">
              <button className="tree-head" aria-expanded={isOpen} onClick={() => toggle(m.id)}>
                <CaretRight size={14} className="tree-caret" aria-hidden />
                {isOpen ? <FolderOpen size={20} weight="fill" aria-hidden /> : <Folder size={20} weight="fill" aria-hidden />}
                <b>{m.name}</b>
                <span className="muted small">{mine.length === 0 ? 'no requests yet' : `${mine.length} request${mine.length === 1 ? '' : 's'}`}</span>
              </button>
              {isOpen && (
                <div className="tree-body">
                  {KINDS.map(k => {
                    const rows = mine.filter(f => (f.kind ?? 'NR') === k.kind).sort((a, b) => (a.requestNo ?? 0) - (b.requestNo ?? 0))
                    const addingHere = adding?.dept === m.id && adding.kind === k.kind
                    return (
                      <div key={k.kind} className="tree-kind" role="group" aria-label={`${m.name}: ${k.title}`}>
                        <div className="tree-kind-head">
                          <span className={`kind-tag ${k.kind.toLowerCase()}`}>{k.kind}</span><b>{k.title}</b><span className="muted small">{rows.length}</span>
                          {canCapture && <button className="btn text" aria-label={`New ${k.singular} in ${m.name}`} onClick={() => { setAdding(addingHere ? null : { dept: m.id, kind: k.kind }); setName(''); setError(null) }}><Plus size={14} aria-hidden />New</button>}
                        </div>
                        {addingHere && (
                          <form className="folder-form" onSubmit={e => { e.preventDefault(); void addRequest() }}>
                            <label className="sr-only" htmlFor="request-name">Name of the {k.singular}</label>
                            <input id="request-name" className="input" autoFocus value={name} onChange={e => setName(e.target.value)} placeholder={k.hint} maxLength={200} />
                            <button className="btn primary" type="submit" disabled={busy || !name.trim()}>Create {k.kind}</button>
                            <button className="btn" type="button" onClick={() => setAdding(null)}>Cancel</button>
                          </form>
                        )}
                        {rows.length === 0 && !addingHere && <div className="muted small tree-none">None yet.</div>}
                        <ul>
                          {rows.map(f => (
                            <li key={f.id} className="tree-reqrow">
                              <button className={`tree-req ${f.id === selectedId ? 'on' : ''}`} aria-current={f.id === selectedId ? 'true' : undefined} onClick={() => onSelect(f.id)}>
                                <span className={`kind-tag ${k.kind.toLowerCase()}`}>{requestCode(f)}</span>
                                <span className="tree-req-name">{f.menuName}</span>
                                <Pill tone={statusTone(f.status)}>{statusText(f.status)}</Pill>
                              </button>
                              {canCapture && <button className="tree-del" aria-label={`Delete ${requestCode(f)} ${f.menuName}`} title={`Delete ${requestCode(f)}`} onClick={() => setDeleting(f)}><Trash size={16} aria-hidden /></button>}
                            </li>
                          ))}
                        </ul>
                      </div>
                    )
                  })}
                </div>
              )}
            </li>
          )
        })}
      </ul>
    </section>
  )
}
