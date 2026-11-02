import { useCallback, useMemo, useRef, useState } from 'react';
import type { ClientBalanceSummary, MoneyOperation } from '../types';
import { apiService, type PagedResult } from '../services/api';
import { captureAuthSessionIdentity } from '../services/authService';
import { awaitOwnedUiResult, StaleUiOperationError } from '../utils/StaleUiOperationError';
import { extractDisplayErrorMessage } from '../utils/errorUtils';
import type { DocumentReadOwner } from './useDocumentDeliveryOwner';
import { useDictionarySnapshot } from './useDictionarySnapshot';

type MoneyPage = Pick<PagedResult<MoneyOperation>, 'items' | 'total' | 'page' | 'pageSize'>;
type Options = {
  owner: DocumentReadOwner;
  balanceEnabled: boolean;
  movementsEnabled: boolean;
  clientId: number | null;
  legalEntityId: number | null;
  invoiceId: number | null;
};
const EMPTY_OPERATIONS: MoneyOperation[] = [];
const PAGE_SIZE = 20;
const validId = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const optionalText = (value: unknown) => value == null || typeof value === 'string';

function assertBalance(value: unknown, clientId: number | null, legalEntityId: number | null): asserts value is ClientBalanceSummary {
  if (!record(value) || typeof value.balance !== 'number' || !Number.isFinite(value.balance)
    || (clientId ? value.clientId !== clientId || value.legalEntityId != null && value.legalEntityId !== 0
      : value.legalEntityId !== legalEntityId || value.clientId != null && value.clientId !== 0)) {
    throw new Error('Не удалось подтвердить баланс выбранного контрагента. Повторите загрузку.');
  }
}
function assertMoneyPage(value: unknown, invoiceId: number, page: number): asserts value is MoneyPage {
  if (!record(value) || !Array.isArray(value.items) || !Number.isSafeInteger(value.total) || Number(value.total) < 0
    || value.page !== page || value.pageSize !== PAGE_SIZE
    || value.items.length !== Math.min(PAGE_SIZE, Math.max(0, Number(value.total) - (page - 1) * PAGE_SIZE))
    || !value.items.every(row => record(row) && typeof row.id === 'number' && Number.isSafeInteger(row.id) && row.id !== 0
      && (row.id > 0 || row.isReadonly === true) && row.linkedInvoiceId === invoiceId
      && (row.relatedPaymentId == null || validId(row.relatedPaymentId) && (row.id > 0 || row.relatedPaymentId === -row.id))
      && typeof row.amount === 'number' && Number.isFinite(row.amount)
      && typeof row.direction === 'string' && ['Income', 'Expense', 'Transfer'].includes(row.direction)
      && typeof row.source === 'string' && ['Bank', 'Cash', 'Card', 'EMoney'].includes(row.source)
      && typeof row.accountingType === 'string' && ['Fact', 'Plan'].includes(row.accountingType) && typeof row.date === 'string' && Number.isFinite(Date.parse(row.date))
      && (row.isReadonly === undefined || typeof row.isReadonly === 'boolean')
      && ['currency', 'purpose', 'counterpartyName', 'paidInvoiceNumber', 'accountName', 'matchStatus'].every(key => optionalText(row[key])))
    || new Set(value.items.map(row => `${row.isReadonly ? 'payment' : 'operation'}:${row.id}`)).size !== value.items.length) {
    throw new Error('Не удалось подтвердить движения денег по выбранному счёту. Повторите загрузку.');
  }
}

