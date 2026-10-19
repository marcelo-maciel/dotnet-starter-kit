// F20 walk against a REAL API (no route mocks): the longest expected senha (5 letters plus the number, 12 characters in
// the column, REQ RF11) on the printed ficha and on the painel highlight and list, at the smallest and largest painel
// font the station cadastro accepts (C04: 8 and 400 px), with the 2-character senha of the seed as regression.
// The senha ABCDE1234567 comes from a real emission: a new fila line with letter ABCDE and initial number 1234567.
//
// Prerequisites: fresh `seed-demo` database, API and Vite up. Run:
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:5518 PAF_SENHA_DEMO=<Seed__DemoPassword> \
//     node tests/e2e/paf-senha-longa.walk.mjs
// or through deploy/paf/walk-ci.sh with PAF_WALK=tests/e2e/paf-senha-longa.walk.mjs.
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
const SENHA_LONGA = "ABCDE1234567";
const LETRA_LONGA = "ABCDE";
const NUMERO_LONGO = 1234567;
const NOME_PRIORIDADE = "Senha longa F20";
// C04 bounds of Painel.TamanhoFontePx (ConfiguracaoDaEstacao.TamanhoMinimoFontePainelPx and TamanhoMaximoPx).
const FONTE_MINIMA_PAINEL = 8;
const FONTE_MAXIMA_PAINEL = 400;

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

// Silent voice; print held until the walk measured the ticket (window.__liberarImpressao).
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
  window.__imprimindo = false;
  window.print = () => {
    window.__imprimindo = true;
    window.__liberarImpressao = () => {
      window.__imprimindo = false;
      window.dispatchEvent(new Event("afterprint"));
    };
  };
};

async function derrubarHub(page) {
  await page.evaluate(() => window.__sockets.filter((s) => s.url.includes("/api/v1/paf/hub")).forEach((s) => s.close()));
}

