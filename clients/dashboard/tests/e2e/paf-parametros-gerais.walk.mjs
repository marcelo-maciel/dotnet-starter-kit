// C20 walk against a REAL API (no route mocks): RF06 Geral on the screens. The call message shows under the senha on
// the painel and is spoken when "chamar ficha por voz" asks for it; the voice offers no "off" (D29, RNF19); "voltar ao
// herdado" brings the plain call back; the guichê shows the waiting total, and the count per fila once "exibir
// informação detalhada de fila" is on (D30).
//
// Prerequisites: fresh `seed-demo` database, API and Vite up. Run:
//   PAF_API=http://localhost:5430 PAF_WEB=http://localhost:5530 node tests/e2e/paf-parametros-gerais.walk.mjs
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
// Guichê count re-read period (10 s) plus margin.
const RELEITURA = 25_000;
const MENSAGEM = "Dirija-se ao guichê indicado";

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

function visivel(locator, timeout) {
  return locator.waitFor({ timeout }).then(
    () => true,
    () => false,
  );
}

async function ok(method, path, token, body) {
  const headers = { tenant: TENANT };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const r = await fetch(API + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(TIMEOUT),
  });
  const texto = await r.text();
  if (r.status >= 300) throw new Error(`${method} ${path} -> ${r.status} ${texto}`);
  return texto ? JSON.parse(texto) : null;
}

function sub(token) {
  return JSON.parse(Buffer.from(token.split(".")[1], "base64url").toString()).sub;
}

function falada(senha) {
  return `senha ${senha.replace(/^([A-Z]+)(\d+)$/, (_, l, n) => `${l} ${Number(n)}`)}`;
}

// speechSynthesis only records the text; print() fires afterprint like --kiosk-printing would.
const STUBS = () => {
  window.__falas = [];
  window.__impressoes = [];
  const voz = { name: "Stub pt-BR", lang: "pt-BR", localService: true, default: true, voiceURI: "stub-pt-br" };
  const sintese = {
    getVoices: () => [voz],
    speak: (u) => {
      window.__falas.push(u.text);
      setTimeout(() => u.onend?.(new Event("end")), 30);
    },
    cancel: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  Object.defineProperty(window, "speechSynthesis", { value: sintese, configurable: true });
  window.SpeechSynthesisUtterance = class {
    constructor(text) {
      this.text = text;
      this.lang = "";
      this.voice = null;
      this.onend = null;
      this.onerror = null;
    }
  };
  window.print = () => {
    window.__impressoes.push(document.querySelector("[data-ficha-impressa] [data-senha]")?.textContent ?? null);
    setTimeout(() => window.dispatchEvent(new Event("afterprint")), 20);
  };
};

async function entrar(contexto) {
  const p = await contexto.newPage();
  await p.goto(`${WEB}/login`);
  await p.locator("#tenant").fill(TENANT);
  await p.locator("#email").fill(EMAIL);
  await p.locator("#password").fill(SENHA_DEMO);
  await p.locator('button[type="submit"]').click();
  await p.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  return p;
}

function linha(p, chave) {
  return p.locator(`[data-parametro="${chave}"]`);
}

async function salvar(p, chave, valor) {
  await linha(p, chave).getByRole("button", { name: "Alterar" }).click();
  const campo = linha(p, chave).getByLabel(/Novo valor de/);
  if ((await campo.evaluate((e) => e.tagName)) === "SELECT") await campo.selectOption(valor);
  else await campo.fill(valor);
  await linha(p, chave).getByRole("button", { name: "Salvar nesta unidade" }).click();
  await p.locator(`[data-parametro="${chave}"] [data-selo="Unidade"]`).waitFor({ timeout: TIMEOUT });
}

async function emitir(pTotem) {
  const antes = await pTotem.evaluate(() => window.__impressoes.length);
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().click();
  await pTotem.waitForFunction((n) => window.__impressoes.length > n, antes, { timeout: TIMEOUT });
  await pTotem.locator("[data-senha-emitida]").waitFor({ state: "detached", timeout: TIMEOUT });
  return pTotem.evaluate(() => window.__impressoes.at(-1));
}

async function chamar(pGuiche) {
  await pGuiche.getByRole("button", { name: "Chamar próxima" }).click();
  await pGuiche.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });
  return (await pGuiche.locator("[data-senha-guiche]").textContent()).trim();
}

