// node --test src/Modules/PAF/ferramentas/citacoes-adr.test.mjs
// Synthetic ADRs and manifests against this repository's own HEAD; the real run over ADR 0007 and 0008 is the gate.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { conferir, extrairCitacoes } from './citacoes-adr.mjs';

const AQUI = path.dirname(fileURLToPath(import.meta.url));
const SCRIPT = path.join(AQUI, 'citacoes-adr.mjs');
const RAIZ = execFileSync('git', ['-C', AQUI, 'rev-parse', '--show-toplevel'], { encoding: 'utf8', timeout: 15000 }).trim();
const ARQ = 'src/BuildingBlocks/Storage/Services/IStorageService.cs';
const upload = (linha) => ({ [`${ARQ}:${linha}`]: { simbolo: 'GenerateUploadUrlAsync', revisao: 'HEAD' } });

const adr = (texto) => [{ nome: 'sintetico.md', texto }];

test('citação certa no HEAD passa', () => {
  const { ok, falhas } = conferir(RAIZ, upload(44), adr(`Upload (\`${ARQ}:44\`).`));
  assert.deepEqual(falhas, []);
  assert.equal(ok.length, 1);
});

test('citação sintética errada (linha que driftou) falha pelo símbolo', () => {
  const { falhas } = conferir(RAIZ, upload(34), adr(`Upload (\`${ARQ}:34\`).`));
  assert.equal(falhas.length, 1);
  assert.match(falhas[0], /não contém "GenerateUploadUrlAsync" em HEAD/);
});

test('citação fora do manifesto e entrada do manifesto sem citação falham', () => {
  const { falhas } = conferir(RAIZ, upload(44), adr(`Download (\`${ARQ}:56\`).`));
  assert.equal(falhas.length, 2);
  assert.match(falhas[0], /:56` não está no manifesto/);
  assert.match(falhas[1], /:44` não é citado por nenhum ADR/);
});

test('continuação `:N` herda o arquivo anterior da mesma linha; sem arquivo antes é erro', () => {
  const { citacoes, erros } = extrairCitacoes(`(\`${ARQ}:44\` e \`:56-60\`)\nsó \`:12\``, 'x.md');
  assert.deepEqual(citacoes.map(c => c.chave), [`${ARQ}:44`, `${ARQ}:56-60`]);
  assert.equal(erros.length, 1);
  assert.match(erros[0], /x\.md:2: `:12` continua uma citação/);
});

test('nome curto resolve pelo caminho completo citado no ADR; ambíguo ou ausente é erro', () => {
  const um = extrairCitacoes(`\`${ARQ}\` e depois \`IStorageService.cs:44-49\``, 'x.md');
  assert.deepEqual(um.citacoes.map(c => c.chave), [`${ARQ}:44-49`]);
  assert.deepEqual(um.erros, []);

  const nenhum = extrairCitacoes('`Extensions.cs:12`', 'x.md');
  assert.match(nenhum.erros[0], /sem caminho e 0 caminhos/);

  const dois = extrairCitacoes('`a/Extensions.cs` `b/Extensions.cs` `Extensions.cs:12`', 'x.md');
  assert.match(dois.erros[0], /sem caminho e 2 caminhos/);
});

test('texto entre crases que não é citação é ignorado', () => {
  const { citacoes, erros } = extrairCitacoes('`perm:u:{userId}` `Storage:Provider` `QuotaOptions:Enabled` `paf:{t}:x` `10:30`', 'x.md');
  assert.deepEqual(citacoes, []);
  assert.deepEqual(erros, []);
});

test('intervalo além do fim, arquivo inexistente e revisão inexistente falham', () => {
  const alem = conferir(RAIZ, { [`${ARQ}:80-9999`]: { simbolo: 'x', revisao: 'HEAD' } }, adr(`\`${ARQ}:80-9999\``));
  assert.match(alem.falhas[0], /passa do fim do arquivo/);

  const sumiu = conferir(RAIZ, { 'src/Nao/Existe.cs:1': { simbolo: 'x', revisao: 'HEAD' } }, adr('`src/Nao/Existe.cs:1`'));
  assert.match(sumiu.falhas[0], /não existe em HEAD/);

  const revisao = conferir(RAIZ, { [`${ARQ}:44`]: { simbolo: 'GenerateUploadUrlAsync', revisao: 'nao-existe-123' } }, adr(`\`${ARQ}:44\``));
  assert.match(revisao.falhas[0], /não existe em nao-existe-123/);
});

test('o mesmo arquivo com uma linha a mais no topo faz a citação falhar (drift real)', () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'citacoes-'));
  try {
    const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8', timeout: 15000 });
    git('init', '-q');
    const original = execFileSync('git', ['-C', RAIZ, 'show', `HEAD:${ARQ}`], { encoding: 'utf8', timeout: 15000 });
    fs.mkdirSync(path.join(repo, path.dirname(ARQ)), { recursive: true });
    fs.writeFileSync(path.join(repo, ARQ), '// linha nova\n' + original);
    git('add', ARQ);
    git('-c', 'user.email=m02@teste', '-c', 'user.name=m02', 'commit', '-q', '-m', 'x');

    const { falhas } = conferir(repo, upload(44), adr(`\`${ARQ}:44\``));
    assert.equal(falhas.length, 1);
    assert.match(falhas[0], /não contém/);
  } finally {
    fs.rmSync(repo, { recursive: true, force: true });
  }
});

test('linha de comando: 0 sem falha, 1 com citação errada, 2 sem argumentos', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'citacoes-cli-'));
  try {
    const manifestoCerto = path.join(dir, 'certo.json');
    const manifestoErrado = path.join(dir, 'errado.json');
    const certo = path.join(dir, 'certo.md');
    const errado = path.join(dir, 'errado.md');
    fs.writeFileSync(manifestoCerto, JSON.stringify(upload(44)));
    fs.writeFileSync(manifestoErrado, JSON.stringify(upload(34)));
    fs.writeFileSync(certo, `Upload (\`${ARQ}:44\`).`);
    fs.writeFileSync(errado, `Upload (\`${ARQ}:34\`).`);
    const rodar = (...a) => spawnSync(process.execPath, [SCRIPT, ...a], { encoding: 'utf8', timeout: 30000 });

    assert.equal(rodar(RAIZ, manifestoCerto, certo).status, 0);

    const comErro = rodar(RAIZ, manifestoErrado, errado);
    assert.equal(comErro.status, 1);
    assert.match(comErro.stdout, /FALHA .*:34` não contém/);

    assert.equal(rodar().status, 2);
    assert.equal(rodar(RAIZ, path.join(dir, 'nao-existe.json'), certo).status, 2);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
