// The demo backend: answers the app's /api/v1 calls inside the browser, so the whole app can run on GitHub Pages with no server.
// It behaves like the real server running its built-in Demo AI mode: an analysis returns one fixed example (recorded from the real server,
// see demo-tools/), everything else (projects, notes, questions, versions, documents, final / verify, admin pages) really changes the data,
// which is kept in this browser (localStorage) until "Reset demo".
import fixtures from './fixtures.json'
import { composePreview } from '../components/ChooseContent'
import type { Clarification, DocumentStage, Entry, GuideChoice, GuideOutline } from '../api/types'

// ---------------------------------------------------------------------------------------------- state

interface User { id: string; email: string; displayName: string; roles: string[]; isActive: boolean; password: string; preferredLanguage: string | null }
interface Client { id: string; name: string; location: string; isActive: boolean; userIds: string[] }
interface Module { id: string; code: string; name: string; isActive: boolean }
interface Project { id: string; code: string; hospitalName: string; location: string; startDate: string; leadUserId: string; status: string; monthlyAiTokenLimit: number | null; clientId: string; deleted?: boolean; rowVersion: string }
interface Dept { id: string; projectId: string; moduleCode: string; name: string; isCustom: boolean }
interface FlowRec { id: string; projectId: string; deptId: string; menuName: string; kind: 'NR' | 'CR' | 'BUG'; requestNo: number; createdAt: string; lastActivityAt: string; deleted?: boolean; rowVersion: string }
interface Content { versionNo: number; source: string; createdAt: string; createdByName: string; content: Record<string, unknown>; lockedIds: string[]; changeNote: string | null }
interface Run { id: string; flowId: string; mode: string; status: 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'; createdAt: string; startedAt: string | null; finishedAt: string | null; resultVersionNo: number | null; due: number; byId: string }
interface Ver {
  versionNo: number; generatedAt: string; by: string; sourceVersion: number; choice: GuideChoice | null; stage: DocumentStage
  finalizedAt: string | null; finalizedBy: string | null; finalNote: string | null; reviewNote: string | null; verifiedBy: string | null; verifiedAt: string | null
}
interface Doc { id: string; kind: 'Guide' | 'ModuleGuide' | 'ProjectGuide'; docNumber: string; title: string; projectId: string; flowId: string | null; moduleId: string | null; versions: Ver[] }
interface Job { jobId: string; kind: string; createdAt: string; due: number; docId: string; choice: GuideChoice | null; byId: string; state: 'Queued' | 'Running' | 'Succeeded'; done: boolean; scope: { flowId?: string; projectId?: string; moduleId?: string } }
interface Use { at: string; kind: string; who: string; whoId: string; project: string; projectId: string }

export interface DemoState {
  v: 1
  users: User[]; clients: Client[]; modules: Module[]
  projects: Project[]; depts: Dept[]; flows: FlowRec[]
  entries: Entry[]; deletedEntries: string[]
  contents: Record<string, Content[]>
  clar: Clarification[]
  runs: Run[]; docs: Doc[]; jobs: Job[]; usage: Use[]
}

export class HttpError extends Error {
  constructor(public status: number, public detail: string, public code?: string, public errors?: Record<string, string[]>) { super(detail) }
}
const fail = (status: number, detail: string, code?: string): never => { throw new HttpError(status, detail, code) }
const notInDemo = (what: string): never => fail(422, `${what} needs the real server and its AI, so it is switched off in this demo.`, 'not_in_demo')

export const DEMO_ACCOUNTS = {
  consultant: { email: 'asha@demo.local', password: 'Demo-Passw0rd-123', label: 'Consultant' },
  admin: { email: 'admin@demo.local', password: 'Demo-Admin-123', label: 'Administrator' },
}
const STORE_KEY = 'fc.demo.state.v1'

let clockOffset = 0
const nowMs = () => Date.now() + clockOffset
const iso = (ms = nowMs()) => new Date(ms).toISOString()
export const uid = (): string => (globalThis.crypto?.randomUUID?.() ?? 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 3) | 8).toString(16) }))
const stamp = () => uid().replace(/-/g, '')
const clone = <T,>(x: T): T => JSON.parse(JSON.stringify(x)) as T
const pad = (n: number) => String(n).padStart(3, '0')
const norm = (s: string) => s.trim().replace(/\s+/g, ' ').toLowerCase()

// ---------------------------------------------------------------------------------------------- seed

function seed(): DemoState {
  const asha: User = { id: uid(), email: DEMO_ACCOUNTS.consultant.email, displayName: 'Asha Kumar', roles: ['ImplementationLead'], isActive: true, password: DEMO_ACCOUNTS.consultant.password, preferredLanguage: null }
  const ravi: User = { id: uid(), email: 'ravi@demo.local', displayName: 'Ravi Shankar', roles: ['ImplementationLead'], isActive: true, password: DEMO_ACCOUNTS.consultant.password, preferredLanguage: null }
  const admin: User = { id: uid(), email: DEMO_ACCOUNTS.admin.email, displayName: 'Demo Administrator', roles: ['Admin'], isActive: true, password: DEMO_ACCOUNTS.admin.password, preferredLanguage: null }
  const group: Client = { id: uid(), name: 'Demo Hospital Group', location: 'Chennai', isActive: true, userIds: [asha.id, ravi.id] }
  const city: Client = { id: uid(), name: 'City Care Hospital', location: 'Pune', isActive: true, userIds: [ravi.id] }
  const s: DemoState = {
    v: 1, users: [asha, ravi, admin], clients: [group, city],
    modules: (fixtures.modules as { code: string; name: string }[]).map(m => ({ id: uid(), code: m.code, name: m.name, isActive: true })),
    projects: [], depts: [], flows: [], entries: [], deletedEntries: [], contents: {}, clar: [], runs: [], docs: [], jobs: [], usage: [],
  }
  // a finished example, made the way a consultant would, a few hours ago: notes, an analysis, one answered question, a document marked final
  const t0 = clockOffset
  clockOffset = -3 * 3600_000
  const p = createProject(s, asha, 'IP case sheet receive', group.id, 'Receiving case sheets from the wards, as the MRD team does it.')
  const flow = s.flows.find(f => f.projectId === p.id)!
  flow.menuName = 'Receive case sheets from the ward'
  addEntry(s, asha, flow.id, 'When the case sheet comes to the department the user opens the receive menu and picks the ward. The system lists the case sheets sent from that ward. The user checks each sheet against the list, scans it and saves. If a sheet has missing pages the system shows a warning and the sheet stays pending. A super user gets his own ward pre-selected.')
  clockOffset = -3 * 3600_000 + 5 * 60_000
  const run = startAnalysis(s, asha, flow.id, 'Initial'); run.due = 0
  tick(s)
  const q1 = s.clar.find(c => c.flowId === flow.id && c.questionKey === 'Q1'); if (q1) { q1.status = 'Answered'; q1.answer = q1.options?.[0] ?? 'The first mapped ward' }
  clockOffset = -2 * 3600_000
  const job = startGuide(s, asha, { flowId: flow.id }, null); job.due = 0
  tick(s)
  const doc = s.docs[0]; const ver = doc.versions[0]
  ver.stage = 'Final'; ver.finalizedAt = iso(); ver.finalizedBy = asha.displayName; ver.finalNote = 'Checked with the MRD in-charge.'
  clockOffset = t0
  return s
}

// ---------------------------------------------------------------------------------------------- store

