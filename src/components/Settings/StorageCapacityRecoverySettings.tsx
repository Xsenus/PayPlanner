import { useEffect, useState } from 'react';
import { apiService, type StorageCapacityHealthDto, type StorageStageRecoveryReportDto } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

const STATUS_LABELS: Record<string, string> = {
  eligible: 'Полный файл — можно подтвердить',
  settled: 'Подтверждено',
  incompleteFile: 'Файл неполный — резерв сохранён',
  unavailableFile: 'Файл недоступен — резерв сохранён',
  invalidAuthority: 'Путь требует разбора — резерв сохранён',
  changedVolume: 'Том изменился — резерв сохранён',
  unsafeFile: 'Небезопасный файл — резерв сохранён',
  writerNotProvenStopped: 'Запись файла может продолжаться — резерв сохранён',
  eligibleMissingStage: 'Файл отсутствует, запись остановлена — резерв можно освободить',
  releasedMissingStage: 'Файл отсутствует — резерв освобождён',
  eligibleReleasedMarker: 'Резерв освобождён — служебный маркер можно удалить',
  releasedMarkerRemoved: 'Служебный маркер удалён',
  markerRetained: 'Удаление не подтверждено — маркер сохранён',
  markerAlreadyAbsent: 'Служебного маркера нет',
  markerNotProvenStopped: 'Запись или подлинность маркера не подтверждены — маркер сохранён',
  changedAuthority: 'Подтверждение резерва изменилось — маркер сохранён',
};

async function reconcileReport(releasedMarkers: boolean, params: { apply?: boolean; afterKey?: string | null; limit: number }): Promise<StorageStageRecoveryReportDto> {
  if (!releasedMarkers) return apiService.reconcileStorageStages(params);
  const report = await apiService.reconcileStorageStageMarkers(params);
  return { ...report, settled: report.removed, retained: report.inspected - report.removed };
}

