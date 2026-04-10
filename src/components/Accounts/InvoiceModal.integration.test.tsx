import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { InvoiceModal } from './InvoiceModal';
import type { Invoice, Payment } from '../../types';

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
  useTranslation: () => ({ t: (key: string) => key }),
}));

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    invoiceUnits: [],
    invoiceItemNames: [],
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
    downloadInvoiceDocument: vi.fn().mockResolvedValue(new Blob(['<html><body>preview</body></html>'], { type: 'text/html' })),
    getInvoiceRulesSettings: vi.fn().mockResolvedValue({ maxClientCasesPerInvoice: 3, maxActsPerInvoice: 3 }),
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
});
