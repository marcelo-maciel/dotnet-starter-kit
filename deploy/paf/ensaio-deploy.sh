#!/usr/bin/env bash
# Local rehearsal of deploy.sh (O06, RNF16) behind the environment's Caddy (O03), never on the VPS. A Linux "host"
# container (Ubuntu 22.04 with docker CLI and compose, flock, curl) shares the Docker VM's network (--network host),
# so deploy.sh and the published 127.0.0.1 ports of the compose project mean the same thing they mean on the VPS.
# The tree deploy.sh sees is the one implantar-remoto.sh ships (deploy/paf, deploy/docker/postgres-init and
# appsettings.Production.json), mounted at the path the Docker daemon resolves, so compose's bind mounts work.
#
# cloudflared never runs here: it needs a real tunnel credential and would dial Cloudflare. deploy.sh never starts it
# either, so the rehearsal's clients play its part: they join the project's network and reach Caddy with the public
# host, and CA7 sends CF-Connecting-IP the way cloudflared does.
#
# Steps, each recorded; exit 0 only when every one passed:
#   1. env files (fake tunnel credentials, never used), images tagged under a local registry name: v1 and v2 (the
#      given images), v3 = API that never answers health, v4 = migrator that fails;
#   2. host container;
#   3. first deploy (v1): exit 0, both instances healthy, Caddy routing by host (API 200, unknown host 404), no
#      cloudflared container; seed-demo with a password fresh per run;
#   4. walk in a loop (ensaio-deploy.mjs) through Caddy, CA1 (101 with no negotiate), then deploy v2 under load: exit
#      0, both containers replaced, each stopped only after the other answered;
#   5. two simultaneous deploys: the second exits 3 (flock) within 10 s, the first exits 0;
#   6. v3 (no health): exit 7, api-a back on v2 and healthy; api-b the same container on v2;
#   7. v4 (migrator fails): exit 6, no API container replaced;
#   8. CA3: docker kill of api-a, api-b, api-a under the walk: every hub client reconnects within 30 s;
#   9. walk results (CA8): no 5xx in emission outside the kills, retries without duplicate ficha (fichas created =
#      keys), every called senha on the painel, every SenhaChamada EventoId of the outbox received by the hub or
#      recovered by the re-query; AtividadesSessao keys and SessoesEstacao rows unchanged across the deploys;
#  10. CA6: no access_token= and no token in Caddy's log, with the hub's lines present as the control;
#  11. CA7: the audited IP of a login is CF-Connecting-IP, never the client's forged X-Forwarded-For.
#
# Usage: deploy/paf/ensaio-deploy.sh <imagem-api> <imagem-migrator> <imagem-frente>
#   e.g. paf-api:o06 paf-dbmigrator:o06 paf-dashboard:o02 (built by publicar-imagem-api.sh / -migrator.sh; the front
#   image serves both dashboard and admin here, the fronts are not under test).
# Needs: docker (Docker Desktop on Windows, or Linux), bash; node runs inside node:22-alpine, curl inside
# curlimages/curl.
set -uo pipefail
export MSYS_NO_PATHCONV=1

if [ "$#" -ne 3 ]; then
  echo "uso: $0 <imagem-api> <imagem-migrator> <imagem-frente>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RAIZ="$(cd "$AQUI/../.." && pwd)"
IMAGEM_API="$1"
IMAGEM_MIGRATOR="$2"
IMAGEM_FRENTE="$3"
PREFIXO=paf-ensaio-deploy
HOST_CONTAINER="$PREFIXO-host"
WALK_CONTAINER="$PREFIXO-walk"
IMAGEM_HOST=paf-ensaio-deploy-host:22.04
IMAGEM_CURL=curlimages/curl:8.11.1
REGISTRO=paf-ensaio-registro
PROJETO_PROD=paf-ensaio-deploy-prod
PROJETO_HOMOLOG=paf-ensaio-deploy-homolog
REDE="${PROJETO_PROD}_paf"
SENHA_DEMO="Ens-$(od -An -tx1 -N12 /dev/urandom | tr -d ' \n')-Aa1!"
LIMITE_DEPLOY_S=900
LIMITE_WALK_S=300
ESPERA_ENTREGA_S=20
LIMITE_FLOCK_S=10
LIMITE_RECONEXAO_MS=30000
LIMITE_QUEDA_S=35
# Caddy's health interval plus its fail_duration, with margin (caddy/Caddyfile, deploy.sh ESPERA_BALANCEADOR_S).
ESPERA_BALANCEADOR_S=6
IP_CLIENTE=198.51.100.23
IP_FORJADO=203.0.113.66
PID_LOGS_API=""

TRABALHO="$(mktemp -d)"
# Path of the work dir as the Docker daemon sees it: Docker Desktop on Windows mounts C: at /run/desktop/mnt/host/c.
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    janela="$(cd "$TRABALHO" && pwd -W)"
    unidade="$(printf '%s' "${janela%%:*}" | tr '[:upper:]' '[:lower:]')"
    NO_DAEMON="/run/desktop/mnt/host/$unidade${janela#*:}"
    ;;
  *) NO_DAEMON="$TRABALHO" ;;
