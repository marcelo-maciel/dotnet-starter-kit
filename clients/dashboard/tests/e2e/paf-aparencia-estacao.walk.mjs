// C18 walk against a REAL API (no route mocks): the appearance the station cadastro (C04) and the fila cadastro (C09)
// write shows on the totem and on the painel, without reloading the screens.
//
// Run (API and Vite dev server already up, fresh `seed-demo` database):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:5518 node tests/e2e/paf-aparencia-estacao.walk.mjs
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
const SENHA_DEMO = process.env.PAF_SENHA_DEMO ?? "Password123!";
const TIMEOUT = 15_000;

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

// Same stubs as the first-slice walk: sockets reachable to force a reconnection, silent voice, instant print.
const STUBS = () => {
  window.__sockets = [];
  const SocketNativo = window.WebSocket;
  window.WebSocket = class extends SocketNativo {
    constructor(...args) {
      super(...args);
      window.__sockets.push(this);
    }
  };
  const voz = { name: "Stub pt-BR", lang: "pt-BR", localService: true, default: true, voiceURI: "stub-pt-br" };
  Object.defineProperty(window, "speechSynthesis", {
    value: {
      getVoices: () => [voz],
      speak: (u) => setTimeout(() => u.onend?.(new Event("end")), 30),
      cancel: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
    },
    configurable: true,
  });
  window.SpeechSynthesisUtterance = class {
    constructor(text) {
      this.text = text;
      this.voice = null;
      this.onend = null;
      this.onerror = null;
    }
  };
  // Hold the print long enough for the walk to read the touched button's color during the cycle.
  window.print = () => setTimeout(() => window.dispatchEvent(new Event("afterprint")), 1_500);
};

// A network drop: the hub reconnects and the screen re-reads its configuration, with no page reload.
async function derrubarHub(page) {
  await page.evaluate(() => window.__sockets.filter((s) => s.url.includes("/api/v1/paf/hub")).forEach((s) => s.close()));
}

async function estiloBotao(page) {
  return page.locator("[data-botao-emissao]").first().evaluate((el) => {
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    const desc = getComputedStyle(el.querySelector("[data-descricao]"));
    return { fundo: cs.backgroundColor, cor: cs.color, desc: desc.color, fonte: cs.fontSize, familia: cs.fontFamily, w: r.width, h: r.height };
  });
}

