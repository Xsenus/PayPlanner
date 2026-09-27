import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { useEffect, useMemo, useRef, useState, type ChangeEvent, type KeyboardEvent } from 'react';
import { createPortal } from 'react-dom';
import {
  Calendar,
  Ban,
  AlertTriangle,
  ChevronDown,
  CheckCircle2,
  Copy,
  Download,
  Filter,
  GripVertical,
  HelpCircle,
  History,
  Plus,
  Pencil,
  Printer,
  ReceiptText,
  RefreshCw,
  RotateCcw,
  Save,
  Search,
  SlidersHorizontal,
  Trash2,
  WalletCards,
  UploadCloud,
  Link2,
  X,
} from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useMoneyOperations } from '../../hooks/useMoneyOperations';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import { useTabNavigation } from '../../contexts/TabContext';
import { fromInputToApiDate, toDateInputValue, todayYMD } from '../../utils/dateUtils';
import { ConfirmDialog } from '../ui/ConfirmDialog';
import { ObjectAuditModal } from '../ui/ObjectAuditModal';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { TableSelectCheckbox } from '../ui/TableSelectCheckbox';
import { formatMoneyOperationDate as formatDate } from './moneyOperationFormatters';
import type {
  BankStatementImportBatch,
  BankStatementImportBatchDetail,
  BankStatementAutoImportRunResult,
  BankStatementImportCreatedOperation,
  MoneyAccount,
  MoneyOperation,
  MoneyOperationAccountSummary,
  MoneyOperationAccountingType,
  MoneyOperationsSummary,
  MoneyBalanceReconciliationResult,
  MoneyOperationDailyBalance,
  MoneyOperationDirection,
  MoneyOperationInvoiceCandidate,
  MoneyOperationJournalStatus,
  MoneyOperationSource,
  ActResponsible,
  CashBookSummary,
  CashInventory,
  CashOrder,
  CashOrderRegister,
  CashOrderType,
  IncomeType,
  PaymentKind,
} from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { requestPaymentAdvanceReviewFilter } from '../../utils/paymentAdvanceReview';

type MoneyTab = MoneyOperationSource | 'Total';

type BankStatementAutoImportSettings = {
  enabled: boolean;
  folderPath: string;
  fileMask: string;
  lastCheckedAt?: string | null;
};

type BankStatementCategoryRule = {
  id: string;
  enabled: boolean;
  categoryName: string;
  paymentType: PaymentKind;
  keywords: string;
  incomeTypeId?: number | null;
};

type BankStatementCategoryRulesSettings = {
  rules: BankStatementCategoryRule[];
};

type CashLimitSettings = {
  enabled: boolean;
  amount: number | null;
};

const BANK_STATEMENT_AUTO_IMPORT_SETTING_KEY = 'money.bankStatement.autoImport';
const BANK_STATEMENT_CATEGORY_RULES_SETTING_KEY = 'money.bankStatement.categoryRules';
const CASH_LIMIT_SETTING_KEY = 'money.cash.limit';

const DEFAULT_BANK_STATEMENT_AUTO_IMPORT_SETTINGS: BankStatementAutoImportSettings = {
  enabled: false,
  folderPath: '',
  fileMask: '*.csv;*.txt;*.1c',
  lastCheckedAt: null,
};

const DEFAULT_BANK_STATEMENT_CATEGORY_RULES_SETTINGS: BankStatementCategoryRulesSettings = {
  rules: [],
};

const DEFAULT_CASH_LIMIT_SETTINGS: CashLimitSettings = {
  enabled: false,
  amount: null,
};

const ACCOUNTABLE_ADVANCE_CATEGORY = 'Подотчетные суммы';
const ACCOUNTABLE_ADVANCE_PURPOSE = 'Выдача денежных средств под отчет';

const SOURCE_LABELS: Record<MoneyTab, string> = {
  Bank: 'Банк',
  Cash: 'Касса',
  Card: 'Карты',
  EMoney: 'Электронные деньги',
  Total: 'Итого',
};

const SOURCE_OPERATION_SUBTITLES: Record<MoneyOperationSource, string> = {
  Bank: 'Операции по банковским счетам',
  Cash: 'Операции по кассе и наличным',
  Card: 'Операции по картам',
  EMoney: 'Операции по электронным кошелькам',
};

const SOURCE_ACCOUNT_FILTER_LABELS: Record<MoneyOperationSource, string> = {
  Bank: 'Банковский счёт',
  Cash: 'Касса',
  Card: 'Счёт',
  EMoney: 'Кошелёк',
};

const SOURCE_ALL_ACCOUNTS_LABELS: Record<MoneyOperationSource, string> = {
  Bank: 'Все счета',
  Cash: 'Все кассы',
  Card: 'Все карты',
  EMoney: 'Все кошельки',
};

const SOURCE_MODAL_LABELS: Record<MoneyOperationSource, string> = {
  Bank: 'банку',
  Cash: 'кассе',
  Card: 'картам',
  EMoney: 'Электронные деньги',
};

const MONEY_DIRECTION_FILTER_LABELS: Record<MoneyOperationDirection, string> = {
  Income: 'Приход',
  Expense: 'Расход',
  Transfer: 'Между счетами',
};

const MONEY_ACCOUNTING_FILTER_LABELS: Record<MoneyOperationAccountingType, string> = {
  Fact: 'Факт',
  Plan: 'План',
};

const MONEY_JOURNAL_STATUS_LABELS: Record<MoneyOperationJournalStatus, string> = {
  Posted: 'Проведено',
  Processing: 'В обработке',
  Error: 'Ошибка',
};

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];

const MONEY_OPERATION_COLUMNS = [
  { key: 'source', label: 'Источник', defaultVisible: false, defaultWidth: 120 },
  { key: 'journalStatus', label: 'Статус', defaultVisible: true, defaultWidth: 150 },
  { key: 'date', label: 'Дата', defaultVisible: true, defaultWidth: 120 },
  { key: 'recognitionDate', label: 'Дата признания', defaultVisible: true, defaultWidth: 150 },
  { key: 'income', label: 'Приход', defaultVisible: true, defaultWidth: 130 },
  { key: 'expense', label: 'Расход', defaultVisible: true, defaultWidth: 130 },
  { key: 'vatRate', label: 'НДС, %', defaultVisible: false, defaultWidth: 90 },
  { key: 'vatAmount', label: 'НДС', defaultVisible: false, defaultWidth: 120 },
  { key: 'currency', label: 'Валюта', defaultVisible: true, defaultWidth: 90 },
  { key: 'accountName', label: 'Счета', defaultVisible: true, defaultWidth: 180 },
  { key: 'counterpartyName', label: 'Контрагент', defaultVisible: true, defaultWidth: 180 },
  { key: 'responsibleName', label: 'Ответственный', defaultVisible: true, defaultWidth: 170 },
  { key: 'projectName', label: 'Проект', defaultVisible: true, defaultWidth: 160 },
  { key: 'salesPointName', label: 'Точка продаж', defaultVisible: false, defaultWidth: 160 },
  { key: 'directionName', label: 'Направление', defaultVisible: false, defaultWidth: 160 },
  { key: 'paymentPriority', label: 'Приоритет в оплате', defaultVisible: false, defaultWidth: 150 },
  { key: 'purpose', label: 'Назначение', defaultVisible: true, defaultWidth: 240 },
  { key: 'paidAccountName', label: 'Опл. счета', defaultVisible: false, defaultWidth: 160 },
  { key: 'linkedContractNumber', label: 'Договор', defaultVisible: true, defaultWidth: 160 },
  { key: 'linkedActNumber', label: 'Акт', defaultVisible: true, defaultWidth: 150 },
  { key: 'categoryName', label: 'Статья', defaultVisible: true, defaultWidth: 160 },
  { key: 'matchStatus', label: 'Сверка', defaultVisible: true, defaultWidth: 180 },
  { key: 'attachmentSummary', label: 'Вложения', defaultVisible: true, defaultWidth: 180 },
  { key: 'cashOrder', label: 'Кассовый ордер', defaultVisible: true, defaultWidth: 190 },
] as const;

type MoneyOperationColumnKey = (typeof MONEY_OPERATION_COLUMNS)[number]['key'];

const DEFAULT_FORM: MoneyOperation = {
  id: 0,
  source: 'Bank',
  direction: 'Income',
  accountingType: 'Fact',
  journalStatus: 'Processing',
  moneyAccountId: null,
  moneyAccountName: null,
  moneyAccount: null,
  accountName: '',
  counterpartyName: '',
  responsibleUserId: null,
  responsibleName: null,
  incomeTypeId: null,
  categoryName: '',
  amount: 0,
  vatRate: null,
  vatAmount: null,
  currency: 'RUB',
  paymentOrderNumber: '',
  date: '',
  recognitionDate: '',
  isAdvancePayment: false,
  purpose: '',
  paidInvoiceNumber: '',
  paidAmount: 0,
  projectName: '',
  directionName: '',
  salesPointName: '',
  paymentPriority: undefined,
  paidAccountName: '',
  linkedInvoiceId: null,
  linkedInvoiceNumber: null,
  linkedInvoiceDate: null,
  matchStatus: 'None',
  matchedAt: null,
  matchComment: '',
  createdAt: new Date().toISOString(),
  updatedAt: null,
  transferTargetMoneyAccountId: null,
};

function createDefaultForm(source: MoneyOperationSource): MoneyOperation {
  return {
    ...DEFAULT_FORM,
    source,
    date: todayYMD(),
    createdAt: new Date().toISOString(),
  };
}

function mapMoneyOperationToForm(source: MoneyOperationSource, initialData?: MoneyOperation | null): MoneyOperation {
  if (!initialData) {
    return createDefaultForm(source);
  }

  return {
    ...createDefaultForm(source),
    ...initialData,
    source: initialData.source ?? source,
    date: toDateInputValue(initialData.date) || todayYMD(),
    recognitionDate: toDateInputValue(initialData.recognitionDate) || '',
    createdAt: initialData.createdAt ?? new Date().toISOString(),
  };
}

