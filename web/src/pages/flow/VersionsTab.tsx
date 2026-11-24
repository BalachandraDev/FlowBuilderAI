import { useEffect, useState } from 'react'
import { api, describeError, get } from '../../api/client'
import { useRefreshFlow, useVersions } from '../../api/hooks'
import type { Flow, FlowContentDto, FlowDiff } from '../../api/types'
import { Alert, ConfirmDialog, Field, Pill, Skeletons, useToast } from '../../components/ui'
import { humanize, summarize } from '../../lib/flowContent'
import { formatWhen } from '../../lib/format'
import { useSession } from '../../state/session'

export function DiffView({ diff }: { diff: FlowDiff }) {
  if (diff.items.length === 0) return <Alert>Versions {diff.fromVersion} and {diff.toVersion} are identical.</Alert>
  const groups = new Map<string, typeof diff.items>()
  for (const i of diff.items) groups.set(i.section, [...(groups.get(i.section) ?? []), i])
  return (
    <>
      <h3 style={{ margin: '12px 0' }}>Version {diff.fromVersion} to version {diff.toVersion}: {diff.items.length} change(s)</h3>
      {[...groups].map(([section, items]) => (
        <section key={section}>
          <div style={{ fontWeight: 600, marginTop: 12 }}>{humanize(section)}</div>
          {items.map((d, i) => (
            <div key={d.itemId + i} className="card" style={{ display: 'block', padding: 10 }}>
              <Pill tone={d.change === 'Added' ? 'good' : d.change === 'Removed' ? 'bad' : 'warn'}>{d.change}</Pill> <b>{d.itemId}</b>
              {d.before != null && <div className="diff before"><span className="muted">Before:</span> {summarize(d.before)}</div>}
              {d.after != null && <div className="diff after"><span className="muted">After:</span> {summarize(d.after)}</div>}
            </div>
          ))}
        </section>
      ))}
    </>
  )
}

/** Every analysis and every edit is kept as a version. Compare two of them, or roll back (a rollback is saved as a new version). */
export function VersionsTab({ flow }: { flow: Flow }) {
  const { isLeadIn } = useSession()
  const toast = useToast()
  const refresh = useRefreshFlow()
  const versions = useVersions(flow.id)
  const [from, setFrom] = useState(0)
  const [to, setTo] = useState(0)
  const [diff, setDiff] = useState<FlowDiff | null>(null)
  const [busy, setBusy] = useState(false)
  const [rollback, setRollback] = useState<number | null>(null)
  const list = versions.data ?? []

  useEffect(() => {
    if (list.length === 0) return
    setTo(list[0].versionNo)
    setFrom((list[1] ?? list[0]).versionNo)
  }, [versions.data])                                                                      // eslint-disable-line react-hooks/exhaustive-deps

  async function compare() {
    setBusy(true)
    try { setDiff(await get<FlowDiff>(`flows/${flow.id}/versions/${Math.min(from, to)}/diff/${Math.max(from, to)}`)) }
    catch (e) { toast(describeError(e), 'error') }
    finally { setBusy(false) }
  }

  async function doRollback(n: number) {
    setRollback(null); setBusy(true)
    try {
      await api<FlowContentDto>(`flows/${flow.id}/versions/${n}/rollback`, { method: 'POST', body: {} })
      toast(`Rolled back to version ${n}.`, 'good')
      setDiff(null)
      refresh(flow.id, flow.projectId)
    } catch (e) { toast(describeError(e), 'error') }
    finally { setBusy(false) }
  }

  if (versions.isPending) return <Skeletons count={3} />
  if (versions.isError) return <Alert tone="error">{describeError(versions.error)}</Alert>
  return (
    <>
      <p className="muted">Every analysis and every edit is kept as a version. Compare two versions, or roll back (a rollback is saved as a new version).</p>
      {list.length > 1 && (
        <div className="card" style={{ display: 'block' }}>
          <div className="row" style={{ alignItems: 'flex-end' }}>
            <Field label="From">{id => <select id={id} className="input" value={from} onChange={e => setFrom(Number(e.target.value))}>{list.map(v => <option key={v.versionNo} value={v.versionNo}>Version {v.versionNo}</option>)}</select>}</Field>
            <Field label="To">{id => <select id={id} className="input" value={to} onChange={e => setTo(Number(e.target.value))}>{list.map(v => <option key={v.versionNo} value={v.versionNo}>Version {v.versionNo}</option>)}</select>}</Field>
            <button className="btn primary" style={{ marginBottom: 14 }} onClick={() => void compare()} disabled={busy || from === to}>Compare</button>
          </div>
        </div>
      )}
      {diff && <DiffView diff={diff} />}
      {list.map(v => (
        <div key={v.versionNo} className="card" style={{ display: 'block' }}>
          <div className="row">
            <b>Version {v.versionNo}{v.versionNo === flow.currentVersionNo ? ' (current)' : ''}</b>
            {v.isConfirmed && <Pill tone="good">Was confirmed</Pill>}
            <span style={{ flex: 1 }} />
            {isLeadIn(flow.projectId) && v.versionNo !== flow.currentVersionNo && <button className="btn" disabled={busy} onClick={() => setRollback(v.versionNo)}>Roll back to this version</button>}
          </div>
          <div className="muted small">{v.source} · {v.authorName} · {formatWhen(v.createdAt)}</div>
          {v.changeSummary && <p style={{ marginTop: 4 }}>{v.changeSummary}</p>}
        </div>
      ))}
      {rollback !== null && (
        <ConfirmDialog title={`Roll back to version ${rollback}?`} okText="Roll back"
          message="A new version with that content is created. If the flow was confirmed, it goes back to review."
          onClose={() => setRollback(null)} onConfirm={() => void doRollback(rollback)} />
      )}
    </>
  )
}
