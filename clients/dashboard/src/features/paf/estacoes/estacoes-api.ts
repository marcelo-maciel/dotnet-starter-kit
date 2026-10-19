import { apiFetch } from "@/lib/api-client";
import type { BlocosEstacaoDto, Papel, TipoEstacao } from "@/features/paf/estacoes/estacoes-regras";
import type { EstacaoDesativadaDto, ImpactoDesativacaoDto } from "@/features/paf/estacoes/desativacao";
import type { VinculoAtualDto } from "@/features/paf/estacoes/vinculo-estacao";

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
  vinculoAtual: VinculoAtualDto | null;
  ultimaConexaoEmissaoEm: string | null;
  ultimaConexaoPainelEm: string | null;
};

/** The token exists only in this response: the server keeps its hash (VinculoEstacaoGeradoDto). */
export type VinculoGeradoDto = { vinculoId: string; tenant: string; token: string };

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

export function preverDesativacao(id: string) {
  return apiFetch<ImpactoDesativacaoDto>(`/api/v1/paf/estacoes/${encodeURIComponent(id)}/desativacao`);
}

/** A null `cancelarAtendimentoId` keeps the atendimento in progress until the attendant ends it. */
export function desativarEstacao(id: string, cancelarAtendimentoId: string | null) {
  return apiFetch<EstacaoDesativadaDto>(`/api/v1/paf/estacoes/${encodeURIComponent(id)}/disable`, {
    method: "POST",
    body: JSON.stringify({ cancelarAtendimentoId }),
  });
}

/** Manager's release of a stuck guichê (Guiches.Liberar); 409 while an atendimento is in progress on it. */
export function liberarGuiche(id: string) {
  return apiFetch<void>(`/api/v1/paf/guiches/${encodeURIComponent(id)}/liberar`, { method: "POST" });
}

/** The type is not sent: it is fixed after creation (D25). */
export function atualizarEstacao(id: string, corpo: EstacaoCorpo) {
  return apiFetch<EstacaoAlteradaDto>(`/api/v1/paf/estacoes/${encodeURIComponent(id)}`, {
    method: "PUT",
    body: JSON.stringify({ ...corpo, id }),
  });
}

/** Revokes the station's previous link and drops its device in the same act (RF04). */
export function gerarVinculo(id: string) {
  return apiFetch<VinculoGeradoDto>(`/api/v1/paf/estacoes/${encodeURIComponent(id)}/vinculo`, { method: "POST" });
}

/** Idempotent: revoking a station with no live link is not an error. */
export function revogarVinculo(id: string) {
  return apiFetch<void>(`/api/v1/paf/estacoes/${encodeURIComponent(id)}/vinculo/revogar`, { method: "POST" });
}
