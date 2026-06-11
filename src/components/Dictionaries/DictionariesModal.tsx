import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Plus, Pencil, Trash2, Check, X, Search } from 'lucide-react';
import { apiService, type DictKind } from '../../services/api';
import type {
  ClientStatus,
  DocumentStatusEntity,
  DealType,
  IncomeType,
  PaymentSource,
  ServiceDictionaryItem,
  ServiceCategoryDictionaryItem,
} from '../../types';
import { isDocumentStatusCodeAllowedForKind } from '../../utils/documentStatusSettings';

function sortRows<T extends RowState>(rows: T[]): T[] {
  return [...rows].sort((a, b) => {
    if (a.item.isActive !== b.item.isActive) return a.item.isActive ? -1 : 1;
    if ((a.item.sortOrder ?? 0) !== (b.item.sortOrder ?? 0)) {
      return (a.item.sortOrder ?? 0) - (b.item.sortOrder ?? 0);
    }
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
  disabled?: boolean;
}) {
  const { checked, onChange, id, label, disabled = false } = props;
  return (
    <label htmlFor={id} className={`inline-flex select-none items-center gap-2 ${disabled ? 'opacity-50' : ''}`}>
      <input
        id={id}
        type="checkbox"
        className="h-4 w-4 rounded border-gray-300 text-emerald-600 focus:ring-emerald-500"
        checked={checked && !disabled}
        disabled={disabled}
        onChange={(e) => onChange(e.currentTarget.checked && !disabled)}
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
  if (tabs.length === 1) {
    return (
      <div className="flex justify-center">
        {tabs.map((t) => (
          <button
            key={t.value}
            onClick={() => onChange(t.value)}
            className={`w-full max-w-md rounded-lg px-3 py-2 text-sm ${
              value === t.value ? 'bg-white shadow' : 'text-gray-600 hover:bg-white/60'
            }`}>
            {t.label}
          </button>
        ))}
      </div>
    );
  }

  return (
    <div className={`grid rounded-xl bg-gray-100 p-1 ${tabs.length <= 2 ? 'grid-cols-2' : tabs.length <= 4 ? 'grid-cols-2 sm:grid-cols-4' : 'grid-cols-2 sm:grid-cols-3 lg:grid-cols-6'}`}>
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
          <button
            onClick={onClose}
            className="rounded-lg p-2 hover:bg-gray-100"
            aria-label="Закрыть окно справочников"
            title="Закрыть">
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="max-h-[72vh] overflow-auto px-5 py-4">{children}</div>
        <div className="flex items-center justify-end gap-2 border-t px-5 py-3">{footer}</div>
      </div>
    </div>
  );
}

function ConfirmModal(props: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: React.ReactNode;
}) {
  const { open, title, onClose, children } = props;
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-[60]">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} />
      <div className="absolute left-1/2 top-1/2 w-[min(520px,92vw)] -translate-x-1/2 -translate-y-1/2 rounded-2xl bg-white shadow-2xl">
        <div className="flex items-center justify-between border-b px-5 py-4">
          <div className="text-lg font-semibold">{title}</div>
          <button
            onClick={onClose}
            className="rounded-lg p-2 hover:bg-gray-100"
            aria-label="Закрыть подтверждение удаления"
            title="Закрыть">
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="px-5 py-4">{children}</div>
      </div>
    </div>
  );
}

type VisibleKind =
  | 'deal-types'
  | 'income-income'
  | 'income-expense'
  | 'payment-sources'
  | 'payment-statuses'
  | 'document-statuses'
  | 'services'
  | 'service-categories'
  | 'client-statuses'
  | 'countries'
  | 'base-currencies'
  | 'service-languages'
  | 'organization-forms'
  | 'timezones'
  | 'tax-systems'
  | 'tax-system-modes'
  | 'invoice-units'
  | 'invoice-item-names';
type LooseDictItem = {
  id: number;
  name: string;
  description?: string;
  colorHex?: string;
  isActive?: boolean;
  createdAt?: string;
  code?: string;
  useForInvoices?: boolean;
  useForActs?: boolean;
  useForContracts?: boolean;
  sortOrder?: number;
};
type ApiVisibleItem = DealType | IncomeType | PaymentSource | ClientStatus | DocumentStatusEntity | ServiceDictionaryItem | ServiceCategoryDictionaryItem | LooseDictItem;

