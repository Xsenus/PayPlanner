import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { MoneyOperations } from './MoneyOperations';
import type { BankStatementAutoImportRunResult, MoneyAccount, MoneyOperation, MoneyOperationsSummary } from '../../types';

const {
  createCashOrderMock,
  createCashInventoryMock,
  createCashOrderFromPaymentMock,
  createCashOrderFromMoneyOperationMock,
  cancelCashOrderMock,
  copyCashOrderMock,
  createMoneyOperationMock,
  createMoneyOperationTransferMock,
  deleteCashOrderMock,
  deleteMoneyOperationMock,
  downloadCashOrderPdfMock,
  getCashBookMock,
  getCashBookPrintHtmlMock,
  getCashInventoriesMock,
  getCashOrderRegisterMock,
  getCashOrderRegisterPrintHtmlMock,
  getCashOrderPrintHtmlMock,
  getBankStatementImportMock,
  getBankStatementImportsMock,
  getCashOrderMock,
  getIncomeTypesMock,
  getActResponsiblesMock,
  getMoneyAccountsMock,
  getMoneyOperationAccountsMock,
  getMoneyOperationAccountSummariesMock,
  getMoneyOperationDailyBalancesMock,
  getMoneyBalanceReconciliationResultsMock,
  getMoneyOperationInvoiceCandidatesMock,
  getMoneyOperationInvoiceCandidatePageMock,
  getMoneyOperationMock,
  getMoneyOperationsPagedMock,
  getMoneyOperationsPrintHtmlMock,
  getPaymentsV2Mock,
  getUserSettingMock,
  exportMoneyOperationsCsvMock,
  exportMoneyOperationsExcelMock,
  exportMoneyOperationsPdfMock,
  importBankStatementMock,
  postCashOrderMock,
  runMoneyBalanceReconciliationMock,
  runBankStatementAutoImportMock,
  reprocessBankStatementImportMock,
  rollbackBankStatementImportMock,
  toastMock,
  toastErrorMock,
  toastInfoMock,
  toastSuccessMock,
  dialogConfirmMock,
  setActiveTabMock,
  updateCashOrderMock,
  updateMoneyOperationMock,
  upsertUserSettingMock,
} = vi.hoisted(() => {
  const toastErrorMock = vi.fn();
  const toastInfoMock = vi.fn();
  const toastSuccessMock = vi.fn();

  return {
    createCashOrderMock: vi.fn(),
    createCashInventoryMock: vi.fn(),
    createCashOrderFromPaymentMock: vi.fn(),
    createCashOrderFromMoneyOperationMock: vi.fn(),
    cancelCashOrderMock: vi.fn(),
    copyCashOrderMock: vi.fn(),
    createMoneyOperationMock: vi.fn(),
    createMoneyOperationTransferMock: vi.fn(),
    deleteCashOrderMock: vi.fn(),
    deleteMoneyOperationMock: vi.fn(),
    downloadCashOrderPdfMock: vi.fn(),
    getCashBookMock: vi.fn(),
    getCashBookPrintHtmlMock: vi.fn(),
    getCashInventoriesMock: vi.fn(),
    getCashOrderRegisterMock: vi.fn(),
    getCashOrderRegisterPrintHtmlMock: vi.fn(),
    getCashOrderPrintHtmlMock: vi.fn(),
    getBankStatementImportMock: vi.fn(),
    getBankStatementImportsMock: vi.fn(),
    getCashOrderMock: vi.fn(),
    getIncomeTypesMock: vi.fn(),
    getActResponsiblesMock: vi.fn(),
    getMoneyAccountsMock: vi.fn(),
    getMoneyOperationAccountsMock: vi.fn(),
    getMoneyOperationAccountSummariesMock: vi.fn(),
    getMoneyOperationDailyBalancesMock: vi.fn(),
    getMoneyBalanceReconciliationResultsMock: vi.fn(),
    getMoneyOperationInvoiceCandidatesMock: vi.fn(),
    getMoneyOperationInvoiceCandidatePageMock: vi.fn(),
    getMoneyOperationMock: vi.fn(),
    getMoneyOperationsPagedMock: vi.fn(),
    getMoneyOperationsPrintHtmlMock: vi.fn(),
    getPaymentsV2Mock: vi.fn(),
    getUserSettingMock: vi.fn(),
    exportMoneyOperationsCsvMock: vi.fn(),
    exportMoneyOperationsExcelMock: vi.fn(),
    exportMoneyOperationsPdfMock: vi.fn(),
    importBankStatementMock: vi.fn(),
    postCashOrderMock: vi.fn(),
    runMoneyBalanceReconciliationMock: vi.fn(),
    runBankStatementAutoImportMock: vi.fn(),
    reprocessBankStatementImportMock: vi.fn(),
    rollbackBankStatementImportMock: vi.fn(),
    toastMock: { error: toastErrorMock, info: toastInfoMock, success: toastSuccessMock },
    toastErrorMock,
    toastInfoMock,
    toastSuccessMock,
    dialogConfirmMock: vi.fn(),
    setActiveTabMock: vi.fn(),
    updateCashOrderMock: vi.fn(),
    updateMoneyOperationMock: vi.fn(),
    upsertUserSettingMock: vi.fn(),
  };
});

const permissionsState = vi.hoisted(() => ({
  accountsCanView: true,
  clientsCanView: true,
  paymentsCanView: true,
  paymentsCanCreate: true,
  paymentsCanEdit: true,
  paymentsCanDelete: true,
  paymentsCanExport: true,
  paymentsViewScope: 'all',
}));
const testColumnState = vi.hoisted(() => ({ extra: false }));

const expectAbortOptions = () => expect.objectContaining({ signal: expect.any(AbortSignal) });

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({ user: { id: 7 } }),
}));

vi.mock('../../hooks/useRolePermissions', () => ({
  useRolePermissions: () => {
    const section = {
      canView: true,
      canCreate: true,
      canEdit: true,
      canDelete: true,
      canExport: true,
      viewScope: 'all',
    };

    return {
      calendar: { ...section, canViewAnalytics: true },
      tasks: { ...section },
      reports: { ...section },
      calculator: { ...section },
      legalEntities: { ...section },
      clients: { ...section, canView: permissionsState.clientsCanView },
      cases: { ...section },
      accounts: { ...section, canView: permissionsState.accountsCanView },
      acts: { ...section },
      payments: {
        ...section,
        canView: permissionsState.paymentsCanView,
        canCreate: permissionsState.paymentsCanCreate,
        canEdit: permissionsState.paymentsCanEdit,
        canDelete: permissionsState.paymentsCanDelete,
        canExport: permissionsState.paymentsCanExport,
        viewScope: permissionsState.paymentsViewScope,
      },
      contracts: { ...section },
      dictionaries: { ...section },
      roles: { ...section },
      audit: { ...section },
    };
  },
}));

