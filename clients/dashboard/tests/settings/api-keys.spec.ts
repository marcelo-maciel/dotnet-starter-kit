import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// Chaves de API is not part of the PAF product (02/11/2026): the page was a "coming soon" stub pointing at the kit's
// roadmap. The route redirects home like the other kit screens left out, and no menu, tab or palette entry leads there.
const GUICHE = "Permissions.PAF.Guiches.Selecionar";

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", [GUICHE]);
  await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
});

test.describe("settings/api-keys — fora do produto", () => {
  test("/settings/api-keys volta para o início do PAF", async ({ page }) => {
    await page.goto("/settings/api-keys");
    await expect(page).toHaveURL(/\/paf\/guiche$/);
  });

  test("Minha conta, o menu do perfil e a paleta não oferecem Chaves de API", async ({ page }) => {
    await page.goto("/settings/profile");
    await expect(page.getByRole("link", { name: /Perfil/ }).first()).toBeVisible();
    await expect(page.locator('a[href="/settings/api-keys"]')).toHaveCount(0);

    await page.getByRole("button", { name: "Abrir menu do perfil" }).click();
    await expect(page.getByRole("menuitem", { name: "Perfil" })).toBeVisible();
    await expect(page.getByRole("menuitem", { name: "Chaves de API" })).toHaveCount(0);
    await page.keyboard.press("Escape");

    await page.getByRole("banner").getByRole("button", { name: /buscar/i }).click();
    const paleta = page.getByRole("dialog");
    await expect(paleta.getByRole("option", { name: /^Perfil/ })).toBeVisible();
    await expect(paleta.getByRole("option", { name: /^Chaves de API/ })).toHaveCount(0);
  });
});
