#!/usr/bin/env bash
# Local rehearsal of the PAF vhost (O03) on the Contabo Apache version: Ubuntu 22.04 apache2 (2.4.52, mpm_event,
# Ubuntu's default module set and MaxRequestWorkers 150), never on the shared host. Everything runs in containers
# named paf-ensaio-apache*, removed on exit.
#
# Topology: the Apache container owns the network namespace; Valkey, two SignalR instances and one stand-in for the
# dashboard and admin join it (--network container:), listening on 127.0.0.1 at the ports of the environment file,
# so the vhost under test is byte-for-byte what gerar-vhost.sh renders for the host. Self-signed certificates go
# where the template expects Let's Encrypt.
#
# Steps, each recorded; exit 0 only when every one passed:
#   1. aplicar-vhost.sh inside the container (conferir, gerar, a2ensite, configtest, graceful);
#   2. plain HTTP answers 301 to https on the same host and path; dashboard and admin hosts reach their backend;
#   3. ensaio-apache.mjs: 101 + WebSockets, kill of the connected instance -> reconnect on the other, rejoin after
#      retry, N WebSockets held S seconds with a graceful in the middle, broadcasts, /health p95;
#   4. logs: zero access_token= in the PAF access and error logs and in the main error log, with the hub lines present
#      (positive control); zero AH00484 (MaxRequestWorkers reached) and AH00485 (scoreboard full);
#   5. configtest failure: an environment whose host has no certificate makes aplicar-vhost.sh exit non-zero, the
#      installed file stays byte-identical (sha256), no graceful happens and the API still answers;
#   6. missing module: with headers disabled, aplicar-vhost.sh aborts before touching the file.
#
# Usage: deploy/paf/apache/ensaio-apache.sh <imagem-signalr> <conexoes> <segundos>
#   <imagem-signalr>: the WebSocket spike hub (PAFV2/spikes/websocket/app: /hubs/spike with WhoAmI, POST /broadcast,
#   GET /health; reads INSTANCE_ID, VALKEY, ASPNETCORE_URLS), e.g. spike-ws-app.
# Needs: docker, node >= 22 (global WebSocket), curl, sha256sum.
set -uo pipefail
# Git Bash: container paths (/opt/...) must reach docker untouched; host paths are passed relative (cd first).
export MSYS_NO_PATHCONV=1

if [ "$#" -ne 3 ]; then
  echo "uso: $0 <imagem-signalr> <conexoes> <segundos>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGEM_HUB="$1"
CONEXOES="$2"
SEGUNDOS="$3"
PREFIXO=paf-ensaio-apache
IMAGEM_APACHE=paf-ensaio-apache:22.04
PORTA_HTTPS=19443
PORTA_HTTP=19480
TOKEN="SEGREDO-ENSAIO-$RANDOM$RANDOM"
TIMEOUT_HTTP_S=10
TRABALHO="$(mktemp -d)"
AMBIENTE="$TRABALHO/ensaio.env"

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
  if [ "$falhas" -eq 0 ]; then echo "ENSAIO-APACHE OK"; exit 0; fi
  echo "ENSAIO-APACHE FALHOU ($falhas)"
  exit 1
}
limpar() {
  echo "--- limpeza"
  docker rm -f "$PREFIXO-app1" "$PREFIXO-app2" "$PREFIXO-frentes" "$PREFIXO-valkey" "$PREFIXO" >/dev/null
  rm -rf "$TRABALHO"
}
trap limpar EXIT

no_apache() {
  docker exec "$PREFIXO" bash -c "$1"
}

# Production example with both instances on: the rendered vhost is the one production gets.
sed -e 's#^COMPOSE_PROJECT_NAME=.*#COMPOSE_PROJECT_NAME=paf-ensaio-apache#' \
    -e 's#^COMPOSE_PROFILES=.*#COMPOSE_PROFILES=duas-instancias#' \
    "$AQUI/../ambientes/producao.env.exemplo" >"$AMBIENTE"
