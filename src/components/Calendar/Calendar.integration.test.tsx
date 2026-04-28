import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';

import type { Payment } from '../../types';
import { Calendar } from './Calendar';

const paymentsResult = {
  payments: [] as Payment[],
  createPayment: vi.fn(),
  updatePayment: vi.fn(),
  deletePayment: vi.fn(),
  refresh: vi.fn(),
};

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

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 7, role: { id: 1 } },
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn().mockResolvedValue(true) }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => ({
    calendar: {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
      canViewAnalytics: true,
    },
  }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({
    t: () => undefined,
    formatMonth: () => 'Апрель 2026',
  }),
}));

vi.mock('../../hooks/usePayments', () => ({
  usePayments: () => paymentsResult,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getClientBalance: vi.fn().mockResolvedValue({ clientId: 1, balance: 0 }),
    applyClientBalance: vi.fn().mockResolvedValue({ appliedAmount: 0, balance: 0 }),
    updateInvoiceManagedPayment: vi.fn(),
  },
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

vi.mock('./CalendarGrid', () => ({
  CalendarGrid: (props: {
    payments: Payment[];
    onEditPayment?: (payment: Payment) => void;
  }) => (
    <div data-testid="calendar-grid">
      {props.payments.map((payment) => (
        <button
          key={payment.id}
          type="button"
          onClick={() => props.onEditPayment?.(payment)}
        >
          {`open-payment-${payment.id}`}
        </button>
      ))}
    </div>
  ),
}));

vi.mock('./PaymentModal', () => ({
  PaymentModal: (props: { isOpen: boolean; payment?: Payment | null }) =>
    props.isOpen ? <div data-testid="calendar-payment-modal">{props.payment?.id ?? 'new'}</div> : null,
}));

vi.mock('./PaymentsTable', () => ({
  PaymentsTable: () => null,
}));

vi.mock('../MonthRange/MonthRangePicker', () => ({
  MonthRangePicker: () => null,
}));

vi.mock('../Statistics/TwoTypeStats', () => ({
  TwoTypeStats: () => null,
}));

vi.mock('../ui/TableColumnSettings', () => ({
  TableColumnSettings: () => null,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => null,
}));

describe('Calendar integration', () => {
  beforeEach(() => {
    paymentsResult.payments = [];
    localStorage.removeItem('pp.calendar_view');
  });

  it('opens the existing payment modal for invoice-managed payments from the calendar grid', async () => {
    const invoiceManagedPayment = {
      id: 159,
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

    paymentsResult.payments = [invoiceManagedPayment];

    render(<Calendar />);

    fireEvent.click(await screen.findByRole('button', { name: 'open-payment-159' }));

    await waitFor(() => {
      expect(screen.getByTestId('calendar-payment-modal').textContent).toContain('159');
    });
  });
});
