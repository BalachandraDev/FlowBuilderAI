import { createContext, useCallback, useContext, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { avatarColour, initials, type Tone } from '../lib/format'

export function Avatar({ name, size = 'md' }: { name: string; size?: 'sm' | 'md' | 'lg' }) {
  return <span className={`avatar ${size === 'md' ? '' : size}`} style={{ background: avatarColour(name) }} aria-hidden="true">{initials(name)}</span>
}

export function Pill({ tone = '', children }: { tone?: Tone; children: ReactNode }) {
  return <span className={`pill ${tone}`}>{children}</span>
}

export function PageHeader({ title, subtitle, hash, avatar, children }: { title: string; subtitle?: string; hash?: boolean; avatar?: string; children?: ReactNode }) {
  return (
    <header className="pagehead">
      {hash && <span className="hash" aria-hidden="true">#</span>}
      {avatar && <Avatar name={avatar} size="lg" />}
      <div className="grow">
        <h1>{title}</h1>
        {subtitle && <div className="sub">{subtitle}</div>}
      </div>
      {children}
    </header>
  )
}

export function EmptyState({ icon, title, text, children }: { icon?: ReactNode; title: string; text?: string; children?: ReactNode }) {
  return (
    <div className="empty" role="status">
      {icon && <div className="icon">{icon}</div>}
      <b>{title}</b>
      {text && <div>{text}</div>}
      {children}
    </div>
  )
}

export function Alert({ tone = 'info', children }: { tone?: 'info' | 'warn' | 'error' | 'good'; children: ReactNode }) {
  return <div className={`alert ${tone}`} role={tone === 'error' ? 'alert' : 'status'}>{children}</div>
}

export function Skeletons({ count = 3 }: { count?: number }) {
  return <div aria-busy="true" aria-label="Loading">{Array.from({ length: count }, (_, i) => <div key={i} className="skeleton" />)}</div>
}

/** What a field's input needs to say about itself to a screen reader: whether it is required, wrong, and where its explanation is. */
export interface FieldAria { 'aria-invalid'?: boolean; 'aria-required'?: boolean; 'aria-describedby'?: string }

/**
 * A text field with its label above it, a star when it is required, help below it and, when something is wrong, the reason in red. The star is drawn
 * by the style sheet, so the label's own text stays what it says. Callers that ignore the second argument keep working as before.
 */
export function Field({ label, help, required, error, children }: { label: string; help?: string; required?: boolean; error?: string | null; children: (id: string, aria: FieldAria) => ReactNode }) {
  const id = useId()
  const describedBy = [error ? `${id}-error` : null, help ? `${id}-help` : null].filter(Boolean).join(' ') || undefined
  return (
    <div className={`field ${required ? 'required' : ''} ${error ? 'invalid' : ''}`}>
      <label htmlFor={id}>{label}</label>
      {children(id, { 'aria-invalid': error ? true : undefined, 'aria-required': required ? true : undefined, 'aria-describedby': describedBy })}
      {error && <div className="field-error" id={`${id}-error`}>{error}</div>}
      {help && <div className="help" id={`${id}-help`}>{help}</div>}
    </div>
  )
}
// ---------- dialogs ----------

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

/**
 * What every window over the page does: the first control gets the focus, Tab stays inside the window, Escape closes it and the focus goes back to
 * what had it before. The closing function is read when Escape is pressed, so a window that is redrawn does not lose or steal the focus.
 */
/** Whether the person could reach the element: not hidden by the page itself or by a style, on it or on anything around it inside the window. */
function shown(el: HTMLElement, within: HTMLElement): boolean {
  for (let n: HTMLElement | null = el; n && n !== within.parentElement; n = n.parentElement) {
    if (n.hidden) return false
    const style = getComputedStyle(n)
    if (style.display === 'none' || (n === el && style.visibility === 'hidden')) return false
  }
  return true
}

export function useWindowBehaviour(ref: React.RefObject<HTMLElement | null>, onClose: () => void) {
  const close = useRef(onClose)
  close.current = onClose
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    const box = ref.current
    const first = box?.querySelector<HTMLElement>('[autofocus], input, textarea, select, button') ?? box
    if (first && !box?.contains(document.activeElement)) first.focus()
    const onKey = (e: KeyboardEvent) => {
      // a box inside the window that uses Escape itself (to stop editing a heading) marks itself with data-escape-own: then only that box closes
      if (e.key === 'Escape') { if ((e.target as HTMLElement | null)?.closest?.('[data-escape-own]')) return; e.stopPropagation(); close.current(); return }
      if (e.key !== 'Tab' || !box) return
      const items = [...box.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(el => shown(el, box))
      if (items.length === 0) { e.preventDefault(); return }
      const firstItem = items[0], last = items[items.length - 1]
      if (e.shiftKey && document.activeElement === firstItem) { e.preventDefault(); last.focus() }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); firstItem.focus() }
    }
    document.addEventListener('keydown', onKey, true)
    return () => { document.removeEventListener('keydown', onKey, true); previous?.focus?.() }
  }, [ref])
}

