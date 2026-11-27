import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { cleanText, composePreview, plainTextOf, textSection } from './components/ChooseContent'
import { clearOutbox } from './offline/outbox'
import { app, confirmPdf, contentDto, flow, outline, signedIn, standard } from './test/fixtures'
import { mockApi } from './test/mockApi'

vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox(); localStorage.clear() })

const now = () => new Date().toISOString()
const job = (over: object = {}) => ({ jobId: 'j-1', kind: 'GuideDocument', state: 'Queued', percent: null, message: null, resultId: null, createdAt: now(), ...over })
const analysed = { ...flow, currentVersionNo: 1 }
const NONE = '00000000-0000-0000-0000-000000000000'

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
const section = (key: string, title: string, body: string, flowId = NONE) => ({ key, title, detail: '', parts: [{ flowId, html: `<section><h2><span>\u0001</span>${title}</h2>${body}</section>` }] })

describe('the text of a section as lines to start from', () => {
  it('turns paragraphs, lists, table rows and labels into lines, and leaves out the heading, the number and pictures', () => {
    const html = `<section><h2><span>\u0001</span>Scope</h2><p>What is  covered.</p><div class="two"><div class="panel"><div class="label">Covered</div><ul><li>Bed screen</li><li>Ward list</li></ul></div></div>`
      + `<table><tr><td>Admin</td><td>Sets up<br>the master</td></tr></table><img src="x.png"><figcaption>Figure 1</figcaption></section>`
    expect(plainTextOf(html)).toBe('What is covered.\nCovered\n- Bed screen\n- Ward list\nAdmin – Sets up the master\nFigure 1')
  })
  it('is tidied the way the server tidies the written text', () => {
    expect(cleanText('  First   line \r\n\r\n\r\n- a  bullet \n')).toBe('First line\n- a bullet')
  })
})

describe('the preview of a document with text the person wrote', () => {
  const data = { sections: [section('purpose', 'Purpose', '<p>Generated purpose.</p>'), section('scope', 'Scope', '<p>Generated scope.</p>'), section('diagram', 'The flow at a glance', '<p>drawing</p>')], requests: [], style: '', cover: '<div>cover</div>' }

  it('prints their lines as paragraphs and bullets under the same heading and number, and leaves the other sections alone', () => {
    const html = composePreview(data, new Set(['purpose', 'scope']), new Set(), {}, { purpose: 'We want one screen.\n- Faster\n- Fewer calls\nDone.' })
    expect(html).toContain('<h2><span>1</span>Purpose</h2><p>We want one screen.</p><ul><li>Faster</li><li>Fewer calls</li></ul><p>Done.</p></section>')
    expect(html).not.toContain('Generated purpose.')
    expect(html).toContain('<h2><span>2</span>Scope</h2><p>Generated scope.</p>')
  })
  it('works together with a renamed heading, writes the text safely and ignores text for a section that cannot be edited', () => {
    const html = composePreview(data, new Set(['purpose', 'diagram']), new Set(), { purpose: 'Aim' }, { purpose: '<b>Why</b> & "$&"', diagram: 'Not allowed' })
    expect(html).toContain('<h2><span>1</span>Aim</h2><p>&lt;b&gt;Why&lt;/b&gt; &amp; &quot;$&amp;&quot;</p>')
    expect(html).not.toContain('<b>Why</b>')
    expect(html).not.toContain('Not allowed')
    expect(html).toContain('drawing')
  })
  it('writes the same markup as the server does for the PDF', () => {
    expect(textSection('Purpose', 'One\n- a\n- b\nTwo')).toBe('<section><h2><span>\u0001</span>Purpose</h2><p>One</p><ul><li>a</li><li>b</li></ul><p>Two</p></section>')
  })
  it('uses the text of each request in a project or module PDF', () => {
    const a = 'aaaaaaaa-0000-0000-0000-000000000001', b = 'bbbbbbbb-0000-0000-0000-000000000002'
    const big = { sections: [{ key: 'purpose', title: 'Purpose', detail: '', parts: [{ flowId: a, html: '<section><h2><span>\u0001</span>Purpose</h2><p>A generated</p></section>' }, { flowId: b, html: '<section><h2><span>\u0001</span>Purpose</h2><p>B generated</p></section>' }] }],
      requests: [{ flowId: a, code: 'NR-001', title: 'Bed allocation', module: 'Ward' }, { flowId: b, code: 'NR-002', title: 'Bed transfer', module: 'Ward' }], style: '', cover: '' }
    const html = composePreview(big, new Set(['purpose']), new Set([a, b]), {}, { [`${a}/purpose`]: 'Only for A' })
    expect(html).toContain('Only for A')
    expect(html).toContain('B generated')
    expect(html).not.toContain('A generated')
  })
})

