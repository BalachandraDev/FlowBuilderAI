import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, confirmPdf, contentDto, dept, flow, outline, signedIn, standard } from './test/fixtures'
import { mockApi, problem } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const now = () => new Date().toISOString()
const job = (over: object = {}) => ({ jobId: 'j-1', kind: 'GuideDocument', state: 'Queued', percent: null, message: null, resultId: null, createdAt: now(), ...over })
const analysed = { ...flow, currentVersionNo: 1 }
const ward = { ...dept, id: 'd-1', name: 'Ward' }
const pharmacy = { ...dept, id: 'd-2', moduleCode: 'PHARMACY', name: 'Pharmacy' }
const two = [
  { ...flow, id: 'f-1', projectDepartmentId: 'd-1', menuName: 'Bed allocation', currentVersionNo: 1 },
  { ...flow, id: 'f-3', projectDepartmentId: 'd-2', menuName: 'Returns', currentVersionNo: 2 },
  { ...flow, id: 'f-4', projectDepartmentId: 'd-2', menuName: 'Refunds', currentVersionNo: 1 },
]
const requests = [
  { flowId: 'f-1', code: 'NR-001', title: 'Bed allocation', module: 'Ward' },
  { flowId: 'f-3', code: 'NR-001', title: 'Returns', module: 'Pharmacy' },
  { flowId: 'f-4', code: 'NR-002', title: 'Refunds', module: 'Pharmacy' },
]

function single(over: Record<string, unknown> = {}) {
  return {
    ...standard, 'GET /projects/p-1/flows': () => [analysed], 'GET /flows/f-1': () => analysed, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [],
    'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
    'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
    'POST /flows/f-1/guide': () => job(), 'GET /jobs/j-1': () => job({ state: 'Running' }), ...over,
  }
}

