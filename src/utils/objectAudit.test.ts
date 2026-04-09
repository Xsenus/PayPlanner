import { describe, expect, it } from 'vitest';
import {
  formatAuditValue,
  formatAuditChangeSummary,
  parseAuditFieldChanges,
  getObjectAuditFieldLabel,
  parseAuditChangedFields,
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
  });

  it('builds readable change summary', () => {
    expect(formatAuditChangeSummary('payment', '["Status"]')).toBe('Изменено поле: Статус платежа');
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
  });
});
