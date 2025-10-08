import { useMemo, useState } from 'react';
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from 'recharts';
import { usePeriodStats } from '../../hooks/usePeriodStats';
import { MonthRangePicker, type MonthRange } from '../MonthRange/MonthRangePicker';

const statusRu: Record<'Completed' | 'Pending' | 'Overdue', string> = {
  Completed: 'Оплачено',
  Pending: 'Ожидается',
  Overdue: 'Просрочено',
};

type PieTooltipProps = {
  active?: boolean;
  payload?: Array<{
    name?: 'Completed' | 'Pending' | 'Overdue';
    value?: number;
  }>;
};

function PieTooltip({ active, payload }: PieTooltipProps) {
  if (!active || !payload || payload.length === 0) return null;
  const p = payload[0];
  const name = (p?.name ?? 'Pending') as 'Completed' | 'Pending' | 'Overdue';
  const value = p?.value ?? 0;

  return (
    <div className="bg-white/95 border border-gray-200 rounded-md px-3 py-2 shadow-sm text-sm">
      <div className="font-medium text-gray-900">{statusRu[name]}</div>
      <div className="text-gray-600">Кол-во: {value}</div>
    </div>
  );
}

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    minimumFractionDigits: 0,
    maximumFractionDigits: 0,
  }).format(amount);
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
  const map = {
    Completed: { label: 'Оплачено', dot: 'bg-emerald-500' },
    Pending: { label: 'Ожидается', dot: 'bg-yellow-500' },
    Overdue: { label: 'Просрочено', dot: 'bg-red-500' },
  } as const;

  const StatusRows = (
    <div className="space-y-3 md:space-y-4">
      {byStatus.map((s) => (
        <div
          key={s.key}
          className="grid grid-cols-[auto_1fr_auto_auto_auto] items-center gap-x-2 md:gap-x-3 min-w-0">
          <span
            className={`inline-block w-2 h-2 md:w-2.5 md:h-2.5 rounded-full ${map[s.key].dot}`}
          />
          <span className="text-gray-700 text-sm md:text-base">{map[s.key].label}</span>
          <span className="text-gray-500 tabular-nums text-sm md:text-base">{s.percent}%</span>
          <span className="text-gray-500 tabular-nums text-sm md:text-base">
            {s.count}&nbsp;плат.
          </span>
          <span className="text-gray-900 font-medium text-right whitespace-nowrap tabular-nums text-sm md:text-base">
            {formatCurrency(s.amount)}
          </span>
        </div>
      ))}
    </div>
  );

  // Всегда «как на мобилке»: статусы сверху, кольцо ниже — и на десктопе тоже
  const Ring = (
    <div className="flex items-center justify-center">
      <div className="w-36 h-36 md:w-48 md:h-48 rounded-full border-[10px] border-yellow-400 grid place-items-center">
        <div className="text-center px-1">
          <div className="text-xs text-gray-500 mb-1">Всего платежей</div>
          <div className="text-3xl md:text-4xl font-bold text-gray-900 tabular-nums">
            {totalCount}
          </div>
          <div className="text-xs text-gray-500 mt-1 whitespace-nowrap tabular-nums">
            {formatCurrency(totalAmount)}
          </div>
        </div>
      </div>
    </div>
  );

  return (
    <div className="bg-white rounded-xl p-5 md:p-6 shadow-sm border border-gray-100">
      <h3 className="text-base md:text-lg font-semibold text-gray-900 mb-4 md:mb-6">{title}</h3>
      <div className="space-y-5">
        {StatusRows}
        {Ring}
      </div>
    </div>
  );
}

