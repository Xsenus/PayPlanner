import type { ClientCase } from '../types';
import type { TaskJournalPriority, TaskJournalUpsertPayload } from './taskJournalStore';

const ALLOWED_TASK_PRIORITIES = new Set<TaskJournalPriority>(['low', 'normal', 'high', 'urgent']);

function truncate(value: string, maxLength: number) {
  return value.length > maxLength ? value.slice(0, maxLength - 1).trimEnd() : value;
}

function resolveTaskPriority(value?: string | null): TaskJournalPriority {
  return ALLOWED_TASK_PRIORITIES.has(value as TaskJournalPriority) ? value as TaskJournalPriority : 'normal';
}

function buildDefaultDueAt() {
  const dueAt = new Date();
  dueAt.setDate(dueAt.getDate() + 1);
  dueAt.setHours(10, 0, 0, 0);
  return dueAt.toISOString();
}

export function buildCaseTaskPayload(caseItem: ClientCase): TaskJournalUpsertPayload {
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
    dueAt: buildDefaultDueAt(),
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
