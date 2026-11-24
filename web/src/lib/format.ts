import type { AuditLog, FlowStatus } from '../api/types'

export const statusText = (s: FlowStatus | string): string => (s === 'UnderReview' ? 'Under review' : s === 'DocumentsGenerated' ? 'Documents generated' : s)

export type Tone = 'good' | 'progress' | 'warn' | 'bad' | ''
export const statusTone = (s: FlowStatus | string): Tone =>
  s === 'Confirmed' || s === 'DocumentsGenerated' ? 'good' : s === 'UnderReview' || s === 'Analysed' ? 'progress' : ''

/** "09:41" for today, otherwise "16-11-2026 09:41". */
export function formatWhen(iso: string, now = new Date()): string {
  const d = new Date(iso)
  const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  return d.toDateString() === now.toDateString() ? time : `${pad(d.getDate())}-${pad(d.getMonth() + 1)}-${d.getFullYear()} ${time}`
}

const pad = (n: number) => String(n).padStart(2, '0')

export function dayLabel(d: Date, now = new Date()): string {
  const start = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime()
  const diff = Math.round((start(now) - start(d)) / 86_400_000)
  if (diff === 0) return 'Today'
  if (diff === 1) return 'Yesterday'
  return d.toLocaleDateString([], { weekday: 'long', day: '2-digit', month: 'long', year: 'numeric' })
}

export function ago(iso: string | null, now = Date.now()): string {
  if (!iso) return 'never'
  const s = (now - Date.parse(iso)) / 1000
  if (s < 45) return 'just now'
  if (s < 3600) return `${Math.floor(s / 60)} min ago`
  if (s < 86_400) return `${Math.floor(s / 3600)} h ago`
  return formatWhen(iso, new Date(now))
}

/** 45200 becomes "45.2K", 1300000 becomes "1.3M": big numbers a person can read at a glance. Small numbers stay as they are. */
export function compact(n: number): string {
  if (n >= 1_000_000) return `${trim(n / 1_000_000)}M`
  if (n >= 10_000) return `${trim(n / 1000)}K`
  return n.toLocaleString('en-IN')
}
const trim = (x: number) => x.toFixed(x >= 100 ? 0 : 1).replace(/\.0$/, '')

/** "2026-11" becomes "November 2026". */
export function monthName(month: string): string {
  const [y, m] = month.split('-').map(Number)
  return new Date(Date.UTC(y, (m || 1) - 1, 1)).toLocaleDateString('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' })
}

/** The month before or after "2026-11". */
export function shiftMonth(month: string, by: number): string {
  const [y, m] = month.split('-').map(Number)
  const d = new Date(Date.UTC(y, (m || 1) - 1 + by, 1))
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`
}

/** "+12%" or "-8%" against last month, or null when there is nothing to compare with. */
export function change(now: number, before: number): string | null {
  if (before <= 0) return null
  const p = Math.round(((now - before) / before) * 100)
  return `${p > 0 ? '+' : ''}${p}%`
}

/** "purposeAndScope" becomes "Purpose and scope". */
export function humanize(name: string): string {
  const spaced = name.replace(/(?<!^)([A-Z])/g, ' $1').trim()
  if (!spaced) return name
  const lower = spaced.toLowerCase()
  return lower[0].toUpperCase() + lower.slice(1)
}

/** One line for an audit entry, for example "Flow: Status Analysed to Confirmed". Free text is never in the audit log. */
export function auditSummary(a: AuditLog): string {
  const kind = humanize(a.entityType)
  try {
    if (a.action === 'Download' && a.newValues) {
      const r = JSON.parse(a.newValues)
      return `Downloaded ${r.DocNumber ?? '?'} version ${r.VersionNo ?? '?'} as ${r.format ?? '?'}`
    }
    if (a.action === 'Update' && a.newValues) {
      const next = JSON.parse(a.newValues) as Record<string, unknown>
      const prev = a.oldValues ? JSON.parse(a.oldValues) as Record<string, unknown> : {}
      const show = (v: unknown) => (v === null || v === undefined ? 'none' : String(v))
      const parts = Object.entries(next).slice(0, 3).map(([k, v]) => `${humanize(k)} ${k in prev ? show(prev[k]) : 'none'} to ${show(v)}`)
      return `${kind}: ${parts.join(', ')}`
    }
  } catch { /* odd data: fall through to the plain wording */ }
  return a.action === 'Create' ? `${kind} added` : a.action === 'Delete' ? `${kind} removed` : `${kind} ${a.action.toLowerCase()}`
}

/** Two letters for an avatar: "Ravi Executive" gives "RE". */
export function initials(name: string): string {
  const words = (name ?? '').split(/[\s\-_.]+/).filter(w => /[\p{L}\p{N}]/u.test(w))
  const letters = words.slice(0, 2).map(w => ([...w].find(c => /[\p{L}\p{N}]/u.test(c)) ?? '').toUpperCase())
  return letters.length === 0 ? '?' : letters.join('')
}

const PALETTE = ['#0f766e', '#b45309', '#1d4ed8', '#be185d', '#4d7c0f', '#0369a1', '#9f1239', '#475569']
/** The same name always gets the same colour. */
export function avatarColour(name: string): string {
  let h = 7
  for (const c of name ?? '') h = (h * 31 + c.charCodeAt(0)) & 0x7fffffff
  return PALETTE[h % PALETTE.length]
}
