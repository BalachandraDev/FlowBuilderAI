import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, describeError } from '../../api/client'
import type { AiAdmin, AiModelQuota } from '../../api/types'
import { Alert, useToast } from '../../components/ui'
import { renewsIn } from '../AiUsage'

type Field = 'PerDay' | 'PerMinute' | 'TokensPerMinute'
const FIELDS: { what: Field; label: string; read: (m: AiModelQuota) => number | null }[] = [
  { what: 'PerDay', label: 'Requests a day', read: m => m.perDay },
  { what: 'PerMinute', label: 'Requests a minute', read: m => m.perMinute },
  { what: 'TokensPerMinute', label: 'Tokens a minute', read: m => m.tokensPerMinute },
]
const text = (n: number | null) => (n ? String(n) : '')
const full = (n: number) => n.toLocaleString('en-IN')

/**
 * The free Gemini limits are per model (Google AI Studio, Rate limit page): requests a minute, tokens a minute and requests a day. The app counts every
 * request it sends to each model and never sends to a model that is at a limit: it uses another model, waits out the minute, or says it is used up for the day.
 */
export function ModelLimits({ a }: { a: AiAdmin }) {
  const qc = useQueryClient()
  const toast = useToast()
  const models = a.modelQuotas ?? []
  const [values, setValues] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const v: Record<string, string> = {}
    for (const m of models) for (const f of FIELDS) v[`${m.model}.${f.what}`] = text(f.read(m))
    setValues(v)
  }, [a])   // eslint-disable-line react-hooks/exhaustive-deps

  const saved = (key: string) => {
    const i = key.lastIndexOf('.'); const model = key.slice(0, i); const what = key.slice(i + 1) as Field
    const m = models.find(x => x.model === model); const f = FIELDS.find(x => x.what === what)
    return m && f ? text(f.read(m)) : ''
  }
  const changed = Object.keys(values).filter(k => (values[k] ?? '').trim() !== saved(k))
  const invalid = Object.values(values).some(v => v.trim() !== '' && !/^\d{1,12}$/.test(v.trim()))

  async function save() {
    setBusy(true); setError(null)
    try {
      const modelLimits: Record<string, number> = {}
      for (const k of changed) modelLimits[k] = values[k].trim() === '' ? 0 : Number(values[k].trim())      // an empty box removes the limit
      qc.setQueryData(['admin-ai'], await api<AiAdmin>('admin/ai', { method: 'PUT', body: { modelLimits } }))
      void qc.invalidateQueries({ queryKey: ['ai-usage'] }); void qc.invalidateQueries({ queryKey: ['system-info'] })
      toast('The model limits were saved.', 'good')
    } catch (e) { setError(describeError(e)) } finally { setBusy(false) }
  }

  if (models.length === 0) return null
  return (
    <section className="panel" aria-labelledby="h-model-limits">
      <h2 id="h-model-limits"><span className="num">3</span>Limits of each Gemini model</h2>
      <p className="muted">Google’s free tier limits <b>each model</b> on its own. The app counts every request it sends to each one, <b>never sends to a model that is at a limit</b> (it uses the next model, waits out the minute, or says the day is used up) and tells everybody how many requests are left. Take the numbers from Google AI Studio, Rate limit. The first three models are filled in from your page; an empty box means no limit in the app.</p>
      <div className="table-wrap">
        <table className="plain" aria-label="Limits of each Gemini model">
          <thead><tr><th>Model</th><th className="num-col">Today</th>{FIELDS.map(f => <th key={f.what}>{f.label}</th>)}</tr></thead>
          <tbody>
            {models.map(m => (
              <tr key={m.model} className={m.reached ? 'failed' : ''}>
                <td><b>{m.name}</b><div className="muted small mono">{m.model}</div>{m.isPreset && <div className="muted small">preset</div>}</td>
                <td className="num-col">{full(m.usedToday)}{m.perDay ? <span className="muted"> of {full(m.perDay)}</span> : null}{m.reached && <div><span className="tag-bad">used up</span></div>}<div className="muted small">renews in {renewsIn(m.resetsAt)}</div></td>
                {FIELDS.map(f => (
                  <td key={f.what}>
                    <label className="sr-only" htmlFor={`${m.model}-${f.what}`}>{f.label} for {m.name}</label>
                    <input id={`${m.model}-${f.what}`} className="input" style={{ minWidth: 96 }} inputMode="numeric" autoComplete="off" placeholder="No limit"
                      value={values[`${m.model}.${f.what}`] ?? ''} onChange={e => setValues(v => ({ ...v, [`${m.model}.${f.what}`]: e.target.value }))} />
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {error && <Alert tone="error">{error}</Alert>}
      <div className="row">
        <button className="btn primary" disabled={changed.length === 0 || invalid || busy} onClick={() => void save()}>{busy ? 'Saving…' : 'Save the model limits'}</button>
        {invalid && <span className="muted small">Use whole numbers only.</span>}
      </div>
      <p className="muted small">The tool’s own count can be lower than Google’s if the same key is used somewhere else. When Google says a model’s day is used up, the tool believes it and stops using that model until the day renews. <b>Test the key</b> asks every model one question, which uses one request of each model’s day, so it skips a model that is used up.</p>
    </section>
  )
}
