import { useEffect, useState } from 'react';
import { apiService } from '../../services/api';
import type { DatabaseBackupAttemptPageDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

const statuses: Record<string, string> = {
  running: 'Выполняется', succeeded: 'Запуск завершён', degraded: 'Локальная копия создана, есть проблемы с отправкой',
  failed: 'Ошибка', cancelled: 'Отменён', interrupted: 'Прерван до подтверждения завершения',
};
const stages: Record<string, string> = {
  preparing: 'Подготовка', database: 'Создание копии базы', files: 'Сбор файлов',
  verifying: 'Проверка локального комплекта', uploading: 'Отправка в хранилища', cleanup: 'Очистка старых копий', completed: 'Завершение',
};

export function DatabaseBackupAttempts({ timeZoneId }: { timeZoneId?: string | null }) {
  const [cursor, setCursor] = useState<string>();
  const [previous, setPrevious] = useState<Array<string | undefined>>([]);
  const [revision, setRevision] = useState(0);
  const [page, setPage] = useState<DatabaseBackupAttemptPageDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const date = (value: string) => formatDateTimeInTimeZone(value, timeZoneId) || '—';
  useEffect(() => {
    let cancelled = false;
    setPage(null); setLoading(true); setError(null);
    void apiService.getDatabaseBackupAttempts(cursor).then(result => {
      if (!cancelled) setPage(result);
    }).catch(failure => {
      if (!cancelled) setError(extractDisplayErrorMessage(failure, 'Не удалось загрузить историю запусков.'));
    }).finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [cursor, revision]);
  return (
    <section aria-label="История запусков резервирования" className="rounded-2xl border border-gray-200 bg-white p-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h4 className="font-semibold text-gray-900">История запусков резервирования</h4>
          <p className="mt-1 text-sm text-gray-600">Включает ошибки до создания копии. Завершённый запуск не заменяет проверку копий и пробное восстановление.</p>
        </div>
        <button type="button" disabled={loading} onClick={() => { setCursor(undefined); setPrevious([]); setRevision(value => value + 1); }}
          className="rounded-xl border px-3 py-2 text-sm disabled:opacity-50">Обновить историю</button>
      </div>
      {loading && <p role="status" className="mt-3 text-sm text-gray-600">Загрузка истории…</p>}
      {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
      {!loading && !error && page?.items.length === 0 && <p className="mt-3 text-sm text-gray-600">Сохранённых запусков пока нет. История старых копий не создаётся задним числом.</p>}
      {!!page?.items.length && <ul className="mt-3 max-h-[32rem] space-y-3 overflow-auto">{page.items.map(item => (
        <li key={item.id} className="rounded-xl border border-gray-200 p-3 text-sm">
          <div className="flex flex-wrap justify-between gap-2">
            <span className="font-medium">{item.trigger === 'manual' ? 'Ручной запуск' : item.trigger === 'auto' ? 'По расписанию' : 'Другой запуск'} · {date(item.startedAt)}</span>
            <span className={item.status === 'succeeded' ? 'text-emerald-700' : item.status === 'failed' ? 'text-red-700' : 'text-amber-700'}>{statuses[item.status] || 'Неизвестный результат'}</span>
          </div>
          <p className="mt-1">Последний этап: {stages[item.stage] || 'Неизвестный этап'}</p>
          <p className="mt-1 text-xs text-gray-500">{item.completedAt ? `Окончание: ${date(item.completedAt)}` : `Обновлено: ${date(item.updatedAt)} · Завершение не подтверждено`}</p>
          {item.scheduleSlot && <p className="mt-1 break-all text-xs text-gray-500">Слот расписания: {item.scheduleSlot}</p>}
          <p className="mt-1 break-all text-xs text-gray-500">ID запуска: {item.id}</p>
          {item.backupSetId ? <p className="mt-1 break-all text-xs text-gray-500">ID комплекта: {item.backupSetId}. Его копии показаны в журнале комплектов.</p>
            : <p className="mt-1 text-xs text-gray-500">Связь с готовым комплектом отсутствует.</p>}
          {item.error && <p className="mt-1 break-words text-xs text-red-700">{item.error}</p>}
        </li>
      ))}</ul>}
      <div className="mt-3 flex items-center justify-between gap-3 text-sm">
        <button type="button" disabled={loading || previous.length === 0} onClick={() => {
          setCursor(previous[previous.length - 1]); setPrevious(values => values.slice(0, -1));
        }} className="rounded-xl border px-3 py-2 disabled:opacity-50">Назад</button>
        <span className="text-gray-500">Страница {previous.length + 1}</span>
        <button type="button" disabled={loading || !page?.nextCursor} onClick={() => {
          setPrevious(values => [...values, cursor]); setCursor(page?.nextCursor ?? undefined);
        }} className="rounded-xl border px-3 py-2 disabled:opacity-50">Далее</button>
      </div>
    </section>
  );
}
