import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ClientDetail } from './ClientDetail';
import type { Act, Client, Contract, Invoice, Payment, PowerOfAttorney } from '../../types';
import { apiService } from '../../services/api';

const CLIENT_PROFILE_NOTES_PREFIX = '__PP_CLIENT_PROFILE_V2__:';

const {
  confirmMock,
  emptyActsResult,
  emptyClientCasesResult,
  emptyContractsResult,
  emptyInvoicesResult,
  emptyLegalEntitiesResult,
  emptyPaymentsResult,
  authState,
  permissionsResult,
  toastInfoMock,
  toastErrorMock,
  toastSuccessMock,
  toastWarningMock,
  actModalPropsSpy,
  paymentModalPropsSpy,
  taskJournalPropsSpy,
  twoTypeStatsMock,
  useActsMock,
  useClientCasesMock,
  useContractsMock,
  useInvoicesMock,
  usePaymentsPagedMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  emptyActsResult: {
    acts: [] as unknown[],
    loading: false,
    refreshing: false,
    error: null,
    summary: null,
    pagination: { total: 0, page: 1, pageSize: 10 },
    refresh: vi.fn(),
    createAct: vi.fn(),
    updateAct: vi.fn(),
    deleteAct: vi.fn(),
  },
  emptyClientCasesResult: { cases: [] as unknown[] },
  emptyContractsResult: {
    contracts: [] as unknown[],
    loading: false,
    refreshing: false,
    error: null,
    pagination: { total: 0, page: 1, pageSize: 10 },
    refresh: vi.fn(),
    createContract: vi.fn(),
    updateContract: vi.fn(),
    deleteContract: vi.fn(),
  },
  emptyInvoicesResult: {
    invoices: [] as unknown[],
    loading: false,
    refreshing: false,
    error: null,
    summary: null,
    pagination: { total: 0, page: 1, pageSize: 10 },
    refresh: vi.fn(),
    createInvoice: vi.fn(),
    updateInvoice: vi.fn(),
    deleteInvoice: vi.fn(),
  },
  emptyLegalEntitiesResult: {
    legalEntities: [] as unknown[],
    createLegalEntity: vi.fn(),
    refresh: vi.fn(),
  },
  emptyPaymentsResult: {
    payments: [] as unknown[],
    total: 0,
    loading: false,
    refreshing: false,
    error: null,
    refresh: vi.fn(),
  },
  authState: { isAdmin: false },
  permissionsResult: createPermissions(),
  toastInfoMock: vi.fn(),
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
  toastWarningMock: vi.fn(),
  actModalPropsSpy: vi.fn(),
  paymentModalPropsSpy: vi.fn(),
  taskJournalPropsSpy: vi.fn(),
  twoTypeStatsMock: vi.fn(),
  useActsMock: vi.fn(),
  useClientCasesMock: vi.fn(),
  useContractsMock: vi.fn(),
  useInvoicesMock: vi.fn(),
  usePaymentsPagedMock: vi.fn(),
}));

function createPermissions() {
  return {
    clients: { canView: true, canCreate: false, canEdit: true, canDelete: false },
    accounts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    acts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    contracts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    payments: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    tasks: { canView: true, canCreate: false, canEdit: false, canDelete: false, canExport: false },
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

function createClient(profileOverrides?: Record<string, unknown>): Client {
  return {
    id: 1,
    name: 'Иванов Иван Иванович',
    email: '',
    phone: '',
    company: '',
    address: '',
    notes: `${CLIENT_PROFILE_NOTES_PREFIX}${JSON.stringify({
      kind: 'individual',
      lastName: 'Иванов',
      firstName: 'Иван',
      middleName: 'Иванович',
      education: [
        { institution: 'НГУ', specialty: 'Право', diplomaNumber: 'Д-1', oksoCode: '40.03.01' },
        { institution: 'МГУ', specialty: 'Экономика', diplomaNumber: 'Д-2', oksoCode: '38.03.01' },
      ],
      professions: [{ profession: 'Юрист', okpdtrCode: '12345' }],
      workplaces: [{ organizationName: 'ООО Альфа', position: 'Юрист', startDate: '2024-01-01', endDate: '2024-12-31' }],
      relatives: [{ relation: 'Отец', fullName: 'Иванов Петр Сергеевич', birthYear: '1960', phone: '+79990000000' }],
      comment: '',
      legacyNotes: '',
      ...profileOverrides,
    })}`,
    createdAt: '2026-04-15T10:00:00Z',
    isActive: true,
  } as Client;
}

function createPowerOfAttorney(overrides: Partial<PowerOfAttorney> = {}): PowerOfAttorney {
  return {
    id: 77,
    clientId: 1,
    number: '321',
    issuedAt: '2026-04-15',
    validUntil: '2026-04-30',
    issuer: 'Старый нотариус',
    notes: null,
    isActive: true,
    createdAt: '2026-04-15T10:00:00Z',
    updatedAt: null,
    ...overrides,
  } as PowerOfAttorney;
}

function mockRelatedDocumentLookups() {
  vi.mocked(apiService.getContracts).mockResolvedValue({
    items: [{ id: 11, number: 'C-11', title: 'Client contract' } as Contract],
    total: 1,
    page: 1,
    pageSize: 200,
  });
  vi.mocked(apiService.getInvoices).mockResolvedValue({
    items: [{ id: 22, number: 'I-22', date: '2026-04-10' } as Invoice],
    total: 1,
    page: 1,
    pageSize: 200,
  });
  vi.mocked(apiService.getActs).mockResolvedValue({
    items: [{ id: 33, number: 'A-33', date: '2026-04-11' } as Act],
    total: 1,
    page: 1,
    pageSize: 200,
  });
  vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
    items: [
      {
        id: 44,
        date: '2026-04-12',
        amount: 12_500,
        description: 'Linked payment',
        documentKind: 'Payment',
      } as Payment,
    ],
    total: 1,
    page: 1,
    pageSize: 500,
    summary: {
      income: 12_500,
      expense: 0,
      balance: 12_500,
      incomeCount: 1,
      expenseCount: 0,
      total: 1,
    },
  });
}

async function selectRelatedFilter(label: string, value: string, user: ReturnType<typeof userEvent.setup>) {
  await waitFor(() => {
    const select = screen.getByLabelText(label) as HTMLSelectElement;
    expect([...select.options].some((option) => option.value === value)).toBe(true);
  });
  await user.selectOptions(screen.getByLabelText(label), value);
}

function getRowContainerByValue(value: string): HTMLElement {
  const input = screen.getByDisplayValue(value);
  const container = input.parentElement?.parentElement;
  expect(container).toBeTruthy();
  return container as HTMLElement;
}

function getPowerOfAttorneyCard(text: string): HTMLElement {
  const element = screen.getByText(text);
  const container = element.closest('[data-poa-card="true"]');
  expect(container).toBeTruthy();
  return container as HTMLElement;
}

function formatLocalYmd(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 7, role: { id: 1 } },
    isAdmin: () => authState.isAdmin,
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: toastSuccessMock,
    error: toastErrorMock,
    info: toastInfoMock,
    warning: toastWarningMock,
  }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useFormRequirements', () => ({
  useFormRequirements: () => ({ isRequired: () => false }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => permissionsResult,
}));

vi.mock('../../hooks/useLegalEntities', () => ({
  useLegalEntities: () => emptyLegalEntitiesResult,
}));

