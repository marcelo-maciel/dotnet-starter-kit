// RF04 "selecionar ficha para atendimento" on the RF12 screen (F21). No imports on purpose: `node --test` runs this
// file as is (D21). The composition itself is F13's `chamarProxima`; this module only supplies its `chamar`.

export const PERMISSAO_SELECIONAR_FICHA = "Permissions.PAF.Atendimentos.SelecionarFicha";

/** The guichê steps `chamarProxima` (guiche-fluxo.ts) runs; same shape, declared here to keep the file import-free. */
export type PassosEscolhida<Atual> = {
  confirmar: (pergunta: string) => boolean;
  operar: (atual: Atual, operacao: "cancelar" | "encerrar") => Promise<unknown>;
  chamar: () => Promise<unknown>;
};

export type ApiSelecao<Atual> = {
  confirmar: (pergunta: string) => boolean;
  operar: (atual: Atual, operacao: "cancelar" | "encerrar") => Promise<unknown>;
  chamarEscolhida: (atendimentoId: string) => Promise<unknown>;
};

/**
 * The RF15 steps with the call pointed at the chosen atendimento instead of the next one: the server re-validates
 * the choice under the ficha lock, so the screen sends only the id.
 */
export function passosParaEscolhida<Atual>(atendimentoId: string, api: ApiSelecao<Atual>): PassosEscolhida<Atual> {
  return {
    confirmar: api.confirmar,
    operar: api.operar,
    chamar: () => api.chamarEscolhida(atendimentoId),
  };
}

/**
 * The waiting list answers 409 when this guichê has the option off or no guichê is selected, and 403 without the
 * permission: all mean "the action does not exist here", so the section is hidden instead of showing an error.
 * Any other failure (network, 5xx) is shown, never swallowed.
 */
export function selecaoIndisponivel(status: number | null): boolean {
  return status === 403 || status === 409;
}

/** 1-based place of each waiting atendimento in the order the call would pick it (the server's order). */
export function posicaoNaChamada(emEspera: readonly { atendimentoId: string }[]): Map<string, number> {
  return new Map(emEspera.map((linha, indice) => [linha.atendimentoId, indice + 1]));
}
