import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { imageFixtureBytes, imageFixtureFile, installBlobArrayBufferForTests } from './legalEntityImageTestFixtures';

import { LegalEntityModal } from './LegalEntityModal';
import { apiService } from '../../services/api';
import { StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { documentCopyTestSession } from '../../test/documentCopySession';
import { bankConnectionFixture, bankReportFixture, bankVersion, bindingFixture } from '../../test/companyBankBindingFixtures';
import { bindPaymentCreateOwner, createPaymentGuarded, pendingPaymentCreates, openCommittedPaymentCreate, fencePaymentCreateAdoption,
  acknowledgePaymentCreate, PaymentCreateAdoptedError, PaymentCreateRecoveryError, type PaymentCreatePorts, type PaymentAttemptState } from '../../services/paymentCreateAttempts';
import type { MenuSectionPermissions } from '../../types/permissions';
import type {
  ActsSummary,
  Client,
  ClientCase,
  InvoiceSummary,
  Invoice,
  InvoiceInput,
  Act,
  ActInput,
  LegalEntityDadataDetails,
  LegalEntityDetail,
  LegalEntitySummary,
  LegalEntityInput,
  LegalEntitySuggestion,
  Payment,
  PaymentPayload,
  ClientCaseFinanceSummary,
} from '../../types';

let restoreArrayBuffer: () => void;
let baseAuthSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { baseAuthSession = documentCopyTestSession(); });
afterEach(() => baseAuthSession.restore());
beforeAll(() => { restoreArrayBuffer = installBlobArrayBufferForTests(); });
afterAll(() => { restoreArrayBuffer(); });

const {
  confirmMock,
  emptyActsSummary,
  emptyInvoiceSummary,
  paymentModalPropsSpy,
  invoiceModalPropsSpy,
  relatedCasePropsSpy,
  relatedActPropsSpy,
  toastErrorMock,
  toastInfoMock,
  toastSuccessMock,
} = vi.hoisted(() => ({
  confirmMock: vi.fn(),
  emptyActsSummary: {
    created: { amount: 0, count: 0 },
    transferred: { amount: 0, count: 0 },
    signed: { amount: 0, count: 0 },
    terminated: { amount: 0, count: 0 },
    totalAmount: 0,
    totalCount: 0,
  } satisfies ActsSummary,
  emptyInvoiceSummary: {
    total: { amount: 0, count: 0 },
    pending: { amount: 0, count: 0 },
    paid: { amount: 0, count: 0 },
    overdue: { amount: 0, count: 0 },
  } satisfies InvoiceSummary,
  paymentModalPropsSpy: vi.fn(),
  invoiceModalPropsSpy: vi.fn(),
  relatedCasePropsSpy: vi.fn(),
  relatedActPropsSpy: vi.fn(),
  toastErrorMock: vi.fn(),
  toastInfoMock: vi.fn(),
  toastSuccessMock: vi.fn(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => '' }),
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: confirmMock }),
}));

vi.mock('../../hooks/useToast', () => {
  const toast = {
    error: toastErrorMock,
    info: toastInfoMock,
    success: toastSuccessMock,
  };
  return { useToast: () => toast };
});

vi.mock('../../hooks/useDictionaries', () => ({
  useDictionaries: () => ({
    countries: [],
    baseCurrencies: [],
    serviceLanguages: [],
    organizationForms: [],
    timezones: [],
    taxSystems: [],
    taxSystemModes: [],
  }),
}));

vi.mock('../../hooks/useFormRequirements', () => ({
  useFormRequirements: () => ({
    isRequired: () => false,
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getCompanyRequisitesCheck: vi.fn().mockResolvedValue({ legalEntityId: 9, issues: [] }),
    getCompanyBranches: vi.fn().mockResolvedValue([]),
    getBankConnections: vi.fn().mockResolvedValue([]),
    getCompanyBankAccountMatches: vi.fn(),
    bindCompanyBankAccount: vi.fn(),
    revokeCompanyBankAccount: vi.fn(),
    getTaxRegisterBook: vi.fn(),
    getTaxRegisterDrafts: vi.fn(),
    closeTaxRegisterYear: vi.fn(),
    syncBankConnectionAccounts: vi.fn(),
    syncBankConnectionStatements: vi.fn(),
    updateBankAccountSettings: vi.fn(),
    getActResponsibles: vi.fn().mockResolvedValue([]),
    getActs: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 }),
    getActsSummary: vi.fn().mockResolvedValue(emptyActsSummary),
    getCasesV1: vi.fn().mockResolvedValue([]),
    createCase: vi.fn(),
    updateCase: vi.fn(),
    deleteCase: vi.fn(),
    createInvoice: vi.fn(),
    updateInvoice: vi.fn(),
    createAct: vi.fn(),
    updateAct: vi.fn(),
    createPayment: vi.fn(),
    updatePayment: vi.fn(),
    transferPaymentRemainder: vi.fn(),
    rollbackPaymentRemainderTransfer: vi.fn(),
    adjustPaymentRemainderTransfer: vi.fn(),
    getClientsV2: vi.fn().mockResolvedValue({ items: [], total: 0 }),
    getClient: vi.fn().mockResolvedValue(null),
    getInvoices: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 }),
    getInvoicesSummary: vi.fn().mockResolvedValue(emptyInvoiceSummary),
    getLegalEntityCaseFinanceSummaries: vi.fn().mockResolvedValue([]),
    getLegalEntities: vi.fn().mockResolvedValue([]),
    getMainCompanySettings: vi.fn().mockResolvedValue({
      defaultCompanyId: 9,
      companies: [{ id: 9, shortName: 'Наша компания', clientsCount: 0, createdAt: '2026-04-09T13:18:06Z' }],
    }),
    getPaymentsV2: vi.fn().mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 5,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    }),
    getPayment: vi.fn(),
    suggestClientAddresses: vi.fn().mockResolvedValue([]),
    cleanAddress: vi.fn().mockResolvedValue({ result: '' }),
  },
}));

vi.mock('../Dictionaries/DictionariesModal', () => ({
  default: () => null,
}));

