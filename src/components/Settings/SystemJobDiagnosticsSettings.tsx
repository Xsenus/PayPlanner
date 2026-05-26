import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, CheckCircle2, Clock3, Play, RefreshCw } from 'lucide-react';
import { apiService, type SystemJobDiagnosticsDto, type SystemJobDiagnosticsItemDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

function formatDateTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  try {
    return new Date(value).toLocaleString('ru-RU');
  } catch {
    return value;
  }
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
}: {
  item: SystemJobDiagnosticsItemDto;
  onRun: (jobKey: string) => void;
  running: boolean;
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

      <div className="mt-4 grid gap-3 text-sm text-slate-600 sm:grid-cols-2 xl:grid-cols-4">
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
          <div className="mt-1 text-slate-900">{formatDateTime(item.lastRunFinishedAt)}</div>
        </div>
      </div>

      <div className="mt-4 flex flex-wrap items-center gap-2 text-sm">
        <span className={`rounded-full border px-3 py-1 font-semibold ${getRunStatusClasses(item.lastRunStatus)}`}>
          {item.lastRunStatusText || 'Запуск не зафиксирован'}
        </span>
        {item.lastRunStartedAt ? (
          <span className="text-slate-500">Начало: {formatDateTime(item.lastRunStartedAt)}</span>
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

      {item.lastActivityAt || item.lastActivityDescription || item.nextRunHint ? (
        <div className="mt-4 space-y-2 text-sm leading-6 text-slate-600">
          {item.lastActivityAt ? <p>Последняя видимая активность: {formatDateTime(item.lastActivityAt)}</p> : null}
          {item.lastActivityDescription ? <p>{item.lastActivityDescription}</p> : null}
          {item.nextRunHint ? <p>{item.nextRunHint}</p> : null}
        </div>
      ) : null}
    </article>
  );
}

export function SystemJobDiagnosticsSettings() {
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
          Обновлено: {formatDateTime(diagnostics?.generatedAt)}
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
          />
        ))}
      </div>
    </div>
  );
}
