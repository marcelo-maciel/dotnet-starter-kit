import { apiFetch } from "@/lib/api-client";

// The guichê is a person screen (arquitetura seção 3): kit HTTP client, kit user token.

export type UnidadeDto = { id: string; nome: string; documentoFiscal: string | null; ativa: boolean };

export type EstacaoDto = { id: string; unidadeId: string; nome: string; tipo: string; ativa: boolean };

export type ChamadaDto = {
  atendimentoId: string;
  fichaId: string;
  senha: string;
  filaId: string;
  prioridadeId: string;
  versao: number;
};

export type AtendimentoAlteradoDto = { atendimentoId: string; estado: string; versao: number };

export type OperacaoAtendimento = "rechamar" | "iniciar" | "encerrar" | "cancelar";

const TIPOS_COM_GUICHE = new Set(["Guiche", "EmissaoGuiche", "GuichePainel"]);

// Gap reported to the backend: both lists need Unidades.View / Estacoes.View, which the basic
// attendant does not have; there is no read of the session's own units and guichês yet.
export function listarUnidades() {
  return apiFetch<UnidadeDto[]>("/api/v1/paf/unidades");
}

export async function listarGuiches() {
  const estacoes = await apiFetch<EstacaoDto[]>("/api/v1/paf/estacoes");
  return estacoes.filter((e) => e.ativa && TIPOS_COM_GUICHE.has(e.tipo));
}

export function definirUnidadeCorrente(unidadeId: string) {
  return apiFetch<void>("/api/v1/paf/sessao/unidade-corrente", {
    method: "POST",
    body: JSON.stringify({ unidadeId }),
  });
}

export function definirGuiche(estacaoId: string) {
  return apiFetch<void>("/api/v1/paf/sessao/guiche", {
    method: "POST",
    body: JSON.stringify({ estacaoId }),
  });
}

export function chamarProxima() {
  return apiFetch<ChamadaDto>("/api/v1/paf/guiche/chamar-proximo", { method: "POST" });
}

export function operarAtendimento(atendimentoId: string, operacao: OperacaoAtendimento) {
  return apiFetch<AtendimentoAlteradoDto>(`/api/v1/paf/guiche/atendimentos/${atendimentoId}/${operacao}`, {
    method: "POST",
  });
}
