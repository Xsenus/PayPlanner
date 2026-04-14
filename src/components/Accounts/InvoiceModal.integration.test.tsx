import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { InvoiceModal } from './InvoiceModal';
import type { Invoice, Payment } from '../../types';
import { apiService } from '../../services/api';

const {
  confirmMock,
  toastErrorMock,
  toastSuccessMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    invoiceUnits: [],
    services: [],
    serviceCategories: [],
  }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ error: toastErrorMock, success: toastSuccessMock }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    downloadInvoiceDocument: vi
      .fn()
      .mockResolvedValue(new Blob(['<html><body>preview</body></html>'], { type: 'text/html' })),
    getInvoiceRulesSettings: vi
      .fn()
      .mockResolvedValue({ maxClientCasesPerInvoice: 3, maxActsPerInvoice: 3 }),
    getContracts: vi.fn().mockResolvedValue({ items: [] }),
    getCases: vi.fn().mockResolvedValue([]),
    getActs: vi.fn().mockResolvedValue({ items: [] }),
    getNextInvoiceNumber: vi.fn().mockResolvedValue('24'),
    createContract: vi.fn(),
    createCase: vi.fn(),
    createAct: vi.fn(),
    updateInvoice: vi.fn(),
    createInvoice: vi.fn(),
    sendInvoiceByEmail: vi.fn(),
    deleteInvoice: vi.fn(),
  },
}));

vi.mock('../Clients/CaseModal', () => ({
  CaseModal: () => null,
}));

vi.mock('../Contracts/ContractModal', () => ({
  ContractModal: () => null,
}));

vi.mock('../ui/LookupSelect', () => ({
  LookupSelect: () => null,
}));

