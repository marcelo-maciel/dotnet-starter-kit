#!/usr/bin/env bash
# D04/O15 smoke of a dashboard image: with deploy/paf/dashboard/config.json.template mounted over the kit's template
# (the mount O02's compose must make), the container serves the one-shift inactivity timeout at /config.json; without
# the mount (control) it does not. Runs the kit's real entrypoint (envsubst) and nginx. Exit 0 only when both passed.
#
# Usage: deploy/paf/smoke-inatividade.sh <imagem>   (e.g. built by: docker build -t paf-dashboard:smoke clients/dashboard)
# Needs: docker, node >= 18. Leaves nothing running: containers go on EXIT.
set -uo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: $0 <imagem>" >&2
  exit 2
fi
IMAGEM="$1"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATE="$DIR/dashboard/config.json.template"
SUFIXO="paf-smoke-inat-$$"
COM_MONTAGEM="$SUFIXO-paf"
SEM_MONTAGEM="$SUFIXO-kit"
API_URL="https://api.smoke.invalid"
# The kit image copies its template here (clients/dashboard/Dockerfile) and the entrypoint renders it at start.
ALVO_TEMPLATE="/etc/fsh/config.json.template"
LIMITE_PRONTO_S=30

if [ ! -f "$TEMPLATE" ]; then
  echo "template ausente: $TEMPLATE" >&2
  exit 2
fi
# Docker Desktop on Windows needs a native path for the bind mount; elsewhere the path is used as is.
if command -v cygpath >/dev/null; then
  ORIGEM_TEMPLATE="$(cygpath -m "$TEMPLATE")"
else
  ORIGEM_TEMPLATE="$TEMPLATE"
fi

falhas=0
resultados=()
registrar() {
  resultados+=("$1 $2")
  if [ "$1" != "PASS" ]; then falhas=$((falhas + 1)); fi
}

limpar() {
  echo "--- limpeza"
  docker rm -f "$COM_MONTAGEM" "$SEM_MONTAGEM" >/dev/null
}
trap limpar EXIT

# Prints the host URL of the container's port 80 once nginx answers, or nothing on timeout.
subir() {
  local nome="$1"; shift
  if ! MSYS_NO_PATHCONV=1 docker run -d --name "$nome" -p 127.0.0.1::80 -e FSH_API_URL="$API_URL" "$@" "$IMAGEM" >/dev/null; then
    return 1
  fi
  local porta
  porta="$(docker port "$nome" 80/tcp | head -n 1 | sed 's/.*://')"
  if [ -z "$porta" ]; then return 1; fi
  local i
  for ((i = 0; i < LIMITE_PRONTO_S; i++)); do
    if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$porta/config.json"; then
      echo "http://127.0.0.1:$porta"
      return 0
    fi
    sleep 1
  done
  docker logs "$nome" >&2
  return 1
}

verificar() {
  local nome="$1" esperado="$2"; shift 2
  local url
  url="$(subir "$nome" "$@")"
  if [ -z "$url" ]; then
    registrar FAIL "$esperado: contêiner não respondeu em ${LIMITE_PRONTO_S}s"
    return
  fi
  if PAF_DASHBOARD="$url" PAF_ESPERADO="$esperado" PAF_API_URL="$API_URL" node "$DIR/smoke-inatividade.mjs"; then
    registrar PASS "$esperado"
  else
    registrar FAIL "$esperado: checagens do /config.json"
  fi
}

echo "--- 1. template do PAF montado em $ALVO_TEMPLATE"
verificar "$COM_MONTAGEM" paf -v "$ORIGEM_TEMPLATE:$ALVO_TEMPLATE:ro"

echo "--- 2. controle: imagem sem a montagem"
verificar "$SEM_MONTAGEM" kit

echo "--- resumo"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -eq 0 ]; then
  echo "SMOKE-INATIVIDADE-IMAGEM OK"
  exit 0
fi
echo "SMOKE-INATIVIDADE-IMAGEM FALHOU ($falhas)"
exit 1
