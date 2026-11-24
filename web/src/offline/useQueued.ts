import { useEffect, useState } from 'react'
import { queuedEntries, subscribeOutbox, type QueuedEntry } from './outbox'

/** The notes of one flow that are still waiting on this device (or were refused by the server), kept up to date. */
export function useQueued(flowId: string): QueuedEntry[] {
  const [items, setItems] = useState<QueuedEntry[]>([])
  useEffect(() => {
    const refresh = () => { void queuedEntries(flowId).then(setItems).catch(() => setItems([])) }
    refresh()
    return subscribeOutbox(refresh)
  }, [flowId])
  return items
}
