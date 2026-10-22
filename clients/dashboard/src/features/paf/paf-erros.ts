import { ApiRequestError } from "@/lib/api-client";

// The PAF handlers already write PT-BR prose in ProblemDetails.detail (MensagensGuiche,
// ChamarProximaFichaCommandHandler.Mensagem*, EmitirFichaCommandHandler.Mensagem*), so the detail
// wins; the per-status text only covers kit-level failures whose detail is English or absent.
const MENSAGEM_POR_STATUS: Record<number, string> = {
  400: "Pedido inválido. Confira os dados e tente de novo.",
  401: "Sessão expirada. Entre de novo.",
  403: "Você não tem permissão para esta operação.",
  404: "Registro não encontrado.",
  409: "A operação conflita com o estado atual. Atualize e tente de novo.",
  429: "Muitas tentativas em pouco tempo. Aguarde um minuto.",
  503: "Serviço ocupado. Tente de novo.",
};

const MENSAGEM_REDE = "Sem conexão com o servidor. Confira a rede e tente de novo.";

function temAcento(texto: string): boolean {
  return /[à-úÀ-Ú]/.test(texto);
}

/** Text for the operator: the PAF message when the server sent one in Portuguese, else a per-status line. */
export function mensagemAmigavel(erro: unknown): string {
  if (erro instanceof ApiRequestError) {
    const detail = erro.problem?.detail;
    if (typeof detail === "string" && temAcento(detail)) return detail;
    return MENSAGEM_POR_STATUS[erro.status] ?? `Erro inesperado (${erro.status}). Tente de novo.`;
  }
  return MENSAGEM_REDE;
}
