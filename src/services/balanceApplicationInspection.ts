import type { BalanceApplicationCommand, BalanceApplicationInspection, ManagedBalanceApplicationResult } from '../types';

export type BalanceApplicationOwnerKind = 'client' | 'legalEntity';
const uuid = /^(?!00000000-0000-0000-0000-000000000000$)[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;
function positiveId(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 && value <= 2147483647;
}
function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}
function key(value: unknown): string {
  if (typeof value !== 'string' || value.length !== 36 || !uuid.test(value)) throw new Error('Некорректный идентификатор зачёта.');
  return value.toLowerCase();
}
function sameKey(value: unknown, expected: string): boolean {
  return typeof value === 'string' && value.length === 36 && uuid.test(value) && value.toLowerCase() === expected;
}

export function balanceApplicationInspectionPath(kind: BalanceApplicationOwnerKind, ownerId: number, paymentId: number, requestId: string): string {
  if (!positiveId(ownerId) || !positiveId(paymentId) || !['client', 'legalEntity'].includes(kind))
    throw new Error('Не выбран сохранённый контрагент или платёж.');
  return `/${kind === 'client' ? 'clients' : 'legal-entities'}/${ownerId}/balance/applications/${key(requestId)}?paymentId=${paymentId}`;
}

export function prepareBalanceApplicationCommand(value: BalanceApplicationCommand): Readonly<Required<BalanceApplicationCommand>> {
  if (!record(value)) throw new Error('Проверьте параметры зачёта.');
  const { paymentId, amount, description, sourcePaymentId } = value;
  if (!positiveId(paymentId) || typeof amount !== 'number' || !Number.isFinite(amount) || amount <= 0
    || (description !== undefined && description !== null && (typeof description !== 'string' || description.length > 500))
    || (sourcePaymentId !== undefined && sourcePaymentId !== null && (!positiveId(sourcePaymentId) || sourcePaymentId === paymentId)))
    throw new Error('Проверьте параметры зачёта.');
  return Object.freeze({ paymentId, amount, description: description?.trim() ?? '', sourcePaymentId: sourcePaymentId ?? null });
}

export function parseManagedBalanceApplicationResult(value: unknown, requestId: string): ManagedBalanceApplicationResult {
  const expected = key(requestId);
  if (!record(value) || !sameKey(value.requestId, expected) || typeof value.isReplay !== 'boolean'
    || typeof value.appliedAmount !== 'number' || !Number.isFinite(value.appliedAmount) || value.appliedAmount <= 0
    || typeof value.balance !== 'number' || !Number.isFinite(value.balance))
    throw new Error('Результат зачёта ещё не подтверждён. Проверьте его перед новой операцией.');
  return { requestId: expected, appliedAmount: value.appliedAmount, balance: value.balance, isReplay: value.isReplay };
}

export function parseBalanceApplicationInspection(value: unknown, requestId: string, paymentId: number): BalanceApplicationInspection {
  const expected = key(requestId);
  const invalid = () => new Error('Результат проверки зачёта не подтверждён. Обновите проверку перед новым зачётом.');
  if (!positiveId(paymentId) || !record(value) || value.paymentId !== paymentId || !sameKey(value.requestId, expected)) throw invalid();
  if ((value.state === 'Unconfirmed' || value.state === 'Cancelled') && value.result === null)
    return { requestId: expected, paymentId, state: value.state, result: null };
  if (value.state !== 'Committed') throw invalid();
  const result = parseManagedBalanceApplicationResult(value.result, expected);
  if (result.isReplay !== true) throw invalid();
  return { requestId: expected, paymentId, state: 'Committed', result: {
    requestId: expected, appliedAmount: result.appliedAmount, balance: result.balance, isReplay: true,
  } };
}
