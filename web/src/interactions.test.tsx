import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, client, flow, project, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }
const now = () => new Date().toISOString()
const account = (over: object) => ({ id: 'u-2', email: 'ravi@demo.local', displayName: 'Ravi Shankar', isActive: true, roles: ['ImplementationLead'], preferredLanguage: null, ...over })
const doc = (over: object = {}) => ({ id: 'doc-1', docNumber: 'IMPL-KMC-NR-001', title: 'Bed allocation', kind: 'Guide', projectId: 'p-1', projectName: 'Kauvery Hospital', flowId: 'f-1', versionNo: 1,
  createdAt: now(), createdByName: 'Asha Lead', formats: ['pdf'], hasDiagram: false, moduleId: null, stage: 'Working', clientId: 'c-1', clientName: 'Kauvery Group', ...over })
const clipboard = () => { const writeText = vi.fn().mockResolvedValue(undefined); Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true }); return writeText }
/** Catches the file the page saves (CSV), and gives back its text. */
function captureFile() {
  let saved: Blob | null = null
  vi.spyOn(URL, 'createObjectURL').mockImplementation(b => { saved = b as Blob; return 'blob:x' })
  return () => new Promise<string>(done => { const r = new FileReader(); r.onload = () => done(String(r.result).replace('﻿', '')); r.readAsText(saved!) })
}

