import { useMemo, useState } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { ArrowRight, Buildings, ChartBar, Clock, FilePdf, FolderSimple, MagnifyingGlass, Microphone, Plus, Sparkle, Star, Trash, TreeStructure } from '@phosphor-icons/react'
import { describeError } from '../api/client'
import { useAiUsage, useDeleteProject, useFlows, useLibrary, useProjects } from '../api/hooks'
import type { Flow, Project } from '../api/types'
import { ErrorState } from '../components/ErrorState'
import { Avatar, ConfirmDialog, Pill, Skeletons, useToast } from '../components/ui'
import { usePref, useRecentProjects } from '../ux/prefs'
import { ago, compact, statusText } from '../lib/format'
import { useSession } from '../state/session'

/** Where a project is, in one phrase and one colour, and how far along it is (0 to 3 steps done). */
export function projectProgress(flow: Flow | undefined, hasDocument = false): { text: string; tone: 'good' | 'progress' | 'warn' | ''; steps: number } {
  if (!flow) return { text: 'Getting ready', tone: '', steps: 0 }
  if (flow.currentVersionNo === 0) return flow.entryCount === 0 ? { text: 'Add your notes', tone: 'warn', steps: 0 } : { text: 'Ready to analyse', tone: 'progress', steps: 1 }
  if (hasDocument) return { text: 'Document ready', tone: 'good', steps: 3 }
  if (flow.openQuestionCount > 0) return { text: `${flow.openQuestionCount} question${flow.openQuestionCount === 1 ? '' : 's'} to answer`, tone: 'warn', steps: 2 }
  return { text: flow.status === 'Analysed' ? 'Workflow ready' : statusText(flow.status), tone: 'good', steps: 2 }
}

/** Asks first, then deletes: the project leaves every list. */
export function DeleteProjectDialog({ project, onClose, onDone }: { project: { id: string; hospitalName: string }; onClose: () => void; onDone?: () => void }) {
  const remove = useDeleteProject()
  const toast = useToast()
  return (
    <ConfirmDialog
      title={`Delete ${project.hospitalName}?`}
      message="The project and all its requests, notes and documents disappear from every list and nobody can open them. The AI usage already recorded stays. Nothing is erased from the database, so an administrator can bring it back."
      okText="Delete project" danger onClose={onClose}
      onConfirm={() => { remove.mutate(project.id, { onSuccess: () => { toast('The project was deleted.', 'good'); onClose(); onDone?.() }, onError: e => { toast(describeError(e), 'error'); onClose() } }) }} />
  )
}
export function ProjectCard({ project, pinned, onPin }: { project: Project; pinned?: boolean; onPin?: (id: string) => void }) {
  const { isAdmin, isLeadIn } = useSession()
  const [asking, setAsking] = useState(false)
  const canDelete = isAdmin || isLeadIn(project.id)
  const flows = useFlows(project.id)
  const library = useLibrary()
  const flow = flows.data?.[0]
  const hasDocument = !!library.data?.some(d => d.projectId === project.id && d.kind === 'Guide')
  const p = projectProgress(flow, hasDocument)
  return (
    <div className="pcard-wrap">
      <Link to={`/p/${project.id}`} className="pcard" aria-label={`Open ${project.hospitalName}`}>
        <div className="pcard-top"><Avatar name={project.hospitalName} size="lg" /><Pill tone={p.tone}>{p.text}</Pill></div>
        {project.clientName && <div className="muted small pcard-client">{project.clientName}</div>}
        <h3>{project.hospitalName}</h3>
        <div className="segs" aria-hidden>{[0, 1, 2].map(i => <i key={i} className={i < p.steps ? 'on' : ''} />)}</div>
        <div className="muted small">{flow ? `${flow.entryCount} note${flow.entryCount === 1 ? '' : 's'} · updated ${ago(flow.lastActivityAt)}` : project.leadName}</div>
        <span className="pcard-go" aria-hidden>Open<ArrowRight size={16} weight="bold" /></span>
      </Link>
      {onPin && <button className="pin pcard-pin" aria-pressed={!!pinned} aria-label={pinned ? `Unpin ${project.hospitalName}` : `Pin ${project.hospitalName}`} title={pinned ? 'Unpin this project' : 'Pin this project to the top'} onClick={() => onPin(project.id)}><Star size={18} weight={pinned ? 'fill' : 'regular'} aria-hidden /></button>}
      {canDelete && <button className="pcard-del" aria-label={`Delete ${project.hospitalName}`} title="Delete this project" onClick={() => setAsking(true)}><Trash size={18} aria-hidden /></button>}
      {asking && <DeleteProjectDialog project={project} onClose={() => setAsking(false)} />}
    </div>
  )
}

