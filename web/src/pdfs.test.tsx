import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, confirmPdf, contentDto, dept, flow, outline, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const ward = { ...dept, id: 'd-1', name: 'Ward' }
const pharmacy = { ...dept, id: 'd-2', moduleCode: 'PHARMACY', name: 'Pharmacy' }
const billing = { ...dept, id: 'd-3', moduleCode: 'BILLING', name: 'Billing' }
const flows = [
  { ...flow, id: 'f-1', projectDepartmentId: 'd-1', menuName: 'Bed allocation', currentVersionNo: 1 },
  { ...flow, id: 'f-2', projectDepartmentId: 'd-1', menuName: 'Visitor pass', currentVersionNo: 0, entryCount: 0 },
  { ...flow, id: 'f-3', projectDepartmentId: 'd-2', menuName: 'Returns', currentVersionNo: 2 },
  { ...flow, id: 'f-4', projectDepartmentId: 'd-3', menuName: 'Nothing analysed', currentVersionNo: 0, entryCount: 0 },
]
const doc = (over: object) => ({ id: 'doc-1', docNumber: 'IMPL-KMC-FULL', title: 'Kauvery Hospital: whole project', kind: 'ProjectGuide', projectId: 'p-1', projectName: 'Kauvery Hospital', flowId: null, versionNo: 1,
  createdAt: new Date().toISOString(), createdByName: 'Asha Lead', formats: ['pdf'], hasDiagram: false, moduleId: null, ...over })
const job = (state: string, over: object = {}) => ({ jobId: 'j-1', kind: 'GuideProjectDocument', state, percent: null, message: null, resultId: null, createdAt: new Date().toISOString(), ...over })

const routes = (over: Record<string, unknown> = {}) => ({
  ...standard, 'GET /projects/p-1/flows': () => flows, 'GET /projects/p-1/departments': () => [ward, pharmacy, billing],
  'GET /flows/f-1': () => flows[0], 'GET /flows/f-1/entries': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [],
  'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
  'GET /projects/p-1/guide-outline': () => outline(), 'GET /projects/p-1/modules/d-2/guide-outline': () => outline(),
  'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
  ...over,
})

async function panel() {
  const list = await screen.findByRole('list', { name: 'PDFs' })
  return list
}

