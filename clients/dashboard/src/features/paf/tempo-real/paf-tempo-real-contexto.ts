import { createContext, useContext } from "react";

export type PafTempoReal = {
  /** This tab switched the current unit: rejoin the hub groups here and tell the other tabs (RF08). */
  unidadeTrocada: (unidadeId: string, unidade: string) => void;
};

export const PafTempoRealContexto = createContext<PafTempoReal | null>(null);

export function usePafTempoReal(): PafTempoReal {
  const valor = useContext(PafTempoRealContexto);
  if (!valor) throw new Error("usePafTempoReal fora do PafTempoRealProvider.");
  return valor;
}

// A PAF screen reloads when another tab switched the unit; the notice survives the reload only in this tab.
const CHAVE_AVISO_RECARGA = "paf.aviso-troca-unidade";

export function guardarAvisoParaRecarga(texto: string) {
  sessionStorage.setItem(CHAVE_AVISO_RECARGA, texto);
}

export function lerAvisoDeRecarga(): string | null {
  return sessionStorage.getItem(CHAVE_AVISO_RECARGA);
}

export function apagarAvisoDeRecarga() {
  sessionStorage.removeItem(CHAVE_AVISO_RECARGA);
}
