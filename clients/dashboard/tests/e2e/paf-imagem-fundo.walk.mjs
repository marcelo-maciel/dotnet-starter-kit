// C17 walk against a REAL API and a REAL S3 store (RustFS), no route mocks except the store outage of step 5: the
// station cadastro measures a background image in the browser, the server refuses a text color that loses contrast
// against it, the totem and the painel show the saved image, keep running while the image cannot be read, and drop
// it when the cadastro removes it.
//
// Run (fresh `seed-demo` database; API with Storage:Provider=s3 against RustFS; Vite dev server):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:5518 PAF_S3=http://127.0.0.1:9000 \
//   PAF_S3_BUCKET=<bucket of Storage:S3:Bucket> node tests/e2e/paf-imagem-fundo.walk.mjs
// Keys default to RustFS's rustfsadmin (PAF_S3_KEY / PAF_S3_SECRET). The walk creates the bucket when missing.
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import crypto from "node:crypto";
import zlib from "node:zlib";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const S3 = process.env.PAF_S3;
const BUCKET = process.env.PAF_S3_BUCKET;
const S3_KEY = process.env.PAF_S3_KEY ?? "rustfsadmin";
const S3_SECRET = process.env.PAF_S3_SECRET ?? "rustfsadmin";
if (!API || !WEB || !S3 || !BUCKET) {
  console.error("PAF_API, PAF_WEB, PAF_S3 and PAF_S3_BUCKET are required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const SENHA_DEMO = process.env.PAF_SENHA_DEMO ?? "Password123!";
const TIMEOUT = 20_000;

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

// ── S3 setup: SigV4 by hand (the dashboard has no AWS SDK), only to create the walk's bucket ──
function sha256(dado) {
  return crypto.createHash("sha256").update(dado).digest("hex");
}
async function s3(method, caminho, consulta, corpo) {
  const url = new URL(S3 + caminho + (consulta ? `?${consulta}` : ""));
  const amzData = new Date().toISOString().replace(/[:-]|\.\d{3}/g, "");
  const dia = amzData.slice(0, 8);
  const hashCorpo = sha256(corpo ?? "");
  const cabecalhos = { host: url.host, "x-amz-content-sha256": hashCorpo, "x-amz-date": amzData };
  if (corpo !== undefined) cabecalhos["content-md5"] = crypto.createHash("md5").update(corpo).digest("base64");
  const nomes = Object.keys(cabecalhos).sort();
  const consultaCanonica = consulta ? `${consulta}=` : "";
  const canonico = [method, url.pathname, consultaCanonica, nomes.map((n) => `${n}:${cabecalhos[n]}\n`).join(""), nomes.join(";"), hashCorpo].join("\n");
  const escopo = `${dia}/us-east-1/s3/aws4_request`;
  const aAssinar = ["AWS4-HMAC-SHA256", amzData, escopo, sha256(canonico)].join("\n");
  const chave = ["us-east-1", "s3", "aws4_request"].reduce(
    (k, parte) => crypto.createHmac("sha256", k).update(parte).digest(),
    crypto.createHmac("sha256", `AWS4${S3_SECRET}`).update(dia).digest(),
  );
  const assinatura = crypto.createHmac("sha256", chave).update(aAssinar).digest("hex");
  const { host: _host, ...enviados } = cabecalhos;
  return fetch(url, {
    method,
    headers: { ...enviados, authorization: `AWS4-HMAC-SHA256 Credential=${S3_KEY}/${escopo}, SignedHeaders=${nomes.join(";")}, Signature=${assinatura}` },
    body: corpo,
    signal: AbortSignal.timeout(TIMEOUT),
  });
}

// The cadastro reads the image pixels on a canvas, which the browser only allows with CORS: the bucket must answer
// GET for the dashboard origin (production: the same rule on the R2 bucket, an environment step).
const CORS_DO_BUCKET = `<CORSConfiguration><CORSRule><AllowedOrigin>${WEB}</AllowedOrigin><AllowedMethod>GET</AllowedMethod><AllowedMethod>HEAD</AllowedMethod><AllowedMethod>PUT</AllowedMethod><AllowedHeader>*</AllowedHeader><MaxAgeSeconds>600</MaxAgeSeconds></CORSRule></CORSConfiguration>`;

// A solid-color PNG built by hand: the image's luminance is known exactly.
function png(r, g, b, lado) {
  const linha = Buffer.concat([Buffer.from([0]), Buffer.alloc(lado * 3).map((_, i) => [r, g, b][i % 3])]);
  const dados = zlib.deflateSync(Buffer.concat(Array.from({ length: lado }, () => linha)));
  const bloco = (tipo, conteudo) => {
    const tamanho = Buffer.alloc(4);
    tamanho.writeUInt32BE(conteudo.length);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(zlib.crc32(Buffer.concat([Buffer.from(tipo), conteudo])) >>> 0);
    return Buffer.concat([tamanho, Buffer.from(tipo), conteudo, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(lado, 0);
  ihdr.writeUInt32BE(lado, 4);
  ihdr.set([8, 2, 0, 0, 0], 8);
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), bloco("IHDR", ihdr), bloco("IDAT", dados), bloco("IEND", Buffer.alloc(0))]);
}

async function cadastrarImagem(token, descricao, bytes) {
  const envio = await api("POST", "/api/v1/paf/midias/envios", token, { nomeArquivo: `${descricao}.png`, contentType: "image/png" });
  const put = await fetch(envio.url, { method: "PUT", headers: envio.cabecalhosObrigatorios, body: bytes, signal: AbortSignal.timeout(TIMEOUT) });
  if (!put.ok) throw new Error(`PUT do arquivo -> ${put.status} ${await put.text()}`);
  return api("POST", "/api/v1/paf/midias", token, { envioId: envio.envioId, descricao, duracaoSegundos: 30 });
}

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
    }
  };
};

