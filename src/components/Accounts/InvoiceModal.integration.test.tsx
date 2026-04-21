import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { InvoiceModal } from './InvoiceModal';
import type {
  Act,
  Client,
  ClientCase,
  Contract,
  ContractInput,
  InstallmentPlanInput,
  Invoice,
  Payment,
  ServiceCategoryDictionaryItem,
  ServiceDictionaryItem,
} from '../../types';
import type { BaseDictItem } from '../../types/dictionaries';
import { apiService } from '../../services/api';
import { storeInvoiceEditorDraft } from '../../utils/invoiceEditorBridge';

type ContractModalMockProps = {
  open: boolean;
  onSubmit: (payload: ContractInput) => void | Promise<void>;
};

type CaseModalMockProps = {
  isOpen: boolean;
  onSave: (payload: Partial<ClientCase>) => void | Promise<void>;
  overlayClassName?: string;
};

type InstallmentPlannerMockProps = {
  onApplyPlan: (plan: InstallmentPlanInput | null) => void;
};

const {
  caseModalSubmitPayloadRef,
  caseModalPropsSpy,
  confirmMock,
  installmentPlannerPlanRef,
  installmentPlannerPropsSpy,
  contractModalSubmitPayloadRef,
  invoiceUnitsMock,
  serviceCategoriesMock,
  servicesMock,
  toastErrorMock,
  toastSuccessMock,
} = vi.hoisted(() => ({
  caseModalSubmitPayloadRef: {
    current: {
      title: 'Case from invoice',
      description: 'Created from invoice',
      status: 'Open',
    } as Partial<ClientCase>,
  },
  caseModalPropsSpy: vi.fn(),
  confirmMock: vi.fn(),
  installmentPlannerPlanRef: { current: null as InstallmentPlanInput | null },
  installmentPlannerPropsSpy: vi.fn(),
  contractModalSubmitPayloadRef: {
    current: {
      number: 'CT-001',
      date: '2026-04-15',
      billingMode: 'Fixed',
      isOpenEnded: false,
      amount: 10_000,
      clients: [{ clientId: 1 }],
      actIds: [],
      invoiceIds: [],
      paymentIds: [],
    } satisfies ContractInput,
  },
  invoiceUnitsMock: [] as BaseDictItem[],
  serviceCategoriesMock: [] as ServiceCategoryDictionaryItem[],
  servicesMock: [] as ServiceDictionaryItem[],
  toastErrorMock: vi.fn(),
  toastSuccessMock: vi.fn(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    invoiceUnits: invoiceUnitsMock,
    services: servicesMock,
    serviceCategories: serviceCategoriesMock,
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
    getContract: vi.fn(),
    getContracts: vi.fn().mockResolvedValue({ items: [] }),
    getCases: vi.fn().mockResolvedValue([]),
    getActs: vi.fn().mockResolvedValue({ items: [] }),
    getNextInvoiceNumber: vi.fn().mockResolvedValue('24'),
    getPayment: vi.fn(),
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
  CaseModal: (props: CaseModalMockProps) => {
    caseModalPropsSpy(props);
    if (!props.isOpen) return null;
    return (
      <div data-testid="case-modal">
        <button type="button" onClick={() => props.onSave(caseModalSubmitPayloadRef.current)}>
          submit-case
        </button>
      </div>
    );
  },
}));

vi.mock('../Contracts/ContractModal', () => ({
  ContractModal: (props: ContractModalMockProps) => {
    if (!props.open) return null;
    return (
      <div data-testid="contract-modal">
        <button type="button" onClick={() => props.onSubmit(contractModalSubmitPayloadRef.current)}>
          submit-contract
        </button>
      </div>
    );
  },
}));

vi.mock('../ui/LookupSelect', () => ({
  LookupSelect: () => null,
}));

vi.mock('./InstallmentPlanner', () => ({
  InstallmentPlanner: (props: InstallmentPlannerMockProps) => {
    installmentPlannerPropsSpy(props);
    return (
      <div data-testid="installment-planner">
        <button type="button" onClick={() => props.onApplyPlan(installmentPlannerPlanRef.current)}>
          apply-installment-plan
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

vi.mock('../../utils/invoiceEditorBridge', () => ({
  storeInvoiceEditorDraft: vi.fn(),
}));

function createDeferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });

  return { promise, resolve, reject };
}

function getContractSelect(): HTMLSelectElement {
  const select = getAddContractButton().parentElement?.querySelector('select');
  expect(select).toBeTruthy();
  return select as HTMLSelectElement;
}

function getAddContractButton(): HTMLButtonElement {
  return screen.getByRole('button', { name: 'Добавить договор' }) as HTMLButtonElement;
}

function createTestClient(id: number, name: string): Client {
  return {
    id,
    name,
    company: null,
    isActive: true,
    createdAt: '2026-04-15T00:00:00Z',
  } as Client;
}

describe('InvoiceModal integration', () => {
  const storeInvoiceEditorDraftMock = vi.mocked(storeInvoiceEditorDraft);

  beforeEach(() => {
    caseModalSubmitPayloadRef.current = {
      title: 'Case from invoice',
      description: 'Created from invoice',
      status: 'Open',
    };
    caseModalPropsSpy.mockReset();
    confirmMock.mockReset();
    confirmMock.mockResolvedValue(true);
    installmentPlannerPlanRef.current = null;
    installmentPlannerPropsSpy.mockReset();
    contractModalSubmitPayloadRef.current = {
      number: 'CT-001',
      date: '2026-04-15',
      billingMode: 'Fixed',
      isOpenEnded: false,
      amount: 10_000,
      clients: [{ clientId: 1 }],
      actIds: [],
      invoiceIds: [],
      paymentIds: [],
    };
    invoiceUnitsMock.splice(0, invoiceUnitsMock.length);
    serviceCategoriesMock.splice(0, serviceCategoriesMock.length);
    servicesMock.splice(0, servicesMock.length);
    toastErrorMock.mockReset();
    toastSuccessMock.mockReset();
    storeInvoiceEditorDraftMock.mockReset();
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
    vi.mocked(apiService.getCases).mockReset();
    vi.mocked(apiService.getCases).mockResolvedValue([]);
    vi.mocked(apiService.createCase).mockReset();
    vi.mocked(apiService.createAct).mockReset();
    vi.mocked(apiService.getContract).mockReset();
    vi.mocked(apiService.getPayment).mockReset();
    vi.mocked(apiService.updateInvoice).mockClear();
  });

  it('keeps the linked contract selected after invoice data is reopened and contracts finish loading', async () => {
    const deferredContracts = createDeferred<{ items: Array<{ id: number; number: string; title?: string; date: string }> }>();
    vi.mocked(apiService.getContracts).mockImplementationOnce(async () => deferredContracts.promise);

    const invoice = {
      id: 700,
      invoiceEntityId: 700,
      number: '700',
      date: '2026-04-15',
      dueDate: '2026-04-20',
      amount: 15000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      contractId: 77,
      lineItems: [{ name: 'Услуга', quantity: 1, unit: 'шт', price: 15000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    deferredContracts.resolve({
      items: [{ id: 77, number: 'CT-077', title: 'Обслуживание', date: '2026-04-15' }],
    });

    await waitFor(() => {
      expect(getContractSelect().value).toBe('77');
    });
  });

  it('preserves a contract created from the invoice even when an older contracts request resolves afterwards', async () => {
    const deferredContracts = createDeferred<{ items: Array<{ id: number; number: string; title?: string; date: string }> }>();
    vi.mocked(apiService.getContracts).mockImplementationOnce(async () => deferredContracts.promise);
    vi.mocked(apiService.createContract).mockResolvedValue({
      id: 91,
      number: 'CT-091',
      title: 'Новый договор',
      date: '2026-04-15',
      billingMode: 'Fixed',
      isOpenEnded: false,
      amount: 10000,
      createdAt: '2026-04-15T10:00:00Z',
      invoiceAmountTotal: 0,
      actAmountTotal: 0,
      paymentAmountTotal: 0,
      clients: [{ id: 1, name: 'Тестовый клиент' }],
      actIds: [],
      invoiceIds: [],
      paymentIds: [],
    } as Contract);

    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        defaultClientId={1}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(getAddContractButton());
    fireEvent.click(await screen.findByRole('button', { name: 'submit-contract' }));

    await waitFor(() => {
      expect(getContractSelect().value).toBe('91');
    });

    deferredContracts.resolve({ items: [] });

    await waitFor(() => {
      expect(getContractSelect().value).toBe('91');
    });

    fireEvent.change(screen.getByLabelText('Номер счёта'), { target: { value: 'INV-091' } });
    const form = document.getElementById('invoice-edit-form') as HTMLFormElement | null;
    expect(form).toBeTruthy();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ contractId: 91 }));
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
    expect(screen.getAllByText(/₽/).length).toBeGreaterThan(0);
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

    await screen.findByText((content) => content.includes('26') && content.includes('1/2'));
    expect(screen.getAllByText(/1\/2/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/2\/2/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/11\s*200/).length).toBeGreaterThan(0);
  });

  it('prompts to align due date with the last installment payment before saving the invoice', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const invoice = {
      id: 531,
      invoiceEntityId: 27,
      number: '27',
      date: '2026-05-10',
      dueDate: '2026-05-10',
      amount: 10000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      lineItems: [{ name: 'Рассрочка по счету', quantity: 1, unit: 'шт', price: 10000 }],
    } as unknown as Invoice;

    const firstInstallment = {
      id: 611,
      invoiceId: 27,
      amount: 5600,
      date: '2026-05-10',
      plannedDate: '2026-05-10',
      type: 'Income',
      status: 'Pending',
      description: 'Рассрочка по счету №27 · платеж 1/2',
      account: '27',
      accountDate: '2026-05-10',
      systemNotes:
        '[pp:installment-payment] {"sequence":1,"total":10000,"downPayment":0,"annualRate":0,"months":2,"startDate":"2026-05-10","roundingMode":"none","roundingStep":null,"toPay":11200,"overpay":1200,"loanAmount":10000,"baseMonthlyPayment":5600,"roundedMonthlyPayment":5600,"item":{"sequence":1,"date":"2026-05-10","principal":5000,"interest":600,"payment":5600,"balance":5000}}',
    } as unknown as Payment;

    const secondInstallment = {
      id: 612,
      invoiceId: 27,
      amount: 5600,
      date: '2026-06-10',
      plannedDate: '2026-06-10',
      type: 'Income',
      status: 'Pending',
      description: 'Рассрочка по счету №27 · платеж 2/2',
      account: '27',
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
        onSubmit={onSubmit}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[firstInstallment, secondInstallment]}
      />,
    );

    const saveButton = document.querySelector('button[type="submit"]') as HTMLButtonElement | null;
    expect(saveButton).toBeTruthy();
    fireEvent.click(saveButton!);

    await waitFor(() => {
      expect(confirmMock).toHaveBeenCalled();
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ dueDate: '2026-06-10' }));
    });
  });

  it('aligns due date with the last installment payment when the plan is applied from the editor', async () => {
    installmentPlannerPlanRef.current = {
      total: 10000,
      downPayment: 0,
      annualRate: 0,
      months: 2,
      startDate: '2026-05-10',
      roundingMode: 'none',
      roundingStep: null,
      toPay: 11200,
      overpay: 1200,
      loanAmount: 10000,
      baseMonthlyPayment: 5600,
      roundedMonthlyPayment: 5600,
      items: [
        { sequence: 1, date: '2026-05-10', principal: 5000, interest: 600, payment: 5600, balance: 5000 },
        { sequence: 2, date: '2026-06-10', principal: 5000, interest: 600, payment: 5600, balance: 0 },
      ],
    };

    const invoice = {
      id: 532,
      invoiceEntityId: 28,
      number: '28',
      date: '2026-05-10',
      dueDate: '2026-05-10',
      amount: 10000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      lineItems: [{ name: 'Рассрочка по счету', quantity: 1, unit: 'шт', price: 10000 }],
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
        relatedPayments={[]}
      />,
    );

    const installmentButton = document.querySelector('label button[type="button"]') as HTMLButtonElement | null;
    expect(installmentButton).toBeTruthy();
    fireEvent.click(installmentButton!);
    expect(await screen.findByTestId('installment-planner')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'apply-installment-plan' }));

    await waitFor(() => {
      expect(vi.mocked(apiService.updateInvoice)).toHaveBeenCalledWith(
        532,
        expect.objectContaining({
          dueDate: '2026-06-10',
          replaceInstallmentPlan: true,
          installmentPlan: expect.objectContaining({
            items: expect.arrayContaining([
              expect.objectContaining({ sequence: 1 }),
              expect.objectContaining({ sequence: 2 }),
            ]),
          }),
        }),
      );
    });

    const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[];
    expect(dateInputs[1]?.value).toBe('2026-06-10');
  });

  it('sends explicit installment removal when the plan is cleared from the editor', async () => {
    const invoice = {
      id: 533,
      invoiceEntityId: 29,
      number: '29',
      date: '2026-05-10',
      dueDate: '2026-06-10',
      amount: 10000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      lineItems: [{ name: 'Рассрочка по счету', quantity: 1, unit: 'шт', price: 10000 }],
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
        relatedPayments={[]}
      />,
    );

    const installmentButton = document.querySelector('label button[type="button"]') as HTMLButtonElement | null;
    expect(installmentButton).toBeTruthy();
    fireEvent.click(installmentButton!);
    expect(await screen.findByTestId('installment-planner')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'apply-installment-plan' }));

    await waitFor(() => {
      expect(vi.mocked(apiService.updateInvoice)).toHaveBeenCalledWith(
        533,
        expect.objectContaining({
          replaceInstallmentPlan: true,
          installmentPlan: null,
        }),
      );
    });
  });

  it('opens polished category and service dialogs, filters by search and applies the matching template', async () => {
    invoiceUnitsMock.push({ name: 'шт' }, { name: 'усл.ед.' }, { name: 'час' });
    serviceCategoriesMock.push(
      { id: 1, name: 'Арбитраж', description: '', colorHex: '#111827', isActive: true, createdAt: '2026-04-01' },
      { id: 2, name: 'Гражданские дела', description: '', colorHex: '#1d4ed8', isActive: true, createdAt: '2026-04-01' },
    );
    servicesMock.push(
      { id: 1, name: 'Консультация', description: '', unit: 'час', defaultPrice: 5000, categoryId: 1, colorHex: '#111827', isActive: true, createdAt: '2026-04-01' },
      { id: 2, name: 'Экспертиза', description: '', unit: 'усл.ед.', defaultPrice: 12000, categoryId: 1, colorHex: '#111827', isActive: true, createdAt: '2026-04-01' },
      { id: 3, name: 'Консультация', description: '', unit: 'усл.ед.', defaultPrice: 8000, categoryId: 2, colorHex: '#1d4ed8', isActive: true, createdAt: '2026-04-01' },
      { id: 4, name: 'Составление иска', description: '', unit: 'усл.ед.', defaultPrice: 15000, categoryId: 2, colorHex: '#1d4ed8', isActive: true, createdAt: '2026-04-01' },
    );

    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    expect(screen.queryByRole('button', { name: 'Категория услуги' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Наименование услуги' }));
    expect(screen.getByText('Выбор наименования услуги')).toBeTruthy();
    expect(screen.getByText('Категория услуги')).toBeTruthy();
    expect(screen.getByText('Категория сохраняется у позиции счёта и сразу фильтрует подходящие шаблоны услуги.')).toBeTruthy();
    fireEvent.change(screen.getByTestId('invoice-line-item-lookup-filter-select'), { target: { value: '2' } });
    await waitFor(() => {
      expect(screen.getByText(/Категория: Гражданские дела\./i)).toBeTruthy();
    });

    fireEvent.change(screen.getByPlaceholderText('Поиск наименования'), { target: { value: 'Составл' } });
    fireEvent.click(screen.getByRole('button', { name: /Составление иска/i }));

    await waitFor(() => {
      expect((screen.getByLabelText('Единица измерения') as HTMLInputElement).value).toBe('усл.ед.');
      expect((screen.getByLabelText('Цена') as HTMLInputElement).value).toBe('15 000.00');
      expect(screen.getByRole('button', { name: 'Наименование услуги' }).textContent).toContain('Составление иска');
      expect(screen.getByRole('button', { name: 'Наименование услуги' }).textContent).not.toContain('Гражданские дела');
    });
  });

  it('allows saving a custom service name from the search dialog', async () => {
    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Наименование услуги' }));
    fireEvent.change(screen.getByPlaceholderText('Поиск наименования'), {
      target: { value: 'Нестандартная услуга' },
    });

    fireEvent.click(screen.getByRole('button', { name: /Использовать:\s*Нестандартная услуга/i }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Наименование услуги' }).textContent).toContain('Нестандартная услуга');
    });
  });
  it('lets the user reopen a selected service and append custom text without losing the template fields', async () => {
    const serviceName = 'Claim drafting';
    const customServiceName = 'Claim drafting for case 42';

    invoiceUnitsMock.push({ name: 'pcs' }, { name: 'svc' }, { name: 'hour' });
    serviceCategoriesMock.push(
      { id: 1, name: 'Litigation', description: '', colorHex: '#111827', isActive: true, createdAt: '2026-04-01' },
    );
    servicesMock.push(
      { id: 1, name: serviceName, description: '', unit: 'svc', defaultPrice: 15000, categoryId: 1, colorHex: '#111827', isActive: true, createdAt: '2026-04-01' },
    );

    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click((await screen.findAllByTestId('invoice-line-item-service-trigger'))[0]);
    fireEvent.click(screen.getByTestId('invoice-line-item-lookup-option-1'));

    await waitFor(() => {
      expect(screen.getAllByTestId('invoice-line-item-service-trigger')[0].textContent).toContain(serviceName);
      expect((screen.getAllByTestId('invoice-line-item-unit-input')[0] as HTMLInputElement).value).toBe('svc');
      expect((screen.getAllByTestId('invoice-line-item-price-input')[0] as HTMLInputElement).value).toBe('15 000.00');
    });

    fireEvent.click(screen.getAllByTestId('invoice-line-item-service-trigger')[0]);

    const nameSearchInput = screen.getByTestId('invoice-line-item-lookup-query-input') as HTMLInputElement;
    const categoryFilterSelect = screen.getByTestId('invoice-line-item-lookup-filter-select') as HTMLSelectElement;
    expect(nameSearchInput.value).toBe(serviceName);
    expect(categoryFilterSelect.value).toBe('1');

    fireEvent.change(nameSearchInput, {
      target: { value: customServiceName },
    });
    fireEvent.click(screen.getByTestId('invoice-line-item-lookup-custom-action'));

    await waitFor(() => {
      expect(screen.getAllByTestId('invoice-line-item-service-trigger')[0].textContent).toContain(customServiceName);
      expect((screen.getAllByTestId('invoice-line-item-unit-input')[0] as HTMLInputElement).value).toBe('svc');
      expect((screen.getAllByTestId('invoice-line-item-price-input')[0] as HTMLInputElement).value).toBe('15 000.00');
    });
  });

  it('opens a related payment preview from the page sidebar', async () => {
    const invoice = {
      id: 550,
      invoiceEntityId: 550,
      number: '550',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 18000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 18000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 880,
      invoiceId: 550,
      amount: 6000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Completed',
      description: 'Частичная оплата по счету №550',
      account: '550',
      accountDate: '2026-04-08',
      notes: '',
      systemNotes: '',
    } as unknown as Payment;

    vi.mocked(apiService.getPayment).mockResolvedValue({
      ...relatedPayment,
      notes: 'Платеж поступил по банку',
    } as Payment);

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
        relatedPayments={[relatedPayment]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-payment-link-880'));

    expect(await screen.findByTestId('invoice-related-payment-preview')).toBeTruthy();
    await waitFor(() => {
      expect(apiService.getPayment).toHaveBeenCalledWith(880);
    });
    expect(screen.getByText('Платеж #880')).toBeTruthy();
    expect(screen.getByText('Платеж поступил по банку')).toBeTruthy();
  });

  it('opens the existing payment modal callback for regular related payments when edit mode is available', async () => {
    const onEditRelatedPayment = vi.fn();
    const invoice = {
      id: 553,
      invoiceEntityId: 553,
      number: '553',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 21000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Test service', quantity: 1, unit: 'шт', price: 21000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 883,
      invoiceId: 553,
      documentKind: 'Payment',
      amount: 7000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Completed',
      description: 'Open via existing modal callback',
      account: '553',
      accountDate: '2026-04-08',
      notes: '',
      systemNotes: '',
    } as unknown as Payment;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onEditRelatedPayment={onEditRelatedPayment}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[relatedPayment]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-payment-link-883'));

    expect(onEditRelatedPayment).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 883,
        invoiceId: 553,
      }),
    );
    expect(onEditRelatedPayment).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('invoice-related-payment-preview')).toBeNull();
  });

  it('routes invoice-managed sidebar clicks through the payment edit callback', async () => {
    const onEditRelatedPayment = vi.fn();
    const invoice = {
      id: 554,
      invoiceEntityId: 554,
      number: '554',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 21000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Test service', quantity: 1, unit: 'шт', price: 21000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 884,
      invoiceId: 554,
      documentKind: 'Invoice',
      amount: 7000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Completed',
      description: 'Invoice-managed related payment',
      account: '554',
      accountDate: '2026-04-08',
      notes: '',
      systemNotes: '',
    } as unknown as Payment;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onEditRelatedPayment={onEditRelatedPayment}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[relatedPayment]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-payment-link-884'));

    expect(onEditRelatedPayment).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 884,
        invoiceId: 554,
      }),
    );
    expect(screen.queryByTestId('invoice-related-payment-preview')).toBeNull();
  });

  it('routes invoice-managed edit-page actions through the payment edit callback', async () => {
    const onEditRelatedPayment = vi.fn();
    const invoice = {
      id: 555,
      invoiceEntityId: 555,
      number: '555',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 21000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Test service', quantity: 1, unit: 'шт', price: 21000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 885,
      invoiceId: 555,
      documentKind: 'Payment',
      amount: 7000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Completed',
      description: 'Invoice-managed action button',
      account: '555',
      accountDate: '2026-04-08',
      notes: '',
      systemNotes: '[pp:installment-payment] 2',
    } as unknown as Payment;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onEditRelatedPayment={onEditRelatedPayment}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[relatedPayment]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Редактировать счёт' }));
    fireEvent.click(await screen.findByTestId('invoice-related-payment-edit-885'));

    expect(onEditRelatedPayment).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 885,
        invoiceId: 555,
      }),
    );
    expect(screen.queryByTestId('invoice-related-payment-preview')).toBeNull();
  });

  it('opens the full payment callback from a standalone payment preview', async () => {
    const onOpenPayment = vi.fn();
    const invoice = {
      id: 551,
      invoiceEntityId: 551,
      number: '551',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 21000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Р®СЂРёРґРёС‡РµСЃРєРёРµ СѓСЃР»СѓРіРё', quantity: 1, unit: 'С€С‚', price: 21000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 881,
      invoiceId: undefined,
      amount: 7000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Completed',
      description: 'РџРµСЂРІР°СЏ РѕРїР»Р°С‚Р° РїРѕ СЃС‡РµС‚Сѓ в„–551',
      account: '551',
      accountDate: '2026-04-08',
      notes: '',
      systemNotes: '',
    } as unknown as Payment;

    vi.mocked(apiService.getPayment).mockResolvedValue({
      ...relatedPayment,
      notes: 'РџР»Р°С‚РµР¶ Р·Р°С‡С‚РµРЅ РєР°Рє РѕСЃРЅРѕРІРЅРѕР№',
    } as Payment);

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onOpenPayment={onOpenPayment}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[relatedPayment]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-payment-link-881'));
    expect(await screen.findByTestId('invoice-related-payment-preview')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Открыть полный платеж' }));

    expect(onOpenPayment).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 881,
        notes: 'РџР»Р°С‚РµР¶ Р·Р°С‡С‚РµРЅ РєР°Рє РѕСЃРЅРѕРІРЅРѕР№',
      }),
    );
  });

  it('routes invoice-managed sidebar clicks through the payment edit callback when it is available', async () => {
    const invoice = {
      id: 552,
      invoiceEntityId: 552,
      number: '552',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 21000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Test service', quantity: 1, unit: 'шт', price: 21000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 882,
      invoiceId: 552,
      documentKind: 'Invoice',
      amount: 7000,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Completed',
      description: 'Test invoice-managed payment',
      account: '552',
      accountDate: '2026-04-08',
      notes: '',
      systemNotes: '',
    } as unknown as Payment;

    const onEditRelatedPayment = vi.fn();

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onEditRelatedPayment={onEditRelatedPayment}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[relatedPayment]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-payment-link-882'));
    expect(onEditRelatedPayment).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 882,
        invoiceId: 552,
      }),
    );
    expect(await screen.findByRole('button', { name: 'Сохранить' })).toBeTruthy();
    expect(screen.queryByTestId('invoice-related-payment-preview')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Открыть полный платеж' })).toBeNull();
  });

  it('opens a related contract preview from the page sidebar', async () => {
    const invoice = {
      id: 560,
      invoiceEntityId: 560,
      number: '560',
      date: '2026-04-12',
      dueDate: '2026-05-12',
      amount: 25000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      contractId: 77,
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 25000 }],
    } as unknown as Invoice;

    const contract = {
      id: 77,
      number: 'CT-077',
      title: 'Абонентское обслуживание',
      date: '2026-04-01',
      billingMode: 'Fixed',
      isOpenEnded: false,
      amount: 25000,
      validUntil: '2026-12-31',
      invoiceAmountTotal: 25000,
      actAmountTotal: 0,
      paymentAmountTotal: 0,
      clients: [{ id: 1, name: 'Тестовый клиент', company: null }],
      invoiceIds: [560],
      actIds: [],
      paymentIds: [880],
      createdAt: '2026-04-01T00:00:00Z',
      description: 'Основной договор на обслуживание',
    } as unknown as Contract;

    vi.mocked(apiService.getContracts).mockResolvedValueOnce({ items: [contract] });
    vi.mocked(apiService.getContract).mockResolvedValue(contract);

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-contract-link'));

    expect(await screen.findByTestId('invoice-related-contract-preview')).toBeTruthy();
    await waitFor(() => {
      expect(apiService.getContract).toHaveBeenCalledWith(77);
    });
    expect(screen.getByText('Основной договор на обслуживание')).toBeTruthy();
    expect(screen.getAllByText(/CT-077/).length).toBeGreaterThan(0);
  });

  it('opens the full contract callback from the contract preview', async () => {
    const onOpenContract = vi.fn();
    const invoice = {
      id: 561,
      invoiceEntityId: 561,
      number: '561',
      date: '2026-04-12',
      dueDate: '2026-05-12',
      amount: 25000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      contractId: 77,
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 25000 }],
    } as unknown as Invoice;

    const contract = {
      id: 77,
      number: 'CT-077',
      title: 'Абонентское обслуживание',
      date: '2026-04-01',
      billingMode: 'Fixed',
      isOpenEnded: false,
      amount: 25000,
      validUntil: '2026-12-31',
      invoiceAmountTotal: 25000,
      actAmountTotal: 0,
      paymentAmountTotal: 0,
      clients: [{ id: 1, name: 'Тестовый клиент', company: null }],
      invoiceIds: [561],
      actIds: [],
      paymentIds: [880],
      createdAt: '2026-04-01T00:00:00Z',
      description: 'Основной договор на обслуживание',
    } as unknown as Contract;

    vi.mocked(apiService.getContracts).mockResolvedValueOnce({ items: [contract] });
    vi.mocked(apiService.getContract).mockResolvedValue(contract);

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onOpenContract={onOpenContract}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-contract-link'));
    expect(await screen.findByTestId('invoice-related-contract-preview')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Открыть полный договор' }));

    expect(onOpenContract).toHaveBeenCalledWith(77);
  });

  it('creates a linked act from a saved invoice and reuses its line items', async () => {
    const createActLabel = '\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u0430\u043a\u0442';
    const actNumberLabel = '\u041d\u043e\u043c\u0435\u0440 \u0430\u043a\u0442\u0430';
    const createActMock = vi.mocked(apiService.createAct);
    createActMock.mockResolvedValue({
      id: 901,
      number: 'ACT-901',
      title: null,
      date: '2026-04-10',
      amount: 18_000,
      creationMode: 'Full',
      invoiceNumber: '36',
      invoiceId: 618,
      status: 'Created',
      clientId: 1,
      clientName: 'РўРµСЃС‚РѕРІС‹Р№ РєР»РёРµРЅС‚',
      responsibleId: 9,
      responsibleName: 'РћС‚РІРµС‚СЃС‚РІРµРЅРЅС‹Р№ РјРµРЅРµРґР¶РµСЂ',
      lineItems: [],
      createdAt: '2026-04-10T00:00:00Z',
    } as Act);

    const invoice = {
      id: 618,
      invoiceEntityId: 618,
      number: '36',
      date: '2026-04-10',
      dueDate: '2026-04-20',
      amount: 18000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      contractId: 77,
      responsibles: [{ id: 9, fullName: 'РћС‚РІРµС‚СЃС‚РІРµРЅРЅС‹Р№ РјРµРЅРµРґР¶РµСЂ' }],
      lineItems: [{ name: 'РЎРѕСЃС‚Р°РІР»РµРЅРёРµ РёСЃРєР°', quantity: 1, unit: 'СѓСЃР».РµРґ.', price: 18000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'РўРµСЃС‚РѕРІС‹Р№ РєР»РёРµРЅС‚')]}
        responsibles={[{ id: 9, fullName: 'РћС‚РІРµС‚СЃС‚РІРµРЅРЅС‹Р№ РјРµРЅРµРґР¶РµСЂ' }]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: createActLabel }));
    fireEvent.change(screen.getByLabelText(actNumberLabel), { target: { value: 'ACT-901' } });
    fireEvent.click(screen.getAllByRole('button', { name: createActLabel }).at(-1)!);

      await waitFor(() => {
        expect(createActMock).toHaveBeenCalledWith(expect.objectContaining({
          number: 'ACT-901',
        amount: 0,
        creationMode: 'Full',
        invoiceId: 618,
        invoiceNumber: '36',
        clientId: 1,
        contractId: 77,
        responsibleId: 9,
        status: 'Created',
        }));
      });
    });

    it('routes act creation to a dedicated page and stores the page draft instead of opening an inline modal', async () => {
      const onCreateActPage = vi.fn();

      const invoice = {
        id: 618,
        invoiceEntityId: 618,
        number: '36',
        date: '2026-04-10',
        dueDate: '2026-04-20',
        amount: 18000,
        type: 'Income',
        status: 'Pending',
        clientId: 1,
        contractId: 77,
        description: 'Счет на юридические услуги',
        responsibles: [{ id: 9, fullName: 'Ответственный менеджер' }],
        lineItems: [{ name: 'Составление иска', quantity: 1, unit: 'усл.ед.', price: 18000 }],
      } as unknown as Invoice;

      render(
        <InvoiceModal
          open
          mode="edit"
          presentation="page"
          invoice={invoice}
          onClose={vi.fn()}
          onSubmit={vi.fn().mockResolvedValue(undefined)}
          submitting={false}
          clients={[createTestClient(1, 'Тестовый клиент')]}
          responsibles={[{ id: 9, fullName: 'Ответственный менеджер' }]}
          lookupsLoading={false}
          relatedPayments={[]}
          onCreateActPage={onCreateActPage}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: 'Редактировать счёт' }));
      fireEvent.click(await screen.findByRole('button', { name: 'Создать акт' }));

      expect(screen.queryByLabelText('Номер акта')).toBeNull();
      expect(storeInvoiceEditorDraftMock).toHaveBeenCalledTimes(1);
      expect(storeInvoiceEditorDraftMock.mock.calls[0]?.[0]).toMatchObject({
        invoiceId: 618,
        mode: 'edit',
        presentation: 'page',
      });
      expect(onCreateActPage).toHaveBeenCalledWith(expect.objectContaining({
        number: '36',
        amount: 18000,
        creationMode: 'Full',
        invoiceId: 618,
        invoiceNumber: '36',
        clientId: 1,
        responsibleId: 9,
        status: 'Created',
      }), 618);
    });
  
    it('creates and loads client cases for the expense client on expense invoices', async () => {
      const getCasesMock = vi.mocked(apiService.getCases);
    const createCaseMock = vi.mocked(apiService.createCase);
    const onClientCaseCreated = vi.fn();
    getCasesMock.mockResolvedValue([]);
    createCaseMock.mockResolvedValue({
      id: 441,
      clientId: 2,
      title: 'Case from invoice',
      description: 'Created from invoice',
      status: 'Open',
      createdAt: '2026-04-15T10:00:00Z',
    } as ClientCase);

    const invoice = {
      id: 820,
      invoiceEntityId: 820,
      number: '820',
      date: '2026-04-15',
      dueDate: '2026-04-20',
      amount: 5000,
      type: 'Expense',
      status: 'Pending',
      clientId: 1,
      expenseClientId: 2,
      lineItems: [{ name: 'Expense service', quantity: 1, unit: 'pcs', price: 5000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Client owner'), createTestClient(2, 'Expense supplier')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
        onClientCaseCreated={onClientCaseCreated}
      />,
    );

    await waitFor(() => {
      expect(getCasesMock).toHaveBeenCalledWith({ clientId: 2 });
    });

    fireEvent.click(screen.getByRole('button', { name: 'Создать дело' }));
    fireEvent.click(await screen.findByRole('button', { name: 'submit-case' }));

    await waitFor(() => {
      expect(createCaseMock).toHaveBeenCalledWith(expect.objectContaining({
        clientId: 2,
        title: 'Case from invoice',
        status: 'Open',
      }));
    });

    expect(onClientCaseCreated).toHaveBeenCalledWith(expect.objectContaining({
      id: 441,
      clientId: 2,
      title: 'Case from invoice',
    }));
    expect(await screen.findByText('Case from invoice')).toBeTruthy();
  });

  it('opens the client case modal above the invoice overlay', async () => {
    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        defaultClientId={1}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Создать дело' }));

    expect(await screen.findByTestId('case-modal')).toBeTruthy();
    expect(caseModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({
      isOpen: true,
      overlayClassName: 'z-[2300]',
    }));
  });

  it('opens the client case modal above the invoice overlay in edit mode', async () => {
    const invoice = {
      id: 822,
      invoiceEntityId: 822,
      number: '822',
      date: '2026-04-15',
      dueDate: '2026-04-20',
      amount: 5000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      lineItems: [{ name: 'Услуга', quantity: 1, unit: 'шт', price: 5000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Создать дело' }));

    expect(await screen.findByTestId('case-modal')).toBeTruthy();
    expect(caseModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({
      isOpen: true,
      overlayClassName: 'z-[2300]',
    }));
  });

  it('submits an empty client case list after unlinking the selected case from the invoice', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const invoice = {
      id: 821,
      invoiceEntityId: 821,
      number: '821',
      date: '2026-04-15',
      dueDate: '2026-04-20',
      amount: 5000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      clientCaseId: 441,
      clientCaseIds: [441],
      clientCases: [
        {
          id: 441,
          clientId: 1,
          title: 'Case Alpha',
          status: 'Open',
          createdAt: '2026-04-14T10:00:00Z',
        },
      ],
      lineItems: [{ name: 'Income service', quantity: 1, unit: 'pcs', price: 5000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Client owner')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getAllByRole('button', { name: 'Очистить' })[0]);
    const form = document.getElementById('invoice-edit-form') as HTMLFormElement | null;
    expect(form).toBeTruthy();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
        clientCaseId: null,
        clientCaseIds: [],
      }));
    });
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

    fireEvent.click(screen.getByRole('button', { name: /Скачать/i }));
    fireEvent.click(screen.getByRole('button', { name: /PDF без подписи и печати/i }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(551, 'pdf', { includeSignatureAndSeal: false });
    });
  });

  it('shows the calculated line item amount and keeps invoice total in sync', async () => {
    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.change(screen.getByLabelText('Количество'), { target: { value: '3' } });
    fireEvent.change(screen.getByLabelText('Цена'), { target: { value: '2500' } });
    fireEvent.blur(screen.getByLabelText('Цена'));

    await waitFor(() => {
      expect(screen.getByLabelText('Сумма позиции').textContent).toBe('7 500.00');
      expect(screen.getAllByText(/7 500\.00/).length).toBeGreaterThan(1);
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

    fireEvent.click(screen.getByRole('button', { name: /Печать/i }));

    expect(openMock).toHaveBeenCalled();
    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(552, 'pdf', { includeSignatureAndSeal: false });
    });
  });
});

