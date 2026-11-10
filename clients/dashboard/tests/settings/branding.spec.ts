import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// Marca is not part of the PAF product (02/11/2026): the editor asked for image URLs hosted through the Files module,
// which the PAF does not ship. The route redirects home like the other kit screens left out, even for a user who
// holds the theme permissions, and the tab is gone from Minha conta.
const GUICHE = "Permissions.PAF.Guiches.Selecionar";
const PERMISSOES_DE_MARCA = ["Permissions.Tenants.ViewTheme", "Permissions.Tenants.UpdateTheme"];

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", [GUICHE, ...PERMISSOES_DE_MARCA]);
  await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
});

test.describe("settings/branding — fora do produto", () => {
  test("/settings/branding volta para o início do PAF sem pedir o tema da empresa", async ({ page }) => {
    const pedidosDeTema: string[] = [];
    page.on("request", (r) => {
      if (r.url().includes("/api/v1/tenants/theme")) pedidosDeTema.push(r.url());
    });
    await page.goto("/settings/branding");
    await expect(page).toHaveURL(/\/paf\/guiche$/);
    expect(pedidosDeTema).toEqual([]);
  });

  test("Minha conta mostra só Perfil, Segurança e Aparência, mesmo com as permissões de tema", async ({ page }) => {
    await page.goto("/settings/profile");
    const abas = page.getByRole("navigation", { name: "Seções de Minha conta" });
    await expect(abas.getByRole("link", { name: /Perfil/ }).first()).toBeVisible();
    const destinos = await abas.locator("a").evaluateAll((links) => [...new Set(links.map((a) => a.getAttribute("href")))]);
    expect(destinos).toEqual(["/settings/profile", "/settings/security", "/settings/appearance"]);
    await expect(page.getByRole("link", { name: /Marca/ })).toHaveCount(0);
  });
});
