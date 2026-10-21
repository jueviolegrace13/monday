import { defineConfig, devices } from '@playwright/test';

// End-to-end tests against `ng serve` on a local port. The API is stubbed in each test, so nothing
// leaves this machine except the graph library from its CDN. Never point this at production.
const PORT = 4300;

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: `http://localhost:${PORT}`,
    ...devices['Desktop Chrome'],
    viewport: { width: 1440, height: 900 },
  },
  webServer: {
    command: `npx ng serve --port ${PORT}`,
    url: `http://localhost:${PORT}`,
    reuseExistingServer: true,
    timeout: 180_000,
  },
});
