import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowsLeftRight, CheckCircle, Database as DatabaseIcon, PlugsConnected } from '@phosphor-icons/react'
import { api, ApiError, describeError, get } from '../../api/client'
import type { DbInfo, DbMove, DbTest, DbUse } from '../../api/types'
import { Alert, ConfirmDialog, Field, Pill, Skeletons, useToast } from '../../components/ui'

const problem = (e: unknown) => (e instanceof ApiError && e.errors ? Object.values(e.errors).flat().join(' ') : describeError(e))

/**
 * Which SQL Server the app uses, and moving it to another one: test the server, copy the data (making any missing table, skipping rows that
 * are already there or cannot be saved, and carrying on), then make the app use it. The password is sent to the server for the test and the
 * copy only; it is never shown again and never saved in the clear.
 */
export function DatabasePage() {
  const qc = useQueryClient()
  const toast = useToast()
  const info = useQuery({ queryKey: ['admin-database'], queryFn: () => get<DbInfo>('admin/database') })
  const [server, setServer] = useState('')
  const [database, setDatabase] = useState('FlowCapture')
  const [windows, setWindows] = useState(false)
  const [user, setUser] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState<'' | 'test' | 'move' | 'use'>('')
  const [error, setError] = useState<string | null>(null)
  const [test, setTest] = useState<DbTest | null>(null)
  const [move, setMove] = useState<DbMove | null>(null)
  const [asking, setAsking] = useState<'use' | 'forget' | null>(null)
  const [done, setDone] = useState<DbUse | null>(null)

  const details = { server: server.trim(), database: database.trim(), user: user.trim(), password, windowsLogin: windows }
  const filled = details.server && details.database && (windows || (details.user && password))
  const running = move?.state === 'running'

  // while a move runs, ask how far it is
  useEffect(() => {
    if (!running) return
    const t = setInterval(() => { void get<DbMove>('admin/database/move').then(setMove).catch(() => undefined) }, 1500)
    return () => clearInterval(t)
  }, [running])
  // a move that was started earlier (the page was closed) is picked up again
  useEffect(() => { void get<DbMove>('admin/database/move').then(m => { if (m.state !== 'idle') setMove(m) }).catch(() => undefined) }, [])

  async function testIt() {
    setBusy('test'); setError(null); setTest(null); setDone(null)
    try { setTest(await api<DbTest>('admin/database/test', { method: 'POST', body: details })) } catch (e) { setError(problem(e)) } finally { setBusy('') }
  }
  async function moveIt() {
    setBusy('move'); setError(null); setDone(null)
    try { setMove(await api<DbMove>('admin/database/move', { method: 'POST', body: details })) } catch (e) { setError(problem(e)) } finally { setBusy('') }
  }
  async function useIt(forget: boolean) {
    setBusy('use'); setError(null)
    try {
      const r = await api<DbUse>('admin/database/use', { method: forget ? 'DELETE' : 'POST', body: forget ? undefined : {} })
      setDone(r); void qc.invalidateQueries({ queryKey: ['admin-database'] })
      toast(forget ? 'The saved server was removed.' : 'The new server was saved.', 'good')
    } catch (e) { setError(problem(e)) } finally { setBusy(''); setAsking(null) }
  }

  const totals = move ? move.tables.reduce((a, t) => ({ inserted: a.inserted + t.inserted, there: a.there + t.alreadyThere, failed: a.failed + t.failed }), { inserted: 0, there: 0, failed: 0 }) : null

  return (
    <>
      <div className="page-title">
        <div><h1>Database</h1><p className="muted">Move the app to another SQL Server. Missing tables are made, the data is copied across, and a row that cannot be saved is skipped and reported without stopping the rest. Nothing is deleted or changed on the server the app uses now.</p></div>
      </div>

      {info.isPending && <Skeletons count={1} />}
      {info.isError && <Alert tone="error">{describeError(info.error)}</Alert>}
      {info.data && (
        <section className="panel" aria-label="The server in use">
          <div className="row"><DatabaseIcon size={26} weight="duotone" aria-hidden /><div><b>The app uses: {info.data.server}{info.data.database ? ` / ${info.data.database}` : ''}</b><div className="muted small">{info.data.provider}{info.data.usingSavedServer ? ' · the server chosen on this page' : ' · the server the app was installed with'}</div></div></div>
          {info.data.problem && <Alert tone="warn">{info.data.problem}</Alert>}
          {info.data.usingSavedServer && <div className="row"><button className="btn" disabled={busy !== ''} onClick={() => setAsking('forget')}>Go back to the server the app was installed with</button></div>}
        </section>
      )}

      <section className="panel" aria-labelledby="h-newserver">
        <h2 id="h-newserver"><span className="num">1</span>The new server</h2>
        <div className="limit-grid">
          <Field label="Server address" help="For example 10.0.0.5,1433 or SERVER\INSTANCE">{id => <input id={id} className="input" value={server} onChange={e => setServer(e.target.value)} autoComplete="off" spellCheck={false} />}</Field>
          <Field label="Database name" help="Made if it is not on the server.">{id => <input id={id} className="input" value={database} onChange={e => setDatabase(e.target.value)} autoComplete="off" spellCheck={false} />}</Field>
          <Field label="User name">{id => <input id={id} className="input" value={user} onChange={e => setUser(e.target.value)} disabled={windows} autoComplete="off" spellCheck={false} />}</Field>
          <Field label="Password" help="Used for the test and the copy only. It is not shown again.">{id => <input id={id} className="input" type="password" value={password} onChange={e => setPassword(e.target.value)} disabled={windows} autoComplete="new-password" />}</Field>
        </div>
        <label className="check switch"><input type="checkbox" checked={windows} onChange={e => setWindows(e.target.checked)} /><span><b>Use the Windows account the app runs as</b><br /><span className="muted small">For a server inside the hospital network. No user name or password is needed.</span></span></label>
        {error && <Alert tone="error">{error}</Alert>}
        <div className="row">
          <button className="btn" disabled={!filled || busy !== '' || running} onClick={() => void testIt()}><PlugsConnected size={18} aria-hidden />{busy === 'test' ? 'Testing…' : 'Test the connection'}</button>
        </div>
        {test && (
          <div className="stack" role="status" aria-label="Test result">
            <Alert tone={test.ok ? 'good' : 'error'}>{test.message}</Alert>
            {test.ok && test.missingTables.length > 0 && <div className="muted small">To be made: {test.missingTables.join(', ')}</div>}
            {test.ok && test.existingTables.length > 0 && <div className="muted small">Already there, kept: {test.existingTables.join(', ')}</div>}
          </div>
        )}
      </section>

      <section className="panel" aria-labelledby="h-copy">
        <h2 id="h-copy"><span className="num">2</span>Copy the data</h2>
        <p className="muted">Makes the missing tables on the new server and adds every row it does not have yet. Rows that are already there are left alone. Voice notes, pictures and documents are files, not database rows: they stay where the app keeps them.</p>
        <div className="row">
          <button className="btn primary" disabled={!filled || !test?.ok || busy !== '' || running} onClick={() => void moveIt()}><ArrowsLeftRight size={18} aria-hidden />{running ? 'Copying…' : 'Make the tables and copy the data'}</button>
        </div>
        {move && (
          <div className="stack" aria-label="Copy progress" aria-live="polite">
            <div className="row">
              <Pill tone={move.state === 'done' ? 'good' : move.state === 'failed' ? 'bad' : 'progress'}>{move.state === 'done' ? 'Finished' : move.state === 'failed' ? 'Stopped' : 'Running'}</Pill>
              <span className="muted small">{move.step}{move.target ? ` · ${move.target}` : ''}</span>
            </div>
            {move.notes.map(n => <div key={n} className="muted small">{n}</div>)}
            {totals && move.tables.length > 0 && (
              <>
                <div className="table-wrap">
                  <table className="plain" aria-label="Rows copied">
                    <thead><tr><th>Table</th><th className="num-col">In the app now</th><th className="num-col">Added</th><th className="num-col">Already there</th><th className="num-col">Skipped</th></tr></thead>
                    <tbody>
                      {move.tables.filter(t => t.sourceRows > 0 || t.failed > 0).map(t => (
                        <tr key={t.table} className={t.failed ? 'failed' : ''}>
                          <td><b>{t.table}</b>{t.errors.map(e => <div key={e} className="muted small">{e}</div>)}</td>
                          <td className="num-col">{t.sourceRows.toLocaleString('en-IN')}</td><td className="num-col">{t.inserted.toLocaleString('en-IN')}</td>
                          <td className="num-col">{t.alreadyThere.toLocaleString('en-IN')}</td><td className="num-col">{t.failed ? <span className="tag-bad">{t.failed}</span> : 0}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                {move.state !== 'running' && <div className="muted small">{totals.inserted.toLocaleString('en-IN')} rows added, {totals.there.toLocaleString('en-IN')} were already there, {totals.failed} skipped.</div>}
              </>
            )}
          </div>
        )}
      </section>

      <section className="panel" aria-labelledby="h-use">
        <h2 id="h-use"><span className="num">3</span>Use the new server</h2>
        <p className="muted">When the copy has finished, make the app use the new server from its next start. The old server stays as it is, so you can go back.</p>
        <div className="row">
          <button className="btn primary" disabled={!(move?.canUse || test?.ok) || busy !== '' || running} onClick={() => setAsking('use')}><CheckCircle size={18} aria-hidden />Use this server from now on</button>
        </div>
        {!move?.canUse && test?.ok && <div className="muted small">The data has not been copied yet. You can still switch, and the new server will start with what it has.</div>}
        {done && <Alert tone="good">{done.message}</Alert>}
      </section>

      {asking === 'use' && <ConfirmDialog title="Use the new server?" okText="Use it" onClose={() => setAsking(null)} onConfirm={() => void useIt(false)}
        message="The app will use the new server from its next start. Anything added after the copy stays on the old server. Make sure the copy has finished first." />}
      {asking === 'forget' && <ConfirmDialog title="Go back to the original server?" okText="Go back" danger onClose={() => setAsking(null)} onConfirm={() => void useIt(true)}
        message="The app will use the server it was installed with from its next start. Anything added since the move stays on the other server." />}
    </>
  )
}
