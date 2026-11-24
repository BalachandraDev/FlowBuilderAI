import { useState } from 'react'
import { ArrowClockwise, CaretLeft, CaretRight, FileText, Image, Lightning, Microphone, Sparkle, TestTube } from '@phosphor-icons/react'
import { useAiUsage } from '../api/hooks'
import { Link } from 'react-router-dom'
import type { AiModelQuota, AiModelUsage, AiQuota, AiUsageKind, AiUsageSummary, AiUsageWho } from '../api/types'
import { DataTable, type Column } from '../components/DataTable'
import { ErrorState } from '../components/ErrorState'
import { Alert, Skeletons } from '../components/ui'
import { change, compact, formatWhen, monthName, shiftMonth } from '../lib/format'

const KINDS: Record<string, { label: string; hint: string; icon: typeof Sparkle }> = {
  Analysis: { label: 'Analysing notes', hint: 'Turning notes into a workflow', icon: Sparkle },
  Guide: { label: 'Implementation documents', hint: 'Writing the explanation in the PDF', icon: FileText },
  Voice: { label: 'Voice notes', hint: 'Listening to recordings', icon: Microphone },
  Photo: { label: 'Photos of notes', hint: 'Reading handwriting', icon: Image },
  Test: { label: 'Key tests', hint: 'Checking the AI key', icon: TestTube },
}
const kindLabel = (k: string) => KINDS[k]?.label ?? k
const full = (n: number) => n.toLocaleString('en-IN')

