// O13 walk against a REAL API (RNF20): the Codefi operator opens the stations page of the demo client, sees the
// combined station "Totem e Painel" as two lines, the painel connects (real SignalR over WebSockets with its station
// credential) and turns "Conectada" while the totem keeps its warning, then the painel drops and its line shows the
// drop and the warning. The client's own admin is refused (403). Only /config.json is intercepted.
//
// Run (API up on a migrated database with `seed-demo`, Valkey on, admin under `vite preview`):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:4188 PAF_SENHA_DEMO=<seed password> \
//     node tests/e2e/paf-operador-estacoes.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { chromium } from "@playwright/test";
import { HttpTransportType, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const SENHA = process.env.PAF_SENHA_DEMO;
if (!API || !WEB || !SENHA) {
  console.error("PAF_API, PAF_WEB and PAF_SENHA_DEMO are required");
  process.exit(2);
}
const ROOT_EMAIL = "admin@root.com";
const TENANT = "acme";
const ADMIN_CLIENTE = "admin@acme.com";
const TIMEOUT = 15_000;
const SCREENSHOT = process.env.PAF_SCREENSHOT;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

async function api(metodo, rota, tenant, token, corpo) {
  const headers = { tenant, "Content-Type": "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  const r = await fetch(`${API}${rota}`, {
    method: metodo,
    headers,
    body: corpo === undefined ? undefined : JSON.stringify(corpo),
    signal: AbortSignal.timeout(TIMEOUT),
  });
  const texto = await r.text();
  return { status: r.status, corpo: texto ? JSON.parse(texto) : null };
}

async function token(tenant, email) {
  const r = await api("POST", "/api/v1/identity/token/issue", tenant, null, { email, password: SENHA });
  if (r.status !== 200) throw new Error(`login ${email}: ${r.status}`);
  return r.corpo.accessToken ?? r.corpo.token;
}

// The combined station's two credentials from one link (a second link would replace the device).
async function credenciaisDaCombinada() {
  const admin = await token(TENANT, ADMIN_CLIENTE);
  const estacoes = await api("GET", "/api/v1/paf/estacoes", TENANT, admin);
  const combinada = estacoes.corpo.find((e) => e.tipo === "EmissaoPainel");
  const link = await api("POST", `/api/v1/paf/estacoes/${combinada.id}/vinculo`, TENANT, admin, {});
  const resgate = await api("POST", "/api/v1/paf/estacoes/vinculo/resgatar", TENANT, null, { token: link.corpo.token });
  if (resgate.status !== 200) throw new Error(`resgate: ${resgate.status}`);
  return { estacao: combinada, porPapel: Object.fromEntries(resgate.corpo.credenciais.map((c) => [c.papel, c.accessToken])) };
}

function conectarEstacao(accessToken) {
  // Same setup as the front clients: WebSockets only, no negotiation, tenant and token in the query.
  const url = `${API}/api/v1/paf/hub?tenant=${encodeURIComponent(TENANT)}&access_token=${encodeURIComponent(accessToken)}`;
  return new HubConnectionBuilder()
    .withUrl(url, { transport: HttpTransportType.WebSockets, skipNegotiation: true })
    .configureLogging(LogLevel.Warning)
    .build();
}

async function linhaDe(page, papelRotulo) {
  return page.locator("tr", { hasText: "Totem e Painel" }).filter({ hasText: papelRotulo });
}

async function atualizar(page) {
  const resposta = page.waitForResponse((r) => r.url().includes("/api/v1/paf/operador/estacoes"), { timeout: TIMEOUT });
  await page.getByRole("button", { name: "Atualizar" }).click();
  return resposta;
}

