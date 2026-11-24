import { expect, test, type Page } from '@playwright/test'

// The browser demo as a visitor (for example an interviewer) uses it: no server, everything answered inside the page.
const APP = '/FlowBuilderAI/app/'

async function openPanel(page: Page) {
  if (await page.locator('#demo-panel').isHidden()) await page.locator('#demo-chip').click()
  await expect(page.locator('#demo-panel')).toBeVisible()
}
async function signInVia(page: Page, who: 'consultant' | 'administrator') {
  await openPanel(page)
  await page.getByRole('button', { name: `Sign in as ${who}` }).click()
}

test('the site opens, explains itself, and the sample project is there after signing in as the consultant', async ({ page }) => {
  const problems: string[] = []
  page.on('pageerror', e => problems.push(e.message))
  await page.goto(APP)
  await expect(page.getByLabel('Email')).toBeVisible()
  await expect(page.locator('.login-foot')).toHaveText(/browser demo/)
  await signInVia(page, 'consultant')
  await expect(page.getByText('IP case sheet receive').first()).toBeVisible()
  await expect(page.getByText(/Demo: this runs in your browser/)).toBeVisible()
  expect(problems).toEqual([])
})

test('a visitor can also type the demo account in by hand', async ({ page }) => {
  await page.goto(APP)
  await page.getByLabel('Email').fill('asha@demo.local')
  await page.getByLabel('Password').fill('Demo-Passw0rd-123')
  await page.getByRole('main').getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByText('IP case sheet receive').first()).toBeVisible()
})

test('the whole journey: new project, a note, the analysis, the document with a renamed heading, final, then the administrator verifies it', async ({ page, context }) => {
  await page.goto(APP)
  await signInVia(page, 'consultant')
  await expect(page.getByText('IP case sheet receive').first()).toBeVisible()

  // the finished example opens with its diagram and its document
  await page.getByText('IP case sheet receive').first().click()
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible()

  // a new project, from nothing
  await page.goto(`${APP}#/new`)
  await page.getByLabel('Project name').fill('Pharmacy issue to ward')
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name: 'Pharmacy issue to ward' })).toBeVisible()
  await page.getByRole('button', { name: /Type a note/ }).click()
  await page.getByRole('textbox', { name: 'Your note' }).fill('The pharmacist opens the issue screen, picks the ward and the prescription, and the system lists the items to issue.')
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText('Typed note')).toBeVisible()

  // analyse, choose what the document holds and rename a heading
  await page.getByRole('button', { name: 'Analyse and make the document' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('list', { name: 'Headings' })).toBeVisible({ timeout: 30_000 })
  await dialog.getByRole('button', { name: 'Rename the heading Purpose' }).click()
  await dialog.getByRole('textbox', { name: 'Heading for Purpose' }).fill('Why we are doing this')
  await page.keyboard.press('Enter')
  await dialog.getByRole('button', { name: 'Make the PDF' }).click()
  await expect(page.getByRole('button', { name: 'Open the PDF' })).toBeVisible({ timeout: 30_000 })

  // the printable document carries the project and the new heading
  const [paper] = await Promise.all([context.waitForEvent('page'), page.getByRole('button', { name: 'Open the PDF' }).click()])
  await expect(paper.locator('body')).toContainText('Why we are doing this', { timeout: 15_000 })
  await expect(paper.locator('body')).toContainText('Pharmacy issue to ward')
  await expect(paper.locator('body')).not.toContainText(/AOSTA|BACKBONE/)
  await paper.close()

  // mark it final
  await page.getByRole('button', { name: /Mark .*final/i }).first().click()
  const confirm = page.getByRole('dialog')
  if (await confirm.isVisible()) await confirm.getByRole('button', { name: /Mark .*final|Yes|Confirm/i }).last().click()
  await expect(page.getByText(/waiting for the administrator|Final/i).first()).toBeVisible()

  // the administrator now sees it among the final documents and verifies it
  await openPanel(page)
  await page.getByRole('button', { name: 'Sign in as administrator' }).click()
  await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible()
  await page.goto(`${APP}#/documents`)
  await expect(page.getByText('Pharmacy issue to ward').first()).toBeVisible()
  await page.getByRole('button', { name: /^Verify IMPL-PHAR-\d{4}-NR-001$/ }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Verify', exact: true }).click()
  await expect(page.getByText(/IMPL-PHAR-\d{4}-NR-001 was verified/)).toBeVisible()
})

test('the administrator’s pages all open, and the administrator can add a module', async ({ page }) => {
  await page.goto(APP)
  await signInVia(page, 'administrator')
  await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible()
  for (const route of ['#/admin/modules', '#/admin/users', '#/admin/clients', '#/admin/handling', '#/admin/ai', '#/admin/database', '#/documents', '#/usage']) {
    await page.goto(`${APP}${route}`)
    await expect(page.getByRole('heading').first()).toBeVisible()
    await expect(page.getByText('That page does not exist.')).toHaveCount(0)
    await expect(page.getByText('The demo hit a problem')).toHaveCount(0)
  }
  await page.goto(`${APP}#/admin/modules`)
  await page.getByRole('button', { name: 'Add module' }).first().click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel(/Module name/).fill('Radiology')
  await dialog.getByRole('button', { name: 'Add module' }).click()
  await expect(page.getByText('Radiology').first()).toBeVisible()
})

test('Reset demo brings the sample data back', async ({ page }) => {
  await page.goto(APP)
  await signInVia(page, 'consultant')
  await expect(page.getByText('IP case sheet receive').first()).toBeVisible()
  await page.goto(`${APP}#/new`)
  await page.getByLabel('Project name').fill('To be thrown away')
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name: 'To be thrown away' })).toBeVisible()
  await openPanel(page)
  await page.getByRole('button', { name: 'Reset demo' }).click()
  await expect(page.getByLabel('Email')).toBeVisible()
  await signInVia(page, 'consultant')
  await expect(page.getByText('IP case sheet receive').first()).toBeVisible()
  await expect(page.getByText('To be thrown away')).toHaveCount(0)
})
