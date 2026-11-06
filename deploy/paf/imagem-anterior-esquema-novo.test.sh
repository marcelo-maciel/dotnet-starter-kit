#!/usr/bin/env bash
# Failure paths of imagem-anterior-esquema-novo.sh that need no docker nor build: usage errors exit 2 before touching
# anything, and a previous ref without the PAF module says NAO-SE-APLICA instead of pretending it ran.
# Usage: bash deploy/paf/imagem-anterior-esquema-novo.test.sh   (needs the git history: fetch-depth 0 in CI)
set -uo pipefail

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="$RAIZ/deploy/paf/imagem-anterior-esquema-novo.sh"
SAIDA="$(mktemp)"
trap 'rm -f "$SAIDA"' EXIT
falhas=0
casos=0

caso() {
  local nome="$1" esperado="$2" trecho="$3"; shift 3
  local codigo
  casos=$((casos + 1))
  bash "$SCRIPT" "$@" >"$SAIDA" 2>&1
  codigo=$?
  if [ "$codigo" -eq "$esperado" ] && grep -q -- "$trecho" "$SAIDA"; then
    echo "PASS $nome"
  else
    echo "FAIL $nome :: exit $codigo (esperado $esperado), saída: $(head -c 300 "$SAIDA")"
    falhas=$((falhas + 1))
  fi
}

caso "sem argumento sai 2" 2 "uso:"
caso "argumento vazio sai 2" 2 "uso:" ""
caso "dois argumentos saem 2" 2 "uso:" HEAD HEAD
caso "ref inexistente sai 2" 2 "FAIL ref:" "ref-que-nao-existe-$$"

PRIMEIRO_COM_PAF="$(git -C "$RAIZ" log --format=%H --diff-filter=A -- src/Modules/PAF/Modules.PAF/PafModule.cs | tail -n 1)"
if [ -z "$PRIMEIRO_COM_PAF" ]; then
  echo "FAIL histórico: commit que criou o PafModule.cs não encontrado (clone raso?)"
  falhas=$((falhas + 1))
else
  caso "ref sem o módulo PAF diz NAO-SE-APLICA e não sobe nada" 0 "NAO-SE-APLICA" "$PRIMEIRO_COM_PAF^"
fi

echo "$((casos - falhas))/$casos casos"
[ "$falhas" -eq 0 ]