export function useInvoiceFinancialReads(options: Options) {
  const { clientId, legalEntityId, invoiceId, owner } = options;
  const captureForm = owner.capture;
  const hasOwner = captureAuthSessionIdentity().authenticated && Boolean(captureForm());
  const balanceEnabled = options.balanceEnabled && hasOwner
    && (validId(clientId) && legalEntityId == null || validId(legalEntityId) && clientId == null);
  const movementsEnabled = options.movementsEnabled && hasOwner && validId(invoiceId);
  const balanceScope = JSON.stringify([owner.scope, clientId, legalEntityId]);
  const movementScope = JSON.stringify([owner.scope, invoiceId]);
  const [pageState, setPageState] = useState({ scope: movementScope, page: 1 });
  const page = pageState.scope === movementScope ? pageState.page : 1;
  const readBalance = useCallback(async (signal: AbortSignal) => {
    const form = captureForm();
    if (!form) throw new StaleUiOperationError();
    const value = await awaitOwnedUiResult(() => clientId
      ? apiService.getClientBalance(clientId, { signal })
      : apiService.getLegalEntityBalance(legalEntityId!, { signal }), () => form.isCurrent() && !signal.aborted)
      .catch(error => { throw error instanceof Error ? error : new Error('Не удалось загрузить баланс контрагента. Повторите загрузку.'); });
    assertBalance(value, clientId, legalEntityId); return value;
  }, [captureForm, clientId, legalEntityId]);
  const readMovements = useCallback(async (signal: AbortSignal) => {
    const form = captureForm();
    if (!form) throw new StaleUiOperationError();
    const value = await awaitOwnedUiResult(() => apiService.getMoneyOperationsPaged({ linkedInvoiceId: invoiceId!, page, pageSize: PAGE_SIZE },
      { signal, cache: 'no-store', expectedStatus: 200 }), () => form.isCurrent() && !signal.aborted)
      .catch(error => { throw error instanceof Error ? error : new Error('Не удалось загрузить движения денег. Повторите загрузку.'); });
    assertMoneyPage(value, invoiceId!, page); return value;
  }, [captureForm, invoiceId, page]);
  const balance = useDictionarySnapshot<ClientBalanceSummary | null>(readBalance, null, { enabled: balanceEnabled, ownerScope: balanceScope }, false);
  const money = useDictionarySnapshot<MoneyPage | null>(readMovements, null,
    { enabled: movementsEnabled, ownerScope: JSON.stringify([movementScope, page]) }, false);
  const { capture: captureBalance, refresh: refreshBalance, updateValue: updateBalance } = balance;
  const { capture: captureMoney, refresh: refreshMoney, updateValue: updateMoney, loading: moneyLoading, value: moneyValue, error: moneyError } = money;
  const pending = useRef<{ balance?: { scope: string; promise: Promise<void> }; money?: { scope: string; promise: Promise<void> } }>({});
  const retryBalance = useCallback(() => {
    if (!balanceEnabled || !captureForm() || !captureBalance()) return Promise.resolve();
    if (pending.current.balance?.scope === balanceScope) return pending.current.balance.promise;
    const attempt = { scope: balanceScope, promise: refreshBalance() };
    pending.current.balance = attempt;
    void attempt.promise.finally(() => { if (pending.current.balance === attempt) delete pending.current.balance; });
    return attempt.promise;
  }, [balanceEnabled, captureForm, captureBalance, refreshBalance, balanceScope]);
  const moneyKey = useMemo(() => JSON.stringify([movementScope, page]), [movementScope, page]);
  const retryMovements = useCallback(() => {
    if (!movementsEnabled || !captureForm() || !captureMoney()) return Promise.resolve();
    if (pending.current.money?.scope === moneyKey) return pending.current.money.promise;
    const attempt = { scope: moneyKey, promise: refreshMoney() };
    pending.current.money = attempt;
    void attempt.promise.finally(() => { if (pending.current.money === attempt) delete pending.current.money; });
    return attempt.promise;
  }, [movementsEnabled, captureForm, captureMoney, refreshMoney, moneyKey]);
  const cancel = useCallback(() => {
    if (!captureForm()) return;
    updateBalance(() => null); updateMoney(() => null);
  }, [captureForm, updateBalance, updateMoney]);
  const movePage = useCallback((delta: -1 | 1) => {
    if (!movementsEnabled || !captureForm() || !captureMoney() || moneyLoading) return;
    const next = page + delta;
    if (next < 1 || delta > 0 && (moneyError || !moneyValue || next > Math.ceil(moneyValue.total / PAGE_SIZE))) return;
    setPageState({ scope: movementScope, page: next });
  }, [movementsEnabled, captureForm, captureMoney, moneyLoading, moneyValue, moneyError, page, movementScope]);
  const readyBalance = balanceEnabled && !balance.loading && !balance.error ? balance.value : null;
  const readyMoney = movementsEnabled && !money.loading && !money.error ? money.value : null;
  return {
    balance: readyBalance, balanceLoading: balanceEnabled && balance.loading,
    balanceError: balanceEnabled && balance.error ? extractDisplayErrorMessage(balance.error, 'Не удалось загрузить баланс контрагента. Повторите загрузку.') : null, retryBalance,
    movementsEnabled, movements: readyMoney?.items ?? EMPTY_OPERATIONS, movementsLoading: movementsEnabled && money.loading,
    movementsError: movementsEnabled && money.error ? extractDisplayErrorMessage(money.error, 'Не удалось загрузить движения денег по счёту. Повторите загрузку.') : null,
    total: readyMoney?.total ?? null, page, pageSize: PAGE_SIZE,
    hasNext: Boolean(readyMoney && page * PAGE_SIZE < readyMoney.total), retryMovements, movePage, cancel,
  };
}
