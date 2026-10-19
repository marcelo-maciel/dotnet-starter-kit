#!/usr/bin/env bash
# Demo data of the homologation environment (O09, decision D19, RNF17, ARQ l.386): runs the kit DbMigrator verb
# `seed-demo` once inside the homologation project, with DOTNET_ENVIRONMENT=Development only for that one container,
# instead of editing the kit guard (src/Host/FSH.Starter.DbMigrator/Program.cs), which keeps refusing Production.
#
# Every guard runs before the first docker call, so a refused run opens no connection anywhere:
#   - the project must be the homologation one (paf-homologacao) or a disposable rehearsal (paf-ensaio-*): an
#     allowlist, so the production file and any unknown or mistyped file are refused alike;
#   - the connection string must point at Host=postgres, the project's own PostgreSQL, reachable only inside the
#     project's network: a homologation file edited to reach production's database (published port, host gateway,
#     another host name) is refused;
#   - the demo password comes from PAF_SENHA_DEMO in the caller's environment, never from a file: it must exist, have
#     12 characters or more and differ from the kit defaults that every fixture and appsettings.Development.json carry.
#
# The seed is idempotent (tenants, users, roles and PAF rows are looked up before being created), so running it again
# keeps every table count; deploy/paf/seed-homolog.test.sh proves it on a disposable project.
#
# Usage: PAF_SENHA_DEMO=<senha> deploy/paf/seed-homolog.sh <homologacao.env>
# Needs: docker compose v2, the project's postgres and valkey up (compose starts them otherwise), the migrator image
# named in the file. Exit 0 = seeded; 2 = refused by a guard (nothing touched); 1 = the seed failed or hit the ceiling.
set -uo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: PAF_SENHA_DEMO=<senha> $0 <homologacao.env>" >&2
  exit 2
fi
AQUI="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARQUIVO="$1"
SENHA_MINIMA=12
# Ceiling of the whole one-shot run: catalog and tenant migrations plus the demo tenants on a cold database.
LIMITE_SEED_S=900

recusar() {
  echo "SEED-HOMOLOG RECUSADO: $1" >&2
  exit 2
}

valor() {
  # Last KEY=value line of an env file, literal (no interpolation).
  grep "^$2=" "$1" | tail -n 1 | cut -d= -f2- | tr -d '\r'
}

[ -f "$ARQUIVO" ] || recusar "arquivo de ambiente '$ARQUIVO' não existe"

projeto="$(valor "$ARQUIVO" COMPOSE_PROJECT_NAME)"
case "$projeto" in
  paf-homologacao | paf-ensaio-*) ;;
  *) recusar "COMPOSE_PROJECT_NAME='$projeto'; o seed demo só roda em paf-homologacao (ou paf-ensaio-* no ensaio)" ;;
esac

conexao="$(valor "$ARQUIVO" DatabaseOptions__ConnectionString | tr '[:upper:]' '[:lower:]')"
if ! printf '%s' "$conexao" | grep -Eq '(^|;) *host *= *postgres *(;|$)' \
  || printf '%s' "$conexao" | grep -Eq '(^|;) *server *='; then
  recusar "DatabaseOptions__ConnectionString não aponta só para Host=postgres, o PostgreSQL do próprio projeto"
fi

[ -n "${PAF_SENHA_DEMO:-}" ] || recusar "PAF_SENHA_DEMO ausente: a homologação exige senha demo própria"
case "$PAF_SENHA_DEMO" in
  'Password123!' | '123Pa$$word!') recusar "PAF_SENHA_DEMO é a senha padrão do kit" ;;
esac
[ "${#PAF_SENHA_DEMO}" -ge "$SENHA_MINIMA" ] || recusar "PAF_SENHA_DEMO com menos de $SENHA_MINIMA caracteres"

echo "seed-demo no projeto $projeto (DOTNET_ENVIRONMENT=Development só neste contêiner)"
# `-e Seed__DemoPassword` without a value copies it from this process's environment: the password never reaches argv.
Seed__DemoPassword="$PAF_SENHA_DEMO" timeout "$LIMITE_SEED_S" \
  docker compose -f "$AQUI/compose.yml" --env-file "$ARQUIVO" \
  run --rm -T -e DOTNET_ENVIRONMENT=Development -e Seed__DemoPassword migrator seed-demo
codigo=$?
if [ "$codigo" -ne 0 ]; then
  echo "SEED-HOMOLOG FALHOU (código $codigo)" >&2
  exit 1
fi
echo "SEED-HOMOLOG OK"
