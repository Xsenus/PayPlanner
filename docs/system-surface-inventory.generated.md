# Автоматическая инвентаризация поверхностей PayPlanner

Файл формируется командой `npm run audit:surface:update`. Ручное редактирование не допускается.
Признак теста означает, что имя контроллера, компонента или фонового сервиса найдено в тестовом контуре; детальная полнота сценариев проверяется последующими этапами roadmap.

## Сводка

- Разделов frontend: 49.
- Backend-контроллеров: 64.
- HTTP endpoints: 777; с найденной тестовой связью: 697.
- Реальных HTML-форм: 17; с найденной тестовой связью: 15.
- Фоновых сервисов: 26; с найденной тестовой связью: 24.

## Разделы frontend

| Раздел | Правило доступа |
| --- | --- |
| notifications | TAB_ACCESS_RULES |
| profile | TAB_ACCESS_RULES |
| calendar | TAB_ACCESS_RULES |
| tasks | TAB_ACCESS_RULES |
| tasksControl | TAB_ACCESS_RULES |
| tasksApprovals | TAB_ACCESS_RULES |
| tasksCalendar | TAB_ACCESS_RULES |
| notes | TAB_ACCESS_RULES |
| reports | TAB_ACCESS_RULES |
| calculator | TAB_ACCESS_RULES |
| legalEntities | TAB_ACCESS_RULES |
| cases | TAB_ACCESS_RULES |
| caseDetail | TAB_ACCESS_RULES |
| clients | TAB_ACCESS_RULES |
| contacts | TAB_ACCESS_RULES |
| contactsClients | TAB_ACCESS_RULES |
| contactsLegal | TAB_ACCESS_RULES |
| contactsExternal | TAB_ACCESS_RULES |
| clientDetail | TAB_ACCESS_RULES |
| accounts | TAB_ACCESS_RULES |
| accountsIncome | TAB_ACCESS_RULES |
| accountsExpense | TAB_ACCESS_RULES |
| installments | TAB_ACCESS_RULES |
| acts | TAB_ACCESS_RULES |
| actsIncome | TAB_ACCESS_RULES |
| actsExpense | TAB_ACCESS_RULES |
| actCreate | TAB_ACCESS_RULES |
| actDetail | TAB_ACCESS_RULES |
| payments | TAB_ACCESS_RULES |
| paymentsIncome | TAB_ACCESS_RULES |
| paymentsExpense | TAB_ACCESS_RULES |
| moneyBank | TAB_ACCESS_RULES |
| moneyCash | TAB_ACCESS_RULES |
| moneyCards | TAB_ACCESS_RULES |
| moneyEMoney | TAB_ACCESS_RULES |
| moneyTotal | TAB_ACCESS_RULES |
| contracts | TAB_ACCESS_RULES |
| contractCreate | TAB_ACCESS_RULES |
| contractDetail | TAB_ACCESS_RULES |
| companyProfile | TAB_ACCESS_RULES |
| users | TAB_ACCESS_RULES |
| userDetail | TAB_ACCESS_RULES |
| userActivity | TAB_ACCESS_RULES |
| roles | TAB_ACCESS_RULES |
| audit | TAB_ACCESS_RULES |
| yandexDisk | TAB_ACCESS_RULES |
| s3Storage | TAB_ACCESS_RULES |
| settings | TAB_ACCESS_RULES |
| dictionaries | TAB_ACCESS_RULES |

## Формы

| Компонент | Файл | Строка | Тест найден |
| --- | --- | --- | --- |
| InvoiceModal | src/components/Accounts/InvoiceModal.tsx | 4360 | да |
| ActModal | src/components/Acts/ActModal.tsx | 1505 | да |
| Login | src/components/Auth/Login.tsx | 73 | да |
| Register | src/components/Auth/Register.tsx | 94 | да |
| PaymentModal | src/components/Calendar/PaymentModal.tsx | 2896 | да |
| ClientDetail | src/components/Clients/ClientDetail.tsx | 6338 | да |
| ClientModal | src/components/Clients/ClientModal.tsx | 240 | да |
| ContractCreatePage | src/components/Contracts/ContractCreatePage.tsx | 293 | нет |
| ContractModal | src/components/Contracts/ContractModal.tsx | 366 | да |
| LegalEntityModal | src/components/LegalEntities/LegalEntityModal.tsx | 3820 | да |
| ClientPortalPage | src/components/Public/ClientPortalPage.tsx | 223 | да |
| RoleModal | src/components/Roles/RoleModal.tsx | 121 | нет |
| S3Storage | src/components/S3Storage/S3Storage.tsx | 773 | да |
| FormPromptModal | src/components/ui/FormPromptModal.tsx | 65 | да |
| TextPromptModal | src/components/ui/TextPromptModal.tsx | 55 | да |
| YandexDisk | src/components/YandexDisk/YandexDisk.tsx | 722 | да |
| DialogContext | src/contexts/DialogContext.tsx | 213 | да |

## Фоновые сервисы

| Сервис | Файл | Строка | Тест найден |
| --- | --- | --- | --- |
| CalendarSyncLogRetentionService | backend/Services/CalendarSyncLogRetentionService.cs | 6 | да |
| CalendarSyncSchedulerService | backend/Services/CalendarSyncSchedulerService.cs | 10 | да |
| CaseProceduralDeadlineReminderNotificationService | backend/Services/CaseProceduralDeadlineReminderNotificationService.cs | 8 | да |
| ContractExpirationNotificationService | backend/Services/ContractExpirationNotificationService.cs | 8 | да |
| DatabaseBackupSchedulerService | backend/Services/DatabaseBackupSchedulerService.cs | 3 | да |
| ExternalWorkOrderApprovalExpirationService | backend/Services/ExternalWorkOrderApprovalNotifications.cs | 116 | да |
| GoogleCalendarBackgroundSyncService | backend/Services/GoogleCalendarBackgroundSyncService.cs | 11 | да |
| InvoicePaymentStateReconciliationService | backend/Services/InvoicePaymentStateReconciliationService.cs | 7 | да |
| ClientCaseCourtSyncSchedulerService | backend/Services/LegalIntegrations/ClientCaseCourtSyncSchedulerService.cs | 14 | да |
| CourtSyncMetricsRollupService | backend/Services/LegalIntegrations/CourtSyncMetricsRollupService.cs | 8 | да |
| MessengerDocumentOutboxWorker | backend/Services/MessengerDocumentOutboxService.cs | 274 | нет |
| MoneyBalanceReconciliationSchedulerService | backend/Services/MoneyBalanceReconciliationSchedulerService.cs | 10 | да |
| MaxOutboxWorker | backend/Services/Notifications/MaxOutboxService.cs | 353 | нет |
| NotificationDeliveryRetentionService | backend/Services/Notifications/NotificationDeliveryRetentionService.cs | 6 | да |
| NotificationRetrySchedulerService | backend/Services/Notifications/NotificationRetrySchedulerService.cs | 8 | да |
| SmsChannelMonitoringService | backend/Services/Notifications/SmsChannelMonitoringService.cs | 122 | да |
| SmsDeliveryStatusReconciliationService | backend/Services/Notifications/SmsDeliveryStatusReconciliationService.cs | 12 | да |
| StickyNoteReminderNotificationService | backend/Services/Notifications/StickyNoteReminderNotificationService.cs | 7 | да |
| TaskJournalReminderNotificationService | backend/Services/Notifications/TaskJournalReminderNotificationService.cs | 9 | да |
| TelegramBotPollingService | backend/Services/Notifications/TelegramBotPollingService.cs | 3 | да |
| PaymentAdvanceReviewMonitoringService | backend/Services/PaymentAdvanceReviewMonitoringService.cs | 8 | да |
| PaymentStatusUpdater | backend/Services/PaymentStatusUpdater.cs | 12 | да |
| TaskJournalAutomationSchedulerService | backend/Services/TaskJournalAutomationSchedulerService.cs | 10 | да |
| UserActivityRetentionService | backend/Services/UserActivity/UserActivityRetentionService.cs | 7 | да |
| YandexCalendarBackgroundSyncService | backend/Services/YandexCalendarBackgroundSyncService.cs | 11 | да |
| YandexDiskCrmSyncSchedulerService | backend/Services/YandexDiskCrmSyncSchedulerService.cs | 12 | да |

## Контроллеры

| Контроллер | Файл | Endpoints | Правило доступа | Тест найден |
| --- | --- | --- | --- | --- |
| AccountsController | backend/Controllers/AccountsController.cs | 3 | [Authorize] | да |
| ActsController | backend/Controllers/ActsController.cs | 16 | [Authorize] | да |
| AdminUsersController | backend/Controllers/AdminUsersController.cs | 8 | [Authorize(Policy = "Admin")] | да |
| AppVersionController | backend/Controllers/AppVersionController.cs | 7 | [Authorize] | да |
| AuthController | backend/Controllers/AuthController.cs | 4 | не задано | нет |
| AuthProvidersAdminController | backend/Controllers/AuthProvidersAdminController.cs | 8 | [Authorize(Policy = "Admin")] | да |
| BanksController | backend/Controllers/BanksController.cs | 5 | [Authorize] | да |
| CalendarSyncController | backend/Controllers/CalendarSyncController.cs | 5 | [Authorize] | да |
| CasesController | backend/Controllers/CasesController.cs | 5 | [Authorize] | да |
| CashOrdersController | backend/Controllers/CashOrdersController.cs | 19 | [Authorize] | да |
| ClientBalancesController | backend/Controllers/ClientBalancesController.cs | 3 | [Authorize] | да |
| ClientPassportController | backend/Controllers/ClientPassportController.cs | 2 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет |
| ClientPortalAdminController | backend/Controllers/ClientPortalAdminController.cs | 7 | [Authorize(Policy = "Admin")] | нет |
| ClientPortalController | backend/Controllers/ClientPortalController.cs | 10 | не задано | да |
| ClientReferenceDataController | backend/Controllers/ClientReferenceDataController.cs | 3 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет |
| ClientsController | backend/Controllers/ClientsController.cs | 12 | [Authorize] | да |
| ContactsController | backend/Controllers/ContactsController.cs | 7 | [Authorize] | да |
| ContractsController | backend/Controllers/ContractsController.cs | 23 | [Authorize] | да |
| DadataIntegrationSettingsController | backend/Controllers/DadataIntegrationSettingsController.cs | 2 | [Authorize] | да |
| DatabaseAdminController | backend/Controllers/DatabaseAdminController.cs | 15 | [Authorize(Policy = "Admin")] | да |
| DictionariesAdminController | backend/Controllers/DictionariesAdminController.cs | 74 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | да |
| DictionariesReadController | backend/Controllers/DictionariesReadController.cs | 18 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| ElectronicSignaturesController | backend/Controllers/ElectronicSignaturesController.cs | 6 | [Authorize(Policy = "Admin")] | да |
| ExternalAuthController | backend/Controllers/ExternalAuthController.cs | 8 | не задано | нет |
| FeatureFlagsController | backend/Controllers/FeatureFlagsController.cs | 2 | не задано | да |
| FormRequirementsController | backend/Controllers/FormRequirementsController.cs | 3 | [Authorize] | да |
| GoogleCalendarController | backend/Controllers/GoogleCalendarController.cs | 8 | не задано | нет |
| HealthController | backend/Controllers/HealthController.cs | 1 | не задано | да |
| InstallmentsController | backend/Controllers/InstallmentsController.cs | 1 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Accounts)] | нет |
| InvoicesController | backend/Controllers/InvoicesController.cs | 31 | [Authorize] | да |
| LegalEntitiesController | backend/Controllers/LegalEntitiesController.cs | 17 | [Authorize] | да |
| LegalEntityBalancesController | backend/Controllers/LegalEntityBalancesController.cs | 3 | [Authorize] | да |
| MaxMessengerController | backend/Controllers/MaxMessengerController.cs | 8 | [Authorize] | нет |
| MessengerDocumentDeliveriesController | backend/Controllers/MessengerDocumentDeliveriesController.cs | 9 | [Authorize(Policy = "Admin")] | да |
| MoneyAccountsController | backend/Controllers/MoneyAccountsController.cs | 5 | [Authorize] | да |
| MoneyOperationsController | backend/Controllers/MoneyOperationsController.cs | 25 | [Authorize] | да |
| NotificationInboxController | backend/Controllers/NotificationInboxController.cs | 11 | [Authorize] | да |
| NotificationsController | backend/Controllers/NotificationsController.cs | 24 | [Authorize] | да |
| ObjectAuditController | backend/Controllers/ObjectAuditController.cs | 4 | [Authorize] | да |
| PaymentsController | backend/Controllers/PaymentsController.cs | 20 | [Authorize] | да |
| PowerOfAttorneyController | backend/Controllers/PowerOfAttorneyController.cs | 4 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| ProfileController | backend/Controllers/ProfileController.cs | 6 | [Authorize] | да |
| PublicWorkOrderApprovalController | backend/Controllers/PublicWorkOrderApprovalController.cs | 4 | не задано | нет |
| PushSubscriptionsController | backend/Controllers/PushSubscriptionsController.cs | 6 | [Authorize] | да |
| ReportsController | backend/Controllers/ReportsController.cs | 39 | [Authorize] | да |
| RolePermissionsController | backend/Controllers/RolePermissionsController.cs | 6 | [Authorize] | да |
| RolesController | backend/Controllers/RolesController.cs | 7 | [Authorize] | нет |
| S3StorageController | backend/Controllers/S3StorageController.cs | 13 | [Authorize] | да |
| StatsController | backend/Controllers/StatsController.cs | 1 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | нет |
| StickyNotesController | backend/Controllers/StickyNotesController.cs | 14 | [Authorize] | да |
| SystemJobsController | backend/Controllers/SystemJobsController.cs | 2 | [Authorize(Policy = "Admin")] | да |
| TasksController | backend/Controllers/TasksController.cs | 40 | [Authorize] | да |
| UserActivityController | backend/Controllers/UserActivityController.cs | 10 | [Authorize] | да |
| UsersController | backend/Controllers/UsersController.cs | 11 | [Authorize(Policy = "Admin")] | да |
| UserSettingsController | backend/Controllers/UserSettingsController.cs | 4 | [Authorize] | да |
| CasesV1Controller | backend/Controllers/V1/CasesV1Controller.cs | 5 | [Authorize] | да |
| ClientsV1Controller | backend/Controllers/V1/ClientsV1Controller.cs | 5 | [Authorize] | да |
| PaymentsV1Controller | backend/Controllers/V1/PaymentsV1Controller.cs | 5 | [Authorize] | да |
| CasesV2Controller | backend/Controllers/V2/CasesV2Controller.cs | 98 | [Authorize] | да |
| ClientsV2Controller | backend/Controllers/V2/ClientsV2Controller.cs | 5 | [Authorize] | да |
| PaymentsV2Controller | backend/Controllers/V2/PaymentsV2Controller.cs | 7 | [Authorize] | да |
| StatsV2Controller | backend/Controllers/V2/StatsV2Controller.cs | 2 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | нет |
| YandexCalendarController | backend/Controllers/YandexCalendarController.cs | 7 | не задано | нет |
| YandexDiskController | backend/Controllers/YandexDiskController.cs | 34 | [Authorize] | да |

## Все HTTP endpoints

