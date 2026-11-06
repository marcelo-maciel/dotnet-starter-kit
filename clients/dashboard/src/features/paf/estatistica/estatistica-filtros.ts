// RF07 filters as the screen holds them and as `GET api/v1/paf/estatistica` reads them. The server is the
// authority (FluentValidation 400, scope 403); these checks only spare the round trip. Pure, `node --test` (D21).

export const TIPOS_ESTATISTICA = [
  { valor: 1, nome: "Atendimentos por tempo de espera" },
  { valor: 2, nome: "Atendimentos por tempo de atendimento" },
  { valor: 3, nome: "Atendimentos por guichê" },
  { valor: 4, nome: "Atendimentos por atendente" },
  { valor: 5, nome: "Atendimentos por horário de pico" },
  { valor: 6, nome: "Atendimentos não efetuados ou cancelados" },
  { valor: 7, nome: "Fichas geradas por horário de pico" },
  { valor: 8, nome: "Fichas emitidas por guichê" },
  { valor: 9, nome: "Totais por tipo de atendimento" },
  { valor: 10, nome: "Total de atendimentos" },
] as const;

export type Agrupamento = "Fila" | "Setor";

export type FiltrosEstatistica = {
  tipo: number;
  dataInicial: string;
  dataFinal: string;
  agrupamento: Agrupamento;
  unidadeId: string | null;
  filaId: string | null;
  prioridadeId: string | null;
  somenteEstacoesComAtendimento: boolean;
};

export const MENSAGEM_PERIODO_INVERTIDO = "A data final não pode ser anterior à data inicial.";
export const MENSAGEM_PERIODO_INCOMPLETO = "Informe a data inicial e a data final.";

const DATA_ISO = /^\d{4}-\d{2}-\d{2}$/;

/** Types 9 and 10 only: the grouping is a filter of those two (decided 24/08/2026), the server refuses it elsewhere. */
export function aceitaAgrupamento(tipo: number): boolean {
  return tipo === 9 || tipo === 10;
}

/** Type 3 only: "somente estações que tiveram atendimento" (RF07). */
export function aceitaSomenteEstacoes(tipo: number): boolean {
  return tipo === 3;
}

/** Calendar day in America/Sao_Paulo, `yyyy-MM-dd`: the period is on the data de operação, a BRT calendar date. */
export function hojeEmSaoPaulo(agora: Date): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Sao_Paulo", year: "numeric", month: "2-digit", day: "2-digit" }).format(agora);
}

/** Null when the period can be sent; `yyyy-MM-dd` strings compare in calendar order, no Date (and no timezone) involved. */
export function problemaDoPeriodo(dataInicial: string, dataFinal: string): string | null {
  if (!DATA_ISO.test(dataInicial) || !DATA_ISO.test(dataFinal)) return MENSAGEM_PERIODO_INCOMPLETO;
  return dataFinal < dataInicial ? MENSAGEM_PERIODO_INVERTIDO : null;
}

/**
 * Query string of the API. A filter the type does not use is left out instead of sent empty: the server refuses it
 * (400) rather than ignore it. No unidade = the caller's whole effective scope.
 */
export function consultaDaApi(filtros: FiltrosEstatistica): string {
  const q = new URLSearchParams();
  q.set("tipo", String(filtros.tipo));
  q.set("dataInicial", filtros.dataInicial);
  q.set("dataFinal", filtros.dataFinal);
  if (aceitaAgrupamento(filtros.tipo)) q.set("agrupamento", filtros.agrupamento);
  if (aceitaSomenteEstacoes(filtros.tipo) && filtros.somenteEstacoesComAtendimento) q.set("somenteEstacoesComAtendimento", "true");
  if (filtros.unidadeId) q.append("unidades", filtros.unidadeId);
  if (filtros.filaId) q.append("filas", filtros.filaId);
  if (filtros.prioridadeId) q.append("prioridades", filtros.prioridadeId);
  return q.toString();
}

/** FluentValidation sends `errors` keyed by field; CustomException sends a flat list. Both become plain lines. */
export function mensagensDeValidacao(errors: Record<string, string[]> | string[] | undefined): string[] {
  if (errors === undefined) return [];
  return Array.isArray(errors) ? errors : Object.values(errors).flat();
}
