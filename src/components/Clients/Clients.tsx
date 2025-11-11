import { ReactNode, useEffect, useMemo, useState } from 'react';
import {
  Users,
  Plus,
  Building,
  Building2,
  Mail,
  Phone,
  Eye,
  Edit,
  FolderKanban,
  PlusCircle,
  Search,
  X,
  LayoutGrid,
  List,
  Ban,
} from 'lucide-react';
import { useClients } from '../../hooks/useClients';
import { useTranslation } from '../../hooks/useTranslation';
import { ClientModal } from './ClientModal';
import { ClientDetail } from './ClientDetail';
import type { Client, ClientCase, ClientInput } from '../../types';
import { CaseModal } from './CaseModal';
import { apiService } from '../../services/api';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useClientStatuses } from '../../hooks/useClientStatuses';
import { ClientStatusBadge } from './ClientStatusBadge';

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

function matchesClient(c: Client, q: string): boolean {
  const ql = q.trim().toLowerCase();
  if (!ql) return true;

  const qDigits = q.replace(/\D/g, '');
  const name = (c.name ?? '').toLowerCase();
  const company = (c.company ?? '').toLowerCase();
  const email = (c.email ?? '').toLowerCase();
  const phone = (c.phone ?? '').toLowerCase();
  const phoneDigits = (c.phone ?? '').replace(/\D/g, '');
  const legal = c.legalEntity;
  const legalShort = (legal?.shortName ?? '').toLowerCase();
  const legalFull = (legal?.fullName ?? '').toLowerCase();
  const legalInn = (legal?.inn ?? '').toLowerCase();
  const legalOgrn = (legal?.ogrn ?? '').toLowerCase();
  const legalKpp = (legal?.kpp ?? '').toLowerCase();
  const statusName = (c.clientStatus?.name ?? '').toLowerCase();
  const legalDigits = [legal?.inn ?? '', legal?.ogrn ?? '', legal?.kpp ?? '']
    .map((value) => value.replace(/\D/g, ''))
    .join(' ');

  return (
    name.includes(ql) ||
    company.includes(ql) ||
    email.includes(ql) ||
    phone.includes(ql) ||
    statusName.includes(ql) ||
    legalShort.includes(ql) ||
    legalFull.includes(ql) ||
    legalInn.includes(ql) ||
    legalOgrn.includes(ql) ||
    legalKpp.includes(ql) ||
    (!!qDigits && (phoneDigits.includes(qDigits) || legalDigits.includes(qDigits)))
  );
}

function escapeRegExp(s: string) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function highlight(text: string, q: string): ReactNode {
  const query = q.trim();
  if (!query) return text;
  const re = new RegExp(`(${escapeRegExp(query)})`, 'gi');
  const parts = text.split(re);
  return parts.map((part, i) =>
    i % 2 === 1 ? (
      <mark key={i} className="bg-yellow-200 rounded px-0.5">
        {part}
      </mark>
    ) : (
      part
    ),
  );
}

function highlightPhone(phone: string, q: string): ReactNode {
  const qDigits = q.replace(/\D/g, '');
  if (!qDigits) return highlight(phone, q);

  const digitPositions: number[] = [];
  const digitsOnlyChars: string[] = [];
  for (let i = 0; i < phone.length; i++) {
    const ch = phone[i];
    if (/\d/.test(ch)) {
      digitPositions.push(i);
      digitsOnlyChars.push(ch);
    }
  }
  const digitsOnly = digitsOnlyChars.join('');
  const pos = digitsOnly.indexOf(qDigits);
  if (pos === -1) return highlight(phone, q);

  const toHi = new Set(digitPositions.slice(pos, pos + qDigits.length));
  const out: ReactNode[] = [];
  let buf = '';
  let inMark = false;
  let key = 0;

  for (let i = 0; i < phone.length; i++) {
    const should = toHi.has(i);
    if (should) {
      if (!inMark) {
        if (buf) out.push(buf);
        buf = phone[i];
        inMark = true;
      } else {
        buf += phone[i];
      }
    } else {
      if (inMark) {
        out.push(
          <mark key={`m${key++}`} className="bg-yellow-200 rounded px-0.5">
            {buf}
          </mark>,
        );
        buf = '';
        inMark = false;
      }
      buf += phone[i];
    }
  }
  if (buf) {
    if (inMark) {
      out.push(
        <mark key={`m${key++}`} className="bg-yellow-200 rounded px-0.5">
          {buf}
        </mark>,
      );
    } else {
      out.push(buf);
    }
  }
  return out;
}

