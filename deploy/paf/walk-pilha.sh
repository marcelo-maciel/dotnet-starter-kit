# shellcheck shell=bash
# The PAF walk stack, the only copy of its recipe, sourced by walk-local.sh (one walk) and walk-ci.sh (every walk of a
# CI matrix part on one stack). Ephemeral PostgreSQL, Valkey and RustFS (Storage:Provider=s3) and the dashboard
# servers come up once; each walk then gets a fresh seed-demo database (dropped and recreated, migrations, seed and
# seed-demo by the DbMigrator, Valkey flushed) and its own API process. The API is restarted per walk rather than kept
# up: its in-memory caches (tenant store, HybridCache L1) would hold the previous database's ids, and the M08 walk
# starts it with its own short media URL settings.
#
# The caller sets `set -euo pipefail`, calls pilha_validar_segredo, sets `trap pilha_limpar EXIT`, then pilha_subir and
# runs each walk as `( pilha_walk <arquivo> )`: the subshell's own EXIT trap stops that walk's API.
# The walk gets PAF_API, PAF_WEB, PAF_S3, PAF_S3_BUCKET, PAF_SENHA_DEMO, PAF_PG and PAF_PG_CONTAINER (the PostgreSQL
# container). Logs stay in the directory printed by pilha_subir.

PILHA_RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PILHA_DASHBOARD="$PILHA_RAIZ/clients/dashboard"
PILHA_SUFIXO="paf-walk-$$"
PILHA_PG="$PILHA_SUFIXO-pg"; PILHA_VALKEY="$PILHA_SUFIXO-valkey"; PILHA_S3C="$PILHA_SUFIXO-s3"
PILHA_SENHA_PG="walk-pg-$$"; PILHA_SENHA_VALKEY="walk-valkey-$$"
PILHA_BUCKET="paf-walk"
PILHA_MIGRATOR="$PILHA_RAIZ/src/Host/FSH.Starter.DbMigrator/bin/Release/net10.0/FSH.Starter.DbMigrator.dll"
PILHA_API_DLL="$PILHA_RAIZ/src/Host/FSH.Starter.Api/bin/Release/net10.0/FSH.Starter.Api.dll"
# Ceilings for every blocking step, so a hung dependency fails the run instead of eating the runner's 6 h.
LIMITE_PRONTO_S=60; LIMITE_BUILD_S=900; LIMITE_MIGRATOR_S=600; LIMITE_API_S=180; LIMITE_WEB_S=60; LIMITE_WALK_S=900
PILHA_LOGS=""; PID_WEB_DEV=""; PID_WEB_BUILD=""; PID_API=""
URL_API=""; URL_WEB_DEV=""; URL_WEB_BUILD=""; URL_S3=""

pilha_validar_segredo() {
  # The kit's public demo password (appsettings.Development.json): a run seeded with it would pass with a password
  # anyone can read (O08: never the default password).
  local senha_padrao_do_kit='Password123!'
  if [ -z "${PAF_SENHA_DEMO:-}" ]; then echo "FAIL segredo: PAF_SENHA_DEMO ausente ou vazio (secret PAF_SENHA_DEMO do repositório)" >&2; exit 2; fi
  if [ "$PAF_SENHA_DEMO" = "$senha_padrao_do_kit" ]; then echo "FAIL segredo: PAF_SENHA_DEMO igual à senha demo pública do kit" >&2; exit 2; fi
}

# What each walk's header asks of the stack, by file name. The production build (`npm run build` + `vite preview`,
# dist/config.json apiBase = the API) instead of the Vite dev server, for the walks that check the built bundle.
pilha_precisa_build_web() {
  case "$(basename "$1")" in
    paf-navegacao.walk.mjs | paf-fichas-do-dia.walk.mjs | paf-selecionar-ficha.walk.mjs) return 0 ;;
    *) return 1 ;;
  esac
}

# API settings a walk asks for: short media read URLs so their expiry happens inside the M08 walk.
pilha_ambiente_api_do_walk() {
  case "$(basename "$1")" in
    paf-midia-painel.walk.mjs) printf '%s\n' PAF__Midia__MargemLeitura=00:00:40 PAF__Midia__PisoValidadeUrlLeitura=00:00:01 ;;
    *) ;;
  esac
}

pilha_limpar() {
  local codigo=$?
  echo "--- limpeza (código $codigo)"
  if [ -n "$PID_WEB_DEV" ]; then kill "$PID_WEB_DEV" || echo "servidor de desenvolvimento do dashboard já tinha saído"; fi
  if [ -n "$PID_WEB_BUILD" ]; then kill "$PID_WEB_BUILD" || echo "vite preview já tinha saído"; fi
  docker rm -f "$PILHA_PG" "$PILHA_VALKEY" "$PILHA_S3C" >/dev/null || echo "contêineres já removidos"
  if [ "$codigo" -ne 0 ] && [ -n "$PILHA_LOGS" ]; then
    echo "--- últimas linhas do log do dashboard"; tail -n 20 "$PILHA_LOGS/web.log" || echo "(sem log do dashboard)"
  fi
  exit "$codigo"
}

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

