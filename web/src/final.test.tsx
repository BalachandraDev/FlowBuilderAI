import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, contentDto, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }
const now = () => new Date().toISOString()
const doc = (over: object = {}) => ({ id: 'doc-1', docNumber: 'IMPL-KMC-NR-001', title: 'Bed allocation', kind: 'Guide', projectId: 'p-1', projectName: 'Kauvery Hospital', flowId: 'f-1', versionNo: 1,
  createdAt: now(), createdByName: 'Asha Lead', formats: ['pdf'], hasDiagram: false, moduleId: null, stage: 'Working', clientId: 'c-1', clientName: 'Kauvery Group', ...over })

describe('a user marks a document as final, to give to the team', () => {
  it('asks first, sends the note, and then shows the document as final with a way to take it back', async () => {
    signedIn()
    let marked = false
    const server = mockApi({
      ...standard,
      'GET /library': () => [marked ? doc({ stage: 'Final', finalizedByName: 'Asha Lead', finalizedAt: now(), finalNote: 'Please check the scope' }) : doc()],
      'POST /documents/doc-1/versions/1/final': () => { marked = true; return {} },
    })
    app('/documents')
    const user = userEvent.setup()
    expect(await screen.findByText('Working copy')).toBeInTheDocument()
    await user.click(await screen.findByRole('button', { name: 'Mark as final' }))

    const dialog = await screen.findByRole('dialog', { name: 'Mark this document as final?' })
    expect(within(dialog).getByText((_, el) => el?.tagName === 'P' && /hands version 1 to the administrator/.test(el.textContent ?? ''))).toBeInTheDocument()
    expect(server.count('POST', 'documents/doc-1/versions/1/final')).toBe(0)                  // nothing is sent until it is confirmed
    await user.type(within(dialog).getByLabelText(/Note for the administrator/), 'Please check the scope')
    await user.click(within(dialog).getByRole('button', { name: 'Mark as final' }))

    await waitFor(() => expect(server.count('POST', 'documents/doc-1/versions/1/final')).toBe(1))
    expect(server.calls.find(c => c.path === 'documents/doc-1/versions/1/final')!.body).toEqual({ note: 'Please check the scope' })
    expect(await screen.findByText('Final · waiting for the administrator')).toBeInTheDocument()
    expect(screen.getByText('Your note: Please check the scope')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Take back' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Mark as final' })).not.toBeInTheDocument()
  })

  it('takes the final mark back', async () => {
    signedIn()
    let back = false
    const server = mockApi({
      ...standard,
      'GET /library': () => [back ? doc() : doc({ stage: 'Final', finalizedByName: 'Asha Lead', finalizedAt: now() })],
      'DELETE /documents/doc-1/versions/1/final': () => { back = true; return {} },
    })
    app('/documents')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Take back' }))
    await waitFor(() => expect(server.count('DELETE', 'documents/doc-1/versions/1/final')).toBe(1))
    expect(await screen.findByRole('button', { name: 'Mark as final' })).toBeInTheDocument()
  })

  it('shows why the administrator returned a document, and that a new version is needed; a verified one has nothing more to do', async () => {
    signedIn()
    mockApi({
      ...standard,
      'GET /library': () => [doc({ stage: 'Returned', finalizedAt: now(), reviewNote: 'The scope of the second module is missing' }),
        doc({ id: 'doc-2', docNumber: 'IMPL-KMC-NR-002', title: 'Visitor pass', stage: 'Verified', finalizedAt: now(), verifiedByName: 'Dev Admin' })],
    })
    app('/documents')
    expect(await screen.findByText(/The scope of the second module is missing/)).toBeInTheDocument()
    expect(screen.getByText(/Update the document, then mark the new version as final/)).toBeInTheDocument()
    expect(screen.getByText('Verified by the administrator')).toBeInTheDocument()
    expect(screen.queryAllByRole('button', { name: 'Mark as final' })).toHaveLength(0)
  })

  it('filters by where the documents stand, and says why when the server refuses', async () => {
    signedIn()
    mockApi({
      ...standard,
      'GET /library': () => [doc(), doc({ id: 'doc-2', docNumber: 'IMPL-KMC-NR-002', title: 'Visitor pass', stage: 'Verified', finalizedAt: now() })],
      'POST /documents/doc-1/versions/1/final': () => problem(422, 'Only the latest version can be marked final.', undefined, { code: 'not_latest' }),
    })
    app('/documents')
    const user = userEvent.setup()
    expect(await screen.findByText('Visitor pass')).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Show'), 'Working')
    expect(screen.queryByText('Visitor pass')).not.toBeInTheDocument()
    expect(screen.getByText('Bed allocation')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Mark as final' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Mark as final' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Only the latest version can be marked final.')
  })

  it('is not offered to somebody who may only read the project', async () => {
    signedIn()
    mockApi({ ...standard, 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }), 'GET /library': () => [doc()] })
    app('/documents')
    expect(await screen.findByText('Working copy')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Mark as final' })).not.toBeInTheDocument()
  })

  it('is on the request page beside the document, for the version shown there', async () => {
    signedIn()
    const guide = { id: 'doc-1', docNumber: 'IMPL-KMC-NR-001', title: 'Bed allocation', scope: 'Guide', flowId: 'f-1', currentVersionNo: 2,
      versions: [{ versionNo: 2, generatedAt: now(), generatedByName: 'Asha Lead', approvalStatus: 'Pending', templateVersion: 'x', sourceFlowVersionNos: [1], approvedByName: null, approvalNote: null }] }
    const server = mockApi({
      ...standard, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/content': () => contentDto(), 'GET /flows/f-1/documents': () => [guide],
      'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
      'POST /documents/doc-1/versions/2/final': () => ({}),
    })
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Mark as final' }))
    expect(await screen.findByText((_, el) => el?.tagName === 'P' && /hands version 2 to the administrator/.test(el.textContent ?? ''))).toBeInTheDocument()
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Mark as final' }))
    await waitFor(() => expect(server.count('POST', 'documents/doc-1/versions/2/final')).toBe(1))
  })
})

