import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, confirmPdf, contentDto, entry, flow, signedIn, standard } from './test/fixtures'
import { mockApi } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const now = () => new Date().toISOString()
const run = (status: string) => ({ id: 'r-1', scope: 'Part', mode: 'Initial', status, flowId: 'f-1', model: 'm', inputTokens: 1, outputTokens: 1, durationMs: 1, attempts: 1, errorCode: null, errorMessage: status === 'Failed' ? 'The free AI limit has been reached for now.' : null, resultVersionNo: status === 'Succeeded' ? 1 : null, warnings: [], startedAt: null, finishedAt: null, createdAt: now() })
const job = (state: string, message: string | null = null) => ({ jobId: 'j-1', kind: 'GuideDocument', state, percent: null, message, resultId: null, createdAt: now() })
const fresh = { ...flow, status: 'Draft', currentVersionNo: 0, openQuestionCount: 0, entryCount: 2 }
const photo = (state: string) => ({ ...entry, id: 'e-2', refNo: 2, kind: 'Image', transcriptState: state, rawText: null })

function routes(over: Record<string, () => object | undefined> = {}) {
  return {
    ...standard,
    'GET /projects/p-1/flows': () => [fresh], 'GET /flows/f-1': () => fresh,
    'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/documents': () => [],
    'GET /flows/f-1/content': () => contentDto(1),
    ...over,
  }
}

describe('make the document in one step', () => {
  it('reads the notes, analyses and makes the document without the user doing anything in between', async () => {
    signedIn()
    let analysed = false
    const server = mockApi(routes({
      'POST /flows/f-1/analyses': () => { analysed = true; return run('Queued') },
      'GET /flows/f-1/analyses': () => (analysed ? [run('Succeeded')] : []),
      'POST /flows/f-1/guide': () => job('Queued'),
      'GET /jobs/j-1': () => job('Succeeded'),
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Analyse and make the document' }))
    await confirmPdf(user)                  // the headings are shown after the analysis; all are ticked
    expect(await screen.findByText('The implementation document is ready.')).toBeInTheDocument()

    const order = server.calls.filter(c => c.method === 'POST' && /analyses|guide/.test(c.path)).map(c => c.path)
    expect(order).toEqual(['flows/f-1/analyses', 'flows/f-1/guide'])                       // analysis first, then the document
    expect(server.calls.find(c => c.path === 'flows/f-1/analyses' && c.method === 'POST')!.body).toEqual({ mode: 'Initial', entryIds: null })
    expect(server.calls.find(c => c.path === 'flows/f-1/guide')!.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('goes straight to the document when the workflow is already analysed', async () => {
    signedIn()
    const analysedFlow = { ...flow, currentVersionNo: 1, openQuestionCount: 0 }
    const server = mockApi(routes({
      'GET /projects/p-1/flows': () => [analysedFlow], 'GET /flows/f-1': () => analysedFlow,
      'POST /flows/f-1/guide': () => job('Queued'), 'GET /jobs/j-1': () => job('Succeeded'),
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Make the document' }))
    await confirmPdf(user)
    await waitFor(() => expect(server.count('POST', 'flows/f-1/guide')).toBe(1))
    expect(server.count('POST', 'flows/f-1/analyses')).toBe(0)
    expect(server.calls.find(c => c.path === 'flows/f-1/guide')!.body).toEqual({})                  // everything ticked: the complete document
  })

  it('does not start while a photo is still being read, and carries on once it has been', async () => {
    signedIn()
    let polls = 0
    const server = mockApi(routes({
      'GET /flows/f-1/entries': () => [entry, photo(++polls < 3 ? 'Processing' : 'Done')],
      'POST /flows/f-1/analyses': () => run('Queued'), 'GET /flows/f-1/analyses': () => [run('Succeeded')],
      'POST /flows/f-1/guide': () => job('Queued'), 'GET /jobs/j-1': () => job('Succeeded'),
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Analyse and make the document' }))
    await confirmPdf(user, 'Make the PDF', 12_000)
    expect(await screen.findByText('The implementation document is ready.', {}, { timeout: 12_000 })).toBeInTheDocument()
    expect(server.count('POST', 'flows/f-1/analyses')).toBe(1)                               // only one analysis, after the photo was read
  }, 20_000)

  it('stops and says which notes could not be read, without spending an analysis', async () => {
    signedIn()
    const server = mockApi(routes({ 'GET /flows/f-1/entries': () => [entry, photo('Failed')] }))
    app('/p/p-1')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Analyse and make the document' }))
    await waitFor(() => expect(screen.getAllByText(/1 recording or photo could not be read/).length).toBeGreaterThan(1))   // here and in the analysis panel
    expect(server.count('POST', 'flows/f-1/analyses')).toBe(0)
    expect(screen.getByRole('button', { name: 'Analyse and make the document' })).toBeEnabled()   // and it can be tried again
  })

  it('shows why the analysis failed and does not try to make a document from nothing', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /flows/f-1/analyses': () => run('Queued'), 'GET /flows/f-1/analyses': () => [run('Failed')] }))
    app('/p/p-1')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Analyse and make the document' }))
    expect(await screen.findByText(/The analysis did not finish: The free AI limit has been reached for now\./)).toBeInTheDocument()
    expect(server.count('POST', 'flows/f-1/guide')).toBe(0)
  })

  it('is not offered to someone who cannot add notes, to an empty project, or once the document exists', async () => {
    signedIn()
    mockApi(routes({ 'GET /projects/p-1/flows': () => [{ ...fresh, entryCount: 0 }], 'GET /flows/f-1': () => ({ ...fresh, entryCount: 0 }) }))
    const first = app('/p/p-1')
    expect(await screen.findByText('Add your notes')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /make the document/i })).not.toBeInTheDocument()
    first.unmount()

    const analysedFlow = { ...flow, currentVersionNo: 1 }
    mockApi(routes({
      'GET /projects/p-1/flows': () => [analysedFlow], 'GET /flows/f-1': () => analysedFlow,
      'GET /flows/f-1/documents': () => [{ id: 'd-1', scope: 'Guide', docNumber: 'IMPL-KMC-001', title: 'x', versions: [{ versionNo: 1, generatedByName: 'Asha', generatedAt: now(), sourceFlowVersionNos: [1], approvalStatus: 'Pending' }] }],
    }))
    app('/p/p-1')
    expect(await screen.findByRole('button', { name: 'Update the document' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^make the document$/i })).not.toBeInTheDocument()
  })
})

describe('photos and voice switched off by the administrator', () => {
  it('disables the buttons and says why, while typing still works', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /system/info': () => ({ ai: 'gemini', notice: null, allowMedia: false }), 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/documents': () => [] })
    app('/p/p-1')
    expect(await screen.findByText(/Photos and voice recordings are switched off by the administrator/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Record voice/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: /Add photos/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: /Type a note/ })).toBeEnabled()
  })
})
