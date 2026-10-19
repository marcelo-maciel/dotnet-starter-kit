// F18 walk against a REAL API: the guichê screen pre-marks the last guichê chosen on this machine (RF04),
// never calls SetGuiche by itself, and does not carry the suggestion into another unidade.
//
// Run (API and Vite dev server already up, fresh `seed-demo` database):
//   PAF_API=http://localhost:5391 PAF_WEB=http://localhost:5184 node tests/e2e/paf-ultimo-guiche.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
if (!API || !WEB) {
  console.error("PAF_API and PAF_WEB are required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const SENHA_DEMO = "Password123!";
const TIMEOUT = 15_000;
const CHAVE = "fsh.dashboard.paf.guiche.ultimo";
const UNIDADE_DEMO = "Loja Demo";
const UNIDADE_NOVA = `Loja Walk F18 ${Date.now()}`;
const GUICHE_NOVO = `Guichê F18 ${Date.now()}`;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
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
  return r.status === 204 ? null : r.json();
}

async function entrar(page) {
  await page.goto(`${WEB}/login`);
  await page.locator("#tenant").fill(TENANT);
  await page.locator("#email").fill(EMAIL);
  await page.locator("#password").fill(SENHA_DEMO);
  await page.locator('button[type="submit"]').click();
  await page.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  await page.goto(`${WEB}/paf/guiche`);
}

async function escolherUnidade(page, nome) {
  const trocar = page.getByRole("button", { name: "Trocar" });
  const unidade = page.getByLabel("Unidade");
  await Promise.race([trocar.waitFor({ timeout: TIMEOUT }), unidade.waitFor({ timeout: TIMEOUT })]);
  if (await trocar.isVisible()) await trocar.click();
  await unidade.selectOption({ label: nome });
  await page.getByRole("button", { name: "Usar esta unidade" }).click();
  await page.getByLabel("Guichê").waitFor({ timeout: TIMEOUT });
}

// After a unidade switch the screen briefly keeps the previous post while it refetches, with the button busy;
// the checks judge the settled screen: the expected value within the timeout, or the last one seen.
async function guicheMarcado(page, esperado) {
  const select = page.getByLabel("Guichê");
  const limite = Date.now() + TIMEOUT;
  let valor = await select.inputValue();
  while (valor !== esperado && Date.now() < limite) {
    await page.waitForTimeout(200);
    valor = await select.inputValue();
  }
  // "" is also what the select shows while loading: re-read after a pause so a late suggestion is caught.
  await page.waitForTimeout(1500);
  return select.inputValue();
}

async function botaoPronto(page) {
  const botao = page.getByRole("button", { name: "Usar este guichê" });
  const limite = Date.now() + TIMEOUT;
  while (!(await botao.isEnabled()) && Date.now() < limite) await page.waitForTimeout(200);
  return botao.isEnabled();
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── Setup through the admin API: a second guichê in Loja Demo and a second unidade ──
  const admin = await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const estacoes = await api("GET", "/api/v1/paf/estacoes", admin.accessToken);
  const g1 = estacoes.find((e) => e.tipo === "Guiche");
  const g2Id = await api("POST", "/api/v1/paf/estacoes", admin.accessToken, {
    nome: GUICHE_NOVO,
    tipo: "Guiche",
    filas: g1.filas,
    prioridades: g1.prioridades,
  });
  const unidadeNovaId = await api("POST", "/api/v1/paf/unidades", admin.accessToken, { nome: UNIDADE_NOVA, documentoFiscal: null });
  // The selection lists only the user's scope (RF08): put the new unidade in it unless the scope is the company.
  const claims = JSON.parse(Buffer.from(admin.accessToken.split(".")[1], "base64url").toString("utf8"));
  const usuarioId = claims.sub ?? claims["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"];
  const escopo = await api("GET", `/api/v1/paf/usuarios/${usuarioId}/unidades`, admin.accessToken);
  if (!escopo.escopoEmpresa) {
    await api("PUT", `/api/v1/paf/usuarios/${usuarioId}/unidades`, admin.accessToken, {
      usuarioId,
      unidades: [...escopo.unidades.map((u) => u.unidadeId), unidadeNovaId],
      escopoEmpresa: false,
    });
  }

  // One browser context = one machine: its localStorage outlives the sign-out.
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  const setGuiche = [];
  page.on("request", (r) => {
    if (r.method() === "POST" && new URL(r.url()).pathname.endsWith("/paf/sessao/guiche")) setGuiche.push(r.url());
  });

  await entrar(page);
  await escolherUnidade(page, UNIDADE_DEMO);
  check("primeira vez na máquina: nada pré-marcado", (await guicheMarcado(page, "")) === "");
  check("primeira vez na máquina: nada guardado", (await page.evaluate((k) => localStorage.getItem(k), CHAVE)) === null);

  await page.getByLabel("Guichê").selectOption(g2Id);
  await page.getByRole("button", { name: "Usar este guichê" }).click();
  await page.getByRole("button", { name: "Chamar próxima" }).waitFor({ timeout: TIMEOUT });
  const guardado = await page.evaluate((k) => localStorage.getItem(k), CHAVE);
  check("escolha aceita guarda o guichê com tenant e unidade", guardado !== null && guardado.includes(g2Id) && guardado.includes(TENANT), guardado);
  const setGuicheAntesDeSair = setGuiche.length;
  check("a escolha explícita chamou SetGuiche uma vez", setGuicheAntesDeSair === 1, String(setGuicheAntesDeSair));

  // ── Sign out through the kit menu and come back on the same machine ──
  await page.getByRole("button", { name: "Open profile menu" }).click();
  await page.getByRole("menuitem", { name: "Sign out" }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Sign out" }).click();
  await page.waitForURL((u) => u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  check("sair mantém a sugestão da máquina", (await page.evaluate((k) => localStorage.getItem(k), CHAVE)) === guardado);

  await entrar(page);
  await escolherUnidade(page, UNIDADE_DEMO);
  check("de volta na mesma máquina: o guichê anterior vem pré-marcado", (await guicheMarcado(page, g2Id)) === g2Id, await guicheMarcado(page, g2Id));
  check("o botão fica pronto para a escolha do usuário", await botaoPronto(page));
  await page.waitForTimeout(2000);
  check("pré-marcar não dispara SetGuiche", setGuiche.length === setGuicheAntesDeSair, String(setGuiche.length));

  // ── Another unidade does not inherit the suggestion ──
  await escolherUnidade(page, UNIDADE_NOVA);
  check("outra unidade: nada pré-marcado", (await guicheMarcado(page, "")) === "");
  check("outra unidade: botão desabilitado", await page.getByRole("button", { name: "Usar este guichê" }).isDisabled());

  // ── NEGATIVE: a corrupted value on the machine is no suggestion, not a broken screen ──
  await page.evaluate((k) => localStorage.setItem(k, "{"), CHAVE);
  await escolherUnidade(page, UNIDADE_DEMO);
  check("valor corrompido: tela abre sem sugestão", (await guicheMarcado(page, "")) === "");
  check("nenhum SetGuiche além da escolha explícita", setGuiche.length === setGuicheAntesDeSair, String(setGuiche.length));
} catch (e) {
  console.log(`FAIL walk abortado :: ${e instanceof Error ? e.stack : String(e)}`);
  falhas += 1;
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
