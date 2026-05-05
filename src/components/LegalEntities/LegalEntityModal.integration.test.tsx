import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LegalEntityModal } from './LegalEntityModal';
import { apiService } from '../../services/api';
import type {
  LegalEntityDadataDetails,
  LegalEntityDetail,
  LegalEntitySuggestion,
} from '../../types';

const {
  confirmMock,
  toastErrorMock,
  toastInfoMock,
  toastSuccessMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  toastErrorMock: vi.fn(),
  toastInfoMock: vi.fn(),
  toastSuccessMock: vi.fn(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => '' }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    error: toastErrorMock,
    info: toastInfoMock,
    success: toastSuccessMock,
  }),
}));

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    countries: [],
    baseCurrencies: [],
    serviceLanguages: [],
    organizationForms: [],
    timezones: [],
    taxSystems: [],
    taxSystemModes: [],
  }),
}));

vi.mock('../../hooks/useFormRequirements', () => ({
  useFormRequirements: () => ({
    isRequired: () => false,
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getActResponsibles: vi.fn().mockResolvedValue([]),
    getActs: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 }),
    getActsSummary: vi.fn().mockResolvedValue(null),
    getCasesV1: vi.fn().mockResolvedValue([]),
    getClientsV2: vi.fn().mockResolvedValue({ items: [], total: 0 }),
    getInvoices: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 }),
    getInvoicesSummary: vi.fn().mockResolvedValue(null),
    getLegalEntities: vi.fn().mockResolvedValue([]),
    suggestClientAddresses: vi.fn().mockResolvedValue([]),
    cleanAddress: vi.fn().mockResolvedValue({ result: '' }),
  },
}));

