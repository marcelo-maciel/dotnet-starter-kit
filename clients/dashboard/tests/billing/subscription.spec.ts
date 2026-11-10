import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks, paged } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// ── Fixtures ─────────────────────────────────────────────────────────

const SUBSCRIPTION = {
  id: "sub-1",
  tenantId: "acme",
  planId: "plan-scale",
  planKey: "Scale",
  startUtc: new Date(Date.UTC(2026, 0, 1)).toISOString(),
  endUtc: null,
  status: "Active",
};

/** Status with plenty of runway — banner stays hidden. */
const HEALTHY_STATUS = {
  id: "acme",
  name: "Acme Corp",
  isActive: true,
  validUpto: new Date(Date.now() + 90 * 24 * 60 * 60 * 1000).toISOString(),
  hasConnectionString: false,
  adminEmail: "admin@acme.com",
  issuer: null,
  plan: "Scale",
  expiryState: "Active",
  graceEndsUtc: new Date(Date.now() + 97 * 24 * 60 * 60 * 1000).toISOString(),
};

/** Active but within the 7-day window — soft info bar should appear. */
const NEARING_STATUS = {
  ...HEALTHY_STATUS,
  validUpto: new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString(),
  expiryState: "Active",
};

/** Expired, still inside the grace window — warning bar should appear. */
const GRACE_STATUS = {
  ...HEALTHY_STATUS,
  validUpto: new Date(Date.now() - 2 * 24 * 60 * 60 * 1000).toISOString(),
  expiryState: "InGrace",
  graceEndsUtc: new Date(Date.now() + 5 * 24 * 60 * 60 * 1000).toISOString(),
};

/** Fully lapsed — grace exhausted. Persistent danger bar should appear. */
const EXPIRED_STATUS = {
  ...HEALTHY_STATUS,
  validUpto: new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString(),
  expiryState: "Expired",
  graceEndsUtc: new Date(Date.now() - 16 * 24 * 60 * 60 * 1000).toISOString(),
};

// ── Expiry banner ──────────────────────────────────────────────────────

test.describe("expiry banner", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/billing/subscriptions/me**", SUBSCRIPTION);
    await mockJsonResponse(page, "**/api/v1/billing/usage**", []);
    await mockJsonResponse(page, "**/api/v1/audits**", paged([]));
  });

  test("warns while in grace", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", GRACE_STATUS);
    await page.goto("/overview");
    await expect(page.getByText(/sua assinatura expirou\. restam/i)).toBeVisible();
    await expect(page.getByText(/de carência/i)).toBeVisible();
  });

  test("informs when nearing expiry", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", NEARING_STATUS);
    await page.goto("/overview");
    await expect(page.getByText(/sua assinatura expira em/i)).toBeVisible();
  });

  test("pins a persistent bar when expired (no dismiss)", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", EXPIRED_STATUS);
    await page.goto("/overview");
    await expect(page.getByText(/sua assinatura expirou\. fale/i)).toBeVisible();
    // Expired is the hardest state — it cannot be dismissed.
    await expect(
      page.getByRole("button", { name: /dispensar aviso da assinatura/i }),
    ).toHaveCount(0);
  });

  test("is absent when healthy", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", HEALTHY_STATUS);
    await page.goto("/overview");
    // Wait for the page to settle, then assert the bar never showed.
    await expect(page.getByRole("heading", { name: /(bom dia|boa tarde|boa noite)/i })).toBeVisible();
    await expect(page.getByText(/sua assinatura expir/i)).toHaveCount(0);
  });

  test("can be dismissed for the session", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", GRACE_STATUS);
    await page.goto("/overview");
    await expect(page.getByText(/sua assinatura expirou\. restam/i)).toBeVisible();
    await page.getByRole("button", { name: /dispensar aviso da assinatura/i }).click();
    await expect(page.getByText(/sua assinatura expirou\. restam/i)).toHaveCount(0);
  });
});

