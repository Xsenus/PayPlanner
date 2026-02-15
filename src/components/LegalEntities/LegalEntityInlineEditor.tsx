import { useEffect, useMemo, useState } from 'react';
import { Loader2, Mail, MapPin, Phone, Users } from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type { Client, LegalEntityDetail, LegalEntityInput } from '../../types';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

interface Props {
  initial?: LegalEntityDetail | null;
  clients: Client[];
  loading?: boolean;
  allowEdit: boolean;
  allowDelete: boolean;
  onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail>;
  onDelete?: () => Promise<void>;
  onCancel: () => void;
}

interface FormState {
  shortName: string;
  fullName: string;
  inn: string;
  kpp: string;
  ogrn: string;
  address: string;
  phone: string;
  email: string;
  director: string;
  notes: string;
}

const DEFAULT_STATE: FormState = {
  shortName: '',
  fullName: '',
  inn: '',
  kpp: '',
  ogrn: '',
  address: '',
  phone: '',
  email: '',
  director: '',
  notes: '',
};

export function LegalEntityInlineEditor({
  initial,
  clients,
  allowEdit,
  allowDelete,
  loading,
  onSubmit,
  onDelete,
  onCancel,
}: Props) {
  const { t } = useTranslation();
  const [formState, setFormState] = useState<FormState>(DEFAULT_STATE);
  const [clientIds, setClientIds] = useState<number[]>([]);
  const [clientSearch, setClientSearch] = useState('');
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const readOnly = !!initial && !allowEdit;

  useEffect(() => {
    if (initial) {
      setFormState({
        shortName: initial.shortName ?? '',
        fullName: initial.fullName ?? '',
        inn: initial.inn ?? '',
        kpp: initial.kpp ?? '',
        ogrn: initial.ogrn ?? '',
        address: initial.address ?? '',
        phone: initial.phone ?? '',
        email: initial.email ?? '',
        director: initial.director ?? '',
        notes: initial.notes ?? '',
      });
      setClientIds((initial.clients ?? []).map((item) => item.id));
    } else {
      setFormState(DEFAULT_STATE);
      setClientIds([]);
    }
    setError(null);
    setClientSearch('');
  }, [initial]);

  const filteredClients = useMemo(() => {
    const term = clientSearch.trim().toLowerCase();
    if (!term) return clients;
    return clients.filter((client) => {
      const values = [client.name, client.phone, client.email, client.company]
        .filter(Boolean)
        .map((value) => value.toLowerCase());
      return values.some((value) => value.includes(term));
    });
  }, [clientSearch, clients]);

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (readOnly) return;
    const shortName = formState.shortName.trim();
    if (!shortName) {
      setError(t('legalEntityShortNameRequired') || 'Укажите краткое название');
      return;
    }

    const normalize = (value: string) => {
      const trimmed = value.trim();
      return trimmed || null;
    };

    const payload: LegalEntityInput = {
      shortName,
      fullName: normalize(formState.fullName),
      inn: normalize(formState.inn),
      kpp: normalize(formState.kpp),
      ogrn: normalize(formState.ogrn),
      address: normalize(formState.address),
      phone: normalize(formState.phone),
      email: normalize(formState.email),
      director: normalize(formState.director),
      notes: normalize(formState.notes),
      clientIds,
    };

    try {
      setSaving(true);
      setError(null);
      await onSubmit(payload);
      onCancel();
    } catch (err) {
      setError(err instanceof Error ? err.message : t('saveError') || 'Не удалось сохранить');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete || !initial) return;
    if (!confirm(t('deleteLegalEntityConfirm') || 'Удалить юридическое лицо?')) return;
    try {
      setDeleting(true);
      setError(null);
      await onDelete();
      onCancel();
    } catch (err) {
      setError(err instanceof Error ? err.message : t('legalEntityDeleteError') || 'Не удалось удалить запись');
    } finally {
      setDeleting(false);
    }
  };

  const setValue = (key: keyof FormState, value: string) => {
    setFormState((prev) => ({ ...prev, [key]: value }));
  };

  return (
    <section className="mb-6 rounded-2xl border border-blue-200 bg-white p-5 shadow-sm">
      <div className="mb-4 flex items-start justify-between gap-4 border-b border-gray-200 pb-3">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">
            {initial ? t('editLegalEntity') || 'Редактирование юридического лица' : t('addLegalEntity') || 'Добавление юридического лица'}
          </h2>
          <p className="text-xs text-gray-500">{readOnly ? t('view') || 'Просмотр' : 'Редактирование на странице'}</p>
        </div>
      </div>

      {error && <div className="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error}</div>}
      {(loading || saving) && (
        <div className="mb-4 flex items-center gap-2 rounded-lg border border-blue-100 bg-blue-50 p-3 text-sm text-blue-700">
          <Loader2 className="h-4 w-4 animate-spin" /> {t('loading') || 'Загрузка данных...'}
        </div>
      )}

      <form onSubmit={handleSubmit} className="space-y-5">
        <div className="grid gap-4 md:grid-cols-2">
          <label className="text-sm text-gray-700">
            <span className="mb-1 block">{t('legalEntityShortName') || 'Краткое название'}*</span>
            <input value={formState.shortName} onChange={(e) => setValue('shortName', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" />
          </label>
          <label className="text-sm text-gray-700">
            <span className="mb-1 block">{t('legalEntityFullName') || 'Полное название'}</span>
            <input value={formState.fullName} onChange={(e) => setValue('fullName', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" />
          </label>
          <label className="text-sm text-gray-700"><span className="mb-1 block">ИНН</span><input value={formState.inn} onChange={(e) => setValue('inn', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
          <label className="text-sm text-gray-700"><span className="mb-1 block">КПП</span><input value={formState.kpp} onChange={(e) => setValue('kpp', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
          <label className="text-sm text-gray-700"><span className="mb-1 block">ОГРН</span><input value={formState.ogrn} onChange={(e) => setValue('ogrn', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
          <label className="text-sm text-gray-700">
            <span className="mb-1 flex items-center gap-1"><MapPin size={14} />{t('legalEntityAddress') || 'Адрес'}</span>
            <input value={formState.address} onChange={(e) => setValue('address', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" />
          </label>
          <label className="text-sm text-gray-700">
            <span className="mb-1 flex items-center gap-1"><Phone size={14} />{t('legalEntityPhone') || 'Телефон'}</span>
            <input value={formState.phone} onChange={(e) => setValue('phone', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" />
          </label>
          <label className="text-sm text-gray-700">
            <span className="mb-1 flex items-center gap-1"><Mail size={14} />{t('legalEntityEmail') || 'Email'}</span>
            <input value={formState.email} onChange={(e) => setValue('email', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" />
          </label>
          <label className="text-sm text-gray-700"><span className="mb-1 block">{t('legalEntityDirector') || 'Руководитель'}</span><input value={formState.director} onChange={(e) => setValue('director', e.target.value)} disabled={readOnly} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
          <label className="text-sm text-gray-700 md:col-span-2"><span className="mb-1 block">{t('legalEntityNotes') || 'Заметки'}</span><textarea value={formState.notes} onChange={(e) => setValue('notes', e.target.value)} disabled={readOnly} rows={3} className="w-full rounded-lg border border-gray-300 px-3 py-2" /></label>
        </div>

        <div className="rounded-xl border border-gray-200 p-4">
          <div className="mb-2 flex items-center gap-2 text-sm font-medium text-gray-700"><Users size={14} />{t('legalEntityClients') || 'Клиенты юридического лица'}</div>
          <input value={clientSearch} onChange={(e) => setClientSearch(e.target.value)} placeholder={t('search') || 'Поиск'} disabled={readOnly} className="mb-3 w-full rounded-lg border border-gray-300 px-3 py-2 text-sm" />
          <div className="max-h-52 space-y-2 overflow-y-auto">
            {filteredClients.map((client) => (
              <label key={client.id} className="flex items-center justify-between rounded-lg border border-gray-200 px-3 py-2 text-sm">
                <span>{client.name}</span>
                <span className="flex items-center gap-2">
                  <ClientStatusBadge status={client.clientStatus ?? null} />
                  <input type="checkbox" checked={clientIds.includes(client.id)} onChange={() => setClientIds((prev) => prev.includes(client.id) ? prev.filter((id) => id !== client.id) : [...prev, client.id])} disabled={readOnly} />
                </span>
              </label>
            ))}
          </div>
        </div>

        <div className="flex flex-wrap gap-3">
          <button type="button" onClick={onCancel} className="rounded-lg border border-gray-300 px-4 py-2 text-sm">{t('cancel') || 'Отмена'}</button>
          {!readOnly && <button type="submit" disabled={saving} className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white">{initial ? t('save') || 'Сохранить' : t('add') || 'Добавить'}</button>}
          {allowDelete && initial && onDelete && (
            <button type="button" onClick={handleDelete} disabled={deleting} className="ml-auto rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-600">{t('delete') || 'Удалить'}</button>
          )}
        </div>
      </form>
    </section>
  );
}
