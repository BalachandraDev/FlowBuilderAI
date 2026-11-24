import { expect, test, type Page } from '@playwright/test'

// The whole journey a user takes: a project, notes in all three ways (typed, photographed, spoken), analysis, the workflow
// diagram, the implementation document as a PDF, and everybody else finding it in the documents.
// Needs the server's AI in demo mode (Ai__Provider=Demo), which answers with a fixed example, because real analysis needs a key,
// and Chrome's fake microphone (set in playwright.config.ts) for the voice note.
const PASSWORD = process.env.FLOWCAPTURE_DEMO_PASSWORD ?? 'Demo-Passw0rd-123'
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')

async function signIn(page: Page, email: string) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)                              // wait until it has worked, or the next page load would cancel it
}

/** The dialog that asks what the PDF should hold: waits for the headings, optionally unticks some, then makes the PDF. */
async function confirmPdf(page: Page, opts: { untick?: (string | RegExp)[]; label?: string; read?: boolean } = {}) {
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('list', { name: 'Headings' })).toBeVisible({ timeout: 60_000 })
  // on a narrow screen the headings and the document are on two tabs; on a wide one both are always there
  const goTo = async (tab: string) => { const t = dialog.getByRole('tab', { name: tab }); if (await t.isVisible()) await t.click() }
  if (opts.read) {                                                                              // the document itself is shown beside the headings, as it will print
    const doc = dialog.frameLocator('iframe[title="The document as it will print"]')
    await goTo('See the document')
    await expect(doc.getByRole('heading', { name: /Purpose/ })).toBeVisible()
    await expect(doc.getByRole('heading', { name: /Approval and sign-off/ })).toHaveCount(1)
    await goTo('Choose')
    await dialog.getByRole('checkbox', { name: /Approval and sign-off/ }).uncheck()
    await goTo('See the document')
    await expect(doc.getByRole('heading', { name: /Approval and sign-off/ })).toHaveCount(0)    // leaving a heading out takes it out of the page
    await goTo('Choose')
  }
  for (const heading of opts.untick ?? []) await dialog.getByRole('checkbox', { name: heading }).uncheck()
  await dialog.getByRole('button', { name: opts.label ?? 'Make the PDF' }).click()
  await expect(dialog).toHaveCount(0)
}
test('from a new project to a workflow diagram and an implementation document that everybody can open', async ({ page, browser }) => {
  const name = `Journey ${Date.now()}`
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()

  // 1. a project, in one step
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Run analysis' })).toBeDisabled()               // nothing to analyse yet
  await expect(page.getByText('Add some input first.')).toBeVisible()

  // 2. notes, three ways
  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('When the case sheet comes to the department the user opens the receive menu and picks the ward.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText('Typed note')).toBeVisible()

  await page.getByLabel('Choose photos of notes').setInputFiles({ name: 'notes.png', mimeType: 'image/png', buffer: PNG })
  await expect(page.getByText('Photo of notes')).toBeVisible()
  await expect(page.getByText('Ready').first()).toBeVisible({ timeout: 30_000 })

  await page.getByRole('button', { name: /Record voice/ }).click()
  await page.getByRole('button', { name: 'Start recording' }).click()
  await page.waitForTimeout(2500)
  await page.getByRole('button', { name: 'Stop' }).click()
  await expect(page.getByRole('button', { name: 'Use this recording' })).toBeVisible()
  await page.getByRole('button', { name: 'Use this recording' }).click()
  await expect(page.getByText(/Voice note · 0:0\d/)).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('(Demo transcript)')).toBeVisible({ timeout: 30_000 })        // turned into text

  // 3. analysis
  await expect(page.getByRole('button', { name: 'Run analysis' })).toBeEnabled({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Run analysis' }).click()
  await expect(page.getByText(/Last analysis: version 1 created/)).toBeVisible({ timeout: 60_000 })
  await expect(page.getByText(/Questions to answer \(\d+ open\)/)).toBeVisible()

  // 4. the workflow and the implementation document
  const picture = page.getByRole('img', { name: 'Workflow diagram' }).first()                 // drawn by the server, the same picture as in the PDF
  await expect(picture).toBeVisible({ timeout: 30_000 })
  await expect.poll(() => picture.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 300), { message: 'the picture really loaded and is not empty' }).toBe(true)
  await expect(page.getByLabel('What was found')).toContainText('steps')
  await page.getByRole('button', { name: 'Create the implementation document' }).click()
  await confirmPdf(page, { read: true })                                // the person picks the headings first
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 120_000 })
  await expect(page.getByText(/IMPL-.+ · version 1 · made by Asha Kumar/)).toBeVisible()

  // the PDF is a real PDF
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download' }).click()
  const file = await download
  expect(file.suggestedFilename()).toMatch(/^IMPL-.+\.pdf$/)
  const path = await file.path()
  const bytes = (await import('node:fs')).readFileSync(path!)
  expect(bytes.subarray(0, 5).toString('ascii')).toBe('%PDF-')
  expect(bytes.length).toBeGreaterThan(5000)

  // the next step is always shown beside the work, and the AI's use is counted
  await expect(page.getByRole('list', { name: 'Progress' }).getByRole('listitem').filter({ hasText: 'Implementation document' })).toHaveClass(/done/)
  await page.goto('/usage')
  await expect(page.getByRole('heading', { name: 'My AI usage' })).toBeVisible()
  const calls = page.getByLabel('Recent AI calls')
  await expect(calls).toContainText('Analysing notes')
  await expect(calls).toContainText(name)
  // this project made one AI call for its photo (none in demo), one for the analysis and none for the document, which is written from the analysed workflow
  const mine = calls.getByRole('row').filter({ hasText: name })
  await expect(mine).toHaveCount(1)
  await expect(mine).toContainText('Analysing notes')
  await expect(mine).not.toContainText('Implementation documents')

  // 5. another user finds it in the shared documents, and an administrator sees it counted
  const other = await (await browser.newContext()).newPage()
  await signIn(other, 'ravi@demo.local')
  await other.goto('/documents')
  const doc = other.getByRole('listitem').filter({ hasText: name })
  await expect(doc).toBeVisible()
  await expect(doc).toContainText('Implementation document')
  await expect(doc).toContainText('Asha Kumar')
  await doc.getByRole('button', { name: /Diagram/ }).click()
  await expect(other.getByRole('dialog').getByRole('img', { name: /Workflow diagram/ })).toBeVisible()

  // and it is also on the first page of the project
  await page.goto('/')
  await expect(page.getByRole('link', { name: `Open ${name}` })).toContainText('Document ready')      // the card now says the document is made
})

