import { duracaoDoMetadado, type TipoMidia } from "@/features/paf/midias/midias-regras";

/**
 * Duration of a local video or audio file read by the browser itself (only the metadata, from an object URL, nothing
 * leaves the machine). Null for an image, an unreadable file, or past the deadline: the operator types it then.
 */
export function lerDuracaoDoArquivo(arquivo: File, tipo: TipoMidia, prazoMs: number): Promise<number | null> {
  if (tipo === "Imagem") return Promise.resolve(null);
  return new Promise((resolve) => {
    const elemento = document.createElement(tipo === "Video" ? "video" : "audio");
    const url = URL.createObjectURL(arquivo);
    let prazo = 0;
    const terminar = (duracao: number | null) => {
      window.clearTimeout(prazo);
      // Detached first: emptying the source fires an error event that must not finish twice.
      elemento.onloadedmetadata = null;
      elemento.onerror = null;
      elemento.removeAttribute("src");
      elemento.load();
      URL.revokeObjectURL(url);
      resolve(duracao);
    };
    prazo = window.setTimeout(() => terminar(null), prazoMs);
    elemento.preload = "metadata";
    elemento.onloadedmetadata = () => terminar(duracaoDoMetadado(elemento.duration));
    elemento.onerror = () => terminar(null);
    elemento.src = url;
  });
}
