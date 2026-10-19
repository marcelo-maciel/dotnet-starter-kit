// C19 walk against a REAL API (no route mocks): RF06 "Logotipo". With no row the totem, the painel and the printed
// ficha show the system logo; an image uploaded through the mídia flow (M03) and picked on the parameters screen as the
// empresa default shows on the totem screen, in the painel header and on the ficha at print time (decoded, not a
// blank box); picking "Logotipo padrão do sistema" again brings the system logo back.
//
// Prerequisites: fresh `seed-demo` database, API and Vite up, API origin resolvable for the local store's URLs. Run:
//   PAF_API=http://localhost:5430 PAF_WEB=http://localhost:5530 PAF_SENHA_DEMO=<Seed__DemoPassword> node tests/e2e/paf-logotipo.walk.mjs
// or through deploy/paf/walk-ci.sh with PAF_WALK=tests/e2e/paf-logotipo.walk.mjs and OriginOptions__OriginUrl="".
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { deflateSync } from "node:zlib";
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
const LARGURA = 64;
const ALTURA = 32;
const DESCRICAO = `Logo walk ${Date.now()}`;

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

// A real, decodable PNG (solid color), so the browser's naturalWidth proves the file arrived whole.
function png(largura, altura) {
  const tabela = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (buf) => {
    let c = 0xffffffff;
    for (const b of buf) c = tabela[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const bloco = (tipo, dados) => {
    const cabeca = Buffer.alloc(8);
    cabeca.writeUInt32BE(dados.length, 0);
    cabeca.write(tipo, 4, "ascii");
    const fim = Buffer.alloc(4);
    fim.writeUInt32BE(crc(Buffer.concat([cabeca.subarray(4), dados])), 0);
    return Buffer.concat([cabeca, dados, fim]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(largura, 0);
  ihdr.writeUInt32BE(altura, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // RGB
  const linha = Buffer.concat([Buffer.from([0]), Buffer.alloc(largura * 3, Buffer.from([200, 30, 30]))]);
  const pixels = Buffer.concat(Array.from({ length: altura }, () => linha));
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    bloco("IHDR", ihdr),
    bloco("IDAT", deflateSync(pixels)),
    bloco("IEND", Buffer.alloc(0)),
  ]);
}

// print() records what the ticket holds at that instant (the logo's decode state included) and fires afterprint.
const STUBS = () => {
  window.__impressoes = [];
  window.print = () => {
    const ficha = document.querySelector("[data-ficha-impressa]");
    const img = ficha?.querySelector('img[data-logotipo="personalizado"]');
    window.__impressoes.push({
      senha: ficha?.querySelector("[data-senha]")?.textContent ?? null,
      logotipo: img ? { completa: img.complete, largura: img.naturalWidth } : null,
      padrao: ficha?.querySelector('[data-logotipo="padrao"]') !== null,
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

const linha = (p) => p.locator('[data-parametro="logotipo"]');

async function escolherNaEmpresa(p, rotulo, selo) {
  await linha(p).getByRole("button", { name: "Alterar" }).click();
  await linha(p).getByLabel(/Novo valor de/).selectOption({ label: rotulo });
  await linha(p).getByRole("button", { name: "Salvar como padrão da empresa" }).click();
  await p.locator(`[data-parametro="logotipo"] [data-selo="${selo}"]`).waitFor({ timeout: TIMEOUT });
}

async function emitir(pTotem) {
  const antes = await pTotem.evaluate(() => window.__impressoes.length);
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().click();
  await pTotem.waitForFunction((n) => window.__impressoes.length > n, antes, { timeout: TIMEOUT });
  await pTotem.locator("[data-senha-emitida]").waitFor({ state: "detached", timeout: TIMEOUT });
  return pTotem.evaluate(() => window.__impressoes.at(-1));
}

async function larguraDoLogotipo(p, escopo) {
  const img = p.locator(`${escopo} img[data-logotipo="personalizado"]`).first();
  if (!(await visivel(img, TIMEOUT))) return null;
  await p.waitForFunction((el) => el.complete, await img.elementHandle(), { timeout: TIMEOUT });
  return img.evaluate((el) => el.naturalWidth);
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── Setup: the demo admin as company admin (RF23), a link for the demo totem + painel, an image via M03 ──
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

  const envio = await ok("POST", "/api/v1/paf/midias/envios", admin.accessToken, { nomeArquivo: "logo.png", contentType: "image/png" });
  const destino = new URL(envio.url, API);
  const put = await fetch(destino, {
    method: "PUT",
    headers: { ...envio.cabecalhosObrigatorios, "Content-Type": "image/png" },
    body: png(LARGURA, ALTURA),
    signal: AbortSignal.timeout(TIMEOUT),
  });
  check("envio: PUT do arquivo pelo fluxo de M03", put.ok, `${put.status}`);
  await ok("POST", "/api/v1/paf/midias", admin.accessToken, { envioId: envio.envioId, descricao: DESCRICAO, duracaoSegundos: 10 });

  // ── Stations with the system logo ──
  const estacaoCtx = await browser.newContext();
  await estacaoCtx.addInitScript(STUBS);
  const pVinculo = await estacaoCtx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o painel" }).waitFor({ timeout: TIMEOUT });
  await pVinculo.close();
  const pTotem = await estacaoCtx.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().waitFor({ timeout: TIMEOUT });
  check("totem: sem parâmetro, logotipo padrão do sistema", await visivel(pTotem.locator('main > [data-logotipo="padrao"]'), TIMEOUT));
  const semLogo = await emitir(pTotem);
  check("ficha: sem parâmetro, imprime o logotipo padrão", semLogo.padrao && semLogo.logotipo === null, JSON.stringify(semLogo));

  // ── Parameters screen: pick the uploaded image as the empresa default ──
  const pessoaCtx = await browser.newContext();
  const pPar = await entrar(pessoaCtx);
  await pPar.goto(`${WEB}/paf/parametros`);
  await linha(pPar).waitFor({ timeout: TIMEOUT });
  check("tela: linha do logotipo com o padrão do sistema", (await linha(pPar).locator("[data-valor-efetivo]").textContent()) === "Logotipo padrão do sistema");
  await escolherNaEmpresa(pPar, DESCRICAO, "Empresa");
  check("tela: mostra a descrição da imagem, nunca o id", (await linha(pPar).locator("[data-valor-efetivo]").textContent()) === `Imagem personalizada: ${DESCRICAO}`);

  // ── Totem, ficha and painel show the custom logo ──
  await pTotem.reload();
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().waitFor({ timeout: TIMEOUT });
  check("totem: mostra o logotipo personalizado, decodificado", (await larguraDoLogotipo(pTotem, "main")) === LARGURA);
  const comLogo = await emitir(pTotem);
  check(
    "ficha: no instante do print o logotipo já está decodificado",
    comLogo.logotipo?.completa === true && comLogo.logotipo.largura === LARGURA && !comLogo.padrao,
    JSON.stringify(comLogo),
  );
  const pPainel = await estacaoCtx.newPage();
  await pPainel.goto(`${WEB}/paf/painel`);
  await pPainel.getByRole("button", { name: "Iniciar painel" }).click();
  await pPainel.locator('[data-conexao="conectado"]').waitFor({ timeout: TIMEOUT });
  check("painel: logotipo personalizado no cabeçalho", (await larguraDoLogotipo(pPainel, "header")) === LARGURA);

  // ── Back to the system logo ──
  await escolherNaEmpresa(pPar, "Logotipo padrão do sistema", "Empresa");
  await pTotem.reload();
  await pTotem.getByRole("button", { name: /Atendimento/ }).first().waitFor({ timeout: TIMEOUT });
  check("totem: de volta ao logotipo padrão", await visivel(pTotem.locator('main > [data-logotipo="padrao"]'), TIMEOUT));
  const deVolta = await emitir(pTotem);
  check("ficha: de volta ao logotipo padrão", deVolta.padrao && deVolta.logotipo === null, JSON.stringify(deVolta));
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
