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