describe('writing the text under a heading before a document is made', () => {
  it('starts from the generated text, shows the new text in the document at once, and sends it with the ticked headings', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    expect(within(list).getAllByRole('button', { name: /^Edit the text under/ })).toHaveLength(3)          // purpose, requirements and stages: not the document control or the sign-off
    expect(within(list).queryByRole('button', { name: 'Edit the text under Document control' })).not.toBeInTheDocument()
    expect(within(list).queryByRole('button', { name: 'Edit the text under Approval and sign-off' })).not.toBeInTheDocument()

    await user.click(within(list).getByRole('button', { name: 'Edit the text under Purpose' }))
    const box = screen.getByRole('textbox', { name: 'Text under Purpose' })
    expect(box).toHaveValue('Show the default ward to a super user.')
    await user.clear(box)
    await user.type(box, 'Super users see their own ward first.{Enter}- No more searching{Enter}- Fewer mistakes')
    await user.click(screen.getByRole('button', { name: 'Save text' }))

    expect(screen.queryByRole('textbox', { name: 'Text under Purpose' })).not.toBeInTheDocument()
    expect(within(list).getByText('(text edited)')).toBeInTheDocument()
    expect(srcdoc()).toContain('<h2><span>2</span>Purpose</h2><p>Super users see their own ward first.</p><ul><li>No more searching</li><li>Fewer mistakes</li></ul>')
    expect(srcdoc()).not.toContain('Show the default ward to a super user.')
    expect(screen.getByText(/1 text written by you/)).toBeInTheDocument()

    await confirmPdf(user)
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'flows/f-1/guide')?.body).toEqual({ texts: { purpose: 'Super users see their own ward first.\n- No more searching\n- Fewer mistakes' } }))
  })

  it('goes back to the generated text, and sends nothing when the text is the generated one again', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    await user.click(within(list).getByRole('button', { name: 'Edit the text under Purpose' }))
    const box = screen.getByRole('textbox', { name: 'Text under Purpose' })
    await user.clear(box); await user.type(box, 'Mine')
    await user.click(screen.getByRole('button', { name: 'Save text' }))
    expect(srcdoc()).toContain('<p>Mine</p>')

    await user.click(within(list).getByRole('button', { name: 'Use the generated text under Purpose' }))
    expect(srcdoc()).toContain('Show the default ward to a super user.')
    expect(within(list).queryByText('(text edited)')).not.toBeInTheDocument()

    await user.click(within(list).getByRole('button', { name: 'Edit the text under Purpose' }))
    await user.clear(screen.getByRole('textbox', { name: 'Text under Purpose' }))                    // empty text keeps the generated text
    await user.click(screen.getByRole('button', { name: 'Save text' }))
    await confirmPdf(user)
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'flows/f-1/guide')?.body).toEqual({}))
  })

  it('can be cancelled, and the button in the editor puts the generated text back in the box', async () => {
    signedIn(); mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    await user.click(within(list).getByRole('button', { name: 'Edit the text under Purpose' }))
    const box = screen.getByRole('textbox', { name: 'Text under Purpose' })
    await user.clear(box); await user.type(box, 'Half written')
    await user.click(screen.getByRole('button', { name: 'Use the generated text' }))
    expect(box).toHaveValue('Show the default ward to a super user.')
    await user.type(box, ' More')
    await user.click(within(box.closest('form')!).getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('textbox', { name: 'Text under Purpose' })).not.toBeInTheDocument()
    expect(srcdoc()).not.toContain('More')
    expect(within(list).queryByText('(text edited)')).not.toBeInTheDocument()
  })

  it('does not use text written over generated text that has changed since, and says so', async () => {
    signedIn()
    let purpose = 'Show the default ward to a super user.'
    const o = () => { const x = outline(); x.sections = x.sections.map(s => (s.key === 'purpose' ? { ...s, parts: s.parts.map(p => ({ ...p, html: p.html.replace('Show the default ward to a super user.', purpose) })) } : s)); return x }
    mockApi(single({ 'GET /flows/f-1/guide-outline': o })); app('/p/p-1')
    const user = userEvent.setup()
    let list = await open(user)
    await user.click(within(list).getByRole('button', { name: 'Edit the text under Purpose' }))
    const box = screen.getByRole('textbox', { name: 'Text under Purpose' })
    await user.clear(box); await user.type(box, 'My old words')
    await user.click(screen.getByRole('button', { name: 'Save text' }))
    expect(srcdoc()).toContain('My old words')
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    purpose = 'The notes were analysed again and this is new.'                                          // the generated text is now different
    list = await open(user)
    expect(srcdoc()).toContain('The notes were analysed again and this is new.')
    expect(srcdoc()).not.toContain('My old words')
    expect(await screen.findByText(/1 of your edited texts was not used/)).toBeInTheDocument()
  })

  it('does not send the text of a heading that is not ticked', async () => {
    signedIn(); const server = mockApi(single()); app('/p/p-1')
    const user = userEvent.setup()
    const list = await open(user)
    await user.click(within(list).getByRole('button', { name: 'Edit the text under How it works, stage by stage' }))
    const box = screen.getByRole('textbox', { name: 'Text under How it works, stage by stage' })
    await user.clear(box); await user.type(box, 'Hand-written stages')
    await user.click(screen.getByRole('button', { name: 'Save text' }))
    await user.click(within(list).getByRole('checkbox', { name: /How it works, stage by stage/ }))      // untick it
    await confirmPdf(user)
    await waitFor(() => expect(server.calls.find(c => c.method === 'POST' && c.path === 'flows/f-1/guide')?.body).toEqual({ sections: ['control', 'purpose', 'requirements', 'signoff'] }))
  })
})
