/// <reference types="node" />
// Run: node --test src/features/paf/totem/ciclo-emissao.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  EmissaoTotem,
  esperaDoRetryAfter,
  PAUSA_REENVIO_MS,
  type RespostaEmissao,
} from "./ciclo-emissao.ts";

type Ficha = { senha: string };
type Envio = { escolha: string; chave: string; em: number };

const REDE_CAIU = new TypeError("Failed to fetch");
const FICHA: Ficha = { senha: "N7" };

/** Fake clock: `esperar` advances it and records the wait, so no test sleeps. */
function cenario(respostas: RespostaEmissao<Ficha>[]) {
  let relogio = 0;
  let chaves = 0;
  const envios: Envio[] = [];
  const esperas: number[] = [];
  const entregas: Ficha[] = [];
  const passos = {
    enviar: async (escolha: string, chave: string) => {
      envios.push({ escolha, chave, em: relogio });
      const proxima = respostas.shift();
      if (proxima === undefined) throw new Error("envio além do roteiro");
      return proxima;
    },
    entregar: async (ficha: Ficha) => {
      entregas.push(ficha);
    },
    esperar: async (ms: number) => {
      esperas.push(ms);
      relogio += ms;
    },
    agora: () => relogio,
    novaChave: () => `k${++chaves}`,
  };
  return { emissao: new EmissaoTotem<string, Ficha>(passos), envios, esperas, entregas, passos };
}

const sucesso = (repetida: boolean): RespostaEmissao<Ficha> => ({ tipo: "sucesso", ficha: FICHA, repetida });
const rede: RespostaEmissao<Ficha> = { tipo: "transitoria", erro: REDE_CAIU };

test("409 em curso com Retry-After 1 aguarda 1 s e reenvia com a mesma chave", async () => {
  const c = cenario([{ tipo: "emCurso", esperaMs: esperaDoRetryAfter("1") }, sucesso(false)]);

  const fim = await c.emissao.tocar("caixa:normal", 75_000);

  assert.equal(fim.tipo, "entregue");
  assert.deepEqual(c.esperas, [1_000]);
  assert.deepEqual(
    c.envios.map((e) => [e.chave, e.em]),
    [
      ["k1", 0],
      ["k1", 1_000],
    ],
  );
  assert.equal(c.entregas.length, 1);
});

test("rede caída três vezes e depois sucesso imprime uma vez, sempre com a mesma chave", async () => {
  const c = cenario([rede, rede, rede, sucesso(false)]);

  const fim = await c.emissao.tocar("caixa:normal", 75_000);

  assert.deepEqual(fim, { tipo: "entregue", ficha: FICHA, repetida: false });
  assert.deepEqual(new Set(c.envios.map((e) => e.chave)), new Set(["k1"]));
  assert.equal(c.envios.length, 4);
  assert.deepEqual(c.esperas, [PAUSA_REENVIO_MS, PAUSA_REENVIO_MS, PAUSA_REENVIO_MS]);
  assert.deepEqual(c.entregas, [FICHA]);
});

test("resposta repetida (Idempotency-Replayed) num ciclo que não imprimiu é impressa", async () => {
  // The first POST stored the ficha but its answer was lost: the resend gets the replay, the only paper.
  const c = cenario([rede, sucesso(true)]);

  const fim = await c.emissao.tocar("caixa:normal", 75_000);

  assert.deepEqual(fim, { tipo: "entregue", ficha: FICHA, repetida: true });
  assert.deepEqual(c.entregas, [FICHA]);
});

test("toque repetido durante a impressão não envia, não gera chave e não imprime de novo", async () => {
  const c = cenario([sucesso(false)]);
  let liberarImpressao = () => {};
  c.passos.entregar = async (ficha: Ficha) => {
    c.entregas.push(ficha);
    await new Promise<void>((resolve) => {
      liberarImpressao = resolve;
    });
  };

  const primeiro = c.emissao.tocar("caixa:normal", 75_000);
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(c.emissao.ocupada(), true);
  const repetido = await c.emissao.tocar("caixa:normal", 75_000);
  const outroBotao = await c.emissao.tocar("caixa:preferencial", 75_000);
  liberarImpressao();
  const fim = await primeiro;

  assert.deepEqual(repetido, { tipo: "ocupado" });
  assert.deepEqual(outroBotao, { tipo: "ocupado" });
  assert.equal(fim.tipo, "entregue");
  assert.equal(c.envios.length, 1);
  assert.equal(c.entregas.length, 1);
  assert.equal(c.emissao.ocupada(), false);
});

