import { expect, test, type Page } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// A kit screen the menu hides by permission used to mount when opened by URL, call the API, take a 403 and leave an
// error in the console (homologação walk: atendente on /system/audits and /system/sessions). The route now shows the
// notice and mounts nothing.
const TELAS_ESCONDIDAS = [
  ["/system/audits", "Permissions.AuditTrails.View", "**/api/v1/audits**"],
  ["/system/sessions", "Permissions.Sessions.ViewAll", "**/api/v1/identity/sessions**"],
] as const;

// Holds a permission that opens none of the screens, so the user is signed in but allowed nowhere.
const OUTRA_PERMISSAO = "Permissions.Users.Update";

// The PafHub opens on sign-in, outside the screens. Accept its SignalR handshake so a missing backend does not log
// connection errors that have nothing to do with the screen under test.
async function aceitarHandshakeDoPafHub(page: Page) {
  await page.routeWebSocket("**/api/v1/paf/hub**", (ws) => {
    let handshaken = false;
    ws.onMessage(() => {
      if (handshaken) return;
      handshaken = true;
      ws.send("{}\u001e");
    });
  });
}

function vigiarConsole(page: Page) {
  const erros: string[] = [];
  const respostas403: string[] = [];
  page.on("console", (m) => {
    // ERR_FAILED is the shell mocks aborting the SSE stream on purpose; a 403 logs "status of 403" instead.
    if (m.type() === "error" && !m.text().includes("net::ERR_FAILED")) erros.push(m.text());
  });
  page.on("pageerror", (e) => erros.push(e.message));
  page.on("response", (r) => {
    if (r.status() === 403) respostas403.push(r.url());
  });
  return { erros, respostas403 };
}

test.describe("kit: tela escondida pelo menu, aberta pela URL sem a permissão", () => {
  for (const [rota, , apiGlob] of TELAS_ESCONDIDAS) {
    test(`${rota} mostra o aviso, sem 403 da API nem erro no console`, async ({ page }) => {
      const { erros, respostas403 } = vigiarConsole(page);
      const chamadas: string[] = [];
      await seedAuthedSession(page, TEST_USER);
      await installShellMocks(page);
      await mockJsonResponse(page, "**/api/v1/identity/permissions", [OUTRA_PERMISSAO]);
      await page.route(apiGlob, (route) => {
        chamadas.push(route.request().url());
        return route.fulfill({ status: 403, headers: { "Content-Type": "application/json" }, body: "{}" });
      });
      await aceitarHandshakeDoPafHub(page);

      await page.goto(rota);

      await expect(page.locator("[data-sem-acesso]")).toBeVisible();
      await expect(page.locator("[data-sem-acesso]")).toContainText("Você não tem permissão para ver esta tela.");
      await page.waitForTimeout(1_500);
      expect(chamadas, "chamadas à API da tela").toEqual([]);
      expect(respostas403, "respostas 403").toEqual([]);
      expect(erros, "erros de console").toEqual([]);
    });
  }

  for (const [rota, perm, apiGlob] of TELAS_ESCONDIDAS) {
    test(`${rota} abre com ${perm}, sem aviso`, async ({ page }) => {
      await seedAuthedSession(page, TEST_USER);
      await installShellMocks(page);
      await mockJsonResponse(page, "**/api/v1/identity/permissions", [perm]);
      await mockJsonResponse(page, apiGlob, []);
      await aceitarHandshakeDoPafHub(page);

      await page.goto(rota);

      await expect(page.getByRole("main")).toBeVisible();
      await page.waitForTimeout(500);
      await expect(page.locator("[data-sem-acesso]")).toHaveCount(0);
    });
  }

  for (const rota of ["/system/health", "/activity", "/overview"]) {
    test(`${rota} é de todos: abre sem a permissão e sem aviso`, async ({ page }) => {
      await seedAuthedSession(page, TEST_USER);
      await installShellMocks(page);
      await mockJsonResponse(page, "**/api/v1/identity/permissions", [OUTRA_PERMISSAO]);
      await aceitarHandshakeDoPafHub(page);

      await page.goto(rota);

      await expect(page.getByRole("main")).toBeVisible();
      await page.waitForTimeout(500);
      await expect(page.locator("[data-sem-acesso]")).toHaveCount(0);
    });
  }
});
