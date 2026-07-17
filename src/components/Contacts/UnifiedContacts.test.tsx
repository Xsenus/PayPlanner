import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { UnifiedContacts, formatDate } from './UnifiedContacts';
import { apiService, type UserSettingDto } from '../../services/api';
import type { Client, LegalEntityDetail, LegalEntitySummary } from '../../types';

type LegalEntityModalMockProps = {
  isOpen?: boolean;
  allowEdit?: boolean;
  allowDelete?: boolean;
  mode?: string;
  onBack?: () => void;
  onSubmit: (payload: Partial<LegalEntityDetail>) => Promise<void> | void;
  hideFormActions?: boolean;
  hideDadataSection?: boolean;
  compactReadOnlyView?: boolean;
  titleOverride?: string;
};

const legalEntityModalMock = vi.hoisted(() => vi.fn((props: LegalEntityModalMockProps) => {
  void props;
  return null;
}));
const toastSuccessMock = vi.hoisted(() => vi.fn());
const permissionsMock = vi.hoisted(() => {
  const section = {
    canView: true,
    canCreate: true,
    canEdit: true,
    canDelete: true,
    canExport: true,
  };

  return {
    value: {
      legalEntities: section,
      cases: section,
      accounts: section,
      acts: section,
      payments: section,
      contracts: section,
    },
    section,
  };
});

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 42, email: 'test@example.test', role: { id: 1, name: 'admin' } },
  }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => permissionsMock.value,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: toastSuccessMock,
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  }),
}));

vi.mock('../LegalEntities/LegalEntityModal', () => ({
  LegalEntityModal: legalEntityModalMock,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getClients: vi.fn(),
    getClientsV2: vi.fn(),
    getLegalEntities: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    getInvoices: vi.fn(),
    getLegalEntity: vi.fn(),
    updateLegalEntity: vi.fn(),
    suggestLegalEntities: vi.fn(),
    findLegalEntityById: vi.fn(),
    getUserSetting: vi.fn(),
    upsertUserSetting: vi.fn(),
  },
}));

function createSetting(key: string, value: string): UserSettingDto {
  return { key, value, updatedAt: '2026-04-27T10:00:00Z' };
}

function createLegalEntity(overrides: Partial<LegalEntitySummary> = {}): LegalEntitySummary {
  return {
    id: 21,
    shortName: 'ООО Ромашка',
    fullName: 'ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ РОМАШКА',
    inn: '6312000000',
    kpp: '631201001',
    ogrn: '1236300000000',
    address: 'Самара, улица Юридическая, 2',
    phone: '88460000000',
    email: 'legal@example.test',
    director: 'Петров Петр',
    responsibleUserName: 'Ответственный юрлица',
    country: 'Россия',
    organizationForm: 'ООО',
    taxSystem: 'УСН',
    taxSystemMode: 'Доходы',
    vatWithoutVat: true,
    okved: '69.10',
    okpo: '12345678',
    okato: '36401000000',
    oktmo: '36701000001',
    okogu: '4210014',
    okfs: '16',
    okopf: '12300',
    pfr: '077-001',
    fss: '6300000000',
    taxOfficeCode: '6312',
    taxRegion: 'Самарская область',
    ifnsName: 'ИФНС России по тестовому району',
    taxOfficePhone: '88469990000',
    treasuryInn: '6312999999',
    treasuryKpp: '631201001',
    treasuryRecipientName: 'УФК по Самарской области',
    treasuryBankBik: '013601205',
    treasuryBankName: 'ОТДЕЛЕНИЕ САМАРА',
    treasuryAccountNumber: '03212643000000014200',
    notes: 'Комментарий юрлица',
    clientsCount: 1,
    casesCount: 2,
    contractsCount: 3,
    invoicesCount: 4,
    actsCount: 5,
    paymentsCount: 6,
    relatedCases: [{ id: 201, label: 'Дело юрлица', clientId: null, legalEntityId: 21 }],
    relatedContracts: [{ id: 202, label: '№LC-1', legalEntityId: 21 }],
    relatedInvoices: [{ id: 203, label: '№LI-1', legalEntityId: 21, type: 'Income' }],
    relatedActs: [{ id: 204, label: '№LA-1', legalEntityId: 21 }],
    relatedPayments: [{ id: 205, label: '№LP-1', legalEntityId: 21, type: 'Income' }],
    createdAt: '2026-04-03T00:00:00Z',
    updatedAt: '2026-04-04T00:00:00Z',
    auditCreatedByUserName: 'Создатель юрлица',
    auditUpdatedByUserName: 'Редактор юрлица',
    clients: [],
    ...overrides,
  };
}

