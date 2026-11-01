import { hasStorableLineItemPrecision, lineItemPrecisionError, roundedLineTotal, sumLineAmounts } from '../../utils/lineItemMoney';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { ArrowLeft, ChevronDown, Link2, Loader2, X } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { apiService } from '../../services/api';
import type {
  Act,
  ActCreationMode,
  ActInput,
  ActLineItem,
  ActResponsible,
  ActStatus,
  ActWorkflowStatus,
  Client,
  Invoice,
  InvoiceLineItem,
  LegalEntitySummary,
  PaymentKind,
} from '../../types';
import { useTranslation } from '../../hooks/useTranslation';
import { useFormRequirements } from '../../hooks/useFormRequirements';
import { useDocumentStatusSettings } from '../../hooks/useDocumentStatusSettings';
import { fromInputToApiDate, todayYMDInTimeZone, toDateInputValue } from '../../utils/dateUtils';
import { getDocumentStatusCodes, getDocumentStatusLabels } from '../../utils/documentStatusSettings';
import { extractDisplayErrorMessage, isHttpErrorStatus } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { LookupSelect, type LookupSelectOption } from '../ui/LookupSelect';
import { CounterpartyPickerModal, type CounterpartySelection } from '../ui/CounterpartyPickerModal';
import { useLegalEntities } from '../../hooks/useLegalEntities';
import { useUserTimeZone } from '../../hooks/useUserTimeZone';
import { VatRateSelect } from '../Documents/VatRateSelect';
import { DocumentSellerPicker } from '../Documents/DocumentSellerPicker';

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
  invoiceSeed?: Invoice | null;
  presentation?: 'modal' | 'page';
  onBack?: () => void;
  onOpenInvoice?: (invoiceId: number, type?: PaymentKind) => void;
  canViewInvoices?: boolean;
  accessScopeKey?: string;
  onActUnavailable?: (actId: number, status: 401 | 403 | 404) => void;
}

type ActReadState = { scope: string; kind: 'loading' | 'ready' | 'retry' | 'unavailable'; status?: 401 | 403 | 404 };

function actUnavailableMessage(status: 401 | 403 | 404): string {
  if (status === 401) return 'Сеанс истёк. Войдите снова, чтобы открыть акт.';
  return status === 403 ? 'Нет доступа к акту.' : 'Акт больше недоступен или не найден.';
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
  workflowStatus: ActWorkflowStatus;
  clientId: string;
  legalEntityId: string;
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
  vatRate?: string | null;
  totalQuantity: number;
  usedByOtherActsQuantity: number;
  availableQuantity: number;
};

type InvoiceLookupRecord = Pick<
  Invoice,
  | 'id'
  | 'number'
  | 'date'
  | 'amount'
  | 'type'
  | 'clientId'
  | 'legalEntityId'
  | 'expenseClientId'
  | 'expenseLegalEntityId'
  | 'clientName'
  | 'legalEntityName'
  | 'expenseClientName'
  | 'expenseLegalEntityName'
  | 'legalEntityInn'
  | 'expenseLegalEntityInn'
  | 'description'
  | 'lineItems'
  | 'acts'
>;

type ManualActLineItemRow = {
  id: string;
  sourceKey: string;
  name: string;
  quantity: string;
  unit: string;
  price: string;
  vatRate: string;
};

const QUANTITY_EPSILON = 0.0001;
const DEFAULT_MANUAL_LINE_ITEM_UNIT = 'шт';
const MANUAL_LINE_ITEM_UNIT_OPTIONS = ['шт', 'усл.', 'час'];
const LINE_ITEM_GRID_TEMPLATE =
  'md:grid-cols-[minmax(24rem,2.7fr)_minmax(5.5rem,0.55fr)_minmax(6.5rem,0.7fr)_minmax(8.5rem,0.85fr)_minmax(6.5rem,0.7fr)_minmax(8.5rem,0.85fr)_2.75rem]';
const LINE_ITEM_GRID_MIN_WIDTH_CLASS = 'md:min-w-[64rem]';
const ACT_WORKFLOW_STATUS_ORDER: ActWorkflowStatus[] = [
  'Draft',
  'NotSigned',
  'RequiresClientSignature',
  'OnApproval',
  'Approved',
  'ApprovalRejected',
  'NeedsCorrection',
  'OnSigning',
  'Signed',
  'Cancelled',
];
const DEFAULT_ACT_WORKFLOW_STATUS_LABELS: Record<ActWorkflowStatus, string> = {
  Draft: 'Черновик',
  NotSigned: 'Не подписан',
  RequiresClientSignature: 'Требуется подпись клиента',
  OnApproval: 'На согласовании',
  Approved: 'Согласован',
  ApprovalRejected: 'Отказ в согласовании',
  NeedsCorrection: 'Требует исправления',
  OnSigning: 'На подписании',
  Signed: 'Подписан клиентом',
  Cancelled: 'Аннулирован',
};

type CounterpartyOwner = {
  clientId: number | null;
  legalEntityId: number | null;
};

function getLegalEntityDisplayName(entity: LegalEntitySummary): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юр. лицо #${entity.id}`;
}

function buildOwnerKey(owner: CounterpartyOwner | null): string {
  if (!owner) return '';
  if (owner.clientId) return `client:${owner.clientId}`;
  if (owner.legalEntityId) return `legal:${owner.legalEntityId}`;
  return '';
}