esac

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  echo "$1 $2"
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}
terminar() {
  echo "=== resultado"
  printf '%s\n' "${resultados[@]}"
  if [ "$falhas" -eq 0 ]; then echo "ENSAIO-DEPLOY OK"; exit 0; fi
  echo "ENSAIO-DEPLOY FALHOU ($falhas)"
  exit 1
}
no_host() {
  docker exec -e PAF_REGISTRO="$REGISTRO" -w "$NO_DAEMON" "$HOST_CONTAINER" bash -c "$1"
}
limpar() {
  echo "--- limpeza"
  touch "$TRABALHO/parar"
  if [ -n "$PID_LOGS_API" ]; then kill "$PID_LOGS_API" >/dev/null 2>&1 || echo "coletor de logs da API já parado"; fi
  docker rm -f "$WALK_CONTAINER" >/dev/null 2>&1 || echo "walk já removido"
  if docker inspect "$HOST_CONTAINER" >/dev/null 2>&1; then
    no_host "docker compose -f deploy/paf/compose.yml --env-file deploy/paf/ambientes/producao.env --profile '*' down -v --remove-orphans" \
      || echo "down do projeto do ensaio falhou: conferir 'docker ps -a --filter label=com.docker.compose.project=$PROJETO_PROD'"
    docker rm -f "$HOST_CONTAINER" >/dev/null
  fi
  for versao in v1 v2 v3 v4; do
    for nome in paf-api paf-dbmigrator paf-dashboard paf-admin; do
      docker rmi "$REGISTRO/$nome:$versao" >/dev/null || echo "tag $REGISTRO/$nome:$versao já removida"
    done
  done
  rm -rf "$TRABALHO"
}
trap limpar EXIT

segredo() { od -An -tx1 -N24 /dev/urandom | tr -d ' \n'; }
deploy() {
  # Runs deploy.sh in the host container and echoes its exit code; the output goes to the log named by $2.
  # Each line gets the UTC time, to line the deploy up with the walk's errors and the API logs.
  local tag="$1" log="$2"
  timeout "$LIMITE_DEPLOY_S" docker exec -e PAF_REGISTRO="$REGISTRO" -w "$NO_DAEMON" "$HOST_CONTAINER" \
    bash deploy/paf/deploy.sh producao "$tag" 2>&1 | while IFS= read -r linha; do echo "$(date -u +%T) $linha"; done >"$TRABALHO/$log"
  echo "${PIPESTATUS[0]}"
}
id_de() {
  docker ps -q --no-trunc --filter "label=com.docker.compose.project=$PROJETO_PROD" --filter "label=com.docker.compose.service=$1"
}
imagem_de() {
  docker inspect -f '{{.Config.Image}}' "$(id_de "$1")"
}
psql_prod() {
  docker exec -i "$(id_de postgres)" psql -U fsh -d fsh -Atc "$1"
}
saude_de() {
  no_host "curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:$1/api/v1/paf/saude"
}
# curl from inside the project's network, as cloudflared reaches Caddy: prints the HTTP status, body to $TRABALHO/$1.
pelo_caddy() {
  local corpo="$1"
  shift
  docker run --rm --network "$REDE" -v "$NO_DAEMON:/ensaio" "$IMAGEM_CURL" \
    -sS -o "/ensaio/$corpo" -w '%{http_code}' --max-time 30 "$@" 2>"$TRABALHO/$corpo.erro"
}
mostrar_log() {
  echo "--- $1 (linhas FAIL e últimas 40 linhas)"
  grep "^FAIL" "$TRABALHO/$1" || echo "(sem linha FAIL)"
  tail -n 40 "$TRABALHO/$1"
}

