import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
  LegalEntitySummary,
  MoneyOperation,
  MoneyOperationsSummary,
  Payment,
  ServiceCategoryDictionaryItem,
  ServiceDictionaryItem,
} from '../../types';
import type { BaseDictItem } from '../../types/dictionaries';
import { apiService, type PagedResult } from '../../services/api';
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

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => 'Europe/Samara',
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 7, role: { id: 1 } },
  }),
}));

vi.mock('../../hooks/useInvoiceReferenceDictionaries', () => ({
  useInvoiceReferenceDictionaries: () => ({
    invoiceUnits: invoiceUnitsMock,
    services: servicesMock,
    serviceCategories: serviceCategoriesMock,
    loading: false,
    error: null,
    refresh: vi.fn(),
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
    getClientsV2: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 }),
    getActs: vi.fn().mockResolvedValue({ items: [] }),
    getLegalEntities: vi.fn().mockResolvedValue([]),
    getLegalEntitiesPaged: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 }),
    getClientBalance: vi.fn().mockResolvedValue({ clientId: 1, balance: 0 }),
    getLegalEntityBalance: vi.fn().mockResolvedValue({ clientId: null, legalEntityId: 1, balance: 0 }),
    getNextInvoiceNumber: vi.fn().mockResolvedValue('24'),
    getPayment: vi.fn(),
    getMoneyOperationsPaged: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20, summary: createEmptyMoneyOperationsSummary() }),
    createContract: vi.fn(),
    createCase: vi.fn(),
    createAct: vi.fn(),
    updateInvoice: vi.fn(),
    createInvoice: vi.fn(),
    cancelInvoice: vi.fn(),
    createClient: vi.fn(),
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
    email: '',
    phone: '',
    company: null,
    address: '',
    notes: '',
    isActive: true,
    createdAt: '2026-04-15T00:00:00Z',
  } as unknown as Client;
}

function createEmptyMoneyOperationsSummary(): MoneyOperationsSummary {
  return {
    startBalance: 0,
    income: 0,
    expense: 0,
    endBalance: 0,
    incomeCount: 0,
    expenseCount: 0,
    bySource: [],
  };
}

