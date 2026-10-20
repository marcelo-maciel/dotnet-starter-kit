// C13 walk against a REAL API and PostgreSQL (no route mocks): PAF menu by permission, current-unit selector,
// unit switch noticed by the other tabs, and a kit screen keeping the licence alive through the PafHub connection.
//
// Run against the production build (`npm run build`, dist/config.json apiBase = the API, `vite preview`): the dev
// server mounts twice under StrictMode, two SSE streams per tab, and three tabs exhaust the 6 HTTP/1.1 connections
// per host, which starves the switch request. API up, fresh `seed-demo` database, its PostgreSQL in a local container:
//   PAF_API=http://localhost:5412 PAF_WEB=http://localhost:5413 PAF_PG=c13-pg node tests/e2e/paf-navegacao.walk.mjs
// PAF_ESPERA_MS (default 180000) is how long the kit screen stays idle. Exit code 0 only when every check passed.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const PG = process.env.PAF_PG;
if (!API || !WEB || !PG) {
  console.error("PAF_API, PAF_WEB and PAF_PG are required");
  process.exit(2);
}
const ESPERA_MS = Number(process.env.PAF_ESPERA_MS ?? 180_000);
const TENANT = "acme";
const SENHA_DEMO = process.env.PAF_SENHA_DEMO ?? "Password123!";
const TIMEOUT = 15_000;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

