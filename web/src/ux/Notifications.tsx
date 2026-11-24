import { useEffect, useRef, useState } from 'react'
import { Bell, CheckCircle, Info, Trash, WarningCircle } from '@phosphor-icons/react'
import { useNotices } from '../components/ui'

const clock = (at: number) => new Date(at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })

/**
 * The bell in the top bar: what the app told the person during this visit (saved, ready, failed), newest first, so a message that faded away can still be
 * read. The number is how many are new since it was last opened. Only real messages appear here, and they are forgotten when the page is closed.
 */
export function NotificationBell() {
  const { notices, unread, markRead, clear } = useNotices()
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return undefined
    markRead()
    const away = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false) }
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', away); document.addEventListener('keydown', esc)
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', esc) }
  }, [open])   // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="bell" ref={box}>
      <button className="icon-btn" onClick={() => setOpen(o => !o)} aria-haspopup="true" aria-expanded={open} aria-label={unread > 0 ? `Activity, ${unread} new` : 'Activity'} data-tip-below="Activity">
        <Bell size={20} aria-hidden />{unread > 0 && <span className="bell-dot" aria-hidden>{unread > 9 ? '9+' : unread}</span>}
      </button>
      {open && (
        <div className="menu bell-menu" role="region" aria-label="Recent activity">
          <div className="bell-head"><b>Activity in this visit</b>{notices.length > 0 && <button className="btn link" onClick={clear}><Trash size={14} aria-hidden />Clear</button>}</div>
          {notices.length === 0 && <p className="muted small bell-empty">Nothing yet. Saves, documents that are ready and anything that fails are listed here.</p>}
          <ul className="bell-list">
            {notices.map(n => (
              <li key={n.id} className={n.tone}>
                {n.tone === 'good' ? <CheckCircle size={18} weight="fill" aria-label="Done" /> : n.tone === 'error' ? <WarningCircle size={18} weight="fill" aria-label="Problem" /> : <Info size={18} weight="fill" aria-label="Note" />}
                <span>{n.text}</span><time className="muted small">{clock(n.at)}</time>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
