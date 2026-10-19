// F21 walk against a REAL API and PostgreSQL: RF04 "selecionar ficha para atendimento" on the RF12 screen. A
// guichê without the option sees no "Chamar esta"; with it, the button carries the place in the call order, calls a
// ficha out of order, asks the RF15 questions first (refusal writes nothing, EmAtendimento is closed, Chamado is
// cancelled), and a guichê whose list went stale gets the server's 409, sees the message and the refreshed list.
//
// Run against the production build (`npm run build`, dist/config.json apiBase = the API, `vite preview`), fresh
// `seed-demo` database in a docker container (user fsh, database fsh):
//   PAF_API=http://localhost:5421 PAF_WEB=http://localhost:5521 PAF_PG=f21walk-pg node tests/e2e/paf-selecionar-ficha.walk.mjs
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
const ADMIN = "admin@acme.com";
// One session per user: the admin keeps the API token, two attendants run the two guichês on screen.
const ALICE = "alice@acme.com";
const BOB = "bob@acme.com";
const SENHA_DEMO = process.env.PAF_SENHA_DEMO ?? "Password123!";
const TIMEOUT = 15_000;
const MSG_JA_CHAMADA = "A ficha já foi chamada por outro guichê.";

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

// The database is the truth the screen must match; setup of roles goes here too (no PAF role endpoint yet, C15).
function sql(consulta) {
  return execFileSync("docker", ["exec", PG, "psql", "-U", "fsh", "-d", "fsh", "-At", "-F", "|", "-c", consulta], {
    encoding: "utf8",
    timeout: TIMEOUT,
  }).trim();
}

const atendimentoNoBanco = (id) =>
  sql(`SELECT "Estado" || '|' || coalesce("EstacaoId"::text, '') || '|' || "Versao" || '|' || ("IniciadoEm" IS NOT NULL)
         FROM paf."Atendimentos" WHERE "Id" = '${id}'`);

async function ate(condicao) {
  const limite = Date.now() + TIMEOUT;
  while (Date.now() < limite) {
    if (await condicao()) return true;
    await new Promise((r) => setTimeout(r, 200));
  }
  return condicao();
}

// ── Setup through the admin API: two guichês on the demo fila, four fichas, two attendants ──
const admin = await api("POST", "/api/v1/identity/token/issue", null, { email: ADMIN, password: SENHA_DEMO });
const t = admin.accessToken;
const normal = (await api("GET", "/api/v1/paf/prioridades", t)).find((p) => p.nome === "Normal");
const estacoes = await api("GET", "/api/v1/paf/estacoes", t);
const guiche1 = estacoes.find((e) => e.tipo === "Guiche");
const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
const filaId = guiche1.filas[0].filaId;
const blocoGuiche = (numero, selecionarFicha) => ({
  emissao: null,
  painel: null,
  guiche: { numero, nomeExibicao: null, ladoSeta: null, respeitarOrdemFilas: false, selecionarFicha },
});
const configurarGuiche1 = (selecionarFicha) =>
  api("PUT", `/api/v1/paf/estacoes/${guiche1.id}`, t, {
    id: guiche1.id,
    nome: guiche1.nome,
    filas: [{ papel: "Guiche", filaId, ordem: 0 }],
    prioridades: [{ prioridadeId: normal.id, ordem: 0 }],
    ramal: guiche1.ramal,
    configuracao: blocoGuiche(1, selecionarFicha),
  });
await configurarGuiche1(false);
const guiche2Id = await api("POST", "/api/v1/paf/estacoes", t, {
  nome: `Guichê 2 F21 ${Date.now().toString().slice(-5)}`,
  tipo: "Guiche",
  filas: [{ papel: "Guiche", filaId, ordem: 0 }],
  prioridades: [{ prioridadeId: normal.id, ordem: 0 }],
  ramal: null,
  configuracao: blocoGuiche(2, true),
});

const unidadeDemo = (await api("GET", "/api/v1/paf/unidades", t)).find((u) => u.nome === "Loja Demo");
for (const email of [ALICE, BOB]) {
  const id = sql(`SELECT "Id" FROM identity."Users" WHERE "TenantId" = '${TENANT}' AND "Email" = '${email}'`);
  await api("PUT", `/api/v1/paf/usuarios/${id}/unidades`, t, {
    usuarioId: "00000000-0000-0000-0000-000000000000",
    unidades: [unidadeDemo.id],
    escopoEmpresa: false,
  });
  // Gerente (PapeisPadrao): Atendente's guichê permissions plus FichasDoDia.View, which the screen needs.
  sql(`INSERT INTO identity."UserRoles" ("UserId", "RoleId", "TenantId")
       SELECT '${id}', r."Id", r."TenantId" FROM identity."Roles" r WHERE r."TenantId" = '${TENANT}' AND r."Name" = 'Gerente'
       ON CONFLICT DO NOTHING`);
  check(`setup: ${email} com o papel Gerente`, sql(`SELECT count(*) FROM identity."UserRoles" ur JOIN identity."Roles" r ON r."Id" = ur."RoleId"
    WHERE ur."UserId" = '${id}' AND r."Name" = 'Gerente'`) === "1");
}

