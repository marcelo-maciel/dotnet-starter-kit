// PAF first slice walk against a REAL API (no route mocks except the one marked NEGATIVE below):
// station link → totem emission + print → guichê call/recall/start/finish/cancel → painel live + reconnect.
//
// Run (API and Vite dev server already up, fresh `seed-demo` database):
//   PAF_API=http://localhost:5391 PAF_WEB=http://localhost:5184 node tests/e2e/paf-primeira-fatia.walk.mjs
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

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

// isVisible() does not wait; this does, and turns a timeout into false so the check reports it.
function visivel(locator) {
  return locator.waitFor({ timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
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

// speechSynthesis is never audible in automated runs: speak() only records the text.
// print() records the ticket geometry and fires afterprint like --kiosk-printing would.
const STUBS = () => {
  window.__falas = [];
  // Open sockets, so the walk can drop the hub connection the way a network failure would.
  window.__sockets = [];
  const SocketNativo = window.WebSocket;
  window.WebSocket = class extends SocketNativo {
    constructor(...args) {
      super(...args);
      window.__sockets.push(this);
    }
  };
  window.__impressoes = [];
  const voz = { name: "Stub pt-BR", lang: "pt-BR", localService: true, default: true, voiceURI: "stub-pt-br" };
  const sintese = {
    getVoices: () => [voz],
    speak: (u) => {
      window.__falas.push({ texto: u.text, voz: u.voice?.name ?? null, em: performance.now() });
      setTimeout(() => u.onend?.(new Event("end")), 30);
    },
    cancel: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  Object.defineProperty(window, "speechSynthesis", { value: sintese, configurable: true });
  // The real utterance refuses a voice that is not a native SpeechSynthesisVoice, like the stub voice.
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
    const ficha = document.querySelector("[data-ficha-impressa]");
    const senha = ficha?.querySelector("[data-senha]");
    window.__impressoes.push({
      pagina: document.getElementById("paf-ficha-pagina")?.textContent ?? null,
      larguraPx: ficha?.getBoundingClientRect().width ?? 0,
      senha: senha?.textContent ?? null,
      fontePx: senha ? parseFloat(senha.style.fontSize) : 0,
      senhaCabe: senha ? senha.scrollWidth <= senha.clientWidth : false,
      temQr: !!ficha?.querySelector("svg"),
    });
    setTimeout(() => window.dispatchEvent(new Event("afterprint")), 20);
  };
};

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── Setup through the admin API: ids for the checks and a fresh station link ──
  const admin = await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const estacoes = await api("GET", "/api/v1/paf/estacoes", admin.accessToken);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const filas = await api("GET", "/api/v1/paf/filas", admin.accessToken);
  const prioridades = await api("GET", "/api/v1/paf/prioridades", admin.accessToken);
  const filaId = totem.filas.find((f) => f.papel === "Emissao").filaId;
  const fila = filas.find((f) => f.id === filaId);
  const prioridade = prioridades[0];
  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, admin.accessToken, {});

  // ── Link redemption ──
  const estacaoCtx = await browser.newContext();
  await estacaoCtx.addInitScript(STUBS);
  let pVinculo = await estacaoCtx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${TENANT}`);
  check("vinculo: link sem token recusado", await visivel(pVinculo.getByText("Link incompleto")));

  // A fresh page: in the same tab only the fragment would change, which is not a navigation.
  await pVinculo.close();
  pVinculo = await estacaoCtx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  check("vinculo: oferece totem e painel", await pVinculo.getByRole("link", { name: "Abrir o painel" }).isVisible());
  check("vinculo: token apagado da barra de endereço", !pVinculo.url().includes("token="), pVinculo.url());
  const chaves = await pVinculo.evaluate(() => Object.keys(localStorage));
  check(
    "vinculo: credenciais em chaves próprias, fora de fsh.dashboard.",
    chaves.includes("paf.estacao.emissao") && chaves.includes("paf.estacao.painel") && !chaves.some((k) => k.startsWith("fsh.dashboard.")),
    chaves.join(","),
  );
  const pReuso = await estacaoCtx.newPage();
  await pReuso.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  check("vinculo: link de uso único recusado na segunda vez", await visivel(pReuso.getByText("Link inválido")));
  await pReuso.close();

  // ── Totem against the real configuration: buttons come from the station's emission filas ──
  const pTotem = await estacaoCtx.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  check(
    "totem: botões vêm da configuração real",
    await visivel(pTotem.getByRole("button", { name: new RegExp(fila.nome) })),
  );

  // Idempotency: the first emission reaches the server but the answer is dropped (network error on
  // the totem); the second tap must reuse the key and get the SAME ficha back.
  const chavesEnviadas = [];
  let derrubar = true;
  await estacaoCtx.route("**/api/v1/paf/estacao/emissao/fichas", async (route) => {
    chavesEnviadas.push(route.request().headers()["idempotency-key"]);
    const real = await route.fetch();
    if (derrubar) {
      derrubar = false;
      await route.abort("failed");
      return;
    }
    await route.fulfill({ response: real });
  });

  await pTotem.reload();
  const botao = pTotem.getByRole("button", { name: new RegExp(fila.nome) }).first();
  await botao.click();
  check("totem: falha de rede pede novo toque", await visivel(pTotem.getByText("Toque de novo")));
  await botao.click();
  await pTotem.locator("[data-senha-emitida]").waitFor({ timeout: TIMEOUT });
  const senha1 = await pTotem.locator("[data-senha-emitida]").textContent();
  check(
    "totem: reenvio usa a mesma Idempotency-Key",
    chavesEnviadas.length === 2 && chavesEnviadas[0] && chavesEnviadas[0] === chavesEnviadas[1],
    chavesEnviadas.join(" / "),
  );
  const impressoes = await pTotem.evaluate(() => window.__impressoes);
  const imp = impressoes[0];
  check("totem: imprimiu uma via", impressoes.length === 1, JSON.stringify(impressoes));
  check("totem: @page com largura e altura explícitas", /@page \{ size: 80mm \d+mm; margin: 0 \}/.test(imp?.pagina ?? ""), imp?.pagina);
  check("totem: ficha com 80 mm", Math.abs((imp?.larguraPx ?? 0) - (80 * 96) / 25.4) < 1, imp?.larguraPx);
  // RF11 and requisitos 7.1: no QR while the public ficha page does not exist.
  check("totem: senha cabe e sem QR", imp?.senhaCabe === true && imp?.temQr === false && imp?.senha === senha1, JSON.stringify(imp));

  // Second ficha: a new cycle must get a new key and the next number.
  await pTotem.locator("[data-senha-emitida]").waitFor({ state: "detached", timeout: TIMEOUT });
  await botao.click();
  await pTotem.locator("[data-senha-emitida]").waitFor({ timeout: TIMEOUT });
  const senha2 = await pTotem.locator("[data-senha-emitida]").textContent();
  check("totem: novo ciclo gera nova chave e nova senha", chavesEnviadas[2] !== chavesEnviadas[0] && senha2 !== senha1, `${senha1} ${senha2}`);

  // Long senha layout: 5 letters + 4 digits must fit 80 mm (spike: about 39 px).
  const larga = await pTotem.evaluate(async () => {
    const { ajustarFonteSenha } = await import("/src/features/paf/totem/impressao.ts");
    const caixa = document.createElement("div");
    caixa.style.cssText = "width:72mm;white-space:nowrap;font-family:Arial;font-weight:bold;position:fixed;left:-9999px";
    caixa.textContent = "WWWWW9999";
    document.body.appendChild(caixa);
    const px = ajustarFonteSenha(caixa);
    const cabe = caixa.scrollWidth <= caixa.clientWidth;
    caixa.remove();
    return { px, cabe };
  });
  check("totem: WWWWW9999 cabe em 80 mm reduzindo a fonte", larga.cabe && larga.px < 55 && larga.px >= 30, JSON.stringify(larga));

  // NEGATIVE: definitive 4xx. The server never offers a fila outside the station, so only this case
  // appends one button to the real configuration; the emission itself goes to the real API.
  await estacaoCtx.route("**/api/v1/paf/estacao/emissao/configuracao", async (route) => {
    const real = await route.fetch();
    const json = await real.json();
    const alheia = { filaId: crypto.randomUUID(), fila: "Fila Alheia", prioridadeId: prioridade.id, prioridade: "Normal", letra: "Z", ordem: 999 };
    await route.fulfill({ response: real, json: { ...json, botoes: [...json.botoes, alheia] } });
  });
  await pTotem.waitForTimeout(4_500);
  await pTotem.reload();
  await pTotem.getByRole("button", { name: /Fila Alheia/ }).click();
  check("totem: fila fora da estação mostra a mensagem do servidor", await visivel(pTotem.getByText("Fila não disponível nesta estação.")));

  // ── Painel in its own context (so it can go offline alone), same credentials ──
  const painelCtx = await browser.newContext({ storageState: await estacaoCtx.storageState() });
  await painelCtx.addInitScript(STUBS);
  const pPainel = await painelCtx.newPage();
  await pPainel.goto(`${WEB}/paf/painel`);
  await pPainel.getByRole("button", { name: "Iniciar painel" }).click();
  await pPainel.locator('[data-conexao="conectado"]').waitFor({ timeout: TIMEOUT });
  check("painel: conectado ao PafHub", true);
  check("painel: sem aviso de voz com voz pt-BR local", !(await pPainel.getByText("Nenhuma voz em português").isVisible()));

  // ── Guichê: real login through the kit login page ──
  const guicheCtx = await browser.newContext();
  const pGuiche = await guicheCtx.newPage();
  await pGuiche.goto(`${WEB}/login`);
  await pGuiche.locator("#tenant").fill(TENANT);
  await pGuiche.locator("#email").fill(EMAIL);
  await pGuiche.locator("#password").fill(SENHA_DEMO);
  await pGuiche.locator('button[type="submit"]').click();
  await pGuiche.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  await pGuiche.goto(`${WEB}/paf/guiche`);
  await pGuiche.getByLabel("Unidade").selectOption({ label: "Loja Demo" });
  await pGuiche.getByRole("button", { name: "Usar esta unidade" }).click();
  await pGuiche.getByLabel("Guichê").selectOption({ label: "Guichê 1" });
  await pGuiche.getByRole("button", { name: "Usar este guichê" }).click();
  const chamar = pGuiche.getByRole("button", { name: "Chamar próxima" });
  await chamar.waitFor({ timeout: TIMEOUT });

  // Call senha1: painel highlights it and speaks it.
  const t0 = Date.now();
  await chamar.click();
  await pGuiche.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });
  const chamada = await pGuiche.locator("[data-senha-guiche]").textContent();
  check("guichê: chama a primeira ficha emitida", chamada === senha1, `${chamada} vs ${senha1}`);
  await pPainel.locator(`li:has-text("${chamada}")`).waitFor({ timeout: TIMEOUT });
  const latencia = Date.now() - t0;
  check("painel: senha chamada aparece na lista", true, `${latencia} ms do clique até a lista`);
  await pPainel.waitForFunction((s) => window.__falas.some((f) => f.texto === s), `senha ${chamada.replace(/^([A-Z]+)(\d+)$/, "$1 $2")}`, { timeout: TIMEOUT });
  const falas = await pPainel.evaluate(() => window.__falas);
  check("painel: fala 'senha X N' com a voz pt-BR local", falas.at(-1).voz === "Stub pt-BR", JSON.stringify(falas));

  // Reload: unit, guichê and the called atendimento come back from GET posto, not from the tab.
  await pGuiche.reload();
  await pGuiche.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });
  check("guichê: recarregar a página traz o atendimento do servidor", (await pGuiche.locator("[data-senha-guiche]").textContent()) === chamada);
  check("guichê: sem estado do posto no sessionStorage", (await pGuiche.evaluate(() => sessionStorage.getItem("paf.guiche.posto"))) === null);

  // Recall: spoken again, the list never duplicates the entry.
  await pGuiche.getByRole("button", { name: "Chamar novamente" }).click();
  // The recall waits for the first highlight (8 s default) before it is spoken.
  await pPainel.waitForFunction(() => window.__falas.length >= 2, null, { timeout: 30_000 });
  check("painel: rechamada não duplica a senha na lista", (await pPainel.locator(`li:has-text("${chamada}")`).count()) === 1);

  await pGuiche.getByRole("button", { name: "Iniciar atendimento" }).click();
  await pGuiche.getByText("Em atendimento").waitFor({ timeout: TIMEOUT });
  await pPainel.locator(`li:has-text("${chamada}") >> text=Em atendimento`).waitFor({ timeout: TIMEOUT });
  check("guichê+painel: atendimento iniciado", true);
  await pGuiche.getByRole("button", { name: "Encerrar atendimento" }).click();
  await pGuiche.getByText("Nenhum atendimento em curso").waitFor({ timeout: TIMEOUT });
  check("guichê: encerrar libera o posto", true);

  // Reconnect: the painel goes offline, a call happens meanwhile, and the state reload brings it back.
  // Offline blocks the reconnection; closing the open socket is the drop itself (Chrome's offline
  // emulation leaves an established WebSocket to localhost open).
  await painelCtx.setOffline(true);
  // Only the hub socket: closing the Vite HMR socket would make the dev client reload the page.
  await pPainel.evaluate(() => window.__sockets.filter((s) => s.url.includes("/api/v1/paf/hub")).forEach((s) => s.close()));
  await pPainel.locator('[data-conexao]:not([data-conexao="conectado"])').waitFor({ timeout: 30_000 });
  check("painel: detecta queda e tenta reconectar", true);
  await chamar.click();
  await pGuiche.locator("[data-senha-guiche]").waitFor({ timeout: TIMEOUT });
  const chamadaOffline = await pGuiche.locator("[data-senha-guiche]").textContent();
  await painelCtx.setOffline(false);
  await pPainel.locator('[data-conexao="conectado"]').waitFor({ timeout: 60_000 });
  await pPainel.locator(`li:has-text("${chamadaOffline}")`).waitFor({ timeout: TIMEOUT });
  check("painel: reconsulta o estado ao reconectar", true, chamadaOffline);

  // Cancel (no-show) and the empty-queue message from the server.
  await pGuiche.getByRole("button", { name: "Cancelar (não compareceu)" }).click();
  await pGuiche.getByText("Nenhum atendimento em curso").waitFor({ timeout: TIMEOUT });
  await pPainel.locator(`li:has-text("${chamadaOffline}")`).waitFor({ state: "detached", timeout: TIMEOUT });
  check("painel: cancelado sai da lista", true);
  await chamar.click();
  check("guichê: fila vazia mostra a mensagem do servidor", await visivel(pGuiche.getByText("Não existem fichas na fila.")));

  // Renewal success path: a token the client believes valid but the server refuses (drifted clock)
  // must be renewed through the secret, on HTTP (totem) and on the hub negotiate (painel).
  const estragar = (papel) => {
    const chave = `paf.estacao.${papel}`;
    const c = JSON.parse(localStorage.getItem(chave));
    localStorage.setItem(chave, JSON.stringify({ ...c, accessToken: "token-recusado", accessTokenExpiresAt: new Date(Date.now() + 3_600_000).toISOString() }));
  };
  await pTotem.evaluate(estragar, "emissao");
  await pTotem.reload();
  check("totem: token recusado é renovado pelo segredo", await visivel(pTotem.getByRole("button", { name: new RegExp(fila.nome) })));
  await pPainel.evaluate(estragar, "painel");
  await pPainel.reload();
  check("painel: hub renova o token recusado e conecta", await visivel(pPainel.locator('[data-conexao="conectado"]')));

  // Revoked link: the totem drops to the "not linked" screen.
  await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo/revogar`, (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken, {});
  await pTotem.reload();
  check("totem: vínculo revogado volta à tela de vínculo", await visivel(pTotem.getByText("Totem não vinculado")));
} catch (erro) {
  falhas += 1;
  console.log(`FAIL exceção :: ${erro?.stack ?? erro}`);
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "RESULTADO: todos os checks passaram" : `RESULTADO: ${falhas} falha(s)`);
process.exit(falhas === 0 ? 0 : 1);
