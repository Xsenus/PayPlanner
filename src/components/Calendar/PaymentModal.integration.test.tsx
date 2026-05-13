import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { PaymentModal } from './PaymentModal';

const {
  confirmMock,
  createInvoiceMock,
  getActResponsiblesMock,
  getCaseMock,
  getCasesMock,
  getContractsMock,
  getInvoicesMock,
  getClientsV2Mock,
  getLegalEntitiesPagedMock,
  getClientBalanceMock,
  applyClientBalanceMock,
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
  getClientsV2Mock: vi.fn(),
  getLegalEntitiesPagedMock: vi.fn(),
  getClientBalanceMock: vi.fn(),
  applyClientBalanceMock: vi.fn(),
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
    dealTypes: [],
    incomeTypes: [],
    paymentSources: [
      { id: 5, name: 'Банковский перевод', description: '', colorHex: '#000000', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
      { id: 6, name: 'Наличные', description: '', colorHex: '#000000', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
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
  useToast: () => ({ error: toastErrorMock, success: toastSuccessMock }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getInvoices: getInvoicesMock,
    getActResponsibles: getActResponsiblesMock,
    getCases: getCasesMock,
    getCase: getCaseMock,
    getContracts: getContractsMock,
    createInvoice: createInvoiceMock,
    getClientsV2: getClientsV2Mock,
    getLegalEntitiesPaged: getLegalEntitiesPagedMock,
    getClientBalance: getClientBalanceMock,
    applyClientBalance: applyClientBalanceMock,
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
  let current: HTMLElement | null = label;

  while (current && current !== document.body) {
    const actions = within(current).queryAllByRole('button', { name: 'Выбрать' });
    if (actions.length === 1) {
      return actions[0] as HTMLButtonElement;
    }
    current = current.parentElement;
  }

  throw new Error(`Не найдена кнопка выбора для ${label.textContent ?? 'контрагента'}`);
}

async function selectPickerOption(name: string): Promise<void> {
  const labels = await screen.findAllByText(name);
  for (const label of labels.reverse()) {
    try {
      fireEvent.click(getPickerOptionAction(label));
      return;
    } catch {
      // Some names can also exist in hidden selects outside the picker.
    }
  }

  const visibleActions = screen.queryAllByRole('button', { name: 'Выбрать' });
  if (visibleActions.length > 0) {
    fireEvent.click(visibleActions[0]);
    return;
  }

  throw new Error(`Не найден вариант выбора ${name}`);
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
    getClientsV2Mock.mockReset();
    getLegalEntitiesPagedMock.mockReset();
    getClientBalanceMock.mockReset();
    applyClientBalanceMock.mockReset();
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
    getClientsV2Mock.mockResolvedValue({
      items: [
        { id: 69, name: 'Test Client', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
        { id: 77, name: 'Supplier Client', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
        { id: 88, name: 'Expense Client', isActive: true, createdAt: '2026-04-01T00:00:00Z' },
      ],
      total: 3,
      page: 1,
      pageSize: 25,
    });
    getLegalEntitiesPagedMock.mockResolvedValue({
      items: [
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
      total: 2,
      page: 1,
      pageSize: 25,
    });
    getClientBalanceMock.mockResolvedValue({ clientId: 69, balance: 0 });
    applyClientBalanceMock.mockResolvedValue({ appliedAmount: 0, balance: 0 });
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
    expect(screen.getAllByDisplayValue('5 000.00')).toHaveLength(2);
    expect(screen.getByDisplayValue('Existing payment description')).toBeTruthy();
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
    expect(screen.getByText('draft-amount:5 000.00')).toBeTruthy();
    expect(screen.getByText('draft-date:2026-04-01')).toBeTruthy();
    expect(screen.getByText('draft-client:69')).toBeTruthy();
    expect(screen.getByText('draft-contract:321')).toBeTruthy();
    expect(screen.getByText('draft-case-count:1')).toBeTruthy();

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
    fireEvent.click(screen.getByRole('button', { name: 'save' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const payload = onSubmit.mock.calls[0][0] as Record<string, unknown>;
    expect(payload.legalEntityId).toBe(301);
    expect(payload.clientId).toBeUndefined();
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
    expect(await screen.findByText('750.00')).toBeTruthy();

    const balanceInput = screen.getByRole('textbox', { name: 'Сумма зачета' });
    fireEvent.change(balanceInput, { target: { value: '750' } });
    fireEvent.click(screen.getByRole('button', { name: /Зачесть/i }));

    await waitFor(() => expect(applyClientBalanceMock).toHaveBeenCalledWith(69, { paymentId: 996, amount: 750 }));
    await waitFor(() => expect(onBalanceApplied).toHaveBeenCalledWith(996));
    expect(screen.getAllByDisplayValue('750.00').length).toBeGreaterThan(0);
    expect(await screen.findByText(/Зачтено с баланса: 750.00/i)).toBeTruthy();

    const paidAmountInput = document.body.querySelector(
      'input[name="paidAmount"]',
    ) as HTMLInputElement | null;
    expect(paidAmountInput).not.toBeNull();
    fireEvent.change(paidAmountInput!, { target: { value: '100' } });
    fireEvent.blur(paidAmountInput!);
    expect(paidAmountInput!.value).toBe('750.00');
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
    await waitFor(() => expect(paidAmountInput!.value).toBe('500.00'));

    fireEvent.change(paidAmountInput!, { target: { value: '11' } });
    expect(paidAmountInput!.value).toBe('11');
    fireEvent.click(screen.getByRole('button', { name: 'save' }));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(paidAmountInput!.value).toBe('500.00');

    fireEvent.change(paidAmountInput!, { target: { value: '11' } });
    expect(paidAmountInput!.value).toBe('11');
    fireEvent.blur(paidAmountInput!);
    expect(paidAmountInput!.value).toBe('500.00');

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
