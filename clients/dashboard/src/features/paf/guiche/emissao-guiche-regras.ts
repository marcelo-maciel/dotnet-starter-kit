// RF11 manual emission at the guichê (F12). No imports on purpose: `node --test` runs this file as is (D21); the
// HTTP call and the printing live in emissao-guiche-api.ts and emissao-guiche.tsx.

/** Basic permission of the ordinary attendant; the emission panel and its reprint need only this one. */
export const PERMISSAO_EMITIR = "Permissions.PAF.Fichas.Emitir";

// `code` of the kit's in-flight 409 (IdempotencyEndpointFilter); any other 409 (window, coverage) is final.
export const CODIGO_EM_CURSO = "Idempotency.RequestInProgress";

export type PrioridadeEmissaoGuicheDto = { prioridadeId: string; nome: string; letra: string };

export type FilaEmissaoGuicheDto = {
  filaId: string;
  nomeExibicao: string;
  ativa: boolean;
  foraDoHorario: boolean;
  /** Unidade, station and fila active and the window open by the database clock: only then the buttons show. */
  emissivel: boolean;
  /** America/Sao_Paulo instant (ISO with offset) of the fila's last call; null = never called. */
  ultimaChamadaEm: string | null;
  /** Active prioridades covered by a guichê (C07). */
  prioridades: PrioridadeEmissaoGuicheDto[];
};

export function podeEmitirNoGuiche(permissoes: readonly string[]): boolean {
  return permissoes.includes(PERMISSAO_EMITIR);
}

/** Buttons of one grid line: none while the fila cannot emit (RF02: outside the window the button disappears). */
export function botoesDaFila(fila: FilaEmissaoGuicheDto): PrioridadeEmissaoGuicheDto[] {
  return fila.emissivel ? fila.prioridades : [];
}

/** Why a line has no buttons, for the screen; empty when it has them. */
export function situacaoDaFila(fila: FilaEmissaoGuicheDto): string {
  if (!fila.ativa) return "Inativa";
  if (fila.foraDoHorario) return "Fora do horário";
  if (!fila.emissivel) return "Indisponível";
  if (fila.prioridades.length === 0) return "Sem guichê que atenda";
  return "";
}

const formatoChamada = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
});

/** Date and time of the last call in America/Sao_Paulo; "—" for a fila never called (RF11 l.364). */
export function formatarUltimaChamada(instante: string | null): string {
  if (instante === null) return "—";
  const data = new Date(instante);
  return Number.isNaN(data.getTime()) ? "—" : formatoChamada.format(data);
}

/** How one emission POST ended. */
export type DesfechoEmissao = "sucesso" | "emCurso" | "transitoria" | "definitiva";

/** Classifies a failed POST: only a request that may have reached the server keeps its key for the resend. */
export function desfechoDoErro(status: number | null, codigo: string | null): DesfechoEmissao {
  // null status = network failure or timeout: the ficha may be stored, the resend with the key gets it back.
  if (status === null || status === 408 || status === 429 || status >= 500) return "transitoria";
  if (status === 409 && codigo === CODIGO_EM_CURSO) return "emCurso";
  return "definitiva";
}

/** The idempotency key of the emission in course; null when none is pending. */
export type EmissaoPendente = { filaId: string; prioridadeId: string; chave: string } | null;

/**
 * Key for a click. A failed request that may have reached the server leaves its key pending: clicking the SAME
 * button again resends it, so a lost response never prints a second ficha. Another button gets a new key, because
 * the kit does not compare bodies and the old key would replay the other ficha.
 */
export function chaveDoClique(
  pendente: EmissaoPendente,
  filaId: string,
  prioridadeId: string,
  novaChave: () => string,
): NonNullable<EmissaoPendente> {
  if (pendente !== null && pendente.filaId === filaId && pendente.prioridadeId === prioridadeId) return pendente;
  return { filaId, prioridadeId, chave: novaChave() };
}

/** Pending key after the answer: kept only when the server may hold the ficha without having answered it. */
export function pendenteApos(pendente: NonNullable<EmissaoPendente>, desfecho: DesfechoEmissao): EmissaoPendente {
  return desfecho === "transitoria" || desfecho === "emCurso" ? pendente : null;
}
