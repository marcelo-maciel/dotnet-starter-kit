/// <reference types="node" />
// Run: node --test src/features/paf/operador/monitoramento.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  agruparPorUnidade,
  instanteEmSaoPaulo,
  rotuloPapel,
  situacaoPapel,
  type PapelEstacaoMonitorado,
} from "./monitoramento.ts";

function papel(campos: Partial<PapelEstacaoMonitorado>): PapelEstacaoMonitorado {
  return {
    unidadeId: "u1",
    unidadeNome: "Loja 1",
    estacaoId: "e1",
    estacaoNome: "Totem",
    tipo: "Emissao",
    ativa: true,
    papel: "Emissao",
    ultimaConexaoEm: null,
    ultimaQuedaEm: null,
    conectada: false,
    aviso: false,
    ...campos,
  };
}

test("situação: desativada vence tudo, conectada vence as colunas, nunca conectou sem instante", () => {
  assert.equal(situacaoPapel(papel({ ativa: false, conectada: true })), "Desativada");
  assert.equal(situacaoPapel(papel({ conectada: true, ultimaConexaoEm: null })), "Conectada");
  assert.equal(situacaoPapel(papel({ conectada: false, ultimaConexaoEm: null })), "Nunca conectou");
  // Dead instance: the columns still hold a connection newer than the drop, the server says not connected.
  assert.equal(
    situacaoPapel(papel({ conectada: false, ultimaConexaoEm: "2026-10-19T14:00:00Z", ultimaQuedaEm: null })),
    "Sem conexão",
  );
});

test("instante sai em America/Sao_Paulo, não no fuso do navegador", () => {
  assert.equal(instanteEmSaoPaulo("2026-10-19T14:59:30Z"), "19/10/2026, 11:59:30");
  assert.equal(instanteEmSaoPaulo("2026-10-20T02:30:00+00:00"), "19/10/2026, 23:30:00");
  assert.equal(instanteEmSaoPaulo(null), "nunca");
});

test("papel desconhecido aparece cru, os dois papéis têm rótulo próprio", () => {
  assert.equal(rotuloPapel("Emissao"), "Totem (emissão)");
  assert.equal(rotuloPapel("Painel"), "Painel chamador");
  assert.equal(rotuloPapel("Outro"), "Outro");
});

test("agrupa na ordem do servidor e conta os avisos por unidade", () => {
  const grupos = agruparPorUnidade([
    papel({ estacaoId: "a", aviso: true }),
    papel({ estacaoId: "b", papel: "Painel", aviso: true }),
    papel({ unidadeId: "u2", unidadeNome: "Loja 2", estacaoId: "c" }),
  ]);
  assert.deepEqual(
    grupos.map((g) => [g.unidadeNome, g.papeis.map((p) => p.estacaoId), g.avisos]),
    [
      ["Loja 1", ["a", "b"], 2],
      ["Loja 2", ["c"], 0],
    ],
  );
  assert.deepEqual(agruparPorUnidade([]), []);
});
