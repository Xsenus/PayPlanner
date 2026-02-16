import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  ArrowLeft,
  CheckCircle2,
  Loader2,
  Mail,
  MapPin,
  Phone,
  Search,
  Plus,
  X,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type {
  Client,
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntitySuggestion,
} from '../../types';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { useDialog } from '../../hooks/useDialog';

interface LegalEntityModalProps {
  isOpen: boolean;
  onClose: () => void;
  onSubmit: (payload: LegalEntityInput) => Promise<LegalEntityDetail>;
  onDelete?: () => Promise<void>;
  initial?: LegalEntityDetail | null;
  clients: Client[];
  suggest: (payload: { query?: string; inn?: string; limit?: number }) => Promise<LegalEntitySuggestion[]>;
  allowEdit: boolean;
  allowDelete: boolean;
  mode?: 'modal' | 'page';
  onBack?: () => void;
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

export function LegalEntityModal({
  isOpen,
  onClose,
  onSubmit,
  onDelete,
  initial,
  clients,
  suggest,
  allowEdit,
  allowDelete,
  mode = 'modal',
  onBack,
}: LegalEntityModalProps) {
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const [formState, setFormState] = useState<FormState>(DEFAULT_STATE);
  const [clientIds, setClientIds] = useState<number[]>([]);
  const [loading, setLoading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerSearch, setPickerSearch] = useState('');
  const [dadataQuery, setDadataQuery] = useState('');
  const [suggestions, setSuggestions] = useState<LegalEntitySuggestion[]>([]);
  const [suggestLoading, setSuggestLoading] = useState(false);
  const [suggestError, setSuggestError] = useState<string | null>(null);

  const readOnly = !allowEdit && !!initial;
  const isPageMode = mode === 'page';

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
      }
    };
    window.addEventListener('keydown', handleKey);
    return () => window.removeEventListener('keydown', handleKey);
  }, [isOpen, onClose]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }
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
      setClientIds((initial.clients ?? []).map((c) => c.id));
    } else {
      setFormState(DEFAULT_STATE);
      setClientIds([]);
    }
    setError(null);
    setSuggestions([]);
    setSuggestError(null);
    setDadataQuery('');
    setPickerOpen(false);
    setPickerSearch('');
  }, [initial, isOpen]);

  const selectedClients = useMemo(
    () => clients.filter((client) => clientIds.includes(client.id)),
    [clientIds, clients],
  );

  const availableClients = useMemo(() => {
    const selected = new Set(clientIds);
    const term = pickerSearch.trim().toLowerCase();

    return clients
      .filter((client) => !selected.has(client.id))
      .filter((client) => {
        if (!term) return true;
        return `${client.name ?? ''} ${client.company ?? ''} ${client.phone ?? ''} ${client.email ?? ''}`
          .toLowerCase()
          .includes(term);
      });
  }, [clientIds, clients, pickerSearch]);

  const handleInputChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>,
  ) => {
    const { name, value } = e.target;
    setFormState((prev) => ({ ...prev, [name]: value }));
  };

  const handleAddClient = (id: number) => {
    setClientIds((prev) => (prev.includes(id) ? prev : [...prev, id]));
  };

  const handleRemoveClient = (id: number) => {
    setClientIds((prev) => prev.filter((clientId) => clientId !== id));
  };

  const handleSuggest = async () => {
    setSuggestError(null);
    const query = dadataQuery.trim();
    if (!query) {
      setSuggestError(t('legalEntityDadataEmptyQuery') || 'Введите запрос для поиска');
      return;
    }
    try {
      setSuggestLoading(true);
      const results = await suggest({ query, limit: 6 });
      setSuggestions(results ?? []);
      if ((results ?? []).length === 0) {
        setSuggestError(t('legalEntityDadataNoResults') || 'Ничего не найдено');
      }
    } catch (err) {
      setSuggestError(
        err instanceof Error ? err.message : t('legalEntityDadataError') || 'Не удалось получить подсказки',
      );
    } finally {
      setSuggestLoading(false);
    }
  };

  const applySuggestion = (suggestion: LegalEntitySuggestion) => {
    setFormState((prev) => ({
      ...prev,
      shortName: suggestion.shortName || prev.shortName,
      fullName: suggestion.fullName ?? prev.fullName,
      inn: suggestion.inn ?? prev.inn,
      kpp: suggestion.kpp ?? prev.kpp,
      ogrn: suggestion.ogrn ?? prev.ogrn,
      address: suggestion.address ?? prev.address,
      phone: suggestion.phone ?? prev.phone,
      email: suggestion.email ?? prev.email,
      director: suggestion.director ?? prev.director,
    }));
  };

  const handleSubmit = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (readOnly) {
      onClose();
      return;
    }
    setError(null);
    setLoading(true);
    try {
      const shortName = formState.shortName.trim();
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
      if (!payload.shortName) {
        setError(t('legalEntityShortNameRequired') || 'Укажите краткое название');
        setLoading(false);
        return;
      }
      const saved = await onSubmit(payload);
      setFormState({
        shortName: saved.shortName ?? payload.shortName,
        fullName: saved.fullName ?? formState.fullName,
        inn: saved.inn ?? formState.inn,
        kpp: saved.kpp ?? formState.kpp,
        ogrn: saved.ogrn ?? formState.ogrn,
        address: saved.address ?? formState.address,
        phone: saved.phone ?? formState.phone,
        email: saved.email ?? formState.email,
        director: saved.director ?? formState.director,
        notes: saved.notes ?? formState.notes,
      });
      setClientIds((saved.clients ?? []).map((c) => c.id));
      if (!isPageMode) {
        onClose();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : t('saveError') || 'Не удалось сохранить');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete || !initial) return;
    if (!(await confirm({ title: 'Подтверждение удаления', description: t('deleteLegalEntityConfirm') || 'Удалить юридическое лицо?', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }))) {
      return;
    }
    try {
      setDeleting(true);
      await onDelete();
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : t('legalEntityDeleteError') || 'Не удалось удалить запись');
    } finally {
      setDeleting(false);
    }
  };

  if (!isOpen) return null;

  const content = (
    <div className={isPageMode ? 'rounded-2xl border border-gray-200 bg-white shadow-sm' : 'w-full max-w-5xl max-h-[90vh] overflow-y-auto rounded-2xl bg-white shadow-2xl'}>
        <div className="flex items-start justify-between border-b border-gray-200 px-6 py-4">
          <div>
            {isPageMode && onBack && (
              <button
                type="button"
                onClick={onBack}
                className="mb-3 inline-flex items-center gap-2 rounded border px-3 py-2 text-sm">
                <ArrowLeft size={16} />
                {t('back') || 'Назад'}
              </button>
            )}
            <h2 className="text-xl font-semibold text-gray-900">
              {initial
                ? t('editLegalEntity') || 'Редактирование юридического лица'
                : t('addLegalEntity') || 'Добавление юридического лица'}
            </h2>
            {initial && (
              <p className="mt-1 text-sm text-gray-500">
                {(t('createdAt') || 'Создано')}: {new Date(initial.createdAt).toLocaleString()}
                {initial.updatedAt ? (
                  <> • {(t('updatedAt') || 'Обновлено')}: {new Date(initial.updatedAt).toLocaleString()}</>
                ) : null}
              </p>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg p-2 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
            aria-label={isPageMode ? (t('back') || 'Назад') : (t('cancel') || 'Закрыть')}>
            <X size={20} />
          </button>
        </div>

        {error && (
          <div className="mx-6 mt-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700">
            {error}
          </div>
        )}

        <form onSubmit={handleSubmit} className="px-6 pb-6 pt-4 space-y-6">
          <div className="grid gap-6 md:grid-cols-2">
            <div className="space-y-4">
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityShortName') || 'Краткое название'}*
                </label>
                <input
                  type="text"
                  name="shortName"
                  value={formState.shortName}
                  onChange={handleInputChange}
                  required
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityFullName') || 'Полное название'}
                </label>
                <textarea
                  name="fullName"
                  value={formState.fullName}
                  onChange={handleInputChange}
                  rows={2}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityInn') || 'ИНН'}
                  </label>
                  <input
                    type="text"
                    name="inn"
                    value={formState.inn}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityKpp') || 'КПП'}
                  </label>
                  <input
                    type="text"
                    name="kpp"
                    value={formState.kpp}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityOgrn') || 'ОГРН'}
                  </label>
                  <input
                    type="text"
                    name="ogrn"
                    value={formState.ogrn}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityDirector') || 'Руководитель'}
                </label>
                <input
                  type="text"
                  name="director"
                  value={formState.director}
                  onChange={handleInputChange}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
            </div>
            <div className="space-y-4">
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityAddress') || 'Адрес'}
                </label>
                <div className="relative">
                  <MapPin className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
                  <input
                    type="text"
                    name="address"
                    value={formState.address}
                    onChange={handleInputChange}
                    disabled={readOnly}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
              </div>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityPhone') || 'Телефон'}
                  </label>
                  <div className="relative">
                    <Phone className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
                    <input
                      type="text"
                      name="phone"
                      value={formState.phone}
                      onChange={handleInputChange}
                      disabled={readOnly}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                  </div>
                </div>
                <div>
                  <label className="mb-2 block text-sm font-medium text-gray-700">
                    {t('legalEntityEmail') || 'Email'}
                  </label>
                  <div className="relative">
                    <Mail className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
                    <input
                      type="email"
                      name="email"
                      value={formState.email}
                      onChange={handleInputChange}
                      disabled={readOnly}
                      className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                  </div>
                </div>
              </div>
              <div>
                <label className="mb-2 block text-sm font-medium text-gray-700">
                  {t('legalEntityNotes') || 'Заметки'}
                </label>
                <textarea
                  name="notes"
                  value={formState.notes}
                  onChange={handleInputChange}
                  rows={3}
                  disabled={readOnly}
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
            </div>
          </div>

          <div className="grid gap-6 lg:grid-cols-[2fr,1fr]">
            <div className="rounded-xl border border-gray-200 p-4">
              <div className="flex items-center justify-between gap-2 border-b border-gray-100 pb-3">
                <div>
                  <h3 className="text-sm font-semibold text-gray-900">
                    {t('legalEntityClients') || 'Клиенты юридического лица'}
                  </h3>
                  <p className="text-xs text-gray-500">
                    {t('legalEntityClientsHint') || 'Выберите клиентов, которые относятся к этому юридическому лицу.'}
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => setPickerOpen(true)}
                  disabled={readOnly}
                  className="inline-flex items-center gap-2 rounded border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 disabled:cursor-not-allowed disabled:opacity-60">
                  <Plus className="h-4 w-4" />
                  {t('addClient') || 'Добавить клиента'}
                </button>
              </div>

              <div className="mt-3 overflow-x-auto">
                <table className="min-w-full divide-y text-sm">
                  <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                    <tr>
                      <th className="px-3 py-2 text-left">ФИО</th>
                      <th className="px-3 py-2 text-left">Компания</th>
                      <th className="px-3 py-2 text-left">Контакты</th>
                      <th className="px-3 py-2 text-right">Действия</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {selectedClients.map((client) => (
                      <tr key={client.id}>
                        <td className="px-3 py-2">
                          <div className="font-medium text-gray-900">{client.name}</div>
                          <div className="mt-1"><ClientStatusBadge status={client.clientStatus} /></div>
                        </td>
                        <td className="px-3 py-2">{client.company || '—'}</td>
                        <td className="px-3 py-2 text-xs text-gray-600">
                          <div>{client.phone || '—'}</div>
                          <div>{client.email || '—'}</div>
                        </td>
                        <td className="px-3 py-2 text-right">
                          <button
                            type="button"
                            onClick={() => handleRemoveClient(client.id)}
                            disabled={readOnly}
                            className="rounded border border-rose-200 p-1 text-rose-600 disabled:opacity-60">
                            <X className="h-4 w-4" />
                          </button>
                        </td>
                      </tr>
                    ))}
                    {selectedClients.length === 0 && (
                      <tr>
                        <td colSpan={4} className="px-3 py-6 text-center text-slate-500">
                          {t('nothingFound') || 'Ничего не найдено'}
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            </div>
            <div className="rounded-xl border border-dashed border-blue-200 bg-blue-50/50 p-4">
              <h3 className="text-sm font-semibold text-blue-900">
                {t('legalEntityDadataTitle') || 'Заполнить по данным DaData'}
              </h3>
              <p className="mt-1 text-xs text-blue-700/80">
                {t('legalEntityDadataDescription') ||
                  'Введите ИНН, название или адрес компании, чтобы автоматически заполнить основные поля.'}
              </p>
              <div className="mt-3 flex items-center gap-2">
                <div className="relative flex-1">
                  <Search className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-blue-400" />
                  <input
                    type="text"
                    value={dadataQuery}
                    onChange={(e) => setDadataQuery(e.target.value)}
                    placeholder={t('legalEntityDadataPlaceholder') || 'Например: 7707083893 или ООО Пример'}
                    className="w-full rounded-lg border border-blue-200 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-400"
                  />
                </div>
                <button
                  type="button"
                  onClick={handleSuggest}
                  disabled={suggestLoading || readOnly}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300">
                  {suggestLoading && <Loader2 className="h-4 w-4 animate-spin" />}
                  {t('legalEntityDadataSearch') || 'Найти'}
                </button>
              </div>
              {suggestError && (
                <div className="mt-2 rounded-lg border border-amber-200 bg-amber-50 p-2 text-xs text-amber-700">
                  {suggestError}
                </div>
              )}
              <div className="mt-3 space-y-2">
                {suggestions.map((suggestion, idx) => (
                  <button
                    key={`${suggestion.inn ?? suggestion.shortName}-${idx}`}
                    type="button"
                    onClick={() => applySuggestion(suggestion)}
                    className="w-full rounded-lg border border-blue-200 bg-white px-4 py-3 text-left text-sm shadow-sm transition hover:border-blue-400 hover:shadow">
                    <div className="flex items-start justify-between gap-2">
                      <div>
                        <div className="font-medium text-blue-900">{suggestion.shortName}</div>
                        {suggestion.fullName && (
                          <div className="text-xs text-blue-700">{suggestion.fullName}</div>
                        )}
                        <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-blue-700/80">
                          {suggestion.inn && <span>ИНН: {suggestion.inn}</span>}
                          {suggestion.kpp && <span>КПП: {suggestion.kpp}</span>}
                          {suggestion.ogrn && <span>ОГРН: {suggestion.ogrn}</span>}
                        </div>
                        {suggestion.address && (
                          <div className="mt-1 text-xs text-blue-700/80">{suggestion.address}</div>
                        )}
                      </div>
                      <CheckCircle2 className="h-5 w-5 text-blue-500" />
                    </div>
                  </button>
                ))}
                {!suggestLoading && suggestions.length === 0 && !suggestError && (
                  <div className="rounded-lg border border-dashed border-blue-200 p-3 text-center text-xs text-blue-700/80">
                    {t('legalEntityDadataHint') || 'Результаты появятся после выполнения поиска.'}
                  </div>
                )}
              </div>
            </div>
          </div>

          <div className="flex flex-col gap-3 border-t border-gray-200 pt-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="text-xs text-gray-500">
              {t('legalEntityFormHint') || 'Поля, помеченные * обязательны для заполнения.'}
            </div>
            <div className="flex flex-wrap items-center gap-3">
              {allowDelete && initial && onDelete && (
                <button
                  type="button"
                  onClick={handleDelete}
                  disabled={deleting}
                  className="inline-flex items-center gap-2 rounded-lg border border-red-200 bg-red-50 px-4 py-2 text-sm font-medium text-red-600 hover:bg-red-100">
                  {deleting && <Loader2 className="h-4 w-4 animate-spin" />}
                  {t('delete') || 'Удалить'}
                </button>
              )}
              <div className="flex items-center gap-3">
                <button
                  type="button"
                  onClick={onClose}
                  className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-600 hover:border-gray-400 hover:text-gray-700">
                  {isPageMode ? (t('back') || 'Назад') : (t('cancel') || 'Отмена')}
                </button>
                <button
                  type="submit"
                  disabled={loading || readOnly}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-blue-300">
                  {loading && <Loader2 className="h-4 w-4 animate-spin" />}
                  {initial ? t('save') || 'Сохранить' : t('add') || 'Добавить'}
                </button>
              </div>
            </div>
          </div>
        </form>

        {pickerOpen && (
          <div className="fixed inset-0 z-[1100] flex items-center justify-center bg-black/50 p-4">
            <div className="w-full max-w-3xl rounded-xl bg-white shadow-2xl">
              <div className="flex items-center justify-between border-b px-4 py-3">
                <h3 className="text-lg font-semibold">Выбор клиента</h3>
                <button className="rounded border px-2 py-1 text-sm" onClick={() => setPickerOpen(false)}>
                  Закрыть
                </button>
              </div>
              <div className="p-4">
                <div className="relative mb-3">
                  <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="search"
                    value={pickerSearch}
                    onChange={(e) => setPickerSearch(e.target.value)}
                    className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-3"
                    placeholder={t('searchClientsPlaceholder') || 'Поиск клиентов'}
                  />
                </div>

                <div className="max-h-80 overflow-auto rounded border">
                  <table className="min-w-full divide-y text-sm">
                    <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                      <tr>
                        <th className="px-3 py-2 text-left">ФИО</th>
                        <th className="px-3 py-2 text-left">Компания</th>
                        <th className="px-3 py-2 text-right">Действие</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y">
                      {availableClients.map((client) => (
                        <tr key={client.id}>
                          <td className="px-3 py-2">{client.name}</td>
                          <td className="px-3 py-2">{client.company || '—'}</td>
                          <td className="px-3 py-2 text-right">
                            <button
                              type="button"
                              className="rounded border border-emerald-200 bg-emerald-50 px-2 py-1 text-xs text-emerald-700"
                              onClick={() => handleAddClient(client.id)}>
                              Добавить
                            </button>
                          </td>
                        </tr>
                      ))}
                      {availableClients.length === 0 && (
                        <tr>
                          <td colSpan={3} className="px-3 py-6 text-center text-slate-500">
                            Нет доступных клиентов
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
  );

  if (isPageMode) {
    return content;
  }

  return createPortal(
    <div className="fixed inset-0 z-[1000] flex items-center justify-center bg-black/50 p-4">
      {content}
    </div>,
    document.body,
  );
}
