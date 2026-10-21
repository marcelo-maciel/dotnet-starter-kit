/// <reference types="node" />
// Run: node --test src/features/paf/programacoes/programacoes-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  alternarDia,
  calcular,
  consultaDaPesquisa,
  duracaoDaMidia,
  duracaoLegivel,
  FILTROS_VAZIOS,
  MENSAGEM_SEM_LISTA_DE_MIDIAS,
  horaDosSegundos,
  periodoInvertido,
  rotuloDosDias,
  segundosDaHora,
  TODOS_OS_DIAS,
  validarProgramacao,
  type FormProgramacao,
} from "./programacoes-regras.ts";

const FORM: FormProgramacao = {
  midiaId: "m1",
  nome: "Manhã",
  dataInicial: "2026-10-19",
  dataFinal: "2026-10-31",
  horaInicial: "10:00",
  repeticoes: "3",
  dias: TODOS_OS_DIAS,
};

test("hora e segundos: ida, volta e meia-noite como 24:00:00", () => {
  assert.equal(segundosDaHora("10:00"), 36_000);
  assert.equal(segundosDaHora("10:00:30"), 36_030);
  assert.equal(segundosDaHora("23:59:59"), 86_399);
  for (const ruim of ["24:00", "10:60", "1:00", "", "10h", "10:00:60"]) assert.equal(segundosDaHora(ruim), null, ruim);
  assert.equal(horaDosSegundos(36_030), "10:00:30");
  assert.equal(horaDosSegundos(0), "00:00:00");
  assert.equal(horaDosSegundos(86_400), "24:00:00");
});

test("cálculo ao vivo: total = duração × repetições, fim = início + total", () => {
  assert.deepEqual(calcular(600, "10:00", "3"), {
    tempoMidiaSegundos: 600,
    tempoTotalSegundos: 1800,
    horaFinalSegundos: 37_800,
    passaDaMeiaNoite: false,
  });
  assert.equal(calcular(null, "10:00", "3"), null, "sem mídia");
  assert.equal(calcular(600, "", "3"), null, "sem hora");
  assert.equal(calcular(600, "10:00", "0"), null, "repetição zero");
  assert.equal(calcular(600, "10:00", "1.5"), null);
});

test("D13: termina exatamente à meia-noite passa; um segundo depois não", () => {
  const meiaNoite = calcular(3600, "23:00", "1");
  assert.equal(meiaNoite?.horaFinalSegundos, 86_400);
  assert.equal(meiaNoite?.passaDaMeiaNoite, false);
  assert.equal(calcular(3601, "23:00", "1")?.passaDaMeiaNoite, true);
  assert.equal(validarProgramacao({ ...FORM, horaInicial: "23:00", repeticoes: "1" }, 3600).ok, true);
  const recusada = validarProgramacao({ ...FORM, horaInicial: "23:00", repeticoes: "1" }, 3601);
  assert.ok(!recusada.ok && /meia-noite/.test(recusada.mensagem));
});

test("validação do formulário antes do envio", () => {
  const ok = validarProgramacao({ ...FORM, nome: "  Manhã  " }, 600);
  assert.deepEqual(ok, {
    ok: true,
    corpo: { midiaId: "m1", nome: "Manhã", dataInicial: "2026-10-19", dataFinal: "2026-10-31", horaInicial: "10:00:00", repeticoes: 3, dias: 127 },
  });
  assert.equal(validarProgramacao({ ...FORM, nome: " " }, 600).ok, false);
  assert.equal(validarProgramacao({ ...FORM, nome: "x".repeat(121) }, 600).ok, false);
  assert.equal(validarProgramacao({ ...FORM, midiaId: "" }, null).ok, false);
  assert.equal(validarProgramacao(FORM, null).ok, false, "mídia fora da lista (inativa ou sem permissão)");
  assert.equal(validarProgramacao({ ...FORM, dataFinal: "2026-10-18" }, 600).ok, false);
  assert.equal(validarProgramacao({ ...FORM, dataFinal: "2026-10-19" }, 600).ok, true, "mesmo dia");
  assert.equal(validarProgramacao({ ...FORM, dataInicial: "" }, 600).ok, false);
  assert.equal(validarProgramacao({ ...FORM, dias: 0 }, 600).ok, false);
  assert.equal(validarProgramacao({ ...FORM, repeticoes: "0" }, 600).ok, false);
  assert.equal(validarProgramacao({ ...FORM, repeticoes: "86401" }, 1).ok, false);
  assert.equal(validarProgramacao({ ...FORM, horaInicial: "25:00" }, 600).ok, false);
});

