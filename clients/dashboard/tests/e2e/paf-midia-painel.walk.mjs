// M08 walk against a REAL API and a REAL S3 store (RustFS), in the system Chrome: the painel's media layer. No media =
// no media element; the station list alternates video and image without reload; nothing plays before the start
// gesture; a call pauses the video and a burst of 5 calls keeps it paused; an object missing in the store is skipped
// without a message; a programação takes over at its second and gives the screen back at its end; URLs that really
// expired are re-read without a reload; disabling a mídia takes it off at once; a network drop resumes. Review fixes:
// A2, a programação whose file is shorter than its registered duration restarts from 0 without a remount loop; A1,
// the São Paulo midnight re-reads the agenda once (browser clock moved, the server's has no override).
//
// Local runner: deploy/paf/walk-local.sh (header has this walk's invocation).
//
// Run (fresh `seed-demo` database; API with Storage:Provider=s3 against RustFS and SHORT read URLs so expiry happens
// inside the walk: PAF__Midia__MargemLeitura=00:00:40 and PAF__Midia__PisoValidadeUrlLeitura=00:00:01; Vite dev server):
//   PAF_API=http://localhost:5418 PAF_WEB=http://localhost:5518 PAF_S3=http://127.0.0.1:9000 \
//   PAF_S3_BUCKET=<bucket of Storage:S3:Bucket> PAF_SENHA_DEMO=<seed password> node tests/e2e/paf-midia-painel.walk.mjs
// Keys default to RustFS's rustfsadmin (PAF_S3_KEY / PAF_S3_SECRET). The walk creates the bucket when missing.
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import crypto, { randomUUID } from "node:crypto";
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
// The switch is judged against the server second as the painel's own last read placed it on the local clock: the
// 50 ms observer, a render and a timer. (The Docker VM clock behind the database drifts and jumps by seconds against
// this machine's, so a fixed local instant would judge the VM, not the painel.)
const TOLERANCIA_TROCA_MS = 400;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}
const dormir = (ms) => new Promise((r) => setTimeout(r, ms));

async function api(method, path, token, body, extras = {}) {
  const headers = { tenant: TENANT, "X-FSH-App": "dashboard", ...extras };
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

// ── S3 by hand (SigV4, the dashboard has no AWS SDK): create the bucket and delete one object ──
function sha256(dado) {
  return crypto.createHash("sha256").update(dado).digest("hex");
}
async function s3(method, caminho) {
  const url = new URL(S3 + caminho);
  const amzData = new Date().toISOString().replace(/[:-]|\.\d{3}/g, "");
  const dia = amzData.slice(0, 8);
  const hashCorpo = sha256("");
  const cabecalhos = { host: url.host, "x-amz-content-sha256": hashCorpo, "x-amz-date": amzData };
  const nomes = Object.keys(cabecalhos).sort();
  const canonico = [method, url.pathname, "", nomes.map((n) => `${n}:${cabecalhos[n]}\n`).join(""), nomes.join(";"), hashCorpo].join("\n");
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
    signal: AbortSignal.timeout(TIMEOUT),
  });
}

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

// A real WebM (VP8 + Opus) recorded by the browser itself: no ffmpeg on the CI runner, and the file is what Chrome plays.
async function gravarWebm(pagina, segundos) {
  const base64 = await pagina.evaluate(async (s) => {
    const canvas = document.createElement("canvas");
    canvas.width = 320;
    canvas.height = 180;
    const g = canvas.getContext("2d");
    const audio = new AudioContext();
    const oscilador = audio.createOscillator();
    const destino = audio.createMediaStreamDestination();
    oscilador.connect(destino);
    oscilador.start();
    const fluxo = new MediaStream([...canvas.captureStream(25).getVideoTracks(), ...destino.stream.getAudioTracks()]);
    const gravador = new MediaRecorder(fluxo, { mimeType: "video/webm;codecs=vp8,opus" });
    const partes = [];
    gravador.ondataavailable = (e) => partes.push(e.data);
    let quadro = 0;
    const timer = setInterval(() => {
      g.fillStyle = `hsl(${(quadro * 12) % 360},80%,50%)`;
      g.fillRect(0, 0, 320, 180);
      quadro += 1;
    }, 40);
    const parado = new Promise((r) => (gravador.onstop = r));
    gravador.start(250);
    await new Promise((r) => setTimeout(r, s * 1000));
    gravador.stop();
    await parado;
    clearInterval(timer);
    oscilador.stop();
    await audio.close();
    const bytes = new Uint8Array(await new Blob(partes, { type: "video/webm" }).arrayBuffer());
    let binario = "";
    for (const b of bytes) binario += String.fromCharCode(b);
    return btoa(binario);
  }, segundos);
  return Buffer.from(base64, "base64");
}

