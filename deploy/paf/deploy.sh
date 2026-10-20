#!/usr/bin/env bash
# Rolling deploy of one PAF environment on the host (O06, RNF16, ARQ seção 11). Called by implantar-remoto.sh from
# $PAF_DIR_HOST over SSH; runs the same way by hand on the host. Never run it from a laptop against the VPS.
#
#   deploy/paf/deploy.sh <homologacao|producao> <tag>
#   Env: PAF_REGISTRO (required): images are $PAF_REGISTRO/paf-{api,dbmigrator,dashboard,admin}:<tag>.
#   Rollback = this same script with the previous tag, which it prints and saves before touching anything:
#   deploy/paf/deploy.sh <ambiente> "$(cat deploy/paf/ambientes/<ambiente>.tag-anterior)"
#
# Sequence, each step with its own exit code; nothing after a failed step runs:
#   1. lock per environment (flock -n): a second deploy of the same environment fails at once with 3;
#   2. pre-flight, no mutation: environment files, verificar-ambiente.sh (with the other environment's file when the
#      host has it; without it the cross checks print SKIPPED), the four images present or pulled; the
#      running tag that is not <tag> saved to ambientes/<ambiente>.tag-anterior (after a partial deploy the instances
#      differ: retry or rollback to one of their tags, a third tag fails with 4);
#   3. PostgreSQL, Valkey and Caddy started if missing, never recreated; migrator of the new tag (expand only: the O07
#      barrier refuses any other migration in CI), before any instance changes;
#   4. per API instance, api-a and api-b (one without a running container first: starting it touches nothing): the
#      other must answer health; stop this one (SIGTERM, stop_grace_period 30 s in compose.yml: Kestrel finishes
#      in-flight requests and closes the WebSockets, whose clients reconnect through Caddy to the other instance),
#      start the new tag, GET api/v1/paf/saude on the instance port 200 within 90 s, then wait for Caddy's health
#      check to take it back before the next instance stops. A failure puts that instance back on its previous image
#      and aborts without touching the next instance;
#   5. dashboard and admin on the new tag (after the APIs: the old screens work on the new API, not the reverse);
#   6. ambientes/<ambiente>.imagens.env records the four images now running.
#
# Exit codes: 0 deployed; 2 usage; 3 another deploy of this environment holds the lock; 4 pre-flight failed (nothing
# touched); 5 image missing (nothing touched); 6 infra or migrator failed (no instance touched); 7 an instance failed
# and is back on its previous image and healthy, the next one untouched; 8 an instance failed and could not be put
# back (no previous image, or the previous one is not healthy either): manual action; 9 APIs deployed, dashboard or
# admin failed and were put back on their previous images; 10 dashboard or admin failed and could not be put back.
# Needs on the host: bash, docker compose v2, flock, curl, timeout. Caddy (caddy/Caddyfile) balances the instances by
# its own health check; deploy.sh never talks to it. cloudflared is not touched here (README, "Túnel e Caddy").
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TIMEOUT_DOCKER_S=180
TIMEOUT_PULL_S=600
TIMEOUT_MIGRATOR_S=900
TIMEOUT_HTTP_S=5
LIMITE_SAUDE_S=90
LIMITE_FRENTE_S=60
INTERVALO_S=2
ROTA_SAUDE=/api/v1/paf/saude
# Caddy's active check runs every 2 s and a failed dial keeps an upstream out for 2 s (caddy/Caddyfile): after this
# wait a healthy new instance is back in the pool, so stopping the next one never leaves Caddy without either.
ESPERA_BALANCEADOR_S=6

AMBIENTE=""
TAG=""
ARQUIVO=""

falhar() {
  local codigo="$1"
  shift
  echo "FAIL $*" >&2
  exit "$codigo"
}

passo() {
  echo "--- $*"
}

valor() {
  # Last KEY=value line of the environment file, literal (never sourced: secrets may hold shell characters).
  # A missing key prints nothing; the callers check the value.
  awk -v chave="$1=" 'index($0, chave) == 1 { linha = substr($0, length(chave) + 1) } END { sub(/\r$/, "", linha); printf "%s", linha }' "$ARQUIVO"
}

