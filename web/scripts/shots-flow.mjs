// Screenshots of the analysis, review and versions tabs of the newest analysed flow, at phone and desktop size.
// Run: node scripts/shots-flow.mjs <output-folder> [url]   (needs demo data and a flow that has been analysed)
import { chromium } from '@playwright/test'
import { join } from 'node:path'

const out = process.argv[2] ?? '.'
const url = process.argv[3] ?? 'http://localhost:5199'
const browser = await chromium.launch({ channel: 'chrome' })
for (const [tag, viewport, mobile] of [['desktop', { width: 1280, height: 900 }, false], ['phone', { width: 390, height: 844 }, true]]) {
  const ctx = await browser.newContext({ viewport, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: mobile ? 2 : 1 })
  const page = await ctx.newPage()
  const shot = async name => { await page.waitForTimeout(900); await page.screenshot({ path: join(out, `${tag}-${name}.png`) }); console.log('shot', tag, name) }
  await page.goto(url)
  await page.getByLabel('Email').fill('lead@demo.local')
  await page.getByLabel('Password').fill('Demo-Passw0rd-123')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByText(/^Hello /).waitFor()
  await page.getByRole('link', { name: /^Open / }).first().click()
  await page.getByRole('heading', { name: 'Departments' }).waitFor()
  await page.waitForTimeout(800)
  await page.getByRole('main').getByRole('link', { name: /MRD/ }).first().click()
  await page.getByRole('main').getByRole('link', { name: /^Open Journey/ }).first().click()
  await page.getByRole('tab', { name: 'Analysis' }).click()
  await shot('6-analysis')
  await page.getByRole('tab', { name: 'Review' }).click()
  await page.locator('.diagram svg').waitFor({ timeout: 30000 })
  await shot('7-review-diagram')
  await page.locator('details.section', { hasText: 'Steps (' }).locator('summary').click()
  await page.locator('details.section', { hasText: 'Steps (' }).scrollIntoViewIfNeeded()
  await shot('8-review-steps')
  await page.getByRole('tab', { name: 'Versions' }).click()
  await shot('9-versions')
  await ctx.close()
}
await browser.close()
