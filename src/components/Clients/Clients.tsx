import { useEffect, useState } from 'react';
import {
  Users,
  Plus,
  Building,
  Mail,
  Phone,
  Eye,
  Edit,
  FolderKanban,
  PlusCircle,
} from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useTranslation } from '../../hooks/useTranslation';
import { ClientModal } from './ClientModal';
import { ClientDetail } from './ClientDetail';
import type { Client, ClientCase } from '../../types';
import { CaseModal } from './CaseModal';
import { apiService } from '../../services/api';

function statusOrder(statusRaw?: string): number {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 0;
  if (s.includes('onhold') || s.includes('hold')) return 1;
  if (s.includes('closed')) return 2;
  return 99;
}

function caseStatusLabel(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'Открыто';
  if (s.includes('onhold') || s.includes('hold')) return 'Приостановлено';
  if (s.includes('closed')) return 'Закрыто';
  if (s.includes('в работе') || s.includes('актив')) return 'Открыто';
  if (s.includes('ожид')) return 'Приостановлено';
  if (s.includes('закры')) return 'Закрыто';
  return statusRaw ?? '';
}

function statusClasses(statusRaw?: string) {
  const s = (statusRaw ?? '').toLowerCase();
  if (s.includes('open')) return 'bg-green-100 text-green-700 border border-green-200';
  if (s.includes('onhold') || s.includes('hold'))
    return 'bg-amber-100 text-amber-800 border border-amber-200';
  if (s.includes('closed')) return 'bg-red-100 text-red-700 border border-red-200';
  if (s.includes('overdue') || s.includes('проср'))
    return 'bg-rose-100 text-rose-700 border border-rose-200';
  if (s.includes('pending') || s.includes('ожид'))
    return 'bg-amber-100 text-amber-800 border border-amber-200';
  if (s.includes('completed') || s.includes('выполн'))
    return 'bg-emerald-100 text-emerald-700 border border-emerald-200';
  if (s.includes('inprogress') || s.includes('в работе') || s.includes('актив'))
    return 'bg-blue-100 text-blue-700 border border-blue-200';
  if (s.includes('cancel')) return 'bg-gray-200 text-gray-700 border border-gray-300';
  return 'bg-slate-100 text-slate-700 border border-slate-200';
}

function formatDate(d?: string) {
  if (!d) return '';
  try {
    return new Date(d).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
    });
  } catch {
    return d;
  }
}

