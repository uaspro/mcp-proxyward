import { defineConfig, devices } from '@playwright/test'

const apiPort = Number(process.env.PROXYWARD_E2E_API_PORT ?? 8091)
const dashboardPort = Number(process.env.PROXYWARD_E2E_DASHBOARD_PORT ?? 5174)
const apiBaseUrl = process.env.PROXYWARD_E2E_API_BASE_URL ?? `http://127.0.0.1:${apiPort}`
const dashboardBaseUrl = process.env.PROXYWARD_E2E_DASHBOARD_BASE_URL ?? `http://127.0.0.1:${dashboardPort}`

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  expect: {
    timeout: 10_000,
  },
  use: {
    baseURL: dashboardBaseUrl,
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: 'node e2e/management-api-fixture.mjs',
      url: `${apiBaseUrl}/api/status`,
      reuseExistingServer: false,
      timeout: 15_000,
      env: {
        PROXYWARD_E2E_API_PORT: apiPort.toString(),
      },
    },
    {
      command: `npm run dev -- --host 127.0.0.1 --port ${dashboardPort}`,
      url: dashboardBaseUrl,
      reuseExistingServer: false,
      timeout: 15_000,
      env: {
        VITE_PROXYWARD_API_BASE_URL: apiBaseUrl,
        VITE_PROXYWARD_ADMIN_TOKEN: 'test-admin-token',
      },
    },
  ],
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
})
