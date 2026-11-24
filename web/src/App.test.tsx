import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox, enqueueEntry, queuedEntries } from './offline/outbox'
import { app, confirmPdf, contentDto, entry, flow, project, signedIn, standard, usage } from './test/fixtures'
import { nextStep } from './pages/Workspace'
import { mockApi, problem, tokens } from './test/mockApi'

// the diagram library is large and needs a real browser; the screens only hand it the text
vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }
const asReviewer = { 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }) }
const draft = { ...flow, status: 'Draft', currentVersionNo: 0, openQuestionCount: 0, entryCount: 0 }

/** What the project page asks the server for. */
const workspace = (over: Record<string, unknown> = {}) => ({
  ...standard,
  'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
  'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
  ...over,
})
const voice = { ...entry, id: 'e-2', refNo: 2, kind: 'Voice', rawText: null, audioState: 'Complete', audioDurationSec: 84, transcriptState: 'Ready' }
const photo = { ...entry, id: 'e-3', refNo: 3, kind: 'Image', rawText: null, audioState: 'None', transcriptState: 'Processing' }
const guide = (versionNo = 1, source = 1) => ({ id: 'd-1', docNumber: 'IMPL-KMC-001', title: 'IP Case Sheet Receive', scope: 'Guide', flowId: 'f-1', currentVersionNo: versionNo,
  versions: [{ versionNo, generatedAt: new Date().toISOString(), generatedByName: 'Asha Lead', approvalStatus: 'Pending', templateVersion: 'implementation guide', sourceFlowVersionNos: [source], approvedByName: null, approvalNote: null }] })

describe('signing in', () => {
  it('sends someone who is not signed in to the sign-in page, and explains a wrong password', async () => {
    mockApi({ 'POST /auth/login': () => problem(401, 'Authentication failed.', 'Wrong password') })
    app('/')
    expect(await screen.findByRole('heading', { name: 'Welcome to bbflowbuild' })).toBeInTheDocument()
    const user = userEvent.setup()
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeDisabled()
    await user.type(screen.getByLabelText('Email'), 'asha@demo.local')
    await user.type(screen.getByLabelText('Password'), 'nope')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The email or password is incorrect.')
  })

  it('signs in, shows the projects and keeps the refresh token for the next visit', async () => {
    mockApi({ ...standard, 'POST /auth/login': () => tokens({ refresh: 'rt-9' }) })
    app('/')
    const user = userEvent.setup()
    await user.type(await screen.findByLabelText('Email'), 'asha@demo.local')
    await user.type(screen.getByLabelText('Password'), 'Demo-Passw0rd-123')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('heading', { name: 'Hello, Asha' })).toBeInTheDocument()
    expect(await screen.findByRole('link', { name: 'Open Kauvery Hospital' })).toBeInTheDocument()
    expect(localStorage.getItem('fc.refresh')).toBe('rt-9')
  })

  it('goes straight to the app when a saved sign-in is still good (no flash of the sign-in page)', async () => {
    signedIn(); mockApi(standard); app('/')
    expect(await screen.findByRole('heading', { name: 'Hello, Asha' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Welcome to bbflowbuild' })).not.toBeInTheDocument()
  })

  it('sends an administrator to the admin pages and an ordinary user away from them', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /admin/overview': () => ({ users: 1, activeUsers: 1, projects: 0, flows: 0, notes: 0, documents: 0, aiProvider: 'gemini', aiReady: true }), 'GET /projects': () => [] })
    app('/')
    expect(await screen.findByRole('heading', { name: 'Admin' })).toBeInTheDocument()
    expect(within(screen.getByRole('navigation', { name: 'Main' })).getAllByRole('link').map(l => l.textContent)).toEqual(['Overview', 'Final documents', 'Who handles what', 'Clients', 'Modules', 'Users', 'AI setup', 'Database', 'AI usage'])
  })

  it('does not let an ordinary user open an admin page', async () => {
    signedIn(); mockApi(standard); app('/admin/users')
    expect(await screen.findByRole('heading', { name: 'Hello, Asha' })).toBeInTheDocument()
    expect(within(screen.getByRole('navigation', { name: 'Main' })).queryByText('Users')).not.toBeInTheDocument()
  })
})

describe('home', () => {
  it('shows each project with where it is, so the next thing to do is obvious', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /projects': () => [project, { ...project, id: 'p-2', hospitalName: 'Fresh project' }, { ...project, id: 'p-3', hospitalName: 'Done project' }],
      'GET /projects/p-1/flows': () => [flow], 'GET /projects/p-2/flows': () => [draft], 'GET /projects/p-3/flows': () => [{ ...flow, openQuestionCount: 0 }] })
    app('/')
    expect(await within(await screen.findByRole('link', { name: 'Open Kauvery Hospital' })).findByText('3 questions to answer')).toBeInTheDocument()
    expect(await within(screen.getByRole('link', { name: 'Open Fresh project' })).findByText('Add your notes')).toBeInTheDocument()
    expect(await within(screen.getByRole('link', { name: 'Open Done project' })).findByText('Workflow ready')).toBeInTheDocument()
  })

  it('explains the three steps to someone with no project yet', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects': () => [] }); app('/')
    expect(await screen.findByRole('heading', { name: 'Make your first project' })).toBeInTheDocument()
    for (const step of ['Add notes', 'Let the AI analyse', 'Get the workflow']) expect(screen.getByText(step)).toBeInTheDocument()
  })

  it('shows a clear error, not a blank page, when the list cannot be loaded', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects': () => problem(403, 'Not allowed.', 'You may not list projects.') }); app('/')
    expect(await screen.findByRole('alert')).toHaveTextContent('You may not list projects.')
  })

  it('points to the newest documents of everybody', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /library': () => [{ id: 'd-9', docNumber: 'IMPL-X-001', title: 'Discharge', kind: 'Guide', projectId: 'p-9', projectName: 'Other hospital', flowId: 'f-9', versionNo: 1, createdAt: new Date().toISOString(), createdByName: 'Ravi', formats: ['pdf'], hasDiagram: true }] })
    app('/')
    expect(await screen.findByText('Latest documents')).toBeInTheDocument()
    expect(screen.getByText('Other hospital · Ravi')).toBeInTheDocument()
  })

  it('shows what the server says about the AI at the top: a notice in demo mode', async () => {
    signedIn(); mockApi({ ...standard, 'GET /system/info': () => ({ ai: 'demo', notice: 'AI is in demo mode: analyses return a fixed example.' }) }); app('/')
    expect(await screen.findByText(/AI is in demo mode/)).toBeInTheDocument()
  })
})

