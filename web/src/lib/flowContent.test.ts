import { describe, expect, it } from 'vitest'
import { HIDDEN, IdIssuer, SECTIONS, TBC, choicesFor, humanize, idOf, newItem, normalize, summarize, type Item } from './flowContent'

describe('ids', () => {
  it('reads the id of an item, or the requirement id', () => {
    expect(idOf({ id: 'S1' })).toBe('S1')
    expect(idOf({ reqId: 'REQ001' })).toBe('REQ001')
    expect(idOf({ name: 'no id' })).toBeNull()
    expect(idOf('text')).toBeNull()
    expect(idOf(null)).toBeNull()
  })

  it('hands out the next free number per prefix and never reuses one, even after an item was deleted', () => {
    const ids = new IdIssuer({ steps: [{ id: 'S1' }, { id: 'S2' }, { id: 'S5' }], requirements: [{ reqId: 'REQ002' }], businessRules: [{ id: 'BR1' }] } as Item)
    expect(ids.next('S')).toBe('S6')
    expect(ids.next('S')).toBe('S7')                                  // S6 may have been deleted since; it is still not reused
    expect(ids.next('BR')).toBe('BR2')
    expect(ids.next('REQ', 3)).toBe('REQ003')
    expect(ids.next('NF')).toBe('NF1')                                // a section that was empty
  })

  it('starts again when new content is loaded', () => {
    const ids = new IdIssuer({ steps: [{ id: 'S9' }] } as Item)
    ids.reset({ steps: [{ id: 'S2' }] } as Item)
    expect(ids.next('S')).toBe('S3')
  })
})

describe('new items', () => {
  it('every list the editor offers can make a valid new item with a fresh id and no blank text', () => {
    for (const s of SECTIONS) {
      const item = newItem(s.key, undefined, `${s.prefix}1`, ['CR', 'NR'])
      expect(idOf(item), s.key).toBe(`${s.prefix}1`)
      const blanks: string[] = []
      const visit = (v: unknown, path: string) => {
        if (typeof v === 'string' && v.trim() === '' && !path.endsWith('id') && !path.endsWith('reqId')) blanks.push(path)
        else if (Array.isArray(v)) v.forEach((x, i) => visit(x, `${path}[${i}]`))
        else if (v && typeof v === 'object') Object.entries(v).forEach(([k, x]) => visit(x, `${path}.${k}`))
      }
      visit(item, s.key)
      expect(blanks, s.key).toEqual([])
    }
  })

  it('gives steps a way out, alternate flows a place to branch from and requirements the first category', () => {
    expect(newItem('steps', undefined, 'S1', []).next).toEqual(['END'])
    expect(newItem('alternateFlows', undefined, 'ALT1', [])).toMatchObject({ branchFrom: 'S1', rejoinAt: 'END', steps: [TBC] })
    expect(newItem('requirements', undefined, 'REQ001', ['NR', 'CR'])).toMatchObject({ reqId: 'REQ001', category: 'NR' })
    expect(newItem('fields', 'screens', '', [])).toHaveProperty('defaultValue')
    expect(newItem('fields', 'dataEntities', '', [])).not.toHaveProperty('defaultValue')
  })
})