async function derrubarHub(page) {
  await page.evaluate(() => window.__sockets.filter((s) => s.url.includes("/api/v1/paf/hub")).forEach((s) => s.close()));
}

async function esperarAte(page, fn, arg, timeout = TIMEOUT) {
  return page.waitForFunction(fn, arg, { timeout }).then(
    () => true,
    () => false,
  );
}

const fundoDoMain = (page) => page.locator("main").first().evaluate((el) => ({ imagem: getComputedStyle(el).backgroundImage, attr: el.getAttribute("data-imagem-fundo") }));

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const bucket = await s3("PUT", `/${BUCKET}`);
  check("setup: bucket do walk existe", bucket.ok || bucket.status === 409, `${bucket.status}`);
  const cors = await s3("PUT", `/${BUCKET}`, "cors", CORS_DO_BUCKET);
  check("setup: CORS do bucket para a origem do dashboard", cors.ok, `${cors.status} ${cors.ok ? "" : await cors.text()}`);

  const ctxAdmin = await browser.newContext({ viewport: { width: 1280, height: 1600 } });
  const pAdmin = await ctxAdmin.newPage();
  await pAdmin.goto(`${WEB}/login`);
  await pAdmin.locator("#tenant").fill(TENANT);
  await pAdmin.locator("#email").fill(EMAIL);
  await pAdmin.locator("#password").fill(SENHA_DEMO);
  await pAdmin.locator('button[type="submit"]').click();
  await pAdmin.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  // One login only: a second one would revoke the browser session (sessão única).
  const token = () => pAdmin.evaluate(() => localStorage.getItem("fsh.dashboard.accessToken"));

  const clara = await cadastrarImagem(await token(), "Parede clara", png(0xf0, 0xf0, 0xf0, 32));
  check("setup: imagem clara cadastrada (M03, RustFS)", clara.tipo === "Imagem" && clara.ativa, JSON.stringify(clara));
  const estacao = (await api("GET", "/api/v1/paf/estacoes", await token())).find((e) => e.tipo === "EmissaoPainel");

  // ── 1-2. Cadastro: measure in the browser, the server refuses white text on the light image ──
  await pAdmin.goto(`${WEB}/paf/estacoes`);
  await pAdmin.locator(`[data-estacao="${estacao.id}"]`).getByRole("button", { name: "Editar" }).click();
  const painel = pAdmin.locator('[data-bloco="Painel"]');
  await painel.getByLabel("Cor de fundo", { exact: true }).fill("#000000");
  await painel.getByLabel("Cor da fonte das filas").fill("#FFFFFF");
  await painel.getByLabel("Cor da fonte da chamada").fill("#FFFFFF");
  await painel.getByLabel("Cor da fonte alternada").fill("#FFFFFF");
  const urlsLidas = [];
  pAdmin.on("request", (r) => {
    if (r.url().startsWith(S3)) urlsLidas.push(r.url());
  });
  await painel.getByLabel("Imagem de fundo do painel").selectOption({ label: "Parede clara" });
  check(
    "cadastro: imagem medida no navegador (aviso de contraste da imagem)",
    await painel.getByText("Fonte das filas sobre a imagem de fundo").waitFor({ timeout: TIMEOUT }).then(() => true, () => false),
  );
  check("cadastro: a medição leu o arquivo do armazenamento", urlsLidas.length > 0, urlsLidas[0]?.slice(0, 60));
  check("cadastro: sem erro de leitura (CORS do armazenamento)", (await painel.locator('[role="alert"]').count()) === 0, await painel.locator('[role="alert"]').allTextContents().then((t) => t.join(" | ")));

  const emissao = pAdmin.locator('[data-bloco="Emissao"]');
  await emissao.getByLabel("Imagem de fundo do totem").selectOption({ label: "Parede clara" });
  const respostaRecusa = pAdmin.waitForResponse((r) => r.url().endsWith(`/api/v1/paf/estacoes/${estacao.id}`) && r.request().method() === "PUT", { timeout: TIMEOUT });
  await esperarAte(pAdmin, () => !document.body.textContent?.includes("Medindo o contraste da imagem"));
  await pAdmin.getByRole("button", { name: "Salvar" }).click();
  const recusa = await respostaRecusa;
  check("servidor: texto branco sobre a imagem clara 400", recusa.status() === 400, `${recusa.status()}`);
  check(
    "tela: motivo do servidor exibido",
    await pAdmin.locator("[data-edicao-estacao] ul[role=alert]").getByText("Painel.CorFonteFilas sobre a imagem de fundo").waitFor({ timeout: TIMEOUT }).then(() => true, () => false),
  );

  // Changing the block background re-measures (translucent pixels show it); then black text on white passes.
  await painel.getByLabel("Cor de fundo", { exact: true }).fill("#FFFFFF");
  await painel.getByLabel("Cor da fonte das filas").fill("#000000");
  await painel.getByLabel("Cor da fonte da chamada").fill("#000000");
  await painel.getByLabel("Cor da fonte alternada").fill("#000000");
  check(
    "cadastro: aviso some com texto preto",
    await esperarAte(pAdmin, () => !document.querySelector('[data-bloco="Painel"]')?.textContent?.includes("sobre a imagem de fundo")),
  );
  await esperarAte(pAdmin, () => !document.body.textContent?.includes("Medindo o contraste da imagem"));
  const respostaOk = pAdmin.waitForResponse((r) => r.url().endsWith(`/api/v1/paf/estacoes/${estacao.id}`) && r.request().method() === "PUT", { timeout: TIMEOUT });
  await pAdmin.getByRole("button", { name: "Salvar" }).click();
  const ok = await respostaOk;
  check("servidor: texto preto sobre a imagem clara 200", ok.status() === 200, `${ok.status()} ${ok.status() === 200 ? "" : await ok.text()}`);
  const gravada = (await api("GET", "/api/v1/paf/estacoes", await token())).find((e) => e.id === estacao.id);
  check(
    "gravado: a amostra medida no navegador é a de uma imagem clara (faixa 0xF0 só)",
    gravada.configuracao?.painel?.imagemFundo?.luminancias === gravada.configuracao?.emissao?.imagemFundo?.luminancias &&
      /^0+[1-9A-F]0*$/.test(gravada.configuracao?.painel?.imagemFundo?.luminancias ?? ""),
    gravada.configuracao?.painel?.imagemFundo?.luminancias,
  );

  // ── 3. Totem and painel show the image ──
  const link = await api("POST", `/api/v1/paf/estacoes/${estacao.id}/vinculo`, await token(), {});
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await ctx.addInitScript(STUBS);
  const pVinculo = await ctx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  await pVinculo.close();
  const pTotem = await ctx.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  const pPainel = await ctx.newPage();
  await pPainel.goto(`${WEB}/paf/painel`);
  await pPainel.getByRole("button", { name: "Iniciar painel" }).click();

  for (const [nome, pagina] of [["totem", pTotem], ["painel", pPainel]]) {
    check(`${nome}: imagem carregada e aplicada`, await esperarAte(pagina, () => document.querySelector("main")?.hasAttribute("data-imagem-fundo")));
    const f = await fundoDoMain(pagina);
    check(`${nome}: background-image é a URL assinada do armazenamento`, f.imagem.includes(new URL(S3).host) && f.imagem.includes("X-Amz-Signature"), f.imagem.slice(0, 80));
  }
  const cor = await pTotem.locator("h1").evaluate((el) => getComputedStyle(el).color);
  check("totem: título no preto padrão medido contra a imagem", cor === "rgb(0, 0, 0)", cor);

  // ── 4. Store unreachable: the painel keeps running, re-reads the configuration and retries ──
  const ctxQueda = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  await ctxQueda.addInitScript(STUBS);
  const linkQueda = await api("POST", `/api/v1/paf/estacoes/${estacao.id}/vinculo`, await token(), {});
  const pV2 = await ctxQueda.newPage();
  await pV2.goto(`${WEB}/paf/estacao/vinculo#tenant=${linkQueda.tenant}&token=${linkQueda.token}`);
  await pV2.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  await pV2.close();
  const pQueda = await ctxQueda.newPage();
  const leiturasConfig = [];
  pQueda.on("request", (r) => {
    if (r.url().endsWith("/api/v1/paf/estacao/painel/configuracao")) leiturasConfig.push(Date.now());
  });
  let falhaDaImagemEm = null;
  pQueda.on("console", (m) => {
    if (falhaDaImagemEm === null && m.text().includes("imagem de fundo não carregou")) falhaDaImagemEm = Date.now();
  });
  await pQueda.route(`${S3}/**`, (r) => r.abort("connectionrefused"));
  await pQueda.goto(`${WEB}/paf/painel`);
  await pQueda.getByRole("button", { name: "Iniciar painel" }).click();
  const limiteQueda = Date.now() + TIMEOUT;
  while (Date.now() < limiteQueda && !(falhaDaImagemEm !== null && leiturasConfig.some((t) => t >= falhaDaImagemEm))) {
    await new Promise((r) => setTimeout(r, 250));
  }
  check(
    "queda: a imagem que não carrega dispara uma releitura da configuração",
    falhaDaImagemEm !== null && leiturasConfig.some((t) => t >= falhaDaImagemEm),
    `falha=${falhaDaImagemEm !== null} leituras=${leiturasConfig.length}`,
  );
  const semImagem = await fundoDoMain(pQueda);
  check("queda: tela não fica vazia (painel segue com o conteúdo)", (await pQueda.locator('section[aria-label="Chamada atual"]').isVisible()) && semImagem.attr === null, JSON.stringify(semImagem));
  await pQueda.unroute(`${S3}/**`);
  check("queda: armazenamento de volta, a nova tentativa aplica a imagem", await esperarAte(pQueda, () => document.querySelector("main")?.hasAttribute("data-imagem-fundo"), undefined, 45_000));
  await ctxQueda.close();

  // ── 5. Removing the image in the cadastro takes it off both screens, without reload ──
  const corpo = {
    nome: gravada.nome,
    filas: gravada.filas,
    prioridades: gravada.prioridades,
    ramal: gravada.ramal,
    configuracao: {
      emissao: { ...gravada.configuracao.emissao, imagemFundo: null },
      guiche: null,
      painel: { ...gravada.configuracao.painel, imagemFundo: null },
    },
  };
  await api("PUT", `/api/v1/paf/estacoes/${estacao.id}`, await token(), corpo);
  await derrubarHub(pTotem);
  await derrubarHub(pPainel);
  for (const [nome, pagina] of [["totem", pTotem], ["painel", pPainel]]) {
    check(`${nome}: imagem some ao remover`, await esperarAte(pagina, () => getComputedStyle(document.querySelector("main")).backgroundImage === "none"));
  }
  check("sem recarregar: o mesmo totem desde o início", (await pTotem.evaluate(() => performance.getEntriesByType("navigation").length)) === 1);

  await ctx.close();
  await ctxAdmin.close();
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.stack : String(erro));
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
