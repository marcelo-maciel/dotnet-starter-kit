// F09 walk against a REAL API and PostgreSQL: the guichê forwards a started atendimento (RF17). The screen offers
// only EmAtendimento, marks the closed window, intersects the covered prioridades, blocks confirming without a
// common one, keeps the server's 409 on screen without writing, and a forwarding to two filas leaves the same
// ficha Aguardando in both.
//
// Run (API and Vite dev server already up, fresh `seed-demo` database, PostgreSQL in a docker container):
//   PAF_API=http://localhost:5409 PAF_WEB=http://localhost:5509 PAF_PG=f09walk-pg node tests/e2e/paf-encaminhar.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const PG = process.env.PAF_PG;
if (!API || !WEB || !PG) {
  console.error("PAF_API, PAF_WEB and PAF_PG are required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
// The attendant runs the screen: one session per user, so the admin's API token survives her sign-in.
const OPERADORA = "alice@acme.com";
const SENHA_DEMO = process.env.PAF_SENHA_DEMO ?? "Password123!";
const TIMEOUT = 15_000;
const SUFIXO = Date.now().toString().slice(-6);
const CAIXA = `Caixa F09 ${SUFIXO}`;
const CREDITO = `Crédito F09 ${SUFIXO}`;
const SEGURO = `Seguro F09 ${SUFIXO}`;
const MSG_SEM_FILAS = "Selecione a(s) fila(s) para qual deseja encaminhar a ficha";
const MSG_SEM_COMUM = "As filas marcadas não têm prioridade em comum com guichê que atenda. Ajuste a seleção de filas.";
const MSG_PRIORIDADE_SERVIDOR = "A prioridade escolhida não está associada, com guichê que atenda, a todas as filas de destino.";

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

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
  return r.status === 204 ? null : r.json();
}

// Read-only SQL straight on the walk's database: the truth the screen must match.
function sql(consulta) {
  return execFileSync("docker", ["exec", PG, "psql", "-U", "fsh", "-d", "fsh", "-At", "-F", "|", "-c", consulta], {
    encoding: "utf8",
    timeout: TIMEOUT,
  }).trim();
}

async function ate(condicao) {
  const limite = Date.now() + TIMEOUT;
  while (Date.now() < limite) {
    if (await condicao()) return true;
    await new Promise((r) => setTimeout(r, 200));
  }
  return condicao();
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
let page = null;
try {
  // ── Setup through the admin API: three destination filas and the guichê covering them ──
  const admin = await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const t = admin.accessToken;
  const normal = (await api("GET", "/api/v1/paf/prioridades", t)).find((p) => p.nome === "Normal");
  const prefId = await api("POST", "/api/v1/paf/prioridades", t, { nome: `Preferencial ${SUFIXO}`, valor: 1 });
  const aparencia = { fonte: null, corFonte: null, corBotaoSelecionado: null };
  const novaFila = (nome, janela, prioridades) =>
    api("POST", "/api/v1/paf/filas", t, {
      nome,
      nomeExibicao: nome,
      horaInicial: janela?.[0] ?? null,
      horaFinal: janela?.[1] ?? null,
      setorId: null,
      aparencia,
      prioridades: prioridades.map(([prioridadeId, letra], ordem) => ({ prioridadeId, letra, numeroInicial: 1, ordem, corBotao: null })),
    });
  const caixaId = await novaFila(CAIXA, null, [[normal.id, "B"], [prefId, "C"]]);
  // A one-second window at midnight: closed for the whole walk.
  const creditoId = await novaFila(CREDITO, ["00:00:00", "00:00:01"], [[normal.id, "D"]]);
  const seguroId = await novaFila(SEGURO, null, [[prefId, "E"]]);

  const estacoes = await api("GET", "/api/v1/paf/estacoes", t);
  const guiche = estacoes.find((e) => e.tipo === "Guiche");
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const origemId = guiche.filas[0].filaId;
  const unidadeDemo = (await api("GET", "/api/v1/paf/unidades", t)).find((u) => u.nome === "Loja Demo");
  const aliceId = sql(`SELECT "Id" FROM identity."Users" WHERE "TenantId" = '${TENANT}' AND "Email" = '${OPERADORA}'`);
  await api("PUT", `/api/v1/paf/usuarios/${aliceId}/unidades`, t, {
    usuarioId: "00000000-0000-0000-0000-000000000000",
    unidades: [unidadeDemo.id],
    escopoEmpresa: false,
  });
  const atualizarGuiche = (prioridades) =>
    api("PUT", `/api/v1/paf/estacoes/${guiche.id}`, t, {
      id: guiche.id,
      nome: guiche.nome,
      filas: [origemId, caixaId, creditoId, seguroId].map((filaId, ordem) => ({ papel: "Guiche", filaId, ordem })),
      prioridades: prioridades.map((prioridadeId, ordem) => ({ prioridadeId, ordem })),
      ramal: guiche.ramal,
      // The seed leaves the guichê without its block, which an update requires (D25).
      configuracao: guiche.configuracao?.guiche
        ? guiche.configuracao
        : { emissao: null, painel: null, guiche: { numero: 1, nomeExibicao: null, ladoSeta: null, respeitarOrdemFilas: false, selecionarFicha: false } },
    });
  await atualizarGuiche([normal.id, prefId]);

  // A ficha in the origin fila, emitted by the totem's own credential.
  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, t, {});
  const cred = await api("POST", "/api/v1/paf/estacoes/vinculo/resgatar", null, { token: link.token });
  const emissao = cred.credenciais.find((c) => c.papel === "Emissao");
  const ficha = await api("POST", "/api/v1/paf/estacao/emissao/fichas", emissao.accessToken, { filaId: origemId, prioridadeId: normal.id }, { "Idempotency-Key": randomUUID() });
  const fichaId = ficha.fichaId ?? ficha.id;
  check("setup: ficha emitida na fila de origem", typeof fichaId === "string", JSON.stringify(ficha));
  const fichasAntes = sql(`SELECT count(*) FROM paf."Fichas"`);

  // ── Guichê: select, call and see the button follow the state ──
  page = await (await browser.newContext()).newPage();
  await page.goto(`${WEB}/login`);
  await page.locator("#tenant").fill(TENANT);
  await page.locator("#email").fill(OPERADORA);
  await page.locator("#password").fill(SENHA_DEMO);
  await page.locator('button[type="submit"]').click();
  await page.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  await page.goto(`${WEB}/paf/guiche`);
  const unidade = page.getByLabel("Unidade");
  const chamar = page.getByRole("button", { name: "Chamar próxima" });
  await Promise.race([unidade.waitFor({ timeout: TIMEOUT }), chamar.waitFor({ timeout: TIMEOUT }), page.getByLabel("Guichê").waitFor({ timeout: TIMEOUT })]);
  if (await unidade.isVisible()) {
    await unidade.selectOption({ label: "Loja Demo" });
    await page.getByRole("button", { name: "Usar esta unidade" }).click();
  }
  const selGuiche = page.getByLabel("Guichê");
  if (await ate(async () => (await selGuiche.isVisible()) || (await chamar.isVisible()))) {
    if (await selGuiche.isVisible()) {
      await selGuiche.selectOption(guiche.id);
      await page.getByRole("button", { name: /Usar este guichê/ }).click();
    }
  }
  await chamar.waitFor({ timeout: TIMEOUT });
  const encaminhar = page.getByRole("button", { name: "Encaminhar", exact: true });
  check("sem atendimento: Encaminhar desabilitado", await encaminhar.isDisabled());
  // The call skips a ficha younger than five seconds (REQ seção 4, guarda de tempo; ChamadaDeFicha.Elegiveis). On a
  // fast runner the screen is ready before that: wait on the database clock for the same predicate, never a fixed sleep.
  check(
    "setup: ficha passou da guarda de 5 s",
    await ate(() => sql(`SELECT "CriadoEm" <= now() - interval '5 seconds' FROM paf."Fichas" WHERE "Id" = '${fichaId}'`) === "t"),
  );
  await chamar.click();
  await page.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });
  const senha = (await page.locator("[data-senha-guiche]").textContent())?.trim();
  check("chamado: Encaminhar desabilitado", await ate(async () => (await page.locator("[data-estado-guiche]").textContent()) === "Chamada") && (await encaminhar.isDisabled()));
  await page.getByRole("button", { name: "Iniciar atendimento" }).click();
  check(
    "em atendimento: Encaminhar habilitado",
    await ate(() => encaminhar.isEnabled()),
    await page.locator("[data-estado-guiche]").textContent().catch(() => "sem estado"),
  );

  // ── Dialog: origin, destinations, closed-window badge ──
  await encaminhar.click();
  const dialogo = page.getByRole("dialog");
  await dialogo.getByText(`Senha ${senha}, fila de origem`).waitFor({ timeout: TIMEOUT });
  check("diálogo: mostra a senha e a fila de origem", await dialogo.getByText(`Senha ${senha}, fila de origem Atendimento.`).isVisible());
  const filaLabel = (nome) => dialogo.locator("label").filter({ hasText: nome });
  check("diálogo: a origem não é destino", (await dialogo.locator("fieldset label").filter({ hasText: /^Atendimento$/ }).count()) === 0);
  check("diálogo: Crédito marcado fora do horário", await filaLabel(CREDITO).getByText("fora do horário de atendimento").isVisible());
  check("diálogo: Caixa sem selo", (await filaLabel(CAIXA).getByText("fora do horário de atendimento").count()) === 0);
  const confirmar = dialogo.getByRole("button", { name: "Encaminhar", exact: true });
  const prioridade = dialogo.locator("select");
  check("nenhuma fila: confirmar bloqueado", await confirmar.isDisabled());
  check("nenhuma fila: mensagem do RF17", (await dialogo.locator("[data-bloqueio-encaminhar]").textContent()) === MSG_SEM_FILAS);

  await filaLabel(CAIXA).getByRole("checkbox").check();
  check("Caixa: original (Normal) pré-selecionada", (await prioridade.inputValue()) === normal.id);
  check("Caixa: confirmar liberado", await confirmar.isEnabled());
  await filaLabel(SEGURO).getByRole("checkbox").check();
  const opcoes = await prioridade.locator("option").allTextContents();
  check("Caixa+Seguro: interseção só Preferencial", opcoes.length === 2 && opcoes[1] === `Preferencial ${SUFIXO}`, opcoes.join(","));
  check("Caixa+Seguro: original fora, nada selecionado", (await prioridade.inputValue()) === "");
  check("Caixa+Seguro: confirmar bloqueado", await confirmar.isDisabled());
  await filaLabel(CAIXA).getByRole("checkbox").uncheck();
  await filaLabel(CREDITO).getByRole("checkbox").check();
  check("Crédito+Seguro: confirmar bloqueado com interseção vazia", await confirmar.isDisabled());
  check("Crédito+Seguro: aviso de nenhuma prioridade em comum", (await dialogo.locator("[data-bloqueio-encaminhar]").textContent()) === MSG_SEM_COMUM);
  await filaLabel(CREDITO).getByRole("checkbox").uncheck();
  await filaLabel(CAIXA).getByRole("checkbox").check();
  await prioridade.selectOption(prefId);

  // ── Server refusal: the prioridade loses guichê coverage between reading and confirming ──
  const versaoAntes = sql(`SELECT "Versao" FROM paf."Atendimentos" WHERE "FichaId" = '${fichaId}' AND "Estado" = 'EmAtendimento'`);
  await atualizarGuiche([normal.id]);
  await confirmar.click();
  const erro = dialogo.locator("[data-erro-encaminhar]");
  check("409: a mensagem do servidor fica no diálogo", await ate(async () => (await erro.count()) > 0 && (await erro.textContent()) === MSG_PRIORIDADE_SERVIDOR), await erro.textContent().catch(() => ""));
  check("409: o diálogo não fechou", await dialogo.isVisible());
  check(
    "409: nada gravado, origem EmAtendimento com a mesma Versao",
    sql(`SELECT "Versao" FROM paf."Atendimentos" WHERE "FichaId" = '${fichaId}' AND "Estado" = 'EmAtendimento'`) === versaoAntes && versaoAntes !== "",
    versaoAntes,
  );
  check("409: destinos relidos, confirmar bloqueado sem a prioridade perdida", await ate(() => confirmar.isDisabled()));

  // ── Forward to two filas: Caixa and Crédito (closed window) with Normal ──
  await filaLabel(SEGURO).getByRole("checkbox").uncheck();
  await filaLabel(CREDITO).getByRole("checkbox").check();
  check("Caixa+Crédito: Normal pré-selecionada de novo", (await prioridade.inputValue()) === normal.id);
  await confirmar.click();
  check("sucesso: diálogo fechou", await ate(async () => !(await dialogo.isVisible())));
  check("sucesso: guichê livre mostra a senha encerrada", await ate(() => page.getByText(`Último: ${senha} (Encerrado).`).isVisible()));

  const linhas = sql(
    `SELECT f."Nome", a."Estado", a."PrioridadeId", a."DataOperacional" = (now() AT TIME ZONE 'America/Sao_Paulo')::date
       FROM paf."Atendimentos" a JOIN paf."Filas" f ON f."Id" = a."FilaId"
      WHERE a."FichaId" = '${fichaId}' ORDER BY f."Nome"`,
  ).split("\n");
  check(
    "banco: origem Encerrado e as duas filas Aguardando na mesma ficha, do dia",
    linhas.length === 3 &&
      linhas.includes(`Atendimento|Encerrado|${normal.id}|t`) &&
      linhas.includes(`${CAIXA}|Aguardando|${normal.id}|t`) &&
      linhas.includes(`${CREDITO}|Aguardando|${normal.id}|t`),
    linhas.join(" ; "),
  );
  check("banco: nenhuma ficha nova", sql(`SELECT count(*) FROM paf."Fichas"`) === fichasAntes, fichasAntes);
} catch (e) {
  console.log(`FAIL exceção: ${e instanceof Error ? e.stack : e}`);
  if (page) console.log(`.. alertas na tela: ${JSON.stringify(await page.getByRole("alert").allTextContents().catch(() => []))}`);
  falhas += 1;
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
