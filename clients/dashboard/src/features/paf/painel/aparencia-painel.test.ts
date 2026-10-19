/// <reference types="node" />
// Run: node --test src/features/paf/painel/aparencia-painel.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  APARENCIA_PAINEL_PADRAO,
  corDaChamada,
  formatarDataHora,
  lerAparenciaPainel,
} from "./aparencia-painel.ts";

// Shape written by ConfiguracaoDaEstacao.Serializar: operational keys at the root, appearance under `painel`.
const GRAVADA = JSON.stringify({
  tempoExibicaoChamadaSegundos: 8,
  painel: {
    mostrarDataHora: true,
    tamanhoFontePx: 20,
    corFundo: "#000033",
    corFonteFilas: "#FFFFFF",
    corFonteChamada: "#FFFF00",
    corFonteAlternada: "#00FFFF",
    modo: "Normal",
  },
});

test("lê o bloco Painel gravado pelo cadastro", () => {
  assert.deepEqual(lerAparenciaPainel(GRAVADA), {
    tamanhoFontePx: 20,
    cores: { fundo: "#000033", filas: "#FFFFFF", chamada: "#FFFF00", alternada: "#00FFFF" },
    mostrarDataHora: true,
    mostrarUltimasChamadas: false,
  });
});

test("painel sem configuração, JSON malformado ou sem bloco mantém a aparência atual", () => {
  assert.deepEqual(lerAparenciaPainel(null), APARENCIA_PAINEL_PADRAO);
  assert.deepEqual(lerAparenciaPainel("{"), APARENCIA_PAINEL_PADRAO);
  assert.deepEqual(lerAparenciaPainel("42"), APARENCIA_PAINEL_PADRAO);
  assert.deepEqual(lerAparenciaPainel(JSON.stringify({ tempoExibicaoChamadaSegundos: 8 })), APARENCIA_PAINEL_PADRAO);
  // The default is today's screen: no clock, latest calls listed.
  assert.equal(APARENCIA_PAINEL_PADRAO.mostrarDataHora, false);
  assert.equal(APARENCIA_PAINEL_PADRAO.mostrarUltimasChamadas, true);
});

test("data e hora só aparece com o valor verdadeiro", () => {
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { mostrarDataHora: false } })).mostrarDataHora, false);
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { mostrarDataHora: "true" } })).mostrarDataHora, false);
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { mostrarDataHora: true } })).mostrarDataHora, true);
});

test("modo: Normal esconde as últimas chamadas; Informativo e ausente mostram", () => {
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { modo: "Normal" } })).mostrarUltimasChamadas, false);
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { modo: "Informativo" } })).mostrarUltimasChamadas, true);
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: {} })).mostrarUltimasChamadas, true);
});

test("cores só valem como conjunto completo e no formato #RRGGBB", () => {
  const tres = lerAparenciaPainel(
    JSON.stringify({ painel: { corFundo: "#000033", corFonteFilas: "#FFFFFF", corFonteChamada: "#FFFF00" } }),
  );
  assert.equal(tres.cores, null);
  const curta = lerAparenciaPainel(
    JSON.stringify({ painel: { corFundo: "#003", corFonteFilas: "#FFFFFF", corFonteChamada: "#FFFF00", corFonteAlternada: "#00FFFF" } }),
  );
  assert.equal(curta.cores, null);
});

test("fonte não positiva ou não numérica cai no padrão", () => {
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { tamanhoFontePx: 0 } })).tamanhoFontePx, null);
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { tamanhoFontePx: "20" } })).tamanhoFontePx, null);
  assert.equal(lerAparenciaPainel(JSON.stringify({ painel: { tamanhoFontePx: 400 } })).tamanhoFontePx, 400);
});

test("a chamada alterna entre as duas cores só enquanto está em destaque", () => {
  const cores = lerAparenciaPainel(GRAVADA).cores!;
  assert.equal(corDaChamada(cores, true, 0), "#FFFF00");
  assert.equal(corDaChamada(cores, true, 1), "#00FFFF");
  assert.equal(corDaChamada(cores, true, 2), "#FFFF00");
  assert.equal(corDaChamada(cores, false, 1), "#FFFF00");
});

test("data e hora sai no fuso de São Paulo, qualquer que seja o da máquina", () => {
  // 02:30 UTC on 2 Jan is 23:30 of 1 Jan in São Paulo (UTC-3, no DST since 2019).
  assert.equal(formatarDataHora(new Date("2026-01-02T02:30:00Z")), "01/01/2026, 23:30");
});