echo "--- 1. árvore do host, arquivos de ambiente e imagens"
mkdir -p "$TRABALHO/deploy/docker" "$TRABALHO/src/Host/FSH.Starter.Api" "$TRABALHO/tmp"
cp -r "$AQUI" "$TRABALHO/deploy/paf"
cp -r "$RAIZ/deploy/docker/postgres-init" "$TRABALHO/deploy/docker/postgres-init"
cp "$RAIZ/src/Host/FSH.Starter.Api/appsettings.Production.json" "$TRABALHO/src/Host/FSH.Starter.Api/"
rm -f "$TRABALHO/deploy/paf/ambientes/"*.env "$TRABALHO/deploy/paf/ambientes/"*.deploy.lock "$TRABALHO/deploy/paf/ambientes/"*.tag-anterior
gerar_ambiente() {
  local exemplo="$1" destino="$2" projeto="$3" tunel="$4" base="$5" rede="$6" url="$7"
  sed -e "s#^COMPOSE_PROJECT_NAME=.*#COMPOSE_PROJECT_NAME=$projeto#" \
      -e "s#^PAF_SUBNET=.*#PAF_SUBNET=$rede.0/24#" \
      -e "s#^PAF_SUBNET_DINAMICA=.*#PAF_SUBNET_DINAMICA=$rede.128/25#" \
      -e "s#^PAF_IP_CADDY=.*#PAF_IP_CADDY=$rede.10#" \
      -e "s#^PAF_PORTA_POSTGRES=.*#PAF_PORTA_POSTGRES=${base}432#" \
      -e "s#^PAF_PORTA_API_A=.*#PAF_PORTA_API_A=${base}080#" \
      -e "s#^PAF_PORTA_API_B=.*#PAF_PORTA_API_B=${base}081#" \
      -e "s#^PAF_PORTA_DASHBOARD=.*#PAF_PORTA_DASHBOARD=${base}082#" \
      -e "s#^PAF_PORTA_ADMIN=.*#PAF_PORTA_ADMIN=${base}083#" \
      -e "s#^PAF_IMAGEM_API=.*#PAF_IMAGEM_API=$REGISTRO/paf-api:v1#" \
      -e "s#^PAF_IMAGEM_MIGRATOR=.*#PAF_IMAGEM_MIGRATOR=$REGISTRO/paf-dbmigrator:v1#" \
      -e "s#^PAF_IMAGEM_DASHBOARD=.*#PAF_IMAGEM_DASHBOARD=$REGISTRO/paf-dashboard:v1#" \
      -e "s#^PAF_IMAGEM_ADMIN=.*#PAF_IMAGEM_ADMIN=$REGISTRO/paf-admin:v1#" \
      -e "s#^POSTGRES_PASSWORD=.*#POSTGRES_PASSWORD=$(segredo)#" \
      -e "s#^VALKEY_PASSWORD=.*#VALKEY_PASSWORD=$(segredo)#" \
      -e "s#^JwtOptions__SigningKey=.*#JwtOptions__SigningKey=$(segredo)$(segredo)#" \
      -e "s#^PAF_API_URL=.*#PAF_API_URL=https://api.$url#" \
      -e "s#^PAF_DASHBOARD_URL=.*#PAF_DASHBOARD_URL=https://$url#" \
      -e "s#^PAF_ADMIN_URL=.*#PAF_ADMIN_URL=https://admin.$url#" \
      -e "s#<uuid-do-tunel>#$tunel#" \
      -e "s#<usuario>#ensaio#g" -e "s#<account-id>#ensaio#g" -e "s#<smtp>#smtp.ensaio.test#g" \
      -e "s#<webhook-de-alerta>#alerta.ensaio.test/x#g" -e "s#<tag>#v1#g" \
      "$exemplo" >"$destino"
  # One fresh value per placeholder: the checker refuses the backup token equal to the application's Storage key.
  while grep -q '<segredo>' "$destino"; do
    sed -i "0,/<segredo>/s//$(segredo)/" "$destino"
  done
  # Fake credential in the tunnel's own file: cloudflared never runs here, the checker only reads it.
  sed -e "s#<credenciais-json-do-tunel>#{\"AccountTag\":\"ensaio\",\"TunnelSecret\":\"$(segredo)\",\"TunnelID\":\"$tunel\"}#" \
    "${exemplo%.env.exemplo}.tunel.env.exemplo" >"${destino%.env}.tunel.env"
}
gerar_ambiente "$AQUI/ambientes/producao.env.exemplo" "$TRABALHO/deploy/paf/ambientes/producao.env" \
  "$PROJETO_PROD" 00000000-0000-4000-8000-0000000e0001 28 172.31.210 paf-ensaio-prod.test
gerar_ambiente "$AQUI/ambientes/homologacao.env.exemplo" "$TRABALHO/deploy/paf/ambientes/homologacao.env" \
  "$PROJETO_HOMOLOG" 00000000-0000-4000-8000-0000000e0002 29 172.31.220 paf-ensaio-homolog.test
HOST_API=api.paf-ensaio-prod.test
SUBNET_PROD=172.31.210.0/24
PORTA_A=28080
PORTA_B=28081

marcar() { docker tag "$1" "$REGISTRO/$2" || { registrar FAIL "docker tag $1 -> $2"; terminar; }; }
for versao in v1 v2 v3 v4; do
  marcar "$IMAGEM_FRENTE" "paf-dashboard:$versao"
  marcar "$IMAGEM_FRENTE" "paf-admin:$versao"
done
marcar "$IMAGEM_API" paf-api:v1
marcar "$IMAGEM_API" paf-api:v2
marcar "$IMAGEM_API" paf-api:v4
marcar "$IMAGEM_MIGRATOR" paf-dbmigrator:v1
marcar "$IMAGEM_MIGRATOR" paf-dbmigrator:v2
marcar "$IMAGEM_MIGRATOR" paf-dbmigrator:v3
# v3: an API image that starts and never answers paf/saude (nothing listens on 8080).
if ! docker build -q -t "$REGISTRO/paf-api:v3" - >/dev/null <<'DOCKERFILE'
FROM alpine:3.22
CMD ["sleep", "infinity"]
DOCKERFILE
then registrar FAIL "imagem v3 sem saúde"; terminar; fi
# v4: a migrator that fails (exit 1) before touching the database.
if ! docker build -q -t "$REGISTRO/paf-dbmigrator:v4" - >/dev/null <<'DOCKERFILE'
FROM alpine:3.22
ENTRYPOINT ["sh", "-c", "echo migrator do ensaio falha de propósito >&2; exit 1"]
DOCKERFILE
then registrar FAIL "imagem v4 do migrator"; terminar; fi
registrar PASS "árvore do host, ambientes e imagens v1..v4"

