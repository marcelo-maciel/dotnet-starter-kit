// Station deactivation dialog (RF04, C05): the text of the preview and of the result. Pure module (node --test, D21);
// the server re-checks everything under its locks, so the preview only informs the choice.

/** Pair of fila and prioridade as the API returns it (CombinacaoFilaPrioridadeDto). */
export type CombinacaoDto = { id: string; filaId: string; fila: string; prioridadeId: string; prioridade: string; letra: string };

/** Atendimento Chamado or EmAtendimento on the station. */
export type AtendimentoEmCursoDto = { atendimentoId: string; fichaId: string; senha: string; estado: string; versao: number };

export type ImpactoDesativacaoDto = {
  id: string;
  guicheOcupado: boolean;
  emCurso: AtendimentoEmCursoDto[];
  combinacoesSemCobertura: CombinacaoDto[];
};

export type EstacaoDesativadaDto = {
  id: string;
  combinacoesSemCobertura: CombinacaoDto[];
  emCurso: AtendimentoEmCursoDto[];
  guicheLiberado: boolean;
};

const ROTULO_ESTADO: Record<string, string> = { Chamado: "chamada", EmAtendimento: "em atendimento" };

function estado(estadoApi: string): string {
  return ROTULO_ESTADO[estadoApi] ?? estadoApi;
}

function combinacoes(lista: CombinacaoDto[]): string {
  return lista.map((c) => `${c.fila}/${c.prioridade} (${c.letra})`).join(", ");
}

/** Lines of the confirmation dialog, in reading order; empty means nothing beyond the deactivation itself. */
export function linhasDoImpacto(impacto: ImpactoDesativacaoDto): string[] {
  const linhas: string[] = [];
  for (const a of impacto.emCurso) {
    linhas.push(
      `A senha ${a.senha} está ${estado(a.estado)} nesta estação. Sem cancelar, o guichê fica com ela até o atendente encerrar; nenhuma senha nova é chamada.`,
    );
  }
  if (impacto.emCurso.length === 0 && impacto.guicheOcupado) {
    linhas.push("O guichê está em uso e será liberado; o atendente volta à escolha de guichê.");
  }
  if (impacto.combinacoesSemCobertura.length > 0) {
    linhas.push(`Ficam sem guichê e saem do totem: ${combinacoes(impacto.combinacoesSemCobertura)}.`);
  }
  return linhas;
}

/** The single atendimento the dialog may cancel: a station holds at most one in progress (UX_Atendimentos_EstacaoEmCurso). */
export function atendimentoCancelavel(impacto: ImpactoDesativacaoDto): AtendimentoEmCursoDto | null {
  return impacto.emCurso[0] ?? null;
}

/** Notice after the deactivation, null when there is nothing to tell. */
export function avisoDoResultado(resultado: EstacaoDesativadaDto): string | null {
  const partes: string[] = [];
  for (const a of resultado.emCurso) {
    partes.push(`A senha ${a.senha} segue ${estado(a.estado)}; o guichê é liberado quando ela terminar.`);
  }
  if (resultado.combinacoesSemCobertura.length > 0) {
    partes.push(`Sem guichê depois desta alteração (saem do totem): ${combinacoes(resultado.combinacoesSemCobertura)}.`);
  }
  return partes.length === 0 ? null : partes.join(" ");
}
