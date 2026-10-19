// Deployment invariants of the PAF over the rendered compose model of both environments (O02). Called by
// verificar-ambiente.sh, which renders each environment with `docker compose config --format json`; the model is what
// the containers get, env_file merged and interpolated, so nothing here re-implements compose.
//
//   node verificar-ambiente.mjs <appsettings.Production.json> <producao.env> <producao.json> <producao.stderr> \
//                               <homologacao.env> <homologacao.json> <homologacao.stderr>
//
// Prints one PASS/FAIL line per check and exits 1 when any check failed.
import { readFileSync } from "node:fs";

const FUSO = "America/Sao_Paulo";
// RNF05 ceiling shared with the O19 test (src/Tests/PAF.Tests/PrazosEntradaSaida/RegrasDePrazo.cs, Teto = 30 s).
const TETO_PRAZO_S = 30;
const SERVICOS_DOTNET = ["api-a", "api-b", "migrator"];

const argumentos = process.argv.slice(2);
if (argumentos.length !== 7) {
  console.error("uso: node verificar-ambiente.mjs <appsettings.Production.json> <prod.env> <prod.json> <prod.stderr> <homolog.env> <homolog.json> <homolog.stderr>");
  process.exit(2);
}
const [arquivoAppsettings, ...porAmbiente] = argumentos;