echo "--- 2. host do ensaio: docker compose, flock, curl"
if ! docker build -q -t "$IMAGEM_HOST" - >/dev/null <<'DOCKERFILE'
FROM ubuntu:22.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY --from=docker:29-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker:29-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
CMD ["sleep", "infinity"]
DOCKERFILE
then registrar FAIL "build da imagem do host"; terminar; fi
docker rm -f "$HOST_CONTAINER" "$WALK_CONTAINER" >/dev/null 2>&1 || echo "nenhum contêiner anterior do ensaio"
if ! docker run -d --name "$HOST_CONTAINER" --network host -v /var/run/docker.sock:/var/run/docker.sock \
     -v "$NO_DAEMON:$NO_DAEMON" -e TMPDIR="$NO_DAEMON/tmp" "$IMAGEM_HOST" >/dev/null; then
  registrar FAIL "subir o host do ensaio"; terminar
fi
no_host 'docker compose version; flock --version | head -n 1; curl --version | head -n 1'

# deploy.sh recreates the API containers and their logs go with them: follow every API container from its start,
# so a server-side error seen by the walk can be read after the run.
# The `docker logs -f` children end when compose removes their container.
seguir_logs_da_api() {
  local servico id
  while [ ! -f "$TRABALHO/parar" ]; do
    for servico in api-a api-b; do
      for id in $(docker ps -q --filter "label=com.docker.compose.project=$PROJETO_PROD" --filter "label=com.docker.compose.service=$servico"); do
        if [ ! -f "$TRABALHO/api-$servico-$id.log" ]; then
          docker logs -f -t "$id" >"$TRABALHO/api-$servico-$id.log" 2>&1 &
        fi
      done
    done
    sleep 1
  done
}
seguir_logs_da_api &
PID_LOGS_API=$!
erros_da_api() {
  grep -h -i -E '"@l":"(Error|Fatal)"|EntrarNoGuiche|Unhandled|exception' "$TRABALHO"/api-*.log | cut -c1-400 | tail -n "$1" \
    || echo "(nenhuma linha de erro nos logs da API)"
}

echo "--- 3. primeira implantação (v1), roteamento do Caddy e massa demo"
codigo="$(deploy v1 deploy-v1.log)"
if [ "$codigo" = "0" ] && [ "$(saude_de $PORTA_A)" = "200" ] && [ "$(saude_de $PORTA_B)" = "200" ]; then
  registrar PASS "deploy v1 (primeira): código 0, api-a e api-b saudáveis"
else
  registrar FAIL "deploy v1 (primeira): código $codigo"; mostrar_log deploy-v1.log; terminar
fi
if [ -n "$(id_de caddy)" ] && [ -z "$(docker ps -aq --filter "label=com.docker.compose.project=$PROJETO_PROD" --filter label=com.docker.compose.service=cloudflared)" ]; then
  registrar PASS "Caddy no ar pelo deploy; cloudflared nunca criado no ensaio"
else
  registrar FAIL "Caddy ausente ou cloudflared criado"
fi
sleep "$ESPERA_BALANCEADOR_S"
codigo_api="$(pelo_caddy saude.txt -H "Host: $HOST_API" http://caddy/api/v1/paf/saude)"
codigo_outro="$(pelo_caddy outro.txt -H "Host: outro.paf-ensaio-prod.test" http://caddy/api/v1/paf/saude)"
codigo_painel="$(pelo_caddy painel.html -H "Host: paf-ensaio-prod.test" http://caddy/)"
if [ "$codigo_api" = "200" ] && [ "$codigo_outro" = "404" ] && [ "$codigo_painel" = "200" ]; then
  registrar PASS "Caddy por host: API 200, dashboard 200, host desconhecido 404"
else
  registrar FAIL "Caddy por host: API $codigo_api, dashboard $codigo_painel, host desconhecido $codigo_outro"
fi
if no_host "PAF_IMAGEM_MIGRATOR=$REGISTRO/paf-dbmigrator:v1 docker compose -f deploy/paf/compose.yml --env-file deploy/paf/ambientes/producao.env \
     run --rm --no-deps -T -e DOTNET_ENVIRONMENT=Development -e 'Seed__DemoPassword=$SENHA_DEMO' migrator seed-demo" >"$TRABALHO/seed.log" 2>&1; then
  registrar PASS "seed-demo com senha nova"
else
  registrar FAIL "seed-demo"; mostrar_log seed.log; terminar
fi

echo "--- 3b. CA7: IP auditado com X-Forwarded-For forjado"
printf '{"email":"admin@acme.com","password":"%s"}' "$SENHA_DEMO" >"$TRABALHO/login.json"
SQL_IP='SELECT "IpAddress" FROM identity."UserSessions" ORDER BY "CreatedAt" DESC LIMIT 1'
login() {
  pelo_caddy token.json -H "Host: $HOST_API" -H 'tenant: acme' -H 'Content-Type: application/json' "$@" \
    --data @/ensaio/login.json http://caddy/api/v1/identity/token/issue
}
codigo="$(login -H "CF-Connecting-IP: $IP_CLIENTE" -H "X-Forwarded-For: $IP_FORJADO")"
ip_auditado="$(psql_prod "$SQL_IP")"
if [ "$codigo" = "200" ] && [ "$ip_auditado" = "$IP_CLIENTE" ]; then
  registrar PASS "CA7: com CF-Connecting-IP $IP_CLIENTE e X-Forwarded-For forjado $IP_FORJADO, IP auditado $ip_auditado"
