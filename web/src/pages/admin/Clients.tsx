import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { Buildings, DownloadSimple, MagnifyingGlass, PencilSimple, Plus, UsersThree } from '@phosphor-icons/react'
import { api, ApiError, describeError } from '../../api/client'
import { useClients, useUsers } from '../../api/hooks'
import type { Client, UserAccount } from '../../api/types'
import { ErrorState } from '../../components/ErrorState'
import { Pager, pageOf } from '../../components/Pager'
import { Alert, Dialog, Field, Pill, Skeletons, useToast } from '../../components/ui'
import { downloadCsv, stamp } from '../../ux/csv'
import { usePref } from '../../ux/prefs'

const problem = (e: unknown) => (e instanceof ApiError && e.errors ? Object.values(e.errors).flat().join(' ') : describeError(e))

/** Name and place of a client: used to add one and to change one. */
function ClientForm({ client, others, onClose, onSaved }: { client?: Client; others: Client[]; onClose: () => void; onSaved: (c: Client) => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(client?.name ?? '')
  const [location, setLocation] = useState(client?.location ?? '')
  const [touched, setTouched] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const duplicate = !!name.trim() && others.some(c => c.id !== client?.id && c.name.trim().toLowerCase() === name.trim().toLowerCase())
  const nameError = duplicate ? 'A client with this name already exists.' : touched && !name.trim() ? 'Enter the client’s name.' : null
  const ok = name.trim().length > 0 && !duplicate && (!client || name.trim() !== client.name || location.trim() !== client.location)

  async function save() {
    setBusy(true); setError(null)
    try {
      const saved = client
        ? await api<Client>(`clients/${client.id}`, { method: 'PUT', body: { name: name.trim(), location: location.trim() || null, isActive: client.isActive } })
        : await api<Client>('clients', { method: 'POST', body: { name: name.trim(), location: location.trim() || null } })
      await qc.invalidateQueries({ queryKey: ['clients'] }); await qc.invalidateQueries({ queryKey: ['projects'] })
      onSaved(saved)
    } catch (e) { setError(problem(e)); setBusy(false) }
  }

  return (
    <Dialog title={client ? `Change ${client.name}` : 'Add a client'} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" onClick={() => void save()} disabled={busy || !ok}>{busy ? 'Saving…' : client ? 'Save' : 'Add client'}</button>
    </>}>
      <Field label="Client name" required error={nameError} help="The hospital or hospital group, for example Kauvery Hospital.">{(id, aria) => <input id={id} className="input" autoFocus value={name} onChange={e => setName(e.target.value)} onBlur={() => setTouched(true)} maxLength={200} {...aria} />}</Field>
      <Field label="Place (optional)">{id => <input id={id} className="input" value={location} onChange={e => setLocation(e.target.value)} maxLength={200} placeholder="Chennai" />}</Field>
      {error && <Alert tone="error">{error}</Alert>}
    </Dialog>
  )
}

