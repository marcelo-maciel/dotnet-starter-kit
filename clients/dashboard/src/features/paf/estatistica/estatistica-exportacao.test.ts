/// <reference types="node" />
// Run: node --test src/features/paf/estatistica/estatistica-exportacao.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  formatarDuracao,
  paraCsvBytes,
  paraTsv,
  textoNaTela,
  type ResultadoEstatistica,
} from "./estatistica-exportacao.ts";

const resultado: ResultadoEstatistica = {
  tipo: "AtendimentosPorTempoDeEspera",
  colunas: [
    { chave: "unidade", titulo: "Unidade", tipo: "Texto" },
    { chave: "fila", titulo: "Fila", tipo: "Texto" },
    { chave: "tempoMedioEspera", titulo: "Tempo médio de espera", tipo: "DuracaoSegundos" },
    { chave: "totalAtendimentos", titulo: "Total", tipo: "Inteiro" },
    { chave: "percentual", titulo: "%", tipo: "Percentual" },
  ],
  linhas: [
    {
      unidadeId: "u1",
      celulas: [
        { texto: "São João", numero: null },
        { texto: 'Fila "A"; balcão', numero: null },
        { texto: null, numero: 240.5 },
        { texto: null, numero: 3 },
        { texto: null, numero: 33.33 },
      ],
    },
    {
      unidadeId: "u1",
      celulas: [
        { texto: "São João", numero: null },
        { texto: "=HYPERLINK(1)", numero: null },
        { texto: null, numero: null },
        { texto: null, numero: 0 },
        { texto: null, numero: null },
      ],
    },
  ],
  totais: [
    {
      unidadeId: null,
      celulas: [
        { texto: "Total", numero: null },
        { texto: null, numero: null },
        { texto: null, numero: 240.5 },
        { texto: null, numero: 3 },
        { texto: null, numero: 100 },
      ],
    },
  ],
};

const decodificar = (bytes: Uint8Array) => new TextDecoder("utf-8", { ignoreBOM: true }).decode(bytes);

test("CSV começa com o BOM do UTF-8 (EF BB BF), uma vez só", () => {
  const bytes = paraCsvBytes(resultado);
  assert.deepEqual([...bytes.slice(0, 3)], [0xef, 0xbb, 0xbf]);
  assert.notDeepEqual([...bytes.slice(3, 6)], [0xef, 0xbb, 0xbf]);
});

test("CSV separa por ponto e vírgula, quebra em CRLF e termina a última linha", () => {
  const linhas = decodificar(paraCsvBytes(resultado)).slice(1).split("\r\n");
  assert.equal(linhas[0], "Unidade;Fila;Tempo médio de espera (segundos);Total;%");
  assert.equal(linhas.length, 5, "cabeçalho, duas linhas, um total e o vazio depois do CRLF final");
  assert.equal(linhas.at(-1), "");
  assert.equal(decodificar(paraCsvBytes(resultado)).replaceAll("\r\n", "").includes("\n"), false);
});

test("CSV põe entre aspas o campo com ponto e vírgula ou aspas e duplica as aspas", () => {
  const linhas = decodificar(paraCsvBytes(resultado)).slice(1).split("\r\n");
  assert.equal(linhas[1], 'São João;"Fila ""A""; balcão";240,5;3;33,33');
});

test("CSV escreve decimais com vírgula, média vazia como célula vazia e o total depois das linhas", () => {
  const linhas = decodificar(paraCsvBytes(resultado)).slice(1).split("\r\n");
  assert.equal(linhas[2], "São João;'=HYPERLINK(1);;0;");
  assert.equal(linhas[3], "Total;;240,5;3;100");
});

test("CSV grava acentos em UTF-8 (ã = C3 A3), nunca em Latin-1", () => {
  const bytes = paraCsvBytes(resultado);
  assert.ok([...bytes].some((b, i) => b === 0xc3 && bytes[i + 1] === 0xa3), "ã em UTF-8");
  // fatal: a Latin-1 byte (a lone E3) would throw here.
  assert.ok(new TextDecoder("utf-8", { fatal: true }).decode(bytes).includes("São João;"));
});

test("TSV separa por tabulação, sem BOM e sem aspas, com os mesmos valores do CSV", () => {
  const tsv = paraTsv({
    ...resultado,
    linhas: [{ unidadeId: "u1", celulas: [{ texto: "Loja\tDemo", numero: null }, { texto: 'A "1"\r\nB', numero: null }, { texto: null, numero: 61 }, { texto: null, numero: 1 }, { texto: null, numero: 12.5 }] }],
  });
  const linhas = tsv.split("\r\n");
  assert.equal(tsv.charCodeAt(0) === 0xfeff, false);
  assert.equal(linhas[0], "Unidade\tFila\tTempo médio de espera (segundos)\tTotal\t%");
  assert.equal(linhas[1], 'Loja Demo\tA "1" B\t61\t1\t12,5');
  assert.equal(linhas[2], "Total\t\t240,5\t3\t100");
});

test("tela formata duração, percentual e média vazia", () => {
  const [, , espera, total, pct] = resultado.colunas;
  assert.equal(textoNaTela(espera, { texto: null, numero: 240.5 }), "4:01");
  assert.equal(textoNaTela(espera, { texto: null, numero: null }), "–");
  assert.equal(textoNaTela(total, { texto: null, numero: 0 }), "0");
  assert.equal(textoNaTela(pct, { texto: null, numero: 33.33 }), "33,33 %");
  assert.equal(formatarDuracao(0), "0:00");
  assert.equal(formatarDuracao(59.4), "0:59");
  assert.equal(formatarDuracao(3600), "1:00:00");
  assert.equal(formatarDuracao(3725), "1:02:05");
});
