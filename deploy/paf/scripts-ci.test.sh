#!/usr/bin/env bash
# Failure paths of the CI scripts (O08): exigir-segredos.sh, the guards of walk-ci.sh and walk-local.sh, the walk
# matrix partition (walks-da-parte.sh, walks-ci.txt, the parts in paf.yml) and the checks implantar-remoto.sh runs
# before the first SSH connection. Nothing is started or contacted.
# Usage: deploy/paf/scripts-ci.test.sh   -> exit 0 only when every case got the expected exit code.
set -uo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
falhas=0
caso() {
  local esperado="$1" nome="$2"; shift 2
  "$@" >/dev/null 2>&1
  local obtido=$?
  if [ "$obtido" = "$esperado" ]; then echo "PASS $nome"; else echo "FAIL $nome (esperado $esperado, obtido $obtido)"; falhas=$((falhas + 1)); fi
}

caso 2 "segredos: sem argumento" bash "$DIR/exigir-segredos.sh"
caso 2 "segredos: nome inválido" bash "$DIR/exigir-segredos.sh" 'A;rm'
caso 0 "segredos: todos presentes" env PAF_X=1 PAF_Y=2 bash "$DIR/exigir-segredos.sh" PAF_X PAF_Y
caso 1 "segredos: um vazio" env PAF_X=1 PAF_Y= bash "$DIR/exigir-segredos.sh" PAF_X PAF_Y
caso 1 "segredos: um ausente" env -u PAF_Y PAF_X=1 bash "$DIR/exigir-segredos.sh" PAF_X PAF_Y
caso 2 "walk-ci: senha ausente" env -u PAF_SENHA_DEMO bash "$DIR/walk-ci.sh"
caso 2 "walk-ci: senha vazia" env PAF_SENHA_DEMO= bash "$DIR/walk-ci.sh"
caso 2 "walk-ci: senha padrão do kit" env 'PAF_SENHA_DEMO=Password123!' bash "$DIR/walk-ci.sh" demais
caso 2 "walk-ci: sem parte" env PAF_SENHA_DEMO=outra-senha bash "$DIR/walk-ci.sh"
caso 2 "walk-ci: parte desconhecida" env PAF_SENHA_DEMO=outra-senha bash "$DIR/walk-ci.sh" parte-que-nao-existe
caso 2 "walk-local: senha ausente" env -u PAF_SENHA_DEMO PAF_WALK=tests/e2e/paf-vip.walk.mjs bash "$DIR/walk-local.sh"
caso 2 "walk-local: senha padrão do kit" env 'PAF_SENHA_DEMO=Password123!' PAF_WALK=tests/e2e/paf-vip.walk.mjs bash "$DIR/walk-local.sh"
caso 2 "walk-local: walk ausente" env -u PAF_WALK PAF_SENHA_DEMO=outra-senha bash "$DIR/walk-local.sh"
caso 2 "walk-local: walk inexistente" env PAF_SENHA_DEMO=outra-senha PAF_WALK=tests/e2e/paf-nao-existe.walk.mjs bash "$DIR/walk-local.sh"

# Walk matrix partition. Failure paths on scratch partition files; then the real one: every walk in exactly one part,
# and the parts of paf.yml's walk matrix are the file's parts plus "demais".
TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT
caso 2 "walks-da-parte: sem parte" bash "$DIR/walks-da-parte.sh"
caso 2 "walks-da-parte: parte desconhecida" bash "$DIR/walks-da-parte.sh" parte-que-nao-existe
printf '1 paf-vip.walk.mjs\n1 paf-nao-existe.walk.mjs\n' >"$TEMP/inexistente.txt"
caso 1 "walks-da-parte: walk listado que não existe" env PAF_WALKS_PARTES="$TEMP/inexistente.txt" bash "$DIR/walks-da-parte.sh" 1
printf '1 paf-vip.walk.mjs\n2 paf-vip.walk.mjs\n' >"$TEMP/repetido.txt"
caso 1 "walks-da-parte: walk em duas partes" env PAF_WALKS_PARTES="$TEMP/repetido.txt" bash "$DIR/walks-da-parte.sh" 2
printf '# só comentário\n1 paf-vip.walk.mjs\n' >"$TEMP/um.txt"
env PAF_WALKS_PARTES="$TEMP/um.txt" bash "$DIR/walks-da-parte.sh" demais >"$TEMP/demais.out"
caso 0 "walks-da-parte: demais roda" test -s "$TEMP/demais.out"
caso 1 "walks-da-parte: demais sem o listado" grep -qx paf-vip.walk.mjs "$TEMP/demais.out"

partes_arquivo="$(sed -e 's/#.*//' -e '/^[[:space:]]*$/d' "$DIR/walks-ci.txt" | awk '{ print $1 }' | sort -u)"
partes_yml="$(sed -n 's/^ *parte: \[\(.*\)\]$/\1/p' "$DIR/../../.github/workflows/paf.yml" | tr ',' '\n' | tr -d ' ' | sort)"
esperadas="$(printf '%s\ndemais\n' "$partes_arquivo" | sort)"
caso 0 "walks: matriz do paf.yml = partes do walks-ci.txt + demais" test "$partes_yml" = "$esperadas"
distribuidos="$(for parte in $esperadas; do bash "$DIR/walks-da-parte.sh" "$parte"; done | sort)"
todos="$(cd "$DIR/../../clients/dashboard/tests/e2e" && ls paf-*.walk.mjs | sort)"
caso 0 "walks: todo walk em exatamente uma parte" test "$distribuidos" = "$todos"

SHA="$(printf 'a%.0s' $(seq 1 40))"
SEGREDOS=(PAF_SSH_HOST=h.invalid PAF_SSH_USUARIO=u PAF_SSH_CHAVE=k PAF_SSH_KNOWN_HOSTS=kh PAF_DIR_HOST=/opt/paf PAF_REGISTRO=ghcr.io/Dono)
caso 2 "implantar-remoto: ambiente inválido" env "${SEGREDOS[@]}" bash "$DIR/implantar-remoto.sh" staging "$SHA"
caso 2 "implantar-remoto: sha curto" env "${SEGREDOS[@]}" bash "$DIR/implantar-remoto.sh" homologacao abc123
caso 1 "implantar-remoto: segredo ausente" env -u PAF_SSH_CHAVE PAF_SSH_HOST=h.invalid PAF_SSH_USUARIO=u PAF_SSH_KNOWN_HOSTS=kh \
  PAF_DIR_HOST=/opt/paf PAF_REGISTRO=ghcr.io/dono bash "$DIR/implantar-remoto.sh" producao "$SHA"
if [ ! -f "$DIR/deploy.sh" ]; then
  caso 1 "implantar-remoto: deploy.sh (O06) ausente" env "${SEGREDOS[@]}" bash "$DIR/implantar-remoto.sh" homologacao "$SHA"
fi

if [ "$falhas" -eq 0 ]; then echo "SCRIPTS-CI OK"; exit 0; fi
echo "SCRIPTS-CI FALHOU ($falhas)"
exit 1
