#!/usr/bin/env bash
# Rehearsal of the two-environment topology of the PAF (O02) on a disposable pair of projects: both stacks up from
# deploy/paf/compose.yml, then the gates of the O02 row of the slice-2 plan:
#   - PostgreSQL of each environment answers SHOW timezone = America/Sao_Paulo;
#   - CORS preflight with X-Paf-Unidade from the environment's dashboard origin answers 204 and allows the header,
#     and the other environment's origin is not allowed;
#   - the dashboard serves the environment's inactivity timeout in /config.json (D04);
#   - an emission in homologation publishes nothing on production's Valkey within 5 s, while the same emission in
#     production does (positive control): homologation never reaches production's panels;
#   - `down -v` of homologation leaves every production container running and its API ready.
# Exit 0 only when every step passed. Removes both projects (containers, network, volumes) on exit.
#
# Usage: deploy/paf/ensaio-ambientes.sh <producao.env> <homologacao.env>
# Refuses any project name not starting with paf-ensaio-: it runs `down -v`, which destroys the databases.
# Needs: docker compose v2, node >= 18, curl; images named in the files already present (no build here).
set -uo pipefail

if [ "$#" -ne 2 ]; then
  echo "uso: $0 <producao.env> <homologacao.env>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROD_ENV="$1"
HOMOLOG_ENV="$2"
SENHA_DEMO="Password123!"
LIMITE_PRONTO_S=180
JANELA_TEMPO_REAL_S=5
TIMEOUT_HTTP_S=10

valor() {
  # Last KEY=value line of an env file, literal (no interpolation): only used for plain values.
  grep "^$2=" "$1" | tail -n 1 | cut -d= -f2-
}

for arquivo in "$PROD_ENV" "$HOMOLOG_ENV"; do
  projeto="$(valor "$arquivo" COMPOSE_PROJECT_NAME)"
  case "$projeto" in
    paf-ensaio-*) ;;
    *) echo "recusado: $arquivo tem COMPOSE_PROJECT_NAME='$projeto'; o ensaio só roda em projetos paf-ensaio-*" >&2; exit 2 ;;
  esac
done

compose() {
  local arquivo="$1"; shift
  docker compose -f "$AQUI/compose.yml" --env-file "$arquivo" "$@"
}

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}

limpar() {
  echo "--- limpeza"
  compose "$HOMOLOG_ENV" down -v --remove-orphans
  compose "$PROD_ENV" down -v --remove-orphans
}
trap limpar EXIT

aguardar_api() {
  local porta="$1" i
  for ((i = 0; i < LIMITE_PRONTO_S; i += 2)); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "http://127.0.0.1:$porta/health/ready")" = "200" ]; then return 0; fi
    sleep 2
  done
  return 1
}

echo "--- 1. invariantes (verificar-ambiente.sh)"
verificacao="$(bash "$AQUI/verificar-ambiente.sh" "$PROD_ENV" "$HOMOLOG_ENV" 2>&1)"
codigo=$?
echo "$verificacao" | grep -E "^FAIL|^VERIFICAR"
if [ "$codigo" -eq 0 ]; then registrar PASS "invariantes dos dois arquivos"
else registrar FAIL "invariantes dos dois arquivos (código $codigo)"; printf '%s\n' "${resultados[@]}"; exit 1; fi

echo "--- 2. subir os dois projetos"
for arquivo in "$PROD_ENV" "$HOMOLOG_ENV"; do
  if compose "$arquivo" up -d --wait --wait-timeout "$LIMITE_PRONTO_S" postgres valkey \
     && compose "$arquivo" up -d api-a dashboard; then
    registrar PASS "up $(valor "$arquivo" COMPOSE_PROJECT_NAME)"
  else
    registrar FAIL "up $(valor "$arquivo" COMPOSE_PROJECT_NAME)"
    printf '%s\n' "${resultados[@]}"; exit 1
  fi
done
for arquivo in "$PROD_ENV" "$HOMOLOG_ENV"; do
  if aguardar_api "$(valor "$arquivo" PAF_PORTA_API_A)"; then registrar PASS "api pronta $(valor "$arquivo" COMPOSE_PROJECT_NAME)"
  else registrar FAIL "api pronta $(valor "$arquivo" COMPOSE_PROJECT_NAME)"; compose "$arquivo" logs --tail 40 api-a; printf '%s\n' "${resultados[@]}"; exit 1; fi
done

echo "--- 3. fuso do PostgreSQL"
for arquivo in "$PROD_ENV" "$HOMOLOG_ENV"; do
  fuso="$(compose "$arquivo" exec -T postgres psql -U fsh -d fsh -Atc 'SHOW timezone' | tr -d '\r')"
  if [ "$fuso" = "America/Sao_Paulo" ]; then registrar PASS "SHOW timezone $(valor "$arquivo" COMPOSE_PROJECT_NAME) = $fuso"
  else registrar FAIL "SHOW timezone $(valor "$arquivo" COMPOSE_PROJECT_NAME) = '$fuso'"; fi
done

echo "--- 4. preflight CORS com X-Paf-Unidade"
preflight() {
  local porta="$1" origem="$2"
  curl -s -D - -o /dev/null --max-time "$TIMEOUT_HTTP_S" -X OPTIONS \
    -H "Origin: $origem" -H "Access-Control-Request-Method: GET" \
    -H "Access-Control-Request-Headers: authorization,tenant,x-paf-unidade" \
    "http://127.0.0.1:$porta/api/v1/paf/sessao/unidade-corrente" | tr -d '\r'
}
PORTA_PROD="$(valor "$PROD_ENV" PAF_PORTA_API_A)"
PORTA_HOMOLOG="$(valor "$HOMOLOG_ENV" PAF_PORTA_API_A)"
resposta="$(preflight "$PORTA_PROD" "$(valor "$PROD_ENV" PAF_DASHBOARD_URL)")"
if echo "$resposta" | head -n 1 | grep -q " 204" && echo "$resposta" | grep -i "^access-control-allow-headers:" | grep -qi "x-paf-unidade"; then
  registrar PASS "preflight do dashboard de produção: 204 com x-paf-unidade"
