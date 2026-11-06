// Painel appearance (RF04 bloco Painel Chamador, RNF19). Pure module (node --test, D21).
// The cadastro (C04) stores the block nested under `painel`. A missing or malformed value keeps the screen default,
// so a painel never saved through the cadastro looks exactly as before.
import { formatoValido } from "../aparencia/contraste.ts";

export type CoresPainel = { fundo: string; filas: string; chamada: string; alternada: string };

export type AparenciaPainel = {
  /** Base font size of the whole screen; every text of the painel scales from it. */
  tamanhoFontePx: number | null;
  /** All four or none: each text color was checked against this background only as a set. */
  cores: CoresPainel | null;
  mostrarDataHora: boolean;
  /** The legacy "modo informativo" adds the list of the latest calls; "Normal" shows only the current call. */
  mostrarUltimasChamadas: boolean;
};

export const APARENCIA_PAINEL_PADRAO: AparenciaPainel = {
  tamanhoFontePx: null,
  cores: null,
  mostrarDataHora: false,
  mostrarUltimasChamadas: true,
};

// Legacy painel (tmDestaca, 300 ms) toggles the call text between the two colors; 500 ms keeps the attention cue at
// one flash per second, well under the WCAG 2.3.1 limit of three.
export const INTERVALO_ALTERNANCIA_MS = 500;

function objeto(valor: unknown): Record<string, unknown> | null {
  return typeof valor === "object" && valor !== null && !Array.isArray(valor) ? (valor as Record<string, unknown>) : null;
}

function cor(valor: unknown): string | null {
  return typeof valor === "string" && formatoValido(valor) ? valor : null;
}

export function lerAparenciaPainel(configuracao: string | null): AparenciaPainel {
  if (configuracao === null) return APARENCIA_PAINEL_PADRAO;
  let raiz: Record<string, unknown> | null;
  try {
    raiz = objeto(JSON.parse(configuracao));
  } catch {
    // A malformed block must not take the painel down; the default appearance keeps it announcing.
    return APARENCIA_PAINEL_PADRAO;
  }
  const bloco = objeto(raiz?.painel);
  if (bloco === null) return APARENCIA_PAINEL_PADRAO;
  const tamanhoFontePx = bloco.tamanhoFontePx;
  const fundo = cor(bloco.corFundo);
  const filas = cor(bloco.corFonteFilas);
  const chamada = cor(bloco.corFonteChamada);
  const alternada = cor(bloco.corFonteAlternada);
  return {
    tamanhoFontePx:
      typeof tamanhoFontePx === "number" && Number.isFinite(tamanhoFontePx) && tamanhoFontePx > 0 ? tamanhoFontePx : null,
    cores:
      fundo !== null && filas !== null && chamada !== null && alternada !== null ? { fundo, filas, chamada, alternada } : null,
    mostrarDataHora: bloco.mostrarDataHora === true,
    mostrarUltimasChamadas: bloco.modo !== "Normal",
  };
}

/** Text color of the call: alternates while highlighted (fase = tick count), steady otherwise. */
export function corDaChamada(cores: CoresPainel, destacada: boolean, fase: number): string {
  return destacada && fase % 2 === 1 ? cores.alternada : cores.chamada;
}

const FORMATO_DATA_HORA = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
});

/** Shown in America/Sao_Paulo whatever the machine's zone is (project rule: BRT across the stack). */
export function formatarDataHora(instante: Date): string {
  return FORMATO_DATA_HORA.format(instante);
}
