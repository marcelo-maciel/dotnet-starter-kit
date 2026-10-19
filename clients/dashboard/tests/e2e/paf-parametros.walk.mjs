// C10 walk against a REAL API (no route mocks): RF06 screen with inherited / overridden badge, ficha title and
// footer on the totem print with the DELETE falling back to the empresa, QR read but not printed, hotkey configured
// in the unidade firing on the guichê, hotkey conflict refused, fila ordering changing the totem buttons.
//
// Prerequisites: fresh `seed-demo` database plus a second fila "Abertura" on the demo totem and guichê (station order
// after "Atendimento"), API and Vite up. Run:
//   PAF_API=http://localhost:5420 PAF_WEB=http://localhost:5520 node tests/e2e/paf-parametros.walk.mjs
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

function visivel(locator) {
  return locator.waitFor({ timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
}

async function api(method, path, token, body) {
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
  return { status: r.status, json: texto ? JSON.parse(texto) : null };
}

async function ok(method, path, token, body) {
  const r = await api(method, path, token, body);
  if (r.status >= 300) throw new Error(`${method} ${path} -> ${r.status} ${JSON.stringify(r.json)}`);
  return r.json;
}

function sub(token) {
  return JSON.parse(Buffer.from(token.split(".")[1], "base64url").toString()).sub;
}

// print() records what the ticket shows and fires afterprint like --kiosk-printing would.
const STUBS = () => {
  window.__impressoes = [];
  window.print = () => {
    const ficha = document.querySelector("[data-ficha-impressa]");
    window.__impressoes.push({
      titulo: ficha?.querySelector("[data-titulo-ficha]")?.textContent ?? null,
      rodape: ficha?.querySelector("[data-rodape-ficha]")?.textContent ?? null,
      senha: ficha?.querySelector("[data-senha]")?.textContent ?? null,
      temQr: !!ficha?.querySelector("svg, canvas, img"),
    });
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

async function seloDe(p, chave) {
  return linha(p, chave).locator("[data-selo]").getAttribute("data-selo", { timeout: TIMEOUT });
}

async function emitir(pTotem, fila) {
  const antes = await pTotem.evaluate(() => window.__impressoes.length);
  await pTotem.getByRole("button", { name: new RegExp(fila) }).first().click();
  await pTotem.waitForFunction((n) => window.__impressoes.length > n, antes, { timeout: TIMEOUT });
  await pTotem.locator("[data-senha-emitida]").waitFor({ state: "detached", timeout: TIMEOUT });
  return pTotem.evaluate(() => window.__impressoes.at(-1));
}

async function botoesDoTotem(pTotem) {
  await pTotem.reload();
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().waitFor({ timeout: TIMEOUT });
  return pTotem.evaluate(() =>
    [...document.querySelectorAll("main button, button")].map((b) => b.textContent ?? "").filter((t) => /Atendimento|Abertura/.test(t)),
  );
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── Setup: the demo admin becomes the company admin (RF23), through the PAF scope endpoint ──
  let admin = await ok("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const unidades = await ok("GET", "/api/v1/paf/unidades", admin.accessToken);
  const loja = unidades.find((u) => u.nome === "Loja Demo");
  await ok("PUT", `/api/v1/paf/usuarios/${sub(admin.accessToken)}/unidades`, admin.accessToken, {
    usuarioId: sub(admin.accessToken),
    unidades: [loja.id],
    escopoEmpresa: true,
  });
  admin = await ok("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO });
  const inicial = await ok("GET", "/api/v1/paf/parametros", admin.accessToken);
  check(
    "api: banco novo, todas as chaves de fábrica, administrador da empresa",
    inicial.alteraEmpresa === true && inicial.itens.every((i) => i.origem === "Fabrica"),
    JSON.stringify({ alteraEmpresa: inicial.alteraEmpresa, origens: [...new Set(inicial.itens.map((i) => i.origem))] }),
  );
  const estacoes = await ok("GET", "/api/v1/paf/estacoes", admin.accessToken);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const link = await ok("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, admin.accessToken, {});

  // ── Parameters screen: empresa default, unidade override, badges ──
  const adminCtx = await browser.newContext();
  const pPar = await entrar(adminCtx);
  await pPar.goto(`${WEB}/paf/parametros`);
  check("tela: menu tem Parâmetros", await visivel(pPar.getByRole("link", { name: "Parâmetros" })));
  check("tela: título começa no padrão do sistema", (await seloDe(pPar, "tituloFichas")) === "Fabrica");

  async function salvar(chave, valor, botao) {
    await linha(pPar, chave).getByRole("button", { name: "Alterar" }).click();
    const campo = linha(pPar, chave).getByLabel(/Novo valor de/);
    if ((await campo.evaluate((e) => e.tagName)) === "SELECT") await campo.selectOption(valor);
    else await campo.fill(valor);
    await linha(pPar, chave).getByRole("button", { name: botao }).click();
  }

  await salvar("tituloFichas", "Rede Acme", "Salvar como padrão da empresa");
  await pPar.locator('[data-parametro="tituloFichas"] [data-selo="Empresa"]').waitFor({ timeout: TIMEOUT });
  check("tela: padrão da empresa aparece como herdado", (await seloDe(pPar, "tituloFichas")) === "Empresa");
  await salvar("tituloFichas", "Loja Demo Centro", "Salvar nesta unidade");
  await pPar.locator('[data-parametro="tituloFichas"] [data-selo="Unidade"]').waitFor({ timeout: TIMEOUT });
  check(
    "tela: sobrescrita mostra o selo e o padrão da empresa que ela esconde",
    (await linha(pPar, "tituloFichas").textContent()).includes("Padrão da empresa: Rede Acme"),
    await linha(pPar, "tituloFichas").textContent(),
  );
  await salvar("mensagemRodapeFicha", "Obrigado, aguarde ser chamado", "Salvar nesta unidade");
  await pPar.locator('[data-parametro="mensagemRodapeFicha"] [data-selo="Unidade"]').waitFor({ timeout: TIMEOUT });
  await salvar("imprimirQrFicha", "true", "Salvar nesta unidade");
  await pPar.locator('[data-parametro="imprimirQrFicha"] [data-selo="Unidade"]').waitFor({ timeout: TIMEOUT });

  // Hotkey conflict: F7 is "chamar o próximo" by default.
  await salvar("teclaEncerrar", "F7", "Salvar nesta unidade");
  check("tela: tecla em conflito recusada com a regra", await visivel(pPar.getByRole("alert").getByText(/ficaria com duas ações do guichê/)));
  await linha(pPar, "teclaEncerrar").getByRole("button", { name: "Cancelar" }).click();
  check("tela: tecla recusada não grava", (await seloDe(pPar, "teclaEncerrar")) === "Fabrica");
  await salvar("teclaChamarProxima", "F9", "Salvar nesta unidade");
  await pPar.locator('[data-parametro="teclaChamarProxima"] [data-selo="Unidade"]').waitFor({ timeout: TIMEOUT });

  // ── Totem: title and footer of the unidade on the print, no QR element ──
  const estacaoCtx = await browser.newContext();
  await estacaoCtx.addInitScript(STUBS);
  const pVinculo = await estacaoCtx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  await pVinculo.close();
  const pTotem = await estacaoCtx.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().waitFor({ timeout: TIMEOUT });

  const naUnidade = await emitir(pTotem, "Atendimento");
  check(
    "totem: ficha impressa com título e rodapé da unidade",
    naUnidade.titulo === "Loja Demo Centro" && naUnidade.rodape === "Obrigado, aguarde ser chamado",
    JSON.stringify(naUnidade),
  );
  check("totem: QR ligado no parâmetro e a ficha continua sem QR (REQ l.368)", naUnidade.temQr === false, JSON.stringify(naUnidade));

  await linha(pPar, "tituloFichas").getByRole("button", { name: "Voltar ao herdado" }).click();
  await pPar.locator('[data-parametro="tituloFichas"] [data-selo="Empresa"]').waitFor({ timeout: TIMEOUT });
  check("tela: voltar ao herdado devolve o selo da empresa", (await seloDe(pPar, "tituloFichas")) === "Empresa");
  const daEmpresa = await emitir(pTotem, "Atendimento");
  check("totem: depois do DELETE a ficha sai com o título da empresa", daEmpresa.titulo === "Rede Acme", JSON.stringify(daEmpresa));

  // ── Fila ordering: station order, then by name ──
  const porOrdem = await botoesDoTotem(pTotem);
  await salvar("ordenacaoFilas", "nome", "Salvar nesta unidade");
  await pPar.locator('[data-parametro="ordenacaoFilas"] [data-selo="Unidade"]').waitFor({ timeout: TIMEOUT });
  const porNome = await botoesDoTotem(pTotem);
  check(
    "totem: ordenação por nome muda a ordem dos botões",
    /Atendimento/.test(porOrdem[0] ?? "") && /Abertura/.test(porNome[0] ?? ""),
    `${porOrdem.join(" | ")} -> ${porNome.join(" | ")}`,
  );
  await adminCtx.close();

  // ── Guichê: the hotkey configured in the unidade (F9) calls the next ficha; the old default (F7) does nothing ──
  const guicheCtx = await browser.newContext();
  const pGuiche = await entrar(guicheCtx);
  await pGuiche.goto(`${WEB}/paf/guiche`);
  await pGuiche.getByLabel("Guichê").waitFor({ timeout: TIMEOUT });
  await pGuiche.getByLabel("Guichê").selectOption({ label: "Guichê 1" });
  await pGuiche.getByRole("button", { name: "Usar este guichê" }).click();
  await pGuiche.locator("[data-teclas-atalho]").waitFor({ timeout: TIMEOUT });
  check("guichê: lista de atalhos mostra a tecla da unidade", (await pGuiche.locator("[data-teclas-atalho]").textContent()).includes("F9 chamar próxima"));
  // The fresh-ficha guard (seção 4) holds a new ficha for five seconds before it can be called.
  await pGuiche.waitForTimeout(5_500);
  await pGuiche.keyboard.press("F7");
  await pGuiche.waitForTimeout(1_500);
  check("guichê: F7 (padrão antigo) não chama", (await pGuiche.locator("[data-senha-guiche]").count()) === 0);
  await pGuiche.keyboard.press("F9");
  check("guichê: F9 configurada chama a próxima", await visivel(pGuiche.locator("[data-senha-guiche]")));
  await pGuiche.keyboard.press("F3");
  check("guichê: F3 (padrão) inicia o atendimento", await visivel(pGuiche.locator('[data-estado-guiche]:has-text("Em atendimento")')));
  await pGuiche.keyboard.press("F4");
  check("guichê: F4 encerra", await visivel(pGuiche.getByText(/Nenhum atendimento em curso/)));
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
