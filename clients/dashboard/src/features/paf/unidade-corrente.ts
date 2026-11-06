// The session's current unit changed on the server without the screen asking: a session revived by the licence
// cascade opened in another unit (RF22). Every window drops what it shows and re-reads its context from the server.
// Name = UnidadeCorrenteAlterada.Metodo (Modules.PAF.Contracts/v1/TempoReal/UnidadeCorrenteAlterada.cs).

export const UNIDADE_CORRENTE_ALTERADA = "UnidadeCorrenteAlterada";

type Handlers = Record<string, (payload: unknown) => void>;

/** The screen's own handlers plus the unit-change one, which only asks for a reload of the context. */
export function comAvisoDeUnidadeCorrente(handlers: Handlers, recarregarContexto: () => void): Handlers {
  return { ...handlers, [UNIDADE_CORRENTE_ALTERADA]: () => recarregarContexto() };
}

/**
 * The window's own connection dies with an expired mark, so the notice above usually lands after the reconnection
 * (outbox copy); a reconnection may also have missed it. Every reconnection re-reads the context; the first
 * connection does not, because the screen has just read it.
 */
export function recarregarAoReconectar(recarregarContexto: () => void): () => void {
  let conectouAntes = false;
  return () => {
    if (conectouAntes) recarregarContexto();
    conectouAntes = true;
  };
}
