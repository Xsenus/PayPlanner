import { describe, expect, it } from 'vitest';
import {
  formatAuditValue,
  formatAuditChangeSummary,
  formatObjectAuditText,
  getObjectAuditActorLabel,
  getObjectAuditActionLabel,
  getObjectAuditTypeLabel,
  parseAuditFieldChanges,
  getObjectAuditFieldLabel,
  parseAuditChangedFields,
  parseAuditReason,
} from './objectAudit';

describe('objectAudit utilities', () => {
  it('parses changed fields list from metadata json', () => {
    expect(parseAuditChangedFields('["Status","ExpenseClientId"]')).toEqual([
      'Status',
      'ExpenseClientId',
    ]);
    expect(
      parseAuditChangedFields(
        '{"changedFields":["Status","ExpenseClientId"],"changes":[{"field":"Status","oldValue":"Pending","newValue":"Paid"}]}',
      ),
    ).toEqual(['Status', 'ExpenseClientId']);
    expect(parseAuditReason('{"changedFields":["Status"],"reason":"Клиент прислал отказ"}')).toBe('Клиент прислал отказ');
  });

  it('returns empty list for invalid metadata', () => {
    expect(parseAuditChangedFields('{bad json')).toEqual([]);
    expect(parseAuditChangedFields(null)).toEqual([]);
    expect(parseAuditFieldChanges('{bad json')).toEqual([]);
  });

  it('maps known field labels with object-specific overrides', () => {
    expect(getObjectAuditFieldLabel('payment', 'CounterpartyName')).toBe('Клиент / поставщик');
    expect(getObjectAuditFieldLabel('client-case', 'Name')).toBe('Название дела');
    expect(getObjectAuditFieldLabel('unknown', 'UpdatedByUserName')).toBe('Изменил');
    expect(getObjectAuditFieldLabel('user', 'FullName')).toBe('ФИО пользователя');
    expect(getObjectAuditFieldLabel('role', 'Name')).toBe('Название роли');
    expect(getObjectAuditFieldLabel('payment', 'Legal Entity Id')).toBe('Юридическое лицо');
    expect(getObjectAuditFieldLabel('payment', 'Payment Status Id')).toBe('Статус оплаты');
    expect(getObjectAuditFieldLabel('payment', 'Advance Review Status')).toBe('Контроль аванса');
    expect(getObjectAuditFieldLabel('payment', 'Advance Reviewed By User Name')).toBe('Кто принял решение по авансу');
    expect(getObjectAuditFieldLabel('payment', 'Client Case Reference')).toBe('Дело');
    expect(getObjectAuditFieldLabel('payment', 'Invoice Workflow Status')).toBe('Статус документа');
    expect(getObjectAuditFieldLabel('payment', 'System Notes')).toBe('Служебные заметки');
    expect(getObjectAuditFieldLabel('invoice-line-item', 'Sort Order')).toBe('Порядок строки');
    expect(getObjectAuditFieldLabel('client-case-work-order', 'ApprovalDecisionByUserName')).toBe('Согласующий');
    expect(getObjectAuditFieldLabel('client-case-work-order-item', 'Unit Price')).toBe('Цена');
    expect(getObjectAuditFieldLabel('client-case-related-party', 'Archive Reason')).toBe('Причина архивирования');
    expect(getObjectAuditFieldLabel('client-case-related-party', 'DisplayName')).toBe('Участник');
    expect(getObjectAuditFieldLabel('client-case', 'Event History')).toBe('История событий');
    expect(getObjectAuditFieldLabel('task-journal-item', 'Assignee Name')).toBe('Исполнитель');
    expect(getObjectAuditFieldLabel('task-journal-item', 'Channels Json')).toBe('Каналы уведомлений');
    expect(getObjectAuditFieldLabel('task-journal-item', 'Reminders Json')).toBe('Напоминания');
    expect(getObjectAuditFieldLabel('task-journal-item', 'Contractor Payment Id')).toBe('Платеж подрядчику');
    expect(getObjectAuditFieldLabel('task-journal-item', 'Result')).toBe('Результат задачи');
    expect(getObjectAuditFieldLabel('client', 'Client Status Id')).toBe('Статус клиента');
    expect(getObjectAuditFieldLabel('client-case', 'Last Viewed At')).toBe('Последний просмотр');
    expect(getObjectAuditFieldLabel('client-case', 'Last Viewed By User Id')).toBe('Кто просматривал');
    expect(getObjectAuditFieldLabel('client-case', 'Last Viewed By User Name')).toBe('Кто просматривал');
    expect(getObjectAuditFieldLabel('calendar-sync', 'External Calendar Sync Enabled')).toBe('Внешняя синхронизация календарей');
    expect(getObjectAuditFieldLabel('calendar-sync', 'Google Calendar Sync Enabled')).toBe('Синхронизация Google Calendar');
    expect(getObjectAuditFieldLabel('calendar-sync', 'Yandex Calendar Sync Enabled')).toBe('Синхронизация Яндекс.Календаря');
    expect(getObjectAuditFieldLabel('calendar-sync', 'SyncMode')).toBe('Главный источник событий');
    expect(getObjectAuditFieldLabel('calendar-sync', 'DryRunConfirmed')).toBe('Dry-run на копии базы');
    expect(getObjectAuditFieldLabel('user', 'Last Activity Action')).toBe('Последнее действие');
    expect(getObjectAuditFieldLabel('user', 'Last Activity At')).toBe('Время последней активности');
    expect(getObjectAuditFieldLabel('user', 'Last Activity Section')).toBe('Последний раздел');
    expect(getObjectAuditFieldLabel('user', 'Last Login At')).toBe('Последний вход');
  });

  it('builds readable change summary', () => {
    expect(formatAuditChangeSummary('payment', '["Status"]')).toBe('Изменено поле: Статус платежа');
    expect(formatAuditChangeSummary('payment', '["Invoice Workflow Status","IsPaid","LastPaymentDate","PaidAmount"]')).toBe(
      'Изменены поля: Статус документа, Оплачен, Дата последней оплаты и ещё 1',
    );
    expect(formatAuditChangeSummary('payment', '["Status","ExpenseClientId","ClientCaseId","Amount"]')).toBe(
      'Изменены поля: Статус платежа, Клиент расхода, Дело и ещё 1',
    );
    expect(formatAuditChangeSummary('user', '["FullName","RoleId"]')).toBe(
      'Изменены поля: ФИО пользователя, Роль',
    );
  });

  it('parses detailed field changes from structured metadata', () => {
    expect(
      parseAuditFieldChanges(
        '{"changedFields":["Status","Amount"],"changes":[{"field":"Status","oldValue":"Pending","newValue":"Paid"},{"field":"Amount","oldValue":"1000","newValue":"1500"}]}',
      ),
    ).toEqual([
      { field: 'Status', oldValue: 'Pending', newValue: 'Paid' },
      { field: 'Amount', oldValue: '1000', newValue: '1500' },
    ]);
  });

  it('formats empty audit values with placeholder', () => {
    expect(formatAuditValue(null)).toBe('—');
    expect(formatAuditValue('')).toBe('—');
    expect(formatAuditValue('  новое значение  ')).toBe('новое значение');
    expect(formatAuditValue('true')).toBe('Да');
    expect(formatAuditValue('0', 'payment', 'Is Paid')).toBe('Нет');
    expect(formatAuditValue('Overdue')).toBe('Просрочено');
    expect(formatAuditValue('Signed')).toBe('Подписан клиентом');
    expect(formatAuditValue('SignedByClient')).toBe('Подписан клиентом');
    expect(formatAuditValue('Concluded')).toBe('Подписан клиентом');
    expect(formatAuditValue('RequiresClientSignature')).toBe('Требуется подпись клиента');
    expect(formatAuditValue('OnApproval')).toBe('На согласовании');
    expect(formatAuditValue('ApprovalRejected')).toBe('Отказ в согласовании');
    expect(formatAuditValue('NeedsCorrection')).toBe('Требует исправления');
    expect(formatAuditValue('PartiallyPaid')).toBe('Частично оплачен');
    expect(formatAuditValue('RequiresDecision')).toBe('Требует решения по авансу');
    expect(formatAuditValue('AcceptedAsAdvance')).toBe('Принят как аванс');
    expect(formatAuditValue('Allocated')).toBe('Распределен');
    expect(formatAuditValue('Ignored')).toBe('Исключен из контроля');
    expect(formatAuditValue('crmMaster')).toBe('CRM главная');
    expect(formatAuditValue('manualConfirmation')).toBe('Ручное подтверждение');
    expect(formatAuditValue('open_tab', 'user', 'Last Activity Action')).toBe('Открытие раздела');
    expect(formatAuditValue('login', 'user', 'Last Activity Action')).toBe('Вход в систему');
    expect(formatAuditValue('settings', 'user', 'Last Activity Section')).toBe('Настройки');
    expect(formatAuditValue('auth', 'user', 'Last Activity Section')).toBe('Авторизация');
    expect(formatAuditValue('inProgress', 'task-journal-item', 'Status')).toBe('В работе');
    expect(formatAuditValue('done', 'task-journal-item', 'Status')).toBe('Выполнена');
    expect(formatAuditValue('new', 'task-journal-item', 'Status')).toBe('Новая');
    expect(formatAuditValue('waiting', 'task-journal-item', 'Status')).toBe('Ожидает');
    expect(formatAuditValue('task', 'task-journal-item', 'Type')).toBe('Задача');
    expect(formatAuditValue('courtHearing', 'task-journal-item', 'Type')).toBe('Судебное заседание');
    expect(formatAuditValue('2026-04-27 00:00:00', 'payment', 'Date')).toBe('27.04.2026');
    expect(formatAuditValue('2026-06-04T20:30:00Z', 'task-journal-item', 'DueAt', 'Europe/Samara')).toBe('05.06.2026, 00:30');
    expect(formatAuditValue('1500.00', 'payment', 'Amount')).toMatch(/1\s*500\s*₽/u);
    expect(formatAuditValue('42', 'payment', 'Payment Status Id')).toBe('ID 42');
  });

  it('translates technical audit labels and hides Codex actors', () => {
    expect(getObjectAuditActionLabel('DataRepair')).toBe('Исправление данных');
    expect(getObjectAuditActionLabel('Automation')).toBe('Автоконтроль');
    expect(getObjectAuditActionLabel('Downloaded')).toBe('Скачивание');
    expect(getObjectAuditActionLabel('Exported')).toBe('Экспорт');
    expect(getObjectAuditActionLabel('ConnectionTested')).toBe('Проверка подключения');
    expect(getObjectAuditActionLabel('ConnectionUpdated')).toBe('Обновление подключения');
    expect(getObjectAuditActionLabel('ObjectMoved')).toBe('Перемещение объекта');
    expect(getObjectAuditTypeLabel('s3 storage')).toBe('S3-хранилище');
    expect(getObjectAuditTypeLabel('s3-storage')).toBe('S3-хранилище');
    expect(getObjectAuditTypeLabel('yandex-disk')).toBe('Я.Диск');
    expect(getObjectAuditTypeLabel('task-journal-item')).toBe('Задача');
    expect(getObjectAuditTypeLabel('calendar-sync')).toBe('Синхронизация календаря');
    expect(getObjectAuditTypeLabel('invoice-line-item')).toBe('Позиция счета');
    expect(getObjectAuditTypeLabel('client-case-document')).toBe('Документ дела');
    expect(getObjectAuditTypeLabel('client-case-work-order')).toBe('Заказ-наряд');
    expect(getObjectAuditTypeLabel('client-case-related-party')).toBe('Связанное лицо дела');
    expect(getObjectAuditFieldLabel('client-case-document', 'DocumentType')).toBe('Тип документа');
    expect(formatAuditValue('pending_approval')).toBe('На согласовании');
    expect(formatAuditValue('invoice_created')).toBe('Счет создан');
    expect(formatAuditValue('legalEntity')).toBe('Юридическое лицо');
    expect(getObjectAuditTypeLabel('money-operation')).toBe('Денежная операция');
    expect(getObjectAuditActorLabel('Codex data repair')).toBe('Администратор');
    expect(getObjectAuditActorLabel('Codex maintenance')).toBe('Администратор');
    expect(formatObjectAuditText('Codex maintenance updated System Notes')).toBe(
      'Администратор изменил Служебные заметки',
    );
    expect(formatObjectAuditText('ConnectionUpdated for s3 storage')).toBe('Подключение обновлено для S3-хранилища');
  });
});
