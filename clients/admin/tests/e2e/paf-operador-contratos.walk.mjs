// C08 walk against a REAL API: the Codefi operator creates a client tenant in the admin, the client's admin is
// refused for lack of contract, the operator records the contract of the initial unidade on the PAF page (client
// tenant chosen there, sent explicitly), and the client's admin then signs in. The only interception is
// /config.json, pointing the production build at the API.
//
// Run (API up on a migrated + seeded database with its outbox dispatcher and Hangfire on, admin under `vite preview`):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:4188 node tests/e2e/paf-operador-contratos.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
if (!API || !WEB) {
  console.error("PAF_API and PAF_WEB are required");
  process.exit(2);
}
const ROOT_EMAIL = "admin@root.com";
const SENHA = "123Pa$$word!";
const TIMEOUT = 15_000;
const SEM_CONTRATO = "Nenhuma unidade do seu acesso tem contrato ativo. Procure o administrador da empresa.";

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

async function emitirToken(tenant, email) {
  const r = await fetch(`${API}/api/v1/identity/token/issue`, {
    method: "POST",
    headers: { tenant, "Content-Type": "application/json" },
    body: JSON.stringify({ email, password: SENHA }),
    signal: AbortSignal.timeout(TIMEOUT),
  });
  const corpo = await r.json().catch(() => ({}));
  return { status: r.status, detalhe: corpo.detail, token: corpo.token ?? corpo.accessToken };
}

function hojeMenos(dias) {
  const d = new Date(Date.now() - dias * 86_400_000);
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Sao_Paulo" }).format(d);
}

async function esperar(descricao, tentar, limiteMs) {
  const fim = Date.now() + limiteMs;
  let ultimo;
  while (Date.now() < fim) {
    ultimo = await tentar();
    if (ultimo.ok) return ultimo;
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`${descricao}: não aconteceu em ${limiteMs} ms (último: ${JSON.stringify(ultimo)})`);
}

async function entrar(page, tenant, email) {
  await page.goto(`${WEB}/login`);
  await page.locator("#tenant").fill(tenant);
  await page.locator("#email").fill(email);
  await page.locator("#password").fill(SENHA);
  const resposta = page.waitForResponse((r) => r.url().endsWith("/token/issue"), { timeout: TIMEOUT });
  await page.getByRole("button", { name: /sign in/i }).click();
  return resposta;
}

