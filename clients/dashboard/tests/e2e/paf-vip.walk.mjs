// BX4 walk against a REAL API and PostgreSQL: RF19 "atendimento VIP" on the guichê screen (F11). The search by the
// printed senha lists every ficha of the current unidade with that senha on that date: none shows the message and
// calls nothing; one is called at once; two or more show the list and only the one the attendant picks is called. A
// ficha of another unidade with the same senha and date never shows, alone or next to the unidade's own.
//
// The other unidade's fichas are planted in PostgreSQL (copies of a real row with the unidade, number and keys
// changed): the search reads only paf."Fichas", and emitting there would need a whole second station setup.
//
// Run (fresh `seed-demo` database in a docker container, user fsh, database fsh; API and Vite up):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:5518 PAF_PG=<pg container> PAF_SENHA_DEMO=<seed password> \
//     node tests/e2e/paf-vip.walk.mjs
// or PAF_WALK=tests/e2e/paf-vip.walk.mjs PAF_SENHA_DEMO=<secret> deploy/paf/walk-local.sh
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const PG = process.env.PAF_PG;
const SENHA_DEMO = process.env.PAF_SENHA_DEMO;
if (!API || !WEB || !PG || !SENHA_DEMO) {
  console.error("PAF_API, PAF_WEB, PAF_PG and PAF_SENHA_DEMO are required");
  process.exit(2);
}
const TENANT = "acme";
const ADMIN = "admin@acme.com";
// The admin keeps the API token (one session per user); the attendant runs the guichê on screen.
const ALICE = "alice@acme.com";
const TIMEOUT = 15_000;
const SUFIXO = Date.now().toString().slice(-5);
const MSG_NENHUMA = "Nenhuma ficha com esta senha nesta data nesta unidade.";

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
  if (r.status === 204) return null;
  const texto = await r.text();
  return texto === "" ? null : JSON.parse(texto);
}

function sql(consulta) {
  return execFileSync("docker", ["exec", PG, "psql", "-U", "fsh", "-d", "fsh", "-v", "ON_ERROR_STOP=1", "-At", "-F", "|", "-c", consulta], {
    encoding: "utf8",
    timeout: TIMEOUT,
  }).trim();
}

const estadoNoBanco = (atendimentoId) =>
  sql(`SELECT "Estado" || '|' || coalesce("EstacaoId"::text, '') FROM paf."Atendimentos" WHERE "Id" = '${atendimentoId}'`);
const sub = (token) => JSON.parse(Buffer.from(token.split(".")[1], "base64url").toString("utf8")).sub;

async function ate(condicao) {
  const limite = Date.now() + TIMEOUT;
  while (Date.now() < limite) {
    if (await condicao()) return true;
    await new Promise((r) => setTimeout(r, 200));
  }
  return condicao();
}

// ── Setup through the admin API (company admin first: only it creates a unidade, RF23) ──
let t = (await api("POST", "/api/v1/identity/token/issue", null, { email: ADMIN, password: SENHA_DEMO })).accessToken;
const demo = (await api("GET", "/api/v1/paf/unidades", t)).find((u) => u.nome === "Loja Demo");
await api("PUT", `/api/v1/paf/usuarios/${sub(t)}/unidades`, t, { usuarioId: sub(t), unidades: [demo.id], escopoEmpresa: true });
t = (await api("POST", "/api/v1/identity/token/issue", null, { email: ADMIN, password: SENHA_DEMO })).accessToken;

