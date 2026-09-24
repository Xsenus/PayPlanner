import type {
  ClientCase,
  ClientCaseCourtEvent,
  ClientCaseCourtInstance,
  ClientCaseProceduralDeadline,
} from '../types';
import { getEffectiveScheduleTime } from './timeZoneDate';

export type CourtProcessSummaryTone = 'emerald' | 'amber' | 'rose' | 'slate' | 'blue';

export interface CourtProcessSummaryItem {
  status: 'confirmed' | 'not-found' | 'pending' | 'unknown';
  label: string;
  detail: string;
  basis: string;
  date?: string | null;
  dateLabel?: string;
  allDay?: boolean;
  tone: CourtProcessSummaryTone;
}

export interface CourtCaseProcessSummary {
  decision: CourtProcessSummaryItem;
  appeal: CourtProcessSummaryItem;
  nextAction: CourtProcessSummaryItem;
  risk: CourtProcessSummaryItem;
  sourceCheckedAt: string | null;
}

export interface CourtCaseProcessSummaryOptions {
  now?: Date;
  successfulSourceChecks?: Array<string | null | undefined>;
  timeZoneId?: string | null;
}

export interface CourtInstanceStage {
  label: string;
  detail: string;
  tone: CourtProcessSummaryTone;
  lastConfirmedResult: string | null;
}

const TERMINAL_EVENT_STATUSES = new Set(['archived', 'cancelled', 'completed', 'held']);
const INACTIVE_DEADLINE_STATUSES = new Set(['closed', 'cancelled', 'archived']);
const THREE_DAYS_MS = 3 * 24 * 60 * 60 * 1000;

function eventText(event: ClientCaseCourtEvent) {
  return `${event.title || ''} ${event.resultSummary || ''} ${event.description || ''}`.toLocaleLowerCase('ru');
}

function containsAny(value: string, fragments: string[]) {
  return fragments.some((fragment) => value.includes(fragment));
}

function isDecisionEvent(event: ClientCaseCourtEvent) {
  const status = (event.movementStatus || '').trim().toLowerCase();
  if (!['completed', 'held'].includes(status)) return false;
  if (event.eventType === 'decision') return true;
  const result = (event.resultSummary || '').toLocaleLowerCase('ru');
  if (containsAny(result, [
    'вынесено решение',
    'принято решение',
    'решение оставлено без изменения',
    'решение отменено',
    'решение изменено',
    'апелляционное определение',
  ])) return true;
  if (isAppealMovement(event)) return false;

  return containsAny(eventText(event), [
    'решение суда',
    'решение по делу',
    'вынесено решение',
    'мотивированное решение',
    'иск удовлетвор',
    'заявление удовлетвор',
    'жалоба удовлетвор',
    'в удовлетворении отказ',
    'производство прекращено',
    'оставлено без рассмотрения',
  ]);
}

function isAppealMovement(event: ClientCaseCourtEvent) {
  const text = eventText(event);
  return containsAny(text, [
    'апелляционная жалоба',
    'апелляционное представление',
    'кассационная жалоба',
    'обжалование',
    'обжалован',
    'обжалуется',
    'жалоба на решение',
  ]);
}

function hasPopulatedLaterInstance(instance: ClientCaseCourtInstance) {
  return instance.instance !== 'first' && Boolean(
    instance.courtName
    || instance.courtCaseNumber
    || instance.courtUrl
    || instance.complaints?.some((complaint) => !complaint.isArchived),
  );
}

function getTime(value?: string | null) {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isFinite(time) ? time : null;
}

function isActiveDeadline(deadline: ClientCaseProceduralDeadline) {
  return !INACTIVE_DEADLINE_STATUSES.has((deadline.status || '').trim().toLowerCase());
}

function isActiveCourtEvent(event: ClientCaseCourtEvent) {
  return (event.movementStatus || '').trim().toLowerCase() !== 'archived';
}