/** "3 h 26 min", "12 min": how long until a free allowance renews. */
export function renewsIn(iso: string, now = Date.now()): string {
  const minutes = Math.max(0, Math.round((new Date(iso.endsWith('Z') ? iso : iso + 'Z').getTime() - now) / 60_000))
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`
}

function Meter({ label, used, limit }: { label: string; used: number; limit: number | null }) {
  const pct = limit ? Math.min(100, Math.round((used / limit) * 100)) : 0
  return (
    <div className="quota-line">
      <div className="ranked-top"><span>{label}</span><b>{full(used)}{limit ? <span className="muted"> of {full(limit)}</span> : <span className="muted"> · no limit set</span>}</b></div>
      {limit ? <div className="ranked-bar" role="progressbar" aria-label={`${label} used`} aria-valuemin={0} aria-valuemax={limit} aria-valuenow={Math.min(used, limit)}><i className={pct >= 100 ? 'quota-full' : pct >= 80 ? 'quota-high' : ''} style={{ width: `${pct}%` }} /></div> : null}
    </div>
  )
}

/** The free allowance of each AI today: what was used, the limit the administrator set, when it renews, and that the app stops at the limit. */
function Allowance({ quotas }: { quotas: AiQuota[] }) {
  return (
    <section className="card2" aria-label="Free allowance today">
      <div className="folders-head"><h3 style={{ margin: 0 }}>Free allowance today</h3><Link className="btn text" to="/admin/ai">Set the limits</Link></div>
      <p className="muted small" style={{ marginTop: 4 }}>Each AI has its own daily allowance. When an AI reaches the limit set here, the app stops sending anything to it until it renews.</p>
      <div className="quota-grid">
        {quotas.map(q => (
          <div key={q.provider} className={`quota ${q.reached ? 'is-full' : ''}`}>
            <div className="quota-head"><b>{q.provider}</b>{q.reached ? <span className="tag-bad">Limit reached: paused</span> : <span className="muted small">renews in {renewsIn(q.resetsAt)}</span>}</div>
            <Meter label="Requests today" used={q.usedRequests} limit={q.limitRequests} />
            <Meter label="Tokens today" used={q.usedTokens} limit={q.limitTokens} />
            {q.reached && q.message && <p className="small" role="status">{q.message}</p>}
          </div>
        ))}
      </div>
    </section>
  )
}

/** Each Gemini model today against its own limits: the numbers Google AI Studio shows, kept by the app. */
function ModelAllowance({ models }: { models: AiModelQuota[] }) {
  return (
    <section className="card2" aria-label="Gemini models today">
      <div className="folders-head"><h3 style={{ margin: 0 }}>Gemini models today</h3><Link className="btn text" to="/admin/ai">Set the limits</Link></div>
      <p className="muted small" style={{ marginTop: 4 }}>Google limits each model on its own. The app counts every request it sends and stops using a model that is at a limit.</p>
      <div className="quota-grid">
        {models.map(m => (
          <div key={m.model} className={`quota ${m.reached ? 'is-full' : ''}`}>
            <div className="quota-head"><b>{m.name}</b>{m.reached ? <span className="tag-bad">Used up for today</span> : <span className="muted small">renews in {renewsIn(m.resetsAt)}</span>}</div>
            <Meter label="Requests today" used={m.usedToday} limit={m.perDay} />
            <Meter label="Requests, last minute" used={m.usedLastMinute} limit={m.perMinute} />
            <Meter label="Tokens, last minute" used={m.tokensLastMinute} limit={m.tokensPerMinute} />
          </div>
        ))}
      </div>
    </section>
  )
}

function ByModel({ rows }: { rows: AiModelUsage[] }) {
  const max = Math.max(1, ...rows.map(r => r.totalTokens))
  return (
    <section className="card2" aria-label="Each AI and model">
      <h3>Each AI and model</h3>
      {rows.length === 0 && <p className="muted small">Nothing yet this month.</p>}
      <ul className="ranked">
        {rows.map(r => (
          <li key={r.provider + r.model}>
            <div className="ranked-top"><span>{r.provider} <span className="mono small">{r.model}</span></span><b title={`${full(r.totalTokens)} tokens`}>{compact(r.totalTokens)}</b></div>
            <div className="ranked-bar"><i style={{ width: `${(r.totalTokens / max) * 100}%` }} /></div>
            <div className="muted small">{r.calls} call{r.calls === 1 ? '' : 's'} · {full(r.inputTokens)} sent · {full(r.outputTokens)} written</div>
          </li>
        ))}
      </ul>
    </section>
  )
}

/** One bar per day of the month. The tallest bar is the busiest day; today is highlighted. */
function DayBars({ days, today }: { days: AiUsageSummary['byDay']; today: string }) {
  const max = Math.max(1, ...days.map(d => d.totalTokens))
  const peak = days.reduce((a, d) => (d.totalTokens > a.totalTokens ? d : a), days[0])
  const label = (d: string) => new Date(d + 'T00:00:00Z').toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' })
  const [hover, setHover] = useState<AiUsageSummary['byDay'][number] | null>(null)
  const readout = (d: AiUsageSummary['byDay'][number]) => `${label(d.day)}: ${full(d.totalTokens)} tokens, ${d.calls} call${d.calls === 1 ? '' : 's'}`
  return (
    <figure className="bars" role="img" aria-label={peak && peak.totalTokens > 0 ? `Tokens used each day. The busiest day was ${label(peak.day)} with ${full(peak.totalTokens)}.` : 'No tokens were used this month.'}>
      <div className={`bars-readout ${hover ? 'on' : ''}`} aria-hidden>{hover ? readout(hover) : 'Point at a day to see its tokens'}</div>
      <div className="bars-plot" style={{ gridTemplateColumns: `repeat(${days.length}, 1fr)` }} onMouseLeave={() => setHover(null)}>
        {days.map(d => (
          <div key={d.day} className={`bar ${d.day === today ? 'now' : ''} ${d.totalTokens === 0 ? 'zero' : ''} ${hover?.day === d.day ? 'hot' : ''}`} title={readout(d)} onMouseEnter={() => setHover(d)}>
            <i style={{ height: `${Math.max(d.totalTokens === 0 ? 0 : 4, (d.totalTokens / max) * 100)}%` }} />
          </div>
        ))}
      </div>
      <figcaption className="bars-axis"><span>{days[0] ? label(days[0].day) : ''}</span><span>{peak && peak.totalTokens > 0 ? `Busiest: ${label(peak.day)} · ${compact(peak.totalTokens)}` : ''}</span><span>{days.length ? label(days[days.length - 1].day) : ''}</span></figcaption>
    </figure>
  )
}

function Ranked({ title, rows, empty }: { title: string; rows: AiUsageWho[]; empty: string }) {
  const max = Math.max(1, ...rows.map(r => r.totalTokens))
  return (
    <section className="card2" aria-label={title}>
      <h3>{title}</h3>
      {rows.length === 0 && <p className="muted small">{empty}</p>}
      <ul className="ranked">
        {rows.map(r => (
          <li key={(r.id ?? '') + r.name}>
            <div className="ranked-top"><span>{r.name}</span><b title={`${full(r.totalTokens)} tokens`}>{compact(r.totalTokens)}</b></div>
            <div className="ranked-bar"><i style={{ width: `${(r.totalTokens / max) * 100}%` }} /></div>
            <div className="muted small">{r.calls} call{r.calls === 1 ? '' : 's'}</div>
          </li>
        ))}
      </ul>
    </section>
  )
}

function ByKind({ rows, total }: { rows: AiUsageKind[]; total: number }) {
  return (
    <section className="card2" aria-label="What the AI was used for">
      <h3>What it was used for</h3>
      {rows.length === 0 && <p className="muted small">Nothing yet this month.</p>}
      <ul className="kinds">
        {rows.map(k => {
          const meta = KINDS[k.kind]
          const Icon = meta?.icon ?? Lightning
          const share = total > 0 ? Math.round((k.totalTokens / total) * 100) : 0
          return (
            <li key={k.kind}>
              <span className="kind-icon" aria-hidden><Icon size={20} weight="duotone" /></span>
              <div className="kind-main">
                <div className="ranked-top"><span>{kindLabel(k.kind)}</span><b title={`${full(k.totalTokens)} tokens`}>{compact(k.totalTokens)}</b></div>
                <div className="ranked-bar"><i style={{ width: `${share}%` }} /></div>
                <div className="muted small">{meta?.hint ?? ''} · {k.calls} call{k.calls === 1 ? '' : 's'} · {share}%</div>
              </div>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

type Call = AiUsageSummary['recent'][number]

/** The columns of the table of the latest calls (the Who column only for an administrator, who sees everybody's). */
function callColumns(allUsers: boolean): Column<Call>[] {
  const cols: Column<Call>[] = [
    { key: 'when', header: 'When', text: c => c.at, cell: c => formatWhen(c.at) },
    { key: 'what', header: 'What', text: c => kindLabel(c.kind) + (c.success ? '' : ' (not used)'), cell: c => <>{kindLabel(c.kind)}{!c.success && <span className="tag-bad"> not used</span>}</> },
    { key: 'who', header: 'Who', text: c => c.who ?? '', cell: c => c.who },
    { key: 'project', header: 'Project', text: c => c.project ?? '', cell: c => c.project || <span className="muted">-</span> },
    { key: 'sent', header: 'Sent', numeric: true, text: c => c.inputTokens, cell: c => full(c.inputTokens) },
    { key: 'written', header: 'Written', numeric: true, text: c => c.outputTokens, cell: c => full(c.outputTokens) },
    { key: 'time', header: 'Time', numeric: true, text: c => c.durationMs, cell: c => (c.durationMs > 0 ? `${(c.durationMs / 1000).toFixed(1)} s` : '-') },
  ]
  return allUsers ? cols : cols.filter(c => c.key !== 'who')
}
/** How many tokens the AI used: this month, by purpose, by day, by person and by project, and the latest calls. */
export function AiUsage() {
  const [month, setMonth] = useState<string | undefined>(undefined)
  const usage = useAiUsage(month)
  const u = usage.data
  const thisMonth = new Date().toISOString().slice(0, 7)
  const shown = u?.month ?? month ?? thisMonth
  const todayIso = new Date().toISOString().slice(0, 10)
  const delta = u ? change(u.totals.totalTokens, u.lastMonth.totalTokens) : null

  return (
    <>
      <div className="page-title">
        <div>
          <h1>{u?.allUsers ? 'AI usage' : 'My AI usage'}</h1>
          <p className="muted">{u?.allUsers ? 'How many tokens the AI has used, and what for.' : 'How many tokens the AI has used for your notes and documents.'}</p>
        </div>
        <div className="row">
          <button className="btn" onClick={() => void usage.refetch()} disabled={usage.isFetching} aria-label="Refresh the numbers"><ArrowClockwise size={16} aria-hidden />{usage.isFetching ? 'Refreshing…' : 'Refresh'}</button>
          <div className="monthnav" role="group" aria-label="Month">
            <button className="icon-btn" onClick={() => setMonth(shiftMonth(shown, -1))} aria-label="Previous month"><CaretLeft size={20} /></button>
            <span className="monthnav-label" aria-live="polite">{monthName(shown)}</span>
            <button className="icon-btn" onClick={() => setMonth(shiftMonth(shown, 1) >= thisMonth ? undefined : shiftMonth(shown, 1))} disabled={shown >= thisMonth} aria-label="Next month"><CaretRight size={20} /></button>
          </div>
        </div>
      </div>

      {usage.isPending && <Skeletons count={3} />}
      {usage.isError && <ErrorState error={usage.error} onRetry={() => void usage.refetch()} retrying={usage.isFetching} />}
      {u && (
        <>
          <section className="usage-hero" aria-label="Tokens used this month">
            <div className="usage-hero-main">
              <div className="usage-kicker">Tokens used in {monthName(u.month)}</div>
              <div className="usage-big" title={`${full(u.totals.totalTokens)} tokens`}>{compact(u.totals.totalTokens)}</div>
              <div className="muted">
                {u.totals.calls === 0 ? 'The AI has not been used in this month.' : `${full(u.totals.totalTokens)} tokens in ${u.totals.calls} call${u.totals.calls === 1 ? '' : 's'}`}
                {delta && <span className={`delta ${delta.startsWith('+') ? 'up' : 'down'}`}> {delta} on the month before</span>}
              </div>
            </div>
            <DayBars days={u.byDay} today={todayIso} />
          </section>

          {u.allUsers && (u.modelQuotas?.length ?? 0) > 0 && <ModelAllowance models={u.modelQuotas!} />}
          {u.allUsers && (u.quotas?.length ?? 0) > 0 && <Allowance quotas={u.quotas!} />}

          <div className="stats4" aria-label="Totals">
            <div className="stat"><div className="n">{full(u.totals.calls)}</div><div className="l">AI calls</div></div>
            <div className="stat"><div className="n" title={`${full(u.totals.inputTokens)} tokens`}>{compact(u.totals.inputTokens)}</div><div className="l">tokens sent to the AI</div></div>
            <div className="stat"><div className="n" title={`${full(u.totals.outputTokens)} tokens`}>{compact(u.totals.outputTokens)}</div><div className="l">tokens the AI wrote</div></div>
            <div className="stat"><div className="n" title={`${full(u.today.totalTokens)} tokens`}>{compact(u.today.totalTokens)}</div><div className="l">tokens today</div></div>
          </div>

          <div className="usage-cols">
            <ByKind rows={u.byKind} total={u.totals.totalTokens} />
            {(u.byModel?.length ?? 0) > 0 && <ByModel rows={u.byModel!} />}
            <Ranked title="Projects" rows={u.byProject} empty="No project has used the AI yet." />
            {u.allUsers && <Ranked title="People" rows={u.byUser} empty="Nobody has used the AI yet." />}
          </div>

          <section className="card2" aria-label="Recent AI calls">
            <h3>Latest calls</h3>
            <DataTable id="ai-calls" label="Latest calls" rows={u.recent} exportName="ai-calls" empty="Nothing yet this month." rowKey={(_, i) => String(i)} rowClass={c => (c.success ? '' : 'failed')}
              onRefresh={() => void usage.refetch()} refreshing={usage.isFetching} defaultSort={{ key: 'when', dir: 'desc' }}
              columns={callColumns(u.allUsers)} />
          </section>

          {u.provider === 'Demo' && <Alert>The AI is in demo mode, which returns a fixed example and uses no tokens, so the numbers here stay at zero. Switch to the free Gemini AI in AI setup to see real usage.</Alert>}
          <p className="muted small footnote">
            Counted from what the AI says each call used. {u.provider === 'Gemini' && <>The free tier’s own limits (calls per minute and per day) are set by Google and shown in <a href="https://aistudio.google.com/usage" target="_blank" rel="noopener noreferrer">Google AI Studio</a>. </>}
            Calls the AI refused or that failed before it answered use nothing and are not listed.
          </p>
        </>
      )}
    </>
  )
}
