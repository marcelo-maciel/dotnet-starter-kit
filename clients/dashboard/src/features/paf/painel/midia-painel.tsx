// The element of one painel exhibition (M08): image for its duration, video and audio until the end of the file or
// the registered duration, sound paused during a call. Failures go back to `use-midias-do-painel.ts`.
import { useEffect, useRef } from "react";
import type { Execucao } from "@/features/paf/painel/agenda-midia";

// A video or audio that stops advancing this long while it should play (network drop, store hung) counts as failed.
const LIMITE_SEM_PROGRESSO_MS = 15_000;
const VERIFICACAO_PROGRESSO_MS = 5_000;

type PropsMidiaPainel = {
  execucao: Execucao;
  /** A call is being announced: media with sound stops and resumes after it. */
  pausado: boolean;
  aoTerminar: (id: number) => void;
  aoFalhar: (id: number, url: string) => void;
};

/** The element of one exhibition; the caller remounts it (key = `execucao.id`) for each new one. */
export function MidiaPainel({ execucao, pausado, aoTerminar, aoFalhar }: PropsMidiaPainel) {
  if (execucao.tipo === "Imagem") {
    return <ImagemPainel execucao={execucao} aoTerminar={aoTerminar} aoFalhar={aoFalhar} />;
  }
  return <SomPainel execucao={execucao} pausado={pausado} aoTerminar={aoTerminar} aoFalhar={aoFalhar} />;
}

function ImagemPainel({ execucao, aoTerminar, aoFalhar }: Omit<PropsMidiaPainel, "pausado">) {
  // An image has no sound: it keeps its time during a call.
  useEffect(() => {
    const timer = window.setTimeout(() => aoTerminar(execucao.id), execucao.duracaoSegundos * 1000);
    return () => window.clearTimeout(timer);
  }, [execucao, aoTerminar]);
  return (
    <img
      src={execucao.url}
      alt=""
      data-midia-painel={execucao.midiaId}
      data-execucao={execucao.id}
      className="max-h-full max-w-full object-contain"
      onError={() => aoFalhar(execucao.id, execucao.url)}
    />
  );
}

function SomPainel({ execucao, pausado, aoTerminar, aoFalhar }: PropsMidiaPainel) {
  const ref = useRef<HTMLVideoElement & HTMLAudioElement>(null);
  // Read by the progress check, which must not restart its count at every call.
  const pausadoRef = useRef(pausado);

  useEffect(() => {
    pausadoRef.current = pausado;
    const elemento = ref.current;
    if (elemento === null) return;
    if (pausado) {
      elemento.pause();
      return;
    }
    elemento.play().catch((e: unknown) => {
      // Aborted by a pause or a remount is normal; anything else (blocked autoplay, bad file) the error event or
      // the progress check below handles.
      if (e instanceof DOMException && e.name === "AbortError") return;
      console.warn("[paf-painel] reprodução recusada", { execucao: execucao.id, erro: e instanceof Error ? e.name : "desconhecido" });
    });
  }, [pausado, execucao.id]);

  // A stalled stream fires no error for a long time: without progress while it should play, skip it.
  useEffect(() => {
    let ultimaPosicao = -1;
    let paradoDesde = Date.now();
    const timer = window.setInterval(() => {
      const elemento = ref.current;
      if (elemento === null) return;
      // Paused by a call is fine; paused on its own (refused play) while it should play is as stuck as a stall.
      if (pausadoRef.current || elemento.ended || elemento.currentTime !== ultimaPosicao) {
        ultimaPosicao = elemento.currentTime;
        paradoDesde = Date.now();
        return;
      }
      if (Date.now() - paradoDesde >= LIMITE_SEM_PROGRESSO_MS) aoFalhar(execucao.id, execucao.url);
    }, VERIFICACAO_PROGRESSO_MS);
    return () => window.clearInterval(timer);
  }, [execucao, aoFalhar]);

  const comum = {
    ref,
    src: execucao.url,
    preload: "auto",
    playsInline: true,
    "data-midia-painel": execucao.midiaId,
    "data-execucao": execucao.id,
    onLoadedMetadata: () => {
      if (ref.current !== null && execucao.inicioNaMidiaSegundos > 0) ref.current.currentTime = execucao.inicioNaMidiaSegundos;
    },
    // Until the end of the file or the registered duration, whichever comes first.
    onTimeUpdate: () => {
      if (ref.current !== null && ref.current.currentTime >= execucao.duracaoSegundos) aoTerminar(execucao.id);
    },
    onEnded: () => aoTerminar(execucao.id),
    onError: () => {
      // The MediaError code tells a refused URL or missing file (2, network) from a file the browser cannot decode (3, 4).
      console.warn("[paf-painel] erro de mídia", { execucao: execucao.id, codigo: ref.current?.error?.code ?? null });
      aoFalhar(execucao.id, execucao.url);
    },
  } as const;

  // ponytail: RF03 stores no caption file, so the track is empty and the painel shows no captions; the calls the
  // public must read are on screen as text. Upgrade: a caption file per mídia in the cadastro, served as this track.
  if (execucao.tipo === "Audio") {
    return (
      <audio {...comum}>
        <track kind="captions" />
      </audio>
    );
  }
  return (
    <video {...comum} className="max-h-full max-w-full object-contain">
      <track kind="captions" />
    </video>
  );
}
