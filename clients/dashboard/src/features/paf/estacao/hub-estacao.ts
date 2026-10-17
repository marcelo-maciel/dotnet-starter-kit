import type { HubConnection } from "@microsoft/signalr";
import { env } from "@/env";
import {
  CredencialEstacaoInvalidaError,
  forcarRenovacao,
  obterTokenValido,
  tenantDaEstacao,
} from "@/features/paf/estacao/estacao-http";
import type { PapelEstacao } from "@/features/paf/estacao/credencial-estacao";

// Connection to PafHub with the station credential (arquitetura seção 3: never the kit's realtime
// provider, which is bound to the user token and to AppHub).

export type EstadoConexao = "conectando" | "conectado" | "reconectando" | "desconectado";

export type OpcoesHubEstacao = {
  papel: PapelEstacao;
  /** Server-to-client method name → handler; names come from PafHubMetodos. */
  handlers: Record<string, (payload: unknown) => void>;
  /** Runs after every (re)connection: the screen re-reads its state, events may have been missed. */
  aoConectar: () => void;
  aoMudarEstado: (estado: EstadoConexao) => void;
  /** The credential was revoked or is gone; the connection stops for good. */
  aoPerderCredencial: () => void;
};

const ESPERAS_RECONEXAO_MS = [0, 2_000, 5_000, 10_000, 30_000];
const ESPERA_MAXIMA_MS = 30_000;

/** Starts the connection and keeps it alive until the returned function is called. */
export function conectarHubEstacao(opcoes: OpcoesHubEstacao): () => void {
  let parado = false;
  let conexao: HubConnection | null = null;
  let falhas = 0;
  let renovouNestaSequencia = false;
  let timer: number | undefined;

  const agendarNovaTentativa = () => {
    if (parado) return;
    // A failed start may also raise onclose: clearing first collapses both into one retry.
    window.clearTimeout(timer);
    const espera = Math.min(ESPERA_MAXIMA_MS, 1_000 * 2 ** falhas);
    falhas += 1;
    opcoes.aoMudarEstado("reconectando");
    timer = window.setTimeout(() => void iniciar(), espera);
  };

  const iniciar = async () => {
    if (parado) return;
    // After a loss the screen keeps saying "reconectando" across attempts instead of flickering.
    if (falhas === 0) opcoes.aoMudarEstado("conectando");
    try {
      const sr = await import("@microsoft/signalr");
      // The tenant goes in the query because a browser cannot set headers on the WebSocket upgrade;
      // SignalR itself appends access_token to the query for WebSockets and SSE.
      const url = `${env.apiBase}/api/v1/paf/hub?tenant=${encodeURIComponent(tenantDaEstacao(opcoes.papel))}`;
      const nova = new sr.HubConnectionBuilder()
        .withUrl(url, {
          accessTokenFactory: () => obterTokenValido(opcoes.papel),
          transport:
            sr.HttpTransportType.WebSockets |
            sr.HttpTransportType.ServerSentEvents |
            sr.HttpTransportType.LongPolling,
        })
        .withAutomaticReconnect(ESPERAS_RECONEXAO_MS)
        .configureLogging(sr.LogLevel.Warning)
        .build();
      for (const [metodo, handler] of Object.entries(opcoes.handlers)) nova.on(metodo, handler);
      nova.onreconnecting(() => opcoes.aoMudarEstado("reconectando"));
      nova.onreconnected(() => {
        opcoes.aoMudarEstado("conectado");
        opcoes.aoConectar();
      });
      // The hub closes the connection when the token expires (CloseOnAuthenticationExpiration) and the
      // automatic reconnect gives up after its list: either way we start over with a fresh token.
      nova.onclose(() => {
        if (conexao === nova) conexao = null;
        agendarNovaTentativa();
      });
      conexao = nova;
      await nova.start();
      if (parado) {
        await nova.stop();
        return;
      }
      falhas = 0;
      renovouNestaSequencia = false;
      opcoes.aoMudarEstado("conectado");
      opcoes.aoConectar();
    } catch (erro) {
      if (perdeuCredencial(erro)) return;
      console.warn("[paf-hub] falha ao conectar", erro);
      conexao = null;
      // The negotiate 401 comes wrapped (FailedToNegotiateWithServerError), so instead of reading it,
      // a failure streak renews once: a token the client thinks valid but the server refused (drifted
      // clock) would otherwise be retried forever. Once per streak keeps the `auth` rate limit safe.
      if (!renovouNestaSequencia) {
        renovouNestaSequencia = true;
        try {
          await forcarRenovacao(opcoes.papel);
        } catch (erroRenovacao) {
          if (perdeuCredencial(erroRenovacao)) return;
          console.warn("[paf-hub] falha ao renovar a credencial", erroRenovacao);
        }
      }
      agendarNovaTentativa();
    }
  };

  const perdeuCredencial = (erro: unknown): boolean => {
    if (!(erro instanceof CredencialEstacaoInvalidaError)) return false;
    parado = true;
    opcoes.aoMudarEstado("desconectado");
    opcoes.aoPerderCredencial();
    return true;
  };

  void iniciar();

  return () => {
    parado = true;
    window.clearTimeout(timer);
    const atual = conexao;
    conexao = null;
    opcoes.aoMudarEstado("desconectado");
    if (atual) void atual.stop();
  };
}
