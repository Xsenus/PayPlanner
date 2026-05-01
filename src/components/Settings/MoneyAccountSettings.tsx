import { Building2, CreditCard, Landmark, Pencil, Plus, Save, Trash2, WalletCards, X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';

import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { MoneyAccount, MoneyOperationSource } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type MoneyAccountForm = {
  name: string;
  source: MoneyOperationSource;
  currency: string;
  bankName: string;
  bankAccountNumber: string;
  isDefault: boolean;
  isActive: boolean;
};

const SOURCE_LABELS: Record<MoneyOperationSource, string> = {
  Bank: 'Банк',
  Cash: 'Касса',
  Card: 'Карты',
  EMoney: 'E-money',
};

const SOURCE_HINTS: Record<MoneyOperationSource, string> = {
  Bank: 'Расчетные счета и банковские выписки',
  Cash: 'Наличные деньги и кассовые ордера',
  Card: 'Корпоративные карты',
  EMoney: 'Электронные кошельки',
};

const SOURCE_ICON: Record<MoneyOperationSource, typeof Landmark> = {
  Bank: Landmark,
  Cash: WalletCards,
  Card: CreditCard,
  EMoney: Building2,
};

const EMPTY_FORM: MoneyAccountForm = {
  name: '',
  source: 'Bank',
  currency: 'RUB',
  bankName: '',
  bankAccountNumber: '',
  isDefault: false,
  isActive: true,
};

function toForm(account?: MoneyAccount | null): MoneyAccountForm {
  if (!account) return EMPTY_FORM;

  return {
    name: account.name ?? '',
    source: account.source,
    currency: account.currency || 'RUB',
    bankName: account.bankName ?? '',
    bankAccountNumber: account.bankAccountNumber ?? '',
    isDefault: account.isDefault,
    isActive: account.isActive,
  };
}

function toPayload(form: MoneyAccountForm, id = 0): MoneyAccount {
  const optional = (value: string) => {
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  };

  return {
    id,
    name: form.name.trim(),
    source: form.source,
    currency: (form.currency.trim() || 'RUB').toUpperCase(),
    bankName: optional(form.bankName),
    bankAccountNumber: optional(form.bankAccountNumber),
    isDefault: form.isDefault,
    isActive: form.isActive,
    createdAt: new Date().toISOString(),
    updatedAt: null,
  };
}

function formatAccountMeta(account: MoneyAccount) {
  const parts = [account.currency || 'RUB'];
  if (account.bankName) parts.push(account.bankName);
  if (account.bankAccountNumber) parts.push(account.bankAccountNumber);
  return parts.join(' · ');
}

export function MoneyAccountSettings() {
  const toast = useToast();
  const { confirm } = useDialog();
  const [accounts, setAccounts] = useState<MoneyAccount[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [sourceFilter, setSourceFilter] = useState<'all' | MoneyOperationSource>('all');
  const [editorOpen, setEditorOpen] = useState(false);
  const [editingAccount, setEditingAccount] = useState<MoneyAccount | null>(null);
  const [form, setForm] = useState<MoneyAccountForm>(EMPTY_FORM);

  const loadAccounts = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await apiService.getMoneyAccounts({ activeOnly: false });
      setAccounts(data ?? []);
    } catch (loadError) {
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить денежные счета.'));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadAccounts();
  }, [loadAccounts]);

  const filteredAccounts = useMemo(
    () => accounts.filter((account) => sourceFilter === 'all' || account.source === sourceFilter),
    [accounts, sourceFilter],
  );

  const groupedTotals = useMemo(
    () =>
      (['Bank', 'Cash', 'Card', 'EMoney'] as MoneyOperationSource[]).map((source) => ({
        source,
        active: accounts.filter((account) => account.source === source && account.isActive).length,
        total: accounts.filter((account) => account.source === source).length,
      })),
    [accounts],
  );

  const openCreate = (source: MoneyOperationSource = 'Bank') => {
    setEditingAccount(null);
    setForm({ ...EMPTY_FORM, source });
    setEditorOpen(true);
  };

  const openEdit = (account: MoneyAccount) => {
    setEditingAccount(account);
    setForm(toForm(account));
    setEditorOpen(true);
  };

  const resetEditor = () => {
    setEditorOpen(false);
    setEditingAccount(null);
    setForm(EMPTY_FORM);
  };

  const closeEditor = () => {
    if (saving) return;
    resetEditor();
  };

  const saveAccount = async () => {
    const payload = toPayload(form, editingAccount?.id ?? 0);
    if (!payload.name) {
      toast.error('Укажите название денежного счета.', 'Денежные счета');
      return;
    }

    try {
      setSaving(true);
      setError(null);
      setMessage(null);

      editingAccount
        ? await apiService.updateMoneyAccount(editingAccount.id, payload)
        : await apiService.createMoneyAccount(payload);
      await loadAccounts();

      const successMessage = editingAccount ? 'Денежный счет обновлен.' : 'Денежный счет добавлен.';
      setMessage(successMessage);
      toast.success(successMessage, 'Денежные счета');
      resetEditor();
    } catch (saveError) {
      const nextError = extractDisplayErrorMessage(saveError, 'Не удалось сохранить денежный счет.');
      setError(nextError);
      toast.error(nextError, 'Денежные счета');
    } finally {
      setSaving(false);
    }
  };

  const deleteAccount = async (account: MoneyAccount) => {
    const approved = await confirm({
      title: 'Удалить денежный счет',
      description: `Удалить «${account.name}»? Если по счету уже есть операции, он будет отключен и останется в истории.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    try {
      await apiService.deleteMoneyAccount(account.id);
      await loadAccounts();
      setMessage('Денежный счет удален или отключен.');
      toast.success('Денежный счет удален или отключен.', 'Денежные счета');
    } catch (deleteError) {
      const nextError = extractDisplayErrorMessage(deleteError, 'Не удалось удалить денежный счет.');
      setError(nextError);
      toast.error(nextError, 'Денежные счета');
    }
  };

  return (
    <div className="rounded-3xl border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h4 className="text-xl font-semibold text-gray-900">Денежные счета</h4>
          <p className="mt-1 max-w-3xl text-sm text-gray-600">
            Банковские счета, кассы, карты и e-money кошельки используются в журналах денег, фильтрах и формах операций.
          </p>
        </div>

        <button
          type="button"
          onClick={() => openCreate(sourceFilter === 'all' ? 'Bank' : sourceFilter)}
          className="inline-flex items-center justify-center gap-2 rounded-xl bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-blue-700">
          <Plus className="h-4 w-4" />
          Добавить счет
        </button>
      </div>

      <div className="mt-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {groupedTotals.map(({ source, active, total }) => {
          const Icon = SOURCE_ICON[source];
          return (
            <button
              key={source}
              type="button"
              onClick={() => setSourceFilter(source)}
              className={`rounded-2xl border p-4 text-left transition ${
                sourceFilter === source
                  ? 'border-blue-300 bg-blue-50'
                  : 'border-gray-200 bg-white hover:border-blue-200 hover:bg-blue-50/50'
              }`}>
              <div className="flex items-center gap-3">
                <span className="rounded-xl bg-blue-100 p-2 text-blue-700">
                  <Icon className="h-5 w-5" />
                </span>
                <div>
                  <div className="font-semibold text-gray-900">{SOURCE_LABELS[source]}</div>
                  <div className="text-xs text-gray-500">{SOURCE_HINTS[source]}</div>
                </div>
              </div>
              <div className="mt-3 text-sm text-gray-600">
                Активных: <span className="font-semibold text-gray-900">{active}</span>
                <span className="mx-2 text-gray-300">/</span>
                Всего: <span className="font-semibold text-gray-900">{total}</span>
              </div>
            </button>
          );
        })}
      </div>

      <div className="mt-5 flex flex-wrap items-center justify-between gap-3">
        <select
          value={sourceFilter}
          onChange={(event) => setSourceFilter(event.target.value as 'all' | MoneyOperationSource)}
          className="rounded-xl border border-gray-200 px-3 py-2 text-sm text-gray-700 focus:border-blue-500 focus:outline-none">
          <option value="all">Все источники</option>
          <option value="Bank">Банк</option>
          <option value="Cash">Касса</option>
          <option value="Card">Карты</option>
          <option value="EMoney">E-money</option>
        </select>

        <button
          type="button"
          onClick={() => void loadAccounts()}
          className="rounded-xl border border-gray-200 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50">
          Обновить
        </button>
      </div>

      {error && <div className="mt-4 rounded-xl border border-red-100 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>}
      {message && <div className="mt-4 rounded-xl border border-emerald-100 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">{message}</div>}

      <div className="mt-5 overflow-hidden rounded-2xl border border-gray-200">
        <table className="w-full min-w-[860px] text-sm">
          <thead className="bg-gray-50 text-xs uppercase tracking-wide text-gray-500">
            <tr>
              <th className="px-4 py-3 text-left font-semibold">Название</th>
              <th className="px-4 py-3 text-left font-semibold">Источник</th>
              <th className="px-4 py-3 text-left font-semibold">Реквизиты</th>
              <th className="px-4 py-3 text-left font-semibold">Статус</th>
              <th className="px-4 py-3 text-right font-semibold">Действия</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {loading ? (
              <tr>
                <td colSpan={5} className="px-4 py-8 text-center text-gray-500">
                  Загружаем денежные счета...
                </td>
              </tr>
            ) : filteredAccounts.length === 0 ? (
              <tr>
                <td colSpan={5} className="px-4 py-8 text-center text-gray-500">
                  Денежные счета не найдены.
                </td>
              </tr>
            ) : (
              filteredAccounts.map((account) => (
                <tr key={account.id} className={!account.isActive ? 'bg-gray-50 text-gray-400' : 'bg-white'}>
                  <td className="px-4 py-3">
                    <div className="font-semibold text-gray-900">{account.name}</div>
                    {account.isDefault && (
                      <span className="mt-1 inline-flex rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
                        По умолчанию
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3 text-gray-700">{SOURCE_LABELS[account.source]}</td>
                  <td className="px-4 py-3 text-gray-600">{formatAccountMeta(account)}</td>
                  <td className="px-4 py-3">
                    <span
                      className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${
                        account.isActive ? 'bg-emerald-50 text-emerald-700' : 'bg-gray-100 text-gray-500'
                      }`}>
                      {account.isActive ? 'Активен' : 'Отключен'}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex justify-end gap-2">
                      <button
                        type="button"
                        onClick={() => openEdit(account)}
                        className="rounded-lg border border-gray-200 p-2 text-blue-600 hover:bg-blue-50"
                        aria-label={`Редактировать ${account.name}`}
                        title="Редактировать">
                        <Pencil className="h-4 w-4" />
                      </button>
                      <button
                        type="button"
                        onClick={() => void deleteAccount(account)}
                        className="rounded-lg border border-red-100 p-2 text-red-600 hover:bg-red-50"
                        aria-label={`Удалить или отключить ${account.name}`}
                        title="Удалить или отключить">
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

      {editorOpen &&
        createPortal(
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-4">
            <div className="w-full max-w-2xl overflow-hidden rounded-2xl bg-white shadow-xl">
              <div className="flex items-start justify-between border-b border-gray-200 px-6 py-4">
                <div>
                  <h3 className="text-lg font-semibold text-gray-900">
                    {editingAccount ? 'Редактировать денежный счет' : 'Добавить денежный счет'}
                  </h3>
                  <p className="mt-1 text-sm text-gray-500">Источник определяет, в какой журнал попадут операции.</p>
                </div>
                <button type="button" onClick={closeEditor} className="rounded-lg p-2 text-gray-400 hover:bg-gray-50 hover:text-gray-600">
                  <X className="h-5 w-5" />
                </button>
              </div>

              <div className="grid gap-4 px-6 py-5 sm:grid-cols-2">
                <label className="flex flex-col gap-1 text-sm font-medium text-gray-700">
                  Название
                  <input
                    value={form.name}
                    onChange={(event) => setForm((prev) => ({ ...prev, name: event.target.value }))}
                    className="rounded-xl border border-gray-200 px-3 py-2 font-normal focus:border-blue-500 focus:outline-none"
                    placeholder="Основной банк"
                  />
                </label>

                <label className="flex flex-col gap-1 text-sm font-medium text-gray-700">
                  Источник
                  <select
                    value={form.source}
                    onChange={(event) => setForm((prev) => ({ ...prev, source: event.target.value as MoneyOperationSource }))}
                    className="rounded-xl border border-gray-200 px-3 py-2 font-normal focus:border-blue-500 focus:outline-none">
                    <option value="Bank">Банк</option>
                    <option value="Cash">Касса</option>
                    <option value="Card">Карты</option>
                    <option value="EMoney">E-money</option>
                  </select>
                </label>

                <label className="flex flex-col gap-1 text-sm font-medium text-gray-700">
                  Валюта
                  <input
                    value={form.currency}
                    onChange={(event) => setForm((prev) => ({ ...prev, currency: event.target.value }))}
                    className="rounded-xl border border-gray-200 px-3 py-2 font-normal uppercase focus:border-blue-500 focus:outline-none"
                    placeholder="RUB"
                  />
                </label>

                <label className="flex flex-col gap-1 text-sm font-medium text-gray-700">
                  Банк или площадка
                  <input
                    value={form.bankName}
                    onChange={(event) => setForm((prev) => ({ ...prev, bankName: event.target.value }))}
                    className="rounded-xl border border-gray-200 px-3 py-2 font-normal focus:border-blue-500 focus:outline-none"
                    placeholder="Точка, Сбер, офисная касса"
                  />
                </label>

                <label className="flex flex-col gap-1 text-sm font-medium text-gray-700 sm:col-span-2">
                  Номер счета, карты или кошелька
                  <input
                    value={form.bankAccountNumber}
                    onChange={(event) => setForm((prev) => ({ ...prev, bankAccountNumber: event.target.value }))}
                    className="rounded-xl border border-gray-200 px-3 py-2 font-normal focus:border-blue-500 focus:outline-none"
                    placeholder="4070..., последние цифры карты или ID кошелька"
                  />
                </label>

                <label className="flex items-center gap-2 rounded-xl border border-gray-200 px-3 py-2 text-sm text-gray-700">
                  <input
                    type="checkbox"
                    checked={form.isDefault}
                    onChange={(event) => setForm((prev) => ({ ...prev, isDefault: event.target.checked }))}
                  />
                  Счет по умолчанию
                </label>

                <label className="flex items-center gap-2 rounded-xl border border-gray-200 px-3 py-2 text-sm text-gray-700">
                  <input
                    type="checkbox"
                    checked={form.isActive}
                    onChange={(event) => setForm((prev) => ({ ...prev, isActive: event.target.checked }))}
                  />
                  Активен
                </label>
              </div>

              <div className="flex justify-end gap-3 border-t border-gray-200 px-6 py-4">
                <button
                  type="button"
                  onClick={closeEditor}
                  className="rounded-xl border border-gray-200 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50">
                  Отмена
                </button>
                <button
                  type="button"
                  onClick={() => void saveAccount()}
                  disabled={saving}
                  className="inline-flex items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60">
                  <Save className="h-4 w-4" />
                  {saving ? 'Сохраняем...' : 'Сохранить'}
                </button>
              </div>
            </div>
          </div>,
          document.body,
        )}
    </div>
  );
}