let falhas = 0;
function conferir(rotulo, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${rotulo}${ok || detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

function lerArquivoDeAmbiente(caminho) {
  const valores = new Map();
  for (const linha of readFileSync(caminho, "utf8").split(/\r?\n/)) {
    const limpa = linha.trim();
    if (limpa === "" || limpa.startsWith("#")) continue;
    const igual = limpa.indexOf("=");
    if (igual > 0) valores.set(limpa.slice(0, igual), limpa.slice(igual + 1));
  }
  return valores;
}

function carregar(rotulo, arquivoEnv, arquivoJson, arquivoStderr) {
  return {
    rotulo,
    env: lerArquivoDeAmbiente(arquivoEnv),
    modelo: JSON.parse(readFileSync(arquivoJson, "utf8")),
    avisos: readFileSync(arquivoStderr, "utf8").split(/\r?\n/).filter((l) => l.trim() !== ""),
  };
}

const appsettingsDoKit = JSON.parse(readFileSync(arquivoAppsettings, "utf8").replace(/^﻿/, ""));
const cabecalhosDoKit = appsettingsDoKit.CorsOptions.AllowedHeaders.map((h) => h.toLowerCase());
// O10: positions of the kit's Serilog sinks, so the environment overrides the right index whatever the kit reorders.
const sinksDoKit = (appsettingsDoKit.Serilog?.WriteTo ?? []).map((w, i) => ({ indice: i, nome: w.Name }));

const ambientes = [
  carregar("producao", porAmbiente[0], porAmbiente[1], porAmbiente[2]),
  carregar("homologacao", porAmbiente[3], porAmbiente[4], porAmbiente[5]),
];

// "key=value;key=value" with Npgsql's tolerance: keys case- and space-insensitive.
function pares(texto, separador) {
  const mapa = new Map();
  for (const parte of texto.split(separador)) {
    const igual = parte.indexOf("=");
    if (igual > 0) mapa.set(parte.slice(0, igual).replace(/\s+/g, "").toLowerCase(), parte.slice(igual + 1).trim());
  }
  return mapa;
}

function inteiroNoIntervalo(valor, minimo, maximo) {
  return valor !== undefined && /^\d+$/.test(valor) && Number(valor) >= minimo && Number(valor) <= maximo;
}

function subrede(modelo) {
  return modelo.networks?.paf?.ipam?.config?.[0]?.subnet;
}

function conferirAmbiente({ rotulo, env, modelo, avisos }) {
  const r = (texto) => `${rotulo}: ${texto}`;
  const servicos = modelo.services ?? {};

  // A `$` inside a secret is interpolated by compose and silently cut; any warning means the model is not the file.
  conferir(r("compose sem aviso de interpolação"), avisos.length === 0, avisos.join(" | "));
  conferir(r("nome do projeto = COMPOSE_PROJECT_NAME"), modelo.name !== undefined && modelo.name === env.get("COMPOSE_PROJECT_NAME"), `${modelo.name} x ${env.get("COMPOSE_PROJECT_NAME")}`);

  const comNomeFixo = Object.entries(servicos).filter(([, s]) => s.container_name !== undefined).map(([n]) => n);
  conferir(r("nenhum container_name"), comNomeFixo.length === 0, comNomeFixo.join(", "));

  const recursos = [...Object.values(modelo.volumes ?? {}), ...Object.values(modelo.networks ?? {})];
  const foraDoProjeto = recursos.filter((v) => v.external === true || !String(v.name).startsWith(`${modelo.name}_`)).map((v) => v.name);
  conferir(r("volumes e rede prefixados pelo projeto"), foraDoProjeto.length === 0, foraDoProjeto.join(", "));

  const portasAbertas = Object.entries(servicos).flatMap(([n, s]) => (s.ports ?? []).filter((p) => p.host_ip !== "127.0.0.1").map((p) => `${n}:${p.published}`));
  conferir(r("toda porta publicada em 127.0.0.1"), portasAbertas.length === 0, portasAbertas.join(", "));

  const rede = subrede(modelo);
  conferir(r("rede com subnet fixa"), typeof rede === "string" && /^\d+\.\d+\.\d+\.\d+\/\d+$/.test(rede), rede);

  const postgres = servicos.postgres;
  conferir(r("postgres com timezone=America/Sao_Paulo"), (postgres?.command ?? []).includes(`timezone=${FUSO}`), JSON.stringify(postgres?.command));
  conferir(r("postgres com TZ"), postgres?.environment?.TZ === FUSO, postgres?.environment?.TZ);

  const valkey = servicos.valkey;
  const comandoValkey = valkey?.command ?? [];
  const senhaValkey = comandoValkey[comandoValkey.indexOf("--requirepass") + 1];
  conferir(r("valkey com senha"), comandoValkey.includes("--requirepass") && typeof senhaValkey === "string" && senhaValkey.length > 0);
  conferir(r("valkey com AOF"), comandoValkey.join(" ").includes("--appendonly yes"), comandoValkey.join(" "));
  conferir(r("valkey sem porta publicada"), (valkey?.ports ?? []).length === 0);

  const dashboard = servicos.dashboard;
  conferir(r("dashboard com prazo de inatividade (D04)"), inteiroNoIntervalo(dashboard?.environment?.PAF_INATIVIDADE_MS, 1, Number.MAX_SAFE_INTEGER), dashboard?.environment?.PAF_INATIVIDADE_MS);
  conferir(r("dashboard com template do PAF montado"), (dashboard?.volumes ?? []).some((v) => v.target === "/etc/fsh/config.json.template" && v.read_only === true));

  for (const nome of SERVICOS_DOTNET) {
    const servico = servicos[nome];
    conferir(r(`${nome} presente no modelo`), servico !== undefined);
    if (servico === undefined) continue;
    const e = servico.environment ?? {};
    const s = (texto) => r(`${nome} ${texto}`);

    const exemplos = Object.entries(e).filter(([, v]) => typeof v === "string" && /<[^>]+>/.test(v)).map(([k]) => k);
    conferir(s("sem valor de exemplo (<...>)"), exemplos.length === 0, exemplos.join(", "));
    // Every literal line of the file reaches the container: a wrong PAF_ARQUIVO_AMBIENTE loads another environment.
    const divergentes = [...env].filter(([k, v]) => !v.includes("$") && e[k] !== v).map(([k]) => k);
    conferir(s("env_file é o próprio arquivo do ambiente"), divergentes.length === 0, divergentes.join(", "));

    conferir(s("TZ=America/Sao_Paulo"), e.TZ === FUSO, e.TZ);
    conferir(s("QuotaOptions__Enabled=false explícito"), typeof e.QuotaOptions__Enabled === "string" && e.QuotaOptions__Enabled.toLowerCase() === "false", e.QuotaOptions__Enabled ?? "ausente");

    const chave = e.JwtOptions__SigningKey ?? "";
    conferir(s("chave de assinatura presente"), chave.length > 0);
    conferir(s("chave de assinatura com 32+ caracteres"), chave.length >= 32, `${chave.length} caracteres`);
    // The kit signs with UTF-8 and validates with ASCII (ADR 0008): any other byte breaks every token.
    conferir(s("chave de assinatura só ASCII imprimível"), /^[\x21-\x7E]*$/.test(chave));
    conferir(s("chave de assinatura não é o exemplo"), !chave.toLowerCase().includes("replace-with"));
    conferir(s("AccessTokenMinutes explícito"), inteiroNoIntervalo(e.JwtOptions__AccessTokenMinutes, 1, 1440), e.JwtOptions__AccessTokenMinutes ?? "ausente");

    const redesConfiaveis = Object.entries(e).filter(([k, v]) => k.startsWith("TrustedProxyOptions__KnownNetworks__") && v).map(([, v]) => v);
    conferir(s("KnownNetworks não vazio"), redesConfiaveis.length > 0);
    conferir(s("KnownNetworks = subnet da rede do projeto"), redesConfiaveis.includes(rede), `${redesConfiaveis.join(",")} x ${rede}`);

    const cabecalhos = Object.entries(e).filter(([k]) => k.startsWith("CorsOptions__AllowedHeaders__")).map(([, v]) => String(v).toLowerCase());
    conferir(s("CORS aceita x-paf-unidade"), cabecalhos.includes("x-paf-unidade"));
    // Env entries replace the kit's array by index: any kit header missing here was overwritten.
    const perdidos = cabecalhosDoKit.filter((h) => !cabecalhos.includes(h));
    conferir(s("CORS mantém os cabeçalhos do kit"), perdidos.length === 0, perdidos.join(", "));

    const banco = pares(e.DatabaseOptions__ConnectionString ?? "", ";");
    conferir(s("PostgreSQL do próprio projeto"), banco.get("host") === "postgres", banco.get("host"));
    conferir(s("senha do PostgreSQL = POSTGRES_PASSWORD"), banco.get("password") === postgres?.environment?.POSTGRES_PASSWORD);
    conferir(s(`PostgreSQL Timeout explícito, 1..${TETO_PRAZO_S} s`), inteiroNoIntervalo(banco.get("timeout"), 1, TETO_PRAZO_S), banco.get("timeout") ?? "ausente");
    conferir(s(`PostgreSQL Command Timeout explícito, 1..${TETO_PRAZO_S} s`), inteiroNoIntervalo(banco.get("commandtimeout"), 1, TETO_PRAZO_S), banco.get("commandtimeout") ?? "ausente");

    const partesValkey = String(e.CachingOptions__Redis ?? "").split(",").map((p) => p.trim()).filter((p) => p !== "");
    const destinos = partesValkey.filter((p) => !p.includes("="));
    const opcoesValkey = pares(partesValkey.filter((p) => p.includes("=")).join(","), ",");
    // The only accepted endpoint is this project's own valkey service, reachable only inside its network: an
    // environment pointing anywhere else may be sharing the other environment's instance (RNF02).
    conferir(s("Valkey do próprio projeto (valkey:6379)"), destinos.length === 1 && ["valkey", "valkey:6379"].includes(destinos[0]), destinos.join(","));
    conferir(s("senha do Valkey = VALKEY_PASSWORD"), opcoesValkey.get("password") === senhaValkey);
    for (const prazo of ["connecttimeout", "synctimeout", "asynctimeout"]) {
      conferir(s(`Valkey ${prazo} explícito, 1..${TETO_PRAZO_S * 1000} ms`), inteiroNoIntervalo(opcoesValkey.get(prazo), 1, TETO_PRAZO_S * 1000), opcoesValkey.get(prazo) ?? "ausente");
    }

    conferir(s("Storage__Provider=s3 (R2, ADR 0007)"), e.Storage__Provider === "s3", e.Storage__Provider);
    conferir(s("Storage__S3__ServiceUrl https"), /^https:\/\//.test(e.Storage__S3__ServiceUrl ?? ""), e.Storage__S3__ServiceUrl);
    conferir(s("Storage__S3__Bucket definido"), Boolean(e.Storage__S3__Bucket));

    // O10 (RNF06, ARQ l.392): OTLP to New Relic through the kit's env-var path, one log sink, JSON console.
    conferir(s("OTEL_EXPORTER_OTLP_ENDPOINT https"), /^https:\/\/\S+$/.test(e.OTEL_EXPORTER_OTLP_ENDPOINT ?? ""), e.OTEL_EXPORTER_OTLP_ENDPOINT ?? "ausente");
    conferir(s("OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf"), e.OTEL_EXPORTER_OTLP_PROTOCOL === "http/protobuf", e.OTEL_EXPORTER_OTLP_PROTOCOL ?? "ausente");
    conferir(s("OTEL_EXPORTER_OTLP_HEADERS com api-key"), /^api-key=\S+$/.test(e.OTEL_EXPORTER_OTLP_HEADERS ?? ""), e.OTEL_EXPORTER_OTLP_HEADERS === undefined ? "ausente" : "formato inválido");
    conferir(s(`OTEL_EXPORTER_OTLP_TIMEOUT explícito, 1..${TETO_PRAZO_S * 1000} ms`), inteiroNoIntervalo(e.OTEL_EXPORTER_OTLP_TIMEOUT, 1, TETO_PRAZO_S * 1000), e.OTEL_EXPORTER_OTLP_TIMEOUT ?? "ausente");
    conferir(s("OTEL_SERVICE_NAME definido"), Boolean(e.OTEL_SERVICE_NAME));
    for (const { indice } of sinksDoKit.filter((w) => w.nome === "OpenTelemetry")) {
      // The kit adds its own OTLP sink from OTEL_EXPORTER_OTLP_ENDPOINT; the configured one would ship every log twice.
      conferir(s(`Serilog__WriteTo__${indice}__Name vazio (sink OTLP do appsettings desligado)`), e[`Serilog__WriteTo__${indice}__Name`] === "", e[`Serilog__WriteTo__${indice}__Name`] ?? "ausente");
    }
    for (const { indice } of sinksDoKit.filter((w) => w.nome === "Console")) {
      const formatador = e[`Serilog__WriteTo__${indice}__Args__formatter`] ?? "";
      conferir(s(`console em JSON (Serilog__WriteTo__${indice}__Args__formatter)`), formatador.startsWith("Serilog.Formatting.Compact."), formatador || "ausente");
    }
  }
}

function ipv4ParaNumero(ip) {
  return ip.split(".").reduce((acc, octeto) => acc * 256 + Number(octeto), 0);
}

function sobrepoe(a, b) {
  if (typeof a !== "string" || typeof b !== "string") return true;
  const faixa = (cidr) => {
    const [ip, bits] = cidr.split("/");
    const tamanho = 2 ** (32 - Number(bits));
    const inicio = Math.floor(ipv4ParaNumero(ip) / tamanho) * tamanho;
    return [inicio, inicio + tamanho - 1];
  };
  const [ai, af] = faixa(a);
  const [bi, bf] = faixa(b);
  return ai <= bf && bi <= af;
}

function portasPublicadas(modelo) {
  return Object.values(modelo.services ?? {}).flatMap((s) => (s.ports ?? []).map((p) => String(p.published)));
}

function api(modelo) {
  return modelo.services?.["api-a"]?.environment ?? {};
}

for (const ambiente of ambientes) conferirAmbiente(ambiente);

const [prod, homolog] = ambientes.map((a) => a.modelo);
conferir("cruzado: nomes de projeto diferentes", prod.name !== homolog.name, `${prod.name} x ${homolog.name}`);
conferir("cruzado: subnets sem sobreposição", !sobrepoe(subrede(prod), subrede(homolog)), `${subrede(prod)} x ${subrede(homolog)}`);
const portasRepetidas = portasPublicadas(prod).filter((p) => portasPublicadas(homolog).includes(p));
conferir("cruzado: portas publicadas distintas", portasRepetidas.length === 0, portasRepetidas.join(", "));
conferir("cruzado: mesmo AccessTokenMinutes", api(prod).JwtOptions__AccessTokenMinutes === api(homolog).JwtOptions__AccessTokenMinutes, `${api(prod).JwtOptions__AccessTokenMinutes} x ${api(homolog).JwtOptions__AccessTokenMinutes}`);
// Same key, issuer and audience would make a homologation token valid in production.
conferir("cruzado: chaves de assinatura diferentes", api(prod).JwtOptions__SigningKey !== api(homolog).JwtOptions__SigningKey);
conferir("cruzado: buckets do R2 diferentes", api(prod).Storage__S3__Bucket !== api(homolog).Storage__S3__Bucket, api(prod).Storage__S3__Bucket);
// Same service name would mix homologation telemetry into production's in New Relic.
conferir("cruzado: OTEL_SERVICE_NAME diferentes", api(prod).OTEL_SERVICE_NAME !== api(homolog).OTEL_SERVICE_NAME, api(prod).OTEL_SERVICE_NAME);

console.log(falhas === 0 ? "VERIFICAR-AMBIENTE OK" : `VERIFICAR-AMBIENTE FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