const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, t, {});
const cred = await api("POST", "/api/v1/paf/estacoes/vinculo/resgatar", null, { token: link.token });
const emissao = cred.credenciais.find((c) => c.papel === "Emissao");
const fichas = [];
for (let i = 0; i < 4; i += 1) {
  const ficha = await api("POST", "/api/v1/paf/estacao/emissao/fichas", emissao.accessToken, { filaId, prioridadeId: normal.id }, { "Idempotency-Key": randomUUID() });
  fichas.push({ atendimentoId: ficha.atendimentoId, senha: ficha.senha });
}
const [x1, x2, x3, x4] = fichas;
check("setup: quatro fichas Aguardando", fichas.every((f) => atendimentoNoBanco(f.atendimentoId).startsWith("Aguardando|")), JSON.stringify(fichas));
// The call refuses a ficha younger than five seconds (REQ seção 4, guarda de tempo).
await new Promise((r) => setTimeout(r, 6_000));

async function entrar(contexto, email, estacaoId) {
  const pagina = await contexto.newPage();
  pagina.on("dialog", (d) => void d.dismiss());
  await pagina.goto(`${WEB}/login`);
  await pagina.locator("#tenant").fill(TENANT);
  await pagina.locator("#email").fill(email);
  await pagina.locator("#password").fill(SENHA_DEMO);
  await pagina.locator('button[type="submit"]').click();
  await pagina.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  await pagina.goto(`${WEB}/paf/guiche`);
  const unidade = pagina.getByLabel("Unidade");
  const chamar = pagina.getByRole("button", { name: "Chamar próxima" });
  const selGuiche = pagina.getByLabel("Guichê");
  await ate(async () => (await unidade.isVisible()) || (await selGuiche.isVisible()) || (await chamar.isVisible()));
  if (await unidade.isVisible()) {
    await unidade.selectOption({ label: "Loja Demo" });
    await pagina.getByRole("button", { name: "Usar esta unidade" }).click();
  }
  await ate(async () => (await selGuiche.isVisible()) || (await chamar.isVisible()));
  if (await selGuiche.isVisible()) {
    await selGuiche.selectOption(estacaoId);
    await pagina.getByRole("button", { name: /Usar este guichê/ }).click();
  }
  await chamar.waitFor({ timeout: TIMEOUT });
  return pagina;
}

// The default handler dismisses; a step that must accept installs a one-shot accept before clicking.
function responder(pagina, aceitar) {
  pagina.removeAllListeners("dialog");
  pagina.once("dialog", (d) => {
    pagina.on("dialog", (outro) => void outro.dismiss());
    void (aceitar ? d.accept() : d.dismiss());
  });
}

