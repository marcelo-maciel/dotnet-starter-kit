// What the guichê screen does with a realtime notice, as a pure function (arquitetura seção 7, ADR 0005).
// Notices of two instances can arrive in the reverse order of their commits, and every one arrives twice
// (direct send + outbox): the highest Versao applied per atendimento is remembered here, because the
// posto in the query cache only catches up after the re-read the first notice triggered.

/** What SenhaChamada, SenhaRechamada and AtendimentoAtualizado share, as PafHub sends them (camelCase). */
export type AvisoAtendimento = { atendimentoId: string; versao: number };

/** The atendimento the screen shows, from GET guiche/posto. */
export type AtendimentoNaTela = { atendimentoId: string; versao: number };

export type DecisaoAviso = {
  versoes: Record<string, number>;
  /** The server's post must be read again. */
  reler: boolean;
  /** Final state to show as the last outcome, when the notice closed the atendimento on screen. */
  estadoFinal: string | null;
};

export const ESTADOS_FINAIS = new Set(["Encerrado", "Cancelado"]);

/**
 * Only a notice about the atendimento on screen with a Versao above both the posto's and every notice
 * already applied changes anything. `estado` is null for SenhaChamada and SenhaRechamada.
 */
export function decidirAviso(
  versoes: Record<string, number>,
  atual: AtendimentoNaTela | null | undefined,
  aviso: AvisoAtendimento,
  estado: string | null,
): DecisaoAviso {
  const ignorado: DecisaoAviso = { versoes, reler: false, estadoFinal: null };
  if (atual?.atendimentoId !== aviso.atendimentoId) return ignorado;
  const aplicada = Math.max(versoes[aviso.atendimentoId] ?? 0, atual.versao);
  if (aviso.versao <= aplicada) return ignorado;
  return {
    versoes: { ...versoes, [aviso.atendimentoId]: aviso.versao },
    reler: true,
    estadoFinal: estado !== null && ESTADOS_FINAIS.has(estado) ? estado : null,
  };
}