type FullDictItem = {
  id: number;
  name: string;
  unit?: string;
  defaultPrice?: number;
  categoryId?: number | null;
  code?: string;
  useForInvoices?: boolean;
  useForActs?: boolean;
  useForContracts?: boolean;
  sortOrder?: number;
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
const isPersistedFullDictItem = (item: FullDictItem | Omit<FullDictItem, 'id' | 'createdAt'>): item is FullDictItem =>
  typeof (item as FullDictItem).id === 'number';

function isDocumentStatusScopeCompatible(
  item: { code?: string | null },
  scope: 'invoice' | 'act' | 'contract',
): boolean {
  return !item.code || isDocumentStatusCodeAllowedForKind(item.code, scope);
}

const TABS: { value: VisibleKind; label: string }[] = [
  { value: 'deal-types', label: 'Тип сделки' },
  { value: 'income-income', label: 'Тип дохода' },
  { value: 'income-expense', label: 'Тип расхода' },
  { value: 'payment-sources', label: 'Источник платежа' },
  { value: 'payment-statuses', label: 'Статус оплаты' },
  { value: 'document-statuses', label: 'Статусы документов' },
  { value: 'services', label: 'Услуги' },
  { value: 'service-categories', label: 'Категории услуг' },
  { value: 'client-statuses', label: 'Статус клиента' },
  { value: 'countries', label: 'Страны' },
  { value: 'base-currencies', label: 'Базовые валюты' },
  { value: 'service-languages', label: 'Языки сервиса' },
  { value: 'organization-forms', label: 'Формы организации' },
  { value: 'timezones', label: 'Часовые пояса' },
  { value: 'tax-systems', label: 'Системы налогообложения' },
  { value: 'tax-system-modes', label: 'Режимы налогообложения' },
  { value: 'invoice-units', label: 'Единицы позиций счета' },
  { value: 'invoice-item-names', label: 'Наименования позиций счета' },
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
    case 'payment-statuses':
      return '#0F766E';
    case 'document-statuses':
      return '#64748B';
    case 'services':
      return '#0EA5E9';
    case 'service-categories':
      return '#0EA5E9';
    case 'client-statuses':
      return '#2563EB';
    case 'countries':
      return '#2563EB';
    case 'base-currencies':
      return '#16A34A';
    case 'service-languages':
      return '#9333EA';
    case 'organization-forms':
      return '#F59E0B';
    case 'timezones':
      return '#0EA5E9';
    case 'tax-systems':
      return '#0EA5E9';
    case 'tax-system-modes':
      return '#0284C7';
    case 'invoice-units':
      return '#475569';
    case 'invoice-item-names':
      return '#334155';
  }
}

const isValidHexColor = (v: string) => /^#[0-9A-Fa-f]{6}$/.test(v);
const SERVICE_NAME_MAX_LENGTH = 1000;
const SERVICE_DESCRIPTION_MAX_LENGTH = 2000;

function prettyErrorMessage(e: unknown): string {
  const raw = e instanceof Error ? e.message : String(e);

  // Формат из apiService: "HTTP 400 Bad Request at <url> - <server message>"
  const marker = ' - ';
  const markerIdx = raw.indexOf(marker);
  if (markerIdx >= 0) {
    const serverPart = raw.slice(markerIdx + marker.length).trim();
    if (serverPart) return serverPart;
  }

  // fallback: скрываем техническую часть до " at "
  const atIdx = raw.indexOf(' at ');
  if (atIdx > 0) {
    return raw.slice(0, atIdx).trim();
  }

  return raw;
}

function normalize(kind: VisibleKind, x: ApiVisibleItem): FullDictItem {
  return {
    id: x.id,
    name: x.name,
    unit: 'unit' in x && typeof x.unit === 'string' ? x.unit : undefined,
    defaultPrice: 'defaultPrice' in x && typeof x.defaultPrice === 'number' ? x.defaultPrice : undefined,
    categoryId: 'categoryId' in x ? (x.categoryId as number | null | undefined) ?? null : undefined,
    code: 'code' in x && typeof x.code === 'string' ? x.code : undefined,
    useForInvoices: 'useForInvoices' in x && typeof x.useForInvoices === 'boolean' ? x.useForInvoices && isDocumentStatusScopeCompatible(x, 'invoice') : undefined,
    useForActs: 'useForActs' in x && typeof x.useForActs === 'boolean' ? x.useForActs && isDocumentStatusScopeCompatible(x, 'act') : undefined,
    useForContracts: 'useForContracts' in x && typeof x.useForContracts === 'boolean' ? x.useForContracts && isDocumentStatusScopeCompatible(x, 'contract') : undefined,
    sortOrder: 'sortOrder' in x && typeof x.sortOrder === 'number' ? x.sortOrder : undefined,
    description: x.description ?? '',
    colorHex: x.colorHex ?? defaultColor(kind),
    isActive: !!x.isActive,
    createdAt: x.createdAt,
  };
}

