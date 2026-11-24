import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowClockwise, Buildings, DownloadSimple, MagnifyingGlass, Printer, Users } from '@phosphor-icons/react'
import { useHandling } from '../../api/hooks'
import type { HandlingRow } from '../../api/types'
import { ErrorState } from '../../components/ErrorState'
import { Avatar, Pill, Skeletons } from '../../components/ui'
import { formatWhen } from '../../lib/format'
import { downloadCsv, stamp } from '../../ux/csv'
import { usePref } from '../../ux/prefs'

type View = 'client' | 'user'

/** The final documents of a row, in a few words. */
function Docs({ r }: { r: Pick<HandlingRow, 'awaitingVerification' | 'verified' | 'returned'> }) {
  if (r.awaitingVerification + r.verified + r.returned === 0) return <span className="muted small">No final documents yet</span>
  return (
    <span className="docs-count">
      {r.awaitingVerification > 0 && <Pill tone="progress">{r.awaitingVerification} waiting</Pill>}
      {r.verified > 0 && <Pill tone="good">{r.verified} verified</Pill>}
      {r.returned > 0 && <Pill tone="warn">{r.returned} returned</Pill>}
    </span>
  )
}

const sum = (rows: HandlingRow[], pick: (r: HandlingRow) => number) => rows.reduce((n, r) => n + pick(r), 0)

/**
 * Who handles which client, for the administrator. Every client is listed with the people on it, and every person with the clients they handle, with what
 * each has done: projects, requests and final documents. Filter by client or by person, or switch between the two views.
 */
