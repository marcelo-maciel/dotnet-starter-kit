import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// iPhone Safari zooms the page when a field under 16px takes focus (v-midias-mobile of the homologação walk reported
// one at 14px on Arquivos de mídia, Pixel 7). Every field on the screen, the file picker included, reads 16px there.
const PIXEL_7 = { width: 412, height: 915 };
const PERMISSOES = ["Permissions.PAF.Midias.View", "Permissions.PAF.Midias.Create", "Permissions.PAF.Midias.Update"];
const MIDIA = {
  id: "m-1",
  descricao: "Vídeo institucional",
  tipo: "Video",
  duracaoSegundos: 8,
  ativa: true,
  nomeArquivo: "institucional.mp4",
};

test("Arquivos de mídia no Pixel 7: nenhum campo com fonte abaixo de 16px", async ({ page }) => {
  await page.setViewportSize(PIXEL_7);
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", PERMISSOES);
  await mockJsonResponse(page, "**/api/v1/paf/midias", [MIDIA]);
  await mockJsonResponse(page, "**/api/v1/paf/guiche/unidades", []);

  await page.goto("/paf/midias");
  await expect(page.locator("[data-nova-midia]")).toBeVisible();
  await page.locator("[data-midia]").getByRole("button", { name: "Editar" }).first().click();
  await expect(page.getByLabel(/Nova descrição de/)).toBeVisible();

  const campos = await page.locator("main").evaluate((main) =>
    [...main.querySelectorAll("input, select, textarea")]
      .filter((el) => (el as HTMLElement).offsetParent !== null)
      .map((el) => ({
        tipo: `${el.tagName.toLowerCase()}${(el as HTMLInputElement).type ? `[${(el as HTMLInputElement).type}]` : ""}`,
        id: el.id || el.getAttribute("aria-label") || "",
        fonte: parseFloat(getComputedStyle(el).fontSize),
      })),
  );
  expect(campos.length, "campos medidos").toBeGreaterThanOrEqual(4);
  expect(campos.filter((c) => c.fonte < 16), JSON.stringify(campos)).toEqual([]);

  // At 16px the picker must not push the card wider than the phone (why the global 16px rule once skipped files).
  const cartao = page.locator("[data-nova-midia]");
  const estouro = await cartao.evaluate((el) => el.scrollWidth - el.clientWidth);
  expect(estouro, "estouro horizontal do cartão Novo arquivo").toBeLessThanOrEqual(0);
  await cartao.screenshot({ path: "C:/wt/homolog/sino/midias-novo-arquivo-pixel7.png" });
});
