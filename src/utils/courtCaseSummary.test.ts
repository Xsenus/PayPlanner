import { describe, expect, it } from 'vitest';

import type { ClientCaseCourtEvent, ClientCaseCourtInstance } from '../types';
import { buildCourtCaseProcessSummary } from './courtCaseSummary';

function courtEvent(overrides: Partial<ClientCaseCourtEvent> = {}): ClientCaseCourtEvent {
  return {
    id: 1,
    clientCaseId: 10,
    eventType: 'movement',
    eventTypeLabel: 'Движение дела',
    title: 'Передача материалов судье',
    scheduledAt: '2026-04-14T09:50:00Z',
    allDay: false,
    courtInstance: 'first',
    courtInstanceLabel: 'Первая инстанция',
    movementStatus: 'completed',
    movementStatusLabel: 'Завершено',
    sourceType: 'sudrf',
    createdAt: '2026-09-16T00:00:00Z',
    isOverdue: false,
    ...overrides,
  };
}

describe('buildCourtCaseProcessSummary', () => {
  it('recognizes a final result without mistaking claim acceptance for a decision', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-08-21T00:00:00Z' },
      [
        courtEvent({ title: 'Решение вопроса о принятии иска к рассмотрению', eventType: 'claim' }),
        courtEvent({
          id: 2,
          eventType: 'decision',
          eventTypeLabel: 'Судебное решение',
          title: 'Судебное заседание',
          scheduledAt: '2026-08-21T10:00:00Z',
          resultSummary: 'Иск удовлетворен частично',
        }),
      ],
      [],
    );

    expect(summary.decision.label).toBe('Решение вынесено');
    expect(summary.decision.detail).toBe('Иск удовлетворен частично');
    expect(summary.appeal.label).toBe('Обжалование не обнаружено');
  });

  it('recognizes an appeal from a linked later instance', () => {
    const instance = {
      instance: 'appeal',
      instanceLabel: 'Апелляция',
      courtCaseNumber: '33-1000/2026',
      complaints: [],
    } as ClientCaseCourtInstance;

    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-08-21T00:00:00Z' },
      [],
      [instance],
    );

    expect(summary.appeal.label).toBe('Обжалование зафиксировано');
    expect(summary.appeal.detail).toContain('Апелляция');
  });

  it('does not treat a shared judicial UID as a populated appeal instance', () => {
    const instance = {
      instance: 'appeal',
      instanceLabel: 'Апелляция',
      judicialUid: '63RS0018-01-2026-001242-18',
      complaints: [],
    } as ClientCaseCourtInstance;

    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-08-21T00:00:00Z' },
      [],
      [instance],
    );

    expect(summary.appeal.label).toBe('Обжалование не обнаружено');
  });

  it('recognizes an appeal movement without mistaking its decision reference for a decision', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({ title: 'Направлена апелляционная жалоба на решение суда' })],
      [],
    );

    expect(summary.decision.label).toBe('Решение не обнаружено');
    expect(summary.appeal.label).toBe('Обжалование зафиксировано');
  });

  it('does not invent a decision when movement has not been loaded', () => {
    const summary = buildCourtCaseProcessSummary({ courtDecisionAt: null }, [], []);

    expect(summary.decision.label).toBe('Нет данных о решении');
    expect(summary.appeal.label).toBe('Обжалование пока не определяется');
  });
});
