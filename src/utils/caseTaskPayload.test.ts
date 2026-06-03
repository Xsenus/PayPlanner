import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ClientCase } from '../types';
import { buildCaseTaskPayload } from './caseTaskPayload';
import { fromTimeZoneDateTimeInput } from './timeZoneDate';

function createCase(overrides: Partial<ClientCase> = {}): ClientCase {
  return {
    id: 15,
    clientId: 12,
    legalEntityId: null,
    title: 'ЗЕМЦОВ - банкротство',
    priority: 'normal',
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
});
