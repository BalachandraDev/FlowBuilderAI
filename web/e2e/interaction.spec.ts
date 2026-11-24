import { expect, test, type Page } from '@playwright/test'

// The everyday shortcuts of the app, in a real browser: search and commands, keyboard shortcuts, the menu that stays collapsed, saved filters,
// and the administrator verifying several final documents in one go. Needs the demo accounts (scripts/demo-setup.ps1) and the AI in demo mode.
const PASSWORD = process.env.FLOWCAPTURE_DEMO_PASSWORD ?? 'Demo-Passw0rd-123'
const ADMIN_PASSWORD = process.env.FLOWCAPTURE_ADMIN_PASSWORD ?? 'ChangeMe-Dev-12345'

async function signIn(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function makeFinalDocument(page: Page, name: string) {
  await page.goto('/new')
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('When the case sheet comes to the department the user opens the receive menu and picks the ward.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText('Typed note')).toBeVisible()
  await page.getByRole('button', { name: 'Analyse and make the document' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('list', { name: 'Headings' })).toBeVisible({ timeout: 60_000 })
  await dialog.getByRole('button', { name: 'Make the PDF' }).click()
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 90_000 })
  await page.getByRole('button', { name: 'Mark as final' }).first().click()
  await page.getByRole('dialog', { name: 'Mark this document as final?' }).getByRole('button', { name: 'Mark as final' }).click()
  await expect(page.getByText('Final · waiting for the administrator').first()).toBeVisible()
}

test('search and commands open from the keyboard, find a page, and the shortcut list is one key away', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: /^Hello,/ })).toBeVisible()

  await page.keyboard.press('Control+k')
  const palette = page.getByRole('dialog', { name: 'Search and commands' })
  await expect(palette).toBeVisible()
  await expect(palette.getByRole('combobox', { name: 'Search' })).toBeFocused()
  await palette.getByRole('combobox', { name: 'Search' }).fill('docum')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/\/documents/)
  await expect(page.getByRole('heading', { name: 'Documents' })).toBeVisible()

  await page.keyboard.press('g')
  await page.keyboard.press('h')
  await expect(page.getByRole('heading', { name: /^Hello,/ })).toBeVisible()

  await page.keyboard.press('Shift+?')
  const help = page.getByRole('dialog', { name: 'Keyboard shortcuts' })
  await expect(help).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(help).toHaveCount(0)
  await expect(page.getByRole('heading', { name: /^Hello,/ })).toBeVisible()
})

test('the menu stays collapsed after a reload, and only on a computer', async ({ page }, info) => {
  test.skip(info.project.name === 'phone', 'the phone has tabs at the bottom instead of a menu on the left')
  await signIn(page, 'asha@demo.local')
  await page.getByRole('button', { name: 'Collapse the menu' }).click()
  await expect(page.locator('.app')).toHaveClass(/compact/)
  await page.reload()
  await expect(page.locator('.app')).toHaveClass(/compact/)
  await expect(page.getByRole('button', { name: 'Expand the menu' })).toBeVisible()
  await page.getByRole('button', { name: 'Expand the menu' }).click()
  await expect(page.locator('.app')).not.toHaveClass(/compact/)
  await page.reload()
  await expect(page.locator('.app')).not.toHaveClass(/compact/)
})

test('the administrator verifies several final documents together, exports the list, and filters are remembered', async ({ page, browser }) => {
  test.setTimeout(240_000)
  const tag = `Bulk ${Date.now()}`
  await signIn(page, 'asha@demo.local')
  await makeFinalDocument(page, `${tag} A`)
  await makeFinalDocument(page, `${tag} B`)

  const admin = await (await browser.newContext({ acceptDownloads: true })).newPage()
  await signIn(admin, 'admin@flowcapture.local', ADMIN_PASSWORD)
  await admin.goto('/documents')
  await expect(admin.getByRole('heading', { name: 'Final documents' })).toBeVisible()
  await admin.getByRole('searchbox', { name: 'Search the final documents' }).fill(tag)
  const list = admin.getByRole('list', { name: 'Final documents' })
  await expect(list.getByRole('listitem')).toHaveCount(2)

  // the list can be taken away as a spreadsheet
  const download = admin.waitForEvent('download')
  await admin.getByRole('button', { name: 'Export CSV' }).click()
  expect((await download).suggestedFilename()).toMatch(/^final-documents-\d{4}-\d{2}-\d{2}\.csv$/)

  // both at once, after a confirmation that lists them
  await admin.getByRole('checkbox', { name: 'Select all 2 waiting' }).check()
  await expect(admin.getByRole('region', { name: 'Selected documents' })).toContainText('2')
  await admin.getByRole('button', { name: 'Verify selected' }).click()
  const ask = admin.getByRole('dialog', { name: 'Verify 2 documents?' })
  await expect(ask.getByRole('list', { name: 'Documents to verify' }).getByRole('listitem')).toHaveCount(2)
  await ask.getByRole('button', { name: 'Verify 2' }).click()
  await expect(admin.getByRole('dialog', { name: /Verify/ })).toHaveCount(0, { timeout: 30_000 })
  await expect(list.getByRole('listitem').filter({ hasText: 'Verified by the administrator' })).toHaveCount(2)
  await expect(admin.getByRole('region', { name: 'Selected documents' })).toHaveCount(0)

  // what was chosen in the filters is still chosen after a reload
  await admin.getByLabel('Status').selectOption('Verified')
  await admin.reload()
  await expect(admin.getByLabel('Status')).toHaveValue('Verified')
})
