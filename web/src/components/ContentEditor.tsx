import { createContext, useContext, useId, type ReactNode } from 'react'
import { LockSimple, LockSimpleOpen, Plus, Trash } from '@phosphor-icons/react'
import { HIDDEN, LONG_TEXT, READ_ONLY_FIELDS, choicesFor, humanize, idOf, newItem, SECTIONS, type IdIssuer, type Item, type Json } from '../lib/flowContent'

/** What every editor on the review screen shares: who may edit, which items are locked, valid category codes and id numbering. */
export interface EditContext {
  readOnly: boolean
  locked: Set<string>
  toggleLock: (id: string) => void
  categories: string[]
  ids: IdIssuer
}
const Ctx = createContext<EditContext | null>(null)
export const EditProvider = Ctx.Provider
const useEdit = () => { const c = useContext(Ctx); if (!c) throw new Error('EditProvider is missing'); return c }

const prefixOf = (listKey: string) => SECTIONS.find(s => s.key === listKey)

function StringField({ name, parent, value, onChange }: { name: string; parent?: string; value: string; onChange: (v: string) => void }) {
  const { readOnly, categories } = useEdit()
  const label = humanize(name)
  const id = useId()
  const choices = choicesFor(name, parent, categories, value)
  const locked = readOnly || READ_ONLY_FIELDS.has(name)
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {choices
        ? <select id={id} className="input" value={value} disabled={readOnly} onChange={e => onChange(e.target.value)}>{!value && <option value="" />}{choices.map(c => <option key={c} value={c}>{c}</option>)}</select>
        : LONG_TEXT.has(name)
          ? <textarea id={id} className="input" rows={2} style={{ minHeight: 64 }} value={value} readOnly={locked} onChange={e => onChange(e.target.value)} />
          : <input id={id} className="input" value={value} readOnly={locked} onChange={e => onChange(e.target.value)} />}
    </div>
  )
}

function StringList({ name, value, onChange }: { name: string; value: string[]; onChange: (v: string[]) => void }) {
  const { readOnly } = useEdit()
  return (
    <div className="field">
      <div className="label">{humanize(name)}</div>
      {value.map((line, i) => (
        <div className="row" key={i} style={{ marginBottom: 6, flexWrap: 'nowrap' }}>
          <input className="input" aria-label={`${humanize(name)} ${i + 1}`} value={line} readOnly={readOnly} onChange={e => onChange(value.map((x, j) => (j === i ? e.target.value : x)))} />
          {!readOnly && <button className="icon-btn" aria-label={`Delete ${humanize(name)} ${i + 1}`} onClick={() => onChange(value.filter((_, j) => j !== i))}><Trash size={20} /></button>}
        </div>
      ))}
      {!readOnly && <button className="btn outline" onClick={() => onChange([...value, ''])}><Plus size={16} weight="bold" aria-hidden />Add</button>}
      {value.length === 0 && readOnly && <div className="muted small">None.</div>}
    </div>
  )
}

function Sources({ value }: { value: string[] }) {
  return (
    <div className="muted small" style={{ marginBottom: 8 }}>
      Based on {value.length === 0 ? <span className="pill">added by you</span> : value.map(r => <span key={r} className="pill" style={{ marginRight: 4 }}>{r}</span>)}
    </div>
  )
}

function ItemList({ listKey, parent, title, value, onChange }: { listKey: string; parent?: string; title: string; value: Item[]; onChange: (v: Item[]) => void }) {
  const edit = useEdit()
  const section = prefixOf(listKey)
  function add() {
    const id = section ? edit.ids.next(section.prefix, section.idWidth ?? 0) : ''
    onChange([...value, newItem(listKey, parent, id, edit.categories)])
  }
  return (
    <div style={{ marginBottom: 12 }}>
      {value.map((item, i) => {
        const id = idOf(item)
        const isLocked = !!id && edit.locked.has(id)
        return (
          <div key={id ?? i} className={`item ${isLocked ? 'locked' : ''}`}>
            <div className="row" style={{ flexWrap: 'nowrap' }}>
              <b style={{ flex: 1 }}>{id ?? `#${i + 1}`}{isLocked ? ' · locked' : ''}</b>
              {id && (
                <button className="icon-btn" disabled={edit.readOnly} onClick={() => edit.toggleLock(id)}
                  aria-pressed={isLocked} aria-label={isLocked ? `Unlock ${id}` : `Lock ${id} so re-analysis cannot change it`}>
                  {isLocked ? <LockSimple size={20} weight="fill" color="var(--warn)" /> : <LockSimpleOpen size={20} />}
                </button>
              )}
              {!edit.readOnly && (
                <button className="icon-btn" aria-label={`Delete ${id ?? `item ${i + 1}`}`} onClick={() => { onChange(value.filter((_, j) => j !== i)); if (id && isLocked) edit.toggleLock(id) }}><Trash size={20} /></button>
              )}
            </div>
            <ObjectEditor value={item} parent={listKey} onChange={next => onChange(value.map((x, j) => (j === i ? next : x)))} />
          </div>
        )
      })}
      {!edit.readOnly && <button className="btn outline" onClick={add}><Plus size={16} weight="bold" aria-hidden />Add {title.toLowerCase().replace(/s$/, '')}</button>}
      {value.length === 0 && edit.readOnly && <div className="muted small">None.</div>}
    </div>
  )
}

/** Edits any flow item from the shape of its data. */
export function ObjectEditor({ value, parent, onChange, only }: { value: Item; parent?: string; onChange: (v: Item) => void; only?: string[] }): ReactNode {
  const set = (k: string, v: Json) => onChange({ ...value, [k]: v })
  return (
    <>
      {Object.entries(value)
        .filter(([k]) => (only ? only.includes(k) : !HIDDEN.has(k)))
        .map(([k, v]) => {
          if (typeof v === 'string') return <StringField key={k} name={k} parent={parent} value={v} onChange={x => set(k, x)} />
          if (Array.isArray(v)) {
            if (k === 'sourceEntryIds') return <Sources key={k} value={v as string[]} />
            const objects = v.length > 0 ? typeof v[0] === 'object' : ['fields'].includes(k) || (parent === undefined && !!prefixOf(k))
            return objects
              ? <div key={k}><div className="label" style={{ fontWeight: 600, margin: '8px 0 4px' }}>{humanize(k)}</div><ItemList listKey={k} parent={parent} title={humanize(k)} value={v as Item[]} onChange={x => set(k, x)} /></div>
              : <StringList key={k} name={k} value={v as string[]} onChange={x => set(k, x)} />
          }
          if (v && typeof v === 'object') {
            return (
              <div key={k} style={{ marginBottom: 8 }}>
                <div style={{ fontWeight: 600 }}>{humanize(k)}</div>
                <div style={{ paddingLeft: 12 }}><ObjectEditor value={v as Item} parent={k} onChange={x => set(k, x)} /></div>
              </div>
            )
          }
          return null
        })}
    </>
  )
}

export { ItemList }
