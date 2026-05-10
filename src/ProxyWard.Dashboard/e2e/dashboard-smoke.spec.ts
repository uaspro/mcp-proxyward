import { expect, test } from '@playwright/test'

test.beforeEach(async ({ request }) => {
  await request.post('http://127.0.0.1:8091/__test/reset')
})

test('dashboard smoke covers overview, audit, drift, policy, and settings', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible()
  await expect(page.getByText('path_traversal')).toBeVisible()
  await expect(page.getByText('fs.read')).toBeVisible()

  await page.getByRole('button', { name: /Audit log/i }).click()
  await expect(page).toHaveURL(/\/audit$/)
  await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible()
  await page.getByPlaceholder('Search server, tool, reason, correlation id').fill('fs.read')
  await expect(page.getByRole('table')).toContainText('fs.read')
  await expect(page.getByRole('table')).toContainText('would block')

  await page.getByRole('button', { name: /Schema drift/i }).click()
  await expect(page).toHaveURL(/\/schema-drift$/)
  await expect(page.getByRole('heading', { name: 'Schema drift' })).toBeVisible()
  await expect(page.getByRole('button', { name: /repos\.search/i })).toBeVisible()
  await page.getByRole('button', { name: /repos\.search/i }).click()
  await expect(page.getByText('new search')).toBeVisible()

  await page.getByRole('button', { name: /^Policy$/i }).click()
  await expect(page).toHaveURL(/\/policy$/)
  await expect(page.getByRole('heading', { name: 'Policy', exact: true })).toBeVisible()
  await expect(page.getByText('sha256:e2e')).toBeVisible()

  await page.getByRole('button', { name: /^Add server$/i }).click()
  const addServerDialog = page.getByRole('dialog', { name: /Add server policy/i })
  await expect(addServerDialog).toBeVisible()
  await addServerDialog.getByLabel('id').fill('replacement')
  await addServerDialog.getByLabel('route').fill('/replacement/mcp')
  await addServerDialog.getByLabel('upstream').fill('http://replacement:8080/mcp')
  await addServerDialog.getByRole('button', { name: /Add server/i }).click()
  await expect(page.getByRole('button', { name: /replacement/i })).toBeVisible()
  await expect(page.getByText('2 servers', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: /sample/i }).click()
  await expect(page.getByRole('button', { name: /^Delete$/i })).toBeVisible()
  await page.getByRole('button', { name: /^Delete$/i }).click()
  await expect(page.getByRole('dialog', { name: /Delete sample/i })).toBeVisible()
  await page.getByRole('button', { name: /Delete server/i }).click()
  await expect(page.getByRole('button', { name: /sample/i })).toHaveCount(0)

  await page.getByRole('button', { name: /replacement/i }).click()
  await page.getByRole('button', { name: /^Delete$/i }).click()
  await expect(page.getByRole('dialog', { name: /Delete replacement/i })).toBeVisible()
  await page.getByRole('button', { name: /Delete server/i }).click()
  await expect(page.getByText('No server policies')).toBeVisible()
  await expect(page.getByText('dirty')).toBeVisible()
  await page.getByRole('button', { name: /^Validate$/i }).click()
  await expect(page.getByText('servers must contain at least one server')).toBeVisible()

  await page.getByRole('button', { name: /^Settings$/i }).click()
  await expect(page).toHaveURL(/\/settings$/)
  await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()
  await expect(page.getByText('mcp-proxyward')).toBeVisible()
  await expect(page.getByText('/app/data/proxyward.db', { exact: true })).toBeVisible()

  await page.reload()
  await expect(page).toHaveURL(/\/settings$/)
  await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()
})

test('dashboard can load directly into a routed screen', async ({ page }) => {
  await page.goto('/policy')
  await expect(page.getByRole('heading', { name: 'Policy', exact: true })).toBeVisible()

  await page.reload()
  await expect(page).toHaveURL(/\/policy$/)
  await expect(page.getByRole('heading', { name: 'Policy', exact: true })).toBeVisible()
})

test('audit to enforce mode switch shows confirmation and updates topbar mode', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('button', { name: /^audit$/i })).toBeVisible()
  await page.getByRole('button', { name: /^audit$/i }).click()

  const dialog = page.getByRole('dialog', { name: /Switch to enforce mode/i })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('Type ENFORCE to confirm')).toBeVisible()

  const confirmButton = dialog.getByRole('button', { name: /^Confirm$/i })
  await expect(confirmButton).toBeDisabled()

  await dialog.getByLabel('I have reviewed the impact preview.').check()
  await dialog.getByLabel('Type ENFORCE to confirm').fill('ENFORCE')
  await expect(confirmButton).toBeEnabled()
  await confirmButton.click()

  await expect(dialog).toHaveCount(0)
  await expect(page.getByRole('button', { name: /^enforce$/i })).toBeVisible()

  await page.reload()
  await expect(page.getByRole('button', { name: /^enforce$/i })).toBeVisible()
})

test('policy screen audit to enforce apply shows confirmation and updates topbar mode', async ({ page }) => {
  await page.goto('/policy')

  await expect(page.locator('.topbar .mode-pill.audit')).toHaveText(/audit/i)
  await page.getByRole('button', { name: /^Enforce$/ }).click()
  await page.getByRole('button', { name: /^Apply$/ }).click()

  const dialog = page.getByRole('dialog', { name: /Switch to enforce mode/i })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('Type ENFORCE to confirm')).toBeVisible()

  const applyButton = dialog.getByRole('button', { name: /^Apply$/i })
  await expect(applyButton).toBeDisabled()

  await dialog.getByLabel('I have reviewed the impact preview.').check()
  await dialog.getByLabel('Type ENFORCE to confirm').fill('ENFORCE')
  await expect(applyButton).toBeEnabled()
  await applyButton.click()

  await expect(dialog).toHaveCount(0)
  await expect(page.locator('.topbar .mode-pill.enforce')).toHaveText(/enforce/i)
  await expect(page.getByText('Policy applied')).toBeVisible()
})

test('navigation metrics use API counts and hide drift indicator when zero', async ({ page, request }) => {
  await request.post('http://127.0.0.1:8091/__test/reset?audit=7&drift=0')
  await page.goto('/')

  await expect(page.getByRole('button', { name: /Audit log/i }).getByText('7', { exact: true })).toBeVisible()
  await expect(page.locator('.nav-dot')).toHaveCount(0)
})