const browser = await chromium.launch({ channel: "chrome" });
let painel;
try {
  const { porPapel } = await credenciaisDaCombinada();
  check("credenciais de emissão e painel resgatadas", Boolean(porPapel.Emissao && porPapel.Painel));

  const contexto = await browser.newContext();
  await contexto.route("**/config.json", (route) =>
    route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({
        apiBase: API,
        defaultTenant: "root",
        dashboardUrl: "http://localhost:5174",
        inactivityIdleMs: 600000,
        inactivityWarningMs: 60000,
      }),
    }),
  );
  const page = await contexto.newPage();
  page.setDefaultTimeout(TIMEOUT);

  await page.goto(`${WEB}/login`);
  await page.locator("#tenant").fill("root");
  await page.locator("#email").fill(ROOT_EMAIL);
  await page.locator("#password").fill(SENHA);
  const login = page.waitForResponse((r) => r.url().endsWith("/token/issue"));
  await page.getByRole("button", { name: /sign in/i }).click();
  check("operador entra", (await login).status() === 200);
  await page.waitForURL((u) => !u.pathname.startsWith("/login"));

  // Menu item, then the client chosen on the page.
  const item = page.getByRole("link", { name: "Estações" });
  if (!(await item.isVisible())) await page.getByRole("button", { name: /^PAF$/ }).click();
  await item.click();
  await page.waitForURL("**/paf/estacoes");
  const chamadas = [];
  page.on("request", (r) => {
    if (r.url().includes("/api/v1/paf/operador/estacoes")) chamadas.push(r.headers()["tenant"]);
  });
  await page.getByRole("button", { name: /escolha o cliente/i }).click();
  await page.getByRole("menuitem", { name: new RegExp(`\\(${TENANT}\\)`) }).click();
  await page.waitForURL(`**/paf/estacoes?tenant=${TENANT}`);
  // A table row, not getByText: the page description also says "totem e painel" (case-insensitive match).
  await page.locator("tr", { hasText: "Totem e Painel" }).first().waitFor();

  const totemLinha = await linhaDe(page, "Totem (emissão)");
  const painelLinha = await linhaDe(page, "Painel chamador");
  check("estação combinada em duas linhas", (await totemLinha.count()) === 1 && (await painelLinha.count()) === 1);
  check("guichê fora da lista", (await page.locator("tr", { hasText: "Guichê 1" }).count()) === 0);
  check("chamada leva o tenant do cliente", chamadas.length > 0 && chamadas.every((t) => t === TENANT), chamadas.join(","));
  check("painel desconectado avisa (fila sem janela = em operação)",
    await painelLinha.getByText("Aviso: sem conexão em horário de operação").isVisible());

  // The painel connects: its line turns connected, the totem line keeps its warning.
  painel = conectarEstacao(porPapel.Painel);
  await painel.start();
  check("painel conectado por WebSocket", painel.state === "Connected");
  check("leitura com o painel conectado (200)", (await atualizar(page)).status() === 200);
  await painelLinha.getByText("Conectada", { exact: true }).waitFor();
  check("linha do painel conectada, sem aviso",
    (await painelLinha.getByText("Aviso: sem conexão em horário de operação").count()) === 0);
  check("linha do totem segue sem conexão e com aviso",
    await totemLinha.getByText("Aviso: sem conexão em horário de operação").isVisible());
  check("estado pelo registro de conexões (Valkey)",
    !(await page.getByText("Registro de conexões indisponível").isVisible()));

  // The painel drops: the drop instant appears, the warning returns.
  await painel.stop();
  painel = undefined;
  await new Promise((r) => setTimeout(r, 1500));
  check("leitura depois da queda (200)", (await atualizar(page)).status() === 200);
  await painelLinha.getByText("Sem conexão", { exact: true }).waitFor();
  const celulas = await painelLinha.locator("td").allInnerTexts();
  check("última queda preenchida no horário de Brasília", celulas[4] !== "nunca" && /\d{2}\/\d{2}\/\d{4}/.test(celulas[4]), celulas.join(" | "));
  check("aviso volta na linha do painel",
    await painelLinha.getByText("Aviso: sem conexão em horário de operação").isVisible());
  if (SCREENSHOT) await page.screenshot({ path: SCREENSHOT, fullPage: true });

  // The client's own admin never reaches the operator route.
  const recusa = await api("GET", "/api/v1/paf/operador/estacoes", TENANT, await token(TENANT, ADMIN_CLIENTE));
  check("admin do cliente recebe 403", recusa.status === 403, recusa.status);
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.message : String(erro));
} finally {
  if (painel) await painel.stop();
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FAIL (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
