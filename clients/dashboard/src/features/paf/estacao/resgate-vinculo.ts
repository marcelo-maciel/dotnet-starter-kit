import { env } from "@/env";
import { ApiRequestError, type ApiError } from "@/lib/api-client";
import {
  ehPapelEstacao,
  gravarCredencial,
  type PapelEstacao,
} from "@/features/paf/estacao/credencial-estacao";

type CredencialEstacaoDto = {
  papel: string;
  segredo: string;
  accessToken: string;
  accessTokenExpiresAt: string;
};

const TEMPO_LIMITE_MS = 15_000;

async function resgatar(tenant: string, token: string): Promise<PapelEstacao[]> {
  const resposta = await fetch(`${env.apiBase}/api/v1/paf/estacoes/vinculo/resgatar`, {
    method: "POST",
    headers: { "Content-Type": "application/json", tenant },
    body: JSON.stringify({ token }),
    signal: AbortSignal.timeout(TEMPO_LIMITE_MS),
  });
  if (!resposta.ok) {
    const problema = (resposta.headers.get("content-type") ?? "").includes("json")
      ? ((await resposta.json()) as ApiError)
      : undefined;
    throw new ApiRequestError(resposta.status, problema?.title ?? resposta.statusText, problema);
  }
  const { credenciais } = (await resposta.json()) as { credenciais: CredencialEstacaoDto[] };
  const papeis: PapelEstacao[] = [];
  for (const c of credenciais) {
    if (!ehPapelEstacao(c.papel)) continue;
    gravarCredencial({
      papel: c.papel,
      tenant,
      segredo: c.segredo,
      accessToken: c.accessToken,
      accessTokenExpiresAt: c.accessTokenExpiresAt,
    });
    papeis.push(c.papel);
  }
  return papeis;
}

// The link is single-use: React StrictMode runs the mount effect twice in dev, and a second POST
// with the same token would fail and overwrite the success on screen. One promise per token.
const resgates = new Map<string, Promise<PapelEstacao[]>>();

/** Redeems the one-time link and stores one credential per role; resolves with the roles received. */
export function resgatarVinculoEstacao(tenant: string, token: string): Promise<PapelEstacao[]> {
  const existente = resgates.get(token);
  if (existente) return existente;
  const promessa = resgatar(tenant, token);
  resgates.set(token, promessa);
  return promessa;
}
