// Walk in a loop for the rolling deploy rehearsal (O06, RNF16), run by ensaio-deploy.sh while deploy.sh replaces the
// two API instances behind the local Apache. Everything goes through the vhost (https, the public API host), the way
// the totem, the painel and the guichê reach production:
//   - totem: one emission per second, each sent twice with the same Idempotency-Key (the client retry), and retried
//     with the same key on a network error or 5xx;
//   - guichê: chamar-proximo, iniciar, encerrar in a loop, on the demo admin's session, with its own hub connection
//     (EntrarNoGuiche after every reconnect, as the guichê screen does);
//   - painel: hub connection on the station credential, SenhaChamada/SenhaRechamada recorded by EventoId, and on every
//     reconnect a re-query of painel/estado, as painel-estado.ts does.
// Both hub clients speak the SignalR JSON protocol over a raw WebSocket, WebSockets only, no negotiate (ARQ seção 11),
// reconnecting with the SignalR client's default delays (0, 2, 10, 30 s, then 30 s).
//
// Stops when the file PAF_ENSAIO_PARAR exists, waits PAF_ENSAIO_ESPERA_S for late deliveries (outbox), prints
// PASS/FAIL lines and writes the JSON summary to PAF_ENSAIO_SAIDA (ensaio-deploy.sh compares it with the database).
// Env (all required): PAF_ENSAIO_API (https://<host da API>), PAF_SENHA_DEMO, PAF_ENSAIO_PARAR, PAF_ENSAIO_SAIDA,
// PAF_ENSAIO_ESPERA_S. TLS is verified: the rehearsal passes its self-signed certificate through NODE_EXTRA_CA_CERTS.
// Exit 0 only when every check passed; 2 on a missing variable.
import { existsSync, writeFileSync } from "node:fs";

const exigida = (nome) => {
  const valor = process.env[nome];
  if (!valor) {
    console.error(`${nome} é obrigatória`);
    process.exit(2);
  }
  return valor;
};
const API = exigida("PAF_ENSAIO_API");
const SENHA_DEMO = exigida("PAF_SENHA_DEMO");
const PARAR = exigida("PAF_ENSAIO_PARAR");
const SAIDA = exigida("PAF_ENSAIO_SAIDA");
const ESPERA_MS = Number(exigida("PAF_ENSAIO_ESPERA_S")) * 1000;

const TENANT = "acme";
const EMAIL = "admin@acme.com";
const TIMEOUT_MS = 10_000;
const PASSO_MS = 1_000;
const PING_MS = 15_000;
const SEPARADOR = "\x1e";
const ATRASOS_RECONEXAO_MS = [0, 2_000, 10_000, 30_000];
const TENTATIVAS_EMISSAO = 4;

const esperar = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

