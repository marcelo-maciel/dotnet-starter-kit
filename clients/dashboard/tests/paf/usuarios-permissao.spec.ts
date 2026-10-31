import { expect, test, type Page } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// D11 (browser test of 31/10/2026): /paf/usuarios opened by URL by a Gerente rendered the whole "Novo usuário"
// form and only then "Acesso não autorizado." from the 403 of the list.
const USUARIOS_VIEW = "Permissions.PAF.Usuarios.View";

async function contarListagens(page: Page) {
  const contador = { chamadas: 0 };
  await page.route("**/api/v1/paf/usuarios", (route) => {
    contador.chamadas += 1;
    return route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: "[]" });
  });
  return contador;
}

test.describe("PAF usuários: permissão antes da tela", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/paf/unidades**", []);
    await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
  });

  test("sem Usuarios.View a tela diz que não tem acesso e não pede a lista", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", ["Permissions.PAF.Guiches.Selecionar"]);
    const listagens = await contarListagens(page);

    await page.goto("/paf/usuarios");

    await expect(page.getByRole("alert").filter({ hasText: "Você não tem permissão para ver os usuários do PAF." })).toBeVisible();
    await expect(page.getByText("Novo usuário")).toHaveCount(0);
    expect(listagens.chamadas).toBe(0);
  });

  test("com Usuarios.View o cadastro abre e pede a lista", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [USUARIOS_VIEW]);
    const listagens = await contarListagens(page);

    await page.goto("/paf/usuarios");

    await expect(page.getByText("Novo usuário").first()).toBeVisible();
    await expect.poll(() => listagens.chamadas).toBeGreaterThan(0);
    await expect(page.getByText("Você não tem permissão para ver os usuários do PAF.")).toHaveCount(0);
  });
});
