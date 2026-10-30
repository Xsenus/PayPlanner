import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Contract } from '../../types';
import { ContractDetailPage } from './ContractDetailPage';
import { configureDocumentDeliveryTestApi } from '../../test/documentDeliveryFixtures';
import { documentCopyTestSession } from '../../test/documentCopySession';

const { dialogConfirmMock, dialogPromptMock, rolePermissionsResult, authState, deliveryToastSuccess, deliveryToastError } = vi.hoisted(() => ({
  deliveryToastSuccess: vi.fn(),
  deliveryToastError: vi.fn(),
  dialogConfirmMock: vi.fn(),
  dialogPromptMock: vi.fn(),
  authState: { user: { id: 7, role: { id: 1 } } },
  rolePermissionsResult: {
    yandexDisk: { canView: true, canCreate: true, viewScope: 'all' },
    clients: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    legalEntities: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    accounts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    acts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    payments: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    contracts: { canView: true, canCreate: false, canEdit: false, canDelete: false, canExport: true, viewScope: 'all' },
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
  useAuth: () => authState,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: deliveryToastSuccess,
    error: deliveryToastError,
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
    getOwnCompanyOptions: vi.fn().mockResolvedValue([]),
    getMessengerRecipients: vi.fn(),
    getMessengerDocumentDeliveryHistory: vi.fn(),
    prepareFinancialDocumentMessengerDelivery: vi.fn(),
    queueMessengerDocumentDelivery: vi.fn(),
    prepareElectronicSignatureEnvelope: vi.fn(),
    getMessengerDocumentDeliveryConfiguration: vi.fn().mockResolvedValue({ enabled: false, providers: [] }),
    getElectronicSignatureConfiguration: vi.fn().mockResolvedValue({ enabled: false, preparationEnabled: false, defaultSignatureType: 'qualified', defaultSigningOrder: 'sequential', defaultArchiveRetentionYears: 10, providers: [] }),
    getElectronicSignatureRecipients: vi.fn().mockResolvedValue([]),
    getElectronicSignatureHistory: vi.fn().mockResolvedValue([]),
    getDocumentImagePrintDefaults: vi.fn(),
    prepareDocumentPrint: vi.fn(),
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
    configureDocumentDeliveryTestApi('contract', 0, 0, false);
    deliveryToastSuccess.mockClear(); deliveryToastError.mockClear();
    Object.assign(rolePermissionsResult.yandexDisk, { canView: true, canCreate: true, viewScope: 'all' });
    vi.useRealTimers();
    authState.user = { id: 7, role: { id: 1 } };
    rolePermissionsResult.contracts.canView = true;
    rolePermissionsResult.contracts.canExport = true;
    rolePermissionsResult.contracts.viewScope = 'all';
    Object.assign(rolePermissionsResult.clients, { canView: true, canCreate: false, canEdit: false, canDelete: false });
    Object.assign(rolePermissionsResult.legalEntities, { canView: true, canCreate: false, canEdit: false, canDelete: false });
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
    vi.mocked(apiService.prepareDocumentPrint).mockReset().mockResolvedValue(new Blob(['contract print']));
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockReset().mockResolvedValue({ includeLogo: false, includeSeal: false, includeSignature: false });
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

    expect(screen.queryByRole('button', { name: 'Счета' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Акты' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Деньги' })).toBeNull();
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

  it('disables counterparty selection when clients and legal entities are hidden', async () => {
    Object.assign(rolePermissionsResult.clients, { canView: false });
    Object.assign(rolePermissionsResult.legalEntities, { canView: false });

    render(
      <ContractDetailPage
        contractId={77}
        onBack={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));

    const addCounterpartyButton = screen.getByRole('button', { name: 'Добавить контрагента' }) as HTMLButtonElement;
    expect(addCounterpartyButton.disabled).toBe(true);
  });

  describe('canonical delivery image defaults', () => {
    it.each(['Мессенджер', 'Э-подпись'])('prepares the contract PDF for %s without overriding saved image choices', async channel => {
      Object.assign(authState.user.role, { name: 'admin' });
      const pdf = configureDocumentDeliveryTestApi('contract', 77, 10);
      vi.mocked(apiService.downloadContractDocument).mockResolvedValueOnce(pdf);
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name: channel }));
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      if (channel === 'Мессенджер') {
        await screen.findAllByText(/Получатель PDF/);
        expect((screen.getByRole('button', { name: /Подготовить и отправить/ }) as HTMLButtonElement).disabled).toBe(true);
        fireEvent.click(screen.getByLabelText(/Подтверждаю выбранный документ/));
        fireEvent.click(screen.getByRole('button', { name: /Подготовить и отправить/ }));
        await waitFor(() => expect(apiService.queueMessengerDocumentDelivery).toHaveBeenCalledExactlyOnceWith(102));
        expect(apiService.prepareFinancialDocumentMessengerDelivery).toHaveBeenCalledExactlyOnceWith('contract', 77, pdf, expect.objectContaining({ recipientBindingId: 8, confirmRecipientAndDocument: true }));
      } else {
        fireEvent.click(await screen.findByText('Подписант PDF'));
        fireEvent.click(screen.getByLabelText(/Подтверждаю выбранную неизменяемую PDF-версию/));
        fireEvent.click(screen.getByRole('button', { name: 'Подготовить неизменяемую версию' }));
        await waitFor(() => expect(apiService.prepareElectronicSignatureEnvelope).toHaveBeenCalledExactlyOnceWith('contract', 77, pdf, expect.objectContaining({ clientId: 10, signerAccountIds: [7], confirmSnapshotAndSigners: true })));
        expect(apiService.queueMessengerDocumentDelivery).not.toHaveBeenCalled();
      }
      expect(apiService.downloadContractDocument).toHaveBeenCalledExactlyOnceWith(77, 'pdf');
    });
  });

  describe('delivery ownership', () => {
    it.each(['Мессенджер', 'Э-подпись'].flatMap(channel => ['document', 'user', 'role', 'scope', 'admin'].map(change => ({ channel, change }))))('closes $channel and discards confirmation after $change', async ({ channel, change }) => {
      Object.assign(authState.user.role, { name: 'admin' });
      vi.mocked(apiService.getContract).mockImplementation(async id => ({ ...contractFixture, id, number: String(id) }));
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name: channel }));
      const title = channel === 'Мессенджер' ? 'Отправка документа в мессенджер' : 'Электронная подпись';
      expect(await screen.findByLabelText(title)).toBeTruthy(); changeOwner(change);
      if (change === 'admin') Object.assign(authState.user.role, { name: 'employee' });
      view.rerender(<ContractDetailPage contractId={change === 'document' ? 78 : 77} onBack={vi.fn()} />);
      await waitFor(() => expect(screen.queryByLabelText(title)).toBeNull());
      Object.assign(authState.user, { id: 7, role: { id: 1, name: 'admin' } }); rolePermissionsResult.contracts.viewScope = 'all';
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect(screen.queryByLabelText(title)).toBeNull();
    });
    const deferred = <T,>() => {
      let resolve!: (value: T) => void; let reject!: (error: Error) => void;
      const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
      return { promise, resolve, reject };
    };
    const changeOwner = (change: string) => {
      if (change === 'user') authState.user = { id: 8, role: { id: 1 } };
      if (change === 'role') authState.user = { id: 7, role: { id: 2 } };
      if (change === 'export') rolePermissionsResult.contracts.canExport = false;
      if (change === 'view') rolePermissionsResult.contracts.canView = false;
      if (change === 'scope') rolePermissionsResult.contracts.viewScope = 'own';
    };
    it.each(['document', 'user', 'role', 'export', 'view', 'scope', 'unmount'] as const)('rejects a pending email prompt after %s', async change => {
      const pending = deferred<string | null>(); dialogPromptMock.mockReturnValueOnce(pending.promise);
      vi.mocked(apiService.getContract).mockImplementation(async id => ({ ...contractFixture, id, number: String(id) }));
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Email' }));
      expect(dialogPromptMock).toHaveBeenCalledOnce(); changeOwner(change);
      if (change === 'unmount') view.unmount(); else view.rerender(<ContractDetailPage contractId={change === 'document' ? 78 : 77} onBack={vi.fn()} />);
      await act(async () => pending.resolve('client@example.test'));
      expect(apiService.sendContractByEmail).not.toHaveBeenCalled(); expect(deliveryToastSuccess).not.toHaveBeenCalled();
    });
    it.each(['email-success', 'email-error', 'disk-success', 'disk-error'] as const)('ignores %s after a scope change', async action => {
      const pending = deferred<never>(); dialogPromptMock.mockResolvedValueOnce('client@example.test');
      const send = vi.mocked(action.startsWith('email') ? apiService.sendContractByEmail : apiService.uploadContractDocumentToYandexDisk);
      send.mockReturnValueOnce(pending.promise);
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name: action.startsWith('email') ? 'Email' : 'Я.Диск' }));
      await waitFor(() => expect(send).toHaveBeenCalledOnce()); rolePermissionsResult.contracts.viewScope = 'own';
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      await act(async () => { if (action.endsWith('error')) pending.reject(new Error('stale delivery failure')); else pending.resolve({ path: '/old' } as never); });
      expect(deliveryToastSuccess).not.toHaveBeenCalled(); expect(deliveryToastError).not.toHaveBeenCalled();
      expect(screen.queryByText('stale delivery failure')).toBeNull();
    });
    it.each(['export', 'disk-view', 'disk-create'] as const)('denies disk delivery without %s', async denied => {
      if (denied === 'export') rolePermissionsResult.contracts.canExport = false;
      if (denied === 'disk-view') rolePermissionsResult.yandexDisk.canView = false;
      if (denied === 'disk-create') rolePermissionsResult.yandexDisk.canCreate = false;
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      const button = await screen.findByRole('button', { name: 'Я.Диск' });
      expect((button as HTMLButtonElement).disabled).toBe(true); fireEvent.click(button);
      expect(apiService.uploadContractDocumentToYandexDisk).not.toHaveBeenCalled();
    });
    it('sends only the current contract and serializes email with disk delivery', async () => {
      const pending = deferred<string | null>(); dialogPromptMock.mockReturnValueOnce(pending.promise);
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Email' }));
      const disk = screen.getByRole('button', { name: 'Я.Диск' });
      expect((disk as HTMLButtonElement).disabled).toBe(true); fireEvent.click(disk);
      await act(async () => pending.resolve('client@example.test'));
      expect(apiService.sendContractByEmail).toHaveBeenCalledExactlyOnceWith(77, { format: 'pdf', email: 'client@example.test' });
      expect(apiService.uploadContractDocumentToYandexDisk).not.toHaveBeenCalled();
      expect(deliveryToastSuccess).toHaveBeenCalledOnce();
    });
    it('uploads the current contract to disk with permitted access and saved image defaults', async () => {
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Я.Диск' }));
      await waitFor(() => expect(deliveryToastSuccess).toHaveBeenCalledOnce());
      expect(apiService.uploadContractDocumentToYandexDisk).toHaveBeenCalledExactlyOnceWith(77, 'pdf');
      expect(deliveryToastError).not.toHaveBeenCalled();
      expect((screen.getByRole('button', { name: 'Я.Диск' }) as HTMLButtonElement).disabled).toBe(false);
    });
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
    expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
    await waitFor(() => expect((within(screen.getByRole('dialog', { name: 'Изображения в документе' })).getByRole('button', { name: 'Скачать Word' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Изображения в документе' })).getByRole('button', { name: 'Скачать Word' }));

    await waitFor(() => {
      expect(apiService.downloadContractDocument).toHaveBeenCalledWith(77, 'word', { includeLogo: false, includeSeal: false, includeSignature: false, signal: expect.any(AbortSignal) });
    });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'PDF' }));
    await waitFor(() => expect((within(screen.getByRole('dialog', { name: 'Изображения в документе' })).getByRole('button', { name: 'Скачать PDF' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Изображения в документе' })).getByRole('button', { name: 'Скачать PDF' }));

    await waitFor(() => {
      expect(apiService.downloadContractDocument).toHaveBeenCalledWith(77, 'pdf', { includeLogo: false, includeSeal: false, includeSignature: false, signal: expect.any(AbortSignal) });
    });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());

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

  describe('termination copy owner', () => {
    const terminated = { ...contractFixture, status: 'Terminated' as const, terminationDate: '2026-04-12', terminationTerms: 'Согласованное прекращение услуг' };
    beforeEach(() => vi.mocked(apiService.getContract).mockResolvedValue(terminated));

    it.each(['login', 'user', 'scope', 'export', 'back', 'error'] as const)('copy session expansion fences the former termination copy on %s', async change => {
      const session = documentCopyTestSession(); let resolve!: (blob: Blob) => void; let reject!: (error: Error) => void;
      const pending = new Promise<Blob>((yes, no) => { resolve = yes; reject = no; });
      vi.mocked(apiService.downloadContractTerminationAgreement).mockReturnValueOnce(pending);
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {}); const back = vi.fn();
      try {
        const view = render(<ContractDetailPage contractId={77} onBack={back} />);
        const button = await screen.findByRole('button', { name: 'Соглашение PDF' }); fireEvent.click(button); fireEvent.click(button);
        expect(apiService.downloadContractTerminationAgreement).toHaveBeenCalledExactlyOnceWith(77, 'pdf');
        if (change === 'login') session.replace();
        if (change === 'back') { fireEvent.click(screen.getByRole('button', { name: 'Назад' })); expect(back).toHaveBeenCalledOnce(); }
        if (change === 'user') authState.user = { id: 8, role: { id: 1 } };
        if (change === 'export') rolePermissionsResult.contracts.canExport = false;
        if (change === 'scope' || change === 'error') rolePermissionsResult.contracts.viewScope = 'own';
        if (['user', 'export', 'scope', 'error'].includes(change)) view.rerender(<ContractDetailPage contractId={77} onBack={back} />);
        await act(async () => { if (change === 'error') reject(new Error('Старая ошибка соглашения')); else resolve(new Blob(['late agreement'])); });
        expect(click).not.toHaveBeenCalled(); expect(deliveryToastSuccess).not.toHaveBeenCalled(); expect(deliveryToastError).not.toHaveBeenCalled();
      } finally { click.mockRestore(); session.restore(); }
    });

    it.each(['export', 'view'] as const)('copy session expansion refuses termination download without %s', async denied => {
      if (denied === 'export') rolePermissionsResult.contracts.canExport = false; else rolePermissionsResult.contracts.canView = false;
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      if (denied === 'export') {
        const button = await screen.findByRole('button', { name: 'Соглашение PDF' }); expect((button as HTMLButtonElement).disabled).toBe(true); fireEvent.click(button);
      } else await screen.findByText('Нет доступа к договору.');
      expect(apiService.downloadContractTerminationAgreement).not.toHaveBeenCalled();
    });

    it('copy session expansion keeps a new termination copy locked after the former finally', async () => {
      let first!: (blob: Blob) => void; let second!: (blob: Blob) => void;
      vi.mocked(apiService.getContract).mockImplementation(async id => ({ ...terminated, id, number: String(id) }));
      vi.mocked(apiService.downloadContractTerminationAgreement).mockReturnValueOnce(new Promise(resolve => { first = resolve; })).mockReturnValueOnce(new Promise(resolve => { second = resolve; }));
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      try {
        const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); fireEvent.click(await screen.findByRole('button', { name: 'Соглашение PDF' }));
        view.rerender(<ContractDetailPage contractId={78} onBack={vi.fn()} />); const button = await screen.findByRole('button', { name: 'Соглашение PDF' }); fireEvent.click(button);
        expect(apiService.downloadContractTerminationAgreement).toHaveBeenCalledTimes(2);
        await act(async () => first(new Blob(['old agreement']))); expect((button as HTMLButtonElement).disabled).toBe(true); expect(click).not.toHaveBeenCalled();
        await act(async () => second(new Blob(['current agreement']))); expect(click).toHaveBeenCalledOnce(); expect(deliveryToastSuccess).toHaveBeenCalledOnce();
        expect(click.mock.contexts[0]).toHaveProperty('download', 'contract-termination-78.pdf');
      } finally { click.mockRestore(); }
    });

    it('copy session expansion downloads the current termination PDF with permitted access', async () => {
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      try {
        render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); fireEvent.click(await screen.findByRole('button', { name: 'Соглашение PDF' }));
        await waitFor(() => expect(click).toHaveBeenCalledOnce()); expect(apiService.downloadContractTerminationAgreement).toHaveBeenCalledExactlyOnceWith(77, 'pdf');
        expect(deliveryToastSuccess).toHaveBeenCalledOnce(); expect(deliveryToastError).not.toHaveBeenCalled();
      } finally { click.mockRestore(); }
    });
  });

  describe('image print dialog', () => {
    it.each((['print', 'word'] as const).flatMap(format => (['login', 'refresh'] as const).map(mode => ({ format, mode }))))('copy session expansion handles contract $format after $mode', async ({ format, mode }) => {
      const session = documentCopyTestSession(); let finish!: (blob: Blob) => void;
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      const operation = vi.mocked(format === 'print' ? apiService.prepareDocumentPrint : apiService.downloadContractDocument);
      try {
        render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
        operation.mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
        const dialog = await open(format); fireEvent.click(dialog.getByRole('button', { name: format === 'print' ? 'Печатать' : 'Скачать Word' }));
        expect(operation).toHaveBeenCalledOnce(); if (mode === 'login') session.replace(); else session.refresh();
        await act(async () => finish(new Blob(['current copy'])));
        if (format === 'print') {
          const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
          if (mode === 'login') { expect(popup.close).toHaveBeenCalledOnce(); expect(popup.location.href).toBe(''); }
          else expect(popup.location.href).toContain('blob:');
        } else expect(click).toHaveBeenCalledTimes(mode === 'login' ? 0 : 1);
      } finally { click.mockRestore(); session.restore(); }
    });

    it('copy session expansion closes the contract dialog on a same-user replacement login', async () => {
      const session = documentCopyTestSession();
      try {
        const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready(); await open('pdf');
        session.replace(); view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
        expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      } finally { session.restore(); }
    });

    it('copy session expansion refuses to open contract defaults from a former login', async () => {
      const session = documentCopyTestSession();
      try {
        render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready(); session.replace();
        fireEvent.click(screen.getByRole('button', { name: 'PDF' }));
        expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull(); expect(apiService.getDocumentImagePrintDefaults).not.toHaveBeenCalled();
      } finally { session.restore(); }
    });
    it('copy session fences a late contract PDF before auth rerender', async () => {
      const session = documentCopyTestSession(); let finish!: (blob: Blob) => void;
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      try {
        render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
        vi.mocked(apiService.downloadContractDocument).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
        const dialog = await open('pdf'); fireEvent.click(dialog.getByRole('button', { name: 'Скачать PDF' }));
        expect(apiService.downloadContractDocument).toHaveBeenCalledOnce(); session.replace();
        await act(async () => finish(new Blob(['late copy'])));
        expect(click).not.toHaveBeenCalled();
      } finally { click.mockRestore(); session.restore(); }
    });
    const open = async (format: 'print' | 'word' | 'pdf') => {
      fireEvent.click(screen.getByRole('button', { name: format === 'print' ? 'Печать' : format === 'word' ? 'Word' : 'PDF' }));
      const dialog = within(screen.getByRole('dialog', { name: 'Изображения в документе' }));
      await waitFor(() => expect((dialog.getByRole('button', { name: format === 'print' ? 'Печатать' : `Скачать ${format === 'word' ? 'Word' : 'PDF'}` }) as HTMLButtonElement).disabled).toBe(false));
      return dialog;
    };
    const ready = async () => screen.findByRole('button', { name: 'Word' });

    it.each(['print', 'word', 'pdf'] as const)('starts the contract document copy with its saved defaults for %s', async format => {
      vi.mocked(apiService.getDocumentImagePrintDefaults).mockResolvedValueOnce({ includeLogo: true, includeSeal: true, includeSignature: false });
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      const dialog = await open(format);
      expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledExactlyOnceWith('contracts', 77, expect.any(AbortSignal));
      expect((dialog.getByLabelText('Логотип') as HTMLInputElement).checked).toBe(true);
      expect((dialog.getByLabelText('Печать') as HTMLInputElement).checked).toBe(true);
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      expect(apiService.prepareDocumentPrint).not.toHaveBeenCalled();
      if (format === 'print') expect(dialog.getByText(/Отмена в системном окне печати/)).toBeTruthy();
      fireEvent.click(dialog.getByRole('button', { name: format === 'print' ? 'Печатать' : `Скачать ${format === 'word' ? 'Word' : 'PDF'}` }));
      const options = { includeLogo: true, includeSeal: true, includeSignature: false, signal: expect.any(AbortSignal) };
      if (format === 'print') {
        await waitFor(() => expect(apiService.prepareDocumentPrint).toHaveBeenCalledExactlyOnceWith('contracts', 77, options));
        expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      } else await waitFor(() => expect(apiService.downloadContractDocument).toHaveBeenCalledExactlyOnceWith(77, format, options));
      expect(apiService.updateContract).not.toHaveBeenCalled();
    });

    it.each(['switch', 'user', 'role', 'scope', 'revoke', 'hide', 'unmount'] as const)('aborts contract document defaults and ignores the former scope on %s', async change => {
      let finish!: (value: { includeLogo: boolean; includeSeal: boolean; includeSignature: boolean }) => void;
      vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      fireEvent.click(screen.getByRole('button', { name: 'Word' }));
      const signal = vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[0][2]!;
      if (change === 'unmount') view.unmount();
      else {
        if (change === 'user') authState.user = { id: 8, role: { id: 1 } };
        if (change === 'role') authState.user = { id: 7, role: { id: 2 } };
        if (change === 'scope') rolePermissionsResult.contracts.viewScope = 'own';
        if (change === 'revoke') rolePermissionsResult.contracts.canExport = false;
        if (change === 'hide') rolePermissionsResult.contracts.canView = false;
        if (change === 'switch') vi.mocked(apiService.getContract).mockResolvedValue({ ...contractFixture, id: 78, number: 'CTR-78' });
        view.rerender(<ContractDetailPage contractId={change === 'switch' ? 78 : 77} onBack={vi.fn()} />);
      }
      expect(signal.aborted).toBe(true);
      await act(async () => finish({ includeLogo: true, includeSeal: true, includeSignature: true }));
      expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled(); expect(apiService.updateContract).not.toHaveBeenCalled();
    });

    it('keeps a contract default-loading error blocked until the explicit retry succeeds', async () => {
      vi.mocked(apiService.getDocumentImagePrintDefaults).mockRejectedValueOnce(new Error('Нет доступа к настройкам'));
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      fireEvent.click(screen.getByRole('button', { name: 'Word' }));
      const dialog = within(screen.getByRole('dialog', { name: 'Изображения в документе' }));
      expect((await dialog.findByRole('alert')).textContent).toContain('Нет доступа к настройкам');
      expect((dialog.getByRole('button', { name: 'Скачать Word' }) as HTMLButtonElement).disabled).toBe(true);
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      fireEvent.click(dialog.getByRole('button', { name: 'Повторить загрузку' }));
      await waitFor(() => expect((dialog.getByRole('button', { name: 'Скачать Word' }) as HTMLButtonElement).disabled).toBe(false));
      expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledTimes(2);
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
    });

    it.each(['print', 'word', 'pdf'] as const)('confirms independent images for %s without saving the contract', async format => {
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      const dialog = (await open(format));
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      fireEvent.click(dialog.getByLabelText('Логотип')); fireEvent.click(dialog.getByLabelText('Подпись'));
      fireEvent.click(dialog.getByRole('button', { name: format === 'print' ? 'Печатать' : `Скачать ${format === 'word' ? 'Word' : 'PDF'}` }));
      const options = { includeLogo: true, includeSeal: false, includeSignature: true, signal: expect.any(AbortSignal) };
      if (format === 'print') {
        await waitFor(() => expect(apiService.prepareDocumentPrint).toHaveBeenCalledExactlyOnceWith('contracts', 77, options));
        expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      } else await waitFor(() => expect(apiService.downloadContractDocument).toHaveBeenCalledExactlyOnceWith(77, format, options));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
      expect(apiService.updateContract).not.toHaveBeenCalled();
      if (format === 'print') {
        const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
        expect(popup.location.href).toBe('blob:contract'); expect(popup.close).not.toHaveBeenCalled();
      }
    });

    it('cancels before confirmation without fetching a document', async () => {
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      fireEvent.click((await open('word')).getByRole('button', { name: 'Отмена' }));
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
    });

    it('retains choices on failure and retries with a fresh request', async () => {
      vi.mocked(apiService.downloadContractDocument).mockRejectedValueOnce(new Error('Синтетический отказ')).mockResolvedValueOnce(new Blob(['contract']));
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      const dialog = (await open('word')); fireEvent.click(dialog.getByLabelText('Печать'));
      fireEvent.click(dialog.getByRole('button', { name: 'Скачать Word' })); await dialog.findByRole('alert');
      expect((dialog.getByLabelText('Печать') as HTMLInputElement).checked).toBe(true);
      fireEvent.click(dialog.getByRole('button', { name: 'Скачать Word' }));
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
      const calls = vi.mocked(apiService.downloadContractDocument).mock.calls;
      expect(calls).toHaveLength(2); expect(calls[1][2]?.signal).not.toBe(calls[0][2]?.signal);
    });

    it('aborts print and closes its pending window immediately on cancellation', async () => {
      let resolve!: (value: Blob) => void; const pending = new Promise<Blob>(done => { resolve = done; });
      vi.mocked(apiService.prepareDocumentPrint).mockReturnValueOnce(pending);
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      const dialog = (await open('print')); fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
      const signal = vi.mocked(apiService.prepareDocumentPrint).mock.calls[0][2]!.signal!;
      const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
      fireEvent.click(dialog.getByRole('button', { name: 'Отмена' }));
      expect(signal.aborted).toBe(true); expect(popup.close).toHaveBeenCalled();
      await act(async () => { resolve(new Blob(['late'])); await pending; });
      expect(popup.location.href).toBe('');
    });

    it.each(['switch', 'unmount', 'revoke', 'hide'] as const)('fences a late document on %s', async change => {
      let resolve!: (value: Blob) => void; const pending = new Promise<Blob>(done => { resolve = done; });
      vi.mocked(apiService.downloadContractDocument).mockReturnValueOnce(pending);
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      fireEvent.click((await open('word')).getByRole('button', { name: 'Скачать Word' }));
      const signal = vi.mocked(apiService.downloadContractDocument).mock.calls[0][2]!.signal!;
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click');
      try {
        if (change === 'unmount') view.unmount();
        else {
          if (change === 'revoke') rolePermissionsResult.contracts.canExport = false;
          if (change === 'hide') rolePermissionsResult.contracts.canView = false;
          if (change === 'switch') vi.mocked(apiService.getContract).mockResolvedValue({ ...contractFixture, id: 78, number: 'CTR-78' });
          view.rerender(<ContractDetailPage contractId={change === 'switch' ? 78 : 77} onBack={vi.fn()} />);
        }
        expect(signal.aborted).toBe(true);
        await act(async () => { resolve(new Blob(['late'])); await pending; });
        expect(click).not.toHaveBeenCalled(); expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      } finally { click.mockRestore(); }
    });

    it('disables document actions without export permission', async () => {
      rolePermissionsResult.contracts.canExport = false;
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      for (const label of ['Печать', 'Word', 'PDF']) expect((screen.getByRole('button', { name: label }) as HTMLButtonElement).disabled).toBe(true);
      expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
    });

    it('does not request a document if its print window is blocked', async () => {
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />); await ready();
      vi.mocked(window.open).mockReturnValueOnce(null);
      const dialog = (await open('print')); fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
      await dialog.findByRole('alert'); expect(apiService.downloadContractDocument).not.toHaveBeenCalled();
      expect(apiService.prepareDocumentPrint).not.toHaveBeenCalled();
    });
  });

  describe('contract access scope', () => {
    const deferred = () => {
      let resolve!: (value: Contract) => void;
      const promise = new Promise<Contract>(done => { resolve = done; });
      return { promise, resolve };
    };

    it('does not load or display a contract without view permission', async () => {
      rolePermissionsResult.contracts.canView = false;
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect((await screen.findByRole('alert')).textContent).toContain('Нет доступа к договору.');
      expect(apiService.getContract).not.toHaveBeenCalled();
      expect(apiService.getContractRulesSettings).not.toHaveBeenCalled();
      expect(screen.queryByDisplayValue('CTR-77')).toBeNull();
    });

    it('hides cached data on revocation and requires a fresh load after view is restored', async () => {
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      await screen.findByDisplayValue('CTR-77');
      rolePermissionsResult.contracts.canView = false;
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect(screen.getByRole('alert').textContent).toContain('Нет доступа к договору.');
      expect(screen.queryByDisplayValue('CTR-77')).toBeNull();
      expect(apiService.getContract).toHaveBeenCalledTimes(1);
      const fresh = deferred(); vi.mocked(apiService.getContract).mockReturnValueOnce(fresh.promise);
      rolePermissionsResult.contracts.canView = true;
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect(screen.queryByDisplayValue('CTR-77')).toBeNull();
      await act(async () => { fresh.resolve({ ...contractFixture, number: 'FRESH-77' }); await fresh.promise; });
      expect(await screen.findByDisplayValue('FRESH-77')).toBeTruthy();
    });

    it('hides old data across visibility changes and ignores a late response from the previous scope', async () => {
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      await screen.findByDisplayValue('CTR-77');
      const own = deferred(); vi.mocked(apiService.getContract).mockReturnValueOnce(own.promise);
      rolePermissionsResult.contracts.viewScope = 'own';
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect(screen.queryByDisplayValue('CTR-77')).toBeNull();
      const all = deferred(); vi.mocked(apiService.getContract).mockReturnValueOnce(all.promise);
      rolePermissionsResult.contracts.viewScope = 'all';
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect(screen.queryByDisplayValue('CTR-77')).toBeNull();
      await act(async () => { own.resolve({ ...contractFixture, number: 'HIDDEN-LATE' }); await own.promise; });
      expect(screen.queryByDisplayValue('HIDDEN-LATE')).toBeNull();
      await act(async () => { all.resolve({ ...contractFixture, number: 'CURRENT-ALL' }); await all.promise; });
      expect(await screen.findByDisplayValue('CURRENT-ALL')).toBeTruthy();
      expect(apiService.getContract).toHaveBeenCalledTimes(3);
    });

    it.each(['user', 'role'] as const)('hides cached data before reloading for a changed %s', async change => {
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      await screen.findByDisplayValue('CTR-77');
      const fresh = deferred(); vi.mocked(apiService.getContract).mockReturnValueOnce(fresh.promise);
      authState.user = change === 'user' ? { id: 8, role: { id: 1 } } : { id: 7, role: { id: 2 } };
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect(screen.queryByDisplayValue('CTR-77')).toBeNull();
      await act(async () => { fresh.resolve({ ...contractFixture, number: 'CURRENT-USER' }); await fresh.promise; });
      expect(await screen.findByDisplayValue('CURRENT-USER')).toBeTruthy();
      expect(apiService.getContract).toHaveBeenCalledTimes(2);
    });

    it('does not display a response belonging to a different contract ID', async () => {
      vi.mocked(apiService.getContract).mockResolvedValue({ ...contractFixture, id: 78, number: 'FOREIGN-78' });
      render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect((await screen.findByRole('alert')).textContent).toContain('Договор недоступен.');
      expect(screen.queryByDisplayValue('FOREIGN-78')).toBeNull();
    });

    it('shows a failed scope reload without restoring cached contract data', async () => {
      const view = render(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      await screen.findByDisplayValue('CTR-77');
      vi.mocked(apiService.getContract).mockRejectedValueOnce(new Error('Нет доступа к этому договору.'));
      rolePermissionsResult.contracts.viewScope = 'own';
      view.rerender(<ContractDetailPage contractId={77} onBack={vi.fn()} />);
      expect((await screen.findByRole('alert')).textContent).toContain('Нет доступа к этому договору.');
      expect(screen.queryByDisplayValue('CTR-77')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Word' })).toBeNull();
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
