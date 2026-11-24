import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { attachFiles, kindOfFile, MAX_TEXT_CHARS } from './attach'

const uploadPhoto = vi.fn()
const uploadVoice = vi.fn()
const preparePhoto = vi.fn()
const toWav16k = vi.fn()
const enqueueEntry = vi.fn()
vi.mock('./upload', () => ({ uploadPhoto: (...a: unknown[]) => uploadPhoto(...a), uploadVoice: (...a: unknown[]) => uploadVoice(...a) }))
vi.mock('./image', () => ({ preparePhoto: (...a: unknown[]) => preparePhoto(...a) }))
vi.mock('./audio', async () => ({ ...(await vi.importActual<object>('./audio')), toWav16k: (...a: unknown[]) => toWav16k(...a) }))
vi.mock('../offline/outbox', () => ({ enqueueEntry: (...a: unknown[]) => enqueueEntry(...a) }))

const file = (name: string, type: string, body = 'x') => new File([body], name, { type })
const base = { flowId: 'f-1', language: 'en-IN', mediaOk: true, sleep: () => Promise.resolve() }

beforeEach(() => {
  vi.clearAllMocks()
  preparePhoto.mockImplementation(async (f: File) => f)
  uploadPhoto.mockResolvedValue({})
  uploadVoice.mockResolvedValue({})
  toWav16k.mockResolvedValue({ wav: new Blob(['wav']), durationSec: 90 })
  enqueueEntry.mockResolvedValue(undefined)
})

describe('which kind of note a file becomes', () => {
  it.each([
    ['page.jpg', 'image/jpeg', 'photo'], ['IMG_1.HEIC', '', 'photo'], ['scan.png', 'image/png', 'photo'],
    ['call.m4a', 'audio/mp4', 'voice'], ['talk.mp3', '', 'voice'], ['rec.webm', 'audio/webm', 'voice'], ['voice.OGG', '', 'voice'],
    ['notes.txt', 'text/plain', 'text'], ['notes.md', '', 'text'], ['list.csv', 'text/csv', 'text'],
    ['plan.pdf', 'application/pdf', 'document'], ['sheet.xlsx', '', 'document'], ['old.xls', 'application/vnd.ms-excel', 'document'], ['letter.docx', '', 'other'], ['noextension', '', 'other'],
  ])('%s (%s) is a %s', (name, type, kind) => expect(kindOfFile({ name, type })).toBe(kind))
})

