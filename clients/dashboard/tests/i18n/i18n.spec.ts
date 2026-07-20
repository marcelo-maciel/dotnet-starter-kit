import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// These specs cover Task 8 (i18n bootstrap) + Task 9 (Accept-Language header).
// Any authenticated route renders the AppShell + Topbar; the Topbar's profile
// menu button is a stable "the app mounted" signal. main.tsx awaits initI18n()
// before mounting React, so a boot failure there would leave nothing to find.

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
});

test.describe("i18n", () => {
  test("app boots with i18n initialized", async ({ page }) => {
    await page.goto("/");

    // The Topbar (which consumes the i18n instance for the profile-locale sync)
    // rendered → initI18n() resolved and React mounted.
    await expect(
      page.getByRole("button", { name: /open profile menu/i }),
    ).toBeVisible({ timeout: 10_000 });
  });

  test("apiFetch sends Accept-Language matching the active locale", async ({ page }) => {
    let seenLang: string | null = null;

    // Registered AFTER installShellMocks so this handler wins (LIFO) and can
    // inspect the request header. Return a profile whose locale matches the
    // default active locale so the sync effect does not switch languages.
    await page.route("**/api/v1/identity/profile", async (route) => {
      if (route.request().method() !== "GET") {
        await route.fallback();
        return;
      }
      seenLang = route.request().headers()["accept-language"] ?? null;
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          id: "u-test-1",
          isActive: true,
          emailConfirmed: true,
          locale: "en-US",
        }),
      });
    });

    const profileReq = page.waitForRequest(
      (r) => r.url().includes("/api/v1/identity/profile") && r.method() === "GET",
      { timeout: 10_000 },
    );
    await page.goto("/");
    await profileReq;

    expect(seenLang).toBe("en-US");
  });
});
