// Walks the whole journey in the real app (Demo AI) and records every API call with its real answer.
// Recording a new set of fixtures (only needed when the app's screens or API change):
//   1. start a throwaway server in Demo AI mode (Ai__Provider=Demo) with a fresh database and run scripts/demo-setup.ps1 against it
//   2. node demo-tools/record.mjs <folder>        (the server is expected on http://localhost:5498)
//   3. fetch the diagram: save GET /api/v1/flows/<id>/diagram?format=svg as <folder>/diagram.svg
//   4. node demo-tools/build-fixtures.mjs <folder>
import { chromium } from 'playwright-core'
import fs from 'node:fs'
const BASE = 'http://localhost:5498'
const OUT = process.argv[2]
fs.mkdirSync(OUT, { recursive: true })
const calls = []
let seq = 0
const browser = await chromium.launch({ channel: 'chrome', headless: true })

async function session(role, email, password, steps) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } })
  const page = await ctx.newPage()
  page.on('response', async r => {
    const u = new URL(r.url())
    if (!u.pathname.startsWith('/api/')) return
    const req = r.request()
    const ct = r.headers()['content-type'] ?? ''
    let body = null, file = null
    try {
      if (/json|text/.test(ct)) body = await r.text()
      else { const buf = await r.body(); file = `bin-${++seq}`; fs.writeFileSync(`${OUT}/${file}`, buf) }
    } catch { /* redirect or empty */ }
    calls.push({ role, method: req.method(), path: u.pathname, query: u.search, post: req.postData() ?? null, status: r.status(), ct, body, file })
  })
  await page.goto(BASE + '/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.waitForURL(u => !u.pathname.startsWith('/login'))
  await page.waitForTimeout(800)
  await steps(page)
  await ctx.close()
}

const step = async (name, fn) => { try { await fn(); console.log('ok  ', name) } catch (e) { console.log('FAIL', name, String(e).split('\n')[0]) } }

await session('user', 'asha@demo.local', 'Demo-Passw0rd-123', async page => {
  await step('home', async () => { await page.waitForTimeout(1200) })
  await step('new project', async () => {
    await page.getByRole('link', { name: 'New project' }).first().click()
    await page.getByLabel('Project name').fill('MRD case sheet receive')
    await page.getByRole('button', { name: 'Create and add notes' }).click()
    await page.getByRole('heading', { name: 'MRD case sheet receive' }).waitFor()
  })
  await step('type a note', async () => {
    await page.getByRole('button', { name: /Type a note/ }).click()
    await page.getByRole('textbox', { name: 'Your note' }).fill('When the case sheet comes to the department the user opens the receive menu and picks the ward. The system lists the case sheets sent from that ward. The user checks each sheet against the list, scans it and saves. If a sheet has missing pages the system shows a warning and the sheet stays pending. A super user gets his own ward pre-selected.')
    await page.getByRole('button', { name: 'Add note' }).click()
    await page.getByText('Typed note').waitFor()
    await page.waitForTimeout(800)
  })
  await step('analyse and make the document', async () => {
    await page.getByRole('button', { name: 'Analyse and make the document' }).click()
    const d = page.getByRole('dialog')
    await d.getByRole('list', { name: 'Headings' }).waitFor({ timeout: 60000 })
    await page.waitForTimeout(1000)
    await d.getByRole('button', { name: 'Make the PDF' }).click()
    await page.getByRole('button', { name: 'Open the PDF' }).waitFor({ timeout: 120000 })
    await page.waitForTimeout(800)
  })
  await step('answer a question', async () => {
    await page.getByRole('button', { name: 'The first mapped ward' }).first().click()
    await page.getByRole('button', { name: 'Save answer' }).first().click()
    await page.waitForTimeout(1000)
  })
  await step('open the pdf (download)', async () => {
    const [pop] = await Promise.all([page.waitForEvent('popup', { timeout: 8000 }).catch(() => null), page.getByRole('button', { name: 'Open the PDF' }).click()])
    await page.waitForTimeout(1500)
    if (pop) await pop.close()
  })
  await step('review and edit', async () => {
    await page.getByRole('link', { name: /Review and edit it/ }).click()
    await page.waitForTimeout(2500)
    await page.goBack()
    await page.waitForTimeout(1000)
  })
  await step('add a module + new request', async () => {
    await page.getByRole('button', { name: 'Add a module' }).click()
    await page.getByLabel('Module to add').selectOption({ label: 'Ward' })
    await page.getByRole('button', { name: 'Add module' }).click()
    await page.waitForTimeout(1200)
  })
  await step('mark final', async () => {
    await page.goto(BASE + '/documents'); await page.waitForTimeout(1500)
    const b = page.getByRole('button', { name: /final/i }).first()
    if (await b.count()) { await b.click(); await page.waitForTimeout(1200) }
  })
  for (const p of ['/', '/documents', '/usage', '/new']) await step('visit ' + p, async () => { await page.goto(BASE + p); await page.waitForTimeout(1500) })
  await step('open the project', async () => { await page.goto(BASE + '/'); await page.getByText('MRD case sheet receive').first().click(); await page.waitForTimeout(1800) })
  await step('account menu', async () => { await page.getByRole('button', { name: /account|Asha/i }).first().click().catch(() => {}); await page.waitForTimeout(500) })
})

await session('admin', 'admin@flowcapture.local', 'ChangeMe-Dev-12345', async page => {
  for (const p of ['/admin', '/admin/final', '/admin/handling', '/admin/clients', '/admin/modules', '/admin/users', '/admin/ai', '/admin/database', '/usage', '/documents', '/'])
    await step('visit ' + p, async () => { await page.goto(BASE + p); await page.waitForTimeout(1800) })
})

await browser.close()
fs.writeFileSync(`${OUT}/calls.json`, JSON.stringify(calls, null, 1))
const uniq = new Map()
for (const c of calls) { const k = `${c.role} ${c.method} ${c.path.replace(/[0-9a-f]{8}-[0-9a-f-]{27}/g, ':id')}`; uniq.set(k, (uniq.get(k) ?? 0) + 1) }
console.log('calls', calls.length, 'distinct', uniq.size)
for (const [k, n] of [...uniq].sort()) console.log(String(n).padStart(3), k)
