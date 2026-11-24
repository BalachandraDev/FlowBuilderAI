// Walks the whole journey in real Chrome and takes screenshots of every screen, at desktop and phone size.
// Run: node scripts/journey.mjs <output-folder> [url]    (needs the API running in demo mode with the demo users, see scripts/demo-setup.ps1)
import { chromium } from '@playwright/test'
import { join } from 'node:path'

const out = process.argv[2] ?? '.'
const url = process.argv[3] ?? 'http://localhost:5499'
const PASSWORD = 'Demo-Passw0rd-123'
// a 1x1 picture is enough: in demo mode the "reading" is a fixed text
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')

const browser = await chromium.launch({ channel: 'chrome', args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] })
for (const [tag, viewport, mobile] of [['desktop', { width: 1280, height: 900 }, false], ['phone', { width: 390, height: 844 }, true]]) {
  const ctx = await browser.newContext({ viewport, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: mobile ? 2 : 1, permissions: ['microphone'] })
  const page = await ctx.newPage()
  const shot = async (name, full = false) => { await page.waitForTimeout(700); await page.screenshot({ path: join(out, `${tag}-${name}.png`), fullPage: full }); console.log('shot', tag, name) }
  const signIn = async (email, password) => {
    await page.goto(url + '/login')
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: 'Sign in' }).click()
  }
  const signOut = async () => { await page.getByRole('button', { name: /Account menu/ }).click(); await page.getByRole('menuitem', { name: 'Sign out' }).click(); await page.waitForURL('**/login') }

  try {
  await page.goto(url + '/login'); await shot('01-login')

  // administrator
  await signIn('admin@flowcapture.local', 'ChangeMe-Dev-12345')
  await page.getByRole('heading', { name: 'Admin' }).waitFor()
  await shot('02-admin-overview', true)
  await page.goto(url + '/admin/users'); await page.getByRole('heading', { name: 'Users' }).waitFor(); await shot('03-admin-users')
  await page.getByRole('button', { name: 'Add user' }).click(); await shot('04-admin-add-user')
  await page.keyboard.press('Escape')
  await page.goto(url + '/admin/ai'); await page.getByRole('heading', { name: 'AI setup' }).waitFor(); await shot('05-admin-ai', true)
  await signOut()

  // an ordinary user
  await signIn('asha@demo.local', PASSWORD)
  await page.getByRole('heading', { name: /^Hello/ }).waitFor()
  await shot('06-home-first-time', true)
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill('IP Case Sheet Receive ' + tag)
  await shot('07-new-project')
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await page.getByRole('heading', { name: /Add your notes/ }).waitFor()
  await shot('08-workspace-empty', true)

  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('When the case sheet comes to the department the user opens the receive menu and picks the ward. A super user gets his own ward pre-selected.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await page.getByText('Typed note').first().waitFor()

  await page.locator('input[type=file]').setInputFiles({ name: 'notes.png', mimeType: 'image/png', buffer: PNG })
  await page.getByText('Photo of notes').first().waitFor()

  await page.getByRole('button', { name: /Record voice/ }).click()
  await page.getByRole('button', { name: 'Start recording' }).click()
  await page.waitForTimeout(2600)
  await shot('09-recording')
  await page.getByRole('button', { name: 'Stop' }).click()
  await shot('10-recorded')
  await page.getByRole('button', { name: 'Use this recording' }).click()
  await page.getByText('Voice note').first().waitFor({ timeout: 30000 })
  await page.waitForTimeout(4000)
  await shot('11-notes', true)

  await page.getByRole('button', { name: 'Run analysis' }).click()
  await page.getByText(/Last analysis: version 1 created/).waitFor({ timeout: 60000 })
  await shot('12-analysed', true)
  await page.locator('.diagram svg').first().waitFor({ timeout: 30000 })
  await page.getByRole('button', { name: /Create the implementation document/ }).click()
  await page.getByRole('button', { name: 'Open the PDF' }).waitFor({ timeout: 120000 })
  await page.locator('#result').scrollIntoViewIfNeeded()
  await shot('13-result-with-document', true)

  await page.goto(url + '/documents'); await page.getByRole('heading', { name: 'Documents' }).waitFor(); await shot('14-documents')
  await page.goto(url + '/usage'); await page.getByRole('heading', { name: 'My AI usage' }).waitFor(); await shot('16-usage', true)
  await page.goto(url + '/'); await page.getByRole('heading', { name: /^Hello/ }).waitFor(); await shot('15-home-with-project', true)
  } catch (e) { console.log('FAILED', tag, String(e).split('\n')[0]); await shot('FAIL', true) }
  await ctx.close()
}
await browser.close()
