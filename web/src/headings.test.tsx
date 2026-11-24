import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, dept, flow, project, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const ward = { ...dept, id: 'd-2', moduleCode: 'WARD', name: 'Ward', flowCount: 0 }
const nr1 = { ...flow, kind: 'NR', requestNo: 1, requestCode: 'NR-001' }
const bug1 = { ...flow, id: 'f-4', projectDepartmentId: 'd-2', moduleCode: 'WARD', departmentName: 'Ward', menuName: 'Discharge shows wrong ward', kind: 'BUG', requestNo: 1, requestCode: 'BUG-001', status: 'Draft', currentVersionNo: 0, entryCount: 0 }
const master = [
  { id: 'm-1', code: 'MRD', name: 'Medical Records', isActive: true },
  { id: 'm-2', code: 'PHARMACY', name: 'Pharmacy', isActive: true },
  { id: 'm-3', code: 'LAB', name: 'Lab', isActive: true },
  { id: 'm-4', code: 'OLD', name: 'Retired module', isActive: false },
]

const routes = (over: Record<string, () => object | undefined> = {}) => ({
  ...standard,
  'GET /projects/p-1/departments': () => [dept, ward],
  'GET /projects/p-1/flows': () => [nr1, bug1],
  'GET /master/departments': () => master,
  'GET /flows/f-4': () => bug1,
  'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => ({}),
  'GET /flows/f-4/entries': () => [], 'GET /flows/f-4/analyses': () => [], 'GET /flows/f-4/clarifications': () => [], 'GET /flows/f-4/documents': () => [],
  ...over,
})
const reader = (over: Record<string, () => object | undefined> = {}) =>
  routes({ 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }), ...over })

describe('bugs are requests too', () => {
  it('lists bugs in their own group, numbered BUG-001, beside the new requirements and change requests', async () => {
    signedIn(); mockApi(routes()); app('/p/p-1')
    const tree = await screen.findByRole('list', { name: 'Modules' })
    const user = userEvent.setup()
    await user.click(await within(tree).findByRole('button', { name: /^Ward/ }))
    const bugs = within(tree).getByRole('group', { name: 'Ward: Bugs' })
    expect(within(bugs).getByRole('button', { name: /^BUG-001.*Discharge shows wrong ward/ })).toBeInTheDocument()
    expect(within(tree).getByRole('group', { name: 'Ward: New requirements' })).toBeInTheDocument()
    expect(within(tree).getByRole('group', { name: 'Ward: Change requests' })).toBeInTheDocument()
  })

  it('files a bug in a module and opens it', async () => {
    signedIn()
    let made: object | undefined
    const server = mockApi(routes({ 'POST /projects/p-1/flows': () => { made = { ...bug1, id: 'f-9', menuName: 'Print is cut off', requestNo: 2, requestCode: 'BUG-002' }; return made }, 'GET /flows/f-9': () => made,
      'GET /flows/f-9/entries': () => [], 'GET /flows/f-9/analyses': () => [], 'GET /flows/f-9/clarifications': () => [], 'GET /flows/f-9/documents': () => [] }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /^Ward/ }))
    await user.click(screen.getByRole('button', { name: 'New bug in Ward' }))
    await user.type(screen.getByLabelText('Name of the bug'), ' Print is cut off ')
    await user.click(screen.getByRole('button', { name: 'Create BUG' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'projects/p-1/flows')?.body).toEqual({ projectDepartmentId: 'd-2', menuName: 'Print is cut off', kind: 'BUG' }))
    expect(await screen.findByText('BUG-002 was created.')).toBeInTheDocument()
  })

  it('shows the bug number on the request being worked on', async () => {
    signedIn(); mockApi(routes()); app('/p/p-1?flow=f-4')
    expect(await screen.findByText('Discharge shows wrong ward', { selector: '.request-name' })).toBeInTheDocument()
    expect(document.querySelector('.request-title .kind-tag.bug')).toHaveTextContent('BUG-001')
  })
})

