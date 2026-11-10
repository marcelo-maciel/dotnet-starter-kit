/// <reference types="node" />
// Run: node --test src/features/paf/estacoes/vinculo-estacao.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { avisosDoLinkGerado, formatarInstante, linhasDoVinculo, papeisComLink, usaLink } from "./vinculo-estacao.ts";

const CRIADO = "2026-10-19T13:00:00+00:00";
const RESGATADO = "2026-10-19T13:05:00+00:00";
const CONEXAO = "2026-10-19T14:30:00.123456+00:00";

test("só tipos com Emissão ou Painel usam link; guichê e administração não", () => {
  assert.deepEqual(papeisComLink("Guiche"), []);
  assert.deepEqual(papeisComLink("Administracao"), []);
  assert.equal(usaLink("Guiche"), false);
  assert.deepEqual(papeisComLink("Emissao"), ["Emissao"]);
  assert.deepEqual(papeisComLink("EmissaoGuiche"), ["Emissao"]);
  assert.deepEqual(papeisComLink("GuichePainel"), ["Painel"]);
  assert.deepEqual(papeisComLink("EmissaoPainel"), ["Emissao", "Painel"]);
  assert.equal(usaLink("Painel"), true);
});

test("instante no relógio de São Paulo, não no do navegador nem em UTC", () => {
  assert.equal(formatarInstante(CRIADO), "19/10/2026, 10:00");
});

test("guichê não tem linha de vínculo", () => {
  assert.deepEqual(
    linhasDoVinculo({ tipo: "Guiche", vinculoAtual: null, ultimaConexaoEmissaoEm: null, ultimaConexaoPainelEm: null }),
    [],
  );
});

test("sem link, link aguardando e link resgatado, com a última conexão por papel", () => {
  assert.deepEqual(
    linhasDoVinculo({ tipo: "EmissaoPainel", vinculoAtual: null, ultimaConexaoEmissaoEm: null, ultimaConexaoPainelEm: CONEXAO }),
    ["Sem link: gere um para vincular o aparelho.", "Totem: nunca conectou.", "Painel: última conexão em 19/10/2026, 11:30."],
  );
  assert.deepEqual(
    linhasDoVinculo({
      tipo: "Painel",
      vinculoAtual: { criadoEm: CRIADO, resgatadoEm: null, papeisResgatados: [] },
      ultimaConexaoEmissaoEm: CONEXAO,
      ultimaConexaoPainelEm: null,
    }),
    ["Link gerado em 19/10/2026, 10:00, aguardando o aparelho.", "Painel: nunca conectou."],
    "a coluna de emissão não aparece numa estação só de painel",
  );
  assert.deepEqual(
    linhasDoVinculo({
      tipo: "EmissaoPainel",
      vinculoAtual: { criadoEm: CRIADO, resgatadoEm: RESGATADO, papeisResgatados: ["Painel"] },
      ultimaConexaoEmissaoEm: null,
      ultimaConexaoPainelEm: CONEXAO,
    }),
    [
      "Link resgatado em 19/10/2026, 10:05.",
      "Totem: fora do resgate atual; nunca conectou.",
      "Painel: última conexão em 19/10/2026, 11:30.",
    ],
    "o tipo combinado mostra cada papel conforme o resgate atual",
  );
});

test("o aviso do link gerado diz que o anterior caiu só quando havia um", () => {
  assert.equal(avisosDoLinkGerado(false).length, 1);
  const comAnterior = avisosDoLinkGerado(true);
  assert.equal(comAnterior.length, 2);
  assert.match(comAnterior[1], /anterior deixou de valer/);
});
