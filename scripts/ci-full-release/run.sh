#!/usr/bin/env bash
set -euo pipefail

helper="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
tool=tools/PayPlanner.TestShards/bin/Release/net9.0/PayPlanner.TestShards.dll
tests=backend/PayPlanner.Api.Tests/bin/Release/net9.0/PayPlanner.Api.Tests.dll
migrations=backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj

check_subject() {
  test "$(git rev-parse HEAD)" = "$SUBJECT_SHA"
}

restore_backend() {
  test "$(dotnet --version)" = "$PILOT_SDK"
  dotnet restore backend/PayPlanner.Api.sln --force --disable-parallel -p:PayPlannerReleaseAudit=true
}

verify_archive() {
  printf '%s  %s\n' "$1" "$2" | sha256sum -c -
}

verify_portable_package() {
  node --input-type=module -e "import { verifyPortablePackage } from './scripts/verify-xsenus-runner.mjs'; verifyPortablePackage();"
}

case "${1:?Expected CI phase}" in
  frontend)
    check_subject
    node --test scripts/xsenus-artifact-store.node-tests.mjs
    node --test scripts/report-backend-execution.node-tests.mjs
    node --test scripts/prepare-deployment-plan.node-tests.mjs
    node --test scripts/windows-vps-access.node-tests.mjs
    node --test scripts/verify-qualified-shard-outputs.node-tests.mjs
    npm ci
    node scripts/validate-deploy-workflow.mjs
    bash scripts/prune-deploy-backups.test.sh
    bash scripts/preserve-data-protection-keys.test.sh
    node --test scripts/storage-legacy-backfill.node-tests.mjs
    node --test scripts/task-notification-refresh-observer.node-tests.mjs
    npm run lint
    npm run typecheck
    npm run audit:dates:test
    npm run audit:encoding
    npm run audit:surface:test
    npm run audit:surface
    npm audit --audit-level=moderate
    node --test scripts/qualified-check-reuse.node-tests.mjs
    npm run test:ci
    PAYPLANNER_READINESS_API_URL="$FE_API_URL" npm run test:legal-flags:strict -- --warn-on-feature-flags-summary-failure
    VITE_API_URL="$FE_API_URL" npm run build
    mkdir -p out/fe
    cp -r dist/* out/fe/
    printf '%s\n' "$SUBJECT_SHA" >out/fe/deploy-revision.txt
    tar -czf frontend.tar.gz -C out fe
    printf 'sha=%s\n' "$(sha256sum frontend.tar.gz | cut -d' ' -f1)" >>"$GITHUB_OUTPUT"
    ;;
  frontend-integration)
    check_subject
    npm ci
    npx playwright install chromium
    npm run test:integration
    ;;
  format)
    check_subject
    restore_backend
    dotnet format backend/PayPlanner.Api.csproj --verify-no-changes --no-restore --verbosity minimal
    DOTNET_GCHeapHardLimit=240000000 CompilerResponseFile="$FORMAT_COMPILER_RESPONSE" \
      dotnet format whitespace "$migrations" --verify-no-changes --no-restore --verbosity diagnostic
    DOTNET_GCHeapHardLimit=240000000 CompilerResponseFile="$FORMAT_COMPILER_RESPONSE" \
      dotnet format style "$migrations" --verify-no-changes --no-restore --verbosity diagnostic
    DOTNET_GCHeapHardLimit=280000000 CompilerResponseFile="$FORMAT_COMPILER_RESPONSE" \
      dotnet format analyzers "$migrations" --verify-no-changes --no-restore --verbosity diagnostic
    dotnet format tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --verify-no-changes --no-restore --verbosity diagnostic
    dotnet format tests/PayPlanner.StorageStageWriterProbe/PayPlanner.StorageStageWriterProbe.csproj --verify-no-changes --no-restore --verbosity diagnostic
    dotnet format backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --verify-no-changes --no-restore --verbosity diagnostic
    ;;
  build)
    check_subject
    restore_backend
    npm run audit:reference-cache
    dotnet build backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false
    dotnet build tools/PayPlanner.TestShards/PayPlanner.TestShards.csproj --no-restore -c Release -m:1 -nr:false -p:UseSharedCompilation=false
    gzip -d -c "$helper/reference-timings.json.gz" > reference-timings.json
    dotnet "$tool" discover --assembly "$tests" --manifest test-plan.json --revision "$SUBJECT_SHA" \
      --shards 4 --execution-profile bounded --timings reference-timings.json
    python -B "$helper/catalogue.py" verify test-plan.json
    dotnet publish backend/PayPlanner.Api.csproj -c Release --no-build --no-restore -o out/be
    verify_portable_package
    python -B "$helper/package.py" pack
    tar -czf test-runtime.tar.gz test-plan.json test-plan.summary.json "$(dirname "$tests")" \
      "$(dirname "$tool")" portable-map.json portable-extra
    printf 'runtime-sha=%s\n' "$(sha256sum test-runtime.tar.gz | cut -d' ' -f1)" >>"$GITHUB_OUTPUT"
    printf 'manifest-sha=%s\n' "$(node -p "require('./test-plan.summary.json').ManifestSha256")" >>"$GITHUB_OUTPUT"
    ;;
  unpack-runtime)
    check_subject
    test "$(dotnet --version)" = "$PILOT_SDK"
    verify_archive "$RUNTIME_SHA" test-runtime.tar.gz
    tar -xzf test-runtime.tar.gz
    verify_archive "$EXPECTED_MANIFEST_SHA" test-plan.json
    ;;
  setup-browsers)
    check_subject
    npm ci
    if [[ "${2:-}" == synthetic ]]; then node scripts/prepare-synthetic-browser-ui.mjs; fi
    npx playwright install chromium
    node --input-type=module -e "import { verifyBrowser } from './scripts/prepare-ci-browsers.mjs'; console.log(await verifyBrowser());" ||
      node --input-type=module -e "import { verifyBrowser } from './scripts/prepare-ci-browsers.mjs'; console.log(await verifyBrowser());"
    if ! node --input-type=module -e "import { verifyBrowser } from './scripts/prepare-ci-browsers.mjs'; console.log(await verifyBrowser('chrome'));"; then
      npx playwright install chrome
      node --input-type=module -e "import { verifyBrowser } from './scripts/prepare-ci-browsers.mjs'; console.log(await verifyBrowser('chrome'));"
    fi
    npm install --global @playwright/cli@0.1.22
    printf 'PAYPLANNER_TEST_PLAYWRIGHT_CLI=%s/@playwright/cli/playwright-cli.js\n' "$(npm root --global)" >>"$GITHUB_ENV"
    printf '%s\n' 'PAYPLANNER_TEST_BROWSER=chrome' >>"$GITHUB_ENV"
    ;;
  shard)
    check_subject
    shard="${2:?Expected a shard number}"
    [[ "$shard" =~ ^[0-3]$ ]]
    mkdir -p test-results
    dotnet "$tool" run --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" \
      --shard "$shard" --results "test-results/shard-$shard" --workers auto --suite all 2>&1 |
      tee "test-results/shard-$shard.log"
    ;;
  restored)
    check_subject
    mkdir -p test-results/restored-browser
    printf '%s\n' '<RunSettings><RunConfiguration><TreatNoTestsAsError>true</TreatNoTestsAsError></RunConfiguration></RunSettings>' >test-results/restored-browser/gate.runsettings
    native_exit=0
    PAYPLANNER_TEST_RESTORED_BROWSER=true dotnet vstest "$tests" \
      --Settings:test-results/restored-browser/gate.runsettings '--Logger:trx;LogFileName=results.trx' \
      --ResultsDirectory:test-results/restored-browser \
      '--TestCaseFilter:FullyQualifiedName~RestoredFullModelQualifiesProtectedSettingsWithCopiedKeysWithoutSourceRing|FullyQualifiedName~FullModelBackupRestoresReadableManagedGenerationsWithoutSource' \
      >test-results/restored-browser/test.log 2>&1 || native_exit=$?
    cat test-results/restored-browser/test.log
    dotnet "$tool" verify-restored --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" \
      --results test-results/restored-browser --exit-code "$native_exit"
    ;;
  union)
    check_subject
    dotnet "$tool" verify --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" \
      --results-root test-results | tee backend-qualification.json
    dotnet "$tool" timings --manifest test-plan.json --manifest-sha "$EXPECTED_MANIFEST_SHA" \
      --results-root test-results --output qualified-timings.json
    ;;
  linux)
    check_subject
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
  package)
    check_subject
    node --test scripts/windows-vps-deploy.node-tests.mjs
    verify_archive "$FRONTEND_SHA" frontend.tar.gz
    python -B "$helper/package.py" unpack
    verify_portable_package
    tar -xzf frontend.tar.gz -C out
    test -s out/be/PayPlanner.Api.dll
    test "$(cat out/fe/deploy-revision.txt)" = "$SUBJECT_SHA"
    test -s out/fe/index.html
    test -s out/fe/payplanner-push-worker.js
    node scripts/prepare-deployment-package.mjs out "$SUBJECT_SHA"
    tar --null --no-recursion -czf "payplanner_$SUBJECT_SHA.tar.gz" -C out -T - <out/deployment-files.nul
    sha256sum "payplanner_$SUBJECT_SHA.tar.gz" >release-package.sha256
    cat release-package.sha256
    ;;
  *) exit 64 ;;
esac