export function Dialog({ title, children, onClose, actions, wide = false }: { title: string; children: ReactNode; onClose: () => void; actions: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDivElement>(null)
  const titleId = useId()
  useWindowBehaviour(ref, onClose)
  // Drawn on the page itself, not inside whatever opened it: a dialog opened from the top bar (which blurs what is behind it) would otherwise
  // be placed relative to that bar, and show squeezed and cut off at the top of the screen.
  return createPortal(
    <div className="dialog-backdrop" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className={wide ? 'dialog wide' : 'dialog'} role="dialog" aria-modal="true" aria-labelledby={titleId} ref={ref}>
        <h2 id={titleId}>{title}</h2>
        {children}
        <div className="actions">{actions}</div>
      </div>
    </div>,
    document.body,
  )
}
export function PromptDialog({ title, label, message, okText = 'OK', initial = '', lines = 1, onSubmit, onClose }: {
  title: string; label: string; message?: string; okText?: string; initial?: string; lines?: number
  onSubmit: (value: string) => void; onClose: () => void
}) {
  const [value, setValue] = useState(initial)
  const ok = value.trim().length > 0
  const submit = () => { if (ok) onSubmit(value.trim()) }
  return (
    <Dialog title={title} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose}>Cancel</button>
      <button className="btn primary" onClick={submit} disabled={!ok}>{okText}</button>
    </>}>
      {message && <p className="muted">{message}</p>}
      <Field label={label}>
        {id => lines > 1
          ? <textarea id={id} className="input" rows={lines} value={value} onChange={e => setValue(e.target.value)} />
          : <input id={id} className="input" value={value} onChange={e => setValue(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') submit() }} />}
      </Field>
    </Dialog>
  )
}

export function ConfirmDialog({ title, message, okText, danger, onConfirm, onClose }: {
  title: string; message: string; okText: string; danger?: boolean; onConfirm: () => void; onClose: () => void
}) {
  return (
    <Dialog title={title} onClose={onClose} actions={<>
      <button className="btn" onClick={onClose}>Cancel</button>
      <button className={`btn ${danger ? 'danger' : 'primary'}`} onClick={onConfirm}>{okText}</button>
    </>}>
      <p>{message}</p>
    </Dialog>
  )
}

// ---------- toasts and the notification centre ----------

type ToastTone = 'good' | 'error' | ''
export interface ToastAction { label: string; onClick: () => void }
export interface ToastOptions { action?: ToastAction }
interface ToastItem { id: number; text: string; tone: ToastTone; action?: ToastAction }
export interface Notice { id: number; text: string; tone: ToastTone; at: number }

const ToastCtx = createContext<(text: string, tone?: ToastTone, options?: ToastOptions) => void>(() => {})
interface NoticeValue { notices: Notice[]; unread: number; markRead: () => void; clear: () => void }
const NoticeCtx = createContext<NoticeValue>({ notices: [], unread: 0, markRead: () => {}, clear: () => {} })
export const useToast = () => useContext(ToastCtx)
/** What the app has told the person in this visit (saved, failed, ready), so a message that disappeared can still be read. Nothing is kept after the page closes. */
export const useNotices = () => useContext(NoticeCtx)

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([])
  const [notices, setNotices] = useState<Notice[]>([])
  const [readUpTo, setReadUpTo] = useState(0)
  const next = useRef(1)
  const dismiss = useCallback((id: number) => setItems(list => list.filter(t => t.id !== id)), [])
  const push = useCallback((text: string, tone: ToastTone = '', options?: ToastOptions) => {
    const id = next.current++
    setItems(list => [...list, { id, text, tone, action: options?.action }])
    setNotices(list => [{ id, text, tone, at: Date.now() }, ...list].slice(0, 30))
    window.setTimeout(() => dismiss(id), options?.action ? 8000 : 4200)
  }, [dismiss])
  const value: NoticeValue = {
    notices, unread: notices.filter(n => n.id > readUpTo).length,
    markRead: () => setReadUpTo(next.current), clear: () => { setNotices([]); setReadUpTo(next.current) },
  }
  return (
    <ToastCtx.Provider value={push}>
      <NoticeCtx.Provider value={value}>
        {children}
        <div className="toasts" role="status" aria-live="polite">
          {items.map(t => (
            <div key={t.id} className={`toast ${t.tone}`}>
              <span>{t.text}</span>
              {t.action && <button className="toast-action" onClick={() => { t.action!.onClick(); dismiss(t.id) }}>{t.action.label}</button>}
            </div>
          ))}
        </div>
      </NoticeCtx.Provider>
    </ToastCtx.Provider>
  )
}