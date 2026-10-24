import type { TaskBillingBulkItemResult, TaskBillingBulkReason, TaskBillingBulkResult, TaskJournalItem } from '../../utils/taskJournalStore';

export type BillingTarget = 'billable' | 'internal';
export type BillingSnapshot = { scopeKey: string; scopeLabel: string; tasks: TaskJournalItem[] };

export const BILLING_REASONS: Record<TaskBillingBulkReason, string> = {
  notAvailable: 'Задача недоступна.', forbidden: 'Недостаточно прав на изменение задачи.',
  invoiced: 'Есть связанный счёт. Исправление выполняется в финансовом разделе.',
  paid: 'Есть фактическая выплата. Учёт защищён.',
  contractorPaymentProtected: 'Выплата исполнителю или её расчёт требуют отдельной проверки. Учёт защищён.',
  contractual: 'Договорной учёт не входит в эту операцию.',
  unsupportedStatus: 'Этот статус не поддерживает массовую смену учёта.',
  cancelled: 'Отменённая задача не изменяется.',
  completedExcluded: 'Завершённая задача не включена в выбранный режим.',
  unknownClassification: 'Текущий тип учёта требует отдельного разбора.',
  clientCaseRequired: 'Для платной задачи нужны клиент и дело.',
  versionConflict: 'Задача изменилась. Сначала обновите сведения и подтвердите заново.',
};

export function freezeBillingSelection(tasks: TaskJournalItem[], scopeKey: string, scopeLabel: string): BillingSnapshot {
  if (!tasks.length || tasks.length > 500 || tasks.some((task) => !Number.isSafeInteger(task.id) || task.id <= 0
    || (task.updatedAt != null && typeof task.updatedAt !== 'string'))) {
    throw new Error('Выберите от 1 до 500 задач на текущей странице.');
  }
  return { scopeKey, scopeLabel, tasks: [...new Map(tasks.map((task) => [task.id, { ...task, updatedAt: task.updatedAt ?? null }])).values()] };
}

export function billingPayload(tasks: TaskJournalItem[], target: BillingTarget, includeCompleted: boolean) {
  const fixed = freezeBillingSelection(tasks, '', '').tasks;
  return {
    ids: fixed.map((task) => task.id), billingClassification: target, includeCompleted,
    // Never parse/format these strings: API concurrency tokens can contain seven fractional digits.
    expectedUpdatedAtById: Object.fromEntries(fixed.map((task) => [String(task.id), task.updatedAt ?? null])),
  };
}

export function billingPreview(task: TaskJournalItem, target: BillingTarget, includeCompleted: boolean): string {
  if (task.billingInvoiceId != null) return BILLING_REASONS.invoiced;
  if (task.contractorPaymentId != null || task.contractorPaymentStatus) return BILLING_REASONS.contractorPaymentProtected;
  // Updating a completed result-triggered task also shifts delayed payout backfill, even before a payment exists.
  if (task.status === 'done' && task.contractorPaymentEnabled && task.contractorPaymentOnResult) return BILLING_REASONS.contractorPaymentProtected;
  if (task.billingContractId != null || task.billingMode) return BILLING_REASONS.contractual;
  if (task.status === 'cancelled') return BILLING_REASONS.cancelled;
  if (task.status === 'done' && !includeCompleted) return BILLING_REASONS.completedExcluded;
  if (!['new', 'inProgress', 'waiting', 'onApproval', 'approved', 'revision', 'overdueApproval', 'done'].includes(task.status)) return BILLING_REASONS.unsupportedStatus;
  if (task.clientBillingClassification && !['legacy', 'billable', 'internal'].includes(task.clientBillingClassification)) return BILLING_REASONS.unknownClassification;
  if (target === 'billable' && (!task.clientCaseId || (!task.clientId && !task.legalEntityId))) return BILLING_REASONS.clientCaseRequired;
  return 'Предварительно подходит; окончательное решение принимает сервер.';
}

/** Fail closed: HTTP 200 is insufficient evidence of a complete, internally consistent outcome. */
export function checkedBillingResult(value: unknown, ids: number[]): TaskBillingBulkResult {
  const result = value as TaskBillingBulkResult | null;
  const invalid = () => { throw new Error('Сервер вернул неподтверждённый итог. Обновите сведения перед повтором.'); };
  if (!result || !Array.isArray(result.items) || result.items.length !== ids.length
    || !['requested', 'updated', 'unchanged', 'skipped', 'conflicted'].every((key) => Number.isSafeInteger(result[key as keyof TaskBillingBulkResult])
      && Number(result[key as keyof TaskBillingBulkResult]) >= 0) || result.requested !== ids.length) return invalid();
  const expected = new Set(ids);
  const counts = { updated: 0, unchanged: 0, skipped: 0, conflict: 0 };
  const items: TaskBillingBulkItemResult[] = [];
  for (const item of result.items) {
    if (!item || !expected.delete(item.id) || typeof item.outcome !== 'string' || !Object.hasOwn(counts, item.outcome)) return invalid();
    const successful = item.outcome === 'updated' || item.outcome === 'unchanged';
    // The API omits nullable JSON properties. Explicit undefined cannot come from JSON and is not omission.
    const reason = Object.hasOwn(item, 'reason') ? item.reason : successful ? null : undefined;
    const updatedAt = Object.hasOwn(item, 'updatedAt') ? item.updatedAt : null;
    if (reason === undefined || (reason !== null && (typeof reason !== 'string' || !Object.hasOwn(BILLING_REASONS, reason)))
      || (!successful && reason === null) || (successful && reason !== null)
      || (updatedAt !== null && !isBillingUtcToken(updatedAt))
      || (item.outcome === 'updated' && updatedAt === null)
      || (item.outcome === 'conflict' && reason !== 'versionConflict')
      || (reason === 'versionConflict' && item.outcome !== 'conflict')
      || (reason === 'notAvailable' && updatedAt !== null)) return invalid();
    items.push({ id: item.id, outcome: item.outcome, reason, updatedAt });
    counts[item.outcome] += 1;
  }
  if (expected.size || counts.updated !== result.updated || counts.unchanged !== result.unchanged
    || counts.skipped !== result.skipped || counts.conflict !== result.conflicted) return invalid();
  return { requested: result.requested, updated: result.updated, unchanged: result.unchanged,
    skipped: result.skipped, conflicted: result.conflicted, items };
}

function isBillingUtcToken(value: unknown): value is string {
  if (typeof value !== 'string') return false;
  const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?Z$/.exec(value);
  if (!parts || Number(parts[1]) < 1) return false;
  // Validate the calendar without formatting or replacing the original concurrency token.
  const date = new Date(value);
  return Number.isFinite(date.getTime()) && [date.getUTCFullYear(), date.getUTCMonth() + 1, date.getUTCDate(),
    date.getUTCHours(), date.getUTCMinutes(), date.getUTCSeconds()].every((part, index) => part === Number(parts[index + 1]));
}

export function billingSummary(items: TaskBillingBulkItemResult[]): string {
  const count = (outcome: TaskBillingBulkItemResult['outcome']) => items.filter((item) => item.outcome === outcome).length;
  return `Изменено: ${count('updated')}. Уже установлен нужный учёт: ${count('unchanged')}. Пропущено: ${count('skipped')}. Конфликтов: ${count('conflict')}.`;
}
