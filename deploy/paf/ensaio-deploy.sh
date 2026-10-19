#!/usr/bin/env bash
# Local rehearsal of deploy.sh (O06, RNF16), never on the VPS. A Linux "host" container (Ubuntu 22.04 with the
# Contabo Apache 2.4.52, docker CLI and compose, flock, curl) shares the Docker VM's network (--network host), so
# Apache, deploy.sh and the published 127.0.0.1 ports of the compose project mean the same thing they mean on the VPS.
# The tree deploy.sh sees is the one implantar-remoto.sh ships (deploy/paf, deploy/docker/postgres-init and
# appsettings.Production.json), mounted at the path the Docker daemon resolves, so compose's bind mounts work.
#
# Steps, each recorded; exit 0 only when every one passed:
#   1. env files (production with duas-instancias, homologation only for verificar-ambiente), images tagged under a
#      local registry name: v1 and v2 (the given images), v3 = API that never answers health, v4 = migrator that fails;
#   2. aplicar-vhost.sh in the host container, self-signed certificates trusted by the host's CA store (deploy.sh
#      keeps verifying TLS);
#   3. first deploy (v1): exit 0, both instances healthy and enabled; seed-demo with a password fresh per run;
#   4. walk in a loop (ensaio-deploy.mjs) through the vhost, then deploy v2 under load: exit 0, both containers replaced;
#   5. two simultaneous deploys: the second exits 3 (flock) within 10 s, the first exits 0;
#   6. v3 (no health): exit 7, api-a back on v2, healthy and enabled; api-b the same container on v2;
#   7. v4 (migrator fails): exit 6, no API container replaced;
#   8. walk results: no 5xx in emission, retries without duplicate ficha (fichas created = keys), every called senha
#      on the painel, every SenhaChamada EventoId of the outbox received by the hub or recovered by the re-query;
#      AtividadesSessao keys and SessoesEstacao rows unchanged across the deploys.
#
# Usage: deploy/paf/ensaio-deploy.sh <imagem-api> <imagem-migrator> <imagem-frente>
#   e.g. paf-api:o06 paf-dbmigrator:o06 paf-dashboard:o02 (built by publicar-imagem-api.sh / -migrator.sh; the front
#   image serves both dashboard and admin here, the fronts are not under test).
# Needs: docker (Docker Desktop on Windows, or Linux), bash; node runs inside node:22-alpine.
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
REGISTRO=paf-ensaio-registro
PROJETO_PROD=paf-ensaio-deploy-prod
PROJETO_HOMOLOG=paf-ensaio-deploy-homolog
SENHA_DEMO="Ens-$(od -An -tx1 -N12 /dev/urandom | tr -d ' \n')-Aa1!"
LIMITE_DEPLOY_S=900
LIMITE_WALK_S=300
ESPERA_ENTREGA_S=20
LIMITE_FLOCK_S=10

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
  local tag="$1" log="$2"
  timeout "$LIMITE_DEPLOY_S" docker exec -e PAF_REGISTRO="$REGISTRO" -w "$NO_DAEMON" "$HOST_CONTAINER" \
    bash deploy/paf/deploy.sh producao "$tag" >"$TRABALHO/$log" 2>&1
  echo $?
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
status_no_balanceador() {
  no_host "curl -sS --max-time 5 --resolve $HOST_API:443:127.0.0.1 https://$HOST_API/paf-balanceador" \
    | awk -v alvo=">http://127.0.0.1:$1</a>" 'index($0, alvo) { gsub("</td><td>", "|"); gsub(/<[^>]*>/, ""); print; exit }' \
    | cut -d'|' -f6 | tr -d ' '
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
  local exemplo="$1" destino="$2" projeto="$3" perfis="$4" base="$5" subnet="$6" url="$7"
  sed -e "s#^COMPOSE_PROJECT_NAME=.*#COMPOSE_PROJECT_NAME=$projeto#" \
      -e "s#^COMPOSE_PROFILES=.*#COMPOSE_PROFILES=$perfis#" \
      -e "s#^PAF_SUBNET=.*#PAF_SUBNET=$subnet#" \
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
      -e "s#<usuario>#ensaio#g" -e "s#<account-id>#ensaio#g" -e "s#<smtp>#smtp.ensaio.test#g" \
      -e "s#<webhook-de-alerta>#alerta.ensaio.test/x#g" -e "s#<tag>#v1#g" \
      "$exemplo" >"$destino"
  # One fresh value per placeholder: the checker refuses the backup token equal to the application's Storage key.
  while grep -q '<segredo>' "$destino"; do
    sed -i "0,/<segredo>/s//$(segredo)/" "$destino"
  done
}
gerar_ambiente "$AQUI/ambientes/producao.env.exemplo" "$TRABALHO/deploy/paf/ambientes/producao.env" \
  "$PROJETO_PROD" duas-instancias 28 172.31.210.0/24 paf-ensaio-prod.test