export function Clients() {
  const { clients, loading, createClient, updateClient, deleteClient, setClients, refresh } =
    useClients();
  const { t } = useTranslation();

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [editingClient, setEditingClient] = useState<Client | null>(null);

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [caseClientId, setCaseClientId] = useState<number | null>(null);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('edit');

  const [expandedCards, setExpandedCards] = useState<Set<number>>(new Set());

  useEffect(() => {
    const handler = (e: Event) => {
      const { clientId, caseId } =
        (e as CustomEvent<{ clientId: number; caseId?: number }>).detail || {};
      if (typeof clientId === 'number') {
        setSelectedClientId(clientId);
        if (caseId !== undefined) {
          setInitialCaseId(caseId);
        }
      }
    };

    window.addEventListener('open-client-detail', handler as EventListener);
    return () => window.removeEventListener('open-client-detail', handler as EventListener);
  }, []);

  const toggleCases = (clientId: number) =>
    setExpandedCards((prev) => {
      const next = new Set(prev);
      if (next.has(clientId)) {
        next.delete(clientId);
      } else {
        next.add(clientId);
      }
      return next;
    });

  if (selectedClientId) {
    return (
      <ClientDetail
        clientId={selectedClientId}
        onBack={() => setSelectedClientId(null)}
        initialCaseId={initialCaseId}
      />
    );
  }

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-500">{t('loading')}</div>
      </div>
    );
  }

  const handleAddClient = () => {
    setEditingClient(null);
    setIsModalOpen(true);
  };

  const handleEditClient = (client: Client) => {
    setEditingClient(client);
    setIsModalOpen(true);
  };

  const handleCloseModal = () => {
    setIsModalOpen(false);
    setEditingClient(null);
  };

  const handleSubmitClient = async (clientData: Omit<Client, 'id' | 'createdAt'>) => {
    if (editingClient) {
      await updateClient(editingClient.id, clientData);
    } else {
      await createClient(clientData);
    }
  };

  const handleDeleteClient = async (id: number) => {
    setEditingClient(null);
    setIsModalOpen(false);

    setClients((prev: Client[]) => prev.filter((c: Client) => c.id !== id));

    try {
      await deleteClient(id);
    } catch (err) {
      console.error(err);
      await refresh();
    }
  };

  const openAddCase = (clientId: number) => {
    setCaseClientId(clientId);
    setEditingCase(null);
    setCaseMode('create');
    setCaseModalOpen(true);
  };

  const openEditCase = (clientId: number, clientCase: ClientCase) => {
    setCaseClientId(clientId);
    setEditingCase(clientCase);
    setCaseMode('edit');
    setCaseModalOpen(true);
  };

  const saveCase = async (patch: Partial<ClientCase>): Promise<void> => {
    if (!caseClientId) return;

    type Status = ClientCase['status'];
    const DEFAULT_STATUS: Status = 'Open';

    const base: Omit<ClientCase, 'id' | 'createdAt' | 'payments'> = {
      clientId: caseClientId,
      title: (patch.title ?? editingCase?.title ?? '').trim(),
      description: (patch.description ?? editingCase?.description ?? '').trim(),
      status: (patch.status ?? editingCase?.status ?? DEFAULT_STATUS) as Status,
    };

    if (caseMode === 'edit' && editingCase) {
      const updated = await apiService.updateCase(editingCase.id, base);
      setClients((prev) =>
        prev.map((c) =>
          c.id === caseClientId
            ? { ...c, cases: (c.cases ?? []).map((k) => (k.id === updated.id ? updated : k)) }
            : c,
        ),
      );
    } else {
      const created = await apiService.createCase(base);
      setClients((prev) =>
        prev.map((c) =>
          c.id === caseClientId ? { ...c, cases: [created, ...(c.cases ?? [])] } : c,
        ),
      );
    }

    setCaseModalOpen(false);
    setEditingCase(null);
  };

  const deleteCase = async (): Promise<void> => {
    if (!editingCase || !caseClientId) return;
    await apiService.deleteCase(editingCase.id);

    setClients((prev) =>
      prev.map((c) =>
        c.id === caseClientId
          ? { ...c, cases: (c.cases ?? []).filter((k) => k.id !== editingCase.id) }
          : c,
      ),
    );

    setCaseModalOpen(false);
    setEditingCase(null);
  };

  const sortedClients = [...clients].sort((a, b) => Number(b.isActive) - Number(a.isActive));

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-7xl mx-auto p-6">
        <div className="flex items-center justify-between mb-8">
          <div className="flex items-center gap-3">
            <Users size={32} className="text-blue-600" />
            <div>
              <h1 className="text-3xl font-bold text-gray-900">{t('clients')}</h1>
              <p className="text-gray-600">{t('manageClients')}</p>
            </div>
          </div>
          <button
            type="button"
            onClick={handleAddClient}
            className="flex items-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors font-medium">
            <Plus size={20} />
            {t('addClient')}
          </button>
        </div>

        {sortedClients.length === 0 ? (
          <div className="text-center py-12">
            <Users size={48} className="mx-auto mb-4 text-gray-300" />
            <h3 className="text-lg font-medium text-gray-900 mb-2">{t('noClientsYet')}</h3>
            <p className="text-gray-500 mb-4">{t('getStartedByAdding')}</p>
            <button
              type="button"
              onClick={handleAddClient}
              className="bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors">
              {t('addClient')}
            </button>
          </div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            {sortedClients.map((client) => {
              const cases = (client.cases ?? []) as ClientCase[];
              const totalCases = cases.length;

              const counts = cases.reduce<Record<string, number>>((acc, c) => {
                const key = (c.status ?? 'unknown').toLowerCase();
                acc[key] = (acc[key] ?? 0) + 1;
                return acc;
              }, {});

              // Сортировка: Open → OnHold → Closed, затем по дате (новые сверху)
              const sortedByStatusThenDate = [...cases].sort((a, b) => {
                const ra = statusOrder(a.status);
                const rb = statusOrder(b.status);
                if (ra !== rb) return ra - rb;
                return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
              });

              const isExpanded = expandedCards.has(client.id);

              // В свернутом виде показываем только НЕ закрытые.
              // Если все дела закрытые — покажем первые по общему списку.
              const nonClosed = sortedByStatusThenDate.filter((k) => statusOrder(k.status) !== 2);
              const visibleCases = isExpanded
                ? sortedByStatusThenDate
                : nonClosed.length
                ? nonClosed.slice(0, 3)
                : sortedByStatusThenDate.slice(0, 3);

              const hiddenCount = Math.max(0, sortedByStatusThenDate.length - visibleCases.length);

              return (
                <div
                  key={client.id}
                  className={`rounded-xl p-6 shadow-sm border border-gray-100 hover:shadow-md transition-shadow
                  ${client.isActive ? 'bg-white' : 'bg-gray-100 opacity-70'}`}>
                  <div className="flex items-start justify-between mb-4">
                    <div className="flex items-center gap-3">
                      <div className="w-12 h-12 bg-blue-100 rounded-lg flex items-center justify-center">
                        <Users size={24} className="text-blue-600" />
                      </div>
                      <div>
                        <h3 className="font-semibold text-gray-900">{client.name}</h3>
                        <p className="text-sm text-gray-500">{client.company}</p>
                      </div>
                    </div>
                    <div className="flex gap-1">
                      {/* Новая кнопка "Добавить дело" */}
                      <button
                        type="button"
                        onClick={() => openAddCase(client.id)}
                        className="p-2 text-gray-400 hover:text-emerald-600 hover:bg-emerald-50 rounded-lg transition-colors"
                        title="Добавить дело">
                        <PlusCircle size={16} />
                      </button>

                      <button
                        type="button"
                        onClick={() => handleEditClient(client)}
                        className="p-2 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded-lg transition-colors"
                        title={t('edit')}>
                        <Edit size={16} />
                      </button>
                      <button
                        type="button"
                        onClick={() => setSelectedClientId(client.id)}
                        className="p-2 text-gray-400 hover:text-gray-600 hover:bg-gray-100 rounded-lg transition-colors"
                        title={t('viewDetails')}>
                        <Eye size={16} />
                      </button>
                    </div>
                  </div>

                  {/* Контакты */}
                  <div className="space-y-2 mb-5">
                    {client.email && (
                      <div className="flex items-center gap-2 text-sm text-gray-600">
                        <Mail size={14} />
                        <span className="truncate">{client.email}</span>
                      </div>
                    )}
                    {client.phone && (
                      <div className="flex items-center gap-2 text-sm text-gray-600">
                        <Phone size={14} />
                        <span>{client.phone}</span>
                      </div>
                    )}
                    {client.address && (
                      <div className="flex items-center gap-2 text-sm text-gray-600">
                        <Building size={14} />
                        <span className="truncate">{client.address}</span>
                      </div>
                    )}
                  </div>

                  {/* Дела клиента */}
                  <div className="mt-2">
                    <div className="flex items-center gap-2 mb-2">
                      <FolderKanban size={18} className="text-slate-600" />
                      <span className="font-medium text-slate-800">{t('cases') || 'Дела'}</span>
                      <span className="ml-auto text-xs text-slate-500">
                        {t('total') || 'Всего'}: {totalCases}
                      </span>
                    </div>

                    {totalCases > 0 ? (
                      <div className="flex flex-wrap gap-2 mb-3">
                        {Object.entries(counts)
                          .sort((a, b) => b[1] - a[1])
                          .slice(0, 4)
                          .map(([status, count]) => {
                            const label = caseStatusLabel(status);
                            return (
                              <span
                                key={status}
                                className={`text-xs px-2.5 py-1 rounded-full ${statusClasses(
                                  status,
                                )}`}
                                title={label}>
                                {label} · {count}
                              </span>
                            );
                          })}
                      </div>
                    ) : (
                      <div className="text-xs text-slate-500 mb-3">
                        {t('noCases') || 'Дел пока нет'}
                      </div>
                    )}

                    {/* Список дел */}
                    {visibleCases.length > 0 && (
                      <ul className="space-y-2">
                        {visibleCases.map((c) => (
                          <li
                            key={c.id}
                            className="flex items-start justify-between rounded-lg border border-slate-100 bg-slate-50 px-3 py-2">
                            <div className="min-w-0">
                              <div className="text-sm font-medium text-slate-800 truncate">
                                {c.title}
                              </div>
                              {c.description && (
                                <div className="text-xs text-slate-500 truncate">
                                  {c.description}
                                </div>
                              )}
                              <div className="text-[11px] text-slate-400 mt-0.5">
                                {t('created') || 'Создано'}: {formatDate(c.createdAt)}
                              </div>
                            </div>
                            <div className="flex items-center gap-1">
                              <span
                                className={`shrink-0 text-[11px] px-2 py-1 rounded-full ${statusClasses(
                                  c.status,
                                )}`}>
                                {caseStatusLabel(c.status)}
                              </span>
                              <button
                                type="button"
                                onClick={() => openEditCase(client.id, c)}
                                className="ml-1 p-1.5 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded transition-colors"
                                title="Редактировать дело">
                                <Edit size={14} />
                              </button>
                            </div>
                          </li>
                        ))}
                      </ul>
                    )}

                    {/* Кнопки показать ещё / свернуть */}
                    {hiddenCount > 0 && !isExpanded && (
                      <button
                        type="button"
                        onClick={() => toggleCases(client.id)}
                        className="mt-2 text-xs text-blue-600 hover:text-blue-700">
                        Показать ещё {hiddenCount}
                      </button>
                    )}
                    {isExpanded && sortedByStatusThenDate.length > 3 && (
                      <button
                        type="button"
                        onClick={() => toggleCases(client.id)}
                        className="mt-2 text-xs text-blue-600 hover:text-blue-700">
                        Свернуть
                      </button>
                    )}
                  </div>

                  {/* Примечания */}
                  {client.notes && (
                    <p className="mt-4 text-sm text-gray-500 line-clamp-2">{client.notes}</p>
                  )}

                  <div className="mt-4 pt-4 border-t border-gray-100">
                    <button
                      type="button"
                      onClick={() => setSelectedClientId(client.id)}
                      className="w-full text-center text-blue-600 hover:text-blue-700 font-medium text-sm transition-colors">
                      {t('viewDetails')}
                    </button>
                  </div>
                </div>
              );
            })}
          </div>
        )}

        <ClientModal
          isOpen={isModalOpen}
          onClose={handleCloseModal}
          onSubmit={handleSubmitClient}
          onDelete={handleDeleteClient}
          client={editingClient ?? undefined}
        />

        {caseModalOpen && (
          <CaseModal
            isOpen={caseModalOpen}
            onClose={() => setCaseModalOpen(false)}
            caseData={
              caseMode === 'edit' && editingCase
                ? editingCase
                : ({
                    id: 0,
                    clientId: caseClientId ?? 0,
                    title: '',
                    description: '',
                    status: 'Open',
                    createdAt: new Date().toISOString(),
                  } as ClientCase)
            }
            onSave={saveCase}
            onDelete={caseMode === 'edit' ? deleteCase : undefined}
          />
        )}
      </div>
    </div>
  );
}
