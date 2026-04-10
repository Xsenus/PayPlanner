import { render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { Accounts } from './Accounts';
import type { AccountRegistryRow, AccountsSummary, Client, Invoice, Payment } from '../../types';

const {
  getActResponsiblesMock,
  getClientsMock,
  getInvoiceMock,
  invoiceModalPropsSpy,
  refreshAccountsMock,
} = vi.hoisted(() => ({
  getActResponsiblesMock: vi.fn(),
  getClientsMock: vi.fn(),
  getInvoiceMock: vi.fn(),
  invoiceModalPropsSpy: vi.fn(),
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

function createInvoice(): Invoice {
  return {
    id: 528,
    invoiceEntityId: 24,
    number: '24',
    date: '2026-04-02',
    dueDate: '2026-04-03',
    amount: 10_000,
    type: 'Income',
    status: 'Completed',
    isPaid: true,
    clientId: 55,
    clientName: 'Байгузина Екатерина Евгеньевна',
    responsibleName: 'Черкасова Н.Д.',
    createdAt: '2026-04-02T10:00:00Z',
  } as Invoice;
}

function createRepresentativePayment(): Payment {
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
    description: 'Представляющая запись по счету №24',
    createdAt: '2026-04-02T10:05:00Z',
    updatedAt: null,
    documentKind: 'Payment',
    account: '24',
    accountDate: '2026-04-02',
  } as Payment;
}

function createSiblingPayment(): Payment {
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
    description: 'Доплата по счету №24',
    createdAt: '2026-04-03T12:00:00Z',
    updatedAt: null,
    documentKind: 'Payment',
    account: '24',
    accountDate: '2026-04-02',
  } as Payment;
}

function createAccountRow(): AccountRegistryRow {
  return {
    groupKey: 'invoice-24',
    accountNumber: '24',
    invoice: createInvoice(),
    payments: [createRepresentativePayment(), createSiblingPayment()],
    acts: [],
    totalAmount: 10_000,
    dueDate: '2026-04-03',
    paidDate: '2026-04-03',
    status: 'paid',
    type: 'Income',
    isOverdue: false,
  };
}

function createSummary(): AccountsSummary {
  return {
    total: { amount: 10_000, count: 1 },
    pending: { amount: 0, count: 0 },
    paid: { amount: 10_000, count: 1 },
    overdue: { amount: 0, count: 0 },
    cancelled: { amount: 0, count: 0 },
  };
}

function createClient(): Client {
  return {
    id: 55,
    name: 'Байгузина Екатерина Евгеньевна',
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
  useAccountsRegistry: () => ({
    items: [createAccountRow()],
    total: 1,
    summary: createSummary(),
    loading: false,
    refreshing: false,
    error: null,
    refresh: refreshAccountsMock,
  }),
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
  InvoiceModal: (props: { open: boolean; relatedPayments?: Payment[]; paymentsLoading?: boolean }) => {
    invoiceModalPropsSpy(props);
    if (!props.open) {
      return null;
    }

    return (
      <div data-testid="invoice-modal">
        <div>{`payments-loading:${String(props.paymentsLoading)}`}</div>
        {(props.relatedPayments ?? []).map((payment) => (
          <div key={payment.id}>{payment.description}</div>
        ))}
      </div>
    );
  },
}));

vi.mock('../Calendar/PaymentModal', () => ({
  PaymentModal: () => null,
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
    getInvoiceMock.mockReset();
    getClientsMock.mockReset();
    getActResponsiblesMock.mockReset();
    invoiceModalPropsSpy.mockReset();
    refreshAccountsMock.mockReset();

    getInvoiceMock.mockResolvedValue(createInvoice());
    getClientsMock.mockResolvedValue([createClient()]);
    getActResponsiblesMock.mockResolvedValue([]);
  });

  it('passes grouped invoice operations into the editor opened from the accounts registry', async () => {
    render(<Accounts routeInvoiceId={528} />);

    await waitFor(() => expect(getInvoiceMock).toHaveBeenCalledWith(528));

    expect(await screen.findByTestId('invoice-modal')).toBeTruthy();
    expect(await screen.findByText('Представляющая запись по счету №24')).toBeTruthy();
    expect(screen.getByText('Доплата по счету №24')).toBeTruthy();
    expect(screen.getByText('payments-loading:false')).toBeTruthy();
  });
});
