import { Fragment, useEffect, useRef, useState } from 'react'
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { Buildings, CaretDoubleLeft, CaretDoubleRight, ChartBar, CloudCheck, Database, LockKey, CloudSlash, FilePdf, House, MagnifyingGlass, PlusCircle, Robot, SignOut, Stack, Users, SquaresFour, Sun, Moon, Desktop, TreeStructure } from '@phosphor-icons/react'
import { useAdminOverview, useSystemInfo } from '../api/hooks'
import { renewsIn } from '../pages/AiUsage'
import { applyTheme, getTheme, type ThemeChoice } from '../lib/theme'
import { clearOutbox, flushOutbox, queuedCount, queuedEntries, subscribeOutbox, type QueuedEntry } from '../offline/outbox'
import { useSession } from '../state/session'
import { ChangePasswordDialog } from './ChangePassword'
import { CommandPalette } from '../ux/CommandPalette'
import { NotificationBell } from '../ux/Notifications'
import { ShortcutsDialog, useGlobalShortcuts } from '../ux/shortcuts'
import { usePref } from '../ux/prefs'
import { Avatar, ConfirmDialog } from './ui'

export function SyncBadge() {
  const [online, setOnline] = useState(navigator.onLine)
  const [queue, setQueue] = useState<QueuedEntry[]>([])
  useEffect(() => {
    const on = () => setOnline(true), off = () => setOnline(false)
    window.addEventListener('online', on); window.addEventListener('offline', off)
    const refresh = () => { void queuedEntries().then(setQueue).catch(() => setQueue([])) }
    refresh()
    const unsub = subscribeOutbox(refresh)
    return () => { window.removeEventListener('online', on); window.removeEventListener('offline', off); unsub() }
  }, [])
  const waiting = queue.filter(q => !q.rejected).length
  const problems = queue.filter(q => q.rejected).length
  // nothing to say while everything is fine and the connection is good
  if (online && waiting === 0 && problems === 0) return null
  const [text, tone] =
    problems > 0 ? [`${problems} note${problems === 1 ? '' : 's'} need${problems === 1 ? 's' : ''} attention`, 'is-bad']
    : !online ? [waiting > 0 ? `Offline · ${waiting} waiting` : 'Offline', 'is-warn']
    : [`Sending ${waiting} note${waiting === 1 ? '' : 's'}…`, 'is-idle']
  return (
    <button className={`sync-pill ${tone}`} onClick={() => void flushOutbox()} aria-label={`Sync status: ${text}. Tap to send now.`}>
      {online ? <CloudCheck size={16} weight="bold" aria-hidden /> : <CloudSlash size={16} weight="bold" aria-hidden />}
      {text}
    </button>
  )
}

const THEMES: { value: ThemeChoice; label: string; icon: typeof Sun }[] = [
  { value: 'system', label: 'Auto', icon: Desktop }, { value: 'light', label: 'Light', icon: Sun }, { value: 'dark', label: 'Dark', icon: Moon },
]

function AccountMenu() {
  const { user, isAdmin, signOut } = useSession()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [theme, setTheme] = useState<ThemeChoice>(getTheme())
  const [confirm, setConfirm] = useState(0)
  const [changing, setChanging] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return undefined
    const away = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false) }
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', away); document.addEventListener('keydown', esc)
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', esc) }
  }, [open])

  async function leave(force: boolean) {
    if (force) await clearOutbox()
    await signOut()
    navigate('/login', { replace: true })
  }
  async function trySignOut() {
    setOpen(false)
    const waiting = await queuedCount().catch(() => 0)
    if (waiting > 0) setConfirm(waiting); else await leave(false)
  }

  return (
    <div className="account" ref={box}>
      <button className="account-btn" onClick={() => setOpen(o => !o)} aria-haspopup="menu" aria-expanded={open} aria-label={`Account menu for ${user?.displayName ?? 'you'}`}>
        <Avatar name={user?.displayName ?? '?'} />
      </button>
      {open && (
        <div className="menu" role="menu">
          <div className="menu-who"><b>{user?.displayName}</b><span>{user?.email}</span><span className="role-chip">{isAdmin ? 'Administrator' : 'User'}</span></div>
          <div className="menu-label">Appearance</div>
          <div className="seg" role="group" aria-label="Theme">
            {THEMES.map(t => (
              <button key={t.value} className={theme === t.value ? 'on' : ''} aria-pressed={theme === t.value} onClick={() => { setTheme(t.value); applyTheme(t.value) }}><t.icon size={16} aria-hidden />{t.label}</button>
            ))}
          </div>
          <button className="menu-item" role="menuitem" onClick={() => { setOpen(false); setChanging(true) }}><LockKey size={18} aria-hidden />Change password</button>
          <button className="menu-item" role="menuitem" onClick={() => void trySignOut()}><SignOut size={18} aria-hidden />Sign out</button>
        </div>
      )}
      {changing && <ChangePasswordDialog onClose={() => setChanging(false)} />}
      {confirm > 0 && (
        <ConfirmDialog title="Sign out and lose work?" okText="Sign out and delete" danger onClose={() => setConfirm(0)} onConfirm={() => void leave(true)}
          message={`${confirm} note(s) on this device have not reached the server yet. Signing out now deletes them. Wait until the orange badge at the top disappears first if you can.`} />
      )}
    </div>
  )
}