function formatMoney(value: number) {
  return new Intl.NumberFormat('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);
}

function calculateVatAmount(amount: number | null | undefined, vatRate: number | null | undefined) {
  if (!amount || amount <= 0 || vatRate === null || vatRate === undefined || vatRate <= 0) {
    return vatRate === 0 ? 0 : null;
  }

  return Math.round((amount * vatRate) / (100 + vatRate) * 100) / 100;
}

function buildUniqueOptions(values: Array<string | null | undefined>) {
  return Array.from(
    new Set(
      values
        .map((value) => value?.trim())
        .filter((value): value is string => Boolean(value)),
    ),
  ).sort((a, b) => a.localeCompare(b, 'ru'));
}

type MoneyOperationCategoryOption = Pick<IncomeType, 'id' | 'name' | 'paymentType'>;

function getPaymentKindForDirection(direction: MoneyOperationDirection): PaymentKind | null {
  if (direction === 'Income') return 'Income';
  if (direction === 'Expense') return 'Expense';
  return null;
}

function findCategoryOption(
  options: MoneyOperationCategoryOption[] | undefined,
  direction: MoneyOperationDirection,
  value: string | null | undefined,
) {
  const paymentKind = getPaymentKindForDirection(direction);
  const normalizedValue = value?.trim().toLocaleLowerCase('ru');
  if (!paymentKind || !normalizedValue) return null;

  return (
    options?.find(
      (option) =>
        option.paymentType === paymentKind &&
        option.name.trim().toLocaleLowerCase('ru') === normalizedValue,
    ) ?? null
  );
}

function renderMatchStatusBadge(
  item: MoneyOperation,
  onOpenInvoice?: (invoiceId: number, type?: 'Income' | 'Expense') => void,
  onClarify?: (item: MoneyOperation) => void,
) {
  if (item.matchStatus === 'Matched') {
    if (item.linkedInvoiceId && onOpenInvoice) {
      return (
        <button
          type="button"
          onClick={() => onOpenInvoice(item.linkedInvoiceId!, item.direction === 'Expense' ? 'Expense' : 'Income')}
          className="inline-flex items-center gap-1.5 rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700 transition hover:bg-emerald-100 hover:text-emerald-800"
          title={item.matchComment ?? 'Открыть связанный счёт'}>
          <CheckCircle2 className="h-3.5 w-3.5" />
          {item.linkedInvoiceNumber ? `Счёт ${item.linkedInvoiceNumber}` : 'Сверено'}
        </button>
      );
    }

    return (
      <span
        className="inline-flex items-center gap-1.5 rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700"
        title={item.matchComment ?? undefined}>
        <CheckCircle2 className="h-3.5 w-3.5" />
        {item.linkedInvoiceNumber ? `Счёт ${item.linkedInvoiceNumber}` : 'Сверено'}
      </span>
    );
  }

  if (item.matchStatus === 'ClarificationRequired') {
    const title = item.matchComment ?? 'Нужно проверить назначение и привязать счёт';
    if (onClarify && !item.isReadonly && item.id > 0) {
      return (
        <button
          type="button"
          onClick={() => onClarify(item)}
          className="inline-flex items-center gap-1.5 rounded-full bg-amber-50 px-2.5 py-1 text-xs font-medium text-amber-700 transition hover:bg-amber-100 hover:text-amber-800"
          title={title}>
          <AlertTriangle className="h-3.5 w-3.5" />
          Уточнить
        </button>
      );
    }

    return (
      <span
        className="inline-flex items-center gap-1.5 rounded-full bg-amber-50 px-2.5 py-1 text-xs font-medium text-amber-700"
        title={title}>
        <AlertTriangle className="h-3.5 w-3.5" />
        Уточнить
      </span>
    );
  }

  if (item.matchStatus === 'Ignored') {
    return (
      <span
        className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-2.5 py-1 text-xs font-medium text-slate-600"
        title={item.matchComment ?? undefined}>
        <Ban className="h-3.5 w-3.5" />
        Не учитывать
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-1.5 rounded-full bg-slate-100 px-2.5 py-1 text-xs font-medium text-slate-500">
      Не сверено
    </span>
  );
}

function renderJournalStatusBadge(item: MoneyOperation) {
  const status = item.journalStatus ?? 'Posted';
  const label = MONEY_JOURNAL_STATUS_LABELS[status];

  if (status === 'Error') {
    return (
      <span
        className="inline-flex items-center gap-1.5 rounded-full bg-rose-50 px-2.5 py-1 text-xs font-medium text-rose-700"
        title={item.matchComment ?? 'Операция требует проверки'}>
        <AlertTriangle className="h-3.5 w-3.5" />
        {label}
      </span>
    );
  }

  if (status === 'Processing') {
    return (
      <span
        className="inline-flex items-center gap-1.5 rounded-full bg-amber-50 px-2.5 py-1 text-xs font-medium text-amber-700"
        title={item.matchComment ?? undefined}>
        <HelpCircle className="h-3.5 w-3.5" />
        {label}
      </span>
    );
  }

  return (
    <span className="inline-flex items-center gap-1.5 rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700">
      <CheckCircle2 className="h-3.5 w-3.5" />
      {label}
    </span>
  );
}

function isAppliedToInvoice(item: MoneyOperation) {
  return Boolean(item.linkedInvoiceId && item.matchStatus === 'Matched');
}

function isUnallocatedAdvanceMoneyOperation(item: MoneyOperation) {
  return (
    item.source === 'Bank' &&
    item.direction === 'Income' &&
    item.accountingType === 'Fact' &&
    item.amount > 0 &&
    !item.linkedInvoiceId &&
    item.matchStatus !== 'Ignored'
  );
}

type BalanceCheckSource = Pick<MoneyOperationsSummary | MoneyOperationAccountSummary, 'startBalance' | 'income' | 'expense' | 'endBalance'>;
type DailyBalanceCheckSource = Pick<
  MoneyOperationDailyBalance,
  'openingBalance' | 'income' | 'expense' | 'closingBalance' | 'incomeCount' | 'expenseCount' | 'isBalanced'
>;
type ReconciliationNotification = {
  key: string;
  title: string;
  description: string;
  tone: 'rose' | 'amber';
};

function getBalanceCheck(source: BalanceCheckSource) {
  const expectedEndBalance = Math.round((source.startBalance + source.income - source.expense) * 100) / 100;
  const difference = Math.round((source.endBalance - expectedEndBalance) * 100) / 100;

  return {
    expectedEndBalance,
    difference,
    isBalanced: Math.abs(difference) < 0.01,
  };
}

function renderBalanceCheckBadge(source: BalanceCheckSource) {
  const check = getBalanceCheck(source);

  return (
    <div
      className={`inline-flex items-center gap-2 rounded-full px-2.5 py-1 text-xs font-medium ${
        check.isBalanced ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'
      }`}
      title={`Расчетный остаток: ${formatMoney(check.expectedEndBalance)} ₽`}>
      <span>{check.isBalanced ? 'Сверка сходится' : `Расхождение: ${formatMoney(check.difference)} ₽`}</span>
    </div>
  );
}

function roundMoney(value: number) {
  return Math.round(value * 100) / 100;
}

function getSavedBalanceDrift(current: DailyBalanceCheckSource, saved?: MoneyBalanceReconciliationResult | null) {
  if (!saved) {
    return null;
  }

  const amountDifference =
    Math.abs(roundMoney(current.openingBalance - saved.openingBalance)) +
    Math.abs(roundMoney(current.income - saved.income)) +
    Math.abs(roundMoney(current.expense - saved.expense)) +
    Math.abs(roundMoney(current.closingBalance - saved.closingBalance));
  const countDifference =
    current.incomeCount !== saved.incomeCount ||
    current.expenseCount !== saved.expenseCount ||
    current.isBalanced !== saved.isBalanced;

  return {
    isActual: amountDifference < 0.01 && !countDifference && saved.issueCount === 0,
    amountDifference: roundMoney(amountDifference),
  };
}

function renderSavedBalanceReconciliationBadge(
  current: DailyBalanceCheckSource,
  saved?: MoneyBalanceReconciliationResult | null,
  timeZoneId?: string | null,
) {
  if (!saved) {
    return <span className="text-xs text-slate-400">Нет</span>;
  }

  const drift = getSavedBalanceDrift(current, saved);
  if (!drift?.isActual) {
    return (
      <span
        className="inline-flex rounded-full bg-rose-50 px-2 py-1 text-xs font-medium text-rose-700"
        title={`Сохранено ${formatDate(saved.checkedAt, timeZoneId)}. Изменение сумм: ${formatMoney(drift?.amountDifference ?? 0)} ₽`}>
        Изменилось
      </span>
    );
  }

  return (
    <span
      className="inline-flex rounded-full bg-blue-50 px-2 py-1 text-xs font-medium text-blue-700"
      title={`Сохранено ${formatDate(saved.checkedAt, timeZoneId)}`}>
      Актуально
    </span>
  );
}

function getOperationOriginBadge(item: MoneyOperation) {
  if (item.isReadonly) {
    return {
      label: 'Из графика',
      title: 'Строка построена из платежного графика. Редактируйте исходный платеж или счет.',
      className: 'bg-slate-100 text-slate-500',
    };
  }

  if (isAppliedToInvoice(item)) {
    return {
      label: 'Зачтено в счет',
      title: 'Операция уже зачтена в счет. Удаление и перенос заблокированы, чтобы не задвоить оплату.',
      className: 'bg-blue-50 text-blue-700',
    };
  }

  if (item.isAdvancePayment && isUnallocatedAdvanceMoneyOperation(item)) {
    return {
      label: 'Аванс без счета',
      title: 'Поступление не связано со счетом. Проверьте его как нераспределенный аванс.',
      className: 'bg-rose-50 text-rose-700',
    };
  }

  if (item.cashOrderId) {
    return {
      label: 'Кассовый ордер',
      title: 'Операция связана с кассовым ордером.',
      className: 'bg-emerald-50 text-emerald-700',
    };
  }

  return null;
}

function parseBankStatementAutoImportSettings(value?: string | null): BankStatementAutoImportSettings {
  if (!value) return DEFAULT_BANK_STATEMENT_AUTO_IMPORT_SETTINGS;

  try {
    const parsed = JSON.parse(value) as Partial<BankStatementAutoImportSettings>;

    return {
      enabled: Boolean(parsed.enabled),
      folderPath: typeof parsed.folderPath === 'string' ? parsed.folderPath : '',
      fileMask: typeof parsed.fileMask === 'string' && parsed.fileMask.trim() ? parsed.fileMask : '*.csv;*.txt;*.1c',
      lastCheckedAt: typeof parsed.lastCheckedAt === 'string' ? parsed.lastCheckedAt : null,
    };
  } catch {
    return DEFAULT_BANK_STATEMENT_AUTO_IMPORT_SETTINGS;
  }
}

function serializeBankStatementAutoImportSettings(settings: BankStatementAutoImportSettings) {
  return JSON.stringify({
    enabled: settings.enabled,
    folderPath: settings.folderPath.trim(),
    fileMask: settings.fileMask.trim() || '*.csv;*.txt;*.1c',
    lastCheckedAt: settings.lastCheckedAt ?? null,
  });
}

function createEmptyBankStatementCategoryRule(): BankStatementCategoryRule {
  return {
    id: `rule-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
    enabled: true,
    categoryName: '',
    paymentType: 'Expense',
    keywords: '',
    incomeTypeId: null,
  };
}

function parseBankStatementCategoryRulesSettings(value?: string | null): BankStatementCategoryRulesSettings {
  if (!value) return DEFAULT_BANK_STATEMENT_CATEGORY_RULES_SETTINGS;

  try {
    const parsed = JSON.parse(value) as Partial<BankStatementCategoryRulesSettings>;
    const rules = Array.isArray(parsed.rules)
      ? parsed.rules
          .map((rule, index) => {
            const source = rule as Partial<BankStatementCategoryRule>;
            return {
              id: typeof source.id === 'string' && source.id.trim() ? source.id : `saved-rule-${index}`,
              enabled: source.enabled !== false,
              categoryName: typeof source.categoryName === 'string' ? source.categoryName : '',
              paymentType: source.paymentType === 'Income' ? ('Income' as PaymentKind) : ('Expense' as PaymentKind),
              keywords: typeof source.keywords === 'string' ? source.keywords : '',
              incomeTypeId: typeof source.incomeTypeId === 'number' && source.incomeTypeId > 0 ? source.incomeTypeId : null,
            };
          })
          .filter((rule) => rule.categoryName.trim() || rule.keywords.trim() || rule.incomeTypeId)
      : [];
    return { rules };
  } catch {
    return DEFAULT_BANK_STATEMENT_CATEGORY_RULES_SETTINGS;
  }
}

function serializeBankStatementCategoryRulesSettings(settings: BankStatementCategoryRulesSettings) {
  return JSON.stringify({
    rules: settings.rules
      .map((rule) => ({
        id: rule.id,
        enabled: rule.enabled,
        categoryName: rule.categoryName.trim(),
        paymentType: rule.paymentType,
        keywords: rule.keywords.trim(),
        incomeTypeId: rule.incomeTypeId ?? null,
      }))
      .filter((rule) => rule.categoryName && rule.keywords),
  });
}

function parseCashLimitSettings(value?: string | null): CashLimitSettings {
  if (!value) return DEFAULT_CASH_LIMIT_SETTINGS;

  try {
    const parsed = JSON.parse(value) as Partial<CashLimitSettings>;
    const amount = typeof parsed.amount === 'number' && Number.isFinite(parsed.amount) && parsed.amount > 0
      ? parsed.amount
      : null;
    return {
      enabled: Boolean(parsed.enabled) && amount !== null,
      amount,
    };
  } catch {
    return DEFAULT_CASH_LIMIT_SETTINGS;
  }
}

function serializeCashLimitSettings(settings: CashLimitSettings) {
  const amount = settings.amount && settings.amount > 0 ? settings.amount : null;
  return JSON.stringify({
    enabled: Boolean(settings.enabled && amount),
    amount,
  });
}

function getBankImportStatusLabel(status: string) {
  switch (status) {
    case 'Applied':
      return 'Загружено';
    case 'AppliedWithErrors':
      return 'Загружено с ошибками';
    case 'RolledBack':
      return 'Откат выполнен';
    case 'RollbackBlocked':
      return 'Откат частичный';
    case 'Created':
      return 'Создано';
    case 'Skipped':
      return 'Пропущено';
    case 'Error':
      return 'Ошибка';
    case 'RollbackSkipped':
      return 'Откат пропущен';
    default:
      return status || '—';
  }
}

function getBankImportStatusClassName(status: string) {
  switch (status) {
    case 'Applied':
    case 'Created':
      return 'bg-emerald-50 text-emerald-700';
    case 'AppliedWithErrors':
    case 'RollbackBlocked':
    case 'Skipped':
    case 'RollbackSkipped':
      return 'bg-amber-50 text-amber-700';
    case 'RolledBack':
      return 'bg-slate-100 text-slate-600';
    case 'Error':
      return 'bg-red-50 text-red-700';
    default:
      return 'bg-slate-100 text-slate-600';
  }
}

function mapCashOrderToForm(order: CashOrder): CashOrder {
  return {
    ...order,
    date: toDateInputValue(order.date) || todayYMD(),
    currency: order.currency || 'RUB',
    account: order.account || '',
    payerName: order.payerName || '',
    receiverName: order.receiverName || '',
    basis: order.basis || '',
    appendix: order.appendix || '',
    comment: order.comment || '',
    cancelledReason: order.cancelledReason || '',
  };
}

function formatCashOrderOrigin(order: CashOrder) {
  if (order.originType === 'payment' || order.paymentId) {
    return order.paymentId ? `Платеж #${order.paymentId}` : 'Платеж';
  }

  if (order.originType === 'moneyOperation' || order.moneyOperationId) {
    return order.moneyOperationId ? `Операция денег #${order.moneyOperationId}` : 'Операция денег';
  }

  if (order.originType === 'inventory') {
    return 'Инвентаризация кассы';
  }

  return 'Ручной ордер';
}

function CashOrderModal({
  order,
  open,
  timeZoneId,
  isSaving,
  onClose,
  onSave,
  onPostOrder,
  onCancelOrder,
  onDeleteOrder,
  onCopy,
  onDownloadPdf,
  onDownloadWord,
  onPrint,
  canCreate,
  canEdit,
  canDelete,
  canViewAudit,
}: {
  order: CashOrder | null;
  open: boolean;
  timeZoneId?: string | null;
  isSaving: boolean;
  onClose: () => void;
  onSave: (payload: CashOrder) => Promise<void>;
  onPostOrder: (order: CashOrder) => Promise<void>;
  onCancelOrder: (order: CashOrder, reason?: string) => Promise<void>;
  onDeleteOrder: (order: CashOrder) => Promise<void>;
  onCopy: (order: CashOrder) => Promise<void>;
  onDownloadPdf: (id: number) => Promise<void>;
  onDownloadWord: (id: number) => Promise<void>;
  onPrint: (id: number) => void;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  canViewAudit: boolean;
}) {
  const [form, setForm] = useState<CashOrder | null>(order ? mapCashOrderToForm(order) : null);
  const [error, setError] = useState<string | null>(null);
  const [confirmingCancel, setConfirmingCancel] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [cancelReason, setCancelReason] = useState('');

  useEffect(() => {
    if (open && order) {
      setForm(mapCashOrderToForm(order));
      setError(null);
      setConfirmingCancel(false);
      setConfirmingDelete(false);
      setHelpOpen(false);
      setHistoryOpen(false);
      setCancelReason(order.cancelledReason ?? '');
    }
  }, [open, order]);

  if (!open || !order || !form) return null;

  const isCancelled = form.status === 'Cancelled';
  const isDraft = form.status === 'Draft';
  const isPersisted = form.id > 0;
  const statusLabel = isCancelled ? 'Отменён' : isDraft ? 'Черновик' : 'Проведён';
  const statusClassName = isCancelled
    ? 'bg-rose-50 text-rose-700'
    : isDraft
      ? 'bg-amber-50 text-amber-700'
      : 'bg-emerald-50 text-emerald-700';
  const title = `${form.type === 'Incoming' ? 'Приходный кассовый ордер' : 'Расходный кассовый ордер'} ${form.number || 'Новый'}`;
  const clientLabel = form.clientName ?? '—';
  const paymentLabel = form.paymentId ? `#${form.paymentId}` : '—';
  const invoiceLabel = form.invoiceId ? `#${form.invoiceId}` : '—';
  const operationLabel = form.moneyOperationId ? `#${form.moneyOperationId}` : '—';
  const originLabel = formatCashOrderOrigin(form);
  const createdByLabel = form.createdByUserName || (form.createdByUserId ? `#${form.createdByUserId}` : '—');
  const updatedByLabel = form.updatedByUserName || (form.updatedByUserId ? `#${form.updatedByUserId}` : '—');
  const canSaveCurrentOrder = isPersisted ? canEdit : canCreate;

  const setField = <K extends keyof CashOrder>(key: K, value: CashOrder[K]) => {
    setForm((prev) => (prev ? { ...prev, [key]: value } : prev));
  };

  const handleSave = async () => {
    setError(null);
    if (!canSaveCurrentOrder) {
      setError('У вас нет прав на сохранение кассового ордера.');
      return;
    }

    if (form.amount <= 0) {
      setError('Сумма кассового ордера должна быть больше нуля.');
      return;
    }

    try {
      await onSave({
        ...form,
        date: fromInputToApiDate(form.date) ?? form.date,
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить кассовый ордер.');
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-4">
      <div className="flex max-h-[90vh] w-full max-w-4xl flex-col overflow-hidden rounded-2xl bg-white shadow-xl">
        <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-4">
          <div>
            <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">Кассовый документ</div>
            <h2 className="text-xl font-semibold text-slate-900">{title}</h2>
            <div className="mt-1 flex flex-wrap items-center gap-2 text-sm text-slate-500">
              <span>{formatDate(form.date)}</span>
              <span>•</span>
              <span>{formatMoney(form.amount)} ₽</span>
              <span
                className={['rounded-full px-2 py-0.5 text-xs font-medium', statusClassName].join(' ')}>
                {statusLabel}
              </span>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50"
            aria-label="Закрыть карточку кассового ордера">
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto p-6">
          {error && (
            <div className="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>
          )}

          <div className="grid gap-4 md:grid-cols-3">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Тип
              <SystemSelect
                value={form.type}
                disabled={isCancelled}
                onChange={(event) => setField('type', event.target.value as CashOrderType)}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100">
                <option value="Incoming">Приходный кассовый ордер (ПКО)</option>
                <option value="Outgoing">Расходный кассовый ордер (РКО)</option>
              </SystemSelect>
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Дата
              <SystemDateInput
                type="date"
                value={form.date}
                disabled={isCancelled}
                onChange={(event) => setField('date', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Сумма
              <input
                type="number"
                min="0"
                step="0.01"
                value={form.amount}
                disabled={isCancelled}
                onChange={(event) => setField('amount', Number(event.target.value) || 0)}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Валюта
              <input
                value={form.currency ?? ''}
                disabled={isCancelled}
                onChange={(event) => setField('currency', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Счёт / касса
              <input
                value={form.account ?? ''}
                disabled={isCancelled}
                onChange={(event) => setField('account', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Плательщик
              <input
                value={form.payerName ?? ''}
                disabled={isCancelled}
                onChange={(event) => setField('payerName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Получатель
              <input
                value={form.receiverName ?? ''}
                disabled={isCancelled}
                onChange={(event) => setField('receiverName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
          </div>

          <div className="mt-4 grid gap-4 md:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Основание
              <textarea
                value={form.basis ?? ''}
                disabled={isCancelled}
                onChange={(event) => setField('basis', event.target.value)}
                rows={4}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Приложение
              <textarea
                value={form.appendix ?? ''}
                disabled={isCancelled}
                onChange={(event) => setField('appendix', event.target.value)}
                rows={4}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Комментарий
              <textarea
                value={form.comment ?? ''}
                disabled={isCancelled}
                onChange={(event) => setField('comment', event.target.value)}
                rows={4}
                className="rounded-lg border border-slate-200 px-3 py-2 disabled:bg-slate-100"
              />
            </label>
          </div>

          {isCancelled && (
            <div className="mt-5 rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-700">
              <div className="font-semibold">Документ отменён</div>
              <div className="mt-1">
                {form.cancelledAt ? `Дата отмены: ${formatDate(form.cancelledAt, timeZoneId)}.` : null}
                {form.cancelledReason ? ` Причина: ${form.cancelledReason}` : ' Причина не указана.'}
              </div>
            </div>
          )}

          <div className="mt-5 grid gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4 text-sm text-slate-600 md:grid-cols-4">
            <div>
              <div className="text-xs uppercase text-slate-400">Источник</div>
              <div className="mt-1 font-medium text-slate-800">{originLabel}</div>
            </div>
            <div>
              <div className="text-xs uppercase text-slate-400">Клиент</div>
              <div className="mt-1 font-medium text-slate-800">{clientLabel}</div>
            </div>
            <div>
              <div className="text-xs uppercase text-slate-400">Связанный платёж</div>
              <div className="mt-1 font-medium text-slate-800">{paymentLabel}</div>
            </div>
            <div>
              <div className="text-xs uppercase text-slate-400">Связанный счёт</div>
              <div className="mt-1 font-medium text-slate-800">{invoiceLabel}</div>
            </div>
            <div>
              <div className="text-xs uppercase text-slate-400">Операция денег</div>
              <div className="mt-1 font-medium text-slate-800">{operationLabel}</div>
            </div>
            <div>
              <div className="text-xs uppercase text-slate-400">Создан</div>
              <div className="mt-1 font-medium text-slate-800">{formatDate(form.createdAt, timeZoneId)}</div>
              <div className="mt-0.5 text-xs text-slate-500">{createdByLabel}</div>
            </div>
            <div>
              <div className="text-xs uppercase text-slate-400">Обновлён</div>
              <div className="mt-1 font-medium text-slate-800">{form.updatedAt ? formatDate(form.updatedAt, timeZoneId) : '—'}</div>
              <div className="mt-0.5 text-xs text-slate-500">{updatedByLabel}</div>
            </div>
          </div>
        </div>

        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 px-6 py-4">
          <div className="flex flex-wrap items-center gap-2">
            {confirmingCancel && (
              <input
                value={cancelReason}
                onChange={(event) => setCancelReason(event.target.value)}
                placeholder="Причина отмены"
                className="min-w-64 rounded-lg border border-rose-200 px-3 py-2 text-sm text-slate-700"
              />
            )}
            <button
              type="button"
              onClick={() => onPrint(form.id)}
              disabled={!isPersisted}
              className="inline-flex items-center gap-2 rounded-lg border border-emerald-200 px-3 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-50 disabled:opacity-50">
              <Printer className="h-4 w-4" />
              Печать
            </button>
            {canViewAudit ? (
              <button
                type="button"
                onClick={() => setHistoryOpen(true)}
                disabled={!isPersisted}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50">
                <History className="h-4 w-4" />
                История
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => setHelpOpen(true)}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50">
              <HelpCircle className="h-4 w-4" />
              Справка
            </button>
            <button
              type="button"
              onClick={() => void onDownloadPdf(form.id)}
              disabled={!isPersisted || isSaving}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50">
              <Download className="h-4 w-4" />
              PDF
            </button>
            <button
              type="button"
              onClick={() => void onDownloadWord(form.id)}
              disabled={!isPersisted || isSaving}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50">
              <Download className="h-4 w-4" />
              Word
            </button>
            <button
              type="button"
              onClick={() => void onCopy(form)}
              disabled={!isPersisted || isSaving || !canEdit}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
              <Copy className="h-4 w-4" />
              Копировать
            </button>
            {isDraft && isPersisted && (
              <button
                type="button"
                onClick={() => void onPostOrder(form)}
                disabled={isSaving || !canEdit}
                className="inline-flex items-center gap-2 rounded-lg border border-emerald-200 px-3 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-50 disabled:opacity-50">
                <CheckCircle2 className="h-4 w-4" />
                Провести
              </button>
            )}
            <button
              type="button"
              onClick={() => {
                if (!confirmingCancel) {
                  setConfirmingCancel(true);
                  return;
                }

                void onCancelOrder(form, cancelReason.trim() || undefined);
              }}
              disabled={!isPersisted || isCancelled || isSaving || !canEdit}
              className="inline-flex items-center gap-2 rounded-lg border border-rose-200 px-3 py-2 text-sm font-medium text-rose-700 hover:bg-rose-50 disabled:opacity-50">
              <Ban className="h-4 w-4" />
              {confirmingCancel ? 'Подтвердить отмену' : 'Отменить'}
            </button>
            {confirmingCancel && (
              <button
                type="button"
                onClick={() => setConfirmingCancel(false)}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50">
                Назад
              </button>
            )}
            {isDraft && isPersisted && (
              <button
                type="button"
                onClick={() => {
                  if (!confirmingDelete) {
                    setConfirmingDelete(true);
                    return;
                  }

                  void onDeleteOrder(form);
                }}
                disabled={isSaving || !canDelete}
                className="inline-flex items-center gap-2 rounded-lg border border-red-200 px-3 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:opacity-50">
                <Trash2 className="h-4 w-4" />
                {confirmingDelete ? 'Подтвердить удаление' : 'Удалить'}
              </button>
            )}
            {confirmingDelete && (
              <button
                type="button"
                onClick={() => setConfirmingDelete(false)}
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50">
                Не удалять
              </button>
            )}
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50">
              Закрыть
            </button>
            <button
              type="button"
              onClick={() => void handleSave()}
              disabled={isCancelled || isSaving || !canSaveCurrentOrder}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50">
              <Save className="h-4 w-4" />
              {isSaving ? 'Сохранение...' : 'Сохранить'}
            </button>
          </div>
        </div>
      </div>
      <ObjectAuditModal
        open={historyOpen}
        onClose={() => setHistoryOpen(false)}
        objectType="cash-order"
        objectId={form.id}
        createdAt={form.createdAt}
        updatedAt={form.updatedAt}
        title={`История кассового ордера ${form.number || 'Новый'}`}
        subtitle="Здесь видны создание, изменения, отмена и удаление кассового ордера."
      />
      {helpOpen && (
        <div className="fixed inset-0 z-[2400] flex items-center justify-center bg-slate-950/45 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white shadow-2xl">
            <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-4">
              <div>
                <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">Справка</p>
                <h3 className="mt-1 text-lg font-semibold text-slate-900">Как работает кассовый ордер</h3>
              </div>
              <button
                type="button"
                onClick={() => setHelpOpen(false)}
                className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50"
                aria-label="Закрыть справку">
                <X className="h-4 w-4" />
              </button>
            </div>
            <div className="space-y-4 px-6 py-5 text-sm leading-6 text-slate-600">
              <p>
                Приходный кассовый ордер фиксирует поступление наличных в кассу, Расходный кассовый ордер фиксирует
                выдачу наличных из кассы. Проведённые ордера
                участвуют в кассовой книге, оборотах и остатках.
              </p>
              <ul className="list-disc space-y-2 pl-5">
                <li>Черновик можно сохранить, удалить или провести позже.</li>
                <li>Проведённый ордер можно печатать, скачать в PDF, копировать или отменить.</li>
                <li>Отменённый ордер остаётся в журнале для истории, но не участвует в остатках кассы.</li>
              </ul>
            </div>
          </div>
        </div>
      )}
    </div>,
    document.body,
  );
}

function CashOrdersPanel({
  from,
  to,
  search,
  accountName,
  refreshKey,
  onCreate,
  onOpen,
  onPrint,
  canCreate,
  canExport,
}: {
  from: string;
  to: string;
  search: string;
  accountName: string;
  refreshKey: number;
  onCreate: () => void;
  onOpen: (cashOrderId: number) => void;
  onPrint: (cashOrderId: number) => void;
  canCreate: boolean;
  canExport: boolean;
}) {
  const [orders, setOrders] = useState<CashOrder[]>([]);
  const [cashBook, setCashBook] = useState<CashBookSummary | null>(null);
  const [registerSummary, setRegisterSummary] = useState<CashOrderRegister | null>(null);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [typeFilter, setTypeFilter] = useState<CashOrderType | 'all'>('all');
  const [statusFilter, setStatusFilter] = useState<'all' | 'Draft' | 'Posted' | 'Cancelled'>('all');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pageSize = 10;
  const queryScopeKey = JSON.stringify([accountName, from, search, statusFilter, to, typeFilter]);
  const queryScopeKeyRef = useRef(queryScopeKey);

  const openPrintHtml = (html: string) => {
    const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    window.open(url, '_blank', 'noopener,noreferrer');
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
  };

  const handlePrintCashBook = async () => {
    try {
      const html = await apiService.getCashBookPrintHtml({ from, to, accountName: accountName || undefined });
      openPrintHtml(html);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось открыть печатную форму кассовой книги.'));
    }
  };

  const handlePrintRegister = async () => {
    try {
      const html = await apiService.getCashOrderRegisterPrintHtml({
        from,
        to,
        accountName: accountName || undefined,
        type: typeFilter === 'all' ? undefined : typeFilter,
        status: statusFilter === 'all' ? undefined : statusFilter,
        search,
      });
      openPrintHtml(html);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось открыть печатную форму журнала регистрации.'));
    }
  };

  useEffect(() => {
    if (queryScopeKeyRef.current !== queryScopeKey) {
      queryScopeKeyRef.current = queryScopeKey;
      if (page !== 1) {
        setPage(1);
        return;
      }
    }

    const controller = new AbortController();
    setLoading(true);
    setError(null);

    Promise.all([
      apiService.getCashOrderRegister({
        from,
        to,
        type: typeFilter === 'all' ? undefined : typeFilter,
        status: statusFilter === 'all' ? undefined : statusFilter,
        search,
        accountName: accountName || undefined,
        page,
        pageSize,
      }, { signal: controller.signal }),
      apiService.getCashBook(
        { from, to, accountName: accountName || undefined },
        { signal: controller.signal },
      ),
    ])
      .then(([data, book]) => {
        if (controller.signal.aborted) return;
        setOrders(data.items);
        setTotal(data.total);
        setRegisterSummary(data);
        setCashBook(book);
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        setOrders([]);
        setTotal(0);
        setRegisterSummary(null);
        setCashBook(null);
        setError(extractDisplayErrorMessage(err, 'Не удалось загрузить кассовые ордера.'));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => {
      controller.abort();
    };
  }, [accountName, from, page, queryScopeKey, refreshKey, search, statusFilter, to, typeFilter]);

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  return (
    <div className="mt-6 min-w-0 rounded-xl border border-slate-200 bg-white">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 px-4 py-3">
        <div>
          <h2 className="text-lg font-semibold text-slate-900">Кассовые ордера</h2>
          <p className="text-sm text-slate-500">Приходные и расходные кассовые ордера по выбранному периоду кассы</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {canCreate && (
            <button
              type="button"
              onClick={onCreate}
              className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-emerald-700">
              <Plus className="h-4 w-4" />
              Создать ордер
            </button>
          )}
          <button
            type="button"
            onClick={() => void handlePrintCashBook()}
            disabled={!canExport}
            className="inline-flex items-center gap-2 rounded-lg border border-amber-200 px-3 py-1.5 text-sm font-medium text-amber-700 hover:bg-amber-50">
            <Printer className="h-4 w-4" />
            Кассовая книга
          </button>
          <button
            type="button"
            onClick={() => void handlePrintRegister()}
            disabled={!canExport}
            className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-3 py-1.5 text-sm font-medium text-blue-700 hover:bg-blue-50">
            <Printer className="h-4 w-4" />
            Журнал
          </button>
          {([
            { key: 'all', label: 'Все' },
            { key: 'Incoming', label: 'Приходный кассовый ордер' },
            { key: 'Outgoing', label: 'Расходный кассовый ордер' },
          ] as const).map((item) => (
            <button
              key={item.key}
              type="button"
              onClick={() => setTypeFilter(item.key)}
              className={[
                'rounded-lg border px-3 py-1.5 text-sm font-medium transition-colors',
                typeFilter === item.key
                  ? 'border-slate-900 bg-slate-900 text-white'
                  : 'border-slate-200 text-slate-600 hover:bg-slate-50',
              ].join(' ')}>
              {item.label}
            </button>
          ))}
          <span className="mx-1 h-6 w-px bg-slate-200" aria-hidden="true" />
          {([
            { key: 'all', label: 'Все статусы' },
            { key: 'Draft', label: 'Черновики' },
            { key: 'Posted', label: 'Проведён' },
            { key: 'Cancelled', label: 'Отменён' },
          ] as const).map((item) => (
            <button
              key={item.key}
              type="button"
              onClick={() => setStatusFilter(item.key)}
              className={[
                'rounded-lg border px-3 py-1.5 text-sm font-medium transition-colors',
                statusFilter === item.key
                  ? 'border-slate-900 bg-slate-900 text-white'
                  : 'border-slate-200 text-slate-600 hover:bg-slate-50',
              ].join(' ')}>
              {item.label}
            </button>
          ))}
          <div className="ml-2 text-sm text-slate-500">Всего: {total}</div>
        </div>
      </div>

      {registerSummary && (
        <div className="grid gap-3 border-b border-slate-200 px-4 py-3 md:grid-cols-5">
          <div className="rounded-lg bg-emerald-50 px-3 py-2">
            <div className="text-xs uppercase text-emerald-700/70">Приходные ордера</div>
            <div className="mt-1 text-base font-semibold text-emerald-700">
              {registerSummary.incomingCount} на {formatMoney(registerSummary.incomingAmount)} ₽
            </div>
          </div>
          <div className="rounded-lg bg-rose-50 px-3 py-2">
            <div className="text-xs uppercase text-rose-700/70">Расходные ордера</div>
            <div className="mt-1 text-base font-semibold text-rose-700">
              {registerSummary.outgoingCount} на {formatMoney(registerSummary.outgoingAmount)} ₽
            </div>
          </div>
          <div className="rounded-lg bg-slate-50 px-3 py-2">
            <div className="text-xs uppercase text-slate-500">Черновики</div>
            <div className="mt-1 text-base font-semibold text-slate-800">{registerSummary.draftCount}</div>
          </div>
          <div className="rounded-lg bg-slate-50 px-3 py-2">
            <div className="text-xs uppercase text-slate-500">Проведено</div>
            <div className="mt-1 text-base font-semibold text-slate-800">{registerSummary.postedCount}</div>
          </div>
          <div className="rounded-lg bg-slate-50 px-3 py-2">
            <div className="text-xs uppercase text-slate-500">Отменено</div>
            <div className="mt-1 text-base font-semibold text-slate-800">{registerSummary.cancelledCount}</div>
          </div>
        </div>
      )}

      {cashBook && (
        <div
          data-testid="cash-book-summary"
          className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-3 border-b border-slate-200 bg-slate-50/60 p-3 sm:p-4 md:grid-cols-4">
          <div className="min-w-0 md:col-span-4">
            <div className="inline-flex max-w-full items-center rounded-full border border-slate-200 bg-white px-3 py-1 text-xs font-medium text-slate-600">
              Касса: {cashBook.accountName || accountName || 'Все кассы'}
            </div>
          </div>
          <div className="min-w-0 rounded-lg border border-slate-200 bg-white p-3">
            <div className="text-xs uppercase text-slate-400">Остаток на начало</div>
            <div className="mt-1 text-lg font-semibold text-amber-600">{formatMoney(cashBook.openingBalance)} ₽</div>
            <div className="mt-1 text-xs text-slate-400">{formatDate(cashBook.from)}</div>
          </div>
          <div className="min-w-0 rounded-lg border border-slate-200 bg-white p-3">
            <div className="text-xs uppercase text-slate-400">Приход</div>
            <div className="mt-1 text-lg font-semibold text-emerald-600">{formatMoney(cashBook.income)} ₽</div>
            <div className="mt-1 text-xs text-slate-400">Приходных ордеров: {cashBook.incomingCount}</div>
          </div>
          <div className="min-w-0 rounded-lg border border-slate-200 bg-white p-3">
            <div className="text-xs uppercase text-slate-400">Расход</div>
            <div className="mt-1 text-lg font-semibold text-rose-600">{formatMoney(cashBook.expense)} ₽</div>
            <div className="mt-1 text-xs text-slate-400">Расходных ордеров: {cashBook.outgoingCount}</div>
          </div>
          <div className="min-w-0 rounded-lg border border-slate-200 bg-white p-3">
            <div className="text-xs uppercase text-slate-400">Остаток на конец</div>
            <div className="mt-1 text-lg font-semibold text-amber-600">{formatMoney(cashBook.closingBalance)} ₽</div>
            <div className="mt-1 text-xs text-slate-400">{formatDate(cashBook.to)}</div>
          </div>
          {cashBook.days.length > 0 && (
            <div className="min-w-0 md:col-span-4">
              <div
                data-testid="cash-book-days-scroll"
                className="max-w-full overflow-x-auto rounded-lg border border-slate-200 bg-white">
                <table className="w-full min-w-[720px] text-sm">
                  <thead className="bg-white text-xs uppercase text-slate-400">
                    <tr>
                      <th className="px-3 py-2 text-left font-medium">День</th>
                      <th className="px-3 py-2 text-right font-medium">Начало</th>
                      <th className="px-3 py-2 text-right font-medium">Приход</th>
                      <th className="px-3 py-2 text-right font-medium">Расход</th>
                      <th className="px-3 py-2 text-right font-medium">Конец</th>
                      <th className="px-3 py-2 text-right font-medium">Документы</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {cashBook.days.map((day) => (
                      <tr key={day.date}>
                        <td className="px-3 py-2 font-medium text-slate-800">{formatDate(day.date)}</td>
                        <td className="px-3 py-2 text-right">{formatMoney(day.openingBalance)} ₽</td>
                        <td className="px-3 py-2 text-right text-emerald-600">{formatMoney(day.income)} ₽</td>
                        <td className="px-3 py-2 text-right text-rose-600">{formatMoney(day.expense)} ₽</td>
                        <td className="px-3 py-2 text-right font-medium">{formatMoney(day.closingBalance)} ₽</td>
                        <td className="px-3 py-2 text-right text-slate-500">
                          Приходных ордеров: {day.incomingCount}, расходных: {day.outgoingCount}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}

      <div className="overflow-x-auto">
        <table className="w-full min-w-[900px] table-fixed text-sm">
          <colgroup>
            <col style={{ width: 140 }} />
            <col style={{ width: 120 }} />
            <col style={{ width: 120 }} />
            <col style={{ width: 150 }} />
            <col style={{ width: 220 }} />
            <col style={{ width: 190 }} />
            <col style={{ width: 260 }} />
            <col style={{ width: 90 }} />
          </colgroup>
          <thead className="bg-slate-50 text-slate-500">
            <tr>
              <th className="px-3 py-2 text-left font-medium">Номер</th>
              <th className="px-3 py-2 text-left font-medium">Дата</th>
              <th className="px-3 py-2 text-left font-medium">Тип</th>
              <th className="px-3 py-2 text-left font-medium">Сумма</th>
              <th className="px-3 py-2 text-left font-medium">Плательщик</th>
              <th className="px-3 py-2 text-left font-medium">Источник</th>
              <th className="px-3 py-2 text-left font-medium">Основание</th>
              <th className="px-3 py-2 text-left font-medium">Действия</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {loading ? (
              <tr>
                <td colSpan={8} className="px-4 py-8 text-center text-slate-500">
                  Загрузка кассовых ордеров...
                </td>
              </tr>
            ) : error ? (
              <tr>
                <td colSpan={8} className="px-4 py-8 text-center text-red-500">
                  {error}
                </td>
              </tr>
            ) : orders.length === 0 ? (
              <tr>
                <td colSpan={8} className="px-4 py-8 text-center text-slate-500">
                  За выбранный период кассовых ордеров нет.
                </td>
              </tr>
            ) : (
              orders.map((order) => (
                <tr key={order.id} className="hover:bg-slate-50">
                  <td className="px-3 py-2 font-medium text-slate-900">
                    <button
                      type="button"
                      onClick={() => onOpen(order.id)}
                      className="text-left text-blue-700 hover:text-blue-900 hover:underline">
                      {order.number}
                    </button>
                  </td>
                  <td className="px-3 py-2">{formatDate(order.date)}</td>
                  <td className="px-3 py-2">
                    <span
                      className={[
                        'inline-flex rounded-full px-2 py-1 text-xs font-medium',
                        order.type === 'Incoming'
                          ? 'bg-emerald-50 text-emerald-700'
                          : 'bg-rose-50 text-rose-700',
                      ].join(' ')}>
                      {order.type === 'Incoming' ? 'Приходный кассовый ордер' : 'Расходный кассовый ордер'}
                    </span>
                  </td>
                  <td className="px-3 py-2 font-medium">{formatMoney(order.amount)} ₽</td>
                  <td className="px-3 py-2 text-slate-700">{order.payerName ?? order.clientName ?? '—'}</td>
                  <td className="px-3 py-2 text-slate-700">{formatCashOrderOrigin(order)}</td>
                  <td className="px-3 py-2">
                    <div className="line-clamp-2 text-slate-700" title={order.basis ?? undefined}>
                      {order.basis ?? '—'}
                    </div>
                  </td>
                  <td className="px-3 py-2">
                    <button
                      type="button"
                      onClick={() => onOpen(order.id)}
                      className="mr-2 rounded-md border border-blue-200 p-2 text-blue-700 hover:bg-blue-50"
                      aria-label="Открыть карточку кассового ордера"
                      title="Открыть карточку кассового ордера">
                      <ReceiptText className="h-4 w-4" />
                    </button>
                    <button
                      type="button"
                      onClick={() => onPrint(order.id)}
                      className="rounded-md border border-emerald-200 p-2 text-emerald-700 hover:bg-emerald-50"
                      aria-label="Печать кассового ордера"
                      title="Печать кассового ордера">
                      <Printer className="h-4 w-4" />
                    </button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      <TablePagination
        page={page}
        totalPages={totalPages}
        totalItems={total}
        pageSize={pageSize}
        pageSizeOptions={[pageSize]}
        onPageChange={setPage}
        onPageSizeChange={() => undefined}
        recordsLabel="Найдено ордеров"
        pageSizeLabel="Строк на странице"
        previousLabel="Назад"
        nextLabel="Вперед"
      />
    </div>
  );
}

function MoneyOperationModal({
  source,
  initialData,
  moneyAccounts,
  responsibles,
  categoryOptions,
  open,
  onClose,
  onSave,
  isSaving,
}: {
  source: MoneyOperationSource;
  initialData?: MoneyOperation | null;
  moneyAccounts?: MoneyAccount[];
  responsibles?: ActResponsible[];
  categoryOptions?: MoneyOperationCategoryOption[];
  open: boolean;
  onClose: () => void;
  onSave: (payload: MoneyOperation) => Promise<void>;
  isSaving: boolean;
}) {
  const [form, setForm] = useState<MoneyOperation>(() => mapMoneyOperationToForm(source, initialData));
  const [error, setError] = useState<string | null>(null);
  const [invoiceCandidates, setInvoiceCandidates] = useState<MoneyOperationInvoiceCandidate[]>([]);
  const [invoiceCandidatesLoading, setInvoiceCandidatesLoading] = useState(false);
  const activeMoneyAccounts = useMemo(() => (moneyAccounts ?? []).filter((account) => account.isActive), [moneyAccounts]);
  const sourceMoneyAccounts = useMemo(
    () => (moneyAccounts ?? []).filter((account) => account.source === source && account.isActive),
    [moneyAccounts, source],
  );
  const transferTargetAccounts = useMemo(
    () => activeMoneyAccounts.filter((account) => account.id !== form.moneyAccountId),
    [activeMoneyAccounts, form.moneyAccountId],
  );
  const modalCategoryOptions = useMemo(() => {
    const paymentKind = getPaymentKindForDirection(form.direction);
    return paymentKind
      ? (categoryOptions ?? []).filter((option) => option.paymentType === paymentKind)
      : [];
  }, [categoryOptions, form.direction]);

  useEffect(() => {
    if (open) {
      const nextForm = mapMoneyOperationToForm(source, initialData);
      const defaultAccount = sourceMoneyAccounts.find((account) => account.isDefault) ?? sourceMoneyAccounts[0];
      setForm(
        !initialData && defaultAccount
          ? {
              ...nextForm,
              moneyAccountId: defaultAccount.id,
              moneyAccountName: defaultAccount.name,
              accountName: defaultAccount.name,
              source: defaultAccount.source,
              currency: defaultAccount.currency || nextForm.currency || 'RUB',
            }
          : nextForm,
      );
      setError(null);
    }
  }, [initialData, open, source, sourceMoneyAccounts]);

  useEffect(() => {
    if (!open || !initialData?.id || initialData.id < 0 || initialData.direction !== 'Income') {
      setInvoiceCandidates([]);
      setInvoiceCandidatesLoading(false);
      return;
    }

    let cancelled = false;
    setInvoiceCandidatesLoading(true);
    apiService
      .getMoneyOperationInvoiceCandidates(initialData.id)
      .then((items) => {
        if (!cancelled) {
          setInvoiceCandidates(items);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setInvoiceCandidates([]);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setInvoiceCandidatesLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [initialData?.direction, initialData?.id, open, source]);

  if (!open) return null;

  const setField = <K extends keyof MoneyOperation>(key: K, value: MoneyOperation[K]) => {
    setForm((prev) => ({ ...prev, [key]: value }));
  };
  const setAmount = (amount: number) => {
    setForm((prev) => ({
      ...prev,
      amount,
      vatAmount: calculateVatAmount(amount, prev.vatRate),
    }));
  };
  const setVatRate = (vatRate: number | null) => {
    setForm((prev) => ({
      ...prev,
      vatRate,
      vatAmount: calculateVatAmount(prev.amount, vatRate),
    }));
  };

  const handleDirectionChange = (direction: MoneyOperationDirection) => {
    setForm((prev) => {
      const selectedCategory = findCategoryOption(categoryOptions, direction, prev.categoryName);
      return {
        ...prev,
        direction,
        incomeTypeId: direction === 'Transfer' ? null : selectedCategory?.id ?? null,
        categoryName: direction === 'Transfer' ? '' : prev.categoryName,
        linkedInvoiceId: direction === 'Transfer' ? null : prev.linkedInvoiceId,
        paidInvoiceNumber: direction === 'Transfer' ? '' : prev.paidInvoiceNumber,
        paidAmount: direction === 'Transfer' ? 0 : prev.paidAmount,
      };
    });
  };

  const applyAccountableAdvancePreset = () => {
    setForm((prev) => {
      const selectedCategory = findCategoryOption(categoryOptions, 'Expense', ACCOUNTABLE_ADVANCE_CATEGORY);

      return {
        ...prev,
        direction: 'Expense',
        isAdvancePayment: true,
        incomeTypeId: selectedCategory?.id ?? null,
        categoryName: ACCOUNTABLE_ADVANCE_CATEGORY,
        purpose: prev.purpose?.trim() ? prev.purpose : ACCOUNTABLE_ADVANCE_PURPOSE,
        linkedInvoiceId: null,
        paidInvoiceNumber: '',
        paidAccountName: '',
        paidAmount: 0,
        matchStatus: prev.matchStatus === 'Matched' ? 'None' : prev.matchStatus,
      };
    });
  };

  const handleCategoryChange = (value: string) => {
    setForm((prev) => {
      const selectedCategory = findCategoryOption(categoryOptions, prev.direction, value);
      return {
        ...prev,
        categoryName: value,
        incomeTypeId: selectedCategory?.id ?? null,
      };
    });
  };

  const handleMoneyAccountChange = (value: string) => {
    const accountId = Number(value);
    const account = sourceMoneyAccounts.find((item) => item.id === accountId);
    setForm((prev) => ({
      ...prev,
      moneyAccountId: account?.id ?? null,
      moneyAccountName: account?.name ?? null,
      accountName: account?.name ?? '',
      source: account?.source ?? source,
      currency: account?.currency || prev.currency || 'RUB',
      transferTargetMoneyAccountId:
        prev.transferTargetMoneyAccountId === account?.id ? null : prev.transferTargetMoneyAccountId,
    }));
  };

  const handleSubmit = async () => {
    setError(null);
    if (!Number.isFinite(form.amount) || form.amount <= 0) {
      setError('Сумма денежной операции должна быть больше нуля.');
      return;
    }

    if (!form.moneyAccountId && !form.accountName?.trim()) {
      setError('Укажите денежный счёт или его название.');
      return;
    }

    if (form.direction === 'Transfer') {
      if (!form.moneyAccountId || !form.transferTargetMoneyAccountId) {
        setError('Выберите счёт списания и счёт зачисления для перевода.');
        return;
      }

      if (form.moneyAccountId === form.transferTargetMoneyAccountId) {
        setError('Для перевода выберите два разных денежных счёта.');
        return;
      }
    }

    try {
      await onSave({
        ...form,
        date: fromInputToApiDate(form.date) ?? form.date,
        recognitionDate: fromInputToApiDate(form.recognitionDate) ?? '',
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Не удалось сохранить операцию.';
      setError(message);
    }
  };

  const handleSelectInvoiceCandidate = (candidate: MoneyOperationInvoiceCandidate) => {
    setForm((prev) => ({
      ...prev,
      linkedInvoiceId: candidate.invoiceId,
      paidInvoiceNumber: candidate.invoiceNumber,
      paidAccountName: candidate.invoiceNumber,
      matchStatus: 'Matched',
      matchComment: prev.matchComment || `Выбрано из кандидатов сверки: счёт ${candidate.invoiceNumber}.`,
    }));
  };

  const title = initialData
    ? `Редактировать операцию по ${SOURCE_MODAL_LABELS[source]}`
    : `Добавить операцию по ${SOURCE_MODAL_LABELS[source]}`;
  const appliedInvoiceLocked = initialData ? isAppliedToInvoice(initialData) : false;
  const isAccountableAdvance = form.direction === 'Expense' && form.isAdvancePayment;
  const counterpartyLabel =
    form.direction === 'Income'
      ? 'Покупатель'
      : form.direction === 'Transfer'
        ? 'Получатель'
        : isAccountableAdvance
          ? 'Подотчётное лицо'
          : 'Поставщик';
  const counterpartyPlaceholder = isAccountableAdvance ? 'Сотрудник или получатель' : 'Контрагент';

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-4">
      <div className="flex max-h-[90vh] w-full max-w-4xl flex-col overflow-hidden rounded-2xl bg-white shadow-xl">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <div>
            <h2 className="text-xl font-semibold text-slate-900">{title}</h2>
            <p className="text-sm text-slate-500">Заполните основные параметры операции.</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="text-slate-400 hover:text-slate-600"
            aria-label="Закрыть форму денежной операции">
            ✕
          </button>
        </div>

        <div className="flex-1 space-y-6 overflow-y-auto p-6">
          <div className="flex flex-wrap items-center gap-4">
            <div className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2">
              <span className="text-sm text-slate-500">Операция</span>
              <SystemSelect className="border-none bg-transparent text-sm font-medium text-slate-700 focus:outline-none">
                <option>1 операция</option>
                <option>2 операции</option>
              </SystemSelect>
            </div>
            <div className="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2">
              <span className="text-sm text-slate-500">Учёт</span>
              <SystemSelect
                value={form.accountingType}
                onChange={(event) => setField('accountingType', event.target.value as MoneyOperationAccountingType)}
                className="border-none bg-transparent text-sm font-medium text-slate-700 focus:outline-none">
                <option value="Fact">Факт</option>
                <option value="Plan">План</option>
              </SystemSelect>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-6 text-sm font-medium text-slate-700">
            {(['Income', 'Expense', 'Transfer'] as MoneyOperationDirection[]).map((direction) => (
              <label key={direction} className="flex items-center gap-2">
                <input
                  type="radio"
                  name="direction"
                  checked={form.direction === direction}
                  onChange={() => handleDirectionChange(direction)}
                />
                {direction === 'Income' ? 'Приход' : direction === 'Expense' ? 'Расход' : 'Между счетами'}
              </label>
            ))}
          </div>

          {form.direction !== 'Transfer' && (
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                data-testid="money-accountable-advance-preset"
                aria-pressed={isAccountableAdvance}
                onClick={applyAccountableAdvancePreset}
                className={`rounded-lg border px-3 py-2 text-sm font-medium transition ${
                  isAccountableAdvance
                    ? 'border-amber-300 bg-amber-100 text-amber-900'
                    : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
                }`}>
                Выдача под отчёт
              </button>
            </div>
          )}

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              {SOURCE_ACCOUNT_FILTER_LABELS[source]}
              {sourceMoneyAccounts.length > 0 ? (
                <SystemSelect
                  value={form.moneyAccountId ?? ''}
                  onChange={(event) => handleMoneyAccountChange(event.target.value)}
                  className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none">
                  <option value="">Выберите денежный счёт</option>
                  {sourceMoneyAccounts.map((account) => (
                    <option key={account.id} value={account.id}>
                      {account.name}
                    </option>
                  ))}
                </SystemSelect>
              ) : (
                <input
                  value={form.accountName ?? ''}
                  onChange={(event) => setField('accountName', event.target.value)}
                  className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                  placeholder="Основной счёт"
                />
              )}
            </label>
            {form.direction === 'Transfer' && (
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Куда перевести
                <SystemSelect
                  value={form.transferTargetMoneyAccountId ?? ''}
                  onChange={(event) =>
                    setField('transferTargetMoneyAccountId', event.target.value ? Number(event.target.value) : null)
                  }
                  className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none">
                  <option value="">Выберите счёт зачисления</option>
                  {transferTargetAccounts.map((account) => (
                    <option key={account.id} value={account.id}>
                      {account.name}
                    </option>
                  ))}
                </SystemSelect>
              </label>
            )}
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              {counterpartyLabel}
              <input
                data-testid="money-operation-counterparty"
                value={form.counterpartyName ?? ''}
                onChange={(event) => setField('counterpartyName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder={counterpartyPlaceholder}
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Ответственный
              <SystemSelect
                value={form.responsibleUserId ?? ''}
                onChange={(event) => {
                  const nextId = event.target.value ? Number(event.target.value) : null;
                  const selected = (responsibles ?? []).find((item) => item.id === nextId);
                  setField('responsibleUserId', nextId);
                  setField('responsibleName', selected?.fullName ?? null);
                }}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none">
                <option value="">Без ответственного</option>
                {(responsibles ?? []).map((responsible) => (
                  <option key={responsible.id} value={responsible.id}>
                    {responsible.fullName}
                  </option>
                ))}
              </SystemSelect>
            </label>
            {form.direction !== 'Transfer' && (
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                {form.direction === 'Income' ? 'Статья прихода' : 'Статья расхода'}
                <input
                  value={form.categoryName ?? ''}
                  onChange={(event) => handleCategoryChange(event.target.value)}
                  list={`money-operation-category-options-${source}`}
                  className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                  placeholder="Статья"
                />
                <datalist id={`money-operation-category-options-${source}`}>
                  {modalCategoryOptions.map((option) => (
                    <option key={`${option.paymentType}-${option.id}`} value={option.name} />
                  ))}
                </datalist>
              </label>
            )}
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Номер ПП
              <input
                value={form.paymentOrderNumber ?? ''}
                onChange={(event) => setField('paymentOrderNumber', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Например 873864"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Сумма, RUB
              <input
                type="number"
                value={form.amount}
                onChange={(event) => setAmount(Number(event.target.value))}
                disabled={appliedInvoiceLocked}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              НДС, %
              <SystemSelect
                value={form.vatRate ?? ''}
                onChange={(event) => setVatRate(event.target.value === '' ? null : Number(event.target.value))}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none">
                <option value="">Без НДС</option>
                <option value="0">0%</option>
                <option value="5">5%</option>
                <option value="7">7%</option>
                <option value="10">10%</option>
                <option value="20">20%</option>
              </SystemSelect>
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Сумма НДС
              <input
                type="number"
                value={form.vatAmount ?? ''}
                readOnly
                className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-slate-600"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Дата
              <SystemDateInput
                type="date"
                value={form.date}
                onChange={(event) => setField('date', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Дата признания {form.direction === 'Income' ? 'дохода' : 'расхода'}
              <SystemDateInput
                type="date"
                value={form.recognitionDate ?? ''}
                onChange={(event) => setField('recognitionDate', event.target.value)}
                aria-label={`Дата признания ${form.direction === 'Income' ? 'дохода' : 'расхода'}`}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            <label className="flex items-center gap-2 text-sm text-slate-600">
              <SystemCheckbox
                type="checkbox"
                data-testid="money-advance-checkbox"
                checked={form.isAdvancePayment}
                onChange={(event) => setField('isAdvancePayment', event.target.checked)}
              />
              {form.direction === 'Expense' ? 'Выдача под отчёт / аванс' : 'Авансовый платёж'}
            </label>
          </div>

          <label className="flex flex-col gap-1 text-sm text-slate-600">
            Назначение
            <textarea
              value={form.purpose ?? ''}
              onChange={(event) => setField('purpose', event.target.value)}
              className="min-h-[90px] rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              placeholder="Назначение платежа"
            />
          </label>

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Оплаченные счета
              <input
                value={form.paidInvoiceNumber ?? ''}
                onChange={(event) => setField('paidInvoiceNumber', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Номер счёта"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              На сумму
              <input
                type="number"
                value={form.paidAmount ?? 0}
                onChange={(event) => setField('paidAmount', Number(event.target.value))}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
              />
            </label>
            {form.direction === 'Income' && (
              <div className="md:col-span-2 rounded-xl border border-blue-100 bg-blue-50/60 p-4">
                <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-blue-900">
                  <Link2 className="h-4 w-4" />
                  Сверка с выставленным счётом
                </div>
                <div className="grid grid-cols-1 gap-3 md:grid-cols-[180px_1fr_180px]">
                  <label className="flex flex-col gap-1 text-sm text-slate-600">
                    ID счёта
                    <input
                      type="number"
                      min={1}
                      value={form.linkedInvoiceId ?? ''}
                      onChange={(event) =>
                        setField('linkedInvoiceId', event.target.value ? Number(event.target.value) : null)
                      }
                      disabled={appliedInvoiceLocked}
                      className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                      placeholder="Например 33"
                    />
                  </label>
                  <label className="flex flex-col gap-1 text-sm text-slate-600">
                    Комментарий сверки
                    <input
                      value={form.matchComment ?? ''}
                      onChange={(event) => setField('matchComment', event.target.value)}
                      className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                      placeholder="Что уточнить или почему привязали"
                    />
                  </label>
                  <label className="flex flex-col gap-1 text-sm text-slate-600">
                    Статус
                    <SystemSelect
                      value={form.linkedInvoiceId ? 'Matched' : form.matchStatus ?? 'None'}
                      onChange={(event) => {
                        const value = event.target.value as MoneyOperation['matchStatus'];
                        setField('matchStatus', value);
                        if (value !== 'Matched') {
                          setField('linkedInvoiceId', null);
                        }
                      }}
                      disabled={appliedInvoiceLocked}
                      className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none">
                      <option value="None">Не сверено</option>
                      <option value="ClarificationRequired">Уточнить</option>
                      <option value="Matched" disabled={!form.linkedInvoiceId}>
                        Сверено
                      </option>
                      <option value="Ignored">Не учитывать</option>
                    </SystemSelect>
                  </label>
                </div>
                {form.linkedInvoiceNumber && (
                  <p className="mt-2 text-xs text-blue-700">
                    Сейчас привязано: счёт {form.linkedInvoiceNumber}
                    {form.linkedInvoiceDate ? ` от ${formatDate(form.linkedInvoiceDate)}` : ''}
                  </p>
                )}
                {appliedInvoiceLocked && (
                  <p className="mt-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
                    Операция уже зачтена в счёте. Чтобы изменить сумму или связь со счётом, нужна отдельная операция сторно.
                  </p>
                )}
                <div className="mt-4 space-y-2">
                  <div className="flex items-center justify-between gap-3">
                    <p className="text-xs font-semibold uppercase tracking-wide text-blue-900/70">
                      Подходящие счета
                    </p>
                    {invoiceCandidatesLoading && <span className="text-xs text-blue-700">Ищем кандидатов...</span>}
                  </div>
                  {!invoiceCandidatesLoading && invoiceCandidates.length === 0 && (
                    <p className="rounded-lg border border-dashed border-blue-200 bg-white/70 px-3 py-2 text-xs text-blue-700">
                      Подходящих счетов не найдено. Можно указать ID вручную.
                    </p>
                  )}
                  {invoiceCandidates.map((candidate) => {
                    const isSelected = form.linkedInvoiceId === candidate.invoiceId;
                    return (
                      <button
                        type="button"
                        key={`${candidate.invoiceId}-${candidate.paymentId}`}
                        onClick={() => handleSelectInvoiceCandidate(candidate)}
                        disabled={appliedInvoiceLocked}
                        className={`w-full rounded-lg border bg-white px-3 py-2 text-left transition hover:border-blue-400 ${
                          isSelected ? 'border-blue-500 ring-2 ring-blue-100' : 'border-blue-100'
                        } disabled:cursor-not-allowed disabled:opacity-60`}>
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <span className="text-sm font-semibold text-slate-900">
                            Счёт {candidate.invoiceNumber}
                            {candidate.invoiceDate ? ` от ${formatDate(candidate.invoiceDate)}` : ''}
                          </span>
                          <span className="text-sm font-semibold text-blue-700">
                            Остаток {formatMoney(candidate.outstandingAmount)} ₽
                          </span>
                        </div>
                        <div className="mt-1 flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500">
                          <span>{candidate.clientName || candidate.counterpartyName || 'Контрагент не указан'}</span>
                          {candidate.matchReason && <span>{candidate.matchReason}</span>}
                        </div>
                      </button>
                    );
                  })}
                </div>
              </div>
            )}
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Проект
              <input
                value={form.projectName ?? ''}
                onChange={(event) => setField('projectName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Без проекта"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Направление
              <input
                value={form.directionName ?? ''}
                onChange={(event) => setField('directionName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Без направления"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Точка продаж
              <input
                value={form.salesPointName ?? ''}
                onChange={(event) => setField('salesPointName', event.target.value)}
                className="rounded-lg border border-slate-200 px-3 py-2 focus:border-blue-500 focus:outline-none"
                placeholder="Без точки продаж"
              />
            </label>
          </div>
        </div>

        <div className="flex items-center justify-between border-t border-slate-200 px-6 py-4">
          {error ? <div className="text-sm text-red-500">{error}</div> : <span />}
          <button
            type="button"
            onClick={handleSubmit}
            disabled={isSaving}
            className="rounded-lg bg-blue-600 px-5 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-60">
            {isSaving ? 'Сохранение...' : 'Сохранить'}
          </button>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:border-slate-300">
            Отменить
          </button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

export function MoneyOperations({
  source,
  onOpenInvoice,
}: {
  source: MoneyTab;
  onOpenInvoice?: (invoiceId: number, type?: 'Income' | 'Expense') => void;
}) {
  const { user } = useAuth();
  const profileTimeZoneId = useUserTimeZone();
  const permissions = useRolePermissions(user?.role?.id);
  const canExportJournal = permissions.payments.canExport;
  const canCreateMoneyOperations = permissions.payments.canCreate;
  const canEditMoneyOperations = permissions.payments.canEdit;
  const canDeleteMoneyOperations = permissions.payments.canDelete;
  const canViewFullMoneyBalance = permissions.payments.viewScope === 'all';
  const canOpenLinkedAccounts = permissions.accounts?.canView === true;
  const canOpenLinkedClients = permissions.clients?.canView === true;
  const canViewAudit = permissions.audit?.canView === true;
  const openLinkedInvoice = canOpenLinkedAccounts ? onOpenInvoice : undefined;
  const { setActiveTab } = useTabNavigation();
  const toast = useToast();
  const { confirm } = useDialog();
  const [showFilters, setShowFilters] = useState(false);
  const [showAdvancedSearch, setShowAdvancedSearch] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [search, setSearch] = useState('');
  const [manualSearchTerm, setManualSearchTerm] = useState<string | null>(null);
  const [directionFilter, setDirectionFilter] = useState<'all' | MoneyOperationDirection>('all');
  const [accountingTypeFilter, setAccountingTypeFilter] = useState<'all' | MoneyOperationAccountingType>('all');
  const [totalSourceFilter, setTotalSourceFilter] = useState<'all' | MoneyOperationSource>('all');
  const [moneyAccountId, setMoneyAccountId] = useState<number | ''>('');
  const [moneyAccounts, setMoneyAccounts] = useState<MoneyAccount[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [accountName, setAccountName] = useState('');
  const [categoryName, setCategoryName] = useState('');
  const [categoryDictionaryOptions, setCategoryDictionaryOptions] = useState<MoneyOperationCategoryOption[]>([]);
  const [accountSummaries, setAccountSummaries] = useState<MoneyOperationAccountSummary[]>([]);
  const [dailyBalances, setDailyBalances] = useState<MoneyOperationDailyBalance[]>([]);
  const [balanceReconciliationResults, setBalanceReconciliationResults] = useState<MoneyBalanceReconciliationResult[]>([]);
  const [balanceReconciliationRunning, setBalanceReconciliationRunning] = useState(false);
  const [modalOpen, setModalOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [editingOperation, setEditingOperation] = useState<MoneyOperation | null>(null);
  const [totalTab, setTotalTab] = useState<'total' | 'cashflow'>('total');
  const [confirmDelete, setConfirmDelete] = useState<MoneyOperation | null>(null);
  const [pendingUnallocatedAdvanceOperation, setPendingUnallocatedAdvanceOperation] = useState<MoneyOperation | null>(null);
  const [selectedOperationIds, setSelectedOperationIds] = useState<number[]>([]);
  const [confirmBulkDeleteOpen, setConfirmBulkDeleteOpen] = useState(false);
  const [page, setPage] = useState(1);
  const [cashOrdersRefreshKey, setCashOrdersRefreshKey] = useState(0);
  const [cashOrderModalOpen, setCashOrderModalOpen] = useState(false);
  const [selectedCashOrder, setSelectedCashOrder] = useState<CashOrder | null>(null);
  const [cashOrderSaving, setCashOrderSaving] = useState(false);
  const [bankStatementImporting, setBankStatementImporting] = useState(false);
  const [moneyOperationsExporting, setMoneyOperationsExporting] = useState(false);
  const [moneyOperationsExcelExporting, setMoneyOperationsExcelExporting] = useState(false);
  const [moneyOperationsPdfExporting, setMoneyOperationsPdfExporting] = useState(false);
  const [moneyOperationsPrinting, setMoneyOperationsPrinting] = useState(false);
  const [journalActionsOpen, setJournalActionsOpen] = useState(false);
  const [bankStatementImportResult, setBankStatementImportResult] = useState<{
    batchId?: number | null;
    fileName?: string | null;
    rowsRead: number;
    created: number;
    skipped: number;
    errors: string[];
    operations: BankStatementImportCreatedOperation[];
  } | null>(null);
  const [showBankImportsJournal, setShowBankImportsJournal] = useState(false);
  const [bankImportsRefreshKey, setBankImportsRefreshKey] = useState(0);
  const [bankImportBatches, setBankImportBatches] = useState<BankStatementImportBatch[]>([]);
  const [bankImportBatchesLoading, setBankImportBatchesLoading] = useState(false);
  const [selectedBankImport, setSelectedBankImport] = useState<BankStatementImportBatchDetail | null>(null);
  const [bankImportDetailsLoading, setBankImportDetailsLoading] = useState(false);
  const [bankImportRollbackLoading, setBankImportRollbackLoading] = useState(false);
  const [bankImportReprocessLoading, setBankImportReprocessLoading] = useState(false);
  const [bankAutoImportSettings, setBankAutoImportSettings] = useState<BankStatementAutoImportSettings>(
    DEFAULT_BANK_STATEMENT_AUTO_IMPORT_SETTINGS,
  );
  const [bankAutoImportLoading, setBankAutoImportLoading] = useState(false);
  const [bankAutoImportSaving, setBankAutoImportSaving] = useState(false);
  const [bankAutoImportRunning, setBankAutoImportRunning] = useState(false);
  const [bankAutoImportRunResult, setBankAutoImportRunResult] = useState<BankStatementAutoImportRunResult | null>(null);
  const [showBankCategoryRules, setShowBankCategoryRules] = useState(false);
  const [bankCategoryRulesSettings, setBankCategoryRulesSettings] = useState<BankStatementCategoryRulesSettings>(
    DEFAULT_BANK_STATEMENT_CATEGORY_RULES_SETTINGS,
  );
  const [bankCategoryRulesLoading, setBankCategoryRulesLoading] = useState(false);
  const [bankCategoryRulesSaving, setBankCategoryRulesSaving] = useState(false);
  const [cashLimitSettings, setCashLimitSettings] = useState<CashLimitSettings>(DEFAULT_CASH_LIMIT_SETTINGS);
  const [cashLimitLoading, setCashLimitLoading] = useState(false);
  const [cashLimitSaving, setCashLimitSaving] = useState(false);
  const [cashInventories, setCashInventories] = useState<CashInventory[]>([]);
  const [cashInventoryTotal, setCashInventoryTotal] = useState(0);
  const [cashInventoryLoading, setCashInventoryLoading] = useState(false);
  const [cashInventorySaving, setCashInventorySaving] = useState(false);
  const [cashInventoryActualBalance, setCashInventoryActualBalance] = useState('');
  const [cashInventoryComment, setCashInventoryComment] = useState('');
  const [cashInventoryCreateCorrection, setCashInventoryCreateCorrection] = useState(true);
  const [advanceReviewMonitor, setAdvanceReviewMonitor] = useState({
    count: 0,
    amount: 0,
    loading: false,
    error: null as string | null,
  });
  const bankStatementInputRef = useRef<HTMLInputElement | null>(null);
  const journalActionsRef = useRef<HTMLDivElement | null>(null);

  const currentYear = new Date().getFullYear();
  const [from, setFrom] = useState(`${currentYear}-01-01`);
  const [to, setTo] = useState(`${currentYear}-12-31`);

  const handleCurrentYear = () => {
    const year = new Date().getFullYear();
    setFrom(`${year}-01-01`);
    setTo(`${year}-12-31`);
  };

  const debouncedSearch = useDebouncedValue(search, 300);
  const effectiveSearch = manualSearchTerm === search ? manualSearchTerm : debouncedSearch;
  const applySearch = () => {
    setManualSearchTerm(search);
  };
  const handleSearchKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      applySearch();
    }
  };
  const {
    columnOrder,
    orderedVisibleColumns,
    visibleColumns,
    pageSize,
    setPageSize,
    draggingColumn,
    toggleColumn,
    moveColumn,
    handleColumnDragStart,
    handleColumnDragOver,
    handleColumnDrop,
    handleColumnDragEnd,
    handleResizeStart,
    getColumnWidth,
    getTableWidth,
    resetTableSettings,
  } = usePersistedTableSettings<MoneyOperationColumnKey>({
    storageKey: `pp.money_operations_table_settings.${user?.id ?? 'guest'}.v2`,
    settingsKey: 'money_operations_table_settings_v2',
    columns: MONEY_OPERATION_COLUMNS.map((column) => ({
      key: column.key,
      defaultVisible: column.defaultVisible,
      defaultWidth: column.defaultWidth,
    })),
    defaultPageSize: 25,
    minPageSize: 10,
    maxPageSize: 100,
    minColumnWidth: 110,
  });

  const apiSource = source === 'Total' ? (totalSourceFilter === 'all' ? undefined : totalSourceFilter) : source;
  const effectivePageSize = pageSize;
  const { items, total, summary, loading, error, reload } = useMoneyOperations({
    from,
    to,
    source: apiSource,
    direction: directionFilter === 'all' ? undefined : directionFilter,
    accountingType: accountingTypeFilter === 'all' ? undefined : accountingTypeFilter,
    moneyAccountId: moneyAccountId === '' ? undefined : moneyAccountId,
    accountName: accountName || undefined,
    categoryName: categoryName.trim() || undefined,
    search: effectiveSearch,
    page,
    pageSize: effectivePageSize,
  });
  const categoryOptions = useMemo(
    () => buildUniqueOptions([...categoryDictionaryOptions.map((item) => item.name), ...items.map((item) => item.categoryName)]),
    [categoryDictionaryOptions, items],
  );
  const totalPages = useMemo(
    () => (effectivePageSize > 0 ? Math.max(1, Math.ceil(total / effectivePageSize)) : 1),
    [effectivePageSize, total],
  );
  useEffect(() => {
    if (!permissions.payments.canView) {
      setAdvanceReviewMonitor({ count: 0, amount: 0, loading: false, error: null });
      return;
    }

    let cancelled = false;
    setAdvanceReviewMonitor((current) => ({ ...current, loading: true, error: null }));

    apiService.getPaymentsV2({
      advanceReviewStatus: 'RequiresDecision',
      page: 1,
      pageSize: 1,
    })
      .then((response) => {
        if (cancelled) {
          return;
        }
        setAdvanceReviewMonitor({
          count: response.total,
          amount: response.summary?.income ?? response.summary?.total ?? 0,
          loading: false,
          error: null,
        });
      })
      .catch((error) => {
        if (cancelled) {
          return;
        }
        setAdvanceReviewMonitor({
          count: 0,
          amount: 0,
          loading: false,
          error: extractDisplayErrorMessage(error, 'Не удалось загрузить контроль авансов.'),
        });
      });

    return () => {
      cancelled = true;
    };
  }, [permissions.payments.canView]);

  const openAdvanceReviewPayments = () => {
    requestPaymentAdvanceReviewFilter('RequiresDecision');
    setActiveTab('payments');
  };

  const moneyColumnsByKey = useMemo(
    () =>
      Object.fromEntries(MONEY_OPERATION_COLUMNS.map((column) => [column.key, column])) as Record<
        MoneyOperationColumnKey,
        (typeof MONEY_OPERATION_COLUMNS)[number]
      >,
    [],
  );
  const visibleMoneyColumns = useMemo(
    () => orderedVisibleColumns.map((key) => moneyColumnsByKey[key]).filter(Boolean),
    [moneyColumnsByKey, orderedVisibleColumns],
  );
  const selectionEnabled = source === 'Bank';
  const selectedOperationIdSet = useMemo(() => new Set(selectedOperationIds), [selectedOperationIds]);
  const selectableOperationIds = useMemo(
    () =>
      selectionEnabled && canDeleteMoneyOperations
        ? items.filter((item) => !item.isReadonly && item.id > 0 && !isAppliedToInvoice(item)).map((item) => item.id)
        : [],
    [canDeleteMoneyOperations, items, selectionEnabled],
  );
  const selectedOperationsCount = selectedOperationIds.length;
  const moneyTableColumnCount = visibleMoneyColumns.length + 1 + (selectionEnabled ? 1 : 0);
  const allVisibleOperationsSelected =
    selectableOperationIds.length > 0 && selectableOperationIds.every((id) => selectedOperationIdSet.has(id));
  const someVisibleOperationsSelected =
    selectableOperationIds.some((id) => selectedOperationIdSet.has(id)) && !allVisibleOperationsSelected;
  useEffect(() => {
    if (!selectionEnabled || !canDeleteMoneyOperations) {
      setSelectedOperationIds([]);
      return;
    }

    const availableIds = new Set(selectableOperationIds);
    setSelectedOperationIds((current) => current.filter((id) => availableIds.has(id)));
  }, [canDeleteMoneyOperations, selectableOperationIds, selectionEnabled]);
  const selectedAccountSummary = useMemo(
    () => accountSummaries.find((account) => account.accountName === accountName) ?? null,
    [accountName, accountSummaries],
  );
  const visibleDailyBalances = useMemo(
    () => (accountName ? dailyBalances.filter((item) => item.accountName === accountName) : dailyBalances),
    [accountName, dailyBalances],
  );
  const cashLimitExceedances = useMemo(() => {
    if (source !== 'Cash' || !cashLimitSettings.enabled || !cashLimitSettings.amount) {
      return [];
    }

    return visibleDailyBalances
      .filter((item) => item.closingBalance > cashLimitSettings.amount!)
      .map((item) => ({
        ...item,
        excess: item.closingBalance - cashLimitSettings.amount!,
      }));
  }, [cashLimitSettings.amount, cashLimitSettings.enabled, source, visibleDailyBalances]);
  const cashInventoryAccountingBalance = useMemo(() => {
    if (source !== 'Cash') {
      return 0;
    }

    if (selectedAccountSummary) {
      return selectedAccountSummary.endBalance;
    }

    return accountSummaries
      .filter((item) => item.source === 'Cash')
      .reduce((sum, item) => sum + item.endBalance, 0);
  }, [accountSummaries, selectedAccountSummary, source]);
  const visibleBalanceReconciliationResults = useMemo(
    () => (accountName ? balanceReconciliationResults.filter((item) => item.accountName === accountName) : balanceReconciliationResults),
    [accountName, balanceReconciliationResults],
  );
  const balanceReconciliationByKey = useMemo(
    () =>
      new Map(
        visibleBalanceReconciliationResults.map((item) => [
          `${item.source}-${item.accountName}-${toDateInputValue(item.date)}`,
          item,
        ]),
      ),
    [visibleBalanceReconciliationResults],
  );
  const latestBalanceReconciliation = useMemo(
    () =>
      visibleBalanceReconciliationResults
        .slice()
        .sort((left, right) => new Date(right.checkedAt).getTime() - new Date(left.checkedAt).getTime())[0] ?? null,
    [visibleBalanceReconciliationResults],
  );
  const reconciliationNotifications = useMemo<ReconciliationNotification[]>(() => {
    if (source === 'Total') {
      return [];
    }

    const notifications: ReconciliationNotification[] = [];
    const staleRows = visibleDailyBalances
      .map((item) => {
        const saved = balanceReconciliationByKey.get(`${item.source}-${item.accountName}-${toDateInputValue(item.date)}`);
        const drift = getSavedBalanceDrift(item, saved);
        return saved && drift && !drift.isActual ? { item, drift } : null;
      })
      .filter((item): item is { item: MoneyOperationDailyBalance; drift: { amountDifference: number; isActual: boolean } } =>
        Boolean(item),
      );

    if (staleRows.length > 0) {
      const totalDifference = staleRows.reduce((sum, row) => sum + row.drift.amountDifference, 0);
      notifications.push({
        key: 'amount',
        title: 'Сумма изменилась',
        description: `${staleRows.length} дн. требуют повторной сверки. Разница: ${formatMoney(totalDifference)} ₽.`,
        tone: 'rose',
      });
    }

    const operationsWithUnknownAccount = items.filter(
      (item) => item.source === source && !item.accountName?.trim() && !item.moneyAccountName?.trim(),
    );
    if (operationsWithUnknownAccount.length > 0) {
      notifications.push({
        key: 'unknown-account',
        title: 'Неизвестный счет',
        description: `${operationsWithUnknownAccount.length} опер. без счета в текущей выборке.`,
        tone: 'amber',
      });
    }

    const operationsWithoutPurpose = items.filter(
      (item) => item.source === source && item.source === 'Bank' && !item.purpose?.trim(),
    );
    if (operationsWithoutPurpose.length > 0) {
      notifications.push({
        key: 'purpose',
        title: 'Пустое назначение платежа',
        description: `${operationsWithoutPurpose.length} банк. опер. требуют назначения платежа.`,
        tone: 'amber',
      });
    }

    const duplicateGroups = new Map<string, MoneyOperation[]>();
    items
      .filter((item) => item.source === source && item.id > 0)
      .forEach((item) => {
        const duplicateKey = [
          item.source,
          item.direction,
          toDateInputValue(item.date),
          roundMoney(item.amount),
          item.paymentOrderNumber?.trim() || item.counterpartyName?.trim() || '',
          item.purpose?.trim() || '',
        ].join('|');
        duplicateGroups.set(duplicateKey, [...(duplicateGroups.get(duplicateKey) ?? []), item]);
      });
    const duplicateCount = Array.from(duplicateGroups.values()).filter((group) => group.length > 1).length;
    if (duplicateCount > 0) {
      notifications.push({
        key: 'duplicate',
        title: 'Дубликаты операций',
        description: `${duplicateCount} похож. групп в текущей выборке.`,
        tone: 'amber',
      });
    }

    return notifications;
  }, [balanceReconciliationByKey, items, source, visibleDailyBalances]);
  const selectedMoneyAccount = useMemo(
    () => (moneyAccountId === '' ? null : moneyAccounts.find((account) => account.id === moneyAccountId) ?? null),
    [moneyAccountId, moneyAccounts],
  );
  const selectMoneyAccount = (value: string) => {
    const account = moneyAccounts.find((item) => String(item.id) === value);
    setMoneyAccountId(account?.id ?? '');
    setAccountName(account?.name ?? value);
  };
  const moneyAccountOptions = useMemo(
    () =>
      moneyAccounts.length > 0
        ? moneyAccounts.map((account) => ({
            id: account.id as number | null,
            source: account.source,
            accountName: account.name,
          }))
        : accountSummaries.map((account) => ({
            id: null,
            source: account.source,
            accountName: account.accountName,
          })),
    [accountSummaries, moneyAccounts],
  );
  const visibleMoneyAccountOptions = useMemo(
    () =>
      source === 'Total'
        ? moneyAccountOptions
        : moneyAccountOptions.filter((account) => account.source === source),
    [moneyAccountOptions, source],
  );
  const activeMoneyFilters = useMemo(() => {
    const filters: Array<{ key: string; label: string; value: string; onClear: () => void }> = [];

    if (accountName) {
      filters.push({
        key: 'accountName',
        label: 'Счет',
        value: accountName,
        onClear: () => {
          setMoneyAccountId('');
          setAccountName('');
        },
      });
    }
    if (directionFilter !== 'all') {
      filters.push({
        key: 'direction',
        label: 'Движение',
        value: MONEY_DIRECTION_FILTER_LABELS[directionFilter],
        onClear: () => setDirectionFilter('all'),
      });
    }
    if (accountingTypeFilter !== 'all') {
      filters.push({
        key: 'accountingType',
        label: 'Учет',
        value: MONEY_ACCOUNTING_FILTER_LABELS[accountingTypeFilter],
        onClear: () => setAccountingTypeFilter('all'),
      });
    }
    if (categoryName.trim()) {
      filters.push({
        key: 'categoryName',
        label: 'Статья',
        value: categoryName.trim(),
        onClear: () => setCategoryName(''),
      });
    }
    if (source === 'Total' && totalSourceFilter !== 'all') {
      filters.push({
        key: 'source',
        label: 'Источник',
        value: SOURCE_LABELS[totalSourceFilter],
        onClear: () => setTotalSourceFilter('all'),
      });
    }

    return filters;
  }, [accountName, accountingTypeFilter, categoryName, directionFilter, source, totalSourceFilter]);

  useEffect(() => {
    if (!modalOpen || responsibles.length > 0) {
      return;
    }

    let cancelled = false;
    apiService
      .getActResponsibles()
      .then((items) => {
        if (!cancelled) {
          setResponsibles(items ?? []);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setResponsibles([]);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [modalOpen, responsibles.length]);

  useEffect(() => {
    if (source === 'Total') {
      setMoneyAccountId('');
      setAccountName('');
      setMoneyAccounts([]);
      setAccountSummaries([]);
      setDailyBalances([]);
      setBalanceReconciliationResults([]);
      return;
    }

    let cancelled = false;
    setMoneyAccountId('');
    setAccountName('');

    Promise.all([
      apiService.getMoneyOperationAccountSummaries({ from, to, source }),
      apiService.getMoneyOperationDailyBalances({ from, to, source }),
      canViewFullMoneyBalance
        ? apiService.getMoneyBalanceReconciliationResults({ from, to, source }).catch(() => [])
        : Promise.resolve([]),
      apiService.getMoneyAccounts({ activeOnly: true }),
    ])
      .then(([accounts, balances, reconciliationResults, moneyAccountItems]) => {
        if (!cancelled) {
          setAccountSummaries(accounts);
          setDailyBalances(balances);
          setBalanceReconciliationResults(reconciliationResults);
          setMoneyAccounts(moneyAccountItems);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setAccountSummaries([]);
          setDailyBalances([]);
          setBalanceReconciliationResults([]);
          setMoneyAccounts([]);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [canViewFullMoneyBalance, from, source, to]);

  useEffect(() => {
    let cancelled = false;

    Promise.all([apiService.getIncomeTypes('Income', true), apiService.getIncomeTypes('Expense', true)])
      .then(([incomeTypes, expenseTypes]) => {
        if (!cancelled) {
          const uniqueOptions = new Map<string, MoneyOperationCategoryOption>();
          [...incomeTypes, ...expenseTypes].forEach((item) => {
            uniqueOptions.set(`${item.paymentType}-${item.id}`, {
              id: item.id,
              name: item.name,
              paymentType: item.paymentType,
            });
          });
          setCategoryDictionaryOptions(
            Array.from(uniqueOptions.values()).sort((a, b) => a.name.localeCompare(b.name, 'ru')),
          );
        }
      })
      .catch(() => {
        if (!cancelled) {
          setCategoryDictionaryOptions([]);
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (source !== 'Bank') {
      setShowBankImportsJournal(false);
      setBankImportBatches([]);
      setSelectedBankImport(null);
      return;
    }
    if (!showBankImportsJournal) {
      return;
    }

    let cancelled = false;
    setBankImportBatchesLoading(true);

    apiService
      .getBankStatementImports({ page: 1, pageSize: 5 })
      .then((response) => {
        if (!cancelled) {
          setBankImportBatches(response.items ?? []);
        }
      })
      .catch((loadError) => {
        if (!cancelled) {
          setBankImportBatches([]);
          const message = extractDisplayErrorMessage(loadError, 'Не удалось загрузить журнал импортов.');
          toast.error(message, 'Выписка банка');
        }
      })
      .finally(() => {
        if (!cancelled) {
          setBankImportBatchesLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [bankImportsRefreshKey, showBankImportsJournal, source, toast]);

  useEffect(() => {
    if (source !== 'Bank') {
      setBankAutoImportSettings(DEFAULT_BANK_STATEMENT_AUTO_IMPORT_SETTINGS);
      return;
    }

    let cancelled = false;
    setBankAutoImportLoading(true);

    apiService
      .getUserSetting(BANK_STATEMENT_AUTO_IMPORT_SETTING_KEY)
      .then((setting) => {
        if (!cancelled) {
          setBankAutoImportSettings(parseBankStatementAutoImportSettings(setting?.value ?? ''));
        }
      })
      .catch(() => {
        if (!cancelled) {
          setBankAutoImportSettings(DEFAULT_BANK_STATEMENT_AUTO_IMPORT_SETTINGS);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setBankAutoImportLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [source]);

  useEffect(() => {
    if (source !== 'Bank') {
      setShowBankCategoryRules(false);
      setBankCategoryRulesSettings(DEFAULT_BANK_STATEMENT_CATEGORY_RULES_SETTINGS);
      return;
    }

    let cancelled = false;
    setBankCategoryRulesLoading(true);

    apiService
      .getUserSetting(BANK_STATEMENT_CATEGORY_RULES_SETTING_KEY)
      .then((setting) => {
        if (!cancelled) {
          setBankCategoryRulesSettings(parseBankStatementCategoryRulesSettings(setting?.value ?? ''));
        }
      })
      .catch(() => {
        if (!cancelled) {
          setBankCategoryRulesSettings(DEFAULT_BANK_STATEMENT_CATEGORY_RULES_SETTINGS);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setBankCategoryRulesLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [source]);

  useEffect(() => {
    if (source !== 'Cash') {
      setCashLimitSettings(DEFAULT_CASH_LIMIT_SETTINGS);
      return;
    }

    let cancelled = false;
    setCashLimitLoading(true);

    apiService
      .getUserSetting(CASH_LIMIT_SETTING_KEY)
      .then((setting) => {
        if (!cancelled) {
          setCashLimitSettings(parseCashLimitSettings(setting?.value ?? ''));
        }
      })
      .catch(() => {
        if (!cancelled) {
          setCashLimitSettings(DEFAULT_CASH_LIMIT_SETTINGS);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setCashLimitLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [source]);

  useEffect(() => {
    if (source !== 'Cash') {
      setCashInventories([]);
      setCashInventoryTotal(0);
      return;
    }

    let cancelled = false;
    setCashInventoryLoading(true);

    apiService
      .getCashInventories({
        from,
        to,
        accountName: accountName || undefined,
        page: 1,
        pageSize: 8,
      })
      .then((response) => {
        if (!cancelled) {
          setCashInventories(response.items);
          setCashInventoryTotal(response.total);
        }
      })
      .catch((error) => {
        if (!cancelled) {
          setCashInventories([]);
          setCashInventoryTotal(0);
          const message = extractDisplayErrorMessage(error, 'Не удалось загрузить историю инвентаризаций кассы.');
          toast.error(message, 'Касса');
        }
      })
      .finally(() => {
        if (!cancelled) {
          setCashInventoryLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [accountName, cashOrdersRefreshKey, from, source, to, toast]);

  const saveBankAutoImportSettings = async (nextSettings = bankAutoImportSettings) => {
    setBankAutoImportSaving(true);
    try {
      const normalizedSettings = parseBankStatementAutoImportSettings(
        serializeBankStatementAutoImportSettings(nextSettings),
      );
      const setting = await apiService.upsertUserSetting(
        BANK_STATEMENT_AUTO_IMPORT_SETTING_KEY,
        serializeBankStatementAutoImportSettings(normalizedSettings),
      );
      setBankAutoImportSettings(parseBankStatementAutoImportSettings(setting.value));
      toast.success('Настройки автоподгрузки выписки сохранены.', 'Выписка банка');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось сохранить настройки автоподгрузки выписки.');
      toast.error(message, 'Выписка банка');
    } finally {
      setBankAutoImportSaving(false);
    }
  };

  const updateBankAutoImportSettings = (patch: Partial<BankStatementAutoImportSettings>) => {
    setBankAutoImportSettings((current) => ({ ...current, ...patch }));
  };

  const saveBankCategoryRulesSettings = async (nextSettings = bankCategoryRulesSettings) => {
    setBankCategoryRulesSaving(true);
    try {
      const normalizedSettings = parseBankStatementCategoryRulesSettings(
        serializeBankStatementCategoryRulesSettings(nextSettings),
      );
      const setting = await apiService.upsertUserSetting(
        BANK_STATEMENT_CATEGORY_RULES_SETTING_KEY,
        serializeBankStatementCategoryRulesSettings(normalizedSettings),
      );
      setBankCategoryRulesSettings(parseBankStatementCategoryRulesSettings(setting.value));
      toast.success('Правила распознавания выписки сохранены.', 'Выписка банка');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось сохранить правила распознавания выписки.');
      toast.error(message, 'Выписка банка');
    } finally {
      setBankCategoryRulesSaving(false);
    }
  };

  const addBankCategoryRule = () => {
    setBankCategoryRulesSettings((current) => ({
      rules: [...current.rules, createEmptyBankStatementCategoryRule()],
    }));
    setShowBankCategoryRules(true);
  };

  const updateBankCategoryRule = (id: string, patch: Partial<BankStatementCategoryRule>) => {
    setBankCategoryRulesSettings((current) => ({
      rules: current.rules.map((rule) =>
        rule.id === id
          ? {
              ...rule,
              ...patch,
              incomeTypeId: patch.paymentType && patch.paymentType !== rule.paymentType ? null : patch.incomeTypeId ?? rule.incomeTypeId,
            }
          : rule,
      ),
    }));
  };

  const removeBankCategoryRule = (id: string) => {
    setBankCategoryRulesSettings((current) => ({
      rules: current.rules.filter((rule) => rule.id !== id),
    }));
  };

  const updateCashLimitSettings = (patch: Partial<CashLimitSettings>) => {
    setCashLimitSettings((current) => ({ ...current, ...patch }));
  };

  const saveCashLimitSettings = async (nextSettings = cashLimitSettings) => {
    setCashLimitSaving(true);
    try {
      const normalizedSettings = parseCashLimitSettings(serializeCashLimitSettings(nextSettings));
      const setting = await apiService.upsertUserSetting(
        CASH_LIMIT_SETTING_KEY,
        serializeCashLimitSettings(normalizedSettings),
      );
      setCashLimitSettings(parseCashLimitSettings(setting.value));
      toast.success('Лимит кассы сохранён.', 'Касса');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось сохранить лимит кассы.');
      toast.error(message, 'Касса');
    } finally {
      setCashLimitSaving(false);
    }
  };

  const handleCreateCashInventory = async () => {
    if (!canEditMoneyOperations) {
      toast.error('У вас нет прав на изменение операций кассы.', 'Касса');
      return;
    }

    const actualBalance = Number(cashInventoryActualBalance);
    if (cashInventoryActualBalance.trim() === '' || !Number.isFinite(actualBalance) || actualBalance < 0) {
      toast.error('Укажите фактический остаток кассы.', 'Касса');
      return;
    }

    const inventoryAccountName = accountName || selectedMoneyAccount?.name || '';
    if (!inventoryAccountName.trim()) {
      toast.error('Выберите конкретную кассу перед инвентаризацией.', 'Касса');
      return;
    }

    setCashInventorySaving(true);
    try {
      await apiService.createCashInventory({
        date: to,
        accountName: inventoryAccountName,
        actualBalance,
        comment: cashInventoryComment.trim() || null,
        createCorrection: cashInventoryCreateCorrection,
      });
      setCashInventoryActualBalance('');
      setCashInventoryComment('');
      setCashOrdersRefreshKey((value) => value + 1);
      await reload();
      toast.success('Инвентаризация кассы сохранена.', 'Касса');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось сохранить инвентаризацию кассы.');
      toast.error(message, 'Касса');
    } finally {
      setCashInventorySaving(false);
    }
  };

  const handleRunBankAutoImport = async () => {
    if (!canCreateMoneyOperations) {
      toast.error('У вас нет прав на создание операций денег.', 'Выписка банка');
      return;
    }

    setBankAutoImportRunning(true);
    try {
      const result = await apiService.runBankStatementAutoImport();
      setBankAutoImportRunResult(result);
      setBankAutoImportSettings((current) => ({
        ...current,
        lastCheckedAt: result.checkedAt,
      }));
      setBankImportsRefreshKey((value) => value + 1);
      if (result.created > 0) {
        await reload();
      }
      toast.success(
        `Проверено файлов: ${result.filesFound}. Создано операций: ${result.created}. Пропущено: ${result.skipped}.`,
        'Выписка банка',
      );
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось запустить автоподгрузку выписок.');
      toast.error(message, 'Выписка банка');
    } finally {
      setBankAutoImportRunning(false);
    }
  };

  const handleRunBalanceReconciliation = async () => {
    if (source === 'Total') {
      return;
    }

    if (!canEditMoneyOperations || !canViewFullMoneyBalance) {
      toast.error('Для сохранения общей сверки нужны право изменения и полный доступ к платежам.', 'Сверка остатков');
      return;
    }

    setBalanceReconciliationRunning(true);
    try {
      const result = await apiService.runMoneyBalanceReconciliation({
        from,
        to,
        source,
        accountName: accountName || undefined,
      });
      setBalanceReconciliationResults(result.items);
      toast.success(
        `Сверка сохранена: создано ${result.created}, обновлено ${result.updated}, замечаний ${result.issueCount}.`,
        'Сверка остатков',
      );
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось запустить сверку остатков.');
      toast.error(message, 'Сверка остатков');
    } finally {
      setBalanceReconciliationRunning(false);
    }
  };

  const totalRows = useMemo(() => {
    const bySource = new Map(summary.bySource.map((row) => [row.source, row]));

    const rows = ([
      { key: 'Bank', label: 'Банк' },
      { key: 'Cash', label: 'Касса' },
      { key: 'Card', label: 'Карты' },
      { key: 'EMoney', label: 'Электронные деньги' },
    ] as const).map((row) => {
      const data = bySource.get(row.key) ?? {
        startBalance: 0,
        income: 0,
        expense: 0,
        endBalance: 0,
      };
      return {
        label: row.label,
        start: data.startBalance,
        income: data.income,
        expense: data.expense,
        end: data.endBalance,
      };
    });

    const total = rows.reduce(
      (acc, row) => ({
        start: acc.start + row.start,
        income: acc.income + row.income,
        expense: acc.expense + row.expense,
        end: acc.end + row.end,
      }),
      { start: 0, income: 0, expense: 0, end: 0 },
    );

    return { rows, total };
  }, [summary.bySource]);

  const journalActionBusy =
    moneyOperationsExporting
    || moneyOperationsPdfExporting
    || moneyOperationsExcelExporting
    || moneyOperationsPrinting;

  useEffect(() => {
    if (!journalActionsOpen) {
      return;
    }

    const handleClickOutside = (event: MouseEvent) => {
      const target = event.target as Node | null;
      if (journalActionsRef.current && target && !journalActionsRef.current.contains(target)) {
        setJournalActionsOpen(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
    };
  }, [journalActionsOpen]);

  const handleExportCsv = async () => {
    if (!canExportJournal) {
      toast.error('У роли нет права на выгрузку денежного журнала.', 'Журнал денег');
      return;
    }

    setMoneyOperationsExporting(true);
    try {
      const { blob, fileName } = await apiService.exportMoneyOperationsCsv({
        from,
        to,
        source: apiSource,
        direction: directionFilter === 'all' ? undefined : directionFilter,
        accountingType: accountingTypeFilter === 'all' ? undefined : accountingTypeFilter,
        moneyAccountId: moneyAccountId === '' ? undefined : moneyAccountId,
        accountName: accountName || undefined,
        categoryName: categoryName.trim() || undefined,
        search: effectiveSearch,
      });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName || 'money-operations.csv';
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
      toast.success('Журнал экспортирован в CSV.', 'Журнал денег');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось экспортировать журнал в CSV.');
      toast.error(message, 'Журнал денег');
    } finally {
      setMoneyOperationsExporting(false);
    }
  };

  const handleExportPdf = async () => {
    if (!canExportJournal) {
      toast.error('У роли нет права на выгрузку денежного журнала.', 'Журнал денег');
      return;
    }

    setMoneyOperationsPdfExporting(true);
    try {
      const { blob, fileName } = await apiService.exportMoneyOperationsPdf({
        from,
        to,
        source: apiSource,
        direction: directionFilter === 'all' ? undefined : directionFilter,
        accountingType: accountingTypeFilter === 'all' ? undefined : accountingTypeFilter,
        moneyAccountId: moneyAccountId === '' ? undefined : moneyAccountId,
        accountName: accountName || undefined,
        categoryName: categoryName.trim() || undefined,
        search: effectiveSearch,
      });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName || 'money-operations.pdf';
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
      toast.success('Журнал экспортирован в PDF.', 'Журнал денег');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось экспортировать журнал в PDF.');
      toast.error(message, 'Журнал денег');
    } finally {
      setMoneyOperationsPdfExporting(false);
    }
  };

  const handleExportExcel = async () => {
    if (!canExportJournal) {
      toast.error('У роли нет права на выгрузку денежного журнала.', 'Журнал денег');
      return;
    }

    setMoneyOperationsExcelExporting(true);
    try {
      const { blob, fileName } = await apiService.exportMoneyOperationsExcel({
        from,
        to,
        source: apiSource,
        direction: directionFilter === 'all' ? undefined : directionFilter,
        accountingType: accountingTypeFilter === 'all' ? undefined : accountingTypeFilter,
        moneyAccountId: moneyAccountId === '' ? undefined : moneyAccountId,
        accountName: accountName || undefined,
        categoryName: categoryName.trim() || undefined,
        search: effectiveSearch,
      });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName || 'money-operations.xls';
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
      toast.success('Журнал экспортирован в Excel.', 'Журнал денег');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось экспортировать журнал в Excel.');
      toast.error(message, 'Журнал денег');
    } finally {
      setMoneyOperationsExcelExporting(false);
    }
  };

  const handlePrintJournal = async () => {
    if (!canExportJournal) {
      toast.error('У роли нет права на печать денежного журнала.', 'Журнал денег');
      return;
    }

    setMoneyOperationsPrinting(true);
    try {
      const html = await apiService.getMoneyOperationsPrintHtml({
        from,
        to,
        source: apiSource,
        direction: directionFilter === 'all' ? undefined : directionFilter,
        accountingType: accountingTypeFilter === 'all' ? undefined : accountingTypeFilter,
        moneyAccountId: moneyAccountId === '' ? undefined : moneyAccountId,
        accountName: accountName || undefined,
        categoryName: categoryName.trim() || undefined,
        search: effectiveSearch,
      });
      const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener,noreferrer');
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось открыть печатную форму журнала.');
      toast.error(message, 'Журнал денег');
    } finally {
      setMoneyOperationsPrinting(false);
    }
  };

  const handlePrintCashOrder = async (cashOrderId: number) => {
    try {
      const html = await apiService.getCashOrderPrintHtml(cashOrderId);
      const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener,noreferrer');
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось открыть печатную форму кассового ордера.');
      toast.error(message, 'Касса');
    }
  };

  const handleOpenCashOrder = async (cashOrderId: number) => {
    try {
      const order = await apiService.getCashOrder(cashOrderId);
      setSelectedCashOrder(order);
      setCashOrderModalOpen(true);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось открыть карточку кассового ордера.');
      toast.error(message, 'Касса');
    }
  };

  const handleCreateStandaloneCashOrder = () => {
    if (!canCreateMoneyOperations) {
      toast.error('У вас нет прав на создание кассового ордера.', 'Касса');
      return;
    }

    const cashAccountName = accountName || selectedMoneyAccount?.name || '';
    const draft: CashOrder = {
      id: 0,
      // Пустой номер позволяет серверу назначить следующий номер ПКО/РКО при создании.
      number: '',
      type: 'Incoming',
      status: 'Draft',
      date: todayYMD(),
      amount: 0,
      currency: selectedMoneyAccount?.currency || 'RUB',
      clientId: null,
      clientName: null,
      paymentId: null,
      moneyOperationId: null,
      invoiceId: null,
      account: cashAccountName,
      payerName: '',
      receiverName: cashAccountName,
      basis: '',
      appendix: '',
      comment: '',
      cancelledAt: null,
      cancelledReason: null,
      createdAt: new Date().toISOString(),
      updatedAt: null,
    };

    setSelectedCashOrder(draft);
    setCashOrderModalOpen(true);
  };

  const handleSaveCashOrder = async (payload: CashOrder) => {
    const canSaveCashOrder = payload.id > 0 ? canEditMoneyOperations : canCreateMoneyOperations;
    if (!canSaveCashOrder) {
      toast.error('У вас нет прав на сохранение кассового ордера.', 'Касса');
      return;
    }

    setCashOrderSaving(true);
    try {
      const isNewOrder = payload.id <= 0;
      const updated = isNewOrder
        ? await apiService.createCashOrder(payload)
        : await apiService.updateCashOrder(payload.id, payload);
      setSelectedCashOrder(updated);
      setCashOrdersRefreshKey((value) => value + 1);
      await reload();
      toast.success(isNewOrder ? 'Кассовый ордер создан.' : 'Кассовый ордер сохранён.', 'Касса');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось сохранить кассовый ордер.');
      toast.error(message, 'Касса');
      throw new Error(message);
    } finally {
      setCashOrderSaving(false);
    }
  };

  const handleCancelCashOrder = async (order: CashOrder, reason?: string) => {
    if (!canEditMoneyOperations) {
      toast.error('У вас нет прав на отмену кассового ордера.', 'Касса');
      return;
    }

    setCashOrderSaving(true);
    try {
      const cancelled = await apiService.cancelCashOrder(order.id, reason);
      setSelectedCashOrder(cancelled);
      setCashOrdersRefreshKey((value) => value + 1);
      await reload();
      toast.success('Кассовый ордер отменён.', 'Касса');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось отменить кассовый ордер.');
      toast.error(message, 'Касса');
      throw new Error(message);
    } finally {
      setCashOrderSaving(false);
    }
  };

  const handlePostCashOrder = async (order: CashOrder) => {
    if (!canEditMoneyOperations) {
      toast.error('У вас нет прав на проведение кассового ордера.', 'Касса');
      return;
    }

    setCashOrderSaving(true);
    try {
      const posted = await apiService.postCashOrder(order.id);
      setSelectedCashOrder(posted);
      setCashOrdersRefreshKey((value) => value + 1);
      await reload();
      toast.success('Кассовый ордер проведён.', 'Касса');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось провести кассовый ордер.');
      toast.error(message, 'Касса');
      throw new Error(message);
    } finally {
      setCashOrderSaving(false);
    }
  };

  const handleDeleteCashOrder = async (order: CashOrder) => {
    if (!canDeleteMoneyOperations) {
      toast.error('У вас нет прав на удаление кассового ордера.', 'Касса');
      return;
    }

    setCashOrderSaving(true);
    try {
      await apiService.deleteCashOrder(order.id);
      setSelectedCashOrder(null);
      setCashOrderModalOpen(false);
      setCashOrdersRefreshKey((value) => value + 1);
      await reload();
      toast.success('Черновик кассового ордера удалён.', 'Касса');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось удалить кассовый ордер.');
      toast.error(message, 'Касса');
      throw new Error(message);
    } finally {
      setCashOrderSaving(false);
    }
  };

  const handleCopyCashOrder = async (order: CashOrder) => {
    if (!canEditMoneyOperations) {
      toast.error('У вас нет прав на копирование кассового ордера.', 'Касса');
      return;
    }

    setCashOrderSaving(true);
    try {
      const copied = await apiService.copyCashOrder(order.id);
      setSelectedCashOrder(copied);
      setCashOrdersRefreshKey((value) => value + 1);
      await reload();
      toast.success(`Создана копия кассового ордера ${copied.number}.`, 'Касса');
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось скопировать кассовый ордер.');
      toast.error(message, 'Касса');
      throw new Error(message);
    } finally {
      setCashOrderSaving(false);
    }
  };

  const handleDownloadCashOrderPdf = async (cashOrderId: number) => {
    try {
      const blob = await apiService.downloadCashOrderPdf(cashOrderId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${selectedCashOrder?.number ?? `cash-order-${cashOrderId}`}.pdf`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось скачать PDF кассового ордера.');
      toast.error(message, 'Касса');
      throw new Error(message);
    }
  };

  const handleDownloadCashOrderWord = async (cashOrderId: number) => {
    try {
      const blob = await apiService.downloadCashOrderWord(cashOrderId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${selectedCashOrder?.number ?? `cash-order-${cashOrderId}`}.docx`;
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось скачать Word кассового ордера.');
      toast.error(message, 'Касса');
      throw new Error(message);
    }
  };

  const handleCreateCashOrder = async (item: MoneyOperation) => {
    if (!canCreateMoneyOperations) {
      toast.error('У вас нет прав на создание кассового ордера.', 'Касса');
      return;
    }

    if (!item.relatedPaymentId && item.id <= 0) return;

    try {
      const order = item.relatedPaymentId
        ? await apiService.createCashOrderFromPayment(item.relatedPaymentId)
        : await apiService.createCashOrderFromMoneyOperation(item.id);
      toast.success(`Создан кассовый ордер ${order.number}.`, 'Касса');
      await reload();
      setCashOrdersRefreshKey((value) => value + 1);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось создать кассовый ордер.');
      toast.error(message, 'Касса');
    }
  };

  const handleBankStatementFileSelected = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    if (!canCreateMoneyOperations) {
      toast.error('У вас нет прав на импорт операций денег.', 'Выписка банка');
      return;
    }

    setBankStatementImporting(true);
    setBankStatementImportResult(null);
    try {
      const result = await apiService.importBankStatement(file);
      setBankStatementImportResult({
        batchId: result.batchId ?? null,
        rowsRead: result.rowsRead,
        created: result.created,
        skipped: result.skipped,
        errors: result.errors ?? [],
        operations: result.operations ?? [],
      });
      toast.success(`Создано операций: ${result.created}. Пропущено: ${result.skipped}.`, 'Выписка банка');
      await reload();
      setShowBankImportsJournal(true);
      setBankImportsRefreshKey((value) => value + 1);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось загрузить банковскую выписку.');
      toast.error(message, 'Выписка банка');
    } finally {
      setBankStatementImporting(false);
    }
  };

  const handleOpenImportedOperation = async (operationId: number) => {
    const operation = items.find((item) => item.id === operationId);
    if (operation) {
      handleEdit(operation);
      return;
    }

    try {
      const loadedOperation = await apiService.getMoneyOperation(operationId);
      handleEdit(loadedOperation);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось открыть импортированную операцию.');
      toast.error(message, 'Выписка банка');
    }
  };

  const handleOpenBankImport = async (batchId: number) => {
    setBankImportDetailsLoading(true);
    try {
      const detail = await apiService.getBankStatementImport(batchId);
      setSelectedBankImport(detail);
    } catch (openError) {
      const message = extractDisplayErrorMessage(openError, 'Не удалось открыть импорт выписки.');
      toast.error(message, 'Выписка банка');
    } finally {
      setBankImportDetailsLoading(false);
    }
  };

  const handleRollbackBankImport = async (batchId: number) => {
    if (!canEditMoneyOperations) {
      toast.error('У вас нет прав на откат импорта выписки.', 'Выписка банка');
      return;
    }

    const approved = await confirm({
      title: 'Откатить импорт выписки?',
      description: 'Будут удалены только несверенные операции без связанных кассовых ордеров.',
      confirmLabel: 'Откатить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setBankImportRollbackLoading(true);
    try {
      const result = await apiService.rollbackBankStatementImport(batchId);
      toast.success(
        `Откат импорта: удалено операций ${result.deletedOperations}, пропущено строк ${result.skippedRows}.`,
        'Выписка банка',
      );
      await reload();
      setBankImportsRefreshKey((value) => value + 1);
      if (selectedBankImport?.id === batchId) {
        const detail = await apiService.getBankStatementImport(batchId);
        setSelectedBankImport(detail);
      }
    } catch (rollbackError) {
      const message = extractDisplayErrorMessage(rollbackError, 'Не удалось откатить импорт выписки.');
      toast.error(message, 'Выписка банка');
    } finally {
      setBankImportRollbackLoading(false);
    }
  };

  const handleReprocessBankImport = async (batchId: number) => {
    if (!canCreateMoneyOperations) {
      toast.error('У вас нет прав на повторную обработку выписки.', 'Выписка банка');
      return;
    }

    setBankImportReprocessLoading(true);
    try {
      const result = await apiService.reprocessBankStatementImport(batchId);
      setBankStatementImportResult({
        batchId: result.batchId ?? null,
        rowsRead: result.rowsRead,
        created: result.created,
        skipped: result.skipped,
        errors: result.errors ?? [],
        operations: result.operations ?? [],
      });
      toast.success(`Повторная обработка: создано ${result.created}, пропущено ${result.skipped}.`, 'Выписка банка');
      await reload();
      setBankImportsRefreshKey((value) => value + 1);
      const detail = await apiService.getBankStatementImport(batchId);
      setSelectedBankImport(detail);
    } catch (reprocessError) {
      const message = extractDisplayErrorMessage(reprocessError, 'Не удалось повторно обработать импорт выписки.');
      toast.error(message, 'Выписка банка');
    } finally {
      setBankImportReprocessLoading(false);
    }
  };

  const renderCell = (item: MoneyOperation, columnKey: MoneyOperationColumnKey) => {
    switch (columnKey) {
      case 'date':
        return formatDate(item.date);
      case 'source':
        return SOURCE_LABELS[item.source];
      case 'journalStatus':
        return renderJournalStatusBadge(item);
      case 'recognitionDate':
        return item.recognitionDate ? formatDate(item.recognitionDate) : '—';
      case 'income':
        return (
          <span className="text-emerald-600">
            {item.direction === 'Income' ? `${formatMoney(item.amount)} ₽` : '—'}
          </span>
        );
      case 'expense':
        return (
          <span className="text-red-600">
            {item.direction === 'Expense' ? `${formatMoney(item.amount)} ₽` : '—'}
          </span>
        );
      case 'vatRate':
        return item.vatRate === null || item.vatRate === undefined ? '—' : `${formatMoney(item.vatRate)}%`;
      case 'vatAmount':
        return item.vatAmount === null || item.vatAmount === undefined ? '—' : `${formatMoney(item.vatAmount)} ₽`;
      case 'currency':
        return item.currency ?? 'RUB';
      case 'accountName':
        return item.accountName && canOpenLinkedAccounts ? (
          <button
            type="button"
            onClick={() => setActiveTab('accounts')}
            className="text-blue-600 underline-offset-2 hover:underline">
            {item.accountName}
          </button>
        ) : (
          item.accountName ?? '—'
        );
      case 'counterpartyName':
        return item.counterpartyName ?? '—';
      case 'responsibleName':
        return item.responsibleName ?? '—';
      case 'projectName':
        return item.projectName && canOpenLinkedClients ? (
          <button
            type="button"
            onClick={() => setActiveTab('clients')}
            className="text-blue-600 underline-offset-2 hover:underline">
            {item.projectName}
          </button>
        ) : (
          item.projectName ?? '—'
        );
      case 'salesPointName':
        return item.salesPointName ?? '—';
      case 'directionName':
        return item.directionName ?? '—';
      case 'paymentPriority':
        return item.paymentPriority ?? '—';
      case 'purpose':
        return (
          <div className="line-clamp-3 text-slate-700" title={item.purpose ?? undefined}>
            {item.purpose ?? '—'}
          </div>
        );
      case 'paidAccountName':
        return item.linkedInvoiceId && openLinkedInvoice ? (
          <button
            type="button"
            onClick={() => openLinkedInvoice(item.linkedInvoiceId!, item.direction === 'Expense' ? 'Expense' : 'Income')}
            className="text-blue-600 underline-offset-2 hover:underline">
            {item.paidAccountName || item.linkedInvoiceNumber || `Счёт ${item.linkedInvoiceId}`}
          </button>
        ) : (
          item.paidAccountName ?? '—'
        );
      case 'linkedContractNumber':
        return item.linkedContractNumber ?? '—';
      case 'linkedActNumber':
        return item.linkedActNumber ?? '—';
      case 'matchStatus':
        return renderMatchStatusBadge(item, openLinkedInvoice, handleEdit);
      case 'attachmentSummary':
        return item.attachmentSummary ? (
          <div className="line-clamp-2 text-slate-700" title={item.attachmentSummary}>
            {item.attachmentSummary}
          </div>
        ) : (
          '—'
        );
      case 'categoryName':
        return item.categoryName ?? '—';
      case 'cashOrder':
        if (item.cashOrderNumber) {
          return (
            <button
              type="button"
              onClick={() => item.cashOrderId && void handleOpenCashOrder(item.cashOrderId)}
              className="inline-flex items-center gap-2 rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-medium text-emerald-700 hover:bg-emerald-100"
              title="Открыть карточку кассового ордера">
              <ReceiptText className="h-3.5 w-3.5" />
              <span className="inline-flex flex-col items-start leading-tight">
                <span>{item.cashOrderNumber}</span>
                {item.cashOrderDate && (
                  <span className="text-[11px] font-normal text-emerald-600">
                    {formatDate(item.cashOrderDate)}
                  </span>
                )}
              </span>
            </button>
          );
        }

        if (item.canCreateCashOrder) {
          return (
            <span className="inline-flex items-center gap-2 rounded-full bg-amber-50 px-2.5 py-1 text-xs font-medium text-amber-700">
              Нет приходного кассового ордера
            </span>
          );
        }

        return '—';
      default:
        return '—';
    }
  };

  useEffect(() => {
    setPage(1);
  }, [
    accountName,
    accountingTypeFilter,
    categoryName,
    effectiveSearch,
    directionFilter,
    from,
    moneyAccountId,
    pageSize,
    source,
    to,
    totalSourceFilter,
  ]);

  useEffect(() => {
    if (page > totalPages) {
      setPage(totalPages);
    }
  }, [page, source, totalPages]);

  const savePreparedMoneyOperation = async (prepared: MoneyOperation) => {
    if (editingOperation ? !canEditMoneyOperations : !canCreateMoneyOperations) {
      toast.error('У вас нет прав на сохранение операции денег.', 'Деньги');
      return;
    }

    setSaving(true);
    try {
      if (editingOperation) {
        await apiService.updateMoneyOperation(editingOperation.id, prepared);
        await reload();
        toast.success('Операция обновлена.', 'Деньги');
      } else if (prepared.direction === 'Transfer') {
        await apiService.createMoneyOperationTransfer({
          sourceMoneyAccountId: prepared.moneyAccountId ?? 0,
          targetMoneyAccountId: prepared.transferTargetMoneyAccountId ?? 0,
          amount: prepared.amount,
          currency: prepared.currency ?? null,
          date: prepared.date,
          recognitionDate: prepared.recognitionDate,
          paymentOrderNumber: prepared.paymentOrderNumber ?? null,
          purpose: prepared.purpose ?? null,
          responsibleUserId: prepared.responsibleUserId ?? null,
        });
        toast.success('Перевод между счетами создан.', 'Деньги');
        if (source !== 'Total' && page !== 1) {
          setPage(1);
        } else {
          await reload();
        }
      } else {
        await apiService.createMoneyOperation({
          ...prepared,
          id: 0,
          createdAt: new Date().toISOString(),
        });
        toast.success('Операция создана.', 'Деньги');
        if (source !== 'Total' && page !== 1) {
          setPage(1);
        } else {
          await reload();
        }
      }
      setModalOpen(false);
      setEditingOperation(null);
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось сохранить операцию.');
      toast.error(message, 'Деньги');
      throw new Error(message);
    } finally {
      setSaving(false);
    }
  };

  const prepareMoneyOperationForSave = (payload: MoneyOperation): MoneyOperation => {
    const normalizedRecognitionDate =
      payload.recognitionDate && payload.recognitionDate.trim() !== '' ? payload.recognitionDate : null;
    const normalizedDate = fromInputToApiDate(payload.date) ?? payload.date;
    const normalizedPaidAmount = Number.isFinite(payload.paidAmount ?? NaN) ? payload.paidAmount : null;
    const normalizedLinkedInvoiceId =
      Number.isFinite(payload.linkedInvoiceId ?? NaN) && (payload.linkedInvoiceId ?? 0) > 0
        ? payload.linkedInvoiceId
        : null;
    const selectedCategory = findCategoryOption(categoryDictionaryOptions, payload.direction, payload.categoryName);

    return {
      ...payload,
      source: apiSource ?? payload.source,
      date: normalizedDate,
      recognitionDate: normalizedRecognitionDate,
      paidAmount: normalizedPaidAmount,
      incomeTypeId: selectedCategory?.id ?? payload.incomeTypeId ?? null,
      categoryName: selectedCategory ? null : payload.categoryName,
      linkedInvoiceId: normalizedLinkedInvoiceId,
      matchStatus: normalizedLinkedInvoiceId ? 'Matched' : payload.matchStatus ?? 'None',
    };
  };

  const handleSave = async (payload: MoneyOperation) => {
    const prepared = prepareMoneyOperationForSave(payload);
    if (isUnallocatedAdvanceMoneyOperation(prepared) && !prepared.isAdvancePayment) {
      setPendingUnallocatedAdvanceOperation(prepared);
      return;
    }

    await savePreparedMoneyOperation(prepared);
  };

  const confirmUnallocatedAdvanceOperation = async () => {
    if (!pendingUnallocatedAdvanceOperation) return;

    const prepared = {
      ...pendingUnallocatedAdvanceOperation,
      isAdvancePayment: true,
      matchStatus:
        pendingUnallocatedAdvanceOperation.matchStatus === 'Ignored'
          ? pendingUnallocatedAdvanceOperation.matchStatus
          : ('ClarificationRequired' as MoneyOperation['matchStatus']),
      matchComment:
        pendingUnallocatedAdvanceOperation.matchComment?.trim() ||
        'Поступление не связано со счетом. Проверьте его как возможный нераспределенный аванс.',
    };

    setPendingUnallocatedAdvanceOperation(null);
    await savePreparedMoneyOperation(prepared);
  };

  const handleEdit = (item: MoneyOperation) => {
    if (!canEditMoneyOperations) return;
    if (item.isReadonly || item.id <= 0) return;
    setEditingOperation(item);
    setModalOpen(true);
  };

  const handleDelete = (item: MoneyOperation) => {
    if (!canDeleteMoneyOperations) return;
    if (item.isReadonly || item.id <= 0) return;
    setConfirmDelete(item);
  };

  const toggleOperationSelection = (id: number, checked: boolean) => {
    setSelectedOperationIds((current) => {
      if (checked) {
        return current.includes(id) ? current : [...current, id];
      }

      return current.filter((itemId) => itemId !== id);
    });
  };

  const toggleVisibleOperationsSelection = (checked: boolean) => {
    setSelectedOperationIds(checked ? selectableOperationIds : []);
  };

  const confirmDeleteOperation = async () => {
    if (!confirmDelete) return;
    if (!canDeleteMoneyOperations) {
      setConfirmDelete(null);
      return;
    }

    try {
      await apiService.deleteMoneyOperation(confirmDelete.id);
      toast.success('Операция удалена.', 'Деньги');
      setConfirmDelete(null);
      if (source !== 'Total' && items.length === 1 && page > 1) {
        setPage((prev) => Math.max(1, prev - 1));
      } else {
        await reload();
      }
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось удалить операцию.');
      toast.error(message, 'Деньги');
    }
  };

  const confirmBulkDeleteOperations = async () => {
    if (selectedOperationIds.length === 0) return;
    if (!canDeleteMoneyOperations) {
      setConfirmBulkDeleteOpen(false);
      return;
    }

    const idsToDelete = [...selectedOperationIds];
    try {
      await Promise.all(idsToDelete.map((id) => apiService.deleteMoneyOperation(id)));
      toast.success(`Удалено операций: ${idsToDelete.length}.`, 'Деньги');
      setSelectedOperationIds([]);
      setConfirmBulkDeleteOpen(false);
      if (source !== 'Total' && items.length === idsToDelete.length && page > 1) {
        setPage((prev) => Math.max(1, prev - 1));
      } else {
        await reload();
      }
    } catch (error) {
      const message = extractDisplayErrorMessage(error, 'Не удалось удалить выбранные операции.');
      toast.error(message, 'Деньги');
    }
  };

  return (
    <div className="px-6 py-6">
      {source === 'Total' ? (
        <div className="space-y-4">
          <div className="flex items-center gap-6 border-b border-slate-200">
            <button
              type="button"
              onClick={() => setTotalTab('total')}
              className={[
                'px-1 py-3 text-sm font-medium',
                totalTab === 'total' ? 'border-b-2 border-blue-600 text-blue-600' : 'text-slate-500',
              ].join(' ')}>
              Итого
            </button>
            <button
              type="button"
              onClick={() => setTotalTab('cashflow')}
              className={[
                'px-1 py-3 text-sm font-medium',
                totalTab === 'cashflow' ? 'border-b-2 border-blue-600 text-blue-600' : 'text-slate-500',
              ].join(' ')}>
              ОДДС
            </button>
          </div>

          <div className="flex flex-wrap items-end justify-between gap-3 rounded-xl border border-slate-200 bg-white px-6 py-4">
            <div>
              <div className="text-lg font-semibold text-slate-900">Обороты</div>
              <p className="text-sm text-slate-500">Свод по банку, кассе, картам и электронным кошелькам.</p>
            </div>
            <div className="flex flex-wrap items-end gap-3">
              <label className="flex flex-col gap-1 text-xs font-medium uppercase tracking-wide text-slate-400">
                С
                <SystemDateInput
                  type="date"
                  value={from}
                  onChange={(event) => setFrom(event.target.value)}
                  className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-normal normal-case tracking-normal text-slate-700 focus:border-blue-500 focus:outline-none"
                />
              </label>
              <label className="flex flex-col gap-1 text-xs font-medium uppercase tracking-wide text-slate-400">
                По
                <SystemDateInput
                  type="date"
                  value={to}
                  onChange={(event) => setTo(event.target.value)}
                  className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-normal normal-case tracking-normal text-slate-700 focus:border-blue-500 focus:outline-none"
                />
              </label>
              <button
                type="button"
                onClick={handleCurrentYear}
                className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <Calendar className="h-4 w-4" />
                Текущий год
              </button>
            </div>
          </div>

          <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <div className="rounded-xl border border-slate-200 bg-white p-4">
              <div className="text-sm text-slate-500">Баланс на начало</div>
              <div className="mt-1 text-xl font-semibold text-amber-600">{formatMoney(totalRows.total.start)} ₽</div>
              <div className="mt-3 h-0.5 w-full rounded-full bg-amber-400"></div>
              <div className="mt-2 text-xs text-slate-400">{formatDate(from)}</div>
            </div>
            <div className="rounded-xl border border-slate-200 bg-white p-4">
              <div className="text-sm text-slate-500">Приход за период</div>
              <div className="mt-1 text-xl font-semibold text-emerald-600">{formatMoney(totalRows.total.income)} ₽</div>
              <div className="mt-3 h-0.5 w-full rounded-full bg-emerald-400"></div>
              <div className="mt-2 text-xs text-slate-400">Все источники</div>
            </div>
            <div className="rounded-xl border border-slate-200 bg-white p-4">
              <div className="text-sm text-slate-500">Расход за период</div>
              <div className="mt-1 text-xl font-semibold text-rose-600">{formatMoney(totalRows.total.expense)} ₽</div>
              <div className="mt-3 h-0.5 w-full rounded-full bg-rose-400"></div>
              <div className="mt-2 text-xs text-slate-400">Все источники</div>
            </div>
            <div className="rounded-xl border border-slate-200 bg-white p-4">
              <div className="text-sm text-slate-500">Общий баланс</div>
              <div className="mt-1 text-xl font-semibold text-blue-700">{formatMoney(totalRows.total.end)} ₽</div>
              <div className="mt-3 h-0.5 w-full rounded-full bg-blue-500"></div>
              <div className="mt-2 text-xs text-slate-400">{formatDate(to)}</div>
            </div>
          </div>

          <div className="rounded-xl border border-slate-200 bg-white">
            <div className="overflow-x-auto">
              <table className="w-full min-w-[900px] table-fixed text-sm">
                <colgroup>
                  <col style={{ width: '24%' }} />
                  <col style={{ width: '19%' }} />
                  <col style={{ width: '19%' }} />
                  <col style={{ width: '19%' }} />
                  <col style={{ width: '19%' }} />
                </colgroup>
                <thead className="bg-slate-50 text-slate-500">
                  <tr>
                    <th className="px-4 py-3 text-left font-medium">Кошелёк</th>
                    <th className="px-4 py-3 text-left font-medium">Баланс на начало периода</th>
                    <th className="px-4 py-3 text-left font-medium">Приход</th>
                    <th className="px-4 py-3 text-left font-medium">Расход</th>
                    <th className="px-4 py-3 text-left font-medium">Баланс на конец периода</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {totalRows.rows.map((row) => (
                    <tr key={row.label} className="hover:bg-slate-50">
                      <td className="px-4 py-3 font-medium text-slate-800">{row.label}</td>
                      <td className="px-4 py-3">{formatMoney(row.start)}</td>
                      <td className="px-4 py-3">{formatMoney(row.income)}</td>
                      <td className="px-4 py-3">{formatMoney(row.expense)}</td>
                      <td className="px-4 py-3">{formatMoney(row.end)}</td>
                    </tr>
                  ))}
                  <tr className="bg-slate-50 font-semibold">
                    <td className="px-4 py-3 text-slate-900">Итого</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.start)}</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.income)}</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.expense)}</td>
                    <td className="px-4 py-3">{formatMoney(totalRows.total.end)}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
          {totalTab === 'cashflow' && (
            <div className="rounded-xl border border-slate-200 bg-white">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 px-4 py-3">
                <div>
                  <h2 className="text-base font-semibold text-slate-900">Журнал движений</h2>
                  <p className="text-sm text-slate-500">Все фактические и плановые операции из разделов денег.</p>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <SystemSelect
                    value={totalSourceFilter}
                    onChange={(event) => setTotalSourceFilter(event.target.value as 'all' | MoneyOperationSource)}
                    className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
                    <option value="all">Все источники</option>
                    <option value="Bank">Банк</option>
                    <option value="Cash">Касса</option>
                    <option value="Card">Карты</option>
                    <option value="EMoney">Электронные деньги</option>
                  </SystemSelect>
                  <SystemSelect
                    value={directionFilter}
                    onChange={(event) => setDirectionFilter(event.target.value as 'all' | MoneyOperationDirection)}
                    className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
                    <option value="all">Все движения</option>
                    <option value="Income">Приход</option>
                    <option value="Expense">Расход</option>
                  </SystemSelect>
                  <SystemSelect
                    value={accountingTypeFilter}
                    onChange={(event) => setAccountingTypeFilter(event.target.value as 'all' | MoneyOperationAccountingType)}
                    className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
                    <option value="all">Факт и план</option>
                    <option value="Fact">Факт</option>
                    <option value="Plan">План</option>
                  </SystemSelect>
                  <input
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                    onKeyDown={handleSearchKeyDown}
                    placeholder="Поиск по контрагенту, назначению или документу"
                    className="min-w-[260px] rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none"
                  />
                </div>
              </div>

              <div className="overflow-x-auto">
                <table className="w-full min-w-[1100px] text-sm">
                  <thead className="bg-slate-50 text-slate-500">
                    <tr>
                      <th className="px-3 py-2 text-left font-medium">Источник</th>
                      <th className="px-3 py-2 text-left font-medium">Дата</th>
                      <th className="px-3 py-2 text-left font-medium">Сумма</th>
                      <th className="px-3 py-2 text-left font-medium">Счёт</th>
                      <th className="px-3 py-2 text-left font-medium">Контрагент</th>
                      <th className="px-3 py-2 text-left font-medium">Назначение</th>
                      <th className="px-3 py-2 text-left font-medium">Сверка</th>
                      <th className="px-3 py-2 text-right font-medium">Действия</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {loading ? (
                      <tr>
                        <td colSpan={8} className="px-4 py-10 text-center text-slate-500">
                          Загрузка операций...
                        </td>
                      </tr>
                    ) : error ? (
                      <tr>
                        <td colSpan={8} className="px-4 py-10 text-center text-red-500">
                          {error}
                        </td>
                      </tr>
                    ) : items.length === 0 ? (
                      <tr>
                        <td colSpan={8} className="px-4 py-10 text-center text-slate-500">
                          В указанном периоде нет движений денег.
                        </td>
                      </tr>
                    ) : (
                      items.map((item) => (
                        <tr key={item.id} className="hover:bg-slate-50">
                          <td className="px-3 py-2 font-medium text-slate-800">{SOURCE_LABELS[item.source]}</td>
                          <td className="px-3 py-2">{formatDate(item.date)}</td>
                          <td className="px-3 py-2">
                            <span className={item.direction === 'Income' ? 'text-emerald-600' : 'text-rose-600'}>
                              {item.direction === 'Income' ? '+' : '-'} {formatMoney(item.amount)} ₽
                            </span>
                          </td>
                          <td className="px-3 py-2">{item.accountName || '—'}</td>
                          <td className="px-3 py-2">{item.counterpartyName || '—'}</td>
                          <td className="px-3 py-2">
                            <div className="line-clamp-2 text-slate-700" title={item.purpose ?? undefined}>
                              {item.purpose || '—'}
                            </div>
                          </td>
                          <td className="px-3 py-2">{renderMatchStatusBadge(item, openLinkedInvoice, handleEdit)}</td>
                          <td className="px-3 py-2 text-right">
                            <div className="inline-flex items-center gap-2">
                              {item.matchStatus === 'ClarificationRequired' && (
                                <button
                                  type="button"
                                  onClick={() => handleEdit(item)}
                                  disabled={Boolean(item.isReadonly) || item.id <= 0 || !canEditMoneyOperations}
                                  className="rounded-md border border-amber-200 p-2 text-amber-700 hover:bg-amber-50 disabled:cursor-not-allowed disabled:opacity-40"
                                  aria-label="Уточнить"
                                  title="Уточнить операцию и привязать счёт">
                                  <AlertTriangle className="h-4 w-4" />
                                </button>
                              )}
                              <button
                                type="button"
                                onClick={() => handleEdit(item)}
                                disabled={Boolean(item.isReadonly) || item.id <= 0 || !canEditMoneyOperations}
                                className="rounded-md border border-slate-200 p-2 text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                                aria-label="Изменить">
                                <Pencil className="h-4 w-4" />
                              </button>
                            </div>
                          </td>
                        </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>

              <TablePagination
                page={page}
                totalPages={totalPages}
                totalItems={total}
                pageSize={pageSize}
                pageSizeOptions={PAGE_SIZE_OPTIONS}
                onPageChange={setPage}
                onPageSizeChange={setPageSize}
                recordsLabel="Найдено движений"
                pageSizeLabel="Строк на странице"
                previousLabel="Назад"
                nextLabel="Вперед"
              />
            </div>
          )}
        </div>
      ) : (
        <>
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">
            {selectedMoneyAccount ? selectedMoneyAccount.name : SOURCE_LABELS[source]}
          </h1>
          <p className="text-sm text-slate-500">{SOURCE_OPERATION_SUBTITLES[source]}</p>
          {selectedMoneyAccount && (
            <p className="mt-1 text-xs text-slate-400">
              {selectedMoneyAccount.bankName ? `${selectedMoneyAccount.bankName} · ` : ''}
              {selectedMoneyAccount.bankAccountNumber || selectedMoneyAccount.currency || 'RUB'}
            </p>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-3">
          {(
            <div className="flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-slate-400">
              <span>{SOURCE_ACCOUNT_FILTER_LABELS[source]}</span>
              <SystemSelect
                aria-label={`${SOURCE_ACCOUNT_FILTER_LABELS[source]} в заголовке`}
                value={moneyAccountId === '' ? accountName : String(moneyAccountId)}
                onChange={(event) => selectMoneyAccount(event.target.value)}
                className="min-w-52 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-normal normal-case tracking-normal text-slate-700 focus:border-blue-500 focus:outline-none">
                <option value="">{SOURCE_ALL_ACCOUNTS_LABELS[source]}</option>
                {visibleMoneyAccountOptions.map((account) => (
                  <option key={`${account.source}-${account.accountName}-${account.id ?? ''}`} value={account.id ?? account.accountName}>
                    {account.accountName}
                  </option>
                ))}
              </SystemSelect>
            </div>
          )}
          {source === 'Bank' && (
            <>
              <input
                ref={bankStatementInputRef}
                type="file"
                accept=".csv,.tsv,.txt,.1c,text/csv,text/tab-separated-values,text/plain"
                className="hidden"
                onChange={handleBankStatementFileSelected}
              />
              <button
                className="flex items-center gap-2 rounded-lg border border-blue-500 px-4 py-2 text-sm font-medium text-blue-600 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
                type="button"
                disabled={bankStatementImporting || !canCreateMoneyOperations}
                onClick={() => bankStatementInputRef.current?.click()}>
                <UploadCloud className="h-4 w-4" />
                {bankStatementImporting ? 'Загружаем...' : 'Загрузить выписку'}
              </button>
              <div className="flex flex-wrap items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-600">
                <label className="inline-flex items-center gap-2">
                  <SystemCheckbox
                    type="checkbox"
                    role="switch"
                    checked={bankAutoImportSettings.enabled}
                    disabled={bankAutoImportLoading || bankAutoImportSaving || !canCreateMoneyOperations}
                    onChange={(event) => updateBankAutoImportSettings({ enabled: event.target.checked })}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  <span className="font-medium">Автоподгрузка</span>
                </label>
                <button
                  type="button"
                  onClick={() => void saveBankAutoImportSettings()}
                  disabled={bankAutoImportLoading || bankAutoImportSaving || !canCreateMoneyOperations}
                  className="rounded-md border border-blue-200 px-2 py-1 text-xs font-medium text-blue-700 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60">
                  {bankAutoImportSaving ? 'Сохранение...' : 'Сохранить'}
                </button>
              </div>
              <button
                type="button"
                aria-label={showBankImportsJournal ? 'Скрыть журнал импортов выписки' : 'Открыть журнал импортов выписки'}
                onClick={() => setShowBankImportsJournal((value) => !value)}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50">
                Журнал импортов
                <ChevronDown className={`h-4 w-4 transition-transform ${showBankImportsJournal ? 'rotate-180' : ''}`} />
              </button>
              <button
                type="button"
                aria-label={showBankCategoryRules ? 'Скрыть правила распознавания выписки' : 'Открыть правила распознавания выписки'}
                onClick={() => setShowBankCategoryRules((value) => !value)}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50">
                <SlidersHorizontal className="h-4 w-4" />
                Правила
              </button>
              <div className="text-xs text-slate-500">CSV, TSV, TXT, выгрузка 1С</div>
            </>
          )}
        </div>
      </div>

      {source === 'Bank' && bankAutoImportSettings.enabled && (
        <div className="mt-4 rounded-xl border border-blue-100 bg-blue-50/70 px-4 py-3">
          <div className="grid gap-3 md:grid-cols-[1fr_220px_auto_auto]">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Папка выписок
              <input
                value={bankAutoImportSettings.folderPath}
                onChange={(event) => updateBankAutoImportSettings({ folderPath: event.target.value })}
                placeholder="C:\\BankStatements"
                className="rounded-lg border border-blue-100 bg-white px-3 py-2"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Маска файлов
              <input
                value={bankAutoImportSettings.fileMask}
                onChange={(event) => updateBankAutoImportSettings({ fileMask: event.target.value })}
                placeholder="*.csv;*.txt;*.1c"
                className="rounded-lg border border-blue-100 bg-white px-3 py-2"
              />
            </label>
            <div className="flex items-end">
              <button
                type="button"
                onClick={() => void saveBankAutoImportSettings()}
                disabled={bankAutoImportLoading || bankAutoImportSaving || !canCreateMoneyOperations}
                className="inline-flex w-full items-center justify-center rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60">
                {bankAutoImportSaving ? 'Сохранение...' : 'Сохранить'}
              </button>
            </div>
            <div className="flex items-end">
              <button
                type="button"
                onClick={() => void handleRunBankAutoImport()}
                disabled={bankAutoImportLoading || bankAutoImportSaving || bankAutoImportRunning || !canCreateMoneyOperations}
                className="inline-flex w-full items-center justify-center gap-2 rounded-lg border border-blue-200 bg-white px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60">
                <RefreshCw className={`h-4 w-4 ${bankAutoImportRunning ? 'animate-spin' : ''}`} />
                {bankAutoImportRunning ? 'Проверяем...' : 'Проверить'}
              </button>
            </div>
          </div>
          <div className="mt-2 text-xs text-blue-700">
            {bankAutoImportSettings.lastCheckedAt
              ? `Последняя проверка: ${formatDate(bankAutoImportSettings.lastCheckedAt, profileTimeZoneId)}`
              : 'Ожидает первой проверки.'}
          </div>
          {bankAutoImportRunResult && (
            <div className="mt-3 rounded-lg border border-blue-100 bg-white px-3 py-2 text-xs text-slate-600">
              <div className="font-medium text-slate-800">
                Проверено файлов: {bankAutoImportRunResult.filesFound}. Создано операций: {bankAutoImportRunResult.created}.
                Пропущено: {bankAutoImportRunResult.skipped}.
              </div>
              {bankAutoImportRunResult.files.length > 0 && (
                <div className="mt-1 flex flex-wrap gap-2">
                  {bankAutoImportRunResult.files.slice(0, 4).map((file) => (
                    <span key={`${file.fileName}-${file.batchId ?? 'skip'}`} className="rounded-full bg-blue-50 px-2 py-0.5 text-blue-700">
                      {file.fileName}: +{file.created} / {file.skipped}
                    </span>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {source === 'Cash' && (
        <div className="mt-4 rounded-xl border border-amber-200 bg-amber-50/70 px-4 py-3">
          <div className="grid gap-3 md:grid-cols-[auto_240px_auto_1fr]">
            <label className="flex items-center gap-2 text-sm font-medium text-amber-900">
              <SystemCheckbox
                type="checkbox"
                role="switch"
                checked={cashLimitSettings.enabled}
                disabled={cashLimitLoading || cashLimitSaving}
                onChange={(event) => updateCashLimitSettings({ enabled: event.target.checked })}
                className="h-4 w-4 rounded border-amber-300 text-amber-600 focus:ring-amber-500"
              />
              Лимит кассы
            </label>
            <label className="flex flex-col gap-1 text-sm text-amber-900">
              Сумма лимита
              <input
                type="number"
                min="0"
                step="100"
                value={cashLimitSettings.amount ?? ''}
                onChange={(event) =>
                  updateCashLimitSettings({
                    amount: event.target.value ? Number(event.target.value) : null,
                  })
                }
                placeholder="0"
                className="rounded-lg border border-amber-200 bg-white px-3 py-2 text-sm text-slate-700"
              />
            </label>
            <div className="flex items-end">
              <button
                type="button"
                onClick={() => void saveCashLimitSettings()}
                disabled={cashLimitLoading || cashLimitSaving}
                className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:cursor-not-allowed disabled:opacity-60">
                <Save className="h-4 w-4" />
                {cashLimitSaving ? 'Сохранение...' : 'Сохранить'}
              </button>
            </div>
            <div className="flex items-end text-sm text-amber-900">
              {cashLimitSettings.enabled && cashLimitSettings.amount
                ? cashLimitExceedances.length > 0
                  ? `Превышений за период: ${cashLimitExceedances.length}`
                  : 'Превышений за период нет.'
                : 'Лимит выключен.'}
            </div>
          </div>

          {cashLimitSettings.enabled && cashLimitSettings.amount && cashLimitExceedances.length > 0 && (
            <div className="mt-3 overflow-x-auto rounded-lg border border-amber-200 bg-white">
              <table className="w-full min-w-[620px] text-sm">
                <thead className="text-xs uppercase text-amber-700">
                  <tr>
                    <th className="px-3 py-2 text-left font-medium">Дата</th>
                    <th className="px-3 py-2 text-left font-medium">Касса</th>
                    <th className="px-3 py-2 text-right font-medium">Остаток</th>
                    <th className="px-3 py-2 text-right font-medium">Превышение</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-amber-100">
                  {cashLimitExceedances.slice(0, 8).map((item) => (
                    <tr key={`${item.accountName}-${item.date}`}>
                      <td className="px-3 py-2 font-medium text-slate-800">{formatDate(item.date)}</td>
                      <td className="px-3 py-2 text-slate-600">{item.accountName}</td>
                      <td className="px-3 py-2 text-right text-amber-700">{formatMoney(item.closingBalance)} ₽</td>
                      <td className="px-3 py-2 text-right text-rose-600">{formatMoney(item.excess)} ₽</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {cashLimitExceedances.length > 8 && (
                <div className="border-t border-amber-100 px-3 py-2 text-xs text-amber-700">
                  Показаны первые 8 дней из {cashLimitExceedances.length}
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {source === 'Cash' && (
        <div className="mt-4 rounded-xl border border-slate-200 bg-white px-4 py-3">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold text-slate-900">Инвентаризация кассы</h2>
              <p className="text-xs text-slate-500">
                Фиксация фактического остатка и расхождения с учетным остатком по выбранной кассе.
              </p>
            </div>
            <div className="rounded-lg bg-slate-50 px-3 py-2 text-right">
              <div className="text-xs uppercase text-slate-400">Учетный остаток</div>
              <div className="text-base font-semibold text-slate-900">{formatMoney(cashInventoryAccountingBalance)} ₽</div>
            </div>
          </div>

          <div className="mt-3 grid gap-3 md:grid-cols-[180px_1fr_auto]">
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Фактический остаток
              <input
                type="number"
                min="0"
                step="100"
                value={cashInventoryActualBalance}
                disabled={!canEditMoneyOperations}
                onChange={(event) => setCashInventoryActualBalance(event.target.value)}
                placeholder="0"
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700"
              />
            </label>
            <label className="flex flex-col gap-1 text-sm text-slate-600">
              Комментарий
              <input
                value={cashInventoryComment}
                disabled={!canEditMoneyOperations}
                onChange={(event) => setCashInventoryComment(event.target.value)}
                placeholder="Причина расхождения или примечание"
                className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700"
              />
            </label>
            <div className="flex items-end">
              <button
                type="button"
                onClick={() => void handleCreateCashInventory()}
                disabled={cashInventorySaving || !canEditMoneyOperations}
                className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60">
                <History className="h-4 w-4" />
                {cashInventorySaving ? 'Сохранение...' : 'Провести'}
              </button>
            </div>
          </div>

          <label className="mt-3 inline-flex items-center gap-2 text-sm text-slate-600">
            <SystemCheckbox
              type="checkbox"
              checked={cashInventoryCreateCorrection}
              disabled={!canEditMoneyOperations}
              onChange={(event) => setCashInventoryCreateCorrection(event.target.checked)}
              className="h-4 w-4 rounded border-slate-300 text-slate-900 focus:ring-slate-500"
            />
            Создать корректирующий приходный/расходный кассовый ордер при расхождении
          </label>

          <div className="mt-3 overflow-x-auto rounded-lg border border-slate-200">
            <table className="w-full min-w-[760px] text-sm">
              <thead className="bg-slate-50 text-xs uppercase text-slate-400">
                <tr>
                  <th className="px-3 py-2 text-left font-medium">Номер</th>
                  <th className="px-3 py-2 text-left font-medium">Дата</th>
                  <th className="px-3 py-2 text-left font-medium">Касса</th>
                  <th className="px-3 py-2 text-right font-medium">Учетный</th>
                  <th className="px-3 py-2 text-right font-medium">Фактический</th>
                  <th className="px-3 py-2 text-right font-medium">Расхождение</th>
                  <th className="px-3 py-2 text-left font-medium">Коррекция</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {cashInventoryLoading ? (
                  <tr>
                    <td colSpan={7} className="px-3 py-6 text-center text-slate-500">
                      Загрузка инвентаризаций...
                    </td>
                  </tr>
                ) : cashInventories.length === 0 ? (
                  <tr>
                    <td colSpan={7} className="px-3 py-6 text-center text-slate-500">
                      Инвентаризации за период еще не проводились.
                    </td>
                  </tr>
                ) : (
                  cashInventories.map((inventory) => (
                    <tr key={inventory.id} className="hover:bg-slate-50">
                      <td className="px-3 py-2 font-medium text-slate-800">{inventory.number}</td>
                      <td className="px-3 py-2 text-slate-600">{formatDate(inventory.date)}</td>
                      <td className="px-3 py-2 text-slate-600">{inventory.accountName}</td>
                      <td className="px-3 py-2 text-right">{formatMoney(inventory.accountingBalance)} ₽</td>
                      <td className="px-3 py-2 text-right">{formatMoney(inventory.actualBalance)} ₽</td>
                      <td className={`px-3 py-2 text-right ${inventory.difference === 0 ? 'text-slate-500' : inventory.difference > 0 ? 'text-emerald-600' : 'text-rose-600'}`}>
                        {formatMoney(inventory.difference)} ₽
                      </td>
                      <td className="px-3 py-2">
                        {inventory.correctionCashOrderId ? (
                          <button
                            type="button"
                            onClick={() => void handleOpenCashOrder(inventory.correctionCashOrderId!)}
                            className="text-blue-600 underline-offset-2 hover:underline">
                            {inventory.correctionCashOrderNumber || `Ордер ${inventory.correctionCashOrderId}`}
                          </button>
                        ) : (
                          '—'
                        )}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
          {cashInventoryTotal > cashInventories.length && (
            <div className="mt-2 text-xs text-slate-500">
              Показаны последние {cashInventories.length} из {cashInventoryTotal} инвентаризаций.
            </div>
          )}
        </div>
      )}

      {source === 'Bank' && showBankCategoryRules && (
        <div className="mt-4 rounded-xl border border-slate-200 bg-white px-4 py-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold text-slate-900">Правила распознавания выписки</h2>
              <p className="text-xs text-slate-500">Ключевые слова назначений, которые будут применяться при следующем импорте.</p>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={addBankCategoryRule}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <Plus className="h-4 w-4" />
                Добавить
              </button>
              <button
                type="button"
                onClick={() => void saveBankCategoryRulesSettings()}
                disabled={bankCategoryRulesLoading || bankCategoryRulesSaving}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60">
                <Save className="h-4 w-4" />
                {bankCategoryRulesSaving ? 'Сохранение...' : 'Сохранить'}
              </button>
            </div>
          </div>

          <div className="mt-3 overflow-x-auto">
            <table className="w-full min-w-[900px] text-sm">
              <thead className="text-xs uppercase text-slate-400">
                <tr>
                  <th className="px-3 py-2 text-left font-medium">Вкл</th>
                  <th className="px-3 py-2 text-left font-medium">Тип</th>
                  <th className="px-3 py-2 text-left font-medium">Категория</th>
                  <th className="px-3 py-2 text-left font-medium">Статья</th>
                  <th className="px-3 py-2 text-left font-medium">Ключевые слова</th>
                  <th className="px-3 py-2 text-right font-medium">Действия</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {bankCategoryRulesSettings.rules.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="px-3 py-6 text-center text-slate-500">
                      Правила ещё не добавлены.
                    </td>
                  </tr>
                ) : (
                  bankCategoryRulesSettings.rules.map((rule) => {
                    const ruleCategoryOptions = categoryDictionaryOptions.filter(
                      (option) => option.paymentType === rule.paymentType,
                    );
                    return (
                      <tr key={rule.id} className="align-top">
                        <td className="px-3 py-2">
                          <SystemCheckbox
                            type="checkbox"
                            checked={rule.enabled}
                            onChange={(event) => updateBankCategoryRule(rule.id, { enabled: event.target.checked })}
                            className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                            aria-label={`Включить правило ${rule.categoryName || rule.id}`}
                          />
                        </td>
                        <td className="px-3 py-2">
                          <SystemSelect
                            value={rule.paymentType}
                            onChange={(event) =>
                              updateBankCategoryRule(rule.id, { paymentType: event.target.value as PaymentKind })
                            }
                            className="w-32 rounded-lg border border-slate-200 px-2 py-1.5 text-sm text-slate-700">
                            <option value="Income">Приход</option>
                            <option value="Expense">Расход</option>
                          </SystemSelect>
                        </td>
                        <td className="px-3 py-2">
                          <input
                            value={rule.categoryName}
                            onChange={(event) => updateBankCategoryRule(rule.id, { categoryName: event.target.value })}
                            placeholder="Например, Хостинг"
                            className="w-52 rounded-lg border border-slate-200 px-2 py-1.5 text-sm text-slate-700"
                          />
                        </td>
                        <td className="px-3 py-2">
                          <SystemSelect
                            value={rule.incomeTypeId ?? ''}
                            onChange={(event) =>
                              updateBankCategoryRule(rule.id, {
                                incomeTypeId: event.target.value ? Number(event.target.value) : null,
                              })
                            }
                            className="w-56 rounded-lg border border-slate-200 px-2 py-1.5 text-sm text-slate-700">
                            <option value="">Только текстовая категория</option>
                            {ruleCategoryOptions.map((option) => (
                              <option key={`${option.paymentType}-${option.id}`} value={option.id}>
                                {option.name}
                              </option>
                            ))}
                          </SystemSelect>
                        </td>
                        <td className="px-3 py-2">
                          <input
                            value={rule.keywords}
                            onChange={(event) => updateBankCategoryRule(rule.id, { keywords: event.target.value })}
                            placeholder="хостинг; cloud; vps"
                            className="w-full min-w-64 rounded-lg border border-slate-200 px-2 py-1.5 text-sm text-slate-700"
                          />
                        </td>
                        <td className="px-3 py-2 text-right">
                          <button
                            type="button"
                            onClick={() => removeBankCategoryRule(rule.id)}
                            aria-label={`Удалить правило ${rule.categoryName || rule.id}`}
                            className="rounded-md border border-rose-200 p-2 text-rose-600 hover:bg-rose-50">
                            <Trash2 className="h-4 w-4" />
                          </button>
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {source === 'Bank' && bankStatementImportResult && (
        <div className="mt-4 rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm text-blue-900">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="font-medium">
              Импорт выписки: прочитано {bankStatementImportResult.rowsRead}, создано {bankStatementImportResult.created}, пропущено {bankStatementImportResult.skipped}
            </div>
            {bankStatementImportResult.batchId && (
              <button
                type="button"
                onClick={() => void handleOpenBankImport(bankStatementImportResult.batchId!)}
                aria-label={`Открыть импорт выписки ${bankStatementImportResult.fileName || `#${bankStatementImportResult.batchId}`}`}
                className="rounded-md border border-blue-200 bg-white px-2.5 py-1.5 text-xs font-medium text-blue-700 hover:bg-blue-50">
                Открыть импорт
              </button>
            )}
          </div>
          {bankStatementImportResult.errors.length > 0 && (
            <div className="mt-2 max-h-24 overflow-y-auto text-xs text-blue-800">
              {bankStatementImportResult.errors.slice(0, 5).map((error) => (
                <div key={error}>{error}</div>
              ))}
              {bankStatementImportResult.errors.length > 5 && (
                <div>Ещё ошибок: {bankStatementImportResult.errors.length - 5}</div>
              )}
            </div>
          )}
          {bankStatementImportResult.operations.length > 0 && (
            <div className="mt-3 overflow-x-auto rounded-lg border border-blue-100 bg-white/70">
              <table className="w-full min-w-[640px] text-xs">
                <thead className="text-blue-700">
                  <tr>
                    <th className="px-3 py-2 text-left font-medium">Дата</th>
                    <th className="px-3 py-2 text-left font-medium">Тип</th>
                    <th className="px-3 py-2 text-left font-medium">Сумма</th>
                    <th className="px-3 py-2 text-left font-medium">Контрагент</th>
                    <th className="px-3 py-2 text-left font-medium">Документ</th>
                    <th className="px-3 py-2 text-right font-medium">Действия</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-blue-100 text-blue-950">
                  {bankStatementImportResult.operations.slice(0, 5).map((operation) => (
                    <tr key={`${operation.id}-${operation.date}-${operation.amount}`}>
                      <td className="px-3 py-2">{formatDate(operation.date)}</td>
                      <td className="px-3 py-2">
                        {operation.direction === 'Income' ? 'Приход' : 'Расход'}
                      </td>
                      <td className="px-3 py-2">{formatMoney(operation.amount)} ₽</td>
                      <td className="px-3 py-2">{operation.counterpartyName || '—'}</td>
                      <td className="px-3 py-2">{operation.paymentOrderNumber || '—'}</td>
                      <td className="px-3 py-2 text-right">
                        <button
                          type="button"
                          className="rounded-md border border-blue-200 px-2 py-1 text-xs font-medium text-blue-700 hover:bg-blue-50"
                          aria-label={`Открыть операцию импорта ${operation.id}`}
                          onClick={() => void handleOpenImportedOperation(operation.id)}>
                          Открыть
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {bankStatementImportResult.operations.length > 5 && (
                <div className="border-t border-blue-100 px-3 py-2 text-xs text-blue-700">
                  Ещё создано операций: {bankStatementImportResult.operations.length - 5}
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {source === 'Bank' && showBankImportsJournal && (
        <div className="mt-4 rounded-xl border border-slate-200 bg-white px-4 py-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold text-slate-900">Журнал импортов выписки</h2>
              <p className="text-xs text-slate-500">Последние загрузки, строки и безопасный откат несверенных операций.</p>
            </div>
            <button
              type="button"
              onClick={() => setBankImportsRefreshKey((value) => value + 1)}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
              disabled={bankImportBatchesLoading}>
              <RefreshCw className={`h-4 w-4 ${bankImportBatchesLoading ? 'animate-spin' : ''}`} />
              Обновить
            </button>
          </div>

          <div className="mt-3 overflow-x-auto">
            <table className="w-full min-w-[780px] text-sm">
              <thead className="text-xs uppercase text-slate-400">
                <tr>
                  <th className="px-3 py-2 text-left font-medium">Файл</th>
                  <th className="px-3 py-2 text-left font-medium">Статус</th>
                  <th className="px-3 py-2 text-left font-medium">Строки</th>
                  <th className="px-3 py-2 text-left font-medium">Дата</th>
                  <th className="px-3 py-2 text-right font-medium">Действия</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {bankImportBatchesLoading ? (
                  <tr>
                    <td colSpan={5} className="px-3 py-4 text-center text-slate-500">
                      Загрузка журнала...
                    </td>
                  </tr>
                ) : bankImportBatches.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="px-3 py-4 text-center text-slate-500">
                      Импортов пока нет.
                    </td>
                  </tr>
                ) : (
                  bankImportBatches.map((batch) => (
                    <tr key={batch.id} className="hover:bg-slate-50">
                      <td className="px-3 py-2">
                        <div className="font-medium text-slate-800">{batch.fileName || `Импорт #${batch.id}`}</div>
                        <div className="text-xs text-slate-400">ID {batch.id}</div>
                      </td>
                      <td className="px-3 py-2">
                        <span
                          className={`inline-flex rounded-full px-2.5 py-1 text-xs font-medium ${getBankImportStatusClassName(batch.status)}`}>
                          {getBankImportStatusLabel(batch.status)}
                        </span>
                      </td>
                      <td className="px-3 py-2 text-slate-600">
                        <div>Создано: {batch.createdRows}</div>
                        <div className="text-xs text-slate-400">
                          Пропущено: {batch.skippedRows}, ошибок: {batch.errorRows}
                        </div>
                      </td>
                      <td className="px-3 py-2 text-slate-600">{formatDate(batch.createdAt, profileTimeZoneId)}</td>
                      <td className="px-3 py-2">
                        <div className="flex justify-end gap-2">
                          <button
                            type="button"
                            onClick={() => void handleOpenBankImport(batch.id)}
                            aria-label={`Открыть импорт выписки ${batch.fileName || `#${batch.id}`}`}
                            className="rounded-md border border-slate-200 px-2.5 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50"
                            disabled={bankImportDetailsLoading}>
                            Открыть
                          </button>
                          <button
                            type="button"
                            onClick={() => void handleRollbackBankImport(batch.id)}
                            aria-label={`Откатить импорт выписки ${batch.fileName || `#${batch.id}`} из списка`}
                            className="inline-flex items-center gap-1.5 rounded-md border border-amber-200 px-2.5 py-1.5 text-xs font-medium text-amber-700 hover:bg-amber-50 disabled:cursor-not-allowed disabled:opacity-60"
                            disabled={bankImportRollbackLoading || batch.status === 'RolledBack' || !canEditMoneyOperations}>
                            <RotateCcw className="h-3.5 w-3.5" />
                            Откатить
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      <div className="mt-6 grid gap-4 lg:grid-cols-[1fr_260px]">
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Баланс на начало</div>
            <div className="text-lg font-semibold text-amber-700">{formatMoney(summary.startBalance)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-amber-400"></div>
            <div className="mt-2 text-xs text-slate-400">{formatDate(from)}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Приход Факт</div>
            <div className="text-lg font-semibold text-cyan-700">{formatMoney(summary.income)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-cyan-400"></div>
            <div className="mt-2 text-xs text-slate-400">Всего поступлений: {summary.incomeCount}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Расход Факт</div>
            <div className="text-lg font-semibold text-red-500">{formatMoney(summary.expense)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-red-400"></div>
            <div className="mt-2 text-xs text-slate-400">Всего списаний: {summary.expenseCount}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="text-sm text-slate-500">Баланс на конец</div>
            <div className="text-lg font-semibold text-amber-700">{formatMoney(summary.endBalance)} ₽</div>
            <div className="mt-3 h-0.5 w-full rounded-full bg-amber-400"></div>
            <div className="mt-2 text-xs text-slate-400">{formatDate(to)}</div>
          </div>
        </div>

        <div className="flex flex-col gap-3 rounded-xl border border-slate-200 bg-white p-4">
          <div className="flex items-center justify-between gap-3 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2">
            <div>
              <div className="text-sm font-medium text-slate-800">
                {canViewFullMoneyBalance ? 'Сверка остатков' : 'Движение доступных операций'}
              </div>
              <div className="mt-1 text-xs text-slate-500">
                {canViewFullMoneyBalance
                  ? 'Начало + приход - расход = конец'
                  : 'Доступ к платежам ограничен: общий остаток может отличаться от показанного.'}
              </div>
            </div>
            {canViewFullMoneyBalance && renderBalanceCheckBadge(summary)}
          </div>
          {permissions.payments.canView ? (
            <div className="rounded-lg border border-rose-100 bg-rose-50 px-3 py-2">
              <div className="flex items-start gap-2">
                <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-rose-600" />
                <div className="min-w-0">
                  <div className="text-sm font-semibold text-rose-800">Контроль авансов</div>
                  <div className="mt-1 text-xs text-rose-700/80">
                    {advanceReviewMonitor.loading
                      ? 'Проверяем платежи на разбор...'
                      : advanceReviewMonitor.error
                        ? advanceReviewMonitor.error
                        : advanceReviewMonitor.count > 0
                          ? `На разборе: ${advanceReviewMonitor.count} на ${formatMoney(advanceReviewMonitor.amount)} ₽`
                          : 'Нет платежей, требующих решения по авансу'}
                  </div>
                </div>
              </div>
              <button
                type="button"
                onClick={openAdvanceReviewPayments}
                disabled={advanceReviewMonitor.loading}
                className="mt-2 inline-flex w-full items-center justify-center gap-2 rounded-md border border-rose-200 bg-white px-3 py-1.5 text-xs font-semibold text-rose-700 transition hover:bg-rose-100 disabled:cursor-not-allowed disabled:opacity-60"
              >
                Открыть разбор
              </button>
            </div>
          ) : null}
          <button
            type="button"
            onClick={handleCurrentYear}
            className="flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
            <Calendar className="h-4 w-4" />
            Текущий год
          </button>
          {canCreateMoneyOperations && (
            <button
              type="button"
              onClick={() => {
                setEditingOperation(null);
                setModalOpen(true);
              }}
              className="flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
              <Plus className="h-4 w-4" />
              Добавить
            </button>
          )}
          <button
            type="button"
            onClick={() => void reload()}
            disabled={loading}
            className="flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60">
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
          {canExportJournal ? (
            <div ref={journalActionsRef} className="relative">
              <button
                type="button"
                onClick={() => setJournalActionsOpen((prev) => !prev)}
                disabled={journalActionBusy}
                className="flex w-full items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                aria-haspopup="menu"
                aria-expanded={journalActionsOpen}>
                {journalActionBusy ? (
                  <RefreshCw className="h-4 w-4 animate-spin" />
                ) : (
                  <Download className="h-4 w-4" />
                )}
                {journalActionBusy ? 'Выполняется...' : 'Экспорт и печать'}
                <ChevronDown className={`h-4 w-4 transition-transform ${journalActionsOpen ? 'rotate-180' : ''}`} />
              </button>
              {journalActionsOpen && (
                <div
                  role="menu"
                  className="absolute right-0 z-20 mt-2 w-52 rounded-lg border border-slate-200 bg-white p-1 shadow-lg">
                  <button
                    type="button"
                    onClick={() => {
                      setJournalActionsOpen(false);
                      void handleExportCsv();
                    }}
                    disabled={moneyOperationsExporting}
                    className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60">
                    <Download className={`h-4 w-4 ${moneyOperationsExporting ? 'animate-pulse' : ''}`} />
                    {moneyOperationsExporting ? 'Экспорт CSV...' : 'CSV'}
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setJournalActionsOpen(false);
                      void handleExportPdf();
                    }}
                    disabled={moneyOperationsPdfExporting}
                    className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60">
                    <Download className={`h-4 w-4 ${moneyOperationsPdfExporting ? 'animate-pulse' : ''}`} />
                    {moneyOperationsPdfExporting ? 'Экспорт PDF...' : 'PDF'}
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setJournalActionsOpen(false);
                      void handleExportExcel();
                    }}
                    disabled={moneyOperationsExcelExporting}
                    className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60">
                    <Download className={`h-4 w-4 ${moneyOperationsExcelExporting ? 'animate-pulse' : ''}`} />
                    {moneyOperationsExcelExporting ? 'Экспорт Excel...' : 'Excel'}
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setJournalActionsOpen(false);
                      void handlePrintJournal();
                    }}
                    disabled={moneyOperationsPrinting}
                    className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60">
                    <Printer className={`h-4 w-4 ${moneyOperationsPrinting ? 'animate-pulse' : ''}`} />
                    {moneyOperationsPrinting ? 'Подготовка печати...' : 'Печать'}
                  </button>
                </div>
              )}
            </div>
          ) : null}
        </div>
      </div>

      {reconciliationNotifications.length > 0 && (
        <div className="mt-6 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3">
          <div className="mb-3 flex items-center gap-2 text-sm font-semibold text-slate-800">
            <AlertTriangle className="h-4 w-4 text-amber-600" />
            Уведомления сверки
          </div>
          <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
            {reconciliationNotifications.map((notification) => (
              <div
                key={notification.key}
                className={`rounded-lg border bg-white px-3 py-2 ${
                  notification.tone === 'rose' ? 'border-rose-200' : 'border-amber-200'
                }`}>
                <div className={notification.tone === 'rose' ? 'text-sm font-semibold text-rose-700' : 'text-sm font-semibold text-amber-700'}>
                  {notification.title}
                </div>
                <div className="mt-1 text-xs text-slate-600">{notification.description}</div>
              </div>
            ))}
          </div>
        </div>
      )}

      {visibleDailyBalances.length > 0 && (
        <div className="mt-6 rounded-xl border border-slate-200 bg-white">
          <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 px-4 py-3">
            <div>
              <div className="text-sm font-semibold text-slate-800">
                {canViewFullMoneyBalance ? 'Дневная сверка остатков' : 'Дневное движение доступных операций'}
              </div>
              <div className="mt-1 text-xs text-slate-500">
                {canViewFullMoneyBalance
                  ? 'Начало дня + приход - расход = конец дня'
                  : 'Общий остаток и сохранённая сверка доступны только при полном доступе к платежам.'}
                {latestBalanceReconciliation && (
                  <span className="ml-2 text-slate-400">
                    · сохранено {formatDate(latestBalanceReconciliation.checkedAt, profileTimeZoneId)}
                  </span>
                )}
              </div>
            </div>
            <div className="flex flex-wrap items-center justify-end gap-3">
              <div className="text-xs text-slate-500">
                {accountName || SOURCE_LABELS[source]} · {formatDate(from)} - {formatDate(to)}
              </div>
              {canViewFullMoneyBalance && (
                <button
                  type="button"
                  onClick={() => void handleRunBalanceReconciliation()}
                  disabled={balanceReconciliationRunning}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60">
                  <CheckCircle2 className={`h-4 w-4 ${balanceReconciliationRunning ? 'animate-pulse' : ''}`} />
                  {balanceReconciliationRunning ? 'Сверка...' : 'Запустить сверку'}
                </button>
              )}
            </div>
          </div>
          <div className="overflow-x-auto">
            <table className="w-full min-w-[960px] text-sm">
              <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                <tr>
                  <th className="px-4 py-2 text-left font-medium">День</th>
                  <th className="px-4 py-2 text-left font-medium">Счет</th>
                  <th className="px-4 py-2 text-right font-medium">Начало</th>
                  <th className="px-4 py-2 text-right font-medium">Приход</th>
                  <th className="px-4 py-2 text-right font-medium">Расход</th>
                  <th className="px-4 py-2 text-right font-medium">Конец</th>
                  <th className="px-4 py-2 text-right font-medium">{canViewFullMoneyBalance ? 'Сверка' : 'Расчёт'}</th>
                  <th className="px-4 py-2 text-right font-medium">Сохранено</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {visibleDailyBalances.slice(0, 12).map((item) => {
                  const savedResult = balanceReconciliationByKey.get(
                    `${item.source}-${item.accountName}-${toDateInputValue(item.date)}`,
                  );

                  return (
                    <tr key={`${item.source}-${item.accountName}-${item.date}`} className="hover:bg-slate-50">
                      <td className="px-4 py-2 text-slate-700">{formatDate(item.date)}</td>
                      <td className="px-4 py-2 text-slate-700">{item.accountName}</td>
                      <td className="px-4 py-2 text-right text-amber-600">{formatMoney(item.openingBalance)} ₽</td>
                      <td className="px-4 py-2 text-right text-emerald-600">
                        {formatMoney(item.income)} ₽
                        <div className="text-[11px] text-slate-400">{item.incomeCount}</div>
                      </td>
                      <td className="px-4 py-2 text-right text-rose-600">
                        {formatMoney(item.expense)} ₽
                        <div className="text-[11px] text-slate-400">{item.expenseCount}</div>
                      </td>
                      <td className="px-4 py-2 text-right font-medium text-amber-600">{formatMoney(item.closingBalance)} ₽</td>
                      <td className="px-4 py-2 text-right">
                        <span
                          className={`inline-flex rounded-full px-2 py-1 text-xs font-medium ${
                            item.isBalanced ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'
                          }`}>
                          {canViewFullMoneyBalance
                            ? item.isBalanced ? 'Сходится' : 'Расхождение'
                            : 'По доступным операциям'}
                        </span>
                      </td>
                      <td className="px-4 py-2 text-right">
                        {canViewFullMoneyBalance
                          ? renderSavedBalanceReconciliationBadge(item, savedResult, profileTimeZoneId)
                          : '—'}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {visibleDailyBalances.length > 12 && (
            <div className="border-t border-slate-100 px-4 py-2 text-xs text-slate-500">
              Показаны первые 12 дней из {visibleDailyBalances.length}
            </div>
          )}
        </div>
      )}

      <div className="mt-6 rounded-xl border border-slate-200 bg-white">
        <div className="flex flex-wrap items-center justify-between gap-4 border-b border-slate-200 px-4 py-3">
          <div className="flex items-center gap-2">
            <button type="button" className="rounded-lg bg-blue-600 p-2 text-white" aria-label="Журнал денег">
              <WalletCards className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              aria-label={settingsOpen ? 'Скрыть настройки таблицы' : 'Показать настройки таблицы'}
              className={`rounded-lg border p-2 transition-colors ${
                settingsOpen
                  ? 'border-slate-900 bg-slate-900 text-white'
                  : 'border-slate-200 text-slate-500 hover:bg-slate-50'
              }`}>
              <SlidersHorizontal className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => setShowAdvancedSearch((prev) => !prev)}
              aria-label={showAdvancedSearch ? 'Скрыть расширенный поиск' : 'Показать расширенный поиск'}
              className={`rounded-lg border p-2 transition-colors ${
                showAdvancedSearch
                  ? 'border-slate-900 bg-slate-900 text-white'
                  : 'border-slate-200 text-slate-500 hover:bg-slate-50'
              }`}>
              <Search className="h-4 w-4" />
            </button>
          </div>

          <div className="flex flex-1 flex-wrap items-center justify-end gap-3">
            <div className="relative">
              <button
                type="button"
                onClick={() => setShowFilters((prev) => !prev)}
                className="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 hover:bg-slate-50">
                <Filter className="h-4 w-4" />
                Фильтр
                <ChevronDown className="h-4 w-4" />
              </button>
              {showFilters && (
                <div className="absolute right-0 top-12 z-10 w-80 rounded-xl border border-slate-200 bg-white p-4 shadow-xl">
                  <div className="grid gap-3 text-sm text-slate-600">
                    <label className="flex flex-col gap-1">
                      {SOURCE_ACCOUNT_FILTER_LABELS[source]}
                      <SystemSelect
                        value={moneyAccountId === '' ? accountName : String(moneyAccountId)}
                        onChange={(event) => {
                          selectMoneyAccount(event.target.value);
                        }}
                        className="rounded-lg border border-slate-200 px-3 py-2">
                        <option value="">{SOURCE_ALL_ACCOUNTS_LABELS[source]}</option>
                        {moneyAccountOptions.map((account) => (
                          <option
                            key={`${account.source}-${account.accountName}-${account.id ?? ''}`}
                            value={account.id ?? account.accountName}>
                            {account.accountName}
                          </option>
                        ))}
                      </SystemSelect>
                    </label>
                    {selectedAccountSummary && (
                      <div className="rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 text-xs text-slate-600">
                        <div className="flex items-center justify-between gap-2">
                          <div className="font-medium text-slate-800">
                            Остаток: {formatMoney(selectedAccountSummary.endBalance)} ₽
                          </div>
                          {renderBalanceCheckBadge(selectedAccountSummary)}
                        </div>
                        <div className="mt-1 flex items-center justify-between gap-2">
                          <span className="text-emerald-600">Приход: {formatMoney(selectedAccountSummary.income)} ₽</span>
                          <span className="text-rose-600">Расход: {formatMoney(selectedAccountSummary.expense)} ₽</span>
                        </div>
                      </div>
                    )}
                    <label className="flex flex-col gap-1">
                      Приход/Расход
                      <SystemSelect
                        value={directionFilter}
                        onChange={(event) => setDirectionFilter(event.target.value as 'all' | MoneyOperationDirection)}
                        className="rounded-lg border border-slate-200 px-3 py-2">
                        <option value="all">Все</option>
                        <option value="Income">Приход</option>
                        <option value="Expense">Расход</option>
                      </SystemSelect>
                    </label>
                    <label className="flex flex-col gap-1">
                      Учет
                      <SystemSelect
                        value={accountingTypeFilter}
                        onChange={(event) => setAccountingTypeFilter(event.target.value as 'all' | MoneyOperationAccountingType)}
                        className="rounded-lg border border-slate-200 px-3 py-2">
                        <option value="all">Факт и план</option>
                        <option value="Fact">Факт</option>
                        <option value="Plan">План</option>
                      </SystemSelect>
                    </label>
                    <label className="flex flex-col gap-1">
                      Статья
                      <input
                        value={categoryName}
                        onChange={(event) => setCategoryName(event.target.value)}
                        list="money-operation-category-filter-options"
                        className="rounded-lg border border-slate-200 px-3 py-2"
                        placeholder="Например: услуги"
                      />
                      <datalist id="money-operation-category-filter-options">
                        {categoryOptions.map((option) => (
                          <option key={option} value={option} />
                        ))}
                      </datalist>
                    </label>
                    <div className="flex items-center justify-between pt-2">
                      <button
                        type="button"
                        onClick={() => {
                          setMoneyAccountId('');
                          setAccountName('');
                          setCategoryName('');
                          setDirectionFilter('all');
                          setAccountingTypeFilter('all');
                          setShowFilters(false);
                        }}
                        className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 hover:bg-slate-50">
                        Сбросить
                      </button>
                      <button
                        type="button"
                        onClick={() => setShowFilters(false)}
                        className="rounded-lg bg-blue-600 px-3 py-2 text-sm text-white hover:bg-blue-700">
                        Применить
                      </button>
                    </div>
                  </div>
                </div>
              )}
            </div>

            <input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              onKeyDown={handleSearchKeyDown}
              placeholder="Поиск по контрагенту..."
              className="min-w-[220px] rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-600 focus:border-blue-500 focus:outline-none"
            />
            <button
              type="button"
              onClick={() => setShowAdvancedSearch((prev) => !prev)}
              className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50"
              title="Расширенный поиск">
              <SlidersHorizontal className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={applySearch}
              className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700">
              Найти
            </button>
          </div>
        </div>

        {activeMoneyFilters.length > 0 && (
          <div className="flex flex-wrap items-center gap-2 border-b border-slate-200 bg-slate-50 px-4 py-3">
            <span className="text-xs font-medium uppercase tracking-wide text-slate-400">Активные фильтры</span>
            {activeMoneyFilters.map((filter) => (
              <button
                key={filter.key}
                type="button"
                onClick={filter.onClear}
                aria-label={`Сбросить фильтр ${filter.value}`}
                className="inline-flex items-center gap-2 rounded-full border border-blue-100 bg-white px-3 py-1.5 text-sm text-slate-700 shadow-sm transition hover:border-blue-200 hover:bg-blue-50">
                <span className="text-slate-400">{filter.label}:</span>
                <span className="font-medium">{filter.value}</span>
                <X className="h-3.5 w-3.5 text-slate-400" />
              </button>
            ))}
          </div>
        )}

        {showAdvancedSearch && (
          <div className="border-b border-slate-200 bg-slate-50 px-4 py-4">
            <div className="grid gap-4 md:grid-cols-4">
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Контрагент
                <input className="rounded-lg border border-slate-200 px-3 py-2" placeholder="Название или ИНН" />
              </label>
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Назначение
                <input className="rounded-lg border border-slate-200 px-3 py-2" placeholder="Поиск по словам" />
              </label>
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Сумма
                <SystemSelect className="rounded-lg border border-slate-200 px-3 py-2">
                  <option>Равно</option>
                  <option>Больше</option>
                  <option>Меньше</option>
                </SystemSelect>
              </label>
              <label className="flex flex-col gap-1 text-sm text-slate-600">
                Поиск
                <SystemSelect className="rounded-lg border border-slate-200 px-3 py-2">
                  <option>Приход и Расход</option>
                  <option>Приход</option>
                  <option>Расход</option>
                </SystemSelect>
              </label>
            </div>
          </div>
        )}

        {settingsOpen && (
          <div className="border-b border-slate-200 bg-slate-50 px-4 py-4">
            <TableColumnSettings
              columns={MONEY_OPERATION_COLUMNS.map((column) => ({
                key: column.key,
                label: column.label,
              }))}
              columnOrder={columnOrder}
              visibleColumns={visibleColumns}
              draggingColumn={draggingColumn}
              onToggleColumn={toggleColumn}
              onMoveColumn={moveColumn}
              onDragStart={handleColumnDragStart}
              onDragOver={handleColumnDragOver}
              onDrop={handleColumnDrop}
              onDragEnd={handleColumnDragEnd}
              onReset={resetTableSettings}
              footer={
                <div className="flex items-center gap-2 text-sm text-slate-600">
                  <span>Строк на странице</span>
                  <SystemSelect
                    value={pageSize}
                    onChange={(event) => setPageSize(Number(event.target.value) || pageSize)}
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700">
                    {PAGE_SIZE_OPTIONS.map((size) => (
                      <option key={size} value={size}>
                        {size}
                      </option>
                    ))}
                  </SystemSelect>
                </div>
              }
            />
          </div>
        )}

        {selectionEnabled && selectedOperationsCount > 0 && (
          <div className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 bg-blue-50 px-4 py-3">
            <div className="text-sm font-medium text-blue-900">
              Выбрано операций: {selectedOperationsCount}
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => setSelectedOperationIds([])}
                className="rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50">
                Снять выбор
              </button>
              <button
                type="button"
                onClick={() => setConfirmBulkDeleteOpen(true)}
                disabled={!canDeleteMoneyOperations}
                className="inline-flex items-center gap-2 rounded-lg border border-red-200 bg-white px-3 py-2 text-sm font-medium text-red-600 hover:bg-red-50">
                <Trash2 className="h-4 w-4" />
                Удалить выбранные
              </button>
            </div>
          </div>
        )}

        <div className="overflow-x-auto">
          <table
            className="w-full min-w-[1500px] table-fixed text-sm"
            style={{ width: getTableWidth((selectionEnabled ? 48 : 0) + 140, 1500), minWidth: '100%' }}
          >
            <colgroup>
              {selectionEnabled && <col style={{ width: 48 }} />}
              {orderedVisibleColumns.map((columnKey) => (
                <col key={columnKey} style={{ width: getColumnWidth(columnKey) }} />
              ))}
              <col style={{ width: 140 }} />
            </colgroup>
            <thead className="bg-slate-50 text-slate-500">
              <tr>
                {selectionEnabled && (
                  <th className="px-3 py-2 text-left font-medium">
                    <TableSelectCheckbox
                      aria-label="Выбрать операции на странице"
                      checked={allVisibleOperationsSelected}
                      indeterminate={someVisibleOperationsSelected}
                      disabled={selectableOperationIds.length === 0 || !canDeleteMoneyOperations}
                      onChange={(event) => toggleVisibleOperationsSelection(event.target.checked)}
                    />
                  </th>
                )}
                {visibleMoneyColumns.map((column) => (
                  <th
                    key={column.key}
                    style={{ width: getColumnWidth(column.key) }}
                    className={`relative px-3 py-2 text-left font-medium ${
                      draggingColumn === column.key ? 'bg-blue-50 text-blue-700' : ''
                    }`}
                    onDragOver={handleColumnDragOver}
                    onDrop={(event) => handleColumnDrop(column.key, event)}>
                    <button
                      type="button"
                      className="inline-flex min-w-0 items-center gap-2 rounded p-1 text-left text-slate-700 transition-colors cursor-grab active:cursor-grabbing"
                      draggable
                      onDragStart={(event) => handleColumnDragStart(column.key, event)}
                      onDragEnd={handleColumnDragEnd}
                      title="Перетащите, чтобы изменить порядок">
                      <GripVertical className="h-4 w-4 shrink-0 text-slate-400" />
                      <span className="truncate">{column.label}</span>
                    </button>
                    <div
                      role="separator"
                      aria-label={`Изменить ширину колонки ${column.label}`}
                      className="absolute right-0 top-0 z-10 h-full w-3 cursor-col-resize"
                      onMouseDown={(event) => handleResizeStart(column.key, event)}
                      onDragStart={(event) => event.preventDefault()}
                    />
                  </th>
                ))}
                <th className="px-3 py-2 text-left font-medium">Действия</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {loading ? (
                <tr>
                  <td colSpan={moneyTableColumnCount} className="px-4 py-10 text-center text-slate-500">
                    Загрузка операций...
                  </td>
                </tr>
              ) : error ? (
                <tr>
                  <td colSpan={moneyTableColumnCount} className="px-4 py-10 text-center text-red-500">
                    {error}
                  </td>
                </tr>
              ) : items.length === 0 ? (
                <tr>
                  <td colSpan={moneyTableColumnCount} className="px-4 py-10 text-center text-slate-500">
                    В указанном периоде нет операций. Добавить операцию или импортировать из банка
                  </td>
                </tr>
              ) : (
                items.map((item) => (
                  <tr key={item.id} className="hover:bg-slate-50">
                    {selectionEnabled && (
                      <td className="px-3 py-2 align-top">
                        <TableSelectCheckbox
                          aria-label={`Выбрать операцию ${item.id}`}
                          checked={selectedOperationIdSet.has(item.id)}
                          disabled={!canDeleteMoneyOperations || item.isReadonly || item.id <= 0 || isAppliedToInvoice(item)}
                          onChange={(event) => toggleOperationSelection(item.id, event.target.checked)}
                        />
                      </td>
                    )}
                    {visibleMoneyColumns.map((column) => (
                      <td
                        key={column.key}
                        style={{ width: getColumnWidth(column.key) }}
                        className="max-w-0 overflow-hidden px-3 py-2 align-top">
                        {renderCell(item, column.key)}
                      </td>
                    ))}
                    <td className="px-3 py-2">
                      <div className="flex items-center gap-2">
                        {(() => {
                          const badge = getOperationOriginBadge(item);
                          return badge ? (
                            <span
                              className={`rounded-full px-2 py-1 text-xs font-medium ${badge.className}`}
                              title={badge.title}>
                              {badge.label}
                            </span>
                          ) : null;
                        })()}
                        {item.canCreateCashOrder && canCreateMoneyOperations && (
                          <button
                            type="button"
                            onClick={() => handleCreateCashOrder(item)}
                            className="rounded-md border border-amber-200 p-2 text-amber-700 hover:bg-amber-50"
                            aria-label="Создать приходный кассовый ордер"
                            title="Создать приходный кассовый ордер">
                            <ReceiptText className="h-4 w-4" />
                          </button>
                        )}
                        {item.cashOrderId && (
                          <button
                            type="button"
                            onClick={() => void handleOpenCashOrder(item.cashOrderId!)}
                            className="rounded-md border border-emerald-200 p-2 text-emerald-700 hover:bg-emerald-50"
                            aria-label="Открыть карточку кассового ордера"
                            title="Открыть карточку кассового ордера">
                            <ReceiptText className="h-4 w-4" />
                          </button>
                        )}
                        {item.matchStatus === 'ClarificationRequired' && (
                          <button
                            type="button"
                            onClick={() => handleEdit(item)}
                            disabled={Boolean(item.isReadonly) || item.id <= 0 || !canEditMoneyOperations}
                            className="rounded-md border border-amber-200 p-2 text-amber-700 hover:bg-amber-50 disabled:cursor-not-allowed disabled:opacity-40"
                            aria-label="Уточнить"
                            title="Уточнить операцию и привязать счёт">
                            <AlertTriangle className="h-4 w-4" />
                          </button>
                        )}
                        <button
                          type="button"
                          onClick={() => handleEdit(item)}
                          disabled={Boolean(item.isReadonly) || item.id <= 0 || !canEditMoneyOperations}
                          className="rounded-md border border-slate-200 p-2 text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                          aria-label="Изменить">
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          onClick={() => handleDelete(item)}
                          disabled={Boolean(item.isReadonly) || item.id <= 0 || isAppliedToInvoice(item) || !canDeleteMoneyOperations}
                          className="rounded-md border border-red-200 p-2 text-red-600 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                          aria-label="Удалить"
                          title={isAppliedToInvoice(item) ? 'Операция уже зачтена в счёте' : 'Удалить'}>
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      <TablePagination
        page={page}
        totalPages={totalPages}
        totalItems={total}
        pageSize={pageSize}
        pageSizeOptions={PAGE_SIZE_OPTIONS}
        onPageChange={setPage}
        onPageSizeChange={setPageSize}
        recordsLabel="Найдено записей"
        pageSizeLabel="Строк на странице"
        previousLabel="Назад"
        nextLabel="Вперед"
      />

      {source === 'Cash' && (
        <CashOrdersPanel
          from={from}
          to={to}
          search={effectiveSearch}
          accountName={accountName}
          refreshKey={cashOrdersRefreshKey}
          onCreate={handleCreateStandaloneCashOrder}
          onOpen={(cashOrderId) => void handleOpenCashOrder(cashOrderId)}
          onPrint={handlePrintCashOrder}
          canCreate={canCreateMoneyOperations}
          canExport={canExportJournal}
        />
      )}

      <CashOrderModal
        order={selectedCashOrder}
        open={cashOrderModalOpen}
        timeZoneId={profileTimeZoneId}
        isSaving={cashOrderSaving}
        onClose={() => {
          setCashOrderModalOpen(false);
          setSelectedCashOrder(null);
        }}
        onSave={handleSaveCashOrder}
        onPostOrder={handlePostCashOrder}
        onCancelOrder={handleCancelCashOrder}
        onDeleteOrder={handleDeleteCashOrder}
        onCopy={handleCopyCashOrder}
        onDownloadPdf={handleDownloadCashOrderPdf}
        onDownloadWord={handleDownloadCashOrderWord}
        onPrint={handlePrintCashOrder}
        canCreate={canCreateMoneyOperations}
        canEdit={canEditMoneyOperations}
        canDelete={canDeleteMoneyOperations}
        canViewAudit={canViewAudit}
      />

      {selectedBankImport &&
        createPortal(
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 px-4 py-6">
            <div className="max-h-[90vh] w-full max-w-5xl overflow-hidden rounded-2xl bg-white shadow-2xl">
              <div className="flex flex-wrap items-start justify-between gap-4 border-b border-slate-200 px-6 py-4">
                <div>
                  <p className="text-xs font-medium uppercase tracking-wide text-slate-400">Импорт банковской выписки</p>
                  <h2 className="text-lg font-semibold text-slate-900">{selectedBankImport.fileName}</h2>
                  <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-slate-500">
                    <span>ID {selectedBankImport.id}</span>
                    <span>•</span>
                    <span>{formatDate(selectedBankImport.createdAt, profileTimeZoneId)}</span>
                    <span
                      className={`inline-flex rounded-full px-2 py-0.5 font-medium ${getBankImportStatusClassName(selectedBankImport.status)}`}>
                      {getBankImportStatusLabel(selectedBankImport.status)}
                    </span>
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={() => void handleReprocessBankImport(selectedBankImport.id)}
                    aria-label={`Повторить обработку импорта выписки ${selectedBankImport.fileName || `#${selectedBankImport.id}`}`}
                    className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
                    disabled={
                      bankImportReprocessLoading ||
                      !canCreateMoneyOperations ||
                      !selectedBankImport.rows.some((row) => ['Skipped', 'Error', 'RolledBack'].includes(row.status))
                    }>
                    <RefreshCw className="h-4 w-4" />
                    Повторить
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleRollbackBankImport(selectedBankImport.id)}
                    aria-label={`Откатить импорт выписки ${selectedBankImport.fileName || `#${selectedBankImport.id}`} из карточки`}
                    className="inline-flex items-center gap-2 rounded-lg border border-amber-200 px-3 py-2 text-sm font-medium text-amber-700 hover:bg-amber-50 disabled:cursor-not-allowed disabled:opacity-60"
                    disabled={bankImportRollbackLoading || selectedBankImport.status === 'RolledBack' || !canEditMoneyOperations}>
                    <RotateCcw className="h-4 w-4" />
                    Откатить
                  </button>
                  <button
                    type="button"
                    onClick={() => setSelectedBankImport(null)}
                    className="rounded-lg border border-slate-200 p-2 text-slate-500 hover:bg-slate-50"
                    aria-label="Закрыть импорт">
                    <X className="h-4 w-4" />
                  </button>
                </div>
              </div>

              <div className="grid gap-3 border-b border-slate-100 bg-slate-50 px-6 py-4 sm:grid-cols-4">
                <div className="rounded-xl bg-white p-3">
                  <div className="text-xs text-slate-400">Прочитано строк</div>
                  <div className="text-lg font-semibold text-slate-900">{selectedBankImport.rowsRead}</div>
                </div>
                <div className="rounded-xl bg-white p-3">
                  <div className="text-xs text-slate-400">Создано</div>
                  <div className="text-lg font-semibold text-emerald-600">{selectedBankImport.createdRows}</div>
                </div>
                <div className="rounded-xl bg-white p-3">
                  <div className="text-xs text-slate-400">Пропущено</div>
                  <div className="text-lg font-semibold text-amber-600">{selectedBankImport.skippedRows}</div>
                </div>
                <div className="rounded-xl bg-white p-3">
                  <div className="text-xs text-slate-400">Ошибки</div>
                  <div className="text-lg font-semibold text-red-600">{selectedBankImport.errorRows}</div>
                </div>
              </div>

              <div className="max-h-[52vh] overflow-auto px-6 py-4">
                <table className="w-full min-w-[920px] text-sm">
                  <thead className="sticky top-0 bg-white text-xs uppercase text-slate-400">
                    <tr>
                      <th className="px-3 py-2 text-left font-medium">Строка</th>
                      <th className="px-3 py-2 text-left font-medium">Статус</th>
                      <th className="px-3 py-2 text-left font-medium">Дата</th>
                      <th className="px-3 py-2 text-left font-medium">Сумма</th>
                      <th className="px-3 py-2 text-left font-medium">Контрагент</th>
                      <th className="px-3 py-2 text-left font-medium">Назначение</th>
                      <th className="px-3 py-2 text-left font-medium">Комментарий</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {selectedBankImport.rows.map((row) => (
                      <tr key={row.id} className="hover:bg-slate-50">
                        <td className="px-3 py-2 text-slate-500">{row.rowNumber}</td>
                        <td className="px-3 py-2">
                          <span className={`inline-flex rounded-full px-2 py-1 text-xs font-medium ${getBankImportStatusClassName(row.status)}`}>
                            {getBankImportStatusLabel(row.status)}
                          </span>
                        </td>
                        <td className="px-3 py-2">{formatDate(row.date)}</td>
                        <td className="px-3 py-2">{formatMoney(row.amount)} ₽</td>
                        <td className="px-3 py-2">{row.counterpartyName || '—'}</td>
                        <td className="px-3 py-2">
                          <div className="line-clamp-2" title={row.purpose ?? undefined}>
                            {row.purpose || '—'}
                          </div>
                        </td>
                        <td className="px-3 py-2 text-slate-500">{row.message || '—'}</td>
                      </tr>
                    ))}
                    {selectedBankImport.rows.length === 0 && (
                      <tr>
                        <td colSpan={7} className="px-3 py-8 text-center text-slate-500">
                          Строки импорта не найдены.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            </div>
          </div>,
          document.body,
        )}

      <MoneyOperationModal
        source={apiSource ?? 'Bank'}
        initialData={editingOperation}
        moneyAccounts={moneyAccounts}
        responsibles={responsibles}
        categoryOptions={categoryDictionaryOptions}
        open={modalOpen}
        onClose={() => {
          setModalOpen(false);
          setEditingOperation(null);
        }}
        onSave={handleSave}
        isSaving={saving}
      />
      <ConfirmDialog
        open={Boolean(confirmDelete)}
        title="Удалить операцию?"
        description="Действие необратимо. Вы уверены, что хотите удалить операцию?"
        confirmLabel="Удалить"
        cancelLabel="Отмена"
        variant="danger"
        onConfirm={confirmDeleteOperation}
        onCancel={() => setConfirmDelete(null)}
      />
      <ConfirmDialog
        open={confirmBulkDeleteOpen}
        title="Удалить выбранные операции?"
        description={`Будет удалено операций: ${selectedOperationsCount}. Действие необратимо.`}
        confirmLabel="Удалить выбранные"
        cancelLabel="Отмена"
        variant="danger"
        onConfirm={confirmBulkDeleteOperations}
        onCancel={() => setConfirmBulkDeleteOpen(false)}
      />
      <ConfirmDialog
        open={Boolean(pendingUnallocatedAdvanceOperation)}
        title="Нераспределенный аванс"
        description="Банковское поступление не связано со счетом. Если сохранить его сейчас, оно будет отмечено как аванс без счета и останется в денежном журнале на проверке до привязки или разборки."
        confirmLabel="Отметить как аванс"
        cancelLabel="Вернуться к операции"
        variant="primary"
        onConfirm={confirmUnallocatedAdvanceOperation}
        onCancel={() => setPendingUnallocatedAdvanceOperation(null)}
      />
        </>
      )}
    </div>
  );
}
