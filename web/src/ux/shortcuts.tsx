import { useEffect, useRef } from 'react'
import { Dialog } from '../components/ui'
import { useSession } from '../state/session'

const typing = (t: EventTarget | null) => {
  const el = t as HTMLElement | null
  return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable)
}

/**
 * Keyboard shortcuts for the things done most. They never fire while the person is typing in a field, and never with Ctrl or Alt held (except Ctrl+K,
 * which opens search from anywhere). Two-key shortcuts start with G ("go to"): press G, then the letter.
 */
export function useGlobalShortcuts(on: { palette: () => void; help: () => void; rail: () => void; go: (to: string) => void }) {
  const { isAdmin } = useSession()
  const handlers = useRef(on)
  handlers.current = on
  useEffect(() => {
    let pending = 0
    const clear = () => { pending = 0 }
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); handlers.current.palette(); return }
      if (e.ctrlKey || e.metaKey || e.altKey || typing(e.target) || document.querySelector('[role="dialog"][aria-modal="true"]')) return
      const key = e.key.toLowerCase()
      if (pending && Date.now() - pending < 1500) {
        const table: Record<string, string> = isAdmin
          ? { h: '/admin', d: '/documents', c: '/admin/clients', m: '/admin/modules', u: '/admin/users', w: '/admin/handling', a: '/admin/ai', t: '/usage' }
          : { h: '/', d: '/documents', t: '/usage', n: '/new' }
        pending = 0
        if (table[key]) { e.preventDefault(); handlers.current.go(table[key]) }
        return
      }
      if (key === 'g') { pending = Date.now(); window.setTimeout(clear, 1500); return }
      if (key === '/') { e.preventDefault(); handlers.current.palette() }
      else if (key === '?') { e.preventDefault(); handlers.current.help() }
      else if (key === '[') handlers.current.rail()
      else if (key === 'n' && !isAdmin) { e.preventDefault(); handlers.current.go('/new') }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [isAdmin])
}

interface Row { keys: string[]; then?: boolean; or?: string[]; what: string }

/** The list of shortcuts, as the person can open with ? */
export function ShortcutsDialog({ onClose }: { onClose: () => void }) {
  const { isAdmin } = useSession()
  const go = (k: string, what: string): Row => ({ keys: ['G', k], then: true, what })
  const rows: Row[] = [
    { keys: ['Ctrl', 'K'], or: ['/'], what: 'Search everything and run commands' },
    { keys: ['?'], what: 'Show this list' },
    { keys: ['['], what: 'Collapse or expand the menu' },
    ...(isAdmin
      ? [go('H', 'Go to Overview'), go('D', 'Go to Final documents'), go('W', 'Go to Who handles what'), go('C', 'Go to Clients'), go('M', 'Go to Modules'), go('U', 'Go to Users'), go('A', 'Go to AI setup'), go('T', 'Go to AI usage')]
      : [{ keys: ['N'], what: 'Start a new project' }, go('H', 'Go to Projects'), go('D', 'Go to Documents'), go('T', 'Go to AI usage')]),
    { keys: ['Esc'], what: 'Close a window, a menu or a panel' },
    { keys: ['↑', '↓'], or: ['Enter'], what: 'Move through and choose in menus and search' },
  ]
  return (
    <Dialog title="Keyboard shortcuts" onClose={onClose} actions={<button className="btn primary" onClick={onClose}>Done</button>}>
      <p className="muted small">Shortcuts do not work while you are typing in a field.</p>
      <dl className="shortcuts">
        {rows.map(r => (
          <div key={r.keys.join() + r.what}>
            <dt>
              {r.keys.map((k, i) => <span key={k}>{i > 0 && r.then && <span className="muted small"> then </span>}<kbd>{k}</kbd></span>)}
              {r.or && <> <span className="muted small">or</span> {r.or.map(k => <kbd key={k}>{k}</kbd>)}</>}
            </dt>
            <dd>{r.what}</dd>
          </div>
        ))}
      </dl>
    </Dialog>
  )
}