function FirstTime() {
  return (
    <section className="hero" aria-label="How it works">
      <h2>Make your first project</h2>
      <p>Turn what a department tells you into a clear workflow and a document that explains it to the developers. It takes three steps.</p>
      <ol className="how">
        <li><span className="how-icon"><Microphone size={26} weight="duotone" aria-hidden /></span><b>Add notes</b><span>Record a conversation, photograph handwritten notes, or type.</span></li>
        <li><span className="how-icon"><Sparkle size={26} weight="duotone" aria-hidden /></span><b>Let the AI analyse</b><span>It structures the notes and asks about anything missing.</span></li>
        <li><span className="how-icon"><TreeStructure size={26} weight="duotone" aria-hidden /></span><b>Get the workflow</b><span>A diagram and an implementation document as a PDF.</span></li>
      </ol>
      <Link to="/new" className="btn primary lg"><Plus size={20} weight="bold" aria-hidden />New project</Link>
    </section>
  )
}

type Filter = 'all' | 'progress' | 'ready' | 'pinned'
type Sort = 'recent' | 'name' | 'new'
const FILTERS: [Filter, string][] = [['all', 'All'], ['progress', 'In progress'], ['ready', 'Document ready'], ['pinned', 'Pinned']]

export function Home() {
  const { user, isAdmin } = useSession()
  const projects = useProjects()
  const library = useLibrary()
  const usage = useAiUsage()
  const [query, setQuery] = useState('')
  const [filter, setFilter, resetFilter] = usePref<Filter>('home.filter', 'all')
  const [sort, setSort] = usePref<Sort>('home.sort', 'recent')
  const [pins, setPins] = usePref<string[]>('pins', [])
  const { recent } = useRecentProjects()
  const mine = projects.data ?? []
  const withDocument = useMemo(() => new Set((library.data ?? []).filter(d => d.kind === 'Guide').map(d => d.projectId)), [library.data])
  const togglePin = (id: string) => setPins(list => (list.includes(id) ? list.filter(x => x !== id) : [id, ...list]))
  const many = mine.length >= 5                                       // a few projects need no search

  const shown = useMemo(() => {
    const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
    let list = mine.filter(p => words.every(w => `${p.hospitalName} ${p.clientName ?? ''} ${p.leadName} ${p.code}`.toLowerCase().includes(w)))
    if (filter === 'ready') list = list.filter(p => withDocument.has(p.id))
    else if (filter === 'progress') list = list.filter(p => !withDocument.has(p.id))
    else if (filter === 'pinned') list = list.filter(p => pins.includes(p.id))
    if (sort === 'name') list = [...list].sort((a, b) => a.hospitalName.localeCompare(b.hospitalName, undefined, { sensitivity: 'base' }))
    else if (sort === 'new') list = [...list].sort((a, b) => b.startDate.localeCompare(a.startDate))
    return list
  }, [mine, query, filter, sort, pins, withDocument])
  const count = (f: Filter) => (f === 'all' ? mine.length : f === 'ready' ? mine.filter(p => withDocument.has(p.id)).length : f === 'progress' ? mine.filter(p => !withDocument.has(p.id)).length : mine.filter(p => pins.includes(p.id)).length)
  const filtered = query.trim() !== '' || filter !== 'all' || sort !== 'recent'

  if (isAdmin) return <Navigate to="/admin" replace />
  const first = user?.displayName.split(' ')[0] ?? ''
  const pinned = filter === 'pinned' ? [] : shown.filter(p => pins.includes(p.id))
  const rest = filter === 'pinned' ? shown : shown.filter(p => !pins.includes(p.id))
  // when somebody works for several clients, their projects are shown client by client
  const groups = new Map<string, Project[]>()
  for (const p of rest) { const k = p.clientName ?? 'Other projects'; groups.set(k, [...(groups.get(k) ?? []), p]) }
  const byClient = [...groups].sort((a, b) => a[0].localeCompare(b[0]))
  const card = (p: Project) => <ProjectCard key={p.id} project={p} pinned={pins.includes(p.id)} onPin={togglePin} />
  const lastOpened = recent.filter(r => mine.some(p => p.id === r.id)).slice(0, 3)

  return (
    <>
      <div className="page-title">
        <div><h1>Hello, {first}</h1><p className="muted">Your projects, and the documents everyone has made.</p></div>
        <Link to="/new" className="btn primary lg"><Plus size={20} weight="bold" aria-hidden />New project</Link>
      </div>

      {projects.isPending && <Skeletons count={2} />}
      {projects.isError && <ErrorState error={projects.error} onRetry={() => void projects.refetch()} retrying={projects.isFetching} />}
      {projects.data && mine.length > 0 && (
        <div className="strip" aria-label="At a glance">
          <div className="strip-item"><FolderSimple size={22} weight="duotone" aria-hidden /><div><b>{mine.length}</b><span>project{mine.length === 1 ? '' : 's'}</span></div></div>
          <Link to="/documents" className="strip-item"><FilePdf size={22} weight="duotone" aria-hidden /><div><b>{library.data?.length ?? 0}</b><span>documents in the library</span></div></Link>
          <Link to="/usage" className="strip-item"><ChartBar size={22} weight="duotone" aria-hidden /><div><b>{usage.data ? compact(usage.data.totals.totalTokens) : '-'}</b><span>AI tokens this month</span></div></Link>
        </div>
      )}
      {projects.data && mine.length === 0 && <FirstTime />}

      {many && lastOpened.length > 0 && (
        <div className="continue" aria-label="Continue where you left off">
          <span className="muted small"><Clock size={14} aria-hidden /> Continue</span>
          {lastOpened.map(r => <Link key={r.id} to={`/p/${r.id}`} className="chip">{r.name}</Link>)}
        </div>
      )}

      {mine.length > 0 && (
        <>
          <h2 className="section-title">Your projects</h2>
          {many && (
            <div className="listbar" role="search" aria-label="Find a project">
              <label className="search"><MagnifyingGlass size={18} aria-hidden /><span className="sr-only">Search your projects</span>
                <input className="input" type="search" placeholder="Search by name, client or person" value={query} onChange={e => setQuery(e.target.value)} /></label>
              <div className="chips" role="group" aria-label="Show">
                {FILTERS.map(([f, label]) => <button key={f} className="chip" aria-pressed={filter === f} onClick={() => setFilter(f)}>{label}<span className="n">{count(f)}</span></button>)}
              </div>
              <label className="field-inline"><span>Sort</span>
                <select className="input" value={sort} onChange={e => setSort(e.target.value as Sort)} aria-label="Sort projects">
                  <option value="recent">Recently active</option><option value="name">Name A to Z</option><option value="new">Newest first</option></select></label>
              {filtered && <button className="btn link" onClick={() => { setQuery(''); resetFilter(); setSort('recent') }}>Clear filters</button>}
              <span className="count" role="status">{shown.length} of {mine.length}</span>
            </div>
          )}
          {shown.length === 0 && <div className="empty" role="status"><b>No project matches</b><div>{filter === 'pinned' && !query ? 'Pin a project with its star to keep it here.' : 'Try fewer words, or clear the filters.'}</div></div>}
          {pinned.length > 0 && (
            <section aria-label="Pinned projects">
              <h3 className="client-title"><Star size={18} weight="fill" aria-hidden />Pinned</h3>
              <div className="grid">{pinned.map(card)}</div>
            </section>
          )}
          {byClient.length > 1
            ? byClient.map(([client, list]) => (
                <section key={client} aria-label={`Projects of ${client}`}>
                  <h3 className="client-title"><Buildings size={18} aria-hidden />{client}<span className="muted small"> · {list.length} project{list.length === 1 ? '' : 's'}</span></h3>
                  <div className="grid">{list.map(card)}</div>
                </section>
              ))
            : rest.length > 0 && <div className="grid">{rest.map(card)}</div>}
        </>
      )}

      {(library.data?.length ?? 0) > 0 && (
        <>
          <div className="section-row"><h2 className="section-title">Latest documents</h2><Link to="/documents" className="btn text">See all<ArrowRight size={16} aria-hidden /></Link></div>
          <div className="docs-strip">
            {library.data!.slice(0, 3).map(d => (
              <Link key={d.id} to="/documents" className="dstrip"><FilePdf size={26} weight="duotone" aria-hidden /><div><b>{d.title}</b><div className="muted small">{d.projectName} · {d.createdByName}</div></div></Link>
            ))}
          </div>
        </>
      )}
    </>
  )
}