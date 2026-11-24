import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { ArrowLeft } from '@phosphor-icons/react'
import { ApiError, auth, describeError, post } from '../api/client'
import { keys, useClients, useProjects } from '../api/hooks'
import type { QuickProject } from '../api/types'
import { ErrorState } from '../components/ErrorState'
import { Alert, ConfirmDialog, Field, Skeletons } from '../components/ui'
import { usePref } from '../ux/prefs'
import { useUnsavedWarning } from '../ux/useUnsavedWarning'

const NAME_MAX = 120
const ABOUT_MAX = 500

/** Which client, and what is it called. Everything else (notes, analysis, the guide) happens on the project page. */
export function NewProject() {
  const navigate = useNavigate()
  const qc = useQueryClient()
  const clients = useClients()
  const projects = useProjects()
  const [lastClient, setLastClient] = usePref<string>('new-project.client', '')
  const [name, setName] = useState('')
  const [about, setAbout] = useState('')
  const [picked, setPicked] = useState('')
  const [touched, setTouched] = useState({ name: false, client: false })
  const [busy, setBusy] = useState(false)
  const [leaving, setLeaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const options = clients.data ?? []
  // with one client there is nothing to choose; with several, the one used last is already chosen
  const remembered = options.some(c => c.id === lastClient) ? lastClient : ''
  const clientId = options.length === 1 ? options[0].id : picked || remembered
  useEffect(() => { if (!picked && remembered) setPicked(remembered) }, [remembered])   // eslint-disable-line react-hooks/exhaustive-deps

  const dirty = (name.trim() !== '' || about.trim() !== '') && !busy
  useUnsavedWarning(dirty)

  const nameError = touched.name && !name.trim() ? 'Give the project a name.' : null
  const clientError = touched.client && !clientId ? 'Choose the client this project is for.' : null
  const duplicate = !!name.trim() && (projects.data ?? []).some(p => p.hospitalName.trim().toLowerCase() === name.trim().toLowerCase() && (!clientId || p.clientId === clientId))

  async function submit(e: FormEvent) {
    e.preventDefault()
    setTouched({ name: true, client: true })
    if (!name.trim() || !clientId || busy) return
    setBusy(true); setError(null)
    try {
      const created = await post<QuickProject>('projects/quick', { name: name.trim(), description: about.trim() || null, clientId }, { idempotent: true })
      setLastClient(clientId)
      // what a person may do in a project comes with their sign-in, so ask for a fresh one that already knows about this project
      await auth.refresh()
      await qc.invalidateQueries({ queryKey: keys.projects })
      navigate(`/p/${created.projectId}`, { replace: true })
    } catch (err) {
      setError(err instanceof ApiError && err.errors ? Object.values(err.errors).flat().join(' ') : describeError(err))
      setBusy(false)
    }
  }
  const cancel = () => { if (dirty) setLeaving(true); else navigate('/') }

  return (
    <div className="narrow">
      <Link to="/" className="back"><ArrowLeft size={16} aria-hidden />All projects</Link>
      <h1>New project</h1>
      <p className="muted">Name the workflow you are about to capture. For example the department and the process: <i>Pharmacy – returns</i> or <i>IP discharge</i>.</p>
      {clients.isPending && <Skeletons count={1} />}
      {clients.isError && <ErrorState error={clients.error} onRetry={() => void clients.refetch()} retrying={clients.isFetching} />}
      {clients.data && options.length === 0 && (
        <div className="empty" role="status"><b>You are not assigned to a client yet</b><div>Ask an administrator to assign you to a client. Then you can start projects for it here.</div></div>
      )}
      {options.length > 0 && <form className="panel" onSubmit={submit} noValidate>
        <Field label="Client" required={options.length > 1} error={clientError} help={options.length === 1 ? 'The only client you work for.' : 'Which customer is this project for?'}>
          {(id, aria) => options.length === 1
            ? <input id={id} className="input" value={options[0].name} readOnly />
            : <select id={id} className="input" value={picked} onChange={e => setPicked(e.target.value)} onBlur={() => setTouched(t => ({ ...t, client: true }))} disabled={busy} {...aria}>
                <option value="">Choose a client…</option>
                {options.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>}
        </Field>
        <Field label="Project name" required error={nameError}>
          {(id, aria) => (
            <>
              <input id={id} className="input big" autoFocus value={name} onChange={e => setName(e.target.value)} onBlur={() => setTouched(t => ({ ...t, name: true }))} maxLength={NAME_MAX} disabled={busy} placeholder="IP discharge" {...aria} />
              <div className="field-count" aria-hidden>{name.length}/{NAME_MAX}</div>
              {duplicate && <div className="field-warn" role="status">You already have a project with this name. You can still make another.</div>}
            </>
          )}
        </Field>
        <Field label="What is it about? (optional)" help="A line for the people who read it later, such as the hospital and the department.">
          {id => (
            <>
              <textarea id={id} className="input" rows={3} value={about} onChange={e => setAbout(e.target.value)} maxLength={ABOUT_MAX} disabled={busy} />
              <div className="field-count" aria-hidden>{about.length}/{ABOUT_MAX}</div>
            </>
          )}
        </Field>
        {error && <Alert tone="error">{error}</Alert>}
        <div className="row">
          <button className="btn primary lg" type="submit" disabled={busy || !name.trim() || !clientId}>{busy ? 'Creating…' : 'Create and add notes'}</button>
          <button className="btn lg" type="button" onClick={cancel} disabled={busy}>Cancel</button>
        </div>
      </form>}
      {leaving && <ConfirmDialog title="Leave without creating the project?" message="What you typed will be lost." okText="Leave" danger onClose={() => setLeaving(false)} onConfirm={() => navigate('/')} />}
    </div>
  )
}
