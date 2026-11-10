import { apiFetch } from "@/lib/api-client";

// Prioridade cadastro (RF05) of the session's current unidade; the list comes from filas-api (listarPrioridades),
// shared with the fila grid so both screens read one cache entry.

const BASE = "/api/v1/paf/prioridades";

export function criarPrioridade(nome: string, valor: number) {
  return apiFetch<string>(BASE, { method: "POST", body: JSON.stringify({ nome, valor }) });
}

export function atualizarPrioridade(id: string, nome: string, valor: number) {
  return apiFetch<string>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify({ id, nome, valor }) });
}

export function desativarPrioridade(id: string) {
  return apiFetch<string>(`${BASE}/${id}/disable`, { method: "POST" });
}

export function reativarPrioridade(id: string) {
  return apiFetch<string>(`${BASE}/${id}/enable`, { method: "POST" });
}
