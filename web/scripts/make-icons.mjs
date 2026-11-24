// Draws the app icons (a teal rounded square with an F) with the installed Chrome. Run: node scripts/make-icons.mjs
// Replace public/icons/*.png with the real AOSTA / BACKBONE artwork when it is available; names and sizes stay the same.
import { chromium } from '@playwright/test'
import { mkdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'icons')
mkdirSync(out, { recursive: true })

const html = (size, maskable) => `<!doctype html><meta charset="utf-8"><body style="margin:0;width:${size}px;height:${size}px;background:${maskable ? '#0f766e' : 'transparent'}">
<div style="width:${size}px;height:${size}px;display:flex;align-items:center;justify-content:center;background:#0f766e;border-radius:${maskable ? 0 : Math.round(size * 0.22)}px;
font:800 ${Math.round(size * (maskable ? 0.42 : 0.56))}px/1 'Segoe UI',system-ui,Arial,sans-serif;color:#fff">F</div>`

const browser = await chromium.launch({ channel: 'chrome' })
for (const [name, size, maskable] of [['icon-192.png', 192, false], ['icon-512.png', 512, false], ['icon-512-maskable.png', 512, true]]) {
  const page = await browser.newPage({ viewport: { width: size, height: size } })
  await page.setContent(html(size, maskable))
  await page.screenshot({ path: join(out, name), omitBackground: !maskable })
  await page.close()
  console.log('wrote', name)
}
await browser.close()