/** Tick the people who work for this client. They can then pick it when they start a project, and open every project of the client. */
function AssignPeople({ client, onClose, onSaved }: { client: Client; onClose: () => void; onSaved: () => void }) {
  const qc = useQueryClient()
  const users = useUsers()
  const [chosen, setChosen] = useState<Set<string>>(() => new Set(client.userIds))
  const [find, setFind] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // administrators see everything already, so only the other accounts are listed (a stopped account stays listed while it is assigned)
  const everyone = (users.data ?? []).filter((u: UserAccount) => !u.roles.includes('Admin') && (u.isActive || chosen.has(u.id)))
  const words = find.trim().toLowerCase().split(/\s+/).filter(Boolean)
  const people = everyone.filter(u => words.every(w => `${u.displayName} ${u.email}`.toLowerCase().includes(w)))
  const tickShown = (on: boolean) => setChosen(s => { const n = new Set(s); people.forEach(u => (on ? n.add(u.id) : n.delete(u.id))); return n })
  const changed = chosen.size !== client.userIds.length || client.userIds.some(id => !chosen.has(id))
  const toggle = (id: string) => setChosen(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })

  async function save() {
    setBusy(true); setError(null)
    try {
      await api<Client>(`clients/${client.id}/users`, { method: 'PUT', body: { userIds: [...chosen] } })
      await qc.invalidateQueries({ queryKey: ['clients'] }); await qc.invalidateQueries({ queryKey: ['projects'] })
      onSaved()
    } catch (e) { setError(problem(e)); setBusy(false) }
  }

  return (
    <Dialog title={`People for ${client.name}`} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" onClick={() => void save()} disabled={busy || !changed}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <p className="muted small">Everyone ticked can start projects for this client and open all of its projects, to add notes and make documents. Only the person who started a project (or an administrator) can delete it.</p>
      {users.isPending && <Skeletons count={2} />}
      {users.isError && <Alert tone="error">{describeError(users.error)}</Alert>}
      {users.data && everyone.length === 0 && <div className="empty" role="status"><b>No users yet</b><div>Add users first, then come back to assign them.</div></div>}
      {everyone.length > 6 && (
        <div className="choose-head">
          <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Find a person</span>
            <input className="input" type="search" placeholder="Find a person" value={find} onChange={e => setFind(e.target.value)} /></label>
          <button className="btn link" onClick={() => tickShown(true)} disabled={people.length === 0}>Tick shown</button>
          <button className="btn link" onClick={() => tickShown(false)} disabled={people.length === 0}>Untick shown</button>
        </div>
      )}
      {everyone.length > 0 && <p className="muted small" role="status">{chosen.size} of {everyone.length} ticked{find.trim() ? ` · ${people.length} shown` : ''}</p>}
      {everyone.length > 0 && people.length === 0 && <div className="empty" role="status"><b>Nobody matches “{find}”</b></div>}
      <ul className="user-list" aria-label="People">
        {people.map(u => (
          <li key={u.id} className="urow">
            <label className="check">
              <input type="checkbox" checked={chosen.has(u.id)} onChange={() => toggle(u.id)} />
              <span><b>{u.displayName}</b><br /><span className="muted small">{u.email}{u.isActive ? '' : ' · access stopped'}</span></span>
            </label>
          </li>
        ))}
      </ul>
      {error && <Alert tone="error">{error}</Alert>}
    </Dialog>
  )
}

