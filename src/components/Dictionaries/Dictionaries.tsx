import React, { useCallback, useEffect, useState } from 'react';
import { Plus, Pencil, Trash2, Check, X, ShieldAlert } from 'lucide-react';
import { apiService, type DictKind } from '../../services/api';
import type { DealType, IncomeType, PaymentSource } from '../../types';
import { useAuth } from '../../contexts/AuthContext';

function sortRows<T extends RowState>(rows: T[]): T[] {
  return [...rows].sort((a, b) => {
    if (a.item.isActive !== b.item.isActive) return a.item.isActive ? -1 : 1;
    return a.item.name.localeCompare(b.item.name, 'ru', { sensitivity: 'base' });
  });
}

const emitDictsChanged = () => {
  window.dispatchEvent(new CustomEvent('dicts:changed'));
};

type VisibleKind = 'deal-types' | 'income-income' | 'income-expense' | 'payment-sources';
type ApiVisibleItem = DealType | IncomeType | PaymentSource;

type FullDictItem = {
  id: number;
  name: string;
  description: string;
  colorHex: string;
  isActive: boolean;
  createdAt?: string;
};

type RowKey = string;

type RowState =
  | { _key: RowKey; mode: 'view'; item: FullDictItem }
  | { _key: RowKey; mode: 'edit'; item: FullDictItem }
  | { _key: RowKey; mode: 'new'; item: Omit<FullDictItem, 'id' | 'createdAt'> };

const makeKey = (): RowKey => `${Date.now()}-${Math.random().toString(36).slice(2)}`;

const TABS: { value: VisibleKind; label: string }[] = [
  { value: 'deal-types', label: 'Тип сделки' },
  { value: 'income-income', label: 'Тип дохода' },
  { value: 'income-expense', label: 'Тип расхода' },
  { value: 'payment-sources', label: 'Источник платежа' },
];

function defaultColor(kind: VisibleKind): string {
  switch (kind) {
    case 'deal-types':
      return '#3B82F6';
    case 'income-income':
      return '#10B981';
    case 'income-expense':
      return '#EF4444';
    case 'payment-sources':
      return '#8B5CF6';
  }
}

const isValidHexColor = (v: string) => /^#[0-9A-Fa-f]{6}$/.test(v);

function normalize(kind: VisibleKind, x: ApiVisibleItem): FullDictItem {
  return {
    id: x.id,
    name: x.name,
    description: x.description ?? '',
    colorHex: x.colorHex ?? defaultColor(kind),
    isActive: !!x.isActive,
    createdAt: x.createdAt,
  };
}

