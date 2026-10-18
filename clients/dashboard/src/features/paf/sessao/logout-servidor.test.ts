/// <reference types="node" />
// Run: node --test src/features/paf/sessao/logout-servidor.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { iniciarLogoutServidor, type PedidoLogoutServidor } from "./logout-servidor.ts";

const API = "https://api.exemplo";
const LOGOUT = `${API}/api/v1/paf/sessao/logout`;
const REFRESH = `${API}/api/v1/identity/token/refresh`;

function jwt(claims: Record<string, unknown>): string {
  const parte = (valor: unknown) => Buffer.from(JSON.stringify(valor)).toString("base64url");
  return `${parte({ alg: "HS256", typ: "JWT" })}.${parte(claims)}.assinatura`;
}

const agoraSeg = () => Math.floor(Date.now() / 1000);
const VIVO = jwt({ sub: "u1", tenant: "t1", sid: "s1", exp: agoraSeg() + 600 });
const VENCIDO = jwt({ sub: "u1", tenant: "t1", sid: "s1", exp: agoraSeg() - 600 });
const RENOVADO = jwt({ sub: "u1", tenant: "t1", sid: "s1", exp: agoraSeg() + 2700 });

type Chamada = { url: string; init: RequestInit };

function buscarCom(respostas: Record<string, () => Promise<Response>>): { buscar: typeof fetch; chamadas: Chamada[] } {
  const chamadas: Chamada[] = [];
  const buscar = (async (url: string | URL | Request, init?: RequestInit) => {
    const texto = String(url);
    chamadas.push({ url: texto, init: init ?? {} });
    const resposta = respostas[texto];
    if (!resposta) throw new Error(`chamada inesperada: ${texto}`);
    return resposta();
  }) as typeof fetch;
  return { buscar, chamadas };
}

function pedido(accessToken: string | null, refreshToken: string | null, buscar: typeof fetch, prazoMs: number): PedidoLogoutServidor {
  return { apiBase: API, tenant: "t1", accessToken, refreshToken, prazoMs, buscar };
}

const PRAZO_TESTE_MS = 1_000;

const semConteudo = () => Promise.resolve(new Response(null, { status: 204 }));

// Never answers until the signal aborts, like a server that accepted the connection and hung.
function pendurado(chamadas: Chamada[]): () => Promise<Response> {
  return () =>
    new Promise<Response>((_, rejeitar) => {
      const signal = chamadas[chamadas.length - 1].init.signal;
      signal?.addEventListener("abort", () => rejeitar(signal.reason), { once: true });
    });
}

function cabecalho(chamada: Chamada, nome: string): string | undefined {
  return (chamada.init.headers as Record<string, string>)[nome];
}

test("impersonação não chama o servidor e limpa na hora", () => {
  const { buscar, chamadas } = buscarCom({});
  const token = jwt({ sub: "u2", tenant: "t1", sid: "s1", act_sub: "operador", act_tenant: "root", exp: agoraSeg() + 600 });
  assert.equal(iniciarLogoutServidor(pedido(token, null, buscar, PRAZO_TESTE_MS)), null);
  assert.equal(chamadas.length, 0);
});

test("token sem a claim sid, sem token ou ilegível não chama o servidor", () => {
  const { buscar, chamadas } = buscarCom({});
  assert.equal(iniciarLogoutServidor(pedido(jwt({ sub: "u1", exp: agoraSeg() + 600 }), "r1", buscar, PRAZO_TESTE_MS)), null);
  assert.equal(iniciarLogoutServidor(pedido(jwt({ sub: "u1", sid: "", exp: agoraSeg() + 600 }), "r1", buscar, PRAZO_TESTE_MS)), null);
  assert.equal(iniciarLogoutServidor(pedido(null, "r1", buscar, PRAZO_TESTE_MS)), null);
  assert.equal(iniciarLogoutServidor(pedido("não.é.jwt", "r1", buscar, PRAZO_TESTE_MS)), null);
  assert.equal(chamadas.length, 0);
});

test("token vivo: um POST ao logout do PAF com o token e o tenant, sem renovação", async () => {
  const { buscar, chamadas } = buscarCom({ [LOGOUT]: semConteudo });
  assert.equal(await iniciarLogoutServidor(pedido(VIVO, "r1", buscar, PRAZO_TESTE_MS)), "encerrada");
  assert.equal(chamadas.length, 1);
  assert.equal(chamadas[0].url, LOGOUT);
  assert.equal(chamadas[0].init.method, "POST");
  assert.equal(cabecalho(chamadas[0], "Authorization"), `Bearer ${VIVO}`);
  assert.equal(cabecalho(chamadas[0], "tenant"), "t1");
});

