import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS } from "../helpers/shell-mocks";

// ProfileSettings is a read-only identity view. The page loads the
// current user via GET /api/v1/identity/profile. The installAdminShellMocks helper already
// stubs /identity/profile with ADMIN_PROFILE; tests that need different field
// values OVERRIDE that endpoint after the shell call (later route wins).

const PROFILE = {
  id: "u-test-1",
  userName: "rootadmin",
  email: "admin@root.com",
  firstName: "Root",
  lastName: "Admin",
  phoneNumber: "+1 555 0142",
  isActive: true,
  emailConfirmed: true,
  twoFactorEnabled: false,
  imageUrl: null,
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
});

test.describe("settings · profile", () => {
  test("renders the identity fields from the mocked profile", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", PROFILE);

    await page.goto("/settings/profile");

    const main = page.getByRole("main");
    // Section titles render with a literal "\\ " prefix → match via regex.
    await expect(main.getByText(/Identity/)).toBeVisible({ timeout: 10_000 });

    // Read-only identity inputs are surfaced via labelled fields.
    await expect(main.getByLabel(/^Username/)).toHaveValue("rootadmin");
    await expect(main.getByLabel(/^Display name/)).toHaveValue("Root Admin");
    await expect(main.getByLabel(/^Email/)).toHaveValue("admin@root.com");
    await expect(main.getByLabel(/^Phone/)).toHaveValue("+1 555 0142");
  });

  test("shows status badges driven by the profile flags", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", PROFILE);

    await page.goto("/settings/profile");

    const main = page.getByRole("main");
    await expect(main.getByText(/Identity/)).toBeVisible({ timeout: 10_000 });
    await expect(main.getByText("Active", { exact: true })).toBeVisible();
    await expect(main.getByText("Email confirmed", { exact: true })).toBeVisible();
    await expect(main.getByText("2FA off", { exact: true })).toBeVisible();
  });

  test("reflects a disabled / unverified / 2FA-enabled profile", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", {
      ...PROFILE,
      isActive: false,
      emailConfirmed: false,
      twoFactorEnabled: true,
    });

    await page.goto("/settings/profile");

    const main = page.getByRole("main");
    await expect(main.getByText("Disabled", { exact: true })).toBeVisible({ timeout: 10_000 });
    await expect(main.getByText("Email pending", { exact: true })).toBeVisible();
    await expect(main.getByText("2FA enabled", { exact: true })).toBeVisible();
  });

  // No avatar editor: the PAF never shows the profile photo, and the URL an upload stores points at R2's S3 API
  // endpoint, which has no anonymous read (decided 08/11/2026).
  test("has no avatar section nor upload affordance", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", PROFILE);

    await page.goto("/settings/profile");

    const main = page.getByRole("main");
    await expect(main.getByText(/Identity/)).toBeVisible({ timeout: 10_000 });
    await expect(main.getByText(/Avatar/)).toHaveCount(0);
    await expect(main.getByRole("button", { name: /choose image/i })).toHaveCount(0);
  });
});
