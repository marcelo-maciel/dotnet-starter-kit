// Pure rules of the RF12 screen, run by node --test (D21).

export const PERMISSAO_CANCELAR = "Permissions.PAF.FichasDoDia.Cancelar";
export const PERMISSAO_REABILITAR = "Permissions.PAF.FichasDoDia.Reabilitar";

/** Names the server accepts, exactly (EstadoAtendimento); anything else is a 400. */
export const ESTADOS = ["Aguardando", "Chamado", "EmAtendimento", "Encerrado", "Cancelado"] as const;
export type Estado = (typeof ESTADOS)[number];

const ROTULOS: Record<Estado, string> = {
  Aguardando: "Aguardando",
  Chamado: "Chamado",
  EmAtendimento: "Em atendimento",
  Encerrado: "Encerrado",
  Cancelado: "Cancelado",
};

/** Null fields are left out of the query: the server then lists every fila, every state, and its own today. */
export type FiltroFichasDoDia = { data: string | null; filaId: string | null; estado: Estado | null };

export function consultaDosFiltros(filtro: FiltroFichasDoDia): string {
  const parametros = new URLSearchParams();
  if (filtro.data) parametros.set("data", filtro.data);
  if (filtro.filaId) parametros.set("filaId", filtro.filaId);
  if (filtro.estado) parametros.set("estado", filtro.estado);
  const texto = parametros.toString();
  return texto === "" ? "" : `?${texto}`;
}

export function rotuloEstado(estado: string): string {
  return (ROTULOS as Record<string, string>)[estado] ?? estado;
}

export type AcoesAtendimento = { cancelar: boolean; reabilitar: boolean };

/**
 * Mirrors the server (F04: Aguardando, Chamado and EmAtendimento cancel; F05: only Cancelado comes back) and
 * the caller's permissions, so no button is offered that can only fail. The server still decides (409, 403).
 */
export function acoesDoAtendimento(estado: string, permissoes: readonly string[]): AcoesAtendimento {
  const cancelavel = estado === "Aguardando" || estado === "Chamado" || estado === "EmAtendimento";
  return {
    cancelar: cancelavel && permissoes.includes(PERMISSAO_CANCELAR),
    reabilitar: estado === "Cancelado" && permissoes.includes(PERMISSAO_REABILITAR),
  };
}

/** "m:ss" under an hour, "h:mm:ss" from there; a negative or fractional value never reaches the screen. */
export function formatarEspera(segundos: number): string {
  const total = Math.max(0, Math.floor(segundos));
  const horas = Math.floor(total / 3600);
  const minutos = Math.floor((total % 3600) / 60);
  const resto = String(total % 60).padStart(2, "0");
  return horas > 0 ? `${horas}:${String(minutos).padStart(2, "0")}:${resto}` : `${minutos}:${resto}`;
}

/** Number and display name of the guichê when the cadastro has them, else the station name; "—" when none. */
export function descricaoGuiche(linha: {
  guiche: string | null;
  numeroGuiche: number | null;
  nomeExibicaoGuiche: string | null;
}): string {
  if (linha.numeroGuiche !== null) {
    return linha.nomeExibicaoGuiche ? `${linha.numeroGuiche} - ${linha.nomeExibicaoGuiche}` : String(linha.numeroGuiche);
  }
  return linha.guiche ?? "—";
}
