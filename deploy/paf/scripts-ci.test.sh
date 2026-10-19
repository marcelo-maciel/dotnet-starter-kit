#!/usr/bin/env bash
# Failure paths of the CI scripts (O08): exigir-segredos.sh, the secret guard of walk-ci.sh and the checks
# implantar-remoto.sh runs before the first SSH connection. Nothing is started or contacted.
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
caso 2 "walk-ci: senha padrão do kit" env 'PAF_SENHA_DEMO=Password123!' bash "$DIR/walk-ci.sh"

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
