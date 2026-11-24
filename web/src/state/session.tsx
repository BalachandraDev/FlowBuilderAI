import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, auth } from '../api/client'
import type { TokenResponse, UserProfile } from '../api/types'
import { uuid } from '../lib/uuid'

interface SessionValue {
  /** False until we know whether a saved sign-in is still good, so the first screen is never a flash of the sign-in page. */
  ready: boolean
  user: UserProfile | null
  /** An administrator makes users and sets up the AI. Everybody else is a user: they make projects and see every document. */
  isAdmin: boolean
  signIn: (email: string, password: string) => Promise<void>
  signOut: () => Promise<void>
  roleIn: (projectId: string) => string | undefined
  isLeadIn: (projectId: string) => boolean
  canCaptureIn: (projectId: string) => boolean
}

const Ctx = createContext<SessionValue | null>(null)

export function SessionProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserProfile | null>(null)
  const [ready, setReady] = useState(false)
  const qc = useQueryClient()

  useEffect(() => {
    auth.onTokens = t => setUser(t.user)
    const off = auth.subscribe(signedIn => { if (!signedIn) { setUser(null); qc.clear() } })
    let cancelled = false
    ;(async () => {
      if (auth.refreshToken) {
        // a saved sign-in: swap it for fresh tokens (which also returns the profile)
        const ok = await auth.refresh()
        if (!ok && auth.refreshToken) {
          // offline: show the app with the saved profile if we have one, otherwise the person signs in again later
          try { const me = await api<UserProfile>('auth/me'); if (!cancelled) setUser(me) } catch { /* stay signed out in the UI, keep the token */ }
        }
      }
      if (!cancelled) setReady(true)
    })()
    return () => { cancelled = true; off(); auth.onTokens = undefined }
  }, [qc])

  const signIn = useCallback(async (email: string, password: string) => {
    const t = await api<TokenResponse>('auth/login', { method: 'POST', body: { email, password, deviceId: deviceId() } })
    auth.setTokens(t)
  }, [])

  const signOut = useCallback(async () => {
    const refreshToken = auth.refreshToken
    try { if (refreshToken) await api('auth/logout', { method: 'POST', body: { refreshToken } }) } catch { /* signing out must work offline too */ }
    auth.clear()
  }, [])

  const value = useMemo<SessionValue>(() => {
    const roleIn = (id: string) => user?.projects.find(p => p.projectId === id)?.projectRole
    return {
      ready, user, isAdmin: !!user?.roles.includes('Admin'), signIn, signOut, roleIn,
      isLeadIn: id => roleIn(id) === 'Lead',
      canCaptureIn: id => ['Lead', 'Executive', 'Collaborator'].includes(roleIn(id) ?? ''),
    }
  }, [ready, user, signIn, signOut])

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useSession(): SessionValue {
  const v = useContext(Ctx)
  if (!v) throw new Error('useSession must be used inside SessionProvider')
  return v
}

/** An id for this browser, so the server can tell devices apart in its audit trail. */
function deviceId(): string {
  try {
    let id = localStorage.getItem('fc.device')
    if (!id) { id = 'web-' + uuid().replace(/-/g, '').slice(0, 10); localStorage.setItem('fc.device', id) }
    return id
  } catch { return 'web-unknown' }
}
