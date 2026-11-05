import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

// Notificações is not part of the PAF product (02/11/2026): the page was a roadmap stub. The route redirects home like
// the other kit screens left out; the bell was hidden from the topbar (no backend code creates notifications since the Chat module was removed).
const GUICHE = "Permissions.PAF.Guiches.Selecionar";

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", [GUICHE]);
  await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
});

test.describe("settings/notifications — fora do produto", () => {
  test("/settings/notifications volta para o início do PAF", async ({ page }) => {
    await page.goto("/settings/notifications");
    await expect(page).toHaveURL(/\/paf\/guiche$/);
  });

  test("Minha conta e a paleta não levam às preferências de notificação", async ({ page }) => {
    await page.goto("/settings/profile");
    await expect(page.getByRole("link", { name: /Segurança/ }).first()).toBeVisible();
    await expect(page.locator('a[href="/settings/notifications"]')).toHaveCount(0);

    await page.getByRole("banner").getByRole("button", { name: /buscar/i }).click();
    const paleta = page.getByRole("dialog");
    await expect(paleta.getByRole("option", { name: /^Segurança/ })).toBeVisible();
    await expect(paleta.getByRole("option", { name: /^Notificações/ })).toHaveCount(0);
    await page.keyboard.press("Escape");

  });

  test("o topbar não tem sino e mostra o estado da conexão em tempo real", async ({ page }) => {
    await page.goto("/settings/profile");
    const topbar = page.getByRole("banner");
    await expect(topbar.getByRole("button", { name: /^Notificações/ })).toHaveCount(0);
    await expect(topbar.getByRole("status")).toBeVisible();
  });
});
