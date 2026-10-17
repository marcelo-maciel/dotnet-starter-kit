import { apiFetch } from "@/lib/api-client";

// The guichê is a person screen (arquitetura seção 3): kit HTTP client, kit user token.

/** Only active unidades of the caller's scope come back. */
export type UnidadeDto = { id: string; nome: string };

/** `ocupante` is another session's user name; null when free or when `meu`. */
export type GuicheDto = { id: string; nome: string; tipo: string; ocupante: string | null; meu: boolean };

export type AtendimentoEmCursoDto = {
  atendimentoId: string;
  fichaId: string;
  senha: string;
  estado: string;
  versao: number;
};

/** The server's view of the session: current unit, selected guichê and the atendimento in progress on it. */
export type PostoDto = {
  unidadeId: string | null;
  unidade: string | null;
  estacaoId: string | null;
  guiche: string | null;
  atendimento: AtendimentoEmCursoDto | null;
};

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

// These three reads need only Guiches.Selecionar, which the ordinary attendant has.
export function listarUnidades() {
  return apiFetch<UnidadeDto[]>("/api/v1/paf/guiche/unidades");
}

export function listarGuiches() {
  return apiFetch<GuicheDto[]>("/api/v1/paf/guiche/guiches");
}

export function obterPosto() {
  return apiFetch<PostoDto>("/api/v1/paf/guiche/posto");
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