export function Clients() {
  const { clients, loading, createClient, updateClient, deleteClient, setClients, refresh } =
    useClients();
  const { legalEntities, loading: legalEntitiesLoading } = useLegalEntities();
  const { statuses: clientStatuses } = useClientStatuses();
  const { t } = useTranslation();
  const { user } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const clientPermissions = permissions.clients;
  const canViewClients = clientPermissions.canView;
  const canCreateClients = clientPermissions.canCreate;
  const canEditClients = clientPermissions.canEdit;
  const canDeleteClients = clientPermissions.canDelete;

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedClientId, setSelectedClientId] = useState<number | null>(null);
  const [initialCaseId, setInitialCaseId] = useState<number | 'all'>('all');
  const [editingClient, setEditingClient] = useState<Client | null>(null);

  const [caseModalOpen, setCaseModalOpen] = useState(false);
  const [caseClientId, setCaseClientId] = useState<number | null>(null);
  const [editingCase, setEditingCase] = useState<ClientCase | null>(null);
  const [caseMode, setCaseMode] = useState<'create' | 'edit'>('edit');

  const [expandedCards, setExpandedCards] = useState<Set<number>>(new Set());

  const [query, setQuery] = useState('');
  const [viewMode, setViewMode] = useState<'cards' | 'table'>(() => {
    try {
      return (localStorage.getItem('pp.clients_view') as 'cards' | 'table') || 'cards';
    } catch {
      return 'cards';
    }
  });
  const filteredClients = useMemo(() => {
    if (!query.trim()) return clients;
    return clients.filter((c) => matchesClient(c, query));
  }, [clients, query]);

  const sortedClients = useMemo(
    () => [...filteredClients].sort((a, b) => Number(b.isActive) - Number(a.isActive)),
    [filteredClients],
  );

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

  const showPermissionWarning = (message: string) => {
    window.alert(message || 'Недостаточно прав для выполнения действия.');
  };

  if (!canViewClients) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center p-6">
        <div className="w-full max-w-xl rounded-2xl border border-red-200 bg-white px-6 py-10 text-center shadow-sm">
          <Ban className="mx-auto h-12 w-12 text-red-500" />
          <h1 className="mt-4 text-2xl font-semibold text-red-700">Доступ к клиентам ограничен</h1>
          <p className="mt-2 text-sm text-red-600">
            Обратитесь к администратору, чтобы получить права на просмотр и управление базой клиентов.
          </p>
        </div>
      </div>
    );
  }

  if (selectedClientId) {
    return (
      <ClientDetail
        clientId={selectedClientId}
        onBack={() => setSelectedClientId(null)}
        initialCaseId={initialCaseId}
      />
    );
  }

  if (loading || legalEntitiesLoading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-500">{t('loading')}</div>
      </div>
    );
  }

  const handleAddClient = () => {
    if (!canCreateClients) {
      showPermissionWarning('Недостаточно прав для добавления клиентов.');
      return;
    }
    setEditingClient(null);
    setIsModalOpen(true);
  };

  const handleEditClient = (client: Client) => {
    if (!canEditClients) {
      showPermissionWarning('Недостаточно прав для редактирования клиентов.');
      return;
    }
    setEditingClient(client);
    setIsModalOpen(true);
  };

  const handleCloseModal = () => {
    setIsModalOpen(false);
    setEditingClient(null);
  };

  const handleSubmitClient = async (clientData: ClientInput) => {
    if (editingClient) {
      if (!canEditClients) {
        showPermissionWarning('Недостаточно прав для редактирования клиентов.');
        return;
      }
      await updateClient(editingClient.id, clientData);
    } else {
      if (!canCreateClients) {
        showPermissionWarning('Недостаточно прав для добавления клиентов.');
        return;
      }
      await createClient(clientData);
    }
  };

  const handleDeleteClient = async (id: number) => {
    if (!canDeleteClients) {
      showPermissionWarning('Недостаточно прав для удаления клиентов.');
      return;
    }
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
    if (!canCreateClients) {
      showPermissionWarning('Недостаточно прав для добавления дел.');
      return;
    }
    setCaseClientId(clientId);
    setEditingCase(null);
    setCaseMode('create');
    setCaseModalOpen(true);
  };

  const openEditCase = (clientId: number, clientCase: ClientCase) => {
    if (!canEditClients) {
      showPermissionWarning('Недостаточно прав для редактирования дел.');
      return;
    }
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
      if (!canEditClients) {
        showPermissionWarning('Недостаточно прав для редактирования дел.');
        return;
      }
      const updated = await apiService.updateCase(editingCase.id, base);
      setClients((prev) =>
        prev.map((c) =>
          c.id === caseClientId
            ? { ...c, cases: (c.cases ?? []).map((k) => (k.id === updated.id ? updated : k)) }
            : c,
        ),
      );
    } else {
      if (!canCreateClients) {
        showPermissionWarning('Недостаточно прав для добавления дел.');
        return;
      }
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
    if (!canDeleteClients) {
      showPermissionWarning('Недостаточно прав для удаления дел.');
      return;
    }
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

  const hasAnyClients = clients.length > 0;

  const handleViewModeChange = (mode: 'cards' | 'table') => {
    setViewMode(mode);
    try {
      localStorage.setItem('pp.clients_view', mode);
    } catch {
      /** */
    }
  };

  return (
    <div className="min-h-screen bg-gray-50">
      <div className="max-w-[calc(100vw-2rem)] mx-auto p-4 sm:p-6">
        <div className="mb-6 sm:mb-8">
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-col items-center sm:items-start text-center sm:text-left gap-2">
              <div className="flex items-center gap-3">
                <Users size={32} className="text-blue-600" />
                <h1 className="text-2xl sm:text-3xl font-bold text-gray-900">{t('clients')}</h1>
              </div>
              <p className="text-gray-600">{t('manageClients')}</p>
            </div>
            <div className="w-full sm:w-auto flex flex-col sm:flex-row gap-2 sm:items-center">
              <div className="relative w-full sm:w-64">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2" size={16} />
                <input
                  type="text"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder={t('search') || 'Поиск по имени, телефону, компании или email'}
                  className="pl-9 pr-8 py-2 w-full rounded-lg border border-gray-200 bg-white text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
                {query && (
                  <button
                    type="button"
                    aria-label={t('clear') || 'Очистить'}
                    className="absolute right-1 top-1/2 -translate-y-1/2 p-1 rounded hover:bg-gray-100"
                    onClick={() => setQuery('')}>
                    <X size={14} />
                  </button>
                )}
              </div>
              <div className="flex gap-1 bg-gray-100 p-1 rounded-lg">
                <button
                  type="button"
                  onClick={() => handleViewModeChange('cards')}
                  className={`p-2 rounded transition-colors ${
                    viewMode === 'cards'
                      ? 'bg-white text-blue-600 shadow-sm'
                      : 'text-gray-600 hover:text-gray-900'
                  }`}
                  title="Карточки">
                  <LayoutGrid size={18} />
                </button>
                <button
                  type="button"
                  onClick={() => handleViewModeChange('table')}
                  className={`p-2 rounded transition-colors ${
                    viewMode === 'table'
                      ? 'bg-white text-blue-600 shadow-sm'
                      : 'text-gray-600 hover:text-gray-900'
                  }`}
                  title="Таблица">
                  <List size={18} />
                </button>
              </div>
              {canCreateClients && (
                <button
                  type="button"
                  onClick={handleAddClient}
                  className="w-full sm:w-auto flex items-center justify-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors font-medium">
                  <Plus size={20} />
                  {t('addClient')}
                </button>
              )}
            </div>
          </div>
        </div>

        {!hasAnyClients ? (
          <div className="text-center py-12">
            <Users size={48} className="mx-auto mb-4 text-gray-300" />
            <h3 className="text-lg font-medium text-gray-900 mb-2">{t('noClientsYet')}</h3>
            <p className="text-gray-500 mb-4">{t('getStartedByAdding')}</p>
            {canCreateClients ? (
              <button
                type="button"
                onClick={handleAddClient}
                className="bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors">
                {t('addClient')}
              </button>
            ) : (
              <p className="text-sm text-gray-500">
                У вас нет прав для добавления клиентов.
              </p>
            )}
          </div>
        ) : sortedClients.length === 0 ? (
          <div className="text-center py-12">
            <Users size={48} className="mx-auto mb-4 text-gray-300" />
            <h3 className="text-lg font-medium text-gray-900 mb-2">
              {t('nothingFound') || 'Ничего не найдено'}
            </h3>
            <p className="text-gray-500 mb-4">
              {t('tryAnotherQuery') || 'Попробуйте изменить поисковый запрос.'}
            </p>
            {query && (
              <button
                type="button"
                onClick={() => setQuery('')}
                className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-gray-200 hover:bg-gray-50">
                <X size={16} />
                {t('clear') || 'Очистить поиск'}
              </button>
            )}
          </div>
        ) : viewMode === 'table' ? (
          <div className="bg-white rounded-xl shadow-sm border border-gray-100 overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full">
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    <th className="px-6 py-3 text-left text-xs font-semibold text-gray-700 uppercase tracking-wider">
                      Клиент
                    </th>
                    <th className="px-6 py-3 text-left text-xs font-semibold text-gray-700 uppercase tracking-wider">
                      {t('legalEntity') || 'Юр. лицо'}
                    </th>
                    <th className="px-6 py-3 text-left text-xs font-semibold text-gray-700 uppercase tracking-wider">
                      Контакты
                    </th>
                    <th className="px-6 py-3 text-left text-xs font-semibold text-gray-700 uppercase tracking-wider">
                      Дела
                    </th>
                    <th className="px-6 py-3 text-right text-xs font-semibold text-gray-700 uppercase tracking-wider">
                      Действия
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {sortedClients.map((client) => {
                    const cases = (client.cases ?? []) as ClientCase[];
                    const totalCases = cases.length;
                    const counts = cases.reduce<Record<string, number>>((acc, c) => {
                      const key = (c.status ?? 'unknown').toLowerCase();
                      acc[key] = (acc[key] ?? 0) + 1;
                      return acc;
                    }, {});

                    return (
                      <tr
                        key={client.id}
                        className={`hover:bg-gray-50 transition-colors ${
                          client.isActive ? '' : 'bg-gray-50 opacity-70'
                        }`}>
                        <td className="px-6 py-4">
                          <div className="flex items-center gap-3">
                            <div className="w-10 h-10 bg-blue-100 rounded-lg flex items-center justify-center shrink-0">
                              <Users size={20} className="text-blue-600" />
                            </div>
                            <div className="min-w-0">
                              <div className="flex flex-wrap items-center gap-2">
                                <span className="font-medium text-gray-900">
                                  {highlight(client.name ?? '', query)}
                                </span>
                                <ClientStatusBadge status={client.clientStatus} />
                              </div>
                              {client.company && (
                                <div className="text-sm text-gray-500">
                                  {highlight(client.company ?? '', query)}
                                </div>
                              )}
                            </div>
                          </div>
                        </td>
                        <td className="px-6 py-4">
                          {client.legalEntity ? (
                            <div className="space-y-1 text-sm text-gray-600">
                              <div className="flex items-center gap-2 text-gray-700">
                                <Building2 size={14} className="text-blue-500" />
                                <span className="truncate">
                                  {highlight(client.legalEntity.shortName ?? '', query)}
                                </span>
                              </div>
                              {client.legalEntity.inn && (
                                <div className="text-xs text-gray-500">
                                  ИНН: {highlight(client.legalEntity.inn ?? '', query)}
                                </div>
                              )}
                              {client.legalEntity.ogrn && (
                                <div className="text-xs text-gray-500">
                                  ОГРН: {highlight(client.legalEntity.ogrn ?? '', query)}
                                </div>
                              )}
                            </div>
                          ) : (
                            <span className="text-xs text-gray-400">{t('legalEntityNotAssigned') || 'Не указано'}</span>
                          )}
                        </td>
                        <td className="px-6 py-4">
                          <div className="text-sm space-y-1">
                            {client.email && (
                              <div className="flex items-center gap-2 text-gray-600">
                                <Mail size={14} />
                                <span>{highlight(client.email ?? '', query)}</span>
                              </div>
                            )}
                            {client.phone && (
                              <div className="flex items-center gap-2 text-gray-600">
                                <Phone size={14} />
                                <span>{highlightPhone(client.phone ?? '', query)}</span>
                              </div>
                            )}
                          </div>
                        </td>
                        <td className="px-6 py-4">
                          {totalCases > 0 ? (
                            <div className="space-y-1.5">
                              <div className="flex flex-wrap gap-1.5">
                                {Object.entries(counts)
                                  .sort((a, b) => b[1] - a[1])
                                  .slice(0, 3)
                                  .map(([status, count]) => {
                                    const label = caseStatusLabel(status);
                                    return (
                                      <span
                                        key={status}
                                        className={`text-xs px-2 py-1 rounded-full ${statusClasses(
                                          status,
                                        )}`}>
                                        {label} · {count}
                                      </span>
                                    );
                                  })}
                              </div>
                              <button
                                type="button"
                                onClick={() => {
                                  setSelectedClientId(client.id);
                                  setInitialCaseId('all');
                                }}
                                className="text-xs text-blue-600 hover:text-blue-700 hover:underline">
                                Посмотреть все дела →
                              </button>
                            </div>
                          ) : (
                            <span className="text-xs text-gray-500">Дел нет</span>
                          )}
                        </td>
                        <td className="px-6 py-4">
                          <div className="flex items-center justify-end gap-1">
                            {canCreateClients && (
                              <button
                                type="button"
                                onClick={() => openAddCase(client.id)}
                                className="p-2 text-gray-400 hover:text-emerald-600 hover:bg-emerald-50 rounded-lg transition-colors"
                                title="Добавить дело">
                                <PlusCircle size={16} />
                              </button>
                            )}
                            {canEditClients && (
                              <button
                                type="button"
                                onClick={() => handleEditClient(client)}
                                className="p-2 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded-lg transition-colors"
                                title={t('edit')}>
                                <Edit size={16} />
                              </button>
                            )}
                            <button
                              type="button"
                              onClick={() => setSelectedClientId(client.id)}
                              className="p-2 text-gray-400 hover:text-gray-600 hover:bg-gray-100 rounded-lg transition-colors"
                              title={t('viewDetails')}>
                              <Eye size={16} />
                            </button>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4 sm:gap-6 items-stretch min-h-0">
            {sortedClients.map((client) => {
              const cases = (client.cases ?? []) as ClientCase[];
              const totalCases = cases.length;

              const counts = cases.reduce<Record<string, number>>((acc, c) => {
                const key = (c.status ?? 'unknown').toLowerCase();
                acc[key] = (acc[key] ?? 0) + 1;
                return acc;
              }, {});

              const sortedByStatusThenDate = [...cases].sort((a, b) => {
                const ra = statusOrder(a.status);
                const rb = statusOrder(b.status);
                if (ra !== rb) return ra - rb;
                return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
              });

              const isExpanded = expandedCards.has(client.id);

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
                  className={`h-full flex flex-col overflow-hidden rounded-xl p-5 sm:p-6 shadow-sm border border-gray-100 hover:shadow-md transition-shadow
                  ${client.isActive ? 'bg-white' : 'bg-gray-100 opacity-70'}`}>
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between mb-4">
                    <div className="flex items-center gap-3 min-w-0 flex-1">
                      <div className="w-12 h-12 bg-blue-100 rounded-lg flex items-center justify-center shrink-0">
                        <Users size={24} className="text-blue-600" />
                      </div>
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center gap-2">
                          <h3 className="font-semibold text-gray-900 block max-w-full truncate">
                            {highlight(client.name ?? '', query)}
                          </h3>
                          <ClientStatusBadge status={client.clientStatus} />
                        </div>
                        {client.company && (
                          <p className="text-sm text-gray-500 block max-w-full truncate">
                            {highlight(client.company ?? '', query)}
                          </p>
                        )}
                      </div>
                    </div>
                    <div className="flex gap-1 self-end sm:self-auto shrink-0">
                      {canCreateClients && (
                        <button
                          type="button"
                          onClick={() => openAddCase(client.id)}
                          className="p-2 text-gray-400 hover:text-emerald-600 hover:bg-emerald-50 rounded-lg transition-colors"
                          title="Добавить дело">
                          <PlusCircle size={16} />{' '}
                        </button>
                      )}
                      {canEditClients && (
                        <button
                          type="button"
                          onClick={() => handleEditClient(client)}
                          className="p-2 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded-lg transition-colors"
                          title={t('edit')}>
                          <Edit size={16} />
                        </button>
                      )}
                      <button
                        type="button"
                        onClick={() => setSelectedClientId(client.id)}
                        className="p-2 text-gray-400 hover:text-gray-600 hover:bg-gray-100 rounded-lg transition-colors"
                        title={t('viewDetails')}>
                        <Eye size={16} />
                      </button>
                    </div>
                  </div>
                    <div className="flex-1 min-h-0 flex flex-col gap-1">
                      {client.email && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Mail size={14} />
                          <span className="truncate">{highlight(client.email ?? '', query)}</span>
                        </div>
                      )}
                      {client.phone && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Phone size={14} />
                          <span className="truncate">
                            {highlightPhone(client.phone ?? '', query)}
                          </span>
                        </div>
                      )}
                      {client.legalEntity && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Building2 size={14} className="text-blue-500" />
                          <span className="truncate">
                            {highlight(client.legalEntity.shortName ?? '', query)}
                          </span>
                        </div>
                      )}
                      {client.address && (
                        <div className="flex items-center gap-2 text-sm text-gray-600">
                          <Building size={14} />
                        <span className="truncate">{client.address}</span>
                      </div>
                    )}
                  </div>
                  <div className="mt-2">
                    <div className="flex items-center gap-2 mb-2">
                      <FolderKanban size={18} className="text-slate-600" />
                      <span className="font-medium text-slate-800">{t('cases') || 'Дела'}</span>
                      <span className="ml-auto text-xs text-slate-500">
                        {t('total') || 'Всего'}: {totalCases}
                      </span>
                    </div>
                    {totalCases > 0 ? (
                      <div className="flex flex-wrap gap-1.5 sm:gap-2 mb-3">
                        {Object.entries(counts)
                          .sort((a, b) => b[1] - a[1])
                          .slice(0, 4)
                          .map(([status, count]) => {
                            const label = caseStatusLabel(status);
                            return (
                              <span
                                key={status}
                                className={`text-[11px] sm:text-xs px-2 py-1 rounded-full ${statusClasses(
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
                            <div className="flex items-center gap-1 shrink-0">
                              <span
                                className={`text-[11px] px-2 py-1 rounded-full ${statusClasses(
                                  c.status,
                                )}`}>
                                {caseStatusLabel(c.status)}
                              </span>
                              {canEditClients && (
                                <button
                                  type="button"
                                  onClick={() => openEditCase(client.id, c)}
                                  className="ml-1 p-1.5 text-gray-400 hover:text-blue-600 hover:bg-blue-50 rounded transition-colors"
                                  title="Редактировать дело">
                                  <Edit size={14} />
                                </button>
                              )}
                            </div>
                          </li>
                        ))}
                      </ul>
                    )}
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
          legalEntities={legalEntities}
          clientStatuses={clientStatuses}
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
