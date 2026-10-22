import { env } from "@/env";
import { tokenStore } from "@/auth/token-store";
import { conectarPafHub, type ConexaoPafHub, type EstadoConexao } from "@/features/paf/paf-hub";
import { comAvisoDeUnidadeCorrente, recarregarAoReconectar } from "@/features/paf/unidade-corrente";
import { queryClient } from "@/lib/query-client";

// The guichê's PafHub connection, with the kit user token. The server puts it in the group of the guichê
// held by the session's SessaoEstacao; the screen asks it to re-read that link after every selection and
// every reconnect (PafHubChamadas.EntrarNoGuiche), never naming the guichê itself (arquitetura seção 7).

const ENTRAR_NO_GUICHE = "EntrarNoGuiche";

export type OpcoesHubGuiche = {
  /** Server-to-client method name → handler; names come from PafHubMetodos. */
  handlers: Record<string, (payload: unknown) => void>;
  aoMudarEstado: (estado: EstadoConexao) => void;
};

export type HubGuiche = {
  parar: () => void;
  /** After selecting a guichê; a no-op while not Connected, the next (re)connection does it. */
  entrarNoGuiche: () => void;
};

export function conectarHubGuiche(opcoes: OpcoesHubGuiche): HubGuiche {
  const entrar = () => {
    conexao.invocarSeConectado<string | null>(ENTRAR_NO_GUICHE)?.catch((erro: unknown) => {
      console.warn("[paf-hub] falha ao entrar no grupo do guichê", erro);
    });
  };

  const recarregarContexto = () => {
    void queryClient.invalidateQueries({ queryKey: ["paf"] });
  };
  const aoReconectar = recarregarAoReconectar(recarregarContexto);

  const conexao: ConexaoPafHub = conectarPafHub({
    tenant: () => tokenStore.getTenant() ?? env.defaultTenant,
    obterToken: () => tokenStore.getAccessToken() ?? "",
    // The kit's apiFetch renews the user token on its own 401; the next attempt reads the stored one.
    renovarToken: null,
    // Session loss is the kit's to handle (logout redirect); the loop keeps retrying until the page leaves.
    credencialPerdida: () => false,
    // The guichê page is F18's in wave 1; the unit-change reload lives here so the page stays untouched (S05).
    handlers: comAvisoDeUnidadeCorrente(opcoes.handlers, recarregarContexto),
    aoConectar: () => {
      entrar();
      aoReconectar();
    },
    aoMudarEstado: opcoes.aoMudarEstado,
    aoPerderCredencial: () => undefined,
  });

  return { parar: conexao.parar, entrarNoGuiche: entrar };
}
