// O12 measurement: how many API requests one guichê, one totem and one painel make when a store opens, and in the
// minute after, against a REAL API (same setup as paf-primeira-fatia.walk.mjs, fresh `seed-demo` database). The
// rate-limit partitions are per minute, so each station is counted over two fixed 60 s windows: "abertura" (from
// its first navigation) and "regime" (the next 60 s, idle, which is where any polling shows up). The numbers feed
// the RateLimitingOptions of deploy/paf/ambientes (README, section "Limite de taxa").
//
// Run (API and Vite dev server already up):
//   PAF_API=http://localhost:5391 PAF_WEB=http://localhost:5184 PAF_SENHA_DEMO=<Seed__DemoPassword of that seed> \
//     node tests/e2e/paf-abertura-loja.medicao.mjs
// Prints one JSON line per station and window plus PASS/FAIL checks; exit code 0 only when every check passed.
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const SENHA_DEMO = process.env.PAF_SENHA_DEMO;
if (!API || !WEB || !SENHA_DEMO) {
  console.error("PAF_API, PAF_WEB and PAF_SENHA_DEMO are required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const TIMEOUT = 15_000;
const JANELA_MS = 60_000;
// Endpoints under the kit's "auth" policy (partitioned by IP when anonymous): IdentityModule.cs:186-187 and the
// station credential endpoints of the PAF (RequireRateLimiting("auth")).
const CAMINHOS_AUTH = ["/identity/token/issue", "/identity/token/refresh", "/paf/estacoes/credencial/renovar", "/paf/estacoes/vinculo/resgatar"];

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

// Every request the context sends to the API origin, with its time; the hub's WebSocket upgrade is a request the
// server's limiter sees too, so it is counted from the websocket event (Playwright does not report it as a request).
function contador(ctx) {
  const pedidos = [];
  const origem = new URL(API).origin;
  ctx.on("request", (r) => {
    const url = new URL(r.url());
    if (url.origin === origem && r.method() !== "OPTIONS") pedidos.push({ em: Date.now(), caminho: `${r.method()} ${url.pathname}` });
  });
  ctx.on("page", (p) =>
    p.on("websocket", (ws) => {
      const url = new URL(ws.url().replace(/^ws/, "http"));
      if (url.origin === origem) pedidos.push({ em: Date.now(), caminho: `WS ${url.pathname}` });
    }),
  );
  return pedidos;
}

function resumo(estacao, janela, pedidos, inicio) {
  const dentro = pedidos.filter((p) => p.em >= inicio && p.em < inicio + JANELA_MS);
  const auth = dentro.filter((p) => CAMINHOS_AUTH.some((c) => p.caminho.includes(c))).length;
  const porCaminho = {};
  for (const p of dentro) porCaminho[p.caminho] = (porCaminho[p.caminho] ?? 0) + 1;
  const linha = { estacao, janela, total: dentro.length, auth, outros: dentro.length - auth, porCaminho };
  console.log(`MEDICAO ${JSON.stringify(linha)}`);
  return linha;
}

const esperar = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const admin = await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const estacoes = await api("GET", "/api/v1/paf/estacoes", admin.accessToken);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, admin.accessToken, {});

  // ── Totem: link redemption (once per device, at installation) and then the totem screen ──
  const totemCtx = await browser.newContext();
  const pedidosTotem = contador(totemCtx);
  const pVinculo = await totemCtx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  await pVinculo.close();
  const credenciais = await totemCtx.storageState();
  const resgate = pedidosTotem.length;
  pedidosTotem.length = 0;

  const inicioTotem = Date.now();
  const pTotem = await totemCtx.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  await pTotem.getByRole("button").first().waitFor({ timeout: TIMEOUT });

  // ── Painel: same credentials in its own context, as a second device ──
  const painelCtx = await browser.newContext({ storageState: credenciais });
  const pedidosPainel = contador(painelCtx);
  const inicioPainel = Date.now();
  const pPainel = await painelCtx.newPage();
  await pPainel.goto(`${WEB}/paf/painel`);
  await pPainel.getByRole("button", { name: "Iniciar painel" }).click();
  await pPainel.locator('[data-conexao="conectado"]').waitFor({ timeout: TIMEOUT });

  // ── Guichê: kit login page, then the guichê screen until "Chamar próxima" ──
  const guicheCtx = await browser.newContext();
  const pedidosGuiche = contador(guicheCtx);
  const inicioGuiche = Date.now();
  const pGuiche = await guicheCtx.newPage();
  await pGuiche.goto(`${WEB}/login`);
  await pGuiche.locator("#tenant").fill(TENANT);
  await pGuiche.locator("#email").fill(EMAIL);
  await pGuiche.locator("#password").fill(SENHA_DEMO);
  await pGuiche.locator('button[type="submit"]').click();
  await pGuiche.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  await pGuiche.goto(`${WEB}/paf/guiche`);
  await pGuiche.getByLabel("Guichê").waitFor({ timeout: TIMEOUT });
  await pGuiche.getByLabel("Guichê").selectOption({ label: "Guichê 1" });
  await pGuiche.getByRole("button", { name: "Usar este guichê" }).click();
  await pGuiche.getByRole("button", { name: "Chamar próxima" }).waitFor({ timeout: TIMEOUT });

  // Both windows of the last station to start have to close before counting.
  await esperar(inicioGuiche + 2 * JANELA_MS - Date.now() + 1_000);

  const linhas = [
    resumo("totem", "abertura", pedidosTotem, inicioTotem),
    resumo("totem", "regime", pedidosTotem, inicioTotem + JANELA_MS),
    resumo("painel", "abertura", pedidosPainel, inicioPainel),
    resumo("painel", "regime", pedidosPainel, inicioPainel + JANELA_MS),
    resumo("guiche", "abertura", pedidosGuiche, inicioGuiche),
    resumo("guiche", "regime", pedidosGuiche, inicioGuiche + JANELA_MS),
  ];
  console.log(`MEDICAO ${JSON.stringify({ estacao: "totem", janela: "resgate-do-vinculo", total: resgate })}`);

  check("guichê: o login passou pela política auth", linhas[4].auth >= 1, JSON.stringify(linhas[4].porCaminho));
  check("guichê: o hub abriu WebSocket", Object.keys(linhas[4].porCaminho).some((c) => c.startsWith("WS ")));
  check("painel: o hub abriu WebSocket", Object.keys(linhas[2].porCaminho).some((c) => c.startsWith("WS ")));
  check("totem: a abertura chamou a configuração", Object.keys(linhas[0].porCaminho).some((c) => c.includes("/emissao/configuracao")));
} catch (erro) {
  falhas += 1;
  console.log(`FAIL exceção :: ${erro?.stack ?? erro}`);
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "RESULTADO: todos os checks passaram" : `RESULTADO: ${falhas} falha(s)`);
process.exit(falhas === 0 ? 0 : 1);
