import { expect, test } from "@playwright/test";
import { captureRequest, mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";

test.describe("forgot-password page", () => {
  test("renders the form with the headline + field labels", async ({ page }) => {
    await page.goto("/forgot-password");

    // The redesigned AuthShell drops the editorial eyebrow in favor of
    // the brand wordmark + headline. Assert against the headline copy
    // + form scaffolding only.
    await expect(page.getByRole("heading", { name: /recupere sua senha/i })).toBeVisible();
    await expect(page.getByLabel("E-mail")).toBeVisible();
    await expect(page.getByLabel("Empresa")).toBeVisible();
    await expect(page.getByRole("button", { name: /enviar link de redefinição/i })).toBeVisible();
  });

  test("posts to /forgot-password with the tenant header on submit", async ({ page }) => {
    const captured = captureRequest(page, "**/api/v1/identity/forgot-password");

    await page.goto("/forgot-password");
    await page.getByLabel("E-mail").fill("alice@acme.com");
    await page.getByLabel("Empresa").fill("acme");
    await page.getByRole("button", { name: /enviar link de redefinição/i }).click();

    const { body, headers } = await captured.value();
    expect(body).toMatchObject({ email: "alice@acme.com" });
    // Tenant header must override the default for cross-tenant reset flows.
    expect(headers.tenant).toBe("acme");
  });

  test("shows the 'check your inbox' success state after 200", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/forgot-password", '""');

    await page.goto("/forgot-password");
    await page.getByLabel("E-mail").fill("bob@globex.com");
    await page.getByLabel("Empresa").fill("globex");
    await page.getByRole("button", { name: /enviar link de redefinição/i }).click();

    // Success heading + echoed email/tenant in the confirmation copy.
    // The redesigned success surface renders the echoed values as
    // foreground-tinted spans inside the prose — assert against the
    // visible body text instead.
    await expect(page.getByRole("heading", { name: /confira sua caixa de entrada/i })).toBeVisible();
    await expect(page.getByText(/bob@globex\.com/)).toBeVisible();
    await expect(page.getByText(/na empresa globex/i)).toBeVisible();

    // "Try a different address" affordance lets the user retry without
    // a page reload — important when they typo their email.
    await expect(page.getByRole("button", { name: /tentar outro endereço/i })).toBeVisible();
  });

  test("surfaces server errors (5xx / tenant-not-resolvable) on the form", async ({ page }) => {
    await mockProblemDetails(page, "**/api/v1/identity/forgot-password", 500, {
      title: "Tenant resolution failed",
      detail: "No tenant matching the supplied identifier.",
    });

    await page.goto("/forgot-password");
    await page.getByLabel("E-mail").fill("alice@acme.com");
    await page.getByLabel("Empresa").fill("does-not-exist");
    await page.getByRole("button", { name: /enviar link de redefinição/i }).click();

    // Error band is rendered (role=alert) with the server's detail surfaced.
    const alert = page.getByRole("alert");
    await expect(alert).toBeVisible();
    await expect(alert).toContainText(/no tenant matching/i);

    // Still on the form, not the success state.
    await expect(page.getByRole("heading", { name: /confira sua caixa de entrada/i })).not.toBeVisible();
  });
});
