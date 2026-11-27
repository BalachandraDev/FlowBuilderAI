import { beforeEach, describe, expect, it } from 'vitest'
import { DEMO_ACCOUNTS, handleDemoRequest, resetDemoData, skipDemoClock } from './backend'
import type { Client, Entry, Flow, GeneratedDocument, GuideOutline, LibraryDocument, Project, QuickProject, TokenResponse } from '../api/types'

// The demo backend answers the app's API calls inside the browser (GitHub Pages has no server). These tests drive it the way the app does.

async function call<T = unknown>(method: string, path: string, body?: unknown, token?: string): Promise<{ status: number; data: T }> {
  const res = await handleDemoRequest({ url: `/api/v1/${path}`, method, headers: token ? { Authorization: `Bearer ${token}` } : {}, body: body === undefined ? undefined : JSON.stringify(body) })
  if (!res) throw new Error('not an API call')
  const text = res.status === 204 ? '' : await res.text()
  let data: unknown = text
  try { data = text ? JSON.parse(text) : undefined } catch { /* a file */ }
  return { status: res.status, data: data as T }
}
async function signIn(who: keyof typeof DEMO_ACCOUNTS): Promise<string> {
  const r = await call<TokenResponse>('POST', 'auth/login', { email: DEMO_ACCOUNTS[who].email, password: DEMO_ACCOUNTS[who].password })
  expect(r.status).toBe(200)
  return r.data.accessToken
}
/** Lets queued work (an analysis, a PDF) finish, the way a few seconds would. */
const later = () => skipDemoClock(10_000)

beforeEach(() => { localStorage.clear(); resetDemoData() })