function parseDecimalInput(value: string): number | null {
  const normalized = value.replace(/\s+/g, '').replace(',', '.').trim();
  if (!normalized) {
    return null;
  }

  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function deriveActStatusFromWorkflowStatus(workflowStatus: ActWorkflowStatus): ActStatus {
  if (workflowStatus === 'OnSigning') return 'Transferred';
  if (workflowStatus === 'Signed') return 'Signed';
  if (workflowStatus === 'Cancelled') return 'Terminated';
  return 'Created';
}

function formatDecimalInput(value: number): string {
  if (!Number.isFinite(value)) {
    return '';
  }

  const rounded = Math.round(value * 10_000) / 10_000;
  return String(rounded).replace(/(\.\d*?[1-9])0+$/u, '$1').replace(/\.0+$/u, '');
}

function calculateLineItemAmountValue(
  quantity: string | number | null | undefined,
  price: string | number | null | undefined,
  roundLine = false,
): string {
  const parsedQuantity = parseDecimalInput(String(quantity ?? '')) ?? 0;
  const parsedPrice = parseDecimalInput(String(price ?? '')) ?? 0;
  return formatDecimalInput(roundLine ? roundedLineTotal(parsedQuantity, parsedPrice) : parsedQuantity * parsedPrice);
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

function createManualActLineItemRow(seed?: Partial<ActLineItem> | null, index = 0): ManualActLineItemRow {
  const fallbackKey = `manual:${Date.now()}-${index}-${Math.random().toString(36).slice(2, 8)}`;
  return {
    id: `${Date.now()}-${index}-${Math.random().toString(36).slice(2, 10)}`,
    sourceKey: seed?.sourceKey?.trim() || fallbackKey,
    name: seed?.name ?? '',
    quantity:
      seed?.quantity !== undefined && seed.quantity !== null
        ? formatDecimalInput(Number(seed.quantity))
        : '',
    unit: seed?.unit ?? DEFAULT_MANUAL_LINE_ITEM_UNIT,
    price:
      seed?.price !== undefined && seed.price !== null
        ? formatDecimalInput(Number(seed.price))
        : '',
    vatRate: seed?.vatRate ?? '',
  };
}

function normalizeManualActLineItems(lineItems?: ActLineItem[] | null): ManualActLineItemRow[] {
  return (lineItems ?? []).map((item, index) => createManualActLineItemRow(item, index));
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

function resolveCounterpartyOwner(invoice: Invoice | null): CounterpartyOwner {
  if (!invoice) {
    return { clientId: null, legalEntityId: null };
  }

  if (invoice.type === 'Expense') {
    const legalEntityId = invoice.expenseLegalEntityId ?? invoice.legalEntityId ?? null;
    return {
      clientId: legalEntityId ? null : invoice.expenseClientId ?? invoice.clientId ?? null,
      legalEntityId,
    };
  }

  const legalEntityId = invoice.legalEntityId ?? invoice.expenseLegalEntityId ?? null;
  return {
    clientId: legalEntityId ? null : invoice.clientId ?? invoice.expenseClientId ?? null,
    legalEntityId,
  };
}

function resolveCounterpartyName(invoice: Invoice | null): string {
  if (!invoice) {
    return '';
  }

  if (invoice.type === 'Expense') {
    return invoice.expenseLegalEntityName?.trim()
      || invoice.expenseClientName?.trim()
      || invoice.legalEntityName?.trim()
      || invoice.clientName?.trim()
      || '';
  }

  return invoice.legalEntityName?.trim()
    || invoice.clientName?.trim()
    || invoice.expenseLegalEntityName?.trim()
    || invoice.expenseClientName?.trim()
    || '';
}

function resolveInvoiceLookupCounterpartyOwner(invoice: InvoiceLookupRecord | null): CounterpartyOwner {
  if (!invoice) {
    return { clientId: null, legalEntityId: null };
  }

  if (invoice.type === 'Expense') {
    const legalEntityId = invoice.expenseLegalEntityId ?? invoice.legalEntityId ?? null;
    return {
      clientId: legalEntityId ? null : invoice.expenseClientId ?? invoice.clientId ?? null,
      legalEntityId,
    };
  }

  const legalEntityId = invoice.legalEntityId ?? invoice.expenseLegalEntityId ?? null;
  return {
    clientId: legalEntityId ? null : invoice.clientId ?? invoice.expenseClientId ?? null,
    legalEntityId,
  };
}

function formatInvoiceLookupDate(value?: string | null): string {
  return toDateInputValue(value ?? '') || value || '';
}

function buildInvoiceLookupLabel(invoice: InvoiceLookupRecord): string {
  const dateLabel = formatInvoiceLookupDate(invoice.date);
  return dateLabel
    ? `Счет №${invoice.number} от ${dateLabel}`
    : `Счет №${invoice.number}`;
}

function buildInvoiceLookupDescription(invoice: InvoiceLookupRecord): string {
  const counterpartyName = invoice.type === 'Expense'
    ? invoice.expenseLegalEntityName?.trim()
      || invoice.expenseClientName?.trim()
      || invoice.legalEntityName?.trim()
      || invoice.clientName?.trim()
      || 'Без контрагента'
    : invoice.legalEntityName?.trim()
      || invoice.clientName?.trim()
      || invoice.expenseLegalEntityName?.trim()
      || invoice.expenseClientName?.trim()
      || 'Без контрагента';
  const typeLabel = invoice.type === 'Expense' ? 'Расходный счет' : 'Доходный счет';
  return `${typeLabel} • ${counterpartyName} • ${formatCurrencySmart(invoice.amount ?? 0).full}`;
}

function toInvoiceLookupRecord(invoice?: Invoice | null): InvoiceLookupRecord | null {
  if (!invoice || typeof invoice.id !== 'number' || invoice.id <= 0) {
    return null;
  }

  return {
    id: invoice.id,
    number: invoice.number ?? '',
    date: invoice.date ?? '',
    amount: Number(invoice.amount ?? 0),
    type: invoice.type ?? 'Income',
    clientId: invoice.clientId ?? null,
    legalEntityId: invoice.legalEntityId ?? null,
    expenseClientId: invoice.expenseClientId ?? null,
    expenseLegalEntityId: invoice.expenseLegalEntityId ?? null,
    clientName: invoice.clientName ?? null,
    legalEntityName: invoice.legalEntityName ?? null,
    expenseClientName: invoice.expenseClientName ?? null,
    expenseLegalEntityName: invoice.expenseLegalEntityName ?? null,
    legalEntityInn: invoice.legalEntityInn ?? null,
    expenseLegalEntityInn: invoice.expenseLegalEntityInn ?? null,
    description: invoice.description ?? null,
    lineItems: invoice.lineItems ?? [],
    acts: invoice.acts ?? [],
  };
}

function mergeInvoiceDetails(
  primary: Invoice | null,
  ...fallbacks: Array<InvoiceLookupRecord | null>
): Invoice | null {
  const fallback = fallbacks.reduce<Partial<Invoice>>((accumulator, item) => {
    if (!item) {
      return accumulator;
    }

    return {
      ...accumulator,
      ...item,
      description: item.description?.trim() ? item.description : accumulator.description,
      lineItems: item.lineItems?.length ? item.lineItems : accumulator.lineItems,
      acts: item.acts?.length ? item.acts : accumulator.acts,
    };
  }, {});

  if (!primary) {
    return Object.keys(fallback).length > 0 ? (fallback as Invoice) : null;
  }

  return {
    ...(fallback as Invoice),
    ...primary,
    description: primary.description?.trim() ? primary.description : fallback.description,
    lineItems: primary.lineItems?.length ? primary.lineItems : fallback.lineItems,
    acts: primary.acts?.length ? primary.acts : fallback.acts,
  };
}

function normalizeActToForm(
  act: Act | null,
  defaultClientId?: number | null,
  defaultResponsibleId?: number | null,
  initialValues?: Partial<ActInput> & { amount?: number | null },
  timeZoneId?: string | null,
): FormState {
  if (!act) {
    const today = todayYMDInTimeZone(timeZoneId);
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
      workflowStatus: (initialValues?.workflowStatus as ActWorkflowStatus) ?? 'Draft',
      clientId: initialValues?.clientId
        ? String(initialValues.clientId)
        : initialValues?.legalEntityId
          ? ''
          : defaultClientId
          ? String(defaultClientId)
          : '',
      legalEntityId: initialValues?.legalEntityId ? String(initialValues.legalEntityId) : '',
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
    date: toDateInputValue(act.date) || todayYMDInTimeZone(timeZoneId),
    amount: act.amount !== undefined && act.amount !== null ? String(act.amount) : '',
    creationMode: act.creationMode === 'Manual' ? 'Manual' : 'Partial',
    invoiceNumber: act.invoiceNumber ?? '',
    invoiceId: act.invoiceId ? String(act.invoiceId) : '',
    counterpartyInn: act.counterpartyInn ?? '',
    status: act.status,
    workflowStatus: act.workflowStatus ?? 'Draft',
    clientId: act.legalEntityId ? '' : act.clientId ? String(act.clientId) : defaultClientId ? String(defaultClientId) : '',
    legalEntityId: act.legalEntityId ? String(act.legalEntityId) : '',
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
  invoiceSeed,
  presentation = 'modal',
  onBack,
  onOpenInvoice,
  canViewInvoices = true,
  accessScopeKey,
  onActUnavailable,
}: ActModalProps) {
  const { t } = useTranslation();
  const { user } = useAuth();
  const actorScope = JSON.stringify([user?.id, user?.role?.id]);
  const editorScope = JSON.stringify([open, mode, act?.id, actorScope, accessScopeKey]);
  const editorScopeRef = useRef(editorScope);
  editorScopeRef.current = editorScope;
  const verifiedActorScopeRef = useRef(actorScope);
  const onActUnavailableRef = useRef(onActUnavailable);
  onActUnavailableRef.current = onActUnavailable;
  const actReadRevisionRef = useRef(0);
  const [actReadAttempt, setActReadAttempt] = useState(0);
  const [actReadState, setActReadState] = useState<ActReadState>({ scope: editorScope, kind: 'loading' });
  const actReadStateRef = useRef(actReadState);
  actReadStateRef.current = actReadState;
  const { isRequired } = useFormRequirements('acts', open);
  const documentStatusSettings = useDocumentStatusSettings();
  const { legalEntities } = useLegalEntities({ enabled: open, ownerScope: editorScope });
  const profileTimeZoneId = useUserTimeZone();
  const profileTimeZoneIdRef = useRef(profileTimeZoneId);
  profileTimeZoneIdRef.current = profileTimeZoneId;
  const formDefaultsRef = useRef({ defaultClientId, defaultResponsibleId, initialValues });
  formDefaultsRef.current = { defaultClientId, defaultResponsibleId, initialValues };
  const defaultDate = todayYMDInTimeZone(profileTimeZoneId);
  const defaultDateRef = useRef(defaultDate);
  const [form, setForm] = useState<FormState>(() =>
    normalizeActToForm(act, defaultClientId, defaultResponsibleId, initialValues, profileTimeZoneId),
  );
  const [localError, setLocalError] = useState<string | null>(null);
  const [sellerSelection, setSellerSelection] = useState<{ companyId: number | null; branchId: number | null }>({ companyId: null, branchId: null });
  const [invoiceDetails, setInvoiceDetails] = useState<Invoice | null>(null);
  const [invoiceLoading, setInvoiceLoading] = useState(false);
  const [invoiceError, setInvoiceError] = useState<string | null>(null);
  const [invoiceOptions, setInvoiceOptions] = useState<InvoiceLookupRecord[]>([]);
  const [invoiceOptionsLoading, setInvoiceOptionsLoading] = useState(false);
  const [invoiceOptionsError, setInvoiceOptionsError] = useState<string | null>(null);
  const [actDetails, setActDetails] = useState<Act | null>(null);
  const [actDetailsScope, setActDetailsScope] = useState(editorScope);
  const [actDetailsLoading, setActDetailsLoading] = useState(false);
  const [actDetailsError, setActDetailsError] = useState<string | null>(null);
  const [selectedLineQuantities, setSelectedLineQuantities] = useState<Record<string, string>>({});
  const selectionSeedRef = useRef<string | null>(null);
  const [manualLineItems, setManualLineItems] = useState<ManualActLineItemRow[]>([]);
  const roundManualLineAmounts = useMemo(() => manualLineItems.some((item) => Boolean(item.vatRate)), [manualLineItems]);
  const manualLineItemsSeedRef = useRef<string | null>(null);
  const [lineItemPickerOpen, setLineItemPickerOpen] = useState(false);
  const [counterpartyPickerOpen, setCounterpartyPickerOpen] = useState(false);

  const isEdit = mode === 'edit';
  const isPageMode = presentation === 'page';
  const actUnavailable = actReadState.scope === editorScope && actReadState.kind === 'unavailable';
  const actReadBlocked = isEdit && (actReadState.scope !== editorScope || actReadState.kind !== 'ready');
  const linkedInvoiceId = form.invoiceId ? Number(form.invoiceId) : null;

  useEffect(() => {
    if (open) {
      setForm(normalizeActToForm(act, defaultClientId, defaultResponsibleId, initialValues, profileTimeZoneIdRef.current));
      setSellerSelection({ companyId: initialValues?.sellerCompanyId ?? null, branchId: initialValues?.sellerBranchId ?? null });
      setLocalError(null);
      setInvoiceDetails(null);
      setInvoiceError(null);
      setInvoiceOptionsError(null);
      setActDetails(null);
      setActDetailsError(null);
      setSelectedLineQuantities({});
      selectionSeedRef.current = null;
      setManualLineItems([]);
      manualLineItemsSeedRef.current = null;
      setLineItemPickerOpen(false);
      setCounterpartyPickerOpen(false);
    }
  }, [actorScope, open, act, defaultClientId, defaultResponsibleId, initialValues]);

  useEffect(() => {
    if (!open || isEdit) {
      defaultDateRef.current = defaultDate;
      return;
    }

    setForm((current) => (
      current.date === defaultDateRef.current
        ? { ...current, date: defaultDate }
        : current
    ));
    defaultDateRef.current = defaultDate;
  }, [defaultDate, isEdit, open]);

  useEffect(() => {
    if (!open || !canViewInvoices || actUnavailable) {
      setInvoiceOptions([]);
      setInvoiceOptionsLoading(false);
      setInvoiceOptionsError(null);
      return;
    }

    let cancelled = false;
    setInvoiceOptionsLoading(true);
    setInvoiceOptionsError(null);

    void apiService
      .getInvoices({ page: 1, pageSize: 200 })
      .then((response) => {
        if (cancelled) {
          return;
        }

        setInvoiceOptions(
          (response.items ?? [])
            .filter((invoice): invoice is Invoice => typeof invoice?.id === 'number' && invoice.id > 0)
            .map((invoice) => ({
              id: invoice.id,
              number: invoice.number,
              date: invoice.date,
              amount: invoice.amount,
              type: invoice.type,
              clientId: invoice.clientId,
              legalEntityId: invoice.legalEntityId,
              expenseClientId: invoice.expenseClientId,
              expenseLegalEntityId: invoice.expenseLegalEntityId,
              clientName: invoice.clientName,
              legalEntityName: invoice.legalEntityName,
              expenseClientName: invoice.expenseClientName,
              expenseLegalEntityName: invoice.expenseLegalEntityName,
              legalEntityInn: invoice.legalEntityInn,
              expenseLegalEntityInn: invoice.expenseLegalEntityInn,
              description: invoice.description,
              lineItems: invoice.lineItems ?? [],
              acts: invoice.acts ?? [],
            })),
        );
      })
      .catch((error) => {
        if (cancelled) {
          return;
        }

        setInvoiceOptions([]);
        setInvoiceOptionsError(extractDisplayErrorMessage(error, 'Не удалось загрузить список счетов'));
      })
      .finally(() => {
        if (!cancelled) {
          setInvoiceOptionsLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [actUnavailable, canViewInvoices, editorScope, open]);

  useEffect(() => {
    if (!open || !linkedInvoiceId || !canViewInvoices || actUnavailable) {
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
  }, [actUnavailable, canViewInvoices, editorScope, open, linkedInvoiceId]);

  useEffect(() => {
    const revision = ++actReadRevisionRef.current;
    let cancelled = false;
    const isCurrent = () => !cancelled && editorScopeRef.current === editorScope && actReadRevisionRef.current === revision;
    const setRead = (kind: ActReadState['kind'], status?: 401 | 403 | 404) => {
      const next = { scope: editorScope, kind, status };
      actReadStateRef.current = next;
      setActReadState(next);
    };
    const unavailable = (status: 401 | 403 | 404) => {
      setRead('unavailable', status);
      setActDetails(null);
      setActDetailsError(actUnavailableMessage(status));
      setCounterpartyPickerOpen(false);
      setLineItemPickerOpen(false);
      if (act?.id) onActUnavailableRef.current?.(act.id, status);
    };
    if (!open || !isEdit || !act?.id) {
      setRead(isEdit ? 'unavailable' : 'ready', isEdit ? 404 : undefined);
      setActDetails(null);
      setActDetailsError(null);
      setActDetailsLoading(false);
      return () => { cancelled = true; actReadRevisionRef.current += 1; };
    }

    setRead('loading');
    setActDetailsLoading(true);
    setActDetailsError(null);

    void apiService
      .getAct(act.id)
      .then((value) => {
        if (!isCurrent()) return;
        if (!value || value.id !== act.id) { unavailable(404); return; }
        if (verifiedActorScopeRef.current !== actorScope) {
          const defaults = formDefaultsRef.current;
          setForm(normalizeActToForm(value, defaults.defaultClientId, defaults.defaultResponsibleId, defaults.initialValues, profileTimeZoneIdRef.current));
          setLocalError(null);
        }
        verifiedActorScopeRef.current = actorScope;
        setActDetails(value);
        setActDetailsScope(editorScope);
        setRead('ready');
      })
      .catch((error) => {
        if (!isCurrent()) return;
        const status = ([401, 403, 404] as const).find((candidate) => isHttpErrorStatus(error, candidate));
        if (status) { unavailable(status); return; }
        setRead('retry');
        setActDetailsError('Не удалось загрузить актуальные данные акта. Черновик сохранён. Повторите загрузку перед сохранением.');
      })
      .finally(() => {
        if (isCurrent()) {
          setActDetailsLoading(false);
        }
      });

    return () => {
      cancelled = true;
      actReadRevisionRef.current += 1;
    };
  }, [act, actorScope, actReadAttempt, editorScope, isEdit, open]);

  const currentAct = useMemo(
    () => actDetailsScope === editorScope && actDetails?.id === act?.id ? actDetails : act,
    [act, actDetails, actDetailsScope, editorScope],
  );
  const selectedInvoiceLookupRecord = useMemo(
    () => invoiceOptions.find((item) => String(item.id) === String(form.invoiceId)) ?? null,
    [form.invoiceId, invoiceOptions],
  );
  const invoiceSeedRecord = useMemo(
    () => (linkedInvoiceId && invoiceSeed?.id === linkedInvoiceId ? toInvoiceLookupRecord(invoiceSeed) : null),
    [invoiceSeed, linkedInvoiceId],
  );
  const effectiveInvoiceDetails = useMemo(
    () => mergeInvoiceDetails(invoiceDetails, invoiceSeedRecord, selectedInvoiceLookupRecord),
    [invoiceDetails, invoiceSeedRecord, selectedInvoiceLookupRecord],
  );
  const actType = useMemo<PaymentKind | null>(
    () => effectiveInvoiceDetails?.type ?? act?.type ?? null,
    [act?.type, effectiveInvoiceDetails?.type],
  );
  const counterpartyLabel = useMemo(() => resolveCounterpartyLabel(actType), [actType]);
  const linkedCounterpartyOwner = useMemo(
    () => resolveCounterpartyOwner(effectiveInvoiceDetails),
    [effectiveInvoiceDetails],
  );
  const linkedCounterpartyKey = useMemo(() => buildOwnerKey(linkedCounterpartyOwner), [linkedCounterpartyOwner]);
  const linkedCounterpartyName = useMemo(
    () => resolveCounterpartyName(effectiveInvoiceDetails),
    [effectiveInvoiceDetails],
  );
  const selectedCounterpartyOwner = useMemo<CounterpartyOwner>(
    () => ({
      clientId: form.clientId ? Number(form.clientId) : null,
      legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : null,
    }),
    [form.clientId, form.legalEntityId],
  );
  const selectedCounterpartyKey = useMemo(() => buildOwnerKey(selectedCounterpartyOwner), [selectedCounterpartyOwner]);
  const invoiceLookupOptions = useMemo<LookupSelectOption[]>(() => {
    const items = [...invoiceOptions];

    if (
      linkedInvoiceId
      && !items.some((item) => item.id === linkedInvoiceId)
      && (effectiveInvoiceDetails || form.invoiceNumber.trim())
    ) {
      items.unshift({
        id: linkedInvoiceId,
        number: effectiveInvoiceDetails?.number ?? (form.invoiceNumber.trim() || `#${linkedInvoiceId}`),
        date: effectiveInvoiceDetails?.date ?? '',
        amount: effectiveInvoiceDetails?.amount ?? 0,
        type: effectiveInvoiceDetails?.type ?? 'Income',
        clientId: effectiveInvoiceDetails?.clientId ?? null,
        legalEntityId: effectiveInvoiceDetails?.legalEntityId ?? null,
        expenseClientId: effectiveInvoiceDetails?.expenseClientId ?? null,
        expenseLegalEntityId: effectiveInvoiceDetails?.expenseLegalEntityId ?? null,
        clientName: effectiveInvoiceDetails?.clientName ?? null,
        legalEntityName: effectiveInvoiceDetails?.legalEntityName ?? null,
        expenseClientName: effectiveInvoiceDetails?.expenseClientName ?? null,
        expenseLegalEntityName: effectiveInvoiceDetails?.expenseLegalEntityName ?? null,
        legalEntityInn: effectiveInvoiceDetails?.legalEntityInn ?? null,
        expenseLegalEntityInn: effectiveInvoiceDetails?.expenseLegalEntityInn ?? null,
        description: effectiveInvoiceDetails?.description ?? null,
        lineItems: effectiveInvoiceDetails?.lineItems ?? [],
        acts: effectiveInvoiceDetails?.acts ?? [],
      });
    }

    const filteredItems = selectedCounterpartyKey
      ? items.filter((invoice) => buildOwnerKey(resolveInvoiceLookupCounterpartyOwner(invoice)) === selectedCounterpartyKey)
      : items;

    return filteredItems.map((invoice) => ({
      value: String(invoice.id),
      label: buildInvoiceLookupLabel(invoice),
      description: buildInvoiceLookupDescription(invoice),
      keywords: [
        invoice.number,
        invoice.clientName ?? '',
        invoice.legalEntityName ?? '',
        invoice.expenseClientName ?? '',
        invoice.expenseLegalEntityName ?? '',
        invoice.type === 'Expense' ? 'расход поставщик' : 'доход клиент',
      ],
    }));
  }, [effectiveInvoiceDetails, form.invoiceNumber, invoiceOptions, linkedInvoiceId, selectedCounterpartyKey]);

  useEffect(() => {
    if (!open || !effectiveInvoiceDetails) {
      return;
    }

    setForm((previous) => {
      let next = previous;
      let changed = false;

      if (effectiveInvoiceDetails.number?.trim() && previous.invoiceNumber !== effectiveInvoiceDetails.number) {
        next = { ...next, invoiceNumber: effectiveInvoiceDetails.number };
        changed = true;
      }

      if (
        linkedCounterpartyOwner.clientId
        && (previous.clientId !== String(linkedCounterpartyOwner.clientId) || previous.legalEntityId)
      ) {
        next = { ...next, clientId: String(linkedCounterpartyOwner.clientId), legalEntityId: '' };
        changed = true;
      }

      if (
        linkedCounterpartyOwner.legalEntityId
        && (previous.legalEntityId !== String(linkedCounterpartyOwner.legalEntityId) || previous.clientId)
      ) {
        next = { ...next, clientId: '', legalEntityId: String(linkedCounterpartyOwner.legalEntityId) };
        changed = true;
      }

      if (!previous.number.trim() && effectiveInvoiceDetails.number?.trim()) {
        next = { ...next, number: effectiveInvoiceDetails.number };
        changed = true;
      }

      if (!previous.title.trim() && effectiveInvoiceDetails.description?.trim()) {
        next = { ...next, title: effectiveInvoiceDetails.description.trim() };
        changed = true;
      }

      return changed ? next : previous;
    });
  }, [effectiveInvoiceDetails, linkedCounterpartyOwner, open]);

  const invoiceLineAvailability = useMemo<InvoiceLineAvailabilityRow[]>(() => {
    const sourceItems = effectiveInvoiceDetails?.lineItems ?? [];
    if (sourceItems.length === 0) {
      return [];
    }

    const currentActId = currentAct?.id ?? null;
    const usedByOtherActs = new Map<string, number>();

    (effectiveInvoiceDetails?.acts ?? []).forEach((invoiceAct) => {
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
          vatRate: item.vatRate,
          totalQuantity,
          usedByOtherActsQuantity: usedQuantity,
          availableQuantity,
        };
      })
      .sort((left, right) => left.sortOrder - right.sortOrder);
  }, [currentAct?.id, effectiveInvoiceDetails]);

  const currentActLineQuantities = useMemo(() => sumLineItemQuantities(currentAct?.lineItems), [currentAct?.lineItems]);
  const hasInvoiceLineItems = invoiceLineAvailability.length > 0;
  const availabilityBySourceKey = useMemo(
    () => new Map(invoiceLineAvailability.map((item) => [item.sourceKey, item])),
    [invoiceLineAvailability],
  );
  const unselectedInvoiceLineItems = useMemo(
    () =>
      invoiceLineAvailability.filter((item) => {
        if (item.availableQuantity <= 0) {
          return false;
        }

        const selectedQuantity = parseDecimalInput(selectedLineQuantities[item.sourceKey] ?? '') ?? 0;
        return selectedQuantity <= 0;
      }),
    [invoiceLineAvailability, selectedLineQuantities],
  );

  useEffect(() => {
    if (unselectedInvoiceLineItems.length > 0) {
      return;
    }

    setLineItemPickerOpen(false);
  }, [unselectedInvoiceLineItems.length]);

  useEffect(() => {
    if (!open) {
      return;
    }

    if (linkedInvoiceId && effectiveInvoiceDetails && !invoiceLoading && !hasInvoiceLineItems && form.creationMode !== 'Manual') {
      setForm((previous) => ({ ...previous, creationMode: 'Manual' }));
    }
  }, [effectiveInvoiceDetails, form.creationMode, hasInvoiceLineItems, invoiceLoading, linkedInvoiceId, open]);

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

  const manualLineItemsSeedKey = useMemo(() => {
    const currentManualItemsKey = (currentAct?.lineItems ?? [])
      .map((item) => [
        item.sourceKey ?? '',
        item.name ?? '',
        item.quantity ?? '',
        item.unit ?? '',
        item.price ?? '',
      ].join(':'))
      .join('|');

    return `${open ? 1 : 0}:${currentAct?.id ?? 0}:${currentAct?.creationMode ?? ''}:${currentManualItemsKey}`;
  }, [currentAct?.creationMode, currentAct?.id, currentAct?.lineItems, open]);

  useEffect(() => {
    if (!open) {
      manualLineItemsSeedRef.current = null;
      return;
    }

    if (manualLineItemsSeedRef.current === manualLineItemsSeedKey) {
      return;
    }

    manualLineItemsSeedRef.current = manualLineItemsSeedKey;

    if (currentAct?.lineItems?.length && (!linkedInvoiceId || !hasInvoiceLineItems || currentAct.creationMode === 'Manual')) {
      setManualLineItems(normalizeManualActLineItems(currentAct.lineItems));
      return;
    }

    if (!isEdit) {
      setManualLineItems([createManualActLineItemRow()]);
      return;
    }

    setManualLineItems([]);
  }, [currentAct, hasInvoiceLineItems, isEdit, linkedInvoiceId, manualLineItemsSeedKey, open]);

  const selectedClient = useMemo(
    () => clients.find((client) => String(client.id) === String(form.clientId)) ?? null,
    [clients, form.clientId],
  );
  const selectedLegalEntity = useMemo(
    () => legalEntities.find((entity) => String(entity.id) === String(form.legalEntityId)) ?? null,
    [legalEntities, form.legalEntityId],
  );
  const retainedCounterparty = useMemo<CounterpartySelection | null>(() => {
    if (!isEdit || !act || !selectedCounterpartyKey) return null;

    const retainedAct = currentAct?.id === act.id ? currentAct : act;
    const legalEntityId = retainedAct.legalEntityId ?? null;
    const clientId = legalEntityId ? null : retainedAct.clientId ?? null;
    if (buildOwnerKey({ clientId, legalEntityId }) !== selectedCounterpartyKey) return null;

    const id = legalEntityId || clientId;
    const name = (legalEntityId ? retainedAct.legalEntityName : retainedAct.clientName)?.trim();
    return id && name ? { kind: legalEntityId ? 'legalEntity' : 'client', id, name } : null;
  }, [act, currentAct, isEdit, selectedCounterpartyKey]);
  const selectedCounterpartyPickerValue = useMemo<CounterpartySelection[]>(() => {
    if (selectedClient) {
      return [{
        kind: 'client',
        id: selectedClient.id,
        name: selectedClient.name,
        subtitle: selectedClient.company ?? null,
        client: selectedClient,
      }];
    }

    if (selectedLegalEntity) {
      return [{
        kind: 'legalEntity',
        id: selectedLegalEntity.id,
        name: getLegalEntityDisplayName(selectedLegalEntity),
        subtitle: selectedLegalEntity.inn ? `ИНН ${selectedLegalEntity.inn}` : selectedLegalEntity.fullName ?? null,
        legalEntity: selectedLegalEntity,
      }];
    }

    return retainedCounterparty ? [retainedCounterparty] : [];
  }, [retainedCounterparty, selectedClient, selectedLegalEntity]);

  const selectedResponsible = useMemo(
    () => responsibles.find((responsible) => String(responsible.id) === String(form.responsibleId)) ?? null,
    [form.responsibleId, responsibles],
  );

  const workflowStatusLabels = useMemo(
    () => getDocumentStatusLabels(documentStatusSettings, 'act', DEFAULT_ACT_WORKFLOW_STATUS_LABELS),
    [documentStatusSettings],
  );
  const workflowStatusCodes = useMemo(
    () => getDocumentStatusCodes(documentStatusSettings, 'act', ACT_WORKFLOW_STATUS_ORDER),
    [documentStatusSettings],
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
    () => {
      const roundLines = selectedLineItems.some((item) => Boolean(item.vatRate));
      return sumLineAmounts(selectedLineItems.map((item) => roundLines ? roundedLineTotal(item.selectedQuantity, item.price) : item.lineAmount), roundLines);
    },
    [selectedLineItems],
  );

  const preparedManualLineItems = useMemo(
    () =>
      manualLineItems.map((item, index) => {
        const normalizedName = item.name.trim();
        const normalizedUnit = item.unit.trim() || DEFAULT_MANUAL_LINE_ITEM_UNIT;
        const parsedQuantity = parseDecimalInput(item.quantity);
        const parsedPrice = parseDecimalInput(item.price);
        const hasContent = Boolean(
          normalizedName
          || item.quantity.trim()
          || item.price.trim()
          || (item.unit.trim() && item.unit.trim() !== DEFAULT_MANUAL_LINE_ITEM_UNIT),
        );

        return {
          ...item,
          sourceKey: item.sourceKey.trim() || `manual:${index + 1}`,
          normalizedName,
          normalizedUnit,
          parsedQuantity,
          parsedPrice,
          hasContent,
          isValid: Boolean(normalizedName && parsedQuantity !== null && parsedQuantity > 0 && parsedPrice !== null && parsedPrice >= 0),
          lineAmount: roundManualLineAmounts ? roundedLineTotal(parsedQuantity ?? 0, parsedPrice ?? 0) : (parsedQuantity ?? 0) * (parsedPrice ?? 0),
        };
      }),
    [manualLineItems, roundManualLineAmounts],
  );
  const manualLineItemsWithContent = useMemo(
    () => preparedManualLineItems.filter((item) => item.hasContent),
    [preparedManualLineItems],
  );
  const validManualLineItems = useMemo(
    () => preparedManualLineItems.filter((item) => item.isValid),
    [preparedManualLineItems],
  );
  const manualLineItemsAmount = useMemo(
    () => sumLineAmounts(preparedManualLineItems.map((item) => item.lineAmount), roundManualLineAmounts),
    [preparedManualLineItems, roundManualLineAmounts],
  );
  const canEditManualLineItems = linkedInvoiceId === null || !hasInvoiceLineItems;
  const isUsingManualLineItems = canEditManualLineItems && form.creationMode === 'Manual' && manualLineItemsWithContent.length > 0;

  useEffect(() => {
    if (!open || !isUsingManualLineItems) {
      return;
    }

    const nextAmount = formatDecimalInput(manualLineItemsAmount);
    setForm((previous) => (previous.amount === nextAmount ? previous : { ...previous, amount: nextAmount }));
  }, [isUsingManualLineItems, manualLineItemsAmount, open]);

  const effectiveAmount = isUsingInvoiceItems
    ? computedAmount
    : isUsingManualLineItems
      ? manualLineItemsAmount
    : parseDecimalInput(form.amount) ?? 0;
  const amountReadonly = isUsingInvoiceItems || isUsingManualLineItems;
  const availableLinesCount = useMemo(
    () => invoiceLineAvailability.filter((item) => item.availableQuantity > 0).length,
    [invoiceLineAvailability],
  );
  const selectedCounterpartyName = selectedClient?.name
    || (selectedLegalEntity ? getLegalEntityDisplayName(selectedLegalEntity) : '')
    || linkedCounterpartyName
    || retainedCounterparty?.name
    || 'Не выбран';
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
    setForm((previous) => {
      if (field === 'workflowStatus') {
        const workflowStatus = value as ActWorkflowStatus;
        return {
          ...previous,
          workflowStatus,
          status: deriveActStatusFromWorkflowStatus(workflowStatus),
        };
      }

      return { ...previous, [field]: value };
    });
  };

  const updateManualLineItem = (id: string, patch: Partial<ManualActLineItemRow>) => {
    setManualLineItems((previous) => previous.map((item) => (item.id === id ? { ...item, ...patch } : item)));
  };

  const addManualLineItem = () => {
    setManualLineItems((previous) => [...previous, createManualActLineItemRow(null, previous.length)]);
  };

  const removeManualLineItem = (id: string) => {
    setManualLineItems((previous) => previous.filter((item) => item.id !== id));
  };

  const handleCounterpartySelect = (value: string) => {
    const [ownerType, rawOwnerId] = value.split(':');
    const nextOwnerKey = ownerType && rawOwnerId ? `${ownerType}:${rawOwnerId}` : '';
    const shouldClearInvoice = linkedInvoiceId !== null
      && Boolean(linkedCounterpartyKey)
      && linkedCounterpartyKey !== nextOwnerKey;
    const nextClientId = ownerType === 'client' ? rawOwnerId : '';
    const nextLegalEntityId = ownerType === 'legal' ? rawOwnerId : '';

    if (shouldClearInvoice) {
      selectionSeedRef.current = null;
      setSelectedLineQuantities({});
      setLineItemPickerOpen(false);
      setInvoiceError(null);
      setInvoiceDetails(null);

      setForm((previous) => ({
        ...previous,
        clientId: nextClientId,
        legalEntityId: nextLegalEntityId,
        invoiceId: '',
        invoiceNumber: '',
        creationMode: 'Manual',
      }));
      return;
    }

    setForm((previous) => ({
      ...previous,
      clientId: nextClientId,
      legalEntityId: nextLegalEntityId,
    }));
  };

  const handleCounterpartyPick = (selection: CounterpartySelection | null) => {
    if (!selection) {
      handleCounterpartySelect('');
      return;
    }

    handleCounterpartySelect(selection.kind === 'legalEntity' ? `legal:${selection.id}` : `client:${selection.id}`);
  };

  const handleInvoiceSelect = (value: string) => {
    selectionSeedRef.current = null;
    setSelectedLineQuantities({});
    setLineItemPickerOpen(false);
    setInvoiceError(null);
    setInvoiceDetails(null);

    if (!value) {
      setForm((previous) => ({
        ...previous,
        invoiceId: '',
        invoiceNumber: '',
        creationMode: 'Manual',
      }));
      return;
    }

    const selectedInvoice = invoiceOptions.find((item) => String(item.id) === value) ?? null;
    const counterpartyOwner = resolveInvoiceLookupCounterpartyOwner(selectedInvoice);

    setForm((previous) => ({
      ...previous,
      clientId: counterpartyOwner.clientId ? String(counterpartyOwner.clientId) : counterpartyOwner.legalEntityId ? '' : previous.clientId,
      legalEntityId: counterpartyOwner.legalEntityId ? String(counterpartyOwner.legalEntityId) : counterpartyOwner.clientId ? '' : previous.legalEntityId,
      invoiceId: value,
      invoiceNumber: selectedInvoice?.number ?? previous.invoiceNumber,
      creationMode: 'Partial',
    }));
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

  const handleAddInvoiceLineItem = (sourceKey: string) => {
    const availableQuantity = availabilityBySourceKey.get(sourceKey)?.availableQuantity ?? 0;
    if (availableQuantity <= 0) {
      return;
    }

    setSelectedLineQuantities((previous) => ({
      ...previous,
      [sourceKey]: formatDecimalInput(availableQuantity),
    }));
    setForm((previous) => ({ ...previous, creationMode: 'Partial' }));
    setLineItemPickerOpen(false);
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
    if (editorScopeRef.current !== editorScope || submitting || (isEdit && (
      actReadStateRef.current.scope !== editorScope || actReadStateRef.current.kind !== 'ready'
    ))) return;
    const revision = actReadRevisionRef.current;
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
    let lineItems: NonNullable<ActInput['lineItems']> = [];

    if (linkedToInvoice && hasInvoiceLineItems && form.creationMode !== 'Manual') {
      lineItems = selectedLineItems.map((item) => ({
        sourceKey: item.sourceKey,
        quantity: item.selectedQuantity,
      }));

      if (lineItems.length === 0) {
        setLocalError('Выберите хотя бы одну позицию счета для акта');
        return;
      }

      if (lineItems.some((item) => !hasStorableLineItemPrecision(Number(item.quantity ?? 0), 0))) {
        setLocalError(lineItemPrecisionError);
        return;
      }

      creationMode = allAvailableSelected ? 'Full' : 'Partial';
      amount = computedAmount;
    } else {
      creationMode = 'Manual';
      if (!canEditManualLineItems) {
        const parsedAmount = parseDecimalInput(form.amount);
        if (parsedAmount === null) {
          setLocalError('Введите корректную сумму');
          return;
        }

        amount = parsedAmount;
        lineItems = [];
      } else {
        const manualPayload = preparedManualLineItems.reduce<{
          error: string | null;
          items: NonNullable<ActInput['lineItems']>;
        }>((accumulator, item, index) => {
          if (accumulator.error || !item.hasContent) {
            return accumulator;
          }

          if (!item.normalizedName) {
            return {
              error: `Заполните название позиции акта в строке ${index + 1}`,
              items: accumulator.items,
            };
          }

          if (item.parsedQuantity === null || item.parsedQuantity <= 0) {
            return {
              error: `Укажите корректное количество для позиции "${item.normalizedName}"`,
              items: accumulator.items,
            };
          }

          if (item.parsedPrice === null || item.parsedPrice < 0) {
            return {
              error: `Укажите корректную цену для позиции "${item.normalizedName}"`,
              items: accumulator.items,
            };
          }

          if (!hasStorableLineItemPrecision(item.parsedQuantity, item.parsedPrice)) {
            return { error: lineItemPrecisionError, items: accumulator.items };
          }

          return {
            error: null,
            items: [
              ...accumulator.items,
              {
                sourceKey: item.sourceKey,
                name: item.normalizedName,
                quantity: item.parsedQuantity,
                unit: item.normalizedUnit,
                price: item.parsedPrice,
                vatRate: item.vatRate ? item.vatRate : null,
              },
            ],
          };
        }, { error: null, items: [] });

        if (manualPayload.error) {
          setLocalError(manualPayload.error);
          return;
        }

        lineItems = manualPayload.items;

        if (lineItems.length > 0) {
          const roundLines = lineItems.some((line) => Boolean(line.vatRate));
          amount = sumLineAmounts(
            lineItems.map((item) => {
              const value = Number(item.quantity ?? 0) * Number(item.price ?? 0);
              return roundLines ? roundedLineTotal(Number(item.quantity ?? 0), Number(item.price ?? 0)) : value;
            }),
            roundLines,
          );
        } else {
          const parsedAmount = parseDecimalInput(form.amount);
          if (parsedAmount === null) {
            setLocalError('Введите корректную сумму');
            return;
          }

          amount = parsedAmount;
        }
      }
    }

    const payload: ActInput = {
      ...(!isEdit ? { sellerCompanyId: sellerSelection.companyId, sellerBranchId: sellerSelection.branchId } : {}),
      number: form.number.trim(),
      title: form.title.trim() ? form.title.trim() : undefined,
      date: fromInputToApiDate(form.date) ?? form.date,
      amount,
      creationMode,
      invoiceNumber: form.invoiceNumber.trim() ? form.invoiceNumber.trim() : undefined,
      invoiceId: linkedInvoiceId ?? undefined,
      counterpartyInn: undefined,
      status: deriveActStatusFromWorkflowStatus(form.workflowStatus),
      workflowStatus: form.workflowStatus,
      clientId: form.clientId ? Number(form.clientId) : null,
      legalEntityId: form.legalEntityId ? Number(form.legalEntityId) : null,
      responsibleId: form.responsibleId ? Number(form.responsibleId) : undefined,
      comment: form.comment.trim() ? form.comment.trim() : undefined,
      lineItems,
    };

    try {
      await onSubmit(payload);
    } catch (error) {
      if (editorScopeRef.current === editorScope && actReadRevisionRef.current === revision && error instanceof Error && !errorMessage) {
        setLocalError(error.message);
      }
    }
  };

  if (!open) {
    return null;
  }

  if (isEdit && (actUnavailable || actReadState.scope !== editorScope || verifiedActorScopeRef.current !== actorScope)) {
    const pending = actReadState.scope !== editorScope || actReadState.kind === 'loading';
    const unavailableContent = (
      <div className="w-full max-w-xl rounded-xl border border-slate-200 bg-white p-4">
        <p role={pending ? 'status' : 'alert'} className="text-sm text-slate-700">
          {pending ? 'Проверка доступа к акту...' : actDetailsError || 'Акт недоступен.'}
        </p>
        <div className="mt-3 flex flex-wrap gap-2">
          <button type="button" onClick={isPageMode ? (onBack ?? onClose) : onClose} className="rounded-lg border border-slate-300 px-3 py-2 text-sm">
            {isPageMode ? 'Назад' : 'Закрыть'}
          </button>
          {actReadState.scope === editorScope && actReadState.kind === 'retry' ? (
            <button type="button" onClick={() => setActReadAttempt((attempt) => attempt + 1)} className="rounded-lg border border-slate-300 px-3 py-2 text-sm">Повторить загрузку акта</button>
          ) : null}
        </div>
      </div>
    );
    return isPageMode
      ? <div className="w-full px-4 py-6 sm:px-6 lg:px-8">{unavailableContent}</div>
      : <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 px-4 py-8">{unavailableContent}</div>;
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
        {!isEdit ? (
          <div className="text-sm">
            <DocumentSellerPicker kind="act" documentId={0} draft canEdit={!submitting}
              companyId={sellerSelection.companyId} branchId={sellerSelection.branchId} onChanged={setSellerSelection} />
          </div>
        ) : null}
        <div className={isPageMode ? 'grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,1fr)_320px]' : 'space-y-6'}>
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
                  <SystemDateInput
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
                  <span className="font-medium text-slate-700">Статус документа</span>
                  <SystemSelect
                    value={form.workflowStatus}
                    onChange={handleChange('workflowStatus')}
                    className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                  >
                    {workflowStatusCodes.map((status) => (
                      <option key={status} value={status}>
                        {workflowStatusLabels[status]}
                      </option>
                    ))}
                  </SystemSelect>
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
                    <span className="text-xs text-slate-500">
                      {isUsingInvoiceItems
                        ? 'Сумма считается по выбранным позициям счета.'
                        : 'Сумма считается по позициям акта.'}
                    </span>
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
                      ? 'Тип акта, контрагент и доступные позиции определяются по выбранному счету.'
                      : 'Можно выбрать счет из справочника или оставить акт пустым и заполнить его вручную.'}
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
                      Ручной акт
                    </button>
                  </div>
                ) : null}
              </div>

              <div className="space-y-4">
                <div className="space-y-2">
                  <label className="flex flex-col gap-1 text-sm">
                    <span className="font-medium text-slate-700">{counterpartyLabel}</span>
                    <div className="flex gap-2">
                      <input
                        type="text"
                        readOnly
                        value={selectedCounterpartyKey ? selectedCounterpartyName : ''}
                        placeholder="Выберите контрагента"
                        className="min-w-0 flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-800 focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                        onClick={() => setCounterpartyPickerOpen(true)}
                      />
                      <button
                        type="button"
                        onClick={() => setCounterpartyPickerOpen(true)}
                        className="shrink-0 rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                      >
                        Выбрать
                      </button>
                      {selectedCounterpartyKey ? (
                        <button
                          type="button"
                          onClick={() => handleCounterpartyPick(null)}
                          className="shrink-0 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-500 transition hover:bg-slate-50"
                        >
                          Очистить
                        </button>
                      ) : null}
                    </div>
                    <span className="text-xs text-slate-500">
                      {linkedInvoiceId
                        ? 'Контрагент синхронизируется с выбранным счетом. При смене контрагента счет будет сброшен.'
                        : selectedCounterpartyKey
                          ? 'Теперь в справочнике счетов будут показаны только документы этого контрагента.'
                          : 'Сначала можно выбрать контрагента, а потом сузить список счетов.'}
                    </span>
                  </label>

                  {selectedClient?.clientStatus ? (
                    <div>
                      <ClientStatusBadge status={selectedClient.clientStatus} />
                    </div>
                  ) : null}
                  {selectedLegalEntity ? (
                    <div className="rounded-xl border border-indigo-100 bg-indigo-50/70 px-3 py-2 text-xs text-slate-600">
                      Юрлицо: {getLegalEntityDisplayName(selectedLegalEntity)}
                      {selectedLegalEntity.inn ? `, ИНН ${selectedLegalEntity.inn}` : ''}
                      {selectedLegalEntity.kpp ? `, КПП ${selectedLegalEntity.kpp}` : ''}
                    </div>
                  ) : null}
                </div>

                <LookupSelect
                  label="Счет"
                  value={form.invoiceId}
                  options={invoiceLookupOptions}
                  onChange={handleInvoiceSelect}
                  title="Выбор счета"
                  dictionaryLabel="Справочник"
                  placeholder={invoiceOptionsLoading ? 'Загружаю счета...' : 'Выберите счет'}
                  searchPlaceholder="Поиск по номеру счета, клиенту или поставщику"
                  emptyOptionLabel="Без счета"
                  emptyOptionDescription="Оставить акт пустым и заполнить сумму вручную."
                  helperText={
                    linkedInvoiceId
                      ? 'Счет, тип акта, контрагент и позиции в акте связаны между собой.'
                      : selectedCounterpartyKey
                        ? 'Показываем только счета выбранного контрагента.'
                        : 'После выбора счета контрагент и позиции подтянутся автоматически.'
                  }
                  noOptionsText={
                    invoiceOptionsLoading
                      ? 'Загружаем счета...'
                      : selectedCounterpartyKey
                        ? 'У выбранного контрагента нет счетов'
                        : 'Счета не найдены'
                  }
                  disabled={invoiceOptionsLoading}
                />

                {invoiceOptionsError ? (
                  <div className="rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                    {invoiceOptionsError}
                  </div>
                ) : null}

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
                        <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">{counterpartyLabel}</div>
                        <div className="mt-2 text-sm font-medium text-slate-900">{selectedCounterpartyName}</div>
                        {selectedClient?.clientStatus ? (
                          <div className="mt-2">
                            <ClientStatusBadge status={selectedClient.clientStatus} />
                          </div>
                        ) : null}
                      </div>

                      <div>
                        <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">Счет</div>
                        {invoiceLoading ? (
                          <div className="mt-2 inline-flex items-center gap-2 text-sm text-slate-500">
                            <Loader2 className="h-4 w-4 animate-spin" />
                            Загружаю связанный счет...
                          </div>
                        ) : effectiveInvoiceDetails ? (
                          onOpenInvoice ? (
                            <button
                              type="button"
                              onClick={() => onOpenInvoice(linkedInvoiceId, actType ?? undefined)}
                              className="mt-2 inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-indigo-700 transition hover:border-indigo-200 hover:bg-indigo-50"
                            >
                              <Link2 className="h-4 w-4" />
                              Счет №{effectiveInvoiceDetails.number} от {toDateInputValue(effectiveInvoiceDetails.date) || effectiveInvoiceDetails.date}
                            </button>
                          ) : (
                            <div className="mt-2 text-sm font-medium text-slate-900">
                              Счет №{effectiveInvoiceDetails.number} от {toDateInputValue(effectiveInvoiceDetails.date) || effectiveInvoiceDetails.date}
                            </div>
                          )
                        ) : (
                          <div className="mt-2 text-sm text-slate-500">
                            Связанный счет будет показан после загрузки.
                          </div>
                        )}
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
                            ? 'Ручной акт'
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
                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600">
                      <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">{counterpartyLabel}</div>
                      <div className="mt-2 font-medium text-slate-900">{selectedCounterpartyName}</div>
                      <p className="mt-3 text-xs leading-5 text-slate-500">
                        Если сначала выбрать контрагента, список счетов автоматически отфильтруется по нему.
                      </p>
                    </div>

                    <div className="rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600">
                      <div className="font-medium text-slate-700">Ответственный</div>
                      <div className="mt-2">{selectedResponsibleLabel}</div>
                      <p className="mt-3 text-xs leading-5 text-slate-500">
                        Ответственный подставляется автоматически как пользователь, который создает акт.
                      </p>
                    </div>
                  </div>
                )}
              </div>

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

            {canEditManualLineItems ? (
              <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                  <div>
                    <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500">Позиции акта</h3>
                    <p className="mt-1 text-sm text-slate-500">
                      {linkedInvoiceId
                        ? 'У выбранного счета нет позиций, поэтому строки акта можно заполнить вручную.'
                        : 'Добавьте позиции акта, чтобы сумма считалась автоматически. Если строки не нужны, сумму можно оставить ручной.'}
                    </p>
                  </div>

                  <button
                    type="button"
                    onClick={addManualLineItem}
                    className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                  >
                    + Добавить позицию
                  </button>
                </div>

                {manualLineItems.length === 0 ? (
                  <div className="mt-4 rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-center text-sm text-slate-600">
                    <div className="font-medium text-slate-900">В акте пока нет ни одной позиции.</div>
                    <p className="mt-2 text-slate-500">
                      Добавьте строки как в счете или оставьте сумму акта вручную.
                    </p>
                  </div>
                ) : (
                  <>
                    <div className="-mx-1 mt-4 overflow-x-auto px-1">
                      <div className={`space-y-3 ${LINE_ITEM_GRID_MIN_WIDTH_CLASS}`}>
                        <div className={`hidden items-center gap-3 px-1 text-xs font-semibold uppercase tracking-wide text-slate-500 md:grid ${LINE_ITEM_GRID_TEMPLATE}`}>
                          <span>Наименование</span>
                          <span className="text-center">Кол-во</span>
                          <span>Ед.</span>
                          <span className="text-right">Цена</span>
                          <span>НДС</span>
                          <span className="text-right">Сумма</span>
                          <span className="sr-only">Действия</span>
                        </div>

                        {preparedManualLineItems.map((item) => (
                          <div
                            key={item.id}
                            className={`grid gap-3 rounded-2xl border border-slate-200 bg-slate-50/80 p-3 md:items-start md:rounded-none md:border-0 md:bg-transparent md:p-0 ${LINE_ITEM_GRID_TEMPLATE}`}
                          >
                            <label className="space-y-1">
                              <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Услуга</span>
                              <input
                                type="text"
                                value={item.name}
                                onChange={(event) => updateManualLineItem(item.id, { name: event.target.value })}
                                placeholder="Название позиции"
                                aria-label="Название позиции акта"
                                className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                              />
                            </label>

                            <label className="space-y-1">
                              <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Кол-во</span>
                              <input
                                type="text"
                                inputMode="decimal"
                                value={item.quantity}
                                onChange={(event) => updateManualLineItem(item.id, { quantity: event.target.value })}
                                placeholder="Кол-во"
                                aria-label="Количество позиции акта"
                                className="w-full rounded-lg border border-slate-300 px-2 py-2 text-center text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                              />
                            </label>

                            <label className="space-y-1">
                              <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Ед.</span>
                              <input
                                type="text"
                                list="act-line-item-units"
                                value={item.unit}
                                onChange={(event) => updateManualLineItem(item.id, { unit: event.target.value })}
                                placeholder="Ед."
                                aria-label="Единица измерения позиции акта"
                                className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                              />
                            </label>

                            <label className="space-y-1">
                              <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Цена</span>
                              <input
                                type="text"
                                inputMode="decimal"
                                value={item.price}
                                onChange={(event) => updateManualLineItem(item.id, { price: event.target.value })}
                                placeholder="0.00"
                                aria-label="Цена позиции акта"
                                className="w-full rounded-lg border border-slate-300 px-3 py-2 text-right text-sm tabular-nums focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                              />
                            </label>

                            <label className="space-y-1">
                              <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">НДС</span>
                              <VatRateSelect value={item.vatRate} onChange={(value) => updateManualLineItem(item.id, { vatRate: value })} />
                            </label>

                            <div className="space-y-1">
                              <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Сумма</span>
                              <output className="inline-flex min-h-10 w-full items-center justify-end rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-right text-sm font-medium tabular-nums text-slate-700">
                                {calculateLineItemAmountValue(item.quantity, item.price, roundManualLineAmounts)}
                              </output>
                            </div>

                            <div className="flex items-end justify-end md:justify-start">
                              <button
                                type="button"
                                onClick={() => removeManualLineItem(item.id)}
                                className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-rose-200 bg-white text-rose-500 transition hover:border-rose-300 hover:bg-rose-50 hover:text-rose-700"
                                aria-label="Удалить позицию акта"
                                title="Удалить позицию акта"
                              >
                                <X className="h-4 w-4" />
                              </button>
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>

                    <datalist id="act-line-item-units">
                      {MANUAL_LINE_ITEM_UNIT_OPTIONS.map((unit) => (
                        <option key={unit} value={unit} />
                      ))}
                    </datalist>

                    <div className="mt-4 flex flex-col gap-2 border-t border-slate-200 pt-4 text-sm sm:flex-row sm:items-center sm:justify-between">
                      <div className="text-slate-500">
                        Позиций в акте: <span className="font-medium text-slate-900">{validManualLineItems.length}</span>
                      </div>
                      <div className="text-base font-semibold text-slate-900">
                        Итого по акту: {formatCurrencySmart(effectiveAmount).full}
                      </div>
                    </div>
                  </>
                )}
              </section>
            ) : hasInvoiceLineItems ? (
              <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                  <div>
                    <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-500">Позиции счета</h3>
                    <p className="mt-1 text-sm text-slate-500">
                      Сумма акта считается по выбранным позициям. Лишние строки можно удалить, а затем вернуть через кнопку добавления.
                    </p>
                  </div>

                  {form.creationMode !== 'Manual' ? (
                    <div className="relative flex flex-wrap items-center gap-2">
                      <button
                        type="button"
                        onClick={() => setLineItemPickerOpen((previous) => !previous)}
                        disabled={unselectedInvoiceLineItems.length === 0}
                        aria-expanded={lineItemPickerOpen}
                        className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:text-slate-400"
                      >
                        + Добавить
                      </button>
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

                      {lineItemPickerOpen && unselectedInvoiceLineItems.length > 0 ? (
                        <div className="absolute right-0 top-full z-10 mt-2 w-full min-w-[20rem] rounded-2xl border border-slate-200 bg-white p-2 shadow-2xl sm:w-[24rem]">
                          <div className="px-2 pb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">
                            Доступные позиции
                          </div>
                          <div className="max-h-72 space-y-2 overflow-y-auto">
                            {unselectedInvoiceLineItems.map((item) => (
                              <button
                                key={item.sourceKey}
                                type="button"
                                onClick={() => handleAddInvoiceLineItem(item.sourceKey)}
                                className="flex w-full items-center justify-between gap-3 rounded-xl border border-slate-200 px-3 py-3 text-left transition hover:border-indigo-200 hover:bg-indigo-50/40"
                              >
                                <div className="min-w-0">
                                  <div className="truncate text-sm font-medium text-slate-900">{item.name}</div>
                                  <div className="mt-1 text-xs text-slate-500">
                                    Остаток: {formatDecimalInput(item.availableQuantity)} {item.unit} • {formatCurrencySmart(item.price).full}
                                  </div>
                                </div>
                                <span className="rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700">
                                  Добавить
                                </span>
                              </button>
                            ))}
                          </div>
                        </div>
                      ) : null}
                    </div>
                  ) : null}
                </div>

                {form.creationMode === 'Manual' ? (
                  <div className="mt-4 rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-4 text-sm text-slate-600">
                    Позиции счета сейчас не используются, потому что выбран ручной режим акта. Сумму можно указать вручную.
                    <button
                      type="button"
                      onClick={handleEnableInvoiceMode}
                      className="mt-3 inline-flex items-center rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white transition hover:bg-indigo-500"
                    >
                      Вернуть позиции счета
                    </button>
                  </div>
                ) : (
                  <>
                    <div className="-mx-1 mt-4 overflow-x-auto px-1">
                      <div className={`space-y-3 ${LINE_ITEM_GRID_MIN_WIDTH_CLASS}`}>
                        <div className={`hidden items-center gap-3 px-1 text-xs font-semibold uppercase tracking-wide text-slate-500 md:grid ${LINE_ITEM_GRID_TEMPLATE}`}>
                          <span>Наименование</span>
                          <span className="text-center">Кол-во</span>
                          <span>Ед.</span>
                          <span className="text-right">Цена</span>
                          <span className="text-right">Сумма</span>
                          <span className="sr-only">Действия</span>
                        </div>

                        {selectedLineItems.length === 0 ? (
                          <div className="rounded-2xl border border-dashed border-slate-200 bg-slate-50 px-4 py-8 text-center text-sm text-slate-600">
                            <div className="font-medium text-slate-900">Пока в акт не добавлена ни одна позиция счета.</div>
                            <p className="mt-2 text-slate-500">
                              Добавьте нужные строки из счета или переключите акт в ручной режим.
                            </p>
                          </div>
                        ) : (
                          selectedLineItems.map((item) => (
                            <div
                              key={item.sourceKey}
                              className={`grid gap-3 rounded-2xl border border-slate-200 bg-slate-50/80 p-3 md:items-start md:rounded-none md:border-0 md:bg-transparent md:p-0 ${LINE_ITEM_GRID_TEMPLATE}`}
                            >
                              <div className="space-y-1">
                                <div
                                  className="flex min-h-[3.25rem] w-full items-center justify-between gap-3 rounded-xl border border-slate-300 bg-white px-3 py-2.5 text-left text-sm md:h-10 md:min-h-10 md:py-2"
                                  aria-label="Наименование услуги"
                                  title={item.name}
                                >
                                  <span className="min-w-0 flex-1">
                                    <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Услуга</span>
                                    <span className="mt-1 block truncate font-medium text-slate-700 md:mt-0">
                                      {item.name}
                                    </span>
                                  </span>
                                  <ChevronDown className="h-4 w-4 shrink-0 text-slate-300" />
                                </div>
                                <div className="flex flex-wrap gap-3 text-xs text-slate-500">
                                  <span>Всего: {formatDecimalInput(item.totalQuantity)}</span>
                                  <span>Уже в актах: {formatDecimalInput(item.usedByOtherActsQuantity)}</span>
                                  <span>Остаток: {formatDecimalInput(item.availableQuantity)}</span>
                                </div>
                              </div>

                              <label className="space-y-1">
                                <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Кол-во</span>
                                <input
                                  type="text"
                                  inputMode="decimal"
                                  value={selectedLineQuantities[item.sourceKey] ?? ''}
                                  onChange={handleQuantityChange(item.sourceKey)}
                                  placeholder="Кол-во"
                                  aria-label="Количество"
                                  className="w-full rounded-lg border border-slate-300 px-2 py-2 text-center text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-200"
                                />
                              </label>

                              <label className="space-y-1">
                                <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Ед.</span>
                                <div className="inline-flex min-h-10 w-full items-center rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700">
                                  {item.unit || '—'}
                                </div>
                              </label>

                              <label className="space-y-1">
                                <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Цена</span>
                                <output
                                  aria-label="Цена"
                                  className="inline-flex min-h-10 w-full items-center justify-end rounded-lg border border-slate-300 bg-white px-3 py-2 text-right text-sm tabular-nums text-slate-700"
                                >
                                  {formatCurrencySmart(item.price).full}
                                </output>
                              </label>

                              <div className="space-y-1">
                                <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 md:hidden">Сумма</span>
                                <output className="inline-flex min-h-10 w-full items-center justify-end rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-right text-sm font-medium tabular-nums text-slate-700">
                                  {formatCurrencySmart(item.lineAmount).full}
                                </output>
                              </div>

                              <div className="flex items-end justify-end md:justify-start">
                                <button
                                  type="button"
                                  onClick={() => handleRemoveLineItem(item.sourceKey)}
                                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-rose-200 bg-white text-rose-500 transition hover:border-rose-300 hover:bg-rose-50 hover:text-rose-700"
                                  aria-label="Удалить позицию"
                                  title="Удалить позицию"
                                >
                                  <X className="h-4 w-4" />
                                </button>
                              </div>
                            </div>
                          ))
                        )}
                      </div>
                    </div>

                    <div className="mt-4 flex flex-col gap-2 border-t border-slate-200 pt-4 text-sm sm:flex-row sm:items-center sm:justify-between">
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
                    : isUsingManualLineItems
                      ? `Позиции в акте: ${validManualLineItems.length}`
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
            {actDetailsError || lookupsError || invoiceError}
            {isEdit && actReadState.scope === editorScope && actReadState.kind === 'retry' ? (
              <button type="button" onClick={() => setActReadAttempt((attempt) => attempt + 1)} className="ml-2 underline">Повторить загрузку акта</button>
            ) : null}
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
            disabled={submitting || invoiceLoading || actDetailsLoading || actReadBlocked}
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
    return (
      <>
        <div className="w-full px-4 py-6 sm:px-6 lg:px-8">{content}</div>
        <CounterpartyPickerModal
          open={counterpartyPickerOpen}
          title="Выбор контрагента"
          allowEmpty
          selected={selectedCounterpartyPickerValue}
          initialScope={form.legalEntityId ? 'legalEntity' : 'client'}
          onClose={() => setCounterpartyPickerOpen(false)}
          onSelect={handleCounterpartyPick}
        />
      </>
    );
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 px-4 py-8">
      {content}
      <CounterpartyPickerModal
        open={counterpartyPickerOpen}
        title="Выбор контрагента"
        allowEmpty
        selected={selectedCounterpartyPickerValue}
        initialScope={form.legalEntityId ? 'legalEntity' : 'client'}
        overlayClassName="z-[1600]"
        onClose={() => setCounterpartyPickerOpen(false)}
        onSelect={handleCounterpartyPick}
      />
    </div>
  );
}
