import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox } from './offline/outbox'
import { app, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const asAdmin = { 'POST /auth/refresh': () => tokens({ roles: ['Admin'], name: 'Dev Admin', projects: [] }) }

async function openDialog(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: /Account menu/ }))
  await user.click(await screen.findByRole('menuitem', { name: 'Change password' }))
  return screen.findByRole('dialog', { name: 'Change your password' })
}

describe('changing your own password', () => {
  it.each([['a user', {}], ['an administrator', asAdmin]])('is in the account menu for %s, and needs the current password and a good new one typed twice', async (_who, extra) => {
    signedIn(); mockApi({ ...standard, ...extra, 'GET /admin/overview': () => ({ users: 1, activeUsers: 1, projects: 0, flows: 0, notes: 0, documents: 0, aiProvider: 'gemini', aiReady: true }) }); app('/')
    const user = userEvent.setup()
    const dialog = await openDialog(user)
    // drawn on the page, not inside the top bar that opened it (the bar blurs its background, which would squeeze a dialog into it)
    expect(dialog.closest('header')).toBeNull()
    expect(dialog.parentElement?.parentElement).toBe(document.body)
    const save = within(dialog).getByRole('button', { name: 'Change password' })
    expect(save).toBeDisabled()
    await user.type(within(dialog).getByLabelText('Current password'), 'Old-Passw0rd-1')
    await user.type(within(dialog).getByLabelText('New password'), 'short1')
    expect(within(dialog).getByText('Use at least 10 characters.')).toBeInTheDocument()
    await user.clear(within(dialog).getByLabelText('New password')); await user.type(within(dialog).getByLabelText('New password'), 'onlyletterspassword')
    expect(within(dialog).getByText('Use letters and digits together.')).toBeInTheDocument()
    await user.clear(within(dialog).getByLabelText('New password')); await user.type(within(dialog).getByLabelText('New password'), 'New-Passw0rd-123')
    await user.type(within(dialog).getByLabelText('New password again'), 'New-Passw0rd-12')
    expect(within(dialog).getByText('The two do not match.')).toBeInTheDocument()
    expect(save).toBeDisabled()
  })

  it('sends the passwords, keeps the person signed in with the new tokens, and says it worked', async () => {
    signedIn()
    const server = mockApi({ ...standard, 'POST /auth/change-password': () => tokens({ access: 'access-new', refresh: 'refresh-new' }) })
    app('/')
    const user = userEvent.setup()
    const dialog = await openDialog(user)
    await user.type(within(dialog).getByLabelText('Current password'), 'Old-Passw0rd-1')
    await user.type(within(dialog).getByLabelText('New password'), 'New-Passw0rd-123')
    await user.type(within(dialog).getByLabelText('New password again'), 'New-Passw0rd-123')
    await user.click(within(dialog).getByRole('button', { name: 'Change password' }))
    await waitFor(() => expect(server.calls.find(c => c.path === 'auth/change-password')?.body).toEqual({ currentPassword: 'Old-Passw0rd-1', newPassword: 'New-Passw0rd-123' }))
    expect(await screen.findByText('Your password was changed.')).toBeInTheDocument()
    expect(auth.refreshToken).toBe('refresh-new')                                  // this sign-in carries on
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Change your password' })).not.toBeInTheDocument())
  })

  it('says why when the current password is wrong, and keeps the dialog open', async () => {
    signedIn()
    mockApi({ ...standard, 'POST /auth/change-password': () => problem(422, 'The current password is not right.', undefined, { code: 'wrong_password' }) })
    app('/')
    const user = userEvent.setup()
    const dialog = await openDialog(user)
    await user.type(within(dialog).getByLabelText('Current password'), 'Wrong-Passw0rd-1')
    await user.type(within(dialog).getByLabelText('New password'), 'New-Passw0rd-123')
    await user.type(within(dialog).getByLabelText('New password again'), 'New-Passw0rd-123')
    await user.click(within(dialog).getByRole('button', { name: 'Change password' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('The current password is not right.')
  })
})

describe('deleting a user (administrator only)', () => {
  const account = (over: object) => ({ id: 'u-2', email: 'ravi@demo.local', displayName: 'Ravi Shankar', isActive: true, roles: ['ImplementationLead'], preferredLanguage: null, ...over })
  const routes = (over: Record<string, unknown> = {}) => ({
    ...standard, ...asAdmin, 'GET /projects': () => [],
    'GET /users': () => [account({ id: 'u-1', email: 'admin@flowcapture.local', displayName: 'Dev Admin', roles: ['Admin'] }), account({})], ...over,
  })

  it('offers Delete beside everybody except yourself, asks first, and removes the person', async () => {
    signedIn()
    let gone = false
    const server = mockApi(routes({
      'GET /users': () => [account({ id: 'u-1', email: 'admin@flowcapture.local', displayName: 'Dev Admin', roles: ['Admin'] }), ...(gone ? [] : [account({})])],
      'DELETE /users/u-2': () => { gone = true; return undefined },
    }))
    app('/admin/users')
    const user = userEvent.setup()
    const me = (await screen.findByText('Dev Admin (you)')).closest('li')!
    expect(within(me).queryByRole('button', { name: /Delete/ })).not.toBeInTheDocument()
    await user.click(await screen.findByRole('button', { name: 'Delete Ravi Shankar' }))
    const dialog = await screen.findByRole('dialog', { name: 'Delete Ravi Shankar?' })
    expect(within(dialog).getByText(/Nothing is erased from the database/)).toBeInTheDocument()
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(server.count('DELETE', 'users/u-2')).toBe(0)
    await user.click(screen.getByRole('button', { name: 'Delete Ravi Shankar' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete user' }))
    await waitFor(() => expect(server.count('DELETE', 'users/u-2')).toBe(1))
    expect(await screen.findByText('Ravi Shankar was deleted.')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByText('Ravi Shankar')).not.toBeInTheDocument())
  })

  it('shows the reason when the server refuses (for example the last administrator)', async () => {
    signedIn()
    mockApi(routes({ 'DELETE /users/u-2': () => problem(422, 'This is the only administrator. Make another person an administrator first.', undefined, { code: 'last_admin' }) }))
    app('/admin/users')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Delete Ravi Shankar' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete user' }))
    expect(await screen.findByText(/This is the only administrator/)).toBeInTheDocument()
  })
})


describe('seeing what was typed in the password boxes', () => {
  it('has an eye on every box that shows the typing and hides it again', async () => {
    signedIn(); mockApi(standard); app('/')
    const user = userEvent.setup()
    const dialog = await openDialog(user)
    const boxes = ['Current password', 'New password', 'New password again'].map(name => within(dialog).getByLabelText(name) as HTMLInputElement)
    const eyes = within(dialog).getAllByRole('button', { name: 'Show password' })
    expect(eyes).toHaveLength(3)                                                          // one beside each box
    boxes.forEach(b => expect(b.type).toBe('password'))
    for (const [i, text] of ['Old-Passw0rd-1', 'New-Passw0rd-123', 'New-Passw0rd-123'].entries()) await user.type(boxes[i], text)

    await user.click(eyes[1])                                                             // only the box beside the pressed eye changes
    expect(boxes[1].type).toBe('text'); expect(boxes[1]).toHaveValue('New-Passw0rd-123')
    expect(boxes[0].type).toBe('password'); expect(boxes[2].type).toBe('password')
    const hide = within(dialog).getByRole('button', { name: 'Hide password' })
    expect(hide).toHaveAttribute('aria-pressed', 'true')
    await user.click(hide)
    expect(boxes[1].type).toBe('password')
    expect(within(dialog).getAllByRole('button', { name: 'Show password' })).toHaveLength(3)

    await user.click(eyes[0]); await user.click(eyes[2])
    expect(boxes[0]).toHaveValue('Old-Passw0rd-1'); expect(boxes[2]).toHaveValue('New-Passw0rd-123')
  })

  it('keeps the cursor in the box when the eye is pressed, and starts hidden every time the window opens', async () => {
    signedIn(); mockApi(standard); app('/')
    const user = userEvent.setup()
    let dialog = await openDialog(user)
    const box = within(dialog).getByLabelText('New password') as HTMLInputElement
    await user.click(box)
    await user.click(within(dialog).getAllByRole('button', { name: 'Show password' })[1])
    expect(document.activeElement).toBe(box)
    expect(box.type).toBe('text')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    dialog = await openDialog(user)
    expect((within(dialog).getByLabelText('New password') as HTMLInputElement).type).toBe('password')
  })
})