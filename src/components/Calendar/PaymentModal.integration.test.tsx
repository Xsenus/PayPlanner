import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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

describe('PaymentModal integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    createInvoiceMock.mockReset();
    getActResponsiblesMock.mockReset();
    getCaseMock.mockReset();
    getCasesMock.mockReset();
    getContractsMock.mockReset();
    getInvoicesMock.mockReset();
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
    expect(screen.getByRole('button', { name: 'selectClient' })).toHaveProperty('disabled', true);
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
});