export function StorageCapacityRecoverySettings({ releasedMarkers = false }: { releasedMarkers?: boolean } = {}) {
  const { confirm } = useDialog();
  const toast = useToast();
  const timeZone = useUserTimeZone();
  const [afterKey, setAfterKey] = useState<string | null>(null);
  const [previousKeys, setPreviousKeys] = useState<Array<string | null>>([]);
  const [refreshId, setRefreshId] = useState(0);
  const [loading, setLoading] = useState(true);
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [health, setHealth] = useState<StorageCapacityHealthDto | null>(null);
  const [report, setReport] = useState<StorageStageRecoveryReportDto | null>(null);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError(null);
    setHealth(null);
    setReport(null);
    void Promise.all([
      releasedMarkers ? Promise.resolve(null) : apiService.getStorageCapacityHealth(),
      reconcileReport(releasedMarkers, { afterKey, limit: 50 }),
    ]).then(([nextHealth, nextReport]) => {
      if (!current) return;
      setHealth(nextHealth);
      setReport(nextReport);
    }).catch((loadError: unknown) => {
      if (current) setError(extractDisplayErrorMessage(loadError, 'Не удалось проверить резервы хранилища.'));
    }).finally(() => {
      if (current) setLoading(false);
    });
    return () => { current = false; };
  }, [afterKey, refreshId, releasedMarkers]);

  const apply = async () => {
    if (!report?.eligible || applying || loading) return;
    const approved = await confirm({
      title: releasedMarkers ? 'Удалить служебные маркеры' : 'Подтвердить восстановление резервов',
      description: releasedMarkers
        ? `Можно удалить служебных маркеров: ${report.eligible}. CRM повторно проверит освобождение резервов и завершение записи файлов. Документы и содержимое временных файлов сохранятся.`
        : `На странице найдено резервов для восстановления: ${report.eligible}. Система повторно проверит полные файлы и отсутствие файлов остановившихся загрузок. Резервы продолжающихся загрузок и неполных файлов сохранятся.`,
      confirmLabel: releasedMarkers ? 'Удалить маркеры' : 'Подтвердить',
      cancelLabel: 'Отмена',
    });
    if (!approved) return;
    setApplying(true);
    try {
      const result = await reconcileReport(releasedMarkers, { apply: true, afterKey, limit: 50 });
      toast.success(releasedMarkers ? `Удалено служебных маркеров: ${result.settled}.` : `Восстановлено резервов: ${result.settled}.`, 'Хранилище');
      setRefreshId(value => value + 1);
    } catch (applyError) {
      setError(extractDisplayErrorMessage(applyError, releasedMarkers ? 'Не удалось очистить служебные маркеры.' : 'Не удалось подтвердить файлы.'));
    } finally {
      setApplying(false);
    }
  };

  const nextPage = () => {
    if (!report?.nextAfterKey) return;
    setPreviousKeys(keys => [...keys, afterKey]);
    setAfterKey(report.nextAfterKey);
  };

  const previousPage = () => {
    const previous = previousKeys.at(-1);
    if (previous === undefined) return;
    setPreviousKeys(keys => keys.slice(0, -1));
    setAfterKey(previous);
  };

  return (
    <div className="space-y-3">
      {!releasedMarkers && <section className="rounded-xl border border-slate-200 bg-white p-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h4 className="font-semibold text-slate-900">Резервы места для файлов</h4>
            <p className="text-sm text-slate-600">Сверка не удаляет файлы и не освобождает место у неполных загрузок.</p>
          </div>
          <button type="button" onClick={() => setRefreshId(value => value + 1)} disabled={loading || applying}
            className="rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium disabled:opacity-50">
            Обновить
          </button>
        </div>
        {health && (
          <p className="mt-3 text-sm text-slate-700">
            Ожидающих резервов всех загрузок: <strong>{health.pendingCapacityReservations}</strong>
            {health.oldestCapacityReservationAt && (
              <> · старейший: {formatDateTimeInTimeZone(health.oldestCapacityReservationAt, timeZone)}</>
            )}
          </p>
        )}
        {health && (
          <p className="mt-1 text-sm text-slate-700">
            Из них для временных файлов: <strong>{health.pendingStageCapacityReservations}</strong>
            {health.oldestStageCapacityReservationAt && (
              <> · старейший обновлён: {formatDateTimeInTimeZone(health.oldestStageCapacityReservationAt, timeZone)}</>
            )}
          </p>
        )}
      </section>}

      <section className="rounded-xl border border-slate-200 bg-white p-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h4 className="font-semibold text-slate-900">{releasedMarkers ? 'Очистка служебных маркеров' : 'Проверка прерванных загрузок'}</h4>
            <p className="text-sm text-slate-600">{releasedMarkers
              ? 'Резервы, освобождённые более 10 минут назад, по 50 записей на странице. Очистка удаляет только служебные маркеры; файлы документов и бюджет хранения сохраняются.'
              : 'Показаны только временные файлы с резервом старше 10 минут, по 50 записей на странице.'}</p>
          </div>
          <div className="flex flex-wrap gap-2">
          {releasedMarkers && <button type="button" onClick={() => setRefreshId(value => value + 1)} disabled={loading || applying}
            className="rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium disabled:opacity-50">Обновить</button>}
          <button type="button" onClick={() => void apply()}
            disabled={loading || applying || !report?.eligible}
            className="rounded-lg bg-amber-700 px-3 py-2 text-sm font-medium text-white disabled:opacity-50">
            {applying ? 'Подтверждение…' : `${releasedMarkers ? 'Очистить маркеры' : 'Подтвердить восстановление'}${report?.eligible ? ` (${report.eligible})` : ''}`}
          </button>
          </div>
        </div>
        {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
        {loading && <p className="mt-3 text-sm text-slate-500">Проверка…</p>}
        {!loading && report && (
          <>
            <p className="mt-3 text-sm text-slate-700">
              Проверено: {report.inspected}. {releasedMarkers ? 'Можно очистить маркеров' : 'Можно восстановить резервов'}: {report.eligible}. Без изменений: {report.retained}.
            </p>
            {report.items.length === 0 ? (
              <p className="mt-3 text-sm text-slate-500">{releasedMarkers ? 'На этой странице освобождённых резервов нет.' : 'На этой странице старых резервов нет.'}</p>
            ) : (
              <ul className="mt-3 divide-y divide-slate-100 rounded-lg border border-slate-200">
                {report.items.map(item => (
                  <li key={item.key} className="space-y-1 p-2 text-sm">
                    <div className="flex flex-wrap justify-between gap-2">
                      <code className="break-all text-xs text-slate-600">{item.key}</code>
                      <span className={item.status === 'eligible' || item.status === 'eligibleReleasedMarker' ? 'font-medium text-emerald-700' : 'text-amber-800'}>
                        {STATUS_LABELS[item.status] ?? 'Требует разбора — резерв сохранён'}
                      </span>
                    </div>
                    {item.relativePath && <p className="break-all text-xs text-slate-600">Файл: {item.relativePath}</p>}
                    <p className="text-xs text-slate-500">
                      {releasedMarkers ? 'Размер исходной загрузки' : 'Ожидалось'}: {item.expectedBytes.toLocaleString('ru-RU')} Б
                      {typeof item.observedBytes === 'number' && ` · найдено: ${item.observedBytes.toLocaleString('ru-RU')} Б`}
                      {' · '}обновлён: {formatDateTimeInTimeZone(item.updatedAt, timeZone)}
                    </p>
                  </li>
                ))}
              </ul>
            )}
            <div className="mt-3 flex gap-2">
              <button type="button" onClick={previousPage} disabled={previousKeys.length === 0 || applying}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:opacity-50">Назад</button>
              <button type="button" onClick={nextPage} disabled={!report.hasMore || !report.nextAfterKey || applying}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:opacity-50">Далее</button>
            </div>
          </>
        )}
      </section>
    </div>
  );
}
