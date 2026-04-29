import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { MoneyOperations } from './MoneyOperations';
import type { MoneyOperation, MoneyOperationsSummary } from '../../types';

const {
  createCashOrderFromPaymentMock,
  createCashOrderFromMoneyOperationMock,
  createMoneyOperationMock,
  deleteMoneyOperationMock,
  getCashOrderPrintHtmlMock,
  getCashOrdersPagedMock,
  getMoneyOperationMock,
  getMoneyOperationsPagedMock,
  importBankStatementMock,
  toastErrorMock,
  toastSuccessMock,
  updateMoneyOperationMock,
} = vi.hoisted(() => ({
  createCashOrderFromPaymentMock: vi.fn(),
  createCashOrderFromMoneyOperationMock: vi.fn(),
  createMoneyOperationMock: vi.fn(),
  deleteMoneyOperationMock: vi.fn(),
  getCashOrderPrintHtmlMock: vi.fn(),
  getCashOrdersPagedMock: vi.fn(),
  getMoneyOperationMock: vi.fn(),
  getMoneyOperationsPagedMock: vi.fn(),
  importBankStatementMock: vi.fn(),
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
    createCashOrderFromPayment: createCashOrderFromPaymentMock,
    createCashOrderFromMoneyOperation: createCashOrderFromMoneyOperationMock,
    createMoneyOperation: createMoneyOperationMock,
    deleteMoneyOperation: deleteMoneyOperationMock,
    getCashOrderPrintHtml: getCashOrderPrintHtmlMock,
    getCashOrdersPaged: getCashOrdersPagedMock,
    getMoneyOperation: getMoneyOperationMock,
    getMoneyOperationsPaged: getMoneyOperationsPagedMock,
    importBankStatement: importBankStatementMock,
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
    cashOrderId: overrides.cashOrderId ?? null,
    cashOrderNumber: overrides.cashOrderNumber ?? null,
    cashOrderStatus: overrides.cashOrderStatus ?? null,
    canCreateCashOrder: overrides.canCreateCashOrder ?? false,
  };
}

describe('MoneyOperations integration', () => {
  let operations: MoneyOperation[];
  let nextId: number;

  beforeEach(() => {
    operations = [createOperation()];
    nextId = 10;

    createCashOrderFromPaymentMock.mockReset();
    createCashOrderFromMoneyOperationMock.mockReset();
    createMoneyOperationMock.mockReset();
    deleteMoneyOperationMock.mockReset();
    getCashOrderPrintHtmlMock.mockReset();
    getCashOrdersPagedMock.mockReset();
    getMoneyOperationMock.mockReset();
    getMoneyOperationsPagedMock.mockReset();
    importBankStatementMock.mockReset();
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

    getCashOrdersPagedMock.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 10,
    });

    getMoneyOperationMock.mockImplementation(async (id: number) => operations.find((item) => item.id === id));

    importBankStatementMock.mockResolvedValue({
      rowsRead: 0,
      created: 0,
      skipped: 0,
      errors: [],
      operations: [],
    });

    createMoneyOperationMock.mockImplementation(async (payload: MoneyOperation) => {
      const created = { ...payload, id: nextId++, updatedAt: null };
      operations = [created, ...operations];
      return created;
    });

    createCashOrderFromPaymentMock.mockImplementation(async (paymentId: number) => ({
      id: 5,
      number: 'ПКО-2026-0001',
      type: 'Incoming',
      status: 'Posted',
      date: '2026-04-20',
      amount: 25000,
      currency: 'RUB',
      clientId: 74,
      paymentId,
      invoiceId: null,
      account: '33',
      payerName: 'Ахмедова Айтан Рафик Кызы',
      basis: 'Оплата от клиента. счет №33',
      comment: null,
      createdAt: '2026-04-20T08:00:00Z',
      updatedAt: null,
    }));

    createCashOrderFromMoneyOperationMock.mockImplementation(async (operationId: number) => ({
      id: 6,
      number: 'ПКО-2026-0002',
      type: 'Incoming',
      status: 'Posted',
      date: '2026-04-20',
      amount: 5000,
      currency: 'RUB',
      clientId: null,
      paymentId: null,
      moneyOperationId: operationId,
      invoiceId: null,
      account: 'Касса',
      payerName: 'Ручной клиент',
      basis: 'Прием наличных в кассу',
      comment: null,
      createdAt: '2026-04-20T08:00:00Z',
      updatedAt: null,
    }));

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
    await screen.findByText('Добавить операцию по банку');

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
    await screen.findByText('Редактировать операцию по банку');

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

  it('creates a cash order for a readonly cash payment', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: -77,
        source: 'Cash',
        direction: 'Income',
        accountName: '33',
        counterpartyName: 'Ахмедова Айтан Рафик Кызы',
        amount: 25000,
        isReadonly: true,
        relatedPaymentId: 77,
        canCreateCashOrder: true,
      }),
    ];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Ахмедова Айтан Рафик Кызы');
    await user.click(screen.getByRole('button', { name: 'Создать ПКО' }));

    await waitFor(() => expect(createCashOrderFromPaymentMock).toHaveBeenCalledWith(77));
    expect(toastSuccessMock).toHaveBeenCalledWith('Создан кассовый ордер ПКО-2026-0001.', 'Касса');
    await waitFor(() => expect(getCashOrdersPagedMock).toHaveBeenCalledTimes(2));
  }, 15000);

  it('creates a cash order for a manual cash operation', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 11,
        source: 'Cash',
        direction: 'Income',
        accountName: 'Касса',
        counterpartyName: 'Ручной клиент',
        amount: 5000,
        isReadonly: false,
        relatedPaymentId: null,
        canCreateCashOrder: true,
      }),
    ];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Ручной клиент');
    await user.click(screen.getByRole('button', { name: 'Создать ПКО' }));

    await waitFor(() => expect(createCashOrderFromMoneyOperationMock).toHaveBeenCalledWith(11));
    expect(toastSuccessMock).toHaveBeenCalledWith('Создан кассовый ордер ПКО-2026-0002.', 'Касса');
    await waitFor(() => expect(getCashOrdersPagedMock).toHaveBeenCalledTimes(2));
  }, 15000);

  it('loads and renders the cash orders journal in the cash section', async () => {
    operations = [];
    getCashOrdersPagedMock.mockResolvedValueOnce({
      items: [
        {
          id: 9,
          number: 'ПКО-2026-0003',
          type: 'Incoming',
          status: 'Posted',
          date: '2026-04-29',
          amount: 25000,
          currency: 'RUB',
          clientId: 74,
          clientName: 'Ахмедова Айтан Рафик Кызы',
          paymentId: 77,
          moneyOperationId: null,
          invoiceId: null,
          account: '33',
          payerName: 'Ахмедова Айтан Рафик Кызы',
          basis: 'Оплата от клиента. счет №33',
          comment: null,
          createdAt: '2026-04-29T08:00:00Z',
          updatedAt: null,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 10,
    });

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await screen.findByText('ПКО-2026-0003');
    await screen.findByText('Оплата от клиента. счет №33');
    expect(getCashOrdersPagedMock).toHaveBeenCalledWith(
      expect.objectContaining({
        page: 1,
        pageSize: 10,
      }),
    );
  }, 15000);

  it('filters the cash orders journal by outgoing orders', async () => {
    const user = userEvent.setup();
    operations = [];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'РКО' }));

    await waitFor(() =>
      expect(getCashOrdersPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({
          type: 'Outgoing',
          page: 1,
          pageSize: 10,
        }),
      ),
    );
  }, 15000);

  it('filters the cash orders journal by cancelled status', async () => {
    const user = userEvent.setup();
    operations = [];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'Отменён' }));

    await waitFor(() =>
      expect(getCashOrdersPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({
          status: 'Cancelled',
          page: 1,
          pageSize: 10,
        }),
      ),
    );
  }, 15000);

  it('imports a bank statement file and reloads bank operations', async () => {
    operations = [];
    importBankStatementMock.mockResolvedValueOnce({
      rowsRead: 2,
      created: 2,
      skipped: 0,
      errors: [],
      operations: [
        {
          id: 301,
          date: '2026-04-29',
          direction: 'Income',
          amount: 25000,
          counterpartyName: 'Ахмедова Айтан Рафик Кызы',
          paymentOrderNumber: '101',
        },
      ],
    });
    getMoneyOperationMock.mockResolvedValueOnce(createOperation({
      id: 301,
      source: 'Bank',
      direction: 'Income',
      amount: 25000,
      counterpartyName: 'Ахмедова Айтан Рафик Кызы',
      paymentOrderNumber: '101',
    }));

    const { container } = render(<MoneyOperations source="Bank" />);

    await screen.findByText('Загрузить выписку');
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(['Дата;Сумма\n29.04.2026;25000'], 'statement.csv', { type: 'text/csv' });
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => expect(importBankStatementMock).toHaveBeenCalledWith(file));
    await screen.findByText('Импорт выписки: прочитано 2, создано 2, пропущено 0');
    await screen.findByText('Ахмедова Айтан Рафик Кызы');
    await screen.findByText('101');
    await userEvent.click(screen.getByRole('button', { name: 'Открыть' }));
    await waitFor(() => expect(getMoneyOperationMock).toHaveBeenCalledWith(301));
    await screen.findByText('Редактировать операцию по банку');
    expect(toastSuccessMock).toHaveBeenCalledWith('Создано операций: 2. Пропущено: 0.', 'Выписка банка');
    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(2));
  }, 15000);
});
