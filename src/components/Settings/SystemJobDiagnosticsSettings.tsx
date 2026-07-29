import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, Clock3, Play, RefreshCw } from 'lucide-react';
import { apiService, type SystemJobDiagnosticsDto, type SystemJobDiagnosticsItemDto } from '../../services/api';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

function formatDateTime(value?: string | null, timeZoneId?: string | null) {
  if (!value) {
    return '—';
  }

  try {
    return formatDateTimeInTimeZone(value, timeZoneId) || value;
  } catch {
    return value;
  }
}

function formatDuration(durationMs?: number | null) {
  if (durationMs == null) {
    return '—';
  }

  const safeDurationMs = Math.max(0, Math.round(durationMs));
  if (safeDurationMs < 1000) {
    return `${safeDurationMs} мс`;
  }

  const totalSeconds = Math.round(safeDurationMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;

  if (minutes > 0) {
    return `${minutes} мин. ${seconds.toString().padStart(2, '0')} сек.`;
  }

  return `${seconds} сек.`;
}

function formatBytes(bytes?: number | null) {
  const safeBytes = Math.max(0, bytes ?? 0);
  if (safeBytes < 1024) return `${safeBytes} Б`;
  if (safeBytes < 1024 * 1024) return `${(safeBytes / 1024).toFixed(1)} КБ`;
  return `${(safeBytes / (1024 * 1024)).toFixed(1)} МБ`;
}

function formatMetricDate(value?: string | null) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '');
  return match ? `${match[3]}.${match[2]}.${match[1]}` : value || '—';
}

function getStatusClasses(status: string) {
  if (status === 'active') {
    return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  }

  if (status === 'blocked') {
    return 'border-amber-200 bg-amber-50 text-amber-700';
  }

  return 'border-slate-200 bg-slate-50 text-slate-600';
}

function getRunStatusClasses(status?: string | null) {
  if (status === 'success') {
    return 'border-emerald-200 bg-emerald-50 text-emerald-700';
  }

  if (status === 'error') {
    return 'border-red-200 bg-red-50 text-red-700';
  }

  return 'border-slate-200 bg-slate-50 text-slate-600';
}

function JobStatusIcon({ status }: { status: string }) {
  if (status === 'active') {
    return <CheckCircle2 className="h-5 w-5 text-emerald-600" />;
  }

  if (status === 'blocked') {
    return <AlertTriangle className="h-5 w-5 text-amber-600" />;
  }

  return <Clock3 className="h-5 w-5 text-slate-500" />;
}

