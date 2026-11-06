// Panel re-query vs pending announcements (M00, RF21, arquitetura seção 7).
// Runner per plan decision D21, no dependency: from clients/dashboard,
//   node --test tests/unit/paf/painel-reconsulta.test.ts
// Lives outside src so `tsc -b` (types: vite/client) does not compile the node:test import.
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  ESTADO_INICIAL_PAINEL,
  reduzirPainel,
  type ChamadaPainelDto,
  type EstadoPainel,
  type SenhaChamadaEvento,
} from "../../../src/features/paf/painel/painel-estado.ts";

const UNIDADE = "u-1";

function evento(n: number, versao: number, eventoId: string): SenhaChamadaEvento {
  return {
    eventoId,
    unidadeId: UNIDADE,
    atendimentoId: `at-${n}`,
    fichaId: `fi-${n}`,
    versao,
    senha: `A${n}`,
    estacaoId: "es-1",
    nomeEstacao: "Guichê 1",
  };
}

function linha(n: number, versao: number, estado: string): ChamadaPainelDto {
  return {
    atendimentoId: `at-${n}`,
    fichaId: `fi-${n}`,
    versao,
    senha: `A${n}`,
    estado,
    estacaoId: "es-1",
    nomeEstacao: "Guichê 1",
    chamadoEm: "2026-10-17T13:00:00Z",
    ultimaRechamadaEm: null,
  };
}

/** Panel started, then calls 1..12 arrive (version 2 each) faster than the highlight: all 12 wait in the queue. */
function dozePendentes(): EstadoPainel {
  let estado = reduzirPainel(ESTADO_INICIAL_PAINEL, { tipo: "anunciosIniciados" });
  for (let n = 1; n <= 12; n++) {
    estado = reduzirPainel(estado, { tipo: "senhaChamada", evento: evento(n, 2, `ev-${n}`) });
  }
  return estado;
}

/** The server's re-query: the 10 most recent calls (12 down to 3), latest first, as the handler orders them. */
function reconsultaDasDezMaisRecentes(estadoDe: (n: number) => [number, string]): ChamadaPainelDto[] {
  const linhas: ChamadaPainelDto[] = [];
  for (let n = 12; n >= 3; n--) {
    const [versao, estado] = estadoDe(n);
    linhas.push(linha(n, versao, estado));
  }
  return linhas;
}

const senhasNaFila = (estado: EstadoPainel) => estado.fila.map((a) => a.senha);
const DOZE_EM_ORDEM = Array.from({ length: 12 }, (_, i) => `A${i + 1}`);

test("re-query with the 10 most recent keeps all 12 pending announcements, in arrival order", () => {
  const antes = dozePendentes();
  assert.deepEqual(senhasNaFila(antes), DOZE_EM_ORDEM);

  const depois = reduzirPainel(antes, {
    tipo: "estadoCarregado",
    estado: { chamadas: reconsultaDasDezMaisRecentes(() => [2, "Chamado"]) },
  });

  assert.deepEqual(senhasNaFila(depois), DOZE_EM_ORDEM);
  assert.equal(depois.chamadas.length, 10);
  assert.equal(depois.chamadas[0].senha, "A12");
});

test("empty re-query keeps every pending announcement", () => {
  const depois = reduzirPainel(dozePendentes(), { tipo: "estadoCarregado", estado: { chamadas: [] } });
  assert.deepEqual(senhasNaFila(depois), DOZE_EM_ORDEM);
});

test("an old pending call the re-query shows Cancelado leaves the queue; the rest keep their order", () => {
  const antes = dozePendentes();
  const chamadas = [...reconsultaDasDezMaisRecentes(() => [2, "Chamado"]), linha(2, 3, "Cancelado")];

  const depois = reduzirPainel(antes, { tipo: "estadoCarregado", estado: { chamadas } });

  assert.deepEqual(
    senhasNaFila(depois),
    DOZE_EM_ORDEM.filter((s) => s !== "A2"),
  );
  assert.ok(!depois.chamadas.some((c) => c.senha === "A2"));
});

test("a state other than Chamado with a higher version removes it; with the same version it does not", () => {
  const antes = dozePendentes();
  const depois = reduzirPainel(antes, {
    tipo: "estadoCarregado",
    estado: {
      chamadas: reconsultaDasDezMaisRecentes((n) => {
        if (n === 5) return [3, "EmAtendimento"];
        if (n === 6) return [2, "EmAtendimento"];
        return [2, "Chamado"];
      }),
    },
  });

  assert.deepEqual(
    senhasNaFila(depois),
    DOZE_EM_ORDEM.filter((s) => s !== "A5"),
  );
});

test("a recall made while offline (Chamado, higher version) keeps the single pending item", () => {
  const antes = dozePendentes();
  const depois = reduzirPainel(antes, {
    tipo: "estadoCarregado",
    estado: { chamadas: reconsultaDasDezMaisRecentes((n) => (n === 7 ? [3, "Chamado"] : [2, "Chamado"])) },
  });

  assert.deepEqual(senhasNaFila(depois), DOZE_EM_ORDEM);
  assert.equal(depois.fila.filter((a) => a.atendimentoId === "at-7").length, 1);
});

test("after the re-query, the outbox copy of an applied SenhaChamada (same EventoId) does not re-enter", () => {
  let estado = reduzirPainel(dozePendentes(), {
    tipo: "estadoCarregado",
    estado: { chamadas: reconsultaDasDezMaisRecentes(() => [2, "Chamado"]) },
  });
  // Highlight A1 and finish it, so a duplicate would no longer be blocked by "already in the queue".
  estado = reduzirPainel(estado, { tipo: "destaqueIniciado" });
  estado = reduzirPainel(estado, { tipo: "destaqueEncerrado" });
  assert.equal(estado.fila.length, 11);

  const depois = reduzirPainel(estado, { tipo: "senhaChamada", evento: evento(1, 2, "ev-1") });

  assert.equal(depois.fila.length, 11);
  assert.ok(!depois.fila.some((a) => a.senha === "A1"));
});

test("after the re-query, a SenhaChamada with version at or below the re-query's is not announced", () => {
  // Page reloaded: nothing pending, the re-query brings A20 at version 2 (it was called before the reload).
  let estado = reduzirPainel(ESTADO_INICIAL_PAINEL, { tipo: "anunciosIniciados" });
  estado = reduzirPainel(estado, { tipo: "estadoCarregado", estado: { chamadas: [linha(20, 2, "Chamado")] } });

  const igual = reduzirPainel(estado, { tipo: "senhaChamada", evento: evento(20, 2, "ev-20-outbox") });
  const menor = reduzirPainel(estado, { tipo: "senhaChamada", evento: evento(20, 1, "ev-20-velho") });
  const maior = reduzirPainel(estado, { tipo: "senhaRechamada", evento: evento(20, 3, "ev-20-rechamada") });

  assert.equal(igual.fila.length, 0);
  assert.equal(menor.fila.length, 0);
  assert.deepEqual(senhasNaFila(maior), ["A20"]);
  assert.equal(estado.chamadas[0].senha, "A20");
});
