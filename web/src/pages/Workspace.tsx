import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ArrowLeft, ArrowRight, Check, Lightning, Trash } from '@phosphor-icons/react'
import { auth, describeError } from '../api/client'
import { useAiUsage, useFlowDocuments, useFlows, useProjects, useRenameFlow, useRenameProject } from '../api/hooks'
import type { Flow } from '../api/types'
import { EditableTitle } from '../components/EditableTitle'
import { GenerateCard } from '../components/GenerateCard'
import { NotesPanel } from '../components/NotesPanel'
import { FigurePanel } from '../components/FigurePanel'
import { ProjectPdfs } from '../components/ProjectPdfs'
import { RequestFolders, requestCode } from '../components/RequestFolders'
import { ResultPanel } from '../components/ResultPanel'
import { Alert, Skeletons } from '../components/ui'
import { ago, compact } from '../lib/format'
import { useSession } from '../state/session'
import { useRecentProjects } from '../ux/prefs'
import { AnalysisTab } from './flow/AnalysisTab'
import { DeleteProjectDialog } from './Home'

/** What to do next, worked out from where the project is. */
export function nextStep(flow: Flow, hasDocument: boolean, canCapture: boolean): { label: string; href: string; hint: string } {
  if (!canCapture) return { label: 'Read the notes', href: '#notes', hint: 'You can read this project but not change it.' }
  if (flow.entryCount === 0) return { label: 'Add your first note', href: '#notes', hint: 'Record a conversation, photograph handwritten notes, or type.' }
  if (flow.currentVersionNo === 0) return { label: 'Run the analysis', href: '#analyse', hint: 'The AI turns the notes into a workflow.' }
  if (flow.openQuestionCount > 0 && !hasDocument)
    return { label: `Answer ${flow.openQuestionCount} question${flow.openQuestionCount === 1 ? '' : 's'}`, href: '#analyse', hint: 'Optional, but the document is better with the answers.' }
  if (!hasDocument) return { label: 'Create the implementation document', href: '#result', hint: 'A PDF that explains the flow to the developers.' }
  return { label: 'Open the implementation document', href: '#result', hint: 'It is ready to read, download and share.' }
}

function Progress({ flow, hasDocument, canCapture, tokens }: { flow: Flow; hasDocument: boolean; canCapture: boolean; tokens: number | null }) {
  const analysed = flow.currentVersionNo > 0
  const items = [
    { label: 'Notes added', detail: flow.entryCount > 0 ? `${flow.entryCount} note${flow.entryCount === 1 ? '' : 's'}` : 'None yet', done: flow.entryCount > 0, href: '#notes' },
    { label: 'Analysed', detail: analysed ? `Version ${flow.currentVersionNo}` : 'Not yet', done: analysed, href: '#analyse' },
    { label: 'Questions answered', detail: !analysed ? '-' : flow.openQuestionCount === 0 ? 'All answered' : `${flow.openQuestionCount} open`, done: analysed && flow.openQuestionCount === 0, href: '#analyse' },
    { label: 'Implementation document', detail: hasDocument ? 'Made' : 'Not yet', done: hasDocument, href: '#result' },
  ]
  const next = nextStep(flow, hasDocument, canCapture)
  const doneCount = items.filter(i => i.done).length
  return (
    <aside className="side" aria-label="Progress and next step">
      <div className="side-card">
        <div className="side-head"><b>Progress</b><span className="muted small">{doneCount} of {items.length}</span></div>
        <div className="side-meter" role="presentation"><i style={{ width: `${(doneCount / items.length) * 100}%` }} /></div>
        <ol className="checks" aria-label="Progress">
          {items.map(i => (
            <li key={i.label} className={i.done ? 'done' : ''}>
              <a href={i.href}>
                <span className="tick" aria-hidden>{i.done ? <Check size={13} weight="bold" /> : null}</span>
                <span className="check-main"><span>{i.label}</span><span className="muted small">{i.detail}</span></span>
              </a>
            </li>
          ))}
        </ol>
        <a className="btn primary lg block next" href={next.href}>{next.label}<ArrowRight size={18} weight="bold" aria-hidden /></a>
        <p className="muted small next-hint">{next.hint}</p>
      </div>
      {tokens !== null && (
        <Link to="/usage" className="side-usage" aria-label={`AI used ${tokens} tokens in this project this month. Open the AI usage page.`}>
          <Lightning size={18} weight="fill" aria-hidden /><span><b>{compact(tokens)} tokens</b><br /><span className="muted small">AI used here this month</span></span><ArrowRight size={14} aria-hidden />
        </Link>
      )}
    </aside>
  )
}

