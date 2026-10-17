import { env } from "@/env";
import { ApiRequestError, type ApiError } from "@/lib/api-client";
import {
  apagarCredencial,
  gravarCredencial,
  lerCredencial,
  type CredencialEstacao,
  type PapelEstacao,
} from "@/features/paf/estacao/credencial-estacao";

// Station HTTP client (arquitetura seção 3): totem and painel never go through lib/api-client.ts,
// which always sends the user's token from the fsh.dashboard.* keys.

const TEMPO_LIMITE_MS = 15_000;
// Renew a bit before expiry so a request or a hub negotiate never leaves with a token about to die.
const MARGEM_RENOVACAO_MS = 60_000;

/** The station link was revoked, the station disabled, or the credential is gone: only a new link fixes it. */
export class CredencialEstacaoInvalidaError extends Error {
  readonly papel: PapelEstacao;

  constructor(papel: PapelEstacao) {
    super(`Credencial de estação (${papel}) ausente ou revogada.`);
    this.papel = papel;
  }
}

type AcessoEstacaoDto = { papel: string; accessToken: string; accessTokenExpiresAt: string };

async function lerProblema(resposta: Response): Promise<ApiError | undefined> {
  if (!(resposta.headers.get("content-type") ?? "").includes("json")) return undefined;
  try {
    return (await resposta.json()) as ApiError;
  } catch {
    // Body announced as JSON but unreadable: the status alone still drives the message.
    return undefined;
  }
}

async function erroDaResposta(resposta: Response): Promise<ApiRequestError> {
  const problema = await lerProblema(resposta);
  return new ApiRequestError(resposta.status, problema?.title ?? resposta.statusText, problema);
}

const renovacoesEmCurso = new Map<PapelEstacao, Promise<CredencialEstacao>>();

async function renovar(atual: CredencialEstacao): Promise<CredencialEstacao> {
  // Anonymous endpoint: the tenant header decides it, and it must be the credential's own tenant.
  const resposta = await fetch(`${env.apiBase}/api/v1/paf/estacoes/credencial/renovar`, {
    method: "POST",
    headers: { "Content-Type": "application/json", tenant: atual.tenant },
    body: JSON.stringify({ segredo: atual.segredo }),
    signal: AbortSignal.timeout(TEMPO_LIMITE_MS),
  });
  if (resposta.status === 400 || resposta.status === 401 || resposta.status === 403 || resposta.status === 404) {
    apagarCredencial(atual.papel);
    throw new CredencialEstacaoInvalidaError(atual.papel);
  }
  if (!resposta.ok) throw await erroDaResposta(resposta);
  const acesso = (await resposta.json()) as AcessoEstacaoDto;
  const nova: CredencialEstacao = {
    ...atual,
    accessToken: acesso.accessToken,
    accessTokenExpiresAt: acesso.accessTokenExpiresAt,
  };
  gravarCredencial(nova);
  return nova;
}

/** One renewal at a time per role: the `auth` rate limit is 10 per minute per IP for the whole store. */
function renovarUmaVez(atual: CredencialEstacao): Promise<CredencialEstacao> {
  const emCurso = renovacoesEmCurso.get(atual.papel);
  if (emCurso) return emCurso;
  const promessa = renovar(atual).finally(() => renovacoesEmCurso.delete(atual.papel));
  renovacoesEmCurso.set(atual.papel, promessa);
  return promessa;
}

function exigirCredencial(papel: PapelEstacao): CredencialEstacao {
  const credencial = lerCredencial(papel);
  if (credencial === null) throw new CredencialEstacaoInvalidaError(papel);
  return credencial;
}

/** Access token valid for at least the renewal margin; renews through the secret when it is not. */
export async function obterTokenValido(papel: PapelEstacao): Promise<string> {
  const credencial = exigirCredencial(papel);
  const expiraEm = Date.parse(credencial.accessTokenExpiresAt);
  if (Number.isFinite(expiraEm) && expiraEm - Date.now() > MARGEM_RENOVACAO_MS) {
    return credencial.accessToken;
  }
  return (await renovarUmaVez(credencial)).accessToken;
}

/** Renews even when the stored token looks valid: the server refused it (drifted clock, rotated key). */
export async function forcarRenovacao(papel: PapelEstacao): Promise<void> {
  await renovarUmaVez(exigirCredencial(papel));
}

export function tenantDaEstacao(papel: PapelEstacao): string {
  return exigirCredencial(papel).tenant;
}

async function enviar(papel: PapelEstacao, caminho: string, init: RequestInit, token: string): Promise<Response> {
  const cabecalhos = new Headers(init.headers);
  cabecalhos.set("Authorization", `Bearer ${token}`);
  // The server refuses (403) a tenant header that differs from the token's tenant, so it is always the stored one.
  cabecalhos.set("tenant", tenantDaEstacao(papel));
  if (typeof init.body === "string" && !cabecalhos.has("Content-Type")) {
    cabecalhos.set("Content-Type", "application/json");
  }
  return fetch(`${env.apiBase}${caminho}`, {
    ...init,
    headers: cabecalhos,
    signal: AbortSignal.timeout(TEMPO_LIMITE_MS),
  });
}

/** Authenticated request with the station credential of `papel`; a 401 renews once and retries. */
export async function estacaoFetch<T>(papel: PapelEstacao, caminho: string, init: RequestInit): Promise<T> {
  let resposta = await enviar(papel, caminho, init, await obterTokenValido(papel));
  if (resposta.status === 401) {
    const renovada = await renovarUmaVez(exigirCredencial(papel));
    resposta = await enviar(papel, caminho, init, renovada.accessToken);
  }
  if (!resposta.ok) throw await erroDaResposta(resposta);
  if (resposta.status === 204) return undefined as T;
  return (await resposta.json()) as T;
}