vi.mock('../ui/TablePagination', () => ({
  TablePagination: () => null,
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

vi.mock('../Calendar/PaymentModal', () => ({
  PaymentModal: (props: unknown) => {
    paymentModalPropsSpy(props);
    return <div data-testid="legal-related-payment-modal" />;
  },
}));

// These tests verify the parent's lookup lifecycle; document editors have their own suites.
vi.mock('../Accounts/InvoiceModal', () => ({ InvoiceModal: (props: { clients: { name: string }[]; lookupsLoading: boolean; lookupsError?: string; onClose: () => void }) => {
  invoiceModalPropsSpy(props);
  return <div data-testid="invoice-lookup-fixture"><span>{props.lookupsLoading ? 'Loading clients' : props.clients.map(client => client.name).join(',')}</span><span>{props.lookupsError}</span><button onClick={props.onClose}>Close document fixture</button></div>;
} }));
vi.mock('../Acts/ActModal', () => ({ ActModal: (props: { clients: { name: string }[]; lookupsLoading: boolean; lookupsError?: string; onClose: () => void }) => {
  relatedActPropsSpy(props);
  return <div data-testid="act-lookup-fixture"><span>{props.lookupsLoading ? 'Loading clients' : props.clients.map(client => client.name).join(',')}</span><span>{props.lookupsError}</span><button onClick={props.onClose}>Close document fixture</button></div>;
} }));
vi.mock('../Clients/CaseModal', () => ({ CaseModal: (props: { isOpen: boolean; onClose: () => void }) => {
  relatedCasePropsSpy(props); return props.isOpen ? <div data-testid="related-case-fixture"><button onClick={props.onClose}>Close case fixture</button></div> : null;
} }));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

function createInitialLegalEntity(
  overrides: Partial<LegalEntityDetail> = {},
): LegalEntityDetail {
  return {
    id: 9,
    profileEditVersion: 'A'.repeat(64),
    shortName: '',
    fullName: '',
    inn: '',
    createdAt: '2026-04-09T13:18:06Z',
    updatedAt: '2026-04-10T06:18:36Z',
    clients: [],
    addresses: [],
    ...overrides,
  } as LegalEntityDetail;
}

function lookupClient(id: number, name: string): Client {
  return { id, name, email: '', phone: '', company: '', address: '', notes: '', createdAt: '', isActive: true };
}

function transparentImageData(_x: number, _y: number, width: number, height: number): ImageData {
  return { data: new Uint8ClampedArray(width * height * 4) } as ImageData;
}

function deferCompanyImageProcessing() {
  const images: DeferredImage[] = [];
  let drawnSource = '';
  class DeferredImage {
    width = 400;
    height = 400;
    onload: (() => void) | null = null;
    onerror: (() => void) | null = null;
    source = '';
    set src(value: string) { this.source = value; images.push(this); }
    removeAttribute(name: string) { if (name === 'src') this.source = ''; }
  }
  const context = {
    clearRect: vi.fn(),
    drawImage: vi.fn((image: DeferredImage) => { drawnSource = image.source; }),
    getImageData: vi.fn(transparentImageData),
    save: vi.fn(), restore: vi.fn(), fillRect: vi.fn(),
  };
  const getContext = vi.spyOn(HTMLCanvasElement.prototype, 'getContext')
    .mockReturnValue(context as unknown as CanvasRenderingContext2D);
  const encode = vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(() => drawnSource);
  vi.stubGlobal('Image', DeferredImage);
  return {
    images,
    context,
    encode,
    restore() { vi.unstubAllGlobals(); getContext.mockRestore(); encode.mockRestore(); },
  };
}

describe('LegalEntityModal integration', () => {
  describe('automatic related reads', () => {
    const areas = ['case', 'invoice', 'act', 'payment'] as const;
    type Area = typeof areas[number];
    const section = { case: 'cases', invoice: 'accounts', act: 'acts', payment: 'payments' } as const;
    const apiFor = (area: Area) => (area === 'case' ? apiService.getCasesV1 : area === 'invoice' ? apiService.getInvoices : area === 'act' ? apiService.getActs : apiService.getPaymentsV2) as unknown as ReturnType<typeof vi.fn>;
    const row = (area: Area, label = 'Private automatic row') => area === 'case' ? { id: 81, title: label, legalEntityId: 9, status: 'Open', createdAt: '2026-10-31' } as ClientCase : area === 'invoice' ? { id: 81, invoiceEntityId: 81, number: label, legalEntityId: 9, amount: 100, type: 'Income', status: 'Pending', date: '2026-10-31' } as Invoice : area === 'act' ? { id: 81, number: label, legalEntityId: 9, amount: 100, type: 'Income', status: 'Created', date: '2026-10-31' } as Act : { id: 81, description: label, legalEntityId: 9, amount: 100, paidAmount: 0, type: 'Income', status: 'Pending', date: '2026-10-31' } as Payment;
    const response = (area: Area, label?: string) => area === 'case' ? [row(area, label)] : { items: [row(area, label)], total: 1, page: 1, pageSize: 5, ...(area === 'payment' ? { summary: { income: 100, expense: 0, balance: 100, incomeCount: 1, expenseCount: 0, total: 1 } } : {}) };
    const propsFor = (mode: 'page' | 'modal') => ({ isOpen: true, mode, initial: createInitialLegalEntity({ isOwnCompany: false }), allowEdit: true, allowDelete: false, canViewCases: true, canViewAccounts: true, canViewActs: true, canViewPayments: true, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    const counts = () => [apiService.getCasesV1, apiService.getInvoices, apiService.getInvoicesSummary, apiService.getActs, apiService.getActsSummary, apiService.getPaymentsV2].map(fn => vi.mocked(fn).mock.calls.length);
    for (const mode of ['page', 'modal'] as const) for (const area of areas) {
      it(`profile open read scope: an already opened ${area} cannot retain cached details after scope changes in ${mode}`, async () => {
        apiFor(area).mockResolvedValue(response(area)); if (area === 'payment') vi.mocked(apiService.getPayment).mockResolvedValue(row('payment') as Payment); const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); await screen.findAllByText(/Private automatic row/);
        if (area === 'case') fireEvent.click(screen.getByTitle('Редактировать дело')); else fireEvent.click(screen.getByRole('button', { name: /Private automatic row/ })); const fixture = area === 'case' ? 'related-case-fixture' : area === 'invoice' ? 'invoice-lookup-fixture' : area === 'act' ? 'act-lookup-fixture' : 'legal-related-payment-modal'; await screen.findByTestId(fixture);
        apiFor(area).mockResolvedValue(area === 'case' ? [] : { items: [], total: 0, page: 1, pageSize: 5, summary: { income: 0, expense: 0, balance: 0, incomeCount: 0, expenseCount: 0, total: 0 } }); view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ [section[area]]: relatedPermissions({ canEdit: true, viewScope: 'own' }) }} />); expect(screen.queryByTestId(fixture)).toBeNull();
        view.rerender(<LegalEntityModal {...props} />); expect(screen.queryByTestId(fixture)).toBeNull();
      });
      it(`profile automatic: cached ${area} is cleared and only its scope reloads in ${mode}`, async () => {
        apiFor(area).mockResolvedValue(response(area)); const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); await screen.findAllByText(/Private automatic row/); await act(async () => undefined); const before = counts();
        let finish!: (value: unknown) => void; apiFor(area).mockReturnValue(new Promise(resolve => { finish = resolve; })); view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ [section[area]]: relatedPermissions({ viewScope: 'own' }) }} />);
        expect(screen.queryByText(/Private automatic row/)).toBeNull(); const after = counts(); const affected = area === 'case' ? [0] : area === 'invoice' ? [1, 2] : area === 'act' ? [3, 4] : [5]; expect(after).toEqual(before.map((n, i) => n + (affected.includes(i) ? 1 : 0)));
        await act(async () => finish(response(area, 'Current automatic row'))); expect(screen.getAllByText(/Current automatic row/).length).toBeGreaterThan(0);
      });
      for (const outcome of ['success', 'error'] as const) it.each(['login', 'actor', 'logout'] as const)(`profile automatic: ${area} ${outcome} cannot follow changed %s without render in ${mode}`, async change => {
        let finish!: (value: unknown) => void; let reject!: (error: Error) => void; apiFor(area).mockReturnValue(new Promise((yes, no) => { finish = yes; reject = no; })); render(<LegalEntityModal {...propsFor(mode)} />); await waitFor(() => expect(apiFor(area)).toHaveBeenCalledOnce()); await act(async () => undefined); baseAuthSession.replace(change);
        await act(async () => { if (outcome === 'success') finish(response(area)); else reject(new Error('Private automatic failure')); }); expect(screen.queryByText(/Private automatic row|Private automatic failure/)).toBeNull(); expect(apiFor(area)).toHaveBeenCalledOnce(); expect(toastErrorMock).not.toHaveBeenCalled();
      });
    }
    const finance = { caseId: 81, hasCompletePaymentVisibility: true, incomeTotal: 87654, incomePaid: 0, incomeOutstanding: 87654, incomeCount: 1, expenseTotal: 0, expensePaid: 0, expenseOutstanding: 0, expenseCount: 0, plannedMargin: 87654, paidMargin: 0, outstandingBalance: 87654 } satisfies ClientCaseFinanceSummary;
    it('profile finance read: current background refresh keeps its accepted economics until the next answer', async () => {
      apiFor('case').mockResolvedValue(response('case')); vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValueOnce([finance]); vi.mocked(apiService.createPayment).mockResolvedValue({ id: 701 } as Payment); render(<LegalEntityModal {...propsFor('page')} />); await waitFor(() => expect(document.body.textContent!.replace(/\s/g, '')).toContain('87654'));
      let finish!: (value: ClientCaseFinanceSummary[]) => void; vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReturnValue(new Promise(resolve => { finish = resolve; })); fireEvent.click(screen.getAllByRole('button', { name: 'Платёж' })[0]); const child = paymentModalPropsSpy.mock.lastCall![0] as { onSubmit: (payload: PaymentPayload) => Promise<Payment> };
      await act(async () => { await child.onSubmit({ amount: 100, type: 'Income', date: '2026-10-31', status: 'Pending' } as PaymentPayload); }); await waitFor(() => expect(apiService.getLegalEntityCaseFinanceSummaries).toHaveBeenCalledTimes(2)); expect(document.body.textContent!.replace(/\s/g, '')).toContain('87654'); await act(async () => finish([{ ...finance, incomeTotal: 12345 }])); expect(document.body.textContent!.replace(/\s/g, '')).toContain('12345');
    });
    for (const mode of ['page', 'modal'] as const) {
      it.each(['login', 'actor', 'logout'] as const)(`profile finance read: late economics cannot follow %s without render in ${mode}`, async change => {
        apiFor('case').mockResolvedValue(response('case')); let finish!: (value: ClientCaseFinanceSummary[]) => void; vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReturnValue(new Promise(resolve => { finish = resolve; })); render(<LegalEntityModal {...propsFor(mode)} />); await waitFor(() => expect(apiService.getLegalEntityCaseFinanceSummaries).toHaveBeenCalledOnce()); await act(async () => undefined); baseAuthSession.replace(change);
        await act(async () => finish([finance])); expect(document.body.textContent!.replace(/\s/g, '')).not.toContain('87654'); expect(apiService.getLegalEntityCaseFinanceSummaries).toHaveBeenCalledOnce();
      });
      it(`profile finance read: cached economics are hidden while a new payment visibility read waits in ${mode}`, async () => {
        apiFor('case').mockResolvedValue(response('case')); vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValue([finance]); const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); await waitFor(() => expect(document.body.textContent!.replace(/\s/g, '')).toContain('87654'));
        let finish!: (value: ClientCaseFinanceSummary[]) => void; vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReturnValue(new Promise(resolve => { finish = resolve; })); view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions({ viewScope: 'own' }) }} />); expect(document.body.textContent!.replace(/\s/g, '')).not.toContain('87654'); expect(apiService.getLegalEntityCaseFinanceSummaries).toHaveBeenCalledTimes(2);
        await act(async () => finish([{ ...finance, incomeTotal: 12345, incomeOutstanding: 12345 }])); expect(document.body.textContent!.replace(/\s/g, '')).toContain('12345');
      });
      it(`profile finance read: late errors stay hidden after scope ABA and cannot stop a new loader in ${mode}`, async () => {
        apiFor('case').mockResolvedValue(response('case')); let fail!: (error: Error) => void; vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReturnValueOnce(new Promise((_yes, no) => { fail = no; })).mockReturnValue(new Promise(() => undefined)); const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); await waitFor(() => expect(apiService.getLegalEntityCaseFinanceSummaries).toHaveBeenCalledOnce());
        view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions({ viewScope: 'own' }) }} />); view.rerender(<LegalEntityModal {...props} />); await act(async () => fail(new Error('Private previous economics failed'))); expect(screen.queryByText(/Private previous economics failed/)).toBeNull(); expect(screen.getByText('Считаем экономику дела...')).toBeTruthy();
      });
    }
    it('profile automatic: logout rerender clears cached rows and dispatches no unauthenticated related reads', async () => {
      apiFor('case').mockResolvedValue(response('case')); const props = propsFor('page'); const view = render(<LegalEntityModal {...props} />); await screen.findAllByText(/Private automatic row/); await act(async () => undefined); const before = counts(); baseAuthSession.replace('logout'); view.rerender(<LegalEntityModal {...props} />); expect(screen.queryByText(/Private automatic row/)).toBeNull(); expect(counts()).toEqual(before);
    });
  });
  describe('financial section admission', () => {
    let session: ReturnType<typeof documentCopyTestSession>;
    beforeEach(() => {
      session = documentCopyTestSession();
      vi.mocked(apiService.getBankConnections).mockImplementation(async id => [{ id: 1, legalEntityId: id, provider: 'Tochka', status: 'Active', accounts: [{ id: 10, externalAccountId: 'fixture', accountNumber: '40702810200000000001', currency: 'RUB', status: 'Open', isDefault: false, isHidden: false, statementsEnabled: true }] }]);
      vi.mocked(apiService.getTaxRegisterBook).mockImplementation(async (id, year) => ({ legalEntityId: id, year, isClosed: false, totals: { income: 0, expense: 0, taxBase: 0, entries: 0 }, entries: [], page: 1, pageSize: 100 }));
      vi.mocked(apiService.getTaxRegisterDrafts).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 });
      vi.mocked(apiService.closeTaxRegisterYear).mockClear(); vi.mocked(apiService.syncBankConnectionAccounts).mockClear(); vi.mocked(apiService.syncBankConnectionStatements).mockClear(); vi.mocked(apiService.updateBankAccountSettings).mockClear();
    });
    afterEach(() => session.restore());
    const propsFor = (mode: 'page' | 'modal') => ({ isOpen: true, mode, initial: createInitialLegalEntity({ isOwnCompany: true }), allowEdit: true, allowDelete: false, canManageOwnCompanySettings: true,
      canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: false, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    for (const mode of ['page', 'modal'] as const) {
      it(`finance parent: administrator view mode reaches both financial blocks in ${mode}`, async () => {
        render(<LegalEntityModal {...propsFor(mode)} allowEdit={false} />); await screen.findByTestId('company-tax-register'); await screen.findByTestId('company-bank-connections');
        expect(screen.queryByRole('button', { name: 'Добавить черновик' })).toBeNull();
        for (const name of ['Закрыть год', 'Обновить счета', 'Загрузить выписки по всем счетам']) { const b = screen.getByRole('button', { name }); expect(b).toHaveProperty('disabled', true); fireEvent.click(b); }
        expect(screen.getByLabelText('Скрыть счёт 40702810200000000001')).toHaveProperty('disabled', true);
        expect(apiService.closeTaxRegisterYear).not.toHaveBeenCalled(); expect(apiService.syncBankConnectionAccounts).not.toHaveBeenCalled(); expect(apiService.syncBankConnectionStatements).not.toHaveBeenCalled();
      });
      it.each(['readonly', 'authority'] as const)(`finance parent: pending close-year belongs to its ${mode} %s admission`, async change => {
        let finish!: (value: boolean) => void; confirmMock.mockReturnValue(new Promise(resolve => { finish = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Закрыть год' })); const signal = confirmMock.mock.calls[0][0].signal as AbortSignal;
        if (change === 'readonly') view.rerender(<LegalEntityModal {...props} allowEdit={false} />); else view.rerender(<LegalEntityModal {...props} canManageOwnCompanySettings={false} />);
        expect(signal.aborted).toBe(true); await act(async () => finish(true)); expect(apiService.closeTaxRegisterYear).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
      });
    }
  });

  describe('bank binding parent admission', () => {
    beforeEach(() => {
      confirmMock.mockResolvedValue(true);
      vi.mocked(apiService.getBankConnections).mockClear().mockImplementation(async id => [bankConnectionFixture(id)]);
      vi.mocked(apiService.getCompanyBankAccountMatches).mockImplementation(async id => bankReportFixture(id));
      vi.mocked(apiService.bindCompanyBankAccount).mockClear(); vi.mocked(apiService.revokeCompanyBankAccount).mockClear();
    });
    const propsFor = (mode: 'page' | 'modal', own = true) => ({ isOpen: true, mode, initial: createInitialLegalEntity({ isOwnCompany: own }),
      allowEdit: true, allowDelete: false, canManageOwnCompanySettings: true, canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: false,
      onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    for (const mode of ['page', 'modal'] as const) {
      it(`bank binding parent: delegated profile edit does not read bank administration in ${mode}`, async () => {
        render(<LegalEntityModal {...propsFor(mode)} canManageOwnCompanySettings={false} />); await act(async () => undefined);
        expect(screen.queryByTestId('company-bank-connections')).toBeNull(); expect(apiService.getBankConnections).not.toHaveBeenCalled();
      });
      it(`bank binding parent: former own company cleanup is reachable in ${mode}`, async () => {
        const p = propsFor(mode, false); const id = p.initial.id;
        const report = { ...bankReportFixture(id), canBind: false, profileIssues: ['CompanyNotOwn'], groups: [], accounts: [], activeBindings: [{ ...bindingFixture(), bankAccountId: id + 96 }] };
        vi.mocked(apiService.getCompanyBankAccountMatches).mockResolvedValueOnce(report).mockResolvedValueOnce({ ...report, activeBindings: [], version: bankVersion('B') });
        vi.mocked(apiService.revokeCompanyBankAccount).mockResolvedValue({ bindingId: 77, state: 'Revoked', isReplay: false });
        render(<LegalEntityModal {...p} />); fireEvent.click(await screen.findByRole('button', { name: 'Сверить счета профиля' }));
        fireEvent.click(await screen.findByRole('button', { name: 'Снять связь' })); await screen.findByText('Подтверждённые связи компании (0)');
        expect(apiService.revokeCompanyBankAccount).toHaveBeenCalledWith(id + 6, 77, bankVersion(), expect.any(AbortSignal));
      });
      it.each(['rights', 'draft'] as const)(`bank binding parent: pending confirmation is fenced by ${mode} %s`, async change => {
        let finish!: (value: boolean) => void; confirmMock.mockReturnValue(new Promise<boolean>(resolve => { finish = resolve; }));
        const p = propsFor(mode); const view = render(<LegalEntityModal {...p} />);
        fireEvent.click(await screen.findByRole('button', { name: 'Сверить счета профиля' })); const bank = await screen.findByLabelText('Банковский источник');
        fireEvent.click(bank); fireEvent.click(screen.getByRole('option', { name: `Сбербанк · ${bankConnectionFixture(p.initial.id).accounts[0].accountNumber} · RUB · БИК 044525225` }));
        fireEvent.click(screen.getByLabelText('Счёт профиля')); fireEvent.click(screen.getByRole('option', { name: 'Основной расчётный — profile-A' })); fireEvent.click(screen.getByRole('button', { name: 'Подтвердить связь' }));
        const signal = confirmMock.mock.calls.at(-1)![0].signal as AbortSignal;
        if (change === 'rights') view.rerender(<LegalEntityModal {...p} canManageOwnCompanySettings={false} />);
        else { fireEvent.click(screen.getByLabelText('Состояние профиля компании')); fireEvent.click(screen.getByRole('option', { name: 'В архиве' })); }
        expect(signal.aborted).toBe(true); await act(async () => finish(true)); expect(apiService.bindCompanyBankAccount).not.toHaveBeenCalled();
      });
    }
  });

  describe('invoice case parent ownership', () => {
    let session: ReturnType<typeof documentCopyTestSession>;
    beforeEach(() => { session = documentCopyTestSession(); invoiceModalPropsSpy.mockReset(); vi.mocked(apiService.getClientsV2).mockReset().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 }); });
    afterEach(() => session.restore());
    const propsFor = (mode: 'page' | 'modal', own = false) => ({ isOpen: true, mode, initial: createInitialLegalEntity({ isOwnCompany: own }),
      allowEdit: true, allowDelete: false, canManageOwnCompanySettings: false, canViewCases: true, canViewAccounts: true, canViewActs: false, canViewPayments: false,
      onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    const created = (overrides: Partial<ClientCase> = {}) => ({ id: 441, legalEntityId: 9, clientId: null, title: 'Owned invoice case', status: 'Open', createdAt: '2026-10-31T02:00:00Z', ...overrides } as ClientCase);
    const callback = () => invoiceModalPropsSpy.mock.lastCall![0].onClientCaseCreated as (value: ClientCase) => void;
    const openInvoice = async () => { fireEvent.click(await screen.findByRole('button', { name: 'Счёт' })); await screen.findByTestId('invoice-lookup-fixture'); await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalled()); return callback(); };

    for (const mode of ['page', 'modal'] as const) {
      it.each([false, true])(`invoice case parent: owned result remains available without own-settings authority in ${mode}, own=%s`, async own => {
        const props = propsFor(mode, own); render(<LegalEntityModal {...props} />); const accept = await openInvoice();
        const reads = vi.mocked(apiService.getClientsV2).mock.calls.length; const defaultCase = invoiceModalPropsSpy.mock.lastCall![0].defaultClientCaseId;
        expect(invoiceModalPropsSpy.mock.lastCall![0].onClientCreated).toBeUndefined(); session.refresh();
        act(() => { accept(created()); accept(created()); });
        expect(invoiceModalPropsSpy.mock.lastCall![0].defaultClientCaseId).toBe(defaultCase);
        expect(screen.getAllByText('Owned invoice case').length).toBeGreaterThan(0); expect(apiService.getClientsV2).toHaveBeenCalledTimes(reads);
      });

      it.each(['company', 'readonly', 'invoice-rights', 'case-rights', 'closed', 'reopen', 'document-reopen', 'mode', 'profile-version', 'unmount', 'rights-aba'] as const)(`invoice case parent: old callback cannot follow %s in ${mode}`, async change => {
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); const accept = await openInvoice();
        if (change === 'company') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />);
        if (change === 'readonly') view.rerender(<LegalEntityModal {...props} allowEdit={false} />);
        if (change === 'invoice-rights' || change === 'rights-aba') { view.rerender(<LegalEntityModal {...props} canCreateAccounts={false} />); if (change === 'rights-aba') view.rerender(<LegalEntityModal {...props} />); }
        if (change === 'case-rights') view.rerender(<LegalEntityModal {...props} canViewCases={false} />);
        if (change === 'closed' || change === 'reopen') { view.rerender(<LegalEntityModal {...props} isOpen={false} />); if (change === 'reopen') view.rerender(<LegalEntityModal {...props} />); }
        if (change === 'document-reopen') { fireEvent.click(screen.getByRole('button', { name: 'Close document fixture' })); await openInvoice(); }
        if (change === 'mode') view.rerender(<LegalEntityModal {...props} mode={mode === 'page' ? 'modal' : 'page'} />);
        if (change === 'profile-version') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, profileEditVersion: 'B'.repeat(64) }} />);
        if (change === 'unmount') view.unmount();
        const reads = vi.mocked(apiService.getClientsV2).mock.calls.length; act(() => accept(created()));
        if (change === 'case-rights') view.rerender(<LegalEntityModal {...props} />);
        expect(screen.queryAllByText('Owned invoice case')).toHaveLength(0); expect(apiService.getClientsV2).toHaveBeenCalledTimes(reads); expect(toastErrorMock).not.toHaveBeenCalled();
      });
    }
    it.each(['login', 'actor', 'logout'] as const)('invoice case parent: old %s callback is rejected without a render', async change => {
      render(<LegalEntityModal {...propsFor('page')} />); const accept = await openInvoice(); session.replace(change); act(() => accept(created()));
      expect(screen.queryAllByText('Owned invoice case')).toHaveLength(0);
    });
    it.each([{ id: 0 }, { legalEntityId: 8 }, { legalEntityId: null, clientId: 7 }] as const)('invoice case parent: rejects a malformed or foreign case %j', async wrong => {
      render(<LegalEntityModal {...propsFor('page')} />); const accept = await openInvoice(); act(() => accept(created(wrong)));
      expect(screen.queryAllByText('Owned invoice case')).toHaveLength(0);
    });
  });

  describe('related write ownership', () => {
    type Kind = 'case' | 'invoice' | 'act';
    type Child = { onSave: (payload: Partial<ClientCase>) => Promise<void>; onSubmit: (payload: InvoiceInput | ActInput) => Promise<void>; onDelete?: () => Promise<void>; onClose: () => void; onNextHearingConfirmed?: (value: Partial<ClientCase>) => void; submitting?: boolean; errorMessage?: string | null; caseData?: ClientCase; initialValues?: Partial<ActInput> };
    const kindSection = { case: 'cases', invoice: 'accounts', act: 'acts' } as const;
    const kindLabel = { case: 'Дело', invoice: 'Счёт', act: 'Акт' };
    const fixture = { case: 'related-case-fixture', invoice: 'invoice-lookup-fixture', act: 'act-lookup-fixture' };
    const spy = (kind: Kind) => kind === 'case' ? relatedCasePropsSpy : kind === 'invoice' ? invoiceModalPropsSpy : relatedActPropsSpy;
    const child = (kind: Kind) => spy(kind).mock.lastCall![0] as Child;
    const row = { id: 81, legalEntityId: 9, clientId: null, title: 'Owned related case', status: 'Open', createdAt: '2026-10-31' } as ClientCase;
    const payload = (kind: Kind) => kind === 'case' ? { title: 'Preserved case title', feeAmount: 215.5 } : { number: 'PRESERVED-NUMBER', legalEntityId: 9, type: 'Income', amount: 215.5 } as InvoiceInput;
    const save = (kind: Kind, props = child(kind)) => kind === 'case' ? props.onSave(payload(kind) as Partial<ClientCase>) : props.onSubmit(payload(kind) as InvoiceInput);
    const apiCreate = (kind: Kind) => kind === 'case' ? vi.mocked(apiService.createCase) : kind === 'invoice' ? vi.mocked(apiService.createInvoice) : vi.mocked(apiService.createAct);
    const propsFor = (mode: 'page' | 'modal') => ({ isOpen: true, mode, initial: createInitialLegalEntity({ isOwnCompany: false }), allowEdit: true, allowDelete: false,
      canViewCases: true, canViewAccounts: true, canViewActs: true, canViewPayments: true, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    const open = async (kind: Kind) => { fireEvent.click(await screen.findByRole('button', { name: kindLabel[kind] })); await screen.findByTestId(fixture[kind]); return child(kind); };
    const counts = () => [apiService.getCasesV1, apiService.getInvoices, apiService.getInvoicesSummary, apiService.getActs, apiService.getActsSummary, apiService.getPaymentsV2, apiService.getLegalEntityCaseFinanceSummaries].map(fn => vi.mocked(fn).mock.calls.length);
    beforeEach(() => {
      relatedCasePropsSpy.mockReset(); relatedActPropsSpy.mockReset(); invoiceModalPropsSpy.mockReset();
      vi.mocked(apiService.getClientsV2).mockReset().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 });
      vi.mocked(apiService.createCase).mockReset().mockResolvedValue(row); vi.mocked(apiService.updateCase).mockReset().mockResolvedValue(row); vi.mocked(apiService.deleteCase).mockReset().mockResolvedValue(undefined);
      vi.mocked(apiService.createInvoice).mockReset().mockResolvedValue({ id: 82, number: 'PRESERVED-NUMBER' } as Invoice); vi.mocked(apiService.updateInvoice).mockReset().mockResolvedValue({ id: 82 } as Invoice);
      vi.mocked(apiService.createAct).mockReset().mockResolvedValue({ id: 83, number: 'PRESERVED-NUMBER' } as Act); vi.mocked(apiService.updateAct).mockReset().mockResolvedValue({ id: 83 } as Act);
    });
    it('related write microtask: login replacement after hook acceptance prevents the parent continuation', async () => {
      const session = documentCopyTestSession(); try {
        vi.mocked(apiService.createCase).mockImplementation(async () => { queueMicrotask(() => queueMicrotask(() => queueMicrotask(() => session.replace('actor')))); return row; });
        render(<LegalEntityModal {...propsFor('page')} />); const current = await open('case');
        await act(async () => { await expect(save('case', current)).rejects.toBeInstanceOf(StaleUiOperationError); });
      } finally { session.restore(); }
    });
    it.each(['save', 'delete'] as const)('related write microtask: login replacement after case %s prevents the parent continuation', async action => {
      const session = documentCopyTestSession(); try {
        vi.mocked(apiService.getCasesV1).mockResolvedValue([row]); const mutation = action === 'save' ? vi.mocked(apiService.updateCase) : vi.mocked(apiService.deleteCase);
        (mutation as unknown as ReturnType<typeof vi.fn>).mockImplementation(async () => { queueMicrotask(() => queueMicrotask(() => queueMicrotask(() => session.replace('actor')))); return action === 'delete' ? undefined : row; });
        render(<LegalEntityModal {...propsFor('page')} />); fireEvent.click(await screen.findByTitle('Редактировать дело')); const old = child('case');
        await act(async () => { await expect(action === 'save' ? save('case', old) : old.onDelete!()).rejects.toBeInstanceOf(StaleUiOperationError); });
      } finally { session.restore(); }
    });
    for (const mode of ['page', 'modal'] as const) for (const kind of ['case', 'invoice', 'act'] as const) {
      it.each(['company', 'readonly', 'permission', 'permission-aba', 'closed', 'reopen', 'mode', 'version', 'unmount'] as const)(`related write: pending ${kind} cannot follow %s in ${mode}`, async change => {
        let finish!: (value: unknown) => void; (apiCreate(kind) as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); const old = await open(kind); let saved!: Promise<void>; act(() => { saved = save(kind, old); });
        if (change === 'company') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />);
        if (change === 'readonly') view.rerender(<LegalEntityModal {...props} allowEdit={false} />);
        if (change === 'permission' || change === 'permission-aba') { view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ [kindSection[kind]]: relatedPermissions({ canCreate: false }) }} />); if (change === 'permission-aba') view.rerender(<LegalEntityModal {...props} />); }
        if (change === 'closed' || change === 'reopen') { view.rerender(<LegalEntityModal {...props} isOpen={false} />); if (change === 'reopen') { view.rerender(<LegalEntityModal {...props} />); await open(kind); } }
        if (change === 'mode') view.rerender(<LegalEntityModal {...props} mode={mode === 'page' ? 'modal' : 'page'} />);
        if (change === 'version') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, profileEditVersion: 'B'.repeat(64) }} />);
        if (change === 'unmount') view.unmount();
        const before = counts(); await act(async () => { finish(kind === 'case' ? row : { id: 82, number: 'Private old result' }); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); });
        expect(counts()).toEqual(before); expect(screen.queryAllByText('Owned related case')).toHaveLength(0); expect(screen.queryAllByText('Private old result')).toHaveLength(0); expect(toastErrorMock).not.toHaveBeenCalled();
        if (change === 'reopen') { act(() => old.onClose()); expect(screen.getByTestId(fixture[kind])).toBeTruthy(); }
      });

      it(`related write: current ${kind} blocks duplicates, preserves seeds and finishes in ${mode}`, async () => {
        let finish!: (value: unknown) => void; (apiCreate(kind) as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; }));
        render(<LegalEntityModal {...propsFor(mode)} />); const old = await open(kind); let saved!: Promise<void>; act(() => { saved = save(kind, old); });
        await expect(save(kind, old)).rejects.toBeInstanceOf(StaleUiOperationError); expect(apiCreate(kind)).toHaveBeenCalledOnce();
        if (kind === 'case') expect(child(kind).caseData).toBe(old.caseData); else expect(child(kind).submitting).toBe(true);
        if (kind === 'act') expect(child(kind).initialValues).toBe(old.initialValues);
        await act(async () => { finish(kind === 'case' ? row : { id: 82 }); await saved; });
        if (kind === 'case') { expect(screen.getAllByText('Owned related case').length).toBeGreaterThan(0); act(() => old.onClose()); }
        expect(screen.queryByTestId(fixture[kind])).toBeNull();
        expect(apiCreate(kind)).toHaveBeenCalledWith(expect.objectContaining(kind === 'case' ? { legalEntityId: 9, clientId: null, title: 'Preserved case title', feeAmount: 215.5 } : { number: 'PRESERVED-NUMBER', legalEntityId: 9, amount: 215.5 }));
      });
    }

    for (const kind of ['case', 'invoice', 'act'] as const) {
      it.each(['login', 'actor', 'logout'] as const)(`related write: ${kind} completion rejects a changed %s without render`, async change => {
        const session = documentCopyTestSession(); try {
          let finish!: (value: unknown) => void; (apiCreate(kind) as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; })); render(<LegalEntityModal {...propsFor('page')} />);
          const old = await open(kind); let saved!: Promise<void>; act(() => { saved = save(kind, old); }); session.replace(change);
          await expect(save(kind, old)).rejects.toBeInstanceOf(StaleUiOperationError); const before = counts(); await act(async () => { finish(kind === 'case' ? row : { id: 82 }); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); });
          expect(counts()).toEqual(before); expect(apiCreate(kind)).toHaveBeenCalledOnce();
        } finally { session.restore(); }
      });
      it(`related write: current ${kind} failure preserves the form and allows a deliberate retry`, async () => {
        (apiCreate(kind) as unknown as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error('Current write rejected')); render(<LegalEntityModal {...propsFor('page')} />); const old = await open(kind);
        await act(async () => { await expect(save(kind, old)).rejects.toThrow('Current write rejected'); }); expect(screen.getByTestId(fixture[kind])).toBeTruthy(); if (kind !== 'case') { expect(child(kind).submitting).toBe(false); expect(child(kind).errorMessage).toBe('Current write rejected'); }
        await act(async () => { await save(kind); }); expect(apiCreate(kind)).toHaveBeenCalledTimes(2);
      });
    }

    it('related write: old invoice finalization and close cannot unlock a new pending invoice', async () => {
      let first!: (value: Invoice) => void; let second!: (value: Invoice) => void; vi.mocked(apiService.createInvoice).mockReturnValueOnce(new Promise(resolve => { first = resolve; })).mockReturnValueOnce(new Promise(resolve => { second = resolve; }));
      render(<LegalEntityModal {...propsFor('page')} />); const old = await open('invoice'); let firstSave!: Promise<void>; act(() => { firstSave = save('invoice', old); }); act(() => old.onClose()); const next = await open('invoice'); let secondSave!: Promise<void>; act(() => { secondSave = save('invoice', next); });
      await act(async () => { first({ id: 82 } as Invoice); await expect(firstSave).rejects.toBeInstanceOf(StaleUiOperationError); }); act(() => old.onClose()); expect(child('invoice').submitting).toBe(true); expect(screen.getByTestId(fixture.invoice)).toBeTruthy();
      await act(async () => { second({ id: 83 } as Invoice); await secondSave; }); expect(screen.queryByTestId(fixture.invoice)).toBeNull();
    });
    it.each(['save', 'delete'] as const)('related write: current case %s keeps edit/delete callbacks and API identity', async action => {
      vi.mocked(apiService.getCasesV1).mockResolvedValue([row]); render(<LegalEntityModal {...propsFor('page')} />); fireEvent.click(await screen.findByTitle('Редактировать дело')); await screen.findByTestId(fixture.case); const current = child('case');
      await act(async () => { if (action === 'save') await save('case', current); else await current.onDelete!(); });
      if (action === 'save') expect(apiService.updateCase).toHaveBeenCalledWith(81, expect.objectContaining({ title: 'Preserved case title', legalEntityId: 9, feeAmount: 215.5 })); else expect(apiService.deleteCase).toHaveBeenCalledExactlyOnceWith(81);
    });
    it.each(['invoice', 'act'] as const)('related write edit: current %s keeps its existing ID and update payload', async kind => {
      if (kind === 'invoice') vi.mocked(apiService.getInvoices).mockResolvedValue({ items: [{ id: 82, invoiceEntityId: 82, number: 'INV-EDIT', type: 'Income', date: '2026-10-31', legalEntityId: 9, amount: 100, status: 'Pending' } as Invoice], total: 1, page: 1, pageSize: 5 });
      else vi.mocked(apiService.getActs).mockResolvedValue({ items: [{ id: 83, number: 'ACT-EDIT', date: '2026-10-31', amount: 100, status: 'Created', legalEntityId: 9, createdAt: '2026-10-31' } as Act], total: 1, page: 1, pageSize: 5 });
      render(<LegalEntityModal {...propsFor('page')} />); fireEvent.click(await screen.findByRole('button', { name: kind === 'invoice' ? /INV-EDIT/ : /ACT-EDIT/ })); await screen.findByTestId(fixture[kind]);
      await act(async () => { await save(kind); });
      if (kind === 'invoice') expect(apiService.updateInvoice).toHaveBeenCalledExactlyOnceWith(82, payload(kind)); else expect(apiService.updateAct).toHaveBeenCalledExactlyOnceWith(83, payload(kind));
      expect(apiCreate(kind)).not.toHaveBeenCalled(); expect(screen.queryByTestId(fixture[kind])).toBeNull();
    });
    it.each(['invoice', 'act'] as const)('related write edit: stale %s error and a restored permission cannot revive old form errors', async kind => {
      let reject!: (error: Error) => void; (apiCreate(kind) as unknown as ReturnType<typeof vi.fn>).mockReturnValueOnce(new Promise((_resolve, fail) => { reject = fail; }));
      const props = propsFor('page'); const view = render(<LegalEntityModal {...props} />); const old = await open(kind); let saved!: Promise<void>; act(() => { saved = save(kind, old); }); act(() => old.onClose()); await open(kind);
      await act(async () => { reject(new Error('Private old form error')); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); }); expect(child(kind).errorMessage).toBeNull(); expect(screen.getByTestId(fixture[kind])).toBeTruthy();
      (apiCreate(kind) as unknown as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error('Current owned error')); await act(async () => { await expect(save(kind)).rejects.toThrow('Current owned error'); }); expect(child(kind).errorMessage).toBe('Current owned error');
      view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ [kindSection[kind]]: relatedPermissions({ canCreate: false }) }} />); view.rerender(<LegalEntityModal {...props} />); expect(child(kind).errorMessage).toBeNull();
    });
    for (const mode of ['page', 'modal'] as const) for (const action of ['save', 'delete'] as const) {
      it.each(['company', 'permission'] as const)(`related case edit: ${action} cannot follow %s in ${mode}`, async change => {
        let finish!: (value: unknown) => void; vi.mocked(apiService.getCasesV1).mockImplementation(async params => params?.legalEntityId === 9 ? [row] : []);
        const mutation = action === 'save' ? vi.mocked(apiService.updateCase) : vi.mocked(apiService.deleteCase); (mutation as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); fireEvent.click(await screen.findByTitle('Редактировать дело'));
        await screen.findByTestId(fixture.case); const old = child('case'); let saved!: Promise<void>; act(() => { saved = action === 'save' ? save('case', old) : old.onDelete!(); });
        if (change === 'company') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />);
        else view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ cases: relatedPermissions(action === 'save' ? { canEdit: false } : { canEdit: true, canDelete: false }) }} />);
        const before = counts(); await act(async () => { finish(action === 'save' ? { ...row, title: 'Private edited case' } : undefined); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); }); expect(counts()).toEqual(before); expect(screen.queryByText('Private edited case')).toBeNull();
      });
    }
    it('related case edit: old hearing confirmation cannot modify a new case while its current callback works', async () => {
      vi.mocked(apiService.getCasesV1).mockImplementation(async params => params?.legalEntityId === 9 ? [row] : [{ ...row, id: 84, legalEntityId: 8 }]);
      const props = propsFor('page'); const view = render(<LegalEntityModal {...props} />); fireEvent.click(await screen.findByTitle('Редактировать дело')); const old = child('case');
      view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); fireEvent.click(await screen.findByTitle('Редактировать дело')); const confirmation = { nextHearingConfirmedAt: '2026-10-31T06:00:00Z' };
      act(() => old.onNextHearingConfirmed!(confirmation)); expect(child('case').caseData?.nextHearingConfirmedAt).toBeUndefined();
      act(() => child('case').onNextHearingConfirmed!(confirmation)); expect(child('case').caseData?.nextHearingConfirmedAt).toBe(confirmation.nextHearingConfirmedAt);
    });
    it.each(['case', 'invoice', 'act'] as const)('related read-write scope: pending %s cannot adopt or refresh after visibility changes', async kind => {
      let finish!: (value: unknown) => void; (apiCreate(kind) as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; })); const props = propsFor('page'); const view = render(<LegalEntityModal {...props} />); const old = await open(kind); let saved!: Promise<void>; act(() => { saved = save(kind, old); });
      view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ [kindSection[kind]]: relatedPermissions({ canCreate: true, viewScope: 'own' }) }} />); const before = counts(); await act(async () => { finish({ ...row, number: 'Private scoped write' }); await expect(saved).rejects.toBeInstanceOf(StaleUiOperationError); }); expect(counts()).toEqual(before); expect(toastSuccessMock).not.toHaveBeenCalled();
    });
  });

  describe('payment write ownership', () => {
    type Child = { onSubmit: (payload: PaymentPayload | ({ id: number } & PaymentPayload)) => Promise<Payment>; onTransferRemainder: (payload: { id: number } & PaymentPayload, date: string) => Promise<Payment>; onRollbackRemainderTransfer: (id: number) => Promise<void>; onAdjustRemainderTransfer: (id: number, paid: number, date: string) => Promise<void>; onClose: () => void };
    const child = () => paymentModalPropsSpy.mock.lastCall![0] as Child;
    const payload = { date: '2026-10-31', plannedDate: '2026-10-31', legalEntityId: 9, amount: 123.456789, paidAmount: 0, type: 'Income', status: 'Pending', description: 'Owned payment', isPaid: false, notes: '', systemNotes: '', rescheduleCount: 0 } as PaymentPayload;
    const payment = { ...payload, id: 701, createdAt: '2026-10-31' } as Payment;
    const propsFor = (mode: 'page' | 'modal' = 'page') => ({ isOpen: true, mode, initial: createInitialLegalEntity({ isOwnCompany: false }), allowEdit: true, allowDelete: false,
      canViewCases: true, canViewAccounts: true, canViewActs: true, canViewPayments: true, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    const open = async () => { fireEvent.click(await screen.findByRole('button', { name: 'Платёж' })); await screen.findByTestId('legal-related-payment-modal'); return child(); };
    const counts = () => [apiService.getPaymentsV2, apiService.getInvoices, apiService.getInvoicesSummary, apiService.getActs, apiService.getActsSummary, apiService.getLegalEntityCaseFinanceSummaries].map(fn => vi.mocked(fn).mock.calls.length);
    type Action = 'create' | 'update' | 'transfer' | 'rollback' | 'adjust';
    const apiFor = (action: Action) => action === 'create' ? apiService.createPayment : action === 'update' ? apiService.updatePayment : action === 'transfer' ? apiService.transferPaymentRemainder : action === 'rollback' ? apiService.rollbackPaymentRemainderTransfer : apiService.adjustPaymentRemainderTransfer;
    const invoke = (action: Action, props = child()): Promise<unknown> => action === 'create' ? props.onSubmit(payload) : action === 'update' ? props.onSubmit({ ...payload, id: 701 }) : action === 'transfer' ? props.onTransferRemainder({ ...payload, id: 701 }, '2026-11-02') : action === 'rollback' ? props.onRollbackRemainderTransfer(701) : props.onAdjustRemainderTransfer(701, 12.345678, '2026-11-02');
    const response = (action: Action) => action === 'create' || action === 'update' ? payment : action === 'rollback' ? { payment, removedRemainderPaymentId: 702 } : { payment, remainderPayment: { ...payment, id: 702 }, createdRemainder: action === 'transfer' };
    beforeEach(() => {
      vi.mocked(apiService.createPayment).mockReset().mockResolvedValue(payment); vi.mocked(apiService.updatePayment).mockReset().mockResolvedValue(payment);
      vi.mocked(apiService.transferPaymentRemainder).mockReset().mockResolvedValue({ payment, remainderPayment: { ...payment, id: 702 }, createdRemainder: true } as Awaited<ReturnType<typeof apiService.transferPaymentRemainder>>);
      vi.mocked(apiService.rollbackPaymentRemainderTransfer).mockReset().mockResolvedValue({ payment, removedRemainderPaymentId: 702 });
      vi.mocked(apiService.adjustPaymentRemainderTransfer).mockReset().mockResolvedValue({ payment, remainderPayment: { ...payment, id: 702 }, createdRemainder: false });
    });
    it('payment write owner: committed create returns its exact canonical object without refreshing another company', async () => {
      let finish!: (value: Payment) => void; vi.mocked(apiService.createPayment).mockReturnValue(new Promise(resolve => { finish = resolve; })); const props = propsFor(); const view = render(<LegalEntityModal {...props} />); const old = await open();
      let saved!: Promise<Payment>; act(() => { saved = old.onSubmit(payload); }); view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); const before = counts();
      await act(async () => { finish(payment); expect(await saved).toBe(payment); }); expect(counts()).toEqual(before); expect(toastErrorMock).not.toHaveBeenCalled(); expect(toastSuccessMock).not.toHaveBeenCalled();
    });
    it('payment write owner: late rollback never closes a newly opened payment', async () => {
      let finish!: (value: Awaited<ReturnType<typeof apiService.rollbackPaymentRemainderTransfer>>) => void; vi.mocked(apiService.rollbackPaymentRemainderTransfer).mockReturnValue(new Promise(resolve => { finish = resolve; })); const props = propsFor(); const view = render(<LegalEntityModal {...props} />); const old = await open();
      let saved!: Promise<void>; act(() => { saved = old.onRollbackRemainderTransfer(701); }); view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); await open(); const before = counts();
      await act(async () => { finish({ payment, removedRemainderPaymentId: 702 }); await saved; }); expect(screen.getByTestId('legal-related-payment-modal')).toBeTruthy(); expect(counts()).toEqual(before); expect(toastSuccessMock).not.toHaveBeenCalled();
    });
    it('payment party: an explicitly selected client does not acquire the profile company as another counterparty', async () => {
      render(<LegalEntityModal {...propsFor()} />); const current = await open(); await act(async () => { await current.onSubmit({ ...payload, clientId: 69, legalEntityId: null }); });
      expect(apiService.createPayment).toHaveBeenCalledWith(expect.objectContaining({ clientId: 69, legalEntityId: null, amount: 123.456789 }));
    });
    for (const mode of ['page', 'modal'] as const) for (const action of ['create', 'update', 'transfer', 'rollback', 'adjust'] as const) {
      it.each(['company', 'readonly', 'permission', 'permission-aba', 'closed', 'reopen', 'mode', 'version', 'unmount'] as const)(`payment write matrix: ${action} success cannot follow %s in ${mode}`, async change => {
        let finish!: (value: unknown) => void; (apiFor(action) as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; })); const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); const old = await open();
        let saved!: Promise<unknown>; act(() => { saved = invoke(action, old); });
        if (change === 'company') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />);
        if (change === 'readonly') view.rerender(<LegalEntityModal {...props} allowEdit={false} />);
        if (change === 'permission' || change === 'permission-aba') { view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions({ canCreate: false, canEdit: false }) }} />); if (change === 'permission-aba') view.rerender(<LegalEntityModal {...props} />); }
        if (change === 'closed' || change === 'reopen') { view.rerender(<LegalEntityModal {...props} isOpen={false} />); if (change === 'reopen') { view.rerender(<LegalEntityModal {...props} />); await open(); } }
        if (change === 'mode') view.rerender(<LegalEntityModal {...props} mode={mode === 'page' ? 'modal' : 'page'} />);
        if (change === 'version') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, profileEditVersion: 'B'.repeat(64) }} />);
        if (change === 'unmount') view.unmount(); const before = counts();
        await act(async () => { finish(response(action)); const value = await saved; if (action === 'create' || action === 'update' || action === 'transfer') expect(value).toBe(payment); });
        expect(counts()).toEqual(before); expect(toastSuccessMock).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
        if (change === 'reopen') { act(() => old.onClose()); expect(screen.getByTestId('legal-related-payment-modal')).toBeTruthy(); }
      });
      it(`payment write matrix: current ${action} preserves precision, endpoint and duplicate exclusion in ${mode}`, async () => {
        let finish!: (value: unknown) => void; (apiFor(action) as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; })); render(<LegalEntityModal {...propsFor(mode)} />); const current = await open(); let saved!: Promise<unknown>; act(() => { saved = invoke(action, current); });
        await expect(invoke(action, current)).rejects.toBeInstanceOf(StaleUiOperationError); expect(apiFor(action)).toHaveBeenCalledOnce();
        await act(async () => { finish(response(action)); const value = await saved; if (action === 'create' || action === 'update' || action === 'transfer') expect(value).toBe(payment); });
        if (action === 'create') expect(apiService.createPayment).toHaveBeenCalledWith(expect.objectContaining({ amount: 123.456789, legalEntityId: 9, clientId: null }));
        if (action === 'update') expect(apiService.updatePayment).toHaveBeenCalledWith(701, expect.objectContaining({ amount: 123.456789, legalEntityId: 9 }));
        if (action === 'transfer') expect(apiService.transferPaymentRemainder).toHaveBeenCalledWith(701, expect.objectContaining({ amount: 123.456789 }), '2026-11-02');
        if (action === 'rollback') expect(apiService.rollbackPaymentRemainderTransfer).toHaveBeenCalledExactlyOnceWith(701);
        if (action === 'adjust') expect(apiService.adjustPaymentRemainderTransfer).toHaveBeenCalledExactlyOnceWith(701, 12.345678, '2026-11-02');
        if (action === 'rollback' || action === 'adjust') expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull(); else expect(screen.getByTestId('legal-related-payment-modal')).toBeTruthy();
      });
    }
    for (const action of ['create', 'update', 'transfer', 'rollback', 'adjust'] as const) {
      it.each(['login', 'actor', 'logout'] as const)(`payment write matrix: ${action} keeps confirmed success but no old UI after %s without render`, async change => {
        const session = documentCopyTestSession(); try {
          let finish!: (value: unknown) => void; (apiFor(action) as unknown as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(resolve => { finish = resolve; })); render(<LegalEntityModal {...propsFor()} />); const old = await open(); let saved!: Promise<unknown>; act(() => { saved = invoke(action, old); }); session.replace(change); const before = counts();
          await expect(invoke(action, old)).rejects.toBeInstanceOf(StaleUiOperationError); await act(async () => { finish(response(action)); await saved; }); expect(apiFor(action)).toHaveBeenCalledOnce(); expect(counts()).toEqual(before); expect(toastSuccessMock).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
        } finally { session.restore(); }
      });
      it(`payment write matrix: ${action} failure remains retryable but a stale failure cannot notify a new form`, async () => {
        (apiFor(action) as unknown as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error('Current payment write refused')); const props = propsFor(); const view = render(<LegalEntityModal {...props} />); const current = await open();
        await act(async () => { await expect(invoke(action, current)).rejects.toThrow('Current payment write refused'); }); expect(toastErrorMock).toHaveBeenCalledOnce(); await act(async () => { await invoke(action); }); expect(apiFor(action)).toHaveBeenCalledTimes(2);
        if (action === 'rollback' || action === 'adjust') await open(); let reject!: (error: Error) => void; (apiFor(action) as unknown as ReturnType<typeof vi.fn>).mockReturnValueOnce(new Promise((_resolve, fail) => { reject = fail; })); let saved!: Promise<unknown>; act(() => { saved = invoke(action); });
        view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); const errors = toastErrorMock.mock.calls.length; const successes = toastSuccessMock.mock.calls.length;
        await act(async () => { reject(new Error('Private stale write error')); await expect(saved).rejects.toThrow('Private stale write error'); }); expect(toastErrorMock).toHaveBeenCalledTimes(errors); expect(toastSuccessMock).toHaveBeenCalledTimes(successes);
      });
      it(`payment write matrix: ${action} rejects a retained handler before dispatch`, async () => {
        render(<LegalEntityModal {...propsFor()} />); const old = await open(); act(() => old.onClose()); await expect(invoke(action, old)).rejects.toBeInstanceOf(StaleUiOperationError); expect(apiFor(action)).not.toHaveBeenCalled();
      });
    }
    it('payment write matrix: a cross-command duplicate cannot overlap a pending update', async () => {
      let finish!: (value: Payment) => void; vi.mocked(apiService.updatePayment).mockReturnValue(new Promise(resolve => { finish = resolve; })); render(<LegalEntityModal {...propsFor()} />); const current = await open(); let saved!: Promise<unknown>; act(() => { saved = invoke('update', current); });
      await expect(invoke('transfer', current)).rejects.toBeInstanceOf(StaleUiOperationError); await expect(invoke('rollback', current)).rejects.toBeInstanceOf(StaleUiOperationError); await act(async () => { finish(payment); await saved; });
      expect(apiService.transferPaymentRemainder).not.toHaveBeenCalled(); expect(apiService.rollbackPaymentRemainderTransfer).not.toHaveBeenCalled();
    });
    it('payment refresh owner: returning a confirmed payment does not await or own the background read lock', async () => {
      render(<LegalEntityModal {...propsFor()} />); const old = await open(); let finish!: (value: Awaited<ReturnType<typeof apiService.getPaymentsV2>>) => void; vi.mocked(apiService.getPaymentsV2).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      await act(async () => { expect(await old.onSubmit(payload)).toBe(payment); }); act(() => old.onClose()); await open(); const before = counts();
      await act(async () => finish({ items: [{ ...payment, description: 'Private old background read' }], total: 1, page: 1, pageSize: 5, summary: { income: 0, expense: 0, balance: 0, incomeCount: 0, expenseCount: 0, total: 1 } }));
      expect(counts()).toEqual(before); expect(screen.queryByText('Private old background read')).toBeNull(); expect(screen.getByTestId('legal-related-payment-modal')).toBeTruthy();
    });
    it.each(['rollback', 'adjust'] as const)('payment refresh owner: completed %s cannot close a new form after its refresh waits', async action => {
      const props = propsFor(); const view = render(<LegalEntityModal {...props} />); const old = await open(); let finish!: (value: Awaited<ReturnType<typeof apiService.getPaymentsV2>>) => void; vi.mocked(apiService.getPaymentsV2).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      let saved!: Promise<unknown>; act(() => { saved = invoke(action, old); }); await waitFor(() => expect(apiFor(action)).toHaveBeenCalledOnce()); view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); await open(); const before = counts();
      await act(async () => { finish({ items: [], total: 0, page: 1, pageSize: 5, summary: { income: 0, expense: 0, balance: 0, incomeCount: 0, expenseCount: 0, total: 0 } }); await saved; }); expect(counts()).toEqual(before); expect(screen.getByTestId('legal-related-payment-modal')).toBeTruthy(); expect(toastSuccessMock).not.toHaveBeenCalled();
    });
    it.each([{ clientId: 69, legalEntityId: undefined, expectedClient: 69, expectedCompany: null }, { clientId: 0, legalEntityId: null, expectedClient: 0, expectedCompany: null }, { clientId: null, legalEntityId: undefined, expectedClient: null, expectedCompany: 9 }, { clientId: 69, legalEntityId: 8, expectedClient: null, expectedCompany: 8 }])('payment party: primary choice and fallback are explicit %j', async ({ clientId, legalEntityId, expectedClient, expectedCompany }) => {
      render(<LegalEntityModal {...propsFor()} />); const current = await open(); await act(async () => { await current.onSubmit({ ...payload, clientId, legalEntityId }); }); expect(apiService.createPayment).toHaveBeenCalledWith(expect.objectContaining({ clientId: expectedClient, legalEntityId: expectedCompany, amount: 123.456789 }));
    });
    it('payment receipt owner: a replaced company keeps the exact durable success and blocks a second registration until explicit recovery', async () => {
      const previousLocks = Object.getOwnPropertyDescriptor(navigator, 'locks'); Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: async (_name: string, _options: LockOptions, action: (lock: Lock) => Promise<unknown>) => action({ name: 'synthetic-payment-write', mode: 'exclusive' } as Lock) } }); bindPaymentCreateOwner(7, '/api/company-payment-write-tests');
      try {
        const now = new Date(); const state: PaymentAttemptState = { attemptId: '11111111-1111-4111-8111-111111111111', state: 'Draft', operation: 'primary', primaryPaymentId: null, sellerCompanyId: null, sellerBranchId: null, createdAtUtc: now.toISOString(), expiresAtUtc: new Date(now.getTime() + 3600000).toISOString(), canonicalPaymentId: null, identityVersion: 'payment-create-v1' };
        let finish!: (value: Payment) => void; const io: PaymentCreatePorts = { register: vi.fn().mockResolvedValue(state), commit: vi.fn().mockReturnValue(new Promise(resolve => { finish = resolve; })), get: vi.fn().mockResolvedValue({ ...state, state: 'Committed', canonicalPaymentId: 701 }), cancel: vi.fn(), payment: vi.fn().mockResolvedValue(payment) };
        vi.mocked(apiService.createPayment).mockImplementation(value => createPaymentGuarded(value, undefined, io)); const props = propsFor(); const view = render(<LegalEntityModal {...props} />); const old = await open(); let saved!: Promise<Payment>; act(() => { saved = old.onSubmit(payload); }); await waitFor(() => expect(io.commit).toHaveBeenCalledOnce());
        view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); const next = await open(); await act(async () => { finish(payment); expect(await saved).toBe(payment); }); const entry = pendingPaymentCreates()[0]; expect(entry.canonicalPaymentId).toBe(701);
        await act(async () => { await expect(next.onSubmit({ ...payload, legalEntityId: 8 })).rejects.toBeInstanceOf(PaymentCreateRecoveryError); }); expect(io.register).toHaveBeenCalledOnce(); expect(io.commit).toHaveBeenCalledOnce();
        const recovered = await openCommittedPaymentCreate(entry, io); await fencePaymentCreateAdoption(entry, recovered); await acknowledgePaymentCreate(entry, recovered); expect(pendingPaymentCreates()).toEqual([]); expect(io.commit).toHaveBeenCalledOnce();
      } finally {
        bindPaymentCreateOwner(null, '/api/company-payment-write-tests'); for (const key of Object.keys(localStorage)) if (key.startsWith('pp.payment-create.v1:') && key.includes('company-payment-write-tests')) localStorage.removeItem(key);
        if (previousLocks) Object.defineProperty(navigator, 'locks', previousLocks); else Reflect.deleteProperty(navigator, 'locks');
      }
    });
    it.each(['adopted', 'recovery'] as const)('payment receipt owner: preserves the %s error object for the existing continuation', async kind => {
      const error = kind === 'adopted' ? new PaymentCreateAdoptedError(payment) : new PaymentCreateRecoveryError({ attemptId: '11111111-1111-4111-8111-111111111111', operation: 'primary', primaryPaymentId: null, sellerCompanyId: null, sellerBranchId: null, canonicalPaymentId: null, createdAtUtc: '2026-10-31T00:00:00Z', expiresAtUtc: '2026-11-01T00:00:00Z', identityVersion: 'payment-create-v1', actorId: 7, apiScope: '/api/company-payment-write-tests', phase: 'dispatched' }, true);
      vi.mocked(apiService.createPayment).mockRejectedValue(error); render(<LegalEntityModal {...propsFor()} />); const current = await open(); await act(async () => { await expect(current.onSubmit(payload)).rejects.toBe(error); }); if (kind === 'adopted') expect(toastErrorMock).not.toHaveBeenCalled();
    });
  });

  describe('payment open ownership', () => {
    let session: ReturnType<typeof documentCopyTestSession>;
    beforeEach(() => { session = documentCopyTestSession(); });
    afterEach(() => session.restore());
    const payment = (id = 701, companyId = 9) => ({ id, legalEntityId: companyId, clientId: null, description: `Owned payment ${id}`, date: '2026-10-31', type: 'Expense', status: 'Pending', amount: 100, paidAmount: 0 } as Payment);
    const propsFor = (mode: 'page' | 'modal' = 'page') => ({ isOpen: true, mode, initial: createInitialLegalEntity({ isOwnCompany: false }), allowEdit: true, allowDelete: false,
      canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: true, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    const showPayments = () => vi.mocked(apiService.getPaymentsV2).mockResolvedValue({ items: [payment()], total: 1, page: 1, pageSize: 5, summary: { income: 0, expense: 100, balance: -100, incomeCount: 0, expenseCount: 1, total: 1 } });
    it('payment open owner: old details cannot open in another company', async () => {
      let finish!: (value: Payment) => void; showPayments(); vi.mocked(apiService.getPayment).mockReturnValue(new Promise(resolve => { finish = resolve; }));
      const props = propsFor(); const view = render(<LegalEntityModal {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Owned payment 701' }));
      view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); await act(async () => finish(payment()));
      expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull(); expect(toastErrorMock).not.toHaveBeenCalled();
    });

    for (const mode of ['page', 'modal'] as const) {
      it.each(['closed', 'reopen', 'readonly', 'edit-permission', 'view-permission', 'permission-aba', 'mode', 'version', 'unmount'] as const)(`payment open owner: pending details cannot follow %s in ${mode}`, async change => {
        let finish!: (value: Payment) => void; showPayments(); vi.mocked(apiService.getPayment).mockReturnValue(new Promise(resolve => { finish = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />); fireEvent.click(await screen.findByRole('button', { name: 'Owned payment 701' }));
        if (change === 'closed' || change === 'reopen') { view.rerender(<LegalEntityModal {...props} isOpen={false} />); if (change === 'reopen') view.rerender(<LegalEntityModal {...props} />); }
        if (change === 'readonly') view.rerender(<LegalEntityModal {...props} allowEdit={false} />);
        if (change === 'edit-permission' || change === 'permission-aba') { view.rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions({ canEdit: false }) }} />); if (change === 'permission-aba') view.rerender(<LegalEntityModal {...props} />); }
        if (change === 'view-permission') view.rerender(<LegalEntityModal {...props} canViewPayments={false} />);
        if (change === 'mode') view.rerender(<LegalEntityModal {...props} mode={mode === 'page' ? 'modal' : 'page'} />);
        if (change === 'version') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, profileEditVersion: 'B'.repeat(64) }} />);
        if (change === 'unmount') view.unmount();
        await act(async () => finish(payment())); expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull(); expect(toastErrorMock).not.toHaveBeenCalled();
      });

      it(`payment open owner: current details and token refresh open once in ${mode}`, async () => {
        let finish!: (value: Payment) => void; showPayments(); vi.mocked(apiService.getPayment).mockReturnValue(new Promise(resolve => { finish = resolve; }));
        render(<LegalEntityModal {...propsFor(mode)} />); const button = await screen.findByRole('button', { name: 'Owned payment 701' }); fireEvent.click(button); fireEvent.click(button);
        expect(apiService.getPayment).toHaveBeenCalledExactlyOnceWith(701); session.refresh(); await act(async () => finish({ ...payment(), notes: 'Full owned payment details' }));
        expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy(); expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({ payment: expect.objectContaining({ id: 701, notes: 'Full owned payment details' }) }));
      });
    }

    it.each(['login', 'actor', 'logout'] as const)('payment open owner: rejects old %s details without a render', async change => {
      let finish!: (value: Payment) => void; showPayments(); vi.mocked(apiService.getPayment).mockReturnValue(new Promise(resolve => { finish = resolve; }));
      render(<LegalEntityModal {...propsFor()} />); const button = await screen.findByRole('button', { name: 'Owned payment 701' }); fireEvent.click(button); session.replace(change); fireEvent.click(button);
      await act(async () => finish(payment())); expect(apiService.getPayment).toHaveBeenCalledOnce(); expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
    });

    it('payment open owner: only the latest distinct payment request opens an editor', async () => {
      const finish = new Map<number, (value: Payment) => void>(); showPayments(); const response = await vi.mocked(apiService.getPaymentsV2)();
      vi.mocked(apiService.getPaymentsV2).mockResolvedValue({ ...response, items: [payment(), payment(702)], total: 2 }); vi.mocked(apiService.getPayment).mockImplementation(id => new Promise(resolve => { finish.set(id, resolve); }));
      render(<LegalEntityModal {...propsFor()} />); fireEvent.click(await screen.findByRole('button', { name: 'Owned payment 701' })); fireEvent.click(await screen.findByRole('button', { name: 'Owned payment 702' }));
      await act(async () => finish.get(701)!(payment())); expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
      await act(async () => finish.get(702)!(payment(702))); expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({ payment: expect.objectContaining({ id: 702 }) }));
    });

    it('payment open owner: choosing a new payment cancels pending details without replacing the new form', async () => {
      let finish!: (value: Payment) => void; showPayments(); vi.mocked(apiService.getPayment).mockReturnValue(new Promise(resolve => { finish = resolve; }));
      render(<LegalEntityModal {...propsFor()} />); fireEvent.click(await screen.findByRole('button', { name: 'Owned payment 701' })); fireEvent.click(screen.getByRole('button', { name: 'Платёж' }));
      await act(async () => finish(payment())); expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({ payment: undefined })); expect(screen.getByTestId('legal-related-payment-modal')).toBeTruthy();
    });

    it('payment open owner: a current failure permits retry while a stale failure stays private', async () => {
      showPayments(); vi.mocked(apiService.getPayment).mockRejectedValueOnce(new Error('Current payment load failed')).mockResolvedValueOnce(payment());
      const props = propsFor(); const view = render(<LegalEntityModal {...props} />); const button = await screen.findByRole('button', { name: 'Owned payment 701' }); fireEvent.click(button);
      await waitFor(() => expect(toastErrorMock).toHaveBeenCalledOnce()); fireEvent.click(button); await screen.findByTestId('legal-related-payment-modal'); expect(apiService.getPayment).toHaveBeenCalledTimes(2);
      const close = paymentModalPropsSpy.mock.lastCall![0].onClose as () => void; act(() => close()); let reject!: (error: Error) => void; vi.mocked(apiService.getPayment).mockReturnValueOnce(new Promise((_resolve, fail) => { reject = fail; }));
      fireEvent.click(button); view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />); await act(async () => reject(new Error('Private old load error')));
      expect(toastErrorMock).toHaveBeenCalledOnce(); expect(screen.queryByText('Private old load error')).toBeNull();
    });

    it('payment open owner: a mismatched detail ID never opens an editor', async () => {
      showPayments(); vi.mocked(apiService.getPayment).mockResolvedValue(payment(702)); render(<LegalEntityModal {...propsFor()} />); fireEvent.click(await screen.findByRole('button', { name: 'Owned payment 701' }));
      await waitFor(() => expect(toastErrorMock).toHaveBeenCalledOnce()); expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
    });

    it('payment open owner: an invoice-linked row may retain its original parties', async () => {
      showPayments(); vi.mocked(apiService.getPayment).mockResolvedValue({ ...payment(), legalEntityId: 8, expenseLegalEntityId: null, invoiceId: 71 }); render(<LegalEntityModal {...propsFor()} />);
      fireEvent.click(await screen.findByRole('button', { name: 'Owned payment 701' })); expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy();
      expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({ payment: expect.objectContaining({ id: 701, legalEntityId: 8 }) }));
    });
  });

  describe('document client lookup lifecycle', () => {
    beforeEach(() => { vi.mocked(apiService.getClientsV2).mockReset(); });
    it.each(['Счёт', 'Акт'] as const)('document clients: a failed new opening never restores the previous list in %s', async name => {
      vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [lookupClient(91, 'Previous document client')], total: 1, page: 1, pageSize: 200 });
      render(<LegalEntityModal isOpen mode="page" initial={createInitialLegalEntity({ isOwnCompany: false })} allowEdit allowDelete={false} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()} />);
      fireEvent.click(await screen.findByRole('button', { name })); await screen.findByText('Previous document client');
      fireEvent.click(screen.getByRole('button', { name: 'Close document fixture' }));
      vi.mocked(apiService.getClientsV2).mockRejectedValue(new Error('Document clients unavailable'));
      fireEvent.click(screen.getByRole('button', { name })); await screen.findByText('Document clients unavailable');
      expect(screen.queryByText('Previous document client')).toBeNull(); expect(apiService.getClientsV2).toHaveBeenCalledTimes(2);
    });
    it.each(['Счёт', 'Акт'] as const)('document clients: load only on opening %s and refresh after reopening', async name => {
      vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [lookupClient(91, 'Fresh document client')], total: 1, page: 1, pageSize: 200 });
      render(<LegalEntityModal isOpen mode="page" initial={createInitialLegalEntity({ isOwnCompany: false })} allowEdit allowDelete={false} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()} />);
      expect(apiService.getClientsV2).not.toHaveBeenCalled(); fireEvent.click(await screen.findByRole('button', { name }));
      await screen.findByText('Fresh document client'); expect(apiService.getClientsV2).toHaveBeenCalledTimes(1);
      expect(apiService.getClientsV2).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 200, sortBy: 'name' }), expect.objectContaining({ signal: expect.any(AbortSignal) }));
      fireEvent.click(screen.getByRole('button', { name: 'Close document fixture' }));
      vi.mocked(apiService.getClientsV2).mockResolvedValue({ items: [lookupClient(92, 'New document client')], total: 1, page: 1, pageSize: 200 });
      fireEvent.click(screen.getByRole('button', { name })); await screen.findByText('New document client');
      expect(screen.queryByText('Fresh document client')).toBeNull(); expect(apiService.getClientsV2).toHaveBeenCalledTimes(2);
    });
    it.each(['Счёт', 'Акт'] as const)('document clients: revoke %s access while the request is pending', async name => {
      let finish!: (value: Awaited<ReturnType<typeof apiService.getClientsV2>>) => void;
      vi.mocked(apiService.getClientsV2).mockReturnValue(new Promise(resolve => { finish = resolve; }));
      const props = { isOpen: true, mode: 'page' as const, initial: createInitialLegalEntity({ isOwnCompany: false }), allowEdit: true, allowDelete: false, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() };
      const view = render(<LegalEntityModal {...props} />); fireEvent.click(await screen.findByRole('button', { name }));
      await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(1)); const signal = vi.mocked(apiService.getClientsV2).mock.calls[0][1]!.signal!;
      await screen.findByText('Loading clients');
      view.rerender(<LegalEntityModal {...props} canViewAccounts={false} canViewActs={false} />);
      expect(signal.aborted).toBe(true); await act(async () => finish({ items: [lookupClient(91, 'Private late document client')], total: 1, page: 1, pageSize: 200 }));
      expect(screen.queryByText('Private late document client')).toBeNull(); expect(screen.queryByTestId('invoice-lookup-fixture')).toBeNull(); expect(screen.queryByTestId('act-lookup-fixture')).toBeNull();
    });
  });

  describe('own settings authority', () => {
    let session: ReturnType<typeof documentCopyTestSession>;
    beforeEach(() => { session = documentCopyTestSession(); vi.mocked(apiService.getClientsV2).mockClear(); vi.mocked(apiService.getClient).mockClear(); });
    afterEach(() => session.restore());
    const propsFor = (mode: 'page' | 'modal', own?: boolean) => ({
      isOpen: true, mode, initial: createInitialLegalEntity({ shortName: 'Reviewed company', isOwnCompany: own, clients: [{ id: 7, name: 'Linked client', isActive: true }] }),
      allowEdit: true, allowDelete: false, onClose: vi.fn(), onSubmit: vi.fn().mockImplementation(async value => ({ ...value, id: 9 })),
      suggest: vi.fn(), findById: vi.fn(), canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: false,
    });
    for (const mode of ['page', 'modal'] as const) {
      it(`own settings: defaults deny administrative settings but permit requisites editing in ${mode}`, async () => {
        vi.mocked(apiService.getClient).mockClear(); const props = propsFor(mode, true);
        render(<LegalEntityModal {...props} />);
        expect(screen.queryByRole('button', { name: 'Добавить клиента' })).toBeNull(); expect(screen.queryByLabelText('Состояние профиля компании')).toBeNull();
        expect(screen.getByDisplayValue('Reviewed company')).toHaveProperty('disabled', false);
        expect(apiService.getMainCompanySettings).not.toHaveBeenCalled(); expect(apiService.getBankConnections).not.toHaveBeenCalled(); expect(apiService.getClient).not.toHaveBeenCalled();
        fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ }));
        await waitFor(() => expect(props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ clientIds: [7], profileStatus: 'Active' })));
      });
      it(`own settings: ordinary client links remain available without profile permission in ${mode}`, async () => {
        vi.mocked(apiService.getMainCompanySettings).mockRejectedValue(new Error('HTTP 403 Forbidden'));
        const props = propsFor(mode, false); render(<LegalEntityModal {...props} />);
        const add = screen.getByRole('button', { name: 'Добавить клиента' }); expect(add).toHaveProperty('disabled', false);
        expect(apiService.getMainCompanySettings).not.toHaveBeenCalled(); expect(apiService.getBankConnections).not.toHaveBeenCalled();
        fireEvent.click(add); expect(await screen.findByPlaceholderText('Поиск клиентов')).toBeTruthy(); expect(apiService.getClientsV2).toHaveBeenCalledTimes(1);
      });
      it(`own settings: revocation drops the picker and does not submit hidden link edits in ${mode}`, async () => {
        const props = propsFor(mode, true); const view = render(<LegalEntityModal {...props} canManageOwnCompanySettings />);
        await screen.findByLabelText('Состояние профиля компании');
        const section = screen.getByRole('heading', { name: /Клиенты юридического лица/i }).closest('div')!.parentElement!.parentElement!;
        const remove = Array.from(section.querySelectorAll('button')).find(button => !button.textContent?.trim())!;
        expect(remove).toBeTruthy(); fireEvent.click(remove);
        fireEvent.click(screen.getByRole('button', { name: 'Добавить клиента' })); await screen.findByPlaceholderText('Поиск клиентов');
        view.rerender(<LegalEntityModal {...props} canManageOwnCompanySettings={false} />);
        expect(screen.queryByPlaceholderText('Поиск клиентов')).toBeNull(); expect(screen.queryByLabelText('Состояние профиля компании')).toBeNull();
        fireEvent.click(screen.getByRole('button', { name: /^Сохранить$/ }));
        await waitFor(() => expect(props.onSubmit).toHaveBeenCalledWith(expect.objectContaining({ clientIds: [7] })));
      });
      it(`own settings: unknown legacy classification preserves input and retries explicitly in ${mode}`, async () => {
        vi.mocked(apiService.getMainCompanySettings).mockRejectedValue(new Error('Offline classification'));
        const props = propsFor(mode); render(<LegalEntityModal {...props} />);
        await screen.findByText('Принадлежность компании не подтверждена. Введённые данные остаются в форме.');
        expect(screen.queryByRole('button', { name: 'Добавить клиента' })).toBeNull(); expect(screen.getByRole('button', { name: /^Сохранить$/ })).toHaveProperty('disabled', true);
        fireEvent.change(screen.getByDisplayValue('Reviewed company'), { target: { value: 'Preserved draft' } });
        fireEvent.submit(document.querySelector('form')!); expect(props.onSubmit).not.toHaveBeenCalled();
        vi.mocked(apiService.getMainCompanySettings).mockResolvedValue({ companies: [], defaultCompanyId: null });
        fireEvent.click(screen.getByRole('button', { name: 'Повторить проверку принадлежности' }));
        await screen.findByRole('button', { name: 'Добавить клиента' }); expect(screen.getByDisplayValue('Preserved draft')).toBeTruthy(); expect(apiService.getMainCompanySettings).toHaveBeenCalledTimes(2);
      });
      it(`own settings: a late legacy result cannot reclassify another card in ${mode}`, async () => {
        let finish!: (value: Awaited<ReturnType<typeof apiService.getMainCompanySettings>>) => void;
        vi.mocked(apiService.getMainCompanySettings).mockReturnValue(new Promise(resolve => { finish = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />);
        view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8, isOwnCompany: false }} />);
        await act(async () => finish({ companies: [{ id: 8, shortName: 'Stale own classification', clientsCount: 0, createdAt: '' }], defaultCompanyId: 8 }));
        expect(screen.queryByLabelText('Состояние профиля компании')).toBeNull(); expect(screen.getByRole('button', { name: 'Добавить клиента' })).toHaveProperty('disabled', false);
      });
    }
    it.each(['company', 'rights', 'session'] as const)('own settings: pending picker results cannot follow a changed %s', async change => {
      let finish!: (value: Awaited<ReturnType<typeof apiService.getClientsV2>>) => void;
      vi.mocked(apiService.getClientsV2).mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
      const props = propsFor('page', true); const view = render(<LegalEntityModal {...props} canManageOwnCompanySettings />);
      fireEvent.click(screen.getByRole('button', { name: 'Добавить клиента' })); await waitFor(() => expect(apiService.getClientsV2).toHaveBeenCalledTimes(1));
      if (change === 'company') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8, isOwnCompany: false }} canManageOwnCompanySettings />);
      else if (change === 'rights') view.rerender(<LegalEntityModal {...props} canManageOwnCompanySettings={false} />);
      else session.replace();
      await act(async () => finish({ items: [lookupClient(91, 'Private late client')], total: 1, page: 1, pageSize: 24 }));
      expect(screen.queryByText('Private late client')).toBeNull(); expect(toastErrorMock).not.toHaveBeenCalled();
    });
    it('own settings: late linked-client hydration cannot adopt another session without a render', async () => {
      let finish!: () => void;
      vi.mocked(apiService.getClient).mockReturnValueOnce(new Promise(resolve => { finish = () => resolve(lookupClient(7, 'Private late hydration')); }));
      render(<LegalEntityModal {...propsFor('page', false)} />);
      await waitFor(() => expect(apiService.getClient).toHaveBeenCalledTimes(1)); session.replace();
      await act(async () => finish()); expect(screen.queryByText('Private late hydration')).toBeNull();
    });
  });

  for (const mode of ['page', 'modal'] as const) {
    it(`branch default: missing management authority remains readonly in ${mode}`, async () => {
      const session = documentCopyTestSession();
      try {
        render(<LegalEntityModal isOpen mode={mode} initial={createInitialLegalEntity({ id: 9 })} allowEdit allowDelete={false}
          onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()} canViewCases={false} canViewAccounts={false} canViewActs={false} canViewPayments={false} />);
        await waitFor(() => expect(screen.getByTestId('company-branches-section').textContent).toContain('Подразделения не добавлены.'));
        expect(screen.getByTestId('company-branches-section').querySelector('button')).toBeNull();
      } finally { session.restore(); }
    });

    it(`branch hookup: a compact read-only assigned profile shows branches in ${mode}`, async () => {
      const session = documentCopyTestSession();
      try {
        vi.mocked(apiService.getCompanyBranches).mockResolvedValueOnce([{ id: 19, legalEntityId: 9, name: 'Read-only office', kind: 'Office', status: 'Active', createdAt: '2026-10-30T00:00:00Z' }]);
        render(<LegalEntityModal isOpen mode={mode} initial={createInitialLegalEntity({ id: 9 })} allowEdit={false} allowDelete={false} canManageOwnCompanySettings={false} compactReadOnlyView hideFormActions
          onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()} canViewCases={false} canViewAccounts={false} canViewActs={false} canViewPayments={false} />);
        expect(await screen.findByText('Read-only office')).toBeTruthy(); expect(screen.getByTestId('company-branches-section').querySelector('button')).toBeNull();
      } finally { session.restore(); }
    });

    it(`branch hookup: changing the parent profile version closes its branch editor in ${mode}`, async () => {
      const session = documentCopyTestSession();
      try {
        const initial = createInitialLegalEntity({ id: 9, profileEditVersion: 'A'.repeat(64) });
        const props = { isOpen: true, mode, initial, allowEdit: true, allowDelete: false, canManageCompanyBranches: true, canManageOwnCompanySettings: true, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn(), canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: false };
        const view = render(<LegalEntityModal {...props} />);
        const add = await screen.findByRole('button', { name: /^Добавить$/ }); await waitFor(() => expect(add).toHaveProperty('disabled', false));
        fireEvent.click(add); expect(screen.getByRole('dialog', { name: 'Подразделение' })).toBeTruthy();
        view.rerender(<LegalEntityModal {...props} initial={{ ...initial, profileEditVersion: 'B'.repeat(64) }} />);
        expect(screen.queryByRole('dialog', { name: 'Подразделение' })).toBeNull();
      } finally { session.restore(); }
    });

    it(`branch hookup: revoking management keeps profile editing but removes branch editing in ${mode}`, async () => {
      const session = documentCopyTestSession();
      try {
        const props = { isOpen: true, mode, initial: createInitialLegalEntity({ id: 9 }), allowEdit: true, allowDelete: false, canManageCompanyBranches: true, canManageOwnCompanySettings: true, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn(), canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: false };
        const view = render(<LegalEntityModal {...props} />); const add = await screen.findByRole('button', { name: /^Добавить$/ });
        await waitFor(() => expect(add).toHaveProperty('disabled', false)); fireEvent.click(add);
        view.rerender(<LegalEntityModal {...props} canManageOwnCompanySettings={false} />);
        expect(screen.queryByRole('dialog', { name: 'Подразделение' })).toBeNull();
        await waitFor(() => expect(screen.getByTestId('company-branches-section').textContent).toContain('Подразделения не добавлены.'));
        expect(screen.getByTestId('company-branches-section').querySelector('button')).toBeNull();
      } finally { session.restore(); }
    });

    it(`branch hookup: a counterparty does not load own-company branches in ${mode}`, async () => {
      const session = documentCopyTestSession();
      try {
        render(<LegalEntityModal isOpen mode={mode} initial={createInitialLegalEntity({ id: 77 })} allowEdit allowDelete={false}
          onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()} canViewCases={false} canViewAccounts={false} canViewActs={false} canViewPayments={false} />);
        await waitFor(() => expect(apiService.getMainCompanySettings).toHaveBeenCalled());
        expect(screen.queryByTestId('company-branches-section')).toBeNull(); expect(apiService.getCompanyBranches).not.toHaveBeenCalled();
      } finally { session.restore(); }
    });
  }

  it.each(['page', 'modal'] as const)('branch access baseline: shows assigned branches without administrator actions in %s', async mode => {
    const session = documentCopyTestSession();
    try {
      const initial = createInitialLegalEntity({ id: 9, shortName: 'Assigned own company' });
      vi.mocked(apiService.getCompanyBranches).mockResolvedValueOnce([{ id: 19, legalEntityId: 9, name: 'Assigned office', kind: 'Office', status: 'Active', createdAt: '2026-10-30T00:00:00Z' }]);
      render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} canManageOwnCompanySettings={false}
        canViewCases={false} canViewAccounts={false} canViewActs={false} canViewPayments={false}
        onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()} />);
      expect(await screen.findByText('Assigned office')).toBeTruthy();
      const section = screen.getByTestId('company-branches-section');
      expect(section.querySelector('button')).toBeNull();
      expect(screen.queryByLabelText('Состояние профиля компании')).toBeNull();
      expect(screen.getByDisplayValue('Assigned own company')).toHaveProperty('disabled', false);
      expect(apiService.getBankConnections).not.toHaveBeenCalled();
    } finally { session.restore(); }
  });

  describe('deletion guard', () => {
    let session: ReturnType<typeof documentCopyTestSession>;
    beforeEach(() => { session = documentCopyTestSession(); });
    afterEach(() => session.restore());
    const propsFor = (mode: 'page' | 'modal') => ({
      isOpen: true, mode, initial: createInitialLegalEntity({ id: 7, shortName: 'Reviewed delete target' }), allowEdit: true, allowDelete: true,
      onDelete: vi.fn().mockResolvedValue(undefined), onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn(),
    });
    for (const mode of ['page', 'modal'] as const) {
      it(`deletion generation: cancels the parent role scope in ${mode}`, async () => {
        let approve!: (value: boolean) => void;
        confirmMock.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} deleteOwnerScope="role-one" />);
        fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ }));
        const signal = confirmMock.mock.lastCall![0].signal as AbortSignal;
        view.rerender(<LegalEntityModal {...props} deleteOwnerScope="role-two" />);
        expect(signal.aborted).toBe(true);
        await act(async () => { approve(true); });
        expect(props.onDelete).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled();
      });

      it(`deletion generation: an old result cannot unlock a new deletion in ${mode}`, async () => {
        let finishFirst!: () => void; let finishSecond!: () => void;
        confirmMock.mockResolvedValue(true);
        const first = propsFor(mode); first.onDelete.mockReturnValue(new Promise<void>(resolve => { finishFirst = resolve; }));
        const second = { ...propsFor(mode), initial: createInitialLegalEntity({ id: 8, shortName: 'Second deletion' }) };
        second.onDelete.mockReturnValue(new Promise<void>(resolve => { finishSecond = resolve; }));
        const view = render(<LegalEntityModal {...first} />);
        fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ })); await waitFor(() => expect(first.onDelete).toHaveBeenCalledOnce());
        view.rerender(<LegalEntityModal {...second} />);
        const button = screen.getByRole('button', { name: /^Удалить$/ }); fireEvent.click(button);
        await waitFor(() => expect(second.onDelete).toHaveBeenCalledOnce());
        await act(async () => { finishFirst(); });
        expect(button).toHaveProperty('disabled', true); fireEvent.submit(button.closest('form')!);
        expect(second.onSubmit).not.toHaveBeenCalled(); expect(first.onClose).not.toHaveBeenCalled();
        await act(async () => { finishSecond(); }); expect(second.onClose).toHaveBeenCalledOnce();
      });

      it.each(['closed', 'permission', 'readonly', 'mode', 'same-id-data', 'unmount', 'reopen', 'permission-aba'])(`rejects a stale %s confirmation in ${mode}`, async change => {
        let approve!: (value: boolean) => void;
        confirmMock.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />);
        fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ }));
        if (change === 'closed') view.rerender(<LegalEntityModal {...props} isOpen={false} />);
        if (change === 'permission') view.rerender(<LegalEntityModal {...props} allowDelete={false} />);
        if (change === 'readonly') view.rerender(<LegalEntityModal {...props} allowEdit={false} />);
        if (change === 'mode') view.rerender(<LegalEntityModal {...props} mode={mode === 'page' ? 'modal' : 'page'} />);
        if (change === 'same-id-data') view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, shortName: 'Updated same company' }} />);
        if (change === 'unmount') view.unmount();
        if (change === 'reopen') { view.rerender(<LegalEntityModal {...props} isOpen={false} />); view.rerender(<LegalEntityModal {...props} />); }
        if (change === 'permission-aba') { view.rerender(<LegalEntityModal {...props} allowDelete={false} />); view.rerender(<LegalEntityModal {...props} />); }
        await act(async () => { approve(true); });
        expect(props.onDelete).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
      });

      it.each(['login', 'actor', 'logout'] as const)(`rejects an old %s confirmation without a rerender in ${mode}`, async change => {
        let approve!: (value: boolean) => void;
        confirmMock.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
        const props = propsFor(mode); render(<LegalEntityModal {...props} />);
        fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ }));
        session.replace(change);
        await act(async () => { approve(true); });
        expect(props.onDelete).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
      });

      it.each(['cancel', 'confirm-error', 'server-error'])(`preserves the draft after %s in ${mode}`, async outcome => {
        const props = propsFor(mode);
        if (outcome === 'cancel') confirmMock.mockResolvedValue(false);
        if (outcome === 'confirm-error') confirmMock.mockRejectedValue(new Error('Confirmation unavailable'));
        if (outcome === 'server-error') { confirmMock.mockResolvedValue(true); props.onDelete.mockRejectedValue(new Error('Company changed, deletion refused')); }
        render(<LegalEntityModal {...props} />);
        const name = await screen.findByDisplayValue('Reviewed delete target');
        fireEvent.change(name, { target: { value: 'Unsaved company draft' } });
        const button = screen.getByRole('button', { name: /^Удалить$/ }); fireEvent.click(button);
        if (outcome !== 'cancel') await waitFor(() => expect(toastErrorMock).toHaveBeenCalledOnce());
        await waitFor(() => expect(button).toHaveProperty('disabled', false));
        expect(screen.getByDisplayValue('Unsaved company draft')).toBeTruthy(); expect(props.onClose).not.toHaveBeenCalled();
        expect(props.onDelete).toHaveBeenCalledTimes(outcome === 'server-error' ? 1 : 0);
      });

      it.each(['normal', 'readonly', 'refresh'])(`closes exactly once after an owned %s deletion in ${mode}`, async kind => {
        let approve!: (value: boolean) => void;
        confirmMock.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} allowEdit={kind !== 'readonly'} />);
        fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ }));
        if (kind === 'refresh') { session.refresh(); view.rerender(<LegalEntityModal {...props} />); }
        await act(async () => { approve(true); });
        await waitFor(() => expect(props.onClose).toHaveBeenCalledOnce()); expect(props.onDelete).toHaveBeenCalledOnce(); expect(toastErrorMock).not.toHaveBeenCalled();
      });

      it(`blocks duplicate deletion and saving during confirmation and request in ${mode}`, async () => {
        let approve!: (value: boolean) => void; let finish!: () => void;
        confirmMock.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
        const props = propsFor(mode); props.onDelete.mockReturnValue(new Promise<void>(resolve => { finish = resolve; }));
        render(<LegalEntityModal {...props} />);
        const button = await screen.findByRole('button', { name: /^Удалить$/ }); const form = button.closest('form')!;
        fireEvent.click(button); fireEvent.click(button); fireEvent.submit(form);
        expect(confirmMock).toHaveBeenCalledOnce(); expect(props.onSubmit).not.toHaveBeenCalled();
        expect(screen.getByRole('button', { name: /^Сохранить$/ })).toHaveProperty('disabled', true);
        await act(async () => { approve(true); });
        await waitFor(() => expect(props.onDelete).toHaveBeenCalledOnce());
        fireEvent.click(button); fireEvent.submit(form);
        expect(props.onDelete).toHaveBeenCalledOnce(); expect(props.onSubmit).not.toHaveBeenCalled();
        await act(async () => { finish(); }); expect(props.onClose).toHaveBeenCalledOnce();
      });

      it(`blocks deletion while the current save is pending in ${mode}`, async () => {
        let finish!: (value: LegalEntityDetail) => void;
        const props = propsFor(mode); props.onSubmit.mockReturnValue(new Promise<LegalEntityDetail>(resolve => { finish = resolve; }));
        render(<LegalEntityModal {...props} />);
        fireEvent.click(await screen.findByRole('button', { name: /^Сохранить$/ }));
        await waitFor(() => expect(props.onSubmit).toHaveBeenCalledOnce());
        const button = screen.getByRole('button', { name: /^Удалить$/ }); expect(button).toHaveProperty('disabled', true); fireEvent.click(button);
        expect(confirmMock).not.toHaveBeenCalled(); expect(props.onDelete).not.toHaveBeenCalled();
        await act(async () => { finish(props.initial); });
      });

      it(`discards an obsolete confirmation error in ${mode}`, async () => {
        let fail!: (error: Error) => void;
        confirmMock.mockReturnValue(new Promise<boolean>((_resolve, reject) => { fail = reject; }));
        const props = propsFor(mode); const view = render(<LegalEntityModal {...props} />);
        fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ }));
        view.rerender(<LegalEntityModal {...props} initial={{ ...props.initial, id: 8 }} />);
        await act(async () => { fail(new Error('Old confirmation failed')); });
        expect(props.onDelete).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
        expect(screen.queryByText('Old confirmation failed')).toBeNull();
      });
    }
  });

  it.each(['page', 'modal'] as const)('deletion ownership: drops confirmation after a company change in %s', async mode => {
    const session = documentCopyTestSession();
    try {
      let approve!: (value: boolean) => void;
      confirmMock.mockReturnValue(new Promise<boolean>(resolve => { approve = resolve; }));
      const initial = createInitialLegalEntity({ id: 7, shortName: 'Original delete target' });
      const props = { isOpen: true, mode, initial, allowEdit: true, allowDelete: true, onDelete: vi.fn().mockResolvedValue(undefined), onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() };
      const view = render(<LegalEntityModal {...props} />);
      fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ }));
      expect(confirmMock).toHaveBeenCalledOnce();
      view.rerender(<LegalEntityModal {...props} initial={createInitialLegalEntity({ id: 8, shortName: 'Current delete target' })} />);
      await act(async () => { approve(true); });
      expect(props.onDelete).not.toHaveBeenCalled(); expect(props.onClose).not.toHaveBeenCalled();
      expect(screen.getByDisplayValue('Current delete target')).toBeTruthy();
    } finally { session.restore(); }
  });

  for (const mode of ['page', 'modal'] as const) it.each(['success', 'failure'])('deletion ownership: discards a late %s in '+mode, async outcome => {
    const session = documentCopyTestSession();
    try {
      let finish!: () => void; let fail!: (error: Error) => void;
      const pending = new Promise<void>((resolve, reject) => { finish = resolve; fail = reject; });
      confirmMock.mockResolvedValue(true);
      const initial = createInitialLegalEntity({ id: 7, shortName: 'Original delete target' });
      const props = { isOpen: true, mode, initial, allowEdit: true, allowDelete: true, onDelete: vi.fn().mockReturnValue(pending), onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() };
      const view = render(<LegalEntityModal {...props} />);
      fireEvent.click(await screen.findByRole('button', { name: /^Удалить$/ }));
      await waitFor(() => expect(props.onDelete).toHaveBeenCalledOnce());
      view.rerender(<LegalEntityModal {...props} initial={createInitialLegalEntity({ id: 8, shortName: 'Current delete target' })} />);
      await act(async () => { if (outcome === 'success') finish(); else fail(new Error('Old deletion failed')); });
      expect(props.onClose).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
      expect(screen.queryByText('Old deletion failed')).toBeNull();
      expect(screen.getByDisplayValue('Current delete target')).toBeTruthy();
    } finally { session.restore(); }
  });

  it.each(['page', 'modal'] as const)('company creation conflict: keeps new input without an automatic retry in %s', async mode => {
    const user = userEvent.setup();
    const onSubmit = vi.fn().mockRejectedValue(new Error('Карточка не создана. Проверьте список компаний и повторите действие.'));
    const onClose = vi.fn();
    render(<LegalEntityModal isOpen mode={mode} allowEdit allowDelete={false} onClose={onClose} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    const name = document.querySelector<HTMLInputElement>('input[name="shortName"]')!;
    await user.type(name, 'New company draft');
    await user.click(screen.getByRole('button', { name: /^Добавить$/ }));
    await screen.findByText('Карточка не создана. Проверьте список компаний и повторите действие.');
    expect(onSubmit).toHaveBeenCalledTimes(1); expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByDisplayValue('New company draft')).toBeTruthy();
    expect(onSubmit.mock.calls[0][0]).not.toHaveProperty('profileEditVersion');
    expect(screen.getByRole('button', { name: /^Добавить$/ })).toHaveProperty('disabled', false);
  });

  it.each(['page', 'modal'] as const)('profile version: preserves the draft and original version on conflict in %s', async mode => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Original company' });
    const onSubmit = vi.fn().mockRejectedValue(new Error('Профиль компании изменился. Откройте карточку заново.'));
    const onClose = vi.fn();
    render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} onClose={onClose} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    const name = await screen.findByDisplayValue('Original company');
    await user.clear(name); await user.type(name, 'Unsaved draft');
    await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
    await screen.findByText('Профиль компании изменился. Откройте карточку заново.');
    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toMatchObject({ shortName: 'Unsaved draft', profileEditVersion: 'A'.repeat(64) });
    expect(screen.getByDisplayValue('Unsaved draft')).toBeTruthy();
    expect(onClose).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    expect(onSubmit.mock.calls[1][0].profileEditVersion).toBe('A'.repeat(64));
  });

  it.each(['page', 'modal'] as const)('profile version: blocks an old own-company form without losing input in %s', async mode => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    render(<LegalEntityModal isOpen mode={mode} initial={createInitialLegalEntity({ shortName: 'Old tab', profileEditVersion: null })} allowEdit allowDelete={false} onClose={vi.fn()} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    await user.click(await screen.findByRole('button', { name: /^Сохранить$/ }));
    await screen.findByText('Для безопасного сохранения откройте карточку компании заново. Введённые данные пока остаются на экране.');
    expect(screen.getByDisplayValue('Old tab')).toBeTruthy(); expect(onSubmit).not.toHaveBeenCalled();
  });

  it('profile version: adopts the saved version for a second page save without fetching it over the draft', async () => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Company' });
    const onSubmit = vi.fn().mockResolvedValue({ ...initial, profileEditVersion: 'B'.repeat(64) });
    render(<LegalEntityModal isOpen mode="page" initial={initial} allowEdit allowDelete={false} onClose={vi.fn()} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    await user.click(await screen.findByRole('button', { name: /^Сохранить$/ }));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Сохранить$/ })).toHaveProperty('disabled', false));
    await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    expect(onSubmit.mock.calls[0][0].profileEditVersion).toBe('A'.repeat(64));
    expect(onSubmit.mock.calls[1][0].profileEditVersion).toBe('B'.repeat(64));
  });

  it.each((['page', 'modal'] as const).flatMap(mode => (['success', 'error'] as const).map(outcome => [mode, outcome] as const)))('profile version: ignores a late %s save after a fresh profile (%s)', async (mode, outcome) => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Original' });
    let resolve!: (value: LegalEntityDetail) => void; let reject!: (error: Error) => void;
    const pending = new Promise<LegalEntityDetail>((ok, fail) => { resolve = ok; reject = fail; });
    const props = { isOpen: true, mode, initial, allowEdit: true, allowDelete: false, onClose: vi.fn(), onSubmit: vi.fn().mockReturnValue(pending), suggest: vi.fn(), findById: vi.fn() };
    const view = render(<LegalEntityModal {...props} />);
    await user.click(await screen.findByRole('button', { name: /^Сохранить$/ }));
    await waitFor(() => expect(props.onSubmit).toHaveBeenCalledTimes(1));
    view.rerender(<LegalEntityModal {...props} initial={{ ...initial, shortName: 'Fresh profile', profileEditVersion: 'C'.repeat(64) }} />);
    await act(async () => { if (outcome === 'success') resolve({ ...initial, shortName: 'Late stale profile', profileEditVersion: 'B'.repeat(64) }); else reject(new Error('Late stale conflict')); });
    expect(screen.getByDisplayValue('Fresh profile')).toBeTruthy();
    expect(screen.queryByDisplayValue('Late stale profile')).toBeNull(); expect(screen.queryByText('Late stale conflict')).toBeNull();
    expect(props.onClose).not.toHaveBeenCalled();
  });

  beforeEach(() => {
    vi.mocked(apiService.getCompanyBranches).mockClear();
    vi.mocked(apiService.getCompanyRequisitesCheck).mockClear();
    vi.mocked(apiService.getBankConnections).mockClear();
    vi.mocked(apiService.getBankConnections).mockResolvedValue([]);
    confirmMock.mockReset();
    toastErrorMock.mockReset();
    toastInfoMock.mockReset();
    toastSuccessMock.mockReset();
    paymentModalPropsSpy.mockReset();
    vi.mocked(apiService.suggestClientAddresses).mockReset();
    vi.mocked(apiService.cleanAddress).mockReset();
    vi.mocked(apiService.getMainCompanySettings).mockReset();
    vi.mocked(apiService.getCasesV1).mockReset();
    vi.mocked(apiService.getInvoices).mockReset();
    vi.mocked(apiService.getInvoicesSummary).mockReset();
    vi.mocked(apiService.getActs).mockReset();
    vi.mocked(apiService.getActsSummary).mockReset();
    vi.mocked(apiService.getPaymentsV2).mockReset();
    vi.mocked(apiService.getPayment).mockReset();
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockReset();
    vi.mocked(apiService.suggestClientAddresses).mockResolvedValue([]);
    vi.mocked(apiService.cleanAddress).mockResolvedValue({ result: '' });
    vi.mocked(apiService.getMainCompanySettings).mockResolvedValue({
      defaultCompanyId: 9,
      companies: [{ id: 9, shortName: 'Наша компания', clientsCount: 0, createdAt: '2026-04-09T13:18:06Z' }],
    });
    vi.mocked(apiService.getCasesV1).mockResolvedValue([]);
    vi.mocked(apiService.getInvoices).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
    vi.mocked(apiService.getInvoicesSummary).mockResolvedValue(emptyInvoiceSummary);
    vi.mocked(apiService.getActs).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 5 });
    vi.mocked(apiService.getActsSummary).mockResolvedValue(emptyActsSummary);
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 5,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    });
    vi.mocked(apiService.getLegalEntityCaseFinanceSummaries).mockResolvedValue([]);
  });

  const relatedPermissions = (allowed: Partial<MenuSectionPermissions> = {}): MenuSectionPermissions => ({
    canView: true, canCreate: false, canEdit: false, canDelete: false, canExport: false, ...allowed,
  });

  it('related permissions: viewing sections does not enable creation from an editable company profile', async () => {
    const initial = createInitialLegalEntity({ shortName: 'Editable profile' });
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()}
      allowEdit allowDelete={false} relatedRecordPermissions={{ cases: relatedPermissions(), accounts: relatedPermissions(), acts: relatedPermissions(), payments: relatedPermissions() }} />);
    for (const name of ['Дело', 'Счёт', 'Акт', 'Платёж']) {
      for (const button of await screen.findAllByRole('button', { name })) expect(button).toHaveProperty('disabled', true);
    }
    expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
  });

  it.each(['cases', 'accounts', 'acts', 'payments'] as const)('related permissions: only independently granted %s creation is available', async section => {
    const initial = createInitialLegalEntity({ shortName: 'Editable profile' });
    const props = {
      isOpen: true, initial, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn(), allowEdit: true, allowDelete: false,
      relatedRecordPermissions: { [section]: relatedPermissions({ canCreate: true }) },
    };
    render(<LegalEntityModal {...props} />);
    const names = { cases: 'Дело', accounts: 'Счёт', acts: 'Акт', payments: 'Платёж' };
    for (const [key, name] of Object.entries(names)) {
      for (const button of await screen.findAllByRole('button', { name })) expect(button).toHaveProperty('disabled', key !== section);
    }
    if (section === 'payments') {
      await userEvent.click(screen.getByRole('button', { name: 'Платёж' }));
      expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy();
      expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(expect.objectContaining({ canCreateLinkedInvoices: false }));
    }
  });

  it('related permissions: explicit account creation prohibition applies without an additional matrix', async () => {
    const initial = createInitialLegalEntity();
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()}
      allowEdit allowDelete={false} canViewAccounts canCreateAccounts={false} />);
    for (const button of await screen.findAllByRole('button', { name: 'Счёт' })) expect(button).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Платёж' })).toHaveProperty('disabled', false);
  });

  it('related permissions: an opened payment form disappears immediately when its creation permission is revoked', async () => {
    const initial = createInitialLegalEntity();
    const props = { isOpen: true, initial, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn(), allowEdit: true, allowDelete: false };
    const { rerender } = render(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions({ canCreate: true }) }} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Платёж' }));
    expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy();
    rerender(<LegalEntityModal {...props} relatedRecordPermissions={{ payments: relatedPermissions() }} />);
    expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
    expect(screen.getByRole('button', { name: 'Платёж' })).toHaveProperty('disabled', true);
  });

  it.each([false, true])('related permissions: payment viewing preserves independent edit permission=%s', async canEdit => {
    const initial = createInitialLegalEntity();
    const payment = { id: 901, description: 'Visible related payment', date: '2026-10-23', type: 'Expense', status: 'Pending', amount: 100, paidAmount: 0 } as Payment;
    vi.mocked(apiService.getPaymentsV2).mockResolvedValue({ items: [payment], total: 1, page: 1, pageSize: 5,
      summary: { income: 0, expense: 100, balance: -100, incomeCount: 0, expenseCount: 1, total: 1 } });
    vi.mocked(apiService.getPayment).mockResolvedValue(payment);
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={vi.fn()} suggest={vi.fn()} findById={vi.fn()}
      allowEdit allowDelete={false} relatedRecordPermissions={{ payments: relatedPermissions({ canEdit }) }} />);
    const button = await screen.findByRole('button', { name: payment.description! });
    expect(button).toHaveProperty('disabled', !canEdit);
    await userEvent.click(button);
    if (canEdit) {
      expect(await screen.findByTestId('legal-related-payment-modal')).toBeTruthy();
      expect(apiService.getPayment).toHaveBeenCalledWith(901);
    } else {
      expect(apiService.getPayment).not.toHaveBeenCalled();
      expect(screen.queryByTestId('legal-related-payment-modal')).toBeNull();
    }
  });

  it('loads full payment details before opening the legal entity payment editor', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });
    const compactPayment = {
      id: 701,
      date: '2026-07-17',
      plannedDate: '2026-07-17',
      amount: 8_500,
      paidAmount: 0,
      type: 'Expense',
      status: 'Pending',
      description: 'Компактный платеж юрлица',
      notes: '',
      systemNotes: '',
      isPaid: false,
      rescheduleCount: 0,
      createdAt: '2026-07-17T00:00:00Z',
    } as Payment;
    vi.mocked(apiService.getPaymentsV2).mockResolvedValueOnce({
      items: [compactPayment],
      total: 1,
      page: 1,
      pageSize: 5,
      summary: {
        income: 0,
        expense: 8_500,
        balance: -8_500,
        incomeCount: 0,
        expenseCount: 1,
        total: 1,
      },
    });
    vi.mocked(apiService.getPayment).mockResolvedValueOnce({
      ...compactPayment,
      notes: 'Полные данные платежа',
    });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
        canViewPayments
      />,
    );

    await userEvent.click(await screen.findByRole('button', { name: 'Компактный платеж юрлица' }));

    await waitFor(() => {
      expect(apiService.getPayment).toHaveBeenCalledWith(701);
      expect(paymentModalPropsSpy).toHaveBeenLastCalledWith(
        expect.objectContaining({ isOpen: true, payment: expect.objectContaining({ id: 701 }) }),
      );
    });
  });

  it('does not load legal entity payments or case finance summaries without payment permission', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
        canViewPayments={false}
      />,
    );

    await waitFor(() => expect(apiService.getMainCompanySettings).toHaveBeenCalled());

    expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
    expect(apiService.getLegalEntityCaseFinanceSummaries).not.toHaveBeenCalled();
    expect(screen.queryByText('Платежи')).toBeNull();
  });

  it('does not load or show closed related sections in legal entity card', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
        canViewCases={false}
        canViewAccounts={false}
        canViewActs={false}
        canViewPayments={false}
      />,
    );

    await waitFor(() => expect(apiService.getMainCompanySettings).toHaveBeenCalled());

    expect(apiService.getCasesV1).not.toHaveBeenCalled();
    expect(apiService.getInvoices).not.toHaveBeenCalled();
    expect(apiService.getInvoicesSummary).not.toHaveBeenCalled();
    expect(apiService.getActs).not.toHaveBeenCalled();
    expect(apiService.getActsSummary).not.toHaveBeenCalled();
    expect(apiService.getPaymentsV2).not.toHaveBeenCalled();
    expect(apiService.getLegalEntityCaseFinanceSummaries).not.toHaveBeenCalled();
    expect(screen.queryByText('Дела и документы')).toBeNull();
    // Document-kind legends in profile preferences do not grant access to financial sections.
    expect(screen.queryByRole('heading', { name: 'Счета' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Акты' })).toBeNull();
    expect(screen.queryByText('Платежи')).toBeNull();
  });

  describe('company directory parent ownership', () => {
    it.each(['page', 'modal'] as const)('directory caller: profile %s reads companies only for an open document and clears a changed scope', async mode => {
      const p = { isOpen: true, mode, initial: createInitialLegalEntity({ shortName: 'Компания', isOwnCompany: false }), allowEdit: true, allowDelete: false,
        canManageOwnCompanySettings: false, canViewCases: false, canViewAccounts: true, canViewActs: false, canViewPayments: false,
        onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() };
      vi.mocked(apiService.getLegalEntities).mockClear().mockResolvedValue([{ id: 301, shortName: 'Каталог all', clientsCount: 0, createdAt: '2026-11-01T00:00:00Z' }]);
      const view = render(<LegalEntityModal {...p} />); await act(async () => undefined); expect(apiService.getLegalEntities).not.toHaveBeenCalled();
      fireEvent.click(screen.getAllByRole('button', { name: 'Счёт' })[0]); await screen.findByTestId('invoice-lookup-fixture');
      const last = () => invoiceModalPropsSpy.mock.calls.at(-1)![0] as { legalEntities: { id: number }[] };
      await waitFor(() => expect(last().legalEntities.map(row => row.id)).toEqual([301]));
      let resolve!: (value: LegalEntitySummary[]) => void;
      vi.mocked(apiService.getLegalEntities).mockReturnValueOnce(new Promise(done => { resolve = done; }));
      view.rerender(<LegalEntityModal {...p} relatedRecordPermissions={{ accounts: relatedPermissions({ canCreate: true, viewScope: 'own' }) }} />);
      expect(last().legalEntities).toEqual([]);
      await act(async () => resolve([{ id: 302, shortName: 'Каталог own', clientsCount: 0, createdAt: '2026-11-01T00:00:00Z' }]));
      await waitFor(() => expect(last().legalEntities.map(row => row.id)).toEqual([302]));
    });
  });

  describe('company address autofill ownership', () => {
    const propsFor = (mode: 'page' | 'modal') => ({ isOpen: true, mode, initial: createInitialLegalEntity({ shortName: 'Профиль адресов', isOwnCompany: false,
      addresses: [{ id: 1, type: 3, value: 'Первый адрес', isPrimary: true, createdAt: '2026-11-01T00:00:00Z' }, { id: 2, type: 2, value: 'Второй адрес', isPrimary: false, createdAt: '2026-11-01T00:00:00Z' }] }),
      allowEdit: true, allowDelete: false, canManageOwnCompanySettings: false, canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: false,
      onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() });
    for (const mode of ['page', 'modal'] as const) {
      it.each(['manual', 'delete', 'company'] as const)(`company address baseline: ${mode} clean result cannot cross %s`, async change => {
        let resolve!: (value: { result: string; cityWithType: string }) => void;
        vi.mocked(apiService.cleanAddress).mockReturnValue(new Promise(done => { resolve = done; }));
        const p = propsFor(mode); const view = render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Адреса' }));
        fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]); expect(apiService.cleanAddress).toHaveBeenCalledOnce();
        if (change === 'manual') fireEvent.change(screen.getAllByLabelText('Полный адрес')[0], { target: { value: 'Ручной адрес' } });
        if (change === 'delete') fireEvent.click(screen.getAllByRole('button', { name: 'Удалить адрес' })[0]);
        if (change === 'company') view.rerender(<LegalEntityModal {...p} initial={{ ...p.initial, id: 12, shortName: 'Новый профиль' }} />);
        await act(async () => resolve({ result: 'Старый результат', cityWithType: 'Устаревший город' }));
        expect(screen.queryByDisplayValue('Устаревший город')).toBeNull();
        if (change === 'manual') expect(screen.getByDisplayValue('Ручной адрес')).toBeTruthy();
        if (change === 'delete') expect(screen.getByDisplayValue('Второй адрес')).toBeTruthy();
      });
      it(`company address baseline: ${mode} removing a row cancels its scheduled request`, async () => {
        const p = propsFor(mode); render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Адреса' }));
        fireEvent.change(screen.getAllByLabelText('Полный адрес')[0], { target: { value: 'Запрос удалённого адреса' } });
        fireEvent.click(screen.getAllByRole('button', { name: 'Удалить адрес' })[0]);
        await act(async () => { await new Promise(done => setTimeout(done, 400)); });
        expect(apiService.suggestClientAddresses).not.toHaveBeenCalled(); expect(screen.getByDisplayValue('Второй адрес')).toBeTruthy();
      });
      it(`company address parent: ${mode} repeated clicks share one request and show its result`, async () => {
        let resolve!: (value: { result: string; cityWithType: string }) => void;
        vi.mocked(apiService.cleanAddress).mockReturnValueOnce(new Promise(done => { resolve = done; }));
        render(<LegalEntityModal {...propsFor(mode)} />); fireEvent.click(screen.getByRole('button', { name: 'Адреса' }));
        const find = screen.getAllByRole('button', { name: 'Найти адрес' })[0]; fireEvent.click(find); fireEvent.click(find);
        expect(apiService.cleanAddress).toHaveBeenCalledOnce(); await act(async () => resolve({ result: 'Первый адрес', cityWithType: 'Текущий город' }));
        expect(screen.getAllByLabelText('Город')[0]).toHaveProperty('value', 'Текущий город'); expect(screen.getByDisplayValue('Второй адрес')).toBeTruthy();
      });
      it(`company address parent: ${mode} an owned failure keeps input and permits an explicit retry`, async () => {
        vi.mocked(apiService.cleanAddress).mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce({ result: 'Первый адрес', cityWithType: 'Текущий город' });
        render(<LegalEntityModal {...propsFor(mode)} />); fireEvent.click(screen.getByRole('button', { name: 'Адреса' }));
        fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]);
        expect((await screen.findByRole('alert')).textContent).toContain('Введённые данные сохранены'); expect(screen.getByDisplayValue('Первый адрес')).toBeTruthy();
        fireEvent.click(screen.getAllByRole('button', { name: 'Найти адрес' })[0]); await waitFor(() => expect(screen.getAllByLabelText('Город')[0]).toHaveProperty('value', 'Текущий город'));
        expect(screen.queryByRole('alert')).toBeNull(); expect(apiService.cleanAddress).toHaveBeenCalledTimes(2);
      });
      it(`company address parent: ${mode} chosen suggestion remains visible when normalization fails`, async () => {
        vi.mocked(apiService.suggestClientAddresses).mockResolvedValue([{ value: 'Выбранный адрес' }]); vi.mocked(apiService.cleanAddress).mockRejectedValueOnce(new Error('offline'));
        render(<LegalEntityModal {...propsFor(mode)} />); fireEvent.click(screen.getByRole('button', { name: 'Адреса' }));
        fireEvent.change(screen.getAllByLabelText('Полный адрес')[0], { target: { value: 'Запрос адреса' } }); fireEvent.click(await screen.findByText('Выбранный адрес'));
        expect((await screen.findByRole('alert')).textContent).toContain('Введённые данные сохранены'); expect(screen.getByDisplayValue('Выбранный адрес')).toBeTruthy();
      });
      it(`company address parent: ${mode} save sends only persisted address fields`, async () => {
        const p = propsFor(mode); p.onSubmit.mockResolvedValue(p.initial); render(<LegalEntityModal {...p} />);
        fireEvent.click(screen.getByRole('button', { name: 'Сохранить' })); await waitFor(() => expect(p.onSubmit).toHaveBeenCalledOnce());
        const payload = p.onSubmit.mock.calls[0][0] as LegalEntityInput; expect(payload.addresses).toHaveLength(2);
        expect(payload.addresses?.map(row => row.value)).toEqual(['Первый адрес', 'Второй адрес']);
        expect(JSON.stringify(payload)).not.toContain('uiKey');
      });
    }
  });

  describe('company autofill ownership', () => {
    const first = { shortName: 'Первый кандидат', inn: '7707083893', address: 'Первый адрес' };
    const propsFor = (mode: 'page' | 'modal') => ({ isOpen: true, mode, initial: createInitialLegalEntity({ shortName: 'Текущий профиль', inn: '7707083893', isOwnCompany: false }),
      allowEdit: true, allowDelete: false, canManageOwnCompanySettings: false, canViewCases: false, canViewAccounts: false, canViewActs: false, canViewPayments: false,
      onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn().mockResolvedValue([first]), findById: vi.fn() });
    for (const mode of ['page', 'modal'] as const) {
      it(`company autofill: old search cannot populate a different ${mode} profile`, async () => {
        let resolve!: (value: LegalEntitySuggestion[]) => void; const p = propsFor(mode); p.suggest.mockReturnValue(new Promise(done => { resolve = done; }));
        const view = render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
        view.rerender(<LegalEntityModal {...p} initial={{ ...p.initial, id: 12, shortName: 'Новый профиль' }} />);
        await act(async () => resolve([first])); expect(screen.queryByText('Первый кандидат')).toBeNull();
      });
      it.each(['company', 'readonly', 'manual'] as const)(`company autofill: late details preserve ${mode} %s context`, async change => {
        let resolve!: (value: LegalEntityDadataDetails) => void; const p = propsFor(mode); p.findById.mockReturnValue(new Promise(done => { resolve = done; }));
        const view = render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
        fireEvent.click(await screen.findByText('Первый кандидат')); expect(p.findById).toHaveBeenCalledOnce();
        if (change === 'company') view.rerender(<LegalEntityModal {...p} initial={{ ...p.initial, id: 12, shortName: 'Новый профиль' }} />);
        if (change === 'readonly') view.rerender(<LegalEntityModal {...p} allowEdit={false} />);
        if (change === 'manual') fireEvent.change(screen.getByDisplayValue('Первый кандидат'), { target: { value: 'Ручное название' } });
        await act(async () => resolve({ shortName: 'Устаревшие реквизиты', address: 'Устаревший адрес' }));
        expect(screen.queryByDisplayValue('Устаревшие реквизиты')).toBeNull(); expect(screen.queryByDisplayValue('Устаревший адрес')).toBeNull();
        if (change === 'manual') expect(screen.getByDisplayValue('Ручное название')).toBeTruthy();
      });
      it.each(['close', 'readonly', 'session', 'refresh'] as const)(`company autofill boundary: ${mode} %s rejects old search success and error`, async change => {
        for (const outcome of ['success', 'error'] as const) {
          const iterationSession = documentCopyTestSession();
          let resolve!: (value: LegalEntitySuggestion[]) => void; let reject!: (reason: Error) => void;
          const p = propsFor(mode); p.suggest.mockReturnValue(new Promise((a, b) => { resolve = a; reject = b; }));
          const view = render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
          if (change === 'close') { view.rerender(<LegalEntityModal {...p} isOpen={false} />); view.rerender(<LegalEntityModal {...p} />); }
          if (change === 'readonly') view.rerender(<LegalEntityModal {...p} allowEdit={false} />);
          if (change === 'session') iterationSession.replace();
          if (change === 'refresh') view.rerender(<LegalEntityModal {...p} initial={{ ...p.initial }} />);
          await act(async () => { if (outcome === 'success') resolve([first]); else reject(new Error('Устаревшая ошибка DaData')); });
          expect(screen.queryByText('Первый кандидат')).toBeNull(); expect(screen.queryByText('Устаревшая ошибка DaData')).toBeNull();
          if (change !== 'readonly') expect(screen.getByRole('button', { name: 'Найти' })).toHaveProperty('disabled', false);
          view.unmount();
          iterationSession.restore();
        }
      });
      it.each(['success', 'error'] as const)(`company autofill boundary: ${mode} old %s cannot finish a newer query`, async outcome => {
        let resolveOld!: (value: LegalEntitySuggestion[]) => void; let rejectOld!: (reason: Error) => void; let resolveNew!: (value: LegalEntitySuggestion[]) => void;
        const p = propsFor(mode); p.suggest.mockReturnValueOnce(new Promise((a, b) => { resolveOld = a; rejectOld = b; })).mockReturnValueOnce(new Promise(done => { resolveNew = done; }));
        render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
        fireEvent.change(screen.getByPlaceholderText('Например: 7707083893 или ООО Пример'), { target: { value: 'Новый поиск' } });
        fireEvent.click(screen.getByRole('button', { name: 'Найти' })); expect(p.suggest).toHaveBeenCalledTimes(2);
        await act(async () => { if (outcome === 'success') resolveOld([first]); else rejectOld(new Error('Устаревшая ошибка DaData')); });
        expect(screen.getByRole('button', { name: 'Найти' })).toHaveProperty('disabled', true);
        expect(screen.queryByText('Первый кандидат')).toBeNull(); expect(screen.queryByText('Устаревшая ошибка DaData')).toBeNull();
        await act(async () => resolveNew([{ shortName: 'Новый кандидат', inn: '633009620345' }]));
        expect(screen.getByText('Новый кандидат')).toBeTruthy(); expect(screen.getByRole('button', { name: 'Найти' })).toHaveProperty('disabled', false);
      });
      it.each(['success', 'error'] as const)(`company autofill boundary: ${mode} latest selection wins over old %s`, async outcome => {
        let resolveOld!: (value: LegalEntityDadataDetails) => void; let rejectOld!: (reason: Error) => void;
        const p = propsFor(mode); p.suggest.mockResolvedValue([first, { shortName: 'Второй кандидат', inn: '633009620345' }]);
        p.findById.mockReturnValueOnce(new Promise((a, b) => { resolveOld = a; rejectOld = b; })).mockResolvedValueOnce({ shortName: 'Последний выбор', inn: '633009620345', address: 'Последний адрес' });
        render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' })); fireEvent.click(await screen.findByText('Первый кандидат'));
        fireEvent.click(screen.getByText('Второй кандидат')); await screen.findByDisplayValue('Последний выбор');
        await act(async () => { if (outcome === 'success') resolveOld({ shortName: 'Устаревшие реквизиты', address: 'Устаревший адрес' }); else rejectOld(new Error('Old details')); });
        expect(screen.getByDisplayValue('Последний выбор')).toBeTruthy(); expect(screen.queryByDisplayValue('Устаревшие реквизиты')).toBeNull();
        fireEvent.click(screen.getByRole('button', { name: 'Адреса' })); expect(screen.queryByDisplayValue('Первый адрес')).toBeNull();
      });
      it(`company autofill boundary: ${mode} session replacement before render blocks cached selection`, async () => {
        const p = propsFor(mode); render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' }));
        const candidate = await screen.findByText('Первый кандидат'); baseAuthSession.replace(); fireEvent.click(candidate);
        expect(p.findById).not.toHaveBeenCalled(); expect(screen.getByDisplayValue('Текущий профиль')).toBeTruthy();
      });
      it(`company autofill boundary: ${mode} manual contacts cancel pending details`, async () => {
        let resolve!: (value: LegalEntityDadataDetails) => void; const p = propsFor(mode); p.findById.mockReturnValue(new Promise(done => { resolve = done; }));
        render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' })); fireEvent.click(await screen.findByText('Первый кандидат'));
        fireEvent.click(screen.getByRole('button', { name: 'Контакты' })); fireEvent.click(screen.getByRole('button', { name: '+ Добавить email' }));
        await act(async () => resolve({ shortName: 'Устаревшие реквизиты', emails: ['stale@example.local'] }));
        expect(screen.queryByDisplayValue('Устаревшие реквизиты')).toBeNull(); expect(screen.queryByDisplayValue('stale@example.local')).toBeNull();
      });
      it.each(['close', 'session'] as const)(`company autofill extra: ${mode} pending details cannot cross %s`, async change => {
        let resolve!: (value: LegalEntityDadataDetails) => void; const p = propsFor(mode); p.findById.mockReturnValue(new Promise(done => { resolve = done; }));
        const view = render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' })); fireEvent.click(await screen.findByText('Первый кандидат'));
        if (change === 'close') { view.rerender(<LegalEntityModal {...p} isOpen={false} />); view.rerender(<LegalEntityModal {...p} />); } else baseAuthSession.replace();
        await act(async () => resolve({ shortName: 'Устаревшие реквизиты', address: 'Устаревший адрес' }));
        expect(screen.queryByDisplayValue('Устаревшие реквизиты')).toBeNull(); expect(screen.queryByDisplayValue('Устаревший адрес')).toBeNull();
      });
      it(`company autofill extra: ${mode} same-session token refresh preserves pending details`, async () => {
        let resolve!: (value: LegalEntityDadataDetails) => void; const p = propsFor(mode); p.findById.mockReturnValue(new Promise(done => { resolve = done; }));
        const view = render(<LegalEntityModal {...p} />); fireEvent.click(screen.getByRole('button', { name: 'Найти' })); fireEvent.click(await screen.findByText('Первый кандидат'));
        baseAuthSession.refresh(); view.rerender(<LegalEntityModal {...p} />);
        await act(async () => resolve({ shortName: 'Актуальные реквизиты', inn: '7707083893' })); expect(screen.getByDisplayValue('Актуальные реквизиты')).toBeTruthy();
      });
    }
  });

  it('prefills DaData query from saved inn and keeps the full address after suggestion selection', async () => {
    const suggestMock = vi
      .fn<(payload: { query?: string; inn?: string; limit?: number }) => Promise<LegalEntitySuggestion[]>>()
      .mockResolvedValue([
        {
          shortName: 'ИП Кожеваткин Андрей Владимирович',
          fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
          inn: '633009620345',
          ogrn: '317631300153603',
          address: 'Самарская обл, г Новокуйбышевск',
        },
      ]);

    const details: LegalEntityDadataDetails = {
      shortName: 'ИП Кожеваткин Андрей Владимирович',
      fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
      inn: '633009620345',
      ogrn: '317631300153603',
      address: 'Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
      addressUnrestricted:
        '633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
      country: 'Россия',
      postalCode: '633208',
      regionWithType: 'Новосибирская обл',
      cityWithType: 'г Искитим',
      streetWithType: 'Южный мкр',
      house: '37',
      flat: '19',
    };

    const findByIdMock = vi
      .fn<(payload: { query: string; kpp?: string }) => Promise<LegalEntityDadataDetails>>()
      .mockResolvedValue(details);

    const initial = createInitialLegalEntity({ inn: '633009620345' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={suggestMock}
        findById={findByIdMock}
        allowEdit
        allowDelete={false}
      />,
    );

    const user = userEvent.setup();
    const dadataInput = screen.getByPlaceholderText(
      'Например: 7707083893 или ООО Пример',
    ) as HTMLInputElement;
    expect(dadataInput.value).toBe('633009620345');

    await user.click(screen.getByRole('button', { name: 'Найти' }));

    await waitFor(() =>
      expect(suggestMock).toHaveBeenCalledWith({ query: '633009620345', limit: 6 }),
    );

    await user.click(await screen.findByText('ИП Кожеваткин Андрей Владимирович'));

    await waitFor(() =>
      expect(findByIdMock).toHaveBeenCalledWith({ query: '633009620345', kpp: undefined }),
    );

    await user.click(screen.getByRole('button', { name: 'Адреса' }));

    expect(
      await screen.findByDisplayValue(
        '633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
      ),
    ).toBeTruthy();
    expect(screen.getByDisplayValue('633208')).toBeTruthy();
    expect(screen.getByDisplayValue('Новосибирская обл')).toBeTruthy();
    expect(screen.getByDisplayValue('г Искитим')).toBeTruthy();
    expect(screen.getByDisplayValue('Южный мкр')).toBeTruthy();
    expect(screen.getByDisplayValue('37')).toBeTruthy();
    expect(screen.getByDisplayValue('19')).toBeTruthy();
  }, 15000);

  it('deduplicates repeated DaData suggestions in the modal UI', async () => {
    const suggestMock = vi
      .fn<(payload: { query?: string; inn?: string; limit?: number }) => Promise<LegalEntitySuggestion[]>>()
      .mockResolvedValue([
        {
          shortName: 'ИП Кожеваткин Андрей Владимирович',
          fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
          inn: '633009620345',
          ogrn: '317631300153603',
          address: 'Самарская обл, г Новокуйбышевск',
        },
        {
          shortName: 'ИП Кожеваткин Андрей Владимирович',
          fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
          inn: '633009620345',
          ogrn: '307631300200020',
          address: 'Самарская обл, г Новокуйбышевск',
        },
        {
          shortName: 'ООО Пример',
          fullName: 'Общество с ограниченной ответственностью Пример',
          inn: '7707083893',
          kpp: '770701001',
          ogrn: '1027700132195',
          address: 'г Москва, ул Ильинка, д 9',
        },
      ]);

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(createInitialLegalEntity())}
        initial={createInitialLegalEntity()}
        suggest={suggestMock}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
      />,
    );

    const user = userEvent.setup();
    await user.type(
      screen.getByPlaceholderText('Например: 7707083893 или ООО Пример'),
      'Кожеваткин',
    );
    await user.click(screen.getByRole('button', { name: 'Найти' }));

    await waitFor(() =>
      expect(suggestMock).toHaveBeenCalledWith({ query: 'Кожеваткин', limit: 6 }),
    );

    expect(await screen.findAllByText('ИП Кожеваткин Андрей Владимирович')).toHaveLength(1);
    expect(screen.getByText('ООО Пример')).toBeTruthy();
  });

  it.each([false, true])('allows delegated profile requisites editing without administrator or financial actions when checkFailed=%s', async checkFailed => {
    if (checkFailed) vi.mocked(apiService.getCompanyRequisitesCheck).mockRejectedValueOnce(new Error('Проверка временно недоступна'));
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Assigned own company' });
    const onSubmit = vi.fn().mockResolvedValue(initial);
    render(<LegalEntityModal isOpen initial={initial} onClose={vi.fn()} onSubmit={onSubmit}
      suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false}
      mode="page" canManageOwnCompanySettings={false} canViewCases={false} canViewAccounts={false}
      canCreateAccounts={false} canViewActs={false} canViewPayments={false} />);
    await screen.findByText('Печать компании');
    expect(vi.mocked(apiService.getCompanyBranches)).not.toHaveBeenCalled();
    if (checkFailed) expect(await screen.findByText('Проверка временно недоступна')).toBeTruthy();
    else expect(await screen.findByText('По проверяемым реквизитам замечаний нет.')).toBeTruthy();
    expect(vi.mocked(apiService.getCompanyRequisitesCheck)).toHaveBeenCalledExactlyOnceWith(9, expect.any(AbortSignal));
    expect(vi.mocked(apiService.getBankConnections)).not.toHaveBeenCalled();
    expect(screen.queryByLabelText('Состояние профиля компании')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Добавить клиента' })).toBeNull();
    const name = screen.getByDisplayValue('Assigned own company');
    expect(name).toHaveProperty('disabled', false);
    await user.clear(name); await user.type(name, 'Updated own company');
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ shortName: 'Updated own company' })));
  });

  it('renders company profile page as read-only when profile mode hides editing controls', async () => {
    const initial = createInitialLegalEntity({
      shortName: 'ИП Кожеваткин Андрей Владимирович',
      fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
      inn: '633009620345',
      ogrn: '317631300153603',
      director: 'Кожеваткин Андрей Владимирович',
    });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit={false}
        allowDelete={false}
        mode="page"
        onBack={vi.fn()}
        titleOverride="Профиль компании"
        hidePageNavigation
        hideFormActions
        hideDadataSection
        hideMetaInfo
        hideAuditSection
        hideDictionaryButtons
        compactReadOnlyView
      />,
    );

    expect(screen.getByText('Профиль компании')).toBeTruthy();
    expect(await screen.findByText('По проверяемым реквизитам замечаний нет.')).toBeTruthy();
    expect(vi.mocked(apiService.getCompanyRequisitesCheck)).toHaveBeenCalledExactlyOnceWith(initial.id, expect.any(AbortSignal));
    expect(screen.queryByRole('button', { name: 'Назад' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
    expect(screen.queryByText('Заполнить по данным DaData')).toBeNull();
    expect(screen.queryByText('Создано')).toBeNull();
    expect(screen.queryByText('Справочник банков')).toBeNull();
    expect(screen.queryByText('Действия')).toBeNull();
    expect(screen.queryByText('+ Добавить телефон')).toBeNull();
    expect(screen.queryByText('+ Добавить email')).toBeNull();
    expect(screen.queryByText('Справочник')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Найти адрес' })).toBeNull();
    expect(screen.getByDisplayValue('ИП Кожеваткин Андрей Владимирович')).toHaveProperty('disabled', true);
    expect(screen.getByDisplayValue('633009620345')).toHaveProperty('disabled', true);
  });

  it('shows logo, seal and signature fields for own legal entities only', async () => {
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Наша компания' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
      />,
    );

    expect(await screen.findByText('Логотип компании')).toBeTruthy();
    expect(screen.getByText('Печать компании')).toBeTruthy();
    expect(screen.getByText('Подпись руководителя')).toBeTruthy();
  });

  it.each([
    ['page', 'Печать компании'], ['page', 'Подпись руководителя'],
    ['modal', 'Печать компании'], ['modal', 'Подпись руководителя'],
  ] as const)('keeps %s form values and clears the %s file input after an oversized PNG rejection', async (mode, label) => {
    const user = userEvent.setup();
    const logo = 'data:image/png;base64,existing-logo';
    const initial = createInitialLegalEntity({ shortName: 'Неизменённая компания', logoDataUrl: logo });
    const submit = vi.fn();
    const context = { clearRect: vi.fn(), drawImage: vi.fn(), getImageData: vi.fn(transparentImageData), save: vi.fn(), restore: vi.fn(), fillRect: vi.fn() };
    const getContext = vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D);
    const encode = vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(mime => mime === 'image/png'
      ? 'P'.repeat(500_000) : 'data:image/jpeg;base64,incorrect-white-background');
    class DecodedImage {
      width = 400;
      height = 400;
      onload: (() => void) | null = null;
      set src(_value: string) { queueMicrotask(() => this.onload?.()); }
    }
    vi.stubGlobal('Image', DecodedImage);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const title = await screen.findByText(label);
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      expect(input).not.toBeNull();
      const companyName = screen.getByDisplayValue('Неизменённая компания');
      await user.clear(companyName);
      await user.type(companyName, 'Несохранённое название');
      const file = imageFixtureFile('same-seal.png');
      await user.upload(input, file);
      await screen.findByText(`${label}: PNG слишком большой для сохранения. Выберите PNG меньшего размера с прозрачным фоном.`);
      await waitFor(() => expect(input.value).toBe(''));
      expect((companyName as HTMLInputElement).value).toBe('Несохранённое название');
      expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(logo);
      expect(context.fillRect).not.toHaveBeenCalled();
      expect(encode).toHaveBeenCalledExactlyOnceWith('image/png');
      await user.upload(input, file);
      await waitFor(() => expect(encode).toHaveBeenCalledTimes(2));
      await waitFor(() => expect(input.value).toBe(''));
      expect(submit).not.toHaveBeenCalled();
    } finally {
      vi.unstubAllGlobals();
      getContext.mockRestore();
      encode.mockRestore();
    }
  });

  it.each([
    ['page', 'Логотип компании', 'logoDataUrl'], ['modal', 'Логотип компании', 'logoDataUrl'],
    ['page', 'Печать компании', 'sealDataUrl'], ['modal', 'Печать компании', 'sealDataUrl'],
    ['page', 'Подпись руководителя', 'directorSignatureDataUrl'], ['modal', 'Подпись руководителя', 'directorSignatureDataUrl'],
  ] as const)('keeps the latest %s %s upload and prevents saving while it is processing', async (mode, label, field) => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const initial = createInitialLegalEntity({ shortName: 'Компания' });
    const submit = vi.fn().mockResolvedValue(initial);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const title = await screen.findByText(label);
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, imageFixtureFile('first.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const oldSource = deferred.images[0].source;
      const oldLoad = deferred.images[0].onload;
      const oldError = deferred.images[0].onerror;
      await user.upload(input, imageFixtureFile('latest.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(2));
      expect(deferred.images[1].source).not.toBe(oldSource);
      const save = screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement;
      expect(save.disabled).toBe(true);
      fireEvent.submit(save.closest('form')!);
      expect(submit).not.toHaveBeenCalled();
      expect(screen.getByText('Дождитесь окончания обработки изображений перед сохранением.')).toBeTruthy();
      const latest = deferred.images[1].source;
      await act(async () => { deferred.images[1].onload?.(); });
      await waitFor(() => expect(save.disabled).toBe(false));
      expect(screen.queryByText('Дождитесь окончания обработки изображений перед сохранением.')).toBeNull();
      await act(async () => { oldLoad?.(); oldError?.(); });
      expect(screen.getByAltText(label).getAttribute('src')).toBe(latest);
      expect(screen.queryByText('Не удалось обработать изображение.')).toBeNull();
      await user.click(save);
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ [field]: latest, shortName: 'Компания' });
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('cancels a pending replacement when restoring the saved seal in %s mode', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const initial = createInitialLegalEntity({ shortName: 'Компания', sealDataUrl: seal });
    confirmMock.mockResolvedValue(true);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={vi.fn()} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      await screen.findByAltText('Печать компании');
      await user.click(screen.getByRole('button', { name: 'Удалить изображение' }));
      const input = screen.getByText('Печать компании').parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, imageFixtureFile('replacement.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const oldLoad = deferred.images[0].onload;
      await user.click(screen.getByRole('button', { name: 'Вернуть текущее изображение' }));
      await act(async () => { oldLoad?.(); });
      expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
      expect((screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement).disabled).toBe(false);
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('waits for independent image fields before enabling save in %s mode', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={vi.fn()}
        initial={createInitialLegalEntity({ shortName: 'Компания' })} suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()} allowEdit allowDelete={false} />);
      for (const [index, label] of ['Логотип компании', 'Подпись руководителя'].entries()) {
        const title = await screen.findByText(label);
        const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
        await user.upload(input, imageFixtureFile(`${index}.png`));
        await waitFor(() => expect(deferred.images).toHaveLength(index + 1));
      }
      const save = screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement;
      await act(async () => { deferred.images[0].onload?.(); });
      expect(save.disabled).toBe(true);
      await act(async () => { deferred.images[1].onload?.(); });
      await waitFor(() => expect(save.disabled).toBe(false));
    } finally { deferred.restore(); }
  });

  it.each([
    ['page', 'close'], ['modal', 'close'], ['page', 'refresh'], ['modal', 'refresh'],
    ['page', 'permission'], ['modal', 'permission'],
  ] as const)('discards pending image work after %s form %s', async (mode, change) => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const initial = createInitialLegalEntity({ shortName: 'Первая компания' });
    const props = { isOpen: true, mode, initial, onClose: vi.fn(), onSubmit: vi.fn(),
      suggest: vi.fn().mockResolvedValue([]), findById: vi.fn(), allowEdit: true, allowDelete: false };
    try {
      const view = render(<LegalEntityModal {...props} />);
      const title = await screen.findByText('Логотип компании');
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, imageFixtureFile('old.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const oldLoad = deferred.images[0].onload;
      const next = change === 'refresh' ? { ...initial, shortName: 'Обновлённая компания' } : initial;
      view.rerender(<LegalEntityModal {...props} initial={next} isOpen={change !== 'close'} allowEdit={change !== 'permission'} />);
      await act(async () => { oldLoad?.(); });
      view.rerender(<LegalEntityModal {...props} initial={next} />);
      await screen.findByText('Логотип компании');
      expect(screen.queryByAltText('Логотип компании')).toBeNull();
      expect(screen.queryByText('Обрабатываем изображения. Дождитесь окончания перед сохранением.')).toBeNull();
      expect(props.onSubmit).not.toHaveBeenCalled();
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('cancels file reading and recovers from the current read failure in %s mode', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const readers: DeferredReader[] = [];
    class DeferredReader {
      result: string | null = null;
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      abort = vi.fn();
      readAsDataURL(file: File) {
        this.result = `data:image/png;base64,${btoa(file.name)}`;
        readers.push(this);
      }
    }
    vi.stubGlobal('FileReader', DeferredReader);
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const submit = vi.fn().mockResolvedValue(createInitialLegalEntity());
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit}
        initial={createInitialLegalEntity({ shortName: 'Компания', sealDataUrl: seal })}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const title = await screen.findByText('Логотип компании');
      const input = title.parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      const file = imageFixtureFile('latest.png');
      await user.upload(input, imageFixtureFile('old.png'));
      await waitFor(() => expect(readers).toHaveLength(1));
      const oldLoad = readers[0].onload;
      const oldError = readers[0].onerror;
      await user.upload(input, file);
      await waitFor(() => expect(readers).toHaveLength(2));
      expect(readers[0].abort).toHaveBeenCalledTimes(1);
      await act(async () => { oldLoad?.(); oldError?.(); });
      expect(deferred.images).toHaveLength(0);
      const save = screen.getByRole('button', { name: /^Сохранить$/ }) as HTMLButtonElement;
      expect(save.disabled).toBe(true);
      await act(async () => { readers[1].onerror?.(); });
      await screen.findByText('Не удалось прочитать файл изображения.');
      expect(save.disabled).toBe(false);
      expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
      await waitFor(() => expect(input.value).toBe(''));
      await user.upload(input, file);
      await waitFor(() => expect(readers).toHaveLength(3));
      const latest = readers[2].result;
      await act(async () => { readers[2].onload?.(); });
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      await act(async () => { deferred.images[0].onload?.(); });
      await waitFor(() => expect(save.disabled).toBe(false));
      expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(latest);
      expect(screen.queryByText('Не удалось прочитать файл изображения.')).toBeNull();
      await user.click(save);
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ logoDataUrl: latest });
    } finally { deferred.restore(); }
  });

  it.each([
    ['page', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'remove'],
    ['page', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'restore'],
    ['modal', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'remove'],
    ['modal', 'Логотип компании', 'logoDataUrl', 'logoChanged', 'restore'],
    ['page', 'Печать компании', 'sealDataUrl', 'sealChanged', 'remove'],
    ['page', 'Печать компании', 'sealDataUrl', 'sealChanged', 'restore'],
    ['modal', 'Печать компании', 'sealDataUrl', 'sealChanged', 'remove'],
    ['modal', 'Печать компании', 'sealDataUrl', 'sealChanged', 'restore'],
    ['page', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'remove'],
    ['page', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'restore'],
    ['modal', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'remove'],
    ['modal', 'Подпись руководителя', 'directorSignatureDataUrl', 'directorSignatureChanged', 'restore'],
  ] as const)('keeps deletion feedback inline in %s for %s, preserving %s/%s on %s', async (mode, label, field, flag, action) => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Компания' });
    initial[field] = 'data:image/png;base64,c2F2ZWQ=';
    const submit = vi.fn<(payload: LegalEntityInput) => Promise<LegalEntityDetail>>().mockResolvedValue(initial);
    confirmMock.mockResolvedValue(true);
    render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
      suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
    const title = await screen.findByText(label);
    const imageField = title.parentElement!;
    await user.click(screen.getByRole('button', { name: 'Удалить изображение' }));
    await waitFor(() => expect(imageField.querySelector('[role="status"]')?.textContent).toContain('Изображение помечено на удаление.'));
    expect(toastInfoMock).not.toHaveBeenCalled();
    if (action === 'restore') {
      await user.click(screen.getByRole('button', { name: 'Вернуть текущее изображение' }));
      expect(imageField.querySelector('[role="status"]')).toBeNull();
    }
    await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    const payload = submit.mock.calls[0][0];
    expect(payload[flag]).toBe(action === 'remove');
    if (action === 'remove') expect(payload[field]).toBeNull();
    else expect(Object.hasOwn(payload, field)).toBe(false);
  });

  it.each(['page', 'modal'] as const)('rejects source metadata in the %s form before reading, preserving the draft and other image', async mode => {
    const user = userEvent.setup({ applyAccept: false });
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const initial = createInitialLegalEntity({ shortName: 'Компания', sealDataUrl: seal });
    const submit = vi.fn();
    const read = vi.spyOn(FileReader.prototype, 'readAsDataURL');
    render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
      suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
    const logoField = (await screen.findByText('Логотип компании')).parentElement!;
    const input = logoField.querySelector('input[type="file"]') as HTMLInputElement;
    const name = screen.getByDisplayValue('Компания');
    await user.clear(name);
    await user.type(name, 'Мой черновик');
    try {
      for (const [file, message] of [
        [new File([], 'empty.png', { type: 'image/png' }), 'Логотип компании: выбран пустой файл. Выберите изображение.'],
        [new File(['<svg/>'], 'renamed.png', { type: 'image/svg+xml' }), 'Логотип компании: PNG или JPG, до 2 МБ.'],
        [new File([new Uint8Array(2 * 1024 * 1024 + 1)], 'big.png', { type: 'image/png' }), 'Логотип компании: исходный файл больше 2 МБ. Выберите файл меньшего размера.'],
      ] as const) {
        await user.upload(input, file);
        await screen.findByText(message);
        await waitFor(() => expect(input.value).toBe(''));
        expect((name as HTMLInputElement).value).toBe('Мой черновик');
        expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
        expect(screen.getByRole<HTMLButtonElement>('button', { name: /^Сохранить$/ }).disabled).toBe(false);
      }
      const signatureField = screen.getByText('Подпись руководителя').parentElement!;
      const signature = signatureField.querySelector('input[type="file"]') as HTMLInputElement;
      expect(signature.accept).toBe('image/png');
      await user.upload(signature, new File(['jpeg'], 'renamed.png', { type: 'image/jpeg' }));
      await screen.findByText('Подпись руководителя: PNG с прозрачным фоном.');
      expect(read).not.toHaveBeenCalled();
      expect(submit).not.toHaveBeenCalled();
      await waitFor(() => expect(signature.value).toBe(''));
    } finally { read.mockRestore(); }
  });

  it.each([
    ['page', 'Печать компании', 'sealDataUrl'], ['modal', 'Печать компании', 'sealDataUrl'],
    ['page', 'Подпись руководителя', 'directorSignatureDataUrl'], ['modal', 'Подпись руководителя', 'directorSignatureDataUrl'],
  ] as const)('rejects an opaque %s %s before encoding, then accepts a transparent retry', async (mode, label, key) => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const initial = createInitialLegalEntity({ shortName: 'Несохранённые реквизиты', logoDataUrl: 'data:image/png;base64,old-logo' });
    const submit = vi.fn<(payload: LegalEntityInput) => Promise<LegalEntityDetail>>().mockResolvedValue(initial);
    deferred.context.getImageData.mockImplementationOnce((_x, _y, width, height) => ({
      data: new Uint8ClampedArray(width * height * 4).fill(255),
    } as ImageData));
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const input = (await screen.findByText(label)).parentElement!.querySelector('input[type="file"]') as HTMLInputElement;
      const file = imageFixtureFile('same-png.png');
      await user.upload(input, file);
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      await act(async () => { deferred.images[0].onload?.(); });
      await screen.findByText(`${label}: нужен PNG с прозрачными областями. Выберите файл без непрозрачной подложки.`);
      await waitFor(() => expect(input.value).toBe(''));
      expect(deferred.encode).not.toHaveBeenCalled();
      expect(submit).not.toHaveBeenCalled();
      expect(screen.queryByAltText(label)).toBeNull();
      expect(screen.getByDisplayValue('Несохранённые реквизиты')).toBeTruthy();
      expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(initial.logoDataUrl);
      await user.upload(input, file);
      await waitFor(() => expect(deferred.images).toHaveLength(2));
      await act(async () => { deferred.images[1].onload?.(); });
      await screen.findByAltText(label);
      await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ [key]: expect.stringContaining('data:image/png;base64,'), shortName: initial.shortName, logoChanged: false });
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('rejects actual source headers in the %s form before full read, preserving the draft', async mode => {
    const user = userEvent.setup({ applyAccept: false });
    const seal = 'data:image/png;base64,c2F2ZWQ=';
    const initial = createInitialLegalEntity({ shortName: 'Мой черновик', sealDataUrl: seal });
    const submit = vi.fn();
    const read = vi.spyOn(FileReader.prototype, 'readAsDataURL');
    const image = vi.fn(); vi.stubGlobal('Image', image);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const field = (await screen.findByText('Логотип компании')).parentElement!;
      const input = field.querySelector('input[type="file"]') as HTMLInputElement;
      const large = imageFixtureBytes();
      new DataView(large.buffer).setUint32(16, 65535);
      new DataView(large.buffer).setUint32(20, 65535);
      for (const [file, text] of [
        [new File([large.buffer], 'large.png', { type: 'image/png' }), 'Логотип компании: изображение превышает 4096 пикселей по стороне или 4 мегапикселя. Уменьшите его.'],
        [new File([imageFixtureBytes('jpeg').buffer], 'renamed.png', { type: 'image/png' }), 'Логотип компании: содержимое файла не соответствует указанному формату.'],
      ] as const) {
        await user.upload(input, file);
        await screen.findByText(text);
        await waitFor(() => expect(input.value).toBe(''));
        expect(screen.getByDisplayValue('Мой черновик')).toBeTruthy();
        expect(screen.getByAltText('Печать компании').getAttribute('src')).toBe(seal);
        expect(screen.getByRole<HTMLButtonElement>('button', { name: /^Сохранить$/ }).disabled).toBe(false);
      }
      expect(read).not.toHaveBeenCalled(); expect(image).not.toHaveBeenCalled(); expect(submit).not.toHaveBeenCalled();
    } finally { vi.unstubAllGlobals(); read.mockRestore(); }
  });

  it.each(['page', 'modal'] as const)('discards a late header result in the %s form after a newer file succeeds', async mode => {
    const user = userEvent.setup();
    const deferred = deferCompanyImageProcessing();
    const initial = createInitialLegalEntity({ shortName: 'Мой черновик' });
    const submit = vi.fn<(payload: LegalEntityInput) => Promise<LegalEntityDetail>>().mockResolvedValue(initial);
    const old = imageFixtureFile('old-header.png');
    const oldBytes = await old.arrayBuffer();
    let release!: (value: ArrayBuffer) => void;
    vi.spyOn(old, 'slice').mockReturnValue({ arrayBuffer: () => new Promise<ArrayBuffer>(resolve => { release = resolve; }) } as Blob);
    try {
      render(<LegalEntityModal isOpen mode={mode} onClose={vi.fn()} onSubmit={submit} initial={initial}
        suggest={vi.fn().mockResolvedValue([])} findById={vi.fn()} allowEdit allowDelete={false} />);
      const field = (await screen.findByText('Логотип компании')).parentElement!;
      const input = field.querySelector('input[type="file"]') as HTMLInputElement;
      await user.upload(input, old);
      expect(screen.getByRole<HTMLButtonElement>('button', { name: /^Сохранить$/ }).disabled).toBe(true);
      expect(deferred.images).toHaveLength(0);
      await user.upload(input, imageFixtureFile('latest-header.png'));
      await waitFor(() => expect(deferred.images).toHaveLength(1));
      const latest = deferred.images[0].source;
      await act(async () => { deferred.images[0].onload?.(); });
      await waitFor(() => expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(latest));
      await act(async () => { release(oldBytes); });
      expect(deferred.images).toHaveLength(1);
      expect(screen.getByAltText('Логотип компании').getAttribute('src')).toBe(latest);
      await user.click(screen.getByRole('button', { name: /^Сохранить$/ }));
      await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
      expect(submit.mock.calls[0][0]).toMatchObject({ logoDataUrl: latest, shortName: 'Мой черновик' });
    } finally { deferred.restore(); }
  });

  it.each(['page', 'modal'] as const)('print defaults: round trips independent choices and the new version in %s mode for a delegated editor', async mode => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Own company', documentImagePrintDefaultsVersion: 'A'.repeat(64) });
    const onSubmit = vi.fn().mockImplementation(async (payload: LegalEntityInput) => ({ ...initial, ...payload, documentImagePrintDefaultsVersion: 'B'.repeat(64) }));
    render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} canManageOwnCompanySettings={false} onClose={vi.fn()} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    const ownInvoice = await screen.findByRole('checkbox', { name: 'Свой набор для счетов' });
    await user.click(ownInvoice);
    await user.click(screen.getByRole('checkbox', { name: 'Логотип для счетов' }));
    await user.click(screen.getByRole('checkbox', { name: 'Печать для счетов' }));
    await user.click(screen.getByRole('checkbox', { name: 'Свой набор для договоров' }));
    expect(screen.getByRole('checkbox', { name: 'Свой набор для актов' })).toHaveProperty('checked', false);
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0][0]).toEqual(expect.objectContaining({
      documentImagePrintDefaultsChanged: true, documentImagePrintDefaultsVersion: 'A'.repeat(64),
      documentImagePrintDefaults: { invoice: { includeLogo: false, includeSeal: true, includeSignature: false }, act: null, contract: { includeLogo: true, includeSeal: false, includeSignature: false } },
    }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false));
    await user.click(screen.getByRole('checkbox', { name: 'Свой набор для актов' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    expect(onSubmit.mock.calls[1][0]).toEqual(expect.objectContaining({ documentImagePrintDefaultsVersion: 'B'.repeat(64) }));
  });

  it.each(['page', 'modal'] as const)('print defaults: preserves draft and original version on conflict in %s mode', async mode => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Own company', documentImagePrintDefaultsVersion: 'A'.repeat(64) });
    const onSubmit = vi.fn().mockRejectedValue(new Error('Настройки печати компании изменились. Обновите карточку.'));
    render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} onClose={vi.fn()} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    await user.click(await screen.findByRole('checkbox', { name: 'Свой набор для счетов' }));
    await user.click(screen.getByRole('checkbox', { name: 'Подпись для счетов' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
    expect(screen.getByRole('checkbox', { name: 'Подпись для счетов' })).toHaveProperty('checked', true);
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(2));
    expect(onSubmit.mock.calls[1][0]).toEqual(onSubmit.mock.calls[0][0]);
    expect(onSubmit.mock.calls[1][0].documentImagePrintDefaultsVersion).toBe('A'.repeat(64));
  });

  it.each(['page', 'modal'] as const)('print defaults: explicit reset repairs an unreadable setting in %s mode', async mode => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Preserved name', documentImagePrintDefaultsInvalid: true, documentImagePrintDefaultsVersion: 'A'.repeat(64) });
    const onSubmit = vi.fn().mockResolvedValue({ ...initial, documentImagePrintDefaultsInvalid: false, documentImagePrintDefaultsVersion: 'B'.repeat(64) });
    render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} onClose={vi.fn()} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    await user.click(await screen.findByRole('button', { name: 'Сбросить настройки изображений' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ shortName: 'Preserved name', documentImagePrintDefaultsChanged: true, documentImagePrintDefaults: null, documentImagePrintDefaultsVersion: 'A'.repeat(64) })));
    await waitFor(() => expect(screen.queryByText(/Сохранённые настройки изображений не удалось прочитать/)).toBeNull());
  });

  it.each(['page', 'modal'] as const)('print defaults: reverted choices and ordinary saves omit all settings fields in %s mode', async mode => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Own company', documentImagePrintDefaultsVersion: 'A'.repeat(64) });
    const onSubmit = vi.fn().mockResolvedValue(initial);
    render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} onClose={vi.fn()} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    const own = await screen.findByRole('checkbox', { name: 'Свой набор для счетов' });
    await user.click(own); await user.click(own);
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    for (const key of ['documentImagePrintDefaults', 'documentImagePrintDefaultsChanged', 'documentImagePrintDefaultsVersion']) expect(onSubmit.mock.calls[0][0]).not.toHaveProperty(key);
  });

  it.each(['page', 'modal'] as const)('print defaults: view-only and unavailable-version controls stay disabled in %s mode', async mode => {
    const initial = createInitialLegalEntity({ shortName: 'Own company' });
    const props = { isOpen: true, mode, initial, allowEdit: true, allowDelete: false, onClose: vi.fn(), onSubmit: vi.fn(), suggest: vi.fn(), findById: vi.fn() };
    const view = render(<LegalEntityModal {...props} />);
    expect(await screen.findByRole('checkbox', { name: 'Свой набор для счетов' })).toHaveProperty('disabled', true);
    view.rerender(<LegalEntityModal {...props} allowEdit={false} initial={{ ...initial, documentImagePrintDefaultsVersion: 'A'.repeat(64) }} />);
    expect(screen.getByRole('checkbox', { name: 'Свой набор для счетов' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Сбросить настройки изображений' })).toHaveProperty('disabled', true);
    expect(props.onSubmit).not.toHaveBeenCalled();
  });

  it.each((['page', 'modal'] as const).flatMap(mode => (['company', 'close', 'permission', 'refresh'] as const).flatMap(reason => (['success', 'error'] as const).map(outcome => [mode, reason, outcome] as const))))('print defaults: ignores late %s save after %s (%s)', async (mode, reason, outcome) => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Original company', documentImagePrintDefaultsVersion: 'A'.repeat(64) });
    let resolve!: (value: LegalEntityDetail) => void; let reject!: (error: Error) => void;
    const pending = new Promise<LegalEntityDetail>((ok, fail) => { resolve = ok; reject = fail; });
    const props = { isOpen: true, mode, initial, allowEdit: true, allowDelete: false, onClose: vi.fn(), onSubmit: vi.fn().mockReturnValue(pending), suggest: vi.fn(), findById: vi.fn() };
    const view = render(<LegalEntityModal {...props} />);
    await user.click(await screen.findByRole('checkbox', { name: 'Свой набор для счетов' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(props.onSubmit).toHaveBeenCalledTimes(1));
    const refreshed = { ...initial, shortName: 'Fresh profile', documentImagePrintDefaultsVersion: 'C'.repeat(64) };
    if (reason === 'company') view.rerender(<LegalEntityModal {...props} initial={{ ...refreshed, id: 10 }} />);
    if (reason === 'close') { view.rerender(<LegalEntityModal {...props} isOpen={false} />); view.rerender(<LegalEntityModal {...props} initial={refreshed} />); }
    if (reason === 'permission') view.rerender(<LegalEntityModal {...props} initial={refreshed} allowEdit={false} />);
    if (reason === 'refresh') view.rerender(<LegalEntityModal {...props} initial={refreshed} />);
    await act(async () => { if (outcome === 'success') resolve({ ...initial, shortName: 'Late obsolete name', documentImagePrintDefaultsVersion: 'B'.repeat(64) }); else reject(new Error('Late obsolete error')); });
    expect(screen.queryByDisplayValue('Late obsolete name')).toBeNull();
    expect(screen.queryByText('Late obsolete error')).toBeNull();
    expect(props.onClose).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
    if (reason !== 'company') expect(screen.getByRole('checkbox', { name: 'Свой набор для счетов' })).toHaveProperty('checked', false);
  });

  it.each(['page', 'modal'] as const)('parent ownership: stale result is silent and cannot close the current %s form', async mode => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Current profile', documentImagePrintDefaultsVersion: 'A'.repeat(64) });
    const onClose = vi.fn();
    const onSubmit = vi.fn().mockRejectedValue(new StaleUiOperationError());
    render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} onClose={onClose} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    await user.click(await screen.findByRole('checkbox', { name: 'Свой набор для счетов' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', false));
    expect(onClose).not.toHaveBeenCalled(); expect(toastErrorMock).not.toHaveBeenCalled();
    expect(screen.getByRole('checkbox', { name: 'Свой набор для счетов' })).toHaveProperty('checked', true);
  });

  it.each(['page', 'modal'] as const)('parent ownership: refuses a wrong saved ID in the current %s form', async mode => {
    const user = userEvent.setup();
    const initial = createInitialLegalEntity({ shortName: 'Current profile', documentImagePrintDefaultsVersion: 'A'.repeat(64) });
    const onClose = vi.fn();
    const onSubmit = vi.fn().mockResolvedValue({ ...initial, id: 10, shortName: 'Wrong returned profile', documentImagePrintDefaultsVersion: 'B'.repeat(64) });
    render(<LegalEntityModal isOpen mode={mode} initial={initial} allowEdit allowDelete={false} onClose={onClose} onSubmit={onSubmit} suggest={vi.fn()} findById={vi.fn()} />);
    await user.click(await screen.findByRole('checkbox', { name: 'Свой набор для счетов' }));
    await user.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
    expect(onClose).not.toHaveBeenCalled(); expect(screen.queryByDisplayValue('Wrong returned profile')).toBeNull();
    expect(screen.getByRole('checkbox', { name: 'Свой набор для счетов' })).toHaveProperty('checked', true);
  });

  it('hides logo, seal and signature fields for non-own legal entities', async () => {
    vi.mocked(apiService.getMainCompanySettings).mockResolvedValueOnce({
      defaultCompanyId: 7,
      companies: [{ id: 7, shortName: 'Наша компания', clientsCount: 0, createdAt: '2026-04-09T13:18:06Z' }],
    });
    const initial = createInitialLegalEntity({ id: 9, shortName: 'Контрагент' });

    render(
      <LegalEntityModal
        isOpen
        onClose={vi.fn()}
        onSubmit={vi.fn().mockResolvedValue(initial)}
        initial={initial}
        suggest={vi.fn().mockResolvedValue([])}
        findById={vi.fn()}
        allowEdit
        allowDelete={false}
      />,
    );

    await waitFor(() => expect(apiService.getMainCompanySettings).toHaveBeenCalled());
    expect(screen.queryByText('Логотип компании')).toBeNull();
    expect(screen.queryByText('Печать компании')).toBeNull();
    expect(screen.queryByText('Подпись руководителя')).toBeNull();
    expect(screen.queryByText('Изображения в документах')).toBeNull();
  });
});