async function esperarAte(page, fn, arg) {
  return page.waitForFunction(fn, arg, { timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const admin = (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken;
  const estacoes = await api("GET", "/api/v1/paf/estacoes", admin);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const filaId = totem.filas.find((f) => f.papel === "Emissao").filaId;
  const fila = (await api("GET", "/api/v1/paf/filas", admin)).find((f) => f.id === filaId);
  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, admin, {});
  check("setup: estação do seed sem configuração gravada", totem.configuracao === null, JSON.stringify(totem.configuracao));

  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await ctx.addInitScript(STUBS);
  const pVinculo = await ctx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  await pVinculo.close();

  const pTotem = await ctx.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  await pTotem.locator("[data-botao-emissao]").first().waitFor({ timeout: TIMEOUT });
  const pPainel = await ctx.newPage();
  await pPainel.goto(`${WEB}/paf/painel`);
  await pPainel.getByRole("button", { name: "Iniciar painel" }).click();

  // ── Station without configuration: today's appearance ──
  const base = await estiloBotao(pTotem);
  check("sem configuração: título padrão", (await pTotem.locator("h1").textContent()) === "Retire sua senha");
  check(
    "sem configuração: botão sem estilo inline (classes padrão)",
    (await pTotem.locator("[data-botao-emissao]").first().getAttribute("style")) === null ||
      !(await pTotem.locator("[data-botao-emissao]").first().getAttribute("style")).includes("background"),
  );
  check("sem configuração: alvo do botão >= 44 x 44", base.w >= 44 && base.h >= 44, `${base.w}x${base.h}`);
  const painelBase = await pPainel.locator("main").evaluate((el) => ({ fundo: getComputedStyle(el).backgroundColor, fonte: getComputedStyle(el).fontSize }));
  check("sem configuração: painel sem data e hora", (await pPainel.locator("[data-data-hora]").count()) === 0);
  check("sem configuração: painel lista as últimas chamadas", await pPainel.getByRole("region", { name: "Últimas chamadas" }).isVisible());
  check("sem configuração: fonte base do painel 16px", painelBase.fonte === "16px", painelBase.fonte);

  // ── Cadastro writes the station blocks; the open screens apply them on reconnection ──
  const blocos = (emissao, painel) => ({
    nome: totem.nome,
    filas: totem.filas,
    prioridades: totem.prioridades,
    ramal: totem.ramal,
    configuracao: { emissao, guiche: null, painel },
  });
  const emissao = {
    titulo: "Toque e retire sua senha",
    tamanhoFontePx: 32,
    tamanhoBotaoPx: 150,
    corFundoBotao: "#003366",
    corFonteBotao: "#FFFFFF",
    corDescricaoBotao: "#FFFF00",
    prazoCicloEmissaoSegundos: null,
  };
  const painel = {
    tempoExibicaoChamadaSegundos: 3,
    mostrarDataHora: true,
    tamanhoFontePx: 20,
    corFundo: "#000033",
    corFonteFilas: "#FFFFFF",
    corFonteChamada: "#FFFF00",
    corFonteAlternada: "#00FFFF",
    modo: "Normal",
  };
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}`, admin, blocos(emissao, painel));
  await derrubarHub(pTotem);
  await derrubarHub(pPainel);

  check(
    "totem: título gravado aparece sem recarregar",
    await esperarAte(pTotem, () => document.querySelector("h1")?.textContent === "Toque e retire sua senha"),
  );
  const est = await estiloBotao(pTotem);
  check("totem: cor de fundo do botão computada", est.fundo === "rgb(0, 51, 102)", est.fundo);
  check("totem: cor da fonte do botão computada", est.cor === "rgb(255, 255, 255)", est.cor);
  check("totem: cor da descrição computada", est.desc === "rgb(255, 255, 0)", est.desc);
  check("totem: fonte do botão", est.fonte === "32px", est.fonte);
  check("totem: tamanho do botão >= 150 e alvo >= 44 x 44 (getBoundingClientRect)", est.w >= 150 && est.h >= 150 && est.w >= 44 && est.h >= 44, `${est.w}x${est.h}`);

  check(
    "painel: fundo gravado aplicado sem recarregar",
    await esperarAte(pPainel, () => getComputedStyle(document.querySelector("main")).backgroundColor === "rgb(0, 0, 51)"),
  );
  const pnl = await pPainel.locator("main").evaluate((el) => ({ cor: getComputedStyle(el).color, fonte: getComputedStyle(el).fontSize }));
  check("painel: cor da fonte das filas", pnl.cor === "rgb(255, 255, 255)", pnl.cor);
  check("painel: fonte base gravada", pnl.fonte === "20px", pnl.fonte);
  const dataHora = pPainel.locator("[data-data-hora]");
  check("painel: data e hora ligada aparece", (await dataHora.count()) === 1 && /^\d{2}\/\d{2}\/\d{4}, \d{2}:\d{2}$/.test((await dataHora.textContent()) ?? ""), await dataHora.textContent().catch(() => null));
  check("painel: modo Normal esconde as últimas chamadas", (await pPainel.getByRole("region", { name: "Últimas chamadas" }).count()) === 0);

  // ── Per-fila colors (C09) win over the station's, as a whole pair ──
  await api("PUT", `/api/v1/paf/filas/${fila.id}`, admin, {
    nome: fila.nome,
    nomeExibicao: fila.nomeExibicao,
    horaInicial: fila.horaInicial,
    horaFinal: fila.horaFinal,
    setorId: fila.setorId,
    aparencia: { fonte: "Georgia", corFonte: "#000000", corBotaoSelecionado: "#FFFFFF" },
  });
  for (const linha of fila.prioridades) {
    await api("PUT", `/api/v1/paf/filas/${fila.id}/prioridades/${linha.prioridadeId}`, admin, {
      letra: linha.letra,
      numeroInicial: linha.numeroInicial,
      ordem: linha.ordem,
      corBotao: "#FFCC00",
    });
  }
  await derrubarHub(pTotem);
  check(
    "totem: cor por prioridade aplicada sem recarregar",
    await esperarAte(pTotem, () => getComputedStyle(document.querySelector("[data-botao-emissao]")).backgroundColor === "rgb(255, 204, 0)"),
  );
  const daFila = await estiloBotao(pTotem);
  check("totem: texto com a cor de fonte da fila (par inteiro)", daFila.cor === "rgb(0, 0, 0)" && daFila.desc === "rgb(0, 0, 0)", `${daFila.cor} / ${daFila.desc}`);
  check("totem: fonte da fila aplicada", daFila.familia.includes("Georgia"), daFila.familia);

  // Touching a button: it shows the fila's "selecionada" color during the emission cycle.
  await pTotem.locator("[data-botao-emissao]").first().click();
  const selecionado = await esperarAte(pTotem, () => {
    const el = document.querySelector('[data-selecionado="sim"]');
    return el !== null && getComputedStyle(el).backgroundColor === "rgb(255, 255, 255)";
  });
  check("totem: botão tocado com a cor de fila selecionada", selecionado);
  const senhaEmitida = await pTotem.locator("[data-senha-emitida]").textContent({ timeout: TIMEOUT });

  // ── Guichê calls the senha: the painel highlight alternates the call colors ──
  const gCtx = await browser.newContext();
  const pGuiche = await gCtx.newPage();
  await pGuiche.goto(`${WEB}/login`);
  await pGuiche.locator("#tenant").fill(TENANT);
  await pGuiche.locator("#email").fill(EMAIL);
  await pGuiche.locator("#password").fill(SENHA_DEMO);
  await pGuiche.locator('button[type="submit"]').click();
  await pGuiche.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  await pGuiche.goto(`${WEB}/paf/guiche`);
  await pGuiche.getByLabel("Guichê").waitFor({ timeout: TIMEOUT });
  await pGuiche.getByRole("button", { name: "Trocar" }).click();
  await pGuiche.getByLabel("Unidade").selectOption({ label: "Loja Demo" });
  await pGuiche.getByRole("button", { name: "Usar esta unidade" }).click();
  await pGuiche.getByLabel("Guichê").selectOption({ label: "Guichê 1" });
  await pGuiche.getByRole("button", { name: "Usar este guichê" }).click();
  await pGuiche.getByRole("button", { name: "Chamar próxima" }).click();
  await pGuiche.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });

  check("painel: senha chamada em destaque", await esperarAte(pPainel, () => document.querySelector('[data-destaque="sim"]') !== null));
  const cores = await pPainel.evaluate(async () => {
    const vistas = new Set();
    for (let i = 0; i < 16; i += 1) {
      const el = document.querySelector('[data-destaque="sim"]');
      if (el) vistas.add(getComputedStyle(el).color);
      await new Promise((r) => setTimeout(r, 100));
    }
    return [...vistas];
  });
  check(
    "painel: chamada alterna entre a cor da chamada e a alternada",
    cores.includes("rgb(255, 255, 0)") && cores.includes("rgb(0, 255, 255)") && cores.length === 2,
    cores.join(" | "),
  );
  const senhaPainel = await pPainel.locator("[data-senha-atual]").textContent();
  check("painel: a senha chamada é a emitida", senhaPainel === senhaEmitida, `${senhaPainel} vs ${senhaEmitida}`);
  const fonteSenha = await pPainel.locator("[data-senha-atual]").evaluate((el) => getComputedStyle(el).fontSize);
  check("painel: senha escala com a fonte base (10em de 20px)", fonteSenha === "200px", fonteSenha);
  check(
    "painel: fora do destaque fica na cor da chamada, sem alternar",
    await esperarAte(pPainel, () => {
      const el = document.querySelector('[data-destaque="nao"]');
      return el !== null && getComputedStyle(el).color === "rgb(255, 255, 0)";
    }),
  );

  // ── Cadastro turns date/time off and goes back to Informativo: applied live ──
  // The guichê login of the same demo user ended the admin's API session (one session per user): issue a new token.
  const admin2 = (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken;
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}`, admin2, blocos(emissao, { ...painel, mostrarDataHora: false, modo: "Informativo" }));
  await derrubarHub(pPainel);
  check("painel: data e hora desligada some", await esperarAte(pPainel, () => document.querySelector("[data-data-hora]") === null));
  check("painel: modo Informativo mostra as últimas chamadas", await esperarAte(pPainel, () => document.querySelector('section[aria-label="Últimas chamadas"]') !== null));
  check("sem recarregar: a mesma página do painel desde o início", (await pPainel.evaluate(() => performance.getEntriesByType("navigation").length)) === 1);

  await gCtx.close();
  await ctx.close();
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.stack : String(erro));
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
