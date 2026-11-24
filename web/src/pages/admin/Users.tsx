import { useMemo, useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { Copy, DownloadSimple, KeyReturn, MagnifyingGlass, PencilSimple, ShieldCheck, Trash, UserCircleMinus, UserCirclePlus } from '@phosphor-icons/react'
import { api, ApiError, del, describeError } from '../../api/client'
import { useUsers } from '../../api/hooks'
import type { UserAccount } from '../../api/types'
import { ErrorState } from '../../components/ErrorState'
import { Pager, pageOf } from '../../components/Pager'
import { Alert, Avatar, ConfirmDialog, Dialog, Field, Pill, Skeletons, useToast } from '../../components/ui'
import { useSession } from '../../state/session'
import { downloadCsv, stamp } from '../../ux/csv'
import { usePref } from '../../ux/prefs'
import { useUnsavedWarning } from '../../ux/useUnsavedWarning'

const ADMIN = 'Admin'
const USER = 'ImplementationLead'
const isAdminAccount = (u: UserAccount) => u.roles.includes(ADMIN)

/** A password that meets the rules (10 or more characters, letters and digits) and is easy to read out loud. */
export function generatePassword(): string {
  const letters = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz'
  const digits = '23456789'
  const pick = (set: string, n: number) => Array.from(crypto.getRandomValues(new Uint32Array(n)), v => set[v % set.length]).join('')
  const parts = pick(letters, 7) + pick(digits, 3) + pick(letters, 2)
  return [...parts].sort(() => 0.5 - Math.random()).join('')
}

function Credentials({ title, email, password, onClose }: { title: string; email?: string; password: string; onClose: () => void }) {
  const toast = useToast()
  const text = [email && `Email: ${email}`, `Password: ${password}`].filter(Boolean).join('\n')
  return (
    <Dialog title={title} onClose={onClose} actions={<>
      <button className="btn" onClick={() => { void navigator.clipboard?.writeText(text).then(() => toast('Copied.', 'good'), () => toast('Could not copy. Select the text and copy it.', 'error')) }}><Copy size={18} aria-hidden />Copy</button>
      <button className="btn primary" onClick={onClose}>Done</button>
    </>}>
      <p className="muted">Copy these and give them to the person. The password is shown only now. If it is lost, make a new one from the users list.</p>
      <pre className="source" aria-label="Sign-in details">{text}</pre>
    </Dialog>
  )
}

const EMAIL = /^\S+@\S+\.\S+$/
const emailProblem = (email: string, others: UserAccount[], self?: string): string | null =>
  !email.trim() ? 'Enter their email address.' : !EMAIL.test(email.trim()) ? 'Enter a valid email address, like name@company.com.'
    : others.some(u => u.id !== self && u.email.toLowerCase() === email.trim().toLowerCase()) ? 'A user with this email already exists.' : null

function AddUser({ existing, onClose, onCreated }: { existing: UserAccount[]; onClose: () => void; onCreated: (email: string, password: string) => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState(generatePassword)
  const [admin, setAdmin] = useState(false)
  const [touched, setTouched] = useState({ name: false, email: false })
  const [asking, setAsking] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const nameError = touched.name && !name.trim() ? 'Enter their full name.' : null
  const mailError = (touched.email || email !== '') ? emailProblem(email, existing) : null
  const rules: [string, boolean][] = [['At least 10 characters', password.length >= 10], ['Has letters', /[A-Za-z]/.test(password)], ['Has digits', /\d/.test(password)]]
  const ok = !!name.trim() && !emailProblem(email, existing) && rules.every(r => r[1])
  const dirty = (name.trim() !== '' || email.trim() !== '') && !busy
  useUnsavedWarning(dirty)

  async function create() {
    if (!ok || busy) return
    setBusy(true); setError(null)
    try {
      await api('users', { method: 'POST', body: { email: email.trim(), displayName: name.trim(), password, roles: [admin ? ADMIN : USER] } })
      await qc.invalidateQueries({ queryKey: ['users'] })
      await qc.invalidateQueries({ queryKey: ['admin-overview'] })
      onCreated(email.trim(), password)
    } catch (e) {
      setError(e instanceof ApiError && e.errors ? Object.values(e.errors).flat().join(' ') : describeError(e))
      setBusy(false)
    }
  }
  const close = () => { if (dirty) setAsking(true); else onClose() }

  return (
    <Dialog title="Add a user" onClose={close} actions={<>
      <button className="btn" onClick={close} disabled={busy}>Cancel</button>
      <button className="btn primary" onClick={() => void create()} disabled={busy || !ok}>{busy ? 'Adding…' : 'Add user'}</button>
    </>}>
      <form onSubmit={(e: FormEvent) => { e.preventDefault(); void create() }} noValidate>
        <Field label="Full name" required error={nameError}>{(id, aria) => <input id={id} className="input" autoFocus value={name} onChange={e => setName(e.target.value)} onBlur={() => setTouched(t => ({ ...t, name: true }))} {...aria} />}</Field>
        <Field label="Email (they sign in with this)" required error={mailError}>{(id, aria) => <input id={id} className="input" type="email" value={email} onChange={e => setEmail(e.target.value)} onBlur={() => setTouched(t => ({ ...t, email: true }))} {...aria} />}</Field>
        <Field label="Password" required help="A good one is already filled in.">
          {(id, aria) => (
            <>
              <div className="row" style={{ flexWrap: 'nowrap' }}>
                <input id={id} className="input mono" value={password} onChange={e => setPassword(e.target.value)} {...aria} />
                <button className="btn" type="button" onClick={() => setPassword(generatePassword())}>New</button>
              </div>
              <ul className="checklist" aria-label="Requirements">{rules.map(([text, met]) => <li key={text} className={met ? 'ok' : ''}>{text}{met ? <span className="sr-only"> (met)</span> : <span className="sr-only"> (not met yet)</span>}</li>)}</ul>
            </>
          )}
        </Field>
        <label className="check switch"><input type="checkbox" checked={admin} onChange={e => setAdmin(e.target.checked)} /><span><b>Also an administrator</b><br /><span className="muted small">Can make users and set up the AI. Most people should not be.</span></span></label>
        <button type="submit" hidden tabIndex={-1} aria-hidden>Add user</button>
        {error && <Alert tone="error">{error}</Alert>}
      </form>
      {asking && <ConfirmDialog title="Close without adding?" message="What you typed will be lost." okText="Close" danger onClose={() => setAsking(false)} onConfirm={onClose} />}
    </Dialog>
  )
}
/** Change the name and the sign-in email of any account (administrators and users alike). */
function EditUser({ user, self, existing, onClose, onSaved }: { user: UserAccount; self: boolean; existing: UserAccount[]; onClose: () => void; onSaved: (emailChanged: boolean) => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(user.displayName)
  const [email, setEmail] = useState(user.email)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const changed = name.trim() !== user.displayName || email.trim().toLowerCase() !== user.email
  const mailError = email !== user.email ? emailProblem(email, existing, user.id) : null
  const ok = changed && name.trim().length > 0 && !emailProblem(email, existing, user.id)

  async function save() {
    setBusy(true); setError(null)
    try {
      await api(`users/${user.id}`, { method: 'PUT', body: { displayName: name.trim(), email: email.trim(), isActive: user.isActive, roles: user.roles, preferredLanguage: user.preferredLanguage } })
      await qc.invalidateQueries({ queryKey: ['users'] }); await qc.invalidateQueries({ queryKey: ['admin-overview'] })
      onSaved(email.trim().toLowerCase() !== user.email)
    } catch (e) {
      setError(e instanceof ApiError && e.errors ? Object.values(e.errors).flat().join(' ') : describeError(e))
      setBusy(false)
    }
  }

  return (
    <Dialog title={`Change ${user.displayName}`} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" onClick={() => void save()} disabled={busy || !ok}>{busy ? 'Saving…' : 'Save'}</button>
    </>}>
      <Field label="Full name" required error={!name.trim() ? 'Enter their full name.' : null}>{(id, aria) => <input id={id} className="input" autoFocus value={name} onChange={e => setName(e.target.value)} maxLength={120} {...aria} />}</Field>
      <Field label="Email (they sign in with this)" required error={mailError} help={self ? 'This is your own sign-in. After you change it, sign in with the new email next time.' : 'If you change it, they are signed out and must sign in with the new email.'}>
        {(id, aria) => <input id={id} className="input" type="email" value={email} onChange={e => setEmail(e.target.value)} maxLength={256} {...aria} />}
      </Field>
      {error && <Alert tone="error">{error}</Alert>}
    </Dialog>
  )
}

export function UsersPage() {
  const { user: me } = useSession()
  const users = useUsers()
  const qc = useQueryClient()
  const toast = useToast()
  const [adding, setAdding] = useState(false)
  const [shown, setShown] = useState<{ title: string; email?: string; password: string } | null>(null)
  const [toggle, setToggle] = useState<UserAccount | null>(null)
  const [editing, setEditing] = useState<UserAccount | null>(null)
  const [removing, setRemoving] = useState<UserAccount | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [params] = useSearchParams()
  const [query, setQuery] = useState(params.get('q') ?? '')
  const [role, setRole, resetRole] = usePref<'all' | 'user' | 'admin'>('users.role', 'all')
  const [status, setStatus, resetStatus] = usePref<'all' | 'active' | 'stopped'>('users.status', 'all')
  const [sort, setSort, resetSort] = usePref<'name' | 'email' | 'status'>('users.sort', 'name')
  const [size, setSize] = usePref<number>('users.size', 25)
  const [page, setPage] = useState(0)
  const all = users.data ?? []
  const view = useMemo(() => {
    const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
    const list = all.filter(u => words.every(w => `${u.displayName} ${u.email}`.toLowerCase().includes(w))
      && (role === 'all' || (role === 'admin') === isAdminAccount(u)) && (status === 'all' || (status === 'active') === u.isActive))
    return [...list].sort((a, b) => sort === 'email' ? a.email.localeCompare(b.email) : sort === 'status' ? Number(b.isActive) - Number(a.isActive) || a.displayName.localeCompare(b.displayName) : a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' }))
  }, [all, query, role, status, sort])
  const filtered = query.trim() !== '' || role !== 'all' || status !== 'all' || sort !== 'name'
  const clearFilters = () => { setQuery(''); resetRole(); resetStatus(); resetSort(); setPage(0) }
  const exportCsv = () => downloadCsv(`users-${stamp()}`, [['Name', 'Email', 'Role', 'Access'], ...view.map(u => [u.displayName, u.email, isAdminAccount(u) ? 'Administrator' : 'User', u.isActive ? 'Active' : 'Stopped'])])

  async function update(u: UserAccount, change: Partial<Pick<UserAccount, 'isActive' | 'roles'>>) {
    setBusy(u.id)
    try {
      await api(`users/${u.id}`, { method: 'PUT', body: { displayName: u.displayName, isActive: change.isActive ?? u.isActive, roles: change.roles ?? u.roles, preferredLanguage: u.preferredLanguage } })
      await qc.invalidateQueries({ queryKey: ['users'] }); await qc.invalidateQueries({ queryKey: ['admin-overview'] })
    } catch (e) { toast(describeError(e), 'error') } finally { setBusy(null) }
  }
  async function remove(u: UserAccount) {
    setBusy(u.id)
    try {
      await del(`users/${u.id}`)
      await qc.invalidateQueries({ queryKey: ['users'] }); await qc.invalidateQueries({ queryKey: ['admin-overview'] }); await qc.invalidateQueries({ queryKey: ['clients'] })
      toast(`${u.displayName} was deleted.`, 'good')
    } catch (e) { toast(describeError(e), 'error') } finally { setBusy(null) }
  }
  async function reset(u: UserAccount) {
    setBusy(u.id)
    try {
      const r = await api<{ temporaryPassword: string }>(`users/${u.id}/reset-password`, { method: 'POST', body: {} })
      setShown({ title: `New password for ${u.displayName}`, email: u.email, password: r.temporaryPassword })
    } catch (e) { toast(describeError(e), 'error') } finally { setBusy(null) }
  }

  return (
    <>
      <div className="page-title">
        <div><h1>Users</h1><p className="muted">Everyone who can sign in. Assign each user to their clients under Clients; see who handles what under Who handles what.</p></div>
        <button className="btn primary lg" onClick={() => setAdding(true)}><UserCirclePlus size={20} weight="bold" aria-hidden />Add user</button>
      </div>

      <div className="roles-help" aria-label="What each role can do">
        <div><b>User</b><span>Works on the clients assigned to them: makes projects, takes notes, makes the documents, and marks the one to give to the team as <i>final</i>. Sees only the documents of their own clients.</span></div>
        <div><b>Administrator</b><span>Makes users and clients and assigns users to clients. Sees only the documents users marked as final, and verifies them or returns them. Does not make documents.</span></div>
      </div>

      {users.isPending && <Skeletons count={3} />}
      {users.isError && <ErrorState error={users.error} onRetry={() => void users.refetch()} retrying={users.isFetching} />}
      {all.length > 0 && (
        <div className="listbar" role="search" aria-label="Find a user">
          <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search users</span>
            <input className="input" type="search" placeholder="Search by name or email" value={query} onChange={e => { setQuery(e.target.value); setPage(0) }} /></label>
          <label className="field-inline"><span>Role</span>
            <select className="input" value={role} onChange={e => { setRole(e.target.value as typeof role); setPage(0) }} aria-label="Filter by role"><option value="all">Everyone</option><option value="user">Users</option><option value="admin">Administrators</option></select></label>
          <label className="field-inline"><span>Access</span>
            <select className="input" value={status} onChange={e => { setStatus(e.target.value as typeof status); setPage(0) }} aria-label="Filter by access"><option value="all">All</option><option value="active">Active</option><option value="stopped">Stopped</option></select></label>
          <label className="field-inline"><span>Sort</span>
            <select className="input" value={sort} onChange={e => setSort(e.target.value as typeof sort)} aria-label="Sort users"><option value="name">Name</option><option value="email">Email</option><option value="status">Active first</option></select></label>
          {filtered && <button className="btn link" onClick={clearFilters}>Clear filters</button>}
          <span className="grow" />
          <button className="btn" onClick={exportCsv} disabled={view.length === 0}><DownloadSimple size={16} aria-hidden />Export CSV</button>
        </div>
      )}
      {all.length > 0 && view.length === 0 && <div className="empty" role="status"><b>No user matches</b><button className="btn link" onClick={clearFilters}>Clear the filters</button></div>}
      <ul className="user-list">
        {pageOf(view, page, size).map(u => {
          const self = u.id === me?.id
          return (
            <li key={u.id} className={`urow ${u.isActive ? '' : 'off'}`}>
              <Avatar name={u.displayName} size="lg" />
              <div className="urow-main">
                <b>{u.displayName}{self ? ' (you)' : ''}</b>
                <span className="muted small">{u.email}</span>
              </div>
              <div className="urow-tags">
                {isAdminAccount(u) ? <Pill tone="progress"><ShieldCheck size={14} aria-hidden />Administrator</Pill> : <Pill>User</Pill>}
                {!u.isActive && <Pill tone="bad">Stopped</Pill>}
              </div>
              <div className="urow-actions">
                <button className="btn" aria-label={`Change name or email of ${u.displayName}`} disabled={busy === u.id} onClick={() => setEditing(u)}><PencilSimple size={18} aria-hidden />Edit</button>
                <button className="btn" disabled={busy === u.id} onClick={() => void reset(u)}><KeyReturn size={18} aria-hidden />New password</button>
                {!self && (u.isActive
                  ? <button className="btn" disabled={busy === u.id} onClick={() => setToggle(u)}><UserCircleMinus size={18} aria-hidden />Stop access</button>
                  : <button className="btn" disabled={busy === u.id} onClick={() => void update(u, { isActive: true })}>Allow again</button>)}
                {!self && <button className="btn" disabled={busy === u.id} onClick={() => void update(u, { roles: [isAdminAccount(u) ? USER : ADMIN] })}>{isAdminAccount(u) ? 'Make a user' : 'Make administrator'}</button>}
                {!self && <button className="btn danger" aria-label={`Delete ${u.displayName}`} disabled={busy === u.id} onClick={() => setRemoving(u)}><Trash size={18} aria-hidden />Delete</button>}
              </div>
            </li>
          )
        })}
      </ul>
      <Pager total={view.length} page={page} size={size} onPage={setPage} onSize={setSize} />

      {adding && <AddUser existing={all} onClose={() => setAdding(false)} onCreated={(email, password) => { setAdding(false); setQuery(email); resetRole(); resetStatus(); setPage(0); setShown({ title: 'User added', email, password }) }} />}
      {editing && <EditUser user={editing} self={editing.id === me?.id} existing={all} onClose={() => setEditing(null)}
        onSaved={emailChanged => { const self = editing.id === me?.id; setEditing(null); toast(self ? 'Saved. Reload the page to see your new name in the corner.' : emailChanged ? 'Saved. They must sign in with the new email.' : 'Saved.', 'good') }} />}
      {removing && <ConfirmDialog title={`Delete ${removing.displayName}?`} okText="Delete user" danger onClose={() => setRemoving(null)}
        message={`${removing.displayName} (${removing.email}) is signed out now, cannot sign in again, and leaves the users list and every client. Their notes and documents stay with their name on them, and projects they started stay. Nothing is erased from the database. Only an administrator can do this.`}
        onConfirm={() => { const u = removing; setRemoving(null); void remove(u) }} />}
      {shown && <Credentials {...shown} onClose={() => setShown(null)} />}
      {toggle && <ConfirmDialog title={`Stop access for ${toggle.displayName}?`} okText="Stop access" danger onClose={() => setToggle(null)}
        message="They are signed out now and cannot sign in again until you allow them. Their projects and documents stay." onConfirm={() => { const u = toggle; setToggle(null); void update(u, { isActive: false }) }} />}
    </>
  )
}
