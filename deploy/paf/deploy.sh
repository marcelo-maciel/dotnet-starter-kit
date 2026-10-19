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
#   2. pre-flight, no mutation: environment files, verificar-ambiente.sh, balancer-manager of the vhost, the four
#      images present or pulled; the running tag that is not <tag> saved to ambientes/<ambiente>.tag-anterior (after
#      a partial deploy the instances differ: retry or rollback to one of their tags, a third tag fails with 4);
#   3. PostgreSQL and Valkey started if missing, never recreated; migrator of the new tag (expand only: the O07
#      barrier refuses any other migration in CI), before any instance changes;
#   4. per API instance (api-a, then api-b when COMPOSE_PROFILES has duas-instancias): the other instances must be
#      healthy and enabled; drain in the balancer (re-read until Apache reports Drn), wait Busy 0 or 60 s, stop
#      (stop_grace_period 30 s in compose.yml), start the new tag, GET api/v1/paf/saude on the instance port 200
#      within 90 s, enable again. A failure puts that instance back on its previous image, enables it and aborts
#      without touching the next instance;
#   5. dashboard and admin on the new tag (after the APIs: the old screens work on the new API, not the reverse);
#   6. ambientes/<ambiente>.imagens.env records the four images now running.
#
# Exit codes: 0 deployed; 2 usage; 3 another deploy of this environment holds the lock; 4 pre-flight failed (nothing
# touched); 5 image missing (nothing touched); 6 infra or migrator failed (no instance touched); 7 an instance failed
# and is back on its previous image, healthy and enabled, the next one untouched; 8 an instance failed and could not
# be put back (no previous image, or the previous one is not healthy either): manual action; 9 APIs deployed, dashboard
# or admin failed and were put back on their previous images; 10 dashboard or admin failed and could not be put back.
# Needs on the host: bash, docker compose v2, flock, curl, timeout; the vhost of this environment applied by
# apache/aplicar-vhost.sh (balancer-manager at /paf-balanceador, loopback only).
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

TIMEOUT_DOCKER_S=180
TIMEOUT_PULL_S=600
TIMEOUT_MIGRATOR_S=900
TIMEOUT_HTTP_S=5
LIMITE_DRENAGEM_S=60
LIMITE_SAUDE_S=90
LIMITE_FRENTE_S=60
INTERVALO_S=2
ROTA_SAUDE=/api/v1/paf/saude
CAMINHO_GESTAO=/paf-balanceador

AMBIENTE=""
TAG=""
ARQUIVO=""
PROJETO=""
HOST_API=""
URL_GESTAO=""
# Port of a member this run drained and has not enabled yet; the EXIT trap enables it if the instance is healthy.
PORTA_DRENADA=""

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

curl_gestao() {
  # The manager answers only on loopback (Require local in the vhost): the API host resolved to 127.0.0.1, with the
  # host's real certificate, never -k.
  curl -sS --max-time "$TIMEOUT_HTTP_S" --resolve "$HOST_API:443:127.0.0.1" "$@"
}

pagina_gestao() {
  # Prints the balancer-manager page; an unreadable page is reported and prints nothing, which every caller
  # treats as "member not found / not in the expected state".
  local pagina
  if ! pagina="$(curl_gestao "$URL_GESTAO")"; then
    echo "balancer-manager ilegível em $URL_GESTAO" >&2
    return 0
  fi
  printf '%s\n' "$pagina"
}

linha_membro() {
  # The member's row of the balancer-manager page: URL, route, redirect, factor, set, status, elected, busy, ...
  local porta="$1"
  pagina_gestao | awk -v alvo=">http://127.0.0.1:$porta</a>" \
    'index($0, alvo) { gsub("</td><td>", "|"); gsub(/<[^>]*>/, ""); print; exit }'
}

status_membro() {
  linha_membro "$1" | cut -d'|' -f6
}

ocupacao_membro() {
  linha_membro "$1" | cut -d'|' -f8 | tr -d ' '
}

marcar_drenagem() {
  # Apache ignores an update without a Referer of the manager or with a stale nonce and still answers 200, so the
  # caller re-reads the status instead of trusting the answer.
  local porta="$1" ligada="$2" nonce
  nonce="$(pagina_gestao | awk -v chave="b=$PROJETO&amp;nonce=" \
    'index($0, chave) { s = substr($0, index($0, chave) + length(chave)); match(s, /^[0-9a-f-]+/); print substr(s, 1, RLENGTH); exit }')"
  if [ -z "$nonce" ]; then
    return 1
  fi
  curl_gestao -o /dev/null -H "Referer: $URL_GESTAO" \
    --data "b=$PROJETO&w=http://127.0.0.1:$porta&w_status_N=$ligada&nonce=$nonce" "$URL_GESTAO"
}

drenar() {
  local porta="$1"
  marcar_drenagem "$porta" 1 || return 1
  PORTA_DRENADA="$porta"
  case "$(status_membro "$porta")" in *Drn*) return 0 ;; *) return 1 ;; esac
}

reabilitar() {
  local porta="$1"
  marcar_drenagem "$porta" 0 || return 1
  case "$(status_membro "$porta")" in *Drn*) return 1 ;; *) PORTA_DRENADA=""; return 0 ;; esac
}

