import { expect, test } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

function profile2fa(enabled: boolean) {
  return {
    id: TEST_USER.sub,
    userName: "alice",
    email: TEST_USER.email,
    firstName: TEST_USER.firstName,
    lastName: TEST_USER.lastName,
    phoneNumber: "",
    isActive: true,
    emailConfirmed: true,
    twoFactorEnabled: enabled,
  };
}

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  // Sessions endpoint isn't the focus here — return an empty list so
  // the SessionsCard renders its empty state without 401-ing.
  await mockJsonResponse(page, "**/api/v1/identity/sessions/me", []);
});

test.describe("settings/security — change password (Dialog)", () => {
  test.beforeEach(async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", profile2fa(false));
  });

  test("opens the dialog when 'Change password' is clicked", async ({ page }) => {
    await page.goto("/settings/security");
    await page.getByRole("button", { name: /trocar senha/i }).click();

    await expect(
      page.getByRole("heading", { name: /trocar senha/i, level: 2 }),
    ).toBeVisible();
    await expect(page.getByLabel("Senha atual")).toBeFocused();
  });

  test("client-side validation: empty/short/mismatched", async ({ page }) => {
    await page.goto("/settings/security");
    await page.getByRole("button", { name: /trocar senha/i }).click();

    // Scope getByLabel into the dialog so the on-page password card
    // (and any other "Password" label on the page) doesn't ambiguate
    // — Radix Dialog announces role="dialog".
    const dialog = page.getByRole("dialog");

    await dialog.getByLabel("Senha atual").fill("oldsecret");
    await dialog.getByLabel("Nova senha", { exact: true }).fill("short");
    await dialog.getByLabel("Confirme a nova senha").fill("short");
    await dialog.getByRole("button", { name: /atualizar senha/i }).click();
    await expect(dialog.getByText(/pelo menos 8 caracteres/i)).toBeVisible();

    await dialog.getByLabel("Nova senha", { exact: true }).fill("longenough123");
    await dialog.getByLabel("Confirme a nova senha").fill("differentpw123");
    await dialog.getByRole("button", { name: /atualizar senha/i }).click();
    // Scope to the submit-error alert — the live "…don't match yet" hint under
    // the confirm field also contains this phrase.
    await expect(dialog.getByRole("alert")).toContainText(/as senhas não conferem/i);

    // Current === new is blocked — keeps users from "rotating" to the
    // same value just to reset a forgotten password.
    await dialog.getByLabel("Senha atual").fill("longenough123");
    await dialog.getByLabel("Nova senha", { exact: true }).fill("longenough123");
    await dialog.getByLabel("Confirme a nova senha").fill("longenough123");
    await dialog.getByRole("button", { name: /atualizar senha/i }).click();
    await expect(dialog.getByText(/precisa ser diferente/i)).toBeVisible();
  });

  test("POSTs to /change-password on success and closes the dialog", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/change-password", '""');

    await page.goto("/settings/security");
    await page.getByRole("button", { name: /trocar senha/i }).click();

    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Senha atual").fill("oldsecret123");
    await dialog.getByLabel("Nova senha", { exact: true }).fill("newSecret!1234");
    await dialog.getByLabel("Confirme a nova senha").fill("newSecret!1234");

    const reqPromise = page.waitForRequest(
      (r) =>
        r.url().includes("/api/v1/identity/change-password") && r.method() === "POST",
      { timeout: 5_000 },
    );
    await dialog.getByRole("button", { name: /atualizar senha/i }).click();
    const req = await reqPromise;

    expect(JSON.parse(req.postData() ?? "{}")).toMatchObject({
      password: "oldsecret123",
      newPassword: "newSecret!1234",
      confirmNewPassword: "newSecret!1234",
    });

    await expect(page.getByText(/senha trocada/i)).toBeVisible();
    // Dialog closes on success.
    await expect(page.getByRole("dialog")).not.toBeVisible();
  });

  test("surfaces server errors inside the dialog without closing", async ({ page }) => {
    await mockProblemDetails(page, "**/api/v1/identity/change-password", 400, {
      title: "Invalid password",
      detail: "Current password is incorrect.",
    });

    await page.goto("/settings/security");
    await page.getByRole("button", { name: /trocar senha/i }).click();

    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Senha atual").fill("wrongsecret");
    await dialog.getByLabel("Nova senha", { exact: true }).fill("newSecret!1234");
    await dialog.getByLabel("Confirme a nova senha").fill("newSecret!1234");
    await dialog.getByRole("button", { name: /atualizar senha/i }).click();

    await expect(dialog.getByText(/current password is incorrect/i)).toBeVisible();
    // Dialog stays open on error so user can correct without re-entering.
    await expect(dialog).toBeVisible();
  });
});