function JobDiagnosticsCard({
  item,
  onRun,
  running,
  timeZoneId,
}: {
  item: SystemJobDiagnosticsItemDto;
  onRun: (jobKey: string) => void;
  running: boolean;
  timeZoneId?: string | null;
}) {
  return (
    <article className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-start gap-3">
          <JobStatusIcon status={item.status} />
          <div>
            <h4 className="text-base font-semibold text-slate-900">{item.name}</h4>
            <p className="mt-1 text-sm leading-6 text-slate-600">{item.description}</p>
          </div>
        </div>
        <span className={`shrink-0 rounded-full border px-3 py-1 text-xs font-semibold ${getStatusClasses(item.status)}`}>
          {item.statusText || (item.enabled ? 'Включено' : 'Выключено')}
        </span>
      </div>

      <div className="mt-4 grid gap-3 text-sm text-slate-600 sm:grid-cols-2 xl:grid-cols-6">
        <div className="rounded-xl bg-slate-50 p-3">
          <div className="font-medium text-slate-500">Периодичность</div>
          <div className="mt-1 text-slate-900">{item.intervalText || '—'}</div>
        </div>
        <div className="rounded-xl bg-slate-50 p-3">
          <div className="font-medium text-slate-500">В очереди</div>
          <div className="mt-1 text-slate-900">{item.pendingCount ?? '—'}</div>
        </div>
        <div className="rounded-xl bg-slate-50 p-3">
          <div className="font-medium text-slate-500">Лимит/результат</div>
          <div className="mt-1 text-slate-900">{item.lastProcessedCount ?? '—'}</div>
        </div>
        <div className="rounded-xl bg-slate-50 p-3">
          <div className="font-medium text-slate-500">Последний запуск</div>
          <div className="mt-1 text-slate-900">{formatDateTime(item.lastRunFinishedAt, timeZoneId)}</div>
        </div>
        <div className="rounded-xl bg-slate-50 p-3">
          <div className="font-medium text-slate-500">Последний успех</div>
          <div className="mt-1 text-slate-900">{formatDateTime(item.lastSuccessAt, timeZoneId)}</div>
        </div>
        <div className="rounded-xl bg-slate-50 p-3">
          <div className="font-medium text-slate-500">Длительность</div>
          <div className="mt-1 text-slate-900">{formatDuration(item.lastRunDurationMs)}</div>
        </div>
      </div>

      <div className="mt-4 flex flex-wrap items-center gap-2 text-sm">
        <span className={`rounded-full border px-3 py-1 font-semibold ${getRunStatusClasses(item.lastRunStatus)}`}>
          {item.lastRunStatusText || 'Запуск не зафиксирован'}
        </span>
        {item.lastRunStartedAt ? (
          <span className="text-slate-500">Начало: {formatDateTime(item.lastRunStartedAt, timeZoneId)}</span>
        ) : null}
        {item.lastRunError ? (
          <span className="text-red-700">{item.lastRunError}</span>
        ) : null}
        {item.manualRunEnabled ? (
          <button
            type="button"
            onClick={() => onRun(item.key)}
            disabled={running}
            className="ml-auto inline-flex items-center gap-2 rounded-xl border border-blue-200 bg-blue-50 px-3 py-1.5 font-semibold text-blue-700 hover:bg-blue-100 disabled:opacity-60"
            title={item.manualRunHint || 'Запустить сервис вручную'}
          >
            {running ? <RefreshCw className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
            Запустить
          </button>
        ) : null}
      </div>

      {item.courtSyncMetrics ? (
        <section className="mt-4 rounded-xl border border-blue-100 bg-blue-50/60 p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h5 className="text-sm font-semibold text-slate-900">Метрики судебных источников</h5>
            <span className="text-xs text-slate-500">
              С {formatDateTime(item.courtSyncMetrics.startedAt, timeZoneId)}
            </span>
          </div>
          <div className="mt-3 grid grid-cols-2 gap-2 text-sm sm:grid-cols-3 xl:grid-cols-6">
            <MetricValue label="Запросы" value={item.courtSyncMetrics.requestCount} />
            <MetricValue label="Ошибки" value={item.courtSyncMetrics.failedRequestCount} />
            <MetricValue
              label="Средняя / максимум"
              value={`${formatDuration(item.courtSyncMetrics.averageLatencyMs)} / ${formatDuration(item.courtSyncMetrics.maxLatencyMs)}`}
            />
            <MetricValue label="Повторы исключены" value={item.courtSyncMetrics.duplicateSkipCount} />
            <MetricValue label="Документы" value={item.courtSyncMetrics.documentCount} />
            <MetricValue label="Объём документов" value={formatBytes(item.courtSyncMetrics.documentBytes)} />
          </div>
          {item.courtSyncMetrics.retained ? (
            <div className="mt-3 border-t border-blue-100 pt-3">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h6 className="text-xs font-semibold uppercase tracking-wide text-slate-600">
                  Сводка за {item.courtSyncMetrics.retained.retentionDays} дней
                </h6>
                <span className="text-xs text-slate-500">
                  {item.courtSyncMetrics.retained.periodStartedOn && item.courtSyncMetrics.retained.periodEndedOn
                    ? `${formatMetricDate(item.courtSyncMetrics.retained.periodStartedOn)} — ${formatMetricDate(item.courtSyncMetrics.retained.periodEndedOn)}`
                    : 'Данные накопятся после синхронизации'}
                </span>
              </div>
              <div className="mt-2 grid grid-cols-2 gap-2 text-sm sm:grid-cols-3 xl:grid-cols-6">
                <MetricValue label="Запросы" value={item.courtSyncMetrics.retained.requestCount} />
                <MetricValue label="Ошибки" value={item.courtSyncMetrics.retained.failedRequestCount} />
                <MetricValue
                  label="Средняя / максимум"
                  value={`${formatDuration(item.courtSyncMetrics.retained.averageLatencyMs)} / ${formatDuration(item.courtSyncMetrics.retained.maxLatencyMs)}`}
                />
                <MetricValue label="Повторы исключены" value={item.courtSyncMetrics.retained.duplicateSkipCount} />
                <MetricValue label="Документы" value={item.courtSyncMetrics.retained.documentCount} />
                <MetricValue label="Объём документов" value={formatBytes(item.courtSyncMetrics.retained.documentBytes)} />
              </div>
            </div>
          ) : null}
        </section>
      ) : null}

      {item.lastActivityAt || item.lastActivityDescription || item.nextRunHint ? (
        <div className="mt-4 space-y-2 text-sm leading-6 text-slate-600">
          {item.lastActivityAt ? <p>Последняя видимая активность: {formatDateTime(item.lastActivityAt, timeZoneId)}</p> : null}
          {item.lastActivityDescription ? <p>{item.lastActivityDescription}</p> : null}
          {item.nextRunHint ? <p>{item.nextRunHint}</p> : null}
        </div>
      ) : null}
    </article>
  );
}

function MetricValue({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="rounded-lg bg-white px-3 py-2">
      <div className="text-xs text-slate-500">{label}</div>
      <div className="mt-1 font-semibold text-slate-900">{value}</div>
    </div>
  );
}

export function SystemJobDiagnosticsSettings() {
  const timeZoneId = useUserTimeZone();
  const [diagnostics, setDiagnostics] = useState<SystemJobDiagnosticsDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [runningJobKey, setRunningJobKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [runError, setRunError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setDiagnostics(await apiService.getSystemJobDiagnostics());
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось загрузить диагностику фоновых сервисов.'));
    } finally {
      setLoading(false);
    }
  }, []);

  const runJob = useCallback(async (jobKey: string) => {
    setRunningJobKey(jobKey);
    setRunError(null);
    try {
      const result = await apiService.runSystemJob(jobKey);
      setDiagnostics(result.diagnostics);
    } catch (err) {
      setRunError(extractDisplayErrorMessage(err, 'Не удалось запустить фоновый сервис.'));
    } finally {
      setRunningJobKey(null);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const summary = useMemo(() => {
    const items = diagnostics?.items ?? [];
    return {
      total: items.length,
      active: items.filter((item) => item.status === 'active').length,
      blocked: items.filter((item) => item.status === 'blocked').length,
      disabled: items.filter((item) => item.status !== 'active' && item.status !== 'blocked').length,
    };
  }, [diagnostics]);

  return (
    <div className="space-y-6">
      <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div>
            <h3 className="text-xl font-semibold text-slate-900">Фоновые сервисы</h3>
            <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-600">
              Общая диагностика показывает, какие автоматические процессы включены, сколько объектов ожидает обработки и когда была последняя видимая активность.
            </p>
          </div>
          <button
            type="button"
            onClick={() => void load()}
            disabled={loading}
            className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-60"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} />
            Обновить
          </button>
        </div>

        <div className="mt-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <div className="rounded-2xl bg-slate-50 p-4">
            <div className="text-sm text-slate-500">Всего сервисов</div>
            <div className="mt-1 text-2xl font-semibold text-slate-900">{summary.total}</div>
          </div>
          <div className="rounded-2xl bg-emerald-50 p-4">
            <div className="text-sm text-emerald-700">Включено</div>
            <div className="mt-1 text-2xl font-semibold text-emerald-900">{summary.active}</div>
          </div>
          <div className="rounded-2xl bg-amber-50 p-4">
            <div className="text-sm text-amber-700">Ожидает условий</div>
            <div className="mt-1 text-2xl font-semibold text-amber-900">{summary.blocked}</div>
          </div>
          <div className="rounded-2xl bg-slate-50 p-4">
            <div className="text-sm text-slate-500">Выключено</div>
            <div className="mt-1 text-2xl font-semibold text-slate-900">{summary.disabled}</div>
          </div>
        </div>

        <div className="mt-4 text-sm text-slate-500">
          Обновлено: {formatDateTime(diagnostics?.generatedAt, timeZoneId)}
        </div>
      </div>

      {error ? (
        <div className="rounded-2xl border border-red-200 bg-red-50 p-4 text-sm text-red-700">
          {error}
        </div>
      ) : null}

      {runError ? (
        <div className="rounded-2xl border border-red-200 bg-red-50 p-4 text-sm text-red-700">
          {runError}
        </div>
      ) : null}

      {loading && !diagnostics ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-500">
          Загружаем диагностику...
        </div>
      ) : null}

      <div className="space-y-4">
        {(diagnostics?.items ?? []).map((item) => (
          <JobDiagnosticsCard
            key={item.key}
            item={item}
            onRun={runJob}
            running={runningJobKey === item.key}
            timeZoneId={timeZoneId}
          />
        ))}
      </div>
    </div>
  );
}