async function encerrar(pGuiche) {
  await pGuiche.getByRole("button", { name: "Iniciar atendimento" }).click();
  await pGuiche.locator('[data-estado-guiche]:has-text("Em atendimento")').waitFor({ timeout: TIMEOUT });
  await pGuiche.getByRole("button", { name: "Encerrar atendimento" }).click();
  await pGuiche.getByText(/Nenhum atendimento em curso/).waitFor({ timeout: TIMEOUT });
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── Setup: the demo admin as company admin (RF23), a station link for the demo totem + painel ──
  let admin = await ok("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const unidades = await ok("GET", "/api/v1/paf/unidades", admin.accessToken);
  const loja = unidades.find((u) => u.nome === "Loja Demo");
  await ok("PUT", `/api/v1/paf/usuarios/${sub(admin.accessToken)}/unidades`, admin.accessToken, {
    usuarioId: sub(admin.accessToken),
    unidades: [loja.id],
    escopoEmpresa: true,
  });
  admin = await ok("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const estacoes = await ok("GET", "/api/v1/paf/estacoes", admin.accessToken);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const link = await ok("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, admin.accessToken, {});

  // ── Parameters screen: the three Geral keys, the voice offers no "off" ──
  const pessoaCtx = await browser.newContext();
  const pPar = await entrar(pessoaCtx);
  await pPar.goto(`${WEB}/paf/parametros`);
  await linha(pPar, "mensagemAoChamar").waitFor({ timeout: TIMEOUT });
  check(
    "tela: Geral lista mensagem, voz e informação detalhada com rótulo",
    (await linha(pPar, "mensagemAoChamar").textContent()).includes("Mensagem exibida ao chamar a ficha") &&
      (await linha(pPar, "chamarFichaPorVoz").textContent()).includes("Chamar ficha por voz") &&
      (await linha(pPar, "exibirInformacaoDetalhadaFila").textContent()).includes("informação detalhada de fila"),
  );
  await linha(pPar, "chamarFichaPorVoz").getByRole("button", { name: "Alterar" }).click();
  const opcoesVoz = await linha(pPar, "chamarFichaPorVoz").getByLabel(/Novo valor de/).locator("option").allTextContents();
  check("tela: a voz só escolhe o que fala, sem opção de desligar (D29)", opcoesVoz.length === 2 && !opcoesVoz.some((o) => /Não|desl/i.test(o)), opcoesVoz.join(" | "));
  await linha(pPar, "chamarFichaPorVoz").getByRole("button", { name: "Cancelar" }).click();
  await salvar(pPar, "mensagemAoChamar", MENSAGEM);
  await salvar(pPar, "chamarFichaPorVoz", "senhaEMensagem");

  // ── Stations: totem and painel with the same link ──
  const estacaoCtx = await browser.newContext();
  await estacaoCtx.addInitScript(STUBS);
  const pVinculo = await estacaoCtx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o painel" }).waitFor({ timeout: TIMEOUT });
  await pVinculo.close();
  const pTotem = await estacaoCtx.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().waitFor({ timeout: TIMEOUT });
  const pPainel = await estacaoCtx.newPage();
  await pPainel.goto(`${WEB}/paf/painel`);
  await pPainel.getByRole("button", { name: "Iniciar painel" }).click();
  await pPainel.locator('[data-conexao="conectado"]').waitFor({ timeout: TIMEOUT });

  // ── Guichê: total only while the detail is off ──
  const pGuiche = await pessoaCtx.newPage();
  await pGuiche.goto(`${WEB}/paf/guiche`);
  await pGuiche.getByLabel("Guichê").waitFor({ timeout: TIMEOUT });
  await pGuiche.getByLabel("Guichê").selectOption({ label: "Guichê 1" });
  await pGuiche.getByRole("button", { name: "Usar este guichê" }).click();
  await pGuiche.locator("[data-aguardando-total]").waitFor({ timeout: TIMEOUT });
  const inicial = Number(await pGuiche.locator("[data-aguardando-total]").getAttribute("data-aguardando-total"));
  check("guichê: mostra o total aguardando", Number.isInteger(inicial), String(inicial));

  const senha1 = await emitir(pTotem);
  check(
    "guichê: depois da guarda de 5 s a ficha entra no total, sem detalhe por fila",
    (await visivel(pGuiche.locator(`[data-aguardando-total="${inicial + 1}"]`), RELEITURA)) &&
      (await pGuiche.locator("[data-aguardando-fila]").count()) === 0,
  );
  await salvar(pPar, "exibirInformacaoDetalhadaFila", "true");
  check(
    "guichê: ligado, mostra a quantidade por fila da estação",
    await visivel(pGuiche.locator(`[data-aguardando-fila="Atendimento"]:has-text("Atendimento: ${inicial + 1}")`), RELEITURA),
  );

  // ── Call with the message: text under the senha and spoken after it ──
  const chamada1 = await chamar(pGuiche);
  check("guichê: chamou a ficha emitida", inicial > 0 || chamada1 === senha1, `${chamada1} / ${senha1}`);
  check("guichê: a própria chamada relê a contagem na hora", await visivel(pGuiche.locator(`[data-aguardando-total="${inicial}"]`), 3_000));
  check(
    "painel: mensagem ao chamar junto da senha em destaque",
    await visivel(pPainel.locator(`[data-destaque="sim"]:has([data-senha-atual]:has-text("${chamada1}")) [data-mensagem-chamada]:has-text("${MENSAGEM}")`), TIMEOUT),
  );
  const esperada1 = `${falada(chamada1)}. ${MENSAGEM}`;
  await pPainel.waitForFunction((t) => window.__falas.includes(t), esperada1, { timeout: TIMEOUT }).catch(() => {});
  check("painel: fala a senha e a mensagem (voz e texto, RNF19)", (await pPainel.evaluate(() => window.__falas)).includes(esperada1), JSON.stringify(await pPainel.evaluate(() => window.__falas)));
  await encerrar(pGuiche);

  // ── DELETE: back to the default, the next call has no message and speaks the senha only ──
  await linha(pPar, "mensagemAoChamar").getByRole("button", { name: "Voltar ao herdado" }).click();
  await pPar.locator('[data-parametro="mensagemAoChamar"] [data-selo="Fabrica"]').waitFor({ timeout: TIMEOUT });
  // The emission's FilaAtualizada makes the painel re-read its configuration before the next call.
  const senha2 = await emitir(pTotem);
  await pPainel.locator('[data-destaque="nao"]').waitFor({ timeout: 30_000 });
  await pGuiche.waitForTimeout(5_500);
  const chamada2 = await chamar(pGuiche);
  check("guichê: chamou a segunda ficha", inicial > 0 || chamada2 === senha2, `${chamada2} / ${senha2}`);
  await pPainel.locator(`[data-destaque="sim"]:has([data-senha-atual]:has-text("${chamada2}"))`).waitFor({ timeout: TIMEOUT });
  check("painel: depois do DELETE a chamada sai sem mensagem", (await pPainel.locator("[data-mensagem-chamada]").count()) === 0);
  await pPainel.waitForFunction((t) => window.__falas.includes(t), falada(chamada2), { timeout: TIMEOUT }).catch(() => {});
  const falas = await pPainel.evaluate(() => window.__falas);
  check("painel: e fala só a senha, a voz continua ligada", falas.at(-1) === falada(chamada2), JSON.stringify(falas));
  await encerrar(pGuiche);
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
