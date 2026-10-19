import { apiFetch } from "@/lib/api-client";
import type { CorpoProgramacao } from "@/features/paf/programacoes/programacoes-regras";

// Programação de mídias (RF10) of the session's current unidade, over the M05 routes.

const BASE = "/api/v1/paf/programacoes-midia";

/** ProgramacaoMidiaDto: dates "yyyy-MM-dd", HoraInicial "HH:mm:ss", end in seconds from midnight (86400 = midnight). */
export type ProgramacaoDto = {
  id: string;
  unidadeId: string;
  midiaId: string;
  midiaDescricao: string;
  midiaAtiva: boolean;
  nome: string;
  dataInicial: string;
  dataFinal: string;
  horaInicial: string;
  repeticoes: number;
  dias: number;
  ativa: boolean;
  versao: number;
  tempoMidiaSegundos: number;
  tempoTotalSegundos: number;
  horaFinalSegundos: number;
};

export type PaginaDto<T> = { items: T[]; pageNumber: number; pageSize: number; totalCount: number; totalPages: number };

export function pesquisarProgramacoes(consulta: string) {
  return apiFetch<PaginaDto<ProgramacaoDto>>(`${BASE}?${consulta}`);
}

export function criarProgramacao(corpo: CorpoProgramacao) {
  return apiFetch<ProgramacaoDto>(BASE, { method: "POST", body: JSON.stringify(corpo) });
}

export function atualizarProgramacao(id: string, corpo: CorpoProgramacao, versao: number) {
  return apiFetch<ProgramacaoDto>(`${BASE}/${encodeURIComponent(id)}`, {
    method: "PUT",
    body: JSON.stringify({ ...corpo, id, versao }),
  });
}

export function ativarProgramacao(id: string) {
  return apiFetch<ProgramacaoDto>(`${BASE}/${encodeURIComponent(id)}/enable`, { method: "POST" });
}

export function desativarProgramacao(id: string) {
  return apiFetch<ProgramacaoDto>(`${BASE}/${encodeURIComponent(id)}/disable`, { method: "POST" });
}

export function excluirProgramacao(id: string) {
  return apiFetch<void>(`${BASE}/${encodeURIComponent(id)}`, { method: "DELETE" });
}
