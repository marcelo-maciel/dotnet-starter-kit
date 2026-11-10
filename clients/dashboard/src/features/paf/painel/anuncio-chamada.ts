// RF06 Geral on the painel (C20): "mensagem exibida ao chamar a ficha" and "chamar ficha por voz", which D29 turned
// from a switch into a choice of what the voice says. RNF19 wants every call in voice and text, so no value of the
// parameter, known or not, ever yields an empty speech.
import { textoFalado } from "./voz.ts";

export const FALA_SO_SENHA = "senha";
export const FALA_SENHA_E_MENSAGEM = "senhaEMensagem";

export type AnuncioDaChamada = { mensagemAoChamar: string; chamarFichaPorVoz: string };

export const ANUNCIO_PADRAO: AnuncioDaChamada = { mensagemAoChamar: "", chamarFichaPorVoz: FALA_SO_SENHA };

/** The `anuncioDaChamada` of the station configuration; absent or malformed (an older API) = no message, senha only. */
export function lerAnuncioDaChamada(dado: unknown): AnuncioDaChamada {
  if (typeof dado !== "object" || dado === null) return ANUNCIO_PADRAO;
  const { mensagemAoChamar, chamarFichaPorVoz } = dado as Record<string, unknown>;
  return {
    mensagemAoChamar: typeof mensagemAoChamar === "string" ? mensagemAoChamar.trim() : "",
    chamarFichaPorVoz: typeof chamarFichaPorVoz === "string" ? chamarFichaPorVoz : FALA_SO_SENHA,
  };
}

/** What the painel speaks for a call: always the senha; the message after it only when the parameter asks for it. */
export function textoDoAnuncio(senha: string, anuncio: AnuncioDaChamada): string {
  const falada = textoFalado(senha);
  if (anuncio.chamarFichaPorVoz !== FALA_SENHA_E_MENSAGEM || anuncio.mensagemAoChamar === "") return falada;
  return `${falada}. ${anuncio.mensagemAoChamar}`;
}
