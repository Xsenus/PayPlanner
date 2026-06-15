import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { Payment } from '../../types';
import { Payments } from './Payments';

const { confirmMock, rolePermissionsMock, usePaymentsPagedMock } = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  rolePermissionsMock: vi.fn(),
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
    getTableWidth: (extraWidth = 0, minimumWidth = 0) =>
      Math.max(
        minimumWidth,
        orderedKeys.reduce(
          (sum, key) => sum + (columns.find((column) => column.key === key)?.defaultWidth ?? 180),
          extraWidth,
        ),
      ),
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
  useRolePermissions: () => rolePermissionsMock(),
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

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Europe/Samara',
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
    onStatusChange?: (payment: Payment) => void;
    onAcceptAdvance?: (payment: Payment) => void;
    onIgnoreAdvance?: (payment: Payment) => void;
    onAllocateAdvance?: (payment: Payment) => void;
    onRefundAdvance?: (payment: Payment) => void;
  }) => (
    <div data-testid="payments-table">
      {props.payments.map((payment) => (
        <div key={payment.id}>
          <button
            type="button"
            onClick={() => props.onEditPayment?.(payment)}
          >
            {`edit-payment-${payment.id}`}
          </button>
          <button
            type="button"
            onClick={() => props.onStatusChange?.(payment)}
          >
            {`status-payment-${payment.id}`}
          </button>
          <button
            type="button"
            onClick={() => props.onAcceptAdvance?.(payment)}
          >
            {`accept-advance-${payment.id}`}
          </button>
          <button
            type="button"
            onClick={() => props.onIgnoreAdvance?.(payment)}
          >
            {`ignore-advance-${payment.id}`}
          </button>
          <button
            type="button"
            onClick={() => props.onAllocateAdvance?.(payment)}
          >
            {`allocate-advance-${payment.id}`}
          </button>
          <button
            type="button"
            onClick={() => props.onRefundAdvance?.(payment)}
          >
            {`refund-advance-${payment.id}`}
          </button>
        </div>
      ))}
    </div>
  ),
}));

vi.mock('../ui/StatusChangeModal', () => ({
  StatusChangeModal: (props: {
    open: boolean;
    onChange: (value: Payment['status']) => void;
    onConfirm: () => void;
  }) => (
    props.open ? (
      <div data-testid="status-change-dialog">
        <button type="button" onClick={() => props.onChange('Completed')}>
          select-completed
        </button>
        <button type="button" onClick={props.onConfirm}>
          confirm-status
        </button>
      </div>
    ) : null
  ),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getPayment: vi.fn(),
    getPaymentsV2: vi.fn(),
    createPayment: vi.fn(),
    updatePayment: vi.fn(),
    updateInvoiceManagedPayment: vi.fn(),
    updatePaymentStatus: vi.fn(),
    deletePayment: vi.fn(),
    acceptPaymentAdvance: vi.fn(),
    ignorePaymentAdvance: vi.fn(),
    refundPaymentAdvance: vi.fn(),
    getPaymentAdvanceDiagnostics: vi.fn(),
    exportPaymentsCsv: vi.fn(),
    exportPaymentsExcel: vi.fn(),
    getClientBalance: vi.fn(),
    getClientBalanceTransactions: vi.fn(),
    applyClientBalance: vi.fn(),
    getLegalEntityBalance: vi.fn(),
    getLegalEntityBalanceTransactions: vi.fn(),
    applyLegalEntityBalance: vi.fn(),
    getPartialPaymentRemainderCandidates: vi.fn(),
  },
}));

import { apiService } from '../../services/api';

