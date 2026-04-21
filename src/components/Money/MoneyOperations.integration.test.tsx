import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { MoneyOperations } from './MoneyOperations';
import type { MoneyOperation, MoneyOperationsSummary } from '../../types';

const {
  createMoneyOperationMock,
  deleteMoneyOperationMock,
  getMoneyOperationsPagedMock,
  toastErrorMock,
  toastSuccessMock,
  updateMoneyOperationMock,
} = vi.hoisted(() => ({
  createMoneyOperationMock: vi.fn(),
  deleteMoneyOperationMock: vi.fn(),
  getMoneyOperationsPagedMock: vi.fn(),
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
  updateMoneyOperationMock: vi.fn(),
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ user: { id: 7 } }),
}));

vi.mock('../../contexts/TabContext', () => ({
  useTabNavigation: () => ({ setActiveTab: vi.fn() }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    error: toastErrorMock,
    success: toastSuccessMock,
  }),
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: () => ({
    columnOrder: ['date', 'accountName', 'counterpartyName', 'categoryName', 'purpose'],
    orderedVisibleColumns: ['date', 'accountName', 'counterpartyName', 'categoryName', 'purpose'],
    visibleColumns: ['date', 'accountName', 'counterpartyName', 'categoryName', 'purpose'],
    pageSize: 25,
    setPageSize: vi.fn(),
    draggingColumn: null,
    toggleColumn: vi.fn(),
    moveColumn: vi.fn(),
    handleColumnDragStart: vi.fn(),
    handleColumnDragOver: vi.fn(),
    handleColumnDrop: vi.fn(),
    handleColumnDragEnd: vi.fn(),
    handleResizeStart: vi.fn(),
    getColumnWidth: () => 160,
    resetTableSettings: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    createMoneyOperation: createMoneyOperationMock,
    deleteMoneyOperation: deleteMoneyOperationMock,
    getMoneyOperationsPaged: getMoneyOperationsPagedMock,
    updateMoneyOperation: updateMoneyOperationMock,
  },
}));

vi.mock('../../utils/dateUtils', () => ({
  todayYMD: () => '2026-04-21',
  toDateInputValue: (value: string | Date | null | undefined) => {
    if (!value) return '';
    if (value instanceof Date) {
      return value.toISOString().slice(0, 10);
    }

    const normalized = String(value);
    return normalized.slice(0, 10);
  },
  fromInputToApiDate: (value: string | null | undefined) => value ?? undefined,
}));

function createSummary(items: MoneyOperation[]): MoneyOperationsSummary {
  const incomeItems = items.filter((item) => item.accountingType === 'Fact' && item.direction === 'Income');
  const expenseItems = items.filter((item) => item.accountingType === 'Fact' && item.direction === 'Expense');

  return {
    startBalance: 0,
    income: incomeItems.reduce((sum, item) => sum + item.amount, 0),
    expense: expenseItems.reduce((sum, item) => sum + item.amount, 0),
    endBalance:
      incomeItems.reduce((sum, item) => sum + item.amount, 0) -
      expenseItems.reduce((sum, item) => sum + item.amount, 0),
    incomeCount: incomeItems.length,
    expenseCount: expenseItems.length,
    bySource: [
      { source: 'Cash', startBalance: 0, income: 0, expense: 0, endBalance: 0 },
      {
        source: 'Bank',
        startBalance: 0,
        income: incomeItems.reduce((sum, item) => sum + item.amount, 0),
        expense: expenseItems.reduce((sum, item) => sum + item.amount, 0),
        endBalance:
          incomeItems.reduce((sum, item) => sum + item.amount, 0) -
          expenseItems.reduce((sum, item) => sum + item.amount, 0),
      },
      { source: 'Card', startBalance: 0, income: 0, expense: 0, endBalance: 0 },
    ],
  };
}