describe('a new project', () => {
  it('needs a name, creates the project and its workflow in one step, refreshes the permissions and opens it', async () => {
    signedIn()
    let made = false
    const server = mockApi({
      ...workspace(),
      'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Lead' }, { projectId: 'p-2', projectCode: 'NEW-1234', projectRole: 'Lead' }] }),
      'GET /projects': () => (made ? [project, { ...project, id: 'p-2', hospitalName: 'IP discharge' }] : [project]),
      'POST /projects/quick': () => { made = true; return { projectId: 'p-2', departmentId: 'd-2', flowId: 'f-2', name: 'IP discharge', code: 'NEW-1234' } },
      'GET /projects/p-2/flows': () => [{ ...draft, id: 'f-2', projectId: 'p-2', menuName: 'IP discharge' }],
      'GET /flows/f-2/entries': () => [], 'GET /flows/f-2/clarifications': () => [], 'GET /flows/f-2/analyses': () => [],
    })
    app('/new')
    const user = userEvent.setup()
    expect(await screen.findByRole('button', { name: 'Create and add notes' })).toBeDisabled()
    await user.type(await screen.findByLabelText('Project name'), '  IP discharge ')
    await user.click(screen.getByRole('button', { name: 'Create and add notes' }))
    expect(await screen.findByRole('heading', { name: 'IP discharge' })).toBeInTheDocument()
    expect(server.calls.find(c => c.path === 'projects/quick')!.body).toEqual({ name: 'IP discharge', description: null, clientId: 'c-1' })   // the only client is used without asking
    expect(server.calls.find(c => c.path === 'projects/quick')!.headers.get('Idempotency-Key')).toBeTruthy()
    expect(server.count('POST', 'auth/refresh')).toBeGreaterThan(1)                      // asked again so the new project is in the permissions
    expect(await screen.findByRole('group', { name: 'Add notes' })).toBeInTheDocument()   // and the person is allowed to add to it at once
  })

  it('says why when the project cannot be created', async () => {
    signedIn(); mockApi({ ...standard, 'POST /projects/quick': () => problem(400, 'Invalid.', undefined, { errors: { 'Body.Name': ['Give the project a name.'] } }) }); app('/new')
    const user = userEvent.setup()
    await user.type(await screen.findByLabelText('Project name'), 'x')
    await user.click(screen.getByRole('button', { name: 'Create and add notes' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Give the project a name.')
  })
})

describe('the project page: notes', () => {
  it('shows the three steps and the three ways to add notes', async () => {
    signedIn(); mockApi(workspace({ 'GET /projects/p-1/flows': () => [draft], 'GET /flows/f-1/entries': () => [] })); app('/p/p-1')
    expect(await screen.findByRole('heading', { name: 'Kauvery Hospital' })).toBeInTheDocument()
    const steps = screen.getByRole('list', { name: 'Progress' })
    expect(within(steps).getAllByRole('link').map(l => l.textContent)).toEqual(['Notes addedNone yet', 'AnalysedNot yet', 'Questions answered-', 'Implementation documentNot yet'])
    expect(screen.getByRole('link', { name: /Add your first note/ })).toHaveAttribute('href', '#notes')          // the next step is always one tap away
    const group = await screen.findByRole('group', { name: 'Add notes' })
    for (const name of [/Record voice/, /Add photos/, /Type a note/]) expect(within(group).getByRole('button', { name })).toBeInTheDocument()
    expect(await screen.findByText('No notes yet')).toBeInTheDocument()
    expect(screen.getByText('Nothing here yet')).toBeInTheDocument()                    // no workflow until the notes are analysed
  })

  it('lists typed, spoken and photographed notes with what has happened to each', async () => {
    signedIn()
    mockApi(workspace({ 'GET /flows/f-1/entries': () => [entry, voice, photo], 'GET /entries/e-2/transcript': () => ({ entryId: 'e-2', status: 'Ready', maskedText: 'What the voice said.', userEditedText: null, error: null }) }))
    app('/p/p-1')
    expect(await screen.findByText('The user opens the receive menu.')).toBeInTheDocument()
    expect(await screen.findByText(/Voice note · 1:24/)).toBeInTheDocument()
    expect(await screen.findByText('What the voice said.')).toBeInTheDocument()
    expect(screen.getByText('Photo of notes')).toBeInTheDocument()
    expect(screen.getByText('Reading the photo…')).toBeInTheDocument()
    expect(screen.getAllByText('Saved').length).toBeGreaterThan(0)
  })

  it('keeps a typed note on the device first, shows it as waiting, and does not save an empty one', async () => {
    signedIn(); mockApi(workspace({ 'GET /flows/f-1/entries': () => [] })); app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Type a note/ }))
    expect(screen.getByRole('button', { name: 'Add note' })).toBeDisabled()
    await user.type(screen.getByRole('textbox', { name: 'Your note' }), 'The nurse picks the ward.')
    await user.click(screen.getByRole('button', { name: 'Add note' }))
    expect(await screen.findByText('Saved on this device, waiting to send')).toBeInTheDocument()
    expect(await screen.findByText('The nurse picks the ward.')).toBeInTheDocument()
    expect((await queuedEntries()).map(q => q.request.rawText)).toEqual(['The nurse picks the ward.'])
    expect(localStorage.getItem('fc.draft.f-1')).toBeNull()
  })

  it('keeps every typed note when several are added one after the other (each is its own note, not a repeat of the first)', async () => {
    signedIn(); mockApi(workspace({ 'GET /flows/f-1/entries': () => [] })); app('/p/p-1')
    const user = userEvent.setup()
    const texts = ['The nurse picks the ward.', 'The doctor signs the discharge.', 'The bill is printed.']
    for (const [i, text] of texts.entries()) {
      await user.click(await screen.findByRole('button', { name: /Type a note/ }))
      await user.type(screen.getByRole('textbox', { name: 'Your note' }), text)
      await user.click(screen.getByRole('button', { name: 'Add note' }))
      await waitFor(async () => expect((await queuedEntries()).length).toBe(i + 1))      // each one is kept as a note of its own
    }
    const queued = await queuedEntries()
    expect(queued.map(q => q.request.rawText)).toEqual(['The nurse picks the ward.', 'The doctor signs the discharge.', 'The bill is printed.'])
    expect(new Set(queued.map(q => q.id)).size).toBe(3)
  }, 20_000)                                                          // typing three notes takes a while on a busy machine

  it('does not reuse the id of a note the server already has when a kept draft carries it', async () => {
    signedIn(); localStorage.setItem('fc.draft.f-1', JSON.stringify({ id: 'e-1', text: 'A second thought' }))      // e-1 is the note already on the server
    mockApi(workspace({ 'GET /flows/f-1/entries': () => [entry] })); app('/p/p-1')
    const user = userEvent.setup()
    await screen.findByText('The user opens the receive menu.')
    await user.click(await screen.findByRole('button', { name: 'Add note' }))
    await waitFor(async () => expect((await queuedEntries()).length).toBe(1))
    expect((await queuedEntries())[0].id).not.toBe('e-1')
  })

  it('brings back a note that was half typed when the page is opened again', async () => {
    signedIn(); localStorage.setItem('fc.draft.f-1', JSON.stringify({ id: 'draft-1', text: 'Half a thought' }))
    mockApi(workspace({ 'GET /flows/f-1/entries': () => [] })); app('/p/p-1')
    expect(await screen.findByDisplayValue('Half a thought')).toBeInTheDocument()
  })

  it('sends photos one after the other and says each was sent', async () => {
    signedIn()
    const server = mockApi(workspace({ 'GET /flows/f-1/entries': () => [], 'POST /flows/f-1/images': () => ({ ...photo, transcriptState: 'Queued' }) }))
    app('/p/p-1')
    const user = userEvent.setup()
    await screen.findByRole('group', { name: 'Add notes' })
    await user.upload(screen.getByLabelText('Choose photos of notes'), [new File(['a'], 'one.jpg', { type: 'image/jpeg' }), new File(['b'], 'two.png', { type: 'image/png' })])
    await waitFor(() => expect(server.count('POST', 'flows/f-1/images')).toBe(2))
    expect(await screen.findByText('Photo 2 of 2 sent. It is being read.')).toBeInTheDocument()
  })

  it('names the photo that could not be sent and keeps going', async () => {
    signedIn()
    mockApi(workspace({ 'GET /flows/f-1/entries': () => [], 'POST /flows/f-1/images': () => problem(422, 'The request cannot be completed.', 'Upload a JPEG, PNG or WebP photo.') }))
    app('/p/p-1')
    await screen.findByRole('group', { name: 'Add notes' })
    await userEvent.setup().upload(screen.getByLabelText('Choose photos of notes'), new File(['a'], 'scan.jpg', { type: 'image/jpeg' }))
    expect(await screen.findByText(/scan\.jpg: Upload a JPEG, PNG or WebP photo\./)).toBeInTheDocument()
  })

  it('opens the recorder, and says plainly why a browser that cannot record cannot', async () => {
    signedIn(); mockApi(workspace({ 'GET /flows/f-1/entries': () => [] })); app('/p/p-1')
    await userEvent.setup().click(await screen.findByRole('button', { name: /Record voice/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Record a voice note' })
    expect(within(dialog).getByRole('alert')).toHaveTextContent('This browser cannot record sound')
    expect(within(dialog).queryByRole('button', { name: 'Start recording' })).not.toBeInTheDocument()
  })

  it('asks for a secure address when the page is not https', async () => {
    signedIn(); mockApi(workspace({ 'GET /flows/f-1/entries': () => [] }))
    Object.defineProperty(window, 'isSecureContext', { value: false, configurable: true })
    try {
      app('/p/p-1')
      await userEvent.setup().click(await screen.findByRole('button', { name: /Record voice/ }))
      expect(await screen.findByRole('alert')).toHaveTextContent('Recording needs a secure address')
    } finally { Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true }) }
  })

  it('lets the writer correct a typed note and sends the version it was based on', async () => {
    signedIn()
    const server = mockApi(workspace({ 'PUT /entries/e-1': () => ({ ...entry, rawText: 'Fixed.' }) }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Typed note, note E1/ }))      // a note is one line until it is opened
    await user.click(await screen.findByRole('button', { name: 'Edit text' }))
    const box = screen.getByRole('textbox', { name: 'Note text' })
    await user.clear(box); await user.type(box, 'Fixed.')
    await user.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(server.count('PUT', 'entries/e-1')).toBe(1))
    const call = server.calls.find(c => c.method === 'PUT')!
    expect(call.body).toMatchObject({ rawText: 'Fixed.' })
    expect(call.headers.get('If-Match')).toBe('"v1"')
  })

  it('shows each note as one short line, opens it on a click, and keeps a note that failed open', async () => {
    signedIn()
    const failed = { ...photo, id: 'e-4', refNo: 4, transcriptState: 'Failed' }
    mockApi(workspace({ 'GET /flows/f-1/entries': () => [entry, voice, failed], 'GET /entries/e-2/transcript': () => ({ entryId: 'e-2', status: 'Ready', maskedText: 'What the voice said.', userEditedText: null, error: null }), 'GET /entries/e-4/transcript': () => ({ entryId: 'e-4', status: 'Failed', error: 'No readable text was found in the photo.' }) }))
    app('/p/p-1')
    const user = userEvent.setup()
    const typed = await screen.findByRole('button', { name: /Typed note, note E1/ })
    expect(typed).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(1)                 // only the note that needs attention is open
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
    await user.click(typed)
    expect(typed).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(2)
    await user.click(typed)
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(1)
  })

  it('removes a note only after asking', async () => {
    signedIn(); const server = mockApi(workspace({ 'DELETE /entries/e-1': () => undefined }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Typed note, note E1/ }))
    await user.click(await screen.findByRole('button', { name: 'Remove' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(server.count('DELETE', 'entries/e-1')).toBe(0)
    await user.click(screen.getByRole('button', { name: 'Remove' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(server.count('DELETE', 'entries/e-1')).toBe(1))
  })

  it('shows why a photo could not be read and offers to try again', async () => {
    signedIn()
    const failed = { ...photo, transcriptState: 'Failed' }
    const server = mockApi(workspace({ 'GET /flows/f-1/entries': () => [failed], 'GET /entries/e-3/transcript': () => ({ entryId: 'e-3', status: 'Failed', error: 'No readable text was found in the photo.' }), 'POST /entries/e-3/transcribe': () => ({ status: 'Queued' }) }))
    app('/p/p-1')
    expect(await screen.findByText('No readable text was found in the photo.')).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(server.count('POST', 'entries/e-3/transcribe')).toBe(1))
  })

  it('gives a reviewer the notes to read but no way to add', async () => {
    signedIn(); mockApi(workspace({ ...asReviewer })); app('/p/p-1')
    expect(await screen.findByText('You can read these notes but not add to them.')).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: 'Add notes' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument()
  })

  it('will not analyse while a recording or photo is still being turned into text, and says so', async () => {
    signedIn(); mockApi(workspace({ 'GET /flows/f-1/entries': () => [entry, photo], 'GET /projects/p-1/flows': () => [draft] })); app('/p/p-1')
    expect(await screen.findByText(/1 recording or photo is still being turned into text/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Run analysis' })).toBeDisabled()
  })

  it('tells the user when the project does not exist or is not theirs', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/p-9/flows': () => problem(403, 'Forbidden.', 'You are not a member of this project.') }); app('/p/p-9')
    expect(await screen.findByText('You are not a member of this project.')).toBeInTheDocument()
  })
})

describe('the project page: the workflow and the implementation document', () => {
  it('shows what was found, the diagram, and offers to make the document', async () => {
    signedIn(); const server = mockApi(workspace()); app('/p/p-1')
    const picture = await screen.findByRole('img', { name: 'Workflow diagram' })          // drawn by the server, the same picture as in the PDF
    expect(picture).toHaveAttribute('src', expect.stringMatching(/^blob:/))
    const asked = server.calls.find(c => c.path === 'flows/f-1/diagram')!
    expect(asked.method).toBe('GET')
    const stats = screen.getByLabelText('What was found')
    for (const [n, l] of [['2', 'steps'], ['1', 'rules'], ['1', 'requirements'], ['3', 'open questions']]) expect(within(stats).getByText(l).previousElementSibling).toHaveTextContent(n)
    expect(screen.getByRole('button', { name: 'Create the implementation document' })).toBeEnabled()
  })

  it('makes the document in the background, then offers the PDF', async () => {
    signedIn()
    let made = false
    const server = mockApi(workspace({
      'GET /flows/f-1/documents': () => (made ? [guide()] : []),
      'POST /flows/f-1/guide': () => { made = true; return { jobId: 'j-1', kind: 'GuideDocument', state: 'Queued', percent: null, message: null, resultId: null, createdAt: new Date().toISOString() } },
      'GET /jobs/j-1': () => ({ jobId: 'j-1', kind: 'GuideDocument', state: 'Succeeded', percent: 100, message: null, resultId: 'd-1', createdAt: new Date().toISOString() }),
      'GET /documents/d-1/versions/1/download': () => new Response('%PDF-1.4 test', { status: 200, headers: { 'Content-Type': 'application/pdf', 'Content-Disposition': 'attachment; filename="IMPL-KMC-001.pdf"' } }),
    }))
    const saved = vi.spyOn(HTMLAnchorElement.prototype, 'click')
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    await confirmPdf(user)
    expect(await screen.findByRole('button', { name: 'Open the PDF' }, { timeout: 8000 })).toBeInTheDocument()
    expect(screen.getByText(/IMPL-KMC-001 · version 1 · made by Asha Lead/)).toBeInTheDocument()
    expect(server.calls.find(c => c.path === 'flows/f-1/guide')!.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)
    // a copy of the new PDF is saved on this device by itself
    expect(await screen.findByText('A copy of the PDF was saved in the Downloads of this device.', {}, { timeout: 8000 })).toBeInTheDocument()
    expect(server.count('GET', 'documents/d-1/versions/1/download')).toBe(1)
    expect(saved).toHaveBeenCalled()
    saved.mockRestore()
  }, 15_000)

  it('does not save a copy on the device when the person turned it off, nor for a document that already existed when the page opened', async () => {
    signedIn()
    const server = mockApi(workspace({ 'GET /flows/f-1/documents': () => [guide(1, 1)], 'GET /documents/d-1/versions/1/download': () => new Response('%PDF-1.4', { status: 200 }) }))
    app('/p/p-1')
    expect(await screen.findByRole('button', { name: 'Open the PDF' })).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /Keep a copy on this computer/ })).toBeChecked()
    await new Promise(r => setTimeout(r, 300))
    expect(server.count('GET', 'documents/d-1/versions/1/download')).toBe(0)
  })

  it('keeps no copy when the person switched the setting off', async () => {
    signedIn()
    localStorage.setItem('fc.saveCopy', 'off')
    let made = false
    const server = mockApi(workspace({
      'GET /flows/f-1/documents': () => (made ? [guide()] : []),
      'POST /flows/f-1/guide': () => { made = true; return { jobId: 'j-1', kind: 'GuideDocument', state: 'Queued', percent: null, message: null, resultId: null, createdAt: new Date().toISOString() } },
      'GET /jobs/j-1': () => ({ jobId: 'j-1', kind: 'GuideDocument', state: 'Succeeded', percent: 100, message: null, resultId: 'd-1', createdAt: new Date().toISOString() }),
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    await confirmPdf(user)
    expect(await screen.findByRole('button', { name: 'Open the PDF' }, { timeout: 8000 })).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /Keep a copy on this computer/ })).not.toBeChecked()
    await new Promise(r => setTimeout(r, 300))
    expect(server.count('GET', 'documents/d-1/versions/1/download')).toBe(0)
    localStorage.removeItem('fc.saveCopy')
  }, 15_000)

  it('shows why the document could not be made', async () => {
    signedIn()
    mockApi(workspace({
      'POST /flows/f-1/guide': () => ({ jobId: 'j-2', kind: 'GuideDocument', state: 'Queued', percent: null, message: null, resultId: null, createdAt: new Date().toISOString() }),
      'GET /jobs/j-2': () => ({ jobId: 'j-2', kind: 'GuideDocument', state: 'Failed', percent: null, message: 'The free AI limit has been reached for now.', resultId: null, createdAt: new Date().toISOString() }),
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
    await confirmPdf(user)
    expect(await screen.findByText('The free AI limit has been reached for now.', {}, { timeout: 8000 })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create the implementation document' })).toBeEnabled()          // and it can be tried again
  }, 15_000)

  it('warns when the workflow changed after the document was made, and opens the PDF in a new tab', async () => {
    signedIn()
    const tab = { location: { href: '' }, close: vi.fn() }
    vi.spyOn(window, 'open').mockReturnValue(tab as unknown as Window)
    const server = mockApi(workspace({ 'GET /projects/p-1/flows': () => [{ ...flow, currentVersionNo: 3 }], 'GET /flows/f-1': () => ({ ...flow, currentVersionNo: 3 }), 'GET /flows/f-1/documents': () => [guide(2, 1)], 'GET /documents/d-1/versions/2/download': () => ({}) }))
    app('/p/p-1')
    expect(await screen.findByText(/The workflow changed after this document was made/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Update the document' })).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Open the PDF' }))
    await waitFor(() => expect(server.count('GET', 'documents/d-1/versions/2/download')).toBe(1))
    await waitFor(() => expect(tab.location.href).toMatch(/^blob:/))                 // the PDF opens in the tab that was opened for it
    expect(tab.close).not.toHaveBeenCalled()
  })

  it('shows someone who cannot capture the result but not the buttons that change it', async () => {
    signedIn(); mockApi(workspace({ ...asReviewer })); app('/p/p-1')
    expect(await screen.findByText('The document has not been made yet.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create the implementation document' })).not.toBeInTheDocument()
  })
})

describe('documents', () => {
  const doc = (over: object) => ({ id: 'd-1', docNumber: 'IMPL-KMC-001', title: 'IP Case Sheet Receive', kind: 'Guide', projectId: 'p-1', projectName: 'Kauvery Hospital', flowId: 'f-1', versionNo: 2,
    createdAt: new Date().toISOString(), createdByName: 'Asha Lead', formats: ['pdf'], hasDiagram: true, ...over })

  it('lists everybody\'s documents with who made them, and filters as you type', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /library': () => [doc({}), doc({ id: 'd-2', title: 'Discharge', projectName: 'Other hospital', docNumber: 'IMPL-OTH-001', createdByName: 'Ravi', versionNo: 1, kind: 'Requirements', formats: ['pdf', 'docx'] })] })
    app('/documents')
    expect(await screen.findByRole('heading', { name: 'IP Case Sheet Receive' })).toBeInTheDocument()
    expect(screen.getByText(/Kauvery Hospital · IMPL-KMC-001 · by Asha Lead/)).toBeInTheDocument()
    expect(screen.getByText('Version 2')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /Word/ })).toHaveLength(1)                 // only the one that has a Word file
    await userEvent.setup().type(screen.getByRole('searchbox'), 'ravi')
    expect(screen.queryByRole('heading', { name: 'IP Case Sheet Receive' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Discharge' })).toBeInTheDocument()
    await userEvent.setup().clear(screen.getByRole('searchbox')); await userEvent.setup().type(screen.getByRole('searchbox'), 'zzz')
    expect(await screen.findByText(/Nothing matches/)).toBeInTheDocument()
  })

  it('says so when nothing has been made yet', async () => {
    signedIn(); mockApi(standard); app('/documents')
    expect(await screen.findByText('No documents yet')).toBeInTheDocument()
  })

  it('shows the diagram of a document in a window, and downloads the PDF', async () => {
    signedIn()
    const server = mockApi({ ...standard, 'GET /library': () => [doc({})], 'GET /library/d-1/diagram': () => ({}), 'GET /documents/d-1/versions/2/download': () => ({}) })
    app('/documents')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Diagram/ }))
    const dialog = await screen.findByRole('dialog', { name: 'IP Case Sheet Receive' })
    expect(await within(dialog).findByRole('img', { name: /Workflow diagram of IP Case Sheet Receive/ })).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Close' }))
    await user.click(screen.getByRole('button', { name: /^PDF$/ }))
    await waitFor(() => expect(server.count('GET', 'documents/d-1/versions/2/download')).toBe(1))
  })
})

