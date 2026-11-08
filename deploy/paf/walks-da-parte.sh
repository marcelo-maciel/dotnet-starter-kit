#!/usr/bin/env bash
# Walks of one part of the CI walk matrix, one file name per line (paf.yml job "walk", run by walk-ci.sh). The parts
# live in walks-ci.txt; the part "demais" is every paf-*.walk.mjs of clients/dashboard/tests/e2e and
# clients/admin/tests/e2e not listed there, so a new walk always runs somewhere, the same way as the integration matrix.
# Walk names are unique across both folders: walk-pilha.sh finds a walk's app by its name.
#
# Usage: deploy/paf/walks-da-parte.sh <parte>
#   PAF_WALKS_PARTES=<arquivo>  another partition file (scripts-ci.test.sh uses it for the failure paths).
# Exit 2: unknown part, or a part with no walk. Exit 1: a listed walk that does not exist, or listed twice, or a
# name in both folders.
set -euo pipefail

if [ "$#" -ne 1 ] || [ -z "$1" ]; then echo "uso: $0 <parte>" >&2; exit 2; fi
PARTE="$1"
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E_DASHBOARD="$AQUI/../../clients/dashboard/tests/e2e"
E2E_ADMIN="$AQUI/../../clients/admin/tests/e2e"
PARTES="${PAF_WALKS_PARTES:-$AQUI/walks-ci.txt}"

# "<parte> <walk>" lines, comments and blank lines dropped.
listadas="$(sed -e 's/#.*//' -e '/^[[:space:]]*$/d' "$PARTES")"

todas="$(find "$E2E_DASHBOARD" "$E2E_ADMIN" -maxdepth 1 -name 'paf-*.walk.mjs' -printf '%f\n' | sort)"
nos_dois="$(printf '%s\n' "$todas" | uniq -d)"
if [ -n "$nos_dois" ]; then echo "FAIL walk com o mesmo nome em clients/dashboard e clients/admin: $nos_dois" >&2; exit 1; fi

repetidas="$(printf '%s\n' "$listadas" | awk '{ print $2 }' | sort | uniq -d)"
if [ -n "$repetidas" ]; then echo "FAIL $PARTES: walk em mais de uma linha: $repetidas" >&2; exit 1; fi
while read -r _ walk; do
  # A partition with no line at all (only "demais") reads one empty line here.
  if [ -z "$listadas" ]; then break; fi
  if [ -z "$walk" ] || { [ ! -f "$E2E_DASHBOARD/$walk" ] && [ ! -f "$E2E_ADMIN/$walk" ]; }; then
    echo "FAIL $PARTES: $walk não existe em clients/dashboard/tests/e2e nem em clients/admin/tests/e2e" >&2; exit 1
  fi
done <<<"$listadas"

if [ "$PARTE" = demais ]; then
  walks="$(comm -23 <(printf '%s\n' "$todas") <(printf '%s\n' "$listadas" | awk '{ print $2 }' | sort))"
else
  walks="$(printf '%s\n' "$listadas" | awk -v parte="$PARTE" '$1 == parte { print $2 }')"
fi
if [ -z "$walks" ]; then echo "FAIL parte '$PARTE' sem walk em $PARTES" >&2; exit 2; fi
printf '%s\n' "$walks"
