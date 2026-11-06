import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// The kit's Groups screen is not part of the PAF product (02/11/2026). Its URLs redirect home even for someone still
// holding the old permissions, and the page never mounts, so it never calls the kit's group endpoints.
const GUICHE = "Permissions.PAF.Guiches.Selecionar";

test.describe("identity/groups — fora do produto", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [
      GUICHE,
      "Permissions.Groups.View",
      "Permissions.Groups.Create",
      "Permissions.Groups.Update",
    ]);
  });

  for (const rota of ["/identity/groups", "/identity/groups?action=create", "/identity/groups/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"]) {
    test(`${rota} volta para o início do PAF sem chamar a API de grupos do kit`, async ({ page }) => {
      const chamadas: string[] = [];
      page.on("request", (req) => {
        if (/\/api\/v1\/identity\/groups\b/.test(req.url())) chamadas.push(req.url());
      });
      await page.goto(rota);
      await expect(page).toHaveURL(/\/paf\/guiche$/);
      expect(chamadas).toEqual([]);
    });
  }
});