export function Reports() {
  // дефолт — текущий месяц
  const now = new Date();
  const ymNow = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` as const;

  const [range, setRange] = useState<MonthRange>({ from: ymNow, to: ymNow });

  // нормализуем границы
  const fromYM = (range.from ?? ymNow) as `${number}-${string}`;
  const toYM = (range.to ?? range.from ?? ymNow) as `${number}-${string}`;

  const { stats, types, loading, error } = usePeriodStats(fromYM, toYM);

  const titleSuffix = useMemo(() => {
    if (fromYM === toYM) return fromYM; // один месяц
    return `${fromYM} — ${toYM}`; // период
  }, [fromYM, toYM]);

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-500">Loading reports...</div>
      </div>
    );
  }

  if (!stats || !types) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-500">{error ?? 'No data available'}</div>
      </div>
    );
  }

  const pieData = [
    { name: 'Completed', value: stats.counts.completed, color: '#10B981' },
    { name: 'Pending', value: stats.counts.pending, color: '#F59E0B' },
    { name: 'Overdue', value: stats.counts.overdue, color: '#EF4444' },
  ].filter((item) => item.value > 0);

  // Быстрые пресеты
  function applyPreset(preset: 'this' | 'prev' | 'q' | 'y') {
    const d = new Date();
    const ym = (y: number, m: number) => `${y}-${String(m).padStart(2, '0')}`;

    if (preset === 'this') {
      setRange({
        from: ym(d.getFullYear(), d.getMonth() + 1),
        to: ym(d.getFullYear(), d.getMonth() + 1),
      });
    } else if (preset === 'prev') {
      d.setMonth(d.getMonth() - 1);
      setRange({
        from: ym(d.getFullYear(), d.getMonth() + 1),
        to: ym(d.getFullYear(), d.getMonth() + 1),
      });
    } else if (preset === 'q') {
      const end = new Date();
      const start = new Date();
      start.setMonth(start.getMonth() - 2);
      setRange({
        from: ym(start.getFullYear(), start.getMonth() + 1),
        to: ym(end.getFullYear(), end.getMonth() + 1),
      });
    } else if (preset === 'y') {
      const y = new Date().getFullYear();
      setRange({ from: `${y}-01`, to: `${y}-12` });
    }
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-6">
        {/* Заголовки по центру на мобиле + выбор периода */}
        <div className="mb-6 md:mb-8 flex flex-col md:flex-row md:items-center md:justify-between gap-4">
          <div className="text-center md:text-left">
            <h1 className="text-3xl font-bold text-gray-900">Отчёты</h1>
            <p className="text-gray-600">Финансовая аналитика и статистика — {titleSuffix}</p>
          </div>

          <div className="w-full md:w-auto flex flex-col sm:flex-row gap-3 sm:items-center sm:justify-end">
            {/* выбор периода */}
            <div className="w-full sm:w-auto">
              <MonthRangePicker value={range} onChange={setRange} />
            </div>

            {/* пресеты */}
            <div className="flex flex-wrap gap-2">
              <button
                onClick={() => applyPreset('this')}
                className="px-3 py-2 text-sm rounded-lg border border-gray-300 hover:bg-gray-100">
                Этот месяц
              </button>
              <button
                onClick={() => applyPreset('prev')}
                className="px-3 py-2 text-sm rounded-lg border border-gray-300 hover:bg-gray-100">
                Прошлый
              </button>
              <button
                onClick={() => applyPreset('q')}
                className="px-3 py-2 text-sm rounded-lg border border-gray-300 hover:bg-gray-100">
                Последние 3 мес
              </button>
              <button
                onClick={() => applyPreset('y')}
                className="px-3 py-2 text-sm rounded-lg border border-gray-300 hover:bg-gray-100">
                Год
              </button>
            </div>
          </div>
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-3 gap-8">
          {/* KPI: мобил — столбиком; десктоп — 4 колонки, заголовок сверху, значение снизу */}
          <div className="lg:col-span-3 grid grid-cols-1 md:grid-cols-4 gap-4 md:gap-6 mb-8">
            <div className="bg-white rounded-xl p-4 md:p-6 shadow-sm border border-gray-100">
              <div className="flex items-center justify-between md:block">
                <h3 className="text-sm font-medium text-gray-600">Доходы</h3>
                <p className="text-xl md:text-2xl font-bold text-emerald-600 tabular-nums md:mt-2">
                  {formatCurrency(stats.income)}
                </p>
              </div>
            </div>

            <div className="bg-white rounded-xl p-4 md:p-6 shadow-sm border border-gray-100">
              <div className="flex items-center justify-between md:block">
                <h3 className="text-sm font-medium text-gray-600">Расходы</h3>
                <p className="text-xl md:text-2xl font-bold text-red-600 tabular-nums md:mt-2">
                  {formatCurrency(stats.expense)}
                </p>
              </div>
            </div>

            <div className="bg-white rounded-xl p-4 md:p-6 shadow-sm border border-gray-100">
              <div className="flex items-center justify-between md:block">
                <h3 className="text-sm font-medium text-gray-600">Прибыль</h3>
                <p
                  className={`text-xl md:text-2xl font-bold tabular-nums md:mt-2 ${
                    stats.profit >= 0 ? 'text-emerald-600' : 'text-red-600'
                  }`}>
                  {formatCurrency(stats.profit)}
                </p>
              </div>
            </div>

            <div className="bg-white rounded-xl p-4 md:p-6 shadow-sm border border-gray-100">
              <div className="flex items-center justify-between md:block">
                <h3 className="text-sm font-medium text-gray-600">Выполнено</h3>
                <p className="text-xl md:text-2xl font-bold text-blue-600 tabular-nums md:mt-2">
                  {stats.completionRate}%{' '}
                  <span className="text-gray-500 text-sm font-normal tabular-nums">
                    ({stats.counts.completed}/{stats.counts.total})
                  </span>
                </p>
              </div>
            </div>
          </div>

          {/* StatusPanel: мобил — столбиком; md+ — 2 колонки, без налезания */}
          <div className="lg:col-span-3 grid grid-cols-1 md:grid-cols-2 gap-6 md:gap-8">
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
          </div>

          {/* Диаграмма */}
          <div className="lg:col-span-2">
            <div className="bg-white rounded-xl p-6 shadow-sm border border-gray-100">
              <h3 className="text-lg font-semibold text-gray-900 mb-6">
                Распределение по статусам
              </h3>
              {pieData.length > 0 ? (
                <div className="h-80">
                  <ResponsiveContainer width="100%" height="100%">
                    <PieChart>
                      <Pie
                        data={pieData}
                        cx="50%"
                        cy="50%"
                        outerRadius={80}
                        dataKey="value"
                        label={({ name, percent }) =>
                          `${statusRu[name as 'Completed' | 'Pending' | 'Overdue']} ${(
                            (percent ?? 0) * 100
                          ).toFixed(0)}%`
                        }>
                        {pieData.map((entry, index) => (
                          <Cell key={`cell-${index}`} fill={entry.color} />
                        ))}
                      </Pie>
                      <Tooltip content={<PieTooltip />} />
                    </PieChart>
                  </ResponsiveContainer>
                </div>
              ) : (
                <div className="h-80 flex items-center justify-center text-gray-500">
                  Нет данных
                </div>
              )}
            </div>
          </div>

          {/* Правая колонка */}
          <div className="lg:col-span-1">
            <div className="bg-white rounded-xl p-6 shadow-sm border border-gray-100">
              <h3 className="text-lg font-semibold text-gray-900 mb-6">Статистика периода</h3>
              <div className="space-y-4">
                <div className="flex justify-between items-center">
                  <span className="text-gray-600">Выполнено:</span>
                  <span className="text-lg font-semibold text-emerald-600 tabular-nums">
                    {stats.counts.completed}
                  </span>
                </div>
                <div className="flex justify-between items-center">
                  <span className="text-gray-600">Ожидается:</span>
                  <span className="text-lg font-semibold text-yellow-600 tabular-nums">
                    {stats.counts.pending}
                  </span>
                </div>
                <div className="flex justify-between items-center">
                  <span className="text-gray-600">Просрочено:</span>
                  <span className="text-lg font-semibold text-red-600 tabular-nums">
                    {stats.counts.overdue}
                  </span>
                </div>
                <div className="border-t pt-4 mt-4">
                  <div className="flex justify-between items-center">
                    <span className="text-gray-900 font-medium">Всего событий:</span>
                    <span className="text-xl font-bold text-gray-900 tabular-nums">
                      {stats.counts.total}
                    </span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
