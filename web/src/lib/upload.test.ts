import { beforeEach, describe, expect, it, vi } from 'vitest'
import { auth } from '../api/client'
import { mockApi, problem, tokens } from '../test/mockApi'
import { uploadPhoto, uploadVoice } from './upload'

// hashing needs the browser's crypto; what matters here is that the right value is sent
vi.mock('./audio', async orig => ({ ...(await orig<typeof import('./audio')>()), sha256Hex: async () => 'ab'.repeat(32) }))

beforeEach(() => { vi.unstubAllGlobals(); auth.clear(); auth.setTokens(tokens()) })

const entry = { id: 'e-1', flowId: 'f-1', kind: 'Voice' }
const MB = 1024 * 1024

describe('sending a recording', () => {
  it('makes the note, then sends the audio in pieces, then says it is done', async () => {
    const server = mockApi({
      'POST /flows/f-1/entries': () => entry,
      'POST /audio-uploads': () => ({ uploadId: 'u-1', chunkSizeBytes: 4 * MB, totalChunks: 3, receivedChunks: [], state: 'Initiated' }),
      'PUT /audio-uploads/u-1/chunks/*': () => ({ uploadId: 'u-1' }),
      'POST /audio-uploads/u-1/complete': () => ({ uploadId: 'u-1' }),
    })
    const progress: number[] = []

    const made = await uploadVoice('f-1', new Blob([new Uint8Array(10 * MB)]), 84, 'ta-IN', p => progress.push(p), 'entry-1')

    expect(made.id).toBe('e-1')
    expect(server.calls.map(c => `${c.method} ${c.path}`)).toEqual([
      'POST flows/f-1/entries', 'POST audio-uploads',
      'PUT audio-uploads/u-1/chunks/0', 'PUT audio-uploads/u-1/chunks/1', 'PUT audio-uploads/u-1/chunks/2', 'POST audio-uploads/u-1/complete',
    ])
    expect(server.calls[0].body).toMatchObject({ id: 'entry-1', kind: 'Voice', language: 'ta-IN', rawText: null })
    expect(server.calls[1].body).toEqual({ entryId: 'entry-1', sizeBytes: 10 * MB, sha256: 'ab'.repeat(32), contentType: 'audio/wav', durationSec: 84, chunkSizeBytes: 4 * MB })
    expect(server.calls[2].headers.get('Content-Type')).toBe('application/octet-stream')
    expect(progress.at(-1)).toBe(100)
    expect([...progress].sort((a, b) => a - b)).toEqual(progress)                     // the bar only moves forward
  })

  it('sends a short recording as one piece that is at least the smallest piece the server accepts', async () => {
    const server = mockApi({
      'POST /flows/f-1/entries': () => entry,
      'POST /audio-uploads': () => ({ uploadId: 'u-1', receivedChunks: [] }),
      'PUT /audio-uploads/u-1/chunks/0': () => ({}), 'POST /audio-uploads/u-1/complete': () => ({}),
    })
    await uploadVoice('f-1', new Blob([new Uint8Array(1000)]), 1, 'en-IN', () => undefined)
    expect(server.calls[1].body).toMatchObject({ sizeBytes: 1000, chunkSizeBytes: 16 * 1024 })
    expect(server.calls.filter(c => c.method === 'PUT')).toHaveLength(1)
  })

  it('carries on where it stopped: pieces the server already has are not sent again', async () => {
    const server = mockApi({
      'POST /flows/f-1/entries': () => entry,
      'POST /audio-uploads': () => ({ uploadId: 'u-1', receivedChunks: [0, 1] }),
      'PUT /audio-uploads/u-1/chunks/*': () => ({}), 'POST /audio-uploads/u-1/complete': () => ({}),
    })
    await uploadVoice('f-1', new Blob([new Uint8Array(10 * MB)]), 60, 'en-IN', () => undefined, 'same-id')
    expect(server.calls.filter(c => c.method === 'PUT').map(c => c.path)).toEqual(['audio-uploads/u-1/chunks/2'])
  })

  it('stops with the server\'s reason, and does not say "done", when a piece is refused', async () => {
    const server = mockApi({
      'POST /flows/f-1/entries': () => entry,
      'POST /audio-uploads': () => ({ uploadId: 'u-1', receivedChunks: [] }),
      'PUT /audio-uploads/u-1/chunks/0': () => problem(422, 'The request cannot be completed.', 'Chunk 0 must be exactly 100 bytes.'),
    })
    await expect(uploadVoice('f-1', new Blob([new Uint8Array(1000)]), 1, 'en-IN', () => undefined)).rejects.toMatchObject({ status: 422 })
    expect(server.count('POST', 'audio-uploads/u-1/complete')).toBe(0)
  })
})

describe('sending a photo', () => {
  it('posts the picture as a form with the language and the id that makes a retry safe', async () => {
    const seen: FormData[] = []
    const fetchSpy = vi.fn(async (_: unknown, init?: RequestInit) => { seen.push(init!.body as FormData); return new Response(JSON.stringify({ id: 'e-9', kind: 'Image' }), { status: 201, headers: { 'Content-Type': 'application/json' } }) })
    vi.stubGlobal('fetch', fetchSpy)

    const made = await uploadPhoto('f-1', new Blob(['jpeg'], { type: 'image/jpeg' }), 'hi-IN', 'photo-1')

    expect(made.id).toBe('e-9')
    expect(String(fetchSpy.mock.calls[0][0])).toContain('/api/v1/flows/f-1/images')
    const form = seen[0]
    expect(form.get('Language')).toBe('hi-IN'); expect(form.get('EntryId')).toBe('photo-1')
    expect((form.get('File') as File).name).toBe('notes.jpg')
    const headers = fetchSpy.mock.calls[0][1]!.headers as Record<string, string>
    expect(headers['Content-Type']).toBeUndefined()                                   // the browser adds the multipart boundary itself
  })
})