export const Dictionaries = () => {
  const { isAdmin } = useAuth();
  const [activeTab, setActiveTab] = useState<VisibleKind>('deal-types');
  const [data, setData] = useState<Record<VisibleKind, RowState[]>>({
    'deal-types': [],
    'income-income': [],
    'income-expense': [],
    'payment-sources': [],
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<{ kind: VisibleKind; id: number } | null>(
    null,
  );

  const loadKind = useCallback(async (kind: VisibleKind) => {
    setBusy(true);
    setError(null);
    try {
      let list: ApiVisibleItem[] = [];
      if (kind === 'income-income') {
        list = await apiService.getIncomeTypes('Income');
      } else if (kind === 'income-expense') {
        list = await apiService.getIncomeTypes('Expense');
      } else {
        list = await apiService.getDict(dictKindFor(kind));
      }
      const normalized = (list as ApiVisibleItem[]).map((x) => normalize(kind, x));
      setData((prev) => ({
        ...prev,
        [kind]: sortRows(
          normalized.map((i) => ({ _key: makeKey(), mode: 'view' as const, item: i })),
        ),
      }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => {
    (async () => {
      await Promise.all(TABS.map((t) => loadKind(t.value)));
    })();
  }, [loadKind]);

  const addNew = (kind: VisibleKind) => {
    setData((prev) => ({
      ...prev,
      [kind]: [
        {
          _key: makeKey(),
          mode: 'new' as const,
          item: { name: '', description: '', colorHex: defaultColor(kind), isActive: true },
        },
        ...prev[kind],
      ],
    }));
  };

  const setRow = (
    kind: VisibleKind,
    idx: number,
    build: (prevRow: RowState) => Omit<RowState, '_key'> & Partial<Pick<RowState, '_key'>>,
  ) => {
    setData((prev) => {
      const copy = [...prev[kind]];
      const prevRow = copy[idx];
      const next = build(prevRow);
      const withKey = { ...next, _key: prevRow._key } as RowState;
      copy[idx] = withKey;
      return { ...prev, [kind]: copy };
    });
  };

  const removeRow = (kind: VisibleKind, idx: number) => {
    setData((prev) => {
      const copy = [...prev[kind]];
      copy.splice(idx, 1);
      return { ...prev, [kind]: copy };
    });
  };

  function isIncomeKind(k: VisibleKind) {
    return k === 'income-income' || k === 'income-expense';
  }
  function paymentTypeByKind(k: VisibleKind): 'Income' | 'Expense' {
    return k === 'income-expense' ? 'Expense' : 'Income';
  }
  function dictKindFor(k: VisibleKind): DictKind {
    if (k === 'income-income' || k === 'income-expense') return 'income-types';
    return k;
  }

  async function saveNew(
    kind: VisibleKind,
    idx: number,
    item: Omit<FullDictItem, 'id' | 'createdAt'>,
  ) {
    if (!item.name.trim()) return setError('Название не должно быть пустым');
    if (item.colorHex && !isValidHexColor(item.colorHex))
      return setError('Цвет должен быть в формате #RRGGBB');

    setBusy(true);
    setError(null);
    try {
      const created = isIncomeKind(kind)
        ? await apiService.createIncomeType({
            name: item.name,
            isActive: item.isActive,
            description: item.description,
            colorHex: item.colorHex,
            paymentType: paymentTypeByKind(kind),
          })
        : await apiService.createDict(dictKindFor(kind), {
            name: item.name,
            isActive: item.isActive,
            description: item.description,
            colorHex: item.colorHex,
          });
      const normalized = normalize(kind, created as ApiVisibleItem);
      setData((prev) => {
        const copy = [...prev[kind]];
        copy[idx] = { _key: copy[idx]._key, mode: 'view', item: normalized };
        return { ...prev, [kind]: sortRows(copy) };
      });
      emitDictsChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function saveEdit(kind: VisibleKind, idx: number, item: FullDictItem) {
    if (!item.name.trim()) return setError('Название не должно быть пустым');
    if (item.colorHex && !isValidHexColor(item.colorHex))
      return setError('Цвет должен быть в формате #RRGGBB');

    setBusy(true);
    setError(null);
    try {
      const updated = isIncomeKind(kind)
        ? await apiService.updateIncomeType(item.id, {
            name: item.name,
            isActive: item.isActive,
            description: item.description,
            colorHex: item.colorHex,
            paymentType: paymentTypeByKind(kind),
          })
        : await apiService.updateDict(dictKindFor(kind), item.id, {
            name: item.name,
            isActive: item.isActive,
            description: item.description,
            colorHex: item.colorHex,
          });
      const normalized = normalize(kind, updated as ApiVisibleItem);
      setData((prev) => {
        const copy = [...prev[kind]];
        copy[idx] = { _key: copy[idx]._key, mode: 'view', item: normalized };
        return { ...prev, [kind]: sortRows(copy) };
      });
      emitDictsChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function doDelete() {
    if (!confirmDelete) return;
    setBusy(true);
    setError(null);
    try {
      await apiService.deleteDict(dictKindFor(confirmDelete.kind), confirmDelete.id);
      setData((prev) => ({
        ...prev,
        [confirmDelete.kind]: prev[confirmDelete.kind].filter(
          (r) => !(r.mode !== 'new' && r.item.id === confirmDelete.id),
        ),
      }));
      emitDictsChanged();
      setConfirmDelete(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  function NewRow({
    idx,
    kind,
    item,
  }: {
    idx: number;
    kind: VisibleKind;
    item: Omit<FullDictItem, 'id' | 'createdAt'>;
  }) {
    const [local, setLocal] = React.useState(item);
    useEffect(() => setLocal(item), [item]);
    const set = (patch: Partial<typeof local>) => setLocal((p) => ({ ...p, ...patch }));
    return (
      <div className="rounded-xl border bg-slate-50 p-4">
        <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-12">
          <div className="lg:col-span-4">
            <label className="block text-xs font-medium text-slate-700 mb-1.5">
              Название <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              placeholder="Например: Консультация"
              value={local.name}
              onChange={(e) => set({ name: e.target.value })}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-slate-900"
            />
          </div>

          <div className="lg:col-span-3">
            <label className="block text-xs font-medium text-slate-700 mb-1.5">Цвет</label>
            <div className="flex items-center gap-2">
              <input
                type="text"
                placeholder="#3B82F6"
                value={local.colorHex}
                onChange={(e) => set({ colorHex: e.target.value })}
                className="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-slate-900"
              />
              <input
                type="color"
                value={isValidHexColor(local.colorHex) ? local.colorHex : defaultColor(kind)}
                onChange={(e) => set({ colorHex: e.target.value })}
                className="h-10 w-12 cursor-pointer rounded-lg border border-slate-300"
                aria-label="color picker"
              />
            </div>
          </div>

          <div className="lg:col-span-2 flex items-center lg:pt-7">
            <label className="inline-flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={!!local.isActive}
                onChange={(e) => set({ isActive: e.target.checked })}
                className="h-4 w-4 rounded border-slate-300 text-slate-900 focus:ring-slate-900"
              />
              <span className="text-sm text-slate-700">Активно</span>
            </label>
          </div>

          <div className="lg:col-span-3 flex h-full items-end justify-end gap-2">
            <button
              onClick={() => removeRow(kind, idx)}
              className="inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-200 transition">
              <X className="h-4 w-4" />
              Сброс
            </button>
            <button
              onClick={() => saveNew(kind, idx, local)}
              className="inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm bg-slate-900 text-white hover:bg-slate-800 transition">
              <Check className="h-4 w-4" />
              Сохранить
            </button>
          </div>

          <div className="lg:col-span-12">
            <label className="block text-xs font-medium text-slate-700 mb-1.5">Описание</label>
            <textarea
              placeholder="Короткое пояснение..."
              rows={2}
              value={local.description}
              onChange={(e) => set({ description: e.target.value })}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-slate-900 resize-none"
            />
          </div>
        </div>
      </div>
    );
  }

  function EditRow({ idx, kind, item }: { idx: number; kind: VisibleKind; item: FullDictItem }) {
    const [local, setLocal] = React.useState(item);
    useEffect(() => setLocal(item), [item]);

    const set = (patch: Partial<FullDictItem>) => setLocal((p) => ({ ...p, ...patch }));

    return (
      <div className="rounded-xl border bg-slate-50 p-4">
        <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-12">
          <div className="lg:col-span-4">
            <label className="block text-xs font-medium text-slate-700 mb-1.5">
              Название <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={local.name}
              onChange={(e) => set({ name: e.target.value })}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-slate-900"
            />
          </div>

          <div className="lg:col-span-3">
            <label className="block text-xs font-medium text-slate-700 mb-1.5">Цвет</label>
            <div className="flex items-center gap-2">
              <input
                type="text"
                value={local.colorHex}
                onChange={(e) => set({ colorHex: e.target.value })}
                className="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-slate-900"
              />
              <input
                type="color"
                value={isValidHexColor(local.colorHex) ? local.colorHex : defaultColor(kind)}
                onChange={(e) => set({ colorHex: e.target.value })}
                className="h-10 w-12 cursor-pointer rounded-lg border border-slate-300"
              />
            </div>
          </div>

          <div className="lg:col-span-2 flex items-center lg:pt-7">
            <label className="inline-flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={!!local.isActive}
                onChange={(e) => set({ isActive: e.target.checked })}
                className="h-4 w-4 rounded border-slate-300 text-slate-900 focus:ring-slate-900"
              />
              <span className="text-sm text-slate-700">Активно</span>
            </label>
          </div>

          <div className="lg:col-span-3 flex h-full items-end justify-end gap-2">
            <button
              onClick={() => setRow(kind, idx, (prev) => ({ ...prev, mode: 'view' }))}
              className="inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-200 transition">
              <X className="h-4 w-4" />
              Сброс
            </button>
            <button
              onClick={() => saveEdit(kind, idx, local)}
              className="inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm bg-slate-900 text-white hover:bg-slate-800 transition">
              <Check className="h-4 w-4" />
              Сохранить
            </button>
          </div>

          <div className="lg:col-span-12">
            <label className="block text-xs font-medium text-slate-700 mb-1.5">Описание</label>
            <textarea
              rows={2}
              value={local.description}
              onChange={(e) => set({ description: e.target.value })}
              className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-slate-900 resize-none"
            />
          </div>

          {local.createdAt && (
            <div className="lg:col-span-12 text-xs text-slate-500">
              Создано: {new Date(local.createdAt).toLocaleString()}
            </div>
          )}
        </div>
      </div>
    );
  }

  function ViewRow({
    item,
    onEdit,
    onDelete,
    kind,
  }: {
    item: FullDictItem;
    onEdit: () => void;
    onDelete: () => void;
    kind: VisibleKind;
  }) {
    return (
      <div
        className={`grid grid-cols-12 items-center gap-4 rounded-xl border p-4 transition ${
          item.isActive ? 'bg-white hover:shadow-sm' : 'bg-slate-100'
        }`}>
        <div className="col-span-8 lg:col-span-9">
          <div className="flex items-center gap-3">
            <span
              className={`inline-block h-3 w-3 rounded-full ${item.isActive ? '' : 'opacity-40'}`}
              style={{ backgroundColor: item.colorHex || defaultColor(kind) }}
            />
            <div className={`text-sm font-medium ${item.isActive ? 'text-slate-900' : 'text-slate-400'}`}>
              {item.name}
            </div>
            {!item.isActive && (
              <span className="text-xs text-slate-500 bg-slate-200 px-2 py-0.5 rounded">
                Не активно
              </span>
            )}
          </div>
          {item.description.trim() && (
            <div className={`mt-1.5 text-xs ${item.isActive ? 'text-slate-600' : 'text-slate-400'}`}>
              {item.description}
            </div>
          )}
          {item.createdAt && (
            <div className="mt-1 text-xs text-slate-400">
              Создано: {new Date(item.createdAt).toLocaleDateString()}
            </div>
          )}
        </div>
        <div className="col-span-4 lg:col-span-3 flex justify-end gap-2">
          <button
            onClick={onEdit}
            className="p-2 text-slate-600 hover:text-slate-900 hover:bg-slate-100 rounded-lg transition">
            <Pencil className="h-4 w-4" />
          </button>
          <button
            onClick={onDelete}
            className="p-2 text-red-600 hover:text-red-700 hover:bg-red-50 rounded-lg transition">
            <Trash2 className="h-4 w-4" />
          </button>
        </div>
      </div>
    );
  }

  if (!isAdmin()) {
    return (
      <div className="p-8">
        <div className="max-w-7xl mx-auto">
          <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-center">
            <ShieldAlert className="w-12 h-12 text-red-600 mx-auto mb-4" />
            <h2 className="text-xl font-bold text-red-900 mb-2">Доступ запрещён</h2>
            <p className="text-red-700">
              Нужны права администратора для доступа к управлению справочниками.
            </p>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="p-8">
      <div className="max-w-7xl mx-auto">
        <div className="mb-8">
          <h1 className="text-3xl font-bold text-slate-900">Справочники</h1>
          <p className="text-slate-600 mt-2">Управляйте справочниками системы</p>
        </div>

        <div className="mb-6">
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-2 bg-slate-100 p-1.5 rounded-xl">
            {TABS.map((t) => (
              <button
                key={t.value}
                onClick={() => setActiveTab(t.value)}
                className={`rounded-lg px-4 py-2.5 text-sm font-medium transition ${
                  activeTab === t.value
                    ? 'bg-white text-slate-900 shadow'
                    : 'text-slate-600 hover:bg-white/60'
                }`}>
                {t.label}
              </button>
            ))}
          </div>
        </div>

        {error && (
          <div className="mb-6 p-4 bg-red-50 border border-red-200 rounded-lg text-red-700">
            {error}
          </div>
        )}

        <div className={busy ? 'pointer-events-none opacity-70' : ''}>
          <div className="mb-4 flex items-center justify-between">
            <div className="text-sm text-slate-600">Всего: {data[activeTab]?.length ?? 0}</div>
            <button
              onClick={() => addNew(activeTab)}
              className="inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm bg-slate-900 text-white hover:bg-slate-800 transition">
              <Plus className="h-4 w-4" />
              Добавить
            </button>
          </div>

          <div className="space-y-3">
            {(data[activeTab] ?? []).map((row, idx) => {
              const key = row._key;
              if (row.mode === 'view') {
                return (
                  <ViewRow
                    key={key}
                    item={row.item}
                    kind={activeTab}
                    onEdit={() => setRow(activeTab, idx, (prev) => ({ ...prev, mode: 'edit' }))}
                    onDelete={() => setConfirmDelete({ kind: activeTab, id: row.item.id })}
                  />
                );
              }
              if (row.mode === 'edit') {
                return <EditRow key={key} idx={idx} kind={activeTab} item={row.item} />;
              }
              return <NewRow key={key} idx={idx} kind={activeTab} item={row.item} />;
            })}
          </div>
        </div>

        {confirmDelete && (
          <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
            <div className="bg-white rounded-xl shadow-xl max-w-md w-full mx-4">
              <div className="p-6">
                <h2 className="text-xl font-bold text-slate-900 mb-4">Удалить элемент?</h2>
                <p className="text-sm text-slate-600 mb-6">
                  Действие необратимо. Вы уверены, что хотите удалить этот элемент?
                </p>
                <div className="flex gap-3">
                  <button
                    onClick={() => setConfirmDelete(null)}
                    className="flex-1 px-4 py-2 border border-slate-300 text-slate-700 rounded-lg hover:bg-slate-50 transition">
                    Отмена
                  </button>
                  <button
                    onClick={doDelete}
                    className="flex-1 px-4 py-2 bg-red-600 text-white rounded-lg hover:bg-red-700 transition">
                    Удалить
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
};
