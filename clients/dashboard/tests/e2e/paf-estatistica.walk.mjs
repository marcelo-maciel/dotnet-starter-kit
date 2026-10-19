// E5 walk against a REAL API (no response mocks; one request is tampered on the way out, marked NEGATIVE): the RF07
// screen shows total 1 with both times filled after one ficha goes through the guichê, the prioridade filter changes
// the total exactly as the API answers it, copy carries TSV, the CSV file has BOM, `;`, CRLF and decimal comma, an
// inverted period is blocked on the screen, the server's 400 and 403 are shown with their message.
//
// Run through the CI harness (fresh database, seed-demo, API and dashboard on free ports, everything torn down):
//   PAF_SENHA_DEMO=<not the kit default> PAF_WALK=tests/e2e/paf-estatistica.walk.mjs deploy/paf/walk-ci.sh
// or against an API and dashboard already up on a fresh `seed-demo` database:
//   PAF_API=http://localhost:5391 PAF_WEB=http://localhost:5184 PAF_SENHA_DEMO=<seed password> node tests/e2e/paf-estatistica.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL. Opening the CSV in Excel pt-BR by double
// click is a manual step: this walk prints it as SKIPPED, never as a pass.
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const SENHA_DEMO = process.env.PAF_SENHA_DEMO;
if (!API || !WEB || !SENHA_DEMO) {
  console.error("PAF_API, PAF_WEB and PAF_SENHA_DEMO are required");
  process.exit(2);
}
const TENANT = "acme";
const ADMIN = "admin@acme.com";
// Created by the walk through the PAF cadastro with the Atendente role (PapeisPadrao): no Estatisticas.View. A kit demo
// user with no PAF unidade cannot even sign in (PAF login refuses him), so it would not reach the screen.
const SEM_PERMISSAO = `atendente.e5.${Date.now()}@acme.com`;
const TIMEOUT = 15_000;
const ROTA_API = "/api/v1/paf/estatistica";
const MSG_PERIODO = "A data final não pode ser anterior à data inicial.";

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

const esperar = (ms) => new Promise((r) => setTimeout(r, ms));

