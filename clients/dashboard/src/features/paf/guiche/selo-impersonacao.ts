// S11 (RF22, RNF15): the guichê operated by the Codefi's impersonation says so on the page itself, next to the post,
// because every call, start, close and cancel there is recorded for the impersonated user (statistics, RF07) while the
// server keeps the operator in AtoImpersonacao. The kit's banner above the shell stays; this one names the guichê's case.

export type ImpersonacaoDaTela = { actorName?: string } | null;

/** Badge text, or null outside impersonation (the screen shows nothing then). */
export function textoSeloImpersonacao(impersonacao: ImpersonacaoDaTela, nomeUsuario: string | undefined): string | null {
  if (impersonacao === null) return null;
  const operador = impersonacao.actorName?.trim() || "Suporte da Codefi";
  const usuario = nomeUsuario?.trim() || "o usuário";
  return `${operador} opera este guichê como ${usuario}: os atendimentos ficam em nome dele, e cada operação é registrada com o operador.`;
}
