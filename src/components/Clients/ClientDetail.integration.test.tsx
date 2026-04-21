import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ClientDetail } from './ClientDetail';
import type { Client, PowerOfAttorney } from '../../types';
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
  permissionsResult,
  toastInfoMock,
  toastErrorMock,
  toastSuccessMock,
  toastWarningMock,
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
  permissionsResult: createPermissions(),
  toastInfoMock: vi.fn(),
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
  toastWarningMock: vi.fn(),
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
    isAdmin: () => false,
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
    createClientPowerOfAttorney: vi.fn(),
    updateClientPowerOfAttorney: vi.fn(),
    deleteClientPowerOfAttorney: vi.fn(),
    getOksoDictionary: vi.fn(),
    getOkpdtrDictionary: vi.fn(),
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
  PaymentModal: () => null,
}));

vi.mock('../Acts/ActModal', () => ({
  ActModal: () => null,
}));

vi.mock('../Accounts/InvoiceModal', () => ({
  InvoiceModal: () => null,
}));

vi.mock('../Contracts/ContractModal', () => ({
  ContractModal: () => null,
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
  ObjectAuditLauncher: () => null,
}));

vi.mock('./ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

describe('ClientDetail integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    confirmMock.mockResolvedValue(true);
    toastInfoMock.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    toastWarningMock.mockReset();
    twoTypeStatsMock.mockReset();
    useClientCasesMock.mockReset();
    useActsMock.mockReset();
    useInvoicesMock.mockReset();
    useContractsMock.mockReset();
    usePaymentsPagedMock.mockReset();

    useClientCasesMock.mockReturnValue(emptyClientCasesResult);
    useActsMock.mockReturnValue(emptyActsResult);
    useInvoicesMock.mockReturnValue(emptyInvoicesResult);
    useContractsMock.mockReturnValue(emptyContractsResult);
    usePaymentsPagedMock.mockReturnValue(emptyPaymentsResult);

    vi.mocked(apiService.getClient).mockResolvedValue(createClient());
    vi.mocked(apiService.updateClient).mockResolvedValue(createClient());
    vi.mocked(apiService.getClientPowersOfAttorney).mockResolvedValue([]);
    vi.mocked(apiService.getClientCaseFinanceSummaries).mockResolvedValue([]);
    vi.mocked(apiService.createClientPowerOfAttorney).mockResolvedValue(createPowerOfAttorney());
    vi.mocked(apiService.updateClientPowerOfAttorney).mockResolvedValue(createPowerOfAttorney());
    vi.mocked(apiService.deleteClientPowerOfAttorney).mockResolvedValue(undefined);
    vi.mocked(apiService.getOksoDictionary).mockResolvedValue([]);
    vi.mocked(apiService.getOkpdtrDictionary).mockResolvedValue([]);
  });

  it('shows delete controls in profile subsections and removes an education row', async () => {
    const user = userEvent.setup();

    render(<ClientDetail clientId={1} onBack={vi.fn()} initialSection="profile" />);

    await screen.findByDisplayValue('Иванов');

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
