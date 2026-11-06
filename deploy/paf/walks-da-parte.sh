#!/usr/bin/env bash
# Walks of one part of the CI walk matrix, one file name per line (paf.yml job "walk", run by walk-ci.sh). The parts
# live in walks-ci.txt; the part "demais" is every clients/dashboard/tests/e2e/paf-*.walk.mjs not listed there, so a
# new walk always runs somewhere, the same way as the integration matrix.
#
# Usage: deploy/paf/walks-da-parte.sh <parte>
#   PAF_WALKS_PARTES=<arquivo>  another partition file (scripts-ci.test.sh uses it for the failure paths).
# Exit 2: unknown part, or a part with no walk. Exit 1: a listed walk that does not exist, or listed twice.
set -euo pipefail

if [ "$#" -ne 1 ] || [ -z "$1" ]; then echo "uso: $0 <parte>" >&2; exit 2; fi
PARTE="$1"
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
E2E="$AQUI/../../clients/dashboard/tests/e2e"
PARTES="${PAF_WALKS_PARTES:-$AQUI/walks-ci.txt}"

# "<parte> <walk>" lines, comments and blank lines dropped.
listadas="$(sed -e 's/#.*//' -e '/^[[:space:]]*$/d' "$PARTES")"

repetidas="$(printf '%s\n' "$listadas" | awk '{ print $2 }' | sort | uniq -d)"
if [ -n "$repetidas" ]; then echo "FAIL $PARTES: walk em mais de uma linha: $repetidas" >&2; exit 1; fi
while read -r _ walk; do
  # A partition with no line at all (only "demais") reads one empty line here.
  if [ -z "$listadas" ]; then break; fi
  if [ -z "$walk" ] || [ ! -f "$E2E/$walk" ]; then echo "FAIL $PARTES: $walk não existe em clients/dashboard/tests/e2e" >&2; exit 1; fi
done <<<"$listadas"

if [ "$PARTE" = demais ]; then
  todas="$(cd "$E2E" && ls paf-*.walk.mjs)"
  walks="$(comm -23 <(printf '%s\n' "$todas" | sort) <(printf '%s\n' "$listadas" | awk '{ print $2 }' | sort))"
else
  walks="$(printf '%s\n' "$listadas" | awk -v parte="$PARTE" '$1 == parte { print $2 }')"
fi
if [ -z "$walks" ]; then echo "FAIL parte '$PARTE' sem walk em $PARTES" >&2; exit 2; fi
printf '%s\n' "$walks"