vi.mock('../../contexts/TabContext', () => ({
  useTabNavigation: () => ({ setActiveTab: setActiveTabMock }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({
    confirm: dialogConfirmMock,
  }),
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: () => ({
    columnOrder: ['date', 'accountName', 'counterpartyName', 'responsibleName', 'linkedContractNumber', 'linkedActNumber', 'cashOrder', 'attachmentSummary', 'categoryName', 'purpose'],
    orderedVisibleColumns: testColumnState.extra ? ['date', 'income', 'expense', 'vatAmount', 'currency'] : ['date', 'accountName', 'counterpartyName', 'responsibleName', 'linkedContractNumber', 'linkedActNumber', 'cashOrder', 'attachmentSummary', 'categoryName', 'purpose'],
    visibleColumns: ['date', 'accountName', 'counterpartyName', 'responsibleName', 'linkedContractNumber', 'linkedActNumber', 'cashOrder', 'attachmentSummary', 'categoryName', 'purpose'],
    pageSize: 25,
    setPageSize: vi.fn(),
    draggingColumn: null,
    toggleColumn: vi.fn(),
    moveColumn: vi.fn(),
    handleColumnDragStart: vi.fn(),
    handleColumnDragOver: vi.fn(),
    handleColumnDrop: vi.fn(),
    handleColumnDragEnd: vi.fn(),
    handleResizeStart: vi.fn(),
    getColumnWidth: () => 160,
    getTableWidth: (extraWidth = 0, minimumWidth = 0) => Math.max(minimumWidth, extraWidth + 8 * 160),
    resetTableSettings: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    createCashOrder: createCashOrderMock,
    createCashInventory: createCashInventoryMock,
    createCashOrderFromPayment: createCashOrderFromPaymentMock,
    createCashOrderFromMoneyOperation: createCashOrderFromMoneyOperationMock,
    cancelCashOrder: cancelCashOrderMock,
    copyCashOrder: copyCashOrderMock,
    createMoneyOperation: createMoneyOperationMock,
    createMoneyOperationTransfer: createMoneyOperationTransferMock,
    deleteCashOrder: deleteCashOrderMock,
    deleteMoneyOperation: deleteMoneyOperationMock,
    downloadCashOrderPdf: downloadCashOrderPdfMock,
    getCashBook: getCashBookMock,
    getCashBookPrintHtml: getCashBookPrintHtmlMock,
    getCashInventories: getCashInventoriesMock,
    getCashOrderRegister: getCashOrderRegisterMock,
    getCashOrderRegisterPrintHtml: getCashOrderRegisterPrintHtmlMock,
    getCashOrderPrintHtml: getCashOrderPrintHtmlMock,
    getBankStatementImport: getBankStatementImportMock,
    getBankStatementImports: getBankStatementImportsMock,
    getCashOrder: getCashOrderMock,
    getIncomeTypes: getIncomeTypesMock,
    getActResponsibles: getActResponsiblesMock,
    getMoneyAccounts: getMoneyAccountsMock,
    getMoneyOperationAccounts: getMoneyOperationAccountsMock,
    getMoneyOperationAccountSummaries: getMoneyOperationAccountSummariesMock,
    getMoneyOperationDailyBalances: getMoneyOperationDailyBalancesMock,
    getMoneyBalanceReconciliationResults: getMoneyBalanceReconciliationResultsMock,
    getMoneyOperationInvoiceCandidates: getMoneyOperationInvoiceCandidatesMock,
    getMoneyOperationInvoiceCandidatePage: getMoneyOperationInvoiceCandidatePageMock,
    getMoneyOperation: getMoneyOperationMock,
    getMoneyOperationsPaged: getMoneyOperationsPagedMock,
    getMoneyOperationsPrintHtml: getMoneyOperationsPrintHtmlMock,
    getPaymentsV2: getPaymentsV2Mock,
    getUserSetting: getUserSettingMock,
    exportMoneyOperationsCsv: exportMoneyOperationsCsvMock,
    exportMoneyOperationsExcel: exportMoneyOperationsExcelMock,
    exportMoneyOperationsPdf: exportMoneyOperationsPdfMock,
    importBankStatement: importBankStatementMock,
    postCashOrder: postCashOrderMock,
    runMoneyBalanceReconciliation: runMoneyBalanceReconciliationMock,
    runBankStatementAutoImport: runBankStatementAutoImportMock,
    reprocessBankStatementImport: reprocessBankStatementImportMock,
    rollbackBankStatementImport: rollbackBankStatementImportMock,
    updateCashOrder: updateCashOrderMock,
    updateMoneyOperation: updateMoneyOperationMock,
    upsertUserSetting: upsertUserSettingMock,
  },
}));

vi.mock('../../utils/dateUtils', () => ({
  todayYMD: () => '2026-04-21',
  toDateInputValue: (value: string | Date | null | undefined) => {
    if (!value) return '';
    if (value instanceof Date) {
      return value.toISOString().slice(0, 10);
    }

    const normalized = String(value);
    return normalized.slice(0, 10);
  },
  fromInputToApiDate: (value: string | null | undefined) => value ?? undefined,
}));

function createSummary(items: MoneyOperation[]): MoneyOperationsSummary {
  const incomeItems = items.filter((item) => item.accountingType === 'Fact' && item.direction === 'Income');
  const expenseItems = items.filter((item) => item.accountingType === 'Fact' && item.direction === 'Expense');
  const currencies = Array.from(new Set([...incomeItems, ...expenseItems].map((item) => item.currency?.trim().toUpperCase() || null)));
  const currencyStatus = currencies.length === 0 ? 'empty' : currencies.length > 1 ? 'mixed' : currencies[0] ? 'single' : 'unknown';

  return {
    currencyStatus,
    currency: currencyStatus === 'single' ? currencies[0] : null,
    startBalance: 0,
    income: incomeItems.reduce((sum, item) => sum + item.amount, 0),
    expense: expenseItems.reduce((sum, item) => sum + item.amount, 0),
    endBalance:
      incomeItems.reduce((sum, item) => sum + item.amount, 0) -
      expenseItems.reduce((sum, item) => sum + item.amount, 0),
    incomeCount: incomeItems.length,
    expenseCount: expenseItems.length,
    bySource: (['Cash', 'Bank', 'Card', 'EMoney'] as const).map((source) => {
      const sourceIncome = incomeItems
        .filter((item) => item.source === source)
        .reduce((sum, item) => sum + item.amount, 0);
      const sourceExpense = expenseItems
        .filter((item) => item.source === source)
        .reduce((sum, item) => sum + item.amount, 0);

      return {
        source,
        startBalance: 0,
        income: sourceIncome,
        expense: sourceExpense,
        endBalance: sourceIncome - sourceExpense,
      };
    }),
  };
}

function createOperation(overrides: Partial<MoneyOperation> = {}): MoneyOperation {
  return {
    id: overrides.id ?? 1,
    source: overrides.source ?? 'Bank',
    direction: overrides.direction ?? 'Expense',
    accountingType: overrides.accountingType ?? 'Fact',
    moneyAccountId: overrides.moneyAccountId ?? null,
    moneyAccountName: overrides.moneyAccountName ?? null,
    moneyAccount: overrides.moneyAccount ?? null,
    accountName: overrides.accountName ?? 'Основной счёт',
    counterpartyName: overrides.counterpartyName ?? 'Поставщик А',
    responsibleUserId: overrides.responsibleUserId ?? null,
    responsibleName: overrides.responsibleName ?? null,
    categoryName: overrides.categoryName ?? 'Хозрасходы',
    amount: overrides.amount ?? 1500,
    currency: overrides.currency ?? 'RUB',
    paymentOrderNumber: overrides.paymentOrderNumber ?? '873864',
    date: overrides.date ?? '2026-04-20',
    recognitionDate: overrides.recognitionDate ?? '2026-04-20',
    isAdvancePayment: overrides.isAdvancePayment ?? true,
    purpose: overrides.purpose ?? 'Оплата услуг',
    paidInvoiceNumber: overrides.paidInvoiceNumber ?? 'INV-1',
    paidAmount: overrides.paidAmount ?? 1500,
    projectName: overrides.projectName ?? 'Проект Альфа',
    directionName: overrides.directionName ?? 'Корпоративное право',
    salesPointName: overrides.salesPointName ?? 'Офис',
    paymentPriority: overrides.paymentPriority ?? null,
    paidAccountName: overrides.paidAccountName ?? 'Счёт 51',
    linkedInvoiceId: overrides.linkedInvoiceId ?? null,
    linkedInvoiceNumber: overrides.linkedInvoiceNumber ?? null,
    linkedInvoiceDate: overrides.linkedInvoiceDate ?? null,
    linkedContractId: overrides.linkedContractId ?? null,
    linkedContractNumber: overrides.linkedContractNumber ?? null,
    linkedActId: overrides.linkedActId ?? null,
    linkedActNumber: overrides.linkedActNumber ?? null,
    linkedActDate: overrides.linkedActDate ?? null,
    matchStatus: overrides.matchStatus ?? 'None',
    matchedAt: overrides.matchedAt ?? null,
    matchComment: overrides.matchComment ?? null,
    attachmentCount: overrides.attachmentCount ?? 0,
    attachmentSummary: overrides.attachmentSummary ?? null,
    createdAt: overrides.createdAt ?? '2026-04-20T08:00:00Z',
    updatedAt: overrides.updatedAt ?? null,
    isReadonly: overrides.isReadonly ?? false,
    relatedPaymentId: overrides.relatedPaymentId ?? null,
    cashOrderId: overrides.cashOrderId ?? null,
    cashOrderNumber: overrides.cashOrderNumber ?? null,
    cashOrderDate: overrides.cashOrderDate ?? null,
    cashOrderStatus: overrides.cashOrderStatus ?? null,
    canCreateCashOrder: overrides.canCreateCashOrder ?? false,
  };
}

function getLastButton(name: string | RegExp): HTMLElement {
  const buttons = screen.getAllByRole('button', { name });
  const button = buttons.at(-1);
  expect(button).toBeTruthy();
  return button as HTMLElement;
}

async function clickJournalAction(user: ReturnType<typeof userEvent.setup>, name: string): Promise<void> {
  await user.click(screen.getByRole('button', { name: 'Экспорт и печать' }));
  await user.click(await screen.findByRole('button', { name }));
}

async function selectBankImportAccount(user: ReturnType<typeof userEvent.setup>, id = '101') {
  const select = await screen.findByLabelText('Банковский счёт в заголовке') as HTMLSelectElement;
  await waitFor(() => expect(Array.from(select.options, option => option.value)).toContain(id));
  await user.selectOptions(select, id);
  return select;
}

function enableBankImportFolderFixture() {
  getUserSettingMock.mockImplementation(async (key: string) => {
    if (key !== 'money.bankStatement.autoImport') throw new Error('not found');
    return { key, value: JSON.stringify({ enabled: true, folderPath: 'C:\\BankStatements', fileMask: '*.csv' }), updatedAt: '2026-10-24T00:00:00Z' };
  });
}

function createBankFolderRunResult(overrides: Partial<BankStatementAutoImportRunResult> = {}): BankStatementAutoImportRunResult {
  return {
    enabled: true, folderPath: 'C:\\BankStatements', fileMask: '*.csv', checkedAt: '2026-10-24T00:00:00Z',
    filesFound: 0, filesImported: 0, rowsRead: 0, created: 0, skipped: 0, errors: [], files: [], ...overrides,
  };
}

describe('MoneyOperations integration', () => {
  let operations: MoneyOperation[];
  let nextId: number;

  beforeEach(() => {
    testColumnState.extra = false;
    operations = [createOperation()];
    nextId = 10;

    createCashOrderMock.mockReset();
    createCashInventoryMock.mockReset();
    createCashOrderFromPaymentMock.mockReset();
    createCashOrderFromMoneyOperationMock.mockReset();
    cancelCashOrderMock.mockReset();
    copyCashOrderMock.mockReset();
    createMoneyOperationMock.mockReset();
    createMoneyOperationTransferMock.mockReset();
    deleteCashOrderMock.mockReset();
    deleteMoneyOperationMock.mockReset();
    downloadCashOrderPdfMock.mockReset();
    getCashBookMock.mockReset();
    getCashBookPrintHtmlMock.mockReset();
    getCashInventoriesMock.mockReset();
    getCashOrderPrintHtmlMock.mockReset();
    getCashOrderMock.mockReset();
    getCashOrderRegisterMock.mockReset();
    getCashOrderRegisterPrintHtmlMock.mockReset();
    getBankStatementImportMock.mockReset();
    getBankStatementImportsMock.mockReset();
    getIncomeTypesMock.mockReset();
    getActResponsiblesMock.mockReset();
    getMoneyAccountsMock.mockReset();
    getMoneyOperationAccountsMock.mockReset();
    getMoneyOperationAccountSummariesMock.mockReset();
    getMoneyOperationDailyBalancesMock.mockReset();
    getMoneyBalanceReconciliationResultsMock.mockReset();
    getMoneyOperationInvoiceCandidatesMock.mockReset();
    getMoneyOperationInvoiceCandidatePageMock.mockReset();
    getMoneyOperationInvoiceCandidatePageMock.mockImplementation(async (id, search, cursor, signal) => ({
      items: await getMoneyOperationInvoiceCandidatesMock(id, search, signal), search: search ?? null,
      cursor: cursor ?? null, nextCursor: null, hasMore: false,
    }));
    getMoneyOperationMock.mockReset();
    getMoneyOperationsPagedMock.mockReset();
    getMoneyOperationsPrintHtmlMock.mockReset();
    getPaymentsV2Mock.mockReset();
    getUserSettingMock.mockReset();
    exportMoneyOperationsCsvMock.mockReset();
    exportMoneyOperationsExcelMock.mockReset();
    exportMoneyOperationsPdfMock.mockReset();
    importBankStatementMock.mockReset();
    postCashOrderMock.mockReset();
    runMoneyBalanceReconciliationMock.mockReset();
    runBankStatementAutoImportMock.mockReset();
    reprocessBankStatementImportMock.mockReset();
    rollbackBankStatementImportMock.mockReset();
    toastErrorMock.mockReset();
    toastInfoMock.mockReset();
    toastSuccessMock.mockReset();
    dialogConfirmMock.mockReset();
    dialogConfirmMock.mockResolvedValue(true);
    setActiveTabMock.mockReset();
    updateCashOrderMock.mockReset();
    updateMoneyOperationMock.mockReset();
    upsertUserSettingMock.mockReset();
    permissionsState.accountsCanView = true;
    permissionsState.clientsCanView = true;
    permissionsState.paymentsCanView = true;
    permissionsState.paymentsCanCreate = true;
    permissionsState.paymentsCanEdit = true;
    permissionsState.paymentsCanDelete = true;
    permissionsState.paymentsCanExport = true;
    permissionsState.paymentsViewScope = 'all';
    window.localStorage.clear();
    window.sessionStorage.clear();
    if (!URL.createObjectURL) {
      Object.defineProperty(URL, 'createObjectURL', { value: vi.fn(), configurable: true });
    }
    if (!URL.revokeObjectURL) {
      Object.defineProperty(URL, 'revokeObjectURL', { value: vi.fn(), configurable: true });
    }
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:cash-order');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    vi.spyOn(window, 'open').mockImplementation(() => null);

    const moneyAccounts: MoneyAccount[] = [
      {
        id: 101,
        companyId: 1,
        name: 'Основной счёт',
        source: 'Bank',
        currency: 'RUB',
        bankName: 'Банк',
        bankAccountNumber: null,
        isDefault: true,
        isActive: true,
        createdAt: '2026-04-20T08:00:00Z',
        updatedAt: null,
      },
      {
        id: 102,
        companyId: 1,
        name: 'Резервный счёт',
        source: 'Bank',
        currency: 'RUB',
        bankName: 'Банк',
        bankAccountNumber: null,
        isDefault: false,
        isActive: true,
        createdAt: '2026-04-20T08:00:00Z',
        updatedAt: null,
      },
      {
        id: 103,
        companyId: 1,
        name: 'Основная касса',
        source: 'Cash',
        currency: 'RUB',
        bankName: null,
        bankAccountNumber: null,
        isDefault: false,
        isActive: true,
        createdAt: '2026-04-20T08:00:00Z',
        updatedAt: null,
      },
      {
        id: 104,
        companyId: 1,
        name: 'Резервная касса',
        source: 'Cash',
        currency: 'RUB',
        bankName: null,
        bankAccountNumber: null,
        isDefault: false,
        isActive: true,
        createdAt: '2026-04-20T08:00:00Z',
        updatedAt: null,
      },
    ];

    getMoneyAccountsMock.mockImplementation(async (params?: { source?: 'Bank' | 'Cash' | 'Card' | 'EMoney' }) =>
      moneyAccounts.filter((account) => !params?.source || account.source === params.source),
    );
    getIncomeTypesMock.mockImplementation(async (paymentType?: 'Income' | 'Expense') => [
      {
        id: paymentType === 'Expense' ? 2 : 1,
        name: paymentType === 'Expense' ? 'Хозрасходы' : 'Доход от услуг',
        description: '',
        paymentType: paymentType ?? 'Income',
        isActive: true,
        createdAt: '2026-04-20T08:00:00Z',
      },
    ]);
    getActResponsiblesMock.mockResolvedValue([
      { id: 12, fullName: 'Анна Иванова', email: 'anna@example.test' },
      { id: 13, fullName: 'Олег Бухгалтер', email: 'oleg@example.test' },
    ]);

    getMoneyOperationAccountsMock.mockImplementation(async (params?: { source?: 'Bank' | 'Cash' | 'Card' | 'EMoney' }) =>
      Array.from(
        new Set(
          operations
            .filter((item) => !params?.source || item.source === params.source)
            .map((item) => item.accountName)
            .filter(Boolean) as string[],
        ),
      ),
    );
    getMoneyOperationAccountSummariesMock.mockImplementation(async (params?: { source?: 'Bank' | 'Cash' | 'Card' | 'EMoney' }) => {
      const accountNames = Array.from(
        new Set(
          operations
            .filter((item) => !params?.source || item.source === params.source)
            .map((item) => item.accountName)
            .filter(Boolean) as string[],
        ),
      );

      return accountNames.map((accountName) => {
        const accountItems = operations.filter(
          (item) => item.accountName === accountName && (!params?.source || item.source === params.source),
        );
        const incomeItems = accountItems.filter((item) => item.direction === 'Income');
        const expenseItems = accountItems.filter((item) => item.direction === 'Expense');
        const income = incomeItems.reduce((sum, item) => sum + item.amount, 0);
        const expense = expenseItems.reduce((sum, item) => sum + item.amount, 0);

        return {
          accountName,
          source: params?.source ?? accountItems[0]?.source ?? 'Bank',
          startBalance: 0,
          income,
          expense,
          endBalance: income - expense,
          incomeCount: incomeItems.length,
          expenseCount: expenseItems.length,
        };
      });
    });
    getMoneyOperationDailyBalancesMock.mockResolvedValue([]);
    getMoneyBalanceReconciliationResultsMock.mockResolvedValue([]);
    getMoneyOperationInvoiceCandidatesMock.mockResolvedValue([]);

    getMoneyOperationsPagedMock.mockImplementation(
      async (params?: {
        source?: 'Bank' | 'Cash' | 'Card' | 'EMoney';
        moneyAccountId?: number;
        accountName?: string;
        categoryName?: string;
        direction?: 'Income' | 'Expense' | 'Transfer';
        accountingType?: 'Fact' | 'Plan';
      }) => {
      const filtered = operations.filter(
        (item) =>
          (!params?.source || item.source === params.source) &&
          (!params?.moneyAccountId || item.moneyAccountId === params.moneyAccountId) &&
          (!params?.accountName || item.accountName === params.accountName) &&
          (!params?.categoryName || item.categoryName?.includes(params.categoryName)) &&
          (!params?.direction || item.direction === params.direction) &&
          (!params?.accountingType || item.accountingType === params.accountingType),
      );
      return {
        items: filtered,
        total: filtered.length,
        page: 1,
        pageSize: 25,
        summary: createSummary(filtered),
      };
    });
    getPaymentsV2Mock.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 1,
      summary: {
        income: 0,
        expense: 0,
        balance: 0,
        incomeCount: 0,
        expenseCount: 0,
        total: 0,
      },
    });

    getCashOrderRegisterMock.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 10,
      incomingCount: 0,
      outgoingCount: 0,
      draftCount: 0,
      postedCount: 0,
      cancelledCount: 0,
      incomingAmount: 0,
      outgoingAmount: 0,
    });
    getCashBookMock.mockResolvedValue({
      from: '2026-04-01',
      to: '2026-04-30',
      currencyStatus: 'single',
      currency: 'RUB',
      openingBalance: 0,
      income: 25000,
      expense: 0,
      closingBalance: 25000,
      incomingCount: 1,
      outgoingCount: 0,
      days: [
        {
          date: '2026-04-29',
          openingBalance: 0,
          income: 25000,
          expense: 0,
          closingBalance: 25000,
          incomingCount: 1,
          outgoingCount: 0,
        },
      ],
    });
    getCashBookPrintHtmlMock.mockResolvedValue('<html><body>Кассовая книга</body></html>');
    getCashInventoriesMock.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 8,
    });
    createCashInventoryMock.mockResolvedValue({
      id: 1,
      number: 'ИНВК-2026-0001',
      date: '2026-04-30',
      accountName: 'Основная касса',
      accountingBalance: 0,
      actualBalance: 0,
      difference: 0,
      correctionCashOrderId: null,
      correctionCashOrderNumber: null,
      comment: null,
      createdAt: '2026-04-30T08:00:00Z',
      updatedAt: null,
    });
    getCashOrderRegisterPrintHtmlMock.mockResolvedValue('<html><body>Журнал регистрации</body></html>');
    getMoneyOperationsPrintHtmlMock.mockResolvedValue('<html><body>Денежный журнал</body></html>');
    getUserSettingMock.mockRejectedValue(new Error('not found'));
    upsertUserSettingMock.mockImplementation(async (key: string, value: string) => ({
      key,
      value,
      updatedAt: '2026-04-21T08:00:00Z',
    }));
    exportMoneyOperationsCsvMock.mockResolvedValue({
      blob: new Blob(['Дата;Источник'], { type: 'text/csv;charset=utf-8' }),
      fileName: 'money-operations.csv',
    });
    exportMoneyOperationsPdfMock.mockResolvedValue({
      blob: new Blob(['%PDF-money-journal%'], { type: 'application/pdf' }),
      fileName: 'money-operations.pdf',
    });
    exportMoneyOperationsExcelMock.mockResolvedValue({
      blob: new Blob(['<Workbook />'], { type: 'application/vnd.ms-excel' }),
      fileName: 'money-operations.xls',
    });
    getBankStatementImportsMock.mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 5,
    });
    getBankStatementImportMock.mockImplementation(async (id: number) => ({
      id,
      fileName: 'statement.csv',
      status: 'Applied',
      rowsRead: 1,
      createdRows: 1,
      skippedRows: 0,
      errorRows: 0,
      rollbackRows: 0,
      fileHash: 'hash',
      fileSize: 128,
      errorSummary: null,
      createdAt: '2026-04-29T10:00:00Z',
      rolledBackAt: null,
      rows: [
        {
          id: 1,
          rowNumber: 2,
          status: 'Created',
          message: null,
          moneyOperationId: 44,
          date: '2026-04-29',
          direction: 'Income',
          amount: 25000,
          accountName: 'Основной счет',
          counterpartyName: 'Ахмедова Айтан Рафик Кызы',
          purpose: 'Оплата по счету №33',
          paymentOrderNumber: '101',
          createdAt: '2026-04-29T10:00:00Z',
          rolledBackAt: null,
        },
      ],
    }));
    rollbackBankStatementImportMock.mockResolvedValue({
      batchId: 1,
      status: 'RolledBack',
      deletedOperations: 1,
      skippedRows: 0,
      messages: [],
    });
    reprocessBankStatementImportMock.mockResolvedValue({
      batchId: 2,
      rowsRead: 1,
      created: 1,
      skipped: 0,
      errors: [],
      operations: [],
    });
    getCashOrderMock.mockImplementation(async (id: number) => ({
      id,
      number: 'ПКО-2026-0003',
      type: 'Incoming',
      status: 'Posted',
      date: '2026-04-29',
      amount: 25000,
      currency: 'RUB',
      clientId: 74,
      clientName: 'Ахмедова Айтан Рафик Кызы',
      paymentId: 33,
      moneyOperationId: null,
      invoiceId: 33,
      account: 'Основная касса',
      payerName: 'Ахмедова Айтан Рафик Кызы',
      receiverName: 'Основная касса',
      basis: 'Оплата от клиента. счет №33',
      appendix: 'Счёт №33',
      comment: null,
      cancelledAt: null,
      cancelledReason: null,
      createdAt: '2026-04-29T10:00:00Z',
      updatedAt: null,
    }));
    updateCashOrderMock.mockImplementation(async (_id: number, payload: unknown) => payload);
    postCashOrderMock.mockImplementation(async (id: number) => ({
      ...(await getCashOrderMock(id)),
      status: 'Posted',
      updatedAt: '2026-04-29T12:30:00Z',
    }));
    createCashOrderMock.mockImplementation(async (payload: unknown) => ({
      ...(payload as object),
      id: 77,
      number: 'ПКО-2026-0100',
      createdAt: '2026-04-29T12:00:00Z',
      updatedAt: null,
    }));
    cancelCashOrderMock.mockImplementation(async (_id: number, reason?: string) => ({
      ...(await getCashOrderMock(_id)),
      status: 'Cancelled',
      cancelledAt: '2026-04-29T11:00:00Z',
      cancelledReason: reason ?? null,
    }));
    copyCashOrderMock.mockImplementation(async (_id: number) => ({
      ...(await getCashOrderMock(_id)),
      id: 10,
      number: 'ПКО-2026-0004',
    }));
    deleteCashOrderMock.mockResolvedValue(undefined);
    downloadCashOrderPdfMock.mockResolvedValue(new Blob(['pdf'], { type: 'application/pdf' }));

    getMoneyOperationMock.mockImplementation(async (id: number) => operations.find((item) => item.id === id));

    importBankStatementMock.mockResolvedValue({
      batchId: null,
      rowsRead: 0,
      created: 0,
      skipped: 0,
      errors: [],
      operations: [],
    });
    runBankStatementAutoImportMock.mockResolvedValue({
      enabled: true,
      folderPath: 'C:\\BankStatements',
      fileMask: '*.csv',
      checkedAt: '2026-04-21T08:00:00Z',
      filesFound: 0,
      filesImported: 0,
      rowsRead: 0,
      created: 0,
      skipped: 0,
      errors: [],
      files: [],
    });

    createMoneyOperationMock.mockImplementation(async (payload: MoneyOperation) => {
      const created = { ...payload, id: nextId++, updatedAt: null };
      operations = [created, ...operations];
      return created;
    });
    createMoneyOperationTransferMock.mockImplementation(async (payload: {
      sourceMoneyAccountId: number;
      targetMoneyAccountId: number;
      amount: number;
      currency?: string | null;
      date: string;
      purpose?: string | null;
    }) => {
      const sourceOperation = createOperation({
        id: nextId++,
        source: 'Bank',
        direction: 'Expense',
        moneyAccountId: payload.sourceMoneyAccountId,
        accountName: 'Основной счёт',
        counterpartyName: 'Основная касса',
        amount: payload.amount,
        currency: payload.currency ?? 'RUB',
        date: payload.date,
        purpose: payload.purpose ?? 'Перевод между счетами',
        matchStatus: 'Ignored',
      });
      const targetOperation = createOperation({
        id: nextId++,
        source: 'Cash',
        direction: 'Income',
        moneyAccountId: payload.targetMoneyAccountId,
        accountName: 'Основная касса',
        counterpartyName: 'Основной счёт',
        amount: payload.amount,
        currency: payload.currency ?? 'RUB',
        date: payload.date,
        purpose: payload.purpose ?? 'Перевод между счетами',
        matchStatus: 'Ignored',
      });
      operations = [targetOperation, sourceOperation, ...operations];
      return { sourceOperation, targetOperation };
    });

    createCashOrderFromPaymentMock.mockImplementation(async (paymentId: number) => ({
      id: 5,
      number: 'ПКО-2026-0001',
      type: 'Incoming',
      status: 'Posted',
      date: '2026-04-20',
      amount: 25000,
      currency: 'RUB',
      clientId: 74,
      paymentId,
      invoiceId: null,
      account: '33',
      payerName: 'Ахмедова Айтан Рафик Кызы',
      receiverName: 'Основная касса',
      basis: 'Оплата от клиента. счет №33',
      appendix: null,
      comment: null,
      cancelledAt: null,
      cancelledReason: null,
      createdAt: '2026-04-20T08:00:00Z',
      updatedAt: null,
    }));

    createCashOrderFromMoneyOperationMock.mockImplementation(async (operationId: number) => ({
      id: 6,
      number: 'ПКО-2026-0002',
      type: 'Incoming',
      status: 'Posted',
      date: '2026-04-20',
      amount: 5000,
      currency: 'RUB',
      clientId: null,
      paymentId: null,
      moneyOperationId: operationId,
      invoiceId: null,
      account: 'Касса',
      payerName: 'Ручной клиент',
      receiverName: 'Касса',
      basis: 'Прием наличных в кассу',
      appendix: null,
      comment: null,
      cancelledAt: null,
      cancelledReason: null,
      createdAt: '2026-04-20T08:00:00Z',
      updatedAt: null,
    }));

    updateMoneyOperationMock.mockImplementation(async (id: number, payload: MoneyOperation) => {
      const updated = {
        ...payload,
        id,
        linkedInvoiceNumber: payload.linkedInvoiceId ? String(payload.linkedInvoiceId) : null,
        matchStatus: payload.linkedInvoiceId ? 'Matched' : payload.matchStatus,
        updatedAt: '2026-04-21T09:00:00Z',
      } as MoneyOperation;
      operations = operations.map((item) => (item.id === id ? updated : item));
      return updated;
    });

    deleteMoneyOperationMock.mockImplementation(async (id: number) => {
      operations = operations.filter((item) => item.id !== id);
    });
  });

  it('renders operation responsible in the money journal table', async () => {
    operations = [
      createOperation({
        responsibleUserId: 12,
        responsibleName: 'Анна Иванова',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Ответственный')).toBeTruthy();
    expect(await screen.findByText('Анна Иванова')).toBeTruthy();
  });

  it('shows advance review monitor and opens payment review from money journal', async () => {
    const user = userEvent.setup();
    getPaymentsV2Mock.mockResolvedValue({
      items: [],
      total: 2,
      page: 1,
      pageSize: 1,
      summary: {
        income: 9_600,
        expense: 0,
        balance: 9_600,
        incomeCount: 2,
        expenseCount: 0,
        total: 9_600,
      },
    });

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Контроль авансов')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText((text) => text.includes('На разборе: 2') && text.includes('9'))).toBeTruthy();
    });
    expect(getPaymentsV2Mock).toHaveBeenCalledWith({
      advanceReviewStatus: 'RequiresDecision',
      page: 1,
      pageSize: 1,
    });

    await user.click(screen.getByRole('button', { name: 'Открыть разбор' }));

    expect(window.sessionStorage.getItem('pp.payments.advanceReviewFilter')).toBe('RequiresDecision');
    expect(setActiveTabMock).toHaveBeenCalledWith('payments');
  });

  it('renders linked contract number in the money journal table', async () => {
    operations = [
      createOperation({
        linkedContractId: 44,
        linkedContractNumber: 'ДОГ-44/26',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Договор')).toBeTruthy();
    expect(await screen.findByText('ДОГ-44/26')).toBeTruthy();
  });

  it('renders linked act number in the money journal table', async () => {
    operations = [
      createOperation({
        linkedActId: 45,
        linkedActNumber: 'АКТ-45/26',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Акт')).toBeTruthy();
    expect(await screen.findByText('АКТ-45/26')).toBeTruthy();
  });

  it('renders cash order number and date in the money journal table', async () => {
    operations = [
      createOperation({
        source: 'Cash',
        cashOrderId: 5,
        cashOrderNumber: 'PKO-2026-0005',
        cashOrderDate: '2026-04-29',
      }),
    ];

    render(<MoneyOperations source="Cash" />);

    const cashOrderNumber = await screen.findByText('PKO-2026-0005');
    const cashOrderButton = cashOrderNumber.closest('button');

    expect(cashOrderButton).toBeTruthy();
    expect(within(cashOrderButton as HTMLElement).getByText('29.04.2026')).toBeTruthy();
  });

  it('renders attachment summary in the money journal table', async () => {
    operations = [
      createOperation({
        attachmentCount: 1,
        attachmentSummary: 'statement-may.csv',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Вложения')).toBeTruthy();
    expect(await screen.findByText('statement-may.csv')).toBeTruthy();
  });

  it('renders daily balance reconciliation rows', async () => {
    operations = [createOperation({ accountName: 'Основной банк', amount: 500 })];
    getMoneyOperationDailyBalancesMock.mockResolvedValueOnce([
      {
        date: '2026-05-01',
        accountName: 'Основной банк',
        source: 'Bank',
        companyId: 1, moneyAccountId: 101, currency: 'RUB', balanceKey: 'bank101',
        openingBalance: 1000,
        income: 500,
        expense: 200,
        closingBalance: 1300,
        incomeCount: 1,
        expenseCount: 1,
        isBalanced: true,
      },
    ]);

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Дневная сверка остатков')).toBeTruthy();
    expect((await screen.findAllByText('Основной банк')).length).toBeGreaterThan(0);
    expect(await screen.findByText('Сходится')).toBeTruthy();
    expect(getMoneyOperationDailyBalancesMock).toHaveBeenCalledWith(
      expect.objectContaining({ source: 'Bank', from: '2026-01-01', to: '2026-12-31' }),
    );
  });

  it('does not label or compare an old daily balance response without confirmed identity and currency', async () => {
    const old = { date: '2026-05-01', accountName: 'Старый счёт', source: 'Bank', openingBalance: 1000,
      income: 500, expense: 0, closingBalance: 1500, incomeCount: 1, expenseCount: 0, isBalanced: true };
    getMoneyOperationDailyBalancesMock.mockResolvedValue([old]);
    getMoneyBalanceReconciliationResultsMock.mockResolvedValue([{ ...old, id: 7, checkedAt: '2026-05-02T10:00:00Z', issueCount: 0 }]);
    render(<MoneyOperations source="Bank" />);
    const account = await screen.findByText('Старый счёт');
    const row = account.closest('tr')!;
    expect(within(row).getByText('Не подтверждено')).toBeTruthy();
    expect(within(row).queryByText('Актуально')).toBeNull();
    expect(within(row).queryByText('1 500,00 ₽')).toBeNull();
  });

  it.each([
    { label: 'Неизвестная валюта', companyId: 1, moneyAccountId: 101, currency: null, balanceKey: 'unknown-currency' },
    { label: 'Неизвестная касса', companyId: 1, moneyAccountId: null, currency: 'RUB', balanceKey: 'unknown-account' },
    { label: 'Неизвестная компания', companyId: null, moneyAccountId: 101, currency: 'RUB', balanceKey: 'unknown-company' },
    { label: 'Нет ключа', companyId: 1, moneyAccountId: 101, currency: 'RUB', balanceKey: null },
  ])('hides unconfirmed balance amounts: $label', async (identity) => {
    const row = { ...identity, accountName: identity.label, date: '2026-05-01', source: 'Bank', openingBalance: 0,
      income: 500, expense: 0, closingBalance: 500, incomeCount: 1, expenseCount: 0, isBalanced: true };
    getMoneyOperationDailyBalancesMock.mockResolvedValue([row]);
    getMoneyBalanceReconciliationResultsMock.mockResolvedValue([{ ...row, id: 7, checkedAt: '2026-05-02T10:00:00Z', issueCount: 0 }]);
    render(<MoneyOperations source="Bank" />);
    const tableRow = (await screen.findByText(identity.label)).closest('tr')!;
    expect(within(tableRow).getByText('Не подтверждено')).toBeTruthy();
    expect(within(tableRow).queryByText(/500,00/)).toBeNull();
    expect(within(tableRow).queryByText('Актуально')).toBeNull();
  });

  it('shows saved reconciliation load failure and retries without losing daily balances', async () => {
    const user = userEvent.setup();
    const balance = {
      date: '2026-05-01', accountName: 'Основной банк', source: 'Bank', companyId: 1, moneyAccountId: 101,
      currency: 'RUB', balanceKey: 'bank101', openingBalance: 100, income: 50, expense: 0,
      closingBalance: 150, incomeCount: 1, expenseCount: 0, isBalanced: true,
    };
    getMoneyOperationDailyBalancesMock.mockResolvedValue([balance]);
    getMoneyBalanceReconciliationResultsMock.mockRejectedValueOnce(new Error('offline')).mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce([{
      ...balance, id: 12, checkedAt: '2026-05-02T10:00:00Z', issueCount: 0,
    }]);
    render(<MoneyOperations source="Bank" />);
    expect(await screen.findByText('Не удалось загрузить сохранённую сверку.')).toBeTruthy();
    expect(screen.getByText('Дневная сверка остатков')).toBeTruthy();
    expect(screen.queryByText('Нет')).toBeNull();
    expect((screen.getByRole('button', { name: 'Запустить сверку' }) as HTMLButtonElement).disabled).toBe(true);
    await user.click(screen.getByRole('button', { name: 'Повторить загрузку сверки' }));
    expect(await screen.findByText('Не удалось загрузить сохранённую сверку.')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Повторить загрузку сверки' }));
    expect(await screen.findByText('Актуально')).toBeTruthy();
    expect(screen.queryByText('Не удалось загрузить сохранённую сверку.')).toBeNull();
    expect(getMoneyOperationDailyBalancesMock).toHaveBeenCalledTimes(1);
  });

  it('ignores a late saved reconciliation response after changing sections', async () => {
    let finishBank!: (value: unknown[]) => void;
    getMoneyBalanceReconciliationResultsMock.mockImplementation(({ source }: { source: string }) =>
      source === 'Bank' ? new Promise((resolve) => { finishBank = resolve; }) : Promise.resolve([]));
    const { rerender } = render(<MoneyOperations source="Bank" />);
    await waitFor(() => expect(finishBank).toBeDefined());
    rerender(<MoneyOperations source="Cash" />);
    await waitFor(() => expect(getMoneyBalanceReconciliationResultsMock).toHaveBeenCalledWith(expect.objectContaining({ source: 'Cash' })));
    await act(async () => finishBank([{ date: '2026-05-01', accountName: 'Чужой раздел', source: 'Bank', checkedAt: '2026-05-02', id: 1 }]));
    expect(screen.queryByText('Чужой раздел')).toBeNull();
    expect(screen.queryByText('Не удалось загрузить сохранённую сверку.')).toBeNull();
  });

  it('does not load or offer the shared reconciliation with limited payment visibility', async () => {
    permissionsState.paymentsViewScope = 'own';
    operations = [createOperation({ accountName: 'Основной банк', amount: 500 })];
    getMoneyOperationDailyBalancesMock.mockResolvedValueOnce([
      {
        date: '2026-05-01',
        accountName: 'Основной банк',
        source: 'Bank',
        openingBalance: 1000,
        income: 500,
        expense: 0,
        closingBalance: 1500,
        incomeCount: 1,
        expenseCount: 0,
        isBalanced: true,
      },
    ]);

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Дневное движение доступных операций')).toBeTruthy();
    expect(screen.getByText('Общий остаток и сохранённая сверка доступны только при полном доступе к платежам.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Запустить сверку' })).toBeNull();
    expect(screen.queryByText('Сходится')).toBeNull();
    expect(getMoneyBalanceReconciliationResultsMock).not.toHaveBeenCalled();
    expect(runMoneyBalanceReconciliationMock).not.toHaveBeenCalled();
  });

  it('runs and stores daily balance reconciliation rows', async () => {
    const user = userEvent.setup();
    operations = [createOperation({ accountName: 'Основной банк', amount: 500 })];
    getMoneyOperationDailyBalancesMock.mockResolvedValueOnce([
      {
        date: '2026-05-01',
        accountName: 'Основной банк',
        source: 'Bank',
        companyId: 1, moneyAccountId: 101, currency: 'RUB', balanceKey: 'bank101',
        openingBalance: 1000,
        income: 500,
        expense: 0,
        closingBalance: 1500,
        incomeCount: 1,
        expenseCount: 0,
        isBalanced: true,
      },
    ]);
    runMoneyBalanceReconciliationMock.mockResolvedValueOnce({
      checkedAt: '2026-05-02T10:00:00Z',
      created: 1,
      updated: 0,
      issueCount: 0,
      items: [
        {
          id: 7,
          date: '2026-05-01',
          accountName: 'Основной банк',
          source: 'Bank',
          companyId: 1, moneyAccountId: 101, currency: 'RUB', balanceKey: 'bank101',
          openingBalance: 1000,
          income: 500,
          expense: 0,
          closingBalance: 1500,
          incomeCount: 1,
          expenseCount: 0,
          isBalanced: true,
          issueCount: 0,
          checkedAt: '2026-05-02T10:00:00Z',
          checkedByUserId: 7,
        },
      ],
    });

    render(<MoneyOperations source="Bank" />);

    await user.click(await screen.findByRole('button', { name: 'Запустить сверку' }));

    await waitFor(() =>
      expect(runMoneyBalanceReconciliationMock).toHaveBeenCalledWith(
        expect.objectContaining({ source: 'Bank', from: '2026-01-01', to: '2026-12-31' }),
      ),
    );
    expect(await screen.findByText('Актуально')).toBeTruthy();
    expect(screen.getByTitle((title) => title.startsWith('Сохранено 02.05.2026'))).toBeTruthy();
    expect(toastSuccessMock).toHaveBeenCalledWith(
      'Сверка сохранена: создано 1, обновлено 0, замечаний 0.',
      'Сверка остатков',
    );
  });

  it('runs reconciliation using the selected account ID rather than its shared name', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Альфа', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 2, companyName: 'Бета', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: false },
    ]);
    const first = {
      date: '2026-05-01', accountName: 'Старое название', source: 'Bank', companyId: 1, moneyAccountId: 201,
      currency: 'RUB', balanceKey: 'bank201', openingBalance: 0, income: 10, expense: 0,
      closingBalance: 10, incomeCount: 1, expenseCount: 0, isBalanced: true,
    };
    const second = { ...first, accountName: 'Второй счёт', companyId: 2, moneyAccountId: 202, balanceKey: 'bank202', income: 20, closingBalance: 20 };
    getMoneyOperationDailyBalancesMock.mockResolvedValue([first, second]);
    getMoneyBalanceReconciliationResultsMock.mockResolvedValue([{ ...second, id: 2, checkedAt: '2026-05-02T10:00:00Z', issueCount: 0 }]);
    runMoneyBalanceReconciliationMock.mockResolvedValue({ created: 1, updated: 0, issueCount: 0,
      items: [{ ...first, id: 1, checkedAt: '2026-05-03T10:00:00Z', issueCount: 0 }] });
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Старое название');
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '201');
    await user.click(screen.getByRole('button', { name: 'Запустить сверку' }));
    expect(runMoneyBalanceReconciliationMock).toHaveBeenCalledWith(expect.objectContaining({ source: 'Bank', moneyAccountId: 201 }));
    expect(runMoneyBalanceReconciliationMock.mock.calls[0][0]).not.toHaveProperty('accountName');
    expect(await screen.findByText('Актуально')).toBeTruthy();
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '202');
    expect(screen.getByText('Второй счёт')).toBeTruthy();
    expect(screen.getByText('Актуально')).toBeTruthy();
  });

  it('does not display a delayed reconciliation result in the newly selected account', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Альфа', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 2, companyName: 'Бета', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: false },
    ]);
    const row = { date: '2026-05-01', source: 'Bank', currency: 'RUB', openingBalance: 0, expense: 0,
      incomeCount: 1, expenseCount: 0, isBalanced: true };
    getMoneyOperationDailyBalancesMock.mockResolvedValue([
      { ...row, accountName: 'Первый', companyId: 1, moneyAccountId: 201, balanceKey: 'bank201', income: 10, closingBalance: 10 },
      { ...row, accountName: 'Второй', companyId: 2, moneyAccountId: 202, balanceKey: 'bank202', income: 20, closingBalance: 20 },
    ]);
    let finish!: (value: unknown) => void;
    runMoneyBalanceReconciliationMock.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Первый');
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '201');
    await user.click(screen.getByRole('button', { name: 'Запустить сверку' }));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '202');
    await act(async () => finish({ created: 1, updated: 0, issueCount: 0, items: [{ ...row, id: 1, companyId: 1, moneyAccountId: 201,
      accountName: 'Первый', balanceKey: 'bank201', income: 10, closingBalance: 10, checkedAt: '2026-05-03T10:00:00Z', issueCount: 0 }] }));
    expect(screen.getByText('Второй')).toBeTruthy();
    expect(screen.queryByText('Актуально')).toBeNull();
    expect(toastSuccessMock).not.toHaveBeenCalled();
    await waitFor(() => expect(getMoneyBalanceReconciliationResultsMock).toHaveBeenCalledTimes(2));
  });

  it('does not run a named historical account without a confirmed account ID', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([]);
    getMoneyOperationAccountSummariesMock.mockResolvedValue([{ accountName: 'Историческая касса', source: 'Cash', moneyAccountId: null }]);
    getMoneyOperationDailyBalancesMock.mockResolvedValue([{ date: '2026-05-01', accountName: 'Историческая касса', source: 'Cash',
      companyId: null, moneyAccountId: null, currency: 'RUB', balanceKey: null, openingBalance: 0, income: 10, expense: 0,
      closingBalance: 10, incomeCount: 1, expenseCount: 0, isBalanced: true }]);
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Дневная сверка остатков');
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Касса'), 'Историческая касса');
    await user.click(screen.getByRole('button', { name: 'Запустить сверку' }));
    expect(runMoneyBalanceReconciliationMock).not.toHaveBeenCalled();
    expect(toastErrorMock).toHaveBeenCalledWith('Выберите подтверждённый счёт для сверки или откройте все счета.', 'Сверка остатков');
  });

  it('highlights stale saved daily balance reconciliation rows', async () => {
    operations = [createOperation({ accountName: 'Основной банк', amount: 500 })];
    getMoneyOperationDailyBalancesMock.mockResolvedValueOnce([
      {
        date: '2026-05-01',
        accountName: 'Основной банк',
        source: 'Bank',
        companyId: 1, moneyAccountId: 101, currency: 'RUB', balanceKey: 'bank101',
        openingBalance: 1000,
        income: 500,
        expense: 0,
        closingBalance: 1500,
        incomeCount: 1,
        expenseCount: 0,
        isBalanced: true,
      },
    ]);
    getMoneyBalanceReconciliationResultsMock.mockResolvedValueOnce([
      {
        id: 7,
        date: '2026-05-01',
        accountName: 'Основной банк',
        source: 'Bank',
        companyId: 1, moneyAccountId: 101, currency: 'RUB', balanceKey: 'bank101',
        openingBalance: 1000,
        income: 400,
        expense: 0,
        closingBalance: 1400,
        incomeCount: 1,
        expenseCount: 0,
        isBalanced: true,
        issueCount: 0,
        checkedAt: '2026-05-02T10:00:00Z',
        checkedByUserId: 7,
      },
    ]);

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Изменилось')).toBeTruthy();
    expect(
      screen.getByTitle(
        (title) =>
          title.startsWith('Сохранено 02.05.2026') &&
          title.includes('Изменение сумм: 200,00 ₽'),
      ),
    ).toBeTruthy();
    expect(screen.getByText('Сумма изменилась')).toBeTruthy();
  });

  it.each(['foreign', 'unattributed', 'renamed', 'wrong-company', 'wrong-account', 'wrong-currency'] as const)(
    'matches a saved daily reconciliation by stable identity: %s',
    async (scenario) => {
      const current = {
        date: '2026-05-01', accountName: 'Основной банк', source: 'Bank' as const,
        companyId: 10, moneyAccountId: 20, currency: 'RUB', balanceKey: 'account20:RUB',
        openingBalance: 1000, income: 500, expense: 0, closingBalance: 1500,
        incomeCount: 1, expenseCount: 0, isBalanced: true,
      };
      getMoneyOperationDailyBalancesMock.mockResolvedValueOnce([current]);
      getMoneyBalanceReconciliationResultsMock.mockResolvedValueOnce([{
        ...current, id: 7, income: 400, closingBalance: 1400,
        companyId: scenario === 'wrong-company' ? 11 : current.companyId,
        moneyAccountId: scenario === 'wrong-account' ? 21 : current.moneyAccountId,
        currency: scenario === 'wrong-currency' ? 'USD' : current.currency,
        balanceKey: scenario === 'unattributed' ? null : scenario === 'foreign' ? 'account30:RUB' : current.balanceKey,
        accountName: scenario === 'renamed' ? 'Прежнее название счёта' : current.accountName,
        checkedAt: '2026-05-02T10:00:00Z', issueCount: 0,
      }]);

      render(<MoneyOperations source="Bank" />);
      await screen.findByText('Дневная сверка остатков');
      if (scenario === 'renamed') {
        expect(await screen.findByText('Изменилось')).toBeTruthy();
        expect(screen.getByText('Сумма изменилась')).toBeTruthy();
      } else {
        await waitFor(() => expect(getMoneyBalanceReconciliationResultsMock).toHaveBeenCalled());
        expect(screen.queryByText('Изменилось')).toBeNull();
        expect(screen.queryByText('Сумма изменилась')).toBeNull();
      }
    },
  );

  it('keeps reconciliation drift notifications separate for different currencies', async () => {
    const base = {
      date: '2026-05-01', accountName: 'Основной банк', source: 'Bank' as const,
      companyId: 10, openingBalance: 1000, income: 500, expense: 0, closingBalance: 1500,
      incomeCount: 1, expenseCount: 0, isBalanced: true,
    };
    const rows = [
      { ...base, moneyAccountId: 20, currency: 'RUB', balanceKey: 'account20:RUB' },
      { ...base, moneyAccountId: 30, currency: 'USD', balanceKey: 'account30:USD' },
    ];
    getMoneyOperationDailyBalancesMock.mockResolvedValueOnce(rows);
    getMoneyBalanceReconciliationResultsMock.mockResolvedValueOnce(rows.map((row, index) => ({
      ...row, id: index + 1, income: index === 0 ? 400 : 100, closingBalance: index === 0 ? 1400 : 1100,
      checkedAt: '2026-05-02T10:00:00Z', issueCount: 0,
    })));

    render(<MoneyOperations source="Bank" />);
    expect(await screen.findAllByText('Сумма изменилась')).toHaveLength(2);
    expect(screen.getByText('1 дн. требуют повторной сверки. Разница: 200,00 ₽.')).toBeTruthy();
    expect(screen.getByText('1 дн. требуют повторной сверки. Разница: 800,00 USD.')).toBeTruthy();
    expect(screen.queryByText(/Разница: 1\s?000,00/)).toBeNull();
  });

  it('checks the ruble cash limit only for identified ruble balances and warns about unchecked rows', async () => {
    getUserSettingMock.mockImplementation(async (key: string) => {
      if (key !== 'money.cash.limit') throw new Error('not found');
      return { key, value: JSON.stringify({ enabled: true, amount: 100 }), updatedAt: '2026-05-01T00:00:00Z' };
    });
    const base = { date: '2026-05-01', source: 'Cash', openingBalance: 0, expense: 0, incomeCount: 1, expenseCount: 0, isBalanced: true };
    getMoneyOperationDailyBalancesMock.mockResolvedValue([
      { ...base, companyId: 1, moneyAccountId: 103, accountName: 'Рубли', currency: ' rub ', balanceKey: 'cash103', income: 120, closingBalance: 120 },
      { ...base, companyId: 1, moneyAccountId: 104, accountName: 'На границе', currency: 'RUB', balanceKey: 'cash104', income: 100, closingBalance: 100 },
      { ...base, companyId: 1, moneyAccountId: 105, accountName: 'Доллары', currency: 'USD', income: 900, closingBalance: 900 },
      { ...base, companyId: 1, moneyAccountId: 106, accountName: 'Валюта неизвестна', currency: null, income: 500, closingBalance: 500 },
      { ...base, companyId: null, moneyAccountId: null, accountName: 'Касса неизвестна', currency: 'RUB', income: 700, closingBalance: 700 },
    ]);
    render(<MoneyOperations source="Cash" />);
    expect(await screen.findByText('Превышений за период: 1')).toBeTruthy();
    expect(screen.getByText('Не проверено строк: 3. Лимит применяется только к рублёвым остаткам с подтверждённой кассой и компанией.')).toBeTruthy();
    const cashLimitTable = screen.getByRole('columnheader', { name: 'Превышение' }).closest('table')!;
    expect(within(cashLimitTable).getByText('Рубли')).toBeTruthy();
    expect(within(cashLimitTable).queryByText('Доллары')).toBeNull();
    expect(within(cashLimitTable).queryByText('Касса неизвестна')).toBeNull();
  });

  it.each([
    { name: 'only an unconfirmed currency', rows: [{ currency: null, companyId: 1, moneyAccountId: 103 }], message: 'Нет подтверждённых рублёвых остатков для проверки.', warning: true },
    { name: 'no balances', rows: [], message: 'Нет подтверждённых рублёвых остатков для проверки.', warning: false },
    { name: 'an identified balance at the limit', rows: [{ currency: 'RUB', companyId: 1, moneyAccountId: 103, balanceKey: 'cash103' }], message: 'Превышений за период нет.', warning: false },
  ])('reports cash limit coverage with $name', async ({ rows, message, warning }) => {
    getUserSettingMock.mockImplementation(async (key: string) => {
      if (key !== 'money.cash.limit') throw new Error('not found');
      return { key, value: JSON.stringify({ enabled: true, amount: 100 }), updatedAt: '2026-05-01T00:00:00Z' };
    });
    getMoneyOperationDailyBalancesMock.mockResolvedValue(rows.map((row) => ({
      ...row, date: '2026-05-01', source: 'Cash', accountName: 'Касса', openingBalance: 0,
      income: 100, expense: 0, closingBalance: 100, incomeCount: 1, expenseCount: 0, isBalanced: true,
    })));
    render(<MoneyOperations source="Cash" />);
    expect(await screen.findByText(message)).toBeTruthy();
    expect(Boolean(screen.queryByText(/Не проверено строк:/))).toBe(warning);
  });

  it.each(['own', 'none'])('does not present an unavailable employee balance as a checked cash limit: %s', async (visibility) => {
    permissionsState.paymentsViewScope = visibility === 'own' ? 'own' : 'all';
    permissionsState.paymentsCanView = visibility !== 'none';
    getUserSettingMock.mockImplementation(async (key: string) => {
      if (key !== 'money.cash.limit') throw new Error('not found');
      return { key, value: JSON.stringify({ enabled: true, amount: 100 }), updatedAt: '2026-05-01T00:00:00Z' };
    });
    getMoneyOperationDailyBalancesMock.mockResolvedValue([{
      date: '2026-05-01', source: 'Cash', accountName: 'Касса', companyId: 1, moneyAccountId: 103,
      currency: 'RUB', openingBalance: 0, income: 150, expense: 0, closingBalance: 150,
      incomeCount: 1, expenseCount: 0, isBalanced: true,
    }]);
    render(<MoneyOperations source="Cash" />);
    expect(await screen.findByText('Проверка лимита недоступна: у вас ограничен доступ к платежам.')).toBeTruthy();
    expect(screen.queryByText(/Превышений за период/)).toBeNull();
    expect(screen.queryByRole('columnheader', { name: 'Превышение' })).toBeNull();
  });

  it('distinguishes a failed balance request from a cash limit with no violations', async () => {
    getUserSettingMock.mockImplementation(async (key: string) => {
      if (key !== 'money.cash.limit') throw new Error('not found');
      return { key, value: JSON.stringify({ enabled: true, amount: 100 }), updatedAt: '2026-05-01T00:00:00Z' };
    });
    getMoneyOperationDailyBalancesMock.mockRejectedValue(new Error('offline'));
    render(<MoneyOperations source="Cash" />);
    expect(await screen.findByText('Не удалось загрузить остатки для проверки лимита.')).toBeTruthy();
    expect(screen.queryByText(/Превышений за период/)).toBeNull();
  });

  it('does not claim the cash limit has been checked while balances are loading', async () => {
    getUserSettingMock.mockImplementation(async (key: string) => {
      if (key !== 'money.cash.limit') throw new Error('not found');
      return { key, value: JSON.stringify({ enabled: true, amount: 100 }), updatedAt: '2026-05-01T00:00:00Z' };
    });
    let finish!: (value: unknown[]) => void;
    getMoneyOperationDailyBalancesMock.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    render(<MoneyOperations source="Cash" />);
    expect(await screen.findByText('Загрузка остатков для проверки лимита…')).toBeTruthy();
    expect(screen.queryByText(/Превышений за период/)).toBeNull();
    await act(async () => finish([]));
    expect(await screen.findByText('Нет подтверждённых рублёвых остатков для проверки.')).toBeTruthy();
  });

  it('shows reconciliation notifications for bank mismatches', async () => {
    operations = [
      createOperation({
        id: 1,
        amount: 500,
        accountName: '',
        moneyAccountName: null,
        purpose: '',
        paymentOrderNumber: '101',
      }),
      createOperation({
        id: 2,
        amount: 500,
        accountName: '',
        moneyAccountName: null,
        purpose: '',
        paymentOrderNumber: '101',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Уведомления сверки')).toBeTruthy();
    expect(screen.getByText('Неизвестный счет')).toBeTruthy();
    expect(screen.getByText('Пустое назначение платежа')).toBeTruthy();
    expect(screen.getByText('Дубликаты операций')).toBeTruthy();
  });

  it('labels icon-only journal controls', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByRole('button', { name: 'Журнал денег' })).toBeTruthy();

    const settingsButton = screen.getByRole('button', { name: 'Показать настройки таблицы' });
    const searchButton = screen.getByRole('button', { name: 'Показать расширенный поиск' });

    await user.click(settingsButton);
    expect(screen.getByRole('button', { name: 'Скрыть настройки таблицы' })).toBeTruthy();

    await user.click(searchButton);
    expect(screen.getByRole('button', { name: 'Скрыть расширенный поиск' })).toBeTruthy();
  }, 15000);

  it('hides journal export and print menu when payment export permission is denied', async () => {
    permissionsState.paymentsCanExport = false;

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');

    expect(screen.queryByRole('button', { name: 'Экспорт и печать' })).toBeNull();
  }, 15000);

  it('refreshes the money journal from the side panel', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole('button', { name: 'Обновить' }));

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(2));
  }, 15000);

  it('opens the filtered money journal print form from the side panel', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');
    await clickJournalAction(user, 'Печать');

    await waitFor(() =>
      expect(getMoneyOperationsPrintHtmlMock).toHaveBeenCalledWith(
        expect.objectContaining({
          from: '2026-01-01',
          to: '2026-12-31',
          source: 'Bank',
        }),
      ),
    );
    expect(URL.createObjectURL).toHaveBeenCalled();
    expect(window.open).toHaveBeenCalledWith('blob:cash-order', '_blank', 'noopener,noreferrer');
  }, 15000);

  it('exports the filtered money journal PDF from the side panel', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalled());
    await clickJournalAction(user, 'PDF');

    await waitFor(() =>
      expect(exportMoneyOperationsPdfMock).toHaveBeenCalledWith(
        expect.objectContaining({
          from: '2026-01-01',
          to: '2026-12-31',
          source: 'Bank',
        }),
      ),
    );
    expect(URL.createObjectURL).toHaveBeenCalled();
    expect(toastSuccessMock).toHaveBeenCalledWith('Журнал экспортирован в PDF.', 'Журнал денег');
  }, 15000);

  it('exports the filtered money journal Excel file from the side panel', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalled());
    await clickJournalAction(user, 'Excel');

    await waitFor(() =>
      expect(exportMoneyOperationsExcelMock).toHaveBeenCalledWith(
        expect.objectContaining({
          from: '2026-01-01',
          to: '2026-12-31',
          source: 'Bank',
        }),
      ),
    );
    expect(URL.createObjectURL).toHaveBeenCalled();
    expect(toastSuccessMock).toHaveBeenCalledWith('Журнал экспортирован в Excel.', 'Журнал денег');
  }, 15000);

  it('applies the money journal search immediately from the find button', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(1));

    await user.type(screen.getByPlaceholderText('Поиск по контрагенту...'), 'Ахмедова');
    await user.click(screen.getByRole('button', { name: 'Найти' }));

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ search: 'Ахмедова' }),
        expectAbortOptions(),
      ),
    );
  }, 15000);

  it('shows balance reconciliation for the journal and selected account', async () => {
    operations = [
      createOperation({
        id: 1,
        direction: 'Income',
        amount: 1000,
        accountName: 'Основной счет',
        counterpartyName: 'Клиент',
      }),
      createOperation({
        id: 2,
        direction: 'Expense',
        amount: 250,
        accountName: 'Основной счет',
        counterpartyName: 'Поставщик',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    const badges = await screen.findAllByText('Сверка сходится');
    expect(badges.length).toBeGreaterThan(0);
    expect(screen.getByText('Сверка остатков')).toBeTruthy();
  });

  it('requests historical accounts and filters the journal by their stable ID', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 301, companyId: 1, companyName: 'Компания Альфа', name: 'Закрытый счёт', source: 'Bank', currency: 'RUB',
        isActive: false, isDefault: false, canUseForNewOperations: false, canEditExistingOperations: false },
    ]);
    operations = [createOperation({ moneyAccountId: 301, accountName: 'Закрытый счёт' })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    expect(getMoneyAccountsMock).toHaveBeenCalledWith({ activeOnly: false });
    await user.selectOptions(screen.getByLabelText('Банковский счёт в заголовке'), '301');
    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ moneyAccountId: 301 }), expectAbortOptions(),
    ));
  });

  it('keeps server-blocked active accounts out of new operation choices', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Компания Альфа', name: 'Закрытая банковская привязка', source: 'Bank', currency: 'RUB',
        isActive: true, isDefault: true, canUseForNewOperations: false, canEditExistingOperations: true },
      { id: 202, companyId: 1, companyName: 'Компания Альфа', name: 'Действующий счёт', source: 'Bank', currency: 'RUB',
        isActive: true, isDefault: false, canUseForNewOperations: true, canEditExistingOperations: true },
    ]);
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');
    const select = screen.getByLabelText('Банковский счёт') as HTMLSelectElement;
    expect(Array.from(select.options, option => option.value)).toEqual(['', '202']);
    expect(select.value).toBe('202');
  });

  it('keeps historical bank account financial fields locked while its description remains editable', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Компания Альфа', name: 'Закрытая банковская привязка', source: 'Bank', currency: 'RUB',
        isActive: true, isDefault: false, canUseForNewOperations: false, canEditExistingOperations: true },
    ]);
    operations = [createOperation({ moneyAccountId: 201, accountName: 'Закрытая банковская привязка', purpose: 'Прежнее описание' })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Прежнее описание');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банку');
    expect(screen.getByLabelText('Сумма, RUB')).toHaveProperty('disabled', true);
    expect(screen.getByLabelText(/^Дата$/)).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('Банковский счёт')).toHaveProperty('value', '201');
    expect(screen.getByLabelText('Банковский счёт')).toHaveProperty('disabled', true);
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Уточнённое описание');
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(1, expect.objectContaining({
      moneyAccountId: 201, amount: 1500, date: '2026-04-20', purpose: 'Уточнённое описание',
    }));
  });

  it('shows inactive account history without allowing it to be reassigned through editing', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Компания Альфа', name: 'Отключённый счёт', source: 'Bank', currency: 'RUB',
        isActive: false, isDefault: false, canUseForNewOperations: false, canEditExistingOperations: false },
      { id: 202, companyId: 1, companyName: 'Компания Альфа', name: 'Действующий счёт', source: 'Bank', currency: 'RUB',
        isActive: true, isDefault: true, canUseForNewOperations: true, canEditExistingOperations: true },
    ]);
    operations = [createOperation({ moneyAccountId: 201, accountName: 'Отключённый счёт' })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банку');
    expect(screen.getByLabelText('Банковский счёт')).toHaveProperty('value', '201');
    expect(getLastButton('Сохранить')).toHaveProperty('disabled', true);
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
  });

  it('requires an explicit account when several companies have their own default', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Компания Альфа', name: 'Основной счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 2, companyName: 'Компания Бета', name: 'Основной счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
    ]);
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');
    const select = screen.getByLabelText('Банковский счёт') as HTMLSelectElement;
    expect(select.value).toBe('');
    expect(Array.from(select.options, option => [option.value, option.textContent])).toEqual([
      ['', 'Выберите денежный счёт'],
      ['201', 'Основной счёт · Компания Альфа'],
      ['202', 'Основной счёт · Компания Бета'],
    ]);
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it('uses the explicitly selected journal account instead of another company default', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Компания Альфа', name: 'Основной счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 2, companyName: 'Компания Бета', name: 'Основной счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
    ]);
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    await user.selectOptions(screen.getByLabelText('Банковский счёт в заголовке'), '202');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');
    expect((screen.getByLabelText('Банковский счёт') as HTMLSelectElement).value).toBe('202');
  });

  it('keeps an unsaved operation when the account directory arrives late', async () => {
    const user = userEvent.setup();
    let resolveAccounts!: (items: MoneyAccount[]) => void;
    getMoneyAccountsMock.mockReturnValue(new Promise<MoneyAccount[]>(resolve => { resolveAccounts = resolve; }));
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');
    await user.clear(screen.getByLabelText('Сумма, RUB'));
    await user.type(screen.getByLabelText('Сумма, RUB'), '123');
    await user.type(screen.getByTestId('money-operation-counterparty'), 'Несохранённый контрагент');
    await act(async () => resolveAccounts([
      { id: 201, companyId: 1, companyName: 'Компания Альфа', name: 'Основной счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true, createdAt: '2026-10-01T00:00:00Z' },
    ]));
    await waitFor(() => expect(screen.getByLabelText('Банковский счёт').tagName).toBe('SELECT'));
    expect((screen.getByLabelText('Сумма, RUB') as HTMLInputElement).value).toBe('123');
    expect((screen.getByTestId('money-operation-counterparty') as HTMLInputElement).value).toBe('Несохранённый контрагент');
    expect((screen.getByLabelText('Банковский счёт') as HTMLSelectElement).value).toBe('');
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it.each([false, true])('requires a known company account when the directory is empty or historical: %s', async legacy => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue(legacy ? [
      { id: 999, companyId: null, name: 'Исторический счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true, createdAt: '2026-10-01T00:00:00Z' },
    ] : []);
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');
    expect(screen.queryByLabelText('Банковский счёт', { selector: 'input' })).toBeNull();
    expect(screen.getByLabelText('Банковский счёт')).toHaveProperty('disabled', true);
    expect(screen.getByText('Нет доступных действующих счетов собственной компании. Обратитесь к администратору.')).toBeTruthy();
    await user.click(screen.getByLabelText('Расход'));
    await user.clear(screen.getByLabelText('Сумма, RUB'));
    await user.type(screen.getByLabelText('Сумма, RUB'), '10');
    const save = screen.getAllByRole('button', { name: 'Сохранить' }).find(button => button.className.includes('bg-blue-600'));
    await user.click(save as HTMLElement);
    expect(await screen.findByText('Выберите денежный счёт своей компании.')).toBeTruthy();
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it('uses the local current date when creating a bank operation and saves it', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');
    await waitFor(() => expect(getIncomeTypesMock).toHaveBeenCalledWith('Income', true));
    await waitFor(() => expect(getIncomeTypesMock).toHaveBeenCalledWith('Expense', true));

    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');
    const categoryInput = screen
      .getAllByLabelText(/Статья/)
      .find((element) => element.getAttribute('list') === 'money-operation-category-options-Bank');
    expect(categoryInput).toBeTruthy();

    expect(screen.getByLabelText(/^Дата$/)).toHaveProperty('value', '2026-04-21');
    await user.click(screen.getByLabelText('Расход'));
    expect(document.querySelector('datalist#money-operation-category-options-Bank option[value="Хозрасходы"]')).toBeTruthy();

    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '102');
    // A real paste event fills each text field with one controlled-form update.
    await user.clear(screen.getByLabelText('Поставщик'));
    await user.paste('Новый поставщик');
    await user.selectOptions(screen.getByLabelText('Ответственный'), '12');
    await user.clear(categoryInput as HTMLElement);
    await user.paste('Хозрасходы');
    await user.clear(screen.getByLabelText('Сумма, RUB'));
    await user.type(screen.getByLabelText('Сумма, RUB'), '1500');
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.paste('Оплата по договору');

    const modalSaveButton = screen
      .getAllByRole('button', { name: 'Сохранить' })
      .find((button) => button.className.includes('bg-blue-600'));
    expect(modalSaveButton).toBeTruthy();
    await user.click(modalSaveButton as HTMLElement);

    await waitFor(() => expect(createMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(createMoneyOperationMock).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 0,
        source: 'Bank',
        date: '2026-04-21',
        moneyAccountId: 102,
        accountName: 'Резервный счёт',
        counterpartyName: 'Новый поставщик',
        responsibleUserId: 12,
        responsibleName: 'Анна Иванова',
        incomeTypeId: 2,
        categoryName: null,
        amount: 1500,
        purpose: 'Оплата по договору',
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция создана.', 'Деньги');
    await screen.findByText('Новый поставщик');
  }, 15000);

  it('does not create a money operation with a zero amount or without an account', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');

    const modalSaveButton = screen
      .getAllByRole('button', { name: 'Сохранить' })
      .find((button) => button.className.includes('bg-blue-600'));
    expect(modalSaveButton).toBeTruthy();

    await user.click(modalSaveButton as HTMLElement);
    expect(await screen.findByText('Сумма денежной операции должна быть больше нуля.')).toBeTruthy();
    expect(createMoneyOperationMock).not.toHaveBeenCalled();

    await user.clear(screen.getByLabelText('Сумма, RUB'));
    await user.type(screen.getByLabelText('Сумма, RUB'), '1000');
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '');
    await user.click(modalSaveButton as HTMLElement);

    expect(await screen.findByText('Выберите денежный счёт своей компании.')).toBeTruthy();
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it('warns and marks a bank income without invoice as an advance', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');

    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '101');
    await user.clear(screen.getByTestId('money-operation-counterparty'));
    await user.type(screen.getByTestId('money-operation-counterparty'), 'Винокуров Валерий Витальевич');
    await user.clear(screen.getByLabelText('Сумма, RUB'));
    await user.type(screen.getByLabelText('Сумма, RUB'), '7100');
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Поступление без счета');

    const modalSaveButton = screen
      .getAllByRole('button', { name: 'Сохранить' })
      .find((button) => button.className.includes('bg-blue-600'));
    expect(modalSaveButton).toBeTruthy();
    await user.click(modalSaveButton as HTMLElement);

    expect(await screen.findByText('Нераспределенный аванс')).toBeTruthy();
    expect(createMoneyOperationMock).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Отметить как аванс' }));

    await waitFor(() => expect(createMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(createMoneyOperationMock).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 0,
        source: 'Bank',
        direction: 'Income',
        moneyAccountId: 101,
        accountName: 'Основной счёт',
        counterpartyName: 'Винокуров Валерий Витальевич',
        amount: 7100,
        isAdvancePayment: true,
        matchStatus: 'ClarificationRequired',
        matchComment: expect.stringContaining('не связано со счетом'),
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция создана.', 'Деньги');
    await screen.findByText('Аванс без счета');
  }, 15000);

  it('shows an advance without invoice marker in the money journal', async () => {
    operations = [
      createOperation({
        id: 51,
        direction: 'Income',
        amount: 7100,
        counterpartyName: 'Винокуров Валерий Витальевич',
        isAdvancePayment: true,
        linkedInvoiceId: null,
        matchStatus: 'ClarificationRequired',
        matchComment: 'Поступление не связано со счетом.',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    expect(await screen.findByText('Винокуров Валерий Витальевич')).toBeTruthy();
    expect(screen.getByText('Аванс без счета')).toBeTruthy();
  });

  it('creates an accountable advance from the money operation form', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Cash" />);

    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalled());
    await waitFor(() => expect(getIncomeTypesMock).toHaveBeenCalledWith('Expense', true));

    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по кассе');

    await user.click(screen.getByTestId('money-accountable-advance-preset'));
    expect(screen.getByTestId('money-advance-checkbox')).toHaveProperty('checked', true);

    await user.clear(screen.getByTestId('money-operation-counterparty'));
    await user.type(screen.getByTestId('money-operation-counterparty'), 'Анна Иванова');
    await user.selectOptions(screen.getByLabelText('Ответственный'), '12');
    await user.clear(screen.getByLabelText('Сумма, RUB'));
    await user.type(screen.getByLabelText('Сумма, RUB'), '5000');

    const modalSaveButton = screen
      .getAllByRole('button', { name: 'Сохранить' })
      .find((button) => button.className.includes('bg-blue-600'));
    expect(modalSaveButton).toBeTruthy();
    await user.click(modalSaveButton as HTMLElement);

    await waitFor(() => expect(createMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(createMoneyOperationMock).toHaveBeenCalledWith(
      expect.objectContaining({
        id: 0,
        source: 'Cash',
        direction: 'Expense',
        date: '2026-04-21',
        moneyAccountId: 103,
        accountName: 'Основная касса',
        counterpartyName: 'Анна Иванова',
        responsibleUserId: 12,
        responsibleName: 'Анна Иванова',
        isAdvancePayment: true,
        categoryName: 'Подотчетные суммы',
        purpose: 'Выдача денежных средств под отчет',
        linkedInvoiceId: null,
        paidInvoiceNumber: '',
        paidAmount: 0,
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция создана.', 'Деньги');
  }, 15000);

  it('does not create a cash inventory while the actual balance is blank', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Cash" />);

    await screen.findByRole('heading', { name: 'Инвентаризация кассы' });
    await user.click(screen.getByRole('button', { name: 'Провести' }));

    expect(createCashInventoryMock).not.toHaveBeenCalled();
    expect(toastErrorMock).toHaveBeenCalledWith('Укажите фактический остаток кассы.', 'Касса');
  });

  it('sends the selected cash account ID with an inventory request', async () => {
    const user = userEvent.setup();
    render(<MoneyOperations source="Cash" />);
    await screen.findByRole('heading', { name: 'Инвентаризация кассы' });
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Касса'), '103');
    await user.type(screen.getByLabelText('Фактический остаток'), '1200');
    await user.click(screen.getByRole('button', { name: 'Провести' }));
    await waitFor(() => expect(createCashInventoryMock).toHaveBeenCalledWith(expect.objectContaining({
      accountName: 'Основная касса', moneyAccountId: 103, actualBalance: 1200,
    })));
  });

  it('keeps inventory input after a concurrency conflict and allows a deliberate retry', async () => {
    const user = userEvent.setup();
    const message = 'Касса или её остаток изменились одновременно с инвентаризацией. Обновите данные и повторите действие.';
    createCashInventoryMock.mockRejectedValueOnce(new Error(message));
    render(<MoneyOperations source="Cash" />);
    await screen.findByRole('heading', { name: 'Инвентаризация кассы' });
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Касса'), '103');
    await user.type(screen.getByLabelText('Фактический остаток'), '1200');
    await user.click(screen.getByRole('button', { name: 'Провести' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(message, 'Касса'));
    expect((screen.getByLabelText('Фактический остаток') as HTMLInputElement).value).toBe('1200');
    expect(toastSuccessMock).not.toHaveBeenCalledWith('Инвентаризация кассы сохранена.', 'Касса');
    await user.click(screen.getByRole('button', { name: 'Провести' }));
    await waitFor(() => expect(createCashInventoryMock).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith('Инвентаризация кассы сохранена.', 'Касса'));
  });

  it('does not create an inventory for a historical cash name without an account ID', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([]);
    getMoneyOperationAccountSummariesMock.mockResolvedValue([{ accountName: 'Историческая касса', source: 'Cash', moneyAccountId: null }]);
    render(<MoneyOperations source="Cash" />);
    await screen.findByRole('heading', { name: 'Инвентаризация кассы' });
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Касса'), 'Историческая касса');
    await user.type(screen.getByLabelText('Фактический остаток'), '1200');
    await user.click(screen.getByRole('button', { name: 'Провести' }));
    expect(createCashInventoryMock).not.toHaveBeenCalled();
    expect(toastErrorMock).toHaveBeenCalledWith('Выберите доступную кассу перед инвентаризацией.', 'Касса');
  });

  it('does not present the money journal balance as the cash inventory balance', async () => {
    operations = [createOperation({ id: 1, source: 'Cash', direction: 'Income', amount: 987, currency: 'RUB' })];
    render(<MoneyOperations source="Cash" />);
    const heading = await screen.findByRole('heading', { name: 'Инвентаризация кассы' });
    const inventory = heading.closest('div.rounded-xl');
    expect(inventory).not.toBeNull();
    expect(within(inventory as HTMLElement).getByText('Рассчитывается по проведённым ордерам при сохранении')).toBeTruthy();
    expect(within(inventory as HTMLElement).queryByText('987,00 ₽')).toBeNull();
  });

  it('does not claim inventory history is empty when it cannot be loaded', async () => {
    getCashInventoriesMock.mockRejectedValue(new Error('Forbidden'));
    render(<MoneyOperations source="Cash" />);
    expect(await screen.findByText('Не удалось загрузить историю инвентаризаций. Проверьте доступ или повторите позже.')).toBeTruthy();
    expect(screen.queryByText('Инвентаризации за период еще не проводились.')).toBeNull();
  });

  it('requests inventory history by selected cash account ID, even when names match', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 301, companyId: 1, companyName: 'Альфа', name: 'Касса', source: 'Cash', currency: 'RUB', isActive: true, isDefault: false },
      { id: 302, companyId: 2, companyName: 'Бета', name: 'Касса', source: 'Cash', currency: 'RUB', isActive: true, isDefault: false },
    ]);
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Кассовые ордера');
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '301');
    await waitFor(() => expect(getCashInventoriesMock).toHaveBeenLastCalledWith(expect.objectContaining({ moneyAccountId: 301, accountName: 'Касса' })));
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '302');
    await waitFor(() => expect(getCashInventoriesMock).toHaveBeenLastCalledWith(expect.objectContaining({ moneyAccountId: 302, accountName: 'Касса' })));
  });

  it('does not show an old server inventory history as belonging to the selected cash account', async () => {
    const user = userEvent.setup();
    getCashInventoriesMock.mockResolvedValue({ items: [{ id: 9, number: 'ИНВ-ЧУЖАЯ', date: '2026-10-28',
      accountName: 'Основная касса', accountingBalance: 900, actualBalance: 900, difference: 0 }], total: 1, page: 1, pageSize: 8 });
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Кассовые ордера');
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '103');
    expect(await screen.findByText('Не удалось загрузить историю инвентаризаций. Проверьте доступ или повторите позже.')).toBeTruthy();
    expect(screen.queryByText('ИНВ-ЧУЖАЯ')).toBeNull();
    expect(screen.queryByText('Инвентаризации за период еще не проводились.')).toBeNull();
  });

  it('keeps the confirmed second cash history when the first request returns late', async () => {
    const user = userEvent.setup();
    let finishFirst!: (value: unknown) => void;
    getMoneyAccountsMock.mockResolvedValue([
      { id: 301, companyId: 1, companyName: 'Альфа', name: 'Касса', source: 'Cash', currency: 'RUB', isActive: true, isDefault: false },
      { id: 302, companyId: 2, companyName: 'Бета', name: 'Касса', source: 'Cash', currency: 'RUB', isActive: true, isDefault: false },
    ]);
    getCashInventoriesMock.mockImplementation((params: { moneyAccountId?: number }) => {
      if (params.moneyAccountId === 301) return new Promise(resolve => { finishFirst = resolve; });
      const items = params.moneyAccountId === 302 ? [{ id: 2, number: 'ИНВ-БЕТА', date: '2026-10-28',
        accountName: 'Касса', accountingBalance: 200, actualBalance: 200, difference: 0 }] : [];
      return Promise.resolve({ items, total: items.length, page: 1, pageSize: 8, moneyAccountId: params.moneyAccountId });
    });
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Кассовые ордера');
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '301');
    await waitFor(() => expect(finishFirst).toBeDefined());
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '302');
    expect(await screen.findByText('ИНВ-БЕТА')).toBeTruthy();
    await act(async () => finishFirst({ moneyAccountId: 301, items: [{ id: 1, number: 'ИНВ-АЛЬФА', date: '2026-10-28',
      accountName: 'Касса', accountingBalance: 100, actualBalance: 100, difference: 0 }], total: 1, page: 1, pageSize: 8 }));
    expect(screen.getByText('ИНВ-БЕТА')).toBeTruthy();
    expect(screen.queryByText('ИНВ-АЛЬФА')).toBeNull();
  });

  it('refuses cash journal and printing when an older server does not confirm the selected ID', async () => {
    const user = userEvent.setup();
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Кассовые ордера');
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '103');
    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(expect.objectContaining({ moneyAccountId: 103 }), expectAbortOptions()));
    await waitFor(() => expect(getCashBookMock).toHaveBeenLastCalledWith(expect.objectContaining({ moneyAccountId: 103 }), expectAbortOptions()));
    expect((await screen.findAllByText('Обновите сервер для просмотра выбранной кассы.')).length).toBeGreaterThan(0);
    expect((screen.getByRole('button', { name: 'Кассовая книга' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: /^Журнал$/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('loads and prints the cash journal for the confirmed selected account ID', async () => {
    const user = userEvent.setup();
    getCashOrderRegisterPrintHtmlMock.mockResolvedValue('<html><head><meta name="payplanner-account-id" content="103"></head><body>Журнал</body></html>');
    getCashOrderRegisterMock.mockImplementation(async (params: { moneyAccountId?: number }) => ({
      moneyAccountId: params.moneyAccountId, items: [], total: 0, page: 1, pageSize: 10,
      incomingCount: 0, outgoingCount: 0, draftCount: 0, postedCount: 0, cancelledCount: 0,
      amountsAvailable: true, currencyStatus: 'empty', currency: null, incomingAmount: 0, outgoingAmount: 0,
    }));
    getCashBookMock.mockImplementation(async (params: { moneyAccountId?: number }) => ({
      moneyAccountId: params.moneyAccountId, from: '2026-04-01', to: '2026-04-30', currencyStatus: 'single', currency: 'RUB',
      openingBalance: 0, income: 0, expense: 0, closingBalance: 0, incomingCount: 0, outgoingCount: 0, days: [],
    }));
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Кассовые ордера');
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '103');
    await waitFor(() => expect((screen.getByRole('button', { name: /^Журнал$/ }) as HTMLButtonElement).disabled).toBe(false));
    expect(screen.getByText(/Показаны только ордера с подтверждённой принадлежностью/)).toBeTruthy();
    await user.click(screen.getByRole('button', { name: /^Журнал$/ }));
    await waitFor(() => expect(getCashOrderRegisterPrintHtmlMock).toHaveBeenCalledWith(expect.objectContaining({ moneyAccountId: 103 })));
  });

  it('rejects an unconfirmed print response even after the selected account was loaded', async () => {
    const user = userEvent.setup();
    getCashOrderRegisterMock.mockImplementation(async (params: { moneyAccountId?: number }) => ({
      moneyAccountId: params.moneyAccountId, items: [], total: 0, page: 1, pageSize: 10,
      incomingCount: 0, outgoingCount: 0, draftCount: 0, postedCount: 0, cancelledCount: 0,
      amountsAvailable: true, currencyStatus: 'empty', currency: null, incomingAmount: 0, outgoingAmount: 0,
    }));
    getCashOrderRegisterPrintHtmlMock.mockResolvedValue('<html><body>Ответ старого сервера</body></html>');
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Кассовые ордера');
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), '103');
    await waitFor(() => expect((screen.getByRole('button', { name: /^Журнал$/ }) as HTMLButtonElement).disabled).toBe(false));
    await user.click(screen.getByRole('button', { name: /^Журнал$/ }));
    expect(await screen.findByText('Сервер не подтвердил выбранную кассу для печати.')).toBeTruthy();
  });

  it('keeps an unknown cash order currency empty when editing', async () => {
    const user = userEvent.setup();
    const order = { ...(await getCashOrderMock(9)), currency: null };
    getCashOrderMock.mockResolvedValueOnce(order);
    getCashOrderRegisterMock.mockResolvedValueOnce({ items: [order], total: 1, page: 1, pageSize: 10,
      incomingCount: 1, outgoingCount: 0, draftCount: 0, postedCount: 1, cancelledCount: 0,
      incomingAmount: 25000, outgoingAmount: 0 });
    render(<MoneyOperations source="Cash" />);
    await user.click(await screen.findByRole('button', { name: order.number }));
    const currency = await screen.findByLabelText('Валюта') as HTMLInputElement;
    expect(currency.value).toBe('');
    expect(currency.disabled).toBe(false);
    expect(screen.getAllByText(/валюта не указана/).length).toBeGreaterThan(0);
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateCashOrderMock).toHaveBeenCalledWith(9, expect.objectContaining({ currency: '' })));
  });

  it('shows the linked cash account and currency as fixed while keeping notes editable', async () => {
    const user = userEvent.setup();
    const order = { ...(await getCashOrderMock(9)), hasLinkedCashAccount: true, currency: 'RUB', account: 'Основная касса' };
    getCashOrderMock.mockResolvedValueOnce(order);
    getCashOrderRegisterMock.mockResolvedValueOnce({ items: [order], total: 1, page: 1, pageSize: 10,
      incomingCount: 1, outgoingCount: 0, draftCount: 0, postedCount: 1, cancelledCount: 0,
      incomingAmount: 25000, outgoingAmount: 0 });
    render(<MoneyOperations source="Cash" />);
    await user.click(await screen.findByRole('button', { name: order.number }));
    expect((await screen.findByLabelText('Валюта') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('Счёт / касса') as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText('Касса и валюта закреплены за ордером.')).toBeTruthy();
    await user.type(screen.getAllByLabelText('Комментарий').find(element => element.tagName === 'TEXTAREA')!, ' Проверка');
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateCashOrderMock).toHaveBeenCalledWith(9, expect.objectContaining({
      account: 'Основная касса', currency: 'RUB', comment: expect.stringContaining('Проверка'),
    })));
  });

  it('creates a paired transfer between money accounts from the operation form', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Добавить операцию по банку');

    await user.click(screen.getByLabelText('Между счетами'));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '101');
    await user.selectOptions(screen.getByLabelText('Куда перевести'), '103');
    await user.clear(screen.getByLabelText('Получатель'));
    await user.type(screen.getByLabelText('Получатель'), 'Инкассация');
    await user.clear(screen.getByLabelText('Сумма, RUB'));
    await user.type(screen.getByLabelText('Сумма, RUB'), '12000');
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Инкассация в кассу');

    const modalSaveButton = screen
      .getAllByRole('button', { name: 'Сохранить' })
      .find((button) => button.className.includes('bg-blue-600'));
    expect(modalSaveButton).toBeTruthy();
    await user.click(modalSaveButton as HTMLElement);

    await waitFor(() => expect(createMoneyOperationTransferMock).toHaveBeenCalledTimes(1));
    expect(createMoneyOperationTransferMock).toHaveBeenCalledWith(
      expect.objectContaining({
        sourceMoneyAccountId: 101,
        targetMoneyAccountId: 103,
        amount: 12000,
        date: '2026-04-21',
        purpose: 'Инкассация в кассу',
      }),
    );
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
    expect(toastSuccessMock).toHaveBeenCalledWith('Перевод между счетами создан.', 'Деньги');
  }, 15000);

  it('filters money operations by selected account', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({ id: 1, moneyAccountId: 101, moneyAccountName: 'Основной счёт', accountName: 'Основной счёт', counterpartyName: 'Поставщик А' }),
      createOperation({ id: 2, moneyAccountId: 102, moneyAccountName: 'Резервный счёт', accountName: 'Резервный счёт', counterpartyName: 'Поставщик Б' }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');
    expect(screen.getByText('Поставщик Б')).toBeTruthy();

    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '102');

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ moneyAccountId: 102, accountName: 'Резервный счёт' }),
        expectAbortOptions(),
      ),
    );
    expect(await screen.findByText('Поставщик Б')).toBeTruthy();
    expect(screen.getByText('Расход Факт').parentElement?.textContent).toContain('1 500,00 ₽');
    expect(screen.queryByText('Поставщик А')).toBeNull();

    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '');

    expect(await screen.findByText('Поставщик А')).toBeTruthy();
    expect(screen.getByText('Поставщик Б')).toBeTruthy();
  });

  it('shows total turnover cards across all money sources', async () => {
    operations = [
      createOperation({
        id: 1,
        source: 'Bank',
        direction: 'Income',
        amount: 25000,
        accountName: 'Основной банк',
        counterpartyName: 'Клиент',
      }),
      createOperation({
        id: 2,
        source: 'Cash',
        direction: 'Expense',
        amount: 5000,
        accountName: 'Касса',
        counterpartyName: 'Поставщик',
      }),
      createOperation({
        id: 3,
        source: 'EMoney',
        direction: 'Income',
        amount: 3000,
        accountName: 'ЮMoney',
        counterpartyName: 'Онлайн клиент',
      }),
    ];

    render(<MoneyOperations source="Total" />);

    expect(await screen.findByText('Общий баланс')).toBeTruthy();
    expect(screen.getByText('Приход за период')).toBeTruthy();
    expect(screen.getByText('Расход за период')).toBeTruthy();
    expect(screen.getByText('Электронные деньги')).toBeTruthy();
    expect(document.body.textContent).toContain('25 000,00');
    expect(document.body.textContent).toContain('3 000,00');
    expect(document.body.textContent).toContain('5 000,00');
    expect(document.body.textContent).toContain('23 000,00 ₽');
  });

  it('shows a paged cashflow journal on the total money tab', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 1,
        source: 'Bank',
        direction: 'Income',
        amount: 25000,
        accountName: 'Основной банк',
        counterpartyName: 'Клиент по банку',
        purpose: 'Оплата счета 33',
      }),
      createOperation({
        id: 2,
        source: 'Cash',
        direction: 'Expense',
        amount: 5000,
        accountName: 'Касса',
        counterpartyName: 'Поставщик наличными',
        purpose: 'Расход из кассы',
      }),
    ];

    render(<MoneyOperations source="Total" />);

    await screen.findByText('Общий баланс');
    await user.click(screen.getByRole('button', { name: 'ОДДС' }));

    expect(await screen.findByText('Журнал движений')).toBeTruthy();
    expect(screen.getByText('Клиент по банку')).toBeTruthy();
    expect(screen.getByText('Поставщик наличными')).toBeTruthy();
    expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ source: undefined, page: 1, pageSize: 25 }),
      expectAbortOptions(),
    );

    const journalSelects = Array.from(document.querySelectorAll('select'));
    const sourceSelect = journalSelects.find((select) => Array.from(select.options).some((option) => option.value === 'Cash'));
    const directionSelect = journalSelects.find((select) => Array.from(select.options).some((option) => option.value === 'Income'));
    expect(sourceSelect).toBeTruthy();
    expect(directionSelect).toBeTruthy();
    await user.selectOptions(sourceSelect!, 'Cash');

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ source: 'Cash' }),
        expectAbortOptions(),
      ),
    );

    await user.selectOptions(directionSelect!, 'Income');

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ source: 'Cash', direction: 'Income' }),
        expectAbortOptions(),
      ),
    );
  });

  it('renders the e-money journal with wallet labels', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 1,
        source: 'EMoney',
        moneyAccountId: 301,
        moneyAccountName: 'ЮMoney',
        direction: 'Income',
        amount: 3000,
        accountName: 'ЮMoney',
        counterpartyName: 'Онлайн клиент',
      }),
      createOperation({
        id: 2,
        source: 'EMoney',
        moneyAccountId: 302,
        moneyAccountName: 'CloudPayments',
        direction: 'Income',
        amount: 1500,
        accountName: 'CloudPayments',
        counterpartyName: 'Другой клиент',
      }),
    ];
    getMoneyAccountsMock.mockImplementation(async (params?: { source?: 'Bank' | 'Cash' | 'Card' | 'EMoney' }) => {
      const accounts: MoneyAccount[] = [
        {
          id: 301,
          name: 'ЮMoney',
          source: 'EMoney',
          currency: 'RUB',
          bankName: null,
          bankAccountNumber: null,
          isDefault: true,
          isActive: true,
          createdAt: '2026-04-20T08:00:00Z',
          updatedAt: null,
        },
        {
          id: 302,
          name: 'CloudPayments',
          source: 'EMoney',
          currency: 'RUB',
          bankName: null,
          bankAccountNumber: null,
          isDefault: false,
          isActive: true,
          createdAt: '2026-04-20T08:00:00Z',
          updatedAt: null,
        },
      ];
      return accounts.filter((account) => !params?.source || account.source === params.source);
    });

    render(<MoneyOperations source="EMoney" />);

    expect(await screen.findByText('Электронные деньги')).toBeTruthy();
    expect(screen.getByText('Операции по электронным кошелькам')).toBeTruthy();
    await user.selectOptions(screen.getByLabelText('Кошелёк в заголовке'), '302');
    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ source: 'EMoney', moneyAccountId: 302, accountName: 'CloudPayments' }),
        expectAbortOptions(),
      ),
    );
    expect(await screen.findByText('Другой клиент')).toBeTruthy();
    expect(screen.queryByText('Онлайн клиент')).toBeNull();

    expect(screen.getByText('Все кошельки', { selector: 'option' })).toBeTruthy();
  });

  it('applies direction and accounting filters to the money journal request', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 1,
        direction: 'Income',
        accountingType: 'Fact',
        counterpartyName: 'Клиент факт',
        amount: 9000,
      }),
      createOperation({
        id: 2,
        direction: 'Expense',
        accountingType: 'Fact',
        counterpartyName: 'Расход факт',
        amount: 3000,
      }),
      createOperation({
        id: 3,
        direction: 'Income',
        accountingType: 'Plan',
        counterpartyName: 'Клиент план',
        amount: 7000,
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Клиент факт');
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Приход/Расход'), 'Income');
    await user.selectOptions(screen.getByLabelText('Учет'), 'Fact');
    await user.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ direction: 'Income', accountingType: 'Fact' }),
        expectAbortOptions(),
      ),
    );
    expect(await screen.findByText('Клиент факт')).toBeTruthy();
    expect(screen.queryByText('Расход факт')).toBeNull();
    expect(screen.queryByText('Клиент план')).toBeNull();
    expect(screen.getByText('Активные фильтры')).toBeTruthy();
    expect(screen.getByText('Приход')).toBeTruthy();
    expect(screen.getByText('Факт')).toBeTruthy();

    await user.click(screen.getByRole('button', { name: 'Сбросить фильтр Приход' }));

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ accountingType: 'Fact' }),
        expectAbortOptions(),
      ),
    );
    expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.not.objectContaining({ direction: 'Income' }),
      expectAbortOptions(),
    );
    expect(await screen.findByText('Расход факт')).toBeTruthy();
    expect(screen.queryByText('Клиент план')).toBeNull();

    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.click(screen.getByRole('button', { name: 'Сбросить' }));

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.not.objectContaining({ direction: 'Income', accountingType: 'Fact' }),
        expectAbortOptions(),
      ),
    );
  }, 15000);

  it('shows account summaries only for the selected account identity and each currency', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Альфа', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 2, companyName: 'Бета', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: false },
    ]);
    getMoneyOperationAccountSummariesMock.mockResolvedValue([
      { moneyAccountId: 201, companyId: 1, accountName: 'Общий счёт', source: 'Bank', currencyStatus: 'single', currency: 'RUB', startBalance: 0, income: 100, expense: 0, endBalance: 100, incomeCount: 1, expenseCount: 0 },
      { moneyAccountId: 201, companyId: 1, accountName: 'Общий счёт', source: 'Bank', currencyStatus: 'single', currency: 'USD', startBalance: 0, income: 25, expense: 0, endBalance: 25, incomeCount: 1, expenseCount: 0 },
      { moneyAccountId: 202, companyId: 2, accountName: 'Общий счёт', source: 'Bank', currencyStatus: 'single', currency: 'RUB', startBalance: 0, income: 999, expense: 0, endBalance: 999, incomeCount: 1, expenseCount: 0 },
    ]);
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '201');
    expect(screen.getByText('Остаток: 100,00 ₽')).toBeTruthy();
    expect(screen.getByText('Остаток: 25,00 USD')).toBeTruthy();
    expect(screen.queryByText('Остаток: 999,00 ₽')).toBeNull();
  });

  it('filters daily balances and saved reconciliation by account identity after a rename', async () => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 1, companyName: 'Альфа', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 2, companyName: 'Бета', name: 'Общий счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: false },
    ]);
    const base = { date: '2026-05-01', source: 'Bank', currency: 'RUB', openingBalance: 0, expense: 0, incomeCount: 1, expenseCount: 0, isBalanced: true };
    const rows = [
      { ...base, companyId: 1, moneyAccountId: 201, accountName: 'Прежнее имя', income: 123, closingBalance: 123, balanceKey: 'one' },
      { ...base, companyId: 2, moneyAccountId: 202, accountName: 'Общий счёт', income: 999, closingBalance: 999, balanceKey: 'two' },
      { ...base, companyId: null, moneyAccountId: null, accountName: 'Общий счёт', income: 777, closingBalance: 777, balanceKey: null },
    ];
    getMoneyOperationDailyBalancesMock.mockResolvedValue(rows);
    getMoneyBalanceReconciliationResultsMock.mockResolvedValue(rows.map((row, index) => ({
      ...row, id: index + 1, checkedAt: `2026-05-0${index + 2}T10:00:00Z`, issueCount: 0,
    })));
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Прежнее имя');
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '201');
    expect(screen.getByText('Прежнее имя')).toBeTruthy();
    expect(screen.queryByText('999,00 ₽')).toBeNull();
    expect(screen.queryByText('777,00 ₽')).toBeNull();
    expect(screen.getByText(/сохранено 02\.05\.2026/)).toBeTruthy();
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '202');
    expect(screen.queryByText('Прежнее имя')).toBeNull();
    expect(screen.getAllByText('999,00 ₽').length).toBeGreaterThan(0);
    expect(screen.queryByText('777,00 ₽')).toBeNull();
    expect(screen.getByText(/сохранено 03\.05\.2026/)).toBeTruthy();
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '');
    expect(screen.getByText('Прежнее имя')).toBeTruthy();
    expect(screen.getAllByText('999,00 ₽').length).toBeGreaterThan(0);
    expect(screen.getByText('Не подтверждено')).toBeTruthy();
    expect(screen.queryByText('777,00 ₽')).toBeNull();
  });

  it('applies a category filter to the money journal request', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({ id: 1, counterpartyName: 'Поставщик услуг', categoryName: 'Юридические услуги' }),
      createOperation({ id: 2, counterpartyName: 'Поставщик офиса', categoryName: 'Офисные расходы' }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик услуг');
    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.type(screen.getByLabelText('Статья'), 'Юридические');
    await user.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ categoryName: 'Юридические' }),
        expectAbortOptions(),
      ),
    );
    expect(await screen.findByText('Поставщик услуг')).toBeTruthy();
    expect(screen.queryByText('Поставщик офиса')).toBeNull();
    expect(screen.getByText('Статья')).toBeTruthy();
    expect(screen.getByText('Юридические')).toBeTruthy();
  }, 15000);

  it('does not infer an account for an unattributed historical operation', async () => {
    const user = userEvent.setup();
    operations = [createOperation({ moneyAccountId: null, purpose: 'Историческая запись без подтверждённого счёта' })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Историческая запись без подтверждённого счёта');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банку');
    expect((screen.getByLabelText('Банковский счёт') as HTMLSelectElement).value).toBe('');
    expect(screen.getByLabelText('Банковский счёт')).toHaveProperty('disabled', true);
    expect(getLastButton('Сохранить')).toHaveProperty('disabled', true);
    expect(screen.getByText('Историческая операция доступна только для просмотра. Её счёт и принадлежность сохраняются.')).toBeTruthy();
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
  });

  it.each(['None', 'Matched'] as const)('preserves a protected historical invoice link while editing its description (%s)', async (matchStatus) => {
    const user = userEvent.setup();
    operations = [Object.assign(createOperation({
      moneyAccountId: 101,
      direction: 'Income',
      accountingType: 'Fact',
      linkedInvoiceId: null,
      matchStatus,
      isAdvancePayment: false,
      purpose: 'Описание своей операции со скрытым документом',
    }), { isInvoiceLinkProtected: true })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Описание своей операции со скрытым документом');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банку');
    expect(screen.getByLabelText(/^Сумма, RUB$/)).toHaveProperty('disabled', true);
    expect(screen.getByLabelText(/^Дата$/)).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('Банковский счёт')).toHaveProperty('disabled', true);
    expect(getMoneyOperationInvoiceCandidatesMock).not.toHaveBeenCalled();
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Уточнённое описание своей операции');
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(1, expect.objectContaining({
      linkedInvoiceId: null,
      matchStatus,
      purpose: 'Уточнённое описание своей операции',
    }));
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it('does not apply an unallocated historical invoice pointer when only its description changes', async () => {
    const user = userEvent.setup();
    operations = [createOperation({
      moneyAccountId: 101,
      direction: 'Income',
      linkedInvoiceId: 33,
      matchStatus: 'None',
      purpose: 'Прежняя ссылка без зачёта',
    })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Прежняя ссылка без зачёта');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банку');
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Описание без повторного распределения');
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(1, expect.objectContaining({
      linkedInvoiceId: 33,
      matchStatus: 'None',
      purpose: 'Описание без повторного распределения',
    }));
  });

  it('normalizes API dates in the edit form and updates the operation', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        moneyAccountId: 101,
        date: '2026-04-20T00:00:00Z',
        recognitionDate: '2026-04-18T15:45:00Z',
        purpose: 'Старое назначение',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Старое назначение');

    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банку');

    expect(screen.getByLabelText(/^Дата$/)).toHaveProperty('value', '2026-04-20');
    expect(screen.getByLabelText(/^Дата признания расхода$/)).toHaveProperty('value', '2026-04-18');

    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Обновлённое назначение');

    await user.click(getLastButton('Сохранить'));

    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(
      1,
      expect.objectContaining({
        date: '2026-04-20',
        recognitionDate: '2026-04-18',
        purpose: 'Обновлённое назначение',
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция обновлена.', 'Деньги');
    await screen.findByText('Обновлённое назначение');
  }, 15000);

  it('links a bank operation to an invoice from the edit form', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 7,
        moneyAccountId: 101,
        source: 'Bank',
        direction: 'Income',
        counterpartyName: 'Клиент по выписке',
        matchStatus: 'ClarificationRequired',
        linkedInvoiceId: null,
        linkedInvoiceNumber: null,
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Клиент по выписке');

    await user.click(screen.getByRole('button', { name: 'Уточнить' }));
    await user.clear(screen.getByLabelText('ID счёта'));
    await user.type(screen.getByLabelText('ID счёта'), '33');
    await user.type(screen.getByLabelText('Комментарий сверки'), 'Оплата по выписке');
    await user.click(getLastButton('Сохранить'));

    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(
      7,
      expect.objectContaining({
        linkedInvoiceId: 33,
        matchStatus: 'Matched',
        matchComment: 'Оплата по выписке',
      }),
    );
  }, 15000);

  it('selects a suggested invoice candidate for bank operation matching', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 8,
        moneyAccountId: 101,
        source: 'Bank',
        direction: 'Income',
        amount: 25000,
        counterpartyName: 'Ахмедова Айтан Рафик Кызы',
        purpose: 'Оплата по счету №33',
        matchStatus: 'ClarificationRequired',
        linkedInvoiceId: null,
        linkedInvoiceNumber: null,
      }),
    ];
    getMoneyOperationInvoiceCandidatesMock.mockResolvedValueOnce([
      {
        invoiceId: 33,
        paymentId: 330,
        invoiceNumber: '33',
        invoiceDate: '2026-04-20T00:00:00',
        amount: 25000,
        paidAmount: 0,
        outstandingAmount: 25000,
        status: 'Pending',
        clientId: 74,
        clientName: 'Ахмедова Айтан Рафик Кызы',
        counterpartyName: 'Ахмедова Айтан Рафик Кызы',
        matchReason: 'номер счёта найден в назначении',
        score: 180,
      },
    ]);

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Ахмедова Айтан Рафик Кызы');

    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Подходящие счета');
    await screen.findByText(/Счёт 33 от 20\.04\.2026/);
    await user.click(screen.getByRole('button', { name: /Счёт 33/ }));
    await user.click(getLastButton('Сохранить'));

    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(
      8,
      expect.objectContaining({
        linkedInvoiceId: 33,
        paidInvoiceNumber: '33',
        matchStatus: 'Matched',
      }),
    );
  }, 15000);

  it.each(['Income', 'Expense'] as const)('requires an explicit invoice choice before saving multiple candidates (%s)', async (direction) => {
    const user = userEvent.setup();
    operations = [createOperation({
      id: 8,
      moneyAccountId: 101,
      source: 'Bank',
      direction,
      amount: 25000,
      counterpartyName: 'Ручной выбор бухгалтера',
      purpose: 'Оплата по счету №33',
      isAdvancePayment: false,
      matchStatus: 'ClarificationRequired',
      linkedInvoiceId: null,
      linkedInvoiceNumber: null,
    })];
    getMoneyOperationInvoiceCandidatesMock.mockResolvedValueOnce([
      {
        invoiceId: 33, paymentId: 330, invoiceNumber: '33', invoiceDate: '2026-04-20T00:00:00',
        amount: 25000, paidAmount: 0, outstandingAmount: 25000, status: 'Pending',
        counterpartyName: 'Ручной выбор бухгалтера', matchReason: 'номер и сумма совпадают', score: 160,
      },
      {
        invoiceId: 34, paymentId: 340, invoiceNumber: '33', invoiceDate: '2026-04-21T00:00:00',
        amount: 30000, paidAmount: 0, outstandingAmount: 30000, status: 'Pending',
        counterpartyName: 'Второй документ', matchReason: 'номер счёта найден в назначении', score: 120,
      },
    ]);
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Ручной выбор бухгалтера');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByRole('button', { name: /Счёт 33 от 21\.04\.2026/ });
    expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledWith(8, undefined, expect.any(AbortSignal));
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('value', '');
    expect(operations[0].linkedInvoiceId).toBeNull();
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
    if (direction === 'Expense') expect(screen.getByText('Сверка с расходным счётом')).toBeTruthy();

    await user.click(screen.getByRole('button', { name: /Счёт 33 от 21\.04\.2026/ }));
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('value', '34');
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(8, expect.objectContaining({
      direction, linkedInvoiceId: 34, paidInvoiceNumber: '33', matchStatus: 'Matched',
    }));
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  }, 15000);

  it.each(['Income', 'Expense'] as const)('hides loaded and late invoice candidates after changing direction (%s)', async (direction) => {
    const user = userEvent.setup();
    operations = [createOperation({
      id: 8, moneyAccountId: 101, direction, amount: 25000, isAdvancePayment: false,
      linkedInvoiceId: null, matchStatus: 'ClarificationRequired', purpose: 'Смена направления до сверки',
    })];
    const candidates = [{
      invoiceId: 33, paymentId: 330, invoiceNumber: '33', amount: 25000, paidAmount: 0,
      outstandingAmount: 25000, status: 'Pending', score: 160,
    }];
    let complete!: (items: typeof candidates) => void;
    getMoneyOperationInvoiceCandidatesMock.mockResolvedValueOnce(candidates)
      .mockReturnValueOnce(new Promise<typeof candidates>(resolve => { complete = resolve; }));
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Смена направления до сверки');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByRole('button', { name: /Счёт 33/ });
    const originalLabel = direction === 'Income' ? 'Приход' : 'Расход';
    const changedLabel = direction === 'Income' ? 'Расход' : 'Приход';
    await user.click(screen.getByRole('radio', { name: changedLabel }));
    expect(screen.queryByRole('button', { name: /Счёт 33/ })).toBeNull();
    expect(screen.getByText('После смены направления или денежного счёта сохраните операцию и откройте её снова для выбора подходящего счёта.')).toBeTruthy();
    expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(1);

    await user.click(screen.getByRole('radio', { name: originalLabel }));
    await waitFor(() => expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(2));
    await user.click(screen.getByRole('radio', { name: changedLabel }));
    await act(async () => { complete(candidates); });
    expect(screen.queryByRole('button', { name: /Счёт 33/ })).toBeNull();
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('value', '');
    expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(2);
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it.each(['Income', 'Expense'] as const)('clears an unsaved invoice choice when saving another direction (%s)', async (direction) => {
    const user = userEvent.setup();
    operations = [createOperation({
      id: 8, moneyAccountId: 101, direction, amount: 25000, isAdvancePayment: true,
      linkedInvoiceId: null, matchStatus: 'ClarificationRequired', purpose: 'Смена направления после выбора',
    })];
    getMoneyOperationInvoiceCandidatesMock.mockResolvedValueOnce([{
      invoiceId: 33, paymentId: 330, invoiceNumber: '33', amount: 25000, paidAmount: 0,
      outstandingAmount: 25000, status: 'Pending', score: 160,
    }]);
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Смена направления после выбора');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await user.click(await screen.findByRole('button', { name: /Счёт 33/ }));
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('value', '33');
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    await user.click(screen.getByRole('radio', { name: direction === 'Income' ? 'Расход' : 'Приход' }));
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('value', '');
    expect(screen.queryByRole('button', { name: /Счёт 33/ })).toBeNull();
    expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(1);
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(8, expect.objectContaining({
      direction: direction === 'Income' ? 'Expense' : 'Income', amount: 25000,
      linkedInvoiceId: null, paidInvoiceNumber: '', paidAccountName: '', paidAmount: 0,
      matchStatus: 'None', matchComment: '',
    }));
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it.each(['Income', 'Expense'] as const)('clears a provisional invoice and ignores late candidates after switching company account (%s)', async (direction) => {
    const user = userEvent.setup();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 101, companyId: 1, companyName: 'Компания Альфа', name: 'Банк Альфа', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 2, companyName: 'Компания Бета', name: 'Банк Бета', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
    ]);
    operations = [createOperation({
      id: 8, moneyAccountId: 101, direction, amount: 25000, isAdvancePayment: true,
      linkedInvoiceId: null, matchStatus: 'ClarificationRequired', purpose: 'Смена компании до сверки',
    })];
    const candidates = [{
      invoiceId: 33, paymentId: 330, invoiceNumber: '33', amount: 25000, paidAmount: 0,
      outstandingAmount: 25000, status: 'Pending', score: 160,
    }];
    let complete!: (items: typeof candidates) => void;
    getMoneyOperationInvoiceCandidatesMock.mockResolvedValueOnce(candidates)
      .mockReturnValueOnce(new Promise<typeof candidates>(resolve => { complete = resolve; }));
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Смена компании до сверки');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await user.click(await screen.findByRole('button', { name: /Счёт 33/ }));
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('value', '33');
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '202');
    expect(screen.queryByRole('button', { name: /Счёт 33/ })).toBeNull();
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('value', '');
    expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(1);
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '101');
    await waitFor(() => expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(2));
    await user.selectOptions(screen.getByLabelText('Банковский счёт'), '202');
    await act(async () => { complete(candidates); });
    expect(screen.queryByRole('button', { name: /Счёт 33/ })).toBeNull();
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    await user.click(getLastButton('Сохранить'));
    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(8, expect.objectContaining({
      direction, moneyAccountId: 202, amount: 25000, linkedInvoiceId: null,
      paidInvoiceNumber: '', paidAccountName: '', paidAmount: 0, matchStatus: 'None', matchComment: '',
    }));
    expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(2);
    expect(createMoneyOperationMock).not.toHaveBeenCalled();
  });

  it.each(['applied', 'protected', 'unavailable-account'] as const)('keeps expense invoice selection locked for %s', async (boundary) => {
    const user = userEvent.setup();
    operations = [Object.assign(createOperation({
      direction: 'Expense',
      moneyAccountId: boundary === 'unavailable-account' ? 999 : 101,
      linkedInvoiceId: boundary === 'applied' ? 33 : null,
      matchStatus: boundary === 'applied' ? 'Matched' : 'ClarificationRequired',
      purpose: 'Расход с защищённой финансовой связью',
    }), { isInvoiceLinkProtected: boundary === 'protected' })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Расход с защищённой финансовой связью');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Редактировать операцию по банку');
    expect(screen.getByLabelText('ID счёта')).toHaveProperty('disabled', true);
    expect(getMoneyOperationInvoiceCandidatesMock).not.toHaveBeenCalled();
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
  });

  it('does not open expense invoice selection without payments edit permission', async () => {
    const user = userEvent.setup();
    permissionsState.paymentsCanEdit = false;
    operations = [createOperation({ moneyAccountId: 101, direction: 'Expense', purpose: 'Расход без права редактирования' })];
    render(<MoneyOperations source="Bank" />);
    await screen.findByText('Расход без права редактирования');
    const edit = screen.getByRole('button', { name: 'Изменить' });
    expect(edit).toHaveProperty('disabled', true);
    await user.click(edit);
    expect(screen.queryByText('Подходящие счета')).toBeNull();
    expect(getMoneyOperationInvoiceCandidatesMock).not.toHaveBeenCalled();
    expect(updateMoneyOperationMock).not.toHaveBeenCalled();
  });

  it('allows linking an e-money income operation to an invoice', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 18,
        moneyAccountId: 501,
        source: 'EMoney',
        direction: 'Income',
        amount: 7500,
        counterpartyName: 'Ахмедова Айтан Рафик Кызы',
        purpose: 'ЮMoney, оплата по счету №44',
        matchStatus: 'ClarificationRequired',
        linkedInvoiceId: null,
        linkedInvoiceNumber: null,
      }),
    ];
    getMoneyOperationInvoiceCandidatesMock.mockResolvedValueOnce([
      {
        invoiceId: 44,
        paymentId: 440,
        invoiceNumber: '44',
        invoiceDate: '2026-04-20T00:00:00',
        amount: 7500,
        paidAmount: 0,
        outstandingAmount: 7500,
        status: 'Pending',
        clientId: 74,
        clientName: 'Ахмедова Айтан Рафик Кызы',
        counterpartyName: 'Ахмедова Айтан Рафик Кызы',
        matchReason: 'номер счёта найден в назначении',
        score: 180,
      },
    ]);

    getMoneyAccountsMock.mockResolvedValue([
      { id: 501, companyId: 1, companyName: 'Компания Альфа', name: 'ЮMoney', source: 'EMoney', currency: 'RUB', isActive: true, isDefault: true, createdAt: '2026-10-01T00:00:00Z' },
    ]);
    render(<MoneyOperations source="EMoney" />);

    await screen.findByText('Ахмедова Айтан Рафик Кызы');

    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await screen.findByText('Подходящие счета');
    await screen.findByText(/Счёт 44 от 20\.04\.2026/);
    await user.click(screen.getByRole('button', { name: /Счёт 44/ }));
    await user.click(getLastButton('Сохранить'));

    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(
      18,
      expect.objectContaining({
        source: 'EMoney',
        linkedInvoiceId: 44,
        paidInvoiceNumber: '44',
        matchStatus: 'Matched',
      }),
    );
  }, 15000);

  it('marks a bank operation as ignored from the edit form', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 9,
        moneyAccountId: 101,
        source: 'Bank',
        direction: 'Income',
        counterpartyName: 'Неизвестный отправитель',
        matchStatus: 'ClarificationRequired',
        linkedInvoiceId: null,
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Неизвестный отправитель');
    await user.click(screen.getByRole('button', { name: 'Изменить' }));
    await user.selectOptions(screen.getByLabelText('Статус'), 'Ignored');
    await user.click(getLastButton('Сохранить'));

    await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
    expect(updateMoneyOperationMock).toHaveBeenCalledWith(
      9,
      expect.objectContaining({
        linkedInvoiceId: null,
        matchStatus: 'Ignored',
      }),
    );
  }, 15000);

  it('opens a linked invoice from the cashflow journal', async () => {
    const user = userEvent.setup();
    const onOpenInvoice = vi.fn();
    operations = [
      createOperation({
        id: 10,
        source: 'Bank',
        direction: 'Income',
        counterpartyName: 'Клиент со счётом',
        linkedInvoiceId: 33,
        linkedInvoiceNumber: '33',
        paidAccountName: '33',
        matchStatus: 'Matched',
      }),
    ];

    render(<MoneyOperations source="Total" onOpenInvoice={onOpenInvoice} />);

    await screen.findByText('Общий баланс');
    await user.click(screen.getByRole('button', { name: 'ОДДС' }));
    await screen.findByText('Клиент со счётом');
    await user.click(screen.getByRole('button', { name: 'Счёт 33' }));

    expect(onOpenInvoice).toHaveBeenCalledWith(33, 'Income');
  });

  it('keeps linked invoice as text when accounts section is hidden', async () => {
    const user = userEvent.setup();
    const onOpenInvoice = vi.fn();
    permissionsState.accountsCanView = false;
    operations = [
      createOperation({
        id: 11,
        source: 'Bank',
        direction: 'Income',
        counterpartyName: 'Клиент без счетов',
        linkedInvoiceId: 44,
        linkedInvoiceNumber: '44',
        paidAccountName: '44',
        matchStatus: 'Matched',
      }),
    ];

    render(<MoneyOperations source="Total" onOpenInvoice={onOpenInvoice} />);

    await screen.findByText('Общий баланс');
    await user.click(screen.getByRole('button', { name: 'ОДДС' }));
    await screen.findByText('Клиент без счетов');

    expect(screen.getByText('Счёт 44')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Счёт 44' })).toBeNull();
    expect(onOpenInvoice).not.toHaveBeenCalled();
  });

  it('keeps money account link as text when accounts section is hidden', async () => {
    permissionsState.accountsCanView = false;
    operations = [
      createOperation({
        id: 12,
        source: 'Bank',
        accountName: 'Расчетный счет без доступа',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Расчетный счет без доступа');
    expect(screen.queryByRole('button', { name: 'Расчетный счет без доступа' })).toBeNull();
    expect(setActiveTabMock).not.toHaveBeenCalledWith('accounts');
  });

  it('deletes a custom money operation after confirmation', async () => {
    const user = userEvent.setup();

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');

    await user.click(screen.getByRole('button', { name: 'Удалить' }));

    await screen.findByText('Удалить операцию?');
    const confirmButtons = screen.getAllByRole('button', { name: 'Удалить' });
    await user.click(confirmButtons[confirmButtons.length - 1]);

    await waitFor(() => expect(deleteMoneyOperationMock).toHaveBeenCalledWith(1));
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция удалена.', 'Деньги');
    await waitFor(() => {
      expect(screen.queryByText('Поставщик А')).toBeNull();
    });
  }, 15000);

  it('deletes selected bank operations from the current page', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({ id: 1, counterpartyName: 'Поставщик А' }),
      createOperation({ id: 2, counterpartyName: 'Поставщик Б' }),
      createOperation({
        id: 3,
        counterpartyName: 'Клиент со счетом',
        direction: 'Income',
        linkedInvoiceId: 33,
        linkedInvoiceNumber: '33',
        matchStatus: 'Matched',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Поставщик А');
    await user.click(screen.getByRole('checkbox', { name: 'Выбрать операцию 1' }));
    await user.click(screen.getByRole('checkbox', { name: 'Выбрать операцию 2' }));
    expect(screen.getByText('Выбрано операций: 2')).toBeTruthy();
    expect(screen.getByRole<HTMLInputElement>('checkbox', { name: 'Выбрать операцию 3' }).disabled).toBe(true);

    await user.click(screen.getByRole('button', { name: 'Удалить выбранные' }));
    await screen.findByText('Удалить выбранные операции?');
    const confirmButtons = screen.getAllByRole('button', { name: 'Удалить выбранные' });
    await user.click(confirmButtons[confirmButtons.length - 1]);

    await waitFor(() => expect(deleteMoneyOperationMock).toHaveBeenCalledWith(1));
    expect(deleteMoneyOperationMock).toHaveBeenCalledWith(2);
    expect(deleteMoneyOperationMock).not.toHaveBeenCalledWith(3);
    expect(toastSuccessMock).toHaveBeenCalledWith('Удалено операций: 2.', 'Деньги');
    await waitFor(() => expect(screen.queryByText('Поставщик А')).toBeNull());
    expect(screen.queryByText('Поставщик Б')).toBeNull();
    expect(screen.getByText('Клиент со счетом')).toBeTruthy();
  }, 15000);

  it('does not allow deleting an operation already applied to an invoice', async () => {
    operations = [
      createOperation({
        direction: 'Income',
        counterpartyName: 'Клиент со счётом',
        linkedInvoiceId: 33,
        linkedInvoiceNumber: '33',
        matchStatus: 'Matched',
      }),
    ];

    render(<MoneyOperations source="Bank" />);

    await screen.findByText('Клиент со счётом');
    expect(screen.getByText('Зачтено в счет')).toBeTruthy();
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Удалить' }).disabled).toBe(true);
  });

  it('creates a cash order for a readonly cash payment', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: -77,
        source: 'Cash',
        direction: 'Income',
        accountName: '33',
        counterpartyName: 'Ахмедова Айтан Рафик Кызы',
        amount: 25000,
        isReadonly: true,
        relatedPaymentId: 77,
        canCreateCashOrder: true,
      }),
    ];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Ахмедова Айтан Рафик Кызы');
    expect(screen.getByText('Из графика')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Создать приходный кассовый ордер' }));

    await waitFor(() => expect(createCashOrderFromPaymentMock).toHaveBeenCalledWith(77));
    expect(toastSuccessMock).toHaveBeenCalledWith('Создан кассовый ордер ПКО-2026-0001.', 'Касса');
    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(2));
  }, 15000);

  it('hides cash order creation actions without payment create permission', async () => {
    permissionsState.paymentsCanCreate = false;
    operations = [
      createOperation({
        id: -77,
        source: 'Cash',
        direction: 'Income',
        accountName: '33',
        counterpartyName: 'Клиент без права создания',
        amount: 25000,
        isReadonly: true,
        relatedPaymentId: 77,
        canCreateCashOrder: true,
      }),
    ];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Клиент без права создания');
    expect(screen.queryByRole('button', { name: 'Создать приходный кассовый ордер' })).toBeNull();
    await screen.findByText('Кассовые ордера');
    expect(screen.queryByRole('button', { name: 'Создать ордер' })).toBeNull();
    expect(createCashOrderFromPaymentMock).not.toHaveBeenCalled();
    expect(createCashOrderMock).not.toHaveBeenCalled();
  }, 15000);

  it('creates a cash order for a manual cash operation', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 11,
        source: 'Cash',
        direction: 'Income',
        accountName: 'Касса',
        counterpartyName: 'Ручной клиент',
        amount: 5000,
        isReadonly: false,
        relatedPaymentId: null,
        canCreateCashOrder: true,
      }),
    ];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Ручной клиент');
    await user.click(screen.getByRole('button', { name: 'Создать приходный кассовый ордер' }));

    await waitFor(() => expect(createCashOrderFromMoneyOperationMock).toHaveBeenCalledWith(11));
    expect(toastSuccessMock).toHaveBeenCalledWith('Создан кассовый ордер ПКО-2026-0002.', 'Касса');
    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(2));
  }, 15000);

  it('explains why an order cannot be created for an operation without currency', async () => {
    const user = userEvent.setup();
    operations = [{ ...createOperation({ id: 11, source: 'Cash', direction: 'Income', counterpartyName: 'Ручной клиент',
      amount: 5000, isReadonly: false, relatedPaymentId: null, canCreateCashOrder: true }), currency: null }];
    const message = 'В денежной операции не указана валюта. Уточните её перед созданием кассового ордера.';
    createCashOrderFromMoneyOperationMock.mockRejectedValue(new Error(message));
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Ручной клиент');
    await user.click(screen.getByRole('button', { name: 'Создать приходный кассовый ордер' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(message, 'Касса'));
    expect(toastSuccessMock).not.toHaveBeenCalled();
  });

  it('explains when the cash book currency cannot be confirmed', async () => {
    const message = 'Нельзя рассчитать кассовую книгу: валюта проведённых ордеров не подтверждена как рубли.';
    getCashBookMock.mockRejectedValue(new Error(message));
    const order = { ...(await getCashOrderMock(9)), currency: 'USD' };
    getCashOrderRegisterMock.mockResolvedValueOnce({ items: [order], total: 1, page: 1, pageSize: 10,
      incomingCount: 1, outgoingCount: 0, draftCount: 0, postedCount: 1, cancelledCount: 0,
      amountsAvailable: false, incomingAmount: null, outgoingAmount: null });
    render(<MoneyOperations source="Cash" />);
    expect(await screen.findByText(message)).toBeTruthy();
    expect(screen.queryByTestId('cash-book-summary')).toBeNull();
    expect(screen.getByRole('button', { name: order.number })).toBeTruthy();
    expect(screen.getByText(/25.*000,00 USD/)).toBeTruthy();
    expect(screen.getByText('Приходные ордера').nextElementSibling?.textContent).toContain('1 на —');
  });

  it('does not trust register totals from an older response without currency confirmation', async () => {
    const order = { ...(await getCashOrderMock(9)), currency: 'USD' };
    getCashOrderRegisterMock.mockResolvedValueOnce({ items: [order], total: 1, page: 1, pageSize: 10,
      incomingCount: 1, outgoingCount: 0, draftCount: 0, postedCount: 1, cancelledCount: 0,
      incomingAmount: 25000, outgoingAmount: 0 });
    render(<MoneyOperations source="Cash" />);
    const heading = await screen.findByText('Приходные ордера');
    expect(heading.nextElementSibling?.textContent).toContain('1 на —');
    expect(screen.getByRole('button', { name: order.number })).toBeTruthy();
  });

  it('shows the register total in its confirmed single currency', async () => {
    const order = { ...(await getCashOrderMock(9)), currency: 'USD' };
    getCashOrderRegisterMock.mockResolvedValueOnce({ items: [order], total: 1, page: 1, pageSize: 10,
      incomingCount: 1, outgoingCount: 0, draftCount: 0, postedCount: 1, cancelledCount: 0,
      amountsAvailable: true, currencyStatus: 'single', currency: 'USD', incomingAmount: 25000, outgoingAmount: 0 });
    render(<MoneyOperations source="Cash" />);
    const heading = await screen.findByText('Приходные ордера');
    expect(heading.nextElementSibling?.textContent).toMatch(/25.*000,00 USD/);
    expect(heading.nextElementSibling?.textContent).not.toContain('₽');
  });

  it('shows cash book balances in their confirmed foreign currency', async () => {
    getCashBookMock.mockResolvedValueOnce({ from: '2026-04-01', to: '2026-04-30', currencyStatus: 'single', currency: 'USD',
      openingBalance: 100, income: 0, expense: 20, closingBalance: 80, incomingCount: 0, outgoingCount: 1,
      days: [{ date: '2026-04-29', openingBalance: 100, income: 0, expense: 20, closingBalance: 80, incomingCount: 0, outgoingCount: 1 }] });
    render(<MoneyOperations source="Cash" />);
    const summary = await screen.findByTestId('cash-book-summary');
    expect(within(summary).getAllByText('100,00 USD').length).toBeGreaterThan(0);
    expect(within(summary).getAllByText('80,00 USD').length).toBeGreaterThan(0);
    expect(within(summary).queryByText('80,00 ₽')).toBeNull();
    expect((screen.getByRole('button', { name: 'Кассовая книга' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('does not label an older cash book response as confirmed rubles', async () => {
    getCashBookMock.mockResolvedValueOnce({ from: '2026-04-01', to: '2026-04-30', openingBalance: 0,
      income: 25000, expense: 0, closingBalance: 25000, incomingCount: 1, outgoingCount: 0, days: [] });
    render(<MoneyOperations source="Cash" />);
    const summary = await screen.findByTestId('cash-book-summary');
    expect(within(summary).getAllByText('—').length).toBeGreaterThan(0);
    expect(within(summary).queryByText('25 000,00 ₽')).toBeNull();
    expect((screen.getByRole('button', { name: 'Кассовая книга' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('loads and renders the cash orders journal in the cash section', async () => {
    const user = userEvent.setup();
    operations = [];
    getCashOrderRegisterMock.mockResolvedValueOnce({
      items: [
        {
          id: 9,
          number: 'ПКО-2026-0003',
          type: 'Incoming',
          status: 'Posted',
          date: '2026-04-29',
          amount: 25000,
          currency: 'RUB',
          clientId: 74,
          clientName: 'Ахмедова Айтан Рафик Кызы',
          paymentId: 77,
          moneyOperationId: null,
          invoiceId: null,
          account: '33',
          payerName: 'Ахмедова Айтан Рафик Кызы',
          basis: 'Оплата от клиента. счет №33',
          comment: null,
          createdAt: '2026-04-29T08:00:00Z',
          updatedAt: null,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 10,
      incomingCount: 1,
      outgoingCount: 0,
      draftCount: 0,
      postedCount: 1,
      cancelledCount: 0,
      incomingAmount: 25000,
      outgoingAmount: 0,
    });

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await screen.findByText('ПКО-2026-0003');
    const incomingSummary = await screen.findByText('Приходные ордера');
    expect(incomingSummary.nextElementSibling?.textContent).toContain('1 на');
    await screen.findByText('Оплата от клиента. счет №33');
    expect(getCashOrderRegisterMock).toHaveBeenCalledWith(
      expect.objectContaining({
        page: 1,
        pageSize: 10,
      }),
      expectAbortOptions(),
    );

    await user.click(screen.getByRole('button', { name: 'Кассовая книга' }));
    await waitFor(() =>
      expect(getCashBookPrintHtmlMock).toHaveBeenCalledWith(
        expect.objectContaining({ from: '2026-01-01', to: '2026-12-31' }),
      ),
    );

    await user.click(screen.getByRole('button', { name: 'Журнал' }));
    await waitFor(() =>
      expect(getCashOrderRegisterPrintHtmlMock).toHaveBeenCalledWith(
        expect.objectContaining({
          from: '2026-01-01',
          to: '2026-12-31',
        }),
      ),
    );
  }, 15000);

  it('passes the selected cash account to cash orders and cash book requests', async () => {
    const user = userEvent.setup();
    getCashOrderRegisterMock.mockImplementation(async (params: { moneyAccountId?: number }) => ({
      moneyAccountId: params.moneyAccountId, items: [], total: 0, page: 1, pageSize: 10,
      incomingCount: 0, outgoingCount: 0, draftCount: 0, postedCount: 0, cancelledCount: 0,
      amountsAvailable: true, currencyStatus: 'empty', currency: null, incomingAmount: 0, outgoingAmount: 0,
    }));
    getCashBookMock.mockImplementation(async (params: { moneyAccountId?: number; accountName?: string }) => ({
      moneyAccountId: params.moneyAccountId, accountName: params.accountName,
      from: '2026-04-01', to: '2026-04-30', currencyStatus: 'single', currency: 'RUB',
      openingBalance: 0, income: 0, expense: 0, closingBalance: 0, incomingCount: 0, outgoingCount: 0, days: [],
    }));
    operations = [
      createOperation({
        id: 401,
        source: 'Cash',
        direction: 'Income',
        amount: 25_000,
        accountName: 'Основная касса',
      }),
      createOperation({
        id: 402,
        source: 'Cash',
        direction: 'Income',
        amount: 5_000,
        accountName: 'Резервная касса',
      }),
    ];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.selectOptions(screen.getByLabelText('Касса в заголовке'), 'Резервная касса');

    await waitFor(() =>
      expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(
        expect.objectContaining({
          accountName: 'Резервная касса',
          moneyAccountId: 104,
          page: 1,
          pageSize: 10,
        }),
        expectAbortOptions(),
      ),
    );
    expect(getCashBookMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ accountName: 'Резервная касса', moneyAccountId: 104 }),
      expectAbortOptions(),
    );
    await screen.findByText('Касса: Резервная касса');
  }, 15000);

  it('aborts both obsolete cash journal requests and aborts the active pair on unmount', async () => {
    const user = userEvent.setup();
    operations = [];
    const registerSignals: AbortSignal[] = [];
    const bookSignals: AbortSignal[] = [];
    const registerResolvers: Array<(value: Record<string, unknown>) => void> = [];
    const bookResolvers: Array<(value: Record<string, unknown>) => void> = [];
    getCashOrderRegisterMock.mockImplementation((_params, options) => {
      registerSignals.push(options.signal);
      return new Promise((resolve) => registerResolvers.push(resolve));
    });
    getCashBookMock.mockImplementation((_params, options) => {
      bookSignals.push(options.signal);
      return new Promise((resolve) => bookResolvers.push(resolve));
    });

    const { unmount } = render(<MoneyOperations source="Cash" />);

    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(1));
    expect(registerSignals[0]).toBe(bookSignals[0]);
    expect(registerSignals[0].aborted).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Приходный кассовый ордер' }));

    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(2));
    expect(registerSignals[0].aborted).toBe(true);
    expect(bookSignals[0].aborted).toBe(true);
    expect(registerSignals[1]).toBe(bookSignals[1]);
    expect(registerSignals[1].aborted).toBe(false);

    await act(async () => {
      registerResolvers[1]({
        items: [], total: 2, page: 1, pageSize: 10,
        incomingCount: 2, outgoingCount: 0, draftCount: 0, postedCount: 2, cancelledCount: 0,
        incomingAmount: 3000, outgoingAmount: 0,
      });
      bookResolvers[1]({
        from: '2026-01-01', to: '2026-12-31', openingBalance: 0,
        income: 3000, expense: 0, closingBalance: 3000, incomingCount: 2, outgoingCount: 0, days: [],
      });
    });
    await screen.findByText('Всего: 2');

    await act(async () => {
      registerResolvers[0]({
        items: [], total: 99, page: 1, pageSize: 10,
        incomingCount: 99, outgoingCount: 0, draftCount: 0, postedCount: 99, cancelledCount: 0,
        incomingAmount: 99000, outgoingAmount: 0,
      });
      bookResolvers[0]({
        from: '2026-01-01', to: '2026-12-31', openingBalance: 0,
        income: 99000, expense: 0, closingBalance: 99000, incomingCount: 99, outgoingCount: 0, days: [],
      });
    });
    expect(screen.getByText('Всего: 2')).toBeTruthy();

    unmount();

    expect(registerSignals[1].aborted).toBe(true);
    expect(bookSignals[1].aborted).toBe(true);
  }, 15000);

  it('loads the first cash journal page directly when filters change on a later page', async () => {
    const user = userEvent.setup();
    operations = [];
    getCashOrderRegisterMock.mockImplementation(async (params) => ({
      items: [], total: 25, page: params.page, pageSize: 10,
      incomingCount: 15, outgoingCount: 10, draftCount: 0, postedCount: 25, cancelledCount: 0,
      incomingAmount: 15000, outgoingAmount: 10000,
    }));

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(1));
    const nextButton = screen.getAllByRole<HTMLButtonElement>('button', { name: 'Вперед' })
      .find((button) => !button.disabled);
    expect(nextButton).toBeTruthy();
    await user.click(nextButton!);
    await waitFor(() =>
      expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ page: 2 }),
        expectAbortOptions(),
      ),
    );
    const registerCallsBeforeFilter = getCashOrderRegisterMock.mock.calls.length;
    const bookCallsBeforeFilter = getCashBookMock.mock.calls.length;

    await user.click(screen.getByRole('button', { name: 'Расходный кассовый ордер' }));

    await waitFor(() =>
      expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ type: 'Outgoing', page: 1 }),
        expectAbortOptions(),
      ),
    );
    expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(registerCallsBeforeFilter + 1);
    expect(getCashBookMock).toHaveBeenCalledTimes(bookCallsBeforeFilter + 1);
  }, 15000);

  it('opens and manages a cash order card from the cash journal', async () => {
    const user = userEvent.setup();
    operations = [];
    getCashOrderRegisterMock.mockResolvedValue({
      items: [
        {
          id: 9,
          number: 'ПКО-2026-0003',
          type: 'Incoming',
          status: 'Posted',
          date: '2026-04-29',
          amount: 25000,
          currency: 'RUB',
          clientId: 74,
          clientName: 'Ахмедова Айтан Рафик Кызы',
          paymentId: 33,
          moneyOperationId: null,
          invoiceId: 33,
          account: 'Основная касса',
          payerName: 'Ахмедова Айтан Рафик Кызы',
          receiverName: 'Основная касса',
          basis: 'Оплата от клиента. счет №33',
          appendix: 'Счёт №33',
          comment: null,
          cancelledAt: null,
          cancelledReason: null,
          createdAt: '2026-04-29T10:00:00Z',
          updatedAt: null,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 10,
      incomingCount: 1,
      outgoingCount: 0,
      draftCount: 0,
      postedCount: 1,
      cancelledCount: 0,
      incomingAmount: 25000,
      outgoingAmount: 0,
    });

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'ПКО-2026-0003' }));

    await waitFor(() => expect(getCashOrderMock).toHaveBeenCalledWith(9));
    await screen.findByText('Приходный кассовый ордер ПКО-2026-0003');
    await screen.findByText('Связанный счёт');
    await waitFor(() => expect(screen.getAllByText('#33').length).toBeGreaterThan(0));
    await waitFor(() => expect(screen.getAllByDisplayValue('Основная касса').length).toBeGreaterThan(0));

    const amountInput = screen.getByDisplayValue('25000');
    await user.clear(amountInput);
    await user.type(amountInput, '30000');
    const appendixInput = screen.getByDisplayValue('Счёт №33');
    await user.clear(appendixInput);
    await user.type(appendixInput, 'Квитанция к счёту №33');
    await user.click(getLastButton('Сохранить'));
    await waitFor(() =>
      expect(updateCashOrderMock).toHaveBeenCalledWith(
        9,
        expect.objectContaining({ amount: 30000, appendix: 'Квитанция к счёту №33' }),
      ),
    );

    await user.click(screen.getByRole('button', { name: 'PDF' }));
    await waitFor(() => expect(downloadCashOrderPdfMock).toHaveBeenCalledWith(9));

    await user.click(screen.getByRole('button', { name: 'Копировать' }));
    await waitFor(() => expect(copyCashOrderMock).toHaveBeenCalledWith(9));

    await user.click(screen.getByRole('button', { name: 'Отменить' }));
    await user.type(screen.getByPlaceholderText('Причина отмены'), 'Дубликат');
    await user.click(screen.getByRole('button', { name: 'Подтвердить отмену' }));
    await waitFor(() => expect(cancelCashOrderMock).toHaveBeenCalledWith(10, 'Дубликат'));
    await screen.findByText('Документ отменён');
    await screen.findByText(/Причина: Дубликат/);
  }, 15000);

  it('keeps the cash book summary within the mobile viewport and scrolls only its table', async () => {
    operations = [];

    render(<MoneyOperations source="Cash" />);

    const summary = await screen.findByTestId('cash-book-summary');
    expect(summary.className).toContain('min-w-0');
    expect(summary.className).toContain('grid-cols-[minmax(0,1fr)]');

    const daysScroll = screen.getByTestId('cash-book-days-scroll');
    expect(daysScroll.className).toContain('max-w-full');
    expect(daysScroll.className).toContain('overflow-x-auto');
  });

  it('deletes a draft cash order from the cash order card', async () => {
    const user = userEvent.setup();
    operations = [];
    getCashOrderRegisterMock.mockResolvedValue({
      items: [
        {
          id: 12,
          number: 'ПКО-2026-0005',
          type: 'Incoming',
          status: 'Draft',
          date: '2026-04-29',
          amount: 1500,
          currency: 'RUB',
          clientId: null,
          clientName: null,
          paymentId: null,
          moneyOperationId: null,
          invoiceId: null,
          account: 'Основная касса',
          payerName: 'Черновой клиент',
          receiverName: 'Основная касса',
          basis: 'Черновик приёма наличных',
          appendix: null,
          comment: null,
          cancelledAt: null,
          cancelledReason: null,
          createdAt: '2026-04-29T10:00:00Z',
          updatedAt: null,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 10,
      incomingCount: 1,
      outgoingCount: 0,
      draftCount: 1,
      postedCount: 0,
      cancelledCount: 0,
      incomingAmount: 0,
      outgoingAmount: 0,
    });
    getCashOrderMock.mockResolvedValueOnce({
      id: 12,
      number: 'ПКО-2026-0005',
      type: 'Incoming',
      status: 'Draft',
      date: '2026-04-29',
      amount: 1500,
      currency: 'RUB',
      clientId: null,
      clientName: null,
      paymentId: null,
      moneyOperationId: null,
      invoiceId: null,
      account: 'Основная касса',
      payerName: 'Черновой клиент',
      receiverName: 'Основная касса',
      basis: 'Черновик приёма наличных',
      appendix: null,
      comment: null,
      cancelledAt: null,
      cancelledReason: null,
      createdAt: '2026-04-29T10:00:00Z',
      updatedAt: null,
    });

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('ПКО-2026-0005');
    await user.click(screen.getByRole('button', { name: 'ПКО-2026-0005' }));
    await screen.findByText('Черновик');
    await user.click(screen.getByRole('button', { name: 'Удалить' }));
    await user.click(screen.getByRole('button', { name: 'Подтвердить удаление' }));

    await waitFor(() => expect(deleteCashOrderMock).toHaveBeenCalledWith(12));
    expect(toastSuccessMock).toHaveBeenCalledWith('Черновик кассового ордера удалён.', 'Касса');
  }, 15000);

  it('filters the cash orders journal by outgoing orders', async () => {
    const user = userEvent.setup();
    operations = [];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'Расходный кассовый ордер' }));

    await waitFor(() =>
      expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(
        expect.objectContaining({
          type: 'Outgoing',
          page: 1,
          pageSize: 10,
        }),
        expectAbortOptions(),
      ),
    );
  }, 15000);

  it('creates a standalone cash order from the cash journal', async () => {
    const user = userEvent.setup();
    operations = [];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'Создать ордер' }));
    await screen.findByText('Приходный кассовый ордер Новый');
    expect((screen.getByLabelText('Валюта') as HTMLInputElement).value).toBe('');

    await user.clear(screen.getByLabelText('Сумма'));
    await user.type(screen.getByLabelText('Сумма'), '25000');
    await user.selectOptions(screen.getByLabelText('Счёт / касса'), '103');
    await user.type(screen.getByLabelText('Плательщик'), 'Ахмедова Айтан Рафик Кызы');
    await user.type(screen.getByLabelText('Основание'), 'Оплата по счёту №33');

    await user.click(getLastButton('Сохранить'));

    await waitFor(() =>
      expect(createCashOrderMock).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 0,
          number: '',
          type: 'Incoming',
          status: 'Draft',
          amount: 25000,
          currency: 'RUB',
          account: 'Основная касса',
          selectedMoneyAccountId: 103,
          payerName: 'Ахмедова Айтан Рафик Кызы',
          basis: 'Оплата по счёту №33',
        }),
      ),
    );
    expect(updateCashOrderMock).not.toHaveBeenCalled();
    expect(toastSuccessMock).toHaveBeenCalledWith('Кассовый ордер создан.', 'Касса');
  }, 15000);

  it('does not create a cash order with zero amount', async () => {
    const user = userEvent.setup();
    operations = [];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'Создать ордер' }));
    await screen.findByText('Приходный кассовый ордер Новый');

    await user.click(getLastButton('Сохранить'));

    expect(await screen.findByText('Сумма кассового ордера должна быть больше нуля.')).toBeTruthy();
    expect(createCashOrderMock).not.toHaveBeenCalled();
    expect(updateCashOrderMock).not.toHaveBeenCalled();
  }, 15000);

  it('requires selecting a cash account for a new standalone order', async () => {
    const user = userEvent.setup();
    operations = [];
    render(<MoneyOperations source="Cash" />);
    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'Создать ордер' }));
    await screen.findByText('Приходный кассовый ордер Новый');
    await user.clear(screen.getByLabelText('Сумма'));
    await user.type(screen.getByLabelText('Сумма'), '100');
    await user.selectOptions(screen.getByLabelText('Счёт / касса'), '');
    await user.click(getLastButton('Сохранить'));
    expect(await screen.findByText('Выберите кассу для нового ордера.')).toBeTruthy();
    expect(createCashOrderMock).not.toHaveBeenCalled();
  }, 15000);

  it('posts a draft cash order from the modal', async () => {
    const user = userEvent.setup();
    operations = [];
    getCashOrderRegisterMock.mockResolvedValueOnce({
      items: [
        {
          id: 12,
          number: 'ПКО-2026-0005',
          type: 'Incoming',
          status: 'Draft',
          date: '2026-04-29',
          amount: 1500,
          currency: 'RUB',
          account: 'Основная касса',
          payerName: 'Черновой клиент',
          receiverName: 'Основная касса',
          basis: 'Черновик приёма наличных',
          createdAt: '2026-04-29T10:00:00Z',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 10,
      incomingCount: 1,
      outgoingCount: 0,
      draftCount: 1,
      postedCount: 0,
      cancelledCount: 0,
      incomingAmount: 0,
      outgoingAmount: 0,
    });
    getCashOrderMock.mockResolvedValueOnce({
      id: 12,
      number: 'ПКО-2026-0005',
      type: 'Incoming',
      status: 'Draft',
      date: '2026-04-29',
      amount: 1500,
      currency: 'RUB',
      clientId: null,
      clientName: null,
      paymentId: null,
      moneyOperationId: null,
      invoiceId: null,
      account: 'Основная касса',
      payerName: 'Черновой клиент',
      receiverName: 'Основная касса',
      basis: 'Черновик приёма наличных',
      appendix: null,
      comment: null,
      cancelledAt: null,
      cancelledReason: null,
      createdAt: '2026-04-29T10:00:00Z',
      updatedAt: null,
    });

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('ПКО-2026-0005');
    await user.click(screen.getByRole('button', { name: 'ПКО-2026-0005' }));
    await screen.findByText('Черновик');
    await user.click(getLastButton('Провести'));

    await waitFor(() => expect(postCashOrderMock).toHaveBeenCalledWith(12));
    expect(toastSuccessMock).toHaveBeenCalledWith('Кассовый ордер проведён.', 'Касса');
  }, 15000);

  it('filters the cash orders journal by cancelled status', async () => {
    const user = userEvent.setup();
    operations = [];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'Отменён' }));

    await waitFor(() =>
      expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(
        expect.objectContaining({
          status: 'Cancelled',
          page: 1,
          pageSize: 10,
        }),
        expectAbortOptions(),
      ),
    );
  }, 15000);

  it('filters the cash orders journal by draft status', async () => {
    const user = userEvent.setup();
    operations = [];

    render(<MoneyOperations source="Cash" />);

    await screen.findByText('Кассовые ордера');
    await user.click(screen.getByRole('button', { name: 'Черновики' }));

    await waitFor(() =>
      expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(
        expect.objectContaining({
          status: 'Draft',
          page: 1,
          pageSize: 10,
        }),
        expectAbortOptions(),
      ),
    );
  }, 15000);

  it.each(['Bank', 'Total'] as const)('does not present mixed currencies as a total in %s', async source => {
    operations = [createOperation({ id: 1, direction: 'Income', amount: 123, currency: 'USD' }),
      createOperation({ id: 2, direction: 'Income', amount: 456, currency: 'RUB' })];
    render(<MoneyOperations source={source} />);
    expect(await screen.findByText(/Итоги недоступны: в выбранных операциях разные валюты/)).toBeTruthy();
    expect(screen.queryByText('579,00 ₽')).toBeNull();
    expect(screen.queryByText('579,00 USD')).toBeNull();
  });

  it.each(['Bank', 'Total'] as const)('shows a single foreign currency in the %s summary', async source => {
    operations = [createOperation({ id: 1, direction: 'Income', amount: 123, currency: 'USD' })];
    render(<MoneyOperations source={source} />);
    expect((await screen.findAllByText('123,00 USD')).length).toBeGreaterThan(0);
    expect(screen.queryByText('123,00 ₽')).toBeNull();
  });

  it('does not present an operation of unknown currency as a confirmed total', async () => {
    operations = [{ ...createOperation({ id: 1, direction: 'Income', amount: 321 }), currency: null }];
    render(<MoneyOperations source="Bank" />);
    expect(await screen.findByText(/Итоги недоступны: в выбранных операциях разные валюты/)).toBeTruthy();
    expect(screen.queryByText('321,00 ₽')).toBeNull();
  });

  it('shows each journal operation amount with its currency or an explicit unknown label', async () => {
    testColumnState.extra = true;
    operations = [
      { ...createOperation({ id: 1, direction: 'Income', amount: 123, currency: 'USD' }), vatAmount: 7 },
      { ...createOperation({ id: 2, direction: 'Expense', amount: 456 }), currency: null, vatAmount: 8 },
    ];
    render(<MoneyOperations source="Bank" />);
    expect(await screen.findByText('123,00 USD')).toBeTruthy();
    expect(screen.getByText('7,00 USD')).toBeTruthy();
    expect(screen.getByText('456,00 валюта не указана')).toBeTruthy();
    expect(screen.getByText('8,00 валюта не указана')).toBeTruthy();
  });

  it('shows currency for each operation in the combined journal', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({ id: 1, direction: 'Income', amount: 123, currency: 'USD' }),
      { ...createOperation({ id: 2, direction: 'Expense', amount: 456 }), currency: null },
    ];
    render(<MoneyOperations source="Total" />);
    await screen.findByText('Общий баланс');
    await user.click(screen.getByRole('button', { name: 'ОДДС' }));
    expect(await screen.findByText('+ 123,00 USD')).toBeTruthy();
    expect(screen.getByText('- 456,00 валюта не указана')).toBeTruthy();
  });

  it('shows the actual or unknown currency in the imported operation summary', async () => {
    const user = userEvent.setup();
    importBankStatementMock.mockResolvedValueOnce({ batchId: null, rowsRead: 2, created: 2, skipped: 0, errors: [], operations: [
      { id: 1, date: '2026-04-29', direction: 'Income', amount: 123, currency: 'USD' },
      { id: 2, date: '2026-04-29', direction: 'Expense', amount: 456, currency: null },
    ] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    expect(await screen.findByText('123,00 USD')).toBeTruthy();
    expect(screen.getByText('456,00 валюта не указана')).toBeTruthy();
  }, 15000);

  it('imports a bank statement file and reloads bank operations', async () => {
    const user = userEvent.setup();
    operations = [];
    importBankStatementMock.mockResolvedValueOnce({
      batchId: 1,
      rowsRead: 2,
      created: 2,
      skipped: 0,
      errors: [],
      operations: [
        {
          id: 301,
          date: '2026-04-29',
          direction: 'Income',
          amount: 25000,
          counterpartyName: 'Ахмедова Айтан Рафик Кызы',
          paymentOrderNumber: '101',
        },
      ],
    });
    getMoneyOperationMock.mockResolvedValueOnce(createOperation({
      id: 301,
      source: 'Bank',
      direction: 'Income',
      amount: 25000,
      counterpartyName: 'Ахмедова Айтан Рафик Кызы',
      paymentOrderNumber: '101',
    }));

    const { container } = render(<MoneyOperations source="Bank" />);

    await screen.findByText('Загрузить выписку');
    await selectBankImportAccount(user, '102');
    const requestsBeforeImport = getMoneyOperationsPagedMock.mock.calls.length;
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    expect(input.accept).toContain('.1c');
    const file = new File(['Дата;Сумма\n29.04.2026;25000'], 'statement.csv', { type: 'text/csv' });
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => expect(importBankStatementMock).toHaveBeenCalledWith(file, { companyId: 1, moneyAccountId: 102 }));
    await screen.findByText('Импорт выписки: прочитано 2, создано 2, пропущено 0');
    await screen.findByText('Импорт банковской выписки');
    await user.click(screen.getByRole('button', { name: 'Закрыть импорт' }));
    await screen.findByText('Ахмедова Айтан Рафик Кызы');
    await screen.findByText('101');
    await userEvent.click(screen.getByRole('button', { name: 'Открыть операцию импорта 301' }));
    await waitFor(() => expect(getMoneyOperationMock).toHaveBeenCalledWith(301, expect.objectContaining({ aborted: false })));
    await screen.findByText('Редактировать операцию по банку');
    expect(toastSuccessMock).toHaveBeenCalledWith('Создано операций: 2. Пропущено: 0.', 'Выписка банка');
    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(requestsBeforeImport + 1));
  }, 15000);

  it.each([
    { companyName: 'Компания из сохранённого пакета', moneyAccountName: 'Счёт из сохранённого пакета',
      periodFrom: '2026-10-01T00:00:00', periodTo: '2026-10-28T00:00:00',
      expectedCompany: 'Компания из сохранённого пакета', expectedAccount: 'Счёт из сохранённого пакета',
      expectedFrom: 'с 01.10.2026', expectedTo: 'по 28.10.2026' },
    { companyName: null, moneyAccountName: ' ', periodFrom: null, periodTo: null,
      expectedCompany: 'Не определена', expectedAccount: 'Не определён', expectedFrom: null, expectedTo: null },
    { companyName: '', moneyAccountName: null, periodFrom: '2026-10-01', periodTo: null,
      expectedCompany: 'Не определена', expectedAccount: 'Не определён', expectedFrom: 'с 01.10.2026', expectedTo: null },
    { companyName: 'Компания из сохранённого пакета', moneyAccountName: 'Счёт из сохранённого пакета',
      periodFrom: null, periodTo: '2026-10-28', expectedCompany: 'Компания из сохранённого пакета',
      expectedAccount: 'Счёт из сохранённого пакета', expectedFrom: null, expectedTo: 'по 28.10.2026' },
  ])('shows saved bank import identity and period without inferring missing values: $expectedFrom / $expectedTo', async (scenario) => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementation(async (id: number) => ({ ...await original(id),
      companyName: scenario.companyName, moneyAccountName: scenario.moneyAccountName,
      periodFrom: scenario.periodFrom, periodTo: scenario.periodTo }));
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    const identity = within(dialog).getByLabelText('Реквизиты выписки');
    expect(within(identity).getByText(scenario.expectedCompany)).toBeTruthy();
    expect(within(identity).getByText(scenario.expectedAccount)).toBeTruthy();
    expect(within(identity).queryByText('Основной счет')).toBeNull();
    if (scenario.expectedFrom) expect(within(identity).getByText(scenario.expectedFrom)).toBeTruthy();
    if (scenario.expectedTo) expect(within(identity).getByText(scenario.expectedTo)).toBeTruthy();
    expect(within(identity).queryByText('Не указан') !== null).toBe(!scenario.expectedFrom && !scenario.expectedTo);
    expect(rollbackBankStatementImportMock).not.toHaveBeenCalled();
    expect(reprocessBankStatementImportMock).not.toHaveBeenCalled();
  }, 15000);

  it('opens an imported batch automatically and closing it does not roll back or reprocess it', async () => {
    const user = userEvent.setup();
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    getBankStatementImportsMock.mockResolvedValue({ items: [{ id: 1, fileName: 'statement.csv', status: 'Applied', rowsRead: 1,
      createdRows: 1, skippedRows: 0, errorRows: 0, rollbackRows: 0, createdAt: '2026-04-29T10:00:00Z', rolledBackAt: null }],
    total: 1, page: 1, pageSize: 5 });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    await screen.findByText('Импорт банковской выписки');
    expect(getBankStatementImportMock).toHaveBeenCalledWith(1);
    await user.click(screen.getByRole('button', { name: 'Закрыть импорт' }));
    expect(screen.queryByText('Импорт банковской выписки')).toBeNull();
    expect(rollbackBankStatementImportMock).not.toHaveBeenCalled();
    expect(reprocessBankStatementImportMock).not.toHaveBeenCalled();
    await user.click(await screen.findByRole('button', { name: 'Открыть импорт выписки statement.csv' }));
    expect(await screen.findByText('Импорт банковской выписки')).toBeTruthy();
  }, 15000);

  it('does not open a late import detail after the employee loses import permission', async () => {
    const user = userEvent.setup();
    let complete!: (value: unknown) => void;
    const detail = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container, rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    await waitFor(() => expect(getBankStatementImportMock).toHaveBeenCalledWith(1));
    permissionsState.paymentsCanCreate = false;
    rerender(<MoneyOperations source="Bank" />);
    await act(async () => complete(await detail(1)));
    expect(screen.queryByText('Импорт банковской выписки')).toBeNull();
  }, 15000);

  it('clears an already opened import result when import permission is revoked', async () => {
    const user = userEvent.setup();
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container, rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    await screen.findByText('Импорт банковской выписки');
    permissionsState.paymentsCanCreate = false;
    rerender(<MoneyOperations source="Bank" />);
    await waitFor(() => expect(screen.queryByText('Импорт банковской выписки')).toBeNull());
    expect(screen.queryByText(/Импорт выписки: прочитано/)).toBeNull();
  }, 15000);

  it('focuses the import dialog, traps keyboard navigation and closes on Escape without changing the batch', async () => {
    const user = userEvent.setup();
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    const opener = screen.getByRole('button', { name: 'Загрузить выписку' });
    await user.click(opener);
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    const close = within(dialog).getByRole('button', { name: 'Закрыть импорт' });
    const rollback = within(dialog).getByRole('button', { name: /Откатить импорт выписки/ });
    await waitFor(() => expect(document.activeElement).toBe(close));
    await user.tab();
    expect(document.activeElement).toBe(within(dialog).getByRole('searchbox'));
    await user.tab();
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Найти' }));
    await user.tab();
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 2' }));
    await user.tab();
    expect(document.activeElement).toBe(rollback);
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 2' }));
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Найти' }));
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(within(dialog).getByRole('searchbox'));
    await user.tab({ shift: true });
    expect(document.activeElement).toBe(close);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
    expect(document.activeElement).toBe(opener);
    expect(rollbackBankStatementImportMock).not.toHaveBeenCalled();
    expect(reprocessBankStatementImportMock).not.toHaveBeenCalled();
  }, 15000);

  describe('bank import row operation navigation', () => {
    const prepare = () => {
      const original = getBankStatementImportMock.getMockImplementation()!;
      getBankStatementImportMock.mockImplementation(async (id: number, page = 1, search?: string) => {
        const detail = await original(id);
        return { ...detail, companyId: 1, moneyAccountId: 101, rowsTotal: 130, rowsPage: page, rowsPageSize: 50,
          rowsSearch: search ?? null, rowsMatched: search ? 70 : null, hasReprocessableRows: true,
          rows: [{ ...detail.rows[0], id: page, rowNumber: page === 2 ? 51 : 1, direction: 'Expense', moneyOperationId: 44 }] };
      });
      getBankStatementImportsMock.mockImplementation(async () => ({ items: [await original(1)], total: 1, page: 1, pageSize: 5 }));
      importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 130, created: 129, skipped: 1, errors: [], operations: [] });
      const operation = createOperation({ id: 44, moneyAccountId: 101, amount: 25000, date: '2026-04-29',
        purpose: 'Свежие данные операции', isAdvancePayment: false, paidAmount: 0, paidInvoiceNumber: '', paidAccountName: '', matchStatus: 'ClarificationRequired' });
      getMoneyOperationMock.mockResolvedValue(operation);
      return operation;
    };
    const openBatch = async (user: ReturnType<typeof userEvent.setup>) => {
      const rendered = render(<MoneyOperations source="Bank" />);
      await selectBankImportAccount(user);
      await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
      fireEvent.change(rendered.container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
      const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
      return { ...rendered, dialog };
    };
    const filteredSecondPage = async (user: ReturnType<typeof userEvent.setup>, dialog: HTMLElement) => {
      fireEvent.change(within(dialog).getByRole('searchbox'), { target: { value: 'ёжик' } });
      await user.click(within(dialog).getByRole('button', { name: 'Найти' }));
      await within(dialog).findByText('Поиск: «ёжик». Найдено: 70 из 130.');
      await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
      await within(dialog).findByText(/Страница 2 из 2/);
    };
    const candidates = () => [201, 202].map(id => ({ invoiceId: id, paymentId: id * 10, invoiceNumber: String(id),
      amount: 25000, paidAmount: 0, outstandingAmount: 25000, status: 'Pending', matchReason: 'Нужен выбор бухгалтера', score: 100 }));

    const candidatePage = (cursor?: string, search?: string) => ({
      items: candidates().map(item => ({ ...item, invoiceId: item.invoiceId + (cursor ? 100 : search ? 700 : 0),
        invoiceNumber: String(item.invoiceId + (cursor ? 100 : search ? 700 : 0)) })),
      cursor: cursor ?? null, search: search ?? null, hasMore: !cursor && !search, nextCursor: !cursor && !search ? 'page-two' : null,
    });

    it('pages invoice candidates forward and back without applying a draft search or selecting a document', async () => {
      const user = userEvent.setup();
      prepare();
      getMoneyOperationInvoiceCandidatePageMock.mockImplementation(async (_id, search, cursor) => candidatePage(cursor, search));
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      fireEvent.change(within(editor).getByRole('searchbox'), { target: { value: 'черновик' } });
      await user.click(within(editor).getByRole('button', { name: 'Далее по счетам' }));
      await within(editor).findByRole('button', { name: /Счёт 301/ });
      expect(getMoneyOperationInvoiceCandidatePageMock).toHaveBeenLastCalledWith(44, undefined, 'page-two', expect.any(AbortSignal));
      expect(within(editor).getByRole('group', { name: 'Страницы счетов для сверки' }).textContent).toContain('Страница 2');
      expect(within(editor).getByLabelText('ID счёта')).toHaveProperty('value', '');
      expect(within(editor).getByRole('searchbox')).toHaveProperty('value', 'черновик');
      expect(within(editor).getByRole('button', { name: 'Далее по счетам' })).toHaveProperty('disabled', true);
      await user.click(within(editor).getByRole('button', { name: 'Назад к счетам' }));
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      expect(getMoneyOperationInvoiceCandidatePageMock).toHaveBeenLastCalledWith(44, undefined, undefined, expect.any(AbortSignal));
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await within(editor).findByRole('button', { name: /Счёт 901/ });
      expect(within(editor).getByRole('group', { name: 'Страницы счетов для сверки' }).textContent).toContain('Страница 1');
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    });

    it('retains the visible invoice page on a failed next request and retries that cursor', async () => {
      const user = userEvent.setup();
      prepare();
      let fail = true;
      getMoneyOperationInvoiceCandidatePageMock.mockImplementation(async (_id, search, cursor) => {
        if (cursor && fail) throw new Error('connection failed');
        return candidatePage(cursor, search);
      });
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      await user.click(within(editor).getByRole('button', { name: 'Далее по счетам' }));
      await within(editor).findByRole('alert');
      expect(within(editor).getByRole('button', { name: /Счёт 201/ })).toBeTruthy();
      expect(within(editor).getByRole('group', { name: 'Страницы счетов для сверки' }).textContent).toContain('Страница 1');
      fail = false;
      await user.click(within(editor).getByRole('button', { name: 'Далее по счетам' }));
      await within(editor).findByRole('button', { name: /Счёт 301/ });
      expect(within(editor).queryByRole('alert')).toBeNull();
      expect(getMoneyOperationInvoiceCandidatePageMock).toHaveBeenLastCalledWith(44, undefined, 'page-two', expect.any(AbortSignal));
    });

    it.each(['search', 'cursor', 'legacy-array', 'oversized', 'loop'] as const)('rejects an unsupported invoice page response: %s', async mode => {
      const user = userEvent.setup();
      prepare();
      getMoneyOperationInvoiceCandidatePageMock.mockImplementation(async (_id, search, cursor) => {
        if (!cursor) return candidatePage(cursor, search);
        const page = candidatePage(cursor, search);
        if (mode === 'search') return { ...page, search: 'другой' };
        if (mode === 'cursor') return { ...page, cursor: 'другой' };
        if (mode === 'legacy-array') return page.items;
        if (mode === 'oversized') return { ...page, items: Array.from({ length: 11 }, () => page.items[0]) };
        return { ...page, hasMore: true, nextCursor: cursor };
      });
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      await user.click(within(editor).getByRole('button', { name: 'Далее по счетам' }));
      await within(editor).findByRole('alert');
      expect(within(editor).queryByRole('button', { name: /Счёт 301/ })).toBeNull();
      expect(within(editor).getByRole('button', { name: /Счёт 201/ })).toBeTruthy();
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    });

    it('cancels the pending invoice page when applying a different search and discards its late result', async () => {
      const user = userEvent.setup();
      prepare();
      let complete!: (page: ReturnType<typeof candidatePage>) => void;
      getMoneyOperationInvoiceCandidatePageMock.mockImplementation(async (_id, search, cursor) => cursor
        ? new Promise(resolve => { complete = resolve; }) : candidatePage(cursor, search));
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      await user.click(within(editor).getByRole('button', { name: 'Далее по счетам' }));
      await waitFor(() => expect(complete).toBeDefined());
      const signal = getMoneyOperationInvoiceCandidatePageMock.mock.calls.find(call => call[2] === 'page-two')![3];
      fireEvent.change(within(editor).getByRole('searchbox'), { target: { value: 'Ёжик' } });
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await within(editor).findByRole('button', { name: /Счёт 901/ });
      expect(signal.aborted).toBe(true);
      await act(async () => complete(candidatePage('page-two')));
      expect(within(editor).queryByRole('button', { name: /Счёт 301/ })).toBeNull();
      expect(within(editor).getByRole('button', { name: /Счёт 901/ })).toBeTruthy();
    });

    it('saves the invoice explicitly selected from the second page with unchanged bank facts', async () => {
      const user = userEvent.setup();
      const operation = prepare();
      getMoneyOperationInvoiceCandidatePageMock.mockImplementation(async (_id, search, cursor) => candidatePage(cursor, search));
      updateMoneyOperationMock.mockImplementation(async (_id, payload) => payload);
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      await user.click(within(editor).getByRole('button', { name: 'Далее по счетам' }));
      await user.click(await within(editor).findByRole('button', { name: /Счёт 301/ }));
      await user.click(within(editor).getByRole('button', { name: 'Сохранить' }));
      await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
      expect(updateMoneyOperationMock).toHaveBeenCalledWith(44, expect.objectContaining({ linkedInvoiceId: 301, matchStatus: 'Matched',
        moneyAccountId: 101, amount: operation.amount, date: operation.date, currency: operation.currency, direction: operation.direction }));
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    });

    it.each(['permission', 'close'] as const)('cancels a pending invoice page after %s and ignores its completion', async mode => {
      const user = userEvent.setup();
      prepare();
      let complete!: (page: ReturnType<typeof candidatePage>) => void;
      getMoneyOperationInvoiceCandidatePageMock.mockImplementation(async (_id, search, cursor) => cursor
        ? new Promise(resolve => { complete = resolve; }) : candidatePage(cursor, search));
      const { dialog, rerender } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      await user.click(within(editor).getByRole('button', { name: 'Далее по счетам' }));
      await waitFor(() => expect(complete).toBeDefined());
      const signal = getMoneyOperationInvoiceCandidatePageMock.mock.calls.find(call => call[2] === 'page-two')![3];
      if (mode === 'permission') { permissionsState.paymentsCanEdit = false; rerender(<MoneyOperations source="Bank" />); }
      else await user.click(within(editor).getByRole('button', { name: 'Закрыть форму денежной операции' }));
      await waitFor(() => expect(signal.aborted).toBe(true));
      await act(async () => complete(candidatePage('page-two')));
      expect(screen.queryByRole('dialog', { name: 'Редактировать операцию по банку' })).toBeNull();
      expect(screen.queryByRole('button', { name: /Счёт 301/ })).toBeNull();
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    });

    it('searches invoices only on explicit submission and resets without selecting or saving', async () => {
      const user = userEvent.setup();
      prepare();
      getMoneyOperationInvoiceCandidatesMock.mockResolvedValue(candidates());
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      const previousCount = getMoneyOperationInvoiceCandidatesMock.mock.calls.length;
      fireEvent.change(within(editor).getByRole('searchbox'), { target: { value: '  Ёжик &%_' } });
      expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenCalledTimes(previousCount);
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await waitFor(() => expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenLastCalledWith(44, 'Ёжик &%_', expect.any(AbortSignal)));
      expect(getMoneyOperationInvoiceCandidatesMock.mock.calls[0][2]?.aborted).toBe(true);
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      expect(within(editor).getByLabelText('ID счёта')).toHaveProperty('value', '');
      await user.click(within(editor).getByRole('button', { name: 'Сбросить поиск счетов' }));
      await waitFor(() => expect(getMoneyOperationInvoiceCandidatesMock).toHaveBeenLastCalledWith(44, undefined, expect.any(AbortSignal)));
      expect(within(editor).getByRole('searchbox')).toHaveProperty('value', '');
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    });

    it('discards an older invoice search response and cancels its request', async () => {
      const user = userEvent.setup();
      prepare();
      let complete!: (items: ReturnType<typeof candidates>) => void;
      getMoneyOperationInvoiceCandidatesMock.mockImplementation(async (_id, query) => query === 'первый'
        ? new Promise(resolve => { complete = resolve; }) : candidates().filter(item => item.invoiceId === 202));
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      const input = within(editor).getByRole('searchbox');
      fireEvent.change(input, { target: { value: 'первый' } });
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await waitFor(() => expect(complete).toBeDefined());
      const firstSignal = getMoneyOperationInvoiceCandidatesMock.mock.calls.find(call => call[1] === 'первый')![2];
      fireEvent.change(input, { target: { value: 'второй' } });
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await within(editor).findByRole('button', { name: /Счёт 202/ });
      expect(firstSignal?.aborted).toBe(true);
      await act(async () => complete(candidates().filter(item => item.invoiceId === 201)));
      expect(within(editor).queryByRole('button', { name: /Счёт 201/ })).toBeNull();
      expect(within(editor).getByRole('button', { name: /Счёт 202/ })).toBeTruthy();
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    });

    it('shows a failed invoice search separately from no matches and permits retry', async () => {
      const user = userEvent.setup();
      prepare();
      let failed = true;
      getMoneyOperationInvoiceCandidatesMock.mockImplementation(async (_id, query) => {
        if (query && failed) throw new Error('404 old server');
        return candidates();
      });
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      fireEvent.change(within(editor).getByRole('searchbox'), { target: { value: 'Счёт в хвосте' } });
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await within(editor).findByRole('alert');
      expect(within(editor).queryByText(/По запросу счета не найдены/)).toBeNull();
      expect(within(editor).queryByRole('button', { name: /Счёт 201/ })).toBeNull();
      failed = false;
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await within(editor).findByRole('button', { name: /Счёт 201/ });
      expect(within(editor).queryByRole('alert')).toBeNull();
      expect(within(editor).getByLabelText('ID счёта')).toHaveProperty('value', '');
    });

    it('saves only the explicitly selected searched invoice without changing bank facts', async () => {
      const user = userEvent.setup();
      const operation = prepare();
      const found = { ...candidates()[1], invoiceId: 999, invoiceNumber: 'Хвост-999' };
      getMoneyOperationInvoiceCandidatesMock.mockImplementation(async (_id, query) => query ? [found] : candidates());
      updateMoneyOperationMock.mockImplementation(async (_id, payload) => payload);
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      fireEvent.change(within(editor).getByRole('searchbox'), { target: { value: 'Хвост' } });
      await user.click(within(editor).getByRole('button', { name: 'Найти счёт' }));
      await user.click(await within(editor).findByRole('button', { name: /Счёт Хвост-999/ }));
      await user.click(within(editor).getByRole('button', { name: 'Сохранить' }));
      await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
      expect(updateMoneyOperationMock).toHaveBeenCalledWith(44, expect.objectContaining({
        linkedInvoiceId: 999, matchStatus: 'Matched', amount: operation.amount, date: operation.date,
        moneyAccountId: 101, source: 'Bank', direction: operation.direction, currency: operation.currency,
      }));
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    });

    it('loads the current operation and returns to the filtered page on Escape without saving', async () => {
      const user = userEvent.setup();
      prepare();
      operations = [createOperation({ id: 44, moneyAccountId: 101, purpose: 'Старые кешированные данные' })];
      getMoneyOperationInvoiceCandidatesMock.mockResolvedValue(candidates());
      const { dialog } = await openBatch(user);
      await filteredSecondPage(user, dialog);
      fireEvent.change(within(dialog).getByRole('searchbox'), { target: { value: 'Ещё не применённый запрос' } });
      const beforeOpen = getBankStatementImportMock.mock.calls.length;
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 51' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      expect(await within(editor).findByDisplayValue('Свежие данные операции')).toBeTruthy();
      expect(getMoneyOperationMock).toHaveBeenCalledWith(44, expect.objectContaining({ aborted: false }));
      expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
      await waitFor(() => expect(document.activeElement).toBe(within(editor).getByRole('button', { name: 'Закрыть форму денежной операции' })));
      expect(within(editor).getByLabelText('ID счёта')).toHaveProperty('value', '');
      await user.click(await within(editor).findByRole('button', { name: /Счёт 202/ }));
      expect(within(editor).getByLabelText('ID счёта')).toHaveProperty('value', '202');
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
      await user.keyboard('{Escape}');
      const restored = await screen.findByRole('dialog', { name: 'statement.csv' });
      expect(within(restored).getByText(/Страница 2 из 2/)).toBeTruthy();
      expect(within(restored).getByRole('searchbox')).toHaveProperty('value', 'Ещё не применённый запрос');
      expect(within(restored).getByText('Поиск: «ёжик». Найдено: 70 из 130.')).toBeTruthy();
      expect(getBankStatementImportMock).toHaveBeenCalledTimes(beforeOpen);
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it('saves only the explicitly selected invoice and refreshes the same package page', async () => {
      const user = userEvent.setup();
      prepare();
      getMoneyOperationInvoiceCandidatesMock.mockResolvedValue(candidates());
      const { dialog } = await openBatch(user);
      await filteredSecondPage(user, dialog);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 51' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await user.click(await within(editor).findByRole('button', { name: /Счёт 202/ }));
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
      await user.click(within(editor).getByRole('button', { name: 'Сохранить' }));
      await waitFor(() => expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1));
      expect(updateMoneyOperationMock).toHaveBeenCalledWith(44, expect.objectContaining({ linkedInvoiceId: 202, paidInvoiceNumber: '202',
        matchStatus: 'Matched', amount: 25000, currency: 'RUB', moneyAccountId: 101, source: 'Bank' }));
      const restored = await screen.findByRole('dialog', { name: 'statement.csv' });
      expect(within(restored).getByText(/Страница 2 из 2/)).toBeTruthy();
      expect(getBankStatementImportMock).toHaveBeenLastCalledWith(1, 2, 'ёжик', expect.objectContaining({ aborted: false }));
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it.each(['account', 'source', 'identity', 'readonly', 'denied'] as const)('keeps the package open when the loaded operation is invalid: %s', async (mode) => {
      const user = userEvent.setup();
      const operation = prepare();
      if (mode === 'denied') getMoneyOperationMock.mockRejectedValueOnce(new Error('Нет доступа к операции'));
      else getMoneyOperationMock.mockResolvedValueOnce({ ...operation, ...(mode === 'account' ? { moneyAccountId: 102 }
        : mode === 'source' ? { source: 'Cash' } : mode === 'identity' ? { id: 45 } : { isReadonly: true }) });
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      await waitFor(() => expect(toastErrorMock).toHaveBeenCalled());
      expect(screen.getByRole('dialog', { name: 'statement.csv' })).toBeTruthy();
      expect(screen.queryByRole('dialog', { name: 'Редактировать операцию по банку' })).toBeNull();
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it.each(['close', 'revoke', 'source'] as const)('discards a late operation open after %s', async (mode) => {
      const user = userEvent.setup();
      const operation = prepare();
      let complete!: (value: MoneyOperation) => void;
      let signal!: AbortSignal;
      getMoneyOperationMock.mockImplementationOnce((_id: number, cancellation: AbortSignal) => { signal = cancellation; return new Promise<MoneyOperation>(resolve => { complete = resolve; }); });
      const { dialog, rerender } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      await waitFor(() => expect(complete).toBeDefined());
      if (mode === 'close') await user.click(within(dialog).getByRole('button', { name: 'Закрыть импорт' }));
      else if (mode === 'revoke') { permissionsState.paymentsCanEdit = false; rerender(<MoneyOperations source="Bank" />); }
      else rerender(<MoneyOperations source="Cash" />);
      expect(signal.aborted).toBe(true);
      await act(async () => complete(operation));
      expect(screen.queryByRole('dialog', { name: 'Редактировать операцию по банку' })).toBeNull();
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it('requires edit permission and never creates an operation for a row without a reference', async () => {
      const user = userEvent.setup();
      prepare();
      permissionsState.paymentsCanEdit = false;
      const original = getBankStatementImportMock.getMockImplementation()!;
      getBankStatementImportMock.mockImplementation(async (...args: unknown[]) => {
        const detail = await original(...args);
        return { ...detail, rows: [...detail.rows, { ...detail.rows[0], id: 999, rowNumber: 2, moneyOperationId: null, status: 'Error' }] };
      });
      const { dialog } = await openBatch(user);
      expect(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' })).toHaveProperty('disabled', true);
      expect(within(dialog).queryByRole('button', { name: 'Открыть денежную операцию строки 2' })).toBeNull();
      expect(within(dialog).getByText('Операция не создана')).toBeTruthy();
      expect(getMoneyOperationMock).not.toHaveBeenCalled();
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it.each(['permission', 'source'] as const)('closes the bank operation editor when its %s changes', async (mode) => {
      const user = userEvent.setup();
      prepare();
      const { dialog, rerender } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      if (mode === 'permission') { permissionsState.paymentsCanEdit = false; rerender(<MoneyOperations source="Bank" />); }
      else rerender(<MoneyOperations source="Cash" />);
      await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Редактировать операцию по банку' })).toBeNull());
      expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it('locks bank movement facts while keeping classification and explicit invoice selection available', async () => {
      const user = userEvent.setup();
      prepare();
      getMoneyOperationInvoiceCandidatesMock.mockResolvedValue(candidates());
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      expect(within(editor).getByLabelText('Сумма, RUB')).toHaveProperty('disabled', true);
      expect(within(editor).getByLabelText('Дата')).toHaveProperty('disabled', true);
      expect(within(editor).getByLabelText('Номер ПП')).toHaveProperty('disabled', true);
      expect(within(editor).getByRole('combobox', { name: /^Банковский счёт\./ })).toHaveProperty('disabled', true);
      expect(within(editor).getByRole('combobox', { name: /^Тип учёта денежной операции\./ })).toHaveProperty('disabled', true);
      expect(within(editor).getByRole('combobox', { name: /^Количество денежных операций\./ })).toHaveProperty('disabled', true);
      expect(within(editor).getByRole('radio', { name: 'Расход' })).toHaveProperty('disabled', true);
      expect(within(editor).getByLabelText('Статья расхода')).not.toHaveProperty('disabled', true);
      expect(within(editor).getByLabelText('ID счёта')).not.toHaveProperty('disabled', true);
      expect(await within(editor).findByRole('button', { name: /Счёт 202/ })).not.toHaveProperty('disabled', true);
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it.each(['permission', 'new-editor'] as const)('discards an old operation save response after %s', async (mode) => {
      const user = userEvent.setup();
      const operation = prepare();
      getMoneyOperationInvoiceCandidatesMock.mockResolvedValue(candidates());
      let complete!: (value: MoneyOperation) => void;
      updateMoneyOperationMock.mockImplementationOnce(() => new Promise<MoneyOperation>(resolve => { complete = resolve; }));
      const { dialog, rerender } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      await user.click(await within(editor).findByRole('button', { name: /Счёт 202/ }));
      await user.click(within(editor).getByRole('button', { name: 'Сохранить' }));
      await waitFor(() => expect(complete).toBeDefined());
      const detailCalls = getBankStatementImportMock.mock.calls.length;
      if (mode === 'permission') { permissionsState.paymentsCanEdit = false; rerender(<MoneyOperations source="Bank" />); }
      else {
        await user.click(within(editor).getByRole('button', { name: 'Закрыть форму денежной операции' }));
        const restored = await screen.findByRole('dialog', { name: 'statement.csv' });
        await user.click(within(restored).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
        await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      }
      await act(async () => complete({ ...operation, linkedInvoiceId: 202, matchStatus: 'Matched' }));
      if (mode === 'permission') expect(screen.queryByRole('dialog', { name: 'Редактировать операцию по банку' })).toBeNull();
      else expect(screen.getByRole('dialog', { name: 'Редактировать операцию по банку' })).toBeTruthy();
      expect(getBankStatementImportMock).toHaveBeenCalledTimes(detailCalls);
      expect(updateMoneyOperationMock).toHaveBeenCalledTimes(1);
      expect(createMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);

    it.each(['missing', 'null', 'USD'] as const)('preserves the operation currency without assuming rubles: %s', async (mode) => {
      const user = userEvent.setup();
      const operation = prepare();
      if (mode === 'missing') delete operation.currency;
      else operation.currency = mode === 'null' ? null : 'USD';
      const { dialog } = await openBatch(user);
      await user.click(within(dialog).getByRole('button', { name: 'Открыть денежную операцию строки 1' }));
      const editor = await screen.findByRole('dialog', { name: 'Редактировать операцию по банку' });
      expect(within(editor).getByLabelText(`Сумма, ${mode === 'USD' ? 'USD' : 'валюта не указана'}`)).toHaveProperty('value', '25000');
      expect(within(editor).getByLabelText('ID счёта')).toHaveProperty('disabled', true);
      expect(getMoneyOperationInvoiceCandidatesMock).not.toHaveBeenCalled();
      await user.click(within(editor).getByRole('button', { name: 'Закрыть форму денежной операции' }));
      expect(updateMoneyOperationMock).not.toHaveBeenCalled();
    }, 15000);
  });

  it.each([
    ['rollback', 'close'], ['rollback', 'revoke'], ['reprocess', 'close'], ['reprocess', 'revoke'],
  ] as const)('does not reopen bank import detail after a late %s response following %s', async (action, change) => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementation(async (id: number) => ({ ...await original(id), hasReprocessableRows: true }));
    let complete!: (value: unknown) => void;
    const mutation = action === 'rollback' ? rollbackBankStatementImportMock : reprocessBankStatementImportMock;
    mutation.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container, rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    await user.click(within(dialog).getByRole('button', { name: action === 'rollback' ? /Откатить импорт/ : /Повторить обработку/ }));
    await waitFor(() => expect(mutation).toHaveBeenCalledWith(1));
    const detailCalls = getBankStatementImportMock.mock.calls.length;
    if (change === 'close') await user.click(within(dialog).getByRole('button', { name: 'Закрыть импорт' }));
    else { permissionsState.paymentsCanCreate = false; rerender(<MoneyOperations source="Bank" />); }
    await act(async () => complete(action === 'rollback'
      ? { batchId: 1, status: 'RolledBack', deletedOperations: 1, skippedRows: 0, messages: [] }
      : { batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] }));
    expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
    expect(getBankStatementImportMock).toHaveBeenCalledTimes(detailCalls);
    expect(mutation).toHaveBeenCalledTimes(1);
  }, 15000);

  it.each([
    ['rollback', 'close'], ['rollback', 'revoke'], ['reprocess', 'close'], ['reprocess', 'revoke'],
  ] as const)('discards a late bank import detail refresh after %s and %s', async (action, change) => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementation(async (id: number) => ({ ...await original(id), hasReprocessableRows: true }));
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container, rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    let complete!: (value: unknown) => void;
    getBankStatementImportMock.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    await user.click(within(dialog).getByRole('button', { name: action === 'rollback' ? /Откатить импорт/ : /Повторить обработку/ }));
    await waitFor(() => expect(complete).toBeDefined());
    if (change === 'close') await user.click(within(dialog).getByRole('button', { name: 'Закрыть импорт' }));
    else { permissionsState.paymentsCanCreate = false; rerender(<MoneyOperations source="Bank" />); }
    await act(async () => complete(await original(1)));
    expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
  }, 15000);

  it.each(['close', 'revoke'] as const)('does not submit a delayed rollback confirmation after %s', async (change) => {
    const user = userEvent.setup();
    let approve!: (value: boolean) => void;
    dialogConfirmMock.mockImplementationOnce(() => new Promise<boolean>(resolve => { approve = resolve; }));
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container, rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    await user.click(within(dialog).getByRole('button', { name: /Откатить импорт/ }));
    await waitFor(() => expect(approve).toBeDefined());
    if (change === 'close') await user.click(within(dialog).getByRole('button', { name: 'Закрыть импорт' }));
    else { permissionsState.paymentsCanCreate = false; rerender(<MoneyOperations source="Bank" />); }
    await act(async () => approve(true));
    expect(rollbackBankStatementImportMock).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
  }, 15000);

  it.each(['rollback', 'reprocess'] as const)('retains the applied bank import row search after %s', async (action) => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementation(async (id: number, page = 1, search?: string) => ({ ...await original(id),
      rowsPage: page, rowsPageSize: 50, rowsTotal: 130, rowsSearch: search ?? null, rowsMatched: search ? 70 : null, hasReprocessableRows: true }));
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    fireEvent.change(within(dialog).getByRole('searchbox'), { target: { value: 'ёжик' } });
    await user.click(within(dialog).getByRole('button', { name: 'Найти' }));
    await within(dialog).findByText('Поиск: «ёжик». Найдено: 70 из 130.');
    await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
    await within(dialog).findByText(/Страница 2 из 2/);
    await user.click(within(dialog).getByRole('button', { name: action === 'rollback' ? /Откатить импорт/ : /Повторить обработку/ }));
    await waitFor(() => expect(getBankStatementImportMock).toHaveBeenLastCalledWith(1, 1, 'ёжик', expect.objectContaining({ aborted: false })));
    expect(within(dialog).getByText('Поиск: «ёжик». Найдено: 70 из 130.')).toBeTruthy();
    expect(within(dialog).getByText(/Страница 1 из 2/)).toBeTruthy();
    expect(action === 'rollback' ? rollbackBankStatementImportMock : reprocessBankStatementImportMock).toHaveBeenCalledTimes(1);
  }, 15000);

  it('searches the whole bank import from page one, preserves the search on paging and clears it explicitly', async () => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementation(async (id: number, page = 1, search?: string) => {
      const detail = await original(id);
      return { ...detail, rowsPage: page, rowsPageSize: 50, rowsTotal: 130, hasReprocessableRows: true,
        rowsSearch: search ?? null, rowsMatched: search ? 70 : null,
        rows: [{ ...detail.rows[0], counterpartyName: search ? `Совпадение ${page}` : `Без поиска ${page}` }] };
    });
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 130, created: 129, skipped: 0, errors: [], operations: [] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    expect(within(dialog).getByText('Поручение №101')).toBeTruthy();
    await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
    await within(dialog).findByText('Без поиска 2');
    fireEvent.change(within(dialog).getByRole('searchbox', { name: 'Поиск по строкам выписки' }), { target: { value: '  ЁЖИК &%_?  ' } });
    await user.click(within(dialog).getByRole('button', { name: 'Найти' }));
    expect(await within(dialog).findByText('Совпадение 1')).toBeTruthy();
    expect(getBankStatementImportMock).toHaveBeenLastCalledWith(1, 1, 'ЁЖИК &%_?', expect.objectContaining({ aborted: false }));
    expect(within(dialog).getByText('Поиск: «ЁЖИК &%_?». Найдено: 70 из 130.')).toBeTruthy();
    expect(within(dialog).getByText(/Страница 1 из 2/)).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: /Повторить обработку/ })).not.toHaveProperty('disabled', true);
    await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
    expect(await within(dialog).findByText('Совпадение 2')).toBeTruthy();
    expect(getBankStatementImportMock).toHaveBeenLastCalledWith(1, 2, 'ЁЖИК &%_?', expect.objectContaining({ aborted: false }));
    await user.click(within(dialog).getByRole('button', { name: 'Сбросить поиск' }));
    expect(await within(dialog).findByText('Без поиска 1')).toBeTruthy();
    expect(getBankStatementImportMock).toHaveBeenLastCalledWith(1);
    expect(within(dialog).getByRole('searchbox')).toHaveProperty('value', '');
    expect(rollbackBankStatementImportMock).not.toHaveBeenCalled();
    expect(reprocessBankStatementImportMock).not.toHaveBeenCalled();
  }, 15000);

  it.each(['request-error', 'missing-acknowledgement'] as const)('keeps the previous bank import page and search draft on %s', async (mode) => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    let attempts = 0;
    getBankStatementImportMock.mockImplementation(async (id: number, page = 1, search?: string) => {
      const detail = await original(id);
      if (search && attempts++ === 0) {
        if (mode === 'request-error') throw new Error('Поиск недоступен');
        return detail;
      }
      return { ...detail, rowsPage: page, rowsPageSize: 50, rowsTotal: 80, rowsSearch: search ?? null,
        rowsMatched: search ? 0 : null, rows: search ? [] : [{ ...detail.rows[0], counterpartyName: `Исходная страница ${page}` }] };
    });
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 80, created: 80, skipped: 0, errors: [], operations: [] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
    await within(dialog).findByText('Исходная страница 2');
    fireEvent.change(within(dialog).getByRole('searchbox'), { target: { value: 'новый запрос' } });
    await user.click(within(dialog).getByRole('button', { name: 'Найти' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(mode === 'request-error' ? 'Поиск недоступен'
      : 'Сервер не подтвердил поиск по выписке. Обновите приложение и повторите действие.', 'Выписка банка'));
    expect(within(dialog).getByText('Исходная страница 2')).toBeTruthy();
    expect(within(dialog).getByRole('searchbox')).toHaveProperty('value', 'новый запрос');
    await user.click(within(dialog).getByRole('button', { name: 'Найти' }));
    expect(await within(dialog).findByText('Строки импорта не найдены.')).toBeTruthy();
    expect(within(dialog).getByText('Поиск: «новый запрос». Найдено: 0 из 80.')).toBeTruthy();
    expect(within(dialog).queryByRole('button', { name: 'Далее' })).toBeNull();
  }, 15000);

  it.each(['readonly-view', 'visibility-narrowed', 'edit-only'] as const)(
    'discards bank import row search results after a distinct permission change: %s', async (mode) => {
      const user = userEvent.setup();
      const original = getBankStatementImportMock.getMockImplementation()!;
      if (mode === 'readonly-view') permissionsState.paymentsCanCreate = false;
      getBankStatementImportsMock.mockResolvedValue({ items: [await original(1)], total: 1, page: 1, pageSize: 5 });
      let complete!: (value: unknown) => void;
      let signal!: AbortSignal;
      getBankStatementImportMock.mockImplementation(async (id: number, _page?: number, search?: string, cancellation?: AbortSignal) => {
        if (search) { signal = cancellation!; return new Promise(resolve => { complete = resolve; }); }
        return original(id);
      });
      const { rerender } = render(<MoneyOperations source="Bank" />);
      await user.click(await screen.findByRole('button', { name: 'Открыть журнал импортов выписки' }));
      await user.click(await screen.findByRole('button', { name: 'Открыть импорт выписки statement.csv' }));
      const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
      fireEvent.change(within(dialog).getByRole('searchbox'), { target: { value: 'защищённый поиск' } });
      await user.click(within(dialog).getByRole('button', { name: 'Найти' }));
      await waitFor(() => expect(complete).toBeDefined());
      if (mode === 'readonly-view') permissionsState.paymentsCanView = false;
      else if (mode === 'visibility-narrowed') permissionsState.paymentsViewScope = 'own';
      else permissionsState.paymentsCanEdit = false;
      rerender(<MoneyOperations source="Bank" />);
      await act(async () => complete({ ...await original(1), rowsSearch: 'защищённый поиск', rowsMatched: 1 }));
      expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
      expect(signal.aborted).toBe(true);
    }, 15000,
  );

  it.each(['close', 'revoke'] as const)('aborts a bank import row search on %s and discards a late response', async (mode) => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    let complete!: (value: unknown) => void;
    let signal!: AbortSignal;
    getBankStatementImportMock.mockImplementation(async (id: number, _page?: number, search?: string, cancellation?: AbortSignal) => {
      if (search) { signal = cancellation!; return new Promise(resolve => { complete = resolve; }); }
      return original(id);
    });
    getBankStatementImportsMock.mockResolvedValue({ items: [await original(1)], total: 1, page: 1, pageSize: 5 });
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    const { container, rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    fireEvent.change(within(dialog).getByRole('searchbox'), { target: { value: 'поздний' } });
    await user.click(within(dialog).getByRole('button', { name: 'Найти' }));
    await waitFor(() => expect(signal).toBeDefined());
    if (mode === 'close') await user.click(within(dialog).getByRole('button', { name: 'Закрыть импорт' }));
    else { permissionsState.paymentsCanCreate = false; rerender(<MoneyOperations source="Bank" />); }
    await waitFor(() => expect(signal.aborted).toBe(true));
    await act(async () => complete({ ...await original(1), rowsSearch: 'поздний', rowsMatched: 1 }));
    expect(screen.queryByRole('dialog', { name: 'statement.csv' })).toBeNull();
    if (mode === 'close') {
      await user.click(screen.getByRole('button', { name: 'Открыть импорт выписки statement.csv' }));
      const reopened = await screen.findByRole('dialog', { name: 'statement.csv' });
      expect(within(reopened).getByRole('searchbox')).toHaveProperty('value', '');
    }
  }, 15000);

  it('pages a long imported batch while keeping actions available for errors on another page', async () => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementation(async (id: number, page?: number) => {
      const detail = await original(id);
      return { ...detail, rowsPage: page ?? 1, rowsPageSize: 50, rowsTotal: 101, hasReprocessableRows: true,
        rows: [{ ...detail.rows[0], id: page ?? 1, rowNumber: page === 2 ? 51 : 1, counterpartyName: page === 2 ? 'Вторая страница' : 'Первая страница' }] };
    });
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 101, created: 100, skipped: 1, errors: [], operations: [] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    expect(within(dialog).getByText('Первая страница')).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: /Повторить обработку/ })).not.toHaveProperty('disabled', true);
    await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
    expect(await within(dialog).findByText('Вторая страница')).toBeTruthy();
    expect(getBankStatementImportMock).toHaveBeenCalledWith(1, 2);
    expect(within(dialog).getByText(/Страница 2 из 3/)).toBeTruthy();
    await user.click(within(dialog).getByRole('button', { name: 'Назад' }));
    expect(await within(dialog).findByText('Первая страница')).toBeTruthy();
    expect(rollbackBankStatementImportMock).not.toHaveBeenCalled();
    expect(reprocessBankStatementImportMock).not.toHaveBeenCalled();
  }, 15000);

  it('keeps the current import page on a failed page request and allows retry', async () => {
    const user = userEvent.setup();
    const original = getBankStatementImportMock.getMockImplementation()!;
    let failed = false;
    getBankStatementImportMock.mockImplementation(async (id: number, page?: number) => {
      if (page === 2 && !failed) { failed = true; throw new Error('Страница недоступна'); }
      const detail = await original(id);
      return { ...detail, rowsPage: page ?? 1, rowsPageSize: 50, rowsTotal: 51,
        rows: [{ ...detail.rows[0], counterpartyName: page === 2 ? 'Вторая страница' : 'Первая страница' }] };
    });
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 51, created: 51, skipped: 0, errors: [], operations: [] });
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    const dialog = await screen.findByRole('dialog', { name: 'statement.csv' });
    await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Страница недоступна', 'Выписка банка'));
    expect(within(dialog).getByText('Первая страница')).toBeTruthy();
    await user.click(within(dialog).getByRole('button', { name: 'Далее' }));
    expect(await within(dialog).findByText('Вторая страница')).toBeTruthy();
  }, 15000);

  it('keeps the successful import result when opening its detail fails', async () => {
    const user = userEvent.setup();
    importBankStatementMock.mockResolvedValueOnce({ batchId: 1, rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [] });
    getBankStatementImportMock.mockRejectedValueOnce(new Error('Синтетическая ошибка карточки'));
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
    fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    await screen.findByText('Импорт выписки: прочитано 1, создано 1, пропущено 0');
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Синтетическая ошибка карточки', 'Выписка банка'));
    expect(importBankStatementMock).toHaveBeenCalledTimes(1);
    expect(rollbackBankStatementImportMock).not.toHaveBeenCalled();
  }, 15000);

  it('loads and saves bank statement auto import settings', async () => {
    const user = userEvent.setup();
    getUserSettingMock.mockImplementation(async (key: string) => {
      if (key === 'money.bankStatement.autoImport') {
        return {
          key,
          value: JSON.stringify({
            enabled: true,
            folderPath: 'C:\\BankStatements',
            fileMask: '*.csv',
            lastCheckedAt: null,
          }),
          updatedAt: '2026-04-21T08:00:00Z',
        };
      }

      throw new Error('not found');
    });

    render(<MoneyOperations source="Bank" />);

    const importJournalButton = await screen.findByRole('button', { name: 'Открыть журнал импортов выписки' });
    expect(importJournalButton.textContent).toContain('Журнал импортов');
    const switcher = await screen.findByRole<HTMLInputElement>('switch', { name: /Автоподгрузка/ });
    await waitFor(() => expect(switcher.checked).toBe(true));
    expect(screen.getByDisplayValue('C:\\BankStatements')).toBeTruthy();
    const maskInput = screen.getByDisplayValue('*.csv');
    await user.clear(maskInput);
    await user.paste('*.csv;*.1c');
    expect(screen.getByDisplayValue('*.csv;*.1c')).toBe(maskInput);
    await user.click(screen.getAllByRole('button', { name: 'Сохранить' })[0]);

    await waitFor(() => expect(upsertUserSettingMock).toHaveBeenCalled());
    expect(upsertUserSettingMock).toHaveBeenCalledWith(
      'money.bankStatement.autoImport',
      JSON.stringify({
        enabled: true,
        folderPath: 'C:\\BankStatements',
        fileMask: '*.csv;*.1c',
        lastCheckedAt: null,
      }),
    );

    runBankStatementAutoImportMock.mockResolvedValueOnce({
      enabled: true,
      folderPath: 'C:\\BankStatements',
      fileMask: '*.csv;*.1c',
      checkedAt: '2026-04-21T09:00:00Z',
      filesFound: 1,
      filesImported: 1,
      rowsRead: 1,
      created: 1,
      skipped: 0,
      errors: [],
      files: [
        {
          fileName: 'statement.csv',
          batchId: 7,
          rowsRead: 1,
          created: 1,
          skipped: 0,
          errors: [],
        },
      ],
    });
    await selectBankImportAccount(user, '102');
    await user.click(screen.getByRole('button', { name: 'Проверить' }));

    await waitFor(() => expect(runBankStatementAutoImportMock).toHaveBeenCalledWith({ companyId: 1, moneyAccountId: 102 }));
    expect(await screen.findByText(/Проверено файлов: 1/)).toBeTruthy();
    expect(await screen.findByText('statement.csv: +1 / 0')).toBeTruthy();
  }, 15000);

  it.each([0, 2])('bank folder result: keeps a successful result without errors when created=%s', async created => {
    const user = userEvent.setup();
    enableBankImportFolderFixture();
    const filesFound = created > 0 ? 1 : 0;
    runBankStatementAutoImportMock.mockResolvedValueOnce(createBankFolderRunResult({ created, rowsRead: created, filesFound, filesImported: filesFound }));
    render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    const requestsBefore = getMoneyOperationsPagedMock.mock.calls.length;
    await user.click(screen.getByRole('button', { name: 'Проверить' }));
    const result = await screen.findByRole('status', { name: 'Результат подгрузки выписок' });
    expect(within(result).getByText(`Проверено файлов: ${filesFound}. Создано операций: ${created}. Пропущено: 0.`)).toBeTruthy();
    expect(screen.queryByRole('alert', { name: 'Результат подгрузки выписок' })).toBeNull();
    expect(within(result).queryByText(/Ошибок:/)).toBeNull();
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledWith(
      `Проверено файлов: ${filesFound}. Создано операций: ${created}. Пропущено: 0.`, 'Выписка банка',
    ));
    expect(toastInfoMock).not.toHaveBeenCalled();
    expect(toastErrorMock).not.toHaveBeenCalled();
    expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(requestsBefore + (created > 0 ? 1 : 0));
  });

  it.each([
    { created: 2, errorCount: 1 }, { created: 0, errorCount: 1 },
    { created: 2, errorCount: 5 }, { created: 0, errorCount: 5 },
    { created: 2, errorCount: 6 }, { created: 0, errorCount: 6 },
  ])('bank folder result: reports created=$created with $errorCount errors without claiming full success', async ({ created, errorCount }) => {
    const user = userEvent.setup();
    enableBankImportFolderFixture();
    const errors = Array.from({ length: errorCount }, (_, index) => `statement-${index + 1}.csv: Не удалось прочитать строку ${index + 1}.`);
    const filesFound = errorCount + (created > 0 ? 1 : 0);
    runBankStatementAutoImportMock.mockResolvedValueOnce(createBankFolderRunResult({
      created, rowsRead: created, skipped: errorCount, filesFound, filesImported: created > 0 ? 1 : 0, errors,
    }));
    render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.type(screen.getByPlaceholderText('Поиск по контрагенту...'), 'Текущий поиск{Enter}');
    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'Текущий поиск', moneyAccountId: 101 }), expectAbortOptions(),
    ));
    const requestsBefore = getMoneyOperationsPagedMock.mock.calls.length;
    await user.click(screen.getByRole('button', { name: 'Проверить' }));
    const result = await screen.findByRole('alert', { name: 'Результат подгрузки выписок' });
    expect(within(result).getByText(`Ошибок: ${errorCount}`)).toBeTruthy();
    const visibleErrors = within(result).getAllByRole('listitem');
    expect(visibleErrors.map(item => item.textContent)).toEqual(errors.slice(0, 5));
    if (errorCount > 5) {
      expect(within(result).getByText(`Ещё ошибок: ${errorCount - 5}`)).toBeTruthy();
      expect(within(result).queryByText(errors[5])).toBeNull();
    } else expect(within(result).queryByText(/Ещё ошибок:/)).toBeNull();
    const status = created > 0 ? 'Подгрузка завершена частично. Созданные операции сохранены.' : 'Подгрузка завершена с ошибками.';
    expect(within(result).getByText(status)).toBeTruthy();
    const message = `${status} Проверено файлов: ${filesFound}. Создано операций: ${created}. Пропущено: ${errorCount}. Ошибок: ${errorCount}.`;
    await waitFor(() => expect(created > 0 ? toastInfoMock : toastErrorMock).toHaveBeenCalledWith(message, 'Выписка банка'));
    expect(toastSuccessMock).not.toHaveBeenCalled();
    expect(created > 0 ? toastErrorMock : toastInfoMock).not.toHaveBeenCalled();
    expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(requestsBefore + (created > 0 ? 1 : 0));
    expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'Текущий поиск', moneyAccountId: 101 }), expectAbortOptions(),
    );
    expect(runBankStatementAutoImportMock).toHaveBeenCalledTimes(1);
    expect(runBankStatementAutoImportMock).toHaveBeenCalledWith({ companyId: 1, moneyAccountId: 101 });
  });

  it('bank folder result: renders aggregated errors once as plain text without HTML execution', async () => {
    const user = userEvent.setup();
    enableBankImportFolderFixture();
    const rawError = '<img src=x onerror="window.bankFolderInjected=true"><script>window.bankFolderInjected=true</script>';
    const aggregate = `bad.csv: ${rawError}`;
    runBankStatementAutoImportMock.mockResolvedValueOnce(createBankFolderRunResult({
      filesFound: 1, skipped: 1, errors: [aggregate],
      files: [{ fileName: 'bad.csv', rowsRead: 0, created: 0, skipped: 1, errors: [rawError] }],
    }));
    render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Проверить' }));
    const result = await screen.findByRole('alert', { name: 'Результат подгрузки выписок' });
    expect(within(result).getAllByText(aggregate)).toHaveLength(1);
    expect(within(result).getAllByRole('listitem')).toHaveLength(1);
    expect(within(result).queryByText(rawError)).toBeNull();
    expect(result.querySelector('img,script')).toBeNull();
    expect(result.textContent).toContain(aggregate);
  });

  it('bank folder result: replaces old errors with the next successful run', async () => {
    const user = userEvent.setup();
    enableBankImportFolderFixture();
    runBankStatementAutoImportMock.mockResolvedValueOnce(createBankFolderRunResult({ filesFound: 1, skipped: 1, errors: ['bad.csv: Ошибка чтения.'] }));
    runBankStatementAutoImportMock.mockResolvedValueOnce(createBankFolderRunResult());
    render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Проверить' }));
    expect(await screen.findByText('bad.csv: Ошибка чтения.')).toBeTruthy();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Проверить' })).toHaveProperty('disabled', false));
    await user.click(screen.getByRole('button', { name: 'Проверить' }));
    await screen.findByRole('status', { name: 'Результат подгрузки выписок' });
    expect(screen.queryByText('bad.csv: Ошибка чтения.')).toBeNull();
    expect(screen.queryByRole('alert', { name: 'Результат подгрузки выписок' })).toBeNull();
    await waitFor(() => expect(toastSuccessMock).toHaveBeenCalledTimes(1));
    expect(toastErrorMock).toHaveBeenCalledTimes(1);
    expect(toastInfoMock).not.toHaveBeenCalled();
    expect(runBankStatementAutoImportMock).toHaveBeenCalledTimes(2);
  });

  it('bank folder result: ignores returned errors when permission was revoked during the request', async () => {
    const user = userEvent.setup();
    enableBankImportFolderFixture();
    let complete!: (value: BankStatementAutoImportRunResult) => void;
    runBankStatementAutoImportMock.mockReturnValueOnce(new Promise<BankStatementAutoImportRunResult>(resolve => { complete = resolve; }));
    const { rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Проверить' }));
    await waitFor(() => expect(runBankStatementAutoImportMock).toHaveBeenCalledTimes(1));
    const requestsBefore = getMoneyOperationsPagedMock.mock.calls.length;
    permissionsState.paymentsCanCreate = false;
    rerender(<MoneyOperations source="Bank" />);
    await act(async () => complete(createBankFolderRunResult({ filesFound: 1, created: 1, errors: ['bad.csv: Ошибка чтения.'] })));
    expect(screen.queryByRole('alert', { name: 'Результат подгрузки выписок' })).toBeNull();
    expect(screen.queryByRole('status', { name: 'Результат подгрузки выписок' })).toBeNull();
    expect(toastInfoMock).not.toHaveBeenCalled();
    expect(toastErrorMock).not.toHaveBeenCalled();
    expect(toastSuccessMock).not.toHaveBeenCalled();
    expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(requestsBefore);
  });

  it.each(['file', 'folder'] as const)('bank import context: %s uses the explicitly selected stable ID across identical account names', async mode => {
    const user = userEvent.setup();
    if (mode === 'folder') enableBankImportFolderFixture();
    getMoneyAccountsMock.mockResolvedValue([
      { id: 201, companyId: 11, companyName: 'Компания Альфа', name: 'Основной счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
      { id: 202, companyId: 22, companyName: 'Компания Бета', name: 'Основной счёт', source: 'Bank', currency: 'RUB', isActive: true, isDefault: true },
    ]);
    const { container } = render(<MoneyOperations source="Bank" />);
    const select = await selectBankImportAccount(user, '202');
    expect(select.selectedOptions[0].textContent).toBe('Основной счёт · Компания Бета');
    if (mode === 'file') {
      await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
      const file = new File(['synthetic'], 'statement.csv');
      fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [file] } });
      await waitFor(() => expect(importBankStatementMock).toHaveBeenCalledWith(file, { companyId: 22, moneyAccountId: 202 }));
    } else {
      await user.click(screen.getByRole('button', { name: 'Проверить' }));
      await waitFor(() => expect(runBankStatementAutoImportMock).toHaveBeenCalledWith({ companyId: 22, moneyAccountId: 202 }));
    }
  });

  it.each(['all', 'legacy', 'unknown-company', 'inactive', 'server-blocked'] as const)(
    'bank import context: refuses %s selection for both file and folder without guessing a default', async scenario => {
      const user = userEvent.setup();
      enableBankImportFolderFixture();
      if (scenario === 'legacy') getMoneyAccountsMock.mockResolvedValue([]);
      if (!['all', 'legacy'].includes(scenario)) getMoneyAccountsMock.mockResolvedValue([
        { id: 201, companyId: scenario === 'unknown-company' ? null : 11, name: 'Недоступный счёт', source: 'Bank', currency: 'RUB',
          isActive: scenario !== 'inactive', isDefault: true, canUseForNewOperations: scenario !== 'server-blocked' },
      ]);
      const { container } = render(<MoneyOperations source="Bank" />);
      await screen.findByText('Загрузить выписку');
      if (scenario !== 'all') await selectBankImportAccount(user, scenario === 'legacy' ? 'Основной счёт' : '201');
      const input = container.querySelector('input[type="file"]') as HTMLInputElement;
      const picker = vi.spyOn(input, 'click');
      await waitFor(() => expect(screen.getByRole('button', { name: 'Проверить' })).toHaveProperty('disabled', false));
      await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
      await user.click(screen.getByRole('button', { name: 'Проверить' }));
      expect(picker).not.toHaveBeenCalled();
      expect(importBankStatementMock).not.toHaveBeenCalled();
      expect(runBankStatementAutoImportMock).not.toHaveBeenCalled();
      expect(toastErrorMock).toHaveBeenCalledWith('Выберите действующий банковский счёт своей компании в заголовке.', 'Выписка банка');
      expect(document.activeElement).toBe(screen.getByRole('combobox', { name: /Банковский счёт в заголовке/ }));
    },
  );

  it.each(['paymentsCanCreate', 'paymentsCanView'] as const)('bank import context: disables both entry points without %s', async permission => {
    const user = userEvent.setup();
    enableBankImportFolderFixture();
    permissionsState[permission] = false;
    render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    for (const name of ['Загрузить выписку', 'Проверить']) {
      const button = screen.getByRole('button', { name });
      expect(button).toHaveProperty('disabled', true);
      await user.click(button);
    }
    expect(importBankStatementMock).not.toHaveBeenCalled();
    expect(runBankStatementAutoImportMock).not.toHaveBeenCalled();
  });

  it.each(['changed-account', 'returned-account', 'revoked-permission'] as const)(
    'bank import context: rejects a picker opened before %s', async change => {
      const user = userEvent.setup();
      const { container, rerender } = render(<MoneyOperations source="Bank" />);
      await selectBankImportAccount(user);
      await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
      if (change === 'revoked-permission') {
        permissionsState.paymentsCanCreate = false;
        rerender(<MoneyOperations source="Bank" />);
      } else {
        await selectBankImportAccount(user, '102');
        if (change === 'returned-account') await selectBankImportAccount(user, '101');
      }
      fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
      expect(importBankStatementMock).not.toHaveBeenCalled();
      expect(toastErrorMock).toHaveBeenCalledWith(expect.stringContaining('Выбранный счёт или права изменились'), 'Выписка банка');
    },
  );

  it.each(['file', 'folder'] as const)('bank import context: %s blocks reentry and both account selectors until completion', async mode => {
    const user = userEvent.setup();
    enableBankImportFolderFixture();
    let complete!: (value: unknown) => void;
    const response = new Promise(resolve => { complete = resolve; });
    const pendingRequest = mode === 'file' ? importBankStatementMock : runBankStatementAutoImportMock;
    pendingRequest.mockReturnValueOnce(response);
    const { container } = render(<MoneyOperations source="Bank" />);
    const header = await selectBankImportAccount(user);
    await user.click(screen.getByRole('button', { name: 'Фильтр' }));
    const second = screen.getByLabelText('Банковский счёт', { selector: 'select' });
    const file = new File(['synthetic'], 'statement.csv');
    const input = container.querySelector('input[type="file"]')!;
    if (mode === 'file') {
      await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
      fireEvent.change(input, { target: { files: [file] } });
    } else await user.click(screen.getByRole('button', { name: 'Проверить' }));
    await waitFor(() => expect(pendingRequest).toHaveBeenCalledTimes(1));
    expect(header).toHaveProperty('disabled', true);
    expect(second).toHaveProperty('disabled', true);
    fireEvent.change(header, { target: { value: '102' } });
    fireEvent.change(second, { target: { value: '102' } });
    fireEvent.change(input, { target: { files: [file] } });
    const other = mode === 'file' ? 'Проверить' : 'Загрузить выписку';
    expect(screen.getByRole('button', { name: other })).toHaveProperty('disabled', true);
    await user.click(screen.getByRole('button', { name: other }));
    expect(pendingRequest).toHaveBeenCalledTimes(1);
    expect(mode === 'file' ? runBankStatementAutoImportMock : importBankStatementMock).not.toHaveBeenCalled();
    expect(header).toHaveProperty('value', '101');
    await act(async () => complete(mode === 'file'
      ? { rowsRead: 0, created: 0, skipped: 0, errors: [], operations: [] }
      : { enabled: true, checkedAt: '2026-10-24T00:00:00Z', filesFound: 0, created: 0, skipped: 0, files: [] }));
    await waitFor(() => expect(header).toHaveProperty('disabled', false));
  });

  it.each([
    ['file', false], ['file', true], ['folder', false], ['folder', true],
  ] as const)('bank import context: ignores a late %s response after permission revocation, failure=%s', async (mode, fail) => {
    const user = userEvent.setup();
    if (mode === 'folder') enableBankImportFolderFixture();
    let complete!: (value: unknown) => void;
    let reject!: (reason: Error) => void;
    const response = new Promise((resolve, rejectPromise) => { complete = resolve; reject = rejectPromise; });
    const pendingRequest = mode === 'file' ? importBankStatementMock : runBankStatementAutoImportMock;
    pendingRequest.mockReturnValueOnce(response);
    const { container, rerender } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    if (mode === 'file') {
      await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
      fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    } else await user.click(screen.getByRole('button', { name: 'Проверить' }));
    await waitFor(() => expect(pendingRequest).toHaveBeenCalledTimes(1));
    const requestsBeforeCompletion = getMoneyOperationsPagedMock.mock.calls.length;
    permissionsState.paymentsCanCreate = false;
    rerender(<MoneyOperations source="Bank" />);
    await act(async () => {
      if (fail) reject(new Error('stale failure'));
      else complete({ rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [], checkedAt: '2026-10-24T00:00:00Z', filesFound: 1, files: [] });
    });
    expect(toastSuccessMock).not.toHaveBeenCalled();
    expect(toastErrorMock).not.toHaveBeenCalled();
    expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(requestsBeforeCompletion);
    expect(screen.queryByText(/Импорт выписки: прочитано/)).toBeNull();
    expect(screen.queryByText(/Проверено файлов:/)).toBeNull();
  });

  it.each(['file', 'folder'] as const)('bank import context: %s completion reloads the latest journal search', async mode => {
    const user = userEvent.setup();
    if (mode === 'folder') enableBankImportFolderFixture();
    let complete!: (value: unknown) => void;
    const request = mode === 'file' ? importBankStatementMock : runBankStatementAutoImportMock;
    request.mockReturnValueOnce(new Promise(resolve => { complete = resolve; }));
    const { container } = render(<MoneyOperations source="Bank" />);
    await selectBankImportAccount(user);
    if (mode === 'file') {
      await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
      fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
    } else await user.click(screen.getByRole('button', { name: 'Проверить' }));
    await waitFor(() => expect(request).toHaveBeenCalledTimes(1));
    await user.type(screen.getByPlaceholderText('Поиск по контрагенту...'), 'Синтетический{Enter}');
    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'Синтетический', moneyAccountId: 101 }), expectAbortOptions(),
    ));
    const requestsBeforeCompletion = getMoneyOperationsPagedMock.mock.calls.length;
    await act(async () => complete({ rowsRead: 1, created: 1, skipped: 0, errors: [], operations: [], checkedAt: '2026-10-24T00:00:00Z', filesFound: 1, files: [] }));
    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(requestsBeforeCompletion + 1));
    expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ search: 'Синтетический', moneyAccountId: 101 }), expectAbortOptions(),
    );
  });

  it.each(['file', 'folder'] as const)('bank import context: %s failure unlocks the same account for an explicit retry', async mode => {
    const user = userEvent.setup();
    if (mode === 'folder') enableBankImportFolderFixture();
    const request = mode === 'file' ? importBankStatementMock : runBankStatementAutoImportMock;
    request.mockRejectedValueOnce(new Error('Синтетический отказ'));
    const { container } = render(<MoneyOperations source="Bank" />);
    const header = await selectBankImportAccount(user);
    const start = async () => {
      if (mode === 'file') {
        await user.click(screen.getByRole('button', { name: 'Загрузить выписку' }));
        fireEvent.change(container.querySelector('input[type="file"]')!, { target: { files: [new File(['synthetic'], 'statement.csv')] } });
      } else await user.click(screen.getByRole('button', { name: 'Проверить' }));
    };
    await start();
    await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith('Синтетический отказ', 'Выписка банка'));
    expect(header).toHaveProperty('disabled', false);
    expect(header).toHaveProperty('value', '101');
    await start();
    await waitFor(() => expect(request).toHaveBeenCalledTimes(2));
    const target = { companyId: 1, moneyAccountId: 101 };
    if (mode === 'file') expect(request).toHaveBeenLastCalledWith(expect.any(File), target);
    else expect(request).toHaveBeenLastCalledWith(target);
  });

  it('loads and saves bank statement recognition rules', async () => {
    const user = userEvent.setup();
    getUserSettingMock.mockImplementation(async (key: string) => {
      if (key === 'money.bankStatement.categoryRules') {
        return {
          key,
          value: JSON.stringify({
            rules: [
              {
                id: 'hosting',
                enabled: true,
                categoryName: 'Облачная инфраструктура',
                paymentType: 'Expense',
                keywords: 'хостинг; cloud',
                incomeTypeId: 2,
              },
            ],
          }),
          updatedAt: '2026-04-21T08:00:00Z',
        };
      }
      throw new Error('not found');
    });

    render(<MoneyOperations source="Bank" />);

    await user.click(await screen.findByRole('button', { name: 'Открыть правила распознавания выписки' }));
    const panel = await screen.findByText('Правила распознавания выписки');
    const rulesPanel = panel.closest('div.rounded-xl') as HTMLElement;
    expect(screen.getByDisplayValue('Облачная инфраструктура')).toBeTruthy();
    const keywordsInput = screen.getByDisplayValue('хостинг; cloud');
    await user.clear(keywordsInput);
    await user.paste('хостинг; cloud; vps');
    expect(screen.getByDisplayValue('хостинг; cloud; vps')).toBe(keywordsInput);
    await user.click(within(rulesPanel).getByRole('button', { name: /Сохранить/ }));

    await waitFor(() => expect(upsertUserSettingMock).toHaveBeenCalled());
    expect(upsertUserSettingMock).toHaveBeenCalledWith(
      'money.bankStatement.categoryRules',
      JSON.stringify({
        rules: [
          {
            id: 'hosting',
            enabled: true,
            categoryName: 'Облачная инфраструктура',
            paymentType: 'Expense',
            keywords: 'хостинг; cloud; vps',
            incomeTypeId: 2,
          },
        ],
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Правила распознавания выписки сохранены.', 'Выписка банка');
  }, 15000);

  it('shows bank import journal details and rolls back an import batch', async () => {
    const user = userEvent.setup();
    operations = [];
    const defaultDetails = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementationOnce(async (id: number) => {
      const details = await defaultDetails(id);
      return {
        ...details,
        status: 'AppliedWithErrors',
        rowsRead: 2,
        skippedRows: 1,
        errorRows: 1,
        rows: [...details.rows, {
          ...details.rows[0],
          id: 2,
          rowNumber: 3,
          status: 'ClarificationRequired',
          moneyOperationId: null,
          amount: 0,
          counterpartyName: 'Синтетический плательщик',
          purpose: 'Строка с ошибкой суммы',
          paymentOrderNumber: '102',
          message: 'Не удалось однозначно прочитать сумму «1,234». Исправьте исходную строку и загрузите выписку заново.',
        }],
      };
    });
    getBankStatementImportsMock.mockResolvedValue({
      items: [
        {
          id: 1,
          fileName: 'statement.csv',
          status: 'Applied',
          rowsRead: 1,
          createdRows: 1,
          skippedRows: 0,
          errorRows: 0,
          rollbackRows: 0,
          createdAt: '2026-04-29T10:00:00Z',
          rolledBackAt: null,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 5,
    });
    render(<MoneyOperations source="Bank" />);

    expect(getBankStatementImportsMock).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Открыть журнал импортов выписки' }));

    await screen.findByText('Журнал импортов выписки');
    await screen.findByText('statement.csv');

    await user.click(screen.getByRole('button', { name: 'Открыть импорт выписки statement.csv' }));

    await waitFor(() => expect(getBankStatementImportMock).toHaveBeenCalledWith(1));
    await screen.findByText('Импорт банковской выписки');
    await screen.findByText('Ахмедова Айтан Рафик Кызы');
    await screen.findByText('Оплата по счету №33');

    await screen.findByText('Требует уточнения');
    await screen.findByText(/Не удалось однозначно прочитать сумму «1,234»/);
    expect(screen.queryByText('ClarificationRequired')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Откатить импорт выписки statement.csv из карточки' }));

    await waitFor(() => expect(rollbackBankStatementImportMock).toHaveBeenCalledWith(1));
    expect(toastSuccessMock).toHaveBeenCalledWith('Откат импорта: удалено операций 1, пропущено строк 0.', 'Выписка банка');
  }, 15000);

  it('shows the stored currency of each imported row without assuming rubles', async () => {
    const user = userEvent.setup();
    const defaultDetails = getBankStatementImportMock.getMockImplementation()!;
    getBankStatementImportMock.mockImplementationOnce(async (id: number) => {
      const detail = await defaultDetails(id);
      return { ...detail, rows: [
        { ...detail.rows[0], id: 1, rowNumber: 1, amount: 101, currency: 'RUB' },
        { ...detail.rows[0], id: 2, rowNumber: 2, amount: 102, currency: 'USD' },
        { ...detail.rows[0], id: 3, rowNumber: 3, amount: 103, currency: null },
        { ...detail.rows[0], id: 4, rowNumber: 4, amount: 104 },
      ] };
    });
    getBankStatementImportsMock.mockResolvedValue({ items: [{ id: 1, fileName: 'statement.csv', status: 'Applied', rowsRead: 4,
      createdRows: 4, skippedRows: 0, errorRows: 0, rollbackRows: 0, createdAt: '2026-04-29T10:00:00Z', rolledBackAt: null }],
    total: 1, page: 1, pageSize: 5 });
    render(<MoneyOperations source="Bank" />);
    await user.click(screen.getByRole('button', { name: 'Открыть журнал импортов выписки' }));
    await screen.findByText('statement.csv');
    await user.click(screen.getByRole('button', { name: 'Открыть импорт выписки statement.csv' }));
    expect(await screen.findByText('101,00 ₽')).toBeTruthy();
    expect(screen.getByText('102,00 USD')).toBeTruthy();
    expect(screen.getByText('103,00 валюта не указана')).toBeTruthy();
    expect(screen.getByText('104,00 валюта не указана')).toBeTruthy();
  }, 15000);

  it('reprocesses a rolled back bank import batch', async () => {
    const user = userEvent.setup();
    operations = [];
    getBankStatementImportsMock.mockResolvedValue({
      items: [
        {
          id: 1,
          fileName: 'statement.csv',
          status: 'RolledBack',
          rowsRead: 1,
          createdRows: 1,
          skippedRows: 0,
          errorRows: 0,
          rollbackRows: 1,
          createdAt: '2026-04-29T10:00:00Z',
          rolledBackAt: '2026-04-29T11:00:00Z',
        },
      ],
      total: 1,
      page: 1,
      pageSize: 5,
    });
    getBankStatementImportMock.mockResolvedValue({
      id: 1,
      fileName: 'statement.csv',
      status: 'RolledBack',
      rowsRead: 1,
      createdRows: 1,
      skippedRows: 0,
      errorRows: 0,
      rollbackRows: 1,
      fileHash: 'hash',
      fileSize: 128,
      errorSummary: null,
      createdAt: '2026-04-29T10:00:00Z',
      rolledBackAt: '2026-04-29T11:00:00Z',
      rows: [
        {
          id: 1,
          rowNumber: 2,
          status: 'RolledBack',
          message: null,
          moneyOperationId: 44,
          date: '2026-04-29',
          direction: 'Income',
          amount: 25000,
          accountName: 'Основной счет',
          counterpartyName: 'Ахмедова Айтан Рафик Кызы',
          purpose: 'Оплата по счету №33',
          paymentOrderNumber: '101',
          createdAt: '2026-04-29T10:00:00Z',
          rolledBackAt: '2026-04-29T11:00:00Z',
        },
      ],
    });

    render(<MoneyOperations source="Bank" />);

    await user.click(screen.getByRole('button', { name: 'Открыть журнал импортов выписки' }));
    await screen.findByText('statement.csv');
    await user.click(screen.getByRole('button', { name: 'Открыть импорт выписки statement.csv' }));
    await waitFor(() => expect(screen.getAllByText('Откат выполнен').length).toBeGreaterThan(0));
    await user.click(screen.getByRole('button', { name: 'Повторить обработку импорта выписки statement.csv' }));

    await waitFor(() => expect(reprocessBankStatementImportMock).toHaveBeenCalledWith(1));
    expect(toastSuccessMock).toHaveBeenCalledWith('Повторная обработка: создано 1, пропущено 0.', 'Выписка банка');
    expect(await screen.findByText('Импорт выписки: прочитано 1, создано 1, пропущено 0')).toBeTruthy();
  }, 15000);
});