describe('the admin pages', () => {
  const adminRoutes = (over: Record<string, unknown> = {}) => ({ ...standard, ...asAdmin, 'GET /projects': () => [project], ...over })
  const account = (over: object) => ({ id: 'u-2', email: 'ravi@demo.local', displayName: 'Ravi Shankar', isActive: true, roles: ['ImplementationLead'], preferredLanguage: null, ...over })

  it('shows the totals, and tells the admin when the AI still needs setting up', async () => {
    signedIn()
    mockApi(adminRoutes({ 'GET /admin/overview': () => ({ users: 5, activeUsers: 4, projects: 3, flows: 3, notes: 12, documents: 2, aiProvider: 'no-key', aiReady: false, awaitingVerification: 2, clients: 6 }) }))
    app('/admin')
    expect(await screen.findByText('The AI is not set up yet')).toBeInTheDocument()
    const totals = screen.getByLabelText('Totals')
    expect(within(totals).getByText('active users').previousElementSibling).toHaveTextContent('4 / 5')
    expect(within(totals).getByText('clients').previousElementSibling).toHaveTextContent('6')
    expect(within(totals).getByText('final, waiting').previousElementSibling).toHaveTextContent('2')
    expect(screen.getByRole('link', { name: /Set it up/ })).toHaveAttribute('href', '/admin/ai')
    expect(screen.getByRole('link', { name: /2 final documents waiting for you/ })).toHaveAttribute('href', '/documents')     // what needs the administrator first
    expect(screen.queryByRole('link', { name: 'Open Kauvery Hospital' })).not.toBeInTheDocument()     // the administrator does not open the users' projects
  })

  it('says the AI is ready and which one is used', async () => {
    signedIn(); mockApi(adminRoutes({ 'GET /admin/overview': () => ({ users: 1, activeUsers: 1, projects: 0, flows: 0, notes: 0, documents: 0, aiProvider: 'gemini', aiReady: true }) })); app('/admin')
    expect(await screen.findByText('The AI is ready')).toBeInTheDocument()
    expect(screen.getByText(/Using: Free Gemini AI/)).toBeInTheDocument()
  })

  it('lists the users, with the admin marked and unable to stop themselves', async () => {
    signedIn()
    mockApi(adminRoutes({ 'GET /users': () => [account({ id: 'u-1', displayName: 'Dev Admin', email: 'admin@flowcapture.local', roles: ['Admin'] }), account({}), account({ id: 'u-3', displayName: 'Stopped Person', isActive: false })] }))
    app('/admin/users')
    expect(await screen.findByText('Dev Admin (you)')).toBeInTheDocument()
    const me = screen.getByText('Dev Admin (you)').closest('li')!
    expect(within(me).getByText('Administrator')).toBeInTheDocument()
    expect(within(me).queryByRole('button', { name: /Stop access/ })).not.toBeInTheDocument()
    expect(within(me).getByRole('button', { name: /New password/ })).toBeInTheDocument()
    expect(within(screen.getByText('Stopped Person').closest('li')!).getByText('Stopped')).toBeInTheDocument()
  })

  it('adds a user with a ready password and shows the sign-in details once', async () => {
    signedIn()
    const server = mockApi(adminRoutes({ 'GET /users': () => [], 'POST /users': () => account({}) }))
    app('/admin/users')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Add user/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a user' })
    const add = within(dialog).getByRole('button', { name: 'Add user' })
    expect(add).toBeDisabled()
    await user.type(within(dialog).getByLabelText('Full name'), 'Ravi Shankar')
    await user.type(within(dialog).getByLabelText(/Email/), 'ravi@demo.local')
    const password = (within(dialog).getByLabelText('Password') as HTMLInputElement).value
    expect(password).toMatch(/^(?=.*[A-Za-z])(?=.*\d).{10,}$/)
    await user.click(add)
    const sent = (await waitFor(() => { const c = server.calls.find(x => x.method === 'POST' && x.path === 'users'); expect(c).toBeTruthy(); return c! })).body
    expect(sent).toEqual({ email: 'ravi@demo.local', displayName: 'Ravi Shankar', password, roles: ['ImplementationLead'] })
    const details = await screen.findByRole('dialog', { name: 'User added' })
    expect(within(details).getByLabelText('Sign-in details')).toHaveTextContent(`Email: ravi@demo.local`)
    expect(within(details).getByLabelText('Sign-in details')).toHaveTextContent(`Password: ${password}`)
  })

  it('can make a user an administrator when asked, and refuses nothing silently', async () => {
    signedIn()
    const server = mockApi(adminRoutes({ 'GET /users': () => [account({})], 'PUT /users/u-2': () => account({ roles: ['Admin'] }) }))
    app('/admin/users')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Make administrator' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT')?.body).toMatchObject({ roles: ['Admin'], isActive: true }))
  })

  it('lets the admin change the name and sign-in email of an administrator and of a user', async () => {
    signedIn()
    const server = mockApi(adminRoutes({
      'GET /users': () => [account({ id: 'u-1', displayName: 'Dev Admin', email: 'admin@flowcapture.local', roles: ['Admin'] }), account({})],
      'PUT /users/u-1': () => account({ id: 'u-1', roles: ['Admin'] }), 'PUT /users/u-2': () => account({}),
    }))
    app('/admin/users')
    const user = userEvent.setup()
    // a user
    await user.click(await screen.findByRole('button', { name: /Change name or email of Ravi Shankar/ }))
    let dialog = await screen.findByRole('dialog')
    const save = within(dialog).getByRole('button', { name: 'Save' })
    expect(save).toBeDisabled()                       // nothing changed yet
    await user.clear(within(dialog).getByLabelText('Full name')); await user.type(within(dialog).getByLabelText('Full name'), 'Ravi Kumar')
    await user.clear(within(dialog).getByLabelText(/Email/)); await user.type(within(dialog).getByLabelText(/Email/), 'ravi.kumar@demo.local')
    await user.click(save)
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'users/u-2')?.body).toMatchObject({ displayName: 'Ravi Kumar', email: 'ravi.kumar@demo.local', isActive: true }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    // an administrator, who keeps the role
    await user.click(screen.getByRole('button', { name: /Change name or email of Dev Admin/ }))
    dialog = await screen.findByRole('dialog')
    await user.clear(within(dialog).getByLabelText('Full name')); await user.type(within(dialog).getByLabelText('Full name'), 'Head Admin')
    await user.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'users/u-1')?.body).toMatchObject({ displayName: 'Head Admin', roles: ['Admin'], isActive: true }))
  })

  it('stops someone\'s access only after asking, and gives a new password once', async () => {
    signedIn()
    const server = mockApi(adminRoutes({ 'GET /users': () => [account({})], 'PUT /users/u-2': () => account({ isActive: false }), 'POST /users/u-2/reset-password': () => ({ temporaryPassword: 'Temp1234abcd' }) }))
    app('/admin/users')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Stop access/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }))
    expect(server.count('PUT', 'users/u-2')).toBe(0)
    await user.click(screen.getByRole('button', { name: /Stop access/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Stop access' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT')?.body).toMatchObject({ isActive: false }))

    await user.click(await screen.findByRole('button', { name: /New password/ }))
    expect(await screen.findByLabelText('Sign-in details')).toHaveTextContent('Password: Temp1234abcd')
  })

  describe('AI setup', () => {
    const ai = (over: object = {}) => ({ provider: 'Gemini', providers: ['Gemini', 'Anthropic', 'Demo'], hasKey: false, keyHint: null, geminiModel: '', keyFromDashboard: false, ...over })

    it('walks the admin through getting a free key and saves it, then tests it straight away', async () => {
      signedIn()
      let saved = false
      const server = mockApi(adminRoutes({
        'GET /admin/ai': () => ai(saved ? { hasKey: true, keyHint: '...1234', keyFromDashboard: true } : {}),
        'PUT /admin/ai': () => { saved = true; return ai({ hasKey: true, keyHint: '...1234', keyFromDashboard: true }) },
        'POST /admin/ai/test': () => ({ ok: true, message: 'It works. The free Gemini AI answered using gemini-9-flash.', model: 'gemini-9-flash', models: [{ id: 'gemini-9-flash', name: 'G9' }] }),
      }))
      app('/admin/ai')
      expect(await screen.findByRole('link', { name: /Google AI Studio/ })).toHaveAttribute('href', 'https://aistudio.google.com/apikey')
      expect(screen.getByRole('link', { name: /Google AI Studio/ })).toHaveAttribute('rel', 'noopener noreferrer')
      const user = userEvent.setup()
      const key = screen.getByLabelText('Gemini API key')
      expect(key).toHaveAttribute('type', 'password')
      expect(screen.getByText('Nothing is saved yet.')).toBeInTheDocument()
      await user.type(key, 'AIza-secret-1234')
      await user.click(screen.getAllByRole('button', { name: 'Save' })[0])
      expect(await screen.findByText('It works. The free Gemini AI answered using gemini-9-flash.')).toBeInTheDocument()
      expect(server.calls.find(c => c.method === 'PUT')!.body).toEqual({ geminiApiKey: 'AIza-secret-1234' })
      expect(await screen.findByText(/A key is saved \(ends \.\.\.1234\)/)).toBeInTheDocument()
      expect((screen.getByLabelText('Gemini API key') as HTMLInputElement).value).toBe('')       // the key is never shown again
      expect(screen.getByRole('option', { name: 'gemini-9-flash' })).toBeInTheDocument()          // and the key's own models can be chosen
    })

    it('lets the admin add a free Groq key for voice notes, tests it, and shows what is left of the free allowance', async () => {
      signedIn()
      let state: object = { hasKey: true, keyHint: '...1234', keyFromDashboard: true }
      const server = mockApi(adminRoutes({
        'GET /admin/ai': () => ai(state),
        'PUT /admin/ai': () => { state = { ...state, groqHasKey: true, groqKeyHint: '...abcd', voiceProvider: 'Groq', groqSttModel: 'whisper-large-v3-turbo', limits: [{ provider: 'Groq', model: 'whisper-large-v3-turbo', what: 'requests per day', limit: 2000, remaining: 1990, renews: '43s', at: new Date().toISOString() }] }; return ai(state) },
        'POST /admin/ai/test-groq': () => ({ ok: true, message: 'The Groq key works (20 models available). Voice notes are turned into text with whisper-large-v3-turbo.', model: 'whisper-large-v3-turbo', models: [] }),
      }))
      app('/admin/ai')
      expect(await screen.findByRole('link', { name: /Groq Console/ })).toHaveAttribute('href', 'https://console.groq.com/keys')
      const user = userEvent.setup()
      const groq = screen.getByLabelText('Groq API key')
      expect(groq).toHaveAttribute('type', 'password')
      await user.type(groq, 'gsk_secret_abcd')
      await user.click(screen.getByRole('radio', { name: /^Groq \(recommended\)/ }))
      await user.click(screen.getAllByRole('button', { name: 'Save' })[0])
      expect(await screen.findByText(/The Groq key works \(20 models available\)/)).toBeInTheDocument()
      expect(server.calls.find(c => c.method === 'PUT')!.body).toEqual({ groqApiKey: 'gsk_secret_abcd', voiceProvider: 'Groq' })
      expect((screen.getByLabelText('Groq API key') as HTMLInputElement).value).toBe('')                // the key is never shown again
      expect(await screen.findByText(/A key is saved \(ends \.\.\.abcd\)/)).toBeInTheDocument()
      const table = await screen.findByRole('table', { name: 'What is left of the free allowance' })
      expect(within(table).getByRole('row', { name: /whisper-large-v3-turbo requests per day 1990 of 2000 43s/ })).toBeInTheDocument()
    })
    it('shows which models answered just now and how fast, so a busy newest model is no mystery', async () => {
      signedIn()
      mockApi(adminRoutes({
        'GET /admin/ai': () => ai({ hasKey: true, keyHint: '...1234', keyFromDashboard: true }),
        'POST /admin/ai/test': () => ({
          ok: true, message: 'It works. 1 of 2 models answered; the fastest was gemini-2.5-flash (2.1 s).', model: 'gemini-2.5-flash', models: [],
          probes: [{ model: 'gemini-3.8-flash', ok: false, milliseconds: 30000, message: 'Busy or not answering.' }, { model: 'gemini-2.5-flash', ok: true, milliseconds: 2100, message: 'Answered.' }],
        }),
      }))
      app('/admin/ai')
      await userEvent.setup().click(await screen.findByRole('button', { name: 'Test the key' }))
      const table = await screen.findByRole('table', { name: 'Which models answered just now' })
      expect(within(table).getByRole('row', { name: /gemini-3\.8-flash Busy or not answering\. 30\.0 s/ })).toBeInTheDocument()
      expect(within(table).getByRole('row', { name: /gemini-2\.5-flash Yes 2\.1 s/ })).toBeInTheDocument()
    })

    it('shows why a key does not work', async () => {
      signedIn()
      mockApi(adminRoutes({ 'GET /admin/ai': () => ai({ hasKey: true, keyHint: '...9999', keyFromDashboard: true }), 'POST /admin/ai/test': () => ({ ok: false, message: 'Gemini rejected the key.', model: null, models: [] }) }))
      app('/admin/ai')
      await userEvent.setup().click(await screen.findByRole('button', { name: 'Test the key' }))
      expect(await screen.findByText('Gemini rejected the key.')).toBeInTheDocument()
    })

    it('switches the AI service', async () => {
      signedIn()
      const server = mockApi(adminRoutes({ 'GET /admin/ai': () => ai({ hasKey: true, keyHint: '...1234', keyFromDashboard: true }), 'PUT /admin/ai': () => ai({ provider: 'Demo' }) }))
      app('/admin/ai')
      const user = userEvent.setup()
      await user.click(await screen.findByRole('radio', { name: /Demo mode/ }))
      await user.click(screen.getAllByRole('button', { name: 'Save' })[0])
      await waitFor(() => expect(server.calls.find(c => c.method === 'PUT')!.body).toEqual({ provider: 'Demo' }))
    })

    it('removes a saved key', async () => {
      signedIn()
      const server = mockApi(adminRoutes({ 'GET /admin/ai': () => ai({ hasKey: true, keyHint: '...1234', keyFromDashboard: true }), 'PUT /admin/ai': () => ai() }))
      app('/admin/ai')
      await userEvent.setup().click(await screen.findByRole('button', { name: 'Remove the key' }))
      await waitFor(() => expect(server.calls.find(c => c.method === 'PUT')!.body).toEqual({ geminiApiKey: '' }))
      expect(await screen.findByText('Nothing is saved yet.')).toBeInTheDocument()
    })

    it('has the privacy and speed switches: photos and recordings on, AI wording off, each saved on its own', async () => {
      signedIn()
      let state = { allowMedia: true, aiWording: false }
      const server = mockApi(adminRoutes({
        'GET /admin/ai': () => ai(state),
        'PUT /admin/ai': ({ body }: { body: unknown }) => { state = { ...state, ...(body as object) }; return ai(state) },
      }))
      app('/admin/ai')
      const user = userEvent.setup()
      const media = await screen.findByRole('checkbox', { name: /Allow photos and voice recordings/ })
      const wording = screen.getByRole('checkbox', { name: /rewrite the document wording/ })
      expect(media).toBeChecked()
      expect(wording).not.toBeChecked()

      await user.click(media)
      await waitFor(() => expect(server.calls.filter(c => c.method === 'PUT').at(-1)!.body).toEqual({ allowMedia: false }))
      await waitFor(() => expect(screen.getByRole('checkbox', { name: /Allow photos and voice recordings/ })).not.toBeChecked())
      await user.click(screen.getByRole('checkbox', { name: /rewrite the document wording/ }))
      await waitFor(() => expect(server.calls.filter(c => c.method === 'PUT').at(-1)!.body).toEqual({ aiWording: true }))
      await waitFor(() => expect(screen.getByRole('checkbox', { name: /rewrite the document wording/ })).toBeChecked())
    })

    it('treats a server that does not report the switches as media allowed and wording off', async () => {
      signedIn(); mockApi(adminRoutes({ 'GET /admin/ai': () => ai() })); app('/admin/ai')
      expect(await screen.findByRole('checkbox', { name: /Allow photos and voice recordings/ })).toBeChecked()
      expect(screen.getByRole('checkbox', { name: /rewrite the document wording/ })).not.toBeChecked()
    })

    it('warns about what the free tier means for privacy', async () => {
      signedIn(); mockApi(adminRoutes({ 'GET /admin/ai': () => ai() })); app('/admin/ai')
      expect(await screen.findByText(/About privacy/)).toBeInTheDocument()
      expect(screen.getByText(/cannot hide them inside a photo or a recording/)).toBeInTheDocument()
    })
  })
})

