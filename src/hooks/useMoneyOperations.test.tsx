import { act, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { useMoneyOperations } from './useMoneyOperations';
import type { MoneyOperation, MoneyOperationsSummary } from '../types';

const { getMoneyOperationsPagedMock } = vi.hoisted(() => ({
  getMoneyOperationsPagedMock: vi.fn(),
}));

vi.mock('../services/api', () => ({
  apiService: {
    getMoneyOperationsPaged: getMoneyOperationsPagedMock,
  },
}));

const summary: MoneyOperationsSummary = {
  startBalance: 0,
  income: 0,
  expense: 0,
  endBalance: 0,
  incomeCount: 0,
  expenseCount: 0,
  bySource: [],
};

function createOperation(id: number, counterpartyName: string): MoneyOperation {
  return {
    id,
    source: 'Bank',
    direction: 'Income',
    accountingType: 'Fact',
    amount: 100,
    currency: 'RUB',
    date: '2026-04-29',
    recognitionDate: '2026-04-29',
    accountName: 'Основной счет',
    moneyAccountId: null,
    moneyAccountName: null,
    moneyAccount: null,
    counterpartyName,
    categoryName: 'Доход',
    paymentOrderNumber: null,
    purpose: null,
    paidInvoiceNumber: null,
    paidAmount: null,
    isAdvancePayment: false,
    projectName: null,
    directionName: null,
    salesPointName: null,
    paymentPriority: null,
    paidAccountName: null,
    linkedInvoiceId: null,
    linkedInvoiceNumber: null,
    linkedInvoiceDate: null,
    matchStatus: 'None',
    matchComment: null,
    matchedAt: null,
    createdAt: '2026-04-29T10:00:00Z',
    updatedAt: null,
    isReadonly: false,
    relatedPaymentId: null,
    cashOrderId: null,
    cashOrderNumber: null,
    cashOrderStatus: null,
    canCreateCashOrder: false,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });

  return { promise, resolve, reject };
}

function MoneyOperationsProbe({ search }: { search: string }) {
  const { items, loading } = useMoneyOperations({ source: 'Bank', search, page: 1, pageSize: 25 });

  return (
    <div>
      <div data-testid="loading">{loading ? 'loading' : 'ready'}</div>
      <div data-testid="items">{items.map((item) => item.counterpartyName).join(', ')}</div>
    </div>
  );
}

describe('useMoneyOperations', () => {
  beforeEach(() => {
    getMoneyOperationsPagedMock.mockReset();
  });

  it('keeps the latest response when an older request finishes later', async () => {
    const first = deferred<{ items: MoneyOperation[]; total: number; summary: MoneyOperationsSummary }>();
    const second = deferred<{ items: MoneyOperation[]; total: number; summary: MoneyOperationsSummary }>();

    getMoneyOperationsPagedMock
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);

    const { rerender } = render(<MoneyOperationsProbe search="старый" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(1));
    const firstSignal = getMoneyOperationsPagedMock.mock.calls[0]?.[1]?.signal as AbortSignal | undefined;
    expect(firstSignal?.aborted).toBe(false);

    rerender(<MoneyOperationsProbe search="новый" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(2));
    const secondSignal = getMoneyOperationsPagedMock.mock.calls[1]?.[1]?.signal as AbortSignal | undefined;
    expect(firstSignal?.aborted).toBe(true);
    expect(secondSignal?.aborted).toBe(false);

    await act(async () => {
      second.resolve({
        items: [createOperation(2, 'Новый клиент')],
        total: 1,
        summary,
      });
      await Promise.resolve();
    });

    expect(screen.getByTestId('items').textContent).toBe('Новый клиент');
    expect(screen.getByTestId('loading').textContent).toBe('ready');

    await act(async () => {
      first.resolve({
        items: [createOperation(1, 'Старый клиент')],
        total: 1,
        summary,
      });
      await Promise.resolve();
    });

    expect(screen.getByTestId('items').textContent).toBe('Новый клиент');
  });

  it('aborts the active request when the hook unmounts', async () => {
    const pending = deferred<{ items: MoneyOperation[]; total: number; summary: MoneyOperationsSummary }>();
    getMoneyOperationsPagedMock.mockReturnValue(pending.promise);

    const { unmount } = render(<MoneyOperationsProbe search="активный" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(1));
    const signal = getMoneyOperationsPagedMock.mock.calls[0]?.[1]?.signal as AbortSignal | undefined;
    expect(signal?.aborted).toBe(false);

    unmount();

    expect(signal?.aborted).toBe(true);
  });
});
