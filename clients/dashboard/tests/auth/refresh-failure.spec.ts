import { expect, test, type Page, type Route } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// C3: the refresh ends the session only when the server refuses the refresh token (401/403). A 503 while the
// API restarts, a 429 or a dropped connection says nothing about the token: the person stays signed in and
// the next request tries again. Before the fix every non-2xx refresh cleared the tokens and sent the guichê
// back to /login mid-turn.

const ACCESS_KEY = "fsh.dashboard.accessToken";
const REFRESH_KEY = "fsh.dashboard.refreshToken";

type RefreshAnswer = { label: string; answer: (route: Route) => Promise<void> };

const KEEPS_SESSION: RefreshAnswer[] = [
  { label: "503", answer: (r) => r.fulfill({ status: 503, body: "" }) },
  { label: "429", answer: (r) => r.fulfill({ status: 429, body: "" }) },
  { label: "network failure", answer: (r) => r.abort("connectionrefused") },
];

/** Every permissions read answers 401, so the app refreshes; the refresh answers as the test says. */
async function expireMidSession(page: Page, refresh: (route: Route) => Promise<void>) {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  let refreshCalls = 0;
  await page.route("**/api/v1/identity/permissions", (route) =>
    route.fulfill({
      status: 401,
      headers: { "Content-Type": "application/problem+json" },
      body: JSON.stringify({ title: "Unauthorized", status: 401 }),
    }),
  );
  await page.route("**/api/v1/identity/token/refresh", async (route) => {
    refreshCalls += 1;
    await refresh(route);
  });
  return { refreshCalls: () => refreshCalls };
}

test.describe("refresh failure mid-session", () => {
  for (const { label, answer } of KEEPS_SESSION) {
    test(`a refresh that gets ${label} keeps the session`, async ({ page }) => {
      const probe = await expireMidSession(page, answer);
      await page.goto("/");

      await expect.poll(probe.refreshCalls).toBeGreaterThan(0);
      await expect(page).not.toHaveURL(/\/login$/);
      expect(await page.evaluate((k) => localStorage.getItem(k), ACCESS_KEY)).not.toBeNull();
      expect(await page.evaluate((k) => localStorage.getItem(k), REFRESH_KEY)).toBe("fake-refresh-token");
    });
  }

  // Negative control: the same setup with a refusal does sign out, so the cases above are not passing vacuously.
  test("a refresh refused with 401 ends the session", async ({ page }) => {
    const probe = await expireMidSession(page, (r) =>
      r.fulfill({
        status: 401,
        headers: { "Content-Type": "application/problem+json" },
        body: JSON.stringify({ title: "Unauthorized", status: 401 }),
      }),
    );
    await page.goto("/");

    await expect.poll(probe.refreshCalls).toBeGreaterThan(0);
    await expect(page).toHaveURL(/\/login$/);
    expect(await page.evaluate((k) => localStorage.getItem(k), REFRESH_KEY)).toBeNull();
  });
});