async function cadastrar(token, descricao, arquivo, contentType, bytes, duracaoSegundos) {
  const envio = await api("POST", "/api/v1/paf/midias/envios", token, { nomeArquivo: arquivo, contentType });
  const put = await fetch(envio.url, { method: "PUT", headers: envio.cabecalhosObrigatorios, body: bytes, signal: AbortSignal.timeout(TIMEOUT) });
  if (!put.ok) throw new Error(`PUT do arquivo -> ${put.status} ${await put.text()}`);
  return api("POST", "/api/v1/paf/midias", token, { envioId: envio.envioId, descricao, duracaoSegundos });
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
  // Every 50 ms: which exhibition is on screen (mount log) and, while sampling, the call highlight and the video.
  window.__exibicoes = [];
  window.__amostras = [];
  window.__amostrar = false;
  // Every media element the painel mounts, caught by the DOM itself (no 50 ms gap), with the playhead at its first
  // "playing": a remount loop or a seek past the file's end shows here even when it is faster than the observer.
  window.__montagens = [];
  new MutationObserver((mudancas) => {
    for (const m of mudancas) {
      for (const no of m.addedNodes) {
        if (!(no instanceof Element)) continue;
        const el = no.matches("[data-midia-painel]") ? no : no.querySelector("[data-midia-painel]");
        if (el === null) continue;
        const montagem = { midia: el.getAttribute("data-midia-painel"), exec: el.getAttribute("data-execucao"), em: Date.now(), t0: null };
        window.__montagens.push(montagem);
        el.addEventListener("playing", () => (montagem.t0 ??= el.currentTime), { once: true });
      }
    }
  }).observe(document, { childList: true, subtree: true });
  let ultima = "";
  setInterval(() => {
    const el = document.querySelector("[data-midia-painel]");
    const chave = el === null ? "" : el.getAttribute("data-execucao");
    if (chave !== ultima) {
      ultima = chave;
      window.__exibicoes.push(
        el === null
          ? { exec: null, midia: null, tag: null, src: null, tocou: false, em: Date.now() }
          : { exec: chave, midia: el.getAttribute("data-midia-painel"), tag: el.tagName, src: el.getAttribute("src"), tocou: false, em: Date.now() },
      );
    }
    // Rendered = an image decoded with pixels, or a video whose playhead moved: what the public actually saw.
    const atual = window.__exibicoes[window.__exibicoes.length - 1];
    if (el !== null && atual !== undefined && !atual.tocou) {
      atual.tocou = el.tagName === "IMG" ? el.complete && el.naturalWidth > 0 : el.currentTime > 0.1;
    }
    if (window.__amostrar) {
      const video = el !== null && el.tagName === "VIDEO" ? el : null;
      window.__amostras.push({
        em: Date.now(),
        destaque: document.querySelector('[data-destaque="sim"]') !== null,
        exec: chave,
        video: video === null ? null : { paused: video.paused, t: video.currentTime },
      });
    }
  }, 50);
};

async function esperarAte(page, fn, arg, timeout = TIMEOUT) {
  return page.waitForFunction(fn, arg, { timeout, polling: 50 }).then(
    () => true,
    () => false,
  );
}
const exibicoes = (page) => page.evaluate(() => window.__exibicoes);
const elementosDeMidia = (page) => page.evaluate(() => document.querySelectorAll("video, audio, [data-midia-painel]").length);
const regiaoDeMidia = (page) => page.locator('section[aria-label="Mídia"]').count();
const navegacoes = (page) => page.evaluate(() => performance.getEntriesByType("navigation").length);

