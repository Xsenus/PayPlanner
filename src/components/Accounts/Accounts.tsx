import { Fragment, useEffect, useMemo, useState } from 'react';
import {
  CalendarRange,
  ChevronDown,
  ChevronRight,
  Loader2,
  RotateCcw,
  Search,
  SlidersHorizontal,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { usePayments } from '../../hooks/usePayments';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import type { Payment } from '../../types';

const ACCOUNT_COLUMNS = [
  { id: 'accountDate', label: 'accountDate' },
  { id: 'accountNumber', label: 'invoiceNumber' },
  { id: 'amount', label: 'invoiceAmount' },
  { id: 'counterparty', label: 'invoiceClient' },
  { id: 'status', label: 'invoiceStatus' },
  { id: 'payUntil', label: 'accountPayUntil' },
  { id: 'paidDate', label: 'invoicePaidDate' },
  { id: 'act', label: 'invoiceAct' },
] as const;

type ColumnId = (typeof ACCOUNT_COLUMNS)[number]['id'];

type AccountStatus = 'created' | 'paid' | 'partial';

interface AccountRow {
  account: string;
  accountDate?: string | null;
  counterparty?: string;
  totalAmount: number;
  totalPaid: number;
  status: AccountStatus;
  payUntil?: string | null;
  lastPaymentDate?: string | null;
  actLabel?: string | null;
  payments: Payment[];
}

function defaultPeriod() {
  const now = new Date();
  const start = new Date(now.getFullYear(), now.getMonth(), 1);
  return { from: toDateInputValue(start) ?? '', to: toDateInputValue(now) ?? '' };
}

function safeParseJSON<T>(raw: string | null): T | null {
  if (!raw) return null;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

function withinRange(dateStr: string | null | undefined, from: string, to: string) {
  if (!dateStr) return true;
  const ts = new Date(dateStr).getTime();
  if (Number.isNaN(ts)) return true;
  const fromTs = from ? new Date(from).getTime() : Number.NEGATIVE_INFINITY;
  const toTs = to ? new Date(to).getTime() : Number.POSITIVE_INFINITY;
  return ts >= fromTs && ts <= toTs;
}

function deriveStatus(totalAmount: number, totalPaid: number): AccountStatus {
  if (totalAmount <= 0) return 'created';
  if (totalPaid >= totalAmount) return 'paid';
  if (totalPaid > 0) return 'partial';
  return 'created';
}

export function Accounts() {
  const { t, formatDate } = useTranslation();
  const { user } = useAuth();

  const periodKey = `pp.accounts.period.${user?.id ?? 'guest'}`;
  const columnsKey = `pp.accounts.columns.${user?.id ?? 'guest'}`;

  const storedPeriod = safeParseJSON<{ from?: string; to?: string }>(
    typeof window !== 'undefined' ? window.localStorage.getItem(periodKey) : null,
  );

  const [fromDate, setFromDate] = useState<string>(storedPeriod?.from ?? defaultPeriod().from);
  const [toDate, setToDate] = useState<string>(storedPeriod?.to ?? defaultPeriod().to);
  const [search, setSearch] = useState('');
  const [columnsOpen, setColumnsOpen] = useState(false);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const debouncedSearch = useDebouncedValue(search.trim().toLowerCase(), 250);

  const storedColumns = safeParseJSON<ColumnId[]>(
    typeof window !== 'undefined' ? window.localStorage.getItem(columnsKey) : null,
  );
  const [visibleColumns, setVisibleColumns] = useState<ColumnId[]>(
    storedColumns?.length ? storedColumns : ACCOUNT_COLUMNS.map((c) => c.id),
  );

  useEffect(() => {
    try {
      window.localStorage.setItem(periodKey, JSON.stringify({ from: fromDate, to: toDate }));
    } catch {
      /** */
    }
  }, [fromDate, periodKey, toDate]);

  useEffect(() => {
    try {
      window.localStorage.setItem(columnsKey, JSON.stringify(visibleColumns));
    } catch {
      /** */
    }
  }, [columnsKey, visibleColumns]);

  const { payments, loading, refreshing, error, refresh } = usePayments(
    fromInputToApiDate(fromDate),
    fromInputToApiDate(toDate),
  );

  const accounts = useMemo<AccountRow[]>(() => {
    const map = new Map<string, AccountRow>();

    payments.forEach((payment) => {
      const accountNumber = payment.account?.trim() || t('accountUnknown') || 'Без счёта';
      if (!withinRange(payment.accountDate, fromDate, toDate)) return;

      const existing = map.get(accountNumber);
      const totalPaid = payment.paidAmount ?? 0;
      const payUntilCandidate = payment.plannedDate ?? payment.date;
      const paidDateCandidate = payment.paidDate ?? payment.lastPaymentDate ?? null;
      const status = deriveStatus(payment.amount, totalPaid);
      const counterparty =
        payment.client?.name || payment.clientCase?.title || payment.paymentSource?.name || undefined;

      if (!existing) {
        map.set(accountNumber, {
          account: accountNumber,
          accountDate: payment.accountDate ?? null,
          counterparty,
          totalAmount: payment.amount,
          totalPaid,
          status,
          payUntil: payUntilCandidate ?? null,
          lastPaymentDate: paidDateCandidate,
          actLabel: null,
          payments: [payment],
        });
        return;
      }

      existing.totalAmount += payment.amount;
      existing.totalPaid += totalPaid;
      if (!existing.accountDate && payment.accountDate) existing.accountDate = payment.accountDate;
      if (!existing.counterparty && counterparty) existing.counterparty = counterparty;
      if (payUntilCandidate) {
        const currentTs = existing.payUntil ? new Date(existing.payUntil).getTime() : 0;
        const nextTs = new Date(payUntilCandidate).getTime();
        if (Number.isNaN(currentTs) || nextTs > currentTs) {
          existing.payUntil = payUntilCandidate;
        }
      }
      if (paidDateCandidate) {
        const currentTs = existing.lastPaymentDate ? new Date(existing.lastPaymentDate).getTime() : 0;
        const nextTs = new Date(paidDateCandidate).getTime();
        if (Number.isNaN(currentTs) || nextTs > currentTs) {
          existing.lastPaymentDate = paidDateCandidate;
        }
      }
      existing.payments.push(payment);
      existing.status = deriveStatus(existing.totalAmount, existing.totalPaid);
    });

    return Array.from(map.values()).sort((a, b) => {
      const aDate = a.accountDate ? new Date(a.accountDate).getTime() : 0;
      const bDate = b.accountDate ? new Date(b.accountDate).getTime() : 0;
      return bDate - aDate;
    });
  }, [fromDate, payments, t, toDate]);

  const filteredAccounts = useMemo(() => {
    if (!debouncedSearch) return accounts;
    return accounts.filter((acc) => {
      const fields: Array<string | null | undefined> = [
        acc.account,
        acc.accountDate,
        acc.counterparty,
        acc.actLabel,
        acc.payUntil,
      ];
      return fields.some((field) => field?.toLowerCase().includes(debouncedSearch));
    });
  }, [accounts, debouncedSearch]);

  const toggleColumn = (id: ColumnId) => {
    setVisibleColumns((prev) =>
      prev.includes(id) ? prev.filter((col) => col !== id) : [...prev, id],
    );
  };

  const toggleRow = (account: string) => {
    setExpanded((prev) => ({ ...prev, [account]: !prev[account] }));
  };

  const statusLabels: Record<AccountStatus, string> = {
    created: t('invoiceStatusCreated') ?? 'Создан',
    paid: t('invoiceStatusPaid') ?? 'Оплачен',
    partial: t('invoiceStatusPartial') ?? 'Оплачен частично',
  };

  const statusClasses: Record<AccountStatus, string> = {
    created: 'bg-amber-100 text-amber-700 border border-amber-200',
    paid: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    partial: 'bg-sky-100 text-sky-700 border border-sky-200',
  };

  const visibleColumnsSet = new Set<ColumnId>(visibleColumns);

  return (
    <div className="p-6 lg:p-8">
      <div className="mx-auto max-w-7xl space-y-6">
        <div className="flex flex-col gap-2">
          <div className="flex items-center justify-between gap-3">
            <div>
              <h1 className="text-2xl font-bold text-gray-900">{t('accounts') ?? 'Счета'}</h1>
              <p className="text-gray-600 text-sm">
                {t('accountsSummaryDescription') ?? 'Сводка по счетам и связанной рассрочке.'}
              </p>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => refresh()}
                className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
              >
                {refreshing ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
                {t('invoiceRefresh') ?? 'Обновить'}
              </button>
              <div className="relative">
                <button
                  type="button"
                  onClick={() => setColumnsOpen((v) => !v)}
                  className="inline-flex items-center gap-2 rounded-lg border border-gray-200 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
                >
                  <SlidersHorizontal className="h-4 w-4" />
                  {t('tableColumns') ?? 'Столбцы'}
                </button>
                {columnsOpen ? (
                  <div className="absolute right-0 z-20 mt-2 w-64 rounded-xl border border-gray-200 bg-white p-3 shadow-xl">
                    <div className="mb-2 text-xs font-semibold uppercase text-gray-500">
                      {t('tableColumns') ?? 'Столбцы'}
                    </div>
                    <div className="space-y-1">
                      {ACCOUNT_COLUMNS.map((col) => (
                        <label
                          key={col.id}
                          className="flex items-center justify-between rounded-lg px-2 py-1 text-sm text-gray-700 hover:bg-gray-50"
                        >
                          <span>{t(col.label as never) ?? col.label}</span>
                          <input
                            type="checkbox"
                            className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                            checked={visibleColumnsSet.has(col.id)}
                            onChange={() => toggleColumn(col.id)}
                          />
                        </label>
                      ))}
                    </div>
                  </div>
                ) : null}
              </div>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <div className="flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-3 py-2 shadow-sm">
              <CalendarRange className="h-4 w-4 text-gray-500" />
              <input
                type="date"
                value={fromDate}
                onChange={(e) => setFromDate(e.target.value)}
                className="w-36 rounded-lg border border-gray-200 px-2 py-1.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
              <span className="text-gray-400">—</span>
              <input
                type="date"
                value={toDate}
                onChange={(e) => setToDate(e.target.value)}
                className="w-36 rounded-lg border border-gray-200 px-2 py-1.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
            </div>

            <div className="relative min-w-[240px] flex-1">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
              <input
                type="search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder={t('accountsSearchPlaceholder') ?? 'Поиск по счетам и контрагентам'}
                className="w-full rounded-lg border border-gray-200 pl-9 pr-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
              />
            </div>
          </div>
        </div>

        <div className="rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200">
              <thead className="bg-gray-50">
                <tr className="text-left text-xs font-semibold uppercase tracking-wide text-gray-500">
                  <th className="w-12 px-4 py-3"></th>
                  {visibleColumns.includes('accountDate') && (
                    <th className="px-4 py-3">{t('accountDate') ?? 'Дата счёта'}</th>
                  )}
                  {visibleColumns.includes('accountNumber') && (
                    <th className="px-4 py-3">{t('invoiceNumber') ?? 'Номер счёта'}</th>
                  )}
                  {visibleColumns.includes('amount') && (
                    <th className="px-4 py-3">{t('invoiceAmount') ?? 'Сумма'}</th>
                  )}
                  {visibleColumns.includes('counterparty') && (
                    <th className="px-4 py-3">{t('invoiceClient') ?? 'Контрагент'}</th>
                  )}
                  {visibleColumns.includes('status') && (
                    <th className="px-4 py-3">{t('invoiceStatus') ?? 'Статус'}</th>
                  )}
                  {visibleColumns.includes('payUntil') && (
                    <th className="px-4 py-3">{t('accountPayUntil') ?? 'Оплатить до'}</th>
                  )}
                  {visibleColumns.includes('paidDate') && (
                    <th className="px-4 py-3">{t('invoicePaidDate') ?? 'Дата оплаты'}</th>
                  )}
                  {visibleColumns.includes('act') && (
                    <th className="px-4 py-3">{t('invoiceAct') ?? 'Акт'}</th>
                  )}
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
                {loading ? (
                  <tr>
                    <td colSpan={1 + visibleColumns.length} className="py-10 text-center text-gray-500">
                      <div className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        {t('loading') ?? 'Загрузка...'}
                      </div>
                    </td>
                  </tr>
                ) : filteredAccounts.length === 0 ? (
                  <tr>
                    <td colSpan={1 + visibleColumns.length} className="py-10 text-center text-gray-500">
                      {t('invoiceNoData') ?? 'Счета не найдены'}
                    </td>
                  </tr>
                ) : (
                  filteredAccounts.map((account) => {
                    const isExpanded = expanded[account.account];
                    return (
                      <Fragment key={account.account}>
                        <tr className="hover:bg-gray-50/80">
                          <td className="px-4 py-3">
                            <button
                              type="button"
                              onClick={() => toggleRow(account.account)}
                              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-gray-200 text-gray-600 hover:bg-gray-50"
                              aria-label={isExpanded ? t('collapse') ?? 'Скрыть платежи' : t('expand') ?? 'Показать платежи'}
                            >
                              {isExpanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
                            </button>
                          </td>
                          {visibleColumns.includes('accountDate') && (
                            <td className="px-4 py-3 font-medium text-gray-900">
                              {account.accountDate ? formatDate(account.accountDate) : '—'}
                            </td>
                          )}
                          {visibleColumns.includes('accountNumber') && (
                            <td className="px-4 py-3 text-gray-900">{account.account}</td>
                          )}
                          {visibleColumns.includes('amount') && (
                            <td className="px-4 py-3">
                              <span className="font-semibold text-gray-900">
                                {formatCurrencySmart(account.totalAmount).full}
                              </span>
                              <div className="text-xs text-gray-500">
                                {t('accountPaidAmount') ?? 'Оплачено'}: {formatCurrencySmart(account.totalPaid).full}
                              </div>
                            </td>
                          )}
                          {visibleColumns.includes('counterparty') && (
                            <td className="px-4 py-3">{account.counterparty ?? '—'}</td>
                          )}
                          {visibleColumns.includes('status') && (
                            <td className="px-4 py-3">
                              <span
                                className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-medium ${statusClasses[account.status]}`}
                              >
                                {statusLabels[account.status]}
                              </span>
                            </td>
                          )}
                          {visibleColumns.includes('payUntil') && (
                            <td className="px-4 py-3">{account.payUntil ? formatDate(account.payUntil) : '—'}</td>
                          )}
                          {visibleColumns.includes('paidDate') && (
                            <td className="px-4 py-3">
                              {account.lastPaymentDate ? formatDate(account.lastPaymentDate) : '—'}
                            </td>
                          )}
                          {visibleColumns.includes('act') && (
                            <td className="px-4 py-3">
                              {account.actLabel ? account.actLabel : (
                                <span className="text-gray-500">{t('noActAttached') ?? 'Акт не привязан'}</span>
                              )}
                            </td>
                          )}
                        </tr>
                        {isExpanded ? (
                          <tr className="bg-slate-50/70" key={`${account.account}-details`}>
                            <td colSpan={1 + visibleColumns.length} className="px-6 py-4">
                              <div className="text-sm font-semibold text-gray-700 mb-2">
                                {t('accountPaymentsTitle') ?? 'Платежи по счёту'}
                              </div>
                              <div className="overflow-x-auto rounded-lg border border-gray-200 bg-white">
                                <table className="min-w-full divide-y divide-gray-200">
                                  <thead className="bg-gray-50 text-xs uppercase text-gray-500">
                                    <tr>
                                      <th className="px-3 py-2 text-left">{t('date') ?? 'Дата'}</th>
                                      <th className="px-3 py-2 text-left">{t('description') ?? 'Описание'}</th>
                                      <th className="px-3 py-2 text-left">{t('invoiceStatus') ?? 'Статус'}</th>
                                      <th className="px-3 py-2 text-right">{t('amount') ?? 'Сумма'}</th>
                                    </tr>
                                  </thead>
                                  <tbody className="divide-y divide-gray-100">
                                    {account.payments.map((payment) => {
                                      const paymentStatus = deriveStatus(payment.amount, payment.paidAmount ?? 0);
                                      return (
                                        <tr key={payment.id}>
                                          <td className="px-3 py-2 text-sm text-gray-700 whitespace-nowrap">
                                            {payment.plannedDate ? formatDate(payment.plannedDate) : '—'}
                                          </td>
                                          <td className="px-3 py-2 text-sm text-gray-700 max-w-[360px]">
                                            <div className="truncate" title={payment.description}>
                                              {payment.description || t('noDescription') || 'Без описания'}
                                            </div>
                                          </td>
                                          <td className="px-3 py-2 text-sm">
                                            <span
                                              className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                                                statusClasses[paymentStatus]
                                              }`}
                                            >
                                              {statusLabels[paymentStatus]}
                                            </span>
                                          </td>
                                          <td className="px-3 py-2 text-sm font-semibold text-right text-gray-900">
                                            {formatCurrencySmart(payment.amount).full}
                                          </td>
                                        </tr>
                                      );
                                    })}
                                  </tbody>
                                </table>
                              </div>
                            </td>
                          </tr>
                        ) : null}
                      </Fragment>
                    );
                  })
                )}
              </tbody>
            </table>
          </div>
          {error && (
            <div className="border-t border-gray-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {String(error)}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

