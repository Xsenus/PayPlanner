import { useEffect, useState } from 'react';
import { apiService } from '../../services/api';
import type { DatabaseBackupCatalogCopyDto, DatabaseBackupCatalogPageDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';
import { DatabaseBackupCatalogImport } from './DatabaseBackupCatalogImport';

function copyLabel(copy: DatabaseBackupCatalogCopyDto) {
  if (copy.status === 'verified') return copy.providerKey === 'local' ? 'Локальная целостность подтверждена' : 'Проверено после отправки';
  if (copy.status === 'pending') return 'Загрузка не подтверждена';
  if (copy.status === 'failed') return 'Ошибка';
  if (copy.status === 'deleted') return 'Удалено';
  return 'Неизвестное состояние';
}

const setLabels: Record<string, string> = {
  localVerified: 'Подтверждена локальная целостность',
  replicating: 'Отправка не завершена',
  awaitingCopies: 'Нет всех выбранных копий',
  verifiedConfigured: 'Выбранные копии подтверждены',
  degraded: 'Есть ошибка или неполная настройка',
  noLocalCopy: 'Нет подтверждённой локальной копии',
};

export function DatabaseBackupCatalog({ timeZoneId, connectionNames = {} }: {
  timeZoneId?: string | null;
  connectionNames?: Record<string, string>;
}) {
  const [cursor, setCursor] = useState<string>();
  const [previous, setPrevious] = useState<Array<string | undefined>>([]);
  const [revision, setRevision] = useState(0);
  const [page, setPage] = useState<DatabaseBackupCatalogPageDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const date = (value: string) => formatDateTimeInTimeZone(value, timeZoneId) || '—';

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setPage(null);
    const load = async () => {
      try {
        const result = await apiService.getDatabaseBackupCatalog(cursor);
        if (!cancelled) setPage(result);
      } catch (failure) {
        if (!cancelled) setError(extractDisplayErrorMessage(failure, 'Не удалось загрузить журнал копий.'));
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    void load();
    return () => { cancelled = true; };
  }, [cursor, revision]);

  const refresh = () => {
    setCursor(undefined);
    setPrevious([]);
    setRevision(value => value + 1);
  };

  return (
    <section className="rounded-2xl border border-gray-200 bg-white p-3" aria-label="Журнал комплектов резервных копий">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h4 className="font-semibold text-gray-900">Журнал комплектов резервных копий</h4>
          <p className="mt-1 text-sm text-gray-600">Состояние каждой копии сохраняется отдельно, даже после удаления локального файла.</p>
        </div>
        <button type="button" onClick={refresh} disabled={loading}
          className="rounded-xl border border-slate-300 px-3 py-2 text-sm font-medium disabled:opacity-50">Обновить журнал</button>
      </div>
      {loading && <p className="mt-3 text-sm text-gray-600" role="status">Загрузка журнала…</p>}
      {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
      {!loading && !error && page?.items.length === 0 && (
        <p className="mt-3 text-sm text-gray-600">Зарегистрированных комплектов пока нет. Старые локальные копии можно проверить и добавить ниже.</p>
      )}
      {!!page?.items.length && (
        <div className="mt-3 max-h-[32rem] overflow-auto rounded-xl border border-gray-200">
          <table className="w-full text-left text-sm">
            <thead className="bg-slate-50 text-slate-600"><tr>
              <th className="p-3 font-medium">Комплект</th><th className="p-3 font-medium">Копии и последняя проверка</th>
            </tr></thead>
            <tbody className="divide-y divide-gray-200">{page.items.map(item => (
              <tr key={item.id}>
                <td className="p-3 align-top">
                  <div className="break-all font-medium text-gray-900">{item.backupRelativePath}</div>
                  <div className="mt-1 text-xs text-gray-500">{date(item.createdAt)} · {item.databaseProvider} · {item.backupFormat}</div>
                  <div className="mt-1 break-all text-xs text-gray-500">ID: {item.id}</div>
                  <p className={`mt-1 text-xs ${item.status === 'verifiedConfigured' || item.status === 'localVerified' ? 'text-emerald-700' : 'text-amber-700'}`}>
                    {setLabels[item.status] || 'Состояние комплекта не определено'}
                  </p>
                </td>
                <td className="p-3 align-top"><ul className="space-y-2">{item.copies.map(copy => (
                  <li key={`${copy.providerKey}:${copy.connectionId}`}>
                    <span className="font-medium">{copy.providerKey === 'local' ? 'Локально' : copy.providerKey === 'yandexDisk' ? 'Яндекс.Диск' : `S3: ${connectionNames[copy.connectionId] || copy.connectionId}`}</span>
                    {' — '}<span className={copy.status === 'verified' ? 'text-emerald-700' : copy.status === 'failed' ? 'text-red-700' : 'text-slate-600'}>{copyLabel(copy)}</span>
                    <div className="text-xs text-gray-500">Проверка: {copy.lastVerifiedAt ? date(copy.lastVerifiedAt) : 'не подтверждена'}{copy.providerKey !== 'local' && ` · Попыток: ${copy.attemptCount}`}</div>
                    {copy.lastError && <p className="mt-1 break-words text-xs text-red-700">{copy.lastError}</p>}
                  </li>
                ))}</ul></td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
      <div className="mt-3 flex items-center justify-between gap-3 text-sm">
        <button type="button" disabled={loading || previous.length === 0} onClick={() => {
          setCursor(previous[previous.length - 1]);
          setPrevious(values => values.slice(0, -1));
        }} className="rounded-xl border px-3 py-2 disabled:opacity-50">Назад</button>
        <span className="text-gray-500">Страница {previous.length + 1}</span>
        <button type="button" disabled={loading || !!error || !page?.nextCursor} onClick={() => {
          setPrevious(values => [...values, cursor]);
          setCursor(page?.nextCursor || undefined);
        }} className="rounded-xl border px-3 py-2 disabled:opacity-50">Следующие копии</button>
      </div>
      <p className="mt-3 text-xs text-gray-500">Это результаты последней выполненной проверки, а не подтверждение текущей доступности или восстановления. Ключи шифрования проверяются отдельно.</p>
      <DatabaseBackupCatalogImport onImported={refresh} />
    </section>
  );
}
