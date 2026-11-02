import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// D8 (browser test of 31/10/2026, 1440px): the colour placeholder "padrão da estação" was cut, and the font-colour
// requirement only appeared after a button colour was filled.
test.describe("PAF filas: aparência do formulário", () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/identity/permissions", ["Permissions.PAF.Filas.View"]);
    await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);
    await mockJsonResponse(page, "**/api/v1/paf/filas", []);
    await mockJsonResponse(page, "**/api/v1/paf/setores", []);
    await mockJsonResponse(page, "**/api/v1/paf/prioridades", [
      { id: "p-normal", unidadeId: "u1", nome: "Normal", valor: 0, ativa: true },
      { id: "p-pref", unidadeId: "u1", nome: "Preferencial", valor: 10, ativa: true },
    ]);
    await page.goto("/paf/filas");
    await page.getByRole("button", { name: "Nova fila" }).click();
    for (const id of ["p-normal", "p-pref"]) await page.getByLabel("Adicionar prioridade").selectOption(id);
  });

  test("a exigência da cor da fonte aparece antes de qualquer cor de botão", async ({ page }) => {
    const corFonte = page.getByLabel("Cor da fonte", { exact: true });
    await expect(corFonte).toHaveValue("");
    await expect(corFonte).toHaveAccessibleDescription("Obrigatória quando algum botão tem cor.");
    await expect(page.getByText("Campo vazio usa o padrão da estação.")).toBeVisible();
  });

  test("o texto de exemplo dos campos de cor cabe no campo", async ({ page }) => {
    const campos = page.locator('input[placeholder^="padrão"]');
    // Normal, Preferencial, fonte, cor da fonte, cor do botão selecionado.
    await expect(campos).toHaveCount(5);
    const cortados = await campos.evaluateAll((inputs) =>
      (inputs as HTMLInputElement[])
        .map((input) => {
          const estilo = getComputedStyle(input);
          const contexto = document.createElement("canvas").getContext("2d")!;
          contexto.font = `${estilo.fontWeight} ${estilo.fontSize} ${estilo.fontFamily}`;
          const largura = contexto.measureText(input.placeholder).width;
          const espaco = input.clientWidth - parseFloat(estilo.paddingLeft) - parseFloat(estilo.paddingRight);
          return { rotulo: input.labels?.[0]?.textContent ?? input.id, largura, espaco };
        })
        .filter((c) => c.largura > c.espaco),
    );
    expect(cortados).toEqual([]);
  });
});
