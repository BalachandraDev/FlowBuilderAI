// Opens the app in Chrome and prints what the browser says (console errors, failed requests, what is on the page). For finding why a page is blank.
// Run: node scripts/probe.mjs [url] [screenshot.png]
import { chromium } from '@playwright/test'

const url = process.argv[2] ?? 'http://localhost:5199/'
const shot = process.argv[3]
const browser = await chromium.launch({ channel: 'chrome' })
const page = await browser.newPage()
page.on('console', m => { if (['error', 'warning'].includes(m.type())) console.log(`console ${m.type()}: ${m.text()}`) })
page.on('pageerror', e => console.log('page error:', e.message))
page.on('requestfailed', r => console.log('request failed:', r.url(), r.failure()?.errorText))
page.on('response', r => { if (r.status() >= 400) console.log('http', r.status(), r.url()) })
await page.goto(url)
await page.waitForTimeout(2500)
console.log('title:', await page.title())
console.log('body text:', (await page.locator('body').innerText()).replace(/\s+/g, ' ').slice(0, 300))
if (shot) await page.screenshot({ path: shot })
await browser.close()
