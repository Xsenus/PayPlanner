# Автоматическая инвентаризация поверхностей PayPlanner

Файл формируется командой `npm run audit:surface:update`. Ручное редактирование не допускается.
Для endpoint тестовая связь считается прямой, только если тестовый файл одновременно относится к контроллеру и вызывает конкретный метод либо проверяет его контракт через `nameof`. Для формы и фонового сервиса пока используется связь по имени компонента или класса; детальная полнота сценариев проверяется последующими этапами roadmap.

## Сводка

- Разделов frontend: 55.
- Backend-контроллеров: 97.
- HTTP endpoints: 963; с прямой тестовой связью: 675.
- HTTP endpoints с точной ссылкой из документации: 264; критичных без такой ссылки: 532.
- Критичных endpoints без прямой тестовой связи: 240.
- Реальных HTML-форм: 21; с найденной тестовой связью: 19.
- Фоновых сервисов: 35; с найденной тестовой связью: 28.
- Исходящих HTTP-интеграций: 12; с найденной тестовой связью: 12.

## Разделы frontend

| Раздел | Правило доступа | Тестовая связь | Документация |
| --- | --- | --- | --- |
| workspace | () => true | да | docs/full-project-audit-2026-06-10.md, README.md |
| notifications | () => true | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/claude-yougile-finance-development-spec-2026-10-10.md, docs/claude-yougile-ui-development-spec-2026-10-10.md, docs/deploy-runbook.md, docs/deployment-manual-2026-10-21.md, docs/development-pause-plan-2026-09-30.md, docs/full-project-audit-2026-06-10.md, docs/max-today-reply-drafts-2026-10-14.md, docs/payplanner-requests-roadmap-2026-10-14.md, docs/task-critical-events-matrix-2026-07-09.md, docs/task-critical-notifications-audit-2026-07-09.md |
| messenger | () => true | да | docs/claude-yougile-development-handoff-2026-10-10.md, docs/claude-yougile-operations-development-spec-2026-10-10.md, docs/development-release-preparation-2026-10-06.md, docs/payplanner-requests-roadmap-2026-10-14.md, docs/payplanner-roadmap-coverage-2026-10-14.md, docs/public-access-link-security.md, docs/storage-backup-snapshot-boundary-audit-2026-09-28.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/unpushed-changes-map-2026-10-03.md |
| profile | () => true | да | docs/company-bank-binding-checkpoint-2026-11-01.md, docs/company-settings-authority-checkpoint-2026-10-31.md, docs/development-pause-plan-2026-09-30.md, docs/development-profile-max-planning-final-gate-2026-10-23.md, docs/development-release-preparation-2026-10-06.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-esign-roadmap-2026-10-14.md, docs/payplanner-handoff-2026-10-21.md, docs/payplanner-roadmap-coverage-2026-10-14.md, docs/payplanner-visual-comparison-2026-10-23.md, docs/release-verification-history-2026-10-06.md, docs/storage-resilience-runbook.md, docs/storage-s3-backup-audit-2026-09-22.md |
| calendar | (permissions) => permissions.calendar.canView | да | docs/action-map-pay-20-task-cards-2026-10-12.md, docs/calendar-log-audit-2026-10-23.md, docs/calendar-log-cleanup-execution-2026-10-23.md, docs/calendar-log-cleanup-preparation-2026-10-23.md, docs/calendar-log-compaction-2026-10-23.md, docs/claude-yougile-development-handoff-2026-10-10.md, docs/date-time-inventory.md, docs/development-calendar-final-gate-2026-10-23.md, docs/development-calendar-log-guard-2026-10-23.md, docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/legal-feature-flag-ci-gate-2026-06-10.md, docs/owner-decisions-2026-10-12.md, docs/payplanner-court-roadmap-2026-10-14.md, docs/payplanner-handoff-2026-10-21.md, docs/payplanner-roadmap-coverage-2026-10-14.md, docs/yougile-drafts-continuation-2026-10-12.md, docs/yougile-payplanner-drafts-2026-10-12.md, docs/yougile-work-categories-2026-10-12.md |
| tasks | (permissions) => permissions.tasks.canView | да | docs/crm-legal-module-gap-report-2026-05-25.md, docs/deployment-manual-2026-10-22.md, docs/development-max-task-notifications-2026-10-23.md, docs/development-release-preparation-2026-10-06.md, docs/development-task-billing-bulk-frontend-2026-10-24.md, docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/release-verification-history-2026-10-06.md, docs/report-inventory-2026-08-09.md, docs/storage-resilience-runbook.md, docs/task-critical-events-matrix-2026-07-09.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-journal-query-optimization.md, docs/task-result-approval-current-state-2026-07-09.md, docs/task-template-current-state-2026-07-09.md |
| tasksUntriaged | (permissions) => permissions.tasks.canView | да | нет |
| tasksControl | (permissions) => permissions.tasks.canView | да | нет |
| tasksApprovals | (permissions) => permissions.tasks.canView | да | docs/full-project-audit-2026-06-10.md |
| tasksCalendar | (permissions) => permissions.tasks.canView | да | docs/full-project-audit-2026-06-10.md |
| notes | (permissions) => permissions.notes.canView | да | docs/deploy-runbook.md, docs/deployment-manual-2026-10-21.md, docs/development-pause-plan-2026-09-30.md |
| reports | (permissions) => permissions.reports.canView | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-court-roadmap-2026-10-14.md, docs/report-inventory-2026-08-09.md |
| calculator | (permissions) => permissions.calculator.canView | да | docs/full-project-audit-2026-06-10.md |
| legalEntities | (permissions) => permissions.legalEntities.canView | да | docs/full-project-audit-2026-06-10.md |
| cases | (permissions) => permissions.cases.canView | да | docs/case-access-query-optimization.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md, docs/case-registry-query-optimization.md, docs/crm-legal-module-acceptance-checklist.md, docs/full-project-audit-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/storage-resilience-runbook.md |
| courtCases | (permissions) => permissions.cases.canView | да | нет |
| caseDetail | (permissions) => permissions.cases.canView | да | docs/full-project-audit-2026-06-10.md |
| requests | (permissions) => permissions.requests?.canView === true | да | docs/claude-branch-review-2026-10-10.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-requests-roadmap-2026-10-14.md |
| clients | (permissions) => permissions.clients.canView | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/development-pause-plan-2026-09-30.md, docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/max-review-and-replies-2026-10-16.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-access-security-roadmap-2026-10-14.md |
| contacts | (permissions) => permissions.clients.canView \|\| permissions.legalEntities.canView | да | docs/contacts-directory-implementation-log.md, docs/full-project-audit-2026-06-10.md |
| contactsClients | (permissions) => permissions.clients.canView | да | docs/full-project-audit-2026-06-10.md |
| contactsLegal | (permissions) => permissions.legalEntities.canView | да | docs/full-project-audit-2026-06-10.md |
| contactsExternal | (permissions) => permissions.clients.canView | да | docs/full-project-audit-2026-06-10.md |
| clientDetail | (permissions) => permissions.clients.canView | да | docs/full-project-audit-2026-06-10.md |
| accounts | (permissions) => permissions.accounts.canView | да | docs/full-project-audit-2026-06-10.md, docs/money-invoice-allocation-boundary-checkpoint-2026-10-31.md, docs/payplanner-roadmap-coverage-2026-10-14.md |
| accountsIncome | (permissions) => permissions.accounts.canView | да | docs/full-project-audit-2026-06-10.md |
| accountsExpense | (permissions) => permissions.accounts.canView | да | docs/full-project-audit-2026-06-10.md |
| installments | (permissions) => permissions.accounts.canView | да | docs/full-project-audit-2026-06-10.md |
| acts | (permissions) => permissions.acts.canView | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/development-act-source-access-2026-10-24.md, docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md |
| actsIncome | (permissions) => permissions.acts.canView | да | docs/full-project-audit-2026-06-10.md |
| actsExpense | (permissions) => permissions.acts.canView | да | docs/full-project-audit-2026-06-10.md |
| actCreate | (permissions) => permissions.acts.canView && permissions.acts.canCreate | да | docs/full-project-audit-2026-06-10.md |
| actDetail | (permissions) => permissions.acts.canView | да | docs/full-project-audit-2026-06-10.md |
| payments | (permissions) => permissions.payments.canView | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/client-contract-invoice-act-payment-analysis.md, docs/final-acceptance-2026-06-10.md, docs/financial-cash-access-acceptance-report-2026-06-16.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-visual-comparison-2026-10-23.md |
| paymentsIncome | (permissions) => permissions.payments.canView | да | docs/full-project-audit-2026-06-10.md |
| paymentsExpense | (permissions) => permissions.payments.canView | да | docs/full-project-audit-2026-06-10.md |
| moneyBank | (permissions) => permissions.payments.canView | да | docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md |
| moneyCash | (permissions) => permissions.payments.canView | да | docs/full-project-audit-2026-06-10.md |
| moneyCards | (permissions) => permissions.payments.canView | да | docs/full-project-audit-2026-06-10.md |
| moneyEMoney | (permissions) => permissions.payments.canView | да | docs/full-project-audit-2026-06-10.md |
| moneyTotal | (permissions) => permissions.payments.canView | да | docs/full-project-audit-2026-06-10.md |
| payroll | (_permissions, options) => options.isAdminRole === true | да | нет |
| contracts | (permissions) => permissions.contracts.canView | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/development-performance-audit-2026-10-23.md, docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-esign-roadmap-2026-10-14.md, docs/payplanner-visual-comparison-2026-10-23.md |
| contractCreate | (permissions) => permissions.contracts.canView && permissions.contracts.canCreate | да | docs/full-project-audit-2026-06-10.md |
| contractDetail | (permissions) => permissions.contracts.canView | да | docs/full-project-audit-2026-06-10.md |
| companyProfile | (permissions, options) => options.isAdminRole === true \|\| permissions.companyProfile?.canView === true | да | docs/development-company-profile-permissions-2026-10-23.md, docs/full-project-audit-2026-06-10.md |
| users | (_permissions, options) => options.isAdminRole === true | да | docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md |
| userDetail | (_permissions, options) => options.isAdminRole === true | да | docs/full-project-audit-2026-06-10.md |
| userActivity | (_permissions, options) => options.isAdminRole === true | да | docs/full-project-audit-2026-06-10.md |
| roles | (_permissions, options) => options.canManageRoles === true | да | docs/full-project-audit-2026-06-10.md, docs/payplanner-roadmap-coverage-2026-10-14.md, docs/release-verification-history-2026-10-06.md |
| audit | (_permissions, options) => options.canViewAudit === true | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/crm-legal-module-release-runbook.md, docs/date-time-inventory.md, docs/deployment-ci-2026-10-25.md, docs/development-act-source-access-2026-10-24.md, docs/development-calendar-final-gate-2026-10-23.md, docs/development-pause-plan-2026-09-30.md, docs/development-performance-audit-2026-10-23.md, docs/development-profile-max-planning-final-gate-2026-10-23.md, docs/development-release-preparation-2026-10-06.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-external-approval-roadmap-2026-10-14.md, docs/payplanner-financial-documents-roadmap-2026-10-14.md, docs/payplanner-handoff-2026-10-25.md, docs/payplanner-messenger-roadmap-2026-10-14.md, docs/payplanner-roadmap-coverage-2026-10-14.md, docs/release-verification-history-2026-10-06.md, docs/unpushed-changes-map-2026-10-03.md |
| yandexDisk | (_permissions, options) => options.canViewYandexDisk === true | да | docs/full-project-audit-2026-06-10.md |
| s3Storage | (_permissions, options) => options.canViewS3Storage === true | да | docs/full-project-audit-2026-06-10.md |
| settings | (_permissions, options) => options.canViewSettings === true | да | docs/company-images-remaining-audit-2026-10-30.md, docs/development-main-integration-2026-10-26.md, docs/development-release-preparation-2026-10-06.md, docs/final-acceptance-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-calendar-roadmap-2026-10-14.md, docs/payplanner-handoff-2026-10-21.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/release-verification-history-2026-10-06.md |
| dictionaries | (permissions) => permissions.dictionaries.canView | да | docs/full-project-audit-2026-06-10.md |

## Формы

| Компонент | Файл | Строка | Тест найден | Документация |
| --- | --- | --- | --- | --- |
| InvoiceModal | src/components/Accounts/InvoiceModal.tsx | 4478 | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/company-automatic-read-ownership-checkpoint-2026-10-31.md, docs/company-images-remaining-audit-2026-10-30.md, docs/company-related-read-ownership-checkpoint-2026-10-31.md, docs/company-related-write-ownership-checkpoint-2026-10-31.md, docs/date-time-inventory.md, docs/development-performance-audit-2026-10-23.md, docs/development-test-acceleration-2026-10-21.md, docs/final-acceptance-2026-06-10.md, docs/financial-invoice-cancellation-current-state-2026-07-08.md, docs/full-project-audit-2026-06-10.md, docs/invoice-case-ownership-checkpoint-2026-10-31.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-financial-documents-roadmap-2026-10-14.md, docs/payplanner-work-order-2026-10-21.md |
| ActModal | src/components/Acts/ActModal.tsx | 1632 | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/company-related-write-ownership-checkpoint-2026-10-31.md, docs/date-time-inventory.md, docs/full-project-audit-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md |
| Login | src/components/Auth/Login.tsx | 73 | да | docs/external-authentication-security-and-performance.md, docs/field-encryption-candidates.generated.md, docs/field-encryption-data-inventory-2026-07-30.md, docs/full-project-audit-2026-06-10.md |
| Register | src/components/Auth/Register.tsx | 94 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/company-finance-ownership-checkpoint-2026-10-31.md, docs/date-time-inventory.md, docs/field-encryption-candidates.generated.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-tax-roadmap-2026-10-14.md, docs/storage-resilience-runbook.md, docs/storage-s3-backup-audit-2026-09-22.md |
| PaymentModal | src/components/Calendar/PaymentModal.tsx | 3295 | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/advance-allocation-recovery-checkpoint-2026-10-31.md, docs/balance-application-durable-flow-checkpoint-2026-10-31.md, docs/balance-application-recovery-ui-checkpoint-2026-10-31.md, docs/company-automatic-read-ownership-checkpoint-2026-10-31.md, docs/company-payment-write-ownership-checkpoint-2026-10-31.md, docs/company-related-read-ownership-checkpoint-2026-10-31.md, docs/company-related-write-ownership-checkpoint-2026-10-31.md, docs/date-time-inventory.md, docs/full-project-audit-2026-06-10.md, docs/max-latest-reply-drafts-2026-10-15.md, docs/max-review-and-replies-2026-10-16.md, docs/payment-balance-ownership-checkpoint-2026-10-31.md, docs/payplanner-financial-documents-roadmap-2026-10-14.md, docs/technical-stabilization-log.md |
| ClientDetail | src/components/Clients/ClientDetail.tsx | 6489 | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/advance-allocation-recovery-checkpoint-2026-10-31.md, docs/advance-review-batch-checkpoint-2026-10-31.md, docs/advance-review-cursor-checkpoint-2026-10-31.md, docs/advance-review-monitor-checkpoint-2026-10-31.md, docs/balance-application-durable-flow-checkpoint-2026-10-31.md, docs/balance-application-idempotency-checkpoint-2026-10-31.md, docs/balance-application-recovery-ui-checkpoint-2026-10-31.md, docs/claude-handoff-for-codex-review-2026-10-12.md, docs/company-branches-ui-checkpoint-2026-10-30.md, docs/company-settings-authority-checkpoint-2026-10-31.md, docs/compensated-payment-writers-checkpoint-2026-10-31.md, docs/development-client-detail-visual-2026-10-24.md, docs/development-performance-audit-2026-10-23.md, docs/development-roadmap-release-integration-2026-10-28.md, docs/full-project-audit-2026-06-10.md, docs/invoice-cancellation-credit-checkpoint-2026-10-31.md, docs/invoice-cancellation-history-checkpoint-2026-10-31.md, docs/invoice-reconciliation-boundary-checkpoint-2026-10-31.md, docs/invoice-remainder-writers-checkpoint-2026-10-31.md, docs/money-invoice-allocation-boundary-checkpoint-2026-10-31.md, docs/payment-advance-history-checkpoint-2026-10-31.md, docs/payment-balance-ownership-checkpoint-2026-10-31.md, docs/payment-create-financial-boundary-checkpoint-2026-10-31.md, docs/payment-overdue-delivery-checkpoint-2026-10-31.md, docs/payment-overdue-writer-checkpoint-2026-10-31.md, docs/payment-writer-balance-boundary-checkpoint-2026-10-31.md, docs/payplanner-access-security-roadmap-2026-10-14.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-visual-comparison-2026-10-23.md, docs/payplanner-work-order-2026-10-21.md, docs/remainder-financial-identity-checkpoint-2026-10-31.md, docs/service-balance-credit-guard-checkpoint-2026-10-31.md, docs/unused-frontend-candidates-2026-06-10.md, docs/versioned-payment-structure-checkpoint-2026-10-31.md, docs/versioned-payment-writers-checkpoint-2026-10-31.md |
| ClientModal | src/components/Clients/ClientModal.tsx | 240 | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/claude-yougile-ui-development-spec-2026-10-10.md |
| ContractCreatePage | src/components/Contracts/ContractCreatePage.tsx | 297 | нет | docs/full-project-audit-2026-06-10.md, docs/unused-frontend-candidates-2026-06-10.md |
| ContractModal | src/components/Contracts/ContractModal.tsx | 370 | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/full-project-audit-2026-06-10.md |
| DocumentImagePrintDialog | src/components/Documents/DocumentImagePrintDialog.tsx | 93 | да | docs/payplanner-company-roadmap-2026-10-14.md |
| LegalEntityModal | src/components/LegalEntities/LegalEntityModal.tsx | 4073 | да | docs/action-map-pay-32-individual-client-2026-10-12.md, docs/company-autofill-ownership-checkpoint-2026-11-01.md, docs/company-automatic-read-ownership-checkpoint-2026-10-31.md, docs/company-branch-version-checkpoint-2026-10-31.md, docs/company-branches-ui-checkpoint-2026-10-30.md, docs/company-deletion-atomicity-checkpoint-2026-10-30.md, docs/company-finance-ownership-checkpoint-2026-10-31.md, docs/company-images-remaining-audit-2026-10-30.md, docs/company-payment-write-ownership-checkpoint-2026-10-31.md, docs/company-profile-concurrency-checkpoint-2026-10-30.md, docs/company-related-read-ownership-checkpoint-2026-10-31.md, docs/company-related-write-ownership-checkpoint-2026-10-31.md, docs/development-profile-max-planning-final-gate-2026-10-23.md, docs/full-project-audit-2026-06-10.md, docs/invoice-case-ownership-checkpoint-2026-10-31.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-work-order-2026-10-21.md |
| MoneyOperations | src/components/Money/MoneyOperations.tsx | 6435 | да | docs/bank-integrations-api-verification-2026-10-09.md, docs/batch-operations-audit-coverage-2026-06-10.md, docs/date-time-inventory.md, docs/deployment-manual-2026-10-22.md, docs/development-bank-import-verification-2026-10-16.md, docs/development-client-detail-visual-2026-10-24.md, docs/development-performance-audit-2026-10-23.md, docs/development-performance-implementation-2026-10-23.md, docs/development-roadmap-release-integration-2026-10-28.md, docs/development-test-speed-drafts-2026-10-24.md, docs/financial-cash-access-acceptance-report-2026-06-16.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/max-finance-provenance-review-2026-10-15.md, docs/max-latest-reply-drafts-2026-10-15.md, docs/max-review-and-replies-2026-10-16.md, docs/max-today-reply-drafts-2026-10-14.md, docs/payplanner-bank-import-guide-2026-10-24.md, docs/payplanner-bank-roadmap-2026-10-14.md |
| ClientPortalPage | src/components/Public/ClientPortalPage.tsx | 223 | да | нет |
| RoleModal | src/components/Roles/RoleModal.tsx | 121 | нет | docs/full-project-audit-2026-06-10.md |
| S3Storage | src/components/S3Storage/S3Storage.tsx | 773 | да | docs/date-time-inventory.md, docs/development-pause-plan-2026-09-30.md, docs/file-upload-limits-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/http-integration-error-coverage-2026-06-10.md, docs/s3-storage-acceptance-checklist.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/unpushed-changes-map-2026-10-03.md |
| TaskJournalSettings | src/components/Settings/TaskJournalSettings.tsx | 998 | да | docs/action-map-pay-20-task-cards-2026-10-12.md, docs/payplanner-task-billing-bulk-plan-2026-10-23.md, docs/payplanner-visual-comparison-2026-10-23.md, docs/task-template-current-state-2026-07-09.md |
| TaskJournal | src/components/Tasks/TaskJournal.tsx | 11034 | да | docs/action-map-pay-19-journal-2026-10-12.md, docs/action-map-pay-20-task-cards-2026-10-12.md, docs/action-map-pay-32-individual-client-2026-10-12.md, docs/backend-build-speed-2026-10-11.md, docs/batch-operations-audit-coverage-2026-06-10.md, docs/calendar-log-cleanup-execution-2026-10-23.md, docs/calendar-log-compaction-2026-10-23.md, docs/claude-branch-review-2026-10-10.md, docs/claude-handoff-for-codex-review-2026-10-12.md, docs/claude-next-development-prompt-2026-10-10.md, docs/claude-yougile-ui-development-spec-2026-10-10.md, docs/codex-preparation-report-2026-10-12.md, docs/codex-preparation-request-2026-10-12.md, docs/date-time-inventory.md, docs/development-main-integration-2026-10-26.md, docs/development-msg02-edit-draft-2026-10-26.md, docs/development-msg05-user-snoozes-2026-10-26.md, docs/development-pause-plan-2026-09-30.md, docs/development-performance-audit-2026-10-23.md, docs/development-performance-implementation-2026-10-23.md, docs/development-release-preparation-2026-10-06.md, docs/development-roadmap-merge-2026-10-27.md, docs/ef-production-warning-cleanup-plan-2026-06-22.md, docs/field-encryption-candidates.generated.md, docs/field-encryption-data-inventory-2026-07-30.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/max-latest-reply-drafts-2026-10-15.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-handoff-2026-10-25.md, docs/payplanner-payroll-roadmap-2026-10-14.md, docs/payplanner-task-billing-bulk-plan-2026-10-23.md, docs/payplanner-ui-roadmap-2026-10-14.md, docs/payplanner-visual-comparison-2026-10-23.md, docs/release-verification-history-2026-10-06.md, docs/storage-backup-snapshot-boundary-audit-2026-09-28.md, docs/storage-resilience-runbook.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/task-critical-events-matrix-2026-07-09.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-participants-current-state-2026-07-09.md, docs/task-result-approval-current-state-2026-07-09.md, docs/task-template-current-state-2026-07-09.md, docs/unpushed-changes-map-2026-10-03.md, docs/yougile-clarifications-drafts-2026-10-09.md |
| FormPromptModal | src/components/ui/FormPromptModal.tsx | 65 | да | нет |
| TextPromptModal | src/components/ui/TextPromptModal.tsx | 55 | да | docs/full-project-audit-2026-06-10.md |
| YandexDisk | src/components/YandexDisk/YandexDisk.tsx | 722 | да | docs/action-map-pay-19-journal-2026-10-12.md, docs/action-map-pay-20-task-cards-2026-10-12.md, docs/development-pause-plan-2026-09-30.md, docs/field-encryption-candidates.generated.md, docs/file-upload-limits-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/http-integration-error-coverage-2026-06-10.md, docs/release-verification-history-2026-10-06.md, docs/storage-backup-snapshot-boundary-audit-2026-09-28.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/unpushed-changes-map-2026-10-03.md, docs/yandex-disk-integration-delivery-report.md |
| DialogContext | src/contexts/DialogContext.tsx | 265 | да | docs/unpushed-changes-map-2026-10-03.md |

### Формы без тестовой связи

| Компонент | Файл:строка | Документация |
| --- | --- | --- |
| ContractCreatePage | src/components/Contracts/ContractCreatePage.tsx:297 | docs/full-project-audit-2026-06-10.md, docs/unused-frontend-candidates-2026-06-10.md |
| RoleModal | src/components/Roles/RoleModal.tsx:121 | docs/full-project-audit-2026-06-10.md |

## Фоновые сервисы

| Сервис | Файл | Строка | Тест найден | Документация |
| --- | --- | --- | --- | --- |
| CalendarSyncLogRetentionService | backend/Services/CalendarSyncLogRetentionService.cs | 6 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/calendar-log-audit-2026-10-23.md, docs/date-time-inventory.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| CalendarSyncSchedulerService | backend/Services/CalendarSyncSchedulerService.cs | 10 | да | docs/date-time-inventory.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| CaseProceduralDeadlineReminderNotificationService | backend/Services/CaseProceduralDeadlineReminderNotificationService.cs | 8 | да | docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| ClientBirthdayTaskSchedulerService | backend/Services/ClientBirthdayTaskSchedulerService.cs | 9 | да | docs/claude-yougile-operations-development-spec-2026-10-10.md, docs/codex-preparation-report-2026-10-12.md, docs/dev-environment-claude-2026-10-12.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md |
| ContractExpirationNotificationService | backend/Services/ContractExpirationNotificationService.cs | 8 | да | нет |
| DatabaseBackupSchedulerService | backend/Services/DatabaseBackupSchedulerService.cs | 3 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/storage-s3-backup-audit-2026-09-22.md |
| ExternalCalendarTaskSyncOutboxWorker | backend/Services/ExternalCalendarTaskSyncOutboxService.cs | 194 | нет | нет |
| ExternalWorkOrderApprovalExpirationService | backend/Services/ExternalWorkOrderApprovalNotifications.cs | 116 | да | нет |
| GoogleCalendarBackgroundSyncService | backend/Services/GoogleCalendarBackgroundSyncService.cs | 11 | да | docs/date-time-inventory.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| InvoicePaymentStateReconciliationService | backend/Services/InvoicePaymentStateReconciliationService.cs | 9 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/invoice-payment-status-automation-note.md, docs/invoice-reconciliation-boundary-checkpoint-2026-10-31.md |
| ClientCaseCourtSyncSchedulerService | backend/Services/LegalIntegrations/ClientCaseCourtSyncSchedulerService.cs | 17 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/unpushed-changes-map-2026-10-03.md |
| CourtSyncMetricsRollupService | backend/Services/LegalIntegrations/CourtSyncMetricsRollupService.cs | 8 | да | docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| MessengerDocumentOutboxWorker | backend/Services/MessengerDocumentOutboxService.cs | 288 | нет | нет |
| MoneyBalanceReconciliationSchedulerService | backend/Services/MoneyBalanceReconciliationSchedulerService.cs | 6 | да | docs/date-time-inventory.md, docs/development-bank-import-verification-2026-10-16.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-handoff-2026-10-21.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/payplanner-work-order-2026-10-21.md |
| CaseDocumentNotificationDispatchWorker | backend/Services/Notifications/CaseDocumentNotificationDispatchWorker.cs | 3 | нет | docs/unpushed-changes-map-2026-10-03.md |
| InternalChatNoReplyReminderService | backend/Services/Notifications/InternalChatNoReplyReminderService.cs | 7 | да | docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| InternalChatNotificationRecoveryService | backend/Services/Notifications/InternalChatNotificationRecoveryService.cs | 8 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/unpushed-changes-map-2026-10-03.md |
| MaxOutboxWorker | backend/Services/Notifications/MaxOutboxService.cs | 642 | нет | нет |
| NotificationDeliveryRetentionService | backend/Services/Notifications/NotificationDeliveryRetentionService.cs | 6 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| NotificationRetrySchedulerService | backend/Services/Notifications/NotificationRetrySchedulerService.cs | 8 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| SmsChannelMonitoringService | backend/Services/Notifications/SmsChannelMonitoringService.cs | 122 | да | нет |
| SmsDeliveryStatusReconciliationService | backend/Services/Notifications/SmsDeliveryStatusReconciliationService.cs | 12 | да | нет |
| StickyNoteReminderNotificationService | backend/Services/Notifications/StickyNoteReminderNotificationService.cs | 7 | да | нет |
| TaskDeadlineReviewerDispatchWorker | backend/Services/Notifications/TaskDeadlineReviewerDispatchProcessor.cs | 88 | нет | нет |
| TaskJournalReminderNotificationService | backend/Services/Notifications/TaskJournalReminderNotificationService.cs | 9 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| TaskStatusNotificationDispatchWorker | backend/Services/Notifications/TaskStatusNotificationDispatchProcessor.cs | 113 | нет | нет |
| TelegramBotPollingService | backend/Services/Notifications/TelegramBotPollingService.cs | 3 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| PaymentAdvanceReviewMonitoringService | backend/Services/PaymentAdvanceReviewMonitoringService.cs | 11 | да | нет |
| PaymentStatusUpdater | backend/Services/PaymentStatusUpdater.cs | 15 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/payment-overdue-delivery-checkpoint-2026-10-31.md, docs/payment-overdue-outbox-checkpoint-2026-10-31.md, docs/payment-overdue-writer-checkpoint-2026-10-31.md |
| SalesRequestAutomationService | backend/Services/Requests/SalesRequestAutomationService.cs | 9 | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| StorageOperationWorker | backend/Services/Storage/StorageOperationProcessor.cs | 886 | да | docs/development-pause-plan-2026-09-30.md, docs/storage-resilience-runbook.md |
| TaskJournalAutomationSchedulerService | backend/Services/TaskJournalAutomationSchedulerService.cs | 10 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| UserActivityRetentionService | backend/Services/UserActivity/UserActivityRetentionService.cs | 7 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| YandexCalendarBackgroundSyncService | backend/Services/YandexCalendarBackgroundSyncService.cs | 11 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |
| YandexDiskCrmSyncSchedulerService | backend/Services/YandexDiskCrmSyncSchedulerService.cs | 12 | да | docs/full-project-audit-2026-06-10.md, docs/hosted-service-lifecycle-coverage-2026-06-10.md |

### Фоновые сервисы без тестовой связи

| Сервис | Файл:строка | Документация |
| --- | --- | --- |
| ExternalCalendarTaskSyncOutboxWorker | backend/Services/ExternalCalendarTaskSyncOutboxService.cs:194 | нет |
| MessengerDocumentOutboxWorker | backend/Services/MessengerDocumentOutboxService.cs:288 | нет |
| CaseDocumentNotificationDispatchWorker | backend/Services/Notifications/CaseDocumentNotificationDispatchWorker.cs:3 | docs/unpushed-changes-map-2026-10-03.md |
| MaxOutboxWorker | backend/Services/Notifications/MaxOutboxService.cs:642 | нет |
| TaskDeadlineReviewerDispatchWorker | backend/Services/Notifications/TaskDeadlineReviewerDispatchProcessor.cs:88 | нет |
| TaskStatusNotificationDispatchWorker | backend/Services/Notifications/TaskStatusNotificationDispatchProcessor.cs:113 | нет |
| SalesRequestAutomationService | backend/Services/Requests/SalesRequestAutomationService.cs:9 | docs/pay-33-36-implementation-log-2026-10-09.md |

## Контроллеры

