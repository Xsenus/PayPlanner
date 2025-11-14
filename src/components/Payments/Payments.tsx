import {
  ArrowDownCircle,
  ArrowUpCircle,
  CalendarRange,
  Filter,
  Loader2,
  RefreshCcw,
  Search,
} from 'lucide-react';
import { useMemo, useState } from 'react';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { usePayments } from '../../hooks/usePayments';
import { useTranslation } from '../../hooks/useTranslation';
import type { Payment, PaymentStatus } from '../../types';
import { formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { formatCurrencySmart } from '../../utils/formatters';

type TypeFilter = 'all' | Payment['type'];
type StatusFilter = 'all' | PaymentStatus;

const STATUS_ORDER: PaymentStatus[] = ['Completed', 'Pending', 'Overdue', 'Processing', 'Cancelled'];

function getDateInputValue(value?: string): string {
  if (!value) return '';
  return toDateInputValue(value) ?? value;
}

export function Payments() {
  const { t, formatDate } = useTranslation();

  const today = useMemo(() => new Date(), []);
  const startOfYear = useMemo(
    () => formatLocalYMD(new Date(today.getFullYear(), 0, 1)),
    [today],
  );
  const todayYMD = useMemo(() => formatLocalYMD(today), [today]);

  const [from, setFrom] = useState<string>(startOfYear);
  const [to, setTo] = useState<string>(todayYMD);
  const [typeFilter, setTypeFilter] = useState<TypeFilter>('all');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search.trim(), 400);

  const { payments, loading, error, refresh, refreshing } = usePayments(
    from || undefined,
    to || undefined,
  );

  const statusLabels = useMemo(
    () => ({
      Completed: t('statusCompleted') ?? t('completedStatus') ?? 'Выполнено',
      Pending: t('pending') ?? 'Ожидается',
      Overdue: t('overdue') ?? 'Просрочено',
      Processing: t('processingStatus') ?? 'В обработке',
      Cancelled: t('cancelledStatus') ?? 'Отменено',
    }),
    [t],
  );

  const filteredPayments = useMemo(() => {
    const query = debouncedSearch.toLowerCase();
    return payments.filter((payment) => {
      const matchesType = typeFilter === 'all' || payment.type === typeFilter;
      const matchesStatus = statusFilter === 'all' || payment.status === statusFilter;
      const matchesQuery =
        query.length === 0 ||
        [
          payment.description,
          payment.notes,
          payment.client?.name,
          payment.clientCase?.title,
          payment.paymentSource?.name,
          payment.dealType?.name,
          payment.incomeType?.name,
          payment.account ?? undefined,
        ]
          .filter(Boolean)
          .some((value) => value!.toLowerCase().includes(query));

      return matchesType && matchesStatus && matchesQuery;
    });
  }, [debouncedSearch, payments, statusFilter, typeFilter]);

  const summary = useMemo(() => {
    let incomeAmount = 0;
    let expenseAmount = 0;
    let incomeCount = 0;
    let expenseCount = 0;
    const byStatus: Record<PaymentStatus, { count: number; amount: number }> = {
      Completed: { count: 0, amount: 0 },
      Pending: { count: 0, amount: 0 },
      Overdue: { count: 0, amount: 0 },
      Processing: { count: 0, amount: 0 },
      Cancelled: { count: 0, amount: 0 },
    };

    for (const payment of filteredPayments) {
      if (payment.type === 'Income') {
        incomeAmount += payment.amount;
        incomeCount += 1;
      } else if (payment.type === 'Expense') {
        expenseAmount += payment.amount;
        expenseCount += 1;
      }

      const status = payment.status;
      if (status in byStatus) {
        byStatus[status].count += 1;
        byStatus[status].amount += payment.amount;
      }
    }

    return {
      incomeAmount,
      expenseAmount,
      incomeCount,
      expenseCount,
      balance: incomeAmount - expenseAmount,
      totalCount: filteredPayments.length,
      byStatus,
    };
  }, [filteredPayments]);

  const handleRefresh = () => {
    void refresh();
  };

  return (
    <div className="min-h-screen bg-gray-50 lg:pl-[var(--pp-sidebar-w,17rem)]">
      <div className="mx-auto w-full max-w-7xl px-4 pb-12 pt-6 sm:px-6 lg:px-8">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h1 className="text-2xl font-bold text-slate-900">
              {t('paymentsJournalTitle') ?? 'Журнал платежей'}
            </h1>
            <p className="mt-1 text-sm text-slate-600">
              {t('paymentsJournalSubtitle') ??
                'Просматривайте все платежи компании и фильтруйте их по направлениям и статусам.'}
            </p>
          </div>
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={handleRefresh}
              disabled={refreshing}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 shadow-sm transition-colors hover:bg-white disabled:cursor-not-allowed disabled:opacity-60"
            >
              {refreshing ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <RefreshCcw className="h-4 w-4" />
              )}
              {t('paymentsRefresh') ?? 'Обновить'}
            </button>
          </div>
        </div>

        <div className="mt-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          <SummaryCard
            icon={ArrowUpCircle}
            title={t('paymentsSummaryIncome') ?? 'Доходные платежи'}
            amount={formatCurrencySmart(summary.incomeAmount).full}
            count={summary.incomeCount}
            accent="border border-emerald-200 bg-emerald-50 text-emerald-700"
          />
          <SummaryCard
            icon={ArrowDownCircle}
            title={t('paymentsSummaryExpense') ?? 'Расходные платежи'}
            amount={formatCurrencySmart(summary.expenseAmount).full}
            count={summary.expenseCount}
            accent="border border-rose-200 bg-rose-50 text-rose-700"
          />
          <SummaryCard
            icon={CalendarRange}
            title={t('paymentsSummaryBalance') ?? 'Баланс'}
            amount={formatCurrencySmart(summary.balance).full}
            count={summary.totalCount}
            accent="border border-slate-200 bg-slate-50 text-slate-700"
          />
          <SummaryCard
            icon={Filter}
            title={t('paymentsSummaryFiltered') ?? 'Фильтр'}
            amount={
              typeFilter === 'all'
                ? t('paymentTypeAll') ?? 'Все направления'
                : typeFilter === 'Income'
                ? t('paymentTypeIncome') ?? 'Доходные'
                : t('paymentTypeExpense') ?? 'Расходные'
            }
            count={summary.totalCount}
            accent="border border-sky-200 bg-sky-50 text-sky-700"
          />
        </div>

        <div className="mt-6 grid gap-4 lg:grid-cols-12">
          <div className="lg:col-span-3">
            <label className="block text-sm font-medium text-slate-700" htmlFor="payments-from">
              {t('fromDate') ?? 'С'}
            </label>
            <input
              id="payments-from"
              type="date"
              value={getDateInputValue(from)}
              onChange={(event) => setFrom(event.target.value)}
              className="mt-2 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500/40"
            />
          </div>
          <div className="lg:col-span-3">
            <label className="block text-sm font-medium text-slate-700" htmlFor="payments-to">
              {t('toDate') ?? 'По'}
            </label>
            <input
              id="payments-to"
              type="date"
              value={getDateInputValue(to)}
              onChange={(event) => setTo(event.target.value)}
              className="mt-2 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500/40"
            />
          </div>
          <div className="lg:col-span-3">
            <label className="block text-sm font-medium text-slate-700" htmlFor="payments-status">
              {t('statusFilter') ?? 'Статус'}
            </label>
            <select
              id="payments-status"
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value as StatusFilter)}
              className="mt-2 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500/40"
            >
              <option value="all">{t('paymentsStatusAll') ?? t('allStatuses') ?? 'Все статусы'}</option>
              {STATUS_ORDER.map((status) => (
                <option key={status} value={status}>
                  {statusLabels[status] ?? status}
                </option>
              ))}
            </select>
          </div>
          <div className="lg:col-span-3">
            <label className="block text-sm font-medium text-slate-700" htmlFor="payments-search">
              {t('search') ?? 'Поиск'}
            </label>
            <div className="mt-2 flex items-center rounded-lg border border-slate-300 px-3 py-2 shadow-sm focus-within:border-blue-500 focus-within:ring-2 focus-within:ring-blue-500/40">
              <Search className="h-4 w-4 text-slate-400" />
              <input
                id="payments-search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t('paymentsSearchPlaceholder') ?? 'Клиент, описание или категория'}
                className="ml-2 w-full border-none bg-transparent text-sm text-slate-700 placeholder:text-slate-400 focus:outline-none"
              />
            </div>
          </div>
        </div>

        <div className="mt-4 flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => setTypeFilter('all')}
            className={`inline-flex items-center rounded-full px-3 py-1 text-sm font-medium transition-colors ${
              typeFilter === 'all'
                ? 'bg-slate-900 text-white shadow-sm'
                : 'bg-white text-slate-600 border border-slate-200 hover:bg-slate-50'
            }`}
          >
            {t('paymentTypeAll') ?? 'Все платежи'}
          </button>
          <button
            type="button"
            onClick={() => setTypeFilter('Income')}
            className={`inline-flex items-center rounded-full px-3 py-1 text-sm font-medium transition-colors ${
              typeFilter === 'Income'
                ? 'bg-emerald-600 text-white shadow-sm'
                : 'bg-white text-emerald-600 border border-emerald-200 hover:bg-emerald-50'
            }`}
          >
            {t('paymentTypeIncome') ?? 'Доходные'}
          </button>
          <button
            type="button"
            onClick={() => setTypeFilter('Expense')}
            className={`inline-flex items-center rounded-full px-3 py-1 text-sm font-medium transition-colors ${
              typeFilter === 'Expense'
                ? 'bg-rose-600 text-white shadow-sm'
                : 'bg-white text-rose-600 border border-rose-200 hover:bg-rose-50'
            }`}
          >
            {t('paymentTypeExpense') ?? 'Расходные'}
          </button>
        </div>

        <div className="mt-6 grid gap-3 md:grid-cols-2 xl:grid-cols-5">
          {STATUS_ORDER.map((status) => {
            const bucket = summary.byStatus[status];
            return (
              <div
                key={status}
                className="rounded-xl border border-slate-200 bg-white px-4 py-3 shadow-sm"
              >
                <div className="text-xs font-medium uppercase text-slate-500">
                  {statusLabels[status] ?? status}
                </div>
                <div className="mt-2 text-lg font-semibold text-slate-900">
                  {formatCurrencySmart(bucket.amount).full}
                </div>
                <div className="text-xs text-slate-500">
                  {t('paymentsCountLabel') ?? 'Количество'}: {bucket.count}
                </div>
              </div>
            );
          })}
        </div>

        <div className="mt-6 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table className="min-w-[960px] w-full divide-y divide-slate-200">
              <thead className="bg-slate-50">
                <tr>
                  {[
                    t('date') ?? 'Дата',
                    t('client') ?? 'Клиент',
                    t('description') ?? 'Описание',
                    t('type') ?? 'Тип',
                    t('status') ?? t('statusFilter') ?? 'Статус',
                    t('amount') ?? 'Сумма',
                    t('paidAmount') ?? 'Оплачено',
                    t('outstandingAmount') ?? 'Остаток',
                    t('account') ?? 'Счёт',
                  ].map((column, index) => (
                    <th
                      key={index}
                      scope="col"
                      className="px-4 py-3 text-left text-xs font-semibold uppercase tracking-wide text-slate-500"
                    >
                      {column}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {loading ? (
                  <tr>
                    <td colSpan={9} className="px-4 py-10 text-center text-sm text-slate-500">
                      <div className="flex flex-col items-center gap-3">
                        <Loader2 className="h-6 w-6 animate-spin text-slate-400" />
                        <span>{t('loading') ?? 'Загрузка платежей...'}</span>
                      </div>
                    </td>
                  </tr>
                ) : error ? (
                  <tr>
                    <td colSpan={9} className="px-4 py-10 text-center text-sm text-rose-600">
                      <div className="mx-auto max-w-lg">
                        <p className="font-medium">{t('paymentsLoadError') ?? 'Не удалось загрузить платежи.'}</p>
                        <p className="mt-1 text-sm text-rose-500">
                          {error}
                        </p>
                        <button
                          type="button"
                          onClick={handleRefresh}
                          className="mt-4 inline-flex items-center gap-2 rounded-lg border border-rose-200 px-4 py-2 text-sm font-medium text-rose-600 hover:bg-rose-50"
                        >
                          <RefreshCcw className="h-4 w-4" />
                          {t('paymentsRetry') ?? 'Повторить попытку'}
                        </button>
                      </div>
                    </td>
                  </tr>
                ) : filteredPayments.length === 0 ? (
                  <tr>
                    <td colSpan={9} className="px-4 py-10 text-center text-sm text-slate-500">
                      <div className="mx-auto max-w-lg">
                        <p className="font-medium">
                          {t('paymentsNoResults') ?? 'По заданным условиям платежи не найдены.'}
                        </p>
                        <p className="mt-1 text-sm text-slate-400">
                          {t('paymentsNoResultsHint') ?? 'Измените фильтры или период, чтобы увидеть результаты.'}
                        </p>
                      </div>
                    </td>
                  </tr>
                ) : (
                  filteredPayments.map((payment) => {
                    const isIncome = payment.type === 'Income';
                    const typeClass = isIncome
                      ? 'bg-emerald-50 text-emerald-700'
                      : 'bg-rose-50 text-rose-700';
                    return (
                      <tr key={payment.id} className="hover:bg-slate-50/70">
                        <td className="px-4 py-3 text-sm text-slate-700 whitespace-nowrap">
                          {payment.date ? formatDate(payment.date) : '—'}
                        </td>
                        <td className="px-4 py-3 text-sm text-slate-700">
                          <div className="font-medium text-slate-900">
                            {payment.client?.name ?? t('noClient') ?? 'Без клиента'}
                          </div>
                          {payment.clientCase?.title ? (
                            <div className="text-xs text-slate-500">{payment.clientCase.title}</div>
                          ) : null}
                        </td>
                        <td className="px-4 py-3 text-sm text-slate-700 max-w-[240px]">
                          <div className="truncate" title={payment.description || undefined}>
                            {payment.description || t('noDescription') || 'Без описания'}
                          </div>
                          {payment.notes ? (
                            <div className="text-xs text-slate-500 truncate" title={payment.notes}>
                              {payment.notes}
                            </div>
                          ) : null}
                        </td>
                        <td className="px-4 py-3">
                          <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${typeClass}`}>
                            {isIncome ? t('income') ?? 'Доход' : t('expense') ?? 'Расход'}
                          </span>
                        </td>
                        <td className="px-4 py-3">
                          <span className="inline-flex items-center rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
                            {statusLabels[payment.status] ?? payment.status}
                          </span>
                        </td>
                        <td className="px-4 py-3 text-sm font-semibold text-slate-900 whitespace-nowrap">
                          {formatCurrencySmart(payment.amount).full}
                        </td>
                        <td className="px-4 py-3 text-sm text-slate-700 whitespace-nowrap">
                          {formatCurrencySmart(payment.paidAmount).full}
                        </td>
                        <td className="px-4 py-3 text-sm text-slate-700 whitespace-nowrap">
                          {formatCurrencySmart(payment.outstandingAmount).full}
                        </td>
                        <td className="px-4 py-3 text-sm text-slate-600">
                          {payment.account ?? '—'}
                        </td>
                      </tr>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </div>
  );
}

interface SummaryCardProps {
  icon: React.ComponentType<{ className?: string }>;
  title: string;
  amount: string;
  count: number;
  accent: string;
}

function SummaryCard({ icon: Icon, title, amount, count, accent }: SummaryCardProps) {
  return (
    <div className={`rounded-2xl px-4 py-5 shadow-sm ${accent}`}>
      <div className="flex items-center justify-between">
        <div>
          <div className="text-sm font-medium uppercase tracking-wide text-slate-600/80">{title}</div>
          <div className="mt-2 text-xl font-semibold text-slate-900/90">{amount}</div>
          <div className="text-xs text-slate-600/80">{count}</div>
        </div>
        <Icon className="h-10 w-10 opacity-70" />
      </div>
    </div>
  );
}