describe('Payments integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    confirmMock.mockResolvedValue(true);
    rolePermissionsMock.mockReset();
    rolePermissionsMock.mockReturnValue({
      payments: {
        canView: true,
        canCreate: true,
        canEdit: true,
        canDelete: true,
        canExport: true,
      },
    });
    vi.mocked(apiService.getPayment).mockReset();
    vi.mocked(apiService.getPaymentsV2).mockReset();
    vi.mocked(apiService.createPayment).mockReset();
    vi.mocked(apiService.updatePayment).mockReset();
    vi.mocked(apiService.updateInvoiceManagedPayment).mockReset();
    vi.mocked(apiService.updatePaymentStatus).mockReset();
    vi.mocked(apiService.deletePayment).mockReset();
    vi.mocked(apiService.acceptPaymentAdvance).mockReset();
    vi.mocked(apiService.ignorePaymentAdvance).mockReset();
    vi.mocked(apiService.refundPaymentAdvance).mockReset();
    vi.mocked(apiService.getPaymentAdvanceDiagnostics).mockReset();
    vi.mocked(apiService.exportPaymentsCsv).mockReset();
    vi.mocked(apiService.exportPaymentsExcel).mockReset();
    vi.mocked(apiService.getClientBalance).mockReset();
    vi.mocked(apiService.getClientBalanceTransactions).mockReset();
    vi.mocked(apiService.applyClientBalance).mockReset();
    vi.mocked(apiService.getLegalEntityBalance).mockReset();
    vi.mocked(apiService.getLegalEntityBalanceTransactions).mockReset();
    vi.mocked(apiService.applyLegalEntityBalance).mockReset();
    vi.mocked(apiService.getPartialPaymentRemainderCandidates).mockReset();
    vi.mocked(apiService.exportPaymentsCsv).mockResolvedValue({
      blob: new Blob(['csv'], { type: 'text/csv' }),
      fileName: 'payments-export.csv',
    });
    vi.mocked(apiService.exportPaymentsExcel).mockResolvedValue({
      blob: new Blob(['xls'], { type: 'application/vnd.ms-excel' }),
      fileName: 'payments-export.xls',
    });
    vi.mocked(apiService.getClientBalance).mockResolvedValue({ clientId: 1, balance: 0 });
    vi.mocked(apiService.getClientBalanceTransactions).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntityBalance).mockResolvedValue({ clientId: null, legalEntityId: 1, balance: 0 });
    vi.mocked(apiService.getLegalEntityBalanceTransactions).mockResolvedValue([]);
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 100,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    });
    vi.mocked(apiService.acceptPaymentAdvance).mockResolvedValue({ id: 1, advanceReviewStatus: 'AcceptedAsAdvance' } as Payment);
    vi.mocked(apiService.ignorePaymentAdvance).mockResolvedValue({ id: 1, advanceReviewStatus: 'Ignored' } as Payment);
    vi.mocked(apiService.refundPaymentAdvance).mockResolvedValue({ id: 1, advanceReviewStatus: 'Refunded' } as Payment);
    vi.mocked(apiService.getPaymentAdvanceDiagnostics).mockResolvedValue({
      items: [],
      total: 0,
      totalAmount: 0,
      limit: 20,
      query: null,
      from: null,
      to: null,
      amount: null,
      amountTolerance: 0.01,
    });
    vi.mocked(apiService.applyClientBalance).mockResolvedValue({ appliedAmount: 0, balance: 0 });
    vi.mocked(apiService.applyLegalEntityBalance).mockResolvedValue({ appliedAmount: 0, balance: 0 });
    vi.mocked(apiService.getPartialPaymentRemainderCandidates).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 100,
      totalOriginalAmount: 0,
      totalPaidAmount: 0,
      totalRemainderAmount: 0,
      includeFuture: false,
      checkedAt: '2026-05-08T00:00:00Z',
    });
    usePaymentsPagedMock.mockReset();
    usePaymentsPagedMock.mockReturnValue(paymentsPagedResult);
    paymentsPagedResult.payments = [];
    paymentsPagedResult.total = 0;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('uses the profile time zone for the default payments period near midnight', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-04T22:30:00.000Z'));

    render(<Payments />);
    vi.useRealTimers();

    const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
    expect(lastCall).toMatchObject({
      from: '2026-06-01',
      to: '2026-06-05',
    });
  });

  it('exports payments with the current filtered query', async () => {
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:payments-export');
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);

    render(<Payments />);
    const currentQuery = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;

    fireEvent.click(screen.getByRole('button', { name: 'Выгрузить платежи в CSV' }));

    await waitFor(() => {
      expect(apiService.exportPaymentsCsv).toHaveBeenCalledWith(expect.objectContaining({
        from: currentQuery.from,
        to: currentQuery.to,
        sortBy: 'date',
        sortDir: 'desc',
      }));
    });
    expect(createObjectUrl).toHaveBeenCalled();
    expect(anchorClick).toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Выгрузить платежи в Excel' }));

    await waitFor(() => {
      expect(apiService.exportPaymentsExcel).toHaveBeenCalledWith(expect.objectContaining({
        from: currentQuery.from,
        to: currentQuery.to,
        sortBy: 'date',
        sortDir: 'desc',
      }));
    });

    createObjectUrl.mockRestore();
    revokeObjectUrl.mockRestore();
    anchorClick.mockRestore();
  });

  it('exposes payment toolbar actions with accessible names', () => {
    render(<Payments />);

    const toolbar = screen.getByRole('toolbar', { name: 'Действия с платежами' });

    expect(within(toolbar).getByRole('button', { name: 'Настроить таблицу' })).toBeTruthy();
    expect(within(toolbar).getByRole('button', { name: 'Обновить' })).toBeTruthy();
    expect(within(toolbar).getByRole('button', { name: 'Выгрузить платежи в CSV' })).toBeTruthy();
    expect(within(toolbar).getByRole('button', { name: 'Выгрузить платежи в Excel' })).toBeTruthy();
    expect(within(toolbar).getByRole('button', { name: 'Частичные без остатка' })).toBeTruthy();
  });

  it('shows advance monitoring summary and switches to required decisions', async () => {
    const requiredPayment = {
      id: 801,
      amount: 7_100,
      paidAmount: 7_100,
      date: '2026-03-02',
      plannedDate: '2026-03-02',
      type: 'Income',
      status: 'Completed',
      description: 'Поступление без счета',
      notes: '',
      systemNotes: '',
      isPaid: true,
      outstandingAmount: 0,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Payment',
      advanceReviewStatus: 'RequiresDecision',
    } as unknown as Payment;
    const acceptedPayment = {
      ...requiredPayment,
      id: 802,
      amount: 3_000,
      paidAmount: 3_000,
      description: 'Принятый аванс',
      advanceReviewStatus: 'AcceptedAsAdvance',
    } as unknown as Payment;

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      payments: [requiredPayment, acceptedPayment],
      total: 2,
    });

    render(<Payments />);

    const monitoring = screen.getByRole('region', { name: 'Мониторинг нераспределенных авансов' });
    expect(within(monitoring).getByText('Нераспределенные авансы')).toBeTruthy();
    expect(within(monitoring).getByText(/1 на 7 100/)).toBeTruthy();
    expect(within(monitoring).getByText(/1 на 3 000/)).toBeTruthy();

    fireEvent.click(within(monitoring).getByRole('button', { name: /На разбор/ }));

    await waitFor(() => {
      const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall.advanceReviewStatus).toBe('RequiresDecision');
      expect(lastCall.status).toBeUndefined();
      expect(lastCall.page).toBe(1);
    });
  });

  it('checks a suspected unallocated advance from the monitoring panel', async () => {
    vi.mocked(apiService.getPaymentAdvanceDiagnostics).mockResolvedValueOnce({
      total: 1,
      totalAmount: 7_100,
      limit: 20,
      query: 'Винокуров',
      from: '2026-03-02',
      to: '2026-03-02',
      amount: 7_100,
      amountTolerance: 0.01,
      items: [
        {
          id: 901,
          date: '2026-03-02',
          amount: 7_100,
          paidAmount: 7_100,
          advanceAmount: 7_100,
          description: 'Поступление без счета',
          clientId: 15,
          legalEntityId: null,
          counterpartyName: 'Винокуров Валерий Витальевич',
          clientCaseId: 44,
          contractId: null,
          status: 'Completed',
          advanceReviewStatus: 'RequiresDecision',
          type: 'Income',
          isUnallocatedAdvanceCandidate: true,
          hasInvoiceLink: false,
          hasActLink: false,
          hasAccountNumber: false,
          hasUnallocatedAdvanceBalanceTransaction: false,
          unallocatedAdvanceBalanceAmount: 0,
        },
      ],
    });

    render(<Payments />);

    const monitoring = screen.getByRole('region', { name: 'Мониторинг нераспределенных авансов' });
    fireEvent.change(within(monitoring).getByLabelText('Клиент, описание или номер для диагностики аванса'), {
      target: { value: 'Винокуров' },
    });
    fireEvent.change(within(monitoring).getByLabelText('Дата платежа для диагностики аванса'), {
      target: { value: '2026-03-02' },
    });
    fireEvent.change(within(monitoring).getByLabelText('Сумма платежа для диагностики аванса'), {
      target: { value: '7100' },
    });

    fireEvent.click(within(monitoring).getByRole('button', { name: /Проверить платеж/ }));

    await waitFor(() => {
      expect(apiService.getPaymentAdvanceDiagnostics).toHaveBeenCalledWith({
        q: 'Винокуров',
        from: '2026-03-02',
        to: '2026-03-02',
        amount: 7100,
        amountTolerance: 0.01,
        limit: 20,
      });
    });

    expect(await within(monitoring).findByText(/Винокуров Валерий Витальевич/)).toBeTruthy();
    expect(within(monitoring).getByText('Кандидат в аванс')).toBeTruthy();
    expect(within(monitoring).getByText('Нет проводки баланса')).toBeTruthy();
  });

  it('runs advance review actions from the monitoring list and refreshes payments', async () => {
    const refresh = vi.fn().mockResolvedValue(undefined);
    const requiredPayment = {
      id: 821,
      amount: 7_100,
      paidAmount: 7_100,
      date: '2026-03-02',
      plannedDate: '2026-03-02',
      type: 'Income',
      status: 'Completed',
      description: 'Поступление без счета',
      notes: '',
      systemNotes: '',
      isPaid: true,
      outstandingAmount: 0,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Payment',
      advanceReviewStatus: 'RequiresDecision',
    } as unknown as Payment;
    const ignoredPayment = {
      ...requiredPayment,
      id: 822,
      amount: 2_500,
      paidAmount: 2_500,
      description: 'Служебный платеж',
    } as unknown as Payment;
    const acceptedPayment = {
      ...requiredPayment,
      id: 823,
      amount: 3_000,
      paidAmount: 3_000,
      description: 'Принятый аванс',
      advanceReviewStatus: 'AcceptedAsAdvance',
    } as unknown as Payment;

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      payments: [requiredPayment, ignoredPayment, acceptedPayment],
      total: 3,
      refresh,
    });
    vi.mocked(apiService.acceptPaymentAdvance).mockResolvedValueOnce({
      ...requiredPayment,
      advanceReviewStatus: 'AcceptedAsAdvance',
    } as Payment);
    vi.mocked(apiService.ignorePaymentAdvance).mockResolvedValueOnce({
      ...ignoredPayment,
      advanceReviewStatus: 'Ignored',
    } as Payment);
    vi.mocked(apiService.refundPaymentAdvance).mockResolvedValueOnce({
      ...acceptedPayment,
      advanceReviewStatus: 'Refunded',
    } as Payment);

    render(<Payments />);

    fireEvent.click(screen.getByRole('button', { name: 'accept-advance-821' }));

    await waitFor(() => {
      expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Принять платеж как аванс',
        confirmLabel: 'Принять как аванс',
      }));
    });
    await waitFor(() => {
      expect(apiService.acceptPaymentAdvance).toHaveBeenCalledWith(
        821,
        'Принято как аванс из журнала платежей',
      );
    });
    expect(refresh).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'ignore-advance-822' }));

    await waitFor(() => {
      expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Исключить из контроля авансов',
        confirmLabel: 'Игнорировать',
      }));
    });
    await waitFor(() => {
      expect(apiService.ignorePaymentAdvance).toHaveBeenCalledWith(
        822,
        'Исключено из контроля авансов из журнала платежей',
      );
    });
    expect(refresh).toHaveBeenCalledTimes(2);

    fireEvent.click(screen.getByRole('button', { name: 'refund-advance-823' }));

    await waitFor(() => {
      expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Вернуть аванс клиенту',
        confirmLabel: 'Вернуть аванс',
      }));
    });
    await waitFor(() => {
      expect(apiService.refundPaymentAdvance).toHaveBeenCalledWith(
        823,
        'Возврат аванса из журнала платежей',
      );
    });
    expect(refresh).toHaveBeenCalledTimes(3);
  });

  it('blocks advance review actions when the user cannot edit payments', async () => {
    rolePermissionsMock.mockReturnValue({
      payments: {
        canView: true,
        canCreate: false,
        canEdit: false,
        canDelete: false,
        canExport: false,
      },
    });

    const requiredPayment = {
      id: 831,
      amount: 7_100,
      paidAmount: 7_100,
      date: '2026-03-02',
      plannedDate: '2026-03-02',
      type: 'Income',
      status: 'Completed',
      description: 'Advance requiring review',
      notes: '',
      systemNotes: '',
      isPaid: true,
      outstandingAmount: 0,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Payment',
      advanceReviewStatus: 'RequiresDecision',
    } as unknown as Payment;
    const acceptedPayment = {
      ...requiredPayment,
      id: 832,
      advanceReviewStatus: 'AcceptedAsAdvance',
      clientId: 12,
      client: { id: 12, name: 'Client with advance' },
    } as unknown as Payment;

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      payments: [requiredPayment, acceptedPayment],
      total: 2,
    });

    render(<Payments />);

    fireEvent.click(screen.getByRole('button', { name: 'accept-advance-831' }));
    fireEvent.click(screen.getByRole('button', { name: 'ignore-advance-831' }));
    fireEvent.click(screen.getByRole('button', { name: 'allocate-advance-832' }));
    fireEvent.click(screen.getByRole('button', { name: 'refund-advance-832' }));

    await waitFor(() => {
      expect(confirmMock).not.toHaveBeenCalled();
      expect(apiService.acceptPaymentAdvance).not.toHaveBeenCalled();
      expect(apiService.ignorePaymentAdvance).not.toHaveBeenCalled();
      expect(apiService.refundPaymentAdvance).not.toHaveBeenCalled();
      expect(apiService.getClientBalance).not.toHaveBeenCalled();
      expect(apiService.applyClientBalance).not.toHaveBeenCalled();
    });
  });

  it('uses the profile time zone for the paid date when completing a payment', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-04T22:30:00.000Z'));

    const refresh = vi.fn().mockResolvedValue(undefined);
    const payment = {
      id: 990,
      amount: 12_000,
      date: '2026-06-04',
      plannedDate: '2026-06-04',
      type: 'Income',
      status: 'Pending',
      description: 'Profile timezone payment',
      notes: '',
      systemNotes: '',
      isPaid: false,
      paidAmount: 0,
      outstandingAmount: 12_000,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Payment',
    } as unknown as Payment;

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      payments: [payment],
      total: 1,
      refresh,
    });
    vi.mocked(apiService.updatePaymentStatus).mockResolvedValue({ ...payment, status: 'Completed' } as Payment);

    render(<Payments />);

    fireEvent.click(screen.getByRole('button', { name: 'status-payment-990' }));
    fireEvent.click(screen.getByRole('button', { name: 'select-completed' }));
    fireEvent.click(screen.getByRole('button', { name: 'confirm-status' }));
    await Promise.resolve();
    await Promise.resolve();

    expect(apiService.updatePaymentStatus).toHaveBeenCalledWith(
      990,
      'Completed',
      '2026-06-05',
      null,
    );
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('allocates an accepted advance to an open client debt from the payments journal', async () => {
    const refresh = vi.fn().mockResolvedValue(undefined);
    const sourceAdvance = {
      id: 901,
      amount: 7_100,
      date: '2026-03-02',
      plannedDate: '2026-03-02',
      type: 'Income',
      status: 'Completed',
      description: 'Нераспределенный аванс',
      notes: '',
      systemNotes: '',
      isPaid: true,
      paidAmount: 7_100,
      outstandingAmount: 0,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Payment',
      clientId: 12,
      client: { id: 12, name: 'Винокуров Валерий' },
      advanceReviewStatus: 'AcceptedAsAdvance',
    } as unknown as Payment;
    const targetDebt = {
      id: 902,
      amount: 10_000,
      date: '2026-03-10',
      plannedDate: '2026-03-10',
      type: 'Income',
      status: 'Overdue',
      description: 'Долг клиента',
      notes: '',
      systemNotes: '',
      isPaid: false,
      paidAmount: 2_000,
      outstandingAmount: 8_000,
      hasPartialPayment: true,
      rescheduleCount: 0,
      documentKind: 'Payment',
      clientId: 12,
      client: { id: 12, name: 'Винокуров Валерий' },
    } as unknown as Payment;

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      payments: [sourceAdvance],
      total: 1,
      refresh,
    });
    vi.mocked(apiService.getClientBalance).mockResolvedValueOnce({ clientId: 12, balance: 7_100 });
    vi.mocked(apiService.getClientBalanceTransactions).mockResolvedValueOnce([
      {
        id: 1,
        clientId: 12,
        paymentId: 901,
        amount: 7_100,
        type: 'UnallocatedAdvance',
        direction: 'Credit',
        description: 'Аванс по платежу #901',
        externalKey: 'payment-unallocated-advance:901',
        createdAt: '2026-03-02T00:00:00Z',
      },
    ]);
    vi.mocked(apiService.getPaymentsV2).mockResolvedValueOnce({
      items: [sourceAdvance, targetDebt],
      total: 2,
      page: 1,
      pageSize: 100,
      summary: {
        income: 17_100,
        expense: 0,
        balance: 17_100,
        incomeCount: 2,
        expenseCount: 0,
        total: 17_100,
      },
    });
    vi.mocked(apiService.applyClientBalance).mockResolvedValueOnce({ appliedAmount: 7_100, balance: 0 });

    render(<Payments />);

    fireEvent.click(screen.getByRole('button', { name: 'allocate-advance-901' }));

    expect(await screen.findByRole('dialog', { name: 'Распределить аванс' })).toBeTruthy();
    expect(apiService.getClientBalance).toHaveBeenCalledWith(12);
    expect(apiService.getClientBalanceTransactions).toHaveBeenCalledWith(12);
    expect(apiService.getPaymentsV2).toHaveBeenCalledWith(expect.objectContaining({
      clientId: 12,
      type: 'Income',
      page: 1,
      pageSize: 100,
    }));

    fireEvent.click(screen.getByRole('button', { name: 'Зачесть аванс' }));

    await waitFor(() => {
      expect(apiService.applyClientBalance).toHaveBeenCalledWith(12, {
        paymentId: 902,
        amount: 7_100,
        description: 'Распределение аванса по платежу #901',
        sourcePaymentId: 901,
      });
    });
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('allocates an accepted legal entity advance through the legal entity balance api', async () => {
    const refresh = vi.fn().mockResolvedValue(undefined);
    const sourceAdvance = {
      id: 911,
      amount: 9_000,
      date: '2026-04-01',
      plannedDate: '2026-04-01',
      type: 'Income',
      status: 'Completed',
      description: 'Аванс юридического лица',
      notes: '',
      systemNotes: '',
      isPaid: true,
      paidAmount: 9_000,
      outstandingAmount: 0,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Payment',
      legalEntityId: 77,
      legalEntityName: 'ООО Вектор',
      advanceReviewStatus: 'AcceptedAsAdvance',
    } as unknown as Payment;
    const targetDebt = {
      id: 912,
      amount: 12_000,
      date: '2026-04-15',
      plannedDate: '2026-04-15',
      type: 'Income',
      status: 'Pending',
      description: 'Долг юридического лица',
      notes: '',
      systemNotes: '',
      isPaid: false,
      paidAmount: 0,
      outstandingAmount: 12_000,
      hasPartialPayment: false,
      rescheduleCount: 0,
      documentKind: 'Payment',
      legalEntityId: 77,
      legalEntityName: 'ООО Вектор',
    } as unknown as Payment;

    usePaymentsPagedMock.mockReturnValue({
      ...paymentsPagedResult,
      payments: [sourceAdvance],
      total: 1,
      refresh,
    });
    vi.mocked(apiService.getLegalEntityBalance).mockResolvedValueOnce({ clientId: null, legalEntityId: 77, balance: 9_000 });
    vi.mocked(apiService.getLegalEntityBalanceTransactions).mockResolvedValueOnce([
      {
        id: 2,
        clientId: null,
        legalEntityId: 77,
        paymentId: 911,
        amount: 9_000,
        type: 'UnallocatedAdvance',
        direction: 'Credit',
        description: 'Аванс по платежу #911',
        externalKey: 'payment-unallocated-advance:911',
        createdAt: '2026-04-01T00:00:00Z',
      },
    ]);
    vi.mocked(apiService.getPaymentsV2).mockResolvedValueOnce({
      items: [sourceAdvance, targetDebt],
      total: 2,
      page: 1,
      pageSize: 100,
      summary: {
        income: 21_000,
        expense: 0,
        balance: 21_000,
        incomeCount: 2,
        expenseCount: 0,
        total: 21_000,
      },
    });
    vi.mocked(apiService.applyLegalEntityBalance).mockResolvedValueOnce({ appliedAmount: 9_000, balance: 0 });

    render(<Payments />);

    fireEvent.click(screen.getByRole('button', { name: 'allocate-advance-911' }));

    expect(await screen.findByRole('dialog', { name: 'Распределить аванс' })).toBeTruthy();
    expect(apiService.getLegalEntityBalance).toHaveBeenCalledWith(77);
    expect(apiService.getLegalEntityBalanceTransactions).toHaveBeenCalledWith(77);
    expect(apiService.getPaymentsV2).toHaveBeenCalledWith(expect.objectContaining({
      legalEntityId: 77,
      type: 'Income',
      page: 1,
      pageSize: 100,
    }));

    fireEvent.click(screen.getByRole('button', { name: 'Зачесть аванс' }));

    await waitFor(() => {
      expect(apiService.applyLegalEntityBalance).toHaveBeenCalledWith(77, {
        paymentId: 912,
        amount: 9_000,
        description: 'Распределение аванса по платежу #911',
        sourcePaymentId: 911,
      });
    });
    expect(apiService.applyClientBalance).not.toHaveBeenCalled();
    expect(refresh).toHaveBeenCalledTimes(1);
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

    fireEvent.change(screen.getByLabelText('Роль клиента'), {
      target: { value: 'expenseClient' },
    });

    await waitFor(() => {
      const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toMatchObject({ clientRole: 'expenseClient' });
    });
  });

  it('preloads partial remainder candidates and highlights the toolbar action', async () => {
    vi.mocked(apiService.getPartialPaymentRemainderCandidates).mockResolvedValueOnce({
      items: [],
      total: 3,
      page: 1,
      pageSize: 100,
      totalOriginalAmount: 80_000,
      totalPaidAmount: 20_000,
      totalRemainderAmount: 60_000,
      includeFuture: false,
      checkedAt: '2026-05-08T00:00:00Z',
    });

    render(<Payments />);

    await waitFor(() => {
      expect(apiService.getPartialPaymentRemainderCandidates).toHaveBeenCalledWith(
        expect.objectContaining({
          includeFuture: false,
          page: 1,
          pageSize: 100,
        }),
      );
    });

    const action = screen.getByRole('button', { name: 'Частичные без остатка' });
    await waitFor(() => {
      expect(action.className).toContain('border-amber');
      expect(action.textContent).toContain('3');
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