/** São Paulo wall clock of an instant, as the API takes it. */
function emSaoPaulo(ms) {
  const partes = Object.fromEntries(
    new Intl.DateTimeFormat("en-CA", { timeZone: "America/Sao_Paulo", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" })
      .formatToParts(new Date(ms))
      .map((p) => [p.type, p.value]),
  );
  return { data: `${partes.year}-${partes.month}-${partes.day}`, hora: `${partes.hour}:${partes.minute}:${partes.second}` };
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  const bucket = await s3("PUT", `/${BUCKET}`);
  check("setup: bucket do walk existe", bucket.ok || bucket.status === 409, `${bucket.status}`);

  // One token for every person call: the guichê session below is this same login (sessão única).
  const token = (await api("POST", "/api/v1/identity/token/issue", null, { email: EMAIL, password: SENHA_DEMO })).accessToken;
  const estacoes = await api("GET", "/api/v1/paf/estacoes", token);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const guiche = estacoes.find((e) => e.tipo === "Guiche");
  const filaId = totem.filas.find((f) => f.papel === "Emissao").filaId;
  const prioridadeId = (await api("GET", "/api/v1/paf/prioridades", token)).find((p) => p.nome === "Normal").id;
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}`, token, {
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
      // A 2 s highlight keeps the burst of step 4 short.
      painel: {
        ...(totem.configuracao?.painel ?? { mostrarDataHora: false, tamanhoFontePx: null, corFundo: null, corFonteFilas: null, corFonteChamada: null, corFonteAlternada: null, modo: "Informativo" }),
        tempoExibicaoChamadaSegundos: 2,
      },
    },
  });
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}/midias`, token, { midias: [] });

  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, token, {});
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  await ctx.addInitScript(STUBS);
  const pVinculo = await ctx.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  const emissao = await pVinculo.evaluate(() => JSON.parse(localStorage.getItem("paf.estacao.emissao")));
  // The ficha is emitted now so it is already callable (5 s rule) when step 4 needs it.
  const ficha = await api("POST", "/api/v1/paf/estacao/emissao/fichas", emissao.accessToken, { filaId, prioridadeId }, { "Idempotency-Key": randomUUID() });
  check("setup: ficha emitida pelo totem", typeof ficha.atendimentoId === "string", JSON.stringify(ficha).slice(0, 120));

  const credPainel = await pVinculo.evaluate(() => JSON.parse(localStorage.getItem("paf.estacao.painel")));
  // The painel follows the SERVER clock (database), which may differ from this machine's (Docker VM): the switch
  // instants are judged on the server clock, estimated from the fastest of a few reads.
  const medirRelogioServidor = async () => {
    let melhor = null;
    for (let i = 0; i < 8; i++) {
      const ida = Date.now();
      const resposta = await api("GET", "/api/v1/paf/estacao/painel/midias", credPainel.accessToken);
      const volta = Date.now();
      console.log(`DIAG leitura do node: desvio ${Date.parse(resposta.agora) - ida} ms, ida e volta ${volta - ida} ms`);
      if (melhor === null || volta - ida < melhor.rtt) melhor = { rtt: volta - ida, desvio: Date.parse(resposta.agora) - ida };
    }
    return melhor;
  };
  const pPainel = await ctx.newPage();
  const respostasS3 = [];
  pPainel.on("response", (r) => {
    if (r.url().startsWith(S3)) respostasS3.push({ url: r.url(), status: r.status(), em: Date.now() });
  });
  const pedidosS3 = [];
  pPainel.on("request", (r) => {
    if (r.url().startsWith(S3)) pedidosS3.push({ url: r.url(), em: Date.now() });
  });
  // An image whose store answer is an error (XML body) is cut by Chrome's ORB: it shows up as a failed request, not a
  // response; both count as the store refusing.
  const recusasS3 = [];
  pPainel.on("requestfailed", (r) => {
    if (r.url().startsWith(S3)) recusasS3.push({ url: r.url(), erro: r.failure()?.errorText ?? "?", em: Date.now() });
  });
  pPainel.on("response", (r) => {
    if (r.url().startsWith(S3) && r.status() >= 400) recusasS3.push({ url: r.url(), erro: `${r.status()}`, em: Date.now() });
  });
  const pulos = [];
  pPainel.on("console", (m) => {
    if (m.text().includes("[paf-painel] mídia não tocou")) pulos.push(Date.now());
    if (m.text().includes("[paf-painel]")) console.log(`DIAG console ${m.type()}: ${m.text().slice(0, 220)}`);
  });
  const leiturasMidias = [];
  const temposLeitura = [];
  pPainel.on("request", (r) => {
    if (r.url().endsWith("/api/v1/paf/estacao/painel/midias")) leiturasMidias.push(Date.now());
  });
  // The painel's view of the server clock, read by read: server instant minus the moment the request left.
  const saidasLeitura = new Map();
  const desviosPainel = [];
  pPainel.on("request", (r) => {
    if (r.url().endsWith("/api/v1/paf/estacao/painel/midias")) saidasLeitura.set(r, Date.now());
  });
  pPainel.on("requestfinished", async (r) => {
    if (!r.url().endsWith("/api/v1/paf/estacao/painel/midias")) return;
    const recebidoEm = Date.now();
    temposLeitura.push(Math.round(r.timing().responseEnd));
    const resposta = await r.response();
    if (resposta === null) return;
    const corpo = await resposta.json();
    desviosPainel.push({ recebidoEm, desvio: Date.parse(corpo.agora) - saidasLeitura.get(r) });
  });
  /**
   * Local instant the painel should switch for a server instant: each read's offset holds until the next read lands,
   * and a read that lands after the instant it now places in the past switches at once (it cannot switch earlier).
   */
  const trocaEsperada = (servidorMs) => {
    for (let i = 0; i < desviosPainel.length; i++) {
      const candidata = Math.max(desviosPainel[i].recebidoEm, servidorMs - desviosPainel[i].desvio);
      if (i + 1 === desviosPainel.length || candidata < desviosPainel[i + 1].recebidoEm) return candidata;
    }
    return Number.NaN;
  };
  await pPainel.goto(`${WEB}/paf/painel`);

  // ── 1. No media: no media element, before and after the gesture ──
  await pPainel.getByRole("button", { name: "Iniciar painel" }).waitFor({ timeout: TIMEOUT });
  await dormir(1_500);
  check("sem mídia, antes do gesto: nenhum elemento de mídia", (await elementosDeMidia(pPainel)) === 0);
  await pPainel.getByRole("button", { name: "Iniciar painel" }).click();
  await dormir(3_000);
  check("sem mídia: leitura das mídias feita", leiturasMidias.length > 0, `${leiturasMidias.length}`);
  check("sem mídia: nenhum elemento de mídia no DOM", (await elementosDeMidia(pPainel)) === 0);
  check("sem mídia: sem região de mídia (senhas em tela cheia)", (await regiaoDeMidia(pPainel)) === 0);

  // ── 2. Video and image alternate, without reload ──
  const pGravador = await (await browser.newContext()).newPage();
  await pGravador.goto(`${WEB}/login`);
  const webm = await gravarWebm(pGravador, 4);
  await pGravador.context().close();
  check("setup: vídeo WebM gravado pelo navegador", webm.length > 1_000, `${webm.length} bytes`);
  const video = await cadastrar(token, "Vídeo institucional", "institucional.webm", "video/webm", webm, 3);
  const imagem = await cadastrar(token, "Promoção", "promocao.png", "image/png", png(0xc0, 0x20, 0x20, 64), 3);
  const urlTeste = await api("GET", `/api/v1/paf/midias/${imagem.id}/url`, token);
  const validadeS = (Date.parse(urlTeste.expiraEm) - Date.now()) / 1000;
  check("setup: URL de leitura curta (MargemLeitura do walk aplicada)", validadeS > 30 && validadeS < 60, `${validadeS} s`);
  check("setup: vídeo e imagem cadastrados (M03, RustFS)", video.tipo === "Video" && imagem.tipo === "Imagem", `${video.tipo} ${imagem.tipo}`);
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}/midias`, token, { midias: [video.id, imagem.id] });

  const alternou = await esperarAte(
    pPainel,
    ([v, i]) => {
      const seq = window.__exibicoes.filter((e) => e.midia !== null).map((e) => `${e.tag}:${e.midia}`);
      const alvo = [`VIDEO:${v}`, `IMG:${i}`, `VIDEO:${v}`];
      for (let k = 0; k + alvo.length <= seq.length; k++) if (alvo.every((a, j) => seq[k + j] === a)) return true;
      return false;
    },
    [video.id, imagem.id],
    30_000,
  );
  const seq = (await exibicoes(pPainel)).filter((e) => e.midia !== null);
  check("lista: vídeo, imagem e vídeo de novo, sem recarregar", alternou && (await navegacoes(pPainel)) === 1, seq.map((e) => e.tag).join(","));
  const iVideo = seq.findIndex((e) => e.midia === video.id);
  const duracaoVideoMs = iVideo >= 0 && seq[iVideo + 1] ? seq[iVideo + 1].em - seq[iVideo].em : -1;
  // The file lasts 4 s and the cadastro says 3 s: the screen stops at whichever comes first.
  check("lista: o vídeo de 4 s para na duração cadastrada de 3 s", duracaoVideoMs >= 2_500 && duracaoVideoMs < 3_900, `${duracaoVideoMs} ms`);
  const iImagem = seq.findIndex((e) => e.midia === imagem.id);
  const duracaoImagemMs = iImagem >= 0 && seq[iImagem + 1] ? seq[iImagem + 1].em - seq[iImagem].em : -1;
  check("lista: a imagem fica a sua duração (3 s)", duracaoImagemMs >= 2_800 && duracaoImagemMs < 3_600, `${duracaoImagemMs} ms`);
  check("lista: região de mídia presente com item", (await regiaoDeMidia(pPainel)) === 1);

  // ── 3. Nothing plays before the gesture, even with a list ──
  const pSemGesto = await ctx.newPage();
  await pSemGesto.goto(`${WEB}/paf/painel`);
  await pSemGesto.getByRole("button", { name: "Iniciar painel" }).waitFor({ timeout: TIMEOUT });
  await dormir(4_000);
  check("antes do gesto, com lista: nenhum elemento de mídia (nada toca com som)", (await elementosDeMidia(pSemGesto)) === 0);
  await pSemGesto.close();

  // ── 4. A call pauses the video; a burst of 5 calls keeps it paused; it resumes after ──
  await api("POST", "/api/v1/paf/sessao/guiche", token, { estacaoId: guiche.id, liberarPendencia: true });
  const videoNoInicio = await esperarAte(
    pPainel,
    (v) => {
      const el = document.querySelector(`video[data-midia-painel="${v}"]`);
      return el !== null && !el.paused && el.currentTime > 0.2 && el.currentTime < 1.2;
    },
    video.id,
    30_000,
  );
  check("chamada: vídeo tocando quando a chamada chega", videoNoInicio);
  await pPainel.evaluate(() => {
    window.__amostras = [];
    window.__amostrar = true;
  });
  const chamadaEm = Date.now();
  const chamada = await api("POST", "/api/v1/paf/guiche/chamar-proximo", token);
  // Each recall lands after the previous one left the announcement queue (a recall of a senha still waiting there is
  // dropped, RF21), so the five 2 s highlights run back to back: 0-2, 2-4, 4-6, 6-8, 8-10 s.
  for (const emMs of [1_000, 2_500, 4_500, 6_500]) {
    await dormir(Math.max(0, chamadaEm + emMs - Date.now()));
    await api("POST", `/api/v1/paf/guiche/atendimentos/${chamada.atendimentoId}/rechamar`, token);
  }
  // The last highlight ends 2 s after its turn; then the video must resume.
  const retomou = await esperarAte(
    pPainel,
    () => {
      const el = document.querySelector("video[data-midia-painel]");
      return document.querySelector('[data-destaque="sim"]') === null && el !== null && !el.paused;
    },
    undefined,
    30_000,
  );
  await dormir(500);
  const amostras = await pPainel.evaluate(() => {
    window.__amostrar = false;
    return window.__amostras;
  });
  const comDestaque = amostras.filter((a) => a.destaque);
  const primeiro = amostras.findIndex((a) => a.destaque);
  const ultimo = amostras.findLastIndex((a) => a.destaque);
  const rajada = primeiro >= 0 ? amostras.slice(primeiro, ultimo + 1) : [];
  check("chamada: destaque apareceu", comDestaque.length > 0, `${comDestaque.length} amostras`);
  check(
    "chamada: vídeo pausado em toda amostra com destaque",
    comDestaque.length > 0 && comDestaque.every((a) => a.video !== null && a.video.paused),
    JSON.stringify(comDestaque.find((a) => a.video === null || !a.video.paused) ?? null),
  );
  check(
    "5 chamadas: o vídeo não retoma entre um destaque e o próximo",
    rajada.length > 0 && rajada.every((a) => a.video !== null && a.video.paused),
    `${((amostras[ultimo]?.em ?? 0) - (amostras[primeiro]?.em ?? 0)) / 1000} s de rajada, ${rajada.filter((a) => a.video === null || !a.video.paused).length} amostras sem pausa`,
  );
  check("5 chamadas: o mesmo vídeo segue na tela (não foi pulado)", new Set(rajada.map((a) => a.exec)).size === 1, [...new Set(rajada.map((a) => a.exec))].join(","));
  check("5 chamadas: a rajada durou as 5 chamadas (≥ 8 s)", (amostras[ultimo]?.em ?? 0) - (amostras[primeiro]?.em ?? 0) >= 8_000);
  const posicoes = rajada.map((a) => a.video?.t ?? -1);
  check("chamada: o vídeo ficou parado no mesmo ponto durante a rajada", Math.max(...posicoes) - Math.min(...posicoes) < 0.3, `${Math.min(...posicoes)}..${Math.max(...posicoes)}`);
  check("chamada: o vídeo retoma depois do último destaque", retomou);
  await api("POST", `/api/v1/paf/guiche/atendimentos/${chamada.atendimentoId}/iniciar`, token);
  await api("POST", `/api/v1/paf/guiche/atendimentos/${chamada.atendimentoId}/encerrar`, token);

  // ── 5. An object missing in the store is skipped silently ──
  const sumida = await cadastrar(token, "Arquivo apagado", "apagado.png", "image/png", png(0x20, 0xc0, 0x20, 64), 3);
  const urlSumida = await api("GET", `/api/v1/paf/midias/${sumida.id}/url`, token);
  const apagar = await s3("DELETE", new URL(urlSumida.url).pathname);
  check("setup: objeto apagado direto no armazenamento", apagar.status === 204 || apagar.ok, `${apagar.status}`);
  const alertasAntes = await pPainel.locator('[role="alert"]').count();
  const trocaListaEm = Date.now();
  const caminhoSumida = new URL(urlSumida.url).pathname;
  const naLoja = await fetch(urlSumida.url, { signal: AbortSignal.timeout(TIMEOUT) });
  check("objeto inexistente: a URL assinada responde 404 no armazenamento", naLoja.status === 404, `${naLoja.status}`);
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}/midias`, token, { midias: [video.id, sumida.id, imagem.id] });
  // The failure is faster than the 50 ms observer: judge by the refused request and what comes right after it.
  const recusa = await (async () => {
    const limite = Date.now() + 30_000;
    while (Date.now() < limite) {
      const r = recusasS3.find((x) => x.em >= trocaListaEm && new URL(x.url).pathname === caminhoSumida);
      if (r !== undefined) return r;
      await dormir(100);
    }
    return null;
  })();
  check("objeto inexistente: o painel tentou o item e o armazenamento recusou", recusa !== null, recusa?.erro);
  await esperarAte(pPainel, ([i, desde]) => window.__exibicoes.some((e) => e.midia === i && e.em > desde), [imagem.id, recusa?.em ?? Date.now()], 15_000);
  const depoisDaRecusa = recusa === null ? [] : (await exibicoes(pPainel)).filter((e) => e.em >= recusa.em - 60);
  const sumidaNaTela = (await exibicoes(pPainel)).filter((e, k, todas) => e.midia === sumida.id && (todas[k + 1]?.em ?? Date.now()) - e.em >= 1_000);
  // The first refusal may be the preload, while the video still plays; what counts is how fast the screen leaves the
  // missing item once it is its turn (the observer may not even catch it: the failure takes a few ms).
  const kApagado = depoisDaRecusa.findIndex((e) => e.midia === sumida.id);
  const seguinte = kApagado >= 0 ? depoisDaRecusa[kApagado + 1] : depoisDaRecusa.find((e) => e.midia === imagem.id);
  check(
    "objeto inexistente: pulado, a imagem seguinte entra em menos de 1 s",
    seguinte !== undefined && seguinte.midia === imagem.id && (kApagado < 0 || seguinte.em - depoisDaRecusa[kApagado].em < 1_000),
    depoisDaRecusa.map((e) => `${e.midia === sumida.id ? "apagado" : e.tag}+${e.em - (recusa?.em ?? 0)}ms`).join(","),
  );
  check("objeto inexistente: nunca fica 1 s na tela", sumidaNaTela.length === 0);
  check("objeto inexistente: nenhuma mensagem ao público", (await pPainel.locator('[role="alert"]').count()) === alertasAntes);
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}/midias`, token, { midias: [video.id, imagem.id] });

  // ── 6. A programação takes over at its second and gives the screen back at its end (D12) ──
  const programada = await cadastrar(token, "Programada", "programada.png", "image/png", png(0x20, 0x20, 0xc0, 64), 5);
  // Every programação the walk creates, so step 10 can take them off the painel's day.
  const programacoesDoWalk = [];
  const relogio = await medirRelogioServidor();
  console.log(`DIAG relógio do servidor: desvio ${relogio.desvio} ms (ida e volta ${relogio.rtt} ms); leituras do painel (ms): ${temposLeitura.slice(-12).join(",")}`);
  // Server wall-clock second of the start, and the local instants it and the end happen at.
  const inicioServidorMs = Math.ceil((Date.now() + relogio.desvio + 15_000) / 1000) * 1000;
  const inicio = emSaoPaulo(inicioServidorMs);
  const inicioMs = inicioServidorMs - relogio.desvio;
  const fimMs = inicioMs + 10_000;
  if (emSaoPaulo(inicioServidorMs + 10_000).data !== inicio.data) {
    check("programação: janela cabe no dia (rode fora da virada da meia-noite)", false, inicio.hora);
  } else {
    const criada = await api("POST", "/api/v1/paf/programacoes-midia", token, {
      midiaId: programada.id,
      nome: "Walk M08",
      dataInicial: inicio.data,
      dataFinal: inicio.data,
      horaInicial: inicio.hora,
      repeticoes: 2,
      dias: 127,
    });
    programacoesDoWalk.push(criada.id);
    console.log(`DIAG programação ${inicio.hora} total=${criada.tempoTotalSegundos} fim=${criada.horaFinalSegundos}`);
    const entrou = await esperarAte(pPainel, (p) => document.querySelector(`[data-midia-painel="${p}"]`) !== null, programada.id, 30_000);
    // Judged on the observer's log, not on the DOM: the switch and the observer's next 50 ms tick are not atomic.
    const saiu =
      entrou &&
      (await esperarAte(
        pPainel,
        (p) => {
          const k = window.__exibicoes.findIndex((e) => e.midia === p);
          return k >= 0 && window.__exibicoes.slice(k).some((e) => e.midia !== p);
        },
        programada.id,
        20_000,
      ));
    console.log(`DIAG leituras do painel (ms): ${temposLeitura.slice(-8).join(",")}; desvios vistos pelo painel: ${desviosPainel.slice(-12).map((d) => Math.round(d.desvio)).join(",")}`);
    const seqP = (await exibicoes(pPainel)).filter((e) => e.em >= inicioMs - 15_000);
    console.log(`DIAG exibições ${JSON.stringify(seqP.map((e) => [e.tag, e.midia === programada.id ? "P" : e.midia === video.id ? "V" : e.midia === imagem.id ? "I" : e.midia, e.em - inicioMs]))}`);
    const entrada = seqP.find((e) => e.midia === programada.id);
    const saida = seqP.find((e) => entrada !== undefined && e.em > entrada.em && e.midia !== programada.id);
    const entradaEsperada = trocaEsperada(inicioServidorMs);
    const saidaEsperada = trocaEsperada(inicioServidorMs + 10_000);
    check(
      "programação: entra no segundo da hora inicial (relógio do servidor)",
      entrou && entrada !== undefined && Math.abs(entrada.em - entradaEsperada) <= TOLERANCIA_TROCA_MS,
      `${Math.round((entrada?.em ?? 0) - entradaEsperada)} ms de ${inicio.hora} no servidor (${(entrada?.em ?? 0) - inicioMs} ms do relógio desta máquina corrigido no início)`,
    );
    check(
      "programação: sai no fim da janela, 2 × 5 s (relógio do servidor)",
      saiu && saida !== undefined && Math.abs(saida.em - saidaEsperada) <= TOLERANCIA_TROCA_MS,
      `${Math.round((saida?.em ?? 0) - saidaEsperada)} ms`,
    );
    check("programação: a lista volta depois da janela", saida !== undefined && (saida.midia === video.id || saida.midia === imagem.id), saida?.midia ?? "nada");
  }

  // ── 6b. Review A2: a programação whose file (4 s) is shorter than its registered duration (7 s) restarts from 0 ──
  // Before the fix each end joined the window at its offset, past the end of the file: the player remounted in a loop
  // until the next repetition. Now each end is a new run from 0: in a 2 × 7 s window, at most ceil(14 / 4) runs.
  const curta = await cadastrar(token, "Programada curta", "programada-curta.webm", "video/webm", webm, 7);
  const caminhoCurta = new URL((await api("GET", `/api/v1/paf/midias/${curta.id}/url`, token)).url).pathname;
  const relogioA2 = await medirRelogioServidor();
  const inicioA2ServidorMs = Math.ceil((Date.now() + relogioA2.desvio + 15_000) / 1000) * 1000;
  const inicioA2 = emSaoPaulo(inicioA2ServidorMs);
  const inicioA2Ms = inicioA2ServidorMs - relogioA2.desvio;
  if (emSaoPaulo(inicioA2ServidorMs + 14_000).data !== inicioA2.data) {
    check("A2: janela cabe no dia (rode fora da virada da meia-noite)", false, inicioA2.hora);
  } else {
    const criadaA2 = await api("POST", "/api/v1/paf/programacoes-midia", token, {
      midiaId: curta.id,
      nome: "Walk M08 A2",
      dataInicial: inicioA2.data,
      dataFinal: inicioA2.data,
      horaInicial: inicioA2.hora,
      repeticoes: 2,
      dias: 127,
    });
    programacoesDoWalk.push(criadaA2.id);
    const entrouA2 = await esperarAte(pPainel, (p) => document.querySelector(`[data-midia-painel="${p}"]`) !== null, curta.id, 30_000);
    const saiuA2 =
      entrouA2 &&
      (await esperarAte(pPainel, (p) => document.querySelector(`[data-midia-painel="${p}"]`) === null, curta.id, 30_000));
    const montagensA2 = (await pPainel.evaluate(() => window.__montagens)).filter((m) => m.midia === curta.id);
    const getsA2 = pedidosS3.filter((r) => new URL(r.url).pathname === caminhoCurta && r.em >= inicioA2Ms - 2_000);
    console.log(`DIAG A2 montagens ${JSON.stringify(montagensA2.map((m) => [m.exec, m.em - inicioA2Ms, m.t0 === null ? null : Number(m.t0.toFixed(2))]))}; GETs ao armazenamento ${getsA2.length}`);
    check("A2: a programação curta entra e devolve a tela", entrouA2 && saiuA2);
    check("A2: sem laço de remontagem (no máximo ceil(14 / 4) = 4 execuções na janela)", montagensA2.length >= 2 && montagensA2.length <= 4, `${montagensA2.length} montagens`);
    check(
      "A2: cada repetição recomeça do 0 do arquivo e toca",
      montagensA2.slice(1).every((m) => m.t0 !== null && m.t0 < 0.5),
      montagensA2.map((m) => (m.t0 === null ? "não tocou" : m.t0.toFixed(2))).join(","),
    );
    check("A2: GETs ao armazenamento sem laço (até 3 por execução)", getsA2.length <= 3 * Math.max(1, montagensA2.length), `${getsA2.length} GETs, ${montagensA2.length} execuções`);
    check("A2: sem recarregar", (await navegacoes(pPainel)) === 1);
  }

  // ── 7. URLs are renewed before they expire; URLs that really expired are re-read without reload ──
  // With PAF:Midia:MargemLeitura = 40 s a URL lives 43 s: by now the painel must have played with several signatures.
  const assinaturas = new Set(respostasS3.filter((r) => r.status === 200 || r.status === 206).map((r) => new URL(r.url).searchParams.get("X-Amz-Date")));
  check("renovação: a mídia tocou com várias assinaturas sem recarregar", assinaturas.size >= 3 && (await navegacoes(pPainel)) === 1, `${assinaturas.size} assinaturas, ${leiturasMidias.length} leituras, recusas até aqui: ${recusasS3.length}`);
  await pPainel.route("**/api/v1/paf/estacao/painel/midias", (r) => r.abort("connectionrefused"));
  const bloqueadoEm = Date.now();
  // A failed request is not always the store refusing: switching items aborts a video's pending range request
  // (net::ERR_ABORTED) with a URL still valid. Each candidate is checked outside the browser; the first the store
  // really refuses (403) is the expired one.
  const conferidas = [];
  const { vencida, prova, corpoProva } = await (async () => {
    const limite = Date.now() + 90_000;
    while (Date.now() < limite) {
      for (const r of recusasS3.filter((x) => x.em > bloqueadoEm && !conferidas.includes(x))) {
        conferidas.push(r);
        const resposta = await fetch(r.url, { signal: AbortSignal.timeout(TIMEOUT) });
        const corpo = await resposta.text();
        if (resposta.status === 403) return { vencida: r, prova: resposta, corpoProva: corpo };
      }
      await dormir(250);
    }
    return { vencida: null, prova: null, corpoProva: "" };
  })();
  check(
    "expiração: o armazenamento recusou uma URL vencida (403 real, conferido fora do navegador)",
    prova !== null && prova.status === 403,
    `${vencida?.erro} / ${prova?.status} ${corpoProva.slice(0, 120)}; falhas descartadas por URL ainda válida: ${conferidas.length - (vencida === null ? 0 : 1)}`,
  );
  await dormir(5_000);
  // An exhibition mounted after the block with a URL the store refused stays under 1.5 s (often under the 50 ms
  // observer); the painel's own skip log proves an expired item did reach its turn.
  // Every exhibition since the block either rendered (decoded image, moving video) or left within 1.5 s; the painel's
  // own skip log proves an expired item did reach its turn.
  const todas = await exibicoes(pPainel);
  const semImagem = todas
    .map((e, k) => ({ ...e, ficou: (todas[k + 1]?.em ?? Date.now()) - e.em }))
    .filter((e) => e.em > bloqueadoEm && e.exec !== null && !e.tocou && e.ficou >= 1_500);
  check(
    "expiração: item com URL vencida pulado, nada fica na tela sem imagem (sem tela preta)",
    pulos.some((t) => t > bloqueadoEm) && semImagem.length === 0,
    `${pulos.filter((t) => t > bloqueadoEm).length} pulos; sem imagem por 1,5 s: ${semImagem.map((e) => `${e.tag} ${e.ficou}ms`).join(",") || "nenhuma"}`,
  );
  check("expiração: nenhuma mensagem ao público", (await pPainel.locator('[role="alert"]').count()) === alertasAntes);
  await pPainel.unroute("**/api/v1/paf/estacao/painel/midias");
  const desbloqueadoEm = Date.now();
  const voltou = await esperarAte(
    pPainel,
    (v) => {
      const el = document.querySelector(`video[data-midia-painel="${v}"]`);
      return el !== null && !el.paused && el.currentTime > 0.3;
    },
    video.id,
    90_000,
  );
  const urlNova = respostasS3.find((r) => r.em > desbloqueadoEm && (r.status === 200 || r.status === 206) && r.url.includes(new URL(vencida?.url ?? S3).pathname));
  console.log(
    `DIAG expiração: bloqueado ${(desbloqueadoEm - bloqueadoEm) / 1000} s; leituras após desbloquear em ${leiturasMidias.filter((l) => l > desbloqueadoEm).map((l) => l - desbloqueadoEm).join(",") || "nenhuma"} ms; ` +
      `S3 após desbloquear ${respostasS3.filter((r) => r.em > desbloqueadoEm).map((r) => r.status).join(",") || "nada"}; ` +
      `tela ${JSON.stringify((await exibicoes(pPainel)).slice(-4).map((e) => [e.tag, e.exec, e.tocou, e.em - desbloqueadoEm]))}`,
  );
  check("expiração: releitura traz URL nova e o vídeo volta a tocar sem recarregar", vencida !== null && voltou && urlNova !== undefined && urlNova.url !== vencida.url && (await navegacoes(pPainel)) === 1, `${(Date.now() - desbloqueadoEm) / 1000} s`);

  // ── 8. Disabling a mídia takes it off without reload ──
  await esperarAte(pPainel, (v) => document.querySelector(`video[data-midia-painel="${v}"]`) !== null, video.id, 30_000);
  const desativadoEm = Date.now();
  await api("POST", `/api/v1/paf/midias/${video.id}/disable`, token);
  const sumiu = await esperarAte(pPainel, (v) => document.querySelector(`[data-midia-painel="${v}"]`) === null, video.id, 5_000);
  check("desativar: o vídeo some sem recarregar", sumiu && (await navegacoes(pPainel)) === 1, `${(Date.now() - desativadoEm) / 1000} s`);
  await dormir(8_000);
  const depois = (await exibicoes(pPainel)).filter((e) => e.em > desativadoEm + 1_000 && e.midia !== null);
  check("desativar: só a imagem segue no laço", depois.length > 0 && depois.every((e) => e.midia === imagem.id), depois.map((e) => e.tag).join(","));

  // ── 9. Network drop: the painel resumes by itself ──
  await ctx.setOffline(true);
  await dormir(20_000);
  await ctx.setOffline(false);
  const conectado = await esperarAte(pPainel, () => document.querySelector('[data-conexao="conectado"]') !== null, undefined, 60_000);
  const retomouRede = await esperarAte(pPainel, () => document.querySelector("[data-midia-painel]") !== null, undefined, 60_000);
  check("rede: volta conectado", conectado);
  check("rede: a mídia volta a tocar sem recarregar", retomouRede && (await navegacoes(pPainel)) === 1);

  // ── 10. Review A1: crossing the São Paulo midnight re-reads once, never in a loop ──
  // The server clock has no override (TimeProvider.System, PostgreSQL now()), so the painel's view of it is moved: the
  // agenda places the server clock at local clock + the offset measured at its read, and a fake browser clock jumped
  // forward after that read makes the painel believe the server is 10 s before midnight. With no item and no
  // programação nothing else re-reads on the way (URL renewals need items); a read that still happens during the jump
  // (hub reconnection after the fake hours) is refused, so the offset stays the one measured before the jump.
  await api("PUT", `/api/v1/paf/estacoes/${totem.id}/midias`, token, { midias: [] });
  for (const id of programacoesDoWalk) await api("POST", `/api/v1/paf/programacoes-midia/${id}/disable`, token);
  await pPainel.close();
  const pMeiaNoite = await ctx.newPage();
  await pMeiaNoite.clock.install();
  const leiturasA1 = [];
  pMeiaNoite.on("request", (r) => {
    if (r.url().endsWith("/api/v1/paf/estacao/painel/midias")) leiturasA1.push({ em: Date.now(), r });
  });
  await pMeiaNoite.goto(`${WEB}/paf/painel`);
  await pMeiaNoite.getByRole("button", { name: "Iniciar painel" }).click();
  // The fake clock replaces `performance` too, so a reload is caught by a mark on the window instead.
  await pMeiaNoite.evaluate(() => {
    window.__semRecarregar = true;
  });
  await esperarAte(pMeiaNoite, () => document.querySelector('[data-conexao="conectado"]') !== null, undefined, 30_000);
  await dormir(3_000);
  const ultimaA1 = leiturasA1.at(-1);
  const respostaA1 = ultimaA1 === undefined ? null : await ultimaA1.r.response();
  const corpoA1 = respostaA1 === null ? null : await respostaA1.json();
  const vazia = corpoA1 !== null && corpoA1.lista.length === 0 && corpoA1.programacoes.length === 0;
  check("A1 setup: painel leu uma agenda sem itens nem programações", vazia, corpoA1 === null ? "sem leitura" : `${corpoA1.lista.length} itens, ${corpoA1.programacoes.length} programações`);
  if (corpoA1 !== null) {
    // Same reading as the painel: the wall-clock part of `agora` is São Paulo's.
    const [, hh, mm, ss] = /T(\d{2}):(\d{2}):(\d{2})/.exec(corpoA1.agora);
    const meiaNoiteServidorMs = Date.parse(corpoA1.agora) - (Number(hh) * 3600 + Number(mm) * 60 + Number(ss)) * 1000 + 86_400_000;
    const desvioA1 = Date.parse(corpoA1.agora) - ultimaA1.em;
    await pMeiaNoite.route("**/api/v1/paf/estacao/painel/midias", (r) => r.abort("connectionrefused"));
    const antesDoSalto = leiturasA1.length;
    const saltoMs = Math.floor(meiaNoiteServidorMs - (Date.now() + desvioA1) - 10_000);
    const localAntes = await pMeiaNoite.evaluate(() => Date.now());
    await pMeiaNoite.clock.fastForward(saltoMs);
    const saltoEm = Date.now();
    const localDepois = await pMeiaNoite.evaluate(() => Date.now());
    console.log(`DIAG A1 salto ${saltoMs} ms (relógio do navegador andou ${localDepois - localAntes} ms); leituras recusadas no salto: ${leiturasA1.length - antesDoSalto}`);
    await dormir(1_000);
    await pMeiaNoite.unroute("**/api/v1/paf/estacao/painel/midias");
    const aposSalto = leiturasA1.length;
    // 10 s to midnight on the painel's view of the server clock, then 20 s past it.
    await dormir(30_000);
    const naVirada = leiturasA1.slice(aposSalto);
    const relogioNavegador = await pMeiaNoite.evaluate(() => Date.now());
    console.log(`DIAG A1 leituras na virada ${naVirada.length}; navegador ${new Date(relogioNavegador + desvioA1).toISOString()} no relógio do servidor`);
    check("A1: o relógio do navegador passou a meia-noite de São Paulo do servidor", relogioNavegador + desvioA1 > meiaNoiteServidorMs + 5_000, `${relogioNavegador + desvioA1 - meiaNoiteServidorMs} ms depois`);
    check("A1: a virada do dia relê as mídias 1 a 2 vezes, sem laço", naVirada.length >= 1 && naVirada.length <= 2, `${naVirada.length} GET estacao/painel/midias em 30 s`);
    // A read before the painel's midnight (a late hub reconnection) would measure the offset again and hide the turn.
    check(
      "A1: a primeira releitura veio da virada, não de uma reconexão antes dela",
      naVirada.length >= 1 && naVirada[0].em - saltoEm >= 8_000,
      naVirada.map((l) => `${l.em - saltoEm} ms após o salto`).join(","),
    );
    check("A1: sem recarregar", await pMeiaNoite.evaluate(() => window.__semRecarregar === true));
  }
  await pMeiaNoite.close();

  await ctx.close();
} catch (erro) {
  check("walk sem exceção", false, erro instanceof Error ? erro.stack : String(erro));
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU: ${falhas}`);
process.exit(falhas === 0 ? 0 : 1);
