import { apiFetch } from "@/lib/api-client";

export type TokenResponse = {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  refreshTokenExpiresAt: string;
};

/** The login's tokens plus the company the server found for the e-mail: later requests send it as `tenant`. */
export type EmailLoginResponse = TokenResponse & { tenant: string };

/**
 * PAF (O2): the person types e-mail and password only. The server finds the one client company that holds
 * the e-mail and answers which it is. No `tenant` header goes out: a stale one from an earlier session
 * (a company since deactivated) would be refused before the login even ran.
 */
export function issueTokenByEmail(input: { email: string; password: string }) {
  return apiFetch<EmailLoginResponse>("/api/v1/identity/token/issue-by-email", {
    method: "POST",
    body: JSON.stringify({ email: input.email, password: input.password }),
    skipAuth: true,
    skipTenant: true,
  });
}
