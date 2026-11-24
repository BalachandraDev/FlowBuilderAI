import { describe, expect, it } from 'vitest'
import type { AuditLog } from '../api/types'
import { ago, auditSummary, avatarColour, change, compact, dayLabel, formatWhen, humanize, initials, monthName, shiftMonth, statusText, statusTone } from './format'

const audit = (over: Partial<AuditLog>): AuditLog => ({ id: 1, at: '', userId: null, userName: null, action: 'Create', entityType: 'Flow', entityId: 'x', projectId: null, oldValues: null, newValues: null, correlationId: null, ...over })

describe('names and colours', () => {
  it.each([
    ['Ravi Executive', 'RE'], ['asha', 'A'], ['Demo City Hospital', 'DC'], ['IP-Case_Sheet', 'IC'], ['', '?'], ['?', '?'], ['— —', '?'],
  ])('initials of %j are %j and never fail on odd names', (name, expected) => expect(initials(name)).toBe(expected))

  it('gives the same name the same colour and different names a spread of colours', () => {
    expect(avatarColour('Asha Lead')).toBe(avatarColour('Asha Lead'))
    const colours = new Set(['Asha Lead', 'Ravi Executive', 'Meera Reviewer', 'Kauvery Hospital', 'City Care', 'Apollo', 'KMC', 'Billing'].map(avatarColour))
    expect(colours.size).toBeGreaterThan(2)
  })
})

describe('words for statuses', () => {
  it('writes statuses in plain words and colours them by how done they are', () => {
    expect(statusText('UnderReview')).toBe('Under review')
    expect(statusText('DocumentsGenerated')).toBe('Documents generated')
    expect(statusText('Draft')).toBe('Draft')
    expect(statusTone('Confirmed')).toBe('good')
    expect(statusTone('DocumentsGenerated')).toBe('good')
    expect(statusTone('Analysed')).toBe('progress')
    expect(statusTone('UnderReview')).toBe('progress')
    expect(statusTone('Draft')).toBe('')
  })

  it('turns field names into sentences', () => {
    expect(humanize('purposeAndScope')).toBe('Purpose and scope')
    expect(humanize('Status')).toBe('Status')
    expect(humanize('CurrentVersionNo')).toBe('Current version no')
  })
})

describe('dates', () => {
  const now = new Date(2026, 10, 16, 15, 0)
  it('labels days the way people say them', () => {
    expect(dayLabel(new Date(2026, 10, 16, 9, 0), now)).toBe('Today')
    expect(dayLabel(new Date(2026, 10, 15, 23, 0), now)).toBe('Yesterday')
    expect(dayLabel(new Date(2026, 10, 10), now)).toMatch(/2026/)
  })
  it('shows only the time for today and the date for other days', () => {
    expect(formatWhen(new Date(2026, 10, 16, 9, 41).toISOString(), now)).not.toContain('-')
    expect(formatWhen(new Date(2026, 10, 10, 9, 41).toISOString(), now)).toMatch(/^10-11-2026 /)
  })
  it('says how long ago', () => {
    const t = Date.parse('2026-11-16T12:00:00Z')
    expect(ago(null)).toBe('never')
    expect(ago('2026-11-16T11:59:50Z', t)).toBe('just now')
    expect(ago('2026-11-16T11:30:00Z', t)).toBe('30 min ago')
    expect(ago('2026-11-16T09:00:00Z', t)).toBe('3 h ago')
  })
})

describe('the activity list in plain words', () => {
  it('describes changes, creations, removals and downloads', () => {
    expect(auditSummary(audit({ action: 'Update', oldValues: '{"Status":"UnderReview"}', newValues: '{"Status":"Confirmed"}' }))).toBe('Flow: Status UnderReview to Confirmed')
    expect(auditSummary(audit({ entityType: 'CaptureEntry' }))).toBe('Capture entry added')
    expect(auditSummary(audit({ action: 'Delete', entityType: 'Comment' }))).toBe('Comment removed')
    expect(auditSummary(audit({ action: 'Download', entityType: 'DocumentVersion', newValues: '{"DocNumber":"MRD-001","VersionNo":2,"format":"pdf"}' }))).toBe('Downloaded MRD-001 version 2 as pdf')
  })
  it('never throws on odd data and shows at most three changes', () => {
    expect(auditSummary(audit({ action: 'Update', oldValues: '{broken', newValues: '{broken' }))).toBe('Flow update')
    const s = auditSummary(audit({ action: 'Update', oldValues: '{"Status":null}', newValues: '{"Status":"Analysed","CurrentVersionNo":1,"Extra":"x","More":"y"}' }))
    expect(s).toContain('Status none to Analysed')
    expect(s).not.toContain('More')
  })
})

describe('numbers and months for the AI usage page', () => {
  it('shortens big numbers and leaves small ones whole', () => {
    expect(compact(0)).toBe('0'); expect(compact(950)).toBe('950'); expect(compact(9999)).toBe('9,999')
    expect(compact(10_000)).toBe('10K'); expect(compact(24_200)).toBe('24.2K'); expect(compact(150_400)).toBe('150K')
    expect(compact(1_000_000)).toBe('1M'); expect(compact(1_340_000)).toBe('1.3M')
  })
  it('names a month and moves between months, across year ends', () => {
    expect(monthName('2026-11')).toBe('November 2026')
    expect(shiftMonth('2026-11', -1)).toBe('2026-10'); expect(shiftMonth('2026-01', -1)).toBe('2025-12')
    expect(shiftMonth('2026-12', 1)).toBe('2027-01'); expect(shiftMonth('2026-11', 0)).toBe('2026-11')
  })
  it('compares with last month, or says nothing when there was nothing to compare', () => {
    expect(change(200, 100)).toBe('+100%'); expect(change(50, 100)).toBe('-50%'); expect(change(100, 100)).toBe('0%')
    expect(change(10, 0)).toBeNull()
  })
})