function createPagedResult<T>(items: T[], total = items.length): PagedResult<T> {
  return { items, total, page: 1, pageSize: 25 };
}

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

  throw new Error(`Не найден вариант выбора ${name}`);
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
        focus: vi.fn(),
        print: vi.fn(),
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
    vi.mocked(apiService.getClientsV2).mockReset();
    vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 });
    vi.mocked(apiService.createCase).mockReset();
    vi.mocked(apiService.createAct).mockReset();
    vi.mocked(apiService.getContract).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 });
    vi.mocked(apiService.getLegalEntities).mockReset();
    vi.mocked(apiService.getLegalEntities).mockResolvedValue([]);
    vi.mocked(apiService.getClientBalance).mockReset();
    vi.mocked(apiService.getClientBalance).mockResolvedValue({ clientId: 1, balance: 0 });
    vi.mocked(apiService.getLegalEntityBalance).mockReset();
    vi.mocked(apiService.getLegalEntityBalance).mockResolvedValue({ clientId: null, legalEntityId: 1, balance: 0 });
    vi.mocked(apiService.getPayment).mockReset();
    vi.mocked(apiService.getMoneyOperationsPaged).mockReset();
    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20, summary: createEmptyMoneyOperationsSummary() });
    vi.mocked(apiService.updateInvoice).mockClear();
    vi.mocked(apiService.createInvoice).mockReset();
    vi.mocked(apiService.cancelInvoice).mockReset();
    vi.mocked(apiService.createClient).mockReset();
  });

  it('uses the profile time zone for a new invoice default date near midnight', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-04T22:30:00.000Z'));

    try {
      const { container } = render(
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

      const dateInput = container.querySelector('input[type="date"]') as HTMLInputElement | null;
      expect(dateInput?.value).toBe('2026-06-05');
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows available client balance in the invoice form', async () => {
    vi.mocked(apiService.getClientBalance).mockResolvedValue({ clientId: 69, balance: 7_100 });

    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(69, 'Клиент с авансом')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
        defaultClientId={69}
        defaultType="Income"
      />,
    );

    expect(await screen.findByText('Доступно на балансе клиента')).toBeTruthy();
    expect(await screen.findByText('7 100.00')).toBeTruthy();
    expect(apiService.getClientBalance).toHaveBeenCalledWith(69);
  });

  it('keeps the linked contract selected after invoice data is reopened and contracts finish loading', async () => {
    const deferredContracts = createDeferred<PagedResult<Contract>>();
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

    deferredContracts.resolve(createPagedResult([
      { id: 77, number: 'CT-077', title: 'Обслуживание', date: '2026-04-15' } as Contract,
    ]));

    await waitFor(() => {
      expect(getContractSelect().value).toBe('77');
    });
  });

  it('selects a legal entity directly and sends it as the invoice counterparty', async () => {
    const legalEntity: LegalEntitySummary = {
      id: 55,
      shortName: 'ООО Ромашка',
      fullName: 'Общество с ограниченной ответственностью Ромашка',
      inn: '7701000000',
      kpp: '770101001',
      ogrn: null,
      address: 'Москва',
      phone: '+7 999 000-00-00',
      email: 'info@romashka.test',
      clientsCount: 0,
      createdAt: '2026-04-15T00:00:00Z',
      clients: [],
    } as LegalEntitySummary;
    const onClientCreated = vi.fn();
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    vi.mocked(apiService.getLegalEntities).mockResolvedValue([legalEntity]);
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValue(createPagedResult([legalEntity]));

    const invoice = {
      id: 701,
      invoiceEntityId: 701,
      number: '701',
      date: '2026-04-15',
      amount: 15000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      lineItems: [{ name: 'Услуга', quantity: 1, unit: 'шт', price: 15000 }],
    } as unknown as Invoice;
    vi.mocked(apiService.getLegalEntitiesPaged).mockResolvedValueOnce({
      items: [legalEntity],
      total: 1,
      page: 1,
      pageSize: 25,
    });

    const { container } = render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Старый клиент')]}
        legalEntities={[legalEntity]}
        onClientCreated={onClientCreated}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /открыть выбор: клиент/i }));
    fireEvent.click(await screen.findByRole('button', { name: 'Юридические лица' }));
    await selectPickerOption('ООО Ромашка');

    await waitFor(() => {
      expect(apiService.createClient).not.toHaveBeenCalled();
    });
    expect(onClientCreated).not.toHaveBeenCalled();

    const form = container.querySelector('form');
    expect(form).toBeTruthy();
    fireEvent.submit(form as HTMLFormElement);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ clientId: null, legalEntityId: 55 }));
    });
  });

  it('preserves a contract created from the invoice even when an older contracts request resolves afterwards', async () => {
    const deferredContracts = createDeferred<PagedResult<Contract>>();
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

    deferredContracts.resolve(createPagedResult<Contract>([]));

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
      plannedDate: '2025-11-24',
      paidDate: '2026-04-03',
      type: 'Income',
      status: 'Completed',
      description: 'Оплата по счету №24',
      account: '24',
      accountDate: '2026-04-02',
      systemNotes: 'Зачтено с баланса клиента: 750,00',
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
    expect(screen.getByText('Зачтено с баланса: 750 ₽')).toBeTruthy();
    expect(screen.getByText('Ожидаемая дата: 02.04.2026')).toBeTruthy();
    expect(screen.queryByText('Ожидаемая дата: 24.11.2025')).toBeNull();
    expect(screen.getByText('Фактическая дата: 03.04.2026')).toBeTruthy();
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

  it('asks how to reconcile payments before saving an invoice amount mismatch', async () => {
    const invoice = {
      id: 531,
      invoiceEntityId: 31,
      number: '31',
      date: '2026-04-02',
      dueDate: '2026-04-03',
      amount: 45000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 45000 }],
    } as unknown as Invoice;

    const relatedPayments = [
      {
        id: 532,
        invoiceId: 31,
        amount: 15000,
        outstandingAmount: 15000,
        date: '2026-04-10',
        plannedDate: '2026-04-10',
        type: 'Income',
        status: 'Pending',
        description: 'Первый платеж по счету №31',
        account: '31',
        accountDate: '2026-04-02',
      },
      {
        id: 533,
        invoiceId: 31,
        amount: 15000,
        outstandingAmount: 15000,
        date: '2026-05-10',
        plannedDate: '2026-05-10',
        type: 'Income',
        status: 'Pending',
        description: 'Второй платеж по счету №31',
        account: '31',
        accountDate: '2026-04-02',
      },
    ] as unknown as Payment[];
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    const { container } = render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={relatedPayments}
      />,
    );

    fireEvent.submit(container.querySelector('form') as HTMLFormElement);

    expect(await screen.findByText('Сверка суммы счета и платежей')).toBeTruthy();
    expect(onSubmit).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить только счет' }));

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ amount: 45000 }));
    });
  });

  it('loads and renders linked money operations for the invoice', async () => {
    const invoice = {
      id: 540,
      invoiceEntityId: 33,
      number: '33',
      date: '2026-04-29',
      dueDate: '2026-04-30',
      amount: 25000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 25000 }],
    } as unknown as Invoice;

    const linkedOperation = {
      id: 9001,
      source: 'Bank',
      direction: 'Income',
      accountingType: 'Fact',
      accountName: 'ОСНОВНОЙ Банк',
      counterpartyName: 'Ахмедова Айтан Рафик Кызы',
      amount: 25000,
      currency: 'RUB',
      date: '2026-04-29',
      isAdvancePayment: false,
      purpose: 'Оплата по счёту №33',
      linkedInvoiceId: 33,
      linkedInvoiceNumber: '33',
      matchStatus: 'Matched',
      createdAt: '2026-04-29T10:00:00Z',
    } as MoneyOperation;

    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce({
      items: [linkedOperation],
      total: 1,
      page: 1,
      pageSize: 20,
      summary: createEmptyMoneyOperationsSummary(),
    });

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

    await screen.findByText('Движения денег');

    await waitFor(() => {
      expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledWith({ linkedInvoiceId: 33, page: 1, pageSize: 20 });
    });

    expect(await screen.findByText('Оплата по счёту №33')).toBeTruthy();
    expect(screen.getByText('Банк')).toBeTruthy();
    expect(screen.getByText('Сверено')).toBeTruthy();
    expect(screen.getByText('ОСНОВНОЙ Банк')).toBeTruthy();
  });

  it('hides the money operations section when there are no linked operations', async () => {
    const invoice = {
      id: 1850,
      invoiceEntityId: 1850,
      number: '52',
      date: '2026-05-03',
      dueDate: '2026-05-15',
      amount: 37000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Анализ документов', quantity: 1, unit: 'усл.ед.', price: 37000 }],
    } as unknown as Invoice;

    vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce({
      items: [],
      total: 0,
      page: 1,
      pageSize: 20,
      summary: createEmptyMoneyOperationsSummary(),
    });

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

    await waitFor(() => {
      expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledWith({ linkedInvoiceId: 1850, page: 1, pageSize: 20 });
    });

    await waitFor(() => {
      expect(screen.queryByText('Движения денег')).toBeNull();
    });
    expect(screen.queryByText('Связанные движения денег пока не найдены.')).toBeNull();
  });

  it('hides the money operations section when linked operations fail to load', async () => {
    const invoice = {
      id: 1849,
      invoiceEntityId: 1849,
      number: '51',
      date: '2026-05-03',
      dueDate: '2026-05-15',
      amount: 37000,
      type: 'Income',
      status: 'Pending',
      lineItems: [{ name: 'Анализ документов', quantity: 1, unit: 'усл.ед.', price: 37000 }],
    } as unknown as Invoice;

    vi.mocked(apiService.getMoneyOperationsPaged).mockRejectedValueOnce(new Error('Failed to fetch'));

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

    await waitFor(() => {
      expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledWith({ linkedInvoiceId: 1849, page: 1, pageSize: 20 });
    });

    await waitFor(() => {
      expect(screen.queryByText('Движения денег')).toBeNull();
    });
    expect(screen.queryByText('Не удалось загрузить движения денег по счёту.')).toBeNull();
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
    expect(screen.getByText('Ожидаемая дата: 10.05.2026')).toBeTruthy();
    expect(screen.queryByText('Фактическая дата: —')).toBeNull();
    expect(screen.getAllByText(/11\s*200/).length).toBeGreaterThan(0);
  });

  it('cancels only unpaid installment payments from the cancellation modal without browser prompts', async () => {
    const invoice = {
      id: 1844,
      invoiceEntityId: 49,
      number: '49',
      date: '2026-04-29',
      dueDate: '2026-06-29',
      amount: 12000,
      paidAmount: 6000,
      outstandingAmount: 6000,
      type: 'Income',
      status: 'Processing',
      workflowStatus: 'PartiallyPaid',
      clientId: 1,
      createdAt: '2026-04-29T00:00:00Z',
      lineItems: [{ name: 'Рассрочка', quantity: 1, unit: 'шт', price: 12000 }],
    } as unknown as Invoice;

    const paidInstallment = {
      id: 701,
      invoiceId: 49,
      amount: 6000,
      paidAmount: 6000,
      outstandingAmount: 0,
      date: '2026-05-29',
      plannedDate: '2026-05-29',
      paidDate: '2026-05-29',
      lastPaymentDate: '2026-05-29',
      type: 'Income',
      status: 'Completed',
      isPaid: true,
      description: 'Рассрочка по счету №49 · платеж 1/2',
      account: '49',
      accountDate: '2026-04-29',
      systemNotes: '[pp:installment-payment] {"sequence":1,"total":12000,"item":{"sequence":1}}',
    } as unknown as Payment;

    const unpaidInstallment = {
      id: 702,
      invoiceId: 49,
      amount: 6000,
      paidAmount: 0,
      outstandingAmount: 6000,
      date: '2026-06-29',
      plannedDate: '2026-06-29',
      type: 'Income',
      status: 'Pending',
      isPaid: false,
      description: 'Рассрочка по счету №49 · платеж 2/2',
      account: '49',
      accountDate: '2026-04-29',
      systemNotes: '[pp:installment-payment] {"sequence":2,"total":12000,"item":{"sequence":2}}',
    } as unknown as Payment;

    vi.mocked(apiService.cancelInvoice).mockResolvedValue({
      ...invoice,
      status: 'Processing',
      workflowStatus: 'PartiallyPaid',
      paidAmount: 6000,
      outstandingAmount: 0,
    } as Invoice);
    const promptSpy = vi.spyOn(window, 'prompt').mockReturnValue('legacy prompt');

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Клиент рассрочки')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[paidInstallment, unpaidInstallment]}
        onRefreshRequested={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Отмен[её]н/i }));

    expect(await screen.findByTestId('invoice-cancel-dialog')).toBeTruthy();
    expect(promptSpy).not.toHaveBeenCalled();

    fireEvent.change(screen.getByTestId('invoice-cancel-reason'), {
      target: { value: 'Клиент отказался от остатка' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-submit'));

    await waitFor(() => {
      expect(apiService.cancelInvoice).toHaveBeenCalledWith(
        1844,
        'Клиент отказался от остатка',
        'Reject',
        null,
        null,
        null,
        'UnpaidInstallmentsOnly',
      );
    });
  });

  it('submits the advance scenario for a paid invoice from the cancellation modal', async () => {
    const invoice = {
      id: 1901,
      invoiceEntityId: 77,
      number: '77',
      date: '2026-05-01',
      dueDate: '2026-05-01',
      amount: 12000,
      paidAmount: 12000,
      outstandingAmount: 0,
      type: 'Income',
      status: 'Completed',
      workflowStatus: 'Paid',
      clientId: 1,
      createdAt: '2026-05-01T00:00:00Z',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 12000 }],
    } as unknown as Invoice;

    vi.mocked(apiService.cancelInvoice).mockResolvedValue({
      ...invoice,
      status: 'Cancelled',
      workflowStatus: 'Cancelled',
      paidAmount: 0,
      outstandingAmount: 0,
    } as Invoice);

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Клиент с оплатой')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Отмен[её]н/i }));

    expect(await screen.findByTestId('invoice-cancel-dialog')).toBeTruthy();
    expect(screen.getByText('Оставить авансом контрагента')).toBeTruthy();

    fireEvent.change(screen.getByTestId('invoice-cancel-reason'), {
      target: { value: 'Счет выставлен ошибочно' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-submit'));

    await waitFor(() => {
      expect(apiService.cancelInvoice).toHaveBeenCalledWith(
        1901,
        'Счет выставлен ошибочно',
        'LeaveAsAdvance',
        null,
        null,
        null,
        'All',
      );
    });
  });

  it('validates and submits the reallocation scenario from the cancellation modal', async () => {
    const invoice = {
      id: 1902,
      invoiceEntityId: 78,
      number: '78',
      date: '2026-05-02',
      dueDate: '2026-05-02',
      amount: 15000,
      paidAmount: 15000,
      outstandingAmount: 0,
      type: 'Income',
      status: 'Completed',
      workflowStatus: 'Paid',
      clientId: 1,
      createdAt: '2026-05-02T00:00:00Z',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 15000 }],
    } as unknown as Invoice;

    vi.mocked(apiService.cancelInvoice).mockResolvedValue({
      ...invoice,
      status: 'Cancelled',
      workflowStatus: 'Cancelled',
      paidAmount: 0,
      outstandingAmount: 0,
    } as Invoice);

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Клиент для зачета')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Отмен[её]н/i }));
    expect(await screen.findByTestId('invoice-cancel-dialog')).toBeTruthy();

    fireEvent.change(screen.getByTestId('invoice-cancel-reason'), {
      target: { value: 'Перевыставили счет' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-money-reallocate'));
    fireEvent.click(screen.getByTestId('invoice-cancel-submit'));

    expect(await screen.findByText('Для зачёта укажите положительный ID счёта.')).toBeTruthy();
    expect(apiService.cancelInvoice).not.toHaveBeenCalled();

    fireEvent.change(screen.getByTestId('invoice-cancel-target-payment-id'), {
      target: { value: '2005' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-submit'));

    await waitFor(() => {
      expect(apiService.cancelInvoice).toHaveBeenCalledWith(
        1902,
        'Перевыставили счет',
        'Reallocate',
        2005,
        null,
        null,
        'All',
      );
    });
  });

  it('submits the refund scenario with source and date from the cancellation modal', async () => {
    const invoice = {
      id: 1903,
      invoiceEntityId: 79,
      number: '79',
      date: '2026-05-03',
      dueDate: '2026-05-03',
      amount: 18000,
      paidAmount: 18000,
      outstandingAmount: 0,
      type: 'Income',
      status: 'Completed',
      workflowStatus: 'Paid',
      clientId: 1,
      createdAt: '2026-05-03T00:00:00Z',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 18000 }],
    } as unknown as Invoice;

    vi.mocked(apiService.cancelInvoice).mockResolvedValue({
      ...invoice,
      status: 'Cancelled',
      workflowStatus: 'Cancelled',
      paidAmount: 0,
      outstandingAmount: 0,
    } as Invoice);

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Клиент для возврата')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Отмен[её]н/i }));
    expect(await screen.findByTestId('invoice-cancel-dialog')).toBeTruthy();

    fireEvent.change(screen.getByTestId('invoice-cancel-reason'), {
      target: { value: 'Возвращаем оплату' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-money-refund'));
    fireEvent.change(screen.getByTestId('invoice-cancel-refund-source'), {
      target: { value: 'Cash' },
    });
    fireEvent.change(screen.getByTestId('invoice-cancel-refund-date'), {
      target: { value: '2026-05-09' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-submit'));

    await waitFor(() => {
      expect(apiService.cancelInvoice).toHaveBeenCalledWith(
        1903,
        'Возвращаем оплату',
        'Refund',
        null,
        'Cash',
        '2026-05-09',
        'All',
      );
    });
  });

  it('ignores stale reallocation data after switching the cancellation wizard back to unpaid installments only', async () => {
    const invoice = {
      id: 1904,
      invoiceEntityId: 80,
      number: '80',
      date: '2026-05-04',
      dueDate: '2026-07-04',
      amount: 20000,
      paidAmount: 10000,
      outstandingAmount: 10000,
      type: 'Income',
      status: 'Processing',
      workflowStatus: 'PartiallyPaid',
      clientId: 1,
      createdAt: '2026-05-04T00:00:00Z',
      lineItems: [{ name: 'Рассрочка', quantity: 1, unit: 'шт', price: 20000 }],
    } as unknown as Invoice;

    const paidInstallment = {
      id: 801,
      invoiceId: 80,
      amount: 10000,
      paidAmount: 10000,
      outstandingAmount: 0,
      date: '2026-06-04',
      plannedDate: '2026-06-04',
      paidDate: '2026-06-04',
      type: 'Income',
      status: 'Completed',
      isPaid: true,
      description: 'Рассрочка по счету №80 · платеж 1/2',
      account: '80',
      accountDate: '2026-05-04',
      systemNotes: '[pp:installment-payment] {"sequence":1,"total":20000,"item":{"sequence":1}}',
    } as unknown as Payment;

    const unpaidInstallment = {
      id: 802,
      invoiceId: 80,
      amount: 10000,
      paidAmount: 0,
      outstandingAmount: 10000,
      date: '2026-07-04',
      plannedDate: '2026-07-04',
      type: 'Income',
      status: 'Pending',
      isPaid: false,
      description: 'Рассрочка по счету №80 · платеж 2/2',
      account: '80',
      accountDate: '2026-05-04',
      systemNotes: '[pp:installment-payment] {"sequence":2,"total":20000,"item":{"sequence":2}}',
    } as unknown as Payment;

    vi.mocked(apiService.cancelInvoice).mockResolvedValue({
      ...invoice,
      status: 'Processing',
      workflowStatus: 'PartiallyPaid',
      paidAmount: 10000,
      outstandingAmount: 0,
    } as Invoice);

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Клиент с частичной оплатой')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[paidInstallment, unpaidInstallment]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Отмен[её]н/i }));
    expect(await screen.findByTestId('invoice-cancel-dialog')).toBeTruthy();

    fireEvent.change(screen.getByTestId('invoice-cancel-reason'), {
      target: { value: 'Отменяем только остаток рассрочки' },
    });
    fireEvent.click(screen.getByLabelText(/Весь счёт и все платежи/i));
    fireEvent.click(screen.getByTestId('invoice-cancel-money-reallocate'));
    fireEvent.change(screen.getByTestId('invoice-cancel-target-payment-id'), {
      target: { value: '2010' },
    });
    fireEvent.click(screen.getByLabelText(/Только неоплаченные платежи графика/i));

    expect(screen.queryByText('Что сделать с полученной оплатой')).toBeNull();
    fireEvent.click(screen.getByTestId('invoice-cancel-submit'));

    await waitFor(() => {
      expect(apiService.cancelInvoice).toHaveBeenCalledWith(
        1904,
        'Отменяем только остаток рассрочки',
        'Reject',
        null,
        null,
        null,
        'UnpaidInstallmentsOnly',
      );
    });
  });

  it('does not send a reallocation target after switching the paid invoice cancellation to refund', async () => {
    const invoice = {
      id: 1905,
      invoiceEntityId: 81,
      number: '81',
      date: '2026-05-05',
      dueDate: '2026-05-05',
      amount: 22000,
      paidAmount: 22000,
      outstandingAmount: 0,
      type: 'Income',
      status: 'Completed',
      workflowStatus: 'Paid',
      clientId: 1,
      createdAt: '2026-05-05T00:00:00Z',
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 22000 }],
    } as unknown as Invoice;

    vi.mocked(apiService.cancelInvoice).mockResolvedValue({
      ...invoice,
      status: 'Cancelled',
      workflowStatus: 'Cancelled',
      paidAmount: 0,
      outstandingAmount: 0,
    } as Invoice);

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Клиент со сменой возврата')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Отмен[её]н/i }));
    expect(await screen.findByTestId('invoice-cancel-dialog')).toBeTruthy();

    fireEvent.change(screen.getByTestId('invoice-cancel-reason'), {
      target: { value: 'Меняем зачет на возврат' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-money-reallocate'));
    fireEvent.change(screen.getByTestId('invoice-cancel-target-payment-id'), {
      target: { value: '2020' },
    });
    fireEvent.click(screen.getByTestId('invoice-cancel-money-refund'));
    fireEvent.change(screen.getByTestId('invoice-cancel-refund-source'), {
      target: { value: 'Card' },
    });
    fireEvent.change(screen.getByTestId('invoice-cancel-refund-date'), {
      target: { value: '2026-05-12' },
    });

    expect(screen.queryByTestId('invoice-cancel-target-payment-id')).toBeNull();
    fireEvent.click(screen.getByTestId('invoice-cancel-submit'));

    await waitFor(() => {
      expect(apiService.cancelInvoice).toHaveBeenCalledWith(
        1905,
        'Меняем зачет на возврат',
        'Refund',
        null,
        'Card',
        '2026-05-12',
        'All',
      );
    });
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

    const dateInputs = document.querySelectorAll('input[type="date"]') as NodeListOf<HTMLInputElement>;
    expect(dateInputs[1]?.disabled).toBe(true);
    expect(screen.getByText((content) => content.includes('корректировка срока оплаты'))).toBeTruthy();

    const saveButton = document.querySelector('button[type="submit"]') as HTMLButtonElement | null;
    expect(saveButton).toBeTruthy();
    fireEvent.click(saveButton!);

    await waitFor(() => {
      expect(confirmMock).toHaveBeenCalled();
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ dueDate: '2026-06-10' }));
    });
  });

  it('moves due date together with invoice date while the due date was not changed manually', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const invoice = {
      id: 532,
      invoiceEntityId: 532,
      number: '80',
      date: '2026-06-09',
      dueDate: '2026-06-09',
      amount: 21000,
      type: 'Income',
      status: 'Overdue',
      clientId: 1,
      lineItems: [{ name: 'Service', quantity: 1, unit: 'pcs', price: 21000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Bannova Client')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[];
    expect(dateInputs[0]?.value).toBe('2026-06-09');
    expect(dateInputs[1]?.value).toBe('2026-06-09');

    fireEvent.change(dateInputs[0], { target: { value: '2026-06-17' } });

    await waitFor(() => {
      expect(dateInputs[1]?.value).toBe('2026-06-17');
    });

    fireEvent.submit(document.getElementById('invoice-edit-form') as HTMLFormElement);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
        date: '2026-06-17',
        dueDate: '2026-06-17',
      }));
    });
  });

  it('keeps a manually selected due date when invoice date changes', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const invoice = {
      id: 533,
      invoiceEntityId: 533,
      number: '81',
      date: '2026-06-09',
      dueDate: '2026-06-25',
      amount: 21000,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      lineItems: [{ name: 'Service', quantity: 1, unit: 'pcs', price: 21000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Bannova Client')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[];
    expect(dateInputs[0]?.value).toBe('2026-06-09');
    expect(dateInputs[1]?.value).toBe('2026-06-25');

    fireEvent.change(dateInputs[0], { target: { value: '2026-06-17' } });

    await waitFor(() => {
      expect(dateInputs[1]?.value).toBe('2026-06-25');
    });

    fireEvent.submit(document.getElementById('invoice-edit-form') as HTMLFormElement);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
        date: '2026-06-17',
        dueDate: '2026-06-25',
      }));
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

  it('allows applying an installment plan to an expense invoice', async () => {
    installmentPlannerPlanRef.current = {
      total: 12000,
      downPayment: 0,
      annualRate: 0,
      months: 3,
      startDate: '2026-05-10',
      roundingMode: 'none',
      roundingStep: null,
      toPay: 12000,
      overpay: 0,
      loanAmount: 12000,
      baseMonthlyPayment: 4000,
      roundedMonthlyPayment: 4000,
      items: [
        { sequence: 1, date: '2026-05-10', principal: 4000, interest: 0, payment: 4000, balance: 8000 },
        { sequence: 2, date: '2026-06-10', principal: 4000, interest: 0, payment: 4000, balance: 4000 },
        { sequence: 3, date: '2026-07-10', principal: 4000, interest: 0, payment: 4000, balance: 0 },
      ],
    };

    const invoice = {
      id: 534,
      invoiceEntityId: 30,
      number: '30',
      date: '2026-05-10',
      dueDate: '2026-05-10',
      amount: 12000,
      type: 'Expense',
      status: 'Pending',
      clientId: 1,
      expenseClientId: 2,
      lineItems: [{ name: 'Expense service', quantity: 1, unit: 'pcs', price: 12000 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Supplier'), createTestClient(2, 'Client owner')]}
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
        534,
        expect.objectContaining({
          type: 'Expense',
          expenseClientId: 2,
          dueDate: '2026-07-10',
          replaceInstallmentPlan: true,
          installmentPlan: expect.objectContaining({
            months: 3,
            items: expect.arrayContaining([
              expect.objectContaining({ sequence: 1 }),
              expect.objectContaining({ sequence: 2 }),
              expect.objectContaining({ sequence: 3 }),
            ]),
          }),
        }),
      );
    });
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
    invoiceUnitsMock.push({ id: 1, name: 'шт' }, { id: 2, name: 'усл.ед.' }, { id: 3, name: 'час' });
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

    invoiceUnitsMock.push({ id: 1, name: 'pcs' }, { id: 2, name: 'svc' }, { id: 3, name: 'hour' });
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

  it('shows paid and debt amounts in the invoice page summary for a partially paid invoice', async () => {
    const invoice = {
      id: 570,
      invoiceEntityId: 570,
      number: '570',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 25000,
      type: 'Income',
      status: 'Processing',
      lineItems: [{ name: 'Test service', quantity: 1, unit: 'pcs', price: 25000 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 890,
      invoiceId: 570,
      amount: 25000,
      paidAmount: 8000,
      outstandingAmount: 17000,
      hasPartialPayment: true,
      date: '2026-04-10',
      plannedDate: '2026-04-10',
      paidDate: '2026-04-11',
      type: 'Income',
      status: 'Processing',
      description: 'Partial payment',
      account: '570',
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
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[]}
        responsibles={[]}
        lookupsLoading={false}
        presentation="page"
        relatedPayments={[relatedPayment]}
      />,
    );

    expect(await screen.findByTestId('invoice-page-payment-summary')).toBeTruthy();
    expect(screen.getByTestId('invoice-page-paid-amount').textContent).toMatch(/8\s000/);
    expect(screen.getByTestId('invoice-page-debt-amount').textContent).toMatch(/17\s000/);
  });

  it('shows unpaid status for a processing invoice without paid amount', async () => {
    const invoice = {
      id: 572,
      invoiceEntityId: 572,
      number: '572',
      date: '2026-04-29',
      dueDate: '2026-05-08',
      amount: 2500,
      paidAmount: 0,
      outstandingAmount: 2500,
      type: 'Expense',
      status: 'Processing',
      lineItems: [{ name: 'Expense service', quantity: 1, unit: 'pcs', price: 2500 }],
    } as unknown as Invoice;

    const relatedPayment = {
      id: 892,
      invoiceId: 572,
      documentKind: 'Invoice',
      amount: 2500,
      paidAmount: 0,
      outstandingAmount: 2500,
      hasPartialPayment: false,
      date: '2026-05-08',
      plannedDate: '2026-05-08',
      type: 'Expense',
      status: 'Processing',
      description: '',
      account: '572',
      accountDate: '2026-04-29',
      notes: '',
      systemNotes: '',
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
        presentation="page"
        relatedPayments={[relatedPayment]}
      />,
    );

    expect((await screen.findByTestId('invoice-page-status-badge')).textContent).toContain('Не оплачен');
    expect(screen.getByTestId('invoice-page-paid-amount').textContent).toContain('0');
    expect(screen.getByTestId('invoice-page-debt-amount').textContent).toMatch(/2\s500/);
  });

  it('keeps the invoice document workflow status visible next to the payment status', async () => {
    const invoice = {
      id: 573,
      invoiceEntityId: 573,
      number: '573',
      date: '2026-04-29',
      dueDate: '2099-06-18',
      amount: 2500,
      paidAmount: 0,
      outstandingAmount: 2500,
      type: 'Income',
      status: 'Processing',
      workflowStatus: 'SignedByClient',
      lineItems: [{ name: 'Service', quantity: 1, unit: 'pcs', price: 2500 }],
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

    expect((await screen.findByTestId('invoice-page-status-badge')).textContent).toContain('Не оплачен');
    expect(screen.getByTestId('invoice-page-workflow-status-badge').textContent).toContain('Подписан клиентом');
    expect(screen.getByTestId('invoice-header-workflow-status-badge').textContent).toContain('Подписан клиентом');
  });

  it('shows full payment and zero debt in the invoice page summary for a completed invoice without related payments', async () => {
    const invoice = {
      id: 571,
      invoiceEntityId: 571,
      number: '571',
      date: '2026-04-08',
      dueDate: '2026-05-07',
      amount: 18000,
      type: 'Income',
      status: 'Completed',
      lineItems: [{ name: 'Test service', quantity: 1, unit: 'pcs', price: 18000 }],
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

    expect(await screen.findByTestId('invoice-page-payment-summary')).toBeTruthy();
    expect(screen.getByTestId('invoice-page-paid-amount').textContent).toMatch(/18\s000/);
    expect(screen.getByTestId('invoice-page-debt-amount').textContent).toContain('0');
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

    expect(await screen.findByText('Печатный шаблон счета')).toBeTruthy();
    expect(screen.queryByText('Основные параметры')).toBeNull();

    fireEvent.click(await screen.findByTestId('invoice-related-payment-link-883'));

    expect(onEditRelatedPayment).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 883,
        invoiceId: 553,
      }),
    );
    expect(onEditRelatedPayment).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('invoice-related-payment-preview')).toBeNull();
    expect(screen.getByText('Печатный шаблон счета')).toBeTruthy();
    expect(screen.queryByText('Основные параметры')).toBeNull();
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
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 21000 }],
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
      description: 'Первая оплата по счету №551',
      account: '551',
      accountDate: '2026-04-08',
      notes: '',
      systemNotes: '',
    } as unknown as Payment;

    vi.mocked(apiService.getPayment).mockResolvedValue({
      ...relatedPayment,
      notes: 'Платеж зачтен как основной',
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
        notes: 'Платеж зачтен как основной',
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
    expect(screen.getByText('Печатный шаблон счета')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
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

    vi.mocked(apiService.getContracts).mockResolvedValueOnce(createPagedResult([contract]));
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

    vi.mocked(apiService.getContracts).mockResolvedValueOnce(createPagedResult([contract]));
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

  it('opens the linked act from the page sidebar', async () => {
    const onOpenActPage = vi.fn();
    const invoice = {
      id: 562,
      invoiceEntityId: 562,
      number: '562',
      date: '2026-04-12',
      dueDate: '2026-05-12',
      amount: 17500,
      type: 'Income',
      status: 'Pending',
      clientId: 1,
      acts: [
        {
          id: 34,
          number: '34',
          title: null,
          date: '2026-04-24',
          amount: 17500,
          status: 'Created',
          clientId: 1,
          clientName: 'Тестовый клиент',
        },
      ],
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 17500 }],
    } as unknown as Invoice;

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onOpenActPage={onOpenActPage}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Тестовый клиент')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

    fireEvent.click(await screen.findByTestId('invoice-related-act-link-34'));

    expect(onOpenActPage).toHaveBeenCalledWith(34, { invoiceId: 562 });
  });

  it('creates an invoice copy without reusing linked acts from the source invoice', async () => {
    const sourceAct = {
      id: 1176,
      number: '1176',
      title: null,
      date: '2026-04-24',
      amount: 35000,
      status: 'Created',
      clientId: 1,
      clientName: 'Лущенко Сергей Валерьевич',
    } as unknown as Act;
    const invoice = {
      id: 250,
      invoiceEntityId: 250,
      number: '250',
      date: '2025-12-29',
      dueDate: '2026-04-01',
      amount: 35000,
      type: 'Income',
      status: 'Pending',
      workflowStatus: 'Draft',
      clientId: 1,
      acts: [sourceAct],
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 35000 }],
    } as unknown as Invoice;
    vi.mocked(apiService.createInvoice).mockResolvedValue({ ...invoice, id: 251, acts: [] } as unknown as Invoice);

    render(
      <InvoiceModal
        open
        mode="edit"
        presentation="page"
        invoice={invoice}
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(undefined)}
        submitting={false}
        clients={[createTestClient(1, 'Лущенко Сергей Валерьевич')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
        onRefreshRequested={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Копировать' }));

    await waitFor(() => {
      expect(apiService.createInvoice).toHaveBeenCalledTimes(1);
    });
    const [payload] = vi.mocked(apiService.createInvoice).mock.calls[0];
    expect(payload).toMatchObject({
      number: '250 (копия)',
      amount: 35000,
      actId: null,
      actIds: [],
    });
    expect(payload.lineItems).toEqual([
      expect.objectContaining({ name: 'Юридические услуги', quantity: 1, unit: 'шт', price: 35000 }),
    ]);
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
      clientName: 'Тестовый клиент',
      responsibleId: 9,
      responsibleName: 'Ответственный менеджер',
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
      responsibles: [{ id: 9, fullName: 'Ответственный менеджер' }],
      lineItems: [{ name: 'Составление иска', quantity: 1, unit: 'усл.ед.', price: 18000 }],
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
        responsibles={[{ id: 9, fullName: 'Ответственный менеджер' }]}
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
      }), expect.objectContaining({
        invoiceId: 618,
        invoiceSeed: expect.objectContaining({ id: 618, number: '36' }),
      }));
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

  it('submits a newly created client case with the invoice payload', async () => {
    const getCasesMock = vi.mocked(apiService.getCases);
    const createCaseMock = vi.mocked(apiService.createCase);
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getCasesMock.mockResolvedValue([]);
    createCaseMock.mockResolvedValue({
      id: 442,
      clientId: 1,
      title: 'Case from invoice',
      description: 'Created from invoice',
      status: 'Open',
      createdAt: '2026-04-15T10:00:00Z',
    } as ClientCase);

    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Client owner')]}
        responsibles={[]}
        lookupsLoading={false}
        defaultClientId={1}
        relatedPayments={[]}
      />,
    );

    await waitFor(() => {
      expect(getCasesMock).toHaveBeenCalledWith({ clientId: 1 });
    });

    fireEvent.change(screen.getByLabelText(/Номер сч/i), {
      target: { value: 'INV-CASE-1' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Создать дело' }));
    fireEvent.click(await screen.findByRole('button', { name: 'submit-case' }));

    await waitFor(() => {
      expect(createCaseMock).toHaveBeenCalledWith(expect.objectContaining({
        clientId: 1,
        title: 'Case from invoice',
        status: 'Open',
      }));
    });

    expect(await screen.findByText('Case from invoice')).toBeTruthy();

    const form = document.getElementById('invoice-edit-form') as HTMLFormElement | null;
    expect(form).toBeTruthy();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
        number: 'INV-CASE-1',
        clientId: 1,
        clientCaseId: 442,
        clientCaseIds: [442],
      }));
    });
  });

  it('keeps the default client case in the create payload when case options are incomplete', async () => {
    const getCasesMock = vi.mocked(apiService.getCases);
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    getCasesMock.mockResolvedValue([]);

    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'Client owner')]}
        responsibles={[]}
        lookupsLoading={false}
        defaultClientId={1}
        defaultClientCaseId={901}
        relatedPayments={[]}
      />,
    );

    await waitFor(() => {
      expect(getCasesMock).toHaveBeenCalledWith({ clientId: 1 });
    });

    fireEvent.change(screen.getByLabelText(/Номер сч[её]та/i), {
      target: { value: 'INV-DEFAULT-CASE-1' },
    });

    const form = document.getElementById('invoice-edit-form') as HTMLFormElement | null;
    expect(form).toBeTruthy();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
        number: 'INV-DEFAULT-CASE-1',
        clientId: 1,
        clientCaseId: 901,
        clientCaseIds: [901],
      }));
    });
  });

  it('submits an expense invoice without an expense counterparty', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);

    render(
      <InvoiceModal
        open
        mode="create"
        invoice={null}
        onClose={vi.fn()}
        onSubmit={onSubmit}
        submitting={false}
        clients={[createTestClient(1, 'External supplier')]}
        responsibles={[]}
        lookupsLoading={false}
        defaultClientId={1}
        defaultType="Expense"
        relatedPayments={[]}
      />,
    );

    fireEvent.change(screen.getByLabelText(/Номер сч/i), {
      target: { value: 'EXP-NO-CLIENT-1' },
    });

    const form = document.getElementById('invoice-edit-form') as HTMLFormElement | null;
    expect(form).toBeTruthy();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({
        type: 'Expense',
        clientId: 1,
        expenseClientId: null,
        expenseLegalEntityId: null,
        clientCaseId: null,
        clientCaseIds: [],
      }));
    });
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

  it('drops stale client cases that are not linked to the selected counterparty', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
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
      clientCaseId: 5,
      clientCaseIds: [5],
      clientCases: [
        {
          id: 5,
          clientId: 2,
          title: 'Foreign case',
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
        clients={[createTestClient(1, 'Selected client'), createTestClient(2, 'Other client')]}
        responsibles={[]}
        lookupsLoading={false}
        relatedPayments={[]}
      />,
    );

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

  it('downloads invoice offer with the offer template', async () => {
    const downloadMock = vi.mocked(apiService.downloadInvoiceDocument);
    downloadMock.mockClear();

    const invoice = {
      id: 553,
      invoiceEntityId: 553,
      number: '553',
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
    fireEvent.click(screen.getByRole('button', { name: /PDF счет-оферта/i }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(553, 'pdf', {
        includeSignatureAndSeal: false,
        templateKey: 'invoice-offer',
      });
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

  it('prints invoice through html document without requesting server pdf rendering', async () => {
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
      expect(downloadMock).toHaveBeenCalledWith(552, 'html', { includeSignatureAndSeal: false });
    });

    const printWindow = openMock.mock.results[0]?.value as Window & {
      document: { write: ReturnType<typeof vi.fn> };
    };
    await waitFor(() => {
      expect(printWindow.document.write).toHaveBeenCalledWith(expect.stringContaining('<body>preview</body>'));
    });
  });
});
