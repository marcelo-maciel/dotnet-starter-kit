import { ApiRequestError } from "@/lib/api-client";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import { CredencialEstacaoInvalidaError, erroDaResposta, estacaoResposta } from "@/features/paf/estacao/estacao-http";
import { esperaDoRetryAfter, type RespostaEmissao } from "@/features/paf/totem/ciclo-emissao";

// `code` of the kit's in-flight 409 (IdempotencyEndpointFilter), culture-independent. Any other 409 (lost
// coverage, closed window) is a final answer, so the status alone never decides a resend.
const CODIGO_EM_CURSO = "Idempotency.RequestInProgress";

function transitorio(status: number): boolean {
  return status === 408 || status === 429 || status >= 500;
}

/** One POST of the emission cycle, classified for `EmissaoTotem` (ciclo-emissao.ts). */
export async function emitirFichaNoCiclo(
  filaId: string,
  prioridadeId: string,
  chaveIdempotencia: string,
): Promise<RespostaEmissao<FichaEmitidaDto>> {
  let resposta: Response;
  try {
    resposta = await estacaoResposta("Emissao", "/api/v1/paf/estacao/emissao/fichas", {
      method: "POST",
      headers: { "Idempotency-Key": chaveIdempotencia },
      body: JSON.stringify({ filaId, prioridadeId }),
    });
  } catch (erro) {
    if (erro instanceof CredencialEstacaoInvalidaError) return { tipo: "definitiva", erro };
    // A failed renewal answered by the server keeps its status; fetch failures and timeouts may have reached it.
    if (erro instanceof ApiRequestError && !transitorio(erro.status)) return { tipo: "definitiva", erro };
    return { tipo: "transitoria", erro };
  }

  if (resposta.ok) {
    try {
      const ficha = (await resposta.json()) as FichaEmitidaDto;
      // Hidden cross-origin (CORS exposes only ETag), then false: it informs, it never decides the printing.
      return { tipo: "sucesso", ficha, repetida: resposta.headers.get("Idempotency-Replayed") === "true" };
    } catch (erro) {
      // The ficha was stored but the body was cut: the resend with this key gets the replay.
      return { tipo: "transitoria", erro };
    }
  }

  const erro = await erroDaResposta(resposta);
  if (resposta.status === 409 && erro.problem?.code === CODIGO_EM_CURSO) {
    return { tipo: "emCurso", esperaMs: esperaDoRetryAfter(resposta.headers.get("Retry-After")) };
  }
  return transitorio(resposta.status) ? { tipo: "transitoria", erro } : { tipo: "definitiva", erro };
}