describe('tidying before saving', () => {
  it('turns cleared text into "To be confirmed", trims, and drops empty list entries, without touching ids or the diagram', () => {
    const content = {
      schemaVersion: '1.0', flowTitle: '  Receive  ', purpose: '', mermaid: '', trigger: '   ',
      steps: [{ id: 'S1', actor: '', action: ' Opens the menu ', screen: '', inputs: ['', ' ward ', '  '], outputs: [], next: [], sourceEntryIds: [] }],
      alternateFlows: [{ id: 'ALT1', name: 'x', condition: '', branchFrom: 'S1', steps: [], rejoinAt: '', sourceEntryIds: [] }],
      exceptionFlows: [{ id: 'EXC1', name: 'x', condition: 'c', handling: ['  '], outcome: 'o', sourceEntryIds: [] }],
      requirements: [{ reqId: 'REQ001', description: '', category: 'CR', priority: 'High', sourceEntryIds: ['E1', ''] }],
      useCase: { primaryActors: [], priority: '', mainScenario: 'm' },
      clarificationQuestions: [{ id: 'Q1', section: 's', question: 'q', relatedGapId: '' }],
    } as unknown as Item

    const n = normalize(content) as Record<string, any>
    expect(n.flowTitle).toBe('Receive')
    expect(n.purpose).toBe(TBC)
    expect(n.trigger).toBe(TBC)
    expect(n.mermaid).toBe('')                                                       // the diagram text is never replaced
    expect(n.schemaVersion).toBe('1.0')
    expect(n.steps[0]).toMatchObject({ id: 'S1', actor: TBC, action: 'Opens the menu', screen: TBC, inputs: ['ward'], outputs: [], next: ['END'] })
    expect(n.alternateFlows[0]).toMatchObject({ condition: TBC, rejoinAt: TBC, steps: [TBC] })     // the schema does not allow an empty list of steps here
    expect(n.exceptionFlows[0].handling).toEqual([TBC])
    expect(n.requirements[0]).toMatchObject({ reqId: 'REQ001', description: TBC, sourceEntryIds: ['E1', ''] })   // source references are left exactly as they are
    expect(n.useCase).toMatchObject({ primaryActors: [], priority: TBC })
    expect(n.clarificationQuestions[0].relatedGapId).toBe('')
    expect(content.purpose).toBe('')                                                  // the original is not changed
  })

  it('leaves an empty top-level list of steps empty (those are items, not text)', () => {
    expect((normalize({ steps: [], actors: [] } as Item) as Record<string, unknown>).steps).toEqual([])
  })
})

describe('fields', () => {
  it('offers fixed answers for coded fields and never hides a value that is already there', () => {
    expect(choicesFor('priority', 'requirements', [], 'High')).toEqual(['High', 'Medium', 'Low', TBC])
    expect(choicesFor('priority', 'requirements', [], 'Urgent')).toContain('Urgent')
    expect(choicesFor('kind', 'reportsAndPrints', [], '')).toEqual(['Report', 'Print', 'Export', TBC])
    expect(choicesFor('category', 'nonFunctionalNotes', [], 'Audit')).toContain('AccessRights')
    expect(choicesFor('category', 'requirements', ['CR', 'NR'], 'CR')).toEqual(['CR', 'NR', TBC])
    expect(choicesFor('category', 'requirements', [], 'ER')).toEqual(['CR', 'NR', 'ER', TBC])        // the standard codes when none were loaded
    expect(choicesFor('action', 'steps', [], 'x')).toBeNull()                                         // free text
  })

  it('hides the parts that are not edited as forms', () => {
    for (const k of ['mermaid', 'confidence', 'changeLog', 'schemaVersion', 'clarificationQuestions']) expect(HIDDEN.has(k)).toBe(true)
    expect(HIDDEN.has('purpose')).toBe(false)
  })

  it('names fields in plain words', () => {
    expect(humanize('sourceEntryIds')).toBe('Source entry ids')
    expect(humanize('yesPath')).toBe('Yes path')
  })
})

describe('diffs', () => {
  it('shows an item as its text fields, one per line, without ids or sources', () => {
    expect(summarize({ id: 'S2', actor: 'MRD user', action: 'Opens the menu', inputs: ['a', 'b'], sourceEntryIds: ['E1'] })).toBe('Actor: MRD user\nAction: Opens the menu\nInputs: a, b')
    expect(summarize('plain')).toBe('plain')
    expect(summarize(null)).toBe('')
    expect(summarize({ yesPath: { summary: 'ok', next: 'S3' } })).toBe('Yes path: summary=ok; next=S3')
  })
})
