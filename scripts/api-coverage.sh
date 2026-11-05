#!/usr/bin/env bash
# API coverage (ADR-0009): unit + integration tests merged, fails under 80 % of lines.
# Needs Docker for the integration tests (Testcontainers starts ArangoDB).
set -euo pipefail
cd "$(dirname "$0")/.."
out="$PWD/coverage/api"
rm -rf "$out" && mkdir -p "$out"
common=(/p:CollectCoverage=true "/p:Include=[LaunchKit.Api]*" "/p:ExcludeByAttribute=GeneratedCodeAttribute%2cCompilerGeneratedAttribute")
dotnet test api/tests/LaunchKit.Api.Tests "${common[@]}" "/p:CoverletOutput=$out/" /p:CoverletOutputFormat=json
dotnet test api/tests/LaunchKit.Api.IntegrationTests "${common[@]}" "/p:CoverletOutput=$out/" \
  "/p:MergeWith=$out/coverage.json" "/p:CoverletOutputFormat=json%2ccobertura" \
  /p:Threshold=80 /p:ThresholdType=line /p:ThresholdStat=Total