async function api(method, path, token, body, extra) {
  const headers = { tenant: TENANT, "X-FSH-App": "dashboard", ...extra };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const r = await fetch(API + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(TIMEOUT),
  });
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${await r.text()}`);
  if (r.status === 204) return null;
  const texto = await r.text();
  return texto === "" ? null : JSON.parse(texto);
}

async function entrar(contexto, email) {
  const pagina = await contexto.newPage();
  // The dev server transforms the dashboard modules on the first visit: on a cold cache that alone passes 30 s.
  await pagina.goto(`${WEB}/login`, { timeout: 180_000 });
  await pagina.locator("#tenant").fill(TENANT);
  await pagina.locator("#email").fill(email);
  await pagina.locator("#password").fill(SENHA_DEMO);
  await pagina.locator('button[type="submit"]').click();
  await pagina.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  return pagina;
}

// Total of the scope as the API answered: the last Totais row (UnidadeId null), column totalAtendimentos.
function totalDaApi(resultado) {
  const i = resultado.colunas.findIndex((c) => c.chave === "totalAtendimentos");
  const escopo = resultado.totais.find((t) => t.unidadeId === null);
  return escopo ? escopo.celulas[i].numero : 0;
}

async function totalNaTela(pagina) {
  if (await pagina.locator("[data-sem-dados]").isVisible()) return 0;
  const texto = await pagina.locator('tr[data-total="escopo"] td[data-coluna="totalAtendimentos"]').textContent();
  return Number(texto);
}

// Clicks Consultar and returns the real API answer the screen received.
async function consultar(pagina) {
  const resposta = pagina.waitForResponse((r) => r.url().includes(ROTA_API), { timeout: TIMEOUT });
  await pagina.getByRole("button", { name: "Consultar" }).click();
  const r = await resposta;
  await pagina.getByRole("button", { name: "Consultar" }).waitFor({ timeout: TIMEOUT });
  return { status: r.status(), url: new URL(r.url()), corpo: await r.json() };
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── Setup through the admin API, before the browser login (one session per user: a later login revokes it) ──
  const t = (await api("POST", "/api/v1/identity/token/issue", null, { email: ADMIN, password: SENHA_DEMO })).accessToken;
  const normal = (await api("GET", "/api/v1/paf/prioridades", t)).find((p) => p.nome === "Normal");
  const estacoes = await api("GET", "/api/v1/paf/estacoes", t);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const guiche = estacoes.find((e) => e.tipo === "Guiche");
  const filaId = totem.filas.find((f) => f.papel === "Emissao").filaId;
  // A second prioridade with no ficha: filtering by it must bring the total to zero.
  const nomeOutra = `Prioritário E5 ${Date.now().toString().slice(-5)}`;
  const outraId = await api("POST", "/api/v1/paf/prioridades", t, { nome: nomeOutra, valor: 10 });
  const lojaDemo = (await api("GET", "/api/v1/paf/unidades", t)).find((u) => u.nome === "Loja Demo");
  await api("POST", "/api/v1/paf/usuarios", t, {
    nome: "Atendente",
    sobrenome: "E5",
    email: SEM_PERMISSAO,
    senha: SENHA_DEMO,
    confirmacaoSenha: SENHA_DEMO,
    funcao: null,
    ramal: null,
    papel: "Atendente",
    unidades: [lojaDemo.id],
    escopoEmpresa: false,
  });
  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, t, {});
  const cred = await api("POST", "/api/v1/paf/estacoes/vinculo/resgatar", null, { token: link.token });
  const emissao = cred.credenciais.find((c) => c.papel === "Emissao");
  const ficha = await api("POST", "/api/v1/paf/estacao/emissao/fichas", emissao.accessToken, { filaId, prioridadeId: normal.id }, { "Idempotency-Key": randomUUID() });
  check("setup: uma ficha Normal emitida pelo totem", !!ficha?.senha, ficha?.senha);
  // The call refuses a ficha younger than five seconds (REQ seção 4, guarda de tempo); this also makes the wait > 0.
  await esperar(6_000);

  // ── The ficha through the guichê on screen: call, start, finish, with time in between so both averages are > 0 ──
  const ctx = await browser.newContext({ acceptDownloads: true });
  await ctx.grantPermissions(["clipboard-read", "clipboard-write"], { origin: WEB });
  const p = await entrar(ctx, ADMIN);
  await p.goto(`${WEB}/paf/guiche`);
  const selUnidade = p.getByLabel("Unidade");
  const selGuiche = p.getByLabel("Guichê");
  const chamar = p.getByRole("button", { name: "Chamar próxima" });
  await Promise.any([selUnidade.waitFor({ timeout: TIMEOUT }), selGuiche.waitFor({ timeout: TIMEOUT }), chamar.waitFor({ timeout: TIMEOUT })]);
  if (await selUnidade.isVisible()) {
    await selUnidade.selectOption({ label: "Loja Demo" });
    await p.getByRole("button", { name: "Usar esta unidade" }).click();
    await Promise.any([selGuiche.waitFor({ timeout: TIMEOUT }), chamar.waitFor({ timeout: TIMEOUT })]);
  }
  if (await selGuiche.isVisible()) {
    await selGuiche.selectOption(guiche.id);
    await p.getByRole("button", { name: /Usar este guichê/ }).click();
  }
  await chamar.click();
  await p.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });
  check("guichê: chamou a ficha emitida", (await p.locator("[data-senha-guiche]").textContent()) === ficha.senha);
  await p.getByRole("button", { name: "Iniciar atendimento" }).click();
  await p.getByText("Em atendimento").waitFor({ timeout: TIMEOUT });
  await esperar(3_000);
  await p.getByRole("button", { name: "Encerrar atendimento" }).click();
  await p.getByText("Nenhum atendimento em curso").waitFor({ timeout: TIMEOUT });

  // ── Menu and screen ──
  await p.goto(`${WEB}/paf/guiche`);
  const linkMenu = p.locator('aside a[href="/paf/estatistica"]');
  check("menu: entrada Estatística para quem tem Estatisticas.View", await linkMenu.waitFor({ state: "attached", timeout: TIMEOUT }).then(() => true, () => false));
  const secao = p.locator('button[aria-controls="nav-section-paf"]');
  if ((await secao.getAttribute("aria-expanded")) === "false") await secao.click();
  await linkMenu.click();
  await p.waitForURL((u) => u.pathname === "/paf/estatistica", { timeout: TIMEOUT });
  check("tela: aviso fixo da soma do escopo", await visivel(p.getByText(/soma das unidades do seu escopo/)));
  check("tela: sem botão de impressão", (await p.getByRole("button", { name: /Imprimir/i }).count()) === 0);
  const hojeBrt = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Sao_Paulo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  check("tela: período começa no dia de hoje em São Paulo", (await p.getByLabel("Data inicial").inputValue()) === hojeBrt && (await p.getByLabel("Data final").inputValue()) === hojeBrt, hojeBrt);
  check("tela: agrupamento e estações com atendimento escondidos no tipo 1", !(await p.getByLabel("Agrupar por").isVisible()) && !(await p.getByText("Somente estações que tiveram atendimento").isVisible()));

  // Type 1, no filter: total 1, both averages filled.
  const tipo1 = await consultar(p);
  check("tipo 1: API 200 com o tipo pelo número", tipo1.status === 200 && tipo1.corpo?.tipo === "AtendimentosPorTempoDeEspera" && tipo1.url.searchParams.get("tipo") === "1", `${tipo1.status} ${tipo1.url.search}`);
  const totalTela = await totalNaTela(p);
  check("tipo 1: total 1 na tela, igual ao da API", totalTela === 1 && totalDaApi(tipo1.corpo) === 1, `tela ${totalTela} api ${totalDaApi(tipo1.corpo)}`);
  const espera = await p.locator('tr[data-total="escopo"] td[data-coluna="tempoMedioEspera"]').textContent();
  const servico = await p.locator('tr[data-total="escopo"] td[data-coluna="tempoMedioAtendimento"]').textContent();
  const segundos = (s) => s.split(":").reduce((acc, n) => acc * 60 + Number(n), 0);
  check("tipo 1: espera preenchida (≥ 0:06, a ficha esperou a guarda de 5 s)", /^\d+:\d\d$/.test(espera) && segundos(espera) >= 6, espera);
  check("tipo 1: atendimento preenchido (≥ 0:02)", /^\d+:\d\d$/.test(servico) && segundos(servico) >= 2, servico);
  check("tipo 1: linha da Loja Demo na tela", await visivel(p.locator("[data-tabela-estatistica] tbody td", { hasText: "Loja Demo" }).first()));

  // Copy: TSV in the clipboard, same table.
  await p.getByRole("button", { name: "Copiar" }).click();
  await p.getByRole("status").filter({ hasText: "copiado" }).waitFor({ timeout: TIMEOUT });
  const tsv = await p.evaluate(() => navigator.clipboard.readText());
  const linhasTsv = tsv.split("\r\n");
  check("copiar: TSV com tabulação, cabeçalho e linhas da API", linhasTsv[0].includes("\t") && !tsv.includes(";") && linhasTsv.length === 1 + tipo1.corpo.linhas.length + tipo1.corpo.totais.length, JSON.stringify(linhasTsv));

  // Export: the real downloaded bytes.
  const download = p.waitForEvent("download", { timeout: TIMEOUT });
  await p.getByRole("button", { name: "Exportar CSV" }).click();
  const arquivo = await download;
  const bytes = await readFile(await arquivo.path());
  const texto = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
  const linhasCsv = texto.slice(1).split("\r\n");
  check("CSV: nome com tipo e período", arquivo.suggestedFilename() === `estatistica-tipo-1-${hojeBrt}-a-${hojeBrt}.csv`, arquivo.suggestedFilename());
  check("CSV: começa com EF BB BF", bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf, [...bytes.subarray(0, 3)].map((b) => b.toString(16)).join(" "));
  check("CSV: CRLF e nenhum LF solto", texto.endsWith("\r\n") && !texto.replaceAll("\r\n", "").includes("\n"));
  check("CSV: ponto e vírgula e duração em segundos", linhasCsv[0].split(";").length === tipo1.corpo.colunas.length && linhasCsv[0].includes("Tempo médio de espera (segundos)"), linhasCsv[0]);
  check("CSV: acentos em UTF-8 (cabeçalho com 'médio')", texto.includes("médio"));
  const esperaApi = tipo1.corpo.totais.at(-1).celulas[tipo1.corpo.colunas.findIndex((c) => c.chave === "tempoMedioEspera")].numero;
  const esperaCsv = linhasCsv[tipo1.corpo.linhas.length + tipo1.corpo.totais.length].split(";")[tipo1.corpo.colunas.findIndex((c) => c.chave === "tempoMedioEspera")];
  check("CSV: espera do total com vírgula decimal igual à API", esperaCsv === String(esperaApi).replace(".", ","), `${esperaCsv} vs ${esperaApi}`);
  console.log("SKIPPED Excel pt-BR: abrir o CSV por duplo clique é passo manual (nunca contado como verde)");

  // Prioridade filter: the total follows the API.
  await p.getByLabel("Prioridade").selectOption(normal.id);
  const soNormal = await consultar(p);
  check("prioridade Normal: filtro enviado e total 1 como na API", soNormal.url.searchParams.get("prioridades") === normal.id && (await totalNaTela(p)) === 1 && totalDaApi(soNormal.corpo) === 1, soNormal.url.search);
  await p.getByLabel("Prioridade").selectOption(outraId);
  const soOutra = await consultar(p);
  const totalOutra = await totalNaTela(p);
  check("prioridade sem fichas: total muda para 0, como na API", soOutra.status === 200 && totalOutra === 0 && totalDaApi(soOutra.corpo) === 0, `tela ${totalOutra} api ${totalDaApi(soOutra.corpo)}`);
  await p.getByLabel("Prioridade").selectOption("");

  // Types 3 and 10: their own filters appear, travel to the API and answer 200.
  await p.getByLabel("Tipo").selectOption("3");
  await p.getByText("Somente estações que tiveram atendimento").click();
  const tipo3 = await consultar(p);
  check("tipo 3: estações com atendimento enviado e 200", tipo3.status === 200 && tipo3.url.searchParams.get("somenteEstacoesComAtendimento") === "true", tipo3.url.search);
  await p.getByLabel("Tipo").selectOption("10");
  await p.getByLabel("Agrupar por").selectOption("Setor");
  const tipo10 = await consultar(p);
  check("tipo 10: agrupamento por setor enviado e 200 com 'Sem setor'", tipo10.status === 200 && tipo10.url.searchParams.get("agrupamento") === "Setor" && (await visivel(p.getByText("Sem setor").first())), tipo10.url.search);

  // Inverted period: blocked on the screen, nothing sent.
  let pedidos = 0;
  p.on("request", (r) => {
    if (r.url().includes(ROTA_API)) pedidos += 1;
  });
  await p.getByLabel("Data inicial").fill("2026-10-19");
  await p.getByLabel("Data final").fill("2026-10-18");
  check("período invertido: mensagem na tela", await visivel(p.locator("[data-periodo-invalido]", { hasText: MSG_PERIODO })));
  check("período invertido: Consultar desabilitado", await p.getByRole("button", { name: "Consultar" }).isDisabled());
  await p.getByRole("button", { name: "Consultar" }).click({ force: true });
  await esperar(500);
  check("período invertido: nenhum pedido à API", pedidos === 0, String(pedidos));

  // NEGATIVE: the server's own 400. The screen never sends an inverted period, so the outgoing request is rewritten;
  // the answer is the real API's.
  await p.getByLabel("Data inicial").fill(hojeBrt);
  await p.getByLabel("Data final").fill(hojeBrt);
  await p.route(`**${ROTA_API}?*`, async (route) => {
    const url = new URL(route.request().url());
    url.searchParams.set("dataInicial", "2026-10-19");
    url.searchParams.set("dataFinal", "2026-10-18");
    await route.continue({ url: url.toString() });
  });
  const invalido = await consultar(p);
  await p.unroute(`**${ROTA_API}?*`);
  check("400 do servidor: status real 400", invalido.status === 400, String(invalido.status));
  check("400 do servidor: mensagem da regra na tela", await visivel(p.locator("[data-erro-consulta]", { hasText: MSG_PERIODO })));
  check("400 do servidor: tabela anterior some", (await p.locator("[data-tabela-estatistica]").count()) === 0);

  // ── 403: a user without Estatisticas.View has no menu entry and sees the refusal on the screen ──
  const ctxBob = await browser.newContext();
  const pBob = await entrar(ctxBob, SEM_PERMISSAO);
  // The guichê entry (Guiches.Selecionar, an Atendente permission) proves the menu has rendered before counting.
  await pBob.locator('aside a[href="/paf/guiche"]').waitFor({ state: "attached", timeout: TIMEOUT });
  check("403: sem entrada no menu", (await pBob.locator('aside a[href="/paf/estatistica"]').count()) === 0);
  await pBob.goto(`${WEB}/paf/estatistica`);
  check(
    "403: filtros de fila e prioridade sem permissão somem com aviso, a tela continua",
    (await visivel(pBob.getByText(/Filtro de fila indisponível/))) && (await pBob.getByLabel("Fila").count()) === 0 && (await pBob.getByLabel("Prioridade").count()) === 0,
  );
  const recusa = await consultar(pBob);
  check("403: status real 403", recusa.status === 403, String(recusa.status));
  // The PAF text wins when the server wrote one in Portuguese (paf-erros.ts); either way the refusal is spelled out.
  const textoRecusa = typeof recusa.corpo?.detail === "string" && /[à-úÀ-Ú]/.test(recusa.corpo.detail) ? recusa.corpo.detail : "Você não tem permissão";
  check("403: mensagem na tela", await visivel(pBob.locator("[data-erro-consulta]", { hasText: textoRecusa })), `esperado "${textoRecusa}"`);
} catch (erro) {
  falhas += 1;
  console.log(`FAIL exceção :: ${erro?.stack ?? erro}`);
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "RESULTADO: todos os checks passaram" : `RESULTADO: ${falhas} falha(s)`);
process.exit(falhas === 0 ? 0 : 1);
