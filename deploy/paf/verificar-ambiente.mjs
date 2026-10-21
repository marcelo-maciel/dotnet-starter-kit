// Deployment invariants of the PAF over the rendered compose model of both environments (O02). Called by
// verificar-ambiente.sh, which renders each environment with `docker compose config --format json`; the model is what
// the containers get, env_file merged and interpolated, so nothing here re-implements compose.
//
//   node verificar-ambiente.mjs <appsettings.Production.json> <producao.env> <producao.json> <producao.stderr> \
//                               <homologacao.env> <homologacao.json> <homologacao.stderr>
//
// An environment whose rendered model (.json) is absent was not given to verificar-ambiente.sh (a host that runs only
// one environment): its checks and every cross check print SKIPPED, never PASS.
// Prints one PASS/FAIL/SKIPPED line per check and exits 1 when any check failed.
import { existsSync, readFileSync } from "node:fs";

const FUSO = "America/Sao_Paulo";
// RNF05 ceiling shared with the O19 test (src/Tests/PAF.Tests/PrazosEntradaSaida/RegrasDePrazo.cs, Teto = 30 s).
const TETO_PRAZO_S = 30;
const SERVICOS_DOTNET = ["api-a", "api-b", "migrator"];
// O12 floors: the measured worst minute at the D20 volume (deploy/paf/README.md, "Limite de taxa"), below which a
// store opening is refused. Auth is the acceptance criterion: 15 logins of one store behind its NAT in 60 s.
const PISOS_LIMITE_TAXA = { Auth: 15, Ip: 387, Tenant: 7740 };

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
  if (!existsSync(arquivoJson)) {
    console.log(`SKIPPED ${rotulo}: ambiente não informado, nenhuma conferência dele`);
    return undefined;
  }
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

const [producao, homologacao] = [
  carregar("producao", porAmbiente[0], porAmbiente[1], porAmbiente[2]),
  carregar("homologacao", porAmbiente[3], porAmbiente[4], porAmbiente[5]),
];
if (producao === undefined && homologacao === undefined) {
  console.error("nenhum ambiente para conferir");
  process.exit(2);
}

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

// Owner's decision: the PafHub takes WebSockets only. Long polling re-authenticates every poll and can lose a
// revocation notice; it is the Integration.Tests host's override (TestServer has no WebSocket), never an environment's.
// Absent = the OpcoesTempoReal default, WebSockets.
function soWebSockets(transportes) {
  return transportes === undefined || String(transportes).trim().toLowerCase() === "websockets";
}

