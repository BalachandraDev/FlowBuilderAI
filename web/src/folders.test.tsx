import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, dept, flow, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const ward = { ...dept, id: 'd-2', moduleCode: 'WARD', name: 'Ward', flowCount: 1 }
const nr1 = { ...flow, kind: 'NR', requestNo: 1, requestCode: 'NR-001' }
const nr2 = { ...flow, id: 'f-2', projectDepartmentId: 'd-2', moduleCode: 'WARD', departmentName: 'Ward', menuName: 'Bed allocation', kind: 'NR', requestNo: 1, requestCode: 'NR-001', status: 'Draft', currentVersionNo: 0, entryCount: 0 }
const cr1 = { ...flow, id: 'f-3', projectDepartmentId: 'd-2', moduleCode: 'WARD', departmentName: 'Ward', menuName: 'Discharge print', kind: 'CR', requestNo: 1, requestCode: 'CR-001', status: 'Draft', currentVersionNo: 0, entryCount: 0 }

const routes = (over: Record<string, () => object | undefined> = {}) => ({
  ...standard,
  'GET /projects/p-1/departments': () => [dept, ward],
  'GET /projects/p-1/flows': () => [nr1, nr2, cr1],
  'GET /flows/f-2': () => nr2, 'GET /flows/f-3': () => cr1,
  'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => ({}),
  'GET /flows/f-2/entries': () => [], 'GET /flows/f-2/analyses': () => [], 'GET /flows/f-2/clarifications': () => [], 'GET /flows/f-2/documents': () => [],
  'GET /flows/f-3/entries': () => [], 'GET /flows/f-3/analyses': () => [], 'GET /flows/f-3/clarifications': () => [], 'GET /flows/f-3/documents': () => [],
  ...over,
})

describe('modules and requests (the project folders)', () => {
  it('shows each module with its new requirements and change requests, numbered, and opens the one that is picked', async () => {
    signedIn(); mockApi(routes()); app('/p/p-1')
    const tree = await screen.findByRole('list', { name: 'Modules' })
    const medical = await within(tree).findByRole('button', { name: /^Medical Records/ })
    expect(medical).toHaveAttribute('aria-expanded', 'true')                       // the module of the request being worked on is open
    expect(within(tree).getByRole('button', { name: /^Ward/ })).toHaveAttribute('aria-expanded', 'false')
    const user = userEvent.setup()
    await user.click(within(tree).getByRole('button', { name: /^Ward/ }))
    const newReq = within(tree).getByRole('group', { name: 'Ward: New requirements' })
    const changes = within(tree).getByRole('group', { name: 'Ward: Change requests' })
    expect(within(newReq).getByRole('button', { name: /^NR-001.*Bed allocation/ })).toBeInTheDocument()
    expect(within(changes).getByRole('button', { name: /^CR-001.*Discharge print/ })).toBeInTheDocument()
    await user.click(within(changes).getByRole('button', { name: /^CR-001/ }))
    await waitFor(() => expect(within(tree).getByRole('button', { name: /^CR-001.*Discharge print/ })).toHaveAttribute('aria-current', 'true'))
    expect(await screen.findByText('Discharge print', { selector: '.request-name' })).toBeInTheDocument()
  })

  it('creates a change request in a module and opens it', async () => {
    signedIn()
    let made: object | undefined
    const server = mockApi(routes({ 'POST /projects/p-1/flows': () => { made = { ...cr1, id: 'f-9', menuName: 'Lab report layout', requestNo: 2, requestCode: 'CR-002' }; return made },
      'GET /flows/f-9': () => made, 'GET /flows/f-9/entries': () => [], 'GET /flows/f-9/analyses': () => [], 'GET /flows/f-9/clarifications': () => [], 'GET /flows/f-9/documents': () => [] }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /^Ward/ }))
    await user.click(screen.getByRole('button', { name: 'New change request in Ward' }))
    await user.type(screen.getByLabelText('Name of the change request'), '  Lab report layout ')
    await user.click(screen.getByRole('button', { name: 'Create CR' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'projects/p-1/flows')?.body).toEqual({ projectDepartmentId: 'd-2', menuName: 'Lab report layout', kind: 'CR' }))
    expect(await screen.findByText('CR-002 was created.')).toBeInTheDocument()
  })

  it('lets only the project lead add a module, only from the administrator’s list, and says why it is refused', async () => {
    signedIn()
    const server = mockApi(routes({
      'GET /master/departments': () => [{ id: 'm-9', code: 'LAB', name: 'Lab', isActive: true }],
      'POST /projects/p-1/departments': () => problem(422, 'Rule broken.', 'This module is not in the administrator’s list (or it was stopped). Choose one from the list.'),
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Add a module/ }))
    expect(screen.queryByRole('textbox', { name: 'Name of the module' })).not.toBeInTheDocument()            // a name cannot be typed
    await user.selectOptions(await screen.findByLabelText('Module to add'), 'Lab')
    await user.click(screen.getByRole('button', { name: 'Add module' }))
    expect(await screen.findByText(/not in the administrator’s list/)).toBeInTheDocument()
    expect(server.calls.find(c => c.method === 'POST' && c.path === 'projects/p-1/departments')!.body).toEqual({ departmentId: 'm-9' })
  })

  it('does not offer to add anything to someone who can only read the project', async () => {
    signedIn()
    mockApi(routes({ 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }) }))
    app('/p/p-1')
    await screen.findByRole('list', { name: 'Modules' })
    expect(screen.queryByRole('button', { name: /Add a module/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^New (new requirement|change request)/ })).not.toBeInTheDocument()
  })

  it('deletes a request from its row, after asking, and the list loses it', async () => {
    signedIn()
    let list = [nr1, nr2, cr1]
    const server = mockApi(routes({ 'GET /projects/p-1/flows': () => list, 'DELETE /flows/f-3': () => { list = [nr1, nr2]; return undefined } }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /^Ward/ }))
    await user.click(await screen.findByRole('button', { name: 'Delete CR-001 Discharge print' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/Nothing is erased from the database/)).toBeInTheDocument()
    expect(within(dialog).getByText(/The number CR-001 is not used again/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(server.count('DELETE', 'flows/f-3')).toBe(0)
    await user.click(screen.getByRole('button', { name: 'Delete CR-001 Discharge print' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete request' }))
    await waitFor(() => expect(server.count('DELETE', 'flows/f-3')).toBe(1))
    expect(await screen.findByText('CR-001 was deleted.')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('button', { name: /^CR-001.*Discharge print/ })).not.toBeInTheDocument())
  })

  it('offers no delete to someone who can only read the project', async () => {
    signedIn()
    mockApi(routes({ 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }) }))
    app('/p/p-1')
    await screen.findByRole('list', { name: 'Modules' })
    expect(screen.queryByRole('button', { name: /^Delete (NR|CR)-/ })).not.toBeInTheDocument()
  })

  it('keeps the project and its folders when every request was deleted, and says how to start a new one', async () => {
    signedIn()
    mockApi(routes({ 'GET /projects/p-1/flows': () => [] }))
    app('/p/p-1')
    expect(await screen.findByText('This project has no requests')).toBeInTheDocument()
    expect(await screen.findByRole('list', { name: 'Modules' })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /^Medical Records/ })).toBeInTheDocument()
  })
})