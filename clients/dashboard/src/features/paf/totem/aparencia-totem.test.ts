/// <reference types="node" />
// Run: node --test src/features/paf/totem/aparencia-totem.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  APARENCIA_EMISSAO_PADRAO,
  estiloDoBotao,
  lerAparenciaEmissao,
  type AparenciaDoBotao,
} from "./aparencia-totem.ts";

// Shape written by ConfiguracaoDaEstacao.Serializar: operational keys at the root, appearance under `emissao`.
const GRAVADA = JSON.stringify({
  prazoCicloEmissaoSegundos: 20,
  emissao: {
    titulo: "Bem-vindo",
    tamanhoFontePx: 32,
    tamanhoBotaoPx: 150,
    corFundoBotao: "#003366",
    corFonteBotao: "#FFFFFF",
    corDescricaoBotao: "#FFFF00",
  },
});

const SEM_COR_DA_FILA: AparenciaDoBotao = { fonte: null, corFonte: null, corBotao: null, corBotaoSelecionado: null };

test("lê o bloco Emissão gravado pelo cadastro", () => {
  assert.deepEqual(lerAparenciaEmissao(GRAVADA), {
    titulo: "Bem-vindo",
    tamanhoFontePx: 32,
    tamanhoBotaoPx: 150,
    cores: { fundo: "#003366", fonte: "#FFFFFF", descricao: "#FFFF00" },
    corTexto: null,
  });
});

test("C17: cor do texto da tela lida do bloco, inválida cai no preto padrão", () => {
  assert.equal(lerAparenciaEmissao(JSON.stringify({ emissao: { corTexto: "#FFFFFF" } })).corTexto, "#FFFFFF");
  assert.equal(lerAparenciaEmissao(JSON.stringify({ emissao: { corTexto: "#FFF" } })).corTexto, null);
  assert.equal(lerAparenciaEmissao(JSON.stringify({ emissao: {} })).corTexto, null);
});

test("estação sem configuração, JSON malformado ou sem bloco mantém o padrão", () => {
  assert.deepEqual(lerAparenciaEmissao(null), APARENCIA_EMISSAO_PADRAO);
  assert.deepEqual(lerAparenciaEmissao("{não é json"), APARENCIA_EMISSAO_PADRAO);
  assert.deepEqual(lerAparenciaEmissao("[1,2]"), APARENCIA_EMISSAO_PADRAO);
  assert.deepEqual(lerAparenciaEmissao("null"), APARENCIA_EMISSAO_PADRAO);
  // Rows written before C04 kept only operational keys at the root.
  assert.deepEqual(lerAparenciaEmissao(JSON.stringify({ prazoCicloEmissaoSegundos: 30 })), APARENCIA_EMISSAO_PADRAO);
  assert.deepEqual(lerAparenciaEmissao(JSON.stringify({ emissao: "x" })), APARENCIA_EMISSAO_PADRAO);
});

test("cada campo ausente ou inválido cai no padrão sem derrubar os outros", () => {
  const a = lerAparenciaEmissao(
    JSON.stringify({ emissao: { titulo: "  ", tamanhoFontePx: "32", tamanhoBotaoPx: -5, corFundoBotao: "#003366" } }),
  );
  assert.deepEqual(a, { titulo: null, tamanhoFontePx: null, tamanhoBotaoPx: null, cores: null, corTexto: null });
  const b = lerAparenciaEmissao(JSON.stringify({ emissao: { tamanhoBotaoPx: 44 } }));
  assert.equal(b.tamanhoBotaoPx, 44);
  assert.equal(b.titulo, null);
});

