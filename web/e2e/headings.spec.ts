import { expect, test, type Page } from '@playwright/test'

// Bugs as requests, picking the module from a list, and editing the headings, in a real browser.
const PASSWORD = process.env.FLOWCAPTURE_DEMO_PASSWORD ?? 'Demo-Passw0rd-123'

async function signIn(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function newProject(page: Page, name: string) {
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
}

test('a bug is filed from the top, picking its module and type, and shows as BUG-001', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await newProject(page, `Bugs ${Date.now()}`)
  await page.getByRole('button', { name: 'New request' }).click()
  const form = page.getByRole('form', { name: 'New request' })
  await expect(form.getByLabel('Module of the new request')).toBeVisible()
  await form.getByLabel('Type of the new request').selectOption({ label: 'Bug (BUG)' })
  await form.getByLabel('Name of the new request').fill('Discharge shows the wrong ward')
  await form.getByRole('button', { name: 'Create BUG' }).click()
  await expect(page.getByText('BUG-001 was created.')).toBeVisible()
  await expect(page.locator('.request-title .kind-tag.bug')).toHaveText('BUG-001')
  await expect(page.locator('.request-name')).toHaveText('Discharge shows the wrong ward')
  // the bug takes notes like any other request
  await page.getByRole('button', { name: 'Type a note' }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('On the discharge screen the ward shows the old ward after a transfer.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText('Saved', { exact: true })).toBeVisible()
})

test('a module is picked from the administrator’s list, and a name cannot be typed', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await newProject(page, `Modules ${Date.now()}`)
  await page.getByRole('button', { name: 'Add a module' }).click()
  const pick = page.getByLabel('Module to add')
  await expect(pick).toBeVisible()
  await expect(page.getByRole('button', { name: 'Add module' })).toBeDisabled()
  await pick.selectOption({ label: 'Pharmacy' })
  await page.getByRole('button', { name: 'Add module' }).click()
  await expect(page.getByText('The module Pharmacy was added.')).toBeVisible()
  await expect(page.getByRole('list', { name: 'Modules' }).getByRole('button', { name: /^Pharmacy/ })).toBeVisible()

  // Pharmacy is now taken, so it is no longer offered; and a name can never be typed: only the administrator makes modules
  await page.getByRole('button', { name: 'Add a module' }).click()
  await expect(page.getByLabel('Module to add').getByRole('option', { name: 'Pharmacy' })).toHaveCount(0)
  await expect(page.getByLabel('Name of the module')).toHaveCount(0)
  await page.getByLabel('Module to add').selectOption({ label: 'Billing' })
  await page.getByRole('button', { name: 'Add module' }).click()
  await expect(page.getByText('The module Billing was added.')).toBeVisible()
})

test('the request heading and the project heading can be edited, and stay edited after a reload', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  const stamp = Date.now()
  await newProject(page, `Headings ${stamp}`)

  await page.getByRole('button', { name: 'Edit request heading' }).click()
  const box = page.getByRole('textbox', { name: 'request heading' })
  await box.fill('Admission and bed allocation')
  // the new heading shows at once, before the server has answered: wait for the answer before reloading the page
  const flowSaved = page.waitForResponse(r => r.request().method() === 'PUT' && /\/api\/v1\/flows\/[0-9a-f-]+$/.test(r.url()))
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.locator('.request-name')).toHaveText('Admission and bed allocation')
  expect((await flowSaved).ok()).toBe(true)

  await page.getByRole('button', { name: 'Edit project heading' }).click()
  await page.getByRole('textbox', { name: 'project heading' }).fill(`Renamed ${stamp}`)
  const projectSaved = page.waitForResponse(r => r.request().method() === 'PUT' && /\/api\/v1\/projects\/[0-9a-f-]+$/.test(r.url()))
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { level: 1, name: `Renamed ${stamp}` })).toBeVisible()
  expect((await projectSaved).ok()).toBe(true)

  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: `Renamed ${stamp}` })).toBeVisible()
  await expect(page.locator('.request-name')).toHaveText('Admission and bed allocation')

  // Escape leaves the heading as it was
  await page.getByRole('button', { name: 'Edit request heading' }).click()
  await page.getByRole('textbox', { name: 'request heading' }).fill('Never saved')
  await page.keyboard.press('Escape')
  await expect(page.locator('.request-name')).toHaveText('Admission and bed allocation')
})

