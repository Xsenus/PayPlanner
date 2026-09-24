import { describe, expect, it } from 'vitest';

import type { ClientCaseCourtEvent, ClientCaseCourtInstance, ClientCaseProceduralDeadline } from '../types';
import { buildCourtCaseProcessSummary, buildCourtInstanceStage } from './courtCaseSummary';

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

describe('buildCourtInstanceStage', () => {
  it('keeps a scheduled decision separate from an issued one', () => {
    const stage = buildCourtInstanceStage([
      courtEvent({
        eventType: 'decision',
        title: 'Оглашение решения',
        movementStatus: 'scheduled',
        resultSummary: 'Ожидается решение',
      }),
    ], 'first');

    expect(stage.label).toBe('Событие назначено');
    expect(stage.lastConfirmedResult).toBeNull();
  });

  it('shows the last confirmed result without treating later scheduled hearing as completed', () => {
    const stage = buildCourtInstanceStage([
      courtEvent({
        eventType: 'decision',
        resultSummary: 'Иск удовлетворён',
      }),
      courtEvent({
        id: 2,
        eventType: 'hearing',
        scheduledAt: '2026-10-14T09:50:00Z',
        movementStatus: 'scheduled',
        resultSummary: 'Предполагаемый итог',
      }),
    ], 'first');

    expect(stage.label).toBe('Заседание назначено');
    expect(stage.lastConfirmedResult).toBe('Иск удовлетворён');
  });

  it('separates the stage and result of each instance', () => {
    const events = [
      courtEvent({ eventType: 'decision', resultSummary: 'Иск удовлетворён' }),
      courtEvent({
        id: 2,
        eventType: 'hearing',
        courtInstance: 'appeal',
        hearingOutcome: 'considered',
        hearingOutcomeNote: 'Рассмотрение окончено',
        movementStatus: 'held',
      }),
    ];

    expect(buildCourtInstanceStage(events, 'first').label).toBe('Решение зафиксировано');
    expect(buildCourtInstanceStage(events, 'appeal').label).toBe('Рассмотрение завершено');
    expect(buildCourtInstanceStage(events, 'appeal').lastConfirmedResult).toBe('Рассмотрение окончено');
    expect(buildCourtInstanceStage(events, 'cassation').label).toBe('Движение не загружено');
  });

  it('ignores cancelled and archived movements', () => {
    expect(buildCourtInstanceStage([
      courtEvent({ movementStatus: 'cancelled', eventType: 'decision' }),
      courtEvent({ id: 2, movementStatus: 'archived', eventType: 'decision' }),
    ], 'first').label).toBe('Движение не загружено');
  });
});

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
    expect(summary.decision.basis).toBe('актуальное движение дела');
    expect(summary.appeal.label).toBe('Обжалование не обнаружено');
  });

  it('does not call a scheduled announcement an issued decision', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        eventType: 'decision',
        title: 'Ожидаемое решение суда',
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
      })],
      [],
    );

    expect(summary.decision.label).toBe('Решение не обнаружено');
    expect(summary.appeal.label).toBe('Обжалование пока не определяется');
  });

  it('shows a completed appeal decision even without a populated instance card', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-07-01T00:00:00Z' },
      [courtEvent({
        eventType: 'decision',
        title: 'Апелляционное определение',
        courtInstance: 'appeal',
        courtInstanceLabel: 'Апелляция',
        movementStatus: 'completed',
        scheduledAt: '2026-09-14T10:00:00Z',
        resultSummary: 'Решение оставлено без изменения',
      })],
      [],
    );

    expect(summary.decision.label).toBe('Решение вынесено');
    expect(summary.decision.date).toBe('2026-09-14T10:00:00Z');
    expect(summary.appeal.label).toBe('Решение апелляции зафиксировано');
    expect(summary.appeal.detail).toBe('Решение оставлено без изменения');
    expect(summary.appeal.basis).toBe('решение апелляционной инстанции');
  });

  it('does not confuse a scheduled appeal decision with an issued one', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-07-01T00:00:00Z' },
      [courtEvent({
        eventType: 'decision',
        title: 'Ожидаемое апелляционное определение',
        courtInstance: 'appeal',
        movementStatus: 'scheduled',
        scheduledAt: '2026-10-14T10:00:00Z',
      })],
      [],
    );

    expect(summary.appeal.label).toBe('Обжалование зафиксировано');
    expect(summary.appeal.label).not.toBe('Решение апелляции зафиксировано');
  });

  it('does not call an appeal completed when a later hearing is scheduled', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-07-01T00:00:00Z' },
      [
        courtEvent({
          eventType: 'decision',
          title: 'Апелляционное определение',
          courtInstance: 'appeal',
          scheduledAt: '2026-09-14T10:00:00Z',
        }),
        courtEvent({
          id: 2,
          eventType: 'hearing',
          title: 'Новое заседание апелляции',
          courtInstance: 'appeal',
          movementStatus: 'scheduled',
          scheduledAt: '2026-10-14T10:00:00Z',
        }),
      ],
      [],
    );

    expect(summary.appeal.label).toBe('Обжалование зафиксировано');
  });

  it('distinguishes a considered appeal hearing from a published decision', () => {
    const completed = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        eventType: 'hearing',
        courtInstance: 'appeal',
        movementStatus: 'held',
        hearingOutcome: 'considered',
        hearingOutcomeNote: 'Рассмотрение завершено',
      })],
      [],
    );
    const continued = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [
        courtEvent({
          eventType: 'hearing',
          courtInstance: 'appeal',
          movementStatus: 'held',
          hearingOutcome: 'considered',
        }),
        courtEvent({
          id: 2,
          eventType: 'hearing',
          courtInstance: 'appeal',
          movementStatus: 'scheduled',
          scheduledAt: '2026-10-14T10:00:00Z',
        }),
      ],
      [],
    );

    expect(completed.appeal.label).toBe('Рассмотрение апелляции завершено');
    expect(completed.appeal.detail).toBe('Рассмотрение завершено');
    expect(continued.appeal.label).toBe('Обжалование зафиксировано');
  });

  it('recognizes an appeal from a linked later instance', () => {
    const instance = {
      id: 1,
      clientCaseId: 10,
      instance: 'appeal',
      instanceLabel: 'Апелляция',
      courtCaseNumber: '33-1000/2026',
      createdAt: '2026-09-16T00:00:00Z',
      complaints: [],
    } as ClientCaseCourtInstance;

    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-08-21T00:00:00Z' },
      [],
      [instance],
    );

    expect(summary.appeal.label).toBe('Обжалование зафиксировано');
    expect(summary.appeal.detail).toContain('Апелляция');
    expect(summary.appeal.basis).toBe('связанная судебная инстанция');
  });

  it('does not treat a shared judicial UID as a populated appeal instance', () => {
    const instance = {
      id: 1,
      clientCaseId: 10,
      instance: 'appeal',
      instanceLabel: 'Апелляция',
      judicialUid: '63RS0018-01-2026-001242-18',
      createdAt: '2026-09-16T00:00:00Z',
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
    expect(summary.appeal.allDay).toBe(true);
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

  it('recognizes an issued appeal decision in the result of a completed hearing about a complaint', () => {
    const event = courtEvent({
      eventType: 'hearing',
      courtInstance: 'appeal',
      title: 'Рассмотрение апелляционной жалобы',
      movementStatus: 'completed',
      scheduledAt: '2026-09-14T10:00:00Z',
      resultSummary: 'Вынесено решение по делу: решение оставлено без изменения',
    });
    const summary = buildCourtCaseProcessSummary({ courtDecisionAt: null }, [event], []);
    const stage = buildCourtInstanceStage([event], 'appeal');

    expect(summary.decision.label).toBe('Решение вынесено');
    expect(summary.appeal.label).toBe('Решение апелляции зафиксировано');
    expect(stage.label).toBe('Решение зафиксировано');
    expect(stage.lastConfirmedResult).toContain('Вынесено решение по делу');
  });

  it('does not treat a completed complaint filing as an issued decision', () => {
    const event = courtEvent({
      eventType: 'other',
      courtInstance: 'appeal',
      title: 'Апелляционная жалоба на решение суда',
      movementStatus: 'completed',
      resultSummary: 'Жалоба направлена в суд',
    });
    const summary = buildCourtCaseProcessSummary({ courtDecisionAt: null }, [event], []);

    expect(summary.decision.label).toBe('Решение не обнаружено');
    expect(summary.appeal.label).toBe('Обжалование зафиксировано');
  });

  it('does not invent a decision when movement has not been loaded', () => {
    const summary = buildCourtCaseProcessSummary({ courtDecisionAt: null }, [], []);

    expect(summary.decision.label).toBe('Нет данных о решении');
    expect(summary.appeal.label).toBe('Обжалование пока не определяется');
    expect(summary.nextAction.label).toBe('Следующее действие не определено');
    expect(summary.risk.label).toBe('Риск не определён');
    expect(summary.risk.basis).toBe('данных недостаточно');
    expect(summary.sourceCheckedAt).toBeNull();
  });

  it('uses the latest active source check and avoids a green guarantee for unverified card data', () => {
    const verified = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({ sourceCheckedAt: '2026-09-15T08:00:00Z' })],
      [],
      [proceduralDeadline({ sourceCheckedAt: '2026-09-16T10:30:00Z' })],
      { now: new Date('2026-09-16T09:00:00Z') },
    );
    const unverified = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({ sourceType: 'manual', sourceCheckedAt: null })],
      [],
      [],
      { now: new Date('2026-09-16T09:00:00Z') },
    );

    expect(verified.sourceCheckedAt).toBe('2026-09-16T10:30:00Z');
    expect(verified.risk.label).toBe('Критичных рисков не обнаружено');
    expect(verified.risk.tone).toBe('emerald');
    expect(unverified.sourceCheckedAt).toBeNull();
    expect(unverified.risk.label).toBe('Срочных рисков не обнаружено');
    expect(unverified.risk.tone).toBe('blue');
  });

  it('uses a successful source sync even when it created no events or deadlines', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({ sourceType: 'manual', sourceCheckedAt: null })],
      [],
      [],
      {
        now: new Date('2026-09-16T09:00:00Z'),
        successfulSourceChecks: [null, 'invalid-date', '2026-09-16T08:30:00Z'],
      },
    );

    expect(summary.sourceCheckedAt).toBe('2026-09-16T08:30:00Z');
    expect(summary.risk.label).toBe('Критичных рисков не обнаружено');
    expect(summary.risk.tone).toBe('emerald');
  });

  it('does not use archived movements as evidence for a current decision or appeal', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [
        courtEvent({ id: 1, title: 'Передача материалов судье', movementStatus: 'completed' }),
        courtEvent({ id: 2, eventType: 'decision', title: 'Решение суда', movementStatus: 'archived' }),
        courtEvent({ id: 3, title: 'Поступила апелляционная жалоба', movementStatus: 'ARCHIVED' }),
      ],
      [],
    );

    expect(summary.decision.label).toBe('Решение не обнаружено');
    expect(summary.decision.basis).toBe('актуальное движение дела');
    expect(summary.appeal.label).toBe('Обжалование пока не определяется');
  });

  it('explains when a decision is based only on the date stored in the case card', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: '2026-08-21T00:00:00Z' },
      [],
      [],
    );

    expect(summary.decision.label).toBe('Решение вынесено');
    expect(summary.decision.detail).toBe('Дата решения указана в карточке дела.');
    expect(summary.decision.basis).toBe('дата в карточке дела');
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
      { now: new Date('2026-09-16T09:00:00Z') },
    );

    expect(summary.nextAction.label).toBe('Предварительное судебное заседание');
    expect(summary.nextAction.detail).toContain('Новокуйбышевский городской суд');
    expect(summary.nextAction.dateLabel).toBe('Дата');
    expect(summary.nextAction.allDay).toBe(false);
    expect(summary.risk.label).toBe('Скоро судебное событие');
    expect(summary.risk.allDay).toBe(false);
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
      { now: new Date('2026-09-16T09:00:00Z') },
    );

    expect(summary.nextAction.label).toBe('Подать ходатайство');
    expect(summary.nextAction.dateLabel).toBe('Срок до');
    expect(summary.nextAction.allDay).toBe(true);
    expect(summary.nextAction.tone).toBe('amber');
    expect(summary.risk.label).toBe('Срок требует внимания');
    expect(summary.risk.allDay).toBe(true);
  });

  it('orders all-day records by the end of the user day instead of midnight', () => {
    const timedEventFirst = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        title: 'Заседание утром',
        scheduledAt: '2026-09-20T05:00:00Z',
        allDay: false,
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
      })],
      [],
      [proceduralDeadline({
        deadlineType: 'motion',
        deadlineTypeLabel: 'Ходатайство',
        title: 'Подать ходатайство до конца дня',
        dueAt: '2026-09-20T00:00:00Z',
        allDay: true,
      })],
      {
        now: new Date('2026-09-19T00:00:00Z'),
        timeZoneId: 'Europe/Samara',
      },
    );

    const timedDeadlineFirst = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        title: 'Событие на весь день',
        scheduledAt: '2026-09-20T00:00:00Z',
        allDay: true,
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
      })],
      [],
      [proceduralDeadline({
        deadlineType: 'motion',
        deadlineTypeLabel: 'Ходатайство',
        title: 'Подать документ к 10:00',
        dueAt: '2026-09-20T06:00:00Z',
        allDay: false,
      })],
      {
        now: new Date('2026-09-19T00:00:00Z'),
        timeZoneId: 'Europe/Samara',
      },
    );

    expect(timedEventFirst.nextAction.label).toBe('Заседание утром');
    expect(timedEventFirst.nextAction.date).toBe('2026-09-20T05:00:00Z');
    expect(timedEventFirst.nextAction.allDay).toBe(false);
    expect(timedDeadlineFirst.nextAction.label).toBe('Подать документ к 10:00');
    expect(timedDeadlineFirst.nextAction.date).toBe('2026-09-20T06:00:00Z');
    expect(timedDeadlineFirst.nextAction.allDay).toBe(false);
  });

  it('keeps an all-day deadline due today visible after midnight', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [],
      [],
      [proceduralDeadline({
        title: 'Подать возражения',
        deadlineType: 'objection',
        deadlineTypeLabel: 'Возражения',
        dueAt: '2026-09-16T00:00:00Z',
        allDay: true,
        isOverdue: false,
        isWarning: true,
      })],
      { now: new Date('2026-09-16T12:00:00Z') },
    );

    expect(summary.nextAction.label).toBe('Подать возражения');
    expect(summary.nextAction.dateLabel).toBe('Срок до');
    expect(summary.nextAction.allDay).toBe(true);
    expect(summary.nextAction.tone).toBe('amber');
    expect(summary.risk.label).toBe('Срок требует внимания');
    expect(summary.risk.allDay).toBe(true);
  });

  it('keeps an all-day court event due today visible after midnight', () => {
    const summary = buildCourtCaseProcessSummary(
      { courtDecisionAt: null },
      [courtEvent({
        title: 'Судебное заседание',
        scheduledAt: '2026-09-16T00:00:00Z',
        allDay: true,
        isOverdue: false,
        movementStatus: 'scheduled',
        movementStatusLabel: 'Назначено',
      })],
      [],
      [],
      { now: new Date('2026-09-16T12:00:00Z') },
    );

    expect(summary.nextAction.label).toBe('Судебное заседание');
    expect(summary.nextAction.dateLabel).toBe('Дата');
    expect(summary.nextAction.allDay).toBe(true);
    expect(summary.nextAction.tone).toBe('amber');
    expect(summary.risk.label).toBe('Скоро судебное событие');
    expect(summary.risk.allDay).toBe(true);
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
      { now: new Date('2026-09-16T09:00:00Z') },
    );

    expect(summary.nextAction.label).toBe('Следующее заседание');
    expect(summary.nextAction.allDay).toBe(false);
    expect(summary.risk.label).toBe('Просрочен процессуальный срок');
    expect(summary.risk.detail).toBe('Подать возражения');
    expect(summary.risk.allDay).toBe(true);
    expect(summary.risk.tone).toBe('rose');
  });
});
