# PayPlanner Quality Gates

Run checks from the repository root. During development, select the tests that cover the changed behavior. Before a final handoff, PR or deploy, run the complete gates for the affected areas once the related changes are finished. A documentation-only change does not require application tests; an API contract change requires both frontend and backend gates. Mandatory full CI remains a release barrier.

## Targeted Development Loop

Install or restore dependencies when their inputs change (`npm ci`, `dotnet restore backend\PayPlanner.Api.sln`), not before every unchanged test repeat. Keep the same Release configuration and output location between backend build and test commands.

```powershell
# One existing frontend unit/component file.
npx vitest run src/utils/reportPeriod.test.ts --maxWorkers=1

# One or more exact integration files; omitted files are not a full integration gate.
npm run test:integration -- src/components/Accounts/InstallmentPlanner.integration.test.tsx --reporter=verbose
npm run test:integration -- --help

# Synthetic local backend only; isolate copied data before any launch (see README).
$env:BackgroundServices__Enabled = "false"
$env:Email__Enabled = "false"
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj -c Release --filter "FullyQualifiedName~TaskBillingConcurrencyTests"
```

The integration runner accepts exact repository-relative filenames, including root-level Vite integration files, and the documented `--reporter` options. Unknown files, directories, globs and unsupported flags fail before Vitest starts. It keeps one worker; without selectors, `npm run test:integration` discovers the entire integration catalog. `-t`/`--testNamePattern` is deliberately unsupported because a miss can produce an all-skipped run. A selected-file result must not be reported as the full integration gate.

After a successful build, an unchanged backend test repeat may add `--no-build --no-restore` with the same configuration/output. Rebuild when C# sources, generated inputs, configuration or dependencies change. A test filter alone does not prevent compilation; `--no-restore` does not mean `--no-build`. Do not reuse an old API binary for changed product code or publish it as a new revision. Build and scoped formatting can themselves take minutes; targeted testing does not promise a three-minute cycle for every backend change.

For local runs, place disposable file databases and fixture directories on a fast local SSD and check available space before launching. Set both `TEMP` and `TMP` before starting the test process; the synthetic writable backend passes them through its child-process environment allowlist. Large build artifacts and logs may use a separate location. Keep the original test timeouts and assertions unchanged; investigate disk latency/queues and memory pressure when an environment stall is suspected before changing application code or test budgets.

