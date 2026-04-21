import { ArrowLeft, Link2, Loader2, Trash2, X } from 'lucide-react';
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
  PaymentKind,
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
  presentation?: 'modal' | 'page';
  onBack?: () => void;
  onOpenInvoice?: (invoiceId: number, type?: PaymentKind) => void;
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
const QUANTITY_EPSILON = 0.0001;

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

function quantitiesEqual(left: number, right: number): boolean {
  return Math.abs(left - right) < QUANTITY_EPSILON;
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

function resolveCounterpartyLabel(type?: PaymentKind | null): string {
  if (type === 'Expense') {
    return 'Поставщик';
  }

  if (type === 'Income') {
    return 'Клиент';
  }

  return 'Контрагент';
}

function resolveCounterpartyClientId(invoice: Invoice | null): number | null {
  if (!invoice) {
    return null;
  }

  if (invoice.type === 'Expense') {
    return invoice.expenseClientId ?? invoice.clientId ?? null;
  }

  return invoice.clientId ?? invoice.expenseClientId ?? null;
}

function resolveCounterpartyName(invoice: Invoice | null): string {
  if (!invoice) {
    return '';
  }

  if (invoice.type === 'Expense') {
    return invoice.expenseClientName?.trim() || invoice.clientName?.trim() || '';
  }

  return invoice.clientName?.trim() || invoice.expenseClientName?.trim() || '';
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

    return {
      number: initialValues?.number ?? '',
      title: initialValues?.title ?? '',
      date: toDateInputValue(initialValues?.date ?? today) || today,
      amount:
        initialValues?.amount !== undefined && initialValues.amount !== null
          ? String(initialValues.amount)
          : '',
      creationMode: initialInvoiceId ? 'Partial' : 'Manual',
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
    creationMode: act.creationMode === 'Manual' ? 'Manual' : 'Partial',
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
  presentation = 'modal',
  onBack,
  onOpenInvoice,
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

  const isEdit = mode === 'edit';
  const isPageMode = presentation === 'page';
  const linkedInvoiceId = form.invoiceId ? Number(form.invoiceId) : null;

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
  const actType = useMemo<PaymentKind | null>(
    () => invoiceDetails?.type ?? act?.type ?? null,
    [act?.type, invoiceDetails?.type],
  );
  const counterpartyLabel = useMemo(() => resolveCounterpartyLabel(actType), [actType]);
  const linkedCounterpartyClientId = useMemo(() => resolveCounterpartyClientId(invoiceDetails), [invoiceDetails]);
  const linkedCounterpartyName = useMemo(() => resolveCounterpartyName(invoiceDetails), [invoiceDetails]);

  useEffect(() => {
    if (!open || !invoiceDetails) {
      return;
    }

    setForm((previous) => {
      let next = previous;
      let changed = false;

      if (invoiceDetails.number?.trim() && previous.invoiceNumber !== invoiceDetails.number) {
        next = { ...next, invoiceNumber: invoiceDetails.number };
        changed = true;
      }

      if (linkedCounterpartyClientId && previous.clientId !== String(linkedCounterpartyClientId)) {
        next = { ...next, clientId: String(linkedCounterpartyClientId) };
        changed = true;
      }

      if (!previous.number.trim() && invoiceDetails.number?.trim()) {
        next = { ...next, number: invoiceDetails.number };
        changed = true;
      }

      if (!previous.title.trim() && invoiceDetails.description?.trim()) {
        next = { ...next, title: invoiceDetails.description.trim() };
        changed = true;
      }

      return changed ? next : previous;
    });
  }, [linkedCounterpartyClientId, invoiceDetails, open]);

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
  const availabilityBySourceKey = useMemo(
    () => new Map(invoiceLineAvailability.map((item) => [item.sourceKey, item])),
    [invoiceLineAvailability],
  );

  useEffect(() => {
    if (!open) {
      return;
    }

    if (linkedInvoiceId && !hasInvoiceLineItems && form.creationMode !== 'Manual') {
      setForm((previous) => ({ ...previous, creationMode: 'Manual' }));
    }
  }, [form.creationMode, hasInvoiceLineItems, linkedInvoiceId, open]);

  const selectionSeedKey = useMemo(() => {
    const availabilityKey = invoiceLineAvailability
      .map((item) => `${item.sourceKey}:${item.availableQuantity}`)
      .join('|');
    const currentActKey = Array.from(currentActLineQuantities.entries())
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([sourceKey, quantity]) => `${sourceKey}:${quantity}`)
      .join('|');

    return `${open ? 1 : 0}:${linkedInvoiceId ?? 0}:${currentAct?.id ?? 0}:${availabilityKey}:${currentActKey}`;
  }, [currentAct?.id, currentActLineQuantities, invoiceLineAvailability, linkedInvoiceId, open]);

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

    const next: Record<string, string> = {};
    invoiceLineAvailability.forEach((item) => {
      if (item.availableQuantity > 0) {
        next[item.sourceKey] = formatDecimalInput(item.availableQuantity);
      }
    });
    setSelectedLineQuantities(next);
  }, [currentActLineQuantities, hasInvoiceLineItems, invoiceLineAvailability, open, selectionSeedKey]);

  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === String(form.clientId)) ?? null,
    [clients, form.clientId],
  );

  const selectedResponsible = useMemo(
    () => responsibles.find((responsible) => String(responsible.id) === String(form.responsibleId)) ?? null,
    [form.responsibleId, responsibles],
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

  const isUsingInvoiceItems = linkedInvoiceId !== null && hasInvoiceLineItems && form.creationMode !== 'Manual';

  const selectedLineItems = useMemo(() => {
    if (!isUsingInvoiceItems) {
      return [];
    }

    return invoiceLineAvailability
      .map((item) => {
        const selectedQuantity = parseDecimalInput(selectedLineQuantities[item.sourceKey] ?? '') ?? 0;

        return {
          ...item,
          selectedQuantity,
          lineAmount: selectedQuantity * item.price,
        };
      })
      .filter((item) => item.selectedQuantity > 0);
  }, [invoiceLineAvailability, isUsingInvoiceItems, selectedLineQuantities]);

  const computedAmount = useMemo(
    () => selectedLineItems.reduce((sum, item) => sum + item.lineAmount, 0),
    [selectedLineItems],
  );

  const effectiveAmount = isUsingInvoiceItems
    ? computedAmount
    : parseDecimalInput(form.amount) ?? 0;
  const amountReadonly = isUsingInvoiceItems;
  const availableLinesCount = useMemo(
    () => invoiceLineAvailability.filter((item) => item.availableQuantity > 0).length,
    [invoiceLineAvailability],
  );
  const selectedCounterpartyName = selectedClient?.name || linkedCounterpartyName || 'Не выбран';
  const selectedResponsibleLabel = selectedResponsible?.fullName
    || (form.responsibleId ? `#${form.responsibleId}` : 'Будет указан автоматически');
  const allAvailableSelected = useMemo(
    () =>
      isUsingInvoiceItems
      && invoiceLineAvailability.every((item) => {
        if (item.availableQuantity <= 0) {
          return true;
        }

        const selectedQuantity = parseDecimalInput(selectedLineQuantities[item.sourceKey] ?? '') ?? 0;
        return quantitiesEqual(selectedQuantity, item.availableQuantity);
      }),
    [invoiceLineAvailability, isUsingInvoiceItems, selectedLineQuantities],
  );

  const handleChange = (field: keyof FormState) => (
    event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const value = event.target.value;
    setForm((previous) => ({ ...previous, [field]: value }));
  };

  const handleQuantityChange = (sourceKey: string) => (event: React.ChangeEvent<HTMLInputElement>) => {
    const value = event.target.value;
    const availableQuantity = availabilityBySourceKey.get(sourceKey)?.availableQuantity ?? 0;

    if (!value.trim()) {
      setSelectedLineQuantities((previous) => ({ ...previous, [sourceKey]: '' }));
      return;
    }

    const parsed = parseDecimalInput(value);
    if (parsed === null) {
      setSelectedLineQuantities((previous) => ({ ...previous, [sourceKey]: value }));
      return;
    }

    const normalized = formatDecimalInput(Math.min(Math.max(parsed, 0), availableQuantity));
    setSelectedLineQuantities((previous) => ({ ...previous, [sourceKey]: normalized }));
  };

  const handleRemoveLineItem = (sourceKey: string) => {
    setSelectedLineQuantities((previous) => ({ ...previous, [sourceKey]: '' }));
  };

  const handleSelectAllLineItems = () => {
    const next: Record<string, string> = {};
    invoiceLineAvailability.forEach((item) => {
      if (item.availableQuantity > 0) {
        next[item.sourceKey] = formatDecimalInput(item.availableQuantity);
      }
    });
    setSelectedLineQuantities(next);
    setForm((previous) => ({ ...previous, creationMode: 'Partial' }));
  };

  const handleClearAllLineItems = () => {
    setSelectedLineQuantities({});
  };

  const handleEnableInvoiceMode = () => {
    setForm((previous) => ({ ...previous, creationMode: 'Partial' }));
    if (selectedLineItems.length === 0) {
      handleSelectAllLineItems();
    }
  };

  const handleEnableManualMode = () => {
    setForm((previous) => ({ ...previous, creationMode: 'Manual' }));
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

    if (linkedToInvoice && hasInvoiceLineItems && form.creationMode !== 'Manual') {
      lineItems = selectedLineItems.map((item) => ({
        sourceKey: item.sourceKey,
        quantity: item.selectedQuantity,
      }));

      if (lineItems.length === 0) {
        setLocalError('Выберите хотя бы одну позицию счета для акта');
        return;
      }

      creationMode = allAvailableSelected ? 'Full' : 'Partial';
      amount = computedAmount;
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
      counterpartyInn: undefined,
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

  const content = (
    <div className={`relative w-full overflow-hidden rounded-2xl bg-white ${isPageMode ? 'max-w-none border border-slate-200 shadow-sm' : 'max-w-6xl shadow-2xl'}`}>
      <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
        <div className="flex items-center gap-3">
          {isPageMode ? (
            <button
              type="button"
              onClick={onBack ?? onClose}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
            >
              <ArrowLeft className="h-4 w-4" />
              Назад
            </button>
          ) : null}
          <div>
            <h2 className="text-xl font-semibold text-slate-900">
              {isEdit ? t('actEdit') ?? 'Редактировать акт' : t('actCreate') ?? 'Создать акт'}
            </h2>
            <p className="text-sm text-slate-500">
              {linkedInvoiceId
                ? 'Акт связан со счетом: позиции подтянутся автоматически, а лишние строки можно исключить.'
                : 'Создайте пустой акт вручную или откройте создание из нужного счета.'}
            </p>
          </div>
        </div>
        {!isPageMode ? (
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Закрыть'}
          >
            <X className="h-5 w-5" />
          </button>
        ) : null}
      </div>

      <form id="act-edit-form" onSubmit={handleSubmit} className={`${isPageMode ? 'px-6 py-5' : 'max-h-[82vh] overflow-y-auto px-6 py-5'} space-y-6`}>
        <div className={isPageMode ? 'grid gap-6 xl:grid-cols-[minmax(0,1fr)_320px]' : 'space-y-6'}>
          <div className="space-y-6">
            <section className="rounded-2xl border border-slate-200 bg-slate-50/70 p-5">
              <div className="mb-4">
                <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500">Основное</h3>
              </div>
              <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
                <label className="flex flex-col gap-1 text-sm">
                  <span className="font-medium text-slate-700">
                    {t('actNumber') ?? 'Номер акта'} {isRequired('Number') ? '*' : ''}
                  </span>
                  <input
                    type="text"
                    value={form.number}
                    onChange={handleChange('number')}
                    className={`rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-2 ${
                      isRequired('Number')
                        ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                        : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'
                    }`}
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
                    className={`rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-2 ${
                      isRequired('Date')
                        ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                        : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'
                    }`}
                    required
                  />
                </label>

                <label className="flex flex-col gap-1 text-sm">
                  <span className="font-medium text-slate-700">{t('actStatus') ?? 'Статус'}</span>
                  <select
                    value={form.status}
                    onChange={handleChange('status')}
                    className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                  >
                    {statusOptions.map((option) => (
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
                    className={`rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-2 ${
                      amountReadonly
                        ? 'border-slate-200 bg-slate-100 text-slate-600 focus:border-slate-200 focus:ring-slate-100'
                        : isRequired('Amount')
                          ? 'border-red-400 focus:border-red-500 focus:ring-red-200'
                          : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'
                    }`}
                    placeholder="0.00"
                  />
                  {amountReadonly ? (
                    <span className="text-xs text-slate-500">Сумма считается по выбранным позициям счета.</span>
                  ) : null}
                </label>

                <label className="flex flex-col gap-1 text-sm xl:col-span-2">
                  <span className="font-medium text-slate-700">{t('actTitle') ?? 'Название'}</span>
                  <input
                    type="text"
                    value={form.title}
                    onChange={handleChange('title')}
                    className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                    placeholder="Например: услуги за отчетный период"
                  />
                </label>
              </div>
            </section>

            <section className="rounded-2xl border border-slate-200 bg-white p-5">
              <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                <div>
                  <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500">Основание</h3>
                  <p className="mt-1 text-sm text-slate-500">
                    {linkedInvoiceId
                      ? 'Тип акта и контрагент определяются по связанному счету.'
                      : 'Это пустой акт без привязки к счету. Контрагента можно выбрать вручную.'}
                  </p>
                </div>
                {linkedInvoiceId && hasInvoiceLineItems ? (
                  <div className="inline-flex flex-wrap items-center gap-2 rounded-full border border-slate-200 bg-slate-50 p-1 shadow-sm">
                    <button
                      type="button"
                      onClick={handleEnableInvoiceMode}
                      className={`rounded-full px-4 py-1.5 text-sm font-medium transition-colors ${
                        form.creationMode !== 'Manual'
                          ? 'bg-indigo-600 text-white shadow'
                          : 'text-slate-600 hover:bg-white'
                      }`}
                    >
                      По счету
                    </button>
                    <button
                      type="button"
                      onClick={handleEnableManualMode}
                      className={`rounded-full px-4 py-1.5 text-sm font-medium transition-colors ${
                        form.creationMode === 'Manual'
                          ? 'bg-indigo-600 text-white shadow'
                          : 'text-slate-600 hover:bg-white'
                      }`}
                    >
                      Пустой акт
                    </button>
                  </div>
                ) : null}
              </div>

              {linkedInvoiceId ? (
                <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${
                      actType === 'Expense'
                        ? 'border border-rose-200 bg-rose-50 text-rose-700'
                        : actType === 'Income'
                          ? 'border border-emerald-200 bg-emerald-50 text-emerald-700'
                          : 'border border-slate-200 bg-white text-slate-600'
                    }`}>
                      {actType === 'Expense' ? 'Расходный акт' : actType === 'Income' ? 'Доходный акт' : 'Акт без типа'}
                    </span>
                    {hasInvoiceLineItems ? (
                      <span className="rounded-full border border-slate-200 bg-white px-3 py-1 text-xs font-medium text-slate-600">
                        Доступно позиций: {availableLinesCount}
                      </span>
                    ) : null}
                  </div>

                  <div className="mt-4 grid gap-4 md:grid-cols-2">
                    <div>
                      <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Счет</div>
                      {invoiceLoading ? (
                        <div className="mt-2 inline-flex items-center gap-2 text-sm text-slate-500">
                          <Loader2 className="h-4 w-4 animate-spin" />
                          Загружаю связанный счет...
                        </div>
                      ) : invoiceDetails ? (
                        onOpenInvoice ? (
                          <button
                            type="button"
                            onClick={() => onOpenInvoice(linkedInvoiceId, actType ?? undefined)}
                            className="mt-2 inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-indigo-700 transition hover:border-indigo-200 hover:bg-indigo-50"
                          >
                            <Link2 className="h-4 w-4" />
                            Счет №{invoiceDetails.number} от {toDateInputValue(invoiceDetails.date) || invoiceDetails.date}
                          </button>
                        ) : (
                          <div className="mt-2 text-sm font-medium text-slate-900">
                            Счет №{invoiceDetails.number} от {toDateInputValue(invoiceDetails.date) || invoiceDetails.date}
                          </div>
                        )
                      ) : (
                        <div className="mt-2 text-sm text-slate-500">
                          Связанный счет будет показан после загрузки.
                        </div>
                      )}
                    </div>

                    <div>
                      <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">{counterpartyLabel}</div>
                      <div className="mt-2 text-sm font-medium text-slate-900">{selectedCounterpartyName}</div>
                      {selectedClient?.clientStatus ? (
                        <div className="mt-2">
                          <ClientStatusBadge status={selectedClient.clientStatus} />
                        </div>
                      ) : null}
                    </div>
                  </div>

                  <div className="mt-4 grid gap-4 md:grid-cols-2">
                    <div>
                      <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Ответственный</div>
                      <div className="mt-2 text-sm text-slate-700">{selectedResponsibleLabel}</div>
                    </div>
                    <div>
                      <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Режим</div>
                      <div className="mt-2 text-sm text-slate-700">
                        {form.creationMode === 'Manual'
                          ? 'Пустой акт'
                          : allAvailableSelected
                            ? 'По счету целиком'
                            : 'По счету с выбором позиций'}
                      </div>
                    </div>
                  </div>

                  <p className="mt-4 text-xs leading-5 text-slate-500">
                    Если в акт попадут не все позиции счета, следующий акт по этому счету заберет только неиспользованный остаток.
                  </p>
                </div>
              ) : (
                <div className="grid gap-4 md:grid-cols-2">
                  <label className="flex flex-col gap-1 text-sm">
                    <span className="font-medium text-slate-700">{counterpartyLabel}</span>
                    <select
                      value={form.clientId}
                      onChange={handleChange('clientId')}
                      className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                    >
                      <option value="">{t('selectClient') ?? 'Выберите контрагента'}</option>
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

                  <div className="rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600">
                    <div className="font-medium text-slate-700">Ответственный</div>
                    <div className="mt-2">{selectedResponsibleLabel}</div>
                    <p className="mt-3 text-xs leading-5 text-slate-500">
                      Ответственный подставляется автоматически как пользователь, который создает акт.
                    </p>
                  </div>
                </div>
              )}

              <label className="mt-4 flex flex-col gap-1 text-sm">
                <span className="font-medium text-slate-700">{t('actComment') ?? 'Комментарий'}</span>
                <textarea
                  value={form.comment}
                  onChange={handleChange('comment')}
                  rows={3}
                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                  placeholder="Короткое пояснение по акту"
                />
              </label>
            </section>

            {hasInvoiceLineItems ? (
              <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white">
                <div className="border-b border-slate-200 bg-slate-50 px-4 py-4">
                  <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                    <div>
                      <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500">Позиции счета</h3>
                      <p className="mt-1 text-sm text-slate-500">
                        Выберите позиции, которые должны попасть в этот акт. Лишние строки можно исключить.
                      </p>
                    </div>
                    <div className="flex flex-wrap gap-2">
                      <button
                        type="button"
                        onClick={handleSelectAllLineItems}
                        className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                      >
                        Выбрать все
                      </button>
                      <button
                        type="button"
                        onClick={handleClearAllLineItems}
                        className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                      >
                        Очистить
                      </button>
                    </div>
                  </div>
                </div>

                {form.creationMode === 'Manual' ? (
                  <div className="px-4 py-5">
                    <div className="rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-4 text-sm text-slate-600">
                      Позиции счета пока не используются, потому что выбран режим пустого акта.
                      <button
                        type="button"
                        onClick={handleEnableInvoiceMode}
                        className="mt-3 inline-flex items-center rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white transition hover:bg-indigo-500"
                      >
                        Вернуть позиции счета
                      </button>
                    </div>
                  </div>
                ) : (
                  <>
                    <div className="overflow-x-auto">
                      <table className="min-w-full divide-y divide-slate-200 text-sm">
                        <thead className="bg-white">
                          <tr className="text-left text-xs uppercase tracking-wide text-slate-500">
                            <th className="px-4 py-3 font-medium">Позиция</th>
                            <th className="px-4 py-3 text-right font-medium">Всего</th>
                            <th className="px-4 py-3 text-right font-medium">Уже в актах</th>
                            <th className="px-4 py-3 text-right font-medium">Остаток</th>
                            <th className="px-4 py-3 text-right font-medium">В этот акт</th>
                            <th className="px-4 py-3 text-right font-medium">Цена</th>
                            <th className="px-4 py-3 text-right font-medium">Сумма</th>
                            <th className="px-4 py-3 text-right font-medium">Действие</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 bg-white">
                          {invoiceLineAvailability.map((item) => {
                            const selectedQuantity = parseDecimalInput(selectedLineQuantities[item.sourceKey] ?? '') ?? 0;
                            const lineAmount = selectedQuantity * item.price;
                            const disabled = item.availableQuantity <= 0;

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
                                    value={selectedLineQuantities[item.sourceKey] ?? ''}
                                    onChange={handleQuantityChange(item.sourceKey)}
                                    disabled={disabled}
                                    className={`w-28 rounded-lg border px-3 py-2 text-right text-sm focus:outline-none focus:ring-2 ${
                                      disabled
                                        ? 'border-slate-200 bg-slate-50 text-slate-400 focus:border-slate-200 focus:ring-slate-100'
                                        : 'border-slate-300 focus:border-indigo-500 focus:ring-indigo-200'
                                    }`}
                                  />
                                </td>
                                <td className="px-4 py-3 text-right text-slate-700">{formatCurrencySmart(item.price).full}</td>
                                <td className="px-4 py-3 text-right font-medium text-slate-900">{formatCurrencySmart(lineAmount).full}</td>
                                <td className="px-4 py-3 text-right">
                                  <button
                                    type="button"
                                    onClick={() => handleRemoveLineItem(item.sourceKey)}
                                    disabled={disabled || selectedQuantity <= 0}
                                    className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-500 transition hover:border-rose-200 hover:bg-rose-50 hover:text-rose-600 disabled:cursor-not-allowed disabled:opacity-40"
                                    title="Исключить позицию из акта"
                                    aria-label="Исключить позицию из акта"
                                  >
                                    <Trash2 className="h-4 w-4" />
                                  </button>
                                </td>
                              </tr>
                            );
                          })}
                        </tbody>
                      </table>
                    </div>

                    <div className="flex flex-col gap-2 border-t border-slate-200 bg-slate-50 px-4 py-4 text-sm sm:flex-row sm:items-center sm:justify-between">
                      <div className="text-slate-500">
                        Выбрано позиций: <span className="font-medium text-slate-900">{selectedLineItems.length}</span>
                      </div>
                      <div className="text-base font-semibold text-slate-900">
                        Итого по акту: {formatCurrencySmart(effectiveAmount).full}
                      </div>
                    </div>
                  </>
                )}
              </section>
            ) : linkedInvoiceId ? (
              <section className="rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-4 text-sm text-slate-600">
                У этого счета пока нет строк, поэтому акт можно сохранить только как пустой.
              </section>
            ) : null}
          </div>

          {isPageMode ? (
            <aside className="space-y-4">
              <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Сводка</div>
                <div className="mt-3 text-2xl font-semibold text-slate-900">{formatCurrencySmart(effectiveAmount).full}</div>
                <div className="mt-2 text-sm text-slate-500">
                  {isUsingInvoiceItems
                    ? `Позиции в акте: ${selectedLineItems.length}`
                    : 'Сумма указана вручную'}
                </div>
              </div>

              <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm">
                <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Тип акта</div>
                <div className="mt-3">
                  <span className={`inline-flex items-center rounded-full px-3 py-1 text-xs font-semibold ${
                    actType === 'Expense'
                      ? 'border border-rose-200 bg-rose-50 text-rose-700'
                      : actType === 'Income'
                        ? 'border border-emerald-200 bg-emerald-50 text-emerald-700'
                        : 'border border-slate-200 bg-slate-50 text-slate-600'
                  }`}>
                    {actType === 'Expense' ? 'Расходный' : actType === 'Income' ? 'Доходный' : 'Без типа'}
                  </span>
                </div>
              </div>

              <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm text-sm">
                <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">{counterpartyLabel}</div>
                <div className="mt-2 font-medium text-slate-900">{selectedCounterpartyName}</div>
                {selectedClient?.clientStatus ? (
                  <div className="mt-3">
                    <ClientStatusBadge status={selectedClient.clientStatus} />
                  </div>
                ) : null}
              </div>

              <div className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm text-sm">
                <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Ответственный</div>
                <div className="mt-2 text-slate-700">{selectedResponsibleLabel}</div>
              </div>
            </aside>
          ) : null}
        </div>

        {(lookupsLoading || invoiceLoading || actDetailsLoading) && (
          <div className="flex items-center gap-2 text-sm text-slate-500">
            <Loader2 className="h-4 w-4 animate-spin" />
            <span>{t('loading') ?? 'Загрузка...'}...</span>
          </div>
        )}

        {(lookupsError || invoiceError || actDetailsError) && (
          <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700">
            {lookupsError || invoiceError || actDetailsError}
          </div>
        )}

        {(localError || errorMessage) && (
          <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
            {localError || errorMessage}
          </div>
        )}

        <div className="flex flex-col-reverse gap-3 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={isPageMode ? (onBack ?? onClose) : onClose}
            className="inline-flex items-center justify-center rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 transition-colors hover:bg-slate-100"
          >
            {t('cancel') ?? 'Отмена'}
          </button>
          <button
            type="submit"
            disabled={submitting || invoiceLoading || actDetailsLoading}
            className="inline-flex items-center justify-center rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-indigo-500 disabled:cursor-not-allowed disabled:bg-indigo-300"
          >
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
  );

  if (isPageMode) {
    return <div className="w-full px-4 py-6 sm:px-6 lg:px-8">{content}</div>;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 px-4 py-8">
      {content}
    </div>
  );
}
