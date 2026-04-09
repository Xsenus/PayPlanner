import { ArrowLeft, Loader2, Pencil, Save, Search, UserRound } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type { Act, ActInput, ActResponsible, ActStatus, Client } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { AuditInfoCard } from '../ui/AuditInfoCard';
import { ObjectAuditHistoryCard } from '../ui/ObjectAuditHistoryCard';

interface ActDetailPageProps {
  actId: number;
  onBack: () => void;
  onOpenClient?: (clientId: number) => void;
}

type FormState = {
  number: string;
  title: string;
  date: string;
  amount: string;
  invoiceNumber: string;
  counterpartyInn: string;
  status: ActStatus;
  clientId: string;
  responsibleId: string;
  comment: string;
};

const STATUS_VALUES: ActStatus[] = ['Created', 'Transferred', 'Signed', 'Terminated'];

function normalizeActToForm(act: Act): FormState {
  return {
    number: act.number ?? '',
    title: act.title ?? '',
    date: toDateInputValue(act.date) || toDateInputValue(new Date()),
    amount: act.amount !== undefined && act.amount !== null ? String(act.amount) : '',
    invoiceNumber: act.invoiceNumber ?? '',
    counterpartyInn: act.counterpartyInn ?? '',
    status: act.status,
    clientId: act.clientId ? String(act.clientId) : '',
    responsibleId: act.responsibleId ? String(act.responsibleId) : '',
    comment: act.comment ?? '',
  };
}