compose() {
  timeout "$TIMEOUT_DOCKER_S" docker compose -f "$DIR/compose.yml" --env-file "$ARQUIVO" "$@"
}

imagem_em_execucao() {
  # Image reference of the service's running container; empty when the service has no running container.
  local servico="$1" id
  id="$(compose ps -q --status running "$servico")"
  if [ -z "$id" ]; then
    return 0
  fi
  timeout "$TIMEOUT_DOCKER_S" docker inspect -f '{{.Config.Image}}' "$id"
}

porta_de() {
  case "$1" in api-a) valor PAF_PORTA_API_A ;; api-b) valor PAF_PORTA_API_B ;; esac
}

saudavel() {
  local porta="$1"
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT_HTTP_S" "http://127.0.0.1:$porta$ROTA_SAUDE")" = "200" ]
}

aguardar_saude() {
  local porta="$1" decorrido=0
  while [ "$decorrido" -lt "$LIMITE_SAUDE_S" ]; do
    if saudavel "$porta"; then
      echo "saúde de :$porta: 200 em ${decorrido}s"
      return 0
    fi
    sleep "$INTERVALO_S"
    decorrido=$((decorrido + INTERVALO_S))
  done
  return 1
}

aguardar_frente() {
  local porta="$1" decorrido=0
  while [ "$decorrido" -lt "$LIMITE_FRENTE_S" ]; do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT_HTTP_S" "http://127.0.0.1:$porta/")" = "200" ]; then
      return 0
    fi
    sleep "$INTERVALO_S"
    decorrido=$((decorrido + INTERVALO_S))
  done
  return 1
}

garantir_imagem() {
  local imagem="$1"
  if timeout "$TIMEOUT_DOCKER_S" docker image inspect "$imagem" >/dev/null 2>&1; then
    return 0
  fi
  timeout "$TIMEOUT_PULL_S" docker pull -q "$imagem" >/dev/null
}

implantar_instancia() {
  local servico="$1" porta="$2" outras="$3" anterior outra
  passo "instância $servico (:$porta)"
  anterior="$(imagem_em_execucao "$servico")"

  if [ -n "$anterior" ]; then
    # Stopping this one while the other does not answer would take the environment down: it must be serving first.
    for outra in $outras; do
      if ! saudavel "$outra"; then
        falhar 4 "$servico: a outra instância (:$outra) não responde saúde 200; nada foi tocado nesta instância"
      fi
    done
    # SIGTERM: Kestrel stops accepting at once, so Caddy's next dial fails and is retried on the other instance;
    # open WebSockets close within stop_grace_period and their clients reconnect through Caddy to the other one.
    compose stop "$servico" || falhar 8 "$servico: docker compose stop falhou"
    echo "$servico parada; Caddy atende pela outra instância"
  else
    echo "$servico sem contêiner em execução: primeira implantação desta instância"
  fi

  if compose up -d --no-deps "$servico" && aguardar_saude "$porta"; then
    sleep "$ESPERA_BALANCEADOR_S"
    echo "$servico em $PAF_IMAGEM_API, de volta no balanceador"
    return 0
  fi

  echo "FAIL $servico: $PAF_IMAGEM_API sem saúde 200 em ${LIMITE_SAUDE_S}s; volta para a imagem anterior" >&2
  compose logs --no-color --tail 40 "$servico" >&2 || echo "(logs de $servico indisponíveis)" >&2
  if [ -z "$anterior" ]; then
    falhar 8 "$servico: sem imagem anterior para voltar"
  fi
  if PAF_IMAGEM_API="$anterior" compose up -d --no-deps "$servico" && aguardar_saude "$porta"; then
    falhar 7 "$servico de volta em $anterior e saudável; as demais instâncias não foram tocadas"
  fi
  falhar 8 "$servico: a imagem anterior $anterior também não respondeu saúde 200; instância fora do balanceador"
}

