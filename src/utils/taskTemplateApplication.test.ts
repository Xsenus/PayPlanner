import { describe, expect, it } from 'vitest';

import type { TaskJournalTemplate, TaskJournalUpsertPayload } from './taskJournalStore';
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

  it('applies values mapped from a saved task template without overwriting protected case context', () => {
    const savedTemplate: TaskJournalTemplate = {
      id: 17,
      name: 'Case checklist',
      description: 'Reusable saved template',
      title: 'Prepare case checklist',
      taskDescription: 'Check documents and prepare the next action plan.',
      type: 'document',
      priority: 'urgent',
      plannedDurationMinutes: 120,
      dueOffsetMinutes: 180,
      assigneeUserId: 12,
      assigneeName: 'Template assignee',
      bindingType: 'caseStage',
      bindingKey: 'appeal',
      isActive: true,
      reminders: ['before1d', 'atDue'],
      channels: ['inApp', 'telegram'],
      tags: ['case', 'checklist'],
      participants: [
        { userId: 21, role: 'approver', displayName: 'Template approver', sortOrder: 0 },
        { userId: 22, role: 'observer', displayName: 'Template observer', sortOrder: 0 },
      ],
      createdByUserId: 5,
      createdByName: 'Template author',
      createdAt: '2026-07-09T05:00:00.000Z',
      updatedAt: '2026-07-09T06:00:00.000Z',
    };

    const result = applyTaskTemplateDefaults(createPayload({
      clientId: 101,
      clientCaseId: 202,
      objectName: 'Existing case context',
      title: '',
      description: '',
      tags: ['manual'],
      reminders: ['before1h'],
      channels: ['email'],
    }), {
      title: savedTemplate.title,
      description: savedTemplate.taskDescription ?? '',
      type: savedTemplate.type,
      priority: savedTemplate.priority,
      plannedDurationMinutes: savedTemplate.plannedDurationMinutes,
      assigneeUserId: savedTemplate.assigneeUserId,
      assigneeName: savedTemplate.assigneeName,
      reminders: savedTemplate.reminders,
      channels: savedTemplate.channels,
      tags: savedTemplate.tags,
      participants: savedTemplate.participants.map((participant) => ({
        userId: participant.userId,
        role: participant.role,
        displayName: participant.displayName,
        email: participant.email,
        sortOrder: participant.sortOrder,
      })),
    }, {
      protectedFields: ['clientId', 'clientCaseId', 'objectName'],
      replaceFields: ['type', 'priority', 'plannedDurationMinutes', 'assigneeUserId', 'assigneeName'],
    });

    expect(result.clientId).toBe(101);
    expect(result.clientCaseId).toBe(202);
    expect(result.objectName).toBe('Existing case context');
    expect(result.title).toBe('Prepare case checklist');
    expect(result.description).toBe('Check documents and prepare the next action plan.');
    expect(result.type).toBe('document');
    expect(result.priority).toBe('urgent');
    expect(result.plannedDurationMinutes).toBe(120);
    expect(result.assigneeUserId).toBe(12);
    expect(result.assigneeName).toBe('Template assignee');
    expect(result.reminders).toEqual(['before1h', 'before1d', 'atDue']);
    expect(result.channels).toEqual(['email', 'inApp', 'telegram']);
    expect(result.tags).toEqual(['manual', 'case', 'checklist']);
    expect(result.participants).toEqual([
      expect.objectContaining({ userId: 21, role: 'approver', displayName: 'Template approver', sortOrder: 0, approvalStatus: 'pending' }),
      expect.objectContaining({ userId: 22, role: 'observer', displayName: 'Template observer', sortOrder: 0, approvalStatus: 'pending' }),
    ]);
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
