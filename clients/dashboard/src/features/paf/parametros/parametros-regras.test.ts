/// <reference types="node" />
// Run: node --test src/features/paf/parametros/parametros-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import type { ParametroDto } from "./parametros-api.ts";
import { agruparPorGrupo, escolhasDoCampo, rotuloDoParametro, seloDaOrigem, valorExibido } from "./parametros-regras.ts";

function item(chave: string, grupo: string, tipo: ParametroDto["tipo"], opcoes: string[]): ParametroDto {
  return { chave, grupo, tipo, valor: "", origem: "Fabrica", padrao: "", valorEmpresa: null, valorUnidade: null, opcoes };
}

test("selo diz o que é herdado e o que foi sobrescrito", () => {
  assert.deepEqual(seloDaOrigem("Unidade"), { texto: "Sobrescrito nesta unidade", sobrescrito: true });
  assert.deepEqual(seloDaOrigem("Empresa"), { texto: "Herdado da empresa", sobrescrito: false });
  assert.deepEqual(seloDaOrigem("Fabrica"), { texto: "Padrão do sistema", sobrescrito: false });
});

test("tecla oferece só F1 a F12, booleano Sim e Não, opção com rótulo", () => {
  const teclas = escolhasDoCampo(item("teclaEncerrar", "Teclas", "Tecla", []));
  assert.equal(teclas.length, 12);
  assert.deepEqual(teclas[0], ["F1", "F1"]);
  assert.deepEqual(teclas[11], ["F12", "F12"]);
  assert.deepEqual(escolhasDoCampo(item("imprimirQrFicha", "Fichas", "Booleano", [])), [
    ["true", "Sim"],
    ["false", "Não"],
  ]);
  assert.deepEqual(escolhasDoCampo(item("ordenacaoFilas", "Ordenação", "Opcao", ["ordem", "nome"])).map(([v]) => v), ["ordem", "nome"]);
  assert.deepEqual(escolhasDoCampo(item("tituloFichas", "Fichas", "Texto", [])), []);
});

test("valor exibido usa o rótulo da escolha e marca o texto vazio", () => {
  assert.equal(valorExibido(item("imprimirQrFicha", "Fichas", "Booleano", []), "false"), "Não");
  assert.equal(valorExibido(item("ordenacaoFilas", "O", "Opcao", ["ordem", "nome"]), "nome"), "Nome da fila");
  assert.equal(valorExibido(item("tituloFichas", "Fichas", "Texto", []), ""), "(vazio)");
  assert.equal(valorExibido(item("quantidadeChamadasPainel", "Painel", "Inteiro", []), "10"), "10");
});

test("chave desconhecida aparece pelo nome, e os grupos mantêm a ordem do catálogo", () => {
  assert.equal(rotuloDoParametro("chaveNova"), "chaveNova");
  assert.equal(rotuloDoParametro("teclaIniciar"), "Iniciar atendimento");
  const grupos = agruparPorGrupo([
    item("a", "Geral", "Booleano", []),
    item("b", "Fichas", "Texto", []),
    item("c", "Geral", "Booleano", []),
  ]);
  assert.deepEqual(grupos.map(([g, itens]) => [g, itens.map((i) => i.chave)]), [
    ["Geral", ["a", "c"]],
    ["Fichas", ["b"]],
  ]);
});
