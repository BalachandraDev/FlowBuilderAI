import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { renewsIn } from './pages/AiUsage'
import { clearOutbox } from './offline/outbox'
import { app, contentDto, project, signedIn, standard, usage } from './test/fixtures'
import { mockApi, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }
const asReviewer = { 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }) }
const inHours = (h: number) => new Date(Date.now() + h * 3_600_000).toISOString()
const quota = (over: object = {}) => ({ provider: 'Gemini', usedRequests: 120, usedTokens: 45_000, limitRequests: 1000, limitTokens: 250_000, resetsAt: inHours(3.5), reached: false, message: null, ...over })
const workspace = () => ({
  ...standard, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
  'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
})

describe('deleting a project', () => {
  it('lets the project lead delete it from its card, after asking, and the card goes', async () => {
    signedIn()
    let gone = false
    const server = mockApi({ ...standard, 'GET /projects': () => (gone ? [] : [project]), 'DELETE /projects/p-1': () => { gone = true; return undefined } })
    app('/')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Delete Kauvery Hospital' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/Nothing is erased from the database/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(server.count('DELETE', 'projects/p-1')).toBe(0)
    await user.click(screen.getByRole('button', { name: 'Delete Kauvery Hospital' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete project' }))
    await waitFor(() => expect(server.count('DELETE', 'projects/p-1')).toBe(1))
    expect(await screen.findByText('The project was deleted.')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Open Kauvery Hospital' })).not.toBeInTheDocument())
  })

  it('offers Delete project on the project page and goes back home afterwards', async () => {
    signedIn()
    const server = mockApi({ ...workspace(), 'DELETE /projects/p-1': () => undefined })
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Delete project/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete project' }))
    await waitFor(() => expect(server.count('DELETE', 'projects/p-1')).toBe(1))
    expect(await screen.findByRole('heading', { name: /Hello, Asha/ })).toBeInTheDocument()
  })

  it('gives someone who can only read the project no way to delete it, and the administrator one on every card', async () => {
    signedIn(); mockApi({ ...workspace(), ...asReviewer }); app('/p/p-1')
    await screen.findByRole('list', { name: 'Modules' })
    expect(screen.queryByRole('button', { name: /Delete project/ })).not.toBeInTheDocument()
  })

  it('does not show the administrator the users\' projects, and sends them to the admin pages when they try to open one', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /projects': () => [project] }); app('/p/p-1')
    expect(await screen.findByRole('heading', { name: 'Admin' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Delete Kauvery Hospital/ })).not.toBeInTheDocument()
  })
})

describe('the free allowance of each AI', () => {
  it('says how long until an allowance renews', () => {
    expect(renewsIn(inHours(3.5))).toMatch(/^3 h (2[89]|30) min$/)
    expect(renewsIn(inHours(0.2))).toMatch(/^1[12] min$/)
    expect(renewsIn(new Date(Date.now() - 1000).toISOString())).toBe('0 min')
  })

  it('shows the admin each AI with what it used today against its limit, and which one is paused', async () => {
    signedIn()
    mockApi({
      ...standard, ...asAdmin,
      'GET /ai-usage': () => usage({
        allUsers: true,
        quotas: [quota(), quota({ provider: 'Groq', usedRequests: 2000, limitRequests: 2000, limitTokens: null, usedTokens: 90_000, reached: true, message: 'The daily free limit for Groq is reached (2,000 of 2,000 requests today).' })],
        byModel: [{ provider: 'Gemini', model: 'gemini-3-flash', calls: 4, inputTokens: 18000, outputTokens: 6200, totalTokens: 24200 }, { provider: 'Groq', model: 'whisper-large-v3-turbo', calls: 9, inputTokens: 0, outputTokens: 900, totalTokens: 900 }],
      }),
    })
    app('/usage')
    const allowance = await screen.findByRole('region', { name: 'Free allowance today' })
    expect(within(allowance).getByText(/renews in 3 h/)).toBeInTheDocument()
    const bars = within(allowance).getAllByRole('progressbar')
    expect(bars[0]).toHaveAttribute('aria-valuenow', '120')
    expect(bars[0]).toHaveAttribute('aria-valuemax', '1000')
    expect(within(allowance).getByText('Limit reached: paused')).toBeInTheDocument()
    expect(within(allowance).getByText(/2,000 of 2,000 requests today/)).toBeInTheDocument()
    expect(within(allowance).getByText(/no limit set/)).toBeInTheDocument()                 // Groq has no token limit
    const models = await screen.findByRole('region', { name: 'Each AI and model' })
    expect(within(models).getByText('gemini-3-flash')).toBeInTheDocument()
    expect(within(models).getByText('whisper-large-v3-turbo')).toBeInTheDocument()
  })

  it('does not show the allowances to an ordinary user', async () => {
    signedIn(); mockApi({ ...standard, 'GET /ai-usage': () => usage({ allUsers: false, quotas: [] }) }); app('/usage')
    await screen.findByRole('heading', { name: 'My AI usage' })
    expect(screen.queryByRole('region', { name: 'Free allowance today' })).not.toBeInTheDocument()
  })

  it('lets the admin set daily limits for each AI and send only what changed', async () => {
    signedIn()
    let state = { quotas: [quota({ limitRequests: null, limitTokens: null }), quota({ provider: 'Groq', limitRequests: null, limitTokens: null })] }
    const server = mockApi({
      ...standard, ...asAdmin,
      'GET /admin/ai': () => ({ provider: 'Gemini', providers: ['Gemini', 'Anthropic', 'Demo'], hasKey: true, keyHint: '...1234', geminiModel: '', keyFromDashboard: true, ...state }),
      'PUT /admin/ai': () => { state = { quotas: [quota({ limitRequests: 1000, limitTokens: null }), quota({ provider: 'Groq', limitRequests: null, limitTokens: 250000 })] }; return { provider: 'Gemini', providers: ['Gemini', 'Anthropic', 'Demo'], hasKey: true, keyHint: '...1234', geminiModel: '', keyFromDashboard: true, ...state } },
    })
    app('/admin/ai')
    const user = userEvent.setup()
    const save = await screen.findByRole('button', { name: 'Save the limits' })
    expect(save).toBeDisabled()
    await user.type(screen.getByLabelText('Gemini: requests a day'), '1000')
    await user.type(screen.getByLabelText('Groq: tokens a day'), '250000')
    await user.type(screen.getByLabelText('Groq: requests a day'), 'abc')
    expect(screen.getByText('Use whole numbers only.')).toBeInTheDocument()
    expect(save).toBeDisabled()
    await user.clear(screen.getByLabelText('Groq: requests a day'))
    await user.click(save)
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT')?.body).toEqual({ limits: { 'Gemini.Requests': 1000, 'Groq.Tokens': 250000 } }))
    expect(await screen.findByText('The limits were saved.')).toBeInTheDocument()
    expect((screen.getByLabelText('Gemini: requests a day') as HTMLInputElement).value).toBe('1000')
  })

  it('tells the admin plainly when an AI is paused at its limit', async () => {
    signedIn()
    mockApi({
      ...standard, ...asAdmin,
      'GET /admin/ai': () => ({ provider: 'Gemini', providers: ['Gemini'], hasKey: true, keyHint: '...1', geminiModel: '', keyFromDashboard: true, quotas: [quota({ reached: true, usedRequests: 1000, message: 'The daily free limit for Gemini is reached (1,000 of 1,000 requests today). Nothing more is sent to it until it renews in 3 h 30 min.' }), quota({ provider: 'Groq' })] }),
    })
    app('/admin/ai')
    expect(await screen.findByText(/Nothing more is sent to it until it renews in 3 h 30 min/)).toBeInTheDocument()
  })
})

const modelQuota = (over: object = {}) => ({ model: 'gemini-3.8-flash', name: 'Gemini 3.8 Flash', usedToday: 27, perDay: 20, usedLastMinute: 4, perMinute: 5, tokensLastMinute: 26_390, tokensPerMinute: 250_000, reached: true, resetsAt: inHours(3.5), isPreset: true, ...over })
const adminAi = (over: object = {}) => ({ provider: 'Gemini', providers: ['Gemini', 'Anthropic', 'Demo'], hasKey: true, keyHint: '...1234', geminiModel: '', keyFromDashboard: true, quotas: [], ...over })

describe('the limits of each Gemini model', () => {
  it('tells everybody how many free requests are left today', async () => {
    signedIn()
    mockApi({ ...workspace(), 'GET /system/info': () => ({ ai: 'gemini', notice: null, capacity: { reached: false, requestsLeft: 41, resetsAt: inHours(2.2), message: null } }) })
    app('/p/p-1')
    expect(await screen.findByTestId('ai-left')).toHaveTextContent(/Free AI left today: 41 requests/)
    expect(screen.getByTestId('ai-left')).toHaveTextContent(/Renews in 2 h/)
  })

  it('warns when only a few requests are left, and says plainly when they are all used', async () => {
    signedIn()
    mockApi({ ...workspace(), 'GET /system/info': () => ({ ai: 'gemini', notice: null, capacity: { reached: false, requestsLeft: 3, resetsAt: inHours(1), message: null } }) })
    const view = app('/p/p-1')
    expect(await screen.findByText(/Only 3 free AI requests left today/)).toBeInTheDocument()
    view.unmount()

    mockApi({ ...workspace(), 'GET /system/info': () => ({ ai: 'gemini', notice: null, capacity: { reached: true, requestsLeft: 0, resetsAt: inHours(1), message: 'The free Gemini AI has used all its requests for today (Gemini 3.8 Flash: 20 of 20 requests today). It works again when the day renews; nothing is lost.' } }) })
    app('/p/p-1')
    expect(await screen.findByText(/It works again when the day renews; nothing is lost/)).toBeInTheDocument()
    expect(screen.queryByTestId('ai-left')).not.toBeInTheDocument()
  })

  it('shows the admin every model against its own limits and saves only what was changed, an empty box meaning no limit', async () => {
    signedIn()
    let state = { modelQuotas: [modelQuota(), modelQuota({ model: 'gemini-3.5-flash-lite', name: 'Gemini 3.5 Flash Lite', usedToday: 12, perDay: 500, perMinute: 15, reached: false })] }
    const server = mockApi({
      ...standard, ...asAdmin,
      'GET /admin/ai': () => adminAi(state),
      'PUT /admin/ai': () => { state = { modelQuotas: [modelQuota({ perDay: 30, perMinute: null, reached: false }), state.modelQuotas[1]] }; return adminAi(state) },
    })
    app('/admin/ai')
    const user = userEvent.setup()
    const table = await screen.findByRole('table', { name: 'Limits of each Gemini model' })
    expect(within(table).getByText('Gemini 3.8 Flash')).toBeInTheDocument()
    expect(within(table).getByText('used up')).toBeInTheDocument()
    expect((screen.getByLabelText('Requests a day for Gemini 3.8 Flash') as HTMLInputElement).value).toBe('20')
    expect((screen.getByLabelText('Requests a day for Gemini 3.5 Flash Lite') as HTMLInputElement).value).toBe('500')
    const save = screen.getByRole('button', { name: 'Save the model limits' })
    expect(save).toBeDisabled()
    await user.clear(screen.getByLabelText('Requests a day for Gemini 3.8 Flash')); await user.type(screen.getByLabelText('Requests a day for Gemini 3.8 Flash'), '30')
    await user.clear(screen.getByLabelText('Requests a minute for Gemini 3.8 Flash'))
    await user.click(save)
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT')?.body).toEqual({ modelLimits: { 'gemini-3.8-flash.PerDay': 30, 'gemini-3.8-flash.PerMinute': 0 } }))
    expect(await screen.findByText('The model limits were saved.')).toBeInTheDocument()
  })

  it('shows the admin each model on the usage page with its meters, and which one is used up', async () => {
    signedIn()
    mockApi({ ...standard, ...asAdmin, 'GET /ai-usage': () => usage({ allUsers: true, modelQuotas: [modelQuota(), modelQuota({ model: 'gemini-3.5-flash-lite', name: 'Gemini 3.5 Flash Lite', usedToday: 12, perDay: 500, reached: false })] }) })
    app('/usage')
    const card = await screen.findByRole('region', { name: 'Gemini models today' })
    expect(within(card).getByText('Gemini 3.8 Flash')).toBeInTheDocument()
    expect(within(card).getByText('Used up for today')).toBeInTheDocument()
    const bars = within(card).getAllByRole('progressbar')
    expect(bars[0]).toHaveAttribute('aria-valuemax', '20')
    expect(within(card).getByText(/of 20/)).toBeInTheDocument()
  })
})