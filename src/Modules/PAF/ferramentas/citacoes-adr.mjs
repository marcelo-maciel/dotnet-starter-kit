// Checks every `arquivo:linha` citation of the PAF ADRs against the symbol expected at that line in the repository.
// Usage: node citacoes-adr.mjs <repo-root> <manifest.json> <adr.md>...
// Exit 0: every citation found, in the manifest, and its symbol present in the cited lines at the manifest revision.
// Exit 1: at least one failure (listed). Exit 2: wrong usage or unreadable input.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const GIT_TIMEOUT_MS = 15000;

// `src/x/Arquivo.cs:12`, `Arquivo.cs:12-20`; the bare `:46` form continues the previous file of the same line.
const CITACAO_COM_ARQUIVO = /^(?<arquivo>[\w.\/-]+\.[A-Za-z]+):(?<inicio>\d+)(?:-(?<fim>\d+))?$/;
const CITACAO_CONTINUADA = /^:(?<inicio>\d+)(?:-(?<fim>\d+))?$/;
const CAMINHO = /^[\w.\/-]+\/[\w.-]+\.[A-Za-z]+$/;

export function extrairCitacoes(texto, nomeAdr) {
  const linhas = texto.split(/\r?\n/);
  const caminhos = linhas.flatMap(l => [...l.matchAll(/`([^`]+)`/g)].map(m => m[1].replace(/:\d+(?:-\d+)?$/, '')))
    .filter(t => CAMINHO.test(t));
  const citacoes = [];
  const erros = [];

  linhas.forEach((linha, i) => {
    let anterior = null;
    for (const m of linha.matchAll(/`([^`]+)`/g)) {
      const trecho = m[1];
      const local = `${nomeAdr}:${i + 1}`;
      const comArquivo = CITACAO_COM_ARQUIVO.exec(trecho);
      const continuada = CITACAO_CONTINUADA.exec(trecho);
      if (!comArquivo && !continuada) {
        continue;
      }

      let arquivo;
      if (comArquivo) {
        arquivo = comArquivo.groups.arquivo;
        if (!arquivo.includes('/')) {
          const candidatos = [...new Set(caminhos.filter(c => path.posix.basename(c) === arquivo))];
          if (candidatos.length !== 1) {
            erros.push(`${local}: \`${trecho}\` sem caminho e ${candidatos.length} caminhos completos com esse nome no ADR`);
            continue;
          }
          arquivo = candidatos[0];
        }
      } else if (anterior) {
        arquivo = anterior;
      } else {
        erros.push(`${local}: \`${trecho}\` continua uma citação, mas não há arquivo antes dela na linha`);
        continue;
      }

      const grupos = (comArquivo ?? continuada).groups;
      const inicio = Number(grupos.inicio);
      const fim = grupos.fim === undefined ? inicio : Number(grupos.fim);
      anterior = arquivo;
      citacoes.push({ local, chave: `${arquivo}:${inicio === fim ? inicio : `${inicio}-${fim}`}`, arquivo, inicio, fim });
    }
  });

  return { citacoes, erros };
}

function lerNaRevisao(raiz, revisao, arquivo) {
  return execFileSync('git', ['-C', raiz, 'show', `${revisao}:${arquivo}`], {
    encoding: 'utf8',
    timeout: GIT_TIMEOUT_MS,
    stdio: ['ignore', 'pipe', 'pipe'],
    maxBuffer: 64 * 1024 * 1024,
  });
}

export function conferir(raiz, manifesto, adrs) {
  const falhas = [];
  const ok = [];
  const usadas = new Set();

  for (const { nome, texto } of adrs) {
    const { citacoes, erros } = extrairCitacoes(texto, nome);
    falhas.push(...erros);
    for (const c of citacoes) {
      const esperado = manifesto[c.chave];
      if (!esperado) {
        falhas.push(`${c.local}: \`${c.chave}\` não está no manifesto (símbolo esperado desconhecido)`);
        continue;
      }
      usadas.add(c.chave);
      if (c.fim < c.inicio) {
        falhas.push(`${c.local}: \`${c.chave}\` com intervalo invertido`);
        continue;
      }

      let conteudo;
      try {
        conteudo = lerNaRevisao(raiz, esperado.revisao, c.arquivo);
      } catch (erro) {
        falhas.push(`${c.local}: \`${c.chave}\` não existe em ${esperado.revisao} (${String(erro.stderr ?? erro.message).trim()})`);
        continue;
      }

      const linhas = conteudo.split(/\r?\n/);
      if (c.fim > linhas.length) {
        falhas.push(`${c.local}: \`${c.chave}\` passa do fim do arquivo (${linhas.length} linhas em ${esperado.revisao})`);
        continue;
      }

      const trecho = linhas.slice(c.inicio - 1, c.fim).join('\n');
      if (!trecho.includes(esperado.simbolo)) {
        falhas.push(`${c.local}: \`${c.chave}\` não contém "${esperado.simbolo}" em ${esperado.revisao}`);
        continue;
      }
      ok.push(`${c.local}: \`${c.chave}\` contém "${esperado.simbolo}" em ${esperado.revisao}`);
    }
  }

  for (const chave of Object.keys(manifesto)) {
    if (!usadas.has(chave)) {
      falhas.push(`manifesto: \`${chave}\` não é citado por nenhum ADR (citação removida ou renumerada)`);
    }
  }

  return { ok, falhas };
}

function principal(argumentos) {
  if (argumentos.length < 3) {
    console.error('uso: node citacoes-adr.mjs <raiz-do-repo> <manifesto.json> <adr.md>...');
    return 2;
  }

  const [raiz, caminhoManifesto, ...arquivosAdr] = argumentos;
  let manifesto;
  let adrs;
  try {
    manifesto = JSON.parse(fs.readFileSync(caminhoManifesto, 'utf8'));
    adrs = arquivosAdr.map(a => ({ nome: path.basename(a), texto: fs.readFileSync(a, 'utf8') }));
  } catch (erro) {
    console.error(`entrada ilegível: ${erro.message}`);
    return 2;
  }

  const { ok, falhas } = conferir(raiz, manifesto, adrs);
  for (const linha of ok) console.log(`OK     ${linha}`);
  for (const linha of falhas) console.log(`FALHA  ${linha}`);
  console.log(`${ok.length} citações conferidas, ${falhas.length} falhas`);
  return falhas.length === 0 ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = principal(process.argv.slice(2));
}
