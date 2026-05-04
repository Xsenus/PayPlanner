import { useMemo, useState } from 'react';
import {
  AlertTriangle,
  ArrowUpRight,
  Banknote,
  CalendarRange,
  CheckCircle2,
  Clock3,
  Compass,
  CreditCard as CreditCardIcon,
  FileSpreadsheet,
  FileUp,
  Flag,
  FolderKanban,
  Handshake,
  Percent,
  RefreshCcw,
  Store,
  Target,
  TrendingDown,
  TrendingUp,
  Wallet,
} from 'lucide-react';
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from 'recharts';
import { usePeriodStats } from '../../hooks/usePeriodStats';
import { useCashFlowReport } from '../../hooks/useCashFlowReport';
import { useCashReport } from '../../hooks/useCashReport';
import { useMoneyCategoryReport } from '../../hooks/useMoneyCategoryReport';
import { useCounterpartyReport } from '../../hooks/useCounterpartyReport';
import { useProjectReport } from '../../hooks/useProjectReport';
import { useDirectionReport } from '../../hooks/useDirectionReport';
import { useSalesPointReport } from '../../hooks/useSalesPointReport';
import { useVatReport } from '../../hooks/useVatReport';
import { usePlanFactReport } from '../../hooks/usePlanFactReport';
import { useAdvanceReport } from '../../hooks/useAdvanceReport';
import { usePaymentPriorityReport } from '../../hooks/usePaymentPriorityReport';
import { usePaidAccountReport } from '../../hooks/usePaidAccountReport';
import { usePaidInvoiceReport } from '../../hooks/usePaidInvoiceReport';
import { useInvoiceStatusReport } from '../../hooks/useInvoiceStatusReport';
import { useActStatusReport } from '../../hooks/useActStatusReport';
import { useContractStatusReport } from '../../hooks/useContractStatusReport';
import { usePaymentOrderReport } from '../../hooks/usePaymentOrderReport';
import { usePaymentPurposeReport } from '../../hooks/usePaymentPurposeReport';
import { useMatchStatusReport } from '../../hooks/useMatchStatusReport';
import { useJournalStatusReport } from '../../hooks/useJournalStatusReport';
import { useCurrencyReport } from '../../hooks/useCurrencyReport';
import { useCashOrderReport } from '../../hooks/useCashOrderReport';
import { useBankStatementImportReport } from '../../hooks/useBankStatementImportReport';
import { useSpreadsheetImportReport } from '../../hooks/useSpreadsheetImportReport';
import { MonthRangePicker, type MonthRange } from '../MonthRange/MonthRangePicker';
import { formatCurrencySmart } from '../../utils/formatters';
import type { ActStatusReport, AdvanceReport, BankStatementImportReport, CashBookSummary, CashFlowReport, CashOrderReport, ContractStatusReport, CounterpartyReport, CurrencyReport, DirectionReport, InvoiceStatusReport, JournalStatusReport, MatchStatusReport, MoneyCategoryReport, MoneyOperationSource, PaidAccountReport, PaidInvoiceReport, PaymentOrderReport, PaymentPurposeReport, PaymentPriorityReport, PlanFactReport, ProjectReport, SalesPointReport, SpreadsheetImportReport, VatReport } from '../../types';

const ruMonthFormatter = new Intl.DateTimeFormat('ru-RU', {
  month: 'long',
  year: 'numeric',
});

const statusMeta: Record<
  'Completed' | 'Pending' | 'Overdue',
  { label: string; color: string; chip: string; text: string }
> = {
  Completed: {
    label: 'Выполнено',
    color: '#059669',
    chip: 'bg-emerald-100 text-emerald-700',
    text: 'text-emerald-600',
  },
  Pending: {
    label: 'Ожидается',
    color: '#d97706',
    chip: 'bg-amber-100 text-amber-700',
    text: 'text-amber-600',
  },
  Overdue: {
    label: 'Просрочено',
    color: '#dc2626',
    chip: 'bg-rose-100 text-rose-700',
    text: 'text-rose-600',
  },
};

type PieTooltipProps = {
  active?: boolean;
  payload?: Array<{
    payload?: {
      key: 'Completed' | 'Pending' | 'Overdue';
      amount: number;
      count: number;
      share: number;
    };
  }>;
};

function formatCurrency(amount: number) {
  return formatCurrencySmart(amount).full;
}

function formatYmLabel(ym: string) {
  const [year, month] = ym.split('-').map(Number);
  return ruMonthFormatter.format(new Date(year, (month ?? 1) - 1, 1));
}

function normalizeYmRange(fromYM: `${number}-${string}`, toYM: `${number}-${string}`) {
  return fromYM <= toYM ? { fromYM, toYM } : { fromYM: toYM, toYM: fromYM };
}

function buildTitleSuffix(fromYM: `${number}-${string}`, toYM: `${number}-${string}`) {
  if (fromYM === toYM) {
    return formatYmLabel(fromYM);
  }

  return `${formatYmLabel(fromYM)} - ${formatYmLabel(toYM)}`;
}

function monthRangeToDates(fromYM: `${number}-${string}`, toYM: `${number}-${string}`) {
  const normalized = normalizeYmRange(fromYM, toYM);
  const [fromYear, fromMonth] = normalized.fromYM.split('-').map(Number);
  const [toYear, toMonth] = normalized.toYM.split('-').map(Number);
  const monthDays = new Date(toYear, toMonth, 0).getDate();

  return {
    from: `${fromYear}-${String(fromMonth).padStart(2, '0')}-01`,
    to: `${toYear}-${String(toMonth).padStart(2, '0')}-${String(monthDays).padStart(2, '0')}`,
  };
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: 'short',
  }).format(new Date(value));
}

function sourceLabel(source: MoneyOperationSource) {
  switch (source) {
    case 'Bank':
      return 'Банк';
    case 'Cash':
      return 'Касса';
    case 'Card':
      return 'Карты';
    case 'EMoney':
      return 'E-money';
    default:
      return source;
  }
}

function PieTooltip({ active, payload }: PieTooltipProps) {
  const point = payload?.[0]?.payload;
  if (!active || !point) return null;

  const meta = statusMeta[point.key];
  return (
    <div className="rounded-2xl border border-slate-200 bg-white/95 px-4 py-3 text-sm shadow-xl backdrop-blur">
      <div className={`font-semibold ${meta.text}`}>{meta.label}</div>
      <div className="mt-1 text-slate-600">{formatCurrency(point.amount)}</div>
      <div className="mt-1 text-xs text-slate-500">
        {point.count} шт. · {point.share}%
      </div>
    </div>
  );
}

function MetricCard({
  title,
  value,
  icon: Icon,
  tone,
  subtitle,
}: {
  title: string;
  value: string;
  icon: React.ComponentType<{ className?: string }>;
  tone: 'income' | 'expense' | 'profit' | 'rate';
  subtitle: string;
}) {
  const toneClasses =
    tone === 'income'
      ? 'from-emerald-500/15 to-emerald-100/60 text-emerald-700'
      : tone === 'expense'
        ? 'from-rose-500/15 to-rose-100/60 text-rose-700'
        : tone === 'profit'
          ? 'from-sky-500/15 to-sky-100/60 text-sky-700'
          : 'from-amber-500/15 to-amber-100/60 text-amber-700';

  return (
    <div className="rounded-3xl border border-white/70 bg-white/90 p-5 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex items-start justify-between gap-4">
        <div>
          <div className="text-sm font-medium text-slate-500">{title}</div>
          <div className="mt-3 text-2xl font-semibold tracking-tight text-slate-950">{value}</div>
        </div>
        <div className={`inline-flex h-11 w-11 items-center justify-center rounded-2xl bg-gradient-to-br ${toneClasses}`}>
          <Icon className="h-5 w-5" />
        </div>
      </div>
      <div className="mt-4 text-sm text-slate-500">{subtitle}</div>
    </div>
  );
}

