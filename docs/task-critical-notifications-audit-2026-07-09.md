# Аудит уведомлений и принудительных окон задач

Дата аудита: 9 июля 2026.
Связанный roadmap: `task-critical-modals-notification-settings-roadmap-2026-07-09.md`.

## Цель аудита

Зафиксировать, какие механизмы уведомлений уже есть в CRM, где они подключены и какие пробелы мешают довести критичные процессы задач до обязательных всплывающих окон с настройками включения/выключения.

## Backend: что уже есть

- Единый каталог типов уведомлений хранится в `backend/Services/Notifications/NotificationContracts.cs`.
- Для задач уже заведены типы:
  - `taskAssigned`;
  - `taskReminder`;
  - `taskChanged`;
  - `taskComment`;
  - `taskCompleted`;
  - `taskCancelled`;
  - `taskScheduleConflict`;
  - `taskApprovalRequested`;
  - `taskApproved`;
  - `taskRevisionRequested`;
  - `taskDeadlineChangeRequested`;
  - `taskDeadlineChangeApproved`;
  - `taskDeadlineChangeRejected`;
  - `taskApprovalOverdue`.
- У типов есть домен, приоритет, список каналов, признак `requiresAction`, toast-настройки и Web Push eligibility.
- Создание пользовательских уведомлений централизовано в `NotificationService.CreateUserNotificationAsync`: запись попадает в `UserNotifications`, после чего публикуется realtime-событие и при доступном канале отправляется Web Push.
- Для задач уже реализованы методы уведомлений:
  - `NotifyTaskApprovalRequestedAsync`;
  - `NotifyTaskApprovalDecisionAsync`;
  - `NotifyTaskDeadlineChangeRequestedAsync`;
  - `NotifyTaskDeadlineChangeApprovedAsync`;
  - `NotifyTaskDeadlineChangeRejectedAsync`;
  - `NotifyTaskApprovalOverdueAsync`;
  - `NotifyTaskChangedAsync`, `NotifyTaskAssignedAsync`, `NotifyTaskReminderAsync` и другие базовые события.
- Настройки каналов учитываются через `ResolveUserNotificationChannelsAsync`, `IsUserNotificationChannelEnabledAsync` и role/user preferences.
- Есть quiet hours / режим "не беспокоить" через `notifications.doNotDisturb.v1`; критичные события могут обходить тихий режим, если включено `AllowCritical`.
- Ролевые настройки уведомлений есть через `/api/notifications/role-preferences`; они хранят отключенные типы и отключенные каналы.
- In-app уведомление не создается, если канал `inApp` отключен для типа. Это важно: текущее отключение типа/канала может убрать событие из центра уведомлений, а roadmap требует, чтобы отключение всплывающего окна не отключало журнал.

## Frontend: что уже есть

- `NotificationCenter` показывает журнал уведомлений: фильтры по статусу, типу, периоду, роли в задаче, клиенту/сотруднику, признак "требует действий", массовое прочтение, архив и удаление.
- В центре уведомлений есть пользовательские предпочтения типов и каналов, а также настройки тихого режима.
- `RealtimeNotificationToasts` подключается к SignalR hub, показывает toast, повторяет пропущенные уведомления после возврата вкладки и включает fallback polling раз в 30 секунд, если realtime недоступен.
- `RealtimeNotificationToasts` специально не показывает toast для уведомлений, которые должны быть принудительными: `taskRevisionRequested` и завершенное `taskApproved`.
- `ForcedTaskRevisionModal` уже существует как отдельное обязательное окно. Оно:
  - загружает непрочитанные `taskRevisionRequested`;
  - дополнительно загружает `taskApproved`, если по metadata задача уже завершена;
  - показывает одно окно за раз;
  - перечитывает центр уведомлений раз в 30 секунд и по событию обновления;
  - закрывается через `markUserNotificationRead`;
  - умеет открыть задачу.
- `WebPushDevicePanel` в профиле управляет подпиской текущего устройства на Web Push и показывает активные устройства.
- `NotificationSettings` в настройках администратора управляет каналами, шаблонами, ролевыми правилами уведомлений, отправителями, SMS/Telegram/email и диагностикой.

## Текущие forced-сценарии

