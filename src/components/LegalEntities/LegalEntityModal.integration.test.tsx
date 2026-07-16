import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LegalEntityModal } from './LegalEntityModal';
import { apiService } from '../../services/api';
import type {
  ActsSummary,
  InvoiceSummary,
  LegalEntityDadataDetails,
  LegalEntityDetail,
  LegalEntitySuggestion,
  Payment,
} from '../../types';

const {
  confirmMock,
  emptyActsSummary,
  emptyInvoiceSummary,
  paymentModalPropsSpy,
  toastErrorMock,
  toastInfoMock,
  toastSuccessMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  emptyActsSummary: {
    created: { amount: 0, count: 0 },
    transferred: { amount: 0, count: 0 },
    signed: { amount: 0, count: 0 },
    terminated: { amount: 0, count: 0 },
    totalAmount: 0,
    totalCount: 0,
  } satisfies ActsSummary,
  emptyInvoiceSummary: {
    total: { amount: 0, count: 0 },
    pending: { amount: 0, count: 0 },
    paid: { amount: 0, count: 0 },
    overdue: { amount: 0, count: 0 },
  } satisfies InvoiceSummary,
  paymentModalPropsSpy: vi.fn(),
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
    getActsSummary: vi.fn().mockResolvedValue(emptyActsSummary),
    getCasesV1: vi.fn().mockResolvedValue([]),
    getClientsV2: vi.fn().mockResolvedValue({ items: [], total: 0 }),
    getInvoices: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 }),
    getInvoicesSummary: vi.fn().mockResolvedValue(emptyInvoiceSummary),
    getLegalEntityCaseFinanceSummaries: vi.fn().mockResolvedValue([]),
    getLegalEntities: vi.fn().mockResolvedValue([]),
    getMainCompanySettings: vi.fn().mockResolvedValue({
      defaultCompanyId: 9,
      companies: [{ id: 9, shortName: 'Наша компания', clientsCount: 0, createdAt: '2026-04-09T13:18:06Z' }],
    }),
    getPaymentsV2: vi.fn().mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 5,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    }),
    getPayment: vi.fn(),
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