describe('choosing what goes into the document of one request', () => {
  it('shows every heading with what is under it, all ticked, before anything is made', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Create the implementation document' }))

    const list = await screen.findByRole('list', { name: 'Headings' })
    const rows = within(list).getAllByRole('checkbox')
    expect(rows).toHaveLength(5)
    rows.forEach(r => expect(r).toBeChecked())
    expect(within(list).getByText('Functional requirements')).toBeInTheDocument()
    expect(within(list).getByText('2 rules, 3 requirements')).toBeInTheDocument()
    expect(screen.getByText('5 of 5 ticked')).toBeInTheDocument()
    expect(server.count('POST', 'flows/f-1/guide')).toBe(0)                                  // nothing is made yet
  })

  it('shows the document itself beside the headings, with the cover and every ticked heading numbered, and it follows the ticks', async () => {
    signedIn(); mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    const frame = await screen.findByTitle('The document as it will print') as HTMLIFrameElement
    expect(frame.getAttribute('sandbox')).not.toContain('allow-scripts')                          // the page can never run a script
    let html = frame.getAttribute('srcdoc') ?? ''
    expect(html).toContain('Implementation Document')                                             // the cover
    for (const [n, t] of [[1, 'Document control'], [2, 'Purpose'], [3, 'Functional requirements'], [4, 'How it works, stage by stage'], [5, 'Approval and sign-off']])
      expect(html).toContain(`<h2><span>${n}</span>${t}</h2>`)
    expect(html).toContain('Show the default ward to a super user.')                              // the real content, not a description of it
    expect(html).not.toContain('\u0001')
    expect(html).toContain('body { font-family: sans-serif }')                                    // in the document's own style

    await user.click(screen.getByRole('checkbox', { name: /Document control/ }))
    await user.click(screen.getByRole('checkbox', { name: /Functional requirements/ }))
    html = (screen.getByTitle('The document as it will print') as HTMLIFrameElement).getAttribute('srcdoc') ?? ''
    expect(html).not.toContain('Document control')
    expect(html).not.toContain('The ward is pre-selected.')
    expect(html).toContain('<h2><span>1</span>Purpose</h2>')                                      // numbered again without gaps
    expect(html).toContain('<h2><span>2</span>How it works, stage by stage</h2>')
    expect(html).toContain('<h2><span>3</span>Approval and sign-off</h2>')
  })

  it('shows nothing to preview when nothing is ticked', async () => {
    signedIn(); mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    await screen.findByTitle('The document as it will print')
    await user.click(screen.getByRole('button', { name: 'Untick all' }))
    expect(screen.queryByTitle('The document as it will print')).not.toBeInTheDocument()
    expect(screen.getByText('Nothing to show')).toBeInTheDocument()
  })
  it('sends only the ticked headings', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    await user.click(await screen.findByRole('checkbox', { name: /Document control/ }))
    await user.click(screen.getByRole('checkbox', { name: /Approval and sign-off/ }))
    expect(screen.getByText('3 of 5 ticked')).toBeInTheDocument()
    await confirmPdf(user)

    await waitFor(() => expect(server.count('POST', 'flows/f-1/guide')).toBe(1))
    expect(server.calls.find(c => c.path === 'flows/f-1/guide')!.body).toEqual({ sections: ['purpose', 'requirements', 'stages'] })
    await waitFor(() => expect(screen.queryByRole('list', { name: 'Headings' })).not.toBeInTheDocument())      // the dialog closes
  })

  it('cannot be made with nothing ticked, and Tick all brings everything back', async () => {
    signedIn(); mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    await user.click(await screen.findByRole('button', { name: 'Untick all' }))

    expect(screen.getByText('Tick at least one heading.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Make the PDF' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Tick all' }))
    expect(screen.getByRole('button', { name: 'Make the PDF' })).toBeEnabled()
    expect(screen.getByText('5 of 5 ticked')).toBeInTheDocument()
  })

  it('makes nothing when the person cancels, and says why it cannot be shown when the server refuses', async () => {
    signedIn()
    let refuse = false
    const server = mockApi(single({ 'GET /flows/f-1/guide-outline': () => (refuse ? problem(422, 'Analyse the notes first. There is nothing to write a document from yet.', undefined, { code: 'not_analysed' }) : outline()) }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    await user.click(await screen.findByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('list', { name: 'Headings' })).not.toBeInTheDocument()
    expect(server.count('POST', 'flows/f-1/guide')).toBe(0)

    refuse = true
    await user.click(screen.getByRole('button', { name: 'Create the implementation document' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Analyse the notes first.')
    expect(screen.getByRole('button', { name: 'Make the PDF' })).toBeDisabled()
  })

  it('shows Update when a document already exists and sends the choice for the update', async () => {
    signedIn()
    const guide = { id: 'd-1', docNumber: 'IMPL-KMC-001', title: 'x', scope: 'Guide', flowId: 'f-1', currentVersionNo: 1,
      versions: [{ versionNo: 1, generatedAt: now(), generatedByName: 'Asha Lead', approvalStatus: 'Pending', templateVersion: 'x', sourceFlowVersionNos: [1], approvedByName: null, approvalNote: null }] }
    const server = mockApi(single({ 'GET /flows/f-1/documents': () => [guide] })); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Update the document' }))
    await user.click(await screen.findByRole('checkbox', { name: /How it works, stage by stage/ }))
    await confirmPdf(user, 'Update the PDF')

    await waitFor(() => expect(server.count('POST', 'flows/f-1/guide')).toBe(1))
    expect(server.calls.find(c => c.path === 'flows/f-1/guide')!.body).toEqual({ sections: ['control', 'purpose', 'requirements', 'signoff'] })
  })
})

describe('the one-step button asks after the analysis, before the document is written', () => {
  const fresh = { ...flow, status: 'Draft', currentVersionNo: 0, openQuestionCount: 0, entryCount: 2 }
  const run = (status: string) => ({ id: 'r-1', scope: 'Part', mode: 'Initial', status, flowId: 'f-1', model: 'm', inputTokens: 1, outputTokens: 1, durationMs: 1, attempts: 1, errorCode: null, errorMessage: null, resultVersionNo: status === 'Succeeded' ? 1 : null, warnings: [], startedAt: null, finishedAt: null, createdAt: now() })

  it('makes no document when the person cancels, and offers it again', async () => {
    signedIn()
    let analysedNow = false
    const server = mockApi(single({
      'GET /projects/p-1/flows': () => [analysedNow ? analysed : fresh], 'GET /flows/f-1': () => (analysedNow ? analysed : fresh),
      'POST /flows/f-1/analyses': () => { analysedNow = true; return run('Queued') }, 'GET /flows/f-1/analyses': () => (analysedNow ? [run('Succeeded')] : []),
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Analyse and make the document' }))
    await screen.findByRole('list', { name: 'Headings' }, { timeout: 8000 })
    expect(server.count('POST', 'flows/f-1/analyses')).toBe(1)
    expect(server.count('POST', 'flows/f-1/guide')).toBe(0)                                    // asked first
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(await screen.findByText(/Nothing was made\. Your notes are analysed/)).toBeInTheDocument()
    expect(server.count('POST', 'flows/f-1/guide')).toBe(0)
    expect(await screen.findByRole('button', { name: 'Make the document' })).toBeEnabled()
  }, 20_000)
})

describe('choosing what goes into the PDF of a project or a module', () => {
  const projectRoutes = (over: Record<string, unknown> = {}) => ({
    ...standard, 'GET /projects/p-1/flows': () => two, 'GET /projects/p-1/departments': () => [ward, pharmacy],
    'GET /flows/f-1': () => two[0], 'GET /flows/f-1/entries': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [],
    'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
    'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
    'GET /projects/p-1/guide-outline': () => outline(requests), 'GET /projects/p-1/modules/d-2/guide-outline': () => outline(requests.slice(1)),
    ...over,
  })

  it('lists the requests by module with the headings, and sends the headings and the requests that stay ticked', async () => {
    signedIn()
    const server = mockApi(projectRoutes({ 'POST /projects/p-1/guide': () => job({ kind: 'GuideProjectDocument' }) })); app('/p/p-1')
    const user = userEvent.setup()
    const pdfs = await screen.findByRole('list', { name: 'PDFs' })
    await user.click(within(pdfs).getByRole('button', { name: 'Make the PDF of Whole project' }))

    expect(await screen.findByRole('list', { name: 'Requests of Ward' })).toBeInTheDocument()
    const pharmacyList = screen.getByRole('list', { name: 'Requests of Pharmacy' })
    expect(within(pharmacyList).getAllByRole('checkbox')).toHaveLength(2)
    expect(screen.getByText('3 of 3 ticked', { selector: '[role="status"]' }) ?? true).toBeTruthy()

    const chapters = () => (screen.getByTitle('The document as it will print') as HTMLIFrameElement).getAttribute('srcdoc') ?? ''
    expect(chapters()).toContain('Bed allocation'); expect(chapters()).toContain('Refunds')       // a chapter for each request
    await user.click(within(pharmacyList).getByRole('checkbox', { name: /Refunds/ }))
    expect(chapters()).not.toContain('Refunds')                                                    // leaving a request out takes its chapter out
    expect((chapters().match(/<h2><span>1<\/span>Document control/g) ?? []).length).toBe(2)        // numbering starts again in each chapter
    await user.click(screen.getByRole('checkbox', { name: /Document control/ }))
    await confirmPdf(user)

    await waitFor(() => expect(server.count('POST', 'projects/p-1/guide')).toBe(1))
    expect(server.calls.find(c => c.path === 'projects/p-1/guide')!.body).toEqual({ sections: ['purpose', 'requirements', 'stages', 'signoff'], flowIds: ['f-1', 'f-3'] })
  })

  it('cannot be made with every request unticked', async () => {
    signedIn(); mockApi(projectRoutes()); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(within(await screen.findByRole('list', { name: 'PDFs' })).getByRole('button', { name: 'Make the PDF of Whole project' }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByRole('list', { name: 'Requests of Ward' })
    const untick = within(dialog).getAllByRole('button', { name: 'Untick all' })
    await user.click(untick[1])                                                                   // the second one is for the requests
    expect(within(dialog).getByText('Tick at least one request.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Make the PDF' })).toBeDisabled()
  })

  it('a module PDF is chosen from its own outline', async () => {
    signedIn()
    const server = mockApi(projectRoutes({ 'POST /projects/p-1/modules/d-2/guide': () => job({ kind: 'GuideModuleDocument' }) })); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await within(await screen.findByRole('list', { name: 'PDFs' })).findByRole('button', { name: 'Make the PDF of Pharmacy' }))
    expect(await screen.findByRole('list', { name: 'Requests of Pharmacy' })).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Requests of Ward' })).not.toBeInTheDocument()
    await confirmPdf(user)
    await waitFor(() => expect(server.count('POST', 'projects/p-1/modules/d-2/guide')).toBe(1))
    expect(server.calls.find(c => c.path === 'projects/p-1/modules/d-2/guide')!.body).toEqual({})
  })
})
