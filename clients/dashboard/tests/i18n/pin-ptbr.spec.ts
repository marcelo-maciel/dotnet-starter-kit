import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { DEFAULT_PROFILE, installShellMocks } from "../helpers/shell-mocks";

// The PAF is a PT-BR product: nothing a user carries (browser language, the locale stored on their profile) moves
// the dashboard off pt-BR, and the API is asked for pt-BR too, so server messages arrive in Portuguese. Replaces the
// upstream hydration-guard spec: the topbar no longer adopts the profile's locale at all.

test.describe("pt-BR pin", () => {
  // What a real user has: no override in storage, an English browser.
  test.use({ storageState: { cookies: [], origins: [] }, locale: "en-US" });

  test("an English browser and an English profile still read pt-BR", async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/identity/profile", { ...DEFAULT_PROFILE, locale: "en-US" });

    const profileReq = page.waitForRequest(
      (r) => r.url().includes("/api/v1/identity/profile") && r.method() === "GET",
    );
    await page.goto("/");

    await expect(page.getByRole("button", { name: "Abrir menu do perfil" })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "pt-BR");
    expect((await profileReq).headers()["accept-language"]).toBe("pt-BR");
    // The profile's locale is never written back as the user's choice.
    expect(await page.evaluate(() => localStorage.getItem("fsh.dashboard.lng"))).toBeNull();
  });
});

test.describe("explicit override", () => {
  // The suites' en-US override (playwright.config.ts) is the only way off pt-BR, and the document follows it.
  test("the document language attribute follows the active locale", async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);

    await page.goto("/");

    await expect(page.getByRole("button", { name: "Open profile menu" })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", "en-US");
  });
});
