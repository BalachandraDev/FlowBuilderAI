import { fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { kindOfFile } from './lib/attach'
import { app, entry, signedIn, standard } from './test/fixtures'
import { mockApi, problem } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const photoEntry = { ...entry, id: 'e-9', refNo: 9, kind: 'Image', rawText: null, transcriptState: 'Queued' }
const routes = (over: Record<string, () => object | undefined> = {}) => ({
  ...standard, 'GET /flows/f-1/entries': () => [], 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/documents': () => [],
  'POST /flows/f-1/images': () => photoEntry, 'POST /flows/f-1/entries': () => entry, ...over,
})
const f = (name: string, type: string, body = 'x') => new File([body], name, { type })

describe('attaching files from the notes panel', () => {
  it('offers one Attach files button next to the others and takes a mix of photos and text files at once', async () => {
    signedIn()
    const server = mockApi(routes())
    app('/p/p-1')
    await screen.findByRole('group', { name: 'Add notes' })
    expect(screen.getByRole('button', { name: /Attach files/ })).toBeEnabled()
    const input = screen.getByLabelText('Choose files to attach') as HTMLInputElement
    expect(input.multiple).toBe(true)
    expect(input.accept).toContain('image/*')
    expect(input.accept).toContain('audio/*')
    expect(input.accept).toContain('.txt')

    await userEvent.setup().upload(input, [f('page1.jpg', 'image/jpeg'), f('notes.txt', 'text/plain', 'The nurse opens the discharge menu.'), f('page2.png', 'image/png')])

    await waitFor(() => expect(server.count('POST', 'flows/f-1/images')).toBe(2))
    await waitFor(() => expect(server.calls.filter(c => c.method === 'POST' && c.path === 'flows/f-1/entries').map(c => c.body)).toEqual([expect.objectContaining({ kind: 'Text', rawText: 'The nurse opens the discharge menu.' })]))
    expect(await screen.findByText('Photo 2 of 2 sent. It is being read.')).toBeInTheDocument()
    expect(await screen.findByText('Note added.')).toBeInTheDocument()
  })

  it('names the files it cannot use and still adds the others', async () => {
    signedIn()
    const server = mockApi(routes())
    app('/p/p-1')
    await screen.findByRole('group', { name: 'Add notes' })
    await userEvent.setup().upload(screen.getByLabelText('Choose files to attach'), [f('talk.mp3', 'audio/mpeg'), f('a.jpg', 'image/jpeg')])
    expect(await screen.findByText(/talk\.mp3: This recording could not be opened/)).toBeInTheDocument()      // jsdom cannot decode audio: the real browser can
    await waitFor(() => expect(server.count('POST', 'flows/f-1/images')).toBe(1))

    // the picker greys out other kinds of file, but a file dropped on the page can be anything
    const area = screen.getByRole('group', { name: 'Add notes' }).closest('.notes') as HTMLElement
    fireEvent.drop(area, { dataTransfer: { types: ['Files'], files: [f('plan.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document')] } })
    expect(await screen.findByText(/plan\.docx: This kind of file cannot be added yet/)).toBeInTheDocument()
  })

  it('takes Excel workbooks and PDFs: they are sent to the server, which reads the text (no AI wait), and the reason is shown when one cannot be read', async () => {
    signedIn()
    const noteFromFile = { ...entry, id: 'e-8', refNo: 8, kind: 'Text', rawText: 'Sheet: Wards', notes: 'From the file wards.xlsx' }
    let n = 0
    const server = mockApi(routes({
      'POST /flows/f-1/files': () => (++n === 3 ? problem(422, 'This PDF has no text in it (it looks like a scan). Take photos of the pages and attach those pictures instead.', undefined, { code: 'pdf_no_text' }) as unknown as object : noteFromFile),
    }))
    app('/p/p-1')
    await screen.findByRole('group', { name: 'Add notes' })
    const input = screen.getByLabelText('Choose files to attach') as HTMLInputElement
    expect(input.accept).toContain('.xlsx')
    expect(input.accept).toContain('.pdf')
    await userEvent.setup().upload(input, [
      f('wards.xlsx', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'),
      f('flow.pdf', 'application/pdf'),
      f('scan.pdf', 'application/pdf'),
    ])
    await waitFor(() => expect(server.count('POST', 'flows/f-1/files')).toBe(3))
    expect(server.count('POST', 'flows/f-1/images')).toBe(0)
    expect(await screen.findByText(/scan\.pdf: This PDF has no text in it/)).toBeInTheDocument()
    expect(await screen.findAllByText(/read and added as a note/)).not.toHaveLength(0)
  })

  it('does not need the AI to be switched on for Excel and PDF files, and says an old .xls must be saved again as .xlsx', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /flows/f-1/files': () => problem(422, 'Old Excel files (.xls) cannot be read. In Excel choose Save As, Excel Workbook (.xlsx), then attach that file.', undefined, { code: 'xls_not_supported' }) as unknown as object }))
    app('/p/p-1')
    await screen.findByRole('group', { name: 'Add notes' })
    await userEvent.setup().upload(screen.getByLabelText('Choose files to attach'), [f('old.xls', 'application/vnd.ms-excel')])
    expect(await screen.findByText(/old\.xls: Old Excel files \(\.xls\) cannot be read/)).toBeInTheDocument()
    expect(server.count('POST', 'flows/f-1/files')).toBe(1)
  })

  it('sorts files by their kind', () => {
    expect(kindOfFile({ name: 'a.xlsx', type: '' })).toBe('document')
    expect(kindOfFile({ name: 'a.XLS', type: 'application/vnd.ms-excel' })).toBe('document')
    expect(kindOfFile({ name: 'a', type: 'application/pdf' })).toBe('document')
    expect(kindOfFile({ name: 'notes.csv', type: 'text/csv' })).toBe('text')
    expect(kindOfFile({ name: 'a.docx', type: '' })).toBe('other')
  })

  it('accepts files dropped on the notes, and shows a hint about it', async () => {
    signedIn()
    const server = mockApi(routes())
    app('/p/p-1')
    const group = await screen.findByRole('group', { name: 'Add notes' })
    expect(screen.getByText(/You can also drop photos, recordings, Excel, PDF and text files here/)).toBeInTheDocument()
    const area = group.closest('.notes') as HTMLElement

    fireEvent.dragOver(area, { dataTransfer: { types: ['Files'], files: [] } })
    expect(area).toHaveClass('is-dragging')
    fireEvent.drop(area, { dataTransfer: { types: ['Files'], files: [f('dropped.jpg', 'image/jpeg')] } })

    await waitFor(() => expect(server.count('POST', 'flows/f-1/images')).toBe(1))
    await waitFor(() => expect(area).not.toHaveClass('is-dragging'))
  })

  it('still adds text files while photos and recordings are switched off, and says why the photos were refused', async () => {
    signedIn()
    const server = mockApi(routes({ 'GET /system/info': () => ({ ai: 'gemini', notice: null, allowMedia: false }) }))
    app('/p/p-1')
    await screen.findByRole('group', { name: 'Add notes' })
    expect(await screen.findByText(/switched off by the administrator/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Attach files/ })).toBeEnabled()
    await userEvent.setup().upload(screen.getByLabelText('Choose files to attach'), [f('a.jpg', 'image/jpeg'), f('notes.txt', 'text/plain', 'Typed in a file.')])

    expect(await screen.findByText(/a\.jpg: Photos and voice recordings are switched off by the administrator/)).toBeInTheDocument()
    await waitFor(() => expect(server.calls.some(c => c.method === 'POST' && c.path === 'flows/f-1/entries')).toBe(true))
    expect(server.count('POST', 'flows/f-1/images')).toBe(0)
  })

  it('is not offered to someone who may not add notes', async () => {
    signedIn()
    mockApi(routes({ 'POST /auth/refresh': () => ({ ...standard['POST /auth/refresh'](), user: { ...standard['POST /auth/refresh']().user, projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }], roles: ['Reviewer'] } }) }))
    app('/p/p-1')
    expect(await screen.findByText(/You can read these notes but not add to them/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Attach files/ })).not.toBeInTheDocument()
  })
})