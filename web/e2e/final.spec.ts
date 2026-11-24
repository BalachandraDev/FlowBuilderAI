import { expect, test, type Page } from '@playwright/test'

// The office's flow: a user makes a document and marks it final; the administrator sees only final documents, filters them, sees who handles which
// client, and verifies; the user sees that it was verified. Needs the demo accounts (scripts/demo-setup.ps1) and the AI in demo mode.
const PASSWORD = process.env.FLOWCAPTURE_DEMO_PASSWORD ?? 'Demo-Passw0rd-123'
const ADMIN_PASSWORD = process.env.FLOWCAPTURE_ADMIN_PASSWORD ?? 'ChangeMe-Dev-12345'

async function signIn(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

/** A project with one typed note, analysed, and its document made (the dialog that asks what to print is answered with everything). */
async function makeDocument(page: Page, name: string) {
  await page.getByRole('link', { name: 'New project' }).first().click()
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
}

test('a user marks a document final, the administrator sees only that one, filters, sees who handles the client, and verifies it', async ({ page, browser }) => {
  const finalName = `Final ${Date.now()}`
  const workingName = `Working ${Date.now()}`
  await signIn(page, 'asha@demo.local')

  // two documents: one is only a working copy, the other is marked final with a note
  await makeDocument(page, workingName)
  await page.goto('/')
  await makeDocument(page, finalName)
  const projectUrl = page.url()
  await expect(page.getByText('Working copy').first()).toBeVisible()
  await page.getByRole('button', { name: 'Mark as final' }).first().click()
  const ask = page.getByRole('dialog', { name: 'Mark this document as final?' })
  await ask.getByLabel(/Note for the administrator/).fill('Please check the scope')
  await ask.getByRole('button', { name: 'Mark as final' }).click()
  await expect(page.getByText('Final · waiting for the administrator').first()).toBeVisible()
  await expect(page.getByRole('button', { name: 'Take back' }).first()).toBeVisible()

  // the administrator
  const adminPage = await (await browser.newContext()).newPage()
  await signIn(adminPage, 'admin@flowcapture.local', ADMIN_PASSWORD)
  await expect(adminPage.getByRole('heading', { name: 'Admin' })).toBeVisible()
  await expect(adminPage.getByRole('link', { name: /final documents? waiting for you/ })).toBeVisible()
  await adminPage.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: /Final documents/ }).click()
  await expect(adminPage.getByRole('heading', { name: 'Final documents' })).toBeVisible()
  const list = adminPage.getByRole('list', { name: 'Final documents' })
  const mine = list.getByRole('listitem').filter({ hasText: finalName })
  await expect(mine).toBeVisible()
  await expect(mine).toContainText('Asha Kumar')
  await expect(mine).toContainText('Please check the scope')
  await expect(list.getByRole('listitem').filter({ hasText: workingName })).toHaveCount(0)        // a working copy is not for the administrator

  // filters: by client, and by person
  await adminPage.getByLabel('Client').selectOption({ label: 'Demo Hospital Group' })
  await expect(mine).toBeVisible()
  await adminPage.getByLabel('Made final by').selectOption({ label: 'Asha Kumar' })
  await expect(mine).toBeVisible()
  await adminPage.getByRole('button', { name: 'Clear filters' }).click()

  // the administrator does not open the users' projects
  await adminPage.goto(new URL(projectUrl).pathname)
  await expect(adminPage.getByRole('heading', { name: 'Admin' })).toBeVisible()

  // who handles what
  await adminPage.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Who handles what' }).click()
  const people = adminPage.getByRole('list', { name: 'People on Demo Hospital Group' })
  await expect(people).toBeVisible()
  await expect(people).toContainText('Asha Kumar')
  await expect(people.getByRole('listitem').filter({ hasText: 'Asha Kumar' })).toContainText('1 waiting')

  // verify
  await adminPage.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: /Final documents/ }).click()
  await list.getByRole('listitem').filter({ hasText: finalName }).getByRole('button', { name: /^Verify / }).click()
  await adminPage.getByRole('dialog').getByRole('button', { name: 'Verify' }).click()
  await expect(list.getByRole('listitem').filter({ hasText: finalName })).toContainText('Verified by the administrator')

  // the user sees that it was verified
  await page.goto(new URL(projectUrl).pathname)
  await expect(page.getByText('Verified by the administrator').first()).toBeVisible()
  await expect(page.getByRole('button', { name: 'Mark as final' })).toHaveCount(0)
})
