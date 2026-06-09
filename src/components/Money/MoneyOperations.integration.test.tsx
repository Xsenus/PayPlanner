import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { MoneyOperations } from './MoneyOperations';
import type { MoneyAccount, MoneyOperation, MoneyOperationsSummary } from '../../types';

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
  getMoneyOperationMock,
  getMoneyOperationsPagedMock,
  getMoneyOperationsPrintHtmlMock,
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
  toastSuccessMock,
  updateCashOrderMock,
  updateMoneyOperationMock,
  upsertUserSettingMock,
} = vi.hoisted(() => {
  const toastErrorMock = vi.fn();
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
    getMoneyOperationMock: vi.fn(),
    getMoneyOperationsPagedMock: vi.fn(),
    getMoneyOperationsPrintHtmlMock: vi.fn(),
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
    toastMock: { error: toastErrorMock, success: toastSuccessMock },
    toastErrorMock,
    toastSuccessMock,
    updateCashOrderMock: vi.fn(),
    updateMoneyOperationMock: vi.fn(),
    upsertUserSettingMock: vi.fn(),
  };
});

const permissionsState = vi.hoisted(() => ({
  paymentsCanExport: true,
}));

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
      clients: { ...section },
      cases: { ...section },
      accounts: { ...section },
      acts: { ...section },
      payments: { ...section, canExport: permissionsState.paymentsCanExport },
      contracts: { ...section },
      dictionaries: { ...section },
      roles: { ...section },
      audit: { ...section },
    };
  },
}));

