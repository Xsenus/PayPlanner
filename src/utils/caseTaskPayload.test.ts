import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ClientCase } from '../types';
import { buildCaseTaskPayload, getCaseStageTaskTemplates } from './caseTaskPayload';
import { fromTimeZoneDateTimeInput } from './timeZoneDate';

function createCase(overrides: Partial<ClientCase> = {}): ClientCase {
  return {
    id: 15,
    clientId: 12,
    legalEntityId: null,
    title: 'ЗЕМЦОВ - банкротство',
    priority: 'normal',
    caseType: 'Банкротство',
    stage: 'Судебное производство',
    ...overrides,
  } as ClientCase;
}

describe('buildCaseTaskPayload', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('builds the default due date at 10:00 in the selected user time zone', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-03T22:30:00Z'));

    const payload = buildCaseTaskPayload(createCase(), 'Europe/Samara');

    expect(payload.dueAt).toBe(fromTimeZoneDateTimeInput('2026-06-05T10:00', 'Europe/Samara'));
  });

  it('suggests stage templates and applies the selected template to the task payload', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-03T22:30:00Z'));

    const templates = getCaseStageTaskTemplates(createCase({ stage: 'Апелляция' }));
    const template = templates[0];
    const payload = buildCaseTaskPayload(createCase({ stage: 'Апелляция' }), 'Europe/Samara', template);

    expect(template.stageLabel).toBe('Апелляция');
    expect(payload.title).toBe('Проверить срок и материалы для апелляции');
    expect(payload.description).toContain('Этап: Апелляция');
    expect(payload.priority).toBe('urgent');
    expect(payload.tags).toContain('апелляция');
    expect(payload.clientCaseId).toBe(15);
  });

  it('applies extended case template fields while keeping the case context protected', () => {
    const payload = buildCaseTaskPayload(createCase({ responsibleUserId: 7 }), 'Europe/Samara', {
      key: 'custom-stage-template',
      stageLabel: 'Индивидуальный этап',
      title: 'Шаблонная задача по делу',
      description: 'Описание из шаблона.',
      priority: 'high',
      plannedDurationMinutes: 120,
      tags: ['дело', 'контроль'],
      assigneeUserId: 8,
      assigneeName: 'Исполнитель шаблона',
      reminders: ['before1d', 'atDue'],
      channels: ['inApp', 'telegram'],
      participants: [
        { userId: 9, role: 'approver', displayName: 'Согласующий шаблона' },
        { userId: 10, role: 'observer', displayName: 'Наблюдатель шаблона' },
      ],
    });

    expect(payload.clientId).toBe(12);
    expect(payload.clientCaseId).toBe(15);
    expect(payload.objectName).toBe('ЗЕМЦОВ - банкротство');
    expect(payload.title).toBe('Шаблонная задача по делу');
    expect(payload.description).toContain('Описание из шаблона.');
    expect(payload.description).toContain('Создано из карточки дела');
    expect(payload.priority).toBe('high');
    expect(payload.plannedDurationMinutes).toBe(120);
    expect(payload.assigneeUserId).toBe(8);
    expect(payload.tags).toEqual(['дело', 'контроль']);
    expect(payload.reminders).toEqual(['atDue', 'before1d']);
    expect(payload.channels).toEqual(['inApp', 'telegram']);
    expect(payload.participants).toEqual([
      expect.objectContaining({ userId: 9, role: 'approver', displayName: 'Согласующий шаблона', approvalStatus: 'pending' }),
      expect.objectContaining({ userId: 10, role: 'observer', displayName: 'Наблюдатель шаблона', approvalStatus: 'pending' }),
    ]);
  });

  it('keeps pretrial stage templates separate from court stages', () => {
    const templates = getCaseStageTaskTemplates(createCase({
      caseType: 'Внесудебное урегулирование',
      stage: 'Внесудебное производство',
    }));

    expect(templates.map((item) => item.key)).toEqual(['pretrial-position']);
    expect(templates[0].title).toBe('Проверить досудебную позицию и доказательства');
  });

  it('falls back to general case task templates when the free-text stage is not recognized', () => {
    const templates = getCaseStageTaskTemplates(createCase({ stage: 'Индивидуальный этап заказчика', caseType: '' }));

    expect(templates.map((item) => item.key)).toEqual(['current-stage-plan', 'documents-check']);
  });
});
