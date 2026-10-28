import { useEffect, useMemo, useState } from 'react';
import { apiService, type TaskTimerCorrections, type TaskTimerHistory, type TaskTimerState } from '../../services/api';
import { formatDateTimeWithSecondsInTimeZone, parseInstant } from '../../utils/timeZoneDate';

const formatSeconds = (seconds: number) => `${Math.floor(seconds / 60)} мин${seconds % 60 ? ` ${seconds % 60} с` : ''}`;
const intervalSeconds = (started: string, stopped: string) => Math.floor(Math.max(0, parseInstant(stopped).getTime() - parseInstant(started).getTime()) / 1000);

function acknowledgeRecoveredStart(key: string, value: TaskTimerState | null | undefined) {
  if (!value) return;
  try {
    if (window.localStorage.getItem(key) === value.id) window.localStorage.removeItem(key);
  } catch { /* The confirmed running timer remains usable if browser storage is unavailable. */ }
}

function TaskTimerElapsed({ startedAtUtc }: { startedAtUtc: string }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const interval = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(interval);
  }, []);
  return <p className="text-sm font-medium text-slate-700">Прошло примерно: {formatSeconds(Math.max(0, Math.floor((now - parseInstant(startedAtUtc).getTime()) / 1000)))}</p>;
}

/** The server owns the running state; the stored key makes an interrupted start safe to retry. */
export function TaskTimerPanel({ taskId, userId, canStart, timeZoneId }: { taskId: number; userId: number; canStart: boolean; timeZoneId?: string }) {
  const [timer, setTimer] = useState<TaskTimerState | null>(null);
  const [known, setKnown] = useState(false);
  const [history, setHistory] = useState<TaskTimerHistory | null>(null);
  const [corrections, setCorrections] = useState<(TaskTimerCorrections & { entryId: string }) | null>(null);
  const [editing, setEditing] = useState<TaskTimerState | null>(null);
  const [correctionNeedsRefresh, setCorrectionNeedsRefresh] = useState(false);
  const [correctionResultUncertain, setCorrectionResultUncertain] = useState(false);
  const [minutes, setMinutes] = useState('');
  const [reason, setReason] = useState('');
  const correctionAlreadyMatches = Boolean(correctionResultUncertain && !correctionNeedsRefresh && editing
    && editing.correctedSeconds !== null && /^\d+$/.test(minutes) && Number(minutes) * 60 === editing.correctedSeconds);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const storageKey = `payplanner.task-timer.start.${userId}.${taskId}`;
  const completed = useMemo(() => {
    const total = { count: 0, seconds: 0 };
    for (const entry of history?.items ?? []) {
      if (!entry.stoppedAtUtc) continue;
      total.count++;
      total.seconds += entry.correctedSeconds ?? intervalSeconds(entry.startedAtUtc, entry.stoppedAtUtc);
    }
    return total;
  }, [history]);

  useEffect(() => {
    let mounted = true;
    setLoading(true);
    setKnown(false);
    setError('');
    apiService.getCurrentTaskTimer().then((value) => {
      if (mounted) { acknowledgeRecoveredStart(storageKey, value); setTimer(value ?? null); setKnown(true); }
    }).catch(() => {
      if (mounted) setError('Не удалось загрузить состояние таймера. Откройте карточку снова.');
    }).finally(() => { if (mounted) setLoading(false); });
    return () => { mounted = false; };
  }, [taskId, userId, storageKey]);

  const start = async () => {
    setLoading(true);
    setError('');
    try {
      let requestId = window.localStorage.getItem(storageKey);
      if (!requestId) {
        requestId = crypto.randomUUID();
        window.localStorage.setItem(storageKey, requestId);
        if (window.localStorage.getItem(storageKey) !== requestId) throw new Error('Storage unavailable');
      }
      const result = await apiService.startTaskTimer(taskId, requestId);
      if (window.localStorage.getItem(storageKey) === requestId) window.localStorage.removeItem(storageKey);
      setTimer(result.stoppedAtUtc ? null : result);
      if (result.stoppedAtUtc) setError('Предыдущий запуск уже завершён. При необходимости запустите новый таймер.');
    } catch {
      setKnown(false);
      setError('Не удалось подтвердить запуск. Обновите состояние и повторите действие.');
    } finally { setLoading(false); }
  };

  const stop = async () => {
    if (!timer) return;
    setLoading(true);
    setError('');
    try {
      await apiService.stopTaskTimer(timer.id);
      setTimer(null);
      setHistory(null);
      setCorrections(null);
    } catch {
      setKnown(false);
      setError('Не удалось подтвердить остановку. Повторите действие или обновите состояние.');
    } finally { setLoading(false); }
  };

  const refresh = async () => {
    setLoading(true);
    setError('');
    try { const value = await apiService.getCurrentTaskTimer(); acknowledgeRecoveredStart(storageKey, value); setTimer(value ?? null); setKnown(true); }
    catch { setKnown(false); setError('Не удалось загрузить состояние таймера.'); }
    finally { setLoading(false); }
  };

  const loadHistory = async (more = false) => {
    setLoading(true);
    setError('');
    if (!more) { setHistory(null); setCorrections(null); }
    try {
      const next = await apiService.getTaskTimerHistory(taskId, more ? history?.nextCursor ?? undefined : undefined);
      if (editing && correctionNeedsRefresh) {
        const refreshed = next.items.find((item) => item.id === editing.id && item.stoppedAtUtc);
        if (refreshed) { setEditing(refreshed); setCorrectionNeedsRefresh(false); }
        else setError(next.hasMore
          ? 'Запись не найдена в загруженной части истории. Загрузите более ранние записи.'
          : 'Запись не найдена в истории. Проверьте доступ или откройте карточку заново.');
      }
      setHistory((previous) => {
        if (!more || !previous) return next;
        const known = new Set(previous.items.map((item) => item.id));
        return { ...next, items: [...previous.items, ...next.items.filter((item) => !known.has(item.id))] };
      });
    }
    catch {
      if (!more) { setHistory(null); setCorrections(null); }
      setError(more ? 'Не удалось загрузить более ранние записи. Повторите действие.' : 'Не удалось загрузить историю времени.');
    }
    finally { setLoading(false); }
  };

  const loadCorrections = async (entryId: string, more = false) => {
    setLoading(true);
    setError('');
    try {
      const next = await apiService.getTaskTimerCorrections(taskId, entryId, more ? corrections?.nextBeforeId ?? undefined : undefined);
      setCorrections((previous) => {
        if (!more || previous?.entryId !== entryId) return { ...next, entryId };
        const known = new Set(previous.items.map((item) => item.id));
        return { ...next, entryId, items: [...previous.items, ...next.items.filter((item) => !known.has(item.id))] };
      });
    } catch {
      if (!more) setCorrections(null);
      setError(more ? 'Не удалось загрузить более ранние исправления. Повторите действие.' : 'Не удалось загрузить историю исправлений.');
    }
    finally { setLoading(false); }
  };

  const correct = async () => {
    if (correctionNeedsRefresh || correctionAlreadyMatches) return;
    if (!editing || !/^\d+$/.test(minutes) || Number(minutes) > Math.floor(2147483647 / 60) || !reason.trim() || reason.trim().length > 500) {
      setError('Укажите целое число минут и причину исправления (до 500 символов).');
      return;
    }
    setLoading(true);
    setError('');
    try {
      const result = await apiService.correctTaskTimer(taskId, editing.id, editing.revision, Number(minutes) * 60, reason.trim());
      setHistory((previous) => previous ? { ...previous, items: previous.items.map((item) => item.id === result.id ? result : item) } : previous);
      setCorrections(null);
      setEditing(null);
      setCorrectionNeedsRefresh(false);
      setCorrectionResultUncertain(false);
      setMinutes('');
      setReason('');
    } catch {
      setHistory(null);
      setCorrections(null);
      setCorrectionNeedsRefresh(true);
      setCorrectionResultUncertain(true);
      setError('Не удалось подтвердить исправление. Загрузите историю заново и проверьте запись.');
    } finally { setLoading(false); }
  };

  return (
    <section className="space-y-2 rounded-lg border border-slate-200 bg-white p-3" aria-label="Фактическое время">
      <h3 className="text-sm font-semibold text-slate-800">Фактическое время</h3>
      <p className="text-sm text-slate-600">
        {!known ? 'Состояние таймера не подтверждено.' : timer ? (timer.taskId === taskId ? 'Таймер этой задачи запущен.' : 'У вас уже запущен таймер другой задачи.') : 'Таймер не запущен.'}
      </p>
      {timer ? <p className="text-xs text-slate-500">Начало: {formatDateTimeWithSecondsInTimeZone(timer.startedAtUtc, timeZoneId)}</p> : null}
      {timer ? <TaskTimerElapsed startedAtUtc={timer.startedAtUtc} /> : null}
      <p className="text-xs text-slate-500">Учёт времени не создаёт оплату. Одновременно может работать один ваш таймер.</p>
      {error ? <p role="alert" className="text-sm text-rose-700">{error}</p> : null}
      <div className="flex flex-wrap gap-2">
        {timer ? (
          <button type="button" disabled={loading} onClick={() => void stop()} className="rounded-lg bg-slate-800 px-3 py-2 text-sm font-semibold text-white disabled:opacity-50">Остановить таймер</button>
        ) : (
          <button type="button" disabled={loading || !known || !canStart} onClick={() => void start()} className="rounded-lg bg-blue-700 px-3 py-2 text-sm font-semibold text-white disabled:opacity-50">Запустить таймер</button>
        )}
        <button type="button" disabled={loading} onClick={() => void refresh()} className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-700 disabled:opacity-50">Обновить состояние</button>
        <button type="button" disabled={loading || !canStart} onClick={() => void loadHistory()} className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-700 disabled:opacity-50">Моя история времени</button>
      </div>
      {history ? (
        <div className="space-y-1 text-sm text-slate-600">
          {completed.count > 0 ? <p className="font-semibold">Завершённые интервалы{history.hasMore ? ' (загруженные)' : ''}: {formatSeconds(completed.seconds)}</p> : null}
          {history.items.length === 0 ? <p>Записей пока нет.</p> : history.items.map((entry) => (
            <div key={entry.id}>
              <p>{formatDateTimeWithSecondsInTimeZone(entry.startedAtUtc, timeZoneId)} — {entry.stoppedAtUtc ? formatDateTimeWithSecondsInTimeZone(entry.stoppedAtUtc, timeZoneId) : 'работает'}
                {entry.stoppedAtUtc ? ` · ${formatSeconds(entry.correctedSeconds ?? intervalSeconds(entry.startedAtUtc, entry.stoppedAtUtc))}${entry.correctedSeconds !== null ? ' (исправлено)' : ''}` : ''}
              </p>
              {entry.stoppedAtUtc && canStart ? <button type="button" disabled={loading} onClick={() => { setEditing(entry); setCorrectionNeedsRefresh(false); setCorrectionResultUncertain(false); setMinutes(''); setReason(''); }} className="text-sm text-blue-700 underline disabled:opacity-50">Исправить длительность</button> : null}
              {entry.stoppedAtUtc && canStart ? <button type="button" disabled={loading} onClick={() => void loadCorrections(entry.id)} className="ml-3 text-sm text-blue-700 underline disabled:opacity-50">История исправлений</button> : null}
              {entry.stoppedAtUtc && corrections?.entryId === entry.id ? (
                <div className="mt-1 space-y-1 border-l-2 border-slate-200 pl-2 text-xs">
                  {corrections.items.length === 0 ? <p>Исправлений пока нет.</p> : corrections.items.map((change) => (
                    <p key={change.id}>{formatDateTimeWithSecondsInTimeZone(change.createdAtUtc, timeZoneId)} · {formatSeconds(change.previousSeconds ?? intervalSeconds(entry.startedAtUtc, entry.stoppedAtUtc!))} → {formatSeconds(change.newSeconds)} · {change.reason}</p>
                  ))}
                  {corrections.nextBeforeId !== null ? <button type="button" disabled={loading} onClick={() => void loadCorrections(entry.id, true)} className="text-blue-700 underline disabled:opacity-50">Показать более ранние исправления</button> : null}
                </div>
              ) : null}
            </div>
          ))}
          {history.hasMore && history.nextCursor ? <button type="button" disabled={loading} onClick={() => void loadHistory(true)} className="text-blue-700 underline disabled:opacity-50">Показать более ранние записи</button> : null}
        </div>
      ) : null}
      {editing ? (
        <div className="space-y-2 rounded-lg bg-slate-50 p-3">
          {!correctionNeedsRefresh && editing.stoppedAtUtc ? <p className="text-xs text-slate-600">Текущая длительность: {formatSeconds(editing.correctedSeconds ?? intervalSeconds(editing.startedAtUtc, editing.stoppedAtUtc))}</p> : null}
          <label className="block text-sm">Длительность, целых минут<input type="number" min="0" step="1" value={minutes} onChange={(event) => setMinutes(event.target.value)} className="ml-2 w-28 rounded border border-slate-300 px-2 py-1" /></label>
          <label className="block text-sm">Причина<input value={reason} maxLength={500} onChange={(event) => setReason(event.target.value)} className="mt-1 w-full rounded border border-slate-300 px-2 py-1" /></label>
          <p className="text-xs text-slate-500">Начало и окончание останутся в истории; причина исправления сохранится.</p>
          {correctionAlreadyMatches ? <p className="text-xs text-amber-800">Указанная длительность уже установлена. Проверьте историю исправлений, прежде чем продолжить.</p> : null}
          <div className="flex gap-2"><button type="button" disabled={loading || correctionNeedsRefresh || correctionAlreadyMatches} onClick={() => void correct()} className="rounded bg-blue-700 px-3 py-1 text-white disabled:opacity-50">Сохранить исправление</button><button type="button" disabled={loading} onClick={() => { setEditing(null); setCorrectionNeedsRefresh(false); setCorrectionResultUncertain(false); }} className="px-3 py-1">Отмена</button></div>
        </div>
      ) : null}
    </section>
  );
}
