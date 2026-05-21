# Agent Instructions

## Release Notes

When implementing a user-facing mechanism, changing business rules, permissions, integrations, data handling, or fixing a visible defect, prepare a human-readable release note for the "Что нового" module.

Release notes must be written for end users, not as an internal checklist. Describe what is now available, improved, or fixed, and where the user can see it in the product. Do not include deployment steps, testing instructions, internal file names, or notes about what agents should do.

After each user-visible change, add a new entry to `backend/AppReleases/releases.json` when the change should be shown in "Что нового". If one task contains several changes, ask the user whether to publish one combined release note or separate release notes for each change, unless the user has already specified the format.

Use the existing structure:

- `new`: new functionality.
- `improved`: improved existing behavior.
- `fixed`: corrected bugs or display issues.
- `important`: user-facing rules or caveats that affect work.

Release entries are stored in `backend/AppReleases/releases.json`. The backend syncs these entries into the database on startup, and admins can edit them manually in the "Версия и обновления" settings section after publication.

If a release note cannot be added directly, include the proposed text in the final response so it can be copied into the release editor manually.

## Development Hygiene

Before adding a new mechanism, look for existing modules for notifications, modals, permissions, storage, and API patterns. Reuse existing UI and backend approaches where possible.

Keep changes scoped to the requested behavior. Do not rewrite unrelated release notes, permissions, migrations, or configuration unless the task explicitly requires it.
