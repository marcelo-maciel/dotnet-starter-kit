// Pure rules of the fila screen (RF02): the same refusals the server makes, shown before saving, plus the
// preview pairs and the setor grouping. The server stays the guarantee (node --test, D21).
import { formatoValido, razao, RAZAO_MINIMA } from "../aparencia/contraste.ts";

export const LETRA_MAX = 5;
export const SEM_SETOR = "Sem setor";

export type AparenciaForm = { fonte: string | null; corFonte: string | null; corBotaoSelecionado: string | null };
export type LinhaForm = { prioridadeId: string; prioridade: string; letra: string; numeroInicial: number; corBotao: string | null };
export type FilaForm = {
  nome: string;
  nomeExibicao: string;
  semRestricao: boolean;
  horaInicial: string;
  horaFinal: string;
  aparencia: AparenciaForm;
  linhas: LinhaForm[];
};

export type ParContraste = { botao: string; fundo: string; razao: number; suficiente: boolean };

/** Each colored button against the fila's font color; only pairs whose two colors are valid. */
export function paresDeContraste(aparencia: AparenciaForm, linhas: LinhaForm[]): ParContraste[] {
  const texto = aparencia.corFonte;
  if (!formatoValido(texto)) return [];
  const botoes: { botao: string; cor: string | null }[] = [
    { botao: "fila selecionada", cor: aparencia.corBotaoSelecionado },
    ...linhas.map((l) => ({ botao: l.prioridade, cor: l.corBotao })),
  ];
  return botoes
    .filter((b): b is { botao: string; cor: string } => formatoValido(b.cor))
    .map((b) => {
      const r = razao(texto, b.cor);
      return { botao: b.botao, fundo: b.cor, razao: r, suficiente: r >= RAZAO_MINIMA };
    });
}

function vazioParaNulo(valor: string | null): string | null {
  return valor === null || valor.trim() === "" ? null : valor.trim();
}

/** Blank inputs become null ("station default"), as the server expects. */
export function normalizarAparencia(aparencia: AparenciaForm): AparenciaForm {
  return {
    fonte: vazioParaNulo(aparencia.fonte),
    corFonte: vazioParaNulo(aparencia.corFonte),
    corBotaoSelecionado: vazioParaNulo(aparencia.corBotaoSelecionado),
  };
}

function problemasDaJanela(form: FilaForm): string[] {
  if (form.semRestricao) return [];
  if (form.horaInicial === "" || form.horaFinal === "") return ["Informe as duas horas da janela, ou marque sem restrição."];
  // "HH:MM" compares as text; equal hours describe an empty window (RF02, 03/10/2026).
  if (form.horaFinal <= form.horaInicial) return ["A hora final precisa ser posterior à inicial (a janela não vira a meia-noite)."];
  return [];
}

function problemasDaGrade(linhas: LinhaForm[]): string[] {
  if (linhas.length === 0) return ["A fila precisa de ao menos uma prioridade."];
  const problemas: string[] = [];
  for (const l of linhas) {
    const letra = l.letra.trim();
    if (letra.length === 0 || letra.length > LETRA_MAX) problemas.push(`A letra de ${l.prioridade} tem de 1 a ${LETRA_MAX} caracteres.`);
    if (!Number.isInteger(l.numeroInicial) || l.numeroInicial < 0) problemas.push(`O número inicial de ${l.prioridade} não pode ser negativo.`);
  }
  const letras = linhas.map((l) => l.letra.trim().toUpperCase());
  if (new Set(letras).size !== letras.length) problemas.push("A mesma letra de senha aparece duas vezes na grade.");
  if (new Set(linhas.map((l) => l.prioridadeId)).size !== linhas.length) problemas.push("A mesma prioridade aparece duas vezes na grade.");
  return problemas;
}

function problemasDeCor(aparencia: AparenciaForm, linhas: LinhaForm[]): string[] {
  const cores = [aparencia.corFonte, aparencia.corBotaoSelecionado, ...linhas.map((l) => l.corBotao)];
  if (cores.some((c) => c !== null && !formatoValido(c))) return ["Cor no formato #RRGGBB."];
  const temBotaoColorido = aparencia.corBotaoSelecionado !== null || linhas.some((l) => l.corBotao !== null);
  if (temBotaoColorido && aparencia.corFonte === null) return ["Informe a cor da fonte da fila para usar cor de botão."];
  return paresDeContraste(aparencia, linhas)
    .filter((p) => !p.suficiente)
    .map((p) => `Botão ${p.botao}: contraste ${p.razao.toFixed(2).replace(".", ",")}:1, abaixo de 4,5:1.`);
}

/** Every refusal the server would give for this form; empty = can save. */
export function problemasDaFila(form: FilaForm): string[] {
  const aparencia = normalizarAparencia(form.aparencia);
  const linhas = form.linhas.map((l) => ({ ...l, corBotao: vazioParaNulo(l.corBotao) }));
  const problemas: string[] = [];
  if (form.nome.trim() === "") problemas.push("Informe o nome da fila.");
  if (form.nomeExibicao.trim() === "") problemas.push("Informe o nome de exibição.");
  if ((aparencia.fonte?.length ?? 0) > 64) problemas.push("A fonte tem no máximo 64 caracteres.");
  return [...problemas, ...problemasDaJanela(form), ...problemasDaGrade(linhas), ...problemasDeCor(aparencia, linhas)];
}

export type GrupoSetor = { setorId: string | null; nome: string; filas: string[] };

/** Setores panel: each setor with its filas by name, then "Sem setor", the normal case, always listed when it has filas. */
export function filasPorSetor(
  setores: { id: string; nome: string }[],
  filas: { nome: string; setorId: string | null }[],
): GrupoSetor[] {
  const nomes = (setorId: string | null) =>
    filas.filter((f) => f.setorId === setorId).map((f) => f.nome).sort((a, b) => a.localeCompare(b, "pt-BR"));
  const grupos: GrupoSetor[] = [...setores]
    .sort((a, b) => a.nome.localeCompare(b.nome, "pt-BR"))
    .map((s) => ({ setorId: s.id, nome: s.nome, filas: nomes(s.id) }));
  const sem = nomes(null);
  return sem.length > 0 ? [...grupos, { setorId: null, nome: SEM_SETOR, filas: sem }] : grupos;
}

/** "Normal (A)" for each grid line no guichê covers: the totem hides its button until a guichê covers it (RF02). */
export function rotulosSemCobertura(
  fila: { prioridades: { id: string; prioridadeId: string; letra: string }[]; combinacoesSemCobertura: string[] },
  nomePrioridade: (id: string) => string,
): string[] {
  return fila.prioridades
    .filter((p) => fila.combinacoesSemCobertura.includes(p.id))
    .map((p) => `${nomePrioridade(p.prioridadeId)} (${p.letra})`);
}