describe('search and commands (Ctrl+K)', () => {
  const projects = [project, { ...project, id: 'p-2', hospitalName: 'Pharmacy returns', code: 'PHR' }]
  const library = () => [doc({ id: 'd-9', docNumber: 'IMPL-PHR-NR-004', title: 'Pharmacy returns document' })]

  it('opens from the keyboard, lists the pages first, and finds a document and opens it with Enter', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects': () => projects, 'GET /library': library, 'GET /projects/*': () => [] }); app('/')
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Hello, Asha' })
    await user.keyboard('{Control>}k{/Control}')
    const dialog = await screen.findByRole('dialog', { name: 'Search and commands' })
    const box = within(dialog).getByRole('combobox', { name: 'Search' })
    expect(box).toHaveFocus()
    for (const page of ['Projects', 'New project', 'Documents', 'AI usage']) expect(within(dialog).getByRole('option', { name: new RegExp('^' + page) })).toBeInTheDocument()

    await user.type(box, 'phr')
    expect(await within(dialog).findByRole('option', { name: /Pharmacy returns document/ })).toBeInTheDocument()   // a document
    expect(within(dialog).getByRole('option', { name: /^Pharmacy returns/ })).toBeInTheDocument()                  // a project
    await user.type(box, ' 004')
    expect(within(dialog).getAllByRole('option')).toHaveLength(1)
    await user.keyboard('{Enter}')
    expect(screen.queryByRole('dialog', { name: 'Search and commands' })).not.toBeInTheDocument()
    expect(await screen.findByRole('searchbox', { name: 'Search documents' })).toHaveValue('IMPL-PHR-NR-004')       // the Documents page, already searching for it
  })

  it('moves with the arrow keys, says when nothing matches, and closes with Escape', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/')
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Hello, Asha' })
    await user.keyboard('{Control>}k{/Control}')
    const dialog = await screen.findByRole('dialog', { name: 'Search and commands' })
    const options = () => within(dialog).getAllByRole('option')
    expect(options()[0]).toHaveAttribute('aria-selected', 'true')
    await user.keyboard('{ArrowDown}{ArrowDown}')
    expect(options()[2]).toHaveAttribute('aria-selected', 'true')
    await user.keyboard('{ArrowUp}')
    expect(options()[1]).toHaveAttribute('aria-selected', 'true')
    await user.type(within(dialog).getByRole('combobox'), 'zzzzzz')
    expect(within(dialog).getByText(/Nothing matches/)).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog', { name: 'Search and commands' })).not.toBeInTheDocument()
  })

  it('offers the projects opened last when nothing is typed, and runs an action such as switching the theme', async () => {
    signedIn()
    localStorage.setItem('fc.pref.u-1.recent-projects', JSON.stringify([{ id: 'p-2', name: 'Pharmacy returns' }]))
    mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/')
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Hello, Asha' })
    await user.click(screen.getByRole('button', { name: 'Search' }))
    const dialog = await screen.findByRole('dialog', { name: 'Search and commands' })
    expect(within(dialog).getByRole('option', { name: /Pharmacy returns/ })).toBeInTheDocument()
    const before = document.documentElement.dataset.theme
    await user.click(within(dialog).getByRole('option', { name: /Switch light and dark/ }))
    expect(document.documentElement.dataset.theme).not.toBe(before)
  })

  it('opens with a slash, but not while the person is typing in a field', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/new')
    const user = userEvent.setup()
    const name = await screen.findByLabelText(/Project name/)
    await user.type(name, 'a/b')
    expect(name).toHaveValue('a/b')                                                                    // the slash was typed, not taken
    expect(screen.queryByRole('dialog', { name: 'Search and commands' })).not.toBeInTheDocument()
    await user.click(document.body)
    await user.keyboard('/')
    expect(await screen.findByRole('dialog', { name: 'Search and commands' })).toBeInTheDocument()
  })

  it('shows an administrator the people and clients, not the projects, and goes to the list already searching', async () => {
    signedIn()
    mockApi({ ...standard, ...asAdmin, 'GET /users': () => [account({ id: 'u-1', displayName: 'Dev Admin', email: 'admin@flowcapture.local', roles: ['Admin'] }), account({})], 'GET /clients': () => [client] })
    app('/admin')
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Admin' })
    await user.keyboard('{Control>}k{/Control}')
    const dialog = await screen.findByRole('dialog', { name: 'Search and commands' })
    const box = within(dialog).getByRole('combobox')
    await user.type(box, 'ravi')
    await user.click(await within(dialog).findByRole('option', { name: /Ravi Shankar/ }))
    expect(await screen.findByRole('searchbox', { name: 'Search users' })).toHaveValue('ravi@demo.local')
    expect(screen.getByText('Ravi Shankar')).toBeInTheDocument()
    await user.keyboard('{Control>}k{/Control}')
    await user.type(within(await screen.findByRole('dialog', { name: 'Search and commands' })).getByRole('combobox'), 'kauvery')
    expect(within(screen.getByRole('dialog')).queryByRole('option', { name: /Kauvery Hospital/ })).not.toBeInTheDocument()   // a project: not for the administrator
    expect(await within(screen.getByRole('dialog')).findByRole('option', { name: /Kauvery Group/ })).toBeInTheDocument()
  })
})

describe('keyboard shortcuts', () => {
  it('goes to a page with G then a letter, starts a project with N, and lists them all with ?', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/')
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Hello, Asha' })
    await user.keyboard('gd')
    expect(await screen.findByRole('heading', { name: 'Documents' })).toBeInTheDocument()
    await user.keyboard('n')
    expect(await screen.findByRole('heading', { name: 'New project' })).toBeInTheDocument()
    await user.click(document.body)
    await user.keyboard('?')
    const help = await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })
    expect(within(help).getByText('Start a new project')).toBeInTheDocument()
    expect(within(help).getByText('Search everything and run commands')).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('gives an administrator their own set, and ignores a shortcut while a window is open', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin }); app('/admin')
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Admin' })
    await user.keyboard('gu')
    expect(await screen.findByRole('heading', { name: 'Users' })).toBeInTheDocument()
    await user.keyboard('?')
    await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })
    await user.keyboard('gc')
    expect(screen.queryByRole('heading', { name: 'Clients' })).not.toBeInTheDocument()                // the window had the keyboard
  })
})

describe('the menu on the left', () => {
  it('collapses to icons, remembers that after a reload, and opens again', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] })
    const first = app('/')
    const user = userEvent.setup()
    await screen.findByRole('heading', { name: 'Hello, Asha' })
    expect(document.querySelector('.app')).not.toHaveClass('compact')
    await user.click(screen.getByRole('button', { name: 'Collapse the menu' }))
    expect(document.querySelector('.app')).toHaveClass('compact')
    expect(screen.getByRole('button', { name: 'Expand the menu' })).toHaveAttribute('aria-pressed', 'true')
    expect(document.querySelector('.rail a[href="/documents"]')).toHaveAttribute('data-tip', 'Documents')    // an icon alone still says what it is
    first.unmount()

    app('/')
    await screen.findByRole('heading', { name: 'Hello, Asha' })
    expect(document.querySelector('.app')).toHaveClass('compact')
    await user.keyboard('[[')                                                                              // the same from the keyboard
    expect(document.querySelector('.app')).not.toHaveClass('compact')
  })

  it('has a link that skips straight to the content, and a breadcrumb that says where the person is', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/new')
    await screen.findByRole('heading', { name: 'New project' })
    expect(screen.getByRole('link', { name: 'Skip to the content' })).toHaveAttribute('href', '#content')
    const crumbs = screen.getByRole('navigation', { name: 'Breadcrumb' })
    expect(within(crumbs).getByRole('link', { name: 'Projects' })).toHaveAttribute('href', '/')
    expect(within(crumbs).getByText('New project')).toHaveAttribute('aria-current', 'page')
  })
})

describe('Home with many projects', () => {
  const many = Array.from({ length: 6 }, (_, i) => ({ ...project, id: `p-${i + 1}`, code: `P${i + 1}`, hospitalName: ['Alpha ward', 'Beta pharmacy', 'Gamma billing', 'Delta lab', 'Epsilon OT', 'Zeta MRD'][i], startDate: `2026-11-0${i + 1}`, clientName: 'Kauvery Group' }))
  const routes = (over: object = {}) => ({ ...standard, 'GET /projects': () => many, 'GET /projects/*': () => [], 'GET /library': () => [doc({ projectId: 'p-2', id: 'd-2' })], ...over })
  const cards = () => screen.getAllByRole('link', { name: /^Open / }).map(a => a.getAttribute('aria-label'))

  it('finds a project by what is typed, filters by where it is, sorts, and clears it all', async () => {
    signedIn(); mockApi(routes()); app('/')
    const user = userEvent.setup()
    await screen.findByRole('link', { name: 'Open Alpha ward' })
    await user.type(screen.getByRole('searchbox', { name: 'Search your projects' }), 'phar')
    expect(cards()).toEqual(['Open Beta pharmacy'])
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(cards()).toHaveLength(6)

    await user.click(screen.getByRole('button', { name: /Document ready/ }))                            // only the project with a document
    await waitFor(() => expect(cards()).toEqual(['Open Beta pharmacy']))
    await user.click(screen.getByRole('button', { name: /In progress/ }))
    await waitFor(() => expect(cards()).toHaveLength(5))
    await user.click(screen.getByRole('button', { name: /^All/ }))

    await user.selectOptions(screen.getByLabelText('Sort projects'), 'name')
    expect(cards()[0]).toBe('Open Alpha ward')
    await user.selectOptions(screen.getByLabelText('Sort projects'), 'new')
    expect(cards()[0]).toBe('Open Zeta MRD')
  })

  it('pins a project to the top, remembers the pin, and filters to the pinned ones', async () => {
    signedIn(); mockApi(routes())
    const first = app('/')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Pin Delta lab' }))
    const pinned = screen.getByRole('region', { name: 'Pinned projects' })
    expect(within(pinned).getByRole('link', { name: 'Open Delta lab' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Unpin Delta lab' })).toHaveAttribute('aria-pressed', 'true')
    first.unmount()

    app('/')
    expect(await within(await screen.findByRole('region', { name: 'Pinned projects' })).findByRole('link', { name: 'Open Delta lab' })).toBeInTheDocument()    // after a reload
    await user.click(screen.getByRole('button', { name: /^Pinned/ }))
    expect(cards()).toEqual(['Open Delta lab'])
    await user.click(screen.getByRole('button', { name: 'Unpin Delta lab' }))
    expect(await screen.findByText('No project matches')).toBeInTheDocument()
  })

  it('offers the projects opened last, and does not show the toolbar for a few projects', async () => {
    signedIn()
    localStorage.setItem('fc.pref.u-1.recent-projects', JSON.stringify([{ id: 'p-4', name: 'Delta lab' }, { id: 'gone', name: 'Deleted one' }]))
    mockApi(routes()); const view = app('/')
    const strip = await screen.findByLabelText('Continue where you left off')
    expect(within(strip).getByRole('link', { name: 'Delta lab' })).toHaveAttribute('href', '/p/p-4')
    expect(within(strip).queryByText('Deleted one')).not.toBeInTheDocument()                          // a project that is gone is not offered
    view.unmount()

    mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/')
    await screen.findByRole('heading', { name: 'Hello, Asha' })
    expect(screen.queryByRole('search', { name: 'Find a project' })).not.toBeInTheDocument()
  })

  it('says why the projects could not be loaded, and tries again on request', async () => {
    signedIn()
    let up = false
    const server = mockApi({ ...standard, 'GET /projects': () => (up ? [project] : problem(500, 'The server is busy.')), 'GET /projects/*': () => [] }); app('/')
    const alert = await screen.findByRole('alert', {}, { timeout: 8000 })
    expect(alert).toHaveTextContent('This could not be loaded.')
    up = true
    await userEvent.setup().click(within(alert).getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('link', { name: 'Open Kauvery Hospital' })).toBeInTheDocument()
    expect(server.count('GET', 'projects')).toBeGreaterThan(1)
  }, 15_000)
})

describe('the new project form', () => {
  it('says what is wrong beside the field once it was left empty, and shows how much is left', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/new')
    const user = userEvent.setup()
    const name = await screen.findByLabelText(/Project name/)
    expect(name.closest('.field')).toHaveClass('required')
    await user.click(name); await user.tab()
    expect(await screen.findByText('Give the project a name.')).toBeInTheDocument()
    expect(name).toBeInvalid()
    await user.type(name, 'IP discharge')
    expect(screen.queryByText('Give the project a name.')).not.toBeInTheDocument()
    expect(screen.getByText('12/120')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create and add notes' })).toBeEnabled()
  })

  it('warns that the name is already used by one of the client’s projects, but still lets it be made', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects': () => [{ ...project, clientId: 'c-1' }], 'GET /projects/*': () => [] }); app('/new')
    await userEvent.setup().type(await screen.findByLabelText(/Project name/), 'kauvery hospital')
    expect(await screen.findByText(/You already have a project with this name/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create and add notes' })).toBeEnabled()
  })

  it('chooses the client used last, and remembers the one chosen now', async () => {
    signedIn()
    const second = { ...client, id: 'c-2', name: 'Apollo Group' }
    const server = mockApi({ ...standard, 'GET /clients': () => [client, second], 'GET /projects/*': () => [], 'POST /projects/quick': () => ({ projectId: 'p-9', departmentId: 'd-9', flowId: 'f-9', name: 'x', code: 'X' }) })
    const first = app('/new')
    const user = userEvent.setup()
    const pick = await screen.findByLabelText('Client')
    expect(pick).toHaveValue('')                                                                         // nothing remembered yet
    await user.selectOptions(pick, 'c-2')
    await user.type(screen.getByLabelText(/Project name/), 'Lab results')
    await user.click(screen.getByRole('button', { name: 'Create and add notes' }))
    await waitFor(() => expect(server.count('POST', 'projects/quick')).toBe(1))
    expect(server.calls.find(c => c.path === 'projects/quick')!.body).toMatchObject({ clientId: 'c-2' })
    first.unmount()

    app('/new')
    expect(await screen.findByLabelText('Client')).toHaveValue('c-2')                                    // chosen for them next time
  })

  it('asks before throwing away what was typed, and leaves at once when nothing was', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/new')
    const user = userEvent.setup()
    await user.type(await screen.findByLabelText(/Project name/), 'Half typed')
    await user.click(screen.getByRole('button', { name: 'Cancel' }))
    const ask = await screen.findByRole('dialog', { name: 'Leave without creating the project?' })
    await user.click(within(ask).getByRole('button', { name: 'Cancel' }))
    expect(screen.getByLabelText(/Project name/)).toHaveValue('Half typed')                              // still there
    await user.click(screen.getByRole('button', { name: 'Cancel' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Leave' }))
    expect(await screen.findByRole('heading', { name: 'Hello, Asha' })).toBeInTheDocument()
  })

  it('warns the browser before the page is closed with something typed', async () => {
    signedIn(); mockApi({ ...standard, 'GET /projects/*': () => [] }); app('/new')
    await userEvent.setup().type(await screen.findByLabelText(/Project name/), 'x')
    const event = new Event('beforeunload', { cancelable: true })
    act(() => { window.dispatchEvent(event) })
    expect(event.defaultPrevented).toBe(true)
  })
})

describe('the users page', () => {
  const people = [
    account({ id: 'u-1', displayName: 'Dev Admin', email: 'admin@flowcapture.local', roles: ['Admin'] }), account({}),
    account({ id: 'u-3', displayName: 'Stopped Person', email: 'stopped@demo.local', isActive: false }), account({ id: 'u-4', displayName: 'Asha Lead', email: 'asha@demo.local' }),
  ]
  const routes = (over: object = {}) => ({ ...standard, ...asAdmin, 'GET /users': () => people, ...over })

  it('searches, filters by role and by access, sorts, takes the list away as CSV and clears it all', async () => {
    signedIn(); mockApi(routes()); app('/admin/users')
    const user = userEvent.setup()
    await screen.findByText('Ravi Shankar')
    const names = () => screen.getAllByRole('listitem').map(li => li.querySelector('.urow-main b')?.textContent).filter(Boolean)
    await user.type(screen.getByRole('searchbox', { name: 'Search users' }), 'demo.local')
    expect(names()).toHaveLength(3)
    await user.selectOptions(screen.getByLabelText('Filter by access'), 'stopped')
    expect(names()).toEqual(['Stopped Person'])
    const file = captureFile()
    await user.click(screen.getByRole('button', { name: 'Export CSV' }))
    expect(await file()).toBe('Name,Email,Role,Access\r\nStopped Person,stopped@demo.local,User,Stopped')
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(names()).toHaveLength(4)
    await user.selectOptions(screen.getByLabelText('Filter by role'), 'admin')
    expect(names()).toEqual(['Dev Admin (you)'])
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    await user.selectOptions(screen.getByLabelText('Sort users'), 'email')
    expect(names()[0]).toBe('Dev Admin (you)')                                                           // admin@ sorts before asha@
  })

  it('remembers how the list was filtered, and says when nothing matches', async () => {
    signedIn(); mockApi(routes()); const first = app('/admin/users')
    const user = userEvent.setup()
    await screen.findByText('Ravi Shankar')
    await user.selectOptions(screen.getByLabelText('Filter by access'), 'active')
    first.unmount()
    app('/admin/users')
    expect(await screen.findByLabelText('Filter by access')).toHaveValue('active')
    expect(screen.queryByText('Stopped Person')).not.toBeInTheDocument()
    await user.type(screen.getByRole('searchbox', { name: 'Search users' }), 'nobody at all')
    expect(await screen.findByText('No user matches')).toBeInTheDocument()
  })

  it('checks a new user as it is typed: a wrong or already used email, the password rules, and what is missing', async () => {
    signedIn(); mockApi(routes()); app('/admin/users')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Add user' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a user' })
    const add = within(dialog).getByRole('button', { name: 'Add user' })
    expect(add).toBeDisabled()
    await user.click(within(dialog).getByLabelText('Full name')); await user.tab()
    expect(within(dialog).getByText('Enter their full name.')).toBeInTheDocument()
    const email = within(dialog).getByLabelText(/^Email/)
    await user.type(email, 'not-an-email')
    expect(within(dialog).getByText(/Enter a valid email address/)).toBeInTheDocument()
    await user.clear(email); await user.type(email, 'RAVI@demo.local')
    expect(within(dialog).getByText('A user with this email already exists.')).toBeInTheDocument()     // not case sensitive
    await user.clear(email); await user.type(email, 'new.person@demo.local')
    await user.type(within(dialog).getByLabelText('Full name'), 'New Person')
    expect(within(dialog).queryByText(/valid email|already exists/)).not.toBeInTheDocument()
    expect(add).toBeEnabled()
    const rules = within(dialog).getByRole('list', { name: 'Requirements' })
    expect(within(rules).getAllByText(/\(met\)/)).toHaveLength(3)                                          // the filled-in password meets all
    await user.clear(within(dialog).getByLabelText('Password')); await user.type(within(dialog).getByLabelText('Password'), 'short')
    expect(within(rules).getByText(/At least 10 characters/)).toHaveTextContent('not met yet')
    expect(add).toBeDisabled()
  })

  it('asks before closing the form with something typed', async () => {
    signedIn(); mockApi(routes()); app('/admin/users')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Add user' }))
    await user.type(await screen.findByLabelText('Full name'), 'Half')
    await user.keyboard('{Escape}')
    const ask = await screen.findByRole('dialog', { name: 'Close without adding?' })
    await user.click(within(ask).getByRole('button', { name: 'Cancel' }))
    expect(screen.getByRole('dialog', { name: 'Add a user' })).toBeInTheDocument()
    await user.keyboard('{Escape}'); await user.click(within(await screen.findByRole('dialog', { name: 'Close without adding?' })).getByRole('button', { name: 'Close' }))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add a user' })).not.toBeInTheDocument())
  })

  it('submits the form with Enter and not twice', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /users': () => account({ id: 'u-9', displayName: 'New Person', email: 'new.person@demo.local' }) }))
    app('/admin/users')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Add user' }))
    await user.type(await screen.findByLabelText('Full name'), 'New Person')
    await user.type(screen.getByLabelText(/^Email/), 'new.person@demo.local{Enter}{Enter}')
    await screen.findByRole('dialog', { name: 'User added' })
    expect(server.count('POST', 'users')).toBe(1)
  })
})

describe('the clients page', () => {
  const ppl = Array.from({ length: 8 }, (_, i) => account({ id: `u-${i + 10}`, displayName: `Person ${i + 1}`, email: `person${i + 1}@demo.local` }))
  const routes = (over: object = {}) => ({ ...standard, ...asAdmin, 'GET /users': () => ppl, 'GET /clients': () => [{ ...client, userIds: ['u-10'] }, { ...client, id: 'c-2', name: 'Apollo Group', projectCount: 9, userIds: [] }], ...over })

  it('will not make a second client with a name that is used, however it is spelt', async () => {
    signedIn(); mockApi(routes()); app('/admin/clients')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Add client' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a client' })
    await user.type(within(dialog).getByLabelText('Client name'), 'apollo group')
    expect(within(dialog).getByText('A client with this name already exists.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Add client' })).toBeDisabled()
    await user.clear(within(dialog).getByLabelText('Client name')); await user.type(within(dialog).getByLabelText('Client name'), 'Fortis')
    expect(within(dialog).getByRole('button', { name: 'Add client' })).toBeEnabled()
  })

  it('finds a client, shows only those nobody handles, sorts by projects and exports', async () => {
    signedIn(); mockApi(routes()); app('/admin/clients')
    const user = userEvent.setup()
    await screen.findByText('Apollo Group')
    const shown = () => within(screen.getByRole('list', { name: 'Clients' })).getAllByRole('listitem').map(li => li.querySelector('.urow-main b')?.textContent)
    await user.selectOptions(screen.getByLabelText('Filter clients'), 'nobody')
    expect(shown()).toEqual(['Apollo Group'])
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    await user.selectOptions(screen.getByLabelText('Sort clients'), 'projects')
    expect(shown()[0]).toBe('Apollo Group')
    await user.type(screen.getByRole('searchbox', { name: 'Search clients' }), 'kauvery')
    expect(shown()).toEqual(['Kauvery Group'])
    const file = captureFile()
    await user.click(screen.getByRole('button', { name: 'Export CSV' }))
    expect(await file()).toContain('Kauvery Group')
  })

  it('finds a person among many in the people window, ticks everybody shown and counts what is ticked', async () => {
    signedIn(); mockApi(routes()); app('/admin/clients')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Assign people to Apollo Group' }))
    const dialog = await screen.findByRole('dialog', { name: 'People for Apollo Group' })
    expect(within(dialog).getByText('0 of 8 ticked')).toBeInTheDocument()
    await user.type(within(dialog).getByRole('searchbox', { name: 'Find a person' }), 'person1')
    expect(within(dialog).getAllByRole('checkbox')).toHaveLength(1)
    expect(within(dialog).getByText(/1 shown/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Tick shown' }))
    expect(within(dialog).getByText(/1 of 8 ticked/)).toBeInTheDocument()
    await user.clear(within(dialog).getByRole('searchbox', { name: 'Find a person' }))
    expect(within(dialog).getAllByRole('checkbox').filter(c => (c as HTMLInputElement).checked)).toHaveLength(1)   // the tick stayed when the search changed
    await user.type(within(dialog).getByRole('searchbox', { name: 'Find a person' }), 'zzz')
    expect(within(dialog).getByText(/Nobody matches/)).toBeInTheDocument()
  })
})

describe('final documents for the administrator', () => {
  const waiting = (n: number, over: object = {}) => doc({ id: `d-${n}`, docNumber: `IMPL-KMC-NR-00${n}`, title: `Request ${n}`, stage: 'Final', finalizedAt: now(), finalizedByName: n % 2 ? 'Asha Lead' : 'Ravi Shankar', clientName: n % 2 ? 'Kauvery Group' : 'Apollo Group', finalNote: `Note ${n}`, ...over })
  const verified = doc({ id: 'd-8', docNumber: 'IMPL-KMC-NR-008', title: 'Old request', stage: 'Verified', finalizedAt: now(), finalizedByName: 'Asha Lead', verifiedByName: 'Dev Admin', verifiedAt: now() })
  const routes = (over: object = {}) => ({ ...standard, ...asAdmin, 'GET /library': () => [waiting(1), waiting(2), waiting(3), verified], ...over })

  it('ticks documents, verifies them together with a progress dialog, and reports the one that failed', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /documents/d-2/versions/1/approve': () => problem(422, 'This version was already verified.', undefined, { code: 'already_decided' }), 'POST /documents/d-1/versions/1/approve': () => ({}), 'POST /documents/d-3/versions/1/approve': () => ({}) }))
    app('/documents')
    const user = userEvent.setup()
    await screen.findByText('Request 1')
    expect(screen.queryByLabelText('Select IMPL-KMC-NR-008')).not.toBeInTheDocument()                  // only a waiting document can be ticked
    await user.click(screen.getByRole('checkbox', { name: 'Select all 3 waiting' }))
    const bar = screen.getByRole('region', { name: 'Selected documents' })
    expect(bar).toHaveTextContent('3selected')
    await user.click(within(bar).getByRole('button', { name: 'Verify selected' }))
    const dialog = await screen.findByRole('dialog', { name: 'Verify 3 documents?' })
    expect(within(within(dialog).getByRole('list', { name: 'Documents to verify' })).getAllByRole('listitem')).toHaveLength(3)
    expect(server.count('POST', 'documents/d-1/versions/1/approve')).toBe(0)                            // nothing happens before it is confirmed
    await user.click(within(dialog).getByRole('button', { name: 'Verify 3' }))
    await waitFor(() => expect(server.count('POST', 'documents/d-3/versions/1/approve')).toBe(1))
    expect(server.calls.filter(c => c.path.endsWith('/approve')).every(c => JSON.stringify(c.body) === JSON.stringify({ approved: true, note: null }))).toBe(true)
    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent('1 could not be verified')
    expect(alert).toHaveTextContent('IMPL-KMC-NR-002')
    expect(within(dialog).getByRole('button', { name: 'Try again' })).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Close' }))
    expect(screen.getByRole('region', { name: 'Selected documents' })).toHaveTextContent('1selected')     // the one that failed stays ticked
  })

  it('opens the details of a document in a side panel with its history, and decides from there', async () => {
    signedIn()
    const detail = { id: 'd-1', docNumber: 'IMPL-KMC-NR-001', title: 'Request 1', scope: 'Guide', flowId: 'f-1', currentVersionNo: 2, versions: [
      { versionNo: 2, generatedAt: now(), generatedByName: 'Asha Lead', approvalStatus: 'Pending', templateVersion: 'x', sourceFlowVersionNos: [1], approvedByName: null, approvalNote: null, finalizedAt: now(), finalizedByName: 'Asha Lead', finalNote: 'Fixed the scope' },
      { versionNo: 1, generatedAt: now(), generatedByName: 'Asha Lead', approvalStatus: 'Rejected', templateVersion: 'x', sourceFlowVersionNos: [1], approvedByName: 'Dev Admin', approvalNote: 'Scope is missing', finalizedAt: now(), finalizedByName: 'Asha Lead', finalNote: null },
    ] }
    mockApi(routes({ 'GET /documents/d-1': () => detail }))
    app('/documents')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Details of IMPL-KMC-NR-001' }))
    const drawer = await screen.findByRole('dialog', { name: 'Request 1' })
    expect(within(drawer).getByText('Their note')).toBeInTheDocument()
    const history = await within(drawer).findByRole('list', { name: 'Versions marked final' })
    expect(within(history).getAllByRole('listitem')).toHaveLength(2)
    expect(within(history).getByText(/Returned by Dev Admin: Scope is missing/)).toBeInTheDocument()
    expect(within(history).getByText(/Fixed the scope/)).toBeInTheDocument()
    await user.click(within(drawer).getByRole('button', { name: 'Return' }))
    expect(await screen.findByRole('dialog', { name: 'Return this document?' })).toBeInTheDocument()   // the drawer closed and the decision opened
    expect(screen.queryByRole('dialog', { name: 'Request 1' })).not.toBeInTheDocument()
  })

  it('remembers the filter, lets a link choose it, and takes the list away as CSV', async () => {
    signedIn(); mockApi(routes()); const first = app('/documents')
    const user = userEvent.setup()
    await screen.findByText('Request 1')
    await user.selectOptions(screen.getByLabelText('Status'), 'Verified')
    expect(screen.queryByText('Request 1')).not.toBeInTheDocument()
    first.unmount()

    app('/documents')
    expect(await screen.findByLabelText('Status')).toHaveValue('Verified')                              // remembered
    expect(await screen.findByText('Old request')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    const file = captureFile()
    await user.click(screen.getByRole('button', { name: 'Export CSV' }))
    const csv = await file()
    expect(csv.split('\r\n')[0]).toBe('Document number,Title,Type,Client,Project,Version,Status,Marked final by,Marked final at,Their note,Verified by,Verified at,Returned because')
    expect(csv).toContain('IMPL-KMC-NR-001,Request 1,Implementation document,Kauvery Group,Kauvery Hospital,1,Waiting,Asha Lead')
    expect(csv.split('\r\n')).toHaveLength(5)                                                           // the header and four documents
  })

  it('opens already showing what a link on the overview asked for, whatever was remembered', async () => {
    signedIn()
    localStorage.setItem('fc.pref.u-1.final.stage', JSON.stringify('Verified'))
    mockApi(routes()); app('/documents?status=Final')
    expect(await screen.findByText('Request 1')).toBeInTheDocument()
    expect(screen.queryByText('Old request')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Status')).toHaveValue('Final')
  })

  it('refreshes on request, copies a number to the clipboard, and tells the time it was loaded', async () => {
    signedIn()
    const server = mockApi(routes())
    app('/documents')
    const user = userEvent.setup()
    const write = clipboard()
    await screen.findByText('Request 1')
    await user.click(screen.getByRole('button', { name: 'Refresh the list' }))
    await waitFor(() => expect(server.count('GET', 'library')).toBe(2))
    await user.click(screen.getByRole('button', { name: 'More actions for IMPL-KMC-NR-002' }))
    await user.click(screen.getByRole('menuitem', { name: 'Copy the document number' }))
    expect(write).toHaveBeenCalledWith('IMPL-KMC-NR-002')
    expect(await screen.findByText('The document number copied.')).toBeInTheDocument()
  })

  it('makes the overview numbers links to what is behind them', async () => {
    signedIn(); mockApi({ ...standard, ...asAdmin, 'GET /admin/overview': () => ({ users: 5, activeUsers: 4, projects: 3, flows: 3, notes: 12, documents: 2, aiProvider: 'gemini', aiReady: true, awaitingVerification: 2, clients: 6 }) }); app('/admin')
    const totals = await screen.findByLabelText('Totals')
    expect(within(totals).getByRole('link', { name: /Active users/ })).toHaveAttribute('href', '/admin/users')
    expect(within(totals).getByRole('link', { name: /Clients/ })).toHaveAttribute('href', '/admin/clients')
    expect(within(totals).getByRole('link', { name: /Projects/ })).toHaveAttribute('href', '/admin/handling')
    expect(within(totals).getByRole('link', { name: /Final documents waiting/ })).toHaveAttribute('href', '/documents?status=Final')
    expect(screen.getByRole('button', { name: 'Refresh the numbers' })).toBeInTheDocument()
  })
})

describe('documents for a user', () => {
  it('offers quick filters with counts that are remembered, and copies the number', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /library': () => [doc(), doc({ id: 'd-2', docNumber: 'IMPL-KMC-NR-002', title: 'Other', stage: 'Returned', finalizedAt: now(), reviewNote: 'Scope' })] })
    const first = app('/documents')
    const user = userEvent.setup()
    const write = clipboard()
    await screen.findByText('Bed allocation')
    const quick = screen.getByRole('group', { name: 'Quick filters' })
    expect(within(quick).getByRole('button', { name: /^Returned to me/ })).toHaveTextContent('1')
    await user.click(within(quick).getByRole('button', { name: /^Returned to me/ }))
    expect(screen.queryByText('Bed allocation')).not.toBeInTheDocument()
    first.unmount()
    app('/documents')
    expect(await screen.findByText('Other')).toBeInTheDocument()                                         // still filtered after a reload
    await user.click(screen.getByRole('button', { name: 'Copy the number IMPL-KMC-NR-002' }))
    expect(write).toHaveBeenCalledWith('IMPL-KMC-NR-002')
    await user.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(screen.getByText('Bed allocation')).toBeInTheDocument()
  })

  it('offers Undo right after a document is marked final, and undoes it', async () => {
    signedIn()
    let final = false
    const server = mockApi({ ...standard, 'GET /library': () => [final ? doc({ stage: 'Final', finalizedAt: now(), finalizedByName: 'Asha Lead' }) : doc()],
      'POST /documents/doc-1/versions/1/final': () => { final = true; return {} }, 'DELETE /documents/doc-1/versions/1/final': () => { final = false; return {} } })
    app('/documents')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Mark as final' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Mark as final' }))
    await user.click(await screen.findByRole('button', { name: 'Undo' }))
    await waitFor(() => expect(server.count('DELETE', 'documents/doc-1/versions/1/final')).toBe(1))
    expect(await screen.findByText('Undone. It is a working copy again.')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Mark as final' })).toBeInTheDocument()
  })
})

describe('the activity list and the password form', () => {
  it('lists what the app said in this visit in the bell in the top bar', async () => {
    signedIn(); mockApi({ ...standard, 'GET /library': () => [doc()], 'POST /documents/doc-1/versions/1/final': () => ({}) }); app('/documents')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Mark as final' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Mark as final' }))
    await user.click(await screen.findByRole('button', { name: 'Activity, 1 new' }))
    expect(within(screen.getByRole('region', { name: 'Recent activity' })).getByText(/Marked as final/)).toBeInTheDocument()
  })

  it('shows the password rules as they are met and does not accept the same password again', async () => {
    signedIn(); mockApi(standard); app('/')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: /Account menu/ }))
    await user.click(screen.getByRole('menuitem', { name: 'Change password' }))
    const dialog = await screen.findByRole('dialog', { name: 'Change your password' })
    await user.type(within(dialog).getByLabelText('Current password'), 'Old-Passw0rd-1')
    await user.type(within(dialog).getByLabelText('New password'), 'Old-Passw0rd-1')
    expect(within(dialog).getByText('The new password must be different from the current one.')).toBeInTheDocument()
    await user.clear(within(dialog).getByLabelText('New password')); await user.type(within(dialog).getByLabelText('New password'), 'abc')
    expect(within(within(dialog).getByRole('list', { name: 'Requirements' })).getByText(/Has digits/)).toHaveTextContent('not met yet')
    await user.clear(within(dialog).getByLabelText('New password')); await user.type(within(dialog).getByLabelText('New password'), 'Brand-New-Passw0rd')
    expect(within(within(dialog).getByRole('list', { name: 'Requirements' })).getAllByText(/\(met\)/)).toHaveLength(3)
    await user.type(within(dialog).getByLabelText('New password again'), 'Brand-New')
    expect(within(dialog).getByText('The two do not match.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Change password' })).toBeDisabled()
  })
})

void flow



