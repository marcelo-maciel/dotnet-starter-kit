import { apiFetch } from "@/lib/api-client";

// Fila cadastro (RF02): kit HTTP client and user token; the unidade is the session's current one.

export type AparenciaFila = { fonte: string | null; corFonte: string | null; corBotaoSelecionado: string | null };

export type FilaPrioridadeDto = {
  id: string;
  prioridadeId: string;
  letra: string;
  numeroInicial: number;
  ordem: number;
  corBotao: string | null;
};

/** `combinacoesSemCobertura`: ids of the grid lines no active guichê covers; their buttons are hidden (RF02). */
export type FilaDto = {
  id: string;
  unidadeId: string;
  nome: string;
  nomeExibicao: string;
  horaInicial: string | null;
  horaFinal: string | null;
  ativa: boolean;
  setorId: string | null;
  aparencia: AparenciaFila;
  prioridades: FilaPrioridadeDto[];
  combinacoesSemCobertura: string[];
};

export type PrioridadeDto = { id: string; unidadeId: string; nome: string; valor: number; ativa: boolean };

export type SetorDto = { id: string; unidadeId: string; nome: string };

export type LinhaGrade = { prioridadeId: string; letra: string; numeroInicial: number; ordem: number; corBotao: string | null };

export type DadosFila = {
  nome: string;
  nomeExibicao: string;
  horaInicial: string | null;
  horaFinal: string | null;
  setorId: string | null;
  aparencia: AparenciaFila;
};

const BASE = "/api/v1/paf";

function corpo(dados: unknown): RequestInit {
  return { body: JSON.stringify(dados) };
}

export const listarFilas = () => apiFetch<FilaDto[]>(`${BASE}/filas`);
export const listarPrioridades = () => apiFetch<PrioridadeDto[]>(`${BASE}/prioridades`);
export const listarSetores = () => apiFetch<SetorDto[]>(`${BASE}/setores`);

export function criarFila(dados: DadosFila, prioridades: LinhaGrade[]) {
  return apiFetch<string>(`${BASE}/filas`, { method: "POST", ...corpo({ ...dados, prioridades }) });
}

export function atualizarFila(id: string, dados: DadosFila) {
  return apiFetch<string>(`${BASE}/filas/${id}`, { method: "PUT", ...corpo({ id, ...dados }) });
}

export function ativarFila(id: string, ativa: boolean) {
  return apiFetch<string>(`${BASE}/filas/${id}/${ativa ? "enable" : "disable"}`, { method: "POST" });
}

export function adicionarLinha(filaId: string, linha: LinhaGrade) {
  return apiFetch<string>(`${BASE}/filas/${filaId}/prioridades`, { method: "POST", ...corpo({ filaId, ...linha }) });
}

export function atualizarLinha(filaId: string, linha: LinhaGrade) {
  return apiFetch<string>(`${BASE}/filas/${filaId}/prioridades/${linha.prioridadeId}`, {
    method: "PUT",
    ...corpo({ filaId, ...linha }),
  });
}

export function criarSetor(nome: string) {
  return apiFetch<string>(`${BASE}/setores`, { method: "POST", ...corpo({ nome }) });
}

export function renomearSetor(id: string, nome: string) {
  return apiFetch<string>(`${BASE}/setores/${id}`, { method: "PUT", ...corpo({ id, nome }) });
}