function createClient(overrides: Partial<Client> = {}): Client {
  return {
    id: 11,
    name: 'Иван Иванов',
    email: 'ivan@example.test',
    phone: '89990000000',
    company: 'Тестовая компания',
    address: 'Самара, улица Клиентская, 1',
    notes: 'Комментарий клиента',
    createdAt: '2026-04-01T00:00:00Z',
    auditCreatedByUserName: 'Администратор',
    auditUpdatedAt: '2026-04-02T00:00:00Z',
    auditUpdatedByUserName: 'Редактор',
    isActive: true,
    responsibleUserName: 'Ответственный клиент',
    clientStatus: {
      id: 1,
      name: 'В работе',
      description: '',
      colorHex: '#2563eb',
      isActive: true,
      createdAt: '2026-04-01T00:00:00Z',
    },
    cases: [
      {
        id: 101,
        clientId: 11,
        title: 'Дело клиента',
        status: 'Open',
        createdAt: '2026-04-01T00:00:00Z',
      },
    ],
    contracts: [],
    casesCount: 1,
    contractsCount: 2,
    invoicesCount: 3,
    actsCount: 4,
    paymentsCount: 5,
    relatedContracts: [{ id: 301, label: '№C-1', clientId: 11 }],
    relatedInvoices: [{ id: 302, label: '№I-1', clientId: 11, type: 'Income' }],
    relatedActs: [{ id: 303, label: '№A-1', clientId: 11 }],
    relatedPayments: [{ id: 304, label: '№P-1', clientId: 11, type: 'Income' }],
    legalEntityId: 21,
    legalEntity: createLegalEntity({ id: 21, shortName: 'ООО Клиент', inn: '6311000000' }),
    ...overrides,
  };
}

function mockUserSettings(values: Record<string, string | null> = {}) {
  vi.mocked(apiService.getUserSetting).mockImplementation(async (key: string) => {
    const value = values[key];
    return value == null ? null : createSetting(key, value);
  });
}

function mockApiData() {
  const clients = [createClient()];
  const legalEntities = [createLegalEntity()];
  vi.mocked(apiService.getClients).mockResolvedValue(clients);
  vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: clients, total: clients.length, page: 1, pageSize: 24 });
  vi.mocked(apiService.getLegalEntities).mockResolvedValue(legalEntities);
  vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: legalEntities, total: legalEntities.length, page: 1, pageSize: 24 });
  vi.mocked(apiService.getLegalEntity).mockResolvedValue(createLegalEntity() as LegalEntityDetail);
  vi.mocked(apiService.updateLegalEntity).mockResolvedValue(createLegalEntity({ shortName: 'ООО Обновлено' }) as LegalEntityDetail);
  vi.mocked(apiService.getInvoices).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 1 });
  vi.mocked(apiService.upsertUserSetting).mockResolvedValue(createSetting('saved', 'ok'));
}

async function openColumnSettings() {
  await screen.findByRole('button', { name: 'Настроить колонки' });
  fireEvent.click(screen.getByRole('button', { name: 'Настроить колонки' }));
  return screen.findByText('Сбросить настройки');
}

