#!/usr/bin/env bash
set -euo pipefail

endpoint="repos/$GITHUB_REPOSITORY/actions/runs/$PRIOR_RUN"
while true; do
  run="$(gh api "$endpoint")"
  jq -e --arg repo "$GITHUB_REPOSITORY" --arg sha "$PRIOR_WORKFLOW_SHA" '
    .head_repository.full_name == $repo and .head_sha == $sha
    and .head_branch == "ci/full-release-ci-pilot" and .run_attempt == 1
    and .path == ".github/workflows/full-release-ci-pilot.yml"
  ' <<<"$run" >/dev/null
  if [[ "$(jq -r .status <<<"$run")" == completed ]]; then break; fi
  sleep 30
done

jobs="$(gh api "$endpoint/attempts/1/jobs?per_page=100")"
jq -e '
  [.jobs[] | select(.name == "frontend" or .name == "frontend-integration"
    or .name == "backend-format" or .name == "backend-build")] as $required
  | ($required | length == 4)
    and all($required[]; .conclusion == "success"
      and .runner_group_name == "GitHub Actions" and (.labels | index("windows-2025")) != null)
' <<<"$jobs" >/dev/null

matrix="$(jq -ec --arg run "$PRIOR_RUN" '
  [.jobs[] | select(.name | test("^Backend shard [0-3]$"))] as $shards
  | select(($shards | length == 4)
    and all($shards[]; (.conclusion == "success" or .conclusion == "failure")
      and .runner_group_name == "GitHub Actions" and (.labels | index("windows-2025")) != null)
    and ($shards | map(.runner_id) | unique | length == 4))
  | { include: [$shards[] | {shard: (.name[-1:] | tonumber), name, conclusion, sourceRun: $run}] | sort_by(.shard) }
' <<<"$jobs")"

if [[ -n "${PREVIOUS_REPAIR_RUN:-}" ]]; then
  repair_endpoint="repos/$GITHUB_REPOSITORY/actions/runs/$PREVIOUS_REPAIR_RUN"
  while true; do
    repair="$(gh api "$repair_endpoint")"
    jq -e --arg repo "$GITHUB_REPOSITORY" --arg sha "$PREVIOUS_REPAIR_SHA" '
      .head_repository.full_name == $repo and .head_sha == $sha
      and .head_branch == "ci/full-release-ci-recovery" and .run_attempt == 1
      and .path == ".github/workflows/full-release-ci-recovery.yml"
    ' <<<"$repair" >/dev/null
    if [[ "$(jq -r .status <<<"$repair")" == completed ]]; then break; fi
    sleep 30
  done
  repair_jobs="$(gh api "$repair_endpoint/attempts/1/jobs?per_page=100")"
  matrix="$(jq -ec --arg run "$PREVIOUS_REPAIR_RUN" --argjson repair "$repair_jobs" '
    .include |= map(
      .name as $name
      | if .conclusion == "failure" and any($repair.jobs[];
          .name == $name and .conclusion == "success" and .runner_group_name == "GitHub Actions"
          and (.labels | index("windows-2025")) != null)
        then .conclusion = "success" | .sourceRun = $run
        else .
        end)
  ' <<<"$matrix")"
fi
printf 'Qualified prerequisites and recorded shard conclusions from run %s: %s\n' "$PRIOR_RUN" "$matrix"
printf 'shards=%s\n' "$matrix" >>"$GITHUB_OUTPUT"
