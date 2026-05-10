import { expect, test } from '@playwright/test'

test('dashboard smoke covers overview, audit, drift, policy, and settings', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible()
  await expect(page.getByText('path_traversal')).toBeVisible()
  await expect(page.getByText('fs.read')).toBeVisible()

  await page.getByRole('button', { name: /Audit log/i }).click()
  await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible()
  await page.getByPlaceholder('Search server, tool, reason, correlation id').fill('fs.read')
  await expect(page.getByRole('table')).toContainText('fs.read')
  await expect(page.getByRole('table')).toContainText('would block')

  await page.getByRole('button', { name: /Schema drift/i }).click()
  await expect(page.getByRole('heading', { name: 'Schema drift' })).toBeVisible()
  await expect(page.getByRole('button', { name: /repos\.search/i })).toBeVisible()
  await page.getByRole('button', { name: /repos\.search/i }).click()
  await expect(page.getByText('new search')).toBeVisible()

  await page.getByRole('button', { name: /^Policy$/i }).click()
  await expect(page.getByRole('heading', { name: 'Policy' })).toBeVisible()
  await expect(page.getByText('sha256:e2e')).toBeVisible()
  await expect(page.getByRole('button', { name: /sample/i })).toBeVisible()

  await page.getByRole('button', { name: /^Settings$/i }).click()
  await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()
  await expect(page.getByText('mcp-proxyward')).toBeVisible()
  await expect(page.getByText('/app/data/proxyward.db')).toBeVisible()
})