| Событие | In-app | Toast | Web Push | Forced modal | Комментарий |
| --- | --- | --- | --- | --- | --- |
| Возврат задачи на доработку (`taskRevisionRequested`) | Да | Исключен из toast | По настройкам | Да, `ForcedTaskRevisionModal` | Окно информационное, пользователь подтверждает прочтение или открывает задачу. |
| Завершенное согласование (`taskApproved` + completed metadata) | Да | Исключен из toast | По настройкам | Да, `ForcedTaskRevisionModal` | Окно подтверждает, что задача согласована и закрыта. |
| Запрос переноса срока (`taskDeadlineChangeRequested`) | Да | Да, если включен канал | По настройкам | Нет | Главный пробел по замечанию клиента: нет обязательного окна с действиями согласовать/отказать. |
| Ожидает согласования (`taskApprovalRequested`) | Да | Да, если включен канал | По настройкам | Нет | Согласующий может пропустить задачу, нет центрального обязательного окна решения. |
| Просрочено согласование (`taskApprovalOverdue`) | Да | Да, критичный тип | По настройкам | Нет | Нет отдельного обязательного окна или очереди. |
| Решение по переносу срока (`taskDeadlineChangeApproved/Rejected`) | Да | По настройкам | По настройкам | Нет | Достаточно информационного события, но может быть частью общей очереди уведомлений без действий. |

## Главные пробелы

- Нет единого UI-слоя принудительных окон. Сейчас forced-поведение зашито в один компонент под конкретные типы.
- Нет общей очереди принудительных событий: если добавлять новые forced-сценарии отдельными компонентами, они могут конкурировать друг с другом за экран.
- Нет отдельной настройки "показывать принудительное окно". Текущие настройки управляют типом и каналами уведомлений, а не presentation-mode события.
- Отключение `inApp` сейчас может предотвратить создание записи в `UserNotifications`. Для roadmap 5 нужно разделить "журнал события" и "всплывающее окно", чтобы отключение окна не отключало центр уведомлений и историю.
- Запрос переноса срока уже создает `taskDeadlineChangeRequested` с metadata `deadlineChangeRequestId`, `requestedDueAt`, `reason`, `reviewerUserId`, но frontend не использует это как обязательное actionable-окно.
- Согласование результата задачи уже создает `taskApprovalRequested`, но frontend не показывает принудительное окно с действиями согласовать/вернуть на доработку.
- Forced modal сейчас сам загружает уведомления из inbox. Для масштабирования лучше сделать общий источник очереди forced-событий, который фильтрует unread + requiresAction + enabled forced presentation.
- Нет явного dedupe/lock для forced-окна на уровне UI-очереди; есть только anti-duplicate при создании in-app уведомлений и локальный `acknowledgedIdsRef` внутри одного компонента.

## Рекомендуемая база для следующего шага

- Составить матрицу критичных событий задач отдельно от каналов доставки:
  - тип уведомления;
  - получатель;
  - нужно ли действие;
  - какие действия доступны прямо из окна;
  - можно ли отключить forced-окно;
  - остается ли запись в центре уведомлений при отключенном forced-окне;
  - что происходит после обновления страницы.
- Проектировать forced presentation как слой поверх `UserNotifications`, а не как новый отдельный журнал.
- Использовать существующие признаки `requiresAction`, `priority`, `metadataJson`, `taskId`, `objectType`, `objectId`, `toastEnabled`, но добавить отдельную настройку/маппинг для forced modal.
- Сохранить существующий `ForcedTaskRevisionModal` как сценарий, который будет перенесен в общий слой очереди.
- Для запроса переноса срока первым подключать actionable-окно по `taskDeadlineChangeRequested`, потому что в metadata уже есть `deadlineChangeRequestId` и backend API согласования/отказа уже реализован.

## Проверенные файлы и модули

- `backend/Services/Notifications/NotificationContracts.cs`
- `backend/Services/Notifications/NotificationService.cs`
- `backend/Controllers/NotificationsController.cs`
- `backend/Controllers/NotificationInboxController.cs`
- `backend/Controllers/PushSubscriptionsController.cs`
- `backend/Services/Notifications/WebPushNotificationService.cs`
- `backend/Hubs/NotificationsHub.cs`
- `src/App.tsx`
- `src/components/Notifications/NotificationCenter.tsx`
- `src/components/Notifications/RealtimeNotificationToasts.tsx`
- `src/components/Notifications/ForcedTaskRevisionModal.tsx`
- `src/components/Notifications/WebPushDevicePanel.tsx`
- `src/components/Settings/NotificationSettings.tsx`
- `src/utils/userNotificationPreferences.ts`
- `src/services/realtimeNotifications.ts`
- `src/services/api.ts`

