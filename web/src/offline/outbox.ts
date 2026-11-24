import { ApiError, OfflineError, post } from '../api/client'
import type { CreateEntryRequest, Entry } from '../api/types'

/**
 * Notes are saved in this browser first, then sent. With no signal they wait and are sent when the connection is back, so a
 * consultant in a basement ward never loses what they typed. The entry id is made here, so sending twice never makes two entries.
 */
export interface QueuedEntry {
  id: string                 // the entry id
  flowId: string
  request: CreateEntryRequest
  queuedAt: number
  attempts: number
  /** Set when the server refused the entry (it will never be accepted as is). It stays visible so nothing is lost silently. */
  rejected?: string
}

const DB = 'flowcapture'
const STORE = 'outbox'

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1)
    req.onupgradeneeded = () => { req.result.createObjectStore(STORE, { keyPath: 'id' }) }
    req.onsuccess = () => resolve(req.result)
    req.onerror = () => reject(req.error)
  })
}

async function tx<T>(mode: IDBTransactionMode, run: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await open()
  try {
    return await new Promise<T>((resolve, reject) => {
      const t = db.transaction(STORE, mode)
      const r = run(t.objectStore(STORE))
      r.onsuccess = () => resolve(r.result)
      r.onerror = () => reject(r.error)
    })
  } finally { db.close() }
}

type Listener = () => void
const listeners = new Set<Listener>()
const notify = () => listeners.forEach(l => l())
export const subscribeOutbox = (l: Listener): (() => void) => { listeners.add(l); return () => listeners.delete(l) }

export async function queuedEntries(flowId?: string): Promise<QueuedEntry[]> {
  const all = await tx<QueuedEntry[]>('readonly', s => s.getAll())
  return all.filter(e => !flowId || e.flowId === flowId).sort((a, b) => a.queuedAt - b.queuedAt)
}

export async function queuedCount(): Promise<number> {
  return (await queuedEntries()).filter(e => !e.rejected).length
}

export async function enqueueEntry(flowId: string, request: CreateEntryRequest): Promise<QueuedEntry> {
  const item: QueuedEntry = { id: request.id, flowId, request, queuedAt: Date.now(), attempts: 0 }
  await tx('readwrite', s => s.put(item))
  notify()
  void flushOutbox()
  return item
}

export async function discardQueued(id: string): Promise<void> {
  await tx('readwrite', s => s.delete(id))
  notify()
}

export async function clearOutbox(): Promise<void> {
  await tx('readwrite', s => s.clear())
  notify()
}

let flushing: Promise<number> | null = null
let onSent: ((entry: Entry) => void) | undefined
export const onEntrySent = (fn: ((entry: Entry) => void) | undefined) => { onSent = fn }

/** Sends everything that is waiting, oldest first. Returns how many were sent. Safe to call at any time: overlapping calls share one run. */
export function flushOutbox(): Promise<number> {
  flushing ??= run().finally(() => { flushing = null })
  return flushing
}

async function run(): Promise<number> {
  let sent = 0
  for (const item of await queuedEntries()) {
    if (item.rejected) continue
    try {
      const entry = await post<Entry>(`flows/${item.flowId}/entries`, item.request)
      await tx('readwrite', s => s.delete(item.id))
      sent++
      onSent?.(entry)
      notify()
    } catch (e) {
      if (e instanceof OfflineError) break                                        // no signal: stop, try again later
      if (e instanceof ApiError && (e.status >= 500 || e.status === 429 || e.status === 408)) {
        await tx('readwrite', s => s.put({ ...item, attempts: item.attempts + 1 }))
        break                                                                      // the server is busy: try again later
      }
      if (e instanceof ApiError && e.status === 401) break                         // signed out: keep the work for the next sign-in
      const reason = e instanceof ApiError ? e.userMessage : 'The entry could not be sent.'
      await tx('readwrite', s => s.put({ ...item, attempts: item.attempts + 1, rejected: reason }))
      notify()
    }
  }
  return sent
}

/** Sends waiting entries when the connection returns, when the tab comes back to the front, and every half minute. */
export function startOutboxSync(): () => void {
  const go = () => { void flushOutbox() }
  const visible = () => { if (document.visibilityState === 'visible') go() }
  window.addEventListener('online', go)
  document.addEventListener('visibilitychange', visible)
  const timer = window.setInterval(go, 30_000)
  go()
  return () => { window.removeEventListener('online', go); document.removeEventListener('visibilitychange', visible); window.clearInterval(timer) }
}
