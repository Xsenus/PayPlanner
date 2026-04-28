import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { Payment } from '../../types';
import { Payments } from './Payments';

const { confirmMock, usePaymentsPagedMock } = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  usePaymentsPagedMock: vi.fn(),
}));

function createPersistedTableSettings(
  columns: Array<{ key: string; defaultWidth?: number; defaultVisible?: boolean }>,
  defaultPageSize: number,
) {
  const orderedKeys = columns
    .filter((column) => column.defaultVisible !== false)
    .map((column) => column.key);

  return {
    columnOrder: orderedKeys,
    orderedVisibleColumns: orderedKeys,
    visibleColumns: orderedKeys,
    pageSize: defaultPageSize,
    setPageSize: vi.fn(),
    draggingColumn: null,
    toggleColumn: vi.fn(),
    moveColumn: vi.fn(),
    handleColumnDragStart: vi.fn(),
    handleColumnDragOver: vi.fn(),
    handleColumnDrop: vi.fn(),
    handleColumnDragEnd: vi.fn(),
    handleResizeStart: vi.fn(),
    getColumnWidth: (key: string) =>
      columns.find((column) => column.key === key)?.defaultWidth ?? 180,
    resetTableSettings: vi.fn(),
  };
}

const paymentsPagedResult = {
  payments: [] as Payment[],
  total: 0,
  summary: {
    income: 0,
    expense: 0,
    balance: 0,
    total: 0,
    incomeCount: 0,
    expenseCount: 0,
  },
  loading: false,
  refreshing: false,
  refresh: vi.fn().mockResolvedValue(undefined),
  error: null,
};

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 7, role: { id: 1 } },
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => ({
    payments: {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
    },
  }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({
    t: () => undefined,
    formatDate: (value: string) => value,
  }),
}));

vi.mock('../../hooks/usePaymentsPaged', () => ({
  usePaymentsPaged: (params: unknown) => usePaymentsPagedMock(params),
}));

vi.mock('../../hooks/useDebouncedValue', () => ({
  useDebouncedValue: (value: string) => value,
}));

vi.mock('../../hooks/useClients', () => ({
  useClients: () => ({
    clients: [],
    loading: false,
  }),
}));

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    dealTypes: [],
    incomeTypes: [],
    paymentSources: [],
  }),
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: ({
    columns,
    defaultPageSize,
  }: {
    columns: Array<{ key: string; defaultWidth?: number; defaultVisible?: boolean }>;
    defaultPageSize: number;
  }) => createPersistedTableSettings(columns, defaultPageSize),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('../Calendar/PaymentModal', () => ({
  PaymentModal: (props: { isOpen: boolean; payment?: Payment | null }) =>
    props.isOpen ? <div data-testid="payments-edit-modal">{props.payment?.id ?? 'new'}</div> : null,
}));

vi.mock('../Calendar/PaymentsTable', () => ({
  PaymentsTable: (props: {
    payments: Payment[];
    onEditPayment?: (payment: Payment) => void;
  }) => (
    <div data-testid="payments-table">
      {props.payments.map((payment) => (
        <button
          key={payment.id}
          type="button"
          onClick={() => props.onEditPayment?.(payment)}
        >
          {`edit-payment-${payment.id}`}
        </button>
      ))}
    </div>
  ),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getPayment: vi.fn(),
    createPayment: vi.fn(),
    updatePayment: vi.fn(),
    updateInvoiceManagedPayment: vi.fn(),
    deletePayment: vi.fn(),
    getClientBalance: vi.fn(),
    applyClientBalance: vi.fn(),
  },
}));

import { apiService } from '../../services/api';

