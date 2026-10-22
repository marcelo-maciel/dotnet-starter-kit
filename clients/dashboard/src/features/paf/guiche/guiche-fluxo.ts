// Composition of the existing guichê calls for RF15 item 4 and RF16 "encerrar e chamar". No imports on purpose:
// `node --test` runs this file as is (D21), and F21 reuses it with its own `chamar`.

export const MENSAGEM_NAO_ATENDENDO = "A estação não está atendendo.";

export type AtendimentoNaTela = { atendimentoId: string; senha: string; estado: string };

export type PassosGuiche = {
  confirmar: (pergunta: string) => boolean;
  operar: (atual: AtendimentoNaTela, operacao: "cancelar" | "encerrar") => Promise<unknown>;
  chamar: () => Promise<unknown>;
};

/** What the screen shows when a step refused locally, without any request. */
export class RecusaGuiche extends Error {}

export function perguntaCancelarEChamar(senha: string): string {
  return `A senha ${senha} foi chamada e não iniciada. Cancelar por não comparecimento e chamar a próxima?`;
}

export function perguntaEncerrarEChamar(senha: string): string {
  return `Encerrar o atendimento da senha ${senha} e chamar a próxima?`;
}

/**
 * RF15 item 4: a called senha is cancelled (no-show) and a started one is closed, each only after the operator
 * confirms, and the next is called only when that step succeeded. Returns false when the operator refused.
 */
export async function chamarProxima(atual: AtendimentoNaTela | null, passos: PassosGuiche): Promise<boolean> {
  if (atual !== null) {
    const chamado = atual.estado === "Chamado";
    const pergunta = chamado ? perguntaCancelarEChamar(atual.senha) : perguntaEncerrarEChamar(atual.senha);
    if (!passos.confirmar(pergunta)) return false;
    await passos.operar(atual, chamado ? "cancelar" : "encerrar");
  }
  await passos.chamar();
  return true;
}

/**
 * RF16: only a started atendimento is closed; a called one leaves by the four RF15 exits, so the screen answers
 * like the server's encerrar and writes nothing.
 */
export async function encerrarEChamar(atual: AtendimentoNaTela | null, passos: PassosGuiche): Promise<void> {
  if (atual?.estado !== "EmAtendimento") throw new RecusaGuiche(MENSAGEM_NAO_ATENDENDO);
  await passos.operar(atual, "encerrar");
  await passos.chamar();
}