else
  registrar FAIL "CA7: login $codigo, IP auditado '$ip_auditado', esperado $IP_CLIENTE ($(cat "$TRABALHO/token.json.erro"))"
fi
codigo="$(login -H "X-Forwarded-For: $IP_FORJADO")"
ip_auditado="$(psql_prod "$SQL_IP")"
dentro_da_rede="$(docker run --rm node:22-alpine node -e '
  const [ip, cidr] = process.argv.slice(1); const [base, bits] = cidr.split("/");
  const n = (x) => x.split(".").reduce((a, o) => a * 256 + Number(o), 0); const t = 2 ** (32 - Number(bits));
  console.log(Math.floor(n(ip) / t) === Math.floor(n(base) / t) ? "sim" : "nao");' "${ip_auditado:-0.0.0.0}" "$SUBNET_PROD")"
if [ "$codigo" = "200" ] && [ "$ip_auditado" != "$IP_FORJADO" ] && [ "$dentro_da_rede" = "sim" ]; then
  registrar PASS "CA7: sem CF-Connecting-IP, o X-Forwarded-For forjado é ignorado (IP auditado $ip_auditado, o par na rede)"
else
  registrar FAIL "CA7: sem CF-Connecting-IP, login $codigo, IP auditado '$ip_auditado'"
fi
# A process on the host reaching an API's loopback port (as deploy.sh does) arrives from the bridge gateway, which
# the API does not trust (KnownProxies = Caddy's fixed IP only): its forged X-Forwarded-For must be ignored.
# The host container works in the rehearsal's directory, so login.json is there under its own name.
codigo="$(no_host "curl -sS -o /dev/null -w '%{http_code}' --max-time 30 -H 'tenant: acme' -H 'Content-Type: application/json' \
  -H 'X-Forwarded-For: $IP_FORJADO' --data @login.json http://127.0.0.1:$PORTA_A/api/v1/identity/token/issue")"
ip_auditado="$(psql_prod "$SQL_IP")"
if [ "$codigo" = "200" ] && [ -n "$ip_auditado" ] && [ "$ip_auditado" != "$IP_FORJADO" ]; then
  registrar PASS "CA7: pela porta de loopback da API, o X-Forwarded-For forjado é ignorado (IP auditado $ip_auditado)"
else
  registrar FAIL "CA7: pela porta de loopback, login $codigo, IP auditado '$ip_auditado' (forjado $IP_FORJADO)"
fi


echo "--- 4. walk em laço pelo Caddy e deploy v2 sob carga"
SQL_FICHAS='SELECT count(*) FROM paf."Fichas"'
fichas_antes="$(psql_prod "$SQL_FICHAS")"
ip_caddy="$(docker inspect -f "{{(index .NetworkSettings.Networks \"$REDE\").IPAddress}}" "$(id_de caddy)")"
if ! docker run -d --name "$WALK_CONTAINER" --network "$REDE" --add-host "$HOST_API:$ip_caddy" \
     -v "$NO_DAEMON:/ensaio" -e PAF_ENSAIO_API="http://$HOST_API" -e PAF_SENHA_DEMO="$SENHA_DEMO" \
     -e PAF_ENSAIO_PARAR=/ensaio/parar -e PAF_ENSAIO_FASE=/ensaio/fase-queda \
     -e PAF_ENSAIO_SAIDA=/ensaio/walk.json -e PAF_ENSAIO_ESPERA_S="$ESPERA_ENTREGA_S" \
     node:22-alpine node /ensaio/deploy/paf/ensaio-deploy.mjs >/dev/null; then
  registrar FAIL "subir o walk"; terminar
fi
walk_conectado() {
  local saida
  saida="$(docker logs "$WALK_CONTAINER" 2>&1)"
  case "$saida" in *"conectados pelo Caddy"*) return 0 ;; *) return 1 ;; esac
}
for _ in $(seq 1 60); do
  if walk_conectado || [ "$(docker inspect -f '{{.State.Running}}' "$WALK_CONTAINER")" != "true" ]; then break; fi
  sleep 1
done
if ! walk_conectado; then
  registrar FAIL "walk não conectou"; docker logs --tail 40 "$WALK_CONTAINER"; terminar
fi
sleep 5
SQL_ATIVIDADE='SELECT "SessaoId","UnidadeId","UsuarioId" FROM paf."AtividadesSessao" ORDER BY 1'
SQL_SESSAO_ESTACAO='SELECT row_to_json(s)::text FROM paf."SessoesEstacao" s ORDER BY s."Id"'
# The kit's outbox lives in the framework schema; the row Id is the payload's EventoId (TempoReal/AvisosPaf.cs).
SQL_OUTBOX="SELECT \"Id\" FROM framework.\"OutboxMessages\" WHERE \"Type\" LIKE 'FSH.Modules.PAF.TempoReal.SenhaChamadaPaf,%' ORDER BY 1"
atividade_antes="$(psql_prod "$SQL_ATIVIDADE")"
sessao_estacao_antes="$(psql_prod "$SQL_SESSAO_ESTACAO")"
outbox_antes="$(psql_prod "$SQL_OUTBOX")"
echo "antes: $(printf '%s' "$atividade_antes" | grep -c .) AtividadesSessao, $(printf '%s' "$sessao_estacao_antes" | grep -c .) SessoesEstacao"
if [ -z "$atividade_antes" ] || [ -z "$sessao_estacao_antes" ]; then
  registrar FAIL "walk sem AtividadesSessao ou SessoesEstacao: a comparação não teria o que comparar"
