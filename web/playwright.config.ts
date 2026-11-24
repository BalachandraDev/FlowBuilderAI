import { defineConfig } from '@playwright/test'

// Real-browser checks of the app against a running FlowCapture server that has the demo data (scripts/demo-setup.ps1).
//   FLOWCAPTURE_URL   where the app is served (default http://localhost:5199, the API serves the built app)
// Uses the Chrome that is installed on the machine, so no browser download is needed.
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: process.env.FLOWCAPTURE_URL ?? 'http://localhost:5199',
    channel: 'chrome',
    headless: true,
    // a fake microphone that makes a test tone, so the voice recorder can be tested without anyone speaking
    launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] },
    permissions: ['microphone'],
    acceptDownloads: true,
    trace: 'retain-on-failure',
    serviceWorkers: 'allow',
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1280, height: 800 } } },
    { name: 'phone', use: { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true } },
  ],
})