vi.mock('../Dictionaries/DictionariesModal', () => ({
  default: () => null,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => null,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

function createInitialLegalEntity(
  overrides: Partial<LegalEntityDetail> = {},
): LegalEntityDetail {
  return {
    id: 9,
    shortName: '',
    fullName: '',
    inn: '',
    createdAt: '2026-04-09T13:18:06Z',
    updatedAt: '2026-04-10T06:18:36Z',
    clients: [],
    addresses: [],
    ...overrides,
  } as LegalEntityDetail;
}

describe('LegalEntityModal integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    toastErrorMock.mockReset();
    toastInfoMock.mockReset();
    toastSuccessMock.mockReset();
    vi.mocked(apiService.suggestClientAddresses).mockReset();
    vi.mocked(apiService.cleanAddress).mockReset();
    vi.mocked(apiService.suggestClientAddresses).mockResolvedValue([]);
    vi.mocked(apiService.cleanAddress).mockResolvedValue({ result: '' });
  });

  it('prefills DaData query from saved inn and keeps the full address after suggestion selection', async () => {
    const suggestMock = vi
      .fn<[{ query?: string; inn?: string; limit?: number }], Promise<LegalEntitySuggestion[]>>()
      .mockResolvedValue([
        {
          shortName: 'ИП Кожеваткин Андрей Владимирович',
          fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
          inn: '633009620345',
          ogrn: '317631300153603',
          address: 'Самарская обл, г Новокуйбышевск',
        },
      ]);

    const details: LegalEntityDadataDetails = {
      shortName: 'ИП Кожеваткин Андрей Владимирович',
      fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
      inn: '633009620345',
      ogrn: '317631300153603',
      address: 'Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
      addressUnrestricted:
        '633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
      country: 'Россия',
      postalCode: '633208',
      regionWithType: 'Новосибирская обл',
      cityWithType: 'г Искитим',
      streetWithType: 'Южный мкр',
      house: '37',
      flat: '19',
    };

    const findByIdMock = vi
      .fn<[{ query: string; kpp?: string }], Promise<LegalEntityDadataDetails>>()
      .mockResolvedValue(details);

    const initial = createInitialLegalEntity({ inn: '633009620345' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={suggestMock}
        findById={findByIdMock}
        allowEdit
        allowDelete={false}
      />,
    );

    const user = userEvent.setup();
    const dadataInput = screen.getByPlaceholderText(
      'Например: 7707083893 или ООО Пример',
    ) as HTMLInputElement;
    expect(dadataInput.value).toBe('633009620345');

    await user.click(screen.getByRole('button', { name: 'Найти' }));

    await waitFor(() =>
      expect(suggestMock).toHaveBeenCalledWith({ query: '633009620345', limit: 6 }),
    );

    await user.click(await screen.findByText('ИП Кожеваткин Андрей Владимирович'));

    await waitFor(() =>
      expect(findByIdMock).toHaveBeenCalledWith({ query: '633009620345', kpp: undefined }),
    );

    await user.click(screen.getByRole('button', { name: 'Адреса' }));

    expect(
      await screen.findByDisplayValue(
        '633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
      ),
    ).toBeTruthy();
    expect(screen.getByDisplayValue('633208')).toBeTruthy();
    expect(screen.getByDisplayValue('Новосибирская обл')).toBeTruthy();
    expect(screen.getByDisplayValue('г Искитим')).toBeTruthy();
    expect(screen.getByDisplayValue('Южный мкр')).toBeTruthy();
    expect(screen.getByDisplayValue('37')).toBeTruthy();
    expect(screen.getByDisplayValue('19')).toBeTruthy();
  }, 15000);

  it('deduplicates repeated DaData suggestions in the modal UI', async () => {
    const suggestMock = vi
      .fn<[{ query?: string; inn?: string; limit?: number }], Promise<LegalEntitySuggestion[]>>()
      .mockResolvedValue([
        {
          shortName: 'ИП Кожеваткин Андрей Владимирович',
          fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
          inn: '633009620345',
          ogrn: '317631300153603',
          address: 'Самарская обл, г Новокуйбышевск',
        },
        {
          shortName: 'ИП Кожеваткин Андрей Владимирович',
          fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
          inn: '633009620345',
          ogrn: '307631300200020',
          address: 'Самарская обл, г Новокуйбышевск',
        },
        {
          shortName: 'ООО Пример',
          fullName: 'Общество с ограниченной ответственностью Пример',
          inn: '7707083893',
          kpp: '770701001',
          ogrn: '1027700132195',
          address: 'г Москва, ул Ильинка, д 9',
        },
      ]);

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(createInitialLegalEntity())}
        initial={createInitialLegalEntity()}
        suggest={suggestMock}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
      />,
    );

    const user = userEvent.setup();
    await user.type(
      screen.getByPlaceholderText('Например: 7707083893 или ООО Пример'),
      'Кожеваткин',
    );
    await user.click(screen.getByRole('button', { name: 'Найти' }));

    await waitFor(() =>
      expect(suggestMock).toHaveBeenCalledWith({ query: 'Кожеваткин', limit: 6 }),
    );

    expect(await screen.findAllByText('ИП Кожеваткин Андрей Владимирович')).toHaveLength(1);
    expect(screen.getByText('ООО Пример')).toBeTruthy();
  });

  it('renders company profile page as read-only when profile mode hides editing controls', () => {
    const initial = createInitialLegalEntity({
      shortName: 'ИП Кожеваткин Андрей Владимирович',
      fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
      inn: '633009620345',
      ogrn: '317631300153603',
      director: 'Кожеваткин Андрей Владимирович',
    });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit={false}
        allowDelete={false}
        mode="page"
        onBack={vi.fn()}
        titleOverride="Профиль компании"
        hidePageNavigation
        hideFormActions
        hideDadataSection
        hideMetaInfo
        hideAuditSection
        hideDictionaryButtons
        compactReadOnlyView
      />,
    );

    expect(screen.getByText('Профиль компании')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Назад' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
    expect(screen.queryByText('Заполнить по данным DaData')).toBeNull();
    expect(screen.queryByText('Создано')).toBeNull();
    expect(screen.queryByText('Справочник банков')).toBeNull();
    expect(screen.queryByText('Действия')).toBeNull();
    expect(screen.queryByText('+ Добавить телефон')).toBeNull();
    expect(screen.queryByText('+ Добавить email')).toBeNull();
    expect(screen.queryByText('Справочник')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Найти адрес' })).toBeNull();
    expect(screen.getByDisplayValue('ИП Кожеваткин Андрей Владимирович')).toHaveProperty('disabled', true);
    expect(screen.getByDisplayValue('633009620345')).toHaveProperty('disabled', true);
  });
});