export interface DictionariesModalProps {
  open: boolean;
  onClose: () => void;
  initialTab?: VisibleKind;
  onlyTab?: VisibleKind;
  onPickItem?: (kind: VisibleKind, item: FullDictItem) => void;
}

export default function DictionariesModal({
  open,
  onClose,
  initialTab = 'deal-types',
  onlyTab,
  onPickItem,
}: DictionariesModalProps) {
  const [activeTab, setActiveTab] = useState<VisibleKind>(initialTab);
  const [data, setData] = useState<Record<VisibleKind, RowState[]>>({
    'deal-types': [],
    'income-income': [],
    'income-expense': [],
    'payment-sources': [],
    'payment-statuses': [],
    'document-statuses': [],
    'services': [],
    'service-categories': [],
    'client-statuses': [],
    'countries': [],
    'base-currencies': [],
    'service-languages': [],
    'organization-forms': [],
    'timezones': [],
    'tax-systems': [],
    'tax-system-modes': [],
    'invoice-units': [],
    'invoice-item-names': [],
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<{ kind: VisibleKind; id: number } | null>(
    null,
  );
  const [searchQuery, setSearchQuery] = useState('');
  const serviceCategories = useMemo(
    () => (data['service-categories'] ?? [])
      .map((row) => row.item)
      .filter((item): item is FullDictItem => isPersistedFullDictItem(item) && item.isActive),
    [data],
  );

  const visibleTabs = useMemo(
    () => (onlyTab ? TABS.filter((t) => t.value === onlyTab) : TABS),
    [onlyTab],
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
      setError(prettyErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }, []);

  useEffect(() => {
    if (!open) return;
    setActiveTab(onlyTab ?? initialTab);
    setSearchQuery('');
    (async () => {
      await Promise.all(visibleTabs.map((t) => loadKind(t.value)));
    })();
  }, [open, loadKind, initialTab, onlyTab, visibleTabs]);

  useEffect(() => {
    if (!open) return;
    setSearchQuery('');
  }, [activeTab, open]);

  const filteredRows = useMemo(() => {
    const rows = (data[activeTab] ?? []).map((row, index) => ({ row, index }));
    const normalizedQuery = searchQuery.trim().toLocaleLowerCase('ru');
    if (!normalizedQuery) {
      return rows;
    }

    return rows.filter(({ row }) => {
      const haystack = `${row.item.name} ${row.item.description ?? ''}`.toLocaleLowerCase('ru');
      return haystack.includes(normalizedQuery);
    });
  }, [activeTab, data, searchQuery]);

  const addNew = (kind: VisibleKind) => {
    const baseItem: Omit<FullDictItem, 'id' | 'createdAt'> = {
      name: '',
      description: '',
      colorHex: defaultColor(kind),
      isActive: true,
    };
    if (supportsServiceFields(kind)) {
      baseItem.unit = 'шт';
      baseItem.defaultPrice = 0;
    }
    if (supportsServiceCategory(kind)) {
      baseItem.categoryId = serviceCategories[0]?.id ?? null;
    }
    setData((prev) => ({
      ...prev,
      [kind]: [
        {
          _key: makeKey(),
          mode: 'new' as const,
          item: baseItem,
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

  function supportsServiceFields(kind: VisibleKind): boolean {
    return kind === 'services' || kind === 'invoice-item-names';
  }

  function supportsServiceCategory(kind: VisibleKind): boolean {
    return kind === 'services';
  }

  function isDocumentStatusKind(kind: VisibleKind): boolean {
    return kind === 'document-statuses';
  }

  function isDocumentStatusScopeAllowed(
    item: { code?: string | null },
    scope: 'invoice' | 'act' | 'contract',
  ): boolean {
    return isDocumentStatusScopeCompatible(item, scope);
  }

  async function saveNew(
    kind: VisibleKind,
    idx: number,
    item: Omit<FullDictItem, 'id' | 'createdAt'>,
  ) {
    if (!item.name.trim()) return setError('Название не должно быть пустым');
    if (supportsServiceCategory(kind) && item.name.trim().length > SERVICE_NAME_MAX_LENGTH) {
      return setError(`Название услуги не должно быть длиннее ${SERVICE_NAME_MAX_LENGTH} символов`);
    }
    if (supportsServiceCategory(kind) && (item.description ?? '').trim().length > SERVICE_DESCRIPTION_MAX_LENGTH) {
      return setError(`Описание услуги не должно быть длиннее ${SERVICE_DESCRIPTION_MAX_LENGTH} символов`);
    }
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
            ...(supportsServiceFields(kind)
              ? {
                  unit: item.unit ?? 'шт',
                  defaultPrice: item.defaultPrice ?? 0,
                }
              : {}),
            ...(supportsServiceCategory(kind)
              ? { categoryId: item.categoryId ?? null }
              : {}),
            ...(isDocumentStatusKind(kind)
              ? {
                  code: item.code,
                  useForInvoices: !!item.useForInvoices && isDocumentStatusScopeAllowed(item, 'invoice'),
                  useForActs: !!item.useForActs && isDocumentStatusScopeAllowed(item, 'act'),
                  useForContracts: !!item.useForContracts && isDocumentStatusScopeAllowed(item, 'contract'),
                  sortOrder: item.sortOrder ?? 0,
                }
              : {}),
          });
      const normalized = normalize(kind, created as ApiVisibleItem);
      setData((prev) => {
        const copy = [...prev[kind]];
        copy[idx] = { _key: copy[idx]._key, mode: 'view', item: normalized };
        return { ...prev, [kind]: sortRows(copy) };
      });
      emitDictsChanged();
    } catch (e) {
      setError(prettyErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function saveEdit(kind: VisibleKind, idx: number, item: FullDictItem) {
    if (!item.name.trim()) return setError('Название не должно быть пустым');
    if (supportsServiceCategory(kind) && item.name.trim().length > SERVICE_NAME_MAX_LENGTH) {
      return setError(`Название услуги не должно быть длиннее ${SERVICE_NAME_MAX_LENGTH} символов`);
    }
    if (supportsServiceCategory(kind) && (item.description ?? '').trim().length > SERVICE_DESCRIPTION_MAX_LENGTH) {
      return setError(`Описание услуги не должно быть длиннее ${SERVICE_DESCRIPTION_MAX_LENGTH} символов`);
    }
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
            ...(supportsServiceFields(kind)
              ? {
                  unit: item.unit ?? 'шт',
                  defaultPrice: item.defaultPrice ?? 0,
                }
              : {}),
            ...(supportsServiceCategory(kind)
              ? { categoryId: item.categoryId ?? null }
              : {}),
            ...(isDocumentStatusKind(kind)
              ? {
                  code: item.code,
                  useForInvoices: !!item.useForInvoices && isDocumentStatusScopeAllowed(item, 'invoice'),
                  useForActs: !!item.useForActs && isDocumentStatusScopeAllowed(item, 'act'),
                  useForContracts: !!item.useForContracts && isDocumentStatusScopeAllowed(item, 'contract'),
                  sortOrder: item.sortOrder ?? 0,
                }
              : {}),
          });
      const normalized = normalize(kind, updated as ApiVisibleItem);
      setData((prev) => {
        const copy = [...prev[kind]];
        copy[idx] = { _key: copy[idx]._key, mode: 'view', item: normalized };
        return { ...prev, [kind]: sortRows(copy) };
      });
      emitDictsChanged();
    } catch (e) {
      setError(prettyErrorMessage(e));
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
      setError(prettyErrorMessage(e));
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
              maxLength={supportsServiceCategory(kind) ? SERVICE_NAME_MAX_LENGTH : undefined}
              onChange={(e) => set({ name: e.target.value })}
            />
          </div>

          {supportsServiceCategory(kind) && (
            <div className="sm:col-span-4">
              <div className="flex flex-col gap-1">
                <label className="text-xs text-gray-600">Категория</label>
                <select
                  value={local.categoryId ?? ''}
                  onChange={(e) =>
                    set({
                      categoryId: e.target.value ? Number(e.target.value) : null,
                    })
                  }
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-500"
                >
                  <option value="">Без категории</option>
                  {serviceCategories.map((category) => (
                    <option key={category.id} value={category.id}>
                      {category.name}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          )}

          {isDocumentStatusKind(kind) && (
            <div className="sm:col-span-3">
              <TextInput
                label="Код"
                value={local.code ?? ''}
                onChange={(e) => set({ code: e.target.value })}
              />
            </div>
          )}

          {supportsServiceFields(kind) && (
            <div className="sm:col-span-3 grid grid-cols-2 gap-2">
              <TextInput
                label="Ед."
                placeholder="шт"
                value={local.unit ?? ''}
                onChange={(e) => set({ unit: e.target.value })}
              />
              <TextInput
                label="Цена"
                placeholder="0"
                inputMode="decimal"
                value={local.defaultPrice ?? 0}
                onChange={(e) => set({ defaultPrice: Number(e.target.value.replace(',', '.')) || 0 })}
              />
            </div>
          )}

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
            {isDocumentStatusKind(kind) && (
              <div className="mb-3 flex flex-wrap gap-3 rounded-lg border border-gray-200 bg-white px-3 py-2">
                <Checkbox checked={!!local.useForInvoices} onChange={(v) => set({ useForInvoices: v })} label="Счета" />
                <Checkbox checked={!!local.useForActs} onChange={(v) => set({ useForActs: v })} label="Акты" />
                <Checkbox checked={!!local.useForContracts} onChange={(v) => set({ useForContracts: v })} label="Договоры" />
              </div>
            )}
            <TextArea
              label="Описание"
              placeholder="Короткое пояснение..."
              rows={2}
              value={local.description}
              maxLength={supportsServiceCategory(kind) ? SERVICE_DESCRIPTION_MAX_LENGTH : undefined}
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
              maxLength={supportsServiceCategory(kind) ? SERVICE_NAME_MAX_LENGTH : undefined}
              onChange={(e) => set({ name: e.target.value })}
            />
          </div>

          {supportsServiceCategory(kind) && (
            <div className="sm:col-span-4">
              <div className="flex flex-col gap-1">
                <label className="text-xs text-gray-600">Категория</label>
                <select
                  value={local.categoryId ?? ''}
                  onChange={(e) =>
                    set({
                      categoryId: e.target.value ? Number(e.target.value) : null,
                    })
                  }
                  className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-500"
                >
                  <option value="">Без категории</option>
                  {serviceCategories.map((category) => (
                    <option key={category.id} value={category.id}>
                      {category.name}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          )}

          {isDocumentStatusKind(kind) && (
            <div className="sm:col-span-3">
              <TextInput
                label="Код"
                value={local.code ?? ''}
                readOnly
                className="bg-gray-100 text-gray-600"
              />
            </div>
          )}

          {supportsServiceFields(kind) && (
            <div className="sm:col-span-3 grid grid-cols-2 gap-2">
              <TextInput
                label="Ед."
                value={local.unit ?? ''}
                onChange={(e) => set({ unit: e.target.value })}
              />
              <TextInput
                label="Цена"
                inputMode="decimal"
                value={local.defaultPrice ?? 0}
                onChange={(e) => set({ defaultPrice: Number(e.target.value.replace(',', '.')) || 0 })}
              />
            </div>
          )}

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
            {isDocumentStatusKind(kind) && (
              <div className="mb-3 flex flex-wrap gap-3 rounded-lg border border-gray-200 bg-white px-3 py-2">
                <Checkbox checked={!!local.useForInvoices} onChange={(v) => set({ useForInvoices: v })} label="Счета" />
                <Checkbox checked={!!local.useForActs} onChange={(v) => set({ useForActs: v })} label="Акты" />
                <Checkbox checked={!!local.useForContracts} onChange={(v) => set({ useForContracts: v })} label="Договоры" />
              </div>
            )}
            <TextArea
              label="Описание"
              rows={2}
              value={local.description}
              maxLength={supportsServiceCategory(kind) ? SERVICE_DESCRIPTION_MAX_LENGTH : undefined}
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
    onPick,
    kind,
  }: {
    item: FullDictItem;
    onEdit: () => void;
    onDelete: () => void;
    onPick?: () => void;
    kind: VisibleKind;
  }) {
    const categoryLabel = kind === 'services' && item.categoryId
      ? serviceCategories.find((category) => category.id === item.categoryId)?.name
      : null;
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
          {categoryLabel && (
            <div className={`mt-1 text-xs ${item.isActive ? 'text-gray-600' : 'text-gray-400'}`}>
              Категория: {categoryLabel}
            </div>
          )}
          {supportsServiceFields(kind) && (
            <div className={`mt-1 text-xs ${item.isActive ? 'text-gray-600' : 'text-gray-400'}`}>
              Ед.: {item.unit || 'шт'} · Цена: {item.defaultPrice ?? 0}
            </div>
          )}
          {isDocumentStatusKind(kind) && (
            <div className="mt-2 flex flex-wrap gap-2 text-xs">
              <span className="rounded bg-gray-100 px-2 py-0.5 text-gray-600">Код: {item.code}</span>
              {item.useForInvoices && <span className="rounded bg-blue-50 px-2 py-0.5 text-blue-700">Счета</span>}
              {item.useForActs && <span className="rounded bg-emerald-50 px-2 py-0.5 text-emerald-700">Акты</span>}
              {item.useForContracts && <span className="rounded bg-violet-50 px-2 py-0.5 text-violet-700">Договоры</span>}
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
          {onPick && (
            <Btn variant="secondary" onClick={onPick}>
              Выбрать
            </Btn>
          )}
          <Btn
            variant="ghost"
            onClick={onEdit}
            aria-label={`Редактировать ${item.name}`}
            title="Редактировать">
            <Pencil className="h-4 w-4" />
          </Btn>
          {!isDocumentStatusKind(kind) && (
            <Btn
              variant="ghost"
              onClick={onDelete}
              aria-label={`Удалить ${item.name}`}
              title="Удалить">
              <Trash2 className="h-4 w-4" />
            </Btn>
          )}
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
          <Tabs tabs={visibleTabs} value={activeTab} onChange={(v) => setActiveTab(v)} />
        </div>

        <div className="mb-4">
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
            <input
              type="text"
              value={searchQuery}
              onChange={(event) => setSearchQuery(event.target.value)}
              placeholder="Поиск по названию и описанию"
              className="w-full rounded-xl border border-gray-300 py-2 pl-10 pr-3 text-sm outline-none focus:ring-2 focus:ring-emerald-500"
            />
          </div>
        </div>

        {error && (
          <div className="mb-3 rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-700">
            {error}
          </div>
        )}

        <div className={busy ? 'pointer-events-none opacity-70' : ''}>
          <div className="mb-3 flex items-center justify-between">
            <div className="text-sm text-gray-600">Всего: {data[activeTab]?.length ?? 0}</div>
            {!isDocumentStatusKind(activeTab) && (
              <Btn onClick={() => addNew(onlyTab ?? activeTab)}>
                <Plus className="h-4 w-4" />
                Добавить
              </Btn>
            )}
          </div>

          <div className="space-y-2">
            {filteredRows.length === 0 ? (
              <div className="rounded-xl border border-dashed border-gray-300 px-4 py-10 text-center text-sm text-gray-500">
                Ничего не найдено
              </div>
            ) : filteredRows.map(({ row, index }) => {
              const key = row._key;
              if (row.mode === 'view') {
                return (
                  <ViewRow
                    key={key}
                    item={row.item}
                    kind={activeTab}
                    onEdit={() => setRow(activeTab, index, (prev) => ({ ...prev, mode: 'edit' }))}
                    onDelete={() => setConfirmDelete({ kind: activeTab, id: row.item.id })}
                    onPick={onPickItem ? () => onPickItem(activeTab, row.item) : undefined}
                  />
                );
              }
              if (row.mode === 'edit') {
                return <EditRow key={key} idx={index} kind={activeTab} item={row.item} />;
              }
              // new
              return <NewRow key={key} idx={index} kind={activeTab} item={row.item} />;
            })}
          </div>
        </div>
      </Modal>

      {confirmDelete && (
        <ConfirmModal open={true} onClose={() => setConfirmDelete(null)} title="Удалить элемент?">
          <div className="space-y-4">
            <p className="text-sm text-gray-700">Действие необратимо. Удалить элемент?</p>
            <div className="flex justify-end gap-2">
              <Btn variant="secondary" onClick={() => setConfirmDelete(null)}>
                Отмена
              </Btn>
              <Btn onClick={doDelete}>Удалить</Btn>
            </div>
          </div>
        </ConfirmModal>
      )}
    </>
  );
}