interface NavItem { to: string; label: string; icon: typeof House; end?: boolean; badge?: number; also?: (path: string) => boolean }
interface NavGroup { label: string; items: NavItem[] }

/** Where the person is, as a trail: Projects › New project. The last part is the page itself. */
function Crumbs({ isAdmin }: { isAdmin: boolean }) {
  const { pathname } = useLocation()
  const admin: Record<string, string> = { users: 'Users', clients: 'Clients', modules: 'Modules', handling: 'Who handles what', ai: 'AI setup', database: 'Database' }
  let trail: { label: string; to?: string }[]
  const [first, second] = pathname.split('/').filter(Boolean)
  if (pathname === '/') trail = [{ label: isAdmin ? 'Administration' : 'Projects' }]
  else if (first === 'new') trail = [{ label: 'Projects', to: '/' }, { label: 'New project' }]
  else if (first === 'p') trail = [{ label: 'Projects', to: '/' }, { label: 'Project' }]
  else if (first === 'flow') trail = [{ label: 'Projects', to: '/' }, { label: 'Request' }]
  else if (first === 'documents') trail = isAdmin ? [{ label: 'Administration', to: '/admin' }, { label: 'Final documents' }] : [{ label: 'Documents' }]
  else if (first === 'usage') trail = [{ label: isAdmin ? 'Administration' : 'Account', to: isAdmin ? '/admin' : undefined }, { label: 'AI usage' }]
  else if (first === 'admin') trail = second ? [{ label: 'Administration', to: '/admin' }, { label: admin[second] ?? 'Page' }] : [{ label: 'Administration' }]
  else return null
  return (
    <nav className="crumbs" aria-label="Breadcrumb">
      <ol>
        {trail.map((c, i) => (
          <li key={c.label + i}>{c.to && i < trail.length - 1 ? <Link to={c.to}>{c.label}</Link> : <span aria-current={i === trail.length - 1 ? 'page' : undefined}>{c.label}</span>}</li>
        ))}
      </ol>
    </nav>
  )
}

/** True on a tablet-sized screen, where the menu is always the narrow one. */
function useNarrowScreen() {
  const query = '(max-width: 1100px)'
  const [narrow, setNarrow] = useState(() => !!window.matchMedia?.(query).matches)
  useEffect(() => {
    const m = window.matchMedia?.(query)
    if (!m) return undefined
    const on = () => setNarrow(m.matches)
    m.addEventListener?.('change', on)
    return () => m.removeEventListener?.('change', on)
  }, [])
  return narrow
}

