#!/usr/bin/env bash
# Restore test of the PAF backup (O11, RNF14: "teste de restauração registrado"). Runs on the host with Docker, in a
# throwaway compose project (deploy/paf/pg-backup/restauracao.yml), never on the environment's database:
#   1. download the copy and its .sha256, refuse a copy without .sha256 (incomplete upload) or with a wrong hash;
#   2. pg_restore into an empty PostgreSQL and compare every table's row count with the counts taken at dump time;
#   3. run the environment's migrator image (`apply`) on the restored copy: any migration it applies was pending;
#   4. HEAD every restored mídia key in the application bucket: a missing object fails the restore;
#   5. register the report in the backup bucket under <ambiente>/restauracoes/.
# Exit 0 only when every step passed; each step's exit code is printed. Removes the throwaway project on exit.
#
# Usage: deploy/paf/restaurar-backup.sh <arquivo.env> <chave sem extensão|ultima>
# Needs: docker compose v2; the backup and migrator images named in the env file.
set -uo pipefail

if [ "$#" -ne 2 ]; then
  echo "uso: $0 <arquivo.env> <chave sem extensão|ultima>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARQUIVO_AMBIENTE="$1"
COPIA="$2"
PRAZO_ETAPA_S=3600

valor() {
  # Last KEY=value line of an env file, literal (no interpolation): only used for plain values.
  grep "^$2=" "$1" | tail -n 1 | cut -d= -f2-
}

[ -f "$ARQUIVO_AMBIENTE" ] || { echo "arquivo de ambiente inexistente: $ARQUIVO_AMBIENTE" >&2; exit 2; }
AMBIENTE="$(valor "$ARQUIVO_AMBIENTE" COMPOSE_PROJECT_NAME)"
[ -n "$AMBIENTE" ] || { echo "COMPOSE_PROJECT_NAME ausente em $ARQUIVO_AMBIENTE" >&2; exit 2; }
PROJETO="$AMBIENTE-restauracao-$(date +%s)"
export PAF_AMBIENTE_RESTAURADO="$AMBIENTE"
PAF_RESTAURACAO_SENHA="$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')"
export PAF_RESTAURACAO_SENHA

# timeout cannot run a shell function, so the compose command line is an array.
DC=(docker compose --project-directory "$AQUI" -f "$AQUI/pg-backup/restauracao.yml" --env-file "$ARQUIVO_AMBIENTE" -p "$PROJETO")

limpar() {
  "${DC[@]}" down -v --remove-orphans >/dev/null 2>&1
  local codigo=$?
  [ "$codigo" -eq 0 ] || echo "AVISO: down -v do projeto $PROJETO saiu com $codigo; confira com docker compose ls" >&2
}
trap limpar EXIT

# Runs one compose subcommand of the throwaway project under the step deadline; stops at the first failure.
etapa() {
  local nome="$1"; shift
  timeout "$PRAZO_ETAPA_S" "${DC[@]}" "$@"
  local codigo=$?
  echo "ETAPA $nome: código $codigo"
  if [ "$codigo" -ne 0 ]; then
    echo "RESTAURACAO FALHOU na etapa $nome ($PROJETO)"
    exit 1
  fi
}

echo "projeto descartável: $PROJETO"
etapa "postgres-temporario" up -d --wait postgres-restauracao
etapa "restaurar" run --rm restaurar restaurar "$COPIA"
etapa "migrator" run --rm migrator
etapa "conferir" run --rm restaurar conferir-restauracao
echo "RESTAURACAO OK ($AMBIENTE, cópia $COPIA)"