function StatusPanel({
  title,
  totalCount,
  totalAmount,
  byStatus,
}: {
  title: string;
  totalCount: number;
  totalAmount: number;
  byStatus: Array<{
    key: 'Completed' | 'Pending' | 'Overdue';
    percent: number;
    count: number;
    amount: number;
  }>;
}) {
  const trackedAmount = byStatus.reduce((sum, row) => sum + row.amount, 0);
  const hasOriginalVolume = Math.abs(totalAmount - trackedAmount) > 0.01;

  return (
    <section className="rounded-3xl border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-3 border-b border-slate-100 pb-5 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h3 className="text-lg font-semibold text-slate-950">{title}</h3>
          <p className="mt-1 text-sm text-slate-500">
            Распределение фактически оплаченной суммы и остатка по статусам за выбранный период.
          </p>
        </div>
        <div className="text-right">
          <div className="text-2xl font-semibold text-slate-950">{formatCurrency(trackedAmount)}</div>
          <div className="text-sm text-slate-500">
            {totalCount} операций
            {hasOriginalVolume ? ` · начислено ${formatCurrency(totalAmount)}` : ''}
          </div>
        </div>
      </div>

      <div className="mt-5 space-y-4">
        {byStatus.map((row) => {
          const meta = statusMeta[row.key];

          return (
            <div key={row.key} className="rounded-2xl border border-slate-100 bg-slate-50/70 p-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex items-center gap-3">
                  <span className={`inline-flex rounded-full px-3 py-1 text-xs font-semibold ${meta.chip}`}>
                    {meta.label}
                  </span>
                  <span className="text-sm text-slate-500">{row.count} шт.</span>
                </div>
                <div className="text-right">
                  <div className="font-semibold text-slate-900">{formatCurrency(row.amount)}</div>
                  <div className="text-xs text-slate-500">{row.percent}% по количеству</div>
                </div>
              </div>
              <div className="mt-3 h-2.5 overflow-hidden rounded-full bg-white">
                <div
                  className="h-full rounded-full transition-all"
                  style={{
                    width: `${Math.max(row.percent, row.count > 0 ? 6 : 0)}%`,
                    backgroundColor: meta.color,
                  }}
                />
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function CashFlowReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: CashFlowReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const topAccounts = (report?.byAccount ?? []).slice(0, 6);
  const recentDays = (report?.days ?? []).slice(-8);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-emerald-100 bg-emerald-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-emerald-700">
            <Wallet className="h-3.5 w-3.5" />
            ДДС
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет движения денежных средств</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактические денежные операции за период: начальный остаток, поступления, списания, чистый поток и конечный остаток.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета движения денежных средств...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет ДДС</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="Начальный остаток"
              value={formatCurrency(report.openingBalance)}
              icon={Wallet}
              tone="rate"
              subtitle="На начало выбранного периода"
            />
            <MetricCard
              title="Поступления"
              value={formatCurrency(report.income)}
              icon={TrendingUp}
              tone="income"
              subtitle={`Фактических приходов: ${report.incomeCount}`}
            />
            <MetricCard
              title="Списания"
              value={formatCurrency(report.expense)}
              icon={TrendingDown}
              tone="expense"
              subtitle={`Фактических расходов: ${report.expenseCount}`}
            />
            <MetricCard
              title="Чистый поток"
              value={formatCurrency(report.netCashFlow)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netCashFlow >= 0 ? 'Приток за период' : 'Отток за период'}
            />
            <MetricCard
              title="Конечный остаток"
              value={formatCurrency(report.closingBalance)}
              icon={Banknote}
              tone="rate"
              subtitle="На конец выбранного периода"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-6 xl:grid-cols-[1.1fr_0.9fr]">
            <div className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
              <div className="flex items-center justify-between gap-4">
                <div>
                  <h3 className="text-lg font-semibold text-slate-950">Динамика по дням</h3>
                  <p className="mt-1 text-sm text-slate-500">Последние дни периода с движением денег.</p>
                </div>
                <div className="text-sm text-slate-500">{report.days.length} дней</div>
              </div>
              <div className="mt-5 space-y-3">
                {recentDays.length > 0 ? recentDays.map((day) => (
                  <div key={day.date} className="grid gap-3 rounded-2xl bg-white p-4 shadow-sm sm:grid-cols-[110px_1fr_1fr_1fr] sm:items-center">
                    <div className="text-sm font-semibold text-slate-900">{formatDate(day.date)}</div>
                    <div>
                      <div className="text-xs text-slate-500">Поступило</div>
                      <div className="font-semibold text-emerald-700">{formatCurrency(day.income)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-slate-500">Списано</div>
                      <div className="font-semibold text-rose-700">{formatCurrency(day.expense)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-slate-500">Остаток</div>
                      <div className="font-semibold text-slate-950">{formatCurrency(day.closingBalance)}</div>
                    </div>
                  </div>
                )) : (
                  <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                    В выбранном периоде нет фактических движений.
                  </div>
                )}
              </div>
            </div>

            <div className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
              <div className="flex items-center justify-between gap-4">
                <div>
                  <h3 className="text-lg font-semibold text-slate-950">Счета и источники</h3>
                  <p className="mt-1 text-sm text-slate-500">Крупнейшие остатки и обороты по счетам.</p>
                </div>
                <div className="text-sm text-slate-500">{report.byAccount.length} счетов</div>
              </div>
              <div className="mt-5 space-y-3">
                {topAccounts.length > 0 ? topAccounts.map((row) => (
                  <div key={`${row.source}:${row.accountName}`} className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <div className="text-sm font-semibold text-slate-950">{row.accountName}</div>
                        <div className="mt-1 text-xs text-slate-500">{sourceLabel(row.source)}</div>
                      </div>
                      <div className="text-right">
                        <div className="text-xs text-slate-500">Конечный остаток</div>
                        <div className="font-semibold text-slate-950">{formatCurrency(row.closingBalance)}</div>
                      </div>
                    </div>
                    <div className="mt-3 grid grid-cols-2 gap-3 text-sm">
                      <div className="rounded-xl bg-emerald-50 px-3 py-2 text-emerald-700">
                        + {formatCurrency(row.income)}
                      </div>
                      <div className="rounded-xl bg-rose-50 px-3 py-2 text-rose-700">
                        - {formatCurrency(row.expense)}
                      </div>
                    </div>
                  </div>
                )) : (
                  <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                    Нет счетов с движением за период.
                  </div>
                )}
              </div>
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function CashReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: CashBookSummary | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const recentDays = (report?.days ?? []).slice(-8);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-amber-100 bg-amber-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-amber-700">
            <Banknote className="h-3.5 w-3.5" />
            Касса
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по кассе</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Кассовая книга за выбранный период: остатки, приходные и расходные ордера, дневное закрытие кассы.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по кассе...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по кассе</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Остаток на начало"
              value={formatCurrency(report.openingBalance)}
              icon={Wallet}
              tone="rate"
              subtitle={report.accountName ? `Касса: ${report.accountName}` : 'Все кассы'}
            />
            <MetricCard
              title="Приход по кассе"
              value={formatCurrency(report.income)}
              icon={TrendingUp}
              tone="income"
              subtitle={`ПКО за период: ${report.incomingCount}`}
            />
            <MetricCard
              title="Расход по кассе"
              value={formatCurrency(report.expense)}
              icon={TrendingDown}
              tone="expense"
              subtitle={`РКО за период: ${report.outgoingCount}`}
            />
            <MetricCard
              title="Остаток на конец"
              value={formatCurrency(report.closingBalance)}
              icon={Banknote}
              tone="profit"
              subtitle="Контроль закрытия кассового периода"
            />
          </div>

          <div className="mt-6 rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
            <div className="flex flex-col gap-2 sm:flex-row sm:items-end sm:justify-between">
              <div>
                <h3 className="text-lg font-semibold text-slate-950">Дневное движение кассы</h3>
                <p className="mt-1 text-sm text-slate-500">Последние дни выбранного периода с приходом, расходом и закрывающим остатком.</p>
              </div>
              <div className="text-sm text-slate-500">{report.days.length} дней</div>
            </div>

            <div className="mt-5 overflow-hidden rounded-2xl border border-slate-100 bg-white">
              {recentDays.length > 0 ? (
                <div className="divide-y divide-slate-100">
                  {recentDays.map((day) => (
                    <div key={day.date} className="grid gap-3 px-4 py-3 text-sm sm:grid-cols-[110px_1fr_1fr_1fr_1fr] sm:items-center">
                      <div className="font-semibold text-slate-900">{formatDate(day.date)}</div>
                      <div>
                        <div className="text-xs text-slate-500">Открытие</div>
                        <div className="font-medium text-slate-900">{formatCurrency(day.openingBalance)}</div>
                      </div>
                      <div>
                        <div className="text-xs text-slate-500">Приход</div>
                        <div className="font-medium text-emerald-700">{formatCurrency(day.income)}</div>
                      </div>
                      <div>
                        <div className="text-xs text-slate-500">Расход</div>
                        <div className="font-medium text-rose-700">{formatCurrency(day.expense)}</div>
                      </div>
                      <div>
                        <div className="text-xs text-slate-500">Закрытие</div>
                        <div className="font-semibold text-slate-950">{formatCurrency(day.closingBalance)}</div>
                      </div>
                    </div>
                  ))}
                </div>
              ) : (
                <div className="p-8 text-center text-sm text-slate-500">
                  В выбранном периоде нет проведенных кассовых ордеров.
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function BankReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: CashFlowReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const recentDays = (report?.days ?? []).slice(-8);
  const accounts = report?.byAccount ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-sky-100 bg-sky-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-sky-700">
            <Banknote className="h-3.5 w-3.5" />
            Банк
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по банку</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Банковские фактические операции отдельно от других источников: остаток, поступления, списания и движение по расчетным счетам.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по банку...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по банку</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Банк на начало"
              value={formatCurrency(report.openingBalance)}
              icon={Wallet}
              tone="rate"
              subtitle="Остаток по банковским операциям"
            />
            <MetricCard
              title="Поступило на банк"
              value={formatCurrency(report.income)}
              icon={TrendingUp}
              tone="income"
              subtitle={`Банковских приходов: ${report.incomeCount}`}
            />
            <MetricCard
              title="Списано с банка"
              value={formatCurrency(report.expense)}
              icon={TrendingDown}
              tone="expense"
              subtitle={`Банковских расходов: ${report.expenseCount}`}
            />
            <MetricCard
              title="Банк на конец"
              value={formatCurrency(report.closingBalance)}
              icon={Banknote}
              tone="profit"
              subtitle={report.netCashFlow >= 0 ? 'Положительный поток' : 'Отрицательный поток'}
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-6 xl:grid-cols-[0.9fr_1.1fr]">
            <div className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
              <div className="flex items-center justify-between gap-4">
                <div>
                  <h3 className="text-lg font-semibold text-slate-950">Расчетные счета</h3>
                  <p className="mt-1 text-sm text-slate-500">Остатки и обороты по банковским счетам.</p>
                </div>
                <div className="text-sm text-slate-500">{accounts.length} счетов</div>
              </div>
              <div className="mt-5 space-y-3">
                {accounts.length > 0 ? accounts.map((row) => (
                  <div key={row.accountName} className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <div className="font-semibold text-slate-950">{row.accountName}</div>
                        <div className="mt-1 text-xs text-slate-500">Чистый поток: {formatCurrency(row.netCashFlow)}</div>
                      </div>
                      <div className="text-right">
                        <div className="text-xs text-slate-500">Остаток</div>
                        <div className="font-semibold text-slate-950">{formatCurrency(row.closingBalance)}</div>
                      </div>
                    </div>
                  </div>
                )) : (
                  <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                    Нет банковских счетов с движением за период.
                  </div>
                )}
              </div>
            </div>

            <div className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
              <div className="flex items-center justify-between gap-4">
                <div>
                  <h3 className="text-lg font-semibold text-slate-950">Дневное движение банка</h3>
                  <p className="mt-1 text-sm text-slate-500">Последние банковские дни с фактическим движением.</p>
                </div>
                <div className="text-sm text-slate-500">{report.days.length} дней</div>
              </div>
              <div className="mt-5 space-y-3">
                {recentDays.length > 0 ? recentDays.map((day) => (
                  <div key={day.date} className="grid gap-3 rounded-2xl bg-white p-4 shadow-sm sm:grid-cols-[110px_1fr_1fr_1fr] sm:items-center">
                    <div className="font-semibold text-slate-900">{formatDate(day.date)}</div>
                    <div>
                      <div className="text-xs text-slate-500">Приход</div>
                      <div className="font-semibold text-emerald-700">{formatCurrency(day.income)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-slate-500">Расход</div>
                      <div className="font-semibold text-rose-700">{formatCurrency(day.expense)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-slate-500">Остаток</div>
                      <div className="font-semibold text-slate-950">{formatCurrency(day.closingBalance)}</div>
                    </div>
                  </div>
                )) : (
                  <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                    В выбранном периоде нет банковских движений.
                  </div>
                )}
              </div>
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function CategoryReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: MoneyCategoryReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const renderRows = (rows: MoneyCategoryReport['income'], tone: 'income' | 'expense') => (
    <div className="space-y-3">
      {rows.length > 0 ? rows.slice(0, 8).map((row) => (
        <div key={`${tone}:${row.categoryName}`} className="rounded-2xl border border-slate-100 bg-white p-4 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <div className="font-semibold text-slate-950">{row.categoryName}</div>
              <div className="mt-1 text-xs text-slate-500">{row.count} операций · {row.share}%</div>
            </div>
            <div className={`font-semibold ${tone === 'income' ? 'text-emerald-700' : 'text-rose-700'}`}>
              {formatCurrency(row.amount)}
            </div>
          </div>
          <div className="mt-3 h-2 overflow-hidden rounded-full bg-slate-100">
            <div
              className={`h-full rounded-full ${tone === 'income' ? 'bg-emerald-500' : 'bg-rose-500'}`}
              style={{ width: `${Math.max(row.share, row.amount > 0 ? 5 : 0)}%` }}
            />
          </div>
        </div>
      )) : (
        <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
          Нет операций по статьям за период.
        </div>
      )}
    </div>
  );

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-violet-100 bg-violet-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-violet-700">
            <ArrowUpRight className="h-3.5 w-3.5" />
            Статьи
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по статьям доходов и расходов</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Группировка фактического движения денег по статьям: видно, какие направления дают основной приход и расход.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по статьям...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по статьям</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-3">
            <MetricCard
              title="Доходы по статьям"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle={`Статей доходов: ${report.income.length}`}
            />
            <MetricCard
              title="Расходы по статьям"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle={`Статей расходов: ${report.expense.length}`}
            />
            <MetricCard
              title="Итог по статьям"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Доходы выше расходов' : 'Расходы выше доходов'}
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-6 xl:grid-cols-2">
            <div className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
              <h3 className="text-lg font-semibold text-slate-950">Статьи доходов</h3>
              <div className="mt-5">{renderRows(report.income, 'income')}</div>
            </div>
            <div className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
              <h3 className="text-lg font-semibold text-slate-950">Статьи расходов</h3>
              <div className="mt-5">{renderRows(report.expense, 'expense')}</div>
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function CounterpartyReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: CounterpartyReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.counterparties ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-cyan-100 bg-cyan-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-cyan-700">
            <Handshake className="h-3.5 w-3.5" />
            Контрагенты
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по контрагентам</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сводка фактических поступлений и списаний по каждому контрагенту с долей в общем обороте периода.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по контрагентам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по контрагентам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По всем контрагентам"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="По всем контрагентам"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Положительный денежный итог' : 'Отрицательный денежный итог'}
            />
            <MetricCard
              title="Контрагентов"
              value={String(report.counterparties.length)}
              icon={Handshake}
              tone="rate"
              subtitle="С фактическим движением денег"
            />
          </div>

          <div className="mt-6 rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
            <div className="flex flex-wrap items-end justify-between gap-3">
              <div>
                <h3 className="text-lg font-semibold text-slate-950">Крупнейшие контрагенты</h3>
                <p className="mt-1 text-sm text-slate-500">Сортировка по суммарному обороту: поступления плюс списания.</p>
              </div>
              <div className="text-sm text-slate-500">Топ {rows.length}</div>
            </div>

            <div className="mt-5 space-y-3">
              {rows.length > 0 ? rows.map((row) => (
                <div key={row.counterpartyName} className="rounded-2xl border border-slate-100 bg-white p-4 shadow-sm">
                  <div className="flex flex-wrap items-start justify-between gap-4">
                    <div className="min-w-0">
                      <div className="truncate font-semibold text-slate-950">{row.counterpartyName}</div>
                      <div className="mt-1 text-xs text-slate-500">
                        {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                      </div>
                    </div>
                    <div className="text-right">
                      <div className="text-xs text-slate-500">Оборот</div>
                      <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                    </div>
                  </div>

                  <div className="mt-4 grid grid-cols-1 gap-3 md:grid-cols-3">
                    <div className="rounded-xl bg-emerald-50 px-3 py-2">
                      <div className="text-xs text-emerald-700">Поступило</div>
                      <div className="font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                    </div>
                    <div className="rounded-xl bg-rose-50 px-3 py-2">
                      <div className="text-xs text-rose-700">Списано</div>
                      <div className="font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                    </div>
                    <div className="rounded-xl bg-sky-50 px-3 py-2">
                      <div className="text-xs text-sky-700">Итог</div>
                      <div className="font-semibold text-sky-800">{formatCurrency(row.netTotal)}</div>
                    </div>
                  </div>

                  <div className="mt-3 h-2 overflow-hidden rounded-full bg-slate-100">
                    <div
                      className="h-full rounded-full bg-cyan-500"
                      style={{ width: `${Math.max(row.share, row.totalAmount > 0 ? 5 : 0)}%` }}
                    />
                  </div>
                </div>
              )) : (
                <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                  Нет операций по контрагентам за период.
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function ProjectReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: ProjectReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.projects ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-lime-100 bg-lime-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-lime-700">
            <FolderKanban className="h-3.5 w-3.5" />
            Проекты
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по проектам</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактическая денежная результативность проектов: поступления, списания, итог и вклад в оборот периода.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по проектам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по проектам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="Фактические приходы проектов"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="Фактические расходы проектов"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Проекты в плюсе' : 'Проекты в минусе'}
            />
            <MetricCard
              title="Проектов"
              value={String(report.projects.length)}
              icon={FolderKanban}
              tone="rate"
              subtitle="С фактическими операциями"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-2">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.projectName} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex flex-wrap items-start justify-between gap-4">
                  <div className="min-w-0">
                    <h3 className="truncate text-lg font-semibold text-slate-950">{row.projectName}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs uppercase tracking-wide text-slate-500">Итог</div>
                    <div className="text-lg font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                  </div>
                </div>

                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Поступления</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Списания</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                  </div>
                </div>

                <div className="mt-4 h-2 overflow-hidden rounded-full bg-white">
                  <div
                    className="h-full rounded-full bg-lime-500"
                    style={{ width: `${Math.max(row.share, row.totalAmount > 0 ? 5 : 0)}%` }}
                  />
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-2">
                Нет операций по проектам за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function DirectionReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: DirectionReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.directions ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-fuchsia-100 bg-fuchsia-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-fuchsia-700">
            <Compass className="h-3.5 w-3.5" />
            Направления
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по направлениям</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Разрез фактических денежных операций по направлениям бизнеса: доходы, расходы, итог и доля в обороте.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по направлениям...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по направлениям</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По направлениям бизнеса"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="Расходы направлений"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Доходы направлений выше расходов' : 'Расходы направлений выше доходов'}
            />
            <MetricCard
              title="Направлений"
              value={String(report.directions.length)}
              icon={Compass}
              tone="rate"
              subtitle="С фактическим движением"
            />
          </div>

          <div className="mt-6 rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
            <div className="flex flex-wrap items-end justify-between gap-3">
              <div>
                <h3 className="text-lg font-semibold text-slate-950">Структура направлений</h3>
                <p className="mt-1 text-sm text-slate-500">Сначала направления с самым большим оборотом за период.</p>
              </div>
              <div className="text-sm text-slate-500">Топ {rows.length}</div>
            </div>

            <div className="mt-5 space-y-3">
              {rows.length > 0 ? rows.map((row) => (
                <div key={row.directionName} className="rounded-2xl border border-slate-100 bg-white p-4 shadow-sm">
                  <div className="grid gap-4 lg:grid-cols-[1fr_160px_160px_160px] lg:items-center">
                    <div className="min-w-0">
                      <div className="truncate font-semibold text-slate-950">{row.directionName}</div>
                      <div className="mt-1 text-xs text-slate-500">
                        {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                      </div>
                    </div>
                    <div>
                      <div className="text-xs text-emerald-700">Поступило</div>
                      <div className="font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-rose-700">Списано</div>
                      <div className="font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-slate-500">Итог</div>
                      <div className="font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                    </div>
                  </div>
                  <div className="mt-3 h-2 overflow-hidden rounded-full bg-slate-100">
                    <div
                      className="h-full rounded-full bg-fuchsia-500"
                      style={{ width: `${Math.max(row.share, row.totalAmount > 0 ? 5 : 0)}%` }}
                    />
                  </div>
                </div>
              )) : (
                <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                  Нет операций по направлениям за период.
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function SalesPointReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: SalesPointReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.salesPoints ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-orange-100 bg-orange-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-orange-700">
            <Store className="h-3.5 w-3.5" />
            Точки продаж
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по точкам продаж</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Денежная сводка по точкам продаж и каналам: какие точки дают основной оборот, приход и расход.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по точкам продаж...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по точкам продаж</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По всем точкам продаж"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="Расходы точек продаж"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Положительный итог' : 'Отрицательный итог'}
            />
            <MetricCard
              title="Точек"
              value={String(report.salesPoints.length)}
              icon={Store}
              tone="rate"
              subtitle="С фактическими операциями"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.salesPointName} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div className="min-w-0">
                    <h3 className="truncate text-lg font-semibold text-slate-950">{row.salesPointName}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.incomeCount + row.expenseCount} операций
                    </div>
                  </div>
                  <div className="rounded-2xl bg-white px-3 py-2 text-sm font-semibold text-orange-700 shadow-sm">
                    {row.share}%
                  </div>
                </div>

                <div className="mt-5 space-y-3">
                  <div className="flex items-center justify-between rounded-2xl bg-white px-4 py-3 shadow-sm">
                    <span className="text-sm text-slate-500">Поступило</span>
                    <span className="font-semibold text-emerald-700">{formatCurrency(row.income)}</span>
                  </div>
                  <div className="flex items-center justify-between rounded-2xl bg-white px-4 py-3 shadow-sm">
                    <span className="text-sm text-slate-500">Списано</span>
                    <span className="font-semibold text-rose-700">{formatCurrency(row.expense)}</span>
                  </div>
                  <div className="flex items-center justify-between rounded-2xl bg-white px-4 py-3 shadow-sm">
                    <span className="text-sm text-slate-500">Итог</span>
                    <span className="font-semibold text-slate-950">{formatCurrency(row.netTotal)}</span>
                  </div>
                </div>

                <div className="mt-4 h-2 overflow-hidden rounded-full bg-white">
                  <div
                    className="h-full rounded-full bg-orange-500"
                    style={{ width: `${Math.max(row.share, row.totalAmount > 0 ? 5 : 0)}%` }}
                  />
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 md:col-span-2 xl:col-span-3">
                Нет операций по точкам продаж за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function VatReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: VatReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.rates ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-teal-100 bg-teal-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-teal-700">
            <Percent className="h-3.5 w-3.5" />
            НДС
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по НДС</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сводка фактических операций по ставкам НДС: начисленный налог с поступлений, входящий НДС по расходам и итог к уплате или возмещению.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по НДС...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по НДС</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="Поступления с НДС"
              value={formatCurrency(report.incomeAmountTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle={`НДС начислен: ${formatCurrency(report.incomeVatTotal)}`}
            />
            <MetricCard
              title="Расходы с НДС"
              value={formatCurrency(report.expenseAmountTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle={`Входящий НДС: ${formatCurrency(report.expenseVatTotal)}`}
            />
            <MetricCard
              title="Баланс НДС"
              value={formatCurrency(report.vatBalance)}
              icon={Percent}
              tone={report.vatBalance >= 0 ? 'profit' : 'rate'}
              subtitle={report.vatBalance >= 0 ? 'К уплате по операциям' : 'К возмещению по операциям'}
            />
            <MetricCard
              title="База доходов"
              value={formatCurrency(report.incomeAmountTotal - report.incomeVatTotal)}
              icon={Wallet}
              tone="income"
              subtitle="Сумма без НДС"
            />
            <MetricCard
              title="База расходов"
              value={formatCurrency(report.expenseAmountTotal - report.expenseVatTotal)}
              icon={Banknote}
              tone="expense"
              subtitle="Сумма без НДС"
            />
          </div>

          <div className="mt-6 rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
            <div className="flex flex-wrap items-end justify-between gap-3">
              <div>
                <h3 className="text-lg font-semibold text-slate-950">Ставки НДС</h3>
                <p className="mt-1 text-sm text-slate-500">Разделение базы и налога по каждой ставке.</p>
              </div>
              <div className="text-sm text-slate-500">{rows.length} ставок</div>
            </div>

            <div className="mt-5 space-y-3">
              {rows.length > 0 ? rows.map((row) => (
                <div key={row.vatRateLabel} className="rounded-2xl border border-slate-100 bg-white p-4 shadow-sm">
                  <div className="flex flex-wrap items-start justify-between gap-4">
                    <div>
                      <div className="text-lg font-semibold text-slate-950">{row.vatRateLabel}</div>
                      <div className="mt-1 text-xs text-slate-500">
                        {row.incomeCount + row.expenseCount} операций
                      </div>
                    </div>
                    <div className="text-right">
                      <div className="text-xs text-slate-500">Баланс НДС</div>
                      <div className="font-semibold text-slate-950">{formatCurrency(row.netVat)}</div>
                    </div>
                  </div>

                  <div className="mt-4 grid grid-cols-1 gap-3 lg:grid-cols-3">
                    <div className="rounded-2xl bg-emerald-50 px-4 py-3">
                      <div className="text-xs text-emerald-700">Поступления</div>
                      <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.incomeGross)}</div>
                      <div className="mt-1 text-xs text-emerald-700">
                        база {formatCurrency(row.incomeBase)} · НДС {formatCurrency(row.incomeVat)}
                      </div>
                    </div>
                    <div className="rounded-2xl bg-rose-50 px-4 py-3">
                      <div className="text-xs text-rose-700">Расходы</div>
                      <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expenseGross)}</div>
                      <div className="mt-1 text-xs text-rose-700">
                        база {formatCurrency(row.expenseBase)} · НДС {formatCurrency(row.expenseVat)}
                      </div>
                    </div>
                    <div className="rounded-2xl bg-teal-50 px-4 py-3">
                      <div className="text-xs text-teal-700">Налоговый итог</div>
                      <div className="mt-1 font-semibold text-teal-800">{formatCurrency(row.netVat)}</div>
                      <div className="mt-1 text-xs text-teal-700">
                        начислено минус входящий НДС
                      </div>
                    </div>
                  </div>
                </div>
              )) : (
                <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                  Нет операций с НДС за период.
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function PlanFactReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: PlanFactReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.days ?? []).slice(-10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-indigo-100 bg-indigo-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-indigo-700">
            <Target className="h-3.5 w-3.5" />
            План-факт
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">План-факт отчет</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сравнение плановых и фактических денежных операций: выполнение доходов, превышение расходов и итоговое отклонение.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка план-факт отчета...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить план-факт отчет</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="План доходов"
              value={formatCurrency(report.planIncome)}
              icon={Target}
              tone="rate"
              subtitle={`Факт: ${formatCurrency(report.factIncome)}`}
            />
            <MetricCard
              title="Выполнение доходов"
              value={`${report.incomeCompletionPercent}%`}
              icon={TrendingUp}
              tone="income"
              subtitle={`Отклонение: ${formatCurrency(report.incomeVariance)}`}
            />
            <MetricCard
              title="План расходов"
              value={formatCurrency(report.planExpense)}
              icon={Target}
              tone="rate"
              subtitle={`Факт: ${formatCurrency(report.factExpense)}`}
            />
            <MetricCard
              title="Исполнение расходов"
              value={`${report.expenseExecutionPercent}%`}
              icon={TrendingDown}
              tone="expense"
              subtitle={`Отклонение: ${formatCurrency(report.expenseVariance)}`}
            />
            <MetricCard
              title="Итоговое отклонение"
              value={formatCurrency(report.netVariance)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={`План ${formatCurrency(report.planNetTotal)} · факт ${formatCurrency(report.factNetTotal)}`}
            />
          </div>

          <div className="mt-6 rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
            <div className="flex flex-wrap items-end justify-between gap-3">
              <div>
                <h3 className="text-lg font-semibold text-slate-950">Динамика по дням</h3>
                <p className="mt-1 text-sm text-slate-500">Последние дни периода, где были плановые или фактические операции.</p>
              </div>
              <div className="text-sm text-slate-500">{report.days.length} дней</div>
            </div>

            <div className="mt-5 space-y-3">
              {rows.length > 0 ? rows.map((day) => (
                <div key={day.date} className="grid gap-3 rounded-2xl bg-white p-4 shadow-sm lg:grid-cols-[110px_1fr_1fr_1fr] lg:items-center">
                  <div className="font-semibold text-slate-950">{formatDate(day.date)}</div>
                  <div>
                    <div className="text-xs text-slate-500">Доходы</div>
                    <div className="text-sm font-semibold text-emerald-700">
                      {formatCurrency(day.factIncome)} / {formatCurrency(day.planIncome)}
                    </div>
                    <div className="text-xs text-slate-500">{formatCurrency(day.incomeVariance)}</div>
                  </div>
                  <div>
                    <div className="text-xs text-slate-500">Расходы</div>
                    <div className="text-sm font-semibold text-rose-700">
                      {formatCurrency(day.factExpense)} / {formatCurrency(day.planExpense)}
                    </div>
                    <div className="text-xs text-slate-500">{formatCurrency(day.expenseVariance)}</div>
                  </div>
                  <div>
                    <div className="text-xs text-slate-500">Итог</div>
                    <div className="text-sm font-semibold text-slate-950">
                      {formatCurrency(day.factNetTotal)} / {formatCurrency(day.planNetTotal)}
                    </div>
                    <div className="text-xs text-slate-500">{formatCurrency(day.netVariance)}</div>
                  </div>
                </div>
              )) : (
                <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                  Нет плановых или фактических операций за период.
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function AdvanceReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: AdvanceReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.counterparties ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-amber-100 bg-amber-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-amber-700">
            <Clock3 className="h-3.5 w-3.5" />
            Авансы
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по авансам</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактические авансовые платежи за период: полученные и выданные авансы, признанные суммы и остаток к признанию.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по авансам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по авансам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Полученные авансы"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="Авансовые поступления"
            />
            <MetricCard
              title="Выданные авансы"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="Авансовые списания"
            />
            <MetricCard
              title="Признано"
              value={formatCurrency(report.recognizedTotal)}
              icon={CheckCircle2}
              tone="profit"
              subtitle={`${report.recognizedCount} операций`}
            />
            <MetricCard
              title="К признанию"
              value={formatCurrency(report.unrecognizedTotal)}
              icon={Clock3}
              tone="rate"
              subtitle={`${report.unrecognizedCount} операций`}
            />
          </div>

          <div className="mt-6 rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
            <div className="flex flex-wrap items-end justify-between gap-3">
              <div>
                <h3 className="text-lg font-semibold text-slate-950">Контрагенты по авансам</h3>
                <p className="mt-1 text-sm text-slate-500">Сортировка по общему объему авансовых операций.</p>
              </div>
              <div className="text-sm text-slate-500">{report.counterparties.length} контрагентов</div>
            </div>

            <div className="mt-5 space-y-3">
              {rows.length > 0 ? rows.map((row) => (
                <div key={row.counterpartyName} className="rounded-2xl border border-slate-100 bg-white p-4 shadow-sm">
                  <div className="flex flex-wrap items-start justify-between gap-4">
                    <div className="min-w-0">
                      <div className="truncate font-semibold text-slate-950">{row.counterpartyName}</div>
                      <div className="mt-1 text-xs text-slate-500">
                        {row.incomeCount + row.expenseCount} авансовых операций
                      </div>
                    </div>
                    <div className="text-right">
                      <div className="text-xs text-slate-500">К признанию</div>
                      <div className="font-semibold text-amber-700">{formatCurrency(row.unrecognizedTotal)}</div>
                    </div>
                  </div>

                  <div className="mt-4 grid grid-cols-1 gap-3 md:grid-cols-4">
                    <div className="rounded-xl bg-emerald-50 px-3 py-2">
                      <div className="text-xs text-emerald-700">Получено</div>
                      <div className="font-semibold text-emerald-800">{formatCurrency(row.incomeTotal)}</div>
                    </div>
                    <div className="rounded-xl bg-rose-50 px-3 py-2">
                      <div className="text-xs text-rose-700">Выдано</div>
                      <div className="font-semibold text-rose-800">{formatCurrency(row.expenseTotal)}</div>
                    </div>
                    <div className="rounded-xl bg-sky-50 px-3 py-2">
                      <div className="text-xs text-sky-700">Признано</div>
                      <div className="font-semibold text-sky-800">{formatCurrency(row.recognizedTotal)}</div>
                    </div>
                    <div className="rounded-xl bg-amber-50 px-3 py-2">
                      <div className="text-xs text-amber-700">Осталось</div>
                      <div className="font-semibold text-amber-800">{formatCurrency(row.unrecognizedTotal)}</div>
                    </div>
                  </div>
                </div>
              )) : (
                <div className="rounded-2xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-500">
                  Нет авансовых операций за период.
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function PaymentPriorityReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: PaymentPriorityReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.priorities ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-red-100 bg-red-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-red-700">
            <Flag className="h-3.5 w-3.5" />
            Приоритеты
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по приоритетам платежей</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактические поступления и списания по приоритетам: видно, какой объем денег прошел по срочным и обычным платежам.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по приоритетам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по приоритетам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="Фактические приходы"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="Фактические расходы"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Приход выше расхода' : 'Расход выше прихода'}
            />
            <MetricCard
              title="Групп"
              value={String(report.priorities.length)}
              icon={Flag}
              tone="rate"
              subtitle="Приоритетных групп"
            />
          </div>

          <div className="mt-6 space-y-3">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.priorityLabel} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex flex-wrap items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.priorityLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="rounded-2xl bg-white px-4 py-3 text-right shadow-sm">
                    <div className="text-xs text-slate-500">Итог</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                  </div>
                </div>

                <div className="mt-4 grid grid-cols-1 gap-3 md:grid-cols-3">
                  <div className="rounded-2xl bg-white px-4 py-3 shadow-sm">
                    <div className="text-xs text-emerald-700">Поступления</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                  </div>
                  <div className="rounded-2xl bg-white px-4 py-3 shadow-sm">
                    <div className="text-xs text-rose-700">Списания</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                  </div>
                  <div className="rounded-2xl bg-white px-4 py-3 shadow-sm">
                    <div className="text-xs text-red-700">Оборот</div>
                    <div className="mt-1 font-semibold text-red-800">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>

                <div className="mt-4 h-2 overflow-hidden rounded-full bg-white">
                  <div
                    className="h-full rounded-full bg-red-500"
                    style={{ width: `${Math.max(row.share, row.totalAmount > 0 ? 5 : 0)}%` }}
                  />
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
                Нет фактических операций с приоритетами за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function PaidAccountReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: PaidAccountReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.accounts ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <CreditCardIcon className="h-3.5 w-3.5" />
            Счета оплаты
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по счетам оплаты</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактические платежи по указанным счетам оплаты: какие счета дают основной оборот, приход и расход.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по счетам оплаты...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по счетам оплаты</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По счетам оплаты"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="По счетам оплаты"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Поступления выше списаний' : 'Списания выше поступлений'}
            />
            <MetricCard
              title="Счетов"
              value={String(report.accounts.length)}
              icon={CreditCardIcon}
              tone="rate"
              subtitle="С фактическими операциями"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-2">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.paidAccountName} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex flex-wrap items-start justify-between gap-4">
                  <div className="min-w-0">
                    <h3 className="truncate text-lg font-semibold text-slate-950">{row.paidAccountName}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Итог</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                  </div>
                </div>

                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Поступления</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Списания</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                  </div>
                </div>

                <div className="mt-4 h-2 overflow-hidden rounded-full bg-white">
                  <div
                    className="h-full rounded-full bg-slate-600"
                    style={{ width: `${Math.max(row.share, row.totalAmount > 0 ? 5 : 0)}%` }}
                  />
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-2">
                Нет операций по счетам оплаты за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function PaidInvoiceReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: PaidInvoiceReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.invoices ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <CheckCircle2 className="h-3.5 w-3.5" />
            Оплаченные счета
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по оплаченным счетам</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактические операции, сгруппированные по номеру оплаченного счета, с отдельным итогом по сумме оплаты.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по оплаченным счетам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по оплаченным счетам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По оплаченным счетам"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="По оплаченным счетам"
            />
            <MetricCard
              title="Оплачено"
              value={formatCurrency(report.paidAmountTotal)}
              icon={CheckCircle2}
              tone="profit"
              subtitle="Сумма оплат по счетам"
            />
            <MetricCard
              title="Счетов"
              value={String(report.invoices.length)}
              icon={CreditCardIcon}
              tone="rate"
              subtitle="С фактическими оплатами"
            />
          </div>

          <div className="mt-6 overflow-hidden rounded-3xl border border-slate-100 bg-white">
            <div className="grid grid-cols-[minmax(180px,1.3fr)_repeat(4,minmax(120px,1fr))] gap-3 border-b border-slate-100 bg-slate-50 px-4 py-3 text-xs font-semibold uppercase tracking-[0.12em] text-slate-500 max-xl:hidden">
              <div>Счет</div>
              <div className="text-right">Операции</div>
              <div className="text-right">Оплачено</div>
              <div className="text-right">Итог</div>
              <div className="text-right">Доля</div>
            </div>
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.paidInvoiceNumber} className="grid gap-3 border-b border-slate-100 px-4 py-4 last:border-b-0 xl:grid-cols-[minmax(180px,1.3fr)_repeat(4,minmax(120px,1fr))] xl:items-center">
                <div className="min-w-0">
                  <div className="truncate font-semibold text-slate-950">{row.paidInvoiceNumber}</div>
                  <div className="mt-1 text-sm text-slate-500">{row.incomeCount + row.expenseCount} операций</div>
                </div>
                <div className="flex justify-between gap-3 text-sm xl:block xl:text-right">
                  <span className="text-slate-500 xl:hidden">Операции</span>
                  <span className="font-medium text-slate-800">{formatCurrency(row.operationTotal)}</span>
                </div>
                <div className="flex justify-between gap-3 text-sm xl:block xl:text-right">
                  <span className="text-slate-500 xl:hidden">Оплачено</span>
                  <span className="font-medium text-emerald-700">{formatCurrency(row.paidAmountTotal)}</span>
                </div>
                <div className="flex justify-between gap-3 text-sm xl:block xl:text-right">
                  <span className="text-slate-500 xl:hidden">Итог</span>
                  <span className="font-medium text-slate-950">{formatCurrency(row.netTotal)}</span>
                </div>
                <div className="flex justify-between gap-3 text-sm xl:block xl:text-right">
                  <span className="text-slate-500 xl:hidden">Доля</span>
                  <span className="font-medium text-slate-700">{row.share}%</span>
                </div>
              </div>
            )) : (
              <div className="p-8 text-center text-sm text-slate-500">
                Нет операций по оплаченным счетам за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function InvoiceStatusReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: InvoiceStatusReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-violet-100 bg-violet-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-violet-700">
            <CreditCardIcon className="h-3.5 w-3.5" />
            Статусы счетов
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по статусам счетов</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сводка счетов по этапам документооборота: сумма, оплачено, остаток, просрочка и распределение входящих и исходящих счетов.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по статусам счетов...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по статусам счетов</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="Счетов"
              value={String(report.invoiceCount)}
              icon={CreditCardIcon}
              tone="rate"
              subtitle={`${report.incomeCount} вход. · ${report.expenseCount} исход.`}
            />
            <MetricCard
              title="Сумма"
              value={formatCurrency(report.totalAmount)}
              icon={Banknote}
              tone="rate"
              subtitle="Все счета периода"
            />
            <MetricCard
              title="Оплачено"
              value={formatCurrency(report.paidAmount)}
              icon={CheckCircle2}
              tone="income"
              subtitle={`${report.paidCount} счетов закрыто`}
            />
            <MetricCard
              title="Остаток"
              value={formatCurrency(report.outstandingAmount)}
              icon={Clock3}
              tone="profit"
              subtitle="К оплате по счетам"
            />
            <MetricCard
              title="Просрочено"
              value={String(report.overdueCount)}
              icon={AlertTriangle}
              tone="expense"
              subtitle="Требуют внимания"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-3">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.status} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.statusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.invoiceCount} счетов · {row.share}% суммы
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Сумма</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>
                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Оплачено</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.paidAmount)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Остаток</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.outstandingAmount)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Входящие</div>
                    <div className="mt-1 font-semibold text-slate-900">{row.incomeCount}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Исходящие</div>
                    <div className="mt-1 font-semibold text-slate-900">{row.expenseCount}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-3">
                Нет счетов за выбранный период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function ActStatusReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: ActStatusReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-emerald-100 bg-emerald-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-emerald-700">
            <CheckCircle2 className="h-3.5 w-3.5" />
            Статусы актов
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по статусам актов</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Контроль актов по этапам подписания: создано, передано, подписано, отменено и сумма в каждом статусе.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по статусам актов...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по статусам актов</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="Актов"
              value={String(report.actCount)}
              icon={CheckCircle2}
              tone="rate"
              subtitle={`${report.createdCount} черн. · ${report.transferredCount} на подп.`}
            />
            <MetricCard
              title="Сумма"
              value={formatCurrency(report.totalAmount)}
              icon={Banknote}
              tone="rate"
              subtitle="Все акты периода"
            />
            <MetricCard
              title="Подписано"
              value={formatCurrency(report.signedAmount)}
              icon={CheckCircle2}
              tone="income"
              subtitle={`${report.signedCount} актов`}
            />
            <MetricCard
              title="В работе"
              value={formatCurrency(report.pendingAmount)}
              icon={Clock3}
              tone="profit"
              subtitle="Еще не подписано"
            />
            <MetricCard
              title="Отменено"
              value={String(report.terminatedCount)}
              icon={AlertTriangle}
              tone="expense"
              subtitle="Расторгнутые акты"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.workflowStatus} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.workflowStatusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.actCount} актов · {row.share}% суммы
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Сумма</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>
                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Подписано</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.signedAmount)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-amber-700">В работе</div>
                    <div className="mt-1 font-semibold text-amber-800">{formatCurrency(row.pendingAmount)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Создано</div>
                    <div className="mt-1 font-semibold text-slate-900">{row.createdCount}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Передано</div>
                    <div className="mt-1 font-semibold text-slate-900">{row.transferredCount}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-4">
                Нет актов за выбранный период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function ContractStatusReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: ContractStatusReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-sky-100 bg-sky-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-sky-700">
            <FolderKanban className="h-3.5 w-3.5" />
            Статусы договоров
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по статусам договоров</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сводка договоров по этапам: черновики, подписание, заключенные, расторгнутые и аннулированные договоры.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по статусам договоров...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по статусам договоров</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="Договоров"
              value={String(report.contractCount)}
              icon={FolderKanban}
              tone="rate"
              subtitle={`${report.draftCount} черн. · ${report.onSigningCount} на подп.`}
            />
            <MetricCard
              title="Сумма"
              value={formatCurrency(report.totalAmount)}
              icon={Banknote}
              tone="rate"
              subtitle="Все договоры периода"
            />
            <MetricCard
              title="Активно"
              value={formatCurrency(report.activeAmount)}
              icon={CheckCircle2}
              tone="income"
              subtitle={`${report.concludedCount} заключено`}
            />
            <MetricCard
              title="Закрыто"
              value={formatCurrency(report.closedAmount)}
              icon={AlertTriangle}
              tone="expense"
              subtitle={`${report.terminatedCount + report.cancelledCount} закрыто`}
            />
            <MetricCard
              title="Бессрочно"
              value={String(report.openEndedCount)}
              icon={Clock3}
              tone="profit"
              subtitle="Без даты окончания"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.status} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.statusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.contractCount} договоров · {row.share}% суммы
                    </div>
                  </div>
                </div>
                <div className="mt-4 space-y-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Сумма</div>
                    <div className="mt-1 font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <div className="rounded-2xl bg-white p-4 shadow-sm">
                      <div className="text-xs text-sky-700">Бессрочно</div>
                      <div className="mt-1 font-semibold text-sky-800">{row.openEndedCount}</div>
                    </div>
                    <div className="rounded-2xl bg-white p-4 shadow-sm">
                      <div className="text-xs text-amber-700">Истекает</div>
                      <div className="mt-1 font-semibold text-amber-800">{row.expiringCount}</div>
                    </div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-5">
                Нет договоров за выбранный период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function PaymentOrderReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: PaymentOrderReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.orders ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <Banknote className="h-3.5 w-3.5" />
            Платежные поручения
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по платежным поручениям</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактический оборот по номерам платежных поручений, чтобы быстро сверять суммы и операции по платежкам.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по платежным поручениям...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по платежным поручениям</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По платежным поручениям"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="По платежным поручениям"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Поступления выше списаний' : 'Списания выше поступлений'}
            />
            <MetricCard
              title="Платежек"
              value={String(report.orders.length)}
              icon={Banknote}
              tone="rate"
              subtitle="С фактическими операциями"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-2">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.paymentOrderNumber} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex flex-wrap items-start justify-between gap-4">
                  <div className="min-w-0">
                    <h3 className="truncate text-lg font-semibold text-slate-950">{row.paymentOrderNumber}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Оборот</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>

                <div className="mt-4 grid grid-cols-3 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Приход</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Расход</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Итог</div>
                    <div className="mt-1 font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-2">
                Нет операций по платежным поручениям за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function PaymentPurposeReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: PaymentPurposeReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = (report?.purposes ?? []).slice(0, 10);

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <Target className="h-3.5 w-3.5" />
            Назначения платежей
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по назначениям платежей</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактические операции по тексту назначения платежа: помогает увидеть повторяющиеся основания оплат и их оборот.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по назначениям платежей...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по назначениям платежей</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По назначениям"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="По назначениям"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Поступления выше списаний' : 'Списания выше поступлений'}
            />
            <MetricCard
              title="Назначений"
              value={String(report.purposes.length)}
              icon={Target}
              tone="rate"
              subtitle="С фактическими операциями"
            />
          </div>

          <div className="mt-6 overflow-hidden rounded-3xl border border-slate-100 bg-white">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.purpose} className="border-b border-slate-100 p-5 last:border-b-0">
                <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                  <div className="min-w-0">
                    <h3 className="line-clamp-2 text-base font-semibold text-slate-950">{row.purpose}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="grid grid-cols-3 gap-3 text-right sm:min-w-[420px]">
                    <div>
                      <div className="text-xs text-emerald-700">Приход</div>
                      <div className="font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-rose-700">Расход</div>
                      <div className="font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                    </div>
                    <div>
                      <div className="text-xs text-slate-500">Итог</div>
                      <div className="font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                    </div>
                  </div>
                </div>
                <div className="mt-4 h-2 overflow-hidden rounded-full bg-slate-100">
                  <div
                    className="h-full rounded-full bg-slate-700"
                    style={{ width: `${Math.max(row.share, row.totalAmount > 0 ? 5 : 0)}%` }}
                  />
                </div>
              </div>
            )) : (
              <div className="p-8 text-center text-sm text-slate-500">
                Нет операций по назначениям платежей за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function MatchStatusReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: MatchStatusReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <CheckCircle2 className="h-3.5 w-3.5" />
            Сверка операций
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по статусам сверки</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сводка фактических операций по статусам сопоставления со счетами и ручной проверкой.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по статусам сверки...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по статусам сверки</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Операций"
              value={String(report.operationCount)}
              icon={Wallet}
              tone="rate"
              subtitle="В фактическом контуре"
            />
            <MetricCard
              title="Сверено"
              value={String(report.matchedCount)}
              icon={CheckCircle2}
              tone="income"
              subtitle="Связано со счетами"
            />
            <MetricCard
              title="Требуют внимания"
              value={String(report.needsAttentionCount)}
              icon={AlertTriangle}
              tone="expense"
              subtitle="Не сверено или нужно уточнить"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle="По операциям сверки"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-2">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.status} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex flex-wrap items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.statusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.operationCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Оборот</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>

                <div className="mt-4 grid grid-cols-3 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Приход</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Расход</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Итог</div>
                    <div className="mt-1 font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-2">
                Нет операций со статусами сверки за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function JournalStatusReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: JournalStatusReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <Clock3 className="h-3.5 w-3.5" />
            Журнал операций
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по журнальным статусам</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Контроль фактических операций по состоянию учета: проведено, в обработке или требует исправления.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по журнальным статусам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по журнальным статусам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Проведено"
              value={String(report.postedCount)}
              icon={CheckCircle2}
              tone="income"
              subtitle="Операций в учете"
            />
            <MetricCard
              title="В обработке"
              value={String(report.processingCount)}
              icon={Clock3}
              tone="rate"
              subtitle="Ожидают завершения"
            />
            <MetricCard
              title="Ошибок"
              value={String(report.errorCount)}
              icon={AlertTriangle}
              tone="expense"
              subtitle="Нужна проверка"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle="По журнальным статусам"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-3">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.status} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.statusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.operationCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Оборот</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>
                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Приход</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Расход</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-3">
                Нет операций с журнальными статусами за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function CurrencyReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: CurrencyReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.currencies ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <Wallet className="h-3.5 w-3.5" />
            Валюты
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по валютам операций</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Фактический оборот по валютам операций с долей каждой валюты в выбранном периоде.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по валютам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по валютам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Поступило"
              value={formatCurrency(report.incomeTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="Во всех валютах"
            />
            <MetricCard
              title="Списано"
              value={formatCurrency(report.expenseTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="Во всех валютах"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.netTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle={report.netTotal >= 0 ? 'Поступления выше списаний' : 'Списания выше поступлений'}
            />
            <MetricCard
              title="Валют"
              value={String(report.currencies.length)}
              icon={Wallet}
              tone="rate"
              subtitle="С фактическими операциями"
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-2">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.currency} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.currency}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.incomeCount + row.expenseCount} операций · {row.share}% оборота
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Оборот</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>
                <div className="mt-4 grid grid-cols-3 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Приход</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.income)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Расход</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.expense)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Итог</div>
                    <div className="mt-1 font-semibold text-slate-950">{formatCurrency(row.netTotal)}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-2">
                Нет операций по валютам за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function CashOrderReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: CashOrderReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-slate-700">
            <Banknote className="h-3.5 w-3.5" />
            Кассовые ордера
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по кассовым ордерам</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сводка приходных и расходных кассовых ордеров по статусам оформления за выбранный период.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по кассовым ордерам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по кассовым ордерам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            <MetricCard
              title="Приход проведен"
              value={formatCurrency(report.postedIncomingTotal)}
              icon={TrendingUp}
              tone="income"
              subtitle="По проведенным ПКО"
            />
            <MetricCard
              title="Расход проведен"
              value={formatCurrency(report.postedOutgoingTotal)}
              icon={TrendingDown}
              tone="expense"
              subtitle="По проведенным РКО"
            />
            <MetricCard
              title="Итог"
              value={formatCurrency(report.postedNetTotal)}
              icon={ArrowUpRight}
              tone="profit"
              subtitle="Проведенные ордера"
            />
            <MetricCard
              title="Ордеров"
              value={String(report.orderCount)}
              icon={Banknote}
              tone="rate"
              subtitle={`${report.draftCount} черн. · ${report.cancelledCount} отм.`}
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-3">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.status} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.statusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.orderCount} ордеров · {row.share}% оборота
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Оборот</div>
                    <div className="font-semibold text-slate-950">{formatCurrency(row.totalAmount)}</div>
                  </div>
                </div>
                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Приход</div>
                    <div className="mt-1 font-semibold text-emerald-800">{formatCurrency(row.incomingTotal)}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Расход</div>
                    <div className="mt-1 font-semibold text-rose-800">{formatCurrency(row.outgoingTotal)}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-3">
                Нет кассовых ордеров за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function BankStatementImportReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: BankStatementImportReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-indigo-100 bg-indigo-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-indigo-700">
            <FileUp className="h-3.5 w-3.5" />
            Импорт выписок
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по импортам банковских выписок</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Контроль загруженных файлов, созданных операций, ошибок и откатов по журналу импортов за выбранный период.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по импортам банковских выписок...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по импортам выписок</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="Импортов"
              value={String(report.batchCount)}
              icon={FileUp}
              tone="rate"
              subtitle={`${report.appliedCount} применено · ${report.appliedWithErrorsCount} с ошибками`}
            />
            <MetricCard
              title="Прочитано строк"
              value={String(report.rowsReadTotal)}
              icon={Banknote}
              tone="rate"
              subtitle="Всего строк в файлах"
            />
            <MetricCard
              title="Создано операций"
              value={String(report.createdRowsTotal)}
              icon={CheckCircle2}
              tone="income"
              subtitle="Строки, ставшие операциями"
            />
            <MetricCard
              title="Ошибки"
              value={String(report.errorRowsTotal)}
              icon={AlertTriangle}
              tone="expense"
              subtitle={`${report.rollbackBlockedCount} импортов с блокировкой отката`}
            />
            <MetricCard
              title="Откат"
              value={String(report.rollbackRowsTotal)}
              icon={RefreshCcw}
              tone="profit"
              subtitle={`${report.rolledBackCount} импортов откатано`}
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.status} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.statusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.batchCount} файлов · {row.share}% импортов
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Строк</div>
                    <div className="font-semibold text-slate-950">{row.rowsRead}</div>
                  </div>
                </div>
                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Создано</div>
                    <div className="mt-1 font-semibold text-emerald-800">{row.createdRows}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Ошибки</div>
                    <div className="mt-1 font-semibold text-rose-800">{row.errorRows}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-amber-700">Пропущено</div>
                    <div className="mt-1 font-semibold text-amber-800">{row.skippedRows}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Откатано</div>
                    <div className="mt-1 font-semibold text-slate-900">{row.rollbackRows}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-4">
                Нет импортов банковских выписок за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function SpreadsheetImportReportSection({
  report,
  loading,
  error,
  onReload,
}: {
  report: SpreadsheetImportReport | null;
  loading: boolean;
  error: string | null;
  onReload: () => void;
}) {
  const rows = report?.statuses ?? [];

  return (
    <section className="mt-8 rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
      <div className="flex flex-col gap-4 border-b border-slate-100 pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full border border-cyan-100 bg-cyan-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.16em] text-cyan-700">
            <FileSpreadsheet className="h-3.5 w-3.5" />
            Табличные импорты
          </div>
          <h2 className="mt-3 text-xl font-semibold text-slate-950">Отчет по импортам из таблиц</h2>
          <p className="mt-1 max-w-3xl text-sm leading-6 text-slate-500">
            Сводка Excel/XML/ZIP-загрузок: пакеты, файлы, строки, изменения, ошибки и откаты.
          </p>
        </div>
        <button
          type="button"
          onClick={onReload}
          className="inline-flex items-center justify-center gap-2 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
        >
          <RefreshCcw className="h-4 w-4" />
          Обновить
        </button>
      </div>

      {loading ? (
        <div className="mt-6 rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500">
          Загрузка отчета по табличным импортам...
        </div>
      ) : error || !report ? (
        <div className="mt-6 rounded-3xl border border-rose-100 bg-rose-50/70 p-6">
          <div className="font-semibold text-rose-700">Не удалось загрузить отчет по табличным импортам</div>
          <div className="mt-2 text-sm text-rose-600">{error ?? 'Нет данных для выбранного периода.'}</div>
        </div>
      ) : (
        <>
          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-5">
            <MetricCard
              title="Пакетов"
              value={String(report.batchCount)}
              icon={FileSpreadsheet}
              tone="rate"
              subtitle={`${report.fileCount} файлов`}
            />
            <MetricCard
              title="Строк"
              value={String(report.totalRows)}
              icon={Banknote}
              tone="rate"
              subtitle="Всего обработано"
            />
            <MetricCard
              title="Создано"
              value={String(report.createdRows)}
              icon={CheckCircle2}
              tone="income"
              subtitle={`${report.updatedRows} строк обновлено`}
            />
            <MetricCard
              title="Ошибки"
              value={String(report.errorRows)}
              icon={AlertTriangle}
              tone="expense"
              subtitle={`${report.appliedWithErrorsCount} пакетов с ошибками`}
            />
            <MetricCard
              title="Откат"
              value={String(report.rollbackRows)}
              icon={RefreshCcw}
              tone="profit"
              subtitle={`${report.rolledBackCount + report.rolledBackWithConflictsCount} пакетов откатано`}
            />
          </div>

          <div className="mt-6 grid grid-cols-1 gap-4 xl:grid-cols-4">
            {rows.length > 0 ? rows.map((row) => (
              <div key={row.status} className="rounded-3xl border border-slate-100 bg-slate-50/70 p-5">
                <div className="flex items-start justify-between gap-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-950">{row.statusLabel}</h3>
                    <div className="mt-1 text-sm text-slate-500">
                      {row.batchCount} пакетов · {row.fileCount} файлов · {row.share}%
                    </div>
                  </div>
                  <div className="text-right">
                    <div className="text-xs text-slate-500">Строк</div>
                    <div className="font-semibold text-slate-950">{row.totalRows}</div>
                  </div>
                </div>
                <div className="mt-4 grid grid-cols-2 gap-3">
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-emerald-700">Создано</div>
                    <div className="mt-1 font-semibold text-emerald-800">{row.createdRows}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-sky-700">Обновлено</div>
                    <div className="mt-1 font-semibold text-sky-800">{row.updatedRows}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-rose-700">Ошибки</div>
                    <div className="mt-1 font-semibold text-rose-800">{row.errorRows}</div>
                  </div>
                  <div className="rounded-2xl bg-white p-4 shadow-sm">
                    <div className="text-xs text-slate-500">Откатано</div>
                    <div className="mt-1 font-semibold text-slate-900">{row.rollbackRows}</div>
                  </div>
                </div>
              </div>
            )) : (
              <div className="rounded-3xl border border-dashed border-slate-200 bg-slate-50 p-8 text-center text-sm text-slate-500 xl:col-span-4">
                Нет табличных импортов за период.
              </div>
            )}
          </div>
        </>
      )}
    </section>
  );
}

export function Reports() {
  const now = new Date();
  const ymNow = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` as const;
  const [range, setRange] = useState<MonthRange>({ from: ymNow, to: ymNow });

  const normalizedRange = useMemo(
    () => normalizeYmRange((range.from ?? ymNow) as `${number}-${string}`, (range.to ?? range.from ?? ymNow) as `${number}-${string}`),
    [range.from, range.to, ymNow],
  );

  const { stats, types, loading, error, refresh } = usePeriodStats(normalizedRange.fromYM, normalizedRange.toYM);
  const cashFlowDates = useMemo(
    () => monthRangeToDates(normalizedRange.fromYM, normalizedRange.toYM),
    [normalizedRange.fromYM, normalizedRange.toYM],
  );
  const {
    report: cashFlowReport,
    loading: cashFlowLoading,
    error: cashFlowError,
    reload: reloadCashFlow,
  } = useCashFlowReport(cashFlowDates);
  const {
    report: cashReport,
    loading: cashReportLoading,
    error: cashReportError,
    reload: reloadCashReport,
  } = useCashReport(cashFlowDates);
  const bankReportFilters = useMemo(
    () => ({ ...cashFlowDates, source: 'Bank' as const }),
    [cashFlowDates],
  );
  const {
    report: bankReport,
    loading: bankReportLoading,
    error: bankReportError,
    reload: reloadBankReport,
  } = useCashFlowReport(bankReportFilters);
  const {
    report: categoryReport,
    loading: categoryReportLoading,
    error: categoryReportError,
    reload: reloadCategoryReport,
  } = useMoneyCategoryReport(cashFlowDates);
  const {
    report: counterpartyReport,
    loading: counterpartyReportLoading,
    error: counterpartyReportError,
    reload: reloadCounterpartyReport,
  } = useCounterpartyReport(cashFlowDates);
  const {
    report: projectReport,
    loading: projectReportLoading,
    error: projectReportError,
    reload: reloadProjectReport,
  } = useProjectReport(cashFlowDates);
  const {
    report: directionReport,
    loading: directionReportLoading,
    error: directionReportError,
    reload: reloadDirectionReport,
  } = useDirectionReport(cashFlowDates);
  const {
    report: salesPointReport,
    loading: salesPointReportLoading,
    error: salesPointReportError,
    reload: reloadSalesPointReport,
  } = useSalesPointReport(cashFlowDates);
  const {
    report: vatReport,
    loading: vatReportLoading,
    error: vatReportError,
    reload: reloadVatReport,
  } = useVatReport(cashFlowDates);
  const {
    report: planFactReport,
    loading: planFactReportLoading,
    error: planFactReportError,
    reload: reloadPlanFactReport,
  } = usePlanFactReport(cashFlowDates);
  const {
    report: advanceReport,
    loading: advanceReportLoading,
    error: advanceReportError,
    reload: reloadAdvanceReport,
  } = useAdvanceReport(cashFlowDates);
  const {
    report: paymentPriorityReport,
    loading: paymentPriorityReportLoading,
    error: paymentPriorityReportError,
    reload: reloadPaymentPriorityReport,
  } = usePaymentPriorityReport(cashFlowDates);
  const {
    report: paidAccountReport,
    loading: paidAccountReportLoading,
    error: paidAccountReportError,
    reload: reloadPaidAccountReport,
  } = usePaidAccountReport(cashFlowDates);
  const {
    report: paidInvoiceReport,
    loading: paidInvoiceReportLoading,
    error: paidInvoiceReportError,
    reload: reloadPaidInvoiceReport,
  } = usePaidInvoiceReport(cashFlowDates);
  const {
    report: invoiceStatusReport,
    loading: invoiceStatusReportLoading,
    error: invoiceStatusReportError,
    reload: reloadInvoiceStatusReport,
  } = useInvoiceStatusReport(cashFlowDates);
  const {
    report: actStatusReport,
    loading: actStatusReportLoading,
    error: actStatusReportError,
    reload: reloadActStatusReport,
  } = useActStatusReport(cashFlowDates);
  const {
    report: contractStatusReport,
    loading: contractStatusReportLoading,
    error: contractStatusReportError,
    reload: reloadContractStatusReport,
  } = useContractStatusReport(cashFlowDates);
  const {
    report: paymentOrderReport,
    loading: paymentOrderReportLoading,
    error: paymentOrderReportError,
    reload: reloadPaymentOrderReport,
  } = usePaymentOrderReport(cashFlowDates);
  const {
    report: paymentPurposeReport,
    loading: paymentPurposeReportLoading,
    error: paymentPurposeReportError,
    reload: reloadPaymentPurposeReport,
  } = usePaymentPurposeReport(cashFlowDates);
  const {
    report: matchStatusReport,
    loading: matchStatusReportLoading,
    error: matchStatusReportError,
    reload: reloadMatchStatusReport,
  } = useMatchStatusReport(cashFlowDates);
  const {
    report: journalStatusReport,
    loading: journalStatusReportLoading,
    error: journalStatusReportError,
    reload: reloadJournalStatusReport,
  } = useJournalStatusReport(cashFlowDates);
  const {
    report: currencyReport,
    loading: currencyReportLoading,
    error: currencyReportError,
    reload: reloadCurrencyReport,
  } = useCurrencyReport(cashFlowDates);
  const {
    report: cashOrderReport,
    loading: cashOrderReportLoading,
    error: cashOrderReportError,
    reload: reloadCashOrderReport,
  } = useCashOrderReport(cashFlowDates);
  const {
    report: bankStatementImportReport,
    loading: bankStatementImportReportLoading,
    error: bankStatementImportReportError,
    reload: reloadBankStatementImportReport,
  } = useBankStatementImportReport(cashFlowDates);
  const {
    report: spreadsheetImportReport,
    loading: spreadsheetImportReportLoading,
    error: spreadsheetImportReportError,
    reload: reloadSpreadsheetImportReport,
  } = useSpreadsheetImportReport(cashFlowDates);

  const titleSuffix = useMemo(
    () => buildTitleSuffix(normalizedRange.fromYM, normalizedRange.toYM),
    [normalizedRange.fromYM, normalizedRange.toYM],
  );

  const statusBreakdown = useMemo(() => {
    if (!stats) return [];

    const totalAmount =
      (stats.completedAmount ?? 0) +
      (stats.pendingAmount ?? 0) +
      (stats.overdueAmount ?? 0);

    return (['Completed', 'Pending', 'Overdue'] as const).map((key) => {
      const count =
        key === 'Completed'
          ? stats.counts.completed
          : key === 'Pending'
            ? stats.counts.pending
            : stats.counts.overdue;

      const amount =
        key === 'Completed'
          ? stats.completedAmount ?? 0
          : key === 'Pending'
            ? stats.pendingAmount ?? 0
            : stats.overdueAmount ?? 0;

      const share = totalAmount > 0 ? Math.round((amount / totalAmount) * 100) : 0;

      return {
        key,
        count,
        amount,
        share,
        color: statusMeta[key].color,
        label: statusMeta[key].label,
      };
    });
  }, [stats]);

  const pieData = useMemo(
    () => statusBreakdown.filter((item) => item.amount > 0),
    [statusBreakdown],
  );

  function applyPreset(preset: 'this' | 'prev' | 'q' | 'y') {
    const d = new Date();
    const ym = (year: number, month: number) => `${year}-${String(month).padStart(2, '0')}` as const;

    if (preset === 'this') {
      const current = ym(d.getFullYear(), d.getMonth() + 1);
      setRange({ from: current, to: current });
      return;
    }

    if (preset === 'prev') {
      d.setMonth(d.getMonth() - 1);
      const previous = ym(d.getFullYear(), d.getMonth() + 1);
      setRange({ from: previous, to: previous });
      return;
    }

    if (preset === 'q') {
      const end = new Date();
      const start = new Date();
      start.setMonth(start.getMonth() - 2);
      setRange({
        from: ym(start.getFullYear(), start.getMonth() + 1),
        to: ym(end.getFullYear(), end.getMonth() + 1),
      });
      return;
    }

    const year = new Date().getFullYear();
    setRange({ from: `${year}-01`, to: `${year}-12` });
  }

  return (
    <div className="min-h-screen bg-[radial-gradient(circle_at_top_left,_rgba(14,165,233,0.12),_transparent_28%),radial-gradient(circle_at_top_right,_rgba(16,185,129,0.14),_transparent_24%),linear-gradient(180deg,_#f8fafc_0%,_#eef6ff_45%,_#f8fafc_100%)]">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 py-8 sm:px-6 lg:px-8">
        <section className="overflow-hidden rounded-[32px] border border-white/70 bg-white/80 shadow-[0_24px_80px_-32px_rgba(15,23,42,0.35)] backdrop-blur">
          <div className="grid gap-6 px-6 py-7 lg:grid-cols-[1.3fr_0.9fr] lg:px-8">
            <div>
              <div className="inline-flex items-center gap-2 rounded-full border border-sky-100 bg-sky-50 px-3 py-1 text-xs font-semibold uppercase tracking-[0.18em] text-sky-700">
                <CalendarRange className="h-3.5 w-3.5" />
                Reports
              </div>
              <h1 className="mt-4 text-3xl font-semibold tracking-tight text-slate-950 sm:text-4xl">
                Отчёты по платежам
              </h1>
              <p className="mt-3 max-w-2xl text-sm leading-6 text-slate-600 sm:text-base">
                Сводка строится по тем же данным, что и общая серверная статистика:
                выполнено берётся по фактически оплаченной сумме, ожидание и просрочка по остаткам.
              </p>
              <div className="mt-5 flex flex-wrap gap-2">
                {(['this', 'prev', 'q', 'y'] as const).map((preset) => {
                  const label =
                    preset === 'this'
                      ? 'Этот месяц'
                      : preset === 'prev'
                        ? 'Прошлый'
                        : preset === 'q'
                          ? '3 месяца'
                          : 'Год';

                  return (
                    <button
                      key={preset}
                      type="button"
                      onClick={() => applyPreset(preset)}
                      className="rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:border-slate-300 hover:bg-slate-50"
                    >
                      {label}
                    </button>
                  );
                })}
              </div>
            </div>

            <div className="rounded-[28px] border border-slate-200/80 bg-slate-50/80 p-5">
              <div className="text-sm font-medium text-slate-500">Выбранный период</div>
              <div className="mt-2 text-2xl font-semibold text-slate-950">{titleSuffix}</div>
              <div className="mt-4">
                <MonthRangePicker value={range} onChange={setRange} className="w-full" />
              </div>
              <div className="mt-5 grid grid-cols-2 gap-3">
                <div className="rounded-2xl bg-white p-4 shadow-sm">
                  <div className="text-xs uppercase tracking-wide text-slate-500">От</div>
                  <div className="mt-2 text-sm font-semibold text-slate-900">{formatYmLabel(normalizedRange.fromYM)}</div>
                </div>
                <div className="rounded-2xl bg-white p-4 shadow-sm">
                  <div className="text-xs uppercase tracking-wide text-slate-500">До</div>
                  <div className="mt-2 text-sm font-semibold text-slate-900">{formatYmLabel(normalizedRange.toYM)}</div>
                </div>
              </div>
            </div>
          </div>
        </section>

        {loading ? (
          <div className="mt-8 rounded-[32px] border border-white/70 bg-white/80 p-12 text-center text-slate-500 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
            Загрузка отчётов...
          </div>
        ) : error || !stats || !types ? (
          <div className="mt-8 rounded-[32px] border border-rose-100 bg-white/90 p-8 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)]">
            <div className="text-lg font-semibold text-slate-950">Не удалось загрузить отчёты</div>
            <div className="mt-2 text-sm text-slate-600">{error ?? 'Нет данных для выбранного периода.'}</div>
            <button
              type="button"
              onClick={refresh}
              className="mt-5 rounded-full border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
            >
              Повторить
            </button>
          </div>
        ) : (
          <>
            <section className="mt-8 grid grid-cols-1 gap-4 xl:grid-cols-4">
              <MetricCard
                title="Поступило"
                value={formatCurrency(stats.income)}
                icon={TrendingUp}
                tone="income"
                subtitle={`Фактически получено за период · операций: ${types.income.totalCount}`}
              />
              <MetricCard
                title="Списано"
                value={formatCurrency(stats.expense)}
                icon={TrendingDown}
                tone="expense"
                subtitle={`Фактически оплачено за период · операций: ${types.expense.totalCount}`}
              />
              <MetricCard
                title="Денежный результат"
                value={formatCurrency(stats.profit)}
                icon={ArrowUpRight}
                tone="profit"
                subtitle={stats.profit >= 0 ? 'Поступления превышают списания' : 'Списания превышают поступления'}
              />
              <MetricCard
                title="Выполнение"
                value={`${stats.completionRate}%`}
                icon={CheckCircle2}
                tone="rate"
                subtitle={`${stats.counts.completed} из ${stats.counts.total} операций закрыты`}
              />
            </section>

            <section className="mt-8 grid grid-cols-1 gap-6 xl:grid-cols-[1.2fr_0.8fr]">
              <div className="rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
                <div className="flex flex-col gap-2 border-b border-slate-100 pb-5 sm:flex-row sm:items-end sm:justify-between">
                  <div>
                    <h2 className="text-xl font-semibold text-slate-950">Статусы по сумме</h2>
                    <p className="mt-1 text-sm text-slate-500">Кольцо показывает распределение оплаченной суммы и остатка между выполненными, ожидаемыми и просроченными платежами.</p>
                  </div>
                  <div className="text-sm text-slate-500">
                    Всего в разрезе статусов: <span className="font-semibold text-slate-900">{formatCurrency((stats.completedAmount ?? 0) + (stats.pendingAmount ?? 0) + (stats.overdueAmount ?? 0))}</span>
                  </div>
                </div>

                <div className="mt-6 grid gap-6 lg:grid-cols-[0.95fr_1.05fr]">
                  <div className="h-[320px]">
                    {pieData.length > 0 ? (
                      <ResponsiveContainer width="100%" height="100%">
                        <PieChart>
                          <Pie
                            data={pieData}
                            dataKey="amount"
                            nameKey="label"
                            innerRadius={72}
                            outerRadius={108}
                            paddingAngle={3}
                            strokeWidth={0}
                          >
                            {pieData.map((entry) => (
                              <Cell key={entry.key} fill={entry.color} />
                            ))}
                          </Pie>
                          <Tooltip content={<PieTooltip />} />
                        </PieChart>
                      </ResponsiveContainer>
                    ) : (
                      <div className="flex h-full items-center justify-center rounded-3xl border border-dashed border-slate-200 bg-slate-50 text-sm text-slate-500">
                        Нет данных по суммам за выбранный период
                      </div>
                    )}
                  </div>

                  <div className="space-y-3">
                    {statusBreakdown.map((item) => {
                      const meta = statusMeta[item.key];

                      return (
                        <div key={item.key} className="rounded-2xl border border-slate-100 bg-slate-50/80 p-4">
                          <div className="flex items-start justify-between gap-3">
                            <div>
                              <div className={`inline-flex rounded-full px-3 py-1 text-xs font-semibold ${meta.chip}`}>
                                {item.label}
                              </div>
                              <div className="mt-3 text-xl font-semibold text-slate-950">{formatCurrency(item.amount)}</div>
                            </div>
                            <div className="text-right text-sm text-slate-500">
                              <div>{item.share}% суммы</div>
                              <div className="mt-1">{item.count} операций</div>
                            </div>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              </div>

              <div className="rounded-[32px] border border-white/70 bg-white/92 p-6 shadow-[0_18px_45px_-24px_rgba(15,23,42,0.35)] backdrop-blur">
                <h2 className="text-xl font-semibold text-slate-950">Краткая сводка периода</h2>
                <div className="mt-5 space-y-4">
                  <div className="flex items-center justify-between rounded-2xl bg-emerald-50/70 px-4 py-3">
                    <div className="flex items-center gap-3">
                      <CheckCircle2 className="h-5 w-5 text-emerald-600" />
                      <span className="text-sm font-medium text-slate-700">Выполнено</span>
                    </div>
                    <div className="text-right">
                      <div className="font-semibold text-slate-950">{formatCurrency(stats.completedAmount ?? 0)}</div>
                      <div className="text-xs text-slate-500">{stats.counts.completed} шт.</div>
                    </div>
                  </div>
                  <div className="flex items-center justify-between rounded-2xl bg-amber-50/70 px-4 py-3">
                    <div className="flex items-center gap-3">
                      <Clock3 className="h-5 w-5 text-amber-600" />
                      <span className="text-sm font-medium text-slate-700">Ожидается</span>
                    </div>
                    <div className="text-right">
                      <div className="font-semibold text-slate-950">{formatCurrency(stats.pendingAmount ?? 0)}</div>
                      <div className="text-xs text-slate-500">{stats.counts.pending} шт.</div>
                    </div>
                  </div>
                  <div className="flex items-center justify-between rounded-2xl bg-rose-50/70 px-4 py-3">
                    <div className="flex items-center gap-3">
                      <AlertTriangle className="h-5 w-5 text-rose-600" />
                      <span className="text-sm font-medium text-slate-700">Просрочено</span>
                    </div>
                    <div className="text-right">
                      <div className="font-semibold text-slate-950">{formatCurrency(stats.overdueAmount ?? 0)}</div>
                      <div className="text-xs text-slate-500">{stats.counts.overdue} шт.</div>
                    </div>
                  </div>
                </div>

                <div className="mt-6 rounded-3xl border border-slate-100 bg-slate-50/80 p-5">
                  <div className="text-xs uppercase tracking-[0.18em] text-slate-500">Проверка периода</div>
                  <div className="mt-3 text-sm leading-6 text-slate-600">
                    Границы применяются по календарным месяцам: от первого дня месяца начала до последнего дня месяца окончания включительно.
                  </div>
                </div>
              </div>
            </section>

            <section className="mt-8 grid grid-cols-1 gap-6 xl:grid-cols-2">
              <StatusPanel
                title="Доходы за период"
                totalCount={types.income.totalCount}
                totalAmount={types.income.totalAmount}
                byStatus={types.income.byStatus}
              />
              <StatusPanel
                title="Расходы за период"
                totalCount={types.expense.totalCount}
                totalAmount={types.expense.totalAmount}
                byStatus={types.expense.byStatus}
              />
            </section>

            <CashFlowReportSection
              report={cashFlowReport}
              loading={cashFlowLoading}
              error={cashFlowError}
              onReload={reloadCashFlow}
            />

            <CashReportSection
              report={cashReport}
              loading={cashReportLoading}
              error={cashReportError}
              onReload={reloadCashReport}
            />

            <BankReportSection
              report={bankReport}
              loading={bankReportLoading}
              error={bankReportError}
              onReload={reloadBankReport}
            />

            <CategoryReportSection
              report={categoryReport}
              loading={categoryReportLoading}
              error={categoryReportError}
              onReload={reloadCategoryReport}
            />

            <CounterpartyReportSection
              report={counterpartyReport}
              loading={counterpartyReportLoading}
              error={counterpartyReportError}
              onReload={reloadCounterpartyReport}
            />

            <ProjectReportSection
              report={projectReport}
              loading={projectReportLoading}
              error={projectReportError}
              onReload={reloadProjectReport}
            />

            <DirectionReportSection
              report={directionReport}
              loading={directionReportLoading}
              error={directionReportError}
              onReload={reloadDirectionReport}
            />

            <SalesPointReportSection
              report={salesPointReport}
              loading={salesPointReportLoading}
              error={salesPointReportError}
              onReload={reloadSalesPointReport}
            />

            <VatReportSection
              report={vatReport}
              loading={vatReportLoading}
              error={vatReportError}
              onReload={reloadVatReport}
            />

            <PlanFactReportSection
              report={planFactReport}
              loading={planFactReportLoading}
              error={planFactReportError}
              onReload={reloadPlanFactReport}
            />

            <AdvanceReportSection
              report={advanceReport}
              loading={advanceReportLoading}
              error={advanceReportError}
              onReload={reloadAdvanceReport}
            />

            <PaymentPriorityReportSection
              report={paymentPriorityReport}
              loading={paymentPriorityReportLoading}
              error={paymentPriorityReportError}
              onReload={reloadPaymentPriorityReport}
            />

            <PaidAccountReportSection
              report={paidAccountReport}
              loading={paidAccountReportLoading}
              error={paidAccountReportError}
              onReload={reloadPaidAccountReport}
            />

            <PaidInvoiceReportSection
              report={paidInvoiceReport}
              loading={paidInvoiceReportLoading}
              error={paidInvoiceReportError}
              onReload={reloadPaidInvoiceReport}
            />

            <InvoiceStatusReportSection
              report={invoiceStatusReport}
              loading={invoiceStatusReportLoading}
              error={invoiceStatusReportError}
              onReload={reloadInvoiceStatusReport}
            />

            <ActStatusReportSection
              report={actStatusReport}
              loading={actStatusReportLoading}
              error={actStatusReportError}
              onReload={reloadActStatusReport}
            />

            <ContractStatusReportSection
              report={contractStatusReport}
              loading={contractStatusReportLoading}
              error={contractStatusReportError}
              onReload={reloadContractStatusReport}
            />

            <PaymentOrderReportSection
              report={paymentOrderReport}
              loading={paymentOrderReportLoading}
              error={paymentOrderReportError}
              onReload={reloadPaymentOrderReport}
            />

            <PaymentPurposeReportSection
              report={paymentPurposeReport}
              loading={paymentPurposeReportLoading}
              error={paymentPurposeReportError}
              onReload={reloadPaymentPurposeReport}
            />

            <MatchStatusReportSection
              report={matchStatusReport}
              loading={matchStatusReportLoading}
              error={matchStatusReportError}
              onReload={reloadMatchStatusReport}
            />

            <JournalStatusReportSection
              report={journalStatusReport}
              loading={journalStatusReportLoading}
              error={journalStatusReportError}
              onReload={reloadJournalStatusReport}
            />

            <CurrencyReportSection
              report={currencyReport}
              loading={currencyReportLoading}
              error={currencyReportError}
              onReload={reloadCurrencyReport}
            />

            <CashOrderReportSection
              report={cashOrderReport}
              loading={cashOrderReportLoading}
              error={cashOrderReportError}
              onReload={reloadCashOrderReport}
            />

            <BankStatementImportReportSection
              report={bankStatementImportReport}
              loading={bankStatementImportReportLoading}
              error={bankStatementImportReportError}
              onReload={reloadBankStatementImportReport}
            />

            <SpreadsheetImportReportSection
              report={spreadsheetImportReport}
              loading={spreadsheetImportReportLoading}
              error={spreadsheetImportReportError}
              onReload={reloadSpreadsheetImportReport}
            />
          </>
        )}
      </div>
    </div>
  );
}