describe('attaching several files at once', () => {
  it('adds a mix of photos, recordings and text files, each as its own kind of note', async () => {
    const summary = await attachFiles([file('a.jpg', 'image/jpeg'), file('b.m4a', 'audio/mp4'), file('c.txt', 'text/plain', 'The nurse opens the menu.'), file('d.png', 'image/png')], base)

    expect(summary).toEqual({ added: 4, failed: [] })
    expect(uploadPhoto).toHaveBeenCalledTimes(2)
    expect(uploadVoice).toHaveBeenCalledTimes(1)
    expect(uploadVoice.mock.calls[0].slice(0, 4)).toEqual(['f-1', expect.any(Blob), 90, 'en-IN'])
    expect(enqueueEntry).toHaveBeenCalledWith('f-1', expect.objectContaining({ kind: 'Text', rawText: 'The nurse opens the menu.', language: 'en-IN' }))
  })

  it('counts each kind separately for the messages, and reports progress after every file', async () => {
    const added: string[] = [], progress: string[] = []
    await attachFiles([file('a.jpg', 'image/jpeg'), file('b.jpg', 'image/jpeg'), file('c.mp3', 'audio/mpeg')], {
      ...base, concurrency: 1, onAdded: (_f, kind, n, of) => added.push(`${kind} ${n}/${of}`), onProgress: (d, t) => progress.push(`${d}/${t}`),
    })
    expect(added).toEqual(['photo 1/2', 'photo 2/2', 'voice 1/1'])
    expect(progress).toEqual(['1/3', '2/3', '3/3'])
  })

  it('never has more than three in flight at once', async () => {
    let inFlight = 0, most = 0
    uploadPhoto.mockImplementation(async () => { most = Math.max(most, ++inFlight); await new Promise(r => setTimeout(r, 5)); inFlight-- })
    await attachFiles(Array.from({ length: 10 }, (_, i) => file(`p${i}.jpg`, 'image/jpeg')), base)
    expect(uploadPhoto).toHaveBeenCalledTimes(10)
    expect(most).toBe(3)
  })

  it('names every file that could not be added with the reason and still adds the rest', async () => {
    uploadPhoto.mockRejectedValueOnce(new ApiError(422, 'x', 'Upload a JPEG, PNG or WebP photo.'))
    const summary = await attachFiles([file('bad.jpg', 'image/jpeg'), file('ok.jpg', 'image/jpeg'), file('plan.docx', ''), file('empty.txt', 'text/plain', '   ')], { ...base, concurrency: 1 })

    expect(summary.added).toBe(1)
    expect(summary.failed).toEqual([
      { file: 'bad.jpg', message: 'Upload a JPEG, PNG or WebP photo.' },
      { file: 'plan.docx', message: expect.stringContaining('cannot be added yet') },
      { file: 'empty.txt', message: 'This file is empty.' },
    ])
  })

  it('refuses a recording longer than seven minutes and one the browser cannot open, without sending anything', async () => {
    toWav16k.mockResolvedValueOnce({ wav: new Blob(['w']), durationSec: 8 * 60 })
    toWav16k.mockRejectedValueOnce(new Error('decode'))
    const summary = await attachFiles([file('long.mp3', 'audio/mpeg'), file('odd.m4a', 'audio/mp4')], { ...base, concurrency: 1 })

    expect(uploadVoice).not.toHaveBeenCalled()
    expect(summary.failed[0].message).toMatch(/8:00 long.*7:00/)
    expect(summary.failed[1].message).toContain('could not be opened')
  })

  it('refuses a text file that is too long for one note and a photo the browser cannot prepare', async () => {
    preparePhoto.mockRejectedValueOnce(new Error('This photo could not be prepared. Try another one, or take it again.'))
    preparePhoto.mockRejectedValueOnce(new TypeError('Cannot read properties of undefined'))
    const summary = await attachFiles([file('big.txt', 'text/plain', 'a'.repeat(MAX_TEXT_CHARS + 1)), file('a.jpg', 'image/jpeg'), file('b.jpg', 'image/jpeg')], { ...base, concurrency: 1 })

    expect(enqueueEntry).not.toHaveBeenCalled()
    expect(summary.failed.map(f => f.message)).toEqual([
      expect.stringContaining('too long for one note'),
      'This photo could not be prepared. Try another one, or take it again.',
      'This photo could not be read. Try another one, or take it again.',          // an unexpected error is never shown as a technical message
    ])
  })

  it('adds only text files while the administrator has switched photos and recordings off', async () => {
    const summary = await attachFiles([file('a.jpg', 'image/jpeg'), file('b.mp3', 'audio/mpeg'), file('c.txt', 'text/plain', 'typed')], { ...base, mediaOk: false })

    expect(summary.added).toBe(1)
    expect(uploadPhoto).not.toHaveBeenCalled()
    expect(summary.failed).toHaveLength(2)
    expect(summary.failed[0].message).toContain('switched off by the administrator')
  })

  it('waits out the per-minute AI limit and carries on instead of failing the file', async () => {
    const sleeps: number[] = [], waiting: number[] = []
    uploadPhoto.mockRejectedValueOnce(new ApiError(429, 'Too many', 'Wait a minute', 'too_many_requests'))
    const summary = await attachFiles([file('a.jpg', 'image/jpeg')], { ...base, sleep: async ms => { sleeps.push(ms) }, onWaiting: s => waiting.push(s) })

    expect(summary).toEqual({ added: 1, failed: [] })
    expect(uploadPhoto).toHaveBeenCalledTimes(2)
    expect(sleeps).toEqual([62_000])
    expect(waiting).toEqual([62])
  })

  it('gives up on a file after three limited tries and says so', async () => {
    uploadPhoto.mockRejectedValue(new ApiError(429, 'Too many', 'That was a lot at once. Wait a minute and try again.', 'too_many_requests'))
    const summary = await attachFiles([file('a.jpg', 'image/jpeg')], base)
    expect(uploadPhoto).toHaveBeenCalledTimes(3)
    expect(summary.failed[0].message).toContain('Wait a minute')
  })

  it('does nothing for no files', async () => {
    expect(await attachFiles([], base)).toEqual({ added: 0, failed: [] })
  })
})