test("token vencido (logout por inatividade): renova em memória e chama o logout com o token novo", async () => {
  const { buscar, chamadas } = buscarCom({
    [REFRESH]: () => Promise.resolve(Response.json({ token: RENOVADO, refreshToken: "r2" })),
    [LOGOUT]: semConteudo,
  });
  assert.equal(await iniciarLogoutServidor(pedido(VENCIDO, "r1", buscar, PRAZO_TESTE_MS)), "encerrada");
  assert.deepEqual(chamadas.map((c) => c.url), [REFRESH, LOGOUT]);
  assert.deepEqual(JSON.parse(String(chamadas[0].init.body)), { token: VENCIDO, refreshToken: "r1" });
  assert.equal(cabecalho(chamadas[0], "tenant"), "t1");
  assert.equal(cabecalho(chamadas[1], "Authorization"), `Bearer ${RENOVADO}`);
});

test("token vencido sem refresh token: falha sem chamar nada", async () => {
  const { buscar, chamadas } = buscarCom({});
  assert.equal(await iniciarLogoutServidor(pedido(VENCIDO, null, buscar, PRAZO_TESTE_MS)), "falhou");
  assert.equal(chamadas.length, 0);
});

test("renovação recusada (sessão já revogada): falha e não chama o logout", async () => {
  const { buscar, chamadas } = buscarCom({ [REFRESH]: () => Promise.resolve(new Response(null, { status: 401 })) });
  assert.equal(await iniciarLogoutServidor(pedido(VENCIDO, "r1", buscar, PRAZO_TESTE_MS)), "falhou");
  assert.deepEqual(chamadas.map((c) => c.url), [REFRESH]);
});

test("renovação sem token no corpo: falha e não chama o logout", async () => {
  const { buscar, chamadas } = buscarCom({ [REFRESH]: () => Promise.resolve(Response.json({ refreshToken: "r2" })) });
  assert.equal(await iniciarLogoutServidor(pedido(VENCIDO, "r1", buscar, PRAZO_TESTE_MS)), "falhou");
  assert.deepEqual(chamadas.map((c) => c.url), [REFRESH]);
});

test("logout recusado pelo servidor (403 de tenant desativado, 500): resolve como falha, nunca rejeita", async () => {
  for (const status of [401, 403, 500]) {
    const { buscar } = buscarCom({ [LOGOUT]: () => Promise.resolve(new Response(null, { status })) });
    assert.equal(await iniciarLogoutServidor(pedido(VIVO, "r1", buscar, PRAZO_TESTE_MS)), "falhou");
  }
});

test("servidor fora (conexão recusada): resolve como falha, nunca rejeita", async () => {
  const { buscar } = buscarCom({ [LOGOUT]: () => Promise.reject(new TypeError("Failed to fetch")) });
  assert.equal(await iniciarLogoutServidor(pedido(VIVO, "r1", buscar, PRAZO_TESTE_MS)), "falhou");
});

test("servidor pendurado: o prazo aborta a chamada e o logout local segue dentro dele", async () => {
  const respostas: Record<string, () => Promise<Response>> = {};
  const { buscar, chamadas } = buscarCom(respostas);
  respostas[LOGOUT] = pendurado(chamadas);
  const inicio = performance.now();
  assert.equal(await iniciarLogoutServidor(pedido(VIVO, "r1", buscar, 80)), "falhou");
  const decorrido = performance.now() - inicio;
  assert.ok(decorrido >= 70, `terminou antes do prazo: ${decorrido} ms`);
  assert.ok(decorrido < 1_000, `passou do prazo: ${decorrido} ms`);
  assert.equal(chamadas[0].init.signal?.aborted, true);
});

test("renovação pendurada: o mesmo prazo cobre a renovação e o logout nunca sai", async () => {
  const respostas: Record<string, () => Promise<Response>> = {};
  const { buscar, chamadas } = buscarCom(respostas);
  respostas[REFRESH] = pendurado(chamadas);
  const inicio = performance.now();
  assert.equal(await iniciarLogoutServidor(pedido(VENCIDO, "r1", buscar, 80)), "falhou");
  assert.ok(performance.now() - inicio < 1_000);
  assert.deepEqual(chamadas.map((c) => c.url), [REFRESH]);
});
