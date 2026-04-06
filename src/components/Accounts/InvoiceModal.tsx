import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ArrowLeft, Ban, Check, Copy, Download, Link2, List, Loader2, Pencil, Plus, Printer, Send, Trash2, UserRoundSearch, X } from 'lucide-react';
import type {
  Act,
  ActStatus,
  ActResponsible,
  Client,
  ClientCase,
  Contract,
  ContractClient,
  ContractInput,
  Invoice,
  InvoiceInput,
  InstallmentPlanInput,
  InvoiceLineItem,
  Payment,
  PaymentKind,
  PaymentStatus,
} from '../../types';
import { fromInputToApiDate, formatLocalYMD, toDateInputValue } from '../../utils/dateUtils';
import { useTranslation } from '../../hooks/useTranslation';
import { useDictionaries } from '../../hooks/useDictionaries';
import { apiService } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import type { InvoiceEditorDraftState } from '../../utils/invoiceEditorBridge';
import { storeInvoiceEditorDraft } from '../../utils/invoiceEditorBridge';
import { useDialog } from '../../hooks/useDialog';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { CaseModal } from '../Clients/CaseModal';
import { ContractModal } from '../Contracts/ContractModal';
import { LookupSelect } from '../ui/LookupSelect';
import { InstallmentPlanner } from './InstallmentPlanner';
import { cloneInstallmentPlan, deriveInstallmentPlanFromPayments, parseInstallmentPaymentMetadata } from '../../utils/installmentPlan';

type FormState = {
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

function formatMoneyValue(value: string | number | null | undefined): string {
  const parsed = parseMoneyInput(value);
  if (!Number.isFinite(parsed)) return '0.00';
  const [integer, decimal] = parsed.toFixed(2).split('.');
  return `${groupThousands(integer)}.${decimal}`;
}

function normalizeMoneyInput(value: string | number | null | undefined): string {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? String(value) : '';
  }

  const raw = String(value ?? '')
    .replace(/\s+/g, '')
    .replace(',', '.')
    .replace(/[^\d.]/g, '');

  if (!raw) return '';

  const dotIndex = raw.indexOf('.');
  if (dotIndex < 0) return raw;

  return `${raw.slice(0, dotIndex + 1)}${raw.slice(dotIndex + 1).replace(/\./g, '')}`;
}

function groupThousands(value: string): string {
  return value.replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
}

function parseMoneyInput(value: string | number | null | undefined): number {
  const normalized = normalizeMoneyInput(value);
  if (!normalized || normalized === '.') return 0;

  const parsed = Number.parseFloat(normalized);
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatMoneyInputValue(value: string | number | null | undefined): string {
  const normalized = normalizeMoneyInput(value);
  if (!normalized) return '';

  const hasDecimal = normalized.includes('.');
  const hasTrailingDot = normalized.endsWith('.');
  const [integerPartRaw, decimalPart = ''] = normalized.split('.', 2);
  const integerPart = groupThousands((integerPartRaw || '0').replace(/^0+(?=\d)/, '') || '0');

  if (hasTrailingDot) {
    return `${integerPart}.`;
  }

  if (hasDecimal) {
    return `${integerPart}.${decimalPart}`;
  }

  return integerPart;
}

function formatMoneyDisplayValue(value: string | number | null | undefined): string {
  return formatMoneyValue(value);
}

function normalizeLineItems(source?: InvoiceLineItem[] | null, fallbackAmount?: number): LineItem[] {
  if (source && source.length > 0) {
    return source.map((item, index) => ({
      id: `${Date.now()}-${index}`,
      name: item.name ?? '',
      quantity: String(item.quantity ?? 0),
      unit: item.unit ?? 'РЎв‚¬РЎвЂљ',
      price: formatMoneyValue(item.price),
    }));
  }

  return [
    {
      id: `${Date.now()}-base`,
      name: '',
      quantity: '1',
      unit: 'РЎв‚¬РЎвЂљ',
      price: formatMoneyValue(fallbackAmount ?? 0),
    },
  ];
}

type ActOption = Pick<Act, 'id' | 'number' | 'title' | 'date' | 'amount' | 'invoiceNumber' | 'clientId' | 'clientName' | 'status'>;
type ClientCaseOption = Pick<ClientCase, 'id' | 'clientId' | 'title' | 'status' | 'createdAt'>;

interface InvoiceModalProps {
  open: boolean;
  mode: 'create' | 'edit';
  invoice: Invoice | null;
  resumeDraft?: InvoiceEditorDraftState | null;
  onClose: () => void;
  onBack?: () => void;
  onOpenClient?: (clientId: number) => void;
  onSubmit: (payload: InvoiceInput) => Promise<void>;
  submitting: boolean;
  errorMessage?: string | null;
  clients: Client[];
  responsibles: ActResponsible[];
  lookupsLoading: boolean;
  lookupsError?: string | null;
  defaultClientId?: number;
  defaultResponsibleId?: number;
  defaultType?: PaymentKind;
  lockType?: boolean;
  presentation?: 'modal' | 'page';
  relatedPayments?: Payment[];
  paymentsLoading?: boolean;
  onRefreshRequested?: () => Promise<void> | void;
  onAddRelatedPayment?: () => void;
  onEditRelatedPayment?: (payment: Payment) => void;
  onDeleteRelatedPayment?: (payment: Payment) => Promise<void> | void;
}

function normalizeInvoiceToForm(
  invoice: Invoice | null,
  defaultClientId?: number,
  defaultResponsibleId?: number,
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
      contractId: '',
      clientCaseReference: '',
      description: '',
      actReference: '',
      paidDate: '',
      responsibleIds: defaultResponsibleId ? [String(defaultResponsibleId)] : [],
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
    contractId: invoice.contractId ? String(invoice.contractId) : '',
    clientCaseReference: invoice.clientCaseReference ?? '',
    description: invoice.description ?? '',
    actReference: invoice.actReference ?? '',
    paidDate: toDateInputValue(invoice.paidDate) || '',
    responsibleIds: (invoice.responsibles ?? []).map((item) => String(item.id)).slice(0, 1),
  };
}

function normalizeInvoiceClientCaseIds(invoice: Invoice | null): number[] {
  const rawIds = invoice?.clientCaseIds?.length
    ? invoice.clientCaseIds
    : invoice?.clientCaseId
      ? [invoice.clientCaseId]
      : [];

  return Array.from(new Set(rawIds.filter((value): value is number => typeof value === 'number' && value > 0)));
}

function normalizeInvoiceClientCases(invoice: Invoice | null): ClientCaseOption[] {
  if (invoice?.clientCases?.length) {
    return invoice.clientCases.map((item) => ({
      id: item.id,
      clientId: item.clientId,
      title: item.title,
      status: item.status,
      createdAt: item.createdAt,
    }));
  }

  if (invoice?.clientCaseId) {
    return [
      {
        id: invoice.clientCaseId,
        clientId: invoice.clientId ?? 0,
        title: invoice.clientCaseTitle ?? '',
        status: '',
        createdAt: invoice.createdAt,
      },
    ];
  }

  return [];
}

function normalizeInvoiceActs(invoice: Invoice | null): ActOption[] {
  if (invoice?.acts?.length) {
    return invoice.acts.map((item) => ({
      id: item.id,
      number: item.number,
      title: item.title ?? undefined,
      date: item.date,
      amount: item.amount,
      invoiceNumber: invoice.number,
      clientId: item.clientId ?? undefined,
      clientName: item.clientName ?? undefined,
      status: item.status,
    }));
  }

  if (invoice?.actId) {
    return [
      {
        id: invoice.actId,
        number: invoice.actNumber ?? invoice.number,
        title: invoice.actTitle ?? undefined,
        date: invoice.date,
        amount: invoice.amount,
        invoiceNumber: invoice.actNumber ?? invoice.number,
        clientId: invoice.clientId ?? undefined,
        clientName: invoice.clientName ?? undefined,
        status: (invoice.actStatus ?? 'Created') as ActStatus,
      },
    ];
  }

  return [];
}

function actLabel(act: ActOption | null): string {
  if (!act) return '';
  const parts: string[] = [];
  if (act.number) parts.push(`РІвЂћвЂ“${act.number}`);
  if (act.title) parts.push(act.title);
  return parts.join(' Р вЂ™Р’В· ');
}

function clientCaseLabel(clientCase: ClientCaseOption | null): string {
  if (!clientCase) return '';
  return clientCase.title?.trim() || `Р вЂќР ВµР В»Р С• #${clientCase.id}`;
}

function clientCaseStatusLabel(statusRaw?: string | null): string {
  const status = (statusRaw ?? '').trim();
  const normalized = status.toLowerCase();

  if (!normalized) return 'Р вЂР ВµР В· РЎРѓРЎвЂљР В°РЎвЂљРЎС“РЎРѓР В°';
  if (normalized.includes('open')) return 'Р С›РЎвЂљР С”РЎР‚РЎвЂ№РЎвЂљР С•';
  if (normalized.includes('onhold') || normalized.includes('hold')) return 'Р СџРЎР‚Р С‘Р С•РЎРѓРЎвЂљР В°Р Р…Р С•Р Р†Р В»Р ВµР Р…Р С•';
  if (normalized.includes('closed')) return 'Р вЂ”Р В°Р С”РЎР‚РЎвЂ№РЎвЂљР С•';
  if (normalized.includes('Р Р† РЎР‚Р В°Р В±Р С•РЎвЂљР Вµ') || normalized.includes('Р В°Р С”РЎвЂљР С‘Р Р†')) return 'Р С›РЎвЂљР С”РЎР‚РЎвЂ№РЎвЂљР С•';
  if (normalized.includes('Р С•Р В¶Р С‘Р Т‘')) return 'Р СџРЎР‚Р С‘Р С•РЎРѓРЎвЂљР В°Р Р…Р С•Р Р†Р В»Р ВµР Р…Р С•';
  if (normalized.includes('Р В·Р В°Р С”РЎР‚РЎвЂ№')) return 'Р вЂ”Р В°Р С”РЎР‚РЎвЂ№РЎвЂљР С•';

  return status;
}

function contractLabel(contract: Contract | null): string {
  if (!contract) return 'Р вЂР ВµР В· Р Т‘Р С•Р С–Р С•Р Р†Р С•РЎР‚Р В°';
  return contract.title?.trim() ? `${contract.number} РІР‚вЂќ ${contract.title}` : contract.number;
}

function formatDate(value?: string | null): string {
  if (!value) return '';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

type StatusTimelineEntry = {
  key: string;
  title: string;
  description: string;
  sourceLabel: string;
  timestampLabel: string;
  sortValue: number;
  isCurrent: boolean;
};

type InvoiceStatusSnapshot = {
  status: PaymentStatus;
  reason: string;
  totalAmount: number;
  paidAmount: number;
  outstandingAmount: number;
  sourcePayments: Payment[];
  timeline: StatusTimelineEntry[];
};

function getStatusSourcePayments(payments: Payment[]): Payment[] {
  const linkedPayments = payments.filter((payment) => payment.documentKind !== 'Invoice');
  return linkedPayments.length > 0 ? linkedPayments : payments;
}

function resolvePaidAmount(payment: Payment): number {
  if (Number.isFinite(payment.paidAmount) && payment.paidAmount > 0) {
    return payment.paidAmount;
  }

  return payment.status === 'Completed' ? Math.max(payment.amount ?? 0, 0) : 0;
}

function resolveOutstandingAmount(payment: Payment): number {
  if (payment.status === 'Completed' || payment.status === 'Cancelled') {
    return 0;
  }

  if (Number.isFinite(payment.outstandingAmount) && payment.outstandingAmount > 0) {
    return payment.outstandingAmount;
  }

  return Math.max((payment.amount ?? 0) - resolvePaidAmount(payment), 0);
}

function paymentSourceLabel(payment: Payment): string {
  const description = payment.description?.trim();
  if (description) return description;
  if (payment.documentKind === 'Invoice' && payment.account) return `Р РЋРЎвЂЎР ВµРЎвЂљ РІвЂћвЂ“${payment.account}`;
  if (payment.account) return `Р СџР В»Р В°РЎвЂљР ВµР В¶ Р С—Р С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ РІвЂћвЂ“${payment.account}`;
  return `Р СџР В»Р В°РЎвЂљР ВµР В¶ #${payment.id}`;
}

function deriveInvoiceStatusFromPayments(
  payments: Payment[],
  fallbackStatus: PaymentStatus,
  fallbackDueDate?: string | null,
): PaymentStatus {
  const sourcePayments = getStatusSourcePayments(payments);

  if (sourcePayments.length === 0) {
    if (fallbackStatus === 'Completed' || fallbackStatus === 'Cancelled' || fallbackStatus === 'Processing' || fallbackStatus === 'Overdue') {
      return fallbackStatus;
    }

    const normalizedDueDate = toDateInputValue(fallbackDueDate ?? null);
    if (normalizedDueDate && normalizedDueDate < formatLocalYMD(new Date())) {
      return 'Overdue';
    }

    return 'Pending';
  }

  if (sourcePayments.every((payment) => payment.status === 'Cancelled')) {
    return 'Cancelled';
  }

  if (sourcePayments.every((payment) => payment.status === 'Completed')) {
    return 'Completed';
  }

  if (sourcePayments.some((payment) => payment.status === 'Overdue')) {
    return 'Overdue';
  }

  const hasProgress = sourcePayments.some(
    (payment) =>
      payment.status === 'Completed'
      || payment.status === 'Processing'
      || payment.hasPartialPayment
      || resolvePaidAmount(payment) > 0,
  );

  return hasProgress ? 'Processing' : 'Pending';
}

function invoiceStatusLabel(status: PaymentStatus): string {
  switch (status) {
    case 'Completed':
      return 'Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…';
    case 'Processing':
      return 'Р В§Р В°РЎРѓРЎвЂљР С‘РЎвЂЎР Р…Р С• Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…';
    case 'Overdue':
      return 'Р СџРЎР‚Р С•РЎРѓРЎР‚Р С•РЎвЂЎР ВµР Р…';
    case 'Cancelled':
      return 'Р С›РЎвЂљР СР ВµР Р…Р ВµР Р…';
    case 'Pending':
    default:
      return 'Р СњР Вµ Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…';
  }
}

function parseStatusTimelineEntry(note: string, payment: Payment, index: number): StatusTimelineEntry | null {
  const trimmed = note.trim().replace(/^[РІР‚Сћ*-]\s*/u, '');
  if (!trimmed) return null;

  if (!/РЎРѓРЎвЂљР В°РЎвЂљРЎС“РЎРѓ|Р С•Р С—Р В»Р В°РЎвЂЎ|Р С•РЎРѓРЎвЂљР В°РЎвЂљР С•Р С”|РЎРѓРЎР‚Р С•Р С”|Р С—Р В»Р В°Р Р…Р С•Р Р†|Р Т‘Р В°РЎвЂљР В° Р С—Р В»Р В°РЎвЂљР ВµР В¶Р В°|Р С—Р ВµРЎР‚Р ВµР Р…Р ВµРЎРѓ|Р С—Р ВµРЎР‚Р ВµР Р…Р С•РЎРѓ|РЎвЂЎР В°РЎРѓРЎвЂљР С‘РЎвЂЎ/iu.test(trimmed)) {
    return null;
  }

  const match = trimmed.match(/^(\d{2})\.(\d{2})\.(\d{4}) \((\d{2}):(\d{2}):(\d{2})\)\s*[РІР‚вЂќ-]\s*(.+?):\s*(.+)$/u);
  if (match) {
    const [, day, month, year, hour, minute, second, author, message] = match;
    const sortValue = Date.UTC(
      Number.parseInt(year, 10),
      Number.parseInt(month, 10) - 1,
      Number.parseInt(day, 10),
      Number.parseInt(hour, 10),
      Number.parseInt(minute, 10),
      Number.parseInt(second, 10),
    );

    return {
      key: `${payment.id}-${index}-${sortValue}`,
      title: message,
      description: author,
      sourceLabel: paymentSourceLabel(payment),
      timestampLabel: `${day}.${month}.${year} ${hour}:${minute}`,
      sortValue,
      isCurrent: false,
    };
  }

  const fallbackTimestamp = Date.parse(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt ?? '') || 0;
  return {
    key: `${payment.id}-${index}-${trimmed}`,
    title: trimmed,
    description: 'Р РЋР С‘РЎРѓРЎвЂљР ВµР СР Р…Р С•Р Вµ Р С‘Р В·Р СР ВµР Р…Р ВµР Р…Р С‘Р Вµ Р С—Р С• РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…Р С•Р СРЎС“ Р С—Р В»Р В°РЎвЂљР ВµР В¶РЎС“',
    sourceLabel: paymentSourceLabel(payment),
    timestampLabel: formatDate(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt) || 'Р вЂР ВµР В· Р Т‘Р В°РЎвЂљРЎвЂ№',
    sortValue: fallbackTimestamp,
    isCurrent: false,
  };
}

function buildInvoiceStatusSnapshot(
  payments: Payment[],
  fallbackStatus: PaymentStatus,
  fallbackDueDate?: string | null,
  fallbackAmount?: string | number | null,
): InvoiceStatusSnapshot {
  const sourcePayments = getStatusSourcePayments(payments);
  const status = deriveInvoiceStatusFromPayments(payments, fallbackStatus, fallbackDueDate);
  const fallbackAmountValue = parseMoneyInput(fallbackAmount);
  const aggregatedTotalFromPayments = sourcePayments.reduce((sum, payment) => {
    const paymentAmount = Math.max(payment.amount ?? 0, 0);
    const resolvedAmount = Math.max(paymentAmount, resolvePaidAmount(payment) + resolveOutstandingAmount(payment));
    return sum + resolvedAmount;
  }, 0);

  const totalAmount = sourcePayments.length > 0
    ? Math.max(fallbackAmountValue, aggregatedTotalFromPayments)
    : (fallbackAmountValue > 0 ? fallbackAmountValue : aggregatedTotalFromPayments);
  const paidAmount = sourcePayments.reduce((sum, payment) => sum + resolvePaidAmount(payment), 0);
  const rawOutstandingAmount = sourcePayments.reduce((sum, payment) => sum + resolveOutstandingAmount(payment), 0);
  const outstandingAmount = rawOutstandingAmount > 0
    ? rawOutstandingAmount
    : Math.max(totalAmount - paidAmount, 0);

  let reason = 'Р РЋР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘ Р С—Р С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ Р ВµРЎвЂ°Р Вµ Р Р…Р Вµ РЎРѓР С•Р В·Р Т‘Р В°Р Р…РЎвЂ№.';
  if (status === 'Completed') {
    reason = 'Р вЂ™РЎРѓР Вµ РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘ Р С—РЎР‚Р С•Р Р†Р ВµР Т‘Р ВµР Р…РЎвЂ№, Р С•РЎРѓРЎвЂљР В°РЎвЂљР С”Р В° Р С” Р С•Р С—Р В»Р В°РЎвЂљР Вµ Р Р…Р ВµРЎвЂљ.';
  } else if (status === 'Cancelled') {
    reason = 'Р вЂ™РЎРѓР Вµ РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘ Р С—Р С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ Р С•РЎвЂљР СР ВµР Р…Р ВµР Р…РЎвЂ№.';
  } else if (status === 'Overdue') {
    reason = paidAmount > 0 && totalAmount > 0
      ? `Р СџР С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ Р ВµРЎРѓРЎвЂљРЎРЉ Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№ Р Р…Р В° ${formatCurrencySmart(paidAmount).short}, Р Р…Р С• Р С•РЎРѓРЎвЂљР В°РЎвЂљР С•Р С” ${formatCurrencySmart(outstandingAmount).short} РЎС“Р В¶Р Вµ Р С—РЎР‚Р С•РЎРѓРЎР‚Р С•РЎвЂЎР ВµР Р….`
      : 'Р РЋРЎР‚Р С•Р С” Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№ Р С‘РЎРѓРЎвЂљР ВµР С”, Р В° РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘ Р С—Р С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ Р ВµРЎвЂ°Р Вµ Р Р…Р Вµ Р В·Р В°Р С”РЎР‚РЎвЂ№РЎвЂљРЎвЂ№.';
  } else if (status === 'Processing') {
    reason = totalAmount > 0
      ? `Р СџР С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…Р С• ${formatCurrencySmart(paidAmount).short} Р С‘Р В· ${formatCurrencySmart(totalAmount).short}. Р С›РЎРѓРЎвЂљР В°РЎвЂљР С•Р С” ${formatCurrencySmart(outstandingAmount).short} Р ВµРЎвЂ°Р Вµ Р Р…Р Вµ Р В·Р В°Р С”РЎР‚РЎвЂ№РЎвЂљ.`
      : 'Р СџР С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ Р ВµРЎРѓРЎвЂљРЎРЉ РЎвЂЎР В°РЎРѓРЎвЂљР С‘РЎвЂЎР Р…Р В°РЎРЏ Р С•Р С—Р В»Р В°РЎвЂљР В°, Р Р…Р С• Р С•Р Р… Р ВµРЎвЂ°Р Вµ Р Р…Р Вµ Р В·Р В°Р С”РЎР‚РЎвЂ№РЎвЂљ Р С—Р С•Р В»Р Р…Р С•РЎРѓРЎвЂљРЎРЉРЎР‹.';
  } else if (sourcePayments.length > 0) {
    reason = 'Р РЋР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘ РЎРѓР С•Р В·Р Т‘Р В°Р Р…РЎвЂ№, Р Р…Р С• Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№ Р С—Р С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“ Р С—Р С•Р С”Р В° Р Р…Р Вµ Р С—Р С•РЎРѓРЎвЂљРЎС“Р С—Р С‘Р В»Р С‘.';
  }

  const historyEntries = payments
    .flatMap((payment) =>
      (payment.systemNotes ?? '')
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean)
        .map((line, index) => parseStatusTimelineEntry(line, payment, index))
        .filter((entry): entry is StatusTimelineEntry => entry !== null),
    )
    .sort((left, right) => right.sortValue - left.sortValue);

  const currentEntry: StatusTimelineEntry = {
    key: `current-${fallbackStatus}-${paidAmount}-${outstandingAmount}`,
    title: `Р СћР ВµР С”РЎС“РЎвЂ°Р С‘Р в„– РЎРѓРЎвЂљР В°РЎвЂљРЎС“РЎРѓ: ${invoiceStatusLabel(fallbackStatus)}`,
    description: reason,
    sourceLabel: sourcePayments.length > 0
      ? `Р РЋР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№РЎвЂ¦ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р ВµР в„–: ${sourcePayments.length}`
      : 'Р РЋРЎвЂљР В°РЎвЂљРЎС“РЎРѓ Р С—Р С•Р С”Р В° Р С•Р С—РЎР‚Р ВµР Т‘Р ВµР В»РЎРЏР ВµРЎвЂљРЎРѓРЎРЏ Р С—Р С• РЎРѓР В°Р СР С•Р СРЎС“ РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“',
    timestampLabel: historyEntries[0]?.timestampLabel ?? 'Р РЋР ВµР в„–РЎвЂЎР В°РЎРѓ',
    sortValue: historyEntries[0]?.sortValue ?? Date.now(),
    isCurrent: true,
  };

  return {
    status,
    reason,
    totalAmount,
    paidAmount,
    outstandingAmount,
    sourcePayments,
    timeline: [currentEntry, ...historyEntries],
  };
}


