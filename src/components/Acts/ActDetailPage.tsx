import { ArrowLeft, Loader2, Pencil, Save } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type { Act, ActInput, ActResponsible, ActStatus, Client } from '../../types';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

interface ActDetailPageProps {
  actId: number;
  onBack: () => void;
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

export function ActDetailPage({ actId, onBack }: ActDetailPageProps) {
  const { t } = useTranslation();
  const [act, setAct] = useState<Act | null>(null);
  const [form, setForm] = useState<FormState | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [editMode, setEditMode] = useState(false);

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
          setError(t('actNotFound') ?? 'Акт не найден');
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
          setError(e instanceof Error ? e.message : (t('failedToLoad') ?? 'Не удалось загрузить данные'));
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [actId, t]);

  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === String(form?.clientId ?? '')) ?? null,
    [clients, form?.clientId],
  );

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
      setError(t('actNumber') ?? 'Номер акта обязателен');
      return;
    }

    if (!form.date) {
      setError(t('actDate') ?? 'Дата акта обязательна');
      return;
    }

    const parsedAmount = Number(String(form.amount).replace(/\s+/g, '').replace(',', '.'));
    if (Number.isNaN(parsedAmount)) {
      setError(t('actAmount') ?? 'Введите корректную сумму');
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
      setError(e instanceof Error ? e.message : (t('failedToSave') ?? 'Не удалось сохранить'));
    } finally {
      setSaving(false);
    }
  };

  if (loading || !form) {
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
            {t('back') ?? 'Назад'}
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
                {t('cancel') ?? 'Отмена'}
              </button>
            )}

            <button
              type="button"
              disabled={!editMode || saving}
              onClick={() => void handleSave()}
              className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white shadow-lg shadow-emerald-200 transition-colors hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              {t('save') ?? 'Сохранить'}
            </button>
          </div>
        </div>

        <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="mb-5">
            <h1 className="text-2xl font-bold text-slate-900">{t('actEdit') ?? 'Редактировать акт'}</h1>
            <p className="text-sm text-slate-500">{t('actModalSubtitle') ?? 'Создайте или обновите данные по акту'}</p>
          </div>

          {error ? (
            <div className="mb-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>
          ) : null}

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actNumber') ?? 'Номер акта'}</span>
              <input type="text" value={form.number} onChange={handleChange('number')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actDate') ?? 'Дата'}</span>
              <input type="date" value={form.date} onChange={handleChange('date')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actAmount') ?? 'Сумма'}</span>
              <input type="number" step="0.01" value={form.amount} onChange={handleChange('amount')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actInvoice') ?? 'Счёт'}</span>
              <input type="text" value={form.invoiceNumber} onChange={handleChange('invoiceNumber')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('actTitle') ?? 'Название'}</span>
              <input type="text" value={form.title} onChange={handleChange('title')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actClient') ?? 'Контрагент'}</span>
              <select value={form.clientId} onChange={handleChange('clientId')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50">
                <option value="">{t('selectClient') ?? 'Выберите клиента'}</option>
                {clients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name}
                    {client.clientStatus?.name ? ` · ${client.clientStatus.name}` : ''}
                    {client.company ? ` — ${client.company}` : ''}
                  </option>
                ))}
              </select>
              {selectedClient?.clientStatus ? (
                <div className="mt-1">
                  <ClientStatusBadge status={selectedClient.clientStatus} />
                </div>
              ) : null}
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actInn') ?? 'ИНН контрагента'}</span>
              <input type="text" value={form.counterpartyInn} onChange={handleChange('counterpartyInn')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actStatus') ?? 'Статус'}</span>
              <select value={form.status} onChange={handleChange('status')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50">
                {statusOptions.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actResponsible') ?? 'Ответственный'}</span>
              <select value={form.responsibleId} onChange={handleChange('responsibleId')} disabled={!editMode} className="rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50">
                <option value="">{t('selectResponsible') ?? 'Выберите ответственного'}</option>
                {responsibles.map((responsible) => (
                  <option key={responsible.id} value={responsible.id}>{responsible.name}</option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm sm:col-span-2">
              <span className="font-medium text-slate-700">{t('comment') ?? 'Комментарий'}</span>
              <textarea value={form.comment} onChange={handleChange('comment')} disabled={!editMode} className="min-h-[96px] rounded-lg border border-slate-300 px-3 py-2 text-sm disabled:bg-slate-50" />
            </label>
          </div>
        </div>
      </div>
    </div>
  );
}
