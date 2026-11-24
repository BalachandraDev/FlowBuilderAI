import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, describeError } from '../../api/client'
import type { AiAdmin, AiQuota } from '../../api/types'
import { Alert, Field, useToast } from '../../components/ui'
import { renewsIn } from '../AiUsage'

const FIELDS: { provider: string; what: 'Requests' | 'Tokens'; label: string }[] = [
  { provider: 'Gemini', what: 'Requests', label: 'Gemini: requests a day' },
  { provider: 'Gemini', what: 'Tokens', label: 'Gemini: tokens a day' },
  { provider: 'Groq', what: 'Requests', label: 'Groq: requests a day' },
  { provider: 'Groq', what: 'Tokens', label: 'Groq: tokens a day' },
]

const valueOf = (q: AiQuota | undefined, what: 'Requests' | 'Tokens') => (what === 'Requests' ? q?.limitRequests : q?.limitTokens)
const used = (q: AiQuota | undefined, what: 'Requests' | 'Tokens') => (what === 'Requests' ? q?.usedRequests : q?.usedTokens) ?? 0

/**
 * The daily free allowance of each AI account. The administrator copies the numbers from the account (Google AI Studio, Groq Console); the app
 * counts what each AI has used since its day began and stops sending to an AI that is at its limit, so a free allowance is never overrun.
 */
export function DailyLimits({ a, number }: { a: AiAdmin; number: number }) {
  const qc = useQueryClient()
  const toast = useToast()
  const quotas = a.quotas ?? []
  const [values, setValues] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // start from what is saved (an empty box means no limit)
  useEffect(() => {
    const v: Record<string, string> = {}
    for (const f of FIELDS) { const n = valueOf(quotas.find(q => q.provider === f.provider), f.what); v[`${f.provider}.${f.what}`] = n ? String(n) : '' }
    setValues(v)
  }, [a])   // eslint-disable-line react-hooks/exhaustive-deps

  const saved = (key: string) => { const [p, w] = key.split('.') as [string, 'Requests' | 'Tokens']; const n = valueOf(quotas.find(q => q.provider === p), w); return n ? String(n) : '' }
  const dirty = Object.keys(values).some(k => (values[k] ?? '').trim() !== saved(k))
  const invalid = Object.values(values).some(v => v.trim() !== '' && !/^\d{1,12}$/.test(v.trim()))

  async function save() {
    setBusy(true); setError(null)
    try {
      const limits: Record<string, number> = {}
      for (const k of Object.keys(values)) if ((values[k] ?? '').trim() !== saved(k)) limits[k] = values[k].trim() === '' ? 0 : Number(values[k].trim())
      qc.setQueryData(['admin-ai'], await api<AiAdmin>('admin/ai', { method: 'PUT', body: { limits } }))
      void qc.invalidateQueries({ queryKey: ['ai-usage'] })
      toast('The limits were saved.', 'good')
    } catch (e) { setError(describeError(e)) } finally { setBusy(false) }
  }

  return (
    <section className="panel" aria-labelledby="h-limits">
      <h2 id="h-limits"><span className="num">{number}</span>Overall daily limits for each AI account (optional)</h2>
      <p className="muted">Each free account has a daily allowance. Enter it here and the app counts what each AI has used today and <b>stops sending anything to an AI that is at its limit</b>, until the allowance renews (Gemini at midnight Pacific time, Groq at midnight UTC). If Groq is at its limit, voice notes go to Gemini while Gemini still has room. Leave a box empty for no limit in the app (the provider still has its own).</p>
      <p className="muted small">Take the numbers from your accounts: Google AI Studio, Usage and limits; Groq Console, Limits. They are counted from what this app sent, so use made outside the app is not included. Gemini’s limits are really per model; the total here is a safety net across all of them.</p>
      <div className="limit-grid">
        {FIELDS.map(f => {
          const key = `${f.provider}.${f.what}`
          const q = quotas.find(x => x.provider === f.provider)
          return (
            <Field key={key} label={f.label} help={`Used today: ${used(q, f.what).toLocaleString('en-IN')}${q ? ` · renews in ${renewsIn(q.resetsAt)}` : ''}`}>
              {id => <input id={id} className="input" inputMode="numeric" autoComplete="off" value={values[key] ?? ''} placeholder="No limit" onChange={e => setValues(v => ({ ...v, [key]: e.target.value }))} />}
            </Field>
          )
        })}
      </div>
      {quotas.filter(q => q.reached).map(q => <Alert key={q.provider} tone="warn">{q.message}</Alert>)}
      {error && <Alert tone="error">{error}</Alert>}
      <div className="row">
        <button className="btn primary" disabled={!dirty || invalid || busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save the limits'}</button>
        {invalid && <span className="muted small">Use whole numbers only.</span>}
      </div>
    </section>
  )
}
