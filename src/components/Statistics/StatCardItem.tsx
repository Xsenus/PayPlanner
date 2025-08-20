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

export function StatsCards({ items, loading = false, lgCols = 6, skeletonCount }: StatsCardsProps) {
  const count = skeletonCount ?? items.length;

  if (loading) {
    return (
      <div
        className={`grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-${lgCols} gap-3 sm:gap-6 mb-6 sm:mb-8`}>
        {[...Array(count)].map((_, i) => (
          <div key={i} className="bg-white rounded-xl p-4 sm:p-6 shadow-sm animate-pulse">
            <div className="h-4 bg-gray-200 rounded w-1/2 mb-3" />
            <div className="h-7 bg-gray-200 rounded w-3/4" />
          </div>
        ))}
      </div>
    );
  }

  return (
    <div
      className={`grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-${lgCols} gap-3 sm:gap-6 mb-6 sm:mb-8`}>
      {items.map((c, i) => {
        const Icon = c.icon;
        return (
          <div key={i} className="bg-white rounded-xl p-4 sm:p-5 shadow-sm border border-gray-100">
            <div className="flex items-center justify-between lg:flex-col lg:items-center lg:text-center h-full">
              <div className="flex items-center gap-3 min-w-0 lg:flex-col lg:gap-2 lg:w-full lg:items-center">
                <span className={`p-2 rounded-lg ${c.bg} lg:mb-1`}>
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
                  'lg:ml-0 lg:mt-3 lg:text-xl',
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
