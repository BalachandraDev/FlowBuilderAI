import { vi } from 'vitest'

type Handler = (req: { url: URL; method: string; body: unknown; headers: Headers }) => Response | object | undefined | Promise<Response | object | undefined>
export interface Call { method: string; path: string; body: unknown; headers: Headers }

/** Replaces fetch with a fake server. Handlers are matched by "METHOD /path" (the part after /api/v1/); an unmatched call looks like a dead network. */
export function mockApi(routes: Record<string, Handler>) {
  const calls: Call[] = []
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, 'http://localhost')
    const method = (init?.method ?? 'GET').toUpperCase()
    const headers = new Headers(init?.headers)
    const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined
    const path = url.pathname.replace(/^\/api\/v1\//, '')
    calls.push({ method, path, body, headers })
    const key = Object.keys(routes).find(k => {
      const [m, p] = k.split(' ')
      const slashed = '/' + path
      return m === method && (p.endsWith('*') ? slashed.startsWith(p.slice(0, -1)) : p === slashed)
    })
    if (!key) throw new TypeError('Failed to fetch')
    const out = await routes[key]({ url, method, body, headers })
    if (out instanceof Response) return out
    if (out === undefined) return new Response(null, { status: 204 })
    return new Response(JSON.stringify(out), { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
  vi.stubGlobal('fetch', fn)
  return { calls, fetch: fn, count: (method: string, path: string) => calls.filter(c => c.method === method && c.path === path).length }
}

export const problem = (status: number, title: string, detail?: string, extra: object = {}) =>
  new Response(JSON.stringify({ status, title, detail, ...extra }), { status, headers: { 'Content-Type': 'application/problem+json' } })

export const tokens = (over: Partial<{ access: string; refresh: string; name: string; id: string; roles: string[]; projects: { projectId: string; projectCode: string; projectRole: string }[] }> = {}) => ({
  accessToken: over.access ?? 'access-1',
  accessTokenExpiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
  refreshToken: over.refresh ?? 'refresh-1',
  user: {
    id: over.id ?? 'u-1', email: 'lead@demo.local', displayName: over.name ?? 'Asha Lead', roles: over.roles ?? ['ImplementationLead'],
    projects: over.projects ?? [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Lead' }],
  },
})
