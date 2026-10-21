#!/usr/bin/env bash
# Deployment invariants of the PAF (O02): renders deploy/paf/compose.yml once per environment, every profile on,
# and checks the rendered model with verificar-ambiente.mjs. Exit 0 only when every check passed; run it on both
# environment files before every `docker compose up` (README).
#
# Usage: deploy/paf/verificar-ambiente.sh <producao.env> <homologacao.env>
# A host that runs only homologação (before produção exists) passes the path of producao.env even though it does not
# exist: produção's checks and every cross check print SKIPPED, never PASS. homologacao.env must always exist:
# produção is never checked without the cross checks against it.
# Needs: docker compose v2; node >= 18 on the host, else the node:22-alpine image runs the checker.
set -uo pipefail

if [ "$#" -ne 2 ]; then
  echo "uso: $0 <producao.env> <homologacao.env>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RAIZ="$(cd "$AQUI/../.." && pwd)"
TRABALHO="$(mktemp -d)"
trap 'rm -rf "$TRABALHO"' EXIT

if [ ! -f "$2" ]; then
  echo "FAIL arquivo de ambiente da homologação inexistente: $2 (só a produção pode faltar, e as conferências cruzadas nunca)" >&2
  exit 1
fi
cp "$AQUI/verificar-ambiente.mjs" "$TRABALHO/"
cp "$RAIZ/src/Host/FSH.Starter.Api/appsettings.Production.json" "$TRABALHO/"

renderizar() {
  local ambiente="$1" arquivo="$2"
  if [ ! -f "$arquivo" ]; then
    echo "SKIPPED $ambiente: arquivo de ambiente inexistente ($arquivo); as conferências cruzadas não rodam"
    : >"$TRABALHO/$ambiente.ausente"
    return 0
  fi
  # Copy only for the checker (it may run in a container); compose reads the original.
  cp "$arquivo" "$TRABALHO/$ambiente.env"
  # `--profile '*'` renders any service behind a profile too: none should be, and the checker fails on one.
  # The compose's env_file entry (PAF_ARQUIVO_AMBIENTE) resolves from deploy/paf; the checker compares it line by
  # line with the file given here, so a file naming another environment fails.
  if ! docker compose -f "$AQUI/compose.yml" --env-file "$arquivo" --profile '*' config --format json >"$TRABALHO/$ambiente.json" 2>"$TRABALHO/$ambiente.stderr"; then
    echo "FAIL $ambiente: docker compose config falhou" >&2
    cat "$TRABALHO/$ambiente.stderr" >&2
    exit 1
  fi
}
renderizar producao "$1"
renderizar homologacao "$2"

argumentos=(
  appsettings.Production.json
  producao.env producao.json producao.stderr
  homologacao.env homologacao.json homologacao.stderr
)
if command -v node >/dev/null; then
  cd "$TRABALHO" && node verificar-ambiente.mjs "${argumentos[@]}"
  exit $?
fi
docker run --rm -v "$TRABALHO:/verificacao:ro" -w /verificacao node:22-alpine node verificar-ambiente.mjs "${argumentos[@]}"
