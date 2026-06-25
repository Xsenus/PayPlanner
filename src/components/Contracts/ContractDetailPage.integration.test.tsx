import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Contract } from '../../types';
import { ContractDetailPage } from './ContractDetailPage';

const { dialogConfirmMock, dialogPromptMock, rolePermissionsResult } = vi.hoisted(() => ({
  dialogConfirmMock: vi.fn(),
  dialogPromptMock: vi.fn(),
  rolePermissionsResult: {
    clients: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    accounts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    acts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    payments: { canView: true, canCreate: false, canEdit: false, canDelete: false },
  },
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

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: dialogConfirmMock,
    prompt: dialogPromptMock,
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

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => rolePermissionsResult,
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
    getContractTerminationCheck: vi.fn(),
    terminateContract: vi.fn(),
    updateContract: vi.fn(),
    downloadContractDocument: vi.fn(),
    downloadContractTerminationAgreement: vi.fn(),
    sendContractByEmail: vi.fn(),
    uploadContractDocumentToYandexDisk: vi.fn(),
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
    vi.useRealTimers();
    Object.assign(rolePermissionsResult.clients, { canView: true, canCreate: false, canEdit: false, canDelete: false });
    Object.assign(rolePermissionsResult.accounts, { canView: true, canCreate: false, canEdit: false, canDelete: false });
    Object.assign(rolePermissionsResult.acts, { canView: true, canCreate: false, canEdit: false, canDelete: false });
    Object.assign(rolePermissionsResult.payments, { canView: true, canCreate: false, canEdit: false, canDelete: false });
    vi.mocked(apiService.getContract).mockReset();
    vi.mocked(apiService.getContractRulesSettings).mockReset();
    vi.mocked(apiService.getInvoices).mockReset();
    vi.mocked(apiService.getActs).mockReset();
    vi.mocked(apiService.getPaymentsV2).mockReset();
    vi.mocked(apiService.getClientsV2).mockReset();
    vi.mocked(apiService.getContractTerminationCheck).mockReset();
    vi.mocked(apiService.terminateContract).mockReset();
    vi.mocked(apiService.updateContract).mockReset();
    vi.mocked(apiService.downloadContractDocument).mockReset();
    vi.mocked(apiService.downloadContractTerminationAgreement).mockReset();
    vi.mocked(apiService.sendContractByEmail).mockReset();
    vi.mocked(apiService.uploadContractDocumentToYandexDisk).mockReset();
    dialogConfirmMock.mockReset();
    dialogPromptMock.mockReset();
    dialogConfirmMock.mockResolvedValue(true);
    dialogPromptMock.mockResolvedValue(null);

    vi.mocked(apiService.getContract).mockResolvedValue(contractFixture);
    vi.mocked(apiService.getContractRulesSettings).mockResolvedValue({
      autoGenerateWhenEmpty: true,
      allowManualNumberEdit: true,
    });
    vi.mocked(apiService.getInvoices).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 25,
    });
    vi.mocked(apiService.getActs).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 25,
    });
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 25,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    });
    vi.mocked(apiService.getClientsV2).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 25,
    });
    vi.mocked(apiService.getContractTerminationCheck).mockResolvedValue({
      hasUnpaidInvoices: false,
      unpaidInvoiceCount: 0,
      unpaidInvoiceAmount: 0,
    });
    vi.mocked(apiService.downloadContractDocument).mockResolvedValue(new Blob(['contract'], { type: 'application/msword' }));
    vi.mocked(apiService.downloadContractTerminationAgreement).mockResolvedValue(new Blob(['agreement'], { type: 'application/pdf' }));
    vi.mocked(apiService.sendContractByEmail).mockResolvedValue({
      sent: true,
      recipient: 'client@example.com',
      fileName: 'contract-CTR-77.pdf',
      format: 'pdf',
    });
    vi.mocked(apiService.uploadContractDocumentToYandexDisk).mockResolvedValue({
      success: true,
      path: '/CRM/contracts/contract.pdf',
    });
    Object.defineProperty(globalThis.URL, 'createObjectURL', {
      configurable: true,
      value: vi.fn(() => 'blob:contract'),
    });
    Object.defineProperty(globalThis.URL, 'revokeObjectURL', {
      configurable: true,
      value: vi.fn(),
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

  it('hides related financial tabs and does not load them without section rights', async () => {
    Object.assign(rolePermissionsResult.accounts, { canView: false });
    Object.assign(rolePermissionsResult.acts, { canView: false });
    Object.assign(rolePermissionsResult.payments, { canView: false });

    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });

    expect(screen.queryByRole('button', { name: 'РЎС‡РµС‚Р°' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'РђРєС‚С‹' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Р”РµРЅСЊРіРё' })).toBeNull();
    expect(apiService.getInvoices).not.toHaveBeenCalled();
    expect(apiService.getActs).not.toHaveBeenCalled();
    expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
  });

  it('keeps contract clients as plain text when clients section is hidden', async () => {
    Object.assign(rolePermissionsResult.clients, { canView: false });
    const onOpenClient = vi.fn();

    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
        onOpenClient={onOpenClient}
      />,
    );

    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });

    expect(screen.getByText('Клиент 1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Клиент 1' })).toBeNull();
    expect(onOpenClient).not.toHaveBeenCalled();
  });

  it('downloads and sends contract documents from the detail header', async () => {
    dialogPromptMock.mockResolvedValueOnce('client@example.com');

    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Word' }));

    await waitFor(() => {
      expect(apiService.downloadContractDocument).toHaveBeenCalledWith(77, 'word');
    });

    fireEvent.click(screen.getByRole('button', { name: 'PDF' }));

    await waitFor(() => {
      expect(apiService.downloadContractDocument).toHaveBeenCalledWith(77, 'pdf');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Email' }));

    await waitFor(() => {
      expect(apiService.sendContractByEmail).toHaveBeenCalledWith(77, {
        email: 'client@example.com',
        format: 'pdf',
      });
    });

    fireEvent.click(screen.getByRole('button', { name: 'Я.Диск' }));

    await waitFor(() => {
      expect(apiService.uploadContractDocumentToYandexDisk).toHaveBeenCalledWith(77, 'pdf');
    });
  });

  it('offers a termination agreement document for terminated contracts', async () => {
    vi.mocked(apiService.getContract).mockResolvedValue({
      ...contractFixture,
      status: 'Terminated',
      terminationDate: '2026-05-22',
      terminationTerms: 'Расторгнуть по соглашению сторон',
    } as Contract);

    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Соглашение PDF' }));

    await waitFor(() => {
      expect(apiService.downloadContractTerminationAgreement).toHaveBeenCalledWith(77, 'pdf');
    });
  });

  it('checks unpaid invoices before terminating a contract', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-04T12:00:00.000Z'));
    dialogPromptMock
      .mockResolvedValueOnce('2026-05-22')
      .mockReturnValueOnce('Расторжение по соглашению')
      .mockReturnValueOnce('Соглашение сторон')
      .mockReturnValueOnce('Оплатить остаток до конца месяца');
    dialogConfirmMock.mockResolvedValueOnce(true);
    vi.mocked(apiService.getContractTerminationCheck).mockResolvedValue({
      hasUnpaidInvoices: true,
      unpaidInvoiceCount: 2,
      unpaidInvoiceAmount: 23_000,
    });
    vi.mocked(apiService.terminateContract).mockResolvedValue({
      ...contractFixture,
      status: 'Terminated',
      terminationDate: '2026-05-22',
      terminationTerms: 'Расторжение по соглашению',
      terminationFinancialTerms: 'Оплатить остаток до конца месяца',
    } as Contract);

    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Расторгнуть' }));

    await waitFor(() => {
      expect(apiService.getContractTerminationCheck).toHaveBeenCalledWith(77);
      expect(dialogConfirmMock).toHaveBeenCalledWith(
        expect.objectContaining({
          description: expect.stringContaining('неоплаченные счета'),
        }),
      );
      expect(apiService.terminateContract).toHaveBeenCalledWith(
        77,
        '2026-05-22',
        'Расторжение по соглашению',
        'Соглашение сторон',
        'Оплатить остаток до конца месяца',
      );
    });
    expect(dialogPromptMock).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({
        defaultValue: '2026-06-04',
        inputType: 'date',
      }),
    );

    vi.useRealTimers();
  });
});
