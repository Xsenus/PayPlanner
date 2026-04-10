import { render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { PaymentModal } from './PaymentModal';

const {
  confirmMock,
  toastErrorMock,
  toastSuccessMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
}));

vi.mock('../../hooks/useClients', () => ({
  useClients: () => ({
    clients: [{ id: 69, name: 'Байгузина Екатерина Евгеньевна' }],
    loading: false,
  }),
}));

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    dealTypes: [],
    incomeTypes: [],
    paymentSources: [],
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
    getInvoices: vi.fn().mockResolvedValue({ items: [] }),
    getActResponsibles: vi.fn().mockResolvedValue([]),
    getCases: vi.fn().mockResolvedValue([]),
    getCase: vi.fn().mockResolvedValue(null),
    getContracts: vi.fn().mockResolvedValue({ items: [] }),
    createInvoice: vi.fn(),
  },
}));

vi.mock('../Accounts/InvoiceModal', () => ({
  InvoiceModal: () => null,
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
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
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
            title: 'БАЙГУЗИНА - ОТМЕНА ПРОТОКОЛОВ ОБЩЕГО СОБРАНИЯ',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    await screen.findByText(/Платеж привязан к счету № 24 от 02\.04\.2026\./);
    expect(screen.getByText('Счет № 24')).toBeTruthy();
    expect(screen.getByDisplayValue('24')).toBeTruthy();
    expect(screen.getByDisplayValue('2026-04-02')).toBeTruthy();
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
          amount: 5000,
          paidAmount: 5000,
          status: 'Completed',
          description: 'Доплата по счету №24',
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
            title: 'БАЙГУЗИНА - ОТМЕНА ПРОТОКОЛОВ ОБЩЕГО СОБРАНИЯ',
            status: 'Open',
            clientId: 69,
            createdAt: '2026-03-01T00:00:00Z',
          },
        ]}
      />,
    );

    await screen.findByText(/Платеж привязан к счету № 24 от 02\.04\.2026\./);
    expect(screen.getByDisplayValue('24')).toBeTruthy();
    expect(screen.getByDisplayValue('2026-04-02')).toBeTruthy();
    expect(screen.queryByDisplayValue('OLD-24')).toBeNull();
    expect(screen.queryByDisplayValue('2026-01-01')).toBeNull();
    expect(screen.getAllByDisplayValue('5 000.00')).toHaveLength(2);
    expect(screen.getByDisplayValue('Доплата по счету №24')).toBeTruthy();
  });
});
