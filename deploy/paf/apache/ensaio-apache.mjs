// WebSocket side of the Apache rehearsal (O03), run by ensaio-apache.sh against the rendered PAF vhost on a local
// Ubuntu 22.04 Apache (2.4.52, the Contabo version) with two SignalR instances behind it. Speaks the SignalR JSON
// protocol by hand, the way the PafHub clients connect: WebSockets only, no negotiate (ARQ seção 11, option B).
//
// Phases, each printing PASS/FAIL:
//   1. one connection: handshake (only reachable after a 101) and WhoAmI reports WebSockets;
//   2. `docker kill` of the instance it landed on: the client reconnects on the other one with the SignalR client's
//      default backoff (0, 2, 10, 30 s); the killed instance is started again and rejoins the pool (retry=10);
//   3. N connections held for S seconds with a ping every 15 s, one broadcast per minute through the vhost, a
//      graceful of Apache at half time: every connection still open and every broadcast received by every one;
//      GET /health p95 under load compared with the baseline; a new connection still opens at the end.
// Every URL carries ?access_token=<marker> so ensaio-apache.sh can prove the marker never reaches the logs.
//
// Environment (all required): PAF_ENSAIO_BASE (https://127.0.0.1:<porta>), PAF_ENSAIO_HUB (path of the hub),
// PAF_ENSAIO_TOKEN (marker), PAF_ENSAIO_CONTAINER_APACHE, PAF_ENSAIO_CONTAINER_APP1, PAF_ENSAIO_CONTAINER_APP2,
// PAF_ENSAIO_CONEXOES, PAF_ENSAIO_SEGUNDOS. Exit 0 only when every check passed.
import { execFileSync } from "node:child_process";

const exigida = (nome) => {
  const valor = process.env[nome];
  if (!valor) {
    console.error(`${nome} is required`);
    process.exit(2);
  }
  return valor;
};
const BASE = exigida("PAF_ENSAIO_BASE");
const HUB = exigida("PAF_ENSAIO_HUB");
const TOKEN = exigida("PAF_ENSAIO_TOKEN");
const APACHE = exigida("PAF_ENSAIO_CONTAINER_APACHE");
const CONTAINERS = { app1: exigida("PAF_ENSAIO_CONTAINER_APP1"), app2: exigida("PAF_ENSAIO_CONTAINER_APP2") };
const CONEXOES = Number(exigida("PAF_ENSAIO_CONEXOES"));
const SEGUNDOS = Number(exigida("PAF_ENSAIO_SEGUNDOS"));

const SEPARADOR = "\x1e";
const PING_MS = 15_000; // SignalR client keepalive
const TIMEOUT_MS = 10_000;
const TIMEOUT_DOCKER_MS = 60_000;
const ATRASOS_RECONEXAO_MS = [0, 2_000, 10_000, 30_000]; // SignalR client WithAutomaticReconnect() defaults
const RETORNO_AO_GRUPO_MS = 45_000;
const LOTE_ABERTURA = 25;
const BROADCAST_MS = 60_000;
const TICK_MS = 5_000;
const URL_HUB = `${BASE.replace(/^http/, "ws")}${HUB}?tenant=ensaio&access_token=${encodeURIComponent(TOKEN)}`;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}
const esperar = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const docker = (...args) => execFileSync("docker", args, { timeout: TIMEOUT_DOCKER_MS, encoding: "utf8" });