implantar_frentes() {
  local anterior_dashboard anterior_admin
  passo "dashboard e admin"
  anterior_dashboard="$(imagem_em_execucao dashboard)"
  anterior_admin="$(imagem_em_execucao admin)"
  if compose up -d --no-deps dashboard admin \
    && aguardar_frente "$(valor PAF_PORTA_DASHBOARD)" && aguardar_frente "$(valor PAF_PORTA_ADMIN)"; then
    return 0
  fi
  echo "FAIL dashboard ou admin sem 200 em ${LIMITE_FRENTE_S}s; voltam para as imagens anteriores" >&2
  if [ -z "$anterior_dashboard" ] || [ -z "$anterior_admin" ]; then
    falhar 10 "dashboard ou admin sem imagem anterior para voltar"
  fi
  if PAF_IMAGEM_DASHBOARD="$anterior_dashboard" PAF_IMAGEM_ADMIN="$anterior_admin" compose up -d --no-deps dashboard admin \
    && aguardar_frente "$(valor PAF_PORTA_DASHBOARD)" && aguardar_frente "$(valor PAF_PORTA_ADMIN)"; then
    falhar 9 "APIs em $TAG; dashboard e admin de volta em $anterior_dashboard e $anterior_admin"
  fi
  falhar 10 "dashboard ou admin não voltaram para as imagens anteriores"
}

