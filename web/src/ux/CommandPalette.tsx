import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { ArrowClockwise, Buildings, ChartBar, Clock, Database, FilePdf, FolderSimple, House, Keyboard, MagnifyingGlass, Moon, PlusCircle, Robot, Sidebar, Stack, SquaresFour, TreeStructure, User, Users } from '@phosphor-icons/react'
import { useClients, useLibrary, useProjects, useUsers } from '../api/hooks'
import { useWindowBehaviour } from '../components/ui'
import { useSession } from '../state/session'
import { useRecentProjects } from './prefs'

interface Command { id: string; group: string; label: string; hint?: string; icon: ReactNode; run: () => void }

const GROUPS = ['Recent', 'Actions', 'Pages', 'Projects', 'Documents', 'People', 'Clients']

/** Every word typed must be found in the name or its hint; names that start with the word come first. */
export function matches(query: string, c: { label: string; hint?: string }): number {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return 1
  const label = c.label.toLowerCase(), hay = `${label} ${(c.hint ?? '').toLowerCase()}`
  if (!words.every(w => hay.includes(w))) return 0
  return 1 + words.reduce((n, w) => n + (label.startsWith(w) ? 3 : label.includes(w) ? 2 : 0), 0)
}

/**
 * Ctrl+K from anywhere: type to jump to a page, a project, a document, a person or a client, or to run a common action. Everything listed is real: pages the
 * person may open, and the projects, documents, users and clients the app already knows. With nothing typed it offers the projects opened last.
 */
