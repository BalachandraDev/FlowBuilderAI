import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from './api/client'
import { clearOutbox, enqueueEntry } from './offline/outbox'
import { app, contentDto, entry, flow, signedIn, standard } from './test/fixtures'
import { mockApi, problem, tokens } from './test/mockApi'

// the diagram library is large and needs a real browser; the screens only hand it the text
vi.mock('./components/MermaidView', () => ({ MermaidView: ({ source }: { source: string }) => <div data-testid="diagram">{source}</div> }))

beforeEach(async () => { vi.unstubAllGlobals(); auth.clear(); await clearOutbox() })

const as = (role: string) => ({ 'POST /auth/refresh': () => tokens({ projects: [{ projectId: 'p-1', projectCode: 'KMC', projectRole: role }] }) })
const run = (status: string, over: object = {}) => ({ id: 'r-1', scope: 'Part', mode: 'Initial', status, flowId: 'f-1', model: 'm', inputTokens: 1, outputTokens: 1, durationMs: 1, attempts: 1, errorCode: null, errorMessage: null, resultVersionNo: status === 'Succeeded' ? 1 : null, warnings: [], startedAt: null, finishedAt: null, createdAt: new Date().toISOString(), ...over })
const question = (over: object = {}) => ({ id: 'q-1', flowId: 'f-1', questionKey: 'Q1', section: 'businessRules', question: 'Which ward is pre-selected?', options: ['First ward', 'No pre-selection'], status: 'Open', isBlocking: false, answer: null, rowVersion: 'v1', ...over })