describe('UnifiedContacts', () => {
  beforeEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    vi.clearAllMocks();
    permissionsMock.value = {
      legalEntities: permissionsMock.section,
      cases: permissionsMock.section,
      accounts: permissionsMock.section,
      acts: permissionsMock.section,
      payments: permissionsMock.section,
      contracts: permissionsMock.section,
    };
    mockApiData();
    mockUserSettings();
  });

  it('formats contact exact dates in the selected profile time zone', () => {
    expect(formatDate('2026-06-04T22:30:00Z', 'Europe/Samara')).toBe('05.06.2026');
    expect(formatDate('2026-06-04T22:30:00Z', 'America/New_York')).toBe('04.06.2026');
  });

  it('loads table view mode for the combined contacts section from user settings', async () => {
    mockUserSettings({ contacts_view_mode: 'table' });

    render(<UnifiedContacts />);

    expect(await screen.findByRole('columnheader', { name: /Контакт/ })).toBeTruthy();
    expect(apiService.getUserSetting).toHaveBeenCalledWith('contacts_view_mode');
    expect(apiService.getUserSetting).toHaveBeenCalledWith('contacts_table_settings');
  });

  it('saves the selected view mode to user settings for the current section', async () => {
    mockUserSettings({ contacts_client_view_mode: 'table' });

    render(<UnifiedContacts initialKind="client" lockKind />);
    await screen.findByRole('columnheader', { name: /Физическое лицо/ });

    fireEvent.click(screen.getByRole('button', { name: 'Показать карточки' }));

    await waitFor(() => {
      expect(apiService.upsertUserSetting).toHaveBeenCalledWith('contacts_client_view_mode', 'cards');
    });
  });

  it('shows only compact shared columns in the combined contacts column picker', async () => {
    mockUserSettings({ contacts_view_mode: 'table' });

    render(<UnifiedContacts />);
    await openColumnSettings();

    expect(screen.getAllByText('Контакт').length).toBeGreaterThan(0);
    expect(screen.getAllByText('КПП').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Дела').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Договоры').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Счета').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Акты').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Платежи').length).toBeGreaterThan(0);
    expect(screen.queryByText('Юрлицо полное')).toBeNull();
    expect(screen.queryByText('ОКВЭД')).toBeNull();
    expect(screen.queryByText('Счет казначейства')).toBeNull();
  });

  it('shows client-specific columns in the physical persons subgroup', async () => {
    mockUserSettings({ contacts_client_view_mode: 'table' });

    render(<UnifiedContacts initialKind="client" lockKind />);
    await openColumnSettings();

    expect(screen.getAllByText('Физическое лицо').length).toBeGreaterThan(0);
    expect(screen.getByText('Юрлицо полное')).toBeTruthy();
    expect(screen.getAllByText('Договоры').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Акты').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Платежи').length).toBeGreaterThan(0);
    expect(screen.getByText('Кто обновил')).toBeTruthy();
    expect(screen.queryByText('ОКВЭД')).toBeNull();
    expect(screen.queryByText('Счет казначейства')).toBeNull();
  });

  it('shows legal-entity-specific columns in the legal persons subgroup', async () => {
    mockUserSettings({ contacts_legal_view_mode: 'table' });

    render(<UnifiedContacts initialKind="legal" lockKind />);
    await openColumnSettings();

    expect(screen.getAllByText('Юридическое лицо').length).toBeGreaterThan(0);
    expect(screen.getByText('ОКВЭД')).toBeTruthy();
    expect(screen.getByText('ИФНС')).toBeTruthy();
    expect(screen.getByText('Счет казначейства')).toBeTruthy();
    expect(screen.getAllByText('Договоры').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Акты').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Платежи').length).toBeGreaterThan(0);
    expect(screen.queryByText('Юрлицо полное')).toBeNull();
  });

  it('renders related document links in the combined contacts table', async () => {
    mockUserSettings({ contacts_view_mode: 'table' });

    render(
      <UnifiedContacts
        onOpenCase={vi.fn()}
        onOpenContract={vi.fn()}
        onOpenInvoice={vi.fn()}
        onOpenAct={vi.fn()}
        onOpenPayment={vi.fn()}
      />,
    );

    const clientCell = await screen.findByText('Иван Иванов');
    const clientRow = clientCell.closest('tr');
    expect(clientRow).not.toBeNull();
    expect(within(clientRow as HTMLElement).getByRole('button', { name: 'Дело клиента' })).toBeTruthy();
    expect(within(clientRow as HTMLElement).getByRole('button', { name: '№C-1' })).toBeTruthy();
    expect(within(clientRow as HTMLElement).getByRole('button', { name: '№I-1' })).toBeTruthy();
    expect(within(clientRow as HTMLElement).getByRole('button', { name: '№A-1' })).toBeTruthy();
    expect(within(clientRow as HTMLElement).getByRole('button', { name: '№P-1' })).toBeTruthy();
  });

  it('does not restore linked section columns from saved settings without permissions', async () => {
    const deniedSection = { ...permissionsMock.section, canView: false };
    permissionsMock.value = {
      legalEntities: deniedSection,
      cases: deniedSection,
      accounts: deniedSection,
      acts: deniedSection,
      payments: deniedSection,
      contracts: deniedSection,
    };
    mockUserSettings({
      contacts_client_view_mode: 'table',
      contacts_client_table_settings: JSON.stringify({
        visibleColumns: ['contact', 'legalShortName', 'legalInn', 'cases', 'contracts', 'invoices', 'acts', 'payments'],
        columnOrder: ['contact', 'legalShortName', 'legalInn', 'cases', 'contracts', 'invoices', 'acts', 'payments'],
        columnWidths: {},
        pageSize: 24,
      }),
    });

    render(<UnifiedContacts initialKind="client" lockKind canViewLegalEntities={false} />);

    expect(await screen.findByRole('columnheader', { name: /Физическое лицо/ })).toBeTruthy();
    expect(screen.queryByRole('columnheader', { name: /Юрлицо/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /ИНН юрлица/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Дела/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Договоры/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Счета/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Акты/ })).toBeNull();
    expect(screen.queryByRole('columnheader', { name: /Платежи/ })).toBeNull();
  });

  it('hides legal clients counters and unavailable related counters in contact cards', async () => {
    const deniedSection = { ...permissionsMock.section, canView: false };
    permissionsMock.value = {
      legalEntities: permissionsMock.section,
      cases: deniedSection,
      accounts: deniedSection,
      acts: deniedSection,
      payments: deniedSection,
      contracts: deniedSection,
    };

    render(<UnifiedContacts initialKind="legal" lockKind canViewClients={false} />);

    const legalTitle = await screen.findByText('ООО Ромашка');
    const legalCard = legalTitle.closest('button');
    expect(legalCard).not.toBeNull();
    expect(within(legalCard as HTMLElement).queryByText('1 клиент(ов)')).toBeNull();
    expect(within(legalCard as HTMLElement).getAllByText('—').length).toBeGreaterThanOrEqual(3);
  });

  it('renders selected hidden legal columns after they are enabled through persisted table settings', async () => {
    mockUserSettings({
      contacts_legal_view_mode: 'table',
      contacts_legal_table_settings: JSON.stringify({
        visibleColumns: ['contact', 'inn', 'okved', 'treasuryAccountNumber'],
        columnOrder: ['contact', 'inn', 'okved', 'treasuryAccountNumber'],
        columnWidths: {},
        pageSize: 24,
      }),
    });

    render(<UnifiedContacts initialKind="legal" lockKind />);

    expect(await screen.findByRole('columnheader', { name: /ОКВЭД/ })).toBeTruthy();
    expect(screen.getByRole('columnheader', { name: /Счет казначейства/ })).toBeTruthy();

    const row = screen.getByText('ООО Ромашка').closest('tr');
    expect(row).not.toBeNull();
    expect(within(row as HTMLElement).getByText('69.10')).toBeTruthy();
    expect(within(row as HTMLElement).getByText('03212643000000014200')).toBeTruthy();
  });

  it('opens legal entity from contacts as editable page', async () => {
    const legalEntity = createLegalEntity({ shortName: 'ООО Ромашка' });
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({
      items: [legalEntity],
      total: 1,
      page: 1,
      pageSize: 24,
    });
    vi.mocked(apiService.getLegalEntity).mockResolvedValue(legalEntity as LegalEntityDetail);

    render(<UnifiedContacts initialKind="legal" lockKind />);

    fireEvent.click(await screen.findByText('ООО Ромашка'));

    await waitFor(() => {
      expect(apiService.getLegalEntity).toHaveBeenCalledWith(21);
    });

    await waitFor(() => {
      const props = legalEntityModalMock.mock.calls.at(-1)?.[0];
      expect(props).toBeTruthy();
      if (!props) throw new Error('Legal entity modal props not captured');
      expect(props).toMatchObject({
        isOpen: true,
        allowEdit: true,
        allowDelete: false,
        mode: 'page',
      });
      expect(typeof props.onBack).toBe('function');
      expect(props.hideFormActions).toBeUndefined();
      expect(props.hideDadataSection).toBeUndefined();
      expect(props.compactReadOnlyView).toBeUndefined();
      expect(props.titleOverride).toBeUndefined();
    });
  });

  it('saves legal entity changes from the contacts detail page', async () => {
    const legalEntity = createLegalEntity({ shortName: 'ООО Ромашка' });
    const savedEntity = createLegalEntity({ shortName: 'ООО Ромашка плюс' }) as LegalEntityDetail;
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({
      items: [legalEntity],
      total: 1,
      page: 1,
      pageSize: 24,
    });
    vi.mocked(apiService.getLegalEntity).mockResolvedValue(legalEntity as LegalEntityDetail);
    vi.mocked(apiService.updateLegalEntity).mockResolvedValue(savedEntity);

    render(<UnifiedContacts initialKind="legal" lockKind />);

    fireEvent.click(await screen.findByText('ООО Ромашка'));

    await waitFor(() => {
      expect(apiService.getLegalEntity).toHaveBeenCalledWith(21);
    });

    const props = legalEntityModalMock.mock.calls.at(-1)?.[0];
    expect(props).toBeTruthy();
    if (!props) throw new Error('Legal entity modal props not captured');
    const payload = { shortName: 'ООО Ромашка плюс' } as Parameters<typeof props.onSubmit>[0];
    await props.onSubmit(payload);

    expect(apiService.updateLegalEntity).toHaveBeenCalledWith(21, payload);
    expect(toastSuccessMock).toHaveBeenCalledWith('Юр. лицо обновлено');
  });

  it('aborts an obsolete client request and keeps the latest response', async () => {
    const signals: AbortSignal[] = [];
    const resolvers: Array<(value: {
      items: Client[];
      total: number;
      page: number;
      pageSize: number;
    }) => void> = [];
    vi.mocked(apiService.getClientsV2).mockImplementation((_params, options) => {
      if (options?.signal) signals.push(options.signal);
      return new Promise((resolve) => resolvers.push(resolve));
    });

    render(<UnifiedContacts initialKind="client" lockKind />);

    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(1));
    expect(signals[0]?.aborted).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));

    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(2));
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(false);

    await act(async () => {
      resolvers[1]({
        items: [createClient({ id: 12, name: 'Текущий клиент' })],
        total: 1,
        page: 1,
        pageSize: 24,
      });
    });
    expect(await screen.findByText('Текущий клиент')).toBeTruthy();

    await act(async () => {
      resolvers[0]({
        items: [createClient({ id: 13, name: 'Устаревший клиент' })],
        total: 1,
        page: 1,
        pageSize: 24,
      });
    });
    expect(screen.queryByText('Устаревший клиент')).toBeNull();
    expect(screen.getByText('Текущий клиент')).toBeTruthy();
  });

  it('passes one signal to both contact sources and aborts them on unmount', async () => {
    let clientsSignal: AbortSignal | undefined;
    let legalEntitiesSignal: AbortSignal | undefined;
    vi.mocked(apiService.getClientsV2).mockImplementation((_params, options) => {
      clientsSignal = options?.signal;
      return new Promise(() => undefined);
    });
    vi.mocked(apiService.getLegalEntitiesPaged).mockImplementation((_params, options) => {
      legalEntitiesSignal = options?.signal;
      return new Promise(() => undefined);
    });

    const { unmount } = render(<UnifiedContacts />);

    await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(apiService.getLegalEntitiesPaged).toHaveBeenCalledTimes(1));
    expect(clientsSignal).toBe(legalEntitiesSignal);
    expect(clientsSignal?.aborted).toBe(false);

    unmount();

    expect(clientsSignal?.aborted).toBe(true);
    expect(legalEntitiesSignal?.aborted).toBe(true);
  });
});
