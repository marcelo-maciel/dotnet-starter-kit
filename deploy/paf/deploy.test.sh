#!/usr/bin/env bash
# Failure paths of deploy.sh (O06) that need no Docker and no Apache: usage errors (2), missing environment file (4)
# and the lock held by another deploy (3). deploy.sh runs from a copy in a temporary directory, so nothing under
# deploy/paf/ambientes is written. The full path (drain, health, rollback) is ensaio-deploy.sh.
# Usage: deploy/paf/deploy.test.sh   -> exit 0 only when every case got the expected exit code.
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT
mkdir -p "$TEMP/paf/ambientes"
cp "$DIR/deploy.sh" "$TEMP/paf/deploy.sh"
DEPLOY="$TEMP/paf/deploy.sh"

falhas=0
caso() {
  local esperado="$1" nome="$2"; shift 2
  "$@" >"$TEMP/saida" 2>&1
  local obtido=$?
  if [ "$obtido" = "$esperado" ]; then echo "PASS $nome"; else echo "FAIL $nome (esperado $esperado, obtido $obtido)"; sed 's/^/    /' "$TEMP/saida"; falhas=$((falhas + 1)); fi
}

caso 2 "sem argumentos" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY"
caso 2 "um argumento" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao
caso 2 "ambiente inválido" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" staging v1
caso 2 "tag com espaço" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao 'v1 v2'
caso 2 "tag com aspas" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao "v1';rm"
caso 2 "PAF_REGISTRO ausente" env -u PAF_REGISTRO bash "$DEPLOY" producao v1
caso 2 "PAF_REGISTRO com maiúscula" env PAF_REGISTRO=ghcr.io/Dono bash "$DEPLOY" producao v1
caso 4 "arquivo de ambiente inexistente" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao v1

if command -v flock >/dev/null; then
  : >"$TEMP/paf/ambientes/producao.env"
  flock "$TEMP/paf/ambientes/producao.deploy.lock" sleep 5 &
  segurando=$!
  sleep 1
  caso 3 "trava já tomada por outro deploy" env PAF_REGISTRO=ghcr.io/dono bash "$DEPLOY" producao v1
  wait "$segurando"
else
  echo "SKIPPED trava já tomada por outro deploy: flock ausente nesta máquina (coberto por ensaio-deploy.sh)"
fi

if [ "$falhas" -eq 0 ]; then echo "DEPLOY-TEST OK"; exit 0; fi
echo "DEPLOY-TEST FALHOU ($falhas)"
exit 1