export function Shell() {
  const { isAdmin } = useSession()
  const { pathname } = useLocation()
  const navigate = useNavigate()
  const info = useSystemInfo()
  const waiting = useAdminOverview(isAdmin).data?.awaitingVerification ?? 0
  const [collapsed, setCollapsed] = usePref<boolean>('rail-collapsed', false)
  const narrow = useNarrowScreen()
  const compact = collapsed || narrow
  const [searching, setSearching] = useState(false)
  const [helping, setHelping] = useState(false)
  const groups: NavGroup[] = isAdmin
    ? [
      { label: 'Overview', items: [{ to: '/admin', label: 'Overview', icon: SquaresFour, end: true }, { to: '/documents', label: 'Final documents', icon: FilePdf, badge: waiting }, { to: '/admin/handling', label: 'Who handles what', icon: TreeStructure }] },
      { label: 'Manage', items: [{ to: '/admin/clients', label: 'Clients', icon: Buildings }, { to: '/admin/modules', label: 'Modules', icon: Stack }, { to: '/admin/users', label: 'Users', icon: Users }] },
      { label: 'System', items: [{ to: '/admin/ai', label: 'AI setup', icon: Robot }, { to: '/admin/database', label: 'Database', icon: Database }, { to: '/usage', label: 'AI usage', icon: ChartBar }] },
    ]
    : [
      { label: 'Workspace', items: [{ to: '/', label: 'Projects', icon: House, end: true, also: p => p.startsWith('/p/') || p.startsWith('/flow/') }, { to: '/new', label: 'New project', icon: PlusCircle }, { to: '/documents', label: 'Documents', icon: FilePdf }] },
      { label: 'Account', items: [{ to: '/usage', label: 'AI usage', icon: ChartBar }] },
    ]
  const links = groups.flatMap(g => g.items)
  useEffect(() => { window.scrollTo(0, 0) }, [pathname])
  const home = isAdmin ? '/admin' : '/'
  const cycleTheme = () => { const order: ThemeChoice[] = ['system', 'light', 'dark']; applyTheme(order[(order.indexOf(getTheme()) + 1) % order.length]) }
  useGlobalShortcuts({ palette: () => setSearching(true), help: () => setHelping(true), rail: () => setCollapsed(c => !c), go: to => navigate(to) })

  return (
    <div className={`app ${compact ? 'compact' : ''}`}>
      <a className="skip-link" href="#content" onClick={e => { e.preventDefault(); document.getElementById('content')?.focus() }}>Skip to the content</a>
      <aside className="rail">
        <div className="rail-head">
          <NavLink to={home} className="brand" aria-label="bbflowbuild home"><span className="brand-mark" aria-hidden>b</span><span>bbflowbuild</span></NavLink>
        </div>
        <nav className="appnav" aria-label="Main">
          {groups.map(g => (
            <Fragment key={g.label}>
              <div className="nav-group" aria-hidden="true">{g.label}</div>
              {g.items.map(l => (
                <NavLink key={l.to} to={l.to} end={l.end} data-tip={l.label} className={({ isActive }) => `navlink ${isActive || l.also?.(pathname) ? 'on' : ''}`}>
                  <l.icon size={20} aria-hidden /><span className="nav-label">{l.label}</span>{!!l.badge && <span className="nav-badge" aria-label={`${l.badge} waiting`}>{l.badge}</span>}
                </NavLink>
              ))}
            </Fragment>
          ))}
        </nav>
        {!narrow && (
          <button className="rail-toggle" onClick={() => setCollapsed(c => !c)} aria-pressed={collapsed} aria-label={collapsed ? 'Expand the menu' : 'Collapse the menu'} data-tip={collapsed ? 'Expand the menu' : 'Collapse the menu'}>
            {collapsed ? <CaretDoubleRight size={18} aria-hidden /> : <><CaretDoubleLeft size={18} aria-hidden /><span className="nav-label">Collapse</span></>}
          </button>
        )}
      </aside>

      <div className="main-col">
        <header className="appbar">
          <NavLink to={home} className="brand brand-phone" aria-label="bbflowbuild home"><span className="brand-mark" aria-hidden>b</span><span>bbflowbuild</span></NavLink>
          <Crumbs isAdmin={isAdmin} />
          <span className="grow" />
          <button className="searchbtn" onClick={() => setSearching(true)} aria-label="Search" aria-keyshortcuts="Control+K"><MagnifyingGlass size={18} aria-hidden /><span>Search</span><kbd>Ctrl K</kbd></button>
          <SyncBadge />
          <NotificationBell />
          <AccountMenu />
        </header>
        <main className="page2">
          {info.data?.capacity?.reached && <div className="banner is-warn" role="status">{info.data.capacity.message ?? 'The free AI has used its requests for today.'}{isAdmin && <> <NavLink to="/admin/ai">See the limits</NavLink></>}</div>}
          {info.data?.capacity && !info.data.capacity.reached && info.data.capacity.requestsLeft !== null && info.data.capacity.requestsLeft <= 5 && (
            <div className="banner" role="status">Only {info.data.capacity.requestsLeft} free AI request{info.data.capacity.requestsLeft === 1 ? '' : 's'} left today (about as many analyses). It renews in {renewsIn(info.data.capacity.resetsAt)}.</div>
          )}
          {info.data?.notice && <div className={`banner ${info.data.ai === 'no-key' ? 'is-warn' : ''}`} role="status">{info.data.notice}{info.data.ai === 'no-key' && isAdmin && <> <NavLink to="/admin/ai">Set it up</NavLink></>}</div>}
          <div className="wrap" id="content" tabIndex={-1} key={pathname}><Outlet /></div>
        </main>
      </div>

      <nav className="tabbar" aria-label="Main (phone)">
        {links.map(l => <NavLink key={l.to} to={l.to} end={l.end} className={({ isActive }) => `tab2 ${isActive || l.also?.(pathname) ? 'on' : ''}`}><l.icon size={22} aria-hidden /><span>{l.label}</span>{!!l.badge && <span className="nav-badge" aria-label={`${l.badge} waiting`}>{l.badge}</span>}</NavLink>)}
      </nav>

      {searching && <CommandPalette onClose={() => setSearching(false)} actions={{ help: () => setHelping(true), theme: cycleTheme, rail: () => setCollapsed(c => !c) }} />}
      {helping && <ShortcutsDialog onClose={() => setHelping(false)} />}
    </div>
  )
}