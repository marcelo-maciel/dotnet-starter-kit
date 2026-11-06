import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// A PAF screen opened by URL without its permission used to mount, call the API, take a 403 and leave an error in
// the console (homologação: atendente on /paf/fichas-do-dia). The PAF layout now shows the notice and mounts nothing.
const TELAS_DO_MENU = [
  ["guiche", "Permissions.PAF.Guiches.Selecionar"],
  ["fichas-do-dia", "Permissions.PAF.FichasDoDia.View"],
  ["unidades", "Permissions.PAF.Unidades.View"],
  ["filas", "Permissions.PAF.Filas.View"],
  ["estacoes", "Permissions.PAF.Estacoes.View"],
  ["fichas-em-atendimento", "Permissions.PAF.FichasEmAtendimento.View"],
  ["consultar-ficha", "Permissions.PAF.FichasDoDia.View"],
  ["ficha-de-espera", "Permissions.PAF.FichasDoDia.View"],
  ["parametros", "Permissions.PAF.Parametros.View"],
  ["usuarios", "Permissions.PAF.Usuarios.View"],
  ["prioridades", "Permissions.PAF.Prioridades.View"],
  ["estatistica", "Permissions.PAF.Estatisticas.View"],
  ["midias", "Permissions.PAF.Midias.View"],
  ["programacoes", "Permissions.PAF.Programacoes.View"],
] as const;

// Holds a permission that opens none of the screens above, so the user is signed in but allowed nowhere.
const OUTRA_PERMISSAO = "Permissions.Users.Update";

test.describe("PAF: tela aberta pela URL sem a permissão", () => {
  for (const [rota] of TELAS_DO_MENU) {
    test(`/paf/${rota} mostra o aviso, sem 403 da API nem erro no console`, async ({ page }) => {
      const respostas403: string[] = [];
      const errosDeConsole: string[] = [];
      const chamadasPaf: string[] = [];
      page.on("console", (m) => {
        // ERR_FAILED is the shell mocks aborting the SSE stream on purpose; a 403 logs "status of 403" instead.
        if (m.type() === "error" && !m.text().includes("net::ERR_FAILED")) errosDeConsole.push(m.text());
      });
      page.on("pageerror", (e) => errosDeConsole.push(e.message));
      page.on("response", (r) => {
        if (r.status() === 403) respostas403.push(r.url());
      });

      await seedAuthedSession(page, TEST_USER);
      await installShellMocks(page);
      await mockJsonResponse(page, "**/api/v1/identity/permissions", [OUTRA_PERMISSAO]);
      // The real API answers 403 to every PAF call this user makes.
      await page.route("**/api/v1/paf/**", (route) => {
        chamadasPaf.push(route.request().url());
        return route.fulfill({ status: 403, headers: { "Content-Type": "application/json" }, body: "{}" });
      });

      // The PafHub opens on sign-in, outside the screens. Accept its SignalR handshake so a missing backend does not
      // log connection errors that have nothing to do with the screen under test.
      await page.routeWebSocket("**/api/v1/paf/hub**", (ws) => {
        let handshaken = false;
        ws.onMessage(() => {
          if (handshaken) return;
          handshaken = true;
          ws.send("{}\u001e");
        });
      });

      await page.goto(`/paf/${rota}`);

      await expect(page.locator("[data-sem-acesso]")).toBeVisible();
      await expect(page.locator("[data-sem-acesso]")).toContainText("Você não tem permissão para ver esta tela do PAF.");
      await page.waitForLoadState("networkidle");
      expect(chamadasPaf, "chamadas ao PAF").toEqual([]);
      expect(respostas403, "respostas 403").toEqual([]);
      expect(errosDeConsole, "erros de console").toEqual([]);
    });
  }

  test("com a permissão a tela abre, sem aviso", async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/identity/permissions", ["Permissions.PAF.Unidades.View"]);
    await mockJsonResponse(page, "**/api/v1/paf/**", []);

    await page.goto("/paf/unidades");

    await expect(page.getByRole("heading", { name: /unidades/i }).first()).toBeVisible();
    await expect(page.locator("[data-sem-acesso]")).toHaveCount(0);
  });
});
