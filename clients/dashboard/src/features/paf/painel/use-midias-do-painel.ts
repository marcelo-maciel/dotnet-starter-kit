// What the painel's media layer plays (M08, RF21 amended, D12, D13): reads the agenda (M06), follows the rules of
// `agenda-midia.ts` on the server's clock and moves from item to item. Absence of media is normal operation: no item,
// no message. A failing item is skipped silently (the public never sees an error) and its URL re-read.
import { useCallback, useEffect, useRef, useState } from "react";
import { obterMidiasPainel } from "@/features/paf/estacao/estacao-api";
import { CredencialEstacaoInvalidaError } from "@/features/paf/estacao/estacao-http";
import {
  aposTermino,
  diaVirou,
  montarAgenda,
  proximaParaPrecarga,
  proximaReleituraLocalMs,
  proximaTrocaLocalMs,
  reavaliar,
  type AgendaPainel,
  type Execucao,
} from "@/features/paf/painel/agenda-midia";

const NOVA_LEITURA_APOS_ERRO_MS = 30_000;
// An item that fails asks for fresh URLs, but a whole list failing at once (network down) must not flood the API.
const INTERVALO_MINIMO_RELEITURA_POR_FALHA_MS = 10_000;
// With nothing playable but items in the agenda (all in quarantine), look again after this long.
const NOVA_TENTATIVA_SEM_ITEM_MS = 15_000;

export type MidiasDoPainel = {
  execucao: Execucao | null;
  /** Re-read the agenda: on `ProgramacaoAlterada` and on start. */
  reler: () => void;
  /** After a reconnection: failures may have been the network, so every URL gets a new chance. */
  relerAoReconectar: () => void;
  terminar: (id: number) => void;
  falhar: (id: number, url: string) => void;
};

/** `habilitado` = the start gesture happened: before it nothing is chosen, so nothing plays with sound. */
export function useMidiasDoPainel(habilitado: boolean, aoPerderCredencial: () => void): MidiasDoPainel {
  const [agenda, setAgenda] = useState<AgendaPainel | null>(null);
  const [execucao, setExecucao] = useState<Execucao | null>(null);
  const [relogio, setRelogio] = useState(0);
  const agendaRef = useRef<AgendaPainel | null>(null);
  const falhasRef = useRef(new Map<string, number>());
  const leituraRef = useRef(0);
  const novaLeituraRef = useRef<number | undefined>(undefined);
  const ultimaReleituraPorFalhaRef = useRef(0);
  // The retry timer calls the latest `reler` without making it depend on itself.
  const releituraRef = useRef<() => void>(() => undefined);

  const reler = useCallback(() => {
    const leitura = ++leituraRef.current;
    window.clearTimeout(novaLeituraRef.current);
    const enviadoEm = Date.now();
    obterMidiasPainel().then(
      (dto) => {
        // Two re-reads in flight (notice + reconnection): only the last one asked counts.
        if (leitura !== leituraRef.current) return;
        try {
          const nova = montarAgenda(dto, enviadoEm, Date.now());
          agendaRef.current = nova;
          setAgenda(nova);
        } catch (e) {
          console.warn("[paf-painel] resposta de mídias ilegível", e);
          novaLeituraRef.current = window.setTimeout(() => releituraRef.current(), NOVA_LEITURA_APOS_ERRO_MS);
        }
      },
      (e: unknown) => {
        if (leitura !== leituraRef.current) return;
        if (e instanceof CredencialEstacaoInvalidaError) {
          aoPerderCredencial();
          return;
        }
        // The last agenda keeps playing; its URLs are valid well past this retry.
        console.warn("[paf-painel] leitura das mídias falhou; nova tentativa em 30 s", e);
        novaLeituraRef.current = window.setTimeout(() => releituraRef.current(), NOVA_LEITURA_APOS_ERRO_MS);
      },
    );
  }, [aoPerderCredencial]);
  useEffect(() => {
    releituraRef.current = reler;
  }, [reler]);

  useEffect(() => () => window.clearTimeout(novaLeituraRef.current), []);

  const relerAoReconectar = useCallback(() => {
    falhasRef.current.clear();
    reler();
  }, [reler]);

  useEffect(() => {
    setExecucao((atual) => (habilitado ? reavaliar(agenda, atual, Date.now(), falhasRef.current) : null));
  }, [agenda, habilitado, relogio]);

  // Window start or end, or the São Paulo midnight (then the day's programações are stale: re-read).
  useEffect(() => {
    if (agenda === null) return;
    const timer = window.setTimeout(() => {
      if (diaVirou(agenda, Date.now())) reler();
      setRelogio((r) => r + 1);
    }, proximaTrocaLocalMs(agenda, Date.now()) - Date.now());
    return () => window.clearTimeout(timer);
  }, [agenda, relogio, reler]);

  useEffect(() => {
    if (agenda === null) return;
    const em = proximaReleituraLocalMs(agenda, Date.now());
    if (em === null) return;
    const timer = window.setTimeout(reler, em - Date.now());
    return () => window.clearTimeout(timer);
  }, [agenda, reler]);

  const temItens = agenda !== null && (agenda.lista.length > 0 || agenda.programacoes.length > 0);
  useEffect(() => {
    if (!habilitado || execucao !== null || !temItens) return;
    const timer = window.setInterval(() => setRelogio((r) => r + 1), NOVA_TENTATIVA_SEM_ITEM_MS);
    return () => window.clearInterval(timer);
  }, [habilitado, execucao, temItens]);

  // Only the next loop item is fetched ahead, by a detached element: nothing enters the DOM before it plays.
  useEffect(() => {
    const proxima = proximaParaPrecarga(agenda, execucao, Date.now(), falhasRef.current);
    if (proxima === null) return;
    if (proxima.tipo === "Imagem") {
      const imagem = new Image();
      imagem.src = proxima.url;
      return () => {
        imagem.src = "";
      };
    }
    const elemento = document.createElement(proxima.tipo === "Audio" ? "audio" : "video");
    elemento.preload = "auto";
    elemento.muted = true;
    elemento.src = proxima.url;
    return () => {
      elemento.removeAttribute("src");
      elemento.load();
    };
  }, [agenda, execucao]);

  const terminar = useCallback((id: number) => {
    setExecucao((atual) =>
      atual === null || atual.id !== id ? atual : aposTermino(agendaRef.current, atual, Date.now(), falhasRef.current),
    );
  }, []);

  const falhar = useCallback(
    (id: number, url: string) => {
      // Ids only in the log: the URL is a signed credential to the file.
      console.warn("[paf-painel] mídia não tocou; pulando", { execucao: id });
      falhasRef.current.set(url, Date.now());
      if (Date.now() - ultimaReleituraPorFalhaRef.current >= INTERVALO_MINIMO_RELEITURA_POR_FALHA_MS) {
        ultimaReleituraPorFalhaRef.current = Date.now();
        reler();
      }
      terminar(id);
    },
    [reler, terminar],
  );

  return { execucao, reler, relerAoReconectar, terminar, falhar };
}

