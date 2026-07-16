import type { TaskJournalParticipant, TaskJournalUpsertPayload } from './taskJournalStore';

export type TaskTemplateField = keyof TaskJournalUpsertPayload;

export interface ApplyTaskTemplateOptions {
  protectedFields?: readonly TaskTemplateField[];
  replaceFields?: readonly TaskTemplateField[];
}

const VALUE_FIELDS = [
  'isInternal',
  'clientId',
  'legalEntityId',
  'clientCaseId',
  'clientBillingClassification',
  'billingContractId',
  'billingMode',
  'objectName',
  'title',
  'description',
  'assigneeUserId',
  'assigneeName',
  'dueAt',
  'approvalDueAt',
  'plannedDurationMinutes',
  'contractorPaymentEnabled',
  'contractorPaymentAmount',
  'contractorPaymentDueDate',
  'contractorPaymentOnResult',
  'contractorPaymentResultDelayDays',
  'type',
  'priority',
  'status',
  'isUnderControl',
  'result',
] as const satisfies readonly TaskTemplateField[];

const ARRAY_FIELDS = ['tags', 'reminders', 'channels'] as const satisfies readonly TaskTemplateField[];

function hasTemplateValue(value: unknown): boolean {
  if (typeof value === 'string') {
    return value.trim().length > 0;
  }

  if (Array.isArray(value)) {
    return value.length > 0;
  }

  return value !== null && value !== undefined;
}

function uniqueValues<T extends string>(current: readonly T[] | undefined, template: readonly T[] | undefined): T[] {
  const values: T[] = [];
  const seen = new Set<string>();

  for (const value of [...(current ?? []), ...(template ?? [])]) {
    const key = value.trim().toLocaleLowerCase('ru');
    if (!key || seen.has(key)) {
      continue;
    }

    seen.add(key);
    values.push(value);
  }

  return values;
}

function participantKey(participant: TaskJournalParticipant): string {
  const identity = participant.userId ?? participant.email?.trim().toLocaleLowerCase('ru') ?? participant.displayName.trim().toLocaleLowerCase('ru');
  return `${participant.role}:${identity}`;
}

function mergeParticipants(
  current: readonly TaskJournalParticipant[] | undefined,
  template: readonly TaskJournalParticipant[] | undefined,
): TaskJournalParticipant[] {
  const result: TaskJournalParticipant[] = [];
  const seen = new Set<string>();
  const roleCounts = new Map<string, number>();

  const append = (participant: TaskJournalParticipant) => {
    const key = participantKey(participant);
    if (seen.has(key)) {
      return;
    }

    seen.add(key);
    const roleCount = roleCounts.get(participant.role) ?? 0;
    roleCounts.set(participant.role, roleCount + 1);
    result.push({
      ...participant,
      sortOrder: participant.sortOrder ?? roleCount,
      approvalStatus: participant.approvalStatus ?? 'pending',
    });
  };

  for (const participant of current ?? []) {
    append(participant);
  }

  for (const participant of template ?? []) {
    append(participant);
  }

  return result;
}

export function applyTaskTemplateDefaults<T extends TaskJournalUpsertPayload>(
  current: T,
  template?: Partial<TaskJournalUpsertPayload> | null,
  options: ApplyTaskTemplateOptions = {},
): T {
  if (!template) {
    return current;
  }

  const protectedFields = new Set<TaskTemplateField>(options.protectedFields ?? []);
  const replaceFields = new Set<TaskTemplateField>(options.replaceFields ?? []);
  const next: TaskJournalUpsertPayload = { ...current };

  for (const field of VALUE_FIELDS) {
    if (protectedFields.has(field)) {
      continue;
    }

    const templateValue = template[field];
    if (!hasTemplateValue(templateValue)) {
      continue;
    }

    if (replaceFields.has(field) || !hasTemplateValue(next[field])) {
      next[field] = templateValue as never;
    }
  }

  for (const field of ARRAY_FIELDS) {
    if (protectedFields.has(field)) {
      continue;
    }

    const templateValue = template[field] as string[] | undefined;
    if (!hasTemplateValue(templateValue)) {
      continue;
    }

    next[field] = uniqueValues(next[field] as string[] | undefined, templateValue) as never;
  }

  if (!protectedFields.has('participants') && hasTemplateValue(template.participants)) {
    next.participants = mergeParticipants(next.participants, template.participants);
  }

  return next as T;
}
