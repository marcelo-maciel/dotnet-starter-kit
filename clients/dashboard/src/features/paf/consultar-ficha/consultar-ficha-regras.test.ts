/// <reference types="node" />
// Run: node --test src/features/paf/consultar-ficha/consultar-ficha-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import type { AtendimentoDaFichaDto } from "./consultar-ficha-api.ts";
import {
  acoesDoAtendimento,
  filaDeOrigem,
  formatarDuracao,
  formatarInstante,
  hojeEmSaoPaulo,
  normalizarSenha,
  rotuloEstado,
  SENHA_MAX,
  senhaValida,
} from "./consultar-ficha-regras.ts";

test("senha digitada com espaços nas pontas e minúsculas vira a impressa", () => {
  assert.equal(normalizarSenha("  ap12\t"), "AP12");
  assert.equal(normalizarSenha("A1"), "A1");
});

test("senha sem número no fim, vazia ou longa demais não sai para o servidor", () => {
  assert.equal(senhaValida("A1"), true);
  assert.equal(senhaValida(" a1 "), true);
  assert.equal(senhaValida("A1B"), false);
  assert.equal(senhaValida("AB"), false);
  assert.equal(senhaValida("   "), false);
  assert.equal(senhaValida(""), false);
  assert.equal(senhaValida("A".repeat(5) + "1".repeat(SENHA_MAX - 5)), true);
  assert.equal(senhaValida("A".repeat(5) + "1".repeat(SENHA_MAX - 4)), false);
});

test("hoje é o dia de São Paulo, não o UTC", () => {
  // 02:30 UTC on the 20th is still 23:30 on the 19th in São Paulo (UTC-3).
  assert.equal(hojeEmSaoPaulo(new Date("2026-10-20T02:30:00Z")), "2026-10-19");
  assert.equal(hojeEmSaoPaulo(new Date("2026-10-20T03:00:00Z")), "2026-10-20");
});

test("cancelar só em estado não terminal e reabilitar só no cancelado", () => {
  assert.deepEqual(acoesDoAtendimento("Aguardando"), { cancelar: true, reabilitar: false });
  assert.deepEqual(acoesDoAtendimento("Chamado"), { cancelar: true, reabilitar: false });
  assert.deepEqual(acoesDoAtendimento("EmAtendimento"), { cancelar: true, reabilitar: false });
  assert.deepEqual(acoesDoAtendimento("Encerrado"), { cancelar: false, reabilitar: false });
  assert.deepEqual(acoesDoAtendimento("Cancelado"), { cancelar: false, reabilitar: true });
  assert.equal(rotuloEstado("EmAtendimento"), "Em atendimento");
});

test("duração em hh:mm:ss, passando de um dia, e traço quando não começou", () => {
  assert.equal(formatarDuracao(null), "-");
  assert.equal(formatarDuracao(0), "00:00:00");
  assert.equal(formatarDuracao(59.9), "00:00:59");
  assert.equal(formatarDuracao(3 * 3600 + 61), "03:01:01");
  assert.equal(formatarDuracao(26 * 3600), "26:00:00");
  assert.equal(formatarDuracao(-5), "00:00:00");
});

test("instante mostrado em São Paulo com o dia", () => {
  assert.equal(formatarInstante(null), "-");
  assert.match(formatarInstante("2026-10-20T02:30:05Z"), /^19\/10,? 23:30:05$/);
});

function atendimento(id: string, fila: string, origem: string | null): AtendimentoDaFichaDto {
  return {
    atendimentoId: id, filaId: `f-${id}`, fila, prioridade: "Normal", estado: "Aguardando", guiche: null,
    criadoEm: "2026-10-19T12:00:00Z", chamadoEm: null, iniciadoEm: null, fimEm: null, tempoTotalSegundos: null,
    atendimentoOrigemId: origem, versao: 0,
  };
}

test("encaminhado aponta a fila de origem; o da emissão não tem origem", () => {
  const origem = atendimento("1", "Caixa", null);
  const destino = atendimento("2", "Protocolo", "1");
  const orfao = atendimento("3", "Retaguarda", "x");
  assert.equal(filaDeOrigem([origem, destino], origem), null);
  assert.equal(filaDeOrigem([origem, destino], destino), "Caixa");
  assert.equal(filaDeOrigem([origem, orfao], orfao), null);
});
