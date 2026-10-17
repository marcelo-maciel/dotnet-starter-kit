import type { HubConnection } from "@microsoft/signalr";
import { env } from "@/env";

// Connection loop to PafHub shared by the station screens (station credential) and the guichê (kit user
// token). Transport per arquitetura seção 11, decided 17/10/2026: WebSockets only with skipNegotiation, so
// two instances need no affinity; the cost accepted is no connection on a network that blocks WebSocket.

export type EstadoConexao = "conectando" | "conectado" | "reconectando" | "desconectado";

export type OpcoesPafHub = {
  /** Read on every attempt; goes in ?tenant= because a browser cannot set headers on the WebSocket upgrade. */
  tenant: () => string;
  /** Read on every attempt; SignalR appends it as ?access_token=. */
  obterToken: () => string | Promise<string>;
  /** Forced renewal, once per failure streak; null when another layer owns the renewal. */
  renovarToken: (() => Promise<void>) | null;
  /** True when the error means the credential is gone for good: the loop stops. */
  credencialPerdida: (erro: unknown) => boolean;
  /** Server-to-client method name → handler; names come from PafHubMetodos. */
  handlers: Record<string, (payload: unknown) => void>;
  /**
   * Runs after every (re)connection, always in the Connected state: the screen re-reads its state, events
   * may have been missed, and only here may it invoke the server.
   */
  aoConectar: (conexao: HubConnection) => void;
  aoMudarEstado: (estado: EstadoConexao) => void;
  /** The credential was revoked or is gone; the connection stops for good. */
  aoPerderCredencial: () => void;
};

export type ConexaoPafHub = {
  parar: () => void;
  /**
   * Invokes a client-to-server method only when Connected; otherwise does nothing and returns null, because
   * an invoke outside Connected throws and every reconnect runs aoConectar again anyway.
   */
  invocarSeConectado: <T>(metodo: string) => Promise<T> | null;
};

const ESPERAS_RECONEXAO_MS = [0, 2_000, 5_000, 10_000, 30_000];
const ESPERA_MAXIMA_MS = 30_000;

/** Starts the connection and keeps it alive until parar() is called. */
export function conectarPafHub(opcoes: OpcoesPafHub): ConexaoPafHub {
  let parado = false;
  let conexao: HubConnection | null = null;
  let conectado: HubConnection | null = null;
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
      const url = `${env.apiBase}/api/v1/paf/hub?tenant=${encodeURIComponent(opcoes.tenant())}`;
      const nova = new sr.HubConnectionBuilder()
        .withUrl(url, {
          accessTokenFactory: opcoes.obterToken,
          transport: sr.HttpTransportType.WebSockets,
          skipNegotiation: true,
        })
        .withAutomaticReconnect(ESPERAS_RECONEXAO_MS)
        .configureLogging(sr.LogLevel.Warning)
        .build();
      for (const [metodo, handler] of Object.entries(opcoes.handlers)) nova.on(metodo, handler);
      nova.onreconnecting(() => {
        conectado = null;
        opcoes.aoMudarEstado("reconectando");
      });
      nova.onreconnected(() => {
        conectado = nova;
        opcoes.aoMudarEstado("conectado");
        opcoes.aoConectar(nova);
      });
      // The hub closes the connection when the token expires (CloseOnAuthenticationExpiration) or the
      // credential is revoked, and the automatic reconnect gives up after its list: either way we start over.
      nova.onclose(() => {
        if (conectado === nova) conectado = null;
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
      conectado = nova;
      opcoes.aoMudarEstado("conectado");
      opcoes.aoConectar(nova);
    } catch (erro) {
      if (perdeuCredencial(erro)) return;
      console.warn("[paf-hub] falha ao conectar", erro);
      conexao = null;
      // Without negotiate a refused token surfaces as a failed WebSocket, with no status to read, so a
      // failure streak renews once: a token the client thinks valid but the server refused (drifted clock)
      // would otherwise be retried forever. Once per streak keeps the `auth` rate limit safe.
      if (opcoes.renovarToken && !renovouNestaSequencia) {
        renovouNestaSequencia = true;
        try {
          await opcoes.renovarToken();
        } catch (erroRenovacao) {
          if (perdeuCredencial(erroRenovacao)) return;
          console.warn("[paf-hub] falha ao renovar a credencial", erroRenovacao);
        }
      }
      agendarNovaTentativa();
    }
  };

  const perdeuCredencial = (erro: unknown): boolean => {
    if (!opcoes.credencialPerdida(erro)) return false;
    parado = true;
    opcoes.aoMudarEstado("desconectado");
    opcoes.aoPerderCredencial();
    return true;
  };

  void iniciar();

  return {
    parar: () => {
      parado = true;
      window.clearTimeout(timer);
      const atual = conexao;
      conexao = null;
      conectado = null;
      opcoes.aoMudarEstado("desconectado");
      if (atual) void atual.stop();
    },
    invocarSeConectado: <T>(metodo: string) => {
      const atual = conectado;
      return atual ? atual.invoke<T>(metodo) : null;
    },
  };
}