// A network error is returned as status 0, so the loops count it next to the 5xx instead of dying on it.
async function chamar(method, path, headers, body) {
  const todos = { "X-FSH-App": "dashboard", ...headers };
  if (body !== undefined) todos["Content-Type"] = "application/json";
  try {
    const r = await fetch(API + path, {
      method,
      headers: todos,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    const texto = await r.text();
    const json = texto.length > 0 && r.headers.get("content-type")?.includes("json");
    return { status: r.status, corpo: json ? JSON.parse(texto) : texto };
  } catch (erro) {
    return { status: 0, corpo: String(erro.cause?.code ?? erro.message) };
  }
}

async function exigir(method, path, headers, body) {
  const r = await chamar(method, path, headers, body);
  if (r.status < 200 || r.status > 299) throw new Error(`${method} ${path} -> ${r.status} ${JSON.stringify(r.corpo)}`);
  return r.corpo;
}

// One logical hub client: reconnects on every close until encerrar() is called.
function clienteHub(nome, tenant, token, aoConectar, aoEvento) {
  const url = `${API.replace(/^https/, "wss")}/api/v1/paf/hub?tenant=${encodeURIComponent(tenant)}&access_token=${encodeURIComponent(token)}`;
  const estado = { nome, conexoes: 0, quedas: 0, encerrado: false, ws: null, pendentes: new Map(), proximoId: 1 };

  const abrir = () =>
    new Promise((resolve, reject) => {
      const ws = new WebSocket(url);
      let pronta = false;
      const limite = setTimeout(() => {
        ws.close();
        reject(new Error("handshake sem resposta"));
      }, TIMEOUT_MS);
      ws.onopen = () => ws.send(JSON.stringify({ protocol: "json", version: 1 }) + SEPARADOR);
      ws.onerror = () => {
        if (!pronta) {
          clearTimeout(limite);
          reject(new Error("WebSocket recusado"));
        }
      };
      ws.onclose = () => {
        clearInterval(estado.ping);
        for (const { rejeitar } of estado.pendentes.values()) rejeitar(new Error("conexão fechada"));
        estado.pendentes.clear();
        if (!pronta) {
          clearTimeout(limite);
          reject(new Error("fechada antes do handshake"));
          return;
        }
        if (!estado.encerrado) {
          estado.quedas += 1;
          reconectar();
        }
      };
      ws.onmessage = (e) => {
        for (const quadro of String(e.data).split(SEPARADOR)) {
          if (quadro.length === 0) continue;
          const mensagem = JSON.parse(quadro);
          if (!pronta) {
            pronta = true;
            clearTimeout(limite);
            if (mensagem.error) {
              reject(new Error(mensagem.error));
              return;
            }
            estado.ws = ws;
            estado.ping = setInterval(() => ws.send(JSON.stringify({ type: 6 }) + SEPARADOR), PING_MS);
            resolve();
            continue;
          }
          if (mensagem.type === 1) aoEvento(mensagem.target, mensagem.arguments[0]);
          if (mensagem.type === 3) {
            const pendente = estado.pendentes.get(mensagem.invocationId);
            estado.pendentes.delete(mensagem.invocationId);
            if (pendente && mensagem.error) pendente.rejeitar(new Error(mensagem.error));
            else if (pendente) pendente.resolver(mensagem.result);
          }
        }
      };
    });

  estado.invocar = (alvo, ...argumentos) =>
    new Promise((resolver, rejeitar) => {
      const invocationId = String(estado.proximoId++);
      estado.pendentes.set(invocationId, { resolver, rejeitar });
      estado.ws.send(JSON.stringify({ type: 1, invocationId, target: alvo, arguments: argumentos }) + SEPARADOR);
    });

  async function conectar() {
    await abrir();
    estado.conexoes += 1;
    await aoConectar(estado);
  }

  async function reconectar() {
    for (let tentativa = 0; !estado.encerrado; tentativa += 1) {
      await esperar(ATRASOS_RECONEXAO_MS[Math.min(tentativa, ATRASOS_RECONEXAO_MS.length - 1)]);
      try {
        await conectar();
        return;
      } catch (erro) {
        console.log(`${nome}: reconexão ${tentativa + 1} falhou (${erro.message})`);
      }
    }
  }

  estado.iniciar = conectar;
  estado.encerrar = () => {
    estado.encerrado = true;
    estado.ws?.close();
  };
  return estado;
}

const resultado = {
  emissoes: { chaves: [], ok: 0, retentativas: 0, erros: [], divergentes: [] },
  guiche: { chamadas: [], semFicha: 0, erros: [] },
  painel: { eventos: [], senhasPorEvento: [], senhasPorConsulta: [], conexoes: 0, quedas: 0 },
  guicheHub: { conexoes: 0, quedas: 0, erros: [] },
};
const senhasPorEvento = new Set();
const senhasPorConsulta = new Set();
const eventos = new Set();

try {
  const admin = await exigir("POST", "/api/v1/identity/token/issue", { tenant: TENANT }, { email: EMAIL, password: SENHA_DEMO });
  const comAdmin = { tenant: TENANT, Authorization: `Bearer ${admin.accessToken}` };
  const estacoes = await exigir("GET", "/api/v1/paf/estacoes", comAdmin);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const guiche = estacoes.find((e) => e.tipo === "Guiche");
  if (totem === undefined || guiche === undefined) throw new Error("seed-demo sem totem ou guichê");

  const link = await exigir("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, comAdmin, {});
  const resgate = await exigir("POST", "/api/v1/paf/estacoes/vinculo/resgatar", { tenant: link.tenant }, { token: link.token });
  const credencial = (papel) => resgate.credenciais.find((c) => c.papel === papel)?.accessToken;
  const comEmissao = { tenant: link.tenant, Authorization: `Bearer ${credencial("Emissao")}` };
  const comPainel = { tenant: link.tenant, Authorization: `Bearer ${credencial("Painel")}` };
  const configuracao = await exigir("GET", "/api/v1/paf/estacao/emissao/configuracao", comEmissao);
  const botao = configuracao.botoes[0];
  if (botao === undefined) throw new Error("totem sem botão de emissão");
  const selecao = await chamar("POST", "/api/v1/paf/sessao/guiche", comAdmin, { estacaoId: guiche.id, liberarPendencia: false });
  if (selecao.status !== 204) throw new Error(`seleção do guichê -> ${selecao.status} ${JSON.stringify(selecao.corpo)}`);

  const painel = clienteHub(
    "painel",
    link.tenant,
    credencial("Painel"),
    async (estado) => {
      if (estado.conexoes === 1) return;
      const r = await chamar("GET", "/api/v1/paf/estacao/painel/estado", comPainel);
      for (const c of r.corpo?.chamadas ?? []) senhasPorConsulta.add(c.senha);
    },
    (alvo, aviso) => {
      if (alvo !== "SenhaChamada" && alvo !== "SenhaRechamada") return;
      eventos.add(aviso.eventoId);
      senhasPorEvento.add(aviso.senha);
    },
  );
  const guicheHub = clienteHub(
    "guiche",
    TENANT,
    admin.accessToken,
    async (estado) => {
      try {
        await estado.invocar("EntrarNoGuiche");
      } catch (erro) {
        resultado.guicheHub.erros.push(erro.message);
      }
    },
    () => {},
  );
  await painel.iniciar();
  await guicheHub.iniciar();
  console.log("walk em laço: totem, guichê e painel conectados pelo vhost");

  const emitir = async () => {
    const chave = crypto.randomUUID();
    let primeira;
    for (let tentativa = 0; tentativa < TENTATIVAS_EMISSAO; tentativa += 1) {
      const r = await chamar("POST", "/api/v1/paf/estacao/emissao/fichas", { ...comEmissao, "Idempotency-Key": chave },
        { filaId: botao.filaId, prioridadeId: botao.prioridadeId });
      if (r.status !== 200) {
        resultado.emissoes.erros.push(`${r.status} ${typeof r.corpo === "string" ? r.corpo.slice(0, 80) : JSON.stringify(r.corpo).slice(0, 80)}`);
        await esperar(500);
        continue;
      }
      primeira = r.corpo;
      break;
    }
    if (primeira === undefined) return;
    resultado.emissoes.chaves.push(chave);
    resultado.emissoes.ok += 1;
    // The client retry: same key, must answer the same ficha, never a second one.
    const repetida = await chamar("POST", "/api/v1/paf/estacao/emissao/fichas", { ...comEmissao, "Idempotency-Key": chave },
      { filaId: botao.filaId, prioridadeId: botao.prioridadeId });
    resultado.emissoes.retentativas += 1;
    if (repetida.status !== 200 || repetida.corpo?.fichaId !== primeira.fichaId) {
      resultado.emissoes.divergentes.push(`${chave}: ${repetida.status} ${repetida.corpo?.fichaId} != ${primeira.fichaId}`);
    }
  };

  const atender = async () => {
    const r = await chamar("POST", "/api/v1/paf/guiche/chamar-proximo", comAdmin, undefined);
    if (r.status === 404) {
      resultado.guiche.semFicha += 1;
      return;
    }
    if (r.status !== 200) {
      resultado.guiche.erros.push(`chamar ${r.status} ${JSON.stringify(r.corpo).slice(0, 120)}`);
      return;
    }
    resultado.guiche.chamadas.push({ senha: r.corpo.senha, atendimentoId: r.corpo.atendimentoId });
    for (const acao of ["iniciar", "encerrar"]) {
      let feito = false;
      for (let tentativa = 0; tentativa < TENTATIVAS_EMISSAO && !feito; tentativa += 1) {
        const a = await chamar("POST", `/api/v1/paf/guiche/atendimentos/${r.corpo.atendimentoId}/${acao}`, comAdmin, undefined);
        if (a.status === 200) feito = true;
        else {
          resultado.guiche.erros.push(`${acao} ${a.status} ${JSON.stringify(a.corpo).slice(0, 120)}`);
          await esperar(500);
        }
      }
    }
  };

  const laco = async (passo) => {
    while (!existsSync(PARAR)) {
      const inicio = Date.now();
      await passo();
      await esperar(Math.max(0, PASSO_MS - (Date.now() - inicio)));
    }
  };
  await Promise.all([laco(emitir), laco(atender)]);
  console.log(`parada pedida; espera de ${ESPERA_MS / 1000}s por entregas tardias`);
  await esperar(ESPERA_MS);
  painel.encerrar();
  guicheHub.encerrar();
  Object.assign(resultado.painel, { conexoes: painel.conexoes, quedas: painel.quedas });
  Object.assign(resultado.guicheHub, { conexoes: guicheHub.conexoes, quedas: guicheHub.quedas });
} catch (erro) {
  check("walk executou sem erro de preparo", false, erro.message);
}

resultado.painel.eventos = [...eventos];
resultado.painel.senhasPorEvento = [...senhasPorEvento];
resultado.painel.senhasPorConsulta = [...senhasPorConsulta];
const chamadas = resultado.guiche.chamadas;
const faltando = chamadas.filter((c) => !senhasPorEvento.has(c.senha) && !senhasPorConsulta.has(c.senha));
const soPorConsulta = chamadas.filter((c) => !senhasPorEvento.has(c.senha) && senhasPorConsulta.has(c.senha));

check("totem: emissões com sucesso durante o laço", resultado.emissoes.ok > 0, `${resultado.emissoes.ok}`);
check("totem: nenhuma resposta 5xx nem erro de rede na emissão", resultado.emissoes.erros.length === 0,
  resultado.emissoes.erros.slice(0, 5).join(" | "));
check("totem: retry com a mesma chave devolve a mesma ficha", resultado.emissoes.divergentes.length === 0,
  `${resultado.emissoes.retentativas} retries; ${resultado.emissoes.divergentes.slice(0, 3).join(" | ")}`);
check("guichê: chamadas durante o laço", chamadas.length > 0, `${chamadas.length} chamadas, ${resultado.guiche.semFicha} sem ficha`);
check("guichê: nenhum erro em chamar, iniciar e encerrar", resultado.guiche.erros.length === 0,
  resultado.guiche.erros.slice(0, 5).join(" | "));
check("guichê: EntrarNoGuiche aceito em toda (re)conexão", resultado.guicheHub.erros.length === 0,
  `${resultado.guicheHub.conexoes} conexões, ${resultado.guicheHub.quedas} quedas; ${resultado.guicheHub.erros.join(" | ")}`);
check("painel: toda senha chamada chegou ao painel", faltando.length === 0,
  `${chamadas.length - faltando.length}/${chamadas.length}; por evento ${chamadas.length - faltando.length - soPorConsulta.length}, só pela reconsulta ${soPorConsulta.length}; faltando ${faltando.map((c) => c.senha).join(",")}`);
check("painel: reconectou depois de cada queda", resultado.painel.conexoes === resultado.painel.quedas + 1,
  `${resultado.painel.conexoes} conexões, ${resultado.painel.quedas} quedas`);

writeFileSync(SAIDA, JSON.stringify(resultado, null, 2));
console.log(falhas === 0 ? "WALK-LACO OK" : `WALK-LACO FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