function subrede(modelo) {
  return modelo.networks?.paf?.ipam?.config?.[0]?.subnet;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// The file `cloudflared tunnel create` writes; undefined when it is not JSON or lacks a field cloudflared needs.
function credenciaisDoTunel(texto) {
  try {
    const c = JSON.parse(texto ?? "");
    return ["AccountTag", "TunnelSecret", "TunnelID"].every((k) => typeof c?.[k] === "string" && c[k] !== "") ? c : undefined;
  } catch {
    return undefined;
  }
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

  // O11 (RNF14, ARQ l.388): the backup reads this project's PostgreSQL and writes only to its own backup bucket.
  const backup = servicos.backup?.environment ?? {};
  conferir(r("backup presente no modelo"), servicos.backup !== undefined);
  const exemplosBackup = Object.entries(backup).filter(([, v]) => typeof v === "string" && /<[^>]+>/.test(v)).map(([k]) => k);
  conferir(r("backup sem valor de exemplo (<...>)"), exemplosBackup.length === 0, exemplosBackup.join(", "));
  conferir(r("backup com TZ=America/Sao_Paulo (03:00 BRT)"), backup.TZ === FUSO, backup.TZ);
  conferir(r("backup lê o PostgreSQL do próprio projeto"), backup.PGHOST === "postgres" && backup.PGPASSWORD === postgres?.environment?.POSTGRES_PASSWORD, backup.PGHOST);
  conferir(r("backup com prefixo = nome do projeto"), backup.PAF_AMBIENTE === modelo.name, `${backup.PAF_AMBIENTE} x ${modelo.name}`);
  conferir(r("backup com endpoint https"), /^https:\/\//.test(backup.PAF_BACKUP_ENDPOINT ?? ""), backup.PAF_BACKUP_ENDPOINT);
  conferir(r("backup com alerta https"), /^https:\/\//.test(backup.PAF_BACKUP_ALERTA_URL ?? ""), backup.PAF_BACKUP_ALERTA_URL);
  const bucketAplicacao = servicos["api-a"]?.environment?.Storage__S3__Bucket;
  conferir(r("bucket de backup separado do bucket da aplicação"), Boolean(backup.PAF_BACKUP_BUCKET) && backup.PAF_BACKUP_BUCKET !== bucketAplicacao, `${backup.PAF_BACKUP_BUCKET} x ${bucketAplicacao}`);
  conferir(r("backup sem a credencial do Storage da aplicação"), Object.keys(backup).every((k) => !k.startsWith("Storage__")) && backup.AWS_ACCESS_KEY_ID !== servicos["api-a"]?.environment?.Storage__S3__AccessKey);

  // O03: the environment's entrance is its own tunnel into Caddy; neither publishes a port (the check above covers
  // every service) and both always run, never behind a profile.
  const caddy = servicos.caddy;
  conferir(r("caddy presente no modelo"), caddy !== undefined);
  conferir(r("caddy sem porta publicada"), (caddy?.ports ?? []).length === 0);
  conferir(r("caddy com o Caddyfile versionado montado só leitura"), (caddy?.volumes ?? []).some((v) => v.target === "/etc/caddy/Caddyfile" && v.read_only === true && /[\\/]caddy[\\/]Caddyfile$/.test(v.source ?? "")));
  // O03, A4: Caddy at a fixed address outside the dynamic range, the API trusting that address alone, and Caddy
  // trusting only the dynamic range; the bridge gateway (first host of the subnet, how host processes arrive) in none.
  const dinamica = modelo.networks?.paf?.ipam?.config?.[0]?.ip_range;
  const ipCaddy = caddy?.networks?.paf?.ipv4_address;
  const gateway = typeof rede === "string" && CIDR.test(rede) ? numeroParaIpv4(faixaCidr(rede)[0] + 1) : undefined;
  conferir(r("faixa dinâmica dentro da subnet e sem o gateway"), CIDR.test(dinamica ?? "") && CIDR.test(rede ?? "") && contem(rede, dinamica) && !dentro(gateway, dinamica), `${dinamica} em ${rede}, gateway ${gateway}`);
  conferir(r("IP fixo do Caddy dentro da subnet, fora da faixa dinâmica e diferente do gateway"), IPV4.test(ipCaddy ?? "") && CIDR.test(rede ?? "") && dentro(ipCaddy, rede) && CIDR.test(dinamica ?? "") && !dentro(ipCaddy, dinamica) && ipCaddy !== gateway && ipCaddy !== numeroParaIpv4(faixaCidr(rede)[0]), `${ipCaddy} em ${rede} fora de ${dinamica}`);
  conferir(r("caddy confia só na faixa dinâmica"), caddy?.environment?.PAF_SUBNET_DINAMICA === dinamica, `${caddy?.environment?.PAF_SUBNET_DINAMICA} x ${dinamica}`);
  for (const chave of ["PAF_API_URL", "PAF_DASHBOARD_URL", "PAF_ADMIN_URL"]) {
    conferir(r(`caddy com ${chave} https://<host>`), /^https:\/\/[a-z0-9.-]+$/.test(caddy?.environment?.[chave] ?? ""), caddy?.environment?.[chave] ?? "ausente");
  }
  const tunel = servicos.cloudflared;
  conferir(r("cloudflared presente no modelo"), tunel !== undefined);
  // api-b included: both environments run two instances, so the deploy never takes the API down (O06).
  const comPerfil = Object.entries(servicos).filter(([, s]) => s.profiles !== undefined).map(([n]) => n);
  conferir(r("nenhum serviço atrás de perfil"), comPerfil.length === 0, comPerfil.join(", "));
  conferir(r("cloudflared com 2 réplicas (CA4)"), tunel?.deploy?.replicas === 2, tunel?.deploy?.replicas);
  conferir(r("cloudflared com o config.yml versionado montado só leitura"), (tunel?.volumes ?? []).some((v) => v.target === "/etc/cloudflared/config.yml" && v.read_only === true && /[\\/]cloudflared[\\/]config\.yml$/.test(v.source ?? "")));
  const idTunel = env.get("PAF_TUNEL_ID") ?? "";
  conferir(r("PAF_TUNEL_ID é um UUID"), UUID.test(idTunel), idTunel || "ausente");
  conferir(r("cloudflared roda o túnel de PAF_TUNEL_ID"), (tunel?.command ?? []).at(-1) === idTunel && (tunel?.command ?? []).at(-2) === "run", JSON.stringify(tunel?.command));
  const credenciais = credenciaisDoTunel(tunel?.environment?.TUNNEL_CRED_CONTENTS);
  conferir(r("TUNNEL_CRED_CONTENTS do arquivo do túnel é o JSON de credenciais (AccountTag, TunnelSecret, TunnelID)"), credenciais !== undefined);
  conferir(r("TUNNEL_CRED_CONTENTS é do túnel de PAF_TUNEL_ID"), credenciais?.TunnelID === idTunel, credenciais?.TunnelID);
  // The tunnel file is this environment's own: ambientes/<x>.env pairs with ambientes/<x>.tunel.env.
  const arquivoTunel = env.get("PAF_ARQUIVO_TUNEL") ?? "";
  const esperadoTunel = (env.get("PAF_ARQUIVO_AMBIENTE") ?? "").replace(/\.env$/, ".tunel.env");
  conferir(r("arquivo do túnel é o do próprio ambiente"), arquivoTunel !== "" && arquivoTunel === esperadoTunel, `${arquivoTunel} x ${esperadoTunel}`);
  // A3: the credential reaches cloudflared only; the API and the migrator get the whole environment file.
  const comCredencial = SERVICOS_DOTNET.filter((n) => Object.entries(servicos[n]?.environment ?? {})
    .some(([k, v]) => k === "TUNNEL_CRED_CONTENTS" || k.startsWith("PAF_TUNEL_CRED") || (credenciais !== undefined && String(v).includes(credenciais.TunnelSecret))));
  conferir(r("API e migrator sem a credencial do túnel"), comCredencial.length === 0, comCredencial.join(", "));

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
    conferir(s("PafHub só em WebSockets (PAF__TempoReal__Transportes)"), soWebSockets(e.PAF__TempoReal__Transportes), e.PAF__TempoReal__Transportes);
    conferir(s("QuotaOptions__Enabled=false explícito"), typeof e.QuotaOptions__Enabled === "string" && e.QuotaOptions__Enabled.toLowerCase() === "false", e.QuotaOptions__Enabled ?? "ausente");

    const chave = e.JwtOptions__SigningKey ?? "";
    conferir(s("chave de assinatura presente"), chave.length > 0);
    conferir(s("chave de assinatura com 32+ caracteres"), chave.length >= 32, `${chave.length} caracteres`);
    // The kit signs with UTF-8 and validates with ASCII (ADR 0008): any other byte breaks every token.
    conferir(s("chave de assinatura só ASCII imprimível"), /^[\x21-\x7E]*$/.test(chave));
    conferir(s("chave de assinatura não é o exemplo"), !chave.toLowerCase().includes("replace-with"));
    conferir(s("AccessTokenMinutes explícito"), inteiroNoIntervalo(e.JwtOptions__AccessTokenMinutes, 1, 1440), e.JwtOptions__AccessTokenMinutes ?? "ausente");

    // A4: a network here would trust the bridge gateway too, i.e. any process on the host hitting the loopback port.
    const redesConfiaveis = Object.entries(e).filter(([k, v]) => k.startsWith("TrustedProxyOptions__KnownNetworks__") && v).map(([, v]) => v);
    conferir(s("KnownNetworks vazio (só o Caddy é proxy confiável)"), redesConfiaveis.length === 0, redesConfiaveis.join(","));
    const proxiesConfiaveis = Object.entries(e).filter(([k, v]) => k.startsWith("TrustedProxyOptions__KnownProxies__") && v).map(([, v]) => v);
    conferir(s("KnownProxies = IP fixo do Caddy"), proxiesConfiaveis.length === 1 && proxiesConfiaveis[0] === ipCaddy, `${proxiesConfiaveis.join(",")} x ${ipCaddy}`);

    // Absent = the kit's appsettings.Production.json value (Auth 10, Ip 300, Tenant 1000), all below the floor.
    for (const [particao, piso] of Object.entries(PISOS_LIMITE_TAXA)) {
      const limite = e[`RateLimitingOptions__${particao}__PermitLimit`];
      conferir(s(`limite de taxa ${particao} >= ${piso} (O12)`), inteiroNoIntervalo(limite, piso, Number.MAX_SAFE_INTEGER), limite ?? "ausente");
    }

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

const IPV4 = /^\d+\.\d+\.\d+\.\d+$/;
const CIDR = /^\d+\.\d+\.\d+\.\d+\/\d+$/;

// First and last address of a CIDR block, as numbers.
function faixaCidr(cidr) {
  const [ip, bits] = cidr.split("/");
  const tamanho = 2 ** (32 - Number(bits));
  const inicio = Math.floor(ipv4ParaNumero(ip) / tamanho) * tamanho;
  return [inicio, inicio + tamanho - 1];
}

function numeroParaIpv4(n) {
  return [24, 16, 8, 0].map((d) => Math.floor(n / 2 ** d) % 256).join(".");
}

function dentro(ip, cidr) {
  if (typeof ip !== "string" || !IPV4.test(ip) || typeof cidr !== "string" || !CIDR.test(cidr)) return false;
  const [inicio, fim] = faixaCidr(cidr);
  return ipv4ParaNumero(ip) >= inicio && ipv4ParaNumero(ip) <= fim;
}

function contem(externa, interna) {
  const [ei, ef] = faixaCidr(externa);
  const [ii, ifim] = faixaCidr(interna);
  return ii >= ei && ifim <= ef;
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

conferir("appsettings.Production.json: PafHub só em WebSockets", soWebSockets(appsettingsDoKit.PAF?.TempoReal?.Transportes), appsettingsDoKit.PAF?.TempoReal?.Transportes);
for (const ambiente of [producao, homologacao]) if (ambiente !== undefined) conferirAmbiente(ambiente);

function conferirCruzado(prod, homolog) {
  conferir("cruzado: nomes de projeto diferentes", prod.name !== homolog.name, `${prod.name} x ${homolog.name}`);
  conferir("cruzado: subnets sem sobreposição", !sobrepoe(subrede(prod), subrede(homolog)), `${subrede(prod)} x ${subrede(homolog)}`);
  const portasRepetidas = portasPublicadas(prod).filter((p) => portasPublicadas(homolog).includes(p));
  conferir("cruzado: portas publicadas distintas", portasRepetidas.length === 0, portasRepetidas.join(", "));
  conferir("cruzado: mesmo AccessTokenMinutes", api(prod).JwtOptions__AccessTokenMinutes === api(homolog).JwtOptions__AccessTokenMinutes, `${api(prod).JwtOptions__AccessTokenMinutes} x ${api(homolog).JwtOptions__AccessTokenMinutes}`);
  // Same key, issuer and audience would make a homologation token valid in production.
  conferir("cruzado: chaves de assinatura diferentes", api(prod).JwtOptions__SigningKey !== api(homolog).JwtOptions__SigningKey);
  conferir("cruzado: buckets do R2 diferentes", api(prod).Storage__S3__Bucket !== api(homolog).Storage__S3__Bucket, api(prod).Storage__S3__Bucket);
  const bucketBackup = (modelo) => modelo.services?.backup?.environment?.PAF_BACKUP_BUCKET;
  conferir("cruzado: buckets de backup diferentes", bucketBackup(prod) !== bucketBackup(homolog), bucketBackup(prod));
  // One tunnel per environment (O03): a leaked homologation credential never reaches production.
  const tunelDe = (modelo) => modelo.services?.cloudflared?.command?.at(-1);
  conferir("cruzado: túneis diferentes", tunelDe(prod) !== tunelDe(homolog), tunelDe(prod));
  // Same service name would mix homologation telemetry into production's in New Relic.
  conferir("cruzado: OTEL_SERVICE_NAME diferentes", api(prod).OTEL_SERVICE_NAME !== api(homolog).OTEL_SERVICE_NAME, api(prod).OTEL_SERVICE_NAME);
}

const cruzadasPuladas = producao === undefined || homologacao === undefined;
if (cruzadasPuladas) {
  console.log(`SKIPPED cruzado: só ${(producao ?? homologacao).rotulo} informado; nomes, subnets, portas, chaves, buckets, túneis e OTEL_SERVICE_NAME entre ambientes não conferidos`);
} else {
  conferirCruzado(producao.modelo, homologacao.modelo);
}

console.log(falhas > 0 ? `VERIFICAR-AMBIENTE FALHOU (${falhas})` : cruzadasPuladas ? "VERIFICAR-AMBIENTE OK (conferências cruzadas SKIPPED)" : "VERIFICAR-AMBIENTE OK");
process.exit(falhas === 0 ? 0 : 1);