test("prazo vencido volta ao início e o próximo toque abre ciclo com chave nova", async () => {
  const respostas: RespostaEmissao<Ficha>[] = Array.from({ length: 5 }, () => rede);
  const c = cenario([...respostas, sucesso(false)]);

  const fim = await c.emissao.tocar("caixa:normal", 3_500);

  assert.deepEqual(fim, { tipo: "prazoVencido", erro: REDE_CAIU });
  // 0, 1000, 2000, 3000 sent; the fourth failure has 500 ms left, waits it and gives up.
  assert.deepEqual(
    c.envios.map((e) => e.em),
    [0, 1_000, 2_000, 3_000],
  );
  assert.deepEqual(c.esperas, [1_000, 1_000, 1_000, 500]);
  assert.equal(c.entregas.length, 0);
  assert.equal(c.emissao.ocupada(), false);

  const depois = await c.emissao.tocar("caixa:normal", 3_500);
  assert.equal(depois.tipo, "entregue");
  assert.deepEqual(
    c.envios.slice(4).map((e) => e.chave),
    ["k2", "k2"],
    "o ciclo novo não herda a chave do que venceu",
  );
});

test("o prazo do ciclo é o teto da espera do 409: Retry-After maior que o restante não passa do prazo", async () => {
  const c = cenario([{ tipo: "emCurso", esperaMs: esperaDoRetryAfter("120") }]);

  const fim = await c.emissao.tocar("caixa:normal", 2_000);

  assert.deepEqual(fim, { tipo: "prazoVencido", erro: null });
  assert.deepEqual(c.esperas, [2_000]);
  assert.equal(c.envios.length, 1);
});

test("toque em outro botão depois de um ciclo abre ciclo com chave nova", async () => {
  const c = cenario([sucesso(false), sucesso(false)]);

  await c.emissao.tocar("caixa:normal", 75_000);
  await c.emissao.tocar("protocolo:preferencial", 75_000);

  assert.deepEqual(
    c.envios.map((e) => [e.escolha, e.chave]),
    [
      ["caixa:normal", "k1"],
      ["protocolo:preferencial", "k2"],
    ],
  );
});

test("recusa definitiva (409 de cobertura, 400, credencial) encerra sem reenviar nem imprimir", async () => {
  const recusa = new Error("Fila sem guichê");
  const c = cenario([{ tipo: "definitiva", erro: recusa }]);

  const fim = await c.emissao.tocar("caixa:normal", 75_000);

  assert.deepEqual(fim, { tipo: "recusada", erro: recusa });
  assert.equal(c.envios.length, 1);
  assert.deepEqual(c.esperas, []);
  assert.equal(c.entregas.length, 0);
});

test("sucesso que chega depois do prazo ainda é impresso: a ficha já existe", async () => {
  const c = cenario([sucesso(false)]);
  const enviar = c.passos.enviar;
  c.passos.enviar = async (escolha: string, chave: string) => {
    const resposta = await enviar(escolha, chave);
    await c.passos.esperar(20_000);
    return resposta;
  };

  const fim = await c.emissao.tocar("caixa:normal", 5_000);

  assert.equal(fim.tipo, "entregue");
  assert.equal(c.entregas.length, 1);
});

test("tela encerrada no meio do ciclo não reenvia nem imprime", async () => {
  const c = cenario([rede, sucesso(false)]);
  c.passos.esperar = async (ms: number) => {
    c.esperas.push(ms);
    c.emissao.encerrar();
  };

  const fim = await c.emissao.tocar("caixa:normal", 75_000);

  assert.deepEqual(fim, { tipo: "abandonado" });
  assert.equal(c.envios.length, 1);
  assert.equal(c.entregas.length, 0);
});

test("Retry-After: segundos inteiros com piso de 1 s; data HTTP, zero, vazio, negativo ou lixo caem em 1 s", () => {
  assert.equal(esperaDoRetryAfter("1"), 1_000);
  assert.equal(esperaDoRetryAfter(" 3 "), 3_000);
  assert.equal(esperaDoRetryAfter("0"), 1_000);
  assert.equal(esperaDoRetryAfter(null), 1_000);
  assert.equal(esperaDoRetryAfter(""), 1_000);
  assert.equal(esperaDoRetryAfter("-2"), 1_000);
  assert.equal(esperaDoRetryAfter("1.5"), 1_000);
  assert.equal(esperaDoRetryAfter("Wed, 21 Oct 2026 07:28:00 GMT"), 1_000);
});