describe('the account menu', () => {
  it('offers the three themes and signs out cleanly when nothing is waiting', async () => {
    signedIn(); mockApi({ ...standard, 'POST /auth/logout': () => undefined }); app('/')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Account menu for Asha Lead/ }))
    expect(screen.getByText('User')).toBeInTheDocument()
    const themes = within(screen.getByRole('group', { name: 'Theme' }))
    await user.click(themes.getByRole('button', { name: 'Dark' }))
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(themes.getByRole('button', { name: 'Dark' })).toHaveAttribute('aria-pressed', 'true')
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))
    expect(await screen.findByRole('heading', { name: 'Welcome to bbflowbuild' })).toBeInTheDocument()
    expect(localStorage.getItem('fc.refresh')).toBeNull()
  })

  it('warns before signing out with notes that have not been sent', async () => {
    signedIn(); mockApi({ ...standard, 'POST /auth/logout': () => undefined })
    await enqueueEntry('f-1', { id: 'n-1', kind: 'Text', capturedAt: new Date().toISOString(), language: 'en-IN', rawText: 'not sent yet', notes: null, intervieweeName: null, intervieweeRole: null })
    app('/')
    const user = userEvent.setup()
    expect(await screen.findByText('Sending 1 note…')).toBeInTheDocument()                    // the badge appears only when something is waiting
    await user.click(await screen.findByRole('button', { name: /Account menu/ }))
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))
    const dialog = await screen.findByRole('dialog', { name: 'Sign out and lose work?' })
    expect(dialog).toHaveTextContent('1 note(s) on this device have not reached the server yet')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(localStorage.getItem('fc.refresh')).not.toBeNull()
    expect(await queuedEntries()).toHaveLength(1)
    await user.click(screen.getByRole('button', { name: /Account menu/ }))
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Sign out and delete' }))
    expect(await screen.findByRole('heading', { name: 'Welcome to bbflowbuild' })).toBeInTheDocument()
    expect(await queuedEntries()).toHaveLength(0)
  })
})