export function ActDetailPage({ actId, onBack, onOpenClient }: ActDetailPageProps) {
  const { t } = useTranslation();
  const [act, setAct] = useState<Act | null>(null);
  const [form, setForm] = useState<FormState | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [editMode, setEditMode] = useState(false);
  const [clientPickerOpen, setClientPickerOpen] = useState(false);
  const [clientSearch, setClientSearch] = useState('');

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      setLoading(true);
      setError(null);
      try {
        const [actsData, clientsData, responsiblesData] = await Promise.all([
          apiService.getActs({ page: 1, pageSize: 1000 }),
          apiService.getClients(),
          apiService.getActResponsibles(),
        ]);
        if (cancelled) return;
        const target = (actsData.items ?? []).find((item) => item.id === actId) ?? null;
        if (!target) {
          setError('Акт не найден');
          setAct(null);
          setForm(null);
          return;
        }
        setAct(target);
        setForm(normalizeActToForm(target));
        setClients(clientsData ?? []);
        setResponsibles(responsiblesData ?? []);
      } catch (e) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Не удалось загрузить данные');
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [actId]);

  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === String(form?.clientId ?? '')) ?? null,
    [clients, form?.clientId],
  );

  const filteredClients = useMemo(() => {
    const searchValue = clientSearch.trim().toLowerCase();
    return clients.filter((client) => {
      if (!searchValue) return true;
      return `${client.name} ${client.company ?? ''}`.toLowerCase().includes(searchValue);
    });
  }, [clientSearch, clients]);

  const statusOptions = useMemo(
    () =>
      STATUS_VALUES.map((status) => ({
        value: status,
        label:
          status === 'Created'
            ? t('actStatusCreated') ?? 'Создан'
            : status === 'Transferred'
              ? t('actStatusTransferred') ?? 'Передано'
              : status === 'Signed'
                ? t('actStatusSigned') ?? 'Подписано'
                : t('actStatusTerminated') ?? 'Расторгнуто',
      })),
    [t],
  );

  const handleChange = (field: keyof FormState) => (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>) => {
    const value = event.target.value;
    setForm((prev) => (prev ? { ...prev, [field]: value } : prev));
  };

  const handleSave = async () => {
    if (!form || !act) return;

    if (!form.number.trim()) {
      setError('Номер акта обязателен');
      return;
    }

    if (!form.date) {
      setError('Дата акта обязательна');
      return;
    }

    const parsedAmount = Number(String(form.amount).replace(/\s+/g, '').replace(',', '.'));
    if (Number.isNaN(parsedAmount)) {
      setError('Введите корректную сумму');
      return;
    }

    const payload: ActInput = {
      number: form.number.trim(),
      title: form.title.trim() ? form.title.trim() : undefined,
      date: fromInputToApiDate(form.date) ?? form.date,
      amount: parsedAmount,
      invoiceNumber: form.invoiceNumber.trim() ? form.invoiceNumber.trim() : undefined,
      counterpartyInn: form.counterpartyInn.trim() ? form.counterpartyInn.trim() : undefined,
      status: form.status,
      clientId: form.clientId ? Number(form.clientId) : undefined,
      responsibleId: form.responsibleId ? Number(form.responsibleId) : undefined,
      comment: form.comment.trim() ? form.comment.trim() : undefined,
    };

    setSaving(true);
    setError(null);
    try {
      const updated = await apiService.updateAct(act.id, payload);
      setAct(updated);
      setForm(normalizeActToForm(updated));
      setEditMode(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить');
    } finally {
      setSaving(false);
    }
  };

  if (loading || !form || !act) {
    return (
      <div className="p-8">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50 pb-12">
      <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 pt-8 sm:px-6 lg:px-8">
        <div className="mb-6 flex flex-wrap items-center justify-between gap-3">
          <button
            type="button"
            onClick={onBack}
            className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
          >
            <ArrowLeft className="h-4 w-4" />
            Назад
          </button>

          <div className="flex items-center gap-2">
            {!editMode ? (
              <button
                type="button"
                onClick={() => setEditMode(true)}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
              >
                <Pencil className="h-4 w-4" />
                {t('edit') ?? 'Редактировать'}
              </button>
            ) : (
              <button
                type="button"
                onClick={() => {
                  setForm(act ? normalizeActToForm(act) : form);
                  setEditMode(false);
                  setError(null);
                }}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
              >
                Отмена
              </button>
            )}

            <button
              type="button"
              disabled={!editMode || saving}
              onClick={() => void handleSave()}
              className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white shadow-lg shadow-emerald-200 transition-colors hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Сохранить
            </button>
          </div>
        </div>

        <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="mb-5">
            <h1 className="text-2xl font-bold text-slate-900">Редактировать акт</h1>
            <p className="text-sm text-slate-500">Создайте или обновите данные по акту</p>
          </div>

          {error ? (
            <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
          ) : null}

          <div className="mb-4">
            <div className="space-y-3">
              <AuditInfoCard
                createdAt={act.createdAt}
                createdByUserName={act.auditCreatedByUserName}
                updatedAt={act.auditUpdatedAt}
                updatedByUserName={act.auditUpdatedByUserName}
                isDeleted={act.auditIsDeleted}
                deletedAt={act.auditDeletedAt}
                deletedByUserName={act.auditDeletedByUserName}
                compact
              />
              <ObjectAuditHistoryCard objectType="act" objectId={act.id} compact />
            </div>
          </div>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Номер акта</span>
              <input type="text" value={form.number} onChange={handleChange('number')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Дата</span>
              <input type="date" value={form.date} onChange={handleChange('date')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Сумма</span>
              <input type="number" step="0.01" value={form.amount} onChange={handleChange('amount')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Счёт</span>
              <input type="text" value={form.invoiceNumber} onChange={handleChange('invoiceNumber')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">Название</span>
              <input type="text" value={form.title} onChange={handleChange('title')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>

            <div className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Контрагент</span>
              <div className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-700">
                {selectedClient?.name ?? 'Клиент не выбран'}
              </div>
              {selectedClient?.clientStatus ? (
                <div className="mt-1">
                  <ClientStatusBadge status={selectedClient.clientStatus} />
                </div>
              ) : null}
              <div className="mt-2 flex flex-wrap gap-2">
                <button
                  type="button"
                  disabled={!editMode}
                  onClick={() => setClientPickerOpen(true)}
                  className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  <Search className="h-4 w-4" />
                  Выбрать из справочника
                </button>
                {selectedClient?.id ? (
                  <button
                    type="button"
                    onClick={() => onOpenClient?.(selectedClient.id)}
                    className="inline-flex items-center gap-2 rounded-lg border border-indigo-200 bg-indigo-50 px-3 py-1.5 text-sm text-indigo-700 hover:bg-indigo-100"
                  >
                    <UserRound className="h-4 w-4" />
                    Открыть карточку
                  </button>
                ) : null}
              </div>
            </div>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">ИНН контрагента</span>
              <input type="text" value={form.counterpartyInn} onChange={handleChange('counterpartyInn')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Статус</span>
              <select value={form.status} onChange={handleChange('status')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50">
                {statusOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Ответственный</span>
              <select value={form.responsibleId} onChange={handleChange('responsibleId')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50">
                <option value="">Выберите ответственного</option>
                {responsibles.map((responsible) => (
                  <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">Комментарий</span>
              <textarea value={form.comment} onChange={handleChange('comment')} disabled={!editMode} className="min-h-[96px] rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
          </div>
        </div>
      </div>

      {clientPickerOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 px-4" onClick={() => setClientPickerOpen(false)}>
          <div className="w-full max-w-2xl rounded-2xl bg-white p-5 shadow-2xl" onClick={(event) => event.stopPropagation()}>
            <h3 className="text-lg font-semibold text-slate-900">Выберите контрагента</h3>
            <div className="mt-3">
              <input
                type="text"
                value={clientSearch}
                onChange={(event) => setClientSearch(event.target.value)}
                placeholder="Поиск по имени или компании"
                className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm"
              />
            </div>
            <div className="mt-3 max-h-[360px] overflow-auto rounded-lg border border-slate-200">
              {filteredClients.length === 0 ? (
                <div className="px-4 py-8 text-center text-sm text-slate-500">Клиенты не найдены</div>
              ) : (
                <div className="divide-y divide-slate-100">
                  {filteredClients.map((client) => (
                    <button
                      key={client.id}
                      type="button"
                      className="flex w-full items-start justify-between px-4 py-3 text-left hover:bg-slate-50"
                      onClick={() => {
                        setForm((prev) => (prev ? { ...prev, clientId: String(client.id) } : prev));
                        setClientPickerOpen(false);
                        setClientSearch('');
                      }}
                    >
                      <div>
                        <div className="font-medium text-slate-900">{client.name}</div>
                        {client.company ? <div className="text-xs text-slate-500">{client.company}</div> : null}
                      </div>
                      {client.clientStatus?.name ? <div className="text-xs text-slate-500">{client.clientStatus.name}</div> : null}
                    </button>
                  ))}
                </div>
              )}
            </div>
            <div className="mt-4 flex justify-end">
              <button
                type="button"
                onClick={() => setClientPickerOpen(false)}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm text-slate-700 hover:bg-slate-50"
              >
                Закрыть
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
