// ADR 0005 consequences: the notices of two operations on one atendimento can arrive in the reverse
// order of their commits (two instances behind the backplane). Both screens must drop the lower Versao.
// Executor (decisão D21 do plano da fatia 2): node --test with Node 22 type stripping, no new dependency.
//   cd clients/dashboard && node --test "tests/unit/paf/*.unit.mjs"
// The suffix .unit.mjs keeps Playwright (testDir ./tests, *.test|spec.*) from collecting these files.
import { test } from "node:test";
import assert from "node:assert/strict";
import { ESTADO_INICIAL_PAINEL, reduzirPainel } from "../../../src/features/paf/painel/painel-estado.ts";
import { decidirAviso } from "../../../src/features/paf/guiche/avisos-guiche.ts";

const ATENDIMENTO = "0199a000-0000-7000-8000-000000000001";
const OUTRO = "0199a000-0000-7000-8000-000000000002";
const FICHA = "0199a000-0000-7000-8000-0000000000f1";
const UNIDADE = "0199a000-0000-7000-8000-0000000000u1";
const GUICHE = "0199a000-0000-7000-8000-0000000000g1";

let sequencia = 0;
const novoEventoId = () => `evento-${++sequencia}`;

const chamada = (atendimentoId, versao, eventoId = novoEventoId()) => ({
  eventoId,
  unidadeId: UNIDADE,
  atendimentoId,
  fichaId: FICHA,
  versao,
  senha: atendimentoId === ATENDIMENTO ? "A15" : "A16",
  estacaoId: GUICHE,
  nomeEstacao: "Guichê 1",
});

const atualizado = (atendimentoId, versao, estado, eventoId = novoEventoId()) => ({
  eventoId,
  unidadeId: UNIDADE,
  atendimentoId,
  fichaId: FICHA,
  versao,
  estado,
  estacaoId: GUICHE,
});

const aplicar = (acoes) => acoes.reduce(reduzirPainel, reduzirPainel(ESTADO_INICIAL_PAINEL, { tipo: "anunciosIniciados" }));

// ─── painel ────────────────────────────────────────────────────────

test("painel: Cancelado v3 antes da rechamada v2 e da chamada v1 termina sem a senha e sem anúncio", () => {
  const estado = aplicar([
    { tipo: "atendimentoAtualizado", evento: atualizado(ATENDIMENTO, 3, "Cancelado") },
    { tipo: "senhaRechamada", evento: chamada(ATENDIMENTO, 2) },
    { tipo: "senhaChamada", evento: chamada(ATENDIMENTO, 1) },
  ]);

  assert.deepEqual(estado.chamadas, []);
  assert.deepEqual(estado.fila, []);
  assert.equal(estado.versoes[ATENDIMENTO], 3);
});

test("painel: na ordem das confirmações, chamada e rechamada anunciam uma vez e o cancelamento tira a senha", () => {
  const depoisDaRechamada = aplicar([
    { tipo: "senhaChamada", evento: chamada(ATENDIMENTO, 1) },
    { tipo: "senhaRechamada", evento: chamada(ATENDIMENTO, 2) },
  ]);
  assert.deepEqual(depoisDaRechamada.chamadas.map((c) => c.senha), ["A15"]);
  assert.equal(depoisDaRechamada.fila.length, 1, "a rechamada de senha que ainda aguarda a vez não cria segundo anúncio");

  const depoisDoCancelamento = reduzirPainel(depoisDaRechamada, {
    tipo: "atendimentoAtualizado",
    evento: atualizado(ATENDIMENTO, 3, "Cancelado"),
  });
  assert.deepEqual(depoisDoCancelamento.chamadas, []);
  assert.deepEqual(depoisDoCancelamento.fila, []);
});

test("painel: a versão menor de um atendimento não desfaz o de outro atendimento", () => {
  const estado = aplicar([
    { tipo: "atendimentoAtualizado", evento: atualizado(ATENDIMENTO, 3, "Cancelado") },
    { tipo: "senhaChamada", evento: chamada(OUTRO, 1) },
    { tipo: "senhaChamada", evento: chamada(ATENDIMENTO, 1) },
  ]);

  assert.deepEqual(estado.chamadas.map((c) => c.atendimentoId), [OUTRO]);
  assert.deepEqual(estado.fila.map((a) => a.atendimentoId), [OUTRO]);
});

