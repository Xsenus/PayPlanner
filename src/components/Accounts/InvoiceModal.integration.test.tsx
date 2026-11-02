import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { StrictMode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { InvoiceModal } from './InvoiceModal';
import type {
  Act,
  Client,
  ClientBalanceSummary,
  ClientCase,
  Contract,
  ContractInput,
  InstallmentPlanInput,
  Invoice,
  InvoiceInput,
  LegalEntitySummary,
  MoneyOperation,
  MoneyOperationsSummary,
  Payment,
  ServiceCategoryDictionaryItem,
  ServiceDictionaryItem,
} from '../../types';
import type { BaseDictItem } from '../../types/dictionaries';
import { apiService, type PagedResult } from '../../services/api';
import { storeInvoiceEditorDraft, type InvoiceEditorDraftState } from '../../utils/invoiceEditorBridge';
import { defaultRolePermissions, type RolePermissions } from '../../types/permissions';
import { configureDocumentDeliveryTestApi } from '../../test/documentDeliveryFixtures';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { StaleUiOperationError } from '../../utils/StaleUiOperationError';

const permissionState = vi.hoisted(() => ({ value: null as RolePermissions | null }));
const printAuthState = vi.hoisted(() => ({ user: { id: 7, role: { id: 1 } } }));
const referenceState = vi.hoisted(() => ({ loading: false, error: null as string | null, refresh: vi.fn(), capture: vi.fn(() => ({ isCurrent: () => true })) }));
const referenceHookSpy = vi.hoisted(() => vi.fn());
const referenceLookupSpy = vi.hoisted(() => vi.fn());
vi.mock('./InvoiceLineItemLookupDialog', async () => {
  const actual = await vi.importActual<typeof import('./InvoiceLineItemLookupDialog')>('./InvoiceLineItemLookupDialog');
  return { InvoiceLineItemLookupDialog: (props: Parameters<typeof actual.InvoiceLineItemLookupDialog>[0]) => { referenceLookupSpy(props); return <actual.InvoiceLineItemLookupDialog {...props} />; } };
});
let baseAuthSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { baseAuthSession = documentCopyTestSession(); });
afterEach(() => baseAuthSession.restore());
vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => permissionState.value ?? defaultRolePermissions,
}));

type ContractModalMockProps = {
  open: boolean;
  onSubmit: (payload: ContractInput) => void | Promise<void>;
};

type CaseModalMockProps = {
  isOpen: boolean;
  onSave: (payload: Partial<ClientCase>) => void | Promise<void>;
  onClose: () => void;
  overlayClassName?: string;
};

type InstallmentPlannerMockProps = {
  initialPlan?: InstallmentPlanInput | null;
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
    user: printAuthState.user,
  }),
}));

