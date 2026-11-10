import {
  CredencialEstacaoInvalidaError,
  forcarRenovacao,
  obterTokenValido,
  tenantDaEstacao,
} from "@/features/paf/estacao/estacao-http";
import type { PapelEstacao } from "@/features/paf/estacao/credencial-estacao";
import { conectarPafHub, type EstadoConexao } from "@/features/paf/paf-hub";

// Connection to PafHub with the station credential (arquitetura seção 3: never the kit's realtime
// provider, which is bound to the user token and to AppHub). Stations only listen: they invoke nothing.

export type { EstadoConexao };

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

/** Starts the connection and keeps it alive until the returned function is called. */
export function conectarHubEstacao(opcoes: OpcoesHubEstacao): () => void {
  const conexao = conectarPafHub({
    tenant: () => tenantDaEstacao(opcoes.papel),
    obterToken: () => obterTokenValido(opcoes.papel),
    renovarToken: () => forcarRenovacao(opcoes.papel),
    credencialPerdida: (erro) => erro instanceof CredencialEstacaoInvalidaError,
    handlers: opcoes.handlers,
    aoConectar: () => opcoes.aoConectar(),
    aoMudarEstado: opcoes.aoMudarEstado,
    aoPerderCredencial: opcoes.aoPerderCredencial,
  });
  return conexao.parar;
}