function createOperation(overrides: Partial<MoneyOperation> = {}): MoneyOperation {
  return {
    id: overrides.id ?? 1,
    source: overrides.source ?? 'Bank',
    direction: overrides.direction ?? 'Expense',
    accountingType: overrides.accountingType ?? 'Fact',
    accountName: overrides.accountName ?? 'Основной счёт',
    counterpartyName: overrides.counterpartyName ?? 'Поставщик А',
    categoryName: overrides.categoryName ?? 'Хозрасходы',
    amount: overrides.amount ?? 1500,
    currency: overrides.currency ?? 'RUB',
    paymentOrderNumber: overrides.paymentOrderNumber ?? '873864',
    date: overrides.date ?? '2026-04-20',
    recognitionDate: overrides.recognitionDate ?? '2026-04-20',
    isAdvancePayment: overrides.isAdvancePayment ?? true,
    purpose: overrides.purpose ?? 'Оплата услуг',
    paidInvoiceNumber: overrides.paidInvoiceNumber ?? 'INV-1',
    paidAmount: overrides.paidAmount ?? 1500,
    projectName: overrides.projectName ?? 'Проект Альфа',
    directionName: overrides.directionName ?? 'Корпоративное право',
    salesPointName: overrides.salesPointName ?? 'Офис',
    paymentPriority: overrides.paymentPriority ?? null,
    paidAccountName: overrides.paidAccountName ?? 'Счёт 51',
    createdAt: overrides.createdAt ?? '2026-04-20T08:00:00Z',
    updatedAt: overrides.updatedAt ?? null,
    isReadonly: overrides.isReadonly ?? false,
    relatedPaymentId: overrides.relatedPaymentId ?? null,
  };
}

describe('MoneyOperations integration', () => {
  let operations: MoneyOperation[];
  let nextId: number;

  beforeEach(() => {
    operations = [createOperation()];
    nextId = 10;

    createMoneyOperationMock.mockReset();
    deleteMoneyOperationMock.mockReset();
    getMoneyOperationsPagedMock.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    updateMoneyOperationMock.mockReset();

    getMoneyOperationsPagedMock.mockImplementation(async (params?: { source?: 'Bank' | 'Cash' | 'Card' }) => {
      const filtered = params?.source ? operations.filter((item) => item.source === params.source) : operations;
      return {
        items: filtered,
        total: filtered.length,
        page: 1,
        pageSize: 25,
        summary: createSummary(filtered),
      };
    });

    createMoneyOperationMock.mockImplementation(async (payload: MoneyOperation) => {
      const created = { ...payload, id: nextId++, updatedAt: null };
      operations = [created, ...operations];
      return created;
    });

    updateMoneyOperationMock.mockImplementation(async (id: number, payload: MoneyOperation) => {
      const updated = { ...payload, id, updatedAt: '2026-04-21T09:00:00Z' };
      operations = operations.map((item) => (item.id === id ? updated : item));
      return updated;
    });

    deleteMoneyOperationMock.mockImplementation(async (id: number) => {
      operations = operations.filter((item) => item.id !== id);
    });
  });

  it('uses the local current date when creating a bank operation and saves it', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');

    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банк');

    expect(screen.getByLabelText(/^Дата$/)).toHaveProperty('value', '2026-04-21');
    await user.click(screen.getByLabelText('Расход'));

    await user.clear(screen.getByLabelText('Банковский счёт'));
    await user.type(screen.getByLabelText('Банковский счёт'), 'Расчётный счёт');
    await user.clear(screen.getByLabelText('Поставщик'));
    await user.type(screen.getByLabelText('Поставщик'), 'Новый поставщик');
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Оплата по договору');

    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(createMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(createMoneyOperationMock).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 0,
        source: 'Bank',
        date: '2026-04-21',
        accountName: 'Расчётный счёт',
        counterpartyName: 'Новый поставщик',
        purpose: 'Оплата по договору',
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция создана.', 'Деньги');
    await screen.findByText('Новый поставщик');
  }, 15000);

  it('normalizes API dates in the edit form and updates the operation', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        date: '2026-04-20T00:00:00Z',
        recognitionDate: '2026-04-18T15:45:00Z',
        purpose: 'Старое назначение',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Старое назначение');

    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банк');

    expect(screen.getByLabelText(/^Дата$/)).toHaveProperty('value', '2026-04-20');
    expect(screen.getByLabelText(/^Дата признания расхода$/)).toHaveProperty('value', '2026-04-18');

    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Обновлённое назначение');

    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        date: '2026-04-20',
        recognitionDate: '2026-04-18',
        purpose: 'Обновлённое назначение',
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция обновлена.', 'Деньги');
    await screen.findByText('Обновлённое назначение');
  }, 15000);

  it('deletes a custom money operation after confirmation', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');

    await user.click(screen.getByRole('button', { name: 'Удалить' }));

    await screen.findByText('Удалить операцию?');
    const confirmButtons = screen.getAllByRole('button', { name: 'Удалить' });
    await user.click(confirmButtons[confirmButtons.length - 1]);

    await waitFor(() => expect(deleteMoneyOperationMock).toHaveBeenCalledWith(1));
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция удалена.', 'Деньги');
    await waitFor(() => {
      expect(screen.queryByText('Поставщик А')).toBeNull();
    });
  }, 15000);
});
