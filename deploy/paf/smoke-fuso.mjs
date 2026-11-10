// RNF10 smoke of the API image: emission and painel against a container whose only clock source is the image's tzdata.
// PafRelogio resolves America/Sao_Paulo in a static initializer, so an image without /usr/share/zoneinfo answers 500.
//
// Run by smoke-fuso.sh (API already started on a fresh `seed-demo` database):
//   PAF_API=http://127.0.0.1:<porta> node deploy/paf/smoke-fuso.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
const API = process.env.PAF_API;
if (!API) {
  console.error("PAF_API is required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const SENHA_DEMO = "Password123!";
const TIMEOUT_MS = 15_000;
const PRONTIDAO_MS = 180_000;
// Brazil has had no daylight saving time since 2019 (Decreto 9.772): Sao Paulo is always UTC-3.
const OFFSET_SAO_PAULO = "-03:00";

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

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

async function aguardarProntidao() {
  const limite = Date.now() + PRONTIDAO_MS;
  let ultimo = "sem resposta";
  while (Date.now() < limite) {
    try {
      const r = await chamar("GET", "/health/ready", {});
      if (r.status === 200) return true;
      ultimo = `status ${r.status}`;
    } catch (erro) {
      ultimo = erro.message;
    }
    await new Promise((resolve) => setTimeout(resolve, 2_000));
  }
  console.log(`API não ficou pronta em ${PRONTIDAO_MS} ms: ${ultimo}`);
  return false;
}

try {
  const pronta = await aguardarProntidao();
  check("api: /health/ready 200", pronta);
  if (!pronta) process.exit(1);

  const admin = await exigir("POST", "/api/v1/identity/token/issue", { tenant: TENANT }, { email: EMAIL, password: SENHA_DEMO });
  const comAdmin = { tenant: TENANT, Authorization: `Bearer ${admin.accessToken}` };
  const estacoes = await exigir("GET", "/api/v1/paf/estacoes", comAdmin);
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  check("seed-demo: estação de emissão e painel existe", totem !== undefined);
  if (totem === undefined) process.exit(1);

  const link = await exigir("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, comAdmin, {});
  const resgate = await exigir("POST", "/api/v1/paf/estacoes/vinculo/resgatar", { tenant: link.tenant }, { token: link.token });
  const credencial = (papel) => resgate.credenciais.find((c) => c.papel === papel)?.accessToken;
  const comEmissao = { tenant: link.tenant, Authorization: `Bearer ${credencial("Emissao")}` };
  const comPainel = { tenant: link.tenant, Authorization: `Bearer ${credencial("Painel")}` };

  const configuracao = await exigir("GET", "/api/v1/paf/estacao/emissao/configuracao", comEmissao);
  const botao = configuracao.botoes[0];
  check("totem: configuração com botão de emissão", botao !== undefined);
  if (botao === undefined) process.exit(1);

  const emissao = await chamar(
    "POST",
    "/api/v1/paf/estacao/emissao/fichas",
    { ...comEmissao, "Idempotency-Key": crypto.randomUUID() },
    { filaId: botao.filaId, prioridadeId: botao.prioridadeId },
  );
  check("emissão: 200", emissao.status === 200, `status ${emissao.status} ${emissao.status === 200 ? "" : JSON.stringify(emissao.corpo)}`);
  const emitidaEm = emissao.corpo?.emitidaEm;
  check(`emissão: EmitidaEm em ${OFFSET_SAO_PAULO}`, typeof emitidaEm === "string" && emitidaEm.endsWith(OFFSET_SAO_PAULO), emitidaEm);

  const painel = await chamar("GET", "/api/v1/paf/estacao/painel/estado", comPainel);
  check("painel: estado 200", painel.status === 200, `status ${painel.status}`);
} catch (erro) {
  check("smoke executou sem erro de chamada", false, erro.message);
}

console.log(falhas === 0 ? "SMOKE-FUSO OK" : `SMOKE-FUSO FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