For local copies of production data, configuration flags alone are insufficient: follow [local email isolation](../README.md#локальная-почта-и-копия-бд), disable `notifications.email.enabled`, and sanitize SMTP settings, delivery queues and mail-producing jobs before launching the backend. Use disposable synthetic databases for destructive checks.

## Final Applicable Local Gates

### Durable local backend run

`node scripts/run-backend-gate.mjs --results E:/ProjectTemp/PayPlanner/<unique-run> --detach`
builds the Release candidate once, copies its runtime, discovers the exact four-part catalog,
runs cheap source guards, then verifies every shard and the five restored-browser cases.
Prepare restored NuGet assets, the dedicated existing PostgreSQL role, pinned browser CLI,
installed browser and environment variables listed below first. The command does not install
a database server, use the working database, or report frontend/format/security checks as done.

Use `--workers 1` to force sequential execution. `auto` admits up to two processes with
at least 8 GiB available memory and four processors, or up to four with 12 GiB and
eight processors. The coordinator checks resources again before starting more work;
already running tests finish normally if available memory falls. xUnit stays sequential
inside each testhost. The four outer parts run one at a time, so their concurrency budgets
cannot multiply. Ordinary testhosts have a 2 GiB managed heap limit; exclusive testhosts
retain 3 GiB. Admission reserves another 4 GiB for native processes, API children and
host headroom; this is not a strict memory ceiling for the complete process tree.
The build requires 8 GiB available memory.
The results volume must have at least 24 GiB free, including the 20 GiB safety reserve.
Redirect `TEMP` and `TMP` to a suitable SSD fixture directory before starting.
If the user explicitly chooses to continue under the current memory pressure, use
`--workers auto --allow-memory-pressure` on that invocation. This records available RAM
and bypasses only the minimum-free-memory preflight; GC limits, disk reserve, complete
test coverage and all qualification checks remain enforced. Timings under contention
must be identified as such. Auto uses one worker below the ordinary parallel thresholds;
the override does not reduce the 8/12 GiB thresholds or the native 1 GiB execution floor.
Use `--workers 1 --allow-memory-pressure` to keep the entire run sequential. Explicit
parallel counts cannot be combined with the pressure override.

The bounded execution plan separates short tests (`fast`), ordinary longer tests
(`heavy`), and browser/process/load-sensitive tests (`exclusive`). Exclusive batches
run alone and are included in the user-facing heavy suite. A checked-in timing baseline
provides scheduling estimates on a fresh checkout; `--timings <prior-run>/timings.json`
can supply newer qualified measurements. Estimates never authorize skipping tests.
Unreviewed new methods use the conservative serial lane until their resource use has
been reviewed. All Theory rows belonging to one method remain in the same batch.

```powershell
# Short development feedback, with an explicitly partial receipt.
npm run test:backend:fast -- --results E:/ProjectTemp/PayPlanner/<unique-fast-run>
# Longer tests, including exclusive browser and process scenarios; also partial.
npm run test:backend:heavy -- --results E:/ProjectTemp/PayPlanner/<unique-heavy-run>
# Complete backend gate, including the five additional restored-browser cases.
npm run test:backend:all -- --results E:/ProjectTemp/PayPlanner/<unique-full-run>
```

Partial suites return `qualified: false` and `subsetPassed: true` only after verifying
their exact selected identities and native exits. They do not produce a full release
receipt. A full run requires the complete four-part union plus restored-browser.
Keep the same `--suite` on resume; a partial run cannot be resumed as a full gate.
Each part preserves its individual batch TRX files and execution resource telemetry.
CI uses this same bounded scheduler inside each of its four jobs; the separate
restored-browser job waits for all four jobs, preserving its exclusive execution.

Read `state.json`, `supervisor.log` and individual stage logs in the results directory.
Use `--prepare-only` to build, copy and fingerprint the candidate without starting tests;
continue with `--resume` without that flag. `prepared` is not a passed test gate.
On Windows the native supervisor owns its process tree through a Job Object, preserves
the child's exit code and terminates descendants when their owner exits. Heavy builds
use `UseSharedCompilation=false` so their memory limits apply to the actual compiler.
After an interruption, the same command with `--resume` reuses only successful parts
with identical source, dependency, runtime and environment fingerprints. It refuses to
reuse live child processes, changed inputs or incomplete/failed results, preserves earlier
attempts, and re-verifies the complete union and restored-browser TRX before qualification.
Do not delete a lock while its runner/children are alive. A changed candidate needs a new
results directory and its applicable final gate.

Known, reviewed documentation outside the test inputs can be excluded with repeatable
`--untested-doc docs/<exact-file>.md`. Unknown/new files remain inputs by default. Do not
exclude tested instructions, fixtures or coverage inventories. Committing identical bytes
does not invalidate evidence; changing an influencing input does. This local receipt is
not a successful GitHub CI or a deployment receipt.

For the development loop, `node scripts/verification-scope.mjs --help` explains area
selection; uncertain/shared changes expand the checks. Release CI remains a full barrier.
`npm run lint` and `npm run typecheck` reuse content-aware local caches;
`npm run typecheck:clean` is available for diagnosing the compiler cache.
`npm run typecheck` includes `npx tsc -p tsconfig.app.json --noEmit` and the
equivalent `tsconfig.node.json` check; both must succeed.

Frontend changes require the complete frontend unit set, lint, typecheck, encoding audit and production build. Include integrations, dates, surface, security and browser checks when the changed boundary or release workflow requires them. Run the commands below for the relevant area, not automatically after every small edit.

```powershell
npm run lint
npm run typecheck
npm run test:ci
npm run test:integration
npm run audit:dates:test
npm run audit:encoding
npm run audit:surface:test
npm run audit:surface
npm run build
```

Backend changes require the full test project in **Release**, relevant migration/integration gates, formatting and dependency checks. Prepare the required disposable PostgreSQL/browser environment before treating a local run as a complete release result:

```powershell
$env:BackgroundServices__Enabled = "false"
$env:Email__Enabled = "false"
dotnet restore backend\PayPlanner.Api.sln --force -p:PayPlannerReleaseAudit=true
dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal
dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --no-restore -c Release
```

The full release environment needs an isolated synthetic PostgreSQL, `PAYPLANNER_TEST_POSTGRES`, `PAYPLANNER_TEST_PG_DUMP`, `PAYPLANNER_TEST_PSQL`, `CI=true`, the installed browsers and pinned Playwright CLI (`PAYPLANNER_TEST_PLAYWRIGHT_CLI`/`PAYPLANNER_TEST_BROWSER`). The separate restored-CRM HTTP/browser step additionally sets `PAYPLANNER_TEST_RESTORED_BROWSER=true` and reuses the Release build with `--no-build --no-restore`. Follow the exact prerequisites, filter and ordering in [deploy.yml](../.github/workflows/deploy.yml); a plain local `dotnet test` without them does not qualify PostgreSQL or browser coverage. Never point these checks at production data or services.

For changed dependencies and the final release workflow, also keep the package security gate:

```powershell
npm audit --audit-level=moderate
```

`npm run lint` can finish with warnings while still returning exit code 0. Treat new warnings in touched files as work to fix or explain.

## Targeted Checks

Choose the smallest set that proves the change during implementation, then the complete applicable gate at final handoff or release. Repeat a successful full gate only after a relevant change, failure or unresolved concern:

- Backend controller/service change: with the local safety environment above, `dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj -c Release --filter "FullyQualifiedName~<TestClassOrArea>"`.
- Frontend routing or permission change: `npm run test:guard`.
- Frontend component or hook change: run the closest Vitest file with `npx vitest run <path>`.
- Frontend integration change: `npm run test:integration -- <exact.integration.test.tsx>`; multiple exact files are supported.
- Date/time behavior change: `npm run audit:dates:test` plus the relevant unit tests.
- New or removed frontend section, HTML form, controller, endpoint, or background service: `npm run audit:surface:update`, then `npm run audit:surface:test` and `npm run audit:surface`.
- Visual or responsive change: `npm run test:visual-smoke` or a focused browser smoke on the changed screen.
- File upload/download change: `npm run test:browser-downloads` plus backend upload/download tests.

## CI Gate Before Deploy

The workflow keeps frontend, formatting and backend qualification as mandatory dependencies of `deploy`. Backend execution uses one analyzed Release build and four separate test jobs; it does not reduce the discovered test catalog.

| Job | Required result |
| --- | --- |
| `frontend` | All frontend audits, lint, types, unit/guard/integration tests, strict legal readiness, build and immutable SHA/attempt-named archive with its uploader artifact ID. |
| `backend-format` | Forced strict restore and complete solution format verification. |
| `backend-build` | Forced strict restore, analyzed test/API and shard-tool builds, complete machine-discovered manifest, compiled test runtime and backend archive. |
| `backend-test` | Four shards (`0..3`), using isolated disposable databases on the existing PostgreSQL 17 service and complete browser/CLI/client prerequisites. `fail-fast: false` preserves the other results when one shard fails. |
| `backend-browser` | The existing restored-CRM filter with browser verification enabled: exactly five cases from the two required methods, every case passed and no skipped/error results. |
| `backend` | All build/test/browser jobs succeeded, then the four result artifacts match the producer manifest exactly: no missing, duplicated, extra or skipped cases. |
| `deploy` | `frontend`, `backend-format` and the aggregate `backend` all succeeded. Download the exact same-run frontend/backend artifact IDs supplied by their qualified producers, then perform the existing VPS deployment and readiness checks. |

`tools/PayPlanner.TestShards` discovers actual VSTest cases and assigns complete method groups deterministically. Discovery fails on deferred or unsupported test metadata rather than silently losing rows. The build job exports the manifest SHA and actual SDK version; downstream jobs use those producer outputs, not a hash recomputed from an untrusted downloaded manifest. The runtime archive preserves relative paths, dependencies and executable permissions. Shards execute the downloaded assembly without compiling it again. The restored browser job uses the same assembly through `dotnet vstest`, retains the actual native exit code and verifies its exact five-case result against the manifest.

The aggregate downloads four qualified shard aliases bound to the source SHA, producer manifest hash and actual SDK version, then invokes the same strict `verify`. Each alias is uploaded only after a successful shard and successful preservation of its immutable attempt diagnostics. Replacing that alias never deletes the separate diagnostic history. Missing artifacts, a changed manifest/runtime, partial collection, duplicates, skipped cases, errors or a failed native test process block release. Default successful dependency conditions are retained: a failure, cancellation or skipped required job never grants VPS access, even when an earlier successful alias exists. Producing the backend archive before tests finish is not permission to deploy it.

Only downloaded NuGet packages are cached across runs. Both .NET restore jobs keep the strict, fail-closed vulnerability audit; the shard tool is restored under the same policy. Build outputs and test results are shared only within this workflow run and revision. Each shard and the restored browser job upload separate SHA/attempt-named TRX/log/qualification evidence even after failure. Their failure status remains mandatory; an `always()` evidence upload cannot turn a failed gate into success.

Large frontend, test-runtime and backend archives are uploaded once per producing attempt, with immutable SHA/attempt names. The upload action's exact artifact ID is passed through job outputs; the backend aggregate forwards only the backend producer's ID to deploy. Consumers reject empty or multiple IDs before download, use no cross-run token/name fallback, and check their actual selected SDK against the producer. This avoids both repeated large uploads and same-name conflicts when a producer reruns. Archive retention remains three days; shard/browser diagnostics and qualified shard aliases remain fourteen days. Missing or expired inputs block release rather than being silently replaced.

Partial retries may reuse successful shards from a previous attempt only for the same producer manifest and SDK. A browser-only or aggregate-only retry therefore does not require new shard artifacts named after its later attempt. A changed producer identity cannot consume old shard aliases; all applicable dependent checks must succeed. The existing job-output dependency model also carries archive IDs: absent producer outputs fail the explicit guards. Keep the final exact-SHA CI result as the release barrier; static workflow tests do not prove a live GitHub retry.

The [GitHub rerun API](https://docs.github.com/en/rest/actions/workflow-runs#re-run-failed-jobs-from-a-workflow-run) describes rerunning failed jobs and their dependents. The [upload action](https://github.com/actions/upload-artifact/tree/v6#using-outputs) exposes the artifact ID, and the [download action](https://github.com/actions/download-artifact/tree/v7#download-artifacts-by-artifact-ids) accepts exact IDs. The workflow deliberately uses one validated positive-decimal ID per download: an empty selector or a partially available multi-ID list must not broaden or weaken release input selection.

`node scripts/validate-deploy-workflow.mjs` verifies the job dependencies, exact four-shard matrix, required commands, same-run artifact IDs, immutable attempt history, success-only qualified aliases, trusted manifest/SDK binding and complete restored-browser qualification. Its negative tests reject missing gates, partial matrices, failure swallowing, empty/substituted IDs, retry collisions and dependency bypasses. Update this contract and its regression cases together when changing workflow structure; unsupported YAML syntax fails closed. Local targeted tests remain useful during development, but neither a single shard nor the separate five-case browser result is a full backend release gate.

The required pre-deploy checks remain:

- backend restore;
- backend format verify;
- backend vulnerable package audit;
- backend tests;
- `npm ci`;
- frontend lint;
- frontend typecheck;
- date/time audit self-test;
- source encoding audit;
- system surface parser self-test and inventory freshness check;
- frontend package audit;
- frontend guard and unit tests;
- frontend integration tests;
- strict legal feature flag readiness.

Do not weaken, skip or replace mandatory full CI with a targeted result to unblock a deploy. Fix the failing check and qualify the affected cases before the next complete release gate.