let S: DemoState | null = null
function state(): DemoState {
  if (S) return S
  try { const raw = localStorage.getItem(STORE_KEY); if (raw) { const x = JSON.parse(raw) as DemoState; if (x.v === 1) { S = x; return S } } } catch { /* storage blocked: the demo lives in memory */ }
  S = seed()
  saveNow()                              // at once: a reload right after the first call must find the same people and ids
  return S
}
let saving: ReturnType<typeof setTimeout> | undefined
function saveNow() {
  if (saving) { clearTimeout(saving); saving = undefined }
  try { if (S) localStorage.setItem(STORE_KEY, JSON.stringify(S)) } catch { /* too big or blocked: the demo keeps working from memory */ }
}
/** Saves soon (a burst of calls is saved once); a page that closes or reloads in the meantime saves first. */
function persist() {
  if (saving) return
  saving = setTimeout(saveNow, 150)
  if (typeof window !== 'undefined' && !flushHooked) { flushHooked = true; window.addEventListener('pagehide', saveNow); document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') saveNow() }) }
}
let flushHooked = false
export function resetDemoData() {
  S = null
  if (saving) { clearTimeout(saving); saving = undefined }
  try { localStorage.removeItem(STORE_KEY) } catch { /* nothing to remove */ }
}

// ---------------------------------------------------------------------------------------------- domain

const userById = (s: DemoState, id: string) => s.users.find(u => u.id === id)
const isAdmin = (u: User) => u.roles.includes('Admin')
const clientsOf = (s: DemoState, u: User) => (isAdmin(u) ? s.clients : s.clients.filter(c => c.isActive && c.userIds.includes(u.id)))
const projectsOf = (s: DemoState, u: User) => {
  const ok = new Set(clientsOf(s, u).map(c => c.id))
  return s.projects.filter(p => !p.deleted && ok.has(p.clientId))
}
function projectFor(s: DemoState, u: User, id: string): Project {
  const p = s.projects.find(x => x.id === id && !x.deleted)
  if (!p || !projectsOf(s, u).includes(p)) return fail(404, 'That project was not found.')
  return p
}
function flowFor(s: DemoState, u: User, id: string): FlowRec {
  const f = s.flows.find(x => x.id === id && !x.deleted)
  if (!f) return fail(404, 'That request was not found.')
  projectFor(s, u, f.projectId)
  return f
}
const liveEntries = (s: DemoState, flowId: string) => s.entries.filter(e => e.flowId === flowId && !e.isDeleted)
const latest = (s: DemoState, flowId: string) => s.contents[flowId]?.[0]

function projectCode(name: string, s: DemoState): string {
  const letters = name.replace(/[^A-Za-z0-9]/g, '').toUpperCase().slice(0, 4).padEnd(4, 'X')
  for (;;) { const code = `${letters}-${Math.floor(1000 + Math.random() * 9000)}`; if (!s.projects.some(p => p.code === code)) return code }
}

function createProject(s: DemoState, u: User, name: string, clientId: string, _about?: string): Project {
  const n = name.trim()
  if (!n) fail(400, 'Give the project a name.', 'validation')
  const client = clientsOf(s, u).find(c => c.id === clientId)
  if (!client) fail(422, 'Choose one of your clients.', 'client_not_available')
  const p: Project = { id: uid(), code: projectCode(n, s), hospitalName: n, location: '', startDate: iso().slice(0, 10), leadUserId: u.id, status: 'Active', monthlyAiTokenLimit: null, clientId, rowVersion: stamp() }
  s.projects.push(p)
  const d: Dept = { id: uid(), projectId: p.id, moduleCode: 'GEN', name: 'General', isCustom: true }
  s.depts.push(d)
  createFlow(s, d, n, 'NR')
  return p
}

function createFlow(s: DemoState, d: Dept, menuName: string, kind: 'NR' | 'CR' | 'BUG'): FlowRec {
  const name = menuName.trim()
  if (!name) fail(400, 'Give the request a name.', 'validation')
  if (s.flows.some(f => !f.deleted && f.deptId === d.id && norm(f.menuName) === norm(name))) fail(409, `This module already has a request called "${name}".`, 'duplicate_request')
  const no = s.flows.filter(f => f.deptId === d.id && f.kind === kind).length + 1
  const f: FlowRec = { id: uid(), projectId: d.projectId, deptId: d.id, menuName: name, kind, requestNo: no, createdAt: iso(), lastActivityAt: iso(), rowVersion: stamp() }
  s.flows.push(f)
  return f
}

function addEntry(s: DemoState, u: User, flowId: string, text: string, id = uid(), capturedAt = iso()): Entry {
  const f = s.flows.find(x => x.id === flowId)!
  const e: Entry = {
    id, flowId, refNo: s.entries.filter(x => x.flowId === flowId).length + 1, kind: 'Text', capturedAt, capturedBy: u.id, capturedByName: u.displayName, language: 'en-IN',
    rawText: text, notes: null, intervieweeName: null, intervieweeRole: null, audioState: 'None', audioDurationSec: null, transcriptState: 'None', isDeleted: false, rowVersion: stamp(),
  }
  s.entries.push(e)
  f.lastActivityAt = iso()
  return e
}

function startAnalysis(s: DemoState, u: User, flowId: string, mode: string): Run {
  if (liveEntries(s, flowId).length === 0) fail(422, 'Add at least one note before analysing.', 'no_notes')
  if (s.runs.some(r => r.flowId === flowId && (r.status === 'Queued' || r.status === 'Running'))) fail(409, 'An analysis of this request is already running.', 'analysis_running')
  const r: Run = { id: uid(), flowId, mode, status: 'Queued', createdAt: iso(), startedAt: null, finishedAt: null, resultVersionNo: null, due: nowMs() + 1500, byId: u.id }
  s.runs.unshift(r)
  return r
}

function pushContent(s: DemoState, flowId: string, source: string, by: string, content: Record<string, unknown>, locked: string[], note: string | null): Content {
  const list = (s.contents[flowId] ??= [])
  const c: Content = { versionNo: (list[0]?.versionNo ?? 0) + 1, source, createdAt: iso(), createdByName: by, content: clone(content), lockedIds: locked, changeNote: note }
  list.unshift(c)
  return c
}

function startGuide(s: DemoState, u: User, scope: Job['scope'], choice: GuideChoice | null): Job {
  let doc: Doc | undefined
  if (scope.flowId) {
    const f = s.flows.find(x => x.id === scope.flowId)!
    if (!latest(s, f.id)) fail(422, 'Analyse the notes first: the document is made from the analysed workflow.', 'not_analysed')
    const p = s.projects.find(x => x.id === f.projectId)!
    doc = s.docs.find(d => d.kind === 'Guide' && d.flowId === f.id)
    if (!doc) { doc = { id: uid(), kind: 'Guide', docNumber: `IMPL-${p.code}-${f.kind}-${pad(f.requestNo)}`, title: f.menuName, projectId: p.id, flowId: f.id, moduleId: null, versions: [] }; s.docs.push(doc) }
  } else if (scope.moduleId) {
    const d = s.depts.find(x => x.id === scope.moduleId)!
    const p = s.projects.find(x => x.id === d.projectId)!
    doc = s.docs.find(x => x.kind === 'ModuleGuide' && x.moduleId === d.id)
    if (!doc) { doc = { id: uid(), kind: 'ModuleGuide', docNumber: `IMPL-${p.code}-${d.moduleCode}`, title: `${p.hospitalName}: ${d.name}`, projectId: p.id, flowId: null, moduleId: d.id, versions: [] }; s.docs.push(doc) }
  } else {
    const p = s.projects.find(x => x.id === scope.projectId)!
    doc = s.docs.find(x => x.kind === 'ProjectGuide' && x.projectId === p.id)
    if (!doc) { doc = { id: uid(), kind: 'ProjectGuide', docNumber: `IMPL-${p.code}-FULL`, title: p.hospitalName, projectId: p.id, flowId: null, moduleId: null, versions: [] }; s.docs.push(doc) }
  }
  const j: Job = { jobId: uid(), kind: 'GuideDocument', createdAt: iso(), due: nowMs() + 1300, docId: doc.id, choice, byId: u.id, state: 'Queued', done: false, scope }
  s.jobs.unshift(j)
  return j
}

/** Moves queued work along: an analysis or a document is "running" for a moment, then done. */
function tick(s: DemoState) {
  const t = nowMs()
  for (const r of s.runs) {
    if (r.status !== 'Queued' && r.status !== 'Running') continue
    if (t >= r.due) {
      const f = s.flows.find(x => x.id === r.flowId)!
      const p = s.projects.find(x => x.id === f.projectId)!
      const c = pushContent(s, f.id, 'Analysis', 'Demo AI', fixtures.content.content as Record<string, unknown>, [], null)
      if (!s.clar.some(q => q.flowId === f.id)) {
        for (const q of fixtures.clarifications as { questionKey: string; section: string; question: string; options: string[] | null; isBlocking: boolean }[])
          s.clar.push({ id: uid(), flowId: f.id, questionKey: q.questionKey, section: q.section, question: q.question, options: q.options, status: 'Open', isBlocking: q.isBlocking, answer: null, rowVersion: stamp() })
      }
      r.status = 'Succeeded'; r.startedAt = r.startedAt ?? r.createdAt; r.finishedAt = iso(); r.resultVersionNo = c.versionNo
      f.lastActivityAt = iso()
      s.usage.push({ at: iso(), kind: 'Analysis', who: userById(s, r.byId)?.displayName ?? 'Someone', whoId: r.byId, project: p.hospitalName, projectId: p.id })
    } else if (t >= r.due - 1100) { r.status = 'Running'; r.startedAt = r.startedAt ?? iso() }
  }
  for (const j of s.jobs) {
    if (j.done) continue
    if (t >= j.due) {
      const doc = s.docs.find(d => d.id === j.docId)!
      const by = userById(s, j.byId)?.displayName ?? 'Someone'
      const flow = doc.flowId ? s.flows.find(f => f.id === doc.flowId) : undefined
      doc.versions.unshift({
        versionNo: (doc.versions[0]?.versionNo ?? 0) + 1, generatedAt: iso(), by, sourceVersion: flow ? (latest(s, flow.id)?.versionNo ?? 1) : 1, choice: j.choice, stage: 'Working',
        finalizedAt: null, finalizedBy: null, finalNote: null, reviewNote: null, verifiedBy: null, verifiedAt: null,
      })
      if (flow) flow.lastActivityAt = iso()
      j.done = true; j.state = 'Succeeded'
    } else j.state = t >= j.due - 900 ? 'Running' : 'Queued'
  }
}

// ---------------------------------------------------------------------------------------------- the document

const esc = (x: string) => x.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
const jsonEsc = (x: string) => JSON.stringify(x).slice(1, -1)
const dmy = (ms: number) => { const d = new Date(ms); return `${String(d.getDate()).padStart(2, '0')}-${String(d.getMonth() + 1).padStart(2, '0')}-${d.getFullYear()}` }

/** The drafted document of the sample analysis, written for one request: its project, number and author in place of the recorded ones. */
function fill(template: string, p: Project, f: FlowRec, docNo: string, by: string, at: number): string {
  const v: Record<string, string> = { PROJECT: p.hospitalName, DOCNO: docNo, REQ: `${f.kind}-${pad(f.requestNo)}`, USER: by, DATE: dmy(at), CODE: p.code }
  return template.replace(/\{\{(\w+)\}\}/g, (_m, k: string) => jsonEsc(esc(v[k] ?? '')))
}

function outlineFor(s: DemoState, scope: { flowId?: string; projectId?: string; moduleId?: string }, by: string): GuideOutline {
  const base = JSON.parse(fixtures.outline) as GuideOutline
  const render = (f: FlowRec) => {
    const p = s.projects.find(x => x.id === f.projectId)!
    return JSON.parse(fill(fixtures.outline, p, f, `IMPL-${p.code}-${f.kind}-${pad(f.requestNo)}`, by, nowMs())) as GuideOutline
  }
  if (scope.flowId) return render(s.flows.find(f => f.id === scope.flowId)!)
  const flows = s.flows.filter(f => !f.deleted && (scope.moduleId ? f.deptId === scope.moduleId : f.projectId === scope.projectId) && latest(s, f.id))
  const sections = base.sections.map(sec => ({ ...sec, parts: flows.map(f => ({ flowId: f.id, html: render(f).sections.find(x => x.key === sec.key)!.parts[0].html })) }))
  const requests = flows.map(f => ({ flowId: f.id, code: `${f.kind}-${pad(f.requestNo)}`, title: f.menuName, module: s.depts.find(d => d.id === f.deptId)?.name ?? '' }))
  return { sections, requests, style: base.style, cover: '' }
}

/** The printable page of a document version: the headings that were ticked, in the wording the person gave them. */
function printable(s: DemoState, d: Doc, ver: Ver): string {
  const scope = d.flowId ? { flowId: d.flowId } : d.moduleId ? { moduleId: d.moduleId } : { projectId: d.projectId }
  const outline = outlineFor(s, scope, ver.by)
  const sections = new Set(ver.choice?.sections ?? outline.sections.map(x => x.key))
  const requests = new Set(ver.choice?.flowIds ?? outline.requests.map(x => x.flowId))
  const page = composePreview(outline, sections, requests, ver.choice?.titles ?? {})
  const note = '<div class="demo-note">Demo: this is the printable page of the document. Press <b>Ctrl+P</b> (or <b>Cmd+P</b>) and choose <b>Save as PDF</b> to get the PDF. The real server makes the PDF itself.</div>'
  const css = '.demo-note{position:sticky;top:0;z-index:5;background:#fff7e6;color:#5b3b00;border-bottom:1px solid #e8c77a;padding:8px 14px;font:14px/1.4 system-ui,sans-serif}@media print{.demo-note{display:none}}'
  return page.replace('</style>', `${css}</style>`).replace('<body>', `<body>${note}`)
}

async function svgToPng(svg: string): Promise<Blob> {
  const img = new Image()
  const url = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
  await new Promise<void>((ok, bad) => { img.onload = () => ok(); img.onerror = () => bad(new Error('The picture could not be made.')); img.src = url })
  const scale = 2
  const canvas = document.createElement('canvas')
  canvas.width = Math.max(1, Math.round((img.naturalWidth || 600) * scale)); canvas.height = Math.max(1, Math.round((img.naturalHeight || 800) * scale))
  const g = canvas.getContext('2d')!
  g.fillStyle = '#ffffff'; g.fillRect(0, 0, canvas.width, canvas.height); g.drawImage(img, 0, 0, canvas.width, canvas.height)
  return new Promise((ok, bad) => canvas.toBlob(b => (b ? ok(b) : bad(new Error('The picture could not be made.'))), 'image/png'))
}

// ---------------------------------------------------------------------------------------------- shapes the app expects

const profile = (s: DemoState, u: User) => ({
  id: u.id, email: u.email, displayName: u.displayName, roles: u.roles,
  projects: isAdmin(u) ? [] : projectsOf(s, u).map(p => ({ projectId: p.id, projectCode: p.code, projectRole: 'Lead' })),
})
const tokens = (s: DemoState, u: User) => ({ accessToken: `demo.${u.id}`, accessTokenExpiresAt: iso(nowMs() + 3600_000), refreshToken: `demo-refresh.${u.id}`, user: profile(s, u) })
const projectDto = (s: DemoState, p: Project) => ({
  id: p.id, code: p.code, hospitalName: p.hospitalName, location: p.location, startDate: p.startDate, leadUserId: p.leadUserId, leadName: userById(s, p.leadUserId)?.displayName ?? '',
  status: p.status, monthlyAiTokenLimit: p.monthlyAiTokenLimit, rowVersion: p.rowVersion, clientId: p.clientId, clientName: s.clients.find(c => c.id === p.clientId)?.name ?? null,
})
const clientDto = (s: DemoState, c: Client) => ({ id: c.id, name: c.name, location: c.location, isActive: c.isActive, projectCount: s.projects.filter(p => !p.deleted && p.clientId === c.id).length, userIds: [...c.userIds] })
const flowDto = (s: DemoState, f: FlowRec) => {
  const d = s.depts.find(x => x.id === f.deptId)!
  const c = latest(s, f.id)
  return {
    id: f.id, projectId: f.projectId, projectDepartmentId: f.deptId, moduleCode: d.moduleCode, departmentName: d.name, menuName: f.menuName, status: c ? 'Analysed' : 'Draft',
    currentVersionNo: c?.versionNo ?? 0, confirmedVersionNo: null, entryCount: liveEntries(s, f.id).length, openQuestionCount: s.clar.filter(q => q.flowId === f.id && q.status === 'Open').length,
    lastActivityAt: f.lastActivityAt, rowVersion: f.rowVersion, kind: f.kind, requestNo: f.requestNo, requestCode: `${f.kind}-${pad(f.requestNo)}`,
  }
}
const deptDto = (s: DemoState, d: Dept) => {
  const fs = s.flows.filter(f => !f.deleted && f.deptId === d.id)
  return { id: d.id, moduleCode: d.moduleCode, name: d.name, isCustom: d.isCustom, assignees: [], flowCount: fs.length, entryCount: fs.reduce((n, f) => n + liveEntries(s, f.id).length, 0), confirmedCount: 0 }
}
const runDto = (r: Run) => ({
  id: r.id, scope: 'Part', mode: r.mode, status: r.status, flowId: r.flowId, model: r.status === 'Succeeded' ? 'demo' : 'demo', inputTokens: r.status === 'Succeeded' ? 0 : null, outputTokens: r.status === 'Succeeded' ? 0 : null,
  durationMs: r.finishedAt ? Date.parse(r.finishedAt) - Date.parse(r.createdAt) : null, attempts: r.status === 'Queued' ? 0 : 1, errorCode: null, errorMessage: null, resultVersionNo: r.resultVersionNo, warnings: [], result: null,
  startedAt: r.startedAt, finishedAt: r.finishedAt, createdAt: r.createdAt,
})
const approval = (st: DocumentStage) => (st === 'Verified' ? 'Approved' : st === 'Returned' ? 'Rejected' : 'Pending')
const versionDto = (v: Ver) => ({
  versionNo: v.versionNo, generatedAt: v.generatedAt, generatedByName: v.by, approvalStatus: approval(v.stage), templateVersion: 'implementation document', sourceFlowVersionNos: [v.sourceVersion],
  approvedByName: v.verifiedBy, approvalNote: v.reviewNote, finalizedAt: v.finalizedAt, finalizedByName: v.finalizedBy, finalNote: v.finalNote,
})
const docDto = (d: Doc) => ({ id: d.id, docNumber: d.docNumber, title: d.title, scope: 'Guide', flowId: d.flowId, currentVersionNo: d.versions[0]?.versionNo ?? 0, versions: d.versions.map(versionDto) })
function libraryDto(s: DemoState, d: Doc) {
  const v = d.versions[0]; const p = s.projects.find(x => x.id === d.projectId)!; const c = s.clients.find(x => x.id === p.clientId)
  return {
    id: d.id, docNumber: d.docNumber, title: d.title, kind: d.kind, projectId: p.id, projectName: p.hospitalName, flowId: d.flowId, versionNo: v.versionNo, createdAt: v.generatedAt, createdByName: v.by,
    formats: ['pdf'], hasDiagram: d.kind === 'Guide', moduleId: d.moduleId, stage: v.stage, finalizedAt: v.finalizedAt, finalizedByName: v.finalizedBy, finalNote: v.finalNote,
    reviewNote: v.reviewNote, verifiedByName: v.verifiedBy, verifiedAt: v.verifiedAt, clientId: c?.id ?? null, clientName: c?.name ?? null,
  }
}
function diff(a: Content, b: Content) {
  const items: { section: string; itemId: string; change: string; before: unknown; after: unknown }[] = []
  const keys = new Set([...Object.keys(a.content), ...Object.keys(b.content)])
  for (const k of keys) {
    const x = a.content[k]; const y = b.content[k]
    if (Array.isArray(x) || Array.isArray(y)) {
      const xs = (x as { id?: string }[] | undefined) ?? []; const ys = (y as { id?: string }[] | undefined) ?? []
      const mx = new Map(xs.map((i, n) => [i.id ?? String(n), i])); const my = new Map(ys.map((i, n) => [i.id ?? String(n), i]))
      for (const [id, i] of my) { const o = mx.get(id); if (!o) items.push({ section: k, itemId: id, change: 'Added', before: null, after: i }); else if (JSON.stringify(o) !== JSON.stringify(i)) items.push({ section: k, itemId: id, change: 'Changed', before: o, after: i }) }
      for (const [id, i] of mx) if (!my.has(id)) items.push({ section: k, itemId: id, change: 'Removed', before: i, after: null })
    } else if (JSON.stringify(x) !== JSON.stringify(y)) items.push({ section: k, itemId: k, change: 'Changed', before: x ?? null, after: y ?? null })
  }
  return items
}

function usageSummary(s: DemoState, u: User, month?: string) {
  const all = isAdmin(u)
  const m = month && /^\d{4}-\d{2}$/.test(month) ? month : iso().slice(0, 7)
  const mine = s.usage.filter(x => (all || x.whoId === u.id) && x.at.slice(0, 7) === m)
  const tot = (n: number) => ({ calls: n, inputTokens: 0, outputTokens: 0, totalTokens: 0 })
  const today = mine.filter(x => x.at.slice(0, 10) === iso().slice(0, 10)).length
  const days = new Date(Number(m.slice(0, 4)), Number(m.slice(5, 7)), 0).getDate()
  const group = (key: (x: Use) => [string | null, string]) => { const g = new Map<string, { id: string | null; name: string; calls: number; totalTokens: number }>(); for (const x of mine) { const [id, name] = key(x); const e = g.get(name) ?? { id, name, calls: 0, totalTokens: 0 }; e.calls++; g.set(name, e) } return [...g.values()] }
  return {
    month: m, allUsers: all, provider: 'Demo', totals: tot(mine.length), today: tot(today), lastMonth: tot(0),
    byKind: mine.length ? [{ kind: 'Analysis', calls: mine.length, inputTokens: 0, outputTokens: 0, totalTokens: 0 }] : [],
    byDay: Array.from({ length: days }, (_v, i) => { const day = `${m}-${String(i + 1).padStart(2, '0')}`; return { day, calls: mine.filter(x => x.at.slice(0, 10) === day).length, totalTokens: 0 } }),
    byUser: all ? group(x => [x.whoId, x.who]) : [], byProject: group(x => [x.projectId, x.project]),
    recent: [...mine].reverse().slice(0, 20).map(x => ({ at: x.at, kind: x.kind, who: x.who, project: x.project, model: 'demo', inputTokens: 0, outputTokens: 0, durationMs: 1200, success: true })),
    byModel: [], quotas: [], modelQuotas: [],
  }
}

// ---------------------------------------------------------------------------------------------- routes

interface Ctx { s: DemoState; user: User | null; method: string; q: URLSearchParams; body: any }   // eslint-disable-line @typescript-eslint/no-explicit-any
/** A file to send back: text (the document page, the diagram) or something made on demand (the PNG picture). */
export class FileResult { constructor(public body: string | (() => Promise<Blob>), public type: string, public fileName?: string) { /* nothing else */ } }
type Handler = (c: Ctx, ...m: string[]) => unknown
const routes: { method: string; re: RegExp; h: Handler }[] = []
const on = (method: string, pattern: string, h: Handler) => routes.push({ method, re: new RegExp(`^${pattern.replace(/:id/g, '([0-9a-zA-Z-]+)').replace(/:n/g, '(\\d+)')}$`), h })
const me = (c: Ctx): User => c.user ?? fail(401, 'Sign in to continue.')
const adminOnly = (c: Ctx): User => { const u = me(c); if (!isAdmin(u)) fail(403, 'Only the administrator can do that.'); return u }
const touch = (x: { rowVersion: string }) => { x.rowVersion = stamp() }

// --- account
on('POST', 'auth/login', c => {
  const email = String(c.body?.email ?? '').trim().toLowerCase()
  const u = c.s.users.find(x => x.email.toLowerCase() === email && x.password === c.body?.password && x.isActive)
  if (!u) fail(401, 'The email or password is not right.', 'bad_credentials')
  return tokens(c.s, u!)
})
on('POST', 'auth/refresh', c => {
  const u = userById(c.s, String(c.body?.refreshToken ?? '').replace('demo-refresh.', ''))
  if (!u || !u.isActive) fail(401, 'Sign in again.', 'bad_refresh')
  return tokens(c.s, u!)
})
on('POST', 'auth/logout', () => undefined)
on('GET', 'auth/me', c => profile(c.s, me(c)))
on('POST', 'auth/change-password', c => {
  const u = me(c)
  if (u.password !== c.body?.currentPassword) fail(422, 'The current password is not right.', 'bad_password')
  if (String(c.body?.newPassword ?? '').length < 8) fail(422, 'The new password needs at least 8 characters.', 'weak_password')
  u.password = String(c.body.newPassword)
  return tokens(c.s, u)
})

// --- reference data
on('GET', 'system/info', () => ({ ai: 'demo', notice: 'Demo: this runs in your browser with sample data. The analysis returns one fixed example, like the real app in its demo mode. Nothing is sent anywhere.', allowMedia: false, capacity: null }))
on('GET', 'master/departments', c => { me(c); return c.s.modules.map(m => ({ id: m.id, code: m.code, name: m.name, isActive: m.isActive })) })
on('GET', 'master/languages', () => fixtures.languages)
on('GET', 'master/requirement-categories', () => fixtures.categories)
on('GET', 'clients', c => clientsOf(c.s, me(c)).map(x => clientDto(c.s, x)))
on('GET', 'users', c => { adminOnly(c); return c.s.users.map(u => ({ id: u.id, email: u.email, displayName: u.displayName, isActive: u.isActive, roles: u.roles, preferredLanguage: u.preferredLanguage })) })
on('GET', 'projects', c => projectsOf(c.s, me(c)).map(p => projectDto(c.s, p)))
on('GET', 'ai-usage', c => usageSummary(c.s, me(c), c.q.get('month') ?? undefined))
on('GET', 'audit', c => { me(c); return { items: [], page: 1, pageSize: 100, total: 0 } })
on('GET', 'library', c => {
  const u = me(c); const visible = new Set(projectsOf(c.s, u).map(p => p.id))
  return c.s.docs.filter(d => visible.has(d.projectId) && d.versions.length > 0 && (!isAdmin(u) || d.versions[0].stage !== 'Working')).map(d => libraryDto(c.s, d))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

// --- projects
on('POST', 'projects/quick', c => {
  const u = me(c); const p = createProject(c.s, u, String(c.body?.name ?? ''), String(c.body?.clientId ?? ''), c.body?.description)
  const d = c.s.depts.find(x => x.projectId === p.id)!; const f = c.s.flows.find(x => x.projectId === p.id)!
  return { projectId: p.id, departmentId: d.id, flowId: f.id, name: p.hospitalName, code: p.code }
})
on('PUT', 'projects/:id', (c, id) => {
  const p = projectFor(c.s, me(c), id); const n = String(c.body?.hospitalName ?? '').trim()
  if (!n) fail(400, 'Give the project a name.', 'validation')
  p.hospitalName = n; touch(p); return projectDto(c.s, p)
})
on('DELETE', 'projects/:id', (c, id) => { projectFor(c.s, me(c), id).deleted = true; return undefined })
on('GET', 'projects/:id/flows', (c, id) => { projectFor(c.s, me(c), id); return c.s.flows.filter(f => !f.deleted && f.projectId === id).map(f => flowDto(c.s, f)) })
on('GET', 'projects/:id/departments', (c, id) => { projectFor(c.s, me(c), id); return c.s.depts.filter(d => d.projectId === id).map(d => deptDto(c.s, d)) })
on('POST', 'projects/:id/departments', (c, id) => {
  projectFor(c.s, me(c), id)
  const m = c.s.modules.find(x => x.id === c.body?.departmentId && x.isActive)
  if (!m) fail(422, 'Choose a module from the list. Modules are created by the administrator.', 'module_not_available')
  if (c.s.depts.some(d => d.projectId === id && d.moduleCode === m!.code)) fail(409, `${m!.name} is already in this project.`, 'module_exists')
  const d: Dept = { id: uid(), projectId: id, moduleCode: m!.code, name: m!.name, isCustom: false }
  c.s.depts.push(d); return deptDto(c.s, d)
})
on('POST', 'projects/:id/flows', (c, id) => {
  projectFor(c.s, me(c), id)
  const d = c.s.depts.find(x => x.id === c.body?.projectDepartmentId && x.projectId === id) ?? fail(404, 'That module was not found.')
  const kind = c.body?.kind ?? 'NR'
  if (!['NR', 'CR', 'BUG'].includes(kind)) fail(422, 'A request is a new requirement (NR), a change request (CR) or a bug (BUG).', 'bad_kind')
  return flowDto(c.s, createFlow(c.s, d, String(c.body?.menuName ?? ''), kind))
})
on('GET', 'projects/:id/dashboard', (c, id) => {
  projectFor(c.s, me(c), id)
  const fs = c.s.flows.filter(f => !f.deleted && f.projectId === id)
  const analysed = fs.filter(f => latest(c.s, f.id)).length
  const open = c.s.clar.filter(q => fs.some(f => f.id === q.flowId) && q.status === 'Open')
  return {
    projectId: id, departmentsTotal: c.s.depts.filter(d => d.projectId === id).length, flowsTotal: fs.length, captured: fs.filter(f => liveEntries(c.s, f.id).length > 0).length, analysed, confirmed: 0,
    documented: c.s.docs.filter(d => d.projectId === id).length, openClarifications: open.length, blockingClarifications: open.filter(q => q.isBlocking).length, lastActivityAt: fs[0]?.lastActivityAt ?? null,
    departments: [], aiUsage: { month: iso().slice(0, 7), inputTokens: 0, outputTokens: 0, estimatedCost: 0, limit: null, runs: c.s.usage.filter(x => x.projectId === id).length },
  }
})
on('GET', 'projects/:id/pending', () => [])
on('GET', 'projects/:id/cross-links', () => [])
on('GET', 'projects/:id/guide-outline', (c, id) => { projectFor(c.s, me(c), id); return outlineFor(c.s, { projectId: id }, me(c).displayName) })
on('GET', 'projects/:id/modules/:id/guide-outline', (c, id, mid) => { projectFor(c.s, me(c), id); return outlineFor(c.s, { moduleId: mid }, me(c).displayName) })
on('POST', 'projects/:id/guide', (c, id) => { projectFor(c.s, me(c), id); return jobDto(startGuide(c.s, me(c), { projectId: id }, choiceOf(c.body))) })
on('POST', 'projects/:id/modules/:id/guide', (c, id, mid) => { projectFor(c.s, me(c), id); return jobDto(startGuide(c.s, me(c), { moduleId: mid }, choiceOf(c.body))) })

// --- one request
on('GET', 'flows/:id', (c, id) => flowDto(c.s, flowFor(c.s, me(c), id)))
on('PUT', 'flows/:id', (c, id) => {
  const f = flowFor(c.s, me(c), id); const name = String(c.body?.menuName ?? '').trim()
  if (!name) fail(400, 'Give the request a name.', 'validation')
  if (c.s.flows.some(x => x.id !== f.id && !x.deleted && x.deptId === f.deptId && norm(x.menuName) === norm(name))) fail(409, `This module already has a request called "${name}".`, 'duplicate_request')
  f.menuName = name; touch(f)
  for (const d of c.s.docs) if (d.flowId === f.id) d.title = name
  return flowDto(c.s, f)
})
on('DELETE', 'flows/:id', (c, id) => { flowFor(c.s, me(c), id).deleted = true; return undefined })
on('GET', 'flows/:id/entries', (c, id) => { flowFor(c.s, me(c), id); return liveEntries(c.s, id) })
on('POST', 'flows/:id/entries', (c, id) => {
  const u = me(c); flowFor(c.s, u, id)
  const text = String(c.body?.rawText ?? '').trim()
  if (!text) fail(400, 'Write something in the note.', 'validation')
  return addEntry(c.s, u, id, text, c.body?.id ?? uid(), c.body?.capturedAt ?? iso())
})
on('PUT', 'entries/:id', (c, id) => {
  const e = c.s.entries.find(x => x.id === id && !x.isDeleted) ?? fail(404, 'That note was not found.')
  flowFor(c.s, me(c), e!.flowId)
  const text = String(c.body?.rawText ?? '').trim(); if (!text) fail(400, 'Write something in the note.', 'validation')
  e!.rawText = text; touch(e!); return e
})
on('DELETE', 'entries/:id', (c, id) => { const e = c.s.entries.find(x => x.id === id) ?? fail(404, 'That note was not found.'); flowFor(c.s, me(c), e!.flowId); e!.isDeleted = true; return undefined })
on('POST', 'entries/:id/transcribe', () => notInDemo('Reading a recording'))
on('GET', 'entries/:id/transcript', () => fail(404, 'There is no transcript for this note.'))
on('POST', 'flows/:id/images', () => notInDemo('Reading a photo'))
on('POST', 'flows/:id/files', () => notInDemo('Reading a file'))
on('POST', 'audio-uploads', () => notInDemo('Recording'))
on('GET', 'flows/:id/figures', () => [])
on('POST', 'flows/:id/figures', () => notInDemo('Adding a picture'))

on('GET', 'flows/:id/analyses', (c, id) => { flowFor(c.s, me(c), id); return c.s.runs.filter(r => r.flowId === id).map(runDto) })
on('POST', 'flows/:id/analyses', (c, id) => { const u = me(c); flowFor(c.s, u, id); return runDto(startAnalysis(c.s, u, id, String(c.body?.mode ?? 'Initial'))) })
on('POST', 'analysis-runs/:id/cancel', (c, id) => { const r = c.s.runs.find(x => x.id === id) ?? fail(404, 'That analysis was not found.'); if (r!.status === 'Queued' || r!.status === 'Running') { r!.status = 'Cancelled'; r!.finishedAt = iso() } return runDto(r!) })

const contentDto = (f: FlowRec, c: Content) => ({ flowId: f.id, versionNo: c.versionNo, source: c.source, content: c.content, lockedIds: c.lockedIds, createdAt: c.createdAt, createdByName: c.createdByName })
on('GET', 'flows/:id/content', (c, id) => { const f = flowFor(c.s, me(c), id); const v = latest(c.s, id) ?? fail(404, 'This request has not been analysed yet.'); return contentDto(f, v!) })
on('PUT', 'flows/:id/content', (c, id) => {
  const u = me(c); const f = flowFor(c.s, u, id); const cur = latest(c.s, id) ?? fail(404, 'This request has not been analysed yet.')
  if (c.body?.baseVersionNo !== cur!.versionNo) fail(409, 'Someone saved a newer version while you were editing. Reload to see it.', 'version_conflict')
  const v = pushContent(c.s, id, 'Edit', u.displayName, c.body.content ?? {}, c.body.lockedIds ?? [], c.body.changeNote ?? null); f.lastActivityAt = iso()
  return contentDto(f, v)
})
on('GET', 'flows/:id/versions', (c, id) => { flowFor(c.s, me(c), id); return (c.s.contents[id] ?? []).map(v => ({ versionNo: v.versionNo, source: v.source, authorName: v.createdByName, createdAt: v.createdAt, changeSummary: v.changeNote, isConfirmed: false })) })
on('GET', 'flows/:id/versions/:n/diff/:n', (c, id, a, b) => {
  flowFor(c.s, me(c), id)
  const list = c.s.contents[id] ?? []; const x = list.find(v => v.versionNo === Number(a)); const y = list.find(v => v.versionNo === Number(b))
  if (!x || !y) fail(404, 'That version was not found.')
  return { fromVersion: Number(a), toVersion: Number(b), items: diff(x!, y!) }
})
on('POST', 'flows/:id/versions/:n/rollback', (c, id, n) => {
  const u = me(c); const f = flowFor(c.s, u, id); const v = (c.s.contents[id] ?? []).find(x => x.versionNo === Number(n)) ?? fail(404, 'That version was not found.')
  return contentDto(f, pushContent(c.s, id, 'Rollback', u.displayName, v!.content, v!.lockedIds, `Rolled back to version ${n}`))
})
on('GET', 'flows/:id/clarifications', (c, id) => { flowFor(c.s, me(c), id); return c.s.clar.filter(q => q.flowId === id) })
on('PUT', 'clarifications/:id', (c, id) => {
  const q = c.s.clar.find(x => x.id === id) ?? fail(404, 'That question was not found.'); flowFor(c.s, me(c), q!.flowId)
  if (typeof c.body?.answer === 'string') { q!.answer = c.body.answer; q!.status = 'Answered' }
  if (typeof c.body?.isBlocking === 'boolean') q!.isBlocking = c.body.isBlocking
  if (c.body?.status === 'Dismissed') q!.status = 'Dismissed'
  touch(q!); return q
})
on('GET', 'flows/:id/diagram', (c, id) => {
  flowFor(c.s, me(c), id); if (!latest(c.s, id)) fail(404, 'This request has not been analysed yet.')
  const svg = fixtures.svg as string
  return c.q.get('format') === 'png' ? new FileResult(() => svgToPng(svg), 'image/png', 'workflow.png') : new FileResult(svg, 'image/svg+xml', 'workflow.svg')
})

// --- documents
const choiceOf = (b: any): GuideChoice | null => (b && (b.sections || b.flowIds || b.titles) ? { sections: b.sections, flowIds: b.flowIds, titles: b.titles } : null)   // eslint-disable-line @typescript-eslint/no-explicit-any
const jobDto = (j: Job) => ({ jobId: j.jobId, kind: j.kind, state: j.state, percent: j.state === 'Succeeded' ? 100 : j.state === 'Running' ? 50 : null, message: null, resultId: j.state === 'Succeeded' ? j.docId : null, createdAt: j.createdAt })
on('GET', 'flows/:id/guide-outline', (c, id) => { flowFor(c.s, me(c), id); if (!latest(c.s, id)) fail(422, 'Analyse the notes first: the document is made from the analysed workflow.', 'not_analysed'); return outlineFor(c.s, { flowId: id }, me(c).displayName) })
on('POST', 'flows/:id/guide', (c, id) => { const u = me(c); flowFor(c.s, u, id); return jobDto(startGuide(c.s, u, { flowId: id }, choiceOf(c.body))) })
on('GET', 'jobs/:id', (c, id) => { me(c); return jobDto(c.s.jobs.find(j => j.jobId === id) ?? fail(404, 'That job was not found.')) })
on('GET', 'flows/:id/documents', (c, id) => { flowFor(c.s, me(c), id); return c.s.docs.filter(d => d.flowId === id && d.versions.length > 0).map(docDto) })
on('GET', 'documents/:id', (c, id) => { const d = c.s.docs.find(x => x.id === id) ?? fail(404, 'That document was not found.'); projectFor(c.s, me(c), d!.projectId); return docDto(d!) })
on('GET', 'documents/:id/versions/:n/download', (c, id, n) => {
  const d = c.s.docs.find(x => x.id === id) ?? fail(404, 'That document was not found.'); projectFor(c.s, me(c), d!.projectId)
  const v = d!.versions.find(x => x.versionNo === Number(n)) ?? fail(404, 'That version was not found.')
  return new FileResult(printable(c.s, d!, v!), 'text/html', `${d!.docNumber}.html`)
})
function versionOf(c: Ctx, id: string, n: string): { d: Doc; v: Ver } {
  const d = c.s.docs.find(x => x.id === id) ?? fail(404, 'That document was not found.'); const v = d!.versions.find(x => x.versionNo === Number(n)) ?? fail(404, 'That version was not found.')
  return { d: d!, v: v! }
}
on('POST', 'documents/:id/versions/:n/final', (c, id, n) => {
  const u = me(c); const { d, v } = versionOf(c, id, n); projectFor(c.s, u, d.projectId)
  if (v.stage === 'Final' || v.stage === 'Verified') fail(409, 'This version is already marked as final.', 'already_final')
  v.stage = 'Final'; v.finalizedAt = iso(); v.finalizedBy = u.displayName; v.finalNote = c.body?.note ?? null; v.reviewNote = null
  return undefined
})
on('DELETE', 'documents/:id/versions/:n/final', (c, id, n) => {
  const u = me(c); const { d, v } = versionOf(c, id, n); projectFor(c.s, u, d.projectId)
  if (v.stage === 'Verified') fail(409, 'The administrator has verified this version, so it can no longer be changed.', 'verified')
  v.stage = 'Working'; v.finalizedAt = null; v.finalizedBy = null; v.finalNote = null
  return undefined
})
on('POST', 'documents/:id/versions/:n/approve', (c, id, n) => {
  const u = adminOnly(c); const { v } = versionOf(c, id, n)
  if (v.stage !== 'Final') fail(409, 'Only a document marked as final can be verified.', 'not_final')
  if (c.body?.approved) { v.stage = 'Verified'; v.verifiedBy = u.displayName; v.verifiedAt = iso(); v.reviewNote = c.body?.note ?? null }
  else { if (!String(c.body?.note ?? '').trim()) fail(422, 'Say why the document is returned.', 'note_required'); v.stage = 'Returned'; v.reviewNote = c.body.note; v.verifiedBy = u.displayName; v.verifiedAt = iso() }
  return undefined
})

// --- administrator
on('GET', 'admin/overview', c => {
  adminOnly(c); const s = c.s; const live = s.projects.filter(p => !p.deleted); const fl = s.flows.filter(f => !f.deleted && live.some(p => p.id === f.projectId))
  return {
    users: s.users.length, activeUsers: s.users.filter(u => u.isActive).length, projects: live.length, flows: fl.length, notes: s.entries.filter(e => !e.isDeleted && fl.some(f => f.id === e.flowId)).length,
    documents: s.docs.length, aiProvider: 'demo', aiReady: true, awaitingVerification: s.docs.filter(d => d.versions[0]?.stage === 'Final').length, clients: s.clients.length,
  }
})
on('GET', 'admin/handling', c => {
  adminOnly(c); const s = c.s; const rows: Record<string, unknown>[] = []
  const stats = (uId: string | null, cId: string) => {
    const ps = s.projects.filter(p => !p.deleted && p.clientId === cId && (uId === null || p.leadUserId === uId)); const ds = s.docs.filter(d => ps.some(p => p.id === d.projectId))
    const last = ds.map(d => d.versions[0]?.finalizedAt).filter(Boolean).sort().pop() ?? null
    return {
      projects: ps.length, projectsStarted: ps.length, modules: s.depts.filter(d => ps.some(p => p.id === d.projectId)).length, requests: s.flows.filter(f => !f.deleted && ps.some(p => p.id === f.projectId)).length,
      awaitingVerification: ds.filter(d => d.versions[0]?.stage === 'Final').length, verified: ds.filter(d => d.versions[0]?.stage === 'Verified').length, returned: ds.filter(d => d.versions[0]?.stage === 'Returned').length, lastFinalAt: last,
    }
  }
  for (const cl of s.clients) {
    if (cl.userIds.length === 0) rows.push({ clientId: cl.id, clientName: cl.name, clientActive: cl.isActive, userId: null, userName: null, userEmail: null, userActive: false, ...stats(null, cl.id) })
    for (const uid_ of cl.userIds) { const u = userById(s, uid_); if (u) rows.push({ clientId: cl.id, clientName: cl.name, clientActive: cl.isActive, userId: u.id, userName: u.displayName, userEmail: u.email, userActive: u.isActive, ...stats(u.id, cl.id) }) }
  }
  return rows
})
on('GET', 'admin/ai', c => { adminOnly(c); return fixtures.aiAdmin })
on('PUT', 'admin/ai', c => { adminOnly(c); return { ...fixtures.aiAdmin, ...c.body, hasKey: false } })
on('POST', 'admin/ai/test', c => { adminOnly(c); return { ok: true, message: 'The demo AI needs no key: it always answers with its fixed example.', model: null, models: [] } })
on('POST', 'admin/ai/test-groq', c => { adminOnly(c); return { ok: true, message: 'Voice notes are switched off in the demo.', model: null, models: [] } })
on('GET', 'admin/database', c => { adminOnly(c); return { server: 'In this browser', database: 'Demo data (localStorage)', provider: 'Browser demo', usingSavedServer: false, problem: null } })
on('GET', 'admin/database/move', c => { adminOnly(c); return { state: 'idle', step: '', startedAt: null, finishedAt: null, tables: [], notes: [], canUse: false, target: null } })
on('POST', 'admin/database/test', c => { adminOnly(c); return notInDemo('Connecting to a database') })
on('POST', 'admin/database/move', c => { adminOnly(c); return notInDemo('Moving the data to a database') })
on('POST', 'admin/database/use', c => { adminOnly(c); return notInDemo('Changing the database') })
on('DELETE', 'admin/database/use', c => { adminOnly(c); return notInDemo('Changing the database') })

on('POST', 'users', c => {
  adminOnly(c); const email = String(c.body?.email ?? '').trim(); const name = String(c.body?.displayName ?? '').trim()
  if (!email || !name) fail(400, 'Give a name and an email.', 'validation')
  if (String(c.body?.password ?? '').length < 8) fail(422, 'The password needs at least 8 characters.', 'weak_password')
  if (c.s.users.some(u => u.email.toLowerCase() === email.toLowerCase())) fail(409, 'There is already a user with that email.', 'duplicate_email')
  const u: User = { id: uid(), email, displayName: name, roles: c.body.roles?.length ? c.body.roles : ['ImplementationLead'], isActive: true, password: c.body.password, preferredLanguage: null }
  c.s.users.push(u); return { id: u.id, email: u.email, displayName: u.displayName, isActive: true, roles: u.roles, preferredLanguage: null }
})
on('PUT', 'users/:id', (c, id) => {
  adminOnly(c); const u = userById(c.s, id) ?? fail(404, 'That user was not found.')
  if (typeof c.body?.displayName === 'string' && c.body.displayName.trim()) u!.displayName = c.body.displayName.trim()
  if (typeof c.body?.email === 'string' && c.body.email.trim()) u!.email = c.body.email.trim()
  if (typeof c.body?.isActive === 'boolean') u!.isActive = c.body.isActive
  if (Array.isArray(c.body?.roles)) u!.roles = c.body.roles
  return { id: u!.id, email: u!.email, displayName: u!.displayName, isActive: u!.isActive, roles: u!.roles, preferredLanguage: u!.preferredLanguage }
})
on('DELETE', 'users/:id', (c, id) => {
  const a = adminOnly(c); const u = userById(c.s, id) ?? fail(404, 'That user was not found.')
  if (u!.id === a.id) fail(422, 'You cannot delete your own account.', 'self_delete')
  c.s.users = c.s.users.filter(x => x.id !== id); for (const cl of c.s.clients) cl.userIds = cl.userIds.filter(x => x !== id)
  return undefined
})
on('POST', 'users/:id/reset-password', (c, id) => { adminOnly(c); const u = userById(c.s, id) ?? fail(404, 'That user was not found.'); u!.password = 'Demo-Reset-1234'; return { temporaryPassword: 'Demo-Reset-1234' } })

on('POST', 'clients', c => {
  adminOnly(c); const name = String(c.body?.name ?? '').trim(); if (!name) fail(400, 'Give the client a name.', 'validation')
  if (c.s.clients.some(x => norm(x.name) === norm(name))) fail(409, 'There is already a client with that name.', 'duplicate_client')
  const cl: Client = { id: uid(), name, location: String(c.body?.location ?? ''), isActive: true, userIds: [] }; c.s.clients.push(cl); return clientDto(c.s, cl)
})
on('PUT', 'clients/:id', (c, id) => {
  adminOnly(c); const cl = c.s.clients.find(x => x.id === id) ?? fail(404, 'That client was not found.'); const name = String(c.body?.name ?? '').trim()
  if (!name) fail(400, 'Give the client a name.', 'validation')
  if (c.s.clients.some(x => x.id !== id && norm(x.name) === norm(name))) fail(409, 'There is already a client with that name.', 'duplicate_client')
  cl!.name = name; cl!.location = String(c.body?.location ?? ''); if (typeof c.body?.isActive === 'boolean') cl!.isActive = c.body.isActive; return clientDto(c.s, cl!)
})
on('PUT', 'clients/:id/users', (c, id) => {
  adminOnly(c); const cl = c.s.clients.find(x => x.id === id) ?? fail(404, 'That client was not found.')
  cl!.userIds = (c.body?.userIds ?? []).filter((x: string) => userById(c.s, x)); return clientDto(c.s, cl!)
})
const moduleBody = (c: Ctx, existing?: Module) => {
  const name = String(c.body?.name ?? '').trim(); const code = String(c.body?.code ?? existing?.code ?? '').trim().toUpperCase()
  if (!name || !code) fail(400, 'Give the module a name.', 'validation')
  if (c.s.modules.some(m => m.id !== existing?.id && norm(m.name) === norm(name))) fail(409, `There is already a module called "${name}".`, 'duplicate_module')
  return { name, code }
}
on('POST', 'master/departments', c => { adminOnly(c); const { name, code } = moduleBody(c); const m: Module = { id: uid(), code, name, isActive: true }; c.s.modules.push(m); return m })
on('PUT', 'master/departments/:id', (c, id) => {
  adminOnly(c); const m = c.s.modules.find(x => x.id === id) ?? fail(404, 'That module was not found.'); const { name } = moduleBody(c, m)
  m!.name = name; if (typeof c.body?.isActive === 'boolean') m!.isActive = c.body.isActive; return m
})

// ---------------------------------------------------------------------------------------------- the fetch

function problem(e: HttpError): Response {
  return new Response(JSON.stringify({ title: e.detail, detail: e.detail, status: e.status, code: e.code, errors: e.errors }), { status: e.status, headers: { 'Content-Type': 'application/problem+json' } })
}

/** Answers one API call. Returns null when the address is not an API call. */
export async function handleDemoRequest(request: { url: string; method: string; headers?: HeadersInit; body?: unknown }): Promise<Response | null> {
  const url = new URL(request.url, typeof location !== 'undefined' ? location.href : 'http://demo.local/')
  const at = url.pathname.indexOf('/api/v1/')
  if (at < 0) return null
  const path = url.pathname.slice(at + '/api/v1/'.length).replace(/\/$/, '')
  const method = request.method.toUpperCase()
  const auth = new Headers(request.headers).get('Authorization') ?? ''
  const s = state()
  const user = userById(s, auth.replace(/^Bearer demo\./, '')) ?? null
  let body: unknown = request.body
  if (typeof body === 'string') { try { body = body ? JSON.parse(body) : undefined } catch { body = undefined } }
  const route = routes.find(r => r.method === method && r.re.test(path))
  if (!route) return problem(new HttpError(404, 'This part of the app is not available in the demo.', 'not_in_demo'))
  try {
    tick(s)
    const out = await Promise.resolve(route.h({ s, user, method, q: url.searchParams, body }, ...(route.re.exec(path) ?? []).slice(1)))
    tick(s)
    persist()
    if (out instanceof FileResult) {
      const body = typeof out.body === 'function' ? await out.body() : out.body
      return new Response(body, { status: 200, headers: { 'Content-Type': out.type, ...(out.fileName ? { 'Content-Disposition': `attachment; filename="${out.fileName}"` } : {}) } })
    }
    if (out === undefined) return new Response(null, { status: 204 })
    return new Response(JSON.stringify(out), { status: 200, headers: { 'Content-Type': 'application/json' } })
  } catch (e) {
    if (e instanceof HttpError) return problem(e)
    return problem(new HttpError(500, 'The demo hit a problem. Try "Reset demo".', 'demo_error'))
  }
}

/** For tests: run the clock forward so queued work finishes at once. */
export function skipDemoClock(ms: number) { clockOffset += ms }
