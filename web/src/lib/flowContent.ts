// Helpers for the flow content (flow.v1): the sections and how to make new items, tidying before saving, and readable diffs.
// The editor is driven by the shape of the data, so a field the server adds shows up without a new screen; this file only knows
// what the data cannot say by itself (id prefixes, choices, what a brand new item looks like).

export type Json = string | number | boolean | null | Json[] | { [key: string]: Json }
export type Item = { [key: string]: Json }

export const TBC = 'To be confirmed'

export interface Section { key: string; title: string; prefix: string; idWidth?: number }

/** The lists of the flow, in the order a person reads them. */
export const SECTIONS: Section[] = [
  { key: 'actors', title: 'Actors', prefix: 'ACT' },
  { key: 'preConditions', title: 'Pre-conditions', prefix: 'PRE' },
  { key: 'postConditions', title: 'Post-conditions', prefix: 'POST' },
  { key: 'steps', title: 'Steps', prefix: 'S' },
  { key: 'decisions', title: 'Decisions', prefix: 'D' },
  { key: 'alternateFlows', title: 'Alternate flows', prefix: 'ALT' },
  { key: 'exceptionFlows', title: 'Exception flows', prefix: 'EXC' },
  { key: 'businessRules', title: 'Business rules', prefix: 'BR' },
  { key: 'validations', title: 'Validations', prefix: 'VAL' },
  { key: 'dataEntities', title: 'Data entities', prefix: 'DE' },
  { key: 'screens', title: 'Screens and fields', prefix: 'SC' },
  { key: 'reportsAndPrints', title: 'Reports and prints', prefix: 'RP' },
  { key: 'integrations', title: 'Integrations', prefix: 'INT' },
  { key: 'requirements', title: 'Requirements', prefix: 'REQ', idWidth: 3 },
  { key: 'acceptanceCriteria', title: 'Acceptance criteria', prefix: 'AC' },
  { key: 'userRoles', title: 'User roles', prefix: 'UR' },
  { key: 'nonFunctionalNotes', title: 'Non-functional notes', prefix: 'NF' },
  { key: 'gaps', title: 'Gaps', prefix: 'G' },
]

/** Parts of the content that are not edited as forms: the diagram is redrawn by the server, the rest is bookkeeping or lives on other tabs. */
export const HIDDEN = new Set(['schemaVersion', 'mermaid', 'confidence', 'changeLog', 'department', 'moduleCode', 'clarificationQuestions'])

export const OVERVIEW = ['flowTitle', 'purpose', 'scope', 'trigger']

const CHOICES: Record<string, string[]> = {
  priority: ['High', 'Medium', 'Low', TBC],
  frequency: ['Frequent', 'Occasional', 'Rare', TBC],
  direction: ['Inbound', 'Outbound', 'Both', TBC],
  mandatory: ['Yes', 'No', TBC],
  'reportsAndPrints.kind': ['Report', 'Print', 'Export', TBC],
  'nonFunctionalNotes.category': ['Performance', 'Audit', 'AccessRights', 'Usability', 'Other'],
}

/** The fixed answers for a field, or null when it is free text. `categories` are the active requirement category codes from the server. */
export function choicesFor(field: string, parent: string | undefined, categories: string[], current: string): string[] | null {
  let options: string[] | undefined
  if (field === 'category' && parent === 'requirements') options = [...(categories.length ? categories : ['CR', 'NR', 'ER']), TBC]
  else options = CHOICES[`${parent}.${field}`] ?? CHOICES[field]
  if (!options) return null
  return current && !options.includes(current) ? [...options, current] : options       // never hide a value that is already there
}

export const LONG_TEXT = new Set(['purpose', 'scope', 'action', 'description', 'rule', 'mainScenario', 'note', 'question', 'condition', 'summary', 'given', 'when', 'then', 'outcome', 'suggestedChange', 'errorMessage', 'text'])
export const READ_ONLY_FIELDS = new Set(['id', 'reqId'])

/** What to call a list of strings the data does not name clearly. */
export const humanize = (name: string): string => {
  const spaced = name.replace(/(?<!^)([A-Z])/g, ' $1').trim()
  return spaced ? spaced[0].toUpperCase() + spaced.slice(1).toLowerCase() : name
}

export const idOf = (item: unknown): string | null => {
  if (item && typeof item === 'object' && !Array.isArray(item)) {
    const o = item as Item
    if (typeof o.id === 'string' && o.id) return o.id
    if (typeof o.reqId === 'string' && o.reqId) return o.reqId
  }
  return null
}

const ID = /^([A-Z]+)(\d+)$/

/** Hands out ids that were never used, not even by an item deleted during this edit. */
export class IdIssuer {
  private highest = new Map<string, number>()
  constructor(content?: Item) { if (content) this.seed(content) }

  /** Starts again from freshly loaded content. */
  reset(content: Item) { this.highest.clear(); this.seed(content) }

  seed(content: Item) {
    for (const value of Object.values(content)) {
      if (!Array.isArray(value)) continue
      for (const item of value) {
        const id = idOf(item)
        const m = id ? ID.exec(id) : null
        if (m) this.highest.set(m[1], Math.max(this.highest.get(m[1]) ?? 0, Number(m[2])))
      }
    }
  }

