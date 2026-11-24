import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { composePreview } from './components/ChooseContent'
import { clearOutbox } from './offline/outbox'
import { app, contentDto, flow, outline, signedIn, standard } from './test/fixtures'
import { mockApi } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox(); localStorage.clear() })

const now = () => new Date().toISOString()
const job = (over: object = {}) => ({ jobId: 'j-1', kind: 'GuideDocument', state: 'Queued', percent: null, message: null, resultId: null, createdAt: now(), ...over })
const analysed = { ...flow, currentVersionNo: 1 }

function single(over: Record<string, unknown> = {}) {
  return {
    ...standard, 'GET /projects/p-1/flows': () => [analysed], 'GET /flows/f-1': () => analysed, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [],
    'GET /flows/f-1/documents': () => [], 'GET /flows/f-1/content': () => contentDto(),
    'GET /flows/f-1/diagram': () => new Response('<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"/>', { status: 200, headers: { 'Content-Type': 'image/svg+xml' } }),
    'POST /flows/f-1/guide': () => job(), 'GET /jobs/j-1': () => job({ state: 'Running' }), ...over,
  }
}
const open = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(await screen.findByRole('button', { name: 'Create the implementation document' }))
  return screen.findByRole('list', { name: 'Headings' })
}
const srcdoc = () => (screen.getByTitle('The document as it will print') as HTMLIFrameElement).getAttribute('srcdoc') ?? ''

describe('the preview of a document with renamed headings', () => {
  const data = {
    sections: [
      { key: 'purpose', title: 'Purpose', detail: 'What the flow is for', parts: [{ flowId: '00000000-0000-0000-0000-000000000000', html: '<section><h2><span>\u0001</span>Purpose</h2><p>Why.</p></section>' }] },
      { key: 'scope', title: 'Scope', detail: 'What is covered', parts: [{ flowId: '00000000-0000-0000-0000-000000000000', html: '<section><h2><span>\u0001</span>Scope</h2><p>What.</p></section>' }] },
    ],
    requests: [], style: '', cover: '<div>cover</div>',
  }
  it('prints the new wording where the standard heading was, keeping the numbers', () => {
    const html = composePreview(data, new Set(['purpose', 'scope']), new Set(), { scope: 'What this covers' })
    expect(html).toContain('<h2><span>1</span>Purpose</h2>')
    expect(html).toContain('<h2><span>2</span>What this covers</h2>')
    expect(html).not.toContain('>Scope<')
  })
  it('writes the new wording safely, ignores spaces around it, and does not read $ as a pattern', () => {
    const html = composePreview(data, new Set(['purpose']), new Set(), { purpose: '  <b>Why</b>   & "$&" $1  ' })
    expect(html).toContain('<h2><span>1</span>&lt;b&gt;Why&lt;/b&gt; &amp; &quot;$&amp;&quot; $1</h2>')
    expect(html).not.toContain('<b>Why</b>')
  })
  it('keeps the standard heading when the new one is empty or the same', () => {
    expect(composePreview(data, new Set(['purpose']), new Set(), { purpose: '   ' })).toContain('<h2><span>1</span>Purpose</h2>')
    expect(composePreview(data, new Set(['purpose']), new Set(), { purpose: 'Purpose' })).toContain('<h2><span>1</span>Purpose</h2>')
  })
})