export function CommandPalette({ onClose, actions }: { onClose: () => void; actions: { help: () => void; theme: () => void; rail: () => void } }) {
  const { isAdmin } = useSession()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const projects = useProjects(!isAdmin)
  const library = useLibrary()
  const users = useUsers(isAdmin)
  const clients = useClients(isAdmin)
  const { recent } = useRecentProjects()
  const [query, setQuery] = useState('')
  const [at, setAt] = useState(0)
  const ref = useRef<HTMLDivElement>(null)
  const listId = useId()
  useWindowBehaviour(ref, onClose)

  const go = (to: string) => () => navigate(to)
  const all = useMemo<Command[]>(() => {
    const list: Command[] = []
    const icon = (n: ReactNode) => n
    if (!isAdmin) recent.slice(0, 4).forEach(r => list.push({ id: `recent-${r.id}`, group: 'Recent', label: r.name, hint: 'Opened recently', icon: icon(<Clock size={18} />), run: go(`/p/${r.id}`) }))
    if (!isAdmin) list.push({ id: 'a-new', group: 'Actions', label: 'New project', hint: 'Start capturing a department', icon: <PlusCircle size={18} />, run: go('/new') })
    list.push(
      { id: 'a-theme', group: 'Actions', label: 'Switch light and dark', hint: 'Appearance', icon: <Moon size={18} />, run: actions.theme },
      { id: 'a-rail', group: 'Actions', label: 'Collapse or expand the menu', hint: 'Sidebar', icon: <Sidebar size={18} />, run: actions.rail },
      { id: 'a-refresh', group: 'Actions', label: 'Refresh all data', hint: 'Load everything again from the server', icon: <ArrowClockwise size={18} />, run: () => { void qc.invalidateQueries() } },
      { id: 'a-help', group: 'Actions', label: 'Keyboard shortcuts', hint: 'Help', icon: <Keyboard size={18} />, run: actions.help },
    )
    const pages: [string, string, ReactNode][] = isAdmin
      ? [['/admin', 'Overview', <SquaresFour size={18} />], ['/documents', 'Final documents', <FilePdf size={18} />], ['/admin/handling', 'Who handles what', <TreeStructure size={18} />], ['/admin/clients', 'Clients', <Buildings size={18} />],
         ['/admin/modules', 'Modules', <Stack size={18} />], ['/admin/users', 'Users', <Users size={18} />], ['/admin/ai', 'AI setup', <Robot size={18} />], ['/admin/database', 'Database', <Database size={18} />], ['/usage', 'AI usage', <ChartBar size={18} />]]
      : [['/', 'Projects', <House size={18} />], ['/documents', 'Documents', <FilePdf size={18} />], ['/usage', 'AI usage', <ChartBar size={18} />]]
    pages.forEach(([to, label, ic]) => list.push({ id: `p-${to}`, group: 'Pages', label, hint: 'Go to', icon: ic, run: go(to) }))
    if (!isAdmin) (projects.data ?? []).forEach(p => list.push({ id: `pr-${p.id}`, group: 'Projects', label: p.hospitalName, hint: p.clientName ?? p.leadName, icon: <FolderSimple size={18} />, run: go(`/p/${p.id}`) }))
    ;(library.data ?? []).forEach(d => list.push({ id: `d-${d.id}`, group: 'Documents', label: d.title, hint: `${d.docNumber} · ${d.clientName ?? d.projectName}`, icon: <FilePdf size={18} />, run: go(`/documents?q=${encodeURIComponent(d.docNumber)}`) }))
    if (isAdmin) {
      ;(users.data ?? []).forEach(u => list.push({ id: `u-${u.id}`, group: 'People', label: u.displayName, hint: u.email, icon: <User size={18} />, run: go(`/admin/users?q=${encodeURIComponent(u.email)}`) }))
      ;(clients.data ?? []).forEach(c => list.push({ id: `c-${c.id}`, group: 'Clients', label: c.name, hint: c.location || `${c.projectCount} projects`, icon: <Buildings size={18} />, run: go(`/admin/clients?q=${encodeURIComponent(c.name)}`) }))
    }
    return list
  }, [isAdmin, recent, projects.data, library.data, users.data, clients.data])   // eslint-disable-line react-hooks/exhaustive-deps

  const results = useMemo(() => {
    const searching = query.trim() !== ''
    const scored = all.map(c => ({ c, s: matches(query, c) })).filter(x => x.s > 0 && (searching ? x.c.group !== 'Recent' : ['Recent', 'Actions', 'Pages'].includes(x.c.group)))
    const out: Command[] = []
    for (const g of GROUPS) out.push(...scored.filter(x => x.c.group === g.toString()).sort((a, b) => b.s - a.s || a.c.label.localeCompare(b.c.label)).slice(0, searching ? 6 : 8).map(x => x.c))
    return out
  }, [all, query])

  useEffect(() => { setAt(0) }, [query])
  useEffect(() => { document.getElementById(`${listId}-${at}`)?.scrollIntoView?.({ block: 'nearest' }) }, [at, listId])
  const run = (c: Command | undefined) => { if (!c) return; onClose(); c.run() }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setAt(i => (results.length ? (i + 1) % results.length : 0)) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setAt(i => (results.length ? (i - 1 + results.length) % results.length : 0)) }
    else if (e.key === 'Enter') { e.preventDefault(); run(results[at]) }
  }

  let lastGroup = ''
  return createPortal(
    <div className="palette-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="palette" role="dialog" aria-modal="true" aria-label="Search and commands" ref={ref}>
        <div className="palette-input">
          <MagnifyingGlass size={20} aria-hidden />
          <input role="combobox" aria-expanded="true" aria-controls={listId} aria-activedescendant={results.length ? `${listId}-${at}` : undefined} aria-autocomplete="list" aria-label="Search"
            placeholder={isAdmin ? 'Search pages, documents, users, clients…' : 'Search projects, documents, pages…'} value={query} onChange={e => setQuery(e.target.value)} onKeyDown={onKey} autoComplete="off" spellCheck={false} />
          <kbd>Esc</kbd>
        </div>
        <ul className="palette-list" role="listbox" id={listId} aria-label="Results">
          {results.map((c, i) => {
            const heading = c.group !== lastGroup ? c.group : null
            lastGroup = c.group
            return (
              <li key={c.id} role="presentation">
                {heading && <div className="palette-group" aria-hidden="true">{heading}</div>}
                <div id={`${listId}-${i}`} role="option" aria-selected={i === at} className={`palette-item ${i === at ? 'on' : ''}`} onMouseMove={() => setAt(i)} onClick={() => run(c)}>
                  <span className="palette-icon" aria-hidden>{c.icon}</span>
                  <span className="palette-label">{c.label}</span>
                  {c.hint && <span className="palette-hint">{c.hint}</span>}
                </div>
              </li>
            )
          })}
        </ul>
        {results.length === 0 && <div className="palette-empty" role="status">{query.trim() ? `Nothing matches “${query}”.` : 'Type to search.'}</div>}
        <div className="palette-foot" aria-hidden="true"><span><kbd>↑</kbd><kbd>↓</kbd> move</span><span><kbd>↵</kbd> open</span><span><kbd>Esc</kbd> close</span></div>
      </div>
    </div>,
    document.body,
  )
}
