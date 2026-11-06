// RF07 result as the screen shows it, as the clipboard carries it (TSV) and as the file carries it (CSV for Excel
// pt-BR). Pure, no "@/" imports, so `node --test` runs it without the alias resolver (D21).

export type TipoColuna = "Texto" | "Inteiro" | "DuracaoSegundos" | "Percentual";

export type ColunaEstatistica = { chave: string; titulo: string; tipo: TipoColuna };
export type CelulaEstatistica = { texto: string | null; numero: number | null };
export type LinhaEstatistica = { unidadeId: string | null; celulas: CelulaEstatistica[] };
export type ResultadoEstatistica = {
  tipo: string;
  colunas: ColunaEstatistica[];
  linhas: LinhaEstatistica[];
  totais: LinhaEstatistica[];
};

const BOM_UTF8 = "﻿";
const CRLF = "\r\n";

function doisDigitos(n: number): string {
  return String(n).padStart(2, "0");
}

/** Seconds as `m:ss` or `h:mm:ss`, rounded to the second: the screen never shows fractions of a second. */
export function formatarDuracao(segundos: number): string {
  const total = Math.round(segundos);
  const horas = Math.floor(total / 3600);
  const minutos = Math.floor((total % 3600) / 60);
  const resto = total % 60;
  return horas > 0 ? `${horas}:${doisDigitos(minutos)}:${doisDigitos(resto)}` : `${minutos}:${doisDigitos(resto)}`;
}

/** Decimal comma, no thousands separator: Excel pt-BR reads "1234,5" as a number and "1.234,5" depends on locale. */
export function numeroComVirgula(numero: number): string {
  return String(numero).replace(".", ",");
}

/** Screen text of one cell; an empty average (no atendimento) is a dash, never a zero. */
export function textoNaTela(coluna: ColunaEstatistica, celula: CelulaEstatistica): string {
  if (coluna.tipo === "Texto") return celula.texto ?? "";
  if (celula.numero === null) return "–";
  if (coluna.tipo === "DuracaoSegundos") return formatarDuracao(celula.numero);
  if (coluna.tipo === "Percentual") return `${numeroComVirgula(celula.numero)} %`;
  return numeroComVirgula(celula.numero);
}

// A name typed in a cadastro (fila, estação, atendente) that starts like a formula would run when the file is
// opened in Excel (CSV injection); the apostrophe makes Excel keep it as text.
function textoSemFormula(texto: string): string {
  return /^[=+\-@\t\r]/.test(texto) ? `'${texto}` : texto;
}

// Files keep numbers as numbers (seconds and percent points, decimal comma), so the sheet can sum and sort them.
function valorExportado(coluna: ColunaEstatistica, celula: CelulaEstatistica): string {
  if (coluna.tipo === "Texto") return textoSemFormula(celula.texto ?? "");
  return celula.numero === null ? "" : numeroComVirgula(celula.numero);
}

function tituloExportado(coluna: ColunaEstatistica): string {
  return coluna.tipo === "DuracaoSegundos" ? `${coluna.titulo} (segundos)` : coluna.titulo;
}

function grade(resultado: ResultadoEstatistica): string[][] {
  const { colunas } = resultado;
  const corpo = [...resultado.linhas, ...resultado.totais].map((linha) =>
    colunas.map((coluna, i) => valorExportado(coluna, linha.celulas[i] ?? { texto: null, numero: null })),
  );
  return [colunas.map(tituloExportado), ...corpo];
}

function campoCsv(valor: string): string {
  return /[;"\r\n]/.test(valor) ? `"${valor.replaceAll('"', '""')}"` : valor;
}

/** Clipboard text: tab between cells, CRLF between rows; a tab or line break inside a name becomes a space. */
export function paraTsv(resultado: ResultadoEstatistica): string {
  return grade(resultado)
    .map((linha) => linha.map((v) => v.replace(/[\t\r\n]+/g, " ")).join("\t"))
    .join(CRLF);
}

/** File bytes for Excel pt-BR by double click: UTF-8 with BOM, `;` separator, CRLF, quotes doubled inside quotes. */
export function paraCsvBytes(resultado: ResultadoEstatistica): Uint8Array<ArrayBuffer> {
  const texto = grade(resultado)
    .map((linha) => linha.map(campoCsv).join(";"))
    .join(CRLF);
  return new TextEncoder().encode(BOM_UTF8 + texto + CRLF);
}