describe('the next step and the progress beside the work', () => {
  const base = { ...flow, status: 'Draft' as const, currentVersionNo: 0, openQuestionCount: 0, entryCount: 0 }

  it('always names the one thing to do next, from where the project is', () => {
    expect(nextStep(base, false, true)).toMatchObject({ label: 'Add your first note', href: '#notes' })
    expect(nextStep({ ...base, entryCount: 2 }, false, true)).toMatchObject({ label: 'Run the analysis', href: '#analyse' })
    expect(nextStep({ ...base, entryCount: 2, currentVersionNo: 1, openQuestionCount: 3 }, false, true)).toMatchObject({ label: 'Answer 3 questions', href: '#analyse' })
    expect(nextStep({ ...base, entryCount: 2, currentVersionNo: 1, openQuestionCount: 1 }, false, true).label).toBe('Answer 1 question')
    expect(nextStep({ ...base, entryCount: 2, currentVersionNo: 1 }, false, true)).toMatchObject({ label: 'Create the implementation document', href: '#result' })
    expect(nextStep({ ...base, entryCount: 2, currentVersionNo: 1, openQuestionCount: 3 }, true, true)).toMatchObject({ label: 'Open the implementation document' })   // open questions do not block a finished document
    expect(nextStep(base, false, false).label).toBe('Read the notes')                                                              // someone who cannot change anything is not told to
  })

  it('ticks off each stage as it is done and counts them', async () => {
    signedIn(); mockApi(workspace({ 'GET /flows/f-1': () => ({ ...flow, openQuestionCount: 0 }), 'GET /projects/p-1/flows': () => [{ ...flow, openQuestionCount: 0 }], 'GET /flows/f-1/documents': () => [guide()] }))
    app('/p/p-1')
    const list = await screen.findByRole('list', { name: 'Progress' })
    await waitFor(() => expect(screen.getByText('4 of 4')).toBeInTheDocument())
    expect(within(list).getAllByRole('listitem').every(li => li.classList.contains('done'))).toBe(true)
    expect(screen.getByRole('link', { name: /Open the implementation document/ })).toBeInTheDocument()
  })

  it('shows how many tokens the AI has used in this project, linking to the usage page', async () => {
    signedIn(); mockApi(workspace()); app('/p/p-1')
    const link = await screen.findByRole('link', { name: /AI used 24200 tokens in this project this month/ })
    expect(link).toHaveAttribute('href', '/usage')
    expect(link).toHaveTextContent('24.2K tokens')
  })
})

