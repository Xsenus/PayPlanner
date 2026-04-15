import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { InvoiceModal } from './InvoiceModal';
import type {
  Client,
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

type ContractModalMockProps = {
  open: boolean;
  onSubmit: (payload: ContractInput) => void | Promise<void>;
};

type InstallmentPlannerMockProps = {
  onApplyPlan: (plan: InstallmentPlanInput | null) => void;
};

const {
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
  beforeEach(() => {
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
      lineItems: [{ name: 'РЈСЃР»СѓРіР°', quantity: 1, unit: 'С€С‚', price: 15000 }],
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
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    deferredContracts.resolve({
      items: [{ id: 77, number: 'CT-077', title: 'РћР±СЃР»СѓР¶РёРІР°РЅРёРµ', date: '2026-04-15' }],
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
      title: 'РќРѕРІС‹Р№ РґРѕРіРѕРІРѕСЂ',
      date: '2026-04-15',
      billingMode: 'Fixed',
      isOpenEnded: false,
      amount: 10000,
      createdAt: '2026-04-15T10:00:00Z',
      invoiceAmountTotal: 0,
      actAmountTotal: 0,
      paymentAmountTotal: 0,
      clients: [{ id: 1, name: 'РўРµСЃС‚РѕРІС‹Р№ РєР»РёРµРЅС‚' }],
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
        clients={[createTestClient(1, 'РўРµСЃС‚РѕРІС‹Р№ РєР»РёРµРЅС‚')]}
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
      lineItems: [{ name: 'Р Р°СЃСЃСЂРѕС‡РєР° РїРѕ СЃС‡РµС‚Сѓ', quantity: 1, unit: 'С€С‚', price: 10000 }],
    } as unknown as Invoice;

    const firstInstallment = {
      id: 611,
      invoiceId: 27,
      amount: 5600,
      date: '2026-05-10',
      plannedDate: '2026-05-10',
      type: 'Income',
      status: 'Pending',
      description: 'Р Р°СЃСЃСЂРѕС‡РєР° РїРѕ СЃС‡РµС‚Сѓ в„–27 В· РїР»Р°С‚РµР¶ 1/2',
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
      description: 'Р Р°СЃСЃСЂРѕС‡РєР° РїРѕ СЃС‡РµС‚Сѓ в„–27 В· РїР»Р°С‚РµР¶ 2/2',
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
      lineItems: [{ name: 'Р Р°СЃСЃСЂРѕС‡РєР° РїРѕ СЃС‡РµС‚Сѓ', quantity: 1, unit: 'С€С‚', price: 10000 }],
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
      lineItems: [{ name: 'Р В Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р В° Р С—Р С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“', quantity: 1, unit: 'РЎв‚¬РЎвЂљ', price: 10000 }],
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

  it('filters services by selected category and applies the matching template', async () => {
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

    const categorySelect = await screen.findByLabelText('Категория услуги');
    fireEvent.change(categorySelect, { target: { value: '2' } });

    const nameInput = screen.getByLabelText('Наименование услуги') as HTMLInputElement;
    const datalistId = nameInput.getAttribute('list');
    expect(datalistId).toBeTruthy();

    const datalist = document.getElementById(datalistId!);
    expect(datalist).toBeTruthy();

    const optionValues = Array.from(datalist!.querySelectorAll('option')).map((option) => option.getAttribute('value'));
    expect(optionValues).toEqual(['Консультация', 'Составление иска']);

    fireEvent.change(nameInput, { target: { value: 'Консультация' } });
    fireEvent.blur(nameInput);

    await waitFor(() => {
      expect((screen.getByLabelText('Единица измерения') as HTMLInputElement).value).toBe('усл.ед.');
      expect((screen.getByLabelText('Цена') as HTMLInputElement).value).toBe('8 000.00');
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
