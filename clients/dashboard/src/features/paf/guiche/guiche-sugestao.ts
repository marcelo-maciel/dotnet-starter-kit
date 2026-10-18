// RF04: the guichê screen suggests the last guichê chosen on this machine, to cut the error of picking the
// neighbour's. Convenience, not a guarantee: the server keeps the one-session-per-guichê rule and the choice
// stays the user's. No imports on purpose: `node --test` runs this file as is (D21).

/** One key per browser; the tenant and unidade go inside the value so the reader can refuse a foreign one. */
export const CHAVE_ULTIMO_GUICHE = "fsh.dashboard.paf.guiche.ultimo";

type UltimoGuiche = { tenant: string; unidadeId: string; estacaoId: string };

export function serializarUltimoGuiche(tenant: string, unidadeId: string, estacaoId: string): string {
  const valor: UltimoGuiche = { tenant, unidadeId, estacaoId };
  return JSON.stringify(valor);
}

function lerUltimoGuiche(bruto: string): UltimoGuiche | null {
  let valor: unknown;
  try {
    valor = JSON.parse(bruto);
  } catch {
    // Anything in the key that is not our JSON (edited by hand, older format) just means no suggestion.
    return null;
  }
  if (typeof valor !== "object" || valor === null) return null;
  const { tenant, unidadeId, estacaoId } = valor as Record<string, unknown>;
  if (typeof tenant !== "string" || typeof unidadeId !== "string" || typeof estacaoId !== "string") return null;
  return { tenant, unidadeId, estacaoId };
}

/**
 * The guichê id to pre-mark, or "" for none. Only a guichê saved for this tenant and unidade that is still in
 * the list of active guichês of the current unidade is suggested, so a deactivated or foreign one never is.
 */
export function sugerirGuiche(
  bruto: string | null,
  tenant: string,
  unidadeId: string,
  guiches: readonly { id: string }[],
): string {
  if (bruto === null) return "";
  const ultimo = lerUltimoGuiche(bruto);
  if (ultimo === null || ultimo.tenant !== tenant || ultimo.unidadeId !== unidadeId) return "";
  return guiches.some((g) => g.id === ultimo.estacaoId) ? ultimo.estacaoId : "";
}
