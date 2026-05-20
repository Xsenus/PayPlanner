import { useCallback, useEffect, useMemo, useState } from 'react';
import { Flag, RefreshCw } from 'lucide-react';

import { apiService, type AppFeatureFlagsDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

const EMPTY_FLAGS: AppFeatureFlagsDto = {
  legal: {
    externalCourtSync: false,
    externalCalendarSync: false,
    courtEvents: false,
    documentTemplateLibrary: false,
  },
};

type FeatureFlagRow = {
  key: keyof AppFeatureFlagsDto['legal'];
  label: string;
  area: string;
};

const LEGAL_FLAGS: FeatureFlagRow[] = [
  {
    key: 'externalCourtSync',
    label: 'Внешняя синхронизация судов',
    area: 'Суды',
  },
  {
    key: 'externalCalendarSync',
    label: 'Google/Яндекс календарь',
    area: 'Календарь',
  },
  {
    key: 'courtEvents',
    label: 'Судебные события',
    area: 'Дела',
  },
  {
    key: 'documentTemplateLibrary',
    label: 'Библиотека шаблонов документов',
    area: 'Документы',
  },
];

export function FeatureFlagSettings() {
  const [flags, setFlags] = useState<AppFeatureFlagsDto>(EMPTY_FLAGS);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const enabledCount = useMemo(
    () => LEGAL_FLAGS.filter((item) => flags.legal[item.key]).length,
    [flags],
  );

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await apiService.getFeatureFlags();
      setFlags({
        legal: {
          ...EMPTY_FLAGS.legal,
          ...response.legal,
        },
      });
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось загрузить feature flags.'));
      setFlags(EMPTY_FLAGS);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full bg-violet-50 px-3 py-1 text-xs font-semibold text-violet-700">
            <Flag className="h-3.5 w-3.5" />
            Feature flags
          </div>
          <h4 className="mt-3 text-xl font-semibold text-gray-900">Юридический контур</h4>
          <p className="mt-1 text-sm text-gray-600">
            Внешние интеграции включаются только после dry-run и проверки ролей на копии базы.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void load()}
          disabled={loading}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-gray-200 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
          Обновить
        </button>
      </div>

      {error ? (
        <div className="mt-4 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
          {error}
        </div>
      ) : null}

      <div className="mt-5 grid gap-3 md:grid-cols-2">
        {LEGAL_FLAGS.map((item) => {
          const enabled = flags.legal[item.key];
          return (
            <div key={item.key} className="rounded-2xl border border-gray-200 bg-slate-50 p-4">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">{item.area}</div>
                  <div className="mt-1 text-sm font-semibold text-gray-900">{item.label}</div>
                </div>
                <span className={`shrink-0 rounded-full px-2.5 py-1 text-xs font-semibold ${
                  enabled
                    ? 'bg-emerald-100 text-emerald-700'
                    : 'bg-slate-200 text-slate-600'
                }`}>
                  {enabled ? 'Включено' : 'Выключено'}
                </span>
              </div>
            </div>
          );
        })}
      </div>

      <div className="mt-4 rounded-2xl border border-violet-100 bg-violet-50 px-4 py-3 text-sm text-violet-900">
        Активно: {enabledCount} из {LEGAL_FLAGS.length}. Источник: backend `FeatureFlags:Legal`.
      </div>
    </div>
  );
}
