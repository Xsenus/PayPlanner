#!/usr/bin/env bash
set -euo pipefail

tool=tools/PayPlanner.TestShards/bin/Release/net9.0/PayPlanner.TestShards.dll
assembly=backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll
transfer=scripts/hosted-release/transfer.mjs

setup_console() {
  test "$RUNNER_ENVIRONMENT" = github-hosted
  chcp.com 65001
  hook=scripts/hosted-release/console-utf8
  dotnet build "$hook/ConsoleUtf8.csproj" -c Release --nologo
  export DOTNET_STARTUP_HOOKS="$(cygpath -w "$(realpath "$hook/bin/Release/net9.0/ConsoleUtf8.dll")")"
}

case "${1:?Expected a CI phase}" in
  unpack-runtime)
    test "$(dotnet --version)" = "$EXPECTED_SDK_VERSION"
    node "$transfer" verify test-runtime.tar.gz "$EXPECTED_SHA256"
    tar -xzf test-runtime.tar.gz
    node "$transfer" verify test-plan.json "${EXPECTED_MANIFEST_SHA,,}"
    ;;
  shard)
    setup_console
    shard="${2:?Expected a shard index}"
    [[ "$shard" =~ ^[0-3]$ ]]
    mkdir -p test-results
    dotnet "$tool" run --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" \
      --shard "$shard" --results "test-results/shard-$shard" --workers auto --suite all 2>&1 |
      tee "test-results/shard-$shard.log"
    ;;
  restored)
    setup_console
    mkdir -p test-results/restored-browser
    printf '%s\n' '<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>' >test-results/restored-browser/gate.runsettings
    native_exit=0
    PAYPLANNER_TEST_RESTORED_BROWSER=true dotnet vstest "$assembly" \
      --Settings:test-results/restored-browser/gate.runsettings '--Logger:trx;LogFileName=results.trx' \
      --ResultsDirectory:test-results/restored-browser \
      '--TestCaseFilter:FullyQualifiedName~RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource' \
      >test-results/restored-browser/test.log 2>&1 || native_exit=$?
    cat test-results/restored-browser/test.log
    dotnet "$tool" verify-restored --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" \
      --results test-results/restored-browser --exit-code "$native_exit"
    ;;
  linux)
    test "$(uname -s)" = Linux
    scratch="$(mktemp -d)"
    mapfile -t files < <(node --input-type=module -e "import { linuxFiles } from './scripts/windows-vps-deploy.mjs'; console.log(linuxFiles.join('\n'));")
    test "${#files[@]}" -gt 0
    mkdir -m 700 "$scratch/fixtures"
    for name in "${files[@]}"; do
      cp "scripts/$name" "$scratch/$name"
      printf '%s  %s\n' "$(sha256sum "scripts/$name" | cut -d' ' -f1)" "$name"
    done >"$scratch/checksums"
    cd "$scratch"
    export TMPDIR="$scratch/fixtures"
    sha256sum -c checksums
    bash prune-deploy-backups.test.sh
    bash preserve-data-protection-keys.test.sh
    python3 -B deploy-history-archive.test.py
    python3 -B deploy-db-proof.test.py
    python3 -B deploy-stopped-archive.test.py
    bash -n deploy-vps.sh
    python3 -B deploy-vps-admission.test.py
    ;;
  *) exit 64 ;;
esac
