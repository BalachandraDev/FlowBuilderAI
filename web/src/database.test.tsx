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
const info = (over: object = {}) => ({ server: '(localdb)\\MSSQLLocalDB', database: 'FlowCapture', provider: 'SQL Server', usingSavedServer: false, problem: null, ...over })
const tested = (over: object = {}) => ({ ok: true, message: 'The server answered. The database FlowCapture does not exist yet, so it will be created with all 40 tables.', databaseExists: false, existingTables: [], missingTables: ['Users', 'Clients'], ...over })
const move = (over: object = {}) => ({ state: 'running', step: 'Copying Users', startedAt: new Date().toISOString(), finishedAt: null, tables: [], notes: [], canUse: false, target: '4.213.114.123,59387 / FlowCapture', ...over })
const routes = (over: Record<string, unknown> = {}) => ({
  ...standard, ...asAdmin, 'GET /admin/database': () => info(), 'GET /admin/database/move': () => move({ state: 'idle' }), ...over,
})

async function fill(user: ReturnType<typeof userEvent.setup>) {
  await user.type(await screen.findByLabelText('Server address'), '4.213.114.123,59387')
  await user.type(screen.getByLabelText('User name'), 'dbuser')
  await user.type(screen.getByLabelText('Password'), 'p@ss;word=1')
}

