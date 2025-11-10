import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  Building2,
  CheckCircle2,
  Loader2,
  Mail,
  MapPin,
  Phone,
  Search,
  Users,
  X,
} from 'lucide-react';
import { useTranslation } from '../../hooks/useTranslation';
import type {
  Client,
  LegalEntityDetail,
  LegalEntityInput,
  LegalEntitySuggestion,
} from '../../types';

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
}: LegalEntityModalProps) {
  const { t } = useTranslation();
  const [formState, setFormState] = useState<FormState>(DEFAULT_STATE);
  const [clientIds, setClientIds] = useState<number[]>([]);
  const [loading, setLoading] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [clientSearch, setClientSearch] = useState('');
  const [dadataQuery, setDadataQuery] = useState('');
  const [suggestions, setSuggestions] = useState<LegalEntitySuggestion[]>([]);
  const [suggestLoading, setSuggestLoading] = useState(false);
  const [suggestError, setSuggestError] = useState<string | null>(null);

  const readOnly = !allowEdit && !!initial;

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
  }, [initial, isOpen]);

  const filteredClients = useMemo(() => {
    const term = clientSearch.trim().toLowerCase();
    if (!term) return clients;
    return clients.filter((client) => {
      const name = (client.name ?? '').toLowerCase();
      const email = (client.email ?? '').toLowerCase();
      const phone = (client.phone ?? '').toLowerCase();
      const company = (client.company ?? '').toLowerCase();
      return (
        name.includes(term) ||
        email.includes(term) ||
        phone.includes(term) ||
        company.includes(term)
      );
    });
  }, [clientSearch, clients]);

  const handleInputChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>,
  ) => {
    const { name, value } = e.target;
    setFormState((prev) => ({ ...prev, [name]: value }));
  };

  const handleToggleClient = (id: number) => {
    setClientIds((prev) =>
      prev.includes(id) ? prev.filter((cid) => cid !== id) : [...prev, id],
    );
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
      const payload: LegalEntityInput = {
        shortName: formState.shortName.trim(),
        clientIds: clientIds,
      };
      if (!payload.shortName) {
        setError(t('legalEntityShortNameRequired') || 'Укажите краткое название');
        setLoading(false);
        return;
      }
      const optionalFields: Array<[keyof FormState, keyof LegalEntityInput]> = [
        ['fullName', 'fullName'],
        ['inn', 'inn'],
        ['kpp', 'kpp'],
        ['ogrn', 'ogrn'],
        ['address', 'address'],
        ['phone', 'phone'],
        ['email', 'email'],
        ['director', 'director'],
        ['notes', 'notes'],
      ];
      for (const [stateKey, payloadKey] of optionalFields) {
        const value = formState[stateKey].trim();
        if (value) {
          (payload as Record<string, unknown>)[payloadKey] = value;
        }
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
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : t('saveError') || 'Не удалось сохранить');
    } finally {
      setLoading(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete || !initial) return;
    if (!confirm(t('deleteLegalEntityConfirm') || 'Удалить юридическое лицо?')) {
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

  const modalContent = (
    <div className="fixed inset-0 z-[1000] flex items-center justify-center bg-black/50 p-4">
      <div className="w-full max-w-5xl max-h-[90vh] overflow-y-auto rounded-2xl bg-white shadow-2xl">
        <div className="flex items-start justify-between border-b border-gray-200 px-6 py-4">
          <div>
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
            aria-label={t('cancel') || 'Закрыть'}>
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
              <div className="flex items-center justify-between gap-2">
                <div>
                  <h3 className="text-sm font-semibold text-gray-900">
                    {t('legalEntityClients') || 'Клиенты юридического лица'}
                  </h3>
                  <p className="text-xs text-gray-500">
                    {t('legalEntityClientsHint') || 'Отметьте клиентов, которые относятся к этому юридическому лицу.'}
                  </p>
                </div>
                <span className="rounded-full bg-blue-50 px-3 py-1 text-xs font-medium text-blue-600">
                  {`${t('selectedCount') || 'Выбрано'}: ${clientIds.length}`}
                </span>
              </div>
              <div className="mt-3 flex items-center gap-2">
                <div className="relative flex-1">
                  <Search className="pointer-events-none absolute left-3 top-3 h-4 w-4 text-gray-400" />
                  <input
                    type="text"
                    value={clientSearch}
                    onChange={(e) => setClientSearch(e.target.value)}
                    placeholder={t('searchClientsPlaceholder') || 'Поиск клиентов'}
                    className="w-full rounded-lg border border-gray-300 px-3 py-2 pl-9 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                  />
                </div>
                <button
                  type="button"
                  onClick={() => setClientIds(clients.map((c) => c.id))}
                  disabled={readOnly}
                  className="rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-600 hover:border-blue-500 hover:text-blue-600 disabled:cursor-not-allowed disabled:opacity-40">
                  {t('selectAll') || 'Выбрать всех'}
                </button>
                <button
                  type="button"
                  onClick={() => setClientIds([])}
                  disabled={readOnly}
                  className="rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-600 hover:border-blue-500 hover:text-blue-600 disabled:cursor-not-allowed disabled:opacity-40">
                  {t('clear') || 'Очистить'}
                </button>
              </div>
              <div className="mt-3 max-h-64 space-y-2 overflow-y-auto pr-2">
                {filteredClients.length === 0 ? (
                  <div className="rounded-lg border border-dashed border-gray-300 p-4 text-center text-sm text-gray-500">
                    {t('nothingFound') || 'Ничего не найдено'}
                  </div>
                ) : (
                  filteredClients.map((client) => {
                    const checked = clientIds.includes(client.id);
                    const assignedEntity = client.legalEntity?.shortName;
                    const assignedToOther = Boolean(
                      client.legalEntity && initial && client.legalEntity.id !== initial.id,
                    );
                    return (
                      <label
                        key={client.id}
                        className={`flex cursor-pointer items-start gap-3 rounded-xl border px-3 py-2 transition-colors ${
                          checked ? 'border-blue-400 bg-blue-50' : 'border-gray-200 hover:border-blue-300'
                        } ${readOnly ? 'pointer-events-none opacity-60' : ''}`}>
                        <input
                          type="checkbox"
                          checked={checked}
                          disabled={readOnly}
                          onChange={() => handleToggleClient(client.id)}
                          className="mt-1 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                        />
                        <div className="flex-1">
                          <div className="flex items-center gap-2 text-sm font-medium text-gray-900">
                            <Users size={16} className="text-blue-500" />
                            <span>{client.name}</span>
                            {!client.isActive && (
                              <span className="rounded bg-gray-200 px-2 py-0.5 text-xs text-gray-600">
                                {t('inactive') || 'неактивен'}
                              </span>
                            )}
                          </div>
                          <div className="mt-1 space-y-1 text-xs text-gray-600">
                            {client.phone && (
                              <div className="flex items-center gap-1">
                                <Phone size={12} />
                                <span>{client.phone}</span>
                              </div>
                            )}
                            {client.email && (
                              <div className="flex items-center gap-1">
                                <Mail size={12} />
                                <span>{client.email}</span>
                              </div>
                            )}
                            {assignedEntity && (
                              <div
                                className={`flex items-center gap-1 ${
                                  assignedToOther ? 'text-amber-600' : 'text-gray-500'
                                }`}>
                                <Building2 size={12} />
                                <span>
                                  {`${assignedToOther ? t('legalEntityAssignedOther') || 'Уже привязан к' : t('legalEntityAssignedCurrent') || 'Привязан к'}: ${assignedEntity}`}
                                </span>
                              </div>
                            )}
                          </div>
                        </div>
                      </label>
                    );
                  })
                )}
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
                  {t('cancel') || 'Отмена'}
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
      </div>
    </div>
  );

  return createPortal(modalContent, document.body);
}
