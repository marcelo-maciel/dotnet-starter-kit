#!/usr/bin/env bash
# Renders paf-vhost.conf.template for one environment file of the PAF compose (O02) and prints it on stdout.
# Reads only plain values of the file (no interpolation): COMPOSE_PROJECT_NAME, COMPOSE_PROFILES, PAF_PORTA_API_A,
# PAF_PORTA_API_B, PAF_PORTA_DASHBOARD, PAF_PORTA_ADMIN, PAF_API_URL, PAF_DASHBOARD_URL, PAF_ADMIN_URL.
# The second balancer member exists only when COMPOSE_PROFILES has duas-instancias, the profile that starts api-b:
# a member pointing at a port nobody listens on would cost a failed connect on every retry window.
# Exit 0 with the rendered file; exit 1, nothing on stdout, when any value is missing or malformed.
#
# Usage: deploy/paf/apache/gerar-vhost.sh <ambiente.env>
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: $0 <ambiente.env>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARQUIVO="$1"
if [ ! -f "$ARQUIVO" ]; then
  echo "FAIL arquivo de ambiente inexistente: $ARQUIVO" >&2
  exit 1
fi

valor() {
  # Last KEY=value line, literal, CR stripped (a file edited on Windows must not leak \r into the vhost).
  local linha
  linha="$(grep "^$1=" "$ARQUIVO" | tail -n 1 | tr -d '\r')"
  printf '%s' "${linha#*=}"
}

falhas=0
exigir() {
  local nome="$1" conteudo="$2" padrao="$3"
  if ! printf '%s' "$conteudo" | grep -Eq "^$padrao\$"; then
    echo "FAIL $nome inválido no arquivo de ambiente: '$conteudo'" >&2
    falhas=$((falhas + 1))
  fi
}

host_da_url() {
  # https://host[/...] -> host. Only https: the template terminates TLS for exactly these names.
  local url="$1" resto
  case "$url" in https://*) ;; *) printf '%s' "$url"; return ;; esac
  resto="${url#https://}"
  printf '%s' "${resto%%/*}"
}

PROJETO="$(valor COMPOSE_PROJECT_NAME)"
PERFIS="$(valor COMPOSE_PROFILES)"
PORTA_API_A="$(valor PAF_PORTA_API_A)"
PORTA_API_B="$(valor PAF_PORTA_API_B)"
PORTA_DASHBOARD="$(valor PAF_PORTA_DASHBOARD)"
PORTA_ADMIN="$(valor PAF_PORTA_ADMIN)"
HOST_API="$(host_da_url "$(valor PAF_API_URL)")"
HOST_DASHBOARD="$(host_da_url "$(valor PAF_DASHBOARD_URL)")"
HOST_ADMIN="$(host_da_url "$(valor PAF_ADMIN_URL)")"

PADRAO_HOST='[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+'
PADRAO_PORTA='[1-9][0-9]{0,4}'
exigir COMPOSE_PROJECT_NAME "$PROJETO" 'paf-[a-z0-9-]+'
exigir PAF_PORTA_API_A "$PORTA_API_A" "$PADRAO_PORTA"
exigir PAF_PORTA_DASHBOARD "$PORTA_DASHBOARD" "$PADRAO_PORTA"
exigir PAF_PORTA_ADMIN "$PORTA_ADMIN" "$PADRAO_PORTA"
exigir "host de PAF_API_URL" "$HOST_API" "$PADRAO_HOST"
exigir "host de PAF_DASHBOARD_URL" "$HOST_DASHBOARD" "$PADRAO_HOST"
exigir "host de PAF_ADMIN_URL" "$HOST_ADMIN" "$PADRAO_HOST"

MEMBRO_API_B="        # api-b off: COMPOSE_PROFILES has no duas-instancias"
case ",$PERFIS," in
  *,duas-instancias,*)
    exigir PAF_PORTA_API_B "$PORTA_API_B" "$PADRAO_PORTA"
    MEMBRO_API_B="        BalancerMember \"http://127.0.0.1:$PORTA_API_B\" upgrade=websocket connectiontimeout=3 retry=10"
    ;;
esac

if [ "$(printf '%s\n%s\n%s\n' "$HOST_API" "$HOST_DASHBOARD" "$HOST_ADMIN" | sort -u | wc -l | tr -d ' ')" != "3" ]; then
  echo "FAIL PAF_API_URL, PAF_DASHBOARD_URL e PAF_ADMIN_URL precisam de três hosts diferentes" >&2
  falhas=$((falhas + 1))
fi
if [ "$falhas" -ne 0 ]; then
  exit 1
fi

# Every value above matched a pattern without '#', '&' or a backslash, so it is safe as a sed replacement.
saida="$(sed -e "s#@@PROJETO@@#$PROJETO#g" \
  -e "s#@@HOST_API@@#$HOST_API#g" -e "s#@@HOST_DASHBOARD@@#$HOST_DASHBOARD#g" -e "s#@@HOST_ADMIN@@#$HOST_ADMIN#g" \
  -e "s#@@PORTA_API_A@@#$PORTA_API_A#g" -e "s#@@PORTA_DASHBOARD@@#$PORTA_DASHBOARD#g" \
  -e "s#@@PORTA_ADMIN@@#$PORTA_ADMIN#g" \
  -e "s|^@@MEMBRO_API_B@@\$|$MEMBRO_API_B|" \
  "$AQUI/paf-vhost.conf.template")"
if printf '%s' "$saida" | grep -q '@@'; then
  echo "FAIL marcador sem valor no template: $(printf '%s' "$saida" | grep -o '@@[A-Z_]*@@' | sort -u | tr '\n' ' ')" >&2
  exit 1
fi
printf '%s\n' "$saida"
