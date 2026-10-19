// Pure rules of the contract form (RF22, RF23). The server decides (409/400); this only explains before sending.
// Dates are ISO calendar days ("YYYY-MM-DD"), compared as text; a null end is open.

export type Vigencia = { id: string; inicioVigencia: string; fimVigencia: string | null };

export type SituacaoContrato = "Vigente" | "Futuro" | "Encerrado";

const FIM_ABERTO = "9999-12-31";

/** The calendar day in America/Sao_Paulo, the day the licence uses (server: PostgreSQL clock in that zone). */
export function hojeEmSaoPaulo(agora: Date): string {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: "America/Sao_Paulo",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(agora);
}

export function fimAntesDoInicio(inicio: string, fim: string | null): boolean {
  return fim !== null && fim < inicio;
}

/** Both ends inclusive, same rule as VigenciaContrato on the server; the contract being edited is left out. */
export function contratoSobreposto(
  existentes: readonly Vigencia[],
  candidato: Vigencia,
): Vigencia | undefined {
  const fimCandidato = candidato.fimVigencia ?? FIM_ABERTO;
  return existentes.find(
    (c) =>
      c.id !== candidato.id &&
      c.inicioVigencia <= fimCandidato &&
      (c.fimVigencia ?? FIM_ABERTO) >= candidato.inicioVigencia,
  );
}

export function situacaoContrato(contrato: Vigencia, hoje: string): SituacaoContrato {
  if (contrato.inicioVigencia > hoje) return "Futuro";
  if (contrato.fimVigencia !== null && contrato.fimVigencia < hoje) return "Encerrado";
  return "Vigente";
}
