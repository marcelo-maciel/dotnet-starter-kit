// C06 walk against a REAL API (no route mocks): the station cadastro generates the device link, the device opens it
// in an anonymous window, a second generation kills the first link and its device, revoking drops the panel, a
// guichê station has no link and a person without the link permissions sees no button and gets 403 from the API.
//
// Run (API and Vite dev server already up, fresh `seed-demo` database):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:5518 PAF_SENHA_DEMO=<Seed__DemoPassword of that seed> \
//     node tests/e2e/paf-vinculo-estacao.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
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

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

async function chamar(method, path, token, body) {
  const headers = { tenant: TENANT, "X-FSH-App": "dashboard" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  return fetch(API + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(TIMEOUT),
  });
}

async function api(method, path, token, body) {
  const r = await chamar(method, path, token, body);
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${await r.text()}`);
  return r.status === 204 ? null : r.json();
}

async function entrar(ctx, email, senha) {
  const p = await ctx.newPage();
  await p.goto(`${WEB}/login`);
  await p.locator("#tenant").fill(TENANT);
  await p.locator("#email").fill(email);
  await p.locator("#password").fill(senha);
  await p.locator('button[type="submit"]').click();
  await p.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  return p;
}

const linha = (p, id) => p.locator(`li[data-estacao="${id}"]`);

async function gerarPelaTela(p, id) {
  await linha(p, id).getByRole("button", { name: "Gerar link" }).click();
  const painel = p.locator("[data-link-gerado]");
  await painel.waitFor({ timeout: TIMEOUT });
  return { link: await painel.locator("input").inputValue(), avisos: await painel.getByRole("listitem").allTextContents() };
}

// The device: a fresh anonymous context opens the link, then the panel screen.
async function abrirPainel(browser, link) {
  const ctx = await browser.newContext();
  const p = await ctx.newPage();
  await p.goto(link);
  const abriu = await p.getByRole("link", { name: "Abrir o painel" }).waitFor({ timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
  const enderecoLimpo = !p.url().includes("token=");
  if (abriu) {
    await p.getByRole("link", { name: "Abrir o painel" }).click();
    await p.getByRole("button", { name: "Iniciar painel" }).click();
  }
  return { ctx, p, abriu, enderecoLimpo };
}

async function painelCaiu(p) {
  return p.getByText("Painel não vinculado ou vínculo revogado", { exact: false }).waitFor({ timeout: 30_000 }).then(
    () => true,
    () => false,
  );
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const ctxAdmin = await browser.newContext({ permissions: ["clipboard-read", "clipboard-write"] });
  const pAdmin = await entrar(ctxAdmin, EMAIL, SENHA_DEMO);
  // Every API call reuses the browser's own token: a second login would revoke the browser session (sessão única).
  const token = () => pAdmin.evaluate(() => localStorage.getItem("fsh.dashboard.accessToken"));
  const estacoes = await api("GET", "/api/v1/paf/estacoes", await token());
  const combinada = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const guiche = estacoes.find((e) => e.tipo === "Guiche");
  check("setup: seed com estação Totem e Painel e com guichê", Boolean(combinada && guiche));

  await pAdmin.goto(`${WEB}/paf/estacoes`);
  await linha(pAdmin, combinada.id).waitFor({ timeout: TIMEOUT });
  check("guichê: sem botão Gerar link", (await linha(pAdmin, guiche.id).getByRole("button", { name: "Gerar link" }).count()) === 0);
  check("guichê: sem linhas de vínculo", (await linha(pAdmin, guiche.id).locator("[data-vinculo-estacao]").count()) === 0);
  const vinculoAntes = await linha(pAdmin, combinada.id).locator("[data-vinculo-estacao]").innerText();
  check("Totem e Painel: listagem mostra Totem e Painel", vinculoAntes.includes("Totem:") && vinculoAntes.includes("Painel:"), vinculoAntes);

  // ── First generation: shown once, copyable, opened on a device in an anonymous window ──
  const primeiro = await gerarPelaTela(pAdmin, combinada.id);
  const url = new URL(primeiro.link);
  check("link: rota de resgate na origem do dashboard", url.origin === new URL(WEB).origin && url.pathname === "/paf/estacao/vinculo", primeiro.link);
  check("link: cliente e token no fragmento, nada na query", url.search === "" && url.hash.startsWith(`#tenant=${TENANT}&token=`));
  await pAdmin.locator("[data-link-gerado]").getByRole("button", { name: "Copiar" }).click();
  await pAdmin.getByText("Link copiado.").waitFor({ timeout: TIMEOUT });
  check("copiar: área de transferência recebe o link", (await pAdmin.evaluate(() => navigator.clipboard.readText())) === primeiro.link);
  check(
    "listagem: link gerado aguardando o aparelho",
    await linha(pAdmin, combinada.id).getByText("aguardando o aparelho").waitFor({ timeout: TIMEOUT }).then(() => true, () => false),
  );

  const dev1 = await abrirPainel(browser, primeiro.link);
  check("aparelho 1: resgate em janela anônima", dev1.abriu);
  check("aparelho 1: token sai da barra de endereço", dev1.enderecoLimpo);
  await pAdmin.locator("[data-link-gerado]").getByRole("button", { name: "Fechar" }).click();
  check("exibição única: link some ao fechar", (await pAdmin.locator("[data-link-gerado]").count()) === 0);
  await pAdmin.reload();
  check(
    "listagem: link resgatado depois do aparelho",
    await linha(pAdmin, combinada.id).getByText("Link resgatado em").waitFor({ timeout: TIMEOUT }).then(() => true, () => false),
  );

  // ── Second generation: warns, kills the first link and its device ──
  const segundo = await gerarPelaTela(pAdmin, combinada.id);
  check("segundo link: diferente do primeiro", segundo.link !== primeiro.link);
  check("segundo link: aviso de que o anterior caiu", segundo.avisos.some((a) => a.includes("anterior deixou de valer")), segundo.avisos.join(" | "));
  check("aparelho 1: painel derrubado pela nova geração", await painelCaiu(dev1.p));
  const reuso = await browser.newContext();
  const pReuso = await reuso.newPage();
  await pReuso.goto(primeiro.link);
  check(
    "primeiro link reaberto: recusa",
    await pReuso.getByText("Link inválido, vencido ou já usado").waitFor({ timeout: TIMEOUT }).then(() => true, () => false),
  );
  await reuso.close();

  const dev2 = await abrirPainel(browser, segundo.link);
  check("aparelho 2: resgate do segundo link", dev2.abriu);
  check(
    "aparelho 2: painel carregado",
    await dev2.p.getByRole("region", { name: "Últimas chamadas" }).waitFor({ timeout: TIMEOUT }).then(() => true, () => false),
  );

  // ── Revoke: confirmation, then the panel drops ──
  pAdmin.once("dialog", (d) => void d.accept());
  await linha(pAdmin, combinada.id).getByRole("button", { name: "Revogar link" }).click();
  await pAdmin.getByText(`Link de ${combinada.nome} revogado.`).waitFor({ timeout: TIMEOUT });
  check("revogar: painel do aparelho 2 cai", await painelCaiu(dev2.p));
  check(
    "revogar: listagem volta a sem link e o botão Revogar some",
    (await linha(pAdmin, combinada.id).getByText("Sem link").waitFor({ timeout: TIMEOUT }).then(() => true, () => false)) &&
      (await linha(pAdmin, combinada.id).getByRole("button", { name: "Revogar link" }).count()) === 0,
  );
  await dev1.ctx.close();
  await dev2.ctx.close();

  // ── A Gerente (Estacoes.View without VinculosEstacao.*): no buttons, and the API answers 403 ──
  const emailGerente = `gerente.c06.${Date.now()}@acme.com`;
  const senhaGerente = `C06-${Date.now()}-Aa!`;
  await api("POST", "/api/v1/paf/usuarios", await token(), {
    nome: "Gerente",
    sobrenome: "C06",
    email: emailGerente,
    senha: senhaGerente,
    confirmacaoSenha: senhaGerente,
    funcao: null,
    ramal: null,
    papel: "Gerente",
    unidades: [combinada.unidadeId],
    escopoEmpresa: false,
  });
  const ctxGerente = await browser.newContext();
  const pGerente = await entrar(ctxGerente, emailGerente, senhaGerente);
  await pGerente.goto(`${WEB}/paf/estacoes`);
  await linha(pGerente, combinada.id).waitFor({ timeout: TIMEOUT });
  check("sem permissão: botão Gerar link some", (await pGerente.getByRole("button", { name: "Gerar link" }).count()) === 0);
  await api("POST", `/api/v1/paf/estacoes/${combinada.id}/vinculo`, await token(), {});
  await pGerente.reload();
  await linha(pGerente, combinada.id).getByText("aguardando o aparelho").waitFor({ timeout: TIMEOUT });
  check("sem permissão: botão Revogar link some mesmo com link vivo", (await pGerente.getByRole("button", { name: "Revogar link" }).count()) === 0);
  const tokenGerente = await pGerente.evaluate(() => localStorage.getItem("fsh.dashboard.accessToken"));
  const gerar = await chamar("POST", `/api/v1/paf/estacoes/${combinada.id}/vinculo`, tokenGerente, {});
  check("sem permissão: gerar pela API 403", gerar.status === 403, String(gerar.status));
  const revogar = await chamar("POST", `/api/v1/paf/estacoes/${combinada.id}/vinculo/revogar`, tokenGerente, {});
  check("sem permissão: revogar pela API 403", revogar.status === 403, String(revogar.status));
  await ctxGerente.close();
  await ctxAdmin.close();
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.stack : String(erro));
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas} check(s)`);
process.exit(falhas === 0 ? 0 : 1);
