// M10 walk against a REAL API and a REAL S3 store (RustFS), no route mocks except the network failure of the PUT:
// the mídia cadastro sends MP4 and PNG straight to the store with progress and the duration read from the file,
// refuses .avi with the list of formats, swaps a file and previews the new one, survives a failed PUT without
// creating a mídia; the station's display list is edited and the painel's own read follows it (a disabled mídia
// leaves it); programações compute the end live, refuse past midnight, filter by the four fields and delete with
// confirmation; a Gerente (no Midias.Create) has no upload and gets 403; a unidade switch switches the list.
//
// Run (fresh `seed-demo` database; API with Storage:Provider=s3 against RustFS; Vite dev server; ffmpeg on PATH or
// PAF_FFMPEG, only to build the MP4 fixtures; PAF_PG_CONTAINER = the PostgreSQL container, only to give the walk's
// second unidade a contract, which only a root operator can create through the API):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:5518 PAF_S3=http://127.0.0.1:9000 PAF_PG_CONTAINER=<pg> \
//   PAF_S3_BUCKET=<bucket of Storage:S3:Bucket> PAF_SENHA_DEMO=<seed password> node tests/e2e/paf-midias.walk.mjs
// Keys default to RustFS's rustfsadmin (PAF_S3_KEY / PAF_S3_SECRET). The walk creates the bucket when missing.
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";
import { execFileSync } from "node:child_process";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const S3 = process.env.PAF_S3;
const BUCKET = process.env.PAF_S3_BUCKET;
const SENHA_DEMO = process.env.PAF_SENHA_DEMO;
const S3_KEY = process.env.PAF_S3_KEY ?? "rustfsadmin";
const S3_SECRET = process.env.PAF_S3_SECRET ?? "rustfsadmin";
const FFMPEG = process.env.PAF_FFMPEG ?? "ffmpeg";
const PG = process.env.PAF_PG_CONTAINER;
if (!API || !WEB || !S3 || !BUCKET || !SENHA_DEMO || !PG) {
  console.error("PAF_API, PAF_WEB, PAF_S3, PAF_S3_BUCKET, PAF_SENHA_DEMO and PAF_PG_CONTAINER are required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const TIMEOUT = 20_000;
const SUFIXO = Date.now().toString().slice(-6);
const MSG_FORMATO = "Formato não aceito. Formatos aceitos: .mp4, .webm, .jpg, .jpeg, .png, .webp, .gif, .mp3, .m4a.";

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

async function api(method, caminho, token, body) {
  const r = await chamar(method, caminho, token, body);
  if (!r.ok) throw new Error(`${method} ${caminho} -> ${r.status} ${await r.text()}`);
  return r.status === 204 ? null : r.json();
}

function chamar(method, caminho, token, body) {
  const headers = { tenant: TENANT, "X-FSH-App": "dashboard" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  return fetch(API + caminho, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(TIMEOUT) });
}

// ── S3 setup: SigV4 by hand (the dashboard has no AWS SDK), only to create the walk's bucket and its CORS ──
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

// The browser PUTs the file and reads it back cross-origin: the bucket must answer CORS for the dashboard origin
// (production: the same rule on the R2 bucket, M11).
const CORS_DO_BUCKET = `<CORSConfiguration><CORSRule><AllowedOrigin>${WEB}</AllowedOrigin><AllowedMethod>GET</AllowedMethod><AllowedMethod>HEAD</AllowedMethod><AllowedMethod>PUT</AllowedMethod><AllowedHeader>*</AllowedHeader><MaxAgeSeconds>600</MaxAgeSeconds></CORSRule></CORSConfiguration>`;

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

// H.264 MP4 of a solid color: the duration is known, so the prefilled value is checkable.
function mp4(pasta, nome, cor, segundos) {
  const destino = path.join(pasta, nome);
  execFileSync(FFMPEG, ["-v", "error", "-y", "-f", "lavfi", "-i", `color=c=${cor}:s=64x64:r=10:d=${segundos}`, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", destino], {
    timeout: 60_000,
  });
  return { name: nome, mimeType: "video/mp4", buffer: fs.readFileSync(destino) };
}

async function entrar(contexto, email) {
  const pagina = await contexto.newPage();
  await pagina.goto(`${WEB}/login`);
  await pagina.locator("#tenant").fill(TENANT);
  await pagina.locator("#email").fill(email);
  await pagina.locator("#password").fill(SENHA_DEMO);
  await pagina.locator('button[type="submit"]').click();
  await pagina.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  return pagina;
}

const tokenDe = (pagina) => pagina.evaluate(() => localStorage.getItem("fsh.dashboard.accessToken"));
const linhaMidia = (pagina, descricao) => pagina.locator("[data-midia]", { has: pagina.locator("[data-descricao]", { hasText: descricao }) });

// Records every percentage the upload bar showed, so a finished upload still proves it moved.
async function vigiarProgresso(pagina) {
  await pagina.evaluate(() => {
    window.__progresso = [];
    new MutationObserver(() => {
      const texto = document.querySelector("[data-progresso-envio] span")?.textContent;
      if (texto && window.__progresso.at(-1) !== texto) window.__progresso.push(texto);
    }).observe(document.body, { subtree: true, childList: true, characterData: true });
  });
}

const pasta = fs.mkdtempSync(path.join(os.tmpdir(), "paf-m10-walk-"));
const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const institucional = mp4(pasta, "institucional.mp4", "blue", 7.3);
  const institucionalNovo = mp4(pasta, "institucional-novo.MP4", "red", 5);
  const cartaz = { name: "cartaz.png", mimeType: "image/png", buffer: png(0x20, 0x80, 0x20, 32) };
  const avi = { name: "filme-antigo.avi", mimeType: "video/x-msvideo", buffer: Buffer.from("RIFF0000AVI LIST") };

  const bucket = await s3("PUT", `/${BUCKET}`);
  check("setup: bucket do walk existe", bucket.ok || bucket.status === 409, `${bucket.status}`);
  const cors = await s3("PUT", `/${BUCKET}`, "cors", CORS_DO_BUCKET);
  check("setup: CORS do bucket para a origem do dashboard", cors.ok, `${cors.status}`);

  // ── Setup through the API (before the browser login: the browser's login then holds the single session) ──
  // Only the company admin creates a unidade (RF23): the demo admin is made one first, as paf-logotipo.walk.mjs does.
  const tInicial = (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken;
  const demo = (await api("GET", "/api/v1/paf/unidades", tInicial)).find((u) => u.nome === "Loja Demo");
  const adminId = JSON.parse(Buffer.from(tInicial.split(".")[1], "base64url").toString("utf8")).sub;
  await api("PUT", `/api/v1/paf/usuarios/${adminId}/unidades`, tInicial, { usuarioId: adminId, unidades: [demo.id], escopoEmpresa: true });
  const tSetup = (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken;
  const nomeOutra = `Loja M10 ${SUFIXO}`;
  const outraId = await api("POST", "/api/v1/paf/unidades", tSetup, { nome: nomeOutra, documentoFiscal: null });
  // Ids are Guids the API returned (never typed text), so they go into the statement as they are.
  execFileSync("docker", ["exec", PG, "psql", "-U", "fsh", "-d", "fsh", "-v", "ON_ERROR_STOP=1", "-tAc",
    `INSERT INTO paf."ContratosUnidade" ("Id", "UnidadeId", "InicioVigencia", "FimVigencia", "Plano", "LimiteSessoes", "Politica", "TenantId")
     SELECT gen_random_uuid(), '${outraId}', "InicioVigencia", "FimVigencia", "Plano", "LimiteSessoes", "Politica", "TenantId"
     FROM paf."ContratosUnidade" WHERE "UnidadeId" = '${demo.id}' LIMIT 1`], { timeout: TIMEOUT });
  const novoUsuario = (email, papel, unidades) =>
    api("POST", "/api/v1/paf/usuarios", tSetup, {
      nome: papel,
      sobrenome: "M10",
      email,
      senha: SENHA_DEMO,
      confirmacaoSenha: SENHA_DEMO,
      funcao: null,
      ramal: null,
      papel,
      unidades,
      escopoEmpresa: false,
    });
  const emailGerente = `gerente.m10.${SUFIXO}@acme.com`;
  const emailRegional = `regional.m10.${SUFIXO}@acme.com`;
  await novoUsuario(emailGerente, "Gerente", [demo.id]);
  await novoUsuario(emailRegional, "Administrador", [demo.id, outraId]);

  const ctxAdmin = await browser.newContext({ viewport: { width: 1400, height: 1600 } });
  const p = await entrar(ctxAdmin, EMAIL);

  // ── Menu ──
  await p.locator("aside").first().waitFor({ timeout: TIMEOUT });
  check("menu: Arquivos de mídia", (await p.locator('aside a[href="/paf/midias"]').count()) > 0);
  check("menu: Programação de mídias", (await p.locator('aside a[href="/paf/programacoes"]').count()) > 0);

  await p.goto(`${WEB}/paf/midias`);
  const nova = p.locator("[data-nova-midia]");
  check("mídias: formulário de envio para quem tem Midias.Create", await visivel(nova));
  const pedidosDeEnvio = [];
  p.on("request", (r) => {
    if (r.url().endsWith("/api/v1/paf/midias/envios") && r.method() === "POST") pedidosDeEnvio.push(r.url());
  });

  // ── .avi refused with the list, nothing asked of the server ──
  await nova.getByLabel("Arquivo").setInputFiles(avi);
  check("avi: recusado com a lista de formatos", await visivel(p.locator("[data-erros-midia]", { hasText: MSG_FORMATO })));
  check("avi: nenhum pedido de envio ao servidor", pedidosDeEnvio.length === 0, String(pedidosDeEnvio.length));
  const recusaServidor = await chamar("POST", "/api/v1/paf/midias/envios", await tokenDe(p), { nomeArquivo: "x.avi", contentType: "video/x-msvideo" });
  check("avi: o servidor também recusa (400 com a lista)", recusaServidor.status === 400 && (await recusaServidor.text()).includes(".mp4, .webm"), String(recusaServidor.status));

  // ── MP4: duration from the metadata, direct PUT with progress, then the confirmation ──
  await nova.getByLabel("Arquivo").setInputFiles(institucional);
  const duracao = nova.getByLabel("Duração (segundos)");
  check("mp4: duração pré-preenchida pelo metadado (7,3 s → 8)", await p.waitForFunction((el) => el.value === "8", await duracao.elementHandle(), { timeout: TIMEOUT }).then(() => true, () => false), await duracao.inputValue());
  await nova.getByLabel("Descrição").fill(`Vídeo institucional ${SUFIXO}`);
  await vigiarProgresso(p);
  const putVideo = p.waitForResponse((r) => r.url().startsWith(S3) && r.request().method() === "PUT", { timeout: TIMEOUT });
  const confirmacao = p.waitForResponse((r) => r.url().endsWith("/api/v1/paf/midias") && r.request().method() === "POST", { timeout: TIMEOUT });
  await nova.getByRole("button", { name: "Enviar e cadastrar" }).click();
  check("mp4: PUT direto ao armazenamento 200", (await putVideo).status() === 200);
  check("mp4: confirmação depois do PUT 200", (await confirmacao).status() === 200);
  const video = linhaMidia(p, `Vídeo institucional ${SUFIXO}`);
  check("mp4: aparece na lista como vídeo de 8 s", await visivel(video.getByText("8 s")), await video.textContent().catch(() => "?"));
  const progresso = await p.evaluate(() => window.__progresso);
  check("mp4: barra de progresso exibida até 100%", progresso.includes("0%") && progresso.includes("100%"), JSON.stringify(progresso));
  check("mp4: formulário limpo depois do envio", (await nova.getByLabel("Descrição").inputValue()) === "");

  // ── PNG: no metadata, the operator types the duration ──
  await nova.getByLabel("Arquivo").setInputFiles(cartaz);
  check("png: pede a duração da imagem", await visivel(nova.getByText("informe por quantos segundos")));
  check("png: duração não pré-preenchida", (await duracao.inputValue()) === "");
  await nova.getByLabel("Descrição").fill(`Cartaz ${SUFIXO}`);
  await duracao.fill("15");
  await nova.getByRole("button", { name: "Enviar e cadastrar" }).click();
  const imagem = linhaMidia(p, `Cartaz ${SUFIXO}`);
  check("png: aparece na lista como imagem", await visivel(imagem.getByText("Imagem")));

  // ── Preview by M04 ──
  await video.getByRole("button", { name: "Pré-visualizar" }).click();
  const tagVideo = video.locator("[data-previa] video");
  check("prévia: vídeo com URL assinada do armazenamento", await visivel(tagVideo));
  const srcAntes = await tagVideo.getAttribute("src");
  check("prévia: a URL é do armazenamento, assinada", srcAntes?.startsWith(S3) && srcAntes.includes("X-Amz-Signature"), srcAntes?.slice(0, 70));
  const duracaoTocada = async () =>
    tagVideo.evaluate(
      (v) =>
        new Promise((resolve) => {
          if (v.readyState >= 1) resolve(v.duration);
          else v.addEventListener("loadedmetadata", () => resolve(v.duration), { once: true });
        }),
    );
  const d1 = await duracaoTocada();
  check("prévia: o navegador toca o arquivo enviado (7,3 s)", Math.abs(d1 - 7.3) < 0.2, String(d1));
  await imagem.getByRole("button", { name: "Pré-visualizar" }).click();
  check(
    "prévia: imagem carregada",
    await p.waitForFunction((el) => el?.complete && el.naturalWidth === 32, await imagem.locator("[data-previa] img").elementHandle({ timeout: TIMEOUT }), { timeout: TIMEOUT }).then(() => true, () => false),
  );

  // ── Swap: same type only; the preview then plays the new file ──
  await video.getByRole("button", { name: "Trocar arquivo" }).click();
  const troca = video.locator("[data-troca-arquivo]");
  await troca.locator('input[type="file"]').setInputFiles(cartaz);
  check("troca: imagem no lugar de vídeo recusada sem envio", await visivel(p.locator("[data-erros-midia]", { hasText: "mantém o tipo da mídia" })));
  // One preview open at a time: the image's replaced the video's.
  await video.getByRole("button", { name: "Pré-visualizar" }).click();
  await visivel(tagVideo);
  const versaoAntes = await video.locator("[data-previa]").getAttribute("data-previa-versao");
  await troca.locator('input[type="file"]').setInputFiles(institucionalNovo);
  check("troca: arquivo trocado", await visivel(p.locator("[data-aviso-midia]", { hasText: "trocado" })));
  check("troca: a pré-visualização relê pela versão nova", await visivel(video.locator(`[data-previa]:not([data-previa-versao="${versaoAntes}"]) video`)));
  const srcDepois = await tagVideo.getAttribute("src");
  check("troca: URL nova (outro objeto)", srcDepois !== srcAntes && srcDepois?.startsWith(S3), srcDepois?.slice(0, 70));
  const d2 = await duracaoTocada();
  check("troca: a prévia toca o arquivo novo (5 s)", Math.abs(d2 - 5) < 0.2, String(d2));
  check("troca: a duração cadastrada não muda", await visivel(video.getByText("8 s")));

  // ── Network failure in the PUT: no mídia created ──
  const antes = (await api("GET", "/api/v1/paf/midias", await tokenDe(p))).length;
  await p.route(`${S3}/**`, (rota) => (rota.request().method() === "PUT" ? rota.abort("internetdisconnected") : rota.continue()));
  await nova.getByLabel("Arquivo").setInputFiles(institucional);
  await nova.getByLabel("Descrição").fill(`Falha ${SUFIXO}`);
  await nova.getByRole("button", { name: "Enviar e cadastrar" }).click();
  check("rede: falha do PUT exibida", await visivel(p.locator("[data-erros-midia]", { hasText: "Falha de rede no envio do arquivo" })));
  await p.unroute(`${S3}/**`);
  const depois = await api("GET", "/api/v1/paf/midias", await tokenDe(p));
  check("rede: nenhuma mídia criada", depois.length === antes && !depois.some((m) => m.descricao === `Falha ${SUFIXO}`), `${antes} -> ${depois.length}`);
  check("rede: o formulário guarda o que foi digitado para tentar de novo", (await nova.getByLabel("Descrição").inputValue()) === `Falha ${SUFIXO}`);

  // ── Edit ──
  await imagem.getByRole("button", { name: "Editar" }).click();
  await p.getByLabel(`Nova duração de Cartaz ${SUFIXO}, em segundos`).fill("20");
  await p.locator(`[data-midia]`).getByRole("button", { name: "Salvar" }).click();
  check("editar: duração nova na lista", await visivel(imagem.getByText("20 s")));

  // ── Display list of the painel station, and the painel's own read ──
  const tAdmin = await tokenDe(p);
  const estacao = (await api("GET", "/api/v1/paf/estacoes", tAdmin)).find((e) => e.tipo === "EmissaoPainel");
  // Starts from an empty list, so a rerun on the same database checks the same order.
  await api("PUT", `/api/v1/paf/estacoes/${estacao.id}/midias`, tAdmin, { estacaoId: estacao.id, midias: [] });
  await p.goto(`${WEB}/paf/estacoes`);
  await p.locator(`[data-estacao="${estacao.id}"]`).getByRole("button", { name: "Editar" }).click();
  const lista =p.locator("[data-lista-midias-painel]");
  check("lista: editor dentro do bloco Painel", await visivel(lista));
  const acrescentar = async (rotulo) => {
    await lista.getByLabel("Acrescentar").selectOption({ label: rotulo });
    await lista.getByRole("button", { name: "Acrescentar à lista" }).click();
  };
  await acrescentar(`Vídeo institucional ${SUFIXO} (8 s)`);
  await acrescentar(`Cartaz ${SUFIXO} (20 s)`);
  check("lista: duração do laço somada", (await lista.locator("[data-duracao-laco]").textContent()) === "28 s", await lista.locator("[data-duracao-laco]").textContent());
  await lista.getByRole("button", { name: "Salvar lista" }).click();
  check("lista: salva", await visivel(lista.locator("[data-aviso-lista]")));
  const ordem = async () => (await api("GET", `/api/v1/paf/estacoes/${estacao.id}/midias`, await tokenDe(p))).itens.map((i) => i.descricao);
  check("lista: gravada na ordem", JSON.stringify(await ordem()) === JSON.stringify([`Vídeo institucional ${SUFIXO}`, `Cartaz ${SUFIXO}`]), JSON.stringify(await ordem()));
  await lista.getByRole("button", { name: `Subir Cartaz ${SUFIXO} (posição 2)` }).click();
  await lista.getByRole("button", { name: "Salvar lista" }).click();
  await visivel(lista.locator("[data-aviso-lista]"));
  check("lista: reordenada", JSON.stringify(await ordem()) === JSON.stringify([`Cartaz ${SUFIXO}`, `Vídeo institucional ${SUFIXO}`]), JSON.stringify(await ordem()));

  const link = await api("POST", `/api/v1/paf/estacoes/${estacao.id}/vinculo`, await tokenDe(p), {});
  const cred = await api("POST", "/api/v1/paf/estacoes/vinculo/resgatar", null, { token: link.token });
  const tPainel = cred.credenciais.find((c) => c.papel === "Painel").accessToken;
  const doPainel = async () => (await api("GET", "/api/v1/paf/estacao/painel/midias", tPainel)).lista.map((i) => i.midiaId);
  const idImagem = (await api("GET", "/api/v1/paf/midias", await tokenDe(p))).find((m) => m.descricao === `Cartaz ${SUFIXO}`).id;
  check("painel: a leitura do painel traz a imagem", (await doPainel()).includes(idImagem));

  // ── Disable: leaves the painel ──
  await p.goto(`${WEB}/paf/midias`);
  await imagem.getByRole("button", { name: "Desativar" }).click();
  check("desativar: marcada inativa", await visivel(imagem.getByText("Inativa", { exact: true })));
  check("desativar: some da leitura do painel", !(await doPainel()).includes(idImagem), JSON.stringify(await doPainel()));
  await imagem.getByRole("button", { name: "Pré-visualizar" }).click();
  check("desativar: sem pré-visualização da inativa", await visivel(imagem.getByText("Mídia inativa: ative para pré-visualizar.")));
  await p.goto(`${WEB}/paf/estacoes`);
  await p.locator(`[data-estacao="${estacao.id}"]`).getByRole("button", { name: "Editar" }).click();
  check("desativar: a lista da estação mostra o item como pulado", await visivel(lista.getByText("inativa: pulada no painel")));
  await p.goto(`${WEB}/paf/midias`);
  await imagem.getByRole("button", { name: "Ativar" }).click();
  await visivel(imagem.getByRole("button", { name: "Desativar" }));
  check("ativar: volta à leitura do painel", (await doPainel()).includes(idImagem));

  // ── Review M1: a list holding an inactive mídia cannot be saved; the screen points at its position ──
  // The PUT takes only active mídias and answers one 400 for the whole list: the screen blocks before asking.
  await api("POST", `/api/v1/paf/midias/${idImagem}/disable`, await tokenDe(p));
  await p.goto(`${WEB}/paf/estacoes`);
  await p.locator(`[data-estacao="${estacao.id}"]`).getByRole("button", { name: "Editar" }).click();
  await visivel(lista.getByText("inativa: pulada no painel"));
  const putsDaLista = [];
  p.on("request", (r) => {
    if (r.method() === "PUT" && r.url().endsWith(`/api/v1/paf/estacoes/${estacao.id}/midias`)) putsDaLista.push(r.url());
  });
  await lista.getByRole("button", { name: `Subir Vídeo institucional ${SUFIXO} (posição 2)` }).click();
  const bloqueio = lista.locator("[data-bloqueio-lista]");
  check("M1: lista alterada com mídia inativa avisa a posição", await visivel(bloqueio), (await bloqueio.count()) > 0 ? await bloqueio.textContent() : "sem aviso");
  check("M1: o aviso aponta a posição da inativa (agora a 2ª)", ((await bloqueio.textContent()) ?? "").includes("(posição 2)"), (await bloqueio.textContent()) ?? "");
  check("M1: Salvar lista bloqueado", await lista.getByRole("button", { name: "Salvar lista" }).isDisabled());
  await lista.getByRole("button", { name: `Tirar Cartaz ${SUFIXO} (posição 2)` }).click();
  check("M1: sem a inativa, o aviso some", (await bloqueio.count()) === 0);
  check("M1: sem a inativa, Salvar lista liberado", await lista.getByRole("button", { name: "Salvar lista" }).isEnabled());
  check("M1: nenhum PUT enquanto bloqueado", putsDaLista.length === 0, `${putsDaLista.length}`);
  await lista.getByRole("button", { name: "Salvar lista" }).click();
  check("M1: salva sem a inativa", await visivel(lista.locator("[data-aviso-lista]")));
  check("M1: gravada só com a ativa", JSON.stringify(await ordem()) === JSON.stringify([`Vídeo institucional ${SUFIXO}`]), JSON.stringify(await ordem()));
  // Back to the state the steps below were written against: the image active and the list as it was.
  await api("POST", `/api/v1/paf/midias/${idImagem}/enable`, await tokenDe(p));
  const idVideo = (await api("GET", "/api/v1/paf/midias", await tokenDe(p))).find((m) => m.descricao === `Vídeo institucional ${SUFIXO}`).id;
  await api("PUT", `/api/v1/paf/estacoes/${estacao.id}/midias`, await tokenDe(p), { estacaoId: estacao.id, midias: [idImagem, idVideo] });
  check("M1: estado anterior restaurado", JSON.stringify(await ordem()) === JSON.stringify([`Cartaz ${SUFIXO}`, `Vídeo institucional ${SUFIXO}`]), JSON.stringify(await ordem()));

  // ── Programações: live calculation, midnight, the four filters, deletion ──
  await p.goto(`${WEB}/paf/programacoes`);
  const criar = async (nome, hora, repeticoes, dias) => {
    await p.getByRole("button", { name: "Nova programação" }).click();
    const form = p.locator("[data-edicao-programacao]");
    await form.getByLabel("Nome").fill(nome);
    await form.getByLabel("Mídia").selectOption({ label: `Vídeo institucional ${SUFIXO} (8 s)` });
    await form.getByLabel("Hora inicial").fill(hora);
    await form.getByLabel("Repetições").fill(repeticoes);
    if (dias !== null) {
      await form.getByLabel("Todos os dias").uncheck();
      for (const d of dias) await form.getByLabel(d, { exact: true }).check();
    }
    return form;
  };
  let form = await criar(`Manhã ${SUFIXO}`, "10:00:00", "3", null);
  check("cálculo: hora final ao vivo (10:00 + 3 × 8 s)", (await form.locator("[data-hora-final-calculada]").textContent()) === "10:00:24", await form.locator("[data-hora-final-calculada]").textContent());
  await form.getByLabel("Repetições").fill("450");
  check("cálculo: acompanha as repetições (450 × 8 s = 1 h)", (await form.locator("[data-hora-final-calculada]").textContent()) === "11:00:00");
  check("cálculo: tempo total", (await form.locator("[data-tempo-total]").textContent()) === "1 h 00 min 00 s", await form.locator("[data-tempo-total]").textContent());
  await form.getByLabel("Hora inicial").fill("23:59:00");
  await form.getByLabel("Repetições").fill("10");
  check("meia-noite: aviso e salvar bloqueado", (await visivel(form.getByText("Termina depois da meia-noite"))) && (await form.getByRole("button", { name: "Salvar" }).isDisabled()));
  await form.getByLabel("Hora inicial").fill("10:00:00");
  await form.getByLabel("Repetições").fill("450");
  await form.getByRole("button", { name: "Salvar" }).click();
  const linhaProg = (nome) => p.locator("[data-programacao]", { hasText: nome });
  check("programação: criada com a hora final calculada pelo servidor", await visivel(linhaProg(`Manhã ${SUFIXO}`).locator("[data-hora-final]", { hasText: "11:00:00" })));
  form = await criar(`Tarde ${SUFIXO}`, "15:00:00", "1", ["Segunda"]);
  await form.getByRole("button", { name: "Salvar" }).click();
  check("programação: segunda criada só às segundas", await visivel(linhaProg(`Tarde ${SUFIXO}`).getByText("Seg", { exact: true })));

  const pesquisa = p.locator("[data-pesquisa-programacao]");
  const pesquisar = async () => {
    const resposta = p.waitForResponse((r) => r.url().includes("/api/v1/paf/programacoes-midia?") && r.request().method() === "GET", { timeout: TIMEOUT });
    await pesquisa.getByRole("button", { name: "Pesquisar" }).click();
    await resposta;
    await p.waitForTimeout(200);
    return p.locator("[data-programacao]").evaluateAll((linhas) => linhas.map((l) => l.querySelector("td")?.textContent));
  };
  const limpar = () => pesquisa.getByRole("button", { name: "Limpar filtros" }).click();
  await pesquisa.getByLabel("Nome contém").fill(`Manhã ${SUFIXO}`);
  check("filtro nome", JSON.stringify(await pesquisar()) === JSON.stringify([`Manhã ${SUFIXO}`]));
  await limpar();
  await pesquisa.getByLabel("No ar à hora").fill("10:30:00");
  let nomes = await pesquisar();
  check("filtro hora: 10:30 dentro da Manhã, fora da Tarde", nomes.includes(`Manhã ${SUFIXO}`) && !nomes.includes(`Tarde ${SUFIXO}`), JSON.stringify(nomes));
  await pesquisa.getByLabel("No ar à hora").fill("11:00:00");
  nomes = await pesquisar();
  check("filtro hora: 11:00 é o fim, fora", !nomes.includes(`Manhã ${SUFIXO}`), JSON.stringify(nomes));
  await limpar();
  await pesquisa.getByLabel("Domingo", { exact: true }).check();
  nomes = await pesquisar();
  check("filtro dias: domingo traz a de todos os dias, não a de segunda", nomes.includes(`Manhã ${SUFIXO}`) && !nomes.includes(`Tarde ${SUFIXO}`), JSON.stringify(nomes));
  await limpar();
  await pesquisa.getByLabel("Período de").fill("2099-01-01");
  await pesquisa.getByLabel("Período até").fill("2099-01-31");
  nomes = await pesquisar();
  check("filtro período: fora do período, nenhuma", !nomes.includes(`Manhã ${SUFIXO}`) && !nomes.includes(`Tarde ${SUFIXO}`), JSON.stringify(nomes));
  await pesquisa.getByLabel("Período de").fill("2099-02-01");
  check("filtro período invertido bloqueado na tela", (await visivel(pesquisa.getByText("não pode ser anterior"))) && (await pesquisa.getByRole("button", { name: "Pesquisar" }).isDisabled()));
  // Clearing applies the empty filters at once (already cached, so no new request).
  await limpar();
  await visivel(linhaProg(`Tarde ${SUFIXO}`));

  await linhaProg(`Tarde ${SUFIXO}`).getByRole("button", { name: "Excluir" }).click();
  check("excluir: pede confirmação", await visivel(linhaProg(`Tarde ${SUFIXO}`).locator("[data-confirmar-exclusao]")));
  await linhaProg(`Tarde ${SUFIXO}`).getByRole("button", { name: "Voltar" }).click();
  check("excluir: voltar mantém", (await linhaProg(`Tarde ${SUFIXO}`).count()) === 1);
  await linhaProg(`Tarde ${SUFIXO}`).getByRole("button", { name: "Excluir" }).click();
  await linhaProg(`Tarde ${SUFIXO}`).getByRole("button", { name: "Confirmar exclusão" }).click();
  check("excluir: some da tabela", await linhaProg(`Tarde ${SUFIXO}`).waitFor({ state: "detached", timeout: TIMEOUT }).then(() => true, () => false));
  const restantes = await api("GET", `/api/v1/paf/programacoes-midia?nome=${encodeURIComponent(`Tarde ${SUFIXO}`)}`, await tokenDe(p));
  check("excluir: o servidor não tem mais", restantes.totalCount === 0, String(restantes.totalCount));
  const noPainel = await api("GET", "/api/v1/paf/estacao/painel/midias", tPainel);
  check(
    "programação: o painel recebe a de hoje, 10:00 a 11:00",
    noPainel.programacoes.some((pr) => pr.inicioSegundos === 36_000 && pr.fimSegundos === 39_600 && pr.repeticoes === 450),
    JSON.stringify(noPainel.programacoes.map((pr) => [pr.inicioSegundos, pr.fimSegundos])),
  );

  // ── Gerente: sees, cannot send ──
  const ctxGerente = await browser.newContext();
  const pg = await entrar(ctxGerente, emailGerente);
  await pg.goto(`${WEB}/paf/midias`);
  check("gerente: vê a lista", await visivel(linhaMidia(pg, `Vídeo institucional ${SUFIXO}`)));
  check("gerente: sem Midias.Create o envio some", (await pg.locator("[data-nova-midia]").count()) === 0);
  check("gerente: sem Midias.Update não troca nem desativa", (await pg.getByRole("button", { name: "Trocar arquivo" }).count()) === 0 && (await pg.getByRole("button", { name: "Desativar" }).count()) === 0);
  const recusa = await chamar("POST", "/api/v1/paf/midias/envios", await tokenDe(pg), { nomeArquivo: "x.mp4", contentType: "video/mp4" });
  check("gerente: POST de envio 403", recusa.status === 403, String(recusa.status));
  await pg.goto(`${WEB}/paf/programacoes`);
  check("gerente: vê as programações", await visivel(pg.locator("[data-programacao]", { hasText: `Manhã ${SUFIXO}` })));
  check("gerente: sem criar, editar nem excluir", (await pg.getByRole("button", { name: /Nova programação|Editar|Excluir/ }).count()) === 0);
  await ctxGerente.close();

  // ── Unidade switch switches the list ──
  const ctxRegional = await browser.newContext();
  const pr = await entrar(ctxRegional, emailRegional);
  await pr.goto(`${WEB}/paf/midias`);
  check("unidade: na Loja Demo a lista tem o vídeo", await visivel(linhaMidia(pr, `Vídeo institucional ${SUFIXO}`)));
  const seletor = pr.locator("[data-paf-seletor-unidade] select");
  const respostaTroca = pr.waitForResponse((r) => r.url().includes("/api/v1/paf/sessao/unidade-corrente"), { timeout: TIMEOUT });
  await seletor.selectOption({ label: nomeOutra });
  const respostaDaTroca = await respostaTroca;
  check("unidade: o servidor aceita a troca", respostaDaTroca.ok(), String(respostaDaTroca.status()));
  check("unidade: na outra unidade a lista é dela (vazia)", await visivel(pr.getByText("Nenhum arquivo nesta unidade.")));
  check("unidade: o vídeo da Loja Demo não aparece", (await linhaMidia(pr, `Vídeo institucional ${SUFIXO}`).count()) === 0);
  await seletor.selectOption({ label: "Loja Demo" });
  check("unidade: de volta, a lista volta", await visivel(linhaMidia(pr, `Vídeo institucional ${SUFIXO}`)));
  await ctxRegional.close();
  await ctxAdmin.close();
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.stack : String(erro));
} finally {
  await browser.close();
  fs.rmSync(pasta, { recursive: true, force: true });
}
console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
