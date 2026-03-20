import { useMemo, useState } from 'react';
import {
  AlertTriangle,
  ArrowUpRight,
  CalendarRange,
  CheckCircle2,
  Clock3,
  TrendingDown,
  TrendingUp,
} from 'lucide-react';
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from 'recharts';
import { usePeriodStats } from '../../hooks/usePeriodStats';
import { MonthRangePicker, type MonthRange } from '../MonthRange/MonthRangePicker';
import { formatCurrencySmart } from '../../utils/formatters';

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

export function Reports() {
  const now = new Date();
  const ymNow = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` as const;
  const [range, setRange] = useState<MonthRange>({ from: ymNow, to: ymNow });

  const normalizedRange = useMemo(
    () => normalizeYmRange((range.from ?? ymNow) as `${number}-${string}`, (range.to ?? range.from ?? ymNow) as `${number}-${string}`),
    [range.from, range.to, ymNow],
  );

  const { stats, types, loading, error, refresh } = usePeriodStats(normalizedRange.fromYM, normalizedRange.toYM);

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
          </>
        )}
      </div>
    </div>
  );
}
