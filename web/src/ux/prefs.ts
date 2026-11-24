import { useCallback, useEffect, useRef, useState } from 'react'
import { useSession } from '../state/session'

/**
 * Display preferences the person has chosen (a collapsed menu, the filters they used last, how many rows a table shows). They are kept in this browser,
 * for this person only, so a shared computer does not mix them up. Nothing secret is ever stored here: only choices about how the app looks and what was
 * last filtered.
 */
const EVENT = 'fc-pref-change'
const storageKey = (uid: string | undefined, key: string) => `fc.pref.${uid ?? 'anon'}.${key}`

function read<T>(uid: string | undefined, key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(storageKey(uid, key))
    return raw === null ? fallback : (JSON.parse(raw) as T)
  } catch { return fallback }
}

/** Like useState, but the value survives a reload and is shared by every part of the page that uses the same key. `reset` goes back to the default. */
export function usePref<T>(key: string, fallback: T): [T, (value: T | ((previous: T) => T)) => void, () => void] {
  const { user } = useSession()
  const uid = user?.id
  const [value, setValue] = useState<T>(() => read(uid, key, fallback))
  const latest = useRef(value)
  latest.current = value
  const defaults = useRef(fallback)

  // another part of the page (or another tab) changed the same preference
  useEffect(() => {
    setValue(read(uid, key, defaults.current))
    const changed = (e: Event) => {
      if (e.type === 'storage') { setValue(read(uid, key, defaults.current)); return }
      const detail = (e as CustomEvent<{ key: string; value: T }>).detail
      if (detail?.key === storageKey(uid, key)) { latest.current = detail.value; setValue(detail.value) }
    }
    window.addEventListener(EVENT, changed); window.addEventListener('storage', changed)
    return () => { window.removeEventListener(EVENT, changed); window.removeEventListener('storage', changed) }
  }, [uid, key])

  const set = useCallback((next: T | ((previous: T) => T)) => {
    const resolved = typeof next === 'function' ? (next as (p: T) => T)(latest.current) : next
    latest.current = resolved
    setValue(resolved)
    try { localStorage.setItem(storageKey(uid, key), JSON.stringify(resolved)) } catch { /* storage blocked: the choice lasts until the page is closed */ }
    window.dispatchEvent(new CustomEvent(EVENT, { detail: { key: storageKey(uid, key), value: resolved } }))
  }, [uid, key])

  const reset = useCallback(() => {
    latest.current = defaults.current
    setValue(defaults.current)
    try { localStorage.removeItem(storageKey(uid, key)) } catch { /* nothing to remove */ }
    window.dispatchEvent(new CustomEvent(EVENT, { detail: { key: storageKey(uid, key), value: defaults.current } }))
  }, [uid, key])

  return [value, set, reset]
}

/** The projects the person opened last, newest first: for the command palette and the "continue where you left off" strip. */
export interface RecentProject { id: string; name: string }
export function useRecentProjects() {
  const [recent, setRecent] = usePref<RecentProject[]>('recent-projects', [])
  const touch = useCallback((id: string, name: string) => {
    setRecent(list => (list[0]?.id === id && list[0]?.name === name ? list : [{ id, name }, ...list.filter(p => p.id !== id)].slice(0, 8)))
  }, [setRecent])
  return { recent, touch }
}
