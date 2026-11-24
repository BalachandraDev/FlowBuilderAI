import { useEffect, useRef, useState, type ReactNode } from 'react'
import { PencilSimple } from '@phosphor-icons/react'
import { describeError } from '../api/client'
import { Alert } from './ui'

/**
 * A heading with a pencil beside it. The pencil turns the heading into a box with Save and Cancel (Enter saves, Escape cancels).
 * The box closes the moment Save is pressed and the new heading shows at once; if the server refuses (for example the name is taken),
 * the box opens again with what was typed and says why. Without `canEdit` it is only the heading.
 */
export function EditableTitle({ value, label, canEdit, onSave, children, maxLength = 200 }: {
  value: string; label: string; canEdit: boolean; onSave: (next: string) => Promise<unknown>; children: ReactNode; maxLength?: number
}) {
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState(value)
  const [error, setError] = useState<string | null>(null)
  const pen = useRef<HTMLButtonElement>(null)
  const wasEditing = useRef(false)

  // after the box closes the keyboard returns to the pencil, so nobody has to find their place again
  useEffect(() => { if (wasEditing.current && !editing) pen.current?.focus(); wasEditing.current = editing }, [editing])

  function open() { setText(value); setError(null); setEditing(true) }

  async function submit() {
    const next = text.trim()
    if (!next) { setError('The heading cannot be empty.'); return }
    if (next === value) { setEditing(false); return }
    setError(null); setEditing(false)
    try { await onSave(next) } catch (e) { setText(next); setError(describeError(e)); setEditing(true) }
  }

  if (!canEdit) return <>{children}</>
  if (editing) {
    return (
      <form className="title-edit" onSubmit={e => { e.preventDefault(); void submit() }} onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); setEditing(false) } }}>
        <input className="input" autoFocus value={text} maxLength={maxLength} aria-label={label} aria-invalid={error ? true : undefined}
          onChange={e => setText(e.target.value)} onFocus={e => e.currentTarget.select()} />
        <button className="btn primary" type="submit" disabled={!text.trim()}>Save</button>
        <button className="btn" type="button" onClick={() => setEditing(false)}>Cancel</button>
        {error && <div className="title-edit-error"><Alert tone="error">{error}</Alert></div>}
      </form>
    )
  }
  return (
    <>
      {children}
      <button ref={pen} type="button" className="edit-pen" aria-label={`Edit ${label}`} title={`Edit ${label}`} onClick={open}><PencilSimple size={16} aria-hidden /></button>
    </>
  )
}
