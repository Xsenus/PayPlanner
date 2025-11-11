import { useMemo, useState } from 'react';
import {
  Building2,
  Factory,
  LayoutGrid,
  List,
  Loader2,
  MapPin,
  Mail,
  Phone,
  Plus,
  Search,
  Users,
  AlertCircle,
  Edit,
  Eye,
  Trash2,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useClients } from '../../hooks/useClients';
import type { Client, LegalEntityDetail, LegalEntityInput, LegalEntitySummary } from '../../types';
import { LegalEntityModal } from './LegalEntityModal';

function matches(entity: LegalEntitySummary, query: string): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  const values = [
    entity.shortName ?? '',
    entity.fullName ?? '',
    entity.inn ?? '',
    entity.kpp ?? '',
    entity.ogrn ?? '',
    entity.director ?? '',
    entity.address ?? '',
  ].map((value) => value.toLowerCase());
  return values.some((value) => value.includes(q));
}

function highlight(text: string | null | undefined, query: string) {
  if (!text) return null;
  const q = query.trim();
  if (!q) return text;
  const regex = new RegExp(`(${q.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})`, 'gi');
  const parts = text.split(regex);
  return parts.map((part, index) =>
    index % 2 === 1 ? (
      <mark key={`${part}-${index}`} className="rounded bg-yellow-200 px-1">
        {part}
      </mark>
    ) : (
      part
    ),
  );
}

