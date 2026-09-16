import { describe, expect, it } from 'vitest';

import type { ClientCaseCourtEvent, ClientCaseCourtInstance, ClientCaseProceduralDeadline } from '../types';
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

function proceduralDeadline(overrides: Partial<ClientCaseProceduralDeadline> = {}): ClientCaseProceduralDeadline {
  return {
    id: 1,
    clientCaseId: 10,
    deadlineType: 'appeal',
    deadlineTypeLabel: 'Апелляция',
    title: 'Срок апелляционного обжалования',
    dueAt: '2026-10-05T00:00:00Z',
    allDay: true,
    status: 'active',
    statusLabel: 'Активен',
    warningDaysBefore: 7,
    sourceType: 'sudrf',
    createdAt: '2026-09-04T00:00:00Z',
    isOverdue: false,
    isWarning: false,
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

  it('shows an active appeal deadline without inventing an appeal', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-09-04T00:00:00Z' },
      [],
      [],
      [{
        id: 1,
        clientCaseId: 10,
        deadlineType: 'appeal',
        deadlineTypeLabel: 'Апелляция',
        title: 'Срок апелляционного обжалования',
        dueAt: '2026-10-05T00:00:00Z',
        allDay: true,
        status: 'active',
        statusLabel: 'Активен',
        warningDaysBefore: 7,
        sourceType: 'sudrf',
        createdAt: '2026-09-04T00:00:00Z',
        isOverdue: false,
        isWarning: false,
      }],
    );

    expect(summary.appeal.label).toBe('Обжалование не обнаружено');
    expect(summary.appeal.detail).toContain('Срок обжалования ещё контролируется');
    expect(summary.appeal.date).toBe('2026-10-05T00:00:00Z');
    expect(summary.appeal.dateLabel).toBe('Срок до');
    expect(summary.appeal.tone).toBe('amber');
  });

  it('warns when the appeal deadline is overdue and no appeal was found', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-09-04T00:00:00Z' },
      [],
      [],
      [{
        id: 1,
        clientCaseId: 10,
        deadlineType: 'appeal',
        deadlineTypeLabel: 'Апелляция',
        title: 'Срок апелляционного обжалования',
        dueAt: '2026-10-05T00:00:00Z',
        allDay: true,
        status: 'active',
        statusLabel: 'Просрочен',
        warningDaysBefore: 7,
        sourceType: 'sudrf',
        createdAt: '2026-09-04T00:00:00Z',
        isOverdue: true,
        isWarning: true,
      }],
    );

    expect(summary.appeal.detail).toContain('срок обжалования уже истёк');
    expect(summary.appeal.tone).toBe('rose');
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
    expect(summary.nextAction.label).toBe('Следующее действие не определено');
    expect(summary.risk.label).toBe('Риск не определён');
  });

  it('selects the nearest upcoming court event and warns when it is imminent', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        title: 'Предварительное судебное заседание',
        scheduledAt: '2026-09-18T09:00:00Z',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
        courtName: 'Новокуйбышевский городской суд',
      })],
      [],
      [],
      new Date('2026-09-16T09:00:00Z'),
    );

    expect(summary.nextAction.label).toBe('Предварительное судебное заседание');
    expect(summary.nextAction.detail).toContain('Новокуйбышевский городской суд');
    expect(summary.nextAction.dateLabel).toBe('Дата');
    expect(summary.risk.label).toBe('Скоро судебное событие');
    expect(summary.risk.tone).toBe('amber');
  });

  it('prioritizes an earlier warning deadline over a later hearing', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        title: 'Судебное заседание',
        scheduledAt: '2026-09-25T09:00:00Z',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
      })],
      [],
      [proceduralDeadline({
        deadlineType: 'motion',
        deadlineTypeLabel: 'Ходатайство',
        title: 'Подать ходатайство',
        dueAt: '2026-09-20T00:00:00Z',
        isWarning: true,
      })],
      new Date('2026-09-16T09:00:00Z'),
    );

    expect(summary.nextAction.label).toBe('Подать ходатайство');
    expect(summary.nextAction.dateLabel).toBe('Срок до');
    expect(summary.nextAction.tone).toBe('amber');
    expect(summary.risk.label).toBe('Срок требует внимания');
  });

  it('surfaces an overdue deadline as the highest risk while keeping a planned next event', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        title: 'Следующее заседание',
        scheduledAt: '2026-09-30T09:00:00Z',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
      })],
      [],
      [proceduralDeadline({
        title: 'Подать возражения',
        deadlineType: 'objection',
        deadlineTypeLabel: 'Возражения',
        dueAt: '2026-09-15T00:00:00Z',
        isOverdue: true,
        isWarning: true,
        statusLabel: 'Просрочен',
      })],
      new Date('2026-09-16T09:00:00Z'),
    );

    expect(summary.nextAction.label).toBe('Следующее заседание');
    expect(summary.risk.label).toBe('Просрочен процессуальный срок');
    expect(summary.risk.detail).toBe('Подать возражения');
    expect(summary.risk.tone).toBe('rose');
  });
});
