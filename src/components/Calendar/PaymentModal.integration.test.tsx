import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PaymentModal } from './PaymentModal';

const {
  confirmMock,
  createInvoiceMock,
  getActResponsiblesMock,
  getCaseMock,
  getCasesMock,
  getContractsMock,
  getInvoicesMock,
  getClientsMock,
  getClientsV2Mock,
  getLegalEntitiesMock,
  getLegalEntitiesPagedMock,
  getClientBalanceMock,
  getLegalEntityBalanceMock,
  applyClientBalanceMock,
  getPaymentsV2Mock,
  getNextCashOrderNumberMock,
  invoiceModalPropsSpy,
  toastErrorMock,
  toastSuccessMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  createInvoiceMock: vi.fn(),
  getActResponsiblesMock: vi.fn(),
  getCaseMock: vi.fn(),
  getCasesMock: vi.fn(),
  getContractsMock: vi.fn(),
  getInvoicesMock: vi.fn(),
  getClientsMock: vi.fn(),
  getClientsV2Mock: vi.fn(),
  getLegalEntitiesMock: vi.fn(),
  getLegalEntitiesPagedMock: vi.fn(),
  getClientBalanceMock: vi.fn(),
  getLegalEntityBalanceMock: vi.fn(),
  applyClientBalanceMock: vi.fn(),
  getPaymentsV2Mock: vi.fn(),
  getNextCashOrderNumberMock: vi.fn(),
  invoiceModalPropsSpy: vi.fn(),
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
}));

vi.mock('../../hooks/useClients', () => ({
  useClients: () => ({
    clients: [
      { id: 69, name: 'Test Client' },
      { id: 77, name: 'Supplier Client' },
      { id: 88, name: 'Expense Client' },
    ],
    loading: false,
  }),
}));

vi.mock('../../hooks/useLegalEntities', () => ({
  useLegalEntities: () => ({
    legalEntities: [
      {
        id: 301,
        shortName: 'ООО Юрклиент',
        fullName: 'Общество с ограниченной ответственностью Юрклиент',
        inn: '5400000001',
        kpp: '540001001',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
      {
        id: 302,
        shortName: 'ООО Поставщик ЮЛ',
        inn: '5400000002',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
    ],
    loading: false,
    error: null,
  }),
}));

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    dealTypes: [
      { id: 7, name: 'Юридические услуги', description: '', colorHex: '#000000', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
    ],
    incomeTypes: [
      { id: 8, name: 'Доход от услуг', description: '', colorHex: '#000000', isActive: true, createdAt: '2026-04-01T00:00:00Z', paymentType: 'Income' },
    ],
    paymentSources: [
      { id: 5, name: 'Банковский перевод', description: '', colorHex: '#000000', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
      { id: 6, name: 'Наличные', description: '', colorHex: '#000000', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
    ],
    services: [
      { id: 9, name: 'Тип юр услуги', description: '', unit: 'шт', defaultPrice: 0, colorHex: '#000000', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
    ],
  }),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useFormRequirements', () => ({
  useFormRequirements: () => ({ isRequired: () => false }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ error: toastErrorMock, success: toastSuccessMock, info: vi.fn() }),
}));

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Europe/Samara',
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getInvoices: getInvoicesMock,
    getActResponsibles: getActResponsiblesMock,
    getCases: getCasesMock,
    getCase: getCaseMock,
    getContracts: getContractsMock,
    createInvoice: createInvoiceMock,
    getClients: getClientsMock,
    getClientsV2: getClientsV2Mock,
    getLegalEntities: getLegalEntitiesMock,
    getLegalEntitiesPaged: getLegalEntitiesPagedMock,
    getClientBalance: getClientBalanceMock,
    getLegalEntityBalance: getLegalEntityBalanceMock,
    applyClientBalance: applyClientBalanceMock,
    getPaymentsV2: getPaymentsV2Mock,
    getNextCashOrderNumber: getNextCashOrderNumberMock,
  },
}));