describe('the PDFs of the whole project and of each module', () => {
  it('lists the whole project and each module with how many requests are analysed, and only offers Make where something is analysed', async () => {
    signedIn(); mockApi(routes()); app('/p/p-1')
    const list = await panel()
    const whole = within(list).getByText('Whole project').closest('li')!
    await within(list).findByText('Ward')                                    // the modules arrive a moment after the list
    expect(within(whole).getByText('2 of 4 requests analysed')).toBeInTheDocument()
    expect(within(list).getByText('Ward').closest('li')).toHaveTextContent('1 of 2 requests analysed')
    expect(within(list).getByText('Pharmacy').closest('li')).toHaveTextContent('1 of 1 request analysed')
    const billingRow = within(list).getByText('Billing').closest('li')!
    expect(within(billingRow).getByRole('button', { name: 'Make the PDF of Billing' })).toBeDisabled()          // nothing analysed there
    expect(within(whole).getByRole('button', { name: 'Make the PDF of Whole project' })).toBeEnabled()
  })

  it('makes the project PDF in the background, then offers Open and Download and an Update button', async () => {
    signedIn()
    let made = false
    const server = mockApi(routes({
      'POST /projects/p-1/guide': () => { made = true; return job('Queued') },
      'GET /jobs/j-1': () => job('Succeeded', { resultId: 'doc-1' }),
      'GET /library': () => (made ? [doc({})] : []),
      'GET /documents/doc-1/versions/1/download': () => new Response('%PDF-1.4', { status: 200, headers: { 'Content-Type': 'application/pdf' } }),
    }))
    app('/p/p-1')
    const list = await panel()
    const whole = within(list).getByText('Whole project').closest('li')!
    expect(within(whole).queryByRole('button', { name: /Open/ })).not.toBeInTheDocument()
    const user = userEvent.setup()
    await user.click(within(whole).getByRole('button', { name: 'Make the PDF of Whole project' }))
    await confirmPdf(user)                       // the headings are shown first
    await waitFor(() => expect(server.count('POST', 'projects/p-1/guide')).toBe(1))
    expect(server.calls.find(c => c.path === 'projects/p-1/guide')!.headers.get('Idempotency-Key')).toBeTruthy()
    expect(await screen.findByText('The PDF is ready.', {}, { timeout: 8000 })).toBeInTheDocument()
    await waitFor(() => expect(within(whole).getByRole('button', { name: 'Update the PDF of Whole project' })).toBeInTheDocument())
    expect(within(whole).getByRole('button', { name: /Open/ })).toBeInTheDocument()
    expect(within(whole).getByRole('button', { name: 'Download the PDF of Whole project' })).toBeInTheDocument()
    expect(whole).toHaveTextContent('IMPL-KMC-FULL')
  }, 20_000)

  it('makes the PDF of one module from its own row', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /projects/p-1/modules/d-2/guide': () => job('Queued', { kind: 'GuideModuleDocument' }), 'GET /jobs/j-1': () => job('Running') }))
    app('/p/p-1')
    const list = await panel()
    const user = userEvent.setup()
    await user.click(await within(list).findByRole('button', { name: 'Make the PDF of Pharmacy' }))
    await confirmPdf(user)
    await waitFor(() => expect(server.count('POST', 'projects/p-1/modules/d-2/guide')).toBe(1))
    expect(server.count('POST', 'projects/p-1/guide')).toBe(0)
    expect(await screen.findByText(/The PDF is being made/)).toBeInTheDocument()
  })

  it('shows a module PDF that was made before, and says why when the server refuses', async () => {
    signedIn()
    mockApi(routes({
      'GET /library': () => [doc({ id: 'doc-2', kind: 'ModuleGuide', docNumber: 'IMPL-WARD-MOD', moduleId: 'd-1', versionNo: 3 })],
      'POST /projects/p-1/guide': () => problem(422, 'No request in this project has been analysed yet. Analyse at least one request first.', undefined, { code: 'not_analysed' }),
    }))
    app('/p/p-1')
    const list = await panel()
    const wardRow = (await within(list).findByText('Ward')).closest('li')!
    await waitFor(() => expect(wardRow).toHaveTextContent('IMPL-WARD-MOD'))
    expect(wardRow).toHaveTextContent('version 3')
    expect(within(wardRow).getByRole('button', { name: 'Update the PDF of Ward' })).toBeInTheDocument()
    const user = userEvent.setup()
    await user.click(within(list).getByRole('button', { name: 'Make the PDF of Whole project' }))
    await confirmPdf(user)
    expect(await screen.findByRole('alert')).toHaveTextContent('Analyse at least one request first.')
  })

  it('does not offer to make anything to somebody who may only read, but still lets them open what exists', async () => {
    signedIn()
    mockApi(routes({
      'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }),
      'GET /library': () => [doc({})],
    }))
    app('/p/p-1')
    const list = await panel()
    expect(await within(list).findByRole('button', { name: /Open/ })).toBeInTheDocument()
    expect(within(list).queryByRole('button', { name: /Make the PDF|Update the PDF/ })).not.toBeInTheDocument()
  })

  it('a colleague on the client (Collaborator) can make the module PDFs and the project PDF, an executive only the module ones', async () => {
    signedIn()
    mockApi(routes({ 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Collaborator' }] }) }))
    app('/p/p-1')
    const list = await panel()
    expect(await within(list).findByRole('button', { name: 'Make the PDF of Ward' })).toBeInTheDocument()
    expect(within(list).getByRole('button', { name: 'Make the PDF of Whole project' })).toBeInTheDocument()
  })
})
