/// <reference types="node" />
// Run: node --test src/features/paf/painel/painel-estado.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  ESTADO_INICIAL_PAINEL,
  reduzirPainel,
  type AtendimentoAtualizadoEvento,
  type ChamadaPainelDto,
  type EstadoPainel,
  type SenhaChamadaEvento,
} from "./painel-estado.ts";

let proximoEvento = 0;

function chamada(atendimentoId: string, versao: number, senha: string): SenhaChamadaEvento {
  proximoEvento += 1;
  return {
    eventoId: `ev-${proximoEvento}`,
    unidadeId: "u1",
    atendimentoId,
    fichaId: `f-${atendimentoId}`,
    versao,
    senha,
    estacaoId: "g1",
    nomeEstacao: "Guichê 1",
  };
}

function atualizacao(atendimentoId: string, versao: number, estado: string): AtendimentoAtualizadoEvento {
  proximoEvento += 1;
  return {
    eventoId: `ev-${proximoEvento}`,
    unidadeId: "u1",
    atendimentoId,
    fichaId: `f-${atendimentoId}`,
    versao,
    estado,
    estacaoId: null,
  };
}

/** Panel already announcing, with a1 called at Versao 1 still waiting its turn in the queue. */
function comA1Chamado(): EstadoPainel {
  const anunciando = reduzirPainel(ESTADO_INICIAL_PAINEL, { tipo: "anunciosIniciados" });
  return reduzirPainel(anunciando, { tipo: "senhaChamada", evento: chamada("a1", 1, "A1") });
}

test("reabilitado (Aguardando com versão maior) sai da lista e da fila de anúncios", () => {
  const antes = comA1Chamado();
  assert.equal(antes.chamadas.length, 1);
  assert.equal(antes.fila.length, 1);

  const depois = reduzirPainel(antes, { tipo: "atendimentoAtualizado", evento: atualizacao("a1", 3, "Aguardando") });

  assert.deepEqual(depois.chamadas, []);
  assert.deepEqual(depois.fila, []);
  assert.equal(depois.versoes.a1, 3);
});

test("Aguardando com versão menor ou igual à aplicada é ignorado", () => {
  const antes = comA1Chamado();
  const recall = reduzirPainel(antes, { tipo: "senhaRechamada", evento: chamada("a1", 4, "A1") });

  const menor = reduzirPainel(recall, { tipo: "atendimentoAtualizado", evento: atualizacao("a1", 3, "Aguardando") });
  const igual = reduzirPainel(recall, { tipo: "atendimentoAtualizado", evento: atualizacao("a1", 4, "Aguardando") });

  assert.equal(menor, recall, "mesma referência: nada mudou");
  assert.equal(igual, recall);
  assert.equal(recall.chamadas[0]?.atendimentoId, "a1");
});

test("ciclo cancelar, reabilitar e chamar de novo volta ao painel e ao anúncio", () => {
  const cancelado = reduzirPainel(comA1Chamado(), {
    tipo: "atendimentoAtualizado",
    evento: atualizacao("a1", 2, "Cancelado"),
  });
  const reabilitado = reduzirPainel(cancelado, {
    tipo: "atendimentoAtualizado",
    evento: atualizacao("a1", 3, "Aguardando"),
  });
  // The direct copy of the old call arriving late (Versao 1) must not resurrect it.
  const atrasada = reduzirPainel(reabilitado, { tipo: "senhaChamada", evento: chamada("a1", 1, "A1") });
  assert.equal(atrasada, reabilitado);

  const chamadaNova = reduzirPainel(reabilitado, { tipo: "senhaChamada", evento: chamada("a1", 4, "A1") });

  assert.deepEqual(chamadaNova.chamadas.map((c) => [c.atendimentoId, c.estado]), [["a1", "Chamado"]]);
  assert.deepEqual(chamadaNova.fila.map((a) => [a.atendimentoId, a.versao]), [["a1", 4]]);
});

test("Aguardando de outro atendimento não mexe na chamada exibida", () => {
  const antes = comA1Chamado();
  const depois = reduzirPainel(antes, { tipo: "atendimentoAtualizado", evento: atualizacao("a2", 5, "Aguardando") });

  assert.deepEqual(depois.chamadas, antes.chamadas);
  assert.deepEqual(depois.fila, antes.fila);
});

test("EmAtendimento continua exibido (regressão: só Cancelado e Aguardando saem)", () => {
  const depois = reduzirPainel(comA1Chamado(), {
    tipo: "atendimentoAtualizado",
    evento: atualizacao("a1", 2, "EmAtendimento"),
  });

  assert.deepEqual(depois.chamadas.map((c) => [c.atendimentoId, c.estado]), [["a1", "EmAtendimento"]]);
  assert.equal(depois.fila.length, 1);
});

function chamadaDoServidor(atendimentoId: string, versao: number): ChamadaPainelDto {
  return {
    atendimentoId,
    fichaId: `f-${atendimentoId}`,
    versao,
    senha: atendimentoId.toUpperCase(),
    estado: "Encerrado",
    estacaoId: "g1",
    nomeEstacao: "Guichê 1",
    chamadoEm: "2026-10-19T12:00:00Z",
    ultimaRechamadaEm: null,
  };
}

test("a lista guarda a quantidade do RF06 que a reconsulta traz, também quando chegam chamadas novas", () => {
  const carregado = reduzirPainel(ESTADO_INICIAL_PAINEL, {
    tipo: "estadoCarregado",
    estado: { quantidadeChamadas: 3, chamadas: ["a1", "a2", "a3", "a4"].map((id) => chamadaDoServidor(id, 1)) },
  });
  assert.equal(carregado.limite, 3);
  assert.deepEqual(carregado.chamadas.map((c) => c.atendimentoId), ["a1", "a2", "a3"]);

  const depois = reduzirPainel(carregado, { tipo: "senhaChamada", evento: chamada("a9", 1, "A9") });
  assert.deepEqual(depois.chamadas.map((c) => c.atendimentoId), ["a9", "a1", "a2"]);
});

test("acima de 10 a lista cresce até a quantidade configurada", () => {
  const ids = Array.from({ length: 12 }, (_, i) => `b${i}`);
  let estado = reduzirPainel(ESTADO_INICIAL_PAINEL, {
    tipo: "estadoCarregado",
    estado: { quantidadeChamadas: 12, chamadas: [] },
  });
  for (const id of ids) estado = reduzirPainel(estado, { tipo: "senhaChamada", evento: chamada(id, 1, id) });
  assert.equal(estado.chamadas.length, 12);
  estado = reduzirPainel(estado, { tipo: "senhaChamada", evento: chamada("b99", 1, "B99") });
  assert.equal(estado.chamadas.length, 12);
  assert.equal(estado.chamadas[0]?.atendimentoId, "b99");
});
