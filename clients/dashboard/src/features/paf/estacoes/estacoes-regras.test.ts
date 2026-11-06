/// <reference types="node" />
// Run: node --test src/features/paf/estacoes/estacoes-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  alternar,
  avisosDeCor,
  corValida,
  EMISSAO_VAZIA,
  formularios,
  GUICHE_VAZIO,
  montarBlocos,
  mover,
  PAINEL_VAZIO,
  papeisDoTipo,
  razaoContraste,
} from "./estacoes-regras.ts";

test("contraste: #777777 sobre branco fica abaixo de 4,5 e #767676 passa", () => {
  assert.ok(razaoContraste("#777777", "#FFFFFF") < 4.5);
  assert.ok(razaoContraste("#767676", "#FFFFFF") >= 4.5);
  assert.equal(Math.round(razaoContraste("#000000", "#FFFFFF") * 100) / 100, 21);
  assert.equal(razaoContraste("#FFFFFF", "#000000"), razaoContraste("#000000", "#FFFFFF"));
});

test("cor: só #RRGGBB opaco", () => {
  assert.ok(corValida("#a1B2c3"));
  for (const cor of ["#GGG", "#GGGGGG", "#FFF", "#767676FF", "767676", "", " #767676"]) {
    assert.equal(corValida(cor), false, cor);
  }
});

test("avisos de cor: parcial, formato e contraste por par", () => {
  assert.deepEqual(avisosDeCor(["Fundo", ""], [["Fonte", ""]]), []);
  assert.deepEqual(avisosDeCor(["Fundo", "#FFFFFF"], [["Fonte", ""]]), ["Informe todas as cores do bloco ou nenhuma."]);
  assert.deepEqual(avisosDeCor(["Fundo", "#FFFFFF"], [["Fonte", "#GGG"]]), ["Fonte: use o formato #RRGGBB."]);
  assert.deepEqual(avisosDeCor(["Fundo", "#FFFFFF"], [["Fonte", "#777777"], ["Descrição", "#000000"]]), [
    "Fonte sobre Fundo: contraste 4.47:1, mínimo 4.5:1.",
  ]);
  assert.deepEqual(avisosDeCor(["Fundo", "#FFFFFF"], [["Fonte", "#767676"]]), []);
});

test("blocos: só os papéis do tipo vão no corpo", () => {
  const guiche = { ...GUICHE_VAZIO, numero: "12", respeitarOrdemFilas: true };
  const blocos = montarBlocos("GuichePainel", { ...EMISSAO_VAZIA, titulo: "ignorado" }, guiche, PAINEL_VAZIO);
  assert.equal(blocos.emissao, null);
  assert.equal(blocos.guiche?.numero, 12);
  assert.equal(blocos.guiche?.respeitarOrdemFilas, true);
  assert.notEqual(blocos.painel, null);
  assert.deepEqual(montarBlocos("Administracao", EMISSAO_VAZIA, guiche, PAINEL_VAZIO), { emissao: null, guiche: null, painel: null });
  assert.deepEqual(papeisDoTipo("EmissaoPainel"), ["Emissao", "Painel"]);
});

test("blocos: número não inteiro vira NaN para o servidor recusar, vazio vira null", () => {
  const blocos = montarBlocos("EmissaoGuiche", { ...EMISSAO_VAZIA, tamanhoFontePx: "24.5", prazoCicloEmissaoSegundos: " " }, { ...GUICHE_VAZIO, numero: "" }, PAINEL_VAZIO);
  assert.ok(Number.isNaN(blocos.emissao?.tamanhoFontePx));
  assert.equal(blocos.emissao?.prazoCicloEmissaoSegundos, null);
  assert.ok(Number.isNaN(blocos.guiche?.numero));
});

test("formulários: ida e volta preserva os blocos gravados", () => {
  const gravados = montarBlocos(
    "EmissaoPainel",
    { ...EMISSAO_VAZIA, titulo: "Bem-vindo", tamanhoBotaoPx: "60", corFundoBotao: "#000000", corFonteBotao: "#FFFFFF", corDescricaoBotao: "#FFFF00" },
    GUICHE_VAZIO,
    { ...PAINEL_VAZIO, tempoExibicaoChamadaSegundos: "15", modo: "Informativo", mostrarDataHora: false },
  );
  const f = formularios(gravados);
  assert.deepEqual(montarBlocos("EmissaoPainel", f.emissao, f.guiche, f.painel), gravados);
  assert.deepEqual(formularios(null).guiche, GUICHE_VAZIO);
});

test("C17: imagem de fundo e cor do texto vão e voltam; sem imagem vai null", () => {
  const imagem = { midiaId: "m1", versaoMidia: 3, luminancias: "0".repeat(63) + "1" };
  const gravados = montarBlocos(
    "EmissaoPainel",
    { ...EMISSAO_VAZIA, corTexto: " #000000 ", imagemFundo: imagem },
    GUICHE_VAZIO,
    { ...PAINEL_VAZIO, imagemFundo: imagem },
  );
  assert.equal(gravados.emissao?.corTexto, "#000000");
  assert.deepEqual(gravados.painel?.imagemFundo, imagem);
  const f = formularios(gravados);
  assert.deepEqual(montarBlocos("EmissaoPainel", f.emissao, f.guiche, f.painel), gravados);
  const sem = montarBlocos("Painel", EMISSAO_VAZIA, GUICHE_VAZIO, PAINEL_VAZIO);
  assert.equal(sem.painel?.imagemFundo, null);
});

test("ordem: alternar acrescenta no fim e mover troca vizinhos sem sair da lista", () => {
  assert.deepEqual(alternar(["a"], "b"), ["a", "b"]);
  assert.deepEqual(alternar(["a", "b"], "a"), ["b"]);
  assert.deepEqual(mover(["a", "b", "c"], "c", -1), ["a", "c", "b"]);
  assert.deepEqual(mover(["a", "b"], "a", -1), ["a", "b"]);
  assert.deepEqual(mover(["a", "b"], "b", 1), ["a", "b"]);
  assert.deepEqual(mover(["a"], "x", 1), ["a"]);
});
