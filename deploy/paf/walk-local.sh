#!/usr/bin/env bash
# LOCAL run of one PAF walk against a REAL stack: walk-ci.sh's recipe plus RustFS with Storage:Provider=s3, so every
# clients/dashboard/tests/e2e/paf-*.walk.mjs runs through it (the walks are not in CI: owner's decision). Ephemeral
# PostgreSQL, Valkey and RustFS, migrations and seed-demo by the DbMigrator, the API and the dashboard on free local
# ports, then the walk in Chrome. Exit code = the walk's; any setup failure exits non-zero before the walk.
#
# Usage: PAF_SENHA_DEMO=<secret> PAF_WALK=tests/e2e/<walk>.mjs deploy/paf/walk-local.sh
#   PAF_WEB_BUILD=1    the walk needs the production build (`npm run build` + `vite preview`, dist/config.json apiBase
#                      = the API) instead of the Vite dev server: paf-navegacao, paf-fichas-do-dia, paf-selecionar-ficha.
#   PAF_PULAR_BUILD=1  skip the dotnet build (Release binaries already built from this tree).
#   API settings the walk asks for go in the environment and reach the API as they are, e.g. the M08 walk:
#     PAF__Midia__MargemLeitura=00:00:40 PAF__Midia__PisoValidadeUrlLeitura=00:00:01 \
#       PAF_WALK=tests/e2e/paf-midia-painel.walk.mjs PAF_SENHA_DEMO=<secret> deploy/paf/walk-local.sh
# The walk gets PAF_API, PAF_WEB, PAF_S3, PAF_S3_BUCKET, PAF_SENHA_DEMO, PAF_PG and PAF_PG_CONTAINER (the PostgreSQL
# container). Logs stay in the directory printed at the start. Leaves nothing running: processes and containers go
# on EXIT. One run at a time is the safe default (each run uses its own names and free ports, but builds share bin/).
set -euo pipefail

SENHA_PADRAO_DO_KIT='Password123!'
if [ -z "${PAF_SENHA_DEMO:-}" ]; then echo "FAIL segredo: PAF_SENHA_DEMO ausente ou vazio" >&2; exit 2; fi
if [ "$PAF_SENHA_DEMO" = "$SENHA_PADRAO_DO_KIT" ]; then echo "FAIL segredo: PAF_SENHA_DEMO igual à senha demo pública do kit" >&2; exit 2; fi
if [ -z "${PAF_WALK:-}" ]; then echo "FAIL uso: PAF_WALK ausente (ex.: tests/e2e/paf-midias.walk.mjs)" >&2; exit 2; fi

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DASHBOARD="$RAIZ/clients/dashboard"
if [ ! -f "$DASHBOARD/$PAF_WALK" ]; then echo "FAIL uso: $DASHBOARD/$PAF_WALK não existe" >&2; exit 2; fi
if [ ! -d "$DASHBOARD/node_modules/vite" ]; then echo "FAIL dashboard: rode npm ci em clients/dashboard antes" >&2; exit 2; fi

SUFIXO="paf-walk-local-$$"
PG="$SUFIXO-pg"; VALKEY="$SUFIXO-valkey"; S3C="$SUFIXO-s3"
SENHA_PG="walk-pg-$$"; SENHA_VALKEY="walk-valkey-$$"
BUCKET="paf-walk-local"
LOGS="$(mktemp -d)"
echo "--- logs em $LOGS"
LIMITE_PRONTO_S=60; LIMITE_BUILD_S=900; LIMITE_MIGRATOR_S=600; LIMITE_API_S=180; LIMITE_WEB_S=60; LIMITE_WALK_S=900
PID_API=""; PID_WEB=""

limpar() {
  local codigo=$?
  echo "--- limpeza (código $codigo)"
  if [ -n "$PID_WEB" ]; then kill "$PID_WEB" || echo "servidor do dashboard já tinha saído"; fi
  if [ -n "$PID_API" ]; then kill "$PID_API" || echo "api já tinha saído"; fi
  docker rm -f "$PG" "$VALKEY" "$S3C" >/dev/null || echo "contêineres já removidos"
  if [ "$codigo" -ne 0 ]; then
    echo "--- últimas linhas do log da API"; tail -n 60 "$LOGS/api.log" || echo "(sem log da API)"
    echo "--- últimas linhas do log do dashboard"; tail -n 20 "$LOGS/web.log" || echo "(sem log do dashboard)"
  fi
  exit "$codigo"
}
trap limpar EXIT

aguardar_conteiner() {
  local nome="$1"; shift; local i
  for ((i = 0; i < LIMITE_PRONTO_S; i++)); do
    if docker exec "$nome" "$@" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  echo "FAIL infra: $nome não ficou pronto em ${LIMITE_PRONTO_S}s" >&2; return 1
}
# pid empty = a container, no process to watch.
aguardar_http() {
  local url="$1" limite="$2" pid="$3"; local i
  for ((i = 0; i < limite; i++)); do
    if [ -n "$pid" ] && ! kill -0 "$pid" 2>/dev/null; then echo "FAIL processo de $url saiu antes de responder" >&2; return 1; fi
    if curl -fs -o /dev/null --max-time 5 "$url"; then return 0; fi
    sleep 1
  done
  echo "FAIL $url não respondeu 2xx em ${limite}s" >&2; return 1
}
porta_livre() { node -e 'const s=require("node:net").createServer().listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})'; }

