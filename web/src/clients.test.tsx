import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, client, contentDto, flow, project, signedIn, standard } from './test/fixtures'
import { mockApi, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }
const second = { ...client, id: 'c-2', name: 'Apollo Hospitals', location: '', projectCount: 0 }
const account = (over: object) => ({ id: 'u-2', email: 'ravi@demo.local', displayName: 'Ravi Shankar', isActive: true, roles: ['ImplementationLead'], preferredLanguage: null, ...over })
const admin = { id: 'u-1', email: 'admin@flowcapture.local', displayName: 'Dev Admin', isActive: true, roles: ['Admin'], preferredLanguage: null }

describe('starting a project for a client', () => {
  it('asks which client when the user works for several, and sends the choice', async () => {
    signedIn()
    const server = mockApi({
      ...standard, 'GET /clients': () => [client, second],
      'POST /projects/quick': () => ({ projectId: 'p-2', departmentId: 'd-2', flowId: 'f-2', name: 'Ward', code: 'WARD-1' }),
      'GET /projects/p-2/flows': () => [], 'GET /projects/p-2/departments': () => [],
    })
    app('/new')
    const user = userEvent.setup()
    await user.type(await screen.findByLabelText('Project name'), 'Ward')
    expect(screen.getByRole('button', { name: 'Create and add notes' })).toBeDisabled()          // no client chosen yet
    await user.selectOptions(screen.getByLabelText('Client'), 'c-2')
    await user.click(screen.getByRole('button', { name: 'Create and add notes' }))
    await waitFor(() => expect(server.calls.find(c => c.path === 'projects/quick')?.body).toEqual({ name: 'Ward', description: null, clientId: 'c-2' }))
  })

  it('shows the only client without asking', async () => {
    signedIn(); mockApi({ ...standard }); app('/new')
    expect(await screen.findByLabelText('Client')).toHaveValue('Kauvery Group')
    expect(screen.getByLabelText('Client')).toHaveAttribute('readonly')
  })

  it('tells a user without a client to ask the administrator, and offers no form', async () => {
    signedIn(); mockApi({ ...standard, 'GET /clients': () => [] }); app('/new')
    expect(await screen.findByText('You are not assigned to a client yet')).toBeInTheDocument()
    expect(screen.queryByLabelText('Project name')).not.toBeInTheDocument()
  })

  it('groups the projects by client when the user works for several', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /projects': () => [{ ...project, clientId: 'c-1', clientName: 'Kauvery Group' }, { ...project, id: 'p-2', hospitalName: 'Apollo ward', clientId: 'c-2', clientName: 'Apollo Hospitals' }] })
    app('/')
    expect(await screen.findByRole('region', { name: 'Projects of Kauvery Group' })).toBeInTheDocument()
    const apollo = screen.getByRole('region', { name: 'Projects of Apollo Hospitals' })
    expect(within(apollo).getByRole('link', { name: 'Open Apollo ward' })).toBeInTheDocument()
  })

  it('lets a colleague on the client add notes: a Collaborator can capture but cannot delete the project', async () => {
    signedIn()
    mockApi({
      ...standard, 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Collaborator' }] }),
      'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
      'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
      'GET /projects/p-1/flows': () => [{ ...flow, currentVersionNo: 0, entryCount: 0, openQuestionCount: 0 }], 'GET /flows/f-1/entries': () => [],
    })
    app('/p/p-1')
    expect(await screen.findByRole('group', { name: 'Add notes' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Delete project/ })).not.toBeInTheDocument()
  })
})

describe('the clients page (administrator)', () => {
  const routes = (over: Record<string, unknown> = {}) => ({
    ...standard, ...asAdmin, 'GET /users': () => [admin, account({}), account({ id: 'u-3', email: 'meena@demo.local', displayName: 'Meena Iyer' }), account({ id: 'u-4', email: 'old@demo.local', displayName: 'Old Hand', isActive: false })],
    'GET /clients': () => [{ ...client, userIds: ['u-2'] }, { ...second, isActive: false }], ...over,
  })

  it('lists the clients with their people, and the stopped one is marked', async () => {
    signedIn(); mockApi(routes()); app('/admin/clients')
    const row = (await screen.findByText('Kauvery Group')).closest('li')!
    expect(within(row).getByText(/1 person: Ravi Shankar/)).toBeInTheDocument()
    expect(within(row).getByText(/Chennai · 1 project/)).toBeInTheDocument()
    const stopped = screen.getByText('Apollo Hospitals').closest('li')!
    expect(within(stopped).getByText('Stopped')).toBeInTheDocument()
    expect(within(stopped).getByText('Nobody assigned yet')).toBeInTheDocument()
  })

  it('adds a client and goes straight to assigning its people', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /clients': () => ({ id: 'c-9', name: 'Fortis', location: 'Delhi', isActive: true, projectCount: 0, userIds: [] }) }))
    app('/admin/clients')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Add client/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a client' })
    expect(within(dialog).getByRole('button', { name: 'Add client' })).toBeDisabled()
    await user.type(within(dialog).getByLabelText(/Client name/), ' Fortis ')
    await user.type(within(dialog).getByLabelText(/Place/), 'Delhi')
    await user.click(within(dialog).getByRole('button', { name: 'Add client' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'clients')?.body).toEqual({ name: 'Fortis', location: 'Delhi' }))
    expect(await screen.findByRole('dialog', { name: 'People for Fortis' })).toBeInTheDocument()
  })

  it('assigns people with ticks (administrators and stopped accounts are not offered) and saves the whole list', async () => {
    signedIn()
    const server = mockApi(routes({ 'PUT /clients/c-1/users': () => ({ ...client, userIds: ['u-2', 'u-3'] }) }))
    app('/admin/clients')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Assign people to Kauvery Group' }))
    const dialog = await screen.findByRole('dialog', { name: 'People for Kauvery Group' })
    expect(within(dialog).queryByText('Dev Admin')).not.toBeInTheDocument()
    expect(within(dialog).queryByText('Old Hand')).not.toBeInTheDocument()
    expect(within(dialog).getByLabelText(/Ravi Shankar/)).toBeChecked()
    const save = within(dialog).getByRole('button', { name: 'Save' })
    expect(save).toBeDisabled()
    await user.click(within(dialog).getByLabelText(/Meena Iyer/))
    await user.click(save)
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'clients/c-1/users')?.body).toEqual({ userIds: ['u-2', 'u-3'] }))
  })

  it('renames a client and stops it without losing its name', async () => {
    signedIn()
    const server = mockApi(routes({ 'PUT /clients/c-1': () => client }))
    app('/admin/clients')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Stop Kauvery Group' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'clients/c-1')?.body).toEqual({ name: 'Kauvery Group', location: 'Chennai', isActive: false }))
    await user.click(screen.getByRole('button', { name: 'Change Kauvery Group' }))
    const dialog = await screen.findByRole('dialog')
    await user.clear(within(dialog).getByLabelText(/Client name/)); await user.type(within(dialog).getByLabelText(/Client name/), 'Kauvery Hospitals')
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(server.calls.filter(c => c.method === 'PUT' && c.path === 'clients/c-1')[1]?.body).toMatchObject({ name: 'Kauvery Hospitals' }))
  })

  it('is for administrators only: a user is sent away', async () => {
    signedIn(); mockApi({ ...standard }); app('/admin/clients')
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Clients' })).not.toBeInTheDocument())
  })
})
