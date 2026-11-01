import { useCallback, useLayoutEffect } from 'react';
import { apiService } from '../services/api';
import { isHttpErrorStatus } from '../utils/errorUtils';
import { useDictionarySnapshot } from './useDictionarySnapshot';
import type { ClientBalanceSummary, ClientBalanceTransaction } from '../types';

const EMPTY_TRANSACTIONS: ClientBalanceTransaction[] = [];
const UNCONFIRMED_BALANCE = 'Не удалось подтвердить баланс клиента. Повторите загрузку.';
const validId = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
const optionalId = (value: unknown) => value == null || typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
const finiteAmount = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value);
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);

function assertSummary(value: unknown, clientId: number): asserts value is ClientBalanceSummary {
  if (!record(value) || value.clientId !== clientId || !finiteAmount(value.balance) || !optionalId(value.legalEntityId))
    throw new Error(UNCONFIRMED_BALANCE);
}
function assertTransactions(value: unknown, clientId: number): asserts value is ClientBalanceTransaction[] {
  if (!Array.isArray(value) || !value.every(row => record(row) && validId(row.id) && row.clientId === clientId
    && finiteAmount(row.amount) && (row.direction === 'Credit' || row.direction === 'Debit')
    && typeof row.type === 'string' && Boolean(row.type.trim()) && typeof row.description === 'string'
    && typeof row.createdAt === 'string' && Number.isFinite(Date.parse(row.createdAt))
    && optionalId(row.legalEntityId) && optionalId(row.paymentId) && optionalId(row.invoiceId)
    && (row.createdByUserName == null || typeof row.createdByUserName === 'string'))
    || new Set(value.map(row => row.id)).size !== value.length) {
    throw new Error('Не удалось подтвердить историю баланса клиента. Повторите загрузку.');
  }
}

export function useClientBalanceReads(clientId: number, options: { enabled: boolean; ownerScope: string; historyOpen: boolean }) {
  const enabled = options.enabled && validId(clientId);
  const scope = JSON.stringify([options.ownerScope, clientId]);
  const loadSummary = useCallback(async (signal: AbortSignal) => {
    const value = await apiService.getClientBalance(clientId, { signal }); assertSummary(value, clientId); return value;
  }, [clientId]);
  const loadHistory = useCallback(async (signal: AbortSignal) => {
    const rows = await apiService.getClientBalanceTransactions(clientId, { signal }); assertTransactions(rows, clientId); return rows;
  }, [clientId]);
  const summary = useDictionarySnapshot<ClientBalanceSummary | null>(loadSummary, null, { enabled, ownerScope: scope }, false);
  const { value: summaryValue, loading: summaryLoading, error: summaryReadError, refresh: refreshSummary,
    capture: summaryCapture, updateValue: updateSummary } = summary;
  const ready = !summaryLoading && !summaryReadError && summaryValue !== null;
  const history = useDictionarySnapshot(loadHistory, EMPTY_TRANSACTIONS,
    { enabled: enabled && options.historyOpen && ready, ownerScope: JSON.stringify([scope, options.historyOpen]) }, false);
  const { value: historyRows, loading: historyLoading, error: historyReadError, refresh: refreshHistory, capture: historyCapture } = history;
  const historyDenied = isHttpErrorStatus(historyReadError, 401) || isHttpErrorStatus(historyReadError, 403);
  // A current denied journal also revokes the cached summary, including after the journal closes.
  useLayoutEffect(() => {
    if (historyDenied && summaryValue !== null) updateSummary(() => null);
  }, [historyDenied, summaryValue, updateSummary]);
  const missing = enabled && !summaryLoading && !summaryReadError && summaryValue === null;
  const balanceError = summaryReadError || (historyDenied ? historyReadError : null) || (missing ? UNCONFIRMED_BALANCE : null);
  const historyError = options.historyOpen ? historyReadError || (!summaryLoading ? balanceError : null) : null;
  const capture = useCallback(() => enabled ? summaryCapture() : null, [enabled, summaryCapture]);
  const captureHistory = useCallback(() => enabled && options.historyOpen ? historyCapture() : null, [enabled, options.historyOpen, historyCapture]);
  const retryTransactions = useCallback(async () => {
    if (!captureHistory()) return;
    await (!ready ? refreshSummary() : refreshHistory());
  }, [captureHistory, ready, refreshSummary, refreshHistory]);
  return {
    balance: ready && !historyDenied ? summaryValue : null, balanceLoading: summaryLoading, balanceError,
    transactions: historyLoading || historyError ? EMPTY_TRANSACTIONS : historyRows,
    transactionsLoading: enabled && options.historyOpen && (summaryLoading || historyLoading), transactionsError: historyError,
    refreshBalance: refreshSummary, refreshTransactions: refreshHistory, retryTransactions, capture, captureHistory,
  };
}
