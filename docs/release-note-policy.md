# Release Note Policy

PayPlanner publishes end-user release notes in the What New module. The source of truth is `backend/AppReleases/releases.json`; the backend syncs these entries into the database on startup, and admins can adjust them later in the Version and updates settings section.

## When To Add A Release Note

Add a release note when a change is user-facing or changes how users work:

- new functionality;
- improved existing behavior;
- fixed visible defects;
- changed business rules, permissions, integrations, or data handling;
- user-visible caveats that affect daily work.

Do not add a release note for purely internal refactors, tests, documentation-only changes, build metadata, or CI cleanup unless users can see a product behavior change.

## Categories

Use the existing entry structure:

- `new`: new functionality.
- `improved`: improved existing behavior.
- `fixed`: corrected bugs or display issues.
- `important`: user-facing rules or caveats that affect work.

## Writing Style

Write for end users, not for developers. Describe what is now available, improved, or fixed, where users can see it, and how it affects their work. Avoid internal file names, deployment steps, commit hashes, test names, and vague phrases such as "minor fixes" when the visible result can be described.

If one task contains several visible changes, publish one combined note when they belong to one user story. Use separate notes when different user groups or product areas need separate explanations.

## Workflow

1. Before committing, decide whether the change is user-facing.
2. If yes, add a new entry to `backend/AppReleases/releases.json`.
3. Keep the JSON valid and preserve existing entries.
4. Run targeted tests for the changed behavior and the standard quality gate.
5. If a release note cannot be committed directly, include the proposed text in the final handoff so it can be added manually.