describe('the browser demo backend', () => {
  it('is not used for anything that is not an API call', async () => {
    expect(await handleDemoRequest({ url: '/assets/app.js', method: 'GET' })).toBeNull()
  })

  it('signs the two demo accounts in, refuses a wrong password, and refuses a call with no sign-in', async () => {
    expect((await call('POST', 'auth/login', { email: DEMO_ACCOUNTS.consultant.email, password: 'wrong' })).status).toBe(401)
    expect((await call('GET', 'projects')).status).toBe(401)
    const login = await call<TokenResponse>('POST', 'auth/login', { email: DEMO_ACCOUNTS.admin.email, password: DEMO_ACCOUNTS.admin.password })
    expect(login.data.user.roles).toContain('Admin')
    const refreshed = await call<TokenResponse>('POST', 'auth/refresh', { refreshToken: login.data.refreshToken })
    expect(refreshed.data.user.id).toBe(login.data.user.id)
  })

  it('starts with a finished example the consultant can open: notes, an analysis, a diagram and a document marked final', async () => {
    const t = await signIn('consultant')
    const projects = (await call<Project[]>('GET', 'projects', undefined, t)).data
    expect(projects.map(p => p.hospitalName)).toContain('IP case sheet receive')
    const flows = (await call<Flow[]>('GET', `projects/${projects[0].id}/flows`, undefined, t)).data
    expect(flows[0]).toMatchObject({ requestCode: 'NR-001', status: 'Analysed', entryCount: 1 })
    expect((await call('GET', `flows/${flows[0].id}/content`, undefined, t)).status).toBe(200)
    const svg = await handleDemoRequest({ url: `/api/v1/flows/${flows[0].id}/diagram`, method: 'GET', headers: { Authorization: `Bearer ${t}` } })
    expect(svg?.headers.get('Content-Type')).toBe('image/svg+xml')
    const lib = (await call<LibraryDocument[]>('GET', 'library', undefined, t)).data
    expect(lib).toHaveLength(1)
    expect(lib[0].stage).toBe('Final')
  })

  it('keeps what a visitor does: a project, a note, an analysis, a document, then final, then verified by the administrator', async () => {
    const t = await signIn('consultant')
    const client = (await call<Client[]>('GET', 'clients', undefined, t)).data[0]
    const quick = (await call<QuickProject>('POST', 'projects/quick', { name: 'Pharmacy issue to ward', description: null, clientId: client.id }, t)).data
    expect(quick.name).toBe('Pharmacy issue to ward')

    // analysing with no note is refused with the reason
    const none = await call<{ detail: string }>('POST', `flows/${quick.flowId}/analyses`, { mode: 'Initial', entryIds: null }, t)
    expect(none.status).toBe(422)
    expect(none.data.detail).toMatch(/note/i)

    const note = await call<Entry>('POST', `flows/${quick.flowId}/entries`, { id: crypto.randomUUID(), kind: 'Text', capturedAt: new Date().toISOString(), language: 'en-IN', rawText: 'The pharmacist picks the ward and issues the items.', notes: null, intervieweeName: null, intervieweeRole: null }, t)
    expect(note.status).toBe(200)
    expect(note.data.refNo).toBe(1)

    const run = (await call<{ status: string }>('POST', `flows/${quick.flowId}/analyses`, { mode: 'Initial', entryIds: null }, t)).data
    expect(run.status).toBe('Queued')
    later()
    expect((await call<{ status: string }[]>('GET', `flows/${quick.flowId}/analyses`, undefined, t)).data[0].status).toBe('Succeeded')
    const flow = (await call<Flow>('GET', `flows/${quick.flowId}`, undefined, t)).data
    expect(flow).toMatchObject({ status: 'Analysed', currentVersionNo: 1, openQuestionCount: 3 })

    // answering a question
    const qs = (await call<{ id: string; status: string }[]>('GET', `flows/${quick.flowId}/clarifications`, undefined, t)).data
    const answered = await call<{ status: string; answer: string }>('PUT', `clarifications/${qs[0].id}`, { answer: 'The first mapped ward' }, t)
    expect(answered.data).toMatchObject({ status: 'Answered', answer: 'The first mapped ward' })

    // the document, with one heading renamed and the text under it written by the person, is made and can be opened
    const job = (await call<{ jobId: string; state: string }>('POST', `flows/${quick.flowId}/guide`, { titles: { purpose: 'Why we are doing this' }, texts: { purpose: 'Written by the consultant.\n- first point' } }, t)).data
    expect(job.state).toBe('Queued')
    later()
    const done = (await call<{ state: string; resultId: string }>('GET', `jobs/${job.jobId}`, undefined, t)).data
    expect(done.state).toBe('Succeeded')
    const docs = (await call<GeneratedDocument[]>('GET', `flows/${quick.flowId}/documents`, undefined, t)).data
    expect(docs[0].docNumber).toMatch(/^IMPL-PHAR-\d{4}-NR-001$/)
    const page = await handleDemoRequest({ url: `/api/v1/documents/${done.resultId}/versions/1/download?format=pdf`, method: 'GET', headers: { Authorization: `Bearer ${t}` } })
    const html = await page!.text()
    expect(html).toContain('Why we are doing this')
    expect(html).toContain('<p>Written by the consultant.</p><ul><li>first point</li></ul>')
    expect(html).toContain('Pharmacy issue to ward')
    expect(html).not.toMatch(/AOSTA|BACKBONE/)

    // the administrator does not see it until it is final
    const admin = await signIn('admin')
    expect((await call<LibraryDocument[]>('GET', 'library', undefined, admin)).data.map(d => d.docNumber)).not.toContain(docs[0].docNumber)
    expect((await call('POST', `documents/${done.resultId}/versions/1/final`, { note: 'Checked' }, t)).status).toBe(204)
    const seen = (await call<LibraryDocument[]>('GET', 'library', undefined, admin)).data.find(d => d.id === done.resultId)
    expect(seen?.stage).toBe('Final')
    // a consultant cannot verify; the administrator can
    expect((await call('POST', `documents/${done.resultId}/versions/1/approve`, { approved: true, note: null }, t)).status).toBe(403)
    expect((await call('POST', `documents/${done.resultId}/versions/1/approve`, { approved: true, note: null }, admin)).status).toBe(204)
    expect((await call<LibraryDocument[]>('GET', 'library', undefined, t)).data.find(d => d.id === done.resultId)?.stage).toBe('Verified')
    // and a verified document cannot be taken back
    expect((await call('DELETE', `documents/${done.resultId}/versions/1/final`, undefined, t)).status).toBe(409)
  })

  it('makes the PDF of a module and of the whole project from the analysed requests', async () => {
    const t = await signIn('consultant')
    const project = (await call<Project[]>('GET', 'projects', undefined, t)).data[0]
    const outline = (await call<GuideOutline>('GET', `projects/${project.id}/guide-outline`, undefined, t)).data
    expect(outline.requests).toHaveLength(1)
    const job = (await call<{ jobId: string }>('POST', `projects/${project.id}/guide`, {}, t)).data
    later()
    const done = (await call<{ state: string; resultId: string }>('GET', `jobs/${job.jobId}`, undefined, t)).data
    expect(done.state).toBe('Succeeded')
    const lib = (await call<LibraryDocument[]>('GET', 'library', undefined, t)).data
    expect(lib.find(d => d.id === done.resultId)?.kind).toBe('ProjectGuide')
  })

  it('applies the rules a user meets: names are unique in a module, a module is picked from the administrator’s list, only the administrator makes modules', async () => {
    const t = await signIn('consultant'); const admin = await signIn('admin')
    const project = (await call<Project[]>('GET', 'projects', undefined, t)).data[0]
    const dept = (await call<{ id: string }[]>('GET', `projects/${project.id}/departments`, undefined, t)).data[0]
    const clash = await call('POST', `projects/${project.id}/flows`, { projectDepartmentId: dept.id, menuName: 'RECEIVE case sheets from the ward', kind: 'NR' }, t)
    expect(clash.status).toBe(409)
    const bug = await call<Flow>('POST', `projects/${project.id}/flows`, { projectDepartmentId: dept.id, menuName: 'Wrong ward shown', kind: 'BUG' }, t)
    expect(bug.data.requestCode).toBe('BUG-001')

    expect((await call('POST', `projects/${project.id}/departments`, { departmentId: crypto.randomUUID() }, t)).status).toBe(422)
    expect((await call('POST', 'master/departments', { name: 'Radiology', code: 'RAD' }, t)).status).toBe(403)
    const mod = await call<{ id: string }>('POST', 'master/departments', { name: 'Radiology', code: 'RAD' }, admin)
    expect(mod.status).toBe(200)
    expect((await call('POST', 'master/departments', { name: 'radiology', code: 'RAD2' }, admin)).status).toBe(409)
    expect((await call('POST', `projects/${project.id}/departments`, { departmentId: mod.data.id }, t)).status).toBe(200)
    expect((await call('POST', `projects/${project.id}/departments`, { departmentId: mod.data.id }, t)).status).toBe(409)
  })

  it('shows a user only the clients they are assigned to, and the administrator everything', async () => {
    const t = await signIn('consultant'); const admin = await signIn('admin')
    expect((await call<Client[]>('GET', 'clients', undefined, t)).data.map(c => c.name)).toEqual(['Demo Hospital Group'])
    expect((await call<Client[]>('GET', 'clients', undefined, admin)).data).toHaveLength(2)
    expect((await call('GET', 'users', undefined, t)).status).toBe(403)
    expect((await call<unknown[]>('GET', 'users', undefined, admin)).data).toHaveLength(3)
    const overview = (await call<{ awaitingVerification: number; projects: number }>('GET', 'admin/overview', undefined, admin)).data
    expect(overview).toMatchObject({ awaitingVerification: 1, projects: 1 })
  })

  it('lets the administrator manage users, and refuses what cannot work without a server', async () => {
    const admin = await signIn('admin'); const t = await signIn('consultant')
    expect((await call('POST', 'users', { email: 'asha@demo.local', displayName: 'Dup', password: 'Long-enough-1', roles: ['ImplementationLead'] }, admin)).status).toBe(409)
    const made = await call<{ id: string }>('POST', 'users', { email: 'new@demo.local', displayName: 'New Person', password: 'Long-enough-1', roles: ['ImplementationLead'] }, admin)
    expect(made.status).toBe(200)
    expect((await call('POST', 'auth/login', { email: 'new@demo.local', password: 'Long-enough-1' })).status).toBe(200)
    expect((await call<{ temporaryPassword: string }>('POST', `users/${made.data.id}/reset-password`, {}, admin)).data.temporaryPassword).toBeTruthy()
    const media = await call<{ detail: string }>('POST', 'flows/abc/images', {}, t)
    expect(media.status).toBe(422)
    expect(media.data.detail).toMatch(/demo/i)
    expect((await call('GET', 'nothing/here', undefined, t)).status).toBe(404)
  })

  it('saves edits as versions, refuses a stale save, and can roll back', async () => {
    const t = await signIn('consultant')
    const project = (await call<Project[]>('GET', 'projects', undefined, t)).data[0]
    const flow = (await call<Flow[]>('GET', `projects/${project.id}/flows`, undefined, t)).data[0]
    const cur = (await call<{ versionNo: number; content: Record<string, unknown> }>('GET', `flows/${flow.id}/content`, undefined, t)).data
    const edited = await call<{ versionNo: number }>('PUT', `flows/${flow.id}/content`, { baseVersionNo: cur.versionNo, content: { ...cur.content, flowTitle: 'My own title' }, lockedIds: [], changeNote: 'title' }, t)
    expect(edited.data.versionNo).toBe(cur.versionNo + 1)
    expect((await call('PUT', `flows/${flow.id}/content`, { baseVersionNo: cur.versionNo, content: cur.content, lockedIds: [] }, t)).status).toBe(409)
    const diff = (await call<{ items: { change: string }[] }>('GET', `flows/${flow.id}/versions/${cur.versionNo}/diff/${edited.data.versionNo}`, undefined, t)).data
    expect(diff.items.length).toBeGreaterThan(0)
    const back = await call<{ versionNo: number; content: { flowTitle: string } }>('POST', `flows/${flow.id}/versions/${cur.versionNo}/rollback`, {}, t)
    expect(back.data.content.flowTitle).toBe(cur.content.flowTitle)
    expect(back.data.versionNo).toBe(cur.versionNo + 2)
  })

  it('keeps the data across a reload until the demo is reset', async () => {
    const t = await signIn('consultant')
    const client = (await call<Client[]>('GET', 'clients', undefined, t)).data[0]
    await call('POST', 'projects/quick', { name: 'Kept across reload', description: null, clientId: client.id }, t)
    await new Promise(r => setTimeout(r, 250))                                    // the save to storage is a moment after the change
    expect(localStorage.getItem('fc.demo.state.v1')).toContain('Kept across reload')
    resetDemoData()
    expect(localStorage.getItem('fc.demo.state.v1')).toBeNull()
  })
})
