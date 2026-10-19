#!/usr/bin/env bash
# CI run of the first slice walk (O08) against a REAL API: ephemeral PostgreSQL and Valkey, migrations and seed-demo
# by the DbMigrator, the API and the dashboard dev server on free local ports, then
# clients/dashboard/tests/e2e/paf-primeira-fatia.walk.mjs in Chrome. Exit code = the walk's (0 only when every
# check passed); any setup failure exits non-zero before the walk.
#
# Usage: PAF_SENHA_DEMO=<secret> deploy/paf/walk-ci.sh
# Needs: docker, dotnet SDK, node 22, `npm ci` done in clients/dashboard, Google Chrome (the walk launches
# channel "chrome"). Leaves nothing running: processes, containers and network go on EXIT.
set -euo pipefail

# The kit's public demo password (appsettings.Development.json). A CI seeded with it would pass with a password
# anyone can read, so the secret has to exist and be different (O08: never the default password).
SENHA_PADRAO_DO_KIT='Password123!'
if [ -z "${PAF_SENHA_DEMO:-}" ]; then
  echo "FAIL segredo: PAF_SENHA_DEMO ausente ou vazio (secret PAF_SENHA_DEMO do repositório)" >&2
  exit 2
fi
if [ "$PAF_SENHA_DEMO" = "$SENHA_PADRAO_DO_KIT" ]; then
  echo "FAIL segredo: PAF_SENHA_DEMO igual à senha demo pública do kit" >&2
  exit 2
fi

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DASHBOARD="$RAIZ/clients/dashboard"
if [ ! -d "$DASHBOARD/node_modules/vite" ]; then
  echo "FAIL dashboard: rode npm ci em clients/dashboard antes" >&2
  exit 2
fi

SUFIXO="paf-walk-$$"
REDE="$SUFIXO-rede"
PG="$SUFIXO-pg"
VALKEY="$SUFIXO-valkey"
SENHA_PG="walk-pg-$$"
SENHA_VALKEY="walk-valkey-$$"
LOGS="$(mktemp -d)"
# Ceilings for every blocking step, so a hung dependency fails the job instead of eating the runner's 6 h.
LIMITE_PRONTO_S=60
LIMITE_BUILD_S=900
LIMITE_MIGRATOR_S=600
LIMITE_API_S=180
LIMITE_WEB_S=60
LIMITE_WALK_S=900
PID_API=""
PID_WEB=""

limpar() {
  local codigo=$?
  echo "--- limpeza"
  if [ -n "$PID_WEB" ]; then kill "$PID_WEB" || echo "vite já tinha saído"; fi
  if [ -n "$PID_API" ]; then kill "$PID_API" || echo "api já tinha saído"; fi
  docker rm -f "$PG" "$VALKEY" >/dev/null || echo "contêineres já removidos"
  docker network rm "$REDE" >/dev/null || echo "rede já removida"
  if [ "$codigo" -ne 0 ]; then
    echo "--- últimas linhas do log da API"; tail -n 60 "$LOGS/api.log" || echo "(sem log da API)"
    echo "--- últimas linhas do log do vite"; tail -n 20 "$LOGS/web.log" || echo "(sem log do vite)"
  fi
  rm -rf "$LOGS"
  exit "$codigo"
}
trap limpar EXIT

aguardar_conteiner() {
  local nome="$1"; shift
  local i
  for ((i = 0; i < LIMITE_PRONTO_S; i++)); do
    if docker exec "$nome" "$@" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  echo "FAIL infra: $nome não ficou pronto em ${LIMITE_PRONTO_S}s" >&2
  return 1
}

aguardar_http() {
  local url="$1" limite="$2" pid="$3"
  local i
  for ((i = 0; i < limite; i++)); do
    if ! kill -0 "$pid" 2>/dev/null; then
      echo "FAIL processo de $url saiu antes de responder" >&2
      return 1
    fi
    if curl -fs -o /dev/null --max-time 5 "$url"; then return 0; fi
    sleep 1
  done
  echo "FAIL $url não respondeu 2xx em ${limite}s" >&2
  return 1
}

porta_livre() {
  node -e 'const s=require("node:net").createServer().listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})'
}

