import { apiFetch } from "@/lib/api-client";

export type EstadoAtendimento = "Aguardando" | "Chamado" | "EmAtendimento" | "Encerrado" | "Cancelado";

/** `fimEm` is the close or the cancel; `tempoTotalSegundos` runs from `iniciadoEm` to the end, or to the server's now. */
export type AtendimentoDaFichaDto = {
  atendimentoId: string;
  filaId: string;
  fila: string;
  prioridade: string;
  estado: EstadoAtendimento;
  guiche: string | null;
  criadoEm: string;
  chamadoEm: string | null;
  iniciadoEm: string | null;
  fimEm: string | null;
  tempoTotalSegundos: number | null;
  atendimentoOrigemId: string | null;
  versao: number;
};

/** More than one only when a letter changed hands during the day and two senhas print the same. */
export type FichaConsultadaDto = {
  fichaId: string;
  senha: string;
  dataOperacional: string;
  criadoEm: string;
  fila: string;
  prioridade: string;
  atendimentos: AtendimentoDaFichaDto[];
};

/** RF18 (F14): the current unit's ficha by printed senha and day; 404 when there is none. */
export function consultarFicha(senha: string, data: string) {
  const parametros = new URLSearchParams({ senha, data });
  return apiFetch<FichaConsultadaDto[]>(`/api/v1/paf/fichas/consulta?${parametros.toString()}`);
}

// RF12 cancel (F04) and reabilitar (F05): the same routes as the fichas do dia screen, never a copy of the rule.
export function cancelarAtendimentoDaFicha(atendimentoId: string) {
  return apiFetch<unknown>(`/api/v1/paf/fichas/atendimentos/${atendimentoId}/cancelar`, { method: "POST" });
}

export function reabilitarAtendimentoDaFicha(atendimentoId: string) {
  return apiFetch<unknown>(`/api/v1/paf/fichas/atendimentos/${atendimentoId}/reabilitar`, { method: "POST" });
}
