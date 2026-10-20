// One logout at a time per tab. The server logout revokes the session and the server then sends SessaoInvalidada
// to that session's own connections (EndSessaoCommandHandler), so the tab that is logging out receives the notice
// while its token still exists and paf-tempo-real-provider calls logout() again: without this guard that second
// call posts to api/v1/paf/sessao/logout with a revoked token and gets 401. Other tabs and devices run their own
// guard, so the notice still logs them out. Pure on purpose, no imports: `node --test` runs the test as is (D21).

/**
 * Wraps `sair` so a call while a previous one has not settled is dropped. `sair` must never reject
 * (logoutPeloServidor settles within its deadline); the guard opens again once it settles.
 */
export function umLogoutPorVez(sair: () => Promise<unknown>): () => void {
  let emCurso = false;
  return () => {
    if (emCurso) return;
    emCurso = true;
    void sair().finally(() => {
      emCurso = false;
    });
  };
}