test('the headings of the document can be renamed before the PDF is made, and the wording is remembered', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await newProject(page, `Wording ${Date.now()}`)
  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('When the case sheet comes to the department the user opens the receive menu and picks the ward.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText('Typed note')).toBeVisible()
  await page.getByRole('button', { name: 'Analyse and make the document' }).click()

  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('list', { name: 'Headings' })).toBeVisible({ timeout: 60_000 })
  const goTo = async (tab: string) => { const t = dialog.getByRole('tab', { name: tab }); if (await t.isVisible()) await t.click() }
  await dialog.getByRole('button', { name: 'Rename the heading Purpose' }).click()
  const box = dialog.getByRole('textbox', { name: 'Heading for Purpose' })
  await box.fill('Why we are doing this')
  await page.keyboard.press('Escape')                                                       // only the box closes, the window stays and nothing is renamed
  await expect(box).toHaveCount(0)
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('(renamed)')).toHaveCount(0)
  await dialog.getByRole('button', { name: 'Rename the heading Purpose' }).click()
  await dialog.getByRole('textbox', { name: 'Heading for Purpose' }).fill('Why we are doing this')
  await page.keyboard.press('Enter')
  await expect(dialog.getByText('(renamed)')).toBeVisible()
  await expect(dialog.getByText('Standard heading: Purpose')).toBeVisible()

  const doc = dialog.frameLocator('iframe[title="The document as it will print"]')
  await goTo('See the document')
  await expect(doc.getByRole('heading', { name: /Why we are doing this/ })).toBeVisible()      // the document on the right follows at once
  await expect(doc.getByRole('heading', { name: /^\d+\s*Purpose$/ })).toHaveCount(0)
  await goTo('Choose')
  const sent = page.waitForRequest(r => r.method() === 'POST' && /\/api\/v1\/flows\/[0-9a-f-]+\/guide$/.test(r.url()))
  await dialog.getByRole('button', { name: 'Make the PDF' }).click()
  expect((await sent).postDataJSON()).toEqual({ titles: { purpose: 'Why we are doing this' } })
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 120_000 })

  // the next time the window opens for this document, the wording is already there
  await page.getByRole('button', { name: 'Update the document' }).click()
  await expect(dialog.getByRole('list', { name: 'Headings' }).getByText('Why we are doing this')).toBeVisible({ timeout: 60_000 })
  await dialog.getByRole('button', { name: 'Use the standard headings' }).click()
  await expect(dialog.getByText('(renamed)')).toHaveCount(0)
  await dialog.getByRole('button', { name: 'Cancel' }).click()
})

test('a heading that another request already has is refused with the reason, and can be corrected', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await newProject(page, `Clash ${Date.now()}`)
  for (const name of ['Bed transfer', 'Ward round']) {
    await page.getByRole('button', { name: 'New request' }).click()
    const form = page.getByRole('form', { name: 'New request' })
    await form.getByLabel('Name of the new request').fill(name)
    await form.getByRole('button', { name: 'Create NR' }).click()
    await expect(page.locator('.request-name')).toHaveText(name)
  }

  // "Ward round" is open: asking for the name "bed transfer" (any letters) is refused, and the box stays open with what was typed
  await page.getByRole('button', { name: 'Edit request heading' }).click()
  await page.getByRole('textbox', { name: 'request heading' }).fill('bed transfer')
  await page.keyboard.press('Enter')
  await expect(page.getByRole('alert').filter({ hasText: 'already has a request called' })).toBeVisible()
  const box = page.getByRole('textbox', { name: 'request heading' })
  await expect(box).toHaveValue('bed transfer')
  await box.fill('Ward round on Sundays')
  await page.keyboard.press('Enter')
  await expect(page.locator('.request-name')).toHaveText('Ward round on Sundays')
})
