import React, { useCallback, useEffect, useState } from 'react';
import { Plus, Pencil, Trash2, Check, X } from 'lucide-react';
import { apiService, type DictKind } from '../../services/api';
import type { DealType, IncomeType, PaymentSource } from '../../types';

function sortRows<T extends RowState>(rows: T[]): T[] {
  return [...rows].sort((a, b) => {
    if (a.item.isActive !== b.item.isActive) return a.item.isActive ? -1 : 1;
    return a.item.name.localeCompare(b.item.name, 'ru', { sensitivity: 'base' });
  });
}

const emitDictsChanged = () => {
  window.dispatchEvent(new CustomEvent('dicts:changed'));
};

function Btn(
  props: React.ButtonHTMLAttributes<HTMLButtonElement> & {
    variant?: 'primary' | 'ghost' | 'secondary';
  },
) {
  const { variant = 'primary', className = '', ...rest } = props;
  const base = 'inline-flex items-center gap-2 rounded-xl px-3 py-2 text-sm transition';
  const variants = {
    primary: 'bg-emerald-600 text-white hover:bg-emerald-700',
    ghost: 'hover:bg-gray-100',
    secondary: 'bg-gray-200 text-gray-900 hover:bg-gray-300',
  } as const;
  return <button {...rest} className={`${base} ${variants[variant]} ${className}`} />;
}

function TextInput(
  props: React.InputHTMLAttributes<HTMLInputElement> & { label?: string; id?: string },
) {
  const { label, id, className = '', ...rest } = props;
  return (
    <div className="flex flex-col gap-1">
      {label ? (
        <label htmlFor={id} className="text-xs text-gray-600">
          {label}
        </label>
      ) : null}
      <input
        id={id}
        {...rest}
        className={`w-full rounded-lg border border-gray-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-500 ${className}`}
      />
    </div>
  );
}

function TextArea(
  props: React.TextareaHTMLAttributes<HTMLTextAreaElement> & { label?: string; id?: string },
) {
  const { label, id, className = '', ...rest } = props;
  return (
    <div className="flex flex-col gap-1">
      {label ? (
        <label htmlFor={id} className="text-xs text-gray-600">
          {label}
        </label>
      ) : null}
      <textarea
        id={id}
        {...rest}
        className={`w-full rounded-lg border border-gray-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-500 ${className}`}
      />
    </div>
  );
}

function Checkbox(props: {
  checked: boolean;
  onChange: (v: boolean) => void;
  id?: string;
  label?: string;
}) {
  const { checked, onChange, id, label } = props;
  return (
    <label htmlFor={id} className="inline-flex select-none items-center gap-2">
      <input
        id={id}
        type="checkbox"
        className="h-4 w-4 rounded border-gray-300 text-emerald-600 focus:ring-emerald-500"
        checked={checked}
        onChange={(e) => onChange(e.currentTarget.checked)}
      />
      {label ? <span className="text-sm text-gray-700">{label}</span> : null}
    </label>
  );
}

function Tabs<T extends string>({
  tabs,
  value,
  onChange,
}: {
  tabs: { value: T; label: string }[];
  value: T;
  onChange: (v: T) => void;
}) {
  return (
    <div className="grid grid-cols-2 sm:grid-cols-4 rounded-xl bg-gray-100 p-1">
      {tabs.map((t) => (
        <button
          key={t.value}
          onClick={() => onChange(t.value)}
          className={`rounded-lg px-3 py-2 text-sm ${
            value === t.value ? 'bg-white shadow' : 'text-gray-600 hover:bg-white/60'
          }`}>
          {t.label}
        </button>
      ))}
    </div>
  );
}

function Modal(props: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  footer?: React.ReactNode;
}) {
  const { open, title, onClose, children, footer } = props;
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} />
      <div className="absolute left-1/2 top-1/2 w-[min(900px,92vw)] -translate-x-1/2 -translate-y-1/2 rounded-2xl bg-white shadow-2xl">
        <div className="flex items-center justify-between border-b px-5 py-4">
          <div className="text-lg font-semibold">{title}</div>
          <button onClick={onClose} className="rounded-lg p-2 hover:bg-gray-100">
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="max-h-[72vh] overflow-auto px-5 py-4">{children}</div>
        <div className="flex items-center justify-end gap-2 border-t px-5 py-3">{footer}</div>
      </div>
    </div>
  );
}

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

export interface DictionariesModalProps {
  open: boolean;
  onClose: () => void;
}

