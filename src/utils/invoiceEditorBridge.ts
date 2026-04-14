import type { ActStatus, InstallmentPlanInput, PaymentKind, PaymentStatus } from '../types';

export const INVOICE_EDITOR_STORAGE_KEY = 'pp.invoice-editor-draft';

const PAYMENT_KINDS: readonly PaymentKind[] = ['Income', 'Expense'];
const PAYMENT_STATUSES: readonly PaymentStatus[] = ['Pending', 'Overdue', 'Completed', 'Processing', 'Cancelled'];
const ACT_STATUSES: readonly ActStatus[] = ['Created', 'Transferred', 'Signed', 'Terminated'];

export interface InvoiceEditorFormDraft {
  number: string;
  date: string;
  dueDate: string;
  amount: string;
  type: PaymentKind;
  status: PaymentStatus;
  clientId: string;
  contractId: string;
  clientCaseReference: string;
  description: string;
  actReference: string;
  paidDate: string;
  responsibleIds: string[];
}

export interface InvoiceEditorLineItemDraft {
  id: string;
  name: string;
  quantity: string;
  unit: string;
  price: string;
  serviceCategoryId: string;
}

export interface InvoiceEditorActDraft {
  id: number;
  number: string;
  title?: string | null;
  date: string;
  amount: number;
  invoiceNumber?: string | null;
  clientId?: number | null;
  clientName?: string | null;
  status: ActStatus;
}

export interface InvoiceEditorDraftState {
  invoiceId?: number | null;
  mode: 'create' | 'edit';
  presentation: 'modal' | 'page';
  pageViewMode: boolean;
  form: InvoiceEditorFormDraft;
  lineItems: InvoiceEditorLineItemDraft[];
  selectedActs: InvoiceEditorActDraft[];
  selectedClientCaseIds: number[];
  installmentPlan?: InstallmentPlanInput | null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function readString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function readStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];
}

function readNumberArray(value: unknown): number[] {
  return Array.isArray(value)
    ? value.filter((item): item is number => typeof item === 'number' && Number.isFinite(item))
    : [];
}

function readInstallmentPlan(value: unknown): InstallmentPlanInput | null {
  if (!isRecord(value)) return null;
  if (!Array.isArray(value.items)) return null;

  return {
    total: typeof value.total === 'number' && Number.isFinite(value.total) ? value.total : 0,
    downPayment: typeof value.downPayment === 'number' && Number.isFinite(value.downPayment) ? value.downPayment : 0,
    annualRate: typeof value.annualRate === 'number' && Number.isFinite(value.annualRate) ? value.annualRate : 0,
    months: typeof value.months === 'number' && Number.isFinite(value.months) ? value.months : 1,
    startDate: readString(value.startDate),
    roundingMode:
      value.roundingMode === 'down' || value.roundingMode === 'up' || value.roundingMode === 'none'
        ? value.roundingMode
        : 'none',
    roundingStep: typeof value.roundingStep === 'number' && Number.isFinite(value.roundingStep) ? value.roundingStep : null,
    toPay: typeof value.toPay === 'number' && Number.isFinite(value.toPay) ? value.toPay : 0,
    overpay: typeof value.overpay === 'number' && Number.isFinite(value.overpay) ? value.overpay : 0,
    loanAmount: typeof value.loanAmount === 'number' && Number.isFinite(value.loanAmount) ? value.loanAmount : null,
    baseMonthlyPayment:
      typeof value.baseMonthlyPayment === 'number' && Number.isFinite(value.baseMonthlyPayment)
        ? value.baseMonthlyPayment
        : null,
    roundedMonthlyPayment:
      typeof value.roundedMonthlyPayment === 'number' && Number.isFinite(value.roundedMonthlyPayment)
        ? value.roundedMonthlyPayment
        : null,
    items: value.items
      .filter(isRecord)
      .map((item, index) => ({
        sequence:
          typeof item.sequence === 'number' && Number.isFinite(item.sequence)
            ? item.sequence
            : index + 1,
        date: readString(item.date),
        principal: typeof item.principal === 'number' && Number.isFinite(item.principal) ? item.principal : 0,
        interest: typeof item.interest === 'number' && Number.isFinite(item.interest) ? item.interest : 0,
        payment: typeof item.payment === 'number' && Number.isFinite(item.payment) ? item.payment : 0,
        balance: typeof item.balance === 'number' && Number.isFinite(item.balance) ? item.balance : 0,
      })),
  };
}

