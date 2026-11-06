// Production gate of the PAF workflow (O08, RNF17): a SHA only goes to production after it went green in homologação.
// GitHub records every run of a job bound to `environment: homologacao` as a deployment of that SHA, with
// statuses; the most recent deployment of the SHA has to end in "success". Anything else (none, failed,
// still running, only an older success followed by a failed redeploy) fails before anyone approves production.
//
// Usage (in the workflow): GITHUB_TOKEN=... GITHUB_REPOSITORY=owner/repo node deploy/paf/exigir-homologacao-verde.mjs <sha>
// Exit 0 = green; 1 = not green; 2 = bad input or GitHub API unreachable.
// Test: node --test deploy/paf/exigir-homologacao-verde.test.mjs
import { pathToFileURL } from "node:url";

export const AMBIENTE_HOMOLOGACAO = "homologacao";
const LIMITE_HTTP_MS = 15_000;

// Pure decision over what the API returned, so the rule is testable without GitHub.
// implantacoes: [{ id, sha, environment, created_at, statuses: [{ state, created_at }] }]
export function avaliarHomologacao(sha, implantacoes) {
  const doSha = implantacoes
    .filter((d) => d.sha === sha && d.environment === AMBIENTE_HOMOLOGACAO)
    .sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at));
  if (doSha.length === 0) return { verde: false, motivo: `nenhuma implantação de ${sha} em ${AMBIENTE_HOMOLOGACAO}` };
  const ultima = doSha[0];
  const status = [...ultima.statuses].sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at))[0];
  if (!status) return { verde: false, motivo: `implantação ${ultima.id} sem status` };
  if (status.state !== "success") return { verde: false, motivo: `implantação ${ultima.id} terminou em ${status.state}` };
  return { verde: true, motivo: `implantação ${ultima.id} verde em ${status.created_at}` };
}

async function github(caminho, token) {
  const resposta = await fetch(`https://api.github.com${caminho}`, {
    headers: { Authorization: `Bearer ${token}`, Accept: "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28" },
    signal: AbortSignal.timeout(LIMITE_HTTP_MS),
  });
  if (!resposta.ok) throw new Error(`GET ${caminho} -> ${resposta.status}`);
  return resposta.json();
}

async function principal() {
  const sha = process.argv[2];
  const token = process.env.GITHUB_TOKEN;
  const repositorio = process.env.GITHUB_REPOSITORY;
  if (!sha || !/^[0-9a-f]{40}$/.test(sha) || !token || !repositorio) {
    console.error("uso: GITHUB_TOKEN=... GITHUB_REPOSITORY=dono/repo node exigir-homologacao-verde.mjs <sha de 40 hex>");
    return 2;
  }
  const base = `/repos/${repositorio}/deployments?sha=${sha}&environment=${AMBIENTE_HOMOLOGACAO}&per_page=100`;
  const implantacoes = await github(base, token);
  for (const d of implantacoes) {
    d.statuses = await github(`/repos/${repositorio}/deployments/${d.id}/statuses?per_page=100`, token);
  }
  const { verde, motivo } = avaliarHomologacao(sha, implantacoes);
  console.log(`${verde ? "VERDE" : "NAO VERDE"}: ${motivo}`);
  return verde ? 0 : 1;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  principal().then(
    (codigo) => process.exit(codigo),
    (erro) => {
      console.error(`FAIL consulta ao GitHub: ${erro.message}`);
      process.exit(2);
    },
  );
}