describe('the administrator sees only the final documents and verifies them', () => {
  const finals = () => [
    doc({ id: 'd-1', docNumber: 'IMPL-KMC-NR-001', title: 'Bed allocation', stage: 'Final', finalizedAt: now(), finalizedByName: 'Asha Lead', finalNote: 'Please check the scope', clientName: 'Kauvery Group' }),
    doc({ id: 'd-2', docNumber: 'IMPL-APL-NR-004', title: 'Pharmacy returns', stage: 'Verified', finalizedAt: now(), finalizedByName: 'Ravi Shankar', verifiedByName: 'Dev Admin', verifiedAt: now(), clientName: 'Apollo Group' }),
    doc({ id: 'd-3', docNumber: 'IMPL-APL-NR-005', title: 'Visitor pass', stage: 'Returned', finalizedAt: now(), finalizedByName: 'Ravi Shankar', reviewNote: 'Scope is missing', clientName: 'Apollo Group' }),
  ]

  it('lists them with who made each final and for which client, waiting ones first, and shows the counts', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /library': finals }); app('/documents')
    expect(await screen.findByRole('heading', { name: 'Final documents' })).toBeInTheDocument()
    const list = await screen.findByRole('list', { name: 'Final documents' })
    const titles = (await within(list).findAllByRole('heading', { level: 3 })).map(h => h.textContent)
    expect(titles).toEqual(['Bed allocation', 'Visitor pass', 'Pharmacy returns'])            // waiting, then returned, then verified
    const first = within(list).getAllByRole('listitem')[0]
    expect(first).toHaveTextContent('Kauvery Group')
    expect(first).toHaveTextContent('Asha Lead')
    expect(first).toHaveTextContent('Please check the scope')
    const tiles = screen.getByLabelText('Where the documents stand')
    expect(within(tiles).getByText('waiting for you').previousElementSibling).toHaveTextContent('1')
    expect(within(tiles).getByText('verified').previousElementSibling).toHaveTextContent('1')
    expect(screen.queryByRole('button', { name: 'Mark as final' })).not.toBeInTheDocument()
  })

  it('filters by client, by person and by status, and clears the filters', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /library': finals }); app('/documents')
    const user = userEvent.setup()
    await screen.findByText('Pharmacy returns')

    await user.selectOptions(screen.getByLabelText('Client'), 'Apollo Group')
    expect(screen.queryByText('Bed allocation')).not.toBeInTheDocument()
    expect(screen.getByText('Pharmacy returns')).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Status'), 'Returned')
    expect(screen.queryByText('Pharmacy returns')).not.toBeInTheDocument()
    expect(screen.getByText('Visitor pass')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(screen.getByText('Bed allocation')).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Made final by'), 'Asha Lead')
    expect(screen.queryByText('Pharmacy returns')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    await user.click(within(screen.getByLabelText('Where the documents stand')).getByRole('button', { name: /verified/ }))      // a count is also a filter
    expect(screen.queryByText('Bed allocation')).not.toBeInTheDocument()
    expect(screen.getByText('Pharmacy returns')).toBeInTheDocument()
  })

  it('verifies a final document', async () => {
    signedIn()
    const server = mockApi({ ...standard, ...asAdmin, 'GET /library': finals, 'POST /documents/d-1/versions/1/approve': () => ({}) }); app('/documents')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Verify IMPL-KMC-NR-001' }))
    const dialog = await screen.findByRole('dialog', { name: 'Verify this document?' })
    await user.click(within(dialog).getByRole('button', { name: 'Verify' }))
    await waitFor(() => expect(server.count('POST', 'documents/d-1/versions/1/approve')).toBe(1))
    expect(server.calls.find(c => c.path === 'documents/d-1/versions/1/approve')!.body).toEqual({ approved: true, note: null })
    expect(await screen.findByText('IMPL-KMC-NR-001 was verified.')).toBeInTheDocument()
  })

  it('returns a final document only with a reason', async () => {
    signedIn()
    const server = mockApi({ ...standard, ...asAdmin, 'GET /library': finals, 'POST /documents/d-1/versions/1/approve': () => ({}) }); app('/documents')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Return IMPL-KMC-NR-001' }))
    const dialog = await screen.findByRole('dialog', { name: 'Return this document?' })
    expect(within(dialog).getByRole('button', { name: 'Return it' })).toBeDisabled()                    // no reason yet
    await user.type(within(dialog).getByLabelText('Why is it returned?'), 'The scope is missing')
    await user.click(within(dialog).getByRole('button', { name: 'Return it' }))
    await waitFor(() => expect(server.count('POST', 'documents/d-1/versions/1/approve')).toBe(1))
    expect(server.calls.find(c => c.path === 'documents/d-1/versions/1/approve')!.body).toEqual({ approved: false, note: 'The scope is missing' })
  })

  it('offers Verify and Return only on a document that is waiting, and says when nothing has been made final', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /library': () => finals().slice(1) }); app('/documents')
    await screen.findByText('Pharmacy returns')
    expect(screen.queryByRole('button', { name: /^Verify / })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Return / })).not.toBeInTheDocument()
  })

  it('says so when nothing has been made final yet', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /library': () => [] }); app('/documents')
    expect(await screen.findByText('No final documents yet')).toBeInTheDocument()
  })

  it('shows how many are waiting in the menu, for the administrator only', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /admin/overview': () => ({ users: 1, activeUsers: 1, projects: 0, flows: 0, notes: 0, documents: 0, aiProvider: 'gemini', aiReady: true, awaitingVerification: 3, clients: 1 }) }); app('/admin')
    const nav = await screen.findByRole('navigation', { name: 'Main' })
    expect(await within(nav).findByLabelText('3 waiting')).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: /Who handles what/ })).toHaveAttribute('href', '/admin/handling')
  })
})