const browser = await chromium.launch({ channel: "chrome" });
try {
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

  // 1. The operator signs in and creates the client tenant through the kit's own dialog.
  check("operador entra", (await entrar(page, "root", ROOT_EMAIL)).status() === 200);
  await page.waitForURL((u) => !u.pathname.startsWith("/login"));
  const nomeCliente = `c08walk${Date.now().toString(36)}`;
  await page.goto(`${WEB}/tenants`);
  await page.getByRole("button", { name: /new tenant/i }).click();
  // The identifier is derived from the display name (read-only field); read what the dialog will send.
  await page.locator("#ct-name").fill(nomeCliente);
  const tenantId = await page.locator("#ct-id").inputValue();
  const adminEmail = `admin@${tenantId}.test`;
  await page.locator("#ct-adminEmail").fill(adminEmail);
  await page.locator("#ct-adminPassword").fill(SENHA);
  await page.getByRole("button", { name: "Create tenant" }).click();
  await page.waitForURL(`**/tenants/${tenantId}`, { timeout: 60_000 });
  check("tenant criado pelo admin", true, tenantId);

  // 2. The PAF reaches the tenant (outbox, then the Hangfire job): the admin is refused for lack of contract.
  const recusa = await esperar(
    "recusa sem contrato",
    async () => {
      const t = await emitirToken(tenantId, adminEmail);
      return { ok: t.status === 403 && t.detalhe === SEM_CONTRATO, ...t, token: undefined };
    },
    90_000,
  );
  check("admin do cliente recusado sem contrato", recusa.status === 403, recusa.detalhe);

  // 3. PAF page through the menu; the client is chosen on the page and goes to the URL.
  const link = page.getByRole("link", { name: "Unidades e contratos" });
  if (!(await link.isVisible())) await page.getByRole("button", { name: /^PAF$/ }).click();
  await link.click();
  await page.waitForURL("**/paf/unidades");
  const chamadasOperador = [];
  page.on("request", (r) => {
    if (r.url().includes("/api/v1/paf/")) chamadasOperador.push({ url: r.url(), tenant: r.headers()["tenant"], metodo: r.method() });
  });
  await page.getByRole("button", { name: /escolha o cliente/i }).click();
  await page.getByRole("menuitem", { name: new RegExp(tenantId) }).click();
  await page.waitForURL(`**/paf/unidades?tenant=${tenantId}`);
  await page.getByText("Sem contrato: ninguém desta unidade entra").waitFor();
  check("unidade inicial aparece sem contrato", await page.getByText("Inicial", { exact: true }).isVisible());

  // 4. Negative paths of the form: end before start, then (after the first contract) an overlap; neither is sent.
  await page.getByRole("button", { name: /novo contrato de/i }).click();
  await page.locator("#contrato-inicio").fill("2026-06-01");
  await page.locator("#contrato-fim").fill("2026-05-31");
  check("fim antes do início explicado", await page.getByText("O fim da vigência não pode ser anterior ao início.").isVisible());
  await page.locator("#contrato-fim").fill("");
  await page.locator("#contrato-inicio").fill(hojeMenos(2));
  await page.locator("#contrato-plano").fill("Básico");
  await page.locator("#contrato-limite").fill("3");
  const gravacao = page.waitForResponse((r) => r.url().endsWith("/api/v1/paf/contratos") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Gravar contrato" }).click();
  check("contrato gravado (200)", (await gravacao).status() === 200);
  await page.getByText("Vigente", { exact: true }).waitFor();
  check("tabela mostra o contrato vigente", await page.getByRole("cell", { name: "Básico" }).isVisible());

  const postsAntes = chamadasOperador.filter((c) => c.metodo === "POST").length;
  await page.getByRole("button", { name: /novo contrato de/i }).click();
  await page.locator("#contrato-inicio").fill(hojeMenos(-10));
  await page.locator("#contrato-plano").fill("Pro");
  await page.locator("#contrato-limite").fill("9");
  check("sobreposição explicada antes de enviar", await page.getByText(/se sobrepõe ao contrato de/).isVisible());
  await page.getByRole("button", { name: "Gravar contrato" }).click();
  await page.waitForTimeout(500);
  check("sobreposição não é enviada", chamadasOperador.filter((c) => c.metodo === "POST").length === postsAntes);
  await page.getByRole("button", { name: "Cancelar" }).click();

  check(
    "toda chamada do PAF levou o tenant do cliente",
    chamadasOperador.length > 0 && chamadasOperador.every((c) => c.tenant === tenantId),
    JSON.stringify(chamadasOperador.map((c) => `${c.metodo} ${c.tenant}`)),
  );

  // 5. The client's admin now signs in (same admin app, his tenant), and the operator page is not his.
  const cliente = await browser.newContext();
  await cliente.route("**/config.json", (route) =>
    route.fulfill({
      contentType: "application/json",
      body: JSON.stringify({ apiBase: API, defaultTenant: tenantId, dashboardUrl: "http://localhost:5174", inactivityIdleMs: 600000, inactivityWarningMs: 60000 }),
    }),
  );
  const paginaCliente = await cliente.newPage();
  paginaCliente.setDefaultTimeout(TIMEOUT);
  const login = await entrar(paginaCliente, tenantId, adminEmail);
  check("admin do cliente entra depois do contrato", login.status() === 200, String(login.status()));
  await paginaCliente.waitForURL((u) => !u.pathname.startsWith("/login"));
  const lista = await fetch(`${API}/api/v1/paf/operador/unidades`, {
    headers: { tenant: tenantId, Authorization: `Bearer ${(await emitirToken(tenantId, adminEmail)).token}` },
    signal: AbortSignal.timeout(TIMEOUT),
  });
  check("admin do cliente 403 na lista do operador", lista.status === 403, String(lista.status));
  await cliente.close();
} catch (e) {
  check("walk sem exceção", false, e instanceof Error ? e.message : String(e));
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
