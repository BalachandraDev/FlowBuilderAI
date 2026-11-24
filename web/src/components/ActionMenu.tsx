import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { DotsThree } from '@phosphor-icons/react'

export interface MenuItem { label: string; icon?: ReactNode; onSelect: () => void; danger?: boolean; disabled?: boolean }

/**
 * A "more actions" button: the less common things that can be done to one record, kept out of the way until they are wanted. It opens with a click or
 * Enter, moves with the arrow keys, closes with Escape or a click elsewhere, and opens upward when there is no room below.
 */
export function ActionMenu({ label, items, align = 'right' }: { label: string; items: MenuItem[]; align?: 'left' | 'right' }) {
  const [open, setOpen] = useState(false)
  const [up, setUp] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const id = useId()

  useEffect(() => {
    if (!open) return undefined
    const rect = box.current?.getBoundingClientRect()
    setUp(!!rect && window.innerHeight - rect.bottom < items.length * 40 + 24 && rect.top > items.length * 40)
    box.current?.querySelector<HTMLElement>('[role="menuitem"]:not([disabled])')?.focus()
    const away = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', away)
    return () => document.removeEventListener('mousedown', away)
  }, [open, items.length])

  function key(e: React.KeyboardEvent) {
    if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); trigger.current?.focus(); return }
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
    e.preventDefault()
    const list = [...(box.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([disabled])') ?? [])]
    const at = list.indexOf(document.activeElement as HTMLElement)
    list[(at + (e.key === 'ArrowDown' ? 1 : -1) + list.length) % list.length]?.focus()
  }

  return (
    <div className="actmenu" ref={box} onKeyDown={key}>
      <button ref={trigger} className="icon-btn" aria-label={label} aria-haspopup="menu" aria-expanded={open} aria-controls={open ? id : undefined} onClick={() => setOpen(o => !o)}><DotsThree size={22} weight="bold" aria-hidden /></button>
      {open && (
        <div className={`menu actmenu-list ${up ? 'up' : ''} ${align}`} role="menu" id={id}>
          {items.map(it => (
            <button key={it.label} role="menuitem" className={`menu-item ${it.danger ? 'danger' : ''}`} disabled={it.disabled} onClick={() => { setOpen(false); it.onSelect() }}>{it.icon}{it.label}</button>
          ))}
        </div>
      )}
    </div>
  )
}
