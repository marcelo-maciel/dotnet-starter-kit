import { apiFetch } from "@/lib/api-client";

/** RF08 user as the server lists it: only users whose whole scope fits in the caller's. `unidades[0]` is the principal. */
export type UsuarioPafDto = {
  id: string;
  nome: string;
  sobrenome: string;
  email: string;
  ativo: boolean;
  funcao: string | null;
  ramal: string | null;
  escopoEmpresa: boolean;
  unidades: string[];
  papeis: string[];
};

export type AtendimentoEmCursoDto = {
  atendimentoId: string;
  unidadeId: string;
  senha: string;
  estado: string;
  estacaoId: string | null;
};

export type NovoUsuarioPaf = {
  nome: string;
  sobrenome: string;
  email: string;
  senha: string;
  confirmacaoSenha: string;
  funcao: string | null;
  ramal: string | null;
  papel: string;
  unidades: string[];
  escopoEmpresa: boolean;
};

export const PAPEIS_PAF = ["Administrador", "Gerente", "Atendente"] as const;

const BASE = "/api/v1/paf/usuarios";

export function listarUsuarios() {
  return apiFetch<UsuarioPafDto[]>(BASE);
}

export function criarUsuario(novo: NovoUsuarioPaf) {
  return apiFetch<string>(BASE, { method: "POST", body: JSON.stringify(novo) });
}

export function atualizarUsuario(id: string, nome: string, sobrenome: string, funcao: string | null, ramal: string | null) {
  return apiFetch<string>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify({ nome, sobrenome, funcao, ramal }) });
}

export function definirEscopo(id: string, unidades: string[], escopoEmpresa: boolean, cancelarAtendimentosEmCurso: boolean) {
  return apiFetch<string>(`${BASE}/${id}/unidades`, {
    method: "PUT",
    body: JSON.stringify({ usuarioId: id, unidades, escopoEmpresa, cancelarAtendimentosEmCurso }),
  });
}

export function desativarUsuario(id: string, cancelarAtendimentosEmCurso: boolean) {
  return apiFetch<string>(`${BASE}/${id}/desativar`, { method: "POST", body: JSON.stringify({ cancelarAtendimentosEmCurso }) });
}

export function copiarPerfil(id: string, usuarioOrigemId: string, cancelarAtendimentosEmCurso: boolean) {
  return apiFetch<string>(`${BASE}/${id}/copiar-perfil`, {
    method: "POST",
    body: JSON.stringify({ usuarioOrigemId, cancelarAtendimentosEmCurso }),
  });
}

export function listarAtendimentosEmCurso(id: string) {
  return apiFetch<AtendimentoEmCursoDto[]>(`${BASE}/${id}/atendimentos-em-curso`);
}

/** C16: the administrator sets another user's password; every session of that user drops on success. */
export function definirSenha(id: string, senha: string, confirmacaoSenha: string) {
  return apiFetch<string>(`${BASE}/${id}/senha`, { method: "POST", body: JSON.stringify({ senha, confirmacaoSenha }) });
}