vi.mock('../../hooks/useClientStatuses', () => ({
  useClientStatuses: () => ({ statuses: [] as unknown[] }),
}));

vi.mock('../../hooks/useClientCases', () => ({
  useClientCases: (...args: unknown[]) => useClientCasesMock(...args),
}));

vi.mock('../../hooks/useDebouncedValue', () => ({
  useDebouncedValue: (value: string) => value,
}));

vi.mock('../../hooks/useActs', () => ({
  useActs: (...args: unknown[]) => useActsMock(...args),
}));

vi.mock('../../hooks/useInvoices', () => ({
  useInvoices: (...args: unknown[]) => useInvoicesMock(...args),
}));

vi.mock('../../hooks/useContracts', () => ({
  useContracts: (...args: unknown[]) => useContractsMock(...args),
}));

vi.mock('../../hooks/usePaymentsPaged', () => ({
  usePaymentsPaged: (...args: unknown[]) => usePaymentsPagedMock(...args),
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

vi.mock('../../services/api', () => ({
  apiService: {
    getClient: vi.fn(),
    updateClient: vi.fn(),
    deleteClient: vi.fn(),
    getClientPowersOfAttorney: vi.fn(),
    getClientCaseFinanceSummaries: vi.fn(),
    getClientCaseExpenseDetails: vi.fn(),
    getContracts: vi.fn(),
    getInvoices: vi.fn(),
    getActs: vi.fn(),
    getPaymentsV2: vi.fn(),
    getClients: vi.fn(),
    getActResponsibles: vi.fn(),
    createClientPowerOfAttorney: vi.fn(),
    updateClientPowerOfAttorney: vi.fn(),
    deleteClientPowerOfAttorney: vi.fn(),
    getOksoDictionary: vi.fn(),
    getOkpdtrDictionary: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    createLegalEntity: vi.fn(),
    updateLegalEntity: vi.fn(),
    getLegalEntity: vi.fn(),
    getClientBalance: vi.fn().mockResolvedValue({ clientId: 1, balance: 0 }),
    getClientBalanceTransactions: vi.fn().mockResolvedValue([]),
    applyClientBalance: vi.fn().mockResolvedValue({ appliedAmount: 0, balance: 0 }),
  },
}));

vi.mock('../../utils/clientInvoiceRelatedPayments', () => ({
  loadClientInvoiceRelatedPayments: vi.fn().mockResolvedValue([]),
}));

vi.mock('../../utils/clientInvoiceModalData', () => ({
  refreshClientInvoiceModalData: vi.fn().mockResolvedValue({ invoice: null, relatedPayments: [] }),
}));

vi.mock('../../utils/clientInvoiceModalApi', () => ({
  bindClientInvoiceModalApi: () => ({
    fetchInvoice: vi.fn(),
    fetchPayments: vi.fn().mockResolvedValue([]),
  }),
}));

vi.mock('../Calendar/PaymentModal', () => ({
  PaymentModal: (props: unknown) => {
    paymentModalPropsSpy(props);
    return null;
  },
}));

vi.mock('../Acts/ActModal', () => ({
  ActModal: (props: unknown) => {
    actModalPropsSpy(props);
    return null;
  },
}));

vi.mock('../Accounts/InvoiceModal', () => ({
  InvoiceModal: () => null,
}));

vi.mock('../Contracts/ContractModal', () => ({
  ContractModal: () => null,
}));

vi.mock('../Tasks/TaskJournal', () => ({
  TaskJournal: (props: unknown) => {
    taskJournalPropsSpy(props);
    return <div data-testid="client-task-journal">Задачи клиента</div>;
  },
}));

vi.mock('./CaseModal', () => ({
  CaseModal: () => null,
}));

vi.mock('../Statistics/TwoTypeStats', () => ({
  TwoTypeStats: (props: unknown) => {
    twoTypeStatsMock(props);
    return null;
  },
}));

vi.mock('../MonthRange/MonthRangePicker', () => ({
  MonthRangePicker: ({
    value,
    onChange,
  }: {
    value: { from?: string; to?: string };
    onChange: (next: { from?: string; to?: string }) => void;
  }) => (
    <div>
      <label>
        <span>Month from</span>
        <input
          aria-label="month-range-from"
          value={value.from ?? ''}
          onChange={(event) => onChange({ from: event.target.value || undefined, to: value.to })}
        />
      </label>
      <label>
        <span>Month to</span>
        <input
          aria-label="month-range-to"
          value={value.to ?? ''}
          onChange={(event) => onChange({ from: value.from, to: event.target.value || undefined })}
        />
      </label>
    </div>
  ),
}));

vi.mock('../DateRange/DatePeriodPicker', () => ({
  DatePeriodPicker: ({
    label = 'Period',
    value,
    onChange,
  }: {
    label?: string;
    value: { mode: 'preset'; preset: string } | { mode: 'custom'; from?: string; to?: string };
    onChange: (next: { mode: 'preset'; preset: string } | { mode: 'custom'; from?: string; to?: string }) => void;
  }) => (
    <div>
      <div>{label}</div>
      <label>
        <span>{`${label} from`}</span>
        <input
          aria-label="date-period-from"
          value={value.mode === 'custom' ? value.from ?? '' : ''}
          onChange={(event) =>
            onChange({
              mode: 'custom',
              from: event.target.value || undefined,
              to: value.mode === 'custom' ? value.to : undefined,
            })}
        />
      </label>
      <label>
        <span>{`${label} to`}</span>
        <input
          aria-label="date-period-to"
          value={value.mode === 'custom' ? value.to ?? '' : ''}
          onChange={(event) =>
            onChange({
              mode: 'custom',
              from: value.mode === 'custom' ? value.from : undefined,
              to: event.target.value || undefined,
            })}
        />
      </label>
      <button type="button" onClick={() => onChange({ mode: 'preset', preset: 'year_to_date' })}>
        Apply year to date
      </button>
      <button type="button" onClick={() => onChange({ mode: 'custom' })}>
        Clear period
      </button>
    </div>
  ),
}));

vi.mock('../ui/LookupSelect', () => ({
  LookupSelect: ({
    label,
    value,
    onChange,
    options = [],
    disabled,
  }: {
    label: string;
    value: string;
    onChange: (value: string) => void;
    options?: Array<{ value: string; label: string }>;
    disabled?: boolean;
  }) => (
    <label>
      <span>{label}</span>
      <select
        aria-label={label}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        disabled={disabled}
      >
        <option value="">Не указано</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  ),
}));

vi.mock('../ui/TableColumnSettings', () => ({
  TableColumnSettings: () => null,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => null,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditModal: ({ open }: { open: boolean }) =>
    open ? <div data-testid="object-audit-modal" /> : null,
  ObjectAuditLauncher: () => null,
}));

vi.mock('./ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

describe('ClientDetail integration', () => {
  beforeEach(() => {
    Object.assign(permissionsResult.clients, createPermissions().clients);
    Object.assign(permissionsResult.accounts, createPermissions().accounts);
    Object.assign(permissionsResult.acts, createPermissions().acts);
    Object.assign(permissionsResult.contracts, createPermissions().contracts);
    Object.assign(permissionsResult.payments, createPermissions().payments);
    Object.assign(permissionsResult.tasks, createPermissions().tasks);
    confirmMock.mockReset();
    confirmMock.mockResolvedValue(true);
    toastInfoMock.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    toastWarningMock.mockReset();
    actModalPropsSpy.mockReset();
    paymentModalPropsSpy.mockReset();
    taskJournalPropsSpy.mockReset();
    twoTypeStatsMock.mockReset();
    useClientCasesMock.mockReset();
    useActsMock.mockReset();
    useInvoicesMock.mockReset();
    useContractsMock.mockReset();
    usePaymentsPagedMock.mockReset();
    window.sessionStorage.clear();
    authState.isAdmin = false;

    useClientCasesMock.mockReturnValue(emptyClientCasesResult);
    useActsMock.mockReturnValue(emptyActsResult);
    useInvoicesMock.mockReturnValue(emptyInvoicesResult);
    useContractsMock.mockReturnValue(emptyContractsResult);
    usePaymentsPagedMock.mockReturnValue(emptyPaymentsResult);

    vi.mocked(apiService.getClient).mockResolvedValue(createClient());
    vi.mocked(apiService.getClients).mockResolvedValue([]);
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([]);
    vi.mocked(apiService.updateClient).mockResolvedValue(createClient());
    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValue([]);
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.getClientCaseExpenseDetails).mockResolvedValue([]);
    vi.mocked(apiService.getContracts).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 });
    vi.mocked(apiService.getInvoices).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 });
    vi.mocked(apiService.getActs).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 });
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 500,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    });
    vi.mocked(apiService.createClientPowerOfAttorney).mockResolvedValue(createPowerOfAttorney());
    vi.mocked(apiService.updateClientPowerOfAttorney).mockResolvedValue(createPowerOfAttorney());
    vi.mocked(apiService.deleteClientPowerOfAttorney).mockResolvedValue(undefined);
    vi.mocked(apiService.getOksoDictionary).mockResolvedValue([]);
    vi.mocked(apiService.getOkpdtrDictionary).mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 20,
    });
  });

  it('does not enable hidden tab data hooks on the initial profile view', async () => {
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { name: 'Карточка клиента' });

    expect(useClientCasesMock).toHaveBeenCalledWith(1, expect.objectContaining({ enabled: false }));
    expect(usePaymentsPagedMock).toHaveBeenCalledWith(expect.objectContaining({ enabled: false }));
    expect(useInvoicesMock).toHaveBeenCalledWith(expect.objectContaining({ enabled: false }));
    expect(useActsMock).toHaveBeenCalledWith(expect.objectContaining({ enabled: false }));
    expect(useContractsMock).toHaveBeenCalledWith(expect.objectContaining({ enabled: false }));
    expect(apiService.getClients).not.toHaveBeenCalled();
    expect(apiService.getOksoDictionary).not.toHaveBeenCalled();
    expect(apiService.getOkpdtrDictionary).not.toHaveBeenCalled();
    expect(apiService.getClientPowersOfAttorney).not.toHaveBeenCalled();
  });

  it('does not load all clients for admin responsible lookup on initial profile view', async () => {
    authState.isAdmin = true;

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { name: 'Карточка клиента' });

    await waitFor(() => {
      expect(apiService.getActResponsibles).toHaveBeenCalledTimes(1);
    });
    expect(apiService.getClients).not.toHaveBeenCalled();
  });

  it('opens the client tasks tab with a client-scoped task journal', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { name: 'Карточка клиента' });
    await user.click(screen.getByRole('button', { name: 'Задачи' }));

    expect(await screen.findByTestId('client-task-journal')).toBeTruthy();
    expect(taskJournalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({
      embedded: true,
      initialView: 'list',
      contextFilter: expect.objectContaining({
        clientId: 1,
        objectName: 'Иванов Иван Иванович',
        title: 'Иванов Иван Иванович',
      }),
    }));
  });

  it('opens the client profile in readonly mode until editing is requested', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    const lastNameInput = await screen.findByDisplayValue('Иванов');
    expect((lastNameInput as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Редактировать' }));

    expect((lastNameInput as HTMLInputElement).disabled).toBe(false);
    await user.clear(lastNameInput);
    await user.type(lastNameInput, 'Петров');
    expect(screen.getByDisplayValue('Петров')).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Отмена' }));

    await waitFor(() => {
      expect((screen.getByDisplayValue('Иванов') as HTMLInputElement).disabled).toBe(true);
    });
    expect(screen.queryByDisplayValue('Петров')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('shows compact labels under client quick action buttons', async () => {
    Object.assign(permissionsResult.clients, { canCreate: true });
    Object.assign(permissionsResult.accounts, { canCreate: true });
    Object.assign(permissionsResult.acts, { canCreate: true });
    Object.assign(permissionsResult.contracts, { canCreate: true });
    Object.assign(permissionsResult.payments, { canCreate: true });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { name: 'Карточка клиента' });

    expect(screen.getByRole('button', { name: 'Добавить дело' }).parentElement?.textContent).toContain('Дела');
    expect(screen.getByRole('button', { name: 'Добавить платёж' }).parentElement?.textContent).toContain('Платежи');
    expect(screen.getByRole('button', { name: 'Добавить счёт' }).parentElement?.textContent).toContain('Счета');
    expect(screen.getByRole('button', { name: 'Добавить акт' }).parentElement?.textContent).toContain('Акты');
    expect(screen.getByRole('button', { name: 'Добавить договор' }).parentElement?.textContent).toContain('Договоры');
  });

  it('hides client payment actions and keeps the payment section disabled without payment rights', async () => {
    Object.assign(permissionsResult.clients, { canCreate: true });
    Object.assign(permissionsResult.payments, { canView: false, canCreate: true });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    await screen.findByRole('heading', { name: 'Карточка клиента' });

    expect(screen.queryByRole('button', { name: 'Добавить платёж' })).toBeNull();
    await waitFor(() => {
      const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(expect.objectContaining({ enabled: false, clientId: 1 }));
    });
    expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
  });

  it('opens balance journal and audit from compact header actions', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.getClientBalance).mockResolvedValue({ clientId: 1, balance: 750 });
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([
      {
        caseId: 22,
        incomeTotal: 5_000,
        incomePaid: 3_750,
        incomeOutstanding: 1_250,
        incomeCount: 1,
        expenseTotal: 0,
        expensePaid: 0,
        expenseOutstanding: 0,
        expenseCount: 0,
        plannedMargin: 5_000,
        paidMargin: 3_750,
        outstandingBalance: 1_250,
      },
    ]);
    vi.mocked(apiService.getClientBalanceTransactions).mockResolvedValue([
      {
        id: 11,
        clientId: 1,
        amount: 750,
        type: 'Overpayment',
        direction: 'Credit',
        description: 'Переплата по платежу',
        paymentId: 77,
        invoiceId: 33,
        createdAt: '2026-04-28T08:00:00Z',
        createdByUserName: 'Администратор',
      },
    ]);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    const balanceButton = await screen.findByRole('button', { name: /История баланса/ });
    await waitFor(() => {
      expect(within(balanceButton).getByText('Долг')).toBeTruthy();
      expect(within(balanceButton).getByText('Аванс')).toBeTruthy();
      const normalizedText = balanceButton.textContent?.replace(/\s/g, '') ?? '';
      expect(normalizedText).toContain('1250');
      expect(normalizedText).toContain('750');
    });

    await user.click(balanceButton);
    expect(await screen.findByText('Баланс клиента')).toBeTruthy();
    expect(await screen.findByText('Переплата по платежу')).toBeTruthy();
    expect(await screen.findByText('Источник #77')).toBeTruthy();
    expect(await screen.findByText('Счёт #33')).toBeTruthy();
    expect(apiService.getClientBalanceTransactions).toHaveBeenCalledWith(1);

    await user.click(screen.getByRole('button', { name: 'История клиента' }));
    expect(await screen.findByTestId('object-audit-modal')).toBeTruthy();
  });

  it('loads profile dictionaries and powers of attorney only when their groups are opened', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { name: 'Карточка клиента' });
    expect(apiService.getOksoDictionary).not.toHaveBeenCalled();
    expect(apiService.getOkpdtrDictionary).not.toHaveBeenCalled();
    expect(apiService.getClientPowersOfAttorney).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Образование/ }));
    await waitFor(() => {
      expect(apiService.getOksoDictionary).toHaveBeenCalledTimes(1);
    });
    expect(apiService.getOkpdtrDictionary).not.toHaveBeenCalled();
    expect(apiService.getClientPowersOfAttorney).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Профессии/ }));
    await waitFor(() => {
      expect(apiService.getOkpdtrDictionary).toHaveBeenCalledTimes(1);
    });
    expect(apiService.getClientPowersOfAttorney).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Доверенности/ }));
    await waitFor(() => {
      expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledWith(1);
    });
  });

  it('loads legal entity lookup with paged search for legal client profile', async () => {
    const user = userEvent.setup();

    vi.mocked(apiService.getClient).mockResolvedValue({
      ...createClient({ kind: 'legal' }),
      legalEntityId: 21,
      legalEntity: {
        id: 21,
        shortName: 'ООО Ромашка',
        fullName: 'Общество с ограниченной ответственностью Ромашка',
        inn: '6312000000',
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
        clients: [],
      },
    } as Client);
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({
      items: [
        {
          id: 21,
          shortName: 'ООО Ромашка',
          fullName: 'Общество с ограниченной ответственностью Ромашка',
          inn: '6312000000',
          clientsCount: 0,
          createdAt: '2026-04-01T00:00:00Z',
          clients: [],
        },
      ],
      total: 1,
      page: 1,
      pageSize: 20,
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await waitFor(() => {
      expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledWith({
        search: undefined,
        page: 1,
        pageSize: 20,
      });
    });
    expect(screen.getByText(/ООО Ромашка/)).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    await user.type(screen.getByLabelText('Поиск юридического лица клиента'), 'ром');

    await waitFor(() => {
      expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledWith({
        search: 'ром',
        page: 1,
        pageSize: 20,
      });
    });
  });

  it('shows delete controls in profile subsections and removes an education row', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByDisplayValue('Иванов');
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));

    expect(await screen.findByRole('button', { name: 'Удалить телефон' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Удалить email' })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Паспорта' }));
    expect(await screen.findByRole('button', { name: 'Удалить паспорт' })).toBeTruthy();
    expect(screen.queryByText('Действует с')).toBeNull();
    expect(screen.queryByText('Действует по')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Адреса' }));
    expect((await screen.findAllByRole('button', { name: 'Удалить адрес' })).length).toBeGreaterThan(0);

    await user.click(screen.getByRole('button', { name: 'Образование (ОКСО)' }));
    expect(await screen.findByDisplayValue('НГУ')).toBeTruthy();
    expect(screen.getByDisplayValue('МГУ')).toBeTruthy();
    expect(within(getRowContainerByValue('НГУ')).queryByText(/^Удалить$/)).toBeNull();
    expect(within(getRowContainerByValue('НГУ')).getByRole('button', { name: 'Удалить образование' })).toBeTruthy();
    expect(within(getRowContainerByValue('МГУ')).getByRole('button', { name: 'Удалить образование' })).toBeTruthy();

    await user.click(within(getRowContainerByValue('НГУ')).getByRole('button', { name: 'Удалить образование' }));

    await waitFor(() => {
      expect(confirmMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Удаление образования',
          confirmLabel: 'Удалить',
        }),
      );
      expect(toastSuccessMock).toHaveBeenCalledWith('Образование удалено.', 'Образование (ОКСО)');
      expect(screen.queryByDisplayValue('НГУ')).toBeNull();
    });
    expect(screen.getByDisplayValue('МГУ')).toBeTruthy();
    expect(within(getRowContainerByValue('МГУ')).getByRole('button', { name: 'Удалить образование' })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Профессии (ОКПДТР)' }));
    expect(await screen.findByDisplayValue('Юрист')).toBeTruthy();
    expect(within(getRowContainerByValue('Юрист')).getByRole('button', { name: 'Удалить профессию' })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Работа и должности' }));
    expect(await screen.findByDisplayValue('ООО Альфа')).toBeTruthy();
    expect(within(getRowContainerByValue('ООО Альфа')).getByRole('button', { name: 'Удалить место работы' })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Родственники' }));
    expect(await screen.findByDisplayValue('Отец')).toBeTruthy();
    expect(within(getRowContainerByValue('Отец')).getByRole('button', { name: 'Удалить родственника' })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Свидетельства о рождении' }));
    expect(await screen.findByRole('button', { name: 'Удалить свидетельство о рождении' })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Военные билеты' }));
    expect(await screen.findByRole('button', { name: 'Удалить военный билет' })).toBeTruthy();
  }, 15000);

  it('updates and deletes a power of attorney directly from the client card', async () => {
    const user = userEvent.setup();
    const originalPoa = createPowerOfAttorney();
    const updatedPoa = createPowerOfAttorney({
      issuer: 'Новый нотариус',
      validUntil: '2026-05-15',
    });

    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValue([originalPoa]);
    vi.mocked(apiService.updateClientPowerOfAttorney).mockResolvedValue(updatedPoa);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByDisplayValue('Иванов');
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    await user.click(screen.getByRole('button', { name: 'Доверенности' }));

    expect(await screen.findByText('Кем выдана: Старый нотариус')).toBeTruthy();

    expect(within(getPowerOfAttorneyCard('Кем выдана: Старый нотариус')).getByRole('button', { name: 'Удалить доверенность' })).toBeTruthy();
    expect(within(getPowerOfAttorneyCard('Кем выдана: Старый нотариус')).queryByText(/^Изменить$/)).toBeNull();

    await user.click(within(getPowerOfAttorneyCard('Кем выдана: Старый нотариус')).getByRole('button', { name: 'Изменить доверенность' }));

    const promptForm = (await screen.findByText('Редактирование доверенности')).closest('form');
    expect(promptForm).toBeTruthy();

    const issuerInput = within(promptForm as HTMLElement).getByLabelText('Кем выдана');
    const validUntilInput = within(promptForm as HTMLElement).getByLabelText('Действует до') as HTMLInputElement;

    await user.clear(issuerInput);
    await user.type(issuerInput, 'Новый нотариус');
    fireEvent.change(validUntilInput, { target: { value: '2026-05-15' } });

    await user.click(within(promptForm as HTMLElement).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      expect(apiService.updateClientPowerOfAttorney).toHaveBeenCalledWith(
        1,
        originalPoa.id,
        expect.objectContaining({
          number: originalPoa.number,
          issuedAt: originalPoa.issuedAt,
          validUntil: '2026-05-15',
          issuer: 'Новый нотариус',
          isActive: true,
        }),
      );
    });

    expect(await screen.findByText('Кем выдана: Новый нотариус')).toBeTruthy();

    await user.click(within(getPowerOfAttorneyCard('Кем выдана: Новый нотариус')).getByRole('button', { name: 'Удалить доверенность' }));

    await waitFor(() => {
      expect(confirmMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: 'Удаление доверенности',
          confirmLabel: 'Удалить',
        }),
      );
      expect(apiService.deleteClientPowerOfAttorney).toHaveBeenCalledWith(1, originalPoa.id);
      expect(toastSuccessMock).toHaveBeenCalledWith('Доверенность удалена.', 'Доверенности');
    });

    await waitFor(() => {
      expect(screen.queryByText('Кем выдана: Новый нотариус')).toBeNull();
    });
  });

  it('clears hidden passport validity dates before saving the client card', async () => {
    const user = userEvent.setup();

    vi.mocked(apiService.getClient).mockResolvedValue(
      createClient({
        passportEntries: [
          {
            series: '36 19',
            number: '664580',
            issuedBy: 'ОУФМС России по Самарской области',
            issueDate: '2019-06-15',
            departmentCode: '630-001',
            birthPlacePassport: 'г. Самара',
            birthPlaceFias: 'Самарская обл, г Самара',
            validFrom: '2019-06-15',
            validTo: '2045-06-15',
          },
        ],
      }),
    );

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByDisplayValue('Иванов');
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    await user.click(screen.getByRole('button', { name: 'Паспорта' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      expect(apiService.updateClient).toHaveBeenCalled();
    });

    const [, payload] = vi.mocked(apiService.updateClient).mock.calls.at(-1) ?? [];
    const notes = (payload as { notes?: string | null }).notes ?? '';
    const serializedProfile = notes.startsWith(CLIENT_PROFILE_NOTES_PREFIX)
      ? notes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)
      : '';
    const parsedProfile = JSON.parse(serializedProfile) as {
      passportEntries: Array<{ validFrom: string; validTo: string }>;
    };

    expect(parsedProfile.passportEntries[0]).toEqual(
      expect.objectContaining({
        validFrom: '',
        validTo: '',
      }),
    );
  });

  it('filters client cases inside the cases tab', async () => {
    const user = userEvent.setup();

    useClientCasesMock.mockReturnValue({
      cases: [
        {
          id: 11,
          clientId: 1,
          title: 'Наследственное дело',
          description: 'Подготовка документов',
          status: 'Open',
          createdAt: '2026-04-10T10:00:00Z',
        },
        {
          id: 12,
          clientId: 1,
          title: 'Исполнительное производство',
          description: 'Закрыто после исполнения',
          status: 'Closed',
          createdAt: '2026-04-11T10:00:00Z',
        },
      ],
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="cases" />);

    const searchInput = await screen.findByLabelText('Поиск дел клиента');
    expect(screen.getByText('Наследственное дело')).toBeTruthy();
    expect(screen.getByText('Исполнительное производство')).toBeTruthy();

    await user.type(searchInput, 'наслед');
    expect(screen.getByText('Наследственное дело')).toBeTruthy();
    expect(screen.queryByText('Исполнительное производство')).toBeNull();

    await user.clear(searchInput);
    await user.selectOptions(screen.getByLabelText('Статус дел клиента'), 'closed');
    expect(screen.queryByText('Наследственное дело')).toBeNull();
    expect(screen.getByText('Исполнительное производство')).toBeTruthy();
  });

  it('shows active case filter badges and clears them with reset', async () => {
    const user = userEvent.setup();

    useClientCasesMock.mockReturnValue({
      cases: [
        {
          id: 11,
          clientId: 1,
          title: 'Наследственное дело',
          description: 'Подготовка документов',
          status: 'Open',
          createdAt: '2026-04-10T10:00:00Z',
        },
        {
          id: 12,
          clientId: 1,
          title: 'Исполнительное производство',
          description: 'Закрыто после исполнения',
          status: 'Closed',
          createdAt: '2026-04-11T10:00:00Z',
        },
      ],
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="cases" />);

    await user.type(await screen.findByLabelText('Поиск дел клиента'), 'наслед');
    await user.selectOptions(screen.getByLabelText('Статус дел клиента'), 'open');

    expect(screen.getByText('Применены фильтры:')).toBeTruthy();
    expect(screen.getByText('Поиск: наслед')).toBeTruthy();
    expect(screen.getByText('Статус: Открыто')).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Сбросить все' }));

    await waitFor(() => {
      expect(screen.queryByText('Применены фильтры:')).toBeNull();
      expect(screen.getByText('Наследственное дело')).toBeTruthy();
      expect(screen.getByText('Исполнительное производство')).toBeTruthy();
    });
  });

  it('passes payment filters from the client tab to the payments hook', async () => {
    const user = userEvent.setup();

    useClientCasesMock.mockReturnValue({
      cases: [
        {
          id: 22,
          clientId: 1,
          title: 'Дело по взысканию',
          description: '',
          status: 'Open',
          createdAt: '2026-04-10T10:00:00Z',
        },
      ],
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    const searchInput = await screen.findByLabelText('Поиск платежей клиента');
    await user.type(searchInput, '10000');
    await user.selectOptions(screen.getByLabelText('Статус платежей клиента'), 'overdue');

    await waitFor(() => {
      const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: '10000',
          status: 'Overdue',
        }),
      );
    });
  });

  it('keeps long system payment notes compact and expandable', async () => {
    const user = userEvent.setup();
    usePaymentsPagedMock.mockReturnValue({
      ...emptyPaymentsResult,
      total: 1,
      payments: [
        {
          id: 501,
          date: '2026-04-28',
          plannedDate: '2026-04-28',
          amount: 16_250,
          paidAmount: 250,
          outstandingAmount: 16_000,
          hasPartialPayment: true,
          status: 'Pending',
          description: 'Рассрочка по счету №3 · платеж 5/6',
          isPaid: false,
          paidDate: null,
          lastPaymentDate: '2026-04-28',
          notes: '',
          systemNotes: [
            'Зачтено с баланса клиента: 150,00',
            'Зачтено с баланса клиента: 100,00',
            'Дата платежа изменена: 25.05.2026 → 28.04.2026',
            'Изменена оплаченная сумма: было 750,00, стало 16 250,00',
            'Статус обновлён: Ожидается → Выполнен',
          ].join('\n'),
          clientId: 1,
          client: { id: 1, name: 'Вашурина Анастасия Олеговна' },
          clientCase: { id: 7, title: 'Исполнение решения суда', status: 'Open' },
          type: 'Income',
          account: '3',
          accountDate: '2026-01-19',
          documentKind: 'Payment',
          createdAt: '2026-04-28T00:00:00Z',
          rescheduleCount: 0,
        },
      ],
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    await screen.findByText('Рассрочка по счету №3 · платеж 5/6');
    const expandButton = await screen.findByRole('button', { name: 'Показать полностью' });
    const noteCard = expandButton.closest('div');

    expect(noteCard?.querySelector('.max-h-20')).toBeTruthy();

    await user.click(expandButton);

    expect(await screen.findByRole('button', { name: 'Свернуть' })).toBeTruthy();
    expect(noteCard?.querySelector('.max-h-20')).toBeFalsy();
  });

  it('passes payment client role to payments and stats hooks', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    await user.selectOptions(await screen.findByLabelText('Вид платежей клиента'), 'expenseClient');

    await waitFor(() => {
      const lastQuery = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastQuery).toEqual(
        expect.objectContaining({
          clientId: 1,
          clientRole: 'expenseClient',
          type: 'Expense',
        }),
      );

      const statsCall = twoTypeStatsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(statsCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          clientRole: 'expenseClient',
        }),
      );
    });
  });

  it('defaults supplier cards to supplier payments and supplier invoices', async () => {
    vi.mocked(apiService.getClient).mockResolvedValue({
      ...createClient(),
      clientStatusId: 8,
      clientStatus: {
        id: 8,
        name: 'Поставщик',
        description: '',
        colorHex: '#7C3AED',
        isActive: true,
        systemKey: 'supplier',
      },
    } as Client);

    const { unmount } = render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    await waitFor(() => {
      const lastPaymentQuery = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastPaymentQuery).toEqual(
        expect.objectContaining({
          clientId: 1,
          clientRole: 'counterparty',
          type: 'Expense',
        }),
      );

      const statsCall = twoTypeStatsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(statsCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          clientRole: 'counterparty',
        }),
      );
    });

    unmount();
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="accounts" />);

    await waitFor(() => {
      const lastInvoiceQuery = useInvoicesMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastInvoiceQuery).toEqual(
        expect.objectContaining({
          clientId: 1,
          clientRole: 'counterparty',
          type: 'Expense',
        }),
      );
    });
  });

  it('opens a case expense breakdown from the client case card', async () => {
    const user = userEvent.setup();

    useClientCasesMock.mockReturnValue({
      cases: [
        {
          id: 22,
          clientId: 1,
          title: 'Дело по взысканию',
          description: '',
          status: 'Open',
          createdAt: '2026-04-10T10:00:00Z',
        },
      ],
    });
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([
      {
        caseId: 22,
        incomeTotal: 0,
        incomePaid: 0,
        incomeOutstanding: 0,
        incomeCount: 0,
        expenseTotal: 5_000,
        expensePaid: 2_000,
        expenseOutstanding: 3_000,
        expenseCount: 1,
        plannedMargin: -5_000,
        paidMargin: -2_000,
        outstandingBalance: 3_000,
      },
    ]);
    vi.mocked(apiService.getClientCaseExpenseDetails).mockResolvedValue([
      {
        paymentId: 101,
        invoiceId: 44,
        invoiceNumber: 'EXP-44',
        invoiceDate: '2026-04-20',
        paymentDate: '2026-04-21',
        plannedDate: '2026-04-25',
        supplierId: 7,
        supplierName: 'Geo Supplier',
        expenseClientId: 1,
        expenseClientName: 'Matter Client',
        caseId: 22,
        caseTitle: 'Дело по взысканию',
        lineItemId: 501,
        lineItemName: 'Survey',
        serviceId: null,
        serviceName: null,
        quantity: 1,
        unit: 'шт',
        price: 5_000,
        amount: 5_000,
        paidAmount: 2_000,
        outstandingAmount: 3_000,
        status: 'Pending',
        paymentOrderNumber: 'PP-44',
        paymentDocumentDate: '2026-04-22',
        description: 'Survey',
      },
    ]);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="cases" />);

    const title = await screen.findByText('Дело по взысканию');
    const card = title.closest('article') as HTMLElement;

    await waitFor(() => {
      expect(within(card).getByRole('button', { name: /Расходы/i })).toBeTruthy();
    });

    await user.click(within(card).getByRole('button', { name: /Расходы/i }));

    await waitFor(() => {
      expect(apiService.getClientCaseExpenseDetails).toHaveBeenCalledWith(1, {
        caseId: 22,
        clientRole: 'expenseClient',
      });
    });

    expect(await screen.findByRole('dialog', { name: /Расшифровка расходов/i })).toBeTruthy();
    expect(screen.getByText('Geo Supplier')).toBeTruthy();
    expect(screen.getByText('EXP-44')).toBeTruthy();
    expect(screen.getByText('Survey')).toBeTruthy();
    expect(screen.getByText(/PP-44/)).toBeTruthy();
  });

  it('prefills expense client from selected supplier case when creating supplier payments', async () => {
    const user = userEvent.setup();
    permissionsResult.clients.canCreate = true;
    permissionsResult.payments.canCreate = true;

    vi.mocked(apiService.getClient).mockResolvedValue({
      ...createClient(),
      clientStatusId: 9,
      clientStatus: {
        id: 9,
        name: 'External contractor',
        description: '',
        colorHex: '#7C3AED',
        isActive: true,
        systemKey: 'supplier',
      },
    } as Client);
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([
      {
        caseId: 22,
        clientId: 55,
        clientName: 'Matter Client',
        caseTitle: 'Matter case',
        caseStatus: 'Open',
        caseCreatedAt: '2026-04-10T10:00:00Z',
        incomeTotal: 0,
        incomePaid: 0,
        incomeOutstanding: 0,
        incomeCount: 0,
        expenseTotal: 5_000,
        expensePaid: 2_000,
        expenseOutstanding: 3_000,
        expenseCount: 1,
        plannedMargin: -5_000,
        paidMargin: -2_000,
        outstandingBalance: 3_000,
      },
    ]);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    await user.click(await screen.findByRole('button', { name: 'Matter case' }));
    await user.click(screen.getByRole('button', { name: /Добавить плат/ }));

    await waitFor(() => {
      const props = paymentModalPropsSpy.mock.calls.at(-1)?.[0] as {
        defaultClientId?: number;
        defaultExpenseClientId?: number;
        defaultClientCaseId?: number;
        casesPrefetch?: Array<{ id: number; clientId: number }>;
      };

      expect(props.defaultClientId).toBe(1);
      expect(props.defaultExpenseClientId).toBe(55);
      expect(props.defaultClientCaseId).toBe(22);
      expect(props.casesPrefetch).toEqual([
        expect.objectContaining({ id: 22, clientId: 55 }),
      ]);
    });
  });

  it('shows payment filter badges and resets the payment query state', async () => {
    const user = userEvent.setup();

    useClientCasesMock.mockReturnValue({
      cases: [
        {
          id: 22,
          clientId: 1,
          title: 'Дело по взысканию',
          description: '',
          status: 'Open',
          createdAt: '2026-04-10T10:00:00Z',
        },
      ],
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    await user.type(await screen.findByLabelText('month-range-from'), '2026-03');
    await user.type(screen.getByLabelText('month-range-to'), '2026-04');
    await user.type(screen.getByLabelText('Поиск платежей клиента'), '10000');
    await user.selectOptions(screen.getByLabelText('Статус платежей клиента'), 'overdue');
    await user.click(screen.getByRole('button', { name: 'Дело по взысканию' }));

    expect(screen.getByText('Применены фильтры:')).toBeTruthy();
    expect(screen.getByText('Поиск: 10000')).toBeTruthy();
    expect(screen.getByText('Статус: Просрочено')).toBeTruthy();
    expect(screen.getByText('Дело: Дело по взысканию')).toBeTruthy();
    expect(screen.getByText(/Период:/)).toBeTruthy();

    await waitFor(() => {
      const lastQuery = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastQuery).toEqual(
        expect.objectContaining({
          caseId: 22,
          search: '10000',
          status: 'Overdue',
          from: '2026-03-01',
          to: '2026-04-30',
        }),
      );
      const statsCall = twoTypeStatsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(statsCall).toEqual(
        expect.objectContaining({
          caseId: 22,
          search: '10000',
          statusFilter: 'Overdue',
          from: '2026-03-01',
          to: '2026-04-30',
        }),
      );
    });

    await user.click(screen.getByRole('button', { name: 'Сбросить все' }));

    await waitFor(() => {
      const lastQuery = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastQuery).toEqual(
        expect.objectContaining({
          caseId: undefined,
          search: undefined,
          status: undefined,
          from: '1900-01-01',
          to: '2100-12-31',
        }),
      );
      const statsCall = twoTypeStatsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(statsCall).toEqual(
        expect.objectContaining({
          caseId: undefined,
          search: undefined,
          statusFilter: 'All',
          from: '1900-01-01',
          to: '2100-12-31',
        }),
      );
      expect(screen.queryByText('Применены фильтры:')).toBeNull();
    });
  });

  it('passes related document filters from the payment tab to the payments hook', async () => {
    const user = userEvent.setup();
    mockRelatedDocumentLookups();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    await selectRelatedFilter('Договор', '11', user);
    await selectRelatedFilter('Счет', '22', user);
    await selectRelatedFilter('Акт', '33', user);

    await waitFor(() => {
      const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          contractId: 11,
          invoiceId: 22,
          actId: 33,
        }),
      );
    });
  });

  it('passes account filters from the client tab to the invoices hook', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="accounts" />);

    const searchInput = await screen.findByLabelText('Поиск счетов клиента');
    await user.type(searchInput, '31');
    await user.selectOptions(screen.getByLabelText('Статус счетов клиента'), 'Overdue');

    await waitFor(() => {
      const lastCall = useInvoicesMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          type: 'Income',
          search: '31',
          status: 'Overdue',
        }),
      );
    });
  });

  it('restores client account filters after reopening the card', async () => {
    const user = userEvent.setup();
    const { unmount } = render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="accounts" />);

    await user.type(await screen.findByLabelText('Поиск счетов клиента'), 'счет 77');
    await user.selectOptions(screen.getByLabelText('Статус счетов клиента'), 'Completed');

    await waitFor(() => {
      const lastCall = useInvoicesMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: 'счет 77',
          status: 'Completed',
        }),
      );
    });

    unmount();
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="accounts" />);

    expect((await screen.findByLabelText('Поиск счетов клиента') as HTMLInputElement).value).toBe('счет 77');
    await waitFor(() => {
      const lastCall = useInvoicesMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: 'счет 77',
          status: 'Completed',
        }),
      );
    });
  });

  it('passes related document filters from the account tab to the invoices hook', async () => {
    const user = userEvent.setup();
    mockRelatedDocumentLookups();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="accounts" />);

    await selectRelatedFilter('Договор', '11', user);
    await selectRelatedFilter('Акт', '33', user);
    await selectRelatedFilter('Платеж', '44', user);

    await waitFor(() => {
      const lastCall = useInvoicesMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          contractId: 11,
          actId: 33,
          paymentId: 44,
        }),
      );
    });
  });

  it('shows account filter badges and resets the invoice query state', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="accounts" />);

    await user.type(await screen.findByLabelText('date-period-from'), '2026-03-01');
    await user.type(screen.getByLabelText('date-period-to'), '2026-03-31');
    await user.type(screen.getByLabelText('Поиск счетов клиента'), '31');
    await user.selectOptions(screen.getByLabelText('Статус счетов клиента'), 'Overdue');

    expect(screen.getByText('Применены фильтры:')).toBeTruthy();
    expect(screen.getByText('Поиск: 31')).toBeTruthy();
    expect(screen.getByText('Статус: Просрочено')).toBeTruthy();
    expect(screen.getByText(/Период:/)).toBeTruthy();

    await waitFor(() => {
      const lastCall = useInvoicesMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          type: 'Income',
          search: '31',
          status: 'Overdue',
          from: '2026-03-01',
          to: '2026-03-31',
        }),
      );
    });

    await user.click(screen.getByRole('button', { name: 'Сбросить все' }));

    await waitFor(() => {
      const lastCall = useInvoicesMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          type: 'Income',
          search: undefined,
          status: undefined,
          from: undefined,
          to: undefined,
        }),
      );
      expect(screen.queryByText('Применены фильтры:')).toBeNull();
    });
  });

  it('passes act filters from the client tab to the acts hook', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="acts" />);

    const searchInput = await screen.findByLabelText('Поиск актов клиента');
    await user.type(searchInput, 'акт 15');
    await user.selectOptions(screen.getByLabelText('Статус актов клиента'), 'Signed');

    await waitFor(() => {
      const lastCall = useActsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: 'акт 15',
          status: 'Signed',
        }),
      );
    });
  });

  it('edits acts in a client-card modal without opening the act page', async () => {
    const user = userEvent.setup();
    const onOpenActPage = vi.fn();
    permissionsResult.acts.canEdit = true;
    useActsMock.mockReturnValue({
      ...emptyActsResult,
      acts: [
        {
          id: 33,
          number: 'A-33',
          title: 'Client act',
          date: '2026-04-11',
          amount: 12_500,
          status: 'Created',
          createdAt: '2026-04-11T10:00:00Z',
          updatedAt: '2026-04-11T10:00:00Z',
        } as Act,
      ],
      pagination: { total: 1, page: 1, pageSize: 10 },
    });

    render(
      <ClientDetail
        clientId={1}
        onBack={vi.fn()}
        initialSection="acts"
        onOpenActPage={onOpenActPage}
      />,
    );

    await screen.findByText('№A-33');
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));

    expect(onOpenActPage).not.toHaveBeenCalled();
    await waitFor(() => {
      const props = actModalPropsSpy.mock.calls.at(-1)?.[0] as {
        open?: boolean;
        mode?: string;
        act?: Act;
      };
      expect(props).toEqual(
        expect.objectContaining({
          open: true,
          mode: 'edit',
          act: expect.objectContaining({ id: 33 }),
        }),
      );
    });
  });

  it('shows act filter badges and resets the act query state', async () => {
    const user = userEvent.setup();
    const today = new Date();
    const expectedFrom = `${today.getFullYear()}-01-01`;
    const expectedTo = formatLocalYmd(today);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="acts" />);

    fireEvent.change(await screen.findByDisplayValue(expectedFrom), { target: { value: '2026-02-01' } });
    fireEvent.change(screen.getByDisplayValue(expectedTo), { target: { value: '2026-02-28' } });
    await user.type(screen.getByLabelText('Поиск актов клиента'), 'акт 15');
    await user.selectOptions(screen.getByLabelText('Статус актов клиента'), 'Signed');

    expect(screen.getByText('Применены фильтры:')).toBeTruthy();
    expect(screen.getByText('Поиск: акт 15')).toBeTruthy();
    expect(screen.getByText('Статус: Подписано')).toBeTruthy();
    expect(screen.getByText(/Период:/)).toBeTruthy();

    await waitFor(() => {
      const lastCall = useActsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: 'акт 15',
          status: 'Signed',
          from: '2026-02-01',
          to: '2026-02-28',
        }),
      );
    });

    await user.click(screen.getByRole('button', { name: 'Сбросить все' }));

    await waitFor(() => {
      const lastCall = useActsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: undefined,
          status: undefined,
          from: expectedFrom,
          to: expectedTo,
        }),
      );
      expect(screen.queryByText('Поиск: акт 15')).toBeNull();
      expect(screen.queryByText('Статус: Подписано')).toBeNull();
      expect(screen.getByText('Применены фильтры:')).toBeTruthy();
      expect(screen.getByText(/Период:/)).toBeTruthy();
    });
  });

  it('passes related document filters from the act tab to the acts hook', async () => {
    const user = userEvent.setup();
    mockRelatedDocumentLookups();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="acts" />);

    await selectRelatedFilter('Договор', '11', user);
    await selectRelatedFilter('Счет', '22', user);
    await selectRelatedFilter('Платеж', '44', user);

    await waitFor(() => {
      const lastCall = useActsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          contractId: 11,
          invoiceId: 22,
          paymentId: 44,
        }),
      );
    });
  });

  it('passes contract filters from the client tab to the contracts hook', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="contracts" />);

    const searchInput = await screen.findByLabelText('Поиск договоров клиента');
    await user.type(searchInput, '42/2026');

    await waitFor(() => {
      const lastCall = useContractsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: '42/2026',
        }),
      );
    });
  });

  it('passes related document filters from the contract tab to the contracts hook', async () => {
    const user = userEvent.setup();
    mockRelatedDocumentLookups();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="contracts" />);

    await selectRelatedFilter('Счет', '22', user);
    await selectRelatedFilter('Акт', '33', user);
    await selectRelatedFilter('Платеж', '44', user);

    await waitFor(() => {
      const lastCall = useContractsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          invoiceId: 22,
          actId: 33,
          paymentId: 44,
        }),
      );
    });
  });

  it('shows contract amount and related documents when the invoice carries the contract link', async () => {
    const contract = {
      id: 11,
      number: '1-18/05-26',
      title: 'Спор между клиентами',
      date: '2026-05-18',
      billingMode: 'Actual',
      status: 'Concluded',
      isOpenEnded: true,
      invoiceAmountTotal: 0,
      actAmountTotal: 0,
      paymentAmountTotal: 0,
      clients: [{ id: 1, name: 'Иванов Иван Иванович' }],
      invoiceIds: [],
      actIds: [],
      paymentIds: [],
      createdAt: '2026-05-18T00:00:00Z',
    } as Contract;

    useContractsMock.mockReturnValue({
      ...emptyContractsResult,
      contracts: [contract],
      pagination: { total: 1, page: 1, pageSize: 10 },
    });
    vi.mocked(apiService.getContracts).mockResolvedValue({
      items: [contract],
      total: 1,
      page: 1,
      pageSize: 200,
    });
    vi.mocked(apiService.getInvoices).mockResolvedValue({
      items: [
        {
          id: 154,
          invoiceEntityId: 1540,
          number: '154',
          date: '2026-05-18',
          amount: 25_000,
          type: 'Income',
          status: 'Completed',
          isPaid: true,
          contractId: 11,
          createdAt: '2026-05-18T00:00:00Z',
        } as Invoice,
      ],
      total: 1,
      page: 1,
      pageSize: 200,
    });
    vi.mocked(apiService.getActs).mockResolvedValue({
      items: [
        {
          id: 1183,
          number: '1183',
          date: '2026-05-19',
          amount: 25_000,
          invoiceId: 154,
          status: 'Created',
          createdAt: '2026-05-19T00:00:00Z',
        } as Act,
      ],
      total: 1,
      page: 1,
      pageSize: 200,
    });
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
      items: [
        {
          id: 900,
          date: '2026-05-19',
          amount: 25_000,
          invoiceId: 1540,
          documentKind: 'Payment',
        } as Payment,
      ],
      total: 1,
      page: 1,
      pageSize: 500,
      summary: {
        income: 25_000,
        expense: 0,
        balance: 25_000,
        incomeCount: 1,
        expenseCount: 0,
        total: 1,
      },
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="contracts" />);

    await screen.findByText('№1-18/05-26');
    expect(await screen.findByText('Подписан клиентом')).toBeTruthy();
    expect(await screen.findByText('Счет №154')).toBeTruthy();
    expect(await screen.findByText('Акт №1183')).toBeTruthy();
    await waitFor(() => {
      expect(
        screen.getAllByText((content) =>
          content.includes('25') && content.includes('000') && content.includes('₽'),
        ).length,
      ).toBeGreaterThan(0);
    });
  });

  it('shows document status in the client contracts table', async () => {
    useContractsMock.mockReturnValue({
      ...emptyContractsResult,
      contracts: [
        {
          id: 11,
          number: '1-18/05-26',
          title: 'Договор на сопровождение',
          date: '2026-05-18',
          billingMode: 'Fixed',
          status: 'Concluded',
          amount: 25_000,
          invoiceAmountTotal: 0,
          actAmountTotal: 0,
          paymentAmountTotal: 0,
          isOpenEnded: false,
          clients: [{ id: 1, name: 'Иванов Иван Иванович' }],
          invoiceIds: [],
          actIds: [],
          paymentIds: [],
          createdAt: '2026-05-18T00:00:00Z',
        } as Contract,
        {
          id: 12,
          number: '2-20/05-26',
          title: 'Расторгнутый договор',
          date: '2026-05-20',
          billingMode: 'Fixed',
          status: 'Terminated',
          amount: 30_000,
          invoiceAmountTotal: 0,
          actAmountTotal: 0,
          paymentAmountTotal: 0,
          isOpenEnded: false,
          clients: [{ id: 1, name: 'Иванов Иван Иванович' }],
          invoiceIds: [],
          actIds: [],
          paymentIds: [],
          createdAt: '2026-05-20T00:00:00Z',
        } as Contract,
      ],
      pagination: { total: 2, page: 1, pageSize: 10 },
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="contracts" />);

    expect(await screen.findByText('Статус документа')).toBeTruthy();
    expect(await screen.findByText('Подписан клиентом')).toBeTruthy();
    expect(await screen.findByText('Расторгнут')).toBeTruthy();
  });

  it('shows contract filter badges and resets the contract query state', async () => {
    const user = userEvent.setup();
    const today = new Date();
    const expectedFrom = `${today.getFullYear()}-01-01`;
    const expectedTo = formatLocalYmd(today);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="contracts" />);

    fireEvent.change(await screen.findByDisplayValue(expectedFrom), { target: { value: '2026-01-15' } });
    fireEvent.change(screen.getByDisplayValue(expectedTo), { target: { value: '2026-04-10' } });
    await user.type(screen.getByLabelText('Поиск договоров клиента'), '42/2026');

    expect(screen.getByText('Применены фильтры:')).toBeTruthy();
    expect(screen.getByText('Поиск: 42/2026')).toBeTruthy();
    expect(screen.getByText(/Период:/)).toBeTruthy();

    await waitFor(() => {
      const lastCall = useContractsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: '42/2026',
          from: '2026-01-15',
          to: '2026-04-10',
        }),
      );
    });

    await user.click(screen.getByRole('button', { name: 'Сбросить все' }));

    await waitFor(() => {
      const lastCall = useContractsMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(
        expect.objectContaining({
          clientId: 1,
          search: undefined,
          from: expectedFrom,
          to: expectedTo,
        }),
      );
      expect(screen.queryByText('Поиск: 42/2026')).toBeNull();
      expect(screen.getByText('Применены фильтры:')).toBeTruthy();
      expect(screen.getByText(/Период:/)).toBeTruthy();
    });
  });
});