export function buildCourtInstanceStage(
  events: ClientCaseCourtEvent[],
  courtInstance: string,
): CourtInstanceStage {
  const relevant = events
    .filter((event) => event.courtInstance === courtInstance)
    .filter((event) => !['archived', 'cancelled'].includes((event.movementStatus || '').trim().toLowerCase()))
    .sort((left, right) => (getTime(right.scheduledAt) ?? 0) - (getTime(left.scheduledAt) ?? 0));
  const confirmedResultEvent = relevant.find((event) => (
    ['completed', 'held'].includes((event.movementStatus || '').trim().toLowerCase())
    && (event.resultSummary?.trim() || event.hearingOutcomeNote?.trim())
  ));
  const lastConfirmedResult = confirmedResultEvent?.resultSummary?.trim()
    || confirmedResultEvent?.hearingOutcomeNote?.trim()
    || null;
  const latest = relevant[0];

  if (!latest) return {
    label: 'Движение не загружено',
    detail: 'Проверьте карточку суда или добавьте подтверждённое событие.',
    tone: 'slate',
    lastConfirmedResult: null,
  };

  if ((latest.movementStatus || '').trim().toLowerCase() === 'scheduled') return {
    label: latest.eventType === 'hearing' ? 'Заседание назначено' : 'Событие назначено',
    detail: 'Итог ещё не подтверждён. Проверьте событие после его даты.',
    tone: 'amber',
    lastConfirmedResult,
  };

  if (isDecisionEvent(latest)) return {
    label: 'Решение зафиксировано',
    detail: 'Публикация текста и вступление в силу требуют отдельного подтверждения.',
    tone: 'emerald',
    lastConfirmedResult,
  };

  if (['completed', 'held'].includes((latest.movementStatus || '').trim().toLowerCase()) && latest.hearingOutcome === 'considered') return {
    label: 'Рассмотрение завершено',
    detail: 'Решение в движении этой инстанции пока не зафиксировано.',
    tone: 'blue',
    lastConfirmedResult,
  };

  return {
    label: 'Движение обновлено',
    detail: 'По последнему событию нельзя подтвердить итог рассмотрения.',
    tone: 'slate',
    lastConfirmedResult,
  };
}

function isUpcomingCourtEvent(event: ClientCaseCourtEvent, nowTime: number) {
  const scheduledAt = getTime(event.scheduledAt);
  const isTerminal = TERMINAL_EVENT_STATUSES.has((event.movementStatus || '').trim().toLowerCase());
  if (scheduledAt === null || isTerminal) return false;

  // Для события «на весь день» scheduledAt хранится как начало суток. После
  // полуночи простое сравнение дат ошибочно скрывает событие из сводки, хотя
  // backend ещё не считает его просроченным с учётом часового пояса пользователя.
  return event.allDay ? !event.isOverdue : scheduledAt >= nowTime;
}

function getLatestSourceCheckedAt(
  events: ClientCaseCourtEvent[],
  deadlines: ClientCaseProceduralDeadline[],
  successfulSourceChecks: Array<string | null | undefined>,
) {
  let latestValue: string | null = null;
  let latestTime = Number.NEGATIVE_INFINITY;

  for (const items of [events, deadlines]) {
    for (const item of items) {
      const time = getTime(item.sourceCheckedAt);
      if (time !== null && time > latestTime) {
        latestValue = item.sourceCheckedAt || null;
        latestTime = time;
      }
    }
  }

  for (const value of successfulSourceChecks) {
    const time = getTime(value);
    if (time !== null && time > latestTime) {
      latestValue = value || null;
      latestTime = time;
    }
  }

  return latestValue;
}