echo "--- 1. PostgreSQL e Valkey efêmeros"
docker network create "$REDE" >/dev/null
docker run -d --name "$PG" --network "$REDE" -p 127.0.0.1::5432 \
  -e TZ=America/Sao_Paulo -e POSTGRES_DB=fsh -e POSTGRES_USER=fsh -e POSTGRES_PASSWORD="$SENHA_PG" \
  postgres:18-alpine -c timezone=America/Sao_Paulo >/dev/null
docker run -d --name "$VALKEY" --network "$REDE" -p 127.0.0.1::6379 \
  valkey/valkey:9.1.0-alpine valkey-server --requirepass "$SENHA_VALKEY" >/dev/null
aguardar_conteiner "$PG" pg_isready -U fsh -d fsh
aguardar_conteiner "$VALKEY" valkey-cli -a "$SENHA_VALKEY" --no-auth-warning PING
PORTA_PG="$(docker port "$PG" 5432/tcp | head -n 1 | sed 's/.*://')"
PORTA_VALKEY="$(docker port "$VALKEY" 6379/tcp | head -n 1 | sed 's/.*://')"

echo "--- 2. build da API e do DbMigrator"
for projeto in FSH.Starter.DbMigrator FSH.Starter.Api; do
  timeout "$LIMITE_BUILD_S" dotnet build "$RAIZ/src/Host/$projeto/$projeto.csproj" -c Release \
    -p:NuGetAudit=false -nodeReuse:false -v:q -nologo
done

# Development: seed-demo refuses any other environment (DbMigrator guard), and the API's Development settings are
# the ones the walk was written against. Everything secret or host-specific comes from here, never from the file.
export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_ENVIRONMENT=Development
export TZ=America/Sao_Paulo
export DatabaseOptions__ConnectionString="Host=127.0.0.1;Port=$PORTA_PG;Database=fsh;Username=fsh;Password=$SENHA_PG;Timeout=15;Command Timeout=120"
export CachingOptions__Redis="127.0.0.1:$PORTA_VALKEY,password=$SENHA_VALKEY,connectTimeout=5000,syncTimeout=5000"
export Seed__DefaultAdminPassword="$PAF_SENHA_DEMO"
export Seed__DemoPassword="$PAF_SENHA_DEMO"

echo "--- 3. migrações e seed-demo"
MIGRATOR="$RAIZ/src/Host/FSH.Starter.DbMigrator/bin/Release/net10.0/FSH.Starter.DbMigrator.dll"
(cd "$RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$MIGRATOR" apply --seed)
(cd "$RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$MIGRATOR" seed-demo)

echo "--- 4. API e dashboard"
PORTA_API="$(porta_livre)"
PORTA_WEB="$(porta_livre)"
URL_API="http://localhost:$PORTA_API"
URL_WEB="http://localhost:$PORTA_WEB"
(
  cd "$RAIZ/src/Host/FSH.Starter.Api"
  ASPNETCORE_URLS="$URL_API" \
  CorsOptions__AllowedOrigins__0="$URL_WEB" \
  FrontendOptions__AllowedOrigins__0="$URL_WEB" \
  exec dotnet "$RAIZ/src/Host/FSH.Starter.Api/bin/Release/net10.0/FSH.Starter.Api.dll"
) >"$LOGS/api.log" 2>&1 &
PID_API=$!
(
  cd "$DASHBOARD"
  VITE_API_BASE_URL="$URL_API" exec node node_modules/vite/bin/vite.js --port "$PORTA_WEB" --strictPort
) >"$LOGS/web.log" 2>&1 &
PID_WEB=$!
aguardar_http "$URL_API/health/ready" "$LIMITE_API_S" "$PID_API"
aguardar_http "$URL_WEB/config.json" "$LIMITE_WEB_S" "$PID_WEB"

# PAF_WALK runs another script through the same real API (O12: tests/e2e/paf-abertura-loja.medicao.mjs).
PAF_WALK="${PAF_WALK:-tests/e2e/paf-primeira-fatia.walk.mjs}"
echo "--- 5. $PAF_WALK"
cd "$DASHBOARD"
PAF_API="$URL_API" PAF_WEB="$URL_WEB" timeout "$LIMITE_WALK_S" node "$PAF_WALK"
echo "WALK-CI OK"
