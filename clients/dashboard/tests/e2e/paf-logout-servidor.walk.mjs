// S13 walk against a REAL API: every kit logout path (menu, inactivity guard) calls the PAF server logout
// before clearing the token; impersonation never calls it; a hung or refused server only delays the local
// logout up to the deadline. The only interceptions are the ones marked NEGATIVE (server failure) and the
// /config.json idle override, which stands in for the per-environment template of O15.
//
// Run (API and Vite dev server already up, fresh `seed-demo` database, PostgreSQL in a docker container):
//   PAF_API=http://localhost:5413 PAF_WEB=http://localhost:5513 PAF_PG_CONTAINER=s13walk-pg node tests/e2e/paf-logout-servidor.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { execFileSync } from "node:child_process";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const PG = process.env.PAF_PG_CONTAINER;
if (!API || !WEB || !PG) {
  console.error("PAF_API, PAF_WEB and PAF_PG_CONTAINER are required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const SENHA_DEMO = process.env.PAF_SENHA_DEMO ?? "Password123!";
const TIMEOUT = 15_000;
const LOGOUT_PATH = "/api/v1/paf/sessao/logout";

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

function sql(consulta) {
  return execFileSync("docker", ["exec", PG, "psql", "-U", "fsh", "-d", "fsh", "-tAc", consulta], {
    timeout: TIMEOUT,
    encoding: "utf8",
  }).trim();
}

function sid(accessToken) {
  return JSON.parse(Buffer.from(accessToken.split(".")[1], "base64url").toString("utf8")).sid;
}

// Counts of the session's guichê links (open / released) and of its licence activity rows.
function estadoSessao(sessaoId) {
  const [abertos, liberados] = sql(
    `SELECT count(*) FILTER (WHERE "LiberadoEm" IS NULL), count(*) FILTER (WHERE "LiberadoEm" IS NOT NULL) FROM paf."SessoesEstacao" WHERE "SessaoId" = '${sessaoId}'`,
  ).split("|").map(Number);
  const atividades = Number(sql(`SELECT count(*) FROM paf."AtividadesSessao" WHERE "SessaoId" = '${sessaoId}'`));
  return { abertos, liberados, atividades };
}

// Method, status and how long after the first each call left: tells a second logout() apart from a retry.
async function descrever(chamadas) {
  const partes = [];
  for (const c of chamadas) partes.push(`${c.metodo} ${(await c.req.response())?.status() ?? "sem resposta"} +${c.em - chamadas[0].em}ms`);
  return `${chamadas.length} chamada(s): ${partes.join("; ")}`;
}

function registrarLogouts(page) {
  const chamadas = [];
  page.on("request", (req) => {
    if (req.url().endsWith(LOGOUT_PATH)) chamadas.push({ metodo: req.method(), req, em: Date.now() });
  });
  page.on("console", (m) => {
    if (m.text().startsWith("[paf] logout")) console.log(`DIAG console: ${m.text().slice(0, 160)}`);
  });
  return chamadas;
}

async function entrar(page) {
  await page.goto(`${WEB}/login`);
  await page.locator("#tenant").fill(TENANT);
  await page.locator("#email").fill(EMAIL);
  await page.locator("#password").fill(SENHA_DEMO);
  await page.locator('button[type="submit"]').click();
  await page.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
}

async function escolherGuiche(page, guiche) {
  await page.goto(`${WEB}/paf/guiche`);
  await page.getByLabel("Guichê").waitFor({ timeout: TIMEOUT });
  await page.getByRole("button", { name: "Trocar" }).click();
  await page.getByLabel("Unidade").selectOption({ label: "Loja Demo" });
  await page.getByRole("button", { name: "Usar esta unidade" }).click();
  await page.getByLabel("Guichê").selectOption({ label: guiche });
  await page.getByRole("button", { name: "Usar este guichê" }).click();
  await page.getByRole("button", { name: "Chamar próxima" }).waitFor({ timeout: TIMEOUT });
}

function tokens(page) {
  return page.evaluate(() => ({
    access: localStorage.getItem("fsh.dashboard.accessToken"),
    refresh: localStorage.getItem("fsh.dashboard.refreshToken"),
  }));
}

// Menu → "Sign out" → confirm. Returns ms from the confirm click to the /login landing.
async function sairPeloMenu(page) {
  await page.getByRole("button", { name: "Open profile menu" }).click();
  await page.getByRole("menuitem", { name: "Sign out" }).click();
  const confirmar = page.getByRole("dialog").getByRole("button", { name: "Sign out" });
  await confirmar.waitFor({ timeout: TIMEOUT });
  const inicio = Date.now();
  await confirmar.click();
  await page.waitForURL((u) => u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  return Date.now() - inicio;
}

async function refreshRecusado(par) {
  const r = await fetch(`${API}/api/v1/identity/token/refresh`, {
    method: "POST",
    headers: { tenant: TENANT, "Content-Type": "application/json" },
    body: JSON.stringify({ token: par.access, refreshToken: par.refresh }),
    signal: AbortSignal.timeout(TIMEOUT),
  });
  return r.status;
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── 1. Menu logout with a guichê selected: licence and guichê released, old refresh token refused ──
  {
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const chamadas = registrarLogouts(page);
    await entrar(page);
    await escolherGuiche(page, "Guichê 1");
    const par = await tokens(page);
    const sessao = sid(par.access);
    const antes = estadoSessao(sessao);
    check("menu: antes do logout a sessão tem guichê aberto e atividade de licença", antes.abertos === 1 && antes.atividades > 0, JSON.stringify(antes));
    const ms = await sairPeloMenu(page);
    const depois = estadoSessao(sessao);
    check("menu: um POST ao logout do PAF", chamadas.length === 1 && chamadas[0].metodo === "POST", await descrever(chamadas));
    const resposta = chamadas[0] ? await chamadas[0].req.response() : null;
    check("menu: servidor respondeu 204", resposta?.status() === 204, String(resposta?.status()));
    check("menu: guichê solto e licença liberada", depois.abertos === 0 && depois.liberados === 1 && depois.atividades === 0, JSON.stringify(depois));
    const status = await refreshRecusado(par);
    check("menu: refresh token antigo recusado", status >= 400, String(status));
    check("menu: token apagado do navegador", (await tokens(page)).access === null);
    console.log(`INFO menu: logout em ${ms} ms`);
    await ctx.close();
  }

  // ── 2. Impersonation: the kit logout never calls the PAF server logout ──
  {
    const admin = await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
    const usuarios = await api("GET", "/api/v1/identity/users", admin.accessToken);
    const lista = Array.isArray(usuarios) ? usuarios : (usuarios.items ?? []);
    const alvo = lista.find((u) => (u.email ?? "").toLowerCase() !== EMAIL && u.isActive !== false);
    await api("POST", `${LOGOUT_PATH}`, admin.accessToken).catch((e) => console.log(`INFO limpeza do token de apoio: ${e.message}`));
    check("impersonação: há outro usuário no tenant", alvo !== undefined);
    const ctx = await browser.newContext();
    const page = await ctx.newPage();
    const chamadas = registrarLogouts(page);
    await entrar(page);
    await page.goto(`${WEB}/identity/users/${alvo.id}`);
    await page.getByRole("button", { name: "Impersonate" }).click();
    await page.getByRole("button", { name: "Start impersonation" }).click();
    await page.getByRole("button", { name: /End impersonation/ }).waitFor({ timeout: TIMEOUT });
    const par = await tokens(page);
    const actSub = JSON.parse(Buffer.from(par.access.split(".")[1], "base64url").toString("utf8")).act_sub;
    check("impersonação: token ativo é de impersonação", typeof actSub === "string" && actSub.length > 0);
    await sairPeloMenu(page);
    await page.waitForTimeout(500);
    check("impersonação: nenhuma chamada ao logout do PAF", chamadas.length === 0, `${chamadas.length} chamada(s)`);
    check("impersonação: token apagado do navegador", (await tokens(page)).access === null);
    await ctx.close();
  }

  // ── 3. Inactivity guard: the automatic logout goes through the same server call ──
  {
    const ctx = await browser.newContext();
    // Short idle stands in for the per-environment value O15 mounts; everything else is the real config.
    await ctx.route("**/config.json", async (route) => {
      const real = await route.fetch();
      const cfg = await real.json();
      await route.fulfill({ response: real, json: { ...cfg, inactivityIdleMs: 3_000, inactivityWarningMs: 2_000 } });
    });
    const page = await ctx.newPage();
    const chamadas = registrarLogouts(page);
    await entrar(page);
    await escolherGuiche(page, "Guichê 1");
    const sessao = sid((await tokens(page)).access);
    check("inatividade: guichê aberto antes", estadoSessao(sessao).abertos === 1);
    await page.waitForURL((u) => u.pathname.startsWith("/login"), { timeout: 30_000 });
    const resposta = chamadas[0] ? await chamadas[0].req.response() : null;
    check("inatividade: um POST ao logout do PAF com 204", chamadas.length === 1 && resposta?.status() === 204, `${await descrever(chamadas)}, ${resposta?.status()}`);
    const depois = estadoSessao(sessao);
    check("inatividade: guichê solto e licença liberada", depois.abertos === 0 && depois.atividades === 0, JSON.stringify(depois));
    await ctx.close();
  }

  // ── 3b. Session ended in another tab: that tab leaves locally, with no POST to the (already revoked) logout ──
  {
    const ctx = await browser.newContext();
    const abaA = await ctx.newPage();
    const abaB = await ctx.newPage();
    const chamadasA = registrarLogouts(abaA);
    const chamadasB = registrarLogouts(abaB);
    await entrar(abaA);
    await abaB.goto(`${WEB}/`);
    await abaB.getByRole("button", { name: "Open profile menu" }).waitFor({ timeout: TIMEOUT });
    await abaA.bringToFront();
    await sairPeloMenu(abaA);
    await abaB.waitForURL((u) => u.pathname.startsWith("/login"), { timeout: TIMEOUT });
    // Past the notice and any retry: a late second POST would show up here.
    await abaB.waitForTimeout(1_500);
    const respostaA = chamadasA[0] ? await chamadasA[0].req.response() : null;
    check("outra aba: a aba que saiu fez um POST com 204", chamadasA.length === 1 && respostaA?.status() === 204, `${await descrever(chamadasA)}, ${respostaA?.status()}`);
    check("outra aba: a outra aba não fez POST ao logout", chamadasB.length === 0, await descrever(chamadasB));
    check("outra aba: a outra aba foi para o login", new URL(abaB.url()).pathname.startsWith("/login"), abaB.url());
    check("outra aba: token apagado", (await tokens(abaB)).access === null);
    await ctx.close();
  }

  // ── 4. NEGATIVE: server hung, then server refusing the connection: local logout still happens ──
  {
    const ctx = await browser.newContext();
    await ctx.route(`**${LOGOUT_PATH}`, () => {
      // Never fulfilled: a server that accepted the connection and hung.
    });
    const page = await ctx.newPage();
    const chamadas = registrarLogouts(page);
    await entrar(page);
    const ms = await sairPeloMenu(page);
    check("servidor pendurado: a chamada saiu", chamadas.length === 1);
    check("servidor pendurado: logout local dentro do prazo de 3 s", ms >= 2_500 && ms < 5_000, `${ms} ms`);
    check("servidor pendurado: token apagado", (await tokens(page)).access === null);
    await ctx.close();
  }
  {
    const ctx = await browser.newContext();
    await ctx.route(`**${LOGOUT_PATH}`, (route) => route.abort("connectionrefused"));
    const page = await ctx.newPage();
    const chamadas = registrarLogouts(page);
    await entrar(page);
    const ms = await sairPeloMenu(page);
    check("servidor fora: a chamada saiu e o logout local veio sem esperar o prazo", chamadas.length === 1 && ms < 2_500, `${ms} ms`);
    check("servidor fora: token apagado", (await tokens(page)).access === null);
    await ctx.close();
  }
} catch (erro) {
  falhas += 1;
  console.log(`FAIL exceção :: ${erro?.stack ?? erro}`);
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "RESULTADO: todos os checks passaram" : `RESULTADO: ${falhas} falha(s)`);
process.exit(falhas === 0 ? 0 : 1);
