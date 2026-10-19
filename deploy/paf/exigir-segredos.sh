#!/usr/bin/env bash
# Fails a CI job up front when a secret it needs is missing (GitHub hands an unset secret over as an empty string,
# and a deploy that starts with an empty host or key fails halfway, or worse, falls back to a default).
#
# Usage: deploy/paf/exigir-segredos.sh NOME_DA_VARIAVEL...   (each one exported by the job's env from secrets.*)
# Exit 0 when every variable is non-empty; 1 listing every missing one (names only, never values); 2 on bad usage.
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "uso: $0 NOME_DA_VARIAVEL..." >&2
  exit 2
fi

faltando=0
for nome in "$@"; do
  if ! [[ "$nome" =~ ^[A-Z_][A-Z0-9_]*$ ]]; then
    echo "nome de variável inválido: $nome" >&2
    exit 2
  fi
  if [ -z "${!nome:-}" ]; then
    echo "::error::secret ausente ou vazio: $nome"
    faltando=1
  fi
done
exit "$faltando"
