import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, signedIn, standard } from './test/fixtures'
import { mockApi, tokens } from './test/mockApi'

beforeEach(async () => { auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }
const row = (over: object) => ({ clientId: 'c-1', clientName: 'Kauvery Group', clientActive: true, userId: 'u-1', userName: 'Asha Lead', userEmail: 'asha@demo.local', userActive: true,
  projects: 3, projectsStarted: 2, modules: 4, requests: 9, awaitingVerification: 1, verified: 2, returned: 0, lastFinalAt: new Date().toISOString(), ...over })
const rows = () => [
  row({}),
  row({ userId: 'u-2', userName: 'Ravi Shankar', userEmail: 'ravi@demo.local', projectsStarted: 1, awaitingVerification: 0, verified: 0, returned: 1 }),
  row({ clientId: 'c-2', clientName: 'Apollo Group', userId: 'u-2', userName: 'Ravi Shankar', userEmail: 'ravi@demo.local', projects: 1, projectsStarted: 1, modules: 1, requests: 2, awaitingVerification: 0, verified: 0, returned: 0, lastFinalAt: null }),
  row({ clientId: 'c-3', clientName: 'Nobody Hospital', userId: null, userName: null, userEmail: null, projects: 0, projectsStarted: 0, modules: 0, requests: 0, awaitingVerification: 0, verified: 0, returned: 0, lastFinalAt: null }),
  row({ clientId: null, clientName: null, userId: 'u-3', userName: 'Idle Person', userEmail: 'idle@demo.local', projects: 0, projectsStarted: 0, modules: 0, requests: 0, awaitingVerification: 0, verified: 0, returned: 0, lastFinalAt: null }),
]

describe('who handles what (administrator)', () => {
  it('lists each client with the people on it and what they did, and flags a client nobody handles and a user on no client', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /admin/handling': rows }); app('/admin/handling')
    expect(await screen.findByRole('heading', { name: 'Who handles what' })).toBeInTheDocument()

    const kauvery = (await screen.findByRole('list', { name: 'People on Kauvery Group' })).closest('li')!
    expect(within(kauvery).getByText('Asha Lead')).toBeInTheDocument()
    expect(within(kauvery).getByText('Ravi Shankar')).toBeInTheDocument()
    expect(within(kauvery).getByText('3 projects · 4 modules · 9 requests')).toBeInTheDocument()
    const asha = within(kauvery).getByText('Asha Lead').closest('li')!
    expect(asha).toHaveTextContent('started 2 projects')
    expect(asha).toHaveTextContent('1 waiting')
    expect(asha).toHaveTextContent('2 verified')
    expect(within(within(kauvery).getByText('Ravi Shankar').closest('li')!).getByText('1 returned')).toBeInTheDocument()

    expect(screen.getByText(/Nobody handles this client yet/)).toBeInTheDocument()
    const totals = screen.getByLabelText('Totals')
    expect(within(totals).getByText('clients nobody handles').previousElementSibling).toHaveTextContent('1')
    expect(within(totals).getByText('users on no client').previousElementSibling).toHaveTextContent('1')
  })

  it('shows each user with the clients they handle, including a user on no client', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /admin/handling': rows }); app('/admin/handling')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('tab', { name: 'By user' }))

    const ravi = (await screen.findByRole('list', { name: 'Clients of Ravi Shankar' })).closest('li')!
    expect(within(ravi).getByText('Kauvery Group')).toBeInTheDocument()
    expect(within(ravi).getByText('Apollo Group')).toBeInTheDocument()                        // one person, two clients
    expect(screen.getByText(/Not on any client yet/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Idle Person' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Nobody Hospital' })).not.toBeInTheDocument()
  })

  it('filters by client, by user and by search, and clears the filters', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /admin/handling': rows }); app('/admin/handling')
    const user = userEvent.setup()
    await screen.findByRole('list', { name: 'People on Kauvery Group' })

    await user.selectOptions(screen.getByLabelText('Client'), 'c-2')
    expect(screen.queryByRole('list', { name: 'People on Kauvery Group' })).not.toBeInTheDocument()
    expect(screen.getByRole('list', { name: 'People on Apollo Group' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))

    await user.selectOptions(screen.getByLabelText('User'), 'u-1')
    const kauvery = screen.getByRole('list', { name: 'People on Kauvery Group' })
    expect(within(kauvery).getByText('Asha Lead')).toBeInTheDocument()
    expect(within(kauvery).queryByText('Ravi Shankar')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))

    await user.type(screen.getByPlaceholderText('Search by client or user'), 'nobody')
    expect(screen.getByRole('heading', { name: 'Nobody Hospital' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Kauvery Group' })).not.toBeInTheDocument()
    await user.clear(screen.getByPlaceholderText('Search by client or user'))
    await user.type(screen.getByPlaceholderText('Search by client or user'), 'zzz')
    expect(screen.getByText('No client matches')).toBeInTheDocument()
  })

  it('is for the administrator only', async () => {
    signedIn(); mockApi(standard); app('/admin/handling')
    expect(await screen.findByRole('heading', { name: 'Hello, Asha' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Who handles what' })).not.toBeInTheDocument()
  })
})
