#!/usr/bin/env bash
# Deployment invariants of the PAF (O02): renders deploy/paf/compose.yml once per environment, every profile on,
# and checks the rendered model with verificar-ambiente.mjs. Exit 0 only when every check passed; run it on both
# environment files before every `docker compose up` (README).
#
# Usage: deploy/paf/verificar-ambiente.sh <producao.env> <homologacao.env>
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

for arquivo in "$1" "$2"; do
  if [ ! -f "$arquivo" ]; then
    echo "FAIL arquivo de ambiente inexistente: $arquivo" >&2
    exit 1
  fi
done
# Copies only for the checker (it may run in a container); compose reads the originals.
cp "$1" "$TRABALHO/producao.env"
cp "$2" "$TRABALHO/homologacao.env"
cp "$AQUI/verificar-ambiente.mjs" "$TRABALHO/"
cp "$RAIZ/src/Host/FSH.Starter.Api/appsettings.Production.json" "$TRABALHO/"

renderizar() {
  local ambiente="$1" arquivo="$2"
  # `--profile '*'` renders api-b too: an invariant broken only on the second instance still fails here.
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
