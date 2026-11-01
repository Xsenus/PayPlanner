import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ClientDetail } from './ClientDetail';
import type { Act, Client, Contract, Invoice, LegalEntityDetail, LegalEntityInput, Payment, PowerOfAttorney, CleanedAddress, DadataAddressSuggestion } from '../../types';
import { apiService } from '../../services/api';
import { useClientStatuses } from '../../hooks/useClientStatuses';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import * as timeZoneHook from '../../hooks/useUserTimeZone';
import { StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { documentCopyTestSession } from '../../test/documentCopySession';

const CLIENT_PROFILE_NOTES_PREFIX = '__PP_CLIENT_PROFILE_V2__:';
type LegalModalOwnerProps = { initial?: LegalEntityDetail | null; onClose: () => void; onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail> };
const legalModalOwnerSpy = vi.hoisted(() => vi.fn());
const invoiceLookupPropsSpy = vi.hoisted(() => vi.fn());
const caseLookupPropsSpy = vi.hoisted(() => vi.fn());
const poaPromptPropsSpy = vi.hoisted(() => vi.fn());
const addressFormPropsSpy = vi.hoisted(() => vi.fn());
vi.mock('./ClientAddressEntriesForm', async importOriginal => {
  const actual = await importOriginal<typeof import('./ClientAddressEntriesForm')>();
  return { ...actual, ClientAddressEntriesForm: (props: Parameters<typeof actual.ClientAddressEntriesForm>[0]) => {
    addressFormPropsSpy(props); return <actual.ClientAddressEntriesForm {...props} />;
  } };
});
vi.mock('../ui/FormPromptModal', async importOriginal => {
  const actual = await importOriginal<typeof import('../ui/FormPromptModal')>();
  return { ...actual, FormPromptModal: (props: Parameters<typeof actual.FormPromptModal>[0]) => {
    if (props.title === 'Новая доверенность' || props.title === 'Редактирование доверенности') poaPromptPropsSpy(props);
    return <actual.FormPromptModal {...props} />;
  } };
});
vi.mock('../LegalEntities/LegalEntityModal', () => ({ LegalEntityModal: (props: LegalModalOwnerProps) => { legalModalOwnerSpy(props); return null; } }));

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
  authState: { isAdmin: false, userId: 7, roleId: 1 },
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
    legalEntities: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    companyProfile: { canView: false, canCreate: false, canEdit: false, canDelete: false },
    cases: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    accounts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    acts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    contracts: { canView: true, canCreate: false, canEdit: false, canDelete: false },
    payments: { canView: true, canCreate: false, canEdit: false, canDelete: false, viewScope: 'all' },
    tasks: { canView: true, canCreate: false, canEdit: false, canDelete: false, canExport: false },
    audit: { canView: false, canCreate: false, canEdit: false, canDelete: false, canExport: false },
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
    user: { id: authState.userId, role: { id: authState.roleId } },
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
  useFormRequirements: vi.fn(() => ({ isRequired: () => false })),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => permissionsResult,
}));

vi.mock('../../hooks/useLegalEntities', () => ({
  useLegalEntities: () => emptyLegalEntitiesResult,
}));

