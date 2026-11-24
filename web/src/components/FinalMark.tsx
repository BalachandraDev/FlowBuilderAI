import { useState } from 'react'
import { ArrowUUpLeft, CheckCircle, Flag, Hourglass, Warning } from '@phosphor-icons/react'
import { useQueryClient } from '@tanstack/react-query'
import { api, describeError } from '../api/client'
import type { DocumentStage, DocumentVersion } from '../api/types'
import { formatWhen } from '../lib/format'
import { Alert, Dialog, Field, Pill, useToast } from './ui'

/** Where a version of a document stands, from what the server says about that version. */
export function stageOf(v: Pick<DocumentVersion, 'approvalStatus' | 'finalizedAt'>): DocumentStage {
  if (!v.finalizedAt) return 'Working'
  return v.approvalStatus === 'Approved' ? 'Verified' : v.approvalStatus === 'Rejected' ? 'Returned' : 'Final'
}

export const STAGE_LABEL: Record<DocumentStage, string> = {
  Working: 'Working copy', Final: 'Final · waiting for the administrator', Verified: 'Verified by the administrator', Returned: 'Returned by the administrator',
}

export function StagePill({ stage }: { stage: DocumentStage }) {
  const tone = stage === 'Verified' ? 'good' : stage === 'Returned' ? 'warn' : stage === 'Final' ? 'progress' : ''
  const Icon = stage === 'Verified' ? CheckCircle : stage === 'Returned' ? Warning : stage === 'Final' ? Hourglass : null
  return <Pill tone={tone}>{Icon && <Icon size={14} aria-hidden />}{STAGE_LABEL[stage]}</Pill>
}

/** Everything that may have changed when a document is marked final, taken back, verified or returned. */
export function useRefreshDocuments() {
  const qc = useQueryClient()
  return () => qc.invalidateQueries({ predicate: q => ['library', 'flow-documents', 'admin-overview', 'admin-handling', 'pending'].includes(String(q.queryKey[0])) })
}

interface Props {
  docId: string
  versionNo: number
  stage: DocumentStage
  /** Who marked it final and when, the note, and the administrator's decision, to say it in words. */
  finalizedByName?: string | null; finalizedAt?: string | null; finalNote?: string | null
  reviewNote?: string | null; verifiedByName?: string | null
  /** Only people who work on the document may mark it final or take it back. */
  canAct: boolean
  /** A smaller version for a row in a list. */
  compact?: boolean
}

/**
 * Marking a document as final, to give to the team: the maker marks the latest version (the administrator then sees only that, and verifies or returns it),
 * can take the mark back until the administrator decides, and is told when it was verified or returned and why.
 */
export function FinalControls({ docId, versionNo, stage, finalizedByName, finalizedAt, finalNote, reviewNote, verifiedByName, canAct, compact = false }: Props) {
  const toast = useToast()
  const refresh = useRefreshDocuments()
  const [asking, setAsking] = useState(false)
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function mark() {
    setBusy(true); setError(null)
    try {
      await api(`documents/${docId}/versions/${versionNo}/final`, { method: 'POST', body: { note: note.trim() || null } })
      setAsking(false); setNote('')
      toast('Marked as final. The administrator can now see it and verify it.', 'good', { action: { label: 'Undo', onClick: () => void takeBack(true) } })
      void refresh()
    } catch (e) { setError(describeError(e)) } finally { setBusy(false) }
  }

  async function takeBack(undo = false) {
    setBusy(true)
    try {
      await api(`documents/${docId}/versions/${versionNo}/final`, { method: 'DELETE' })
      toast(undo ? 'Undone. It is a working copy again.' : 'Taken back. It is a working copy again, and the administrator no longer sees it.')
      void refresh()
    } catch (e) { toast(describeError(e), 'error') } finally { setBusy(false) }
  }

  return (
    <div className={`final-controls ${compact ? 'compact' : ''}`}>
      <div className="final-line">
        <StagePill stage={stage} />
        {stage === 'Final' && <span className="muted small">{finalizedByName ? `marked by ${finalizedByName}` : 'marked'}{finalizedAt ? ` · ${formatWhen(finalizedAt)}` : ''}</span>}
        {stage === 'Verified' && verifiedByName && <span className="muted small">by {verifiedByName}</span>}
        {canAct && stage === 'Working' && <button className="btn" onClick={() => { setError(null); setAsking(true) }}><Flag size={18} aria-hidden />Mark as final</button>}
        {canAct && stage === 'Final' && <button className="btn" disabled={busy} onClick={() => void takeBack()}><ArrowUUpLeft size={18} aria-hidden />Take back</button>}
      </div>
      {stage === 'Final' && finalNote && <div className="muted small">Your note: {finalNote}</div>}
      {stage === 'Returned' && (
        <Alert tone="warn">
          <b>Returned by the administrator:</b> {reviewNote || 'no reason given'}.{canAct && ' Update the document, then mark the new version as final.'}
        </Alert>
      )}

      {asking && (
        <Dialog title="Mark this document as final?" onClose={() => setAsking(false)} actions={<>
          <button className="btn" onClick={() => setAsking(false)}>Cancel</button>
          <button className="btn primary" onClick={() => void mark()} disabled={busy}>{busy ? 'Marking…' : 'Mark as final'}</button>
        </>}>
          <p>This hands <b>version {versionNo}</b> to the administrator to verify. They will see only this final version, never your working copies. You can take it back until they decide.</p>
          <p className="muted small">Open the PDF and check it first. If something is wrong, update the document before marking it final.</p>
          <Field label="Note for the administrator (optional)" help="For example: what changed, or what to check.">
            {id => <textarea id={id} className="input" rows={3} maxLength={1000} value={note} onChange={e => setNote(e.target.value)} />}
          </Field>
          {error && <Alert tone="error">{error}</Alert>}
        </Dialog>
      )}
    </div>
  )
}