fi
id_a="$(id_de api-a)"
id_b="$(id_de api-b)"
codigo="$(deploy v2 deploy-v2.log)"
if [ "$codigo" = "0" ] && [ "$(id_de api-a)" != "$id_a" ] && [ "$(id_de api-b)" != "$id_b" ] \
   && [ "$(imagem_de api-a)" = "$REGISTRO/paf-api:v2" ] && [ "$(imagem_de api-b)" = "$REGISTRO/paf-api:v2" ]; then
  registrar PASS "deploy v2 sob carga: código 0, as duas instâncias trocadas para v2"
else
  registrar FAIL "deploy v2 sob carga: código $codigo"; mostrar_log deploy-v2.log
fi
if grep -q "api-a parada" "$TRABALHO/deploy-v2.log" && grep -q "api-b parada" "$TRABALHO/deploy-v2.log" \
   && [ "$(grep -c "de volta no balanceador" "$TRABALHO/deploy-v2.log")" = "2" ]; then
  registrar PASS "deploy v2 parou e devolveu ao Caddy uma instância de cada vez"
else
  registrar FAIL "deploy v2 sem a troca registrada das duas instâncias"
fi

echo "--- 5. dois deploys simultâneos"
( deploy v2 deploy-paralelo-1.log >"$TRABALHO/paralelo-1.codigo" ) &
pid_primeiro=$!
for _ in $(seq 1 30); do
  if grep -q -- "--- 2." "$TRABALHO/deploy-paralelo-1.log" 2>/dev/null; then break; fi
  sleep 1
done
inicio_segundo=$SECONDS
codigo_segundo="$(deploy v2 deploy-paralelo-2.log)"
duracao_segundo=$((SECONDS - inicio_segundo))
wait "$pid_primeiro"
codigo_primeiro="$(cat "$TRABALHO/paralelo-1.codigo")"
if [ "$codigo_segundo" = "3" ] && [ "$duracao_segundo" -le "$LIMITE_FLOCK_S" ] && grep -q "flock" "$TRABALHO/deploy-paralelo-2.log"; then
  registrar PASS "segundo deploy simultâneo: código 3 no flock em ${duracao_segundo}s"
else
  registrar FAIL "segundo deploy simultâneo: código $codigo_segundo em ${duracao_segundo}s"; mostrar_log deploy-paralelo-2.log
fi
if [ "$codigo_primeiro" = "0" ]; then registrar PASS "primeiro deploy simultâneo: código 0"
else registrar FAIL "primeiro deploy simultâneo: código $codigo_primeiro"; mostrar_log deploy-paralelo-1.log; fi

echo "--- 6. imagem sem saúde (v3)"
id_b="$(id_de api-b)"
codigo="$(deploy v3 deploy-v3.log)"
if [ "$codigo" = "7" ] && [ "$(imagem_de api-a)" = "$REGISTRO/paf-api:v2" ] && [ "$(saude_de $PORTA_A)" = "200" ]; then
  registrar PASS "v3: código 7, api-a de volta em v2 e saudável"
else
  registrar FAIL "v3: código $codigo, api-a em $(imagem_de api-a)"; mostrar_log deploy-v3.log
fi
if [ "$(id_de api-b)" = "$id_b" ] && [ "$(imagem_de api-b)" = "$REGISTRO/paf-api:v2" ] && [ "$(saude_de $PORTA_B)" = "200" ]; then
  registrar PASS "v3: api-b intocada (mesmo contêiner, v2, saudável)"
else
  registrar FAIL "v3: api-b foi tocada ou ficou sem saúde"
fi
if [ "$(cat "$TRABALHO/deploy/paf/ambientes/producao.tag-anterior")" = "v2" ] && grep -q "rollback: deploy/paf/deploy.sh producao v2" "$TRABALHO/deploy-v3.log"; then
  registrar PASS "v3: tag anterior gravada (v2) e rollback nomeado no log"
else
  registrar FAIL "v3: tag anterior ou rollback ausente"
fi

echo "--- 7. migrator que falha (v4)"
id_a="$(id_de api-a)"
id_b="$(id_de api-b)"
codigo="$(deploy v4 deploy-v4.log)"
if [ "$codigo" = "6" ] && [ "$(id_de api-a)" = "$id_a" ] && [ "$(id_de api-b)" = "$id_b" ] \
   && [ "$(saude_de $PORTA_A)" = "200" ] && [ "$(saude_de $PORTA_B)" = "200" ]; then
  registrar PASS "v4: código 6, nenhuma instância tocada"
