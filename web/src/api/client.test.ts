import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, OfflineError, api, auth, describeError, get, post } from './client'
import { mockApi, problem, tokens } from '../test/mockApi'

beforeEach(() => { auth.clear(); vi.unstubAllGlobals() })

describe('sending requests', () => {
  it('adds the access token, a correlation id, and a key that makes a retry safe when asked', async () => {
    const server = mockApi({ 'POST /thing': () => ({ ok: true }) })
    auth.setTokens(tokens({ access: 'abc' }))
    await api('thing', { method: 'POST', body: { a: 1 }, idempotent: true })
    const { headers, body } = server.calls[0]
    expect(headers.get('Authorization')).toBe('Bearer abc')
    expect(headers.get('X-Correlation-Id')).toMatch(/^[0-9a-f]{32}$/)
    expect(headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)
    expect(body).toEqual({ a: 1 })
  })

  it('sends the row version as If-Match so a stale edit is refused', async () => {
    const server = mockApi({ 'PUT /entries/e1': () => ({}) })
    auth.setTokens(tokens())
    await api('entries/e1', { method: 'PUT', body: {}, ifMatch: 'v7' })
    expect(server.calls[0].headers.get('If-Match')).toBe('"v7"')
  })

  it('puts query values in the address and skips the ones that are not set', async () => {
    const server = mockApi({ 'GET /audit': () => ({}) })
    auth.setTokens(tokens())
    await get('audit', { projectId: 'p 1', page: 2, format: undefined })
    expect(server.fetch.mock.calls[0][0]).toContain('/api/v1/audit?projectId=p%201&page=2')
  })
})

describe('errors', () => {
  it('turns problem details into an ApiError with a message a person can read', async () => {
    mockApi({
      'GET /a': () => problem(422, 'The request cannot be completed.', 'Confirm the flow first.', { code: 'not_confirmed' }),
      'GET /b': () => problem(400, 'The request is not valid.', undefined, { errors: { Email: ['Email is required.', 'Email is required.'], Name: ['Name is too short.'] } }),
      'GET /c': () => problem(429, 'Too many'),
    })
    auth.setTokens(tokens())
    const a = await get('a').catch(e => e)
    expect(a).toBeInstanceOf(ApiError)
    expect(a).toMatchObject({ status: 422, code: 'not_confirmed' })
    expect(describeError(a)).toBe('Confirm the flow first.')
    expect(describeError(await get('b').catch(e => e))).toBe('Email is required. Name is too short.')
    expect(describeError(await get('c').catch(e => e))).toBe('Too many requests. Try again in a minute.')
  })

  it('reports a dead network as an OfflineError, not a crash', async () => {
    mockApi({})
    auth.setTokens(tokens())
    const e = await get('anything').catch(x => x)
    expect(e).toBeInstanceOf(OfflineError)
    expect(describeError(e)).toContain('saved on this device')
  })

  it('does not show technical details for unexpected errors', () => {
    expect(describeError(new Error('TypeError: x is undefined at line 5'))).toBe('Something went wrong. Please try again.')
  })

  it('copes with an error page that is not JSON (a proxy, for example)', async () => {
    mockApi({ 'GET /x': () => new Response('<html>Bad gateway</html>', { status: 502, statusText: 'Bad Gateway' }) })
    auth.setTokens(tokens())
    const e = await get('x').catch((x: unknown) => x) as ApiError
    expect(e).toMatchObject({ status: 502 })
    expect(e.message).toBe('Bad Gateway')
  })
})

describe('keeping you signed in', () => {
  it('refreshes once and retries when the access token was rejected', async () => {
    let first = true
    const server = mockApi({
      'GET /projects': ({ headers }) => (headers.get('Authorization') === 'Bearer access-2' ? [{ id: 'p' }] : problem(401, 'expired')),
      'POST /auth/refresh': () => { first = false; return tokens({ access: 'access-2', refresh: 'refresh-2' }) },
    })
    auth.setTokens(tokens({ access: 'access-1', refresh: 'refresh-1' }))
    expect(await get('projects')).toEqual([{ id: 'p' }])
    expect(first).toBe(false)
    expect(server.count('POST', 'auth/refresh')).toBe(1)
    expect(auth.refreshToken).toBe('refresh-2')                           // the server rotates refresh tokens, the new one is kept
  })

  it('shares one refresh between requests that fail together (a second use of a rotated token would end the session)', async () => {
    const server = mockApi({
      'GET /a': ({ headers }) => (headers.get('Authorization') === 'Bearer access-2' ? { a: 1 } : problem(401, 'expired')),
      'GET /b': ({ headers }) => (headers.get('Authorization') === 'Bearer access-2' ? { b: 1 } : problem(401, 'expired')),
      'POST /auth/refresh': async () => { await new Promise(r => setTimeout(r, 30)); return tokens({ access: 'access-2', refresh: 'refresh-2' }) },
    })
    auth.setTokens(tokens())
    const [a, b] = await Promise.all([get('a'), get('b')])
    expect([a, b]).toEqual([{ a: 1 }, { b: 1 }])
    expect(server.count('POST', 'auth/refresh')).toBe(1)
  })

  it('signs out when the refresh token is refused, and keeps the session when it is merely offline', async () => {
    mockApi({ 'GET /projects': () => problem(401, 'expired'), 'POST /auth/refresh': () => problem(401, 'revoked') })
    auth.setTokens(tokens())
    const seen: boolean[] = []
    auth.subscribe(v => seen.push(v))
    await get('projects').catch(() => undefined)
    expect(auth.refreshToken).toBeNull()
    expect(seen.at(-1)).toBe(false)

    mockApi({})                                                            // no network at all
    auth.setTokens(tokens({ refresh: 'keep-me' }))
    expect(await auth.refresh()).toBe(false)
    expect(auth.refreshToken).toBe('keep-me')
  })

  it('never tries to refresh while signing in', async () => {
    const server = mockApi({ 'POST /auth/login': () => problem(401, 'Authentication failed.', 'Wrong password') })
    auth.setTokens(tokens())
    await post('auth/login', { email: 'a', password: 'b' }).catch(() => undefined)
    expect(server.count('POST', 'auth/refresh')).toBe(0)
  })

  it('refreshes before the access token runs out, not after', async () => {
    const server = mockApi({ 'GET /x': () => ({}), 'POST /auth/refresh': () => tokens({ access: 'fresh', refresh: 'r2' }) })
    auth.setTokens({ ...tokens(), accessTokenExpiresAt: new Date(Date.now() + 10_000).toISOString() })   // 10 s left
    await get('x')
    expect(server.count('POST', 'auth/refresh')).toBe(1)
    expect(server.calls.find(c => c.path === 'x')!.headers.get('Authorization')).toBe('Bearer fresh')
  })
})
