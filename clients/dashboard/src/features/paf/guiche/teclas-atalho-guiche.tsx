import { useEffect, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api-client";
import { acaoDaTecla, acaoPermitida, type AcaoAtalho, type TeclasGuicheDto } from "@/features/paf/guiche/teclas-atalho";

// The forwarding dialog belongs to its own component (F09); the hotkey presses its button, so the dialog opens
// exactly as a click would, and does nothing while that button is absent or disabled.
const SELETOR_ENCAMINHAR = '[data-acao-guiche="encaminhar"]';

type Props = {
  estado: string | null;
  ocupado: boolean;
  executar: Record<Exclude<AcaoAtalho, "encaminhar">, () => void>;
};

/** Window-wide keydown for the RF06 guichê hotkeys of the current unidade; renders nothing but the key list. */
export function TeclasAtalhoGuiche({ estado, ocupado, executar }: Props) {
  const teclas = useQuery({
    queryKey: ["paf", "guiche", "teclas-atalho"],
    queryFn: () => apiFetch<TeclasGuicheDto>("/api/v1/paf/guiche/teclas-atalho"),
  });

  // The listener is installed once; it reads the latest state through the ref instead of re-subscribing per render.
  const atual = useRef({ estado, ocupado, executar, teclas: teclas.data });
  useEffect(() => {
    atual.current = { estado, ocupado, executar, teclas: teclas.data };
  });

  useEffect(() => {
    const aoTeclar = (evento: KeyboardEvent) => {
      const { teclas: mapa, estado: e, ocupado: o, executar: ex } = atual.current;
      if (!mapa || evento.defaultPrevented) return;
      const acao = acaoDaTecla(mapa, evento);
      if (acao === null) return;
      // A configured key never reaches the browser (F5 would reload the guichê in the middle of an atendimento).
      evento.preventDefault();
      if (!acaoPermitida(acao, e, o)) return;
      if (acao === "encaminhar") {
        const botao = document.querySelector<HTMLButtonElement>(SELETOR_ENCAMINHAR);
        if (botao && !botao.disabled) botao.click();
        return;
      }
      ex[acao]();
    };
    window.addEventListener("keydown", aoTeclar);
    return () => window.removeEventListener("keydown", aoTeclar);
  }, []);

  if (!teclas.data) return null;
  const t = teclas.data;
  return (
    <p className="text-center text-xs text-[var(--color-muted-foreground)]" data-teclas-atalho>
      Atalhos: {t.chamarProxima} chamar próxima · {t.chamarNovamente} chamar novamente · {t.iniciar} iniciar ·{" "}
      {t.encerrar} encerrar · {t.encerrarEChamar} encerrar e chamar · {t.encaminhar} encaminhar
    </p>
  );
}