echo "--- 1. PostgreSQL, Valkey e RustFS efêmeros"
docker run -d --name "$PG" -p 127.0.0.1::5432 -e TZ=America/Sao_Paulo -e POSTGRES_DB=fsh -e POSTGRES_USER=fsh \
  -e POSTGRES_PASSWORD="$SENHA_PG" postgres:18-alpine -c timezone=America/Sao_Paulo >/dev/null
docker run -d --name "$VALKEY" -p 127.0.0.1::6379 valkey/valkey:9.1.0-alpine valkey-server --requirepass "$SENHA_VALKEY" >/dev/null
docker run -d --name "$S3C" -p 127.0.0.1::9000 -e RUSTFS_ACCESS_KEY=rustfsadmin -e RUSTFS_SECRET_KEY=rustfsadmin \
  rustfs/rustfs:1.0.0 >/dev/null
aguardar_conteiner "$PG" pg_isready -U fsh -d fsh
aguardar_conteiner "$VALKEY" valkey-cli -a "$SENHA_VALKEY" --no-auth-warning PING
PORTA_PG="$(docker port "$PG" 5432/tcp | head -n 1 | sed 's/.*://')"
PORTA_VALKEY="$(docker port "$VALKEY" 6379/tcp | head -n 1 | sed 's/.*://')"
PORTA_S3="$(docker port "$S3C" 9000/tcp | head -n 1 | sed 's/.*://')"
URL_S3="http://127.0.0.1:$PORTA_S3"
aguardar_http "$URL_S3/health" "$LIMITE_PRONTO_S" ""

echo "--- 2. build da API e do DbMigrator"
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
if [ "${PAF_PULAR_BUILD:-0}" != 1 ]; then
  for projeto in FSH.Starter.DbMigrator FSH.Starter.Api; do
    timeout "$LIMITE_BUILD_S" dotnet build "$RAIZ/src/Host/$projeto/$projeto.csproj" -c Release \
      -p:NuGetAudit=false -nodeReuse:false -v:q -nologo
  done
fi

export DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development TZ=America/Sao_Paulo
export DatabaseOptions__ConnectionString="Host=127.0.0.1;Port=$PORTA_PG;Database=fsh;Username=fsh;Password=$SENHA_PG;Timeout=15;Command Timeout=120"
export CachingOptions__Redis="127.0.0.1:$PORTA_VALKEY,password=$SENHA_VALKEY,connectTimeout=5000,syncTimeout=5000"
export Seed__DefaultAdminPassword="$PAF_SENHA_DEMO" Seed__DemoPassword="$PAF_SENHA_DEMO"
export Storage__Provider=s3 Storage__S3__Bucket="$BUCKET" Storage__S3__ServiceUrl="$URL_S3" \
  Storage__S3__AccessKey=rustfsadmin Storage__S3__SecretKey=rustfsadmin Storage__S3__ForcePathStyle=true \
  Storage__S3__PublicRead=false Storage__S3__Region=us-east-1

echo "--- 3. migrações e seed-demo"
MIGRATOR="$RAIZ/src/Host/FSH.Starter.DbMigrator/bin/Release/net10.0/FSH.Starter.DbMigrator.dll"
(cd "$RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$MIGRATOR" apply --seed) >"$LOGS/migrator.log" 2>&1
(cd "$RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$MIGRATOR" seed-demo) >>"$LOGS/migrator.log" 2>&1

echo "--- 4. API e dashboard"
PORTA_API="$(porta_livre)"; PORTA_WEB="$(porta_livre)"
URL_API="http://localhost:$PORTA_API"; URL_WEB="http://localhost:$PORTA_WEB"
(
  cd "$RAIZ/src/Host/FSH.Starter.Api"
  ASPNETCORE_URLS="$URL_API" CorsOptions__AllowedOrigins__0="$URL_WEB" FrontendOptions__AllowedOrigins__0="$URL_WEB" \
    exec dotnet "$RAIZ/src/Host/FSH.Starter.Api/bin/Release/net10.0/FSH.Starter.Api.dll"
) >"$LOGS/api.log" 2>&1 &
PID_API=$!
if [ "${PAF_WEB_BUILD:-0}" = 1 ]; then
  (cd "$DASHBOARD" && timeout "$LIMITE_BUILD_S" npm run build) >"$LOGS/web-build.log" 2>&1
  (cd "$DASHBOARD" && URL_API="$URL_API" node -e '
    const fs = require("node:fs");
    const arquivo = "dist/config.json";
    fs.writeFileSync(arquivo, JSON.stringify({ ...JSON.parse(fs.readFileSync(arquivo, "utf8")), apiBase: process.env.URL_API }));
  ')
  (cd "$DASHBOARD" && exec node node_modules/vite/bin/vite.js preview --port "$PORTA_WEB" --strictPort) >"$LOGS/web.log" 2>&1 &
else
  (cd "$DASHBOARD" && VITE_API_BASE_URL="$URL_API" exec node node_modules/vite/bin/vite.js --port "$PORTA_WEB" --strictPort) >"$LOGS/web.log" 2>&1 &
fi
PID_WEB=$!
aguardar_http "$URL_API/health/ready" "$LIMITE_API_S" "$PID_API"
aguardar_http "$URL_WEB/config.json" "$LIMITE_WEB_S" "$PID_WEB"

echo "--- 5. $PAF_WALK"
cd "$DASHBOARD"
PAF_API="$URL_API" PAF_WEB="$URL_WEB" PAF_S3="$URL_S3" PAF_S3_BUCKET="$BUCKET" PAF_PG="$PG" PAF_PG_CONTAINER="$PG" \
  timeout "$LIMITE_WALK_S" node "$PAF_WALK"
echo "WALK-LOCAL OK $PAF_WALK"
