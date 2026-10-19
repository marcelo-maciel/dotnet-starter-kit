// Pure rules of the session-wide PafHub connection and of the unit-switch notice between tabs. No imports on
// purpose: `node --test` runs the test as is (D21).

/** The token claims this file reads; the kit's JwtClaims plus the PAF session id (`sid`, ADR 0008). */
export type ClaimsConexao = {
  sub?: string;
  tenant?: string;
  sid?: string;
  act_sub?: string;
  act_tenant?: string;
};

const TENANT_RAIZ = "root";

/**
 * Identity of the connection the provider keeps open, or null when it must not connect. A token refresh keeps the
 * key (same session), so the connection survives it; another subject or session rebuilds it. Only the Codefi's
 * impersonation (act_tenant = root) operates the PAF (RF22): any other impersonation gets 403 on the hub, and
 * without negotiation that 403 is an endless WebSocket retry.
 */
export function chaveConexaoPafHub(claims: ClaimsConexao | null): string | null {
  if (!claims?.sub || !claims.tenant) return null;
  if (claims.act_sub && claims.act_tenant !== TENANT_RAIZ) return null;
  return [claims.tenant, claims.sub, claims.sid ?? "", claims.act_sub ?? ""].join("|");
}

/** Broadcast to the other tabs of this browser after a unit switch (RF08: the unit is the session's). */
export type AvisoTrocaUnidade = {
  tenant: string;
  usuarioId: string;
  abaId: string;
  unidadeId: string;
  unidade: string;
};

/** Anything else on the channel (another version of the app, a forged post) is dropped. */
export function lerAvisoTrocaUnidade(dado: unknown): AvisoTrocaUnidade | null {
  if (typeof dado !== "object" || dado === null) return null;
  const d = dado as Record<string, unknown>;
  const campos = ["tenant", "usuarioId", "abaId", "unidadeId", "unidade"] as const;
  for (const campo of campos) {
    if (typeof d[campo] !== "string" || d[campo] === "") return null;
  }
  return {
    tenant: d.tenant as string,
    usuarioId: d.usuarioId as string,
    abaId: d.abaId as string,
    unidadeId: d.unidadeId as string,
    unidade: d.unidade as string,
  };
}

export type AbaAtual = { tenant: string; usuarioId: string; abaId: string };

/** Only a switch made by the same user of the same tenant in another tab concerns this one. */
export function avisoDeOutraAba(aviso: AvisoTrocaUnidade, aba: AbaAtual): boolean {
  return aviso.tenant === aba.tenant && aviso.usuarioId === aba.usuarioId && aviso.abaId !== aba.abaId;
}
