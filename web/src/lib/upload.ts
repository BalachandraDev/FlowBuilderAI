import { api } from '../api/client'
import type { Entry } from '../api/types'
import { sha256Hex } from './audio'
import { uuid } from './uuid'

const MIN_CHUNK = 16 * 1024
const MAX_CHUNK = 4 * 1024 * 1024

interface AudioUpload { uploadId?: string; id?: string; receivedChunks?: number[] }

/**
 * Sends one recording: the note first (so it has a number and an owner), then the audio in pieces, then "done". Sending it again with
 * the same id carries on where it stopped, so a bad connection only costs a retry.
 */
export async function uploadVoice(
  flowId: string, wav: Blob, durationSec: number, language: string, onProgress: (percent: number) => void, entryId: string = uuid(),
): Promise<Entry> {
  onProgress(2)
  const entry = await api<Entry>(`flows/${flowId}/entries`, {
    method: 'POST', body: { id: entryId, kind: 'Voice', capturedAt: new Date().toISOString(), language, rawText: null, notes: null, intervieweeName: null, intervieweeRole: null },
  })

  const chunkSize = Math.min(MAX_CHUNK, Math.max(MIN_CHUNK, wav.size))
  const upload = await api<AudioUpload>('audio-uploads', {
    method: 'POST', body: { entryId, sizeBytes: wav.size, sha256: await sha256Hex(wav), contentType: 'audio/wav', durationSec, chunkSizeBytes: chunkSize },
  })
  const uploadId = (upload.uploadId ?? upload.id) as string
  const done = new Set(upload.receivedChunks ?? [])
  const total = Math.ceil(wav.size / chunkSize)

  for (let n = 0; n < total; n++) {
    if (!done.has(n)) {
      await api(`audio-uploads/${uploadId}/chunks/${n}`, { method: 'PUT', body: wav.slice(n * chunkSize, (n + 1) * chunkSize), contentType: 'application/octet-stream' })
    }
    onProgress(Math.round(5 + ((n + 1) / total) * 90))
  }
  await api(`audio-uploads/${uploadId}/complete`, { method: 'POST', body: {} })
  onProgress(100)
  return entry
}

/** Sends one photo of notes. The same id sent twice makes one note, not two. */
export async function uploadPhoto(flowId: string, photo: Blob, language: string, entryId: string = uuid()): Promise<Entry> {
  const form = new FormData()
  form.append('File', photo, 'notes.jpg')
  form.append('Language', language)
  form.append('EntryId', entryId)
  return api<Entry>(`flows/${flowId}/images`, { method: 'POST', body: form })
}

/** Sends an Excel workbook or a PDF. The server reads the text out of it (no AI) and saves it as a note that names the file. */
export async function uploadFileNote(flowId: string, file: File, language: string, entryId: string = uuid()): Promise<Entry> {
  const form = new FormData()
  form.append('File', file, file.name)
  form.append('Language', language)
  form.append('EntryId', entryId)
  return api<Entry>(`flows/${flowId}/files`, { method: 'POST', body: form })
}