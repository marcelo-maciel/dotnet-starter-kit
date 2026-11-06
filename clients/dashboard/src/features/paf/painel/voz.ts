// Call voice (RF21, decided 24/08/2026): local pt-BR voice first, then network pt-BR, then any
// Portuguese; the panel warns at start when none exists. Order and text follow spikes/voz.

function ehPtBr(voz: SpeechSynthesisVoice): boolean {
  return voz.lang.replace("_", "-").toLowerCase() === "pt-br";
}

export function escolherVoz(vozes: SpeechSynthesisVoice[]): SpeechSynthesisVoice | null {
  return (
    vozes.find((v) => ehPtBr(v) && v.localService) ??
    vozes.find((v) => ehPtBr(v)) ??
    vozes.find((v) => v.lang.toLowerCase().startsWith("pt")) ??
    null
  );
}

/** Voices load asynchronously in Chrome: waits for `voiceschanged` up to `limiteMs`. */
export function carregarVozes(limiteMs: number): Promise<SpeechSynthesisVoice[]> {
  const imediatas = window.speechSynthesis.getVoices();
  if (imediatas.length > 0) return Promise.resolve(imediatas);
  return new Promise((resolve) => {
    const terminar = () => {
      window.clearTimeout(timer);
      window.speechSynthesis.removeEventListener("voiceschanged", terminar);
      resolve(window.speechSynthesis.getVoices());
    };
    const timer = window.setTimeout(terminar, limiteMs);
    window.speechSynthesis.addEventListener("voiceschanged", terminar);
  });
}

/** "A12" → "senha A 12": only the senha is spoken, never the guichê (requisitos seção 2, RF21). */
export function textoFalado(senha: string): string {
  const partes = senha.trim().toUpperCase().match(/^([A-Z]+)(\d+)$/);
  if (!partes) return `senha ${senha}`;
  return `senha ${partes[1]} ${Number.parseInt(partes[2], 10)}`;
}

/**
 * Speaks and resolves when speech ends, fails, or `limiteMs` passes: a voice that never starts or never
 * fires `end` (the voice spike guards the same case) must not freeze the announcement queue.
 */
export function falar(texto: string, voz: SpeechSynthesisVoice | null, limiteMs: number): Promise<void> {
  return new Promise((resolve) => {
    const fala = new SpeechSynthesisUtterance(texto);
    fala.lang = "pt-BR";
    if (voz) fala.voice = voz;
    const terminar = () => {
      window.clearTimeout(timer);
      resolve();
    };
    const timer = window.setTimeout(() => {
      window.speechSynthesis.cancel();
      terminar();
    }, limiteMs);
    fala.onend = terminar;
    fala.onerror = (evento) => {
      console.warn("[paf-painel] erro na fala", evento.error);
      terminar();
    };
    window.speechSynthesis.speak(fala);
  });
}
