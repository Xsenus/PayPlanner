import { Loader2, RefreshCw, Search, ShieldAlert } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService, type DeletedObjectAuditItemDto } from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { getObjectAuditActorLabel } from '../../utils/objectAudit';

type DeletedObjectType =
  | 'client'
  | 'client-case'
  | 'payment'
  | 'act'
  | 'contract'
  | 'legal-entity'
  | 'contact';

type DeletedObjectsSettingsProps = {
  canRestore?: boolean;
};

const OBJECT_TYPES: Array<{ key: DeletedObjectType; label: string; hint: string }> = [
  { key: 'client', label: 'Клиенты', hint: 'Карточки клиентов и их контактные данные.' },
  { key: 'client-case', label: 'Дела', hint: 'Клиентские дела и рабочие карточки.' },
  { key: 'payment', label: 'Счета и платежи', hint: 'Счета, платежи и агрегаты рассрочки.' },
  { key: 'act', label: 'Акты', hint: 'Акты и документы закрытия.' },
  { key: 'contract', label: 'Договоры', hint: 'Договоры с клиентами и контрагентами.' },
  { key: 'legal-entity', label: 'Юрлица', hint: 'Карточки юридических лиц.' },
  { key: 'contact', label: 'Сторонние контакты', hint: 'Контакты вне клиентского справочника.' },
];

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

function formatAuditUserName(value?: string | null) {
  return value?.trim() ? getObjectAuditActorLabel(value) : '—';
}

function getRestoreDescription(item: DeletedObjectAuditItemDto) {
  const parts = [
    item.deletedByUserName ? `Удалил: ${formatAuditUserName(item.deletedByUserName)}` : null,
    item.deletedAt ? `Когда: ${formatDateTime(item.deletedAt)}` : null,
  ].filter(Boolean);

  return parts.length > 0 ? parts.join(' • ') : 'Удаление было зафиксировано в системе.';
}

