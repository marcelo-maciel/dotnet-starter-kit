/// <reference types="node" />
// Run: node --test src/features/paf/filas/filas-formulario.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  filasPorSetor,
  paresDeContraste,
  problemasDaFila,
  rotulosSemCobertura,
  SEM_SETOR,
  type FilaForm,
} from "./filas-formulario.ts";

function form(mudar: Partial<FilaForm>): FilaForm {
  return {
    nome: "Caixa",
    nomeExibicao: "Caixa",
    semRestricao: true,
    horaInicial: "",
    horaFinal: "",
    aparencia: { fonte: null, corFonte: null, corBotaoSelecionado: null },
    linhas: [{ prioridadeId: "n", prioridade: "Normal", letra: "A", numeroInicial: 1, corBotao: null }],
    ...mudar,
  };
}

test("fila válida sem aparência não tem problema", () => {
  assert.deepEqual(problemasDaFila(form({})), []);
});

test("janela: 22:00-02:00, 10:00-10:00 e só uma hora recusadas; 08:00-17:00 aceita", () => {
  for (const [i, f] of [["22:00", "02:00"], ["10:00", "10:00"], ["08:00", ""], ["", "17:00"]]) {
    assert.equal(problemasDaFila(form({ semRestricao: false, horaInicial: i, horaFinal: f })).length, 1, `${i}-${f}`);
  }
  assert.deepEqual(problemasDaFila(form({ semRestricao: false, horaInicial: "08:00", horaFinal: "17:00" })), []);
});

test("grade: vazia, letra de 6, letra repetida sem diferenciar caixa e prioridade repetida recusadas", () => {
  assert.deepEqual(problemasDaFila(form({ linhas: [] })), ["A fila precisa de ao menos uma prioridade."]);
  const seis = problemasDaFila(form({ linhas: [{ prioridadeId: "n", prioridade: "Normal", letra: "ABCDEF", numeroInicial: 1, corBotao: null }] }));
  assert.match(seis[0], /de 1 a 5 caracteres/);
  const repetidas = problemasDaFila(form({
    linhas: [
      { prioridadeId: "n", prioridade: "Normal", letra: "a", numeroInicial: 1, corBotao: null },
      { prioridadeId: "n", prioridade: "Normal", letra: " A", numeroInicial: 1, corBotao: null },
    ],
  }));
  assert.deepEqual(repetidas, ["A mesma letra de senha aparece duas vezes na grade.", "A mesma prioridade aparece duas vezes na grade."]);
});

test("branco sobre amarelo recusado no botão selecionado e no da prioridade", () => {
  const problemas = problemasDaFila(form({
    aparencia: { fonte: null, corFonte: "#FFFFFF", corBotaoSelecionado: "#FFFF00" },
    linhas: [{ prioridadeId: "n", prioridade: "Normal", letra: "A", numeroInicial: 1, corBotao: "#FFFF00" }],
  }));
  assert.deepEqual(problemas, [
    "Botão fila selecionada: contraste 1,07:1, abaixo de 4,5:1.",
    "Botão Normal: contraste 1,07:1, abaixo de 4,5:1.",
  ]);
});

test("cor de botão sem cor de fonte e cor fora do formato recusadas; campo em branco vira padrão", () => {
  assert.deepEqual(
    problemasDaFila(form({ aparencia: { fonte: null, corFonte: "  ", corBotaoSelecionado: "#0B3D91" } })),
    ["Informe a cor da fonte da fila para usar cor de botão."],
  );
  assert.deepEqual(
    problemasDaFila(form({ aparencia: { fonte: null, corFonte: "#FFF", corBotaoSelecionado: null } })),
    ["Cor no formato #RRGGBB."],
  );
  assert.deepEqual(problemasDaFila(form({ aparencia: { fonte: " ", corFonte: "", corBotaoSelecionado: "" } })), []);
});

test("pares de contraste só com as duas cores válidas", () => {
  assert.deepEqual(paresDeContraste({ fonte: null, corFonte: null, corBotaoSelecionado: "#000000" }, []), []);
  const pares = paresDeContraste(
    { fonte: null, corFonte: "#FFFFFF", corBotaoSelecionado: "#0B3D91" },
    [{ prioridadeId: "n", prioridade: "Normal", letra: "A", numeroInicial: 1, corBotao: "#FFFF0" }],
  );
  assert.equal(pares.length, 1);
  assert.equal(pares[0].botao, "fila selecionada");
  assert.equal(pares[0].suficiente, true);
});

test("setores: ordenados, filas por nome e 'Sem setor' só quando há fila sem setor", () => {
  const setores = [{ id: "s2", nome: "Protocolo" }, { id: "s1", nome: "Cartório" }];
  const grupos = filasPorSetor(setores, [
    { nome: "Notas", setorId: "s1" },
    { nome: "Escrituras", setorId: "s1" },
    { nome: "Caixa", setorId: null },
  ]);
  assert.deepEqual(grupos, [
    { setorId: "s1", nome: "Cartório", filas: ["Escrituras", "Notas"] },
    { setorId: "s2", nome: "Protocolo", filas: [] },
    { setorId: null, nome: SEM_SETOR, filas: ["Caixa"] },
  ]);
  assert.equal(filasPorSetor(setores, [{ nome: "Notas", setorId: "s1" }]).some((g) => g.setorId === null), false);
});

test("aviso de cobertura nomeia prioridade e letra das linhas sem guichê", () => {
  const fila = {
    prioridades: [
      { id: "l1", prioridadeId: "n", letra: "A" },
      { id: "l2", prioridadeId: "p", letra: "AP" },
    ],
    combinacoesSemCobertura: ["l2"],
  };
  assert.deepEqual(rotulosSemCobertura(fila, (id) => (id === "p" ? "Preferencial" : "Normal")), ["Preferencial (AP)"]);
});
