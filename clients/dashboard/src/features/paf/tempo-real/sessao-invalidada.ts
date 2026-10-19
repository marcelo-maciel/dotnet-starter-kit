// SessaoInvalidada (arquitetura seção 7, S04): the server revoked a session and is about to close its PafHub
// connections. Pure on purpose, no imports: `node --test` runs the test as is (D21).

/** Name = SessaoInvalidada.Metodo (Modules.PAF.Contracts/v1/TempoReal/SessaoInvalidada.cs). */
export const SESSAO_INVALIDADA = "SessaoInvalidada";

/** Shown when the payload carries no reason; the server always sends one. */
export const MOTIVO_PADRAO = "Sua sessão foi encerrada. Entre novamente.";

export type SessaoInvalidada = { sessaoId: string; motivo: string };

/** Anything malformed (another version of the server, a forged frame) is dropped. */
export function lerSessaoInvalidada(dado: unknown): SessaoInvalidada | null {
  if (typeof dado !== "object" || dado === null) return null;
  const d = dado as Record<string, unknown>;
  if (typeof d.sessaoId !== "string" || d.sessaoId === "") return null;
  const motivo = typeof d.motivo === "string" && d.motivo.trim() !== "" ? d.motivo : MOTIVO_PADRAO;
  return { sessaoId: d.sessaoId, motivo };
}

/**
 * Only a notice for this tab's own session logs it out: the copy of an older session's revocation must never log
 * out the session that replaced it (seção 7, second protection after the server's per-connection send). The
 * server writes the Guid in lowercase "D" and the `sid` claim too; the compare ignores case anyway.
 */
export function avisoDaPropriaSessao(aviso: SessaoInvalidada, sidAtual: string | undefined): boolean {
  if (sidAtual === undefined || sidAtual === "") return false;
  return aviso.sessaoId.toLowerCase() === sidAtual.toLowerCase();
}
