import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { Accounts } from './Accounts';
import type { Act, AccountRegistryRow, AccountsSummary, Client, Invoice, Payment } from '../../types';
import { clearInvoiceEditorDraft, peekInvoiceEditorDraft } from '../../utils/invoiceEditorBridge';

const {
  createPaymentMock,
  getActResponsiblesMock,
  getAccountsRegistryResultMock,
  getClientsMock,
  getInvoiceMock,
  invoiceModalPropsSpy,
  paymentModalPropsSpy,
  refreshAccountsMock,
  toastErrorMock,
  updateInvoiceManagedPaymentMock,
  updatePaymentMock,
} = vi.hoisted(() => ({
  createPaymentMock: vi.fn(),
  getActResponsiblesMock: vi.fn(),
  getAccountsRegistryResultMock: vi.fn(),
  getClientsMock: vi.fn(),
  getInvoiceMock: vi.fn(),
  invoiceModalPropsSpy: vi.fn(),
  paymentModalPropsSpy: vi.fn(),
  refreshAccountsMock: vi.fn(),
  toastErrorMock: vi.fn(),
  updateInvoiceManagedPaymentMock: vi.fn(),
  updatePaymentMock: vi.fn(),
}));

function createPermissions() {
  return {
    clients: { canView: false, canCreate: false, canEdit: false, canDelete: false },
    accounts: { canView: true, canCreate: true, canEdit: true, canDelete: false },
    acts: { canView: false, canCreate: false, canEdit: false, canDelete: false },
    contracts: { canView: false, canCreate: false, canEdit: false, canDelete: false },
  };
}

function createInvoice(overrides: Partial<Invoice> = {}): Invoice {
  return {
    id: 528,
    invoiceEntityId: 24,
    number: '24',
    date: '2026-04-02',
    dueDate: '2026-04-03',
    amount: 15_000,
    type: 'Income',
    status: 'Completed',
    isPaid: true,
    clientId: 55,
    clientName: 'Test Client',
    clientCaseId: 701,
    responsibleName: 'Test Responsible',
    createdAt: '2026-04-02T10:00:00Z',
    ...overrides,
  } as Invoice;
}

function createRepresentativePayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 528,
    invoiceId: 24,
    date: '2026-04-02',
    plannedDate: '2026-04-02',
    paidDate: '2026-04-03',
    amount: 10_000,
    type: 'Income',
    status: 'Completed',
    isPaid: true,
    description: 'Representative payment for invoice 24',
    createdAt: '2026-04-02T10:05:00Z',
    updatedAt: null,
    documentKind: 'Payment',
    account: '24',
    accountDate: '2026-04-02',
    ...overrides,
  } as Payment;
}

function createSiblingPayment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 529,
    invoiceId: 24,
    date: '2026-04-03',
    plannedDate: '2026-04-03',
    paidDate: '2026-04-04',
    amount: 5_000,
    type: 'Income',
    status: 'Completed',
    isPaid: true,
    description: 'Sibling payment for invoice 24',
    createdAt: '2026-04-03T12:00:00Z',
    updatedAt: null,
    documentKind: 'Payment',
    account: '24',
    accountDate: '2026-04-02',
    ...overrides,
  } as Payment;
}

function createLegacySiblingPayment(overrides: Partial<Payment> = {}): Payment {
  return createSiblingPayment({
    id: 530,
    invoiceId: undefined,
    description: 'Legacy sibling payment for invoice 24',
    ...overrides,
  });
}

function createAccountRow(options?: {
  invoice?: Invoice;
  payments?: Payment[];
  status?: AccountRegistryRow['status'];
}): AccountRegistryRow {
  const payments = options?.payments ?? [createRepresentativePayment(), createSiblingPayment()];
  return {
    groupKey: 'invoice-24',
    accountNumber: '24',
    invoice: options?.invoice ?? createInvoice(),
    payments,
    acts: [],
    totalAmount: payments.reduce((sum, payment) => sum + (payment.amount ?? 0), 0),
    dueDate: '2026-04-03',
    paidDate: '2026-04-03',
    status: options?.status ?? 'paid',
    type: 'Income',
    isOverdue: false,
  };
}

