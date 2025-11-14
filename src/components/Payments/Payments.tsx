import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, Lock, Plus, RotateCcw, Search, SlidersHorizontal } from 'lucide-react';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useTranslation } from '../../hooks/useTranslation';
import { usePayments } from '../../hooks/usePayments';
import type { Payment, PaymentPayload, PaymentStatus } from '../../types';
import { PaymentsTable } from '../Calendar/PaymentsTable';
import { PaymentModal } from '../Calendar/PaymentModal';
import { formatCurrencySmart } from '../../utils/formatters';

type TypeFilter = 'all' | Payment['type'];

type StatusFilter = 'all' | PaymentStatus;

interface PaymentsProps {
  onOpenClient?: (clientId: number, caseId?: number) => void;
  defaultType?: TypeFilter;
  lockType?: boolean;
  titleKey?: string;
  subtitleKey?: string;
  lockedTypeMessageKey?: string;
}

type SubmitDTO = PaymentPayload | ({ id: number } & PaymentPayload);

const STATUS_ORDER: PaymentStatus[] = ['Pending', 'Processing', 'Overdue', 'Completed', 'Cancelled'];

type CategoryFilterKey = `dealType:${number}` | `incomeType:${number}` | `source:${number}`;

function formatDateInput(date: Date) {
  const year = date.getFullYear();
  const month = `${date.getMonth() + 1}`.padStart(2, '0');
  const day = `${date.getDate()}`.padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function getCurrentMonthRange() {
  const now = new Date();
  const start = new Date(now.getFullYear(), now.getMonth(), 1);
  const end = new Date(now.getFullYear(), now.getMonth() + 1, 0);
  return { from: formatDateInput(start), to: formatDateInput(end) };
}

function formatCountLabel(template: string, count: number) {
  return template.replace('{{count}}', count.toLocaleString('ru-RU'));
}

export function Payments({
  onOpenClient,
  defaultType,
  lockType = false,
  titleKey,
  subtitleKey,
  lockedTypeMessageKey,
}: PaymentsProps) {
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const paymentsPermissions = permissions.payments;
  const { t } = useTranslation();

  const initialRangeRef = useRef(getCurrentMonthRange());
  const [dateFrom, setDateFrom] = useState<string>(initialRangeRef.current.from);
  const [dateTo, setDateTo] = useState<string>(initialRangeRef.current.to);

  const {
    payments,
    loading,
    refreshing,
    refresh,
    createPayment,
    updatePayment,
    deletePayment,
    error,
  } = usePayments(dateFrom || undefined, dateTo || undefined);

  const [typeFilter, setTypeFilter] = useState<TypeFilter>(defaultType ?? 'all');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [search, setSearch] = useState('');
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [selectedClientIds, setSelectedClientIds] = useState<number[]>([]);
  const [selectedCategoryKeys, setSelectedCategoryKeys] = useState<CategoryFilterKey[]>([]);
  const [minAmount, setMinAmount] = useState('');
  const [maxAmount, setMaxAmount] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [modalType, setModalType] = useState<Payment['type']>(
    defaultType === 'Expense' ? 'Expense' : 'Income',
  );
  const [editingPayment, setEditingPayment] = useState<Payment | null>(null);
  const [permissionMessage, setPermissionMessage] = useState<string | null>(null);
  const permissionTimerRef = useRef<number | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);
  const [loadTimeoutReached, setLoadTimeoutReached] = useState(false);

  useEffect(() => {
    if (typeof defaultType === 'undefined') {
      return;
    }
    setTypeFilter(defaultType);
    if (defaultType === 'Income' || defaultType === 'Expense') {
      setModalType(defaultType);
    }
  }, [defaultType]);

  useEffect(() => {
    setLastUpdated(new Date());
  }, [payments]);

  useEffect(() => {
    if (loading && payments.length === 0) {
      setLoadTimeoutReached(false);
      const timer = window.setTimeout(() => {
        setLoadTimeoutReached(true);
      }, 15000);
      return () => {
        window.clearTimeout(timer);
      };
    }
    setLoadTimeoutReached(false);
    return undefined;
  }, [loading, payments.length]);

  useEffect(() => () => {
    if (permissionTimerRef.current) {
      window.clearTimeout(permissionTimerRef.current);
    }
  }, []);

  const showPermissionNotice = useCallback(
    (message: string) => {
      setPermissionMessage(message);
      if (permissionTimerRef.current) {
        window.clearTimeout(permissionTimerRef.current);
      }
      permissionTimerRef.current = window.setTimeout(() => {
        setPermissionMessage(null);
        permissionTimerRef.current = null;
      }, 4000);
    },
    [],
  );

  const statusLabels = useMemo(
    () => ({
      Completed: t('statusCompleted') ?? 'Выполнено',
      Pending: t('pending') ?? 'Ожидается',
      Overdue: t('overdue') ?? 'Просрочено',
      Processing: t('processingStatus') ?? 'В обработке',
      Cancelled: t('cancelledStatus') ?? 'Отменено',
    }),
    [t],
  );

  const availableStatuses = useMemo(() => {
    const uniq = new Set<PaymentStatus>();
    payments.forEach((payment) => {
      uniq.add(payment.status);
    });
    const ordered = Array.from(uniq);
    ordered.sort((a, b) => STATUS_ORDER.indexOf(a) - STATUS_ORDER.indexOf(b));
    return ordered;
  }, [payments]);

  const effectiveDefaultType: TypeFilter = defaultType ?? 'all';
  const headingText =
    (titleKey ? t(titleKey) : t('payments')) ?? t('payments') ?? 'Платежи';
  const subtitleText =
    subtitleKey
      ? t(subtitleKey) ?? t('paymentsSubtitle') ?? 'Журнал всех платежей'
      : t('paymentsSubtitle') ?? 'Журнал всех платежей';
  const lockedTypeMessage = lockType
    ? (lockedTypeMessageKey ? t(lockedTypeMessageKey) : undefined) ??
      (effectiveDefaultType === 'Income'
        ? t('paymentsTypeLockedIncome') ?? 'Отображаются только доходные платежи'
        : effectiveDefaultType === 'Expense'
        ? t('paymentsTypeLockedExpense') ?? 'Отображаются только расходные платежи'
        : t('paymentsTypeLockedGeneric') ?? 'Фильтр по типу закреплён')
    : null;

  const searchQuery = search.trim().toLowerCase();

  const defaultDateFrom = initialRangeRef.current.from;
  const defaultDateTo = initialRangeRef.current.to;

  const minAmountNumber = useMemo(() => {
    if (!minAmount) return null;
    const parsed = Number(minAmount.replace(',', '.'));
    return Number.isFinite(parsed) ? parsed : null;
  }, [minAmount]);

  const maxAmountNumber = useMemo(() => {
    if (!maxAmount) return null;
    const parsed = Number(maxAmount.replace(',', '.'));
    return Number.isFinite(parsed) ? parsed : null;
  }, [maxAmount]);

  const clientOptions = useMemo(() => {
    const map = new Map<number, string>();
    payments.forEach((payment) => {
      if (payment.clientId && !map.has(payment.clientId)) {
        map.set(payment.clientId, payment.client?.name || `${t('paymentsClientFallback') ?? 'Клиент'} #${payment.clientId}`);
      }
    });
    return Array.from(map.entries()).map(([id, name]) => ({ id, name }));
  }, [payments, t]);

  const dealTypeLabel = t('paymentsCategoryDealType') ?? 'Тип сделки';
  const incomeTypeLabel = t('paymentsCategoryIncomeType') ?? 'Тип дохода';
  const sourceLabel = t('paymentsCategorySource') ?? 'Источник';

  const categoryOptions = useMemo(() => {
    const map = new Map<CategoryFilterKey, string>();
    payments.forEach((payment) => {
      if (payment.dealTypeId && payment.dealType?.name) {
        map.set(`dealType:${payment.dealTypeId}`, `${dealTypeLabel}: ${payment.dealType.name}`);
      }
      if (payment.incomeTypeId && payment.incomeType?.name) {
        map.set(`incomeType:${payment.incomeTypeId}`, `${incomeTypeLabel}: ${payment.incomeType.name}`);
      }
      if (payment.paymentSourceId && payment.paymentSource?.name) {
        map.set(`source:${payment.paymentSourceId}`, `${sourceLabel}: ${payment.paymentSource.name}`);
      }
    });
    return Array.from(map.entries()).map(([value, label]) => ({ value, label }));
  }, [payments, dealTypeLabel, incomeTypeLabel, sourceLabel]);

  const filtersActive = useMemo(() => {
    return (
      selectedClientIds.length > 0 ||
      selectedCategoryKeys.length > 0 ||
      minAmountNumber !== null ||
      maxAmountNumber !== null ||
      (dateFrom && dateFrom !== defaultDateFrom) ||
      (dateTo && dateTo !== defaultDateTo)
    );
  }, [
    dateFrom,
    dateTo,
    defaultDateFrom,
    defaultDateTo,
    maxAmountNumber,
    minAmountNumber,
    selectedCategoryKeys.length,
    selectedClientIds.length,
  ]);

  const activeFiltersCount = useMemo(() => {
    let count = 0;
    if (selectedClientIds.length > 0) count += 1;
    if (selectedCategoryKeys.length > 0) count += 1;
    if (minAmountNumber !== null || maxAmountNumber !== null) count += 1;
    if ((dateFrom && dateFrom !== defaultDateFrom) || (dateTo && dateTo !== defaultDateTo)) count += 1;
    return count;
  }, [
    dateFrom,
    dateTo,
    defaultDateFrom,
    defaultDateTo,
    maxAmountNumber,
    minAmountNumber,
    selectedCategoryKeys.length,
    selectedClientIds.length,
  ]);

  const filteredPayments = useMemo(() => {
    return payments.filter((payment) => {
      const matchesType = typeFilter === 'all' || payment.type === typeFilter;
      const matchesStatus = statusFilter === 'all' || payment.status === statusFilter;
      if (!matchesType || !matchesStatus) {
        return false;
      }

      const matchesClient =
        selectedClientIds.length === 0 ||
        (typeof payment.clientId === 'number' && selectedClientIds.includes(payment.clientId));
      if (!matchesClient) {
        return false;
      }

      const paymentCategoryKeys: CategoryFilterKey[] = [];
      if (payment.dealTypeId) {
        paymentCategoryKeys.push(`dealType:${payment.dealTypeId}` as CategoryFilterKey);
      }
      if (payment.incomeTypeId) {
        paymentCategoryKeys.push(`incomeType:${payment.incomeTypeId}` as CategoryFilterKey);
      }
      if (payment.paymentSourceId) {
        paymentCategoryKeys.push(`source:${payment.paymentSourceId}` as CategoryFilterKey);
      }

      const matchesCategory =
        selectedCategoryKeys.length === 0 ||
        paymentCategoryKeys.some((key) => selectedCategoryKeys.includes(key));
      if (!matchesCategory) {
        return false;
      }

      const matchesAmount =
        (minAmountNumber === null || payment.amount >= minAmountNumber) &&
        (maxAmountNumber === null || payment.amount <= maxAmountNumber);
      if (!matchesAmount) {
        return false;
      }

      if (!searchQuery) {
        return true;
      }

      const fields: Array<string | null | undefined> = [
        payment.description,
        payment.notes,
        payment.account,
        payment.accountDate,
        payment.client?.name,
        payment.clientCase?.title,
        payment.dealType?.name,
        payment.incomeType?.name,
        payment.paymentSource?.name,
      ];
      return fields.some((field) => field?.toLowerCase().includes(searchQuery));
    });
  }, [
    maxAmountNumber,
    minAmountNumber,
    payments,
    searchQuery,
    selectedCategoryKeys,
    selectedClientIds,
    statusFilter,
    typeFilter,
  ]);

  const summary = useMemo(() => {
    let income = 0;
    let expense = 0;
    let incomeCount = 0;
    let expenseCount = 0;
    filteredPayments.forEach((payment) => {
      if (payment.type === 'Income') {
        income += payment.amount;
        incomeCount += 1;
      } else {
        expense += payment.amount;
        expenseCount += 1;
      }
    });
    return {
      income,
      expense,
      balance: income - expense,
      incomeCount,
      expenseCount,
      total: filteredPayments.length,
    };
  }, [filteredPayments]);

  const viewingIncomeOnly =
    (lockType && effectiveDefaultType === 'Income') || (!lockType && typeFilter === 'Income');
  const viewingExpenseOnly =
    (lockType && effectiveDefaultType === 'Expense') || (!lockType && typeFilter === 'Expense');
  const showIncomeSummary = !viewingExpenseOnly;
  const showExpenseSummary = !viewingIncomeOnly;
  const summaryCardCount = (showIncomeSummary ? 1 : 0) + (showExpenseSummary ? 1 : 0) + 2;
  const summaryGridClass =
    summaryCardCount >= 4
      ? 'md:grid-cols-2 xl:grid-cols-4'
      : summaryCardCount === 3
      ? 'md:grid-cols-2 xl:grid-cols-3'
      : summaryCardCount === 2
      ? 'md:grid-cols-2'
      : '';

  const handleAddPayment = useCallback(
    (type: Payment['type']) => {
      if (!paymentsPermissions.canCreate) {
        showPermissionNotice(t('permissionNoAddPayment') ?? 'Недостаточно прав для добавления платежей.');
        return;
      }
      setModalType(type);
      setEditingPayment(null);
      setIsModalOpen(true);
    },
    [paymentsPermissions.canCreate, showPermissionNotice, t],
  );

  const handleEditPayment = useCallback(
    (payment: Payment) => {
      if (!paymentsPermissions.canEdit) {
        showPermissionNotice(t('permissionNoEditPayment') ?? 'Недостаточно прав для редактирования платежей.');
        return;
      }
      setEditingPayment(payment);
      setModalType(payment.type);
      setIsModalOpen(true);
    },
    [paymentsPermissions.canEdit, showPermissionNotice, t],
  );

  const handleDeletePayment = useCallback(
    async (payment: Payment) => {
      if (!paymentsPermissions.canDelete) {
        showPermissionNotice(t('permissionNoDeletePayment') ?? 'Недостаточно прав для удаления платежей.');
        return;
      }
      const confirmed = window.confirm(t('confirmDeletePayment') ?? 'Удалить платёж?');
      if (!confirmed) {
        return;
      }
      await deletePayment(payment.id);
      await refresh();
    },
    [deletePayment, paymentsPermissions.canDelete, refresh, showPermissionNotice, t],
  );

  const handleSubmit = useCallback(
    async (payload: SubmitDTO) => {
      if ('id' in payload) {
        await updatePayment(payload);
      } else {
        await createPayment(payload);
      }
      await refresh();
      setIsModalOpen(false);
      setEditingPayment(null);
    },
    [createPayment, refresh, updatePayment],
  );

  const handleCloseModal = useCallback(() => {
    setIsModalOpen(false);
    setEditingPayment(null);
    void refresh();
  }, [refresh]);

  const handleRefresh = useCallback(async () => {
    await refresh();
  }, [refresh]);

  const handleToggleClient = useCallback((clientId: number) => {
    setSelectedClientIds((prev) =>
      prev.includes(clientId) ? prev.filter((id) => id !== clientId) : [...prev, clientId],
    );
  }, []);

  const handleToggleCategory = useCallback((value: CategoryFilterKey) => {
    setSelectedCategoryKeys((prev) =>
      prev.includes(value) ? prev.filter((key) => key !== value) : [...prev, value],
    );
  }, []);

  const handleResetFilters = useCallback(() => {
    setSelectedClientIds([]);
    setSelectedCategoryKeys([]);
    setMinAmount('');
    setMaxAmount('');
    setDateFrom(defaultDateFrom);
    setDateTo(defaultDateTo);
    void refresh();
  }, [defaultDateFrom, defaultDateTo, refresh]);

  const handleApplyCurrentMonth = useCallback(() => {
    setDateFrom(defaultDateFrom);
    setDateTo(defaultDateTo);
  }, [defaultDateFrom, defaultDateTo]);

  const lastUpdatedText = useMemo(() => {
    if (!lastUpdated) {
      return null;
    }
    const template = t('paymentsLastUpdated') ?? 'Обновлено {{time}}';
    const time = lastUpdated.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
    return template.replace('{{time}}', time);
  }, [lastUpdated, t]);

  const typeButtonClass = useCallback(
    (target: TypeFilter) => {
      const common = 'px-4 py-2 text-sm font-medium rounded-full border transition-colors focus:outline-none';
      if (target === 'all') {
        const active = typeFilter === 'all';
        return [
          common,
          active
            ? 'bg-slate-900 text-white border-slate-900 shadow'
            : 'bg-white text-slate-600 border-slate-200 hover:border-slate-300',
        ].join(' ');
      }
      if (target === 'Income') {
        const active = typeFilter === 'Income';
        return [
          common,
          active
            ? 'bg-emerald-600 text-white border-emerald-600 shadow'
            : 'bg-emerald-50 text-emerald-700 border-emerald-200 hover:border-emerald-300',
        ].join(' ');
      }
      const active = typeFilter === 'Expense';
      return [
        common,
        active
          ? 'bg-rose-600 text-white border-rose-600 shadow'
          : 'bg-rose-50 text-rose-700 border-rose-200 hover:border-rose-300',
      ].join(' ');
    },
    [typeFilter],
  );

  const showErrorState = Boolean(error) && payments.length === 0;
  const showTimeoutPlaceholder = loadTimeoutReached && payments.length === 0 && !error;
  const initialLoading = loading && payments.length === 0 && !loadTimeoutReached && !error;
  const showIncomeButton =
    paymentsPermissions.canCreate &&
    (!lockType || effectiveDefaultType === 'Income' || effectiveDefaultType === 'all');
  const showExpenseButton =
    paymentsPermissions.canCreate &&
    (!lockType || effectiveDefaultType === 'Expense' || effectiveDefaultType === 'all');

  return (
    <div className="p-8">
      <div className="mx-auto max-w-none space-y-6">
        <div className="flex flex-col md:flex-row md:items-start md:justify-between gap-4">
          <div>
            <h1 className="text-3xl font-bold text-slate-900">{headingText}</h1>
            {subtitleText ? (
              <p className="mt-2 text-slate-600 max-w-2xl">{subtitleText}</p>
            ) : null}
            {lastUpdatedText ? (
              <p className="mt-2 text-xs text-slate-500">{lastUpdatedText}</p>
            ) : null}
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={handleRefresh}
              className={`inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition-colors hover:border-slate-300 hover:text-slate-900 ${
                refreshing ? 'opacity-70 cursor-wait' : ''
              }`}
              disabled={refreshing}>
              <RotateCcw className={`h-4 w-4 ${refreshing ? 'animate-spin' : ''}`} />
              {t('paymentsRefresh') ?? t('invoiceRefresh') ?? 'Обновить'}
            </button>
          </div>
        </div>

        {permissionMessage ? (
          <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
            {permissionMessage}
          </div>
        ) : null}

        <div className={`grid gap-4 grid-cols-1 ${summaryGridClass}`}>
          {showIncomeSummary ? (
            <div className="rounded-xl border border-emerald-100 bg-emerald-50 p-4 shadow-sm">
              <div className="text-sm font-medium text-emerald-700">
                {t('paymentsSummaryIncome') ?? t('incomePlural') ?? 'Доходы'}
              </div>
              <div className="mt-2 text-2xl font-semibold text-emerald-800">
                {formatCurrencySmart(summary.income).full}
              </div>
              <div className="mt-1 text-xs text-emerald-700/80">
                {formatCountLabel(t('paymentsEntriesCount') ?? 'Платежей: {{count}}', summary.incomeCount)}
              </div>
            </div>
          ) : null}

          {showExpenseSummary ? (
            <div className="rounded-xl border border-rose-100 bg-rose-50 p-4 shadow-sm">
              <div className="text-sm font-medium text-rose-700">
                {t('paymentsSummaryExpense') ?? t('expensePlural') ?? 'Расходы'}
              </div>
              <div className="mt-2 text-2xl font-semibold text-rose-700">
                {formatCurrencySmart(summary.expense).full}
              </div>
              <div className="mt-1 text-xs text-rose-700/80">
                {formatCountLabel(t('paymentsEntriesCount') ?? 'Платежей: {{count}}', summary.expenseCount)}
              </div>
            </div>
          ) : null}

          <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="text-sm font-medium text-slate-600">
              {t('paymentsSummaryBalance') ?? 'Сальдо'}
            </div>
            <div
              className={`mt-2 text-2xl font-semibold ${
                summary.balance >= 0 ? 'text-emerald-600' : 'text-rose-600'
              }`}>
              {formatCurrencySmart(summary.balance).full}
            </div>
            <div className="mt-1 text-xs text-slate-500">
              {t('paymentsTotalCount') ?? 'Всего платежей'}: {summary.total.toLocaleString('ru-RU')}
            </div>
          </div>

          <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div className="text-sm font-medium text-slate-600">
              {t('paymentsTotalCount') ?? 'Всего платежей'}
            </div>
            <div className="mt-2 text-3xl font-semibold text-slate-900">
              {summary.total.toLocaleString('ru-RU')}
            </div>
            <div className="mt-1 text-xs text-slate-500">
              {t('recordsFound') ?? 'Найдено записей:'} {filteredPayments.length.toLocaleString('ru-RU')}
            </div>
          </div>
        </div>

        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm space-y-4">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
            <div className="flex flex-wrap items-center gap-2">
              {lockType ? (
                <span className="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1 text-sm font-medium text-slate-600">
                  <Lock className="h-3.5 w-3.5 text-slate-500" />
                  {lockedTypeMessage}
                </span>
              ) : (
                <>
                  <button type="button" className={typeButtonClass('all')} onClick={() => setTypeFilter('all')}>
                    {t('paymentsFilterAll') ?? 'Все'}
                  </button>
                  <button type="button" className={typeButtonClass('Income')} onClick={() => setTypeFilter('Income')}>
                    {t('paymentsFilterIncome') ?? t('incomePlural') ?? 'Доходы'}
                  </button>
                  <button type="button" className={typeButtonClass('Expense')} onClick={() => setTypeFilter('Expense')}>
                    {t('paymentsFilterExpense') ?? t('expensePlural') ?? 'Расходы'}
                  </button>
                </>
              )}
            </div>

            <div className="flex flex-wrap items-center gap-3">
              <button
                type="button"
                onClick={() => setFiltersOpen((open) => !open)}
                aria-expanded={filtersOpen}
                className={[`
                  inline-flex items-center gap-2 rounded-lg border px-4 py-2 text-sm font-medium transition-colors
                `,
                  filtersOpen || filtersActive
                    ? 'border-blue-500 bg-blue-50 text-blue-700 hover:bg-blue-100'
                    : 'border-slate-200 text-slate-600 hover:border-slate-300 hover:text-slate-900',
                ].join(' ')}>
                <SlidersHorizontal className="h-4 w-4" />
                <span>{t('paymentsFiltersToggle') ?? 'Фильтры'}</span>
                {activeFiltersCount > 0 ? (
                  <span className="inline-flex h-5 min-w-[1.25rem] items-center justify-center rounded-full bg-blue-600 px-1 text-xs font-semibold text-white">
                    {activeFiltersCount}
                  </span>
                ) : null}
                <ChevronDown className={`h-4 w-4 transition-transform ${filtersOpen ? 'rotate-180' : ''}`} />
              </button>

              <select
                className="min-w-[180px] rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:border-slate-400 focus:outline-none"
                value={statusFilter}
                onChange={(event) => {
                  const value = event.target.value as StatusFilter;
                  setStatusFilter(value);
                }}>
                <option value="all">{t('paymentsStatusAll') ?? t('allStatuses') ?? 'Все статусы'}</option>
                {availableStatuses.map((status) => (
                  <option key={status} value={status}>
                    {statusLabels[status] ?? status}
                  </option>
                ))}
              </select>

              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                <input
                  type="search"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  placeholder={t('paymentsSearchPlaceholder') ?? t('search') ?? 'Поиск'}
                  className="w-64 rounded-lg border border-slate-200 bg-white py-2 pl-10 pr-3 text-sm text-slate-700 placeholder:text-slate-400 focus:border-slate-400 focus:outline-none"
                />
              </div>
            </div>
          </div>

          {filtersOpen ? (
            <div className="space-y-4 rounded-lg border border-slate-200 bg-slate-50/80 p-4">
              <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
                <div className="space-y-2">
                  <p className="text-sm font-medium text-slate-700">
                    {t('paymentsFiltersDateRange') ?? 'Период'}
                  </p>
                  <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                    <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">
                      {t('paymentsFiltersDateFrom') ?? 'С'}
                      <input
                        type="date"
                        value={dateFrom}
                        max={dateTo || undefined}
                        onChange={(event) => setDateFrom(event.target.value)}
                        className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:border-blue-400 focus:outline-none"
                      />
                    </label>
                    <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">
                      {t('paymentsFiltersDateTo') ?? 'По'}
                      <input
                        type="date"
                        value={dateTo}
                        min={dateFrom || undefined}
                        onChange={(event) => setDateTo(event.target.value)}
                        className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:border-blue-400 focus:outline-none"
                      />
                    </label>
                  </div>
                </div>

                <div className="space-y-2">
                  <p className="text-sm font-medium text-slate-700">
                    {t('paymentsFiltersClients') ?? 'Клиенты'}
                  </p>
                  {clientOptions.length > 0 ? (
                    <div className="max-h-40 overflow-y-auto thin-scrollbar rounded-lg border border-slate-200 bg-white">
                      {clientOptions.map((client) => (
                        <label
                          key={client.id}
                          className="flex items-center gap-2 px-3 py-2 text-sm text-slate-600 transition-colors hover:bg-slate-100">
                          <input
                            type="checkbox"
                            className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                            checked={selectedClientIds.includes(client.id)}
                            onChange={() => handleToggleClient(client.id)}
                          />
                          <span className="truncate">{client.name}</span>
                        </label>
                      ))}
                    </div>
                  ) : (
                    <p className="rounded-lg border border-dashed border-slate-200 bg-white/60 px-3 py-2 text-xs text-slate-500">
                      {t('paymentsFiltersClientsEmpty') ?? 'Клиенты появятся после загрузки платежей'}
                    </p>
                  )}
                </div>

                <div className="space-y-2">
                  <p className="text-sm font-medium text-slate-700">
                    {t('paymentsFiltersCategories') ?? 'Категории'}
                  </p>
                  {categoryOptions.length > 0 ? (
                    <div className="max-h-40 overflow-y-auto thin-scrollbar rounded-lg border border-slate-200 bg-white">
                      {categoryOptions.map((category) => (
                        <label
                          key={category.value}
                          className="flex items-center gap-2 px-3 py-2 text-sm text-slate-600 transition-colors hover:bg-slate-100">
                          <input
                            type="checkbox"
                            className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                            checked={selectedCategoryKeys.includes(category.value)}
                            onChange={() => handleToggleCategory(category.value)}
                          />
                          <span className="truncate">{category.label}</span>
                        </label>
                      ))}
                    </div>
                  ) : (
                    <p className="rounded-lg border border-dashed border-slate-200 bg-white/60 px-3 py-2 text-xs text-slate-500">
                      {t('paymentsFiltersCategoriesEmpty') ?? 'Категории появятся после загрузки платежей'}
                    </p>
                  )}
                </div>

                <div className="space-y-2">
                  <p className="text-sm font-medium text-slate-700">
                    {t('paymentsFiltersAmount') ?? 'Сумма'}
                  </p>
                  <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                    <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">
                      {t('paymentsFiltersAmountMin') ?? 'От'}
                      <input
                        type="number"
                        inputMode="decimal"
                        value={minAmount}
                        onChange={(event) => setMinAmount(event.target.value)}
                        placeholder="0"
                        className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:border-blue-400 focus:outline-none"
                      />
                    </label>
                    <label className="flex flex-col gap-1 text-xs font-medium text-slate-500">
                      {t('paymentsFiltersAmountMax') ?? 'До'}
                      <input
                        type="number"
                        inputMode="decimal"
                        value={maxAmount}
                        onChange={(event) => setMaxAmount(event.target.value)}
                        placeholder="0"
                        className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:border-blue-400 focus:outline-none"
                      />
                    </label>
                  </div>
                </div>
              </div>

              <div className="flex flex-col gap-3 border-t border-slate-200 pt-3 lg:flex-row lg:items-center lg:justify-between">
                <p className="text-xs text-slate-500">
                  {t('paymentsFiltersHint') ?? 'Настройте фильтры и список обновится автоматически.'}
                </p>
                <div className="flex flex-wrap items-center gap-2">
                  <button
                    type="button"
                    onClick={handleApplyCurrentMonth}
                    className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-600 transition-colors hover:border-blue-300 hover:text-blue-700">
                    {t('paymentsFiltersPresetCurrentMonth') ?? 'Текущий месяц'}
                  </button>
                  <button
                    type="button"
                    onClick={handleResetFilters}
                    className="inline-flex items-center gap-2 rounded-lg border border-transparent bg-rose-50 px-3 py-2 text-sm font-medium text-rose-600 transition-colors hover:bg-rose-100">
                    {t('paymentsFiltersReset') ?? 'Сбросить фильтры'}
                  </button>
                </div>
              </div>
            </div>
          ) : null}

          {paymentsPermissions.canCreate && (showIncomeButton || showExpenseButton) ? (
            <div className="flex flex-wrap gap-3">
              {showIncomeButton ? (
                <button
                  type="button"
                  onClick={() => handleAddPayment('Income')}
                  className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-emerald-700 focus:outline-none">
                  <Plus className="h-4 w-4" />
                  {t('addIncome') ?? 'Добавить доход'}
                </button>
              ) : null}
              {showExpenseButton ? (
                <button
                  type="button"
                  onClick={() => handleAddPayment('Expense')}
                  className="inline-flex items-center gap-2 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-rose-700 focus:outline-none">
                  <Plus className="h-4 w-4" />
                  {t('addExpense') ?? 'Добавить расход'}
                </button>
              ) : null}
            </div>
          ) : null}
        </div>

        <div className="space-y-4">
          {initialLoading ? (
            <div className="flex justify-center py-12">
              <div className="flex flex-col items-center gap-3 text-slate-500">
                <div className="h-10 w-10 animate-spin rounded-full border-4 border-slate-200 border-t-slate-900" />
                <span className="text-sm">{t('loading') ?? 'Загрузка...'}</span>
              </div>
            </div>
          ) : showErrorState ? (
            <div className="rounded-xl border border-red-200 bg-red-50 p-6 text-center text-sm text-red-700">
              <p>{t('paymentsLoadError') ?? 'Не удалось загрузить платежи.'}</p>
              <button
                type="button"
                onClick={handleRefresh}
                className="mt-3 inline-flex items-center justify-center rounded-md border border-red-300 bg-white/80 px-3 py-1.5 font-medium text-red-700 transition-colors hover:bg-red-100">
                {t('paymentsRetry') ?? 'Повторить попытку'}
              </button>
            </div>
          ) : showTimeoutPlaceholder ? (
            <div className="rounded-xl border border-amber-200 bg-amber-50 p-6 text-center text-sm text-amber-700">
              <p>
                {t('paymentsSlowLoadWarning') ??
                  'Загрузка занимает больше обычного. Попробуйте обновить список чуть позже.'}
              </p>
              <button
                type="button"
                onClick={handleRefresh}
                className="mt-3 inline-flex items-center justify-center rounded-md border border-amber-300 bg-white/80 px-3 py-1.5 font-medium text-amber-700 transition-colors hover:bg-amber-100">
                {t('paymentsRetry') ?? 'Повторить попытку'}
              </button>
            </div>
          ) : (
            <PaymentsTable
              payments={filteredPayments}
              onEditPayment={handleEditPayment}
              onOpenClient={onOpenClient}
              onAddPayment={handleAddPayment}
              onDeletePayment={handleDeletePayment}
              canAdd={paymentsPermissions.canCreate}
              canEdit={paymentsPermissions.canEdit}
              canDelete={paymentsPermissions.canDelete}
            />
          )}
        </div>

        {!initialLoading && !showErrorState && !showTimeoutPlaceholder && filteredPayments.length === 0 ? (
          <div className="rounded-xl border border-slate-200 bg-white p-6 text-center text-slate-500">
            {t('paymentsEmpty') ?? t('noPaymentsForFilter') ?? 'Платежи не найдены'}
          </div>
        ) : null}
      </div>

      <PaymentModal
        isOpen={isModalOpen}
        onClose={handleCloseModal}
        onSubmit={handleSubmit}
        payment={editingPayment}
        type={modalType}
        onDelete={
          paymentsPermissions.canDelete
            ? async (id) => {
                await deletePayment(id);
                await refresh();
                setIsModalOpen(false);
                setEditingPayment(null);
              }
            : undefined
        }
      />
    </div>
  );
}