// One SignalR connection over a raw WebSocket. Resolves once the handshake answer arrived.
function conectar() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(URL_HUB);
    const conexao = { ws, eventos: new Set(), fechada: false, motivo: "", pendentes: new Map(), proximoId: 1 };
    const limite = setTimeout(() => {
      ws.close();
      reject(new Error("handshake sem resposta em 10 s"));
    }, TIMEOUT_MS);
    let aberta = false;
    ws.onopen = () => ws.send(JSON.stringify({ protocol: "json", version: 1 }) + SEPARADOR);
    ws.onerror = () => {
      if (!aberta) {
        clearTimeout(limite);
        reject(new Error("WebSocket recusado (sem 101)"));
      }
    };
    ws.onclose = (e) => {
      conexao.fechada = true;
      conexao.motivo = `${e.code} ${e.reason}`;
      clearInterval(conexao.ping);
    };
    ws.onmessage = (e) => {
      for (const quadro of String(e.data).split(SEPARADOR)) {
        if (quadro.length === 0) continue;
        const mensagem = JSON.parse(quadro);
        if (!aberta) {
          aberta = true;
          clearTimeout(limite);
          if (mensagem.error) {
            reject(new Error(`handshake recusado: ${mensagem.error}`));
            return;
          }
          conexao.ping = setInterval(() => ws.readyState === WebSocket.OPEN && ws.send('{"type":6}' + SEPARADOR), PING_MS);
          resolve(conexao);
          continue;
        }
        if (mensagem.type === 1 && mensagem.target === "evt") conexao.eventos.add(mensagem.arguments[0].seq);
        if (mensagem.type === 3) conexao.pendentes.get(mensagem.invocationId)?.(mensagem);
      }
    };
  });
}

function invocar(conexao, alvo) {
  const id = String(conexao.proximoId++);
  return new Promise((resolve, reject) => {
    const limite = setTimeout(() => reject(new Error(`${alvo} sem resposta em 10 s`)), TIMEOUT_MS);
    conexao.pendentes.set(id, (m) => {
      clearTimeout(limite);
      if (m.error) reject(new Error(m.error));
      else resolve(m.result);
    });
    conexao.ws.send(JSON.stringify({ type: 1, invocationId: id, target: alvo, arguments: [] }) + SEPARADOR);
  });
}

function fechar(conexao) {
  clearInterval(conexao.ping);
  conexao.ws.close();
}