describe('choosing the module', () => {
  it('offers the master modules the project does not have yet, and adds the one that is picked', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /projects/p-1/departments': () => ({ ...dept, id: 'd-7', moduleCode: 'PHARMACY', name: 'Pharmacy', flowCount: 0 }) }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Add a module/ }))
    const pick = await screen.findByLabelText('Module to add')
    const names = within(pick).getAllByRole('option').map(o => o.textContent)
    expect(names).toEqual(['Choose a module…', 'Lab', 'Pharmacy'])          // Medical Records is already there, a stopped one is not offered, the list is in order, and nothing can be typed
    expect(screen.queryByLabelText('Name of the module')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Add module' })).toBeDisabled()
    await user.selectOptions(pick, 'Pharmacy')
    await user.click(screen.getByRole('button', { name: 'Add module' }))
    expect(await screen.findByText('The module Pharmacy was added.')).toBeInTheDocument()
    expect(server.calls.find(c => c.method === 'POST' && c.path === 'projects/p-1/departments')!.body).toEqual({ departmentId: 'm-2' })
  })

  it('says to ask the administrator when the list has nothing left to add', async () => {
    signedIn()
    const server = mockApi(routes({ 'GET /master/departments': () => [{ id: 'm-1', code: 'MRD', name: 'Medical Records', isActive: true }, { id: 'm-5', code: 'WARD', name: 'Ward', isActive: true }] }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Add a module/ }))
    expect(await screen.findByText(/already has every module on the list/)).toBeInTheDocument()
    expect(screen.getByText(/Modules are created by the administrator/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Module to add')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Add module' })).toBeDisabled()
    expect(server.count('POST', 'projects/p-1/departments')).toBe(0)
  })

  it('creates a request from the top: pick the module, the type and the name', async () => {
    signedIn()
    let made: object | undefined
    const server = mockApi(routes({ 'POST /projects/p-1/flows': () => { made = { ...bug1, id: 'f-9', menuName: 'Bed count is wrong', requestNo: 2, requestCode: 'BUG-002' }; return made }, 'GET /flows/f-9': () => made,
      'GET /flows/f-9/entries': () => [], 'GET /flows/f-9/analyses': () => [], 'GET /flows/f-9/clarifications': () => [], 'GET /flows/f-9/documents': () => [] }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'New request' }))
    const form = screen.getByRole('form', { name: 'New request' })
    expect(within(form).getByLabelText('Module of the new request')).toHaveValue('d-1')                         // the module of the request being worked on
    expect(within(form).getAllByRole('option').map(o => o.textContent)).toEqual(['Medical Records', 'Ward', 'New requirement (NR)', 'Change request (CR)', 'Bug (BUG)'])
    expect(within(form).getByRole('button', { name: 'Create NR' })).toBeDisabled()
    await user.selectOptions(within(form).getByLabelText('Module of the new request'), 'Ward')
    await user.selectOptions(within(form).getByLabelText('Type of the new request'), 'Bug (BUG)')
    await user.type(within(form).getByLabelText('Name of the new request'), 'Bed count is wrong')
    await user.click(within(form).getByRole('button', { name: 'Create BUG' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'projects/p-1/flows')?.body).toEqual({ projectDepartmentId: 'd-2', menuName: 'Bed count is wrong', kind: 'BUG' }))
    expect(await screen.findByText('BUG-002 was created.')).toBeInTheDocument()
    expect(screen.queryByRole('form', { name: 'New request' })).not.toBeInTheDocument()
  })

  it('offers no way to create a request to someone who can only read the project', async () => {
    signedIn(); mockApi(reader()); app('/p/p-1')
    await screen.findByRole('list', { name: 'Modules' })
    expect(screen.queryByRole('button', { name: 'New request' })).not.toBeInTheDocument()
  })
})