describe('analysis', () => {
  const fresh = { ...flow, status: 'Draft', currentVersionNo: 0, openQuestionCount: 0 }

  it('keeps review and versions closed until there is a version, and explains the first analysis', async () => {
    signedIn(); mockApi({ ...standard, 'GET /flows/f-1': () => fresh, 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [] })
    app('/flow/f-1?tab=analysis')
    expect(await screen.findByRole('heading', { name: 'Analyse the captured input' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Review and edit' })).toBeDisabled()
    expect(screen.getByRole('tab', { name: 'Versions' })).toBeDisabled()
    expect(await screen.findByText('Questions appear after the first analysis.')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Run analysis' })).toBeEnabled()
  })

  it('closes a review tab opened from a link when there is nothing to review yet', async () => {
    signedIn(); mockApi({ ...standard, 'GET /flows/f-1': () => fresh })
    app('/flow/f-1?tab=review')
    await waitFor(() => expect(screen.getByRole('tab', { name: 'Notes' })).toHaveAttribute('aria-selected', 'true'))
  })

  it('starts the analysis once the notes are on the server, then announces the version it made', async () => {
    signedIn()
    let finished = false
    const server = mockApi({
      ...standard, 'GET /flows/f-1': () => fresh, 'GET /flows/f-1/clarifications': () => [],
      'GET /flows/f-1/analyses': () => (finished ? [run('Succeeded')] : []),
      'POST /flows/f-1/analyses': () => { finished = true; return run('Queued') },
    })
    app('/flow/f-1?tab=analysis')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Run analysis' }))
    await waitFor(() => expect(server.count('POST', 'flows/f-1/analyses')).toBe(1))
    const call = server.calls.find(c => c.path === 'flows/f-1/analyses' && c.method === 'POST')!
    expect(call.body).toEqual({ mode: 'Initial', entryIds: null })
    expect(call.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/)           // pressing twice cannot start two analyses
    expect(await screen.findByText('Done: version 1 created.')).toBeInTheDocument()
  })

  it('analyses again with the mode "Reanalyse" when a version already exists', async () => {
    signedIn()
    const server = mockApi({ ...standard, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [], 'POST /flows/f-1/analyses': () => run('Queued', { mode: 'Reanalyse' }) })
    app('/flow/f-1?tab=analysis')
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Analyse again' }))
    await waitFor(() => expect(server.calls.find(c => c.path === 'flows/f-1/analyses' && c.method === 'POST')?.body).toMatchObject({ mode: 'Reanalyse' }))
  })

  it('explains that nothing is new instead of spending free AI tokens, and analyses anyway only when asked', async () => {
    signedIn()
    let forced = false
    const server = mockApi({ ...standard, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [],
      'POST /flows/f-1/analyses': () => forced ? run('Queued', { mode: 'Reanalyse' }) : problem(422, 'The request cannot be completed.', 'Nothing has been added, changed or answered since version 1, so analysing again would use the free AI tokens for the same result.', { code: 'nothing_new' }) })
    app('/flow/f-1?tab=analysis')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Analyse again' }))
    expect(await screen.findByText(/Nothing has been added, changed or answered since version 1/)).toBeInTheDocument()
    expect(server.calls.find(c => c.method === 'POST')!.body).not.toHaveProperty('force')
    forced = true
    await user.click(screen.getByRole('button', { name: 'Analyse anyway' }))
    await waitFor(() => expect(server.calls.filter(c => c.method === 'POST' && c.path === 'flows/f-1/analyses').at(-1)?.body).toMatchObject({ mode: 'Reanalyse', force: true }))
  })
  it('will not start while notes are still waiting on the device, or when there is no input, and says why', async () => {
    signedIn()
    mockApi({ ...standard, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/entries': () => [], 'POST /flows/f-1/entries': () => { throw new TypeError('Failed to fetch') } })
    app('/flow/f-1?tab=analysis')
    expect(await screen.findByText('Add some input first.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Analyse again' })).toBeDisabled()

    await enqueueEntry('f-1', { id: 'n-1', kind: 'Text', capturedAt: new Date().toISOString(), language: 'en-IN', rawText: 'still on this phone', notes: null, intervieweeName: null, intervieweeRole: null })
    expect(await screen.findByText(/1 note has not reached the server yet/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Analyse again' })).toBeDisabled()
  })

  it('shows why an analysis failed, with its warnings, and lets a running one be cancelled', async () => {
    signedIn()
    const server = mockApi({
      ...standard, 'GET /flows/f-1/clarifications': () => [],
      'GET /flows/f-1/analyses': () => [run('Failed', { errorMessage: 'The monthly AI limit has been reached.', warnings: ['One source reference was removed.'] })],
    })
    const first = app('/flow/f-1?tab=analysis')
    expect(await screen.findByText(/did not finish: The monthly AI limit has been reached\./)).toBeInTheDocument()
    expect(screen.getByText('One source reference was removed.')).toBeInTheDocument()
    expect(server.count('POST', 'analysis-runs/r-1/cancel')).toBe(0)
    first.unmount()

    const second = mockApi({ ...standard, 'GET /flows/f-1/clarifications': () => [], 'GET /flows/f-1/analyses': () => [run('Running')], 'POST /analysis-runs/r-1/cancel': () => run('Cancelled') })
    app('/flow/f-1?tab=analysis')
    expect(await screen.findByText(/The AI is working/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Analysing…' })).toBeDisabled()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(second.count('POST', 'analysis-runs/r-1/cancel')).toBe(1))
  })

  it('shows the open questions, fills the answer from a suggestion and saves it', async () => {
    signedIn()
    const server = mockApi({ ...standard, 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [question()], 'PUT /clarifications/q-1': () => question({ status: 'Answered', answer: 'First ward' }) })
    app('/flow/f-1?tab=analysis')
    expect(await screen.findByRole('heading', { name: 'Questions to answer (1 open)' })).toBeInTheDocument()
    const user = userEvent.setup()
    expect(screen.getByRole('button', { name: 'Save answer' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'First ward' }))
    expect(screen.getByLabelText('Your answer')).toHaveValue('First ward')
    await user.click(screen.getByRole('button', { name: 'Save answer' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT' && c.path === 'clarifications/q-1')?.body).toEqual({ answer: 'First ward' }))
    expect(await screen.findByText('Answer saved.')).toBeInTheDocument()
  })

  it('lets the lead mark a question blocking or dismiss it with a reason, and not an executive', async () => {
    signedIn()
    const server = mockApi({ ...standard, 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [question()], 'PUT /clarifications/q-1': () => question() })
    app('/flow/f-1?tab=analysis')
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Mark blocking' }))
    await waitFor(() => expect(server.calls.find(c => c.method === 'PUT')?.body).toEqual({ isBlocking: true }))
    await user.click(screen.getByRole('button', { name: 'Dismiss' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('button', { name: 'Dismiss' })).toBeDisabled()                       // a reason is required
    await user.type(within(dialog).getByLabelText('Why is this not needed?'), 'Already decided.')
    await user.click(within(dialog).getByRole('button', { name: 'Dismiss' }))
    await waitFor(() => expect(server.calls.filter(c => c.method === 'PUT').at(-1)?.body).toEqual({ status: 'Dismissed', dismissReason: 'Already decided.' }))
  })

  it('gives an executive the answer box but not the lead\'s buttons, and a reviewer neither', async () => {
    signedIn()
    mockApi({ ...standard, ...as('Executive'), 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [question()] })
    const first = app('/flow/f-1?tab=analysis')
    expect(await screen.findByLabelText('Your answer')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Dismiss' })).not.toBeInTheDocument()
    first.unmount()

    mockApi({ ...standard, ...as('Reviewer'), 'GET /flows/f-1/analyses': () => [], 'GET /flows/f-1/clarifications': () => [question({ status: 'Answered', answer: 'First ward' })] })
    app('/flow/f-1?tab=analysis')
    expect(await screen.findByText('First ward', { exact: false })).toBeInTheDocument()
    expect(screen.queryByLabelText('Your answer')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Analyse again' })).toBeDisabled()
  })
})

describe('review', () => {
  const review = (over: Record<string, unknown> = {}, flowOver: object = {}) => ({
    ...standard, 'GET /flows/f-1': () => ({ ...flow, ...flowOver }), 'GET /flows/f-1/content': () => contentDto(), 'GET /master/requirement-categories': () => [{ code: 'CR', isActive: true }, { code: 'NR', isActive: true }], ...over,
  })

  async function openSteps() {
    const user = userEvent.setup()
    await user.click((await screen.findByText(/^Steps \(2\)/)).closest('summary')!)
    return user
  }

  it('shows the version, the diagram, and every section with its count, but not the parts handled elsewhere', async () => {
    signedIn(); mockApi(review()); app('/flow/f-1?tab=review')
    expect(await screen.findByText(/Version 1 \(analysis\) by System/)).toBeInTheDocument()
    expect(screen.getByTestId('diagram')).toHaveTextContent('Opens the menu')
    for (const s of ['Steps (2)', 'Business rules (1)', 'Requirements (1)', 'Actors (0)', 'Decisions (0)']) expect(screen.getByText(s)).toBeInTheDocument()
    expect(screen.queryByText('hidden here')).not.toBeInTheDocument()              // the questions live on the Analysis tab
    expect(screen.queryByText('Confidence')).not.toBeInTheDocument()
  })

  it('edits any field, tells you there is something to save, and saves it as a new version with your note', async () => {
    signedIn()
    const server = mockApi(review({ 'PUT /flows/f-1/content': () => contentDto(2) }))
    app('/flow/f-1?tab=review')
    const user = await openSteps()
    expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument()
    const action = screen.getAllByLabelText('Action')[1]
    await user.clear(action)
    await user.type(action, 'System pre-selects the ward.')
    await user.type(screen.getByLabelText('What did you change? (optional)'), 'Clarified step 2')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(server.count('PUT', 'flows/f-1/content')).toBe(1))
    const sent = server.calls.find(c => c.method === 'PUT')!.body as { baseVersionNo: number; changeNote: string; lockedIds: string[]; content: { steps: { action: string }[] } }
    expect(sent).toMatchObject({ baseVersionNo: 1, changeNote: 'Clarified step 2', lockedIds: [] })
    expect(sent.content.steps[1].action).toBe('System pre-selects the ward.')
    expect(await screen.findByText('Saved as version 2.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument()
  })

  it('discards unsaved edits without sending anything', async () => {
    signedIn(); const server = mockApi(review()); app('/flow/f-1?tab=review')
    const user = await openSteps()
    await user.type(screen.getAllByLabelText('Action')[0], ' changed')
    await user.click(screen.getByRole('button', { name: 'Discard' }))
    await waitFor(() => expect(screen.getAllByLabelText('Action')[0]).toHaveValue('Opens the menu.'))
    expect(server.count('PUT', 'flows/f-1/content')).toBe(0)
  })

  it('locks an item so re-analysis leaves it alone, and sends the lock with the save', async () => {
    signedIn()
    let saved = false                                                                                  // after the save the server returns the new version, as the real one does
    const server = mockApi(review({
      'GET /flows/f-1/content': () => (saved ? contentDto(2, { lockedIds: ['S2'] }) : contentDto()),
      'PUT /flows/f-1/content': () => { saved = true; return contentDto(2, { lockedIds: ['S2'] }) },
    }))
    app('/flow/f-1?tab=review')
    const user = await openSteps()
    await user.click(screen.getByRole('button', { name: 'Lock S2 so re-analysis cannot change it' }))
    expect(screen.getByText('S2 · locked')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Unlock S2' })).toHaveAttribute('aria-pressed', 'true')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))                        // a lock alone is a change
    await waitFor(() => expect((server.calls.find(c => c.method === 'PUT')!.body as { lockedIds: string[] }).lockedIds).toEqual(['S2']))
    expect(await screen.findByText('S2 · locked')).toBeInTheDocument()
  })

  it('shows locks that were saved earlier', async () => {
    signedIn(); mockApi(review({ 'GET /flows/f-1/content': () => contentDto(3, { lockedIds: ['BR1'] }) })); app('/flow/f-1?tab=review')
    await userEvent.setup().click((await screen.findByText(/^Business rules \(1\)/)).closest('summary')!)
    expect(screen.getByText('BR1 · locked')).toBeInTheDocument()
  })

  it('turns a cleared field into "To be confirmed" before sending, because the server refuses blank text', async () => {
    signedIn(); const server = mockApi(review({ 'PUT /flows/f-1/content': () => contentDto(2) })); app('/flow/f-1?tab=review')
    const user = await openSteps()
    await user.clear(screen.getAllByLabelText('Screen')[0])
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(server.count('PUT', 'flows/f-1/content')).toBe(1))
    expect((server.calls.find(c => c.method === 'PUT')!.body as { content: { steps: { screen: string }[] } }).content.steps[0].screen).toBe('To be confirmed')
  })

  it('adds a step with the next free id and placeholders, and removes one', async () => {
    signedIn(); const server = mockApi(review({ 'PUT /flows/f-1/content': () => contentDto(2) })); app('/flow/f-1?tab=review')
    const user = await openSteps()
    await user.click(screen.getByRole('button', { name: 'Add step' }))
    expect(screen.getByText('S3')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Delete S1' }))
    expect(screen.queryByText('S1')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Add step' }))
    expect(screen.getByText('S4')).toBeInTheDocument()                                              // S3 was used, so it is not handed out again
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(server.count('PUT', 'flows/f-1/content')).toBe(1))
    const steps = (server.calls.find(c => c.method === 'PUT')!.body as { content: { steps: { id: string; action: string; next: string[] }[] } }).content.steps
    expect(steps.map(s => s.id)).toEqual(['S2', 'S3', 'S4'])
    expect(steps[1]).toMatchObject({ action: 'To be confirmed', next: ['END'] })
  })

  it('offers fixed answers for coded fields (priority, the server\'s category codes) and keeps an unlisted value', async () => {
    signedIn(); mockApi(review({ 'GET /flows/f-1/content': () => contentDto(1) })); app('/flow/f-1?tab=review')
    const summary = (await screen.findByText(/^Requirements \(1\)/)).closest('summary')!
    await userEvent.setup().click(summary)
    const inSection = within(summary.closest('details')!)                                            // the use case summary has a Priority too
    const category = inSection.getByLabelText('Category') as HTMLSelectElement
    expect([...category.options].map(o => o.value)).toEqual(['CR', 'NR', 'To be confirmed'])
    expect((inSection.getByLabelText('Priority') as HTMLSelectElement).value).toBe('High')
    expect(inSection.getByLabelText('Req id')).toHaveAttribute('readonly')                            // ids are never edited
  })

  it('shows the server\'s reasons when a save is refused, and offers the latest version after a conflict', async () => {
    signedIn()
    const server = mockApi(review({ 'PUT /flows/f-1/content': () => problem(400, 'The request is not valid.', undefined, { errors: { 'steps[0].next': ['Step S1 points to S9, which does not exist.'] } }) }))
    app('/flow/f-1?tab=review')
    const user = await openSteps()
    await user.type(screen.getAllByLabelText('Action')[0], '!')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText('Step S1 points to S9, which does not exist.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled()                       // nothing was lost, try again
    expect(screen.queryByRole('button', { name: 'Reload the latest version' })).not.toBeInTheDocument()

    mockApi(review({ 'PUT /flows/f-1/content': () => problem(409, 'Conflict.', 'The flow is now at version 3, but you edited version 1. Reload and apply your change again.') }))
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText(/The flow is now at version 3/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reload the latest version' })).toBeInTheDocument()
    expect(server.count('PUT', 'flows/f-1/content')).toBe(1)
  })

  it('is read-only for a reviewer: no padlocks, no add or delete, no save', async () => {
    signedIn(); mockApi(review(as('Reviewer'))); app('/flow/f-1?tab=review')
    const user = await openSteps()
    expect(screen.queryByText(/Change anything that is wrong/)).not.toBeInTheDocument()
    expect(screen.getAllByLabelText('Action')[0]).toHaveAttribute('readonly')
    expect(screen.queryByRole('button', { name: /^Add step/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Delete S1/ })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Lock S1/ })).toBeDisabled()
    await user.type(screen.getAllByLabelText('Action')[0], 'x')
    expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument()
  })

  describe('moving the flow forward', () => {
    it('lets an executive submit an analysed flow for review', async () => {
      signedIn(); const server = mockApi(review({ ...as('Executive'), 'POST /flows/f-1/submit-review': () => ({ ...flow, status: 'UnderReview' }) })); app('/flow/f-1?tab=review')
      await userEvent.setup().click(await screen.findByRole('button', { name: 'Submit for review' }))
      await waitFor(() => expect(server.count('POST', 'flows/f-1/submit-review')).toBe(1))
      expect(await screen.findByText('Submitted for review.')).toBeInTheDocument()
    })

    it('lets only the lead confirm, after asking, and sends the version being confirmed', async () => {
      signedIn(); const server = mockApi(review({ 'POST /flows/f-1/confirm': () => ({ ...flow, status: 'Confirmed' }), 'GET /flows/f-1/content': () => contentDto(4) }, { status: 'UnderReview', currentVersionNo: 4 })); app('/flow/f-1?tab=review')
      const user = userEvent.setup()
      await user.click(await screen.findByRole('button', { name: 'Confirm this version' }))
      const dialog = await screen.findByRole('dialog')
      expect(dialog).toHaveTextContent('Version 4 becomes the confirmed version')
      await user.click(within(dialog).getByRole('button', { name: 'Confirm' }))
      await waitFor(() => expect(server.calls.find(c => c.path === 'flows/f-1/confirm')?.body).toEqual({ versionNo: 4 }))
    })

    it('tells an executive to wait for the lead, and gives no confirm button', async () => {
      signedIn(); mockApi(review(as('Executive'), { status: 'UnderReview' })); app('/flow/f-1?tab=review')
      expect(await screen.findByText('Waiting for the Implementation Lead to confirm.')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Confirm this version' })).not.toBeInTheDocument()
    })

    it('lets the lead re-open a confirmed flow, and says which version is confirmed', async () => {
      signedIn(); const server = mockApi(review({ 'POST /flows/f-1/reopen': () => ({ ...flow, status: 'UnderReview' }) }, { status: 'Confirmed', confirmedVersionNo: 1 })); app('/flow/f-1?tab=review')
      expect(await screen.findByText(/confirmed version 1\. Any change moves the flow back to review\./)).toBeInTheDocument()
      await userEvent.setup().click(screen.getByRole('button', { name: 'Re-open for changes' }))
      await waitFor(() => expect(server.count('POST', 'flows/f-1/reopen')).toBe(1))
    })

    it('does not offer to move the flow forward while there are unsaved edits', async () => {
      signedIn(); mockApi(review(as('Executive'))); app('/flow/f-1?tab=review')
      const user = await openSteps()
      expect(screen.getByRole('button', { name: 'Submit for review' })).toBeInTheDocument()
      await user.type(screen.getAllByLabelText('Action')[0], '!')
      expect(screen.queryByRole('button', { name: 'Submit for review' })).not.toBeInTheDocument()
      expect(screen.getByText('Save or discard your edits before moving the flow forward.')).toBeInTheDocument()
    })
  })
})

describe('versions', () => {
  const versions = [
    { versionNo: 3, source: 'Manual', authorName: 'Asha Lead', createdAt: new Date().toISOString(), changeSummary: 'Clarified step 2', isConfirmed: false },
    { versionNo: 2, source: 'Analysis', authorName: 'System', createdAt: new Date().toISOString(), changeSummary: null, isConfirmed: true },
    { versionNo: 1, source: 'Analysis', authorName: 'System', createdAt: new Date().toISOString(), changeSummary: null, isConfirmed: false },
  ]
  const base = { ...standard, 'GET /flows/f-1': () => ({ ...flow, currentVersionNo: 3 }), 'GET /flows/f-1/versions': () => versions }

  it('lists every version with who made it, marks the current and the confirmed ones', async () => {
    signedIn(); mockApi(base); app('/flow/f-1?tab=versions')
    expect(await screen.findByText('Version 3 (current)')).toBeInTheDocument()
    expect(screen.getByText('Was confirmed')).toBeInTheDocument()
    expect(screen.getByText('Clarified step 2')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Roll back to this version' })).toHaveLength(2)      // not for the current one
  })

  it('compares two versions, oldest first whatever the order chosen, and shows what was added, removed and changed', async () => {
    signedIn()
    const diff = { fromVersion: 1, toVersion: 3, items: [
      { section: 'steps', itemId: 'S2', change: 'Changed', before: { id: 'S2', action: 'Picks the ward.', sourceEntryIds: ['E1'] }, after: { id: 'S2', action: 'Pre-selects the ward.', sourceEntryIds: ['E1'] } },
      { section: 'steps', itemId: 'S6', change: 'Added', before: null, after: { id: 'S6', action: 'Saves.' } },
      { section: 'businessRules', itemId: 'BR2', change: 'Removed', before: { id: 'BR2', rule: 'Old rule.' }, after: null },
    ] }
    const server = mockApi({ ...base, 'GET /flows/f-1/versions/1/diff/3': () => diff })
    app('/flow/f-1?tab=versions')
    const user = userEvent.setup()
    await screen.findByText('Version 3 (current)')
    await user.selectOptions(screen.getByLabelText('From', { exact: true }), '3')
    await user.selectOptions(screen.getByLabelText('To', { exact: true }), '1')                      // chosen "backwards"
    await user.click(screen.getByRole('button', { name: 'Compare' }))
    expect(await screen.findByText('Version 1 to version 3: 3 change(s)')).toBeInTheDocument()
    expect(server.count('GET', 'flows/f-1/versions/1/diff/3')).toBe(1)
    expect(screen.getAllByText('Before:')).toHaveLength(2)                                           // the changed and the removed item
    expect(screen.getByText(/Action: Picks the ward\./)).toBeInTheDocument()
    expect(screen.getByText(/Action: Pre-selects the ward\./)).toBeInTheDocument()
    for (const word of ['Changed', 'Added', 'Removed']) expect(screen.getByText(word)).toBeInTheDocument()
    expect(screen.queryByText(/sourceEntryIds|E1/)).not.toBeInTheDocument()                           // bookkeeping stays out of the picture
  })

  it('says so when two versions are identical', async () => {
    signedIn(); mockApi({ ...base, 'GET /flows/f-1/versions/2/diff/3': () => ({ fromVersion: 2, toVersion: 3, items: [] }) }); app('/flow/f-1?tab=versions')
    await screen.findByText('Version 3 (current)')
    await userEvent.setup().click(screen.getByRole('button', { name: 'Compare' }))
    expect(await screen.findByText('Versions 2 and 3 are identical.')).toBeInTheDocument()
  })

  it('rolls back only after asking, and only the lead can', async () => {
    signedIn(); const server = mockApi({ ...base, 'POST /flows/f-1/versions/1/rollback': () => contentDto(4) }); app('/flow/f-1?tab=versions')
    const user = userEvent.setup()
    await screen.findByText('Version 3 (current)')
    await user.click(screen.getAllByRole('button', { name: 'Roll back to this version' })[1])        // version 1
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Roll back to version 1?')
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(server.count('POST', 'flows/f-1/versions/1/rollback')).toBe(0)
    await user.click(screen.getAllByRole('button', { name: 'Roll back to this version' })[1])
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Roll back' }))
    await waitFor(() => expect(server.count('POST', 'flows/f-1/versions/1/rollback')).toBe(1))
    expect(await screen.findByText('Rolled back to version 1.')).toBeInTheDocument()
  })

  it('gives an executive the history but no roll back', async () => {
    signedIn(); mockApi({ ...base, ...as('Executive') }); app('/flow/f-1?tab=versions')
    await screen.findByText('Version 3 (current)')
    expect(screen.queryByRole('button', { name: 'Roll back to this version' })).not.toBeInTheDocument()
  })
})


describe('the detailed view of a workflow', () => {
  it('shows the notes on its first tab, with the three ways to add more', async () => {
    signedIn(); mockApi(standard); app('/flow/f-1')
    expect(await screen.findByText(entry.rawText)).toBeInTheDocument()
    expect(await screen.findByRole('group', { name: 'Add notes' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Back to the project/ })).toHaveAttribute('href', '/p/p-1')
  })
})