const linha = (pagina, f) => pagina.locator(`tr[data-atendimento="${f.atendimentoId}"]`);
const botao = (pagina, f) => linha(pagina, f).locator("[data-chamar-esta]");
const estadoNaTela = (pagina, f, estado) =>
  linha(pagina, f).locator(`td[data-estado="${estado}"]`).waitFor({ timeout: TIMEOUT }).then(() => true, () => false);

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const alice = await entrar(await browser.newContext(), ALICE, guiche1.id);

  // ── Option off: the screen lists the fichas but offers no out-of-order call ──
  await alice.goto(`${WEB}/paf/fichas-do-dia`);
  check("sem a opção: as fichas aparecem Aguardando", await estadoNaTela(alice, x1, "Aguardando"));
  await alice.waitForTimeout(1_000);
  check("sem a opção: nenhum Chamar esta", (await alice.locator("[data-chamar-esta]").count()) === 0);
  check("sem a opção: nenhum erro na tela", (await alice.getByRole("alert").count()) === 0);

  await configurarGuiche1(true);
  await alice.reload();
  check("com a opção: Chamar esta aparece", await botao(alice, x1).waitFor({ timeout: TIMEOUT }).then(() => true, () => false));
  const rotulos = await Promise.all(fichas.map((f) => botao(alice, f).textContent()));
  check("posição na chamada: a mais antiga é a 1ª, na ordem de emissão",
    rotulos.join(",") === "Chamar esta (1ª),Chamar esta (2ª),Chamar esta (3ª),Chamar esta (4ª)", rotulos.join(","));

  // ── Guichê free: X3 is called ahead of X1 and X2 ──
  await botao(alice, x3).click();
  check("sem atendimento: X3 chamada na tela", await estadoNaTela(alice, x3, "Chamado"));
  check("sem atendimento: X3 Chamado no guichê 1 no banco", atendimentoNoBanco(x3.atendimentoId).startsWith(`Chamado|${guiche1.id}|`));
  check("furou a fila: X1 segue Aguardando", atendimentoNoBanco(x1.atendimentoId).startsWith("Aguardando||"));
  check("aviso da chamada com a senha", ((await alice.locator("[data-aviso-selecao]").textContent()) ?? "").includes(x3.senha));
  check("X3 chamada não oferece mais Chamar esta", await ate(async () => (await botao(alice, x3).count()) === 0));

  // ── Chamado on the guichê: refusing the question writes nothing ──
  const x3Antes = atendimentoNoBanco(x3.atendimentoId);
  responder(alice, false);
  await botao(alice, x4).click();
  await alice.waitForTimeout(1_000);
  check("recusa: X3 inalterada (estado e Versao)", atendimentoNoBanco(x3.atendimentoId) === x3Antes, x3Antes);
  check("recusa: X4 segue Aguardando", atendimentoNoBanco(x4.atendimentoId).startsWith("Aguardando||"));

  // ── Accepting: X3 is cancelled (no-show) and X4 is called ──
  responder(alice, true);
  await botao(alice, x4).click();
  check("Chamado: X4 chamada na tela", await estadoNaTela(alice, x4, "Chamado"));
  check("Chamado: X3 Cancelado no banco", atendimentoNoBanco(x3.atendimentoId).startsWith("Cancelado|"));
  check("Chamado: X4 Chamado no guichê 1", atendimentoNoBanco(x4.atendimentoId).startsWith(`Chamado|${guiche1.id}|`));

  // ── EmAtendimento: start X4 on the guichê page, then choose X2: X4 closed with its start, X2 called ──
  await alice.goto(`${WEB}/paf/guiche`);
  await alice.getByRole("button", { name: "Iniciar atendimento" }).click();
  check("X4 iniciada", await ate(async () => atendimentoNoBanco(x4.atendimentoId).startsWith("EmAtendimento|")));
  await alice.goto(`${WEB}/paf/fichas-do-dia`);
  await botao(alice, x2).waitFor({ timeout: TIMEOUT });
  responder(alice, true);
  await botao(alice, x2).click();
  check("EmAtendimento: X2 chamada na tela", await estadoNaTela(alice, x2, "Chamado"));
  check("EmAtendimento: X4 Encerrado com IniciadoEm", /^Encerrado\|.*\|true$/.test(atendimentoNoBanco(x4.atendimentoId)), atendimentoNoBanco(x4.atendimentoId));
  check("EmAtendimento: X2 Chamado no guichê 1", atendimentoNoBanco(x2.atendimentoId).startsWith(`Chamado|${guiche1.id}|`));

  // ── Two guichês, same ficha: Bob's list goes stale, Alice calls X1 first, Bob's choice is refused ──
  const bob = await entrar(await browser.newContext(), BOB, guiche2Id);
  await bob.goto(`${WEB}/paf/fichas-do-dia`);
  check("guichê 2: X1 oferecida a Bob como 1ª",
    ((await botao(bob, x1).waitFor({ timeout: TIMEOUT }).then(() => botao(bob, x1).textContent(), () => "")) ?? "").includes("1ª"));
  responder(alice, true);
  await botao(alice, x1).click();
  check("Alice: X1 chamada (X2 cancelada antes)", await estadoNaTela(alice, x1, "Chamado"));
  check("Alice: X2 Cancelado no banco", atendimentoNoBanco(x2.atendimentoId).startsWith("Cancelado|"));
  const x1DaAlice = atendimentoNoBanco(x1.atendimentoId);
  await botao(bob, x1).click();
  check("Bob: vê a mensagem do servidor",
    await bob.getByRole("alert").filter({ hasText: MSG_JA_CHAMADA }).waitFor({ timeout: TIMEOUT }).then(() => true, () => false));
  check("Bob: a lista recarregada mostra X1 Chamado e sem Chamar esta",
    (await estadoNaTela(bob, x1, "Chamado")) && (await ate(async () => (await botao(bob, x1).count()) === 0)));
  check("Bob: nada gravado (X1 igual, guichê 2 sem atendimento)",
    atendimentoNoBanco(x1.atendimentoId) === x1DaAlice &&
      sql(`SELECT count(*) FROM paf."Atendimentos" WHERE "EstacaoId" = '${guiche2Id}'`) === "0",
    x1DaAlice);
  check("um Chamado por ficha: X1 só no guichê 1", x1DaAlice.startsWith(`Chamado|${guiche1.id}|`));
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "OK" : `${falhas} FALHA(S)`);
process.exit(falhas === 0 ? 0 : 1);
