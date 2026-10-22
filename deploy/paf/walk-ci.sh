#!/usr/bin/env bash
# CI run of one part of the walk matrix (paf.yml job "walk"): every walk the part holds (walks-da-parte.sh) on ONE
# stack (walk-pilha.sh): containers, the dotnet build and the dashboard servers once, then per walk a fresh seed-demo
# database and its own API process. A failing walk does not stop the next one, so one run shows every FAIL of the
# part. Exit 0 only when every walk of the part exited 0.
#
# Usage: PAF_SENHA_DEMO=<secret> deploy/paf/walk-ci.sh <parte>
# Needs: docker, dotnet SDK, node 22, `npm ci` done in clients/dashboard, Google Chrome (the walks launch channel
# "chrome"), ffmpeg on PATH for walks that record video (paf.yml installs it). Leaves nothing running on EXIT.
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/walk-pilha.sh"
pilha_validar_segredo
if [ "$#" -ne 1 ]; then echo "uso: $0 <parte>" >&2; exit 2; fi
WALKS="$(bash "$(dirname "${BASH_SOURCE[0]}")/walks-da-parte.sh" "$1")"
echo "--- parte $1: $(printf '%s\n' "$WALKS" | wc -l) walk(s)"
printf '%s\n' "$WALKS"

com_dev=0
com_build=0
while read -r walk; do
  if pilha_precisa_build_web "$walk"; then com_build=1; else com_dev=1; fi
done <<<"$WALKS"

trap pilha_limpar EXIT
pilha_subir "$com_dev" "$com_build"

resultados=()
falhas=0
while read -r walk; do
  inicio=$SECONDS
  echo "=== $walk"
  # errexit is off for a command whose status is tested, even inside a subshell: the walk runs untested, with
  # errexit on, and only its exit code is read.
  set +e
  (set -e; pilha_walk "tests/e2e/$walk") </dev/null
  codigo=$?
  set -e
  if [ "$codigo" -ne 0 ]; then falhas=$((falhas + 1)); fi
  resultados+=("$([ "$codigo" -eq 0 ] && echo PASS || echo "FAIL (código $codigo)") $walk $((SECONDS - inicio)) s")
done <<<"$WALKS"

echo "=== resultado da parte $1"
printf '%s\n' "${resultados[@]}"
if [ "$falhas" -ne 0 ]; then echo "WALK-CI FALHOU ($falhas de ${#resultados[@]})"; exit 1; fi
echo "WALK-CI OK (${#resultados[@]} walks)"
