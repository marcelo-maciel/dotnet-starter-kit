import { env } from "@/env";
import { tokenStore } from "@/auth/token-store";
import {
  iniciarLogoutServidor,
  PRAZO_LOGOUT_SERVIDOR_MS,
  type ResultadoLogoutServidor,
} from "@/features/paf/sessao/logout-servidor";

/** The kit's logout calls this before clearing the token; null = clear right away (see iniciarLogoutServidor). */
export function logoutPeloServidor(): Promise<ResultadoLogoutServidor> | null {
  return iniciarLogoutServidor({
    apiBase: env.apiBase,
    tenant: tokenStore.getTenant() ?? env.defaultTenant,
    accessToken: tokenStore.getAccessToken(),
    refreshToken: tokenStore.getRefreshToken(),
    prazoMs: PRAZO_LOGOUT_SERVIDOR_MS,
    buscar: (url, init) => fetch(url, init),
  });
}
