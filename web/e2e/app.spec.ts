import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

const PASSWORD = process.env.FLOWCAPTURE_DEMO_PASSWORD ?? 'Demo-Passw0rd-123'
const ADMIN = { email: 'admin@flowcapture.local', password: process.env.FLOWCAPTURE_ADMIN_PASSWORD ?? 'ChangeMe-Dev-12345' }

async function signIn(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).not.toHaveURL(/\/login/)                              // wait until it has worked, or the next page load would cancel it
}

async function signOut(page: Page) {
  const out = page.getByRole('menuitem', { name: 'Sign out' })
  if (!(await out.isVisible())) await page.getByRole('button', { name: /Account menu/ }).click()
  await out.click()
  await expect(page).toHaveURL(/\/login$/)
}

async function newProject(page: Page, name: string) {
  await page.getByRole('link', { name: 'New project' }).first().click()
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
}

test('a wrong password is explained, and an administrator lands on the admin pages', async ({ page }) => {
  await page.goto('/')
  await expect(page).toHaveURL(/\/login$/)
  await page.getByLabel('Email').fill(ADMIN.email)
  await page.getByLabel('Password').fill('not-the-password')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('alert')).toContainText('incorrect')
  await page.getByLabel('Password').fill(ADMIN.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible()
})

test('the administrator adds a user, who can then sign in and sees an empty start', async ({ page, browser }) => {
  const email = `e2e.${Date.now()}@demo.local`
  await signIn(page, ADMIN.email, ADMIN.password)
  await page.goto('/admin/users')
  await page.getByRole('button', { name: 'Add user' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Add a user' })
  await dialog.getByLabel('Full name').fill('E2E Person')
  await dialog.getByLabel(/Email/).fill(email)
  const password = await dialog.getByLabel('Password').inputValue()
  await dialog.getByRole('button', { name: 'Add user' }).click()
  await expect(page.getByLabel('Sign-in details')).toContainText(password)
  await page.getByRole('button', { name: 'Done' }).click()
  const row = page.getByRole('listitem').filter({ hasText: email })
  await expect(row).toBeVisible()

  const other = await (await browser.newContext()).newPage()
  await signIn(other, email, password)
  await expect(other.getByRole('heading', { name: 'Hello, E2E' })).toBeVisible()
  await expect(other.getByRole('heading', { name: 'Make your first project' })).toBeVisible()

  // and the administrator can stop that access again
  await row.getByRole('button', { name: /Stop access/ }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Stop access' }).click()
  await expect(row.getByText('Stopped')).toBeVisible()
})

test('the administrator makes a client and assigns a user, who can then start a project for it', async ({ page, browser }) => {
  const stamp = Date.now()
  const email = `client.${stamp}@demo.local`
  const clientName = `E2E Client ${stamp}`
  await signIn(page, ADMIN.email, ADMIN.password)
  await page.goto('/admin/users')
  await page.getByRole('button', { name: 'Add user' }).first().click()
  const add = page.getByRole('dialog', { name: 'Add a user' })
  const person = `Client Person ${stamp}`
  await add.getByLabel('Full name').fill(person)
  await add.getByLabel(/Email/).fill(email)
  const password = await add.getByLabel('Password').inputValue()
  await add.getByRole('button', { name: 'Add user' }).click()
  await page.getByRole('button', { name: 'Done' }).click()

  // with no client yet, the new user cannot start a project and is told whom to ask
  const other = await (await browser.newContext()).newPage()
  await signIn(other, email, password)
  await other.getByRole('link', { name: 'New project' }).first().click()
  await expect(other.getByText('You are not assigned to a client yet')).toBeVisible()
  await expect(other.getByLabel('Project name')).toHaveCount(0)

  // the administrator makes the client and ticks the person
  await page.goto('/admin/clients')
  await page.getByRole('button', { name: /Add client/ }).first().click()
  const form = page.getByRole('dialog', { name: 'Add a client' })
  await form.getByLabel(/Client name/).fill(clientName)
  await form.getByRole('button', { name: 'Add client' }).click()
  const people = page.getByRole('dialog', { name: `People for ${clientName}` })
  await people.getByLabel(new RegExp(person)).check()
  await people.getByRole('button', { name: 'Save' }).click()
  await expect(people).toHaveCount(0)
  await page.goto(`/admin/clients?q=${encodeURIComponent(clientName)}`)                  // the test database holds many clients: search for this one
  await expect(page.getByRole('listitem').filter({ hasText: clientName })).toContainText(`1 person: ${person}`)

  // now the user picks it up and starts a project for it
  await other.reload()
  await expect(other.getByRole('textbox', { name: 'Client' })).toHaveValue(clientName)
  const project = `Ward ${stamp}`
  await other.getByLabel('Project name').fill(project)
  await other.getByRole('button', { name: 'Create and add notes' }).click()
  await expect(other.getByRole('heading', { name: project })).toBeVisible()
  await other.goto('/')
  await expect(other.getByText(clientName).first()).toBeVisible()          // the card names the client
})

test('a user changes their own password, and the administrator can delete a user', async ({ page, browser, request }) => {
  const stamp = Date.now()
  const email = `pw.${stamp}@demo.local`
  const name = `Password Person ${stamp}`
  const NEW = 'Brand-New-Passw0rd-9'
  const login = await request.post('/api/v1/auth/login', { data: { email: ADMIN.email, password: ADMIN.password, deviceId: 'e2e' } })
  const headers = { Authorization: `Bearer ${(await login.json()).accessToken}` }
  await request.post('/api/v1/users', { headers, data: { email, displayName: name, password: PASSWORD, roles: ['ImplementationLead'] } })

  // the user changes their own password from the account menu
  await signIn(page, email)
  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('menuitem', { name: 'Change password' }).click()
  const dialog = page.getByRole('dialog', { name: 'Change your password' })
  await dialog.getByLabel('Current password').fill(PASSWORD)
  await dialog.getByLabel('New password', { exact: true }).fill(NEW)
  await dialog.getByLabel('New password again').fill(NEW)
  await dialog.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText('Your password was changed.')).toBeVisible()
  await signOut(page)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)                         // the old one is refused
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('alert')).toContainText('incorrect')
  await page.getByLabel('Password').fill(NEW)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: /Hello, Password/ })).toBeVisible()

  // the administrator deletes them; they cannot sign in again
  const admin = await (await browser.newContext()).newPage()
  await signIn(admin, ADMIN.email, ADMIN.password)
  await admin.goto(`/admin/users?q=${encodeURIComponent(email)}`)          // the list is long and in pages: look the person up, as the administrator would
  await admin.getByRole('button', { name: `Delete ${name}` }).click()
  await admin.getByRole('dialog').getByRole('button', { name: 'Delete user' }).click()
  await expect(admin.getByText(`${name} was deleted.`)).toBeVisible()
  await expect(admin.getByRole('listitem').filter({ hasText: email })).toHaveCount(0)
  const again = await (await browser.newContext()).newPage()
  await again.goto('/login')
  await again.getByLabel('Email').fill(email)
  await again.getByLabel('Password').fill(NEW)
  await again.getByRole('button', { name: 'Sign in' }).click()
  await expect(again.getByRole('alert')).toContainText('incorrect')
})

