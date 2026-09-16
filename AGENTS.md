# Agent Instructions

## Release Notes

When implementing a user-facing mechanism, changing business rules, permissions, integrations, data handling, or fixing a visible defect, prepare a human-readable release note for the "Что нового" module.

Release notes must be written for end users, not as an internal checklist. Describe what is now available, improved, or fixed, and where the user can see it in the product. Do not include deployment steps, testing instructions, internal file names, or notes about what agents should do.

Write release notes in clear, natural language that is easy for non-technical users to understand. Include enough detail for the user to understand what changed, how it affects their work, and where to find the updated behavior in the product. Avoid programming terms, internal architecture details, commit references, and vague phrases like "minor fixes" when a visible result can be described.

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

When the legacy task card and the new task card window both exist, any change to task business logic, saving rules, permissions, statuses, result handling, deadlines, reminders, files, comments, subtasks, labels, priorities, CRM links, or related API behavior must be applied to both forms unless the task explicitly says otherwise. Verify both the old and the new task card before considering the change complete, including the administrator setting that switches which card is shown to all users.

## Local Email Safety

Never allow a local, development, test, or preview environment to send email through production SMTP servers or to real recipients. This rule also applies when the local database is a copy of the production database and therefore contains working mail settings, credentials, sender addresses, notification rules, or queued deliveries.

Before starting a local backend or running any workflow that can create users, notifications, reminders, approvals, calendar events, invoices, or other email-producing records, verify that outbound email is disabled or redirected to a local mail sink/dry-run transport. Do not rely only on the application environment name: inspect effective settings from configuration, environment variables, secrets, and the restored database.

For this project, every local backend launch must explicitly set `BackgroundServices__Enabled=false` and `Email__Enabled=false`. When a production database copy is used, also set the database setting `notifications.email.enabled` to `false` (or remove the production SMTP settings), because database settings can take precedence over `Email__Enabled`. Treat these values as mandatory local safety controls, not optional developer preferences.

If safe email isolation cannot be confirmed, do not start the email-producing workflow. Disable the mail sender, replace the transport with a local catcher, or sanitize the copied database first. Test addresses with non-public domains such as `.local` are not sufficient protection because the production SMTP server will still attempt delivery and send bounce messages to the real sender mailbox.

After importing or refreshing a production database locally, clear or override production SMTP credentials, sender configuration, queued email deliveries, and any background jobs capable of sending mail before launching the application. Starting the local application must not cause any external email, bounce, or delivery-status notification.

## Performance and Optimization

Optimize every implementation as far as reasonably possible. Before completing a task, review database and API query counts, payload sizes, algorithmic complexity, repeated calculations, network requests, rendering work, memory use, and other relevant resource costs. Avoid N+1 queries, loading or processing data before it is needed, duplicate work, and unbounded full-list operations when a narrower indexed or demand-driven operation is available. Preserve correctness, readability, security, and maintainability while choosing the most efficient practical solution.

## Commits

Use short, meaningful Russian commit messages that describe the user-visible or technical result of the change. Avoid generic messages such as `fix`, `update`, `changes`, or English placeholders when a clear Russian summary is possible.

Do not rewrite commits that have already been pushed only to change their message, unless the user explicitly asks for history rewriting or force-push. If a pushed commit message is not ideal, keep history intact and use a clear Russian message for the next follow-up commit.

When work is performed on a secondary development branch and is later merged into the main branch, delete the merged development branch both locally and on the remote so that only the main branch remains. When the user asks to push work from a secondary branch, merge it into the main branch first if it can be merged safely, then push the main branch and delete the secondary branch locally and remotely, unless the current chat explicitly specifies another branch strategy.

## Deployment Verification

After pushing changes to a branch that triggers deployment, verify that GitHub Actions started, completed successfully, and deployed the expected revision without errors. If a workflow fails, a deployment does not start, or the deployed revision does not match the pushed commit, inspect the workflow logs and fix the problem before considering the deployment complete.

After a successful deployment, verify the live environment directly: check the public API health endpoint, the frontend entry point, the service worker when relevant, and the VPS service status/revision when VPS access is available. Do not rely only on a successful `git push` as proof that production is updated.

## Roadmaps

When the user asks to create or continue a roadmap, store it under `docs/` as a human-readable Markdown document with statuses, milestones, rationale, checks, and clear definition of done.

Only roadmap files located directly under `docs/` are active working roadmaps. Roadmaps under `docs/archive/roadmaps/` are historical context and must not be used as a source of current tasks, updated, reopened, or continued unless the user explicitly asks to work with a specific archived roadmap. When starting roadmap-driven work, inspect active roadmap files in `docs/` first and ignore archived roadmaps by default.

When a roadmap is large enough to contain several independent workstreams, split it into a short index roadmap plus separate active roadmap files for each block. Each block roadmap must have its own progress summary and can be completed and moved to `docs/archive/roadmaps/` independently.

Before implementing work that belongs to an existing active roadmap, open the relevant roadmap first, check the current status of every related item, and update the roadmap as the work progresses. Use the roadmap status markers consistently:

- `[ ]` not started.
- `[~]` in progress.
- `[x]` completed and verified.
- `[!]` blocked, with the blocking reason written next to the item.
- `[decision]` requires a user/business decision before implementation.
- `[acceptance]` requires manual acceptance on real data or environment.

Each active roadmap must include a visible progress summary: total items, completed items, blocked/decision/acceptance items, and completion percentage. Update these numbers whenever roadmap items change status, and mention the same percentage summary in the final response for roadmap work.

Do not mark roadmap items as complete until code, tests, encoding checks, and local database checks required by that item have passed. If a new risk or prerequisite is discovered while working, add it to the roadmap instead of keeping it only in chat.

When a roadmap is fully completed, accepted, or intentionally paused/closed by the user, move it from `docs/` to `docs/archive/roadmaps/` in the same change. After archiving, it is no longer an active source of work unless the user explicitly reopens it.

## UI Density

For internal working sections of the CRM, keep spacing compact and consistent. Prefer page paddings around `p-3 lg:p-4`, section gaps around `gap-3` / `space-y-3`, and card paddings around `p-3` unless a form, table, or modal clearly needs more room for readability.

Avoid large decorative whitespace in operational screens. Tables, filters, settings panels, and status cards should start close to the page header and use compact vertical rhythm so users can see more working data without extra scrolling. When updating an existing section, align new blocks to the compact spacing style instead of introducing larger `p-6`, `gap-6`, or `space-y-6` layouts by default.
