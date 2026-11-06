// Prioridade cadastro (RF05): what the screen checks before sending and what it warns about. The server is the
// authority (FluentValidation 400 on the name, JSON binding 400 on a non-integer value); this only spares the round trip.

export const EXPLICACAO_VALOR =
  "O valor define a precedência na chamada: valor maior é chamado antes. Com o mesmo valor, vale a ordem de emissão. No guichê que respeita a ordem das filas, a ordem das filas vem antes do valor.";

export const NOME_MAXIMO = 120;

// Prioridade.Valor is a C# int: anything outside it the server cannot bind.
const INT_MINIMO = -2_147_483_648;
const INT_MAXIMO = 2_147_483_647;

export type PrioridadeValidada = { ok: true; nome: string; valor: number } | { ok: false; mensagem: string };

export function validarPrioridade(nome: string, valorTexto: string): PrioridadeValidada {
  const nomeLimpo = nome.trim();
  if (nomeLimpo === "") return { ok: false, mensagem: "Informe o nome da prioridade." };
  if (nomeLimpo.length > NOME_MAXIMO) return { ok: false, mensagem: `O nome tem no máximo ${NOME_MAXIMO} caracteres.` };

  const texto = valorTexto.trim();
  // A regex, not Number(): Number accepts "1e3", "0x10" and "" (as 0), none of which is what the operator typed.
  if (!/^-?\d+$/.test(texto)) return { ok: false, mensagem: "O valor precisa ser um número inteiro, sem casas decimais." };
  const valor = Number(texto);
  if (valor < INT_MINIMO || valor > INT_MAXIMO) return { ok: false, mensagem: "O valor está fora do limite aceito." };
  return { ok: true, nome: nomeLimpo, valor };
}

export type LinhaDaGrade = { id: string; prioridadeId: string; letra: string };
export type FilaComGrade = {
  nome: string;
  ativa: boolean;
  prioridades: LinhaDaGrade[];
  combinacoesSemCobertura: string[];
};

/**
 * Buttons the totem loses when the prioridade is deactivated (RF02, C07): its lines in active filas that a guichê
 * still covers. Lines already uncovered or in an inactive fila are off the totem already, so they are not news.
 */
export function combinacoesQueSomemDoTotem(prioridadeId: string, filas: FilaComGrade[]): string[] {
  return filas
    .filter((f) => f.ativa)
    .flatMap((f) =>
      f.prioridades
        .filter((l) => l.prioridadeId === prioridadeId && !f.combinacoesSemCobertura.includes(l.id))
        .map((l) => `${f.nome} (${l.letra})`),
    );
}

/** Why an active prioridade has no button on the totem: not in any active fila's grid, or no guichê covers it. */
export function combinacoesSemGuiche(prioridadeId: string, filas: FilaComGrade[]): string[] {
  return filas
    .filter((f) => f.ativa)
    .flatMap((f) =>
      f.prioridades
        .filter((l) => l.prioridadeId === prioridadeId && f.combinacoesSemCobertura.includes(l.id))
        .map((l) => `${f.nome} (${l.letra})`),
    );
}

export function estaEmAlgumaFilaAtiva(prioridadeId: string, filas: FilaComGrade[]): boolean {
  return filas.some((f) => f.ativa && f.prioridades.some((l) => l.prioridadeId === prioridadeId));
}