| Метод | Маршрут | Контроллер | Действие | Файл:строка | Правило доступа | Тест контроллера |
| --- | --- | --- | --- | --- | --- | --- |
| GET | api/[controller]/registry | AccountsController | GetRegistry | backend/Controllers/AccountsController.cs:32 | [Authorize] | да |
| GET | api/[controller]/payables-dashboard | AccountsController | GetPayablesDashboard | backend/Controllers/AccountsController.cs:495 | [Authorize] | да |
| GET | api/[controller] | AccountsController | Get | backend/Controllers/AccountsController.cs:638 | [Authorize] | да |
| GET | api/[controller] | ActsController | Get | backend/Controllers/ActsController.cs:190 | [Authorize] | да |
| GET | api/[controller]/{id:int} | ActsController | GetById | backend/Controllers/ActsController.cs:419 | [Authorize] | да |
| POST | api/[controller]/{id:int}/send-to-signing | ActsController | SendToSigning | backend/Controllers/ActsController.cs:437 | [Authorize] | да |
| POST | api/[controller]/{id:int}/mark-signed | ActsController | MarkSigned | backend/Controllers/ActsController.cs:462 | [Authorize] | да |
| POST | api/[controller]/{id:int}/mark-not-signed | ActsController | MarkNotSigned | backend/Controllers/ActsController.cs:489 | [Authorize] | да |
| POST | api/[controller]/{id:int}/workflow-status | ActsController | UpdateWorkflowStatus | backend/Controllers/ActsController.cs:515 | [Authorize] | да |
| POST | api/[controller]/{id:int}/cancel | ActsController | Cancel | backend/Controllers/ActsController.cs:608 | [Authorize] | да |
| POST | api/[controller]/{id:int}/correction | ActsController | CreateCorrection | backend/Controllers/ActsController.cs:650 | [Authorize] | да |
| GET | api/[controller]/summary | ActsController | GetSummary | backend/Controllers/ActsController.cs:762 | [Authorize] | да |
| GET | api/[controller]/responsibles | ActsController | GetResponsibles | backend/Controllers/ActsController.cs:898 | [Authorize] | да |
| POST | api/[controller] | ActsController | Create | backend/Controllers/ActsController.cs:914 | [Authorize] | да |
| PUT | api/[controller]/{id:int} | ActsController | Update | backend/Controllers/ActsController.cs:992 | [Authorize] | да |
| DELETE | api/[controller]/{id:int} | ActsController | Delete | backend/Controllers/ActsController.cs:1083 | [Authorize] | да |
| GET | api/[controller]/{id:int}/document | ActsController | GetDocument | backend/Controllers/ActsController.cs:1116 | [Authorize] | да |
| POST | api/[controller]/{id:int}/yandex-disk | ActsController | UploadDocumentToYandexDisk | backend/Controllers/ActsController.cs:1168 | [Authorize] | да |
| POST | api/[controller]/{id:int}/send-email | ActsController | SendActEmail | backend/Controllers/ActsController.cs:1296 | [Authorize] | да |
| GET | api/auth/admin/users | AdminUsersController | GetUsers | backend/Controllers/AdminUsersController.cs:38 | [Authorize(Policy = "Admin")] | да |
| GET | api/auth/admin/users/{id:int} | AdminUsersController | GetUser | backend/Controllers/AdminUsersController.cs:42 | [Authorize(Policy = "Admin")] | да |
| POST | api/auth/admin/users | AdminUsersController | CreateUser | backend/Controllers/AdminUsersController.cs:49 | [Authorize(Policy = "Admin")] | да |
| PUT | api/auth/admin/users/{id:int} | AdminUsersController | UpdateUser | backend/Controllers/AdminUsersController.cs:73 | [Authorize(Policy = "Admin")] | да |
| DELETE | api/auth/admin/users/{id:int} | AdminUsersController | DeleteUser | backend/Controllers/AdminUsersController.cs:116 | [Authorize(Policy = "Admin")] | да |
| POST | api/auth/admin/users/{id:int}/approve | AdminUsersController | ApproveUser | backend/Controllers/AdminUsersController.cs:146 | [Authorize(Policy = "Admin")] | да |
| POST | api/auth/admin/users/{id:int}/reject | AdminUsersController | RejectUser | backend/Controllers/AdminUsersController.cs:159 | [Authorize(Policy = "Admin")] | да |
| GET | api/auth/admin/roles | AdminUsersController | GetRoles | backend/Controllers/AdminUsersController.cs:192 | [Authorize(Policy = "Admin")] | да |
| GET | api/app-version/latest | AppVersionController | GetLatest | backend/Controllers/AppVersionController.cs:48 | [Authorize] | да |
| GET | api/app-version/history | AppVersionController | GetHistory | backend/Controllers/AppVersionController.cs:81 | [Authorize] | да |
| POST | api/app-version/mark-seen | AppVersionController | MarkSeen | backend/Controllers/AppVersionController.cs:88 | [Authorize] | да |
| GET | api/app-version/admin/releases | AppVersionController | GetAdminReleases | backend/Controllers/AppVersionController.cs:123 | [Authorize] | да |
| POST | api/app-version/admin/releases | AppVersionController | CreateRelease | backend/Controllers/AppVersionController.cs:142 | [Authorize] | да |
| PUT | api/app-version/admin/releases/{id:int} | AppVersionController | UpdateRelease | backend/Controllers/AppVersionController.cs:187 | [Authorize] | да |
| DELETE | api/app-version/admin/releases/{id:int} | AppVersionController | DeleteRelease | backend/Controllers/AppVersionController.cs:229 | [Authorize] | да |
| POST | api/[controller]/login | AuthController | Login | backend/Controllers/AuthController.cs:16 | не задано | нет |
| POST | api/[controller]/refresh | AuthController | RefreshSession | backend/Controllers/AuthController.cs:56 | не задано | нет |
| POST | api/[controller]/register | AuthController | Register | backend/Controllers/AuthController.cs:89 | не задано | нет |
| GET | api/[controller]/me | AuthController | Me | backend/Controllers/AuthController.cs:126 | не задано | нет |
| GET | api/auth/admin/providers | AuthProvidersAdminController | GetSettings | backend/Controllers/AuthProvidersAdminController.cs:37 | [Authorize(Policy = "Admin")] | да |
| PUT | api/auth/admin/providers | AuthProvidersAdminController | UpdateSettings | backend/Controllers/AuthProvidersAdminController.cs:41 | [Authorize(Policy = "Admin")] | да |
| PUT | api/auth/admin/providers/google | AuthProvidersAdminController | UpdateGoogleSettings | backend/Controllers/AuthProvidersAdminController.cs:56 | [Authorize(Policy = "Admin")] | да |
| PUT | api/auth/admin/providers/vk | AuthProvidersAdminController | UpdateVkSettings | backend/Controllers/AuthProvidersAdminController.cs:71 | [Authorize(Policy = "Admin")] | да |
| PUT | api/auth/admin/providers/yandex | AuthProvidersAdminController | UpdateYandexSettings | backend/Controllers/AuthProvidersAdminController.cs:86 | [Authorize(Policy = "Admin")] | да |
| PUT | api/auth/admin/providers/telegram/bot | AuthProvidersAdminController | UpdateTelegramBotSettings | backend/Controllers/AuthProvidersAdminController.cs:101 | [Authorize(Policy = "Admin")] | да |
| PUT | api/auth/admin/providers/telegram/widget | AuthProvidersAdminController | UpdateTelegramWidgetSettings | backend/Controllers/AuthProvidersAdminController.cs:116 | [Authorize(Policy = "Admin")] | да |
| POST | api/auth/admin/providers/telegram/sync | AuthProvidersAdminController | SyncTelegram | backend/Controllers/AuthProvidersAdminController.cs:131 | [Authorize(Policy = "Admin")] | да |
| GET | api/banks | BanksController | GetBanks | backend/Controllers/BanksController.cs:31 | [Authorize] | да |
| GET | api/banks/{id:int} | BanksController | GetBank | backend/Controllers/BanksController.cs:62 | [Authorize] | да |
| POST | api/banks | BanksController | CreateBank | backend/Controllers/BanksController.cs:79 | [Authorize] | да |
| PUT | api/banks/{id:int} | BanksController | UpdateBank | backend/Controllers/BanksController.cs:115 | [Authorize] | да |
| DELETE | api/banks/{id:int} | BanksController | DeleteBank | backend/Controllers/BanksController.cs:154 | [Authorize] | да |
| GET | api/calendar-sync/providers | CalendarSyncController | GetProviders | backend/Controllers/CalendarSyncController.cs:55 | [Authorize] | да |
| GET | api/calendar-sync/readiness | CalendarSyncController | GetReadiness | backend/Controllers/CalendarSyncController.cs:64 | [Authorize] | да |
| GET | api/calendar-sync/settings | CalendarSyncController | GetSettings | backend/Controllers/CalendarSyncController.cs:74 | [Authorize] | да |
| PUT | api/calendar-sync/settings | CalendarSyncController | UpdateSettings | backend/Controllers/CalendarSyncController.cs:78 | [Authorize] | да |
| GET | api/calendar-sync/logs | CalendarSyncController | GetLogs | backend/Controllers/CalendarSyncController.cs:137 | [Authorize] | да |
| GET | api/[controller] | CasesController | Get | backend/Controllers/CasesController.cs:29 | [Authorize] | да |
| GET | api/[controller]/{id:int} | CasesController | GetById | backend/Controllers/CasesController.cs:58 | [Authorize] | да |
| POST | api/[controller] | CasesController | Create | backend/Controllers/CasesController.cs:82 | [Authorize] | да |
| PUT | api/[controller]/{id:int} | CasesController | Update | backend/Controllers/CasesController.cs:105 | [Authorize] | да |
| DELETE | api/[controller]/{id:int} | CasesController | Delete | backend/Controllers/CasesController.cs:144 | [Authorize] | да |
| GET | api/cash-orders/cashbook | CashOrdersController | GetCashBook | backend/Controllers/CashOrdersController.cs:40 | [Authorize] | да |
| GET | api/cash-orders/cashbook/print | CashOrdersController | PrintCashBook | backend/Controllers/CashOrdersController.cs:76 | [Authorize] | да |
| GET | api/cash-orders/next-number | CashOrdersController | GetNextNumber | backend/Controllers/CashOrdersController.cs:113 | [Authorize] | да |
| GET | api/cash-orders/paged | CashOrdersController | GetPaged | backend/Controllers/CashOrdersController.cs:135 | [Authorize] | да |
| GET | api/cash-orders/register | CashOrdersController | GetRegister | backend/Controllers/CashOrdersController.cs:181 | [Authorize] | да |
| GET | api/cash-orders/register/print | CashOrdersController | PrintRegister | backend/Controllers/CashOrdersController.cs:251 | [Authorize] | да |
| GET | api/cash-orders/inventories | CashOrdersController | GetInventories | backend/Controllers/CashOrdersController.cs:316 | [Authorize] | да |
| POST | api/cash-orders/inventories | CashOrdersController | CreateInventory | backend/Controllers/CashOrdersController.cs:372 | [Authorize] | да |
| GET | api/cash-orders/{id:int} | CashOrdersController | GetById | backend/Controllers/CashOrdersController.cs:445 | [Authorize] | да |
| POST | api/cash-orders/from-payment/{paymentId:int} | CashOrdersController | CreateFromPayment | backend/Controllers/CashOrdersController.cs:470 | [Authorize] | да |
| POST | api/cash-orders/from-money-operation/{operationId:int} | CashOrdersController | CreateFromMoneyOperation | backend/Controllers/CashOrdersController.cs:542 | [Authorize] | да |
| POST | api/cash-orders | CashOrdersController | Create | backend/Controllers/CashOrdersController.cs:603 | [Authorize] | да |
| PUT | api/cash-orders/{id:int} | CashOrdersController | Update | backend/Controllers/CashOrdersController.cs:640 | [Authorize] | да |
| DELETE | api/cash-orders/{id:int} | CashOrdersController | Delete | backend/Controllers/CashOrdersController.cs:695 | [Authorize] | да |
| POST | api/cash-orders/{id:int}/post | CashOrdersController | Post | backend/Controllers/CashOrdersController.cs:728 | [Authorize] | да |
| POST | api/cash-orders/{id:int}/cancel | CashOrdersController | Cancel | backend/Controllers/CashOrdersController.cs:772 | [Authorize] | да |
| POST | api/cash-orders/{id:int}/copy | CashOrdersController | Copy | backend/Controllers/CashOrdersController.cs:808 | [Authorize] | да |
| GET | api/cash-orders/{id:int}/print | CashOrdersController | Print | backend/Controllers/CashOrdersController.cs:859 | [Authorize] | да |
| GET | api/cash-orders/{id:int}/pdf | CashOrdersController | DownloadPdf | backend/Controllers/CashOrdersController.cs:888 | [Authorize] | да |
| GET | api/clients/{clientId:int}/balance | ClientBalancesController | GetSummary | backend/Controllers/ClientBalancesController.cs:57 | [Authorize] | да |
| GET | api/clients/{clientId:int}/balance/transactions | ClientBalancesController | GetTransactions | backend/Controllers/ClientBalancesController.cs:78 | [Authorize] | да |
| POST | api/clients/{clientId:int}/balance/apply | ClientBalancesController | ApplyCredit | backend/Controllers/ClientBalancesController.cs:116 | [Authorize] | да |
| GET | api/clients/{clientId:int}/passport | ClientPassportController | Get | backend/Controllers/ClientPassportController.cs:23 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет |
| PUT | api/clients/{clientId:int}/passport | ClientPassportController | Upsert | backend/Controllers/ClientPassportController.cs:40 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет |
| POST | api/portal/admin/access | ClientPortalAdminController | SaveAccess | backend/Controllers/ClientPortalAdminController.cs:13 | [Authorize(Policy = "Admin")] | нет |
| GET | api/portal/admin/accounts/{accountId:int} | ClientPortalAdminController | GetAccount | backend/Controllers/ClientPortalAdminController.cs:38 | [Authorize(Policy = "Admin")] | нет |
| GET | api/portal/admin/cases/{caseId:int}/accounts | ClientPortalAdminController | GetCaseAccounts | backend/Controllers/ClientPortalAdminController.cs:52 | [Authorize(Policy = "Admin")] | нет |
| GET | api/portal/admin/cases/{caseId:int}/audit | ClientPortalAdminController | GetCaseAudit | backend/Controllers/ClientPortalAdminController.cs:65 | [Authorize(Policy = "Admin")] | нет |
| POST | api/portal/admin/accounts/{accountId:int}/login | ClientPortalAdminController | SendLogin | backend/Controllers/ClientPortalAdminController.cs:78 | [Authorize(Policy = "Admin")] | нет |
| DELETE | api/portal/admin/accounts/{accountId:int} | ClientPortalAdminController | Revoke | backend/Controllers/ClientPortalAdminController.cs:91 | [Authorize(Policy = "Admin")] | нет |
| DELETE | api/portal/admin/accounts/{accountId:int}/cases/{caseId:int} | ClientPortalAdminController | RevokeCaseAccess | backend/Controllers/ClientPortalAdminController.cs:119 | [Authorize(Policy = "Admin")] | нет |
| POST | api/portal/auth/{challengeId:guid}/preview | ClientPortalController | Preview | backend/Controllers/ClientPortalController.cs:20 | не задано | да |
| POST | api/portal/auth/{challengeId:guid}/verify | ClientPortalController | Verify | backend/Controllers/ClientPortalController.cs:37 | не задано | да |
| GET | api/portal/home | ClientPortalController | Home | backend/Controllers/ClientPortalController.cs:67 | не задано | да |
| GET | api/portal/cases/{casePublicId:guid} | ClientPortalController | GetCase | backend/Controllers/ClientPortalController.cs:89 | не задано | да |
| GET | api/portal/profile | ClientPortalController | Profile | backend/Controllers/ClientPortalController.cs:109 | не задано | да |
| POST | api/portal/documents/{documentPublicId:guid}/grant | ClientPortalController | CreateDocumentGrant | backend/Controllers/ClientPortalController.cs:119 | не задано | да |
| POST | api/portal/documents/{documentPublicId:guid}/download | ClientPortalController | DownloadDocument | backend/Controllers/ClientPortalController.cs:130 | не задано | да |
| POST | api/portal/logout | ClientPortalController | Logout | backend/Controllers/ClientPortalController.cs:156 | не задано | да |
| POST | api/portal/max/link | ClientPortalController | CreateMaxLink | backend/Controllers/ClientPortalController.cs:179 | не задано | да |
| DELETE | api/portal/max/link | ClientPortalController | DeleteMaxLink | backend/Controllers/ClientPortalController.cs:196 | не задано | да |
| GET | api/client-reference-data/okin | ClientReferenceDataController | GetOkin | backend/Controllers/ClientReferenceDataController.cs:23 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет |
| GET | api/client-reference-data/okso | ClientReferenceDataController | GetOkso | backend/Controllers/ClientReferenceDataController.cs:32 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет |
| GET | api/client-reference-data/okpdtr | ClientReferenceDataController | GetOkpdtr | backend/Controllers/ClientReferenceDataController.cs:41 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | нет |
| GET | api/[controller] | ClientsController | GetAll | backend/Controllers/ClientsController.cs:229 | [Authorize] | да |
| GET | api/[controller]/{id:int} | ClientsController | GetById | backend/Controllers/ClientsController.cs:279 | [Authorize] | да |
| GET | api/[controller]/{id:int}/stats | ClientsController | GetStats | backend/Controllers/ClientsController.cs:314 | [Authorize] | да |
| GET | api/[controller]/{id:int}/case-finance-summaries | ClientsController | GetCaseFinanceSummaries | backend/Controllers/ClientsController.cs:412 | [Authorize] | да |
| GET | api/[controller]/{id:int}/case-expense-details | ClientsController | GetCaseExpenseDetails | backend/Controllers/ClientsController.cs:537 | [Authorize] | да |
| POST | api/[controller]/dadata/clean-address | ClientsController | CleanAddress | backend/Controllers/ClientsController.cs:852 | [Authorize] | да |
| POST | api/[controller]/dadata/suggest-address | ClientsController | SuggestAddress | backend/Controllers/ClientsController.cs:940 | [Authorize] | да |
| POST | api/[controller] | ClientsController | Create | backend/Controllers/ClientsController.cs:966 | [Authorize] | да |
| PUT | api/[controller]/{id:int} | ClientsController | Update | backend/Controllers/ClientsController.cs:1049 | [Authorize] | да |
| DELETE | api/[controller]/{id:int} | ClientsController | Delete | backend/Controllers/ClientsController.cs:1358 | [Authorize] | да |
| POST | api/[controller]/bulk-delete | ClientsController | BulkDelete | backend/Controllers/ClientsController.cs:1393 | [Authorize] | да |
| POST | api/[controller]/change-responsible | ClientsController | ChangeResponsible | backend/Controllers/ClientsController.cs:1460 | [Authorize] | да |
| GET | api/contacts | ContactsController | GetAll | backend/Controllers/ContactsController.cs:46 | [Authorize] | да |
| PUT | api/contacts/clients/{id:int} | ContactsController | UpdateClientContact | backend/Controllers/ContactsController.cs:138 | [Authorize] | да |
| POST | api/contacts/external | ContactsController | CreateExternalContact | backend/Controllers/ContactsController.cs:209 | [Authorize] | да |
| PUT | api/contacts/external/{id:int} | ContactsController | UpdateExternalContact | backend/Controllers/ContactsController.cs:274 | [Authorize] | да |
| DELETE | api/contacts/external/{id:int} | ContactsController | DeleteExternalContact | backend/Controllers/ContactsController.cs:342 | [Authorize] | да |
| GET | api/contacts/exports/yealink | ContactsController | DownloadYealinkXml | backend/Controllers/ContactsController.cs:368 | [Authorize] | да |
| POST | api/contacts/imports/yealink | ContactsController | ImportYealinkXml | backend/Controllers/ContactsController.cs:416 | [Authorize] | да |
| GET | api/[controller] | ContractsController | Get | backend/Controllers/ContractsController.cs:307 | [Authorize] | да |
| GET | api/[controller]/{id:int} | ContractsController | GetById | backend/Controllers/ContractsController.cs:338 | [Authorize] | да |
| GET | api/[controller]/{id:int}/document | ContractsController | GetDocument | backend/Controllers/ContractsController.cs:352 | [Authorize] | да |
| GET | api/[controller]/{id:int}/termination-agreement | ContractsController | GetTerminationAgreement | backend/Controllers/ContractsController.cs:396 | [Authorize] | да |
| POST | api/[controller]/{id:int}/yandex-disk | ContractsController | UploadDocumentToYandexDisk | backend/Controllers/ContractsController.cs:441 | [Authorize] | да |
| POST | api/[controller]/{id:int}/send-email | ContractsController | SendEmail | backend/Controllers/ContractsController.cs:559 | [Authorize] | да |
| GET | api/[controller]/document-template-settings | ContractsController | GetDocumentTemplateSettings | backend/Controllers/ContractsController.cs:629 | [Authorize] | да |
| PUT | api/[controller]/document-template-settings | ContractsController | UpsertDocumentTemplateSettings | backend/Controllers/ContractsController.cs:633 | [Authorize] | да |
| POST | api/[controller]/{id:int}/send-to-signing | ContractsController | SendToSigning | backend/Controllers/ContractsController.cs:662 | [Authorize] | да |
| GET | api/[controller]/{id:int}/termination-check | ContractsController | GetTerminationCheck | backend/Controllers/ContractsController.cs:689 | [Authorize] | да |
| POST | api/[controller]/{id:int}/mark-concluded | ContractsController | MarkConcluded | backend/Controllers/ContractsController.cs:698 | [Authorize] | да |
| POST | api/[controller]/{id:int}/mark-not-signed | ContractsController | MarkNotSigned | backend/Controllers/ContractsController.cs:727 | [Authorize] | да |
| POST | api/[controller]/{id:int}/status | ContractsController | UpdateStatus | backend/Controllers/ContractsController.cs:755 | [Authorize] | да |
| POST | api/[controller]/{id:int}/terminate | ContractsController | Terminate | backend/Controllers/ContractsController.cs:871 | [Authorize] | да |
| POST | api/[controller]/{id:int}/cancel | ContractsController | Cancel | backend/Controllers/ContractsController.cs:926 | [Authorize] | да |
| GET | api/[controller]/numbering-settings | ContractsController | GetNumberingSettings | backend/Controllers/ContractsController.cs:974 | [Authorize] | да |
| PUT | api/[controller]/numbering-settings | ContractsController | UpsertNumberingSettings | backend/Controllers/ContractsController.cs:978 | [Authorize] | да |
| GET | api/[controller]/rules-settings | ContractsController | GetRulesSettings | backend/Controllers/ContractsController.cs:994 | [Authorize] | да |
| PUT | api/[controller]/rules-settings | ContractsController | UpsertRulesSettings | backend/Controllers/ContractsController.cs:998 | [Authorize] | да |
| GET | api/[controller]/next-number | ContractsController | GetNextNumber | backend/Controllers/ContractsController.cs:1010 | [Authorize] | да |
| POST | api/[controller] | ContractsController | Create | backend/Controllers/ContractsController.cs:1027 | [Authorize] | да |
| PUT | api/[controller]/{id:int} | ContractsController | Update | backend/Controllers/ContractsController.cs:1079 | [Authorize] | да |
| DELETE | api/[controller]/{id:int} | ContractsController | Delete | backend/Controllers/ContractsController.cs:1149 | [Authorize] | да |
| GET | api/integrations/dadata/settings | DadataIntegrationSettingsController | Get | backend/Controllers/DadataIntegrationSettingsController.cs:26 | [Authorize] | да |
| PUT | api/integrations/dadata/settings | DadataIntegrationSettingsController | Update | backend/Controllers/DadataIntegrationSettingsController.cs:61 | [Authorize] | да |
| GET | api/database/status | DatabaseAdminController | GetStatus | backend/Controllers/DatabaseAdminController.cs:55 | [Authorize(Policy = "Admin")] | да |
| GET | api/database/backups | DatabaseAdminController | GetBackupsAsync | backend/Controllers/DatabaseAdminController.cs:67 | [Authorize(Policy = "Admin")] | да |
| PUT | api/database/backups/settings | DatabaseAdminController | UpdateBackupSettingsAsync | backend/Controllers/DatabaseAdminController.cs:74 | [Authorize(Policy = "Admin")] | да |
| POST | api/database/backups | DatabaseAdminController | CreateBackupAsync | backend/Controllers/DatabaseAdminController.cs:101 | [Authorize(Policy = "Admin")] | да |
| POST | api/database/backups/s3-uploads/{uploadResultId}/retry | DatabaseAdminController | RetryBackupS3UploadAsync | backend/Controllers/DatabaseAdminController.cs:121 | [Authorize(Policy = "Admin")] | да |
| POST | api/database/backups/yandex-disk-uploads/{uploadResultId}/retry | DatabaseAdminController | RetryBackupYandexDiskUploadAsync | backend/Controllers/DatabaseAdminController.cs:142 | [Authorize(Policy = "Admin")] | да |
| DELETE | api/database/backups/file | DatabaseAdminController | DeleteBackupAsync | backend/Controllers/DatabaseAdminController.cs:163 | [Authorize(Policy = "Admin")] | да |
| DELETE | api/database/backups/all | DatabaseAdminController | DeleteAllBackupsAsync | backend/Controllers/DatabaseAdminController.cs:185 | [Authorize(Policy = "Admin")] | да |
| GET | api/database/backups/download | DatabaseAdminController | DownloadBackupAsync | backend/Controllers/DatabaseAdminController.cs:198 | [Authorize(Policy = "Admin")] | да |
| POST | api/database/import/sqlite | DatabaseAdminController | ImportSqliteAsync | backend/Controllers/DatabaseAdminController.cs:214 | [Authorize(Policy = "Admin")] | да |
| POST | api/database/import/spreadsheets/preview | DatabaseAdminController | PreviewSpreadsheetImportAsync | backend/Controllers/DatabaseAdminController.cs:254 | [Authorize(Policy = "Admin")] | да |
| POST | api/database/import/spreadsheets/apply | DatabaseAdminController | ApplySpreadsheetImportAsync | backend/Controllers/DatabaseAdminController.cs:279 | [Authorize(Policy = "Admin")] | да |
| GET | api/database/import/spreadsheets/batches | DatabaseAdminController | GetSpreadsheetImportBatchesAsync | backend/Controllers/DatabaseAdminController.cs:310 | [Authorize(Policy = "Admin")] | да |
| GET | api/database/import/spreadsheets/batches/{batchId:int} | DatabaseAdminController | GetSpreadsheetImportBatchAsync | backend/Controllers/DatabaseAdminController.cs:336 | [Authorize(Policy = "Admin")] | да |
| POST | api/database/import/spreadsheets/batches/{batchId:int}/rollback | DatabaseAdminController | RollbackSpreadsheetImportBatchAsync | backend/Controllers/DatabaseAdminController.cs:347 | [Authorize(Policy = "Admin")] | да |
| POST | api/dictionaries/deal-types | DictionariesAdminController | CreateDealType | backend/Controllers/DictionariesAdminController.cs:316 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/deal-types/{id:int} | DictionariesAdminController | UpdateDealType | backend/Controllers/DictionariesAdminController.cs:324 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/deal-types/{id:int} | DictionariesAdminController | DeleteDealType | backend/Controllers/DictionariesAdminController.cs:334 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/deal-types/{id:int}/toggle | DictionariesAdminController | ToggleDealType | backend/Controllers/DictionariesAdminController.cs:346 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/income-types | DictionariesAdminController | CreateIncomeType | backend/Controllers/DictionariesAdminController.cs:358 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/income-types/{id:int} | DictionariesAdminController | UpdateIncomeType | backend/Controllers/DictionariesAdminController.cs:366 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/income-types/{id:int} | DictionariesAdminController | DeleteIncomeType | backend/Controllers/DictionariesAdminController.cs:376 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/income-types/{id:int}/toggle | DictionariesAdminController | ToggleIncomeType | backend/Controllers/DictionariesAdminController.cs:388 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/payment-sources | DictionariesAdminController | CreatePaymentSource | backend/Controllers/DictionariesAdminController.cs:400 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/payment-sources/{id:int} | DictionariesAdminController | UpdatePaymentSource | backend/Controllers/DictionariesAdminController.cs:408 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/payment-sources/{id:int} | DictionariesAdminController | DeletePaymentSource | backend/Controllers/DictionariesAdminController.cs:418 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/payment-sources/{id:int}/toggle | DictionariesAdminController | TogglePaymentSource | backend/Controllers/DictionariesAdminController.cs:430 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/departments | DictionariesAdminController | CreateDepartment | backend/Controllers/DictionariesAdminController.cs:442 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/departments/{id:int} | DictionariesAdminController | UpdateDepartment | backend/Controllers/DictionariesAdminController.cs:472 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/departments/{id:int} | DictionariesAdminController | DeleteDepartment | backend/Controllers/DictionariesAdminController.cs:502 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/departments/{id:int}/toggle | DictionariesAdminController | ToggleDepartment | backend/Controllers/DictionariesAdminController.cs:516 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/services | DictionariesAdminController | CreateService | backend/Controllers/DictionariesAdminController.cs:528 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/services/{id:int} | DictionariesAdminController | UpdateService | backend/Controllers/DictionariesAdminController.cs:539 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/services/{id:int} | DictionariesAdminController | DeleteService | backend/Controllers/DictionariesAdminController.cs:558 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/services/{id:int}/toggle | DictionariesAdminController | ToggleService | backend/Controllers/DictionariesAdminController.cs:572 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/service-categories | DictionariesAdminController | CreateServiceCategory | backend/Controllers/DictionariesAdminController.cs:584 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/service-categories/{id:int} | DictionariesAdminController | UpdateServiceCategory | backend/Controllers/DictionariesAdminController.cs:592 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/service-categories/{id:int} | DictionariesAdminController | DeleteServiceCategory | backend/Controllers/DictionariesAdminController.cs:602 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/service-categories/{id:int}/toggle | DictionariesAdminController | ToggleServiceCategory | backend/Controllers/DictionariesAdminController.cs:614 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/countries | DictionariesAdminController | CreateCountry | backend/Controllers/DictionariesAdminController.cs:627 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/countries/{id:int} | DictionariesAdminController | UpdateCountry | backend/Controllers/DictionariesAdminController.cs:635 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/countries/{id:int} | DictionariesAdminController | DeleteCountry | backend/Controllers/DictionariesAdminController.cs:645 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/countries/{id:int}/toggle | DictionariesAdminController | ToggleCountry | backend/Controllers/DictionariesAdminController.cs:657 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/base-currencies | DictionariesAdminController | CreateBaseCurrency | backend/Controllers/DictionariesAdminController.cs:669 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/base-currencies/{id:int} | DictionariesAdminController | UpdateBaseCurrency | backend/Controllers/DictionariesAdminController.cs:677 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/base-currencies/{id:int} | DictionariesAdminController | DeleteBaseCurrency | backend/Controllers/DictionariesAdminController.cs:687 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/base-currencies/{id:int}/toggle | DictionariesAdminController | ToggleBaseCurrency | backend/Controllers/DictionariesAdminController.cs:699 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/service-languages | DictionariesAdminController | CreateServiceLanguage | backend/Controllers/DictionariesAdminController.cs:711 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/service-languages/{id:int} | DictionariesAdminController | UpdateServiceLanguage | backend/Controllers/DictionariesAdminController.cs:719 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/service-languages/{id:int} | DictionariesAdminController | DeleteServiceLanguage | backend/Controllers/DictionariesAdminController.cs:729 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/service-languages/{id:int}/toggle | DictionariesAdminController | ToggleServiceLanguage | backend/Controllers/DictionariesAdminController.cs:741 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/organization-forms | DictionariesAdminController | CreateOrganizationForm | backend/Controllers/DictionariesAdminController.cs:753 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/organization-forms/{id:int} | DictionariesAdminController | UpdateOrganizationForm | backend/Controllers/DictionariesAdminController.cs:761 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/organization-forms/{id:int} | DictionariesAdminController | DeleteOrganizationForm | backend/Controllers/DictionariesAdminController.cs:771 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/organization-forms/{id:int}/toggle | DictionariesAdminController | ToggleOrganizationForm | backend/Controllers/DictionariesAdminController.cs:783 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/timezones | DictionariesAdminController | CreateTimezone | backend/Controllers/DictionariesAdminController.cs:795 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/timezones/{id:int} | DictionariesAdminController | UpdateTimezone | backend/Controllers/DictionariesAdminController.cs:803 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/timezones/{id:int} | DictionariesAdminController | DeleteTimezone | backend/Controllers/DictionariesAdminController.cs:813 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/timezones/{id:int}/toggle | DictionariesAdminController | ToggleTimezone | backend/Controllers/DictionariesAdminController.cs:825 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/tax-systems | DictionariesAdminController | CreateTaxSystem | backend/Controllers/DictionariesAdminController.cs:838 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/tax-systems/{id:int} | DictionariesAdminController | UpdateTaxSystem | backend/Controllers/DictionariesAdminController.cs:846 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/tax-systems/{id:int} | DictionariesAdminController | DeleteTaxSystem | backend/Controllers/DictionariesAdminController.cs:856 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/tax-systems/{id:int}/toggle | DictionariesAdminController | ToggleTaxSystem | backend/Controllers/DictionariesAdminController.cs:868 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/tax-system-modes | DictionariesAdminController | CreateTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:880 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/tax-system-modes/{id:int} | DictionariesAdminController | UpdateTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:888 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/tax-system-modes/{id:int} | DictionariesAdminController | DeleteTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:898 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/tax-system-modes/{id:int}/toggle | DictionariesAdminController | ToggleTaxSystemMode | backend/Controllers/DictionariesAdminController.cs:910 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/invoice-units | DictionariesAdminController | CreateInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:921 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/invoice-units/{id:int} | DictionariesAdminController | UpdateInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:929 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/invoice-units/{id:int} | DictionariesAdminController | DeleteInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:939 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/invoice-units/{id:int}/toggle | DictionariesAdminController | ToggleInvoiceUnit | backend/Controllers/DictionariesAdminController.cs:949 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/invoice-item-names | DictionariesAdminController | CreateInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:960 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/invoice-item-names/{id:int} | DictionariesAdminController | UpdateInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:970 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/invoice-item-names/{id:int} | DictionariesAdminController | DeleteInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:985 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/invoice-item-names/{id:int}/toggle | DictionariesAdminController | ToggleInvoiceItemName | backend/Controllers/DictionariesAdminController.cs:995 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/client-statuses | DictionariesAdminController | CreateClientStatus | backend/Controllers/DictionariesAdminController.cs:1007 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/client-statuses/{id:int} | DictionariesAdminController | UpdateClientStatus | backend/Controllers/DictionariesAdminController.cs:1016 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/client-statuses/{id:int} | DictionariesAdminController | DeleteClientStatus | backend/Controllers/DictionariesAdminController.cs:1027 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/client-statuses/{id:int}/toggle | DictionariesAdminController | ToggleClientStatus | backend/Controllers/DictionariesAdminController.cs:1040 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/payment-statuses | DictionariesAdminController | CreatePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1052 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/payment-statuses/{id:int} | DictionariesAdminController | UpdatePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1060 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/payment-statuses/{id:int} | DictionariesAdminController | DeletePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1070 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/payment-statuses/{id:int}/toggle | DictionariesAdminController | TogglePaymentStatus | backend/Controllers/DictionariesAdminController.cs:1082 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/document-statuses | DictionariesAdminController | CreateDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1094 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Create)] | да |
| PUT | api/dictionaries/document-statuses/{id:int} | DictionariesAdminController | UpdateDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1116 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| DELETE | api/dictionaries/document-statuses/{id:int} | DictionariesAdminController | DeleteDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1146 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Delete)] | да |
| POST | api/dictionaries/document-statuses/{id:int}/toggle | DictionariesAdminController | ToggleDocumentStatus | backend/Controllers/DictionariesAdminController.cs:1162 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| GET | api/dictionaries/simple/{kind}/duplicates | DictionariesAdminController | GetSimpleDictionaryDuplicates | backend/Controllers/DictionariesAdminController.cs:1181 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| POST | api/dictionaries/simple/{kind}/merge | DictionariesAdminController | MergeSimpleDictionaryDuplicates | backend/Controllers/DictionariesAdminController.cs:1212 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries, RequireSectionPermissionOperations.Edit)] | да |
| GET | api/dictionaries/deal-types | DictionariesReadController | DealTypes | backend/Controllers/DictionariesReadController.cs:26 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/income-types | DictionariesReadController | IncomeTypes | backend/Controllers/DictionariesReadController.cs:36 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/payment-sources | DictionariesReadController | PaymentSources | backend/Controllers/DictionariesReadController.cs:49 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/departments | DictionariesReadController | Departments | backend/Controllers/DictionariesReadController.cs:59 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/countries | DictionariesReadController | Countries | backend/Controllers/DictionariesReadController.cs:70 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/base-currencies | DictionariesReadController | BaseCurrencies | backend/Controllers/DictionariesReadController.cs:80 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/service-languages | DictionariesReadController | ServiceLanguages | backend/Controllers/DictionariesReadController.cs:90 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/organization-forms | DictionariesReadController | OrganizationForms | backend/Controllers/DictionariesReadController.cs:100 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/timezones | DictionariesReadController | Timezones | backend/Controllers/DictionariesReadController.cs:110 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/tax-systems | DictionariesReadController | TaxSystems | backend/Controllers/DictionariesReadController.cs:121 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/tax-system-modes | DictionariesReadController | TaxSystemModes | backend/Controllers/DictionariesReadController.cs:131 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/services | DictionariesReadController | Services | backend/Controllers/DictionariesReadController.cs:141 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/service-categories | DictionariesReadController | ServiceCategories | backend/Controllers/DictionariesReadController.cs:154 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/invoice-units | DictionariesReadController | InvoiceUnits | backend/Controllers/DictionariesReadController.cs:164 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/invoice-item-names | DictionariesReadController | InvoiceItemNames | backend/Controllers/DictionariesReadController.cs:174 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/client-statuses | DictionariesReadController | ClientStatuses | backend/Controllers/DictionariesReadController.cs:184 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/payment-statuses | DictionariesReadController | PaymentStatuses | backend/Controllers/DictionariesReadController.cs:194 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/dictionaries/document-statuses | DictionariesReadController | DocumentStatuses | backend/Controllers/DictionariesReadController.cs:204 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Dictionaries)] | нет |
| GET | api/electronic-signatures/configuration | ElectronicSignaturesController | GetConfiguration | backend/Controllers/ElectronicSignaturesController.cs:21 | [Authorize(Policy = "Admin")] | да |
| GET | api/electronic-signatures/recipients | ElectronicSignaturesController | GetRecipients | backend/Controllers/ElectronicSignaturesController.cs:36 | [Authorize(Policy = "Admin")] | да |
| POST | api/electronic-signatures/financial-documents/{documentType}/{documentId:int}/prepare | ElectronicSignaturesController | Prepare | backend/Controllers/ElectronicSignaturesController.cs:62 | [Authorize(Policy = "Admin")] | да |
| GET | api/electronic-signatures/financial-documents/{documentType}/{documentId:int} | ElectronicSignaturesController | GetHistory | backend/Controllers/ElectronicSignaturesController.cs:144 | [Authorize(Policy = "Admin")] | да |
| POST | api/electronic-signatures/{publicId:guid}/cancel | ElectronicSignaturesController | Cancel | backend/Controllers/ElectronicSignaturesController.cs:161 | [Authorize(Policy = "Admin")] | да |
| GET | api/electronic-signatures/{publicId:guid}/evidence-package | ElectronicSignaturesController | DownloadEvidencePackage | backend/Controllers/ElectronicSignaturesController.cs:182 | [Authorize(Policy = "Admin")] | да |
| GET | api/auth/providers | ExternalAuthController | GetProviders | backend/Controllers/ExternalAuthController.cs:22 | не задано | нет |
| POST | api/auth/external/start | ExternalAuthController | StartExternal | backend/Controllers/ExternalAuthController.cs:27 | не задано | нет |
| GET | api/auth/external/status/{state} | ExternalAuthController | GetExternalStatus | backend/Controllers/ExternalAuthController.cs:49 | не задано | нет |
| GET | api/auth/external/callback/{provider} | ExternalAuthController | ExternalCallback | backend/Controllers/ExternalAuthController.cs:67 | не задано | нет |
| POST | api/auth/telegram/widget | ExternalAuthController | CompleteTelegramWidget | backend/Controllers/ExternalAuthController.cs:91 | не задано | нет |
| POST | api/auth/telegram/bot/start | ExternalAuthController | StartTelegramBot | backend/Controllers/ExternalAuthController.cs:107 | не задано | нет |
| GET | api/auth/telegram/bot/status/{state} | ExternalAuthController | GetTelegramBotStatus | backend/Controllers/ExternalAuthController.cs:123 | не задано | нет |
| POST | api/auth/telegram/webhook | ExternalAuthController | TelegramWebhook | backend/Controllers/ExternalAuthController.cs:141 | не задано | нет |
| GET | api/features | FeatureFlagsController | Get | backend/Controllers/FeatureFlagsController.cs:25 | не задано | да |
| GET | api/features/legal-integrations/diagnostics | FeatureFlagsController | GetLegalIntegrationDiagnostics | backend/Controllers/FeatureFlagsController.cs:32 | не задано | да |
| GET | api/form-requirements | FormRequirementsController | Get | backend/Controllers/FormRequirementsController.cs:242 | [Authorize] | да |
| GET | api/form-requirements/sections | FormRequirementsController | GetSections | backend/Controllers/FormRequirementsController.cs:273 | [Authorize] | да |
| PUT | api/form-requirements | FormRequirementsController | Upsert | backend/Controllers/FormRequirementsController.cs:305 | [Authorize] | да |
| GET | api/google-calendar/status | GoogleCalendarController | GetStatus | backend/Controllers/GoogleCalendarController.cs:26 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет |
| POST | api/google-calendar/auth/start | GoogleCalendarController | StartAuth | backend/Controllers/GoogleCalendarController.cs:40 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| GET | api/google-calendar/auth/status/{state} | GoogleCalendarController | GetAuthStatus | backend/Controllers/GoogleCalendarController.cs:63 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет |
| GET | api/google-calendar/auth/callback | GoogleCalendarController | Callback | backend/Controllers/GoogleCalendarController.cs:83 | публичный ([AllowAnonymous]) | нет |
| PUT | api/google-calendar/settings | GoogleCalendarController | UpdateSettings | backend/Controllers/GoogleCalendarController.cs:97 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| POST | api/google-calendar/sync-now | GoogleCalendarController | SyncNow | backend/Controllers/GoogleCalendarController.cs:124 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| DELETE | api/google-calendar/connection | GoogleCalendarController | Disconnect | backend/Controllers/GoogleCalendarController.cs:154 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| POST | api/google-calendar/webhook | GoogleCalendarController | Webhook | backend/Controllers/GoogleCalendarController.cs:178 | публичный ([AllowAnonymous]) | нет |
| GET | api/[controller] | HealthController | Get | backend/Controllers/HealthController.cs:10 | не задано | да |
| POST | api/[controller]/calc | InstallmentsController | Calc | backend/Controllers/InstallmentsController.cs:19 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Accounts)] | нет |
| GET | api/[controller]/reference-dictionaries | InvoicesController | GetReferenceDictionaries | backend/Controllers/InvoicesController.cs:66 | [Authorize] | да |
| GET | api/[controller] | InvoicesController | Get | backend/Controllers/InvoicesController.cs:239 | [Authorize] | да |
| GET | api/[controller]/summary | InvoicesController | GetSummary | backend/Controllers/InvoicesController.cs:366 | [Authorize] | да |
| GET | api/[controller]/{id:int} | InvoicesController | GetById | backend/Controllers/InvoicesController.cs:504 | [Authorize] | да |
| POST | api/[controller]/repair-payment-state | InvoicesController | RepairPaymentState | backend/Controllers/InvoicesController.cs:528 | [Authorize] | да |
| POST | api/[controller]/recalculate-payment-state | InvoicesController | RepairPaymentState | backend/Controllers/InvoicesController.cs:529 | [Authorize] | да |
| POST | api/[controller]/backfill-expense-cases | InvoicesController | BackfillExpenseInvoiceCases | backend/Controllers/InvoicesController.cs:666 | [Authorize] | да |
| POST | api/[controller]/{id:int}/send-to-signing | InvoicesController | SendToSigning | backend/Controllers/InvoicesController.cs:693 | [Authorize] | да |
| POST | api/[controller]/{id:int}/mark-signed | InvoicesController | MarkSigned | backend/Controllers/InvoicesController.cs:723 | [Authorize] | да |
| POST | api/[controller]/{id:int}/mark-not-signed | InvoicesController | MarkNotSigned | backend/Controllers/InvoicesController.cs:752 | [Authorize] | да |
| POST | api/[controller]/{id:int}/workflow-status | InvoicesController | UpdateWorkflowStatus | backend/Controllers/InvoicesController.cs:781 | [Authorize] | да |
| POST | api/[controller]/{id:int}/cancel | InvoicesController | Cancel | backend/Controllers/InvoicesController.cs:884 | [Authorize] | да |
| POST | api/[controller] | InvoicesController | Create | backend/Controllers/InvoicesController.cs:1287 | [Authorize] | да |
| PUT | api/[controller]/{id:int} | InvoicesController | Update | backend/Controllers/InvoicesController.cs:1505 | [Authorize] | да |
| GET | api/[controller]/numbering-settings | InvoicesController | GetNumberingSettings | backend/Controllers/InvoicesController.cs:1980 | [Authorize] | да |
| PUT | api/[controller]/numbering-settings | InvoicesController | UpsertNumberingSettings | backend/Controllers/InvoicesController.cs:2001 | [Authorize] | да |
| GET | api/[controller]/next-number | InvoicesController | GetNextNumber | backend/Controllers/InvoicesController.cs:2029 | [Authorize] | да |
| GET | api/[controller]/rules-settings | InvoicesController | GetRulesSettings | backend/Controllers/InvoicesController.cs:2057 | [Authorize] | да |
| PUT | api/[controller]/rules-settings | InvoicesController | UpsertRulesSettings | backend/Controllers/InvoicesController.cs:2063 | [Authorize] | да |
| GET | api/[controller]/document-template-settings | InvoicesController | GetDocumentTemplateSettings | backend/Controllers/InvoicesController.cs:2084 | [Authorize] | да |
| PUT | api/[controller]/document-template-settings | InvoicesController | UpsertDocumentTemplateSettings | backend/Controllers/InvoicesController.cs:2090 | [Authorize] | да |
| POST | api/[controller]/migrate-legacy-links | InvoicesController | MigrateLegacyInvoiceLinks | backend/Controllers/InvoicesController.cs:2120 | [Authorize] | да |
| POST | api/[controller]/cleanup-counterparty-names | InvoicesController | CleanupCounterpartyNames | backend/Controllers/InvoicesController.cs:2334 | [Authorize] | да |
| POST | api/[controller]/cleanup-orphan-invoices | InvoicesController | CleanupOrphanInvoices | backend/Controllers/InvoicesController.cs:2369 | [Authorize] | да |
| POST | api/[controller]/{invoiceEntityId:int}/repair-legacy-installments | InvoicesController | RepairLegacyInstallments | backend/Controllers/InvoicesController.cs:2411 | [Authorize] | да |
| DELETE | api/[controller]/{id:int} | InvoicesController | Delete | backend/Controllers/InvoicesController.cs:2570 | [Authorize] | да |
| POST | api/[controller]/bulk-delete | InvoicesController | BulkDelete | backend/Controllers/InvoicesController.cs:2616 | [Authorize] | да |
| POST | api/[controller]/change-responsible | InvoicesController | ChangeResponsible | backend/Controllers/InvoicesController.cs:2791 | [Authorize] | да |
| GET | api/[controller]/{id:int}/document | InvoicesController | GetDocument | backend/Controllers/InvoicesController.cs:2872 | [Authorize] | да |
| POST | api/[controller]/{id:int}/yandex-disk | InvoicesController | UploadDocumentToYandexDisk | backend/Controllers/InvoicesController.cs:2932 | [Authorize] | да |
| POST | api/[controller]/{id:int}/send-email | InvoicesController | SendInvoiceEmail | backend/Controllers/InvoicesController.cs:3066 | [Authorize] | да |
| POST | api/legal-entities/clean/address | LegalEntitiesController | CleanAddress | backend/Controllers/LegalEntitiesController.cs:851 | [Authorize] | да |
| POST | api/legal-entities/clean/email | LegalEntitiesController | CleanEmail | backend/Controllers/LegalEntitiesController.cs:883 | [Authorize] | да |
| POST | api/legal-entities/clean/phone | LegalEntitiesController | CleanPhone | backend/Controllers/LegalEntitiesController.cs:904 | [Authorize] | да |
| POST | api/legal-entities | LegalEntitiesController | Create | backend/Controllers/LegalEntitiesController.cs:1026 | [Authorize] | да |
| DELETE | api/legal-entities/{id:int} | LegalEntitiesController | Delete | backend/Controllers/LegalEntitiesController.cs:1171 | [Authorize] | да |
| GET | api/legal-entities/main-companies/settings | LegalEntitiesController | GetMainCompanySettings | backend/Controllers/LegalEntitiesController.cs:1226 | [Authorize] | да |
| PUT | api/legal-entities/main-companies/settings | LegalEntitiesController | UpdateMainCompanySettings | backend/Controllers/LegalEntitiesController.cs:1250 | [Authorize] | да |
| GET | api/legal-entities/company-profile | LegalEntitiesController | GetCompanyProfile | backend/Controllers/LegalEntitiesController.cs:1293 | [Authorize] | да |
| PUT | api/legal-entities/company-profile/main/{id:int} | LegalEntitiesController | SetCompanyProfileMain | backend/Controllers/LegalEntitiesController.cs:1318 | [Authorize] | да |
| POST | api/legal-entities/find-by-id | LegalEntitiesController | FindById | backend/Controllers/LegalEntitiesController.cs:1342 | [Authorize] | да |
| GET | api/legal-entities | LegalEntitiesController | GetAll | backend/Controllers/LegalEntitiesController.cs:1397 | [Authorize] | да |
| GET | api/legal-entities/paged | LegalEntitiesController | GetPaged | backend/Controllers/LegalEntitiesController.cs:1431 | [Authorize] | да |
| GET | api/legal-entities/{id:int} | LegalEntitiesController | GetById | backend/Controllers/LegalEntitiesController.cs:1804 | [Authorize] | да |
| GET | api/legal-entities/{id:int}/case-finance-summaries | LegalEntitiesController | GetCaseFinanceSummaries | backend/Controllers/LegalEntitiesController.cs:1836 | [Authorize] | да |
| POST | api/legal-entities/suggest | LegalEntitiesController | Suggest | backend/Controllers/LegalEntitiesController.cs:1940 | [Authorize] | да |
| PUT | api/legal-entities/{id:int} | LegalEntitiesController | Update | backend/Controllers/LegalEntitiesController.cs:1966 | [Authorize] | да |
| POST | api/legal-entities/migrate-legacy-addresses | LegalEntitiesController | MigrateLegacyAddresses | backend/Controllers/LegalEntitiesController.cs:2105 | [Authorize] | да |
| GET | api/legal-entities/{legalEntityId:int}/balance | LegalEntityBalancesController | GetSummary | backend/Controllers/LegalEntityBalancesController.cs:57 | [Authorize] | да |
| GET | api/legal-entities/{legalEntityId:int}/balance/transactions | LegalEntityBalancesController | GetTransactions | backend/Controllers/LegalEntityBalancesController.cs:78 | [Authorize] | да |
| POST | api/legal-entities/{legalEntityId:int}/balance/apply | LegalEntityBalancesController | ApplyCredit | backend/Controllers/LegalEntityBalancesController.cs:116 | [Authorize] | да |
| POST | api/max/link | MaxMessengerController | CreateLink | backend/Controllers/MaxMessengerController.cs:20 | [Authorize] | нет |
| DELETE | api/max/link | MaxMessengerController | OptOut | backend/Controllers/MaxMessengerController.cs:27 | [Authorize] | нет |
| POST | api/max/messages | MaxMessengerController | QueueMessage | backend/Controllers/MaxMessengerController.cs:34 | [Authorize] | нет |
| GET | api/max/messages | MaxMessengerController | GetMessages | backend/Controllers/MaxMessengerController.cs:49 | [Authorize] | нет |
| POST | api/max/messages/{publicId:guid}/retry | MaxMessengerController | RetryMessage | backend/Controllers/MaxMessengerController.cs:75 | [Authorize] | нет |
| GET | api/max/correspondence | MaxMessengerController | GetCorrespondence | backend/Controllers/MaxMessengerController.cs:91 | [Authorize] | нет |
| POST | api/max/correspondence/{publicId:guid}/route | MaxMessengerController | RouteCorrespondence | backend/Controllers/MaxMessengerController.cs:99 | [Authorize] | нет |
| POST | api/max | MaxMessengerController | Receive | backend/Controllers/MaxMessengerController.cs:141 | [Authorize] | нет |
| GET | api/messenger-document-deliveries/configuration | MessengerDocumentDeliveriesController | GetConfiguration | backend/Controllers/MessengerDocumentDeliveriesController.cs:31 | [Authorize(Policy = "Admin")] | да |
| GET | api/messenger-document-deliveries/recipients | MessengerDocumentDeliveriesController | GetRecipients | backend/Controllers/MessengerDocumentDeliveriesController.cs:53 | [Authorize(Policy = "Admin")] | да |
| POST | api/messenger-document-deliveries/recipients | MessengerDocumentDeliveriesController | CreateRecipient | backend/Controllers/MessengerDocumentDeliveriesController.cs:78 | [Authorize(Policy = "Admin")] | да |
| POST | api/messenger-document-deliveries/case-documents/{documentId:int}/prepare | MessengerDocumentDeliveriesController | PrepareCaseDocument | backend/Controllers/MessengerDocumentDeliveriesController.cs:120 | [Authorize(Policy = "Admin")] | да |
| POST | api/messenger-document-deliveries/financial-documents/{documentType}/{documentId:int}/prepare | MessengerDocumentDeliveriesController | PrepareFinancialDocument | backend/Controllers/MessengerDocumentDeliveriesController.cs:166 | [Authorize(Policy = "Admin")] | да |
| GET | api/messenger-document-deliveries | MessengerDocumentDeliveriesController | GetHistory | backend/Controllers/MessengerDocumentDeliveriesController.cs:228 | [Authorize(Policy = "Admin")] | да |
| POST | api/messenger-document-deliveries/{deliveryId:int}/queue | MessengerDocumentDeliveriesController | Queue | backend/Controllers/MessengerDocumentDeliveriesController.cs:267 | [Authorize(Policy = "Admin")] | да |
| GET | api/messenger-document-deliveries/{publicId:guid}/download | MessengerDocumentDeliveriesController | Download | backend/Controllers/MessengerDocumentDeliveriesController.cs:388 | [Authorize(Policy = "Admin")] | да |
| POST | api/messenger-document-deliveries/callbacks/{provider} | MessengerDocumentDeliveriesController | Callback | backend/Controllers/MessengerDocumentDeliveriesController.cs:403 | [Authorize(Policy = "Admin")] | да |
| GET | api/money-accounts | MoneyAccountsController | Get | backend/Controllers/MoneyAccountsController.cs:22 | [Authorize] | да |
| GET | api/money-accounts/{id:int} | MoneyAccountsController | GetById | backend/Controllers/MoneyAccountsController.cs:49 | [Authorize] | да |
| POST | api/money-accounts | MoneyAccountsController | Create | backend/Controllers/MoneyAccountsController.cs:56 | [Authorize] | да |
| PUT | api/money-accounts/{id:int} | MoneyAccountsController | Update | backend/Controllers/MoneyAccountsController.cs:94 | [Authorize] | да |
| DELETE | api/money-accounts/{id:int} | MoneyAccountsController | Delete | backend/Controllers/MoneyAccountsController.cs:146 | [Authorize] | да |
| GET | api/money-operations | MoneyOperationsController | Get | backend/Controllers/MoneyOperationsController.cs:199 | [Authorize] | да |
| GET | api/money-operations/paged | MoneyOperationsController | GetPaged | backend/Controllers/MoneyOperationsController.cs:239 | [Authorize] | да |
| GET | api/money-operations/export.csv | MoneyOperationsController | ExportCsv | backend/Controllers/MoneyOperationsController.cs:412 | [Authorize] | да |
| GET | api/money-operations/export.xls | MoneyOperationsController | ExportExcel | backend/Controllers/MoneyOperationsController.cs:462 | [Authorize] | да |
| GET | api/money-operations/print | MoneyOperationsController | Print | backend/Controllers/MoneyOperationsController.cs:509 | [Authorize] | да |
| GET | api/money-operations/export.pdf | MoneyOperationsController | ExportPdf | backend/Controllers/MoneyOperationsController.cs:556 | [Authorize] | да |
| GET | api/money-operations/accounts | MoneyOperationsController | GetAccounts | backend/Controllers/MoneyOperationsController.cs:608 | [Authorize] | да |
| GET | api/money-operations/account-summaries | MoneyOperationsController | GetAccountSummaries | backend/Controllers/MoneyOperationsController.cs:635 | [Authorize] | да |
| GET | api/money-operations/daily-balances | MoneyOperationsController | GetDailyBalances | backend/Controllers/MoneyOperationsController.cs:741 | [Authorize] | да |
| GET | api/money-operations/balance-reconciliation | MoneyOperationsController | GetBalanceReconciliationResults | backend/Controllers/MoneyOperationsController.cs:763 | [Authorize] | да |
| POST | api/money-operations/balance-reconciliation/run | MoneyOperationsController | RunBalanceReconciliation | backend/Controllers/MoneyOperationsController.cs:812 | [Authorize] | да |
| GET | api/money-operations/{id:int} | MoneyOperationsController | GetById | backend/Controllers/MoneyOperationsController.cs:1626 | [Authorize] | да |
| GET | api/money-operations/{id:int}/invoice-candidates | MoneyOperationsController | GetInvoiceCandidates | backend/Controllers/MoneyOperationsController.cs:1744 | [Authorize] | да |
| POST | api/money-operations/{id:int}/ignore | MoneyOperationsController | Ignore | backend/Controllers/MoneyOperationsController.cs:1884 | [Authorize] | да |
| POST | api/money-operations/{id:int}/link-invoice | MoneyOperationsController | LinkInvoice | backend/Controllers/MoneyOperationsController.cs:1915 | [Authorize] | да |
| POST | api/money-operations | MoneyOperationsController | Create | backend/Controllers/MoneyOperationsController.cs:1959 | [Authorize] | да |
| POST | api/money-operations/transfer | MoneyOperationsController | CreateTransfer | backend/Controllers/MoneyOperationsController.cs:2035 | [Authorize] | да |
| POST | api/money-operations/bank-statement/import | MoneyOperationsController | ImportBankStatement | backend/Controllers/MoneyOperationsController.cs:2163 | [Authorize] | да |
| POST | api/money-operations/bank-statement/auto-import/run | MoneyOperationsController | RunBankStatementAutoImport | backend/Controllers/MoneyOperationsController.cs:2289 | [Authorize] | да |
| GET | api/money-operations/bank-statement/imports | MoneyOperationsController | GetBankStatementImports | backend/Controllers/MoneyOperationsController.cs:2394 | [Authorize] | да |
| GET | api/money-operations/bank-statement/imports/{batchId:int} | MoneyOperationsController | GetBankStatementImport | backend/Controllers/MoneyOperationsController.cs:2432 | [Authorize] | да |
| POST | api/money-operations/bank-statement/imports/{batchId:int}/rollback | MoneyOperationsController | RollbackBankStatementImport | backend/Controllers/MoneyOperationsController.cs:2479 | [Authorize] | да |
| POST | api/money-operations/bank-statement/imports/{batchId:int}/reprocess | MoneyOperationsController | ReprocessBankStatementImport | backend/Controllers/MoneyOperationsController.cs:2585 | [Authorize] | да |
| PUT | api/money-operations/{id:int} | MoneyOperationsController | Update | backend/Controllers/MoneyOperationsController.cs:2641 | [Authorize] | да |
| DELETE | api/money-operations/{id:int} | MoneyOperationsController | Delete | backend/Controllers/MoneyOperationsController.cs:2774 | [Authorize] | да |
| GET | api/notifications/inbox/types | NotificationInboxController | GetTypes | backend/Controllers/NotificationInboxController.cs:36 | [Authorize] | да |
| GET | api/notifications/inbox/unread-count | NotificationInboxController | GetUnreadCount | backend/Controllers/NotificationInboxController.cs:40 | [Authorize] | да |
| GET | api/notifications/inbox | NotificationInboxController | GetNotifications | backend/Controllers/NotificationInboxController.cs:67 | [Authorize] | да |
| POST | api/notifications/inbox/read-all | NotificationInboxController | MarkAllRead | backend/Controllers/NotificationInboxController.cs:129 | [Authorize] | да |
| POST | api/notifications/inbox/bulk/archive | NotificationInboxController | ArchiveSelected | backend/Controllers/NotificationInboxController.cs:180 | [Authorize] | да |
| POST | api/notifications/inbox/bulk/delete | NotificationInboxController | DeleteSelected | backend/Controllers/NotificationInboxController.cs:208 | [Authorize] | да |
| PATCH | api/notifications/inbox/{id:int}/read | NotificationInboxController | MarkRead | backend/Controllers/NotificationInboxController.cs:231 | [Authorize] | да |
| PATCH | api/notifications/inbox/{id:int}/unread | NotificationInboxController | MarkUnread | backend/Controllers/NotificationInboxController.cs:245 | [Authorize] | да |
| PATCH | api/notifications/inbox/{id:int}/archive | NotificationInboxController | Archive | backend/Controllers/NotificationInboxController.cs:261 | [Authorize] | да |
| PATCH | api/notifications/inbox/{id:int}/snooze | NotificationInboxController | Snooze | backend/Controllers/NotificationInboxController.cs:281 | [Authorize] | да |
| DELETE | api/notifications/inbox/{id:int} | NotificationInboxController | Delete | backend/Controllers/NotificationInboxController.cs:301 | [Authorize] | да |
| GET | api/notifications/settings | NotificationsController | GetSettings | backend/Controllers/NotificationsController.cs:41 | [Authorize] | да |
| GET | api/notifications/channels | NotificationsController | GetChannels | backend/Controllers/NotificationsController.cs:48 | [Authorize] | да |
| PUT | api/notifications/settings/email | NotificationsController | UpdateEmailSettings | backend/Controllers/NotificationsController.cs:55 | [Authorize] | да |
| PUT | api/notifications/settings/telegram | NotificationsController | UpdateTelegramSettings | backend/Controllers/NotificationsController.cs:64 | [Authorize] | да |
| PUT | api/notifications/settings/sms | NotificationsController | UpdateSmsSettings | backend/Controllers/NotificationsController.cs:73 | [Authorize] | да |
| PUT | api/notifications/settings/max | NotificationsController | UpdateMaxSettings | backend/Controllers/NotificationsController.cs:82 | [Authorize] | да |
| GET | api/notifications/templates | NotificationsController | GetTemplates | backend/Controllers/NotificationsController.cs:91 | [Authorize] | да |
| PUT | api/notifications/templates/{key} | NotificationsController | UpdateTemplate | backend/Controllers/NotificationsController.cs:98 | [Authorize] | да |
| POST | api/notifications/templates/{key}/reset | NotificationsController | ResetTemplate | backend/Controllers/NotificationsController.cs:115 | [Authorize] | да |
| GET | api/notifications/role-preferences | NotificationsController | GetRolePreferences | backend/Controllers/NotificationsController.cs:129 | [Authorize] | да |
| PUT | api/notifications/role-preferences/{roleId:int} | NotificationsController | UpdateRolePreferences | backend/Controllers/NotificationsController.cs:175 | [Authorize] | да |
| POST | api/notifications/senders | NotificationsController | CreateSender | backend/Controllers/NotificationsController.cs:249 | [Authorize] | да |
| PUT | api/notifications/senders/{id:int} | NotificationsController | UpdateSender | backend/Controllers/NotificationsController.cs:265 | [Authorize] | да |
| DELETE | api/notifications/senders/{id:int} | NotificationsController | DeleteSender | backend/Controllers/NotificationsController.cs:286 | [Authorize] | да |
| POST | api/notifications/senders/{id:int}/telegram/webhook/sync | NotificationsController | SyncTelegramWebhook | backend/Controllers/NotificationsController.cs:293 | [Authorize] | да |
| DELETE | api/notifications/senders/{id:int}/telegram/webhook | NotificationsController | DeleteTelegramWebhook | backend/Controllers/NotificationsController.cs:316 | [Authorize] | да |
| POST | api/notifications/senders/{id:int}/telegram/profile/sync | NotificationsController | SyncTelegramBotProfile | backend/Controllers/NotificationsController.cs:339 | [Authorize] | да |
| PUT | api/notifications/senders/{id:int}/telegram/profile | NotificationsController | ApplyTelegramBotProfile | backend/Controllers/NotificationsController.cs:362 | [Authorize] | да |
| GET | api/notifications/senders/{id:int}/telegram/photo | NotificationsController | GetTelegramBotProfilePhoto | backend/Controllers/NotificationsController.cs:388 | [Authorize] | да |
| POST | api/notifications/telegram/{senderId:int}/webhook | NotificationsController | TelegramWebhook | backend/Controllers/NotificationsController.cs:410 | публичный ([AllowAnonymous]) | да |
| GET | api/notifications/deliveries | NotificationsController | GetDeliveries | backend/Controllers/NotificationsController.cs:431 | [Authorize] | да |
| POST | api/notifications/deliveries/{id:int}/retry | NotificationsController | RetryDelivery | backend/Controllers/NotificationsController.cs:473 | [Authorize] | да |
| POST | api/notifications/test | NotificationsController | SendTest | backend/Controllers/NotificationsController.cs:504 | [Authorize] | да |
| POST | api/notifications/users/{id:int}/issue-password | NotificationsController | IssuePassword | backend/Controllers/NotificationsController.cs:566 | [Authorize] | да |
| GET | api/object-audit | ObjectAuditController | GetEvents | backend/Controllers/ObjectAuditController.cs:36 | [Authorize] | да |
| GET | api/object-audit/{objectType}/{objectId:int} | ObjectAuditController | GetHistory | backend/Controllers/ObjectAuditController.cs:141 | [Authorize] | да |
| GET | api/object-audit/deleted/{objectType} | ObjectAuditController | GetDeletedObjects | backend/Controllers/ObjectAuditController.cs:187 | [Authorize] | да |
| POST | api/object-audit/{objectType}/{objectId:int}/restore | ObjectAuditController | RestoreObject | backend/Controllers/ObjectAuditController.cs:221 | [Authorize] | да |
| GET | api/[controller] | PaymentsController | Get | backend/Controllers/PaymentsController.cs:361 | [Authorize] | да |
| GET | api/[controller]/{id:int} | PaymentsController | GetById | backend/Controllers/PaymentsController.cs:394 | [Authorize] | да |
| POST | api/[controller] | PaymentsController | Create | backend/Controllers/PaymentsController.cs:417 | [Authorize] | да |
| PUT | api/[controller]/{id:int} | PaymentsController | Update | backend/Controllers/PaymentsController.cs:474 | [Authorize] | да |
| POST | api/[controller]/{id:int}/status | PaymentsController | UpdateStatus | backend/Controllers/PaymentsController.cs:683 | [Authorize] | да |
| POST | api/[controller]/{id:int}/advance-review/accept | PaymentsController | AcceptAdvance | backend/Controllers/PaymentsController.cs:787 | [Authorize] | да |
| POST | api/[controller]/{id:int}/advance-review/ignore | PaymentsController | IgnoreAdvance | backend/Controllers/PaymentsController.cs:826 | [Authorize] | да |
| POST | api/[controller]/{id:int}/advance-review/refund | PaymentsController | RefundAdvance | backend/Controllers/PaymentsController.cs:865 | [Authorize] | да |
| GET | api/[controller]/advance-review/candidates | PaymentsController | GetAdvanceReviewCandidates | backend/Controllers/PaymentsController.cs:926 | [Authorize] | да |
| GET | api/[controller]/advance-review/diagnostics | PaymentsController | GetAdvanceReviewDiagnostics | backend/Controllers/PaymentsController.cs:985 | [Authorize] | да |
| POST | api/[controller]/advance-review/backfill | PaymentsController | BackfillAdvanceReviewCandidates | backend/Controllers/PaymentsController.cs:1126 | [Authorize] | да |
| POST | api/[controller]/advance-review/backfill/rollback | PaymentsController | RollbackAdvanceReviewBackfill | backend/Controllers/PaymentsController.cs:1215 | [Authorize] | да |
| PUT | api/[controller]/{id:int}/invoice-managed | PaymentsController | UpdateInvoiceManaged | backend/Controllers/PaymentsController.cs:1314 | [Authorize] | да |
| GET | api/[controller]/partial-remainder-candidates | PaymentsController | GetPartialRemainderCandidates | backend/Controllers/PaymentsController.cs:1505 | [Authorize] | да |
| POST | api/[controller]/{id:int}/partial-remainder/from-existing | PaymentsController | TransferExistingPartialRemainder | backend/Controllers/PaymentsController.cs:1630 | [Authorize] | да |
| PUT | api/[controller]/{id:int}/partial-remainder | PaymentsController | TransferPartialRemainder | backend/Controllers/PaymentsController.cs:1672 | [Authorize] | да |
| POST | api/[controller]/{id:int}/partial-remainder/rollback | PaymentsController | RollbackPartialRemainderTransfer | backend/Controllers/PaymentsController.cs:1957 | [Authorize] | да |
| PUT | api/[controller]/{id:int}/partial-remainder/adjust | PaymentsController | AdjustPartialRemainderTransfer | backend/Controllers/PaymentsController.cs:2011 | [Authorize] | да |
| POST | api/[controller]/repair-invoice-links | PaymentsController | RepairInvoiceLinks | backend/Controllers/PaymentsController.cs:3501 | [Authorize] | да |
| DELETE | api/[controller]/{id:int} | PaymentsController | Delete | backend/Controllers/PaymentsController.cs:4464 | [Authorize] | да |
| GET | api/clients/{clientId:int}/powers-of-attorney | PowerOfAttorneyController | Get | backend/Controllers/PowerOfAttorneyController.cs:24 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| POST | api/clients/{clientId:int}/powers-of-attorney | PowerOfAttorneyController | Create | backend/Controllers/PowerOfAttorneyController.cs:32 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| PUT | api/clients/{clientId:int}/powers-of-attorney/{id:int} | PowerOfAttorneyController | Update | backend/Controllers/PowerOfAttorneyController.cs:55 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| DELETE | api/clients/{clientId:int}/powers-of-attorney/{id:int} | PowerOfAttorneyController | Delete | backend/Controllers/PowerOfAttorneyController.cs:74 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Clients)] | да |
| GET | api/profile | ProfileController | GetProfile | backend/Controllers/ProfileController.cs:35 | [Authorize] | да |
| GET | api/profile/contractor-balance | ProfileController | GetContractorBalance | backend/Controllers/ProfileController.cs:48 | [Authorize] | да |
| PUT | api/profile | ProfileController | UpdateProfile | backend/Controllers/ProfileController.cs:101 | [Authorize] | да |
| POST | api/profile/password | ProfileController | ChangePassword | backend/Controllers/ProfileController.cs:125 | [Authorize] | да |
| POST | api/profile/photo | ProfileController | UploadProfilePhoto | backend/Controllers/ProfileController.cs:162 | [Authorize] | да |
| DELETE | api/profile/external/{provider} | ProfileController | DetachExternalIdentity | backend/Controllers/ProfileController.cs:246 | [Authorize] | да |
| POST | api/public/work-order-approvals/{publicId:guid}/open | PublicWorkOrderApprovalController | Open | backend/Controllers/PublicWorkOrderApprovalController.cs:21 | не задано | нет |
| POST | api/public/work-order-approvals/{publicId:guid}/decision | PublicWorkOrderApprovalController | Decide | backend/Controllers/PublicWorkOrderApprovalController.cs:38 | не задано | нет |
| POST | api/public/work-order-approvals/{publicId:guid}/attachments/{attachmentPublicId:guid}/grant | PublicWorkOrderApprovalController | CreateAttachmentGrant | backend/Controllers/PublicWorkOrderApprovalController.cs:65 | не задано | нет |
| POST | api/public/work-order-approvals/{publicId:guid}/attachments/{attachmentPublicId:guid}/download | PublicWorkOrderApprovalController | DownloadAttachment | backend/Controllers/PublicWorkOrderApprovalController.cs:87 | не задано | нет |
| GET | api/notifications/inbox/push/public-key | PushSubscriptionsController | GetPublicKey | backend/Controllers/PushSubscriptionsController.cs:29 | [Authorize] | да |
| GET | api/notifications/inbox/push/subscriptions | PushSubscriptionsController | GetSubscriptions | backend/Controllers/PushSubscriptionsController.cs:41 | [Authorize] | да |
| GET | api/notifications/inbox/push/diagnostics | PushSubscriptionsController | GetDiagnostics | backend/Controllers/PushSubscriptionsController.cs:64 | [Authorize] | да |
| POST | api/notifications/inbox/push/subscriptions | PushSubscriptionsController | UpsertSubscription | backend/Controllers/PushSubscriptionsController.cs:135 | [Authorize] | да |
| DELETE | api/notifications/inbox/push/subscriptions/{id:int} | PushSubscriptionsController | RevokeSubscriptionById | backend/Controllers/PushSubscriptionsController.cs:188 | [Authorize] | да |
| POST | api/notifications/inbox/push/subscriptions/revoke | PushSubscriptionsController | RevokeSubscriptionByEndpoint | backend/Controllers/PushSubscriptionsController.cs:209 | [Authorize] | да |
| GET | api/reports/legal-work | ReportsController | LegalWork | backend/Controllers/ReportsController.cs:187 | [Authorize] | да |
| GET | api/reports/legal-work.csv | ReportsController | ExportLegalWorkCsv | backend/Controllers/ReportsController.cs:223 | [Authorize] | да |
| GET | api/reports/legal-work.xls | ReportsController | ExportLegalWorkExcel | backend/Controllers/ReportsController.cs:245 | [Authorize] | да |
| GET | api/reports/legal-work.pdf | ReportsController | ExportLegalWorkPdf | backend/Controllers/ReportsController.cs:268 | [Authorize] | да |
| GET | api/reports/management.xls | ReportsController | ExportManagementReportExcel | backend/Controllers/ReportsController.cs:293 | [Authorize] | да |
| GET | api/reports/management.csv | ReportsController | ExportManagementReportCsv | backend/Controllers/ReportsController.cs:353 | [Authorize] | да |
| GET | api/reports/management.pdf | ReportsController | ExportManagementReportPdf | backend/Controllers/ReportsController.cs:413 | [Authorize] | да |
| GET | api/reports/management.print | ReportsController | PrintManagementReport | backend/Controllers/ReportsController.cs:476 | [Authorize] | да |
| GET | api/reports/case-finance | ReportsController | CaseFinance | backend/Controllers/ReportsController.cs:533 | [Authorize] | да |
| GET | api/reports/case-success | ReportsController | CaseSuccess | backend/Controllers/ReportsController.cs:569 | [Authorize] | да |
| GET | api/reports/case-activity | ReportsController | CaseActivity | backend/Controllers/ReportsController.cs:1242 | [Authorize] | да |
| GET | api/reports/current-cases | ReportsController | CurrentCases | backend/Controllers/ReportsController.cs:1281 | [Authorize] | да |
| GET | api/reports/tasks | ReportsController | Tasks | backend/Controllers/ReportsController.cs:1325 | [Authorize] | да |
| GET | api/reports/lawyer-workload | ReportsController | LawyerWorkload | backend/Controllers/ReportsController.cs:1362 | [Authorize] | да |
| GET | api/reports/overdue-deadlines | ReportsController | OverdueDeadlines | backend/Controllers/ReportsController.cs:1401 | [Authorize] | да |
| GET | api/reports/cash-flow | ReportsController | CashFlow | backend/Controllers/ReportsController.cs:1438 | [Authorize] | да |
| GET | api/reports/cash-flow-forecast | ReportsController | CashFlowForecast | backend/Controllers/ReportsController.cs:1479 | [Authorize] | да |
| GET | api/reports/categories | ReportsController | Categories | backend/Controllers/ReportsController.cs:1518 | [Authorize] | да |
| GET | api/reports/counterparties | ReportsController | Counterparties | backend/Controllers/ReportsController.cs:1539 | [Authorize] | да |
| GET | api/reports/projects | ReportsController | Projects | backend/Controllers/ReportsController.cs:1560 | [Authorize] | да |
| GET | api/reports/directions | ReportsController | Directions | backend/Controllers/ReportsController.cs:1581 | [Authorize] | да |
| GET | api/reports/sales-points | ReportsController | SalesPoints | backend/Controllers/ReportsController.cs:1602 | [Authorize] | да |
| GET | api/reports/vat | ReportsController | Vat | backend/Controllers/ReportsController.cs:1623 | [Authorize] | да |
| GET | api/reports/plan-fact | ReportsController | PlanFact | backend/Controllers/ReportsController.cs:1644 | [Authorize] | да |
| GET | api/reports/advances | ReportsController | Advances | backend/Controllers/ReportsController.cs:1665 | [Authorize] | да |
| GET | api/reports/payment-priorities | ReportsController | PaymentPriorities | backend/Controllers/ReportsController.cs:1686 | [Authorize] | да |
| GET | api/reports/paid-accounts | ReportsController | PaidAccounts | backend/Controllers/ReportsController.cs:1707 | [Authorize] | да |
| GET | api/reports/paid-invoices | ReportsController | PaidInvoices | backend/Controllers/ReportsController.cs:1728 | [Authorize] | да |
| GET | api/reports/invoice-statuses | ReportsController | InvoiceStatuses | backend/Controllers/ReportsController.cs:1749 | [Authorize] | да |
| GET | api/reports/act-statuses | ReportsController | ActStatuses | backend/Controllers/ReportsController.cs:1770 | [Authorize] | да |
| GET | api/reports/contract-statuses | ReportsController | ContractStatuses | backend/Controllers/ReportsController.cs:1791 | [Authorize] | да |
| GET | api/reports/payment-orders | ReportsController | PaymentOrders | backend/Controllers/ReportsController.cs:1812 | [Authorize] | да |
| GET | api/reports/payment-purposes | ReportsController | PaymentPurposes | backend/Controllers/ReportsController.cs:1833 | [Authorize] | да |
| GET | api/reports/match-statuses | ReportsController | MatchStatuses | backend/Controllers/ReportsController.cs:1854 | [Authorize] | да |
| GET | api/reports/journal-statuses | ReportsController | JournalStatuses | backend/Controllers/ReportsController.cs:1875 | [Authorize] | да |
| GET | api/reports/currencies | ReportsController | Currencies | backend/Controllers/ReportsController.cs:1896 | [Authorize] | да |
| GET | api/reports/cash-orders | ReportsController | CashOrders | backend/Controllers/ReportsController.cs:1917 | [Authorize] | да |
| GET | api/reports/bank-statement-imports | ReportsController | BankStatementImports | backend/Controllers/ReportsController.cs:1951 | [Authorize] | да |
| GET | api/reports/spreadsheet-imports | ReportsController | SpreadsheetImports | backend/Controllers/ReportsController.cs:1997 | [Authorize] | да |
| GET | api/role-permissions/audit | RolePermissionsController | GetAudit | backend/Controllers/RolePermissionsController.cs:36 | [Authorize] | да |
| GET | api/role-permissions/audit.csv | RolePermissionsController | ExportAuditCsv | backend/Controllers/RolePermissionsController.cs:69 | [Authorize] | да |
| GET | api/role-permissions/{roleId:int} | RolePermissionsController | Get | backend/Controllers/RolePermissionsController.cs:113 | [Authorize] | да |
| GET | api/role-permissions/calendar-users | RolePermissionsController | GetCalendarUsers | backend/Controllers/RolePermissionsController.cs:152 | [Authorize] | да |
| PUT | api/role-permissions/{roleId:int} | RolePermissionsController | Update | backend/Controllers/RolePermissionsController.cs:180 | [Authorize] | да |
| DELETE | api/role-permissions/{roleId:int} | RolePermissionsController | Reset | backend/Controllers/RolePermissionsController.cs:283 | [Authorize] | да |
| GET | api/roles | RolesController | GetRoles | backend/Controllers/RolesController.cs:29 | [Authorize] | нет |
| GET | api/roles/paged | RolesController | GetRolesPaged | backend/Controllers/RolesController.cs:44 | [Authorize] | нет |
| GET | api/roles/{id:int} | RolesController | GetRole | backend/Controllers/RolesController.cs:78 | [Authorize] | нет |
| POST | api/roles | RolesController | CreateRole | backend/Controllers/RolesController.cs:94 | [Authorize] | нет |
| POST | api/roles/{id:int}/copy | RolesController | CopyRole | backend/Controllers/RolesController.cs:142 | [Authorize] | нет |
| PUT | api/roles/{id:int} | RolesController | UpdateRole | backend/Controllers/RolesController.cs:197 | [Authorize] | нет |
| DELETE | api/roles/{id:int} | RolesController | DeleteRole | backend/Controllers/RolesController.cs:241 | [Authorize] | нет |
| GET | api/s3-storages | S3StorageController | GetOverview | backend/Controllers/S3StorageController.cs:30 | [Authorize] | да |
| POST | api/s3-storages | S3StorageController | UpsertConnection | backend/Controllers/S3StorageController.cs:39 | [Authorize] | да |
| DELETE | api/s3-storages/{id} | S3StorageController | DeleteConnection | backend/Controllers/S3StorageController.cs:60 | [Authorize] | да |
| POST | api/s3-storages/{id}/connection-test | S3StorageController | TestConnection | backend/Controllers/S3StorageController.cs:82 | [Authorize] | да |
| GET | api/s3-storages/{id}/objects | S3StorageController | ListObjects | backend/Controllers/S3StorageController.cs:105 | [Authorize] | да |
| POST | api/s3-storages/{id}/folders | S3StorageController | CreateFolder | backend/Controllers/S3StorageController.cs:119 | [Authorize] | да |
| POST | api/s3-storages/{id}/upload | S3StorageController | Upload | backend/Controllers/S3StorageController.cs:136 | [Authorize] | да |
| GET | api/s3-storages/{id}/download | S3StorageController | Download | backend/Controllers/S3StorageController.cs:170 | [Authorize] | да |
| DELETE | api/s3-storages/{id}/objects | S3StorageController | DeleteObject | backend/Controllers/S3StorageController.cs:195 | [Authorize] | да |
| POST | api/s3-storages/{id}/copy | S3StorageController | CopyObject | backend/Controllers/S3StorageController.cs:213 | [Authorize] | да |
| POST | api/s3-storages/{id}/move | S3StorageController | MoveObject | backend/Controllers/S3StorageController.cs:230 | [Authorize] | да |
| GET | api/s3-storages/{id}/text | S3StorageController | GetTextFile | backend/Controllers/S3StorageController.cs:247 | [Authorize] | да |
| PUT | api/s3-storages/{id}/text | S3StorageController | SaveTextFile | backend/Controllers/S3StorageController.cs:259 | [Authorize] | да |
| GET | api/[controller]/month | StatsController | Month | backend/Controllers/StatsController.cs:19 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | нет |
| GET | api/sticky-notes/boards | StickyNotesController | GetBoards | backend/Controllers/StickyNotesController.cs:62 | [Authorize] | да |
| POST | api/sticky-notes/boards | StickyNotesController | CreateBoard | backend/Controllers/StickyNotesController.cs:99 | [Authorize] | да |
| PUT | api/sticky-notes/boards/{boardId:int} | StickyNotesController | UpdateBoard | backend/Controllers/StickyNotesController.cs:126 | [Authorize] | да |
| DELETE | api/sticky-notes/boards/{boardId:int} | StickyNotesController | DeleteBoard | backend/Controllers/StickyNotesController.cs:157 | [Authorize] | да |
| GET | api/sticky-notes/boards/{boardId:int}/members | StickyNotesController | GetBoardMembers | backend/Controllers/StickyNotesController.cs:185 | [Authorize] | да |
| GET | api/sticky-notes/member-candidates | StickyNotesController | GetMemberCandidates | backend/Controllers/StickyNotesController.cs:212 | [Authorize] | да |
| PUT | api/sticky-notes/boards/{boardId:int}/members/{userId:int} | StickyNotesController | UpsertBoardMember | backend/Controllers/StickyNotesController.cs:244 | [Authorize] | да |
| DELETE | api/sticky-notes/boards/{boardId:int}/members/{userId:int} | StickyNotesController | DeleteBoardMember | backend/Controllers/StickyNotesController.cs:315 | [Authorize] | да |
| GET | api/sticky-notes/boards/{boardId:int}/notes | StickyNotesController | GetNotes | backend/Controllers/StickyNotesController.cs:354 | [Authorize] | да |
| POST | api/sticky-notes/boards/{boardId:int}/notes | StickyNotesController | CreateNote | backend/Controllers/StickyNotesController.cs:416 | [Authorize] | да |
| PUT | api/sticky-notes/notes/{noteId:int} | StickyNotesController | UpdateNote | backend/Controllers/StickyNotesController.cs:487 | [Authorize] | да |
| PATCH | api/sticky-notes/notes/{noteId:int}/layout | StickyNotesController | UpdateNoteLayout | backend/Controllers/StickyNotesController.cs:578 | [Authorize] | да |
| DELETE | api/sticky-notes/notes/{noteId:int} | StickyNotesController | DeleteNote | backend/Controllers/StickyNotesController.cs:620 | [Authorize] | да |
| POST | api/sticky-notes/notes/{noteId:int}/restore | StickyNotesController | RestoreNote | backend/Controllers/StickyNotesController.cs:658 | [Authorize] | да |
| GET | api/system/jobs/diagnostics | SystemJobsController | GetDiagnostics | backend/Controllers/SystemJobsController.cs:110 | [Authorize(Policy = "Admin")] | да |
| POST | api/system/jobs/{jobKey}/run | SystemJobsController | RunJob | backend/Controllers/SystemJobsController.cs:189 | [Authorize(Policy = "Admin")] | да |
| GET | api/tasks | TasksController | GetTasks | backend/Controllers/TasksController.cs:135 | [Authorize] | да |
| GET | api/tasks/notifications | TasksController | GetNotifications | backend/Controllers/TasksController.cs:207 | [Authorize] | да |
| GET | api/tasks/overdue-summary | TasksController | GetOverdueSummary | backend/Controllers/TasksController.cs:274 | [Authorize] | да |
| GET | api/tasks/assignees | TasksController | GetAssignees | backend/Controllers/TasksController.cs:300 | [Authorize] | да |
| GET | api/tasks/settings | TasksController | GetSettings | backend/Controllers/TasksController.cs:353 | [Authorize] | да |
| PUT | api/tasks/settings | TasksController | UpdateSettings | backend/Controllers/TasksController.cs:370 | [Authorize] | да |
| GET | api/tasks/automation/case-control/diagnostics | TasksController | GetCaseControlAutomationDiagnostics | backend/Controllers/TasksController.cs:464 | [Authorize] | да |
| GET | api/tasks/automation/case-control/preview | TasksController | PreviewCaseControlAutomation | backend/Controllers/TasksController.cs:607 | [Authorize] | да |
| GET | api/tasks/calendar.ics | TasksController | ExportCalendar | backend/Controllers/TasksController.cs:715 | [Authorize] | да |
| GET | api/tasks/planning-report.{format} | TasksController | ExportPlanningReport | backend/Controllers/TasksController.cs:805 | [Authorize] | да |
| GET | api/tasks/automation/case-candidates | TasksController | GetAutomationCaseCandidates | backend/Controllers/TasksController.cs:893 | [Authorize] | да |
| POST | api/tasks/automation/case-candidates/{clientCaseId:int}/task | TasksController | CreateAutomationTaskForCase | backend/Controllers/TasksController.cs:1066 | [Authorize] | да |
| POST | api/tasks/automation/case-candidates/tasks | TasksController | CreateAutomationTasksForCandidates | backend/Controllers/TasksController.cs:1237 | [Authorize] | да |
| GET | api/tasks/templates | TasksController | GetTemplates | backend/Controllers/TasksController.cs:1377 | [Authorize] | да |
| GET | api/tasks/templates/{id:int} | TasksController | GetTemplate | backend/Controllers/TasksController.cs:1462 | [Authorize] | да |
| POST | api/tasks/templates | TasksController | CreateTemplate | backend/Controllers/TasksController.cs:1483 | [Authorize] | да |
| PUT | api/tasks/templates/{id:int} | TasksController | UpdateTemplate | backend/Controllers/TasksController.cs:1548 | [Authorize] | да |
| PATCH | api/tasks/templates/{id:int}/active | TasksController | UpdateTemplateActive | backend/Controllers/TasksController.cs:1620 | [Authorize] | да |
| DELETE | api/tasks/templates/{id:int} | TasksController | DeleteTemplate | backend/Controllers/TasksController.cs:1651 | [Authorize] | да |
| GET | api/tasks/{id:int} | TasksController | GetTask | backend/Controllers/TasksController.cs:1676 | [Authorize] | да |
| GET | api/tasks/{id:int}/comments | TasksController | GetComments | backend/Controllers/TasksController.cs:1697 | [Authorize] | да |
| POST | api/tasks/{id:int}/comments | TasksController | CreateComment | backend/Controllers/TasksController.cs:1728 | [Authorize] | да |
| POST | api/tasks/{id:int}/deadline-change-requests | TasksController | CreateDeadlineChangeRequest | backend/Controllers/TasksController.cs:1780 | [Authorize] | да |
| GET | api/tasks/{id:int}/deadline-change-requests | TasksController | GetDeadlineChangeRequests | backend/Controllers/TasksController.cs:1875 | [Authorize] | да |
| GET | api/tasks/{id:int}/deadline-change-requests/{requestId:int} | TasksController | GetDeadlineChangeRequest | backend/Controllers/TasksController.cs:1906 | [Authorize] | да |
| POST | api/tasks/{id:int}/deadline-change-requests/{requestId:int}/approve | TasksController | ApproveDeadlineChangeRequest | backend/Controllers/TasksController.cs:1934 | [Authorize] | да |
| POST | api/tasks/{id:int}/deadline-change-requests/{requestId:int}/reject | TasksController | RejectDeadlineChangeRequest | backend/Controllers/TasksController.cs:2030 | [Authorize] | да |
| GET | api/tasks/{id:int}/files | TasksController | GetFiles | backend/Controllers/TasksController.cs:2119 | [Authorize] | да |
| POST | api/tasks/{id:int}/files | TasksController | UploadFile | backend/Controllers/TasksController.cs:2220 | [Authorize] | да |
| GET | api/tasks/{id:int}/files/{fileId:int}/download | TasksController | DownloadFile | backend/Controllers/TasksController.cs:2383 | [Authorize] | да |
| DELETE | api/tasks/{id:int}/files/{fileId:int} | TasksController | DeleteFile | backend/Controllers/TasksController.cs:2420 | [Authorize] | да |
| GET | api/tasks/billing-context | TasksController | GetBillingContext | backend/Controllers/TasksController.cs:2471 | [Authorize] | да |
| POST | api/tasks | TasksController | CreateTask | backend/Controllers/TasksController.cs:2543 | [Authorize] | да |
| PUT | api/tasks/{id:int} | TasksController | UpdateTask | backend/Controllers/TasksController.cs:2671 | [Authorize] | да |
| PATCH | api/tasks/{id:int}/status | TasksController | UpdateStatus | backend/Controllers/TasksController.cs:2917 | [Authorize] | да |
| PATCH | api/tasks/{id:int}/control | TasksController | UpdateControl | backend/Controllers/TasksController.cs:3063 | [Authorize] | да |
| POST | api/tasks/bulk | TasksController | BulkUpdateTasks | backend/Controllers/TasksController.cs:3128 | [Authorize] | да |
| POST | api/tasks/bulk/delete | TasksController | BulkDeleteTasks | backend/Controllers/TasksController.cs:3378 | [Authorize] | да |
| PATCH | api/tasks/{id:int}/approval | TasksController | UpdateApproval | backend/Controllers/TasksController.cs:3454 | [Authorize] | да |
| DELETE | api/tasks/{id:int} | TasksController | DeleteTask | backend/Controllers/TasksController.cs:3607 | [Authorize] | да |
| GET | api/user-activity | UserActivityController | GetAsync | backend/Controllers/UserActivityController.cs:62 | [Authorize] | да |
| GET | api/user-activity/online | UserActivityController | GetOnlineAsync | backend/Controllers/UserActivityController.cs:153 | [Authorize] | да |
| GET | api/user-activity/filters | UserActivityController | GetFiltersAsync | backend/Controllers/UserActivityController.cs:287 | [Authorize] | да |
| POST | api/user-activity | UserActivityController | CreateAsync | backend/Controllers/UserActivityController.cs:346 | [Authorize] | да |
| POST | api/user-activity/heartbeat | UserActivityController | HeartbeatAsync | backend/Controllers/UserActivityController.cs:379 | [Authorize] | да |
| GET | api/user-activity/admin/maintenance | UserActivityController | GetMaintenanceAsync | backend/Controllers/UserActivityController.cs:435 | [Authorize] | да |
| PUT | api/user-activity/admin/settings | UserActivityController | UpdateSettingsAsync | backend/Controllers/UserActivityController.cs:442 | [Authorize] | да |
| POST | api/user-activity/admin/cleanup | UserActivityController | CleanupAsync | backend/Controllers/UserActivityController.cs:452 | [Authorize] | да |
| POST | api/user-activity/admin/compact | UserActivityController | CompactAsync | backend/Controllers/UserActivityController.cs:459 | [Authorize] | да |
| DELETE | api/user-activity/cleanup | UserActivityController | DeleteAllAsync | backend/Controllers/UserActivityController.cs:472 | [Authorize] | да |
| POST | api/users/{id:int}/approve | UsersController | ApproveUser | backend/Controllers/UsersController.cs:41 | [Authorize(Policy = "Admin")] | да |
| POST | api/users | UsersController | CreateUser | backend/Controllers/UsersController.cs:54 | [Authorize(Policy = "Admin")] | да |
| DELETE | api/users/{id:int} | UsersController | DeleteUser | backend/Controllers/UsersController.cs:78 | [Authorize(Policy = "Admin")] | да |
| GET | api/users/roles | UsersController | GetRoles | backend/Controllers/UsersController.cs:112 | [Authorize(Policy = "Admin")] | да |
| GET | api/users/{id:int} | UsersController | GetUser | backend/Controllers/UsersController.cs:115 | [Authorize(Policy = "Admin")] | да |
| GET | api/users/{id:int}/contractor-balance | UsersController | GetUserContractorBalance | backend/Controllers/UsersController.cs:119 | [Authorize(Policy = "Admin")] | да |
| GET | api/users | UsersController | GetUsers | backend/Controllers/UsersController.cs:167 | [Authorize(Policy = "Admin")] | да |
| GET | api/users/paged | UsersController | GetUsersPaged | backend/Controllers/UsersController.cs:171 | [Authorize(Policy = "Admin")] | да |
| POST | api/users/{id:int}/reject | UsersController | RejectUser | backend/Controllers/UsersController.cs:178 | [Authorize(Policy = "Admin")] | да |
| POST | api/users/merge | UsersController | MergeUsers | backend/Controllers/UsersController.cs:212 | [Authorize(Policy = "Admin")] | да |
| PUT | api/users/{id:int} | UsersController | UpdateUser | backend/Controllers/UsersController.cs:231 | [Authorize(Policy = "Admin")] | да |
| GET | api/user-settings/{key} | UserSettingsController | Get | backend/Controllers/UserSettingsController.cs:44 | [Authorize] | да |
| PUT | api/user-settings/{key} | UserSettingsController | Upsert | backend/Controllers/UserSettingsController.cs:59 | [Authorize] | да |
| GET | api/user-settings/users/{userId:int}/{key} | UserSettingsController | GetForUser | backend/Controllers/UserSettingsController.cs:68 | [Authorize] | да |
| PUT | api/user-settings/users/{userId:int}/{key} | UserSettingsController | UpsertForUser | backend/Controllers/UserSettingsController.cs:81 | [Authorize] | да |
| GET | api/v1/cases | CasesV1Controller | Get | backend/Controllers/V1/CasesV1Controller.cs:29 | [Authorize] | да |
| GET | api/v1/cases/{id:int} | CasesV1Controller | GetById | backend/Controllers/V1/CasesV1Controller.cs:59 | [Authorize] | да |
| POST | api/v1/cases | CasesV1Controller | Create | backend/Controllers/V1/CasesV1Controller.cs:81 | [Authorize] | да |
| PUT | api/v1/cases/{id:int} | CasesV1Controller | Update | backend/Controllers/V1/CasesV1Controller.cs:102 | [Authorize] | да |
| DELETE | api/v1/cases/{id:int} | CasesV1Controller | Delete | backend/Controllers/V1/CasesV1Controller.cs:133 | [Authorize] | да |
| GET | api/v1/clients | ClientsV1Controller | Get | backend/Controllers/V1/ClientsV1Controller.cs:53 | [Authorize] | да |
| GET | api/v1/clients/{id:int} | ClientsV1Controller | GetById | backend/Controllers/V1/ClientsV1Controller.cs:90 | [Authorize] | да |
| POST | api/v1/clients | ClientsV1Controller | Create | backend/Controllers/V1/ClientsV1Controller.cs:125 | [Authorize] | да |
| PUT | api/v1/clients/{id:int} | ClientsV1Controller | Update | backend/Controllers/V1/ClientsV1Controller.cs:155 | [Authorize] | да |
| DELETE | api/v1/clients/{id:int} | ClientsV1Controller | Delete | backend/Controllers/V1/ClientsV1Controller.cs:199 | [Authorize] | да |
| GET | api/v1/payments | PaymentsV1Controller | Get | backend/Controllers/V1/PaymentsV1Controller.cs:32 | [Authorize] | да |
| GET | api/v1/payments/{id:int} | PaymentsV1Controller | GetById | backend/Controllers/V1/PaymentsV1Controller.cs:59 | [Authorize] | да |
| POST | api/v1/payments | PaymentsV1Controller | Create | backend/Controllers/V1/PaymentsV1Controller.cs:79 | [Authorize] | да |
| PUT | api/v1/payments/{id:int} | PaymentsV1Controller | Update | backend/Controllers/V1/PaymentsV1Controller.cs:107 | [Authorize] | да |
| DELETE | api/v1/payments/{id:int} | PaymentsV1Controller | Delete | backend/Controllers/V1/PaymentsV1Controller.cs:171 | [Authorize] | да |
| GET | api/v2/cases | CasesV2Controller | Get | backend/Controllers/V2/CasesV2Controller.cs:261 | [Authorize] | да |
| GET | api/v2/cases/export.csv | CasesV2Controller | ExportRegistryCsv | backend/Controllers/V2/CasesV2Controller.cs:332 | [Authorize] | да |
| GET | api/v2/cases/export.xls | CasesV2Controller | ExportRegistryExcel | backend/Controllers/V2/CasesV2Controller.cs:381 | [Authorize] | да |
| GET | api/v2/cases/export.pdf | CasesV2Controller | ExportRegistryPdf | backend/Controllers/V2/CasesV2Controller.cs:430 | [Authorize] | да |
| GET | api/v2/cases/print | CasesV2Controller | PrintRegistry | backend/Controllers/V2/CasesV2Controller.cs:484 | [Authorize] | да |
| GET | api/v2/cases/document-templates | CasesV2Controller | GetDocumentTemplates | backend/Controllers/V2/CasesV2Controller.cs:1847 | [Authorize] | да |
| GET | api/v2/cases//api/document-templates | CasesV2Controller | GetDocumentTemplates | backend/Controllers/V2/CasesV2Controller.cs:1848 | [Authorize] | да |
| GET | api/v2/cases/document-template-tokens | CasesV2Controller | GetDocumentTemplateTokens | backend/Controllers/V2/CasesV2Controller.cs:1863 | [Authorize] | да |
| GET | api/v2/cases//api/document-template-tokens | CasesV2Controller | GetDocumentTemplateTokens | backend/Controllers/V2/CasesV2Controller.cs:1864 | [Authorize] | да |
| POST | api/v2/cases/document-templates | CasesV2Controller | CreateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:1883 | [Authorize] | да |
| POST | api/v2/cases//api/document-templates | CasesV2Controller | CreateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:1884 | [Authorize] | да |
| PATCH | api/v2/cases/document-templates/{templateKey} | CasesV2Controller | UpdateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:1912 | [Authorize] | да |
| PATCH | api/v2/cases//api/document-templates/{templateKey} | CasesV2Controller | UpdateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:1913 | [Authorize] | да |
| POST | api/v2/cases/document-templates/{templateKey}/render | CasesV2Controller | RenderDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:1949 | [Authorize] | да |
| POST | api/v2/cases//api/document-templates/{templateKey}/render | CasesV2Controller | RenderDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:1950 | [Authorize] | да |
| GET | api/v2/cases/document-types | CasesV2Controller | GetDocumentTypes | backend/Controllers/V2/CasesV2Controller.cs:2017 | [Authorize] | да |
| GET | api/v2/cases//api/case-calendar/events | CasesV2Controller | GetCaseCalendarEvents | backend/Controllers/V2/CasesV2Controller.cs:2049 | [Authorize] | да |
| GET | api/v2/cases//api/case-calendar/summary | CasesV2Controller | GetCaseCalendarSummary | backend/Controllers/V2/CasesV2Controller.cs:2080 | [Authorize] | да |
| GET | api/v2/cases//api/case-calendar/events.ics | CasesV2Controller | ExportCaseCalendarIcs | backend/Controllers/V2/CasesV2Controller.cs:2111 | [Authorize] | да |
| POST | api/v2/cases/procedural-deadlines/calculate | CasesV2Controller | CalculateProceduralDeadlinePreview | backend/Controllers/V2/CasesV2Controller.cs:2168 | [Authorize] | да |
| GET | api/v2/cases/procedural-deadlines/calendar-settings | CasesV2Controller | GetProceduralDeadlineCalendarSettings | backend/Controllers/V2/CasesV2Controller.cs:2204 | [Authorize] | да |
| PUT | api/v2/cases/procedural-deadlines/calendar-settings | CasesV2Controller | UpdateProceduralDeadlineCalendarSettings | backend/Controllers/V2/CasesV2Controller.cs:2215 | [Authorize] | да |
| GET | api/v2/cases/court-directory | CasesV2Controller | GetCourtDirectory | backend/Controllers/V2/CasesV2Controller.cs:2241 | [Authorize] | да |
| POST | api/v2/cases/court-directory | CasesV2Controller | CreateCourtDirectoryItem | backend/Controllers/V2/CasesV2Controller.cs:2266 | [Authorize] | да |
| PUT | api/v2/cases/court-directory/{courtId:int} | CasesV2Controller | UpdateCourtDirectoryItem | backend/Controllers/V2/CasesV2Controller.cs:2310 | [Authorize] | да |
| GET | api/v2/cases/{id:int} | CasesV2Controller | GetById | backend/Controllers/V2/CasesV2Controller.cs:2351 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/procedural-deadlines | CasesV2Controller | GetProceduralDeadlines | backend/Controllers/V2/CasesV2Controller.cs:2408 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/procedural-deadlines | CasesV2Controller | CreateProceduralDeadline | backend/Controllers/V2/CasesV2Controller.cs:2444 | [Authorize] | да |
| PUT | api/v2/cases/{id:int}/procedural-deadlines/{deadlineId:int} | CasesV2Controller | UpdateProceduralDeadline | backend/Controllers/V2/CasesV2Controller.cs:2523 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/procedural-deadlines/{deadlineId:int}/close | CasesV2Controller | CloseProceduralDeadline | backend/Controllers/V2/CasesV2Controller.cs:2624 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/court-instances | CasesV2Controller | GetCourtInstances | backend/Controllers/V2/CasesV2Controller.cs:2678 | [Authorize] | да |
| PUT | api/v2/cases/{id:int}/court-instances/{instance} | CasesV2Controller | UpsertCourtInstance | backend/Controllers/V2/CasesV2Controller.cs:2721 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/court-instances/{instance}/complaints | CasesV2Controller | CreateCourtComplaint | backend/Controllers/V2/CasesV2Controller.cs:2798 | [Authorize] | да |
| PUT | api/v2/cases/{id:int}/court-instances/{instance}/complaints/{complaintId:int} | CasesV2Controller | UpdateCourtComplaint | backend/Controllers/V2/CasesV2Controller.cs:2862 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int}/court-instances/{instance}/complaints/{complaintId:int} | CasesV2Controller | ArchiveCourtComplaint | backend/Controllers/V2/CasesV2Controller.cs:2929 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/court-events | CasesV2Controller | GetCourtEvents | backend/Controllers/V2/CasesV2Controller.cs:2982 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/court-events | CasesV2Controller | CreateCourtEvent | backend/Controllers/V2/CasesV2Controller.cs:3017 | [Authorize] | да |
| PUT | api/v2/cases/{id:int}/court-events/{courtEventId:int} | CasesV2Controller | UpdateCourtEvent | backend/Controllers/V2/CasesV2Controller.cs:3104 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/court-events/{courtEventId:int}/history | CasesV2Controller | GetCourtEventHistory | backend/Controllers/V2/CasesV2Controller.cs:3189 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/court-parties | CasesV2Controller | GetCourtParties | backend/Controllers/V2/CasesV2Controller.cs:3228 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/court-parties | CasesV2Controller | CreateCourtParty | backend/Controllers/V2/CasesV2Controller.cs:3273 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int}/court-parties/{partyId:int} | CasesV2Controller | ArchiveCourtParty | backend/Controllers/V2/CasesV2Controller.cs:3332 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/access-request | CasesV2Controller | RequestAccess | backend/Controllers/V2/CasesV2Controller.cs:3375 | [Authorize] | да |
| GET | api/v2/cases/access-requests | CasesV2Controller | GetAccessRequests | backend/Controllers/V2/CasesV2Controller.cs:3462 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/access-request/resolve | CasesV2Controller | ResolveAccessRequest | backend/Controllers/V2/CasesV2Controller.cs:3503 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/timeline | CasesV2Controller | GetTimeline | backend/Controllers/V2/CasesV2Controller.cs:3613 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/conversation | CasesV2Controller | GetConversation | backend/Controllers/V2/CasesV2Controller.cs:3646 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/summary | CasesV2Controller | GetSummary | backend/Controllers/V2/CasesV2Controller.cs:3675 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/finance | CasesV2Controller | GetFinance | backend/Controllers/V2/CasesV2Controller.cs:3713 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/documents | CasesV2Controller | GetDocuments | backend/Controllers/V2/CasesV2Controller.cs:3791 | [Authorize] | да |
| GET | api/v2/cases//api/case-documents | CasesV2Controller | GetDocumentRegistry | backend/Controllers/V2/CasesV2Controller.cs:3829 | [Authorize] | да |
| GET | api/v2/cases//api/case-documents/{documentId:int} | CasesV2Controller | GetDocumentById | backend/Controllers/V2/CasesV2Controller.cs:3944 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/documents | CasesV2Controller | UploadDocument | backend/Controllers/V2/CasesV2Controller.cs:3983 | [Authorize] | да |
| POST | api/v2/cases//api/case-documents/{documentId:int}/versions | CasesV2Controller | UploadDocumentVersionById | backend/Controllers/V2/CasesV2Controller.cs:4022 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/documents/generate-template | CasesV2Controller | GenerateDocumentTemplate | backend/Controllers/V2/CasesV2Controller.cs:4068 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/documents/{documentId:int}/download | CasesV2Controller | DownloadDocument | backend/Controllers/V2/CasesV2Controller.cs:4163 | [Authorize] | да |
| GET | api/v2/cases//api/case-documents/{documentId:int}/download | CasesV2Controller | DownloadDocumentById | backend/Controllers/V2/CasesV2Controller.cs:4205 | [Authorize] | да |
| PATCH | api/v2/cases//api/case-documents/{documentId:int} | CasesV2Controller | UpdateDocumentById | backend/Controllers/V2/CasesV2Controller.cs:4242 | [Authorize] | да |
| PATCH | api/v2/cases/{id:int}/documents/{documentId:int} | CasesV2Controller | UpdateDocument | backend/Controllers/V2/CasesV2Controller.cs:4278 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int}/documents/{documentId:int} | CasesV2Controller | DeleteDocument | backend/Controllers/V2/CasesV2Controller.cs:4657 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/activity | CasesV2Controller | MarkActivity | backend/Controllers/V2/CasesV2Controller.cs:5253 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/history | CasesV2Controller | AddHistoryEntry | backend/Controllers/V2/CasesV2Controller.cs:5320 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/conversation | CasesV2Controller | AddConversationEntry | backend/Controllers/V2/CasesV2Controller.cs:5380 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/conversation | CasesV2Controller | AddConversationEntryWithAttachments | backend/Controllers/V2/CasesV2Controller.cs:5417 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/related-parties | CasesV2Controller | GetRelatedParties | backend/Controllers/V2/CasesV2Controller.cs:5455 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/related-parties | CasesV2Controller | AddRelatedParty | backend/Controllers/V2/CasesV2Controller.cs:5501 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int}/related-parties/{partyId:int} | CasesV2Controller | DeleteRelatedParty | backend/Controllers/V2/CasesV2Controller.cs:5745 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/work-order | CasesV2Controller | GetWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:5791 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/work-order/external-approval | CasesV2Controller | GetExternalWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:5824 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/external-approval | CasesV2Controller | CreateExternalWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:5855 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int}/work-order/external-approval | CasesV2Controller | RevokeExternalWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:5892 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/work-order/print | CasesV2Controller | PrintWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:5944 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/generate-document | CasesV2Controller | GenerateWorkOrderDocument | backend/Controllers/V2/CasesV2Controller.cs:5983 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order | CasesV2Controller | CreateWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:6031 | [Authorize] | да |
| PATCH | api/v2/cases/{id:int}/work-order | CasesV2Controller | UpdateWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:6085 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int}/work-order | CasesV2Controller | DeleteWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:6162 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/items | CasesV2Controller | AddWorkOrderItem | backend/Controllers/V2/CasesV2Controller.cs:6195 | [Authorize] | да |
| PATCH | api/v2/cases/{id:int}/work-order/items/{itemId:int} | CasesV2Controller | UpdateWorkOrderItem | backend/Controllers/V2/CasesV2Controller.cs:6240 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int}/work-order/items/{itemId:int} | CasesV2Controller | DeleteWorkOrderItem | backend/Controllers/V2/CasesV2Controller.cs:6290 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/create-invoice | CasesV2Controller | CreateInvoiceFromWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:6330 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/create-act | CasesV2Controller | CreateActFromWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:6462 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/create-contract | CasesV2Controller | CreateContractFromWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:6587 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/submit-approval | CasesV2Controller | SubmitWorkOrderApproval | backend/Controllers/V2/CasesV2Controller.cs:6703 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/approve | CasesV2Controller | ApproveWorkOrder | backend/Controllers/V2/CasesV2Controller.cs:6744 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/work-order/request-revision | CasesV2Controller | RequestWorkOrderRevision | backend/Controllers/V2/CasesV2Controller.cs:6775 | [Authorize] | да |
| GET | api/v2/cases/{id:int}/court-sync/logs | CasesV2Controller | GetCourtSyncLogs | backend/Controllers/V2/CasesV2Controller.cs:6807 | [Authorize] | да |
| GET | api/v2/cases/court-sync/logs | CasesV2Controller | GetCourtSyncJournal | backend/Controllers/V2/CasesV2Controller.cs:6841 | [Authorize] | да |
| GET | api/v2/cases/court-sync/settings | CasesV2Controller | GetCourtSyncSettings | backend/Controllers/V2/CasesV2Controller.cs:6949 | [Authorize] | да |
| GET | api/v2/cases/court-sync/providers | CasesV2Controller | GetCourtSyncProviders | backend/Controllers/V2/CasesV2Controller.cs:6960 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/court-sync/search | CasesV2Controller | SearchCourtSyncCases | backend/Controllers/V2/CasesV2Controller.cs:6991 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/court-sync/bind | CasesV2Controller | BindCourtSyncCase | backend/Controllers/V2/CasesV2Controller.cs:7059 | [Authorize] | да |
| PUT | api/v2/cases/court-sync/settings | CasesV2Controller | UpdateCourtSyncSettings | backend/Controllers/V2/CasesV2Controller.cs:7235 | [Authorize] | да |
| POST | api/v2/cases/{id:int}/court-sync/run | CasesV2Controller | RunCourtSync | backend/Controllers/V2/CasesV2Controller.cs:7259 | [Authorize] | да |
| POST | api/v2/cases | CasesV2Controller | Create | backend/Controllers/V2/CasesV2Controller.cs:8318 | [Authorize] | да |
| PUT | api/v2/cases/{id:int} | CasesV2Controller | Update | backend/Controllers/V2/CasesV2Controller.cs:8358 | [Authorize] | да |
| PATCH | api/v2/cases/{id:int}/control | CasesV2Controller | UpdateControl | backend/Controllers/V2/CasesV2Controller.cs:8429 | [Authorize] | да |
| DELETE | api/v2/cases/{id:int} | CasesV2Controller | Delete | backend/Controllers/V2/CasesV2Controller.cs:8468 | [Authorize] | да |
| POST | api/v2/cases/bulk-update | CasesV2Controller | BulkUpdate | backend/Controllers/V2/CasesV2Controller.cs:8496 | [Authorize] | да |
| GET | api/v2/clients | ClientsV2Controller | Get | backend/Controllers/V2/ClientsV2Controller.cs:91 | [Authorize] | да |
| GET | api/v2/clients/{id:int} | ClientsV2Controller | GetById | backend/Controllers/V2/ClientsV2Controller.cs:531 | [Authorize] | да |
| POST | api/v2/clients | ClientsV2Controller | Create | backend/Controllers/V2/ClientsV2Controller.cs:561 | [Authorize] | да |
| PUT | api/v2/clients/{id:int} | ClientsV2Controller | Update | backend/Controllers/V2/ClientsV2Controller.cs:591 | [Authorize] | да |
| DELETE | api/v2/clients/{id:int} | ClientsV2Controller | Delete | backend/Controllers/V2/ClientsV2Controller.cs:635 | [Authorize] | да |
| GET | api/v2/payments | PaymentsV2Controller | Get | backend/Controllers/V2/PaymentsV2Controller.cs:65 | [Authorize] | да |
| GET | api/v2/payments/export.csv | PaymentsV2Controller | ExportCsv | backend/Controllers/V2/PaymentsV2Controller.cs:289 | [Authorize] | да |
| GET | api/v2/payments/export.xls | PaymentsV2Controller | ExportExcel | backend/Controllers/V2/PaymentsV2Controller.cs:351 | [Authorize] | да |
| GET | api/v2/payments/{id:int} | PaymentsV2Controller | GetById | backend/Controllers/V2/PaymentsV2Controller.cs:413 | [Authorize] | да |
| POST | api/v2/payments | PaymentsV2Controller | Create | backend/Controllers/V2/PaymentsV2Controller.cs:434 | [Authorize] | да |
| PUT | api/v2/payments/{id:int} | PaymentsV2Controller | Update | backend/Controllers/V2/PaymentsV2Controller.cs:477 | [Authorize] | да |
| DELETE | api/v2/payments/{id:int} | PaymentsV2Controller | Delete | backend/Controllers/V2/PaymentsV2Controller.cs:645 | [Authorize] | да |
| GET | api/v2/stats/summary | StatsV2Controller | Summary | backend/Controllers/V2/StatsV2Controller.cs:29 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | нет |
| GET | api/v2/stats/months | StatsV2Controller | Months | backend/Controllers/V2/StatsV2Controller.cs:48 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Payments)] | нет |
| GET | api/yandex-calendar/status | YandexCalendarController | GetStatus | backend/Controllers/YandexCalendarController.cs:26 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет |
| POST | api/yandex-calendar/auth/start | YandexCalendarController | StartAuth | backend/Controllers/YandexCalendarController.cs:40 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| GET | api/yandex-calendar/auth/status/{state} | YandexCalendarController | GetAuthStatus | backend/Controllers/YandexCalendarController.cs:63 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar)] | нет |
| GET | api/yandex-calendar/auth/callback | YandexCalendarController | Callback | backend/Controllers/YandexCalendarController.cs:83 | публичный ([AllowAnonymous]) | нет |
| PUT | api/yandex-calendar/settings | YandexCalendarController | UpdateSettings | backend/Controllers/YandexCalendarController.cs:97 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| POST | api/yandex-calendar/sync-now | YandexCalendarController | SyncNow | backend/Controllers/YandexCalendarController.cs:124 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| DELETE | api/yandex-calendar/connection | YandexCalendarController | Disconnect | backend/Controllers/YandexCalendarController.cs:154 | [Authorize]; [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)] | нет |
| GET | api/yandex-disk/connections | YandexDiskController | GetConnections | backend/Controllers/YandexDiskController.cs:42 | [Authorize] | да |
| PUT | api/yandex-disk/connections | YandexDiskController | UpsertConnection | backend/Controllers/YandexDiskController.cs:53 | [Authorize] | да |
| POST | api/yandex-disk/auth/start | YandexDiskController | StartOAuth | backend/Controllers/YandexDiskController.cs:75 | [Authorize] | да |
| GET | api/yandex-disk/auth/status/{state} | YandexDiskController | GetOAuthStatus | backend/Controllers/YandexDiskController.cs:103 | [Authorize] | да |
| GET | api/yandex-disk/auth/callback | YandexDiskController | OAuthCallback | backend/Controllers/YandexDiskController.cs:123 | публичный ([AllowAnonymous]) | да |
| DELETE | api/yandex-disk/connections/{id} | YandexDiskController | DeleteConnection | backend/Controllers/YandexDiskController.cs:135 | [Authorize] | да |
| POST | api/yandex-disk/connections/{id}/connection-test | YandexDiskController | TestConnection | backend/Controllers/YandexDiskController.cs:157 | [Authorize] | да |
| GET | api/yandex-disk/settings | YandexDiskController | GetSettings | backend/Controllers/YandexDiskController.cs:180 | [Authorize] | да |
| PUT | api/yandex-disk/settings | YandexDiskController | UpdateSettings | backend/Controllers/YandexDiskController.cs:189 | [Authorize] | да |
| POST | api/yandex-disk/connection-test | YandexDiskController | TestConnection | backend/Controllers/YandexDiskController.cs:206 | [Authorize] | да |
| GET | api/yandex-disk/crm-sync/settings | YandexDiskController | GetCrmSyncSettings | backend/Controllers/YandexDiskController.cs:228 | [Authorize] | да |
| GET | api/yandex-disk/crm-sync/preview | YandexDiskController | PreviewCrmSync | backend/Controllers/YandexDiskController.cs:237 | [Authorize] | да |
| GET | api/yandex-disk/crm-sync/status | YandexDiskController | GetCrmSyncStatus | backend/Controllers/YandexDiskController.cs:246 | [Authorize] | да |
| PUT | api/yandex-disk/crm-sync/settings | YandexDiskController | UpdateCrmSyncSettings | backend/Controllers/YandexDiskController.cs:299 | [Authorize] | да |
| GET | api/yandex-disk/resources | YandexDiskController | List | backend/Controllers/YandexDiskController.cs:328 | [Authorize] | да |
| GET | api/yandex-disk/connections/{id}/resources | YandexDiskController | List | backend/Controllers/YandexDiskController.cs:341 | [Authorize] | да |
| POST | api/yandex-disk/folders | YandexDiskController | CreateFolder | backend/Controllers/YandexDiskController.cs:355 | [Authorize] | да |
| POST | api/yandex-disk/connections/{id}/folders | YandexDiskController | CreateFolder | backend/Controllers/YandexDiskController.cs:372 | [Authorize] | да |
| POST | api/yandex-disk/copy | YandexDiskController | Copy | backend/Controllers/YandexDiskController.cs:390 | [Authorize] | да |
| POST | api/yandex-disk/connections/{id}/copy | YandexDiskController | Copy | backend/Controllers/YandexDiskController.cs:407 | [Authorize] | да |
| POST | api/yandex-disk/move | YandexDiskController | Move | backend/Controllers/YandexDiskController.cs:425 | [Authorize] | да |
| POST | api/yandex-disk/connections/{id}/move | YandexDiskController | Move | backend/Controllers/YandexDiskController.cs:442 | [Authorize] | да |
| DELETE | api/yandex-disk/resources | YandexDiskController | Delete | backend/Controllers/YandexDiskController.cs:460 | [Authorize] | да |
| DELETE | api/yandex-disk/connections/{id}/resources | YandexDiskController | Delete | backend/Controllers/YandexDiskController.cs:480 | [Authorize] | да |
| POST | api/yandex-disk/upload | YandexDiskController | Upload | backend/Controllers/YandexDiskController.cs:501 | [Authorize] | да |
| POST | api/yandex-disk/connections/{id}/upload | YandexDiskController | Upload | backend/Controllers/YandexDiskController.cs:529 | [Authorize] | да |
| GET | api/yandex-disk/download | YandexDiskController | Download | backend/Controllers/YandexDiskController.cs:558 | [Authorize] | да |
| GET | api/yandex-disk/connections/{id}/download | YandexDiskController | Download | backend/Controllers/YandexDiskController.cs:580 | [Authorize] | да |
| GET | api/yandex-disk/text | YandexDiskController | GetText | backend/Controllers/YandexDiskController.cs:602 | [Authorize] | да |
| GET | api/yandex-disk/connections/{id}/text | YandexDiskController | GetText | backend/Controllers/YandexDiskController.cs:611 | [Authorize] | да |
| PUT | api/yandex-disk/text | YandexDiskController | SaveText | backend/Controllers/YandexDiskController.cs:620 | [Authorize] | да |
| PUT | api/yandex-disk/connections/{id}/text | YandexDiskController | SaveText | backend/Controllers/YandexDiskController.cs:637 | [Authorize] | да |
| POST | api/yandex-disk/crm/case-documents/{documentId:int}/upload | YandexDiskController | UploadCaseDocument | backend/Controllers/YandexDiskController.cs:655 | [Authorize] | да |
| POST | api/yandex-disk/crm/tasks/{taskId:int}/files/{fileId:int}/upload | YandexDiskController | UploadTaskFile | backend/Controllers/YandexDiskController.cs:744 | [Authorize] | да |
