import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, describeError } from '../../api/client'
import { keys } from '../../api/hooks'
import type { Clarification } from '../../api/types'
import { Pill, PromptDialog, useToast } from '../../components/ui'
import { humanize } from '../../lib/format'

/** One question the AI could not answer from the notes. Anyone who can capture answers it; the lead can also dismiss it or mark it blocking. */
export function QuestionCard({ question, canAnswer, isLead, projectId }: { question: Clarification; canAnswer: boolean; isLead: boolean; projectId: string }) {
  const [answer, setAnswer] = useState(question.answer ?? '')
  const [busy, setBusy] = useState(false)
  const [dismissing, setDismissing] = useState(false)
  const qc = useQueryClient()
  const toast = useToast()
  const done = () => {
    void qc.invalidateQueries({ queryKey: keys.clarifications(question.flowId) })
    void qc.invalidateQueries({ queryKey: keys.flows(projectId) })
    void qc.invalidateQueries({ queryKey: keys.flow(question.flowId) })
  }
  const update = async (body: object, ok?: string) => {
    setBusy(true)
    try { await api(`clarifications/${question.id}`, { method: 'PUT', body }); if (ok) toast(ok, 'good'); done() }
    catch (e) { toast(describeError(e), 'error') }
    finally { setBusy(false) }
  }
  const tone = question.status === 'Answered' ? 'good' : question.status === 'Dismissed' ? '' : 'warn'

  return (
    <div className="card" style={{ display: 'block' }}>
      <div className="row" style={{ marginBottom: 6 }}>
        <b>{question.questionKey}</b><span className="muted small">{humanize(question.section)}</span>
        <span style={{ flex: 1 }} />
        {question.isBlocking && <Pill tone="bad">Blocking</Pill>}
        <Pill tone={tone}>{question.status}</Pill>
      </div>
      <p>{question.question}</p>
      {question.status === 'Dismissed' ? <p className="muted">This question was dismissed.</p>
        : canAnswer ? (
          <>
            {question.options && question.options.length > 0 && (
              <div className="row" style={{ marginBottom: 8 }}>
                {question.options.map(o => <button key={o} className="btn outline" style={{ minHeight: 36 }} onClick={() => setAnswer(o)}>{o}</button>)}
              </div>
            )}
            <div className="field">
              <label htmlFor={`a-${question.id}`}>Your answer</label>
              <textarea id={`a-${question.id}`} className="input" rows={2} style={{ minHeight: 64 }} value={answer} onChange={e => setAnswer(e.target.value)} />
            </div>
            <div className="row">
              <button className="btn primary" disabled={busy || !answer.trim() || answer.trim() === (question.answer ?? '')} onClick={() => void update({ answer: answer.trim() }, 'Answer saved.')}>Save answer</button>
              {isLead && <button className="btn" disabled={busy} onClick={() => void update({ isBlocking: !question.isBlocking })}>{question.isBlocking ? 'Not blocking' : 'Mark blocking'}</button>}
              {isLead && <button className="btn" disabled={busy} onClick={() => setDismissing(true)}>Dismiss</button>}
            </div>
          </>
        ) : question.answer ? <p><b>Answer:</b> {question.answer}</p> : null}
      {dismissing && (
        <PromptDialog title="Dismiss question" label="Why is this not needed?" okText="Dismiss" onClose={() => setDismissing(false)}
          onSubmit={reason => { setDismissing(false); void update({ status: 'Dismissed', dismissReason: reason }) }} />
      )}
    </div>
  )
}
