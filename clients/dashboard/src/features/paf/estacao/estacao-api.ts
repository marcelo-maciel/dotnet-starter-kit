import { estacaoFetch } from "@/features/paf/estacao/estacao-http";
import type { EstadoPainelDto } from "@/features/paf/painel/painel-estado";

/**
 * One totem button. NOT served by the API yet: `ConfiguracaoEstacaoDto` has no filas/prioridades
 * (arquitetura seção 5 says the totem reads them from its own configuration). Shape requested from
 * the backend; until it lands the totem shows "sem filas" instead of buttons.
 */
export type BotaoEmissao = {
  filaId: string;
  fila: string;
  prioridadeId: string;
  prioridade: string;
  letra: string;
  ordem: number;
};

export type ConfiguracaoEstacaoDto = {
  estacaoId: string;
  unidadeId: string;
  nome: string;
  tipo: string;
  /** JSON block of the station type, as a string. */
  configuracao: string | null;
  botoes?: BotaoEmissao[];
};

export type FichaEmitidaDto = {
  fichaId: string;
  atendimentoId: string;
  identificadorPublico: string;
  senha: string;
  letra: string;
  numero: number;
  unidade: string;
  fila: string;
  prioridade: string;
  emitidaEm: string;
};

export function obterConfiguracaoEmissao() {
  return estacaoFetch<ConfiguracaoEstacaoDto>("Emissao", "/api/v1/paf/estacao/emissao/configuracao", {
    method: "GET",
  });
}

export function obterConfiguracaoPainel() {
  return estacaoFetch<ConfiguracaoEstacaoDto>("Painel", "/api/v1/paf/estacao/painel/configuracao", {
    method: "GET",
  });
}

export function emitirFicha(filaId: string, prioridadeId: string, chaveIdempotencia: string) {
  return estacaoFetch<FichaEmitidaDto>("Emissao", "/api/v1/paf/estacao/emissao/fichas", {
    method: "POST",
    headers: { "Idempotency-Key": chaveIdempotencia },
    body: JSON.stringify({ filaId, prioridadeId }),
  });
}

export function obterEstadoPainel() {
  return estacaoFetch<EstadoPainelDto>("Painel", "/api/v1/paf/estacao/painel/estado", { method: "GET" });
}

/** Reads one numeric key of the station's JSON block; absent, malformed or non-positive → `padrao`. */
export function lerNumeroDaConfiguracao(configuracao: string | null, chave: string, padrao: number): number {
  if (configuracao === null) return padrao;
  try {
    const valor = (JSON.parse(configuracao) as Record<string, unknown>)[chave];
    return typeof valor === "number" && Number.isFinite(valor) && valor > 0 ? valor : padrao;
  } catch {
    // A malformed block must not take the panel down; the default keeps it running.
    return padrao;
  }
}