| Контроллер | Файл | Endpoints | Правило доступа | Тест найден |
| --- | --- | --- | --- | --- |
| AccountsController | backend/Controllers/AccountsController.cs | 3 | [Authorize] | да |
| ActsController | backend/Controllers/ActsController.cs | 23 | [Authorize] | да |
| AdminUsersController | backend/Controllers/AdminUsersController.cs | 8 | [Authorize(Policy = "Admin")] | да |
| AppearanceSettingsController | backend/Controllers/AppearanceSettingsController.cs | 2 | [Authorize] | да |
| AppVersionController | backend/Controllers/AppVersionController.cs | 8 | [Authorize] | да |
| AuthController | backend/Controllers/AuthController.cs | 5 | не задано | да |
| AuthProvidersAdminController | backend/Controllers/AuthProvidersAdminController.cs | 8 | [Authorize(Policy = "Admin")] | да |
| BankConnectionsController | backend/Controllers/BankConnectionsController.cs | 10 | [Authorize(Policy = "Admin")] | да |
| BanksController | backend/Controllers/BanksController.cs | 5 | [Authorize] | да |
| CalendarSyncController | backend/Controllers/CalendarSyncController.cs | 5 | [Authorize] | да |
| CasesController | backend/Controllers/CasesController.cs | 5 | [Authorize] | да |
| CashOrdersController | backend/Controllers/CashOrdersController.cs | 21 | [Authorize] | да |
| ClientBalancesController | backend/Controllers/ClientBalancesController.cs | 6 | не задано | да |
| ClientPassportController | backend/Controllers/ClientPassportController.cs | 2 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| ClientPortalAdminController | backend/Controllers/ClientPortalAdminController.cs | 7 | [Authorize(Policy = "Admin")] | нет |
| ClientPortalController | backend/Controllers/ClientPortalController.cs | 10 | публичный ([AllowAnonymous]) | да |
| ClientReferenceDataController | backend/Controllers/ClientReferenceDataController.cs | 3 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| ClientsController | backend/Controllers/ClientsController.cs | 12 | [Authorize] | да |
| CompanyBranchesController | backend/Controllers/CompanyBranchesController.cs | 4 | [Authorize] | да |
| ContactsController | backend/Controllers/ContactsController.cs | 9 | [Authorize] | да |
| ContactsController | backend/Controllers/ContactsController.Merge.cs | 2 | не задано | да |
| ContractsController | backend/Controllers/ContractsController.cs | 29 | [Authorize] | да |
| DadataIntegrationSettingsController | backend/Controllers/DadataIntegrationSettingsController.cs | 2 | [Authorize] | да |
| DatabaseAdminController | backend/Controllers/DatabaseAdminController.cs | 21 | [Authorize(Policy = "Admin")] | да |
| DictionariesAdminController | backend/Controllers/DictionariesAdminController.cs | 74 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | да |
| DictionariesReadController | backend/Controllers/DictionariesReadController.cs | 18 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| ElectronicSignaturesController | backend/Controllers/ElectronicSignaturesController.cs | 6 | [Authorize(Policy = "Admin")] | да |
| ExternalAuthController | backend/Controllers/ExternalAuthController.cs | 8 | не задано | да |
| FeatureFlagsController | backend/Controllers/FeatureFlagsController.cs | 2 | не задано | да |
| FormRequirementsController | backend/Controllers/FormRequirementsController.cs | 3 | [Authorize] | да |
| GoogleCalendarController | backend/Controllers/GoogleCalendarController.cs | 8 | не задано | да |
| HealthController | backend/Controllers/HealthController.cs | 1 | не задано | да |
| InstallmentsController | backend/Controllers/InstallmentsController.cs | 1 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Accounts)] | нет |
| InternalChatController | backend/Controllers/InternalChatController.cs | 19 | [Authorize] | да |
| InternalChatController | backend/Controllers/InternalChatHistoryController.cs | 2 | не задано | да |
| InternalChatController | backend/Controllers/InternalChatManagedSendController.cs | 0 | не задано | да |
| InternalChatController | backend/Controllers/InternalChatSendController.cs | 3 | не задано | да |
| InvoicesController | backend/Controllers/InvoicesController.cs | 38 | [Authorize] | да |
| LegacyStorageMigrationController | backend/Controllers/LegacyStorageMigrationController.cs | 1 | [Authorize(Policy = "Admin")] | нет |
| LegalEntitiesController | backend/Controllers/LegalEntitiesController.cs | 18 | [Authorize] | да |
| LegalEntityBalancesController | backend/Controllers/LegalEntityBalancesController.cs | 6 | не задано | да |
| MaxMessengerController | backend/Controllers/MaxMessengerController.cs | 8 | [Authorize] | да |
| MaxWebhookController | backend/Controllers/MaxMessengerController.cs | 1 | публичный ([AllowAnonymous]) | да |
| MessengerDocumentDeliveriesController | backend/Controllers/MessengerDocumentDeliveriesController.cs | 7 | [Authorize(Policy = "Admin")] | да |
| PublicMessengerDocumentDeliveriesController | backend/Controllers/MessengerDocumentDeliveriesController.cs | 3 | публичный ([AllowAnonymous]) | да |
| MoneyAccountsController | backend/Controllers/MoneyAccountsController.cs | 5 | [Authorize] | да |
| MoneyOperationsController | backend/Controllers/MoneyOperationsController.CompanyAccess.cs | 0 | не задано | да |
| MoneyOperationsController | backend/Controllers/MoneyOperationsController.cs | 27 | [Authorize] | да |
| MoneyOperationsController | backend/Controllers/MoneyOperationsController.DocumentAccess.cs | 0 | не задано | да |
| MoneyOperationsController | backend/Controllers/MoneyOperationsController.InvoiceAccess.cs | 0 | не задано | да |
| MoneyOperationsController | backend/Controllers/MoneyOperationsController.InvoiceCandidateCursor.cs | 0 | не задано | да |
| MoneyOperationsController | backend/Controllers/MoneyOperationsController.InvoiceMutation.cs | 0 | не задано | да |
| NavigationSettingsController | backend/Controllers/NavigationSettingsController.cs | 2 | [Authorize] | да |
| NotificationInboxController | backend/Controllers/NotificationInboxController.cs | 13 | [Authorize] | да |
| NotificationsController | backend/Controllers/NotificationsController.cs | 24 | [Authorize] | да |
| ObjectAuditController | backend/Controllers/ObjectAuditController.cs | 4 | [Authorize] | да |
| OwnCompaniesController | backend/Controllers/OwnCompaniesController.cs | 2 | [Authorize] | да |
| OwnCompaniesController | backend/Controllers/OwnCompaniesController.ReconciliationParty.cs | 1 | не задано | да |
| PaymentsController | backend/Controllers/PaymentsController.cs | 24 | [Authorize] | да |
| PowerOfAttorneyController | backend/Controllers/PowerOfAttorneyController.cs | 4 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| ProfileController | backend/Controllers/ProfileController.cs | 6 | [Authorize] | да |
| PublicWorkOrderApprovalController | backend/Controllers/PublicWorkOrderApprovalController.cs | 4 | публичный ([AllowAnonymous]) | да |
| PushSubscriptionsController | backend/Controllers/PushSubscriptionsController.cs | 6 | [Authorize] | да |
| ReportsController | backend/Controllers/ReportsController.cs | 40 | [Authorize] | да |
| RolePermissionsController | backend/Controllers/RolePermissionsController.cs | 6 | [Authorize] | да |
| RolesController | backend/Controllers/RolesController.cs | 7 | [Authorize] | да |
| S3StorageController | backend/Controllers/S3StorageController.cs | 13 | [Authorize] | да |
| SalesRequestsController | backend/Controllers/SalesRequestsController.cs | 17 | [Authorize] | да |
| StatsController | backend/Controllers/StatsController.cs | 1 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | да |
| StickyNotesController | backend/Controllers/StickyNotesController.cs | 14 | [Authorize] | да |
| StorageAdminController | backend/Controllers/StorageAdminController.cs | 13 | [Authorize(Policy = "Admin")] | да |
| SystemJobsController | backend/Controllers/SystemJobsController.cs | 2 | [Authorize(Policy = "Admin")] | да |
| TasksController | backend/Controllers/TasksController.cs | 47 | [Authorize] | да |
| TaskTimeController | backend/Controllers/TaskTimeController.cs | 6 | [Authorize] | да |
| TaxRegisterController | backend/Controllers/TaxRegisterController.cs | 12 | [Authorize(Policy = "Admin")] | нет |
| UserActivityController | backend/Controllers/UserActivityController.cs | 10 | [Authorize] | да |
| UsersController | backend/Controllers/UsersController.cs | 11 | [Authorize(Policy = "Admin")] | да |
| UserSettingsController | backend/Controllers/UserSettingsController.cs | 4 | [Authorize] | да |
| CasesV1Controller | backend/Controllers/V1/CasesV1Controller.cs | 5 | [Authorize] | да |
| ClientsV1Controller | backend/Controllers/V1/ClientsV1Controller.cs | 5 | [Authorize] | да |
| PaymentsV1Controller | backend/Controllers/V1/PaymentsV1Controller.cs | 5 | [Authorize] | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.Appeal.cs | 4 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.CourtRecoveries.cs | 3 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.cs | 115 | [Authorize] | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.FinancialCapture.cs | 0 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.HearingOutcome.cs | 1 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.HearingPreparation.cs | 2 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.LegacyFinancialAdmission.cs | 0 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.ManagedFinancial.cs | 0 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.NextHearingConfirmation.cs | 1 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.PositionReview.cs | 1 | не задано | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.PositionTemplates.cs | 4 | не задано | да |
| ClientsV2Controller | backend/Controllers/V2/ClientsV2Controller.cs | 5 | [Authorize] | да |
| PaymentsV2Controller | backend/Controllers/V2/PaymentsV2Controller.cs | 7 | [Authorize] | да |
| StatsV2Controller | backend/Controllers/V2/StatsV2Controller.cs | 2 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | да |
| YandexCalendarController | backend/Controllers/YandexCalendarController.cs | 7 | не задано | да |
| YandexDiskController | backend/Controllers/YandexDiskController.cs | 34 | [Authorize] | да |

## Исходящие HTTP-интеграции

| Клиент или сервис | Регистрация | Файл:строка | Тест найден | Документация |
| --- | --- | --- | --- | --- |
| DadataLegalEntityEnrichmentService | AddHttpClient<ILegalEntityEnrichmentService, DadataLegalEntityEnrichmentService> | backend/Program.cs:300 | да | docs/full-project-audit-2026-06-10.md, docs/http-integration-error-coverage-2026-06-10.md, docs/technical-stabilization-log.md |
| ExternalAuthService | IHttpClientFactory | backend/Services/ExternalAuthService.cs:29 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/full-project-audit-2026-06-10.md |
| GoogleCalendarSyncService | IHttpClientFactory | backend/Services/GoogleCalendarSyncService.cs:22 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/calendar-log-cleanup-preparation-2026-10-23.md, docs/date-time-inventory.md, docs/http-integration-error-coverage-2026-06-10.md |
| MaxApiClient | AddHttpClient<IMaxApiClient, MaxApiClient> | backend/Program.cs:246 | да | нет |
| SmsAeroProvider | AddHttpClient<SmsAeroProvider> | backend/Program.cs:238 | да | нет |
| SmscRuProvider | AddHttpClient<SmscRuProvider> | backend/Program.cs:233 | да | нет |
| SmsRuProvider | AddHttpClient<SmsRuProvider> | backend/Program.cs:228 | да | нет |
| SudrfCourtSyncService | AddHttpClient<ISudrfCourtSyncService, SudrfCourtSyncService> | backend/Program.cs:301 | да | docs/crm-legal-module-gap-report-2026-05-25.md, docs/development-pause-plan-2026-09-30.md, docs/full-project-audit-2026-06-10.md, docs/http-integration-error-coverage-2026-06-10.md |
| TelegramBotUpdateService | IHttpClientFactory | backend/Services/Notifications/TelegramBotUpdateService.cs:13 | да | docs/hosted-service-lifecycle-coverage-2026-06-10.md, docs/unpushed-changes-map-2026-10-03.md |
| TelegramNotificationService | IHttpClientFactory | backend/Services/Notifications/TelegramNotificationService.cs:8 | да | docs/advance-review-monitor-checkpoint-2026-10-31.md, docs/http-integration-error-coverage-2026-06-10.md, docs/unpushed-changes-map-2026-10-03.md |
| YandexCalendarSyncService | IHttpClientFactory | backend/Services/YandexCalendarSyncService.cs:21 | да | docs/batch-operations-audit-coverage-2026-06-10.md, docs/calendar-log-cleanup-preparation-2026-10-23.md, docs/date-time-inventory.md, docs/http-integration-error-coverage-2026-06-10.md |
| YandexDiskService | AddHttpClient<IYandexDiskService, YandexDiskService> | backend/Program.cs:302 | да | docs/development-pause-plan-2026-09-30.md, docs/full-project-audit-2026-06-10.md, docs/http-integration-error-coverage-2026-06-10.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/unpushed-changes-map-2026-10-03.md |

## Критичные endpoints без прямой тестовой связи

В список попадают публичные маршруты, операции изменения данных и маршруты с авторизацией, пользователями, правами, настройками, резервными копиями, финансами, документами, файлами, импортом, экспортом или электронной подписью.

| Метод | Маршрут | Контроллер.действие | Причина приоритета | Файл:строка |
| --- | --- | --- | --- | --- |
| POST | api/portal/documents/{documentPublicId:guid}/download | ClientPortalController.DownloadDocument | публичный доступ, изменение данных, чувствительные данные или операция | backend/Controllers/ClientPortalController.cs:132 |
| PUT | api/auth/admin/providers | AuthProvidersAdminController.UpdateSettings | изменение данных, чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:41 |
| PUT | api/auth/admin/providers/google | AuthProvidersAdminController.UpdateGoogleSettings | изменение данных, чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:56 |
| PUT | api/auth/admin/providers/telegram/bot | AuthProvidersAdminController.UpdateTelegramBotSettings | изменение данных, чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:101 |
| POST | api/auth/admin/providers/telegram/sync | AuthProvidersAdminController.SyncTelegram | изменение данных, чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:131 |
| PUT | api/auth/admin/providers/telegram/widget | AuthProvidersAdminController.UpdateTelegramWidgetSettings | изменение данных, чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:116 |
| PUT | api/auth/admin/providers/vk | AuthProvidersAdminController.UpdateVkSettings | изменение данных, чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:71 |
| PUT | api/auth/admin/providers/yandex | AuthProvidersAdminController.UpdateYandexSettings | изменение данных, чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:86 |
| POST | api/auth/admin/users | AdminUsersController.CreateUser | изменение данных, чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:49 |
| PUT | api/auth/admin/users/{id:int} | AdminUsersController.UpdateUser | изменение данных, чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:73 |
| DELETE | api/auth/admin/users/{id:int} | AdminUsersController.DeleteUser | изменение данных, чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:116 |
| POST | api/auth/admin/users/{id:int}/approve | AdminUsersController.ApproveUser | изменение данных, чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:146 |
| POST | api/auth/admin/users/{id:int}/reject | AdminUsersController.RejectUser | изменение данных, чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:159 |
| POST | api/bank-connections/{id:int}/profile-account-bindings | BankConnectionsController.BindProfileAccount | изменение данных, чувствительные данные или операция | backend/Controllers/BankConnectionsController.cs:175 |
| POST | api/bank-connections/{id:int}/profile-account-bindings/{bindingId:int}/revoke | BankConnectionsController.RevokeProfileAccount | изменение данных, чувствительные данные или операция | backend/Controllers/BankConnectionsController.cs:181 |
| POST | api/contacts/imports/yealink | ContactsController.ImportYealinkXml | изменение данных, чувствительные данные или операция | backend/Controllers/ContactsController.cs:810 |
| POST | api/database/backups | DatabaseAdminController.CreateBackupAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:189 |
| DELETE | api/database/backups/all | DatabaseAdminController.DeleteAllBackupsAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:273 |
| POST | api/database/backups/catalog/import | DatabaseAdminController.ImportBackupCatalogAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:167 |
| POST | api/database/backups/discovery | DatabaseAdminController.DiscoverBackupsAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:181 |
| DELETE | api/database/backups/file | DatabaseAdminController.DeleteBackupAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:251 |
| POST | api/database/backups/restore-drill | DatabaseAdminController.RunRestoreDrillAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:317 |
| POST | api/database/backups/s3-uploads/{uploadResultId}/retry | DatabaseAdminController.RetryBackupS3UploadAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:209 |
| PUT | api/database/backups/settings | DatabaseAdminController.UpdateBackupSettingsAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:77 |
| POST | api/database/backups/yandex-disk-uploads/{uploadResultId}/retry | DatabaseAdminController.RetryBackupYandexDiskUploadAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:230 |
| POST | api/database/import/spreadsheets/apply | DatabaseAdminController.ApplySpreadsheetImportAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:397 |
| POST | api/database/import/spreadsheets/batches/{batchId:int}/rollback | DatabaseAdminController.RollbackSpreadsheetImportBatchAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:475 |
| POST | api/database/import/spreadsheets/preview | DatabaseAdminController.PreviewSpreadsheetImportAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:372 |
| POST | api/database/import/sqlite | DatabaseAdminController.ImportSqliteAsync | изменение данных, чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:332 |
| POST | api/dictionaries/document-statuses | DictionariesAdminController.CreateDocumentStatus | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1124 |
| DELETE | api/dictionaries/document-statuses/{id:int} | DictionariesAdminController.DeleteDocumentStatus | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1176 |
| POST | api/dictionaries/invoice-item-names | DictionariesAdminController.CreateInvoiceItemName | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:990 |
| PUT | api/dictionaries/invoice-item-names/{id:int} | DictionariesAdminController.UpdateInvoiceItemName | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1000 |
| DELETE | api/dictionaries/invoice-item-names/{id:int} | DictionariesAdminController.DeleteInvoiceItemName | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1015 |
| POST | api/dictionaries/invoice-item-names/{id:int}/toggle | DictionariesAdminController.ToggleInvoiceItemName | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1025 |
| POST | api/dictionaries/invoice-units | DictionariesAdminController.CreateInvoiceUnit | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:951 |
| PUT | api/dictionaries/invoice-units/{id:int} | DictionariesAdminController.UpdateInvoiceUnit | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:959 |
| DELETE | api/dictionaries/invoice-units/{id:int} | DictionariesAdminController.DeleteInvoiceUnit | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:969 |
| POST | api/dictionaries/invoice-units/{id:int}/toggle | DictionariesAdminController.ToggleInvoiceUnit | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:979 |
| POST | api/dictionaries/payment-sources | DictionariesAdminController.CreatePaymentSource | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:420 |
| PUT | api/dictionaries/payment-sources/{id:int} | DictionariesAdminController.UpdatePaymentSource | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:428 |
| DELETE | api/dictionaries/payment-sources/{id:int} | DictionariesAdminController.DeletePaymentSource | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:438 |
| POST | api/dictionaries/payment-sources/{id:int}/toggle | DictionariesAdminController.TogglePaymentSource | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:450 |
| POST | api/dictionaries/payment-statuses | DictionariesAdminController.CreatePaymentStatus | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1082 |
| PUT | api/dictionaries/payment-statuses/{id:int} | DictionariesAdminController.UpdatePaymentStatus | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1090 |
| DELETE | api/dictionaries/payment-statuses/{id:int} | DictionariesAdminController.DeletePaymentStatus | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1100 |
| POST | api/dictionaries/payment-statuses/{id:int}/toggle | DictionariesAdminController.TogglePaymentStatus | изменение данных, чувствительные данные или операция | backend/Controllers/DictionariesAdminController.cs:1112 |
| POST | api/electronic-signatures/financial-documents/{documentType}/{documentId:int}/prepare | ElectronicSignaturesController.Prepare | изменение данных, чувствительные данные или операция | backend/Controllers/ElectronicSignaturesController.cs:62 |
| POST | api/google-calendar/auth/start | GoogleCalendarController.StartAuth | изменение данных, чувствительные данные или операция | backend/Controllers/GoogleCalendarController.cs:42 |
| PUT | api/google-calendar/settings | GoogleCalendarController.UpdateSettings | изменение данных, чувствительные данные или операция | backend/Controllers/GoogleCalendarController.cs:100 |
| POST | api/messenger-document-deliveries/{deliveryId:int}/queue | MessengerDocumentDeliveriesController.Queue | изменение данных, чувствительные данные или операция | backend/Controllers/MessengerDocumentDeliveriesController.cs:265 |
| POST | api/messenger-document-deliveries/case-documents/{documentId:int}/prepare | MessengerDocumentDeliveriesController.PrepareCaseDocument | изменение данных, чувствительные данные или операция | backend/Controllers/MessengerDocumentDeliveriesController.cs:122 |
| POST | api/messenger-document-deliveries/financial-documents/{documentType}/{documentId:int}/prepare | MessengerDocumentDeliveriesController.PrepareFinancialDocument | изменение данных, чувствительные данные или операция | backend/Controllers/MessengerDocumentDeliveriesController.cs:164 |
| POST | api/messenger-document-deliveries/recipients | MessengerDocumentDeliveriesController.CreateRecipient | изменение данных, чувствительные данные или операция | backend/Controllers/MessengerDocumentDeliveriesController.cs:80 |
| PUT | api/notifications/role-preferences/{roleId:int} | NotificationsController.UpdateRolePreferences | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:176 |
| PUT | api/notifications/senders/{id:int}/telegram/profile | NotificationsController.ApplyTelegramBotProfile | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:363 |
| POST | api/notifications/senders/{id:int}/telegram/profile/sync | NotificationsController.SyncTelegramBotProfile | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:340 |
| PUT | api/notifications/settings/email | NotificationsController.UpdateEmailSettings | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:56 |
| PUT | api/notifications/settings/max | NotificationsController.UpdateMaxSettings | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:83 |
| PUT | api/notifications/settings/sms | NotificationsController.UpdateSmsSettings | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:74 |
| PUT | api/notifications/settings/telegram | NotificationsController.UpdateTelegramSettings | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:65 |
| POST | api/notifications/users/{id:int}/issue-password | NotificationsController.IssuePassword | изменение данных, чувствительные данные или операция | backend/Controllers/NotificationsController.cs:569 |
| POST | api/portal/logout | ClientPortalController.Logout | публичный доступ, изменение данных | backend/Controllers/ClientPortalController.cs:158 |
| POST | api/portal/max/link | ClientPortalController.CreateMaxLink | публичный доступ, изменение данных | backend/Controllers/ClientPortalController.cs:181 |
| DELETE | api/portal/max/link | ClientPortalController.DeleteMaxLink | публичный доступ, изменение данных | backend/Controllers/ClientPortalController.cs:198 |
| GET | api/portal/profile | ClientPortalController.Profile | публичный доступ, чувствительные данные или операция | backend/Controllers/ClientPortalController.cs:111 |
| PUT | api/profile | ProfileController.UpdateProfile | изменение данных, чувствительные данные или операция | backend/Controllers/ProfileController.cs:112 |
| DELETE | api/profile/external/{provider} | ProfileController.DetachExternalIdentity | изменение данных, чувствительные данные или операция | backend/Controllers/ProfileController.cs:280 |
| POST | api/profile/password | ProfileController.ChangePassword | изменение данных, чувствительные данные или операция | backend/Controllers/ProfileController.cs:136 |
| POST | api/public/work-order-approvals/{publicId:guid}/attachments/{attachmentPublicId:guid}/download | PublicWorkOrderApprovalController.DownloadAttachment | публичный доступ, изменение данных | backend/Controllers/PublicWorkOrderApprovalController.cs:89 |
| POST | api/public/work-order-approvals/{publicId:guid}/attachments/{attachmentPublicId:guid}/grant | PublicWorkOrderApprovalController.CreateAttachmentGrant | публичный доступ, изменение данных | backend/Controllers/PublicWorkOrderApprovalController.cs:67 |
| POST | api/public/work-order-approvals/{publicId:guid}/decision | PublicWorkOrderApprovalController.Decide | публичный доступ, изменение данных | backend/Controllers/PublicWorkOrderApprovalController.cs:40 |
| POST | api/public/work-order-approvals/{publicId:guid}/open | PublicWorkOrderApprovalController.Open | публичный доступ, изменение данных | backend/Controllers/PublicWorkOrderApprovalController.cs:23 |
| POST | api/roles | RolesController.CreateRole | изменение данных, чувствительные данные или операция | backend/Controllers/RolesController.cs:94 |
| PUT | api/roles/{id:int} | RolesController.UpdateRole | изменение данных, чувствительные данные или операция | backend/Controllers/RolesController.cs:197 |
| DELETE | api/roles/{id:int} | RolesController.DeleteRole | изменение данных, чувствительные данные или операция | backend/Controllers/RolesController.cs:241 |
| POST | api/roles/{id:int}/copy | RolesController.CopyRole | изменение данных, чувствительные данные или операция | backend/Controllers/RolesController.cs:142 |
| POST | api/user-activity/admin/cleanup | UserActivityController.CleanupAsync | изменение данных, чувствительные данные или операция | backend/Controllers/UserActivityController.cs:467 |
| POST | api/user-activity/admin/compact | UserActivityController.CompactAsync | изменение данных, чувствительные данные или операция | backend/Controllers/UserActivityController.cs:474 |
| PUT | api/user-activity/admin/settings | UserActivityController.UpdateSettingsAsync | изменение данных, чувствительные данные или операция | backend/Controllers/UserActivityController.cs:457 |
| DELETE | api/user-activity/cleanup | UserActivityController.DeleteAllAsync | изменение данных, чувствительные данные или операция | backend/Controllers/UserActivityController.cs:487 |
| PUT | api/user-settings/{key} | UserSettingsController.Upsert | изменение данных, чувствительные данные или операция | backend/Controllers/UserSettingsController.cs:59 |
| PUT | api/user-settings/users/{userId:int}/{key} | UserSettingsController.UpsertForUser | изменение данных, чувствительные данные или операция | backend/Controllers/UserSettingsController.cs:81 |
| POST | api/users | UsersController.CreateUser | изменение данных, чувствительные данные или операция | backend/Controllers/UsersController.cs:54 |
| DELETE | api/users/{id:int} | UsersController.DeleteUser | изменение данных, чувствительные данные или операция | backend/Controllers/UsersController.cs:78 |
| PUT | api/users/{id:int} | UsersController.UpdateUser | изменение данных, чувствительные данные или операция | backend/Controllers/UsersController.cs:231 |
| POST | api/users/{id:int}/approve | UsersController.ApproveUser | изменение данных, чувствительные данные или операция | backend/Controllers/UsersController.cs:41 |
| POST | api/users/{id:int}/reject | UsersController.RejectUser | изменение данных, чувствительные данные или операция | backend/Controllers/UsersController.cs:178 |
| POST | api/users/merge | UsersController.MergeUsers | изменение данных, чувствительные данные или операция | backend/Controllers/UsersController.cs:212 |
| POST | api/yandex-calendar/auth/start | YandexCalendarController.StartAuth | изменение данных, чувствительные данные или операция | backend/Controllers/YandexCalendarController.cs:42 |
| PUT | api/yandex-calendar/settings | YandexCalendarController.UpdateSettings | изменение данных, чувствительные данные или операция | backend/Controllers/YandexCalendarController.cs:100 |
| POST | api/yandex-disk/auth/start | YandexDiskController.StartOAuth | изменение данных, чувствительные данные или операция | backend/Controllers/YandexDiskController.cs:77 |
| PUT | api/yandex-disk/settings | YandexDiskController.UpdateSettings | изменение данных, чувствительные данные или операция | backend/Controllers/YandexDiskController.cs:192 |
| POST |  | PowerOfAttorneyController.Create | изменение данных | backend/Controllers/PowerOfAttorneyController.cs:32 |
| PUT | {id:int} | PowerOfAttorneyController.Update | изменение данных | backend/Controllers/PowerOfAttorneyController.cs:55 |
| DELETE | {id:int} | PowerOfAttorneyController.Delete | изменение данных | backend/Controllers/PowerOfAttorneyController.cs:74 |
| POST | api/[controller]/{id:int}/mark-not-signed | ActsController.MarkNotSigned | изменение данных | backend/Controllers/ActsController.cs:640 |
| POST | api/[controller]/{id:int}/mark-not-signed | ContractsController.MarkNotSigned | изменение данных | backend/Controllers/ContractsController.cs:901 |
| POST | api/[controller]/{id:int}/mark-not-signed | InvoicesController.MarkNotSigned | изменение данных | backend/Controllers/InvoicesController.cs:816 |
| POST | api/[controller]/calc | InstallmentsController.Calc | изменение данных | backend/Controllers/InstallmentsController.cs:19 |
| GET | api/[controller]/document-template-settings | ActsController.GetDocumentTemplateSettings | чувствительные данные или операция | backend/Controllers/ActsController.cs:1308 |
| GET | api/[controller]/document-template-settings | ContractsController.GetDocumentTemplateSettings | чувствительные данные или операция | backend/Controllers/ContractsController.cs:707 |
| GET | api/[controller]/document-template-settings/history/{versionId:int} | InvoicesController.GetDocumentTemplateVersion | чувствительные данные или операция | backend/Controllers/InvoicesController.cs:2401 |
| GET | api/[controller]/numbering-settings | ContractsController.GetNumberingSettings | чувствительные данные или операция | backend/Controllers/ContractsController.cs:1161 |
| GET | api/[controller]/rules-settings | ContractsController.GetRulesSettings | чувствительные данные или операция | backend/Controllers/ContractsController.cs:1181 |
| GET | api/auth/admin/providers | AuthProvidersAdminController.GetSettings | чувствительные данные или операция | backend/Controllers/AuthProvidersAdminController.cs:37 |
| GET | api/auth/admin/roles | AdminUsersController.GetRoles | чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:192 |
| GET | api/auth/admin/users | AdminUsersController.GetUsers | чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:38 |
| GET | api/auth/admin/users/{id:int} | AdminUsersController.GetUser | чувствительные данные или операция | backend/Controllers/AdminUsersController.cs:42 |
| GET | api/bank-connections/{id:int}/profile-account-matches | BankConnectionsController.GetProfileAccountMatches | чувствительные данные или операция | backend/Controllers/BankConnectionsController.cs:161 |
| GET | api/calendar-sync/settings | CalendarSyncController.GetSettings | чувствительные данные или операция | backend/Controllers/CalendarSyncController.cs:74 |
| GET | api/database/backups | DatabaseAdminController.GetBackupsAsync | чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:70 |
| GET | api/database/backups/attempts | DatabaseAdminController.GetBackupAttemptsAsync | чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:104 |
| GET | api/database/backups/catalog | DatabaseAdminController.GetBackupCatalogAsync | чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:127 |
| GET | api/database/backups/download | DatabaseAdminController.DownloadBackupAsync | чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:286 |
| GET | api/database/backups/verify | DatabaseAdminController.VerifyBackupAsync | чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:302 |
| GET | api/database/import/spreadsheets/batches | DatabaseAdminController.GetSpreadsheetImportBatchesAsync | чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:436 |
| GET | api/database/import/spreadsheets/batches/{batchId:int} | DatabaseAdminController.GetSpreadsheetImportBatchAsync | чувствительные данные или операция | backend/Controllers/DatabaseAdminController.cs:462 |
| POST | api/dictionaries/base-currencies | DictionariesAdminController.CreateBaseCurrency | изменение данных | backend/Controllers/DictionariesAdminController.cs:699 |
| PUT | api/dictionaries/base-currencies/{id:int} | DictionariesAdminController.UpdateBaseCurrency | изменение данных | backend/Controllers/DictionariesAdminController.cs:707 |
| DELETE | api/dictionaries/base-currencies/{id:int} | DictionariesAdminController.DeleteBaseCurrency | изменение данных | backend/Controllers/DictionariesAdminController.cs:717 |
| POST | api/dictionaries/base-currencies/{id:int}/toggle | DictionariesAdminController.ToggleBaseCurrency | изменение данных | backend/Controllers/DictionariesAdminController.cs:729 |
| POST | api/dictionaries/client-statuses | DictionariesAdminController.CreateClientStatus | изменение данных | backend/Controllers/DictionariesAdminController.cs:1037 |
| PUT | api/dictionaries/client-statuses/{id:int} | DictionariesAdminController.UpdateClientStatus | изменение данных | backend/Controllers/DictionariesAdminController.cs:1046 |
| DELETE | api/dictionaries/client-statuses/{id:int} | DictionariesAdminController.DeleteClientStatus | изменение данных | backend/Controllers/DictionariesAdminController.cs:1057 |
| POST | api/dictionaries/client-statuses/{id:int}/toggle | DictionariesAdminController.ToggleClientStatus | изменение данных | backend/Controllers/DictionariesAdminController.cs:1070 |
| POST | api/dictionaries/countries | DictionariesAdminController.CreateCountry | изменение данных | backend/Controllers/DictionariesAdminController.cs:657 |
| PUT | api/dictionaries/countries/{id:int} | DictionariesAdminController.UpdateCountry | изменение данных | backend/Controllers/DictionariesAdminController.cs:665 |
| DELETE | api/dictionaries/countries/{id:int} | DictionariesAdminController.DeleteCountry | изменение данных | backend/Controllers/DictionariesAdminController.cs:675 |
| POST | api/dictionaries/countries/{id:int}/toggle | DictionariesAdminController.ToggleCountry | изменение данных | backend/Controllers/DictionariesAdminController.cs:687 |
| DELETE | api/dictionaries/departments/{id:int} | DictionariesAdminController.DeleteDepartment | изменение данных | backend/Controllers/DictionariesAdminController.cs:532 |
| POST | api/dictionaries/departments/{id:int}/toggle | DictionariesAdminController.ToggleDepartment | изменение данных | backend/Controllers/DictionariesAdminController.cs:546 |
| GET | api/dictionaries/document-statuses | DictionariesReadController.DocumentStatuses | чувствительные данные или операция | backend/Controllers/DictionariesReadController.cs:204 |
| POST | api/dictionaries/income-types | DictionariesAdminController.CreateIncomeType | изменение данных | backend/Controllers/DictionariesAdminController.cs:378 |
| PUT | api/dictionaries/income-types/{id:int} | DictionariesAdminController.UpdateIncomeType | изменение данных | backend/Controllers/DictionariesAdminController.cs:386 |
| DELETE | api/dictionaries/income-types/{id:int} | DictionariesAdminController.DeleteIncomeType | изменение данных | backend/Controllers/DictionariesAdminController.cs:396 |
| POST | api/dictionaries/income-types/{id:int}/toggle | DictionariesAdminController.ToggleIncomeType | изменение данных | backend/Controllers/DictionariesAdminController.cs:408 |
| GET | api/dictionaries/invoice-item-names | DictionariesReadController.InvoiceItemNames | чувствительные данные или операция | backend/Controllers/DictionariesReadController.cs:174 |
| GET | api/dictionaries/invoice-units | DictionariesReadController.InvoiceUnits | чувствительные данные или операция | backend/Controllers/DictionariesReadController.cs:164 |
| POST | api/dictionaries/organization-forms | DictionariesAdminController.CreateOrganizationForm | изменение данных | backend/Controllers/DictionariesAdminController.cs:783 |
| PUT | api/dictionaries/organization-forms/{id:int} | DictionariesAdminController.UpdateOrganizationForm | изменение данных | backend/Controllers/DictionariesAdminController.cs:791 |
| DELETE | api/dictionaries/organization-forms/{id:int} | DictionariesAdminController.DeleteOrganizationForm | изменение данных | backend/Controllers/DictionariesAdminController.cs:801 |
| POST | api/dictionaries/organization-forms/{id:int}/toggle | DictionariesAdminController.ToggleOrganizationForm | изменение данных | backend/Controllers/DictionariesAdminController.cs:813 |
| GET | api/dictionaries/payment-sources | DictionariesReadController.PaymentSources | чувствительные данные или операция | backend/Controllers/DictionariesReadController.cs:49 |
| GET | api/dictionaries/payment-statuses | DictionariesReadController.PaymentStatuses | чувствительные данные или операция | backend/Controllers/DictionariesReadController.cs:194 |
| POST | api/dictionaries/service-categories | DictionariesAdminController.CreateServiceCategory | изменение данных | backend/Controllers/DictionariesAdminController.cs:614 |
| PUT | api/dictionaries/service-categories/{id:int} | DictionariesAdminController.UpdateServiceCategory | изменение данных | backend/Controllers/DictionariesAdminController.cs:622 |
| DELETE | api/dictionaries/service-categories/{id:int} | DictionariesAdminController.DeleteServiceCategory | изменение данных | backend/Controllers/DictionariesAdminController.cs:632 |
| POST | api/dictionaries/service-categories/{id:int}/toggle | DictionariesAdminController.ToggleServiceCategory | изменение данных | backend/Controllers/DictionariesAdminController.cs:644 |
| POST | api/dictionaries/service-languages | DictionariesAdminController.CreateServiceLanguage | изменение данных | backend/Controllers/DictionariesAdminController.cs:741 |
| PUT | api/dictionaries/service-languages/{id:int} | DictionariesAdminController.UpdateServiceLanguage | изменение данных | backend/Controllers/DictionariesAdminController.cs:749 |
| DELETE | api/dictionaries/service-languages/{id:int} | DictionariesAdminController.DeleteServiceLanguage | изменение данных | backend/Controllers/DictionariesAdminController.cs:759 |
| POST | api/dictionaries/service-languages/{id:int}/toggle | DictionariesAdminController.ToggleServiceLanguage | изменение данных | backend/Controllers/DictionariesAdminController.cs:771 |
| PUT | api/dictionaries/services/{id:int} | DictionariesAdminController.UpdateService | изменение данных | backend/Controllers/DictionariesAdminController.cs:569 |
| DELETE | api/dictionaries/services/{id:int} | DictionariesAdminController.DeleteService | изменение данных | backend/Controllers/DictionariesAdminController.cs:588 |
| POST | api/dictionaries/services/{id:int}/toggle | DictionariesAdminController.ToggleService | изменение данных | backend/Controllers/DictionariesAdminController.cs:602 |
| POST | api/dictionaries/tax-system-modes | DictionariesAdminController.CreateTaxSystemMode | изменение данных | backend/Controllers/DictionariesAdminController.cs:910 |
| PUT | api/dictionaries/tax-system-modes/{id:int} | DictionariesAdminController.UpdateTaxSystemMode | изменение данных | backend/Controllers/DictionariesAdminController.cs:918 |
| DELETE | api/dictionaries/tax-system-modes/{id:int} | DictionariesAdminController.DeleteTaxSystemMode | изменение данных | backend/Controllers/DictionariesAdminController.cs:928 |
| POST | api/dictionaries/tax-system-modes/{id:int}/toggle | DictionariesAdminController.ToggleTaxSystemMode | изменение данных | backend/Controllers/DictionariesAdminController.cs:940 |
| POST | api/dictionaries/tax-systems | DictionariesAdminController.CreateTaxSystem | изменение данных | backend/Controllers/DictionariesAdminController.cs:868 |
| PUT | api/dictionaries/tax-systems/{id:int} | DictionariesAdminController.UpdateTaxSystem | изменение данных | backend/Controllers/DictionariesAdminController.cs:876 |
| DELETE | api/dictionaries/tax-systems/{id:int} | DictionariesAdminController.DeleteTaxSystem | изменение данных | backend/Controllers/DictionariesAdminController.cs:886 |
| POST | api/dictionaries/tax-systems/{id:int}/toggle | DictionariesAdminController.ToggleTaxSystem | изменение данных | backend/Controllers/DictionariesAdminController.cs:898 |
| POST | api/dictionaries/timezones | DictionariesAdminController.CreateTimezone | изменение данных | backend/Controllers/DictionariesAdminController.cs:825 |
| PUT | api/dictionaries/timezones/{id:int} | DictionariesAdminController.UpdateTimezone | изменение данных | backend/Controllers/DictionariesAdminController.cs:833 |
| DELETE | api/dictionaries/timezones/{id:int} | DictionariesAdminController.DeleteTimezone | изменение данных | backend/Controllers/DictionariesAdminController.cs:843 |
| POST | api/dictionaries/timezones/{id:int}/toggle | DictionariesAdminController.ToggleTimezone | изменение данных | backend/Controllers/DictionariesAdminController.cs:855 |
| GET | api/electronic-signatures/configuration | ElectronicSignaturesController.GetConfiguration | чувствительные данные или операция | backend/Controllers/ElectronicSignaturesController.cs:21 |
| GET | api/electronic-signatures/financial-documents/{documentType}/{documentId:int} | ElectronicSignaturesController.GetHistory | чувствительные данные или операция | backend/Controllers/ElectronicSignaturesController.cs:144 |
| GET | api/google-calendar/auth/status/{state} | GoogleCalendarController.GetAuthStatus | чувствительные данные или операция | backend/Controllers/GoogleCalendarController.cs:65 |
| DELETE | api/google-calendar/connection | GoogleCalendarController.Disconnect | изменение данных | backend/Controllers/GoogleCalendarController.cs:157 |
| POST | api/google-calendar/sync-now | GoogleCalendarController.SyncNow | изменение данных | backend/Controllers/GoogleCalendarController.cs:127 |
| PUT | api/internal-chat/threads/{threadId:int}/members | InternalChatController.UpdateChannelMembers | изменение данных | backend/Controllers/InternalChatController.cs:205 |
| PUT | api/internal-chat/threads/{threadId:int}/notification-mode | InternalChatController.SetNotificationMode | изменение данных | backend/Controllers/InternalChatController.cs:928 |
| POST | api/max/correspondence/{publicId:guid}/route | MaxMessengerController.RouteCorrespondence | изменение данных | backend/Controllers/MaxMessengerController.cs:114 |
| POST | api/max/messages | MaxMessengerController.QueueMessage | изменение данных | backend/Controllers/MaxMessengerController.cs:49 |
| POST | api/max/messages/{publicId:guid}/retry | MaxMessengerController.RetryMessage | изменение данных | backend/Controllers/MaxMessengerController.cs:90 |
| GET | api/messenger-document-deliveries | MessengerDocumentDeliveriesController.GetHistory | чувствительные данные или операция | backend/Controllers/MessengerDocumentDeliveriesController.cs:226 |
| GET | api/messenger-document-deliveries/configuration | MessengerDocumentDeliveriesController.GetConfiguration | чувствительные данные или операция | backend/Controllers/MessengerDocumentDeliveriesController.cs:33 |
| GET | api/messenger-document-deliveries/recipients | MessengerDocumentDeliveriesController.GetRecipients | чувствительные данные или операция | backend/Controllers/MessengerDocumentDeliveriesController.cs:55 |
| POST | api/notifications/deliveries/{id:int}/retry | NotificationsController.RetryDelivery | изменение данных | backend/Controllers/NotificationsController.cs:476 |
| POST | api/notifications/inbox/push/subscriptions/revoke | PushSubscriptionsController.RevokeSubscriptionByEndpoint | изменение данных | backend/Controllers/PushSubscriptionsController.cs:211 |
| GET | api/notifications/role-preferences | NotificationsController.GetRolePreferences | чувствительные данные или операция | backend/Controllers/NotificationsController.cs:130 |
| POST | api/notifications/senders | NotificationsController.CreateSender | изменение данных | backend/Controllers/NotificationsController.cs:250 |
| PUT | api/notifications/senders/{id:int} | NotificationsController.UpdateSender | изменение данных | backend/Controllers/NotificationsController.cs:266 |
| DELETE | api/notifications/senders/{id:int} | NotificationsController.DeleteSender | изменение данных | backend/Controllers/NotificationsController.cs:287 |
| DELETE | api/notifications/senders/{id:int}/telegram/webhook | NotificationsController.DeleteTelegramWebhook | изменение данных | backend/Controllers/NotificationsController.cs:317 |
| POST | api/notifications/senders/{id:int}/telegram/webhook/sync | NotificationsController.SyncTelegramWebhook | изменение данных | backend/Controllers/NotificationsController.cs:294 |
| GET | api/notifications/settings | NotificationsController.GetSettings | чувствительные данные или операция | backend/Controllers/NotificationsController.cs:42 |
| PUT | api/notifications/templates/{key} | NotificationsController.UpdateTemplate | изменение данных | backend/Controllers/NotificationsController.cs:99 |
| POST | api/notifications/templates/{key}/reset | NotificationsController.ResetTemplate | изменение данных | backend/Controllers/NotificationsController.cs:116 |
| POST | api/portal/admin/access | ClientPortalAdminController.SaveAccess | изменение данных | backend/Controllers/ClientPortalAdminController.cs:13 |
| DELETE | api/portal/admin/accounts/{accountId:int} | ClientPortalAdminController.Revoke | изменение данных | backend/Controllers/ClientPortalAdminController.cs:91 |
| DELETE | api/portal/admin/accounts/{accountId:int}/cases/{caseId:int} | ClientPortalAdminController.RevokeCaseAccess | изменение данных | backend/Controllers/ClientPortalAdminController.cs:119 |
| POST | api/portal/admin/accounts/{accountId:int}/login | ClientPortalAdminController.SendLogin | изменение данных | backend/Controllers/ClientPortalAdminController.cs:78 |
| GET | api/portal/cases/{casePublicId:guid} | ClientPortalController.GetCase | публичный доступ | backend/Controllers/ClientPortalController.cs:91 |
| GET | api/portal/home | ClientPortalController.Home | публичный доступ | backend/Controllers/ClientPortalController.cs:69 |
| GET | api/profile | ProfileController.GetProfile | чувствительные данные или операция | backend/Controllers/ProfileController.cs:46 |
| PUT | api/requests/{id:int} | SalesRequestsController.Update | изменение данных | backend/Controllers/SalesRequestsController.cs:393 |
| POST | api/requests/{id:int}/tasks | SalesRequestsController.CreateTask | изменение данных | backend/Controllers/SalesRequestsController.cs:425 |
| POST | api/requests/{id:int}/transition | SalesRequestsController.Transition | изменение данных | backend/Controllers/SalesRequestsController.cs:413 |
| POST | api/requests/statuses | SalesRequestsController.CreateStatus | изменение данных | backend/Controllers/SalesRequestsController.cs:158 |
| PUT | api/requests/statuses/{id:int} | SalesRequestsController.UpdateStatus | изменение данных | backend/Controllers/SalesRequestsController.cs:162 |
| PUT | api/requests/statuses/{id:int}/rules | SalesRequestsController.ReplaceRules | изменение данных | backend/Controllers/SalesRequestsController.cs:166 |
| GET | api/role-permissions/calendar-users | RolePermissionsController.GetCalendarUsers | чувствительные данные или операция | backend/Controllers/RolePermissionsController.cs:158 |
| GET | api/roles/{id:int} | RolesController.GetRole | чувствительные данные или операция | backend/Controllers/RolesController.cs:78 |
| GET | api/roles/paged | RolesController.GetRolesPaged | чувствительные данные или операция | backend/Controllers/RolesController.cs:44 |
| DELETE | api/s3-storages/{id} | S3StorageController.DeleteConnection | изменение данных | backend/Controllers/S3StorageController.cs:60 |
| POST | api/s3-storages/{id}/copy | S3StorageController.CopyObject | изменение данных | backend/Controllers/S3StorageController.cs:213 |
| POST | api/s3-storages/{id}/move | S3StorageController.MoveObject | изменение данных | backend/Controllers/S3StorageController.cs:230 |
| DELETE | api/s3-storages/{id}/objects | S3StorageController.DeleteObject | изменение данных | backend/Controllers/S3StorageController.cs:195 |
| PUT | api/s3-storages/{id}/text | S3StorageController.SaveTextFile | изменение данных | backend/Controllers/S3StorageController.cs:259 |
| POST | api/sticky-notes/boards | StickyNotesController.CreateBoard | изменение данных | backend/Controllers/StickyNotesController.cs:100 |
| POST | api/storage/capacity/stages/markers/reconcile | StorageAdminController.ReconcileStageMarkers | изменение данных | backend/Controllers/StorageAdminController.cs:202 |
| POST | api/storage/legacy-backfill | StorageAdminController.BackfillLegacyStorage | изменение данных | backend/Controllers/StorageAdminController.cs:224 |
| POST | api/storage/legacy-replicas | LegacyStorageMigrationController.Run | изменение данных | backend/Controllers/LegacyStorageMigrationController.cs:12 |
| DELETE | api/storage/objects/{id:guid} | StorageAdminController.DeleteObject | изменение данных | backend/Controllers/StorageAdminController.cs:75 |
| POST | api/storage/reconcile | StorageAdminController.Reconcile | изменение данных | backend/Controllers/StorageAdminController.cs:175 |
| GET | api/tax-register/book/export.csv | TaxRegisterController.ExportCsv | чувствительные данные или операция | backend/Controllers/TaxRegisterController.cs:187 |
| GET | api/tax-register/book/export.xlsx | TaxRegisterController.ExportXlsx | чувствительные данные или операция | backend/Controllers/TaxRegisterController.cs:202 |
| POST | api/tax-register/close-year | TaxRegisterController.CloseYear | изменение данных | backend/Controllers/TaxRegisterController.cs:216 |
| POST | api/tax-register/entries | TaxRegisterController.Create | изменение данных | backend/Controllers/TaxRegisterController.cs:120 |
| PUT | api/tax-register/entries/{id:int} | TaxRegisterController.Update | изменение данных | backend/Controllers/TaxRegisterController.cs:124 |
| POST | api/tax-register/entries/{id:int}/approve | TaxRegisterController.Approve | изменение данных | backend/Controllers/TaxRegisterController.cs:128 |
| POST | api/tax-register/entries/{id:int}/archive | TaxRegisterController.Archive | изменение данных | backend/Controllers/TaxRegisterController.cs:132 |
| POST | api/tax-register/entries/{id:int}/correct | TaxRegisterController.Correct | изменение данных | backend/Controllers/TaxRegisterController.cs:136 |
| GET | api/user-activity/filters | UserActivityController.GetFiltersAsync | чувствительные данные или операция | backend/Controllers/UserActivityController.cs:302 |
| GET | api/users | UsersController.GetUsers | чувствительные данные или операция | backend/Controllers/UsersController.cs:167 |
| GET | api/users/{id:int} | UsersController.GetUser | чувствительные данные или операция | backend/Controllers/UsersController.cs:115 |
| GET | api/users/paged | UsersController.GetUsersPaged | чувствительные данные или операция | backend/Controllers/UsersController.cs:171 |
| GET | api/users/roles | UsersController.GetRoles | чувствительные данные или операция | backend/Controllers/UsersController.cs:112 |
| GET | api/yandex-calendar/auth/status/{state} | YandexCalendarController.GetAuthStatus | чувствительные данные или операция | backend/Controllers/YandexCalendarController.cs:65 |
| DELETE | api/yandex-calendar/connection | YandexCalendarController.Disconnect | изменение данных | backend/Controllers/YandexCalendarController.cs:157 |
| POST | api/yandex-calendar/sync-now | YandexCalendarController.SyncNow | изменение данных | backend/Controllers/YandexCalendarController.cs:127 |
| GET | api/yandex-disk/auth/status/{state} | YandexDiskController.GetOAuthStatus | чувствительные данные или операция | backend/Controllers/YandexDiskController.cs:105 |
| PUT | api/yandex-disk/connections | YandexDiskController.UpsertConnection | изменение данных | backend/Controllers/YandexDiskController.cs:55 |
| DELETE | api/yandex-disk/connections/{id} | YandexDiskController.DeleteConnection | изменение данных | backend/Controllers/YandexDiskController.cs:138 |
| GET | api/yandex-disk/crm-sync/settings | YandexDiskController.GetCrmSyncSettings | чувствительные данные или операция | backend/Controllers/YandexDiskController.cs:231 |
| GET | api/yandex-disk/settings | YandexDiskController.GetSettings | чувствительные данные или операция | backend/Controllers/YandexDiskController.cs:183 |

