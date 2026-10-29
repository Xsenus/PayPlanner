import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { imageFixtureBytes, imageFixtureFile, installBlobArrayBufferForTests } from './legalEntityImageTestFixtures';

import { LegalEntityModal } from './LegalEntityModal';
import { apiService } from '../../services/api';
import type { MenuSectionPermissions } from '../../types/permissions';
import type {
  ActsSummary,
  InvoiceSummary,
  LegalEntityDadataDetails,
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntitySuggestion,
  Payment,
} from '../../types';

let restoreArrayBuffer: () => void;
beforeAll(() => { restoreArrayBuffer = installBlobArrayBufferForTests(); });
afterAll(() => { restoreArrayBuffer(); });

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
    getCompanyRequisitesCheck: vi.fn().mockResolvedValue({ legalEntityId: 9, issues: [] }),
    getCompanyBranches: vi.fn().mockResolvedValue([]),
    getBankConnections: vi.fn().mockResolvedValue([]),
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
    return <div data-testid="legal-related-payment-modal" />;
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

function deferCompanyImageProcessing() {
  const images: DeferredImage[] = [];
  let drawnSource = '';
  class DeferredImage {
    width = 400;
    height = 400;
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    source = '';
    set src(value: string) { this.source = value; images.push(this); }
    removeAttribute(name: string) { if (name === 'src') this.source = ''; }
  }
  const context = {
    clearRect: vi.fn(),
    drawImage: vi.fn((image: DeferredImage) => { drawnSource = image.source; }),
    save: vi.fn(), restore: vi.fn(), fillRect: vi.fn(),
  };
  const getContext = vi.spyOn(HTMLCanvasElement.prototype, 'getContext')
    .mockReturnValue(context as unknown as CanvasRenderingContext2D);
  const encode = vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(() => drawnSource);
  vi.stubGlobal('Image', DeferredImage);
  return {
    images,
    restore() { vi.unstubAllGlobals(); getContext.mockRestore(); encode.mockRestore(); },
  };
}