test("painel: a cópia da caixa de saída com o mesmo EventoId não reentra depois do cancelamento", () => {
  const original = chamada(ATENDIMENTO, 1);
  const estado = aplicar([
    { tipo: "senhaChamada", evento: original },
    { tipo: "atendimentoAtualizado", evento: atualizado(ATENDIMENTO, 2, "Cancelado") },
    { tipo: "senhaChamada", evento: { ...original } },
  ]);

  assert.deepEqual(estado.chamadas, []);
  assert.deepEqual(estado.fila, []);
});

test("painel: reconsulta com o atendimento já Cancelado depois da entrega invertida não traz a senha de volta", () => {
  const invertido = aplicar([
    { tipo: "atendimentoAtualizado", evento: atualizado(ATENDIMENTO, 3, "Cancelado") },
    { tipo: "senhaRechamada", evento: chamada(ATENDIMENTO, 2) },
  ]);
  const estado = reduzirPainel(invertido, {
    tipo: "estadoCarregado",
    estado: {
      chamadas: [
        { ...chamada(ATENDIMENTO, 3), estado: "Cancelado", chamadoEm: "2026-10-17T13:00:00Z", ultimaRechamadaEm: null },
      ],
    },
  });

  assert.deepEqual(estado.chamadas, []);
  assert.deepEqual(estado.fila, []);
  assert.equal(estado.versoes[ATENDIMENTO], 3);
});

// ─── guichê ────────────────────────────────────────────────────────

const naTela = (versao) => ({ atendimentoId: ATENDIMENTO, versao });

test("guichê: Cancelado v3 antes da rechamada v2, com o posto ainda na v1, relê uma vez e ignora a rechamada", () => {
  const cancelado = decidirAviso({}, naTela(1), { atendimentoId: ATENDIMENTO, versao: 3 }, "Cancelado");
  assert.equal(cancelado.reler, true);
  assert.equal(cancelado.estadoFinal, "Cancelado");
  assert.equal(cancelado.versoes[ATENDIMENTO], 3);

  // The posto in the cache is still v1 until the re-read answers: only the remembered version stops it.
  const rechamada = decidirAviso(cancelado.versoes, naTela(1), { atendimentoId: ATENDIMENTO, versao: 2 }, null);
  assert.equal(rechamada.reler, false);
  assert.equal(rechamada.estadoFinal, null);
  assert.equal(rechamada.versoes[ATENDIMENTO], 3);
});

test("guichê: na ordem das confirmações, rechamada relê sem resultado final e o cancelamento mostra Cancelado", () => {
  const rechamada = decidirAviso({}, naTela(1), { atendimentoId: ATENDIMENTO, versao: 2 }, null);
  assert.equal(rechamada.reler, true);
  assert.equal(rechamada.estadoFinal, null);

  const cancelado = decidirAviso(rechamada.versoes, naTela(1), { atendimentoId: ATENDIMENTO, versao: 3 }, "Cancelado");
  assert.equal(cancelado.reler, true);
  assert.equal(cancelado.estadoFinal, "Cancelado");
});

test("guichê: aviso repetido, versão não maior que a do posto, outro atendimento ou tela sem atendimento não mudam nada", () => {
  const versoes = { [ATENDIMENTO]: 3 };
  const casos = [
    decidirAviso(versoes, naTela(1), { atendimentoId: ATENDIMENTO, versao: 3 }, "Cancelado"),
    decidirAviso({}, naTela(4), { atendimentoId: ATENDIMENTO, versao: 4 }, null),
    decidirAviso({}, naTela(4), { atendimentoId: ATENDIMENTO, versao: 2 }, "Cancelado"),
    decidirAviso({}, naTela(1), { atendimentoId: OUTRO, versao: 9 }, "Cancelado"),
    decidirAviso({}, null, { atendimentoId: ATENDIMENTO, versao: 9 }, "Cancelado"),
    decidirAviso({}, undefined, { atendimentoId: ATENDIMENTO, versao: 9 }, null),
  ];

  for (const decisao of casos) {
    assert.equal(decisao.reler, false);
    assert.equal(decisao.estadoFinal, null);
  }
  assert.equal(casos[0].versoes, versoes, "o mapa de versões ignorado volta o mesmo");
});

test("guichê: estado não final (EmAtendimento) relê sem resultado final", () => {
  const decisao = decidirAviso({}, naTela(1), { atendimentoId: ATENDIMENTO, versao: 2 }, "EmAtendimento");
  assert.equal(decisao.reler, true);
  assert.equal(decisao.estadoFinal, null);
});