principal() {
  if [ "$#" -ne 2 ]; then
    echo "uso: $0 <homologacao|producao> <tag>" >&2
    exit 2
  fi
  AMBIENTE="$1"
  TAG="$2"
  case "$AMBIENTE" in homologacao | producao) ;; *) echo "ambiente inválido: $AMBIENTE" >&2; exit 2 ;; esac
  # Docker tag grammar; also keeps the tag safe inside the commands and files below.
  if ! [[ "$TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
    echo "tag inválida: $TAG" >&2
    exit 2
  fi
  local registro="${PAF_REGISTRO:-}"
  if ! [[ "$registro" =~ ^[a-z0-9.:/_-]+$ ]]; then
    echo "PAF_REGISTRO ausente ou fora de [a-z0-9.:/_-]: '$registro'" >&2
    exit 2
  fi

  ARQUIVO="$DIR/ambientes/$AMBIENTE.env"
  if [ ! -f "$ARQUIVO" ]; then
    falhar 4 "arquivo de ambiente inexistente: $ARQUIVO"
  fi

  passo "1. trava de $AMBIENTE"
  exec 9>"$DIR/ambientes/$AMBIENTE.deploy.lock"
  if ! flock -n 9; then
    falhar 3 "outro deploy de $AMBIENTE está em andamento (flock em ambientes/$AMBIENTE.deploy.lock)"
  fi

  passo "2. conferências antes de tocar em qualquer coisa"
  local outro=producao
  if [ "$AMBIENTE" = producao ]; then outro=homologacao; fi
  if [ "$AMBIENTE" = producao ]; then
    bash "$DIR/verificar-ambiente.sh" "$ARQUIVO" "$DIR/ambientes/$outro.env" || falhar 4 "verificar-ambiente.sh reprovou"
  else
    bash "$DIR/verificar-ambiente.sh" "$DIR/ambientes/$outro.env" "$ARQUIVO" || falhar 4 "verificar-ambiente.sh reprovou"
  fi
  # A new instance (no running container) goes first: starting it touches nothing, and then the existing one has a
  # healthy partner to hand its clients to.
  local servicos=() portas=() servico
  for servico in api-a api-b; do
    if [ -z "$(imagem_em_execucao "$servico")" ]; then
      servicos+=("$servico")
      portas+=("$(porta_de "$servico")")
    fi
  done
  for servico in api-a api-b; do
    if [ -n "$(imagem_em_execucao "$servico")" ]; then
      servicos+=("$servico")
      portas+=("$(porta_de "$servico")")
    fi
  done

  export PAF_IMAGEM_API="$registro/paf-api:$TAG"
  export PAF_IMAGEM_MIGRATOR="$registro/paf-dbmigrator:$TAG"
  export PAF_IMAGEM_DASHBOARD="$registro/paf-dashboard:$TAG"
  export PAF_IMAGEM_ADMIN="$registro/paf-admin:$TAG"
  local imagem
  for imagem in "$PAF_IMAGEM_API" "$PAF_IMAGEM_MIGRATOR" "$PAF_IMAGEM_DASHBOARD" "$PAF_IMAGEM_ADMIN"; do
    garantir_imagem "$imagem" || falhar 5 "imagem indisponível: $imagem"
  done

  # After a partial deploy (exit 7) the instances run different tags; reading api-a alone would record the new tag as
  # the rollback on a retry and lose the old one. The rollback is the running tag that is not the one being deployed.
  local em_execucao tags="" anterior="" registro_anterior="$DIR/ambientes/$AMBIENTE.tag-anterior"
  for servico in "${servicos[@]}"; do
    em_execucao="$(imagem_em_execucao "$servico")"
    if [ -n "$em_execucao" ]; then
      tags="$tags $servico=${em_execucao##*:}"
      if [ "${em_execucao##*:}" != "$TAG" ]; then
        if [ -n "$anterior" ] && [ "$anterior" != "${em_execucao##*:}" ]; then
          falhar 4 "instâncias em tags diferentes ($tags ) e nenhuma é $TAG: sem rollback único; implantar primeiro uma das duas"
        fi
        anterior="${em_execucao##*:}"
      fi
    fi
  done
  if [ -n "$anterior" ]; then
    printf '%s\n' "$anterior" >"$registro_anterior"
    echo "tag anterior: $anterior (em execução:$tags)"
    echo "rollback: deploy/paf/deploy.sh $AMBIENTE $anterior"
  elif [ -n "$tags" ] && [ -f "$registro_anterior" ]; then
    # Redeploy of the tag already running everywhere: the recorded rollback stays.
    echo "tag anterior: $TAG já em execução; rollback continua o registrado"
    echo "rollback: deploy/paf/deploy.sh $AMBIENTE $(cat "$registro_anterior")"
  elif [ -n "$tags" ]; then
    echo "tag anterior: $TAG já em execução e nenhuma registrada; sem rollback para outra tag"
  else
    echo "tag anterior: nenhuma (primeira implantação de $AMBIENTE)"
  fi

  passo "3. PostgreSQL, Valkey, Caddy e migrator de $TAG"
  compose up -d --no-recreate --wait --wait-timeout 120 postgres valkey caddy || falhar 6 "PostgreSQL, Valkey ou Caddy sem saúde"
  timeout "$TIMEOUT_MIGRATOR_S" docker compose -f "$DIR/compose.yml" --env-file "$ARQUIVO" run --rm --no-deps -T migrator \
    || falhar 6 "migrator de $TAG falhou; nenhuma instância foi tocada"

  local i j outras
  for i in "${!servicos[@]}"; do
    outras=""
    for j in "${!portas[@]}"; do
      if [ "$j" != "$i" ]; then outras="$outras ${portas[$j]}"; fi
    done
    implantar_instancia "${servicos[$i]}" "${portas[$i]}" "$outras"
  done

  implantar_frentes

  passo "6. registro das imagens"
  printf 'PAF_IMAGEM_API=%s\nPAF_IMAGEM_MIGRATOR=%s\nPAF_IMAGEM_DASHBOARD=%s\nPAF_IMAGEM_ADMIN=%s\n' \
    "$PAF_IMAGEM_API" "$PAF_IMAGEM_MIGRATOR" "$PAF_IMAGEM_DASHBOARD" "$PAF_IMAGEM_ADMIN" >"$DIR/ambientes/$AMBIENTE.imagens.env"
  echo "DEPLOY OK $AMBIENTE $TAG"
}

# The whole body is one function so bash has read all of it before running: implantar-remoto.sh unpacks a new copy
# of this file before taking the lock, while another deploy may still be executing the old one.
principal "$@"