describe('the AI usage page', () => {
  it('shows what the AI used this month: the total, what it was used for, the projects and the latest calls', async () => {
    signedIn(); mockApi(standard); app('/usage')
    expect(await screen.findByRole('heading', { name: 'My AI usage' })).toBeInTheDocument()
    expect(await screen.findByLabelText('Tokens used this month')).toHaveTextContent('24.2K')
    expect(screen.getByLabelText('Tokens used this month')).toHaveTextContent('+102% on the month before')
    const totals = screen.getByLabelText('Totals')
    expect(within(totals).getByText('AI calls').previousElementSibling).toHaveTextContent('4')
    expect(within(totals).getByText('tokens sent to the AI').previousElementSibling).toHaveTextContent('18K')
    expect(within(totals).getByText('tokens the AI wrote').previousElementSibling).toHaveTextContent('6,200')
    expect(within(totals).getByText('tokens sent to the AI').previousElementSibling).toHaveAttribute('title', '18,000 tokens')       // the exact number is one hover away
    expect(within(totals).getByText('tokens today').previousElementSibling).toHaveTextContent('2,700')

    const kinds = screen.getByLabelText('What the AI was used for')
    expect(within(kinds).getByText('Analysing notes')).toBeInTheDocument()
    expect(within(kinds).getByText('Voice notes')).toBeInTheDocument()
    expect(within(kinds).getByText('Photos of notes')).toBeInTheDocument()
    expect(within(kinds).getByText(/2 calls · 83%/)).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Projects' })).getByText('Kauvery Hospital')).toBeInTheDocument()
    expect(screen.queryByRole('region', { name: 'People' })).not.toBeInTheDocument()          // a user does not see the others

    const latest = within(screen.getByLabelText('Recent AI calls'))
    expect(latest.getByText('9,552')).toBeInTheDocument()
    expect(latest.getByText('5,075')).toBeInTheDocument()
    expect(latest.getByText('41.0 s')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: /Tokens used each day\. The busiest day was/ })).toBeInTheDocument()
  })

  it('shows an administrator everyone\'s usage, by person, with the title for the whole system', async () => {
    signedIn()
    mockApi({ ...standard, ...asAdmin, 'GET /ai-usage': () => usage({ allUsers: true, byUser: [{ id: 'u-2', name: 'Ravi Shankar', calls: 3, totalTokens: 15000 }, { id: 'u-1', name: 'Asha Kumar', calls: 1, totalTokens: 9200 }] }) })
    app('/usage')
    expect(await screen.findByRole('heading', { name: 'AI usage' })).toBeInTheDocument()
    const people = await screen.findByRole('region', { name: 'People' })
    expect(within(people).getAllByRole('listitem').map(li => li.textContent)).toEqual(expect.arrayContaining([expect.stringContaining('Ravi Shankar'), expect.stringContaining('Asha Kumar')]))
    expect(within(screen.getByLabelText('Recent AI calls')).getByRole('columnheader', { name: 'Who' })).toBeInTheDocument()
  })

  it('goes back and forward through the months, asking the server for each, and cannot go past this month', async () => {
    signedIn()
    const server = mockApi({ ...standard, 'GET /ai-usage': ({ url }) => usage(url.searchParams.get('month') ? { month: url.searchParams.get('month') } : {}) })
    app('/usage')
    const user = userEvent.setup()
    await screen.findByLabelText('Tokens used this month')
    expect(screen.getByRole('button', { name: 'Next month' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Previous month' }))
    await waitFor(() => expect(server.calls.some(c => c.path === 'ai-usage' && c.method === 'GET')).toBe(true))
    expect(screen.getByRole('button', { name: 'Next month' })).toBeEnabled()
    await user.click(screen.getByRole('button', { name: 'Next month' }))
    expect(screen.getByRole('button', { name: 'Next month' })).toBeDisabled()
  })

  it('says plainly when the AI has not been used, and that demo mode uses no tokens', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /ai-usage': () => usage({ provider: 'Demo', totals: { calls: 0, inputTokens: 0, outputTokens: 0, totalTokens: 0 }, lastMonth: { calls: 0, inputTokens: 0, outputTokens: 0, totalTokens: 0 }, byKind: [], byProject: [], recent: [] }) })
    app('/usage')
    expect(await screen.findByText('The AI has not been used in this month.')).toBeInTheDocument()
    expect(screen.getAllByText('Nothing yet this month.').length).toBeGreaterThan(0)
    expect(screen.getByText(/demo mode, which returns a fixed example and uses no tokens/)).toBeInTheDocument()
  })

  it('is reachable from the menu for a user and for an administrator, and shows the month\'s tokens on the home page', async () => {
    signedIn(); mockApi(standard); app('/')
    expect(await screen.findByRole('link', { name: /AI tokens this month/ })).toHaveTextContent('24.2K')
    expect(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'AI usage' })).toHaveAttribute('href', '/usage')
  })

  it('shows a clear error when the usage cannot be loaded', async () => {
    signedIn(); mockApi({ ...standard, 'GET /ai-usage': () => problem(403, 'Not allowed.', 'The usage could not be read.') }); app('/usage')
    expect(await screen.findByRole('alert')).toHaveTextContent(/usage could not be read|Server error/)
  })
})
