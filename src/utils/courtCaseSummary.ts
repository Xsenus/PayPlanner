import type { ClientCase, ClientCaseCourtEvent, ClientCaseCourtInstance } from '../types';

export type CourtProcessSummaryTone = 'emerald' | 'amber' | 'slate' | 'blue';

export interface CourtProcessSummaryItem {
  status: 'confirmed' | 'not-found' | 'pending' | 'unknown';
  label: string;
  detail: string;
  date?: string | null;
  tone: CourtProcessSummaryTone;
}

export interface CourtCaseProcessSummary {
  decision: CourtProcessSummaryItem;
  appeal: CourtProcessSummaryItem;
}

function eventText(event: ClientCaseCourtEvent) {
  return `${event.title || ''} ${event.resultSummary || ''} ${event.description || ''}`.toLocaleLowerCase('ru');
}

function containsAny(value: string, fragments: string[]) {
  return fragments.some((fragment) => value.includes(fragment));
}

function isDecisionEvent(event: ClientCaseCourtEvent) {
  if (event.eventType === 'decision') return true;
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

export function buildCourtCaseProcessSummary(
  caseItem: Pick<ClientCase, 'courtDecisionAt'>,
  events: ClientCaseCourtEvent[],
  instances: ClientCaseCourtInstance[],
): CourtCaseProcessSummary {
  const decisionEvent = events
    .filter(isDecisionEvent)
    .sort((left, right) => new Date(right.scheduledAt).getTime() - new Date(left.scheduledAt).getTime())[0];
  const decisionDate = caseItem.courtDecisionAt || decisionEvent?.scheduledAt || null;
  const hasDecision = Boolean(decisionDate || decisionEvent);
  const hasCourtMovement = events.some((event) => event.movementStatus !== 'archived');

  const decision: CourtProcessSummaryItem = hasDecision
    ? {
        status: 'confirmed',
        label: 'Решение вынесено',
        detail: decisionEvent?.resultSummary || 'В движении дела зафиксирован итог рассмотрения.',
        date: decisionDate,
        tone: 'emerald',
      }
    : hasCourtMovement
      ? {
          status: 'not-found',
          label: 'Решение не обнаружено',
          detail: 'В загруженном движении дела итоговое решение пока не найдено.',
          tone: 'amber',
        }
      : {
          status: 'unknown',
          label: 'Нет данных о решении',
          detail: 'Сначала проверьте официальный источник и загрузите движение дела.',
          tone: 'slate',
        };

  const appealEvent = events
    .filter(isAppealMovement)
    .sort((left, right) => new Date(right.scheduledAt).getTime() - new Date(left.scheduledAt).getTime())[0];
  const laterInstance = instances.find(hasPopulatedLaterInstance);
  const activeComplaint = instances
    .flatMap((instance) => instance.complaints ?? [])
    .find((complaint) => !complaint.isArchived);
  const hasAppeal = Boolean(appealEvent || laterInstance || activeComplaint);

  const appeal: CourtProcessSummaryItem = hasAppeal
    ? {
        status: 'confirmed',
        label: 'Обжалование зафиксировано',
        detail: appealEvent?.resultSummary
          || activeComplaint?.title
          || (laterInstance ? `${laterInstance.instanceLabel}: ${laterInstance.courtCaseNumber || 'карточка связана'}` : 'В деле есть сведения об обжаловании.'),
        date: appealEvent?.scheduledAt || activeComplaint?.filedAt || null,
        tone: 'blue',
      }
    : hasDecision
      ? {
          status: 'not-found',
          label: 'Обжалование не обнаружено',
          detail: 'В загруженных движениях, жалобах и связанных инстанциях сведений об обжаловании нет.',
          tone: 'slate',
        }
      : {
          status: 'pending',
          label: 'Обжалование пока не определяется',
          detail: 'Статус обжалования станет понятен после появления решения или жалобы.',
          tone: 'amber',
        };

  return { decision, appeal };
}
