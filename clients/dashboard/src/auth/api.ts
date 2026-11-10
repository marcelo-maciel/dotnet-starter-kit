import { ApiRequestError, apiFetch } from "@/lib/api-client";

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
export function issueTokenByEmail(input: {
  email: string;
  password: string;
  /** Authenticator (TOTP) code — sent only on the second step of a 2FA sign-in. */
  twoFactorCode?: string;
}) {
  return apiFetch<EmailLoginResponse>("/api/v1/identity/token/issue-by-email", {
    method: "POST",
    body: JSON.stringify({
      email: input.email,
      password: input.password,
      ...(input.twoFactorCode ? { twoFactorCode: input.twoFactorCode } : {}),
    }),
    skipAuth: true,
    skipTenant: true,
  });
}

// Stable ProblemDetails `code`s the token endpoint sets for the second factor
// (the MessageKey IdentityService throws with). The detail text is localized
// under the request culture, so branch on the code, never on the prose.
const TWO_FACTOR_REQUIRED = "Identity.TwoFactorRequired";
const TWO_FACTOR_INVALID = "Identity.TwoFactorInvalid";

function problemCode(error: unknown): string | undefined {
  if (!(error instanceof ApiRequestError) || error.status !== 401) return undefined;
  const code = error.problem?.code;
  return typeof code === "string" ? code : undefined;
}

/** The password was right, but the account has 2FA on and no code was sent. */
export function isTwoFactorRequiredError(error: unknown): boolean {
  return problemCode(error) === TWO_FACTOR_REQUIRED;
}

/** The authenticator code was wrong or expired. */
export function isTwoFactorInvalidError(error: unknown): boolean {
  return problemCode(error) === TWO_FACTOR_INVALID;
}
