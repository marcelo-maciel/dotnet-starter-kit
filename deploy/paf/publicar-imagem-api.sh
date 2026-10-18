#!/usr/bin/env bash
# Publishes the PAF API image through the .NET SDK on the csproj's ContainerFamily (noble, full Ubuntu with tzdata).
# Never the kit's src/Host/FSH.Starter.Api/Dockerfile: it runs on noble-chiseled, which has no /usr/share/zoneinfo,
# and PafRelogio then fails every emission (RNF10). TZ is not baked in: the compose sets TZ=America/Sao_Paulo.
#
# Usage: deploy/paf/publicar-imagem-api.sh <tag>   -> image paf-api:<tag> in the local Docker daemon
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "uso: $0 <tag>" >&2
  exit 2
fi
TAG="$1"
RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# ContainerImageTags (plural) overrides src/Directory.Build.props; setting ContainerImageTag alongside it is an SDK error.
# NuGetAudit=false: pre-existing audit failure of the kit's package set, tracked outside the PAF.
dotnet publish "$RAIZ/src/Host/FSH.Starter.Api/FSH.Starter.Api.csproj" \
  -c Release --os linux --arch x64 \
  -t:PublishContainer \
  -p:NuGetAudit=false \
  -p:ContainerRepository=paf-api \
  -p:ContainerImageTags="$TAG"

echo "imagem publicada: paf-api:$TAG"