test.describe("settings/security — two-factor enroll (disabled → enabled)", () => {
  test.beforeEach(async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", profile2fa(false));
  });

  test("shows the 'Enable two-factor' affordance when 2FA is off", async ({ page }) => {
    await page.goto("/settings/security");

    // CardTitle in the shadcn-style Card is a styled <div>, not a heading.
    // The primary affordance is the button — assert on that directly.
    await expect(page.getByRole("button", { name: /ativar verificação em duas etapas/i })).toBeVisible();
    // The "disabled" badge inside the card title is also a reliable signal.
    await expect(page.getByText(/^desativada$/i)).toBeVisible();
  });

  test("clicking enable triggers POST /2fa/enroll and reveals the QR + manual key", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/2fa/enroll", {
      sharedKey: "ABCD-EFGH-IJKL-MNOP",
      authenticatorUri: "otpauth://totp/FSH:alice?secret=ABCDEFGHIJKLMNOP&issuer=FSH",
    });

    await page.goto("/settings/security");
    await page.getByRole("button", { name: /ativar verificação em duas etapas/i }).click();

    // QR container + manual key + verification input render.
    await expect(page.getByRole("img", { name: /qr code da verificação em duas etapas/i })).toBeVisible();
    await expect(page.getByText("ABCD-EFGH-IJKL-MNOP")).toBeVisible();
    await expect(page.getByLabel(/código de 6 dígitos/i)).toBeVisible();
  });

  test("submitting the 6-digit code enables 2FA and refetches profile", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/2fa/enroll", {
      sharedKey: "ABCD-EFGH-IJKL-MNOP",
      authenticatorUri: "otpauth://totp/FSH:alice?secret=AAAAAA",
    });
    await mockJsonResponse(page, "**/api/v1/identity/2fa/verify", { success: true });

    await page.goto("/settings/security");
    await page.getByRole("button", { name: /ativar verificação em duas etapas/i }).click();

    const code = page.getByLabel(/código de 6 dígitos/i);
    await code.fill("12 34 56");

    const reqPromise = page.waitForRequest(
      (r) =>
        r.url().includes("/api/v1/identity/2fa/verify") && r.method() === "POST",
      { timeout: 5_000 },
    );
    await page.getByRole("button", { name: /confirmar e ativar/i }).click();
    const req = await reqPromise;

    // Whitespace stripped before submit (the handler does .replace(/\s/g, "")).
    expect(JSON.parse(req.postData() ?? "{}")).toMatchObject({ code: "123456" });
    await expect(page.getByText(/verificação em duas etapas ativada/i)).toBeVisible();
  });

  test("disabled 'Confirm' button until 6+ characters entered", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/2fa/enroll", {
      sharedKey: "ABCD",
      authenticatorUri: "otpauth://totp/FSH:alice?secret=AAAAAA",
    });

    await page.goto("/settings/security");
    await page.getByRole("button", { name: /ativar verificação em duas etapas/i }).click();
    const confirm = page.getByRole("button", { name: /confirmar e ativar/i });
    await expect(confirm).toBeDisabled();

    await page.getByLabel(/código de 6 dígitos/i).fill("123");
    await expect(confirm).toBeDisabled();

    await page.getByLabel(/código de 6 dígitos/i).fill("123456");
    await expect(confirm).toBeEnabled();
  });
});

test.describe("settings/security — two-factor disable", () => {
  test.beforeEach(async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", profile2fa(true));
  });

  test("renders the disable form when 2FA is already enabled", async ({ page }) => {
    await page.goto("/settings/security");
    await expect(page.getByLabel("Senha atual")).toBeVisible();
    await expect(page.getByRole("button", { name: /desativar verificação em duas etapas/i })).toBeVisible();
  });

  test("POST /2fa/disable with the current password, then refetches profile", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/2fa/disable", { success: true });

    await page.goto("/settings/security");
    await page.getByLabel("Senha atual").fill("mypassword");

    const reqPromise = page.waitForRequest(
      (r) =>
        r.url().includes("/api/v1/identity/2fa/disable") && r.method() === "POST",
      { timeout: 5_000 },
    );
    await page.getByRole("button", { name: /desativar verificação em duas etapas/i }).click();
    const req = await reqPromise;

    expect(JSON.parse(req.postData() ?? "{}")).toMatchObject({
      currentPassword: "mypassword",
    });
    await expect(page.getByText(/verificação em duas etapas desativada/i)).toBeVisible();
  });
});

// The session list reads dd/mm/aaaa HH:mm in Brasília (manual 02/11/2026: it showed "Nov 02, 03:20 AM").
test.describe("settings/security — active sessions in pt-BR", () => {
  test.use({ timezoneId: "Europe/Berlin" });

  test("last activity and expiry show the Brasília day and time as dd/mm/aaaa HH:mm", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/profile", profile2fa(false));
    await mockJsonResponse(page, "**/api/v1/identity/sessions/me", [
      {
        id: "s-1",
        ipAddress: "10.0.0.1",
        browser: "Chrome",
        browserVersion: "142",
        operatingSystem: "Windows",
        createdAt: "2026-11-02T03:00:00Z",
        lastActivityAt: "2026-11-02T06:20:00Z",
        expiresAt: "2026-11-03T03:05:00Z",
        isActive: true,
        isCurrentSession: true,
      },
    ]);

    await page.goto("/settings/security");

    const linha = page.getByText(/10\.0\.0\.1 · última atividade/);
    await expect(linha).toHaveText("10.0.0.1 · última atividade 02/11/2026 03:20 · expira em 03/11/2026 00:05");
    await expect(page.getByText("Este dispositivo")).toBeVisible();
    await expect(page.getByText(/\b(AM|PM)\b|Nov 02/)).toHaveCount(0);
  });
});
