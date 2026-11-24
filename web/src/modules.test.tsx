import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { moduleCode } from './pages/admin/Modules'
import { app, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }
const overview = { users: 1, activeUsers: 1, projects: 0, flows: 0, notes: 0, documents: 0, aiProvider: 'gemini', aiReady: true }
const list = () => [
  { id: 'm-1', code: 'MRD', name: 'MRD', isActive: true },
  { id: 'm-2', code: 'PHARMACY', name: 'Pharmacy', isActive: true },
  { id: 'm-3', code: 'OLD', name: 'Old store', isActive: false },
]
const routes = (over: Record<string, () => object | undefined> = {}) => ({ ...standard, ...asAdmin, 'GET /admin/overview': () => overview, 'GET /master/departments': list, ...over })

describe('the short code of a module', () => {
  it('is made from the letters and digits of the name, and made unique with a number', () => {
    expect(moduleCode('Nurse Station', [])).toBe('NURSESTATION')
    expect(moduleCode('  ward  (east) ', [])).toBe('WARDEAST')
    expect(moduleCode('Ward', ['WARD'])).toBe('WARD2')
    expect(moduleCode('Ward', ['WARD', 'ward2'])).toBe('WARD3')
    expect(moduleCode('A very long name of a module that goes on', [])).toHaveLength(18)
    expect(moduleCode('x', [])).toBeNull()
    expect(moduleCode('!!', [])).toBeNull()
  })
})

describe('the administrator’s Modules page', () => {
  it('is in the menu of the administrator only, and lists every module, stopped ones too', async () => {
    signedIn(); mockApi(routes()); app('/admin/modules')
    expect(await screen.findByRole('heading', { name: 'Modules' })).toBeInTheDocument()
    expect(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: /Modules/ })).toHaveAttribute('href', '/admin/modules')
    const rows = within(await screen.findByRole('list', { name: 'Modules' })).getAllByRole('listitem')
    expect(rows.map(r => r.querySelector('.urow-main b')?.textContent)).toEqual(['MRD', 'Old store', 'Pharmacy'])
    expect(within(rows[1]).getByText('Stopped')).toBeInTheDocument()
    expect(within(rows[2]).getByText('Code PHARMACY')).toBeInTheDocument()
  })

  it('is not open to a user', async () => {
    signedIn(); mockApi({ ...standard, 'GET /master/departments': list }); app('/admin/modules')
    await waitFor(() => expect(screen.getAllByRole('link', { name: 'New project' }).length).toBeGreaterThan(0))          // a user is sent to their own start page
    expect(screen.queryByRole('heading', { name: 'Modules' })).not.toBeInTheDocument()
  })

  it('adds a module with the code made from its name', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /master/departments': () => ({ id: 'm-9', code: 'NURSESTATION', name: 'Nurse Station', isActive: true }) }))
    app('/admin/modules')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Add module' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a module' })
    expect(within(dialog).getByRole('button', { name: 'Add module' })).toBeDisabled()
    await user.type(within(dialog).getByLabelText(/Module name/), 'Nurse Station')
    expect(within(dialog).getByText('NURSESTATION')).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Add module' }))
    expect(await screen.findByText('Nurse Station was added. Users can pick it now.')).toBeInTheDocument()
    expect(server.calls.find(c => c.method === 'POST' && c.path === 'master/departments')!.body).toEqual({ code: 'NURSESTATION', name: 'Nurse Station', isActive: true })
  })

  it('does not accept a name that is already a module, in any letters', async () => {
    signedIn(); const server = mockApi(routes()); app('/admin/modules')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Add module' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a module' })
    await user.type(within(dialog).getByLabelText(/Module name/), 'pharmacy')
    expect(within(dialog).getByText('There is already a module with this name.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Add module' })).toBeDisabled()
    expect(server.count('POST', 'master/departments')).toBe(0)
  })

  it('shows the reason when the server refuses', async () => {
    signedIn(); mockApi(routes({ 'POST /master/departments': () => problem(409, 'Conflict.', 'There is already a module called "Stores".') })); app('/admin/modules')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Add module' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a module' })
    await user.type(within(dialog).getByLabelText(/Module name/), 'Stores')
    await user.click(within(dialog).getByRole('button', { name: 'Add module' }))
    expect(await within(dialog).findByText(/There is already a module called "Stores"/)).toBeInTheDocument()
  })

  it('renames a module and keeps its code', async () => {
    signedIn()
    const server = mockApi(routes({ 'PUT /master/departments/m-2': () => ({ id: 'm-2', code: 'PHARMACY', name: 'Pharmacy (main)', isActive: true }) }))
    app('/admin/modules')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Rename Pharmacy' }))
    const dialog = await screen.findByRole('dialog', { name: 'Rename Pharmacy' })
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled()                // nothing changed yet
    const box = within(dialog).getByLabelText(/Module name/)
    await user.clear(box); await user.type(box, 'Pharmacy (main)')
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'master/departments/m-2')?.body).toEqual({ code: 'PHARMACY', name: 'Pharmacy (main)', isActive: true }))
    expect(await screen.findByText('Saved.')).toBeInTheDocument()
  })

  it('stops a module and allows it again', async () => {
    signedIn()
    const server = mockApi(routes({ 'PUT /master/departments/m-2': () => ({ id: 'm-2', code: 'PHARMACY', name: 'Pharmacy', isActive: false }), 'PUT /master/departments/m-3': () => ({ id: 'm-3', code: 'OLD', name: 'Old store', isActive: true }) }))
    app('/admin/modules')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Stop Pharmacy' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'master/departments/m-2')?.body).toEqual({ code: 'PHARMACY', name: 'Pharmacy', isActive: false }))
    await user.click(screen.getByRole('button', { name: 'Allow Old store' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'master/departments/m-3')?.body).toEqual({ code: 'OLD', name: 'Old store', isActive: true }))
  })

  it('says how to start when there are no modules', async () => {
    signedIn(); mockApi(routes({ 'GET /master/departments': () => [] })); app('/admin/modules')
    expect(await screen.findByText('No modules yet')).toBeInTheDocument()
  })
})
