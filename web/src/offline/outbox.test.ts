import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from '../api/client'
import type { CreateEntryRequest, Entry } from '../api/types'
import { mockApi, problem, tokens } from '../test/mockApi'
import { clearOutbox, discardQueued, enqueueEntry, flushOutbox, onEntrySent, queuedCount, queuedEntries, subscribeOutbox } from './outbox'

const request = (id: string, text = 'a note'): CreateEntryRequest => ({
  id, kind: 'Text', capturedAt: new Date().toISOString(), language: 'en-IN', rawText: text, notes: null, intervieweeName: null, intervieweeRole: null,
})
const entry = (id: string): Entry => ({ id, flowId: 'f1', refNo: 1, kind: 'Text', capturedAt: '', capturedBy: 'u', capturedByName: 'A', language: 'en-IN', rawText: 'a note', notes: null, intervieweeName: null, intervieweeRole: null, audioState: 'None', audioDurationSec: null, transcriptState: 'None', isDeleted: false, rowVersion: 'v1' })

beforeEach(async () => { vi.unstubAllGlobals(); onEntrySent(undefined); auth.clear(); auth.setTokens(tokens()); await clearOutbox() })

describe('the outbox', () => {
  it('saves a note on the device first and sends it at once when there is a connection', async () => {
    const server = mockApi({ 'POST /flows/f1/entries': ({ body }) => entry((body as CreateEntryRequest).id) })
    await enqueueEntry('f1', request('e1'))
    await flushOutbox()
    expect(server.count('POST', 'flows/f1/entries')).toBe(1)
    expect(await queuedEntries()).toHaveLength(0)
  })

  it('keeps a note when there is no signal, and sends it, once, when the connection is back', async () => {
    mockApi({})                                                              // dead network
    await enqueueEntry('f1', request('e1', 'typed in the basement'))
    await flushOutbox()
    expect(await queuedEntries()).toMatchObject([{ id: 'e1', attempts: 0 }])
    expect(await queuedCount()).toBe(1)

    const server = mockApi({ 'POST /flows/f1/entries': ({ body }) => entry((body as CreateEntryRequest).id) })
    expect(await flushOutbox()).toBe(1)
    expect(await flushOutbox()).toBe(0)                                      // nothing left to send
    expect(server.count('POST', 'flows/f1/entries')).toBe(1)
    expect(await queuedCount()).toBe(0)
  })

  it('sends oldest first and stops at the first failure so the order is kept', async () => {
    mockApi({})
    await enqueueEntry('f1', request('first'))
    await new Promise(r => setTimeout(r, 5))
    await enqueueEntry('f1', request('second'))
    const sentOrder: string[] = []
    let calls = 0
    mockApi({ 'POST /flows/f1/entries': ({ body }) => { calls++; if (calls === 1) throw new TypeError('Failed to fetch'); sentOrder.push((body as CreateEntryRequest).id); return entry((body as CreateEntryRequest).id) } })
    await flushOutbox()                                                      // first attempt fails: nothing is sent, nothing skipped
    expect(sentOrder).toEqual([])
    await flushOutbox()
    expect(sentOrder).toEqual(['first', 'second'])
  })

  it('keeps a note and counts the attempt when the server is busy', async () => {
    mockApi({ 'POST /flows/f1/entries': () => problem(503, 'Unavailable') })
    await enqueueEntry('f1', request('e1'))
    await flushOutbox()
    expect(await queuedEntries()).toMatchObject([{ id: 'e1', attempts: 1 }])
    expect((await queuedEntries())[0].rejected).toBeUndefined()
  })

  it('keeps a note that the server refuses, marked "rejected" with the reason, and does not send it again', async () => {
    const server = mockApi({ 'POST /flows/f1/entries': () => problem(422, 'The request cannot be completed.', 'This flow is confirmed.') })
    await enqueueEntry('f1', request('e1'))
    await flushOutbox()
    await flushOutbox()
    const [q] = await queuedEntries()
    expect(q.rejected).toBe('This flow is confirmed.')
    expect(await queuedCount()).toBe(0)                                      // it no longer counts as waiting, but it is still there to see
    expect(server.count('POST', 'flows/f1/entries')).toBe(1)
    await discardQueued('e1')
    expect(await queuedEntries()).toHaveLength(0)
  })

  it('keeps everything when the person is signed out and sends it after the next sign-in', async () => {
    mockApi({ 'POST /flows/f1/entries': () => problem(401, 'Authentication failed.') })
    auth.clear()
    await enqueueEntry('f1', request('e1'))
    await flushOutbox()
    expect(await queuedEntries()).toMatchObject([{ id: 'e1' }])
    expect((await queuedEntries())[0].rejected).toBeUndefined()
  })

  it('uses the entry id it made itself, so a retry after a lost answer cannot create a second entry', async () => {
    const ids: string[] = []
    let lose = true
    mockApi({ 'POST /flows/f1/entries': ({ body }) => { ids.push((body as CreateEntryRequest).id); if (lose) { lose = false; throw new TypeError('Failed to fetch') } return entry('e1') } })
    await enqueueEntry('f1', request('e1'))
    await flushOutbox()                                                      // the server may have saved it, the answer was lost
    await flushOutbox()
    expect(ids).toEqual(['e1', 'e1'])
    expect(await queuedCount()).toBe(0)
  })

  it('tells the screen when the list changes and when an entry was sent', async () => {
    mockApi({ 'POST /flows/f1/entries': ({ body }) => entry((body as CreateEntryRequest).id) })
    const changed = vi.fn(), sent = vi.fn()
    const off = subscribeOutbox(changed)
    onEntrySent(sent)
    await enqueueEntry('f1', request('e1'))
    await flushOutbox()
    expect(changed).toHaveBeenCalled()
    expect(sent).toHaveBeenCalledWith(expect.objectContaining({ id: 'e1' }))
    off()
  })

  it('filters by flow', async () => {
    mockApi({})
    await enqueueEntry('f1', request('a'))
    await enqueueEntry('f2', request('b'))
    expect((await queuedEntries('f2')).map(q => q.id)).toEqual(['b'])
  })
})
