// C17, D16: measures a background image in the browser, because the API has no image decoder. The image is drawn
// scaled down on a canvas (each sampled point is then an average of the pixels it covers, about the size of a glyph
// stroke on the screen) and every point counts, the whole image: the cadastro does not know where the screen will
// put each text (cover scaling, screen size), so the worst point anywhere decides.
import { amostrarLuminancias } from "@/features/paf/aparencia/contraste-imagem";

const LADO_MAXIMO_PX = 256;
const LIMITE_CARGA_MS = 15_000;

function carregar(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const imagem = new Image();
    // Without CORS the canvas is tainted and getImageData throws: the store must allow the dashboard origin.
    imagem.crossOrigin = "anonymous";
    const limite = window.setTimeout(() => {
      imagem.onload = null;
      imagem.onerror = null;
      reject(new Error("A imagem não carregou a tempo para medir o contraste."));
    }, LIMITE_CARGA_MS);
    imagem.onload = () => {
      window.clearTimeout(limite);
      resolve(imagem);
    };
    imagem.onerror = () => {
      window.clearTimeout(limite);
      reject(new Error("Não foi possível ler a imagem para medir o contraste (arquivo ou acesso ao armazenamento)."));
    };
    imagem.src = url;
  });
}

/** Luminance sample of the image at `url`, composed over `fundo` (the block's screen background). */
export async function medirImagem(url: string, fundo: string): Promise<string> {
  const imagem = await carregar(url);
  const maior = Math.max(imagem.naturalWidth, imagem.naturalHeight);
  if (maior === 0) throw new Error("A imagem não tem dimensões legíveis.");
  const escala = Math.min(1, LADO_MAXIMO_PX / maior);
  const largura = Math.max(1, Math.round(imagem.naturalWidth * escala));
  const altura = Math.max(1, Math.round(imagem.naturalHeight * escala));
  const canvas = document.createElement("canvas");
  canvas.width = largura;
  canvas.height = altura;
  const contexto = canvas.getContext("2d", { willReadFrequently: true });
  if (contexto === null) throw new Error("O navegador não ofereceu um canvas para medir a imagem.");
  contexto.imageSmoothingQuality = "high";
  contexto.drawImage(imagem, 0, 0, largura, altura);
  return amostrarLuminancias(contexto.getImageData(0, 0, largura, altura).data, fundo);
}