else
  registrar FAIL "v4: código $codigo"; mostrar_log deploy-v4.log
fi

echo "--- 8. CA3: docker kill da instância conectada"
# After the kill of api-a every hub client is on api-b, so the kills 2 and 3 hit both clients (painel and guichê);
# the first kill hits whichever were on api-a. Each killed instance comes back (same container) before the next.
reconexoes() { docker logs "$WALK_CONTAINER" 2>&1 | grep -c "reconectado em" || true; }
touch "$TRABALHO/fase-queda"
tempos=()
repeticao=0
for servico in api-a api-b api-a; do
  repeticao=$((repeticao + 1))
  porta=$PORTA_A
  if [ "$servico" = api-b ]; then porta=$PORTA_B; fi
  esperadas=2
  if [ "$repeticao" = 1 ]; then esperadas=0; fi
  antes="$(reconexoes)"
  id_morto="$(id_de "$servico")"
  docker kill "$id_morto" >/dev/null
  decorrido=0
  while [ "$decorrido" -lt "$LIMITE_QUEDA_S" ] && [ "$(( $(reconexoes) - antes ))" -lt "$esperadas" ]; do
    sleep 1
    decorrido=$((decorrido + 1))
  done
  sleep 3
  novas="$(docker logs "$WALK_CONTAINER" 2>&1 | grep "reconectado em" | tail -n "+$((antes + 1))")"
  echo "queda $repeticao ($servico): $(printf '%s' "$novas" | grep -c . || true) reconexões: $(printf '%s' "$novas" | tr '\n' ';')"
  while read -r ms; do
    if [ -n "$ms" ]; then tempos+=("$ms"); fi
  done < <(printf '%s\n' "$novas" | sed -n 's/.*reconectado em \([0-9]*\) ms.*/\1/p')
  if [ "$(printf '%s' "$novas" | grep -c . || true)" -lt "$esperadas" ]; then
    registrar FAIL "CA3 queda $repeticao ($servico): $(printf '%s' "$novas" | grep -c . || true) de $esperadas clientes reconectaram em ${LIMITE_QUEDA_S}s"
  fi
  docker start "$id_morto" >/dev/null
  for _ in $(seq 1 45); do
    if [ "$(saude_de "$porta")" = "200" ]; then break; fi
    sleep 2
  done
  sleep "$ESPERA_BALANCEADOR_S"
done
rm -f "$TRABALHO/fase-queda"
maior=0
for ms in "${tempos[@]}"; do if [ "$ms" -gt "$maior" ]; then maior="$ms"; fi; done
if [ "${#tempos[@]}" -ge 4 ] && [ "$maior" -le "$LIMITE_RECONEXAO_MS" ]; then
  registrar PASS "CA3: ${#tempos[@]} reconexões em 3 quedas, todas na outra instância, maior ${maior} ms (teto ${LIMITE_RECONEXAO_MS} ms): ${tempos[*]}"
else
  registrar FAIL "CA3: ${#tempos[@]} reconexões, maior ${maior} ms: ${tempos[*]}"
fi

echo "--- 9. resultado do walk e banco (CA8)"
touch "$TRABALHO/parar"
codigo_walk="$(timeout "$LIMITE_WALK_S" docker wait "$WALK_CONTAINER")"
docker logs "$WALK_CONTAINER" >"$TRABALHO/walk.log" 2>&1
grep -E "^(PASS|FAIL)|reconexão|queda proposital" "$TRABALHO/walk.log"
if [ "$codigo_walk" = "0" ]; then registrar PASS "walk em laço: todas as conferências do walk (CA1 e CA8)"
else
  registrar FAIL "walk em laço: código $codigo_walk"; tail -n 20 "$TRABALHO/walk.log"
  echo "--- erros nos logs da API (últimas 30 linhas)"; erros_da_api 30
  echo "--- linhas de troca de instância dos deploys"; grep -h -E "parada|de volta no balanceador|FAIL" "$TRABALHO"/deploy-*.log
fi
if [ ! -f "$TRABALHO/walk.json" ]; then registrar FAIL "walk sem walk.json"; terminar; fi
chaves="$(docker run --rm -v "$NO_DAEMON:/ensaio" node:22-alpine node -e 'const r=require("/ensaio/walk.json");console.log(new Set(r.emissoes.chaves).size)')"
fichas_depois="$(psql_prod "$SQL_FICHAS")"
# The walk is the only writer of fichas in this database: every new ficha belongs to one key that got 200.
if [ "$((fichas_depois - fichas_antes))" = "$chaves" ] && [ "$chaves" -gt 0 ]; then
  registrar PASS "fichas no banco = chaves de idempotência com 200 ($chaves): retry não duplicou"
else
  registrar FAIL "fichas novas no banco $((fichas_depois - fichas_antes)), chaves $chaves"
