#!/usr/bin/env bash
# O07 (RNF16): "imagem anterior contra esquema novo". The rolling deploy (O06) runs the migrator of the NEW release first
# and keeps the OLD API instance serving until it is replaced, so every expand migration has to leave the previous image
# working. This builds the API of <ref-anterior>, migrates and seeds an ephemeral PostgreSQL with the DbMigrator of the
# current tree, starts the old API on it and runs imagem-anterior-esquema-novo.mjs (totem emission, guichê call, painel).
#
# Usage: deploy/paf/imagem-anterior-esquema-novo.sh <ref-anterior>   (sha, tag or branch reachable in this clone)
# Needs: git, docker, dotnet SDK, node 22. Leaves nothing running: API, containers, network and the old tree go on EXIT.
# Exit: 0 every check passed (or the previous ref has no PAF module, printed as NAO-SE-APLICA); 1 a check or step failed;
# 2 usage error.
set -euo pipefail

if [ "$#" -ne 1 ] || [ -z "$1" ]; then
  echo "uso: $0 <ref-anterior>" >&2
  exit 2
fi
REF_ANTERIOR="$1"
RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if ! SHA_ANTERIOR="$(git -C "$RAIZ" rev-parse --verify --quiet "$REF_ANTERIOR^{commit}")"; then
  echo "FAIL ref: '$REF_ANTERIOR' não é um commit deste clone (no CI, checkout com fetch-depth suficiente)" >&2
  exit 2
fi
# The first PAF release has no previous PAF image to protect: the check does not apply, and says so instead of passing.
if [ -z "$(git -C "$RAIZ" ls-tree --name-only "$SHA_ANTERIOR" -- src/Modules/PAF/Modules.PAF/PafModule.cs)" ]; then
  echo "NAO-SE-APLICA: $SHA_ANTERIOR não tem o módulo PAF, não há imagem anterior do PAF a proteger"
  exit 0
fi

SUFIXO="paf-ensaio-$$"
REDE="$SUFIXO-rede"
PG="$SUFIXO-pg"
VALKEY="$SUFIXO-valkey"
SENHA_PG="ensaio-pg-$$"
SENHA_VALKEY="ensaio-valkey-$$"
# Fresh per run, never the kit's public demo password (O08 D1): the database lives only for this run, so no secret is needed.
SENHA_DEMO="Ens-$(od -An -tx1 -N12 /dev/urandom | tr -d ' \n')-Aa1!"
ANTIGA="$(mktemp -d)"
LOGS="$(mktemp -d)"
# Ceilings for every blocking step, so a hung dependency fails the job instead of eating the runner.
LIMITE_PRONTO_S=60
LIMITE_BUILD_S=900
LIMITE_MIGRATOR_S=600
LIMITE_API_S=180
LIMITE_ENSAIO_S=120
PID_API=""

# ponytail: infra helpers copied from walk-pilha.sh (owned by O08/O17); extract a sourced lib if a third script needs them.
limpar() {
  local codigo=$?
  echo "--- limpeza"
  if [ -n "$PID_API" ]; then kill "$PID_API" || echo "api anterior já tinha saído"; fi
  docker rm -f "$PG" "$VALKEY" >/dev/null || echo "contêineres já removidos"
  docker network rm "$REDE" >/dev/null || echo "rede já removida"
  if [ "$codigo" -ne 0 ]; then
    echo "--- últimas linhas do log da API anterior"; tail -n 60 "$LOGS/api.log" || echo "(sem log da API)"
  fi
  rm -rf "$ANTIGA" "$LOGS"
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

echo "--- 1. árvore anterior $SHA_ANTERIOR"
git -C "$RAIZ" archive "$SHA_ANTERIOR" | tar -x -C "$ANTIGA"

echo "--- 2. PostgreSQL e Valkey efêmeros"
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

echo "--- 3. build do DbMigrator novo e da API anterior"
timeout "$LIMITE_BUILD_S" dotnet build "$RAIZ/src/Host/FSH.Starter.DbMigrator/FSH.Starter.DbMigrator.csproj" -c Release \
  -p:NuGetAudit=false -nodeReuse:false -v:q -nologo
timeout "$LIMITE_BUILD_S" dotnet build "$ANTIGA/src/Host/FSH.Starter.Api/FSH.Starter.Api.csproj" -c Release \
  -p:NuGetAudit=false -nodeReuse:false -v:q -nologo

# Development: seed-demo refuses any other environment (DbMigrator guard). Secrets and hosts come from here, never a file.
export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_ENVIRONMENT=Development
export TZ=America/Sao_Paulo
export DatabaseOptions__ConnectionString="Host=127.0.0.1;Port=$PORTA_PG;Database=fsh;Username=fsh;Password=$SENHA_PG;Timeout=15;Command Timeout=120"
export CachingOptions__Redis="127.0.0.1:$PORTA_VALKEY,password=$SENHA_VALKEY,connectTimeout=5000,syncTimeout=5000"
export Seed__DefaultAdminPassword="$SENHA_DEMO"
export Seed__DemoPassword="$SENHA_DEMO"

echo "--- 4. esquema novo: migrações e seed-demo do DbMigrator atual"
MIGRATOR="$RAIZ/src/Host/FSH.Starter.DbMigrator/bin/Release/net10.0/FSH.Starter.DbMigrator.dll"
(cd "$RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$MIGRATOR" apply --seed)
(cd "$RAIZ/src/Host/FSH.Starter.DbMigrator" && timeout "$LIMITE_MIGRATOR_S" dotnet "$MIGRATOR" seed-demo)

echo "--- 5. API anterior sobre o esquema novo"
PORTA_API="$(porta_livre)"
URL_API="http://localhost:$PORTA_API"
(
  cd "$ANTIGA/src/Host/FSH.Starter.Api"
  ASPNETCORE_URLS="$URL_API" exec dotnet "$ANTIGA/src/Host/FSH.Starter.Api/bin/Release/net10.0/FSH.Starter.Api.dll"
) >"$LOGS/api.log" 2>&1 &
PID_API=$!
aguardar_http "$URL_API/health/ready" "$LIMITE_API_S" "$PID_API"

echo "--- 6. emissão, chamada e painel na API anterior"
PAF_API="$URL_API" PAF_SENHA_DEMO="$SENHA_DEMO" timeout "$LIMITE_ENSAIO_S" node "$RAIZ/deploy/paf/imagem-anterior-esquema-novo.mjs"
echo "IMAGEM-ANTERIOR-ESQUEMA-NOVO OK ($SHA_ANTERIOR contra o esquema de $(git -C "$RAIZ" rev-parse HEAD))"
