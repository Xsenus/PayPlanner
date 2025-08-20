import type { LucideIcon } from 'lucide-react';

export type StatCardItem = {
  title: string;
  value: string;
  titleAttr?: string;
  icon: LucideIcon;
  color: string;
  bg: string;
};

interface StatsCardsProps {
  items: StatCardItem[];
  loading?: boolean;
  lgCols?: number;
  skeletonCount?: number;
}

function getLgColsClass(n: number = 6) {
  const cols = Math.max(1, Math.min(12, n));
  const map: Record<number, string> = {
    1: 'lg:grid-cols-1',
    2: 'lg:grid-cols-2',
    3: 'lg:grid-cols-3',
    4: 'lg:grid-cols-4',
    5: 'lg:grid-cols-5',
    6: 'lg:grid-cols-6',
    7: 'lg:grid-cols-7',
    8: 'lg:grid-cols-8',
    9: 'lg:grid-cols-9',
    10: 'lg:grid-cols-10',
    11: 'lg:grid-cols-11',
    12: 'lg:grid-cols-12',
  };
  return map[cols];
}

export function StatsCards({ items, loading = false, lgCols = 6, skeletonCount }: StatsCardsProps) {
  const count = skeletonCount ?? items.length;
  const lgColsClass = getLgColsClass(lgCols);

  if (loading) {
    return (
      <div className={`grid grid-cols-1 sm:grid-cols-2 ${lgColsClass} gap-3 sm:gap-6 mb-6 sm:mb-8`}>
        {[...Array(count)].map((_, i) => (
          <div
            key={i}
            className="bg-white rounded-xl p-4 sm:p-6 shadow-sm animate-pulse min-h-[112px]"
          />
        ))}
      </div>
    );
  }

  return (
    <div className={`grid grid-cols-1 sm:grid-cols-2 ${lgColsClass} gap-3 sm:gap-6 mb-6 sm:mb-8`}>
      {items.map((c, i) => {
        const Icon = c.icon;
        return (
          <div key={i} className="bg-white rounded-xl p-4 sm:p-5 shadow-sm border border-gray-100">
            <div className="flex items-center justify-between h-full lg:flex-col lg:items-center lg:text-center">
              <div className="flex items-center gap-3 min-w-0 lg:flex-col lg:gap-2 lg:w-full lg:items-center">
                <span className={`p-2 rounded-lg ${c.bg}`}>
                  <Icon className={`h-5 w-5 lg:h-6 lg:w-6 ${c.color}`} />
                </span>
                <span className="text-sm font-medium text-gray-600 truncate lg:overflow-visible lg:whitespace-normal lg:w-full">
                  {c.title}
                </span>
              </div>
              <span
                title={c.titleAttr ?? c.value}
                className={[
                  'ml-3 text-base font-bold',
                  'lg:ml-0 lg:mt-auto lg:pt-1 lg:text-xl',
                  'whitespace-nowrap truncate',
                  'tabular-nums font-mono',
                  c.color,
                ].join(' ')}>
                {c.value}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
