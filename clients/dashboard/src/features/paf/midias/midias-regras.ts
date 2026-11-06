// Mídia cadastro (RF03, M10): what the screen checks before asking the server. The server is the authority
// (FormatosMidia, RegrasMidia); this mirrors its table and limits only to spare a round trip and an upload.

export type TipoMidia = "Video" | "Imagem" | "Audio";

export type FormatoMidia = { extensao: string; tipo: TipoMidia; mime: string };

// Same rows and order as FormatosMidia.Aceitos (Modules.PAF/Midias/FormatosMidia.cs), so the refusal lists the
// formats exactly as the server's message does.
export const FORMATOS: readonly FormatoMidia[] = [
  { extensao: ".mp4", tipo: "Video", mime: "video/mp4" },
  { extensao: ".webm", tipo: "Video", mime: "video/webm" },
  { extensao: ".jpg", tipo: "Imagem", mime: "image/jpeg" },
  { extensao: ".jpeg", tipo: "Imagem", mime: "image/jpeg" },
  { extensao: ".png", tipo: "Imagem", mime: "image/png" },
  { extensao: ".webp", tipo: "Imagem", mime: "image/webp" },
  { extensao: ".gif", tipo: "Imagem", mime: "image/gif" },
  { extensao: ".mp3", tipo: "Audio", mime: "audio/mpeg" },
  { extensao: ".m4a", tipo: "Audio", mime: "audio/mp4" },
];

export const LISTA_FORMATOS = FORMATOS.map((f) => f.extensao).join(", ");

export const MENSAGEM_FORMATO = `Formato não aceito. Formatos aceitos: ${LISTA_FORMATOS}.`;

// Same text as PedirEnvioMidiaCommandHandler.MensagemTipoDaTroca.
export const MENSAGEM_TIPO_DA_TROCA = "A troca de arquivo mantém o tipo da mídia. Envie um arquivo do mesmo tipo.";

/** `accept` of the file input: the browser's picker shows only the accepted extensions. */
export const ACEITOS_NO_SELETOR = FORMATOS.map((f) => f.extensao).join(",");

export const ROTULO_TIPO: Record<TipoMidia, string> = { Video: "Vídeo", Imagem: "Imagem", Audio: "Áudio" };

export const DESCRICAO_MAXIMA = 200; // Midia.DescricaoMaxLength
export const DURACAO_MAXIMA_SEGUNDOS = 86_400; // Midia.DuracaoMaximaSegundos

/** The format of the last extension, case-insensitive (`.MP4` is `.mp4`), as FormatosMidia.PorNomeDeArquivo. */
export function formatoDoArquivo(nome: string): FormatoMidia | null {
  const ponto = nome.lastIndexOf(".");
  if (ponto < 0) return null;
  const extensao = nome.slice(ponto).toLowerCase();
  return FORMATOS.find((f) => f.extensao === extensao) ?? null;
}

export type ArquivoConferido = { ok: true; formato: FormatoMidia } | { ok: false; mensagem: string };

/** A new file, of any accepted format. */
export function conferirArquivoNovo(nome: string): ArquivoConferido {
  const formato = formatoDoArquivo(nome);
  return formato === null ? { ok: false, mensagem: MENSAGEM_FORMATO } : { ok: true, formato };
}

/** A swap keeps the mídia's type (the server refuses otherwise). */
export function conferirArquivoDaTroca(nome: string, tipoDaMidia: TipoMidia): ArquivoConferido {
  const conferido = conferirArquivoNovo(nome);
  if (!conferido.ok) return conferido;
  if (conferido.formato.tipo !== tipoDaMidia) return { ok: false, mensagem: MENSAGEM_TIPO_DA_TROCA };
  return conferido;
}

export type CadastroValidado = { ok: true; descricao: string; duracaoSegundos: number } | { ok: false; mensagem: string };

export function validarCadastro(descricao: string, duracaoTexto: string): CadastroValidado {
  const descricaoLimpa = descricao.trim();
  if (descricaoLimpa === "") return { ok: false, mensagem: "A descrição é obrigatória." };
  if (descricaoLimpa.length > DESCRICAO_MAXIMA) {
    return { ok: false, mensagem: `A descrição tem no máximo ${DESCRICAO_MAXIMA} caracteres.` };
  }
  const texto = duracaoTexto.trim();
  // A regex, not Number(): Number accepts "1e3", "0x10" and "" (as 0), none of which is what the operator typed.
  if (!/^\d+$/.test(texto)) return { ok: false, mensagem: "A duração da exibição é um número inteiro de segundos." };
  const duracao = Number(texto);
  if (duracao < 1 || duracao > DURACAO_MAXIMA_SEGUNDOS) {
    return { ok: false, mensagem: `A duração da exibição vai de 1 a ${DURACAO_MAXIMA_SEGUNDOS} segundos.` };
  }
  return { ok: true, descricao: descricaoLimpa, duracaoSegundos: duracao };
}

/**
 * Display duration suggested by the file's own metadata (video and audio). Rounded up: the painel plays a video
 * until it ends or the duration runs out, whichever comes first, so rounding down would cut the last second.
 * Null when the browser could not tell (an image, a stream without duration, a broken file).
 */
export function duracaoDoMetadado(segundos: number): number | null {
  if (!Number.isFinite(segundos) || segundos <= 0) return null;
  return Math.min(Math.max(1, Math.ceil(segundos)), DURACAO_MAXIMA_SEGUNDOS);
}

/** Percentage shown during the direct upload; 0 while the total is unknown. */
export function percentualEnviado(enviados: number, total: number): number {
  if (!Number.isFinite(total) || total <= 0) return 0;
  return Math.min(100, Math.max(0, Math.floor((enviados / total) * 100)));
}

export function formatarBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1).replace(".", ",")} MB`;
  if (bytes >= 1024) return `${Math.ceil(bytes / 1024)} KB`;
  return `${bytes} B`;
}
