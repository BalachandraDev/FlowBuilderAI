import { useState, type FormEvent } from 'react'
import { Navigate } from 'react-router-dom'
import { FilePdf, Microphone, TreeStructure } from '@phosphor-icons/react'
import { ApiError, describeError } from '../api/client'
import { Alert, Field } from '../components/ui'
import { useSession } from '../state/session'

export function Login() {
  const { user, signIn } = useSession()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  if (user) return <Navigate to="/" replace />

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true); setError(null)
    try { await signIn(email.trim(), password) }
    catch (err) { setError(err instanceof ApiError && err.status === 401 ? 'The email or password is incorrect.' : describeError(err)) }
    finally { setBusy(false) }
  }

  return (
    <div className="login">
      <aside className="login-art" aria-hidden="true">
        <span className="brand"><span className="brand-mark">b</span><span>bbflowbuild</span></span>
        <div>
          <h2>From what a department tells you to a clear implementation document.</h2>
          <p>Capture how a hospital department works, let the AI structure it, and hand the team a workflow and a document they can build from.</p>
          <ul className="login-points">
            <li><Microphone size={20} weight="duotone" />Type, speak or photograph: every note is kept, even offline.</li>
            <li><TreeStructure size={20} weight="duotone" />The workflow is drawn for you, step by step.</li>
            <li><FilePdf size={20} weight="duotone" />One document per request, per module, or for the whole project.</li>
          </ul>
        </div>
        <span className="login-foot">{import.meta.env.VITE_DEMO === 'true' ? 'FlowBuilderAI · browser demo with sample data' : 'AOSTA INDIA PRIVATE LIMITED · BACKBONE'}</span>
      </aside>
      <main className="login-panel">
        <form className="login-card" onSubmit={submit} noValidate>
          <div className="login-mark" aria-hidden="true">F</div>
          <h1>Welcome to bbflowbuild</h1>
          <p className="lead">Turn what a department tells you into a clear workflow and an implementation document.</p>
          <Field label="Email">{id => <input id={id} className="input" type="email" autoComplete="username" inputMode="email" autoFocus value={email} onChange={e => setEmail(e.target.value)} disabled={busy} />}</Field>
          <Field label="Password">{id => <input id={id} className="input" type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} disabled={busy} />}</Field>
          {error && <Alert tone="error">{error}</Alert>}
          <button className="btn primary lg block" type="submit" disabled={busy || !email.trim() || !password}>{busy ? 'Signing in…' : 'Sign in'}</button>
          <p className="muted small" style={{ marginTop: 16 }}>Your administrator gave you your email and password. Signing in needs a connection.</p>
        </form>
      </main>
    </div>
  )
}