  next(prefix: string, width = 0): string {
    const n = (this.highest.get(prefix) ?? 0) + 1
    this.highest.set(prefix, n)
    return prefix + String(n).padStart(width, '0')
  }
}

const field = () => ({ name: TBC, type: TBC, mandatory: TBC, source: TBC })

/** A new, valid item for a list: placeholders instead of blanks, because the server refuses empty text. */
export function newItem(listKey: string, parentKey: string | undefined, id: string, categories: string[]): Item {
  const none: Json[] = []
  switch (listKey) {
    case 'actors': return { id, role: TBC, department: TBC, sourceEntryIds: none }
    case 'preConditions':
    case 'postConditions': return { id, text: TBC, sourceEntryIds: none }
    case 'steps': return { id, actor: TBC, action: TBC, screen: TBC, inputs: none, outputs: none, next: ['END'], sourceEntryIds: none }
    case 'decisions': return { id, condition: TBC, yesPath: { summary: TBC, next: 'END' }, noPath: { summary: TBC, next: 'END' }, sourceEntryIds: none }
    case 'alternateFlows': return { id, name: TBC, condition: TBC, branchFrom: 'S1', steps: [TBC], rejoinAt: 'END', sourceEntryIds: none }
    case 'exceptionFlows': return { id, name: TBC, condition: TBC, handling: [TBC], outcome: TBC, sourceEntryIds: none }
    case 'businessRules': return { id, rule: TBC, appliesTo: none, sourceEntryIds: none }
    case 'validations': return { id, field: TBC, rule: TBC, errorMessage: TBC, sourceEntryIds: none }
    case 'dataEntities': return { id, name: TBC, fields: [field()], suggestedChange: TBC, sourceEntryIds: none }
    case 'screens': return { id, menuName: TBC, fields: [{ ...field(), defaultValue: TBC }], sourceEntryIds: none }
    case 'reportsAndPrints': return { id, name: TBC, kind: TBC, description: TBC, sourceEntryIds: none }
    case 'integrations': return { id, system: TBC, direction: TBC, description: TBC, sourceEntryIds: none }
    case 'requirements': return { reqId: id, description: TBC, category: categories[0] ?? TBC, priority: TBC, sourceEntryIds: none }
    case 'acceptanceCriteria': return { id, reqId: TBC, given: TBC, when: TBC, then: TBC, sourceEntryIds: none }
    case 'userRoles': return { id, userRole: TBC, frequency: TBC, module: TBC, menuName: TBC, sourceEntryIds: none }
    case 'nonFunctionalNotes': return { id, category: 'Other', note: TBC, sourceEntryIds: none }
    case 'gaps': return { id, section: TBC, description: TBC }
    case 'fields': return parentKey === 'screens' ? { ...field(), defaultValue: TBC } : field()
    default: return { id }
  }
}

// ------------------------------------------------------------------ tidying before saving

const ID_LIKE = new Set(['id', 'reqId', 'schemaVersion', 'mermaid', 'relatedGapId'])

/**
 * Tidies content the person edited before it is sent. The server rejects blank text, so a field that was cleared becomes
 * "To be confirmed" (the document's way of saying "not known yet") and empty list entries are dropped. Ids are never touched.
 */
export function normalize<T extends Json>(node: T): T {
  const copy = structuredClone(node) as Json
  walk(copy)
  return copy as T
}

function requiredDefault(key: string): string[] {
  return key === 'next' ? ['END'] : key === 'steps' || key === 'handling' ? [TBC] : []
}

function walk(node: Json, key = '') {
  if (Array.isArray(node)) { node.forEach(n => walk(n, key)); return }
  if (!node || typeof node !== 'object') return
  for (const [k, v] of Object.entries(node)) {
    if (typeof v === 'string') {
      if (ID_LIKE.has(k)) continue
      const s = v.trim()
      node[k] = s || TBC
    } else if (Array.isArray(v)) {
      if (v.every(x => typeof x === 'string')) {
        if (k === 'sourceEntryIds') continue
        const cleaned = (v as string[]).map(x => (x ?? '').trim()).filter(Boolean)
        // a list the schema does not allow to be empty gets its placeholder, but only inside an item (the top-level "steps" are objects)
        if (cleaned.length === 0) cleaned.push(...(key === '' && k === 'steps' ? [] : requiredDefault(k)))
        node[k] = cleaned
      } else v.forEach(x => walk(x, k))
    } else if (v && typeof v === 'object') walk(v, k)
  }
}

// ------------------------------------------------------------------ readable diffs

/** A short readable form of a changed item: its text fields, one per line. */
export function summarize(value: unknown): string {
  if (value === null || value === undefined) return ''
  if (typeof value === 'string') return value
  if (typeof value !== 'object' || Array.isArray(value)) return String(value)
  return Object.entries(value as Item)
    .filter(([k]) => !['id', 'reqId', 'sourceEntryIds'].includes(k))
    .map(([k, v]) => `${humanize(k)}: ${compact(v)}`)
    .join('\n')
}

function compact(v: unknown): string {
  if (typeof v === 'string') return v
  if (Array.isArray(v)) return v.map(compact).join(', ')
  if (v && typeof v === 'object') return Object.entries(v).map(([k, x]) => `${k}=${compact(x)}`).join('; ')
  return String(v)
}
