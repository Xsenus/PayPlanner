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

matrix="$(jq -ec '
  [.jobs[] | select(.name | test("^Backend shard [0-3]$"))] as $shards
  | select(($shards | length == 4)
    and all($shards[]; (.conclusion == "success" or .conclusion == "failure")
      and .runner_group_name == "GitHub Actions" and (.labels | index("windows-2025")) != null)
    and ($shards | map(.runner_id) | unique | length == 4))
  | { include: [$shards[] | {shard: (.name[-1:] | tonumber), conclusion}] | sort_by(.shard) }
' <<<"$jobs")"
printf 'Qualified prerequisites and recorded shard conclusions from run %s: %s\n' "$PRIOR_RUN" "$matrix"
printf 'shards=%s\n' "$matrix" >>"$GITHUB_OUTPUT"
