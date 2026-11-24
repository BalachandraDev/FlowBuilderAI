import { useId, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { X } from '@phosphor-icons/react'
import { useWindowBehaviour } from './ui'

/**
 * A panel that slides in from the right to show the details of one record, so the list behind it stays where it was. Escape or a click outside closes it,
 * the focus stays inside while it is open and goes back afterwards, and on a phone it fills the screen.
 */
export function Drawer({ title, subtitle, onClose, children, footer }: { title: string; subtitle?: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  const titleId = useId()
  useWindowBehaviour(ref, onClose)
  return createPortal(
    <div className="drawer-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <aside className="drawer" role="dialog" aria-modal="true" aria-labelledby={titleId} ref={ref}>
        <header className="drawer-head">
          <div><h2 id={titleId}>{title}</h2>{subtitle && <div className="muted small">{subtitle}</div>}</div>
          <button className="icon-btn" onClick={onClose} aria-label="Close the details"><X size={20} aria-hidden /></button>
        </header>
        <div className="drawer-body">{children}</div>
        {footer && <footer className="drawer-foot">{footer}</footer>}
      </aside>
    </div>,
    document.body,
  )
}