function normalizeInvoiceEditorDraft(raw: unknown): InvoiceEditorDraftState | null {
  if (!isRecord(raw)) return null;

  const mode = raw.mode === 'create' || raw.mode === 'edit' ? raw.mode : null;
  if (!mode) return null;

  const presentation = raw.presentation === 'page' ? 'page' : 'modal';
  const form = isRecord(raw.form) ? raw.form : null;
  if (!form) return null;

  const type = PAYMENT_KINDS.includes(form.type as PaymentKind) ? (form.type as PaymentKind) : 'Income';
  const status = PAYMENT_STATUSES.includes(form.status as PaymentStatus) ? (form.status as PaymentStatus) : 'Pending';

  const lineItems = Array.isArray(raw.lineItems)
    ? raw.lineItems
        .filter(isRecord)
        .map((item, index): InvoiceEditorLineItemDraft => ({
          id: readString(item.id) || `draft-line-${index}`,
          name: readString(item.name),
          quantity: readString(item.quantity),
          unit: readString(item.unit),
          price: readString(item.price),
          serviceCategoryId: readString(item.serviceCategoryId),
        }))
    : [];

  const selectedActs = Array.isArray(raw.selectedActs)
    ? raw.selectedActs
        .filter(isRecord)
        .map((item): InvoiceEditorActDraft | null => {
          const id = typeof item.id === 'number' && Number.isFinite(item.id) ? item.id : 0;
          if (id <= 0) return null;

          return {
            id,
            number: readString(item.number),
            title: typeof item.title === 'string' ? item.title : null,
            date: readString(item.date),
            amount: typeof item.amount === 'number' && Number.isFinite(item.amount) ? item.amount : 0,
            invoiceNumber: typeof item.invoiceNumber === 'string' ? item.invoiceNumber : null,
            clientId: typeof item.clientId === 'number' && Number.isFinite(item.clientId) ? item.clientId : null,
            clientName: typeof item.clientName === 'string' ? item.clientName : null,
            status: ACT_STATUSES.includes(item.status as ActStatus) ? (item.status as ActStatus) : 'Created',
          };
        })
        .filter((item): item is InvoiceEditorActDraft => item !== null)
    : [];

  return {
    invoiceId: typeof raw.invoiceId === 'number' && Number.isFinite(raw.invoiceId) ? raw.invoiceId : null,
    mode,
    presentation,
    pageViewMode: typeof raw.pageViewMode === 'boolean' ? raw.pageViewMode : true,
    form: {
      number: readString(form.number),
      date: readString(form.date),
      dueDate: readString(form.dueDate),
      amount: readString(form.amount),
      type,
      status,
      clientId: readString(form.clientId),
      contractId: readString(form.contractId),
      clientCaseReference: readString(form.clientCaseReference),
      description: readString(form.description),
      actReference: readString(form.actReference),
      paidDate: readString(form.paidDate),
      responsibleIds: readStringArray(form.responsibleIds),
    },
    lineItems,
    selectedActs,
    selectedClientCaseIds: readNumberArray(raw.selectedClientCaseIds),
    installmentPlan: readInstallmentPlan(raw.installmentPlan),
  };
}

export function storeInvoiceEditorDraft(draft: InvoiceEditorDraftState) {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(INVOICE_EDITOR_STORAGE_KEY, JSON.stringify(draft));
  } catch {
    /* ignore storage errors */
  }
}

export function peekInvoiceEditorDraft(): InvoiceEditorDraftState | null {
  if (typeof window === 'undefined') return null;
  try {
    const raw = window.localStorage.getItem(INVOICE_EDITOR_STORAGE_KEY);
    if (!raw) return null;
    const parsed = normalizeInvoiceEditorDraft(JSON.parse(raw));
    if (!parsed) {
      clearInvoiceEditorDraft();
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
}

export function clearInvoiceEditorDraft() {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.removeItem(INVOICE_EDITOR_STORAGE_KEY);
  } catch {
    /* ignore storage errors */
  }
}
