# Agent Instructions

## Release Notes

When implementing a new user-facing mechanism, changing business rules, permissions, workflows, notifications, or other behavior that users will notice, prepare a short human-readable update note.

The note must be written as text that can be published to users, not as an internal commit summary. It should explain what changed, why it matters, and what the user should do differently if needed.

Use this structure when it fits:

- `Новое` - new functionality or new screens.
- `Улучшено` - usability, speed, clarity, or workflow improvements.
- `Исправлено` - bugs and regressions fixed.
- `Важно` - behavior changes, permission changes, data rules, migrations, or anything that can affect existing work.

Keep the wording calm, specific, and useful. Avoid raw technical details unless they help the user understand the effect. Prefer examples like:

- `Добавлена возможность открыть окно "Что нового" из меню пользователя.`
- `Закрытые периоды больше не пересчитываются автоматически.`
- `Если сохранение отметки просмотра временно недоступно, окно не будет повторно открываться в текущей сессии.`

For this project, release notes that should be seeded by agents live in `backend/AppReleases/releases.json`. When a change should appear in the "Что нового" window, add or update a release entry there with a unique `releaseId`. The backend syncs this file into the database on startup, and admins can also add or edit releases manually from the application settings.

If the release note cannot be added directly, include the proposed text in the final response so it can be copied into the release notes later.

## Development Hygiene

Before adding a new notification, modal, permission mechanism, or settings storage, check whether the project already has an existing pattern for it and reuse that pattern where possible.

Do not change unrelated release notes, user-facing text, permissions, or configuration while implementing a feature.