async function http(method, caminho) {
  const r = await fetch(`${BASE}${caminho}${caminho.includes("?") ? "&" : "?"}access_token=${encodeURIComponent(TOKEN)}`, {
    method,
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  return { status: r.status, corpo: await r.text() };
}

async function p95Health(amostras) {
  const tempos = [];
  for (let i = 0; i < amostras; i += 1) {
    const inicio = performance.now();
    const r = await http("GET", "/health");
    if (r.status !== 200) throw new Error(`/health ${r.status}`);
    tempos.push(performance.now() - inicio);
  }
  tempos.sort((a, b) => a - b);
  return tempos[Math.ceil(amostras * 0.95) - 1];
}

async function fase1() {
  const conexao = await conectar();
  const quem = await invocar(conexao, "WhoAmI");
  check("1. handshake SignalR pelo vhost (101) e transporte WebSockets", quem.transport === "WebSockets", JSON.stringify(quem));
  return { conexao, instancia: quem.instance };
}

async function fase2(conexao, instancia) {
  const outra = instancia === "app1" ? "app2" : "app1";
  docker("kill", CONTAINERS[instancia]);
  const morte = performance.now();
  for (let i = 0; i < 100 && !conexao.fechada; i += 1) await esperar(50);
  check(`2. conexão cai quando ${instancia} morre`, conexao.fechada, conexao.motivo);
  let nova;
  let tentativas = 0;
  for (const atraso of ATRASOS_RECONEXAO_MS) {
    await esperar(atraso);
    tentativas += 1;
    try {
      nova = await conectar();
      break;
    } catch (erro) {
      console.log(`   tentativa ${tentativas}: ${erro.message}`);
    }
  }
  const ms = Math.round(performance.now() - morte);
  const quem = nova === undefined ? undefined : await invocar(nova, "WhoAmI");
  check(`2. reconecta em ${outra} com ${instancia} morta`, quem?.instance === outra && quem?.transport === "WebSockets",
    `${ms} ms, ${tentativas} tentativa(s), ${JSON.stringify(quem)}`);
  if (nova !== undefined) fechar(nova);

  docker("start", CONTAINERS[instancia]);
  const vistos = new Set();
  const limite = performance.now() + RETORNO_AO_GRUPO_MS;
  let ultimo = "";
  while (performance.now() < limite && vistos.size < 2) {
    try {
      const r = await http("GET", "/health");
      ultimo = `status ${r.status}`;
      if (r.status === 200) vistos.add(r.corpo.replaceAll('"', ""));
    } catch (erro) {
      // Bounded by the deadline above; the last error goes into the check detail.
      ultimo = erro.message;
    }
    await esperar(500);
  }
  check(`2. ${instancia} volta ao balanceador depois de reiniciada (retry)`, vistos.has(instancia),
    `${[...vistos].join(",")} (último: ${ultimo})`);
}

async function fase3() {
  const base = await p95Health(20);
  const conexoes = [];
  const recusas = [];
  for (let i = 0; i < CONEXOES; i += LOTE_ABERTURA) {
    const lote = await Promise.allSettled(Array.from({ length: Math.min(LOTE_ABERTURA, CONEXOES - i) }, conectar));
    for (const r of lote) (r.status === "fulfilled" ? conexoes.push(r.value) : recusas.push(r.reason.message));
  }
  check(`3. ${CONEXOES} WebSockets abertos pelo vhost`, conexoes.length === CONEXOES,
    `${conexoes.length} abertos, ${recusas.length} recusados ${[...new Set(recusas)].join("; ")}`);

  const inicio = performance.now();
  let seq = 0;
  let graceful = false;
  let sobCarga = Number.NaN;
  let ultimoBroadcast = -Infinity;
  while (performance.now() - inicio < SEGUNDOS * 1000) {
    if (performance.now() - ultimoBroadcast >= BROADCAST_MS) {
      ultimoBroadcast = performance.now();
      seq += 1;
      const r = await http("POST", `/broadcast?tag=ensaio&seq=${seq}`);
      if (r.status !== 200) check(`3. broadcast ${seq} pelo vhost`, false, `status ${r.status}`);
    }
    if (!graceful && performance.now() - inicio >= (SEGUNDOS * 1000) / 2) {
      graceful = true;
      docker("exec", APACHE, "apachectl", "graceful");
      console.log(`   graceful do Apache com ${conexoes.filter((c) => !c.fechada).length} WebSockets abertos`);
      sobCarga = await p95Health(20);
    }
    await esperar(Math.min(TICK_MS, Math.max(0, SEGUNDOS * 1000 - (performance.now() - inicio))));
  }
  await esperar(2_000);
  if (Number.isNaN(sobCarga)) sobCarga = await p95Health(20);

  const caidas = conexoes.filter((c) => c.fechada);
  check(`3. nenhum WebSocket caiu em ${SEGUNDOS} s (graceful no meio: ${graceful})`, caidas.length === 0,
    `${caidas.length} caídos ${[...new Set(caidas.map((c) => c.motivo))].join("; ")}`);
  const perdidos = conexoes.reduce((total, c) => total + (seq - c.eventos.size), 0);
  check(`3. ${seq} broadcasts recebidos por todas as conexões`, perdidos === 0, `${perdidos} eventos perdidos`);
  console.log(`   INFO p95 GET /health: base ${base.toFixed(1)} ms, com ${conexoes.length} WebSockets ${sobCarga.toFixed(1)} ms`);
  try {
    const extra = await conectar();
    check("3. conexão nova ainda abre com a carga aberta", true);
    fechar(extra);
  } catch (erro) {
    check("3. conexão nova ainda abre com a carga aberta", false, erro.message);
  }
  for (const c of conexoes) fechar(c);
}

try {
  const { conexao, instancia } = await fase1();
  await fase2(conexao, instancia);
  if (falhas === 0) await fase3();
} catch (erro) {
  check("ensaio executou sem erro de chamada", false, erro.message);
}
console.log(falhas === 0 ? "ENSAIO-APACHE-WS OK" : `ENSAIO-APACHE-WS FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
