import { defineConfig, devices } from "@playwright/test";

/**
 * Playwright config for the admin app. Mirrors the dashboard's setup —
 * tests run against a Vite dev server on port 5173 with API calls
 * intercepted via `page.route()`. See clients/admin/tests/ for specs.
 */
export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [["github"], ["html", { open: "never" }]] : "list",
  use: {
    baseURL: "http://localhost:5173",
    // The app pins pt-BR (src/i18n.ts); the suites assert the en-US catalogs, the one language every key is
    // guaranteed in, through the only override the app reads. Specs about what the PAF operator actually sees clear it.
    storageState: {
      cookies: [],
      origins: [{ origin: "http://localhost:5173", localStorage: [{ name: "fsh.admin.lng", value: "en-US" }] }],
    },
    trace: "on-first-retry",
    actionTimeout: 10_000,
    navigationTimeout: 15_000,
  },
  // Assertions get the same budget as actions. Left at the 5s default they were
  // the tightest deadline in the suite — every test ends in a toBeVisible, and
  // under CPU contention the first paint of a lazy route lands past 5s while
  // staying well inside the action and navigation budgets.
  expect: { timeout: 10_000 },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "npm run dev",
    url: "http://localhost:5173",
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
    stdout: "ignore",
    stderr: "pipe",
  },
});
