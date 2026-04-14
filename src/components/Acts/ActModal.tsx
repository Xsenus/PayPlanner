import { Loader2, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { apiService } from '../../services/api';
import type {
  Act,
  ActCreationMode,
  ActInput,
  ActLineItem,
  ActResponsible,
  ActStatus,
  Client,
  Invoice,
  InvoiceLineItem,
} from '../../types';
import { useTranslation } from '../../hooks/useTranslation';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import { fromInputToApiDate, toDateInputValue } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';

interface ActModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  act: Act | null;
  onClose: () => void;
  onSubmit: (payload: ActInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  responsibles: ActResponsible[];
  lookupsLoading: boolean;
  lookupsError?: string | null;
  defaultClientId?: number | null;
  defaultResponsibleId?: number | null;
  initialValues?: Partial<ActInput> & { amount?: number | null };
}

type FormState = {
  number: string;
  title: string;
  date: string;
  amount: string;
  creationMode: ActCreationMode;
  invoiceNumber: string;
  invoiceId: string;
  counterpartyInn: string;
  status: ActStatus;
  clientId: string;
  responsibleId: string;
  comment: string;
};

type InvoiceLineAvailabilityRow = {
  invoiceLineItemId?: number | null;
  sourceKey: string;
  sortOrder: number;
  name: string;
  unit: string;
  price: number;
  totalQuantity: number;
  usedByOtherActsQuantity: number;
  availableQuantity: number;
};

const STATUS_VALUES: ActStatus[] = ['Created', 'Transferred', 'Signed', 'Terminated'];

function parseDecimalInput(value: string): number | null {
  const normalized = value.replace(/\s+/g, '').replace(',', '.').trim();
  if (!normalized) {
    return null;
  }

  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function formatDecimalInput(value: number): string {
  if (!Number.isFinite(value)) {
    return '';
  }

  const rounded = Math.round(value * 10_000) / 10_000;
  return String(rounded).replace(/(\.\d*?[1-9])0+$/u, '$1').replace(/\.0+$/u, '');
}

function buildInvoiceLineSourceKey(item: InvoiceLineItem, index: number): string {
  const sourceKey = item.sourceKey?.trim();
  if (sourceKey) {
    return sourceKey;
  }

  if (item.id) {
    return `db:${item.id}`;
  }

  if (item.sortOrder !== undefined && item.sortOrder !== null) {
    return `legacy:${item.sortOrder}`;
  }

  return `legacy:auto-${index}`;
}

function sumLineItemQuantities(lineItems?: ActLineItem[] | null): Map<string, number> {
  const quantities = new Map<string, number>();
  (lineItems ?? []).forEach((item) => {
    const sourceKey = item.sourceKey?.trim();
    if (!sourceKey) {
      return;
    }

    quantities.set(sourceKey, (quantities.get(sourceKey) ?? 0) + Number(item.quantity ?? 0));
  });

  return quantities;
}

function normalizeActToForm(
  act: Act | null,
  defaultClientId?: number | null,
  defaultResponsibleId?: number | null,
  initialValues?: Partial<ActInput> & { amount?: number | null },
): FormState {
  if (!act) {
    const today = toDateInputValue(new Date());
    const initialInvoiceId = initialValues?.invoiceId ? String(initialValues.invoiceId) : '';
    const initialCreationMode = initialValues?.creationMode ?? (initialInvoiceId ? 'Full' : 'Manual');

    return {
      number: initialValues?.number ?? '',
      title: initialValues?.title ?? '',
      date: toDateInputValue(initialValues?.date ?? today) || today,
      amount:
        initialValues?.amount !== undefined && initialValues.amount !== null
          ? String(initialValues.amount)
          : '',
      creationMode: initialCreationMode,
      invoiceNumber: initialValues?.invoiceNumber ?? '',
      invoiceId: initialInvoiceId,
      counterpartyInn: initialValues?.counterpartyInn ?? '',
      status: (initialValues?.status as ActStatus) ?? 'Created',
      clientId: initialValues?.clientId
        ? String(initialValues.clientId)
        : defaultClientId
          ? String(defaultClientId)
          : '',
      responsibleId: initialValues?.responsibleId
        ? String(initialValues.responsibleId)
        : defaultResponsibleId
          ? String(defaultResponsibleId)
          : '',
      comment: initialValues?.comment ?? '',
    };
  }

  return {
    number: act.number ?? '',
    title: act.title ?? '',
    date: toDateInputValue(act.date) || toDateInputValue(new Date()),
    amount: act.amount !== undefined && act.amount !== null ? String(act.amount) : '',
    creationMode: act.creationMode ?? (act.invoiceId ? 'Full' : 'Manual'),
    invoiceNumber: act.invoiceNumber ?? '',
    invoiceId: act.invoiceId ? String(act.invoiceId) : '',
    counterpartyInn: act.counterpartyInn ?? '',
    status: act.status,
    clientId: act.clientId ? String(act.clientId) : defaultClientId ? String(defaultClientId) : '',
    responsibleId: act.responsibleId
      ? String(act.responsibleId)
      : defaultResponsibleId
        ? String(defaultResponsibleId)
        : '',
    comment: act.comment ?? '',
  };
}

export function ActModal({
  open,
  mode,
  act,
  onClose,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  responsibles,
  lookupsLoading,
  lookupsError,
  defaultClientId,
  defaultResponsibleId,
  initialValues,
}: ActModalProps) {
  const { t } = useTranslation();
  const { isRequired } = useFormRequirements('acts', open);
  const [form, setForm] = useState<FormState>(() =>
    normalizeActToForm(act, defaultClientId, defaultResponsibleId, initialValues),
  );
  const [localError, setLocalError] = useState<string | null>(null);
  const [invoiceDetails, setInvoiceDetails] = useState<Invoice | null>(null);
  const [invoiceLoading, setInvoiceLoading] = useState(false);
  const [invoiceError, setInvoiceError] = useState<string | null>(null);
  const [actDetails, setActDetails] = useState<Act | null>(null);
  const [actDetailsLoading, setActDetailsLoading] = useState(false);
  const [actDetailsError, setActDetailsError] = useState<string | null>(null);
  const [selectedLineQuantities, setSelectedLineQuantities] = useState<Record<string, string>>({});
  const selectionSeedRef = useRef<string | null>(null);

  useEffect(() => {
    if (open) {
      setForm(normalizeActToForm(act, defaultClientId, defaultResponsibleId, initialValues));
      setLocalError(null);
      setInvoiceDetails(null);
      setInvoiceError(null);
      setActDetails(null);
      setActDetailsError(null);
      setSelectedLineQuantities({});
      selectionSeedRef.current = null;
    }
  }, [open, act, defaultClientId, defaultResponsibleId, initialValues]);

  const isEdit = mode === 'edit';
  const linkedInvoiceId = form.invoiceId ? Number(form.invoiceId) : null;

  useEffect(() => {
    if (!open || !linkedInvoiceId) {
      setInvoiceDetails(null);
      setInvoiceError(null);
      setInvoiceLoading(false);
      return;
    }

    let cancelled = false;
    setInvoiceLoading(true);
    setInvoiceError(null);

    void apiService
      .getInvoice(linkedInvoiceId)
      .then((invoice) => {
        if (cancelled) {
          return;
        }

        setInvoiceDetails(invoice);
      })
      .catch((error) => {
        if (cancelled) {
          return;
        }

        setInvoiceDetails(null);
        setInvoiceError(extractDisplayErrorMessage(error, 'Не удалось загрузить счет для акта'));
      })
      .finally(() => {
        if (!cancelled) {
          setInvoiceLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [open, linkedInvoiceId]);

  useEffect(() => {
    if (!open || !isEdit || !act?.id) {
      setActDetails(null);
      setActDetailsError(null);
      setActDetailsLoading(false);
      return;
    }

    let cancelled = false;
    setActDetailsLoading(true);
    setActDetailsError(null);

    void apiService
      .getAct(act.id)
      .then((value) => {
        if (cancelled) {
          return;
        }

        setActDetails(value);
      })
      .catch((error) => {
        if (cancelled) {
          return;
        }

        setActDetails(act);
        setActDetailsError(extractDisplayErrorMessage(error, 'Не удалось загрузить позиции акта'));
      })
      .finally(() => {
        if (!cancelled) {
          setActDetailsLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [open, isEdit, act]);

  const currentAct = useMemo(() => actDetails ?? act, [act, actDetails]);

  const invoiceLineAvailability = useMemo<InvoiceLineAvailabilityRow[]>(() => {
    const sourceItems = invoiceDetails?.lineItems ?? [];
    if (sourceItems.length === 0) {
      return [];
    }

    const currentActId = currentAct?.id ?? null;
    const usedByOtherActs = new Map<string, number>();

    (invoiceDetails?.acts ?? []).forEach((invoiceAct) => {
      if (invoiceAct.status === 'Terminated') {
        return;
      }

      if (currentActId && invoiceAct.id === currentActId) {
        return;
      }

      (invoiceAct.lineItems ?? []).forEach((lineItem) => {
        const sourceKey = lineItem.sourceKey?.trim();
        if (!sourceKey) {
          return;
        }

        usedByOtherActs.set(sourceKey, (usedByOtherActs.get(sourceKey) ?? 0) + Number(lineItem.quantity ?? 0));
      });
    });

    return sourceItems
      .map((item, index) => {
        const sourceKey = buildInvoiceLineSourceKey(item, index);
        const totalQuantity = Number(item.quantity ?? 0);
        const usedQuantity = usedByOtherActs.get(sourceKey) ?? 0;
        const availableQuantity = Math.max(0, totalQuantity - usedQuantity);

        return {
          invoiceLineItemId: item.id,
          sourceKey,
          sortOrder: item.sortOrder ?? index,
          name: item.name,
          unit: item.unit,
          price: Number(item.price ?? 0),
          totalQuantity,
          usedByOtherActsQuantity: usedQuantity,
          availableQuantity,
        };
      })
      .sort((left, right) => left.sortOrder - right.sortOrder);
  }, [currentAct?.id, invoiceDetails]);

  const currentActLineQuantities = useMemo(() => sumLineItemQuantities(currentAct?.lineItems), [currentAct?.lineItems]);
  const hasInvoiceLineItems = invoiceLineAvailability.length > 0;

  useEffect(() => {
    if (!open) {
      return;
    }

    if (hasInvoiceLineItems) {
      if (mode === 'create' && form.creationMode === 'Manual') {
        setForm((prev) => ({ ...prev, creationMode: 'Full' }));
      }
      return;
    }

    if (form.creationMode !== 'Manual') {
      setForm((prev) => ({ ...prev, creationMode: 'Manual' }));
    }
  }, [form.creationMode, hasInvoiceLineItems, mode, open]);

  const selectionSeedKey = useMemo(() => {
    const availabilityKey = invoiceLineAvailability
      .map((item) => `${item.sourceKey}:${item.availableQuantity}`)
      .join('|');
    const currentActKey = Array.from(currentActLineQuantities.entries())
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([sourceKey, quantity]) => `${sourceKey}:${quantity}`)
      .join('|');

    return `${open ? 1 : 0}:${linkedInvoiceId ?? 0}:${form.creationMode}:${currentAct?.id ?? 0}:${availabilityKey}:${currentActKey}`;
  }, [currentAct?.id, currentActLineQuantities, form.creationMode, invoiceLineAvailability, linkedInvoiceId, open]);

  useEffect(() => {
    if (!open) {
      selectionSeedRef.current = null;
      return;
    }

    if (!hasInvoiceLineItems) {
      selectionSeedRef.current = null;
      setSelectedLineQuantities({});
      return;
    }

    if (selectionSeedRef.current === selectionSeedKey) {
      return;
    }

    selectionSeedRef.current = selectionSeedKey;

    if (form.creationMode === 'Full') {
      const next: Record<string, string> = {};
      invoiceLineAvailability.forEach((item) => {
        if (item.availableQuantity > 0) {
          next[item.sourceKey] = formatDecimalInput(item.availableQuantity);
        }
      });
      setSelectedLineQuantities(next);
      return;
    }

    if (form.creationMode === 'Partial') {
      if (currentActLineQuantities.size > 0) {
        const next: Record<string, string> = {};
        invoiceLineAvailability.forEach((item) => {
          const quantity = Math.min(item.availableQuantity, currentActLineQuantities.get(item.sourceKey) ?? 0);
          if (quantity > 0) {
            next[item.sourceKey] = formatDecimalInput(quantity);
          }
        });
        setSelectedLineQuantities(next);
        return;
      }

      setSelectedLineQuantities((previous) => {
        const next: Record<string, string> = {};
        invoiceLineAvailability.forEach((item) => {
          const parsed = parseDecimalInput(previous[item.sourceKey] ?? '');
          if (parsed && parsed > 0) {
            next[item.sourceKey] = formatDecimalInput(Math.min(item.availableQuantity, parsed));
          }
        });
        return next;
      });
      return;
    }

    setSelectedLineQuantities({});
  }, [form.creationMode, hasInvoiceLineItems, invoiceLineAvailability, currentActLineQuantities, open, selectionSeedKey]);

  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === String(form.clientId)) ?? null,
    [clients, form.clientId],
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

  const creationModeOptions = useMemo(() => {
    const options: Array<{ value: ActCreationMode; label: string }> = [];
    if (!hasInvoiceLineItems || form.creationMode === 'Manual') {
      options.push({ value: 'Manual', label: 'Ручной акт' });
    }
    if (hasInvoiceLineItems) {
      options.push({ value: 'Full', label: 'Полностью по счету' });
      options.push({ value: 'Partial', label: 'Частично по счету' });
    }
    return options;
  }, [form.creationMode, hasInvoiceLineItems]);

  const selectedLineItems = useMemo(() => {
    if (!hasInvoiceLineItems || form.creationMode === 'Manual') {
      return [];
    }

    return invoiceLineAvailability
      .map((item) => {
        const selectedQuantity =
          form.creationMode === 'Full'
            ? item.availableQuantity
            : parseDecimalInput(selectedLineQuantities[item.sourceKey] ?? '') ?? 0;

        return {
          ...item,
          selectedQuantity,
          lineAmount: selectedQuantity * item.price,
        };
      })
      .filter((item) => item.selectedQuantity > 0);
  }, [form.creationMode, hasInvoiceLineItems, invoiceLineAvailability, selectedLineQuantities]);

  const computedAmount = useMemo(
    () => selectedLineItems.reduce((sum, item) => sum + item.lineAmount, 0),
    [selectedLineItems],
  );

  const effectiveAmount = form.creationMode === 'Manual' || !hasInvoiceLineItems
    ? parseDecimalInput(form.amount) ?? 0
    : computedAmount;
  const amountReadonly = hasInvoiceLineItems && form.creationMode !== 'Manual';
  const availableLinesCount = useMemo(
    () => invoiceLineAvailability.filter((item) => item.availableQuantity > 0).length,
    [invoiceLineAvailability],
  );

  const handleChange = (field: keyof FormState) => (
    event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const value = event.target.value;
    setForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleQuantityChange = (sourceKey: string) => (event: React.ChangeEvent<HTMLInputElement>) => {
    const value = event.target.value;
    setSelectedLineQuantities((prev) => ({ ...prev, [sourceKey]: value }));
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    if (!form.number.trim()) {
      setLocalError('Номер акта обязателен');
      return;
    }

    if (!form.date) {
      setLocalError('Дата акта обязательна');
      return;
    }

    const linkedToInvoice = Boolean(linkedInvoiceId);
    let amount = 0;
    let creationMode: ActCreationMode = form.creationMode;
    let lineItems: Pick<ActLineItem, 'sourceKey' | 'quantity'>[] = [];

    if (linkedToInvoice && hasInvoiceLineItems) {
      if (form.creationMode === 'Manual') {
        const parsedAmount = parseDecimalInput(form.amount);
        if (parsedAmount === null) {
          setLocalError('Введите корректную сумму');
          return;
        }

        amount = parsedAmount;
      } else {
        creationMode = form.creationMode === 'Partial' ? 'Partial' : 'Full';
        lineItems = selectedLineItems.map((item) => ({
          sourceKey: item.sourceKey,
          quantity: item.selectedQuantity,
        }));

        if (lineItems.length === 0) {
          setLocalError(
            creationMode === 'Partial'
              ? 'Для частичного акта выберите хотя бы одну позицию счета'
              : 'По счету не осталось доступных позиций для акта',
          );
          return;
        }

        amount = computedAmount;
      }
    } else {
      creationMode = 'Manual';
      const parsedAmount = parseDecimalInput(form.amount);
      if (parsedAmount === null) {
        setLocalError('Введите корректную сумму');
        return;
      }

      amount = parsedAmount;
    }

    const payload: ActInput = {
      number: form.number.trim(),
      title: form.title.trim() ? form.title.trim() : undefined,
      date: fromInputToApiDate(form.date) ?? form.date,
      amount,
      creationMode,
      invoiceNumber: form.invoiceNumber.trim() ? form.invoiceNumber.trim() : undefined,
      invoiceId: linkedInvoiceId ?? undefined,
      counterpartyInn: form.counterpartyInn.trim() ? form.counterpartyInn.trim() : undefined,
      status: form.status,
      clientId: form.clientId ? Number(form.clientId) : undefined,
      responsibleId: form.responsibleId ? Number(form.responsibleId) : undefined,
      comment: form.comment.trim() ? form.comment.trim() : undefined,
      lineItems,
    };

    try {
      await onSubmit(payload);
    } catch (error) {
      if (error instanceof Error && !errorMessage) {
        setLocalError(error.message);
      }
    }
  };

  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 px-4 py-8">
      <div className="relative w-full max-w-6xl overflow-hidden rounded-2xl bg-white shadow-2xl">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <div>
            <h2 className="text-xl font-semibold text-slate-900">
              {isEdit ? t('actEdit') ?? 'Редактировать акт' : t('actCreate') ?? 'Создать акт'}
            </h2>
            <p className="text-sm text-slate-500">
              {hasInvoiceLineItems
                ? 'Можно выставить акт полностью по счету или выбрать только выполненные позиции.'
                : t('actModalSubtitle') ?? 'Создайте или обновите данные по акту'}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Закрыть'}>
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="max-h-[82vh] overflow-y-auto px-6 py-5">
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">
                {t('actNumber') ?? 'Номер акта'} {isRequired('Number') ? '*' : ''}
              </span>
              <input
                type="text"
                value={form.number}
                onChange={handleChange('number')}
                className={`rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-2 ${isRequired('Number') ? 'border-red-400 focus:border-red-500 focus:ring-red-200' : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'}`}
                required
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">
                {t('actDate') ?? 'Дата'} {isRequired('Date') ? '*' : ''}
              </span>
              <input
                type="date"
                value={form.date}
                onChange={handleChange('date')}
                className={`rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-2 ${isRequired('Date') ? 'border-red-400 focus:border-red-500 focus:ring-red-200' : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'}`}
                required
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">Режим акта</span>
              <select
                value={form.creationMode}
                onChange={handleChange('creationMode')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
                {creationModeOptions.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">
                {t('actAmount') ?? 'Сумма'} {isRequired('Amount') ? '*' : ''}
              </span>
              <input
                type="number"
                step="0.01"
                value={amountReadonly ? formatDecimalInput(effectiveAmount) : form.amount}
                onChange={handleChange('amount')}
                readOnly={amountReadonly}
                className={`rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-2 ${amountReadonly ? 'border-slate-200 bg-slate-50 text-slate-600 focus:border-slate-200 focus:ring-slate-100' : isRequired('Amount') ? 'border-red-400 focus:border-red-500 focus:ring-red-200' : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'}`}
                placeholder="0.00"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actInvoice') ?? 'Счет'}</span>
              <input
                type="text"
                value={form.invoiceNumber}
                onChange={handleChange('invoiceNumber')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">ID счета</span>
              <input
                type="number"
                value={form.invoiceId}
                onChange={handleChange('invoiceId')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm xl:col-span-3">
              <span className="font-medium text-slate-700">{t('actTitle') ?? 'Название'}</span>
              <input
                type="text"
                value={form.title}
                onChange={handleChange('title')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actClient') ?? 'Контрагент'}</span>
              <select
                value={form.clientId}
                onChange={handleChange('clientId')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
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
              <input
                type="text"
                value={form.counterpartyInn}
                onChange={handleChange('counterpartyInn')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actStatus') ?? 'Статус'}</span>
              <select
                value={form.status}
                onChange={handleChange('status')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
                {statusOptions.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1 text-sm">
              <span className="font-medium text-slate-700">{t('actResponsible') ?? 'Ответственный'}</span>
              <select
                value={form.responsibleId}
                onChange={handleChange('responsibleId')}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200">
                <option value="">{t('actResponsibleNotSelected') ?? 'Ответственный не выбран'}</option>
                {responsibles.map((employee) => (
                  <option key={employee.id} value={employee.id}>
                    {employee.fullName}
                  </option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1 text-sm xl:col-span-2">
              <span className="font-medium text-slate-700">{t('actComment') ?? 'Комментарий'}</span>
              <textarea
                value={form.comment}
                onChange={handleChange('comment')}
                rows={3}
                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
              />
            </label>
          </div>

          {linkedInvoiceId ? (
            <div className="mt-5 rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-700">
              <div className="flex flex-wrap items-center gap-3">
                <span className="font-medium">Счет привязан к акту.</span>
                {invoiceLoading ? (
                  <span className="inline-flex items-center gap-2 text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Загружаю позиции счета...
                  </span>
                ) : invoiceDetails ? (
                  <span>
                    №{invoiceDetails.number} от {toDateInputValue(invoiceDetails.date) || invoiceDetails.date}
                  </span>
                ) : null}
                {hasInvoiceLineItems ? (
                  <span className="rounded-full bg-white px-2.5 py-1 text-xs font-medium text-slate-600 shadow-sm">
                    Доступно позиций: {availableLinesCount}
                  </span>
                ) : null}
              </div>
              {hasInvoiceLineItems ? (
                <p className="mt-2 text-xs text-slate-500">
                  Для последующих актов будут доступны только те позиции и количества, которые еще не закрыты предыдущими актами.
                </p>
              ) : (
                <p className="mt-2 text-xs text-slate-500">
                  У этого счета пока нет позиций, поэтому акт можно сохранить только в ручном режиме.
                </p>
              )}
            </div>
          ) : null}

          {hasInvoiceLineItems ? (
            <div className="mt-5 overflow-hidden rounded-2xl border border-slate-200">
              <div className="border-b border-slate-200 bg-slate-50 px-4 py-3">
                <h3 className="text-sm font-semibold text-slate-900">Позиции счета для акта</h3>
                <p className="mt-1 text-xs text-slate-500">
                  {form.creationMode === 'Full'
                    ? 'В полном режиме в акт автоматически попадет весь доступный остаток по счету.'
                    : form.creationMode === 'Partial'
                      ? 'В частичном режиме укажи количество по тем позициям, которые выполнены сейчас.'
                      : 'Ручной режим оставлен только для совместимости со старыми актами.'}
                </p>
              </div>

              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-slate-200 text-sm">
                  <thead className="bg-white">
                    <tr className="text-left text-xs uppercase tracking-wide text-slate-500">
                      <th className="px-4 py-3 font-medium">Услуга</th>
                      <th className="px-4 py-3 text-right font-medium">Всего</th>
                      <th className="px-4 py-3 text-right font-medium">Уже в актах</th>
                      <th className="px-4 py-3 text-right font-medium">Доступно</th>
                      <th className="px-4 py-3 text-right font-medium">В этот акт</th>
                      <th className="px-4 py-3 text-right font-medium">Цена</th>
                      <th className="px-4 py-3 text-right font-medium">Сумма</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 bg-white">
                    {invoiceLineAvailability.map((item) => {
                      const selectedQuantity =
                        form.creationMode === 'Full'
                          ? item.availableQuantity
                          : parseDecimalInput(selectedLineQuantities[item.sourceKey] ?? '') ?? 0;
                      const lineAmount = selectedQuantity * item.price;
                      const disabled = form.creationMode === 'Full' || item.availableQuantity <= 0;

                      return (
                        <tr key={item.sourceKey} className="align-top">
                          <td className="px-4 py-3">
                            <div className="font-medium text-slate-900">{item.name}</div>
                            <div className="mt-1 text-xs text-slate-500">{item.unit}</div>
                          </td>
                          <td className="px-4 py-3 text-right text-slate-700">{formatDecimalInput(item.totalQuantity)}</td>
                          <td className="px-4 py-3 text-right text-slate-500">{formatDecimalInput(item.usedByOtherActsQuantity)}</td>
                          <td className="px-4 py-3 text-right font-medium text-slate-900">{formatDecimalInput(item.availableQuantity)}</td>
                          <td className="px-4 py-3 text-right">
                            <input
                              type="number"
                              min="0"
                              step="0.0001"
                              max={formatDecimalInput(item.availableQuantity)}
                              value={
                                form.creationMode === 'Full'
                                  ? formatDecimalInput(item.availableQuantity)
                                  : selectedLineQuantities[item.sourceKey] ?? ''
                              }
                              onChange={handleQuantityChange(item.sourceKey)}
                              readOnly={form.creationMode === 'Full'}
                              disabled={disabled}
                              className={`w-28 rounded-lg border px-3 py-2 text-right text-sm focus:outline-none focus:ring-2 ${disabled ? 'border-slate-200 bg-slate-50 text-slate-500 focus:border-slate-200 focus:ring-slate-100' : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'}`}
                            />
                          </td>
                          <td className="px-4 py-3 text-right text-slate-700">{formatCurrencySmart(item.price).full}</td>
                          <td className="px-4 py-3 text-right font-medium text-slate-900">{formatCurrencySmart(lineAmount).full}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>

              <div className="flex flex-col gap-2 border-t border-slate-200 bg-slate-50 px-4 py-3 text-sm sm:flex-row sm:items-center sm:justify-between">
                <div className="text-slate-500">
                  Выбрано позиций: <span className="font-medium text-slate-900">{selectedLineItems.length}</span>
                </div>
                <div className="text-base font-semibold text-slate-900">
                  Итого по акту: {formatCurrencySmart(effectiveAmount).full}
                </div>
              </div>
            </div>
          ) : null}

          {(lookupsLoading || invoiceLoading || actDetailsLoading) && (
            <div className="mt-4 flex items-center gap-2 text-sm text-slate-500">
              <Loader2 className="h-4 w-4 animate-spin" />
              <span>{t('loading') ?? 'Загрузка...'}...</span>
            </div>
          )}

          {(lookupsError || invoiceError || actDetailsError) && (
            <div className="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
              {lookupsError || invoiceError || actDetailsError}
            </div>
          )}

          {(localError || errorMessage) && (
            <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {localError || errorMessage}
            </div>
          )}

          <div className="mt-6 flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
            <button
              type="button"
              onClick={onClose}
              className="inline-flex items-center justify-center rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-100">
              {t('cancel') ?? 'Отмена'}
            </button>
            <button
              type="submit"
              disabled={submitting || invoiceLoading || actDetailsLoading}
              className="inline-flex items-center justify-center rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-indigo-500 disabled:cursor-not-allowed disabled:bg-indigo-300">
              {submitting && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
              {submitting
                ? isEdit
                  ? t('saving') ?? 'Сохранение...'
                  : t('adding') ?? 'Добавление...'
                : isEdit
                  ? t('save') ?? 'Сохранить'
                  : t('create') ?? 'Создать'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
