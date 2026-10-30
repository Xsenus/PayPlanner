import type { BalanceApplicationInspection } from '../types';

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

export function balanceApplicationInspectionPath(kind: BalanceApplicationOwnerKind, ownerId: number, paymentId: number, requestId: string): string {
  if (!positiveId(ownerId) || !positiveId(paymentId) || !['client', 'legalEntity'].includes(kind))
    throw new Error('Не выбран сохранённый контрагент или платёж.');
  return `/${kind === 'client' ? 'clients' : 'legal-entities'}/${ownerId}/balance/applications/${key(requestId)}?paymentId=${paymentId}`;
}

export function parseBalanceApplicationInspection(value: unknown, requestId: string, paymentId: number): BalanceApplicationInspection {
  const expected = key(requestId);
  const invalid = () => new Error('Результат проверки зачёта не подтверждён. Обновите проверку перед новым зачётом.');
  if (!positiveId(paymentId) || !record(value) || value.paymentId !== paymentId || key(value.requestId) !== expected) throw invalid();
  if (value.state === 'Unconfirmed' && value.result === null)
    return { requestId: expected, paymentId, state: 'Unconfirmed', result: null };
  const result = value.result;
  if (value.state !== 'Committed' || !record(result) || key(result.requestId) !== expected || result.isReplay !== true
    || typeof result.appliedAmount !== 'number' || !Number.isFinite(result.appliedAmount) || result.appliedAmount <= 0
    || typeof result.balance !== 'number' || !Number.isFinite(result.balance)) throw invalid();
  return { requestId: expected, paymentId, state: 'Committed', result: {
    requestId: expected, appliedAmount: result.appliedAmount, balance: result.balance, isReplay: true,
  } };
}