export function buildCourtCaseProcessSummary(
  caseItem: Pick<ClientCase, 'courtDecisionAt'>,
  events: ClientCaseCourtEvent[],
  instances: ClientCaseCourtInstance[],
  proceduralDeadlines: ClientCaseProceduralDeadline[] = [],
  options: CourtCaseProcessSummaryOptions = {},
): CourtCaseProcessSummary {
  const now = options.now ?? new Date();
  const nowTime = now.getTime();
  const timeZoneId = options.timeZoneId;
  const scheduleTimeCache = new Map<string, number | null>();
  const getScheduleTime = (value?: string | null, allDay?: boolean) => {
    const cacheKey = `${allDay === true ? 'all-day' : 'timed'}:${value || ''}`;
    if (scheduleTimeCache.has(cacheKey)) return scheduleTimeCache.get(cacheKey) ?? null;

    const time = getEffectiveScheduleTime(value, allDay, timeZoneId);
    scheduleTimeCache.set(cacheKey, time);
    return time;
  };
  const activeEvents = events.filter(isActiveCourtEvent);
  const decisionEvent = activeEvents
    .filter(isDecisionEvent)
    .sort((left, right) => (
      getScheduleTime(right.scheduledAt, right.allDay) ?? Number.NEGATIVE_INFINITY
    ) - (
      getScheduleTime(left.scheduledAt, left.allDay) ?? Number.NEGATIVE_INFINITY
    ))[0];
  const decisionDate = decisionEvent && ['appeal', 'cassation', 'supervision', 'supreme'].includes(decisionEvent.courtInstance)
    ? decisionEvent.scheduledAt
    : caseItem.courtDecisionAt || decisionEvent?.scheduledAt || null;
  const hasDecision = Boolean(decisionDate || decisionEvent);
  const hasCourtMovement = activeEvents.length > 0;

  const decision: CourtProcessSummaryItem = hasDecision
    ? {
        status: 'confirmed',
        label: 'Решение вынесено',
        detail: decisionEvent?.resultSummary
          || (decisionEvent ? 'В движении дела зафиксирован итог рассмотрения.' : 'Дата решения указана в карточке дела.'),
        basis: decisionEvent ? 'актуальное движение дела' : 'дата в карточке дела',
        date: decisionDate,
        allDay: caseItem.courtDecisionAt ? undefined : decisionEvent?.allDay,
        tone: 'emerald',
      }
    : hasCourtMovement
      ? {
          status: 'not-found',
          label: 'Решение не обнаружено',
          detail: 'В загруженном движении дела итоговое решение пока не найдено.',
          basis: 'актуальное движение дела',
          tone: 'amber',
        }
      : {
          status: 'unknown',
          label: 'Нет данных о решении',
          detail: 'Сначала проверьте официальный источник и загрузите движение дела.',
          basis: 'данных недостаточно',
          tone: 'slate',
        };

  const appealEvent = activeEvents
    .filter(isAppealMovement)
    .sort((left, right) => (
      getScheduleTime(right.scheduledAt, right.allDay) ?? Number.NEGATIVE_INFINITY
    ) - (
      getScheduleTime(left.scheduledAt, left.allDay) ?? Number.NEGATIVE_INFINITY
    ))[0];
  const appealProceedingEvents = activeEvents
    .filter((event) => event.courtInstance === 'appeal')
    .sort((left, right) => (
      getScheduleTime(right.scheduledAt, right.allDay) ?? Number.NEGATIVE_INFINITY
    ) - (
      getScheduleTime(left.scheduledAt, left.allDay) ?? Number.NEGATIVE_INFINITY
    ));
  const appealDecisionCandidate = appealProceedingEvents.find(isDecisionEvent);
  const appealDecisionTime = getScheduleTime(appealDecisionCandidate?.scheduledAt, appealDecisionCandidate?.allDay);
  const hasLaterScheduledAppealEvent = appealDecisionTime !== null && appealProceedingEvents.some((event) => (
    (event.movementStatus || '').trim().toLowerCase() === 'scheduled'
    && (getScheduleTime(event.scheduledAt, event.allDay) ?? Number.NEGATIVE_INFINITY) > appealDecisionTime
  ));
  const appealDecisionEvent = hasLaterScheduledAppealEvent ? null : appealDecisionCandidate;
  const appealProceedingEvent = appealProceedingEvents[0];
  const completedAppealHearing = appealProceedingEvent?.hearingOutcome === 'considered'
    ? appealProceedingEvent
    : null;
  const laterInstance = instances.find(hasPopulatedLaterInstance);
  const activeComplaint = instances
    .flatMap((instance) => instance.complaints ?? [])
    .find((complaint) => !complaint.isArchived);
  const hasAppeal = Boolean(appealEvent || appealProceedingEvent || laterInstance || activeComplaint);
  const appealDeadline = proceduralDeadlines
    .filter((deadline) => deadline.deadlineType === 'appeal')
    .filter(isActiveDeadline)
    .filter((deadline) => getTime(deadline.dueAt) !== null)
    .sort((left, right) => (
      getScheduleTime(left.dueAt, left.allDay) ?? 0
    ) - (
      getScheduleTime(right.dueAt, right.allDay) ?? 0
    ))[0];

  const appeal: CourtProcessSummaryItem = appealDecisionEvent
    ? {
        status: 'confirmed',
        label: 'Решение апелляции зафиксировано',
        detail: appealDecisionEvent.resultSummary || appealDecisionEvent.title,
        basis: 'решение апелляционной инстанции',
        date: appealDecisionEvent.scheduledAt,
        allDay: appealDecisionEvent.allDay,
        tone: 'emerald',
      }
    : completedAppealHearing
      ? {
          status: 'confirmed',
          label: 'Рассмотрение апелляции завершено',
          detail: completedAppealHearing.hearingOutcomeNote || 'Итог заседания указан, но решение апелляционного суда не зафиксировано.',
          basis: 'отметка по заседанию апелляции',
          date: completedAppealHearing.scheduledAt,
          allDay: completedAppealHearing.allDay,
          tone: 'blue',
        }
    : hasAppeal
      ? {
          status: 'confirmed',
          label: 'Обжалование зафиксировано',
          detail: appealEvent?.resultSummary
            || activeComplaint?.title
            || appealProceedingEvent?.title
            || (laterInstance ? `${laterInstance.instanceLabel}: ${laterInstance.courtCaseNumber || 'карточка связана'}` : 'В деле есть сведения об обжаловании.'),
          basis: appealEvent || appealProceedingEvent ? 'актуальное движение дела' : activeComplaint ? 'активная жалоба' : 'связанная судебная инстанция',
          date: appealEvent?.scheduledAt || appealProceedingEvent?.scheduledAt || activeComplaint?.filedAt || null,
          allDay: appealEvent?.scheduledAt ? appealEvent.allDay : appealProceedingEvent?.allDay,
          tone: 'blue',
        }
    : hasDecision
      ? {
          status: 'not-found',
          label: 'Обжалование не обнаружено',
          detail: appealDeadline
            ? appealDeadline.isOverdue
              ? 'Жалоба не найдена, а контрольный срок обжалования уже истёк. Проверьте официальный источник.'
              : 'Жалоба не найдена. Срок обжалования ещё контролируется CRM.'
            : 'В загруженных движениях, жалобах и связанных инстанциях сведений об обжаловании нет.',
          basis: appealDeadline ? 'судебные данные и процессуальный срок' : 'судебные данные дела',
          date: appealDeadline?.dueAt || null,
          dateLabel: appealDeadline ? 'Срок до' : undefined,
          allDay: appealDeadline?.allDay,
          tone: appealDeadline?.isOverdue ? 'rose' : appealDeadline ? 'amber' : 'slate',
        }
      : {
          status: 'pending',
          label: 'Обжалование пока не определяется',
          detail: 'Статус обжалования станет понятен после появления решения или жалобы.',
          basis: 'актуальные движения и жалобы',
          tone: 'amber',
        };

  const activeDeadlines = proceduralDeadlines
    .filter(isActiveDeadline)
    .filter((deadline) => getTime(deadline.dueAt) !== null);
  const sourceCheckedAt = getLatestSourceCheckedAt(
    activeEvents,
    activeDeadlines,
    options.successfulSourceChecks ?? [],
  );
  const overdueDeadlines = activeDeadlines
    .filter((deadline) => deadline.isOverdue)
    .sort((left, right) => (
      getScheduleTime(left.dueAt, left.allDay) ?? 0
    ) - (
      getScheduleTime(right.dueAt, right.allDay) ?? 0
    ));
  const upcomingDeadlines = activeDeadlines
    // Статус просрочки рассчитывает backend с учётом признака «весь день» и
    // часового пояса пользователя. Сравнение ISO-полуночи с текущим временем
    // ошибочно исключало срок уже утром его последнего допустимого дня.
    .filter((deadline) => !deadline.isOverdue)
    .sort((left, right) => (
      getScheduleTime(left.dueAt, left.allDay) ?? 0
    ) - (
      getScheduleTime(right.dueAt, right.allDay) ?? 0
    ));
  const upcomingEvents = activeEvents
    .filter((event) => isUpcomingCourtEvent(event, nowTime))
    .sort((left, right) => (
      getScheduleTime(left.scheduledAt, left.allDay) ?? 0
    ) - (
      getScheduleTime(right.scheduledAt, right.allDay) ?? 0
    ));
  const nextDeadline = upcomingDeadlines[0];
  const nextEvent = upcomingEvents[0];
  const nextDeadlineTime = getScheduleTime(nextDeadline?.dueAt, nextDeadline?.allDay);
  const nextEventTime = getScheduleTime(nextEvent?.scheduledAt, nextEvent?.allDay);
  const deadlineComesFirst = nextDeadlineTime !== null
    && (nextEventTime === null || nextDeadlineTime <= nextEventTime);

  let nextAction: CourtProcessSummaryItem;
  if (deadlineComesFirst && nextDeadline) {
    nextAction = {
      status: 'pending',
      label: nextDeadline.title,
      detail: nextDeadline.isWarning
        ? 'Процессуальный срок уже требует внимания.'
        : `Ближайший процессуальный срок: ${nextDeadline.deadlineTypeLabel || nextDeadline.deadlineType}.`,
      basis: 'процессуальный срок',
      date: nextDeadline.dueAt,
      dateLabel: 'Срок до',
      allDay: nextDeadline.allDay,
      tone: nextDeadline.isWarning ? 'amber' : 'blue',
    };
  } else if (nextEvent) {
    nextAction = {
      status: 'pending',
      label: nextEvent.title,
      detail: [nextEvent.courtName, nextEvent.movementStatusLabel].filter(Boolean).join(' · ') || 'Ближайшее судебное событие.',
      basis: 'актуальное движение дела',
      date: nextEvent.scheduledAt,
      dateLabel: 'Дата',
      allDay: nextEvent.allDay,
      tone: nextEventTime !== null && nextEventTime - nowTime <= THREE_DAYS_MS ? 'amber' : 'blue',
    };
  } else if (overdueDeadlines[0]) {
    nextAction = {
      status: 'pending',
      label: 'Разобрать просроченный срок',
      detail: overdueDeadlines[0].title,
      basis: 'просроченный процессуальный срок',
      date: overdueDeadlines[0].dueAt,
      dateLabel: 'Срок был',
      allDay: overdueDeadlines[0].allDay,
      tone: 'rose',
    };
  } else if (hasCourtMovement || activeDeadlines.length > 0) {
    nextAction = {
      status: 'pending',
      label: 'Ожидается новое движение',
      detail: 'Будущих заседаний и активных сроков в загруженных данных нет.',
      basis: 'актуальные движения и сроки',
      tone: 'slate',
    };
  } else {
    nextAction = {
      status: 'unknown',
      label: 'Следующее действие не определено',
      detail: 'Проверьте официальный источник или добавьте процессуальный срок.',
      basis: 'данных недостаточно',
      tone: 'slate',
    };
  }

  const warningDeadline = upcomingDeadlines.find((deadline) => deadline.isWarning);
  const imminentEvent = upcomingEvents.find((event) => {
    const scheduledAt = getScheduleTime(event.scheduledAt, event.allDay);
    return scheduledAt !== null && scheduledAt - nowTime <= THREE_DAYS_MS;
  });
  let risk: CourtProcessSummaryItem;
  if (overdueDeadlines.length > 0) {
    risk = {
      status: 'pending',
      label: overdueDeadlines.length === 1 ? 'Просрочен процессуальный срок' : `Просрочено сроков: ${overdueDeadlines.length}`,
      detail: overdueDeadlines[0].title,
      basis: 'активные процессуальные сроки',
      date: overdueDeadlines[0].dueAt,
      dateLabel: 'Срок был',
      allDay: overdueDeadlines[0].allDay,
      tone: 'rose',
    };
  } else if (warningDeadline) {
    risk = {
      status: 'pending',
      label: 'Срок требует внимания',
      detail: warningDeadline.title,
      basis: 'активные процессуальные сроки',
      date: warningDeadline.dueAt,
      dateLabel: 'Срок до',
      allDay: warningDeadline.allDay,
      tone: 'amber',
    };
  } else if (imminentEvent) {
    risk = {
      status: 'pending',
      label: 'Скоро судебное событие',
      detail: imminentEvent.title,
      basis: 'актуальное движение дела',
      date: imminentEvent.scheduledAt,
      dateLabel: 'Дата',
      allDay: imminentEvent.allDay,
      tone: 'amber',
    };
  } else if (hasCourtMovement || activeDeadlines.length > 0) {
    risk = sourceCheckedAt
      ? {
          status: 'confirmed',
          label: 'Критичных рисков не обнаружено',
          detail: 'В проверенных данных нет просроченных сроков и ближайших срочных событий.',
          basis: 'проверенные движения и сроки',
          tone: 'emerald',
        }
      : {
          status: 'pending',
          label: 'Срочных рисков не обнаружено',
          detail: 'Вывод сделан по данным карточки без подтверждённой даты проверки источника.',
          basis: 'данные карточки и сроки',
          tone: 'blue',
        };
  } else {
    risk = {
      status: 'unknown',
      label: 'Риск не определён',
      detail: 'Для оценки нужны движение дела или процессуальные сроки.',
      basis: 'данных недостаточно',
      tone: 'slate',
    };
  }

  return { decision, appeal, nextAction, risk, sourceCheckedAt };
}
