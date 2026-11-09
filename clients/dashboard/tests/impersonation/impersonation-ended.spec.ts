// E2E coverage for the impersonation-revoked terminal flow.
//
// When an operator's impersonation grant is revoked (or its short-lived token
// expires) mid-session, the server starts rejecting the impersonation token
// with a 401. Impersonation sessions carry no refresh token, so that 401
// propagates straight through apiFetch; a global query/mutation error hook
// (query-client.ts → isImpersonationRevokedError) routes to the dedicated
// /impersonation-ended page instead of leaving a dead error banner under a
// half-loaded dashboard.
//
// Browser: chromium only, run against the already-running Vite dev server.

import { expect, test } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";

import {
  ACCESS_KEY,
  IMPERSONATED_USER,
  OPERATOR_ACTOR,
  seedImpersonationSession,
} from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

test.beforeEach(async ({ page }) => {
  await seedImpersonationSession(page, IMPERSONATED_USER, OPERATOR_ACTOR);
  await installShellMocks(page);
  // The route refuses the screen without the permission its menu entry carries.
  await mockJsonResponse(page, "**/api/v1/identity/permissions", ["Permissions.Sessions.ViewAll"]);
});

test.describe("impersonation revoked mid-session", () => {
  test("a 401 on an impersonation session routes to the terminal page", async ({ page }) => {
    // The sessions list 401s the moment the grant is revoked (the kit's Roles
    // screen, used before, redirects home since 02/11/2026). The dev build
    // surfaces the JwtBearer rejection reason on the ProblemDetails.
    await mockProblemDetails(page, "**/api/v1/identity/sessions**", 401, {
      title: "Unauthorized",
      detail: "Authentication is required to access this resource.",
    });

    await page.goto("/system/sessions");

    // Lands on the dedicated terminal page rather than showing an inline
    // error band under the half-loaded list.
    await expect(page).toHaveURL(/\/impersonation-ended$/);
    await expect(
      page.getByRole("heading", { name: /acesso do operador encerrado/i }),
    ).toBeVisible();
    await expect(
      page.getByText(/foi revogado ou expirou/i),
    ).toBeVisible();
  });

  test("'Back to sign in' clears the dead token and routes to /login", async ({ page }) => {
    await mockProblemDetails(page, "**/api/v1/identity/sessions**", 401, {
      title: "Unauthorized",
      detail: "Authentication is required to access this resource.",
    });

    await page.goto("/system/sessions");
    await expect(page).toHaveURL(/\/impersonation-ended$/);

    await page.getByRole("button", { name: /voltar para a entrada/i }).click();

    await expect(page).toHaveURL(/\/login$/);
    // The dead impersonation token is cleared so a stale token can't bounce
    // the user straight back into a 401 loop.
    const accessToken = await page.evaluate((key) => localStorage.getItem(key), ACCESS_KEY);
    expect(accessToken).toBeNull();
  });
});