async function esperarAte(page, fn, arg) {
  return page.waitForFunction(fn, arg, { timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
}

// The text itself, not only the element: a box that shrink-wraps its text passes scrollWidth <= clientWidth even when
// the text runs off the screen, so the rendered glyph rectangles are checked against the viewport too.
function medirTexto(el) {
  const range = document.createRange();
  range.selectNodeContents(el);
  const linhas = [...range.getClientRects()].filter((r) => r.width > 0);
  const r = range.getBoundingClientRect();
  return {
    texto: el.textContent,
    sw: el.scrollWidth,
    cw: el.clientWidth,
    linhas: linhas.length,
    esquerda: r.left,
    direita: r.right,
    viewport: document.documentElement.clientWidth,
    fonte: getComputedStyle(el).fontSize,
  };
}

function contido(m) {
  return m.sw <= m.cw && m.linhas === 1 && m.esquerda >= 0 && m.direita <= m.viewport;
}

const resumo = (m) => `${m.texto} sw=${m.sw} cw=${m.cw} linhas=${m.linhas} x=${Math.round(m.esquerda)}..${Math.round(m.direita)}/${m.viewport} fonte=${m.fonte}`;

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  let admin = (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken;
  const estacoes = await api("GET", "/api/v1/paf/estacoes", admin);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const guicheEstacao = estacoes.find((e) => e.tipo === "Guiche");
  const filaId = totem.filas.find((f) => f.papel === "Emissao").filaId;
  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, admin, {});

  const configurarPainel = async (token, painel) =>
    api("PUT", `/api/v1/paf/estacoes/${totem.id}`, token, {
      nome: totem.nome,
      filas: totem.filas,
      prioridades: totem.prioridades,
      ramal: totem.ramal,
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
        painel,
      },
    });
  const painelSemCor = (fonte) => ({
    tempoExibicaoChamadaSegundos: 2,
    mostrarDataHora: false,
    tamanhoFontePx: fonte,
    corFundo: null,
    corFonteFilas: null,
    corFonteChamada: null,
    corFonteAlternada: null,
    modo: "Informativo",
  });
  // With the cadastro colors the highlight box gets a 0.1em border: at 400 px that is 40 px a side less room.
  const painelComCor = (fonte) => ({
    ...painelSemCor(fonte),
    corFundo: "#000033",
    corFonteFilas: "#FFFFFF",
    corFonteChamada: "#FFFF00",
    corFonteAlternada: "#00FFFF",
  });

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

  const emitir = async (botao) => {
    await botao.click();
    check("totem: impressão iniciada", await esperarAte(pTotem, () => window.__imprimindo === true));
    const ficha = await pTotem.locator("[data-ficha-impressa] [data-senha]").evaluate(medirTexto);
    const folha = await pTotem.locator("[data-ficha-impressa]").evaluate((el) => ({ sw: el.scrollWidth, cw: el.clientWidth }));
    await pTotem.evaluate(() => window.__liberarImpressao());
    const senha = await pTotem.locator("[data-senha-emitida]").textContent({ timeout: TIMEOUT });
    return { ficha, folha, senha };
  };

  // ── Regression: the seed's 2-character senha ──
  const curta = await emitir(pTotem.locator("[data-botao-emissao]").first());
  check("setup: senha curta do seed tem 2 caracteres", curta.senha.length === 2, curta.senha);
  check("ficha curta: senha numa linha dentro da bobina", contido({ ...curta.ficha, esquerda: 0, direita: 0 }), resumo(curta.ficha));
  check("ficha curta: fonte cheia de 55px", curta.ficha.fonte === "55px", curta.ficha.fonte);

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
  const chamar = pGuiche.getByRole("button", { name: "Chamar próxima" });
  await chamar.click();
  await pGuiche.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });
  // The guichê login of the same demo user ended the admin's API session (one session per user).
  admin = (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken;

  const senhaPainel = pPainel.locator("[data-senha-atual]");
  const esperarSenha = (senha) => esperarAte(pPainel, (s) => document.querySelector("[data-senha-atual]")?.textContent === s, senha);
  const aplicarFonte = async (painel) => {
    await configurarPainel(admin, painel);
    await derrubarHub(pPainel);
    const alvo = `${painel.tamanhoFontePx}px`;
    return esperarAte(pPainel, (px) => getComputedStyle(document.querySelector("main")).fontSize === px, alvo);
  };

  check("painel curto: senha chamada no destaque", await esperarSenha(curta.senha));
  const curtaPadrao = await senhaPainel.evaluate(medirTexto);
  check("painel curto, fonte padrão: contida", contido(curtaPadrao), resumo(curtaPadrao));
  check("painel curto, fonte padrão: não encolhe (10em de 16px)", curtaPadrao.fonte === "160px", curtaPadrao.fonte);

  check("painel: fonte mínima aplicada", await aplicarFonte(painelSemCor(FONTE_MINIMA_PAINEL)));
  const curtaMin = await senhaPainel.evaluate(medirTexto);
  check("painel curto, fonte 8: contida", contido(curtaMin), resumo(curtaMin));
  check("painel curto, fonte 8: não encolhe (10em de 8px)", curtaMin.fonte === "80px", curtaMin.fonte);

  check("painel: fonte máxima aplicada", await aplicarFonte(painelComCor(FONTE_MAXIMA_PAINEL)));
  const curtaMax = await senhaPainel.evaluate(medirTexto);
  check("painel curto, fonte 400: contida", contido(curtaMax), resumo(curtaMax));

  await pGuiche.getByRole("button", { name: "Iniciar atendimento" }).click();
  await pGuiche.getByText("Em atendimento").waitFor({ timeout: TIMEOUT });
  await pGuiche.getByRole("button", { name: "Encerrar atendimento" }).click();
  await pGuiche.getByText("Nenhum atendimento em curso").waitFor({ timeout: TIMEOUT });

  // ── The longest senha: letter of 5 and a 7-digit number, from a real emission ──
  const prioridadeId = await api("POST", "/api/v1/paf/prioridades", admin, { nome: NOME_PRIORIDADE, valor: 0 });
  await api("POST", `/api/v1/paf/filas/${filaId}/prioridades`, admin, {
    filaId,
    prioridadeId,
    letra: LETRA_LONGA,
    numeroInicial: NUMERO_LONGO,
    ordem: 1,
    corBotao: null,
  });
  // A combination without a guichê that serves it leaves the totem (RF02): the seed guichê takes the new prioridade.
  await api("PUT", `/api/v1/paf/estacoes/${guicheEstacao.id}`, admin, {
    nome: guicheEstacao.nome,
    filas: guicheEstacao.filas,
    prioridades: [...guicheEstacao.prioridades, { prioridadeId, ordem: guicheEstacao.prioridades.length }],
    ramal: guicheEstacao.ramal,
    configuracao: guicheEstacao.configuracao ?? {
      emissao: null,
      guiche: { numero: 1, nomeExibicao: null, ladoSeta: null, respeitarOrdemFilas: false, selecionarFicha: false },
      painel: null,
    },
  });
  await derrubarHub(pTotem);
  const botaoLongo = pTotem.locator("[data-botao-emissao]", { hasText: NOME_PRIORIDADE });
  check("setup: botão da prioridade nova no totem", await botaoLongo.waitFor({ timeout: TIMEOUT }).then(() => true, () => false));

  const longa = await emitir(botaoLongo);
  check("setup: senha longa emitida de verdade", longa.senha === SENHA_LONGA, longa.senha);
  check("ficha longa: senha numa linha dentro da bobina", contido({ ...longa.ficha, esquerda: 0, direita: 0 }), resumo(longa.ficha));
  check("ficha longa: a ficha inteira cabe na bobina", longa.folha.sw <= longa.folha.cw, `${longa.folha.sw} <= ${longa.folha.cw}`);
  check("ficha longa: senha legível (>= 24px)", Number.parseFloat(longa.ficha.fonte) >= 24, longa.ficha.fonte);

  await chamar.click();
  await pGuiche.locator("[data-senha-guiche]", { hasText: SENHA_LONGA }).waitFor({ timeout: TIMEOUT });
  check("painel longo: senha chamada no destaque", await esperarSenha(SENHA_LONGA));

  const medirLonga = async (rotulo) => {
    const m = await senhaPainel.evaluate(medirTexto);
    check(`painel longo, ${rotulo}: destaque contido`, contido(m), resumo(m));
    const item = await pPainel
      .locator("[data-atendimento]", { hasText: SENHA_LONGA })
      .evaluate((el) => ({ sw: el.scrollWidth, cw: el.clientWidth, direita: el.getBoundingClientRect().right, vw: document.documentElement.clientWidth }));
    check(`painel longo, ${rotulo}: item da lista contido`, item.sw <= item.cw && item.direita <= item.vw, JSON.stringify(item));
    const pagina = await pPainel.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
    check(`painel longo, ${rotulo}: sem rolagem horizontal da página na senha`, m.direita <= pagina.cw, JSON.stringify(pagina));
    return m;
  };

  await medirLonga("fonte 400 com cores");
  check("painel: fonte mínima aplicada de novo", await aplicarFonte(painelSemCor(FONTE_MINIMA_PAINEL)));
  const longaMin = await medirLonga("fonte 8");
  check("painel longo, fonte 8: cabe sem encolher (10em de 8px)", longaMin.fonte === "80px", longaMin.fonte);
  check("painel: fonte padrão (16px) aplicada", await aplicarFonte(painelSemCor(16)));
  await medirLonga("fonte 16");
  await pPainel.setViewportSize({ width: 800, height: 900 });
  check("painel: janela estreita refaz a contenção", await esperarAte(pPainel, () => {
    const el = document.querySelector("[data-senha-atual]");
    return el !== null && el.scrollWidth <= el.clientWidth;
  }));
  await medirLonga("janela de 800px");

  await gCtx.close();
  await ctx.close();
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.stack : String(erro));
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
