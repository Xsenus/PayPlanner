import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Contract } from '../../types';
import { ContractDetailPage } from './ContractDetailPage';

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

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('../../hooks/useDebouncedValue', () => ({
  useDebouncedValue: <T,>(value: T) => value,
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

vi.mock('../ui/TableColumnSettings', () => ({
  TableColumnSettings: () => <div data-testid="table-column-settings" />,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => <div data-testid="table-pagination" />,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getContract: vi.fn(),
    getContractRulesSettings: vi.fn(),
    getInvoices: vi.fn(),
    getActs: vi.fn(),
    getPaymentsV2: vi.fn(),
    getClientsV2: vi.fn(),
    updateContract: vi.fn(),
  },
}));

import { apiService } from '../../services/api';

const contractFixture = {
  id: 77,
  number: 'CTR-77',
  title: null,
  date: '2026-04-10',
  startDate: '2026-04-10',
  description: 'Тестовый договор',
  billingMode: 'Fixed',
  isOpenEnded: true,
  amount: 62_500,
  validUntil: null,
  terminationDate: null,
  invoiceAmountTotal: 62_500,
  actAmountTotal: 31_000,
  paymentAmountTotal: 0,
  invoiceIds: [101],
  actIds: [201],
  paymentIds: [],
  clients: [
    { id: 10, name: 'Клиент 1', company: 'ООО Альфа', comment: '' },
    { id: 20, name: 'Клиент 2', company: 'ООО Бета', comment: '' },
  ],
  createdAt: '2026-04-10T00:00:00Z',
  auditCreatedByUserName: null,
  auditUpdatedAt: null,
  auditUpdatedByUserName: null,
  auditIsDeleted: false,
  auditDeletedAt: null,
  auditDeletedByUserName: null,
} as unknown as Contract;

describe('ContractDetailPage integration', () => {
  beforeEach(() => {
    vi.mocked(apiService.getContract).mockReset();
    vi.mocked(apiService.getContractRulesSettings).mockReset();
    vi.mocked(apiService.getInvoices).mockReset();
    vi.mocked(apiService.getActs).mockReset();
    vi.mocked(apiService.getPaymentsV2).mockReset();
    vi.mocked(apiService.getClientsV2).mockReset();
    vi.mocked(apiService.updateContract).mockReset();

    vi.mocked(apiService.getContract).mockResolvedValue(contractFixture);
    vi.mocked(apiService.getContractRulesSettings).mockResolvedValue({
      autoGenerateWhenEmpty: true,
      allowManualNumberEdit: true,
    });
    vi.mocked(apiService.getInvoices).mockResolvedValue({
      items: [],
      total: 0,
    });
    vi.mocked(apiService.getActs).mockResolvedValue({
      items: [],
      total: 0,
    });
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
      items: [],
      total: 0,
    });
    vi.mocked(apiService.getClientsV2).mockResolvedValue({
      items: [],
      total: 0,
    });
  });

  it('switches the invoices tab between contract documents and all client invoices', async () => {
    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Счета' }));

    await waitFor(() => {
      expect(apiService.getInvoices).toHaveBeenLastCalledWith({
        contractId: 77,
        page: 1,
        pageSize: 10,
        sortBy: 'date',
        sortDir: 'desc',
      });
    });

    expect(screen.getByText('Счета в договоре')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Все счета клиентов' }));

    await waitFor(() => {
      expect(apiService.getInvoices).toHaveBeenLastCalledWith({
        clientIds: '10,20',
        page: 1,
        pageSize: 10,
        sortBy: 'date',
        sortDir: 'desc',
      });
    });

    expect(screen.getByText('Счета клиентов договора')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Только в договоре' }));

    await waitFor(() => {
      expect(apiService.getInvoices).toHaveBeenLastCalledWith({
        contractId: 77,
        page: 1,
        pageSize: 10,
        sortBy: 'date',
        sortDir: 'desc',
      });
    });
  });

  it('switches the acts tab between contract documents and all client acts', async () => {
    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Акты' }));

    await waitFor(() => {
      expect(apiService.getActs).toHaveBeenLastCalledWith({
        contractId: 77,
        page: 1,
        pageSize: 10,
        sortBy: 'date',
        sortDir: 'desc',
      });
    });

    expect(screen.getByText('Акты в договоре')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Все акты клиентов' }));

    await waitFor(() => {
      expect(apiService.getActs).toHaveBeenLastCalledWith({
        clientIds: '10,20',
        page: 1,
        pageSize: 10,
        sortBy: 'date',
        sortDir: 'desc',
      });
    });

    expect(screen.getByText('Акты клиентов договора')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Только в договоре' }));

    await waitFor(() => {
      expect(apiService.getActs).toHaveBeenLastCalledWith({
        contractId: 77,
        page: 1,
        pageSize: 10,
        sortBy: 'date',
        sortDir: 'desc',
      });
    });
  });

  it('loads the money tab from contract-linked payments only', async () => {
    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Деньги' }));

    await waitFor(() => {
      expect(apiService.getPaymentsV2).toHaveBeenLastCalledWith({
        contractId: 77,
        excludeInvoiceDocuments: true,
        page: 1,
        pageSize: 10,
        sortBy: 'date',
        sortDir: 'desc',
      });
    });

    expect(screen.getByText('Платежи договора')).toBeTruthy();
  });
});