export function ObjectAuditDeletedObjectsSettings({
  canRestore = true,
}: DeletedObjectsSettingsProps) {
  const toast = useToast();
  const { confirm } = useDialog();
  const [activeType, setActiveType] = useState<DeletedObjectType>('client');
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [items, setItems] = useState<DeletedObjectAuditItemDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [restoringKey, setRestoringKey] = useState<string | null>(null);

  useEffect(() => {
    const handle = window.setTimeout(() => {
      setSearch(searchInput.trim());
    }, 350);

    return () => window.clearTimeout(handle);
  }, [searchInput]);

  const loadItems = async (type: DeletedObjectType, term: string, silent = false) => {
    if (!silent) {
      setLoading(true);
    }
    setError(null);

    try {
      const result = await apiService.getDeletedObjectAuditItems(type, term);
      setItems(result ?? []);
    } catch (loadError) {
      setError(
        extractDisplayErrorMessage(loadError, 'Не удалось загрузить список удалённых объектов.'),
      );
    } finally {
      if (!silent) {
        setLoading(false);
      }
    }
  };

  useEffect(() => {
    void loadItems(activeType, search);
  }, [activeType, search]);

  const activeTypeMeta = useMemo(
    () => OBJECT_TYPES.find((item) => item.key === activeType) ?? OBJECT_TYPES[0],
    [activeType],
  );

  const handleRefresh = async () => {
    await loadItems(activeType, search, true);
    toast.success('Список удалённых объектов обновлён.', 'Аудит объектов');
  };

  const handleRestore = async (item: DeletedObjectAuditItemDto) => {
    if (!canRestore) {
      toast.error('У вас нет прав на восстановление удалённых объектов.', 'Аудит объектов');
      return;
    }

    const approved = await confirm({
      title: 'Восстановить объект?',
      description: `Объект "${item.displayName}" вернётся в рабочие списки и снова станет доступен пользователям.`,
      confirmLabel: 'Восстановить',
      cancelLabel: 'Отмена',
      variant: 'primary',
    });

    if (!approved) {
      return;
    }

    const key = `${item.objectType}:${item.objectId}`;
    setRestoringKey(key);
    setError(null);

    try {
      await apiService.restoreDeletedObject(item.objectType, item.objectId);
      setItems((current) =>
        current.filter(
          (entry) => !(entry.objectType === item.objectType && entry.objectId === item.objectId),
        ),
      );
      toast.success(`Объект "${item.displayName}" восстановлен.`, 'Аудит объектов');
    } catch (restoreError) {
      const message = extractDisplayErrorMessage(
        restoreError,
        'Не удалось восстановить удалённый объект.',
      );
      setError(message);
      toast.error(message, 'Аудит объектов');
    } finally {
      setRestoringKey(null);
    }
  };

  return (
    <div className="space-y-6">
      <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.28em] text-slate-400">
              Корзина аудита
            </div>
            <h4 className="mt-3 text-lg font-semibold text-gray-900">Удалённые объекты</h4>
            <p className="mt-1 max-w-3xl text-sm text-gray-600">
              Здесь можно просмотреть мягко удалённые записи и при наличии прав вернуть их
              обратно без ручного поиска по базе данных.
            </p>
          </div>

          <button
            type="button"
            onClick={() => void handleRefresh()}
            className="inline-flex items-center gap-2 rounded-2xl border border-slate-300 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-60"
            disabled={loading}
          >
            {loading ? <Loader2 size={16} className="animate-spin" /> : <RefreshCw size={16} />}
            Обновить список
          </button>
        </div>

        <div className="mt-5 grid gap-4 lg:grid-cols-[minmax(0,1.3fr),minmax(280px,1fr)]">
          <label className="rounded-2xl border border-gray-200 p-4 text-sm text-gray-700">
            <span className="block font-medium text-gray-900">Поиск</span>
            <span className="mt-1 block text-gray-600">
              Имя, номер, телефон, email или другой заметный фрагмент.
            </span>
            <div className="mt-3 flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3">
              <Search className="h-4 w-4 text-slate-400" />
              <input
                value={searchInput}
                onChange={(event) => setSearchInput(event.target.value)}
                placeholder="Начните вводить для фильтрации удалённых объектов"
                className="w-full bg-transparent text-sm text-slate-700 outline-none placeholder:text-slate-400"
              />
            </div>
          </label>

          <div className="rounded-2xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
            <div className="flex items-start gap-3">
              <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0" />
              <div>
                <div className="font-semibold">
                  {canRestore
                    ? 'Восстановление доступно для вашей роли'
                    : 'Восстановление вам недоступно'}
                </div>
                <div className="mt-1 leading-6">
                  {canRestore
                    ? 'Для счетов и рассрочек система поднимает весь связанный агрегат, чтобы после восстановления не получить половину документа.'
                    : 'Вы можете просматривать удалённые объекты, но вернуть их обратно сможет только администратор или пользователь с правом восстановления.'}
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="grid gap-6 xl:grid-cols-[280px,minmax(0,1fr)]">
        <aside className="space-y-4">
          <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
            <div className="text-xs font-semibold uppercase tracking-[0.28em] text-slate-400">
              Тип объекта
            </div>
            <div className="mt-4 space-y-3">
              {OBJECT_TYPES.map((item) => {
                const isActive = item.key === activeType;
                return (
                  <button
                    key={item.key}
                    type="button"
                    onClick={() => setActiveType(item.key)}
                    className={`w-full rounded-2xl border px-4 py-3 text-left transition ${
                      isActive
                        ? 'border-blue-200 bg-blue-50 text-blue-700'
                        : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
                    }`}
                  >
                    <div className="font-semibold">{item.label}</div>
                    <div
                      className={`mt-1 text-xs leading-5 ${
                        isActive ? 'text-blue-600' : 'text-slate-500'
                      }`}
                    >
                      {item.hint}
                    </div>
                  </button>
                );
              })}
            </div>
          </div>
        </aside>

        <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <h4 className="text-lg font-semibold text-gray-900">{activeTypeMeta.label}</h4>
              <p className="mt-1 text-sm text-gray-600">{activeTypeMeta.hint}</p>
            </div>
            <span className="rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-600">
              Найдено: {items.length}
            </span>
          </div>

          {error ? (
            <div className="mt-5 rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {error}
            </div>
          ) : null}

          {loading ? (
            <div className="mt-6 flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-5 text-sm text-slate-600">
              <Loader2 className="h-4 w-4 animate-spin" />
              Загружаем удалённые объекты...
            </div>
          ) : items.length === 0 ? (
            <div className="mt-6 rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-10 text-center text-sm text-slate-500">
              По текущим фильтрам удалённых объектов не найдено.
            </div>
          ) : (
            <div className="mt-6 space-y-4">
              {items.map((item) => {
                const key = `${item.objectType}:${item.objectId}`;
                const restoring = restoringKey === key;

                return (
                  <div key={key} className="rounded-2xl border border-slate-200 bg-slate-50/60 p-4">
                    <div className="flex flex-wrap items-start justify-between gap-4">
                      <div className="min-w-0 flex-1">
                        <div className="text-base font-semibold text-slate-900">{item.displayName}</div>
                        {item.subtitle ? (
                          <div className="mt-1 text-sm text-slate-500">{item.subtitle}</div>
                        ) : null}
                        <div className="mt-3 grid gap-2 text-xs text-slate-500 md:grid-cols-2">
                          <div>Создан: {formatDateTime(item.createdAt)}</div>
                          <div>Создал: {formatAuditUserName(item.createdByUserName)}</div>
                          <div>Обновлён: {formatDateTime(item.updatedAt)}</div>
                          <div>Изменил: {formatAuditUserName(item.updatedByUserName)}</div>
                        </div>
                        <div className="mt-3 text-xs text-slate-500">{getRestoreDescription(item)}</div>
                      </div>

                      <div className="shrink-0">
                        <button
                          type="button"
                          onClick={() => void handleRestore(item)}
                          disabled={!canRestore || restoring}
                          className="inline-flex min-w-[148px] items-center justify-center gap-2 rounded-2xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-sm font-semibold text-blue-700 transition hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {restoring ? <Loader2 size={16} className="animate-spin" /> : null}
                          {restoring ? 'Восстанавливаем...' : 'Восстановить'}
                        </button>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