test('an ordinary user cannot open the admin pages', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  await page.goto('/admin/users')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Users' })).toHaveCount(0)
})

test('a reload keeps you signed in and a project address can be shared', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  await newProject(page, `Reload check ${Date.now()}`)
  const address = page.url()
  await page.reload()
  await expect(page).toHaveURL(address)
  await expect(page.getByRole('heading', { name: /Add your notes/ })).toBeVisible()
})

/** A user who works for a client of their own (made through the API as the administrator), so they share nothing with the demo users. */
async function outsider(request: APIRequestContext): Promise<{ email: string; password: string }> {
  const login = await request.post('/api/v1/auth/login', { data: { email: ADMIN.email, password: ADMIN.password, deviceId: 'e2e' } })
  const headers = { Authorization: `Bearer ${(await login.json()).accessToken}` }
  const stamp = Date.now()
  const email = `outsider.${stamp}@demo.local`
  const user = await (await request.post('/api/v1/users', { headers, data: { email, displayName: 'Outside Person', password: PASSWORD, roles: ['ImplementationLead'] } })).json()
  const client = await (await request.post('/api/v1/clients', { headers, data: { name: `Other Client ${stamp}`, location: null } })).json()
  await request.put(`/api/v1/clients/${client.id}/users`, { headers, data: { userIds: [user.id] } })
  return { email, password: PASSWORD }
}