vi.mock('../Calendar/PaymentModal', () => ({
  PaymentModal: (props: unknown) => {
    paymentModalPropsSpy(props);
    return null;
  },
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
    paymentModalPropsSpy.mockReset();
    vi.mocked(apiService.suggestClientAddresses).mockReset();
    vi.mocked(apiService.cleanAddress).mockReset();
    vi.mocked(apiService.getMainCompanySettings).mockReset();
    vi.mocked(apiService.getCasesV1).mockReset();
    vi.mocked(apiService.getInvoices).mockReset();
    vi.mocked(apiService.getInvoicesSummary).mockReset();
    vi.mocked(apiService.getActs).mockReset();
    vi.mocked(apiService.getActsSummary).mockReset();
    vi.mocked(apiService.getPaymentsV2).mockReset();
    vi.mocked(apiService.getPayment).mockReset();
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.suggestClientAddresses).mockResolvedValue([]);
    vi.mocked(apiService.cleanAddress).mockResolvedValue({ result: '' });
    vi.mocked(apiService.getMainCompanySettings).mockResolvedValue({
      defaultCompanyId: 9,
      companies: [{ id: 9, shortName: 'Наша компания', clientsCount: 0, createdAt: '2026-04-09T13:18:06Z' }],
    });
    vi.mocked(apiService.getCasesV1).mockResolvedValue([]);
    vi.mocked(apiService.getInvoices).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
    vi.mocked(apiService.getInvoicesSummary).mockResolvedValue(emptyInvoiceSummary);
    vi.mocked(apiService.getActs).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
    vi.mocked(apiService.getActsSummary).mockResolvedValue(emptyActsSummary);
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 5,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    });
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValue([]);
  });

  it('loads full payment details before opening the legal entity payment editor', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });
    const compactPayment = {
      id: 701,
      date: '2026-07-17',
      plannedDate: '2026-07-17',
      amount: 8_500,
      paidAmount: 0,
      type: 'Expense',
      status: 'Pending',
      description: 'Компактный платеж юрлица',
      notes: '',
      systemNotes: '',
      isPaid: false,
      rescheduleCount: 0,
      createdAt: '2026-07-17T00:00:00Z',
    } as Payment;
    vi.mocked(apiService.getPaymentsV2).mockResolvedValueOnce({
      items: [compactPayment],
      total: 1,
      page: 1,
      pageSize: 5,
      summary: {
        income: 0,
        expense: 8_500,
        balance: -8_500,
        incomeCount: 0,
        expenseCount: 1,
        total: 1,
      },
    });
    vi.mocked(apiService.getPayment).mockResolvedValueOnce({
      ...compactPayment,
      notes: 'Полные данные платежа',
    });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
        canViewPayments
      />,
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Компактный платеж юрлица' }));

    await waitFor(() => {
      expect(apiService.getPayment).toHaveBeenCalledWith(701);
      expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(
        expect.objectContaining({ isOpen: true, payment: expect.objectContaining({ id: 701 }) }),
      );
    });
  });

  it('does not load legal entity payments or case finance summaries without payment permission', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
        canViewPayments={false}
      />,
    );

    await waitFor(() => expect(apiService.getMainCompanySettings).toHaveBeenCalled());

    expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
    expect(apiService.getLegalEntityCaseFinanceSummaries).not.toHaveBeenCalled();
    expect(screen.queryByText('Платежи')).toBeNull();
  });

  it('does not load or show closed related sections in legal entity card', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
        canViewCases={false}
        canViewAccounts={false}
        canViewActs={false}
        canViewPayments={false}
      />,
    );

    await waitFor(() => expect(apiService.getMainCompanySettings).toHaveBeenCalled());

    expect(apiService.getCasesV1).not.toHaveBeenCalled();
    expect(apiService.getInvoices).not.toHaveBeenCalled();
    expect(apiService.getInvoicesSummary).not.toHaveBeenCalled();
    expect(apiService.getActs).not.toHaveBeenCalled();
    expect(apiService.getActsSummary).not.toHaveBeenCalled();
    expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
    expect(apiService.getLegalEntityCaseFinanceSummaries).not.toHaveBeenCalled();
    expect(screen.queryByText('Дела и документы')).toBeNull();
    expect(screen.queryByText('Счета')).toBeNull();
    expect(screen.queryByText('Акты')).toBeNull();
    expect(screen.queryByText('Платежи')).toBeNull();
  });

  it('prefills DaData query from saved inn and keeps the full address after suggestion selection', async () => {
    const suggestMock = vi
      .fn<(payload: { query?: string; inn?: string; limit?: number }) => Promise<LegalEntitySuggestion[]>>()
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
      .fn<(payload: { query: string; kpp?: string }) => Promise<LegalEntityDadataDetails>>()
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
      .fn<(payload: { query?: string; inn?: string; limit?: number }) => Promise<LegalEntitySuggestion[]>>()
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

  it('shows logo, seal and signature fields for own legal entities only', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
      />,
    );

    expect(await screen.findByText('Логотип компании')).toBeTruthy();
    expect(screen.getByText('Печать компании')).toBeTruthy();
    expect(screen.getByText('Подпись руководителя')).toBeTruthy();
  });

  it('hides logo, seal and signature fields for non-own legal entities', async () => {
    vi.mocked(apiService.getMainCompanySettings).mockResolvedValueOnce({
      defaultCompanyId: 7,
      companies: [{ id: 7, shortName: 'Наша компания', clientsCount: 0, createdAt: '2026-04-09T13:18:06Z' }],
    });
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Контрагент' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
      />,
    );

    await waitFor(() => expect(apiService.getMainCompanySettings).toHaveBeenCalled());
    expect(screen.queryByText('Логотип компании')).toBeNull();
    expect(screen.queryByText('Печать компании')).toBeNull();
    expect(screen.queryByText('Подпись руководителя')).toBeNull();
  });
});
