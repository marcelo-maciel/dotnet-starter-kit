import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// Product decision of 31/10/2026: the dashboard opens on the PAF (the guichê, else the first PAF screen the user
// opens) and the kit's Chat, Files, Catalog, Helpdesk and Billing leave the menu, their URLs redirecting home.
const GUICHE = "Permissions.PAF.Guiches.Selecionar";
const FICHAS_DO_DIA = "Permissions.PAF.FichasDoDia.View";

test.describe("PAF: tela inicial e menu do cliente", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
  });

  test("quem seleciona guichê abre no guichê", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [GUICHE, FICHAS_DO_DIA]);
    await page.goto("/");
    await expect(page).toHaveURL(/\/paf\/guiche$/);
  });

  test("sem guichê, abre na primeira tela do PAF que a pessoa vê", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [FICHAS_DO_DIA]);
    await page.goto("/");
    await expect(page).toHaveURL(/\/paf\/fichas-do-dia$/);
  });

  test("sem nenhuma tela do PAF, abre na visão geral do kit", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", ["Permissions.Users.Update"]);
    await page.goto("/");
    await expect(page).toHaveURL(/\/overview$/);
  });

  for (const antiga of ["/chat", "/files", "/catalog/products", "/tickets", "/subscription", "/wallet", "/invoices/inv-1", "/system/trash"]) {
    test(`${antiga} volta para o início do PAF`, async ({ page }) => {
      await mockJsonResponse(page, "**/api/v1/identity/permissions", [GUICHE]);
      await page.goto(antiga);
      await expect(page).toHaveURL(/\/paf\/guiche$/);
    });
  }

  test("o menu e a barra do topo não mostram Chat, Arquivos, Catálogo, Helpdesk, Billing nem Lixeira, mesmo com as permissões", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [
      GUICHE,
      "Permissions.Chat.Channels.View",
      "Permissions.Files.Upload",
      "Permissions.Catalog.Products.View",
      "Permissions.Tickets.View",
      "Permissions.Billing.View",
      "Permissions.Users.Update",
      "Permissions.Catalog.Products.Restore",
    ]);
    await page.goto("/");
    const menu = page.locator("aside");
    await expect(menu.locator('a[href="/paf/guiche"]')).toBeAttached();
    for (const href of ["/chat", "/files", "/catalog/products", "/tickets", "/subscription", "/wallet", "/invoices", "/system/trash"]) {
      await expect(menu.locator(`a[href="${href}"]`)).toHaveCount(0);
    }
    // The topbar chat badge (a button that navigates to /chat) is gone too.
    await expect(page.getByRole("banner").getByRole("button", { name: /^Chat/ })).toHaveCount(0);
    await expect(menu.locator('a[href="/overview"]')).toBeAttached();
    await expect(menu.locator('a[href="/activity"]')).toBeAttached();
  });
});
