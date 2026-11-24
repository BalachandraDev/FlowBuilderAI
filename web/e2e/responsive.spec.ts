import { expect, test, type Page } from '@playwright/test'

// Every main page must fit the screen: no sideways scrolling and no scrollbar inside the menu, from a small phone to a wide desktop.
// The test sets its own screen sizes, so it runs once (in the "desktop" project).
const PASSWORD = process.env.FLOWCAPTURE_DEMO_PASSWORD ?? 'Demo-Passw0rd-123'
const ADMIN_PASSWORD = process.env.FLOWCAPTURE_ADMIN_PASSWORD ?? 'ChangeMe-Dev-12345'
const SIZES = [[320, 640], [390, 844], [600, 900], [768, 1024], [1024, 768], [1280, 720], [1920, 1080]] as const

async function signIn(page: Page, email: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function fits(page: Page) {
  return page.evaluate(() => {
    const nav = document.querySelector('.appnav') as HTMLElement | null
    const navScrolls = !!nav && getComputedStyle(nav).overflowX !== 'visible' && nav.scrollWidth > nav.clientWidth
    return { wider: document.documentElement.scrollWidth - document.documentElement.clientWidth, navScrolls }
  })
}

async function sweep(page: Page, routes: string[]) {
  for (const [width, height] of SIZES) {
    await page.setViewportSize({ width, height })
    for (const route of routes) {
      await page.goto(route)
      await expect(page.locator('.wrap')).toBeVisible()
      await page.waitForTimeout(500)                                                  // lists and tables arrive after the page
      const r = await fits(page)
      expect(r.wider, `${route} at ${width}px wide is ${r.wider}px wider than the screen`).toBeLessThanOrEqual(0)
      expect(r.navScrolls, `the menu scrolls sideways at ${width}px (${route})`).toBe(false)
    }
  }
}

test('a user’s pages fit every screen size', async ({ page }, info) => {
  test.skip(info.project.name !== 'desktop', 'sets its own sizes')
  test.setTimeout(180_000)
  await signIn(page, 'asha@demo.local', PASSWORD)
  await sweep(page, ['/', '/new', '/documents', '/usage'])
})

test('the administrator’s pages fit every screen size', async ({ page }, info) => {
  test.skip(info.project.name !== 'desktop', 'sets its own sizes')
  test.setTimeout(240_000)
  await signIn(page, 'admin@flowcapture.local', ADMIN_PASSWORD)
  await sweep(page, ['/admin', '/admin/users', '/admin/clients', '/admin/modules', '/admin/handling', '/admin/ai', '/admin/database', '/documents', '/usage'])
})
