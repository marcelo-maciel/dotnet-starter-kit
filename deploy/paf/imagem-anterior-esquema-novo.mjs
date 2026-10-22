// O07 (RNF16): the PREVIOUS API image against the NEW schema still emits at the totem and calls at the guichê. During a
// rolling deploy the migrator expands the schema first and the old instance keeps serving until it is replaced, so an
// expand migration that breaks it shows up here as a failed emission or call.
//
// Run by imagem-anterior-esquema-novo.sh (old API already started on a database migrated and seeded by the new tree):
//   PAF_API=http://127.0.0.1:<porta> PAF_SENHA_DEMO=<seed password> node deploy/paf/imagem-anterior-esquema-novo.mjs
// Exit 0 only when every check passed; each check prints PASS/FAIL.
const API = process.env.PAF_API;
const SENHA_DEMO = process.env.PAF_SENHA_DEMO;
if (!API || !SENHA_DEMO) {
  console.error("PAF_API e PAF_SENHA_DEMO são obrigatórios");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const TIMEOUT_MS = 15_000;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

// ponytail: same request helper as smoke-fuso.mjs; extract a shared module if a third script needs it.
async function chamar(method, path, headers, body) {
  const todos = { "X-FSH-App": "dashboard", ...headers };
  if (body !== undefined) todos["Content-Type"] = "application/json";
  const r = await fetch(API + path, {
    method,
    headers: todos,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });
  const texto = await r.text();
  return { status: r.status, corpo: texto.length > 0 && r.headers.get("content-type")?.includes("json") ? JSON.parse(texto) : texto };
}

async function exigir(method, path, headers, body) {
  const r = await chamar(method, path, headers, body);
  if (r.status < 200 || r.status > 299) throw new Error(`${method} ${path} -> ${r.status} ${JSON.stringify(r.corpo)}`);
  return r.corpo;
}

try {
  const admin = await exigir("POST", "/api/v1/identity/token/issue", { tenant: TENANT }, { email: EMAIL, password: SENHA_DEMO });
  const comAdmin = { tenant: TENANT, Authorization: `Bearer ${admin.accessToken}` };
  const estacoes = await exigir("GET", "/api/v1/paf/estacoes", comAdmin);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const guiche = estacoes.find((e) => e.tipo === "Guiche");
  check("seed-demo: totem e guichê existem", totem !== undefined && guiche !== undefined);
  if (totem === undefined || guiche === undefined) process.exit(1);

  const link = await exigir("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, comAdmin, {});
  const resgate = await exigir("POST", "/api/v1/paf/estacoes/vinculo/resgatar", { tenant: link.tenant }, { token: link.token });
  const credencial = (papel) => resgate.credenciais.find((c) => c.papel === papel)?.accessToken;
  const comEmissao = { tenant: link.tenant, Authorization: `Bearer ${credencial("Emissao")}` };
  const comPainel = { tenant: link.tenant, Authorization: `Bearer ${credencial("Painel")}` };

  const configuracao = await exigir("GET", "/api/v1/paf/estacao/emissao/configuracao", comEmissao);
  const botao = configuracao.botoes[0];
  check("totem: configuração com botão de emissão", botao !== undefined);
  if (botao === undefined) process.exit(1);

  // The totem endpoint answers 200 with the ficha (IssueFichaEndpoint, Results.Ok), not 201.
  const emissao = await chamar(
    "POST",
    "/api/v1/paf/estacao/emissao/fichas",
    { ...comEmissao, "Idempotency-Key": crypto.randomUUID() },
    { filaId: botao.filaId, prioridadeId: botao.prioridadeId },
  );
  const senhaEmitida = emissao.corpo?.senha;
  check("emissão no totem: 200 com senha", emissao.status === 200 && typeof senhaEmitida === "string",
    `status ${emissao.status} ${emissao.status === 200 ? senhaEmitida : JSON.stringify(emissao.corpo)}`);

  const selecao = await chamar("POST", "/api/v1/paf/sessao/guiche", comAdmin, { estacaoId: guiche.id, liberarPendencia: false });
  check("guichê: seleção 204", selecao.status === 204, `status ${selecao.status} ${JSON.stringify(selecao.corpo)}`);

  // A ficha becomes callable only 5 s after emission (ChamadaDeFicha.Elegiveis, f."CriadoEm" <= now() - 5 s).
  await new Promise((resolve) => setTimeout(resolve, 6_000));
  const chamada = await chamar("POST", "/api/v1/paf/guiche/chamar-proximo", comAdmin, undefined);
  check("guichê: chamar próxima 200 com a ficha emitida", chamada.status === 200 && chamada.corpo?.senha === senhaEmitida,
    `status ${chamada.status} ${JSON.stringify(chamada.corpo)}`);

  const painel = await chamar("GET", "/api/v1/paf/estacao/painel/estado", comPainel);
  check("painel: estado 200 com a chamada", painel.status === 200 && painel.corpo?.chamadas?.some((c) => c.senha === senhaEmitida),
    `status ${painel.status}`);
} catch (erro) {
  check("ensaio executou sem erro de chamada", false, erro.message);
}

console.log(falhas === 0 ? "IMAGEM-ANTERIOR-ESQUEMA-NOVO OK" : `IMAGEM-ANTERIOR-ESQUEMA-NOVO FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
