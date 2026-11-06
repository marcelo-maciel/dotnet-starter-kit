import { apiFetch } from "@/lib/api-client";

/** RF13 row. `prioridade` is the RF13 "tipo"; `chamadoEm` is the entry time and the stay counts from it (database clock). */
export type FichaEmAtendimentoDto = {
  atendimentoId: string;
  fichaId: string;
  senha: string;
  filaId: string;
  fila: string;
  prioridade: string;
  estacaoId: string | null;
  guiche: string | null;
  estado: "Chamado" | "EmAtendimento";
  chamadoEm: string;
  iniciadoEm: string | null;
  permanenciaSegundos: number;
  versao: number;
};

export type FilaDoFiltroDto = { id: string; nome: string };

export type FichasEmAtendimentoDto = {
  total: number;
  atendimentos: FichaEmAtendimentoDto[];
  filas: FilaDoFiltroDto[];
};

export function listarFichasEmAtendimento(filaId: string | null) {
  const filtro = filaId === null ? "" : `?filaId=${encodeURIComponent(filaId)}`;
  return apiFetch<FichasEmAtendimentoDto>(`/api/v1/paf/fichas/em-atendimento${filtro}`);
}

/** RF12 cancel (F04's route): releases the ficha's waiting atendimentos in other filas. */
export function cancelarAtendimento(atendimentoId: string) {
  return apiFetch<unknown>(`/api/v1/paf/fichas/atendimentos/${encodeURIComponent(atendimentoId)}/cancelar`, { method: "POST" });
}