export function HandlingPage() {
  const handling = useHandling()
  const [view, setView] = usePref<View>('handling.view', 'client')
  const [savedClient, setClient, resetClient] = usePref<string>('handling.client', '')
  const [savedPerson, setPerson, resetPerson] = usePref<string>('handling.person', '')
  const [search, setSearch] = useState('')

  const rows = handling.data ?? []
  const clients = useMemo(() => [...new Map(rows.filter(r => r.clientId).map(r => [r.clientId!, r.clientName!])).entries()].sort((a, b) => a[1].localeCompare(b[1])), [rows])
  const people = useMemo(() => [...new Map(rows.filter(r => r.userId).map(r => [r.userId!, r.userName!])).entries()].sort((a, b) => a[1].localeCompare(b[1])), [rows])
  // a remembered client or person who is no longer on the list is not applied
  const client = clients.some(([id]) => id === savedClient) ? savedClient : ''
  const person = people.some(([id]) => id === savedPerson) ? savedPerson : ''
  const shown = useMemo(() => {
    const q = search.trim().toLowerCase()
    return rows.filter(r => (!client || r.clientId === client) && (!person || r.userId === person)
      && (!q || [r.clientName ?? '', r.userName ?? '', r.userEmail ?? ''].some(x => x.toLowerCase().includes(q))))
  }, [rows, client, person, search])
  const filtered = !!client || !!person || !!search.trim()
  const exportCsv = () => downloadCsv(`who-handles-what-${stamp()}`, [
    ['Client', 'Client access', 'User', 'Email', 'User access', 'Projects of the client', 'Projects started', 'Modules', 'Requests', 'Waiting', 'Verified', 'Returned', 'Last final document'],
    ...shown.map(r => [r.clientName ?? '(no client)', r.clientId ? (r.clientActive ? 'Active' : 'Stopped') : '', r.userName ?? '(nobody)', r.userEmail ?? '', r.userId ? (r.userActive ? 'Active' : 'Stopped') : '',
      r.projects, r.projectsStarted, r.modules, r.requests, r.awaitingVerification, r.verified, r.returned, r.lastFinalAt ?? '']),
  ])

  const unassignedClients = rows.filter(r => r.clientId && !r.userId).length
  const idlePeople = rows.filter(r => r.userId && !r.clientId).length

  // by client: the people on each; by person: the clients each handles
  const byClient = useMemo(() => {
    const m = new Map<string, HandlingRow[]>()
    for (const r of shown.filter(r => r.clientId)) m.set(r.clientId!, [...(m.get(r.clientId!) ?? []), r])
    return [...m.values()]
  }, [shown])
  const byPerson = useMemo(() => {
    const m = new Map<string, HandlingRow[]>()
    for (const r of shown.filter(r => r.userId)) m.set(r.userId!, [...(m.get(r.userId!) ?? []), r])
    return [...m.values()].sort((a, b) => a[0].userName!.localeCompare(b[0].userName!))
  }, [shown])

  return (
    <>
      <div className="page-title">
        <div><h1>Who handles what</h1><p className="muted">Which user handles which client, and what each has done. To change who handles a client, open the client.</p></div>
        <div className="row">
          <button className="btn" onClick={() => void handling.refetch()} disabled={handling.isFetching} aria-label="Refresh the list"><ArrowClockwise size={16} aria-hidden />{handling.isFetching ? 'Refreshing…' : 'Refresh'}</button>
          <button className="btn" onClick={exportCsv} disabled={shown.length === 0}><DownloadSimple size={16} aria-hidden />Export CSV</button>
          <button className="btn" onClick={() => window.print()}><Printer size={16} aria-hidden />Print</button>
          <Link to="/admin/clients" className="btn"><Buildings size={18} aria-hidden />Clients</Link>
          <Link to="/admin/users" className="btn"><Users size={18} aria-hidden />Users</Link>
        </div>
      </div>

      {handling.isPending && <Skeletons count={3} />}
      {handling.isError && <ErrorState error={handling.error} onRetry={() => void handling.refetch()} retrying={handling.isFetching} />}

      {handling.data && (
        <>
          <div className="stats4" aria-label="Totals">
            <div className="stat"><Buildings size={22} aria-hidden /><div className="n">{clients.length}</div><div className="l">clients</div></div>
            <div className="stat"><Users size={22} aria-hidden /><div className="n">{people.length}</div><div className="l">users</div></div>
            <div className={`stat ${unassignedClients ? 'attn' : ''}`}><div className="n">{unassignedClients}</div><div className="l">clients nobody handles</div></div>
            <div className={`stat ${idlePeople ? 'attn' : ''}`}><div className="n">{idlePeople}</div><div className="l">users on no client</div></div>
          </div>

          <div className="filter-bar" role="search" aria-label="Filter who handles what">
            <div className="seg" role="tablist" aria-label="View">
              <button role="tab" aria-selected={view === 'client'} className={view === 'client' ? 'on' : ''} onClick={() => setView('client')}>By client</button>
              <button role="tab" aria-selected={view === 'user'} className={view === 'user' ? 'on' : ''} onClick={() => setView('user')}>By user</button>
            </div>
            <label className="field-inline"><span>Client</span>
              <select className="input" value={client} onChange={e => setClient(e.target.value)}><option value="">All clients</option>{clients.map(([id, name]) => <option key={id} value={id}>{name}</option>)}</select></label>
            <label className="field-inline"><span>User</span>
              <select className="input" value={person} onChange={e => setPerson(e.target.value)}><option value="">All users</option>{people.map(([id, name]) => <option key={id} value={id}>{name}</option>)}</select></label>
            <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search clients and users</span>
              <input className="input" type="search" placeholder="Search by client or user" value={search} onChange={e => setSearch(e.target.value)} /></label>
            {filtered && <button className="btn link" onClick={() => { resetClient(); resetPerson(); setSearch('') }}>Clear filters</button>}
          </div>

          {rows.length === 0 && <div className="empty" role="status"><b>Nothing to show yet</b><div>Make a client and assign users to it. They appear here.</div></div>}

          {view === 'client' && (
            <ul className="handle-list" aria-label="Clients and their people">
              {byClient.map(list => {
                const c = list[0]
                const people = list.filter(r => r.userId)
                return (
                  <li key={c.clientId} className="handle-card">
                    <div className="handle-head">
                      <h3>{c.clientName}</h3>{!c.clientActive && <Pill tone="warn">Stopped</Pill>}
                      <span className="muted small">{c.projects} project{c.projects === 1 ? '' : 's'} · {c.modules} module{c.modules === 1 ? '' : 's'} · {c.requests} request{c.requests === 1 ? '' : 's'}</span>
                      <Docs r={{ awaitingVerification: sum(people, r => r.awaitingVerification), verified: sum(people, r => r.verified), returned: sum(people, r => r.returned) }} />
                    </div>
                    {people.length === 0
                      ? <div className="handle-none">Nobody handles this client yet. <Link to="/admin/clients">Assign people</Link></div>
                      : <ul className="handle-people" aria-label={`People on ${c.clientName}`}>
                        {people.map(r => (
                          <li key={r.userId}>
                            <Avatar name={r.userName ?? '?'} size="sm" />
                            <span className="who"><b>{r.userName}</b>{!r.userActive && <> <Pill tone="warn">Access stopped</Pill></>}<br /><span className="muted small">{r.userEmail}</span></span>
                            <span className="muted small">started {r.projectsStarted} project{r.projectsStarted === 1 ? '' : 's'}</span>
                            <Docs r={r} />
                            {r.lastFinalAt && <span className="muted small">last final {formatWhen(r.lastFinalAt)}</span>}
                          </li>
                        ))}
                      </ul>}
                  </li>
                )
              })}
              {byClient.length === 0 && rows.length > 0 && <li className="empty" role="status"><b>No client matches</b></li>}
            </ul>
          )}

          {view === 'user' && (
            <ul className="handle-list" aria-label="Users and their clients">
              {byPerson.map(list => {
                const u = list[0]
                const mine = list.filter(r => r.clientId)
                return (
                  <li key={u.userId} className="handle-card">
                    <div className="handle-head">
                      <Avatar name={u.userName ?? '?'} size="sm" /><h3>{u.userName}</h3>{!u.userActive && <Pill tone="warn">Access stopped</Pill>}
                      <span className="muted small">{u.userEmail}</span>
                      <Docs r={{ awaitingVerification: sum(mine, r => r.awaitingVerification), verified: sum(mine, r => r.verified), returned: sum(mine, r => r.returned) }} />
                    </div>
                    {mine.length === 0
                      ? <div className="handle-none">Not on any client yet. <Link to="/admin/clients">Assign to a client</Link></div>
                      : <ul className="handle-people" aria-label={`Clients of ${u.userName}`}>
                        {mine.map(r => (
                          <li key={r.clientId}>
                            <Buildings size={20} aria-hidden />
                            <span className="who"><b>{r.clientName}</b>{!r.clientActive && <> <Pill tone="warn">Stopped</Pill></>}</span>
                            <span className="muted small">{r.projects} project{r.projects === 1 ? '' : 's'} · started {r.projectsStarted}</span>
                            <Docs r={r} />
                            {r.lastFinalAt && <span className="muted small">last final {formatWhen(r.lastFinalAt)}</span>}
                          </li>
                        ))}
                      </ul>}
                  </li>
                )
              })}
              {byPerson.length === 0 && rows.length > 0 && <li className="empty" role="status"><b>No user matches</b></li>}
            </ul>
          )}
        </>
      )}
    </>
  )
}
