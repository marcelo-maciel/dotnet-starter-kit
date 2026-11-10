import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// The kit's Users screen is not part of the PAF product (02/11/2026): the client manages its users on /paf/usuarios,
// which keeps the unidade scope and revokes sessions on deactivation. Its URLs redirect home even for someone still
// holding the old permissions, and the page never mounts, so it never calls the kit's user endpoints.
const GUICHE = "Permissions.PAF.Guiches.Selecionar";

test.describe("identity/users — fora do produto", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [
      GUICHE,
      "Permissions.Users.View",
      "Permissions.Users.Create",
      "Permissions.Users.Update",
      "Permissions.Users.ConfirmEmail",
    ]);
  });

  for (const rota of ["/identity/users", "/identity/users?action=create", "/identity/users/00000000-0000-0000-0000-0000000000b0"]) {
    test(`${rota} volta para o início do PAF sem chamar a API de usuários do kit`, async ({ page }) => {
      const chamadas: string[] = [];
      page.on("request", (req) => {
        if (/\/api\/v1\/identity\/users\b/.test(req.url())) chamadas.push(req.url());
      });
      await page.goto(rota);
      await expect(page).toHaveURL(/\/paf\/guiche$/);
      expect(chamadas).toEqual([]);
    });
  }
});