## Все HTTP endpoints

| Метод | Маршрут | Контроллер | Действие | Файл:строка | Правило доступа | Прямой тест действия | Документация |
| --- | --- | --- | --- | --- | --- | --- | --- |
| GET | api/[controller]/registry | AccountsController | GetRegistry | backend/Controllers/AccountsController.cs:32 | [Authorize] | да | docs/invoice-cancellation-history-checkpoint-2026-10-31.md |
| GET | api/[controller]/payables-dashboard | AccountsController | GetPayablesDashboard | backend/Controllers/AccountsController.cs:532 | [Authorize] | да | нет |
| GET | api/[controller] | AccountsController | Get | backend/Controllers/AccountsController.cs:676 | [Authorize] | да | docs/full-project-audit-2026-06-10.md, docs/invoice-cancellation-history-checkpoint-2026-10-31.md |
| GET | api/[controller] | ActsController | Get | backend/Controllers/ActsController.cs:200 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/[controller]/{id:int} | ActsController | GetById | backend/Controllers/ActsController.cs:532 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/send-to-signing | ActsController | SendToSigning | backend/Controllers/ActsController.cs:560 | [Authorize] | да | нет |
| PUT | api/[controller]/{id:int}/seller | ActsController | SetSeller | backend/Controllers/ActsController.cs:589 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/mark-signed | ActsController | MarkSigned | backend/Controllers/ActsController.cs:613 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/mark-not-signed | ActsController | MarkNotSigned | backend/Controllers/ActsController.cs:640 | [Authorize] | нет | нет |
| POST | api/[controller]/{id:int}/workflow-status | ActsController | UpdateWorkflowStatus | backend/Controllers/ActsController.cs:666 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/cancel | ActsController | Cancel | backend/Controllers/ActsController.cs:759 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/correction | ActsController | CreateCorrection | backend/Controllers/ActsController.cs:801 | [Authorize] | да | нет |
| GET | api/[controller]/summary | ActsController | GetSummary | backend/Controllers/ActsController.cs:917 | [Authorize] | да | нет |
| GET | api/[controller]/responsibles | ActsController | GetResponsibles | backend/Controllers/ActsController.cs:1053 | [Authorize] | да | нет |
| POST | api/[controller] | ActsController | Create | backend/Controllers/ActsController.cs:1069 | [Authorize] | да | docs/full-project-audit-2026-06-10.md, docs/payplanner-financial-documents-roadmap-2026-10-14.md |
| PUT | api/[controller]/{id:int} | ActsController | Update | backend/Controllers/ActsController.cs:1164 | [Authorize] | да | нет |
| DELETE | api/[controller]/{id:int} | ActsController | Delete | backend/Controllers/ActsController.cs:1275 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings | ActsController | GetDocumentTemplateSettings | backend/Controllers/ActsController.cs:1308 | [Authorize] | нет | нет |
| PUT | api/[controller]/document-template-settings | ActsController | UpsertDocumentTemplateSettings | backend/Controllers/ActsController.cs:1312 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings/history | ActsController | GetDocumentTemplateHistory | backend/Controllers/ActsController.cs:1340 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings/history/{versionId:int} | ActsController | GetDocumentTemplateVersion | backend/Controllers/ActsController.cs:1347 | [Authorize] | да | нет |
| POST | api/[controller]/document-template-settings/history/{versionId:int}/restore | ActsController | RestoreDocumentTemplateVersion | backend/Controllers/ActsController.cs:1357 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/image-print-defaults | ActsController | GetImagePrintDefaults | backend/Controllers/ActsController.cs:1385 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/document | ActsController | GetDocument | backend/Controllers/ActsController.cs:1411 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/yandex-disk | ActsController | UploadDocumentToYandexDisk | backend/Controllers/ActsController.cs:1469 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/send-email | ActsController | SendActEmail | backend/Controllers/ActsController.cs:1599 | [Authorize] | да | нет |
| GET | api/auth/admin/users | AdminUsersController | GetUsers | backend/Controllers/AdminUsersController.cs:38 | [Authorize(Policy = "Admin")] | нет | docs/full-project-audit-2026-06-10.md |
| GET | api/auth/admin/users/{id:int} | AdminUsersController | GetUser | backend/Controllers/AdminUsersController.cs:42 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/auth/admin/users | AdminUsersController | CreateUser | backend/Controllers/AdminUsersController.cs:49 | [Authorize(Policy = "Admin")] | нет | docs/full-project-audit-2026-06-10.md |
| PUT | api/auth/admin/users/{id:int} | AdminUsersController | UpdateUser | backend/Controllers/AdminUsersController.cs:73 | [Authorize(Policy = "Admin")] | нет | нет |
| DELETE | api/auth/admin/users/{id:int} | AdminUsersController | DeleteUser | backend/Controllers/AdminUsersController.cs:116 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/auth/admin/users/{id:int}/approve | AdminUsersController | ApproveUser | backend/Controllers/AdminUsersController.cs:146 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/auth/admin/users/{id:int}/reject | AdminUsersController | RejectUser | backend/Controllers/AdminUsersController.cs:159 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/auth/admin/roles | AdminUsersController | GetRoles | backend/Controllers/AdminUsersController.cs:192 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/appearance-settings | AppearanceSettingsController | Get | backend/Controllers/AppearanceSettingsController.cs:39 | [Authorize] | да | нет |
| PUT | api/appearance-settings | AppearanceSettingsController | Update | backend/Controllers/AppearanceSettingsController.cs:53 | [Authorize] | да | нет |
| GET | api/app-version/latest | AppVersionController | GetLatest | backend/Controllers/AppVersionController.cs:49 | [Authorize] | да | нет |
| GET | api/app-version/history | AppVersionController | GetHistory | backend/Controllers/AppVersionController.cs:82 | [Authorize] | да | нет |
| POST | api/app-version/mark-seen | AppVersionController | MarkSeen | backend/Controllers/AppVersionController.cs:89 | [Authorize] | да | нет |
| GET | api/app-version/admin/releases | AppVersionController | GetAdminReleases | backend/Controllers/AppVersionController.cs:124 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| GET | api/app-version/admin/releases/paged | AppVersionController | GetAdminReleasesPaged | backend/Controllers/AppVersionController.cs:143 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| POST | api/app-version/admin/releases | AppVersionController | CreateRelease | backend/Controllers/AppVersionController.cs:238 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| PUT | api/app-version/admin/releases/{id:int} | AppVersionController | UpdateRelease | backend/Controllers/AppVersionController.cs:283 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| DELETE | api/app-version/admin/releases/{id:int} | AppVersionController | DeleteRelease | backend/Controllers/AppVersionController.cs:325 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| POST | api/[controller]/login | AuthController | Login | backend/Controllers/AuthController.cs:22 | публичный ([AllowAnonymous]) | да | нет |
| POST | api/[controller]/refresh | AuthController | RefreshSession | backend/Controllers/AuthController.cs:64 | [Authorize] | да | нет |
| POST | api/[controller]/logout | AuthController | Logout | backend/Controllers/AuthController.cs:101 | [Authorize] | да | нет |
| POST | api/[controller]/register | AuthController | Register | backend/Controllers/AuthController.cs:119 | публичный ([AllowAnonymous]) | да | нет |
| GET | api/[controller]/me | AuthController | Me | backend/Controllers/AuthController.cs:166 | [Authorize] | да | нет |
| GET | api/auth/admin/providers | AuthProvidersAdminController | GetSettings | backend/Controllers/AuthProvidersAdminController.cs:37 | [Authorize(Policy = "Admin")] | нет | docs/full-project-audit-2026-06-10.md |
| PUT | api/auth/admin/providers | AuthProvidersAdminController | UpdateSettings | backend/Controllers/AuthProvidersAdminController.cs:41 | [Authorize(Policy = "Admin")] | нет | docs/full-project-audit-2026-06-10.md |
| PUT | api/auth/admin/providers/google | AuthProvidersAdminController | UpdateGoogleSettings | backend/Controllers/AuthProvidersAdminController.cs:56 | [Authorize(Policy = "Admin")] | нет | нет |
| PUT | api/auth/admin/providers/vk | AuthProvidersAdminController | UpdateVkSettings | backend/Controllers/AuthProvidersAdminController.cs:71 | [Authorize(Policy = "Admin")] | нет | нет |
| PUT | api/auth/admin/providers/yandex | AuthProvidersAdminController | UpdateYandexSettings | backend/Controllers/AuthProvidersAdminController.cs:86 | [Authorize(Policy = "Admin")] | нет | нет |
| PUT | api/auth/admin/providers/telegram/bot | AuthProvidersAdminController | UpdateTelegramBotSettings | backend/Controllers/AuthProvidersAdminController.cs:101 | [Authorize(Policy = "Admin")] | нет | нет |
| PUT | api/auth/admin/providers/telegram/widget | AuthProvidersAdminController | UpdateTelegramWidgetSettings | backend/Controllers/AuthProvidersAdminController.cs:116 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/auth/admin/providers/telegram/sync | AuthProvidersAdminController | SyncTelegram | backend/Controllers/AuthProvidersAdminController.cs:131 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/bank-connections | BankConnectionsController | List | backend/Controllers/BankConnectionsController.cs:132 | [Authorize(Policy = "Admin")] | да | docs/company-bank-binding-checkpoint-2026-11-01.md, docs/deployment-manual-2026-10-21.md, docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/bank-connections/{id:int}/profile-account-matches | BankConnectionsController | GetProfileAccountMatches | backend/Controllers/BankConnectionsController.cs:161 | [Authorize(Policy = "Admin")] | нет | docs/company-bank-binding-checkpoint-2026-11-01.md |
| POST | api/bank-connections/{id:int}/profile-account-bindings | BankConnectionsController | BindProfileAccount | backend/Controllers/BankConnectionsController.cs:175 | [Authorize(Policy = "Admin")] | нет | docs/company-bank-binding-checkpoint-2026-11-01.md |
| POST | api/bank-connections/{id:int}/profile-account-bindings/{bindingId:int}/revoke | BankConnectionsController | RevokeProfileAccount | backend/Controllers/BankConnectionsController.cs:181 | [Authorize(Policy = "Admin")] | нет | docs/company-bank-binding-checkpoint-2026-11-01.md |
| POST | api/bank-connections/{id:int}/sync-accounts | BankConnectionsController | SyncAccounts | backend/Controllers/BankConnectionsController.cs:199 | [Authorize(Policy = "Admin")] | да | нет |
| POST | api/bank-connections/{id:int}/sync-statements | BankConnectionsController | SyncAllStatements | backend/Controllers/BankConnectionsController.cs:211 | [Authorize(Policy = "Admin")] | да | нет |
| POST | api/bank-connections/accounts/{accountId:int}/sync-statement | BankConnectionsController | SyncStatement | backend/Controllers/BankConnectionsController.cs:228 | [Authorize(Policy = "Admin")] | да | нет |
| PATCH | api/bank-connections/accounts/{accountId:int} | BankConnectionsController | UpdateAccount | backend/Controllers/BankConnectionsController.cs:245 | [Authorize(Policy = "Admin")] | да | нет |
| GET | api/bank-connections/accounts/{accountId:int}/operations | BankConnectionsController | GetOperations | backend/Controllers/BankConnectionsController.cs:258 | [Authorize(Policy = "Admin")] | да | нет |
| GET | api/bank-connections/{id:int}/logs | BankConnectionsController | GetLogs | backend/Controllers/BankConnectionsController.cs:298 | [Authorize(Policy = "Admin")] | да | нет |
| GET | api/banks | BanksController | GetBanks | backend/Controllers/BanksController.cs:31 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/banks/{id:int} | BanksController | GetBank | backend/Controllers/BanksController.cs:62 | [Authorize] | да | нет |
| POST | api/banks | BanksController | CreateBank | backend/Controllers/BanksController.cs:79 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/banks/{id:int} | BanksController | UpdateBank | backend/Controllers/BanksController.cs:115 | [Authorize] | да | нет |
| DELETE | api/banks/{id:int} | BanksController | DeleteBank | backend/Controllers/BanksController.cs:154 | [Authorize] | да | нет |
| GET | api/calendar-sync/providers | CalendarSyncController | GetProviders | backend/Controllers/CalendarSyncController.cs:55 | [Authorize] | да | нет |
| GET | api/calendar-sync/readiness | CalendarSyncController | GetReadiness | backend/Controllers/CalendarSyncController.cs:64 | [Authorize] | да | нет |
| GET | api/calendar-sync/settings | CalendarSyncController | GetSettings | backend/Controllers/CalendarSyncController.cs:74 | [Authorize] | нет | нет |
| PUT | api/calendar-sync/settings | CalendarSyncController | UpdateSettings | backend/Controllers/CalendarSyncController.cs:78 | [Authorize]; [Authorize(Roles = "admin")] | да | нет |
| GET | api/calendar-sync/logs | CalendarSyncController | GetLogs | backend/Controllers/CalendarSyncController.cs:137 | [Authorize]; [Authorize(Roles = "admin")] | да | нет |
| GET | api/[controller] | CasesController | Get | backend/Controllers/CasesController.cs:38 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/[controller]/{id:int} | CasesController | GetById | backend/Controllers/CasesController.cs:67 | [Authorize] | да | нет |
| POST | api/[controller] | CasesController | Create | backend/Controllers/CasesController.cs:91 | [Authorize] | да | docs/full-project-audit-2026-06-10.md, docs/invoice-case-ownership-checkpoint-2026-10-31.md |
| PUT | api/[controller]/{id:int} | CasesController | Update | backend/Controllers/CasesController.cs:117 | [Authorize] | да | нет |
| DELETE | api/[controller]/{id:int} | CasesController | Delete | backend/Controllers/CasesController.cs:190 | [Authorize] | да | нет |
| GET | api/cash-orders/cashbook | CashOrdersController | GetCashBook | backend/Controllers/CashOrdersController.cs:74 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| GET | api/cash-orders/cashbook/print | CashOrdersController | PrintCashBook | backend/Controllers/CashOrdersController.cs:119 | [Authorize] | да | нет |
| GET | api/cash-orders/next-number | CashOrdersController | GetNextNumber | backend/Controllers/CashOrdersController.cs:161 | [Authorize] | да | нет |
| GET | api/cash-orders/paged | CashOrdersController | GetPaged | backend/Controllers/CashOrdersController.cs:183 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| GET | api/cash-orders/register | CashOrdersController | GetRegister | backend/Controllers/CashOrdersController.cs:231 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| GET | api/cash-orders/register/print | CashOrdersController | PrintRegister | backend/Controllers/CashOrdersController.cs:321 | [Authorize] | да | нет |
| GET | api/cash-orders/inventories | CashOrdersController | GetInventories | backend/Controllers/CashOrdersController.cs:420 | [Authorize] | да | нет |
| POST | api/cash-orders/inventories | CashOrdersController | CreateInventory | backend/Controllers/CashOrdersController.cs:505 | [Authorize] | да | нет |
| GET | api/cash-orders/{id:int} | CashOrdersController | GetById | backend/Controllers/CashOrdersController.cs:671 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| POST | api/cash-orders/from-payment/{paymentId:int} | CashOrdersController | CreateFromPayment | backend/Controllers/CashOrdersController.cs:696 | [Authorize] | да | нет |
| POST | api/cash-orders/from-money-operation/{operationId:int} | CashOrdersController | CreateFromMoneyOperation | backend/Controllers/CashOrdersController.cs:768 | [Authorize] | да | нет |
| POST | api/cash-orders | CashOrdersController | Create | backend/Controllers/CashOrdersController.cs:888 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md, docs/full-project-audit-2026-06-10.md, docs/report-inventory-2026-08-09.md |
| PUT | api/cash-orders/{id:int} | CashOrdersController | Update | backend/Controllers/CashOrdersController.cs:964 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| DELETE | api/cash-orders/{id:int} | CashOrdersController | Delete | backend/Controllers/CashOrdersController.cs:1028 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| POST | api/cash-orders/{id:int}/post | CashOrdersController | Post | backend/Controllers/CashOrdersController.cs:1062 | [Authorize] | да | нет |
| POST | api/cash-orders/{id:int}/cancel | CashOrdersController | Cancel | backend/Controllers/CashOrdersController.cs:1147 | [Authorize] | да | нет |
| POST | api/cash-orders/{id:int}/copy | CashOrdersController | Copy | backend/Controllers/CashOrdersController.cs:1184 | [Authorize] | да | нет |
| GET | api/cash-orders/{id:int}/print | CashOrdersController | Print | backend/Controllers/CashOrdersController.cs:1275 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| GET | api/cash-orders/payment/{paymentId:int}/print | CashOrdersController | PrintForPayment | backend/Controllers/CashOrdersController.cs:1305 | [Authorize] | да | нет |
| GET | api/cash-orders/{id:int}/pdf | CashOrdersController | DownloadPdf | backend/Controllers/CashOrdersController.cs:1347 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md |
| GET | api/cash-orders/{id:int}/word | CashOrdersController | DownloadWord | backend/Controllers/CashOrdersController.cs:1385 | [Authorize] | да | нет |
| GET |  | ClientBalancesController | GetSummary | backend/Controllers/ClientBalancesController.cs:56 | не задано | да | нет |
| GET | transactions | ClientBalancesController | GetTransactions | backend/Controllers/ClientBalancesController.cs:77 | не задано | да | docs/bank-integrations-api-verification-2026-10-09.md, docs/delete-behavior-contract-2026-06-10.md |
| GET | applications/{requestId:guid} | ClientBalancesController | GetApplication | backend/Controllers/ClientBalancesController.cs:115 | не задано | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/balance-application-recovery-checkpoint-2026-10-31.md |
| POST | applications/{requestId:guid}/cancel | ClientBalancesController | CancelApplication | backend/Controllers/ClientBalancesController.cs:126 | не задано | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md |
| POST | applications/{requestId:guid}/apply | ClientBalancesController | ApplyManaged | backend/Controllers/ClientBalancesController.cs:137 | не задано | да | docs/balance-application-managed-route-checkpoint-2026-10-31.md |
| POST | apply | ClientBalancesController | ApplyCredit | backend/Controllers/ClientBalancesController.cs:147 | не задано | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-durable-flow-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/company-autofill-ownership-checkpoint-2026-11-01.md, docs/development-bank-import-verification-2026-10-16.md, docs/file-upload-limits-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/payment-advance-history-checkpoint-2026-10-31.md, docs/payplanner-handoff-2026-10-21.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/payplanner-storage-roadmap-2026-10-14.md, docs/quality-gates.md, docs/release-verification-history-2026-10-06.md, docs/service-balance-credit-guard-checkpoint-2026-10-31.md, docs/storage-resilience-runbook.md, docs/unpushed-changes-map-2026-10-03.md |
| GET |  | ClientPassportController | Get | backend/Controllers/ClientPassportController.cs:32 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да | нет |
| PUT |  | ClientPassportController | Upsert | backend/Controllers/ClientPassportController.cs:75 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)]; [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Edit)] | да | нет |
| POST | api/portal/admin/access | ClientPortalAdminController | SaveAccess | backend/Controllers/ClientPortalAdminController.cs:13 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/portal/admin/accounts/{accountId:int} | ClientPortalAdminController | GetAccount | backend/Controllers/ClientPortalAdminController.cs:38 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/portal/admin/cases/{caseId:int}/accounts | ClientPortalAdminController | GetCaseAccounts | backend/Controllers/ClientPortalAdminController.cs:52 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/portal/admin/cases/{caseId:int}/audit | ClientPortalAdminController | GetCaseAudit | backend/Controllers/ClientPortalAdminController.cs:65 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/portal/admin/accounts/{accountId:int}/login | ClientPortalAdminController | SendLogin | backend/Controllers/ClientPortalAdminController.cs:78 | [Authorize(Policy = "Admin")] | нет | нет |
| DELETE | api/portal/admin/accounts/{accountId:int} | ClientPortalAdminController | Revoke | backend/Controllers/ClientPortalAdminController.cs:91 | [Authorize(Policy = "Admin")] | нет | нет |
| DELETE | api/portal/admin/accounts/{accountId:int}/cases/{caseId:int} | ClientPortalAdminController | RevokeCaseAccess | backend/Controllers/ClientPortalAdminController.cs:119 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/portal/auth/{challengeId:guid}/preview | ClientPortalController | Preview | backend/Controllers/ClientPortalController.cs:22 | публичный ([AllowAnonymous]) | да | нет |
| POST | api/portal/auth/{challengeId:guid}/verify | ClientPortalController | Verify | backend/Controllers/ClientPortalController.cs:39 | публичный ([AllowAnonymous]) | да | нет |
| GET | api/portal/home | ClientPortalController | Home | backend/Controllers/ClientPortalController.cs:69 | публичный ([AllowAnonymous]) | нет | нет |
| GET | api/portal/cases/{casePublicId:guid} | ClientPortalController | GetCase | backend/Controllers/ClientPortalController.cs:91 | публичный ([AllowAnonymous]) | нет | нет |
| GET | api/portal/profile | ClientPortalController | Profile | backend/Controllers/ClientPortalController.cs:111 | публичный ([AllowAnonymous]) | нет | нет |
| POST | api/portal/documents/{documentPublicId:guid}/grant | ClientPortalController | CreateDocumentGrant | backend/Controllers/ClientPortalController.cs:121 | публичный ([AllowAnonymous]) | да | нет |
| POST | api/portal/documents/{documentPublicId:guid}/download | ClientPortalController | DownloadDocument | backend/Controllers/ClientPortalController.cs:132 | публичный ([AllowAnonymous]) | нет | нет |
| POST | api/portal/logout | ClientPortalController | Logout | backend/Controllers/ClientPortalController.cs:158 | публичный ([AllowAnonymous]) | нет | нет |
| POST | api/portal/max/link | ClientPortalController | CreateMaxLink | backend/Controllers/ClientPortalController.cs:181 | публичный ([AllowAnonymous]) | нет | нет |
| DELETE | api/portal/max/link | ClientPortalController | DeleteMaxLink | backend/Controllers/ClientPortalController.cs:198 | публичный ([AllowAnonymous]) | нет | нет |
| GET | api/client-reference-data/okin | ClientReferenceDataController | GetOkin | backend/Controllers/ClientReferenceDataController.cs:26 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да | нет |
| GET | api/client-reference-data/okso | ClientReferenceDataController | GetOkso | backend/Controllers/ClientReferenceDataController.cs:32 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да | нет |
| GET | api/client-reference-data/okpdtr | ClientReferenceDataController | GetOkpdtr | backend/Controllers/ClientReferenceDataController.cs:38 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да | нет |
| GET | api/[controller] | ClientsController | GetAll | backend/Controllers/ClientsController.cs:229 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/[controller]/{id:int} | ClientsController | GetById | backend/Controllers/ClientsController.cs:279 | [Authorize] | да | docs/payplanner-release-roadmap-2026-10-14.md |
| GET | api/[controller]/{id:int}/stats | ClientsController | GetStats | backend/Controllers/ClientsController.cs:314 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/case-finance-summaries | ClientsController | GetCaseFinanceSummaries | backend/Controllers/ClientsController.cs:412 | [Authorize] | да | docs/development-2026-10-16-audit-write-budget.md |
| GET | api/[controller]/{id:int}/case-expense-details | ClientsController | GetCaseExpenseDetails | backend/Controllers/ClientsController.cs:538 | [Authorize] | да | нет |
| POST | api/[controller]/dadata/clean-address | ClientsController | CleanAddress | backend/Controllers/ClientsController.cs:854 | [Authorize] | да | нет |
| POST | api/[controller]/dadata/suggest-address | ClientsController | SuggestAddress | backend/Controllers/ClientsController.cs:942 | [Authorize] | да | нет |
| POST | api/[controller] | ClientsController | Create | backend/Controllers/ClientsController.cs:968 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/[controller]/{id:int} | ClientsController | Update | backend/Controllers/ClientsController.cs:1052 | [Authorize] | да | нет |
| DELETE | api/[controller]/{id:int} | ClientsController | Delete | backend/Controllers/ClientsController.cs:1375 | [Authorize] | да | нет |
| POST | api/[controller]/bulk-delete | ClientsController | BulkDelete | backend/Controllers/ClientsController.cs:1410 | [Authorize] | да | нет |
| POST | api/[controller]/change-responsible | ClientsController | ChangeResponsible | backend/Controllers/ClientsController.cs:1477 | [Authorize] | да | нет |
| GET |  | CompanyBranchesController | List | backend/Controllers/CompanyBranchesController.cs:33 | [Authorize] | да | нет |
| POST |  | CompanyBranchesController | Create | backend/Controllers/CompanyBranchesController.cs:56 | [Authorize] | да | нет |
| PUT | {id:int} | CompanyBranchesController | Update | backend/Controllers/CompanyBranchesController.cs:86 | [Authorize] | да | docs/bank-integrations-api-verification-2026-10-09.md, docs/case-access-query-optimization.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md, docs/case-publication-queue-release-note-draft-2026-10-01.md, docs/company-bank-binding-checkpoint-2026-11-01.md, docs/crm-legal-module-gap-report-2026-05-25.md, docs/development-act-source-access-2026-10-24.md, docs/development-max-task-notifications-2026-10-23.md, docs/development-pause-plan-2026-09-30.md, docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md, docs/financial-invoice-cancellation-current-state-2026-07-08.md, docs/full-project-audit-2026-06-10.md, docs/invoice-cancellation-credit-checkpoint-2026-10-31.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-access-security-roadmap-2026-10-14.md, docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/storage-resilience-runbook.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/task-critical-events-matrix-2026-07-09.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-result-approval-current-state-2026-07-09.md |
| GET | {id:int}/effective-requisites | CompanyBranchesController | GetEffectiveRequisites | backend/Controllers/CompanyBranchesController.cs:127 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/contacts | ContactsController | GetAll | backend/Controllers/ContactsController.cs:54 | [Authorize] | да | docs/contacts-directory-implementation-log.md, docs/full-project-audit-2026-06-10.md |
| PUT | api/contacts/clients/{id:int} | ContactsController | UpdateClientContact | backend/Controllers/ContactsController.cs:252 | [Authorize] | да | нет |
| POST | api/contacts/external | ContactsController | CreateExternalContact | backend/Controllers/ContactsController.cs:323 | [Authorize] | да | нет |
| PUT | api/contacts/external/{id:int} | ContactsController | UpdateExternalContact | backend/Controllers/ContactsController.cs:417 | [Authorize] | да | нет |
| DELETE | api/contacts/external/{id:int} | ContactsController | DeleteExternalContact | backend/Controllers/ContactsController.cs:519 | [Authorize] | да | нет |
| POST | api/contacts/external/{id:int}/promote | ContactsController | PromoteExternalContact | backend/Controllers/ContactsController.cs:545 | [Authorize] | да | нет |
| GET | api/contacts/exports/yealink | ContactsController | DownloadYealinkXml | backend/Controllers/ContactsController.cs:709 | [Authorize] | да | docs/contacts-directory-implementation-log.md |
| GET | api/contacts/phonebook/yealink/{token} | ContactsController | GetYealinkRemotePhonebook | backend/Controllers/ContactsController.cs:766 | публичный ([AllowAnonymous]) | да | docs/contacts-directory-implementation-log.md |
| POST | api/contacts/imports/yealink | ContactsController | ImportYealinkXml | backend/Controllers/ContactsController.cs:810 | [Authorize] | нет | docs/contacts-directory-implementation-log.md |
| POST | merge/preview | ContactsController | PreviewMerge | backend/Controllers/ContactsController.Merge.cs:39 | не задано | да | нет |
| POST | merge | ContactsController | MergeContacts | backend/Controllers/ContactsController.Merge.cs:52 | не задано | да | docs/advance-allocation-recovery-checkpoint-2026-10-31.md, docs/advance-review-batch-checkpoint-2026-10-31.md, docs/advance-review-cursor-checkpoint-2026-10-31.md, docs/advance-review-monitor-checkpoint-2026-10-31.md, docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-durable-flow-checkpoint-2026-10-31.md, docs/balance-application-idempotency-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/balance-application-recovery-checkpoint-2026-10-31.md, docs/balance-application-recovery-ui-checkpoint-2026-10-31.md, docs/balance-source-mutations-checkpoint-2026-10-31.md, docs/calendar-log-cleanup-execution-2026-10-23.md, docs/claude-merge-review-2026-10-11.md, docs/claude-yougile-operations-development-spec-2026-10-10.md, docs/company-approved-access-checkpoint-2026-10-30.md, docs/company-autofill-ownership-checkpoint-2026-11-01.md, docs/company-automatic-read-ownership-checkpoint-2026-10-31.md, docs/company-bank-admin-access-checkpoint-2026-11-01.md, docs/company-bank-binding-checkpoint-2026-11-01.md, docs/company-bank-binding-verification-matrix-2026-11-01.md, docs/company-branch-version-checkpoint-2026-10-31.md, docs/company-branches-access-checkpoint-2026-10-30.md, docs/company-branches-ui-checkpoint-2026-10-30.md, docs/company-creation-concurrency-checkpoint-2026-10-30.md, docs/company-deletion-atomicity-checkpoint-2026-10-30.md, docs/company-deletion-ui-ownership-checkpoint-2026-10-30.md, docs/company-finance-ownership-checkpoint-2026-10-31.md, docs/company-images-remaining-audit-2026-10-30.md, docs/company-legacy-seller-deletion-checkpoint-2026-10-30.md, docs/company-payment-write-ownership-checkpoint-2026-10-31.md, docs/company-profile-concurrency-checkpoint-2026-10-30.md, docs/company-related-read-ownership-checkpoint-2026-10-31.md, docs/company-related-write-ownership-checkpoint-2026-10-31.md, docs/company-settings-authority-checkpoint-2026-10-31.md, docs/company-snapshot-import-remap-plan-2026-10-30.md, docs/compensated-payment-writers-checkpoint-2026-10-31.md, docs/create-attempt-remainder-link-checkpoint-2026-11-01.md, docs/create-remainder-source-snapshot-checkpoint-2026-11-01.md, docs/date-time-inventory.md, docs/deployment-ci-2026-10-25.md, docs/deployment-manual-2026-10-23.md, docs/development-bank-import-verification-2026-10-16.md, docs/development-final-gate-2026-10-21.md, docs/development-main-integration-2026-10-26.md, docs/development-roadmap-merge-2026-10-27.md, docs/development-xsenus-runner-2026-10-21.md, docs/frontend-hook-test-coverage-2026-06-10.md, docs/invoice-cancellation-credit-checkpoint-2026-10-31.md, docs/invoice-cancellation-history-checkpoint-2026-10-31.md, docs/invoice-case-ownership-checkpoint-2026-10-31.md, docs/invoice-reconciliation-boundary-checkpoint-2026-10-31.md, docs/invoice-remainder-writers-checkpoint-2026-10-31.md, docs/legacy-remainder-confirmation-checkpoint-2026-11-01.md, docs/main-company-settings-version-checkpoint-2026-10-30.md, docs/max-latest-reply-drafts-2026-10-15.md, docs/max-today-reply-drafts-2026-10-14.md, docs/money-invoice-allocation-boundary-checkpoint-2026-10-31.md, docs/payment-advance-history-checkpoint-2026-10-31.md, docs/payment-balance-ownership-checkpoint-2026-10-31.md, docs/payment-create-financial-boundary-checkpoint-2026-10-31.md, docs/payment-create-graphs-checkpoint-2026-10-31.md, docs/payment-create-seller-selection-checkpoint-2026-11-01.md, docs/payment-create-server-fields-checkpoint-2026-11-01.md, docs/payment-input-state-checkpoint-2026-11-01.md, docs/payment-overdue-delivery-checkpoint-2026-10-31.md, docs/payment-overdue-outbox-checkpoint-2026-10-31.md, docs/payment-overdue-writer-checkpoint-2026-10-31.md, docs/payment-review-update-checkpoint-2026-11-01.md, docs/payment-writer-balance-boundary-checkpoint-2026-10-31.md, docs/payplanner-access-security-roadmap-2026-10-14.md, docs/payplanner-card-coverage-2026-10-14.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-contacts-telephony-roadmap-2026-10-14.md, docs/payplanner-financial-documents-roadmap-2026-10-14.md, docs/payplanner-handoff-2026-10-21.md, docs/payplanner-handoff-2026-10-25.md, docs/payplanner-messenger-roadmap-2026-10-14.md, docs/payplanner-requests-roadmap-2026-10-14.md, docs/payplanner-roadmap-coverage-2026-10-14.md, docs/payplanner-source-review-2026-10-26.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/payplanner-ui-roadmap-2026-10-14.md, docs/payplanner-unified-roadmap-2026-10-14.md, docs/payplanner-work-order-2026-10-21.md, docs/remainder-financial-identity-checkpoint-2026-10-31.md, docs/remainder-source-refresh-checkpoint-2026-11-01.md, docs/service-balance-credit-guard-checkpoint-2026-10-31.md, docs/v1-advance-notification-checkpoint-2026-11-01.md, docs/versioned-invoice-snapshot-checkpoint-2026-11-01.md, docs/versioned-payment-structure-checkpoint-2026-10-31.md, docs/versioned-payment-writers-checkpoint-2026-10-31.md, docs/yougile-payplanner-drafts-2026-10-12.md, docs/yougile-work-categories-2026-10-12.md |
| GET | api/[controller] | ContractsController | Get | backend/Controllers/ContractsController.cs:315 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/[controller]/{id:int} | ContractsController | GetById | backend/Controllers/ContractsController.cs:348 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/image-print-defaults | ContractsController | GetImagePrintDefaults | backend/Controllers/ContractsController.cs:376 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/document | ContractsController | GetDocument | backend/Controllers/ContractsController.cs:396 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/print | ContractsController | PreparePrintDocument | backend/Controllers/ContractsController.cs:407 | [Authorize] | да | docs/payplanner-company-roadmap-2026-10-14.md |
| GET | api/[controller]/{id:int}/termination-agreement | ContractsController | GetTerminationAgreement | backend/Controllers/ContractsController.cs:463 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/yandex-disk | ContractsController | UploadDocumentToYandexDisk | backend/Controllers/ContractsController.cs:508 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/send-email | ContractsController | SendEmail | backend/Controllers/ContractsController.cs:632 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings | ContractsController | GetDocumentTemplateSettings | backend/Controllers/ContractsController.cs:707 | [Authorize] | нет | нет |
| PUT | api/[controller]/document-template-settings | ContractsController | UpsertDocumentTemplateSettings | backend/Controllers/ContractsController.cs:711 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings/history | ContractsController | GetDocumentTemplateHistory | backend/Controllers/ContractsController.cs:751 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings/history/{versionId:int} | ContractsController | GetDocumentTemplateVersion | backend/Controllers/ContractsController.cs:762 | [Authorize] | да | нет |
| POST | api/[controller]/document-template-settings/history/{versionId:int}/restore | ContractsController | RestoreDocumentTemplateVersion | backend/Controllers/ContractsController.cs:786 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/send-to-signing | ContractsController | SendToSigning | backend/Controllers/ContractsController.cs:836 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/termination-check | ContractsController | GetTerminationCheck | backend/Controllers/ContractsController.cs:863 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/mark-concluded | ContractsController | MarkConcluded | backend/Controllers/ContractsController.cs:872 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/mark-not-signed | ContractsController | MarkNotSigned | backend/Controllers/ContractsController.cs:901 | [Authorize] | нет | нет |
| POST | api/[controller]/{id:int}/status | ContractsController | UpdateStatus | backend/Controllers/ContractsController.cs:929 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/terminate | ContractsController | Terminate | backend/Controllers/ContractsController.cs:1045 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/cancel | ContractsController | Cancel | backend/Controllers/ContractsController.cs:1100 | [Authorize] | да | нет |
| GET | api/[controller]/numbering-settings | ContractsController | GetNumberingSettings | backend/Controllers/ContractsController.cs:1161 | [Authorize] | нет | нет |
| PUT | api/[controller]/numbering-settings | ContractsController | UpsertNumberingSettings | backend/Controllers/ContractsController.cs:1165 | [Authorize] | да | нет |
| GET | api/[controller]/rules-settings | ContractsController | GetRulesSettings | backend/Controllers/ContractsController.cs:1181 | [Authorize] | нет | нет |
| PUT | api/[controller]/rules-settings | ContractsController | UpsertRulesSettings | backend/Controllers/ContractsController.cs:1185 | [Authorize] | да | нет |
| GET | api/[controller]/next-number | ContractsController | GetNextNumber | backend/Controllers/ContractsController.cs:1197 | [Authorize] | нет | нет |
| POST | api/[controller] | ContractsController | Create | backend/Controllers/ContractsController.cs:1214 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/[controller]/{id:int} | ContractsController | Update | backend/Controllers/ContractsController.cs:1277 | [Authorize] | да | нет |
| DELETE | api/[controller]/{id:int} | ContractsController | Delete | backend/Controllers/ContractsController.cs:1360 | [Authorize] | да | нет |
| PUT | api/[controller]/{id:int}/seller | ContractsController | SetSeller | backend/Controllers/ContractsController.cs:1654 | [Authorize] | да | нет |
| GET | api/integrations/dadata/settings | DadataIntegrationSettingsController | Get | backend/Controllers/DadataIntegrationSettingsController.cs:26 | [Authorize] | да | нет |
| PUT | api/integrations/dadata/settings | DadataIntegrationSettingsController | Update | backend/Controllers/DadataIntegrationSettingsController.cs:61 | [Authorize] | да | нет |
| GET | api/database/status | DatabaseAdminController | GetStatus | backend/Controllers/DatabaseAdminController.cs:58 | [Authorize(Policy = "Admin")] | нет | docs/crm-legal-module-release-runbook.md, docs/full-project-audit-2026-06-10.md |
| GET | api/database/backups | DatabaseAdminController | GetBackupsAsync | backend/Controllers/DatabaseAdminController.cs:70 | [Authorize(Policy = "Admin")] | нет | docs/crm-legal-module-release-runbook.md, docs/storage-resilience-runbook.md |
| PUT | api/database/backups/settings | DatabaseAdminController | UpdateBackupSettingsAsync | backend/Controllers/DatabaseAdminController.cs:77 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/database/backups/attempts | DatabaseAdminController | GetBackupAttemptsAsync | backend/Controllers/DatabaseAdminController.cs:104 | [Authorize(Policy = "Admin")] | нет | docs/storage-resilience-runbook.md |
| GET | api/database/backups/catalog | DatabaseAdminController | GetBackupCatalogAsync | backend/Controllers/DatabaseAdminController.cs:127 | [Authorize(Policy = "Admin")] | нет | docs/storage-resilience-runbook.md |
| POST | api/database/backups/catalog/import | DatabaseAdminController | ImportBackupCatalogAsync | backend/Controllers/DatabaseAdminController.cs:167 | [Authorize(Policy = "Admin")] | нет | docs/storage-resilience-runbook.md |
| POST | api/database/backups/discovery | DatabaseAdminController | DiscoverBackupsAsync | backend/Controllers/DatabaseAdminController.cs:181 | [Authorize(Policy = "Admin")] | нет | docs/storage-resilience-runbook.md |
| POST | api/database/backups | DatabaseAdminController | CreateBackupAsync | backend/Controllers/DatabaseAdminController.cs:189 | [Authorize(Policy = "Admin")] | нет | docs/crm-legal-module-release-runbook.md, docs/storage-resilience-runbook.md |
| POST | api/database/backups/s3-uploads/{uploadResultId}/retry | DatabaseAdminController | RetryBackupS3UploadAsync | backend/Controllers/DatabaseAdminController.cs:209 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/database/backups/yandex-disk-uploads/{uploadResultId}/retry | DatabaseAdminController | RetryBackupYandexDiskUploadAsync | backend/Controllers/DatabaseAdminController.cs:230 | [Authorize(Policy = "Admin")] | нет | нет |
| DELETE | api/database/backups/file | DatabaseAdminController | DeleteBackupAsync | backend/Controllers/DatabaseAdminController.cs:251 | [Authorize(Policy = "Admin")] | нет | нет |
| DELETE | api/database/backups/all | DatabaseAdminController | DeleteAllBackupsAsync | backend/Controllers/DatabaseAdminController.cs:273 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/database/backups/download | DatabaseAdminController | DownloadBackupAsync | backend/Controllers/DatabaseAdminController.cs:286 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/database/backups/verify | DatabaseAdminController | VerifyBackupAsync | backend/Controllers/DatabaseAdminController.cs:302 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/database/backups/restore-drill | DatabaseAdminController | RunRestoreDrillAsync | backend/Controllers/DatabaseAdminController.cs:317 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/database/import/sqlite | DatabaseAdminController | ImportSqliteAsync | backend/Controllers/DatabaseAdminController.cs:332 | [Authorize(Policy = "Admin")] | нет | docs/file-upload-limits-2026-06-10.md |
| POST | api/database/import/spreadsheets/preview | DatabaseAdminController | PreviewSpreadsheetImportAsync | backend/Controllers/DatabaseAdminController.cs:372 | [Authorize(Policy = "Admin")] | нет | docs/file-upload-limits-2026-06-10.md |
| POST | api/database/import/spreadsheets/apply | DatabaseAdminController | ApplySpreadsheetImportAsync | backend/Controllers/DatabaseAdminController.cs:397 | [Authorize(Policy = "Admin")] | нет | docs/file-upload-limits-2026-06-10.md |
| GET | api/database/import/spreadsheets/batches | DatabaseAdminController | GetSpreadsheetImportBatchesAsync | backend/Controllers/DatabaseAdminController.cs:436 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/database/import/spreadsheets/batches/{batchId:int} | DatabaseAdminController | GetSpreadsheetImportBatchAsync | backend/Controllers/DatabaseAdminController.cs:462 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/database/import/spreadsheets/batches/{batchId:int}/rollback | DatabaseAdminController | RollbackSpreadsheetImportBatchAsync | backend/Controllers/DatabaseAdminController.cs:475 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/dictionaries/deal-types | DictionariesAdminController | CreateDealType | backend/Controllers/DictionariesAdminController.cs:336 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/dictionaries/deal-types/{id:int} | DictionariesAdminController | UpdateDealType | backend/Controllers/DictionariesAdminController.cs:344 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да | нет |
| DELETE | api/dictionaries/deal-types/{id:int} | DictionariesAdminController | DeleteDealType | backend/Controllers/DictionariesAdminController.cs:354 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да | нет |
| POST | api/dictionaries/deal-types/{id:int}/toggle | DictionariesAdminController | ToggleDealType | backend/Controllers/DictionariesAdminController.cs:366 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да | нет |
| POST | api/dictionaries/income-types | DictionariesAdminController | CreateIncomeType | backend/Controllers/DictionariesAdminController.cs:378 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/income-types/{id:int} | DictionariesAdminController | UpdateIncomeType | backend/Controllers/DictionariesAdminController.cs:386 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/income-types/{id:int} | DictionariesAdminController | DeleteIncomeType | backend/Controllers/DictionariesAdminController.cs:396 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/income-types/{id:int}/toggle | DictionariesAdminController | ToggleIncomeType | backend/Controllers/DictionariesAdminController.cs:408 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/payment-sources | DictionariesAdminController | CreatePaymentSource | backend/Controllers/DictionariesAdminController.cs:420 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/payment-sources/{id:int} | DictionariesAdminController | UpdatePaymentSource | backend/Controllers/DictionariesAdminController.cs:428 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/payment-sources/{id:int} | DictionariesAdminController | DeletePaymentSource | backend/Controllers/DictionariesAdminController.cs:438 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/payment-sources/{id:int}/toggle | DictionariesAdminController | TogglePaymentSource | backend/Controllers/DictionariesAdminController.cs:450 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/departments | DictionariesAdminController | CreateDepartment | backend/Controllers/DictionariesAdminController.cs:462 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да | нет |
| PUT | api/dictionaries/departments/{id:int} | DictionariesAdminController | UpdateDepartment | backend/Controllers/DictionariesAdminController.cs:495 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да | нет |
| DELETE | api/dictionaries/departments/{id:int} | DictionariesAdminController | DeleteDepartment | backend/Controllers/DictionariesAdminController.cs:532 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/departments/{id:int}/toggle | DictionariesAdminController | ToggleDepartment | backend/Controllers/DictionariesAdminController.cs:546 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/services | DictionariesAdminController | CreateService | backend/Controllers/DictionariesAdminController.cs:558 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да | нет |
| PUT | api/dictionaries/services/{id:int} | DictionariesAdminController | UpdateService | backend/Controllers/DictionariesAdminController.cs:569 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/services/{id:int} | DictionariesAdminController | DeleteService | backend/Controllers/DictionariesAdminController.cs:588 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/services/{id:int}/toggle | DictionariesAdminController | ToggleService | backend/Controllers/DictionariesAdminController.cs:602 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/service-categories | DictionariesAdminController | CreateServiceCategory | backend/Controllers/DictionariesAdminController.cs:614 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/service-categories/{id:int} | DictionariesAdminController | UpdateServiceCategory | backend/Controllers/DictionariesAdminController.cs:622 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/service-categories/{id:int} | DictionariesAdminController | DeleteServiceCategory | backend/Controllers/DictionariesAdminController.cs:632 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/service-categories/{id:int}/toggle | DictionariesAdminController | ToggleServiceCategory | backend/Controllers/DictionariesAdminController.cs:644 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/countries | DictionariesAdminController | CreateCountry | backend/Controllers/DictionariesAdminController.cs:657 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/countries/{id:int} | DictionariesAdminController | UpdateCountry | backend/Controllers/DictionariesAdminController.cs:665 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/countries/{id:int} | DictionariesAdminController | DeleteCountry | backend/Controllers/DictionariesAdminController.cs:675 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/countries/{id:int}/toggle | DictionariesAdminController | ToggleCountry | backend/Controllers/DictionariesAdminController.cs:687 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/base-currencies | DictionariesAdminController | CreateBaseCurrency | backend/Controllers/DictionariesAdminController.cs:699 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/base-currencies/{id:int} | DictionariesAdminController | UpdateBaseCurrency | backend/Controllers/DictionariesAdminController.cs:707 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/base-currencies/{id:int} | DictionariesAdminController | DeleteBaseCurrency | backend/Controllers/DictionariesAdminController.cs:717 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/base-currencies/{id:int}/toggle | DictionariesAdminController | ToggleBaseCurrency | backend/Controllers/DictionariesAdminController.cs:729 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/service-languages | DictionariesAdminController | CreateServiceLanguage | backend/Controllers/DictionariesAdminController.cs:741 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/service-languages/{id:int} | DictionariesAdminController | UpdateServiceLanguage | backend/Controllers/DictionariesAdminController.cs:749 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/service-languages/{id:int} | DictionariesAdminController | DeleteServiceLanguage | backend/Controllers/DictionariesAdminController.cs:759 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/service-languages/{id:int}/toggle | DictionariesAdminController | ToggleServiceLanguage | backend/Controllers/DictionariesAdminController.cs:771 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/organization-forms | DictionariesAdminController | CreateOrganizationForm | backend/Controllers/DictionariesAdminController.cs:783 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/organization-forms/{id:int} | DictionariesAdminController | UpdateOrganizationForm | backend/Controllers/DictionariesAdminController.cs:791 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/organization-forms/{id:int} | DictionariesAdminController | DeleteOrganizationForm | backend/Controllers/DictionariesAdminController.cs:801 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/organization-forms/{id:int}/toggle | DictionariesAdminController | ToggleOrganizationForm | backend/Controllers/DictionariesAdminController.cs:813 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/timezones | DictionariesAdminController | CreateTimezone | backend/Controllers/DictionariesAdminController.cs:825 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/timezones/{id:int} | DictionariesAdminController | UpdateTimezone | backend/Controllers/DictionariesAdminController.cs:833 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/timezones/{id:int} | DictionariesAdminController | DeleteTimezone | backend/Controllers/DictionariesAdminController.cs:843 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/timezones/{id:int}/toggle | DictionariesAdminController | ToggleTimezone | backend/Controllers/DictionariesAdminController.cs:855 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/tax-systems | DictionariesAdminController | CreateTaxSystem | backend/Controllers/DictionariesAdminController.cs:868 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/tax-systems/{id:int} | DictionariesAdminController | UpdateTaxSystem | backend/Controllers/DictionariesAdminController.cs:876 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/tax-systems/{id:int} | DictionariesAdminController | DeleteTaxSystem | backend/Controllers/DictionariesAdminController.cs:886 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/tax-systems/{id:int}/toggle | DictionariesAdminController | ToggleTaxSystem | backend/Controllers/DictionariesAdminController.cs:898 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/tax-system-modes | DictionariesAdminController | CreateTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:910 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/tax-system-modes/{id:int} | DictionariesAdminController | UpdateTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:918 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/tax-system-modes/{id:int} | DictionariesAdminController | DeleteTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:928 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/tax-system-modes/{id:int}/toggle | DictionariesAdminController | ToggleTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:940 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/invoice-units | DictionariesAdminController | CreateInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:951 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/invoice-units/{id:int} | DictionariesAdminController | UpdateInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:959 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/invoice-units/{id:int} | DictionariesAdminController | DeleteInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:969 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/invoice-units/{id:int}/toggle | DictionariesAdminController | ToggleInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:979 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/invoice-item-names | DictionariesAdminController | CreateInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:990 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/invoice-item-names/{id:int} | DictionariesAdminController | UpdateInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:1000 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/invoice-item-names/{id:int} | DictionariesAdminController | DeleteInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:1015 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/invoice-item-names/{id:int}/toggle | DictionariesAdminController | ToggleInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:1025 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/client-statuses | DictionariesAdminController | CreateClientStatus | backend/Controllers/DictionariesAdminController.cs:1037 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/client-statuses/{id:int} | DictionariesAdminController | UpdateClientStatus | backend/Controllers/DictionariesAdminController.cs:1046 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/client-statuses/{id:int} | DictionariesAdminController | DeleteClientStatus | backend/Controllers/DictionariesAdminController.cs:1057 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/client-statuses/{id:int}/toggle | DictionariesAdminController | ToggleClientStatus | backend/Controllers/DictionariesAdminController.cs:1070 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/payment-statuses | DictionariesAdminController | CreatePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1082 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/payment-statuses/{id:int} | DictionariesAdminController | UpdatePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1090 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/dictionaries/payment-statuses/{id:int} | DictionariesAdminController | DeletePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1100 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/payment-statuses/{id:int}/toggle | DictionariesAdminController | TogglePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1112 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/document-statuses | DictionariesAdminController | CreateDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1124 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | api/dictionaries/document-statuses/{id:int} | DictionariesAdminController | UpdateDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1146 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да | нет |
| DELETE | api/dictionaries/document-statuses/{id:int} | DictionariesAdminController | DeleteDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1176 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | нет | нет |
| POST | api/dictionaries/document-statuses/{id:int}/toggle | DictionariesAdminController | ToggleDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1192 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да | нет |
| GET | api/dictionaries/simple/{kind}/duplicates | DictionariesAdminController | GetSimpleDictionaryDuplicates | backend/Controllers/DictionariesAdminController.cs:1211 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/dictionaries/simple/{kind}/merge | DictionariesAdminController | MergeSimpleDictionaryDuplicates | backend/Controllers/DictionariesAdminController.cs:1242 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да | нет |
| GET | api/dictionaries/deal-types | DictionariesReadController | DealTypes | backend/Controllers/DictionariesReadController.cs:26 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | docs/full-project-audit-2026-06-10.md |
| GET | api/dictionaries/income-types | DictionariesReadController | IncomeTypes | backend/Controllers/DictionariesReadController.cs:36 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/payment-sources | DictionariesReadController | PaymentSources | backend/Controllers/DictionariesReadController.cs:49 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/departments | DictionariesReadController | Departments | backend/Controllers/DictionariesReadController.cs:59 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/countries | DictionariesReadController | Countries | backend/Controllers/DictionariesReadController.cs:70 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/base-currencies | DictionariesReadController | BaseCurrencies | backend/Controllers/DictionariesReadController.cs:80 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/service-languages | DictionariesReadController | ServiceLanguages | backend/Controllers/DictionariesReadController.cs:90 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/organization-forms | DictionariesReadController | OrganizationForms | backend/Controllers/DictionariesReadController.cs:100 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/timezones | DictionariesReadController | Timezones | backend/Controllers/DictionariesReadController.cs:110 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/tax-systems | DictionariesReadController | TaxSystems | backend/Controllers/DictionariesReadController.cs:121 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/tax-system-modes | DictionariesReadController | TaxSystemModes | backend/Controllers/DictionariesReadController.cs:131 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/services | DictionariesReadController | Services | backend/Controllers/DictionariesReadController.cs:141 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/service-categories | DictionariesReadController | ServiceCategories | backend/Controllers/DictionariesReadController.cs:154 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/invoice-units | DictionariesReadController | InvoiceUnits | backend/Controllers/DictionariesReadController.cs:164 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/invoice-item-names | DictionariesReadController | InvoiceItemNames | backend/Controllers/DictionariesReadController.cs:174 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/client-statuses | DictionariesReadController | ClientStatuses | backend/Controllers/DictionariesReadController.cs:184 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/payment-statuses | DictionariesReadController | PaymentStatuses | backend/Controllers/DictionariesReadController.cs:194 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/dictionaries/document-statuses | DictionariesReadController | DocumentStatuses | backend/Controllers/DictionariesReadController.cs:204 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет | нет |
| GET | api/electronic-signatures/configuration | ElectronicSignaturesController | GetConfiguration | backend/Controllers/ElectronicSignaturesController.cs:21 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/electronic-signatures/recipients | ElectronicSignaturesController | GetRecipients | backend/Controllers/ElectronicSignaturesController.cs:36 | [Authorize(Policy = "Admin")] | да | нет |
| POST | api/electronic-signatures/financial-documents/{documentType}/{documentId:int}/prepare | ElectronicSignaturesController | Prepare | backend/Controllers/ElectronicSignaturesController.cs:62 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/electronic-signatures/financial-documents/{documentType}/{documentId:int} | ElectronicSignaturesController | GetHistory | backend/Controllers/ElectronicSignaturesController.cs:144 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/electronic-signatures/{publicId:guid}/cancel | ElectronicSignaturesController | Cancel | backend/Controllers/ElectronicSignaturesController.cs:161 | [Authorize(Policy = "Admin")] | да | нет |
| GET | api/electronic-signatures/{publicId:guid}/evidence-package | ElectronicSignaturesController | DownloadEvidencePackage | backend/Controllers/ElectronicSignaturesController.cs:182 | [Authorize(Policy = "Admin")] | да | нет |
| GET | api/auth/providers | ExternalAuthController | GetProviders | backend/Controllers/ExternalAuthController.cs:29 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md, docs/full-project-audit-2026-06-10.md |
| POST | api/auth/external/start | ExternalAuthController | StartExternal | backend/Controllers/ExternalAuthController.cs:36 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md |
| GET | api/auth/external/status/{state} | ExternalAuthController | GetExternalStatus | backend/Controllers/ExternalAuthController.cs:61 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md |
| GET | api/auth/external/callback/{provider} | ExternalAuthController | ExternalCallback | backend/Controllers/ExternalAuthController.cs:83 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md |
| POST | api/auth/telegram/widget | ExternalAuthController | CompleteTelegramWidget | backend/Controllers/ExternalAuthController.cs:109 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md |
| POST | api/auth/telegram/bot/start | ExternalAuthController | StartTelegramBot | backend/Controllers/ExternalAuthController.cs:128 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md |
| GET | api/auth/telegram/bot/status/{state} | ExternalAuthController | GetTelegramBotStatus | backend/Controllers/ExternalAuthController.cs:147 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md |
| POST | api/auth/telegram/webhook | ExternalAuthController | TelegramWebhook | backend/Controllers/ExternalAuthController.cs:169 | публичный ([AllowAnonymous]) | да | docs/external-authentication-security-and-performance.md |
| GET | api/features | FeatureFlagsController | Get | backend/Controllers/FeatureFlagsController.cs:27 | публичный ([AllowAnonymous]) | да | docs/crm-legal-module-acceptance-checklist.md, docs/crm-legal-module-release-runbook.md, docs/full-project-audit-2026-06-10.md |
| GET | api/features/legal-integrations/diagnostics | FeatureFlagsController | GetLegalIntegrationDiagnostics | backend/Controllers/FeatureFlagsController.cs:35 | [Authorize(Roles = "admin")] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/form-requirements | FormRequirementsController | Get | backend/Controllers/FormRequirementsController.cs:242 | [Authorize] | нет | docs/full-project-audit-2026-06-10.md |
| GET | api/form-requirements/sections | FormRequirementsController | GetSections | backend/Controllers/FormRequirementsController.cs:273 | [Authorize] | нет | нет |
| PUT | api/form-requirements | FormRequirementsController | Upsert | backend/Controllers/FormRequirementsController.cs:305 | [Authorize]; [Authorize(Policy = "Admin")] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/google-calendar/status | GoogleCalendarController | GetStatus | backend/Controllers/GoogleCalendarController.cs:28 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет | нет |
| POST | api/google-calendar/auth/start | GoogleCalendarController | StartAuth | backend/Controllers/GoogleCalendarController.cs:42 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| GET | api/google-calendar/auth/status/{state} | GoogleCalendarController | GetAuthStatus | backend/Controllers/GoogleCalendarController.cs:65 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет | нет |
| GET | api/google-calendar/auth/callback | GoogleCalendarController | Callback | backend/Controllers/GoogleCalendarController.cs:85 | публичный ([AllowAnonymous]) | да | docs/google-calendar-sync-guide.md |
| PUT | api/google-calendar/settings | GoogleCalendarController | UpdateSettings | backend/Controllers/GoogleCalendarController.cs:100 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/google-calendar/sync-now | GoogleCalendarController | SyncNow | backend/Controllers/GoogleCalendarController.cs:127 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/google-calendar/connection | GoogleCalendarController | Disconnect | backend/Controllers/GoogleCalendarController.cs:157 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/google-calendar/webhook | GoogleCalendarController | Webhook | backend/Controllers/GoogleCalendarController.cs:181 | публичный ([AllowAnonymous]) | да | нет |
| GET | api/[controller] | HealthController | Get | backend/Controllers/HealthController.cs:12 | публичный ([AllowAnonymous]) | да | docs/full-project-audit-2026-06-10.md |
| POST | api/[controller]/calc | InstallmentsController | Calc | backend/Controllers/InstallmentsController.cs:19 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Accounts)] | нет | нет |
| GET | api/internal-chat/threads | InternalChatController | GetThreads | backend/Controllers/InternalChatController.cs:74 | [Authorize] | да | docs/payplanner-storage-roadmap-2026-10-14.md |
| GET | api/internal-chat/people | InternalChatController | GetPeople | backend/Controllers/InternalChatController.cs:156 | [Authorize] | нет | нет |
| POST | api/internal-chat/channels | InternalChatController | CreateChannel | backend/Controllers/InternalChatController.cs:169 | [Authorize] | да | нет |
| PUT | api/internal-chat/threads/{threadId:int}/members | InternalChatController | UpdateChannelMembers | backend/Controllers/InternalChatController.cs:205 | [Authorize] | нет | нет |
| PUT | api/internal-chat/threads/{threadId:int}/task-guests | InternalChatController | UpdateTaskGuests | backend/Controllers/InternalChatController.cs:234 | [Authorize] | да | нет |
| PUT | api/internal-chat/threads/{threadId:int}/personal-state | InternalChatController | SetPersonalState | backend/Controllers/InternalChatController.cs:265 | [Authorize] | да | нет |
| POST | api/internal-chat/direct/{otherUserId:int} | InternalChatController | OpenDirect | backend/Controllers/InternalChatController.cs:283 | [Authorize] | да | нет |
| POST | api/internal-chat/tasks/{taskId:int} | InternalChatController | OpenTaskChat | backend/Controllers/InternalChatController.cs:350 | [Authorize] | да | нет |
| GET | api/internal-chat/threads/{threadId:int}/messages | InternalChatController | GetMessages | backend/Controllers/InternalChatController.cs:420 | [Authorize] | да | docs/payplanner-storage-roadmap-2026-10-14.md |
| POST | api/internal-chat/threads/{threadId:int}/messages | InternalChatController | SendMessage | backend/Controllers/InternalChatController.cs:494 | [Authorize] | да | docs/payplanner-storage-roadmap-2026-10-14.md |
| PUT | api/internal-chat/messages/{messageId:int} | InternalChatController | EditMessage | backend/Controllers/InternalChatController.cs:751 | [Authorize] | да | нет |
| DELETE | api/internal-chat/messages/{messageId:int} | InternalChatController | DeleteMessage | backend/Controllers/InternalChatController.cs:775 | [Authorize] | да | docs/storage-backup-snapshot-boundary-audit-2026-09-28.md |
| POST | api/internal-chat/messages/{messageId:int}/no-reply-reminder | InternalChatController | SetNoReplyReminder | backend/Controllers/InternalChatController.cs:828 | [Authorize] | да | нет |
| POST | api/internal-chat/messages/{messageId:int}/reactions | InternalChatController | ToggleReaction | backend/Controllers/InternalChatController.cs:859 | [Authorize] | да | нет |
| POST | api/internal-chat/messages/{messageId:int}/pin | InternalChatController | TogglePin | backend/Controllers/InternalChatController.cs:878 | [Authorize] | да | нет |
| POST | api/internal-chat/threads/{threadId:int}/read | InternalChatController | MarkRead | backend/Controllers/InternalChatController.cs:900 | [Authorize] | да | нет |
| PUT | api/internal-chat/threads/{threadId:int}/notification-mode | InternalChatController | SetNotificationMode | backend/Controllers/InternalChatController.cs:928 | [Authorize] | нет | нет |
| GET | api/internal-chat/attachments/{attachmentId:int} | InternalChatController | DownloadAttachment | backend/Controllers/InternalChatController.cs:944 | [Authorize] | да | нет |
| POST | api/internal-chat/messages/{messageId:int}/task | InternalChatController | CreateTaskFromMessage | backend/Controllers/InternalChatController.cs:975 | [Authorize] | да | нет |
| GET | threads/{threadId:int}/history-visibility | InternalChatController | GetHistoryVisibility | backend/Controllers/InternalChatHistoryController.cs:14 | не задано | да | нет |
| PUT | threads/{threadId:int}/history-visibility | InternalChatController | SetHistoryVisibility | backend/Controllers/InternalChatHistoryController.cs:38 | не задано | да | нет |
| GET | threads/{threadId:int}/sends/{clientRequestId:guid} | InternalChatController | GetSendStatus | backend/Controllers/InternalChatSendController.cs:12 | не задано | да | нет |
| GET | threads/{threadId:int}/sends | InternalChatController | GetPendingSends | backend/Controllers/InternalChatSendController.cs:23 | не задано | да | нет |
| POST | threads/{threadId:int}/sends/{clientRequestId:guid}/resume | InternalChatController | ResumeSend | backend/Controllers/InternalChatSendController.cs:40 | не задано | да | нет |
| GET | api/[controller]/reference-dictionaries | InvoicesController | GetReferenceDictionaries | backend/Controllers/InvoicesController.cs:76 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Accounts)] | да | нет |
| GET | api/[controller] | InvoicesController | Get | backend/Controllers/InvoicesController.cs:249 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/[controller]/summary | InvoicesController | GetSummary | backend/Controllers/InvoicesController.cs:379 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int} | InvoicesController | GetById | backend/Controllers/InvoicesController.cs:519 | [Authorize] | да | нет |
| POST | api/[controller]/repair-payment-state | InvoicesController | RepairPaymentState | backend/Controllers/InvoicesController.cs:551 | [Authorize] | да | нет |
| POST | api/[controller]/recalculate-payment-state | InvoicesController | RepairPaymentState | backend/Controllers/InvoicesController.cs:552 | [Authorize] | да | нет |
| POST | api/[controller]/backfill-expense-cases | InvoicesController | BackfillExpenseInvoiceCases | backend/Controllers/InvoicesController.cs:692 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/send-to-signing | InvoicesController | SendToSigning | backend/Controllers/InvoicesController.cs:719 | [Authorize] | да | нет |
| PUT | api/[controller]/{id:int}/seller | InvoicesController | SetSeller | backend/Controllers/InvoicesController.cs:753 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/mark-signed | InvoicesController | MarkSigned | backend/Controllers/InvoicesController.cs:787 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/mark-not-signed | InvoicesController | MarkNotSigned | backend/Controllers/InvoicesController.cs:816 | [Authorize] | нет | нет |
| POST | api/[controller]/{id:int}/workflow-status | InvoicesController | UpdateWorkflowStatus | backend/Controllers/InvoicesController.cs:845 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/cancel | InvoicesController | Cancel | backend/Controllers/InvoicesController.cs:956 | [Authorize] | да | docs/invoice-cancellation-credit-checkpoint-2026-10-31.md, docs/invoice-cancellation-history-checkpoint-2026-10-31.md |
| POST | api/[controller] | InvoicesController | Create | backend/Controllers/InvoicesController.cs:1463 | [Authorize] | да | docs/full-project-audit-2026-06-10.md, docs/payplanner-task-billing-bulk-plan-2026-10-23.md |
| PUT | api/[controller]/{id:int} | InvoicesController | Update | backend/Controllers/InvoicesController.cs:1702 | [Authorize] | да | нет |
| GET | api/[controller]/numbering-settings | InvoicesController | GetNumberingSettings | backend/Controllers/InvoicesController.cs:2240 | [Authorize] | да | нет |
| PUT | api/[controller]/numbering-settings | InvoicesController | UpsertNumberingSettings | backend/Controllers/InvoicesController.cs:2261 | [Authorize] | да | нет |
| GET | api/[controller]/next-number | InvoicesController | GetNextNumber | backend/Controllers/InvoicesController.cs:2289 | [Authorize] | да | нет |
| GET | api/[controller]/rules-settings | InvoicesController | GetRulesSettings | backend/Controllers/InvoicesController.cs:2317 | [Authorize] | да | нет |
| PUT | api/[controller]/rules-settings | InvoicesController | UpsertRulesSettings | backend/Controllers/InvoicesController.cs:2323 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings | InvoicesController | GetDocumentTemplateSettings | backend/Controllers/InvoicesController.cs:2344 | [Authorize] | да | нет |
| PUT | api/[controller]/document-template-settings | InvoicesController | UpsertDocumentTemplateSettings | backend/Controllers/InvoicesController.cs:2350 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings/history | InvoicesController | GetDocumentTemplateHistory | backend/Controllers/InvoicesController.cs:2390 | [Authorize] | да | нет |
| GET | api/[controller]/document-template-settings/history/{versionId:int} | InvoicesController | GetDocumentTemplateVersion | backend/Controllers/InvoicesController.cs:2401 | [Authorize] | нет | нет |
| POST | api/[controller]/document-template-settings/history/{versionId:int}/restore | InvoicesController | RestoreDocumentTemplateVersion | backend/Controllers/InvoicesController.cs:2425 | [Authorize] | да | нет |
| POST | api/[controller]/migrate-legacy-links | InvoicesController | MigrateLegacyInvoiceLinks | backend/Controllers/InvoicesController.cs:2476 | [Authorize] | да | нет |
| POST | api/[controller]/cleanup-counterparty-names | InvoicesController | CleanupCounterpartyNames | backend/Controllers/InvoicesController.cs:2693 | [Authorize] | да | нет |
| POST | api/[controller]/cleanup-orphan-invoices | InvoicesController | CleanupOrphanInvoices | backend/Controllers/InvoicesController.cs:2728 | [Authorize] | да | нет |
| POST | api/[controller]/{invoiceEntityId:int}/repair-legacy-installments | InvoicesController | RepairLegacyInstallments | backend/Controllers/InvoicesController.cs:2770 | [Authorize] | да | нет |
| DELETE | api/[controller]/{id:int} | InvoicesController | Delete | backend/Controllers/InvoicesController.cs:2932 | [Authorize] | да | нет |
| POST | api/[controller]/bulk-delete | InvoicesController | BulkDelete | backend/Controllers/InvoicesController.cs:2981 | [Authorize] | да | нет |
| POST | api/[controller]/change-responsible | InvoicesController | ChangeResponsible | backend/Controllers/InvoicesController.cs:3184 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/preview | InvoicesController | GetPreview | backend/Controllers/InvoicesController.cs:3265 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/image-print-defaults | InvoicesController | GetImagePrintDefaults | backend/Controllers/InvoicesController.cs:3290 | [Authorize] | да | нет |
| GET | api/[controller]/{id:int}/document | InvoicesController | GetDocument | backend/Controllers/InvoicesController.cs:3316 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/print | InvoicesController | PreparePrintDocument | backend/Controllers/InvoicesController.cs:3328 | [Authorize] | да | docs/payplanner-company-roadmap-2026-10-14.md |
| POST | api/[controller]/{id:int}/yandex-disk | InvoicesController | UploadDocumentToYandexDisk | backend/Controllers/InvoicesController.cs:3405 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/send-email | InvoicesController | SendInvoiceEmail | backend/Controllers/InvoicesController.cs:3564 | [Authorize] | да | нет |
| POST | api/storage/legacy-replicas | LegacyStorageMigrationController | Run | backend/Controllers/LegacyStorageMigrationController.cs:12 | [Authorize(Policy = "Admin")] | нет | docs/storage-resilience-runbook.md |
| POST | api/legal-entities/clean/address | LegalEntitiesController | CleanAddress | backend/Controllers/LegalEntitiesController.cs:908 | [Authorize] | да | нет |
| POST | api/legal-entities/clean/email | LegalEntitiesController | CleanEmail | backend/Controllers/LegalEntitiesController.cs:940 | [Authorize] | да | нет |
| POST | api/legal-entities/clean/phone | LegalEntitiesController | CleanPhone | backend/Controllers/LegalEntitiesController.cs:961 | [Authorize] | да | нет |
| POST | api/legal-entities | LegalEntitiesController | Create | backend/Controllers/LegalEntitiesController.cs:1083 | [Authorize] | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/development-company-profile-permissions-2026-10-23.md, docs/full-project-audit-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md |
| DELETE | api/legal-entities/{id:int} | LegalEntitiesController | Delete | backend/Controllers/LegalEntitiesController.cs:1287 | [Authorize] | да | docs/development-bank-import-verification-2026-10-16.md, docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/legal-entities/main-companies/settings | LegalEntitiesController | GetMainCompanySettings | backend/Controllers/LegalEntitiesController.cs:1387 | [Authorize] | да | нет |
| PUT | api/legal-entities/main-companies/settings | LegalEntitiesController | UpdateMainCompanySettings | backend/Controllers/LegalEntitiesController.cs:1422 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| GET | api/legal-entities/company-profile | LegalEntitiesController | GetCompanyProfile | backend/Controllers/LegalEntitiesController.cs:1500 | [Authorize] | да | docs/development-company-profile-permissions-2026-10-23.md |
| PUT | api/legal-entities/company-profile/main/{id:int} | LegalEntitiesController | SetCompanyProfileMain | backend/Controllers/LegalEntitiesController.cs:1528 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| POST | api/legal-entities/find-by-id | LegalEntitiesController | FindById | backend/Controllers/LegalEntitiesController.cs:1557 | [Authorize] | да | нет |
| GET | api/legal-entities | LegalEntitiesController | GetAll | backend/Controllers/LegalEntitiesController.cs:1612 | [Authorize] | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/development-company-profile-permissions-2026-10-23.md, docs/full-project-audit-2026-06-10.md, docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/legal-entities/paged | LegalEntitiesController | GetPaged | backend/Controllers/LegalEntitiesController.cs:1646 | [Authorize] | да | нет |
| GET | api/legal-entities/{id:int} | LegalEntitiesController | GetById | backend/Controllers/LegalEntitiesController.cs:2019 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/legal-entities/{id:int}/requisites-check | LegalEntitiesController | CheckRequisites | backend/Controllers/LegalEntitiesController.cs:2052 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/legal-entities/{id:int}/case-finance-summaries | LegalEntitiesController | GetCaseFinanceSummaries | backend/Controllers/LegalEntitiesController.cs:2100 | [Authorize] | да | нет |
| POST | api/legal-entities/suggest | LegalEntitiesController | Suggest | backend/Controllers/LegalEntitiesController.cs:2205 | [Authorize] | да | нет |
| PUT | api/legal-entities/{id:int} | LegalEntitiesController | Update | backend/Controllers/LegalEntitiesController.cs:2231 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| POST | api/legal-entities/migrate-legacy-addresses | LegalEntitiesController | MigrateLegacyAddresses | backend/Controllers/LegalEntitiesController.cs:2471 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| GET |  | LegalEntityBalancesController | GetSummary | backend/Controllers/LegalEntityBalancesController.cs:56 | не задано | да | нет |
| GET | transactions | LegalEntityBalancesController | GetTransactions | backend/Controllers/LegalEntityBalancesController.cs:77 | не задано | да | docs/bank-integrations-api-verification-2026-10-09.md, docs/delete-behavior-contract-2026-06-10.md |
| GET | applications/{requestId:guid} | LegalEntityBalancesController | GetApplication | backend/Controllers/LegalEntityBalancesController.cs:115 | не задано | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/balance-application-recovery-checkpoint-2026-10-31.md |
| POST | applications/{requestId:guid}/cancel | LegalEntityBalancesController | CancelApplication | backend/Controllers/LegalEntityBalancesController.cs:126 | не задано | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md |
| POST | applications/{requestId:guid}/apply | LegalEntityBalancesController | ApplyManaged | backend/Controllers/LegalEntityBalancesController.cs:137 | не задано | да | docs/balance-application-managed-route-checkpoint-2026-10-31.md |
| POST | apply | LegalEntityBalancesController | ApplyCredit | backend/Controllers/LegalEntityBalancesController.cs:147 | не задано | да | docs/balance-application-cancel-fence-checkpoint-2026-10-31.md, docs/balance-application-durable-flow-checkpoint-2026-10-31.md, docs/balance-application-managed-route-checkpoint-2026-10-31.md, docs/company-autofill-ownership-checkpoint-2026-11-01.md, docs/development-bank-import-verification-2026-10-16.md, docs/file-upload-limits-2026-06-10.md, docs/full-project-audit-2026-06-10.md, docs/payment-advance-history-checkpoint-2026-10-31.md, docs/payplanner-handoff-2026-10-21.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/payplanner-storage-roadmap-2026-10-14.md, docs/quality-gates.md, docs/release-verification-history-2026-10-06.md, docs/service-balance-credit-guard-checkpoint-2026-10-31.md, docs/storage-resilience-runbook.md, docs/unpushed-changes-map-2026-10-03.md |
| GET | api/max/link | MaxMessengerController | GetLink | backend/Controllers/MaxMessengerController.cs:22 | [Authorize] | да | docs/development-2026-10-15-max-profile.md, docs/handoff-2026-10-15.md, docs/payplanner-messenger-roadmap-2026-10-14.md |
| POST | api/max/link | MaxMessengerController | CreateLink | backend/Controllers/MaxMessengerController.cs:31 | [Authorize] | да | docs/development-2026-10-15-max-profile.md, docs/handoff-2026-10-15.md, docs/payplanner-messenger-roadmap-2026-10-14.md |
| DELETE | api/max/link | MaxMessengerController | OptOut | backend/Controllers/MaxMessengerController.cs:40 | [Authorize] | да | docs/development-2026-10-15-max-profile.md, docs/handoff-2026-10-15.md, docs/payplanner-messenger-roadmap-2026-10-14.md |
| POST | api/max/messages | MaxMessengerController | QueueMessage | backend/Controllers/MaxMessengerController.cs:49 | [Authorize]; [Authorize(Roles = "admin")] | нет | нет |
| GET | api/max/messages | MaxMessengerController | GetMessages | backend/Controllers/MaxMessengerController.cs:64 | [Authorize]; [Authorize(Roles = "admin")] | нет | нет |
| POST | api/max/messages/{publicId:guid}/retry | MaxMessengerController | RetryMessage | backend/Controllers/MaxMessengerController.cs:90 | [Authorize]; [Authorize(Roles = "admin")] | нет | нет |
| GET | api/max/correspondence | MaxMessengerController | GetCorrespondence | backend/Controllers/MaxMessengerController.cs:106 | [Authorize]; [Authorize(Roles = "admin")] | нет | нет |
| POST | api/max/correspondence/{publicId:guid}/route | MaxMessengerController | RouteCorrespondence | backend/Controllers/MaxMessengerController.cs:114 | [Authorize]; [Authorize(Roles = "admin")] | нет | нет |
| POST | api/integrations/max/webhook | MaxWebhookController | Receive | backend/Controllers/MaxMessengerController.cs:156 | публичный ([AllowAnonymous]) | да | docs/payplanner-messenger-roadmap-2026-10-14.md |
| GET | api/messenger-document-deliveries/configuration | MessengerDocumentDeliveriesController | GetConfiguration | backend/Controllers/MessengerDocumentDeliveriesController.cs:33 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/messenger-document-deliveries/recipients | MessengerDocumentDeliveriesController | GetRecipients | backend/Controllers/MessengerDocumentDeliveriesController.cs:55 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/messenger-document-deliveries/recipients | MessengerDocumentDeliveriesController | CreateRecipient | backend/Controllers/MessengerDocumentDeliveriesController.cs:80 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/messenger-document-deliveries/case-documents/{documentId:int}/prepare | MessengerDocumentDeliveriesController | PrepareCaseDocument | backend/Controllers/MessengerDocumentDeliveriesController.cs:122 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/messenger-document-deliveries/financial-documents/{documentType}/{documentId:int}/prepare | MessengerDocumentDeliveriesController | PrepareFinancialDocument | backend/Controllers/MessengerDocumentDeliveriesController.cs:164 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/messenger-document-deliveries | MessengerDocumentDeliveriesController | GetHistory | backend/Controllers/MessengerDocumentDeliveriesController.cs:226 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/messenger-document-deliveries/{deliveryId:int}/queue | MessengerDocumentDeliveriesController | Queue | backend/Controllers/MessengerDocumentDeliveriesController.cs:265 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/public/messenger-document-deliveries/{publicId:guid}/download | PublicMessengerDocumentDeliveriesController | Download | backend/Controllers/MessengerDocumentDeliveriesController.cs:386 | публичный ([AllowAnonymous]) | да | docs/public-access-link-security.md |
| POST | api/public/messenger-document-deliveries/{publicId:guid}/download | PublicMessengerDocumentDeliveriesController | DownloadSecurely | backend/Controllers/MessengerDocumentDeliveriesController.cs:406 | публичный ([AllowAnonymous]) | да | docs/public-access-link-security.md |
| POST | api/public/messenger-document-deliveries/callbacks/{provider} | PublicMessengerDocumentDeliveriesController | Callback | backend/Controllers/MessengerDocumentDeliveriesController.cs:418 | публичный ([AllowAnonymous]) | да | нет |
| GET | api/money-accounts | MoneyAccountsController | Get | backend/Controllers/MoneyAccountsController.cs:15 | [Authorize] | да | docs/deployment-manual-2026-10-21.md, docs/full-project-audit-2026-06-10.md |
| GET | api/money-accounts/{id:int} | MoneyAccountsController | GetById | backend/Controllers/MoneyAccountsController.cs:36 | [Authorize] | да | нет |
| POST | api/money-accounts | MoneyAccountsController | Create | backend/Controllers/MoneyAccountsController.cs:78 | [Authorize]; [Authorize(Policy = "Admin")] | да | docs/deployment-manual-2026-10-21.md, docs/full-project-audit-2026-06-10.md |
| PUT | api/money-accounts/{id:int} | MoneyAccountsController | Update | backend/Controllers/MoneyAccountsController.cs:111 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| DELETE | api/money-accounts/{id:int} | MoneyAccountsController | Delete | backend/Controllers/MoneyAccountsController.cs:170 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| GET | api/money-operations | MoneyOperationsController | Get | backend/Controllers/MoneyOperationsController.cs:99 | [Authorize] | да | docs/deployment-manual-2026-10-21.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md |
| GET | api/money-operations/paged | MoneyOperationsController | GetPaged | backend/Controllers/MoneyOperationsController.cs:139 | [Authorize] | да | docs/deployment-manual-2026-10-21.md |
| GET | api/money-operations/export.csv | MoneyOperationsController | ExportCsv | backend/Controllers/MoneyOperationsController.cs:345 | [Authorize] | да | нет |
| GET | api/money-operations/export.xls | MoneyOperationsController | ExportExcel | backend/Controllers/MoneyOperationsController.cs:395 | [Authorize] | да | нет |
| GET | api/money-operations/print | MoneyOperationsController | Print | backend/Controllers/MoneyOperationsController.cs:442 | [Authorize] | да | нет |
| GET | api/money-operations/export.pdf | MoneyOperationsController | ExportPdf | backend/Controllers/MoneyOperationsController.cs:489 | [Authorize] | да | нет |
| GET | api/money-operations/accounts | MoneyOperationsController | GetAccounts | backend/Controllers/MoneyOperationsController.cs:541 | [Authorize] | да | нет |
| GET | api/money-operations/account-summaries | MoneyOperationsController | GetAccountSummaries | backend/Controllers/MoneyOperationsController.cs:568 | [Authorize] | да | нет |
| GET | api/money-operations/daily-balances | MoneyOperationsController | GetDailyBalances | backend/Controllers/MoneyOperationsController.cs:644 | [Authorize] | да | нет |
| GET | api/money-operations/balance-reconciliation | MoneyOperationsController | GetBalanceReconciliationResults | backend/Controllers/MoneyOperationsController.cs:680 | [Authorize] | да | нет |
| POST | api/money-operations/balance-reconciliation/run | MoneyOperationsController | RunBalanceReconciliation | backend/Controllers/MoneyOperationsController.cs:747 | [Authorize] | да | нет |
| GET | api/money-operations/{id:int} | MoneyOperationsController | GetById | backend/Controllers/MoneyOperationsController.cs:832 | [Authorize] | да | docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md |
| GET | api/money-operations/{id:int}/invoice-candidates | MoneyOperationsController | GetInvoiceCandidates | backend/Controllers/MoneyOperationsController.cs:938 | [Authorize] | да | docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md |
| GET | api/money-operations/{id:int}/invoice-candidates/pages | MoneyOperationsController | GetInvoiceCandidatePage | backend/Controllers/MoneyOperationsController.cs:949 | [Authorize] | да | docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md |
| GET | api/money-operations/{id:int}/invoice-candidates/search | MoneyOperationsController | SearchInvoiceCandidates | backend/Controllers/MoneyOperationsController.cs:1165 | [Authorize] | да | docs/payplanner-bank-roadmap-2026-10-14.md |
| POST | api/money-operations/{id:int}/ignore | MoneyOperationsController | Ignore | backend/Controllers/MoneyOperationsController.cs:1170 | [Authorize] | да | нет |
| POST | api/money-operations/{id:int}/link-invoice | MoneyOperationsController | LinkInvoice | backend/Controllers/MoneyOperationsController.cs:1213 | [Authorize] | да | нет |
| POST | api/money-operations | MoneyOperationsController | Create | backend/Controllers/MoneyOperationsController.cs:1280 | [Authorize] | да | docs/deployment-manual-2026-10-21.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md |
| POST | api/money-operations/transfer | MoneyOperationsController | CreateTransfer | backend/Controllers/MoneyOperationsController.cs:1380 | [Authorize] | да | нет |
| POST | api/money-operations/bank-statement/import | MoneyOperationsController | ImportBankStatement | backend/Controllers/MoneyOperationsController.cs:1523 | [Authorize] | да | нет |
| POST | api/money-operations/bank-statement/auto-import/run | MoneyOperationsController | RunBankStatementAutoImport | backend/Controllers/MoneyOperationsController.cs:1623 | [Authorize] | да | нет |
| GET | api/money-operations/bank-statement/imports | MoneyOperationsController | GetBankStatementImports | backend/Controllers/MoneyOperationsController.cs:1728 | [Authorize] | да | нет |
| GET | api/money-operations/bank-statement/imports/{batchId:int} | MoneyOperationsController | GetBankStatementImport | backend/Controllers/MoneyOperationsController.cs:1781 | [Authorize] | да | нет |
| POST | api/money-operations/bank-statement/imports/{batchId:int}/rollback | MoneyOperationsController | RollbackBankStatementImport | backend/Controllers/MoneyOperationsController.cs:1913 | [Authorize] | да | нет |
| POST | api/money-operations/bank-statement/imports/{batchId:int}/reprocess | MoneyOperationsController | ReprocessBankStatementImport | backend/Controllers/MoneyOperationsController.cs:2016 | [Authorize] | да | нет |
| PUT | api/money-operations/{id:int} | MoneyOperationsController | Update | backend/Controllers/MoneyOperationsController.cs:2078 | [Authorize] | да | docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md |
| DELETE | api/money-operations/{id:int} | MoneyOperationsController | Delete | backend/Controllers/MoneyOperationsController.cs:2261 | [Authorize] | да | docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md |
| GET | api/navigation-settings | NavigationSettingsController | Get | backend/Controllers/NavigationSettingsController.cs:35 | [Authorize] | да | нет |
| PUT | api/navigation-settings | NavigationSettingsController | Update | backend/Controllers/NavigationSettingsController.cs:52 | [Authorize] | да | нет |
| GET | api/notifications/inbox/types | NotificationInboxController | GetTypes | backend/Controllers/NotificationInboxController.cs:36 | [Authorize] | да | нет |
| GET | api/notifications/inbox/unread-count | NotificationInboxController | GetUnreadCount | backend/Controllers/NotificationInboxController.cs:40 | [Authorize] | да | docs/release-open-verification-errors-2026-10-07.md |
| GET | api/notifications/inbox | NotificationInboxController | GetNotifications | backend/Controllers/NotificationInboxController.cs:67 | [Authorize] | да | docs/development-2026-10-15-max-profile.md, docs/development-2026-10-16-audit-write-budget.md, docs/development-release-preparation-2026-10-06.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/release-verification-history-2026-10-06.md |
| POST | api/notifications/inbox/read-all | NotificationInboxController | MarkAllRead | backend/Controllers/NotificationInboxController.cs:129 | [Authorize] | да | нет |
| POST | api/notifications/inbox/bulk/archive | NotificationInboxController | ArchiveSelected | backend/Controllers/NotificationInboxController.cs:180 | [Authorize] | да | нет |
| POST | api/notifications/inbox/bulk/unarchive | NotificationInboxController | UnarchiveSelected | backend/Controllers/NotificationInboxController.cs:208 | [Authorize] | да | нет |
| POST | api/notifications/inbox/bulk/delete | NotificationInboxController | DeleteSelected | backend/Controllers/NotificationInboxController.cs:238 | [Authorize] | да | нет |
| PATCH | api/notifications/inbox/{id:int}/read | NotificationInboxController | MarkRead | backend/Controllers/NotificationInboxController.cs:261 | [Authorize] | да | нет |
| PATCH | api/notifications/inbox/{id:int}/unread | NotificationInboxController | MarkUnread | backend/Controllers/NotificationInboxController.cs:275 | [Authorize] | да | нет |
| PATCH | api/notifications/inbox/{id:int}/archive | NotificationInboxController | Archive | backend/Controllers/NotificationInboxController.cs:291 | [Authorize] | да | нет |
| PATCH | api/notifications/inbox/{id:int}/unarchive | NotificationInboxController | Unarchive | backend/Controllers/NotificationInboxController.cs:311 | [Authorize] | да | нет |
| PATCH | api/notifications/inbox/{id:int}/snooze | NotificationInboxController | Snooze | backend/Controllers/NotificationInboxController.cs:326 | [Authorize] | да | нет |
| DELETE | api/notifications/inbox/{id:int} | NotificationInboxController | Delete | backend/Controllers/NotificationInboxController.cs:346 | [Authorize] | да | нет |
| GET | api/notifications/settings | NotificationsController | GetSettings | backend/Controllers/NotificationsController.cs:42 | [Authorize] | нет | docs/full-project-audit-2026-06-10.md |
| GET | api/notifications/channels | NotificationsController | GetChannels | backend/Controllers/NotificationsController.cs:49 | [Authorize] | нет | нет |
| PUT | api/notifications/settings/email | NotificationsController | UpdateEmailSettings | backend/Controllers/NotificationsController.cs:56 | [Authorize] | нет | нет |
| PUT | api/notifications/settings/telegram | NotificationsController | UpdateTelegramSettings | backend/Controllers/NotificationsController.cs:65 | [Authorize] | нет | нет |
| PUT | api/notifications/settings/sms | NotificationsController | UpdateSmsSettings | backend/Controllers/NotificationsController.cs:74 | [Authorize] | нет | нет |
| PUT | api/notifications/settings/max | NotificationsController | UpdateMaxSettings | backend/Controllers/NotificationsController.cs:83 | [Authorize] | нет | нет |
| GET | api/notifications/templates | NotificationsController | GetTemplates | backend/Controllers/NotificationsController.cs:92 | [Authorize] | нет | нет |
| PUT | api/notifications/templates/{key} | NotificationsController | UpdateTemplate | backend/Controllers/NotificationsController.cs:99 | [Authorize] | нет | нет |
| POST | api/notifications/templates/{key}/reset | NotificationsController | ResetTemplate | backend/Controllers/NotificationsController.cs:116 | [Authorize] | нет | нет |
| GET | api/notifications/role-preferences | NotificationsController | GetRolePreferences | backend/Controllers/NotificationsController.cs:130 | [Authorize] | нет | docs/task-critical-notifications-audit-2026-07-09.md |
| PUT | api/notifications/role-preferences/{roleId:int} | NotificationsController | UpdateRolePreferences | backend/Controllers/NotificationsController.cs:176 | [Authorize] | нет | нет |
| POST | api/notifications/senders | NotificationsController | CreateSender | backend/Controllers/NotificationsController.cs:250 | [Authorize] | нет | нет |
| PUT | api/notifications/senders/{id:int} | NotificationsController | UpdateSender | backend/Controllers/NotificationsController.cs:266 | [Authorize] | нет | нет |
| DELETE | api/notifications/senders/{id:int} | NotificationsController | DeleteSender | backend/Controllers/NotificationsController.cs:287 | [Authorize] | нет | нет |
| POST | api/notifications/senders/{id:int}/telegram/webhook/sync | NotificationsController | SyncTelegramWebhook | backend/Controllers/NotificationsController.cs:294 | [Authorize] | нет | нет |
| DELETE | api/notifications/senders/{id:int}/telegram/webhook | NotificationsController | DeleteTelegramWebhook | backend/Controllers/NotificationsController.cs:317 | [Authorize] | нет | нет |
| POST | api/notifications/senders/{id:int}/telegram/profile/sync | NotificationsController | SyncTelegramBotProfile | backend/Controllers/NotificationsController.cs:340 | [Authorize] | нет | нет |
| PUT | api/notifications/senders/{id:int}/telegram/profile | NotificationsController | ApplyTelegramBotProfile | backend/Controllers/NotificationsController.cs:363 | [Authorize] | нет | нет |
| GET | api/notifications/senders/{id:int}/telegram/photo | NotificationsController | GetTelegramBotProfilePhoto | backend/Controllers/NotificationsController.cs:389 | [Authorize] | нет | нет |
| POST | api/notifications/telegram/{senderId:int}/webhook | NotificationsController | TelegramWebhook | backend/Controllers/NotificationsController.cs:411 | публичный ([AllowAnonymous]) | да | нет |
| GET | api/notifications/deliveries | NotificationsController | GetDeliveries | backend/Controllers/NotificationsController.cs:434 | [Authorize] | нет | нет |
| POST | api/notifications/deliveries/{id:int}/retry | NotificationsController | RetryDelivery | backend/Controllers/NotificationsController.cs:476 | [Authorize] | нет | нет |
| POST | api/notifications/test | NotificationsController | SendTest | backend/Controllers/NotificationsController.cs:507 | [Authorize] | да | нет |
| POST | api/notifications/users/{id:int}/issue-password | NotificationsController | IssuePassword | backend/Controllers/NotificationsController.cs:569 | [Authorize] | нет | нет |
| GET | api/object-audit | ObjectAuditController | GetEvents | backend/Controllers/ObjectAuditController.cs:37 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/object-audit/{objectType}/{objectId:int} | ObjectAuditController | GetHistory | backend/Controllers/ObjectAuditController.cs:187 | [Authorize] | да | нет |
| GET | api/object-audit/deleted/{objectType} | ObjectAuditController | GetDeletedObjects | backend/Controllers/ObjectAuditController.cs:233 | [Authorize] | да | нет |
| POST | api/object-audit/{objectType}/{objectId:int}/restore | ObjectAuditController | RestoreObject | backend/Controllers/ObjectAuditController.cs:267 | [Authorize] | да | нет |
| GET | api/own-companies/snapshot-policy | OwnCompaniesController | GetSnapshotPolicy | backend/Controllers/OwnCompaniesController.cs:38 | [Authorize] | нет | нет |
| GET | api/own-companies/options | OwnCompaniesController | GetOptions | backend/Controllers/OwnCompaniesController.cs:42 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | reconciliation-party | OwnCompaniesController | GetReconciliationParty | backend/Controllers/OwnCompaniesController.ReconciliationParty.cs:18 | не задано | да | docs/development-company-profile-permissions-2026-10-23.md |
| GET | api/[controller] | PaymentsController | Get | backend/Controllers/PaymentsController.cs:487 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/[controller]/{id:int} | PaymentsController | GetById | backend/Controllers/PaymentsController.cs:520 | [Authorize] | да | нет |
| POST | api/[controller]/create-attempts | PaymentsController | RegisterCreateAttempt | backend/Controllers/PaymentsController.cs:547 | [Authorize] | да | нет |
| GET | api/[controller]/create-attempts/{attemptId:guid} | PaymentsController | GetCreateAttempt | backend/Controllers/PaymentsController.cs:575 | [Authorize] | да | нет |
| POST | api/[controller]/create-attempts/{attemptId:guid}/cancel | PaymentsController | CancelCreateAttempt | backend/Controllers/PaymentsController.cs:590 | [Authorize] | да | нет |
| POST | api/[controller]/create-attempts/{attemptId:guid}/commit | PaymentsController | CommitCreateAttempt | backend/Controllers/PaymentsController.cs:622 | [Authorize] | да | нет |
| POST | api/[controller] | PaymentsController | Create | backend/Controllers/PaymentsController.cs:804 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/[controller]/{id:int} | PaymentsController | Update | backend/Controllers/PaymentsController.cs:941 | [Authorize] | да | docs/advance-allocation-recovery-checkpoint-2026-10-31.md |
| POST | api/[controller]/{id:int}/status | PaymentsController | UpdateStatus | backend/Controllers/PaymentsController.cs:1201 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/advance-review/accept | PaymentsController | AcceptAdvance | backend/Controllers/PaymentsController.cs:1322 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/advance-review/ignore | PaymentsController | IgnoreAdvance | backend/Controllers/PaymentsController.cs:1357 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/advance-review/refund | PaymentsController | RefundAdvance | backend/Controllers/PaymentsController.cs:1390 | [Authorize] | да | нет |
| GET | api/[controller]/advance-review/candidates | PaymentsController | GetAdvanceReviewCandidates | backend/Controllers/PaymentsController.cs:1443 | [Authorize] | да | нет |
| GET | api/[controller]/advance-review/diagnostics | PaymentsController | GetAdvanceReviewDiagnostics | backend/Controllers/PaymentsController.cs:1502 | [Authorize] | да | нет |
| POST | api/[controller]/advance-review/backfill | PaymentsController | BackfillAdvanceReviewCandidates | backend/Controllers/PaymentsController.cs:1697 | [Authorize] | да | нет |
| POST | api/[controller]/advance-review/backfill/rollback | PaymentsController | RollbackAdvanceReviewBackfill | backend/Controllers/PaymentsController.cs:1791 | [Authorize] | да | нет |
| PUT | api/[controller]/{id:int}/invoice-managed | PaymentsController | UpdateInvoiceManaged | backend/Controllers/PaymentsController.cs:1881 | [Authorize] | да | нет |
| GET | api/[controller]/partial-remainder-candidates | PaymentsController | GetPartialRemainderCandidates | backend/Controllers/PaymentsController.cs:2091 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/partial-remainder/from-existing | PaymentsController | TransferExistingPartialRemainder | backend/Controllers/PaymentsController.cs:2219 | [Authorize] | да | нет |
| PUT | api/[controller]/{id:int}/partial-remainder | PaymentsController | TransferPartialRemainder | backend/Controllers/PaymentsController.cs:2290 | [Authorize] | да | нет |
| POST | api/[controller]/{id:int}/partial-remainder/rollback | PaymentsController | RollbackPartialRemainderTransfer | backend/Controllers/PaymentsController.cs:2594 | [Authorize] | да | нет |
| PUT | api/[controller]/{id:int}/partial-remainder/adjust | PaymentsController | AdjustPartialRemainderTransfer | backend/Controllers/PaymentsController.cs:2652 | [Authorize] | да | нет |
| POST | api/[controller]/repair-invoice-links | PaymentsController | RepairInvoiceLinks | backend/Controllers/PaymentsController.cs:4140 | [Authorize] | да | нет |
| DELETE | api/[controller]/{id:int} | PaymentsController | Delete | backend/Controllers/PaymentsController.cs:5115 | [Authorize] | да | нет |
| GET |  | PowerOfAttorneyController | Get | backend/Controllers/PowerOfAttorneyController.cs:24 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет | нет |
| POST |  | PowerOfAttorneyController | Create | backend/Controllers/PowerOfAttorneyController.cs:32 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)]; [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Create)] | нет | нет |
| PUT | {id:int} | PowerOfAttorneyController | Update | backend/Controllers/PowerOfAttorneyController.cs:55 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)]; [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Edit)] | нет | docs/bank-integrations-api-verification-2026-10-09.md, docs/case-access-query-optimization.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md, docs/case-publication-queue-release-note-draft-2026-10-01.md, docs/company-bank-binding-checkpoint-2026-11-01.md, docs/crm-legal-module-gap-report-2026-05-25.md, docs/development-act-source-access-2026-10-24.md, docs/development-max-task-notifications-2026-10-23.md, docs/development-pause-plan-2026-09-30.md, docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md, docs/financial-invoice-cancellation-current-state-2026-07-08.md, docs/full-project-audit-2026-06-10.md, docs/invoice-cancellation-credit-checkpoint-2026-10-31.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-access-security-roadmap-2026-10-14.md, docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/storage-resilience-runbook.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/task-critical-events-matrix-2026-07-09.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-result-approval-current-state-2026-07-09.md |
| DELETE | {id:int} | PowerOfAttorneyController | Delete | backend/Controllers/PowerOfAttorneyController.cs:74 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)]; [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Delete)] | нет | docs/bank-integrations-api-verification-2026-10-09.md, docs/case-access-query-optimization.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md, docs/case-publication-queue-release-note-draft-2026-10-01.md, docs/company-bank-binding-checkpoint-2026-11-01.md, docs/crm-legal-module-gap-report-2026-05-25.md, docs/development-act-source-access-2026-10-24.md, docs/development-max-task-notifications-2026-10-23.md, docs/development-pause-plan-2026-09-30.md, docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md, docs/financial-invoice-cancellation-current-state-2026-07-08.md, docs/full-project-audit-2026-06-10.md, docs/invoice-cancellation-credit-checkpoint-2026-10-31.md, docs/pay-33-36-implementation-log-2026-10-09.md, docs/payplanner-access-security-roadmap-2026-10-14.md, docs/payplanner-bank-roadmap-2026-10-14.md, docs/payplanner-company-roadmap-2026-10-14.md, docs/payplanner-release-roadmap-2026-10-14.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/storage-resilience-runbook.md, docs/storage-s3-backup-audit-2026-09-22.md, docs/task-critical-events-matrix-2026-07-09.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-result-approval-current-state-2026-07-09.md |
| GET | api/profile | ProfileController | GetProfile | backend/Controllers/ProfileController.cs:46 | [Authorize] | нет | docs/development-release-preparation-2026-10-06.md, docs/full-project-audit-2026-06-10.md, docs/release-verification-history-2026-10-06.md, docs/storage-resilience-runbook.md |
| GET | api/profile/contractor-balance | ProfileController | GetContractorBalance | backend/Controllers/ProfileController.cs:59 | [Authorize] | да | нет |
| PUT | api/profile | ProfileController | UpdateProfile | backend/Controllers/ProfileController.cs:112 | [Authorize] | нет | docs/development-release-preparation-2026-10-06.md, docs/full-project-audit-2026-06-10.md, docs/release-verification-history-2026-10-06.md, docs/storage-resilience-runbook.md |
| POST | api/profile/password | ProfileController | ChangePassword | backend/Controllers/ProfileController.cs:136 | [Authorize] | нет | нет |
| POST | api/profile/photo | ProfileController | UploadProfilePhoto | backend/Controllers/ProfileController.cs:173 | [Authorize] | да | docs/file-upload-limits-2026-06-10.md, docs/storage-resilience-runbook.md |
| DELETE | api/profile/external/{provider} | ProfileController | DetachExternalIdentity | backend/Controllers/ProfileController.cs:280 | [Authorize] | нет | нет |
| POST | api/public/work-order-approvals/{publicId:guid}/open | PublicWorkOrderApprovalController | Open | backend/Controllers/PublicWorkOrderApprovalController.cs:23 | публичный ([AllowAnonymous]) | нет | нет |
| POST | api/public/work-order-approvals/{publicId:guid}/decision | PublicWorkOrderApprovalController | Decide | backend/Controllers/PublicWorkOrderApprovalController.cs:40 | публичный ([AllowAnonymous]) | нет | нет |
| POST | api/public/work-order-approvals/{publicId:guid}/attachments/{attachmentPublicId:guid}/grant | PublicWorkOrderApprovalController | CreateAttachmentGrant | backend/Controllers/PublicWorkOrderApprovalController.cs:67 | публичный ([AllowAnonymous]) | нет | нет |
| POST | api/public/work-order-approvals/{publicId:guid}/attachments/{attachmentPublicId:guid}/download | PublicWorkOrderApprovalController | DownloadAttachment | backend/Controllers/PublicWorkOrderApprovalController.cs:89 | публичный ([AllowAnonymous]) | нет | нет |
| GET | api/notifications/inbox/push/public-key | PushSubscriptionsController | GetPublicKey | backend/Controllers/PushSubscriptionsController.cs:29 | [Authorize] | нет | нет |
| GET | api/notifications/inbox/push/subscriptions | PushSubscriptionsController | GetSubscriptions | backend/Controllers/PushSubscriptionsController.cs:41 | [Authorize] | да | нет |
| GET | api/notifications/inbox/push/diagnostics | PushSubscriptionsController | GetDiagnostics | backend/Controllers/PushSubscriptionsController.cs:64 | [Authorize] | да | нет |
| POST | api/notifications/inbox/push/subscriptions | PushSubscriptionsController | UpsertSubscription | backend/Controllers/PushSubscriptionsController.cs:137 | [Authorize] | да | нет |
| DELETE | api/notifications/inbox/push/subscriptions/{id:int} | PushSubscriptionsController | RevokeSubscriptionById | backend/Controllers/PushSubscriptionsController.cs:190 | [Authorize] | да | нет |
| POST | api/notifications/inbox/push/subscriptions/revoke | PushSubscriptionsController | RevokeSubscriptionByEndpoint | backend/Controllers/PushSubscriptionsController.cs:211 | [Authorize] | нет | нет |
| GET | api/reports/legal-work | ReportsController | LegalWork | backend/Controllers/ReportsController.cs:203 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/legal-work.csv | ReportsController | ExportLegalWorkCsv | backend/Controllers/ReportsController.cs:240 | [Authorize] | да | нет |
| GET | api/reports/legal-work.xls | ReportsController | ExportLegalWorkExcel | backend/Controllers/ReportsController.cs:262 | [Authorize] | да | нет |
| GET | api/reports/legal-work.pdf | ReportsController | ExportLegalWorkPdf | backend/Controllers/ReportsController.cs:285 | [Authorize] | да | нет |
| GET | api/reports/legal-work.print | ReportsController | PrintLegalWork | backend/Controllers/ReportsController.cs:310 | [Authorize] | да | нет |
| GET | api/reports/management.xls | ReportsController | ExportManagementReportExcel | backend/Controllers/ReportsController.cs:329 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/management.csv | ReportsController | ExportManagementReportCsv | backend/Controllers/ReportsController.cs:389 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/management.pdf | ReportsController | ExportManagementReportPdf | backend/Controllers/ReportsController.cs:449 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/management.print | ReportsController | PrintManagementReport | backend/Controllers/ReportsController.cs:512 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/case-finance | ReportsController | CaseFinance | backend/Controllers/ReportsController.cs:569 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/case-success | ReportsController | CaseSuccess | backend/Controllers/ReportsController.cs:606 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/case-activity | ReportsController | CaseActivity | backend/Controllers/ReportsController.cs:1288 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/current-cases | ReportsController | CurrentCases | backend/Controllers/ReportsController.cs:1327 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/tasks | ReportsController | Tasks | backend/Controllers/ReportsController.cs:1371 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/lawyer-workload | ReportsController | LawyerWorkload | backend/Controllers/ReportsController.cs:1408 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/overdue-deadlines | ReportsController | OverdueDeadlines | backend/Controllers/ReportsController.cs:1447 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/cash-flow | ReportsController | CashFlow | backend/Controllers/ReportsController.cs:1484 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/cash-flow-forecast | ReportsController | CashFlowForecast | backend/Controllers/ReportsController.cs:1525 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/categories | ReportsController | Categories | backend/Controllers/ReportsController.cs:1564 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/counterparties | ReportsController | Counterparties | backend/Controllers/ReportsController.cs:1585 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/projects | ReportsController | Projects | backend/Controllers/ReportsController.cs:1606 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/directions | ReportsController | Directions | backend/Controllers/ReportsController.cs:1627 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/sales-points | ReportsController | SalesPoints | backend/Controllers/ReportsController.cs:1648 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/vat | ReportsController | Vat | backend/Controllers/ReportsController.cs:1669 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/plan-fact | ReportsController | PlanFact | backend/Controllers/ReportsController.cs:1690 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/advances | ReportsController | Advances | backend/Controllers/ReportsController.cs:1711 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/payment-priorities | ReportsController | PaymentPriorities | backend/Controllers/ReportsController.cs:1732 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/paid-accounts | ReportsController | PaidAccounts | backend/Controllers/ReportsController.cs:1753 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/paid-invoices | ReportsController | PaidInvoices | backend/Controllers/ReportsController.cs:1774 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/invoice-statuses | ReportsController | InvoiceStatuses | backend/Controllers/ReportsController.cs:1795 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/act-statuses | ReportsController | ActStatuses | backend/Controllers/ReportsController.cs:1816 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/contract-statuses | ReportsController | ContractStatuses | backend/Controllers/ReportsController.cs:1837 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/payment-orders | ReportsController | PaymentOrders | backend/Controllers/ReportsController.cs:1858 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/payment-purposes | ReportsController | PaymentPurposes | backend/Controllers/ReportsController.cs:1879 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/match-statuses | ReportsController | MatchStatuses | backend/Controllers/ReportsController.cs:1900 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/journal-statuses | ReportsController | JournalStatuses | backend/Controllers/ReportsController.cs:1921 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/currencies | ReportsController | Currencies | backend/Controllers/ReportsController.cs:1942 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/cash-orders | ReportsController | CashOrders | backend/Controllers/ReportsController.cs:1963 | [Authorize] | да | docs/financial-cash-access-manual-acceptance-checklist-2026-06-16.md, docs/report-inventory-2026-08-09.md |
| GET | api/reports/bank-statement-imports | ReportsController | BankStatementImports | backend/Controllers/ReportsController.cs:1997 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/reports/spreadsheet-imports | ReportsController | SpreadsheetImports | backend/Controllers/ReportsController.cs:2043 | [Authorize] | да | docs/report-inventory-2026-08-09.md |
| GET | api/role-permissions/audit | RolePermissionsController | GetAudit | backend/Controllers/RolePermissionsController.cs:42 | [Authorize] | да | docs/crm-legal-module-release-runbook.md |
| GET | api/role-permissions/audit.csv | RolePermissionsController | ExportAuditCsv | backend/Controllers/RolePermissionsController.cs:75 | [Authorize] | да | docs/crm-legal-module-release-runbook.md |
| GET | api/role-permissions/{roleId:int} | RolePermissionsController | Get | backend/Controllers/RolePermissionsController.cs:119 | [Authorize] | да | нет |
| GET | api/role-permissions/calendar-users | RolePermissionsController | GetCalendarUsers | backend/Controllers/RolePermissionsController.cs:158 | [Authorize] | нет | нет |
| PUT | api/role-permissions/{roleId:int} | RolePermissionsController | Update | backend/Controllers/RolePermissionsController.cs:186 | [Authorize] | да | нет |
| DELETE | api/role-permissions/{roleId:int} | RolePermissionsController | Reset | backend/Controllers/RolePermissionsController.cs:290 | [Authorize] | да | нет |
| GET | api/roles | RolesController | GetRoles | backend/Controllers/RolesController.cs:29 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/roles/paged | RolesController | GetRolesPaged | backend/Controllers/RolesController.cs:44 | [Authorize] | нет | нет |
| GET | api/roles/{id:int} | RolesController | GetRole | backend/Controllers/RolesController.cs:78 | [Authorize] | нет | нет |
| POST | api/roles | RolesController | CreateRole | backend/Controllers/RolesController.cs:94 | [Authorize] | нет | docs/full-project-audit-2026-06-10.md |
| POST | api/roles/{id:int}/copy | RolesController | CopyRole | backend/Controllers/RolesController.cs:142 | [Authorize] | нет | нет |
| PUT | api/roles/{id:int} | RolesController | UpdateRole | backend/Controllers/RolesController.cs:197 | [Authorize] | нет | нет |
| DELETE | api/roles/{id:int} | RolesController | DeleteRole | backend/Controllers/RolesController.cs:241 | [Authorize] | нет | нет |
| GET | api/s3-storages | S3StorageController | GetOverview | backend/Controllers/S3StorageController.cs:30 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| POST | api/s3-storages | S3StorageController | UpsertConnection | backend/Controllers/S3StorageController.cs:39 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| DELETE | api/s3-storages/{id} | S3StorageController | DeleteConnection | backend/Controllers/S3StorageController.cs:60 | [Authorize] | нет | нет |
| POST | api/s3-storages/{id}/connection-test | S3StorageController | TestConnection | backend/Controllers/S3StorageController.cs:82 | [Authorize] | да | нет |
| GET | api/s3-storages/{id}/objects | S3StorageController | ListObjects | backend/Controllers/S3StorageController.cs:105 | [Authorize] | нет | нет |
| POST | api/s3-storages/{id}/folders | S3StorageController | CreateFolder | backend/Controllers/S3StorageController.cs:119 | [Authorize] | да | нет |
| POST | api/s3-storages/{id}/upload | S3StorageController | Upload | backend/Controllers/S3StorageController.cs:136 | [Authorize] | да | docs/file-upload-limits-2026-06-10.md |
| GET | api/s3-storages/{id}/download | S3StorageController | Download | backend/Controllers/S3StorageController.cs:170 | [Authorize] | да | нет |
| DELETE | api/s3-storages/{id}/objects | S3StorageController | DeleteObject | backend/Controllers/S3StorageController.cs:195 | [Authorize] | нет | нет |
| POST | api/s3-storages/{id}/copy | S3StorageController | CopyObject | backend/Controllers/S3StorageController.cs:213 | [Authorize] | нет | нет |
| POST | api/s3-storages/{id}/move | S3StorageController | MoveObject | backend/Controllers/S3StorageController.cs:230 | [Authorize] | нет | нет |
| GET | api/s3-storages/{id}/text | S3StorageController | GetTextFile | backend/Controllers/S3StorageController.cs:247 | [Authorize] | нет | нет |
| PUT | api/s3-storages/{id}/text | S3StorageController | SaveTextFile | backend/Controllers/S3StorageController.cs:259 | [Authorize] | нет | нет |
| GET | api/requests/statuses | SalesRequestsController | Statuses | backend/Controllers/SalesRequestsController.cs:143 | [Authorize] | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| POST | api/requests/statuses | SalesRequestsController | CreateStatus | backend/Controllers/SalesRequestsController.cs:158 | [Authorize] | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| PUT | api/requests/statuses/{id:int} | SalesRequestsController | UpdateStatus | backend/Controllers/SalesRequestsController.cs:162 | [Authorize] | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| PUT | api/requests/statuses/{id:int}/rules | SalesRequestsController | ReplaceRules | backend/Controllers/SalesRequestsController.cs:166 | [Authorize] | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/requests | SalesRequestsController | List | backend/Controllers/SalesRequestsController.cs:206 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/requests/{id:int} | SalesRequestsController | Get | backend/Controllers/SalesRequestsController.cs:238 | [Authorize] | да | нет |
| GET | api/requests/{id:int}/invoice-candidates | SalesRequestsController | InvoiceCandidates | backend/Controllers/SalesRequestsController.cs:259 | [Authorize] | да | нет |
| GET | api/requests/{id:int}/service-invoice | SalesRequestsController | GetServiceInvoice | backend/Controllers/SalesRequestsController.cs:297 | [Authorize] | да | нет |
| GET | api/requests/clients/search | SalesRequestsController | SearchClients | backend/Controllers/SalesRequestsController.cs:326 | [Authorize] | да | нет |
| GET | api/requests/clients/{clientId:int}/cases | SalesRequestsController | ClientCases | backend/Controllers/SalesRequestsController.cs:350 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| POST | api/requests | SalesRequestsController | Create | backend/Controllers/SalesRequestsController.cs:379 | [Authorize] | да | docs/pay-33-36-implementation-log-2026-10-09.md |
| PUT | api/requests/{id:int} | SalesRequestsController | Update | backend/Controllers/SalesRequestsController.cs:393 | [Authorize] | нет | нет |
| PUT | api/requests/{id:int}/service-invoice | SalesRequestsController | SetServiceInvoice | backend/Controllers/SalesRequestsController.cs:405 | [Authorize] | да | нет |
| POST | api/requests/{id:int}/transition | SalesRequestsController | Transition | backend/Controllers/SalesRequestsController.cs:413 | [Authorize] | нет | нет |
| POST | api/requests/{id:int}/tasks | SalesRequestsController | CreateTask | backend/Controllers/SalesRequestsController.cs:425 | [Authorize] | нет | нет |
| GET | api/requests/journal | SalesRequestsController | Journal | backend/Controllers/SalesRequestsController.cs:439 | [Authorize] | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/requests/report | SalesRequestsController | Report | backend/Controllers/SalesRequestsController.cs:466 | [Authorize] | да | нет |
| GET | api/[controller]/month | StatsController | Month | backend/Controllers/StatsController.cs:21 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | да | нет |
| GET | api/sticky-notes/boards | StickyNotesController | GetBoards | backend/Controllers/StickyNotesController.cs:63 | [Authorize] | да | нет |
| POST | api/sticky-notes/boards | StickyNotesController | CreateBoard | backend/Controllers/StickyNotesController.cs:100 | [Authorize] | нет | нет |
| PUT | api/sticky-notes/boards/{boardId:int} | StickyNotesController | UpdateBoard | backend/Controllers/StickyNotesController.cs:127 | [Authorize] | да | нет |
| DELETE | api/sticky-notes/boards/{boardId:int} | StickyNotesController | DeleteBoard | backend/Controllers/StickyNotesController.cs:158 | [Authorize] | да | нет |
| GET | api/sticky-notes/boards/{boardId:int}/members | StickyNotesController | GetBoardMembers | backend/Controllers/StickyNotesController.cs:186 | [Authorize] | нет | нет |
| GET | api/sticky-notes/member-candidates | StickyNotesController | GetMemberCandidates | backend/Controllers/StickyNotesController.cs:213 | [Authorize] | да | нет |
| PUT | api/sticky-notes/boards/{boardId:int}/members/{userId:int} | StickyNotesController | UpsertBoardMember | backend/Controllers/StickyNotesController.cs:245 | [Authorize] | да | нет |
| DELETE | api/sticky-notes/boards/{boardId:int}/members/{userId:int} | StickyNotesController | DeleteBoardMember | backend/Controllers/StickyNotesController.cs:316 | [Authorize] | да | нет |
| GET | api/sticky-notes/boards/{boardId:int}/notes | StickyNotesController | GetNotes | backend/Controllers/StickyNotesController.cs:355 | [Authorize] | да | нет |
| POST | api/sticky-notes/boards/{boardId:int}/notes | StickyNotesController | CreateNote | backend/Controllers/StickyNotesController.cs:417 | [Authorize] | да | нет |
| PUT | api/sticky-notes/notes/{noteId:int} | StickyNotesController | UpdateNote | backend/Controllers/StickyNotesController.cs:488 | [Authorize] | да | нет |
| PATCH | api/sticky-notes/notes/{noteId:int}/layout | StickyNotesController | UpdateNoteLayout | backend/Controllers/StickyNotesController.cs:586 | [Authorize] | да | нет |
| DELETE | api/sticky-notes/notes/{noteId:int} | StickyNotesController | DeleteNote | backend/Controllers/StickyNotesController.cs:635 | [Authorize] | да | нет |
| POST | api/sticky-notes/notes/{noteId:int}/restore | StickyNotesController | RestoreNote | backend/Controllers/StickyNotesController.cs:681 | [Authorize] | да | нет |
| GET | api/storage/objects | StorageAdminController | GetObjects | backend/Controllers/StorageAdminController.cs:25 | [Authorize(Policy = "Admin")] | нет | docs/payplanner-storage-roadmap-2026-10-14.md |
| GET | api/storage/objects/{id:guid}/content | StorageAdminController | DownloadObject | backend/Controllers/StorageAdminController.cs:63 | [Authorize(Policy = "Admin")] | нет | нет |
| DELETE | api/storage/objects/{id:guid} | StorageAdminController | DeleteObject | backend/Controllers/StorageAdminController.cs:75 | [Authorize(Policy = "Admin")] | нет | docs/payplanner-storage-roadmap-2026-10-14.md |
| GET | api/storage/policy/explain | StorageAdminController | ExplainPolicy | backend/Controllers/StorageAdminController.cs:81 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/storage/providers | StorageAdminController | GetProviders | backend/Controllers/StorageAdminController.cs:85 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/storage/operations | StorageAdminController | GetOperations | backend/Controllers/StorageAdminController.cs:91 | [Authorize(Policy = "Admin")] | нет | docs/payplanner-storage-roadmap-2026-10-14.md |
| POST | api/storage/operations/{id:guid}/retry | StorageAdminController | RetryOperation | backend/Controllers/StorageAdminController.cs:129 | [Authorize(Policy = "Admin")] | да | нет |
| GET | api/storage/operations/case-documents | StorageAdminController | GetCasePublicationOperations | backend/Controllers/StorageAdminController.cs:159 | [Authorize(Policy = "Admin")] | да | docs/payplanner-storage-roadmap-2026-10-14.md |
| POST | api/storage/reconcile | StorageAdminController | Reconcile | backend/Controllers/StorageAdminController.cs:175 | [Authorize(Policy = "Admin")] | нет | docs/storage-resilience-runbook.md |
| POST | api/storage/capacity/stages/reconcile | StorageAdminController | ReconcileStages | backend/Controllers/StorageAdminController.cs:181 | [Authorize(Policy = "Admin")] | да | docs/storage-resilience-runbook.md |
| POST | api/storage/capacity/stages/markers/reconcile | StorageAdminController | ReconcileStageMarkers | backend/Controllers/StorageAdminController.cs:202 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/storage/legacy-backfill | StorageAdminController | BackfillLegacyStorage | backend/Controllers/StorageAdminController.cs:224 | [Authorize(Policy = "Admin")] | нет | docs/storage-resilience-runbook.md |
| GET | api/storage/health | StorageAdminController | GetHealth | backend/Controllers/StorageAdminController.cs:239 | [Authorize(Policy = "Admin")] | да | docs/storage-resilience-runbook.md |
| GET | api/system/jobs/diagnostics | SystemJobsController | GetDiagnostics | backend/Controllers/SystemJobsController.cs:108 | [Authorize(Policy = "Admin")] | да | нет |
| POST | api/system/jobs/{jobKey}/run | SystemJobsController | RunJob | backend/Controllers/SystemJobsController.cs:190 | [Authorize(Policy = "Admin")] | да | docs/development-bank-import-verification-2026-10-16.md |
| GET | api/tasks | TasksController | GetTasks | backend/Controllers/TasksController.cs:193 | [Authorize] | да | docs/development-release-preparation-2026-10-06.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/release-verification-history-2026-10-06.md, docs/storage-resilience-runbook.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-journal-query-optimization.md, docs/task-result-approval-current-state-2026-07-09.md, docs/task-template-current-state-2026-07-09.md |
| GET | api/tasks/notifications | TasksController | GetNotifications | backend/Controllers/TasksController.cs:282 | [Authorize] | да | нет |
| GET | api/tasks/overdue-summary | TasksController | GetOverdueSummary | backend/Controllers/TasksController.cs:349 | [Authorize] | да | нет |
| GET | api/tasks/assignees | TasksController | GetAssignees | backend/Controllers/TasksController.cs:375 | [Authorize] | да | нет |
| GET | api/tasks/settings | TasksController | GetSettings | backend/Controllers/TasksController.cs:428 | [Authorize] | да | нет |
| PUT | api/tasks/settings | TasksController | UpdateSettings | backend/Controllers/TasksController.cs:445 | [Authorize] | да | нет |
| GET | api/tasks/automation/case-control/diagnostics | TasksController | GetCaseControlAutomationDiagnostics | backend/Controllers/TasksController.cs:617 | [Authorize] | да | нет |
| GET | api/tasks/summary | TasksController | GetTaskListSummary | backend/Controllers/TasksController.cs:748 | [Authorize] | да | docs/development-release-preparation-2026-10-06.md, docs/release-verification-history-2026-10-06.md, docs/task-journal-query-optimization.md |
| GET | api/tasks/automation/case-control/preview | TasksController | PreviewCaseControlAutomation | backend/Controllers/TasksController.cs:895 | [Authorize] | да | нет |
| GET | api/tasks/automation/case-control/preview/details | TasksController | GetCaseControlAutomationPreviewDetails | backend/Controllers/TasksController.cs:970 | [Authorize] | да | нет |
| GET | api/tasks/calendar.ics | TasksController | ExportCalendar | backend/Controllers/TasksController.cs:1139 | [Authorize] | да | нет |
| GET | api/tasks/planning-report.{format} | TasksController | ExportPlanningReport | backend/Controllers/TasksController.cs:1229 | [Authorize] | да | docs/payplanner-task-workflow-roadmap-2026-10-14.md |
| GET | api/tasks/automation/case-candidates | TasksController | GetAutomationCaseCandidates | backend/Controllers/TasksController.cs:1330 | [Authorize] | да | docs/development-release-preparation-2026-10-06.md, docs/release-verification-history-2026-10-06.md, docs/task-template-current-state-2026-07-09.md |
| GET | api/tasks/automation/case-candidates/summary | TasksController | GetAutomationCaseCandidatesSummary | backend/Controllers/TasksController.cs:1572 | [Authorize] | да | нет |
| POST | api/tasks/automation/case-candidates/{clientCaseId:int}/task | TasksController | CreateAutomationTaskForCase | backend/Controllers/TasksController.cs:1709 | [Authorize] | да | docs/task-template-current-state-2026-07-09.md |
| POST | api/tasks/automation/case-candidates/tasks | TasksController | CreateAutomationTasksForCandidates | backend/Controllers/TasksController.cs:1876 | [Authorize] | да | docs/task-template-current-state-2026-07-09.md |
| GET | api/tasks/templates | TasksController | GetTemplates | backend/Controllers/TasksController.cs:2059 | [Authorize] | да | нет |
| GET | api/tasks/templates/{id:int} | TasksController | GetTemplate | backend/Controllers/TasksController.cs:2144 | [Authorize] | да | нет |
| POST | api/tasks/templates | TasksController | CreateTemplate | backend/Controllers/TasksController.cs:2165 | [Authorize] | да | нет |
| PUT | api/tasks/templates/{id:int} | TasksController | UpdateTemplate | backend/Controllers/TasksController.cs:2230 | [Authorize] | да | нет |
| PATCH | api/tasks/templates/{id:int}/active | TasksController | UpdateTemplateActive | backend/Controllers/TasksController.cs:2302 | [Authorize] | да | нет |
| DELETE | api/tasks/templates/{id:int} | TasksController | DeleteTemplate | backend/Controllers/TasksController.cs:2333 | [Authorize] | да | нет |
| GET | api/tasks/{id:int} | TasksController | GetTask | backend/Controllers/TasksController.cs:2358 | [Authorize] | да | docs/development-release-preparation-2026-10-06.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/release-verification-history-2026-10-06.md, docs/storage-resilience-runbook.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-result-approval-current-state-2026-07-09.md |
| GET | api/tasks/{id:int}/comments | TasksController | GetComments | backend/Controllers/TasksController.cs:2379 | [Authorize] | да | нет |
| POST | api/tasks/{id:int}/comments | TasksController | CreateComment | backend/Controllers/TasksController.cs:2410 | [Authorize] | да | нет |
| POST | api/tasks/{id:int}/deadline-change-requests | TasksController | CreateDeadlineChangeRequest | backend/Controllers/TasksController.cs:2463 | [Authorize] | да | docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md |
| GET | api/tasks/{id:int}/deadline-change-requests | TasksController | GetDeadlineChangeRequests | backend/Controllers/TasksController.cs:2567 | [Authorize] | да | docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md |
| GET | api/tasks/{id:int}/deadline-change-requests/{requestId:int} | TasksController | GetDeadlineChangeRequest | backend/Controllers/TasksController.cs:2598 | [Authorize] | да | нет |
| POST | api/tasks/{id:int}/deadline-change-requests/{requestId:int}/approve | TasksController | ApproveDeadlineChangeRequest | backend/Controllers/TasksController.cs:2626 | [Authorize] | да | нет |
| POST | api/tasks/{id:int}/deadline-change-requests/{requestId:int}/reject | TasksController | RejectDeadlineChangeRequest | backend/Controllers/TasksController.cs:2727 | [Authorize] | да | нет |
| GET | api/tasks/{id:int}/files | TasksController | GetFiles | backend/Controllers/TasksController.cs:2821 | [Authorize] | да | docs/storage-resilience-runbook.md |
| GET | api/tasks/{id:int}/files/uploads | TasksController | GetFileUploads | backend/Controllers/TasksController.cs:2922 | [Authorize] | да | docs/storage-resilience-runbook.md, docs/unpushed-changes-map-2026-10-03.md |
| POST | api/tasks/{id:int}/files | TasksController | UploadFile | backend/Controllers/TasksController.cs:2952 | [Authorize] | да | docs/date-time-inventory.md, docs/file-upload-limits-2026-06-10.md, docs/storage-resilience-runbook.md |
| GET | api/tasks/{id:int}/files/{fileId:int}/download | TasksController | DownloadFile | backend/Controllers/TasksController.cs:3188 | [Authorize] | да | нет |
| DELETE | api/tasks/{id:int}/files/{fileId:int} | TasksController | DeleteFile | backend/Controllers/TasksController.cs:3241 | [Authorize] | да | нет |
| GET | api/tasks/billing-context | TasksController | GetBillingContext | backend/Controllers/TasksController.cs:3304 | [Authorize] | да | нет |
| POST | api/tasks | TasksController | CreateTask | backend/Controllers/TasksController.cs:3376 | [Authorize] | да | docs/development-release-preparation-2026-10-06.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/release-verification-history-2026-10-06.md, docs/storage-resilience-runbook.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-journal-query-optimization.md, docs/task-result-approval-current-state-2026-07-09.md, docs/task-template-current-state-2026-07-09.md |
| PUT | api/tasks/{id:int}/billing-classification | TasksController | UpdateTaskBillingClassification | backend/Controllers/TasksController.cs:3531 | [Authorize] | да | нет |
| GET | api/tasks/{id:int}/billing-invoices | TasksController | GetTaskBillingInvoices | backend/Controllers/TasksController.cs:3667 | [Authorize] | да | нет |
| PUT | api/tasks/{id:int}/billing-invoice | TasksController | LinkTaskBillingInvoice | backend/Controllers/TasksController.cs:3721 | [Authorize] | да | нет |
| PUT | api/tasks/{id:int} | TasksController | UpdateTask | backend/Controllers/TasksController.cs:3833 | [Authorize] | да | docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/storage-resilience-runbook.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-result-approval-current-state-2026-07-09.md |
| PATCH | api/tasks/{id:int}/status | TasksController | UpdateStatus | backend/Controllers/TasksController.cs:4192 | [Authorize] | да | docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/task-result-approval-current-state-2026-07-09.md |
| PATCH | api/tasks/{id:int}/control | TasksController | UpdateControl | backend/Controllers/TasksController.cs:4416 | [Authorize] | да | нет |
| POST | api/tasks/bulk | TasksController | BulkUpdateTasks | backend/Controllers/TasksController.cs:4482 | [Authorize] | да | docs/payplanner-task-billing-bulk-plan-2026-10-23.md |
| POST | api/tasks/bulk/delete | TasksController | BulkDeleteTasks | backend/Controllers/TasksController.cs:5160 | [Authorize] | да | нет |
| PATCH | api/tasks/{id:int}/approval | TasksController | UpdateApproval | backend/Controllers/TasksController.cs:5258 | [Authorize] | да | нет |
| DELETE | api/tasks/{id:int} | TasksController | DeleteTask | backend/Controllers/TasksController.cs:5456 | [Authorize] | да | docs/payplanner-task-workflow-roadmap-2026-10-14.md, docs/storage-resilience-runbook.md, docs/task-deadline-change-current-state-2026-07-09.md, docs/task-deadline-change-model-design-2026-07-09.md, docs/task-result-approval-current-state-2026-07-09.md |
| GET | api/task-time/current | TaskTimeController | Current | backend/Controllers/TaskTimeController.cs:28 | [Authorize] | да | нет |
| GET | api/task-time/task/{taskId:int}/history | TaskTimeController | History | backend/Controllers/TaskTimeController.cs:41 | [Authorize] | да | нет |
| GET | api/task-time/task/{taskId:int}/{id:guid}/corrections | TaskTimeController | Corrections | backend/Controllers/TaskTimeController.cs:61 | [Authorize] | да | нет |
| POST | api/task-time/task/{taskId:int}/{id:guid}/correction | TaskTimeController | Correct | backend/Controllers/TaskTimeController.cs:78 | [Authorize] | да | нет |
| POST | api/task-time/start | TaskTimeController | Start | backend/Controllers/TaskTimeController.cs:126 | [Authorize] | да | нет |
| POST | api/task-time/{id:guid}/stop | TaskTimeController | Stop | backend/Controllers/TaskTimeController.cs:192 | [Authorize] | да | нет |
| GET | api/tax-register/entries | TaxRegisterController | List | backend/Controllers/TaxRegisterController.cs:87 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/tax-register/entries | TaxRegisterController | Create | backend/Controllers/TaxRegisterController.cs:120 | [Authorize(Policy = "Admin")] | нет | нет |
| PUT | api/tax-register/entries/{id:int} | TaxRegisterController | Update | backend/Controllers/TaxRegisterController.cs:124 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/tax-register/entries/{id:int}/approve | TaxRegisterController | Approve | backend/Controllers/TaxRegisterController.cs:128 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/tax-register/entries/{id:int}/archive | TaxRegisterController | Archive | backend/Controllers/TaxRegisterController.cs:132 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/tax-register/entries/{id:int}/correct | TaxRegisterController | Correct | backend/Controllers/TaxRegisterController.cs:136 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/tax-register/entries/{id:int}/events | TaxRegisterController | Events | backend/Controllers/TaxRegisterController.cs:140 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/tax-register/journal | TaxRegisterController | Journal | backend/Controllers/TaxRegisterController.cs:155 | [Authorize(Policy = "Admin")] | нет | docs/claude-next-development-prompt-2026-10-10.md, docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/tax-register/book | TaxRegisterController | Book | backend/Controllers/TaxRegisterController.cs:167 | [Authorize(Policy = "Admin")] | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| GET | api/tax-register/book/export.csv | TaxRegisterController | ExportCsv | backend/Controllers/TaxRegisterController.cs:187 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/tax-register/book/export.xlsx | TaxRegisterController | ExportXlsx | backend/Controllers/TaxRegisterController.cs:202 | [Authorize(Policy = "Admin")] | нет | docs/pay-33-36-implementation-log-2026-10-09.md |
| POST | api/tax-register/close-year | TaxRegisterController | CloseYear | backend/Controllers/TaxRegisterController.cs:216 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/user-activity | UserActivityController | GetAsync | backend/Controllers/UserActivityController.cs:62 | [Authorize]; [Authorize(Policy = "Admin")] | да | docs/development-pause-plan-2026-09-30.md, docs/full-project-audit-2026-06-10.md |
| GET | api/user-activity/online | UserActivityController | GetOnlineAsync | backend/Controllers/UserActivityController.cs:168 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| GET | api/user-activity/filters | UserActivityController | GetFiltersAsync | backend/Controllers/UserActivityController.cs:302 | [Authorize]; [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/user-activity | UserActivityController | CreateAsync | backend/Controllers/UserActivityController.cs:361 | [Authorize] | да | docs/development-pause-plan-2026-09-30.md, docs/full-project-audit-2026-06-10.md |
| POST | api/user-activity/heartbeat | UserActivityController | HeartbeatAsync | backend/Controllers/UserActivityController.cs:394 | [Authorize] | да | docs/development-pause-plan-2026-09-30.md |
| GET | api/user-activity/admin/maintenance | UserActivityController | GetMaintenanceAsync | backend/Controllers/UserActivityController.cs:450 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| PUT | api/user-activity/admin/settings | UserActivityController | UpdateSettingsAsync | backend/Controllers/UserActivityController.cs:457 | [Authorize]; [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/user-activity/admin/cleanup | UserActivityController | CleanupAsync | backend/Controllers/UserActivityController.cs:467 | [Authorize]; [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/user-activity/admin/compact | UserActivityController | CompactAsync | backend/Controllers/UserActivityController.cs:474 | [Authorize]; [Authorize(Policy = "Admin")] | нет | нет |
| DELETE | api/user-activity/cleanup | UserActivityController | DeleteAllAsync | backend/Controllers/UserActivityController.cs:487 | [Authorize]; [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/users/{id:int}/approve | UsersController | ApproveUser | backend/Controllers/UsersController.cs:41 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/users | UsersController | CreateUser | backend/Controllers/UsersController.cs:54 | [Authorize(Policy = "Admin")] | нет | docs/full-project-audit-2026-06-10.md |
| DELETE | api/users/{id:int} | UsersController | DeleteUser | backend/Controllers/UsersController.cs:78 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/users/roles | UsersController | GetRoles | backend/Controllers/UsersController.cs:112 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/users/{id:int} | UsersController | GetUser | backend/Controllers/UsersController.cs:115 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/users/{id:int}/contractor-balance | UsersController | GetUserContractorBalance | backend/Controllers/UsersController.cs:119 | [Authorize(Policy = "Admin")] | да | нет |
| GET | api/users | UsersController | GetUsers | backend/Controllers/UsersController.cs:167 | [Authorize(Policy = "Admin")] | нет | docs/full-project-audit-2026-06-10.md |
| GET | api/users/paged | UsersController | GetUsersPaged | backend/Controllers/UsersController.cs:171 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/users/{id:int}/reject | UsersController | RejectUser | backend/Controllers/UsersController.cs:178 | [Authorize(Policy = "Admin")] | нет | нет |
| POST | api/users/merge | UsersController | MergeUsers | backend/Controllers/UsersController.cs:212 | [Authorize(Policy = "Admin")] | нет | нет |
| PUT | api/users/{id:int} | UsersController | UpdateUser | backend/Controllers/UsersController.cs:231 | [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/user-settings/{key} | UserSettingsController | Get | backend/Controllers/UserSettingsController.cs:44 | [Authorize] | да | docs/development-2026-10-16-audit-write-budget.md |
| PUT | api/user-settings/{key} | UserSettingsController | Upsert | backend/Controllers/UserSettingsController.cs:59 | [Authorize] | нет | нет |
| GET | api/user-settings/users/{userId:int}/{key} | UserSettingsController | GetForUser | backend/Controllers/UserSettingsController.cs:68 | [Authorize]; [Authorize(Policy = "Admin")] | да | нет |
| PUT | api/user-settings/users/{userId:int}/{key} | UserSettingsController | UpsertForUser | backend/Controllers/UserSettingsController.cs:81 | [Authorize]; [Authorize(Policy = "Admin")] | нет | нет |
| GET | api/v1/cases | CasesV1Controller | Get | backend/Controllers/V1/CasesV1Controller.cs:29 | [Authorize] | да | docs/full-project-audit-2026-06-10.md, docs/unpushed-changes-map-2026-10-03.md |
| GET | api/v1/cases/{id:int} | CasesV1Controller | GetById | backend/Controllers/V1/CasesV1Controller.cs:59 | [Authorize] | да | нет |
| POST | api/v1/cases | CasesV1Controller | Create | backend/Controllers/V1/CasesV1Controller.cs:81 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/v1/cases/{id:int} | CasesV1Controller | Update | backend/Controllers/V1/CasesV1Controller.cs:103 | [Authorize] | да | нет |
| DELETE | api/v1/cases/{id:int} | CasesV1Controller | Delete | backend/Controllers/V1/CasesV1Controller.cs:161 | [Authorize] | да | нет |
| GET | api/v1/clients | ClientsV1Controller | Get | backend/Controllers/V1/ClientsV1Controller.cs:53 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/v1/clients/{id:int} | ClientsV1Controller | GetById | backend/Controllers/V1/ClientsV1Controller.cs:90 | [Authorize] | да | нет |
| POST | api/v1/clients | ClientsV1Controller | Create | backend/Controllers/V1/ClientsV1Controller.cs:125 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/v1/clients/{id:int} | ClientsV1Controller | Update | backend/Controllers/V1/ClientsV1Controller.cs:155 | [Authorize] | да | нет |
| DELETE | api/v1/clients/{id:int} | ClientsV1Controller | Delete | backend/Controllers/V1/ClientsV1Controller.cs:199 | [Authorize] | да | нет |
| GET | api/v1/payments | PaymentsV1Controller | Get | backend/Controllers/V1/PaymentsV1Controller.cs:50 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/v1/payments/{id:int} | PaymentsV1Controller | GetById | backend/Controllers/V1/PaymentsV1Controller.cs:77 | [Authorize] | да | нет |
| POST | api/v1/payments | PaymentsV1Controller | Create | backend/Controllers/V1/PaymentsV1Controller.cs:97 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/v1/payments/{id:int} | PaymentsV1Controller | Update | backend/Controllers/V1/PaymentsV1Controller.cs:128 | [Authorize] | да | нет |
| DELETE | api/v1/payments/{id:int} | PaymentsV1Controller | Delete | backend/Controllers/V1/PaymentsV1Controller.cs:190 | [Authorize] | да | нет |
| GET | {id:int}/appeal-decision | CasesV2Controller | GetCurrentCaseAppealDecision | backend/Controllers/V2/CasesV2Controller.Appeal.cs:45 | не задано | да | нет |
| GET | appeal-deadlines/expiring | CasesV2Controller | GetExpiringCaseAppeals | backend/Controllers/V2/CasesV2Controller.Appeal.cs:99 | не задано | да | нет |
| POST | {id:int}/appeal-decision | CasesV2Controller | DecideCaseAppeal | backend/Controllers/V2/CasesV2Controller.Appeal.cs:193 | не задано | да | нет |
| POST | {id:int}/appeal-reminder | CasesV2Controller | RemindCaseAppealAssignee | backend/Controllers/V2/CasesV2Controller.Appeal.cs:393 | не задано | да | нет |
| GET | {id:int}/court-recoveries | CasesV2Controller | GetCourtRecoveries | backend/Controllers/V2/CasesV2Controller.CourtRecoveries.cs:32 | не задано | да | нет |
| PUT | {id:int}/court-recoveries/{paymentId:int} | CasesV2Controller | SetCourtRecoveryPayment | backend/Controllers/V2/CasesV2Controller.CourtRecoveries.cs:113 | не задано | да | нет |
| DELETE | {id:int}/court-recoveries/{paymentId:int} | CasesV2Controller | RemoveCourtRecoveryPayment | backend/Controllers/V2/CasesV2Controller.CourtRecoveries.cs:190 | не задано | да | нет |
| GET | api/v2/cases | CasesV2Controller | Get | backend/Controllers/V2/CasesV2Controller.cs:293 | [Authorize] | да | docs/case-access-query-optimization.md, docs/case-business-rules.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md, docs/case-registry-query-optimization.md, docs/development-2026-10-16-audit-write-budget.md, docs/full-project-audit-2026-06-10.md, docs/max-latest-reply-drafts-2026-10-15.md, docs/storage-resilience-runbook.md |
| GET | api/v2/cases/court-registry-summary | CasesV2Controller | GetCourtRegistrySummary | backend/Controllers/V2/CasesV2Controller.cs:392 | [Authorize] | да | нет |
| GET | api/v2/cases/court-report-overview | CasesV2Controller | GetCourtReportOverview | backend/Controllers/V2/CasesV2Controller.cs:503 | [Authorize] | да | нет |
| GET | api/v2/cases/court-workload | CasesV2Controller | GetCourtWorkload | backend/Controllers/V2/CasesV2Controller.cs:737 | [Authorize] | да | нет |
| PATCH | api/v2/cases/{id:int}/court-assignee | CasesV2Controller | AssignCourtCase | backend/Controllers/V2/CasesV2Controller.cs:839 | [Authorize] | да | нет |
| GET | api/v2/cases/court-hearings/tomorrow | CasesV2Controller | GetTomorrowCourtHearings | backend/Controllers/V2/CasesV2Controller.cs:920 | [Authorize] | да | нет |
| GET | api/v2/cases/export.csv | CasesV2Controller | ExportRegistryCsv | backend/Controllers/V2/CasesV2Controller.cs:1063 | [Authorize] | да | docs/case-registry-query-optimization.md |
| GET | api/v2/cases/export.xls | CasesV2Controller | ExportRegistryExcel | backend/Controllers/V2/CasesV2Controller.cs:1112 | [Authorize] | да | docs/case-registry-query-optimization.md |
| GET | api/v2/cases/export.pdf | CasesV2Controller | ExportRegistryPdf | backend/Controllers/V2/CasesV2Controller.cs:1161 | [Authorize] | да | docs/case-registry-query-optimization.md |
| GET | api/v2/cases/print | CasesV2Controller | PrintRegistry | backend/Controllers/V2/CasesV2Controller.cs:1215 | [Authorize] | да | docs/case-registry-query-optimization.md |
| GET | api/v2/cases/document-templates | CasesV2Controller | GetDocumentTemplates | backend/Controllers/V2/CasesV2Controller.cs:2637 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/document-templates | CasesV2Controller | GetDocumentTemplates | backend/Controllers/V2/CasesV2Controller.cs:2638 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/v2/cases/document-template-tokens | CasesV2Controller | GetDocumentTemplateTokens | backend/Controllers/V2/CasesV2Controller.cs:2653 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/document-template-tokens | CasesV2Controller | GetDocumentTemplateTokens | backend/Controllers/V2/CasesV2Controller.cs:2654 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/v2/cases/document-templates/history | CasesV2Controller | GetDocumentTemplateHistory | backend/Controllers/V2/CasesV2Controller.cs:2673 | [Authorize] | да | нет |
| GET | api/document-templates/history | CasesV2Controller | GetDocumentTemplateHistory | backend/Controllers/V2/CasesV2Controller.cs:2674 | [Authorize] | да | нет |
| GET | api/v2/cases/document-templates/history/{versionId:int} | CasesV2Controller | GetDocumentTemplateVersion | backend/Controllers/V2/CasesV2Controller.cs:2685 | [Authorize] | да | нет |
| GET | api/document-templates/history/{versionId:int} | CasesV2Controller | GetDocumentTemplateVersion | backend/Controllers/V2/CasesV2Controller.cs:2686 | [Authorize] | да | нет |
| POST | api/v2/cases/document-templates/history/{versionId:int}/restore | CasesV2Controller | RestoreDocumentTemplateVersion | backend/Controllers/V2/CasesV2Controller.cs:2707 | [Authorize] | да | нет |
| POST | api/document-templates/history/{versionId:int}/restore | CasesV2Controller | RestoreDocumentTemplateVersion | backend/Controllers/V2/CasesV2Controller.cs:2708 | [Authorize] | да | нет |
| POST | api/v2/cases/document-templates | CasesV2Controller | CreateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:2736 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| POST | api/document-templates | CasesV2Controller | CreateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:2737 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PATCH | api/v2/cases/document-templates/{templateKey} | CasesV2Controller | UpdateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:2765 | [Authorize] | да | нет |
| PATCH | api/document-templates/{templateKey} | CasesV2Controller | UpdateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:2766 | [Authorize] | да | нет |
| POST | api/v2/cases/document-templates/{templateKey}/render | CasesV2Controller | RenderDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:2807 | [Authorize] | да | нет |
| POST | api/document-templates/{templateKey}/render | CasesV2Controller | RenderDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:2808 | [Authorize] | да | нет |
| GET | api/v2/cases/document-types | CasesV2Controller | GetDocumentTypes | backend/Controllers/V2/CasesV2Controller.cs:2875 | [Authorize] | да | нет |
| GET | api/case-calendar/events | CasesV2Controller | GetCaseCalendarEvents | backend/Controllers/V2/CasesV2Controller.cs:2907 | [Authorize] | да | нет |
| GET | api/case-calendar/summary | CasesV2Controller | GetCaseCalendarSummary | backend/Controllers/V2/CasesV2Controller.cs:2961 | [Authorize] | да | нет |
| GET | api/case-calendar/events.ics | CasesV2Controller | ExportCaseCalendarIcs | backend/Controllers/V2/CasesV2Controller.cs:2992 | [Authorize] | да | нет |
| POST | api/v2/cases/procedural-deadlines/calculate | CasesV2Controller | CalculateProceduralDeadlinePreview | backend/Controllers/V2/CasesV2Controller.cs:3049 | [Authorize] | да | нет |
| GET | api/v2/cases/procedural-deadlines/calendar-settings | CasesV2Controller | GetProceduralDeadlineCalendarSettings | backend/Controllers/V2/CasesV2Controller.cs:3090 | [Authorize] | да | docs/case-business-rules.md |
| PUT | api/v2/cases/procedural-deadlines/calendar-settings | CasesV2Controller | UpdateProceduralDeadlineCalendarSettings | backend/Controllers/V2/CasesV2Controller.cs:3101 | [Authorize] | да | docs/case-business-rules.md |
| GET | api/v2/cases/court-directory | CasesV2Controller | GetCourtDirectory | backend/Controllers/V2/CasesV2Controller.cs:3127 | [Authorize] | да | нет |
| POST | api/v2/cases/court-directory | CasesV2Controller | CreateCourtDirectoryItem | backend/Controllers/V2/CasesV2Controller.cs:3152 | [Authorize] | да | нет |
| PUT | api/v2/cases/court-directory/{courtId:int} | CasesV2Controller | UpdateCourtDirectoryItem | backend/Controllers/V2/CasesV2Controller.cs:3196 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int} | CasesV2Controller | GetById | backend/Controllers/V2/CasesV2Controller.cs:3237 | [Authorize] | да | docs/case-access-query-optimization.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md |
| GET | api/v2/cases/{id:int}/procedural-deadlines | CasesV2Controller | GetProceduralDeadlines | backend/Controllers/V2/CasesV2Controller.cs:3301 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/procedural-deadlines | CasesV2Controller | CreateProceduralDeadline | backend/Controllers/V2/CasesV2Controller.cs:3337 | [Authorize] | да | нет |
| PUT | api/v2/cases/{id:int}/procedural-deadlines/{deadlineId:int} | CasesV2Controller | UpdateProceduralDeadline | backend/Controllers/V2/CasesV2Controller.cs:3426 | [Authorize] | да | docs/case-business-rules.md |
| POST | api/v2/cases/{id:int}/procedural-deadlines/{deadlineId:int}/close | CasesV2Controller | CloseProceduralDeadline | backend/Controllers/V2/CasesV2Controller.cs:3531 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/court-instances | CasesV2Controller | GetCourtInstances | backend/Controllers/V2/CasesV2Controller.cs:3594 | [Authorize] | да | нет |
| PUT | api/v2/cases/{id:int}/court-instances/{instance} | CasesV2Controller | UpsertCourtInstance | backend/Controllers/V2/CasesV2Controller.cs:3638 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/court-instances/{instance}/complaints | CasesV2Controller | CreateCourtComplaint | backend/Controllers/V2/CasesV2Controller.cs:3728 | [Authorize] | да | нет |
| PUT | api/v2/cases/{id:int}/court-instances/{instance}/complaints/{complaintId:int} | CasesV2Controller | UpdateCourtComplaint | backend/Controllers/V2/CasesV2Controller.cs:3797 | [Authorize] | да | нет |
| DELETE | api/v2/cases/{id:int}/court-instances/{instance}/complaints/{complaintId:int} | CasesV2Controller | ArchiveCourtComplaint | backend/Controllers/V2/CasesV2Controller.cs:3869 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/court-events | CasesV2Controller | GetCourtEvents | backend/Controllers/V2/CasesV2Controller.cs:3926 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/court-events | CasesV2Controller | CreateCourtEvent | backend/Controllers/V2/CasesV2Controller.cs:3961 | [Authorize] | да | нет |
| PUT | api/v2/cases/{id:int}/court-events/{courtEventId:int} | CasesV2Controller | UpdateCourtEvent | backend/Controllers/V2/CasesV2Controller.cs:4055 | [Authorize] | да | docs/case-business-rules.md |
| GET | api/v2/cases/{id:int}/court-events/{courtEventId:int}/history | CasesV2Controller | GetCourtEventHistory | backend/Controllers/V2/CasesV2Controller.cs:4160 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/court-parties | CasesV2Controller | GetCourtParties | backend/Controllers/V2/CasesV2Controller.cs:4203 | [Authorize] | да | docs/case-detail-query-optimization.md |
| POST | api/v2/cases/{id:int}/court-parties | CasesV2Controller | CreateCourtParty | backend/Controllers/V2/CasesV2Controller.cs:4243 | [Authorize] | да | docs/case-detail-query-optimization.md |
| DELETE | api/v2/cases/{id:int}/court-parties/{partyId:int} | CasesV2Controller | ArchiveCourtParty | backend/Controllers/V2/CasesV2Controller.cs:4302 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/access-request | CasesV2Controller | RequestAccess | backend/Controllers/V2/CasesV2Controller.cs:4345 | [Authorize] | да | нет |
| GET | api/v2/cases/access-requests | CasesV2Controller | GetAccessRequests | backend/Controllers/V2/CasesV2Controller.cs:4432 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/access-request/resolve | CasesV2Controller | ResolveAccessRequest | backend/Controllers/V2/CasesV2Controller.cs:4473 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/timeline | CasesV2Controller | GetTimeline | backend/Controllers/V2/CasesV2Controller.cs:4587 | [Authorize] | да | docs/case-detail-query-optimization.md |
| GET | api/v2/cases/{id:int}/conversation | CasesV2Controller | GetConversation | backend/Controllers/V2/CasesV2Controller.cs:4622 | [Authorize] | да | docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md, docs/max-latest-reply-drafts-2026-10-15.md |
| GET | api/v2/cases/{id:int}/summary | CasesV2Controller | GetSummary | backend/Controllers/V2/CasesV2Controller.cs:4652 | [Authorize] | да | docs/case-detail-query-optimization.md |
| GET | api/v2/cases/{id:int}/finance | CasesV2Controller | GetFinance | backend/Controllers/V2/CasesV2Controller.cs:4732 | [Authorize] | да | docs/case-detail-query-optimization.md |
| GET | api/v2/cases/{id:int}/documents | CasesV2Controller | GetDocuments | backend/Controllers/V2/CasesV2Controller.cs:4831 | [Authorize] | да | docs/development-2026-10-16-audit-write-budget.md |
| GET | api/case-documents | CasesV2Controller | GetDocumentRegistry | backend/Controllers/V2/CasesV2Controller.cs:4869 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| GET | api/case-documents/{documentId:int} | CasesV2Controller | GetDocumentById | backend/Controllers/V2/CasesV2Controller.cs:4984 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/documents/uploads | CasesV2Controller | GetDocumentUploads | backend/Controllers/V2/CasesV2Controller.cs:5025 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/documents/uploads/{uploadKey:guid}/retire | CasesV2Controller | RetireDocumentUpload | backend/Controllers/V2/CasesV2Controller.cs:5039 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/documents/uploads/pending | CasesV2Controller | GetPendingDocumentUploads | backend/Controllers/V2/CasesV2Controller.cs:5062 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/documents | CasesV2Controller | UploadDocument | backend/Controllers/V2/CasesV2Controller.cs:5078 | [Authorize] | да | docs/file-upload-limits-2026-06-10.md |
| POST | api/case-documents/{documentId:int}/versions | CasesV2Controller | UploadDocumentVersionById | backend/Controllers/V2/CasesV2Controller.cs:5117 | [Authorize] | да | docs/file-upload-limits-2026-06-10.md |
| POST | api/v2/cases/{id:int}/documents/generate-template | CasesV2Controller | GenerateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:5163 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/documents/{documentId:int}/download | CasesV2Controller | DownloadDocument | backend/Controllers/V2/CasesV2Controller.cs:5349 | [Authorize] | да | нет |
| GET | api/case-documents/{documentId:int}/download | CasesV2Controller | DownloadDocumentById | backend/Controllers/V2/CasesV2Controller.cs:5394 | [Authorize] | да | нет |
| PATCH | api/case-documents/{documentId:int} | CasesV2Controller | UpdateDocumentById | backend/Controllers/V2/CasesV2Controller.cs:5462 | [Authorize] | да | нет |
| PATCH | api/v2/cases/{id:int}/documents/{documentId:int} | CasesV2Controller | UpdateDocument | backend/Controllers/V2/CasesV2Controller.cs:5498 | [Authorize] | да | нет |
| DELETE | api/v2/cases/{id:int}/documents/{documentId:int} | CasesV2Controller | DeleteDocument | backend/Controllers/V2/CasesV2Controller.cs:6073 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/activity | CasesV2Controller | MarkActivity | backend/Controllers/V2/CasesV2Controller.cs:6768 | [Authorize] | да | docs/development-release-preparation-2026-10-06.md, docs/release-verification-history-2026-10-06.md |
| POST | api/v2/cases/{id:int}/history | CasesV2Controller | AddHistoryEntry | backend/Controllers/V2/CasesV2Controller.cs:6843 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/conversation | CasesV2Controller | AddConversationEntry | backend/Controllers/V2/CasesV2Controller.cs:6903 | [Authorize] | да | docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md |
| POST | api/v2/cases/{id:int}/conversation | CasesV2Controller | AddConversationEntryWithAttachments | backend/Controllers/V2/CasesV2Controller.cs:6940 | [Authorize] | да | docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md |
| GET | api/v2/cases/{id:int}/related-parties | CasesV2Controller | GetRelatedParties | backend/Controllers/V2/CasesV2Controller.cs:6978 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/related-parties | CasesV2Controller | AddRelatedParty | backend/Controllers/V2/CasesV2Controller.cs:7024 | [Authorize] | да | нет |
| DELETE | api/v2/cases/{id:int}/related-parties/{partyId:int} | CasesV2Controller | DeleteRelatedParty | backend/Controllers/V2/CasesV2Controller.cs:7268 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/work-order | CasesV2Controller | GetWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:7314 | [Authorize] | да | docs/case-access-query-optimization.md |
| GET | api/v2/cases/{id:int}/work-order/external-approval | CasesV2Controller | GetExternalWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:7347 | [Authorize] | да | docs/case-access-query-optimization.md |
| POST | api/v2/cases/{id:int}/work-order/external-approval | CasesV2Controller | CreateExternalWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:7378 | [Authorize] | да | docs/case-access-query-optimization.md |
| DELETE | api/v2/cases/{id:int}/work-order/external-approval | CasesV2Controller | RevokeExternalWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:7415 | [Authorize] | да | docs/case-access-query-optimization.md |
| GET | api/v2/cases/{id:int}/work-order/print | CasesV2Controller | PrintWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:7467 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order/generate-document | CasesV2Controller | GenerateWorkOrderDocument | backend/Controllers/V2/CasesV2Controller.cs:7506 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order | CasesV2Controller | CreateWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:7627 | [Authorize] | да | docs/case-access-query-optimization.md |
| PATCH | api/v2/cases/{id:int}/work-order | CasesV2Controller | UpdateWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:7681 | [Authorize] | да | docs/case-access-query-optimization.md |
| DELETE | api/v2/cases/{id:int}/work-order | CasesV2Controller | DeleteWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:7758 | [Authorize] | да | docs/case-access-query-optimization.md |
| POST | api/v2/cases/{id:int}/work-order/items | CasesV2Controller | AddWorkOrderItem | backend/Controllers/V2/CasesV2Controller.cs:7791 | [Authorize] | да | нет |
| PATCH | api/v2/cases/{id:int}/work-order/items/{itemId:int} | CasesV2Controller | UpdateWorkOrderItem | backend/Controllers/V2/CasesV2Controller.cs:7836 | [Authorize] | да | нет |
| DELETE | api/v2/cases/{id:int}/work-order/items/{itemId:int} | CasesV2Controller | DeleteWorkOrderItem | backend/Controllers/V2/CasesV2Controller.cs:7886 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order/create-invoice | CasesV2Controller | CreateInvoiceFromWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:7926 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order/create-act | CasesV2Controller | CreateActFromWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:8070 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order/create-contract | CasesV2Controller | CreateContractFromWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:8207 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order/submit-approval | CasesV2Controller | SubmitWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:8340 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order/approve | CasesV2Controller | ApproveWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:8381 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/work-order/request-revision | CasesV2Controller | RequestWorkOrderRevision | backend/Controllers/V2/CasesV2Controller.cs:8412 | [Authorize] | да | нет |
| GET | api/v2/cases/{id:int}/court-sync/logs | CasesV2Controller | GetCourtSyncLogs | backend/Controllers/V2/CasesV2Controller.cs:8444 | [Authorize] | да | нет |
| GET | api/v2/cases/court-sync/health | CasesV2Controller | GetCourtSyncHealth | backend/Controllers/V2/CasesV2Controller.cs:8478 | [Authorize] | да | нет |
| PATCH | api/v2/cases/{id:int}/court-sync/monitoring | CasesV2Controller | SetCourtSyncMonitoring | backend/Controllers/V2/CasesV2Controller.cs:8532 | [Authorize] | да | нет |
| PATCH | api/v2/cases/{id:int}/court-sync/case-number | CasesV2Controller | UpdateCourtSyncCaseNumber | backend/Controllers/V2/CasesV2Controller.cs:8576 | [Authorize] | да | нет |
| GET | api/v2/cases/court-sync/logs | CasesV2Controller | GetCourtSyncJournal | backend/Controllers/V2/CasesV2Controller.cs:8666 | [Authorize] | да | нет |
| GET | api/v2/cases/court-sync/settings | CasesV2Controller | GetCourtSyncSettings | backend/Controllers/V2/CasesV2Controller.cs:8775 | [Authorize] | да | нет |
| GET | api/v2/cases/court-sync/providers | CasesV2Controller | GetCourtSyncProviders | backend/Controllers/V2/CasesV2Controller.cs:8786 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/court-sync/search | CasesV2Controller | SearchCourtSyncCases | backend/Controllers/V2/CasesV2Controller.cs:8817 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/court-sync/bind | CasesV2Controller | BindCourtSyncCase | backend/Controllers/V2/CasesV2Controller.cs:8885 | [Authorize] | да | нет |
| PUT | api/v2/cases/court-sync/settings | CasesV2Controller | UpdateCourtSyncSettings | backend/Controllers/V2/CasesV2Controller.cs:9056 | [Authorize] | да | нет |
| POST | api/v2/cases/{id:int}/court-sync/run | CasesV2Controller | RunCourtSync | backend/Controllers/V2/CasesV2Controller.cs:9080 | [Authorize] | да | нет |
| POST | api/v2/cases | CasesV2Controller | Create | backend/Controllers/V2/CasesV2Controller.cs:10634 | [Authorize] | да | docs/case-access-query-optimization.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md, docs/case-registry-query-optimization.md, docs/full-project-audit-2026-06-10.md, docs/storage-resilience-runbook.md |
| PUT | api/v2/cases/{id:int} | CasesV2Controller | Update | backend/Controllers/V2/CasesV2Controller.cs:10712 | [Authorize] | да | docs/case-access-query-optimization.md, docs/case-business-rules.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md |
| PATCH | api/v2/cases/{id:int}/control | CasesV2Controller | UpdateControl | backend/Controllers/V2/CasesV2Controller.cs:10853 | [Authorize] | да | нет |
| DELETE | api/v2/cases/{id:int} | CasesV2Controller | Delete | backend/Controllers/V2/CasesV2Controller.cs:10892 | [Authorize] | да | docs/case-access-query-optimization.md, docs/case-conversation-current-state-2026-07-08.md, docs/case-detail-query-optimization.md |
| POST | api/v2/cases/bulk-update | CasesV2Controller | BulkUpdate | backend/Controllers/V2/CasesV2Controller.cs:10959 | [Authorize] | да | нет |
| PUT | {id:int}/court-events/{courtEventId:int}/hearing-outcome | CasesV2Controller | RecordHearingOutcome | backend/Controllers/V2/CasesV2Controller.HearingOutcome.cs:18 | не задано | да | нет |
| GET | {id:int}/hearing-preparation | CasesV2Controller | GetHearingPreparation | backend/Controllers/V2/CasesV2Controller.HearingPreparation.cs:32 | не задано | да | нет |
| PUT | {id:int}/hearing-preparation | CasesV2Controller | UpdateHearingPreparation | backend/Controllers/V2/CasesV2Controller.HearingPreparation.cs:57 | не задано | да | нет |
| POST | {id:int}/next-hearing-confirmation | CasesV2Controller | ConfirmNextHearing | backend/Controllers/V2/CasesV2Controller.NextHearingConfirmation.cs:18 | не задано | да | нет |
| POST | {id:int}/position-review | CasesV2Controller | ReviewCasePosition | backend/Controllers/V2/CasesV2Controller.PositionReview.cs:17 | не задано | да | нет |
| GET | position-templates | CasesV2Controller | GetPositionTemplates | backend/Controllers/V2/CasesV2Controller.PositionTemplates.cs:26 | не задано | да | нет |
| POST | position-templates | CasesV2Controller | CreatePositionTemplate | backend/Controllers/V2/CasesV2Controller.PositionTemplates.cs:53 | не задано | да | нет |
| PUT | position-templates/{templateId:int} | CasesV2Controller | UpdatePositionTemplate | backend/Controllers/V2/CasesV2Controller.PositionTemplates.cs:96 | не задано | да | нет |
| DELETE | position-templates/{templateId:int} | CasesV2Controller | ArchivePositionTemplate | backend/Controllers/V2/CasesV2Controller.PositionTemplates.cs:129 | не задано | да | нет |
| GET | api/v2/clients | ClientsV2Controller | Get | backend/Controllers/V2/ClientsV2Controller.cs:91 | [Authorize] | да | docs/development-roadmap-merge-2026-10-27.md, docs/full-project-audit-2026-06-10.md, docs/payplanner-access-security-roadmap-2026-10-14.md |
| GET | api/v2/clients/{id:int} | ClientsV2Controller | GetById | backend/Controllers/V2/ClientsV2Controller.cs:531 | [Authorize] | да | docs/development-roadmap-merge-2026-10-27.md, docs/payplanner-access-security-roadmap-2026-10-14.md |
| POST | api/v2/clients | ClientsV2Controller | Create | backend/Controllers/V2/ClientsV2Controller.cs:561 | [Authorize] | да | docs/full-project-audit-2026-06-10.md, docs/payplanner-access-security-roadmap-2026-10-14.md |
| PUT | api/v2/clients/{id:int} | ClientsV2Controller | Update | backend/Controllers/V2/ClientsV2Controller.cs:591 | [Authorize] | да | docs/payplanner-access-security-roadmap-2026-10-14.md |
| DELETE | api/v2/clients/{id:int} | ClientsV2Controller | Delete | backend/Controllers/V2/ClientsV2Controller.cs:635 | [Authorize] | да | docs/payplanner-access-security-roadmap-2026-10-14.md |
| GET | api/v2/payments | PaymentsV2Controller | Get | backend/Controllers/V2/PaymentsV2Controller.cs:66 | [Authorize] | да | docs/full-project-audit-2026-06-10.md, docs/max-finance-provenance-review-2026-10-15.md, docs/payplanner-financial-documents-roadmap-2026-10-14.md |
| GET | api/v2/payments/export.csv | PaymentsV2Controller | ExportCsv | backend/Controllers/V2/PaymentsV2Controller.cs:290 | [Authorize] | да | нет |
| GET | api/v2/payments/export.xls | PaymentsV2Controller | ExportExcel | backend/Controllers/V2/PaymentsV2Controller.cs:352 | [Authorize] | да | нет |
| GET | api/v2/payments/{id:int} | PaymentsV2Controller | GetById | backend/Controllers/V2/PaymentsV2Controller.cs:414 | [Authorize] | да | нет |
| POST | api/v2/payments | PaymentsV2Controller | Create | backend/Controllers/V2/PaymentsV2Controller.cs:435 | [Authorize] | да | docs/full-project-audit-2026-06-10.md |
| PUT | api/v2/payments/{id:int} | PaymentsV2Controller | Update | backend/Controllers/V2/PaymentsV2Controller.cs:477 | [Authorize] | да | нет |
| DELETE | api/v2/payments/{id:int} | PaymentsV2Controller | Delete | backend/Controllers/V2/PaymentsV2Controller.cs:637 | [Authorize] | да | нет |
| GET | api/v2/stats/summary | StatsV2Controller | Summary | backend/Controllers/V2/StatsV2Controller.cs:29 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | да | нет |
| GET | api/v2/stats/months | StatsV2Controller | Months | backend/Controllers/V2/StatsV2Controller.cs:54 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | да | нет |
| GET | api/yandex-calendar/status | YandexCalendarController | GetStatus | backend/Controllers/YandexCalendarController.cs:28 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет | нет |
| POST | api/yandex-calendar/auth/start | YandexCalendarController | StartAuth | backend/Controllers/YandexCalendarController.cs:42 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| GET | api/yandex-calendar/auth/status/{state} | YandexCalendarController | GetAuthStatus | backend/Controllers/YandexCalendarController.cs:65 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет | нет |
| GET | api/yandex-calendar/auth/callback | YandexCalendarController | Callback | backend/Controllers/YandexCalendarController.cs:85 | публичный ([AllowAnonymous]) | да | docs/yandex-calendar-sync-guide.md |
| PUT | api/yandex-calendar/settings | YandexCalendarController | UpdateSettings | backend/Controllers/YandexCalendarController.cs:100 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| POST | api/yandex-calendar/sync-now | YandexCalendarController | SyncNow | backend/Controllers/YandexCalendarController.cs:127 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| DELETE | api/yandex-calendar/connection | YandexCalendarController | Disconnect | backend/Controllers/YandexCalendarController.cs:157 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет | нет |
| GET | api/yandex-disk/connections | YandexDiskController | GetConnections | backend/Controllers/YandexDiskController.cs:44 | [Authorize] | нет | нет |
| PUT | api/yandex-disk/connections | YandexDiskController | UpsertConnection | backend/Controllers/YandexDiskController.cs:55 | [Authorize] | нет | нет |
| POST | api/yandex-disk/auth/start | YandexDiskController | StartOAuth | backend/Controllers/YandexDiskController.cs:77 | [Authorize] | нет | нет |
| GET | api/yandex-disk/auth/status/{state} | YandexDiskController | GetOAuthStatus | backend/Controllers/YandexDiskController.cs:105 | [Authorize] | нет | нет |
| GET | api/yandex-disk/auth/callback | YandexDiskController | OAuthCallback | backend/Controllers/YandexDiskController.cs:125 | публичный ([AllowAnonymous]) | да | нет |
| DELETE | api/yandex-disk/connections/{id} | YandexDiskController | DeleteConnection | backend/Controllers/YandexDiskController.cs:138 | [Authorize] | нет | нет |
| POST | api/yandex-disk/connections/{id}/connection-test | YandexDiskController | TestConnection | backend/Controllers/YandexDiskController.cs:160 | [Authorize] | да | нет |
| GET | api/yandex-disk/settings | YandexDiskController | GetSettings | backend/Controllers/YandexDiskController.cs:183 | [Authorize] | нет | нет |
| PUT | api/yandex-disk/settings | YandexDiskController | UpdateSettings | backend/Controllers/YandexDiskController.cs:192 | [Authorize] | нет | нет |
| POST | api/yandex-disk/connection-test | YandexDiskController | TestConnection | backend/Controllers/YandexDiskController.cs:209 | [Authorize] | да | нет |
| GET | api/yandex-disk/crm-sync/settings | YandexDiskController | GetCrmSyncSettings | backend/Controllers/YandexDiskController.cs:231 | [Authorize] | нет | нет |
| GET | api/yandex-disk/crm-sync/preview | YandexDiskController | PreviewCrmSync | backend/Controllers/YandexDiskController.cs:240 | [Authorize] | да | нет |
| GET | api/yandex-disk/crm-sync/status | YandexDiskController | GetCrmSyncStatus | backend/Controllers/YandexDiskController.cs:249 | [Authorize] | да | нет |
| PUT | api/yandex-disk/crm-sync/settings | YandexDiskController | UpdateCrmSyncSettings | backend/Controllers/YandexDiskController.cs:302 | [Authorize] | да | нет |
| GET | api/yandex-disk/resources | YandexDiskController | List | backend/Controllers/YandexDiskController.cs:331 | [Authorize] | да | нет |
| GET | api/yandex-disk/connections/{id}/resources | YandexDiskController | List | backend/Controllers/YandexDiskController.cs:344 | [Authorize] | да | нет |
| POST | api/yandex-disk/folders | YandexDiskController | CreateFolder | backend/Controllers/YandexDiskController.cs:358 | [Authorize] | да | нет |
| POST | api/yandex-disk/connections/{id}/folders | YandexDiskController | CreateFolder | backend/Controllers/YandexDiskController.cs:375 | [Authorize] | да | нет |
| POST | api/yandex-disk/copy | YandexDiskController | Copy | backend/Controllers/YandexDiskController.cs:393 | [Authorize] | да | нет |
| POST | api/yandex-disk/connections/{id}/copy | YandexDiskController | Copy | backend/Controllers/YandexDiskController.cs:410 | [Authorize] | да | нет |
| POST | api/yandex-disk/move | YandexDiskController | Move | backend/Controllers/YandexDiskController.cs:428 | [Authorize] | да | нет |
| POST | api/yandex-disk/connections/{id}/move | YandexDiskController | Move | backend/Controllers/YandexDiskController.cs:445 | [Authorize] | да | нет |
| DELETE | api/yandex-disk/resources | YandexDiskController | Delete | backend/Controllers/YandexDiskController.cs:463 | [Authorize] | да | нет |
| DELETE | api/yandex-disk/connections/{id}/resources | YandexDiskController | Delete | backend/Controllers/YandexDiskController.cs:483 | [Authorize] | да | нет |
| POST | api/yandex-disk/upload | YandexDiskController | Upload | backend/Controllers/YandexDiskController.cs:504 | [Authorize] | да | docs/file-upload-limits-2026-06-10.md |
| POST | api/yandex-disk/connections/{id}/upload | YandexDiskController | Upload | backend/Controllers/YandexDiskController.cs:532 | [Authorize] | да | docs/file-upload-limits-2026-06-10.md |
| GET | api/yandex-disk/download | YandexDiskController | Download | backend/Controllers/YandexDiskController.cs:561 | [Authorize] | да | нет |
| GET | api/yandex-disk/connections/{id}/download | YandexDiskController | Download | backend/Controllers/YandexDiskController.cs:583 | [Authorize] | да | нет |
| GET | api/yandex-disk/text | YandexDiskController | GetText | backend/Controllers/YandexDiskController.cs:605 | [Authorize] | да | нет |
| GET | api/yandex-disk/connections/{id}/text | YandexDiskController | GetText | backend/Controllers/YandexDiskController.cs:614 | [Authorize] | да | нет |
| PUT | api/yandex-disk/text | YandexDiskController | SaveText | backend/Controllers/YandexDiskController.cs:623 | [Authorize] | да | нет |
| PUT | api/yandex-disk/connections/{id}/text | YandexDiskController | SaveText | backend/Controllers/YandexDiskController.cs:640 | [Authorize] | да | нет |
| POST | api/yandex-disk/crm/case-documents/{documentId:int}/upload | YandexDiskController | UploadCaseDocument | backend/Controllers/YandexDiskController.cs:658 | [Authorize] | да | нет |
| POST | api/yandex-disk/crm/tasks/{taskId:int}/files/{fileId:int}/upload | YandexDiskController | UploadTaskFile | backend/Controllers/YandexDiskController.cs:747 | [Authorize] | да | нет |
