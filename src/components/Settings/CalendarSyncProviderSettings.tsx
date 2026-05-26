import { CalendarDays, RefreshCw } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { apiService, type CalendarSyncProviderDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

export function CalendarSyncProviderSettings() {
  const [providers, setProviders] = useState<CalendarSyncProviderDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const availableCount = useMemo(
    () => providers.filter((provider) => provider.isAvailable).length,
    [providers],
  );

  const loadProviders = async () => {
    setLoading(true);
    setError(null);
    try {
      setProviders(await apiService.getCalendarSyncProviders());
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить состояние внешних календарей.'));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadProviders();
  }, []);

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <div className="inline-flex items-center gap-2 rounded-full bg-sky-50 px-3 py-1 text-xs font-semibold text-sky-700">
            <CalendarDays className="h-3.5 w-3.5" />
            Внешние календари
          </div>
          <h4 className="mt-3 text-xl font-semibold text-gray-900">Провайдеры календарной синхронизации</h4>
          <p className="mt-1 text-sm text-gray-600">
            Здесь видно, какой контур календарей уже доступен, а какие подключения требуют отдельной настройки и приемки.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void loadProviders()}
          disabled={loading}
          className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-gray-300 px-4 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
          <span>{loading ? 'Обновляем' : 'Обновить'}</span>
        </button>
      </div>

      {error ? (
        <div className="mt-4 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
      ) : null}

      <div className="mt-5 grid gap-3 lg:grid-cols-3">
        {providers.map((provider) => (
          <div key={provider.key} className="rounded-2xl border border-gray-200 bg-gray-50 px-4 py-4">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-semibold text-gray-900">{provider.label}</span>
              <span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${getProviderStatusClass(provider.status)}`}>
                {getProviderStatusLabel(provider.status)}
              </span>
            </div>
            <p className="mt-2 min-h-[40px] text-xs leading-5 text-gray-600">{provider.message}</p>
            <div className="mt-3 flex flex-wrap gap-1.5">
              {provider.supportedTargets.map((target) => (
                <span key={target} className="rounded-full bg-white px-2 py-0.5 text-xs font-medium text-gray-600">{target}</span>
              ))}
            </div>
            <div className="mt-4 grid grid-cols-2 gap-2 text-xs text-gray-600">
              <Capability enabled={provider.supportsManualExport} label="Ручной экспорт" />
              <Capability enabled={provider.supportsBackgroundSync} label="Фоновая синхронизация" />
              <Capability enabled={provider.supportsTwoWaySync} label="Двусторонний режим" />
              <Capability enabled={provider.requiresOAuth} label="OAuth" />
            </div>
          </div>
        ))}
      </div>

      <div className="mt-4 rounded-2xl border border-sky-100 bg-sky-50 px-4 py-3 text-sm text-sky-900">
        Доступно сейчас: {availableCount} из {providers.length || 0}. Google и Яндекс не запускаются автоматически, пока внешний календарный feature flag и OAuth-настройки не будут приняты отдельно.
      </div>
    </div>
  );
}

function Capability({ enabled, label }: { enabled: boolean; label: string }) {
  return (
    <span className={`rounded-xl px-2 py-1 font-medium ${enabled ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'}`}>
      {label}
    </span>
  );
}

function getProviderStatusLabel(status: string) {
  if (status === 'active') return 'Активен';
  if (status === 'requires-setup') return 'Нужна настройка';
  if (status === 'disabled') return 'Выключен';
  return status;
}

function getProviderStatusClass(status: string) {
  if (status === 'active') return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  if (status === 'requires-setup') return 'border-amber-200 bg-amber-50 text-amber-700';
  if (status === 'disabled') return 'border-slate-200 bg-white text-slate-600';
  return 'border-slate-200 bg-white text-slate-700';
}