test("cores só valem como conjunto completo e no formato #RRGGBB", () => {
  const curta = lerAparenciaEmissao(
    JSON.stringify({ emissao: { corFundoBotao: "#036", corFonteBotao: "#FFFFFF", corDescricaoBotao: "#FFFF00" } }),
  );
  assert.equal(curta.cores, null);
  const alfa = lerAparenciaEmissao(
    JSON.stringify({ emissao: { corFundoBotao: "#003366FF", corFonteBotao: "#FFFFFF", corDescricaoBotao: "#FFFF00" } }),
  );
  assert.equal(alfa.cores, null);
  const faltando = lerAparenciaEmissao(JSON.stringify({ emissao: { corFundoBotao: "#003366", corFonteBotao: "#FFFFFF" } }));
  assert.equal(faltando.cores, null);
});

test("sem nenhuma cor, o botão fica com as classes padrão", () => {
  const e = estiloDoBotao(APARENCIA_EMISSAO_PADRAO, SEM_COR_DA_FILA, false);
  assert.deepEqual(e, { fundo: null, corFila: null, corDescricao: null, familiaFonte: null, tamanhoFontePx: null, tamanhoMinimoPx: null });
});

test("só a estação configurada: fundo, fonte e descrição da estação, mais tamanhos", () => {
  const e = estiloDoBotao(lerAparenciaEmissao(GRAVADA), SEM_COR_DA_FILA, false);
  assert.deepEqual(e, {
    fundo: "#003366",
    corFila: "#FFFFFF",
    corDescricao: "#FFFF00",
    familiaFonte: null,
    tamanhoFontePx: 32,
    tamanhoMinimoPx: 150,
  });
});

test("a cor da prioridade com a cor de fonte da fila vence a estação, como par inteiro", () => {
  const botao: AparenciaDoBotao = { fonte: "Verdana", corFonte: "#000000", corBotao: "#FFCC00", corBotaoSelecionado: "#FFFFFF" };
  const e = estiloDoBotao(lerAparenciaEmissao(GRAVADA), botao, false);
  assert.equal(e.fundo, "#FFCC00");
  assert.equal(e.corFila, "#000000");
  assert.equal(e.corDescricao, "#000000", "a descrição da estação nunca foi conferida contra a cor da fila");
  assert.equal(e.familiaFonte, "Verdana");
  assert.equal(e.tamanhoMinimoPx, 150);
});

test("nunca mistura um lado de cada par", () => {
  const estacao = lerAparenciaEmissao(GRAVADA);
  // Fila font color without the line's button color: the station pair stays whole.
  const soFonte = estiloDoBotao(estacao, { ...SEM_COR_DA_FILA, corFonte: "#000000" }, false);
  assert.deepEqual([soFonte.fundo, soFonte.corFila, soFonte.corDescricao], ["#003366", "#FFFFFF", "#FFFF00"]);
  // Line color without the fila's font color (refused by C09, possible in old rows): same.
  const soFundo = estiloDoBotao(estacao, { ...SEM_COR_DA_FILA, corBotao: "#FFCC00" }, false);
  assert.deepEqual([soFundo.fundo, soFundo.corFila], ["#003366", "#FFFFFF"]);
  // And with no station colors either: defaults, never a lone fila color over the default blue.
  const nada = estiloDoBotao(APARENCIA_EMISSAO_PADRAO, { ...SEM_COR_DA_FILA, corBotao: "#FFCC00" }, false);
  assert.deepEqual([nada.fundo, nada.corFila], [null, null]);
});

test("botão tocado usa a cor de fila selecionada e, sem ela, mantém a própria", () => {
  const botao: AparenciaDoBotao = { fonte: null, corFonte: "#000000", corBotao: "#FFCC00", corBotaoSelecionado: "#FFFFFF" };
  assert.equal(estiloDoBotao(APARENCIA_EMISSAO_PADRAO, botao, true).fundo, "#FFFFFF");
  assert.equal(estiloDoBotao(APARENCIA_EMISSAO_PADRAO, { ...botao, corBotaoSelecionado: null }, true).fundo, "#FFCC00");
  assert.equal(estiloDoBotao(APARENCIA_EMISSAO_PADRAO, botao, false).fundo, "#FFCC00");
});