aguardar_ocupacao_zero() {
  local porta="$1" decorrido=0 ocupacao
  while [ "$decorrido" -lt "$LIMITE_DRENAGEM_S" ]; do
    ocupacao="$(ocupacao_membro "$porta")"
    if [ "$ocupacao" = "0" ]; then
      echo "drenagem de :$porta: Busy 0 em ${decorrido}s"
      return 0
    fi
    sleep "$INTERVALO_S"
    decorrido=$((decorrido + INTERVALO_S))
  done
  # WebSockets of painéis and totens stay open until the instance stops; the stop closes them and the clients
  # reconnect on the other member, already the only one Apache routes to.
  echo "drenagem de :$porta: Busy $(ocupacao_membro "$porta") depois de ${LIMITE_DRENAGEM_S}s, segue para a parada"
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

ao_sair() {
  local codigo=$?
  if [ -n "$PORTA_DRENADA" ]; then
    # Interrupted (SIGTERM from the caller's timeout, or a failure between drain and enable): a drained member that
    # answers health goes back to the pool; one that does not stays drained, and that is printed.
    if saudavel "$PORTA_DRENADA" && reabilitar "$PORTA_DRENADA"; then
      echo "saída com código $codigo: membro :$PORTA_DRENADA reabilitado no balanceador" >&2
    else
      echo "RESIDUO membro :$PORTA_DRENADA ficou drenado em $URL_GESTAO (instância sem saúde); ver README, Deploy" >&2
    fi
  fi
  exit "$codigo"
}

implantar_instancia() {
  local servico="$1" porta="$2" outras="$3" anterior outra
  passo "instância $servico (:$porta)"
  anterior="$(imagem_em_execucao "$servico")"

  if [ -n "$anterior" ]; then
    # Draining the only healthy member would take the environment down: the others must be serving first.
    for outra in $outras; do
      if ! saudavel "$outra" || [ "$(status_membro "$outra" | tr -d ' ')" != "InitOk" ]; then
        falhar 4 "$servico: a outra instância (:$outra) não está saudável e habilitada; nada foi tocado nesta instância"
      fi
    done
    drenar "$porta" || falhar 4 "$servico: o balanceador não marcou :$porta como Drn"
    echo "$servico drenada no balanceador"
    aguardar_ocupacao_zero "$porta"
    compose stop "$servico" || falhar 8 "$servico: docker compose stop falhou"
  else
    echo "$servico sem contêiner em execução: primeira implantação desta instância, sem drenagem"
  fi

  if compose up -d --no-deps "$servico" && aguardar_saude "$porta"; then
    reabilitar "$porta" || falhar 8 "$servico: saudável em $PAF_IMAGEM_API, mas o balanceador não a reabilitou"
    echo "$servico em $PAF_IMAGEM_API e reabilitada"
    return 0
  fi

  echo "FAIL $servico: $PAF_IMAGEM_API sem saúde 200 em ${LIMITE_SAUDE_S}s; volta para a imagem anterior" >&2
  compose logs --no-color --tail 40 "$servico" >&2 || echo "(logs de $servico indisponíveis)" >&2
  if [ -z "$anterior" ]; then
    falhar 8 "$servico: sem imagem anterior para voltar"
  fi
  if PAF_IMAGEM_API="$anterior" compose up -d --no-deps "$servico" && aguardar_saude "$porta" && reabilitar "$porta"; then
    falhar 7 "$servico de volta em $anterior, saudável e reabilitada; as demais instâncias não foram tocadas"
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
  trap ao_sair EXIT
  trap 'exit 143' TERM
  trap 'exit 130' INT

  passo "2. conferências antes de tocar em qualquer coisa"
  local outro=producao
  if [ "$AMBIENTE" = producao ]; then outro=homologacao; fi
  if [ "$AMBIENTE" = producao ]; then
    bash "$DIR/verificar-ambiente.sh" "$ARQUIVO" "$DIR/ambientes/$outro.env" || falhar 4 "verificar-ambiente.sh reprovou"
  else
    bash "$DIR/verificar-ambiente.sh" "$DIR/ambientes/$outro.env" "$ARQUIVO" || falhar 4 "verificar-ambiente.sh reprovou"
  fi
  PROJETO="$(valor COMPOSE_PROJECT_NAME)"
  local url_api perfis
  url_api="$(valor PAF_API_URL)"
  HOST_API="${url_api#https://}"
  HOST_API="${HOST_API%%/*}"
  URL_GESTAO="https://$HOST_API$CAMINHO_GESTAO"
  perfis="$(valor COMPOSE_PROFILES)"
  local servicos=(api-a) portas=("$(valor PAF_PORTA_API_A)")
  case ",$perfis," in *,duas-instancias,*) servicos+=(api-b); portas+=("$(valor PAF_PORTA_API_B)") ;; esac

  local porta
  for porta in "${portas[@]}"; do
    if [ -z "$(linha_membro "$porta")" ]; then
      falhar 4 "balancer-manager sem o membro :$porta em $URL_GESTAO (aplicar o vhost: sudo deploy/paf/apache/aplicar-vhost.sh $ARQUIVO)"
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
  local servico em_execucao tags="" anterior="" registro_anterior="$DIR/ambientes/$AMBIENTE.tag-anterior"
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

  passo "3. PostgreSQL, Valkey e migrator de $TAG"
  compose up -d --no-recreate --wait --wait-timeout 120 postgres valkey || falhar 6 "PostgreSQL ou Valkey sem saúde"
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
