import { Building2, Loader2, Pencil, Plus, Trash2, X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';

import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import {
  apiService,
  type BankDto,
  type BankUpsertRequest,
} from '../../services/api';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface BankFormState {
  name: string;
  bik: string;
  correspondentAccount: string;
  city: string;
  isForeign: boolean;
}

const EMPTY_FORM: BankFormState = {
  name: '',
  bik: '',
  correspondentAccount: '',
  city: '',
  isForeign: false,
};

function toFormState(bank?: BankDto | null): BankFormState {
  if (!bank) {
    return EMPTY_FORM;
  }

  return {
    name: bank.name ?? '',
    bik: bank.bik ?? '',
    correspondentAccount: bank.correspondentAccount ?? '',
    city: bank.city ?? '',
    isForeign: bank.isForeign ?? false,
  };
}

function normalizePayload(form: BankFormState): BankUpsertRequest {
  const normalizeOptional = (value: string) => {
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  };

  return {
    name: form.name.trim(),
    bik: normalizeOptional(form.bik),
    correspondentAccount: normalizeOptional(form.correspondentAccount),
    city: normalizeOptional(form.city),
    isForeign: form.isForeign,
  };
}

export function BankDirectorySettings() {
  const toast = useToast();
  const { confirm } = useDialog();
  const [banks, setBanks] = useState<BankDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [editorOpen, setEditorOpen] = useState(false);
  const [editingBank, setEditingBank] = useState<BankDto | null>(null);
  const [form, setForm] = useState<BankFormState>(EMPTY_FORM);

  const loadBanks = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await apiService.getBanks();
      setBanks(data ?? []);
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить справочник банков.'));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadBanks();
  }, [loadBanks]);

  const filteredBanks = useMemo(() => {
    const query = search.trim().toLocaleLowerCase('ru');
    if (!query) {
      return banks;
    }

    return banks.filter((bank) => {
      const haystack = [
        bank.name,
        bank.bik ?? '',
        bank.correspondentAccount ?? '',
        bank.city ?? '',
      ]
        .join(' ')
        .toLocaleLowerCase('ru');

      return haystack.includes(query);
    });
  }, [banks, search]);

  const openCreate = () => {
    setEditingBank(null);
    setForm(EMPTY_FORM);
    setEditorOpen(true);
  };

  const openEdit = (bank: BankDto) => {
    setEditingBank(bank);
    setForm(toFormState(bank));
    setEditorOpen(true);
  };

  const closeEditor = () => {
    if (saving) {
      return;
    }

    setEditorOpen(false);
    setEditingBank(null);
    setForm(EMPTY_FORM);
  };

  const handleSave = async () => {
    const payload = normalizePayload(form);
    if (!payload.name) {
      toast.error('Укажи название банка.', 'Банки');
      return;
    }

    try {
      setSaving(true);
      setError(null);
      setMessage(null);

      const saved = editingBank
        ? await apiService.updateBank(editingBank.id, payload)
        : await apiService.createBank(payload);

      setBanks((prev) => {
        if (editingBank) {
          return prev
            .map((item) => (item.id === saved.id ? saved : item))
            .sort((left, right) => left.name.localeCompare(right.name, 'ru', { sensitivity: 'base' }));
        }

        return [...prev, saved].sort((left, right) =>
          left.name.localeCompare(right.name, 'ru', { sensitivity: 'base' }),
        );
      });

      const successMessage = editingBank
        ? 'Банк обновлен.'
        : 'Банк добавлен в справочник.';
      window.dispatchEvent(new CustomEvent('banks:changed'));
      setMessage(successMessage);
      toast.success(successMessage, 'Банки');
      closeEditor();
    } catch (saveError) {
      const nextError = extractDisplayErrorMessage(saveError, 'Не удалось сохранить банк.');
      setError(nextError);
      toast.error(nextError, 'Банки');
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (bank: BankDto) => {
    const approved = await confirm({
      title: 'Удалить банк',
      description: `Удалить банк «${bank.name}» из справочника?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });

    if (!approved) {
      return;
    }

    try {
      await apiService.deleteBank(bank.id);
      window.dispatchEvent(new CustomEvent('banks:changed'));
      setBanks((prev) => prev.filter((item) => item.id !== bank.id));
      setMessage('Банк удален из справочника.');
      setError(null);
      toast.success('Банк удален из справочника.', 'Банки');
    } catch (deleteError) {
      const nextError = extractDisplayErrorMessage(deleteError, 'Не удалось удалить банк.');
      setError(nextError);
      toast.error(nextError, 'Банки');
    }
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h4 className="text-xl font-semibold text-gray-900">Справочник банков</h4>
          <p className="mt-1 max-w-3xl text-sm text-gray-600">
            Банки теперь живут как отдельная сущность. Их можно один раз занести в справочник и потом
            выбирать в карточках юридических лиц для расчетных счетов и карт.
          </p>
        </div>

        <button
          type="button"
          onClick={openCreate}
          className="inline-flex items-center justify-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700"
        >
          <Plus className="h-4 w-4" />
          Добавить банк
        </button>
      </div>

      <div className="mt-6 grid gap-4 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-end">
        <label className="block">
          <span className="mb-2 block text-sm font-medium text-gray-700">Поиск по справочнику</span>
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder="Название банка, БИК, корр. счет, город"
            className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
          />
        </label>

        <div className="text-sm text-slate-500">
          {loading ? 'Загрузка...' : `Всего банков: ${filteredBanks.length}`}
        </div>
      </div>

      {(error || message) ? (
        <div className="mt-4 space-y-3">
          {error ? (
            <div className="rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {error}
            </div>
          ) : null}
          {message ? (
            <div className="rounded-2xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">
              {message}
            </div>
          ) : null}
        </div>
      ) : null}

      <div className="mt-6 overflow-hidden rounded-2xl border border-slate-200">
        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-slate-200 text-sm">
            <thead className="bg-slate-50">
              <tr className="text-left text-slate-600">
                <th className="px-4 py-3 font-semibold">Банк</th>
                <th className="px-4 py-3 font-semibold">БИК</th>
                <th className="px-4 py-3 font-semibold">Корр. счет</th>
                <th className="px-4 py-3 font-semibold">Город</th>
                <th className="px-4 py-3 font-semibold">Тип</th>
                <th className="px-4 py-3 font-semibold text-right">Действия</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 bg-white">
              {loading ? (
                <tr>
                  <td colSpan={6} className="px-4 py-12 text-center text-slate-500">
                    <span className="inline-flex items-center gap-2">
                      <Loader2 className="h-4 w-4 animate-spin" />
                      Загружаем банки...
                    </span>
                  </td>
                </tr>
              ) : filteredBanks.length === 0 ? (
                <tr>
                  <td colSpan={6} className="px-4 py-12 text-center text-slate-500">
                    Ничего не найдено.
                  </td>
                </tr>
              ) : (
                filteredBanks.map((bank) => (
                  <tr key={bank.id}>
                    <td className="px-4 py-4">
                      <div className="flex items-start gap-3">
                        <div className="mt-0.5 flex h-9 w-9 items-center justify-center rounded-xl bg-slate-100 text-slate-500">
                          <Building2 className="h-4 w-4" />
                        </div>
                        <div className="min-w-0">
                          <div className="font-semibold text-slate-900">{bank.name}</div>
                          <div className="mt-1 text-xs text-slate-500">
                            Создан: {new Date(bank.createdAt).toLocaleString('ru-RU')}
                          </div>
                        </div>
                      </div>
                    </td>
                    <td className="px-4 py-4 text-slate-700">{bank.bik || '—'}</td>
                    <td className="px-4 py-4 text-slate-700">{bank.correspondentAccount || '—'}</td>
                    <td className="px-4 py-4 text-slate-700">{bank.city || '—'}</td>
                    <td className="px-4 py-4 text-slate-700">
                      {bank.isForeign ? 'Иностранный' : 'Российский'}
                    </td>
                    <td className="px-4 py-4">
                      <div className="flex justify-end gap-2">
                        <button
                          type="button"
                          onClick={() => openEdit(bank)}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-slate-200 text-slate-600 transition hover:bg-slate-50"
                          aria-label={`Редактировать банк ${bank.name}`}
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          type="button"
                          onClick={() => void handleDelete(bank)}
                          className="inline-flex h-9 w-9 items-center justify-center rounded-xl border border-rose-200 text-rose-600 transition hover:bg-rose-50"
                          aria-label={`Удалить банк ${bank.name}`}
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      {editorOpen && typeof document !== 'undefined'
        ? createPortal(
          <div className="fixed inset-0 z-[2200] flex items-center justify-center bg-slate-900/45 p-4">
            <div className="w-full max-w-3xl rounded-3xl bg-white shadow-2xl">
              <div className="flex items-center justify-between border-b border-slate-200 px-6 py-5">
                <div>
                  <h3 className="text-2xl font-semibold text-slate-900">
                    {editingBank ? 'Редактировать банк' : 'Добавить банк'}
                  </h3>
                  <p className="mt-1 text-sm text-slate-500">
                    Эти реквизиты потом будут доступны для выбора в расчетных счетах и картах юридических лиц.
                  </p>
                </div>
                <button
                  type="button"
                  onClick={closeEditor}
                  className="rounded-xl p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600"
                  aria-label="Закрыть"
                >
                  <X className="h-5 w-5" />
                </button>
              </div>

              <div className="grid gap-4 px-6 py-5 md:grid-cols-2">
                <label className="block md:col-span-2">
                  <span className="mb-2 block text-sm font-medium text-gray-700">Название банка *</span>
                  <input
                    type="text"
                    value={form.name}
                    onChange={(event) => setForm((prev) => ({ ...prev, name: event.target.value }))}
                    className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                    placeholder="Например, ООО «Банк Точка»"
                  />
                </label>

                <label className="block">
                  <span className="mb-2 block text-sm font-medium text-gray-700">БИК</span>
                  <input
                    type="text"
                    value={form.bik}
                    onChange={(event) => setForm((prev) => ({ ...prev, bik: event.target.value }))}
                    className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                    placeholder="044525104"
                  />
                </label>

                <label className="block">
                  <span className="mb-2 block text-sm font-medium text-gray-700">Корреспондентский счет</span>
                  <input
                    type="text"
                    value={form.correspondentAccount}
                    onChange={(event) => setForm((prev) => ({ ...prev, correspondentAccount: event.target.value }))}
                    className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                    placeholder="30101810..."
                  />
                </label>

                <label className="block">
                  <span className="mb-2 block text-sm font-medium text-gray-700">Город</span>
                  <input
                    type="text"
                    value={form.city}
                    onChange={(event) => setForm((prev) => ({ ...prev, city: event.target.value }))}
                    className="w-full rounded-xl border border-slate-300 px-3 py-2.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                    placeholder="Москва"
                  />
                </label>

                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">
                  <input
                    type="checkbox"
                    checked={form.isForeign}
                    onChange={(event) => setForm((prev) => ({ ...prev, isForeign: event.target.checked }))}
                    className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                  />
                  Иностранный банк
                </label>
              </div>

              <div className="flex items-center justify-end gap-3 border-t border-slate-200 px-6 py-5">
                <button
                  type="button"
                  onClick={closeEditor}
                  disabled={saving}
                  className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  Отмена
                </button>
                <button
                  type="button"
                  onClick={() => void handleSave()}
                  disabled={saving}
                  className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-60"
                >
                  {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                  Сохранить
                </button>
              </div>
            </div>
          </div>,
          document.body,
        )
        : null}
    </div>
  );
}