function visivel(locator) {
  return locator.waitFor({ timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
}

async function api(method, path, token, body) {
  const headers = { tenant: TENANT, "X-FSH-App": "dashboard" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const r = await fetch(API + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(TIMEOUT),
  });
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${await r.text()}`);
  const texto = await r.text();
  return texto === "" ? null : JSON.parse(texto);
}

// Read-only checks go straight to PostgreSQL: a second API login would revoke the browser's session (sessão única).
function sql(consulta) {
  // User fsh, the owner every walk container is created with (deploy/paf/walk-local.sh, walk-ci.sh).
  return execFileSync("docker", ["exec", PG, "psql", "-U", "fsh", "-d", "fsh", "-v", "ON_ERROR_STOP=1", "-At", "-c", consulta], {
    encoding: "utf8",
    timeout: TIMEOUT,
  }).trim();
}

function usuarioId(email) {
  const id = sql(`SELECT "Id" FROM identity."Users" WHERE "TenantId" = '${TENANT}' AND "Email" = '${email}'`);
  if (!id) throw new Error(`usuário ${email} não encontrado`);
  return id;
}

// Open sockets, so the walk knows when the PafHub connection is up.
const SOCKETS = () => {
  window.__sockets = [];
  const SocketNativo = window.WebSocket;
  window.WebSocket = class extends SocketNativo {
    constructor(...args) {
      super(...args);
      window.__sockets.push(this);
    }
  };
};

async function hubAberto(pagina) {
  return pagina
    .waitForFunction(
      () => (window.__sockets ?? []).some((s) => s.url.includes("/api/v1/paf/hub") && s.readyState === 1),
      null,
      { timeout: TIMEOUT },
    )
    .then(
      () => true,
      () => false,
    );
}

async function entrar(contexto, email) {
  const pagina = await contexto.newPage();
  await pagina.goto(`${WEB}/login`);
  await pagina.locator("#tenant").fill(TENANT);
  await pagina.locator("#email").fill(email);
  await pagina.locator("#password").fill(SENHA_DEMO);
  await pagina.locator('button[type="submit"]').click();
  await pagina.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: TIMEOUT });
  return pagina;
}

async function linksPaf(pagina) {
  // The aside renders before the permissions arrive: wait for the PAF section itself, then let the list settle.
  await pagina.locator('aside a[href^="/paf"]').first().waitFor({ timeout: TIMEOUT });
  await pagina.waitForTimeout(1_000);
  return pagina.locator('aside a[href^="/paf"]').evaluateAll((as) => [...new Set(as.map((a) => a.getAttribute("href")))]);
}

const browser = await chromium.launch({ channel: "chrome" });
try {
  // ── Setup through the API: a second unidade in the admin's scope; alice and manager on the demo unidade only.
  // Only the company admin creates a unidade (RF23): the demo admin is made one first, as paf-logotipo.walk.mjs does.
  const inicial = await api("POST", "/api/v1/identity/token/issue", null, { email: "admin@acme.com", password: SENHA_DEMO });
  const unidades = await api("GET", "/api/v1/paf/unidades", inicial.accessToken);
  const demo = unidades.find((u) => u.nome === "Loja Demo");
  await api("PUT", `/api/v1/paf/usuarios/${usuarioId("admin@acme.com")}/unidades`, inicial.accessToken, {
    usuarioId: "00000000-0000-0000-0000-000000000000",
    unidades: [demo.id],
    escopoEmpresa: true,
  });
  const admin = await api("POST", "/api/v1/identity/token/issue", null, { email: "admin@acme.com", password: SENHA_DEMO });
  const nomeCentro = `Loja Centro ${Date.now()}`;
  const centroId = await api("POST", "/api/v1/paf/unidades", admin.accessToken, { nome: nomeCentro, documentoFiscal: null });
  // The switch migrates the licence: the new unidade gets a copy of the demo contract. Ids are Guids the API returned.
  sql(`INSERT INTO paf."ContratosUnidade" ("Id", "UnidadeId", "InicioVigencia", "FimVigencia", "Plano", "LimiteSessoes", "Politica", "TenantId")
       SELECT gen_random_uuid(), '${centroId}', "InicioVigencia", "FimVigencia", "Plano", "LimiteSessoes", "Politica", "TenantId"
       FROM paf."ContratosUnidade" WHERE "UnidadeId" = '${demo.id}' LIMIT 1`);
  const escopo = (unidadesDoUsuario) => ({ usuarioId: "00000000-0000-0000-0000-000000000000", unidades: unidadesDoUsuario, escopoEmpresa: false });
  const adminId = usuarioId("admin@acme.com");
  const aliceId = usuarioId("alice@acme.com");
  const managerId = usuarioId("manager@acme.com");
  await api("PUT", `/api/v1/paf/usuarios/${adminId}/unidades`, admin.accessToken, escopo([demo.id, centroId]));
  await api("PUT", `/api/v1/paf/usuarios/${aliceId}/unidades`, admin.accessToken, escopo([demo.id]));
  await api("PUT", `/api/v1/paf/usuarios/${managerId}/unidades`, admin.accessToken, escopo([demo.id]));

  // ── Menu by permission and selector only for whoever has more than one unidade.
  const ctxAdmin = await browser.newContext();
  await ctxAdmin.addInitScript(SOCKETS);
  const pAdmin = await entrar(ctxAdmin, "admin@acme.com");
  const menuAdmin = await linksPaf(pAdmin);
  // Every screen of the PAF menu definition (menu-paf.ts): the demo admin holds every client-side PAF permission.
  const telasDoMenu = [
    ...new Set(readFileSync(new URL("../../src/features/paf/navegacao/menu-paf.ts", import.meta.url), "utf8").match(/"\/paf\/[a-z-]+"/g).map((s) => JSON.parse(s))),
  ].sort();
  check(
    "menu: admin vê a seção do PAF com todas as telas que existem",
    JSON.stringify([...menuAdmin].sort()) === JSON.stringify(telasDoMenu),
    `${JSON.stringify(menuAdmin)} vs ${telasDoMenu.length} no menu-paf.ts`,
  );
  check("menu: seção com o título Atendimento", await visivel(pAdmin.locator("aside").getByText("Atendimento").first()));
  check("tempo real: tela do kit conecta ao PafHub", await hubAberto(pAdmin));

  await pAdmin.goto(`${WEB}/paf/guiche`);
  const seletor = pAdmin.locator("[data-paf-seletor-unidade] select");
  check("seletor: admin com duas unidades vê o seletor", await visivel(seletor));
  const opcoes = await seletor.locator("option").allTextContents();
  check("seletor: só as unidades ativas do escopo", opcoes.length === 2 && opcoes.includes("Loja Demo") && opcoes.includes(nomeCentro), JSON.stringify(opcoes));

  const ctxAlice = await browser.newContext();
  await ctxAlice.addInitScript(SOCKETS);
  const pAlice = await entrar(ctxAlice, "alice@acme.com");
  const menuAlice = await linksPaf(pAlice);
  check("menu: atendente vê só o guichê", JSON.stringify(menuAlice) === '["/paf/guiche"]', JSON.stringify(menuAlice));
  check("menu: atendente não vê a administração do kit", (await pAlice.locator('aside a[href="/identity/users"]').count()) === 0);
  // The selector decides after both reads come back: wait for them before asserting its absence.
  const leituras = Promise.all(
    ["/api/v1/paf/guiche/unidades", "/api/v1/paf/guiche/posto"].map((rota) =>
      pAlice.waitForResponse((r) => r.url().includes(rota), { timeout: TIMEOUT }),
    ),
  );
  await pAlice.goto(`${WEB}/paf/guiche`);
  const respostas = await leituras;
  const unidadesAlice = await respostas[0].json();
  check("seletor: a leitura das unidades da atendente devolve uma", unidadesAlice.length === 1, JSON.stringify(unidadesAlice));
  await visivel(pAlice.getByRole("heading", { name: "Guichê" }));
  check("seletor: usuário com uma unidade não vê o seletor", (await pAlice.locator("[data-paf-seletor-unidade]").count()) === 0);
  await ctxAlice.close();

  // ── Switch in one tab: the other PAF tab reloads with a notice, a kit tab gets a toast.
  const pOutraPaf = await ctxAdmin.newPage();
  await pOutraPaf.goto(`${WEB}/paf/guiche`);
  await visivel(pOutraPaf.locator("[data-paf-seletor-unidade] select"));
  const pOutraKit = await ctxAdmin.newPage();
  await pOutraKit.goto(`${WEB}/settings/profile`);
  await hubAberto(pOutraKit);
  const recarregou = pOutraPaf.waitForEvent("load", { timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
  await pAdmin.bringToFront();
  const respostaTroca = pAdmin.waitForResponse((r) => r.url().includes("/api/v1/paf/sessao/unidade-corrente"), { timeout: TIMEOUT });
  await seletor.selectOption({ label: nomeCentro });
  const troca = await respostaTroca;
  check("troca: o servidor aceita a troca", troca.ok(), String(troca.status()));
  check("troca: a outra aba do PAF recarrega", await recarregou);
  check(
    "troca: a outra aba do PAF avisa a troca depois de recarregar",
    await visivel(pOutraPaf.locator("[data-paf-aviso-troca]", { hasText: nomeCentro })),
  );
  const selecionadaOutra = await pOutraPaf.locator("[data-paf-seletor-unidade] select").evaluate((s) => s.selectedOptions[0]?.textContent);
  check("troca: a outra aba mostra a unidade nova", selecionadaOutra === nomeCentro, selecionadaOutra);
  check("troca: a aba do kit avisa sem recarregar", await visivel(pOutraKit.getByText(`trocada para ${nomeCentro}`)));
  check("troca: a aba que trocou não mostra o aviso de outra aba", (await pAdmin.locator("[data-paf-aviso-troca]").count()) === 0);
  const selecionadaAqui = await seletor.evaluate((s) => s.selectedOptions[0]?.textContent);
  check("troca: a aba que trocou mostra a unidade nova", selecionadaAqui === nomeCentro, selecionadaAqui);
  await ctxAdmin.close();

  // ── A kit screen open and idle keeps the licence: the PafHub connection renews the session's mark.
  const ctxManager = await browser.newContext();
  await ctxManager.addInitScript(SOCKETS);
  const pManager = await entrar(ctxManager, "manager@acme.com");
  await pManager.goto(`${WEB}/settings/profile`);
  const conectado = await hubAberto(pManager);
  check("licença: tela do kit conectada ao PafHub", conectado);
  // Page-load reads settle before the idle window starts.
  await pManager.waitForTimeout(3_000);
  const marca = () =>
    Number(sql(`SELECT coalesce(extract(epoch FROM max("UltimaAtividadeEm")), 0) FROM paf."AtividadesSessao" WHERE "TenantId" = '${TENANT}' AND "UsuarioId" = '${managerId}'`));
  const antes = marca();
  const requisicoes = [];
  pManager.on("request", (r) => {
    if (r.url().includes("/api/") && !r.url().includes("/api/v1/paf/hub")) requisicoes.push(r.url());
  });
  console.log(`.. tela do kit parada por ${ESPERA_MS} ms (marca inicial ${antes})`);
  await pManager.waitForTimeout(ESPERA_MS);
  const depois = marca();
  console.log(`.. requisições HTTP da tela durante a espera: ${requisicoes.length} ${JSON.stringify(requisicoes.slice(0, 5))}`);
  check("licença: marca da sessão renovada com a tela do kit parada", antes > 0 && depois > antes, `${antes} -> ${depois}`);
  const viva = sql(
    `SELECT count(*) FROM paf."AtividadesSessao" WHERE "TenantId" = '${TENANT}' AND "UsuarioId" = '${managerId}' AND "UltimaAtividadeEm" > now() - interval '10 minutes'`,
  );
  check("licença: sessão ainda ocupa a licença", viva === "1", viva);
  await ctxManager.close();
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas} verificação(ões)`);
process.exit(falhas === 0 ? 0 : 1);