const normal = (await api("GET", "/api/v1/paf/prioridades", t)).find((p) => p.nome === "Normal");
const estacoes = await api("GET", "/api/v1/paf/estacoes", t);
const guiche = estacoes.find((e) => e.tipo === "Guiche");
const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
const atendimentoId = totem.filas.find((f) => f.papel === "Emissao").filaId;
const nomeAtendimento = (await api("GET", "/api/v1/paf/filas", t)).find((f) => f.id === atendimentoId).nome;
// Letter "A1" from 2 next to "A" from 12: both print A12, the case a letter holding digits makes possible (BX4).
const nomeProtocolo = `Protocolo VIP ${SUFIXO}`;
const protocoloId = await api("POST", "/api/v1/paf/filas", t, {
  nome: nomeProtocolo,
  nomeExibicao: nomeProtocolo,
  horaInicial: null,
  horaFinal: null,
  setorId: null,
  aparencia: { fonte: null, corFonte: null, corBotaoSelecionado: null },
  prioridades: [{ prioridadeId: normal.id, letra: "A1", numeroInicial: 2, ordem: 0, corBotao: null }],
});
check(
  "setup: nenhuma ficha emitida ainda na fila A (o número inicial vale)",
  sql(`SELECT count(*) FROM paf."Fichas" WHERE "UnidadeId" = '${demo.id}'`) === "0",
);
sql(`UPDATE paf."FilaPrioridades" SET "NumeroInicial" = 12 WHERE "FilaId" = '${atendimentoId}' AND "PrioridadeId" = '${normal.id}'`);

await api("PUT", `/api/v1/paf/estacoes/${totem.id}`, t, {
  id: totem.id,
  nome: totem.nome,
  filas: [...totem.filas, { papel: "Emissao", filaId: protocoloId, ordem: totem.filas.length }],
  prioridades: totem.prioridades,
  ramal: totem.ramal,
  // The seed may leave the blocks empty; an update requires the station type's blocks (D25), as paf-midia-painel sets.
  configuracao: {
    emissao: totem.configuracao?.emissao ?? {
      titulo: null,
      tamanhoFontePx: null,
      tamanhoBotaoPx: null,
      corFundoBotao: null,
      corFonteBotao: null,
      corDescricaoBotao: null,
      prazoCicloEmissaoSegundos: null,
    },
    guiche: null,
    painel: totem.configuracao?.painel ?? {
      mostrarDataHora: false,
      tamanhoFontePx: null,
      corFundo: null,
      corFonteFilas: null,
      corFonteChamada: null,
      corFonteAlternada: null,
      modo: "Informativo",
      tempoExibicaoChamadaSegundos: null,
    },
  },
});
await api("PUT", `/api/v1/paf/estacoes/${guiche.id}`, t, {
  id: guiche.id,
  nome: guiche.nome,
  filas: [atendimentoId, protocoloId].map((filaId, ordem) => ({ papel: "Guiche", filaId, ordem })),
  prioridades: [{ prioridadeId: normal.id, ordem: 0 }],
  ramal: guiche.ramal,
  // The seed leaves the guichê without its block, which an update requires (D25).
  configuracao: guiche.configuracao?.guiche
    ? guiche.configuracao
    : { emissao: null, painel: null, guiche: { numero: 1, nomeExibicao: null, ladoSeta: null, respeitarOrdemFilas: false, selecionarFicha: false } },
});

const aliceId = sql(`SELECT "Id" FROM identity."Users" WHERE "TenantId" = '${TENANT}' AND "Email" = '${ALICE}'`);
await api("PUT", `/api/v1/paf/usuarios/${aliceId}/unidades`, t, { usuarioId: aliceId, unidades: [demo.id], escopoEmpresa: false });
// Gerente (PapeisPadrao) holds Atendimentos.Vip; there is no PAF role endpoint for it yet (C15).
sql(`INSERT INTO identity."UserRoles" ("UserId", "RoleId", "TenantId")
     SELECT '${aliceId}', r."Id", r."TenantId" FROM identity."Roles" r WHERE r."TenantId" = '${TENANT}' AND r."Name" = 'Gerente'
     ON CONFLICT DO NOTHING`);