test('someone on another client cannot open your project, even with its address', async ({ page, browser, request }) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  await newProject(page, `Private ${Date.now()}`)
  const address = page.url()
  const stranger = await outsider(request)
  const other = await (await browser.newContext()).newPage()
  await signIn(other, stranger.email, stranger.password)
  await expect(other.getByRole('heading', { name: 'Hello, Outside' })).toBeVisible()
  await other.goto(address)
  await expect(other.getByText(/not a member|not found|cannot open/i).first()).toBeVisible()
})

test('a colleague on the same client sees the project and can add notes, but cannot delete it', async ({ page, browser }) => {
  await signIn(page, 'asha@demo.local')
  const name = `Shared ${Date.now()}`
  await newProject(page, name)
  const address = page.url()
  const mate = await (await browser.newContext()).newPage()
  await signIn(mate, 'ravi@demo.local')
  await mate.goto(address)
  await expect(mate.getByRole('heading', { name })).toBeVisible()
  await expect(mate.getByRole('button', { name: /Type a note/ })).toBeVisible()          // can add notes
  await expect(mate.getByRole('button', { name: /Delete project/ })).toHaveCount(0)      // only the lead can delete
})

test('with no signal a typed note waits on the device and is sent when the connection returns', async ({ page, context }) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  await newProject(page, `Offline check ${Date.now()}`)
  await page.getByRole('button', { name: /Type a note/ }).click()

  await context.setOffline(true)
  const text = `Offline note ${Date.now()}`
  await page.getByRole('textbox', { name: 'Your note' }).fill(text)
  await page.getByRole('button', { name: 'Add note' }).click()
  await expect(page.getByText(text)).toBeVisible()
  await expect(page.getByText('Saved on this device, waiting to send')).toBeVisible()
  await expect(page.getByRole('button', { name: /Sync status: Offline · 1 waiting/ })).toBeVisible()

  await context.setOffline(false)
  await expect(page.getByText('Saved on this device, waiting to send')).toHaveCount(0, { timeout: 40_000 })
  await expect(page.getByText(text)).toBeVisible()
  await expect(page.getByRole('button', { name: /Sync status/ })).toHaveCount(0)         // nothing waiting, so nothing to show
})

test('the menu is along the top on a computer and along the bottom on a phone', async ({ page }, info) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  const phone = info.project.name === 'phone'
  const nav = page.getByRole('navigation', { name: phone ? 'Main (phone)' : 'Main', exact: true })
  await expect(nav).toBeVisible()
  await expect(page.getByRole('navigation', { name: phone ? 'Main' : 'Main (phone)', exact: true })).toBeHidden()
  await nav.getByRole('link', { name: 'Documents' }).click()
  await expect(page.getByRole('heading', { name: 'Documents' })).toBeVisible()
  if (phone) {
    const box = await nav.boundingBox()
    expect(box!.y + box!.height).toBeGreaterThan(800)                                      // at the bottom edge of an 844 pixel screen
  }
  await nav.getByRole('link', { name: 'Projects' }).click()
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
})

test('nothing runs off the side of the screen on any page', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  for (const path of ['/', '/new', '/documents']) {
    await page.goto(path)
    await page.waitForLoadState('networkidle')
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
    expect(overflow, path).toBeLessThanOrEqual(1)
  }
})

test('the app can be installed: manifest, icons and a service worker', async ({ page, request }) => {
  await page.goto('/login')
  const manifest = await request.get('/manifest.webmanifest')
  expect(manifest.ok()).toBeTruthy()
  const m = await manifest.json()
  expect(m.display).toBe('standalone')
  for (const icon of m.icons) expect((await request.get(icon.src)).ok(), icon.src).toBeTruthy()
  const registered = await page.evaluate(async () => !!(await navigator.serviceWorker.ready))
  expect(registered).toBe(true)
})

test('the theme can be switched from the account menu and is remembered', async ({ page }) => {
  await signIn(page, 'asha@demo.local')
  await expect(page.getByRole('heading', { name: 'Hello, Asha' })).toBeVisible()
  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('group', { name: 'Theme' }).getByRole('button', { name: 'Dark' }).click()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.reload()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('group', { name: 'Theme' }).getByRole('button', { name: 'Light' }).click()
  await signOut(page)
})
