// The station link (RF04): built by the cadastro screen and read by the redemption page, one module for both so the
// two ends cannot drift. Pure (node --test, D21). The token travels in the fragment so it never reaches a server or
// proxy access log; the tenant travels beside it, readable, because redemption resolves the client before any login.

export const ROTA_RESGATE_VINCULO = "/paf/estacao/vinculo";

export type LinkVinculo = { tenant: string; token: string };

/** Full address the device opens once; `origem` is the dashboard origin (scheme, host and port). */
export function montarLinkVinculo(origem: string, tenant: string, token: string): string {
  // URLSearchParams reads `+` as a space and `&` as a separator, so both values go percent-encoded.
  return `${origem}${ROTA_RESGATE_VINCULO}#tenant=${encodeURIComponent(tenant)}&token=${encodeURIComponent(token)}`;
}

/** Tenant and token of a `#tenant=…&token=…` fragment; null when either is missing or blank. */
export function lerLinkVinculo(fragmento: string): LinkVinculo | null {
  const params = new URLSearchParams(fragmento.replace(/^#/, ""));
  const tenant = params.get("tenant")?.trim() ?? "";
  const token = params.get("token")?.trim() ?? "";
  return tenant && token ? { tenant, token } : null;
}
