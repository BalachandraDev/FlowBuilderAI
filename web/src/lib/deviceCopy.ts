// A copy of every implementation document on the device the person is using: on a computer in the folder they chose (or the browser's
// Downloads folder), on a phone in the phone's Downloads. The server keeps its own copy too, so colleagues can open the document from the library.
// A web page may only write to a folder the person picked, so the folder is picked once and remembered by the browser.

const KEY = 'fc.saveCopy'
const DB = 'fc-device'
const STORE = 'handles'
const FOLDER = 'pdf-folder'

interface FolderHandle {
  name: string
  queryPermission?: (o: { mode: 'readwrite' }) => Promise<'granted' | 'denied' | 'prompt'>
  requestPermission?: (o: { mode: 'readwrite' }) => Promise<'granted' | 'denied' | 'prompt'>
  getFileHandle: (name: string, o: { create: boolean }) => Promise<{ createWritable: () => Promise<{ write: (b: Blob) => Promise<void>; close: () => Promise<void> }> }>
}
type Picker = (o?: { id?: string; mode?: 'readwrite' }) => Promise<FolderHandle>

/** Whether a copy is saved on the device when a document is made. On unless the person turned it off. */
export function saveCopyEnabled(): boolean {
  try { return localStorage.getItem(KEY) !== 'off' } catch { return true }
}
export function setSaveCopyEnabled(on: boolean): void {
  try { localStorage.setItem(KEY, on ? 'on' : 'off') } catch { /* private window: the choice lasts until the page is closed */ }
}

/** A phone or tablet: no folder can be chosen there, files go to the Downloads of the device. */
export function isPhone(): boolean {
  if (typeof navigator === 'undefined') return false
  if (/Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent)) return true
  return typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches && !matchMedia('(hover: hover)').matches
}

const picker = (): Picker | undefined => (window as unknown as { showDirectoryPicker?: Picker }).showDirectoryPicker?.bind(window)
/** A computer whose browser can write to a folder the person chooses (Chrome and Edge). */
export const canChooseFolder = () => !isPhone() && !!picker()

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1)
    req.onupgradeneeded = () => req.result.createObjectStore(STORE)
    req.onsuccess = () => resolve(req.result)
    req.onerror = () => reject(req.error)
  })
}
async function read(): Promise<FolderHandle | null> {
  try {
    const db = await open()
    return await new Promise(resolve => {
      const r = db.transaction(STORE).objectStore(STORE).get(FOLDER)
      r.onsuccess = () => resolve((r.result as FolderHandle | undefined) ?? null)
      r.onerror = () => resolve(null)
    })
  } catch { return null }
}
async function write(handle: FolderHandle | null): Promise<void> {
  const db = await open()
  await new Promise<void>((resolve, reject) => {
    const tx = db.transaction(STORE, 'readwrite')
    if (handle) tx.objectStore(STORE).put(handle, FOLDER); else tx.objectStore(STORE).delete(FOLDER)
    tx.oncomplete = () => resolve()
    tx.onerror = () => reject(tx.error)
  })
}

/** The name of the folder chosen for the copies, or null. */
export async function chosenFolderName(): Promise<string | null> {
  return canChooseFolder() ? (await read())?.name ?? null : null
}

/** Opens the browser's folder picker. Must be called from a click. Returns the folder's name, or null when the person cancelled. */
export async function chooseFolder(): Promise<string | null> {
  const pick = picker()
  if (!pick) return null
  try {
    const handle = await pick({ id: 'flowcapture-documents', mode: 'readwrite' })
    await write(handle)
    return handle.name
  } catch { return null }                                  // cancelled
}
export async function forgetFolder(): Promise<void> { try { await write(null) } catch { /* nothing saved */ } }

/** Hands a file to the browser's normal download, which puts it in the Downloads folder of the computer or the phone. */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
}

export type SavedWhere = { where: 'folder'; folder: string } | { where: 'downloads' }

/**
 * Saves a copy of a document on this device: into the chosen folder when there is one the browser still lets us write to, otherwise the
 * Downloads folder (on a phone always that). Never throws for a folder problem: it falls back to Downloads.
 */
export async function saveCopyOnDevice(blob: Blob, fileName: string): Promise<SavedWhere> {
  if (canChooseFolder()) {
    const handle = await read()
    if (handle) {
      try {
        let ok = (await handle.queryPermission?.({ mode: 'readwrite' })) === 'granted'
        if (!ok) ok = (await handle.requestPermission?.({ mode: 'readwrite' })) === 'granted'      // works only straight after a click
        if (ok) {
          const file = await (await handle.getFileHandle(fileName, { create: true })).createWritable()
          await file.write(blob)
          await file.close()
          return { where: 'folder', folder: handle.name }
        }
      } catch { /* the folder was removed or the permission was lost: use Downloads */ }
    }
  }
  downloadBlob(blob, fileName)
  return { where: 'downloads' }
}
