#!/usr/bin/env bash
# RNF10 smoke of an API image: zone file in the image, `date +%Z` = -03 with TZ set, and emission + painel through
# smoke-fuso.mjs on an ephemeral PostgreSQL and Valkey seeded by `seed-demo`. Exit 0 only when every step passed.
#
# Usage: deploy/paf/smoke-fuso.sh <imagem>   (e.g. paf-api:smoke, from publicar-imagem-api.sh)
# Needs: docker, dotnet SDK, node >= 18. Leaves nothing running: containers and network go on EXIT.
set -uo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: $0 <imagem>" >&2
  exit 2
fi
IMAGEM="$1"
RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SUFIXO="paf-smoke-$$"
REDE="$SUFIXO-rede"
PG="$SUFIXO-pg"
VALKEY="$SUFIXO-valkey"
API="$SUFIXO-api"
SENHA_PG="smoke-pg-$$"
SENHA_VALKEY="smoke-valkey-$$"
# ASCII only: the kit signs with UTF-8 and validates with ASCII (ADR 0008).
CHAVE_JWT="paf-smoke-signing-key-ascii-only-0123456789abcdef"
SENHA_DEMO="Password123!"
FUSO="America/Sao_Paulo"
# Ceilings for every blocking step (RNF05 spirit applied to the gate itself).
LIMITE_PRONTO_S=60
LIMITE_MIGRATOR_S=900

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}

limpar() {
  echo "--- limpeza"
  docker rm -f "$API" "$PG" "$VALKEY" >/dev/null
  docker network rm "$REDE" >/dev/null
}
trap limpar EXIT

aguardar() {
  local nome="$1"; shift
  local i
  for ((i = 0; i < LIMITE_PRONTO_S; i++)); do
    if docker exec "$nome" "$@" >/dev/null; then return 0; fi
    sleep 1
  done
  return 1
}

echo "--- 1. arquivo de zona na imagem $IMAGEM"
conteiner_tmp="$(docker create "$IMAGEM")"
if [ -z "$conteiner_tmp" ]; then
  registrar FAIL "imagem: docker create falhou"
else
  # grep -c exits 1 on zero matches; the count itself is what the check compares.
  zonas="$(docker export "$conteiner_tmp" | tar t | grep -c "usr/share/zoneinfo/$FUSO$")"
  docker rm "$conteiner_tmp" >/dev/null
  if [ "$zonas" = "1" ]; then registrar PASS "imagem: /usr/share/zoneinfo/$FUSO presente"
  else registrar FAIL "imagem: /usr/share/zoneinfo/$FUSO ausente (contagem $zonas)"; fi
fi

echo "--- 2. PostgreSQL e Valkey efêmeros"
if ! docker network create "$REDE" >/dev/null; then
  echo "FAIL infra: docker network create"
  exit 1
fi
docker run -d --name "$PG" --network "$REDE" --network-alias postgres -p 127.0.0.1::5432 \
  -e POSTGRES_DB=fsh -e POSTGRES_USER=fsh -e POSTGRES_PASSWORD="$SENHA_PG" postgres:18-alpine >/dev/null
docker run -d --name "$VALKEY" --network "$REDE" --network-alias valkey -p 127.0.0.1::6379 \
  valkey/valkey:9.1.0-alpine valkey-server --requirepass "$SENHA_VALKEY" >/dev/null
if aguardar "$PG" pg_isready -U fsh -d fsh && aguardar "$VALKEY" valkey-cli -a "$SENHA_VALKEY" --no-auth-warning PING; then
  registrar PASS "infra: postgres e valkey prontos"
else
  registrar FAIL "infra: postgres ou valkey não ficou pronto em ${LIMITE_PRONTO_S}s"
  printf '%s\n' "${resultados[@]}"
  exit 1
fi
PORTA_PG="$(docker port "$PG" 5432/tcp | head -n 1 | sed 's/.*://')"
PORTA_VALKEY="$(docker port "$VALKEY" 6379/tcp | head -n 1 | sed 's/.*://')"

echo "--- 3. migrações e seed-demo pelo DbMigrator (host, Development: seed-demo recusa outro ambiente)"
migrar() {
  DOTNET_ENVIRONMENT=Development \
  DatabaseOptions__ConnectionString="Host=127.0.0.1;Port=$PORTA_PG;Database=fsh;Username=fsh;Password=$SENHA_PG;Timeout=15;Command Timeout=120" \
  CachingOptions__Redis="127.0.0.1:$PORTA_VALKEY,password=$SENHA_VALKEY,connectTimeout=5000,syncTimeout=5000" \
  Seed__DefaultAdminPassword="$SENHA_DEMO" \
  Seed__DemoPassword="$SENHA_DEMO" \
  timeout "$LIMITE_MIGRATOR_S" dotnet run --project "$RAIZ/src/Host/FSH.Starter.DbMigrator" -c Release -p:NuGetAudit=false -- "$@"
}
if migrar apply --seed && migrar seed-demo; then
  registrar PASS "banco: apply --seed e seed-demo"
else
  registrar FAIL "banco: migrator falhou ou passou de ${LIMITE_MIGRATOR_S}s"
  printf '%s\n' "${resultados[@]}"
  exit 1
fi

echo "--- 4. API da imagem com TZ=$FUSO"
docker run -d --name "$API" --network "$REDE" -p 127.0.0.1::8080 \
  -e TZ="$FUSO" \
  -e DOTNET_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e DatabaseOptions__ConnectionString="Host=postgres;Database=fsh;Username=fsh;Password=$SENHA_PG;Timeout=15;Command Timeout=30" \
  -e CachingOptions__Redis="valkey:6379,password=$SENHA_VALKEY,connectTimeout=5000,syncTimeout=5000" \
  -e JwtOptions__SigningKey="$CHAVE_JWT" \
  -e Seed__DefaultAdminPassword="$SENHA_DEMO" \
  -e HangfireOptions__UserName=smoke -e HangfireOptions__Password="smoke-hangfire-$$" \
  -e CorsOptions__AllowedOrigins__0=http://localhost \
  -e FrontendOptions__AllowedOrigins__0=http://localhost -e FrontendOptions__DefaultOrigin=http://localhost \
  -e MailOptions__From=no-reply@paf.local -e MailOptions__Smtp__Host=localhost -e MailOptions__Smtp__Port=1025 \
  -e MailOptions__Smtp__Security=None \
  -e AllowedHosts='*' \
  "$IMAGEM" >/dev/null
PORTA_API="$(docker port "$API" 8080/tcp | head -n 1 | sed 's/.*://')"

echo "--- 5. relógio do contêiner"
zona_api="$(docker exec "$API" date +%Z)"
if [ "$zona_api" = "-03" ]; then registrar PASS "contêiner: date +%Z = -03"
else registrar FAIL "contêiner: date +%Z = '$zona_api', esperado -03"; fi

echo "--- 6. emissão e painel (smoke-fuso.mjs)"
if PAF_API="http://127.0.0.1:$PORTA_API" node "$RAIZ/deploy/paf/smoke-fuso.mjs"; then
  registrar PASS "api: emissão e painel"
else
  registrar FAIL "api: emissão e painel (smoke-fuso.mjs)"
  echo "--- últimas linhas do log da API"
  docker logs --tail 40 "$API"
fi

echo "=== resultado ($IMAGEM)"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -eq 0 ]; then echo "SMOKE-FUSO-IMAGEM OK"; exit 0; fi
echo "SMOKE-FUSO-IMAGEM FALHOU ($falhas)"
exit 1