else
  registrar FAIL "preflight do dashboard de produção: $(echo "$resposta" | head -n 1)"
  echo "$resposta"
fi
resposta="$(preflight "$PORTA_PROD" "$(valor "$HOMOLOG_ENV" PAF_DASHBOARD_URL)")"
if echo "$resposta" | grep -qi "^access-control-allow-origin:"; then registrar FAIL "origem da homologação aceita pela API de produção"
else registrar PASS "origem da homologação recusada pela API de produção"; fi

echo "--- 5. /config.json do dashboard (D04)"
for arquivo in "$PROD_ENV" "$HOMOLOG_ENV"; do
  esperado="$(valor "$arquivo" PAF_INATIVIDADE_MS)"
  servido="$(curl -s --max-time "$TIMEOUT_HTTP_S" "http://127.0.0.1:$(valor "$arquivo" PAF_PORTA_DASHBOARD)/config.json" \
    | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).inactivityIdleMs)}catch(e){console.log("json-invalido")}})')"
  if [ "$servido" = "$esperado" ]; then registrar PASS "config.json $(valor "$arquivo" COMPOSE_PROJECT_NAME) inactivityIdleMs = $servido"
  else registrar FAIL "config.json $(valor "$arquivo" COMPOSE_PROJECT_NAME) inactivityIdleMs = '$servido', esperado '$esperado'"; fi
done

echo "--- 6. massa demo nos dois (só ensaio: DbMigrator em Development dentro da rede do projeto)"
for arquivo in "$PROD_ENV" "$HOMOLOG_ENV"; do
  if compose "$arquivo" run --rm -T -e DOTNET_ENVIRONMENT=Development -e Seed__DemoPassword="$SENHA_DEMO" migrator seed-demo >/dev/null; then
    registrar PASS "seed-demo $(valor "$arquivo" COMPOSE_PROJECT_NAME)"
  else
    registrar FAIL "seed-demo $(valor "$arquivo" COMPOSE_PROJECT_NAME)"; printf '%s\n' "${resultados[@]}"; exit 1
  fi
done

echo "--- 7. tempo real: homologação não chega ao Valkey de produção"
SENHA_VALKEY_PROD="$(valor "$PROD_ENV" VALKEY_PASSWORD)"
# Counts the pub/sub messages production's Valkey carries during the window while one emission runs in ALVO.
mensagens_em_producao() {
  local porta_alvo="$1" captura
  captura="$(mktemp)"
  compose "$PROD_ENV" exec -T valkey timeout "$JANELA_TEMPO_REAL_S" \
    valkey-cli --no-auth-warning -a "$SENHA_VALKEY_PROD" --csv PSUBSCRIBE '*' >"$captura" &
  local assinante=$!
  sleep 1
  PAF_API="http://127.0.0.1:$porta_alvo" node "$AQUI/smoke-fuso.mjs" >/dev/null
  local emissao=$?
  wait "$assinante"
  local total
  total="$(grep -c '^"pmessage"' "$captura")"
  rm -f "$captura"
  echo "$emissao $total"
}
read -r emissao total < <(mensagens_em_producao "$PORTA_HOMOLOG")
if [ "$emissao" = "0" ] && [ "$total" = "0" ]; then registrar PASS "emissão na homologação: 0 mensagens no Valkey de produção em ${JANELA_TEMPO_REAL_S}s"
else registrar FAIL "emissão na homologação: smoke=$emissao, $total mensagens no Valkey de produção"; fi
read -r emissao total < <(mensagens_em_producao "$PORTA_PROD")
if [ "$emissao" = "0" ] && [ "$total" -gt 0 ]; then registrar PASS "controle: emissão na produção publica $total mensagens no Valkey de produção"
else registrar FAIL "controle: emissão na produção smoke=$emissao, $total mensagens (o ensaio não enxerga o canal)"; fi

echo "--- 8. down -v da homologação não derruba produção"
compose "$HOMOLOG_ENV" down -v --remove-orphans
# The migrator is one-shot and exits 0 by design; any other stopped service is a casualty.
parados="$(compose "$PROD_ENV" ps --status exited --status dead --format '{{.Service}}' | grep -v '^migrator$' | tr '\n' ' ')"
rodando="$(compose "$PROD_ENV" ps --status running --format '{{.Service}}' | sort | tr '\n' ' ')"
volumes_prod="$(docker volume ls -q --filter "label=com.docker.compose.project=$(valor "$PROD_ENV" COMPOSE_PROJECT_NAME)" | wc -l | tr -d ' ')"
if [ -z "${parados// /}" ] && [ "$rodando" = "api-a dashboard postgres valkey " ] && [ "$volumes_prod" = "2" ] && aguardar_api "$PORTA_PROD"; then
  registrar PASS "produção intacta depois do down -v da homologação ($rodando, $volumes_prod volumes)"
else
  registrar FAIL "produção depois do down da homologação: rodando='$rodando' parados='$parados' volumes=$volumes_prod"
fi

echo "=== resultado"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -eq 0 ]; then echo "ENSAIO-AMBIENTES OK"; exit 0; fi
echo "ENSAIO-AMBIENTES FALHOU ($falhas)"
exit 1
