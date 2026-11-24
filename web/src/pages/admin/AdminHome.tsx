import { Link } from 'react-router-dom'
import { ArrowClockwise, ArrowRight, Buildings, ChartBar, FilePdf, FolderOpen, Robot, TreeStructure, Users } from '@phosphor-icons/react'
import { useAdminOverview } from '../../api/hooks'
import { ErrorState } from '../../components/ErrorState'
import { Skeletons } from '../../components/ui'

const PROVIDER = { gemini: 'Free Gemini AI', anthropic: 'Claude', demo: 'Demo mode', 'no-key': 'Not set up' } as const

/** The administrator's start page: what is waiting for them, and the places to make users and clients and to see who handles what. */
export function AdminHome() {
  const overview = useAdminOverview()
  const o = overview.data
  const waiting = o?.awaitingVerification ?? 0
  return (
    <>
      <div className="page-title">
        <div><h1>Admin</h1><p className="muted">Make users and clients, assign users to their clients, and verify the documents your users mark as final.</p></div>
        <div className="row">
          <button className="btn" onClick={() => void overview.refetch()} disabled={overview.isFetching} aria-label="Refresh the numbers"><ArrowClockwise size={16} aria-hidden />{overview.isFetching ? 'Refreshing…' : 'Refresh'}</button>
          {overview.dataUpdatedAt > 0 && <span className="updated" role="status">Updated {new Date(overview.dataUpdatedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>}
          <Link to="/admin/clients" className="btn lg"><Buildings size={20} weight="bold" aria-hidden />Manage clients</Link>
          <Link to="/admin/users" className="btn primary lg"><Users size={20} weight="bold" aria-hidden />Manage users</Link>
        </div>
      </div>

      {overview.isPending && <Skeletons count={2} />}
      {overview.isError && <ErrorState error={overview.error} onRetry={() => void overview.refetch()} retrying={overview.isFetching} />}
      {o && (
        <>
          <Link to="/documents" className={`aicard ${waiting > 0 ? 'todo' : 'ok'}`}>
            <span className="aicard-icon"><FilePdf size={28} weight="duotone" aria-hidden /></span>
            <span className="aicard-main">
              <b>{waiting > 0 ? `${waiting} final document${waiting === 1 ? '' : 's'} waiting for you` : 'No final documents are waiting'}</b>
              <span>{waiting > 0 ? 'Users marked these as final, to give to the team. Open each one, then verify it or return it with a reason.' : 'When a user marks a document as final, it appears here for you to verify. You see only final documents, never working copies.'}</span>
            </span>
            <span className="aicard-go">{waiting > 0 ? 'Verify' : 'Open'}<ArrowRight size={16} weight="bold" aria-hidden /></span>
          </Link>
          <Link to="/admin/handling" className="aicard usage-link">
            <span className="aicard-icon"><TreeStructure size={28} weight="duotone" aria-hidden /></span>
            <span className="aicard-main"><b>Who handles what</b><span>See which user handles which client, what each has done, and filter by client or by user.</span></span>
            <span className="aicard-go">Open<ArrowRight size={16} weight="bold" aria-hidden /></span>
          </Link>

          <div className="stats4" aria-label="Totals">
            <Link to="/admin/users" className="stat link" aria-label="Active users: open the users"><Users size={22} aria-hidden /><div className="n">{o.activeUsers}<span className="of"> / {o.users}</span></div><div className="l">active users</div></Link>
            <Link to="/admin/clients" className="stat link" aria-label="Clients: open the clients"><Buildings size={22} aria-hidden /><div className="n">{o.clients ?? 0}</div><div className="l">clients</div></Link>
            <Link to="/admin/handling" className="stat link" aria-label="Projects: see who handles what"><FolderOpen size={22} aria-hidden /><div className="n">{o.projects}</div><div className="l">projects</div></Link>
            <Link to="/documents?status=Final" className="stat link" aria-label="Final documents waiting: open them"><FilePdf size={22} aria-hidden /><div className="n">{waiting}</div><div className="l">final, waiting</div></Link>
          </div>

          <Link to="/usage" className="aicard usage-link">
            <span className="aicard-icon"><ChartBar size={28} weight="duotone" aria-hidden /></span>
            <span className="aicard-main"><b>AI usage</b><span>See how many tokens the AI has used this month, what for, and who used them.</span></span>
            <span className="aicard-go">Open<ArrowRight size={16} weight="bold" aria-hidden /></span>
          </Link>
          <Link to="/admin/ai" className={`aicard ${o.aiReady ? 'ok' : 'todo'}`}>
            <span className="aicard-icon"><Robot size={28} weight="duotone" aria-hidden /></span>
            <span className="aicard-main">
              <b>{o.aiReady ? 'The AI is ready' : 'The AI is not set up yet'}</b>
              <span>{o.aiReady ? `Using: ${PROVIDER[o.aiProvider as keyof typeof PROVIDER] ?? o.aiProvider}. Voice, photos and analysis work.` : 'Voice notes, photos, analysis and documents need a free AI key. It takes two minutes.'}</span>
            </span>
            <span className="aicard-go">{o.aiReady ? 'Settings' : 'Set it up'}<ArrowRight size={16} weight="bold" aria-hidden /></span>
          </Link>
        </>
      )}
    </>
  )
}