fi
if [ "$(psql_prod "$SQL_ATIVIDADE")" = "$atividade_antes" ]; then registrar PASS "AtividadesSessao: mesmas sessões, unidades e usuários"
else registrar FAIL "AtividadesSessao mudou durante os deploys"; fi
if [ "$(psql_prod "$SQL_SESSAO_ESTACAO")" = "$sessao_estacao_antes" ]; then registrar PASS "SessoesEstacao: linhas idênticas"
else registrar FAIL "SessoesEstacao mudou durante os deploys"; psql_prod "$SQL_SESSAO_ESTACAO"; fi
psql_prod "$SQL_OUTBOX" >"$TRABALHO/outbox-depois.txt"
printf '%s\n' "$outbox_antes" >"$TRABALHO/outbox-antes.txt"
grep -vxF -f "$TRABALHO/outbox-antes.txt" "$TRABALHO/outbox-depois.txt" >"$TRABALHO/outbox-novos.txt" || true
psql_prod "SELECT \"Id\"::text || ' ' || \"Payload\"::text FROM framework.\"OutboxMessages\" WHERE \"Type\" LIKE 'FSH.Modules.PAF.TempoReal.SenhaChamadaPaf,%'" \
  >"$TRABALHO/outbox-carga.txt"
# A SenhaChamada sent while the painel is between the closed WebSocket and the new one never reaches it by the hub
# (SignalR keeps nothing for a client that is not connected); the painel recovers it by the re-query on reconnect
# (painel-estado.ts). So the gate is: every EventoId of the outbox arrived by the hub, or its senha came back by
# the re-query. The count by each path is printed.
docker run --rm -v "$NO_DAEMON:/ensaio" node:22-alpine node -e '
  const fs=require("fs");const r=require("/ensaio/walk.json");const recebidos=new Set(r.painel.eventos);
  const consulta=new Set(r.painel.senhasPorConsulta);
  const ler=(f)=>fs.readFileSync(f,"utf8").split("\n").filter(Boolean);
  const carga=new Map(ler("/ensaio/outbox-carga.txt").map((l)=>[l.slice(0,l.indexOf(" ")),l.slice(l.indexOf(" ")+1)]));
  const novos=ler("/ensaio/outbox-novos.txt");
  const fora=novos.filter((i)=>!recebidos.has(i));
  const senha=(i)=>(/"senha"\s*:\s*"([^"]+)"/i.exec(carga.get(i)??"")??[])[1];
  const perdidos=fora.filter((i)=>!consulta.has(senha(i)));
  fs.writeFileSync("/ensaio/eventos.txt",`${novos.length} ${fora.length} ${perdidos.length} ${fora.map((i)=>`${i}=${senha(i)}`).join(",")||"-"}`);'
read -r eventos_novos eventos_fora eventos_perdidos amostra <"$TRABALHO/eventos.txt"
if [ "$eventos_novos" -gt 0 ] && [ "$eventos_perdidos" = "0" ]; then
  registrar PASS "EventoId de SenhaChamada da caixa de saída: $((eventos_novos - eventos_fora)) de $eventos_novos pelo hub, $eventos_fora só pela reconsulta ($amostra), 0 perdidos"
else
  registrar FAIL "EventoId de SenhaChamada: $eventos_perdidos de $eventos_novos perdidos (nem hub nem reconsulta; $amostra)"
fi

echo "--- 10. CA6: log do Caddy sem token"
docker logs "$(id_de caddy)" >"$TRABALHO/caddy.log" 2>&1
com_token="$(grep -c "access_token=" "$TRABALHO/caddy.log" || true)"
com_jwt="$(grep -c "eyJ" "$TRABALHO/caddy.log" || true)"
linhas_hub="$(grep -c '"uri":"/api/v1/paf/hub"' "$TRABALHO/caddy.log" || true)"
if [ "$com_token" = "0" ] && [ "$com_jwt" = "0" ] && [ "$linhas_hub" -gt 0 ]; then
  registrar PASS "CA6: 0 access_token= e 0 JWT no log do Caddy, com $linhas_hub linhas do hub (sem query) como controle"
else
  registrar FAIL "CA6: $com_token linhas com access_token=, $com_jwt com JWT, $linhas_hub linhas do hub"
fi
# Caddy's error logs (no upstream answering) carry the request too: with both APIs stopped, the hub URL with a token
# must answer 502/503 and leave the marker in no log line. Last step: nothing runs on the APIs after it.
MARCADOR="marcador-do-token-$(segredo)"
docker stop "$(id_de api-a)" "$(id_de api-b)" >/dev/null
codigo="$(pelo_caddy hub-sem-api.txt -H "Host: $HOST_API" "http://caddy/api/v1/paf/hub?tenant=acme&access_token=$MARCADOR")"
docker logs "$(id_de caddy)" >"$TRABALHO/caddy.log" 2>&1
com_marcador="$(grep -c "$MARCADOR" "$TRABALHO/caddy.log" || true)"
linhas_erro="$(grep -c '"logger":"http.log.error' "$TRABALHO/caddy.log" || true)"
if { [ "$codigo" = "502" ] || [ "$codigo" = "503" ]; } && [ "$com_marcador" = "0" ] && [ "$linhas_erro" -gt 0 ]; then
  registrar PASS "CA6: APIs paradas, hub com token respondeu $codigo; 0 linhas com o token e $linhas_erro linhas de erro do Caddy como controle"
else
  registrar FAIL "CA6: APIs paradas, hub respondeu $codigo; $com_marcador linhas com o token, $linhas_erro linhas de erro"
fi

terminar