vi.mock('../Accounts/InvoiceModal', () => ({
  InvoiceModal: (props: {
    open: boolean;
    resumeDraft?: {
      form?: {
        number?: string;
        amount?: string;
        date?: string;
        clientId?: string;
        legalEntityId?: string;
        contractId?: string;
      };
      selectedClientCaseIds?: number[];
    } | null;
    onSubmit: (payload: {
      number: string;
      date: string;
      amount: number;
      type: 'Income' | 'Expense';
      status: 'Pending' | 'Overdue' | 'Completed' | 'Processing' | 'Cancelled';
      clientId: number;
    }) => Promise<void>;
  }) => {
    invoiceModalPropsSpy(props);
    if (!props.open) {
      return null;
    }

    return (
      <div data-testid="invoice-create-modal">
        <div>{`draft-number:${props.resumeDraft?.form?.number ?? ''}`}</div>
        <div>{`draft-amount:${props.resumeDraft?.form?.amount ?? ''}`}</div>
        <div>{`draft-date:${props.resumeDraft?.form?.date ?? ''}`}</div>
        <div>{`draft-client:${props.resumeDraft?.form?.clientId ?? ''}`}</div>
        <div>{`draft-legal:${props.resumeDraft?.form?.legalEntityId ?? ''}`}</div>
        <div>{`draft-contract:${props.resumeDraft?.form?.contractId ?? ''}`}</div>
        <div>{`draft-case-count:${props.resumeDraft?.selectedClientCaseIds?.length ?? 0}`}</div>
        <div>{`draft-case-ids:${props.resumeDraft?.selectedClientCaseIds?.join(',') ?? ''}`}</div>
        <button
          type="button"
          onClick={() =>
            void props.onSubmit({
              number: '31',
              date: '2026-04-08',
              amount: 5_000,
              type: 'Income',
              status: 'Pending',
              clientId: 69,
            })
          }>
          submit-created-invoice
        </button>
      </div>
    );
  },
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

function getPickerOptionAction(label: HTMLElement): HTMLButtonElement {
  const option = label.closest('[data-counterparty-option]');
  if (option instanceof HTMLElement) {
    const actions = within(option).queryAllByRole('button');
    if (actions.length > 0) {
      return actions[0] as HTMLButtonElement;
    }
  }

  let current: HTMLElement | null = label;

  while (current && current !== document.body) {
    const actions = within(current).queryAllByRole('button');
    if (actions.length === 1) {
      return actions[0] as HTMLButtonElement;
    }
    current = current.parentElement;
  }

  throw new Error(`Не найдена кнопка выбора для ${label.textContent ?? 'контрагента'}`);
}

async function selectPickerOption(name: string): Promise<void> {
  const option = await waitFor(() => {
    const found = Array.from(document.querySelectorAll('[data-counterparty-option]'))
      .find((element) => element.textContent?.includes(name));
    if (!(found instanceof HTMLElement)) {
      throw new Error(`Не найден вариант выбора ${name}`);
    }
    return found;
  });

  fireEvent.click(getPickerOptionAction(option));
}

function getLastPrintHtml(): string {
  const openMock = vi.mocked(window.open);
  const lastCall = openMock.mock.results[openMock.mock.results.length - 1];
  const printWindow = lastCall?.value as Window | null | undefined;
  expect(printWindow).toBeTruthy();
  const writeMock = vi.mocked(printWindow!.document.write);
  expect(writeMock).toHaveBeenCalled();
  return String(writeMock.mock.calls[0]?.[0] ?? '');
}

describe('PaymentModal integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    createInvoiceMock.mockReset();
    getActResponsiblesMock.mockReset();
    getCaseMock.mockReset();
    getCasesMock.mockReset();
    getContractsMock.mockReset();
    getInvoicesMock.mockReset();
    getClientsMock.mockReset();
    getClientsV2Mock.mockReset();
    getLegalEntitiesMock.mockReset();
    getLegalEntitiesPagedMock.mockReset();
    getClientBalanceMock.mockReset();
    getLegalEntityBalanceMock.mockReset();
    applyClientBalanceMock.mockReset();
    getPaymentsV2Mock.mockReset();
    getNextCashOrderNumberMock.mockReset();
    invoiceModalPropsSpy.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();

    confirmMock.mockResolvedValue(true);
    createInvoiceMock.mockResolvedValue({
      id: 901,
      invoiceEntityId: 31,
      number: '31',
      date: '2026-04-08',
      dueDate: '2026-04-08',
      amount: 5_000,
      type: 'Income',
      status: 'Pending',
      clientId: 69,
      createdAt: '2026-04-08T00:00:00Z',
    });
    getActResponsiblesMock.mockResolvedValue([]);
    getCaseMock.mockResolvedValue(null);
    getCasesMock.mockResolvedValue([]);
    getContractsMock.mockResolvedValue({ items: [] });
    getInvoicesMock.mockResolvedValue({ items: [] });
    const clientLookups = [
      { id: 69, name: 'Test Client', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
      { id: 77, name: 'Supplier Client', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
      { id: 88, name: 'Expense Client', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
    ];
    getClientsMock.mockResolvedValue(clientLookups);
    getClientsV2Mock.mockResolvedValue({
      items: clientLookups,
      total: 3,
      page: 1,
      pageSize: 25,
    });
    const legalEntityLookups = [
      {
        id: 301,
        shortName: 'ООО Юрклиент',
        fullName: 'Общество с ограниченной ответственностью Юрклиент',
        inn: '5400000001',
        kpp: '540001001',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
      {
        id: 302,
        shortName: 'ООО Поставщик ЮЛ',
        inn: '5400000002',
        clients: [],
        clientsCount: 0,
        createdAt: '2026-04-01T00:00:00Z',
      },
    ];
    getLegalEntitiesMock.mockResolvedValue(legalEntityLookups);
    getLegalEntitiesPagedMock.mockResolvedValue({
      items: legalEntityLookups,
      total: 2,
      page: 1,
      pageSize: 25,
    });
    getClientBalanceMock.mockResolvedValue({ clientId: 69, balance: 0 });
    getLegalEntityBalanceMock.mockResolvedValue({ clientId: null, legalEntityId: 301, balance: 0 });
    applyClientBalanceMock.mockResolvedValue({ appliedAmount: 0, balance: 0 });
    getPaymentsV2Mock.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 100, summary: {} });
    getNextCashOrderNumberMock.mockResolvedValue({
      number: 'ПКО-2026-0007',
      type: 'Incoming',
      date: '2026-04-20',
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('uses the profile timezone day for new payment default dates', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-04T20:30:00.000Z'));

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
      />,
    );

    expect(document.querySelector<HTMLInputElement>('input[name="date"]')?.value).toBe('2026-06-05');
    expect(document.querySelector<HTMLInputElement>('input[name="plannedDate"]')?.value).toBe('2026-06-05');
    expect(screen.getByRole('button', { name: 'Закрыть' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Выбрать: Ожидаемая дата платежа' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Выбрать: Плановая дата по графику' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Выбрать: Дата фактического платежа' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Выбрать: Дата счёта' })).toBeTruthy();
  });

  it('keeps the create action available when confirmation is cancelled', async () => {
    confirmMock.mockResolvedValueOnce(false);
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
      />,
    );

    const submit = screen.getByRole('button', { name: 'addPayment' });
    fireEvent.click(submit);

    await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('button', { name: 'addPayment' }).hasAttribute('disabled')).toBe(false);
  });

  it('syncs the planned date when the payment date changes', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 771,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 12_000,
          paidAmount: 0,
          outstandingAmount: 12_000,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Payment date sync',
          isPaid: false,
          notes: '',
          type: 'Income',
          createdAt: '2026-04-01T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
      />,
    );

    fireEvent.change(document.querySelector<HTMLInputElement>('input[name="date"]')!, {
      target: { value: '2026-05-20' },
    });

    expect(document.querySelector<HTMLInputElement>('input[name="plannedDate"]')?.value).toBe('2026-05-20');
    fireEvent.click(document.querySelector<HTMLButtonElement>('button[type="submit"]')!);

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 771,
        date: '2026-05-20',
        plannedDate: '2026-05-20',
      }),
    );
  });

  it('submits edited description for an invoice-linked payment document', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 2082,
          date: '2026-07-02',
          plannedDate: '2026-07-02',
          amount: 10_000,
          paidAmount: 10_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: '',
          isPaid: true,
          paidDate: '2026-07-02',
          lastPaymentDate: '2026-07-03',
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          invoiceId: 94,
          account: '94',
          accountDate: '2026-07-02',
          documentKind: 'Invoice',
          createdAt: '2026-07-02T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
      />,
    );

    fireEvent.change(document.querySelector<HTMLTextAreaElement>('textarea[name="description"]')!, {
      target: { value: 'Описание платежа по счету №94' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 2082,
        description: 'Описание платежа по счету №94',
        invoiceId: 94,
      }),
    );
  });

  it('warns and marks a paid income payment without documents as an advance', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 780,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 7_100,
          paidAmount: 7_100,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Платеж без закрывающих документов',
          isPaid: true,
          paidDate: '2026-04-10',
          lastPaymentDate: '2026-04-10',
          notes: '',
          clientId: 69,
          type: 'Income',
          documentKind: 'Payment',
          createdAt: '2026-04-10T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
          advanceReviewStatus: 'None',
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
    expect(confirmMock).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Нераспределенный аванс',
        description: expect.stringContaining('счету, договору или акту'),
        confirmLabel: 'Принять как аванс',
      }),
    );
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 780,
        advanceReviewStatus: 'AcceptedAsAdvance',
        advanceReviewReason: 'Пользователь подтвердил авансовый платеж при сохранении',
      }),
    );
  });

  it('returns to document linking when the advance warning is cancelled', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    confirmMock.mockResolvedValueOnce(false);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 782,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 7_100,
          paidAmount: 7_100,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Платеж без закрывающих документов',
          isPaid: true,
          paidDate: '2026-04-10',
          lastPaymentDate: '2026-04-10',
          notes: '',
          clientId: 69,
          type: 'Income',
          documentKind: 'Payment',
          createdAt: '2026-04-10T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
          advanceReviewStatus: 'None',
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
    expect(confirmMock).toHaveBeenCalledWith(
      expect.objectContaining({
        cancelLabel: 'Вернуться к привязке',
      }),
    );
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('warns for a partially paid income payment without documents', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 783,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 10_000,
          paidAmount: 4_000,
          outstandingAmount: 6_000,
          hasPartialPayment: true,
          status: 'Processing',
          description: 'Частично оплаченный платеж без документов',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: '2026-04-10',
          notes: '',
          clientId: 69,
          type: 'Income',
          documentKind: 'Payment',
          createdAt: '2026-04-10T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
          advanceReviewStatus: 'None',
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(confirmMock).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Нераспределенный аванс',
      }),
    ));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 783,
        amount: 10_000,
        paidAmount: 4_000,
        status: 'Processing',
        advanceReviewStatus: 'AcceptedAsAdvance',
      }),
    );
  });

  it('does not show the advance warning for a zero paid income payment', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 781,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 0,
          paidAmount: 0,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Нулевой технический платеж',
          isPaid: true,
          paidDate: '2026-04-10',
          lastPaymentDate: '2026-04-10',
          notes: '',
          clientId: 69,
          type: 'Income',
          documentKind: 'Payment',
          createdAt: '2026-04-10T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
          advanceReviewStatus: 'None',
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(confirmMock).not.toHaveBeenCalled();
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 781,
        advanceReviewStatus: 'None',
      }),
    );
  });

  it('clears received state when a zero amount payment is marked as not paid', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 782,
          date: '2026-06-19',
          plannedDate: '2026-06-25',
          amount: 0,
          paidAmount: 0,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Zero installment payment',
          isPaid: true,
          paidDate: '2026-06-19',
          lastPaymentDate: '2026-06-19',
          notes: '',
          clientId: 69,
          type: 'Income',
          documentKind: 'Payment',
          createdAt: '2026-06-19T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
          advanceReviewStatus: 'None',
        }}
      />,
    );

    const isPaidInput = document.querySelector<HTMLInputElement>('input[name="isPaid"]');
    expect(isPaidInput).toBeTruthy();
    expect(isPaidInput!.checked).toBe(true);

    fireEvent.click(isPaidInput!);
    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 782,
        status: 'Pending',
        isPaid: false,
        paidAmount: 0,
        paidDate: undefined,
        lastPaymentDate: undefined,
      }),
    );
  });

  it('uses the profile timezone day when marking a payment as paid', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-04T20:30:00.000Z'));

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
      />,
    );

    const amountInput = document.querySelector<HTMLInputElement>('input[name="amount"]');
    const isPaidInput = document.querySelector<HTMLInputElement>('input[name="isPaid"]');
    expect(amountInput).toBeTruthy();
    expect(isPaidInput).toBeTruthy();

    fireEvent.change(amountInput!, { target: { value: '12000' } });
    fireEvent.click(isPaidInput!);

    expect(document.querySelector<HTMLInputElement>('input[name="lastPaymentDate"]')?.value).toBe('2026-06-05');
    expect(document.querySelector<HTMLInputElement>('input[name="paidDate"]')?.value).toBe('2026-06-05');
  });

  it('shows the locked linked invoice context for a payment created from an invoice', async () => {
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        linkedInvoice={{
          invoiceEntityId: 24,
          invoiceRecordId: 528,
          number: '24',
          date: '2026-04-02',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    await waitFor(() => expect(screen.getByDisplayValue('24')).toBeTruthy());
    expect(screen.getByDisplayValue('2026-04-02')).toBeTruthy();
    expect(screen.getAllByDisplayValue('Test Client')).not.toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Выбрать контрагента' })).toHaveProperty('disabled', true);
  });

  it('allows creating an expense payment from a linked income invoice', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getContractsMock.mockResolvedValue({
      items: [
        {
          id: 321,
          number: '1-14/07-2025',
          title: 'Договор для возврата клиенту',
          date: '2025-07-14',
          billingMode: 'Fixed',
          isOpenEnded: true,
          createdAt: '2025-07-14T00:00:00Z',
          invoiceAmountTotal: 0,
          actAmountTotal: 0,
          paymentAmountTotal: 0,
          clients: [{ id: 69, name: 'Test Client' }],
        },
      ],
    });

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        linkedInvoice={{
          invoiceEntityId: 24,
          invoiceRecordId: 528,
          number: '24',
          date: '2026-04-02',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const typeSelect = await waitFor(() => {
      const select = document.body.querySelector('select[name="type"]') as HTMLSelectElement | null;
      expect(select).not.toBeNull();
      return select!;
    });
    expect(typeSelect.disabled).toBe(false);

    fireEvent.change(typeSelect, { target: { value: 'Expense' } });
    expect(typeSelect.value).toBe('Expense');

    const amountInput = document.body.querySelector('input[name="amount"]') as HTMLInputElement | null;
    expect(amountInput).not.toBeNull();
    fireEvent.change(amountInput!, { target: { value: '1500' } });

    const contractSelect = await screen.findByLabelText('Договор');
    await waitFor(() => expect(within(contractSelect).getByText(/1-14\/07-2025/, { selector: 'option' })).toBeTruthy());
    expect(contractSelect).toHaveProperty('disabled', false);
    fireEvent.change(contractSelect, { target: { value: '321' } });

    fireEvent.click(screen.getByRole('button', { name: 'addPayment' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'Expense',
        invoiceId: 24,
        clientId: 69,
        expenseClientId: 69,
        clientCaseId: 501,
        contractId: 321,
      }),
    );
  });

  it('prefills the contract from a linked invoice when adding a payment', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        linkedInvoice={{
          invoiceEntityId: 24,
          invoiceRecordId: 528,
          number: '24',
          date: '2026-04-02',
          clientId: 69,
          clientCaseId: 501,
          contractId: 321,
          type: 'Income',
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const contractSelect = await screen.findByLabelText('Договор') as HTMLSelectElement;
    await waitFor(() => expect(contractSelect.value).toBe('321'));

    const amountInput = document.body.querySelector('input[name="amount"]') as HTMLInputElement | null;
    expect(amountInput).not.toBeNull();
    fireEvent.change(amountInput!, { target: { value: '1500' } });
    fireEvent.click(screen.getByRole('button', { name: 'addPayment' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'Income',
        invoiceId: 24,
        contractId: 321,
      }),
    );
  });

  it('keeps payment fields while replacing stale invoice fields with the linked invoice context during edit', async () => {
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        payment={{
          id: 529,
          date: '2026-04-03',
          plannedDate: '2026-04-03',
          amount: 5_000,
          paidAmount: 5_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Existing payment description',
          isPaid: true,
          paidDate: '2026-04-04',
          lastPaymentDate: '2026-04-04',
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          account: 'OLD-24',
          accountDate: '2026-01-01',
          documentKind: 'Payment',
          createdAt: '2026-04-03T00:00:00Z',
          updatedAt: null,
          systemNotes: '',
          rescheduleCount: 0,
        }}
        linkedInvoice={{
          invoiceEntityId: 24,
          invoiceRecordId: 528,
          number: '24',
          date: '2026-04-02',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    await waitFor(() => expect(screen.getByDisplayValue('24')).toBeTruthy());
    expect(screen.getByDisplayValue('2026-04-02')).toBeTruthy();
    expect(screen.queryByDisplayValue('OLD-24')).toBeNull();
    expect(screen.queryByDisplayValue('2026-01-01')).toBeNull();
    expect(screen.getAllByDisplayValue('5 000,00')).toHaveLength(2);
    expect(screen.getByDisplayValue('Existing payment description')).toBeTruthy();
  });

  it('shows delete action for legacy invoice-marked payment without invoice link', async () => {
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        onDelete={vi.fn().mockResolvedValue(undefined)}
        payment={{
          id: 339,
          date: '2026-01-30',
          plannedDate: '2026-01-30',
          amount: 15_000,
          paidAmount: 0,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Overdue',
          description: 'БЕЗНЯКОВ - ОПЛАТА ФИН.УПРАВЛЯЮЩЕМУ',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Expense',
          documentKind: 'Invoice',
          invoiceId: null,
          createdAt: '2026-01-30T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-01-01T00:00:00Z',
          },
        ]}
      />,
    );

    expect(await screen.findByRole('button', { name: 'delete' })).toBeTruthy();
  });

  it('submits inferred expense client for legacy case-linked expense payment', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 340,
          date: '2026-01-23',
          plannedDate: '2025-12-01',
          amount: 15_000,
          paidAmount: 0,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Overdue',
          description: 'БЕЗНЯКОВ - ОПЛАТА ФИНАНСОВОМУ УПРАВЛЯЮЩЕМУ',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Expense',
          documentKind: 'Invoice',
          invoiceId: null,
          createdAt: '2026-01-23T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-01-01T00:00:00Z',
          },
        ]}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
      id: 340,
      type: 'Expense',
      expenseClientId: 69,
      clientCaseId: 501,
    }));
  });

  it('creates an invoice from the payment, applies the canonical link, and saves the payment with invoiceId only', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getContractsMock.mockResolvedValue({ items: [{ id: 321 }] });

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 529,
          date: '2026-04-03',
          plannedDate: '2026-04-03',
          amount: 5_000,
          paidAmount: 5_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Existing payment description',
          isPaid: true,
          paidDate: '2026-04-04',
          lastPaymentDate: '2026-04-04',
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          account: 'LEGACY-31',
          accountDate: '2026-04-01',
          documentKind: 'Payment',
          createdAt: '2026-04-03T00:00:00Z',
          updatedAt: null,
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /создать сч[её]т/i }));

    expect(await screen.findByTestId('invoice-create-modal')).toBeTruthy();
    expect(screen.getByText('draft-number:LEGACY-31')).toBeTruthy();
    expect(screen.getByText('draft-amount:5 000,00')).toBeTruthy();
    expect(screen.getByText('draft-date:2026-04-01')).toBeTruthy();
    expect(screen.getByText('draft-client:69')).toBeTruthy();
    expect(screen.getByText('draft-contract:321')).toBeTruthy();
    expect(screen.getByText('draft-case-count:1')).toBeTruthy();
    expect(screen.getByText('draft-case-ids:501')).toBeTruthy();

    fireEvent.click(screen.getByText('submit-created-invoice'));

    await waitFor(() => expect(createInvoiceMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByDisplayValue('31')).toBeTruthy());
    expect(screen.getByDisplayValue('2026-04-08')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 529,
        invoiceId: 31,
        clientId: 69,
        clientCaseId: 501,
        type: 'Income',
      }),
    );

    const submitPayload = onSubmit.mock.calls[0][0] as Record<string, unknown>;
    expect(submitPayload.account).toBeUndefined();
    expect(submitPayload.accountDate).toBeUndefined();
  });

  it('hides linked invoice actions when the user cannot view accounts', () => {
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        canViewLinkedInvoices={false}
        canCreateLinkedInvoices={false}
        payment={{
          id: 530,
          date: '2026-04-03',
          plannedDate: '2026-04-03',
          amount: 5_000,
          paidAmount: 0,
          outstandingAmount: 5_000,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Payment without account permissions',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          type: 'Income',
          account: '',
          accountDate: '',
          documentKind: 'Payment',
          createdAt: '2026-04-03T00:00:00Z',
          updatedAt: null,
          systemNotes: '',
          rescheduleCount: 0,
        }}
      />,
    );

    expect(screen.queryByRole('button', { name: /Выбрать сч[её]т/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /Создать сч[её]т/i })).toBeNull();
    expect(getInvoicesMock).not.toHaveBeenCalled();
    expect(createInvoiceMock).not.toHaveBeenCalled();
  });

  it('keeps invoice selection but hides invoice creation without the create account permission', () => {
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        canViewLinkedInvoices
        canCreateLinkedInvoices={false}
        payment={{
          id: 531,
          date: '2026-04-03',
          plannedDate: '2026-04-03',
          amount: 5_000,
          paidAmount: 0,
          outstandingAmount: 5_000,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Payment with view-only account permissions',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          type: 'Income',
          account: '',
          accountDate: '',
          documentKind: 'Payment',
          createdAt: '2026-04-03T00:00:00Z',
          updatedAt: null,
          systemNotes: '',
          rescheduleCount: 0,
        }}
      />,
    );

    expect(screen.getByRole('button', { name: /Выбрать сч[её]т/i })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Создать сч[её]т/i })).toBeNull();
    expect(createInvoiceMock).not.toHaveBeenCalled();
  });

  it('selects a legal entity for an income payment and submits legalEntityId instead of clientId', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 777,
          date: '2026-04-12',
          plannedDate: '2026-04-12',
          amount: 12_000,
          paidAmount: 0,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Платеж от юрлица',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          type: 'Income',
          account: '',
          accountDate: '',
          documentKind: 'Payment',
          createdAt: '2026-04-12T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Выбрать контрагента' }));
    fireEvent.click(screen.getByRole('button', { name: 'Юридические лица' }));
    await selectPickerOption('ООО Юрклиент');
    await waitFor(() => expect(screen.getAllByDisplayValue('ООО Юрклиент').length).toBeGreaterThan(0));
    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const payload = onSubmit.mock.calls[0][0] as Record<string, unknown>;
    expect(payload.legalEntityId).toBe(301);
    expect(payload.clientId).toBeUndefined();
  });

  it('shows client contracts in the payment card and submits the selected contract', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getContractsMock.mockResolvedValue({
      items: [
        {
          id: 321,
          number: '1-14/07-2025',
          title: 'спор между Чуркиной О.В. и Карповым А.Б.',
          date: '2025-07-14',
          billingMode: 'Fixed',
          isOpenEnded: true,
          createdAt: '2025-07-14T00:00:00Z',
          invoiceAmountTotal: 0,
          actAmountTotal: 0,
          paymentAmountTotal: 0,
          clients: [{ id: 69, name: 'Test Client' }],
        },
      ],
    });

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 778,
          date: '2026-04-12',
          plannedDate: '2026-04-12',
          amount: 25_000,
          paidAmount: 25_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Оплата по счету 154',
          isPaid: true,
          paidDate: '2026-04-12',
          lastPaymentDate: '2026-04-12',
          notes: '',
          clientId: 69,
          type: 'Income',
          account: '',
          accountDate: '',
          documentKind: 'Payment',
          createdAt: '2026-04-12T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
      />,
    );

    const contractSelect = await screen.findByLabelText('Договор');
    await waitFor(() => expect(within(contractSelect).getByText(/1-14\/07-2025/, { selector: 'option' })).toBeTruthy());
    fireEvent.change(contractSelect, { target: { value: '321' } });
    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(confirmMock).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Нераспределенный аванс',
        description: expect.stringContaining('Платеж привязан к договору'),
      }),
    ));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(getContractsMock).toHaveBeenCalledWith(expect.objectContaining({ clientId: 69 }));
    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
      id: 778,
      contractId: 321,
      advanceReviewStatus: 'AcceptedAsAdvance',
    }));
  });

  it('submits expense attribution and payment document details for expense payments', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 991,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 7_500,
          paidAmount: 0,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Оплата подрядчику',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 77,
          expenseClientId: 88,
          clientCaseId: 501,
          paymentSourceId: 5,
          type: 'Expense',
          account: '',
          accountDate: '',
          documentKind: 'Payment',
          createdAt: '2026-04-10T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Expense Case 501',
            status: 'Open',
            clientId: 88,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const orderInput = document.body.querySelector(
      'input[name="paymentOrderNumber"]',
    ) as HTMLInputElement | null;
    const dateInput = document.body.querySelector(
      'input[name="paymentDocumentDate"]',
    ) as HTMLInputElement | null;
    expect(orderInput).not.toBeNull();
    expect(dateInput).not.toBeNull();

    fireEvent.change(orderInput!, { target: { value: 'ПП-778' } });
    fireEvent.change(dateInput!, { target: { value: '2026-04-09' } });
    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 991,
        clientId: 77,
        expenseClientId: 88,
        clientCaseId: 501,
        paymentOrderNumber: 'ПП-778',
        paymentDocumentDate: '2026-04-09',
        type: 'Expense',
      }),
    );
  });

  it('shows editable cash order fields for incoming cash payments', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 992,
          date: '2026-04-20',
          plannedDate: '2026-04-20',
          amount: 25_000,
          paidAmount: 25_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Наличный платеж по счету',
          isPaid: true,
          paidDate: '2026-04-20',
          lastPaymentDate: '2026-04-20',
          notes: '',
          clientId: 77,
          clientCaseId: 501,
          paymentSourceId: 6,
          type: 'Income',
          account: '33',
          accountDate: '2026-04-03',
          documentKind: 'Payment',
          createdAt: '2026-04-20T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Income Case 501',
            status: 'Open',
            clientId: 77,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const orderInput = document.body.querySelector(
      'input[name="paymentOrderNumber"]',
    ) as HTMLInputElement | null;
    const dateInput = document.body.querySelector(
      'input[name="paymentDocumentDate"]',
    ) as HTMLInputElement | null;
    const dealTypeSelect = document.body.querySelector('select[name="dealTypeId"]') as HTMLSelectElement | null;
    const incomeTypeSelect = document.body.querySelector('select[name="incomeTypeId"]') as HTMLSelectElement | null;
    const serviceSelect = document.body.querySelector('select[name="serviceId"]') as HTMLSelectElement | null;
    const sourceSelect = document.body.querySelector('select[name="paymentSourceId"]') as HTMLSelectElement | null;
    expect(orderInput).not.toBeNull();
    expect(dateInput).not.toBeNull();
    expect(dealTypeSelect).not.toBeNull();
    expect(incomeTypeSelect).not.toBeNull();
    expect(serviceSelect).not.toBeNull();
    expect(sourceSelect).not.toBeNull();

    await waitFor(() => {
      expect(getNextCashOrderNumberMock).toHaveBeenCalledWith('Incoming', '2026-04-20');
      expect(orderInput!.value).toBe('ПКО-2026-0007');
      expect(dateInput!.value).toBe('2026-04-20');
    });

    fireEvent.change(dealTypeSelect!, { target: { value: '7' } });
    fireEvent.change(incomeTypeSelect!, { target: { value: '8' } });
    fireEvent.change(serviceSelect!, { target: { value: '9' } });
    fireEvent.change(sourceSelect!, { target: { value: '6' } });
    fireEvent.change(orderInput!, { target: { value: 'ПКО-2026-0099' } });
    fireEvent.change(dateInput!, { target: { value: '2026-04-20' } });
    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 992,
        clientId: 77,
        clientCaseId: 501,
        dealTypeId: 7,
        incomeTypeId: 8,
        serviceId: 9,
        paymentSourceId: 6,
        paymentOrderNumber: 'ПКО-2026-0099',
        paymentDocumentDate: '2026-04-20',
        type: 'Income',
      }),
    );
  });

  it('prints an incoming cash payment as a cash receipt order', async () => {
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        payment={{
          id: 993,
          date: '2026-05-15',
          plannedDate: '2026-05-15',
          amount: 7_000,
          paidAmount: 7_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Рассрочка по счету №52 · платеж 1/20',
          isPaid: true,
          paidDate: '2026-05-15',
          lastPaymentDate: '2026-05-15',
          notes: '',
          clientId: 77,
          clientCaseId: 501,
          paymentSourceId: 6,
          paymentOrderNumber: 'ПКО-2026-0002',
          paymentDocumentDate: '2026-05-15',
          type: 'Income',
          account: '52',
          accountDate: '2026-05-05',
          documentKind: 'Payment',
          createdAt: '2026-05-15T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Income Case 501',
            status: 'Open',
            clientId: 77,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Печать' }));

    const html = getLastPrintHtml();
    expect(html).toContain('Приходный кассовый ордер');
    expect(html).toContain('ПКО-2026-0002');
    expect(html).toContain('Рассрочка по счету №52');
  });

  it('prints a bank payment as a payment order', async () => {
    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        payment={{
          id: 994,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 7_500,
          paidAmount: 7_500,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Оплата подрядчику',
          isPaid: true,
          paidDate: '2026-04-10',
          lastPaymentDate: '2026-04-10',
          notes: '',
          clientId: 77,
          expenseClientId: 88,
          clientCaseId: 501,
          paymentSourceId: 5,
          paymentOrderNumber: 'ПП-778',
          paymentDocumentDate: '2026-04-09',
          type: 'Expense',
          account: '',
          accountDate: '',
          documentKind: 'Payment',
          createdAt: '2026-04-10T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Expense Case 501',
            status: 'Open',
            clientId: 88,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Печать' }));

    const html = getLastPrintHtml();
    expect(html).toContain('Платежное поручение');
    expect(html).toContain('ПП-778');
    expect(html).toContain('Расход');
  });

  it('submits the full overpaid amount and shows balance warning', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 995,
          date: '2026-04-25',
          plannedDate: '2026-04-27',
          amount: 16_250,
          paidAmount: 0,
          outstandingAmount: 16_250,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Рассрочка по счету №3',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          account: '3',
          accountDate: '2026-01-19',
          documentKind: 'Payment',
          createdAt: '2026-04-25T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const paidAmountInput = document.body.querySelector(
      'input[name="paidAmount"]',
    ) as HTMLInputElement | null;
    expect(paidAmountInput).not.toBeNull();

    fireEvent.change(paidAmountInput!, { target: { value: '17000' } });
    fireEvent.blur(paidAmountInput!);

    expect(await screen.findByText('Обнаружена переплата')).toBeTruthy();
    fireEvent.click(screen.getByRole('radio', { name: /Основной платёж \+ аванс/i }));
    fireEvent.change(screen.getByPlaceholderText('Комментарий к обработке переплаты'), {
      target: { value: 'Аванс по делу' },
    });

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 995,
        amount: 16_250,
        paidAmount: 17_000,
        overpaymentHandling: 'Advance',
        overpaymentComment: 'Аванс по делу',
      }),
    );
  });

  it('sends selected installment target for overpayment distribution', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getPaymentsV2Mock.mockResolvedValue({
      items: [
        {
          id: 994,
          date: '2026-04-01',
          plannedDate: '2026-04-01',
          amount: 48_750,
          paidAmount: 16_250,
          outstandingAmount: 32_500,
          hasPartialPayment: true,
          status: 'Processing',
          description: 'Счет № OP-TARGET',
          isPaid: false,
          notes: '',
          type: 'Income',
          invoiceId: 3,
          documentKind: 'Invoice',
          createdAt: '2026-04-01T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        },
        {
          id: 995,
          date: '2026-04-25',
          plannedDate: '2026-04-25',
          amount: 16_250,
          paidAmount: 0,
          outstandingAmount: 16_250,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Счет № OP-TARGET',
          isPaid: false,
          notes: '',
          type: 'Income',
          invoiceId: 3,
          documentKind: 'Invoice',
          createdAt: '2026-04-25T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        },
        {
          id: 996,
          date: '2026-05-25',
          plannedDate: '2026-05-25',
          amount: 16_250,
          paidAmount: 0,
          outstandingAmount: 16_250,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Счет № OP-TARGET',
          isPaid: false,
          notes: '',
          type: 'Income',
          invoiceId: 3,
          documentKind: 'Invoice',
          createdAt: '2026-05-25T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        },
        {
          id: 997,
          date: '2026-06-25',
          plannedDate: '2026-06-25',
          amount: 16_250,
          paidAmount: 0,
          outstandingAmount: 16_250,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Счет № OP-TARGET',
          isPaid: false,
          notes: '',
          type: 'Income',
          invoiceId: 3,
          documentKind: 'Invoice',
          createdAt: '2026-06-25T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        },
      ],
      total: 3,
      page: 1,
      pageSize: 100,
      summary: {},
    });

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 995,
          date: '2026-04-25',
          plannedDate: '2026-04-25',
          amount: 16_250,
          paidAmount: 0,
          outstandingAmount: 16_250,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Счет № OP-TARGET',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          invoiceId: 3,
          account: '3',
          accountDate: '2026-01-19',
          documentKind: 'Invoice',
          createdAt: '2026-04-25T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[{ id: 501, title: 'Case 501', status: 'Open', clientId: 69, createdAt: '2026-03-01T00:00:00Z' }]}
      />,
    );

    const paidAmountInput = document.body.querySelector('input[name="paidAmount"]') as HTMLInputElement | null;
    expect(paidAmountInput).not.toBeNull();
    fireEvent.change(paidAmountInput!, { target: { value: '17000' } });
    fireEvent.blur(paidAmountInput!);

    const nextInstallmentRadio = document.body.querySelector('input[value="NextInstallment"]') as HTMLInputElement | null;
    expect(nextInstallmentRadio).not.toBeNull();
    await waitFor(() => expect(getPaymentsV2Mock).toHaveBeenCalled());
    fireEvent.click(nextInstallmentRadio!);

    expect((await screen.findAllByText(/Платеж для зачета/i)).length).toBeGreaterThan(0);

    expect(await screen.findByText('#997')).toBeTruthy();
    fireEvent.click(screen.getByText('#997'));
    const selectButtons = screen.getAllByRole('button', { name: /Выбрать/i });
    fireEvent.click(selectButtons[selectButtons.length - 1]);

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 995,
        overpaymentHandling: 'NextInstallment',
        overpaymentTargetPaymentId: 997,
      }),
    );
  });

  it('resolves visible account before loading overpayment installment targets', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getInvoicesMock.mockResolvedValue({
      items: [
        {
          id: 3003,
          invoiceEntityId: 3,
          number: '3',
          date: '2026-01-19',
          dueDate: '2026-01-19',
          amount: 90_000,
          type: 'Income',
          status: 'Pending',
          isPaid: false,
          clientId: 69,
          createdAt: '2026-01-19T00:00:00Z',
        },
        {
          id: 3129,
          invoiceEntityId: 129,
          number: '129',
          date: '2025-08-07',
          dueDate: '2025-08-07',
          amount: 90_000,
          type: 'Income',
          status: 'Pending',
          isPaid: false,
          clientId: 69,
          createdAt: '2025-08-07T00:00:00Z',
        },
      ],
      total: 2,
      page: 1,
      pageSize: 20,
    });
    getPaymentsV2Mock.mockImplementation(async (params?: { invoiceId?: number }) => ({
      items: params?.invoiceId === 3
        ? [
            {
              id: 80,
              date: '2026-07-15',
              plannedDate: '2026-07-15',
              amount: 15_000,
              paidAmount: 0,
              outstandingAmount: 15_000,
              hasPartialPayment: false,
              status: 'Pending',
              description: 'Installment for account 3',
              isPaid: false,
              notes: '',
              type: 'Income',
              invoiceId: 3,
              account: '3',
              accountDate: '2026-01-19',
              documentKind: 'Payment',
              createdAt: '2026-07-15T00:00:00Z',
              systemNotes: '[pp:installment-payment] {"sequence":2,"months":6}',
              rescheduleCount: 0,
            },
            {
              id: 81,
              date: '2026-08-15',
              plannedDate: '2026-08-15',
              amount: 15_000,
              paidAmount: 0,
              outstandingAmount: 15_000,
              hasPartialPayment: false,
              status: 'Pending',
              description: 'Installment for account 3',
              isPaid: false,
              notes: '',
              type: 'Income',
              invoiceId: 3,
              account: '3',
              accountDate: '2026-01-19',
              documentKind: 'Payment',
              createdAt: '2026-08-15T00:00:00Z',
              systemNotes: '[pp:installment-payment] {"sequence":3,"months":6}',
              rescheduleCount: 0,
            },
          ]
        : [
            {
              id: 179,
              date: '2026-07-30',
              plannedDate: '2026-07-30',
              amount: 15_000,
              paidAmount: 0,
              outstandingAmount: 15_000,
              hasPartialPayment: false,
              status: 'Pending',
              description: 'Installment for account 129',
              isPaid: false,
              notes: '',
              type: 'Income',
              invoiceId: 129,
              account: '129',
              accountDate: '2025-08-07',
              documentKind: 'Payment',
              createdAt: '2026-07-30T00:00:00Z',
              systemNotes: '[pp:installment-payment] {"sequence":2,"months":6}',
              rescheduleCount: 0,
            },
          ],
      total: 2,
      page: 1,
      pageSize: 100,
      summary: {},
    }));

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 79,
          date: '2026-06-30',
          plannedDate: '2026-06-30',
          amount: 15_000,
          paidAmount: 0,
          outstandingAmount: 15_000,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Current installment with stale invoice link',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          invoiceId: 129,
          account: '3',
          accountDate: '2026-01-19',
          documentKind: 'Payment',
          createdAt: '2026-06-30T00:00:00Z',
          systemNotes: '[pp:installment-payment] {"sequence":1,"months":6}',
          rescheduleCount: 0,
        }}
        casesPrefetch={[{ id: 501, title: 'Case 501', status: 'Open', clientId: 69, createdAt: '2026-03-01T00:00:00Z' }]}
      />,
    );

    await waitFor(() =>
      expect(getPaymentsV2Mock).toHaveBeenCalledWith(expect.objectContaining({ invoiceId: 3 })),
    );
    expect(getPaymentsV2Mock).not.toHaveBeenCalledWith(expect.objectContaining({ invoiceId: 129 }));

    const paidAmountInput = document.body.querySelector('input[name="paidAmount"]') as HTMLInputElement | null;
    expect(paidAmountInput).not.toBeNull();
    fireEvent.change(paidAmountInput!, { target: { value: '17000' } });
    fireEvent.blur(paidAmountInput!);

    const nextInstallmentRadio = document.body.querySelector('input[value="NextInstallment"]') as HTMLInputElement | null;
    expect(nextInstallmentRadio).not.toBeNull();
    fireEvent.click(nextInstallmentRadio!);

    expect(await screen.findByText('#81')).toBeTruthy();
    expect(screen.queryByText('#179')).toBeNull();
    fireEvent.click(screen.getByText('#81'));
    const selectButtons = screen.getAllByRole('button', { name: /Выбрать/i });
    fireEvent.click(selectButtons[selectButtons.length - 1]);

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 79,
        invoiceId: 3,
        overpaymentHandling: 'NextInstallment',
        overpaymentTargetPaymentId: 81,
      }),
    );
  });

  it('transfers an existing partial payment remainder through the atomic handler', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const onCreateRemainder = vi.fn().mockResolvedValue(undefined);
    const onTransferRemainder = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <PaymentModal
        isOpen
        onClose={onClose}
        onSubmit={onSubmit}
        onCreateRemainder={onCreateRemainder}
        onTransferRemainder={onTransferRemainder}
        payment={{
          id: 998,
          date: '2026-04-20',
          plannedDate: '2026-04-20',
          amount: 80_000,
          paidAmount: 0,
          outstandingAmount: 80_000,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Рассрочка по счету №56 · платеж 1/4',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          account: '56',
          accountDate: '2026-04-01',
          documentKind: 'Payment',
          createdAt: '2026-04-20T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const paidAmountInput = document.body.querySelector(
      'input[name="paidAmount"]',
    ) as HTMLInputElement | null;
    expect(paidAmountInput).not.toBeNull();

    fireEvent.change(paidAmountInput!, { target: { value: '35000' } });
    fireEvent.blur(paidAmountInput!);

    fireEvent.click(screen.getByLabelText('Перенести остаток'));

    const rescheduleDateInput = document.body.querySelector('#reschedule-date') as HTMLInputElement | null;
    expect(rescheduleDateInput).not.toBeNull();
    fireEvent.change(rescheduleDateInput!, { target: { value: '2026-05-20' } });

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onTransferRemainder).toHaveBeenCalledTimes(1));
    expect(onTransferRemainder).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 998,
        amount: 35_000,
        paidAmount: 35_000,
        status: 'Completed',
        isPaid: true,
        invoiceId: undefined,
      }),
      '2026-05-20',
    );
    expect(onSubmit).not.toHaveBeenCalled();
    expect(onCreateRemainder).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('applies available client balance to an existing payment', async () => {
    const onBalanceApplied = vi.fn().mockResolvedValue(undefined);
    getClientBalanceMock.mockResolvedValue({ clientId: 69, balance: 750 });
    applyClientBalanceMock.mockResolvedValue({ appliedAmount: 750, balance: 0 });

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        onBalanceApplied={onBalanceApplied}
        payment={{
          id: 996,
          date: '2026-05-25',
          plannedDate: '2026-05-25',
          amount: 16_250,
          paidAmount: 0,
          outstandingAmount: 16_250,
          hasPartialPayment: false,
          status: 'Pending',
          description: 'Рассрочка по счету №3',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: null,
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          account: '3',
          accountDate: '2026-01-19',
          documentKind: 'Payment',
          createdAt: '2026-05-25T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    expect(await screen.findByText('Баланс:')).toBeTruthy();
    expect(await screen.findByText('750,00')).toBeTruthy();

    const balanceInput = screen.getByRole('textbox', { name: 'Сумма зачета' });
    fireEvent.change(balanceInput, { target: { value: '750' } });
    fireEvent.click(screen.getByRole('button', { name: /Зачесть/i }));

    await waitFor(() => expect(applyClientBalanceMock).toHaveBeenCalledWith(69, { paymentId: 996, amount: 750 }));
    await waitFor(() => expect(onBalanceApplied).toHaveBeenCalledWith(996));
    expect(screen.getAllByDisplayValue('750,00').length).toBeGreaterThan(0);
    expect(await screen.findByText(/Зачтено с баланса: 750,00/i)).toBeTruthy();

    const paidAmountInput = document.body.querySelector(
      'input[name="paidAmount"]',
    ) as HTMLInputElement | null;
    expect(paidAmountInput).not.toBeNull();
    fireEvent.change(paidAmountInput!, { target: { value: '100' } });
    fireEvent.blur(paidAmountInput!);
    expect(paidAmountInput!.value).toBe('750,00');
  });

  it('shows available legal entity balance before saving an income payment', async () => {
    getLegalEntityBalanceMock.mockResolvedValue({ clientId: null, legalEntityId: 301, balance: 9_000 });

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        type="Income"
        defaultLegalEntityId={301}
      />,
    );

    expect(await screen.findByText('Доступно на балансе юрлица')).toBeTruthy();
    expect(await screen.findByText('9 000,00')).toBeTruthy();
    expect(getLegalEntityBalanceMock).toHaveBeenCalledWith(301);
    expect(getClientBalanceMock).not.toHaveBeenCalled();
  });

  it('restores existing balance credit as minimum paid amount', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getClientBalanceMock.mockResolvedValue({ clientId: 69, balance: 500 });

    render(
      <PaymentModal
        isOpen
        onClose={vi.fn()}
        onSubmit={onSubmit}
        payment={{
          id: 997,
          date: '2026-06-25',
          plannedDate: '2026-06-25',
          amount: 16_250,
          paidAmount: 11,
          outstandingAmount: 16_239,
          hasPartialPayment: true,
          status: 'Pending',
          description: 'Рассрочка по счету №3',
          isPaid: false,
          paidDate: '',
          lastPaymentDate: '2026-04-28',
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          account: '3',
          accountDate: '2026-01-19',
          documentKind: 'Payment',
          createdAt: '2026-06-25T00:00:00Z',
          systemNotes: 'Системно: Зачтено с баланса клиента: 500,00',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Case 501',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const paidAmountInput = document.body.querySelector(
      'input[name="paidAmount"]',
    ) as HTMLInputElement | null;
    expect(paidAmountInput).not.toBeNull();
    await waitFor(() => expect(paidAmountInput!.value).toBe('500,00'));

    fireEvent.change(paidAmountInput!, { target: { value: '11' } });
    expect(paidAmountInput!.value).toBe('11');
    fireEvent.click(screen.getByRole('button', { name: 'save' }));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(paidAmountInput!.value).toBe('500,00');

    fireEvent.change(paidAmountInput!, { target: { value: '11' } });
    expect(paidAmountInput!.value).toBe('11');
    fireEvent.blur(paidAmountInput!);
    expect(paidAmountInput!.value).toBe('500,00');

    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 997,
        paidAmount: 500,
      }),
    );
  });

  it('annuls an editable payment from the payment card', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <PaymentModal
        isOpen
        onClose={onClose}
        onSubmit={onSubmit}
        payment={{
          id: 992,
          date: '2026-04-10',
          plannedDate: '2026-04-10',
          amount: 15_000,
          paidAmount: 2_000,
          outstandingAmount: 13_000,
          hasPartialPayment: true,
          status: 'Overdue',
          description: 'Просроченный расходный платеж',
          isPaid: false,
          paidDate: '2026-04-11',
          lastPaymentDate: '2026-04-11',
          notes: 'Комментарий',
          clientId: 77,
          expenseClientId: 88,
          clientCaseId: 501,
          paymentSourceId: 5,
          type: 'Expense',
          account: '',
          accountDate: '',
          documentKind: 'Payment',
          createdAt: '2026-04-10T00:00:00Z',
          systemNotes: '',
          rescheduleCount: 0,
        }}
        casesPrefetch={[
          {
            id: 501,
            title: 'Expense Case 501',
            status: 'Open',
            clientId: 88,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    const statusSelect = document.body.querySelector('select[name="status"]') as HTMLSelectElement | null;
    expect(statusSelect).not.toBeNull();
    expect(Array.from(statusSelect!.options).some((option) => option.value === 'Cancelled')).toBe(true);

    fireEvent.click(screen.getByRole('button', { name: 'Аннулировать' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 992,
        status: 'Cancelled',
        isPaid: false,
        paidAmount: 0,
        paidDate: undefined,
        lastPaymentDate: undefined,
        amount: 15_000,
        type: 'Expense',
        clientId: 77,
        expenseClientId: 88,
      }),
    );
    expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({ confirmLabel: 'Аннулировать' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('annuls an invoice-managed payment from the payment card', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <PaymentModal
        isOpen
        onClose={onClose}
        onSubmit={onSubmit}
        payment={{
          id: 1102,
          date: '2026-03-31',
          plannedDate: '2026-03-31',
          amount: 165_000,
          paidAmount: 165_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Счет №94 от 24 апреля 2025 г.',
          isPaid: true,
          paidDate: '2026-03-31',
          lastPaymentDate: '2026-03-31',
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          invoiceId: 94,
          account: '94',
          accountDate: '2025-04-24',
          documentKind: 'Payment',
          createdAt: '2026-03-31T00:00:00Z',
          systemNotes: '[pp:installment-payment] {"sequence":1}',
          rescheduleCount: 0,
        }}
        linkedInvoice={{
          invoiceEntityId: 94,
          invoiceRecordId: 1102,
          number: '94',
          date: '2025-04-24',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Аннулировать' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 1102,
        status: 'Cancelled',
        isPaid: false,
        paidAmount: 0,
        invoiceId: 94,
      }),
    );
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('moves an invoice-managed payment from the payment card', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <PaymentModal
        isOpen
        onClose={onClose}
        onSubmit={onSubmit}
        payment={{
          id: 1103,
          date: '2026-03-31',
          plannedDate: '2026-03-31',
          amount: 165_000,
          paidAmount: 165_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Счет №94 от 24 апреля 2025 г.',
          isPaid: true,
          paidDate: '2026-03-31',
          lastPaymentDate: '2026-03-31',
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          invoiceId: 94,
          account: '94',
          accountDate: '2025-04-24',
          documentKind: 'Payment',
          createdAt: '2026-03-31T00:00:00Z',
          systemNotes: '[pp:installment-payment] {"sequence":1}',
          rescheduleCount: 0,
        }}
        linkedInvoice={{
          invoiceEntityId: 94,
          invoiceRecordId: 1102,
          number: '94',
          date: '2025-04-24',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Перенести' }));
    fireEvent.change(screen.getByLabelText('Новая дата платежа'), {
      target: { value: '2026-04-15' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить перенос' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 1103,
        date: '2026-04-15',
        plannedDate: '2026-04-15',
        status: 'Completed',
        paidAmount: 165_000,
        invoiceId: 94,
      }),
    );
    expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({ confirmLabel: 'Перенести' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('deletes an invoice-managed installment payment from the payment card when deletion is allowed', async () => {
    const onDelete = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <PaymentModal
        isOpen
        onClose={onClose}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        onDelete={onDelete}
        payment={{
          id: 1104,
          date: '2026-03-31',
          plannedDate: '2026-03-31',
          amount: 165_000,
          paidAmount: 165_000,
          outstandingAmount: 0,
          hasPartialPayment: false,
          status: 'Completed',
          description: 'Счет №94 от 24 апреля 2025 г.',
          isPaid: true,
          paidDate: '2026-03-31',
          lastPaymentDate: '2026-03-31',
          notes: '',
          clientId: 69,
          clientCaseId: 501,
          type: 'Income',
          invoiceId: 94,
          account: '94',
          accountDate: '2025-04-24',
          documentKind: 'Payment',
          createdAt: '2026-03-31T00:00:00Z',
          systemNotes: '[pp:installment-payment] {"sequence":1}',
          rescheduleCount: 0,
        }}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'delete' }));

    await waitFor(() => expect(onDelete).toHaveBeenCalledWith(1104));
    expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({ confirmLabel: 'Удалить' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