# Containers, the dotnet build (skipped with PAF_PULAR_BUILD=1), the environment of every DbMigrator and API run, and
# the dashboard servers the walks need: <com_dev> and <com_build> are 1 or 0.
pilha_subir() {
  local com_dev="$1" com_build="$2"
  if [ ! -d "$PILHA_DASHBOARD/node_modules/vite" ]; then echo "FAIL dashboard: rode npm ci em clients/dashboard antes" >&2; return 2; fi
  PILHA_LOGS="$(mktemp -d)"
  echo "--- logs em $PILHA_LOGS"

  echo "--- PostgreSQL, Valkey e RustFS efêmeros"
  docker run -d --name "$PILHA_PG" -p 127.0.0.1::5432 -e TZ=America/Sao_Paulo -e POSTGRES_DB=fsh -e POSTGRES_USER=fsh \
    -e POSTGRES_PASSWORD="$PILHA_SENHA_PG" postgres:18-alpine -c timezone=America/Sao_Paulo >/dev/null
  docker run -d --name "$PILHA_VALKEY" -p 127.0.0.1::6379 valkey/valkey:9.1.0-alpine valkey-server --requirepass "$PILHA_SENHA_VALKEY" >/dev/null
  docker run -d --name "$PILHA_S3C" -p 127.0.0.1::9000 -e RUSTFS_ACCESS_KEY=rustfsadmin -e RUSTFS_SECRET_KEY=rustfsadmin \
    rustfs/rustfs:1.0.0 >/dev/null
  aguardar_conteiner "$PILHA_PG" pg_isready -U fsh -d fsh
  aguardar_conteiner "$PILHA_VALKEY" valkey-cli -a "$PILHA_SENHA_VALKEY" --no-auth-warning PING
  local porta_pg porta_valkey porta_s3
  porta_pg="$(docker port "$PILHA_PG" 5432/tcp | head -n 1 | sed 's/.*://')"
  porta_valkey="$(docker port "$PILHA_VALKEY" 6379/tcp | head -n 1 | sed 's/.*://')"
  porta_s3="$(docker port "$PILHA_S3C" 9000/tcp | head -n 1 | sed 's/.*://')"
  URL_S3="http://127.0.0.1:$porta_s3"
  aguardar_http "$URL_S3/health" "$LIMITE_PRONTO_S" ""
  # The API does not create its bucket; walks that upload without creating it (paf-logotipo) need it there.
  curl -fsS -o /dev/null --max-time 10 --aws-sigv4 "aws:amz:us-east-1:s3" --user rustfsadmin:rustfsadmin -X PUT "$URL_S3/$PILHA_BUCKET"

  echo "--- build da API e do DbMigrator"
  export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
  if [ "${PAF_PULAR_BUILD:-0}" != 1 ]; then
    local projeto
    for projeto in FSH.Starter.DbMigrator FSH.Starter.Api; do
      timeout "$LIMITE_BUILD_S" dotnet build "$PILHA_RAIZ/src/Host/$projeto/$projeto.csproj" -c Release \
        -p:NuGetAudit=false -nodeReuse:false -v:q -nologo
    done
  fi

  # Development: seed-demo refuses any other environment (DbMigrator guard), and the API's Development settings are the
  # ones the walks were written against. Everything secret or host-specific comes from here, never from the file.
  export DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development TZ=America/Sao_Paulo
  export DatabaseOptions__ConnectionString="Host=127.0.0.1;Port=$porta_pg;Database=fsh;Username=fsh;Password=$PILHA_SENHA_PG;Timeout=15;Command Timeout=120"
  export CachingOptions__Redis="127.0.0.1:$porta_valkey,password=$PILHA_SENHA_VALKEY,connectTimeout=5000,syncTimeout=5000"
  export Seed__DefaultAdminPassword="$PAF_SENHA_DEMO" Seed__DemoPassword="$PAF_SENHA_DEMO"
  export Storage__Provider=s3 Storage__S3__Bucket="$PILHA_BUCKET" Storage__S3__ServiceUrl="$URL_S3" \
    Storage__S3__AccessKey=rustfsadmin Storage__S3__SecretKey=rustfsadmin Storage__S3__ForcePathStyle=true \
    Storage__S3__PublicRead=false Storage__S3__Region=us-east-1

  # One API port for the whole run: the dashboard servers point at it while the API restarts per walk.
  URL_API="http://localhost:$(porta_livre)"
  local cors=() indice=0
  echo "--- dashboard"
  if [ "$com_dev" = 1 ]; then
    URL_WEB_DEV="http://localhost:$(porta_livre)"
    (cd "$PILHA_DASHBOARD" && VITE_API_BASE_URL="$URL_API" exec node node_modules/vite/bin/vite.js --port "${URL_WEB_DEV##*:}" --strictPort) >"$PILHA_LOGS/web.log" 2>&1 &
    PID_WEB_DEV=$!
    cors+=("CorsOptions__AllowedOrigins__$indice=$URL_WEB_DEV" "FrontendOptions__AllowedOrigins__$indice=$URL_WEB_DEV")
    indice=$((indice + 1))
  fi
  if [ "$com_build" = 1 ]; then
    URL_WEB_BUILD="http://localhost:$(porta_livre)"
    (cd "$PILHA_DASHBOARD" && timeout "$LIMITE_BUILD_S" npm run build) >"$PILHA_LOGS/web-build.log" 2>&1
    (cd "$PILHA_DASHBOARD" && URL_API="$URL_API" node -e '
      const fs = require("node:fs");
      const arquivo = "dist/config.json";
      fs.writeFileSync(arquivo, JSON.stringify({ ...JSON.parse(fs.readFileSync(arquivo, "utf8")), apiBase: process.env.URL_API }));
    ')
    (cd "$PILHA_DASHBOARD" && exec node node_modules/vite/bin/vite.js preview --port "${URL_WEB_BUILD##*:}" --strictPort) >>"$PILHA_LOGS/web.log" 2>&1 &
    PID_WEB_BUILD=$!
    cors+=("CorsOptions__AllowedOrigins__$indice=$URL_WEB_BUILD" "FrontendOptions__AllowedOrigins__$indice=$URL_WEB_BUILD")
  fi
  export "${cors[@]}"
  if [ -n "$PID_WEB_DEV" ]; then aguardar_http "$URL_WEB_DEV/config.json" "$LIMITE_WEB_S" "$PID_WEB_DEV"; fi
  if [ -n "$PID_WEB_BUILD" ]; then aguardar_http "$URL_WEB_BUILD/config.json" "$LIMITE_WEB_S" "$PID_WEB_BUILD"; fi
}

pilha_parar_api() {
  local codigo=$?
  if [ -n "$PID_API" ]; then
    kill "$PID_API" || echo "api já tinha saído"
    wait "$PID_API" || echo "api saiu (fim do walk)"
  fi
  if [ "$codigo" -ne 0 ]; then echo "--- últimas linhas do log da API"; tail -n 60 "$PILHA_API_LOG" || echo "(sem log da API)"; fi
  exit "$codigo"
}

# One walk on a fresh database and its own API process. Run in a subshell: `( pilha_walk tests/e2e/<walk>.mjs )`.
pilha_walk() {
  local walk="$1" nome
  nome="$(basename "$walk" .mjs)"
  PILHA_API_LOG="$PILHA_LOGS/api-$nome.log"
  trap pilha_parar_api EXIT

  echo "--- $nome: banco novo, migrações e seed-demo"
  docker exec "$PILHA_PG" psql -U fsh -d postgres -v ON_ERROR_STOP=1 -q \
    -c 'DROP DATABASE IF EXISTS fsh WITH (FORCE)' -c 'CREATE DATABASE fsh' >/dev/null
  docker exec "$PILHA_VALKEY" valkey-cli -a "$PILHA_SENHA_VALKEY" --no-auth-warning FLUSHALL >/dev/null
  (cd "$PILHA_RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$PILHA_MIGRATOR" apply --seed) >"$PILHA_LOGS/migrator-$nome.log" 2>&1
  (cd "$PILHA_RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$PILHA_MIGRATOR" seed-demo) >>"$PILHA_LOGS/migrator-$nome.log" 2>&1

  echo "--- $nome: API"
  local ambiente_walk=()
  mapfile -t ambiente_walk < <(pilha_ambiente_api_do_walk "$walk")
  (
    cd "$PILHA_RAIZ/src/Host/FSH.Starter.Api"
    # Exported here, not through `env`: one process between this PID and dotnet, so the kill at the end reaches it.
    for definicao in "${ambiente_walk[@]}"; do export "${definicao?}"; done
    ASPNETCORE_URLS="$URL_API" exec dotnet "$PILHA_API_DLL"
  ) >"$PILHA_API_LOG" 2>&1 &
  PID_API=$!
  aguardar_http "$URL_API/health/ready" "$LIMITE_API_S" "$PID_API"

  local url_web="$URL_WEB_DEV"
  if pilha_precisa_build_web "$walk"; then url_web="$URL_WEB_BUILD"; fi
  echo "--- $nome: walk"
  (cd "$PILHA_DASHBOARD" && PAF_API="$URL_API" PAF_WEB="$url_web" PAF_S3="$URL_S3" PAF_S3_BUCKET="$PILHA_BUCKET" \
    PAF_PG="$PILHA_PG" PAF_PG_CONTAINER="$PILHA_PG" timeout "$LIMITE_WALK_S" node "$walk")
  echo "WALK OK $walk"
}
