import { expect, test } from "@playwright/test";
import { captureRequest, mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// PAF (O3): the form asks for the e-mail only; the server finds the company and the emailed link carries it.
const FORGOT_BY_EMAIL = "**/api/v1/identity/forgot-password-by-email";

test.describe("forgot-password page", () => {
  test("renders the form with the headline + e-mail only", async ({ page }) => {
    await page.goto("/forgot-password");

    await expect(page.getByRole("heading", { name: /recupere sua senha/i })).toBeVisible();
    await expect(page.getByLabel("E-mail")).toBeVisible();
    await expect(page.getByLabel("Empresa")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /enviar link de redefinição/i })).toBeVisible();
  });

  test("posts the e-mail to /forgot-password-by-email with no tenant header", async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem("fsh.dashboard.tenant", "empresa-antiga"));
    const captured = captureRequest(page, FORGOT_BY_EMAIL);

    await page.goto("/forgot-password");
    await page.getByLabel("E-mail").fill("alice@acme.com");
    await page.getByRole("button", { name: /enviar link de redefinição/i }).click();

    const { body, headers } = await captured.value();
    expect(body).toEqual({ email: "alice@acme.com" });
    expect(headers.tenant).toBeUndefined();
  });

  test("shows the same 'check your inbox' state after 200, naming no company", async ({ page }) => {
    await mockJsonResponse(page, FORGOT_BY_EMAIL, '""');

    await page.goto("/forgot-password");
    await page.getByLabel("E-mail").fill("bob@globex.com");
    await page.getByRole("button", { name: /enviar link de redefinição/i }).click();

    await expect(page.getByRole("heading", { name: /confira sua caixa de entrada/i })).toBeVisible();
    await expect(page.getByText(/se existir uma conta para bob@globex\.com, um link/i)).toBeVisible();
    await expect(page.getByText(/empresa/i)).toHaveCount(0);

    // "Try a different address" lets the user retry without a page reload — important after a typo.
    await expect(page.getByRole("button", { name: /tentar outro endereço/i })).toBeVisible();
  });

  test("surfaces server errors (5xx / too many attempts) on the form", async ({ page }) => {
    await mockProblemDetails(page, FORGOT_BY_EMAIL, 429, {
      title: "Too Many Requests",
      detail: "Muitas tentativas. Aguarde um minuto e tente de novo.",
    });

    await page.goto("/forgot-password");
    await page.getByLabel("E-mail").fill("alice@acme.com");
    await page.getByRole("button", { name: /enviar link de redefinição/i }).click();

    const alert = page.getByRole("alert");
    await expect(alert).toBeVisible();
    await expect(alert).toContainText(/muitas tentativas/i);
    await expect(page.getByRole("heading", { name: /confira sua caixa de entrada/i })).not.toBeVisible();
  });
});
