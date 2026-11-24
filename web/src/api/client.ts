import type { TokenResponse } from './types'
import { uuid } from '../lib/uuid'

/** The API answered with an error (RFC 7807 problem details). */
export class ApiError extends Error {
  status: number
  code?: string
  errors?: Record<string, string[]>
  serverCopy?: unknown
  constructor(status: number, title: string, detail?: string, code?: string, errors?: Record<string, string[]>, serverCopy?: unknown) {
    super(detail ?? title)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.errors = errors
    this.serverCopy = serverCopy
  }
  /** Text that is safe to show to the person using the app. */
  get userMessage(): string {
    if (this.errors && Object.keys(this.errors).length > 0) return [...new Set(Object.values(this.errors).flat())].slice(0, 5).join(' ')
    return this.message
  }
}

/** The server could not be reached: no network, a dropped connection, a timeout. Work is kept and retried. */
export class OfflineError extends Error {
  constructor(message = 'The server cannot be reached. Your work is saved on this device.') {
    super(message)
    this.name = 'OfflineError'
  }
}

export function describeError(e: unknown): string {
  if (e instanceof ApiError) return e.userMessage
  if (e instanceof OfflineError) return e.message
  if (e instanceof Error && e.name === 'AbortError') return 'Cancelled.'
  return 'Something went wrong. Please try again.'
}

const REFRESH_KEY = 'fc.refresh'
const BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? ''

type SessionListener = (signedIn: boolean) => void

/** Holds the sign-in. The short-lived access token stays in memory; the refresh token is kept so a reload does not sign you out. */
class Auth {
  private access: string | null = null
  private expiresAt = 0
  private refreshing: Promise<boolean> | null = null
  private listeners = new Set<SessionListener>()
  onTokens?: (t: TokenResponse) => void

  subscribe(l: SessionListener): () => void { this.listeners.add(l); return () => this.listeners.delete(l) }
  private notify(v: boolean) { this.listeners.forEach(l => l(v)) }

  get refreshToken(): string | null { try { return localStorage.getItem(REFRESH_KEY) } catch { return null } }
  private set refreshToken(v: string | null) {
    try { if (v) localStorage.setItem(REFRESH_KEY, v); else localStorage.removeItem(REFRESH_KEY) } catch { /* storage blocked: the sign-in lasts until the tab closes */ }
  }

  setTokens(t: TokenResponse) {
    this.access = t.accessToken
    this.expiresAt = Date.parse(t.accessTokenExpiresAt)
    this.refreshToken = t.refreshToken
    this.onTokens?.(t)
    this.notify(true)
  }

  clear() {
    this.access = null
    this.expiresAt = 0
    this.refreshToken = null
    this.notify(false)
  }

  get hasSession(): boolean { return this.access !== null || this.refreshToken !== null }

  /** A valid access token, refreshing first when it is about to expire. Several callers share one refresh. */
  async token(): Promise<string | null> {
    if (this.access && Date.now() < this.expiresAt - 30_000) return this.access
    if (!this.refreshToken) return this.access
    await this.refresh()
    return this.access
  }

  /** Swaps the refresh token for new tokens. The server rotates refresh tokens, so two calls at once would end the session. */
  refresh(): Promise<boolean> {
    this.refreshing ??= this.doRefresh().finally(() => { this.refreshing = null })
    return this.refreshing
  }

  private async doRefresh(): Promise<boolean> {
    const token = this.refreshToken
    if (!token) return false
    try {
      const res = await fetch(`${BASE}/api/v1/auth/refresh`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ refreshToken: token }),
      })
      if (res.ok) { this.setTokens(await res.json() as TokenResponse); return true }
      if (res.status === 401 || res.status === 403) { this.clear(); return false }
      return false
    } catch {
      return false                                     // offline: keep the session, try again later
    }
  }
}

export const auth = new Auth()

interface RequestOptions {
  method?: string
  /** JSON for most calls; FormData for a photo; a Blob for a chunk of a recording (sent as it is, with contentType). */
  body?: unknown
  contentType?: string
  query?: Record<string, string | number | boolean | undefined>
  idempotent?: boolean
  signal?: AbortSignal
  ifMatch?: string
}

async function problem(res: Response): Promise<ApiError> {
  let title = res.statusText || 'Request failed'
  let detail: string | undefined, code: string | undefined, errors: Record<string, string[]> | undefined, serverCopy: unknown
  try {
    const text = await res.text()
    if (text.trim().startsWith('{')) {
      const p = JSON.parse(text)
      title = p.title ?? title; detail = p.detail; code = p.code; errors = p.errors; serverCopy = p.serverCopy
    }
  } catch { /* not problem+json (for example a proxy page): fall back to the status text */ }
  if (res.status === 429 && !detail) detail = 'Too many requests. Try again in a minute.'
  return new ApiError(res.status, title, detail, code, errors, serverCopy)
}

function url(path: string, query?: RequestOptions['query']): string {
  const q = query ? Object.entries(query).filter(([, v]) => v !== undefined).map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&') : ''
  return `${BASE}/api/v1/${path.replace(/^\//, '')}${q ? `?${q}` : ''}`
}

async function send(path: string, o: RequestOptions, retried = false): Promise<Response> {
  const headers: Record<string, string> = { 'X-Correlation-Id': uuid().replace(/-/g, '') }
  const token = await auth.token()
  if (token) headers.Authorization = `Bearer ${token}`
  const raw = o.body instanceof FormData || o.body instanceof Blob
  if (o.body !== undefined && !(o.body instanceof FormData)) headers['Content-Type'] = o.body instanceof Blob ? (o.contentType ?? 'application/octet-stream') : 'application/json'
  if (o.idempotent) headers['Idempotency-Key'] = uuid()
  if (o.ifMatch) headers['If-Match'] = `"${o.ifMatch}"`
  let res: Response
  try {
    res = await fetch(url(path, o.query), {
      method: o.method ?? 'GET', headers, signal: o.signal,
      body: o.body === undefined ? undefined : raw ? (o.body as BodyInit) : JSON.stringify(o.body),
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new OfflineError()
  }
  if (res.status === 401 && !retried && !path.startsWith('auth/') && auth.refreshToken) {
    if (await auth.refresh()) return send(path, o, true)
  }
  return res
}

export async function api<T>(path: string, o: RequestOptions = {}): Promise<T> {
  const res = await send(path, o)
  if (!res.ok) throw await problem(res)
  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

/** Fetches a file the API sends back, for a download or a picture. */
export async function apiBlob(path: string, query?: RequestOptions['query']): Promise<{ blob: Blob; fileName: string | null }> {
  const res = await send(path, { query })
  if (!res.ok) throw await problem(res)
  const disposition = res.headers.get('Content-Disposition') ?? ''
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)
  return { blob: await res.blob(), fileName: match ? decodeURIComponent(match[1]) : null }
}

export const get = <T>(path: string, query?: RequestOptions['query']) => api<T>(path, { query })
export const post = <T>(path: string, body?: unknown, o: Omit<RequestOptions, 'method' | 'body'> = {}) => api<T>(path, { ...o, method: 'POST', body: body ?? {} })
export const put = <T>(path: string, body?: unknown, o: Omit<RequestOptions, 'method' | 'body'> = {}) => api<T>(path, { ...o, method: 'PUT', body: body ?? {} })
export const del = <T>(path: string) => api<T>(path, { method: 'DELETE' })