vi.mock('../../hooks/useInvoiceReferenceDictionaries', () => ({
  useInvoiceReferenceDictionaries: (options: unknown) => { referenceHookSpy(options); return ({
    invoiceUnits: invoiceUnitsMock,
    services: servicesMock,
    serviceCategories: serviceCategoriesMock,
    ...referenceState,
  }); },
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ error: toastErrorMock, success: toastSuccessMock }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getOwnCompanyOptions: vi.fn().mockResolvedValue([]),
    getMessengerRecipients: vi.fn(),
    getMessengerDocumentDeliveryHistory: vi.fn(),
    prepareFinancialDocumentMessengerDelivery: vi.fn(),
    queueMessengerDocumentDelivery: vi.fn(),
    prepareElectronicSignatureEnvelope: vi.fn(),
    getMessengerDocumentDeliveryConfiguration: vi.fn().mockResolvedValue({ enabled: false, providers: [] }),
    getElectronicSignatureConfiguration: vi.fn().mockResolvedValue({ enabled: false, preparationEnabled: false, defaultSignatureType: 'qualified', defaultSigningOrder: 'sequential', defaultArchiveRetentionYears: 10, providers: [] }),
    getElectronicSignatureRecipients: vi.fn().mockResolvedValue([]),
    getElectronicSignatureHistory: vi.fn().mockResolvedValue([]),
    getDocumentImagePrintDefaults: vi.fn().mockResolvedValue({ includeLogo: false, includeSeal: false, includeSignature: false }),
    prepareDocumentPrint: vi.fn(),
    uploadInvoiceDocumentToYandexDisk: vi.fn(),
    getInvoicePreview: vi.fn().mockResolvedValue(new Blob(['<html><body>preview</body></html>'], { type: 'text/html' })),
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
  const referenceProps = () => ({ open: true, mode: 'create' as const, invoice: null, onClose: vi.fn(), onSubmit: vi.fn().mockResolvedValue(undefined), submitting: false,
    clients: [], responsibles: [], lookupsLoading: false, relatedPayments: [] });
  function seedReferenceServices() {
    serviceCategoriesMock.push({ id: 1, name: 'Текущая категория', description: '', colorHex: '', createdAt: '', isActive: true });
    servicesMock.push({ id: 1, name: 'Услуга из каталога', description: '', colorHex: '', createdAt: '', isActive: true, categoryId: 1, unit: 'усл.', defaultPrice: 9999 });
  }
  const paymentReadProps = () => ({ ...referenceProps(), mode: 'edit' as const, invoice: { id: 91, invoiceEntityId: 910, number: 'СЧ-91', date: '2026-11-02', dueDate: '2026-11-05', amount: 1000,
    clientId: 1, type: 'Expense', status: 'Pending', workflowStatus: 'Draft', isPaid: false, createdAt: '2026-11-02' } as Invoice, clients: [createTestClient(1, 'Клиент счёта')] });
  describe('invoice financial reads', () => {
    const currentProps = () => ({ ...paymentReadProps(), invoice: { ...paymentReadProps().invoice, type: 'Income' as const } });
    const operation = (id = 701, linkedInvoiceId = 910): MoneyOperation => ({ id, linkedInvoiceId, amount: 25, currency: 'RUB', source: 'Bank', direction: 'Income', accountingType: 'Fact',
      isAdvancePayment: false, date: '2026-11-02', createdAt: '2026-11-02T00:00:00Z', purpose: `Движение ${id}` });
    const page = (rows = [operation()], total = rows.length, current = 1) => ({ items: rows, total, page: current, pageSize: 20, summary: createEmptyMoneyOperationsSummary() });
    it('ignores pending movement data from a former role', async () => {
      const pending = createDeferred<ReturnType<typeof page>>(); vi.mocked(apiService.getMoneyOperationsPaged).mockReturnValueOnce(pending.promise).mockResolvedValue(page([operation(702)]));
      const props = currentProps(); const view = render(<InvoiceModal {...props} />); await waitFor(() => expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce());
      printAuthState.user = { id: 7, role: { id: 8 } }; view.rerender(<InvoiceModal {...props} />); await act(async () => pending.resolve(page()));
      expect(screen.queryByText('Движение 701')).toBeNull(); expect(await screen.findByText('Движение 702')).toBeTruthy();
    });
    it('rejects a previous login response even before the auth context rerenders', async () => {
      const pending = createDeferred<ReturnType<typeof page>>(); vi.mocked(apiService.getMoneyOperationsPaged).mockReturnValueOnce(pending.promise);
      render(<InvoiceModal {...currentProps()} />); await waitFor(() => expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce());
      baseAuthSession.replace(); await act(async () => pending.resolve(page())); expect(screen.queryByText('Движение 701')).toBeNull();
    });
    it('rejects a movement explicitly linked to another canonical invoice', async () => {
      vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValue(page([operation(701, 911)])); render(<InvoiceModal {...currentProps()} />);
      await waitFor(() => expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce()); await act(async () => {});
      expect(screen.queryByText('Движение 701')).toBeNull(); expect(screen.getByRole('button', { name: 'Повторить загрузку движений денег' })).toBeTruthy();
    });
    it('keeps a failed movement request visible and allows one explicit retry without changing the draft', async () => {
      vi.mocked(apiService.getMoneyOperationsPaged).mockRejectedValueOnce(new Error('Движения временно недоступны')).mockResolvedValue(page());
      render(<InvoiceModal {...currentProps()} />); fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ' } });
      expect(await screen.findByText('Движения временно недоступны')).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку движений денег' }));
      expect(await screen.findByText('Движение 701')).toBeTruthy(); expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ');
    });
    it('rejects a balance from another counterparty instead of showing its money', async () => {
      vi.mocked(apiService.getClientBalance).mockResolvedValue({ clientId: 2, balance: 98765.43 }); render(<InvoiceModal {...currentProps()} />);
      await waitFor(() => expect(apiService.getClientBalance).toHaveBeenCalledOnce()); await act(async () => {});
      expect(screen.queryByText('Доступно на балансе клиента')).toBeNull(); expect(screen.getByRole('button', { name: 'Повторить загрузку баланса' })).toBeTruthy();
    });
    it('hides a formerly confirmed balance on role change while its replacement is pending', async () => {
      const pending = createDeferred<ClientBalanceSummary>(); vi.mocked(apiService.getClientBalance).mockResolvedValueOnce({ clientId: 1, balance: 100 }).mockReturnValueOnce(pending.promise);
      const props = currentProps(); const view = render(<InvoiceModal {...props} />); expect(await screen.findByText('Доступно на балансе клиента')).toBeTruthy();
      printAuthState.user = { id: 7, role: { id: 8 } }; view.rerender(<InvoiceModal {...props} />);
      expect(screen.queryByText('100,00')).toBeNull(); await act(async () => pending.resolve({ clientId: 1, balance: 0 }));
    });
    it('does not request a full balance with a restricted payment scope', async () => {
      permissionState.value = { ...defaultRolePermissions, payments: { ...defaultRolePermissions.payments, viewScope: 'own' } };
      render(<InvoiceModal {...currentProps()} />); await act(async () => {}); expect(apiService.getClientBalance).not.toHaveBeenCalled();
    });
    it('keeps movement reads bounded and opens the next page on demand', async () => {
      vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValueOnce(page(Array.from({ length: 20 }, (_, i) => operation(701 + i)), 21))
        .mockResolvedValueOnce(page([operation(721)], 21, 2));
      render(<InvoiceModal {...currentProps()} />); expect(await screen.findByText('Движение 720')).toBeTruthy(); expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce();
      fireEvent.click(screen.getByRole('button', { name: 'Следующая страница движений денег' })); expect(await screen.findByText('Движение 721')).toBeTruthy(); expect(screen.queryByText('Движение 701')).toBeNull();
      expect(vi.mocked(apiService.getMoneyOperationsPaged).mock.calls[1][0]).toEqual({ linkedInvoiceId: 910, page: 2, pageSize: 20 });
    });
    it.each(['USD', null] as const)('shows the supplied movement currency %s without silently assuming rubles', async currency => {
      vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValue(page([{ ...operation(), amount: -25.75, currency }])); render(<InvoiceModal {...currentProps()} />);
      expect(await screen.findByText(currency ? '-25,75 USD' : '-25,75 валюта не указана')).toBeTruthy();
    });
    it('displays a large finite movement amount without scientific notation or an undefined fractional part', async () => {
      vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValue(page([{ ...operation(), amount: 1e22, currency: 'USD' }])); render(<InvoiceModal {...currentProps()} />);
      expect(await screen.findByText((_, element) => element?.tagName === 'SPAN'
        && element.textContent?.replace(/\s/g, ' ') === '10 000 000 000 000 000 000 000,00 USD')).toBeTruthy();
    });
    it('keeps the real server readonly payment identity and label', async () => {
      vi.mocked(apiService.getMoneyOperationsPaged).mockResolvedValue(page([{ ...operation(-701), isReadonly: true, relatedPaymentId: 701 }])); render(<InvoiceModal {...currentProps()} />);
      expect(await screen.findByText('Движение -701')).toBeTruthy(); expect(screen.getByText('Платёж')).toBeTruthy();
    });
    it('loads only the selected legal entity balance and masks it when its view permission is revoked', async () => {
      vi.mocked(apiService.getLegalEntityBalance).mockResolvedValue({ legalEntityId: 12, balance: 321 }); const props = currentProps();
      props.invoice = { ...props.invoice, legalEntityId: 12, clientId: null };
      const view = render(<InvoiceModal {...props} />); expect(await screen.findByText('Доступно на балансе юрлица')).toBeTruthy(); expect(apiService.getClientBalance).not.toHaveBeenCalled();
      permissionState.value = { ...defaultRolePermissions, legalEntities: { ...defaultRolePermissions.legalEntities, canView: false } }; view.rerender(<InvoiceModal {...props} />);
      expect(screen.queryByText('Доступно на балансе юрлица')).toBeNull(); expect(screen.queryByText('321,00')).toBeNull();
    });
    it('does not fetch money or balance without the invoice read permission', async () => {
      permissionState.value = { ...defaultRolePermissions, accounts: { ...defaultRolePermissions.accounts, canView: false } };
      render(<InvoiceModal {...currentProps()} />); await act(async () => {}); expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled(); expect(apiService.getClientBalance).not.toHaveBeenCalled();
    });
    it('keeps financial details lazy in page preview and aborts their reads when returning to preview', async () => {
      const pending = createDeferred<ReturnType<typeof page>>(); vi.mocked(apiService.getMoneyOperationsPaged).mockReturnValueOnce(pending.promise);
      render(<InvoiceModal {...currentProps()} presentation="page" />); await screen.findByRole('button', { name: 'Редактировать счёт' });
      expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled(); expect(apiService.getClientBalance).not.toHaveBeenCalled();
      fireEvent.click(screen.getByRole('button', { name: 'Редактировать счёт' })); await waitFor(() => expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledOnce());
      const signal = vi.mocked(apiService.getMoneyOperationsPaged).mock.calls[0][1]?.signal; fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
      expect(signal?.aborted).toBe(true); await act(async () => pending.resolve(page())); expect(screen.queryByText('Движение 701')).toBeNull();
    });
  });
  it.each(['loading', 'error'] as const)('invoice payments read: %s cannot submit unconfirmed financial rows through the form', async state => {
    const props = paymentReadProps(); render(<InvoiceModal {...props} paymentsLoading={state === 'loading'} paymentsError={state === 'error' ? 'Ошибка загрузки платежей' : null} />);
    await screen.findByLabelText('Номер счёта'); expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', true);
    fireEvent.submit(document.getElementById('invoice-edit-form')!); await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Сначала дождитесь загрузки платежей по счёту или повторите её после ошибки.', 'Проверьте данные счета'));
    expect(props.onSubmit).not.toHaveBeenCalled(); expect(screen.queryByText('Связанные платежи пока не добавлены.')).toBeNull();
  });
  it('invoice payments read: retry keeps the manual draft and a confirmed empty result permits saving', async () => {
    const props = paymentReadProps(); const retry = vi.fn().mockResolvedValue(undefined);
    const view = render(<InvoiceModal {...props} paymentsError="Ошибка загрузки платежей" onRetryPayments={retry} />);
    fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'СЧ-РУЧНОЙ' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Повторить загрузку платежей' })[0]); await waitFor(() => expect(retry).toHaveBeenCalledOnce());
    view.rerender(<InvoiceModal {...props} paymentsError={null} onRetryPayments={retry} />);
    expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'СЧ-РУЧНОЙ'); expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false);
    fireEvent.submit(document.getElementById('invoice-edit-form')!); await waitFor(() => expect(props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ number: 'СЧ-РУЧНОЙ', amount: 1000 })));
  });
  it('invoice payments read: Accounts edit without Payments view remains available without exposing rows', async () => {
    permissionState.value = { ...defaultRolePermissions, payments: { ...defaultRolePermissions.payments, canView: false } };
    const props = paymentReadProps(); render(<InvoiceModal {...props} paymentsError="Недостаточно прав для просмотра платежей." />);
    await screen.findByLabelText('Номер счёта'); expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false);
    fireEvent.submit(document.getElementById('invoice-edit-form')!); await waitFor(() => expect(props.onSubmit).toHaveBeenCalledOnce());
    expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled();
  });
  it('invoice payments read: the page summary displays denial instead of claiming there are no payments', async () => {
    const props = paymentReadProps(); render(<InvoiceModal {...props} presentation="page" paymentsError="Платежи недоступны" onRetryPayments={vi.fn()} />);
    expect(await screen.findByText('Платежи недоступны')).toBeTruthy(); expect(screen.queryByText('Связанные оплаты пока не добавлены.')).toBeNull();
    expect(screen.getByRole('button', { name: 'Повторить загрузку платежей' })).toBeTruthy();
  });
  it('invoice payments permission baseline: stale supplied rows remain hidden without Payments view', async () => {
    permissionState.value = { ...defaultRolePermissions, payments: { ...defaultRolePermissions.payments, canView: false } };
    const props = paymentReadProps(); const hidden = { id: 987, invoiceId: 910, amount: 98765.43, type: 'Income', status: 'Completed', description: 'Скрытая оплата',
      date: '2026-11-02', plannedDate: '2026-11-02', paidAmount: 98765.43, isPaid: true, documentKind: 'Payment', systemNotes: '', notes: '' } as Payment;
    render(<InvoiceModal {...props} relatedPayments={[hidden]} />); await screen.findByLabelText('Номер счёта');
    expect(screen.queryByText('Скрытая оплата')).toBeNull(); expect(screen.getByText('Недостаточно прав для просмотра платежей.')).toBeTruthy(); expect(screen.queryByText('Связанные платежи пока не добавлены.')).toBeNull(); expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled();
  });
  describe('invoice draft hydration', () => {
    const props = () => ({ ...paymentReadProps(), invoice: { ...paymentReadProps().invoice,
      description: 'Исходное описание', lineItems: [{ name: 'Услуга', quantity: 1, unit: 'шт', price: 1000 }] } });
    const changedInvoice = (current: ReturnType<typeof props>) => ({ ...current.invoice, description: 'Изменено на сервере', auditUpdatedAt: '2026-11-02T02:00:00Z' });
    it('preserves manual fields and line items after an equivalent metadata refresh', async () => {
      const current = props(); const view = render(<InvoiceModal {...current} />);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      fireEvent.change(screen.getByLabelText('Цена'), { target: { value: '1250,50' } });
      view.rerender(<InvoiceModal {...current} invoice={{ ...current.invoice, lineItems: current.invoice.lineItems.map(row => ({ ...row })), auditUpdatedAt: '2026-11-02T02:00:00Z' }} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ-91');
      fireEvent.submit(document.getElementById('invoice-edit-form')!);
      await waitFor(() => expect(current.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ number: 'РУЧНОЙ-91', amount: 1250.5,
        lineItems: [expect.objectContaining({ name: 'Услуга', price: 1250.5 })] })));
    });
    it('keeps page edit mode and manual input when installment rows arrive late', async () => {
      const current = props(); const view = render(<InvoiceModal {...current} presentation="page" paymentsLoading />);
      fireEvent.click(await screen.findByRole('button', { name: 'Редактировать счёт' }));
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      const row = { id: 701, invoiceId: 910, amount: 1000, date: '2026-11-05', type: 'Expense', status: 'Pending', isPaid: false,
        description: 'Рассрочка', systemNotes: '[pp:installment-payment] {"sequence":1,"total":1000,"item":{"sequence":1}}' } as Payment;
      view.rerender(<InvoiceModal {...current} presentation="page" relatedPayments={[row]} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ-91');
      expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false);
    });
    it('adopts changed server metadata when the editor is untouched', async () => {
      const current = props(); const view = render(<InvoiceModal {...current} />); await screen.findByLabelText('Номер счёта');
      view.rerender(<InvoiceModal {...current} invoice={{ ...changedInvoice(current), number: 'НОВЫЙ-91', amount: 2000,
        lineItems: [{ name: 'Другая услуга', quantity: 1, unit: 'шт', price: 2000 }] }} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'НОВЫЙ-91');
      expect(screen.queryByText('Счёт изменился после начала редактирования. Ваши правки сохранены в форме.')).toBeNull();
      fireEvent.submit(document.getElementById('invoice-edit-form')!);
      await waitFor(() => expect(current.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ number: 'НОВЫЙ-91', amount: 2000, description: 'Изменено на сервере' })));
    });
    it('retains a dirty draft and blocks overwriting concurrently changed metadata', async () => {
      const current = props(); const view = render(<InvoiceModal {...current} />);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      view.rerender(<InvoiceModal {...current} invoice={changedInvoice(current)} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ-91');
      expect(screen.getByText('Счёт изменился после начала редактирования. Ваши правки сохранены в форме.')).toBeTruthy();
      expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', true);
      fireEvent.submit(document.getElementById('invoice-edit-form')!); await act(async () => {});
      expect(current.onSubmit).not.toHaveBeenCalled(); expect(apiService.updateInvoice).not.toHaveBeenCalled();
    });
    it.each([false, true])('reloads concurrent metadata only with explicit confirmation %s', async accepted => {
      const current = props(); const view = render(<InvoiceModal {...current} />);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      view.rerender(<InvoiceModal {...current} invoice={changedInvoice(current)} />);
      confirmMock.mockResolvedValueOnce(accepted); fireEvent.click(screen.getByRole('button', { name: 'Загрузить актуальный счёт' }));
      await waitFor(() => expect(confirmMock).toHaveBeenCalledOnce()); await act(async () => {});
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', accepted ? 'СЧ-91' : 'РУЧНОЙ-91');
      expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', !accepted);
      expect(current.onSubmit).not.toHaveBeenCalled(); expect(apiService.updateInvoice).not.toHaveBeenCalled();
    });
    it.each(['document', 'reopen', 'login', 'role'] as const)('isolates a manual draft across %s', async change => {
      const current = props(); const view = render(<InvoiceModal {...current} />);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      if (change === 'reopen') view.rerender(<InvoiceModal {...current} open={false} />);
      if (change === 'login') baseAuthSession.replace();
      if (change === 'role') printAuthState.user = { id: 7, role: { id: 8 } };
      view.rerender(<InvoiceModal {...current} invoice={change === 'document' ? { ...current.invoice, id: 92, invoiceEntityId: 920, number: 'СЧ-92' } : current.invoice} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', change === 'document' ? 'СЧ-92' : 'СЧ-91');
    });
    it('keeps the same user draft through token refresh and new default responsible props', async () => {
      const current = props(); const view = render(<InvoiceModal {...current} defaultResponsibleId={7} />);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      baseAuthSession.refresh(); view.rerender(<InvoiceModal {...current} defaultResponsibleId={8} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ-91');
    });
    it('keeps editing when a refreshed canonical invoice uses another representative payment ID', async () => {
      const current = props(); const view = render(<InvoiceModal {...current} presentation="page" />);
      fireEvent.click(await screen.findByRole('button', { name: 'Редактировать счёт' }));
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      view.rerender(<InvoiceModal {...current} presentation="page" invoice={{ ...current.invoice, id: 92 }} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ-91');
    });
    it('does not resurrect a manually removed case after equivalent metadata refresh', async () => {
      const current = props(); current.invoice = { ...current.invoice, expenseClientId: 1, clientCaseId: 441, clientCaseIds: [441],
        clientCases: [{ id: 441, clientId: 1, title: 'Связанное дело', status: 'Open', createdAt: '2026-11-02' }] };
      const view = render(<InvoiceModal {...current} />); await screen.findByLabelText('Номер счёта');
      fireEvent.click(screen.getAllByRole('button', { name: 'Очистить' })[0]);
      view.rerender(<InvoiceModal {...current} invoice={{ ...current.invoice }} />);
      fireEvent.submit(document.getElementById('invoice-edit-form')!);
      await waitFor(() => expect(current.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ clientCaseId: null, clientCaseIds: [] })));
    });
    it.each(['document', 'login', 'metadata', 'cancel'] as const)('ignores a deferred reload confirmation after %s', async change => {
      const current = props(); const view = render(<InvoiceModal {...current} />);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      const latest = changedInvoice(current); view.rerender(<InvoiceModal {...current} invoice={latest} />);
      const pending = createDeferred<boolean>(); confirmMock.mockReturnValueOnce(pending.promise);
      fireEvent.click(screen.getByRole('button', { name: 'Загрузить актуальный счёт' }));
      fireEvent.click(screen.getByRole('button', { name: 'Загрузить актуальный счёт' })); expect(confirmMock).toHaveBeenCalledOnce();
      if (change === 'document') view.rerender(<InvoiceModal {...current} invoice={{ ...current.invoice, id: 92, invoiceEntityId: 920, number: 'СЧ-92' }} />);
      if (change === 'login') { baseAuthSession.replace(); view.rerender(<InvoiceModal {...current} invoice={latest} />); }
      if (change === 'metadata') view.rerender(<InvoiceModal {...current} invoice={{ ...latest, description: 'Ещё одно изменение' }} />);
      await act(async () => pending.resolve(change !== 'cancel'));
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', change === 'document' ? 'СЧ-92' : change === 'login' ? 'СЧ-91' : 'РУЧНОЙ-91');
      expect(current.onSubmit).not.toHaveBeenCalled(); expect(apiService.updateInvoice).not.toHaveBeenCalled();
    });
    it('accepts an own successful save response without replacing manual formatting or edit mode', async () => {
      const current = props(); const view = render(<InvoiceModal {...current} presentation="page" />);
      fireEvent.click(await screen.findByRole('button', { name: 'Редактировать счёт' }));
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      fireEvent.submit(document.getElementById('invoice-edit-form')!); await waitFor(() => expect(current.onSubmit).toHaveBeenCalledOnce());
      view.rerender(<InvoiceModal {...current} presentation="page" invoice={{ ...current.invoice, number: 'РУЧНОЙ-91' }} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ-91');
      expect(screen.queryByText('Счёт изменился после начала редактирования. Ваши правки сохранены в форме.')).toBeNull();
    });
    it('does not treat a picker-only service category absent from the saved response as a concurrent edit', async () => {
      seedReferenceServices(); const current = props(); const view = render(<InvoiceModal {...current} />); await screen.findByLabelText('Номер счёта');
      fireEvent.click(screen.getByRole('button', { name: 'Наименование услуги' }));
      act(() => referenceLookupSpy.mock.lastCall![0].onSelect('1'));
      fireEvent.submit(document.getElementById('invoice-edit-form')!); await waitFor(() => expect(current.onSubmit).toHaveBeenCalledOnce());
      const saved = current.onSubmit.mock.lastCall![0] as InvoiceInput;
      view.rerender(<InvoiceModal {...current} invoice={{ ...current.invoice, ...saved } as Invoice} />);
      expect(screen.queryByText('Счёт изменился после начала редактирования. Ваши правки сохранены в форме.')).toBeNull();
      expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false);
      expect(screen.getByLabelText('Цена')).toHaveProperty('value', '9 999,00');
    });
    it('restores a matching bridge draft once and does not carry it into another login', async () => {
      const current = props(); const resumeDraft: InvoiceEditorDraftState = { mode: 'edit', presentation: 'modal', invoiceId: 91, pageViewMode: false,
        form: { number: 'ВОССТАНОВЛЕН-91', date: '2026-11-02', dueDate: '2026-11-05', amount: '1000', type: 'Expense', status: 'Pending', workflowStatus: 'Draft',
          clientId: '1', legalEntityId: '', expenseClientId: '', expenseLegalEntityId: '', contractId: '', clientCaseReference: '', description: 'Мои правки',
          isNotForAccounting: false, actReference: '', paidDate: '', responsibleIds: [] },
        lineItems: [{ id: 'draft-row', name: 'Услуга', quantity: '1', unit: 'шт', price: '1000', serviceCategoryId: '', serviceId: '' }], selectedActs: [], selectedClientCaseIds: [], billingTaskIds: [77] };
      const view = render(<InvoiceModal {...current} resumeDraft={resumeDraft} />);
      expect(await screen.findByLabelText('Номер счёта')).toHaveProperty('value', 'ВОССТАНОВЛЕН-91');
      fireEvent.submit(document.getElementById('invoice-edit-form')!);
      await waitFor(() => expect(current.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ billingTaskIds: [77] })));
      current.onSubmit.mockClear();
      baseAuthSession.replace(); view.rerender(<InvoiceModal {...current} resumeDraft={resumeDraft} />);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'СЧ-91');
      fireEvent.submit(document.getElementById('invoice-edit-form')!);
      await waitFor(() => expect(current.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ billingTaskIds: undefined })));
    });
    it('preserves draft under StrictMode replay and repeated equivalent responses without new financial reads', async () => {
      const current = props(); const view = render(<StrictMode><InvoiceModal {...current} /></StrictMode>);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      const reads = vi.mocked(apiService.getMoneyOperationsPaged).mock.calls.length;
      for (let i = 0; i < 4; i++) view.rerender(<StrictMode><InvoiceModal {...current} invoice={{ ...current.invoice }} /></StrictMode>);
      expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'РУЧНОЙ-91'); expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledTimes(reads);
    });
    it.each(['same-plan', 'different-plan', 'unconfirmed'] as const)('reconciles a locally changed installment plan with %s without silently replacing it', async mode => {
      const current = props(); const plan: InstallmentPlanInput = { total: 1000, downPayment: 0, annualRate: 0, months: 1, startDate: '2026-11-05', toPay: 1000, overpay: 0,
        items: [{ sequence: 1, date: '2026-11-05', principal: 1000, interest: 0, payment: 1000, balance: 0 }] };
      installmentPlannerPlanRef.current = plan; const view = render(<InvoiceModal {...current} />); await screen.findByLabelText('Номер счёта');
      fireEvent.click(screen.getAllByRole('button').find(button => button.textContent === 'Рассрочка')!); fireEvent.click(await screen.findByRole('button', { name: 'apply-installment-plan' }));
      await waitFor(() => expect(apiService.updateInvoice).toHaveBeenCalledOnce()); await act(async () => {});
      const remote = { ...plan, item: { ...plan.items[0], payment: mode === 'same-plan' ? 1000 : 999 }, sequence: 1 };
      const row = { id: 701, invoiceId: 910, amount: 1000, date: '2026-11-05', type: 'Expense', status: 'Pending', isPaid: false,
        description: 'Рассрочка', systemNotes: '[pp:installment-payment] ' + JSON.stringify(remote) } as Payment;
      view.rerender(<InvoiceModal {...current} relatedPayments={[row]} paymentsLoading={mode === 'unconfirmed'} />);
      expect(Boolean(screen.queryByText('Счёт изменился после начала редактирования. Ваши правки сохранены в форме.'))).toBe(mode === 'different-plan');
      expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', mode !== 'same-plan');
      fireEvent.click(screen.getAllByRole('button').find(button => button.textContent === 'Изменить рассрочку')!);
      expect(installmentPlannerPropsSpy.mock.lastCall![0].initialPlan).toEqual(plan);
      expect(apiService.updateInvoice).toHaveBeenCalledOnce();
    });
    it.each(['document', 'metadata', 'loading'] as const)('stops a pending due-date confirmation before mutating or submitting after %s', async change => {
      const current = props(); const row = { id: 701, invoiceId: 910, amount: 1000, date: '2026-11-10', type: 'Expense', status: 'Pending', isPaid: false,
        description: 'Рассрочка', systemNotes: '[pp:installment-payment] {"sequence":1,"total":1000,"item":{"sequence":1,"date":"2026-11-10"}}' } as Payment;
      const view = render(<InvoiceModal {...current} relatedPayments={[row]} />);
      fireEvent.change(await screen.findByLabelText('Номер счёта'), { target: { value: 'РУЧНОЙ-91' } });
      const pending = createDeferred<boolean>(); confirmMock.mockReturnValueOnce(pending.promise);
      fireEvent.submit(document.getElementById('invoice-edit-form')!); await waitFor(() => expect(confirmMock).toHaveBeenCalledOnce());
      view.rerender(<InvoiceModal {...current} relatedPayments={change === 'document' ? [] : [row]} paymentsLoading={change === 'loading'} invoice={change === 'document'
        ? { ...current.invoice, id: 92, invoiceEntityId: 920, number: 'СЧ-92' } : change === 'metadata' ? changedInvoice(current) : current.invoice} />);
      await act(async () => pending.resolve(true));
      const dates = document.querySelectorAll('input[type="date"]') as NodeListOf<HTMLInputElement>;
      expect(dates[1].value).toBe('2026-11-05'); expect(current.onSubmit).not.toHaveBeenCalled();
    });
    it.each(['back', 'close', 'cancel'] as const)('revokes draft callbacks synchronously on %s even before the parent rerenders', async exit => {
      const current = props(); const view = render(<InvoiceModal {...current} presentation={exit === 'back' ? 'page' : 'modal'} />);
      if (exit === 'back') fireEvent.click(await screen.findByRole('button', { name: 'Редактировать счёт' }));
      const form = document.getElementById('invoice-edit-form')!;
      fireEvent.click(screen.getByRole('button', { name: exit === 'back' ? 'Назад' : exit === 'close' ? 'Закрыть' : 'Отмена' }));
      expect(current.onClose).toHaveBeenCalledOnce(); fireEvent.submit(form); await act(async () => {});
      expect(current.onSubmit).not.toHaveBeenCalled(); view.unmount();
    });
  });
  it('reference caller scope: closed and forbidden callers are disabled and permission changes close the open picker', async () => {
    seedReferenceServices(); const props = referenceProps(); const view = render(<InvoiceModal {...props} open={false} />);
    expect(referenceHookSpy.mock.lastCall![0].enabled).toBe(false); view.rerender(<InvoiceModal {...props} />); await act(async () => {});
    fireEvent.click(screen.getByRole('button', { name: 'Наименование услуги' })); expect(screen.getByText('Выбор наименования услуги')).toBeTruthy(); const scope = referenceHookSpy.mock.lastCall![0].ownerScope;
    permissionState.value = { ...defaultRolePermissions, accounts: { ...defaultRolePermissions.accounts, canView: false, canCreate: false } };
    view.rerender(<InvoiceModal {...props} />); expect(referenceHookSpy.mock.lastCall![0].enabled).toBe(false); expect(referenceHookSpy.mock.lastCall![0].ownerScope).not.toBe(scope);
    expect(screen.queryByText('Выбор наименования услуги')).toBeNull();
  });
  it('reference caller session: an old service callback cannot apply a financial template after login replacement', async () => {
    seedReferenceServices(); const props = referenceProps(); render(<InvoiceModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Цена'), { target: { value: '1234,56' } }); const before = (screen.getByLabelText('Цена') as HTMLInputElement).value;
    fireEvent.click(screen.getByRole('button', { name: 'Наименование услуги' })); const old = referenceLookupSpy.mock.lastCall![0];
    baseAuthSession.replace('actor'); act(() => old.onSelect('1'));
    expect(screen.getByLabelText('Цена')).toHaveProperty('value', before); expect(props.onSubmit).not.toHaveBeenCalled();
  });
  it('reference caller refresh: a held choice cannot change price while refreshed catalogues are loading', async () => {
    seedReferenceServices(); const props = referenceProps(); const view = render(<InvoiceModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Цена'), { target: { value: '1234,56' } }); const before = (screen.getByLabelText('Цена') as HTMLInputElement).value;
    fireEvent.click(screen.getByRole('button', { name: 'Наименование услуги' })); const old = referenceLookupSpy.mock.lastCall![0];
    referenceState.loading = true; view.rerender(<InvoiceModal {...props} />); act(() => { old.onSelect('1'); old.onFilterChange('1'); });
    expect(screen.getByLabelText('Цена')).toHaveProperty('value', before); expect(screen.queryByTestId('invoice-line-item-lookup-option-1')).toBeNull();
    expect(screen.getByText('Загрузка справочника услуг...')).toBeTruthy();
  });
  it('reference caller manual: unavailable catalogues allow a custom name without applying cached price or unit defaults', async () => {
    seedReferenceServices(); referenceState.error = 'HTTP 403 - Справочник закрыт';
    const props = referenceProps(); render(<InvoiceModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Цена'), { target: { value: '1234,56' } }); fireEvent.change(screen.getByLabelText('Единица измерения'), { target: { value: 'час' } });
    const before = (screen.getByLabelText('Цена') as HTMLInputElement).value;
    fireEvent.click(screen.getByRole('button', { name: 'Наименование услуги' })); fireEvent.change(screen.getByPlaceholderText('Поиск наименования'), { target: { value: 'Ручное наименование' } });
    fireEvent.click(screen.getByRole('button', { name: /Использовать: Ручное наименование/ }));
    expect(screen.getByLabelText('Цена')).toHaveProperty('value', before); expect(screen.getByLabelText('Единица измерения')).toHaveProperty('value', 'час');
    expect(screen.getByRole('button', { name: 'Наименование услуги' }).textContent).toContain('Ручное наименование'); expect(props.onSubmit).not.toHaveBeenCalled();
  });
  it('reference caller retry: one explicit retry retains query and amount fields', async () => {
    referenceState.error = 'Временный отказ справочника'; const pending = createDeferred<void>(); referenceState.refresh.mockReturnValue(pending.promise);
    const props = referenceProps(); const view = render(<InvoiceModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Цена'), { target: { value: '1234,56' } }); const before = (screen.getByLabelText('Цена') as HTMLInputElement).value;
    fireEvent.click(screen.getByRole('button', { name: 'Наименование услуги' })); fireEvent.change(screen.getByPlaceholderText('Поиск наименования'), { target: { value: 'Сохраняемый поиск' } });
    const retry = referenceLookupSpy.mock.lastCall![0].onRetry; act(() => { retry(); retry(); }); expect(referenceState.refresh).toHaveBeenCalledOnce();
    referenceState.loading = true; view.rerender(<InvoiceModal {...props} />); expect(screen.getAllByRole('button', { name: 'Повторить загрузку услуг' }).every(button => (button as HTMLButtonElement).disabled)).toBe(true);
    referenceState.loading = false; referenceState.error = null; await act(async () => { pending.resolve(); }); view.rerender(<InvoiceModal {...props} />);
    expect(screen.getByPlaceholderText('Поиск наименования')).toHaveProperty('value', 'Сохраняемый поиск'); expect(screen.getByLabelText('Цена')).toHaveProperty('value', before);
  });
  it('caller lookup retry: an explicit retry preserves the invoice draft and is disabled during loading', async () => {
    const retry = vi.fn();
    const props = { open: true, mode: 'create' as const, invoice: null, onClose: vi.fn(), onSubmit: vi.fn().mockResolvedValue(undefined), submitting: false,
      clients: [createTestClient(1, 'Текущий клиент')], responsibles: [], lookupsLoading: false, lookupsError: 'Справочники временно недоступны', onRetryLookups: retry, defaultClientId: 1, relatedPayments: [] };
    const view = render(<InvoiceModal {...props} />); await act(async () => {});
    fireEvent.change(screen.getByLabelText('Номер счёта'), { target: { value: 'INV-DRAFT-RETRY' } });
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку справочников' })); expect(retry).toHaveBeenCalledOnce();
    view.rerender(<InvoiceModal {...props} lookupsLoading />);
    expect(screen.getByRole('button', { name: 'Повторить загрузку справочников' })).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('Номер счёта')).toHaveProperty('value', 'INV-DRAFT-RETRY'); expect(props.onSubmit).not.toHaveBeenCalled();
  });
  const storeInvoiceEditorDraftMock = vi.mocked(storeInvoiceEditorDraft);

  it('opens invoice preview without requesting financial data or enabling export', async () => {
    permissionState.value = {
      ...defaultRolePermissions,
      accounts: { ...defaultRolePermissions.accounts, canExport: false },
      payments: { ...defaultRolePermissions.payments, canView: false },
    };
    const invoice = {
      id: 689, number: '689', date: '2026-10-09', dueDate: '2026-10-10',
      amount: 11000, type: 'Income', status: 'Pending', clientId: 69,
      lineItems: [{ name: 'Юридические услуги', quantity: 1, unit: 'усл.', price: 11000 }],
    } as unknown as Invoice;
    render(<InvoiceModal open mode="edit" invoice={invoice} onClose={vi.fn()}
      onSubmit={vi.fn()} submitting={false} clients={[]} responsibles={[]} presentation="page"
      lookupsLoading={false} relatedPayments={[]} />);

    await screen.findByTitle('Печатный шаблон счета 689');
    expect(apiService.getInvoicePreview).toHaveBeenCalledWith(689);
    expect(apiService.getMoneyOperationsPaged).not.toHaveBeenCalled();
    expect(apiService.getClientBalance).not.toHaveBeenCalled();
    expect(apiService.getLegalEntityBalance).not.toHaveBeenCalled();
    expect(apiService.downloadInvoiceDocument).not.toHaveBeenCalled();
    for (const name of ['Скачать', 'Печать', 'Отправить', 'Я.Диск']) {
      expect((screen.getByRole('button', { name }) as HTMLButtonElement).disabled).toBe(true);
    }
  });

  beforeEach(() => {
    configureDocumentDeliveryTestApi('invoice', 0, 0, false);
    referenceHookSpy.mockClear(); referenceState.loading = false; referenceState.error = null;
    referenceLookupSpy.mockClear();
    referenceState.refresh.mockReset().mockResolvedValue(undefined); referenceState.capture.mockReset().mockImplementation(() => ({ isCurrent: () => true }));
    printAuthState.user = { id: 7, role: { id: 1 } };
    vi.mocked(apiService.getDocumentImagePrintDefaults).mockReset().mockResolvedValue({ includeLogo: false, includeSeal: false, includeSignature: false });
    vi.mocked(apiService.prepareDocumentPrint).mockReset().mockImplementation(async () => Object.assign(new Blob(['print copy']), { text: async () => 'print copy' }));
    vi.mocked(apiService.uploadInvoiceDocumentToYandexDisk).mockReset().mockResolvedValue({ success: true, path: '/CRM/invoice.pdf' });
    permissionState.value = null;
    vi.mocked(apiService.getInvoicePreview).mockClear();
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

  it('keeps invoice cancellation in the dedicated action instead of status selectors', () => {
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

    expect(container.querySelectorAll('option[value="Cancelled"]')).toHaveLength(0);
    expect(screen.getByText(/Для аннулирования используйте действие «Отмена счёта»/)).toBeTruthy();
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
    expect(await screen.findByText('7 100,00')).toBeTruthy();
    expect(apiService.getClientBalance).toHaveBeenCalledWith(69, { signal: expect.any(AbortSignal) });
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
      expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledWith({ linkedInvoiceId: 33, page: 1, pageSize: 20 }, { signal: expect.any(AbortSignal), cache: 'no-store', expectedStatus: 200 });
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
      expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledWith({ linkedInvoiceId: 1850, page: 1, pageSize: 20 }, { signal: expect.any(AbortSignal), cache: 'no-store', expectedStatus: 200 });
    });

    await waitFor(() => {
      expect(screen.queryByText('Движения денег')).toBeNull();
    });
    expect(screen.queryByText('Связанные движения денег пока не найдены.')).toBeNull();
  });

  it('shows a recoverable money operations error when linked operations fail to load', async () => {
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
      expect(apiService.getMoneyOperationsPaged).toHaveBeenCalledWith({ linkedInvoiceId: 1849, page: 1, pageSize: 20 }, { signal: expect.any(AbortSignal), cache: 'no-store', expectedStatus: 200 });
    });

    await waitFor(() => {
      expect(screen.getByText('Движения денег')).toBeTruthy();
    });
    expect(screen.getByText('Не удалось загрузить движения денег по счёту. Повторите загрузку.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Повторить загрузку движений денег' })).toBeTruthy();
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
      expect((screen.getByLabelText('Цена') as HTMLInputElement).value).toBe('15 000,00');
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
      expect((screen.getAllByTestId('invoice-line-item-price-input')[0] as HTMLInputElement).value).toBe('15 000,00');
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
      expect((screen.getAllByTestId('invoice-line-item-price-input')[0] as HTMLInputElement).value).toBe('15 000,00');
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

    expect(onOpenPayment).toHaveBeenCalledWith(881, 'Income');
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
  
  describe('invoice case creation owner', () => {
    let session: ReturnType<typeof documentCopyTestSession>;
    beforeEach(() => { session = documentCopyTestSession(); });
    afterEach(() => session.restore());
    const propsFor = () => ({ open: true, mode: 'create' as const, invoice: null, defaultClientId: 1,
      clients: [createTestClient(1, 'Case owner'), createTestClient(2, 'Other case owner')], responsibles: [], lookupsLoading: false,
      submitting: false, onClose: vi.fn(), onSubmit: vi.fn().mockResolvedValue(undefined), onClientCaseCreated: vi.fn(), relatedPayments: [] });
    const createdCase = (overrides: Partial<ClientCase> = {}) => ({ id: 440, clientId: 1, legalEntityId: null, title: 'Owned new case', status: 'Open', createdAt: '2026-10-31T01:00:00Z', ...overrides } as ClientCase);
    const caseSave = () => caseModalPropsSpy.mock.lastCall![0].onSave as (patch: Partial<ClientCase>) => Promise<void>;
    const openCase = async () => { fireEvent.click(screen.getByRole('button', { name: 'Создать дело' })); await screen.findByTestId('case-modal'); return caseSave(); };

    it('case read owner: an old session cannot populate the open picker', async () => {
      const pending = createDeferred<ClientCase[]>(); vi.mocked(apiService.getCases).mockReturnValue(pending.promise);
      render(<InvoiceModal {...propsFor()} />); fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' }));
      await waitFor(() => expect(apiService.getCases).toHaveBeenCalledOnce()); session.replace('actor');
      await act(async () => pending.resolve([createdCase({ title: 'Private old session case' })]));
      expect(screen.queryByText('Private old session case')).toBeNull();
    });

    it.each(['login', 'logout'] as const)('case read owner: ignores a pending %s success without a render', async change => {
      const pending = createDeferred<ClientCase[]>(); vi.mocked(apiService.getCases).mockReturnValue(pending.promise);
      render(<InvoiceModal {...propsFor()} />); fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' })); await waitFor(() => expect(apiService.getCases).toHaveBeenCalledOnce()); session.replace(change);
      await act(async () => pending.resolve([createdCase({ title: 'Private session list' })])); expect(screen.queryByText('Private session list')).toBeNull();
    });

    it('case read owner: never reveals an old server error in another session', async () => {
      const pending = createDeferred<ClientCase[]>(); vi.mocked(apiService.getCases).mockReturnValue(pending.promise); render(<InvoiceModal {...propsFor()} />);
      fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' })); await waitFor(() => expect(apiService.getCases).toHaveBeenCalledOnce()); session.replace('actor');
      await act(async () => pending.reject(new Error('Private old case error'))); expect(screen.queryByText('Private old case error')).toBeNull();
    });

    it.each(['company', 'invoice', 'mode', 'closed', 'reopen', 'presentation', 'permission', 'permission-aba', 'visibility', 'unmount'] as const)('case read owner: pending list cannot follow %s', async change => {
      const pending = createDeferred<ClientCase[]>(); vi.mocked(apiService.getCases).mockReturnValueOnce(pending.promise).mockResolvedValue([]);
      const props = propsFor(); const view = render(<InvoiceModal {...props} />); fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' })); await waitFor(() => expect(apiService.getCases).toHaveBeenCalledOnce());
      if (change === 'company') view.rerender(<InvoiceModal {...props} defaultClientId={2} />);
      if (change === 'invoice') view.rerender(<InvoiceModal {...props} invoice={{ id: 92, clientId: 1, type: 'Income', lineItems: [] } as unknown as Invoice} />);
      if (change === 'mode') view.rerender(<InvoiceModal {...props} mode="edit" />);
      if (change === 'closed' || change === 'reopen') { view.rerender(<InvoiceModal {...props} open={false} />); if (change === 'reopen') view.rerender(<InvoiceModal {...props} />); }
      if (change === 'presentation') view.rerender(<InvoiceModal {...props} presentation="page" />);
      if (change === 'permission' || change === 'permission-aba' || change === 'visibility') {
        permissionState.value = { ...defaultRolePermissions, cases: { ...defaultRolePermissions.cases, ...(change === 'visibility' ? { viewScope: 'own' } : { canView: false }) } };
        view.rerender(<InvoiceModal {...props} />); if (change === 'permission-aba') { permissionState.value = null; view.rerender(<InvoiceModal {...props} />); }
      }
      if (change === 'unmount') view.unmount();
      await act(async () => pending.resolve([createdCase({ title: 'Private context case' })]));
      const picker = screen.queryByRole('button', { name: 'Выбрать дело' }) as HTMLButtonElement | null; if (picker && !picker.disabled) fireEvent.click(picker);
      expect(screen.queryByText('Private context case')).toBeNull();
    });

    it('case read owner: clears cached names on revocation and requests fresh data on restoration', async () => {
      vi.mocked(apiService.getCases).mockResolvedValueOnce([createdCase({ title: 'Previously visible case' })]).mockResolvedValue([createdCase({ id: 441, title: 'Newly visible case' })]);
      const props = propsFor(); const view = render(<InvoiceModal {...props} />); fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' })); await screen.findByText('Previously visible case');
      permissionState.value = { ...defaultRolePermissions, cases: { ...defaultRolePermissions.cases, canView: false } }; view.rerender(<InvoiceModal {...props} />);
      expect(screen.queryByText('Previously visible case')).toBeNull(); expect(screen.getByRole('button', { name: 'Выбрать дело' })).toHaveProperty('disabled', true); expect(apiService.getCases).toHaveBeenCalledOnce();
      permissionState.value = null; view.rerender(<InvoiceModal {...props} />); fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' })); await screen.findByText('Newly visible case');
      expect(screen.queryByText('Previously visible case')).toBeNull(); expect(apiService.getCases).toHaveBeenCalledTimes(2);
    });

    it('case read owner: token renewal preserves a current read without another request', async () => {
      const pending = createDeferred<ClientCase[]>(); vi.mocked(apiService.getCases).mockReturnValue(pending.promise); const props = propsFor(); const view = render(<InvoiceModal {...props} />);
      fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' })); await waitFor(() => expect(apiService.getCases).toHaveBeenCalledOnce()); session.refresh(); view.rerender(<InvoiceModal {...props} />);
      await act(async () => pending.resolve([createdCase({ title: 'Current read case' })])); expect(await screen.findByText('Current read case')).toBeTruthy(); expect(apiService.getCases).toHaveBeenCalledOnce();
    });

    it.each(['session', 'accounts', 'cases'] as const)('case read owner: initial %s denial sends no case request', async denial => {
      if (denial === 'session') session.replace('logout');
      if (denial === 'accounts') permissionState.value = { ...defaultRolePermissions, accounts: { ...defaultRolePermissions.accounts, canView: false, canCreate: false } };
      if (denial === 'cases') permissionState.value = { ...defaultRolePermissions, cases: { ...defaultRolePermissions.cases, canView: false } };
      render(<InvoiceModal {...propsFor()} />); expect(screen.getByRole('button', { name: 'Выбрать дело' })).toHaveProperty('disabled', true); expect(apiService.getCases).not.toHaveBeenCalled();
    });

    it.each([{ value: [{ id: 0, clientId: 1 }] }, { value: [{ id: 445, clientId: 2, title: 'Foreign private case' }] }, { value: { items: [] } }])('case read owner: rejects malformed or foreign list %j', async ({ value }) => {
      vi.mocked(apiService.getCases).mockResolvedValue(value as unknown as ClientCase[]); render(<InvoiceModal {...propsFor()} />); fireEvent.click(screen.getByRole('button', { name: 'Выбрать дело' }));
      expect(await screen.findByText(/Не удалось подтвердить принадлежность списка дел/)).toBeTruthy(); expect(screen.queryByText('Foreign private case')).toBeNull();
    });

    it('case read owner: a saved case link survives denied lookup without being rewritten', async () => {
      permissionState.value = { ...defaultRolePermissions, cases: { ...defaultRolePermissions.cases, canView: false } }; const props = propsFor();
      render(<InvoiceModal {...props} mode="edit" invoice={{ id: 91, clientId: 1, number: 'SAVED-LINK', date: '2026-10-31', type: 'Income', status: 'Pending', amount: 0, clientCaseId: 741, clientCaseTitle: 'Saved invoice case', lineItems: [] } as unknown as Invoice} />);
      fireEvent.submit(document.getElementById('invoice-edit-form')!); await waitFor(() => expect(props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ clientCaseIds: [741] }))); expect(apiService.getCases).not.toHaveBeenCalled();
    });

    it('case creation owner: late response cannot enter a newly opened invoice', async () => {
      const pending = createDeferred<ClientCase>(); vi.mocked(apiService.createCase).mockReturnValue(pending.promise);
      const props = propsFor(); const view = render(<InvoiceModal {...props} />); const save = await openCase();
      let result!: Promise<void>; act(() => { result = save({ title: 'Owned new case' }); });
      view.rerender(<InvoiceModal {...props} defaultClientId={2} />);
      await act(async () => { pending.resolve(createdCase()); await result.catch(() => undefined); });
      expect(props.onClientCaseCreated).not.toHaveBeenCalled(); expect(screen.queryByText('Owned new case')).toBeNull();
    });
    it.each(['closed', 'reopened', 'mode', 'invoice', 'presentation', 'unmount', 'case-permission', 'invoice-permission', 'permission-aba'] as const)('case creation owner: discards a pending response after %s', async change => {
      const pending = createDeferred<ClientCase>(); vi.mocked(apiService.createCase).mockReturnValue(pending.promise);
      const props = propsFor(); const view = render(<InvoiceModal {...props} />); const save = await openCase();
      let result!: Promise<void>; act(() => { result = save({ title: 'Owned new case' }); });
      if (change === 'closed' || change === 'reopened') { view.rerender(<InvoiceModal {...props} open={false} />); if (change === 'reopened') view.rerender(<InvoiceModal {...props} />); }
      if (change === 'mode') view.rerender(<InvoiceModal {...props} mode="edit" />);
      if (change === 'invoice') view.rerender(<InvoiceModal {...props} invoice={{ id: 92, clientId: 1, type: 'Income', lineItems: [] } as unknown as Invoice} />);
      if (change === 'presentation') view.rerender(<InvoiceModal {...props} presentation="page" />);
      if (change === 'unmount') view.unmount();
      if (change === 'case-permission' || change === 'permission-aba') permissionState.value = { ...defaultRolePermissions, cases: { ...defaultRolePermissions.cases, canCreate: false } };
      if (change === 'invoice-permission') permissionState.value = { ...defaultRolePermissions, accounts: { ...defaultRolePermissions.accounts, canCreate: false } };
      if (change.includes('permission')) { view.rerender(<InvoiceModal {...props} />); if (change === 'permission-aba') { permissionState.value = null; view.rerender(<InvoiceModal {...props} />); } }
      await act(async () => { pending.resolve(createdCase()); await expect(result).rejects.toBeInstanceOf(StaleUiOperationError); });
      expect(props.onClientCaseCreated).not.toHaveBeenCalled(); expect(screen.queryByText('Owned new case')).toBeNull(); expect(toastErrorMock).not.toHaveBeenCalled();
    });

    it.each(['login', 'actor', 'logout'] as const)('case creation owner: rejects %s response and old dispatch without a render', async change => {
      const pending = createDeferred<ClientCase>(); vi.mocked(apiService.createCase).mockReturnValue(pending.promise);
      const props = propsFor(); render(<InvoiceModal {...props} />); const save = await openCase();
      let result!: Promise<void>; act(() => { result = save({ title: 'Owned new case' }); }); session.replace(change);
      await expect(save({ title: 'Second case' })).rejects.toBeInstanceOf(StaleUiOperationError);
      await act(async () => { pending.resolve(createdCase()); await expect(result).rejects.toBeInstanceOf(StaleUiOperationError); });
      expect(apiService.createCase).toHaveBeenCalledOnce(); expect(props.onClientCaseCreated).not.toHaveBeenCalled();
    });

    it('case creation owner: accepts token refresh, prevents duplicates and preserves the owner payload', async () => {
      const pending = createDeferred<ClientCase>(); vi.mocked(apiService.createCase).mockReturnValue(pending.promise);
      const props = propsFor(); render(<InvoiceModal {...props} />); const save = await openCase();
      let result!: Promise<void>; act(() => { result = save({ title: ' Owned new case ' }); });
      await expect(save({ title: 'Duplicate' })).rejects.toBeInstanceOf(StaleUiOperationError); session.refresh();
      await act(async () => { pending.resolve(createdCase()); await result; });
      expect(apiService.createCase).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ clientId: 1, legalEntityId: null, title: 'Owned new case' }));
      expect(props.onClientCaseCreated).toHaveBeenCalledExactlyOnceWith(createdCase()); expect(await screen.findByText('Owned new case')).toBeTruthy();
    });

    it.each([{ id: 0 }, { clientId: 2 }, { legalEntityId: 9 }] as const)('case creation owner: rejects a malformed or foreign result %j', async wrong => {
      vi.mocked(apiService.createCase).mockResolvedValue(createdCase(wrong)); const props = propsFor(); render(<InvoiceModal {...props} />); const save = await openCase();
      await act(async () => { await expect(save({ title: 'Owned new case' })).rejects.toThrow('принадлежность'); });
      expect(props.onClientCaseCreated).not.toHaveBeenCalled(); expect(screen.queryByText('Owned new case')).toBeNull(); expect(screen.getByTestId('case-modal')).toBeTruthy();
    });

    it('case creation owner: old finalization and close cannot unlock or close the next attempt', async () => {
      const first = createDeferred<ClientCase>(); const next = createDeferred<ClientCase>(); vi.mocked(apiService.createCase).mockReturnValueOnce(first.promise).mockReturnValueOnce(next.promise);
      const props = propsFor(); render(<InvoiceModal {...props} />); const oldSave = await openCase(); const oldClose = caseModalPropsSpy.mock.lastCall![0].onClose as () => void;
      let result!: Promise<void>; act(() => { result = oldSave({ title: 'Old case' }); }); act(() => oldClose());
      const newSave = await openCase(); let newResult!: Promise<void>; act(() => { newResult = newSave({ title: 'New case' }); });
      await act(async () => { first.resolve(createdCase({ title: 'Old case' })); await expect(result).rejects.toBeInstanceOf(StaleUiOperationError); });
      act(() => oldClose()); expect(screen.getByTestId('case-modal')).toBeTruthy(); await expect(newSave({ title: 'Duplicate' })).rejects.toBeInstanceOf(StaleUiOperationError);
      await act(async () => { next.resolve(createdCase({ id: 441, title: 'New case' })); await newResult; });
      expect(apiService.createCase).toHaveBeenCalledTimes(2); expect(props.onClientCaseCreated).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ id: 441 })); expect(await screen.findByText('New case')).toBeTruthy();
    });

    it('case creation owner: current failure preserves the editor and allows a deliberate retry', async () => {
      vi.mocked(apiService.createCase).mockRejectedValueOnce(new Error('Case creation refused')).mockResolvedValueOnce(createdCase()); const props = propsFor(); render(<InvoiceModal {...props} />); const save = await openCase();
      await act(async () => { await expect(save({ title: 'Owned new case' })).rejects.toThrow('Case creation refused'); }); expect(screen.getByTestId('case-modal')).toBeTruthy(); expect(props.onClientCaseCreated).not.toHaveBeenCalled();
      await act(async () => { await caseSave()({ title: 'Owned new case' }); }); expect(apiService.createCase).toHaveBeenCalledTimes(2); expect(props.onClientCaseCreated).toHaveBeenCalledOnce();
    });

    it('case creation owner: a late failure is not exposed in the new dialog', async () => {
      const pending = createDeferred<ClientCase>(); vi.mocked(apiService.createCase).mockReturnValue(pending.promise); const props = propsFor(); const view = render(<InvoiceModal {...props} />); const save = await openCase();
      let result!: Promise<void>; act(() => { result = save({ title: 'Owned new case' }); }); view.rerender(<InvoiceModal {...props} defaultClientId={2} />); await openCase();
      await act(async () => { pending.reject(new Error('Private old server failure')); await expect(result).rejects.toBeInstanceOf(StaleUiOperationError); }); expect(screen.queryByText('Private old server failure')).toBeNull(); expect(screen.getByTestId('case-modal')).toBeTruthy();
    });

    it.each(['case', 'invoice'] as const)('case creation owner: current %s denial disables creation and blocks a retained handler', async area => {
      const props = propsFor(); const view = render(<InvoiceModal {...props} />); const save = await openCase();
      permissionState.value = area === 'case' ? { ...defaultRolePermissions, cases: { ...defaultRolePermissions.cases, canCreate: false } } : { ...defaultRolePermissions, accounts: { ...defaultRolePermissions.accounts, canCreate: false } };
      view.rerender(<InvoiceModal {...props} />); expect(screen.getByRole('button', { name: 'Создать дело' })).toHaveProperty('disabled', true); expect(screen.queryByTestId('case-modal')).toBeNull();
      await expect(save({ title: 'Denied' })).rejects.toBeInstanceOf(StaleUiOperationError); expect(apiService.createCase).not.toHaveBeenCalled();
    });

    it.each(['success', 'failure'] as const)('case creation list: an older list %s cannot erase a confirmed new case', async outcome => {
      const list = createDeferred<ClientCase[]>(); vi.mocked(apiService.getCases).mockReturnValue(list.promise); vi.mocked(apiService.createCase).mockResolvedValue(createdCase());
      const props = propsFor(); render(<InvoiceModal {...props} />); await waitFor(() => expect(apiService.getCases).toHaveBeenCalledOnce()); const save = await openCase();
      await act(async () => { await save({ title: 'Owned new case' }); }); expect(await screen.findByText('Owned new case')).toBeTruthy();
      await act(async () => { if (outcome === 'success') list.resolve([]); else list.reject(new Error('Old private list error')); });
      expect(screen.getByText('Owned new case')).toBeTruthy(); expect(screen.queryByText('Old private list error')).toBeNull(); expect(props.onClientCaseCreated).toHaveBeenCalledOnce();
      fireEvent.change(screen.getByLabelText(/Номер сч/i), { target: { value: 'INV-CURRENT-CASE' } }); fireEvent.submit(document.getElementById('invoice-edit-form')!);
      await waitFor(() => expect(props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ clientCaseIds: [440] })));
    });

    it('case creation owner extra: accepts a current legal-entity expense case in StrictMode', async () => {
      const props = propsFor(); vi.mocked(apiService.createCase).mockResolvedValue(createdCase({ clientId: null, legalEntityId: 9 }));
      render(<StrictMode><InvoiceModal {...props} defaultClientId={undefined} defaultType="Expense" defaultLegalEntityId={9} legalEntities={[{ id: 9, shortName: 'Legal expense owner' } as LegalEntitySummary]} /></StrictMode>);
      const save = await openCase(); await act(async () => { await save({ title: 'Owned new case' }); });
      expect(apiService.createCase).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ clientId: null, legalEntityId: 9 })); expect(props.onClientCaseCreated).toHaveBeenCalledOnce(); expect(await screen.findByText('Owned new case')).toBeTruthy();
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

  it('submits the chosen own company and branch atomically with a new income invoice', async () => {
    vi.mocked(apiService.getOwnCompanyOptions).mockResolvedValueOnce([
      { id: 41, shortName: 'Своя компания', isDefault: true, branches: [{ id: 42, name: 'Офис', kind: 'Office' }] },
    ]);
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<InvoiceModal open mode="create" invoice={null} onClose={vi.fn()} onSubmit={onSubmit} submitting={false}
      clients={[createTestClient(1, 'Контрагент')]} responsibles={[]} lookupsLoading={false} defaultClientId={1} relatedPayments={[]} />);

    fireEvent.change(await screen.findByLabelText('Подразделение-продавец'), { target: { value: '42' } });
    fireEvent.change(screen.getByLabelText(/Номер сч[её]та/i), { target: { value: 'INV-SELLER-1' } });
    fireEvent.submit(document.getElementById('invoice-edit-form') as HTMLFormElement);

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0]).toMatchObject({ number: 'INV-SELLER-1', type: 'Income', sellerCompanyId: 41, sellerBranchId: 42 });
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
    expect(onSubmit.mock.calls[0][0]).not.toHaveProperty('sellerCompanyId');
    expect(onSubmit.mock.calls[0][0]).not.toHaveProperty('sellerBranchId');
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

  it.each(['allowed', 'export', 'disk-view', 'disk-create'] as const)('uses invoice image defaults for Yandex Disk and respects %s', async access => {
    permissionState.value = { ...defaultRolePermissions,
      accounts: { ...defaultRolePermissions.accounts, canView: true, canExport: access !== 'export' },
      yandexDisk: { ...defaultRolePermissions.yandexDisk, canView: access !== 'disk-view', canCreate: access !== 'disk-create' } };
    const invoice = { id: 660, number: '660', date: '2026-04-08', dueDate: '2026-05-07', type: 'Income', amount: 100, status: 'Pending', lineItems: [] } as unknown as Invoice;
    render(<InvoiceModal open mode="edit" presentation="page" invoice={invoice} onClose={vi.fn()} onSubmit={vi.fn()} submitting={false} clients={[]} responsibles={[]} lookupsLoading={false} relatedPayments={[]} />);
    const button = await screen.findByRole('button', { name: 'Я.Диск' });
    expect((button as HTMLButtonElement).disabled).toBe(access !== 'allowed');
    fireEvent.click(button);
    if (access === 'allowed') await waitFor(() => expect(apiService.uploadInvoiceDocumentToYandexDisk).toHaveBeenCalledExactlyOnceWith(660, 'pdf'));
    else expect(apiService.uploadInvoiceDocumentToYandexDisk).not.toHaveBeenCalled();
  });

  describe('canonical delivery image defaults', () => {
    it.each(['Мессенджер', 'Э-подпись'])('does not send from %s after a rejected invoice PDF and permits only an explicit retry', async channel => {
      Object.assign(printAuthState.user.role, { name: 'admin' });
      const pdf = configureDocumentDeliveryTestApi('invoice', 660, 11);
      const download = vi.mocked(apiService.downloadInvoiceDocument); download.mockClear(); download.mockRejectedValueOnce(new Error('PDF недоступен')).mockResolvedValueOnce(pdf);
      const invoice = { id: 660, clientId: 11, number: '660', date: '2026-04-08', type: 'Income', amount: 100, status: 'Pending', lineItems: [] } as unknown as Invoice;
      render(<InvoiceModal open mode="edit" presentation="page" invoice={invoice} onClose={vi.fn()} onSubmit={vi.fn()} submitting={false} clients={[]} responsibles={[]} lookupsLoading={false} relatedPayments={[]} />);
      fireEvent.click(await screen.findByRole('button', { name: channel }));
      if (channel === 'Мессенджер') { await screen.findAllByText(/Получатель PDF/); fireEvent.click(screen.getByLabelText(/Подтверждаю выбранный документ/)); }
      else { fireEvent.click(await screen.findByText('Подписант PDF')); fireEvent.click(screen.getByLabelText(/Подтверждаю выбранную неизменяемую PDF-версию/)); }
      const name = channel === 'Мессенджер' ? 'Подготовить и отправить' : 'Подготовить неизменяемую версию';
      fireEvent.click(screen.getByRole('button', { name })); await screen.findByText('PDF недоступен');
      expect(download).toHaveBeenCalledExactlyOnceWith(660, 'pdf');
      expect(apiService.prepareFinancialDocumentMessengerDelivery).not.toHaveBeenCalled(); expect(apiService.prepareElectronicSignatureEnvelope).not.toHaveBeenCalled(); expect(apiService.queueMessengerDocumentDelivery).not.toHaveBeenCalled();
      fireEvent.click(screen.getByRole('button', { name }));
      await waitFor(() => expect(channel === 'Мессенджер' ? apiService.queueMessengerDocumentDelivery : apiService.prepareElectronicSignatureEnvelope).toHaveBeenCalledOnce());
      expect(download.mock.calls).toEqual([[660, 'pdf'], [660, 'pdf']]);
    });
    it.each(['Мессенджер', 'Э-подпись'])('prepares the invoice PDF for %s without overriding saved image choices', async channel => {
      Object.assign(printAuthState.user.role, { name: 'admin' });
      const pdf = configureDocumentDeliveryTestApi('invoice', 660, 11);
      const download = vi.mocked(apiService.downloadInvoiceDocument); download.mockClear(); download.mockResolvedValueOnce(pdf);
      const invoice = { id: 660, clientId: 11, number: '660', date: '2026-04-08', type: 'Income', amount: 100, status: 'Pending', lineItems: [] } as unknown as Invoice;
      render(<InvoiceModal open mode="edit" presentation="page" invoice={invoice} onClose={vi.fn()} onSubmit={vi.fn()} submitting={false} clients={[]} responsibles={[]} lookupsLoading={false} relatedPayments={[]} />);
      fireEvent.click(await screen.findByRole('button', { name: channel }));
      expect(download).not.toHaveBeenCalled();
      if (channel === 'Мессенджер') {
        await screen.findAllByText(/Получатель PDF/);
        expect((screen.getByRole('button', { name: /Подготовить и отправить/ }) as HTMLButtonElement).disabled).toBe(true);
        fireEvent.click(screen.getByLabelText(/Подтверждаю выбранный документ/));
        fireEvent.click(screen.getByRole('button', { name: /Подготовить и отправить/ }));
        await waitFor(() => expect(apiService.queueMessengerDocumentDelivery).toHaveBeenCalledExactlyOnceWith(102));
        expect(apiService.prepareFinancialDocumentMessengerDelivery).toHaveBeenCalledExactlyOnceWith('invoice', 660, pdf, expect.objectContaining({ recipientBindingId: 8, confirmRecipientAndDocument: true }));
      } else {
        fireEvent.click(await screen.findByText('Подписант PDF'));
        fireEvent.click(screen.getByLabelText(/Подтверждаю выбранную неизменяемую PDF-версию/));
        fireEvent.click(screen.getByRole('button', { name: 'Подготовить неизменяемую версию' }));
        await waitFor(() => expect(apiService.prepareElectronicSignatureEnvelope).toHaveBeenCalledExactlyOnceWith('invoice', 660, pdf, expect.objectContaining({ clientId: 11, signerAccountIds: [7], confirmSnapshotAndSigners: true })));
        expect(apiService.queueMessengerDocumentDelivery).not.toHaveBeenCalled();
      }
      expect(download).toHaveBeenCalledExactlyOnceWith(660, 'pdf');
    });
  });

  describe('delivery ownership', () => {
    it.each(['Мессенджер', 'Э-подпись'].flatMap(channel => ['document', 'user', 'role', 'scope', 'admin'].map(change => ({ channel, change }))))('closes $channel and discards confirmation after $change', async ({ channel, change }) => {
      Object.assign(printAuthState.user.role, { name: 'admin' });
      const current = { ...props(), invoice: { ...props().invoice, clientId: 11 } };
      const view = render(<InvoiceModal {...current} />);
      fireEvent.click(await screen.findByRole('button', { name: channel }));
      const title = channel === 'Мессенджер' ? 'Отправка документа в мессенджер' : 'Электронная подпись';
      expect(await screen.findByLabelText(title)).toBeTruthy();
      if (change === 'admin') Object.assign(printAuthState.user.role, { name: 'employee' });
      view.rerender(<InvoiceModal {...changeOwner(change, current)} />);
      await waitFor(() => expect(screen.queryByLabelText(title)).toBeNull());
      Object.assign(printAuthState.user, { id: 7, role: { id: 1, name: 'admin' } });
      permissionState.value!.accounts.viewScope = 'all';
      view.rerender(<InvoiceModal {...current} />);
      expect(screen.queryByLabelText(title)).toBeNull();
    });
    const props = () => ({ open: true, mode: 'edit' as const, presentation: 'page' as const,
      invoice: { id: 660, number: '660', date: '2026-04-08', dueDate: '2026-05-07', type: 'Income', amount: 100, status: 'Pending', lineItems: [] } as unknown as Invoice,
      onClose: vi.fn(), onSubmit: vi.fn(), submitting: false, clients: [], responsibles: [], lookupsLoading: false, relatedPayments: [] });
    beforeEach(() => {
      permissionState.value = { ...defaultRolePermissions, accounts: { ...defaultRolePermissions.accounts, canView: true, canExport: true, viewScope: 'all' }, yandexDisk: { ...defaultRolePermissions.yandexDisk, canView: true, canCreate: true } };
      vi.mocked(apiService.sendInvoiceByEmail).mockReset().mockResolvedValue({ sent: true, recipient: 'client@example.test', fileName: 'invoice.docx', format: 'word' });
    });
    const changeOwner = (change: string, current: ReturnType<typeof props>) => {
      if (change === 'user') printAuthState.user = { id: 8, role: { id: 1 } };
      if (change === 'role') printAuthState.user = { id: 7, role: { id: 2 } };
      if (change === 'export') permissionState.value!.accounts.canExport = false;
      if (change === 'view') permissionState.value!.accounts.canView = false;
      if (change === 'scope') permissionState.value!.accounts.viewScope = 'own';
      if (change === 'close') return { ...current, open: false };
      if (change === 'document') return { ...current, invoice: { ...current.invoice, id: 661, number: '661' } };
      return { ...current };
    };
    it.each(['document', 'user', 'role', 'export', 'view', 'scope', 'close', 'unmount'] as const)('rejects a pending email confirmation after %s', async change => {
      const pending = createDeferred<boolean>(); confirmMock.mockReturnValueOnce(pending.promise);
      const current = props(); const view = render(<InvoiceModal {...current} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Отправить' }));
      expect(confirmMock).toHaveBeenCalledOnce(); const next = changeOwner(change, current);
      if (change === 'unmount') view.unmount(); else view.rerender(<InvoiceModal {...next} />);
      await act(async () => pending.resolve(true));
      expect(apiService.sendInvoiceByEmail).not.toHaveBeenCalled(); expect(toastSuccessMock).not.toHaveBeenCalled();
    });
    it.each(['email-success', 'email-error', 'disk-success', 'disk-error'] as const)('ignores %s after a scope change', async action => {
      const pending = createDeferred<never>(); confirmMock.mockResolvedValue(true);
      const send = vi.mocked(action.startsWith('email') ? apiService.sendInvoiceByEmail : apiService.uploadInvoiceDocumentToYandexDisk);
      send.mockReturnValueOnce(pending.promise);
      const current = props(); const view = render(<InvoiceModal {...current} />);
      fireEvent.click(await screen.findByRole('button', { name: action.startsWith('email') ? 'Отправить' : 'Я.Диск' }));
      await waitFor(() => expect(send).toHaveBeenCalledOnce());
      view.rerender(<InvoiceModal {...changeOwner('scope', current)} />);
      await act(async () => { if (action.endsWith('error')) pending.reject(new Error('stale delivery failure')); else pending.resolve({ path: '/old' } as never); });
      expect(toastSuccessMock).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
      expect(screen.queryByText('stale delivery failure')).toBeNull();
    });
    it('sends only the current invoice and serializes email with disk delivery', async () => {
      const pending = createDeferred<boolean>(); confirmMock.mockReturnValueOnce(pending.promise);
      render(<InvoiceModal {...props()} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Отправить' }));
      const disk = screen.getByRole('button', { name: 'Я.Диск' });
      expect((disk as HTMLButtonElement).disabled).toBe(true); fireEvent.click(disk);
      await act(async () => pending.resolve(true));
      expect(apiService.sendInvoiceByEmail).toHaveBeenCalledExactlyOnceWith(660, { format: 'word', email: undefined });
      expect(apiService.uploadInvoiceDocumentToYandexDisk).not.toHaveBeenCalled();
      expect(toastSuccessMock).toHaveBeenCalledOnce();
    });
  });

  describe('invoice document defaults', () => {
    it.each(['back', 'fallback', 'header', 'cancel'] as const)('invoice exit owner stops deferred printing before the %s callback', async exit => {
      const session = documentCopyTestSession(); const callbacks: Array<() => void> = [];
      const nativeTimeout = window.setTimeout.bind(window);
      const timer = vi.spyOn(window, 'setTimeout').mockImplementation((handler, delay, ...args) => {
        if (delay === 250) { callbacks.push(() => { if (typeof handler === 'function') handler(...args); }); return 0 as unknown as ReturnType<typeof setTimeout>; }
        return nativeTimeout(handler, delay, ...args) as unknown as ReturnType<typeof setTimeout>;
      });
      const leave = vi.fn(() => callbacks.slice().forEach(callback => callback()));
      try {
        const current = { ...props(), onClose: leave, ...(exit === 'back' ? { onBack: leave } : {}) };
        const view = render(<InvoiceModal {...current} />); const dialog = choose('print');
        await waitFor(() => expect((dialog.getByRole('button', { name: 'Печатать' }) as HTMLButtonElement).disabled).toBe(false));
        fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
        const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
        await waitFor(() => expect(popup.document.write).toHaveBeenCalledWith('print copy'));
        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
        if (exit === 'header' || exit === 'cancel') view.rerender(<InvoiceModal {...current} presentation="modal" />);
        fireEvent.click(screen.getByRole('button', { name: exit === 'header' ? 'Закрыть' : exit === 'cancel' ? 'Отмена' : 'Назад' }));
        expect(leave).toHaveBeenCalledOnce(); expect(popup.print).not.toHaveBeenCalled(); expect(popup.close).toHaveBeenCalledOnce();
        expect(apiService.prepareDocumentPrint).toHaveBeenCalledOnce(); expect(current.onSubmit).not.toHaveBeenCalled();
      } finally { timer.mockRestore(); session.restore(); }
    });

    it.each(['success', 'error', 'confirmation'] as const)('invoice exit owner ignores a pending delivery %s after page back', async result => {
      permissionState.value = { ...defaultRolePermissions,
        accounts: { ...defaultRolePermissions.accounts, canView: true, canExport: true, viewScope: 'all' },
        yandexDisk: { ...defaultRolePermissions.yandexDisk, canView: true, canCreate: true } };
      const pending = createDeferred<never>(); const leave = vi.fn();
      if (result === 'confirmation') confirmMock.mockReturnValueOnce(pending.promise);
      else vi.mocked(apiService.uploadInvoiceDocumentToYandexDisk).mockReturnValueOnce(pending.promise);
      render(<InvoiceModal {...props()} onBack={leave} />);
      fireEvent.click(await screen.findByRole('button', { name: result === 'confirmation' ? 'Отправить' : 'Я.Диск' }));
      if (result === 'confirmation') expect(confirmMock).toHaveBeenCalledOnce();
      else await waitFor(() => expect(apiService.uploadInvoiceDocumentToYandexDisk).toHaveBeenCalledOnce());
      fireEvent.click(screen.getByRole('button', { name: 'Назад' })); expect(leave).toHaveBeenCalledOnce();
      await act(async () => { if (result === 'error') pending.reject(new Error('former delivery error')); else pending.resolve((result === 'confirmation' ? true : { path: '/old' }) as never); });
      expect(apiService.sendInvoiceByEmail).not.toHaveBeenCalled(); expect(toastSuccessMock).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
    });

    it.each((['print', 'word'] as const).flatMap(format => (['login', 'refresh'] as const).map(mode => ({ format, mode }))))('copy session expansion handles invoice $format after $mode', async ({ format, mode }) => {
      const session = documentCopyTestSession(); const pending = createDeferred<Blob>();
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      const operation = vi.mocked(format === 'print' ? apiService.prepareDocumentPrint : apiService.downloadInvoiceDocument);
      try {
        operation.mockClear().mockReturnValueOnce(pending.promise);
        render(<InvoiceModal {...props()} />); const dialog = choose(format);
        const label = format === 'print' ? 'Печатать' : 'Скачать Word';
        await waitFor(() => expect((dialog.getByRole('button', { name: label }) as HTMLButtonElement).disabled).toBe(false));
        fireEvent.click(dialog.getByRole('button', { name: label })); expect(operation).toHaveBeenCalledOnce();
        if (mode === 'login') session.replace(); else session.refresh();
        await act(async () => pending.resolve(Object.assign(new Blob(['current copy']), { text: async () => 'current copy' })));
        if (format === 'print') {
          const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
          if (mode === 'login') { expect(popup.close).toHaveBeenCalledOnce(); expect(popup.document.write).not.toHaveBeenCalledWith('current copy'); }
          else expect(popup.document.write).toHaveBeenCalledWith('current copy');
        } else expect(click).toHaveBeenCalledTimes(mode === 'login' ? 0 : 1);
      } finally { click.mockRestore(); session.restore(); }
    });

    it('copy session expansion rejects a login change while reading the invoice HTML body', async () => {
      const session = documentCopyTestSession(); const body = createDeferred<string>();
      const text = vi.fn(() => body.promise);
      try {
        vi.mocked(apiService.prepareDocumentPrint).mockResolvedValueOnce(Object.assign(new Blob(['body']), { text }));
        render(<InvoiceModal {...props()} />); const dialog = choose('print');
        await waitFor(() => expect((dialog.getByRole('button', { name: 'Печатать' }) as HTMLButtonElement).disabled).toBe(false));
        fireEvent.click(dialog.getByRole('button', { name: 'Печатать' })); await waitFor(() => expect(text).toHaveBeenCalledOnce());
        const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
        session.replace(); await act(async () => body.resolve('stale secret body'));
        expect(popup.document.write).not.toHaveBeenCalledWith('stale secret body'); expect(popup.close).toHaveBeenCalledOnce();
      } finally { session.restore(); }
    });

    it.each(['login', 'scope', 'refresh'] as const)('copy session expansion checks deferred invoice printing on %s', async mode => {
      const session = documentCopyTestSession(); const callbacks: Array<() => void> = [];
      const nativeTimeout = window.setTimeout.bind(window);
      const timer = vi.spyOn(window, 'setTimeout').mockImplementation((handler, delay, ...args) => {
        if (delay === 250) { callbacks.push(() => { if (typeof handler === 'function') handler(...args); }); return 0 as unknown as ReturnType<typeof setTimeout>; }
        return nativeTimeout(handler, delay, ...args) as unknown as ReturnType<typeof setTimeout>;
      });
      try {
        const current = props(); const view = render(<InvoiceModal {...current} />); const dialog = choose('print');
        await waitFor(() => expect((dialog.getByRole('button', { name: 'Печатать' }) as HTMLButtonElement).disabled).toBe(false));
        fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
        const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
        await waitFor(() => expect(popup.document.write).toHaveBeenCalledWith('print copy'));
        expect(callbacks.length).toBeGreaterThan(0);
        if (mode === 'login') session.replace();
        if (mode === 'refresh') session.refresh();
        if (mode === 'scope') view.rerender(<InvoiceModal {...changeOwner('scope', current)} />);
        act(() => callbacks.slice().forEach(callback => callback()));
        expect(popup.print).toHaveBeenCalledTimes(mode === 'refresh' ? 1 : 0);
        expect(popup.close).toHaveBeenCalledTimes(mode === 'refresh' ? 0 : 1);
      } finally { timer.mockRestore(); session.restore(); }
    });

    it('copy session expansion closes the invoice dialog on a same-user replacement login', async () => {
      const session = documentCopyTestSession();
      try {
        const current = props(); const view = render(<InvoiceModal {...current} />); choose('pdf');
        session.replace(); view.rerender(<InvoiceModal {...current} />);
        expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      } finally { session.restore(); }
    });

    it('copy session expansion refuses to open invoice defaults from a former login', async () => {
      const session = documentCopyTestSession();
      try {
        render(<InvoiceModal {...props()} />); session.replace();
        fireEvent.click(screen.getByRole('button', { name: /^Скачать/ }));
        const choice = screen.queryByRole('button', { name: 'Счет: PDF по настройкам' });
        if (choice) fireEvent.click(choice);
        expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
        expect(apiService.getDocumentImagePrintDefaults).not.toHaveBeenCalled();
      } finally { session.restore(); }
    });
    it('copy session fences a late invoice PDF before auth rerender', async () => {
      const session = documentCopyTestSession(); const pending = createDeferred<Blob>();
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      try {
        vi.mocked(apiService.downloadInvoiceDocument).mockClear().mockReturnValueOnce(pending.promise);
        render(<InvoiceModal {...props()} />); const dialog = choose('pdf');
        await waitFor(() => expect((dialog.getByRole('button', { name: 'Скачать PDF' }) as HTMLButtonElement).disabled).toBe(false));
        fireEvent.click(dialog.getByRole('button', { name: 'Скачать PDF' }));
        expect(apiService.downloadInvoiceDocument).toHaveBeenCalledOnce(); session.replace();
        await act(async () => pending.resolve(new Blob(['late copy'])));
        expect(click).not.toHaveBeenCalled();
      } finally { click.mockRestore(); session.restore(); }
    });
    const templates = [{ label: 'Счет', key: undefined }, { label: 'Счет-договор', key: 'invoice-offer' }, { label: 'Заказ', key: 'invoice-order' }];
    const props = () => ({ open: true, mode: 'edit' as const, presentation: 'page' as const,
      invoice: { id: 660, number: '660', date: '2026-04-08', dueDate: '2026-05-07', type: 'Income', amount: 100, status: 'Pending', lineItems: [] } as unknown as Invoice,
      onClose: vi.fn(), onSubmit: vi.fn(), submitting: false, clients: [], responsibles: [], lookupsLoading: false, relatedPayments: [] });
    const choose = (format: 'print' | 'pdf' | 'word', index = 0) => {
      fireEvent.click(screen.getByRole('button', { name: format === 'print' ? /^Печать/ : /^Скачать/ }));
      if (format === 'print') fireEvent.click(screen.getAllByRole('button', { name: 'По настройкам документа' })[index]);
      else fireEvent.click(screen.getByRole('button', { name: `${templates[index].label}: ${format === 'pdf' ? 'PDF' : 'Word'} по настройкам` }));
      return within(screen.getByRole('dialog', { name: 'Изображения в документе' }));
    };
    const changeOwner = (action: string, current: ReturnType<typeof props>) => {
      if (['revoke', 'hide', 'scope'].includes(action)) permissionState.value = { ...defaultRolePermissions,
        accounts: { ...defaultRolePermissions.accounts, canExport: action !== 'revoke', canView: action !== 'hide', viewScope: action === 'scope' ? 'own' : 'all' } };
      if (action === 'user') printAuthState.user = { id: 8, role: { id: 1 } };
      if (action === 'role') printAuthState.user = { id: 7, role: { id: 2 } };
      if (action === 'close') return { ...current, open: false };
      if (action === 'change') return { ...current, invoice: { ...current.invoice, id: 661, number: '661' } };
      return { ...current };
    };

    it.each(templates.flatMap((template, index) => (['print', 'pdf', 'word'] as const).map(format => ({ template, index, format }))))('loads saved choices for $template.label $format and applies edits only to its copy', async ({ template, index, format }) => {
      const choices = { includeLogo: false, includeSeal: true, includeSignature: false };
      vi.mocked(apiService.getDocumentImagePrintDefaults).mockResolvedValueOnce(choices);
      const download = vi.mocked(apiService.downloadInvoiceDocument); download.mockClear();
      if (format !== 'print') download.mockResolvedValueOnce(Object.assign(new Blob(['print copy']), { text: async () => 'print copy' }));
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      try {
        const current = props(); render(<InvoiceModal {...current} />);
        const dialog = choose(format, index);
        const label = format === 'print' ? 'Печатать' : `Скачать ${format === 'pdf' ? 'PDF' : 'Word'}`;
        await waitFor(() => expect((dialog.getByRole('button', { name: label }) as HTMLButtonElement).disabled).toBe(false));
        expect(apiService.getDocumentImagePrintDefaults).toHaveBeenCalledExactlyOnceWith('invoices', 660, expect.any(AbortSignal));
        expect(download).not.toHaveBeenCalled();
        expect(apiService.prepareDocumentPrint).not.toHaveBeenCalled();
        if (format === 'print') expect(dialog.getByText(/Отмена в системном окне печати/)).toBeTruthy();
        expect((dialog.getByLabelText('Печать') as HTMLInputElement).checked).toBe(true);
        fireEvent.click(dialog.getByLabelText('Подпись')); fireEvent.click(dialog.getByRole('button', { name: label }));
        const options = { includeLogo: false, includeSeal: true, includeSignature: true, ...(template.key ? { templateKey: template.key } : {}), signal: expect.any(AbortSignal) };
        if (format === 'print') {
          await waitFor(() => expect(apiService.prepareDocumentPrint).toHaveBeenCalledExactlyOnceWith('invoices', 660, options));
          expect(download).not.toHaveBeenCalled();
        } else await waitFor(() => expect(download).toHaveBeenCalledExactlyOnceWith(660, format, options));
        await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
        expect(current.onSubmit).not.toHaveBeenCalled(); expect(choices.includeSignature).toBe(false);
        if (format === 'print') expect(vi.mocked(window.open).mock.results.at(-1)!.value.document.write).toHaveBeenCalledWith('print copy');
      } finally { click.mockRestore(); }
    });

    it.each(['change', 'close', 'revoke', 'hide', 'user', 'role', 'scope'])('aborts defaults and ignores the former owner on %s', async action => {
      let finish!: (value: { includeLogo: boolean; includeSeal: boolean; includeSignature: boolean }) => void;
      vi.mocked(apiService.getDocumentImagePrintDefaults).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      vi.mocked(apiService.downloadInvoiceDocument).mockClear();
      const current = props(); const view = render(<InvoiceModal {...current} />); choose('pdf');
      const signal = vi.mocked(apiService.getDocumentImagePrintDefaults).mock.calls[0][2]!;
      view.rerender(<InvoiceModal {...changeOwner(action, current)} />);
      expect(signal.aborted).toBe(true);
      await act(async () => finish({ includeLogo: true, includeSeal: true, includeSignature: true }));
      expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      expect(apiService.downloadInvoiceDocument).not.toHaveBeenCalled(); expect(current.onSubmit).not.toHaveBeenCalled();
    });

    it.each(['revoke', 'hide', 'user', 'role', 'scope'])('rejects a late file after print authority changes on %s', async action => {
      let finish!: (value: Blob) => void;
      const download = vi.mocked(apiService.downloadInvoiceDocument); download.mockClear();
      download.mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
      try {
        const current = props(); const view = render(<InvoiceModal {...current} />); const dialog = choose('pdf');
        await waitFor(() => expect((dialog.getByRole('button', { name: 'Скачать PDF' }) as HTMLButtonElement).disabled).toBe(false));
        fireEvent.click(dialog.getByRole('button', { name: 'Скачать PDF' }));
        await waitFor(() => expect(download).toHaveBeenCalledTimes(1));
        const signal = download.mock.calls[0][2]!.signal!;
        view.rerender(<InvoiceModal {...changeOwner(action, current)} />);
        expect(signal.aborted).toBe(true);
        await act(async () => finish(new Blob(['private former copy'])));
        expect(click).not.toHaveBeenCalled(); expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull();
      } finally { click.mockRestore(); }
    });

    it.each(['change', 'close', 'revoke', 'hide', 'user', 'role', 'scope', 'cancel'])('closes only the pending print window immediately on %s', async action => {
      let finish!: (value: Blob) => void;
      vi.mocked(apiService.prepareDocumentPrint).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      const current = props(); const view = render(<InvoiceModal {...current} />); const dialog = choose('print');
      await waitFor(() => expect((dialog.getByRole('button', { name: 'Печатать' }) as HTMLButtonElement).disabled).toBe(false));
      fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
      await waitFor(() => expect(apiService.prepareDocumentPrint).toHaveBeenCalledTimes(1));
      const signal = vi.mocked(apiService.prepareDocumentPrint).mock.calls[0][2].signal!;
      const popup = vi.mocked(window.open).mock.results.at(-1)!.value as Window;
      const writesBeforeCancellation = vi.mocked(popup.document.write).mock.calls.length;
      if (action === 'cancel') fireEvent.click(dialog.getByRole('button', { name: 'Отмена' }));
      else view.rerender(<InvoiceModal {...changeOwner(action, current)} />);
      expect(signal.aborted).toBe(true); expect(popup.close).toHaveBeenCalled();
      await act(async () => finish(Object.assign(new Blob(['former private print']), { text: async () => 'former private print' })));
      expect(popup.document.write).toHaveBeenCalledTimes(writesBeforeCancellation);
      expect(popup.print).not.toHaveBeenCalled(); expect(current.onSubmit).not.toHaveBeenCalled();
    });

    it('does not prepare a print snapshot if the browser blocks its window', async () => {
      vi.mocked(window.open).mockReturnValueOnce(null);
      const current = props(); render(<InvoiceModal {...current} />); const dialog = choose('print');
      await waitFor(() => expect((dialog.getByRole('button', { name: 'Печатать' }) as HTMLButtonElement).disabled).toBe(false));
      fireEvent.click(dialog.getByRole('button', { name: 'Печатать' }));
      expect((await dialog.findByRole('alert')).textContent).toContain('окно печати');
      expect(apiService.prepareDocumentPrint).not.toHaveBeenCalled();
    });

    it('keeps an explicit old menu choice independent of profile loading', async () => {
      const current = props(); render(<InvoiceModal {...current} />);
      fireEvent.click(screen.getByRole('button', { name: /^Скачать/ }));
      fireEvent.click(screen.getByRole('button', { name: 'Счет: PDF без подписи и печати' }));
      expect(apiService.getDocumentImagePrintDefaults).not.toHaveBeenCalled();
      expect((within(screen.getByRole('dialog', { name: 'Изображения в документе' })).getByLabelText('Логотип') as HTMLInputElement).checked).toBe(true);
      fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    });
  });

  it.each([
    { format: 'pdf', label: 'PDF' },
    { format: 'word', label: 'Word' },
  ])('keeps the $label invoice download alive until the browser consumes it', async ({ format, label }) => {
    const downloadMock = vi.mocked(apiService.downloadInvoiceDocument);
    downloadMock.mockClear();
    const clicks: { connected: boolean; filename: string; href: string }[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      clicks.push({ connected: this.isConnected, filename: this.download, href: this.href });
    });

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
    fireEvent.click(screen.getByRole('button', { name: `Счет: ${label} без подписи и печати` }));
    expect(downloadMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: `Скачать ${label}` }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(551, format, { includeLogo: true, includeSeal: false, includeSignature: false, signal: expect.any(AbortSignal) });
      expect(click).toHaveBeenCalledTimes(1);
    });
    expect(clicks[0]).toMatchObject({ connected: true, href: 'blob:test' });
    expect(clicks[0].filename).toMatch(format === 'pdf' ? /\.pdf$/ : /\.docx$/);
    expect(URL.revokeObjectURL).not.toHaveBeenCalled();
    expect(document.querySelector('a[download]')).toBeNull();
    await waitFor(() => expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test'), { timeout: 2000 });
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
    fireEvent.click(screen.getByRole('button', { name: 'Счет-договор: PDF без подписи и печати' }));
    fireEvent.click(screen.getByRole('button', { name: 'Скачать PDF' }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(553, 'pdf', {
        includeLogo: true, includeSeal: false, includeSignature: false, signal: expect.any(AbortSignal),
        templateKey: 'invoice-offer',
      });
    });

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: /Скачать/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Заказ: PDF без подписи и печати' }));
    fireEvent.click(screen.getByRole('button', { name: 'Скачать PDF' }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(553, 'pdf', {
        includeLogo: true, includeSeal: false, includeSignature: false, signal: expect.any(AbortSignal),
        templateKey: 'invoice-order',
      });
    });

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: /Скачать/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Счет-договор: Word с подписью и печатью' }));
    fireEvent.click(screen.getByRole('button', { name: 'Скачать Word' }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith(553, 'word', {
        includeLogo: true, includeSeal: true, includeSignature: true, signal: expect.any(AbortSignal),
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
      expect(screen.getByLabelText('Сумма позиции').textContent).toBe('7 500,00');
      expect(screen.getAllByText(/7 500,00/).length).toBeGreaterThan(1);
    });
  });

  it.each([
    { quantity: '1.5', price: '10.075' },
    { quantity: '1.50001', price: '10' },
  ])('prevents saving line precision that changes in storage: $quantity × $price', async ({ quantity, price }) => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    const invoice = {
      id: 955, invoiceEntityId: 955, number: 'PRECISION', date: '2026-10-10', amount: 15,
      type: 'Income', status: 'Pending', clientId: 1,
      lineItems: [{ name: 'Service', quantity: 1.5, price: 10, unit: 'шт', vatRate: '20' }],
    } as unknown as Invoice;
    render(<InvoiceModal open mode="edit" invoice={invoice} onClose={vi.fn()} onSubmit={onSubmit}
      submitting={false} clients={[createTestClient(1, 'Client owner')]} responsibles={[]}
      lookupsLoading={false} relatedPayments={[]} />);
    fireEvent.change(screen.getByLabelText('Количество'), { target: { value: quantity } });
    fireEvent.change(screen.getByLabelText('Цена'), { target: { value: price } });
    fireEvent.submit(document.getElementById('invoice-edit-form')!);
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(
      'В цене допускается до двух знаков после запятой, в количестве — до четырёх.', 'Проверьте данные счета',
    ));
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it.each(['change', 'close', 'cancel'] as const)('aborts an image-selected download on invoice %s', async action => {
    const download = vi.mocked(apiService.downloadInvoiceDocument); download.mockClear();
    let finish!: (blob: Blob) => void; let signal: AbortSignal | undefined;
    download.mockImplementationOnce((_id, _format, options) => {
      signal = options?.signal; return new Promise<Blob>(resolve => { finish = resolve; });
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const invoice = { id: 660, number: '660', date: '2026-04-08', dueDate: '2026-05-07', type: 'Income', amount: 100, status: 'Pending', lineItems: [] } as unknown as Invoice;
    const props = { open: true, mode: 'edit' as const, invoice, presentation: 'page' as const, onClose: vi.fn(), onSubmit: vi.fn(), submitting: false, clients: [], responsibles: [], lookupsLoading: false, relatedPayments: [] };
    const view = render(<InvoiceModal {...props} />);
    fireEvent.click(screen.getByRole('button', { name: /Скачать/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Счет: PDF с подписью и печатью' }));
    fireEvent.click(screen.getByLabelText('Логотип')); fireEvent.click(screen.getByLabelText('Печать'));
    fireEvent.click(screen.getByRole('button', { name: 'Скачать PDF' }));
    await waitFor(() => expect(download).toHaveBeenCalledWith(660, 'pdf', { includeLogo: false, includeSeal: false, includeSignature: true, signal: expect.any(AbortSignal) }));
    if (action === 'cancel') fireEvent.click(within(screen.getByRole('dialog', { name: 'Изображения в документе' })).getByRole('button', { name: 'Отмена' }));
    else view.rerender(<InvoiceModal {...props} open={action !== 'close'} invoice={action === 'change' ? { ...invoice, id: 661, number: '661' } : invoice} />);
    await waitFor(() => expect(signal?.aborted).toBe(true));
    await act(async () => finish(new Blob(['late document'])));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
    expect(click).not.toHaveBeenCalled(); expect(download).toHaveBeenCalledTimes(1);
  });

  it('prints the established HTML form with the requested signature and seal mode', async () => {
    const downloadMock = vi.mocked(apiService.prepareDocumentPrint);
    downloadMock.mockClear();
    // JSDOM's Blob lacks text(); this is the synthetic API response, not the print implementation.
    const response = new Blob(['<html><body>Печатный счёт</body></html>'], { type: 'text/html' });
    Object.defineProperty(response, 'text', { value: async () => '<html><body>Печатный счёт</body></html>' });
    downloadMock.mockResolvedValueOnce(response).mockResolvedValueOnce(response);
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

    fireEvent.click(screen.getByRole('button', { name: /^Печать/i }));
    fireEvent.click(screen.getAllByRole('button', { name: /С подписью и печатью/i })[0]);
    expect(downloadMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByLabelText('Печать'));
    fireEvent.click(screen.getByRole('button', { name: 'Печатать' }));

    expect(openMock).toHaveBeenCalled();
    await waitFor(() => {
      expect(downloadMock).toHaveBeenCalledWith('invoices', 552, { includeLogo: true, includeSeal: false, includeSignature: true, signal: expect.any(AbortSignal) });
    });

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Изображения в документе' })).toBeNull());
    fireEvent.click(screen.getByRole('button', { name: /^Печать/i }));
    fireEvent.click(screen.getAllByRole('button', { name: /Без подписи и печати/i })[1]);
    fireEvent.click(screen.getByRole('button', { name: 'Печатать' }));

    await waitFor(() => {
      expect(downloadMock).toHaveBeenLastCalledWith('invoices', 552, {
        includeLogo: true, includeSeal: false, includeSignature: false, signal: expect.any(AbortSignal),
        templateKey: 'invoice-offer',
      });
    });
  });
});
