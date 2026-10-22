// RF11 emission cycle of the totem (requisitos l.403-406). No imports on purpose: `node --test` runs this
// file as is (D21); the HTTP call, the clock and the printing are injected by totem-page.tsx.

/** Used when `prazoCicloEmissaoSegundos` is absent from the station block (C04 validates the key). */
// Above the kit's idempotency reservation (`IdempotencyOptions.ReservationTtl`, 1 min): an instance that dies
// after the commit leaves the key reserved, every resend 409s until it expires, and a shorter cycle would send
// the person away without paper while the ficha waits in the queue (requisitos l.406).
export const PRAZO_CICLO_PADRAO_S = 75;

// Pause between resends after a network failure, so a dead link is not hammered for the whole cycle.
export const PAUSA_REENVIO_MS = 1_000;

// The kit always answers the in-flight 409 with `Retry-After: 1`; cross-origin the browser may hide the header
// (CORS exposes only ETag), so a missing or unreadable value waits the same second.
const ESPERA_EM_CURSO_PADRAO_MS = 1_000;

/** How one POST of the cycle ended, classified by the caller's adapter. */
export type RespostaEmissao<F> =
  | { tipo: "sucesso"; ficha: F; repetida: boolean }
  // 409 `Idempotency.RequestInProgress`: the first request with this key is still running on the server.
  | { tipo: "emCurso"; esperaMs: number }
  // Network error, timeout, 408, 429 or 5xx: the request may have reached the server, so resend the same key.
  | { tipo: "transitoria"; erro: unknown }
  // The server answered for good (closed window, lost coverage, credential): resending cannot change it.
  | { tipo: "definitiva"; erro: unknown };

export type FimCiclo<F> =
  | { tipo: "entregue"; ficha: F; repetida: boolean }
  | { tipo: "recusada"; erro: unknown }
  // `erro` is the last transient failure, null when only in-flight 409s were seen.
  | { tipo: "prazoVencido"; erro: unknown }
  // A tap while a cycle runs: no request, no key, nothing printed.
  | { tipo: "ocupado" }
  // The screen went away mid-cycle.
  | { tipo: "abandonado" };

export type PassosEmissao<E, F> = {
  enviar: (escolha: E, chave: string) => Promise<RespostaEmissao<F>>;
  /** Prints the ticket and shows it; resolves when the screen is back to the initial state. */
  entregar: (ficha: F) => Promise<void>;
  esperar: (ms: number) => Promise<void>;
  agora: () => number;
  novaChave: () => string;
};

/**
 * `Retry-After` in delta-seconds, never below one second (a 0 would turn the wait into a busy loop against the
 * server); the HTTP-date form, absence or garbage fall back to one second.
 */
export function esperaDoRetryAfter(valor: string | null): number {
  if (valor === null || !/^\s*\d+\s*$/.test(valor)) return ESPERA_EM_CURSO_PADRAO_MS;
  return Math.max(Number.parseInt(valor, 10) * 1_000, ESPERA_EM_CURSO_PADRAO_MS);
}

/**
 * One totem screen. A cycle starts on a tap, owns one idempotency key for every resend, and ends when the
 * screen is back to the initial state: the next tap, on any button, opens a new cycle with a new key, because
 * the kit does not compare bodies and another choice with this key would replay this ficha (requisitos l.403).
 */
export class EmissaoTotem<E, F> {
  private readonly passos: PassosEmissao<E, F>;
  private emCurso = false;
  private encerrada = false;

  constructor(passos: PassosEmissao<E, F>) {
    this.passos = passos;
  }

  ocupada(): boolean {
    return this.emCurso;
  }

  /** The screen unmounted: no further request or print once the current step returns. */
  encerrar(): void {
    this.encerrada = true;
  }

  async tocar(escolha: E, prazoMs: number): Promise<FimCiclo<F>> {
    // Buttons are disabled during the cycle; this also covers a double tap that lands before the re-render.
    if (this.emCurso) return { tipo: "ocupado" };
    this.emCurso = true;
    try {
      return await this.executar(escolha, this.passos.novaChave(), this.passos.agora() + prazoMs);
    } finally {
      this.emCurso = false;
    }
  }

  private async executar(escolha: E, chave: string, fimEm: number): Promise<FimCiclo<F>> {
    let ultimoErro: unknown = null;
    for (;;) {
      if (this.encerrada) return { tipo: "abandonado" };
      const resposta = await this.passos.enviar(escolha, chave);
      if (this.encerrada) return { tipo: "abandonado" };
      // A success is printed even past the deadline: the ficha exists, and dropping it would leave a number
      // nobody holds. The loop ends here, so a cycle prints at most once, original or replayed.
      if (resposta.tipo === "sucesso") {
        await this.passos.entregar(resposta.ficha);
        return { tipo: "entregue", ficha: resposta.ficha, repetida: resposta.repetida };
      }
      if (resposta.tipo === "definitiva") return { tipo: "recusada", erro: resposta.erro };
      if (resposta.tipo === "transitoria") ultimoErro = resposta.erro;

      const restante = fimEm - this.passos.agora();
      if (restante <= 0) return { tipo: "prazoVencido", erro: ultimoErro };
      const espera = resposta.tipo === "emCurso" ? resposta.esperaMs : PAUSA_REENVIO_MS;
      // The cycle deadline is also the ceiling of the in-flight wait (requisitos l.406).
      await this.passos.esperar(Math.min(espera, restante));
      if (this.passos.agora() >= fimEm) return { tipo: "prazoVencido", erro: ultimoErro };
    }
  }
}