gerar_ambiente "$AQUI/ambientes/homologacao.env.exemplo" "$TRABALHO/deploy/paf/ambientes/homologacao.env" \
  "$PROJETO_HOMOLOG" "" 29 172.31.220.0/24 paf-ensaio-homolog.test
HOST_API=api.paf-ensaio-prod.test
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

echo "--- 2. host do ensaio: Apache 2.4.52, docker compose, flock; vhost aplicado"
if ! docker build -q -t "$IMAGEM_HOST" - >/dev/null <<'DOCKERFILE'
FROM ubuntu:22.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends apache2 openssl curl ca-certificates \
 && a2enmod ssl proxy proxy_http proxy_balancer slotmem_shm lbmethod_byrequests headers \
 && a2dissite 000-default && rm -rf /var/lib/apt/lists/*
COPY --from=docker:29-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=docker:29-cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
CMD ["apachectl", "-D", "FOREGROUND"]
DOCKERFILE
then registrar FAIL "build da imagem do host"; terminar; fi
docker rm -f "$HOST_CONTAINER" "$WALK_CONTAINER" >/dev/null 2>&1 || echo "nenhum contêiner anterior do ensaio"
if ! docker run -d --name "$HOST_CONTAINER" --network host -v /var/run/docker.sock:/var/run/docker.sock \
     -v "$NO_DAEMON:$NO_DAEMON" -e TMPDIR="$NO_DAEMON/tmp" "$IMAGEM_HOST" >/dev/null; then
  registrar FAIL "subir o host do ensaio"; terminar
fi
versoes="$(no_host 'apachectl -v | head -n 1; docker compose version; flock --version | head -n 1')"
echo "$versoes"
if ! no_host "set -e; for h in $HOST_API paf-ensaio-prod.test admin.paf-ensaio-prod.test; do d=/etc/letsencrypt/live/\$h; mkdir -p \$d; \
     openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj /CN=\$h -addext subjectAltName=DNS:\$h \
       -keyout \$d/privkey.pem -out \$d/fullchain.pem 2>/tmp/openssl.log || { cat /tmp/openssl.log; exit 1; }; \
     cp \$d/fullchain.pem /usr/local/share/ca-certificates/\$h.crt; done; update-ca-certificates >/dev/null; \
     cp /etc/letsencrypt/live/$HOST_API/fullchain.pem $NO_DAEMON/ca.pem"; then
  registrar FAIL "certificados autoassinados"; terminar
fi
if no_host "bash deploy/paf/apache/aplicar-vhost.sh deploy/paf/ambientes/producao.env" >"$TRABALHO/vhost.log" 2>&1; then
  registrar PASS "aplicar-vhost.sh com o balancer-manager"
else
  registrar FAIL "aplicar-vhost.sh"; mostrar_log vhost.log; terminar
fi
codigo="$(no_host "curl -s -o /dev/null -w '%{http_code}' --max-time 5 --resolve $HOST_API:443:127.0.0.1 -H 'X-Forwarded-For: 203.0.113.9' https://$HOST_API/paf-balanceador")"
if [ "$codigo" = "403" ]; then registrar PASS "balancer-manager recusa requisição com X-Forwarded-For (403)"
else registrar FAIL "balancer-manager com X-Forwarded-For respondeu $codigo, esperado 403"; fi

echo "--- 3. primeira implantação (v1) e massa demo"
codigo="$(deploy v1 deploy-v1.log)"
if [ "$codigo" = "0" ] && [ "$(status_no_balanceador $PORTA_A)" = "InitOk" ] && [ "$(status_no_balanceador $PORTA_B)" = "InitOk" ]; then
  registrar PASS "deploy v1 (primeira): código 0, api-a e api-b habilitadas"
else
  registrar FAIL "deploy v1 (primeira): código $codigo"; mostrar_log deploy-v1.log; terminar
fi
if no_host "PAF_IMAGEM_MIGRATOR=$REGISTRO/paf-dbmigrator:v1 docker compose -f deploy/paf/compose.yml --env-file deploy/paf/ambientes/producao.env \
     run --rm --no-deps -T -e DOTNET_ENVIRONMENT=Development -e 'Seed__DemoPassword=$SENHA_DEMO' migrator seed-demo" >"$TRABALHO/seed.log" 2>&1; then
  registrar PASS "seed-demo com senha nova"
else
  registrar FAIL "seed-demo"; mostrar_log seed.log; terminar
fi

echo "--- 4. walk em laço e deploy v2 sob carga"
SQL_FICHAS='SELECT count(*) FROM paf."Fichas"'
fichas_antes="$(psql_prod "$SQL_FICHAS")"
if ! docker run -d --name "$WALK_CONTAINER" --network host --add-host "$HOST_API:127.0.0.1" \
     -v "$NO_DAEMON:/ensaio" -e NODE_EXTRA_CA_CERTS=/ensaio/ca.pem \
     -e PAF_ENSAIO_API="https://$HOST_API" -e PAF_SENHA_DEMO="$SENHA_DEMO" -e PAF_ENSAIO_PARAR=/ensaio/parar \
     -e PAF_ENSAIO_SAIDA=/ensaio/walk.json -e PAF_ENSAIO_ESPERA_S="$ESPERA_ENTREGA_S" \
     node:22-alpine node /ensaio/deploy/paf/ensaio-deploy.mjs >/dev/null; then
  registrar FAIL "subir o walk"; terminar
fi
walk_conectado() {
  local saida
  saida="$(docker logs "$WALK_CONTAINER" 2>&1)"
  case "$saida" in *"conectados pelo vhost"*) return 0 ;; *) return 1 ;; esac
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
if grep -q "drenagem de :$PORTA_A" "$TRABALHO/deploy-v2.log" && grep -q "drenagem de :$PORTA_B" "$TRABALHO/deploy-v2.log"; then
  registrar PASS "deploy v2 drenou cada instância antes de parar ($(grep -h "drenagem de" "$TRABALHO/deploy-v2.log" | tr '\n' ';'))"
else
  registrar FAIL "deploy v2 sem drenagem registrada das duas instâncias"
fi

echo "--- 5. dois deploys simultâneos"
inicio_segundo=0
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
if [ "$codigo" = "7" ] && [ "$(imagem_de api-a)" = "$REGISTRO/paf-api:v2" ] \
   && [ "$(no_host "curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://127.0.0.1:$PORTA_A/api/v1/paf/saude")" = "200" ] \
   && [ "$(status_no_balanceador $PORTA_A)" = "InitOk" ]; then
  registrar PASS "v3: código 7, api-a de volta em v2, saudável e habilitada"
else
  registrar FAIL "v3: código $codigo, api-a em $(imagem_de api-a), balanceador $(status_no_balanceador $PORTA_A)"; mostrar_log deploy-v3.log
fi
if [ "$(id_de api-b)" = "$id_b" ] && [ "$(imagem_de api-b)" = "$REGISTRO/paf-api:v2" ] && [ "$(status_no_balanceador $PORTA_B)" = "InitOk" ]; then
  registrar PASS "v3: api-b intocada (mesmo contêiner, v2, habilitada)"
else
  registrar FAIL "v3: api-b foi tocada ou ficou fora do balanceador"
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
   && [ "$(status_no_balanceador $PORTA_A)" = "InitOk" ] && [ "$(status_no_balanceador $PORTA_B)" = "InitOk" ]; then
  registrar PASS "v4: código 6, nenhuma instância tocada"
else
  registrar FAIL "v4: código $codigo"; mostrar_log deploy-v4.log
fi

echo "--- 8. resultado do walk e banco"
touch "$TRABALHO/parar"
codigo_walk="$(timeout "$LIMITE_WALK_S" docker wait "$WALK_CONTAINER")"
docker logs "$WALK_CONTAINER" >"$TRABALHO/walk.log" 2>&1
grep -E "^(PASS|FAIL)|reconexão" "$TRABALHO/walk.log"
if [ "$codigo_walk" = "0" ]; then registrar PASS "walk em laço: todas as conferências do walk"
else registrar FAIL "walk em laço: código $codigo_walk"; tail -n 20 "$TRABALHO/walk.log"; fi
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

terminar