export function InvoiceModal({
  open,
  mode,
  invoice,
  resumeDraft,
  onClose,
  onBack,
  onOpenClient,
  onSubmit,
  submitting,
  errorMessage,
  clients,
  responsibles = [],
  lookupsLoading,
  lookupsError,
  defaultClientId,
  defaultResponsibleId,
  defaultType,
  lockType = false,
  presentation = 'modal',
  relatedPayments = [],
  paymentsLoading = false,
  onRefreshRequested,
  onAddRelatedPayment,
  onEditRelatedPayment,
  onDeleteRelatedPayment,
}: InvoiceModalProps) {
  const { t } = useTranslation();
  const { confirm } = useDialog();
  const toast = useToast();
  const typeOptions: PaymentKind[] = ['Income', 'Expense'];
  const typeBadgeClasses: Record<PaymentKind, string> = {
    Income: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    Expense: 'bg-rose-100 text-rose-700 border border-rose-200',
  };
  const isTypeLocked = lockType && mode === 'create';
  const [form, setForm] = useState<FormState>(() =>
    normalizeInvoiceToForm(invoice, defaultClientId, defaultResponsibleId, defaultType),
  );
  const [lineItems, setLineItems] = useState<LineItem[]>([]);
  const [localError, setLocalError] = useState<string | null>(null);
  const [clientPickerOpen, setClientPickerOpen] = useState(false);
  const [clientPickerSearch, setClientPickerSearch] = useState('');
  const [selectedActs, setSelectedActs] = useState<ActOption[]>(() => normalizeInvoiceActs(invoice));
  const [actOptions, setActOptions] = useState<ActOption[]>(() => normalizeInvoiceActs(invoice));
  const [clientCaseOptions, setClientCaseOptions] = useState<ClientCaseOption[]>(() => normalizeInvoiceClientCases(invoice));
  const [selectedClientCaseIds, setSelectedClientCaseIds] = useState<number[]>(() => normalizeInvoiceClientCaseIds(invoice));
  const [caseDropdownOpen, setCaseDropdownOpen] = useState(false);
  const [createCaseOpen, setCreateCaseOpen] = useState(false);
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
  const [contractOptions, setContractOptions] = useState<Contract[]>([]);
  const [contractsLoading, setContractsLoading] = useState(false);
  const [contractsError, setContractsError] = useState<string | null>(null);
  const [createContractOpen, setCreateContractOpen] = useState(false);
  const [createContractSubmitting, setCreateContractSubmitting] = useState(false);
  const [createContractError, setCreateContractError] = useState<string | null>(null);
  const [clientCasesLoading, setClientCasesLoading] = useState(false);
  const [clientCasesError, setClientCasesError] = useState<string | null>(null);
  const [actsLoading, setActsLoading] = useState(false);
  const [actsError, setActsError] = useState<string | null>(null);
  const [invoiceRules, setInvoiceRules] = useState({ maxClientCasesPerInvoice: 1, maxActsPerInvoice: 1 });
  const [numberEditedManually, setNumberEditedManually] = useState(false);
  const [displayPayments, setDisplayPayments] = useState<Payment[]>(relatedPayments);
  const [installmentPlan, setInstallmentPlan] = useState<InstallmentPlanInput | null>(() => cloneInstallmentPlan(deriveInstallmentPlanFromPayments(relatedPayments)));
  const [installmentModalOpen, setInstallmentModalOpen] = useState(false);
  const [installmentPlanTouched, setInstallmentPlanTouched] = useState(false);
  const lastErrorToastRef = useRef<string | null>(null);
  const lastLocalErrorToastRef = useRef<string | null>(null);
  const lastAutoResponsibleIdRef = useRef<string>('');
  const lastAutoContractClientIdRef = useRef<string>('');

  const calculateLineItemsTotal = useCallback(
    (items: LineItem[]) => {
      const total = items.reduce((sum, item) => sum + parseMoneyInput(item.quantity) * parseMoneyInput(item.price), 0);
      return formatMoneyDisplayValue(total);
    },
    [],
  );

  const { invoiceUnits, invoiceItemNames } = useDictionaries();
  const unitOptions = useMemo(() => invoiceUnits.map((item) => item.name).filter((name) => Boolean(name?.trim())), [invoiceUnits]);
  const lineItemNameOptions = useMemo(() => invoiceItemNames.map((item) => item.name).filter((name) => Boolean(name?.trim())), [invoiceItemNames]);

  const handleLineItemPriceChange = (id: string, rawValue: string) => {
    updateLineItem(id, { price: formatMoneyInputValue(rawValue) });
  };

  const handleLineItemPriceBlur = (id: string, rawValue: string) => {
    const formatted = formatMoneyInputValue(rawValue);
    updateLineItem(id, { price: formatted || formatMoneyDisplayValue(0) });
  };

  const applyLineItemTemplate = (id: string, name: string) => {
    const normalized = name.trim().toLowerCase();
    if (!normalized) return;
    const template = invoiceItemNames.find((item) => item.name.trim().toLowerCase() === normalized);
    if (!template) return;
    updateLineItem(id, {
      unit: template.unit?.trim() || undefined,
      price: template.defaultPrice !== undefined && template.defaultPrice !== null
        ? formatMoneyDisplayValue(template.defaultPrice)
        : undefined,
    });
  };

  const [actDropdownOpen, setActDropdownOpen] = useState(false);

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
  const relatedInstallmentPlan = useMemo(
    () => cloneInstallmentPlan(deriveInstallmentPlanFromPayments(relatedPayments)),
    [relatedPayments],
  );
  const selectedResponsibleId = form.responsibleIds[0] ?? '';
  const selectedContract = useMemo(
    () => contractOptions.find((contract) => String(contract.id) === form.contractId) ?? null,
    [contractOptions, form.contractId],
  );
  const defaultContractClients = useMemo<ContractClient[]>(
    () =>
      selectedClient
        ? [
            {
              id: selectedClient.id,
              name: selectedClient.name,
              company: selectedClient.company,
              clientStatusId: selectedClient.clientStatusId ?? null,
              clientStatus: selectedClient.clientStatus ?? null,
            },
          ]
        : [],
    [selectedClient],
  );
  const responsibleOptions = useMemo(() => {
    const options = [...responsibles];
    const hasSelectedResponsible = options.some(
      (responsible) => String(responsible.id) === selectedResponsibleId,
    );

    if (!hasSelectedResponsible && selectedResponsibleId) {
      const fallbackLabel =
        invoice?.responsibles?.find((responsible) => String(responsible.id) === selectedResponsibleId)
          ?.fullName
        ?? (selectedClient?.responsibleUserId
          && String(selectedClient.responsibleUserId) === selectedResponsibleId
            ? selectedClient.responsibleUserName
            : null)
        ?? `#${selectedResponsibleId}`;

      options.push({
        id: Number(selectedResponsibleId),
        fullName: fallbackLabel,
      });
    }

    return options
      .sort((left, right) => left.fullName.localeCompare(right.fullName, 'ru', { sensitivity: 'base' }))
      .map((responsible) => ({
        value: String(responsible.id),
        label: responsible.fullName,
      }));
  }, [
    invoice?.responsibles,
    responsibles,
    selectedClient?.responsibleUserId,
    selectedClient?.responsibleUserName,
    selectedResponsibleId,
  ]);
  const selectedClientCasesSummary = useMemo(() => {
    if (!selectedClientCaseIds.length) return '';
    return selectedClientCaseIds
      .map((id) => {
        const match = clientCaseOptions.find((item) => item.id === id) ?? null;
        return clientCaseLabel(match ?? { id, clientId: selectedClient?.id ?? 0, title: '', status: '', createdAt: '' });
      })
      .join(', ');
  }, [clientCaseOptions, selectedClient?.id, selectedClientCaseIds]);
  const selectedActsSummary = useMemo(
    () => selectedActs.map((item) => actLabel(item)).filter(Boolean).join(', '),
    [selectedActs],
  );
  const selectedClientCases = useMemo(
    () => selectedClientCaseIds
      .map((id) => clientCaseOptions.find((item) => item.id === id) ?? null)
      .filter((item): item is ClientCaseOption => item !== null),
    [clientCaseOptions, selectedClientCaseIds],
  );
  const availableClientCases = useMemo(
    () => clientCaseOptions.filter((item) => !selectedClientCaseIds.includes(item.id)),
    [clientCaseOptions, selectedClientCaseIds],
  );
  const availableActOptions = useMemo(
    () => actOptions.filter((item) => !selectedActs.some((selected) => selected.id === item.id)),
    [actOptions, selectedActs],
  );

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
  const isPageMode = presentation === 'page';

  useEffect(() => {
    if (!open) {
      return;
    }

    let cancelled = false;

    async function loadInvoiceRules() {
      try {
        const response = await apiService.getInvoiceRulesSettings();
        if (!cancelled) {
          setInvoiceRules({
            maxClientCasesPerInvoice: Math.max(1, response.maxClientCasesPerInvoice || 1),
            maxActsPerInvoice: Math.max(1, response.maxActsPerInvoice || 1),
          });
        }
      } catch {
        if (!cancelled) {
          setInvoiceRules({ maxClientCasesPerInvoice: 1, maxActsPerInvoice: 1 });
        }
      }
    }

    void loadInvoiceRules();
    return () => {
      cancelled = true;
    };
  }, [open]);

  useEffect(() => {
    let cancelled = false;

    async function loadContractsByClient() {
      if (!open || !form.clientId) {
        setContractOptions([]);
        setContractsError(null);
        return;
      }

      const clientId = Number.parseInt(form.clientId, 10);
      if (!Number.isFinite(clientId) || clientId <= 0) {
        setContractOptions([]);
        setContractsError(null);
        return;
      }

      setContractsLoading(true);
      setContractsError(null);
      try {
        const response = await apiService.getContracts({
          clientId,
          page: 1,
          pageSize: 200,
          sortBy: 'createdAt',
          sortDir: 'desc',
        });
        if (!cancelled) {
          setContractOptions(response.items ?? []);
        }
      } catch (error) {
        if (!cancelled) {
          setContractOptions([]);
          setContractsError(error instanceof Error ? error.message : 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р В·Р В°Р С–РЎР‚РЎС“Р В·Р С‘РЎвЂљРЎРЉ Р Т‘Р С•Р С–Р С•Р Р†Р С•РЎР‚РЎвЂ№');
        }
      } finally {
        if (!cancelled) setContractsLoading(false);
      }
    }

    void loadContractsByClient();
    return () => {
      cancelled = true;
    };
  }, [open, form.clientId]);

  useEffect(() => {
    if (!open || mode !== 'create' || !form.clientId || form.contractId || contractOptions.length === 0) {
      return;
    }

    if (lastAutoContractClientIdRef.current === form.clientId) {
      return;
    }

    const latestContract = contractOptions[0];
    if (!latestContract) {
      return;
    }

    setForm((prev) => (
      prev.clientId === form.clientId && !prev.contractId
        ? { ...prev, contractId: String(latestContract.id) }
        : prev
    ));
    lastAutoContractClientIdRef.current = form.clientId;
  }, [contractOptions, form.clientId, form.contractId, mode, open]);

  useEffect(() => {
    if (!form.contractId) return;
    if (contractOptions.some((contract) => String(contract.id) === form.contractId)) return;
    setForm((prev) => ({ ...prev, contractId: '' }));
  }, [contractOptions, form.contractId]);

  const handleCreateContractFromInvoice = useCallback(async (payload: ContractInput) => {
    setCreateContractSubmitting(true);
    setCreateContractError(null);
    try {
      const created = await apiService.createContract(payload);
      setContractOptions((prev) => {
        const next = [created, ...prev.filter((contract) => contract.id !== created.id)];
        return next.sort((left, right) => {
          const leftDate = Date.parse(left.date ?? '');
          const rightDate = Date.parse(right.date ?? '');
          if (Number.isFinite(leftDate) && Number.isFinite(rightDate) && leftDate !== rightDate) {
            return rightDate - leftDate;
          }
          return right.id - left.id;
        });
      });
      setForm((prev) => ({ ...prev, contractId: String(created.id) }));
      lastAutoContractClientIdRef.current = form.clientId;
      setCreateContractOpen(false);
      toast.success('Р вЂќР С•Р С–Р С•Р Р†Р С•РЎР‚ РЎРѓР С•Р В·Р Т‘Р В°Р Р… Р С‘ Р Р†РЎвЂ№Р В±РЎР‚Р В°Р Р… Р Р† РЎРѓРЎвЂЎР ВµРЎвЂљР Вµ.', 'Р вЂќР С•Р С–Р С•Р Р†Р С•РЎР‚РЎвЂ№');
    } catch (error) {
      setCreateContractError(extractDisplayErrorMessage(error, 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р Т‘Р С•Р С–Р С•Р Р†Р С•РЎР‚'));
    } finally {
      setCreateContractSubmitting(false);
    }
  }, [form.clientId, toast]);

  useEffect(() => {
    let cancelled = false;

    async function loadClientCasesByClient() {
      if (!open || !form.clientId) {
        setClientCaseOptions([]);
        setClientCasesError(null);
        return;
      }

      const clientId = Number.parseInt(form.clientId, 10);
      if (!Number.isFinite(clientId) || clientId <= 0) {
        setClientCaseOptions([]);
        setClientCasesError(null);
        return;
      }

      setClientCasesLoading(true);
      setClientCasesError(null);
      try {
        const response = await apiService.getCases({ clientId });
        if (!cancelled) {
          const nextOptions = [...(response ?? [])].sort(
            (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(),
          );
          setClientCaseOptions(nextOptions);
        }
      } catch (error) {
        if (!cancelled) {
          setClientCaseOptions([]);
          setClientCasesError(error instanceof Error ? error.message : 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р В·Р В°Р С–РЎР‚РЎС“Р В·Р С‘РЎвЂљРЎРЉ Р Т‘Р ВµР В»Р В°');
        }
      } finally {
        if (!cancelled) setClientCasesLoading(false);
      }
    }

    void loadClientCasesByClient();
    return () => {
      cancelled = true;
    };
  }, [open, form.clientId]);

  useEffect(() => {
    if (open) {
      const nextForm = resumeDraft?.form ?? normalizeInvoiceToForm(invoice, defaultClientId, defaultResponsibleId, defaultType);
      setForm(nextForm);
      setLineItems(resumeDraft?.lineItems ?? normalizeLineItems(invoice?.lineItems, invoice?.amount));
      setSelectedActs(resumeDraft?.selectedActs ?? normalizeInvoiceActs(invoice));
      setActOptions(normalizeInvoiceActs(invoice));
      setClientCaseOptions(normalizeInvoiceClientCases(invoice));
      setSelectedClientCaseIds(resumeDraft?.selectedClientCaseIds ?? normalizeInvoiceClientCaseIds(invoice));
      setCaseDropdownOpen(false);
      setCreateCaseOpen(false);
      setActDropdownOpen(false);
      setActsError(null);
      setClientCasesError(null);
      setCreateActError(null);
      setLocalError(null);
      setInstallmentPlan(cloneInstallmentPlan(resumeDraft?.installmentPlan ?? relatedInstallmentPlan));
      setInstallmentModalOpen(false);
      setInstallmentPlanTouched(Boolean(resumeDraft?.installmentPlan));
      setNumberEditedManually(Boolean(resumeDraft?.form?.number));
      if (presentation === 'page') {
        setPageViewMode(resumeDraft?.pageViewMode ?? true);
      }
      lastAutoContractClientIdRef.current = nextForm.contractId ? nextForm.clientId : '';
    }
  }, [open, invoice, resumeDraft, defaultClientId, defaultResponsibleId, defaultType, presentation]);

  useEffect(() => {
    if (!open || resumeDraft?.installmentPlan || installmentPlanTouched) {
      return;
    }

    setInstallmentPlan(cloneInstallmentPlan(relatedInstallmentPlan));
  }, [open, relatedInstallmentPlan, installmentPlanTouched, resumeDraft?.installmentPlan]);

  useEffect(() => {
    setDisplayPayments(relatedPayments);
  }, [relatedPayments]);

  useEffect(() => {
    if (form.type !== 'Income' && installmentPlan) {
      setInstallmentPlan(null);
      setInstallmentModalOpen(false);
      setInstallmentPlanTouched(true);
    }
  }, [form.type, installmentPlan]);

  useEffect(() => {
    if (!open || mode !== 'create') return;

    const nextAutoResponsibleId = selectedClient?.responsibleUserId
      ? String(selectedClient.responsibleUserId)
      : defaultResponsibleId
        ? String(defaultResponsibleId)
        : '';

    setForm((prev) => {
      const currentResponsibleId = prev.responsibleIds[0] ?? '';
      const shouldReplace =
        !currentResponsibleId || currentResponsibleId === lastAutoResponsibleIdRef.current;

      if (!shouldReplace) {
        lastAutoResponsibleIdRef.current = nextAutoResponsibleId;
        return prev;
      }

      lastAutoResponsibleIdRef.current = nextAutoResponsibleId;
      if (currentResponsibleId === nextAutoResponsibleId) {
        return prev;
      }

      return {
        ...prev,
        responsibleIds: nextAutoResponsibleId ? [nextAutoResponsibleId] : [],
      };
    });
  }, [defaultResponsibleId, mode, open, selectedClient?.id, selectedClient?.responsibleUserId]);

  useEffect(() => {
    if (!errorMessage) {
      lastErrorToastRef.current = null;
      return;
    }

    if (lastErrorToastRef.current === errorMessage) {
      return;
    }

    toast.error(
      errorMessage,
      mode === 'edit' ? 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р С•Р В±Р Р…Р С•Р Р†Р С‘РЎвЂљРЎРЉ РЎРѓРЎвЂЎР ВµРЎвЂљ' : 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎР ВµРЎвЂљ',
    );
    lastErrorToastRef.current = errorMessage;
  }, [errorMessage, mode, toast]);

  useEffect(() => {
    if (!localError) {
      lastLocalErrorToastRef.current = null;
      return;
    }

    if (lastLocalErrorToastRef.current === localError) {
      return;
    }

    toast.error(localError, 'Р СџРЎР‚Р С•Р Р†Р ВµРЎР‚РЎРЉРЎвЂљР Вµ Р Т‘Р В°Р Р…Р Р…РЎвЂ№Р Вµ РЎРѓРЎвЂЎР ВµРЎвЂљР В°');
    lastLocalErrorToastRef.current = localError;
  }, [localError, toast]);

  useEffect(() => {
    if (!open || mode !== 'create' || numberEditedManually) return;

    let cancelled = false;

    async function loadNextNumber() {
      try {
        const next = await apiService.getNextInvoiceNumber(fromInputToApiDate(form.date) ?? form.date);
        if (!cancelled) {
          setForm((prev) => ({ ...prev, number: next.nextNumber || prev.number }));
        }
      } catch {
        // noop: Р С•РЎРѓРЎвЂљР В°Р Р†Р В»РЎРЏР ВµР С Р Р…Р С•Р СР ВµРЎР‚ Р С”Р В°Р С” Р ВµРЎРѓРЎвЂљРЎРЉ
      }
    }

    void loadNextNumber();
    return () => {
      cancelled = true;
    };
  }, [open, mode, form.date, numberEditedManually]);

  useEffect(() => {
    const total = calculateLineItemsTotal(lineItems);
    setForm((prev) => (prev.amount === total ? prev : { ...prev, amount: total }));
  }, [lineItems, calculateLineItemsTotal]);

  useEffect(() => {
    if (!unitOptions.length) return;
    setLineItems((prev) => prev.map((item) => (item.unit.trim() ? item : { ...item, unit: unitOptions[0] })));
  }, [unitOptions]);

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
        unit: unitOptions[0] || 'РЎв‚¬РЎвЂљ',
        price: formatMoneyDisplayValue(0),
      },
    ]);
  };

  const removeLineItem = async (id: string) => {
    const accepted = await confirm({
      title: 'Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ Р С—Р С•Р В·Р С‘РЎвЂ Р С‘РЎР‹',
      description: 'Р СћР С•РЎвЂЎР Р…Р С• Р В»Р С‘ РЎвЂ¦Р С•РЎвЂљР С‘РЎвЂљР Вµ РЎС“Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ Р С—Р С•Р В·Р С‘РЎвЂ Р С‘РЎР‹?',
      confirmLabel: 'Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ',
      cancelLabel: 'Р С›РЎвЂљР СР ВµР Р…Р В°',
      variant: 'danger',
    });
    if (!accepted) return;

    setLineItems((prev) => {
      if (prev.length <= 1) return prev;
      const next = prev.filter((item) => item.id !== id);
      setForm((current) => ({ ...current, amount: calculateLineItemsTotal(next) }));
      return next;
    });
  };

  useEffect(() => {
    let cancelled = false;

    async function loadActsByClient() {
      if (!open || !form.clientId) {
        setActOptions([]);
        setActsError(null);
        return;
      }

      setActsLoading(true);
      try {
        const response = await apiService.getActs({
          page: 1,
          pageSize: 200,
          sortBy: 'date',
          sortDir: 'desc',
          clientId: Number(form.clientId),
        });
        if (cancelled) return;

        const items = response.items ?? [];
        setActOptions(items.map((act) => ({
          id: act.id,
          number: act.number,
          title: act.title ?? undefined,
          date: act.date,
          amount: act.amount,
          invoiceNumber: act.invoiceNumber ?? undefined,
          clientId: act.clientId ?? undefined,
          clientName: act.clientName ?? undefined,
          status: act.status,
        })));
        setActsError(null);
      } catch (err) {
        if (cancelled) return;
        setActsError(err instanceof Error ? err.message : 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р В·Р В°Р С–РЎР‚РЎС“Р В·Р С‘РЎвЂљРЎРЉ Р В°Р С”РЎвЂљРЎвЂ№');
        setActOptions([]);
      } finally {
        if (!cancelled) {
          setActsLoading(false);
        }
      }
    }

    void loadActsByClient();
    return () => {
      cancelled = true;
    };
  }, [form.clientId, open]);

  const handleActSelect = (act: ActOption) => {
    if (!selectedActs.some((item) => item.id === act.id) && selectedActs.length >= invoiceRules.maxActsPerInvoice) {
      toast.error(`Р вЂ™ РЎРѓРЎвЂЎРЎвЂРЎвЂљР Вµ Р СР С•Р В¶Р Р…Р С• Р Р†РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р Р…Р Вµ Р В±Р С•Р В»Р ВµР Вµ ${invoiceRules.maxActsPerInvoice} Р В°Р С”РЎвЂљР В°(Р С•Р Р†).`, 'Р С›Р С–РЎР‚Р В°Р Р…Р С‘РЎвЂЎР ВµР Р…Р С‘Р Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°');
      return;
    }

    setSelectedActs((prev) => (prev.some((item) => item.id === act.id) ? prev : [...prev, act]));
  };

  const removeSelectedAct = (actId: number) => {
    setSelectedActs((prev) => prev.filter((item) => item.id !== actId));
  };

  const toggleAct = (act: ActOption) => {
    if (selectedActs.some((item) => item.id === act.id)) {
      removeSelectedAct(act.id);
      return;
    }

    handleActSelect(act);
  };

  const handleTypeSelect = (nextType: PaymentKind) => {
    if (isTypeLocked) return;
    setForm((prev) => ({
      ...prev,
      type: nextType,
    }));
  };

  const openClientPicker = () => {
    setClientPickerSearch('');
    setClientPickerOpen(true);
  };

  const handleClientPick = (client: Client) => {
    const nextClientId = String(client.id);
    const clientChanged = form.clientId !== nextClientId;
    if (clientChanged) {
      lastAutoContractClientIdRef.current = '';
      setSelectedActs([]);
      setActOptions([]);
      setSelectedClientCaseIds([]);
      setClientCaseOptions([]);
      setCaseDropdownOpen(false);
      setActDropdownOpen(false);
    }
    setForm((prev) => ({
      ...prev,
      clientId: nextClientId,
      contractId: clientChanged ? '' : prev.contractId,
    }));
    setClientPickerOpen(false);
  };

  const handleClientCaseSelect = (clientCase: ClientCaseOption) => {
    if (!selectedClientCaseIds.includes(clientCase.id) && selectedClientCaseIds.length >= invoiceRules.maxClientCasesPerInvoice) {
      toast.error(`Р вЂ™ РЎРѓРЎвЂЎРЎвЂРЎвЂљР Вµ Р СР С•Р В¶Р Р…Р С• Р Р†РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р Р…Р Вµ Р В±Р С•Р В»Р ВµР Вµ ${invoiceRules.maxClientCasesPerInvoice} Р Т‘Р ВµР В»Р В°(Р Т‘Р ВµР В»).`, 'Р С›Р С–РЎР‚Р В°Р Р…Р С‘РЎвЂЎР ВµР Р…Р С‘Р Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°');
      return;
    }

    setSelectedClientCaseIds((prev) => (prev.includes(clientCase.id) ? prev : [...prev, clientCase.id]));
  };

  const removeSelectedClientCase = (clientCaseId: number) => {
    setSelectedClientCaseIds((prev) => prev.filter((id) => id !== clientCaseId));
  };

  const toggleClientCase = (clientCaseId: number) => {
    if (selectedClientCaseIds.includes(clientCaseId)) {
      removeSelectedClientCase(clientCaseId);
      return;
    }

    const clientCase = clientCaseOptions.find((item) => item.id === clientCaseId);
    if (clientCase) {
      handleClientCaseSelect(clientCase);
    }
  };

  const handleCreateClientCase = async (patch: Partial<ClientCase>) => {
    if (!form.clientId) {
      throw new Error('Р РЋР Р…Р В°РЎвЂЎР В°Р В»Р В° Р Р†РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С–');
    }

    const created = await apiService.createCase({
      clientId: Number(form.clientId),
      title: patch.title?.trim() ?? '',
      description: patch.description?.trim(),
      status: patch.status ?? 'Open',
    });

    setClientCaseOptions((prev) => {
      const next = [created, ...prev.filter((item) => item.id !== created.id)];
      return next.sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime());
    });
    setSelectedClientCaseIds((prev) => {
      if (prev.includes(created.id)) return prev;
      if (prev.length >= invoiceRules.maxClientCasesPerInvoice) {
        toast.error(`Р вЂ™ РЎРѓРЎвЂЎРЎвЂРЎвЂљР Вµ Р СР С•Р В¶Р Р…Р С• Р Р†РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р Р…Р Вµ Р В±Р С•Р В»Р ВµР Вµ ${invoiceRules.maxClientCasesPerInvoice} Р Т‘Р ВµР В»Р В°(Р Т‘Р ВµР В»).`, 'Р С›Р С–РЎР‚Р В°Р Р…Р С‘РЎвЂЎР ВµР Р…Р С‘Р Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°');
        return prev;
      }

      return [...prev, created.id];
    });
    setCaseDropdownOpen(false);
    setCreateCaseOpen(false);
  };

  const handleCreateAct = async () => {
    if (!draftAct.number.trim()) {
      setCreateActError(t('actNumber') ?? 'Р СњР С•Р СР ВµРЎР‚ Р В°Р С”РЎвЂљР В° Р С•Р В±РЎРЏР В·Р В°РЎвЂљР ВµР В»Р ВµР Р…');
      return;
    }

    const actAmount = parseMoneyInput(draftAct.amount || form.amount);
    if (!Number.isFinite(actAmount) || actAmount < 0) {
      setCreateActError(t('invoiceAmount') ?? 'Р вЂ™Р Р†Р ВµР Т‘Р С‘РЎвЂљР Вµ Р С”Р С•РЎР‚РЎР‚Р ВµР С”РЎвЂљР Р…РЎС“РЎР‹ РЎРѓРЎС“Р СР СРЎС“');
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
        contractId: form.contractId ? Number(form.contractId) : undefined,
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

      setActOptions((prev) => [option, ...prev.filter((item) => item.id !== option.id)]);
      setSelectedActs((prev) => {
        if (prev.some((item) => item.id === option.id)) return prev;
        if (prev.length >= invoiceRules.maxActsPerInvoice) {
          toast.error(`Р вЂ™ РЎРѓРЎвЂЎРЎвЂРЎвЂљР Вµ Р СР С•Р В¶Р Р…Р С• Р Р†РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р Р…Р Вµ Р В±Р С•Р В»Р ВµР Вµ ${invoiceRules.maxActsPerInvoice} Р В°Р С”РЎвЂљР В°(Р С•Р Р†).`, 'Р С›Р С–РЎР‚Р В°Р Р…Р С‘РЎвЂЎР ВµР Р…Р С‘Р Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°');
          return prev;
        }

        return [...prev, option];
      });
      setActDropdownOpen(false);
      setCreateActOpen(false);
      setDraftAct({
        number: '',
        title: '',
        date: form.date || formatLocalYMD(new Date()),
        amount: form.amount,
        comment: '',
      });
    } catch (err) {
      setCreateActError(err instanceof Error ? err.message : 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р В°Р С”РЎвЂљ');
    } finally {
      setCreatingAct(false);
    }
  };

  const handleChange = (field: keyof FormState) => (
    event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>,
  ) => {
    const value = event.target.value;
    if (field === 'number') {
      setNumberEditedManually(true);
    }
    setForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleResponsibleChange = (value: string) => {
    setForm((prev) => ({
      ...prev,
      responsibleIds: value ? [value] : [],
    }));
  };

  const statusSnapshot = useMemo(
    () => buildInvoiceStatusSnapshot(displayPayments, form.status, form.dueDate, form.amount),
    [displayPayments, form.amount, form.dueDate, form.status],
  );

  const effectiveInvoiceStatus = form.status;
  const installmentPlanAmountMismatch = useMemo(() => {
    if (!installmentPlan) return false;
    return Math.abs(parseMoneyInput(form.amount) - installmentPlan.total) > 0.009;
  }, [form.amount, installmentPlan]);


  const buildInvoicePayload = useCallback((statusOverride?: PaymentStatus, planOverride?: InstallmentPlanInput | null): InvoiceInput => {
    const safeAmount = parseMoneyInput(form.amount);
    const clientCaseIds = selectedClientCaseIds.filter((id) => Number.isFinite(id) && id > 0);
    const actIds = selectedActs.map((item) => item.id).filter((id) => Number.isFinite(id) && id > 0);
    const effectiveInstallmentPlan = planOverride === undefined ? installmentPlan : planOverride;
    const shouldIncludeInstallmentPlan =
      mode === 'create'
        ? Boolean(effectiveInstallmentPlan)
        : installmentPlanTouched || Boolean(relatedInstallmentPlan) || Boolean(effectiveInstallmentPlan);

    return {
      number: form.number.trim() || String(invoice?.number ?? ''),
      date: fromInputToApiDate(form.date) ?? form.date,
      dueDate: form.dueDate ? fromInputToApiDate(form.dueDate) ?? form.dueDate : undefined,
      amount: safeAmount,
      type: form.type,
      status: statusOverride ?? form.status,
      clientId: Number(form.clientId),
      contractId: form.contractId ? Number(form.contractId) : undefined,
      clientCaseId: clientCaseIds[0] ?? null,
      clientCaseIds,
      clientCaseReference: form.clientCaseReference.trim() ? form.clientCaseReference.trim() : undefined,
      description: form.description.trim() ? form.description.trim() : undefined,
      actReference: form.actReference.trim() ? form.actReference.trim() : undefined,
      actId: actIds[0] ?? null,
      actIds,
      paidDate: form.paidDate ? fromInputToApiDate(form.paidDate) ?? form.paidDate : undefined,
      responsibleIds: form.responsibleIds.slice(0, 1).map((id) => Number(id)).filter((id) => Number.isFinite(id) && id > 0),
      lineItems: lineItems
        .map((item) => ({
          name: item.name.trim(),
          quantity: parseMoneyInput(item.quantity),
          unit: item.unit.trim() || 'РЎв‚¬РЎвЂљ',
          price: parseMoneyInput(item.price),
        }))
        .filter((item) => item.name && Number.isFinite(item.quantity) && Number.isFinite(item.price) && item.quantity >= 0 && item.price >= 0),
      installmentPlan: shouldIncludeInstallmentPlan
        ? (effectiveInstallmentPlan ? cloneInstallmentPlan(effectiveInstallmentPlan) : null)
        : undefined,
    };
  }, [form, installmentPlan, installmentPlanTouched, invoice?.number, lineItems, mode, relatedInstallmentPlan, selectedActs, selectedClientCaseIds]);

  const buildInvoiceEditorDraft = useCallback((): InvoiceEditorDraftState => ({
    invoiceId: invoice?.id ?? null,
    mode,
    presentation: isPageMode ? 'page' : 'modal',
    pageViewMode,
    form,
    lineItems,
    selectedActs,
    selectedClientCaseIds,
    installmentPlan,
  }), [form, installmentPlan, invoice?.id, isPageMode, lineItems, mode, pageViewMode, selectedActs, selectedClientCaseIds]);

  const handleOpenClientCard = (clientId: number) => {
    storeInvoiceEditorDraft(buildInvoiceEditorDraft());
    onOpenClient?.(clientId);
  };

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setLocalError(null);

    const number = form.number.trim();
    if (!number) {
      setLocalError(t('invoiceNumber') ?? 'Р СњР С•Р СР ВµРЎР‚ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В° Р С•Р В±РЎРЏР В·Р В°РЎвЂљР ВµР В»Р ВµР Р…');
      return;
    }
    if (!form.date) {
      setLocalError(t('invoiceDate') ?? 'Р вЂќР В°РЎвЂљР В° Р С•Р В±РЎРЏР В·Р В°РЎвЂљР ВµР В»РЎРЉР Р…Р В°');
      return;
    }
    if (!form.clientId) {
      setLocalError(t('invoiceClientPlaceholder') ?? 'Р вЂ™РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р С”Р В»Р С‘Р ВµР Р…РЎвЂљР В°');
      return;
    }

    const parsedAmount = parseMoneyInput(form.amount);
    if (Number.isNaN(parsedAmount) || parsedAmount < 0) {
      setLocalError(t('invoiceAmount') ?? 'Р вЂ™Р Р†Р ВµР Т‘Р С‘РЎвЂљР Вµ Р С”Р С•РЎР‚РЎР‚Р ВµР С”РЎвЂљР Р…РЎС“РЎР‹ РЎРѓРЎС“Р СР СРЎС“');
      return;
    }

    if (installmentPlanAmountMismatch) {
      setLocalError('Р РЋРЎС“Р СР СР В° РЎРѓРЎвЂЎР ВµРЎвЂљР В° Р С‘Р В·Р СР ВµР Р…Р С‘Р В»Р В°РЎРѓРЎРЉ Р С—Р С•РЎРѓР В»Р Вµ Р Р…Р В°РЎРѓРЎвЂљРЎР‚Р С•Р в„–Р С”Р С‘ РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р С‘. Р СџР ВµРЎР‚Р ВµРЎРѓРЎвЂЎР С‘РЎвЂљР В°Р в„–РЎвЂљР Вµ Р С–РЎР‚Р В°РЎвЂћР С‘Р С”.');
      return;
    }

    if (selectedClientCaseIds.length > invoiceRules.maxClientCasesPerInvoice) {
      setLocalError(`Р вЂ™ РЎРѓРЎвЂЎРЎвЂРЎвЂљР Вµ Р СР С•Р В¶Р Р…Р С• РЎС“Р С”Р В°Р В·Р В°РЎвЂљРЎРЉ Р Р…Р Вµ Р В±Р С•Р В»Р ВµР Вµ ${invoiceRules.maxClientCasesPerInvoice} Р Т‘Р ВµР В»Р В°(Р Т‘Р ВµР В»).`);
      return;
    }

    if (selectedActs.length > invoiceRules.maxActsPerInvoice) {
      setLocalError(`Р вЂ™ РЎРѓРЎвЂЎРЎвЂРЎвЂљР Вµ Р СР С•Р В¶Р Р…Р С• РЎС“Р С”Р В°Р В·Р В°РЎвЂљРЎРЉ Р Р…Р Вµ Р В±Р С•Р В»Р ВµР Вµ ${invoiceRules.maxActsPerInvoice} Р В°Р С”РЎвЂљР В°(Р С•Р Р†).`);
      return;
    }

    const payload = buildInvoicePayload();
    payload.number = number;

    try {
      await onSubmit(payload);
    } catch (err) {
      if (!errorMessage) {
        setLocalError(
          extractDisplayErrorMessage(
            err,
            mode === 'edit' ? 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљРЎРЉ РЎРѓРЎвЂЎР ВµРЎвЂљ' : 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎР ВµРЎвЂљ',
          ),
        );
      }
    }
  };

  const handleOpenInstallmentCalculator = () => {
    if (form.type !== 'Income') return;
    setInstallmentModalOpen(true);
  };

  const handleApplyInstallmentPlan = async (plan: InstallmentPlanInput | null) => {
    const nextPlan = cloneInstallmentPlan(plan);

    setInstallmentPlan(nextPlan);
    setInstallmentPlanTouched(true);
    setLocalError(null);

    if (mode === 'edit' && invoice?.id) {
      try {
        const payload = buildInvoicePayload(undefined, nextPlan);
        await apiService.updateInvoice(invoice.id, payload);
        await onRefreshRequested?.();
        setInstallmentModalOpen(false);
        if (nextPlan) {
          toast.success(`Р вЂњРЎР‚Р В°РЎвЂћР С‘Р С” РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р С‘ РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р ВµР Р…: ${nextPlan.items.length} Р С—Р В»Р В°РЎвЂљР ВµР В¶Р ВµР в„–.`);
        } else {
          toast.success('Р вЂњРЎР‚Р В°РЎвЂћР С‘Р С” РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р С‘ РЎС“Р Т‘Р В°Р В»Р ВµР Р….');
        }
      } catch (error) {
        const message = extractDisplayErrorMessage(error, 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљРЎРЉ Р С–РЎР‚Р В°РЎвЂћР С‘Р С” РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р С‘.');
        setLocalError(message);
        toast.error(message);
      }
      return;
    }

    setInstallmentModalOpen(false);

    if (nextPlan) {
      toast.success(
        mode === 'create'
          ? `Р вЂњРЎР‚Р В°РЎвЂћР С‘Р С” РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р ВµР Р… Р Р† РЎвЂћР С•РЎР‚Р СР Вµ РЎРѓРЎвЂЎР ВµРЎвЂљР В°: ${nextPlan.items.length} Р С—Р В»Р В°РЎвЂљР ВµР В¶Р ВµР в„–. Р В§РЎвЂљР С•Р В±РЎвЂ№ РЎРѓР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘, РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљР Вµ РЎРѓРЎвЂЎР ВµРЎвЂљ.`
          : `Р вЂњРЎР‚Р В°РЎвЂћР С‘Р С” РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р С‘ РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р ВµР Р… Р Р† РЎРѓРЎвЂЎР ВµРЎвЂљР Вµ: ${nextPlan.items.length} Р С—Р В»Р В°РЎвЂљР ВµР В¶Р ВµР в„–.`,
      );
    } else {
      toast.success(mode === 'create' ? 'Р вЂњРЎР‚Р В°РЎвЂћР С‘Р С” РЎС“Р Т‘Р В°Р В»Р ВµР Р… Р С‘Р В· РЎвЂћР С•РЎР‚Р СРЎвЂ№ РЎРѓРЎвЂЎР ВµРЎвЂљР В°.' : 'Р вЂњРЎР‚Р В°РЎвЂћР С‘Р С” РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р С‘ Р С•Р В±Р Р…Р С•Р Р†Р В»Р ВµР Р….');
    }
  };

  const invoiceLink = useMemo(() => {
    if (typeof window === 'undefined') return '';
    const invoiceId = invoice?.id ? `/${invoice.id}` : '';
    return `${window.location.origin}/invoices${invoiceId}`;
  }, [invoice?.id]);

  const downloadBlob = (filename: string, blob: Blob) => {
    if (typeof window === 'undefined') return;
    const href = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = href;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(href);
  };

  const printBlob = (blob: Blob) => {
    if (typeof window === 'undefined') return;
    const href = URL.createObjectURL(blob);
    const printWindow = window.open(href, '_blank', 'noopener,noreferrer,width=1024,height=768');
    if (!printWindow) {
      URL.revokeObjectURL(href);
      throw new Error('Р вЂРЎР‚Р В°РЎС“Р В·Р ВµРЎР‚ Р В·Р В°Р В±Р В»Р С•Р С”Р С‘РЎР‚Р С•Р Р†Р В°Р В» Р С•Р С”Р Р…Р С• Р С—Р ВµРЎвЂЎР В°РЎвЂљР С‘. Р В Р В°Р В·РЎР‚Р ВµРЎв‚¬Р С‘РЎвЂљР Вµ Р Р†РЎРѓР С—Р В»РЎвЂ№Р Р†Р В°РЎР‹РЎвЂ°Р С‘Р Вµ Р С•Р С”Р Р…Р В° Р С‘ Р С—Р С•Р Р†РЎвЂљР С•РЎР‚Р С‘РЎвЂљР Вµ.');
    }
    const cleanup = () => URL.revokeObjectURL(href);
    printWindow.addEventListener('load', () => {
      printWindow.focus();
      printWindow.print();
      cleanup();
    });
  };

  const deferClose = useCallback((callback: () => void) => {
    window.setTimeout(callback, 160);
  }, []);


  const handlePageAction = async (action: 'send' | 'print' | 'download' | 'copy' | 'paid' | 'cancel' | 'delete') => {
    const actionMeta = {
      send: { title: 'Р С›РЎвЂљР С—РЎР‚Р В°Р Р†Р С‘РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ', message: 'Р РЋРЎвЂћР С•РЎР‚Р СР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ WORD Р С‘ Р С•РЎвЂљР С—РЎР‚Р В°Р Р†Р С‘РЎвЂљРЎРЉ Р Р…Р В° email Р С”Р В»Р С‘Р ВµР Р…РЎвЂљР В°?', confirmLabel: 'Р С›РЎвЂљР С—РЎР‚Р В°Р Р†Р С‘РЎвЂљРЎРЉ' },
      print: { title: 'Р СџР ВµРЎвЂЎР В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°', message: 'Р РЋРЎвЂћР С•РЎР‚Р СР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ WORD Р С‘ Р С•РЎвЂљР С—РЎР‚Р В°Р Р†Р С‘РЎвЂљРЎРЉ Р ВµР С–Р С• Р Р…Р В° Р С—Р ВµРЎвЂЎР В°РЎвЂљРЎРЉ?', confirmLabel: 'Р СџР ВµРЎвЂЎР В°РЎвЂљРЎРЉ' },
      download: { title: 'Р РЋР С”Р В°РЎвЂЎР В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ', message: 'Р РЋР С”Р В°РЎвЂЎР В°РЎвЂљРЎРЉ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В° Р Р† РЎвЂћР С•РЎР‚Р СР В°РЎвЂљР Вµ WORD?', confirmLabel: 'Р РЋР С”Р В°РЎвЂЎР В°РЎвЂљРЎРЉ' },
      copy: { title: 'Р С™Р С•Р С—Р С‘РЎРЏ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°', message: 'Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р С”Р С•Р С—Р С‘РЎР‹ РЎвЂљР ВµР С”РЎС“РЎвЂ°Р ВµР С–Р С• РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°?', confirmLabel: 'Р С™Р С•Р С—Р С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ' },
      paid: { title: 'Р С›РЎвЂљР СР ВµРЎвЂљР С‘РЎвЂљРЎРЉ Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…Р Р…РЎвЂ№Р С', message: 'Р С›РЎвЂљР СР ВµРЎвЂљР С‘РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ Р С”Р В°Р С” Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…Р Р…РЎвЂ№Р в„–?', confirmLabel: 'Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…' },
      cancel: { title: 'Р С›РЎвЂљР СР ВµР Р…Р С‘РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ', message: 'Р СџР С•Р Т‘РЎвЂљР Р†Р ВµРЎР‚Р Т‘Р С‘РЎвЂљР Вµ Р С•РЎвЂљР СР ВµР Р…РЎС“ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°.', confirmLabel: 'Р С›РЎвЂљР СР ВµР Р…Р С‘РЎвЂљРЎРЉ' },
      delete: { title: 'Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ', message: 'Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ Р В±Р ВµР В· Р Р†Р С•Р В·Р СР С•Р В¶Р Р…Р С•РЎРѓРЎвЂљР С‘ Р Р†Р С•РЎРѓРЎРѓРЎвЂљР В°Р Р…Р С•Р Р†Р В»Р ВµР Р…Р С‘РЎРЏ?', confirmLabel: 'Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ' },
    }[action];

    const accepted = await confirm({
      title: actionMeta.title,
      description: actionMeta.message,
      confirmLabel: actionMeta.confirmLabel,
      cancelLabel: 'Р С›РЎвЂљР СР ВµР Р…Р В°',
      variant: action === 'cancel' || action === 'delete' ? 'danger' : 'primary',
    });
    if (!accepted) return;

    try {
      if (action === 'send') {
        if (!invoice?.id) {
          throw new Error('Р РЋР Р…Р В°РЎвЂЎР В°Р В»Р В° РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљР Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљ, Р В·Р В°РЎвЂљР ВµР С Р СР С•Р В¶Р Р…Р С• Р С•РЎвЂљР С—РЎР‚Р В°Р Р†Р С‘РЎвЂљРЎРЉ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ.');
        }
        await apiService.sendInvoiceByEmail(invoice.id, { format: 'word', email: selectedClient?.email || undefined });
        toast.success(
          selectedClient?.email
            ? `Р вЂќР С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ WORD Р С•РЎвЂљР С—РЎР‚Р В°Р Р†Р В»Р ВµР Р… Р Р…Р В° ${selectedClient.email}.`
            : 'Р вЂќР С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ WORD Р С•РЎвЂљР С—РЎР‚Р В°Р Р†Р В»Р ВµР Р… Р С”Р В»Р С‘Р ВµР Р…РЎвЂљРЎС“ Р С—Р С• email.',
          'Р РЋРЎвЂЎР ВµРЎвЂљ Р С•РЎвЂљР С—РЎР‚Р В°Р Р†Р В»Р ВµР Р…',
        );
        return;
      }

      if (action === 'print') {
        if (!invoice?.id) {
          throw new Error('Р РЋР Р…Р В°РЎвЂЎР В°Р В»Р В° РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљР Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљ, Р В·Р В°РЎвЂљР ВµР С Р СР С•Р В¶Р Р…Р С• Р С—Р ВµРЎвЂЎР В°РЎвЂљР В°РЎвЂљРЎРЉ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ.');
        }
        const blob = await apiService.downloadInvoiceDocument(invoice.id, 'word');
        printBlob(blob);
        return;
      }

      if (action === 'download') {
        if (!invoice?.id) {
          throw new Error('Р РЋР Р…Р В°РЎвЂЎР В°Р В»Р В° РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљР Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљ, Р В·Р В°РЎвЂљР ВµР С Р СР С•Р В¶Р Р…Р С• РЎРѓР С”Р В°РЎвЂЎР В°РЎвЂљРЎРЉ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљ.');
        }
        const blob = await apiService.downloadInvoiceDocument(invoice.id, 'word');
        downloadBlob(`invoice-${form.number || 'export'}.doc`, blob);
        return;
      }

      if (action === 'copy') {
        const payload = buildInvoicePayload();
        payload.number = `${form.number || 'Р РЋРЎвЂЎРЎвЂРЎвЂљ'} (Р С”Р С•Р С—Р С‘РЎРЏ)`;
        await apiService.createInvoice(payload);
        await onRefreshRequested?.();
        toast.success('Р РЋР С•Р В·Р Т‘Р В°Р Р…Р В° Р С”Р С•Р С—Р С‘РЎРЏ РЎвЂљР ВµР С”РЎС“РЎвЂ°Р ВµР С–Р С• РЎРѓРЎвЂЎР ВµРЎвЂљР В°.', 'Р С™Р С•Р С—Р С‘РЎРЏ РЎРѓР С•Р В·Р Т‘Р В°Р Р…Р В°');
        return;
      }

      if ((action === 'paid' || action === 'cancel') && invoice?.id) {
        const nextStatus: PaymentStatus = action === 'paid' ? 'Completed' : 'Cancelled';
        const payload = buildInvoicePayload(nextStatus);
        await apiService.updateInvoice(invoice.id, payload);
        setForm((prev) => ({ ...prev, status: nextStatus }));
        setDisplayPayments((prev) => prev.map((payment) => (
          payment.documentKind === 'Invoice'
            ? {
                ...payment,
                status: nextStatus,
                paidDate: nextStatus === 'Completed'
                  ? (form.paidDate || payment.paidDate || formatLocalYMD(new Date()))
                  : undefined,
              }
            : payment
        )));
        await onRefreshRequested?.();
        toast.success(
          action === 'paid' ? 'Р РЋРЎвЂљР В°РЎвЂљРЎС“РЎРѓ РЎРѓРЎвЂЎР ВµРЎвЂљР В° Р С•Р В±Р Р…Р С•Р Р†Р В»Р ВµР Р…: Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р….' : 'Р РЋРЎвЂљР В°РЎвЂљРЎС“РЎРѓ РЎРѓРЎвЂЎР ВµРЎвЂљР В° Р С•Р В±Р Р…Р С•Р Р†Р В»Р ВµР Р…: Р С•РЎвЂљР СР ВµР Р…Р ВµР Р….',
          'Р РЋРЎвЂЎР ВµРЎвЂљ Р С•Р В±Р Р…Р С•Р Р†Р В»Р ВµР Р…',
        );
        return;
      }

      if (action === 'delete' && invoice?.id) {
        await apiService.deleteInvoice(invoice.id);
        await onRefreshRequested?.();
        toast.success('Р РЋРЎвЂЎР ВµРЎвЂљ РЎС“Р Т‘Р В°Р В»Р ВµР Р….');
        deferClose(onClose);
        return;
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Р СњР Вµ РЎС“Р Т‘Р В°Р В»Р С•РЎРѓРЎРЉ Р Р†РЎвЂ№Р С—Р С•Р В»Р Р…Р С‘РЎвЂљРЎРЉ Р Т‘Р ВµР в„–РЎРѓРЎвЂљР Р†Р С‘Р Вµ Р С—Р С• РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“.');
    }
  };

  const amountValue = parseMoneyInput(form.amount);
  const lineItemsTotal = lineItems.reduce((sum, item) => sum + parseMoneyInput(item.quantity) * parseMoneyInput(item.price), 0);
  const lineItemsCount = lineItems.filter((item) => item.name.trim()).length;
  const printableAmount = formatCurrencySmart(amountValue).full;
  const statusMeta: Record<PaymentStatus, { label: string; className: string }> = {
    Pending: { label: 'Р РЋР С•Р В·Р Т‘Р В°Р Р…', className: 'bg-slate-100 text-slate-700 border border-slate-200' },
    Processing: { label: 'Р вЂ™ РЎР‚Р В°Р В±Р С•РЎвЂљР Вµ', className: 'bg-blue-100 text-blue-700 border border-blue-200' },
    Completed: { label: 'Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…', className: 'bg-emerald-100 text-emerald-700 border border-emerald-200' },
    Cancelled: { label: 'Р С›РЎвЂљР СР ВµР Р…Р ВµР Р…', className: 'bg-rose-100 text-rose-700 border border-rose-200' },
    Overdue: { label: 'Р СџРЎР‚Р С•РЎРѓРЎР‚Р С•РЎвЂЎР ВµР Р…', className: 'bg-amber-100 text-amber-700 border border-amber-200' },
  };
  const relatedPaymentStatusMeta: Record<PaymentStatus, string> = {
    Pending: 'bg-slate-100 text-slate-700',
    Processing: 'bg-blue-100 text-blue-700',
    Completed: 'bg-emerald-100 text-emerald-700',
    Cancelled: 'bg-rose-100 text-rose-700',
    Overdue: 'bg-amber-100 text-amber-700',
  };
  const relatedPaymentStatusLabel: Record<PaymentStatus, string> = {
    Pending: t('pending') ?? 'Р С›Р В¶Р С‘Р Т‘Р В°Р ВµРЎвЂљРЎРѓРЎРЏ',
    Processing: t('processingStatus') ?? 'Р вЂ™ Р С•Р В±РЎР‚Р В°Р В±Р С•РЎвЂљР С”Р Вµ',
    Completed: t('completedStatus') ?? 'Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…',
    Cancelled: t('cancelledStatus') ?? 'Р С›РЎвЂљР СР ВµР Р…РЎвЂР Р…',
    Overdue: t('overdue') ?? 'Р СџРЎР‚Р С•РЎРѓРЎР‚Р С•РЎвЂЎР ВµР Р…',
  };
  const invoiceStatusMeta: Record<PaymentStatus, { label: string; className: string }> = {
    Pending: { label: 'Р СњР Вµ Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…', className: 'bg-slate-100 text-slate-700 border border-slate-200' },
    Processing: { label: 'Р В§Р В°РЎРѓРЎвЂљР С‘РЎвЂЎР Р…Р С• Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…', className: 'bg-blue-100 text-blue-700 border border-blue-200' },
    Completed: { label: 'Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…', className: 'bg-emerald-100 text-emerald-700 border border-emerald-200' },
    Cancelled: { label: 'Р С›РЎвЂљР СР ВµР Р…Р ВµР Р…', className: 'bg-slate-100 text-slate-700 border border-slate-200' },
    Overdue: { label: 'Р СџРЎР‚Р С•РЎРѓРЎР‚Р С•РЎвЂЎР ВµР Р…', className: 'bg-rose-100 text-rose-700 border border-rose-200' },
  };
  const invoicePaymentStatusLabel: Record<PaymentStatus, string> = {
    Pending: 'Р СњР Вµ Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…',
    Processing: 'Р В§Р В°РЎРѓРЎвЂљР С‘РЎвЂЎР Р…Р С• Р С•Р С—Р В»Р В°РЎвЂЎР ВµР Р…',
    Completed: 'Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…',
    Cancelled: t('cancelledStatus') ?? 'Р С›РЎвЂљР СР ВµР Р…РЎвЂР Р…',
    Overdue: t('overdue') ?? 'Р СџРЎР‚Р С•РЎРѓРЎР‚Р С•РЎвЂЎР ВµР Р…',
  };
  const currentStatusMeta = invoiceStatusMeta[effectiveInvoiceStatus] ?? invoiceStatusMeta.Pending;
  void statusMeta;
  void relatedPaymentStatusLabel;
  const canManageRelatedPayments = mode === 'edit' && !!invoice?.invoiceEntityId;
  const canShowRelatedPaymentActions = mode === 'edit' && !!onAddRelatedPayment;
  const createCaseStub = useMemo<ClientCase>(() => ({
    id: 0,
    clientId: form.clientId ? Number(form.clientId) : 0,
    title: '',
    description: '',
    status: 'Open',
    createdAt: new Date().toISOString(),
  }), [form.clientId]);
  const statusHistorySection = (
    <div className="mt-5 rounded-2xl border border-slate-200 bg-slate-50/70 p-4">
      <div className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Р РЋРЎвЂљР В°РЎвЂљРЎС“РЎРѓ РЎРѓРЎвЂЎР ВµРЎвЂљР В°</p>
          <span
            className="inline-flex h-6 min-w-6 items-center justify-center rounded-full bg-slate-200 px-2 text-xs font-semibold text-slate-700"
            title="Р С™Р С•Р В»Р С‘РЎвЂЎР ВµРЎРѓРЎвЂљР Р†Р С• РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№РЎвЂ¦ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р ВµР в„–"
          >
            {statusSnapshot.sourcePayments.length}
          </span>
        </div>
        <div className="flex justify-center">
          <span className={`inline-flex min-w-[180px] items-center justify-center rounded-full px-4 py-2 text-sm font-semibold ${currentStatusMeta.className}`}>
            {currentStatusMeta.label}
          </span>
        </div>
        <p className="text-center text-sm text-slate-600">{statusSnapshot.reason}</p>
        <div className="grid grid-cols-2 gap-3">
          <div className="rounded-xl border border-slate-200 bg-white px-3 py-3 text-center">
            <div className="text-[11px] uppercase tracking-wide text-slate-500">Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…Р С•</div>
            <div className="mt-1 font-semibold text-slate-900">{formatCurrencySmart(statusSnapshot.paidAmount).full}</div>
          </div>
          <div className="rounded-xl border border-slate-200 bg-white px-3 py-3 text-center">
            <div className="text-[11px] uppercase tracking-wide text-slate-500">Р С›РЎРѓРЎвЂљР В°РЎвЂљР С•Р С”</div>
            <div className="mt-1 font-semibold text-slate-900">{formatCurrencySmart(statusSnapshot.outstandingAmount).full}</div>
          </div>
        </div>
      </div>

      <div className="mt-4 border-t border-slate-200 pt-4">
        <div className="mb-3 flex items-center justify-between gap-3">
          <div>
            <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">Р ТђРЎР‚Р С•Р Р…Р С‘Р С”Р В° РЎРѓРЎвЂљР В°РЎвЂљРЎС“РЎРѓР В°</p>
            <p className="mt-1 text-xs text-slate-500">Р ВР В·Р СР ВµР Р…Р ВµР Р…Р С‘РЎРЏ РЎРѓР С•Р В±Р С‘РЎР‚Р В°РЎР‹РЎвЂљРЎРѓРЎРЏ Р С—Р С• РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р С Р С—Р В»Р В°РЎвЂљР ВµР В¶Р В°Р С Р С‘ РЎРѓР В°Р СР С•Р СРЎС“ РЎРѓРЎвЂЎР ВµРЎвЂљРЎС“.</p>
          </div>
          <span
            className="inline-flex h-6 min-w-6 items-center justify-center rounded-full bg-slate-200 px-2 text-xs font-semibold text-slate-700"
            title="Р С™Р С•Р В»Р С‘РЎвЂЎР ВµРЎРѓРЎвЂљР Р†Р С• Р В·Р В°Р С—Р С‘РЎРѓР ВµР в„– Р Р† РЎвЂ¦РЎР‚Р С•Р Р…Р С‘Р С”Р Вµ"
          >
            {statusSnapshot.timeline.length}
          </span>
        </div>

        <div className="max-h-72 space-y-3 overflow-y-auto pr-1">
          {statusSnapshot.timeline.map((entry) => (
            <div
              key={entry.key}
              className={`rounded-xl border px-3 py-3 ${
                entry.isCurrent
                  ? 'border-blue-200 bg-blue-50/80'
                  : 'border-slate-200 bg-white'
              }`}
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-slate-900">{entry.title}</p>
                  <p className="mt-1 text-sm text-slate-600">{entry.description}</p>
                </div>
                <span className="whitespace-nowrap text-xs text-slate-400">{entry.timestampLabel}</span>
              </div>
              <p className="mt-2 text-xs text-slate-500">{entry.sourceLabel}</p>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
  const documentsSection = (
    <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
      <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
        <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
          {t('invoiceDocumentsTitle') ?? 'Р РЋР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљРЎвЂ№'}
        </span>
      </header>
      <div className="mt-4 grid grid-cols-1 gap-5 lg:grid-cols-2">
        <div className="space-y-3 text-sm">
          <div className="flex items-start justify-between gap-3">
            <div>
              <span className="font-medium text-slate-700">{t('cases') ?? 'Р вЂќР ВµР В»Р В°'}</span>
              <p className="mt-1 text-xs text-slate-500">
                Р вЂ™РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р Т‘Р ВµР В»Р С• Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С– Р С‘Р В· РЎРѓР С—Р С‘РЎРѓР С”Р В° Р С‘Р В»Р С‘ РЎРѓР С•Р В·Р Т‘Р В°Р в„–РЎвЂљР Вµ Р Р…Р С•Р Р†Р С•Р Вµ.
              </p>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                title="Р вЂ™РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р Т‘Р ВµР В»Р С•"
                aria-label="Р вЂ™РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р Т‘Р ВµР В»Р С•"
                onClick={() => {
                  setCaseDropdownOpen((prev) => !prev);
                  setActDropdownOpen(false);
                }}
                disabled={!form.clientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <List className="h-4 w-4" />
              </button>
              <button
                type="button"
                title="Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р Т‘Р ВµР В»Р С•"
                aria-label="Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р Т‘Р ВµР В»Р С•"
                onClick={() => setCreateCaseOpen(true)}
                disabled={!form.clientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Plus className="h-4 w-4" />
              </button>
            </div>
          </div>

          <div className="min-h-[136px] rounded-xl border border-slate-200 bg-slate-50/70 p-3">
            {!form.clientId ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Р РЋР Р…Р В°РЎвЂЎР В°Р В»Р В° Р Р†РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С–.
              </div>
            ) : selectedClientCaseIds.length === 0 ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Р вЂќР ВµР В»Р В° Р ВµРЎвЂ°Р Вµ Р Р…Р Вµ Р Т‘Р С•Р В±Р В°Р Р†Р В»Р ВµР Р…РЎвЂ№ Р Р† РЎРѓРЎвЂЎР ВµРЎвЂљ.
              </div>
            ) : (
              <div className="space-y-2">
                {selectedClientCaseIds.map((id) => {
                  const selectedCase = clientCaseOptions.find((item) => item.id === id) ?? null;
                  return (
                    <div
                      key={id}
                      className="flex items-start justify-between gap-3 rounded-xl border border-blue-200 bg-white px-3 py-2"
                    >
                      <div className="min-w-0">
                        <div className="truncate text-sm font-medium text-slate-900">
                          {clientCaseLabel(selectedCase ?? { id, clientId: selectedClient?.id ?? 0, title: '', status: '', createdAt: '' })}
                        </div>
                        <div className="mt-1 text-xs text-slate-500">{clientCaseStatusLabel(selectedCase?.status)}</div>
                      </div>
                      <button
                        type="button"
                        onClick={() => removeSelectedClientCase(id)}
                        className="rounded-full p-1 text-slate-500 transition hover:bg-slate-100"
                        aria-label={t('clear') ?? 'Р С›РЎвЂЎР С‘РЎРѓРЎвЂљР С‘РЎвЂљРЎРЉ'}
                      >
                        <X className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  );
                })}
              </div>
            )}
          </div>

          {caseDropdownOpen && (
            <div className="rounded-xl border border-slate-200 bg-white shadow-sm">
              <div className="flex items-center justify-between border-b border-slate-200 px-3 py-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Р вЂ™РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р Т‘Р ВµР В»Р С•</span>
                <button
                  type="button"
                  onClick={() => setCaseDropdownOpen(false)}
                  className="rounded-full p-1 text-slate-500 hover:bg-slate-100"
                  aria-label={t('clear') ?? 'Р С›РЎвЂЎР С‘РЎРѓРЎвЂљР С‘РЎвЂљРЎРЉ'}
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
              <div className="max-h-56 overflow-auto p-2">
                {clientCasesLoading ? (
                  <div className="flex items-center gap-2 px-2 py-3 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" /> {t('loading') ?? 'Р вЂ”Р В°Р С–РЎР‚РЎС“Р В·Р С”Р В°...'}
                  </div>
                ) : clientCasesError ? (
                  <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-600">{clientCasesError}</div>
                ) : clientCaseOptions.length === 0 ? (
                  <div className="rounded-lg border border-dashed border-slate-200 px-3 py-4 text-center text-xs text-slate-500">
                    Р Р€ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С– Р С—Р С•Р С”Р В° Р Р…Р ВµРЎвЂљ Р Т‘Р ВµР В».
                  </div>
                ) : (
                  <div className="space-y-2">
                    {clientCaseOptions.map((clientCase) => {
                      const checked = selectedClientCaseIds.includes(clientCase.id);
                      return (
                        <button
                          key={clientCase.id}
                          type="button"
                          onClick={() => toggleClientCase(clientCase.id)}
                          className={`flex w-full items-start gap-3 rounded-xl border px-3 py-2 text-left transition ${
                            checked ? 'border-blue-200 bg-blue-50' : 'border-slate-200 bg-white hover:bg-slate-50'
                          }`}
                        >
                          <span className={`mt-0.5 inline-flex h-5 w-5 items-center justify-center rounded-md border ${
                            checked ? 'border-blue-500 bg-blue-500 text-white' : 'border-slate-300 bg-white text-transparent'
                          }`}>
                            <Check className="h-3.5 w-3.5" />
                          </span>
                          <span className="min-w-0 flex-1">
                            <span className="block text-sm font-medium text-slate-900">{clientCaseLabel(clientCase)}</span>
                            <span className="mt-1 block text-xs text-slate-500">{clientCaseStatusLabel(clientCase.status)}</span>
                          </span>
                        </button>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          )}

          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">Р С™Р С•Р СР СР ВµР Р…РЎвЂљР В°РЎР‚Р С‘Р в„– Р С—Р С• Р Т‘Р ВµР В»РЎС“</span>
            <textarea
              value={form.clientCaseReference}
              onChange={handleChange('clientCaseReference')}
              rows={2}
              placeholder="Р СњР В°Р С—РЎР‚Р С‘Р СР ВµРЎР‚: Р Т‘Р ВµР В»Р С• Р С•Р В¶Р С‘Р Т‘Р В°Р ВµРЎвЂљ Р С—Р С•Р Т‘РЎвЂљР Р†Р ВµРЎР‚Р В¶Р Т‘Р ВµР Р…Р С‘РЎРЏ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљР С•Р Р†"
              className="min-h-[96px] resize-y rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </label>
        </div>

        <div className="space-y-3 text-sm">
          <div className="flex items-start justify-between gap-3">
            <div>
              <span className="font-medium text-slate-700">{t('acts') ?? 'Р С’Р С”РЎвЂљРЎвЂ№'}</span>
              <p className="mt-1 text-xs text-slate-500">
                Р вЂ™РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р В°Р С”РЎвЂљ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С– Р С‘Р В· РЎРѓР С—Р С‘РЎРѓР С”Р В° Р С‘Р В»Р С‘ РЎРѓР С•Р В·Р Т‘Р В°Р в„–РЎвЂљР Вµ Р Р…Р С•Р Р†РЎвЂ№Р в„–.
              </p>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                title="Р вЂ™РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р В°Р С”РЎвЂљ"
                aria-label="Р вЂ™РЎвЂ№Р В±РЎР‚Р В°РЎвЂљРЎРЉ Р В°Р С”РЎвЂљ"
                onClick={() => {
                  setActDropdownOpen((prev) => !prev);
                  setCaseDropdownOpen(false);
                }}
                disabled={!form.clientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <List className="h-4 w-4" />
              </button>
              <button
                type="button"
                title="Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р В°Р С”РЎвЂљ"
                aria-label="Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р В°Р С”РЎвЂљ"
                onClick={() => {
                  setDraftAct((prev) => ({
                    ...prev,
                    date: form.date || prev.date,
                    amount: form.amount || prev.amount,
                  }));
                  setCreateActError(null);
                  setCreateActOpen(true);
                }}
                disabled={!form.clientId}
                className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Plus className="h-4 w-4" />
              </button>
            </div>
          </div>

          <div className="min-h-[136px] rounded-xl border border-slate-200 bg-slate-50/70 p-3">
            {!form.clientId ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Р РЋР Р…Р В°РЎвЂЎР В°Р В»Р В° Р Р†РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С–.
              </div>
            ) : selectedActs.length === 0 ? (
              <div className="flex min-h-[108px] items-center justify-center text-center text-xs text-slate-500">
                Р С’Р С”РЎвЂљРЎвЂ№ Р ВµРЎвЂ°Р Вµ Р Р…Р Вµ Р Т‘Р С•Р В±Р В°Р Р†Р В»Р ВµР Р…РЎвЂ№ Р Р† РЎРѓРЎвЂЎР ВµРЎвЂљ.
              </div>
            ) : (
              <div className="space-y-2">
                {selectedActs.map((act) => (
                  <div
                    key={act.id}
                    className="flex items-start justify-between gap-3 rounded-xl border border-emerald-200 bg-white px-3 py-2"
                  >
                    <div className="min-w-0">
                      <div className="truncate text-sm font-medium text-slate-900">{actLabel(act)}</div>
                      <div className="mt-1 text-xs text-slate-500">
                        {formatDate(act.date)} Р вЂ™Р’В· {formatCurrencySmart(act.amount ?? 0).full}
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={() => removeSelectedAct(act.id)}
                      className="rounded-full p-1 text-slate-500 transition hover:bg-slate-100"
                      aria-label={t('clear') ?? 'Р С›РЎвЂЎР С‘РЎРѓРЎвЂљР С‘РЎвЂљРЎРЉ'}
                    >
                      <X className="h-3.5 w-3.5" />
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>

          {actDropdownOpen && (
            <div className="rounded-xl border border-slate-200 bg-white shadow-sm">
              <div className="flex items-center justify-between border-b border-slate-200 px-3 py-2">
                <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Р вЂ™РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р В°Р С”РЎвЂљ</span>
                <button
                  type="button"
                  onClick={() => setActDropdownOpen(false)}
                  className="rounded-full p-1 text-slate-500 hover:bg-slate-100"
                  aria-label={t('clear') ?? 'Р С›РЎвЂЎР С‘РЎРѓРЎвЂљР С‘РЎвЂљРЎРЉ'}
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
              <div className="max-h-56 overflow-auto p-2">
                {actsLoading ? (
                  <div className="flex items-center gap-2 px-2 py-3 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" /> {t('loading') ?? 'Р вЂ”Р В°Р С–РЎР‚РЎС“Р В·Р С”Р В°...'}
                  </div>
                ) : actsError ? (
                  <div className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-600">{actsError}</div>
                ) : actOptions.length === 0 ? (
                  <div className="rounded-lg border border-dashed border-slate-200 px-3 py-4 text-center text-xs text-slate-500">
                    Р Р€ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С– Р С—Р С•Р С”Р В° Р Р…Р ВµРЎвЂљ Р В°Р С”РЎвЂљР С•Р Р†.
                  </div>
                ) : (
                  <div className="space-y-2">
                    {actOptions.map((act) => {
                      const checked = selectedActs.some((item) => item.id === act.id);
                      return (
                        <button
                          key={act.id}
                          type="button"
                          onClick={() => toggleAct(act)}
                          className={`flex w-full items-start gap-3 rounded-xl border px-3 py-2 text-left transition ${
                            checked ? 'border-emerald-200 bg-emerald-50' : 'border-slate-200 bg-white hover:bg-slate-50'
                          }`}
                        >
                          <span className={`mt-0.5 inline-flex h-5 w-5 items-center justify-center rounded-md border ${
                            checked ? 'border-emerald-500 bg-emerald-500 text-white' : 'border-slate-300 bg-white text-transparent'
                          }`}>
                            <Check className="h-3.5 w-3.5" />
                          </span>
                          <span className="min-w-0 flex-1">
                            <span className="block text-sm font-medium text-slate-900">{actLabel(act)}</span>
                            <span className="mt-1 block text-xs text-slate-500">
                              {formatDate(act.date)} Р вЂ™Р’В· {formatCurrencySmart(act.amount ?? 0).full}
                            </span>
                          </span>
                        </button>
                      );
                    })}
                  </div>
                )}
              </div>
            </div>
          )}

          <label className="flex flex-col gap-1 text-sm">
            <span className="font-medium text-slate-700">{t('invoiceManualActLabel') ?? 'Р С™Р С•Р СР СР ВµР Р…РЎвЂљР В°РЎР‚Р С‘Р в„– Р С—Р С• Р В°Р С”РЎвЂљРЎС“'}</span>
            <textarea
              value={form.actReference}
              onChange={handleChange('actReference')}
              rows={2}
              placeholder={t('invoiceActManualPlaceholder') ?? 'Р СњР В°Р С—РЎР‚Р С‘Р СР ВµРЎР‚: Р В°Р С”РЎвЂљ Р В±РЎС“Р Т‘Р ВµРЎвЂљ Р С—Р С•Р Т‘Р С—Р С‘РЎРѓР В°Р Р… Р С—Р С•Р В·Р В¶Р Вµ'}
              className="min-h-[96px] resize-y rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
            />
          </label>
        </div>
      </div>
    </section>
  );
  if (!open) return null;

  const content = (
    <div className={`relative w-full overflow-hidden rounded-2xl bg-white ${isPageMode ? 'max-w-none border border-slate-200 shadow-sm' : 'max-w-5xl shadow-2xl'}`}>
      <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
        <div className="flex items-center gap-3">
          {isPageMode && (
            <button
              type="button"
              onClick={onBack ?? onClose}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
            >
              <ArrowLeft className="h-4 w-4" />
              Р СњР В°Р В·Р В°Р Т‘
            </button>
          )}
          <div>
            <h2 className="text-xl font-semibold text-slate-900">
              {isPageMode && pageViewMode
                ? `Р РЋРЎвЂЎРЎвЂРЎвЂљ РІвЂћвЂ“${form.number || 'РІР‚вЂќ'} Р С•РЎвЂљ ${formatDate(form.date) || 'РІР‚вЂќ'}`
                : mode === 'edit'
                ? t('invoiceEdit') ?? 'Р В Р ВµР Т‘Р В°Р С”РЎвЂљР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ'
                : t('invoiceCreate') ?? 'Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ'}
            </h2>
            <p className="text-sm text-slate-500">
              {isPageMode && pageViewMode
                ? 'Р СџРЎР‚Р С•РЎРѓР СР С•РЎвЂљРЎР‚ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В° РЎРѓ Р Т‘Р ВµР в„–РЎРѓРЎвЂљР Р†Р С‘РЎРЏР СР С‘ Р С‘ Р Т‘Р С•Р С”РЎС“Р СР ВµР Р…РЎвЂљР С•Р С Р Т‘Р В»РЎРЏ Р С—Р ВµРЎвЂЎР В°РЎвЂљР С‘/Р С•РЎвЂљР С—РЎР‚Р В°Р Р†Р С”Р С‘'
                : t('invoiceModalSubtitle') ?? 'Р Р€Р С”Р В°Р В¶Р С‘РЎвЂљР Вµ Р С—Р В°РЎР‚Р В°Р СР ВµРЎвЂљРЎР‚РЎвЂ№ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°'}
            </p>
          </div>
        </div>
        {isPageMode && mode === 'edit' && (
          <div className="flex items-center gap-2">
            <span className={`rounded-full px-3 py-1 text-sm font-semibold ${currentStatusMeta.className}`}>{currentStatusMeta.label}</span>
            {pageViewMode ? (
              <button
                type="button"
                onClick={() => setPageViewMode(false)}
                className="inline-flex items-center rounded-lg border border-slate-200 bg-white p-2 text-slate-700 shadow-sm hover:bg-slate-50"
                aria-label="Р В Р ВµР Т‘Р В°Р С”РЎвЂљР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ"
                title="Р В Р ВµР Т‘Р В°Р С”РЎвЂљР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ"
              >
                <Pencil className="h-4 w-4" />
              </button>
            ) : (
              <>
                <button
                  type="submit"
                  form="invoice-edit-form"
                  disabled={submitting}
                  className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-70"
                >
                  {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
                  {mode === 'edit' ? 'Р РЋР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљРЎРЉ' : t('invoiceCreate') ?? 'Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ'}
                </button>
                <button
                  type="button"
                  onClick={() => setPageViewMode(true)}
                  className="rounded-lg border border-slate-200 px-4 py-2 text-sm text-slate-600 hover:bg-slate-100"
                >
                  {t('cancel') ?? 'Р С›РЎвЂљР СР ВµР Р…Р В°'}
                </button>
              </>
            )}
          </div>
        )}
        {!isPageMode && (
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
            aria-label={t('close') ?? 'Р вЂ”Р В°Р С”РЎР‚РЎвЂ№РЎвЂљРЎРЉ'}
          >
            <X className="h-5 w-5" />
          </button>
        )}
      </div>

      {isPageMode && pageViewMode ? (
        <div className="space-y-4 bg-slate-100 px-4 py-4">
          <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
            <section className="rounded-xl border border-slate-300 bg-white p-3">
              <div className="mb-3" />

              <div className="border border-slate-800 p-3 text-sm text-slate-900">
                <div className="grid grid-cols-2 border border-slate-800">
                  <div className="border-r border-slate-800 p-2">{selectedClient?.name || (t('invoiceClient') ?? 'Р СџР С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С” РЎС“РЎРѓР В»РЎС“Р С–')}</div>
                  <div className="p-2">Р вЂР ВР С™ / Р РЋРЎвЂЎРЎвЂРЎвЂљ</div>
                </div>
                <h3 className="my-4 text-center text-2xl font-bold">Р РЋРЎвЂЎРЎвЂРЎвЂљ РІвЂћвЂ“ {form.number || 'РІР‚вЂќ'} Р С•РЎвЂљ {formatDate(form.date) || 'РІР‚вЂќ'} Р С–.</h3>
                <div className="space-y-1 border-t border-slate-800 pt-3 text-xs sm:text-sm">
                  <p><span className="font-semibold">{t('invoiceClient') ?? 'Р СџР С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С” РЎС“РЎРѓР В»РЎС“Р С–'}:</span> {selectedClient?.name || 'РІР‚вЂќ'}</p>
                  <p><span className="font-semibold">Р вЂќР С•Р С–Р С•Р Р†Р С•РЎР‚:</span> {contractLabel(selectedContract)}</p>
                  <p><span className="font-semibold">Р С›РЎРѓР Р…Р С•Р Р†Р В°Р Р…Р С‘Р Вµ:</span> {form.description || 'РІР‚вЂќ'}</p>
                </div>

                <table className="mt-3 w-full table-fixed border-collapse text-xs sm:text-sm">
                  <colgroup>
                    <col style={{ width: '8%' }} />
                    <col style={{ width: '40%' }} />
                    <col style={{ width: '12%' }} />
                    <col style={{ width: '10%' }} />
                    <col style={{ width: '15%' }} />
                    <col style={{ width: '15%' }} />
                  </colgroup>
                  <thead>
                    <tr>
                      <th className="border border-slate-800 px-2 py-1 text-left">РІвЂћвЂ“</th>
                      <th className="border border-slate-800 px-2 py-1 text-left">Р СћР С•Р Р†Р В°РЎР‚РЎвЂ№ (РЎР‚Р В°Р В±Р С•РЎвЂљРЎвЂ№, РЎС“РЎРѓР В»РЎС“Р С–Р С‘)</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Р С™Р С•Р В»-Р Р†Р С•</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Р вЂўР Т‘.</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Р В¦Р ВµР Р…Р В°</th>
                      <th className="border border-slate-800 px-2 py-1 text-right">Р РЋРЎС“Р СР СР В°</th>
                    </tr>
                  </thead>
                  <tbody>
                    {lineItems.map((item, index) => {
                      const qty = parseMoneyInput(item.quantity);
                      const price = parseMoneyInput(item.price);
                      const rowTotal = qty * price;
                      return (
                        <tr key={item.id}>
                          <td className="border border-slate-800 px-2 py-1">{index + 1}</td>
                          <td className="border border-slate-800 px-2 py-1">{item.name || 'РІР‚вЂќ'}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{qty.toFixed(2)}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{item.unit || 'РЎв‚¬РЎвЂљ'}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{formatMoneyDisplayValue(price)}</td>
                          <td className="border border-slate-800 px-2 py-1 text-right">{formatMoneyDisplayValue(rowTotal)}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>

                <div className="mt-3 text-right text-sm">
                  <p><span className="font-semibold">Р ВРЎвЂљР С•Р С–Р С•:</span> {formatMoneyDisplayValue(lineItemsTotal)}</p>
                  <p><span className="font-semibold">Р вЂ™РЎРѓР ВµР С–Р С• Р С” Р С•Р С—Р В»Р В°РЎвЂљР Вµ:</span> {formatMoneyDisplayValue(amountValue)}</p>
                </div>
              </div>
            </section>

            <aside className="space-y-3">
              <div className="rounded-lg border border-slate-300 bg-white p-3">
                <div className="flex items-center justify-between">
                  <span className="text-xl font-semibold">{printableAmount}</span>
                  <span className={`rounded px-3 py-1 text-sm font-semibold ${currentStatusMeta.className}`}>{currentStatusMeta.label}</span>
                </div>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm">
                <div className="mb-2 flex items-center gap-2 font-medium"><Link2 className="h-4 w-4" />Р РЋРЎРѓРЎвЂ№Р В»Р С”Р В° Р Р…Р В° РЎРѓРЎвЂЎР ВµРЎвЂљ</div>
                <a href={invoiceLink} className="break-all text-blue-600 underline">{invoiceLink || 'РІР‚вЂќ'}</a>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm leading-6">
                <p>
                  <span className="text-slate-500">{t('invoiceClient') ?? 'Р СџР С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С” РЎС“РЎРѓР В»РЎС“Р С–'}:</span>{' '}
                  {selectedClient && onOpenClient ? (
                    <button
                      type="button"
                      onClick={() => handleOpenClientCard(selectedClient.id)}
                      className="text-blue-600 underline hover:text-blue-700"
                    >
                      {selectedClient.name}
                    </button>
                  ) : (
                    selectedClient?.name || 'РІР‚вЂќ'
                  )}
                </p>
                <p><span className="text-slate-500">Р вЂќР С•Р С–Р С•Р Р†Р С•РЎР‚:</span> {contractLabel(selectedContract)}</p>
                <p><span className="text-slate-500">{t('cases') ?? 'Р вЂќР ВµР В»Р В°'}:</span> {selectedClientCasesSummary || 'РІР‚вЂќ'}</p>
                <p><span className="text-slate-500">Р С™Р С•Р СР СР ВµР Р…РЎвЂљР В°РЎР‚Р С‘Р в„– Р С—Р С• Р Т‘Р ВµР В»РЎС“:</span> {form.clientCaseReference || 'РІР‚вЂќ'}</p>
                <p><span className="text-slate-500">{t('acts') ?? 'Р С’Р С”РЎвЂљРЎвЂ№'}:</span> {selectedActsSummary || 'РІР‚вЂќ'}</p>
                <p><span className="text-slate-500">{t('invoiceManualActLabel') ?? 'Р С™Р С•Р СР СР ВµР Р…РЎвЂљР В°РЎР‚Р С‘Р в„– Р С—Р С• Р В°Р С”РЎвЂљРЎС“'}:</span> {form.actReference || 'РІР‚вЂќ'}</p>
                <p><span className="text-slate-500">Р вЂќР В°РЎвЂљР В° Р Р†РЎвЂ№РЎРѓРЎвЂљР В°Р Р†Р В»Р ВµР Р…Р С‘РЎРЏ:</span> {formatDate(form.date) || 'РІР‚вЂќ'}</p>
                <p><span className="text-slate-500">Р РЋРЎР‚Р С•Р С” Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№:</span> {formatDate(form.dueDate) || 'РІР‚вЂќ'}</p>
                <p><span className="text-slate-500">Р вЂќР В°РЎвЂљР В° Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№:</span> {formatDate(form.paidDate) || 'РІР‚вЂќ'}</p>
                <p><span className="text-slate-500">Р СџР С•Р В·Р С‘РЎвЂ Р С‘Р С‘:</span> {lineItemsCount}</p>
              </div>
            </aside>
          </div>

          <div className="grid grid-cols-2 gap-2 rounded-lg border border-slate-300 bg-white p-2 md:grid-cols-4 xl:grid-cols-7">
            <button type="button" onClick={() => void handlePageAction('send')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Send className="h-4 w-4" />Р С›РЎвЂљР С—РЎР‚Р В°Р Р†Р С‘РЎвЂљРЎРЉ</button>
            <button type="button" onClick={() => void handlePageAction('print')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Printer className="h-4 w-4" />Р СџР ВµРЎвЂЎР В°РЎвЂљРЎРЉ</button>
            <button type="button" onClick={() => void handlePageAction('download')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Download className="h-4 w-4" />Р РЋР С”Р В°РЎвЂЎР В°РЎвЂљРЎРЉ</button>
            <button type="button" onClick={() => void handlePageAction('copy')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Copy className="h-4 w-4" />Р С™Р С•Р С—Р С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ</button>
            <button type="button" onClick={() => void handlePageAction('paid')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Check className="h-4 w-4" />Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…</button>
            <button type="button" onClick={() => void handlePageAction('cancel')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Ban className="h-4 w-4" />Р С›РЎвЂљР СР ВµР Р…Р ВµР Р…</button>
            <button type="button" onClick={() => void handlePageAction('delete')} className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm"><Trash2 className="h-4 w-4" />Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ</button>
          </div>
        </div>
      ) : (
      <form id="invoice-edit-form" onSubmit={handleSubmit} className={`${isPageMode ? 'px-6 py-5' : 'max-h-[75vh] overflow-y-auto px-6 py-5'} space-y-6`}>
          <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_420px] xl:items-start">
            <div className="space-y-6">
              <section className="rounded-2xl border border-slate-200 bg-slate-50/50 p-4 sm:p-5">
                          <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                              {t('invoiceMainDetailsTitle') ?? 'Р С›РЎРѓР Р…Р С•Р Р†Р Р…РЎвЂ№Р Вµ Р С—Р В°РЎР‚Р В°Р СР ВµРЎвЂљРЎР‚РЎвЂ№'}
                            </span>
              
                          </header>

                          <div className="mt-4 space-y-4">
                            <div className="flex flex-col gap-2">
                              <div className="flex flex-wrap items-center justify-between gap-3">
                                <span className="font-medium text-slate-700">{t('invoiceType') ?? 'Р СћР С‘Р С— РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°'}</span>
                                {lockType && mode === 'edit' && (
                                  <span className="text-xs text-slate-500">
                                    {t('invoiceTypeEditHint') ?? 'Р СљР С•Р В¶Р Р…Р С• Р С‘Р В·Р СР ВµР Р…Р С‘РЎвЂљРЎРЉ РЎвЂљР С‘Р С—, Р ВµРЎРѓР В»Р С‘ РЎРѓРЎвЂЎРЎвЂРЎвЂљ Р С—Р С•Р С—Р В°Р В» Р Р…Р Вµ Р Р† РЎвЂљРЎС“ Р С–РЎР‚РЎС“Р С—Р С—РЎС“.'}
                                  </span>
                                )}
                              </div>
                              <div>
                                {isTypeLocked ? (
                                  <span
                                    className={`inline-flex items-center gap-2 self-start rounded-full px-4 py-1 text-sm font-semibold ${
                                      typeBadgeClasses[form.type]
                                    }`}
                                  >
                                    {form.type === 'Income'
                                      ? t('invoiceTypeIncome') ?? 'Р вЂќР С•РЎвЂ¦Р С•Р Т‘Р Р…РЎвЂ№Р Вµ РЎРѓРЎвЂЎР ВµРЎвЂљР В°'
                                      : t('invoiceTypeExpense') ?? 'Р В Р В°РЎРѓРЎвЂ¦Р С•Р Т‘Р Р…РЎвЂ№Р Вµ РЎРѓРЎвЂЎР ВµРЎвЂљР В°'}
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
                                            ? t('invoiceTypeIncomeShort') ?? t('invoiceTypeIncome') ?? 'Р вЂќР С•РЎвЂ¦Р С•Р Т‘Р Р…РЎвЂ№Р Вµ'
                                            : t('invoiceTypeExpenseShort') ?? t('invoiceTypeExpense') ?? 'Р В Р В°РЎРѓРЎвЂ¦Р С•Р Т‘Р Р…РЎвЂ№Р Вµ'}
                                        </button>
                                      );
                                    })}
                                  </div>
                                )}
                              </div>
                            </div>

                            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                              <label className="flex flex-col gap-1 text-sm">
                                <span className="flex min-h-7 items-center font-medium text-slate-700">{t('invoiceNumber') ?? 'Р СњР С•Р СР ВµРЎР‚ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°'}</span>
                                <input
                                  type="text"
                                  value={form.number}
                                  onChange={handleChange('number')}
                                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                  required
                                />
                              </label>
                              <label className="flex flex-col gap-1 text-sm">
                                <span className="flex min-h-7 items-center justify-between gap-2 font-medium text-slate-700">
                                  {t('invoiceAmount') ?? 'Р РЋРЎС“Р СР СР В°'}
                                  {isIncome && (
                                    <button
                                      type="button"
                                      onClick={handleOpenInstallmentCalculator}
                                      className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-700 transition-colors hover:bg-slate-200"
                                    >
                                      {installmentPlan ? 'Р ВР В·Р СР ВµР Р…Р С‘РЎвЂљРЎРЉ РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”РЎС“' : (t('invoiceInstallmentButton') ?? 'Р В Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р В°')}
                                    </button>
                                  )}
                                </span>
                                <input
                                  type="text"
                                  inputMode="decimal"
                                  value={formatMoneyDisplayValue(form.amount)}
                                  readOnly
                                  className="rounded-lg border border-slate-300 bg-slate-50 px-3 py-2 text-right text-sm tabular-nums text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                  placeholder="0.00"
                                />
                                {isIncome && installmentPlan ? (
                                  <span className={`text-xs ${installmentPlanAmountMismatch ? 'text-amber-600' : 'text-slate-500'}`}>
                                    {installmentPlanAmountMismatch
                                      ? 'Р РЋРЎС“Р СР СР В° Р С‘Р В·Р СР ВµР Р…Р С‘Р В»Р В°РЎРѓРЎРЉ, Р С—Р ВµРЎР‚Р ВµРЎРѓРЎвЂЎР С‘РЎвЂљР В°Р в„–РЎвЂљР Вµ РЎР‚Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”РЎС“.'
                                      : `Р СњР В°РЎРѓРЎвЂљРЎР‚Р С•Р ВµР Р…Р С• Р С—Р В»Р В°РЎвЂљР ВµР В¶Р ВµР в„–: ${installmentPlan.items.length}, Р С” Р С•Р С—Р В»Р В°РЎвЂљР Вµ ${formatCurrencySmart(installmentPlan.toPay).short}.`}
                                  </span>
                                ) : null}
                              </label>
                            </div>

                            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                              <label className="flex flex-col gap-1 text-sm">
                                <span className="flex min-h-7 items-center font-medium text-slate-700">{t('invoiceStatus') ?? 'Р РЋРЎвЂљР В°РЎвЂљРЎС“РЎРѓ'}</span>
                                <select
                                  value={form.status}
                                  onChange={handleChange('status')}
                                  className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                >
                                  <option value="Pending">{t('invoicePendingBadge') ?? t('pending') ?? 'Р С›Р В¶Р С‘Р Т‘Р В°Р ВµРЎвЂљРЎРѓРЎРЏ'}</option>
                                  <option value="Completed">{t('invoicePaidBadge') ?? t('completedStatus') ?? 'Р С›Р С—Р В»Р В°РЎвЂЎР ВµР Р…'}</option>
                                  <option value="Overdue">{t('invoiceOverdueBadge') ?? t('overdue') ?? 'Р СџРЎР‚Р С•РЎРѓРЎР‚Р С•РЎвЂЎР ВµР Р…'}</option>
                                  <option value="Processing">{t('processingStatus') ?? 'Р вЂ™ Р С•Р В±РЎР‚Р В°Р В±Р С•РЎвЂљР С”Р Вµ'}</option>
                                  <option value="Cancelled">{t('cancelledStatus') ?? 'Р С›РЎвЂљР СР ВµР Р…РЎвЂР Р…'}</option>
                                </select>
                              </label>

                              <LookupSelect
                                label={t('invoiceResponsible') ?? 'Р С›РЎвЂљР Р†Р ВµРЎвЂљРЎРѓРЎвЂљР Р†Р ВµР Р…Р Р…РЎвЂ№Р в„–'}
                                value={selectedResponsibleId}
                                onChange={handleResponsibleChange}
                                options={responsibleOptions}
                                placeholder={t('actResponsibleNotSelected') ?? 'Р СњР Вµ РЎС“Р С”Р В°Р В·Р В°Р Р…'}
                                title="Р вЂ™РЎвЂ№Р В±Р С•РЎР‚ Р С•РЎвЂљР Р†Р ВµРЎвЂљРЎРѓРЎвЂљР Р†Р ВµР Р…Р Р…Р С•Р С–Р С•"
                                searchPlaceholder="Р СџР С•Р С‘РЎРѓР С” Р С•РЎвЂљР Р†Р ВµРЎвЂљРЎРѓРЎвЂљР Р†Р ВµР Р…Р Р…Р С•Р С–Р С•"
                                emptyOptionLabel={t('actResponsibleNotSelected') ?? 'Р СњР Вµ РЎС“Р С”Р В°Р В·Р В°Р Р…'}
                                helperText={
                                  selectedClient?.responsibleUserName
                                    ? `Р СџР С• Р С”Р В»Р С‘Р ВµР Р…РЎвЂљРЎС“: ${selectedClient.responsibleUserName}`
                                    : undefined
                                }
                                disabled={lookupsLoading}
                                buttonClassName="bg-white"
                              />
                            </div>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoiceDescription') ?? 'Р С›Р С—Р С‘РЎРѓР В°Р Р…Р С‘Р Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљР В°'}</span>
                              <textarea
                                value={form.description}
                                onChange={handleChange('description')}
                                rows={3}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                placeholder={t('invoiceDescriptionHint') ?? 'Р СњР В°Р С—РЎР‚Р С‘Р СР ВµРЎР‚: РЎРѓРЎвЂЎРЎвЂРЎвЂљ Р Р…Р В° Р С•Р С—Р В»Р В°РЎвЂљРЎС“ Р В°РЎР‚Р ВµР Р…Р Т‘РЎвЂ№ Р С•РЎвЂћР С‘РЎРѓР В° Р В·Р В° Р С‘РЎР‹Р В»РЎРЉ.'}
                              />
                            </label>
                          </div>
                        </section>

              {documentsSection}

              <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
                          <header className="mb-3 flex items-center justify-between">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">Р СџР С•Р В·Р С‘РЎвЂ Р С‘Р С‘ РЎРѓРЎвЂЎР ВµРЎвЂљР В°</span>
                            <button
                              type="button"
                              onClick={addLineItem}
                              className="rounded-lg border border-slate-200 px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50"
                            >
                              + Р вЂќР С•Р В±Р В°Р Р†Р С‘РЎвЂљРЎРЉ
                            </button>
                          </header>
                          <div className="space-y-2">
                            {lineItems.map((item) => (
                              <div key={item.id} className="flex items-center gap-2">
                                <input type="text" list="invoice-line-item-names" value={item.name} onChange={(event) => updateLineItem(item.id, { name: event.target.value })} onBlur={(event) => applyLineItemTemplate(item.id, event.target.value)} placeholder="Р СњР В°Р С‘Р СР ВµР Р…Р С•Р Р†Р В°Р Р…Р С‘Р Вµ" className="min-w-0 flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm" />
                                <input type="text" inputMode="decimal" value={item.quantity} onChange={(event) => updateLineItem(item.id, { quantity: event.target.value })} placeholder="Р С™Р С•Р В»-Р Р†Р С•" className="w-16 rounded-lg border border-slate-300 px-2 py-2 text-center text-sm" />
                                <input type="text" list="invoice-line-item-units" value={item.unit} onChange={(event) => updateLineItem(item.id, { unit: event.target.value })} placeholder="Р вЂўР Т‘." className="w-28 rounded-lg border border-slate-300 px-3 py-2 text-sm" />
                                <input type="text" inputMode="decimal" value={item.price} onChange={(event) => handleLineItemPriceChange(item.id, event.target.value)} onBlur={(event) => handleLineItemPriceBlur(item.id, event.target.value)} placeholder="0.00" className="w-32 rounded-lg border border-slate-300 px-3 py-2 text-right text-sm tabular-nums" />
<button type="button" onClick={() => { void removeLineItem(item.id); }} className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-rose-200 text-rose-500 transition hover:border-rose-300 hover:bg-rose-50 hover:text-rose-700" aria-label="РЈРґР°Р»РёС‚СЊ" title="Удалить позицию"><X className="h-4 w-4" /></button>
                              </div>
                            ))}
                          </div>
                          <datalist id="invoice-line-item-units">
                            {unitOptions.map((unit) => (
                              <option key={unit} value={unit} />
                            ))}
                          </datalist>
                          <datalist id="invoice-line-item-names">
                            {lineItemNameOptions.map((name) => (
                              <option key={name} value={name} />
                            ))}
                          </datalist>
                          <div className="mt-3 text-right text-sm font-semibold text-slate-700">Р ВРЎвЂљР С•Р С–Р С•: {formatMoneyDisplayValue(form.amount)}</div>
                        </section>

              <section className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
                <header className="mb-3 flex items-center justify-between gap-3">
                  <div>
                    <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                      Р СџР В»Р В°РЎвЂљР ВµР В¶Р С‘ Р С—Р С• РЎРѓРЎвЂЎРЎвЂРЎвЂљРЎС“
                    </span>
                    <p className="mt-1 text-sm text-slate-500">
                      Р вЂ”Р Т‘Р ВµРЎРѓРЎРЉ Р С•РЎвЂљР С•Р В±РЎР‚Р В°Р В¶Р В°РЎР‹РЎвЂљРЎРѓРЎРЏ РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С”Р В°Р В»Р ВµР Р…Р Т‘Р В°РЎР‚Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘ Р С‘ Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№ Р С—Р С• РЎвЂљР ВµР С”РЎС“РЎвЂ°Р ВµР СРЎС“ РЎРѓРЎвЂЎРЎвЂРЎвЂљРЎС“.
                    </p>
                  </div>
                  {canShowRelatedPaymentActions && (
                    <button
                      type="button"
                      onClick={onAddRelatedPayment}
                      className="inline-flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-1.5 text-sm font-semibold text-blue-700 transition hover:bg-blue-100"
                    >
                      <Plus className="h-4 w-4" />
                      Р вЂќР С•Р В±Р В°Р Р†Р С‘РЎвЂљРЎРЉ Р С—Р В»Р В°РЎвЂљР ВµР В¶
                    </button>
                  )}
                </header>

                {!canManageRelatedPayments ? (
                  <div className="rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                    Р РЋР Р…Р В°РЎвЂЎР В°Р В»Р В° РЎРѓР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљР Вµ РЎРѓРЎвЂЎРЎвЂРЎвЂљ, Р С—Р С•РЎРѓР В»Р Вµ РЎРЊРЎвЂљР С•Р С–Р С• Р В·Р Т‘Р ВµРЎРѓРЎРЉ Р СР С•Р В¶Р Р…Р С• Р В±РЎС“Р Т‘Р ВµРЎвЂљ РЎРѓР С•Р В·Р Т‘Р В°Р Р†Р В°РЎвЂљРЎРЉ Р С‘ РЎР‚Р ВµР Т‘Р В°Р С”РЎвЂљР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ РЎРѓР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘.
                  </div>
                ) : paymentsLoading ? (
                  <div className="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    {t('loading') ?? 'Р вЂ”Р В°Р С–РЎР‚РЎС“Р В·Р С”Р В°...'}
                  </div>
                ) : displayPayments.length === 0 ? (
                  <div className="rounded-xl border border-dashed border-slate-200 bg-slate-50 px-4 py-6 text-sm text-slate-500">
                    Р РЋР Р†РЎРЏР В·Р В°Р Р…Р Р…РЎвЂ№Р Вµ Р С—Р В»Р В°РЎвЂљР ВµР В¶Р С‘ Р С—Р С•Р С”Р В° Р Р…Р Вµ Р Т‘Р С•Р В±Р В°Р Р†Р В»Р ВµР Р…РЎвЂ№.
                  </div>
                ) : (
                  <div className="space-y-3">
                    {displayPayments.map((payment) => {
                      const installmentMeta = parseInstallmentPaymentMetadata(payment.systemNotes);
                      const installmentSequenceLabel = installmentMeta
                        ? `${installmentMeta.sequence}/${Math.max(installmentMeta.months, installmentMeta.sequence)}`
                        : null;

                      return (
                        <div
                          key={payment.id}
                          className="flex flex-col gap-3 rounded-xl border border-slate-200 px-4 py-3 sm:flex-row sm:items-start sm:justify-between"
                        >
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="text-sm font-semibold text-slate-900">
                              {formatCurrencySmart(payment.amount ?? 0).full}
                            </span>
                            <span
                              className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-semibold ${
                                relatedPaymentStatusMeta[payment.status] ?? relatedPaymentStatusMeta.Pending
                              }`}
                            >
                              {invoicePaymentStatusLabel[payment.status] ?? invoicePaymentStatusLabel.Pending}
                            </span>
                            {installmentMeta ? (
                              <span className="inline-flex items-center rounded-full border border-violet-200 bg-violet-50 px-2.5 py-1 text-xs font-semibold text-violet-700">
                                Р В Р В°РЎРѓРЎРѓРЎР‚Р С•РЎвЂЎР С”Р В° {installmentSequenceLabel}
                              </span>
                            ) : null}
                          </div>
                          <div className="mt-1 text-sm text-slate-600">
                            {payment.description?.trim() || 'Р вЂР ВµР В· Р С•Р С—Р С‘РЎРѓР В°Р Р…Р С‘РЎРЏ'}
                          </div>
                          <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-slate-500">
                            <span>Р СџР В»Р В°Р Р…: {formatDate(payment.plannedDate || payment.date) || 'РІР‚вЂќ'}</span>
                            <span>Р В¤Р В°Р С”РЎвЂљ: {formatDate(payment.paidDate || payment.lastPaymentDate) || 'РІР‚вЂќ'}</span>
                            {payment.account && <span>Р РЋРЎвЂЎРЎвЂРЎвЂљ: {payment.account}</span>}
                          </div>
                        </div>
                        {(onEditRelatedPayment || onDeleteRelatedPayment) && (
                          <div className="flex items-center gap-2 self-end sm:self-start">
                            {onEditRelatedPayment && (
                              <button
                                type="button"
                                onClick={() => onEditRelatedPayment(payment)}
                                className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-slate-200 text-slate-600 transition hover:bg-slate-50"
                                title={t('edit') ?? 'Р В Р ВµР Т‘Р В°Р С”РЎвЂљР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ'}
                                aria-label={t('edit') ?? 'Р В Р ВµР Т‘Р В°Р С”РЎвЂљР С‘РЎР‚Р С•Р Р†Р В°РЎвЂљРЎРЉ'}
                              >
                                <Pencil className="h-4 w-4" />
                              </button>
                            )}
                            {onDeleteRelatedPayment && (
                              <button
                                type="button"
                                onClick={() => {
                                  void onDeleteRelatedPayment(payment);
                                }}
                                className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-slate-200 text-red-600 transition hover:bg-red-50"
                                title={t('delete') ?? 'Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ'}
                                aria-label={t('delete') ?? 'Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ'}
                              >
                                <Trash2 className="h-4 w-4" />
                              </button>
                            )}
                          </div>
                        )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </section>
            </div>

            <div className="space-y-6">
              <section className="rounded-2xl border border-slate-200 p-4 sm:p-5">
                          <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                              {t('invoicePartiesTitle') ?? 'Р СџР С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С” РЎС“РЎРѓР В»РЎС“Р С– Р С‘ Р Т‘Р С•Р С–Р С•Р Р†Р С•РЎР‚'}
                            </span>
              
                          </header>
                          <div className="mt-4 grid grid-cols-1 gap-4">
                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoiceClient') ?? 'Р СџР С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С” РЎС“РЎРѓР В»РЎС“Р С–'}</span>
                              <div className="flex gap-2">
                                <input
                                  type="text"
                                  value={selectedClient?.name ?? ''}
                                  readOnly
                                  className="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm bg-slate-50"
                                  placeholder={t('invoiceClientPlaceholder') ?? 'Р вЂ™РЎвЂ№Р В±Р ВµРЎР‚Р С‘РЎвЂљР Вµ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С–'}
                                />
                                <button
                                  type="button"
                                  onClick={openClientPicker}
                                  className="inline-flex items-center rounded-lg border border-slate-200 px-3 py-2 text-slate-700 hover:bg-slate-50"
                                >
                                  <UserRoundSearch className="h-4 w-4" />
                                </button>
                              </div>
                              {lookupsLoading && (
                                <span className="text-xs text-slate-500">{t('loading') ?? 'Р вЂ”Р В°Р С–РЎР‚РЎС“Р В·Р С”Р В°...'}</span>
                              )}
                              {lookupsError && (
                                <span className="text-xs text-rose-600">{lookupsError}</span>
                              )}
                              {(selectedClient?.clientStatus || selectedClient?.responsibleUserName) && (
                                <div className="mt-2 space-y-1 rounded-xl border border-slate-100 bg-slate-50 p-2">
                                  {selectedClient.clientStatus && (
                                    <ClientStatusBadge status={selectedClient.clientStatus} />
                                  )}
                                  {selectedClient.responsibleUserName && (
                                    <p className="text-xs text-slate-600">
                                      Р С›РЎвЂљР Р†Р ВµРЎвЂљРЎРѓРЎвЂљР Р†Р ВµР Р…Р Р…РЎвЂ№Р в„–: {selectedClient.responsibleUserName}
                                    </p>
                                  )}
                                  {selectedClient.clientStatus.description && (
                                    <p className="text-xs text-slate-500">{selectedClient.clientStatus.description}</p>
                                  )}
                                </div>
                              )}
                              {selectedClient && onOpenClient && (
                                <button
                                  type="button"
                                  onClick={() => handleOpenClientCard(selectedClient.id)}
                                  className="inline-flex w-fit items-center text-xs font-medium text-blue-700 hover:text-blue-800 hover:underline"
                                >
                                  Р С›РЎвЂљР С”РЎР‚РЎвЂ№РЎвЂљРЎРЉ Р С”Р В°РЎР‚РЎвЂљР С•РЎвЂЎР С”РЎС“ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С–
                                </button>
                              )}
                            </label>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">Р вЂќР С•Р С–Р С•Р Р†Р С•РЎР‚</span>
                              <div className="flex items-stretch gap-2">
                                <select
                                  value={form.contractId}
                                  onChange={handleChange('contractId')}
                                  className="min-w-0 flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                  disabled={!form.clientId || contractsLoading}
                                >
                                  <option value="">Р вЂР ВµР В· Р Т‘Р С•Р С–Р С•Р Р†Р С•РЎР‚Р В°</option>
                                  {contractOptions.map((contract) => (
                                    <option key={contract.id} value={String(contract.id)}>
                                      {contract.number}{contract.title ? ` РІР‚вЂќ ${contract.title}` : ''}
                                    </option>
                                  ))}
                                </select>
                                <button
                                  type="button"
                                  onClick={() => {
                                    setCreateContractError(null);
                                    setCreateContractOpen(true);
                                  }}
                                  disabled={!form.clientId}
                                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
                                  aria-label="Р”РѕР±Р°РІРёС‚СЊ РґРѕРіРѕРІРѕСЂ"
                                  title="Р”РѕР±Р°РІРёС‚СЊ РґРѕРіРѕРІРѕСЂ"
                                >
                                  <Plus className="h-4 w-4" />
                                </button>
                              </div>
                              {contractsLoading && <span className="text-xs text-slate-500">Р вЂ”Р В°Р С–РЎР‚РЎС“Р В·Р С”Р В° Р Т‘Р С•Р С–Р С•Р Р†Р С•РЎР‚Р С•Р Р†...</span>}
                              {contractsError && <span className="text-xs text-rose-600">{contractsError}</span>}
                              {!form.clientId && <span className="text-xs text-slate-500">РЎРЅР°С‡Р°Р»Р° РІС‹Р±РµСЂРёС‚Рµ РєР»РёРµРЅС‚Р°, Р·Р°С‚РµРј РјРѕР¶РЅРѕ Р±СѓРґРµС‚ Р±С‹СЃС‚СЂРѕ СЃРѕР·РґР°С‚СЊ РґРѕРіРѕРІРѕСЂ.</span>}
                            </label>

                          </div>
                        </section>

              <section className="rounded-2xl border border-slate-200 p-4 sm:p-5">
                          <header className="flex flex-col gap-1 border-b border-slate-200 pb-3">
                            <span className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                              {t('invoiceScheduleTitle') ?? 'Р РЋРЎР‚Р С•Р С”Р С‘ Р С‘ Р С•Р С—Р В»Р В°РЎвЂљР В°'}
                            </span>
              
                          </header>
                          <div className="mt-4 grid grid-cols-1 gap-4">
                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoiceDate') ?? 'Р вЂќР В°РЎвЂљР В° Р Р†РЎвЂ№РЎРѓРЎвЂљР В°Р Р†Р В»Р ВµР Р…Р С‘РЎРЏ'}</span>
                              <input
                                type="date"
                                value={form.date}
                                onChange={handleChange('date')}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                                required
                              />
                            </label>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoiceDueDate') ?? 'Р РЋРЎР‚Р С•Р С” Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№'}</span>
                              <input
                                type="date"
                                value={form.dueDate}
                                onChange={handleChange('dueDate')}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                              />
                            </label>

                            <label className="flex flex-col gap-1 text-sm">
                              <span className="font-medium text-slate-700">{t('invoicePaidDate') ?? 'Р вЂќР В°РЎвЂљР В° Р С•Р С—Р В»Р В°РЎвЂљРЎвЂ№'}</span>
                              <input
                                type="date"
                                value={form.paidDate}
                                onChange={handleChange('paidDate')}
                                className="rounded-lg border border-slate-300 px-3 py-2 text-center text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                              />
                            </label>
                          </div>
                          {statusHistorySection}
                        </section>

            </div>
          </div>

          {!(isPageMode && mode === 'edit') && (
            <div className="flex items-center justify-end gap-3 pt-2">
              <button
                type="submit"
                disabled={submitting}
                className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-70"
              >
                {submitting && <Loader2 className="h-4 w-4 animate-spin" />}
                {mode === 'edit' ? 'Р РЋР С•РЎвЂ¦РЎР‚Р В°Р Р…Р С‘РЎвЂљРЎРЉ' : t('invoiceCreate') ?? 'Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ РЎРѓРЎвЂЎРЎвЂРЎвЂљ'}
              </button>
              <button
                type="button"
                onClick={() => {
                  if (isPageMode && mode === 'edit') {
                    setPageViewMode(true);
                    return;
                  }
                  onClose();
                }}
                className="rounded-lg border border-slate-200 px-4 py-2 text-sm text-slate-600 hover:bg-slate-100"
              >
                {t('cancel') ?? 'Р С›РЎвЂљР СР ВµР Р…Р В°'}
              </button>
            </div>
          )}
      </form>
      )}

      {clientPickerOpen && (
        <div className="absolute inset-0 z-30 flex items-center justify-center bg-slate-900/40 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white p-4 shadow-2xl">
            <div className="mb-3 flex items-center justify-between gap-2">
              <h3 className="text-base font-semibold text-slate-900">
                {t('invoicePickClientTitle') ?? 'Р вЂ™РЎвЂ№Р В±Р С•РЎР‚ Р С—Р С•РЎРѓРЎвЂљР В°Р Р†РЎвЂ°Р С‘Р С”Р В° РЎС“РЎРѓР В»РЎС“Р С–'}
              </h3>
              <button type="button" onClick={() => setClientPickerOpen(false)} className="rounded-full p-1 text-slate-500 hover:bg-slate-100">
                <X className="h-4 w-4" />
              </button>
            </div>
            <input
              type="text"
              value={clientPickerSearch}
              onChange={(event) => setClientPickerSearch(event.target.value)}
              placeholder={t('search') ?? 'Р СџР С•Р С‘РЎРѓР С”'}
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

      {installmentModalOpen && (
        <div className="fixed inset-0 z-[2200] flex items-center justify-center bg-slate-900/60 p-4">
          <div className="max-h-[calc(100vh-2rem)] w-full max-w-6xl overflow-y-auto rounded-3xl bg-slate-50 p-4 shadow-2xl sm:p-6">
            <InstallmentPlanner
              mode="invoice"
              initialPlan={installmentPlan}
              initialTotal={parseMoneyInput(form.amount)}
              initialStartDate={form.date}
              initialClientId={form.clientId ? Number(form.clientId) : undefined}
              initialCaseId={selectedClientCaseIds[0]}
              initialClientName={selectedClient?.name ?? undefined}
              initialInvoiceNumber={form.number.trim() || invoice?.number || undefined}
              initialCaseName={selectedClientCasesSummary || undefined}
              onApplyPlan={handleApplyInstallmentPlan}
              onCancel={() => setInstallmentModalOpen(false)}
            />
          </div>
        </div>
      )}

      <CaseModal
        isOpen={createCaseOpen}
        onClose={() => setCreateCaseOpen(false)}
        caseData={createCaseStub}
        onSave={handleCreateClientCase}
      />

      {createActOpen && (
        <div className="absolute inset-0 z-30 flex items-center justify-center bg-slate-900/40 p-4">
          <div className="w-full max-w-2xl rounded-2xl bg-white p-4 shadow-2xl">
            <div className="mb-3 flex items-center justify-between gap-2">
              <h3 className="text-base font-semibold text-slate-900">{t('actCreate') ?? 'Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р В°Р С”РЎвЂљ'}</h3>
              <button type="button" onClick={() => setCreateActOpen(false)} className="rounded-full p-1 text-slate-500 hover:bg-slate-100">
                <X className="h-4 w-4" />
              </button>
            </div>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('actNumber') ?? 'Р СњР С•Р СР ВµРЎР‚ Р В°Р С”РЎвЂљР В°'}</span>
                <input value={draftAct.number} onChange={(e) => setDraftAct((prev) => ({ ...prev, number: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('actDate') ?? 'Р вЂќР В°РЎвЂљР В°'}</span>
                <input type="date" value={draftAct.date} onChange={(e) => setDraftAct((prev) => ({ ...prev, date: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm sm:col-span-2">
                <span className="mb-1 block font-medium text-slate-700">{t('actTitle') ?? 'Р СњР В°Р В·Р Р†Р В°Р Р…Р С‘Р Вµ'}</span>
                <input value={draftAct.title} onChange={(e) => setDraftAct((prev) => ({ ...prev, title: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm">
                <span className="mb-1 block font-medium text-slate-700">{t('invoiceAmount') ?? 'Р РЋРЎС“Р СР СР В°'}</span>
                <input value={draftAct.amount} onChange={(e) => setDraftAct((prev) => ({ ...prev, amount: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm sm:col-span-2">
                <span className="mb-1 block font-medium text-slate-700">{t('actComment') ?? 'Р С™Р С•Р СР СР ВµР Р…РЎвЂљР В°РЎР‚Р С‘Р в„–'}</span>
                <textarea value={draftAct.comment} onChange={(e) => setDraftAct((prev) => ({ ...prev, comment: e.target.value }))} rows={2} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" />
              </label>
            </div>
            {createActError && <div className="mt-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{createActError}</div>}
            <div className="mt-4 flex items-center justify-end gap-2">
              <button type="button" onClick={() => setCreateActOpen(false)} className="rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">{t('cancel') ?? 'Р С›РЎвЂљР СР ВµР Р…Р В°'}</button>
              <button type="button" onClick={() => void handleCreateAct()} disabled={creatingAct} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-60">
                {creatingAct && <Loader2 className="h-4 w-4 animate-spin" />}
                {t('actCreate') ?? 'Р РЋР С•Р В·Р Т‘Р В°РЎвЂљРЎРЉ Р В°Р С”РЎвЂљ'}
              </button>
            </div>
          </div>
        </div>
      )}

      <ContractModal
        open={createContractOpen}
        mode="create"
        contract={null}
        onClose={() => {
          if (createContractSubmitting) return;
          setCreateContractOpen(false);
          setCreateContractError(null);
        }}
        onSubmit={handleCreateContractFromInvoice}
        submitting={createContractSubmitting}
        errorMessage={createContractError}
        defaultClients={defaultContractClients}
        overlayClassName="z-[2300]"
      />
    </div>
  );

  if (isPageMode) {
    return <div className="w-full px-4 py-6 sm:px-6 lg:px-8">{content}</div>;
  }

  return (
    <div className="fixed inset-0 z-[1400] flex items-center justify-center bg-slate-900/60 px-4 py-8">
      {content}
    </div>
  );
}
