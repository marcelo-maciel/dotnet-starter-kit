#!/usr/bin/env bash
# CI side of a PAF deploy (O08): copies deploy/paf of this checkout to the host over SSH and runs
# deploy/paf/deploy.sh <ambiente> <sha> there (O06 owns that script: drain, health and rollback per instance).
# Images are expected at $PAF_REGISTRO/paf-{api,dbmigrator,dashboard,admin}:<sha>, pushed by the homologação job.
#
# Usage: deploy/paf/implantar-remoto.sh <homologacao|producao> <sha de 40 hex>
# Env (from the environment's secrets): PAF_SSH_HOST PAF_SSH_USUARIO PAF_SSH_CHAVE PAF_SSH_KNOWN_HOSTS PAF_DIR_HOST
# PAF_REGISTRO. Exit code = deploy.sh's; anything missing fails before the first connection.
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "uso: $0 <homologacao|producao> <sha>" >&2
  exit 2
fi
AMBIENTE="$1"
SHA="$2"
case "$AMBIENTE" in homologacao | producao) ;; *) echo "ambiente inválido: $AMBIENTE" >&2; exit 2 ;; esac
if ! [[ "$SHA" =~ ^[0-9a-f]{40}$ ]]; then
  echo "sha inválido (40 hex): $SHA" >&2
  exit 2
fi
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# GHCR repository names are lowercase; github.repository_owner keeps the account's casing.
PAF_REGISTRO="${PAF_REGISTRO:-}"
PAF_REGISTRO="${PAF_REGISTRO,,}"
bash "$DIR/exigir-segredos.sh" PAF_SSH_HOST PAF_SSH_USUARIO PAF_SSH_CHAVE PAF_SSH_KNOWN_HOSTS PAF_DIR_HOST PAF_REGISTRO
if [ ! -f "$DIR/deploy.sh" ]; then
  echo "::error::deploy/paf/deploy.sh ausente (tarefa O06)"
  exit 1
fi
# Quoted into a remote shell command line below: refuse anything that could break out of the quotes.
if ! [[ "$PAF_DIR_HOST" =~ ^/[A-Za-z0-9._/-]+$ && "$PAF_REGISTRO" =~ ^[a-z0-9.:/_-]+$ ]]; then
  echo "PAF_DIR_HOST ou PAF_REGISTRO com caractere fora do permitido" >&2
  exit 2
fi

TEMP="$(mktemp -d)"
trap 'rm -rf "$TEMP"' EXIT
install -m 600 /dev/null "$TEMP/chave"
printf '%s\n' "$PAF_SSH_CHAVE" >"$TEMP/chave"
printf '%s\n' "$PAF_SSH_KNOWN_HOSTS" >"$TEMP/known_hosts"
SSH=(ssh -i "$TEMP/chave" -o "UserKnownHostsFile=$TEMP/known_hosts" -o StrictHostKeyChecking=yes -o BatchMode=yes
  -o ConnectTimeout=15 -o ServerAliveInterval=15 -o ServerAliveCountMax=4 "$PAF_SSH_USUARIO@$PAF_SSH_HOST")

# Only the files of this checkout go over; the host's ambientes/*.env (not in git) stay where they are.
tar -C "$DIR/.." -cf - paf | "${SSH[@]}" "mkdir -p '$PAF_DIR_HOST/deploy' && tar -C '$PAF_DIR_HOST/deploy' -xf -"
"${SSH[@]}" "cd '$PAF_DIR_HOST' && PAF_REGISTRO='$PAF_REGISTRO' timeout 1800 deploy/paf/deploy.sh '$AMBIENTE' '$SHA'"