describe('editing a heading', () => {
  it('renames the request: the new heading shows at once and is sent to the server', async () => {
    signedIn()
    let current = nr1
    const server = mockApi(routes({ 'GET /projects/p-1/flows': () => [current, bug1], 'PUT /flows/f-1': () => { current = { ...nr1, menuName: 'Case sheet receive in MRD' }; return current } }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Edit request heading' }))
    const box = screen.getByRole('textbox', { name: 'request heading' })
    expect(box).toHaveValue('IP Case Sheet Receive')
    await user.clear(box)
    await user.type(box, '  Case sheet receive in MRD ')
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(await screen.findByText('Case sheet receive in MRD', { selector: '.request-name' })).toBeInTheDocument()
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'flows/f-1')?.body).toEqual({ menuName: 'Case sheet receive in MRD' }))
    expect(screen.queryByRole('textbox', { name: 'request heading' })).not.toBeInTheDocument()
  })

  it('saves with Enter and cancels with Escape, and nothing is sent when nothing changed', async () => {
    signedIn()
    const server = mockApi(routes({ 'PUT /flows/f-1': () => ({ ...nr1, menuName: 'Another' }) }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Edit request heading' }))
    await user.type(screen.getByRole('textbox', { name: 'request heading' }), ' extra{Escape}')
    expect(screen.queryByRole('textbox', { name: 'request heading' })).not.toBeInTheDocument()
    expect(screen.getByText('IP Case Sheet Receive', { selector: '.request-name' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Edit request heading' }))
    await user.click(screen.getByRole('button', { name: 'Save' }))                                              // unchanged: closes, sends nothing
    expect(server.count('PUT', 'flows/f-1')).toBe(0)
    await user.click(screen.getByRole('button', { name: 'Edit request heading' }))
    const box = screen.getByRole('textbox', { name: 'request heading' })
    await user.clear(box)
    await user.type(box, 'Another{Enter}')
    await waitFor(() => expect(server.count('PUT', 'flows/f-1')).toBe(1))
  })

  it('does not accept an empty heading', async () => {
    signedIn()
    const server = mockApi(routes())
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Edit request heading' }))
    await user.clear(screen.getByRole('textbox', { name: 'request heading' }))
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
    expect(server.count('PUT', 'flows/f-1')).toBe(0)
  })

  it('opens the box again with what was typed when the server refuses, and says why', async () => {
    signedIn()
    const server = mockApi(routes({ 'PUT /flows/f-1': () => problem(409, 'Conflict.', 'This module already has a request called "Bed allocation". Choose another heading.') }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Edit request heading' }))
    const box = screen.getByRole('textbox', { name: 'request heading' })
    await user.clear(box)
    await user.type(box, 'Bed allocation{Enter}')
    expect(await screen.findByText(/already has a request called "Bed allocation"/)).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'request heading' })).toHaveValue('Bed allocation')
    expect(server.count('PUT', 'flows/f-1')).toBe(1)
    await user.keyboard('{Escape}')
    expect(await screen.findByText('IP Case Sheet Receive', { selector: '.request-name' })).toBeInTheDocument()   // the old heading is back
  })

  it('renames the project with the rest of its details unchanged', async () => {
    signedIn()
    let current = project
    const server = mockApi(routes({ 'GET /projects': () => [current], 'PUT /projects/p-1': () => { current = { ...project, hospitalName: 'Kauvery Heart City' }; return current } }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Edit project heading' }))
    const box = screen.getByRole('textbox', { name: 'project heading' })
    await user.clear(box)
    await user.type(box, 'Kauvery Heart City{Enter}')
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'projects/p-1')?.body).toEqual({
      hospitalName: 'Kauvery Heart City', location: '', startDate: '2026-11-01', leadUserId: 'u-1', status: 'Active', monthlyAiTokenLimit: null,
    }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Kauvery Heart City' })).toBeInTheDocument()
  })

  it('gives someone who can only read the project no pencil', async () => {
    signedIn(); mockApi(reader()); app('/p/p-1')
    await screen.findByRole('list', { name: 'Modules' })
    expect(screen.queryByRole('button', { name: /^Edit (request|project) heading/ })).not.toBeInTheDocument()
  })
})
