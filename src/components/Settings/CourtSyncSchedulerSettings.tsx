import { useEffect, useState } from 'react';
import { AlertTriangle, RefreshCw, Save } from 'lucide-react';
import { apiService } from '../../services/api';
import type { ClientCaseCourtSyncSettings } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

const DEFAULT_LIMITS = {
  minIntervalMinutes: 15,
  maxIntervalMinutes: 1440,
  minBatchSize: 1,
  maxBatchSize: 100,
};

function clampNumber(value: number, min: number, max: number) {
  if (!Number.isFinite(value)) return min;
  return Math.min(Math.max(Math.trunc(value), min), max);
}

export function CourtSyncSchedulerSettings() {
  const [settings, setSettings] = useState<ClientCaseCourtSyncSettings | null>(null);
  const [isEnabled, setIsEnabled] = useState(false);
  const [intervalMinutes, setIntervalMinutes] = useState('360');
  const [batchSize, setBatchSize] = useState('25');
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedMessage, setSavedMessage] = useState<string | null>(null);

  const limits = settings ?? DEFAULT_LIMITS;

  const applySettings = (next: ClientCaseCourtSyncSettings) => {
    setSettings(next);
    setIsEnabled(next.isEnabled);
    setIntervalMinutes(String(next.intervalMinutes));
    setBatchSize(String(next.batchSize));
  };

  const loadSettings = async () => {
    setLoading(true);
    setError(null);
    setSavedMessage(null);
    try {
      applySettings(await apiService.getCourtSyncSettings());
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить настройки судебной синхронизации.'));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadSettings();
  }, []);

  const saveSettings = async () => {
    setSaving(true);
    setError(null);
    setSavedMessage(null);
    const normalizedInterval = clampNumber(Number(intervalMinutes), limits.minIntervalMinutes, limits.maxIntervalMinutes);
    const normalizedBatchSize = clampNumber(Number(batchSize), limits.minBatchSize, limits.maxBatchSize);
    try {
      const updated = await apiService.updateCourtSyncSettings({
        isEnabled,
        intervalMinutes: normalizedInterval,
        batchSize: normalizedBatchSize,
      });
      applySettings(updated);
      setSavedMessage('Настройки судебной синхронизации сохранены.');
    } catch (saveError) {
      setError(extractDisplayErrorMessage(saveError, 'Не удалось сохранить настройки судебной синхронизации.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h4 className="text-xl font-semibold text-gray-900">Автосинхронизация судов</h4>
          <p className="mt-1 text-sm text-gray-600">
            Частота фоновой проверки судебных источников по открытым делам.
          </p>
        </div>
        <button
          type="button"
          onClick={() => void loadSettings()}
          disabled={loading || saving}
          className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-gray-300 px-4 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
          <span>{loading ? 'Обновляем' : 'Обновить'}</span>
        </button>
      </div>

      <div className="mt-5 grid gap-4 lg:grid-cols-[minmax(0,1fr)_180px_180px]">
        <label className="flex min-h-[86px] items-center gap-3 rounded-2xl border border-gray-200 bg-gray-50 px-4 py-3">
          <input
            type="checkbox"
            checked={isEnabled}
            onChange={(event) => setIsEnabled(event.target.checked)}
            disabled={loading || saving}
            className="h-5 w-5 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          <span>
            <span className="block text-sm font-semibold text-gray-900">Включить фоновую проверку</span>
            <span className="mt-1 block text-xs text-gray-500">Открытые дела с ссылкой sudrf.ru</span>
          </span>
        </label>

        <label className="block">
          <span className="text-sm font-medium text-gray-700">Интервал, минут</span>
          <input
            type="number"
            min={limits.minIntervalMinutes}
            max={limits.maxIntervalMinutes}
            value={intervalMinutes}
            onChange={(event) => setIntervalMinutes(event.target.value)}
            disabled={loading || saving}
            className="mt-1 h-11 w-full rounded-lg border border-gray-300 px-3 text-sm outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:bg-gray-100"
          />
        </label>

        <label className="block">
          <span className="text-sm font-medium text-gray-700">Дел за запуск</span>
          <input
            type="number"
            min={limits.minBatchSize}
            max={limits.maxBatchSize}
            value={batchSize}
            onChange={(event) => setBatchSize(event.target.value)}
            disabled={loading || saving}
            className="mt-1 h-11 w-full rounded-lg border border-gray-300 px-3 text-sm outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100 disabled:bg-gray-100"
          />
        </label>
      </div>

      {settings && !settings.featureFlagEnabled ? (
        <div className="mt-4 flex gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>Фоновая проверка будет запущена после включения внешней судебной синхронизации на сервере.</span>
        </div>
      ) : null}

      {error ? (
        <div className="mt-4 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
      ) : null}

      {savedMessage ? (
        <div className="mt-4 rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">{savedMessage}</div>
      ) : null}

      <div className="mt-5 flex justify-end">
        <button
          type="button"
          onClick={() => void saveSettings()}
          disabled={loading || saving}
          className="inline-flex h-10 items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 text-sm font-semibold text-white hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
        >
          <Save className="h-4 w-4" />
          <span>{saving ? 'Сохраняем' : 'Сохранить'}</span>
        </button>
      </div>
    </div>
  );
}