describe('renaming the headings of a document before it is made', () => {
  it('renames a heading with the pencil, shows it in the document at once, and sends it with the ticked headings', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    expect(within(list).getAllByRole('button', { name: /^Rename the heading/ })).toHaveLength(outline().sections.length)

    await user.click(within(list).getByRole('button', { name: 'Rename the heading Purpose' }))
    const box = screen.getByRole('textbox', { name: 'Heading for Purpose' })
    expect(box).toHaveValue('Purpose')
    await user.clear(box)
    await user.type(box, 'Why this is needed{Enter}')

    expect(screen.queryByRole('textbox', { name: 'Heading for Purpose' })).not.toBeInTheDocument()
    expect(within(list).getByText('Why this is needed')).toBeInTheDocument()
    expect(within(list).getByText('(renamed)')).toBeInTheDocument()
    expect(within(list).getByText('Standard heading: Purpose')).toBeInTheDocument()
    expect(srcdoc()).toContain('</span>Why this is needed</h2>')
    expect(srcdoc()).not.toContain('</span>Purpose</h2>')
    expect(screen.getByText(/1 heading renamed/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Make the PDF' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'flows/f-1/guide')?.body).toEqual({ titles: { purpose: 'Why this is needed' } }))   // everything ticked: only the renamed heading is sent
  })

  it('does not send the new name of a heading that is not ticked, and keeps the ticked list', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    await user.click(within(list).getByRole('button', { name: 'Rename the heading Purpose' }))
    await user.type(screen.getByRole('textbox', { name: 'Heading for Purpose' }), ' and aim{Enter}')
    await user.click(within(list).getByRole('button', { name: 'Rename the heading Functional requirements' }))
    await user.clear(screen.getByRole('textbox', { name: 'Heading for Functional requirements' }))
    await user.type(screen.getByRole('textbox', { name: 'Heading for Functional requirements' }), 'What it must do{Enter}')
    await user.click(screen.getByRole('checkbox', { name: /Purpose/ }))                          // Purpose is not printed any more
    await user.click(screen.getByRole('button', { name: 'Make the PDF' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'flows/f-1/guide')?.body).toEqual({
      sections: ['control', 'requirements', 'stages', 'signoff'], titles: { requirements: 'What it must do' },
    }))
  })

  it('cancels with Escape or Cancel, will not save an empty heading, and keeps the dialog open on Escape', async () => {
    signedIn(); mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    await user.click(within(list).getByRole('button', { name: 'Rename the heading Purpose' }))
    await user.type(screen.getByRole('textbox', { name: 'Heading for Purpose' }), ' changed{Escape}')
    expect(screen.queryByRole('textbox', { name: 'Heading for Purpose' })).not.toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()                                       // only the box closed, not the window
    expect(within(list).queryByText('(renamed)')).not.toBeInTheDocument()

    await user.click(within(list).getByRole('button', { name: 'Rename the heading Purpose' }))
    await user.clear(screen.getByRole('textbox', { name: 'Heading for Purpose' }))
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
    expect(screen.getByText(/Type a heading, or press Cancel/)).toBeInTheDocument()
    await user.click(within(list).getByRole('button', { name: 'Cancel' }))                       // the Cancel of the box, not the window's
    expect(screen.queryByRole('textbox', { name: 'Heading for Purpose' })).not.toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(within(list).queryByText('(renamed)')).not.toBeInTheDocument()
  })

  it('goes back to the standard heading, one at a time or all together', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    for (const [from, to] of [['Purpose', 'Aim'], ['Functional requirements', 'Needs']]) {
      await user.click(within(list).getByRole('button', { name: `Rename the heading ${from}` }))
      await user.clear(screen.getByRole('textbox', { name: `Heading for ${from}` }))
      await user.type(screen.getByRole('textbox', { name: `Heading for ${from}` }), `${to}{Enter}`)
    }
    expect(screen.getByText(/2 headings renamed/)).toBeInTheDocument()
    await user.click(within(list).getByRole('button', { name: 'Use the standard heading Purpose' }))
    expect(within(list).queryByText('Aim')).not.toBeInTheDocument()
    expect(within(list).getByText('Needs')).toBeInTheDocument()
    expect(srcdoc()).toContain('</span>Purpose</h2>')
    await user.click(screen.getByRole('button', { name: 'Use the standard headings' }))
    expect(within(list).queryByText('(renamed)')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Make the PDF' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'flows/f-1/guide')?.body).toEqual({}))      // the complete, standard document
  })

  it('remembers the wording for the next PDF of the same document, and a renamed heading written back as the standard one is dropped', async () => {
    signedIn(); mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    let list = await open(user)
    await user.click(within(list).getByRole('button', { name: 'Rename the heading Purpose' }))
    await user.clear(screen.getByRole('textbox', { name: 'Heading for Purpose' }))
    await user.type(screen.getByRole('textbox', { name: 'Heading for Purpose' }), 'Aim{Enter}')
    await user.click(within(screen.getByRole('dialog')).getAllByRole('button', { name: 'Cancel' }).at(-1)!)        // the window's own Cancel (the last one): nothing is made
    list = await open(user)
    expect(within(list).getByText('Aim')).toBeInTheDocument()                                    // still there the next time
    expect(srcdoc()).toContain('</span>Aim</h2>')
    await user.click(within(list).getByRole('button', { name: 'Rename the heading Aim' }))
    await user.clear(screen.getByRole('textbox', { name: 'Heading for Aim' }))
    await user.type(screen.getByRole('textbox', { name: 'Heading for Aim' }), 'Purpose{Enter}')   // typing the standard wording is the same as resetting
    expect(within(list).queryByText('(renamed)')).not.toBeInTheDocument()
  })
})