vi.mock('../../contexts/TabContext', () => ({
  useTabNavigation: () => ({ setActiveTab: vi.fn() }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../hooks/usePersistedTableSettings', () => ({
  usePersistedTableSettings: () => ({
    columnOrder: ['date', 'accountName', 'counterpartyName', 'responsibleName', 'linkedContractNumber', 'linkedActNumber', 'cashOrder', 'attachmentSummary', 'categoryName', 'purpose'],
    orderedVisibleColumns: ['date', 'accountName', 'counterpartyName', 'responsibleName', 'linkedContractNumber', 'linkedActNumber', 'cashOrder', 'attachmentSummary', 'categoryName', 'purpose'],
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
    getMoneyOperation: getMoneyOperationMock,
    getMoneyOperationsPaged: getMoneyOperationsPagedMock,
    getMoneyOperationsPrintHtml: getMoneyOperationsPrintHtmlMock,
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

  return {
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

describe('MoneyOperations integration', () => {
  let operations: MoneyOperation[];
  let nextId: number;

  beforeEach(() => {
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
    getMoneyOperationMock.mockReset();
    getMoneyOperationsPagedMock.mockReset();
    getMoneyOperationsPrintHtmlMock.mockReset();
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
    toastSuccessMock.mockReset();
    updateCashOrderMock.mockReset();
    updateMoneyOperationMock.mockReset();
    upsertUserSettingMock.mockReset();
    permissionsState.paymentsCanExport = true;
    window.localStorage.clear();
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

  it('runs and stores daily balance reconciliation rows', async () => {
    const user = userEvent.setup();
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

  it('highlights stale saved daily balance reconciliation rows', async () => {
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
    getMoneyBalanceReconciliationResultsMock.mockResolvedValueOnce([
      {
        id: 7,
        date: '2026-05-01',
        accountName: 'Основной банк',
        source: 'Bank',
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
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'Ахмедова' })),
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
    await user.clear(screen.getByLabelText('Поставщик'));
    await user.type(screen.getByLabelText('Поставщик'), 'Новый поставщик');
    await user.selectOptions(screen.getByLabelText('Ответственный'), '12');
    await user.clear(categoryInput as HTMLElement);
    await user.type(categoryInput as HTMLElement, 'Хозрасходы');
    await user.clear(screen.getByLabelText(/^Назначение$/));
    await user.type(screen.getByLabelText(/^Назначение$/), 'Оплата по договору');

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
        purpose: 'Оплата по договору',
      }),
    );
    expect(toastSuccessMock).toHaveBeenCalledWith('Операция создана.', 'Деньги');
    await screen.findByText('Новый поставщик');
  }, 15000);

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
      ),
    );
    expect(await screen.findByText('Поставщик Б')).toBeTruthy();
    expect(document.body.textContent).toContain('Расход: 1 500,00 ₽');
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
    expect(screen.getByText('E-money')).toBeTruthy();
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
    );

    await user.selectOptions(screen.getAllByRole('combobox')[0], 'Cash');

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ source: 'Cash' }),
      ),
    );

    await user.selectOptions(screen.getAllByRole('combobox')[1], 'Income');

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ source: 'Cash', direction: 'Income' }),
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

    expect(await screen.findByText('E-money')).toBeTruthy();
    expect(screen.getByText('Операции по электронным кошелькам')).toBeTruthy();
    await user.selectOptions(screen.getByLabelText('Кошелёк в заголовке'), '302');
    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ source: 'EMoney', moneyAccountId: 302, accountName: 'CloudPayments' }),
      ),
    );
    expect(await screen.findByText('Другой клиент')).toBeTruthy();
    expect(screen.queryByText('Онлайн клиент')).toBeNull();

    expect(screen.getByRole('option', { name: 'Все кошельки' })).toBeTruthy();
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
      ),
    );
    expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
      expect.not.objectContaining({ direction: 'Income' }),
    );
    expect(await screen.findByText('Расход факт')).toBeTruthy();
    expect(screen.queryByText('Клиент план')).toBeNull();

    await user.click(screen.getByRole('button', { name: /Фильтр/ }));
    await user.click(screen.getByRole('button', { name: 'Сбросить' }));

    await waitFor(() =>
      expect(getMoneyOperationsPagedMock).toHaveBeenLastCalledWith(
        expect.not.objectContaining({ direction: 'Income', accountingType: 'Fact' }),
      ),
    );
  }, 15000);

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
      ),
    );
    expect(await screen.findByText('Поставщик услуг')).toBeTruthy();
    expect(screen.queryByText('Поставщик офиса')).toBeNull();
    expect(screen.getByText('Статья')).toBeTruthy();
    expect(screen.getByText('Юридические')).toBeTruthy();
  }, 15000);

  it('normalizes API dates in the edit form and updates the operation', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
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

  it('allows linking an e-money income operation to an invoice', async () => {
    const user = userEvent.setup();
    operations = [
      createOperation({
        id: 18,
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
    await user.click(screen.getByRole('button', { name: 'Создать ПКО' }));

    await waitFor(() => expect(createCashOrderFromPaymentMock).toHaveBeenCalledWith(77));
    expect(toastSuccessMock).toHaveBeenCalledWith('Создан кассовый ордер ПКО-2026-0001.', 'Касса');
    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(2));
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
    await user.click(screen.getByRole('button', { name: 'Создать ПКО' }));

    await waitFor(() => expect(createCashOrderFromMoneyOperationMock).toHaveBeenCalledWith(11));
    expect(toastSuccessMock).toHaveBeenCalledWith('Создан кассовый ордер ПКО-2026-0002.', 'Касса');
    await waitFor(() => expect(getCashOrderRegisterMock).toHaveBeenCalledTimes(2));
  }, 15000);

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
    await screen.findByText('ПКО: 1, РКО: 0');
    await screen.findByText('Оплата от клиента. счет №33');
    expect(getCashOrderRegisterMock).toHaveBeenCalledWith(
      expect.objectContaining({
        page: 1,
        pageSize: 10,
      }),
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
          page: 1,
          pageSize: 10,
        }),
      ),
    );
    expect(getCashBookMock).toHaveBeenLastCalledWith(expect.objectContaining({ accountName: 'Резервная касса' }));
    await screen.findByText('Касса: Резервная касса');
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
    await user.click(screen.getByRole('button', { name: 'РКО' }));

    await waitFor(() =>
      expect(getCashOrderRegisterMock).toHaveBeenLastCalledWith(
        expect.objectContaining({
          type: 'Outgoing',
          page: 1,
          pageSize: 10,
        }),
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

    await user.clear(screen.getByLabelText('Сумма'));
    await user.type(screen.getByLabelText('Сумма'), '25000');
    await user.clear(screen.getByLabelText('Счёт / касса'));
    await user.type(screen.getByLabelText('Счёт / касса'), 'Основная касса');
    await user.type(screen.getByLabelText('Плательщик'), 'Ахмедова Айтан Рафик Кызы');
    await user.type(screen.getByLabelText('Основание'), 'Оплата по счёту №33');

    await user.click(getLastButton('Сохранить'));

    await waitFor(() =>
      expect(createCashOrderMock).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 0,
          type: 'Incoming',
          status: 'Draft',
          amount: 25000,
          account: 'Основная касса',
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
      ),
    );
  }, 15000);

  it('imports a bank statement file and reloads bank operations', async () => {
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
    const input = container.querySelector('input[type="file"]') as HTMLInputElement;
    expect(input.accept).toContain('.1c');
    const file = new File(['Дата;Сумма\n29.04.2026;25000'], 'statement.csv', { type: 'text/csv' });
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() => expect(importBankStatementMock).toHaveBeenCalledWith(file));
    await screen.findByText('Импорт выписки: прочитано 2, создано 2, пропущено 0');
    await screen.findByText('Ахмедова Айтан Рафик Кызы');
    await screen.findByText('101');
    await userEvent.click(screen.getByRole('button', { name: 'Открыть операцию импорта 301' }));
    await waitFor(() => expect(getMoneyOperationMock).toHaveBeenCalledWith(301));
    await screen.findByText('Редактировать операцию по банку');
    expect(toastSuccessMock).toHaveBeenCalledWith('Создано операций: 2. Пропущено: 0.', 'Выписка банка');
    await waitFor(() => expect(getMoneyOperationsPagedMock).toHaveBeenCalledTimes(2));
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
    await user.type(maskInput, '*.csv;*.1c');
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
    await user.click(screen.getByRole('button', { name: 'Проверить' }));

    await waitFor(() => expect(runBankStatementAutoImportMock).toHaveBeenCalled());
    expect(await screen.findByText(/Проверено файлов: 1/)).toBeTruthy();
    expect(await screen.findByText('statement.csv: +1 / 0')).toBeTruthy();
  }, 15000);

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
    await user.type(keywordsInput, 'хостинг; cloud; vps');
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
    vi.spyOn(window, 'confirm').mockReturnValue(true);

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

    await user.click(screen.getByRole('button', { name: 'Откатить импорт выписки statement.csv из карточки' }));

    await waitFor(() => expect(rollbackBankStatementImportMock).toHaveBeenCalledWith(1));
    expect(toastSuccessMock).toHaveBeenCalledWith('Откат импорта: удалено операций 1, пропущено строк 0.', 'Выписка банка');
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
