// Signs in as a demo user and saves screenshots of the main pages, at phone and desktop size, for looking at the design.
// Run: node scripts/shots.mjs <output-folder> [url] [email]   (needs the demo data: scripts/demo-setup.ps1)
import { chromium } from '@playwright/test'
import { join } from 'node:path'

const out = process.argv[2] ?? '.'
const url = process.argv[3] ?? 'http://localhost:5199'
const email = process.argv[4] ?? 'lead@demo.local'
const browser = await chromium.launch({ channel: 'chrome' })

for (const [tag, viewport, mobile] of [['desktop', { width: 1280, height: 800 }, false], ['phone', { width: 390, height: 844 }, true]]) {
  const ctx = await browser.newContext({ viewport, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: mobile ? 2 : 1 })
  const page = await ctx.newPage()
  const shot = async name => { await page.waitForTimeout(500); await page.screenshot({ path: join(out, `${tag}-${name}.png`) }); console.log('shot', tag, name) }
  await page.goto(url)
  await shot('1-login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('Demo-Passw0rd-123')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByText(/^Hello /).waitFor()
  await shot('2-projects')
  await page.getByRole('link', { name: /^Open / }).first().click()
  await page.getByRole('heading', { name: 'Departments' }).waitFor()
  await page.waitForTimeout(800)
  await shot('3-project')
  if (mobile) { await page.getByRole('button', { name: 'Open the menu' }).click(); await shot('3b-menu'); await page.getByRole('button', { name: 'Open the menu' }).click().catch(() => {}) ; await page.mouse.click(380, 600) }
  await page.getByRole('main').getByRole('link', { name: /MRD/ }).first().click()
  await shot('4-department')
  await page.getByRole('main').getByRole('link', { name: /^Open / }).first().click()
  await page.getByRole('tab', { name: 'Capture' }).waitFor()
  await shot('5-flow')
  await ctx.close()
}
await browser.close()
