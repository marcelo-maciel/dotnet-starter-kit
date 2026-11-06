import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// The kit's Roles screen is not part of the PAF product (02/11/2026): the client's roles are the PAF ones, granted on
// /paf/usuarios. Its URLs redirect home even for someone still holding the old permissions, and the page never mounts,
// so it never calls the kit's role or permission-catalog endpoints.
const GUICHE = "Permissions.PAF.Guiches.Selecionar";

test.describe("identity/roles — fora do produto", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [
      GUICHE,
      "Permissions.Roles.View",
      "Permissions.Roles.Create",
      "Permissions.Roles.Update",
    ]);
  });

  for (const rota of ["/identity/roles", "/identity/roles?action=create", "/identity/roles/11111111-1111-1111-1111-111111111111"]) {
    test(`${rota} volta para o início do PAF sem chamar a API de papéis do kit`, async ({ page }) => {
      const chamadas: string[] = [];
      page.on("request", (req) => {
        if (/\/api\/v1\/identity\/(roles\b|permissions\/catalog|[0-9a-f-]{36}\/permissions)/.test(req.url())) chamadas.push(req.url());
      });
      await page.goto(rota);
      await expect(page).toHaveURL(/\/paf\/guiche$/);
      expect(chamadas).toEqual([]);
    });
  }
});
