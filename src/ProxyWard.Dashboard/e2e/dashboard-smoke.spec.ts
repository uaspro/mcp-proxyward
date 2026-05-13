import { expect, test } from '@playwright/test'

const apiBaseUrl = process.env.PROXYWARD_E2E_API_BASE_URL
  ?? `http://127.0.0.1:${process.env.PROXYWARD_E2E_API_PORT ?? '8091'}`

test.beforeEach(async ({ request }) => {
  await request.post(`${apiBaseUrl}/__test/reset`)
})

test('dashboard smoke covers overview, audit, drift, policy, and settings', async ({ page }) => {
  const overviewRequests: string[] = []
  page.on('request', (request) => {
    const url = new URL(request.url())
    if (url.pathname === '/api/overview') {
      overviewRequests.push(request.url())
    }
  })

  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible()
  const overviewRange = page.getByRole('group', { name: 'Overview time range' })
  await expect(overviewRange).toBeVisible()
  await overviewRange.getByRole('button', { name: '30d' }).click()
  await expect(overviewRange.getByRole('button', { name: '30d' })).toHaveClass(/active/)
  await expect.poll(() => hasOverviewRequest(overviewRequests, '43200', 30)).toBe(true)
  await expect(page.getByText('path_traversal').first()).toBeVisible()
  await expect(page.getByText('fs.read').first()).toBeVisible()
  await expect(page.getByText('Resources')).toHaveCount(0)
  await expect(page.getByRole('button', { name: /^Docs$/i })).toHaveCount(0)

  const topbarSearch = page.getByPlaceholder('Search audit, tools, policy')
  await topbarSearch.fill('fs.read')
  await topbarSearch.press('Enter')
  await expect(page).toHaveURL(/\/audit$/)
  await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible()
  await expect(page.getByPlaceholder('Search server, operation, subject, reason')).toHaveValue('fs.read')
  await expect(page.getByRole('table')).toContainText('fs.read')
  await expect(page.getByRole('table')).toContainText('would block')
  await topbarSearch.fill('')

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
  await expect(page.getByText('unsupportedInspection')).toBeVisible()
  await expect(page.getByText('unsupportedStreaming')).toHaveCount(0)
  await expect(page.locator('.policy-field', { hasText: 'audit.sink' }).locator('input')).toHaveCount(0)
  await expect(page.locator('.policy-field', { hasText: 'audit.sqlitePath' }).locator('input')).toHaveCount(0)

  await page.getByRole('button', { name: /^Add server$/i }).click()
  const addServerDialog = page.getByRole('dialog', { name: /Add server policy/i })
  await expect(addServerDialog).toBeVisible()
  await addServerDialog.getByLabel('upstream').fill('http://replacement:8080/mcp')
  await expect(addServerDialog.getByText('/replacement/mcp', { exact: true })).toBeVisible()
  await expect(addServerDialog.getByText('http://127.0.0.1:8080/replacement/mcp')).toBeVisible()
  await addServerDialog.getByRole('button', { name: /Add server/i }).click()
  await expect(page.getByRole('button', { name: /replacement/i })).toBeVisible()
  await expect(page.getByText('1 server', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'mcp.json' })).toBeVisible()
  await expect(page.getByText('"url": "http://127.0.0.1:8080/replacement/mcp"')).toBeVisible()
  const toolRows = page.locator('.tool-policy-row')
  await expect(toolRows).toHaveCount(3)
  await page.getByLabel('Search server tools').fill('space')
  await expect(toolRows).toHaveCount(1)
  await expect(toolRows.first()).toContainText('space_search')
  await expect(page.getByText('1/3')).toBeVisible()
  await page.getByRole('button', { name: 'Clear tool search' }).click()
  await expect(toolRows).toHaveCount(3)
  await expect(page.locator('textarea[placeholder*="/repos/proxyward"]')).toBeVisible()
  await expect(page.locator('textarea[placeholder*="github_pat_"]')).toBeVisible()
  await expect(page.locator('textarea[placeholder*="Example only - not configured"]')).toHaveCount(4)
  await expect(page.getByText('No configured overrides. Placeholder examples are not active.')).toHaveCount(4)
  await expect(page.getByText('One filesystem root per line. Path arguments outside these roots are treated as policy violations.')).toBeVisible()
  await expect(page.getByText('One literal or /regex/ pattern per line. Matches are redacted and can be blocked on return.')).toBeVisible()
  const rootsEditor = page.locator('textarea[placeholder*="/repos/proxyward"]')
  await rootsEditor.fill('/workspace')
  await rootsEditor.press('Enter')
  await rootsEditor.type('/repos/proxyward')
  await expect(rootsEditor).toHaveValue('/workspace\n/repos/proxyward')
  await expect(page.getByText('2 configured overrides.')).toBeVisible()

  await page.getByRole('button', { name: /replacement/i }).click()
  await page.getByRole('button', { name: /^Delete$/i }).click()
  await expect(page.getByRole('dialog', { name: /Delete replacement/i })).toBeVisible()
  await page.getByRole('button', { name: /Delete server/i }).click()
  await expect(page.getByText('No server policies')).toBeVisible()
  await expect(page.getByText('dirty')).toBeVisible()
  await page.getByRole('button', { name: /^Validate$/i }).click()
  await expect(page.getByText('Policy valid')).toBeVisible()

  await page.getByRole('button', { name: /^Settings$/i }).click()
  await expect(page).toHaveURL(/\/settings$/)
  await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible()
  await expect(page.getByText('mcp-proxyward')).toBeVisible()
  await expect(page.getByText('/app/data/proxyward.db', { exact: true })).toBeVisible()
  await expect(page.getByText('unsupportedInspection')).toBeVisible()

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

test('overview latest audit events drill down to full audit history', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Latest audit events' })).toBeVisible()
  await expect(page.getByRole('table')).toContainText('fs.read')
  await page.getByRole('row', { name: /fs\.read/ }).click()

  await expect(page).toHaveURL(/\/audit\?event=101$/)
  await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible()
  await expect(page.getByLabel('fs.read')).toContainText('e2e-correlation')

  await page.reload()
  await expect(page).toHaveURL(/\/audit\?event=101$/)
  await expect(page.getByLabel('fs.read')).toContainText('e2e-correlation')

  await page.getByRole('button', { name: /Close drawer/i }).click()
  await expect(page).toHaveURL(/\/audit$/)
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
  await request.post(`${apiBaseUrl}/__test/reset?audit=7&drift=0`)
  await page.goto('/')

  await expect(page.getByRole('button', { name: /Audit log/i }).getByText('7', { exact: true })).toBeVisible()
  await expect(page.locator('.nav-dot')).toHaveCount(0)
})

function hasOverviewRequest(requests: string[], bucketSeconds: string, days: number) {
  return requests.some((requestUrl) => {
    const url = new URL(requestUrl)
    if (url.searchParams.get('bucketSeconds') !== bucketSeconds) {
      return false
    }

    const fromUtc = url.searchParams.get('fromUtc')
    const toUtc = url.searchParams.get('toUtc')
    if (!fromUtc || !toUtc) {
      return false
    }

    const durationMs = new Date(toUtc).getTime() - new Date(fromUtc).getTime()
    return Math.abs(durationMs - days * 24 * 60 * 60 * 1000) < 1000
  })
}
