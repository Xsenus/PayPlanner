import { useEffect, useRef, useState } from 'react';
import { apiService, type StorageCasePublicationOperationDto, type StorageCasePublicationPageDto, type StorageOperationStatus } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { SystemSelect } from '../ui/SystemSelect';

const labels: Record<StorageOperationStatus, string> = {
  pending: 'В очереди', running: 'Выполняется', unknown: 'Результат не подтверждён',
  succeeded: 'Завершено', failed: 'Ошибка', deadLetter: 'Автоматические попытки исчерпаны',
};

export function StorageCasePublicationQueueSettings() {
  const { confirm } = useDialog();
  const toast = useToast();
  const timeZone = useUserTimeZone();
  const [status, setStatus] = useState<StorageOperationStatus | ''>('deadLetter');
  const [cursors, setCursors] = useState<(string | null)[]>([null]);
  const [index, setIndex] = useState(0);
  const [revision, setRevision] = useState(0);
  const [page, setPage] = useState<StorageCasePublicationPageDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [retrying, setRetrying] = useState<string | null>(null);
  const action = useRef<AbortController | null>(null);
  const after = cursors[index];

  useEffect(() => () => { action.current?.abort(); }, []);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError(null); setPage(null);
    void apiService.getStorageCasePublicationQueue({ status: status || null, after, signal: controller.signal })
      .then((result) => { if (!controller.signal.aborted) setPage(result); })
      .catch((cause: unknown) => {
        if (!controller.signal.aborted) setError(cause instanceof Error && /HTTP (401|403)\b/.test(cause.message)
          ? 'Нет доступа к очереди добавления документов.' : 'Не удалось загрузить очередь. Обновите список.');
      }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => { controller.abort(); };
  }, [status, after, revision]);

  const refresh = () => { setPage(null); setCursors([null]); setIndex(0); setRevision((value) => value + 1); };
  const retry = async (item: StorageCasePublicationOperationDto) => {
    if (!item.canRetry || item.isRetired || loading || action.current) return;
    const controller = new AbortController();
    action.current = controller;
    setRetrying(item.id); setError(null);
    try {
      const approved = await confirm({ title: 'Повторить добавление документа',
        description: `Поставить операцию для «${item.fileName}» в очередь повторно? Это ещё не означает добавления документа: нужны обязательные копии и действующие права на дело.`,
        confirmLabel: 'Поставить в очередь', cancelLabel: 'Отмена' });
      if (!approved || controller.signal.aborted) return;
      await apiService.retryStorageCasePublication(item.id, controller.signal);
      if (controller.signal.aborted) return;
      toast.success('Операция поставлена в очередь. Завершение можно проверить по её состоянию.', 'Хранилище');
      refresh();
    } catch (cause) {
      if (controller.signal.aborted) return;
      setPage(null);
      setError(cause instanceof Error && /HTTP (401|403)\b/.test(cause.message)
        ? 'Нет доступа к повтору операции.'
        : cause instanceof Error && /HTTP 409\b/.test(cause.message)
          ? 'Состояние операции изменилось. Обновите очередь перед повтором.'
          : 'Не удалось подтвердить постановку на повтор. Обновите очередь перед следующим действием.');
    } finally {
      action.current = null;
      if (!controller.signal.aborted) setRetrying(null);
    }
  };

  return <section aria-label="Очередь добавления документов дела" className="rounded-xl border border-slate-200 bg-white p-3 space-y-3">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div><h4 className="font-semibold text-slate-900">Добавление документов дела</h4>
        <p className="text-sm text-slate-600">Повтор использует сохранённую операцию. Готовность зависит от режима хранения, копий и прав на дело.</p></div>
      <button type="button" disabled={loading || retrying !== null} onClick={refresh} className="rounded-lg border px-3 py-2 text-sm disabled:opacity-50">Обновить очередь</button>
    </div>
    <label className="flex flex-wrap items-center gap-2 text-sm">Состояние операции
      <SystemSelect aria-label="Состояние операции" value={status} disabled={retrying !== null} onChange={(event) => {
        setStatus(event.target.value as StorageOperationStatus | ''); setCursors([null]); setIndex(0); setPage(null);
      }} className="max-w-full rounded-lg border px-2 py-1.5">
        <option value="">Все состояния</option>
        {Object.entries(labels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
      </SystemSelect>
    </label>
    {error ? <p role="alert" className="text-sm text-rose-700">{error}</p> : null}
    {loading ? <p role="status" className="text-sm text-slate-600">Загрузка очереди…</p> : null}
    {page?.items.length === 0 ? <p className="text-sm text-slate-600">Операций в выбранном состоянии нет.</p> : null}
    <ul className="space-y-2">
      {page?.items.map((item) => <li key={item.id} className="rounded-lg border p-3 text-sm">
        <div className="flex flex-wrap justify-between gap-2"><span className="min-w-0 break-all font-medium">{item.fileName}</span><span>{item.isRetired ? 'Попытка прекращена' : labels[item.status]}</span></div>
        <p className="mt-1 text-xs text-slate-600">Попыток: {item.attemptCount} · Создано: {formatDateTimeInTimeZone(item.createdAt, timeZone)}</p>
        {item.nextAttemptAt ? <p className="mt-1 text-xs text-slate-600">Следующая обработка: {formatDateTimeInTimeZone(item.nextAttemptAt, timeZone)}</p> : null}
        {item.leaseExpiresAt ? <p className="mt-1 text-xs text-slate-600">Блокировка до: {formatDateTimeInTimeZone(item.leaseExpiresAt, timeZone)}</p> : null}
        <button type="button" onClick={() => { void retry(item); }} disabled={!item.canRetry || item.isRetired || loading || retrying !== null}
          className="mt-2 rounded-md border px-2 py-1.5 text-xs font-semibold disabled:opacity-50">{retrying === item.id ? 'Ожидание…' : 'Повторить операцию'}</button>
      </li>)}
    </ul>
    {index > 0 || page?.nextCursor ? <div className="flex flex-wrap items-center gap-3 text-xs">
      <button type="button" disabled={index === 0 || loading || retrying !== null} onClick={() => setIndex((value) => value - 1)}>Предыдущая страница</button>
      <span className="whitespace-nowrap">Страница {index + 1}</span>
      <button type="button" disabled={!page?.nextCursor || loading || retrying !== null} onClick={() => {
        if (!page?.nextCursor) return;
        setCursors([...cursors.slice(0, index + 1), page.nextCursor]); setIndex((value) => value + 1);
      }}>Следующая страница</button>
    </div> : null}
  </section>;
}