describe('the database page (administrator)', () => {
  it('shows the server in use, and what the admin has to fill in', async () => {
    signedIn(); mockApi(routes()); app('/admin/database')
    expect(await screen.findByText(/The app uses: \(localdb\)\\MSSQLLocalDB \/ FlowCapture/)).toBeInTheDocument()
    expect(screen.getByLabelText('Database name')).toHaveValue('FlowCapture')
    expect(screen.getByLabelText('Password')).toHaveAttribute('type', 'password')
    expect(screen.getByRole('button', { name: /Test the connection/ })).toBeDisabled()
    expect(screen.getByRole('button', { name: /Make the tables and copy the data/ })).toBeDisabled()
  })

  it('tests the server, then copies, with the progress and the skipped rows shown, without stopping', async () => {
    signedIn()
    let polls = 0
    const server = mockApi(routes({
      'POST /admin/database/test': () => tested(),
      'POST /admin/database/move': () => move(),
      'GET /admin/database/move': () => (++polls < 2 ? move({ state: 'idle' }) : move({
        state: 'done', step: 'Finished', canUse: true, notes: ['The database FlowCapture was created.', '40 tables were created and 0 tables that were already there were kept.'],
        tables: [
          { table: 'Users', sourceRows: 2, inserted: 2, alreadyThere: 0, failed: 0, errors: [] },
          { table: 'Clients', sourceRows: 3, inserted: 2, alreadyThere: 0, failed: 1, errors: ['A row was skipped: Cannot insert duplicate key row'] },
          { table: 'Notes', sourceRows: 0, inserted: 0, alreadyThere: 0, failed: 0, errors: [] },
        ],
      })),
    }))
    app('/admin/database')
    const user = userEvent.setup()
    await fill(user)
    await user.click(screen.getByRole('button', { name: /Test the connection/ }))
    expect(await screen.findByText(/does not exist yet, so it will be created/)).toBeInTheDocument()
    expect(server.calls.find(c => c.path === 'admin/database/test')!.body).toEqual({ server: '4.213.114.123,59387', database: 'FlowCapture', user: 'dbuser', password: 'p@ss;word=1', windowsLogin: false })

    await user.click(screen.getByRole('button', { name: /Make the tables and copy the data/ }))
    const table = await screen.findByRole('table', { name: 'Rows copied' }, { timeout: 8000 })
    expect(within(table).getByText('Users')).toBeInTheDocument()
    expect(within(table).queryByText('Notes')).not.toBeInTheDocument()                    // nothing to copy: not listed
    expect(within(table).getByText('A row was skipped: Cannot insert duplicate key row')).toBeInTheDocument()
    expect(screen.getByText('Finished')).toBeInTheDocument()
    expect(screen.getByText(/4 rows added, 0 were already there, 1 skipped/)).toBeInTheDocument()
    expect(screen.getByText('The database FlowCapture was created.')).toBeInTheDocument()
  }, 20_000)

  it('explains a server that cannot be reached, and never shows the password back', async () => {
    signedIn(); mockApi(routes({ 'POST /admin/database/test': () => tested({ ok: false, message: 'The server could not be reached. Check the address and port, that the server is running, and that a firewall allows this computer.' }) })); app('/admin/database')
    const user = userEvent.setup()
    await fill(user)
    await user.click(screen.getByRole('button', { name: /Test the connection/ }))
    expect(await screen.findByText(/The server could not be reached/)).toBeInTheDocument()
    expect(screen.queryByText(/p@ss;word=1/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Make the tables and copy the data/ })).toBeDisabled()       // only after a good test
  })

  it('needs no user name or password with the Windows account option', async () => {
    signedIn(); const server = mockApi(routes({ 'POST /admin/database/test': () => tested() })); app('/admin/database')
    const user = userEvent.setup()
    await user.type(await screen.findByLabelText('Server address'), 'SQL01\\HMS')
    await user.click(screen.getByLabelText(/Use the Windows account/))
    expect(screen.getByLabelText('User name')).toBeDisabled()
    await user.click(screen.getByRole('button', { name: /Test the connection/ }))
    await waitFor(() => expect(server.calls.find(c => c.path === 'admin/database/test')?.body).toMatchObject({ server: 'SQL01\\HMS', windowsLogin: true }))
  })

  it('switches to the new server only after asking, and says the app must restart', async () => {
    signedIn()
    const server = mockApi(routes({ 'POST /admin/database/test': () => tested(), 'POST /admin/database/use': () => ({ saved: true, restartNeeded: true, restarting: false, message: 'Saved. Restart the app (stop it and start it again) and it will use the new server.' }) }))
    app('/admin/database')
    const user = userEvent.setup()
    await fill(user)
    await user.click(screen.getByRole('button', { name: /Test the connection/ }))
    await screen.findByText(/does not exist yet/)
    await user.click(screen.getByRole('button', { name: /Use this server from now on/ }))
    const dialog = await screen.findByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(server.count('POST', 'admin/database/use')).toBe(0)
    await user.click(screen.getByRole('button', { name: /Use this server from now on/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Use it' }))
    expect(await screen.findByText(/Restart the app \(stop it and start it again\)/)).toBeInTheDocument()
  })

  it('warns when the saved server could not be reached and the app fell back, and can go back to the original', async () => {
    signedIn()
    const server = mockApi(routes({
      'GET /admin/database': () => info({ usingSavedServer: true, problem: 'The saved database server could not be reached (timeout). The configured database is used until it can.' }),
      'DELETE /admin/database/use': () => ({ saved: true, restartNeeded: true, restarting: false, message: 'The saved server was removed. Restart the app.' }),
    }))
    app('/admin/database')
    expect(await screen.findByText(/The saved database server could not be reached/)).toBeInTheDocument()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: /Go back to the server the app was installed with/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Go back' }))
    await waitFor(() => expect(server.count('DELETE', 'admin/database/use')).toBe(1))
  })

  it('shows the plain reason when the move is refused', async () => {
    signedIn()
    mockApi(routes({ 'POST /admin/database/test': () => tested(), 'POST /admin/database/move': () => problem(422, 'That is the server the app uses now. Enter the new server.', undefined, { code: 'same_database' }) }))
    app('/admin/database')
    const user = userEvent.setup()
    await fill(user)
    await user.click(screen.getByRole('button', { name: /Test the connection/ }))
    await screen.findByText(/does not exist yet/)
    await user.click(screen.getByRole('button', { name: /Make the tables and copy the data/ }))
    expect(await screen.findByText(/That is the server the app uses now/)).toBeInTheDocument()
  })
})
