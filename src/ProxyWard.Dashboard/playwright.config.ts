import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  expect: {
    timeout: 10_000,
  },
  use: {
    baseURL: 'http://127.0.0.1:5174',
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: 'node e2e/management-api-fixture.mjs',
      url: 'http://127.0.0.1:8091/api/status',
      reuseExistingServer: false,
      timeout: 15_000,
    },
    {
      command: 'npm run dev -- --host 127.0.0.1 --port 5174',
      url: 'http://127.0.0.1:5174',
      reuseExistingServer: false,
      timeout: 15_000,
      env: {
        VITE_PROXYWARD_API_BASE_URL: 'http://127.0.0.1:8091',
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