const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, t, {});
const cred = await api("POST", "/api/v1/paf/estacoes/vinculo/resgatar", null, { token: link.token });
const emissao = cred.credenciais.find((c) => c.papel === "Emissao").accessToken;
const emitir = (filaId) =>
  api("POST", "/api/v1/paf/estacao/emissao/fichas", emissao, { filaId, prioridadeId: normal.id }, { "Idempotency-Key": randomUUID() });
const dupAtendimento = await emitir(atendimentoId);
const dupProtocolo = await emitir(protocoloId);
const unica = await emitir(atendimentoId);
check(
  "setup: A12 duas vezes (filas A e A1) e A13 uma vez",
  dupAtendimento.senha === "A12" && dupProtocolo.senha === "A12" && unica.senha === "A13",
  `${dupAtendimento.senha} ${dupProtocolo.senha} ${unica.senha}`,
);

// ── Another unidade with fichas printing A12, A13 and A77 on the same day ──
const outraId = await api("POST", "/api/v1/paf/unidades", t, { nome: `Loja VIP Outra ${SUFIXO}`, documentoFiscal: null });
// Generated columns (DataOperacional) are left to PostgreSQL: only the stored ones are copied.
const colunas = sql(`SELECT string_agg(format('%I', column_name), ',' ORDER BY ordinal_position) FROM information_schema.columns
                     WHERE table_schema = 'paf' AND table_name = 'Fichas' AND is_generated = 'NEVER'`).split(",");
