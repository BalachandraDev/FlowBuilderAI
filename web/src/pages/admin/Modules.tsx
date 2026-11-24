import { useMemo, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { MagnifyingGlass, PencilSimple, Plus, Stack } from '@phosphor-icons/react'
import { api, ApiError, describeError } from '../../api/client'
import { useMasterDepartments } from '../../api/hooks'
import type { MasterDepartment } from '../../api/types'
import { ErrorState } from '../../components/ErrorState'
import { Alert, Dialog, Field, Pill, Skeletons, useToast } from '../../components/ui'

const problem = (e: unknown) => (e instanceof ApiError && e.errors ? Object.values(e.errors).flat().join(' ') : describeError(e))

/** The short code that stands for a module in document numbers (IMPL-WARD-NR-001): letters and digits of the name, made unique with a number if needed. */
export function moduleCode(name: string, taken: string[]): string | null {
  const base = name.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 18)
  if (base.length < 2) return null
  const used = new Set(taken.map(c => c.toUpperCase()))
  let code = base
  for (let n = 2; used.has(code); n++) code = base.slice(0, 20 - String(n).length) + n
  return code
}

/** Name of a module: used to add one and to rename one. */
function ModuleForm({ module, all, onClose, onSaved }: { module?: MasterDepartment; all: MasterDepartment[]; onClose: () => void; onSaved: (m: MasterDepartment) => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(module?.name ?? '')
  const [touched, setTouched] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const trimmed = name.trim()
  const duplicate = !!trimmed && all.some(m => m.id !== module?.id && m.name.trim().toLowerCase() === trimmed.toLowerCase())
  const code = module ? module.code : moduleCode(trimmed, all.map(m => m.code))
  const nameError = duplicate ? 'There is already a module with this name.' : touched && !trimmed ? 'Enter the name of the module.' : trimmed && !code ? 'Use at least two letters or digits.' : null
  const ok = !!trimmed && !duplicate && !!code && (!module || trimmed !== module.name)

  async function save() {
    if (!code) return
    setBusy(true); setError(null)
    try {
      const body = { code, name: trimmed, isActive: module?.isActive ?? true }
      const saved = module ? await api<MasterDepartment>(`master/departments/${module.id}`, { method: 'PUT', body }) : await api<MasterDepartment>('master/departments', { method: 'POST', body })
      await qc.invalidateQueries({ queryKey: ['master-departments'] }); await qc.invalidateQueries({ queryKey: ['departments'] }); await qc.invalidateQueries({ queryKey: ['flows'] })
      onSaved(saved)
    } catch (e) { setError(problem(e)); setBusy(false) }
  }

  return (
    <Dialog title={module ? `Rename ${module.name}` : 'Add a module'} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" onClick={() => void save()} disabled={busy || !ok}>{busy ? 'Saving…' : module ? 'Save' : 'Add module'}</button>
    </>}>
      <form onSubmit={e => { e.preventDefault(); if (ok && !busy) void save() }}>
        <Field label="Module name" required error={nameError} help="For example Ward, Nurse Station, Pharmacy. Users pick it from a list when they add a module to a project.">
          {(id, aria) => <input id={id} className="input" autoFocus value={name} onChange={e => setName(e.target.value)} onBlur={() => setTouched(true)} maxLength={120} {...aria} />}
        </Field>
        {code && trimmed && <p className="muted small">Short code in document numbers: <b>{code}</b>{module ? ' (it does not change when the module is renamed)' : ''}</p>}
        <button type="submit" hidden tabIndex={-1} aria-hidden>Save</button>
        {error && <Alert tone="error">{error}</Alert>}
      </form>
    </Dialog>
  )
}

/** Only the administrator makes modules. Users can only pick from this list when they add a module to a project. */
export function ModulesPage() {
  const modules = useMasterDepartments()
  const qc = useQueryClient()
  const toast = useToast()
  const [adding, setAdding] = useState(false)
  const [editing, setEditing] = useState<MasterDepartment | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const all = useMemo(() => [...(modules.data ?? [])].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })), [modules.data])
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  const view = all.filter(m => words.every(w => `${m.name} ${m.code}`.toLowerCase().includes(w)))
  const active = all.filter(m => m.isActive).length

  async function setActive(m: MasterDepartment, isActive: boolean) {
    setBusy(m.id)
    try {
      await api(`master/departments/${m.id}`, { method: 'PUT', body: { code: m.code, name: m.name, isActive } })
      await qc.invalidateQueries({ queryKey: ['master-departments'] })
    } catch (e) { toast(describeError(e), 'error') } finally { setBusy(null) }
  }

  return (
    <>
      <div className="page-title">
        <div><h1>Modules</h1><p className="muted">The modules users can add to a project (Ward, Pharmacy, Billing...). Only you make them: a user picks one from this list and cannot type a new one.</p></div>
        <button className="btn primary lg" onClick={() => setAdding(true)}><Plus size={20} weight="bold" aria-hidden />Add module</button>
      </div>

      {modules.isPending && <Skeletons count={3} />}
      {modules.isError && <ErrorState error={modules.error} onRetry={() => void modules.refetch()} retrying={modules.isFetching} />}
      {modules.data?.length === 0 && <div className="empty" role="status"><b>No modules yet</b><div>Press <b>Add module</b>. Users cannot add a module to a project until there is one on this list.</div></div>}
      {all.length > 6 && (
        <div className="listbar" role="search" aria-label="Find a module">
          <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search modules</span>
            <input className="input" type="search" placeholder="Search by name or code" value={query} onChange={e => setQuery(e.target.value)} /></label>
          <span className="grow" /><span className="muted small" role="status">{active} of {all.length} in use</span>
        </div>
      )}
      {all.length > 0 && view.length === 0 && <div className="empty" role="status"><b>No module matches</b></div>}
      <ul className="user-list" aria-label="Modules">
        {view.map(m => (
          <li key={m.id} className={`urow ${m.isActive ? '' : 'off'}`}>
            <span className="how-icon" aria-hidden><Stack size={26} weight="duotone" /></span>
            <div className="urow-main"><b>{m.name}</b><span className="muted small">Code {m.code}</span></div>
            <div className="urow-tags">{!m.isActive && <Pill tone="bad">Stopped</Pill>}</div>
            <div className="urow-actions">
              <button className="btn" aria-label={`Rename ${m.name}`} onClick={() => setEditing(m)}><PencilSimple size={18} aria-hidden />Rename</button>
              <button className="btn" aria-label={`${m.isActive ? 'Stop' : 'Allow'} ${m.name}`} disabled={busy === m.id} onClick={() => void setActive(m, !m.isActive)}>{m.isActive ? 'Stop' : 'Allow again'}</button>
            </div>
          </li>
        ))}
      </ul>
      <p className="muted small">Stopping a module only removes it from the list users pick from. Projects that already have it keep it, with all their requests and documents. Renaming a module changes its name everywhere; its code in document numbers stays.</p>

      {adding && <ModuleForm all={all} onClose={() => setAdding(false)} onSaved={m => { setAdding(false); toast(`${m.name} was added. Users can pick it now.`, 'good') }} />}
      {editing && <ModuleForm module={editing} all={all} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); toast('Saved.', 'good') }} />}
    </>
  )
}