function createSummary(): AccountsSummary {
  return {
    total: { amount: 15_000, count: 1 },
    pending: { amount: 0, count: 0 },
    paid: { amount: 15_000, count: 1 },
    overdue: { amount: 0, count: 0 },
    cancelled: { amount: 0, count: 0 },
  };
}

function createClient(): Client {
  return {
    id: 55,
    name: 'Test Client',
    email: '',
    phone: '',
    company: '',
    address: '',
    notes: '',
    createdAt: '2026-03-01T00:00:00Z',
    isActive: true,
  };
}

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
  useRolePermissions: () => createPermissions(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useDebouncedValue', () => ({
  useDebouncedValue: (value: string) => value,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: toastErrorMock,
    info: vi.fn(),
    warning: vi.fn(),
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

vi.mock('../../hooks/useAccountsRegistry', () => ({
  useAccountsRegistry: () => getAccountsRegistryResultMock(),
}));

vi.mock('../../hooks/useTableSelection', () => ({
  useTableSelection: () => ({
    selectedIds: [],
    selectedIdSet: new Set<number>(),
    selectedCount: 0,
    allVisibleSelected: false,
    someVisibleSelected: false,
    isSelected: () => false,
    clearSelection: vi.fn(),
    selectOnly: vi.fn(),
    toggleSelection: vi.fn(),
    toggleAllVisible: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    createPayment: createPaymentMock,
    getInvoice: getInvoiceMock,
    getClients: getClientsMock,
    getActResponsibles: getActResponsiblesMock,
    updateInvoiceManagedPayment: updateInvoiceManagedPaymentMock,
    updatePayment: updatePaymentMock,
  },
}));

vi.mock('../../utils/invoiceEditorBridge', () => ({
  clearInvoiceEditorDraft: vi.fn(),
  peekInvoiceEditorDraft: vi.fn(() => null),
}));

vi.mock('./InvoiceModal', () => ({
  InvoiceModal: (props: {
    open: boolean;
    relatedPayments?: Payment[];
    paymentsLoading?: boolean;
    onBack?: () => void;
    onClose?: () => void;
    onEditRelatedPayment?: (payment: Payment) => void;
  }) => {
    invoiceModalPropsSpy(props);
    if (!props.open) {
      return null;
    }

    return (
      <div data-testid="invoice-modal">
        <div>{`payments-loading:${String(props.paymentsLoading)}`}</div>
        <button type="button" onClick={props.onBack}>invoice-back</button>
        <button type="button" onClick={props.onClose}>invoice-close</button>
        {(props.relatedPayments ?? []).map((payment) => (
          <div key={payment.id}>
            <div>{payment.description}</div>
            <button
              type="button"
              data-testid={`invoice-related-payment-link-${payment.id}`}
              onClick={() => props.onEditRelatedPayment?.(payment)}
            >
              {`open-related-payment-${payment.id}`}
            </button>
          </div>
        ))}
      </div>
    );
  },
}));

vi.mock('../Calendar/PaymentModal', () => ({
  PaymentModal: (props: {
    isOpen: boolean;
    onSubmit?: (payload: unknown) => Promise<unknown>;
    payment?: Payment;
    linkedInvoice?: {
      invoiceEntityId: number;
      invoiceRecordId?: number | null;
      number: string;
      date: string;
      clientId?: number | null;
      clientCaseId?: number | null;
      type?: 'Income' | 'Expense';
    } | null;
  }) => {
    paymentModalPropsSpy(props);
    if (!props.isOpen) {
      return null;
    }

    return (
      <div data-testid="payment-modal">
        <div>{`payment-id:${props.payment?.id ?? ''}`}</div>
        <div>{`payment-description:${props.payment?.description ?? ''}`}</div>
        <div>{`linked-invoice-id:${props.linkedInvoice?.invoiceEntityId ?? ''}`}</div>
        <div>{`linked-invoice-record-id:${props.linkedInvoice?.invoiceRecordId ?? ''}`}</div>
        <div>{`linked-invoice-number:${props.linkedInvoice?.number ?? ''}`}</div>
        <div>{`linked-invoice-date:${props.linkedInvoice?.date ?? ''}`}</div>
        <div>{`linked-client-id:${props.linkedInvoice?.clientId ?? ''}`}</div>
        <div>{`linked-case-id:${props.linkedInvoice?.clientCaseId ?? ''}`}</div>
        <div>{`linked-type:${props.linkedInvoice?.type ?? ''}`}</div>
        <button
          type="button"
          onClick={() => {
            const payment = props.payment;
            if (!payment || !props.onSubmit) {
              return;
            }

            const payload = {
              id: payment.id,
              date: payment.date,
              plannedDate: payment.plannedDate ?? payment.date,
              amount: payment.amount,
              paidAmount: payment.amount,
              lastPaymentDate: payment.paidDate ?? payment.date,
              type: payment.type,
              status: payment.status,
              description: payment.description ?? '',
              isPaid: payment.isPaid,
              paidDate: payment.paidDate ?? payment.date,
              notes: payment.notes ?? '',
              systemNotes: payment.systemNotes ?? '',
              rescheduleCount: payment.rescheduleCount ?? 0,
              clientId: payment.clientId ?? 55,
              clientCaseId: payment.clientCaseId ?? 701,
              invoiceId: payment.invoiceId ?? 24,
              account: payment.account ?? '24',
              accountDate: payment.accountDate ?? '2026-04-02',
            };

            void props.onSubmit(payload).catch(() => undefined);
          }}
        >
          submit-payment
        </button>
      </div>
    );
  },
}));

vi.mock('../Acts/ActModal', () => ({
  ActModal: () => null,
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

vi.mock('../DateRange/DatePeriodPicker', () => ({
  DatePeriodPicker: () => null,
}));

vi.mock('../ui/FormPromptModal', () => ({
  FormPromptModal: () => null,
}));

vi.mock('../ui/TableContextMenu', () => ({
  TableContextMenu: () => null,
}));

vi.mock('../ui/TableColumnSettings', () => ({
  TableColumnSettings: () => null,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => null,
}));

vi.mock('../ui/TableSelectCheckbox', () => ({
  TableSelectCheckbox: () => null,
}));

describe('Accounts integration', () => {
  const clearInvoiceEditorDraftMock = vi.mocked(clearInvoiceEditorDraft);
  const peekInvoiceEditorDraftMock = vi.mocked(peekInvoiceEditorDraft);

  beforeEach(() => {
    getActResponsiblesMock.mockReset();
    getAccountsRegistryResultMock.mockReset();
    getClientsMock.mockReset();
    getInvoiceMock.mockReset();
    invoiceModalPropsSpy.mockReset();
    paymentModalPropsSpy.mockReset();
    refreshAccountsMock.mockReset();
    toastErrorMock.mockReset();
    updateInvoiceManagedPaymentMock.mockReset();
    updatePaymentMock.mockReset();
    createPaymentMock.mockReset();
    clearInvoiceEditorDraftMock.mockReset();
    peekInvoiceEditorDraftMock.mockReset();
    window.localStorage.clear();

    getAccountsRegistryResultMock.mockReturnValue({
      items: [createAccountRow()],
      total: 1,
      summary: createSummary(),
      loading: false,
      refreshing: false,
      error: null,
      refresh: refreshAccountsMock,
    });
    getInvoiceMock.mockResolvedValue(createInvoice());
    getClientsMock.mockResolvedValue([createClient()]);
    getActResponsiblesMock.mockResolvedValue([]);
    updateInvoiceManagedPaymentMock.mockResolvedValue(createSiblingPayment());
    updatePaymentMock.mockResolvedValue(createSiblingPayment());
    createPaymentMock.mockResolvedValue(createSiblingPayment({ id: 600 }));
    clearInvoiceEditorDraftMock.mockImplementation(() => undefined);
    peekInvoiceEditorDraftMock.mockReturnValue(null);
  });

  it('shows a visible summary when a saved period filter hides part of the registry', () => {
    window.localStorage.setItem(
      'pp.accounts.7.period',
      JSON.stringify({ mode: 'custom', from: '2026-04-10', to: '2026-04-12' }),
    );

    render(<Accounts />);

    expect(screen.getByText('Применены фильтры:')).toBeTruthy();
    expect(screen.getByText('Период: 10.04.2026 — 12.04.2026')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Сбросить все' })).toBeTruthy();
  });

  it('resets the type filter back to all accounts', async () => {
    render(<Accounts />);

    fireEvent.click(screen.getByRole('button', { name: 'Расходные счета' }));

    expect(screen.getByText('Применены фильтры:')).toBeTruthy();
    expect(screen.getByText('Тип: Расходные счета')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Расходные счета' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Сбросить все' }));

    await waitFor(() => {
      expect(screen.queryByText('Применены фильтры:')).toBeNull();
    });
    expect(screen.getByRole('heading', { name: 'Все счета' })).toBeTruthy();
  });

  it('passes grouped invoice operations into the editor opened from the accounts registry', async () => {
    render(<Accounts routeInvoiceId={528} />);

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(528));

    expect(await screen.findByTestId('invoice-modal')).toBeTruthy();
    expect(await screen.findByText('Representative payment for invoice 24')).toBeTruthy();
    expect(screen.getByText('Sibling payment for invoice 24')).toBeTruthy();
    expect(screen.getByText('payments-loading:false')).toBeTruthy();
  });

  it('opens act creation as a dedicated page from the accounts registry', async () => {
    const onCreateActPage = vi.fn();

    render(<Accounts onCreateActPage={onCreateActPage} />);

    fireEvent.click(screen.getByRole('button', { name: 'Создать акт из счёта' }));

    expect(onCreateActPage).toHaveBeenCalledWith(expect.objectContaining({
      creationMode: 'Full',
      invoiceId: 528,
      invoiceNumber: '24',
      number: '24',
      amount: 15000,
      clientId: 55,
      status: 'Created',
    }), expect.objectContaining({
      invoiceId: 528,
      invoiceSeed: expect.objectContaining({ id: 528, number: '24' }),
    }));
  });

  it('opens an existing act as a dedicated page from the accounts registry', async () => {
    const linkedAct = {
      id: 901,
      number: 'ACT-901',
      date: '2026-04-05',
      amount: 15000,
      status: 'Created',
    } as Act;
    const onOpenActPage = vi.fn();

    getAccountsRegistryResultMock.mockReturnValue({
      items: [{
        ...createAccountRow(),
        acts: [linkedAct],
      }],
      total: 1,
      summary: createSummary(),
      loading: false,
      refreshing: false,
      error: null,
      refresh: refreshAccountsMock,
    });

    render(<Accounts onOpenActPage={onOpenActPage} />);

    fireEvent.click(screen.getByRole('button', { name: /Акт №ACT-901/i }));

    expect(onOpenActPage).toHaveBeenCalledWith(901, {
      invoiceId: 528,
    });
  });

  it('keeps grouped payments when the fetched invoice has another row id but the same invoice entity id', async () => {
    getInvoiceMock.mockResolvedValue(createInvoice({ id: 777 }));

    render(<Accounts routeInvoiceId={777} />);

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(777));

    expect(await screen.findByTestId('invoice-modal')).toBeTruthy();
    expect(await screen.findByText('Representative payment for invoice 24')).toBeTruthy();
    expect(screen.getByText('Sibling payment for invoice 24')).toBeTruthy();
  });

  it('passes legacy sibling payments from the grouped registry row into the editor', async () => {
    getAccountsRegistryResultMock.mockReturnValue({
      items: [createAccountRow({ payments: [createRepresentativePayment(), createLegacySiblingPayment()] })],
      total: 1,
      summary: createSummary(),
      loading: false,
      refreshing: false,
      error: null,
      refresh: refreshAccountsMock,
    });

    render(<Accounts routeInvoiceId={528} />);

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(528));

    expect(await screen.findByTestId('invoice-modal')).toBeTruthy();
    expect(await screen.findByText('Representative payment for invoice 24')).toBeTruthy();
    expect(screen.getByText('Legacy sibling payment for invoice 24')).toBeTruthy();
  });

  it('restores client context when leaving the routed invoice page via the back action', async () => {
    const onRouteInvoiceChange = vi.fn();
    const onRouteInvoicePageBack = vi.fn();

    render(
      <Accounts
        routeInvoiceId={528}
        onRouteInvoiceChange={onRouteInvoiceChange}
        onRouteInvoicePageBack={onRouteInvoicePageBack}
      />,
    );

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(528));

    fireEvent.click(await screen.findByRole('button', { name: 'invoice-back' }));

    await waitFor(() => expect(onRouteInvoicePageBack).toHaveBeenCalledTimes(1));
    expect(onRouteInvoiceChange).toHaveBeenCalledWith(null);
  });

  it('does not restore client context when the routed invoice page is simply closed', async () => {
    const onRouteInvoiceChange = vi.fn();
    const onRouteInvoicePageBack = vi.fn();

    render(
      <Accounts
        routeInvoiceId={528}
        onRouteInvoiceChange={onRouteInvoiceChange}
        onRouteInvoicePageBack={onRouteInvoicePageBack}
      />,
    );

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(528));

    fireEvent.click(await screen.findByRole('button', { name: 'invoice-close' }));

    await waitFor(() => expect(onRouteInvoiceChange).toHaveBeenCalledWith(null));
    expect(onRouteInvoicePageBack).not.toHaveBeenCalled();
  });

  it('clears a stale persisted invoice draft after a not found response and returns to the registry', async () => {
    const onRouteInvoiceChange = vi.fn();
    let persistedDraft: { mode: 'edit'; invoiceId: number } | null = {
      mode: 'edit',
      invoiceId: 999,
    };

    peekInvoiceEditorDraftMock.mockImplementation(() => persistedDraft);
    clearInvoiceEditorDraftMock.mockImplementation(() => {
      persistedDraft = null;
    });
    getInvoiceMock.mockRejectedValue(new Error('Not Found'));

    render(<Accounts onRouteInvoiceChange={onRouteInvoiceChange} />);

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(999));
    await waitFor(() => expect(clearInvoiceEditorDraftMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(onRouteInvoiceChange).toHaveBeenCalledWith(null));
    expect(await screen.findByRole('button', { name: '24' })).toBeTruthy();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(getInvoiceMock.mock.calls.length).toBeLessThanOrEqual(2);
    expect(persistedDraft).toBeNull();
  });

  it('passes the canonical linked invoice context into the payment modal opened from the accounts registry', async () => {
    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));
    fireEvent.click(await screen.findByText('Добавить платеж'));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();
    expect(screen.getByText('payment-id:')).toBeTruthy();
    expect(screen.getByText('linked-invoice-id:24')).toBeTruthy();
    expect(screen.getByText('linked-invoice-record-id:528')).toBeTruthy();
    expect(screen.getByText('linked-invoice-number:24')).toBeTruthy();
    expect(screen.getByText('linked-invoice-date:2026-04-02')).toBeTruthy();
    expect(screen.getByText('linked-client-id:55')).toBeTruthy();
    expect(screen.getByText('linked-case-id:701')).toBeTruthy();
    expect(screen.getByText('linked-type:Income')).toBeTruthy();
  });

  it('opens the payment editor when clicking a payment row in the expanded operations panel', async () => {
    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));
    fireEvent.click(await screen.findByText('Sibling payment for invoice 24'));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();
    expect(screen.getByText('payment-id:529')).toBeTruthy();
    expect(screen.getByText('payment-description:Sibling payment for invoice 24')).toBeTruthy();
  });

  it('opens the payment modal for invoice-managed payments in the expanded operations panel', async () => {
    getAccountsRegistryResultMock.mockReturnValue({
      items: [
        createAccountRow({
          payments: [
            createSiblingPayment({
              id: 531,
              description: 'Installment payment for invoice 24',
              systemNotes: '[pp:installment-payment] 1',
            }),
          ],
        }),
      ],
      total: 1,
      summary: createSummary(),
      loading: false,
      refreshing: false,
      error: null,
      refresh: refreshAccountsMock,
    });

    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));
    fireEvent.click(await screen.findByText('Installment payment for invoice 24'));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();
    expect(screen.getByText('payment-id:531')).toBeTruthy();
    expect(screen.getByText('linked-invoice-id:24')).toBeTruthy();
  });

  it('opens the payment modal from the actions column for invoice-managed payments in the expanded operations panel', async () => {
    getAccountsRegistryResultMock.mockReturnValue({
      items: [
        createAccountRow({
          payments: [
            createSiblingPayment({
              id: 532,
              description: 'Installment payment action for invoice 24',
              systemNotes: '[pp:installment-payment] 2',
            }),
          ],
        }),
      ],
      total: 1,
      summary: createSummary(),
      loading: false,
      refreshing: false,
      error: null,
      refresh: refreshAccountsMock,
    });

    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));

    const operationsCell = await screen.findByText('Installment payment action for invoice 24');
    const paymentRow = operationsCell.closest('tr');
    expect(paymentRow).toBeTruthy();

    fireEvent.click(within(paymentRow as HTMLTableRowElement).getByRole('button', { name: 'Редактировать' }));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();
    expect(screen.getByText('payment-id:532')).toBeTruthy();
  });

  it('opens the payment modal from the routed invoice page related payments list', async () => {
    render(<Accounts routeInvoiceId={528} />);

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(528));

    fireEvent.click(await screen.findByTestId('invoice-related-payment-link-528'));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();
    expect(screen.getByText('payment-id:528')).toBeTruthy();
    expect(screen.getByText('linked-invoice-id:24')).toBeTruthy();
  });

  it('saves invoice-managed payments through the dedicated invoice-managed endpoint', async () => {
    getAccountsRegistryResultMock.mockReturnValue({
      items: [
        createAccountRow({
          payments: [
            createSiblingPayment({
              id: 533,
              description: 'Invoice-managed payment save',
              systemNotes: '[pp:installment-payment] 3',
            }),
          ],
        }),
      ],
      total: 1,
      summary: createSummary(),
      loading: false,
      refreshing: false,
      error: null,
      refresh: refreshAccountsMock,
    });

    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));
    fireEvent.click(await screen.findByText('Invoice-managed payment save'));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'submit-payment' }));

    await waitFor(() => {
      expect(updateInvoiceManagedPaymentMock).toHaveBeenCalledWith(
        533,
        expect.objectContaining({
          description: 'Invoice-managed payment save',
          invoiceId: 24,
        }),
      );
    });
    expect(updatePaymentMock).not.toHaveBeenCalledWith(533, expect.anything());
  });

  it('shows an error toast when saving a regular payment from the expanded operations panel fails', async () => {
    updatePaymentMock.mockRejectedValueOnce(new Error('Конфликт сохранения'));

    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));
    fireEvent.click(await screen.findByText('Sibling payment for invoice 24'));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'submit-payment' }));

    await waitFor(() => {
      expect(updatePaymentMock).toHaveBeenCalledWith(
        529,
        expect.objectContaining({
          description: 'Sibling payment for invoice 24',
          invoiceId: 24,
        }),
      );
    });
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Конфликт сохранения', 'Платежи счета'));
    expect(refreshAccountsMock).not.toHaveBeenCalled();
  });

  it('does not show the invoice edit shortcut in the expanded operations panel', async () => {
    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));

    expect(await screen.findByText('Добавить платеж')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Редактировать счёт №24' })).toBeNull();
  });

  it('keeps centered status badges in the registry row and expanded payments table', async () => {
    getAccountsRegistryResultMock.mockReturnValue({
      items: [
        createAccountRow({
          status: 'overdue',
          payments: [
            createRepresentativePayment({
              id: 530,
              status: 'Pending',
              isPaid: false,
              paidDate: null,
              description: 'Pending payment for invoice 24',
            }),
          ],
        }),
      ],
      total: 1,
      summary: createSummary(),
      loading: false,
      refreshing: false,
      error: null,
      refresh: refreshAccountsMock,
    });

    render(<Accounts />);

    const expandButton = await screen.findByRole('button', { name: '24' });
    const registryStatusBadge = screen
      .getAllByText('Просрочен')
      .find((element) => element.tagName === 'SPAN');
    expect(registryStatusBadge).toBeTruthy();
    expect((registryStatusBadge as HTMLSpanElement).className).toContain('justify-center');
    expect((registryStatusBadge as HTMLSpanElement).className).toContain('text-center');
    expect((registryStatusBadge as HTMLSpanElement).className).toContain('whitespace-normal');

    fireEvent.click(expandButton);

    const operationsCell = await screen.findByText('Pending payment for invoice 24');
    const paymentRow = operationsCell.closest('tr');
    expect(paymentRow).toBeTruthy();

    const paymentStatusBadge = within(paymentRow as HTMLTableRowElement)
      .getAllByText('Не оплачен')
      .find((element) => element.tagName === 'SPAN');
    expect(paymentStatusBadge).toBeTruthy();
    expect((paymentStatusBadge as HTMLSpanElement).className).toContain('justify-center');
    expect((paymentStatusBadge as HTMLSpanElement).className).toContain('text-center');
    expect((paymentStatusBadge as HTMLSpanElement).className).toContain('whitespace-normal');
  });
});
