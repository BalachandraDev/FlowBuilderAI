import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { captionFromName } from './components/FigurePanel'
import { clearOutbox } from './offline/outbox'
import { app, contentDto, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const figure = (over: object = {}) => ({ id: 'fig-1', flowId: 'f-1', caption: 'The receive menu', stepId: null, sortOrder: 1, contentType: 'image/png', sizeBytes: 20480, originalName: 'menu.png', uploadedByName: 'Asha Lead', createdAt: new Date().toISOString(), ...over })
const png = (name: string) => new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], name, { type: 'image/png' })
const routes = (over: Record<string, () => object | undefined> = {}) => ({
  ...standard, 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
  'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
  'GET /figures/fig-1/image': () => new Response(new Uint8Array([1]), { status: 200, headers: { 'Content-Type': 'image/png' } }),
  ...over,
})

describe('pictures for the document', () => {
  it('turns a file name into a caption to start from', () => {
    expect(captionFromName('screen_shot-12.PNG')).toBe('screen shot 12')
    expect(captionFromName('Bed allocation.jpeg')).toBe('Bed allocation')
  })

  it('attaches several pictures at once, each with a caption taken from its name', async () => {
    signedIn()
    const server = mockApi(routes({ 'GET /flows/f-1/figures': () => [], 'POST /flows/f-1/figures': () => figure() }))
    app('/p/p-1')
    expect(await screen.findByRole('heading', { name: /Pictures for the document/ })).toBeInTheDocument()
    expect(await screen.findByText('No pictures yet')).toBeInTheDocument()
    const input = screen.getByLabelText('Choose pictures to attach') as HTMLInputElement
    expect(input.multiple).toBe(true)
    await userEvent.setup().upload(input, [png('Receive menu.png'), png('ward_list.png')])
    await waitFor(() => expect(server.count('POST', 'flows/f-1/figures')).toBe(2))
    expect(await screen.findByText('2 pictures were attached.')).toBeInTheDocument()
  })

  it('names the picture that could not be attached and keeps going', async () => {
    signedIn()
    const server = mockApi(routes({ 'GET /flows/f-1/figures': () => [], 'POST /flows/f-1/figures': () => problem(422, 'The request cannot be completed.', 'Attach a JPEG, PNG or WebP picture.') }))
    app('/p/p-1')
    await screen.findByText('No pictures yet')
    await userEvent.setup().upload(screen.getByLabelText('Choose pictures to attach'), [png('a.png'), png('b.png')])
    expect(await screen.findByText(/a\.png: Attach a JPEG, PNG or WebP picture\./)).toBeInTheDocument()
    expect(await screen.findByText(/b\.png: Attach a JPEG, PNG or WebP picture\./)).toBeInTheDocument()
    expect(server.count('POST', 'flows/f-1/figures')).toBe(2)
  })

  it('lets the writer correct the caption, tie the picture to a step, move it and remove it after asking', async () => {
    signedIn()
    let list = [figure(), figure({ id: 'fig-2', caption: 'The ward list', sortOrder: 2 })]
    const server = mockApi(routes({
      'GET /flows/f-1/figures': () => list,
      'PUT /figures/fig-1': () => figure({ caption: 'The receive menu, new', stepId: 'S1' }),
      'POST /figures/fig-1/move': () => (list = [list[1], list[0]]),
      'DELETE /figures/fig-2': () => { list = [list[0]]; return undefined },
    }))
    app('/p/p-1')
    const user = userEvent.setup()
    const caption = await screen.findByLabelText('Caption of figure 1')
    await user.clear(caption); await user.type(caption, 'The receive menu, new'); await user.tab()
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'figures/fig-1')?.body).toMatchObject({ caption: 'The receive menu, new' }))

    const step = screen.getByLabelText('Step of figure 1')
    expect(within(step).getByRole('option', { name: /^1\./ })).toBeInTheDocument()            // the steps of the analysed workflow are offered
    await user.selectOptions(step, 'S1')
    await waitFor(() => expect(server.calls.filter(c => c.method === 'PUT' && c.path === 'figures/fig-1').at(-1)?.body).toMatchObject({ stepId: 'S1' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Move figure 1 later' })).toBeEnabled())          // the save has finished
    await user.click(screen.getByRole('button', { name: 'Move figure 1 later' }))
    await waitFor(() => expect(server.count('POST', 'figures/fig-1/move')).toBe(1))

    // after the move the ward list (fig-2) is figure 1
    await user.click(await screen.findByRole('button', { name: /Remove figure 1/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel' }))
    expect(server.count('DELETE', 'figures/fig-2')).toBe(0)
    await user.click(screen.getByRole('button', { name: /Remove figure 1/ }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(server.count('DELETE', 'figures/fig-2')).toBe(1))
  })

  it('shows a reviewer the pictures and their captions but no way to change them', async () => {
    signedIn()
    mockApi(routes({ 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: 'Reviewer' }] }), 'GET /flows/f-1/figures': () => [figure({ stepId: 'S1' })] }))
    app('/p/p-1')
    expect(await screen.findByText('The receive menu')).toBeInTheDocument()
    expect(screen.queryByLabelText('Choose pictures to attach')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Caption of figure 1')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Remove figure/ })).not.toBeInTheDocument()
  })
})