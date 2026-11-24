import { useEffect, useState } from 'react'
import { apiBlob } from '../api/client'
import { downloadBlob, saveCopyOnDevice } from './deviceCopy'

/**
 * Saves a file the API sends (it needs the sign-in, so a plain link will not do). A PDF goes into the folder the person chose for their
 * documents, if there is one; everything else, and a PDF with no folder, goes to the Downloads of the computer or the phone.
 */
export async function downloadFile(path: string, query: Record<string, string | number> | undefined, fallbackName: string): Promise<void> {
  const { blob, fileName } = await apiBlob(path, query)
  const name = fileName ?? fallbackName
  if (name.toLowerCase().endsWith('.pdf')) await saveCopyOnDevice(blob, name)
  else downloadBlob(blob, name)
}

/** Opens a PDF the API sends in a new tab, where the browser's own viewer shows it. */
export async function openPdf(path: string, query?: Record<string, string | number>): Promise<void> {
  const tab = window.open('', '_blank')                      // opened first, inside the click, so the pop-up blocker allows it
  try {
    const { blob } = await apiBlob(path, query)
    const url = URL.createObjectURL(blob)
    if (tab) tab.location.href = url; else window.location.href = url
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
  } catch (e) {
    tab?.close()
    throw e
  }
}

/** A picture or recording the API sends, as an address the page can show or play. Null until it has arrived, or when it cannot. */
export function useApiImage(path: string | undefined, query?: Record<string, string | number>, enabled = true): { url: string | null; failed: boolean } {
  const [state, setState] = useState<{ url: string | null; failed: boolean }>({ url: null, failed: false })
  const q = query ? JSON.stringify(query) : ''
  useEffect(() => {
    if (!path || !enabled) return undefined
    let cancelled = false
    let made: string | null = null
    setState({ url: null, failed: false })
    apiBlob(path, q ? JSON.parse(q) : undefined)
      .then(({ blob }) => { if (!cancelled) { made = URL.createObjectURL(blob); setState({ url: made, failed: false }) } })
      .catch(() => { if (!cancelled) setState({ url: null, failed: true }) })
    return () => { cancelled = true; if (made) URL.revokeObjectURL(made) }
  }, [path, q, enabled])
  return state
}
