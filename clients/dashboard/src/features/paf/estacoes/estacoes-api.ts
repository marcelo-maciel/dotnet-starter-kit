import { apiFetch } from "@/lib/api-client";
import type { BlocosEstacaoDto, Papel, TipoEstacao } from "@/features/paf/estacoes/estacoes-regras";

// Cadastro screen: kit HTTP client and user token, current unit from the session (arquitetura seção 3).

export type EstacaoFilaItem = { papel: Papel; filaId: string; ordem: number };
export type EstacaoPrioridadeItem = { prioridadeId: string; ordem: number };

export type EstacaoDto = {
  id: string;
  unidadeId: string;
  nome: string;
  tipo: TipoEstacao;
  ativa: boolean;
  filas: EstacaoFilaItem[];
  prioridades: EstacaoPrioridadeItem[];
  ramal: string | null;
  configuracao: BlocosEstacaoDto | null;
};

/** Combinations the write left without any guichê (RF02): a warning, never a refusal. */
export type CombinacaoSemCoberturaDto = { letra: string; fila: string; prioridade: string };
export type EstacaoAlteradaDto = { id: string; combinacoesSemCobertura: CombinacaoSemCoberturaDto[] };

export type FilaResumoDto = { id: string; nome: string; ativa: boolean };
export type PrioridadeResumoDto = { id: string; nome: string; ativa: boolean };

export type EstacaoCorpo = {
  nome: string;
  filas: EstacaoFilaItem[];
  prioridades: EstacaoPrioridadeItem[];
  ramal: string | null;
  configuracao: BlocosEstacaoDto;
};

export function listarEstacoes() {
  return apiFetch<EstacaoDto[]>("/api/v1/paf/estacoes");
}

export function listarFilas() {
  return apiFetch<FilaResumoDto[]>("/api/v1/paf/filas");
}

export function listarPrioridades() {
  return apiFetch<PrioridadeResumoDto[]>("/api/v1/paf/prioridades");
}

export function criarEstacao(tipo: TipoEstacao, corpo: EstacaoCorpo) {
  return apiFetch<string>("/api/v1/paf/estacoes", { method: "POST", body: JSON.stringify({ ...corpo, tipo }) });
}

/** The type is not sent: it is fixed after creation (D25). */
export function atualizarEstacao(id: string, corpo: EstacaoCorpo) {
  return apiFetch<EstacaoAlteradaDto>(`/api/v1/paf/estacoes/${encodeURIComponent(id)}`, {
    method: "PUT",
    body: JSON.stringify({ ...corpo, id }),
  });
}
