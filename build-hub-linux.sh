#!/usr/bin/env bash
set -euo pipefail

workspace="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${workspace}/dist/hub-linux"

mkdir -p "${output}"
dotnet publish "${workspace}/src/CrossRagfair.Hub/CrossRagfair.Hub.csproj" \
  --configuration Release \
  --no-self-contained \
  -p:UseAppHost=false \
  --output "${output}"
cp "${workspace}/LICENSE" "${output}/LICENSE"
cp "${workspace}/deploy/linux/crossragfair-hub.service" "${output}/crossragfair-hub.service"
cp "${workspace}/deploy/linux/hub.env.example" "${output}/hub.env.example"

echo "Portable Linux Hub package created at ${output}"