/** The whole job on one page: notes, analysis, and what came out of it, with the next step always in view. */
export function Workspace() {
  const { projectId = '' } = useParams()
  const { canCaptureIn, isLeadIn, isAdmin, roleIn } = useSession()
  const navigate = useNavigate()
  const [deleting, setDeleting] = useState(false)
  const projects = useProjects()
  const flows = useFlows(projectId)
  const [params, setParams] = useSearchParams()
  const project = projects.data?.find(p => p.id === projectId)
  const list = flows.data ?? []
  const flow = list.find(f => f.id === params.get('flow')) ?? list[0]
  const docs = useFlowDocuments(flow?.id)
  const usage = useAiUsage()
  const renameFlow = useRenameFlow(projectId)
  const renameProject = useRenameProject()
  // a project of a client the person was assigned to after signing in is not in their sign-in yet: ask for a fresh one (once)
  const unknownRole = !isAdmin && !!project && roleIn(projectId) === undefined
  useEffect(() => { if (unknownRole) void auth.refresh() }, [unknownRole, projectId])
  // the project opened last is offered in search and on Home
  const { touch } = useRecentProjects()
  useEffect(() => { if (project) touch(project.id, project.hospitalName) }, [project?.id, project?.hospitalName])   // eslint-disable-line react-hooks/exhaustive-deps

  if (flows.isPending || projects.isPending) return <Skeletons count={3} />
  if (flows.isError) return <Alert tone="warn">{describeError(flows.error)}</Alert>
  if (!project) return <Alert tone="warn">This project was not found, or you cannot open it.</Alert>
  const canCapture = canCaptureIn(projectId)
  const canRenameProject = isAdmin || isLeadIn(projectId)
  const projectHeading = (
    <div className="title-line">
      <EditableTitle value={project.hospitalName} label="project heading" canEdit={canRenameProject} onSave={name => renameProject.mutateAsync({ project, hospitalName: name })}>
        <h1>{project.hospitalName}</h1>
      </EditableTitle>
    </div>
  )
  // every request was deleted: the project stays, with its folders, so a new one can be made
  if (!flow) return (
    <>
      <Link to="/" className="back"><ArrowLeft size={16} aria-hidden />All projects</Link>
      <div className="page-title">
        <div>{projectHeading}<p className="muted">{project.location ? `${project.location} · ` : ''}Started by {project.leadName}</p></div>
        {(isAdmin || isLeadIn(projectId)) && <button className="btn text" onClick={() => setDeleting(true)}><Trash size={16} aria-hidden />Delete project</button>}
      </div>
      {deleting && <DeleteProjectDialog project={project} onClose={() => setDeleting(false)} onDone={() => navigate('/')} />}
      <RequestFolders projectId={projectId} flows={list} selectedId="" onSelect={id => setParams({ flow: id })} canCapture={canCapture} isLead={isLeadIn(projectId)} />
      <div className="empty" role="status"><b>This project has no requests</b><div>Press <b>New request</b> above, or open a module and press <b>New</b> beside new requirements (NR), change requests (CR) or bugs (BUG), to start one.</div></div>
    </>
  )
  const analysed = flow.currentVersionNo > 0
  const hasDocument = !!docs.data?.some(d => d.scope === 'Guide')
  const tokens = usage.data?.byProject.find(p => p.id === projectId)?.totalTokens ?? (usage.data ? 0 : null)

  return (
    <>
      <Link to="/" className="back"><ArrowLeft size={16} aria-hidden />All projects</Link>
      <div className="page-title">
        <div>
          {projectHeading}
          <p className="muted">{project.location ? `${project.location} · ` : ''}Started by {project.leadName} · updated {ago(flow.lastActivityAt)}</p>
        </div>
        {(isAdmin || isLeadIn(projectId)) && <button className="btn text" onClick={() => setDeleting(true)}><Trash size={16} aria-hidden />Delete project</button>}
      </div>
      {deleting && <DeleteProjectDialog project={project} onClose={() => setDeleting(false)} onDone={() => navigate('/')} />}

      <RequestFolders projectId={projectId} flows={list} selectedId={flow.id} onSelect={id => setParams({ flow: id })} canCapture={canCapture} isLead={isLeadIn(projectId)} />
      <ProjectPdfs projectId={projectId} flows={list} canMakeProject={isAdmin || isLeadIn(projectId) || roleIn(projectId) === 'Collaborator'} canMakeModule={canCapture} />

      <div className="request-title">
        <span className={`kind-tag ${(flow.kind ?? 'NR').toLowerCase()}`}>{requestCode(flow)}</span>
        <EditableTitle value={flow.menuName} label="request heading" canEdit={canCapture} onSave={name => renameFlow.mutateAsync({ flowId: flow.id, menuName: name })}>
          <b className="request-name">{flow.menuName}</b>
        </EditableTitle>
        <span className="muted small">{flow.departmentName === 'General' ? '' : flow.departmentName}</span>
      </div>

      <GenerateCard flow={flow} hasDocument={hasDocument} canCapture={canCapture} />

      <div className="ws">
        <div className="ws-main">
          <section className="panel" id="notes" aria-labelledby="h-notes">
            <h2 id="h-notes"><span className="num">1</span>Add your notes</h2>
            <NotesPanel flow={flow} canCapture={canCapture} />
          </section>

          <section className="panel" id="analyse" aria-labelledby="h-analyse">
            <h2 id="h-analyse"><span className="num">2</span>Analyse</h2>
            <AnalysisTab flow={flow} canCapture={canCapture} />
          </section>

          <section className="panel" id="result" aria-labelledby="h-result">
            <h2 id="h-result"><span className="num">3</span>Your workflow and implementation document</h2>
            {analysed
              ? <ResultPanel flow={flow} canCapture={canCapture} />
              : <div className="empty" role="status"><b>Nothing here yet</b><div>After the notes are analysed, the workflow diagram appears here, with a PDF that explains the flow to the developers.</div></div>}
          </section>

          <FigurePanel flow={flow} canCapture={canCapture} />
        </div>
        <Progress flow={flow} hasDocument={hasDocument} canCapture={canCapture} tokens={tokens} />
      </div>
    </>
  )
}