/** A short real recording (a tone) as a WAV file, for attaching as an audio file. */
function wavFile(seconds = 2, rate = 16000): Buffer {
  const n = seconds * rate
  const b = Buffer.alloc(44 + n * 2)
  b.write('RIFF', 0); b.writeUInt32LE(36 + n * 2, 4); b.write('WAVE', 8); b.write('fmt ', 12); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22)
  b.writeUInt32LE(rate, 24); b.writeUInt32LE(rate * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34); b.write('data', 36); b.writeUInt32LE(n * 2, 40)
  for (let i = 0; i < n; i++) b.writeInt16LE(Math.round(Math.sin(i / 20) * 8000), 44 + i * 2)
  return b
}

test('photos, recordings and text files can be attached together, and more can be added later', async ({ page }) => {
  const name = `Attach ${Date.now()}`
  await signIn(page, 'asha@demo.local')
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  await expect(page.getByRole('button', { name: /Attach files/ })).toBeVisible()

  // first round: a photo, a recording and a text file chosen together
  await page.getByLabel('Choose files to attach').setInputFiles([
    { name: 'page.png', mimeType: 'image/png', buffer: PNG },
    { name: 'interview.wav', mimeType: 'audio/wav', buffer: wavFile() },
    { name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('The user opens the receive menu and picks the ward.') },
  ])
  await expect(page.getByText('Photo sent. It is being read.')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('Recording sent. It is being turned into text.')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('Note added.')).toBeVisible({ timeout: 30_000 })
  const list = page.getByRole('list', { name: 'Notes added so far' })
  await expect(list.getByText('Photo of notes')).toBeVisible({ timeout: 30_000 })
  await expect(list.getByText(/Voice note · 0:0\d/)).toBeVisible({ timeout: 30_000 })
  await expect(list.getByText('Typed note')).toBeVisible()
  await expect(list.getByText('Ready').first()).toBeVisible({ timeout: 30_000 })

  // later: another batch, and a typed note, are added to the same project
  await page.getByLabel('Choose files to attach').setInputFiles([
    { name: 'page2.png', mimeType: 'image/png', buffer: PNG },
    { name: 'more.txt', mimeType: 'text/plain', buffer: Buffer.from('A super user gets his own ward pre-selected.') },
  ])
  await expect(list.getByRole('listitem')).toHaveCount(5, { timeout: 30_000 })
  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('Priority is High.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(list.getByRole('listitem')).toHaveCount(6, { timeout: 30_000 })

  // and all of it is analysed together
  await expect(page.getByRole('button', { name: 'Analyse and make the document' })).toBeEnabled({ timeout: 60_000 })
  await page.getByRole('button', { name: 'Analyse and make the document' }).click()
  await confirmPdf(page)
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 120_000 })
})