export function LegalEntities() {
  const { t } = useTranslation();
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const legalPermissions = permissions.legalEntities;

  const {
    legalEntities,
    loading,
    error,
    createLegalEntity,
    updateLegalEntity,
    deleteLegalEntity,
    getLegalEntity,
    suggestLegalEntities,
  } = useLegalEntities();
  const { clients, loading: clientsLoading } = useClients();

  const [viewMode, setViewMode] = useState<'cards' | 'table'>('cards');
  const [search, setSearch] = useState('');
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<LegalEntityDetail | null>(null);
  const [modalBusy, setModalBusy] = useState(false);
  const [readOnlyMode, setReadOnlyMode] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const filtered = useMemo(
    () => legalEntities.filter((entity) => matches(entity, search)),
    [legalEntities, search],
  );

  const sortedFiltered = useMemo(
    () =>
      [...filtered].sort((a, b) =>
        a.shortName.localeCompare(b.shortName, undefined, { sensitivity: 'base' }),
      ),
    [filtered],
  );

  const handleCreate = () => {
    setEditing(null);
    setReadOnlyMode(false);
    setModalOpen(true);
  };

  const openDetail = async (id: number, allowEditing: boolean) => {
    try {
      setModalBusy(true);
      const detail = await getLegalEntity(id);
      setEditing(detail);
      setReadOnlyMode(!allowEditing);
      setModalOpen(true);
    } catch (err) {
      setActionError(
        err instanceof Error ? err.message : t('legalEntityLoadError') || 'Не удалось загрузить данные юр. лица',
      );
    } finally {
      setModalBusy(false);
    }
  };

  const canCreate = legalPermissions.canCreate;
  const canEdit = legalPermissions.canEdit;
  const canDelete = legalPermissions.canDelete;

  const allowModalEdit = editing ? (!readOnlyMode && canEdit) : canCreate;
  const allowModalDelete = editing ? (!readOnlyMode && canDelete) : false;

  const handleSubmit = async (payload: LegalEntityInput) => {
    if (editing) {
      const updated = await updateLegalEntity(editing.id, payload);
      setEditing(updated);
      return updated;
    }
    const created = await createLegalEntity(payload);
    return created;
  };

  const handleDelete = editing && allowModalDelete
    ? async () => {
        await deleteLegalEntity(editing.id);
        setEditing(null);
        setReadOnlyMode(false);
      }
    : undefined;

  const handleCloseModal = () => {
    setModalOpen(false);
    setEditing(null);
    setReadOnlyMode(false);
    setActionError(null);
  };

  const clientsList: Client[] = useMemo(() => clients, [clients]);

  return (
    <div className="p-6">
      <div className="mb-6 flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-gray-900">
            {t('legalEntitiesTitle') || 'Юридические лица'}
          </h1>
          <p className="text-sm text-gray-500">
            {t('legalEntitiesSubtitle') ||
              'Управляйте карточками юридических лиц и просматривайте связанных клиентов.'}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex rounded-xl border border-gray-200 p-1 shadow-sm">
            <button
              type="button"
              onClick={() => setViewMode('cards')}
              className={`flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium transition ${
                viewMode === 'cards'
                  ? 'bg-blue-600 text-white shadow'
                  : 'text-gray-600 hover:bg-gray-100'
              }`}>
              <LayoutGrid size={16} />
              {t('legalEntityViewCards') || 'Карточки'}
            </button>
            <button
              type="button"
              onClick={() => setViewMode('table')}
              className={`flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium transition ${
                viewMode === 'table'
                  ? 'bg-blue-600 text-white shadow'
                  : 'text-gray-600 hover:bg-gray-100'
              }`}>
              <List size={16} />
              {t('legalEntityViewTable') || 'Таблица'}
            </button>
          </div>
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder={t('legalEntitySearchPlaceholder') || 'Поиск по названию или ИНН'}
              className="w-64 rounded-xl border border-gray-200 px-3 py-2 pl-9 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <button
            type="button"
            onClick={handleCreate}
            disabled={!canCreate}
            className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300">
            <Plus size={16} />
            {t('addLegalEntity') || 'Добавить юр. лицо'}
          </button>
        </div>
      </div>

      {actionError && (
        <div className="mb-4 flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
          <AlertCircle size={18} />
          <span>{actionError}</span>
        </div>
      )}

      {error && (
        <div className="mb-4 flex items-center gap-2 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          <AlertCircle size={18} />
          <span>{error}</span>
        </div>
      )}

      {(loading || clientsLoading || modalBusy) && (
        <div className="mb-4 flex items-center gap-2 rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm text-blue-700">
          <Loader2 className="h-4 w-4 animate-spin" />
          <span>{t('loading') || 'Загрузка данных...'}</span>
        </div>
      )}

      {sortedFiltered.length === 0 && !loading ? (
        <div className="rounded-2xl border border-dashed border-gray-300 bg-white p-10 text-center text-gray-500">
          <div className="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-gray-100">
            <Factory className="h-8 w-8 text-gray-400" />
          </div>
          <h3 className="mt-4 text-lg font-semibold text-gray-900">
            {t('legalEntitiesEmptyTitle') || 'Юридические лица не найдены'}
          </h3>
          <p className="mt-2 text-sm text-gray-500">
            {t('legalEntitiesEmptyDescription') ||
              'Добавьте первое юридическое лицо или измените параметры поиска.'}
          </p>
        </div>
      ) : viewMode === 'cards' ? (
        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          {sortedFiltered.map((entity) => (
            <div
              key={entity.id}
              className="group rounded-2xl border border-gray-200 bg-white p-5 shadow-sm transition hover:border-blue-400 hover:shadow-lg">
              <div className="flex items-start justify-between">
                <div>
                  <h3 className="text-lg font-semibold text-gray-900">
                    {highlight(entity.shortName, search)}
                  </h3>
                  {entity.fullName && (
                    <p className="mt-1 text-sm text-gray-500">
                      {highlight(entity.fullName, search)}
                    </p>
                  )}
                </div>
                <div className="rounded-full bg-blue-50 p-3 text-blue-500">
                  <Building2 size={18} />
                </div>
              </div>

              <div className="mt-4 space-y-2 text-sm text-gray-600">
                <div className="flex flex-wrap gap-x-4 gap-y-1">
                  {entity.inn && <span>ИНН: {highlight(entity.inn, search)}</span>}
                  {entity.kpp && <span>КПП: {highlight(entity.kpp, search)}</span>}
                  {entity.ogrn && <span>ОГРН: {highlight(entity.ogrn, search)}</span>}
                </div>
                {entity.address && (
                  <div className="flex items-center gap-2">
                    <MapPin size={14} className="text-gray-400" />
                    <span>{highlight(entity.address, search)}</span>
                  </div>
                )}
                <div className="flex flex-wrap gap-3 text-xs text-gray-500">
                  {entity.phone && (
                    <span className="flex items-center gap-1">
                      <Phone size={12} /> {entity.phone}
                    </span>
                  )}
                  {entity.email && (
                    <span className="flex items-center gap-1">
                      <Mail size={12} /> {entity.email}
                    </span>
                  )}
                  {entity.director && (
                    <span>{t('legalEntityDirectorLabel') || 'Директор'}: {entity.director}</span>
                  )}
                </div>
              </div>

              <div className="mt-4 rounded-xl bg-gray-50 p-3 text-sm">
                <div className="flex items-center justify-between text-gray-600">
                  <div className="flex items-center gap-2 font-medium text-gray-700">
                    <Users size={16} className="text-blue-500" />
                    {`${t('legalEntityClientsCount') || 'Клиенты'}: ${entity.clientsCount}`}
                  </div>
                  <button
                    type="button"
                    onClick={() => openDetail(entity.id, canEdit)}
                    className="flex items-center gap-1 text-xs text-blue-600 hover:text-blue-700">
                    <Eye size={14} />
                    {t('details') || 'Подробнее'}
                  </button>
                </div>
                <div className="mt-2 flex flex-wrap gap-1 text-xs text-gray-500">
                  {(entity.clients ?? []).slice(0, 4).map((client) => (
                    <span key={client.id} className="rounded-full bg-white px-2 py-0.5 shadow">
                      {client.name}
                    </span>
                  ))}
                  {(entity.clients?.length ?? 0) > 4 && (
                    <span className="text-xs text-gray-400">
                      + {(entity.clients?.length ?? 0) - 4} {(t('andMore') || 'ещё')}
                    </span>
                  )}
                </div>
              </div>

              <div className="mt-4 flex items-center justify-between text-xs text-gray-400">
                <span>
                  {t('createdAt') || 'Создано'}:{' '}
                  {new Date(entity.createdAt).toLocaleDateString()}
                </span>
                {entity.updatedAt && (
                  <span>
                    {t('updatedAt') || 'Обновлено'}:{' '}
                    {new Date(entity.updatedAt).toLocaleDateString()}
                  </span>
                )}
              </div>

              <div className="mt-4 flex items-center gap-2 border-t border-gray-100 pt-3 text-sm">
                <button
                  type="button"
                  onClick={() => openDetail(entity.id, false)}
                  className="inline-flex items-center gap-1 rounded-lg border border-gray-200 px-3 py-1.5 text-gray-600 hover:border-blue-400 hover:text-blue-600">
                  <Eye size={14} />
                  {t('view') || 'Просмотр'}
                </button>
                <button
                  type="button"
                  onClick={() => openDetail(entity.id, true)}
                  disabled={!canEdit}
                  className="inline-flex items-center gap-1 rounded-lg border border-blue-200 bg-blue-50 px-3 py-1.5 text-blue-600 hover:border-blue-400 disabled:cursor-not-allowed disabled:border-gray-200 disabled:bg-gray-100 disabled:text-gray-400">
                  <Edit size={14} />
                  {t('edit') || 'Редактировать'}
                </button>
                <button
                  type="button"
                  onClick={() => openDetail(entity.id, true)}
                  disabled={!canDelete}
                  className="ml-auto inline-flex items-center gap-1 rounded-lg border border-red-200 bg-red-50 px-3 py-1.5 text-red-600 hover:border-red-300 disabled:cursor-not-allowed disabled:border-gray-200 disabled:bg-gray-100 disabled:text-gray-300">
                  <Trash2 size={14} />
                  {t('delete') || 'Удалить'}
                </button>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <div className="overflow-x-auto rounded-2xl border border-gray-200 bg-white shadow-sm">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-6 py-3 text-left text-xs font-semibold uppercase tracking-wider text-gray-600">
                  {t('legalEntityShortName') || 'Название'}
                </th>
                <th className="px-6 py-3 text-left text-xs font-semibold uppercase tracking-wider text-gray-600">
                  {t('legalEntityRequisites') || 'Реквизиты'}
                </th>
                <th className="px-6 py-3 text-left text-xs font-semibold uppercase tracking-wider text-gray-600">
                  {t('contacts') || 'Контакты'}
                </th>
                <th className="px-6 py-3 text-left text-xs font-semibold uppercase tracking-wider text-gray-600">
                  {t('legalEntityClientsColumn') || 'Клиенты'}
                </th>
                <th className="px-6 py-3 text-right text-xs font-semibold uppercase tracking-wider text-gray-600">
                  {t('legalEntityActions') || 'Действия'}
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 text-sm text-gray-700">
              {sortedFiltered.map((entity) => (
                <tr key={entity.id} className="hover:bg-gray-50">
                  <td className="px-6 py-4">
                    <div className="font-medium text-gray-900">
                      {highlight(entity.shortName, search)}
                    </div>
                    {entity.fullName && (
                      <div className="text-xs text-gray-500">{highlight(entity.fullName, search)}</div>
                    )}
                  </td>
                  <td className="px-6 py-4 text-xs text-gray-600">
                    <div className="flex flex-col gap-1">
                      {entity.inn && <span>ИНН: {highlight(entity.inn, search)}</span>}
                      {entity.kpp && <span>КПП: {highlight(entity.kpp, search)}</span>}
                      {entity.ogrn && <span>ОГРН: {highlight(entity.ogrn, search)}</span>}
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex flex-col gap-1 text-xs text-gray-600">
                      {entity.address && (
                        <span className="flex items-center gap-1">
                          <MapPin size={12} /> {highlight(entity.address, search)}
                        </span>
                      )}
                      {entity.phone && (
                        <span className="flex items-center gap-1">
                          <Phone size={12} /> {entity.phone}
                        </span>
                      )}
                      {entity.email && (
                        <span className="flex items-center gap-1">
                          <Mail size={12} /> {entity.email}
                        </span>
                      )}
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex flex-wrap gap-1 text-xs text-gray-600">
                      {(entity.clients ?? []).slice(0, 5).map((client) => (
                        <span key={client.id} className="rounded-full bg-gray-100 px-2 py-0.5">
                          {client.name}
                        </span>
                      ))}
                      {(entity.clients?.length ?? 0) > 5 && (
                        <span className="text-xs text-gray-400">
                          + {(entity.clients?.length ?? 0) - 5} {(t('andMore') || 'ещё')}
                        </span>
                      )}
                    </div>
                  </td>
                  <td className="px-6 py-4 text-right">
                    <div className="flex justify-end gap-2">
                      <button
                        type="button"
                        onClick={() => openDetail(entity.id, false)}
                        className="rounded-lg border border-gray-200 px-3 py-1.5 text-xs text-gray-600 hover:border-blue-400 hover:text-blue-600">
                        <Eye size={14} />
                      </button>
                      <button
                        type="button"
                        onClick={() => openDetail(entity.id, true)}
                        disabled={!canEdit}
                        className="rounded-lg border border-blue-200 px-3 py-1.5 text-xs text-blue-600 hover:border-blue-400 disabled:cursor-not-allowed disabled:border-gray-200 disabled:text-gray-300">
                        <Edit size={14} />
                      </button>
                      <button
                        type="button"
                        onClick={() => openDetail(entity.id, true)}
                        disabled={!canDelete}
                        className="rounded-lg border border-red-200 px-3 py-1.5 text-xs text-red-600 hover:border-red-300 disabled:cursor-not-allowed disabled:border-gray-200 disabled:text-gray-300">
                        <Trash2 size={14} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <LegalEntityModal
        isOpen={modalOpen}
        onClose={handleCloseModal}
        onSubmit={handleSubmit}
        onDelete={handleDelete}
        initial={editing}
        clients={clientsList}
        suggest={suggestLegalEntities}
        allowEdit={allowModalEdit}
        allowDelete={allowModalDelete}
      />
    </div>
  );
}
