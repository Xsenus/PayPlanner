import { describe, expect, it } from 'vitest';

import type { TaskJournalUpsertPayload } from './taskJournalStore';
import { applyTaskTemplateDefaults } from './taskTemplateApplication';

function createPayload(overrides: Partial<TaskJournalUpsertPayload> = {}): TaskJournalUpsertPayload {
  return {
    isInternal: false,
    clientId: null,
    legalEntityId: null,
    clientCaseId: null,
    objectName: '',
    title: '',
    description: '',
    assigneeUserId: null,
    assigneeName: '',
    dueAt: '',
    approvalDueAt: null,
    plannedDurationMinutes: 60,
    contractorPaymentEnabled: false,
    contractorPaymentAmount: null,
    contractorPaymentDueDate: null,
    contractorPaymentOnResult: false,
    contractorPaymentResultDelayDays: null,
    type: 'task',
    priority: 'normal',
    status: 'new',
    isUnderControl: false,
    result: '',
    reminders: ['atDue'],
    channels: ['inApp'],
    tags: [],
    participants: [],
    ...overrides,
  };
}

describe('applyTaskTemplateDefaults', () => {
  it('fills empty task fields from the template', () => {
    const result = applyTaskTemplateDefaults(createPayload(), {
      title: 'Проверить документы',
      description: 'Сверить комплект документов по делу.',
      priority: 'high',
      dueAt: '2026-07-10T06:00:00.000Z',
      plannedDurationMinutes: 90,
      tags: ['дело', 'документы'],
    }, { replaceFields: ['priority', 'dueAt', 'plannedDurationMinutes'] });

    expect(result.title).toBe('Проверить документы');
    expect(result.description).toBe('Сверить комплект документов по делу.');
    expect(result.priority).toBe('high');
    expect(result.dueAt).toBe('2026-07-10T06:00:00.000Z');
    expect(result.plannedDurationMinutes).toBe(90);
    expect(result.tags).toEqual(['дело', 'документы']);
  });

  it('keeps protected manual values and merges list fields', () => {
    const result = applyTaskTemplateDefaults(createPayload({
      title: 'Ручной заголовок',
      dueAt: '2026-07-09T06:00:00.000Z',
      tags: ['срочно'],
      reminders: ['before1h'],
      channels: ['telegram'],
    }), {
      title: 'Шаблонный заголовок',
      dueAt: '2026-07-10T06:00:00.000Z',
      tags: ['срочно', 'дело'],
      reminders: ['before1h', 'atDue'],
      channels: ['inApp'],
    }, { protectedFields: ['title', 'dueAt'] });

    expect(result.title).toBe('Ручной заголовок');
    expect(result.dueAt).toBe('2026-07-09T06:00:00.000Z');
    expect(result.tags).toEqual(['срочно', 'дело']);
    expect(result.reminders).toEqual(['before1h', 'atDue']);
    expect(result.channels).toEqual(['telegram', 'inApp']);
  });

  it('merges template participants without duplicating existing users', () => {
    const result = applyTaskTemplateDefaults(createPayload({
      participants: [
        { userId: 8, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0 },
        { userId: 10, role: 'observer', displayName: 'Первый наблюдатель', sortOrder: 0 },
      ],
    }), {
      participants: [
        { userId: 8, role: 'approver', displayName: 'Первый согласующий' },
        { userId: 9, role: 'approver', displayName: 'Второй согласующий' },
        { userId: 11, role: 'observer', displayName: 'Второй наблюдатель' },
      ],
    });

    expect(result.participants).toEqual([
      expect.objectContaining({ userId: 8, role: 'approver', displayName: 'Первый согласующий', sortOrder: 0, approvalStatus: 'pending' }),
      expect.objectContaining({ userId: 10, role: 'observer', displayName: 'Первый наблюдатель', sortOrder: 0, approvalStatus: 'pending' }),
      expect.objectContaining({ userId: 9, role: 'approver', displayName: 'Второй согласующий', sortOrder: 1, approvalStatus: 'pending' }),
      expect.objectContaining({ userId: 11, role: 'observer', displayName: 'Второй наблюдатель', sortOrder: 1, approvalStatus: 'pending' }),
    ]);
  });

  it('can keep participants untouched when the form marks them as protected', () => {
    const result = applyTaskTemplateDefaults(createPayload({
      participants: [
        { userId: 8, role: 'approver', displayName: 'Ручной согласующий' },
      ],
    }), {
      participants: [
        { userId: 9, role: 'approver', displayName: 'Шаблонный согласующий' },
      ],
    }, { protectedFields: ['participants'] });

    expect(result.participants).toEqual([
      { userId: 8, role: 'approver', displayName: 'Ручной согласующий' },
    ]);
  });
});
