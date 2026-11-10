/// <reference types="node" />
// Run: node --test src/features/paf/parametros/parametros-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import type { ParametroDto } from "./parametros-api.ts";
import {
  agruparPorGrupo,
  escolhasDeImagem,
  logotipoGravavel,
  escolhasDoCampo,
  logotipoExibido,
  rotuloDoParametro,
  seloDaOrigem,
  valorExibido,
} from "./parametros-regras.ts";

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

// --- C19: logotipo ---

const midia = (id: string, descricao: string, tipo: string, ativa: boolean) => ({ id, descricao, tipo, ativa });

test("logotipo: padrão do sistema primeiro, depois só as imagens ativas por descrição", () => {
  const midias = [
    midia("v1", "Abertura", "Video", true),
    midia("i2", "Marca Sul", "Imagem", true),
    midia("i3", "antiga", "Imagem", false),
    midia("i1", "marca centro", "Imagem", true),
  ];
  assert.deepEqual(escolhasDeImagem(midias, ""), [
    ["", "Logotipo padrão do sistema"],
    ["i1", "marca centro"],
    ["i2", "Marca Sul"],
  ]);
});

test("logotipo: valor atual fora da lista (empresa em outra unidade, inativa) continua selecionável", () => {
  const escolhas = escolhasDeImagem([midia("i1", "Marca", "Imagem", true)], "de-outra-unidade");
  assert.deepEqual(escolhas.at(-1), ["de-outra-unidade", "Imagem personalizada"]);
  assert.equal(escolhasDeImagem([], "").length, 1);
});

test("logotipo: só o padrão ou uma imagem ativa desta unidade pode ser salvo (R4)", () => {
  const midias = [midia("i1", "Marca", "Imagem", true), midia("i2", "Antiga", "Imagem", false), midia("v1", "Abertura", "Video", true)];
  assert.equal(logotipoGravavel(midias, ""), true);
  assert.equal(logotipoGravavel(midias, "i1"), true);
  assert.equal(logotipoGravavel(midias, "de-outra-unidade"), false, "herdado da empresa, imagem de outra unidade");
  assert.equal(logotipoGravavel(midias, "i2"), false, "inativa");
  assert.equal(logotipoGravavel(midias, "v1"), false, "vídeo");
  assert.equal(logotipoGravavel([], "i1"), false, "sem a lista de mídias, só o padrão");
});

test("logotipo: exibição nunca mostra o id cru", () => {
  const midias = [midia("i1", "Marca", "Imagem", true)];
  assert.equal(logotipoExibido(midias, ""), "Logotipo padrão do sistema");
  assert.equal(logotipoExibido(midias, "i1"), "Imagem personalizada: Marca");
  assert.equal(logotipoExibido([], "0199f2a4-7b1c-7d3e-8a5b-1234567890ab"), "Imagem personalizada");
  const item = { tipo: "Imagem" as const, opcoes: [] };
  assert.deepEqual(escolhasDoCampo(item), [["", "Logotipo padrão do sistema"]]);
  assert.equal(valorExibido(item, "0199f2a4-7b1c-7d3e-8a5b-1234567890ab"), "Imagem personalizada");
  assert.equal(rotuloDoParametro("logotipo"), "Logotipo do totem, do painel e da ficha impressa");
});