test('one button goes from a typed note to the finished document, quickly', async ({ page }) => {
  const name = `One step ${Date.now()}`
  await signIn(page, 'asha@demo.local')
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  await expect(page.getByRole('button', { name: /make the document/i })).toHaveCount(0)        // nothing to make it from yet

  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('When the case sheet comes to the department the user opens the receive menu and picks the ward.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText('Typed note')).toBeVisible()

  const started = Date.now()
  await page.getByRole('button', { name: 'Analyse and make the document' }).click()
  await confirmPdf(page)
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 60_000 })
  expect(Date.now() - started, 'notes to document with the demo AI').toBeLessThan(30_000)
  await expect(page.getByText(/IMPL-.+ · version 1 · made by Asha Kumar/)).toBeVisible()
  await expect(page.getByRole('button', { name: /make the document/i })).toHaveCount(0)        // done: the offer goes away
  await expect(page.getByRole('list', { name: 'Progress' }).getByRole('listitem').filter({ hasText: 'Analysed' })).toHaveClass(/done/)
})

test('a PDF is attached as a note, and one big PDF of the project and a PDF of its module are made', async ({ page }) => {
  const name = `Big PDF ${Date.now()}`
  // a small PDF with real text in it, as bytes
  const content = 'BT /F1 18 Tf 20 100 Td (Bed allocation happens at admission) Tj ET'
  const objects = ['<</Type/Catalog/Pages 2 0 R>>', '<</Type/Pages/Kids[3 0 R]/Count 1>>', '<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>',
    `<</Length ${content.length}>>\nstream\n${content}\nendstream`, '<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>']
  let pdf = '%PDF-1.4\n'
  const offsets: number[] = []
  objects.forEach((o, i) => { offsets.push(pdf.length); pdf += `${i + 1} 0 obj\n${o}\nendobj\n` })
  const xref = pdf.length
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n${offsets.map(o => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<</Size ${objects.length + 1}/Root 1 0 R>>\nstartxref\n${xref}\n%%EOF`

  await signIn(page, 'asha@demo.local')
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()

  // the PDF is read on the server and becomes a note that names the file
  await page.getByLabel('Choose files to attach').setInputFiles({ name: 'ward-flow.pdf', mimeType: 'application/pdf', buffer: Buffer.from(pdf, 'latin1') })
  await expect(page.getByText('The file was read and added as a note.')).toBeVisible({ timeout: 20_000 })
  await page.getByText('Typed note').first().click()
  await expect(page.getByText('Bed allocation happens at admission')).toBeVisible()

  await page.getByRole('button', { name: 'Analyse and make the document' }).click()
  await confirmPdf(page)
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 60_000 })

  // one big PDF for the project, and one for the module the request is in
  const list = page.getByRole('list', { name: 'PDFs' })
  await expect(list.getByText('1 of 1 request analysed').first()).toBeVisible()
  await list.getByRole('button', { name: 'Make the PDF of Whole project' }).click()
  await confirmPdf(page)
  await expect(list.getByRole('button', { name: 'Update the PDF of Whole project' })).toBeVisible({ timeout: 90_000 })
  const module = list.getByRole('button', { name: /Make the PDF of (?!Whole project)/ }).first()
  await module.click()
  await confirmPdf(page)
  await expect(list.getByRole('button', { name: /Update the PDF of (?!Whole project)/ }).first()).toBeVisible({ timeout: 90_000 })

  // every new PDF is also saved on the device by itself, so downloads arrive from more than one place: look for the project's
  const downloads: import('@playwright/test').Download[] = []
  page.on('download', d => downloads.push(d))
  await list.getByRole('button', { name: 'Download the PDF of Whole project' }).click()
  await expect.poll(() => downloads.some(d => /-FULL/.test(d.suggestedFilename()))).toBe(true)
  const file = downloads.find(d => /-FULL/.test(d.suggestedFilename()))!
  expect(file.suggestedFilename()).toMatch(/^IMPL-.+-FULL.*\.pdf$/)
  const bytes = await (await import('node:fs/promises')).readFile((await file.path())!)
  expect(bytes.subarray(0, 5).toString('latin1')).toBe('%PDF-')
  expect(bytes.length).toBeGreaterThan(8000)
})

test('work is filed as module, then new requirement or change request, and a picture is printed in the document that carries that number', async ({ page }) => {
  const name = `Folders ${Date.now()}`
  await signIn(page, 'asha@demo.local')
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()

  // the project starts with its first request, a new requirement
  const tree = page.getByRole('list', { name: 'Modules' })
  await expect(tree.getByRole('button', { name: /^NR-001/ })).toBeVisible()

  // a module of its own, and a change request inside it
  await page.getByRole('button', { name: 'Add a module' }).click()
  await page.getByLabel('Module to add').selectOption({ label: 'Ward' })                                     // picked from the list of modules
  await page.getByRole('button', { name: 'Add module', exact: true }).click()
  await expect(tree.getByRole('button', { name: /^Ward/ })).toHaveAttribute('aria-expanded', 'true')        // a new module opens, ready for its first request
  await page.getByRole('button', { name: 'New change request in Ward' }).click()
  await page.getByLabel('Name of the change request').fill('Discharge print')
  await page.getByRole('button', { name: 'Create CR' }).click()
  await expect(tree.getByRole('button', { name: /^CR-001.*Discharge print/ })).toHaveAttribute('aria-current', 'true')
  await expect(page.locator('.request-title')).toContainText('CR-001')

  // a note, a picture tied to nothing yet, and the document
  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('When the nurse finishes the discharge the printed summary must show the ward and the doctor.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText('Typed note').first()).toBeVisible()
  await page.getByLabel('Choose pictures to attach').setInputFiles({ name: 'Discharge screen.png', mimeType: 'image/png', buffer: PNG })
  await expect(page.getByLabel('Caption of figure 1')).toHaveValue('Discharge screen')
  await page.getByLabel('Caption of figure 1').fill('The discharge screen')
  await page.getByLabel('Caption of figure 1').blur()

  await page.getByRole('button', { name: 'Analyse and make the document' }).click()
  await confirmPdf(page)
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 90_000 })
  await expect(page.getByText(/IMPL-WARD-CR-001 · version 1/)).toBeVisible()

  // the picture can now be tied to a step, and it stays after a reload
  await page.getByLabel('Step of figure 1').selectOption({ index: 1 })
  await page.reload()
  await expect(page.getByLabel('Caption of figure 1')).toHaveValue('The discharge screen')

  // the change request can be deleted again: it leaves the list, after asking
  const folders = page.getByRole('list', { name: 'Modules' })
  const ward = folders.getByRole('button', { name: /^Ward/ })
  if ((await ward.getAttribute('aria-expanded')) !== 'true') await ward.click()                 // the module of the selected request is already open
  await folders.getByRole('button', { name: 'Delete CR-001 Discharge print' }).click()
  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(folders.getByRole('button', { name: /^CR-001/ })).toBeVisible()
  await folders.getByRole('button', { name: 'Delete CR-001 Discharge print' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Delete request' }).click()
  await expect(page.getByText('CR-001 was deleted.')).toBeVisible()
  await expect(folders.getByRole('button', { name: /^CR-001/ })).toHaveCount(0)
})
test('a project can be deleted from its card, after asking, and it stays gone', async ({ page }) => {
  const name = `Delete me ${Date.now()}`
  await signIn(page, 'asha@demo.local')
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()

  await page.goto('/')
  await expect(page.getByRole('link', { name: `Open ${name}` })).toBeVisible()
  await page.getByRole('button', { name: `Delete ${name}` }).click()
  await expect(page.getByRole('dialog')).toContainText('Nothing is erased from the database')
  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('link', { name: `Open ${name}` })).toBeVisible()                 // asking first: cancel keeps it

  await page.getByRole('button', { name: `Delete ${name}` }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Delete project' }).click()
  await expect(page.getByText('The project was deleted.')).toBeVisible()
  await expect(page.getByRole('link', { name: `Open ${name}` })).toHaveCount(0)
  await page.reload()
  await expect(page.getByRole('link', { name: `Open ${name}` })).toHaveCount(0)
})

test('the administrator sets a daily limit for an AI and sees its allowance for today', async ({ page }) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill('admin@flowcapture.local')
  await page.getByLabel('Password').fill(process.env.FLOWCAPTURE_ADMIN_PASSWORD ?? 'ChangeMe-Dev-12345')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)

  await page.goto('/admin/ai')
  await expect(page.getByRole('heading', { name: /Overall daily limits/ })).toBeVisible()
  await page.getByLabel('Gemini: requests a day').fill('500')
  await page.getByRole('button', { name: 'Save the limits' }).click()
  await expect(page.getByText('The limits were saved.')).toBeVisible()

  await page.goto('/usage')
  const allowance = page.getByRole('region', { name: 'Free allowance today' })
  await expect(allowance).toBeVisible()
  await expect(allowance).toContainText('of 500')
  await expect(allowance).toContainText('renews in')

  // leave the test server as it was found
  await page.goto('/admin/ai')
  await page.getByLabel('Gemini: requests a day').fill('')
  await page.getByRole('button', { name: 'Save the limits' }).click()
  await expect(page.getByText('The limits were saved.')).toBeVisible()
})
test('the administrator sees the AI is set up, and the page that sets it up explains how', async ({ page }) => {
  await page.goto('/login')
  await page.getByLabel('Email').fill('admin@flowcapture.local')
  await page.getByLabel('Password').fill(process.env.FLOWCAPTURE_ADMIN_PASSWORD ?? 'ChangeMe-Dev-12345')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible()
  await expect(page.getByText('The AI is ready')).toBeVisible()
  await page.getByRole('navigation', { name: /^Main/ }).getByRole('link', { name: 'AI usage' }).first().click()
  await expect(page.getByRole('heading', { name: 'AI usage' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'People' })).toBeVisible()
  await page.goto('/admin')
  await page.getByRole('link', { name: /Settings/ }).click()
  await expect(page.getByRole('link', { name: /Google AI Studio/ })).toHaveAttribute('href', 'https://aistudio.google.com/apikey')
  await expect(page.getByLabel('Gemini API key')).toHaveAttribute('type', 'password')
  await expect(page.getByText(/About privacy/)).toBeVisible()
  await expect(page.getByRole('checkbox', { name: /Allow photos and voice recordings/ })).toBeChecked()      // on by default
  await expect(page.getByRole('checkbox', { name: /rewrite the document wording/ })).not.toBeChecked()        // off by default: fast
})