describe('Payments integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    confirmMock.mockResolvedValue(true);
    vi.mocked(apiService.getPayment).mockReset();
    vi.mocked(apiService.createPayment).mockReset();
    vi.mocked(apiService.updatePayment).mockReset();
    vi.mocked(apiService.updateInvoiceManagedPayment).mockReset();
    vi.mocked(apiService.deletePayment).mockReset();
    vi.mocked(apiService.getClientBalance).mockReset();
    vi.mocked(apiService.applyClientBalance).mockReset();
    vi.mocked(apiService.getClientBalance).mockResolvedValue({ clientId: 1, balance: 0 });
    vi.mocked(apiService.applyClientBalance).mockResolvedValue({ appliedAmount: 0, balance: 0 });
    usePaymentsPagedMock.mockReset();
    usePaymentsPagedMock.mockReturnValue(paymentsPagedResult);
    paymentsPagedResult.payments = [];
    paymentsPagedResult.total = 0;
  });

  it('opens the existing payment modal for invoice-managed payments', async () => {
    const invoiceManagedPayment = {
      id: 551,
      amount: 25_000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      type: 'Income',
      status: 'Pending',
      description: 'Оплата по счету',
      notes: '',
      systemNotes: '',
      isPaid: false,
      paidAmount: 0,
      outstandingAmount: 25_000,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Invoice',
      invoiceId: 159,
      account: '159',
      client: { id: 12, name: 'Тестовый клиент' },
    } as unknown as Payment;

    paymentsPagedResult.payments = [invoiceManagedPayment];
    paymentsPagedResult.total = 1;

    render(<Payments />);

    fireEvent.click(await screen.findByRole('button', { name: 'edit-payment-551' }));

    await waitFor(() => {
      expect(screen.getByTestId('payments-edit-modal').textContent).toContain('551');
    });
  });

  it('passes the selected client role into the paged payments query', async () => {
    render(<Payments />);

    fireEvent.click(screen.getByRole('button', { name: /Показать фильтры/i }));
    fireEvent.change(screen.getByLabelText('Роль клиента'), {
      target: { value: 'expenseClient' },
    });

    await waitFor(() => {
      const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toMatchObject({ clientRole: 'expenseClient' });
    });
  });

  it('opens a route-driven payment preview and closes it back through the callback', async () => {
    const onRoutePaymentChange = vi.fn();

    vi.mocked(apiService.getPayment).mockResolvedValue({
      id: 880,
      amount: 25_000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Completed',
      description: 'Платеж по банку',
      notes: 'Оплачен полностью',
      systemNotes: '',
      isPaid: true,
      paidAmount: 25_000,
      outstandingAmount: 0,
      hasPartialPayment: false,
      rescheduleCount: 0,
      createdAt: '2026-04-10T00:00:00Z',
      client: { id: 12, name: 'Тестовый клиент' },
      clientCase: { id: 44, title: 'Дело о взыскании' },
      paymentSource: { id: 3, name: 'Банк', createdAt: '2026-01-01T00:00:00Z' },
      contractId: 77,
      invoiceId: 551,
      account: '551',
    } as unknown as Payment);

    render(
      <Payments
        routePaymentId={880}
        onRoutePaymentChange={onRoutePaymentChange}
      />,
    );

    expect(await screen.findByTestId('payments-route-payment-preview')).toBeTruthy();
    await waitFor(() => {
      expect(apiService.getPayment).toHaveBeenCalledWith(880);
    });

    expect(screen.getByText('Платеж #880')).toBeTruthy();
    expect(screen.getByText('Оплачен полностью')).toBeTruthy();

    fireEvent.click(screen.getAllByRole('button', { name: 'Закрыть' })[1]);

    expect(onRoutePaymentChange).toHaveBeenCalledWith(null);
  });

  it('annuls a route-driven payment preview and closes the preview after refresh', async () => {
    const onRoutePaymentChange = vi.fn();
    const refresh = vi.fn().mockResolvedValue(undefined);

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      refresh,
    });
    vi.mocked(apiService.getPayment).mockResolvedValue({
      id: 881,
      amount: 15_000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      type: 'Expense',
      status: 'Overdue',
      description: 'Просроченный расходный платеж',
      notes: '',
      systemNotes: '',
      isPaid: false,
      paidAmount: 1_500,
      outstandingAmount: 13_500,
      hasPartialPayment: true,
      rescheduleCount: 0,
      createdAt: '2026-04-10T00:00:00Z',
      clientId: 12,
      expenseClientId: 34,
      client: { id: 12, name: 'Безняков' },
      expenseClient: { id: 34, name: 'Поставщик' },
      documentKind: 'Payment',
    } as unknown as Payment);
    vi.mocked(apiService.updatePayment).mockResolvedValue({ id: 881, status: 'Cancelled' } as Payment);

    render(
      <Payments
        routePaymentId={881}
        onRoutePaymentChange={onRoutePaymentChange}
      />,
    );

    expect(await screen.findByTestId('payments-route-payment-preview')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Аннулировать' }));

    await waitFor(() => {
      expect(apiService.updatePayment).toHaveBeenCalledWith(
        881,
        expect.objectContaining({
          status: 'Cancelled',
          isPaid: false,
          paidAmount: 0,
          paidDate: undefined,
          lastPaymentDate: undefined,
          type: 'Expense',
          expenseClientId: 34,
        }),
      );
    });
    expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({ confirmLabel: 'Аннулировать' }));
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(onRoutePaymentChange).toHaveBeenCalledWith(null);
  });

  it('deletes a route-driven payment preview only after confirmation succeeds', async () => {
    const onRoutePaymentChange = vi.fn();
    const refresh = vi.fn().mockResolvedValue(undefined);

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      refresh,
    });
    vi.mocked(apiService.getPayment).mockResolvedValue({
      id: 882,
      amount: 15_000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      type: 'Expense',
      status: 'Overdue',
      description: 'Просроченный расходный платеж',
      notes: '',
      systemNotes: '',
      isPaid: false,
      paidAmount: 0,
      outstandingAmount: 15_000,
      hasPartialPayment: false,
      rescheduleCount: 0,
      createdAt: '2026-04-10T00:00:00Z',
      documentKind: 'Payment',
    } as unknown as Payment);
    vi.mocked(apiService.deletePayment).mockResolvedValue(undefined);

    render(
      <Payments
        routePaymentId={882}
        onRoutePaymentChange={onRoutePaymentChange}
      />,
    );

    expect(await screen.findByTestId('payments-route-payment-preview')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Удалить' }));

    await waitFor(() => {
      expect(apiService.deletePayment).toHaveBeenCalledWith(882);
    });
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(onRoutePaymentChange).toHaveBeenCalledWith(null);
  });
});
