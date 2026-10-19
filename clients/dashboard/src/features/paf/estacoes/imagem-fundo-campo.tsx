import { useCallback, useEffect, useId, useRef, useState } from "react";
import { Label } from "@/components/ui/label";
import { ApiRequestError } from "@/lib/api-client";
import { formatoValido } from "@/features/paf/aparencia/contraste";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { obterUrlMidia, type MidiaResumoDto } from "@/features/paf/estacoes/estacoes-api";
import type { ImagemFundoDto } from "@/features/paf/estacoes/estacoes-regras";
import { medirImagem } from "@/features/paf/estacoes/medir-imagem";

type Estado = { tipo: "ocioso" } | { tipo: "medindo" } | { tipo: "erro"; mensagem: string };

/**
 * C17: picks an image mídia as the block background and measures it. The sample depends on the file (mídia version)
 * and on the block background under translucent pixels, so it is measured again whenever either changes, and once
 * when the form opens, since a stored sample may describe a file swapped since.
 */
export function ImagemFundoCampo({
  rotulo,
  valor,
  fundo,
  midias,
  onChange,
}: {
  rotulo: string;
  valor: ImagemFundoDto | null;
  /** The block's screen background color under the image; the painel needs its CorFundo first. */
  fundo: string;
  /** Null when the mídias could not be listed (no Midias.View). */
  midias: MidiaResumoDto[] | null;
  onChange: (valor: ImagemFundoDto | null) => void;
}) {
  const id = useId();
  const [estado, setEstado] = useState<Estado>({ tipo: "ocioso" });
  const medidaRef = useRef<string | null>(null);

  const medir = useCallback(
    async (midia: MidiaResumoDto) => {
      const cor = fundo.trim();
      medidaRef.current = `${midia.id}:${midia.versao}:${cor}`;
      if (!formatoValido(cor)) {
        setEstado({ tipo: "erro", mensagem: "Informe a cor de fundo do bloco (#RRGGBB) antes de escolher a imagem." });
        return;
      }
      setEstado({ tipo: "medindo" });
      try {
        const { url } = await obterUrlMidia(midia.id);
        const luminancias = await medirImagem(url, cor);
        onChange({ midiaId: midia.id, versaoMidia: midia.versao, luminancias });
        setEstado({ tipo: "ocioso" });
      } catch (erro) {
        console.warn("[paf-estacoes] medição da imagem de fundo falhou", erro);
        setEstado({ tipo: "erro", mensagem: erro instanceof Error && !(erro instanceof ApiRequestError) ? erro.message : mensagemAmigavel(erro) });
      }
    },
    [fundo, onChange],
  );

  useEffect(() => {
    if (valor === null || midias === null) return;
    const midia = midias.find((m) => m.id === valor.midiaId);
    if (midia === undefined || medidaRef.current === `${midia.id}:${midia.versao}:${fundo.trim()}`) return;
    // Deferred: the measurement updates this component's state, never synchronously inside the effect.
    const timer = window.setTimeout(() => void medir(midia), 0);
    return () => window.clearTimeout(timer);
  }, [valor, midias, fundo, medir]);

  const imagens = (midias ?? []).filter((m) => m.tipo === "Imagem" && (m.ativa || m.id === valor?.midiaId));

  return (
    <div className="space-y-1" data-imagem-fundo-campo={rotulo}>
      <Label htmlFor={id}>{rotulo}</Label>
      <select
        id={id}
        className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-2"
        value={valor?.midiaId ?? ""}
        disabled={midias === null || estado.tipo === "medindo"}
        onChange={(e) => {
          const midia = imagens.find((m) => m.id === e.target.value);
          if (midia === undefined) {
            medidaRef.current = null;
            setEstado({ tipo: "ocioso" });
            onChange(null);
            return;
          }
          void medir(midia);
        }}
      >
        <option value="">Sem imagem</option>
        {imagens.map((m) => (
          <option key={m.id} value={m.id}>
            {m.ativa ? m.descricao : `${m.descricao} (inativa)`}
          </option>
        ))}
      </select>
      {midias === null && <p className="text-sm text-[var(--color-muted-foreground)]">Sem acesso à lista de mídias.</p>}
      {estado.tipo === "medindo" && <p role="status">Medindo o contraste da imagem…</p>}
      {estado.tipo === "erro" && (
        <p role="alert" className="text-sm text-[var(--color-destructive)]">
          {estado.mensagem}
        </p>
      )}
    </div>
  );
}
