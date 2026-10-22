import { decodeJwt, isTokenExpired, type JwtClaims } from "../../../auth/jwt.ts";

// Server logout (S13, arquitetura seção 9, ADR 0008 "Logout pelo servidor"): before the kit clears the token,
// POST api/v1/paf/sessao/logout revokes the kit session and releases licence and guichê in the same act.
// Without it the session stays valid and nobody writes the release. Pure module (no store, no env) so
// node --test covers it (D21); logout-pelo-servidor.ts wires it to the kit.

/** Short on purpose: the local logout waits for it, and a dead server must not hold the user on screen. */
export const PRAZO_LOGOUT_SERVIDOR_MS = 3_000;

// The kit's JwtClaims has no `sid`; the claim comes from the Identity token handlers edited by ADR 0008.
type ClaimsComSessao = JwtClaims & { sid?: string };

export type PedidoLogoutServidor = {
  apiBase: string;
  tenant: string;
  accessToken: string | null;
  refreshToken: string | null;
  prazoMs: number;
  buscar: typeof fetch;
};

export type ResultadoLogoutServidor = "encerrada" | "falhou";

/**
 * Starts the server logout, or returns null when there is nothing for the server to end: no token, an
 * impersonation token (its grant ends through the kit's impersonation flow, and the PAF barrier refuses
 * act_sub tokens of other tenants anyway) or a token without the session claim. The promise never
 * rejects and settles within `prazoMs`.
 */
export function iniciarLogoutServidor(pedido: PedidoLogoutServidor): Promise<ResultadoLogoutServidor> | null {
  const claims = decodeJwt(pedido.accessToken) as ClaimsComSessao | null;
  if (pedido.accessToken === null || claims === null) return null;
  if (claims.act_sub != null) return null;
  if (typeof claims.sid !== "string" || claims.sid.length === 0) return null;
  return encerrar(pedido, pedido.accessToken, isTokenExpired(claims));
}

async function encerrar(
  pedido: PedidoLogoutServidor,
  accessToken: string,
  tokenVencido: boolean,
): Promise<ResultadoLogoutServidor> {
  // One controller bounds both requests and the body read: nothing outlives the deadline, so no late
  // response can act after the local logout.
  const controle = new AbortController();
  const timer = setTimeout(() => controle.abort(new DOMException("Prazo do logout esgotado", "TimeoutError")), pedido.prazoMs);
  try {
    const token = tokenVencido ? await renovarEmMemoria(pedido, accessToken, controle.signal) : accessToken;
    if (token === null) return "falhou";

    const resposta = await pedido.buscar(`${pedido.apiBase}/api/v1/paf/sessao/logout`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, tenant: pedido.tenant },
      signal: controle.signal,
      // Lets the request finish if the tab closes right after the click.
      keepalive: true,
    });
    if (!resposta.ok) {
      console.warn(`[paf] logout pelo servidor recusado (${resposta.status}); segue o logout local`);
      return "falhou";
    }
    return "encerrada";
  } catch (erro) {
    console.warn("[paf] logout pelo servidor não concluído; segue o logout local", nomeDoErro(erro));
    return "falhou";
  } finally {
    clearTimeout(timer);
  }
}

// The access token outlives no idle shift (45 min against the hours of the inactivity guard, D04), so the
// inactivity logout usually starts with it expired. The kit's refreshAccessToken would write the token
// store and ignores this deadline: a late success would sign the user back in after the clear. This copy
// of its request keeps the new token in memory only.
async function renovarEmMemoria(
  pedido: PedidoLogoutServidor,
  accessToken: string,
  signal: AbortSignal,
): Promise<string | null> {
  if (pedido.refreshToken === null) {
    console.warn("[paf] logout pelo servidor sem refresh token para o token vencido; segue o logout local");
    return null;
  }
  const resposta = await pedido.buscar(`${pedido.apiBase}/api/v1/identity/token/refresh`, {
    method: "POST",
    headers: { "Content-Type": "application/json", tenant: pedido.tenant },
    body: JSON.stringify({ token: accessToken, refreshToken: pedido.refreshToken }),
    signal,
  });
  if (!resposta.ok) {
    console.warn(`[paf] renovação para o logout pelo servidor recusada (${resposta.status}); segue o logout local`);
    return null;
  }
  const corpo = (await resposta.json()) as { token?: unknown };
  return typeof corpo.token === "string" && corpo.token.length > 0 ? corpo.token : null;
}

function nomeDoErro(erro: unknown): string {
  return erro instanceof Error ? erro.name : typeof erro;
}