test("dias da semana: máscara na ordem do DayOfWeek", () => {
  assert.equal(rotuloDosDias(TODOS_OS_DIAS), "Todos os dias");
  assert.equal(rotuloDosDias(2 | 8), "Seg, Qua");
  assert.equal(rotuloDosDias(1 | 64), "Dom, Sáb");
  assert.equal(rotuloDosDias(0), "Nenhum dia");
  assert.equal(alternarDia(TODOS_OS_DIAS, 1), 126);
  assert.equal(alternarDia(126, 1), TODOS_OS_DIAS);
});

test("pesquisa: só os filtros preenchidos vão na consulta", () => {
  assert.equal(consultaDaPesquisa(FILTROS_VAZIOS, 1, 20), "pageNumber=1&pageSize=20");
  const q = new URLSearchParams(consultaDaPesquisa({ nome: " 50% off ", dias: 2, hora: "10:30", de: "2026-10-01", ate: "2026-10-31" }, 2, 20));
  assert.equal(q.get("nome"), "50% off");
  assert.equal(q.get("dias"), "2");
  assert.equal(q.get("hora"), "10:30:00");
  assert.equal(q.get("de"), "2026-10-01");
  assert.equal(q.get("ate"), "2026-10-31");
  assert.equal(q.get("pageNumber"), "2");
  const incompleta = new URLSearchParams(consultaDaPesquisa({ ...FILTROS_VAZIOS, hora: "1", de: "2026-1" }, 1, 20));
  assert.equal(incompleta.has("hora"), false);
  assert.equal(incompleta.has("de"), false);
  assert.equal(incompleta.has("dias"), false);
});

test("período invertido bloqueado na tela", () => {
  assert.equal(periodoInvertido({ ...FILTROS_VAZIOS, de: "2026-10-02", ate: "2026-10-01" }), true);
  assert.equal(periodoInvertido({ ...FILTROS_VAZIOS, de: "2026-10-01", ate: "2026-10-01" }), false);
  assert.equal(periodoInvertido({ ...FILTROS_VAZIOS, de: "2026-10-02" }), false);
});

test("duração legível", () => {
  assert.equal(duracaoLegivel(45), "45 s");
  assert.equal(duracaoLegivel(1800), "30 min 00 s");
  assert.equal(duracaoLegivel(3723), "1 h 02 min 03 s");
});

test("B1: sem Midias.View, a edição mantém a mídia atual com a duração da própria programação", () => {
  const lista = [{ id: "m1", duracaoSegundos: 600 }];
  const atual = { id: "m2", descricao: "Vídeo da manhã", duracaoSegundos: 90 };
  assert.equal(duracaoDaMidia(lista, null, "m1"), 600, "da lista");
  assert.equal(duracaoDaMidia(lista, atual, "m2"), 90, "fora da lista, a atual");
  assert.equal(duracaoDaMidia(null, atual, "m2"), 90, "sem lista, a atual");
  assert.equal(duracaoDaMidia(null, atual, "m3"), null, "sem lista, outra mídia");
  assert.equal(duracaoDaMidia(null, null, "m2"), null, "nova programação sem lista");
  assert.equal(validarProgramacao({ ...FORM, midiaId: "m2" }, duracaoDaMidia(null, atual, "m2")).ok, true);
  assert.deepEqual(validarProgramacao(FORM, null), { ok: false, mensagem: MENSAGEM_SEM_LISTA_DE_MIDIAS });
  assert.deepEqual(validarProgramacao({ ...FORM, midiaId: "" }, null), { ok: false, mensagem: "Escolha a mídia." });
});