export default function DictionariesModal({ open, onClose }: DictionariesModalProps) {
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
    if (!open) return;
    (async () => {
      await Promise.all(TABS.map((t) => loadKind(t.value)));
    })();
  }, [open, loadKind]);

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
      <div className="rounded-xl border bg-gray-50 p-3">
        <div className="grid grid-cols-1 items-start gap-3 sm:grid-cols-12">
          <div className="sm:col-span-5">
            <TextInput
              label="Название"
              placeholder="Например: Консультация"
              value={local.name}
              onChange={(e) => set({ name: e.target.value })}
            />
          </div>

          <div className="sm:col-span-3">
            <TextInput
              label="Цвет"
              placeholder="#3B82F6"
              value={local.colorHex}
              onChange={(e) => set({ colorHex: e.target.value })}
            />
            <div className="mt-2 flex items-center gap-2">
              <input
                type="color"
                value={isValidHexColor(local.colorHex) ? local.colorHex : defaultColor(kind)}
                onChange={(e) => set({ colorHex: e.target.value })}
                className="h-8 w-12 cursor-pointer rounded border"
                aria-label="color picker"
              />
              <span
                className="inline-block h-4 w-4 rounded-md border border-gray-300"
                style={{ backgroundColor: local.colorHex }}
              />
            </div>
          </div>

          <div className="sm:col-span-2 flex items-center sm:pt-6">
            <Checkbox
              id={`active-new-${idx}`}
              checked={!!local.isActive}
              onChange={(v) => set({ isActive: v })}
              label="Активно"
            />
          </div>

          <div className="sm:col-span-2 flex h-full items-end justify-end gap-2">
            <Btn variant="ghost" onClick={() => removeRow(kind, idx)}>
              <X className="h-4 w-4" />
              Сброс
            </Btn>
            <Btn onClick={() => saveNew(kind, idx, local)}>
              <Check className="h-4 w-4" />
              Сохранить
            </Btn>
          </div>

          <div className="sm:col-span-12">
            <TextArea
              label="Описание"
              placeholder="Короткое пояснение..."
              rows={2}
              value={local.description}
              onChange={(e) => set({ description: e.target.value })}
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
      <div className="rounded-xl border bg-gray-50 p-3">
        <div className="grid grid-cols-1 items-start gap-3 sm:grid-cols-12">
          <div className="sm:col-span-5">
            <TextInput
              label="Название"
              value={local.name}
              onChange={(e) => set({ name: e.target.value })}
            />
          </div>

          <div className="sm:col-span-3">
            <TextInput
              label="Цвет"
              value={local.colorHex}
              onChange={(e) => set({ colorHex: e.target.value })}
            />
            <div className="mt-2 flex items-center gap-2">
              <input
                type="color"
                value={isValidHexColor(local.colorHex) ? local.colorHex : defaultColor(kind)}
                onChange={(e) => set({ colorHex: e.target.value })}
                className="h-8 w-12 cursor-pointer rounded border"
              />
              <span
                className="inline-block h-4 w-4 rounded-md border border-gray-300"
                style={{ backgroundColor: local.colorHex }}
              />
            </div>
          </div>

          <div className="sm:col-span-2 flex items-center sm:pt-6">
            <Checkbox
              id={`active-edit-${item.id}`}
              checked={!!local.isActive}
              onChange={(v) => set({ isActive: v })}
              label="Активно"
            />
          </div>

          <div className="sm:col-span-2 flex h-full items-end justify-end gap-2">
            <Btn
              variant="ghost"
              onClick={() => setRow(kind, idx, (prev) => ({ ...prev, mode: 'view' }))}>
              <X className="h-4 w-4" />
              Сброс
            </Btn>
            <Btn onClick={() => saveEdit(kind, idx, local)}>
              <Check className="h-4 w-4" />
              Сохранить
            </Btn>
          </div>

          <div className="sm:col-span-12">
            <TextArea
              label="Описание"
              rows={2}
              value={local.description}
              onChange={(e) => set({ description: e.target.value })}
            />
          </div>

          {local.createdAt && (
            <div className="sm:col-span-12 text-xs text-gray-500">
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
        className={`grid grid-cols-12 items-center gap-3 rounded-xl border p-3 ${
          item.isActive ? '' : 'bg-gray-100 text-gray-400'
        }`}>
        <div className="col-span-8">
          <div className="flex items-center gap-2">
            <span
              className={`inline-block h-3 w-3 rounded ${item.isActive ? '' : 'opacity-40'}`}
              style={{ backgroundColor: item.colorHex || defaultColor(kind) }}
            />
            <div className={`text-sm font-medium ${item.isActive ? '' : 'text-gray-400'}`}>
              {item.name}
            </div>
          </div>
          {item.description.trim() && (
            <div className={`mt-1 text-xs ${item.isActive ? 'text-gray-600' : 'text-gray-400'}`}>
              {item.description}
            </div>
          )}
          {!item.isActive && <div className="text-xs text-gray-500">Не активно</div>}
          {item.createdAt && (
            <div className="mt-1 text-xs text-gray-400">
              Создано: {new Date(item.createdAt).toLocaleString()}
            </div>
          )}
        </div>
        <div className="col-span-2" />
        <div className="col-span-2 flex justify-end gap-2">
          <Btn variant="ghost" onClick={onEdit}>
            <Pencil className="h-4 w-4" />
          </Btn>
          <Btn variant="ghost" onClick={onDelete}>
            <Trash2 className="h-4 w-4" />
          </Btn>
        </div>
      </div>
    );
  }

  return (
    <>
      <Modal
        open={open}
        onClose={onClose}
        title="Справочники"
        footer={
          <Btn variant="secondary" onClick={onClose}>
            Закрыть
          </Btn>
        }>
        <div className="mb-4">
          <Tabs tabs={TABS} value={activeTab} onChange={(v) => setActiveTab(v)} />
        </div>

        {error && (
          <div className="mb-3 rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </div>
        )}

        <div className={busy ? 'pointer-events-none opacity-70' : ''}>
          <div className="mb-3 flex items-center justify-between">
            <div className="text-sm text-gray-600">Всего: {data[activeTab]?.length ?? 0}</div>
            <Btn onClick={() => addNew(activeTab)}>
              <Plus className="h-4 w-4" />
              Добавить
            </Btn>
          </div>

          <div className="space-y-2">
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
              // new
              return <NewRow key={key} idx={idx} kind={activeTab} item={row.item} />;
            })}
          </div>
        </div>
      </Modal>

      {confirmDelete && (
        <Modal open={true} onClose={() => setConfirmDelete(null)} title="Удалить элемент?">
          <div className="space-y-4">
            <p className="text-sm text-gray-700">Действие необратимо. Удалить элемент?</p>
            <div className="flex justify-end gap-2">
              <Btn variant="secondary" onClick={() => setConfirmDelete(null)}>
                Отмена
              </Btn>
              <Btn onClick={doDelete}>Удалить</Btn>
            </div>
          </div>
        </Modal>
      )}
    </>
  );
}
