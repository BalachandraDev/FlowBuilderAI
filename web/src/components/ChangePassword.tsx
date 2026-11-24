import { useState } from 'react'
import { api, ApiError, auth, describeError } from '../api/client'
import type { TokenResponse } from '../api/types'
import { PasswordInput } from './PasswordInput'
import { Alert, Dialog, Field, useToast } from './ui'

/** The password rules, the same as the server's: at least 10 characters with letters and digits. */
export const passwordProblem = (p: string): string | null =>
  p.length < 10 ? 'Use at least 10 characters.' : !/[A-Za-z]/.test(p) || !/\d/.test(p) ? 'Use letters and digits together.' : null

/**
 * Everybody changes their own password here, administrator or user. The current password is asked for again. Other phones and browsers
 * signed in as the same person are signed out; this one stays signed in.
 */
export function ChangePasswordDialog({ onClose }: { onClose: () => void }) {
  const toast = useToast()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [again, setAgain] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const weak = next ? passwordProblem(next) : null
  const mismatch = again !== '' && again !== next
  const ok = current !== '' && next !== '' && !weak && again === next && next !== current

  async function save() {
    setBusy(true); setError(null)
    try {
      const t = await api<TokenResponse>('auth/change-password', { method: 'POST', body: { currentPassword: current, newPassword: next } })
      auth.setTokens(t)                                        // this sign-in carries on with the new password
      toast('Your password was changed.', 'good')
      onClose()
    } catch (e) {
      setError(e instanceof ApiError && e.errors ? Object.values(e.errors).flat().join(' ') : describeError(e))
      setBusy(false)
    }
  }

  const rules: [string, boolean][] = [['At least 10 characters', next.length >= 10], ['Has letters', /[A-Za-z]/.test(next)], ['Has digits', /\d/.test(next)], ['The two match', next !== '' && again === next]]
  const same = next !== '' && next === current ? 'The new password must be different from the current one.' : null
  return (
    <Dialog title="Change your password" onClose={onClose} actions={<>
      <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
      <button className="btn primary" onClick={() => void save()} disabled={busy || !ok}>{busy ? 'Saving…' : 'Change password'}</button>
    </>}>
      <form onSubmit={e => { e.preventDefault(); if (ok && !busy) void save() }}>
        <Field label="Current password" required>{(id, aria) => <PasswordInput id={id} autoComplete="current-password" autoFocus value={current} onChange={e => setCurrent(e.target.value)} {...aria} />}</Field>
        <Field label="New password" required error={same} help={weak ?? 'At least 10 characters, with letters and digits.'}>
          {(id, aria) => (
            <>
              <PasswordInput id={id} autoComplete="new-password" value={next} onChange={e => setNext(e.target.value)} {...aria} aria-invalid={!!weak || aria['aria-invalid']} />
              <ul className="checklist" aria-label="Requirements">{rules.slice(0, 3).map(([text, met]) => <li key={text} className={met ? 'ok' : ''}>{text}<span className="sr-only">{met ? ' (met)' : ' (not met yet)'}</span></li>)}</ul>
            </>
          )}
        </Field>
        <Field label="New password again" required error={mismatch ? 'The two do not match.' : null}>
          {(id, aria) => <PasswordInput id={id} autoComplete="new-password" value={again} onChange={e => setAgain(e.target.value)} {...aria} />}
        </Field>
        <button type="submit" hidden tabIndex={-1} aria-hidden>Change password</button>
        <p className="muted small">Any other phone or computer signed in as you is signed out.</p>
        {error && <Alert tone="error">{error}</Alert>}
      </form>
    </Dialog>
  )
}