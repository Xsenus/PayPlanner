import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { UnifiedContacts } from './UnifiedContacts';
import { apiService, type UserSettingDto } from '../../services/api';
import type { Client, LegalEntitySummary } from '../../types';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 42, email: 'test@example.test', role: { id: 1, name: 'admin' } },
  }),
}));

vi.mock('../LegalEntities/LegalEntityModal', () => ({
  LegalEntityModal: () => null,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getClients: vi.fn(),
    getClientsV2: vi.fn(),
    getLegalEntities: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    getInvoices: vi.fn(),
    getLegalEntity: vi.fn(),
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
    mockApiData();
    mockUserSettings();
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
    expect(screen.getByText('Договоры')).toBeTruthy();
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
    expect(screen.queryByText('Договоры')).toBeNull();
    expect(screen.queryByText('Юрлицо полное')).toBeNull();
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
});
