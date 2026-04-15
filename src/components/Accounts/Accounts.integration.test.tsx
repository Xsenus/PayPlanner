import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { Accounts } from './Accounts';
import type { AccountRegistryRow, AccountsSummary, Client, Invoice, Payment } from '../../types';

const {
  getActResponsiblesMock,
  getAccountsRegistryResultMock,
  getClientsMock,
  getInvoiceMock,
  invoiceModalPropsSpy,
  paymentModalPropsSpy,
  refreshAccountsMock,
} = vi.hoisted(() => ({
  getActResponsiblesMock: vi.fn(),
  getAccountsRegistryResultMock: vi.fn(),
  getClientsMock: vi.fn(),
  getInvoiceMock: vi.fn(),
  invoiceModalPropsSpy: vi.fn(),
  paymentModalPropsSpy: vi.fn(),
  refreshAccountsMock: vi.fn(),
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

function createAccountRow(options?: { invoice?: Invoice; payments?: Payment[] }): AccountRegistryRow {
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
    status: 'paid',
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
    error: vi.fn(),
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
    getInvoice: getInvoiceMock,
    getClients: getClientsMock,
    getActResponsibles: getActResponsiblesMock,
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
          <div key={payment.id}>{payment.description}</div>
        ))}
      </div>
    );
  },
}));

vi.mock('../Calendar/PaymentModal', () => ({
  PaymentModal: (props: {
    isOpen: boolean;
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
        <div>{`linked-invoice-id:${props.linkedInvoice?.invoiceEntityId ?? ''}`}</div>
        <div>{`linked-invoice-record-id:${props.linkedInvoice?.invoiceRecordId ?? ''}`}</div>
        <div>{`linked-invoice-number:${props.linkedInvoice?.number ?? ''}`}</div>
        <div>{`linked-invoice-date:${props.linkedInvoice?.date ?? ''}`}</div>
        <div>{`linked-client-id:${props.linkedInvoice?.clientId ?? ''}`}</div>
        <div>{`linked-case-id:${props.linkedInvoice?.clientCaseId ?? ''}`}</div>
        <div>{`linked-type:${props.linkedInvoice?.type ?? ''}`}</div>
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
  beforeEach(() => {
    getActResponsiblesMock.mockReset();
    getAccountsRegistryResultMock.mockReset();
    getClientsMock.mockReset();
    getInvoiceMock.mockReset();
    invoiceModalPropsSpy.mockReset();
    paymentModalPropsSpy.mockReset();
    refreshAccountsMock.mockReset();

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
  });

  it('passes grouped invoice operations into the editor opened from the accounts registry', async () => {
    render(<Accounts routeInvoiceId={528} />);

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(528));

    expect(await screen.findByTestId('invoice-modal')).toBeTruthy();
    expect(await screen.findByText('Representative payment for invoice 24')).toBeTruthy();
    expect(screen.getByText('Sibling payment for invoice 24')).toBeTruthy();
    expect(screen.getByText('payments-loading:false')).toBeTruthy();
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

  it('passes the canonical linked invoice context into the payment modal opened from the accounts registry', async () => {
    render(<Accounts />);

    fireEvent.click(await screen.findByRole('button', { name: '24' }));
    fireEvent.click(await screen.findByText('Добавить платеж'));

    expect(await screen.findByTestId('payment-modal')).toBeTruthy();
    expect(screen.getByText('linked-invoice-id:24')).toBeTruthy();
    expect(screen.getByText('linked-invoice-record-id:528')).toBeTruthy();
    expect(screen.getByText('linked-invoice-number:24')).toBeTruthy();
    expect(screen.getByText('linked-invoice-date:2026-04-02')).toBeTruthy();
    expect(screen.getByText('linked-client-id:55')).toBeTruthy();
    expect(screen.getByText('linked-case-id:701')).toBeTruthy();
    expect(screen.getByText('linked-type:Income')).toBeTruthy();
  });
});
