import { useApiImage } from '../lib/files'
import { Alert } from './ui'

/**
 * The workflow picture exactly as it goes into the implementation document: drawn by the server from the steps and decisions, so the
 * screen, the PDF and the Word document always agree. It is plain SVG shown as a picture, so nothing in the notes can run as a script.
 */
export function FlowDiagramView({ flowId, versionNo, full = false }: { flowId: string; versionNo: number; full?: boolean }) {
  const { url, failed } = useApiImage(`flows/${flowId}/diagram`, { format: 'svg', versionNo })
  if (failed) return <Alert tone="warn">The diagram could not be drawn just now. Close this and try again in a moment.</Alert>
  if (!url) return <div className="skeleton" aria-busy="true" aria-label="Drawing the diagram" />
  return (
    <div className={`diagram flow-diagram${full ? ' full' : ''}`}>
      <img src={url} alt="Workflow diagram" />
    </div>
  )
}
