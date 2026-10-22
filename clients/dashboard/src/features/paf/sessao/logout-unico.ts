// One server logout at a time per tab: two entry points (menu, inactivity guard) firing together would post twice and the
// second would get 401 on the revoked token. SessaoInvalidada no longer comes through here: the notice leaves locally
// (logoutLocal). Pure on purpose, no imports: `node --test` runs the test as is (D21).

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
