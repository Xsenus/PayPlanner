import { useCallback, useEffect, useMemo, useState } from 'react';
import { ArrowLeft, Ban, Check, Copy, Download, Info, Link2, Loader2, Pencil, Plus, Printer, Search, Send, Trash2, UserRoundSearch, X } from 'lucide-react';
import type { Act, ActStatus, Client, Invoice, InvoiceInput, InvoiceLineItem, PaymentKind, PaymentStatus } from '../../types';
import { fromInputToApiDate, formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { useTabNavigation } from '../../contexts/TabContext';
import { storeInstallmentDraft } from '../../utils/installmentBridge';
import { useDialog } from '../../hooks/useDialog';

type FormState = {
  number: string;
  date: string;
  dueDate: string;
  amount: string;
  type: PaymentKind;
  status: PaymentStatus;
  clientId: string;
  description: string;
  actReference: string;
  paidDate: string;
  counterpartyName: string;
};

type LineItem = {
  id: string;
  name: string;
  quantity: string;
  unit: string;
  price: string;
};

type DraftActForm = {
  number: string;
  title: string;
  date: string;
  amount: string;
  comment: string;
};

type ClientPickerMode = 'client' | 'counterparty';

function normalizeLineItems(source?: InvoiceLineItem[] | null, fallbackAmount?: number): LineItem[] {
  if (source && source.length > 0) {
    return source.map((item, index) => ({
      id: `${Date.now()}-${index}`,
      name: item.name ?? '',
      quantity: String(item.quantity ?? 0),
      unit: item.unit ?? 'шт',
      price: String(item.price ?? 0),
    }));
  }

  return [
    {
      id: `${Date.now()}-base`,
      name: '',
      quantity: '1',
      unit: 'шт',
      price: fallbackAmount !== undefined && fallbackAmount !== null ? String(fallbackAmount) : '',
    },
  ];
}

type ActOption = Pick<Act, 'id' | 'number' | 'title' | 'date' | 'amount' | 'invoiceNumber' | 'clientId' | 'clientName' | 'status'>;

interface InvoiceModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  invoice: Invoice | null;
  onClose: () => void;
  onBack?: () => void;
  onOpenClient?: (clientId: number) => void;
  onSubmit: (payload: InvoiceInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  lookupsLoading: boolean;
  lookupsError?: string | null;
  defaultClientId?: number;
  defaultType?: PaymentKind;
  lockType?: boolean;
  presentation?: 'modal' | 'page';
}

function normalizeInvoiceToForm(
  invoice: Invoice | null,
  defaultClientId?: number,
  defaultType?: PaymentKind,
): FormState {
  const today = formatLocalYMD(new Date());
  const resolvedType: PaymentKind = defaultType ?? 'Income';
  if (!invoice) {
    return {
      number: '',
      date: today,
      dueDate: '',
      amount: '',
      type: resolvedType,
      status: 'Pending',
      clientId: defaultClientId ? String(defaultClientId) : '',
      description: '',
      actReference: '',
      paidDate: '',
      counterpartyName: '',
    };
  }

  return {
    number: invoice.number ?? '',
    date: toDateInputValue(invoice.date) || today,
    dueDate: toDateInputValue(invoice.dueDate) || '',
    amount: invoice.amount !== undefined && invoice.amount !== null ? String(invoice.amount) : '',
    type: invoice.type ?? resolvedType,
    status: invoice.status ?? 'Pending',
    clientId: invoice.clientId ? String(invoice.clientId) : defaultClientId ? String(defaultClientId) : '',
    description: invoice.description ?? '',
    actReference: invoice.actReference ?? '',
    paidDate: toDateInputValue(invoice.paidDate) || '',
    counterpartyName: invoice.counterpartyName ?? '',
  };
}

function actLabel(act: ActOption | null): string {
  if (!act) return '';
  const parts: string[] = [];
  if (act.number) parts.push(`№${act.number}`);
  if (act.title) parts.push(act.title);
  return parts.join(' · ');
}

function formatDate(value?: string | null): string {
  if (!value) return '';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

export function InvoiceModal({
  open,
  mode,
  invoice,
  onClose,
  onBack,
  onOpenClient,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  lookupsLoading,
  lookupsError,
  defaultClientId,
  defaultType,
  lockType = false,
  presentation = 'modal',
}: InvoiceModalProps) {
  const { t } = useTranslation();
  const { confirm, alert } = useDialog();
  const { setActiveTab } = useTabNavigation();
  const typeOptions: PaymentKind[] = ['Income', 'Expense'];
  const typeBadgeClasses: Record<PaymentKind, string> = {
    Income: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Expense: 'bg-rose-100 text-rose-700 border border-rose-200',
  };
  const isTypeLocked = lockType && mode === 'create';
  const [form, setForm] = useState<FormState>(() =>
    normalizeInvoiceToForm(invoice, defaultClientId, defaultType),
  );
  const [lineItems, setLineItems] = useState<LineItem[]>([]);
  const [localError, setLocalError] = useState<string | null>(null);
  const [clientPickerOpen, setClientPickerOpen] = useState(false);
  const [clientPickerMode, setClientPickerMode] = useState<ClientPickerMode>('client');
  const [clientPickerSearch, setClientPickerSearch] = useState('');
  const [counterpartyClientId, setCounterpartyClientId] = useState<number | null>(null);
  const [selectedAct, setSelectedAct] = useState<ActOption | null>(null);
  const [createActOpen, setCreateActOpen] = useState(false);
  const [pageViewMode, setPageViewMode] = useState<boolean>(presentation === 'page' && mode === 'edit');
  const [creatingAct, setCreatingAct] = useState(false);
  const [createActError, setCreateActError] = useState<string | null>(null);
  const [draftAct, setDraftAct] = useState<DraftActForm>({
    number: '',
    title: '',
    date: formatLocalYMD(new Date()),
    amount: '',
    comment: '',
  });

  const parseMoney = useCallback((value: string): number => {
    const normalized = value.replace(',', '.').trim();
    if (!normalized) return 0;
    const parsed = Number.parseFloat(normalized);
    return Number.isFinite(parsed) ? parsed : 0;
  }, []);

  const calculateLineItemsTotal = useCallback(
    (items: LineItem[]) => {
      const total = items.reduce((sum, item) => sum + parseMoney(item.quantity) * parseMoney(item.price), 0);
      return total.toFixed(2);
    },
    [parseMoney],
  );

  const initialAct = useMemo<ActOption | null>(() => {
    if (!invoice?.actId) return null;
    return {
      id: invoice.actId,
      number: invoice.actNumber ?? invoice.number,
      title: invoice.actTitle ?? undefined,
      date: invoice.date,
      amount: invoice.amount,
      invoiceNumber: invoice.actNumber ?? invoice.number,
      clientId: invoice.clientId ?? undefined,
      clientName: invoice.clientName ?? undefined,
      status: (invoice.actStatus ?? 'Created') as ActStatus,
    };
  }, [invoice]);

  const [actSearch, setActSearch] = useState<string>(() => actLabel(initialAct));
  const [actSuggestions, setActSuggestions] = useState<ActOption[]>([]);
  const [actsLoading, setActsLoading] = useState(false);
  const [actsError, setActsError] = useState<string | null>(null);
  const [actDropdownOpen, setActDropdownOpen] = useState(false);

  const debouncedActSearch = useDebouncedValue(actSearch.trim(), 300);

  useEffect(() => {
    if (presentation === 'page' && mode === 'edit') {
      setPageViewMode(true);
      return;
    }
    setPageViewMode(false);
  }, [mode, presentation, invoice?.id]);

  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === form.clientId) ?? null,
    [clients, form.clientId],
  );

  const selectedCounterpartyClient = useMemo(() => {
    if (counterpartyClientId) {
      return clients.find((client) => client.id === counterpartyClientId) ?? null;
    }
    const value = form.counterpartyName.trim().toLowerCase();
    if (!value) return null;
    return clients.find((client) => client.name.trim().toLowerCase() === value) ?? null;
  }, [clients, counterpartyClientId, form.counterpartyName]);

  const filteredClients = useMemo(() => {
    const query = clientPickerSearch.trim().toLowerCase();
    if (!query) return clients;
    return clients.filter((client) => {
      const statusName = client.clientStatus?.name ?? '';
      return [client.name, client.company ?? '', statusName]
        .join(' ')
        .toLowerCase()
        .includes(query);
    });
  }, [clientPickerSearch, clients]);

  const isIncome = form.type === 'Income';

  useEffect(() => {
    if (open) {
      setForm(normalizeInvoiceToForm(invoice, defaultClientId, defaultType));
      setLineItems(normalizeLineItems(invoice?.lineItems, invoice?.amount));
      const nextInitial = invoice?.actId ? {
        id: invoice.actId,
        number: invoice.actNumber ?? invoice.number,
        title: invoice.actTitle ?? undefined,
        date: invoice.date,
        amount: invoice.amount,
        invoiceNumber: invoice.actNumber ?? invoice.number,
        clientId: invoice.clientId ?? undefined,
        clientName: invoice.clientName ?? undefined,
        status: (invoice.actStatus ?? 'Created') as ActStatus,
      } : null;
      setCounterpartyClientId(null);
      setSelectedAct(nextInitial);
      setActSearch(actLabel(nextInitial));
      setActsError(null);
      setCreateActError(null);
      setLocalError(null);
    }
  }, [open, invoice, defaultClientId, defaultType]);

  const updateLineItem = (id: string, patch: Partial<LineItem>) => {
    setLineItems((prev) => {
      const next = prev.map((item) => (item.id === id ? { ...item, ...patch } : item));
      setForm((current) => ({ ...current, amount: calculateLineItemsTotal(next) }));
      return next;
    });
  };

  const addLineItem = () => {
    setLineItems((prev) => [
      ...prev,
      {
        id: `${Date.now()}-${prev.length}`,
        name: '',
        quantity: '1',
        unit: 'шт',
        price: '',
      },
    ]);
  };

  const removeLineItem = (id: string) => {
    setLineItems((prev) => {
      if (prev.length <= 1) return prev;
      const next = prev.filter((item) => item.id !== id);
      setForm((current) => ({ ...current, amount: calculateLineItemsTotal(next) }));
      return next;
    });
  };

  const fetchActs = useCallback(async () => {
    if (form.type !== 'Income') {
      setActSuggestions([]);
      return;
    }
    if (debouncedActSearch.length < 2) {
      setActSuggestions([]);
      return;
    }
    setActsLoading(true);
    try {
      const response = await apiService.getActs({
        search: debouncedActSearch,
        page: 1,
        pageSize: 5,
        sortBy: 'date',
        sortDir: 'desc',
        clientId: form.clientId ? Number(form.clientId) : undefined,
      });
      const items = response.items ?? [];
      const options: ActOption[] = items.map((act) => ({
        id: act.id,
        number: act.number,
        title: act.title ?? undefined,
        date: act.date,
        amount: act.amount,
        invoiceNumber: act.invoiceNumber ?? undefined,
        clientId: act.clientId ?? undefined,
        clientName: act.clientName ?? undefined,
        status: act.status,
      }));
      setActSuggestions(options);
      setActsError(null);
    } catch (err) {
      setActsError(err instanceof Error ? err.message : 'Не удалось загрузить акты');
      setActSuggestions([]);
    } finally {
      setActsLoading(false);
    }
  }, [debouncedActSearch, form.clientId, form.type]);

  useEffect(() => {
    if (!open) return;
    void fetchActs();
  }, [fetchActs, open]);

  const handleActSelect = (act: ActOption) => {
    setSelectedAct(act);
    setActSearch(actLabel(act));
    setActSuggestions([]);
    setActDropdownOpen(false);
    setForm((prev) => ({
      ...prev,
      type: 'Income',
      number: act.invoiceNumber ?? act.number ?? prev.number,
      date: toDateInputValue(act.date) || prev.date,
      dueDate: prev.dueDate || toDateInputValue(act.date),
      amount: act.amount ? String(act.amount) : prev.amount,
      clientId: act.clientId ? String(act.clientId) : prev.clientId,
    }));
  };

  const clearSelectedAct = () => {
    setSelectedAct(null);
    setActSearch('');
  };

  const handleTypeSelect = (nextType: PaymentKind) => {
    if (isTypeLocked) return;
    setForm((prev) => ({
      ...prev,
      type: nextType,
    }));

    if (nextType === 'Expense') {
      setSelectedAct(null);
      setActSearch('');
      setActSuggestions([]);
    }
  };

  const openClientPicker = (mode: ClientPickerMode) => {
    setClientPickerMode(mode);
    setClientPickerSearch('');
    setClientPickerOpen(true);
  };

  const handleClientPick = (client: Client) => {
    if (clientPickerMode === 'client') {
      setForm((prev) => ({ ...prev, clientId: String(client.id) }));
    } else {
      setCounterpartyClientId(client.id);
      setForm((prev) => ({ ...prev, counterpartyName: client.name }));
    }
    setClientPickerOpen(false);
  };

  const handleCreateAct = async () => {
    if (!draftAct.number.trim()) {
      setCreateActError(t('actNumber') ?? 'Номер акта обязателен');
      return;
    }

    const actAmount = Number.parseFloat((draftAct.amount || form.amount).replace(',', '.'));
    if (!Number.isFinite(actAmount) || actAmount < 0) {
      setCreateActError(t('invoiceAmount') ?? 'Введите корректную сумму');
      return;
    }

    setCreatingAct(true);
    setCreateActError(null);
    try {
      const created = await apiService.createAct({
        number: draftAct.number.trim(),
        title: draftAct.title.trim() || undefined,
        date: fromInputToApiDate(draftAct.date) ?? draftAct.date,
        amount: actAmount,
        invoiceNumber: form.number.trim() || undefined,
        clientId: form.clientId ? Number(form.clientId) : undefined,
        status: 'Created',
        comment: draftAct.comment.trim() || undefined,
      });

      const option: ActOption = {
        id: created.id,
        number: created.number,
        title: created.title ?? undefined,
        date: created.date,
        amount: created.amount,
        invoiceNumber: created.invoiceNumber ?? undefined,
        clientId: created.clientId ?? undefined,
        clientName: created.clientName ?? undefined,
        status: created.status,
      };

      setSelectedAct(option);
      setActSearch(actLabel(option));
      setCreateActOpen(false);
      setDraftAct({
        number: '',
        title: '',
        date: form.date || formatLocalYMD(new Date()),
        amount: form.amount,
        comment: '',
      });
    } catch (err) {
      setCreateActError(err instanceof Error ? err.message : 'Не удалось создать акт');
    } finally {
      setCreatingAct(false);
    }
  };

  const handleChange = (field: keyof FormState) => (
    event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const value = event.target.value;
    setForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    const number = form.number.trim();
    if (!number) {
      setLocalError(t('invoiceNumber') ?? 'Номер счёта обязателен');
      return;
    }
    if (!form.date) {
      setLocalError(t('invoiceDate') ?? 'Дата обязательна');
      return;
    }
    if (!form.clientId) {
      setLocalError(t('invoiceClientPlaceholder') ?? 'Выберите клиента');
      return;
    }

    const parsedAmount = Number.parseFloat(form.amount.replace(',', '.'));
    if (Number.isNaN(parsedAmount) || parsedAmount < 0) {
      setLocalError(t('invoiceAmount') ?? 'Введите корректную сумму');
      return;
    }

    const counterparty = form.counterpartyName.trim();

    const payload: InvoiceInput = {
      number,
      date: fromInputToApiDate(form.date) ?? form.date,
      dueDate: form.dueDate ? fromInputToApiDate(form.dueDate) ?? form.dueDate : undefined,
      amount: parsedAmount,
      type: form.type,
      status: form.status,
      clientId: Number(form.clientId),
      description: form.description.trim() ? form.description.trim() : undefined,
      actReference: form.actReference.trim() ? form.actReference.trim() : undefined,
      actId: selectedAct ? selectedAct.id : null,
      counterpartyName: counterparty ? counterparty : undefined,
      paidDate: form.paidDate ? fromInputToApiDate(form.paidDate) ?? form.paidDate : undefined,
      lineItems: lineItems
        .map((item) => ({
          name: item.name.trim(),
          quantity: Number.parseFloat(item.quantity.replace(',', '.')),
          unit: item.unit.trim() || 'шт',
          price: Number.parseFloat(item.price.replace(',', '.')),
        }))
        .filter((item) => item.name && Number.isFinite(item.quantity) && Number.isFinite(item.price) && item.quantity >= 0 && item.price >= 0),
    };

    try {
      await onSubmit(payload);
    } catch (err) {
      if (err instanceof Error && !errorMessage) {
        setLocalError(err.message);
      }
    }
  };

  const handleOpenInstallmentCalculator = () => {
    if (form.type !== 'Income') return;
    const parsedAmount = Number.parseFloat(form.amount.replace(',', '.'));
    const hasValidAmount = Number.isFinite(parsedAmount) && parsedAmount > 0;
    const clientIdNumber = form.clientId ? Number.parseInt(form.clientId, 10) : undefined;

    storeInstallmentDraft({
      total: hasValidAmount ? parsedAmount : undefined,
      startDate: form.date || undefined,
      clientId: Number.isFinite(clientIdNumber) ? clientIdNumber : undefined,
      clientName: selectedClient?.name ?? undefined,
      invoiceNumber: form.number.trim() ? form.number.trim() : undefined,
    });

    setActiveTab('calculator');
    onClose();
  };

  const invoiceLink = useMemo(() => {
    if (typeof window === 'undefined') return '';
    const invoiceId = invoice?.id ? `/${invoice.id}` : '';
    return `${window.location.origin}/invoices${invoiceId}`;
  }, [invoice?.id]);

  const handlePageAction = async (action: 'send' | 'print' | 'download' | 'copy' | 'paid' | 'cancel' | 'delete') => {
    const actionMeta = {
      send: { title: 'Отправить счёт', message: 'Отправить счёт клиенту?', confirmLabel: 'Отправить' },
      print: { title: 'Печать счёта', message: 'Подготовить счёт к печати?', confirmLabel: 'Печать' },
      download: { title: 'Скачать счёт', message: 'Скачать документ счёта?', confirmLabel: 'Скачать' },
      copy: { title: 'Копия счёта', message: 'Создать копию текущего счёта?', confirmLabel: 'Копировать' },
      paid: { title: 'Отметить оплаченным', message: 'Отметить счёт как оплаченный?', confirmLabel: 'Оплачен' },
      cancel: { title: 'Отменить счёт', message: 'Подтвердите отмену счёта.', confirmLabel: 'Отменить' },
      delete: { title: 'Удалить счёт', message: 'Удалить счёт без возможности восстановления?', confirmLabel: 'Удалить' },
    }[action];

    const accepted = await confirm({
      title: actionMeta.title,
      description: actionMeta.message,
      confirmLabel: actionMeta.confirmLabel,
      cancelLabel: 'Отмена',
      variant: action === 'cancel' || action === 'delete' ? 'danger' : 'primary',
    });
    if (!accepted) return;

    if (action === 'paid') {
      setForm((prev) => ({ ...prev, status: 'Completed' }));
    }
    if (action === 'cancel') {
      setForm((prev) => ({ ...prev, status: 'Cancelled' }));
    }

    await alert({
      title: 'Готово',
      description: 'Действие зарегистрировано. Интеграцию с серверными операциями подключим на следующем шаге.',
      confirmLabel: 'Понятно',
      variant: 'success',
    });
  };



  const amountValue = Number.parseFloat(form.amount.replace(',', '.')) || 0;
  const lineItemsTotal = lineItems.reduce((sum, item) => sum + parseMoney(item.quantity) * parseMoney(item.price), 0);
  const lineItemsCount = lineItems.filter((item) => item.name.trim()).length;
  const printableAmount = formatCurrencySmart(amountValue).full;
  if (!open) return null;

  const isPageMode = presentation === 'page';

  const content = (
    <div className={`relative w-full max-w-6xl overflow-hidden rounded-2xl bg-white ${isPageMode ? 'border border-slate-200 shadow-sm' : 'shadow-2xl'}`}>
      <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
        <div className="flex items-center gap-3">
          {isPageMode && (
            <button
              type="button"
              onClick={onBack ?? onClose}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
            >
              <ArrowLeft className="h-4 w-4" />
              Назад
            </button>
          )}
          <div>
            <h2 className="text-xl font-semibold text-slate-900">
              {isPageMode && pageViewMode
                ? `Счёт №${form.number || '—'} от ${formatDate(form.date) || '—'}`
                : mode === 'edit'
                ? t('invoiceEdit') ?? 'Редактировать счёт'
                : t('invoiceCreate') ?? 'Создать счёт'}
            </h2>
            <p className="text-sm text-slate-500">
              {isPageMode && pageViewMode
                ? 'Просмотр счёта с действиями и документом для печати/отправки'
                : t('invoiceModalSubtitle') ?? 'Укажите параметры счёта'}
            </p>
          </div>
        </div>
        {isPageMode && mode === 'edit' && (
          <button
            type="button"
            onClick={() => setPageViewMode((prev) => !prev)}
            className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
          >
            <Pencil className="h-4 w-4" />
            {pageViewMode ? 'Редактировать счёт' : 'Вернуться к просмотру'}
          </button>
        )}
        {!isPageMode && (
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Закрыть'}
          >
            <X className="h-5 w-5" />
          </button>
        )}
      </div>

      {isPageMode && pageViewMode ? (
        <div className="space-y-4 bg-slate-100 px-4 py-4">
          <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
            <section className="rounded-xl border border-slate-300 bg-white p-3">
              <div className="mb-3 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <button type="button" className="rounded-md bg-blue-600 p-2 text-white"><Info className="h-4 w-4" /></button>
                  <button type="button" onClick={() => setPageViewMode(false)} className="rounded-md bg-blue-500 p-2 text-white"><Pencil className="h-4 w-4" /></button>
                </div>
                <div className="text-sm font-semibold text-slate-700">{printableAmount}</div>
              </div>

              <div className="border border-slate-800 p-3 text-sm text-slate-900">
                <div className="grid grid-cols-2 border border-slate-800">
                  <div className="border-r border-slate-800 p-2">{form.counterpartyName || 'Поставщик'}</div>
                  <div className="p-2">БИК / Счёт</div>
                </div>
                <h3 className="my-4 text-center text-2xl font-bold">Счет № {form.number || '—'} от {formatDate(form.date) || '—'} г.</h3>
                <div className="space-y-1 border-t border-slate-800 pt-3 text-xs sm:text-sm">
                  <p><span className="font-semibold">Поставщик:</span> {form.counterpartyName || '—'}</p>
                  <p><span className="font-semibold">Покупатель:</span> {selectedClient?.name || '—'}</p>
                  <p><span className="font-semibold">Основание:</span> {form.description || '—'}</p>
                </div>

                <table className="mt-3 w-full border-collapse text-xs sm:text-sm">
                  <thead>
                    <tr>
                      <th className="border border-slate-800 px-2 py-1 text-left">№</th>
                      <th className="border border-slate-800 px-2 py-1 text-left">Товары (работы, услуги)</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Кол-во</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Ед.</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Цена</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Сумма</th>
                    </tr>
                  </thead>
                  <tbody>
                    {lineItems.map((item, index) => {
                      const qty = parseMoney(item.quantity);
                      const price = parseMoney(item.price);
                      const rowTotal = qty * price;
                      return (
                        <tr key={item.id}>
                          <td className="border border-slate-800 px-2 py-1">{index + 1}</td>
                          <td className="border border-slate-800 px-2 py-1">{item.name || '—'}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{qty.toFixed(2)}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{item.unit || 'шт'}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{price.toFixed(2)}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{rowTotal.toFixed(2)}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>

                <div className="mt-3 text-right text-sm">
                  <p><span className="font-semibold">Итого:</span> {lineItemsTotal.toFixed(2)}</p>
                  <p><span className="font-semibold">Всего к оплате:</span> {amountValue.toFixed(2)}</p>
                </div>
              </div>
            </section>

            <aside className="space-y-3">
              <div className="rounded-lg border border-slate-300 bg-white p-3">
                <div className="flex items-center justify-between">
                  <span className="text-xl font-semibold">{printableAmount}</span>
                  <span className="rounded bg-blue-600 px-3 py-1 text-sm font-semibold text-white">{form.status === 'Completed' ? 'Оплачен' : form.status === 'Cancelled' ? 'Отменен' : 'Создан'}</span>
                </div>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm">
                <div className="mb-2 flex items-center gap-2 font-medium"><Link2 className="h-4 w-4" />Ссылка на счет для клиента</div>
                <a href={invoiceLink} className="break-all text-blue-600 underline">{invoiceLink || '—'}</a>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm">
                <button type="button" className="flex w-full items-center justify-between rounded border border-slate-200 px-3 py-2 text-left">Товарная накладная <span>+</span></button>
                <button type="button" className="mt-2 flex w-full items-center justify-between rounded border border-slate-200 px-3 py-2 text-left">УПД <span>+</span></button>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm leading-6">
                <p><span className="text-slate-500">Покупатель:</span> <span className="text-blue-600">{selectedClient?.name || '—'}</span></p>
                <p><span className="text-slate-500">Оплатить до:</span> {formatDate(form.dueDate) || '—'}</p>
                <p><span className="text-slate-500">Комментарий:</span> {form.actReference || '—'}</p>
                <p><span className="text-slate-500">Позиции:</span> {lineItemsCount}</p>
              </div>
            </aside>
          </div>

          <div className="grid grid-cols-2 gap-2 rounded-lg border border-slate-300 bg-white p-2 md:grid-cols-4 xl:grid-cols-7">
            <button type="button" onClick={() => void handlePageAction('send')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Send className="h-4 w-4" />Отправить</button>
            <button type="button" onClick={() => void handlePageAction('print')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Printer className="h-4 w-4" />Печать</button>
            <button type="button" onClick={() => void handlePageAction('download')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Download className="h-4 w-4" />Скачать</button>
            <button type="button" onClick={() => void handlePageAction('copy')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Copy className="h-4 w-4" />Копировать</button>
            <button type="button" onClick={() => void handlePageAction('paid')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Check className="h-4 w-4" />Оплачен</button>
            <button type="button" onClick={() => void handlePageAction('cancel')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Ban className="h-4 w-4" />Отменен</button>
            <button type="button" onClick={() => void handlePageAction('delete')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Trash2 className="h-4 w-4" />Удалить</button>
          </div>
        </div>
      ) : (
      <form onSubmit={handleSubmit} className={`${isPageMode ? 'px-6 py-5' : 'max-h-[75vh] overflow-y-auto px-6 py-5'} space-y-6`}>
          <section className="rounded-2xl border border-slate-200 bg-slate-50/50 p-4 sm:p-5">
            <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                {t('invoiceMainDetailsTitle') ?? 'Основные параметры'}
              </span>
              <p className="text-sm text-slate-500">
                {t('invoiceMainDetailsHint') ?? 'Укажите тип, номер, сумму и назначение счёта.'}
              </p>
            </header>

            <div className="mt-4 space-y-4">
              <div className="flex flex-col gap-2">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <span className="font-medium text-slate-700">{t('invoiceType') ?? 'Тип счёта'}</span>
                  {lockType && mode === 'edit' && (
                    <span className="text-xs text-slate-500">
                      {t('invoiceTypeEditHint') ?? 'Можно изменить тип, если счёт попал не в ту группу.'}
                    </span>
                  )}
                </div>
                {isTypeLocked ? (
                  <span
                    className={`inline-flex items-center gap-2 self-start rounded-full px-4 py-1 text-sm font-semibold ${
                      typeBadgeClasses[form.type]
                    }`}
                  >
                    {form.type === 'Income'
                      ? t('invoiceTypeIncome') ?? 'Доходные счета'
                      : t('invoiceTypeExpense') ?? 'Расходные счета'}
                  </span>
                ) : (
                  <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-slate-200 bg-white/80 p-1">
                    {typeOptions.map((option) => {
                      const active = option === form.type;
                      return (
                        <button
                          key={option}
                          type="button"
                          onClick={() => handleTypeSelect(option)}
                          className={`rounded-full px-4 py-1 text-sm font-medium transition-colors ${
                            active
                              ? 'bg-blue-600 text-white shadow'
                              : 'bg-transparent text-slate-600 hover:bg-slate-100'
                          }`}
                        >
                          {option === 'Income'
                            ? t('invoiceTypeIncomeShort') ?? t('invoiceTypeIncome') ?? 'Доходные'
                            : t('invoiceTypeExpenseShort') ?? t('invoiceTypeExpense') ?? 'Расходные'}
                        </button>
                      );
                    })}
                  </div>
                )}
                <p className="text-xs text-slate-500">
                  {isIncome
                    ? t('invoiceTypeIncomeHint') ?? 'Используйте для выставления счётов клиентам.'
                    : t('invoiceTypeExpenseHint') ?? 'Используйте для входящих счетов от поставщиков и подрядчиков.'}
                </p>
              </div>

              <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
                <label className="flex flex-col gap-1 text-sm">
                  <span className="font-medium text-slate-700">{t('invoiceNumber') ?? 'Номер счёта'}</span>
                  <input
                    type="text"
                    value={form.number}
                    onChange={handleChange('number')}
                    className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    required
                  />
                </label>

                <label className="flex flex-col gap-1 text-sm">
                  <span className="font-medium text-slate-700 flex items-center justify-between gap-2">
                    {t('invoiceAmount') ?? 'Сумма'}
                    {isIncome && (
                      <button
                        type="button"
                        onClick={handleOpenInstallmentCalculator}
                        className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700 transition-colors hover:bg-slate-200"
                      >
                        {t('invoiceInstallmentButton') ?? 'Рассрочка'}
                      </button>
                    )}
                  </span>
                  <input
                    type="text"
                    inputMode="decimal"
                    value={form.amount}
                    onChange={handleChange('amount')}
                    className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    placeholder="0.00"
                  />
                </label>

                <label className="flex flex-col gap-1 text-sm">
                  <span className="font-medium text-slate-700">{t('invoiceStatus') ?? 'Статус'}</span>
                  <select
                    value={form.status}
                    onChange={handleChange('status')}
                    className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  >
                    <option value="Pending">{t('invoicePendingBadge') ?? t('pending') ?? 'Ожидается'}</option>
                    <option value="Completed">{t('invoicePaidBadge') ?? t('completedStatus') ?? 'Оплачен'}</option>
                    <option value="Overdue">{t('invoiceOverdueBadge') ?? t('overdue') ?? 'Просрочен'}</option>
                    <option value="Processing">{t('processingStatus') ?? 'В обработке'}</option>
                    <option value="Cancelled">{t('cancelledStatus') ?? 'Отменён'}</option>
                  </select>
                </label>
              </div>

              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('invoiceDescription') ?? 'Описание счёта'}</span>
                <textarea
                  value={form.description}
                  onChange={handleChange('description')}
                  rows={3}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  placeholder={t('invoiceDescriptionHint') ?? 'Например: счёт на оплату аренды офиса за июль.'}
                />
              </label>
            </div>
          </section>

          <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
            <header className="mb-3 flex items-center justify-between">
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Позиции счета</span>
              <button
                type="button"
                onClick={addLineItem}
                className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50"
              >
                + Добавить
              </button>
            </header>
            <div className="space-y-2">
              {lineItems.map((item) => (
                <div key={item.id} className="grid grid-cols-12 gap-2">
                  <input type="text" value={item.name} onChange={(event) => updateLineItem(item.id, { name: event.target.value })} placeholder="Наименование" className="col-span-5 rounded-lg border border-slate-300 px-3 py-2 text-sm" />
                  <input type="text" inputMode="decimal" value={item.quantity} onChange={(event) => updateLineItem(item.id, { quantity: event.target.value })} placeholder="Кол-во" className="col-span-2 rounded-lg border border-slate-300 px-3 py-2 text-sm" />
                  <input type="text" value={item.unit} onChange={(event) => updateLineItem(item.id, { unit: event.target.value })} placeholder="Ед." className="col-span-2 rounded-lg border border-slate-300 px-3 py-2 text-sm" />
                  <input type="text" inputMode="decimal" value={item.price} onChange={(event) => updateLineItem(item.id, { price: event.target.value })} placeholder="Цена" className="col-span-2 rounded-lg border border-slate-300 px-3 py-2 text-sm" />
                  <button type="button" onClick={() => removeLineItem(item.id)} className="col-span-1 rounded-lg border border-slate-200 px-2 text-slate-500 hover:bg-slate-50" aria-label="Удалить">×</button>
                </div>
              ))}
            </div>
            <div className="mt-3 text-right text-sm font-semibold text-slate-700">Итого: {formatCurrencySmart(parseMoney(form.amount)).full}</div>
          </section>

          <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_420px]">
            <div className="space-y-6">
          <section className="rounded-2xl border border-slate-200 p-4 sm:p-5">
            <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                {t('invoiceDocumentsTitle') ?? 'Связанные документы'}
              </span>
              <p className="text-sm text-slate-500">
                {t('invoiceDocumentsHint') ?? 'Прикрепите акт или оставьте комментарий.'}
              </p>
            </header>
            <div className="mt-4 space-y-4">
              {isIncome && (
                <div className="flex flex-col gap-2 text-sm">
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-medium text-slate-700">{t('invoiceAct') ?? 'Акт'}</span>
                    <button
                      type="button"
                      onClick={() => {
                        setDraftAct((prev) => ({
                          ...prev,
                          date: form.date || prev.date,
                          amount: form.amount || prev.amount,
                        }));
                        setCreateActError(null);
                        setCreateActOpen(true);
                      }}
                      className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2.5 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50"
                    >
                      <Plus className="h-3.5 w-3.5" />
                      {t('actCreate') ?? 'Создать акт'}
                    </button>
                  </div>
                  {selectedAct && (
                    <div className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-xs text-emerald-800">
                      {t('invoiceSelectedAct') ?? 'Выбран акт'}: {actLabel(selectedAct)}
                    </div>
                  )}
                  <div className="relative">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input
                      type="text"
                      value={actSearch}
                      onChange={(event) => {
                        setActSearch(event.target.value);
                        setActDropdownOpen(true);
                      }}
                      onFocus={() => setActDropdownOpen(true)}
                      onBlur={() => setTimeout(() => setActDropdownOpen(false), 150)}
                      placeholder={t('invoiceActSearchPlaceholder') ?? 'Поиск акта по номеру или названию'}
                      className="w-full rounded-lg border border-slate-300 px-9 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    />
                    {actSearch && (
                      <button
                        type="button"
                        onClick={clearSelectedAct}
                        className="absolute right-3 top-1/2 -translate-y-1/2 rounded-full p-1 text-slate-500 hover:bg-slate-100"
                        aria-label={t('clear') ?? 'Очистить'}
                      >
                        <X className="h-3.5 w-3.5" />
                      </button>
                    )}
                    {actDropdownOpen && (actsLoading || actSuggestions.length > 0 || actsError) && (
                      <div className="absolute z-20 mt-1 max-h-60 w-full overflow-auto rounded-lg border border-slate-200 bg-white shadow-lg">
                        {actsLoading ? (
                          <div className="flex items-center gap-2 px-3 py-2 text-sm text-slate-500">
                            <Loader2 className="h-4 w-4 animate-spin" /> {t('loading') ?? 'Загрузка...'}
                          </div>
                        ) : actsError ? (
                          <div className="px-3 py-2 text-sm text-rose-600">{actsError}</div>
                        ) : actSuggestions.length === 0 ? (
                          <div className="px-3 py-2 text-sm text-slate-500">
                            {t('nothingFound') ?? 'Ничего не найдено'}
                          </div>
                        ) : (
                          actSuggestions.map((act) => (
                            <button
                              key={act.id}
                              type="button"
                              onClick={() => handleActSelect(act)}
                              className="w-full border-b border-slate-100 px-3 py-2 text-left last:border-b-0 hover:bg-slate-50"
                            >
                              <div className="font-medium text-slate-900">{actLabel(act)}</div>
                              <div className="mt-1 text-xs text-slate-500">
                                <span>{formatDate(act.date)}</span>
                                <span className="mx-2">·</span>
                                <span>{formatCurrencySmart(act.amount ?? 0).full}</span>
                              </div>
                            </button>
                          ))
                        )}
                      </div>
                    )}
                  </div>
                </div>
              )}

              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('invoiceManualActLabel') ?? 'Комментарий по акту'}</span>
                <textarea
                  value={form.actReference}
                  onChange={handleChange('actReference')}
                  rows={2}
                  placeholder={t('invoiceActManualPlaceholder') ?? 'Например: акт будет подписан позже'}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                />
              </label>
            </div>
          </section>
            </div>
            <div className="space-y-6 xl:col-start-2">
<section className="rounded-2xl border border-slate-200 p-4 sm:p-5">
            <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                {t('invoicePartiesTitle') ?? 'Контрагент и источник'}
              </span>
              <p className="text-sm text-slate-500">
                {t('invoicePartiesHint') ?? 'Привяжите счёт к клиенту или укажите поставщика услуги.'}
              </p>
            </header>
            <div className="mt-4 grid grid-cols-1 gap-4">
              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('invoiceClient') ?? 'Контрагент'}</span>
                <div className="flex gap-2">
                  <input
                    type="text"
                    value={selectedClient?.name ?? ''}
                    readOnly
                    className="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm bg-slate-50"
                    placeholder={t('invoiceClientPlaceholder') ?? 'Выберите клиента'}
                  />
                  <button
                    type="button"
                    onClick={() => openClientPicker('client')}
                    className="inline-flex items-center rounded-lg border border-slate-200 px-3 py-2 text-slate-700 hover:bg-slate-50"
                  >
                    <UserRoundSearch className="h-4 w-4" />
                  </button>
                </div>
                {lookupsLoading && (
                  <span className="text-xs text-slate-500">{t('loading') ?? 'Загрузка...'}</span>
                )}
                {lookupsError && (
                  <span className="text-xs text-rose-600">{lookupsError}</span>
                )}
                {selectedClient?.clientStatus && (
                  <div className="mt-2 space-y-1 rounded-xl border border-slate-100 bg-slate-50 p-2">
                    <ClientStatusBadge status={selectedClient.clientStatus} />
                    {selectedClient.clientStatus.description && (
                      <p className="text-xs text-slate-500">{selectedClient.clientStatus.description}</p>
                    )}
                  </div>
                )}
                {selectedClient && onOpenClient && (
                  <button
                    type="button"
                    onClick={() => onOpenClient(selectedClient.id)}
                    className="inline-flex w-fit items-center text-xs font-medium text-blue-700 hover:text-blue-800 hover:underline"
                  >
                    Открыть карточку клиента
                  </button>
                )}
              </label>

              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('invoiceCounterparty') ?? 'От кого счёт'}</span>
                <div className="flex gap-2">
                  <input
                    type="text"
                    value={form.counterpartyName}
                    onChange={(event) => {
                      setCounterpartyClientId(null);
                      setForm((prev) => ({ ...prev, counterpartyName: event.target.value }));
                    }}
                    className="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                    placeholder={t('invoiceCounterpartyPlaceholder') ?? 'Например: ООО «Городской водоканал»'}
                  />
                  <button
                    type="button"
                    onClick={() => openClientPicker('counterparty')}
                    className="inline-flex items-center rounded-lg border border-slate-200 px-3 py-2 text-slate-700 hover:bg-slate-50"
                  >
                    <UserRoundSearch className="h-4 w-4" />
                  </button>
                </div>
                <span className="text-xs text-slate-500">
                  {t('invoiceCounterpartyHint') ?? 'Укажите поставщика или источник счёта, если это необходимо.'}
                </span>
                {selectedCounterpartyClient && onOpenClient && (
                  <button
                    type="button"
                    onClick={() => onOpenClient(selectedCounterpartyClient.id)}
                    className="inline-flex w-fit items-center text-xs font-medium text-blue-700 hover:text-blue-800 hover:underline"
                  >
                    Открыть карточку контрагента
                  </button>
                )}
              </label>
            </div>
          </section>

          <section className="rounded-2xl border border-slate-200 p-4 sm:p-5">
            <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                {t('invoiceScheduleTitle') ?? 'Сроки и оплата'}
              </span>
              <p className="text-sm text-slate-500">
                {t('invoiceScheduleHint') ?? 'Помогает контролировать вовремя ли оплатят счёт.'}
              </p>
            </header>
            <div className="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-3">
              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('invoiceDate') ?? 'Дата выставления'}</span>
                <input
                  type="date"
                  value={form.date}
                  onChange={handleChange('date')}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                  required
                />
              </label>

              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('invoiceDueDate') ?? 'Срок оплаты'}</span>
                <input
                  type="date"
                  value={form.dueDate}
                  onChange={handleChange('dueDate')}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                />
              </label>

              <label className="flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('invoicePaidDate') ?? 'Дата оплаты'}</span>
                <input
                  type="date"
                  value={form.paidDate}
                  onChange={handleChange('paidDate')}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                />
              </label>
            </div>
          </section>
        </div>
            </div>
          {(localError || errorMessage) && (
            <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              {localError ?? errorMessage}
            </div>
          )}

          <div className="flex items-center justify-end gap-3 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="rounded-lg border border-slate-200 px-4 py-2 text-sm text-slate-600 hover:bg-slate-100"
            >
              {t('cancel') ?? 'Отмена'}
            </button>
            <button
              type="submit"
              disabled={submitting}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-70"
            >
              {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
              {mode === 'edit' ? t('invoiceEdit') ?? 'Сохранить изменения' : t('invoiceCreate') ?? 'Создать счёт'}
            </button>
          </div>
      </form>
      )}

      {clientPickerOpen && (
        <div className="absolute inset-0 z-30 flex items-center justify-center bg-slate-900/40 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white p-4 shadow-2xl">
            <div className="mb-3 flex items-center justify-between gap-2">
              <h3 className="text-base font-semibold text-slate-900">
                {clientPickerMode === 'client'
                  ? (t('invoicePickClientTitle') ?? 'Выбор контрагента')
                  : (t('invoicePickCounterpartyTitle') ?? 'Выбор отправителя счёта')}
              </h3>
              <button type="button" onClick={() => setClientPickerOpen(false)} className="rounded-full p-1 text-slate-500 hover:bg-slate-100">
                <X className="h-4 w-4" />
              </button>
            </div>
            <input
              type="text"
              value={clientPickerSearch}
              onChange={(event) => setClientPickerSearch(event.target.value)}
              placeholder={t('search') ?? 'Поиск'}
              className="mb-3 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm"
            />
            <div className="max-h-72 overflow-auto rounded-lg border border-slate-200">
              {filteredClients.map((client) => (
                <button
                  key={client.id}
                  type="button"
                  onClick={() => handleClientPick(client)}
                  className="flex w-full items-center justify-between border-b border-slate-100 px-3 py-2 text-left last:border-0 hover:bg-slate-50"
                >
                  <span className="font-medium text-slate-800">{client.name}</span>
                  {client.clientStatus?.name && <span className="text-xs text-slate-500">{client.clientStatus.name}</span>}
                </button>
              ))}
            </div>
          </div>
        </div>
      )}

      {createActOpen && (
        <div className="absolute inset-0 z-30 flex items-center justify-center bg-slate-900/40 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white p-4 shadow-2xl">
            <div className="mb-3 flex items-center justify-between gap-2">
              <h3 className="text-base font-semibold text-slate-900">{t('actCreate') ?? 'Создать акт'}</h3>
              <button type="button" onClick={() => setCreateActOpen(false)} className="rounded-full p-1 text-slate-500 hover:bg-slate-100">
                <X className="h-4 w-4" />
              </button>
            </div>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('actNumber') ?? 'Номер акта'}</span>
                <input value={draftAct.number} onChange={(e) => setDraftAct((prev) => ({ ...prev, number: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('actDate') ?? 'Дата'}</span>
                <input type="date" value={draftAct.date} onChange={(e) => setDraftAct((prev) => ({ ...prev, date: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm sm:col-span-2">
                <span className="mb-1 block font-medium text-slate-700">{t('actTitle') ?? 'Название'}</span>
                <input value={draftAct.title} onChange={(e) => setDraftAct((prev) => ({ ...prev, title: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('invoiceAmount') ?? 'Сумма'}</span>
                <input value={draftAct.amount} onChange={(e) => setDraftAct((prev) => ({ ...prev, amount: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm sm:col-span-2">
                <span className="mb-1 block font-medium text-slate-700">{t('actComment') ?? 'Комментарий'}</span>
                <textarea value={draftAct.comment} onChange={(e) => setDraftAct((prev) => ({ ...prev, comment: e.target.value }))} rows={2} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
            </div>
            {createActError && <div className="mt-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{createActError}</div>}
            <div className="mt-4 flex items-center justify-end gap-2">
              <button type="button" onClick={() => setCreateActOpen(false)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">{t('cancel') ?? 'Отмена'}</button>
              <button type="button" onClick={() => void handleCreateAct()} disabled={creatingAct} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-60">
                {creatingAct && <Loader2 className="h-4 w-4 animate-spin" />}
                {t('actCreate') ?? 'Создать акт'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );

  if (isPageMode) {
    return <div className="mx-auto w-full max-w-[calc(100vw-2rem)] px-4 py-8 sm:px-6 lg:px-8">{content}</div>;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 px-4 py-8">
      {content}
    </div>
  );
}
