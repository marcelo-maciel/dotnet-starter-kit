// Run: node --test deploy/paf/exigir-homologacao-verde.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { avaliarHomologacao } from "./exigir-homologacao-verde.mjs";

const SHA = "a".repeat(40);
const OUTRO_SHA = "b".repeat(40);

function implantacao(id, sha, environment, criadaEm, ...estados) {
  return {
    id,
    sha,
    environment,
    created_at: criadaEm,
    statuses: estados.map(([state, created_at]) => ({ state, created_at })),
  };
}

test("SHA com a implantação mais recente verde em homologação passa", () => {
  const r = avaliarHomologacao(SHA, [
    implantacao(1, SHA, "homologacao", "2026-10-19T10:00:00Z", ["in_progress", "2026-10-19T10:00:05Z"], ["success", "2026-10-19T10:04:00Z"]),
  ]);
  assert.equal(r.verde, true);
});

test("SHA sem implantação em homologação falha", () => {
  const r = avaliarHomologacao(SHA, []);
  assert.equal(r.verde, false);
  assert.match(r.motivo, /nenhuma implantação/);
});

test("implantação em homologação que falhou bloqueia a produção", () => {
  const r = avaliarHomologacao(SHA, [
    implantacao(1, SHA, "homologacao", "2026-10-19T10:00:00Z", ["in_progress", "2026-10-19T10:00:05Z"], ["failure", "2026-10-19T10:03:00Z"]),
  ]);
  assert.equal(r.verde, false);
  assert.match(r.motivo, /failure/);
});

test("implantação ainda em andamento não conta como verde", () => {
  const r = avaliarHomologacao(SHA, [implantacao(1, SHA, "homologacao", "2026-10-19T10:00:00Z", ["in_progress", "2026-10-19T10:00:05Z"])]);
  assert.equal(r.verde, false);
});

test("implantação sem nenhum status não conta como verde", () => {
  const r = avaliarHomologacao(SHA, [implantacao(1, SHA, "homologacao", "2026-10-19T10:00:00Z")]);
  assert.equal(r.verde, false);
  assert.match(r.motivo, /sem status/);
});

test("verde de outro SHA não libera este", () => {
  const r = avaliarHomologacao(SHA, [implantacao(1, OUTRO_SHA, "homologacao", "2026-10-19T10:00:00Z", ["success", "2026-10-19T10:04:00Z"])]);
  assert.equal(r.verde, false);
});

test("verde do mesmo SHA em outro ambiente não libera", () => {
  const r = avaliarHomologacao(SHA, [implantacao(1, SHA, "producao", "2026-10-19T10:00:00Z", ["success", "2026-10-19T10:04:00Z"])]);
  assert.equal(r.verde, false);
});

test("reimplantação mais recente que falhou vence o verde antigo do mesmo SHA, em qualquer ordem da API", () => {
  const antigaVerde = implantacao(1, SHA, "homologacao", "2026-10-19T10:00:00Z", ["success", "2026-10-19T10:04:00Z"]);
  const novaFalha = implantacao(2, SHA, "homologacao", "2026-10-19T12:00:00Z", ["failure", "2026-10-19T12:03:00Z"], ["in_progress", "2026-10-19T12:00:05Z"]);
  assert.equal(avaliarHomologacao(SHA, [antigaVerde, novaFalha]).verde, false);
  assert.equal(avaliarHomologacao(SHA, [novaFalha, antigaVerde]).verde, false);
});

test("o executável recusa SHA malformado e falta de token com código 2, antes de chamar o GitHub", () => {
  const script = fileURLToPath(new URL("./exigir-homologacao-verde.mjs", import.meta.url));
  for (const [args, env] of [
    [["nao-e-sha"], { GITHUB_TOKEN: "t", GITHUB_REPOSITORY: "dono/repo" }],
    [[SHA], { GITHUB_REPOSITORY: "dono/repo" }],
    [[], { GITHUB_TOKEN: "t", GITHUB_REPOSITORY: "dono/repo" }],
  ]) {
    const limpo = { PATH: process.env.PATH, SystemRoot: process.env.SystemRoot, ...env };
    assert.throws(
      () => execFileSync(process.execPath, [script, ...args], { env: limpo, stdio: "pipe", timeout: 10_000 }),
      (erro) => erro.status === 2,
    );
  }
});
