import { expect, test, type Page } from '@playwright/test'

// Only the administrator makes modules; users pick from that list. And the eye that shows what was typed in the change-password window.
const PASSWORD = process.env.FLOWCAPTURE_DEMO_PASSWORD ?? 'Demo-Passw0rd-123'
const ADMIN = { email: 'admin@flowcapture.local', password: process.env.FLOWCAPTURE_ADMIN_PASSWORD ?? 'ChangeMe-Dev-12345' }

async function signIn(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

test('the administrator makes a module, and a user can then pick it but never type one', async ({ page, browser }) => {
  const stamp = Date.now()
  const moduleName = `Stores ${stamp}`

  const admin = await (await browser.newContext()).newPage()
  await signIn(admin, ADMIN.email, ADMIN.password)
  await admin.getByRole('link', { name: 'Modules' }).first().click()
  await expect(admin.getByRole('heading', { name: 'Modules' })).toBeVisible()
  await admin.getByRole('button', { name: 'Add module' }).first().click()
  const dialog = admin.getByRole('dialog', { name: 'Add a module' })
  await dialog.getByLabel(/Module name/).fill(moduleName)
  await dialog.getByRole('button', { name: 'Add module' }).click()
  await expect(admin.getByText(`${moduleName} was added. Users can pick it now.`)).toBeVisible()
  await expect(admin.getByRole('list', { name: 'Modules' }).getByRole('listitem').filter({ hasText: moduleName })).toBeVisible()

  await signIn(page, 'asha@demo.local')
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(`Pick ${stamp}`)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name: `Pick ${stamp}` })).toBeVisible()
  await page.getByRole('button', { name: 'Add a module' }).click()
  await expect(page.getByLabel('Name of the module')).toHaveCount(0)
  await page.getByLabel('Module to add').selectOption({ label: moduleName })
  await page.getByRole('button', { name: 'Add module' }).click()
  await expect(page.getByText(`The module ${moduleName} was added.`)).toBeVisible()

  // the administrator stops it: it is gone from the list for the next project
  await admin.getByRole('button', { name: `Stop ${moduleName}` }).click()
  await expect(admin.getByRole('list', { name: 'Modules' }).getByRole('listitem').filter({ hasText: moduleName }).getByText('Stopped')).toBeVisible()
  await page.getByRole('button', { name: 'Add a module' }).click()
  await expect(page.getByLabel('Module to add').getByRole('option', { name: moduleName })).toHaveCount(0)
})

test('an eye in the change-password window shows what was typed and hides it again', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('menuitem', { name: 'Change password' }).click()
  const dialog = page.getByRole('dialog', { name: 'Change your password' })
  const next = dialog.getByLabel('New password', { exact: true })
  await next.fill('Typed-Passw0rd-7')
  await expect(next).toHaveAttribute('type', 'password')
  const eye = dialog.locator('.pw-wrap').nth(1).getByRole('button', { name: 'Show password' })
  await eye.click()
  await expect(next).toHaveAttribute('type', 'text')
  await expect(next).toHaveValue('Typed-Passw0rd-7')
  await expect(dialog.getByLabel('Current password')).toHaveAttribute('type', 'password')                // the other boxes stay hidden
  await dialog.getByRole('button', { name: 'Hide password' }).click()
  await expect(next).toHaveAttribute('type', 'password')
  await dialog.getByRole('button', { name: 'Cancel' }).click()                                           // nothing is changed: the demo password stays
})