vi.mock('./InstallmentPlanner', () => ({
  InstallmentPlanner: () => null,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

vi.mock('../../utils/invoiceEditorBridge', () => ({
  storeInvoiceEditorDraft: vi.fn(),
}));

describe('InvoiceModal integration', () => {
  beforeEach(() => {
    confirmMock.mockReset();
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    vi.restoreAllMocks();
    Object.defineProperty(window, 'open', {
      configurable: true,
      value: vi.fn(() => ({
        closed: false,
        location: { href: '' },
        document: {
          open: vi.fn(),
          write: vi.fn(),
          close: vi.fn(),
        },
        close: vi.fn(),
      })),
    });
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: vi.fn(() => 'blob:test'),
    });
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: vi.fn(),
    });
  });

  it('renders related payments even when the current invoice record is the only operation available', async () => {
    const invoice = {
      id: 528,
      invoiceEntityId: 24,
      number: '24',
      date: '2026-04-02',
      dueDate: '2026-04-03',
      amount: 10000,
      type: 'Income',
      status: 'Completed',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 10000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 528,
      invoiceId: 24,
      amount: 10000,
      date: '2026-04-02',
      paidDate: '2026-04-03',
      type: 'Income',
      status: 'Completed',
      description: 'Оплата по счету №24',
      account: '24',
      accountDate: '2026-04-02',
    } as unknown as Payment;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[relatedPayment]}
      />,
    );

    await screen.findByText('Оплата по счету №24');
    expect(screen.queryByText('Связанные платежи пока не добавлены.')).toBeNull();
    expect(screen.getAllByText('10 000 ₽').length).toBeGreaterThan(0);
  });

  it('renders sibling operations for the same invoice together', async () => {
    const invoice = {
      id: 529,
      invoiceEntityId: 25,
      number: '25',
      date: '2026-04-02',
      dueDate: '2026-04-03',
      amount: 15000,
      type: 'Income',
      status: 'Completed',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 15000 }],
    } as unknown as Invoice;

    const representativePayment = {
      id: 529,
      invoiceId: 25,
      amount: 10000,
      date: '2026-04-02',
      paidDate: '2026-04-03',
      type: 'Income',
      status: 'Completed',
      description: 'Первая оплата по счету №25',
      account: '25',
      accountDate: '2026-04-02',
    } as unknown as Payment;

    const siblingPayment = {
      id: 530,
      invoiceId: 25,
      amount: 5000,
      date: '2026-04-04',
      paidDate: '2026-04-05',
      type: 'Income',
      status: 'Completed',
      description: 'Доплата по счету №25',
      account: '25',
      accountDate: '2026-04-02',
    } as unknown as Payment;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[representativePayment, siblingPayment]}
      />,
    );

    await screen.findByText('Первая оплата по счету №25');
    expect(screen.getByText('Доплата по счету №25')).toBeTruthy();
    expect(screen.getAllByText(/₽/).length).toBeGreaterThan(1);
  });

  it('restores installment summary and badges from related installment payments', async () => {
    const invoice = {
      id: 530,
      invoiceEntityId: 26,
      number: '26',
      date: '2026-05-10',
      dueDate: '2026-05-10',
      amount: 10000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Рассрочка по счету', quantity: 1, unit: 'шт', price: 10000 }],
    } as unknown as Invoice;

    const firstInstallment = {
      id: 601,
      invoiceId: 26,
      amount: 5600,
      date: '2026-05-10',
      plannedDate: '2026-05-10',
      type: 'Income',
      status: 'Pending',
      description: 'Рассрочка по счету №26 · платеж 1/2',
      account: '26',
      accountDate: '2026-05-10',
      systemNotes:
        '[pp:installment-payment] {"sequence":1,"total":10000,"downPayment":0,"annualRate":0,"months":2,"startDate":"2026-05-10","roundingMode":"none","roundingStep":null,"toPay":11200,"overpay":1200,"loanAmount":10000,"baseMonthlyPayment":5600,"roundedMonthlyPayment":5600,"item":{"sequence":1,"date":"2026-05-10","principal":5000,"interest":600,"payment":5600,"balance":5000}}',
    } as unknown as Payment;

    const secondInstallment = {
      id: 602,
      invoiceId: 26,
      amount: 5600,
      date: '2026-06-10',
      plannedDate: '2026-06-10',
      type: 'Income',
      status: 'Pending',
      description: 'Рассрочка по счету №26 · платеж 2/2',
      account: '26',
      accountDate: '2026-05-10',
      systemNotes:
        '[pp:installment-payment] {"sequence":2,"total":10000,"downPayment":0,"annualRate":0,"months":2,"startDate":"2026-05-10","roundingMode":"none","roundingStep":null,"toPay":11200,"overpay":1200,"loanAmount":10000,"baseMonthlyPayment":5600,"roundedMonthlyPayment":5600,"item":{"sequence":2,"date":"2026-06-10","principal":5000,"interest":600,"payment":5600,"balance":0}}',
    } as unknown as Payment;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[firstInstallment, secondInstallment]}
      />,
    );

    await screen.findByText('Рассрочка по счету №26 · платеж 1/2');
    expect(screen.getByText('Изменить рассрочку')).toBeTruthy();
    expect(screen.getByText(/Настроено платежей:\s*2,/)).toBeTruthy();
    expect(screen.getByText('Рассрочка 1/2')).toBeTruthy();
    expect(screen.getByText('Рассрочка 2/2')).toBeTruthy();
  });

  it('downloads document variant without signature and seal on demand', async () => {
    const downloadMock = vi.mocked(apiService.downloadInvoiceDocument);
    downloadMock.mockClear();

    const invoice = {
      id: 551,
      invoiceEntityId: 551,
      number: '551',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 18000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 18000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[]}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: /скачать/i }));
    fireEvent.click(screen.getByRole('button', { name: /pdf без подписи и печати/i }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(551, 'pdf', { includeSignatureAndSeal: false });
    });
  });

  it('opens print window before requesting printable pdf document', async () => {
    const downloadMock = vi.mocked(apiService.downloadInvoiceDocument);
    downloadMock.mockClear();
    const openMock = vi.mocked(window.open);

    const invoice = {
      id: 552,
      invoiceEntityId: 552,
      number: '552',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 18000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 18000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[]}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: /печать/i }));

    expect(openMock).toHaveBeenCalled();
    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(552, 'pdf', { includeSignatureAndSeal: false });
    });
  });
});