const plantar = (letra, numero) => {
  // Ids are Guids the API returned and the numbers are literals here: nothing typed by a person reaches the SQL.
  const trocas = {
    '"Id"': "gen_random_uuid()",
    '"UnidadeId"': `'${outraId}'`,
    '"Letra"': `'${letra}'`,
    '"Numero"': String(numero),
    '"IdentificadorPublico"': "gen_random_uuid()",
    '"ChaveIdempotencia"': "gen_random_uuid()::text",
  };
  const nome = (c) => (c.startsWith('"') ? c : `"${c}"`);
  sql(`INSERT INTO paf."Fichas" (${colunas.join(",")})
       SELECT ${colunas.map((c) => trocas[nome(c)] ?? c).join(",")} FROM paf."Fichas" WHERE "Id" = '${unica.fichaId}'`);
};
plantar("A", 12);
plantar("A", 13);
plantar("A", 77);
check(
  "setup: a outra unidade tem A12, A13 e A77 na mesma data",
  sql(`SELECT count(*) FROM paf."Fichas" WHERE "UnidadeId" = '${outraId}' AND "DataOperacional" =
       (SELECT "DataOperacional" FROM paf."Fichas" WHERE "Id" = '${unica.fichaId}')`) === "3",
);
// The call refuses a ficha younger than five seconds (REQ seção 4, guarda de tempo).
await new Promise((r) => setTimeout(r, 6_000));

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const pagina = await (await browser.newContext()).newPage();
  pagina.on("dialog", (d) => void d.accept());
  await pagina.goto(`${WEB}/login`);
  await pagina.locator("#tenant").fill(TENANT);
  await pagina.locator("#email").fill(ALICE);
  await pagina.locator("#password").fill(SENHA_DEMO);
  await pagina.locator('button[type="submit"]').click();
  await pagina.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  await pagina.goto(`${WEB}/paf/guiche`);
  const selGuiche = pagina.getByLabel("Guichê");
  const chamar = pagina.getByRole("button", { name: "Chamar próxima" });
  await ate(async () => (await selGuiche.isVisible()) || (await chamar.isVisible()));
  if (await selGuiche.isVisible()) {
    await selGuiche.selectOption(guiche.id);
    await pagina.getByRole("button", { name: /Usar este guichê/ }).click();
  }
  await chamar.waitFor({ timeout: TIMEOUT });

  const vip = pagina.getByRole("form", { name: "Atendimento VIP" });
  check("tela: formulário VIP para quem tem Atendimentos.Vip", await vip.waitFor({ timeout: TIMEOUT }).then(() => true, () => false));
  const lista = vip.getByRole("list", { name: "Fichas com esta senha" });
  const buscar = async (senha, fila) => {
    await vip.getByLabel("Senha").fill(senha);
    await vip.getByLabel("Fila").selectOption({ label: fila });
    await vip.getByRole("button", { name: "Chamar VIP" }).click();
  };
  const naoChamadas = () => [dupAtendimento, dupProtocolo, unica].filter((f) => estadoNoBanco(f.atendimentoId).startsWith("Aguardando|")).length;

  // ── None: A77 exists only in the other unidade ──
  await buscar("A77", nomeAtendimento);
  check("nenhuma: mostra a mensagem", await pagina.getByText(MSG_NENHUMA).waitFor({ timeout: TIMEOUT }).then(() => true, () => false));
  check("nenhuma: a A77 da outra unidade não aparece (sem lista)", (await lista.count()) === 0);
  check("nenhuma: nada chamado", naoChamadas() === 3);

  // ── One: A13 here (the other unidade's A13 does not count), typed lower case: called at once ──
  await buscar("a13", nomeAtendimento);
  check("uma: chamada direto no guichê", await ate(() => estadoNoBanco(unica.atendimentoId) === `Chamado|${guiche.id}`), estadoNoBanco(unica.atendimentoId));
  check("uma: sem lista para escolher (a A13 da outra unidade não entra)", (await lista.count()) === 0);
  check("uma: as duas A12 seguem Aguardando", naoChamadas() === 2);
  await pagina.getByRole("button", { name: "Iniciar atendimento" }).click();
  await ate(() => estadoNoBanco(unica.atendimentoId).startsWith("EmAtendimento|"));
  await pagina.getByRole("button", { name: "Encerrar atendimento" }).click();
  check("uma: encerrada pela tela", await ate(() => estadoNoBanco(unica.atendimentoId).startsWith("Encerrado|")), estadoNoBanco(unica.atendimentoId));
  await vip.getByRole("button", { name: "Chamar VIP" }).waitFor({ timeout: TIMEOUT });
  await ate(() => vip.getByRole("button", { name: "Chamar VIP" }).isEnabled());

  // ── Two or more: A12 in both filas here (and one in the other unidade): the list waits for the pick ──
  await buscar("A12", nomeProtocolo);
  check("duas: a lista aparece", await lista.waitFor({ timeout: TIMEOUT }).then(() => true, () => false));
  const linhas = lista.getByRole("listitem");
  const textos = await linhas.allTextContents();
  check("duas: só as duas desta unidade, na ordem de emissão", textos.length === 2 && textos[0].includes(nomeAtendimento) && textos[1].includes(nomeProtocolo), JSON.stringify(textos));
  check("duas: nada chamado antes da escolha", naoChamadas() === 2);
  await linhas.filter({ hasText: nomeProtocolo }).getByRole("button", { name: "Chamar esta" }).click();
  check(
    "duas: a escolhida (fila A1) chamada no guichê",
    await ate(() => estadoNoBanco(dupProtocolo.atendimentoId) === `Chamado|${guiche.id}`),
    estadoNoBanco(dupProtocolo.atendimentoId),
  );
  check("duas: a outra A12 segue Aguardando", estadoNoBanco(dupAtendimento.atendimentoId).startsWith("Aguardando|"));
  check("duas: a lista some depois da chamada", await ate(async () => (await lista.count()) === 0));
  check(
    "outra unidade: nenhum atendimento dela mexido",
    sql(`SELECT count(*) FROM paf."Atendimentos" WHERE "UnidadeId" = '${outraId}'`) === "0" &&
      sql(`SELECT count(*) FROM paf."Atendimentos" WHERE "EstacaoId" = '${guiche.id}' AND "Estado" IN ('Chamado', 'EmAtendimento')`) === "1",
  );
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.stack : String(erro));
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "OK" : `${falhas} FALHA(S)`);
process.exit(falhas === 0 ? 0 : 1);