export function ClientsPage() {
  const clients = useClients()
  const users = useUsers()
  const qc = useQueryClient()
  const toast = useToast()
  const [adding, setAdding] = useState(false)
  const [editing, setEditing] = useState<Client | null>(null)
  const [assigning, setAssigning] = useState<Client | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const nameOf = (id: string) => users.data?.find(u => u.id === id)?.displayName
  const [params] = useSearchParams()
  const [query, setQuery] = useState(params.get('q') ?? '')
  const [status, setStatus, resetStatus] = usePref<'all' | 'active' | 'stopped' | 'nobody'>('clients.status', 'all')
  const [sort, setSort, resetSort] = usePref<'name' | 'projects' | 'people'>('clients.sort', 'name')
  const [size, setSize] = usePref<number>('clients.size', 25)
  const [page, setPage] = useState(0)
  const all = clients.data ?? []
  const view = useMemo(() => {
    const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
    const list = all.filter(c => words.every(w => `${c.name} ${c.location}`.toLowerCase().includes(w))
      && (status === 'all' || (status === 'active' && c.isActive) || (status === 'stopped' && !c.isActive) || (status === 'nobody' && c.userIds.length === 0)))
    return [...list].sort((a, b) => sort === 'projects' ? b.projectCount - a.projectCount || a.name.localeCompare(b.name) : sort === 'people' ? b.userIds.length - a.userIds.length || a.name.localeCompare(b.name) : a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }))
  }, [all, query, status, sort])
  const filtered = query.trim() !== '' || status !== 'all' || sort !== 'name'
  const clearFilters = () => { setQuery(''); resetStatus(); resetSort(); setPage(0) }
  const exportCsv = () => downloadCsv(`clients-${stamp()}`, [['Client', 'Place', 'Projects', 'People', 'Access'], ...view.map(c => [c.name, c.location, c.projectCount, c.userIds.map(nameOf).filter(Boolean).join('; '), c.isActive ? 'Active' : 'Stopped'])])

  async function setActive(c: Client, isActive: boolean) {
    setBusy(c.id)
    try {
      await api(`clients/${c.id}`, { method: 'PUT', body: { name: c.name, location: c.location || null, isActive } })
      await qc.invalidateQueries({ queryKey: ['clients'] })
    } catch (e) { toast(describeError(e), 'error') } finally { setBusy(null) }
  }

  return (
    <>
      <div className="page-title">
        <div><h1>Clients</h1><p className="muted">Make a client, then assign the users who work for it. A user picks one of their clients when starting a project, and sees every project of that client.</p></div>
        <button className="btn primary lg" onClick={() => setAdding(true)}><Plus size={20} weight="bold" aria-hidden />Add client</button>
      </div>

      {clients.isPending && <Skeletons count={3} />}
      {clients.isError && <ErrorState error={clients.error} onRetry={() => void clients.refetch()} retrying={clients.isFetching} />}
      {clients.data?.length === 0 && <div className="empty" role="status"><b>No clients yet</b><div>Press <b>Add client</b>, then assign users to it. Users cannot start a project until they have a client.</div></div>}
      {all.length > 0 && (
        <div className="listbar" role="search" aria-label="Find a client">
          <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search clients</span>
            <input className="input" type="search" placeholder="Search by client name or place" value={query} onChange={e => { setQuery(e.target.value); setPage(0) }} /></label>
          <label className="field-inline"><span>Show</span>
            <select className="input" value={status} onChange={e => { setStatus(e.target.value as typeof status); setPage(0) }} aria-label="Filter clients">
              <option value="all">All clients</option><option value="active">Active</option><option value="stopped">Stopped</option><option value="nobody">Nobody assigned</option></select></label>
          <label className="field-inline"><span>Sort</span>
            <select className="input" value={sort} onChange={e => setSort(e.target.value as typeof sort)} aria-label="Sort clients"><option value="name">Name</option><option value="projects">Most projects</option><option value="people">Most people</option></select></label>
          {filtered && <button className="btn link" onClick={clearFilters}>Clear filters</button>}
          <span className="grow" />
          <button className="btn" onClick={exportCsv} disabled={view.length === 0}><DownloadSimple size={16} aria-hidden />Export CSV</button>
        </div>
      )}
      {all.length > 0 && view.length === 0 && <div className="empty" role="status"><b>No client matches</b><button className="btn link" onClick={clearFilters}>Clear the filters</button></div>}
      <ul className="user-list" aria-label="Clients">
        {pageOf(view, page, size).map(c => {
          const names = c.userIds.map(nameOf).filter(Boolean) as string[]
          return (
            <li key={c.id} className={`urow ${c.isActive ? '' : 'off'}`}>
              <span className="how-icon" aria-hidden><Buildings size={26} weight="duotone" /></span>
              <div className="urow-main">
                <b>{c.name}</b>
                <span className="muted small">{[c.location, `${c.projectCount} project${c.projectCount === 1 ? '' : 's'}`].filter(Boolean).join(' · ')}</span>
                <span className="muted small">{c.userIds.length === 0 ? 'Nobody assigned yet' : `${c.userIds.length} ${c.userIds.length === 1 ? 'person' : 'people'}: ${names.join(', ') || '…'}`}</span>
              </div>
              <div className="urow-tags">{!c.isActive && <Pill tone="bad">Stopped</Pill>}</div>
              <div className="urow-actions">
                <button className="btn" aria-label={`Assign people to ${c.name}`} onClick={() => setAssigning(c)}><UsersThree size={18} aria-hidden />People</button>
                <button className="btn" aria-label={`Change ${c.name}`} onClick={() => setEditing(c)}><PencilSimple size={18} aria-hidden />Edit</button>
                <button className="btn" aria-label={`${c.isActive ? 'Stop' : 'Allow'} ${c.name}`} disabled={busy === c.id} onClick={() => void setActive(c, !c.isActive)}>{c.isActive ? 'Stop' : 'Allow again'}</button>
              </div>
            </li>
          )
        })}
      </ul>
      <Pager total={view.length} page={page} size={size} onPage={setPage} onSize={setSize} />
      <p className="muted small">Stopping a client only hides it from the list users choose from. Its existing projects and documents stay where they are.</p>

      {adding && <ClientForm others={all} onClose={() => setAdding(false)} onSaved={c => { setAdding(false); toast(`${c.name} was added. Now assign its people.`, 'good'); setAssigning(c) }} />}
      {editing && <ClientForm client={editing} others={all} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); toast('Saved.', 'good') }} />}
      {assigning && <AssignPeople client={assigning} onClose={() => setAssigning(null)} onSaved={() => { setAssigning(null); toast('Saved. They can pick this client now.', 'good') }} />}
    </>
  )
}