valor() { grep "^$1=" "$AMBIENTE" | tail -n 1 | cut -d= -f2-; }
PORTA_API_A="$(valor PAF_PORTA_API_A)"
PORTA_API_B="$(valor PAF_PORTA_API_B)"
PORTA_DASHBOARD="$(valor PAF_PORTA_DASHBOARD)"
PORTA_ADMIN="$(valor PAF_PORTA_ADMIN)"
HOST_API="$(valor PAF_API_URL | sed 's#^https://##')"
HOST_DASHBOARD="$(valor PAF_DASHBOARD_URL | sed 's#^https://##')"
HOST_ADMIN="$(valor PAF_ADMIN_URL | sed 's#^https://##')"

echo "--- 0. imagem do Apache (ubuntu:22.04 + apache2) e contêineres"
if ! docker build -q -t "$IMAGEM_APACHE" - <<'DOCKERFILE'
FROM ubuntu:22.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends apache2 openssl curl \
 && a2enmod ssl proxy proxy_http proxy_balancer slotmem_shm lbmethod_byrequests headers \
 && a2dissite 000-default && rm -rf /var/lib/apt/lists/*
CMD ["apachectl", "-D", "FOREGROUND"]
DOCKERFILE
then registrar FAIL "build da imagem do Apache"; terminar; fi
existentes="$(docker ps -aq --filter "name=^$PREFIXO")"
if [ -n "$existentes" ]; then docker rm -f $existentes >/dev/null; fi
if ! docker run -d --name "$PREFIXO" -p "127.0.0.1:$PORTA_HTTPS:443" -p "127.0.0.1:$PORTA_HTTP:80" "$IMAGEM_APACHE" >/dev/null \
   || ! docker run -d --name "$PREFIXO-valkey" --network "container:$PREFIXO" valkey/valkey:9.1.0-alpine \
        valkey-server --bind 127.0.0.1 --port 6379 >/dev/null; then
  registrar FAIL "subir Apache e Valkey"; terminar
fi
for instancia in app1:"$PORTA_API_A" app2:"$PORTA_API_B" frentes:"$PORTA_DASHBOARD;http://127.0.0.1:$PORTA_ADMIN"; do
  nome="${instancia%%:*}"
  if ! docker run -d --name "$PREFIXO-$nome" --network "container:$PREFIXO" -e INSTANCE_ID="$nome" \
       -e VALKEY=127.0.0.1:6379 -e ASPNETCORE_URLS="http://127.0.0.1:${instancia#*:}" "$IMAGEM_HUB" >/dev/null; then
    registrar FAIL "subir $nome ($IMAGEM_HUB)"; terminar
  fi
done
versao="$(no_apache 'apachectl -v' | head -n 1)"
echo "Apache do ensaio: $versao"
if ! no_apache "set -e; for h in $HOST_API $HOST_DASHBOARD $HOST_ADMIN; do d=/etc/letsencrypt/live/\$h; mkdir -p \$d; \
     openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj /CN=\$h -keyout \$d/privkey.pem -out \$d/fullchain.pem \
     2>/tmp/openssl.log || { cat /tmp/openssl.log; exit 1; }; done"; then
  registrar FAIL "certificados autoassinados"; terminar
fi
if ! (cd "$AQUI" && docker cp . "$PREFIXO:/opt/paf-apache") || ! (cd "$TRABALHO" && docker cp ensaio.env "$PREFIXO:/opt/paf-apache/ensaio.env"); then
  registrar FAIL "copiar scripts e ambiente para o contêiner"; terminar
fi

echo "--- 1. aplicar-vhost.sh no Apache do ensaio"
if no_apache "bash /opt/paf-apache/aplicar-vhost.sh /opt/paf-apache/ensaio.env"; then registrar PASS "aplicar-vhost.sh instalou e fez graceful"
else registrar FAIL "aplicar-vhost.sh"; terminar; fi
for i in $(seq 1 60); do
  if [ "$(curl -sk -o /dev/null -w '%{http_code}' --max-time 2 "https://127.0.0.1:$PORTA_HTTPS/health")" = "200" ]; then break; fi
  sleep 1
done

echo "--- 2. HTTP, redirecionamento e hosts do dashboard e do admin"
resolver() { printf -- '--resolve %s:%s:127.0.0.1' "$1" "$2"; }
cabecalhos="$(curl -s -D - -o /dev/null --max-time "$TIMEOUT_HTTP_S" \
  $(resolver "$HOST_DASHBOARD" "$PORTA_HTTP") "http://$HOST_DASHBOARD:$PORTA_HTTP/fichas?x=1" | tr -d '\r')"
redirecionamento="$(echo "$cabecalhos" | head -n 1 | cut -d' ' -f2) $(echo "$cabecalhos" | grep -i '^location:' | cut -d' ' -f2-)"
if [ "$redirecionamento" = "301 https://$HOST_DASHBOARD:$PORTA_HTTP/fichas?x=1" ]; then
  registrar PASS "HTTP -> 301 para https no mesmo host, caminho e query ($redirecionamento)"
else registrar FAIL "HTTP -> '$redirecionamento'"; echo "$cabecalhos"; fi
for host in "$HOST_DASHBOARD" "$HOST_ADMIN"; do
  corpo="$(curl -sk --max-time "$TIMEOUT_HTTP_S" $(resolver "$host" "$PORTA_HTTPS") "https://$host:$PORTA_HTTPS/health")"
  if [ "$corpo" = "frentes" ]; then registrar PASS "$host chega ao próprio backend"
  else registrar FAIL "$host respondeu '$corpo'"; fi
done
corpo="$(curl -sk --max-time "$TIMEOUT_HTTP_S" $(resolver "$HOST_API" "$PORTA_HTTPS") "https://$HOST_API:$PORTA_HTTPS/health")"
case "$corpo" in app1|app2) registrar PASS "$HOST_API chega ao balanceador ($corpo)" ;; *) registrar FAIL "$HOST_API respondeu '$corpo'" ;; esac

echo "--- 3. WebSockets (ensaio-apache.mjs)"
if NODE_TLS_REJECT_UNAUTHORIZED=0 PAF_ENSAIO_BASE="https://127.0.0.1:$PORTA_HTTPS" PAF_ENSAIO_HUB=/hubs/spike \
   PAF_ENSAIO_TOKEN="$TOKEN" PAF_ENSAIO_CONTAINER_APACHE="$PREFIXO" PAF_ENSAIO_CONTAINER_APP1="$PREFIXO-app1" \
   PAF_ENSAIO_CONTAINER_APP2="$PREFIXO-app2" PAF_ENSAIO_CONEXOES="$CONEXOES" PAF_ENSAIO_SEGUNDOS="$SEGUNDOS" \
   bash -c 'cd "$1" && node --no-warnings ensaio-apache.mjs' _ "$AQUI"; then
  registrar PASS "ensaio-apache.mjs ($CONEXOES conexões, $SEGUNDOS s)"
else registrar FAIL "ensaio-apache.mjs"; fi

echo "--- 4. logs"
LOGS="/var/log/apache2/paf-ensaio-apache-access.log /var/log/apache2/paf-ensaio-apache-error.log /var/log/apache2/error.log"
com_token="$(no_apache "cat $LOGS | grep -c 'access_token=' || true")"
marcador="$(no_apache "cat $LOGS | grep -c '$TOKEN' || true")"
linhas_hub="$(no_apache "grep -c '\"GET /hubs/spike HTTP/1.1\" 101' /var/log/apache2/paf-ensaio-apache-access.log || true")"
if [ "$com_token" = "0" ] && [ "$marcador" = "0" ] && [ "$linhas_hub" -ge "$CONEXOES" ]; then
  registrar PASS "zero access_token= e zero marcador nos logs; $linhas_hub linhas 101 do hub registradas"
else registrar FAIL "logs: access_token= $com_token, marcador $marcador, linhas 101 do hub $linhas_hub"; fi
limites="$(no_apache "grep -cE 'AH00484|AH00485' /var/log/apache2/error.log || true")"
if [ "$limites" = "0" ]; then registrar PASS "zero AH00484/AH00485 com $CONEXOES WebSockets"
else registrar FAIL "AH00484/AH00485: $limites"; no_apache "grep -E 'AH00484|AH00485' /var/log/apache2/error.log | tail -n 3"; fi
echo "INFO processos apache2 depois do ensaio: $(no_apache 'ps -e -o comm= | grep -c apache2 || true')"

echo "--- 5. configtest falho aborta sem graceful e sem trocar o arquivo"
INSTALADO=/etc/apache2/sites-available/paf-ensaio-apache.conf
antes="$(no_apache "sha256sum $INSTALADO | cut -d' ' -f1")"
gracefuls_antes="$(no_apache "grep -c 'AH00493' /var/log/apache2/error.log || true")"
no_apache "sed 's#^PAF_ADMIN_URL=.*#PAF_ADMIN_URL=https://sem-certificado.paf.exemplo.com.br#' /opt/paf-apache/ensaio.env > /opt/paf-apache/quebrado.env"
saida="$(no_apache "bash /opt/paf-apache/aplicar-vhost.sh /opt/paf-apache/quebrado.env" 2>&1)"
codigo=$?
depois="$(no_apache "sha256sum $INSTALADO | cut -d' ' -f1")"
gracefuls_depois="$(no_apache "grep -c 'AH00493' /var/log/apache2/error.log || true")"
api="$(curl -sk -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT_HTTP_S" "https://127.0.0.1:$PORTA_HTTPS/health")"
if [ "$codigo" -ne 0 ] && [ "$antes" = "$depois" ] && [ "$gracefuls_antes" = "$gracefuls_depois" ] && [ "$api" = "200" ] \
   && echo "$saida" | grep -q "estado anterior restaurado e conferido, sem graceful"; then
  registrar PASS "configtest falho: código $codigo, arquivo igual (sha256), gracefuls $gracefuls_antes -> $gracefuls_depois, API $api"
else
  registrar FAIL "configtest falho: código $codigo, sha256 igual: $([ "$antes" = "$depois" ] && echo sim || echo não), gracefuls $gracefuls_antes -> $gracefuls_depois, API $api"
  echo "$saida" | tail -n 5
fi

echo "--- 6. módulo ausente aborta antes de tocar no arquivo"
# First install on a host without mod_headers: the PAF site is not enabled yet, so only the module check can stop it.
no_apache "a2dissite -q paf-ensaio-apache && a2dismod -q headers"
saida="$(no_apache "bash /opt/paf-apache/aplicar-vhost.sh /opt/paf-apache/ensaio.env" 2>&1)"
codigo=$?
depois="$(no_apache "sha256sum $INSTALADO | cut -d' ' -f1")"
habilitado="$(no_apache "ls /etc/apache2/sites-enabled | grep -c '^paf-ensaio-apache.conf$' || true")"
no_apache "a2enmod -q headers && a2ensite -q paf-ensaio-apache"
if [ "$codigo" -ne 0 ] && [ "$antes" = "$depois" ] && [ "$habilitado" = "0" ] \
   && echo "$saida" | grep -q "módulo headers_module ausente: sudo a2enmod headers"; then
  registrar PASS "headers desligado: código $codigo, arquivo igual, a2enmod indicado"
else registrar FAIL "headers desligado: código $codigo"; echo "$saida" | tail -n 5; fi

terminar