describe('LegalEntityModal integration', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCompanyBranches).mockClear();
    vi.mocked(apiService.getCompanyRequisitesCheck).mockClear();
    vi.mocked(apiService.getBankConnections).mockClear();
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

  const relatedPermissions = (allowed: Partial<MenuSectionPermissions> = {}): MenuSectionPermissions => ({
    canView: true, canCreate: false, canEdit: false, canDelete: false, canExport: false, ...allowed,
  });

  it('related permissions: viewing sections does not enable creation from an editable company profile', async () => {
    const initial = createInitialLegalEntity({ shortName: 'Editable profile' });
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()}
      allowEdit allowDelete={false} relatedRecordPermissions={{ cases: relatedPermissions(), accounts: relatedPermissions(), acts: relatedPermissions(), payments: relatedPermissions() }} />);
    for (const name of ['Дело', 'Счёт', 'Акт', 'Платёж']) {
      for (const button of await screen.findAllByRole('button', { name })) expect(button).toHaveProperty('disabled', true);
    }
    expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
  });

  it.each(['cases', 'accounts', 'acts', 'payments'] as const)('related permissions: only independently granted %s creation is available', async section => {
    const initial = createInitialLegalEntity({ shortName: 'Editable profile' });
    const props = {
      isOpen: true, initial, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn(), allowEdit: true, allowDelete: false,
      relatedRecordPermissions: { [section]: relatedPermissions({ canCreate: true }) },
    };
    render(<LegalEntityModal {...props} />);
    const names = { cases: 'Дело', accounts: 'Счёт', acts: 'Акт', payments: 'Платёж' };
    for (const [key, name] of Object.entries(names)) {
      for (const button of await screen.findAllByRole('button', { name })) expect(button).toHaveProperty('disabled', key !== section);
    }
    if (section === 'payments') {
      await userEvent.click(screen.getByRole('button', { name: 'Платёж' }));
      expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy();
      expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({ canCreateLinkedInvoices: false }));
    }
  });

  it('related permissions: explicit account creation prohibition applies without an additional matrix', async () => {
    const initial = createInitialLegalEntity();
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()}
      allowEdit allowDelete={false} canViewAccounts canCreateAccounts={false} />);
    for (const button of await screen.findAllByRole('button', { name: 'Счёт' })) expect(button).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Платёж' })).toHaveProperty('disabled', false);
  });

  it('related permissions: an opened payment form disappears immediately when its creation permission is revoked', async () => {
    const initial = createInitialLegalEntity();
    const props = { isOpen: true, initial, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn(), allowEdit: true, allowDelete: false };
    const { rerender } = render(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions({ canCreate: true }) }} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Платёж' }));
    expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy();
    rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions() }} />);
    expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
    expect(screen.getByRole('button', { name: 'Платёж' })).toHaveProperty('disabled', true);
  });

  it.each([false, true])('related permissions: payment viewing preserves independent edit permission=%s', async canEdit => {
    const initial = createInitialLegalEntity();
    const payment = { id: 901, description: 'Visible related payment', date: '2026-10-23', type: 'Expense', status: 'Pending', amount: 100, paidAmount: 0 } as Payment;
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({ items: [payment], total: 1, page: 1, pageSize: 5,
      summary: { income: 0, expense: 100, balance: -100, incomeCount: 0, expenseCount: 1, total: 1 } });
    vi.mocked(apiService.getPayment).mockResolvedValue(payment);
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()}
      allowEdit allowDelete={false} relatedRecordPermissions={{ payments: relatedPermissions({ canEdit }) }} />);
    const button = await screen.findByRole('button', { name: payment.description! });
    expect(button).toHaveProperty('disabled', !canEdit);
    await userEvent.click(button);
    if (canEdit) {
      expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy();
      expect(apiService.getPayment).toHaveBeenCalledWith(901);
    } else {
      expect(apiService.getPayment).not.toHaveBeenCalled();
      expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
    }
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

  it('allows delegated profile requisites editing without administrator or financial actions', async () => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Assigned own company' });
    const onSubmit = vi.fn().mockResolvedValue(initial);
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={onSubmit}
      suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false}
      mode="page" canManageOwnCompanySettings={false} canViewCases={false} canViewAccounts={false}
      canCreateAccounts={false} canViewActs={false} canViewPayments={false} />);
    await screen.findByText('Печать компании');
    expect(vi.mocked(apiService.getCompanyBranches)).not.toHaveBeenCalled();
    expect(vi.mocked(apiService.getCompanyRequisitesCheck)).not.toHaveBeenCalled();
    expect(vi.mocked(apiService.getBankConnections)).not.toHaveBeenCalled();
    expect(screen.queryByLabelText('Состояние профиля компании')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Добавить клиента' })).toBeNull();
    const name = screen.getByDisplayValue('Assigned own company');
    expect(name).toHaveProperty('disabled', false);
    await user.clear(name); await user.type(name, 'Updated own company');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ shortName: 'Updated own company' })));
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

  it.each([
    ['page', 'Печать компании'], ['page', 'Подпись руководителя'],
    ['modal', 'Печать компании'], ['modal', 'Подпись руководителя'],
  ] as const)('keeps %s form values and clears the %s file input after an oversized PNG rejection', async (mode, label) => {
    const user = userEvent.setup();
    const logo = 'data:image/png;base64,existing-logo';
    const initial = createInitialLegalEntity({ shortName: 'Неизменённая компания', logoDataUrl: logo });
    const submit = vi.fn();
    const context = { clearRect: vi.fn(), drawImage: vi.fn(), save: vi.fn(), restore: vi.fn(), fillRect: vi.fn() };
    const getContext = vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D);
    const encode = vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(mime => mime === 'image/png'
      ? 'P'.repeat(500_000) : 'data:image/jpeg;base64,incorrect-white-background');
    class DecodedImage {
      width = 400;
      height = 400;
      onload: (() => void) | null = null;
      set src(_value: string) { queueMicrotask(() => this.onload?.()); }
    }
    vi.stubGlobal('Image', DecodedImage);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const title = await screen.findByText(label);
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      expect(input).not.toBeNull();
      const companyName = screen.getByDisplayValue('Неизменённая компания');
      await user.clear(companyName);
      await user.type(companyName, 'Несохранённое название');
      const file = imageFixtureFile('same-seal.png');
      await user.upload(input, file);
      await screen.findByText(`${label}: PNG слишком большой для сохранения. Выберите PNG меньшего размера с прозрачным фоном.`);
      await waitFor(() => expect(input.value).toBe(''));
      expect((companyName as HTMLInputElement).value).toBe('Несохранённое название');
      expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(logo);
      expect(context.fillRect).not.toHaveBeenCalled();
      expect(encode).toHaveBeenCalledExactlyOnceWith('image/png');
      await user.upload(input, file);
      await waitFor(() => expect(encode).toHaveBeenCalledTimes(2));
      await waitFor(() => expect(input.value).toBe(''));
      expect(submit).not.toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
      getContext.mockRestore();
      encode.mockRestore();
    }
  });

  it.each([
    ['page', 'Логотип компании', 'logoDataUrl'], ['modal', 'Логотип компании', 'logoDataUrl'],
    ['page', 'Печать компании', 'sealDataUrl'], ['modal', 'Печать компании', 'sealDataUrl'],
    ['page', 'Подпись руководителя', 'directorSignatureDataUrl'], ['modal', 'Подпись руководителя', 'directorSignatureDataUrl'],
  ] as const)('keeps the latest %s %s upload and prevents saving while it is processing', async (mode, label, field) => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const initial = createInitialLegalEntity({ shortName: 'Компания' });
    const submit = vi.fn().mockResolvedValue(initial);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const title = await screen.findByText(label);
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, imageFixtureFile('first.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const oldSource = deferred.images[0].source;
      const oldLoad = deferred.images[0].onload;
      const oldError = deferred.images[0].onerror;
      await user.upload(input, imageFixtureFile('latest.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(2));
      expect(deferred.images[1].source).not.toBe(oldSource);
      const save = screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement;
      expect(save.disabled).toBe(true);
      fireEvent.submit(save.closest('form')!);
      expect(submit).not.toHaveBeenCalled();
      expect(screen.getByText('Дождитесь окончания обработки изображений перед сохранением.')).toBeTruthy();
      const latest = deferred.images[1].source;
      await act(async () => { deferred.images[1].onload?.(); });
      await waitFor(() => expect(save.disabled).toBe(false));
      expect(screen.queryByText('Дождитесь окончания обработки изображений перед сохранением.')).toBeNull();
      await act(async () => { oldLoad?.(); oldError?.(); });
      expect(screen.getByAltText(label).getAttribute('src')).toBe(latest);
      expect(screen.queryByText('Не удалось обработать изображение.')).toBeNull();
      await user.click(save);
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ [field]: latest, shortName: 'Компания' });
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('cancels a pending replacement when restoring the saved seal in %s mode', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const initial = createInitialLegalEntity({ shortName: 'Компания', sealDataUrl: seal });
    confirmMock.mockResolvedValue(true);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={vi.fn()} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      await screen.findByAltText('Печать компании');
      await user.click(screen.getByRole('button', { name: 'Удалить изображение' }));
      const input = screen.getByText('Печать компании').parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, imageFixtureFile('replacement.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const oldLoad = deferred.images[0].onload;
      await user.click(screen.getByRole('button', { name: 'Вернуть текущее изображение' }));
      await act(async () => { oldLoad?.(); });
      expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
      expect((screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement).disabled).toBe(false);
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('waits for independent image fields before enabling save in %s mode', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={vi.fn()}
        initial={createInitialLegalEntity({ shortName: 'Компания' })} suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()} allowEdit allowDelete={false} />);
      for (const [index, label] of ['Логотип компании', 'Подпись руководителя'].entries()) {
        const title = await screen.findByText(label);
        const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
        await user.upload(input, imageFixtureFile(`${index}.png`));
        await waitFor(() => expect(deferred.images).toHaveLength(index + 1));
      }
      const save = screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement;
      await act(async () => { deferred.images[0].onload?.(); });
      expect(save.disabled).toBe(true);
      await act(async () => { deferred.images[1].onload?.(); });
      await waitFor(() => expect(save.disabled).toBe(false));
    } finally { deferred.restore(); }
  });

  it.each([
    ['page', 'close'], ['modal', 'close'], ['page', 'refresh'], ['modal', 'refresh'],
    ['page', 'permission'], ['modal', 'permission'],
  ] as const)('discards pending image work after %s form %s', async (mode, change) => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const initial = createInitialLegalEntity({ shortName: 'Первая компания' });
    const props = { isOpen: true, mode, initial, onClose: vi.fn(), onSubmit: vi.fn(),
      suggest: vi.fn().mockResolvedValue([]), findById: vi.fn(), allowEdit: true, allowDelete: false };
    try {
      const view = render(<LegalEntityModal {...props} />);
      const title = await screen.findByText('Логотип компании');
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, imageFixtureFile('old.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const oldLoad = deferred.images[0].onload;
      const next = change === 'refresh' ? { ...initial, shortName: 'Обновлённая компания' } : initial;
      view.rerender(<LegalEntityModal {...props} initial={next} isOpen={change !== 'close'} allowEdit={change !== 'permission'} />);
      await act(async () => { oldLoad?.(); });
      view.rerender(<LegalEntityModal {...props} initial={next} />);
      await screen.findByText('Логотип компании');
      expect(screen.queryByAltText('Логотип компании')).toBeNull();
      expect(screen.queryByText('Обрабатываем изображения. Дождитесь окончания перед сохранением.')).toBeNull();
      expect(props.onSubmit).not.toHaveBeenCalled();
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('cancels file reading and recovers from the current read failure in %s mode', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const readers: DeferredReader[] = [];
    class DeferredReader {
      result: string | null = null;
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      abort = vi.fn();
      readAsDataURL(file: File) {
        this.result = `data:image/png;base64,${btoa(file.name)}`;
        readers.push(this);
      }
    }
    vi.stubGlobal('FileReader', DeferredReader);
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const submit = vi.fn().mockResolvedValue(createInitialLegalEntity());
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit}
        initial={createInitialLegalEntity({ shortName: 'Компания', sealDataUrl: seal })}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const title = await screen.findByText('Логотип компании');
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      const file = imageFixtureFile('latest.png');
      await user.upload(input, imageFixtureFile('old.png'));
      await waitFor(() => expect(readers).toHaveLength(1));
      const oldLoad = readers[0].onload;
      const oldError = readers[0].onerror;
      await user.upload(input, file);
      await waitFor(() => expect(readers).toHaveLength(2));
      expect(readers[0].abort).toHaveBeenCalledTimes(1);
      await act(async () => { oldLoad?.(); oldError?.(); });
      expect(deferred.images).toHaveLength(0);
      const save = screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement;
      expect(save.disabled).toBe(true);
      await act(async () => { readers[1].onerror?.(); });
      await screen.findByText('Не удалось прочитать файл изображения.');
      expect(save.disabled).toBe(false);
      expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
      await waitFor(() => expect(input.value).toBe(''));
      await user.upload(input, file);
      await waitFor(() => expect(readers).toHaveLength(3));
      const latest = readers[2].result;
      await act(async () => { readers[2].onload?.(); });
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      await act(async () => { deferred.images[0].onload?.(); });
      await waitFor(() => expect(save.disabled).toBe(false));
      expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(latest);
      expect(screen.queryByText('Не удалось прочитать файл изображения.')).toBeNull();
      await user.click(save);
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ logoDataUrl: latest });
    } finally { deferred.restore(); }
  });

  it.each([
    ['page', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'remove'],
    ['page', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'restore'],
    ['modal', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'remove'],
    ['modal', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'restore'],
    ['page', 'Печать компании', 'sealDataUrl', 'sealChanged', 'remove'],
    ['page', 'Печать компании', 'sealDataUrl', 'sealChanged', 'restore'],
    ['modal', 'Печать компании', 'sealDataUrl', 'sealChanged', 'remove'],
    ['modal', 'Печать компании', 'sealDataUrl', 'sealChanged', 'restore'],
    ['page', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'remove'],
    ['page', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'restore'],
    ['modal', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'remove'],
    ['modal', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'restore'],
  ] as const)('keeps deletion feedback inline in %s for %s, preserving %s/%s on %s', async (mode, label, field, flag, action) => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Компания' });
    initial[field] = 'data:image/png;base64,c2F2ZWQ=';
    const submit = vi.fn<(payload: LegalEntityInput) => Promise<LegalEntityDetail>>().mockResolvedValue(initial);
    confirmMock.mockResolvedValue(true);
    render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
      suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
    const title = await screen.findByText(label);
    const imageField = title.parentElement!;
    await user.click(screen.getByRole('button', { name: 'Удалить изображение' }));
    await waitFor(() => expect(imageField.querySelector('[role="status"]')?.textContent).toContain('Изображение помечено на удаление.'));
    expect(toastInfoMock).not.toHaveBeenCalled();
    if (action === 'restore') {
      await user.click(screen.getByRole('button', { name: 'Вернуть текущее изображение' }));
      expect(imageField.querySelector('[role="status"]')).toBeNull();
    }
    await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    const payload = submit.mock.calls[0][0];
    expect(payload[flag]).toBe(action === 'remove');
    if (action === 'remove') expect(payload[field]).toBeNull();
    else expect(Object.hasOwn(payload, field)).toBe(false);
  });

  it.each(['page', 'modal'] as const)('rejects source metadata in the %s form before reading, preserving the draft and other image', async mode => {
    const user = userEvent.setup({ applyAccept: false });
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const initial = createInitialLegalEntity({ shortName: 'Компания', sealDataUrl: seal });
    const submit = vi.fn();
    const read = vi.spyOn(FileReader.prototype, 'readAsDataURL');
    render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
      suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
    const logoField = (await screen.findByText('Логотип компании')).parentElement!;
    const input = logoField.querySelector('input[type="file"]') as HTMLInputElement;
    const name = screen.getByDisplayValue('Компания');
    await user.clear(name);
    await user.type(name, 'Мой черновик');
    try {
      for (const [file, message] of [
        [new File([], 'empty.png', { type: 'image/png' }), 'Логотип компании: выбран пустой файл. Выберите изображение.'],
        [new File(['<svg/>'], 'renamed.png', { type: 'image/svg+xml' }), 'Логотип компании: PNG или JPG, до 2 МБ.'],
        [new File([new Uint8Array(2 * 1024 * 1024 + 1)], 'big.png', { type: 'image/png' }), 'Логотип компании: исходный файл больше 2 МБ. Выберите файл меньшего размера.'],
      ] as const) {
        await user.upload(input, file);
        await screen.findByText(message);
        await waitFor(() => expect(input.value).toBe(''));
        expect((name as HTMLInputElement).value).toBe('Мой черновик');
        expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
        expect(screen.getByRole<HTMLButtonElement>('button', { name: /^Сохранить$/ }).disabled).toBe(false);
      }
      const signatureField = screen.getByText('Подпись руководителя').parentElement!;
      const signature = signatureField.querySelector('input[type="file"]') as HTMLInputElement;
      expect(signature.accept).toBe('image/png');
      await user.upload(signature, new File(['jpeg'], 'renamed.png', { type: 'image/jpeg' }));
      await screen.findByText('Подпись руководителя: PNG с прозрачным фоном.');
      expect(read).not.toHaveBeenCalled();
      expect(submit).not.toHaveBeenCalled();
      await waitFor(() => expect(signature.value).toBe(''));
    } finally { read.mockRestore(); }
  });

  it.each(['page', 'modal'] as const)('rejects actual source headers in the %s form before full read, preserving the draft', async mode => {
    const user = userEvent.setup({ applyAccept: false });
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const initial = createInitialLegalEntity({ shortName: 'Мой черновик', sealDataUrl: seal });
    const submit = vi.fn();
    const read = vi.spyOn(FileReader.prototype, 'readAsDataURL');
    const image = vi.fn(); vi.stubGlobal('Image', image);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const field = (await screen.findByText('Логотип компании')).parentElement!;
      const input = field.querySelector('input[type="file"]') as HTMLInputElement;
      const large = imageFixtureBytes();
      new DataView(large.buffer).setUint32(16, 65535);
      new DataView(large.buffer).setUint32(20, 65535);
      for (const [file, text] of [
        [new File([large.buffer], 'large.png', { type: 'image/png' }), 'Логотип компании: изображение превышает 4096 пикселей по стороне или 4 мегапикселя. Уменьшите его.'],
        [new File([imageFixtureBytes('jpeg').buffer], 'renamed.png', { type: 'image/png' }), 'Логотип компании: содержимое файла не соответствует указанному формату.'],
      ] as const) {
        await user.upload(input, file);
        await screen.findByText(text);
        await waitFor(() => expect(input.value).toBe(''));
        expect(screen.getByDisplayValue('Мой черновик')).toBeTruthy();
        expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
        expect(screen.getByRole<HTMLButtonElement>('button', { name: /^Сохранить$/ }).disabled).toBe(false);
      }
      expect(read).not.toHaveBeenCalled(); expect(image).not.toHaveBeenCalled(); expect(submit).not.toHaveBeenCalled();
    } finally { vi.unstubAllGlobals(); read.mockRestore(); }
  });

  it.each(['page', 'modal'] as const)('discards a late header result in the %s form after a newer file succeeds', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const initial = createInitialLegalEntity({ shortName: 'Мой черновик' });
    const submit = vi.fn<(payload: LegalEntityInput) => Promise<LegalEntityDetail>>().mockResolvedValue(initial);
    const old = imageFixtureFile('old-header.png');
    const oldBytes = await old.arrayBuffer();
    let release!: (value: ArrayBuffer) => void;
    vi.spyOn(old, 'slice').mockReturnValue({ arrayBuffer: () => new Promise<ArrayBuffer>(resolve => { release = resolve; }) } as Blob);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const field = (await screen.findByText('Логотип компании')).parentElement!;
      const input = field.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, old);
      expect(screen.getByRole<HTMLButtonElement>('button', { name: /^Сохранить$/ }).disabled).toBe(true);
      expect(deferred.images).toHaveLength(0);
      await user.upload(input, imageFixtureFile('latest-header.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const latest = deferred.images[0].source;
      await act(async () => { deferred.images[0].onload?.(); });
      await waitFor(() => expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(latest));
      await act(async () => { release(oldBytes); });
      expect(deferred.images).toHaveLength(1);
      expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(latest);
      await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ logoDataUrl: latest, shortName: 'Мой черновик' });
    } finally { deferred.restore(); }
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
