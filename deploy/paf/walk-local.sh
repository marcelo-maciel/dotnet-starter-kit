#!/usr/bin/env bash
# One PAF walk against a REAL stack (walk-pilha.sh, the only copy of the stack recipe): ephemeral PostgreSQL, Valkey
# and RustFS, a fresh seed-demo database, the API and the dashboard on free local ports, then the walk in Chrome. Every
# clients/dashboard/tests/e2e/paf-*.walk.mjs runs through it; what a walk needs from the stack (production build, API
# settings) is set by walk-pilha.sh from its file name. Exit code = the walk's; any setup failure exits non-zero first.
#
# Usage: PAF_SENHA_DEMO=<secret> PAF_WALK=tests/e2e/<walk>.mjs deploy/paf/walk-local.sh
#   PAF_PULAR_BUILD=1  skip the dotnet build (Release binaries already built from this tree).
#   Other API settings go in the environment and reach the API as they are (e.g. PAF__Midia__MargemLeitura=00:00:40).
# Logs stay in the directory printed at the start. Leaves nothing running: processes and containers go on EXIT. One
# run at a time is the safe default (each run uses its own names and free ports, but builds share bin/).
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/walk-pilha.sh"
pilha_validar_segredo
if [ -z "${PAF_WALK:-}" ]; then echo "FAIL uso: PAF_WALK ausente (ex.: tests/e2e/paf-midias.walk.mjs)" >&2; exit 2; fi
if [ ! -f "$PILHA_DASHBOARD/$PAF_WALK" ]; then echo "FAIL uso: $PILHA_DASHBOARD/$PAF_WALK não existe" >&2; exit 2; fi

trap pilha_limpar EXIT
if pilha_precisa_build_web "$PAF_WALK"; then pilha_subir 0 1; else pilha_subir 1 0; fi
(pilha_walk "$PAF_WALK")
echo "WALK-LOCAL OK $PAF_WALK"