vi.mock('../../hooks/useClientStatuses', () => ({
  useClientStatuses: vi.fn(() => ({ statuses: [] as unknown[] })),
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
    cleanAddress: vi.fn(),
    suggestClientAddresses: vi.fn(),
    getClientPowersOfAttorney: vi.fn(),
    getClientCaseFinanceSummaries: vi.fn(),
    getClientCaseExpenseDetails: vi.fn(),
    getContracts: vi.fn(),
    getInvoices: vi.fn(),
    getActs: vi.fn(),
    getPaymentsV2: vi.fn(),
    getPayment: vi.fn(),
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
  InvoiceModal: (props: unknown) => { invoiceLookupPropsSpy(props); return null; },
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
  CaseModal: (props: unknown) => { caseLookupPropsSpy(props); return null; },
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
  it('profile owner baseline: switching client hides the previous personal fields while the new read is pending', async () => {
    let finish!: (client: Client) => void;
    const next = new Promise<Client>(resolve => { finish = resolve; });
    vi.mocked(apiService.getClient).mockResolvedValueOnce(createClient()).mockReturnValueOnce(next);
    const p = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const };
    const view = render(<ClientDetail {...p} />); await screen.findAllByText('Иванов Иван Иванович');
    view.rerender(<ClientDetail {...p} clientId={2} />);
    expect(screen.queryByText('Иванов Иван Иванович')).toBeNull();
    await act(async () => finish({ ...createClient({ lastName: 'Новый' }), id: 2, name: 'Новый клиент' }));
  });
  it('profile owner baseline: a new role reloads personal data instead of retaining the previous response', async () => {
    vi.mocked(apiService.getClient).mockResolvedValueOnce(createClient()).mockResolvedValueOnce({ ...createClient({ lastName: 'Разрешённый' }), name: 'Разрешённый профиль' });
    const p = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const };
    const view = render(<ClientDetail {...p} />); await screen.findAllByText('Иванов Иван Иванович');
    authState.roleId = 2; view.rerender(<ClientDetail {...p} />);
    await waitFor(() => expect(apiService.getClient).toHaveBeenCalledTimes(2));
    expect(screen.queryByText('Иванов Иван Иванович')).toBeNull();
  });
  it('profile owner baseline: denied client viewing never fetches its personal card', async () => {
    permissionsResult.clients.canView = false;
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    await act(async () => {}); expect(apiService.getClient).not.toHaveBeenCalled();
  });
  it('profile owner baseline: a late profile save cannot notify the parent or toast under another role', async () => {
    let finish!: (client: Client) => void;
    vi.mocked(apiService.updateClient).mockReturnValueOnce(new Promise<Client>(resolve => { finish = resolve; }));
    const changed = vi.fn(); const p = { clientId: 1, onBack: vi.fn(), onClientUpdated: changed, initialSection: 'profile' as const };
    const view = render(<ClientDetail {...p} />); await screen.findAllByText('Иванов Иван Иванович');
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiService.updateClient).toHaveBeenCalledTimes(1));
    authState.roleId = 2; view.rerender(<ClientDetail {...p} />);
    await act(async () => finish(createClient()));
    expect(changed).not.toHaveBeenCalled(); expect(toastSuccessMock).not.toHaveBeenCalled();
  });
  it('profile owner current: parent render and token refresh retain the draft and a successful pending save', async () => {
    const session = documentCopyTestSession();
    try {
      const p = { clientId: 1, onBack: vi.fn(), onClientUpdated: vi.fn(), initialSection: 'profile' as const };
      const view = render(<ClientDetail {...p} />); await screen.findAllByText('Иванов Иван Иванович'); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
      fireEvent.change(screen.getByDisplayValue('Иванов'), { target: { value: 'Черновик' } });
      session.refresh(); view.rerender(<ClientDetail {...p} />); expect(screen.getByDisplayValue('Черновик')).toBeTruthy(); expect(apiService.getClient).toHaveBeenCalledTimes(1);
      let finish!: (client: Client) => void; vi.mocked(apiService.updateClient).mockReturnValueOnce(new Promise<Client>(resolve => { finish = resolve; }));
      const form = screen.getByRole('button', { name: 'Сохранить' }).closest('form')!; fireEvent.submit(form); fireEvent.submit(form);
      expect(apiService.updateClient).toHaveBeenCalledTimes(1); expect(screen.getByDisplayValue('Черновик')).toHaveProperty('disabled', true);
      session.refresh(); view.rerender(<ClientDetail {...p} />); await act(async () => finish(createClient({ lastName: 'Черновик' })));
      expect(p.onClientUpdated).toHaveBeenCalledTimes(1); expect(apiService.getClient).toHaveBeenCalledTimes(1);
      const payload = vi.mocked(apiService.updateClient).mock.calls[0][1]; expect(JSON.parse(payload.notes!.slice(CLIENT_PROFILE_NOTES_PREFIX.length)).lastName).toBe('Черновик');
    } finally { session.restore(); }
  });
  it('profile owner failure: a failed save preserves the draft and allows one explicit retry', async () => {
    vi.mocked(apiService.updateClient).mockReset().mockRejectedValueOnce(new Error('Временная ошибка профиля')).mockResolvedValueOnce(createClient({ lastName: 'Черновик' }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); await screen.findAllByText('Иванов Иван Иванович'); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.change(screen.getByDisplayValue('Иванов'), { target: { value: 'Черновик' } }); fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Временная ошибка профиля', 'Ошибка сохранения'));
    expect(screen.getByDisplayValue('Черновик')).toHaveProperty('disabled', false); expect(toastSuccessMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith('Изменения карточки клиента сохранены.', 'Клиент обновлен'));
    expect(apiService.updateClient).toHaveBeenCalledTimes(2);
  });
  it('profile owner session: replaced login blocks a mounted submit before another render', async () => {
    const session = documentCopyTestSession();
    try {
      render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); await screen.findAllByText('Иванов Иван Иванович'); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
      session.replace('login'); fireEvent.submit(screen.getByRole('button', { name: 'Сохранить' }).closest('form')!);
      expect(apiService.updateClient).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });
  it.each(['success', 'error'] as const)('profile owner read: an obsolete %s after a role change cannot restore the old profile', async outcome => {
    let finish!: (client: Client) => void; let fail!: (error: Error) => void;
    const old = new Promise<Client>((resolve, reject) => { finish = resolve; fail = reject; });
    vi.mocked(apiService.getClient).mockReturnValueOnce(old).mockResolvedValueOnce({ ...createClient({ lastName: 'Разрешённый' }), name: 'Разрешённый профиль' });
    const p = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const }; const view = render(<ClientDetail {...p} />);
    await waitFor(() => expect(apiService.getClient).toHaveBeenCalledTimes(1)); const signal = vi.mocked(apiService.getClient).mock.calls[0][1]?.signal;
    authState.roleId = 2; view.rerender(<ClientDetail {...p} />); await screen.findAllByText('Разрешённый профиль');
    await act(async () => { if (outcome === 'success') finish(createClient()); else fail(new Error('Прежняя ошибка профиля')); });
    expect(signal?.aborted).toBe(true); expect(screen.queryByText('Иванов Иван Иванович')).toBeNull(); expect(screen.queryByText('Прежняя ошибка профиля')).toBeNull();
  });
  it('profile owner retry: a forbidden read has an explicit retry and never exposes a blank editable form', async () => {
    vi.mocked(apiService.getClient).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт')).mockResolvedValueOnce(createClient());
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); await screen.findByText('Недостаточно прав для выполнения действия.');
    expect(screen.queryByRole('button', { name: 'Редактировать' })).toBeNull(); expect(apiService.getClient).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку клиента' })); await screen.findAllByText('Иванов Иван Иванович'); expect(apiService.getClient).toHaveBeenCalledTimes(2);
  });
  it('profile owner identity: a wrong returned client ID never populates personal fields', async () => {
    vi.mocked(apiService.getClient).mockResolvedValueOnce({ ...createClient(), id: 77 });
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); await screen.findByText('Сервер вернул другую карточку клиента. Повторите загрузку.');
    expect(screen.queryByText('Иванов Иван Иванович')).toBeNull(); expect(screen.queryByRole('button', { name: 'Редактировать' })).toBeNull();
  });
  it('profile owner delete: revoking the view cancels confirmation and never dispatches a delete', async () => {
    permissionsResult.clients.canDelete = true; let approve!: (value: boolean) => void; confirmMock.mockReturnValueOnce(new Promise<boolean>(resolve => { approve = resolve; }));
    const p = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const }; const view = render(<ClientDetail {...p} />); await screen.findAllByText('Иванов Иван Иванович');
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' })); fireEvent.click(screen.getByRole('button', { name: 'Удалить' }));
    const signal = confirmMock.mock.calls[0][0].signal; permissionsResult.clients.canView = false; view.rerender(<ClientDetail {...p} />);
    expect(signal.aborted).toBe(true); await act(async () => approve(true)); expect(apiService.deleteClient).not.toHaveBeenCalled(); expect(p.onBack).not.toHaveBeenCalled();
  });
  it('profile owner delete result: a late delete cannot navigate away from a different client', async () => {
    permissionsResult.clients.canDelete = true; let finish!: () => void; vi.mocked(apiService.deleteClient).mockReset().mockReturnValueOnce(new Promise<void>(resolve => { finish = resolve; }));
    const p = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const }; const view = render(<ClientDetail {...p} />); await screen.findAllByText('Иванов Иван Иванович');
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' })); fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); await waitFor(() => expect(apiService.deleteClient).toHaveBeenCalledTimes(1));
    view.rerender(<ClientDetail {...p} clientId={2} />); await act(async () => finish()); expect(p.onBack).not.toHaveBeenCalled();
  });
  it('profile owner delete current: cancelled confirmation preserves the editor and a current retry deletes once', async () => {
    permissionsResult.clients.canDelete = true; confirmMock.mockResolvedValueOnce(false).mockResolvedValue(true);
    let finish!: () => void; vi.mocked(apiService.deleteClient).mockReset().mockReturnValueOnce(new Promise<void>(resolve => { finish = resolve; }));
    const back = vi.fn(); render(<ClientDetail clientId={1} onBack={back} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.change(screen.getByDisplayValue('Иванов'), { target: { value: 'Черновик' } });
    fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); await waitFor(() => expect(screen.getByRole('button', { name: 'Удалить' })).toHaveProperty('disabled', false));
    expect(apiService.deleteClient).not.toHaveBeenCalled(); expect(screen.getByDisplayValue('Черновик')).toHaveProperty('disabled', false);
    const form = screen.getByRole('button', { name: 'Сохранить' }).closest('form')!;
    fireEvent.click(screen.getByRole('button', { name: 'Удалить' })); fireEvent.submit(form);
    await waitFor(() => expect(apiService.deleteClient).toHaveBeenCalledTimes(1)); expect(apiService.updateClient).not.toHaveBeenCalled();
    await act(async () => finish()); expect(back).toHaveBeenCalledTimes(1);
  });
  it('profile owner wrong save: another returned client ID cannot replace the draft or notify its parent', async () => {
    vi.mocked(apiService.updateClient).mockResolvedValueOnce({ ...createClient(), id: 77 });
    const updated = vi.fn(); render(<ClientDetail clientId={1} onBack={vi.fn()} onClientUpdated={updated} initialSection="profile" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' })); fireEvent.change(screen.getByDisplayValue('Иванов'), { target: { value: 'Черновик' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Сервер вернул другую карточку клиента. Повторите сохранение.', 'Ошибка сохранения'));
    expect(updated).not.toHaveBeenCalled(); expect(toastSuccessMock).not.toHaveBeenCalled(); expect(screen.getByDisplayValue('Черновик')).toHaveProperty('disabled', false);
  });
  it('profile owner local confirmation: changing role cancels deletion of an unsaved phone and its old toast', async () => {
    vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient({ phones: ['+70000000001', '+70000000002'] }) });
    let approve!: (value: boolean) => void; confirmMock.mockReturnValueOnce(new Promise<boolean>(resolve => { approve = resolve; }));
    const props = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const }; const view = render(<ClientDetail {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' })); fireEvent.click(screen.getAllByRole('button', { name: 'Удалить телефон' })[0]);
    const signal = confirmMock.mock.calls[0][0].signal; authState.roleId = 2; view.rerender(<ClientDetail {...props} />);
    expect(signal.aborted).toBe(true); await act(async () => approve(true)); expect(toastSuccessMock).not.toHaveBeenCalled(); expect(apiService.updateClient).not.toHaveBeenCalled();
  });
  it('requirements caller: client profile rules follow the current client and view access', async () => {
    const p = { onBack: vi.fn(), initialSection: 'profile' as const }; const view = render(<ClientDetail {...p} clientId={1} />); await waitFor(() => expect(vi.mocked(useFormRequirements).mock.lastCall?.[1]).toBe(true)); const first = vi.mocked(useFormRequirements).mock.lastCall;
    expect(first?.[0]).toBe('clients'); expect(first?.[1]).toBe(true); view.rerender(<ClientDetail {...p} clientId={2} />); expect(vi.mocked(useFormRequirements).mock.lastCall?.[2]?.ownerScope).not.toBe(first?.[2]?.ownerScope);
    permissionsResult.clients.canView = false; view.rerender(<ClientDetail {...p} clientId={2} />); expect(vi.mocked(useFormRequirements).mock.lastCall?.[1]).toBe(false); await act(async () => {});
  });
  it('dictionary caller: client profile status scope follows the opened client and its rights', async () => {
    const onBack = vi.fn(); const view = render(<ClientDetail clientId={1} onBack={onBack} initialSection="profile" />); const first = vi.mocked(useClientStatuses).mock.lastCall?.[0]; expect(first?.enabled).toBe(true);
    view.rerender(<ClientDetail clientId={2} onBack={onBack} initialSection="profile" />); expect(vi.mocked(useClientStatuses).mock.lastCall?.[0]?.ownerScope).not.toBe(first?.ownerScope);
    const second = vi.mocked(useClientStatuses).mock.lastCall?.[0]; permissionsResult.clients.canEdit = false; view.rerender(<ClientDetail clientId={2} onBack={onBack} initialSection="profile" />);
    expect(vi.mocked(useClientStatuses).mock.lastCall?.[0]?.ownerScope).not.toBe(second?.ownerScope); await act(async () => {});
  });
  beforeEach(() => {
    addressFormPropsSpy.mockClear(); poaPromptPropsSpy.mockClear();
    legalModalOwnerSpy.mockClear(); authState.userId = 7; authState.roleId = 1;
    invoiceLookupPropsSpy.mockClear(); caseLookupPropsSpy.mockClear();
    Object.assign(permissionsResult.legalEntities, createPermissions().legalEntities);
    Object.assign(permissionsResult.companyProfile, createPermissions().companyProfile);
    vi.mocked(apiService.getLegalEntity).mockReset(); vi.mocked(apiService.createLegalEntity).mockReset(); vi.mocked(apiService.updateLegalEntity).mockReset();
    Object.assign(permissionsResult.clients, createPermissions().clients);
    Object.assign(permissionsResult.cases, createPermissions().cases);
    Object.assign(permissionsResult.accounts, createPermissions().accounts);
    Object.assign(permissionsResult.acts, createPermissions().acts);
    Object.assign(permissionsResult.contracts, createPermissions().contracts);
    Object.assign(permissionsResult.payments, createPermissions().payments);
    Object.assign(permissionsResult.tasks, createPermissions().tasks);
    Object.assign(permissionsResult.audit, createPermissions().audit);
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
    vi.mocked(apiService.getClientBalance).mockClear();
    vi.mocked(apiService.getClientBalanceTransactions).mockClear();
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockClear();
    window.sessionStorage.clear();
    authState.isAdmin = false;

    useClientCasesMock.mockReturnValue(emptyClientCasesResult);
    useActsMock.mockReturnValue(emptyActsResult);
    useInvoicesMock.mockReturnValue(emptyInvoicesResult);
    useContractsMock.mockReturnValue(emptyContractsResult);
    usePaymentsPagedMock.mockReturnValue(emptyPaymentsResult);

    vi.mocked(apiService.getClient).mockReset().mockImplementation(async id => ({ ...createClient(), id }));
    vi.mocked(useClientStatuses).mockReturnValue({ statuses: [], loading: false, error: null, refresh: vi.fn() } as ReturnType<typeof useClientStatuses>);
    vi.mocked(apiService.getClients).mockReset().mockResolvedValue([]);
    vi.mocked(apiService.getActResponsibles).mockReset().mockResolvedValue([]);
    vi.mocked(apiService.updateClient).mockReset().mockResolvedValue(createClient());
    vi.mocked(apiService.deleteClient).mockReset().mockResolvedValue(undefined);
    vi.mocked(apiService.cleanAddress).mockReset().mockResolvedValue({ result: 'Уточнённый адрес', cityWithType: 'г Новый', house: '42' });
    vi.mocked(apiService.suggestClientAddresses).mockReset().mockResolvedValue([{ value: 'Подсказка адреса', cityWithType: 'г Подсказка' }]);
    vi.mocked(apiService.getClientPowersOfAttorney).mockReset().mockResolvedValue([]);
    vi.mocked(apiService.createClientPowerOfAttorney).mockReset();
    vi.mocked(apiService.updateClientPowerOfAttorney).mockReset();
    vi.mocked(apiService.deleteClientPowerOfAttorney).mockReset();
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
    vi.mocked(apiService.getPayment).mockReset();
    vi.mocked(apiService.createClientPowerOfAttorney).mockResolvedValue(createPowerOfAttorney());
    vi.mocked(apiService.updateClientPowerOfAttorney).mockResolvedValue(createPowerOfAttorney());
    vi.mocked(apiService.deleteClientPowerOfAttorney).mockResolvedValue(undefined);
    vi.mocked(apiService.getOksoDictionary).mockReset().mockResolvedValue([]);
    vi.mocked(apiService.getOkpdtrDictionary).mockReset().mockResolvedValue([]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset().mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 20,
    });
  });

  it('PAY-32 review: does not invent an age for an impossible birth date', async () => {
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({ birthDate: '2000-02-31' }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    const summary = await screen.findByLabelText('Краткие сведения о клиенте');
    expect(summary.textContent).not.toMatch(/\(\d+ (год|года|лет)\)/);
  });

  it('PAY-32 review: keeps the birth date and computes age in the selected time zone at midnight', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-12T00:30:00Z'));
    const timeZoneSpy = vi.spyOn(timeZoneHook, 'useUserTimeZone').mockReturnValue('America/Los_Angeles');
    try {
      vi.mocked(apiService.getClient).mockResolvedValue(createClient({ birthDate: '2000-10-12' }));
      render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
      const summary = await screen.findByLabelText('Краткие сведения о клиенте');
      expect(within(summary).getByText('12.10.2000 (25 лет)')).toBeTruthy();
    } finally {
      timeZoneSpy.mockRestore();
      vi.useRealTimers();
    }
  });

  it('PAY-32: shows short client facts in the header and service facts next to the section menu', async () => {
    vi.mocked(apiService.getClient).mockResolvedValue({
      ...createClient({ birthDate: '2000-01-02' }),
      phone: '+7 893 467-48-83',
      email: 'anna@example.test',
      address: 'г. Новокузнецк',
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { name: 'Карточка клиента' });
    const summary = await screen.findByLabelText('Краткие сведения о клиенте');
    expect(within(summary).getByText('+7 893 467-48-83')).toBeTruthy();
    expect(within(summary).getByText('anna@example.test')).toBeTruthy();
    expect(within(summary).getByText('г. Новокузнецк')).toBeTruthy();
    expect(within(summary).getByText(/02\.01\.2000 \(\d+ (год|года|лет)\)/)).toBeTruthy();

    const menu = screen.getByRole('navigation', { name: 'Разделы клиента' });
    expect(within(menu).getByRole('button', { name: 'Личные данные' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Основная информация' })).toBeTruthy();
    const service = screen.getByLabelText('Служебные сведения');
    expect(within(service).getByText('№ клиента')).toBeTruthy();
    expect(within(service).getByText('Дата создания')).toBeTruthy();
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

  it('shared lookup baseline: an admin responsible denial waits for an explicit retry instead of looping', async () => {
    authState.isAdmin = true;
    vi.mocked(apiService.getActResponsibles).mockReset().mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт')).mockReturnValue(new Promise(() => {}));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    await screen.findByRole('button', { name: 'Редактировать' });
    await act(async () => {});
    expect(apiService.getActResponsibles).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Повторить загрузку ответственных' })).toBeTruthy();
  });

  it('shared lookup baseline: a missing status catalogue keeps the actual selected ID and name', async () => {
    vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient(), clientStatusId: 24, clientStatus: { id: 24, name: 'Сохранённый статус', description: '', colorHex: '#64748b', createdAt: '2026-11-01T00:00:00Z', isActive: true } });
    vi.mocked(useClientStatuses).mockReturnValueOnce({ statuses: [], loading: true, error: null, refresh: vi.fn() } as ReturnType<typeof useClientStatuses>);
    vi.mocked(useClientStatuses).mockReturnValue({ statuses: [], loading: true, error: null, refresh: vi.fn() } as ReturnType<typeof useClientStatuses>);
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    await screen.findByRole('button', { name: 'Редактировать' });
    const select = document.querySelector<HTMLSelectElement>('select[name="clientStatusId"]')!;
    expect(select.value).toBe('24');
    expect(select.selectedOptions[0].textContent).toBe('Сохранённый статус');
  });

  it('shared lookup retry: responsible denial preserves the profile and retries only once on explicit action', async () => {
    authState.isAdmin = true;
    vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient(), responsibleUserId: 5, responsibleUserName: 'Сохранённый ответственный' });
    let finish!: (rows: Awaited<ReturnType<typeof apiService.getActResponsibles>>) => void;
    vi.mocked(apiService.getActResponsibles).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт')).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.change(screen.getByDisplayValue('Иванов'), { target: { value: 'Сохраняемый черновик' } });
    const retry = await screen.findByRole('button', { name: 'Повторить загрузку ответственных' });
    expect(screen.getByRole('combobox', { name: 'Ответственный' })).toHaveProperty('disabled', true);
    fireEvent.click(retry); fireEvent.click(retry);
    expect(apiService.getActResponsibles).toHaveBeenCalledTimes(2); expect(apiService.getClients).not.toHaveBeenCalled();
    await act(async () => finish([]));
    expect(screen.queryByRole('button', { name: 'Повторить загрузку ответственных' })).toBeNull();
    expect(screen.getByDisplayValue('Сохраняемый черновик')).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Ответственный' })).toHaveProperty('disabled', false);
    expect(apiService.getActResponsibles).toHaveBeenCalledTimes(2);
  });

  it('shared lookup role: old responsible rows are aborted and cannot enter the next role catalogue', async () => {
    authState.isAdmin = true;
    let finish!: (rows: Awaited<ReturnType<typeof apiService.getActResponsibles>>) => void;
    vi.mocked(apiService.getActResponsibles).mockReturnValueOnce(new Promise(resolve => { finish = resolve; })).mockResolvedValueOnce([{ id: 8, fullName: 'Текущий сотрудник' }]);
    const props = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const }; const view = render(<ClientDetail {...props} />);
    await waitFor(() => expect(apiService.getActResponsibles).toHaveBeenCalledOnce()); const signal = vi.mocked(apiService.getActResponsibles).mock.calls[0][0]?.signal;
    authState.roleId = 2; view.rerender(<ClientDetail {...props} />); await waitFor(() => expect(apiService.getActResponsibles).toHaveBeenCalledTimes(2));
    expect(signal?.aborted).toBe(true); await act(async () => finish([{ id: 7, fullName: 'Прежний сотрудник' }]));
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    const picker = screen.getByRole('combobox', { name: 'Ответственный' }); expect(within(picker).getByRole('option', { name: 'Текущий сотрудник' })).toBeTruthy(); expect(within(picker).queryByRole('option', { name: 'Прежний сотрудник' })).toBeNull();
  });

  it('shared lookup invoice empty: a valid empty client list is loaded once and does not loop', async () => {
    permissionsResult.accounts.canCreate = true;
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Добавить счёт' })); await act(async () => {});
    expect(apiService.getClients).toHaveBeenCalledOnce(); expect(apiService.getActResponsibles).toHaveBeenCalledOnce();
    expect(invoiceLookupPropsSpy.mock.lastCall![0]).toMatchObject({ lookupsLoading: false, lookupsError: null, clients: [expect.objectContaining({ id: 1 })] });
  });

  it('shared lookup invoice partial: retry keeps successful responsibles and only reloads failed clients', async () => {
    permissionsResult.accounts.canCreate = true;
    vi.mocked(apiService.getActResponsibles).mockResolvedValue([{ id: 7, fullName: 'Текущий ответственный' }]);
    let finish!: (rows: Client[]) => void;
    vi.mocked(apiService.getClients).mockRejectedValueOnce(new Error('HTTP 403 - Клиенты закрыты')).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Добавить счёт' }));
    await waitFor(() => expect(invoiceLookupPropsSpy.mock.lastCall![0].lookupsError).toContain('Недостаточно прав'));
    const retry = invoiceLookupPropsSpy.mock.lastCall![0].onRetryLookups; act(() => { retry(); retry(); });
    expect(apiService.getClients).toHaveBeenCalledTimes(2); expect(apiService.getActResponsibles).toHaveBeenCalledOnce();
    await act(async () => finish([{ ...createClient(), id: 2, name: 'Другой доступный клиент' }]));
    expect(invoiceLookupPropsSpy.mock.lastCall![0]).toMatchObject({ lookupsLoading: false, lookupsError: null, responsibles: [{ id: 7, fullName: 'Текущий ответственный' }] });
  });

  it('shared lookup client denial: financial access does not query the client catalogue without client view', async () => {
    permissionsResult.clients.canView = false; permissionsResult.accounts.canCreate = true;
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="accounts" />);
    fireEvent.click(await screen.findByRole('button', { name: 'Добавить счёт' })); await act(async () => {});
    expect(apiService.getClients).not.toHaveBeenCalled(); expect(apiService.getClient).not.toHaveBeenCalled();
    expect(apiService.getActResponsibles).toHaveBeenCalledOnce();
  });

  it('shared lookup closed invoice: closing aborts the page and a held retry cannot restart the closed form', async () => {
    permissionsResult.accounts.canCreate = true;
    let finish!: (rows: Client[]) => void; vi.mocked(apiService.getClients).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Добавить счёт' }));
    await waitFor(() => expect(apiService.getClients).toHaveBeenCalledOnce()); const signal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal;
    const props = invoiceLookupPropsSpy.mock.lastCall![0]; act(() => props.onClose()); expect(signal?.aborted).toBe(true);
    await act(async () => { await props.onRetryLookups(); finish([{ ...createClient(), id: 99, name: 'Прежний результат' }]); }); expect(apiService.getClients).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole('button', { name: 'Добавить счёт' })); await act(async () => {});
    expect(apiService.getClients).toHaveBeenCalledTimes(2); expect(invoiceLookupPropsSpy.mock.lastCall![0].clients.some((row: Client) => row.id === 99)).toBe(false);
  });

  it('shared lookup case: the case caller receives an independent responsible error and explicit retry', async () => {
    permissionsResult.cases.canCreate = true;
    vi.mocked(apiService.getActResponsibles).mockRejectedValueOnce(new Error('HTTP 403 - Ответственные закрыты')).mockResolvedValueOnce([]);
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Добавить дело' }));
    await waitFor(() => expect(caseLookupPropsSpy.mock.lastCall![0].responsiblesError).toContain('Недостаточно прав'));
    expect(apiService.getClients).not.toHaveBeenCalled(); await act(async () => caseLookupPropsSpy.mock.lastCall![0].onRetryResponsibles());
    expect(apiService.getActResponsibles).toHaveBeenCalledTimes(2); expect(caseLookupPropsSpy.mock.lastCall![0]).toMatchObject({ responsiblesLoading: false, responsiblesError: null });
  });

  it('shared lookup status retry: one explicit refresh preserves the current draft and selected ID', async () => {
    let finish!: () => void; const refresh = vi.fn(() => new Promise<void>(resolve => { finish = resolve; }));
    vi.mocked(useClientStatuses).mockReturnValue({ statuses: [], loading: false, error: 'HTTP 403 - Статусы закрыты', refresh } as ReturnType<typeof useClientStatuses>);
    vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient(), clientStatusId: 24 });
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.change(screen.getByDisplayValue('Иванов'), { target: { value: 'Статусы не стирают ввод' } });
    const select = document.querySelector<HTMLSelectElement>('select[name="clientStatusId"]')!; expect(select.value).toBe('24'); expect(select.disabled).toBe(true);
    const retry = screen.getByRole('button', { name: 'Повторить загрузку статусов' }); fireEvent.click(retry); fireEvent.click(retry); expect(refresh).toHaveBeenCalledOnce();
    await act(async () => finish()); expect(select.value).toBe('24'); expect(screen.getByDisplayValue('Статусы не стирают ввод')).toBeTruthy();
  });

  it.each(['responsibles', 'clients'])('shared lookup malformed: invalid %s rows stop once and expose an explicit retry', async kind => {
    permissionsResult.accounts.canCreate = true;
    if (kind === 'responsibles') vi.mocked(apiService.getActResponsibles).mockResolvedValue([{ id: 7, fullName: 'Один' }, { id: 7, fullName: 'Дубль' }]);
    else vi.mocked(apiService.getClients).mockResolvedValue([{ ...createClient(), id: 0 }]);
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Добавить счёт' }));
    await waitFor(() => expect(invoiceLookupPropsSpy.mock.lastCall![0].lookupsError).toContain('Не удалось подтвердить данные справочника'));
    await act(async () => {}); expect(apiService.getClients).toHaveBeenCalledOnce(); expect(apiService.getActResponsibles).toHaveBeenCalledOnce();
  });

  it('shared lookup status boundary: an obsolete option cannot replace the selected status in the saved profile', async () => {
    const status = { id: 24, name: 'Текущий статус', description: '', colorHex: '#64748b', createdAt: '2026-11-01T00:00:00Z', isActive: true };
    vi.mocked(useClientStatuses).mockReturnValue({ statuses: [status], loading: false, error: null, refresh: vi.fn() } as ReturnType<typeof useClientStatuses>);
    vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient(), clientStatusId: 24, clientStatus: status });
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    const select = document.querySelector<HTMLSelectElement>('select[name="clientStatusId"]')!;
    const option = document.createElement('option'); option.value = '99'; select.append(option); fireEvent.change(select, { target: { value: '99' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(apiService.updateClient).toHaveBeenCalledOnce());
    expect(vi.mocked(apiService.updateClient).mock.calls[0][1].clientStatusId).toBe(24);
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

  it('associates individual profile labels with editable fields', async () => {
    const user = userEvent.setup();
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    const mainInfo = await screen.findByRole('region', { name: 'Основная информация' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    for (const name of ['Фамилия', 'Имя', 'Отчество', 'ИНН', 'СНИЛС']) {
      const input = within(mainInfo).getByRole('textbox', { name: new RegExp(`^${name}`) });
      const label = within(mainInfo).getByText(new RegExp(`^${name}`), { selector: 'label' });
      await user.click(label);
      expect(document.activeElement).toBe(input);
    }
  });

  it('labels and opens the individual profile selectors from their visible labels', async () => {
    const user = userEvent.setup();
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    const mainInfo = await screen.findByRole('region', { name: 'Основная информация' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    for (const name of ['Пол', 'Семейное положение']) {
      const selector = within(mainInfo).getByRole('combobox', { name: new RegExp(`^${name}\\.`) });
      await user.click(within(mainInfo).getByText(name, { selector: 'label' }));
      expect(selector.getAttribute('aria-expanded')).toBe('true');
      await user.keyboard('{Escape}');
      expect(selector.getAttribute('aria-expanded')).toBe('false');
    }
  });

  it('selects and saves a historical birth date using an accessible calendar label', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({ birthDate: '2000-01-02' }));
    vi.mocked(apiService.updateClient).mockResolvedValue(createClient({ birthDate: '1945-03-15' }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    await screen.findByRole('region', { name: 'Основная информация' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    const mainInfo = screen.getByRole('region', { name: 'Основная информация' });
    await user.click(within(mainInfo).getByText('Дата рождения', { selector: 'label' }));
    expect(document.activeElement).toBe(within(mainInfo).getByLabelText('Дата рождения'));
    await user.click(screen.getByRole('button', { name: 'Выбрать: Дата рождения' }));
    await user.click(screen.getByRole('button', { name: 'Выбрать месяц и год' }));
    const year = screen.getByRole('spinbutton', { name: 'Год' });
    await user.clear(year);
    await user.type(year, '1945');
    await user.click(screen.getByRole('button', { name: 'Март' }));
    await user.click(screen.getByRole('button', { name: '15.03.1945' }));
    await user.click(screen.getByRole('button', { name: 'Применить' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiService.updateClient).toHaveBeenCalled());
    const [, payload] = vi.mocked(apiService.updateClient).mock.calls.at(-1) ?? [];
    const notes = (payload as { notes: string }).notes;
    expect(JSON.parse(notes.slice(CLIENT_PROFILE_NOTES_PREFIX.length))).toEqual(expect.objectContaining({ birthDate: '1945-03-15' }));
  });

  it('opens the client profile in readonly mode until editing is requested', async () => {
    const user = userEvent.setup();
    const phones = ['+79990000001', '+79990000002'];
    const emails = ['long-address-without-spaces-for-the-client-profile@example.test', 'second@example.test'];
    const updateCallsBefore = vi.mocked(apiService.updateClient).mock.calls.length;
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({
      phones,
      emails,
      birthDate: '1985-03-15',
      gender: 'male',
      maritalStatus: 'married',
      inn: '123456789012',
      snils: '123-456-789 00',
    }));

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    const mainInfo = await screen.findByRole('region', { name: 'Основная информация' });
    expect(await within(mainInfo).findByText('Иванов Иван Иванович')).toBeTruthy();
    for (const value of ['15.03.1985', 'Мужской', 'В браке', '123456789012', '123-456-789 00']) {
      expect(within(mainInfo).getByText(value)).toBeTruthy();
    }
    expect(within(mainInfo).queryByRole('textbox')).toBeNull();
    expect(within(mainInfo).queryByRole('combobox')).toBeNull();
    const contacts = screen.getByLabelText('Телефоны и email');
    for (const value of [...phones, ...emails]) {
      expect(within(contacts).getByText(value)).toBeTruthy();
    }
    expect(within(contacts).queryByRole('textbox')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Удалить телефон' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Удалить email' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Редактировать' }));

    const phoneInput = screen.getByDisplayValue(phones[0]);
    const emailInput = screen.getByDisplayValue(emails[0]);
    expect((phoneInput as HTMLInputElement).disabled).toBe(false);
    expect((emailInput as HTMLInputElement).disabled).toBe(false);
    expect(screen.getByDisplayValue(phones[1])).toBeTruthy();
    expect(screen.getByDisplayValue(emails[1])).toBeTruthy();
    await user.clear(phoneInput);
    await user.type(phoneInput, '+79991111111');
    await user.clear(emailInput);
    await user.type(emailInput, 'draft@example.test');

    const lastNameInput = await within(mainInfo).findByDisplayValue('Иванов');
    expect((lastNameInput as HTMLInputElement).disabled).toBe(false);
    await user.clear(lastNameInput);
    await user.type(lastNameInput, 'Петров');
    expect(screen.getByDisplayValue('Петров')).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Отмена' }));

    expect(await within(mainInfo).findByText('Иванов Иван Иванович')).toBeTruthy();
    expect(within(mainInfo).queryByRole('textbox')).toBeNull();
    expect(screen.queryByDisplayValue('Петров')).toBeNull();
    const restoredContacts = screen.getByLabelText('Телефоны и email');
    for (const value of [...phones, ...emails]) {
      expect(within(restoredContacts).getByText(value)).toBeTruthy();
    }
    expect(within(restoredContacts).queryByText('draft@example.test')).toBeNull();
    expect(within(restoredContacts).queryByText('+79991111111')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
    expect(vi.mocked(apiService.updateClient).mock.calls.length).toBe(updateCallsBefore);
  });

  it('shows passport fields as text and restores them after cancelling an edit', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({
      passportEntries: [{ series: '36 19', number: '664580', issuedBy: 'Отдел выдачи', issueDate: '2019-06-15', departmentCode: '630-001', birthPlacePassport: 'Самара', birthPlaceFias: 'Самарская область, Самара', validFrom: '', validTo: '' }],
    }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    const passports = await screen.findByLabelText('Паспорта');
    expect(await within(passports).findByText('664580')).toBeTruthy();
    expect(within(passports).getByText('Самарская область, Самара')).toBeTruthy();
    expect(within(passports).getByText('15.06.2019')).toBeTruthy();
    expect(within(passports).queryByRole('textbox')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    const number = screen.getByDisplayValue('664580');
    await user.clear(number);
    await user.type(number, '123456');
    await user.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(within(await screen.findByLabelText('Паспорта')).getByText('664580')).toBeTruthy();
    expect(within(screen.getByLabelText('Паспорта')).getByText('Самарская область, Самара')).toBeTruthy();
    expect(screen.queryByDisplayValue('123456')).toBeNull();
  });

  it('shows birth certificates and military records as text and preserves them on cancel', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({
      birthCertificateEntries: [{ series: 'АБ', number: '123456', issueDate: '2018-04-03', actRecordNumber: 'Р-42', childFullName: 'Иванов Сын', issuedBy: 'Отдел ЗАГС' }],
      militaryIdEntries: [{ series: 'ВБ', number: '654321', issueDate: '2015-02-12', rank: 'Рядовой', category: 'Первая', fitnessCategory: 'А', vusCode: '123456', issuedBy: 'Комиссариат', militaryCommissariat: 'Самара' }],
    }));
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    const birth = await screen.findByRole('region', { name: 'Свидетельства о рождении' });
    const military = screen.getByRole('region', { name: 'Военные билеты' });
    expect(await within(birth).findByText('Р-42')).toBeTruthy();
    expect(within(birth).getByText('03.04.2018')).toBeTruthy();
    expect(within(military).getByText('Рядовой')).toBeTruthy();
    expect(within(military).getByText('12.02.2015')).toBeTruthy();
    expect(within(birth).queryByRole('textbox')).toBeNull();
    expect(within(military).queryByRole('textbox')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    const record = screen.getByDisplayValue('Р-42');
    await user.clear(record);
    await user.type(record, 'Р-99');
    await user.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(within(await screen.findByRole('region', { name: 'Свидетельства о рождении' })).getByText('Р-42')).toBeTruthy();
    expect(screen.queryByDisplayValue('Р-99')).toBeNull();
  });

  it('shows complete notes as readonly text and preserves their explicit edit payload', async () => {
    const user = userEvent.setup();
    const updateCallsBefore = vi.mocked(apiService.updateClient).mock.calls.length;
    const comment = 'Комментарий из нескольких строк\nВторая строка\nТретья строка\nЧетвёртая строка';
    const legacyNotes = 'Историческая запись\n<script>Это текст, а не код</script>';
    const editedComment = 'Уточнённый комментарий\nСохранённый перенос строки';
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({ comment, legacyNotes }));
    vi.mocked(apiService.updateClient).mockResolvedValue(createClient({ comment: editedComment, legacyNotes }));
    const { container } = render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    const notes = await screen.findByLabelText('Заметки клиента');
    expect([...notes.querySelectorAll('dd')].map((element) => element.textContent)).toEqual([comment, legacyNotes]);
    expect(within(notes).queryByRole('textbox')).toBeNull();
    expect(notes.querySelector('script')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    const commentInput = container.querySelector<HTMLTextAreaElement>('textarea[name="comment"]');
    const legacyInput = container.querySelector<HTMLTextAreaElement>('textarea[name="legacyNotes"]');
    expect(commentInput?.value).toBe(comment);
    expect(commentInput?.disabled).toBe(false);
    expect(legacyInput?.value).toBe(legacyNotes);
    await user.clear(commentInput!);
    await user.type(commentInput!, editedComment);
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiService.updateClient).toHaveBeenCalledTimes(updateCallsBefore + 1));
    const [id, payload] = vi.mocked(apiService.updateClient).mock.calls.at(-1) ?? [];
    expect(id).toBe(1);
    const serialized = (payload as { notes: string }).notes;
    expect(serialized.startsWith(CLIENT_PROFILE_NOTES_PREFIX)).toBe(true);
    expect(JSON.parse(serialized.slice(CLIENT_PROFILE_NOTES_PREFIX.length))).toEqual(expect.objectContaining({
      kind: 'individual', lastName: 'Иванов', firstName: 'Иван', middleName: 'Иванович', comment: editedComment, legacyNotes,
    }));
    const savedNotes = await screen.findByLabelText('Заметки клиента');
    expect([...savedNotes.querySelectorAll('dd')].map((element) => element.textContent)).toEqual([editedComment, legacyNotes]);
    expect(within(savedNotes).queryByRole('textbox')).toBeNull();
  });

  it('preserves edited notes after a save failure and sends the same text on retry', async () => {
    const user = userEvent.setup();
    const updatedComment = 'Сохранить после повтора';
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({ comment: 'Исходная заметка' }));
    vi.mocked(apiService.updateClient)
      .mockRejectedValueOnce(new Error('Временный сбой'))
      .mockResolvedValueOnce(createClient({ comment: updatedComment }));
    const { container } = render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByText('Исходная заметка');
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    const comment = container.querySelector<HTMLTextAreaElement>('textarea[name="comment"]')!;
    await user.clear(comment);
    await user.type(comment, updatedComment);
    const callsBefore = vi.mocked(apiService.updateClient).mock.calls.length;
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiService.updateClient).toHaveBeenCalledTimes(callsBefore + 1));
    expect((container.querySelector<HTMLTextAreaElement>('textarea[name="comment"]'))?.value).toBe(updatedComment);
    expect(screen.getByRole('button', { name: 'Сохранить' })).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(apiService.updateClient).toHaveBeenCalledTimes(callsBefore + 2));
    const payload = vi.mocked(apiService.updateClient).mock.calls.at(-1)?.[1] as { notes: string };
    expect(JSON.parse(payload.notes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)).comment).toBe(updatedComment);
    expect(await screen.findByText(updatedComment)).toBeTruthy();
  });

  it('shows empty contact text without exposing edit controls to a read-only role', async () => {
    Object.assign(permissionsResult.clients, { canEdit: false });
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({ phones: [], emails: [] }));

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    const contacts = await screen.findByLabelText('Телефоны и email');
    expect(within(contacts).getAllByText('Не указано')).toHaveLength(2);
    expect(within(contacts).queryByRole('textbox')).toBeNull();
    const notes = screen.getByLabelText('Заметки клиента');
    expect(within(notes).getAllByText('Не указано')).toHaveLength(2);
    expect(within(notes).queryByRole('textbox')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Редактировать' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Удалить телефон' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Удалить email' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('shows compact labels under client quick action buttons', async () => {
    Object.assign(permissionsResult.clients, { canCreate: true });
    Object.assign(permissionsResult.cases, { canCreate: true });
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
    expect(screen.queryByRole('button', { name: /История баланса/ })).toBeNull();
    await waitFor(() => {
      const lastCall = usePaymentsPagedMock.mock.calls.at(-1)?.[0] as Record<string, unknown>;
      expect(lastCall).toEqual(expect.objectContaining({ enabled: false, clientId: 1 }));
    });
    expect(apiService.getClientBalance).not.toHaveBeenCalled();
    expect(apiService.getClientCaseFinanceSummaries).not.toHaveBeenCalled();
    expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
  });

  it('does not request or show the shared advance with limited payment visibility', async () => {
    Object.assign(permissionsResult.payments, { canView: true, viewScope: 'own' });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { name: 'Иванов Иван Иванович' });
    expect(screen.queryByRole('button', { name: /История баланса/ })).toBeNull();
    expect(apiService.getClientBalance).not.toHaveBeenCalled();
    expect(apiService.getClientBalanceTransactions).not.toHaveBeenCalled();
  });

  it('hides client case actions and keeps the case section disabled without case rights', async () => {
    Object.assign(permissionsResult.clients, { canView: true, canCreate: true });
    Object.assign(permissionsResult.cases, {
      canView: false,
      canCreate: true,
      canEdit: true,
      canDelete: true,
    });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="cases" />);

    await screen.findByRole('heading', { name: 'Иванов Иван Иванович' });

    expect(screen.queryByRole('button', { name: 'Добавить дело' })).toBeNull();
    await waitFor(() => {
      expect(useClientCasesMock).toHaveBeenCalledWith(1, expect.objectContaining({ enabled: false }));
    });
    expect(apiService.getClientCaseFinanceSummaries).not.toHaveBeenCalled();
  });

  it('hides client finance create shortcuts when create is allowed but view is closed', async () => {
    Object.assign(permissionsResult.clients, { canCreate: true });
    Object.assign(permissionsResult.accounts, { canView: false, canCreate: true });
    Object.assign(permissionsResult.acts, { canView: false, canCreate: true });
    Object.assign(permissionsResult.contracts, { canView: false, canCreate: true });

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('button', { name: 'Личные данные' });

    expect(screen.queryByRole('button', { name: /Добавить сч/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Добавить акт/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /Добавить договор/ })).toBeNull();
  });

  it('opens balance journal and audit from compact header actions', async () => {
    const user = userEvent.setup();
    permissionsResult.audit.canView = true;
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

    // Раскрытие блока «Дополнительная информация» само по себе ничего не загружает.
    await user.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    expect(apiService.getOksoDictionary).not.toHaveBeenCalled();
    expect(apiService.getOkpdtrDictionary).not.toHaveBeenCalled();
    expect(apiService.getClientPowersOfAttorney).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Образование/ }));
    await waitFor(() => {
      expect(apiService.getOksoDictionary).toHaveBeenCalledTimes(1);
    });
    expect(apiService.getOksoDictionary).toHaveBeenLastCalledWith(
      { search: undefined, limit: 30 },
      { signal: expect.any(AbortSignal) },
    );
    expect(apiService.getOkpdtrDictionary).not.toHaveBeenCalled();
    expect(apiService.getClientPowersOfAttorney).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Профессии/ }));
    await waitFor(() => {
      expect(apiService.getOkpdtrDictionary).toHaveBeenCalledTimes(1);
    });
    expect(apiService.getOkpdtrDictionary).toHaveBeenLastCalledWith(
      { search: undefined, limit: 30 },
      { signal: expect.any(AbortSignal) },
    );
    expect(apiService.getClientPowersOfAttorney).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: /Доверенности/ }));
    await waitFor(() => {
      expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledWith(1, { signal: expect.any(AbortSignal) });
    });
  });

  it('searches large profile dictionaries after debounce and cancels stale requests', async () => {
    const user = userEvent.setup();
    vi.mocked(apiService.getOksoDictionary).mockResolvedValue([
      { id: 1, code: '40.03.01', name: 'Юриспруденция' },
    ]);
    vi.mocked(apiService.getOkpdtrDictionary).mockResolvedValue([
      { id: 2, code: '20662', name: 'Юрисконсульт' },
    ]);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    await screen.findByRole('heading', { name: 'Карточка клиента' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    await user.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    await user.click(screen.getByRole('button', { name: /Образование/ }));
    await waitFor(() => expect(apiService.getOksoDictionary).toHaveBeenCalledTimes(1));
    const firstOksoSignal = vi.mocked(apiService.getOksoDictionary).mock.calls[0][1]?.signal;
    const oksoInput = screen.getAllByPlaceholderText('Код ОКСО')[0];
    await user.clear(oksoInput);
    await user.type(oksoInput, '40.03');
    await waitFor(() => {
      expect(apiService.getOksoDictionary).toHaveBeenLastCalledWith(
        { search: '40.03', limit: 30 },
        { signal: expect.any(AbortSignal) },
      );
    });
    expect(firstOksoSignal?.aborted).toBe(true);
    expect(document.querySelector('option[value="40.03.01"]')).toBeTruthy();

    await user.click(screen.getByRole('button', { name: /Профессии/ }));
    await waitFor(() => expect(apiService.getOkpdtrDictionary).toHaveBeenCalledTimes(1));
    const firstOkpdtrSignal = vi.mocked(apiService.getOkpdtrDictionary).mock.calls[0][1]?.signal;
    const okpdtrInput = screen.getAllByPlaceholderText('Код ОКПДТР')[0];
    await user.clear(okpdtrInput);
    await user.type(okpdtrInput, '20662');
    await waitFor(() => {
      expect(apiService.getOkpdtrDictionary).toHaveBeenLastCalledWith(
        { search: '20662', limit: 30 },
        { signal: expect.any(AbortSignal) },
      );
    });
    expect(firstOkpdtrSignal?.aborted).toBe(true);
    expect(document.querySelector('option[value="20662"]')).toBeTruthy();
  });

  const linkedLookupEntity = { id: 21, shortName: 'Текущая выбранная компания', fullName: 'Текущая выбранная компания', clients: [], clientsCount: 0, createdAt: '2026-11-01T00:00:00Z', isOwnCompany: false } as LegalEntityDetail;
  function linkedLookupClient() { return { ...createClient({ kind: 'legal' }), legalEntityId: 21, legalEntity: linkedLookupEntity }; }
  async function editLinkedLookup() {
    vi.mocked(apiService.getClient).mockResolvedValue(linkedLookupClient());
    const props = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const };
    const view = render(<ClientDetail {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    return { view, props };
  }
  it('company lookup baseline: a role without legal view keeps the linked ID without querying the catalogue', async () => {
    permissionsResult.legalEntities.canView = false;
    await editLinkedLookup(); await act(async () => {});
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect(document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')?.value).toBe('21');
  });
  it('company lookup baseline: changing the query aborts the obsolete page request', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getLegalEntitiesPaged>>) => void;
    vi.mocked(apiService.getLegalEntitiesPaged).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    const { view } = await editLinkedLookup(); await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    const signal = vi.mocked(apiService.getLegalEntitiesPaged).mock.calls[0][1]?.signal;
    fireEvent.change(screen.getByLabelText('Поиск юридического лица клиента'), { target: { value: 'новый поиск' } });
    await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(2));
    expect(signal?.aborted).toBe(true); await act(async () => finish({ items: [], total: 0, page: 1, pageSize: 20 })); view.unmount();
  });
  it('company caller baseline: legal creation permission controls both full and quick creation', async () => {
    await editLinkedLookup();
    expect(screen.getByRole('button', { name: 'Создать юрлицо' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Быстрое создание' })).toHaveProperty('disabled', true);
  });
  it('company caller baseline: viewing requisites without legal edit opens a readonly company', async () => {
    vi.mocked(apiService.getLegalEntity).mockResolvedValue(linkedLookupEntity);
    await editLinkedLookup(); fireEvent.click(screen.getByRole('button', { name: 'Реквизиты' }));
    await waitFor(() => expect(legalModalOwnerSpy).toHaveBeenCalled()); expect(legalModalOwnerSpy.mock.lastCall![0].allowEdit).toBe(false);
  });
  it('company caller baseline: a late quick creation cannot toast after a different role takes over', async () => {
    permissionsResult.legalEntities.canCreate = true;
    let finish!: (entity: LegalEntityDetail) => void; vi.mocked(apiService.createLegalEntity).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    const { view, props } = await editLinkedLookup(); fireEvent.click(screen.getByRole('button', { name: 'Быстрое создание' }));
    fireEvent.change(screen.getByRole('textbox', { name: /Название организации/ }), { target: { value: 'Новая компания' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать' })); await waitFor(() => expect(apiService.createLegalEntity).toHaveBeenCalledTimes(1));
    authState.roleId = 2; view.rerender(<ClientDetail {...props} />);
    await act(async () => finish({ ...linkedLookupEntity, id: 22 })); expect(toastSuccessMock).not.toHaveBeenCalled();
  });

  it('company lookup current: denial removes other rows and explicit retry preserves query and the linked company', async () => {
    const other = { ...linkedLookupEntity, id: 22, shortName: 'Другой контрагент' };
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValueOnce({ items: [linkedLookupEntity, other], total: 2, page: 1, pageSize: 20 }).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт')).mockResolvedValueOnce({ items: [linkedLookupEntity], total: 1, page: 1, pageSize: 20 });
    await editLinkedLookup(); await waitFor(() => expect(document.querySelector('select[name="legalEntityId"] option[value="22"]')).not.toBeNull());
    const query = screen.getByLabelText('Поиск юридического лица клиента'); fireEvent.change(query, { target: { value: 'новая строка' } });
    await screen.findByText('Недостаточно прав для выполнения действия.'); const select = document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')!;
    expect(select.value).toBe('21'); expect(select.disabled).toBe(true); expect(select.querySelector('option[value="22"]')).toBeNull(); expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку организаций' })); await waitFor(() => expect(select.disabled).toBe(false));
    expect(select.value).toBe('21'); expect(query).toHaveProperty('value', 'новая строка'); expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(3);
  });
  it('company lookup selection: choosing a loaded row keeps one page read and the selected ID', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [linkedLookupEntity, { ...linkedLookupEntity, id: 22, shortName: 'Выбранный контрагент' }], total: 2, page: 1, pageSize: 20 });
    await editLinkedLookup(); await waitFor(() => expect(document.querySelector('select[name="legalEntityId"] option[value="22"]')).not.toBeNull());
    const select = document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')!; fireEvent.change(select, { target: { value: '22' } }); await act(async () => {});
    expect(select.value).toBe('22'); expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(1);
  });
  it('company lookup stale: a previous successful query cannot repopulate the current page', async () => {
    let finish!: (value: Awaited<ReturnType<typeof apiService.getLegalEntitiesPaged>>) => void;
    vi.mocked(apiService.getLegalEntitiesPaged).mockReturnValueOnce(new Promise(resolve => { finish = resolve; })).mockResolvedValueOnce({ items: [{ ...linkedLookupEntity, id: 22, shortName: 'Актуальный результат' }], total: 1, page: 1, pageSize: 20 });
    await editLinkedLookup(); fireEvent.change(screen.getByLabelText('Поиск юридического лица клиента'), { target: { value: 'актуальный' } });
    await waitFor(() => expect(document.querySelector('select[name="legalEntityId"] option[value="22"]')).not.toBeNull());
    await act(async () => finish({ items: [{ ...linkedLookupEntity, id: 23, shortName: 'Прежний результат' }], total: 1, page: 1, pageSize: 20 }));
    expect(document.querySelector('select[name="legalEntityId"] option[value="23"]')).toBeNull();
  });
  it('company lookup identity: a manual selection survives denial as an ID without allowing an unknown name to be saved', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValueOnce({ items: [linkedLookupEntity, { ...linkedLookupEntity, id: 22, shortName: 'Ручной выбор' }], total: 2, page: 1, pageSize: 20 }).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт'));
    await editLinkedLookup(); await waitFor(() => expect(document.querySelector('select[name="legalEntityId"] option[value="22"]')).not.toBeNull());
    const select = document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')!; fireEvent.change(select, { target: { value: '22' } });
    fireEvent.change(screen.getByLabelText('Поиск юридического лица клиента'), { target: { value: 'отказ' } }); await screen.findByText('Недостаточно прав для выполнения действия.');
    expect(select.value).toBe('22'); expect(select.querySelector('option[value="22"]')?.textContent).toContain('#22');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Не удалось подтвердить название выбранной организации. Повторите загрузку списка.', 'Проверка данных'));
    expect(apiService.updateClient).not.toHaveBeenCalled();
  });
  it('company caller read: double opening stays single-flight and cancelling the profile aborts its details', async () => {
    let finish!: (value: LegalEntityDetail) => void; vi.mocked(apiService.getLegalEntity).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    await editLinkedLookup(); const open = screen.getByRole('button', { name: 'Реквизиты' }); fireEvent.click(open); fireEvent.click(open);
    expect(apiService.getLegalEntity).toHaveBeenCalledTimes(1); const signal = vi.mocked(apiService.getLegalEntity).mock.calls[0][1]?.signal;
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); expect(signal?.aborted).toBe(true);
    await act(async () => finish(linkedLookupEntity)); expect(legalModalOwnerSpy).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
  });
  it('company caller readonly: direct submit cannot bypass the company edit permission', async () => {
    vi.mocked(apiService.getLegalEntity).mockResolvedValue(linkedLookupEntity); await editLinkedLookup(); fireEvent.click(screen.getByRole('button', { name: 'Реквизиты' }));
    await waitFor(() => expect(legalModalOwnerSpy).toHaveBeenCalled()); const modal = legalModalOwnerSpy.mock.lastCall![0] as LegalModalOwnerProps;
    await expect(modal.onSubmit({ shortName: 'Запрещённое изменение', clientIds: [] })).rejects.toThrow('Нет разрешения'); expect(apiService.updateLegalEntity).not.toHaveBeenCalled();
  });
  it('company caller related rights: editing a company does not grant mutation of its cases or financial records', async () => {
    const { modal } = await openOwnerLegalModal();
    const props = modal as LegalModalOwnerProps & { deleteOwnerScope: string; relatedRecordPermissions: Record<string, unknown> };
    expect(props.deleteOwnerScope).toEqual(expect.any(String));
    expect(props.relatedRecordPermissions).toEqual({
      cases: permissionsResult.cases,
      accounts: permissionsResult.accounts,
      acts: permissionsResult.acts,
      payments: permissionsResult.payments,
    });
    for (const section of ['cases', 'accounts', 'acts', 'payments'] as const) {
      expect(props.relatedRecordPermissions[section]).toEqual(expect.objectContaining({ canCreate: false, canEdit: false, canDelete: false }));
    }
  });
  it('company lookup malformed: duplicate IDs fail closed without losing the linked company', async () => {
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValueOnce({ items: [linkedLookupEntity, linkedLookupEntity], total: 2, page: 1, pageSize: 20 });
    await editLinkedLookup();
    await screen.findByText('Не удалось подтвердить данные списка организаций. Повторите загрузку.');
    expect(document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')?.value).toBe('21');
    expect(screen.getByRole('button', { name: 'Повторить загрузку организаций' })).toBeTruthy();
    expect(apiService.updateClient).not.toHaveBeenCalled();
  });
  it.each([false, true])('company caller own profile: profile edit=%s controls the own-company form independently of legal edit', async canEdit => {
    Object.assign(permissionsResult.companyProfile, { canView: true, canEdit }); permissionsResult.legalEntities.canEdit = !canEdit;
    const own = { ...linkedLookupEntity, isOwnCompany: true }; vi.mocked(apiService.getClient).mockResolvedValue({ ...linkedLookupClient(), legalEntity: own }); vi.mocked(apiService.getLegalEntity).mockResolvedValue(own);
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' })); fireEvent.click(screen.getByRole('button', { name: 'Реквизиты' }));
    await waitFor(() => expect(legalModalOwnerSpy).toHaveBeenCalled()); expect(legalModalOwnerSpy.mock.lastCall![0].allowEdit).toBe(canEdit);
  });
  it('company caller own view: legal view alone does not open a known own-company profile', async () => {
    const own = { ...linkedLookupEntity, isOwnCompany: true }; vi.mocked(apiService.getClient).mockResolvedValue({ ...linkedLookupClient(), legalEntity: own });
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    expect(screen.getByRole('button', { name: 'Реквизиты' })).toHaveProperty('disabled', true); expect(apiService.getLegalEntity).not.toHaveBeenCalled();
  });
  it('company caller save: a current edited company overrides its older page row and stays single-flight', async () => {
    const { entity, modal } = await openOwnerLegalModal(false); let finish!: (value: LegalEntityDetail) => void;
    vi.mocked(apiService.updateLegalEntity).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    let first!: Promise<LegalEntityDetail>; act(() => { first = modal.onSubmit({ shortName: 'Обновлённое название', clientIds: [2] }); });
    await expect(modal.onSubmit({ shortName: 'Дубль', clientIds: [] })).rejects.toThrow('уже сохраняется');
    let finishPage!: (page: Awaited<ReturnType<typeof apiService.getLegalEntitiesPaged>>) => void;
    vi.mocked(apiService.getLegalEntitiesPaged).mockReturnValueOnce(new Promise(resolve => { finishPage = resolve; }));
    await act(async () => { finish({ ...entity, shortName: 'Обновлённое название' }); await first; });
    expect(document.querySelector('select[name="legalEntityId"] option[value="21"]')?.textContent).toContain('Обновлённое название');
    expect(apiService.updateLegalEntity).toHaveBeenCalledTimes(1); expect(apiService.updateLegalEntity).toHaveBeenCalledWith(21, expect.objectContaining({ clientIds: [2, 1] }));
    await act(async () => finishPage({ items: [{ ...entity, shortName: 'Обновлённое название' }], total: 1, page: 1, pageSize: 20 }));
    expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(2);
  });
  it('company caller quick retry: failure retains the draft and success selects one persisted company without saving the client', async () => {
    permissionsResult.legalEntities.canCreate = true; vi.mocked(apiService.createLegalEntity).mockRejectedValueOnce(new Error('Временная ошибка создания')).mockResolvedValueOnce({ ...linkedLookupEntity, id: 22, shortName: 'Новая организация' });
    await editLinkedLookup(); fireEvent.click(screen.getByRole('button', { name: 'Быстрое создание' })); fireEvent.change(screen.getByRole('textbox', { name: /Название организации/ }), { target: { value: 'Новая организация' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать' })); await screen.findByText('Временная ошибка создания');
    expect(screen.getByRole('textbox', { name: /Название организации/ })).toHaveProperty('value', 'Новая организация');
    fireEvent.click(screen.getByRole('button', { name: 'Создать' })); await waitFor(() => expect(document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')?.value).toBe('22'));
    expect(apiService.createLegalEntity).toHaveBeenCalledTimes(2); expect(apiService.updateClient).not.toHaveBeenCalled(); expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(2);
  });
  it('company caller persisted cancel: cancelling the client reloads the company link already saved by the nested form', async () => {
    const { entity, modal } = await openOwnerLegalModal(true); const saved = { ...entity, id: 22, shortName: 'Уже сохранённая компания' }; vi.mocked(apiService.createLegalEntity).mockResolvedValue(saved);
    await act(async () => { await modal.onSubmit({ shortName: saved.shortName, clientIds: [] }); });
    vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient({ kind: 'legal' }), legalEntityId: 22, legalEntity: saved });
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); await screen.findByRole('button', { name: 'Редактировать' });
    expect(document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')?.value).toBe('22'); expect(apiService.getClient).toHaveBeenCalledTimes(2); expect(apiService.updateClient).not.toHaveBeenCalled();
  });
  it('company caller persisted failure: failed reconciliation shows retry instead of the obsolete local link', async () => {
    const { entity, modal } = await openOwnerLegalModal(); const saved = { ...entity, shortName: 'Сохранённые реквизиты' }; vi.mocked(apiService.updateLegalEntity).mockResolvedValue(saved);
    await act(async () => { await modal.onSubmit({ shortName: saved.shortName, clientIds: [] }); }); vi.mocked(apiService.getClient).mockRejectedValueOnce(new Error('HTTP 403 - Доступ закрыт'));
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); await screen.findByText('Недостаточно прав для выполнения действия.');
    expect(screen.queryByRole('button', { name: 'Редактировать' })).toBeNull(); expect(screen.getByRole('button', { name: 'Повторить загрузку клиента' })).toBeTruthy();
  });

  async function openOwnerLegalModal(create = false, load?: Promise<LegalEntityDetail>) {
    const entity = { id: 21, shortName: 'Owner original organisation', clients: [], clientsCount: 0, createdAt: '2026-10-29T00:00:00Z' } as LegalEntityDetail;
    permissionsResult.legalEntities.canEdit = true;
    permissionsResult.legalEntities.canCreate = create;
    vi.mocked(apiService.getClient).mockImplementation(async id => ({ ...createClient({ kind: 'legal' }), id, legalEntityId: 21, legalEntity: entity }));
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [entity], total: 1, page: 1, pageSize: 20 });
    vi.mocked(apiService.getLegalEntity).mockReturnValue(load ?? Promise.resolve(entity));
    const props = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const };
    const view = render(<ClientDetail {...props} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    const open = await screen.findByRole('button', { name: create ? 'Создать юрлицо' : 'Реквизиты' });
    await waitFor(() => expect(open).toHaveProperty('disabled', false));
    await userEvent.click(open);
    if (!load) await waitFor(() => expect(legalModalOwnerSpy).toHaveBeenCalled());
    return { view, props, entity, modal: legalModalOwnerSpy.mock.calls.at(-1)?.[0] as LegalModalOwnerProps };
  }

  it.each([false, true])('branch caller: client requisites forward explicit management when admin=%s', async admin => {
    const previous = authState.isAdmin; authState.isAdmin = admin;
    try {
      await openOwnerLegalModal(); expect(legalModalOwnerSpy.mock.lastCall?.[0]).toMatchObject({ canManageCompanyBranches: admin, canManageOwnCompanySettings: admin });
    } finally { authState.isAdmin = previous; }
  });

  it.each(['close', 'client', 'permission', 'user', 'role', 'unmount'] as const)('parent ownership: does not select an obsolete legal save after %s', async change => {
    const { view, props, entity, modal } = await openOwnerLegalModal();
    let finish!: (value: LegalEntityDetail) => void;
    vi.mocked(apiService.updateLegalEntity).mockReturnValue(new Promise<LegalEntityDetail>(resolve => { finish = resolve; }));
    let pending!: Promise<LegalEntityDetail | Error>;
    act(() => { pending = modal.onSubmit({ shortName: 'Late previous organisation', clientIds: [] }).catch(error => error); });
    await waitFor(() => expect(apiService.updateLegalEntity).toHaveBeenCalledWith(21, expect.objectContaining({ clientIds: [1] })));
    toastSuccessMock.mockClear();
    if (change === 'close') act(() => modal.onClose());
    if (change === 'client') {
      const currentEntity = { ...entity, id: 22, shortName: 'Organisation of the new client' };
      vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient({ kind: 'legal' }), id: 2, legalEntityId: 22, legalEntity: currentEntity });
      vi.mocked(apiService.getLegalEntity).mockImplementation(async id => id === 22 ? currentEntity : entity);
      vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [entity, currentEntity], total: 2, page: 1, pageSize: 20 });
      view.rerender(<ClientDetail {...props} clientId={2} />);
      await screen.findAllByText('Organisation of the new client');
      const edit = screen.queryByRole('button', { name: 'Редактировать' });
      if (edit) await userEvent.click(edit);
      await waitFor(() => expect(document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')?.value).toBe('22'));
    }
    if (change === 'permission') { permissionsResult.clients.canEdit = false; view.rerender(<ClientDetail {...props} />); }
    if (change === 'user') { authState.userId = 8; view.rerender(<ClientDetail {...props} />); }
    if (change === 'role') { authState.roleId = 2; view.rerender(<ClientDetail {...props} />); }
    if (change === 'unmount') view.unmount();
    await act(async () => { finish({ ...entity, shortName: 'Late previous organisation' }); expect(await pending).toBeInstanceOf(StaleUiOperationError); });
    expect(toastSuccessMock).not.toHaveBeenCalled();
    expect(screen.queryByText('Late previous organisation')).toBeNull();
    if (change === 'client') expect(document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')?.value).toBe('22');
  });

  it.each([false, true])('parent ownership: accepts current legal save with create=%s and binds only the original client', async create => {
    const { entity, modal } = await openOwnerLegalModal(create);
    const saved = { ...entity, id: create ? 22 : 21, shortName: 'Current saved organisation' };
    vi.mocked(create ? apiService.createLegalEntity : apiService.updateLegalEntity).mockResolvedValue(saved);
    await act(async () => { await modal.onSubmit({ shortName: saved.shortName, clientIds: [] }); });
    expect(toastSuccessMock).toHaveBeenCalledWith(create ? 'Юридическое лицо создано и выбрано.' : 'Карточка юридического лица обновлена.', 'Юрлица');
    const select = document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]');
    expect(select?.value).toBe(String(saved.id));
    if (create) expect(apiService.createLegalEntity).toHaveBeenCalledWith(expect.objectContaining({ clientIds: [1] }));
    else expect(apiService.updateLegalEntity).toHaveBeenCalledWith(21, expect.objectContaining({ clientIds: [1] }));
    await expect(modal.onSubmit({ shortName: 'Held stale callback', clientIds: [] })).rejects.toBeInstanceOf(StaleUiOperationError);
    expect(create ? apiService.createLegalEntity : apiService.updateLegalEntity).toHaveBeenCalledTimes(1);
  });

  it.each(['success', 'error'] as const)('parent ownership: ignores a late %s legal opening for the previous client', async outcome => {
    let finish!: (value: LegalEntityDetail) => void; let fail!: (error: Error) => void;
    const pending = new Promise<LegalEntityDetail>((resolve, reject) => { finish = resolve; fail = reject; });
    const { view, props, entity } = await openOwnerLegalModal(false, pending);
    await waitFor(() => expect(apiService.getLegalEntity).toHaveBeenCalledWith(21, { signal: expect.any(AbortSignal) }));
    view.rerender(<ClientDetail {...props} clientId={2} />);
    await act(async () => { if (outcome === 'success') finish(entity); else fail(new Error('Obsolete legal opening')); await pending.catch(() => undefined); });
    expect(legalModalOwnerSpy).not.toHaveBeenCalled();
    expect(toastErrorMock).not.toHaveBeenCalled();
  });

  it.each(['read', 'save'] as const)('parent ownership: refuses a wrong organisation ID returned during %s', async operation => {
    if (operation === 'read') {
      const wrong = { id: 22, shortName: 'Wrong returned entity', clients: [], clientsCount: 0, createdAt: '2026-10-29' } as LegalEntityDetail;
      await openOwnerLegalModal(false, Promise.resolve(wrong));
      await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
      expect(legalModalOwnerSpy).not.toHaveBeenCalled();
    } else {
      const { entity, modal } = await openOwnerLegalModal();
      vi.mocked(apiService.updateLegalEntity).mockResolvedValue({ ...entity, id: 22 });
      await expect(modal.onSubmit({ shortName: 'Wrong response', clientIds: [] })).rejects.toThrow('другую карточку');
      expect(document.querySelector<HTMLSelectElement>('select[name="legalEntityId"]')?.value).toBe('21');
      expect(document.querySelector('option[value="22"]')).toBeNull();
      expect(toastSuccessMock).not.toHaveBeenCalled();
    }
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
      }, { signal: expect.any(AbortSignal) });
    });
    expect(screen.getAllByText(/ООО Ромашка/).length).toBeGreaterThan(0);

    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    await user.type(screen.getByLabelText('Поиск юридического лица клиента'), 'ром');

    await waitFor(() => {
      expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledWith({
        search: 'ром',
        page: 1,
        pageSize: 20,
      }, { signal: expect.any(AbortSignal) });
    });
  });

  it('shows whitespace-only additional profile values as unspecified without changing stored values', async () => {
    const updateCallsBefore = vi.mocked(apiService.updateClient).mock.calls.length;
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({
      education: [{ institution: 'НГУ', specialty: '   ', diplomaNumber: '', oksoCode: '' }],
    }));
    const user = userEvent.setup();
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    await screen.findByRole('heading', { level: 1, name: 'Иванов Иван Иванович' });
    await user.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    await user.click(screen.getByRole('button', { name: 'Образование (ОКСО)' }));
    const panel = screen.getByRole('region', { name: 'Образование (ОКСО)' });
    const specialty = within(panel).getByText('Специальность').parentElement;
    expect(specialty?.querySelector('dd')?.textContent).toBe('Не указано');
    expect(within(panel).getByText('НГУ')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    expect(Array.from(document.querySelectorAll('input')).some((input) => input.value === '   ')).toBe(true);
    await user.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(apiService.updateClient).toHaveBeenCalledTimes(updateCallsBefore);
  });

  it('shows additional profile groups as readable text and preserves editing on cancel', async () => {
    const user = userEvent.setup();
    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);
    await screen.findByRole('heading', { level: 1, name: 'Иванов Иван Иванович' });
    await user.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    for (const [name, value] of [
      ['Образование (ОКСО)', 'НГУ'],
      ['Профессии (ОКПДТР)', '12345'],
      ['Работа и должности', 'ООО Альфа'],
      ['Родственники', 'Иванов Петр Сергеевич'],
    ]) {
      await user.click(screen.getByRole('button', { name }));
      expect(screen.getByText(value)).toBeTruthy();
      const panel = screen.getByRole('region', { name: name === 'Работа и должности' ? 'Места работы и должности' : name === 'Родственники' ? 'Близкие родственники' : name });
      expect(within(panel).queryByRole('textbox')).toBeNull();
      expect(within(panel).queryByRole('button', { name: /Добавить|Удалить/ })).toBeNull();
      if (name === 'Работа и должности') {
        expect(within(panel).getByText('01.01.2024')).toBeTruthy();
        expect(within(panel).getByText('31.12.2024')).toBeTruthy();
      }
    }
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    const relation = screen.getByDisplayValue('Отец');
    await user.clear(relation);
    await user.type(relation, 'Брат');
    await user.click(screen.getByRole('button', { name: 'Образование (ОКСО)' }));
    await user.click(screen.getByRole('button', { name: 'Родственники' }));
    expect(screen.getByDisplayValue('Брат')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(screen.getByText('Отец')).toBeTruthy();
    expect(screen.queryByText('Брат')).toBeNull();
  }, 15000);

  it('shows delete controls in profile subsections and removes an education row', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { level: 1, name: 'Иванов Иван Иванович' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));

    expect(await screen.findByRole('button', { name: 'Удалить телефон' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Удалить email' })).toBeTruthy();

    expect(await screen.findByRole('button', { name: 'Удалить паспорт' })).toBeTruthy();
    expect(screen.queryByText('Действует с')).toBeNull();
    expect(screen.queryByText('Действует по')).toBeNull();

    expect((await screen.findAllByRole('button', { name: 'Удалить адрес' })).length).toBeGreaterThan(0);

    // PAY-32: прежние личные данные не удалены, а свёрнуты в «Дополнительную информацию».
    expect(screen.queryByRole('button', { name: 'Образование (ОКСО)' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Доверенности' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Дополнительная информация' }).getAttribute('aria-expanded')).toBe('false');
    await user.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    expect(screen.getByRole('button', { name: 'Скрыть дополнительную информацию' }).getAttribute('aria-expanded')).toBe('true');

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

    expect(await screen.findByRole('button', { name: 'Удалить свидетельство о рождении' })).toBeTruthy();

    expect(await screen.findByRole('button', { name: 'Удалить военный билет' })).toBeTruthy();
  }, 15000);

  it('updates and deletes a power of attorney directly from the client card', async () => {
    const user = userEvent.setup();
    const originalPoa = createPowerOfAttorney();
    permissionsResult.clients.canDelete = true;
    const updatedPoa = createPowerOfAttorney({
      issuer: 'Новый нотариус',
      validUntil: '2026-05-15',
    });

    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValue([originalPoa]);
    vi.mocked(apiService.updateClientPowerOfAttorney).mockResolvedValue(updatedPoa);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { level: 1, name: 'Иванов Иван Иванович' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    await user.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
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

  async function openAddressProfile() {
    vi.mocked(apiService.getClient).mockResolvedValue({ ...createClient({ addressEntries: [
      { type: 'registration', isPrimary: true, fullAddress: 'Первый адрес', postalCode: '630000', country: 'Россия', city: 'г Исходный', house: '1', flat: '7' },
      { type: 'actual', isPrimary: false, fullAddress: 'Второй адрес', city: 'г Другой', house: '2' },
    ] }) });
    const props = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const };
    const view = render(<ClientDetail {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    return { props, view };
  }
  function latestAddressProps() {
    return addressFormPropsSpy.mock.calls.at(-1)![0] as Parameters<typeof import('./ClientAddressEntriesForm').ClientAddressEntriesForm>[0];
  }
  it('client address caller: read mode is lazy and temporary row keys never enter saved notes', async () => {
    await openAddressProfile(); expect(apiService.cleanAddress).not.toHaveBeenCalled(); expect(apiService.suggestClientAddresses).not.toHaveBeenCalled();
    const entries = latestAddressProps().entries; expect(entries[0].uiKey).toBeTruthy(); expect(entries[1].uiKey).not.toBe(entries[0].uiKey);
    fireEvent.submit(screen.getByDisplayValue('Иванов').closest('form')!);
    await waitFor(() => expect(apiService.updateClient).toHaveBeenCalledOnce());
    const notes = vi.mocked(apiService.updateClient).mock.calls[0][1].notes!; const saved = JSON.parse(notes.slice(CLIENT_PROFILE_NOTES_PREFIX.length));
    expect(saved.addressEntries).toHaveLength(2); expect(saved.addressEntries[0].fullAddress).toBe('Первый адрес');
    expect(saved.addressEntries.every((row: object) => !Object.hasOwn(row, 'uiKey'))).toBe(true);
  });
  it('client address caller: repeated find is single flight and applies only to the selected row', async () => {
    let finish!: (row: CleanedAddress) => void; vi.mocked(apiService.cleanAddress).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    await openAddressProfile(); const old = latestAddressProps(); act(() => { old.onFindAddress(0); old.onFindAddress(0); });
    expect(apiService.cleanAddress).toHaveBeenCalledExactlyOnceWith('Первый адрес', expect.any(AbortSignal));
    await act(async () => finish({ result: 'Уточнённый первый адрес', cityWithType: 'г Первый', house: '42' }));
    expect(latestAddressProps().entries[0]).toMatchObject({ city: 'г Первый', house: '42', type: 'registration', isPrimary: true });
    expect(latestAddressProps().entries[1].fullAddress).toBe('Второй адрес');
  });
  it('client address caller: an edited row rejects an old clean and does not launch its fallback', async () => {
    let fail!: (reason: Error) => void; vi.mocked(apiService.cleanAddress).mockReturnValueOnce(new Promise((_, reject) => { fail = reject; }));
    await openAddressProfile(); fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]);
    const signal = vi.mocked(apiService.cleanAddress).mock.calls[0][1]!;
    fireEvent.change(screen.getAllByLabelText('Квартира')[0], { target: { value: '99' } });
    await act(async () => fail(new Error('Старая ошибка')));
    expect(signal.aborted).toBe(true); expect(apiService.suggestClientAddresses).not.toHaveBeenCalled();
    expect(latestAddressProps().entries[0].flat).toBe('99'); expect(toastErrorMock).not.toHaveBeenCalled();
  });
  it('client address caller: a surviving keyed row keeps focus and receives its clean after another row is removed', async () => {
    let finish!: (row: CleanedAddress) => void; vi.mocked(apiService.cleanAddress).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    await openAddressProfile(); const input = screen.getByDisplayValue('Второй адрес');
    fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[1]); input.focus();
    await act(async () => latestAddressProps().onRemoveEntry(0));
    expect(screen.getByDisplayValue('Второй адрес')).toBe(input);
    await act(async () => finish({ result: 'Уточнённый второй', cityWithType: 'г Второй' }));
    expect(latestAddressProps().entries).toHaveLength(1); expect(latestAddressProps().entries[0].city).toBe('г Второй');
  });
  it('client address caller: suggestions debounce and a newer query rejects an old response', async () => {
    let finish!: (rows: DadataAddressSuggestion[]) => void; vi.mocked(apiService.suggestClientAddresses).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }))
      .mockResolvedValueOnce([{ value: 'Текущая подсказка' }]);
    await openAddressProfile(); fireEvent.change(screen.getAllByLabelText('Полный адрес')[0], { target: { value: 'Первый поиск' } });
    await waitFor(() => expect(apiService.suggestClientAddresses).toHaveBeenCalledOnce()); const signal = vi.mocked(apiService.suggestClientAddresses).mock.calls[0][2]!;
    fireEvent.change(screen.getAllByLabelText('Полный адрес')[0], { target: { value: 'Новый поиск' } });
    await screen.findByRole('button', { name: 'Текущая подсказка' });
    await act(async () => finish([{ value: 'Устаревшая подсказка' }]));
    expect(signal.aborted).toBe(true); expect(screen.queryByRole('button', { name: 'Устаревшая подсказка' })).toBeNull();
    expect(latestAddressProps().entries[0].fullAddress).toBe('Новый поиск');
  });
  it('client address caller: a cached suggestion cannot overwrite a manual field edit', async () => {
    await openAddressProfile(); fireEvent.change(screen.getAllByLabelText('Полный адрес')[0], { target: { value: 'Поиск адреса' } });
    await screen.findByRole('button', { name: 'Подсказка адреса' }); const old = latestAddressProps(); const suggestion = old.suggestionsByIndex[0][0];
    fireEvent.change(screen.getAllByLabelText('Квартира')[0], { target: { value: '99' } });
    await act(async () => old.onApplySuggestion(0, suggestion));
    expect(apiService.cleanAddress).not.toHaveBeenCalled(); expect(latestAddressProps().entries[0].flat).toBe('99');
  });
  it('client address caller: a current suggestion preserves partial fields if both remote reads fail', async () => {
    await openAddressProfile(); fireEvent.change(screen.getAllByLabelText('Полный адрес')[0], { target: { value: 'Поиск адреса' } });
    await screen.findByRole('button', { name: 'Подсказка адреса' });
    vi.mocked(apiService.cleanAddress).mockRejectedValueOnce(new Error('clean unavailable')); vi.mocked(apiService.suggestClientAddresses).mockRejectedValueOnce(new Error('fallback unavailable'));
    fireEvent.click(screen.getByRole('button', { name: 'Подсказка адреса' }));
    await screen.findByRole('alert');
    expect(latestAddressProps().entries[0]).toMatchObject({ city: 'г Подсказка', flat: '7', postalCode: '630000' });
    expect(apiService.suggestClientAddresses).toHaveBeenLastCalledWith('Подсказка адреса', 1, expect.any(AbortSignal));
  });
  it('client address caller: a definitive not-found response preserves the row without another search', async () => {
    vi.mocked(apiService.cleanAddress).mockRejectedValueOnce(new Error('HTTP 404 - Адрес не найден'));
    await openAddressProfile(); fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]);
    await screen.findByRole('alert');
    expect(apiService.suggestClientAddresses).not.toHaveBeenCalled(); expect(latestAddressProps().entries[0].fullAddress).toBe('Первый адрес');
  });
  it.each(['role', 'permission', 'cancel'] as const)('client address caller: changing %s cancels a pending find and its late failure', async mode => {
    let fail!: (reason: Error) => void; vi.mocked(apiService.cleanAddress).mockReturnValueOnce(new Promise((_, reject) => { fail = reject; }));
    const { props, view } = await openAddressProfile(); fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]);
    const signal = vi.mocked(apiService.cleanAddress).mock.calls[0][1]!;
    if (mode === 'role') { authState.roleId = 2; view.rerender(<ClientDetail {...props} />); }
    else if (mode === 'permission') { permissionsResult.clients.canEdit = false; view.rerender(<ClientDetail {...props} />); }
    else fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    await act(async () => fail(new Error('Старая ошибка адреса')));
    expect(signal.aborted).toBe(true); expect(apiService.suggestClientAddresses).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
  });
  it('client address caller: old callbacks cannot change the shifted row or start a lookup after deletion', async () => {
    await openAddressProfile(); const old = latestAddressProps(); await act(async () => old.onRemoveEntry(0));
    act(() => { old.onUpdateEntry(0, 'fullAddress', 'Чужая подстановка'); old.onFindAddress(0); old.onScheduleSuggestions(0, 'Старый поиск'); });
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 400)); });
    expect(latestAddressProps().entries[0].fullAddress).toBe('Второй адрес'); expect(apiService.cleanAddress).not.toHaveBeenCalled(); expect(apiService.suggestClientAddresses).not.toHaveBeenCalled();
  });
  it('client address caller: authentication replacement rejects old field and lookup callbacks before rerender', async () => {
    const session = documentCopyTestSession(); await openAddressProfile(); const old = latestAddressProps(); session.replace('actor');
    act(() => { old.onUpdateEntry(0, 'flat', '99'); old.onFindAddress(0); old.onScheduleSuggestions(0, 'Чужой поиск'); });
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 400)); });
    expect(latestAddressProps().entries[0].flat).toBe('7'); expect(apiService.cleanAddress).not.toHaveBeenCalled(); expect(apiService.suggestClientAddresses).not.toHaveBeenCalled();
  });
  it('client address caller: same-user token refresh preserves a pending clean and the typed address', async () => {
    let finish!: (row: CleanedAddress) => void; vi.mocked(apiService.cleanAddress).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    const session = documentCopyTestSession(); const { props, view } = await openAddressProfile(); fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]);
    session.refresh(); view.rerender(<ClientDetail {...props} />);
    await act(async () => finish({ result: 'Текущий адрес', cityWithType: 'г Текущий' }));
    expect(latestAddressProps().entries[0].city).toBe('г Текущий'); expect(apiService.cleanAddress).toHaveBeenCalledOnce();
  });
  it('client address caller: cancelling edit invalidates a queued address deletion and its toast', async () => {
    let answer!: (value: boolean) => void; confirmMock.mockReturnValueOnce(new Promise(resolve => { answer = resolve; }));
    await openAddressProfile(); const old = latestAddressProps(); act(() => { void old.onRemoveEntry(0); });
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' })); await act(async () => answer(true));
    expect(latestAddressProps().entries).toHaveLength(2); expect(toastSuccessMock).not.toHaveBeenCalled();
  });
  it('client address caller: saving the profile cancels pending enrichment before it can alter the saved snapshot', async () => {
    let finishClean!: (row: CleanedAddress) => void; let finishSave!: (row: Client) => void;
    vi.mocked(apiService.cleanAddress).mockReturnValueOnce(new Promise(resolve => { finishClean = resolve; }));
    vi.mocked(apiService.updateClient).mockReturnValueOnce(new Promise(resolve => { finishSave = resolve; }));
    await openAddressProfile(); fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]);
    const signal = vi.mocked(apiService.cleanAddress).mock.calls[0][1]!;
    fireEvent.submit(screen.getByDisplayValue('Иванов').closest('form')!); await waitFor(() => expect(apiService.updateClient).toHaveBeenCalledOnce());
    expect(signal.aborted).toBe(true);
    await act(async () => finishClean({ result: 'Устаревший адрес', cityWithType: 'г Устаревший' }));
    const saved = JSON.parse(vi.mocked(apiService.updateClient).mock.calls[0][1].notes!.slice(CLIENT_PROFILE_NOTES_PREFIX.length));
    expect(saved.addressEntries[0].fullAddress).toBe('Первый адрес'); expect(saved.addressEntries[0].city).toBe('г Исходный');
    expect(latestAddressProps().entries[0].city).toBe('г Исходный');
    await act(async () => finishSave(createClient())); expect(latestAddressProps().entries.every(row => row.city !== 'г Устаревший')).toBe(true);
  });
  async function openPoaPermissionProfile() {
    poaPromptPropsSpy.mockClear();
    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValue([createPowerOfAttorney()]);
    const props = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const };
    const view = render(<ClientDetail {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    await screen.findByText('Кем выдана: Старый нотариус');
    return { props, view };
  }
  function latestPoaPrompt() {
    return poaPromptPropsSpy.mock.calls.map(call => call[0]).filter(props => props.open).at(-1) as Parameters<typeof import('../ui/FormPromptModal').FormPromptModal>[0];
  }
  async function openNewPoaDraft() {
    permissionsResult.clients.canCreate = true;
    const view = await openPoaPermissionProfile();
    fireEvent.click(screen.getByRole('button', { name: '+ Добавить доверенность' }));
    const prompt = within(screen.getByRole('dialog', { name: 'Новая доверенность' }));
    fireEvent.change(prompt.getByLabelText(/Номер доверенности/), { target: { value: 'Новая 78' } });
    fireEvent.change(prompt.getByLabelText('Дата выдачи'), { target: { value: '2026-11-02' } });
    return view;
  }
  it('poa form lifetime: repeated submit sends one create and blocks profile writes until completion', async () => {
    let finish!: (value: PowerOfAttorney) => void;
    vi.mocked(apiService.createClientPowerOfAttorney).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    await openNewPoaDraft();
    const prompt = latestPoaPrompt();
    act(() => { void prompt.onConfirm(); void prompt.onConfirm(); });
    expect(apiService.createClientPowerOfAttorney).toHaveBeenCalledOnce();
    expect(within(screen.getByRole('dialog', { name: 'Новая доверенность' })).getByLabelText(/Номер доверенности/)).toHaveProperty('disabled', true);
    fireEvent.submit(screen.getByDisplayValue('Иванов').closest('form')!);
    expect(apiService.updateClient).not.toHaveBeenCalled();
    await act(async () => finish(createPowerOfAttorney({ id: 78, number: 'Новая 78' })));
    expect(screen.getByText('№Новая 78')).toBeTruthy();
    expect(screen.queryByRole('dialog', { name: 'Новая доверенность' })).toBeNull();
    expect(toastSuccessMock).toHaveBeenCalledWith('Доверенность создана.', 'Доверенности');
    expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledOnce();
  });
  it('poa form lifetime: a failed update preserves the draft and allows one explicit retry', async () => {
    vi.mocked(apiService.updateClientPowerOfAttorney).mockRejectedValueOnce(new Error('Сохранение временно недоступно'))
      .mockResolvedValueOnce(createPowerOfAttorney({ issuer: 'Исправленный нотариус' }));
    await openPoaPermissionProfile(); fireEvent.click(screen.getByRole('button', { name: 'Изменить доверенность' }));
    fireEvent.change(within(screen.getByRole('dialog', { name: 'Редактирование доверенности' })).getByLabelText('Кем выдана'), { target: { value: 'Исправленный нотариус' } });
    const prompt = latestPoaPrompt();
    await act(async () => { void prompt.onConfirm(); void prompt.onConfirm(); });
    expect(apiService.updateClientPowerOfAttorney).toHaveBeenCalledOnce();
    expect(screen.getByText('Сохранение временно недоступно')).toBeTruthy();
    expect(screen.getByDisplayValue('Исправленный нотариус')).toBeTruthy();
    await act(async () => latestPoaPrompt().onConfirm());
    expect(apiService.updateClientPowerOfAttorney).toHaveBeenCalledTimes(2);
    expect(screen.getByText('Кем выдана: Исправленный нотариус')).toBeTruthy();
  });
  it('poa form lifetime: closing a pending create keeps its barrier and reconciles the current list without stale toast', async () => {
    let finish!: (value: PowerOfAttorney) => void;
    vi.mocked(apiService.createClientPowerOfAttorney).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    await openNewPoaDraft(); const prompt = latestPoaPrompt();
    act(() => { void prompt.onConfirm(); });
    fireEvent.click(within(screen.getByRole('dialog', { name: 'Новая доверенность' })).getByRole('button', { name: 'Отмена' }));
    expect(screen.queryByRole('dialog', { name: 'Новая доверенность' })).toBeNull();
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', true);
    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValueOnce([createPowerOfAttorney(), createPowerOfAttorney({ id: 78, number: 'Подтверждено чтением' })]);
    await act(async () => finish(createPowerOfAttorney({ id: 78, number: 'Старый ответ записи' })));
    await screen.findByText('№Подтверждено чтением');
    expect(screen.queryByText('№Старый ответ записи')).toBeNull(); expect(toastSuccessMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: '+ Добавить доверенность' }));
    await act(async () => { prompt.fields[0].onChange('Старая подстановка'); prompt.onCancel(); await prompt.onConfirm(); });
    expect(screen.getByRole('dialog', { name: 'Новая доверенность' })).toBeTruthy();
    expect(within(screen.getByRole('dialog', { name: 'Новая доверенность' })).getByLabelText(/Номер доверенности/)).toHaveProperty('value', '');
    expect(apiService.createClientPowerOfAttorney).toHaveBeenCalledOnce();
  });
  it('poa form lifetime: collapsing the group hides a draft and invalidates its submit, cancel and field callbacks', async () => {
    await openNewPoaDraft(); const old = latestPoaPrompt();
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть дополнительную информацию' }));
    expect(screen.queryByRole('dialog', { name: 'Новая доверенность' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    await screen.findByText('Кем выдана: Старый нотариус');
    fireEvent.click(screen.getByRole('button', { name: 'Изменить доверенность' }));
    await act(async () => { old.fields[0].onChange('Старая строка'); old.onCancel(); await old.onConfirm(); });
    expect(screen.getByRole('dialog', { name: 'Редактирование доверенности' })).toBeTruthy();
    expect(within(screen.getByRole('dialog', { name: 'Редактирование доверенности' })).getByLabelText(/Номер доверенности/)).toHaveProperty('value', '321');
    expect(apiService.createClientPowerOfAttorney).not.toHaveBeenCalled(); expect(apiService.updateClientPowerOfAttorney).not.toHaveBeenCalled();
  });
  it('poa form lifetime: replacing authentication before render prevents submit and field changes', async () => {
    const session = documentCopyTestSession(); await openNewPoaDraft(); const prompt = latestPoaPrompt();
    session.replace('actor');
    await act(async () => { prompt.fields[0].onChange('Чужой ввод'); await prompt.onConfirm(); });
    expect(apiService.createClientPowerOfAttorney).not.toHaveBeenCalled();
    expect(screen.queryByDisplayValue('Чужой ввод')).toBeNull();
  });
  it('poa form lifetime: same-user token refresh preserves the draft and its current submit', async () => {
    const session = documentCopyTestSession(); const { props, view } = await openNewPoaDraft();
    vi.mocked(apiService.createClientPowerOfAttorney).mockResolvedValueOnce(createPowerOfAttorney({ id: 78, number: 'Новая 78' }));
    session.refresh(); view.rerender(<ClientDetail {...props} />);
    expect(screen.getByDisplayValue('Новая 78')).toBeTruthy();
    await act(async () => latestPoaPrompt().onConfirm());
    expect(apiService.createClientPowerOfAttorney).toHaveBeenCalledOnce(); expect(screen.getByText('№Новая 78')).toBeTruthy();
  });
  it.each([
    { label: 'another client', row: createPowerOfAttorney({ id: 78, clientId: 2 }) },
    { label: 'existing ID', row: createPowerOfAttorney() },
    { label: 'invalid shape', row: null },
  ])('poa form lifetime: create rejects an unconfirmed response ($label)', async ({ row }) => {
    vi.mocked(apiService.createClientPowerOfAttorney).mockResolvedValueOnce(row as PowerOfAttorney);
    await openNewPoaDraft(); await act(async () => latestPoaPrompt().onConfirm());
    expect(screen.getByText('Не удалось подтвердить сохранённую доверенность. Обновите список перед повторным созданием.')).toBeTruthy();
    expect(screen.getByDisplayValue('Новая 78')).toBeTruthy(); expect(toastSuccessMock).not.toHaveBeenCalled();
  });
  it.each([
    { label: 'another ID', row: createPowerOfAttorney({ id: 78, issuer: 'Чужой нотариус' }) },
    { label: 'another client', row: createPowerOfAttorney({ clientId: 2, issuer: 'Чужой нотариус' }) },
  ])('poa form lifetime: update rejects a different saved record ($label)', async ({ row }) => {
    vi.mocked(apiService.updateClientPowerOfAttorney).mockResolvedValueOnce(row);
    await openPoaPermissionProfile(); fireEvent.click(screen.getByRole('button', { name: 'Изменить доверенность' }));
    await act(async () => latestPoaPrompt().onConfirm());
    expect(screen.getByText('Сервер вернул другую доверенность. Обновите список перед повторным сохранением.')).toBeTruthy();
    expect(screen.queryByText('Кем выдана: Чужой нотариус')).toBeNull(); expect(toastSuccessMock).not.toHaveBeenCalled();
  });
  it('poa form lifetime: a late delete after reopening the group reconciles only through a current read', async () => {
    permissionsResult.clients.canDelete = true; permissionsResult.clients.canCreate = true;
    let finish!: () => void; vi.mocked(apiService.deleteClientPowerOfAttorney).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    await openPoaPermissionProfile(); fireEvent.click(screen.getByRole('button', { name: 'Удалить доверенность' }));
    await waitFor(() => expect(apiService.deleteClientPowerOfAttorney).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть дополнительную информацию' }));
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    await screen.findByText('Кем выдана: Старый нотариус');
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', true);
    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValueOnce([]);
    await act(async () => finish()); await screen.findByText('Доверенности не найдены.');
    expect(toastSuccessMock).not.toHaveBeenCalled(); expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledTimes(3);
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', false);
  });
  it.each(['role', 'client', 'edit permission'] as const)('poa form lifetime: a late failure cannot notify a changed %s', async change => {
    let fail!: (reason: Error) => void;
    vi.mocked(apiService.createClientPowerOfAttorney).mockReturnValueOnce(new Promise((_, reject) => { fail = reject; }));
    const { props, view } = await openNewPoaDraft(); const prompt = latestPoaPrompt(); act(() => { void prompt.onConfirm(); });
    if (change === 'role') authState.roleId = 2;
    if (change === 'edit permission') permissionsResult.clients.canEdit = false;
    if (change === 'client') vi.mocked(apiService.getClient).mockResolvedValueOnce({ ...createClient(), id: 2, name: 'Другой клиент' });
    view.rerender(<ClientDetail {...props} clientId={change === 'client' ? 2 : 1} />);
    expect(screen.queryByRole('dialog', { name: 'Новая доверенность' })).toBeNull();
    await act(async () => { await prompt.onConfirm(); fail(new Error('Ошибка прежней записи')); });
    expect(toastErrorMock).not.toHaveBeenCalled(); expect(toastSuccessMock).not.toHaveBeenCalled();
    expect(apiService.createClientPowerOfAttorney).toHaveBeenCalledOnce();
  });
  it('poa form lifetime: an old finalizer cannot unlock a new role operation', async () => {
    let failOld!: (reason: Error) => void; let finishNew!: (value: PowerOfAttorney) => void;
    vi.mocked(apiService.createClientPowerOfAttorney).mockReturnValueOnce(new Promise((_, reject) => { failOld = reject; }))
      .mockReturnValueOnce(new Promise(resolve => { finishNew = resolve; }));
    const { props, view } = await openNewPoaDraft(); act(() => { void latestPoaPrompt().onConfirm(); });
    authState.roleId = 2; view.rerender(<ClientDetail {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' })); fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    await screen.findByText('Кем выдана: Старый нотариус'); fireEvent.click(screen.getByRole('button', { name: '+ Добавить доверенность' }));
    const prompt = within(screen.getByRole('dialog', { name: 'Новая доверенность' }));
    fireEvent.change(prompt.getByLabelText(/Номер доверенности/), { target: { value: 'Новая роль' } }); fireEvent.change(prompt.getByLabelText('Дата выдачи'), { target: { value: '2026-11-02' } });
    const current = latestPoaPrompt(); act(() => { void current.onConfirm(); });
    await act(async () => failOld(new Error('Старая ошибка')));
    expect(prompt.getByLabelText(/Номер доверенности/)).toHaveProperty('disabled', true); expect(toastErrorMock).not.toHaveBeenCalled();
    act(() => { void current.onConfirm(); }); expect(apiService.createClientPowerOfAttorney).toHaveBeenCalledTimes(2);
    await act(async () => finishNew(createPowerOfAttorney({ id: 78, number: 'Новая роль' })));
    expect(screen.getByText('№Новая роль')).toBeTruthy(); expect(toastSuccessMock).toHaveBeenCalledOnce();
  });
  it('poa form lifetime: a queued delete blocks other writes and cancellation releases them', async () => {
    permissionsResult.clients.canDelete = true; permissionsResult.clients.canCreate = true;
    let answer!: (value: boolean) => void; confirmMock.mockReturnValueOnce(new Promise(resolve => { answer = resolve; }));
    await openPoaPermissionProfile(); fireEvent.click(screen.getByRole('button', { name: 'Удалить доверенность' }));
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', true);
    fireEvent.submit(screen.getByDisplayValue('Иванов').closest('form')!); expect(apiService.updateClient).not.toHaveBeenCalled();
    await act(async () => answer(false)); expect(apiService.deleteClientPowerOfAttorney).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', false);
  });
  it('poa form lifetime: missing required fields preserve the prompt without starting a write', async () => {
    await openNewPoaDraft(); const prompt = within(screen.getByRole('dialog', { name: 'Новая доверенность' }));
    fireEvent.change(prompt.getByLabelText(/Номер доверенности/), { target: { value: '   ' } });
    await act(async () => latestPoaPrompt().onConfirm());
    expect(screen.getByText('Укажите номер и дату выдачи доверенности.')).toBeTruthy();
    expect(apiService.createClientPowerOfAttorney).not.toHaveBeenCalled(); expect(prompt.getByLabelText(/Номер доверенности/)).toHaveProperty('disabled', false);
  });
  async function openPoaReadProfile() {
    const props = { clientId: 1, onBack: vi.fn(), initialSection: 'profile' as const };
    const view = render(<ClientDetail {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    return { props, view };
  }
  it('poa list read: explicit retry preserves client draft, is single flight and distinguishes an empty result', async () => {
    let finish!: (value: PowerOfAttorney[]) => void;
    vi.mocked(apiService.getClientPowersOfAttorney).mockRejectedValueOnce(new Error('Сеть доверенностей недоступна'))
      .mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    await openPoaReadProfile();
    fireEvent.change(screen.getByDisplayValue('Иванов'), { target: { value: 'Черновик фамилии' } });
    expect((await screen.findByRole('alert')).textContent).toContain('Сеть доверенностей недоступна');
    expect(screen.queryByText('Доверенности не найдены.')).toBeNull();
    expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledOnce();
    const retry = screen.getByRole('button', { name: 'Повторить загрузку доверенностей' });
    fireEvent.click(retry); fireEvent.click(retry);
    expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledTimes(2);
    expect(screen.getByText('Загрузка доверенностей…')).toBeTruthy();
    await act(async () => finish([]));
    expect(screen.getByText('Доверенности не найдены.')).toBeTruthy();
    expect(screen.getByDisplayValue('Черновик фамилии')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });
  it('poa list read: closing the group aborts a pending read and reopening rejects its late response', async () => {
    let finish!: (value: PowerOfAttorney[]) => void;
    vi.mocked(apiService.getClientPowersOfAttorney).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }))
      .mockResolvedValueOnce([createPowerOfAttorney({ number: 'Актуальная доверенность' })]);
    await openPoaReadProfile();
    expect(screen.queryByText('Доверенности не найдены.')).toBeNull();
    const signal = vi.mocked(apiService.getClientPowersOfAttorney).mock.calls[0][1]?.signal;
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть дополнительную информацию' }));
    expect(signal?.aborted).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    await screen.findByText('№Актуальная доверенность');
    await act(async () => finish([createPowerOfAttorney({ number: 'Запоздалая доверенность' })]));
    expect(screen.queryByText('№Запоздалая доверенность')).toBeNull();
    expect(screen.getByText('№Актуальная доверенность')).toBeTruthy();
  });
  it('poa list read: a role change aborts its old read and accepts only the reopened role', async () => {
    let finish!: (value: PowerOfAttorney[]) => void;
    vi.mocked(apiService.getClientPowersOfAttorney).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }))
      .mockResolvedValueOnce([createPowerOfAttorney({ number: 'Доверенность новой роли' })]);
    const { props, view } = await openPoaReadProfile();
    const signal = vi.mocked(apiService.getClientPowersOfAttorney).mock.calls[0][1]?.signal;
    authState.roleId = 2; view.rerender(<ClientDetail {...props} />);
    expect(signal?.aborted).toBe(true);
    fireEvent.click(await screen.findByRole('button', { name: 'Редактировать' }));
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    await screen.findByText('№Доверенность новой роли');
    await act(async () => finish([createPowerOfAttorney({ number: 'Доверенность старой роли' })]));
    expect(screen.queryByText('№Доверенность старой роли')).toBeNull();
    expect(screen.getByText('№Доверенность новой роли')).toBeTruthy();
  });
  it.each([
    { label: 'foreign client', rows: [createPowerOfAttorney({ clientId: 2 })] },
    { label: 'duplicate IDs', rows: [createPowerOfAttorney(), createPowerOfAttorney()] },
    { label: 'null body', rows: null },
    { label: 'zero ID', rows: [createPowerOfAttorney({ id: 0 })] },
    { label: 'invalid issuer', rows: [createPowerOfAttorney({ issuer: {} as unknown as string })] },
    { label: 'invalid end date', rows: [createPowerOfAttorney({ validUntil: {} as unknown as string })] },
  ])('poa list read: invalid or foreign rows are not shown as current records ($label)', async ({ rows }) => {
    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValueOnce(rows as PowerOfAttorney[]);
    await openPoaReadProfile();
    expect((await screen.findByRole('alert')).textContent).toContain('подтвердить список доверенностей');
    expect(screen.queryByRole('button', { name: 'Изменить доверенность' })).toBeNull();
    expect(screen.queryByText('Доверенности не найдены.')).toBeNull();
  });
  it('poa list read: an empty success is stable through a render, token refresh and unrelated dictionary edits', async () => {
    const session = documentCopyTestSession();
    const { props, view } = await openPoaReadProfile();
    await screen.findByText('Доверенности не найдены.');
    session.refresh(); view.rerender(<ClientDetail {...props} />);
    await act(async () => window.dispatchEvent(new Event('dicts:changed')));
    expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledOnce();
  });
  it('poa list read: unmount aborts its read and ignores a late failure', async () => {
    let fail!: (reason: Error) => void;
    vi.mocked(apiService.getClientPowersOfAttorney).mockReturnValueOnce(new Promise((_, reject) => { fail = reject; }));
    const { view } = await openPoaReadProfile();
    const signal = vi.mocked(apiService.getClientPowersOfAttorney).mock.calls[0][1]?.signal;
    view.unmount(); expect(signal?.aborted).toBe(true);
    await act(async () => fail(new Error('Поздняя ошибка доверенностей')));
    expect(toastErrorMock).not.toHaveBeenCalled();
  });
  it('poa list read: an abandoned retry cannot block retrying a newly opened group', async () => {
    let finish!: (value: PowerOfAttorney[]) => void;
    vi.mocked(apiService.getClientPowersOfAttorney).mockRejectedValueOnce(new Error('Первая ошибка списка'))
      .mockReturnValueOnce(new Promise(resolve => { finish = resolve; }))
      .mockRejectedValueOnce(new Error('Ошибка нового открытия')).mockResolvedValueOnce([]);
    await openPoaReadProfile();
    fireEvent.click(await screen.findByRole('button', { name: 'Повторить загрузку доверенностей' }));
    const oldSignal = vi.mocked(apiService.getClientPowersOfAttorney).mock.calls[1][1]?.signal;
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть дополнительную информацию' }));
    expect(oldSignal?.aborted).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Дополнительная информация' }));
    fireEvent.click(screen.getByRole('button', { name: 'Доверенности' }));
    await screen.findByText('Ошибка нового открытия');
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку доверенностей' }));
    await screen.findByText('Доверенности не найдены.');
    expect(apiService.getClientPowersOfAttorney).toHaveBeenCalledTimes(4);
    await act(async () => finish([createPowerOfAttorney({ number: 'Старый повтор' })]));
    expect(screen.queryByText('№Старый повтор')).toBeNull();
  });
  it('poa list read: a forbidden response shows a readable message without an internal URL', async () => {
    vi.mocked(apiService.getClientPowersOfAttorney).mockRejectedValueOnce(new Error('HTTP 403 at https://synthetic.invalid/api/clients/1/powers-of-attorney - Protected response'));
    await openPoaReadProfile();
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Недостаточно прав для выполнения действия.');
    expect(alert.textContent).not.toContain('https://');
    expect(alert.textContent).not.toContain('/api/');
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', true);
    expect(screen.queryByText('Доверенности не найдены.')).toBeNull();
  });
  it('poa operation permissions: profile edit alone does not allow creating or deleting a power', async () => {
    await openPoaPermissionProfile();
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Удалить доверенность' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Изменить доверенность' })).toHaveProperty('disabled', false);
  });
  it('poa operation permissions: separate create and delete permissions enable their existing actions', async () => {
    permissionsResult.clients.canCreate = true; permissionsResult.clients.canDelete = true;
    await openPoaPermissionProfile();
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', false);
    expect(screen.getByRole('button', { name: 'Удалить доверенность' })).toHaveProperty('disabled', false);
  });
  it.each([[true, false], [false, true]])('poa operation permissions: create=%s and delete=%s remain independent', async (create, remove) => {
    permissionsResult.clients.canCreate = create; permissionsResult.clients.canDelete = remove;
    await openPoaPermissionProfile();
    expect(screen.getByRole('button', { name: '+ Добавить доверенность' })).toHaveProperty('disabled', !create);
    expect(screen.getByRole('button', { name: 'Удалить доверенность' })).toHaveProperty('disabled', !remove);
  });
  it('poa operation owner: a late create cannot notify another role', async () => {
    permissionsResult.clients.canCreate = true;
    let finish!: (value: PowerOfAttorney) => void; vi.mocked(apiService.createClientPowerOfAttorney).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    const { props, view } = await openPoaPermissionProfile(); fireEvent.click(screen.getByRole('button', { name: '+ Добавить доверенность' }));
    const prompt = within(screen.getByRole('dialog', { name: 'Новая доверенность' }));
    fireEvent.change(prompt.getByLabelText(/Номер доверенности/), { target: { value: 'Новая доверенность' } });
    fireEvent.change(prompt.getByLabelText('Дата выдачи'), { target: { value: '2026-11-02' } });
    fireEvent.click(prompt.getByRole('button', { name: 'Создать' })); await waitFor(() => expect(apiService.createClientPowerOfAttorney).toHaveBeenCalledOnce());
    authState.roleId = 2; view.rerender(<ClientDetail {...props} />);
    await act(async () => finish(createPowerOfAttorney({ id: 78, number: 'Новая доверенность' })));
    expect(toastSuccessMock).not.toHaveBeenCalled();
  });
  it('poa operation owner: a queued delete confirmation cannot issue a request under another role', async () => {
    permissionsResult.clients.canDelete = true;
    let answer!: (value: boolean) => void; confirmMock.mockReturnValueOnce(new Promise(resolve => { answer = resolve; }));
    const { props, view } = await openPoaPermissionProfile(); fireEvent.click(screen.getByRole('button', { name: 'Удалить доверенность' }));
    await waitFor(() => expect(confirmMock).toHaveBeenCalledOnce()); authState.roleId = 2; view.rerender(<ClientDetail {...props} />);
    expect(confirmMock.mock.calls[0][0].signal.aborted).toBe(true);
    await act(async () => answer(true)); expect(apiService.deleteClientPowerOfAttorney).not.toHaveBeenCalled(); expect(toastSuccessMock).not.toHaveBeenCalled();
  });
  it('poa operation owner: collapsing the group aborts its one pending delete confirmation', async () => {
    permissionsResult.clients.canDelete = true;
    let answer!: (value: boolean) => void; confirmMock.mockReturnValueOnce(new Promise(resolve => { answer = resolve; }));
    await openPoaPermissionProfile(); const remove = screen.getByRole('button', { name: 'Удалить доверенность' }); fireEvent.click(remove); fireEvent.click(remove);
    expect(confirmMock).toHaveBeenCalledOnce(); const signal = confirmMock.mock.calls[0][0].signal as AbortSignal;
    fireEvent.click(screen.getByRole('button', { name: 'Скрыть дополнительную информацию' })); expect(signal.aborted).toBe(true);
    await act(async () => answer(true)); expect(apiService.deleteClientPowerOfAttorney).not.toHaveBeenCalled();
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

    await screen.findByRole('heading', { level: 1, name: 'Иванов Иван Иванович' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
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

  it('PAY-32: saving without opening the collapsed block keeps education, professions, workplaces and relatives', async () => {
    const user = userEvent.setup();
    const updateCallsBefore = vi.mocked(apiService.updateClient).mock.calls.length;
    const phones = ['+79990000001', '+79990000002'];
    const emails = ['first@example.test', 'second@example.test'];
    vi.mocked(apiService.getClient).mockResolvedValue(createClient({ phones, emails }));

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByRole('heading', { level: 1, name: 'Иванов Иван Иванович' });
    await user.click(screen.getByRole('button', { name: 'Редактировать' }));
    expect(screen.queryByDisplayValue('НГУ')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      expect(apiService.updateClient).toHaveBeenCalledTimes(updateCallsBefore + 1);
    });
    const [, payload] = vi.mocked(apiService.updateClient).mock.calls.at(-1) ?? [];
    const notes = (payload as { notes?: string | null }).notes ?? '';
    const profile = JSON.parse(notes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)) as {
      phones: string[];
      emails: string[];
      education: Array<{ institution: string }>;
      professions: Array<{ profession: string }>;
      workplaces: Array<{ organizationName: string }>;
      relatives: Array<{ relation: string }>;
    };
    expect(profile.phones).toEqual(phones);
    expect(profile.emails).toEqual(emails);
    expect(payload).toEqual(expect.objectContaining({ phone: phones[0], email: emails[0] }));
    expect(profile.education.map((item) => item.institution)).toEqual(['НГУ', 'МГУ']);
    expect(profile.professions[0].profession).toBe('Юрист');
    expect(profile.workplaces[0].organizationName).toBe('ООО Альфа');
    expect(profile.relatives[0].relation).toBe('Отец');
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

  it('loads full payment details before opening the client payment editor', async () => {
    const user = userEvent.setup();
    permissionsResult.payments.canEdit = true;
    const compactPayment = {
      id: 502,
      date: '2026-04-28',
      plannedDate: '2026-04-28',
      amount: 16_250,
      paidAmount: 0,
      outstandingAmount: 16_250,
      hasPartialPayment: false,
      status: 'Pending',
      description: 'Компактный платеж клиента',
      isPaid: false,
      notes: '',
      systemNotes: '',
      clientId: 1,
      type: 'Income',
      documentKind: 'Payment',
      createdAt: '2026-04-28T00:00:00Z',
      rescheduleCount: 0,
    } as Payment;
    const detailedPayment = { ...compactPayment, notes: 'Полные данные платежа' };
    usePaymentsPagedMock.mockReturnValue({
      ...emptyPaymentsResult,
      total: 1,
      payments: [compactPayment],
    });
    vi.mocked(apiService.getPayment).mockResolvedValueOnce(detailedPayment);

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="payments" />);

    const paymentRow = (await screen.findByText('Компактный платеж клиента')).closest('tr');
    const editButton = paymentRow?.querySelector<HTMLButtonElement>('button[aria-label="Редактировать"]');
    expect(editButton).toBeTruthy();
    await user.click(editButton!);

    await waitFor(() => {
      expect(apiService.getPayment).toHaveBeenCalledWith(502);
      expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(
        expect.objectContaining({ payment: detailedPayment, isOpen: true }),
      );
    });
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
