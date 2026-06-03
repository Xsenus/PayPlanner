import type { ClientCase } from '../types';
import { fromTimeZoneDateTimeInput, toTimeZoneDateKey } from './timeZoneDate';
import type { TaskJournalPriority, TaskJournalUpsertPayload } from './taskJournalStore';

const ALLOWED_TASK_PRIORITIES = new Set<TaskJournalPriority>(['low', 'normal', 'high', 'urgent']);

function truncate(value: string, maxLength: number) {
  return value.length > maxLength ? value.slice(0, maxLength - 1).trimEnd() : value;
}

function resolveTaskPriority(value?: string | null): TaskJournalPriority {
  return ALLOWED_TASK_PRIORITIES.has(value as TaskJournalPriority) ? value as TaskJournalPriority : 'normal';
}

function addDaysToDateKey(value: string, days: number) {
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day + days));
  return [
    date.getUTCFullYear(),
    String(date.getUTCMonth() + 1).padStart(2, '0'),
    String(date.getUTCDate()).padStart(2, '0'),
  ].join('-');
}

function buildDefaultDueAt(timeZoneId?: string | null) {
  const todayKey = toTimeZoneDateKey(new Date(), timeZoneId);
  const tomorrowKey = todayKey ? addDaysToDateKey(todayKey, 1) : addDaysToDateKey(new Date().toISOString().slice(0, 10), 1);
  return fromTimeZoneDateTimeInput(`${tomorrowKey}T10:00`, timeZoneId);
}

export function buildCaseTaskPayload(caseItem: ClientCase, timeZoneId?: string | null): TaskJournalUpsertPayload {
  return {
    clientId: caseItem.clientId ?? null,
    legalEntityId: caseItem.legalEntityId ?? null,
    isInternal: false,
    clientCaseId: caseItem.id,
    objectName: caseItem.title,
    title: truncate(`Задача по делу: ${caseItem.title}`, 200),
    description: `Создано из карточки дела «${caseItem.title}».`,
    assigneeUserId: caseItem.responsibleUserId ?? null,
    assigneeName: '',
    dueAt: buildDefaultDueAt(timeZoneId),
    approvalDueAt: null,
    plannedDurationMinutes: 60,
    contractorPaymentEnabled: false,
    contractorPaymentAmount: null,
    contractorPaymentDueDate: null,
    contractorPaymentOnResult: false,
    type: 'task',
    priority: resolveTaskPriority(caseItem.priority),
    status: 'new',
    result: '',
    reminders: ['atDue'],
    channels: ['inApp'],
    tags: ['дело'],
    participants: [],
  };
}
