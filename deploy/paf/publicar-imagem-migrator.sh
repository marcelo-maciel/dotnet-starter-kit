#!/usr/bin/env bash
# Publishes the PAF DbMigrator image through the .NET SDK on the csproj's ContainerFamily (noble), the same path as
# publicar-imagem-api.sh (O01 contract): never the kit's chiseled src/Host/FSH.Starter.DbMigrator/Dockerfile.
# The compose of the PAF (deploy/paf/compose.yml) runs it as the one-shot `migrator` service with TZ from the env file.
#
# Usage: deploy/paf/publicar-imagem-migrator.sh <tag>   -> image paf-dbmigrator:<tag> in the local Docker daemon
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: $0 <tag>" >&2
  exit 2
fi
TAG="$1"
RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# ContainerImageTags (plural) overrides src/Directory.Build.props; NuGetAudit=false: pre-existing kit audit failure.
dotnet publish "$RAIZ/src/Host/FSH.Starter.DbMigrator/FSH.Starter.DbMigrator.csproj" \
  -c Release --os linux --arch x64 \
  -t:PublishContainer \
  -p:NuGetAudit=false \
  -p:ContainerRepository=paf-dbmigrator \
  -p:ContainerImageTags="$TAG"

echo "imagem publicada: paf-dbmigrator:$TAG"
