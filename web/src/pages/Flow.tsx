import { useEffect } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { ArrowLeft } from '@phosphor-icons/react'
import { describeError } from '../api/client'
import { useFlow } from '../api/hooks'
import { NotesPanel } from '../components/NotesPanel'
import { Alert, Pill, Skeletons } from '../components/ui'
import { statusText, statusTone } from '../lib/format'
import { useSession } from '../state/session'
import { AnalysisTab } from './flow/AnalysisTab'
import { ReviewTab } from './flow/ReviewTab'
import { VersionsTab } from './flow/VersionsTab'

const TABS = [['capture', 'Notes'], ['analysis', 'Analysis'], ['review', 'Review and edit'], ['versions', 'Versions']] as const

/** The detailed view of one workflow: the notes, the analysis, and every section of the result as a form, with its history. */
export function FlowPage() {
  const { flowId = '' } = useParams()
  const { canCaptureIn } = useSession()
  const flow = useFlow(flowId)
  const [params, setParams] = useSearchParams()
  const tab = params.get('tab') ?? 'capture'
  const setTab = (t: string) => setParams(t === 'capture' ? {} : { tab: t }, { replace: true })
  const analysed = (flow.data?.currentVersionNo ?? 0) > 0

  // a tab that needs a version cannot stay open when there is none (for example right after opening a link to it)
  useEffect(() => { if (flow.data && !analysed && (tab === 'review' || tab === 'versions')) setTab('capture') }, [flow.data, analysed, tab])   // eslint-disable-line react-hooks/exhaustive-deps

  if (flow.isPending) return <Skeletons count={3} />
  if (flow.isError || !flow.data) return <Alert tone="warn">{flow.error ? describeError(flow.error) : 'This workflow was not found.'}</Alert>
  const f = flow.data
  const canCapture = canCaptureIn(f.projectId)

  return (
    <>
      <Link to={`/p/${f.projectId}`} className="back"><ArrowLeft size={16} aria-hidden />Back to the project</Link>
      <div className="page-title">
        <div><h1>{f.menuName}</h1></div>
        <div className="row">
          <Pill tone={statusTone(f.status)}>{statusText(f.status)}</Pill>
          {f.currentVersionNo > 0 && <Pill>Version {f.currentVersionNo}</Pill>}
          {f.openQuestionCount > 0 && <Pill tone="warn">{f.openQuestionCount} open question(s)</Pill>}
        </div>
      </div>
      <div className="tabs" role="tablist">
        {TABS.map(([key, label]) => (
          <button key={key} role="tab" className="tab" aria-selected={tab === key} disabled={(key === 'review' || key === 'versions') && !analysed} onClick={() => setTab(key)}>{label}</button>
        ))}
      </div>
      {tab === 'capture' && <NotesPanel flow={f} canCapture={canCapture} />}
      {tab === 'analysis' && <AnalysisTab flow={f} canCapture={canCapture} />}
      {tab === 'review' && <ReviewTab flow={f} />}
      {tab === 'versions' && <VersionsTab flow={f} />}
    </>
  )
}
