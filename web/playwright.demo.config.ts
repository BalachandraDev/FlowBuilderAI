import { defineConfig } from '@playwright/test'

// Real-browser checks of the browser demo (the static site built by `npm run build:demo`), served under /FlowBuilderAI/ like GitHub Pages does.
//   npm run build:demo && npx playwright test -c playwright.demo.config.ts
// Uses the Chrome that is installed on the machine, so no browser download is needed.
const port = Number(process.env.DEMO_PORT ?? 4173)
export default defineConfig({
  testDir: './e2e-demo',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: `http://localhost:${port}`,
    channel: 'chrome',
    headless: true,
    acceptDownloads: true,
    trace: 'retain-on-failure',
  },
  webServer: {
    command: `node demo-tools/serve-site.mjs ${port}`,
    url: `http://localhost:${port}/FlowBuilderAI/app/`,
    reuseExistingServer: true,
    timeout: 30_000,
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1280, height: 800 } } },
    { name: 'phone', use: { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true } },
  ],
})
