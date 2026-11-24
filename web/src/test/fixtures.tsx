import { expect } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import type { UserEvent } from '@testing-library/user-event'
import { QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { AppRoutes, makeQueryClient } from '../App'
import { ToastProvider } from '../components/ui'
import { SessionProvider } from '../state/session'
import { tokens } from './mockApi'

export const project = { id: 'p-1', code: 'KMC', hospitalName: 'Kauvery Hospital', location: '', startDate: '2026-11-01', leadUserId: 'u-1', leadName: 'Asha Lead', status: 'Active', monthlyAiTokenLimit: null, rowVersion: '1' }
export const client = { id: 'c-1', name: 'Kauvery Group', location: 'Chennai', isActive: true, projectCount: 1, userIds: [] as string[] }
export const dept = { id: 'd-1', moduleCode: 'MRD', name: 'Medical Records', isCustom: false, assignees: [{ id: 'u-1', displayName: 'Asha Lead' }], flowCount: 1, entryCount: 1, confirmedCount: 0 }
export const flow = { id: 'f-1', projectId: 'p-1', projectDepartmentId: 'd-1', moduleCode: 'MRD', departmentName: 'Medical Records', menuName: 'IP Case Sheet Receive', status: 'Analysed', currentVersionNo: 1, confirmedVersionNo: null, entryCount: 1, openQuestionCount: 3, lastActivityAt: new Date().toISOString(), rowVersion: '1' }
export const entry = { id: 'e-1', flowId: 'f-1', refNo: 1, kind: 'Text', capturedAt: new Date().toISOString(), capturedBy: 'u-1', capturedByName: 'Ravi Executive', language: 'en-IN', rawText: 'The user opens the receive menu.', notes: null, intervieweeName: null, intervieweeRole: null, audioState: 'None', audioDurationSec: null, transcriptState: 'None', isDeleted: false, rowVersion: 'v1' }

export const dashboard = { projectId: 'p-1', departmentsTotal: 2, flowsTotal: 4, captured: 4, analysed: 3, confirmed: 2, documented: 1, openClarifications: 2, blockingClarifications: 1, lastActivityAt: new Date().toISOString(), aiUsage: { month: '2026-11', inputTokens: 1000, outputTokens: 500, estimatedCost: 0.01, limit: null, runs: 3 },
  departments: [
    { projectDepartmentId: 'd-1', moduleCode: 'MRD', name: 'Medical Records', entries: 6, flows: 2, analysed: 2, confirmed: 2, documented: 1, openQuestions: 0 },
    { projectDepartmentId: 'd-2', moduleCode: 'BILLING', name: 'Billing', entries: 3, flows: 2, analysed: 1, confirmed: 0, documented: 0, openQuestions: 2 },
  ] }

/** The AI usage page's data: some tokens used this month, by purpose, by day, by project. */
export const usage = (over: object = {}) => ({
  month: new Date().toISOString().slice(0, 7), allUsers: false, provider: 'Gemini',
  totals: { calls: 4, inputTokens: 18000, outputTokens: 6200, totalTokens: 24200 }, today: { calls: 1, inputTokens: 2000, outputTokens: 700, totalTokens: 2700 },
  lastMonth: { calls: 2, inputTokens: 9000, outputTokens: 3000, totalTokens: 12000 },
  byKind: [{ kind: 'Analysis', calls: 2, inputTokens: 15000, outputTokens: 5000, totalTokens: 20000 }, { kind: 'Voice', calls: 1, inputTokens: 2500, outputTokens: 800, totalTokens: 3300 }, { kind: 'Photo', calls: 1, inputTokens: 500, outputTokens: 400, totalTokens: 900 }],
  byDay: [{ day: new Date().toISOString().slice(0, 8) + '01', calls: 3, totalTokens: 21500 }, { day: new Date().toISOString().slice(0, 10), calls: 1, totalTokens: 2700 }],
  byUser: [], byProject: [{ id: 'p-1', name: 'Kauvery Hospital', calls: 4, totalTokens: 24200 }],
  recent: [{ at: new Date().toISOString(), kind: 'Analysis', who: 'Asha Lead', project: 'Kauvery Hospital', model: 'gemini-3.8-flash', inputTokens: 9552, outputTokens: 5075, durationMs: 41000, success: true }],
  ...over,
})

/** In the dialog that asks what the PDF should hold: waits until the headings have arrived (the button is off until then), then presses the button. */
export async function confirmPdf(user: UserEvent, name = 'Make the PDF', timeout = 1000) {
  const button = await screen.findByRole('button', { name }, { timeout })
  await waitFor(() => expect(button).toBeEnabled(), { timeout })
  await user.click(button)
}

/** The headings a request's PDF would have, as the server lists them before the PDF is made: each with its page as printed (a marker where its number goes). */
const page = (title: string, body: string) => `<section><h2><span>\u0001</span>${title}</h2>${body}</section>`
const NO_REQUEST = '00000000-0000-0000-0000-000000000000'          // the server's id for the pages of a request's own document
const heading =(key: string, title: string, detail: string, body: string, flowIds: string[] = [NO_REQUEST]) =>
  ({ key, title, detail, parts: flowIds.map(flowId => ({ flowId, html: page(title, body) })) })
export const outline = (requests: { flowId: string; code: string; title: string; module: string }[] = []) => {
  const ids = requests.length > 0 ? requests.map(r => r.flowId) : [NO_REQUEST]
  return {
    sections: [
      heading('control', 'Document control', 'Document ID, versions and who prepared it', '<p>Document ID IMPL-KMC-001</p>', ids),
      heading('purpose', 'Purpose', 'What the flow is for', '<p>Show the default ward to a super user.</p>', ids),
      heading('requirements', 'Functional requirements', '2 rules, 3 requirements', '<table><tr><td>The ward is pre-selected.</td></tr></table>', ids),
      heading('stages', 'How it works, stage by stage', '4 stages', '<p>Stage 1: Opens the menu</p>', ids),
      heading('signoff', 'Approval and sign-off', 'Names, signatures and dates', '<p>Prepared by Asha Lead</p>', ids),
    ],
    requests,
    style: 'body { font-family: sans-serif }',
    cover: requests.length > 0 ? '' : '<section class="cover"><div class="doctitle">Implementation Document</div></section>',
  }
}
export const standard = {
  'POST /auth/refresh': () => tokens(),
  'GET /admin/overview': () => ({ users: 3, activeUsers: 3, projects: 1, flows: 1, notes: 1, documents: 1, aiProvider: 'gemini', aiReady: true, awaitingVerification: 0, clients: 1 }),
  'GET /flows/f-1/guide-outline': () => outline(),
  'GET /system/info': () => ({ ai: 'gemini', notice: null }),
  'GET /library': () => [],
  'GET /ai-usage': () => usage(),
  'GET /projects': () => [project],
  'GET /clients': () => [client],
  'GET /projects/p-1/departments': () => [dept],
  'GET /projects/p-1/flows': () => [flow],
  'GET /flows/f-1': () => flow,
  'GET /flows/f-1/entries': () => [entry],
  'GET /flows/f-1/figures': () => [],
  'GET /master/languages': () => [{ code: 'en-IN', name: 'English (India)', sttLocale: 'en-IN', isActive: true }, { code: 'ta-IN', name: 'Tamil', sttLocale: 'ta-IN', isActive: true }],
  'GET /projects/p-1/dashboard': () => dashboard,
  'GET /projects/p-1/pending': () => [{ kind: 'OpenQuestions', flowId: 'f-1', title: 'MRD / IP Case Sheet Receive', detail: '3 open question(s), 1 blocking.', since: new Date().toISOString() }],
}

/** A flow's content as the server sends it: a few items in each of the sections the tests use. */
export const content = {
  flowTitle: 'IP Case Sheet Receive', department: 'Medical Records', moduleCode: 'MRD', schemaVersion: '1.0',
  purpose: 'Pre-select the ward for a super user.', scope: 'All IP wards.', trigger: 'A case sheet arrives.',
  mermaid: 'flowchart TD\n  S1["Opens the menu"] --> S2["Picks the ward"]',
  steps: [
    { id: 'S1', actor: 'MRD user', action: 'Opens the menu.', screen: 'Receive', inputs: [], outputs: ['Ward list'], next: ['S2'], sourceEntryIds: ['E1'] },
    { id: 'S2', actor: 'MRD user', action: 'Picks the ward.', screen: 'Receive', inputs: ['Ward'], outputs: [], next: ['END'], sourceEntryIds: [] },
  ],
  businessRules: [{ id: 'BR1', rule: 'A super user sees his own ward first.', appliesTo: ['S1'], sourceEntryIds: ['E1'] }],
  requirements: [{ reqId: 'REQ001', description: 'Pre-select the ward.', category: 'CR', priority: 'High', sourceEntryIds: ['E1'] }],
  useCase: { primaryActors: ['MRD user'], priority: 'High', mainScenario: 'Opens, picks, saves.' },
  actors: [], gaps: [], decisions: [], clarificationQuestions: [{ id: 'Q1', section: 'businessRules', question: 'hidden here', relatedGapId: null }],
  confidence: { steps: 0.9 }, changeLog: [],
}
export const contentDto = (versionNo = 1, over: object = {}) => ({ flowId: 'f-1', versionNo, source: 'Analysis', content, lockedIds: [] as string[], createdAt: new Date().toISOString(), createdByName: 'System', ...over })

export function app(path: string) {
  return render(
    <QueryClientProvider client={makeQueryClient()}>
      <ToastProvider>
        <SessionProvider>
          <MemoryRouter initialEntries={[path]}><AppRoutes /></MemoryRouter>
        </SessionProvider>
      </ToastProvider>
    </QueryClientProvider>,
  )
}

export const signedIn = () => localStorage.setItem('fc.refresh', 'saved-refresh')
