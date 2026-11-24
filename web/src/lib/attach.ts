import { ApiError, describeError } from '../api/client'
import { enqueueEntry } from '../offline/outbox'
import { MAX_RECORDING_SEC, mmss, toWav16k } from './audio'
import { preparePhoto } from './image'
import { uploadFileNote, uploadPhoto, uploadVoice } from './upload'
import { uuid } from './uuid'

export type FileKind = 'photo' | 'voice' | 'text' | 'document' | 'other'

/** A problem with one file that is worth showing to the person as it is written. Anything else becomes a general message. */
class AttachError extends Error {}

/** What the file picker offers: photos, recordings, text notes, Excel workbooks and PDFs, in any mix. */
export const ATTACH_ACCEPT = 'image/*,audio/*,text/plain,application/pdf,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,.txt,.md,.csv,.xlsx,.xls,.pdf,.m4a,.mp3,.wav,.ogg,.opus,.aac,.flac,.webm,.heic'
/** The server takes at most this much text in one note. */
export const MAX_TEXT_CHARS = 200_000
/** The most an Excel or PDF file may weigh. */
export const MAX_FILE_BYTES = 12 * 1024 * 1024

/** Which kind of note a file becomes. The browser's own type is used first, the ending of the name when it gives none (phones often do not). */
export function kindOfFile(f: { name: string; type: string }): FileKind {
  const type = (f.type || '').toLowerCase()
  const name = f.name.toLowerCase()
  if (type.startsWith('image/') || /\.(jpe?g|png|webp|heic|heif)$/.test(name)) return 'photo'
  if (type.startsWith('audio/') || /\.(wav|mp3|m4a|aac|ogg|oga|opus|flac|webm)$/.test(name)) return 'voice'
  if (type.startsWith('text/') || /\.(txt|md|csv|log)$/.test(name)) return 'text'
  // an Excel workbook or a PDF: the server reads the text out of it (no AI)
  if (type === 'application/pdf' || type.includes('spreadsheetml') || type === 'application/vnd.ms-excel' || /\.(pdf|xlsx|xlsm|xls)$/.test(name)) return 'document'
  return 'other'
}

export interface AttachOptions {
  flowId: string
  language: string
  /** The administrator allows photos and recordings (typed and text-file notes are always allowed). */
  mediaOk: boolean
  /** How many files are sent at the same time. */
  concurrency?: number
  /** Called after every file, whether it worked or not. */
  onProgress?: (done: number, total: number) => void
  /** Called when one file is added. */
  onAdded?: (file: File, kind: FileKind, nthOfKind: number, ofKind: number) => void
  /** Called when the free AI limit makes the sender wait. */
  onWaiting?: (seconds: number) => void
  /** Replaced in tests, so they do not really wait. */
  sleep?: (ms: number) => Promise<void>
}

export interface AttachFailure { file: string; message: string }
export interface AttachSummary { added: number; failed: AttachFailure[] }

const LIMIT_WAIT_MS = 62_000
const LIMIT_TRIES = 3

/** The server limits how many photos and recordings one person can send per minute (it protects the shared free AI quota): wait it out instead of failing. */
async function untilAllowed<T>(work: () => Promise<T>, o: AttachOptions): Promise<T> {
  const sleep = o.sleep ?? (ms => new Promise<void>(r => setTimeout(r, ms)))
  for (let attempt = 1; ; attempt++) {
    try { return await work() } catch (e) {
      if (!(e instanceof ApiError && e.status === 429) || attempt >= LIMIT_TRIES) throw e
      o.onWaiting?.(Math.round(LIMIT_WAIT_MS / 1000))
      await sleep(LIMIT_WAIT_MS)
    }
  }
}

/** One file becomes one note of its own kind. Throws a message a person can read when it cannot. */
async function addOne(file: File, kind: FileKind, o: AttachOptions): Promise<void> {
  if (kind === 'other')
    throw new AttachError('This kind of file cannot be added yet. Add photos, recordings (mp3, m4a, wav), Excel (.xlsx), PDF or text (.txt) files, or type the note.')
  if (kind !== 'text' && kind !== 'document' && !o.mediaOk) throw new AttachError('Photos and voice recordings are switched off by the administrator. Type the note, or attach it as a .txt file.')

  if (kind === 'photo') {
    let photo: Blob
    try { photo = await preparePhoto(file) } catch (e) { throw new AttachError(e instanceof Error && e.message.startsWith('This photo') ? e.message : 'This photo could not be read. Try another one, or take it again.') }
    await untilAllowed(() => uploadPhoto(o.flowId, photo, o.language), o)
    return
  }
  if (kind === 'voice') {
    let converted: { wav: Blob; durationSec: number }
    try { converted = await toWav16k(file) } catch { throw new AttachError('This recording could not be opened. Try an mp3, m4a or wav file.') }
    if (converted.durationSec > MAX_RECORDING_SEC)
      throw new AttachError(`This recording is ${mmss(converted.durationSec)} long. The longest is ${mmss(MAX_RECORDING_SEC)}: cut it into shorter parts and attach them together.`)
    if (converted.durationSec < 1) throw new AttachError('This recording is empty.')
    await untilAllowed(() => uploadVoice(o.flowId, converted.wav, converted.durationSec, o.language, () => undefined), o)
    return
  }
  if (kind === 'document') {
    // Excel and PDF are read on the server, so the file itself is sent. It uses no free AI tokens, so there is no per-minute wait.
    if (file.size > MAX_FILE_BYTES) throw new AttachError(`This file is ${Math.round(file.size / 1048576)} MB. The most that can be attached is ${MAX_FILE_BYTES / 1048576} MB.`)
    await uploadFileNote(o.flowId, file, o.language)
    return
  }
  const text = (await file.text()).trim()
  if (!text) throw new AttachError('This file is empty.')
  if (text.length > MAX_TEXT_CHARS) throw new AttachError(`This file is too long for one note (${Math.round(text.length / 1000)} thousand characters; the most is ${MAX_TEXT_CHARS / 1000} thousand). Split it into parts.`)
  await enqueueEntry(o.flowId, { id: uuid(), kind: 'Text', capturedAt: new Date().toISOString(), language: o.language, rawText: text, notes: null, intervieweeName: null, intervieweeRole: null })
}

/**
 * Adds any mix of photos, recordings and text files as notes, a few at a time, in the order they were chosen. One bad file never stops the others:
 * it is reported by name with the reason, and everything else is added.
 */
export async function attachFiles(files: File[], o: AttachOptions): Promise<AttachSummary> {
  const kinds = files.map(kindOfFile)
  const totals: Record<FileKind, number> = { photo: 0, voice: 0, text: 0, document: 0, other: 0 }
  for (const k of kinds) totals[k]++
  const seen: Record<FileKind, number> = { photo: 0, voice: 0, text: 0, document: 0, other: 0 }
  const summary: AttachSummary = { added: 0, failed: [] }
  let next = 0, done = 0

  const worker = async () => {
    while (next < files.length) {
      const i = next++
      const file = files[i], kind = kinds[i]
      try {
        await addOne(file, kind, o)
        summary.added++
        o.onAdded?.(file, kind, ++seen[kind], totals[kind])
      } catch (e) {
        summary.failed.push({ file: file.name || 'The file', message: e instanceof AttachError ? e.message : describeError(e) })
      }
      o.onProgress?.(++done, files.length)
    }
  }
  await Promise.all(Array.from({ length: Math.min(o.concurrency ?? 3, files.length) }, worker))
  return summary
}
