import { Clock3, Loader2, RefreshCw, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { apiService, type ObjectAuditEventDto } from '../../services/api';
import {
  formatAuditChangeSummary,
  formatAuditValue,
  formatObjectAuditText,
  getObjectAuditActorLabel,
  getObjectAuditActionLabel,
  getObjectAuditActionTone,
  getObjectAuditFieldLabel,
  parseAuditChangedFields,
  parseAuditFieldChanges,
} from '../../utils/objectAudit';

interface ObjectAuditHistoryCardProps {
  objectType?: string | null;
  objectId?: number | null;
  compact?: boolean;
}

function formatDateTime(value?: string | null): string {
  if (!value) {
    return 'Неизвестно';
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function getEventDescription(item: ObjectAuditEventDto): string {
  if (item.action?.toLowerCase() === 'updated') {
    const summary = formatAuditChangeSummary(item.objectType, item.metadataJson);
    if (summary) {
      return summary;
    }
  }

  return formatObjectAuditText(item.description) || 'Служебное событие без подробного описания.';
}

function EmptyState({
  title,
  description,
}: {
  title: string;
  description: string;
}) {
  return (
    <div className="mt-5 flex min-h-[240px] flex-col items-center justify-center rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-6 py-10 text-center">
      <div className="rounded-2xl bg-white p-3 text-slate-400 shadow-sm">
        <Clock3 className="h-5 w-5" />
      </div>
      <div className="mt-4 text-base font-semibold text-slate-900">{title}</div>
      <p className="mt-2 max-w-md text-sm leading-6 text-slate-500">{description}</p>
    </div>
  );
}

function HistoryTimeline({ items }: { items: ObjectAuditEventDto[] }) {
  return (
    <div className="space-y-4">
      {items.map((item) => {
        const detailedChanges = parseAuditFieldChanges(item.metadataJson);
        const changedFields = parseAuditChangedFields(item.metadataJson).map((fieldName) =>
          getObjectAuditFieldLabel(item.objectType, fieldName),
        );

        return (
          <article
            key={item.id}
            className="rounded-2xl border border-slate-200 bg-slate-50/70 px-5 py-4"
          >
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span
                    className={`inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-semibold ${getObjectAuditActionTone(
                      item.action,
                    )}`}
                  >
                    {getObjectAuditActionLabel(item.action)}
                  </span>
                  <span className="text-xs text-slate-400">{formatDateTime(item.createdAt)}</span>
                </div>

                <p className="mt-3 break-words text-sm font-semibold text-slate-900">
                  {getObjectAuditActorLabel(item.actorUserName)}
                </p>
                <p className="mt-1 break-words text-sm leading-6 text-slate-600">
                  {getEventDescription(item)}
                </p>

                {detailedChanges.length > 0 ? (
                  <div className="mt-4 space-y-3">
                    {detailedChanges.map((change) => (
                      <div
                        key={`${item.id}-${change.field}`}
                        className="rounded-2xl border border-slate-200 bg-white px-4 py-3"
                      >
                        <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
                          {getObjectAuditFieldLabel(item.objectType, change.field)}
                        </p>
                        <div className="mt-3 grid gap-3 xl:grid-cols-2">
                          <div className="rounded-xl bg-slate-50 px-3 py-3">
                            <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-400">
                              Было
                            </p>
                            <p className="mt-2 break-words text-sm leading-6 text-slate-700">
                              {formatAuditValue(change.oldValue)}
                            </p>
                          </div>
                          <div className="rounded-xl bg-emerald-50/80 px-3 py-3">
                            <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-emerald-500">
                              Стало
                            </p>
                            <p className="mt-2 break-words text-sm leading-6 text-slate-900">
                              {formatAuditValue(change.newValue)}
                            </p>
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                ) : null}

                {changedFields.length > 0 && detailedChanges.length === 0 ? (
                  <div className="mt-4 flex flex-wrap gap-2">
                    {changedFields.map((field) => (
                      <span
                        key={`${item.id}-${field}`}
                        className="inline-flex items-center rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs font-medium text-slate-600"
                      >
                        {field}
                      </span>
                    ))}
                  </div>
                ) : null}
              </div>
            </div>
          </article>
        );
      })}
    </div>
  );
}

export function ObjectAuditHistoryCard({
  objectType,
  objectId,
  compact = false,
}: ObjectAuditHistoryCardProps) {
  const [items, setItems] = useState<ObjectAuditEventDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isExpanded, setIsExpanded] = useState(false);
  const canLoad = Boolean(objectType && objectId && objectId > 0);

  useEffect(() => {
    if (!canLoad) {
      setItems([]);
      setError(null);
      return;
    }

    let cancelled = false;

    const load = async () => {
      setLoading(true);
      setError(null);

      try {
        const result = await apiService.getObjectAuditHistory(objectType!, objectId!);
        if (!cancelled) {
          setItems(result ?? []);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Не удалось загрузить историю.');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    };

    void load();

    return () => {
      cancelled = true;
    };
  }, [canLoad, objectId, objectType]);

  const visibleItems = useMemo(
    () => (compact ? items.slice(0, 6) : items),
    [compact, items],
  );

  const hasMoreItems = compact && items.length > visibleItems.length;

  const refresh = async () => {
    if (!canLoad) {
      return;
    }

    setLoading(true);
    setError(null);

    try {
      const result = await apiService.getObjectAuditHistory(objectType!, objectId!);
      setItems(result ?? []);
    } catch (refreshError) {
      setError(refreshError instanceof Error ? refreshError.message : 'Не удалось обновить историю.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <>
      <section className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex min-w-0 items-start gap-3">
            <div className="rounded-2xl bg-slate-100 p-2.5 text-slate-600">
              <Clock3 className="h-4 w-4" />
            </div>
            <div className="min-w-0">
              <p className="text-base font-semibold text-slate-900">История объекта</p>
              <p className="mt-1 text-sm leading-6 text-slate-500">
                Кто и когда создавал, менял, удалял или восстанавливал запись.
              </p>
            </div>
          </div>

          <div className="flex shrink-0 flex-wrap items-center gap-2">
            {hasMoreItems ? (
              <button
                type="button"
                onClick={() => setIsExpanded(true)}
                className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50"
              >
                Показать все
              </button>
            ) : null}

            <button
              type="button"
              onClick={() => void refresh()}
              disabled={!canLoad || loading}
              className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
              Обновить
            </button>
          </div>
        </div>

        {!canLoad ? (
          <EmptyState
            title="История появится после сохранения"
            description="Когда объект будет создан, все изменения начнут фиксироваться в audit-ленте."
          />
        ) : error ? (
          <div className="mt-5 rounded-2xl border border-rose-200 bg-rose-50 px-4 py-4 text-sm leading-6 text-rose-700">
            {error}
          </div>
        ) : visibleItems.length === 0 && !loading ? (
          <EmptyState
            title="Для этого объекта еще нет записей истории"
            description="Как только по записи будет зафиксировано действие, оно появится здесь."
          />
        ) : (
          <div className="mt-5">
            <HistoryTimeline items={visibleItems} />
          </div>
        )}
      </section>

      {isExpanded && typeof document !== 'undefined'
        ? createPortal(
            <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-900/50 p-4">
              <div className="flex max-h-[88vh] w-full max-w-5xl flex-col overflow-hidden rounded-[28px] bg-white shadow-2xl">
                <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
                  <div>
                    <h3 className="text-lg font-semibold text-slate-900">Полная история объекта</h3>
                    <p className="mt-1 text-sm text-slate-500">
                      Все зафиксированные изменения по этой записи.
                    </p>
                  </div>
                  <button
                    type="button"
                    onClick={() => setIsExpanded(false)}
                    className="rounded-xl border border-slate-200 p-2 text-slate-500 transition hover:bg-slate-50 hover:text-slate-700"
                    aria-label="Закрыть историю"
                  >
                    <X className="h-4 w-4" />
                  </button>
                </div>
                <div className="overflow-y-auto bg-slate-50/70 px-6 py-5">
                  {items.length > 0 ? (
                    <HistoryTimeline items={items} />
                  ) : (
                    <EmptyState
                      title="Записей пока нет"
                      description="Полная история станет доступна, когда по объекту появятся события."
                    />
                  )}
                </div>
              </div>
            </div>,
            document.body,
          )
        : null}
    </>
  );
}
