import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { ClientPortalAccessPanel } from './ClientPortalAccessPanel';
import { MessengerDocumentDeliveryModal } from './MessengerDocumentDeliveryModal';
import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Archive,
  ArrowLeft,
  Briefcase,
  Check,
  Clock3,
  ClipboardPlus,
  Copy,
  Download,
  ExternalLink,
  Eye,
  FilePlus2,
  FileText,
  Gavel,
  Loader2,
  LockKeyhole,
  Mail,
  MessageSquare,
  Paperclip,
  Pencil,
  Phone,
  RefreshCw,
  RotateCw,
  Save,
  Search,
  Send,
  Trash2,
  UploadCloud,
  UserRound,
  WalletCards,
  X,
} from 'lucide-react';

import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import { useDialog } from '../../hooks/useDialog';
import { ObjectAuditHistoryCard } from '../ui/ObjectAuditHistoryCard';
import type {
  ActResponsible,
  Client,
  ClientCase,
  ClientCaseConversationAttachment,
  ClientCaseConversationEntry,
  ClientCaseCourt,
  ClientCaseCourtComplaint,
  ClientCaseCourtEvent,
  ClientCaseCourtInstance,
  ClientCaseCourtParty,
  ClientCaseCourtSyncLog,
  ClientCaseCourtSyncRun,
  ClientCaseCourtSyncRunRequest,
  ClientCaseCourtSyncSearchResult,
  ClientCaseDocument,
  ClientCaseDocumentTemplate,
  ClientCaseFinanceDetail,
  ClientCaseProceduralDeadline,
  ClientCaseRelatedParty,
  ClientCaseTimelineEntry,
  ClientCaseWorkOrder,
  ClientCaseWorkOrderItem,
  ExternalWorkOrderApprovalStatus,
  DocumentStatusEntity,
  LegalEntitySummary,
  ProceduralDeadlineCalculationResult,
} from '../../types';
import { toRuDate } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import { buildCaseTaskPayload, getCaseStageTaskTemplates, isPretrialCaseStage, type CaseStageTaskTemplate } from '../../utils/caseTaskPayload';
import { buildCourtCaseProcessSummary, type CourtProcessSummaryTone } from '../../utils/courtCaseSummary';
import { formatDateTimeInTimeZone, fromTimeZoneDateTimeInput, toTimeZoneDateKey, toTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';
import type { TaskJournalItem } from '../../utils/taskJournalStore';

interface CaseDetailPageProps {
  caseId: number;
  onBack: () => void;
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onOpenLegalEntity?: (legalEntityId: number, caseId?: number) => void;
  onOpenFinance?: (caseItem: ClientCase) => void;
  onCreateInvoice?: (caseItem: ClientCase) => void;
  onCreatePayment?: (caseItem: ClientCase) => void;
  canEdit?: boolean;
  canDelete?: boolean;
  canViewFinance?: boolean;
  canViewTasks?: boolean;
  canCreateTasks?: boolean;
  canSendMessenger?: boolean;
}

type CaseDetailTab = 'overview' | 'participants' | 'tasks' | 'court' | 'finance' | 'workOrder' | 'documents' | 'conversation' | 'history';
type CaseTaskFilter = 'active' | 'all' | 'overdue' | 'done';
type SummaryTone = 'slate' | 'emerald' | 'amber' | 'rose' | 'cyan';
type ParticipantSourceType = 'client' | 'legalEntity' | 'externalContact';
type DocumentSourceType = 'case' | 'task' | 'courtEvent' | 'workOrder' | 'invoice' | 'stage';

const INACTIVE_PROCEDURAL_DEADLINE_STATUSES = new Set(['closed', 'cancelled', 'archived']);
const UPCOMING_DEADLINE_WARNING_MS = 7 * 24 * 60 * 60 * 1000;

function courtProcessSummaryClasses(tone: CourtProcessSummaryTone) {
  switch (tone) {
    case 'emerald':
      return 'border-emerald-200 bg-emerald-50 text-emerald-950';
    case 'blue':
      return 'border-blue-200 bg-blue-50 text-blue-950';
    case 'amber':
      return 'border-amber-200 bg-amber-50 text-amber-950';
    case 'rose':
      return 'border-rose-200 bg-rose-50 text-rose-950';
    default:
      return 'border-slate-200 bg-slate-50 text-slate-900';
  }
}

function courtMovementStatusClasses(status?: string | null) {
  switch ((status || '').trim().toLowerCase()) {
    case 'scheduled':
      return 'border-blue-200 bg-blue-50 text-blue-800';
    case 'postponed':
      return 'border-amber-200 bg-amber-50 text-amber-800';
    case 'held':
    case 'completed':
      return 'border-emerald-200 bg-emerald-50 text-emerald-800';
    case 'suspended':
      return 'border-violet-200 bg-violet-50 text-violet-800';
    case 'cancelled':
      return 'border-rose-200 bg-rose-50 text-rose-800';
    default:
      return 'border-slate-200 bg-slate-50 text-slate-700';
  }
}

interface CourtInstanceDraft {
  courtId: string;
  courtName: string;
  courtCaseNumber: string;
  judicialUid: string;
  courtUrl: string;
  judgeName: string;
  courtroom: string;
  secretaryName: string;
  notes: string;
}

interface CourtComplaintDraft {
  complaintType: string;
  title: string;
  targetInstance: string;
  filedAt: string;
  status: string;
  clientCaseDocumentId: string;
  notes: string;
}

interface CaseEditDraft {
  title: string;
  description: string;
  status: string;
  caseNumber: string;
  caseType: string;
  stage: string;
  responsibleUserId: string;
  startDate: string;
  dueDate: string;
  feeAmount: string;
  priority: string;
  courtName: string;
  courtCaseNumber: string;
  courtUrl: string;
  nextHearingAt: string;
  claimant: string;
  defendant: string;
  thirdParties: string;
  representatives: string;
  judgeName: string;
  courtroom: string;
  courtSecretaryContacts: string;
  claimFiledAt: string;
  courtDecisionAt: string;
  appealDeadlineAt: string;
  cassationDeadlineAt: string;
  motionDeadlineAt: string;
  objectionDeadlineAt: string;
  documentsSummary: string;
  eventHistory: string;
  strategy: string;
  isHiddenFromExecutor: boolean;
  inactivityRemindersEnabled: boolean;
  inactivityRemindersDisabledReason: string;
}

interface CaseStageDeadlineSuggestion {
  key: string;
  deadlineType: 'appeal' | 'cassation' | 'motion' | 'objection';
  title: string;
  description: string;
}

interface StageTransitionPromptState {
  previousStage: string;
  nextStage: string;
  taskTemplates: CaseStageTaskTemplate[];
  deadlineSuggestions: CaseStageDeadlineSuggestion[];
}

interface ParticipantDirectoryOption {
  key: string;
  sourceType: ParticipantSourceType;
  sourceId: number;
  label: string;
  description?: string;
}

const STATUS_LABELS: Record<string, string> = {
  Open: 'В работе',
  OnHold: 'Приостановлено',
  Closed: 'Закрыто',
  Lost: 'Проиграно',
};

const PRIORITY_LABELS: Record<string, string> = {
  low: 'Низкий',
  normal: 'Обычный',
  high: 'Высокий',
  urgent: 'Критический',
};

const TASK_STATUS_LABELS: Record<string, string> = {
  new: 'Новая',
  inProgress: 'В работе',
  waiting: 'Ожидает',
  onApproval: 'На согласовании',
  approved: 'Согласовано',
  revision: 'На доработке',
  overdueApproval: 'Согласование просрочено',
  done: 'Завершена',
  cancelled: 'Отменена',
};

const COURT_SYNC_STATUS_LABELS: Record<string, string> = {
  'manual-check-recorded': 'Проверка записана',
  'binding-required': 'Нужна привязка суда',
  history: 'История',
};

const COURT_SOURCE_LABELS: Record<string, string> = {
  sudrf: 'ГАС «Правосудие»',
  'court-event': 'Судебное событие',
};

const DETAIL_TABS: Array<{ key: CaseDetailTab; label: string }> = [
  { key: 'overview', label: 'Обзор' },
  { key: 'participants', label: 'Участники' },
  { key: 'tasks', label: 'Задачи' },
  { key: 'court', label: 'Суд' },
  { key: 'finance', label: 'Финансы' },
  { key: 'workOrder', label: 'Заказ-наряд' },
  { key: 'documents', label: 'Документы' },
  { key: 'conversation', label: 'Переписка' },
  { key: 'history', label: 'История' },
];

const FALLBACK_DOCUMENT_TEMPLATE_OPTIONS: ClientCaseDocumentTemplate[] = [
  { key: 'case-summary', label: 'Справка по делу', documentType: 'Карточка дела' },
  { key: 'claim', label: 'Исковое заявление', documentType: 'Иск' },
  { key: 'motion', label: 'Ходатайство', documentType: 'Ходатайство' },
  { key: 'objection', label: 'Возражение', documentType: 'Возражение' },
  { key: 'court-letter', label: 'Письмо в суд', documentType: 'Переписка' },
  { key: 'power-of-attorney', label: 'Доверенность', documentType: 'Доверенность' },
  { key: 'application', label: 'Заявление', documentType: 'Заявление' },
  { key: 'procedural-document', label: 'Процессуальный документ', documentType: 'Процессуальный документ' },
].map((template) => ({ ...template, heading: template.label }));

const FALLBACK_DOCUMENT_TYPE_OPTIONS = [
  'Карточка дела',
  'Иск',
  'Ходатайство',
  'Возражение',
  'Переписка',
  'Доверенность',
  'Заявление',
  'Процессуальный документ',
  'Доказательство',
];

const FALLBACK_DOCUMENT_STATUS_OPTIONS = [
  'Подготовлен',
  'На согласовании',
  'Согласован',
  'Подписан',
  'Отправлен',
  'Получен',
  'Архив',
];

const CASE_STATUS_OPTIONS = [
  { value: 'Open', label: 'В работе' },
  { value: 'OnHold', label: 'Приостановлено' },
  { value: 'Closed', label: 'Закрыто' },
  { value: 'Lost', label: 'Проиграно' },
];

const CASE_PRIORITY_OPTIONS = [
  { value: 'low', label: 'Низкий' },
  { value: 'normal', label: 'Обычный' },
  { value: 'high', label: 'Высокий' },
  { value: 'urgent', label: 'Критический' },
];

const CASE_TASK_FILTERS: Array<{ value: CaseTaskFilter; label: string }> = [
  { value: 'active', label: 'Активные' },
  { value: 'overdue', label: 'Просроченные' },
  { value: 'done', label: 'Закрытые' },
  { value: 'all', label: 'Все' },
];

const COURT_INSTANCE_TABS = [
  { value: 'first', label: '1-я инстанция', hint: 'Городской, районный или мировой суд' },
  { value: 'appeal', label: 'Апелляция', hint: 'Апелляционная инстанция' },
  { value: 'cassation', label: 'Кассация', hint: 'Кассационная инстанция' },
  { value: 'supreme', label: 'Верховный суд', hint: 'Верховный суд РФ' },
];

const COURT_COMPLAINT_TYPES = [
  { value: 'appealComplaint', label: 'Апелляционная жалоба' },
  { value: 'privateComplaint', label: 'Частная жалоба' },
  { value: 'cassationComplaint', label: 'Кассационная жалоба' },
  { value: 'supervisoryComplaint', label: 'Жалоба в Верховный суд' },
  { value: 'other', label: 'Иная жалоба' },
];

const COURT_COMPLAINT_STATUSES = [
  { value: 'draft', label: 'Черновик' },
  { value: 'filed', label: 'Подана' },
  { value: 'accepted', label: 'Принята' },
  { value: 'returned', label: 'Возвращена' },
  { value: 'satisfied', label: 'Удовлетворена' },
  { value: 'rejected', label: 'Отклонена' },
];

const WORK_ORDER_STATUS_OPTIONS = [
  { value: 'draft', label: 'Черновик' },
  { value: 'pending_approval', label: 'На согласовании' },
  { value: 'revision_required', label: 'На доработке' },
  { value: 'approved', label: 'Согласован' },
  { value: 'invoice_created', label: 'Счет выставлен' },
  { value: 'cancelled', label: 'Отменен' },
];

interface CaseDetailViewState {
  activeTab?: CaseDetailTab;
  courtOverview?: boolean;
  taskFilter?: CaseTaskFilter;
  participantSourceType?: ParticipantSourceType;
  participantRole?: string;
  documentTemplateKey?: string;
  documentTypeDraft?: string;
  documentStatusDraft?: string;
}

interface DocumentPreviewState {
  document: ClientCaseDocument;
  url: string;
  contentType: string;
  fileName: string;
  htmlContent?: string;
  textContent?: string;
}

const MAX_INLINE_TEXT_PREVIEW_BYTES = 2 * 1024 * 1024;

interface WorkOrderHeaderDraft {
  title: string;
  status: string;
  currency: string;
  notes: string;
}

interface WorkOrderItemDraft {
  serviceName: string;
  description: string;
  executorUserId: string;
  dueDate: string;
  quantity: string;
  unitPrice: string;
}

function getCaseDetailViewStateKey(caseId: number) {
  return `pp.case-detail.${caseId}.view.v1`;
}

function isCaseDetailTab(value: unknown): value is CaseDetailTab {
  return typeof value === 'string' && DETAIL_TABS.some((tab) => tab.key === value);
}

function isCaseTaskFilter(value: unknown): value is CaseTaskFilter {
  return typeof value === 'string' && CASE_TASK_FILTERS.some((filter) => filter.value === value);
}

function isParticipantSourceType(value: unknown): value is ParticipantSourceType {
  return value === 'client' || value === 'legalEntity' || value === 'externalContact';
}

function readCaseDetailViewState(caseId: number): CaseDetailViewState {
  if (typeof window === 'undefined') return {};

  try {
    const raw = window.sessionStorage.getItem(getCaseDetailViewStateKey(caseId));
    if (!raw) return {};

    const parsed = JSON.parse(raw) as Record<string, unknown>;
    return {
      activeTab: isCaseDetailTab(parsed.activeTab) ? parsed.activeTab : undefined,
      courtOverview: parsed.courtOverview === true,
      taskFilter: isCaseTaskFilter(parsed.taskFilter) ? parsed.taskFilter : undefined,
      participantSourceType: isParticipantSourceType(parsed.participantSourceType) ? parsed.participantSourceType : undefined,
      participantRole: typeof parsed.participantRole === 'string' ? parsed.participantRole : undefined,
      documentTemplateKey: typeof parsed.documentTemplateKey === 'string' ? parsed.documentTemplateKey : undefined,
      documentTypeDraft: typeof parsed.documentTypeDraft === 'string' ? parsed.documentTypeDraft : undefined,
      documentStatusDraft: typeof parsed.documentStatusDraft === 'string' ? parsed.documentStatusDraft : undefined,
    };
  } catch {
    return {};
  }
}

function writeCaseDetailViewState(caseId: number, state: CaseDetailViewState) {
  if (typeof window === 'undefined') return;

  try {
    window.sessionStorage.setItem(getCaseDetailViewStateKey(caseId), JSON.stringify(state));
  } catch {
    /** */
  }
}

function formatMoney(value?: number | null) {
  return typeof value === 'number' ? formatCurrencySmart(value).full : '—';
}

function formatRussianCount(value: number, forms: readonly [string, string, string]): string {
  const absolute = Math.abs(value);
  const lastTwo = absolute % 100;
  const last = absolute % 10;
  const form = lastTwo >= 11 && lastTwo <= 14
    ? forms[2]
    : last === 1
      ? forms[0]
      : last >= 2 && last <= 4
        ? forms[1]
        : forms[2];

  return `${value} ${form}`;
}

function buildCourtSyncOverviewMessage(result: ClientCaseCourtSyncRun) {
  if (result.status !== 'external-sync-completed') {
    return result.message;
  }

  const parts = [
    [result.createdCourtEvents ?? 0, ['событие', 'события', 'событий'] as const],
    [result.createdCourtParties ?? 0, ['участник', 'участника', 'участников'] as const],
    [result.createdCourtActDocuments ?? 0, ['судебный акт', 'судебных акта', 'судебных актов'] as const],
    [result.createdProceduralDeadlines ?? 0, ['процессуальный срок', 'процессуальных срока', 'процессуальных сроков'] as const],
  ]
    .filter(([count]) => Number(count) > 0)
    .map(([count, forms]) => formatRussianCount(Number(count), forms as readonly [string, string, string]));
  const checkedAt = formatRuDateTime(result.checkedAt);

  if (parts.length === 0) {
    return `Судебный источник проверен: новых изменений не найдено${checkedAt ? `. Проверено ${checkedAt}` : ''}.`;
  }

  return `Судебный источник обновлён: ${parts.join(', ')}${checkedAt ? `. Проверено ${checkedAt}` : ''}.`;
}

function formatFileSize(value?: number | null) {
  if (!value || value <= 0) return '0 Б';
  if (value < 1024) return `${value} Б`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} КБ`;
  return `${(value / (1024 * 1024)).toFixed(1)} МБ`;
}

function formatRuDateTime(value?: string | null) {
  if (!value) return '';
  return formatDateTimeInTimeZone(value, activeCaseTimeZoneId) || toRuDate(value);
}

function formatCourtSchedule(value?: string | null, allDay?: boolean) {
  if (!value) return '';
  if (allDay === false) return formatRuDateTime(value);

  const date = toRuDate(value);
  return allDay === true && date ? `${date} · весь день` : date;
}

function formatCourtEventSchedule(event: Pick<ClientCaseCourtEvent, 'scheduledAt' | 'allDay'>) {
  return formatCourtSchedule(event.scheduledAt, event.allDay);
}

function toDateInputValue(value?: string | null) {
  return value ? String(value).slice(0, 10) : '';
}

function parseMoneyInput(value: string, fallback = 0) {
  const normalized = value.trim().replace(',', '.');
  if (!normalized) return fallback;
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function buildWorkOrderHeaderDraft(workOrder: ClientCaseWorkOrder | null): WorkOrderHeaderDraft {
  return {
    title: workOrder?.title ?? '',
    status: workOrder?.status ?? 'draft',
    currency: workOrder?.currency ?? 'RUB',
    notes: workOrder?.notes ?? '',
  };
}

function buildWorkOrderItemDraft(item?: ClientCaseWorkOrderItem | null): WorkOrderItemDraft {
  return {
    serviceName: item?.serviceName ?? '',
    description: item?.description ?? '',
    executorUserId: item?.executorUserId ? String(item.executorUserId) : '',
    dueDate: toDateInputValue(item?.dueDate),
    quantity: item ? String(item.quantity) : '1',
    unitPrice: item ? String(item.unitPrice) : '0',
  };
}

function buildWorkOrderItemPayload(draft: WorkOrderItemDraft) {
  return {
    serviceName: draft.serviceName.trim(),
    description: draft.description.trim() || undefined,
    executorUserId: draft.executorUserId ? Number(draft.executorUserId) : null,
    dueDate: draft.dueDate || null,
    quantity: parseMoneyInput(draft.quantity, 1),
    unitPrice: parseMoneyInput(draft.unitPrice, 0),
  };
}

const PAYMENT_STATUS_LABELS: Record<string, string> = {
  Pending: 'Ожидает',
  Completed: 'Оплачен',
  Overdue: 'Просрочен',
  Processing: 'В обработке',
  Cancelled: 'Отменен',
};

const PAYMENT_TYPE_LABELS: Record<string, string> = {
  Income: 'Доход',
  Expense: 'Расход',
};

const DOCUMENT_KIND_LABELS: Record<string, string> = {
  Invoice: 'Счет',
  Accrual: 'Начисление',
  Payment: 'Оплата',
};

function formatFinanceStatus(value?: string | null) {
  if (!value) return '—';
  return PAYMENT_STATUS_LABELS[value] ?? value;
}

function formatFinanceType(value?: string | null) {
  if (!value) return '—';
  return PAYMENT_TYPE_LABELS[value] ?? value;
}

function formatDocumentKind(value?: string | null) {
  if (!value) return 'Документ';
  return DOCUMENT_KIND_LABELS[value] ?? value;
}

type CaseFinancePayment = ClientCaseFinanceDetail['payments'][number];
type PaymentCalendarState = 'paid' | 'planned' | 'overdue' | 'cancelled';

const PAYMENT_CALENDAR_LABELS: Record<PaymentCalendarState, string> = {
  paid: 'Оплачено',
  planned: 'Ожидает',
  overdue: 'Просрочено',
  cancelled: 'Отменено',
};

const PAYMENT_CALENDAR_CLASSES: Record<PaymentCalendarState, string> = {
  paid: 'bg-emerald-50 text-emerald-700 border-emerald-200',
  planned: 'bg-blue-50 text-blue-700 border-blue-200',
  overdue: 'bg-rose-50 text-rose-700 border-rose-200',
  cancelled: 'bg-slate-100 text-slate-600 border-slate-200',
};

function getPaymentCalendarDate(payment: CaseFinancePayment) {
  return payment.plannedDate || payment.date || payment.paidDate || '';
}

function getPaymentCalendarState(payment: CaseFinancePayment): PaymentCalendarState {
  const status = String(payment.status ?? '').toLowerCase();
  if (status.includes('cancel') || status.includes('отмен')) {
    return 'cancelled';
  }

  if (status.includes('complete') || status.includes('paid') || status.includes('оплачен') || payment.outstandingAmount <= 0) {
    return 'paid';
  }

  if (status.includes('overdue') || status.includes('проср')) {
    return 'overdue';
  }

  return 'planned';
}

function getPaymentCalendarMonthKey(payment: CaseFinancePayment) {
  const date = new Date(getPaymentCalendarDate(payment));
  if (Number.isNaN(date.getTime())) {
    return 'unknown';
  }

  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
}

function getPaymentCalendarMonthLabel(key: string) {
  if (key === 'unknown') {
    return 'Без даты';
  }

  const [year, month] = key.split('-').map(Number);
  const date = new Date(year, month - 1, 1);
  return new Intl.DateTimeFormat('ru-RU', { month: 'long', year: 'numeric' }).format(date);
}

function groupPaymentCalendar(payments: CaseFinancePayment[]) {
  const groups = new Map<string, CaseFinancePayment[]>();

  payments.forEach((payment) => {
    const key = getPaymentCalendarMonthKey(payment);
    groups.set(key, [...(groups.get(key) ?? []), payment]);
  });

  return Array.from(groups.entries()).map(([key, items]) => ({
    key,
    label: getPaymentCalendarMonthLabel(key),
    items,
    total: items.reduce((sum, payment) => sum + payment.amount, 0),
    paid: items.reduce((sum, payment) => sum + payment.paidAmount, 0),
    outstanding: items.reduce((sum, payment) => sum + payment.outstandingAmount, 0),
    overdueCount: items.filter((payment) => getPaymentCalendarState(payment) === 'overdue').length,
  }));
}

function getOwnerName(caseItem: ClientCase) {
  return caseItem.client?.name || caseItem.legalEntity?.shortName || caseItem.legalEntity?.fullName || '—';
}

function getResponsibleName(caseItem: ClientCase) {
  return caseItem.responsibleUser?.fullName || caseItem.responsibleUserName || 'Ответственный владельца';
}

function normalizeDirectoryOption(value?: string | null) {
  return (value ?? '').trim();
}

function mergeDirectoryOptions(...sources: Array<Array<string | null | undefined> | undefined>) {
  const seen = new Set<string>();
  const options: string[] = [];

  sources.flatMap((source) => source ?? []).forEach((rawValue) => {
    const value = normalizeDirectoryOption(rawValue);
    if (!value) return;

    const key = value.toLowerCase();
    if (seen.has(key)) return;

    seen.add(key);
    options.push(value);
  });

  return options;
}

function buildDocumentStatusOptions(statuses: DocumentStatusEntity[], documents: ClientCaseDocument[]) {
  const activeStatusNames = statuses
    .filter((status) => status.isActive !== false)
    .sort((a, b) => (a.sortOrder - b.sortOrder) || a.name.localeCompare(b.name, 'ru'))
    .map((status) => status.name);

  return mergeDirectoryOptions(
    activeStatusNames,
    documents.map((document) => document.status),
    FALLBACK_DOCUMENT_STATUS_OPTIONS,
  );
}

function formatCaseDocumentStatus(status: string) {
  const normalized = status.trim().toLowerCase();
  if (normalized === 'archived') return 'В архиве';
  if (normalized === 'received') return 'Получен';
  return status;
}

function buildDocumentTypeOptions(
  directoryTypes: string[],
  templates: ClientCaseDocumentTemplate[],
  documents: ClientCaseDocument[],
) {
  return mergeDirectoryOptions(
    directoryTypes,
    templates.map((template) => template.documentType),
    documents.map((document) => document.documentType),
    FALLBACK_DOCUMENT_TYPE_OPTIONS,
  ).sort((a, b) => a.localeCompare(b, 'ru'));
}

function buildDocumentSourceOptions(
  sourceType: DocumentSourceType,
  tasks: TaskJournalItem[],
  courtEvents: ClientCaseCourtEvent[],
  workOrder: ClientCaseWorkOrder | null,
  financeDetail: ClientCaseFinanceDetail | null,
) {
  if (sourceType === 'task') {
    return tasks
      .filter((task) => task.id)
      .map((task) => ({ value: String(task.id), label: task.title || `Задача #${task.id}` }));
  }

  if (sourceType === 'courtEvent') {
    return courtEvents.map((event) => ({
      value: String(event.id),
      label: `${event.title || `Событие #${event.id}`} · ${formatCourtEventSchedule(event) || 'без даты'}`,
    }));
  }

  if (sourceType === 'workOrder' && workOrder?.id) {
    return [{ value: String(workOrder.id), label: workOrder.title || `Заказ-наряд #${workOrder.id}` }];
  }

  if (sourceType === 'invoice') {
    return (financeDetail?.invoices ?? []).map((invoice) => ({
      value: String(invoice.id),
      label: `Счет №${invoice.number || invoice.id}`,
    }));
  }

  return [];
}

function toDocumentKeySegment(value: string) {
  const normalized = value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9а-яё]+/giu, '-')
    .replace(/^-+|-+$/g, '');
  return normalized || 'document';
}

function buildDocumentSourceKey({
  sourceType,
  sourceId,
  sourceStage,
  fileName,
  workOrderId,
}: {
  sourceType: DocumentSourceType;
  sourceId: string;
  sourceStage: string;
  fileName: string;
  workOrderId: number | null;
}) {
  const fileSegment = toDocumentKeySegment(fileName.replace(/\.[^.]+$/u, ''));
  const numericSourceId = Number(sourceId);

  if (sourceType === 'task' && Number.isInteger(numericSourceId) && numericSourceId > 0) {
    return `task-${numericSourceId}-${fileSegment}`;
  }

  if (sourceType === 'courtEvent' && Number.isInteger(numericSourceId) && numericSourceId > 0) {
    return `court-event-${numericSourceId}`;
  }

  if (sourceType === 'workOrder' && workOrderId) {
    return `work-order-${workOrderId}`;
  }

  if (sourceType === 'invoice' && Number.isInteger(numericSourceId) && numericSourceId > 0) {
    return `invoice-${numericSourceId}`;
  }

  if (sourceType === 'stage') {
    return `stage-${toDocumentKeySegment(sourceStage)}-${fileSegment}`;
  }

  return undefined;
}

function filterCaseDocumentTemplatesByCaseType(
  templates: ClientCaseDocumentTemplate[],
  caseType: string | null | undefined,
) {
  const normalizedCaseType = (caseType ?? '').trim().toLocaleLowerCase('ru');
  return templates.filter((template) => {
    const caseTypes = template.caseTypes ?? [];
    if (caseTypes.length === 0) return true;
    if (!normalizedCaseType) return false;
    return caseTypes.some((item) => item.trim().toLocaleLowerCase('ru') === normalizedCaseType);
  });
}

function getOwnerStatus(caseItem: ClientCase) {
  return caseItem.client?.clientStatus?.name
    || caseItem.legalEntity?.clientStatus?.name
    || (caseItem.client?.isActive === false ? 'Неактивен' : 'Активен');
}

function getOwnerPhone(caseItem: ClientCase) {
  return caseItem.client?.phone || caseItem.legalEntity?.phone || '';
}

function getOwnerEmail(caseItem: ClientCase) {
  return caseItem.client?.email || caseItem.legalEntity?.email || '';
}

function getOwnerInn(caseItem: ClientCase) {
  return caseItem.client?.legalEntity?.inn || caseItem.legalEntity?.inn || '';
}

function getOwnerOrganization(caseItem: ClientCase) {
  return caseItem.client?.legalEntity?.shortName
    || caseItem.client?.legalEntity?.fullName
    || caseItem.client?.company
    || caseItem.legalEntity?.shortName
    || caseItem.legalEntity?.fullName
    || '—';
}

function getOwnerAddress(caseItem: ClientCase) {
  return getPreferredAddress(caseItem.client?.addresses)
    || caseItem.client?.address
    || getPreferredAddress(caseItem.client?.legalEntity?.addresses)
    || caseItem.client?.legalEntity?.address
    || getPreferredAddress(caseItem.legalEntity?.addresses)
    || caseItem.legalEntity?.address
    || '';
}

function getPreferredAddress(addresses?: Client['addresses']) {
  return (addresses ?? [])
    .slice()
    .sort((a, b) => Number(b.isPrimary) - Number(a.isPrimary))
    .map((address) => address.unrestrictedValue || address.value)
    .find((value) => value?.trim())?.trim() ?? '';
}

function maskSensitiveValue(value: string, visibleTail = 4) {
  const normalized = value.trim();
  if (!normalized) return '—';
  const tailLength = Math.max(0, visibleTail);
  if (tailLength === 0) return '••••••••';
  if (normalized.length <= tailLength) return '••••';
  return `${'•'.repeat(Math.min(8, normalized.length - tailLength))}${normalized.slice(-tailLength)}`;
}

function isActiveProceduralDeadline(deadline: ClientCaseProceduralDeadline) {
  return !INACTIVE_PROCEDURAL_DEADLINE_STATUSES.has((deadline.status || '').trim().toLowerCase());
}

function getRiskSummary(caseItem: ClientCase, deadlines: ClientCaseProceduralDeadline[]): { value: string; tone: SummaryTone } {
  const activeDeadlines = deadlines.filter(isActiveProceduralDeadline);
  const overdueTasks = caseItem.overdueTaskCount ?? 0;
  const overdueDeadlines = activeDeadlines.filter((deadline) => deadline.isOverdue).length;
  const overdueTotal = overdueTasks + overdueDeadlines;

  if (overdueTotal > 0) {
    const parts = [
      overdueTasks > 0 ? `задач: ${overdueTasks}` : '',
      overdueDeadlines > 0 ? `сроков: ${overdueDeadlines}` : '',
    ].filter(Boolean);
    return { value: `Просрочено ${parts.join(', ')}`, tone: 'rose' };
  }

  const warningDeadlines = activeDeadlines.filter((deadline) => deadline.isWarning).length;
  if (warningDeadlines > 0) {
    return { value: `Скоро сроков: ${warningDeadlines}`, tone: 'amber' };
  }

  if (caseItem.status === 'Closed') return { value: 'Закрыто', tone: 'slate' };
  return { value: 'Без риска', tone: 'emerald' };
}

function getNearestCaseDateSummary(
  caseItem: ClientCase,
  deadlines: ClientCaseProceduralDeadline[],
  now = new Date(),
  timeZoneId?: string | null,
): { value: string; tone: SummaryTone } {
  const nowTime = now.getTime();
  const todayKey = toTimeZoneDateKey(now, timeZoneId) || toDateInputValue(now.toISOString());
  const warningDateKey = toTimeZoneDateKey(new Date(nowTime + UPCOMING_DEADLINE_WARNING_MS), timeZoneId)
    || toDateInputValue(new Date(nowTime + UPCOMING_DEADLINE_WARNING_MS).toISOString());
  const caseDueDateKey = toDateInputValue(caseItem.dueDate);
  const candidates = [
    caseItem.dueDate ? {
      date: caseItem.dueDate,
      label: 'Срок дела',
      overdue: Boolean(caseDueDateKey && caseDueDateKey < todayKey),
      warning: Boolean(caseDueDateKey && caseDueDateKey >= todayKey && caseDueDateKey <= warningDateKey),
      futureOnly: false,
    } : null,
    caseItem.nextHearingAt ? {
      date: caseItem.nextHearingAt,
      label: 'Заседание',
      overdue: false,
      warning: false,
      futureOnly: true,
    } : null,
    ...deadlines
      .filter(isActiveProceduralDeadline)
      .map((deadline) => ({
        date: deadline.dueAt,
        label: deadline.deadlineTypeLabel || deadline.title || 'Процессуальный срок',
        overdue: deadline.isOverdue,
        warning: deadline.isWarning,
        futureOnly: false,
      })),
  ]
    .filter((candidate): candidate is { date: string; label: string; overdue: boolean; warning: boolean; futureOnly: boolean } => Boolean(candidate))
    .map((candidate) => ({ ...candidate, time: getDateTime(candidate.date) }))
    .filter((candidate): candidate is typeof candidate & { time: number } => candidate.time !== null);

  const upcoming = candidates
    .filter((candidate) => !candidate.overdue && (!candidate.futureOnly || candidate.time >= nowTime))
    .sort((left, right) => left.time - right.time);
  const overdue = candidates
    .filter((candidate) => candidate.overdue)
    .sort((left, right) => right.time - left.time);
  const nearest = upcoming[0] ?? overdue[0];

  if (!nearest) return { value: '—', tone: 'slate' };

  const value = `${toRuDate(nearest.date)} · ${nearest.label}`;
  if (nearest.overdue) return { value, tone: 'rose' };
  if (nearest.warning || (nearest.futureOnly && nearest.time - nowTime <= UPCOMING_DEADLINE_WARNING_MS)) {
    return { value, tone: 'amber' };
  }
  return { value, tone: 'slate' };
}

const DAY_MS = 24 * 60 * 60 * 1000;

function getDateTime(value?: string | null) {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isFinite(time) ? time : null;
}

function getResponsibleLastViewedAt(caseItem: ClientCase) {
  return caseItem.lastViewedByResponsibleAt ?? caseItem.lastViewedAt ?? null;
}

function getLatestCaseActivityTime(caseItem: ClientCase) {
  const dates = [
    getDateTime(caseItem.activityConfirmedAt),
    getDateTime(caseItem.lastTaskAt),
    getDateTime(getResponsibleLastViewedAt(caseItem)),
    getDateTime(caseItem.auditUpdatedAt),
    getDateTime(caseItem.createdAt),
  ].filter((value): value is number => value !== null);

  return dates.length > 0 ? Math.max(...dates) : null;
}

function getInactivitySummary(caseItem: ClientCase): { value: string; tone: SummaryTone } {
  if (caseItem.inactivityRemindersEnabled === false) {
    return { value: 'Контроль выключен', tone: 'slate' };
  }

  const latestActivityTime = getLatestCaseActivityTime(caseItem);
  if (latestActivityTime === null) {
    return { value: 'Нет активности', tone: 'rose' };
  }

  const days = Math.max(0, Math.floor((Date.now() - latestActivityTime) / DAY_MS));
  if (days === 0) {
    return { value: 'Сегодня', tone: 'emerald' };
  }

  const tone: SummaryTone = days >= 14 ? 'rose' : days >= 7 ? 'amber' : 'emerald';
  return { value: `${days} дн. без действий`, tone };
}

function getCourtSyncSummary(caseItem: ClientCase): { value: string; tone: SummaryTone } {
  const hasCourtBinding = Boolean(caseItem.courtName || caseItem.courtCaseNumber || caseItem.judicialUid || caseItem.courtUrl);
  if (!hasCourtBinding) {
    return { value: 'Нет привязки', tone: 'rose' };
  }

  const history = (caseItem.eventHistory ?? '').toLowerCase();
  if (history.includes('проверка судебного источника') || history.includes('судебного источника')) {
    return { value: 'Проверен вручную', tone: 'emerald' };
  }

  return { value: 'Нужна проверка', tone: 'amber' };
}

function splitHistory(value?: string | null) {
  return (value ?? '')
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .filter(Boolean);
}

function isActiveTask(task: TaskJournalItem) {
  return task.status !== 'done' && task.status !== 'cancelled';
}

function isOverdueTask(task: TaskJournalItem) {
  const dueAt = Date.parse(task.dueAt);
  return isActiveTask(task) && Number.isFinite(dueAt) && dueAt < Date.now();
}

function toDateInput(value: string | null | undefined) {
  return value ? String(value).slice(0, 10) : '';
}

let activeCaseTimeZoneId = normalizeUserTimeZoneId();

function setActiveCaseTimeZoneId(value?: string | null) {
  activeCaseTimeZoneId = normalizeUserTimeZoneId(value);
}

function toDateTimeInput(value: string | null | undefined) {
  return toTimeZoneDateTimeInput(value, activeCaseTimeZoneId);
}

function fromDateInput(value: string) {
  return value || null;
}

function fromDateTimeInput(value: string) {
  return value ? fromTimeZoneDateTimeInput(value, activeCaseTimeZoneId) : null;
}

function getNextCourtInstance(instance: string) {
  if (instance === 'first') return 'appeal';
  if (instance === 'appeal') return 'cassation';
  return 'supreme';
}

function buildCourtInstanceDraft(instance?: ClientCaseCourtInstance | null): CourtInstanceDraft {
  return {
    courtId: instance?.courtId ? String(instance.courtId) : '',
    courtName: instance?.courtName ?? '',
    courtCaseNumber: instance?.courtCaseNumber ?? '',
    judicialUid: instance?.judicialUid ?? '',
    courtUrl: instance?.courtUrl ?? '',
    judgeName: instance?.judgeName ?? '',
    courtroom: instance?.courtroom ?? '',
    secretaryName: instance?.secretaryName ?? '',
    notes: instance?.notes ?? '',
  };
}

function buildCourtComplaintDraft(instance = 'first', complaint?: ClientCaseCourtComplaint | null): CourtComplaintDraft {
  return {
    complaintType: complaint?.complaintType ?? (instance === 'cassation' ? 'supervisoryComplaint' : instance === 'appeal' ? 'cassationComplaint' : 'appealComplaint'),
    title: complaint?.title ?? '',
    targetInstance: complaint?.targetInstance ?? getNextCourtInstance(instance),
    filedAt: toDateInput(complaint?.filedAt),
    status: complaint?.status ?? 'draft',
    clientCaseDocumentId: complaint?.clientCaseDocumentId ? String(complaint.clientCaseDocumentId) : '',
    notes: complaint?.notes ?? '',
  };
}

function buildCaseEditDraft(caseItem: ClientCase): CaseEditDraft {
  return {
    title: caseItem.title ?? '',
    description: caseItem.description ?? '',
    status: caseItem.status ?? 'Open',
    caseNumber: caseItem.caseNumber ?? '',
    caseType: caseItem.caseType ?? '',
    stage: caseItem.stage ?? '',
    responsibleUserId: caseItem.responsibleUserId ? String(caseItem.responsibleUserId) : '',
    startDate: toDateInput(caseItem.startDate),
    dueDate: toDateInput(caseItem.dueDate),
    feeAmount: typeof caseItem.feeAmount === 'number' ? String(caseItem.feeAmount) : '',
    priority: caseItem.priority ?? 'normal',
    courtName: caseItem.courtName ?? '',
    courtCaseNumber: caseItem.courtCaseNumber ?? '',
    courtUrl: caseItem.courtUrl ?? '',
    nextHearingAt: toDateTimeInput(caseItem.nextHearingAt),
    claimant: caseItem.claimant ?? '',
    defendant: caseItem.defendant ?? '',
    thirdParties: caseItem.thirdParties ?? '',
    representatives: caseItem.representatives ?? '',
    judgeName: caseItem.judgeName ?? '',
    courtroom: caseItem.courtroom ?? '',
    courtSecretaryContacts: caseItem.courtSecretaryContacts ?? '',
    claimFiledAt: toDateInput(caseItem.claimFiledAt),
    courtDecisionAt: toDateInput(caseItem.courtDecisionAt),
    appealDeadlineAt: toDateInput(caseItem.appealDeadlineAt),
    cassationDeadlineAt: toDateInput(caseItem.cassationDeadlineAt),
    motionDeadlineAt: toDateInput(caseItem.motionDeadlineAt),
    objectionDeadlineAt: toDateInput(caseItem.objectionDeadlineAt),
    documentsSummary: caseItem.documentsSummary ?? '',
    eventHistory: caseItem.eventHistory ?? '',
    strategy: caseItem.strategy ?? '',
    isHiddenFromExecutor: Boolean(caseItem.isHiddenFromExecutor),
    inactivityRemindersEnabled: caseItem.inactivityRemindersEnabled !== false,
    inactivityRemindersDisabledReason: caseItem.inactivityRemindersDisabledReason ?? '',
  };
}

function buildCasePatchFromEditDraft(draft: CaseEditDraft): Partial<ClientCase> {
  return {
    title: draft.title.trim(),
    description: draft.description.trim(),
    status: draft.status,
    caseNumber: draft.caseNumber.trim(),
    caseType: draft.caseType.trim(),
    stage: draft.stage.trim(),
    responsibleUserId: draft.responsibleUserId ? Number(draft.responsibleUserId) : null,
    startDate: fromDateInput(draft.startDate),
    dueDate: fromDateInput(draft.dueDate),
    feeAmount: Number(draft.feeAmount.replace(',', '.')) || 0,
    priority: draft.priority,
    courtName: draft.courtName.trim(),
    courtCaseNumber: draft.courtCaseNumber.trim(),
    courtUrl: draft.courtUrl.trim(),
    nextHearingAt: fromDateTimeInput(draft.nextHearingAt),
    claimant: draft.claimant.trim(),
    defendant: draft.defendant.trim(),
    thirdParties: draft.thirdParties.trim(),
    representatives: draft.representatives.trim(),
    judgeName: draft.judgeName.trim(),
    courtroom: draft.courtroom.trim(),
    courtSecretaryContacts: draft.courtSecretaryContacts.trim(),
    claimFiledAt: fromDateInput(draft.claimFiledAt),
    courtDecisionAt: fromDateInput(draft.courtDecisionAt),
    appealDeadlineAt: fromDateInput(draft.appealDeadlineAt),
    cassationDeadlineAt: fromDateInput(draft.cassationDeadlineAt),
    motionDeadlineAt: fromDateInput(draft.motionDeadlineAt),
    objectionDeadlineAt: fromDateInput(draft.objectionDeadlineAt),
    documentsSummary: draft.documentsSummary.trim(),
    eventHistory: draft.eventHistory.trim(),
    strategy: draft.strategy.trim(),
    isHiddenFromExecutor: draft.isHiddenFromExecutor,
    inactivityRemindersEnabled: draft.inactivityRemindersEnabled,
    inactivityRemindersDisabledReason: draft.inactivityRemindersEnabled
      ? ''
      : draft.inactivityRemindersDisabledReason.trim(),
  };
}

function buildCaseUpdatePayload(base: ClientCase, patch: Partial<ClientCase>): Omit<ClientCase, 'id' | 'createdAt' | 'payments'> {
  return {
    clientId: patch.clientId ?? base.clientId ?? null,
    legalEntityId: patch.legalEntityId ?? base.legalEntityId ?? null,
    client: patch.client ?? base.client ?? null,
    legalEntity: patch.legalEntity ?? base.legalEntity ?? null,
    title: patch.title ?? base.title ?? '',
    description: patch.description ?? base.description ?? '',
    status: patch.status ?? base.status ?? 'Open',
    caseNumber: patch.caseNumber ?? base.caseNumber ?? '',
    caseType: patch.caseType ?? base.caseType ?? '',
    stage: patch.stage ?? base.stage ?? '',
    responsibleUserId: patch.responsibleUserId ?? base.responsibleUserId ?? null,
    startDate: patch.startDate ?? base.startDate ?? null,
    dueDate: patch.dueDate ?? base.dueDate ?? null,
    feeAmount: patch.feeAmount ?? base.feeAmount ?? 0,
    priority: patch.priority ?? base.priority ?? 'normal',
    courtName: patch.courtName ?? base.courtName ?? '',
    courtCaseNumber: patch.courtCaseNumber ?? base.courtCaseNumber ?? '',
    judicialUid: patch.judicialUid ?? base.judicialUid ?? '',
    courtUrl: patch.courtUrl ?? base.courtUrl ?? '',
    nextHearingAt: patch.nextHearingAt ?? base.nextHearingAt ?? null,
    claimant: patch.claimant ?? base.claimant ?? '',
    defendant: patch.defendant ?? base.defendant ?? '',
    thirdParties: patch.thirdParties ?? base.thirdParties ?? '',
    representatives: patch.representatives ?? base.representatives ?? '',
    judgeName: patch.judgeName ?? base.judgeName ?? '',
    courtroom: patch.courtroom ?? base.courtroom ?? '',
    courtSecretaryContacts: patch.courtSecretaryContacts ?? base.courtSecretaryContacts ?? '',
    claimFiledAt: patch.claimFiledAt ?? base.claimFiledAt ?? null,
    courtDecisionAt: patch.courtDecisionAt ?? base.courtDecisionAt ?? null,
    appealDeadlineAt: patch.appealDeadlineAt ?? base.appealDeadlineAt ?? null,
    cassationDeadlineAt: patch.cassationDeadlineAt ?? base.cassationDeadlineAt ?? null,
    motionDeadlineAt: patch.motionDeadlineAt ?? base.motionDeadlineAt ?? null,
    objectionDeadlineAt: patch.objectionDeadlineAt ?? base.objectionDeadlineAt ?? null,
    documentsSummary: patch.documentsSummary ?? base.documentsSummary ?? '',
    eventHistory: patch.eventHistory ?? base.eventHistory ?? '',
    strategy: patch.strategy ?? base.strategy ?? '',
    inactivityRemindersEnabled: patch.inactivityRemindersEnabled ?? base.inactivityRemindersEnabled ?? true,
    inactivityRemindersDisabledReason:
      patch.inactivityRemindersDisabledReason ??
      base.inactivityRemindersDisabledReason ??
      '',
    isHiddenFromExecutor: patch.isHiddenFromExecutor ?? base.isHiddenFromExecutor ?? false,
  } as Omit<ClientCase, 'id' | 'createdAt' | 'payments'>;
}

function normalizeCaseStage(value?: string | null) {
  return (value ?? '').trim().toLocaleLowerCase('ru');
}

function hasCaseStageChanged(previousStage?: string | null, nextStage?: string | null) {
  const normalizedNextStage = normalizeCaseStage(nextStage);
  return Boolean(normalizedNextStage) && normalizeCaseStage(previousStage) !== normalizedNextStage;
}

function buildCaseStageDeadlineSuggestions(caseItem: ClientCase): CaseStageDeadlineSuggestion[] {
  if (isPretrialCaseStage(caseItem)) {
    return [];
  }

  const stageText = [
    normalizeCaseStage(caseItem.stage),
    normalizeCaseStage(caseItem.caseType),
  ].filter(Boolean).join(' ');
  const hasAny = (...aliases: string[]) => aliases.some((alias) => stageText.includes(alias));
  const suggestions: CaseStageDeadlineSuggestion[] = [];

  if (hasAny('апелл') && !caseItem.appealDeadlineAt) {
    suggestions.push({
      key: 'appeal-deadline',
      deadlineType: 'appeal',
      title: 'Добавить срок апелляции',
      description: 'Проверьте дату решения суда и зафиксируйте срок подачи апелляционной жалобы.',
    });
  }

  if (hasAny('кассац') && !caseItem.cassationDeadlineAt) {
    suggestions.push({
      key: 'cassation-deadline',
      deadlineType: 'cassation',
      title: 'Добавить срок кассации',
      description: 'Проверьте судебные акты и зафиксируйте срок подачи кассационной жалобы.',
    });
  }

  if (hasAny('судеб', 'перв', 'инстанц', 'заседан', 'подготов', 'иск') && !caseItem.motionDeadlineAt) {
    suggestions.push({
      key: 'motion-deadline',
      deadlineType: 'motion',
      title: 'Добавить срок ходатайств',
      description: 'Проверьте ближайшее заседание и поставьте контрольный срок для ходатайств и процессуальных документов.',
    });
  }

  if (hasAny('судеб', 'перв', 'инстанц', 'заседан') && !caseItem.objectionDeadlineAt) {
    suggestions.push({
      key: 'objection-deadline',
      deadlineType: 'objection',
      title: 'Добавить срок возражений',
      description: 'Проверьте материалы другой стороны и поставьте срок подготовки возражений.',
    });
  }

  return suggestions.slice(0, 2);
}

function buildStageTransitionPrompt(previousStage: string | null | undefined, nextCase: ClientCase): StageTransitionPromptState | null {
  if (!hasCaseStageChanged(previousStage, nextCase.stage)) {
    return null;
  }

  return {
    previousStage: previousStage?.trim() || 'не указан',
    nextStage: nextCase.stage?.trim() || 'не указан',
    taskTemplates: getCaseStageTaskTemplates(nextCase),
    deadlineSuggestions: buildCaseStageDeadlineSuggestions(nextCase),
  };
}

export function CaseDetailPage({
  caseId,
  onBack,
  onOpenClient,
  onOpenLegalEntity,
  onOpenFinance,
  onCreateInvoice,
  onCreatePayment,
  canEdit = true,
  canDelete = true,
  canViewFinance = true,
  canViewTasks = true,
  canCreateTasks = true,
  canSendMessenger = false,
}: CaseDetailPageProps) {
  const { confirm, prompt } = useDialog();
  const initialViewState = readCaseDetailViewState(caseId);
  const canCreateCaseTask = canViewTasks && canCreateTasks;
  const [caseItem, setCaseItem] = useState<ClientCase | null>(null);
  const markedViewedCaseIdRef = useRef<number | null>(null);
  const [caseTimeZoneId, setCaseTimeZoneId] = useState(() => normalizeUserTimeZoneId());
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [tasks, setTasks] = useState<TaskJournalItem[]>([]);
  const [relatedParties, setRelatedParties] = useState<ClientCaseRelatedParty[]>([]);
  const [showArchivedParties, setShowArchivedParties] = useState(false);
  const [documents, setDocuments] = useState<ClientCaseDocument[]>([]);
  const [courtEvents, setCourtEvents] = useState<ClientCaseCourtEvent[]>([]);
  const [courtInstances, setCourtInstances] = useState<ClientCaseCourtInstance[]>([]);
  const [courtDirectory, setCourtDirectory] = useState<ClientCaseCourt[]>([]);
  const [courtParties, setCourtParties] = useState<ClientCaseCourtParty[]>([]);
  const [proceduralDeadlines, setProceduralDeadlines] = useState<ClientCaseProceduralDeadline[]>([]);
  const [courtSyncLogs, setCourtSyncLogs] = useState<ClientCaseCourtSyncLog[]>([]);
  const [courtSyncSearchResults, setCourtSyncSearchResults] = useState<ClientCaseCourtSyncSearchResult[]>([]);
  const [lastCourtSyncResult, setLastCourtSyncResult] = useState<ClientCaseCourtSyncRun | null>(null);
  const [timeline, setTimeline] = useState<ClientCaseTimelineEntry[]>([]);
  const [conversation, setConversation] = useState<ClientCaseConversationEntry[]>([]);
  const [financeDetail, setFinanceDetail] = useState<ClientCaseFinanceDetail | null>(null);
  const [workOrder, setWorkOrder] = useState<ClientCaseWorkOrder | null>(null);
  const [participantClients, setParticipantClients] = useState<Client[]>([]);
  const [participantLegalEntities, setParticipantLegalEntities] = useState<LegalEntitySummary[]>([]);
  const [participantContacts, setParticipantContacts] = useState<ContactDirectoryItemDto[]>([]);
  const [activeTab, setActiveTab] = useState<CaseDetailTab>(
    (canViewFinance || initialViewState.activeTab !== 'finance')
      && (canViewTasks || initialViewState.activeTab !== 'tasks')
      ? initialViewState.activeTab ?? 'overview'
      : 'overview',
  );
  const [courtOverview, setCourtOverview] = useState(initialViewState.courtOverview === true);
  const [loading, setLoading] = useState(true);
  const [tasksLoading, setTasksLoading] = useState(false);
  const [participantsLoading, setParticipantsLoading] = useState(false);
  const [documentsLoading, setDocumentsLoading] = useState(false);
  const [courtEventsLoading, setCourtEventsLoading] = useState(true);
  const [courtEventsError, setCourtEventsError] = useState<string | null>(null);
  const [courtInstancesLoading, setCourtInstancesLoading] = useState(true);
  const [courtInstancesError, setCourtInstancesError] = useState<string | null>(null);
  const [courtDirectoryLoading, setCourtDirectoryLoading] = useState(false);
  const [courtPartiesLoading, setCourtPartiesLoading] = useState(false);
  const [proceduralDeadlinesLoading, setProceduralDeadlinesLoading] = useState(true);
  const [proceduralDeadlinesError, setProceduralDeadlinesError] = useState<string | null>(null);
  const [courtSyncLogsLoading, setCourtSyncLogsLoading] = useState(false);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [conversationLoading, setConversationLoading] = useState(false);
  const [financeLoading, setFinanceLoading] = useState(false);
  const [workOrderLoading, setWorkOrderLoading] = useState(false);
  const [participantDirectoryLoading, setParticipantDirectoryLoading] = useState(false);
  const [participantDirectoryError, setParticipantDirectoryError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);
  const [creatingTask, setCreatingTask] = useState(false);
  const [savingHeader, setSavingHeader] = useState(false);
  const [requestingAccess, setRequestingAccess] = useState(false);
  const [generatingDocument, setGeneratingDocument] = useState(false);
  const [uploadingDocument, setUploadingDocument] = useState(false);
  const [uploadingDocumentVersionId, setUploadingDocumentVersionId] = useState<number | null>(null);
  const [downloadingDocumentId, setDownloadingDocumentId] = useState<number | null>(null);
  const [previewingDocumentId, setPreviewingDocumentId] = useState<number | null>(null);
  const [sendingYandexDocumentId, setSendingYandexDocumentId] = useState<number | null>(null);
  const [documentPreview, setDocumentPreview] = useState<DocumentPreviewState | null>(null);
  const [updatingDocumentId, setUpdatingDocumentId] = useState<number | null>(null);
  const [deletingDocumentId, setDeletingDocumentId] = useState<number | null>(null);
  const [syncingCourt, setSyncingCourt] = useState(false);
  const [searchingCourtSync, setSearchingCourtSync] = useState(false);
  const [bindingCourtSyncSource, setBindingCourtSyncSource] = useState(false);
  const [savingCourtInstance, setSavingCourtInstance] = useState(false);
  const [savingCourtComplaint, setSavingCourtComplaint] = useState(false);
  const [editingCourtComplaint, setEditingCourtComplaint] = useState<ClientCaseCourtComplaint | null>(null);
  const [archivingCourtComplaintId, setArchivingCourtComplaintId] = useState<number | null>(null);
  const [savingCourtEvent, setSavingCourtEvent] = useState(false);
  const [savingCourtParty, setSavingCourtParty] = useState(false);
  const [archivingCourtPartyId, setArchivingCourtPartyId] = useState<number | null>(null);
  const [savingProceduralDeadline, setSavingProceduralDeadline] = useState(false);
  const [calculatingProceduralDeadline, setCalculatingProceduralDeadline] = useState(false);
  const [closingProceduralDeadlineId, setClosingProceduralDeadlineId] = useState<number | null>(null);
  const [addingHistoryEntry, setAddingHistoryEntry] = useState(false);
  const [addingConversationEntry, setAddingConversationEntry] = useState(false);
  const [addingParticipant, setAddingParticipant] = useState(false);
  const [deletingParticipantId, setDeletingParticipantId] = useState<number | null>(null);
  const [restoringParticipantId, setRestoringParticipantId] = useState<number | null>(null);
  const [savingWorkOrder, setSavingWorkOrder] = useState(false);
  const [deletingWorkOrder, setDeletingWorkOrder] = useState(false);
  const [savingWorkOrderItem, setSavingWorkOrderItem] = useState(false);
  const [deletingWorkOrderItemId, setDeletingWorkOrderItemId] = useState<number | null>(null);
  const [creatingWorkOrderInvoice, setCreatingWorkOrderInvoice] = useState(false);
  const [creatingWorkOrderAct, setCreatingWorkOrderAct] = useState(false);
  const [creatingWorkOrderContract, setCreatingWorkOrderContract] = useState(false);
  const [printingWorkOrder, setPrintingWorkOrder] = useState(false);
  const [generatingWorkOrderDocument, setGeneratingWorkOrderDocument] = useState(false);
  const [workOrderApprovalAction, setWorkOrderApprovalAction] = useState<'submit' | 'approve' | 'revision' | null>(null);
  const [workOrderApprovalComment, setWorkOrderApprovalComment] = useState('');
  const [externalApprovalAvailable, setExternalApprovalAvailable] = useState(false);
  const [externalApprovalStatus, setExternalApprovalStatus] = useState<ExternalWorkOrderApprovalStatus | null>(null);
  const [externalApprovalRecipientName, setExternalApprovalRecipientName] = useState('');
  const [externalApprovalRecipientEmail, setExternalApprovalRecipientEmail] = useState('');
  const [externalApprovalRecipientType, setExternalApprovalRecipientType] = useState<'client' | 'representative'>('client');
  const [externalApprovalDeliveryChannel, setExternalApprovalDeliveryChannel] = useState<'email' | 'email_and_max'>('email');
  const [externalApprovalSaving, setExternalApprovalSaving] = useState(false);
  const [externalApprovalDocumentIds, setExternalApprovalDocumentIds] = useState<number[]>([]);
  const [copiedLink, setCopiedLink] = useState(false);
  const [editPageOpen, setEditPageOpen] = useState(false);
  const [caseEditDraft, setCaseEditDraft] = useState<CaseEditDraft | null>(null);
  const [savingCaseEdit, setSavingCaseEdit] = useState(false);
  const [stageTransitionPrompt, setStageTransitionPrompt] = useState<StageTransitionPromptState | null>(null);
  const [statusDraft, setStatusDraft] = useState('Open');
  const [priorityDraft, setPriorityDraft] = useState('normal');
  const [stageDraft, setStageDraft] = useState('');
  const [responsibleDraft, setResponsibleDraft] = useState('');
  const [hiddenFromExecutorDraft, setHiddenFromExecutorDraft] = useState(false);
  const [inactivityRemindersDraft, setInactivityRemindersDraft] = useState(true);
  const [inactivityRemindersDisabledReasonDraft, setInactivityRemindersDisabledReasonDraft] = useState('');
  const [taskFilter, setTaskFilter] = useState<CaseTaskFilter>(initialViewState.taskFilter ?? 'active');
  const [participantSourceType, setParticipantSourceType] = useState<ParticipantSourceType>(initialViewState.participantSourceType ?? 'client');
  const [participantSourceId, setParticipantSourceId] = useState('');
  const [participantSearch, setParticipantSearch] = useState('');
  const [participantRole, setParticipantRole] = useState(initialViewState.participantRole ?? '');
  const [participantNotes, setParticipantNotes] = useState('');
  const [documentTypeDraft, setDocumentTypeDraft] = useState(initialViewState.documentTypeDraft ?? '');
  const [documentStatusDraft, setDocumentStatusDraft] = useState(initialViewState.documentStatusDraft ?? '');
  const [documentFile, setDocumentFile] = useState<File | null>(null);
  const [documentSourceType, setDocumentSourceType] = useState<DocumentSourceType>('case');
  const [documentSourceId, setDocumentSourceId] = useState('');
  const [documentSourceStage, setDocumentSourceStage] = useState('');
  const [documentTemplates, setDocumentTemplates] = useState<ClientCaseDocumentTemplate[]>(FALLBACK_DOCUMENT_TEMPLATE_OPTIONS);
  const [documentTypeDirectoryOptions, setDocumentTypeDirectoryOptions] = useState<string[]>(FALLBACK_DOCUMENT_TYPE_OPTIONS);
  const [documentStatusDirectoryOptions, setDocumentStatusDirectoryOptions] = useState<DocumentStatusEntity[]>([]);
  const [documentTemplateKey, setDocumentTemplateKey] = useState(initialViewState.documentTemplateKey ?? FALLBACK_DOCUMENT_TEMPLATE_OPTIONS[0].key);
  const [historyDraft, setHistoryDraft] = useState('');
  const [conversationDraft, setConversationDraft] = useState('');
  const [conversationFiles, setConversationFiles] = useState<File[]>([]);
  const [deadlineTypeDraft, setDeadlineTypeDraft] = useState('motion');
  const [deadlineTitleDraft, setDeadlineTitleDraft] = useState('');
  const [deadlineDueAtDraft, setDeadlineDueAtDraft] = useState('');
  const [deadlineWarningDaysDraft, setDeadlineWarningDaysDraft] = useState('7');
  const [deadlineDocumentIdDraft, setDeadlineDocumentIdDraft] = useState('');
  const [deadlineCourtEventIdDraft, setDeadlineCourtEventIdDraft] = useState('');
  const [deadlineNotesDraft, setDeadlineNotesDraft] = useState('');
  const [deadlineCalculationPreview, setDeadlineCalculationPreview] = useState<ProceduralDeadlineCalculationResult | null>(null);
  const [courtEventTypeDraft, setCourtEventTypeDraft] = useState('hearing');
  const [courtEventTitleDraft, setCourtEventTitleDraft] = useState('');
  const [courtEventScheduledAtDraft, setCourtEventScheduledAtDraft] = useState('');
  const [courtEventStatusDraft, setCourtEventStatusDraft] = useState('scheduled');
  const [courtEventCourtIdDraft, setCourtEventCourtIdDraft] = useState('');
  const [courtEventResultDraft, setCourtEventResultDraft] = useState('');
  const [courtEventDocumentIdDraft, setCourtEventDocumentIdDraft] = useState('');
  const [activeCourtInstance, setActiveCourtInstance] = useState('first');
  const [courtInstanceDraft, setCourtInstanceDraft] = useState<CourtInstanceDraft>(() => buildCourtInstanceDraft(null));
  const [courtComplaintDraft, setCourtComplaintDraft] = useState<CourtComplaintDraft>(() => buildCourtComplaintDraft('first'));
  const [courtPartyNameDraft, setCourtPartyNameDraft] = useState('');
  const [courtPartySideDraft, setCourtPartySideDraft] = useState('claimant');
  const [courtPartyRoleDraft, setCourtPartyRoleDraft] = useState('');
  const [courtPartyRepresentativeDraft, setCourtPartyRepresentativeDraft] = useState('');
  const [courtPartyNotesDraft, setCourtPartyNotesDraft] = useState('');
  const [workOrderDraft, setWorkOrderDraft] = useState<WorkOrderHeaderDraft>(() => buildWorkOrderHeaderDraft(null));
  const [newWorkOrderItemDraft, setNewWorkOrderItemDraft] = useState<WorkOrderItemDraft>(() => buildWorkOrderItemDraft(null));
  const [workOrderItemDrafts, setWorkOrderItemDrafts] = useState<Record<number, WorkOrderItemDraft>>({});

  useEffect(() => {
    setActiveCaseTimeZoneId(caseTimeZoneId);
  }, [caseTimeZoneId]);

  const documentTypeOptions = useMemo(
    () => buildDocumentTypeOptions(documentTypeDirectoryOptions, filterCaseDocumentTemplatesByCaseType(documentTemplates, caseItem?.caseType), documents),
    [documentTypeDirectoryOptions, documentTemplates, caseItem?.caseType, documents],
  );
  const availableDocumentTemplates = useMemo(
    () => filterCaseDocumentTemplatesByCaseType(documentTemplates, caseItem?.caseType),
    [documentTemplates, caseItem?.caseType],
  );
  const documentStatusOptions = useMemo(
    () => buildDocumentStatusOptions(documentStatusDirectoryOptions, documents),
    [documentStatusDirectoryOptions, documents],
  );
  const documentSourceOptions = useMemo(
    () => buildDocumentSourceOptions(
      documentSourceType,
      canViewTasks ? tasks : [],
      courtEvents,
      workOrder,
      canViewFinance ? financeDetail : null,
    ),
    [canViewFinance, canViewTasks, courtEvents, documentSourceType, financeDetail, tasks, workOrder],
  );
  const documentTypeListId = useMemo(() => `case-document-types-${caseId}`, [caseId]);

  useEffect(() => {
    const savedState = readCaseDetailViewState(caseId);
    setActiveTab(
      (canViewFinance || savedState.activeTab !== 'finance')
        && (canViewTasks || savedState.activeTab !== 'tasks')
        ? savedState.activeTab ?? 'overview'
        : 'overview',
    );
    setTaskFilter(savedState.taskFilter ?? 'active');
    setParticipantSourceType(savedState.participantSourceType ?? 'client');
    setParticipantRole(savedState.participantRole ?? '');
    setDocumentTypeDraft(savedState.documentTypeDraft ?? '');
    setDocumentStatusDraft(savedState.documentStatusDraft ?? '');
    setDocumentTemplateKey(savedState.documentTemplateKey ?? FALLBACK_DOCUMENT_TEMPLATE_OPTIONS[0].key);
    setParticipantSourceId('');
    setParticipantSearch('');
    setDocumentFile(null);
    setDocumentSourceType('case');
    setDocumentSourceId('');
    setDocumentSourceStage('');
    setWorkOrder(null);
    setCourtEvents([]);
    setCourtEventsLoading(true);
    setCourtEventsError(null);
    setCourtDirectory([]);
    setCourtParties([]);
    setProceduralDeadlines([]);
    setWorkOrderDraft(buildWorkOrderHeaderDraft(null));
    setNewWorkOrderItemDraft(buildWorkOrderItemDraft(null));
    setWorkOrderItemDrafts({});
    setCourtInstances([]);
    setCourtInstancesLoading(true);
    setCourtInstancesError(null);
    setActiveCourtInstance('first');
    setCourtInstanceDraft(buildCourtInstanceDraft(null));
    setCourtComplaintDraft(buildCourtComplaintDraft('first'));
    setEditingCourtComplaint(null);
    setDeadlineTypeDraft('motion');
    setDeadlineTitleDraft('');
    setDeadlineDueAtDraft('');
    setDeadlineWarningDaysDraft('7');
    setDeadlineDocumentIdDraft('');
    setDeadlineCourtEventIdDraft('');
    setDeadlineNotesDraft('');
    setCourtEventTypeDraft('hearing');
    setCourtEventTitleDraft('');
    setCourtEventScheduledAtDraft('');
    setCourtEventStatusDraft('scheduled');
    setCourtEventCourtIdDraft('');
    setCourtEventResultDraft('');
    setCourtEventDocumentIdDraft('');
    setCourtPartyNameDraft('');
    setCourtPartySideDraft('claimant');
    setCourtPartyRoleDraft('');
    setCourtPartyRepresentativeDraft('');
    setCourtPartyNotesDraft('');
  }, [canViewFinance, canViewTasks, caseId]);

  const visibleDetailTabs = useMemo(
    () => DETAIL_TABS.filter((tab) =>
      (canViewFinance || tab.key !== 'finance')
      && (canViewTasks || tab.key !== 'tasks'),
    ),
    [canViewFinance, canViewTasks],
  );

  useEffect(() => {
    if ((!canViewFinance && activeTab === 'finance') || (!canViewTasks && activeTab === 'tasks')) {
      setActiveTab('overview');
    }

    if (!canViewFinance && activeTab === 'finance') {
      setFinanceDetail(null);
      setFinanceLoading(false);
    }

    if (!canViewTasks && activeTab === 'tasks') {
      setTasks([]);
      setTasksLoading(false);
    }
  }, [activeTab, canViewFinance, canViewTasks]);

  useEffect(() => {
    if (!canViewTasks && documentSourceType === 'task') {
      setDocumentSourceType('case');
      setDocumentSourceId('');
    }
  }, [canViewTasks, documentSourceType]);

  useEffect(() => {
    setParticipantSourceId('');
    setParticipantSearch('');
  }, [participantSourceType]);

  useEffect(() => {
    writeCaseDetailViewState(caseId, {
      activeTab,
      courtOverview,
      taskFilter,
      participantSourceType,
      participantRole,
      documentTemplateKey,
      documentTypeDraft,
      documentStatusDraft,
    });
  }, [
    activeTab,
    caseId,
    documentStatusDraft,
    documentTemplateKey,
    documentTypeDraft,
    participantRole,
    participantSourceType,
    taskFilter,
    courtOverview,
  ]);

  useEffect(() => {
    const previewUrl = documentPreview?.url;
    return () => {
      if (previewUrl) {
        URL.revokeObjectURL(previewUrl);
      }
    };
  }, [documentPreview?.url]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    Promise.all([
      apiService.getCaseV2(caseId),
      typeof apiService.getUserSetting === 'function'
        ? apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY).catch(() => null)
        : Promise.resolve(null),
    ])
      .then(([loadedCase, timeZoneResponse]) => {
        if (cancelled) return;
        const nextTimeZoneId = normalizeUserTimeZoneId(timeZoneResponse?.value);
        setActiveCaseTimeZoneId(nextTimeZoneId);
        setCaseTimeZoneId(nextTimeZoneId);
        setCaseItem(loadedCase);
      })
      .catch((err) => {
        if (cancelled) return;
        setCaseItem(null);
        setError(err instanceof Error ? err.message : 'Не удалось загрузить карточку дела.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [caseId, reloadToken]);

  useEffect(() => {
    if (caseItem?.id !== caseId || markedViewedCaseIdRef.current === caseId) return;
    markedViewedCaseIdRef.current = caseId;

    let cancelled = false;
    apiService.markCaseActivity(caseId, { confirmed: false })
      .then((activity) => {
        if (cancelled) return;
        setCaseItem((current) => current?.id === caseId
          ? {
            ...current,
            lastViewedAt: activity.lastViewedAt,
            lastViewedByUserId: activity.lastViewedByUserId,
            lastViewedByUserName: activity.lastViewedByUserName,
            lastViewedByResponsibleAt: activity.lastViewedByResponsibleAt,
            lastViewedByResponsibleUserId: activity.lastViewedByResponsibleUserId,
            lastViewedByResponsibleUserName: activity.lastViewedByResponsibleUserName,
            activityConfirmedAt: activity.activityConfirmedAt,
            activityConfirmedByUserId: activity.activityConfirmedByUserId,
            activityConfirmedByUserName: activity.activityConfirmedByUserName,
            inactivityRemindersEnabled: activity.inactivityRemindersEnabled,
          }
          : current);
      })
      .catch(() => {
        // Фиксация просмотра не должна блокировать работу с карточкой.
      });

    return () => {
      cancelled = true;
    };
  }, [caseId, caseItem?.id]);

  useEffect(() => {
    let cancelled = false;
    apiService.getActResponsibles()
      .then((items) => {
        if (!cancelled) setResponsibles(items ?? []);
      })
      .catch(() => {
        if (!cancelled) setResponsibles([]);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    apiService.getCaseDocumentTemplates()
      .then((items) => {
        if (cancelled) return;

        const templates = (items ?? []).filter((item) => item.key && item.label && item.documentType);
        const nextTemplates = templates.length > 0 ? templates : FALLBACK_DOCUMENT_TEMPLATE_OPTIONS;
        setDocumentTemplates(nextTemplates);
        setDocumentTemplateKey((current) => nextTemplates.some((item) => item.key === current) ? current : nextTemplates[0].key);
      })
      .catch(() => {
        if (cancelled) return;

        setDocumentTemplates(FALLBACK_DOCUMENT_TEMPLATE_OPTIONS);
        setDocumentTemplateKey((current) => FALLBACK_DOCUMENT_TEMPLATE_OPTIONS.some((item) => item.key === current)
          ? current
          : FALLBACK_DOCUMENT_TEMPLATE_OPTIONS[0].key);
      })

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    setDocumentTemplateKey((current) => (
      current && availableDocumentTemplates.some((item) => item.key === current)
        ? current
        : availableDocumentTemplates[0]?.key ?? ''
    ));
  }, [availableDocumentTemplates]);

  useEffect(() => {
    let cancelled = false;

    const loadDocumentDictionaries = async () => {
      const [typesResult, statusesResult] = await Promise.allSettled([
        apiService.getCaseDocumentTypes(),
        apiService.getDocumentStatuses(),
      ]);

      if (cancelled) return;

      if (typesResult.status === 'fulfilled') {
        setDocumentTypeDirectoryOptions(
          mergeDirectoryOptions(typesResult.value, FALLBACK_DOCUMENT_TYPE_OPTIONS),
        );
      } else {
        setDocumentTypeDirectoryOptions(FALLBACK_DOCUMENT_TYPE_OPTIONS);
      }

      if (statusesResult.status === 'fulfilled') {
        setDocumentStatusDirectoryOptions(statusesResult.value ?? []);
      } else {
        setDocumentStatusDirectoryOptions([]);
      }
    };

    void loadDocumentDictionaries();

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!canViewTasks) {
      setTasks([]);
      setTasksLoading(false);
      return;
    }

    if (activeTab !== 'tasks' && activeTab !== 'documents') return;

    let cancelled = false;
    setTasksLoading(true);
    apiService.getTaskJournalItems({ clientCaseId: caseId, page: 1, pageSize: 50 })
      .then((result) => {
        if (!cancelled) setTasks(result.items ?? []);
      })
      .catch(() => {
        if (!cancelled) setTasks([]);
      })
      .finally(() => {
        if (!cancelled) setTasksLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, canViewTasks, caseId, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'participants') return;

    let cancelled = false;
    setParticipantsLoading(true);
    apiService.getCaseRelatedParties(caseId, showArchivedParties)
      .then((items) => {
        if (!cancelled) setRelatedParties(items);
      })
      .catch(() => {
        if (!cancelled) setRelatedParties([]);
      })
      .finally(() => {
        if (!cancelled) setParticipantsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, reloadToken, showArchivedParties]);

  useEffect(() => {
    if (activeTab !== 'participants') return;

    let cancelled = false;
    setParticipantDirectoryLoading(true);
    setParticipantDirectoryError(null);

    const loadDirectory = async () => {
      const [clientsResult, legalEntitiesResult, contactsResult] = await Promise.allSettled([
        apiService.getClients(),
        apiService.getLegalEntitiesPaged({ page: 1, pageSize: 200 }),
        apiService.getContacts({ scope: 'all' }),
      ]);

      if (cancelled) return;

      const loadedClients = clientsResult.status === 'fulfilled' ? clientsResult.value : [];
      const loadedLegalEntities = legalEntitiesResult.status === 'fulfilled' ? legalEntitiesResult.value.items ?? [] : [];
      const loadedContacts = contactsResult.status === 'fulfilled' ? contactsResult.value : [];

      setParticipantClients(loadedClients);
      setParticipantLegalEntities(loadedLegalEntities);
      setParticipantContacts(loadedContacts);

      if (clientsResult.status === 'rejected' && legalEntitiesResult.status === 'rejected' && contactsResult.status === 'rejected') {
        setParticipantDirectoryError('Не удалось загрузить справочники участников дела.');
      }
    };

    void loadDirectory()
      .catch(() => {
        if (!cancelled) {
          setParticipantDirectoryError('Не удалось загрузить справочники участников дела.');
          setParticipantClients([]);
          setParticipantLegalEntities([]);
          setParticipantContacts([]);
        }
      })
      .finally(() => {
        if (!cancelled) setParticipantDirectoryLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab]);

  useEffect(() => {
    if (activeTab !== 'documents'
      && activeTab !== 'court'
      && !(activeTab === 'workOrder' && externalApprovalAvailable)) return;

    let cancelled = false;
    setDocumentsLoading(true);
    apiService.getCaseDocuments(caseId)
      .then((items) => {
        if (!cancelled) setDocuments(items);
      })
      .catch(() => {
        if (!cancelled) setDocuments([]);
      })
      .finally(() => {
        if (!cancelled) setDocumentsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, externalApprovalAvailable, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'court' && activeTab !== 'documents') return;

    let cancelled = false;
    setCourtPartiesLoading(true);
    apiService.getCaseCourtParties(caseId)
      .then((items) => {
        if (!cancelled) setCourtParties(items ?? []);
      })
      .catch(() => {
        if (!cancelled) setCourtParties([]);
      })
      .finally(() => {
        if (!cancelled) setCourtPartiesLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'court' && activeTab !== 'documents') return;

    let cancelled = false;
    setCourtDirectoryLoading(true);
    apiService.getCaseCourtDirectory()
      .then((items) => {
        if (!cancelled) setCourtDirectory(items ?? []);
      })
      .catch(() => {
        if (!cancelled) setCourtDirectory([]);
      })
      .finally(() => {
        if (!cancelled) setCourtDirectoryLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'court' && activeTab !== 'documents') return;

    let cancelled = false;
    setCourtInstancesLoading(true);
    setCourtInstancesError(null);
    apiService.getCaseCourtInstances(caseId)
      .then((items) => {
        if (!cancelled) setCourtInstances(items ?? []);
      })
      .catch((err) => {
        if (!cancelled) {
          setCourtInstances([]);
          setCourtInstancesError(extractDisplayErrorMessage(err, 'Не удалось загрузить судебные инстанции.'));
        }
      })
      .finally(() => {
        if (!cancelled) setCourtInstancesLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, reloadToken]);

  useEffect(() => {
    const selected = courtInstances.find((item) => item.instance === activeCourtInstance);
    setCourtInstanceDraft(buildCourtInstanceDraft(selected ?? null));
    setCourtComplaintDraft(buildCourtComplaintDraft(activeCourtInstance));
    setEditingCourtComplaint(null);
    setCourtSyncSearchResults([]);
  }, [activeCourtInstance, courtInstances]);

  useEffect(() => {
    setLastCourtSyncResult(null);
  }, [caseId]);

  useEffect(() => {
    if (activeTab !== 'court' && activeTab !== 'documents') return;

    let cancelled = false;
    setCourtEventsLoading(true);
    setCourtEventsError(null);
    apiService.getCaseCourtEvents(caseId)
      .then((items) => {
        if (!cancelled) setCourtEvents(items ?? []);
      })
      .catch((err) => {
        if (!cancelled) {
          setCourtEvents([]);
          setCourtEventsError(extractDisplayErrorMessage(err, 'Не удалось загрузить движение судебного дела.'));
        }
      })
      .finally(() => {
        if (!cancelled) setCourtEventsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, reloadToken]);

  useEffect(() => {
    let cancelled = false;
    setProceduralDeadlinesLoading(true);
    setProceduralDeadlinesError(null);
    apiService.getCaseProceduralDeadlines(caseId)
      .then((items) => {
        if (!cancelled) setProceduralDeadlines(items ?? []);
      })
      .catch((err) => {
        if (!cancelled) {
          setProceduralDeadlines([]);
          setProceduralDeadlinesError(extractDisplayErrorMessage(err, 'Не удалось проверить процессуальные сроки.'));
        }
      })
      .finally(() => {
        if (!cancelled) setProceduralDeadlinesLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [caseId, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'court') return;

    let cancelled = false;
    setCourtSyncLogsLoading(true);
    apiService.getCaseCourtSyncLogs(caseId)
      .then((items) => {
        if (!cancelled) setCourtSyncLogs(items ?? []);
      })
      .catch(() => {
        if (!cancelled) setCourtSyncLogs([]);
      })
      .finally(() => {
        if (!cancelled) setCourtSyncLogsLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, reloadToken]);

  useEffect(() => {
    if (!canViewFinance) {
      setFinanceDetail(null);
      setFinanceLoading(false);
      return;
    }
    if (activeTab !== 'finance' && activeTab !== 'documents') return;

    let cancelled = false;
    setFinanceLoading(true);
    apiService.getCaseFinance(caseId)
      .then((detail) => {
        if (!cancelled) setFinanceDetail(detail);
      })
      .catch(() => {
        if (!cancelled) setFinanceDetail(null);
      })
      .finally(() => {
        if (!cancelled) setFinanceLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, canViewFinance, caseId, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'workOrder' && activeTab !== 'documents') return;

    let cancelled = false;
    setWorkOrderLoading(true);
    apiService.getCaseWorkOrder(caseId)
      .then((loadedWorkOrder) => {
        if (cancelled) return;
        setWorkOrder(loadedWorkOrder);
        setWorkOrderDraft(buildWorkOrderHeaderDraft(loadedWorkOrder?.id ? loadedWorkOrder : null));
        setWorkOrderItemDrafts(Object.fromEntries(
          (loadedWorkOrder?.items ?? []).map((item) => [item.id, buildWorkOrderItemDraft(item)]),
        ));
      })
      .catch(() => {
        if (cancelled) return;
        setWorkOrder(null);
        setWorkOrderDraft(buildWorkOrderHeaderDraft(null));
        setWorkOrderItemDrafts({});
      })
      .finally(() => {
        if (!cancelled) setWorkOrderLoading(false);
      });

    apiService.getFeatureFlags()
      .then(async (flags) => {
        if (cancelled) return;
        const available = Boolean(flags.legal.externalWorkOrderApproval);
        setExternalApprovalAvailable(available);
        if (!available) {
          setExternalApprovalStatus(null);
          return;
        }
        const status = await apiService.getExternalWorkOrderApproval(caseId);
        if (!cancelled) setExternalApprovalStatus(status ?? null);
      })
      .catch(() => {
        if (!cancelled) {
          setExternalApprovalAvailable(false);
          setExternalApprovalStatus(null);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, reloadToken]);

  useEffect(() => {
    if (!caseItem) return;
    if (!externalApprovalRecipientName) {
      setExternalApprovalRecipientName(caseItem.client?.name || caseItem.legalEntity?.shortName || '');
    }
    if (!externalApprovalRecipientEmail) {
      setExternalApprovalRecipientEmail(caseItem.client?.email || caseItem.legalEntity?.email || '');
    }
  }, [caseItem, externalApprovalRecipientEmail, externalApprovalRecipientName]);

  useEffect(() => {
    const availableIds = new Set(documents.map((document) => document.id));
    setExternalApprovalDocumentIds((current) => current.filter((id) => availableIds.has(id)).slice(0, 10));
  }, [documents]);

  useEffect(() => {
    if (activeTab !== 'history') return;

    let cancelled = false;
    setTimelineLoading(true);
    apiService.getCaseTimeline(caseId)
      .then((items) => {
        if (!cancelled) setTimeline(items ?? []);
      })
      .catch(() => {
        if (!cancelled) {
          setTimeline(splitHistory(caseItem?.eventHistory).map((line, index) => ({
            id: index + 1,
            clientCaseId: caseId,
            occurredAt: null,
            text: line,
          })));
        }
      })
      .finally(() => {
        if (!cancelled) setTimelineLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, caseItem?.eventHistory, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'conversation') return;

    let cancelled = false;
    setConversationLoading(true);
    apiService.getCaseConversation(caseId)
      .then((items) => {
        if (!cancelled) setConversation(items ?? []);
      })
      .catch(() => {
        if (!cancelled) setConversation([]);
      })
      .finally(() => {
        if (!cancelled) setConversationLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, caseId, reloadToken]);

  useEffect(() => {
    if (!caseItem) return;
    setStatusDraft(caseItem.status || 'Open');
    setPriorityDraft(caseItem.priority || 'normal');
    setStageDraft(caseItem.stage || '');
    setResponsibleDraft(caseItem.responsibleUserId ? String(caseItem.responsibleUserId) : '');
    setHiddenFromExecutorDraft(Boolean(caseItem.isHiddenFromExecutor));
    setInactivityRemindersDraft(caseItem.inactivityRemindersEnabled !== false);
    setInactivityRemindersDisabledReasonDraft(caseItem.inactivityRemindersDisabledReason ?? '');
  }, [caseItem]);

  const filteredTasks = useMemo(() => {
    if (taskFilter === 'all') return tasks;
    if (taskFilter === 'done') return tasks.filter((task) => task.status === 'done' || task.status === 'cancelled');
    if (taskFilter === 'overdue') return tasks.filter(isOverdueTask);
    return tasks.filter(isActiveTask);
  }, [taskFilter, tasks]);
  const stageTaskTemplates = useMemo(
    () => caseItem ? getCaseStageTaskTemplates(caseItem) : [],
    [caseItem],
  );

  const participantOptions = useMemo(
    () => buildParticipantDirectoryOptions({
      sourceType: participantSourceType,
      clients: participantClients,
      legalEntities: participantLegalEntities,
      contacts: participantContacts,
      relatedParties,
      search: participantSearch,
    }),
    [participantClients, participantContacts, participantLegalEntities, participantSearch, participantSourceType, relatedParties],
  );

  const rememberStageTransition = (previousStage: string | null | undefined, nextCase: ClientCase) => {
    setStageTransitionPrompt(buildStageTransitionPrompt(previousStage, nextCase));
  };

  const saveHeaderChanges = async () => {
    if (!caseItem) return;

    const previousStage = caseItem.stage;
    const nextStage = stageDraft.trim();
    setSavingHeader(true);
    setError(null);
    try {
      await apiService.bulkUpdateCases({
        ids: [caseItem.id],
        status: statusDraft,
        priority: priorityDraft,
        stage: stageDraft.trim(),
        responsibleUserId: responsibleDraft ? Number(responsibleDraft) : undefined,
        isHiddenFromExecutor: hiddenFromExecutorDraft,
        inactivityRemindersEnabled: inactivityRemindersDraft,
        inactivityRemindersDisabledReason: inactivityRemindersDraft
          ? ''
          : inactivityRemindersDisabledReasonDraft,
      });
      rememberStageTransition(previousStage, { ...caseItem, stage: nextStage });
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить параметры дела.');
    } finally {
      setSavingHeader(false);
    }
  };

  const changeHeaderInactivityReminders = async (enabled: boolean) => {
    if (enabled) {
      setInactivityRemindersDraft(true);
      setInactivityRemindersDisabledReasonDraft('');
      return;
    }

    const reason = await prompt({
      title: 'Выключить автоконтроль дела',
      label: 'Причина выключения',
      defaultValue: inactivityRemindersDisabledReasonDraft,
      multiline: true,
      confirmLabel: 'Выключить',
      cancelLabel: 'Отмена',
    });
    if (reason === null) return;
    if (!reason.trim()) {
      setError('Укажите причину выключения автоконтроля.');
      return;
    }

    setError(null);
    setInactivityRemindersDisabledReasonDraft(reason.trim());
    setInactivityRemindersDraft(false);
  };

  const archiveCase = async () => {
    if (!caseItem) return;

    setSavingHeader(true);
    setError(null);
    try {
      await apiService.bulkUpdateCases({ ids: [caseItem.id], status: 'Closed' });
      setStatusDraft('Closed');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось архивировать дело.');
    } finally {
      setSavingHeader(false);
    }
  };

  const openCaseEditPage = () => {
    if (!caseItem) return;
    setCaseEditDraft(buildCaseEditDraft(caseItem));
    setEditPageOpen(true);
    setError(null);
    setNotice(null);
  };

  const closeCaseEditPage = () => {
    setEditPageOpen(false);
    setCaseEditDraft(null);
  };

  const saveCaseFromPage = async () => {
    if (!caseItem || !caseEditDraft) return;
    if (!caseEditDraft.title.trim()) {
      setError('Введите название дела.');
      return;
    }
    if (!caseEditDraft.inactivityRemindersEnabled &&
        !caseEditDraft.inactivityRemindersDisabledReason.trim()) {
      setError('Укажите причину выключения автоконтроля.');
      return;
    }

    setSavingCaseEdit(true);
    setError(null);
    try {
      const previousStage = caseItem.stage;
      const patch = buildCasePatchFromEditDraft(caseEditDraft);
      const updated = await apiService.updateCaseV2(caseItem.id, buildCaseUpdatePayload(caseItem, patch));
      setCaseItem(updated);
      setCaseEditDraft(buildCaseEditDraft(updated));
      setEditPageOpen(false);
      rememberStageTransition(previousStage, updated);
      setNotice('Карточка дела обновлена.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить карточку дела.');
    } finally {
      setSavingCaseEdit(false);
    }
  };

  const requestCaseAccess = async () => {
    setRequestingAccess(true);
    setError(null);
    setNotice(null);
    try {
      const reason = await prompt({
        title: 'Запрос доступа к делу',
        label: 'Причина запроса',
        defaultValue: 'Нужно посмотреть или продолжить работу по делу.',
        multiline: true,
        confirmLabel: 'Отправить',
        cancelLabel: 'Отмена',
      });
      if (reason === null) return;
      await apiService.requestCaseAccess(caseId, { reason: reason.trim() });
      setNotice('Запрос доступа отправлен. Руководитель увидит его в истории дела.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось отправить запрос доступа.');
    } finally {
      setRequestingAccess(false);
    }
  };

  const generateDocument = async () => {
    if (!caseItem) return;
    if (!canManageDocuments) return;

    setGeneratingDocument(true);
    setError(null);
    try {
      const selectedTemplate = availableDocumentTemplates.find((item) => item.key === documentTemplateKey)
        ?? availableDocumentTemplates[0];
      if (!selectedTemplate) {
        throw new Error('Нет доступных шаблонов документов для типа этого дела.');
      }

      await apiService.generateCaseDocumentTemplate(caseItem.id, {
        templateKey: selectedTemplate.key,
        documentType: selectedTemplate.documentType,
        status: 'Подготовлен',
      });
      setNotice(`Документ по шаблону «${selectedTemplate.label}» сформирован.`);
      setActiveTab('documents');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось сформировать документ по делу.'));
    } finally {
      setGeneratingDocument(false);
    }
  };

  const uploadDocument = async () => {
    if (!caseItem) return;
    if (!canManageDocuments) return;
    if (!documentFile) {
      setError('Выберите файл документа для загрузки.');
      return;
    }

    setUploadingDocument(true);
    setError(null);
    try {
      const documentKey = buildDocumentSourceKey({
        sourceType: documentSourceType,
        sourceId: documentSourceId,
        sourceStage: documentSourceStage || caseItem.stage || '',
        fileName: documentFile.name,
        workOrderId: workOrder?.id ?? null,
      });
      const payload: { documentType?: string; status?: string; documentKey?: string } = {
        documentType: documentTypeDraft.trim() || undefined,
        status: documentStatusDraft.trim() || undefined,
      };
      if (documentKey) {
        payload.documentKey = documentKey;
      }
      await apiService.uploadCaseDocument(caseItem.id, documentFile, payload);
      setNotice(`Документ «${documentFile.name}» загружен.`);
      setDocumentTypeDraft('');
      setDocumentStatusDraft('');
      setDocumentFile(null);
      if (documentSourceType !== 'stage') {
        setDocumentSourceId('');
      }
      setActiveTab('documents');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось загрузить документ по делу.'));
    } finally {
      setUploadingDocument(false);
    }
  };

  const downloadDocument = async (caseDocument: ClientCaseDocument) => {
    if (!caseItem) return;

    setDownloadingDocumentId(caseDocument.id);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.downloadCaseDocument(caseItem.id, caseDocument.id);
      const url = URL.createObjectURL(result.blob);
      const link = window.document.createElement('a');
      link.href = url;
      link.download = result.fileName || caseDocument.originalFileName || `case-document-${caseDocument.id}`;
      window.document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      setNotice(`Документ «${caseDocument.originalFileName}» подготовлен к скачиванию.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось скачать документ по делу.');
    } finally {
      setDownloadingDocumentId(null);
    }
  };

  const previewDocument = async (caseDocument: ClientCaseDocument) => {
    if (!caseItem) return;

    setPreviewingDocumentId(caseDocument.id);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.downloadCaseDocument(caseItem.id, caseDocument.id);
      const url = URL.createObjectURL(result.blob);
      const contentType = result.blob.type || caseDocument.contentType || '';
      const normalizedContentType = contentType.toLowerCase();
      const isHtml = normalizedContentType.includes('html');
      const isText = normalizedContentType.startsWith('text/') && !isHtml;
      const readableContent = (isHtml || (isText && result.blob.size <= MAX_INLINE_TEXT_PREVIEW_BYTES))
        ? await result.blob.text()
        : undefined;
      setDocumentPreview({
        document: caseDocument,
        url,
        contentType,
        fileName: result.fileName || caseDocument.originalFileName || `case-document-${caseDocument.id}`,
        htmlContent: isHtml ? readableContent : undefined,
        textContent: isText ? readableContent : undefined,
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось открыть предпросмотр документа по делу.');
    } finally {
      setPreviewingDocumentId(null);
    }
  };

  const sendDocumentToYandexDisk = async (caseDocument: ClientCaseDocument) => {
    if (!caseItem) return;

    setSendingYandexDocumentId(caseDocument.id);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.uploadCaseDocumentToYandexDisk(caseDocument.id);
      setNotice(`Документ «${caseDocument.originalFileName}» отправлен на Яндекс.Диск: ${result.path}.`);
    } catch (err) {
      setError(extractDisplayErrorMessage(err, 'Не удалось отправить документ на Яндекс.Диск.'));
    } finally {
      setSendingYandexDocumentId(null);
    }
  };

  const updateDocument = async (caseDocument: ClientCaseDocument, payload: { documentType: string; status: string }) => {
    if (!caseItem) return;
    if (!canManageDocuments) return;

    setUpdatingDocumentId(caseDocument.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.updateCaseDocument(caseItem.id, caseDocument.id, {
        documentType: payload.documentType.trim() || undefined,
        status: payload.status.trim() || undefined,
      });
      setNotice(`Документ «${caseDocument.originalFileName}» обновлен.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось обновить документ по делу.');
    } finally {
      setUpdatingDocumentId(null);
    }
  };

  const uploadDocumentVersion = async (caseDocument: ClientCaseDocument, file: File | null | undefined) => {
    if (!caseItem || !file) return;
    if (!canManageDocuments) return;

    setUploadingDocumentVersionId(caseDocument.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.uploadCaseDocumentVersion(caseDocument.id, file, {
        documentType: caseDocument.documentType,
        status: caseDocument.status,
      });
      setNotice(`Новая версия документа «${caseDocument.originalFileName}» загружена.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить новую версию документа по делу.');
    } finally {
      setUploadingDocumentVersionId(null);
    }
  };

  const deleteDocument = async (caseDocument: ClientCaseDocument) => {
    if (!caseItem) return;
    if (!canDeleteDocuments) return;
    const approved = await confirm({
      title: 'Удалить документ?',
      description: `Документ «${caseDocument.originalFileName}» будет удален из дела.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setDeletingDocumentId(caseDocument.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.deleteCaseDocument(caseItem.id, caseDocument.id);
      setNotice(`Документ «${caseDocument.originalFileName}» удален.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось удалить документ по делу.');
    } finally {
      setDeletingDocumentId(null);
    }
  };

  const addHistoryEntry = async () => {
    if (!caseItem) return;
    const text = historyDraft.trim();
    if (!text) {
      setError('Введите текст комментария по делу.');
      return;
    }

    setAddingHistoryEntry(true);
    setError(null);
    setNotice(null);
    try {
      await apiService.addCaseHistoryEntry(caseItem.id, { text });
      setHistoryDraft('');
      setNotice('Комментарий добавлен в историю дела.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить комментарий в историю дела.');
    } finally {
      setAddingHistoryEntry(false);
    }
  };

  const addConversationEntry = async () => {
    if (!caseItem || !canEdit) return;
    const text = conversationDraft.trim();
    if (!text) {
      setError('Введите текст сообщения по делу.');
      return;
    }

    setAddingConversationEntry(true);
    setError(null);
    setNotice(null);
    try {
      const items = await apiService.addCaseConversationEntry(caseItem.id, conversationFiles.length > 0 ? { text, files: conversationFiles } : { text });
      setConversation(items ?? []);
      setConversationDraft('');
      setConversationFiles([]);
      setNotice('Сообщение добавлено в переписку по делу.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить сообщение в переписку по делу.');
    } finally {
      setAddingConversationEntry(false);
    }
  };

  const downloadConversationAttachment = async (attachment: ClientCaseConversationAttachment) => {
    if (!caseItem) return;

    setDownloadingDocumentId(attachment.documentId);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.downloadCaseDocument(caseItem.id, attachment.documentId);
      const url = URL.createObjectURL(result.blob);
      const link = window.document.createElement('a');
      link.href = url;
      link.download = result.fileName || attachment.fileName || `case-conversation-attachment-${attachment.documentId}`;
      window.document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
      setNotice(`Файл «${attachment.fileName || result.fileName}» подготовлен к скачиванию.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось скачать вложение переписки.');
    } finally {
      setDownloadingDocumentId(null);
    }
  };

  const runCourtSync = async (payload?: ClientCaseCourtSyncRunRequest) => {
    if (!caseItem) return;

    setSyncingCourt(true);
    setError(null);
    setNotice(null);
    try {
      const result = payload
        ? await apiService.runCaseCourtSync(caseItem.id, payload)
        : await apiService.runCaseCourtSync(caseItem.id);
      setLastCourtSyncResult(result);
      setNotice(result.status === 'external-sync-completed' ? null : result.message);
      setActiveTab(result.status === 'external-sync-completed' ? 'court' : 'history');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось запустить проверку судебного источника.');
    } finally {
      setSyncingCourt(false);
    }
  };

  const recordManualCourtCheck = async () => {
    const comment = await prompt({
      title: 'Ручная проверка суда',
      label: 'Комментарий',
      multiline: true,
      confirmLabel: 'Сохранить',
      cancelLabel: 'Отмена',
    });
    if (comment === null) return;

    await runCourtSync({ manualOnly: true, comment: comment.trim() });
  };

  const searchCourtSyncCases = async () => {
    if (!caseItem) return;

    const sourceUrl = courtInstanceDraft.courtUrl.trim() || caseItem.courtUrl || '';
    if (!sourceUrl.trim()) {
      setError('Укажите ссылку на судебную карточку или страницу поиска суда.');
      return;
    }

    setSearchingCourtSync(true);
    setError(null);
    setNotice(null);
    try {
      const results = await apiService.searchCaseCourtSyncCases(caseItem.id, {
        sourceUrl: sourceUrl.trim(),
        courtCaseNumber: courtInstanceDraft.courtCaseNumber.trim() || caseItem.courtCaseNumber || null,
        judicialUid: courtInstanceDraft.judicialUid.trim() || caseItem.judicialUid || null,
        courtName: courtInstanceDraft.courtName.trim() || caseItem.courtName || null,
        instance: activeCourtInstance,
      });
      setCourtSyncSearchResults(results ?? []);
      setNotice((results?.length ?? 0) > 0 ? `Найдено карточек: ${results.length}.` : 'По указанным параметрам карточки не найдены.');
    } catch (err) {
      setCourtSyncSearchResults([]);
      setError(err instanceof Error ? err.message : 'Не удалось найти судебную карточку.');
    } finally {
      setSearchingCourtSync(false);
    }
  };

  const bindCourtSyncCase = async (result: ClientCaseCourtSyncSearchResult) => {
    if (!caseItem) return;

    if (result.requiresReview) {
      const replacementLabels = result.differences
        .filter((difference) => difference.kind === 'replace')
        .map((difference) => difference.label);
      const approved = await confirm({
        title: 'Заменить данные дела?',
        description: `Судебная карточка отличается по полям: ${replacementLabels.join(', ')}. Проверьте сравнение в карточке результата перед продолжением.`,
        confirmLabel: 'Подтвердить замену',
        cancelLabel: 'Отмена',
        variant: 'primary',
      });
      if (!approved) return;
    }

    setBindingCourtSyncSource(true);
    setError(null);
    setNotice(null);
    try {
      const binding = await apiService.bindCaseCourtSyncCase(caseItem.id, {
        instance: activeCourtInstance,
        providerKey: result.providerKey,
        sourceType: result.sourceType,
        sourceUrl: result.sourceUrl,
        courtName: result.courtName,
        courtCaseNumber: result.courtCaseNumber,
        judicialUid: result.judicialUid,
        judgeName: result.differences.find((difference) => difference.field === 'judgeName')?.sourceValue ?? null,
        confirmDifferences: result.requiresReview,
      });
      setCourtInstances((current) => {
        const exists = current.some((entry) => entry.instance === binding.courtInstance.instance);
        return exists
          ? current.map((entry) => entry.instance === binding.courtInstance.instance ? binding.courtInstance : entry)
          : [...current, binding.courtInstance];
      });
      setCourtInstanceDraft(buildCourtInstanceDraft(binding.courtInstance));
      setCaseItem((current) => current
        ? {
          ...current,
          courtName: binding.courtName,
          courtCaseNumber: binding.courtCaseNumber,
          judicialUid: binding.judicialUid,
          courtUrl: binding.courtUrl,
        }
        : current);
      setCourtSyncSearchResults([]);
      setNotice(binding.message || 'Судебная карточка привязана к делу.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось привязать судебную карточку.');
    } finally {
      setBindingCourtSyncSource(false);
    }
  };

  const saveCourtInstance = async () => {
    if (!caseItem) return;

    setSavingCourtInstance(true);
    setError(null);
    setNotice(null);
    try {
      const item = await apiService.updateCaseCourtInstance(caseItem.id, activeCourtInstance, {
        courtId: courtInstanceDraft.courtId ? Number(courtInstanceDraft.courtId) : null,
        courtName: courtInstanceDraft.courtName.trim(),
        courtCaseNumber: courtInstanceDraft.courtCaseNumber.trim(),
        judicialUid: courtInstanceDraft.judicialUid.trim(),
        courtUrl: courtInstanceDraft.courtUrl.trim(),
        judgeName: courtInstanceDraft.judgeName.trim(),
        courtroom: courtInstanceDraft.courtroom.trim(),
        secretaryName: courtInstanceDraft.secretaryName.trim(),
        notes: courtInstanceDraft.notes.trim(),
      });
      setCourtInstances((current) => {
        const exists = current.some((entry) => entry.instance === item.instance);
        return exists ? current.map((entry) => entry.instance === item.instance ? item : entry) : [...current, item];
      });
      setNotice(`Инстанция «${item.instanceLabel}» сохранена.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить судебную инстанцию.');
    } finally {
      setSavingCourtInstance(false);
    }
  };

  const saveCourtComplaint = async () => {
    if (!caseItem) return;
    if (!courtComplaintDraft.title.trim()) {
      setError('Укажите название жалобы.');
      return;
    }

    setSavingCourtComplaint(true);
    setError(null);
    setNotice(null);
    try {
      const payload = {
        complaintType: courtComplaintDraft.complaintType,
        title: courtComplaintDraft.title.trim(),
        targetInstance: courtComplaintDraft.targetInstance,
        filedAt: courtComplaintDraft.filedAt || null,
        status: courtComplaintDraft.status,
        clientCaseDocumentId: courtComplaintDraft.clientCaseDocumentId ? Number(courtComplaintDraft.clientCaseDocumentId) : null,
        notes: courtComplaintDraft.notes.trim(),
      };
      const item = editingCourtComplaint
        ? await apiService.updateCaseCourtComplaint(caseItem.id, activeCourtInstance, editingCourtComplaint.id, payload)
        : await apiService.createCaseCourtComplaint(caseItem.id, activeCourtInstance, payload);
      setCourtInstances((current) => current.map((entry) => entry.instance === activeCourtInstance
        ? {
          ...entry,
          complaints: editingCourtComplaint
            ? entry.complaints.map((complaint) => complaint.id === item.id ? item : complaint)
            : [item, ...entry.complaints],
        }
        : entry));
      setCourtComplaintDraft(buildCourtComplaintDraft(activeCourtInstance));
      setEditingCourtComplaint(null);
      setNotice(editingCourtComplaint ? 'Жалоба обновлена.' : 'Жалоба добавлена.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить жалобу.');
    } finally {
      setSavingCourtComplaint(false);
    }
  };

  const startEditCourtComplaint = (complaint: ClientCaseCourtComplaint) => {
    setEditingCourtComplaint(complaint);
    setCourtComplaintDraft(buildCourtComplaintDraft(activeCourtInstance, complaint));
  };

  const archiveCourtComplaint = async (complaint: ClientCaseCourtComplaint) => {
    if (!caseItem) return;

    setArchivingCourtComplaintId(complaint.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.archiveCaseCourtComplaint(caseItem.id, activeCourtInstance, complaint.id);
      setCourtInstances((current) => current.map((entry) => entry.instance === activeCourtInstance
        ? { ...entry, complaints: entry.complaints.filter((item) => item.id !== complaint.id) }
        : entry));
      if (editingCourtComplaint?.id === complaint.id) {
        setEditingCourtComplaint(null);
        setCourtComplaintDraft(buildCourtComplaintDraft(activeCourtInstance));
      }
      setNotice('Жалоба архивирована.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось архивировать жалобу.');
    } finally {
      setArchivingCourtComplaintId(null);
    }
  };

  const addCourtEvent = async () => {
    if (!caseItem) return;
    if (!courtEventScheduledAtDraft) {
      setError('Укажите дату судебного события.');
      return;
    }

    setSavingCourtEvent(true);
    setError(null);
    setNotice(null);
    try {
      const selectedCourtInstance = courtInstances.find((item) => item.instance === activeCourtInstance);
      const item = await apiService.createCaseCourtEvent(caseItem.id, {
        eventType: courtEventTypeDraft,
        title: courtEventTitleDraft.trim() || undefined,
        scheduledAt: fromDateTimeInput(courtEventScheduledAtDraft) ?? undefined,
        allDay: courtEventTypeDraft !== 'hearing',
        courtId: courtEventCourtIdDraft ? Number(courtEventCourtIdDraft) : undefined,
        courtInstance: activeCourtInstance,
        courtName: selectedCourtInstance?.courtName || caseItem.courtName || undefined,
        courtCaseNumber: selectedCourtInstance?.courtCaseNumber || caseItem.courtCaseNumber || undefined,
        courtroom: selectedCourtInstance?.courtroom || caseItem.courtroom || undefined,
        movementStatus: courtEventStatusDraft,
        resultSummary: courtEventResultDraft.trim() || undefined,
        clientCaseDocumentId: courtEventDocumentIdDraft ? Number(courtEventDocumentIdDraft) : undefined,
      });
      setCourtEvents((current) => [...current, item].sort((left, right) => Date.parse(left.scheduledAt) - Date.parse(right.scheduledAt)));
      setCourtEventTitleDraft('');
      setCourtEventScheduledAtDraft('');
      setCourtEventCourtIdDraft('');
      setCourtEventResultDraft('');
      setCourtEventDocumentIdDraft('');
      setNotice('Судебное событие добавлено.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить судебное событие.');
    } finally {
      setSavingCourtEvent(false);
    }
  };

  const addCourtParty = async () => {
    if (!caseItem) return;
    if (!courtPartyNameDraft.trim()) {
      setError('Укажите сторону процесса.');
      return;
    }

    setSavingCourtParty(true);
    setError(null);
    setNotice(null);
    try {
      const item = await apiService.createCaseCourtParty(caseItem.id, {
        displayName: courtPartyNameDraft.trim(),
        side: courtPartySideDraft,
        processRole: courtPartyRoleDraft.trim() || undefined,
        representativeName: courtPartyRepresentativeDraft.trim() || undefined,
        notes: courtPartyNotesDraft.trim() || undefined,
      });
      setCourtParties((current) => [...current, item].sort((left, right) => left.displayName.localeCompare(right.displayName, 'ru')));
      setCourtPartyNameDraft('');
      setCourtPartyRoleDraft('');
      setCourtPartyRepresentativeDraft('');
      setCourtPartyNotesDraft('');
      setNotice('Сторона процесса добавлена.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить сторону процесса.');
    } finally {
      setSavingCourtParty(false);
    }
  };

  const archiveCourtParty = async (party: ClientCaseCourtParty) => {
    if (!caseItem) return;
    setArchivingCourtPartyId(party.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.archiveCaseCourtParty(caseItem.id, party.id);
      setCourtParties((current) => current.filter((item) => item.id !== party.id));
      setNotice('Сторона процесса архивирована.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось архивировать сторону процесса.');
    } finally {
      setArchivingCourtPartyId(null);
    }
  };

  const addProceduralDeadline = async () => {
    if (!caseItem) return;
    if (!deadlineDueAtDraft) {
      setError('Укажите дату процессуального срока.');
      return;
    }

    setSavingProceduralDeadline(true);
    setError(null);
    setNotice(null);
    try {
      const item = await apiService.createCaseProceduralDeadline(caseItem.id, {
        deadlineType: deadlineTypeDraft,
        title: deadlineTitleDraft.trim() || undefined,
        dueAt: deadlineDueAtDraft,
        allDay: true,
        status: 'active',
        warningDaysBefore: Number(deadlineWarningDaysDraft) || 7,
        clientCaseDocumentId: deadlineDocumentIdDraft ? Number(deadlineDocumentIdDraft) : undefined,
        sourceType: deadlineCourtEventIdDraft ? 'court-event' : undefined,
        sourceId: deadlineCourtEventIdDraft ? Number(deadlineCourtEventIdDraft) : undefined,
        notes: deadlineNotesDraft.trim() || undefined,
      });
      setProceduralDeadlines((current) => [...current, item].sort((left, right) => Date.parse(left.dueAt) - Date.parse(right.dueAt)));
      setDeadlineTitleDraft('');
      setDeadlineDueAtDraft('');
      setDeadlineDocumentIdDraft('');
      setDeadlineCourtEventIdDraft('');
      setDeadlineNotesDraft('');
      setDeadlineCalculationPreview(null);
      setNotice('Процессуальный срок добавлен.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить процессуальный срок.');
    } finally {
      setSavingProceduralDeadline(false);
    }
  };

  const calculateProceduralDeadline = async () => {
    if (!caseItem) return;
    const startAt = caseItem.courtDecisionAt || caseItem.claimFiledAt || caseItem.nextHearingAt || new Date().toISOString();
    const preset = deadlineTypeDraft === 'appeal'
      ? { rule: 'calendarMonths', monthsToAdd: 1, daysToAdd: 0 }
      : deadlineTypeDraft === 'cassation'
        ? { rule: 'calendarMonths', monthsToAdd: 3, daysToAdd: 0 }
        : { rule: 'workingDays', monthsToAdd: 0, daysToAdd: 5 };

    setCalculatingProceduralDeadline(true);
    setError(null);
    try {
      const result = await apiService.calculateCaseProceduralDeadline({
        startAt,
        rule: preset.rule,
        monthsToAdd: preset.monthsToAdd,
        daysToAdd: preset.daysToAdd,
        rollForwardToWorkingDay: true,
      });
      if (result.deadlineAt) {
        setDeadlineDueAtDraft(result.deadlineAt.slice(0, 10));
        setDeadlineCalculationPreview(result);
        setNotice('Дата процессуального срока рассчитана.');
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось рассчитать процессуальный срок.');
    } finally {
      setCalculatingProceduralDeadline(false);
    }
  };

  const closeProceduralDeadline = async (deadline: ClientCaseProceduralDeadline) => {
    if (!caseItem) return;
    const reason = await prompt({
      title: 'Закрыть процессуальный срок',
      label: 'Причина закрытия',
      defaultValue: deadline.closeReason || '',
      multiline: true,
      confirmLabel: 'Закрыть',
      cancelLabel: 'Отмена',
    });
    if (reason === null) return;

    setClosingProceduralDeadlineId(deadline.id);
    setError(null);
    setNotice(null);
    try {
      const updated = await apiService.closeCaseProceduralDeadline(caseItem.id, deadline.id, reason.trim() || undefined);
      setProceduralDeadlines((current) => current.map((item) => item.id === updated.id ? updated : item));
      setNotice('Процессуальный срок закрыт.');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось закрыть процессуальный срок.');
    } finally {
      setClosingProceduralDeadlineId(null);
    }
  };

  const addParticipant = async () => {
    if (!caseItem) return;

    const sourceId = Number(participantSourceId);
    if (!Number.isInteger(sourceId) || sourceId <= 0) {
      setError('Выберите участника дела из справочника.');
      return;
    }

    setAddingParticipant(true);
    setError(null);
    try {
      await apiService.addCaseRelatedParty(caseItem.id, {
        sourceType: participantSourceType,
        sourceId,
        role: participantRole.trim() || undefined,
        notes: participantNotes.trim() || undefined,
      });
      setParticipantSourceId('');
      setParticipantRole('');
      setParticipantNotes('');
      setActiveTab('participants');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить участника дела.');
    } finally {
      setAddingParticipant(false);
    }
  };

  const deleteParticipant = async (party: ClientCaseRelatedParty) => {
    if (!caseItem) return;
    const approved = await confirm({
      title: 'Удалить участника из дела?',
      description: `Связь с участником «${party.displayName}» будет удалена из карточки дела.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setDeletingParticipantId(party.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.deleteCaseRelatedParty(caseItem.id, party.id);
      setNotice(`Связь с участником «${party.displayName}» удалена.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось удалить связь с участником дела.');
    } finally {
      setDeletingParticipantId(null);
    }
  };

  const restoreParticipant = async (party: ClientCaseRelatedParty) => {
    if (!caseItem) return;

    const sourceId = getRelatedPartySourceId(party);
    if (!sourceId) {
      setError('Не удалось определить источник архивной связи для восстановления.');
      return;
    }

    setRestoringParticipantId(party.id);
    setError(null);
    setNotice(null);
    try {
      await apiService.addCaseRelatedParty(caseItem.id, {
        sourceType: party.sourceType as ParticipantSourceType,
        sourceId,
        role: party.role?.trim() || undefined,
        notes: party.notes?.trim() || undefined,
      });
      setNotice(`Связь с участником «${party.displayName}» восстановлена.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось восстановить связь с участником дела.');
    } finally {
      setRestoringParticipantId(null);
    }
  };

  const applyLoadedWorkOrder = (loadedWorkOrder: ClientCaseWorkOrder | null) => {
    setWorkOrder(loadedWorkOrder);
    setWorkOrderDraft(buildWorkOrderHeaderDraft(loadedWorkOrder?.id ? loadedWorkOrder : null));
    setWorkOrderItemDrafts(Object.fromEntries(
      (loadedWorkOrder?.items ?? []).map((item) => [item.id, buildWorkOrderItemDraft(item)]),
    ));
  };

  const saveWorkOrderHeader = async () => {
    if (!caseItem) return;

    setSavingWorkOrder(true);
    setError(null);
    setNotice(null);
    try {
      const payload = {
        title: workOrderDraft.title.trim() || undefined,
        status: workOrderDraft.status,
        currency: workOrderDraft.currency.trim() || 'RUB',
        notes: workOrderDraft.notes.trim() || undefined,
      };
      const savedWorkOrder = workOrder?.id
        ? await apiService.updateCaseWorkOrder(caseItem.id, payload)
        : await apiService.createCaseWorkOrder(caseItem.id, payload);
      applyLoadedWorkOrder(savedWorkOrder);
      setNotice(workOrder?.id ? 'Заказ-наряд обновлен.' : 'Заказ-наряд создан.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить заказ-наряд.');
    } finally {
      setSavingWorkOrder(false);
    }
  };

  const deleteWorkOrder = async () => {
    if (!caseItem || !workOrder?.id) return;
    const approved = await confirm({
      title: 'Удалить заказ-наряд?',
      description: 'Заказ-наряд по делу будет удален.',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setDeletingWorkOrder(true);
    setError(null);
    setNotice(null);
    try {
      await apiService.deleteCaseWorkOrder(caseItem.id);
      applyLoadedWorkOrder(null);
      setNewWorkOrderItemDraft(buildWorkOrderItemDraft(null));
      setNotice('Заказ-наряд удален.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось удалить заказ-наряд.');
    } finally {
      setDeletingWorkOrder(false);
    }
  };

  const addWorkOrderItem = async () => {
    if (!caseItem || !workOrder?.id) return;
    const payload = buildWorkOrderItemPayload(newWorkOrderItemDraft);
    if (!payload.serviceName) {
      setError('Введите название услуги.');
      return;
    }

    setSavingWorkOrderItem(true);
    setError(null);
    setNotice(null);
    try {
      const savedWorkOrder = await apiService.addCaseWorkOrderItem(caseItem.id, payload);
      applyLoadedWorkOrder(savedWorkOrder);
      setNewWorkOrderItemDraft(buildWorkOrderItemDraft(null));
      setNotice('Услуга добавлена в заказ-наряд.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить услугу в заказ-наряд.');
    } finally {
      setSavingWorkOrderItem(false);
    }
  };

  const updateWorkOrderItem = async (item: ClientCaseWorkOrderItem) => {
    if (!caseItem || !workOrder?.id) return;
    const draft = workOrderItemDrafts[item.id] ?? buildWorkOrderItemDraft(item);
    const payload = buildWorkOrderItemPayload(draft);
    if (!payload.serviceName) {
      setError('Введите название услуги.');
      return;
    }

    setSavingWorkOrderItem(true);
    setError(null);
    setNotice(null);
    try {
      const savedWorkOrder = await apiService.updateCaseWorkOrderItem(caseItem.id, item.id, payload);
      applyLoadedWorkOrder(savedWorkOrder);
      setNotice('Услуга в заказ-наряде обновлена.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось обновить услугу в заказ-наряде.');
    } finally {
      setSavingWorkOrderItem(false);
    }
  };

  const deleteWorkOrderItem = async (item: ClientCaseWorkOrderItem) => {
    if (!caseItem || !workOrder?.id) return;
    const approved = await confirm({
      title: 'Удалить услугу?',
      description: `Услуга «${item.serviceName}» будет удалена из заказ-наряда.`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!approved) return;

    setDeletingWorkOrderItemId(item.id);
    setError(null);
    setNotice(null);
    try {
      const savedWorkOrder = await apiService.deleteCaseWorkOrderItem(caseItem.id, item.id);
      applyLoadedWorkOrder(savedWorkOrder);
      setNotice('Услуга удалена из заказ-наряда.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось удалить услугу из заказ-наряда.');
    } finally {
      setDeletingWorkOrderItemId(null);
    }
  };

  const createInvoiceFromWorkOrder = async () => {
    if (!caseItem || !workOrder?.id) return;

    setCreatingWorkOrderInvoice(true);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.createCaseWorkOrderInvoice(caseItem.id);
      applyLoadedWorkOrder(result.workOrder);
      setNotice(`Счет №${result.number} создан на сумму ${formatMoney(result.amount)}.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать счет на основе заказ-наряда.');
    } finally {
      setCreatingWorkOrderInvoice(false);
    }
  };

  const createActFromWorkOrder = async () => {
    if (!caseItem || !workOrder?.id) return;

    setCreatingWorkOrderAct(true);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.createCaseWorkOrderAct(caseItem.id);
      applyLoadedWorkOrder(result.workOrder);
      setNotice(`Акт №${result.number} создан на сумму ${formatMoney(result.amount)}.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать акт на основе заказ-наряда.');
    } finally {
      setCreatingWorkOrderAct(false);
    }
  };

  const createContractFromWorkOrder = async () => {
    if (!caseItem || !workOrder?.id) return;

    setCreatingWorkOrderContract(true);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.createCaseWorkOrderContract(caseItem.id);
      applyLoadedWorkOrder(result.workOrder);
      setNotice(`Договор №${result.number} создан на сумму ${formatMoney(result.amount ?? 0)}.`);
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать договор на основе заказ-наряда.');
    } finally {
      setCreatingWorkOrderContract(false);
    }
  };

  const printWorkOrder = async () => {
    if (!caseItem || !workOrder?.id) return;

    setPrintingWorkOrder(true);
    setError(null);
    setNotice(null);
    try {
      const html = await apiService.getCaseWorkOrderPrintHtml(caseItem.id);
      const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const opened = window.open(url, '_blank', 'noopener,noreferrer');
      if (!opened) {
        const link = window.document.createElement('a');
        link.href = url;
        link.download = `case-${caseItem.id}-work-order.html`;
        window.document.body.appendChild(link);
        link.click();
        link.remove();
      }
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
      setNotice('Печатная форма заказ-наряда подготовлена.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось подготовить печатную форму заказ-наряда.');
    } finally {
      setPrintingWorkOrder(false);
    }
  };

  const generateWorkOrderDocument = async () => {
    if (!caseItem || !workOrder?.id) return;

    setGeneratingWorkOrderDocument(true);
    setError(null);
    setNotice(null);
    try {
      const document = await apiService.generateCaseWorkOrderDocument(caseItem.id);
      setReloadToken((value) => value + 1);
      setNotice(`Заказ-наряд сохранен в документы дела: ${document.originalFileName}.`);
      setActiveTab('documents');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить заказ-наряд в документы дела.');
    } finally {
      setGeneratingWorkOrderDocument(false);
    }
  };

  const runWorkOrderApprovalAction = async (action: 'submit' | 'approve' | 'revision') => {
    if (!caseItem || !workOrder?.id) return;

    setWorkOrderApprovalAction(action);
    setError(null);
    setNotice(null);
    try {
      const payload = { comment: workOrderApprovalComment.trim() || undefined };
      const savedWorkOrder = action === 'submit'
        ? await apiService.submitCaseWorkOrderApproval(caseItem.id, payload)
        : action === 'approve'
          ? await apiService.approveCaseWorkOrder(caseItem.id, payload)
          : await apiService.requestCaseWorkOrderRevision(caseItem.id, payload);
      applyLoadedWorkOrder(savedWorkOrder);
      setWorkOrderApprovalComment('');
      setNotice(action === 'submit'
        ? 'Заказ-наряд отправлен на согласование.'
        : action === 'approve'
          ? 'Заказ-наряд согласован.'
          : 'Заказ-наряд возвращен на доработку.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось выполнить действие по согласованию заказ-наряда.');
    } finally {
      setWorkOrderApprovalAction(null);
    }
  };

  const sendExternalWorkOrderApproval = async () => {
    if (!caseItem || !externalApprovalRecipientName.trim() || !externalApprovalRecipientEmail.trim()) {
      setError('Укажите имя и email клиента или представителя.');
      return;
    }

    const confirmed = await confirm({
      title: externalApprovalStatus ? 'Отправить новую версию?' : 'Отправить клиенту?',
      description: externalApprovalStatus
        ? `Предыдущая активная ссылка будет отозвана. Получатель получит новую ссылку, код и ${externalApprovalDocumentIds.length} вложений.`
        : `Получатель получит одноразовую ссылку, код подтверждения и ${externalApprovalDocumentIds.length} вложений по email${externalApprovalDeliveryChannel === 'email_and_max' ? ', а безопасную кнопку перехода — также в MAX' : ''}.`,
      confirmLabel: 'Отправить',
    });
    if (!confirmed) return;

    setExternalApprovalSaving(true);
    setError(null);
    try {
      const status = await apiService.createExternalWorkOrderApproval(caseItem.id, {
        recipientName: externalApprovalRecipientName.trim(),
        recipientEmail: externalApprovalRecipientEmail.trim(),
        recipientType: externalApprovalRecipientType,
        documentIds: externalApprovalDocumentIds,
        deliveryChannel: externalApprovalDeliveryChannel,
      });
      setExternalApprovalStatus(status);
      setNotice(`Версия ${status.version} отправлена на ${status.recipientEmail}.`);
      const loadedWorkOrder = await apiService.getCaseWorkOrder(caseItem.id);
      applyLoadedWorkOrder(loadedWorkOrder);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось отправить заказ-наряд клиенту.');
    } finally {
      setExternalApprovalSaving(false);
    }
  };

  const revokeExternalWorkOrderApproval = async () => {
    if (!caseItem) return;
    const confirmed = await confirm({
      title: 'Отозвать внешнюю ссылку?',
      description: 'Клиент больше не сможет открыть ссылку или принять решение по этой версии.',
      confirmLabel: 'Отозвать',
      variant: 'danger',
    });
    if (!confirmed) return;

    setExternalApprovalSaving(true);
    setError(null);
    try {
      await apiService.revokeExternalWorkOrderApproval(caseItem.id);
      setExternalApprovalStatus((current) => current ? { ...current, status: 'revoked' } : current);
      setNotice('Внешняя ссылка отозвана.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось отозвать внешнюю ссылку.');
    } finally {
      setExternalApprovalSaving(false);
    }
  };

  const copyCaseLink = async () => {
    if (!caseItem) return;

    const link = `${window.location.origin}/cases/${caseItem.id}`;
    try {
      await navigator.clipboard?.writeText(link);
    } finally {
      setCopiedLink(true);
    }
  };

  const createTaskForCase = async () => {
    if (!caseItem || !canCreateCaseTask) return;

    setCreatingTask(true);
    setError(null);
    try {
      await apiService.createTaskJournalItem(buildCaseTaskPayload(caseItem, caseTimeZoneId));
      setReloadToken((value) => value + 1);
      if (canViewTasks) {
        setActiveTab('tasks');
      }
      setNotice('Задача по делу создана.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать задачу по делу.');
    } finally {
      setCreatingTask(false);
    }
  };

  const createStageTaskForCase = async (template: CaseStageTaskTemplate, stageOverride?: string | null) => {
    if (!caseItem || !canCreateCaseTask) return;
    const sourceCase = stageOverride ? { ...caseItem, stage: stageOverride } : caseItem;

    setCreatingTask(true);
    setError(null);
    try {
      await apiService.createTaskJournalItem(buildCaseTaskPayload(sourceCase, caseTimeZoneId, template));
      setReloadToken((value) => value + 1);
      if (canViewTasks) {
        setActiveTab('tasks');
      }
      setNotice(`Задача по этапу «${template.stageLabel}» создана.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать задачу по этапу дела.');
    } finally {
      setCreatingTask(false);
    }
  };

  const prepareStageDeadlineSuggestion = (suggestion: CaseStageDeadlineSuggestion) => {
    setDeadlineTypeDraft(suggestion.deadlineType);
    setDeadlineTitleDraft(suggestion.title);
    setDeadlineDueAtDraft('');
    setDeadlineDocumentIdDraft('');
    setDeadlineCourtEventIdDraft('');
    setDeadlineNotesDraft(suggestion.description);
    setDeadlineCalculationPreview(null);
    setActiveTab('court');
    setStageTransitionPrompt(null);
    setNotice('Процессуальный срок подготовлен в судебном блоке. Проверьте дату и добавьте срок.');
  };

  const openOwner = () => {
    if (!caseItem) return;
    if (caseItem.clientId) {
      onOpenClient?.(caseItem.clientId, caseItem.id);
      return;
    }

    if (caseItem.legalEntityId) {
      onOpenLegalEntity?.(caseItem.legalEntityId, caseItem.id);
    }
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-slate-50 px-4 py-6 sm:px-6 lg:px-8">
        <div className="mx-auto max-w-[1500px] rounded-xl border border-slate-200 bg-white px-4 py-12 text-center text-slate-500 shadow-sm">
          <span className="inline-flex items-center gap-2"><Loader2 className="h-4 w-4 animate-spin" /> Загрузка карточки дела...</span>
        </div>
      </div>
    );
  }

  if (!caseItem) {
    return (
      <div className="min-h-screen bg-slate-50 px-4 py-6 sm:px-6 lg:px-8">
        <div className="mx-auto max-w-[900px] rounded-xl border border-rose-200 bg-rose-50 p-5 text-rose-700">
          {error ?? 'Дело не найдено.'}
          {notice ? <div className="mt-3 rounded-lg border border-cyan-200 bg-cyan-50 px-3 py-2 text-sm text-cyan-800">{notice}</div> : null}
          <div className="mt-4 flex flex-wrap gap-2">
            <button type="button" onClick={() => void requestCaseAccess()} disabled={requestingAccess} className="inline-flex items-center gap-2 rounded-lg bg-rose-600 px-4 py-2 text-sm font-semibold text-white hover:bg-rose-700 disabled:opacity-50">
              {requestingAccess ? <Loader2 className="h-4 w-4 animate-spin" /> : <LockKeyhole className="h-4 w-4" />}
              Запросить доступ
            </button>
            <button type="button" onClick={onBack} className="rounded-lg border border-rose-200 bg-white px-4 py-2 text-sm font-semibold text-rose-700">
              Вернуться к реестру
            </button>
          </div>
        </div>
      </div>
    );
  }

  const progress = caseItem.taskProgressPercent ?? 0;
  const riskSummary = proceduralDeadlinesLoading
    ? { value: 'Проверяем сроки', tone: 'slate' as SummaryTone }
    : proceduralDeadlinesError
      ? { value: 'Сроки недоступны', tone: 'amber' as SummaryTone }
      : getRiskSummary(caseItem, proceduralDeadlines);
  const nearestCaseDateSummary = proceduralDeadlinesLoading
    ? { value: 'Проверяем сроки', tone: 'slate' as SummaryTone }
    : proceduralDeadlinesError
      ? { value: 'Сроки недоступны', tone: 'amber' as SummaryTone }
      : getNearestCaseDateSummary(caseItem, proceduralDeadlines, new Date(), caseTimeZoneId);
  const inactivitySummary = getInactivitySummary(caseItem);
  const responsibleLastViewedAt = getResponsibleLastViewedAt(caseItem);
  const responsibleLastViewedByName = caseItem.lastViewedByResponsibleUserName ?? caseItem.lastViewedByUserName ?? null;
  const courtSyncSummary = getCourtSyncSummary(caseItem);
  const canRevealCaseSensitiveData = canEdit && (caseItem.canRevealSensitiveData ?? true);
  const canManageDocuments = canEdit;
  const canDeleteDocuments = canManageDocuments && canDelete;

  if (editPageOpen && caseEditDraft) {
    return (
      <CaseEditPage
        caseItem={caseItem}
        draft={caseEditDraft}
        responsibles={responsibles}
        saving={savingCaseEdit}
        error={error}
        canViewFinance={canViewFinance}
        onBack={closeCaseEditPage}
        onChange={setCaseEditDraft}
        onSave={() => void saveCaseFromPage()}
      />
    );
  }

  return (
    <div className="min-h-screen bg-slate-50 px-4 py-6 sm:px-6 lg:px-8">
      <div className="w-full max-w-none space-y-5">
        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_minmax(520px,auto)] xl:items-start">
            <div className="min-w-0">
              <button type="button" onClick={onBack} className="mb-4 inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <ArrowLeft className="h-4 w-4" /> Реестр дел
              </button>
              <div className="flex flex-wrap items-center gap-2">
                <span className="inline-flex items-center gap-2 rounded-full bg-blue-50 px-3 py-1 text-sm font-semibold text-blue-700">
                  <Briefcase className="h-4 w-4" /> {caseItem.caseNumber || `#${caseItem.id}`}
                </span>
                <span className="rounded-full bg-slate-100 px-3 py-1 text-sm font-medium text-slate-700">{STATUS_LABELS[caseItem.status] ?? caseItem.status}</span>
                <span className="rounded-full bg-amber-50 px-3 py-1 text-sm font-medium text-amber-700">{PRIORITY_LABELS[caseItem.priority ?? ''] ?? caseItem.priority ?? 'Обычный'}</span>
                {caseItem.isHiddenFromExecutor ? (
                  <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-3 py-1 text-sm font-medium text-rose-700">
                    <LockKeyhole className="h-3.5 w-3.5" /> Скрыто от исполнителя
                  </span>
                ) : null}
              </div>
              <h1 className="mt-3 text-3xl font-bold text-slate-900">{caseItem.title || 'Без названия'}</h1>
              <p className="mt-2 max-w-3xl text-sm text-slate-600">{caseItem.description || 'Описание по делу пока не заполнено.'}</p>
            </div>

            <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 xl:justify-end">
              {canEdit ? (
                <button type="button" onClick={openCaseEditPage} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg bg-blue-600 px-4 text-sm font-semibold text-white hover:bg-blue-700">
                  <Pencil className="h-4 w-4" /> Редактировать дело
                </button>
              ) : null}
              <button type="button" onClick={() => setReloadToken((value) => value + 1)} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-slate-200 px-4 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <RefreshCw className="h-4 w-4" /> Обновить
              </button>
              <button type="button" onClick={() => void copyCaseLink()} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-slate-200 px-4 text-sm font-medium text-slate-700 hover:bg-slate-50">
                {copiedLink ? <Check className="h-4 w-4 text-emerald-600" /> : <Copy className="h-4 w-4" />} {copiedLink ? 'Ссылка скопирована' : 'Ссылка'}
              </button>
              {canCreateCaseTask ? (
                <button type="button" onClick={() => void createTaskForCase()} disabled={creatingTask} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg bg-emerald-600 px-4 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-50">
                  {creatingTask ? <Loader2 className="h-4 w-4 animate-spin" /> : <ClipboardPlus className="h-4 w-4" />} Создать задачу
                </button>
              ) : null}
              <button type="button" onClick={() => void generateDocument()} disabled={generatingDocument} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-blue-200 px-4 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
                {generatingDocument ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} Документ
              </button>
              <button type="button" onClick={() => void runCourtSync()} disabled={syncingCourt} aria-label="Проверить суд по делу" className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-cyan-200 px-4 text-sm font-medium text-cyan-700 hover:bg-cyan-50 disabled:opacity-50">
                {syncingCourt ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Суд
              </button>
              <button type="button" onClick={openOwner} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-slate-200 px-4 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <UserRound className="h-4 w-4" /> Клиент
              </button>
              {canViewFinance ? (
                <button type="button" onClick={() => onOpenFinance?.(caseItem)} disabled={!onOpenFinance} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-indigo-200 px-4 text-sm font-medium text-indigo-700 hover:bg-indigo-50 disabled:opacity-50">
                  <WalletCards className="h-4 w-4" /> Финансы
                </button>
              ) : null}
              <button type="button" onClick={() => void archiveCase()} disabled={savingHeader || caseItem.status === 'Closed'} className="inline-flex h-10 items-center justify-center gap-2 whitespace-nowrap rounded-lg border border-amber-200 px-4 text-sm font-medium text-amber-700 hover:bg-amber-50 disabled:opacity-50">
                {savingHeader ? <Loader2 className="h-4 w-4 animate-spin" /> : <Archive className="h-4 w-4" />} Архив
              </button>
            </div>
          </div>
        </div>

        {error ? <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div> : null}
        {notice ? <div className="rounded-xl border border-cyan-200 bg-cyan-50 px-4 py-3 text-sm text-cyan-800">{notice}</div> : null}
        {canEdit ? <ClientPortalAccessPanel caseItem={caseItem} documents={documents} /> : null}
        {stageTransitionPrompt ? (
          <StageTransitionActions
            prompt={stageTransitionPrompt}
            canCreateTask={canCreateCaseTask}
            creatingTask={creatingTask}
            onCreateTask={(template) => void createStageTaskForCase(template, stageTransitionPrompt.nextStage)}
            onPrepareDeadline={prepareStageDeadlineSuggestion}
            onClose={() => setStageTransitionPrompt(null)}
          />
        ) : null}

        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="grid items-stretch gap-3 md:grid-cols-2 xl:grid-cols-4">
            <label className="flex h-full flex-col justify-between gap-1 text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
              <SystemSelect
                aria-label="Статус дела"
                value={statusDraft}
                onChange={(event) => setStatusDraft(event.target.value)}
                className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                {CASE_STATUS_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </SystemSelect>
            </label>
            <label className="flex h-full flex-col justify-between gap-1 text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Стадия</span>
              <input
                aria-label="Стадия дела"
                value={stageDraft}
                onChange={(event) => setStageDraft(event.target.value)}
                className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                placeholder="Например: судебное производство"
              />
            </label>
            <label className="flex h-full flex-col justify-between gap-1 text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Приоритет</span>
              <SystemSelect
                aria-label="Приоритет дела"
                value={priorityDraft}
                onChange={(event) => setPriorityDraft(event.target.value)}
                className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                {CASE_PRIORITY_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </SystemSelect>
            </label>
            <label className="flex h-full flex-col justify-between gap-1 text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Ответственный</span>
              <SystemSelect
                aria-label="Ответственный по делу"
                value={responsibleDraft}
                onChange={(event) => setResponsibleDraft(event.target.value)}
                className="h-10 w-full rounded-lg border border-slate-200 px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                <option value="">Не выбран</option>
                {responsibles.map((responsible) => (
                  <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>
                ))}
              </SystemSelect>
            </label>
            <label className="flex h-full min-h-[58px] flex-col justify-between rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700">
              <span className="mb-2 block text-xs uppercase tracking-[0.12em] text-slate-400">Доступ</span>
              <span className="inline-flex items-center gap-2">
                <SystemCheckbox
                  type="checkbox"
                  aria-label="Скрыть дело от исполнителя"
                  checked={hiddenFromExecutorDraft}
                  onChange={(event) => setHiddenFromExecutorDraft(event.target.checked)}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                Скрыть от исполнителя
              </span>
            </label>
            <label className="flex h-full min-h-[58px] flex-col justify-between rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700">
              <span className="mb-2 block text-xs uppercase tracking-[0.12em] text-slate-400">Автоконтроль</span>
              <span className="inline-flex items-center gap-2">
                <SystemCheckbox
                  type="checkbox"
                  aria-label="Учитывать дело в автоконтроле"
                  checked={inactivityRemindersDraft}
                  onChange={(event) => void changeHeaderInactivityReminders(event.target.checked)}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                Включен
              </span>
              {!inactivityRemindersDraft && inactivityRemindersDisabledReasonDraft ? (
                <span className="mt-1 text-xs font-normal text-amber-700">
                  {inactivityRemindersDisabledReasonDraft}
                </span>
              ) : null}
            </label>
            <button
              type="button"
              onClick={() => void saveHeaderChanges()}
              disabled={savingHeader}
              className="inline-flex h-full min-h-[58px] items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50">
              {savingHeader ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Сохранить
            </button>
          </div>
        </div>

        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4 2xl:grid-cols-7">
          <SummaryCard label="Клиент" value={getOwnerName(caseItem)} />
          <SummaryCard label="Ответственный" value={getResponsibleName(caseItem)} />
          <SummaryCard label="Ближайший срок" value={nearestCaseDateSummary.value} tone={nearestCaseDateSummary.tone} />
          <SummaryCard label="Просрочки" value={riskSummary.value} tone={riskSummary.tone} />
          {canViewFinance ? (
            <SummaryCard label="Оплачено / долг" value={`${formatMoney(caseItem.incomePaid)} / ${formatMoney(caseItem.incomeOutstanding)}`} />
          ) : null}
          <SummaryCard label="Активность" value={inactivitySummary.value} tone={inactivitySummary.tone} />
          <SummaryCard label="Судебная проверка" value={courtSyncSummary.value} tone={courtSyncSummary.tone} />
        </div>

        {proceduralDeadlinesError && !proceduralDeadlinesLoading ? (
          <div role="alert" className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
            {proceduralDeadlinesError} Нажмите «Обновить», чтобы повторить проверку.
          </div>
        ) : null}

        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="grid gap-5 xl:grid-cols-[300px_minmax(0,1fr)]">
            <CaseClientSidebar
              caseItem={caseItem}
              canRevealSensitive={canRevealCaseSensitiveData}
              onOpenClient={onOpenClient}
              onOpenLegalEntity={onOpenLegalEntity}
              onOpenFinance={canViewFinance ? onOpenFinance : undefined}
              onCreateInvoice={canViewFinance ? onCreateInvoice : undefined}
              onCreatePayment={canViewFinance ? onCreatePayment : undefined}
            />
            <div className="min-w-0">
          <div className="mb-5 flex flex-wrap gap-2">
            {visibleDetailTabs.map((tab) => (
              <button
                key={tab.key}
                type="button"
                onClick={() => setActiveTab(tab.key)}
                className={`rounded-lg px-3 py-2 text-sm font-semibold ${activeTab === tab.key ? 'bg-blue-600 text-white' : 'bg-slate-100 text-slate-700 hover:bg-slate-200'}`}>
                {tab.label}
              </button>
            ))}
          </div>

          {activeTab === 'overview' ? (
            <div className="grid gap-4 lg:grid-cols-[1.5fr_1fr]">
              <InfoSection title="Основные данные" rows={[
                ['Тип', caseItem.caseType || '—'],
                ['Стадия', caseItem.stage || '—'],
                ['Начало', toRuDate(caseItem.startDate) || '—'],
                ['Срок', toRuDate(caseItem.dueDate) || '—'],
                ['Суд', caseItem.courtName || '—'],
                ['№ в суде', caseItem.courtCaseNumber || '—'],
                ['UID дела', caseItem.judicialUid || '—'],
                ['Заседание', formatRuDateTime(caseItem.nextHearingAt) || '—'],
                ['Просмотр ответственным', responsibleLastViewedAt ? `${formatRuDateTime(responsibleLastViewedAt)}${responsibleLastViewedByName ? ` · ${responsibleLastViewedByName}` : ''}` : '—'],
              ]} />
              <InfoSection title="Участники процесса" rows={[
                ['Истец', caseItem.claimant || '—'],
                ['Ответчик', caseItem.defendant || '—'],
                ['Третьи лица', caseItem.thirdParties || '—'],
                ['Представители', caseItem.representatives || '—'],
                ['Судья', caseItem.judgeName || '—'],
              ]} />
              <TextBlock title="Стратегия" value={caseItem.strategy || 'Стратегия по делу пока не заполнена.'} />
              <TextBlock title="Документы" value={caseItem.documentsSummary || 'Сводка документов пока не заполнена.'} />
            </div>
          ) : null}

          {activeTab === 'participants' ? (
            <div className="space-y-4">
              <section className="rounded-xl border border-slate-200 p-4">
                <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Добавить участника</h2>
                <div className="mt-4 grid gap-3 md:grid-cols-[180px_1.4fr_1fr_auto]">
                  <label className="text-sm font-medium text-slate-700">
                    <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Источник</span>
                    <SystemSelect
                      aria-label="Тип участника"
                      value={participantSourceType}
                      onChange={(event) => setParticipantSourceType(event.target.value as ParticipantSourceType)}
                      className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                      <option value="client">Клиент</option>
                      <option value="legalEntity">Юрлицо</option>
                      <option value="externalContact">Контакт</option>
                    </SystemSelect>
                  </label>
                  <div className="space-y-2 text-sm font-medium text-slate-700">
                    <label className="block">
                      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Поиск</span>
                      <input
                        aria-label="Поиск участника дела"
                        value={participantSearch}
                        onChange={(event) => setParticipantSearch(event.target.value)}
                        className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                        placeholder="ФИО, название, телефон или email"
                      />
                    </label>
                    <label className="block">
                      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Участник</span>
                      <SystemSelect
                        aria-label="Участник дела"
                        value={participantSourceId}
                        onChange={(event) => setParticipantSourceId(event.target.value)}
                        disabled={participantDirectoryLoading || participantOptions.length === 0}
                        className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:bg-slate-50 disabled:text-slate-400">
                        <option value="">
                          {participantDirectoryLoading
                            ? 'Загрузка справочника...'
                            : participantOptions.length === 0
                              ? 'Нет доступных записей'
                              : 'Выберите из справочника'}
                        </option>
                        {participantOptions.map((option) => (
                          <option key={option.key} value={option.sourceId}>
                            {option.description ? `${option.label} · ${option.description}` : option.label}
                          </option>
                        ))}
                      </SystemSelect>
                    </label>
                  </div>
                  <label className="text-sm font-medium text-slate-700">
                    <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Роль</span>
                    <input
                      aria-label="Роль участника"
                      value={participantRole}
                      onChange={(event) => setParticipantRole(event.target.value)}
                      className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                      placeholder="Например: должник"
                    />
                  </label>
                  <button
                    type="button"
                    onClick={() => void addParticipant()}
                    disabled={addingParticipant}
                    className="inline-flex items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50 md:self-end">
                    {addingParticipant ? <Loader2 className="h-4 w-4 animate-spin" /> : <UserRound className="h-4 w-4" />} Добавить
                  </button>
                  <label className="text-sm font-medium text-slate-700 md:col-span-3">
                    <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Основание связи</span>
                    <input
                      aria-label="Основание связи участника"
                      value={participantNotes}
                      onChange={(event) => setParticipantNotes(event.target.value)}
                      className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                      placeholder="Например: доверенное лицо, представитель по доверенности, контрагент"
                    />
                  </label>
                </div>
                {participantDirectoryError ? (
                  <div className="mt-3 rounded-lg border border-rose-100 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                    {participantDirectoryError}
                  </div>
                ) : null}
              </section>
              {participantsLoading ? (
                <LoadingBlock label="Загрузка участников дела..." />
              ) : (
                <RelatedParties
                  parties={relatedParties}
                  canRevealContacts={canRevealCaseSensitiveData}
                  showArchived={showArchivedParties}
                  onToggleArchived={() => setShowArchivedParties((value) => !value)}
                  onOpenClient={(clientId) => onOpenClient?.(clientId, caseItem.id)}
                  onOpenLegalEntity={(legalEntityId) => onOpenLegalEntity?.(legalEntityId, caseItem.id)}
                  onDelete={(party) => void deleteParticipant(party)}
                  onRestore={(party) => void restoreParticipant(party)}
                  deletingPartyId={deletingParticipantId}
                  restoringPartyId={restoringParticipantId}
                />
              )}
            </div>
          ) : null}

          {activeTab === 'tasks' ? (
            tasksLoading ? (
              <LoadingBlock label="Загрузка задач по делу..." />
            ) : (
              <TaskList
                tasks={filteredTasks}
                allTasks={tasks}
                progress={progress}
                filter={taskFilter}
                onFilterChange={setTaskFilter}
                onCreateTask={createTaskForCase}
                stageTaskTemplates={stageTaskTemplates}
                onCreateStageTask={createStageTaskForCase}
                creatingTask={creatingTask}
                canCreateTask={canCreateCaseTask}
              />
            )
          ) : null}

          {activeTab === 'court' ? (
            <CourtPanel
              initialOverview={courtOverview}
              onOverviewChange={setCourtOverview}
              caseItem={caseItem}
              courtDirectory={courtDirectory}
              courtInstances={courtInstances}
              courtInstancesLoading={courtInstancesLoading}
              courtInstancesError={courtInstancesError}
              activeCourtInstance={activeCourtInstance}
              courtInstanceDraft={courtInstanceDraft}
              courtComplaintDraft={courtComplaintDraft}
              editingCourtComplaint={editingCourtComplaint}
              courtEvents={courtEvents}
              courtDirectoryLoading={courtDirectoryLoading}
              courtEventsLoading={courtEventsLoading}
              courtEventsError={courtEventsError}
              documents={documents}
              courtParties={courtParties}
              courtPartiesLoading={courtPartiesLoading}
              courtEventTypeDraft={courtEventTypeDraft}
              courtEventTitleDraft={courtEventTitleDraft}
              courtEventScheduledAtDraft={courtEventScheduledAtDraft}
              courtEventStatusDraft={courtEventStatusDraft}
              courtEventCourtIdDraft={courtEventCourtIdDraft}
              courtEventResultDraft={courtEventResultDraft}
              courtEventDocumentIdDraft={courtEventDocumentIdDraft}
              courtPartyNameDraft={courtPartyNameDraft}
              courtPartySideDraft={courtPartySideDraft}
              courtPartyRoleDraft={courtPartyRoleDraft}
              courtPartyRepresentativeDraft={courtPartyRepresentativeDraft}
              courtPartyNotesDraft={courtPartyNotesDraft}
              proceduralDeadlines={proceduralDeadlines}
              proceduralDeadlinesLoading={proceduralDeadlinesLoading}
              proceduralDeadlinesError={proceduralDeadlinesError}
              deadlineTypeDraft={deadlineTypeDraft}
              deadlineTitleDraft={deadlineTitleDraft}
              deadlineDueAtDraft={deadlineDueAtDraft}
              deadlineWarningDaysDraft={deadlineWarningDaysDraft}
              deadlineDocumentIdDraft={deadlineDocumentIdDraft}
              deadlineCourtEventIdDraft={deadlineCourtEventIdDraft}
              deadlineNotesDraft={deadlineNotesDraft}
              deadlineCalculationPreview={deadlineCalculationPreview}
              logs={courtSyncLogs}
              logsLoading={courtSyncLogsLoading}
              courtSyncSearchResults={courtSyncSearchResults}
              lastCourtSyncResult={lastCourtSyncResult}
              savingCourtInstance={savingCourtInstance}
              savingCourtComplaint={savingCourtComplaint}
              archivingCourtComplaintId={archivingCourtComplaintId}
              savingCourtEvent={savingCourtEvent}
              savingCourtParty={savingCourtParty}
              archivingCourtPartyId={archivingCourtPartyId}
              savingProceduralDeadline={savingProceduralDeadline}
              calculatingProceduralDeadline={calculatingProceduralDeadline}
              closingProceduralDeadlineId={closingProceduralDeadlineId}
              onRunCourtSync={runCourtSync}
              onRecordManualCourtCheck={recordManualCourtCheck}
              syncingCourt={syncingCourt}
              searchingCourtSync={searchingCourtSync}
              bindingCourtSyncSource={bindingCourtSyncSource}
              onActiveCourtInstanceChange={setActiveCourtInstance}
              onCourtInstanceDraftChange={setCourtInstanceDraft}
              onSearchCourtSyncCases={() => void searchCourtSyncCases()}
              onBindCourtSyncCase={(result) => void bindCourtSyncCase(result)}
              onCourtComplaintDraftChange={setCourtComplaintDraft}
              onSaveCourtInstance={() => void saveCourtInstance()}
              onSaveCourtComplaint={() => void saveCourtComplaint()}
              onEditCourtComplaint={startEditCourtComplaint}
              onCancelCourtComplaintEdit={() => {
                setEditingCourtComplaint(null);
                setCourtComplaintDraft(buildCourtComplaintDraft(activeCourtInstance));
              }}
              onArchiveCourtComplaint={(complaint) => void archiveCourtComplaint(complaint)}
              onCourtEventTypeChange={setCourtEventTypeDraft}
              onCourtEventTitleChange={setCourtEventTitleDraft}
              onCourtEventScheduledAtChange={setCourtEventScheduledAtDraft}
              onCourtEventStatusChange={setCourtEventStatusDraft}
              onCourtEventCourtIdChange={setCourtEventCourtIdDraft}
              onCourtEventResultChange={setCourtEventResultDraft}
              onCourtEventDocumentIdChange={setCourtEventDocumentIdDraft}
              onAddCourtEvent={() => void addCourtEvent()}
              onCourtPartyNameChange={setCourtPartyNameDraft}
              onCourtPartySideChange={setCourtPartySideDraft}
              onCourtPartyRoleChange={setCourtPartyRoleDraft}
              onCourtPartyRepresentativeChange={setCourtPartyRepresentativeDraft}
              onCourtPartyNotesChange={setCourtPartyNotesDraft}
              onAddCourtParty={() => void addCourtParty()}
              onArchiveCourtParty={(party) => void archiveCourtParty(party)}
              onDeadlineTypeChange={setDeadlineTypeDraft}
              onDeadlineTitleChange={setDeadlineTitleDraft}
              onDeadlineDueAtChange={setDeadlineDueAtDraft}
              onDeadlineWarningDaysChange={setDeadlineWarningDaysDraft}
              onDeadlineDocumentIdChange={setDeadlineDocumentIdDraft}
              onDeadlineCourtEventIdChange={setDeadlineCourtEventIdDraft}
              onDeadlineNotesChange={setDeadlineNotesDraft}
              onCalculateProceduralDeadline={() => void calculateProceduralDeadline()}
              onAddProceduralDeadline={() => void addProceduralDeadline()}
              onCloseProceduralDeadline={(deadline) => void closeProceduralDeadline(deadline)}
            />
          ) : null}

          {canViewFinance && activeTab === 'finance' ? (
            <FinancePanel
              caseItem={caseItem}
              detail={financeDetail}
              loading={financeLoading}
              onOpenFinance={onOpenFinance}
              onCreateInvoice={onCreateInvoice}
              onCreatePayment={onCreatePayment}
            />
          ) : null}

          {activeTab === 'workOrder' ? (
            <WorkOrderPanel
              workOrder={workOrder}
              draft={workOrderDraft}
              itemDrafts={workOrderItemDrafts}
              newItemDraft={newWorkOrderItemDraft}
              responsibles={responsibles}
              loading={workOrderLoading}
              savingHeader={savingWorkOrder}
              deletingHeader={deletingWorkOrder}
              savingItem={savingWorkOrderItem}
              deletingItemId={deletingWorkOrderItemId}
              creatingInvoice={creatingWorkOrderInvoice}
              creatingAct={creatingWorkOrderAct}
              creatingContract={creatingWorkOrderContract}
              printing={printingWorkOrder}
              generatingDocument={generatingWorkOrderDocument}
              approvalAction={workOrderApprovalAction}
              approvalComment={workOrderApprovalComment}
              externalApprovalAvailable={externalApprovalAvailable}
              externalApprovalStatus={externalApprovalStatus}
              externalApprovalRecipientName={externalApprovalRecipientName}
              externalApprovalRecipientEmail={externalApprovalRecipientEmail}
              externalApprovalRecipientType={externalApprovalRecipientType}
              externalApprovalDeliveryChannel={externalApprovalDeliveryChannel}
              externalApprovalSaving={externalApprovalSaving}
              documents={documents}
              externalApprovalDocumentIds={externalApprovalDocumentIds}
              onDraftChange={setWorkOrderDraft}
              onItemDraftChange={(itemId, draft) => setWorkOrderItemDrafts((current) => ({ ...current, [itemId]: draft }))}
              onNewItemDraftChange={setNewWorkOrderItemDraft}
              onApprovalCommentChange={setWorkOrderApprovalComment}
              onExternalApprovalRecipientNameChange={setExternalApprovalRecipientName}
              onExternalApprovalRecipientEmailChange={setExternalApprovalRecipientEmail}
              onExternalApprovalRecipientTypeChange={setExternalApprovalRecipientType}
              onExternalApprovalDeliveryChannelChange={setExternalApprovalDeliveryChannel}
              onExternalApprovalDocumentIdsChange={setExternalApprovalDocumentIds}
              onSendExternalApproval={() => void sendExternalWorkOrderApproval()}
              onRevokeExternalApproval={() => void revokeExternalWorkOrderApproval()}
              onSaveHeader={() => void saveWorkOrderHeader()}
              onDeleteHeader={() => void deleteWorkOrder()}
              onCreateInvoice={canViewFinance ? () => void createInvoiceFromWorkOrder() : undefined}
              onCreateAct={canViewFinance ? () => void createActFromWorkOrder() : undefined}
              onCreateContract={canViewFinance ? () => void createContractFromWorkOrder() : undefined}
              onPrint={() => void printWorkOrder()}
              onGenerateDocument={() => void generateWorkOrderDocument()}
              onSubmitApproval={() => void runWorkOrderApprovalAction('submit')}
              onApprove={() => void runWorkOrderApprovalAction('approve')}
              onRequestRevision={() => void runWorkOrderApprovalAction('revision')}
              onAddItem={() => void addWorkOrderItem()}
              onUpdateItem={(item) => void updateWorkOrderItem(item)}
              onDeleteItem={(item) => void deleteWorkOrderItem(item)}
            />
          ) : null}

          {activeTab === 'documents' ? (
            <div className="space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 p-4">
                <div>
                  <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Документы дела</h2>
                  <div className="mt-1 text-sm text-slate-500">Версии и шаблоны хранятся в истории документов дела.</div>
                </div>
                {canManageDocuments ? (
                  <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
                    <label className="text-sm font-medium text-slate-700">
                      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Шаблон</span>
                      <SystemSelect
                        aria-label="Шаблон документа дела"
                        value={documentTemplateKey}
                        onChange={(event) => setDocumentTemplateKey(event.target.value)}
                        className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 sm:w-56">
                        {availableDocumentTemplates.map((template) => (
                          <option key={template.key} value={template.key}>{template.label}</option>
                        ))}
                      </SystemSelect>
                    </label>
                    <button type="button" onClick={() => void generateDocument()} disabled={generatingDocument || availableDocumentTemplates.length === 0} className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
                      {generatingDocument ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} Сформировать
                    </button>
                  </div>
                ) : (
                  <div className="max-w-md rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                    Документы доступны только для просмотра. Загрузка, новые версии, генерация, изменение и удаление требуют права на редактирование дела.
                  </div>
                )}
              </div>
              {canManageDocuments ? (
                <section className="rounded-xl border border-slate-200 p-4">
                  <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Загрузить файл</h2>
                  <datalist id={documentTypeListId}>
                    {documentTypeOptions.map((type) => (
                      <option key={type} value={type} />
                    ))}
                  </datalist>
                  <div className="mt-4 grid gap-3 lg:grid-cols-2 2xl:grid-cols-[1fr_180px_180px_220px_220px_auto]">
                    <label className="text-sm font-medium text-slate-700">
                      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Файл</span>
                      <input
                        aria-label="Файл документа"
                        type="file"
                        onChange={(event) => setDocumentFile(event.target.files?.[0] ?? null)}
                        className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm file:mr-3 file:rounded-md file:border-0 file:bg-blue-50 file:px-3 file:py-1.5 file:text-sm file:font-semibold file:text-blue-700"
                      />
                    </label>
                    <label className="text-sm font-medium text-slate-700">
                      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Тип</span>
                      <input
                        aria-label="Тип документа"
                        list={documentTypeListId}
                        value={documentTypeDraft}
                        onChange={(event) => setDocumentTypeDraft(event.target.value)}
                        className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                        placeholder="Иск"
                      />
                    </label>
                    <label className="text-sm font-medium text-slate-700">
                      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
                      <SystemSelect
                        aria-label="Статус документа"
                        value={documentStatusDraft}
                        onChange={(event) => setDocumentStatusDraft(event.target.value)}
                        className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                      >
                        <option value="">По умолчанию: Подготовлен</option>
                        {documentStatusOptions.map((status) => (
                          <option key={status} value={status}>{status}</option>
                        ))}
                      </SystemSelect>
                    </label>
                    <label className="text-sm font-medium text-slate-700">
                      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Источник</span>
                      <SystemSelect
                        aria-label="Источник документа"
                        value={documentSourceType}
                        onChange={(event) => {
                          setDocumentSourceType(event.target.value as DocumentSourceType);
                          setDocumentSourceId('');
                        }}
                        className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                      >
                        <option value="case">Только дело</option>
                        {canViewTasks ? <option value="task">Задача</option> : null}
                        <option value="courtEvent">Судебное событие</option>
                        <option value="workOrder">Заказ-наряд</option>
                        <option value="invoice">Счет</option>
                        <option value="stage">Этап дела</option>
                      </SystemSelect>
                    </label>
                    {documentSourceType === 'stage' ? (
                      <label className="text-sm font-medium text-slate-700">
                        <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Этап</span>
                        <input
                          aria-label="Этап документа"
                          value={documentSourceStage}
                          onChange={(event) => setDocumentSourceStage(event.target.value)}
                          className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                          placeholder={caseItem.stage || 'pretrial'}
                        />
                      </label>
                    ) : (
                      <label className="text-sm font-medium text-slate-700">
                        <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Объект</span>
                        <SystemSelect
                          aria-label="Объект документа"
                          value={documentSourceId}
                          onChange={(event) => setDocumentSourceId(event.target.value)}
                          disabled={documentSourceType === 'case' || documentSourceType === 'workOrder' || documentSourceOptions.length === 0}
                          className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:bg-slate-50 disabled:text-slate-400"
                        >
                          <option value="">{documentSourceType === 'case' ? 'Не требуется' : 'Выберите объект'}</option>
                          {documentSourceOptions.map((option) => (
                            <option key={option.value} value={option.value}>{option.label}</option>
                          ))}
                        </SystemSelect>
                      </label>
                    )}
                    <button
                      type="button"
                      onClick={() => void uploadDocument()}
                      disabled={uploadingDocument}
                      className="inline-flex items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50 md:self-end">
                      {uploadingDocument ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} Загрузить
                    </button>
                  </div>
                </section>
              ) : null}
              {documentsLoading ? (
                <LoadingBlock label="Загрузка документов дела..." />
              ) : (
                <DocumentList
                  documents={documents}
                  clientId={caseItem.clientId ?? null}
                  downloadingDocumentId={downloadingDocumentId}
                  previewingDocumentId={previewingDocumentId}
                  sendingYandexDocumentId={sendingYandexDocumentId}
                  uploadingDocumentVersionId={uploadingDocumentVersionId}
                  updatingDocumentId={updatingDocumentId}
                  deletingDocumentId={deletingDocumentId}
                  documentTypeOptions={documentTypeOptions}
                  documentStatusOptions={documentStatusOptions}
                  canEdit={canManageDocuments}
                  canDelete={canDeleteDocuments}
                  canSendMessenger={canSendMessenger}
                  onPreview={(caseDocument) => void previewDocument(caseDocument)}
                  onDownload={(caseDocument) => void downloadDocument(caseDocument)}
                  onSendToYandexDisk={(caseDocument) => void sendDocumentToYandexDisk(caseDocument)}
                  onUploadVersion={(caseDocument, file) => void uploadDocumentVersion(caseDocument, file)}
                  onUpdate={(caseDocument, payload) => void updateDocument(caseDocument, payload)}
                  onDelete={(caseDocument) => void deleteDocument(caseDocument)}
                />
              )}
            </div>
          ) : null}

          {activeTab === 'conversation' ? (
            <div className="space-y-4">
              {canEdit ? (
                <section className="rounded-xl border border-slate-200 p-4">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                      <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Внутренняя переписка</h2>
                      <p className="mt-1 text-sm text-slate-500">Сообщения видны в контексте этого дела и не смешиваются с общим центром уведомлений.</p>
                    </div>
                    <span className="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1 text-sm font-medium text-slate-600">
                      <MessageSquare className="h-4 w-4" /> {conversation.length}
                    </span>
                  </div>
                  <textarea
                    aria-label="Сообщение во внутреннюю переписку дела"
                    value={conversationDraft}
                    onChange={(event) => setConversationDraft(event.target.value)}
                    maxLength={2000}
                    rows={4}
                    className="mt-4 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                    placeholder="Напишите короткое рабочее сообщение по делу для коллег."
                  />
                  {conversationFiles.length > 0 ? (
                    <div className="mt-2 flex flex-wrap gap-2">
                      {conversationFiles.map((file) => (
                        <span key={`${file.name}-${file.size}-${file.lastModified}`} className="inline-flex max-w-full items-center gap-2 rounded-lg bg-slate-100 px-2.5 py-1.5 text-xs font-medium text-slate-600">
                          <FileText className="h-3.5 w-3.5 shrink-0" />
                          <span className="truncate">{file.name}</span>
                          <span className="shrink-0 text-slate-400">{formatFileSize(file.size)}</span>
                        </span>
                      ))}
                    </div>
                  ) : null}
                  <div className="mt-3 flex flex-wrap items-center gap-2">
                    <input
                      id="case-conversation-files"
                      aria-label="Файлы к сообщению"
                      type="file"
                      multiple
                      className="sr-only"
                      onChange={(event) => setConversationFiles(Array.from(event.target.files ?? []))}
                    />
                    <label
                      htmlFor="case-conversation-files"
                      className="inline-flex cursor-pointer items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50">
                      <Paperclip className="h-4 w-4" /> Файлы
                    </label>
                    {conversationFiles.length > 0 ? (
                      <button
                        type="button"
                        onClick={() => setConversationFiles([])}
                        className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-semibold text-slate-500 hover:bg-slate-50">
                        <X className="h-4 w-4" /> Очистить
                      </button>
                    ) : null}
                  </div>
                  <div className="mt-3 flex items-center justify-between gap-3">
                    <span className="text-xs text-slate-400">{conversationDraft.length}/2000</span>
                    <button
                      type="button"
                      onClick={() => void addConversationEntry()}
                      disabled={addingConversationEntry || !conversationDraft.trim()}
                      className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
                      {addingConversationEntry ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} Отправить
                    </button>
                  </div>
                </section>
              ) : (
                <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">Переписка доступна только для просмотра.</div>
              )}
              {conversationLoading ? (
                <LoadingBlock label="Загрузка переписки по делу..." />
              ) : conversation.length === 0 ? (
                <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">Внутренней переписки по делу пока нет.</div>
              ) : (
                <ConversationTimeline entries={conversation} downloadingDocumentId={downloadingDocumentId} onDownload={(attachment) => void downloadConversationAttachment(attachment)} />
              )}
            </div>
          ) : null}

          {activeTab === 'history' ? (
            <div className="space-y-3">
              <section className="rounded-xl border border-slate-200 p-4">
                <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Комментарий к делу</h2>
                <textarea
                  aria-label="Комментарий по истории дела"
                  value={historyDraft}
                  onChange={(event) => setHistoryDraft(event.target.value)}
                  maxLength={2000}
                  rows={4}
                  className="mt-3 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                  placeholder="Зафиксируйте решение, звонок, внутреннее пояснение или договоренность."
                />
                <div className="mt-3 flex items-center justify-between gap-3">
                  <span className="text-xs text-slate-400">{historyDraft.length}/2000</span>
                  <button
                    type="button"
                    onClick={() => void addHistoryEntry()}
                    disabled={addingHistoryEntry || !historyDraft.trim()}
                    className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50">
                    {addingHistoryEntry ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Добавить комментарий
                  </button>
                </div>
              </section>
              {timelineLoading ? (
                <LoadingBlock label="Загрузка истории дела..." />
              ) : timeline.length === 0 ? (
                <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">История по делу пока не заполнена.</div>
              ) : (
                <HistoryTimeline entries={timeline} />
              )}
            </div>
          ) : null}
            </div>
          </div>
        </div>
      </div>
      {documentPreview ? (
        <DocumentPreviewModal
          preview={documentPreview}
          onClose={() => setDocumentPreview(null)}
          onDownload={() => void downloadDocument(documentPreview.document)}
        />
      ) : null}
    </div>
  );
}

function CaseEditPage({
  caseItem,
  draft,
  responsibles,
  saving,
  error,
  canViewFinance,
  onBack,
  onChange,
  onSave,
}: {
  caseItem: ClientCase;
  draft: CaseEditDraft;
  responsibles: ActResponsible[];
  saving: boolean;
  error: string | null;
  canViewFinance: boolean;
  onBack: () => void;
  onChange: (draft: CaseEditDraft) => void;
  onSave: () => void;
}) {
  const setField = <K extends keyof CaseEditDraft>(key: K, value: CaseEditDraft[K]) => {
    onChange({ ...draft, [key]: value });
  };

  return (
    <div className="min-h-screen bg-slate-50 px-4 py-6 sm:px-6 lg:px-8">
      <div className="w-full max-w-none space-y-5">
        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
            <div className="min-w-0">
              <button type="button" onClick={onBack} className="mb-4 inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <ArrowLeft className="h-4 w-4" /> Карточка дела
              </button>
              <div className="flex flex-wrap items-center gap-2">
                <span className="inline-flex items-center gap-2 rounded-full bg-blue-50 px-3 py-1 text-sm font-semibold text-blue-700">
                  <Briefcase className="h-4 w-4" /> {caseItem.caseNumber || `#${caseItem.id}`}
                </span>
                <span className="rounded-full bg-slate-100 px-3 py-1 text-sm font-medium text-slate-700">Редактирование</span>
              </div>
              <h1 className="mt-3 text-3xl font-bold text-slate-900">Редактирование дела</h1>
              <p className="mt-2 max-w-3xl text-sm text-slate-600">{caseItem.title || 'Без названия'}</p>
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={onBack}
                disabled={saving}
                className="inline-flex h-10 items-center justify-center gap-2 rounded-lg border border-slate-200 px-4 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50">
                <X className="h-4 w-4" /> Отмена
              </button>
              <button
                type="button"
                onClick={onSave}
                disabled={saving}
                className="inline-flex h-10 items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Сохранить дело
              </button>
            </div>
          </div>
        </div>

        {error ? <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div> : null}

        <div className="grid gap-5 xl:grid-cols-[minmax(0,1.15fr)_minmax(360px,0.85fr)]">
          <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
            <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Основные данные</h2>
            <div className="mt-4 grid gap-4 md:grid-cols-2">
              <label className="md:col-span-2 text-sm font-medium text-slate-700">
                Название дела <span className="text-rose-500">*</span>
                <input
                  aria-label="Название дела"
                  value={draft.title}
                  onChange={(event) => setField('title', event.target.value)}
                  className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                />
              </label>
              <label className="md:col-span-2 text-sm font-medium text-slate-700">
                Описание
                <textarea
                  aria-label="Описание дела"
                  value={draft.description}
                  onChange={(event) => setField('description', event.target.value)}
                  rows={3}
                  className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                />
              </label>
              <label className="text-sm font-medium text-slate-700">
                Номер дела
                <input value={draft.caseNumber} onChange={(event) => setField('caseNumber', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                Тип дела
                <input value={draft.caseType} onChange={(event) => setField('caseType', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                Статус
                <SystemSelect aria-label="Статус дела" value={draft.status} onChange={(event) => setField('status', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm">
                  {CASE_STATUS_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                </SystemSelect>
              </label>
              <label className="text-sm font-medium text-slate-700">
                Приоритет
                <SystemSelect aria-label="Приоритет дела" value={draft.priority} onChange={(event) => setField('priority', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm">
                  {CASE_PRIORITY_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                </SystemSelect>
              </label>
              <label className="md:col-span-2 text-sm font-medium text-slate-700">
                Стадия
                <input value={draft.stage} onChange={(event) => setField('stage', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" placeholder="Например: судебное производство" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                Ответственный
                <SystemSelect aria-label="Ответственный" value={draft.responsibleUserId} onChange={(event) => setField('responsibleUserId', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm">
                  <option value="">Не выбран</option>
                  {responsibles.map((responsible) => <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>)}
                </SystemSelect>
              </label>
              {canViewFinance ? (
                <label className="text-sm font-medium text-slate-700">
                  Сумма гонорара
                  <input aria-label="Сумма гонорара" type="number" value={draft.feeAmount} onChange={(event) => setField('feeAmount', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
                </label>
              ) : null}
            </div>
          </section>

          <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
            <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Сроки и доступ</h2>
            <div className="mt-4 grid gap-4 md:grid-cols-2 xl:grid-cols-1">
              <label className="text-sm font-medium text-slate-700">
                Дата начала
                <SystemDateInput type="date" value={draft.startDate} onChange={(event) => setField('startDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                Срок
                <SystemDateInput type="date" value={draft.dueDate} onChange={(event) => setField('dueDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
              </label>
              <label className="flex min-h-[58px] items-center gap-3 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700">
                <SystemCheckbox type="checkbox" checked={draft.isHiddenFromExecutor} onChange={(event) => setField('isHiddenFromExecutor', event.target.checked)} className="h-4 w-4 rounded border-slate-300 text-blue-600" />
                Скрыть от исполнителя
              </label>
              <label className="flex min-h-[58px] items-center gap-3 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700">
                <SystemCheckbox
                  type="checkbox"
                  checked={draft.inactivityRemindersEnabled}
                  onChange={(event) => {
                    setField('inactivityRemindersEnabled', event.target.checked);
                    if (event.target.checked) setField('inactivityRemindersDisabledReason', '');
                  }}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600"
                />
                Автоконтроль включен
              </label>
              {!draft.inactivityRemindersEnabled ? (
                <label className="text-sm font-medium text-slate-700">
                  Причина выключения автоконтроля
                  <textarea
                    required
                    aria-label="Причина выключения автоконтроля"
                    value={draft.inactivityRemindersDisabledReason}
                    onChange={(event) => setField('inactivityRemindersDisabledReason', event.target.value)}
                    className="mt-1 min-h-20 w-full rounded-lg border border-amber-200 px-3 py-2 text-sm"
                  />
                </label>
              ) : null}
            </div>
          </section>
        </div>

        <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Суд и участники процесса</h2>
          <div className="mt-4 grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            <label className="text-sm font-medium text-slate-700">
              Суд
              <input value={draft.courtName} onChange={(event) => setField('courtName', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              № в суде
              <input value={draft.courtCaseNumber} onChange={(event) => setField('courtCaseNumber', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Заседание
              <SystemDateInput type="datetime-local" value={draft.nextHearingAt} onChange={(event) => setField('nextHearingAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="md:col-span-2 xl:col-span-3 text-sm font-medium text-slate-700">
              Ссылка на источник
              <input value={draft.courtUrl} onChange={(event) => setField('courtUrl', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Истец
              <input value={draft.claimant} onChange={(event) => setField('claimant', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Ответчик
              <input value={draft.defendant} onChange={(event) => setField('defendant', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Судья
              <input value={draft.judgeName} onChange={(event) => setField('judgeName', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Третьи лица
              <input value={draft.thirdParties} onChange={(event) => setField('thirdParties', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Представители
              <input value={draft.representatives} onChange={(event) => setField('representatives', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Зал суда
              <input value={draft.courtroom} onChange={(event) => setField('courtroom', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
            <label className="md:col-span-2 xl:col-span-3 text-sm font-medium text-slate-700">
              Контакты секретаря
              <input value={draft.courtSecretaryContacts} onChange={(event) => setField('courtSecretaryContacts', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" />
            </label>
          </div>
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Процессуальные даты</h2>
          <div className="mt-4 grid gap-4 md:grid-cols-2 xl:grid-cols-5">
            <label className="text-sm font-medium text-slate-700">Иск подан<SystemDateInput type="date" value={draft.claimFiledAt} onChange={(event) => setField('claimFiledAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /></label>
            <label className="text-sm font-medium text-slate-700">Решение суда<SystemDateInput type="date" value={draft.courtDecisionAt} onChange={(event) => setField('courtDecisionAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /></label>
            <label className="text-sm font-medium text-slate-700">Апелляция<SystemDateInput type="date" value={draft.appealDeadlineAt} onChange={(event) => setField('appealDeadlineAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /></label>
            <label className="text-sm font-medium text-slate-700">Кассация<SystemDateInput type="date" value={draft.cassationDeadlineAt} onChange={(event) => setField('cassationDeadlineAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /></label>
            <label className="text-sm font-medium text-slate-700">Возражения<SystemDateInput type="date" value={draft.objectionDeadlineAt} onChange={(event) => setField('objectionDeadlineAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /></label>
          </div>
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Рабочие материалы</h2>
          <div className="mt-4 grid gap-4 lg:grid-cols-3">
            <label className="text-sm font-medium text-slate-700">
              Стратегия и план действий
              <textarea value={draft.strategy} onChange={(event) => setField('strategy', event.target.value)} rows={6} className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Документы
              <textarea value={draft.documentsSummary} onChange={(event) => setField('documentsSummary', event.target.value)} rows={6} className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              История событий
              <textarea value={draft.eventHistory} onChange={(event) => setField('eventHistory', event.target.value)} rows={6} className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
          </div>
        </section>

        <div className="sticky bottom-0 -mx-4 border-t border-slate-200 bg-white/95 px-4 py-3 shadow-[0_-8px_20px_rgba(15,23,42,0.08)] backdrop-blur sm:-mx-6 sm:px-6 lg:-mx-8 lg:px-8">
          <div className="flex justify-end gap-2">
            <button type="button" onClick={onBack} disabled={saving} className="h-10 rounded-lg border border-slate-200 px-4 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50">
              Отмена
            </button>
            <button type="button" onClick={onSave} disabled={saving} className="inline-flex h-10 items-center gap-2 rounded-lg bg-blue-600 px-4 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Сохранить дело
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function getTimelineEntryMeta(entry: ClientCaseTimelineEntry): { label: string; className: string } {
  const text = entry.text.toLowerCase();
  if (text.includes('комментар')) {
    return { label: 'Комментарий', className: 'bg-blue-50 text-blue-700' };
  }
  if (text.includes('статус') || text.includes('массовое изменение') || text.includes('измен')) {
    return { label: 'Изменение', className: 'bg-amber-50 text-amber-700' };
  }
  if (text.includes('суд') || text.includes('проверка судеб')) {
    return { label: 'Суд', className: 'bg-cyan-50 text-cyan-700' };
  }
  if (text.includes('документ') || text.includes('файл')) {
    return { label: 'Документ', className: 'bg-violet-50 text-violet-700' };
  }
  return { label: 'Событие', className: 'bg-slate-100 text-slate-700' };
}

function HistoryTimeline({ entries }: { entries: ClientCaseTimelineEntry[] }) {
  return (
    <section className="rounded-xl border border-slate-200 p-4">
      <div className="mb-4 flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Хронология дела</h2>
        <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-500">
          {formatRussianCount(entries.length, ['запись', 'записи', 'записей'])}
        </span>
      </div>
      <div className="space-y-0">
        {entries.map((entry, index) => {
          const meta = getTimelineEntryMeta(entry);
          return (
            <div key={entry.id} className="grid grid-cols-[28px_1fr] gap-3">
              <div className="relative flex justify-center">
                <span className="mt-1 h-3 w-3 rounded-full border-2 border-white bg-blue-500 shadow ring-2 ring-blue-100" />
                {index < entries.length - 1 ? <span className="absolute top-5 bottom-0 w-px bg-slate-200" /> : null}
              </div>
              <article className="pb-4">
                <div className="rounded-lg border border-slate-100 bg-white p-3 shadow-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${meta.className}`}>{meta.label}</span>
                    {entry.occurredAt ? <span className="text-xs text-slate-400">{formatRuDateTime(entry.occurredAt)}</span> : null}
                  </div>
                  <p className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-700">{entry.text}</p>
                </div>
              </article>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function ConversationTimeline({
  entries,
  downloadingDocumentId,
  onDownload,
}: {
  entries: ClientCaseConversationEntry[];
  downloadingDocumentId?: number | null;
  onDownload: (attachment: ClientCaseConversationAttachment) => void;
}) {
  return (
    <section className="rounded-xl border border-slate-200 p-4">
      <div className="mb-4 flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Лента переписки</h2>
        <span className="rounded-full bg-blue-50 px-2.5 py-1 text-xs font-semibold text-blue-700">
          {formatRussianCount(entries.length, ['сообщение', 'сообщения', 'сообщений'])}
        </span>
      </div>
      <div className="space-y-3">
        {entries.map((entry) => (
          <article key={entry.id} className="rounded-xl border border-slate-100 bg-white p-4 shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div className="flex flex-wrap items-center gap-2">
                <span className="inline-flex h-8 w-8 items-center justify-center rounded-full bg-blue-50 text-blue-700">
                  <MessageSquare className="h-4 w-4" />
                </span>
                <div>
                  <div className="text-sm font-semibold text-slate-900">{entry.authorName || 'Сотрудник'}</div>
                  {entry.occurredAt ? <div className="text-xs text-slate-400">{formatRuDateTime(entry.occurredAt)}</div> : null}
                </div>
              </div>
              <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${entry.kind === 'comment' ? 'bg-slate-100 text-slate-600' : 'bg-blue-50 text-blue-700'}`}>
                {entry.kind === 'comment' ? 'Комментарий' : 'Сообщение'}
              </span>
            </div>
            <p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-slate-700">{entry.text}</p>
            {entry.attachments?.length ? (
              <div className="mt-3 flex flex-wrap gap-2">
                {entry.attachments.map((attachment) => (
                  <button
                    key={attachment.documentId}
                    type="button"
                    onClick={() => onDownload(attachment)}
                    className="inline-flex max-w-full items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-left text-xs font-semibold text-slate-700 hover:border-blue-200 hover:bg-blue-50 hover:text-blue-700"
                    aria-label={`Скачать вложение ${attachment.fileName}`}>
                    <FileText className="h-4 w-4 shrink-0" />
                    <span className="min-w-0 truncate">{attachment.fileName || `Документ #${attachment.documentId}`}</span>
                    <span className="shrink-0 text-slate-400">{formatFileSize(attachment.sizeBytes)}</span>
                    {downloadingDocumentId === attachment.documentId ? (
                      <Loader2 className="h-4 w-4 shrink-0 animate-spin" />
                    ) : (
                      <Download className="h-4 w-4 shrink-0" />
                    )}
                  </button>
                ))}
              </div>
            ) : null}
          </article>
        ))}
      </div>
    </section>
  );
}

function CaseClientSidebar({
  caseItem,
  canRevealSensitive,
  onOpenClient,
  onOpenLegalEntity,
  onOpenFinance,
  onCreateInvoice,
  onCreatePayment,
}: {
  caseItem: ClientCase;
  canRevealSensitive: boolean;
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onOpenLegalEntity?: (legalEntityId: number, caseId?: number) => void;
  onOpenFinance?: (caseItem: ClientCase) => void;
  onCreateInvoice?: (caseItem: ClientCase) => void;
  onCreatePayment?: (caseItem: ClientCase) => void;
}) {
  const [showSensitive, setShowSensitive] = useState(false);
  const phone = getOwnerPhone(caseItem);
  const email = getOwnerEmail(caseItem);
  const inn = getOwnerInn(caseItem);
  const address = getOwnerAddress(caseItem);
  const hasSensitive = Boolean(phone || email || inn || address);
  const sensitiveVisible = canRevealSensitive && showSensitive;
  const legalEntityId = caseItem.legalEntityId ?? caseItem.client?.legalEntityId ?? caseItem.client?.legalEntity?.id ?? null;

  return (
    <aside className="self-start rounded-xl border border-slate-200 bg-slate-50 p-4 xl:sticky xl:top-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">Клиент по делу</div>
          <h2 className="mt-2 break-words text-lg font-semibold text-slate-900">{getOwnerName(caseItem)}</h2>
          <div className="mt-2 inline-flex rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-semibold text-emerald-700">{getOwnerStatus(caseItem)}</div>
        </div>
        {canRevealSensitive && hasSensitive ? (
          <button
            type="button"
            onClick={() => setShowSensitive((value) => !value)}
            className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-600 hover:bg-slate-100">
            {showSensitive ? 'Скрыть' : 'Показать'}
          </button>
        ) : (
          <div className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-500">
            <LockKeyhole className="h-3.5 w-3.5" /> Скрыто по роли
          </div>
        )}
      </div>

      <dl className="mt-4 space-y-3 text-sm">
        <ClientSidebarRow label="Телефон" value={sensitiveVisible ? (phone || '—') : maskSensitiveValue(phone)} />
        <ClientSidebarRow label="Email" value={sensitiveVisible ? (email || '—') : maskSensitiveValue(email, 6)} />
        <ClientSidebarRow label="ИНН" value={sensitiveVisible ? (inn || '—') : maskSensitiveValue(inn)} />
        <ClientSidebarRow label="Адрес" value={sensitiveVisible ? (address || '—') : maskSensitiveValue(address, 0)} />
        <ClientSidebarRow label="Организация" value={getOwnerOrganization(caseItem)} />
        <ClientSidebarRow label="Ответственный" value={getResponsibleName(caseItem)} />
      </dl>

      <div className="mt-4 grid gap-2">
        {phone && sensitiveVisible ? (
          <a href={`tel:${phone}`} className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100">
            <Phone className="h-4 w-4" /> Позвонить
          </a>
        ) : null}
        {email && sensitiveVisible ? (
          <a href={`mailto:${email}`} className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100">
            <Mail className="h-4 w-4" /> Написать email
          </a>
        ) : null}
        {caseItem.clientId && onOpenClient ? (
          <button
            type="button"
            onClick={() => onOpenClient(caseItem.clientId!, caseItem.id)}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50">
            <ExternalLink className="h-4 w-4" /> Карточка клиента
          </button>
        ) : null}
        {legalEntityId && onOpenLegalEntity ? (
          <button
            type="button"
            onClick={() => onOpenLegalEntity(legalEntityId, caseItem.id)}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100">
            <ExternalLink className="h-4 w-4" /> Юрлицо
          </button>
        ) : null}
        <div className="grid grid-cols-2 gap-2">
          {onCreateInvoice ? (
            <button
              type="button"
              onClick={() => onCreateInvoice(caseItem)}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-100">
              <FilePlus2 className="h-4 w-4" /> Счет
            </button>
          ) : null}
          {onCreatePayment ? (
            <button
              type="button"
              onClick={() => onCreatePayment(caseItem)}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-100">
              <WalletCards className="h-4 w-4" /> Платеж
            </button>
          ) : null}
        </div>
        {onOpenFinance ? (
          <button
            type="button"
            onClick={() => onOpenFinance(caseItem)}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100">
            <WalletCards className="h-4 w-4" /> Финансы дела
          </button>
        ) : null}
      </div>
    </aside>
  );
}

function ClientSidebarRow({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{label}</dt>
      <dd className="mt-1 break-words font-medium text-slate-800">{value || '—'}</dd>
    </div>
  );
}

function InfoPill({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg bg-slate-50 px-3 py-2">
      <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{label}</div>
      <div className="mt-1 break-words font-medium text-slate-800">{value}</div>
    </div>
  );
}

function canAttachToExternalWorkOrder(document: ClientCaseDocument) {
  const extension = document.originalFileName.toLocaleLowerCase('ru').match(/\.[^.]+$/)?.[0] ?? '';
  const contentType = document.contentType.toLocaleLowerCase('en-US');
  return [
    ['application/pdf', '.pdf'],
    ['image/jpeg', '.jpg'],
    ['image/jpeg', '.jpeg'],
    ['image/png', '.png'],
    ['text/plain', '.txt'],
    ['application/msword', '.doc'],
    ['application/vnd.openxmlformats-officedocument.wordprocessingml.document', '.docx'],
    ['application/vnd.ms-excel', '.xls'],
    ['application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', '.xlsx'],
  ].some(([type, suffix]) => contentType === type && extension === suffix);
}

function WorkOrderPanel({
  workOrder,
  draft,
  itemDrafts,
  newItemDraft,
  responsibles,
  loading,
  savingHeader,
  deletingHeader,
  savingItem,
  deletingItemId,
  creatingInvoice,
  creatingAct,
  creatingContract,
  printing,
  generatingDocument,
  approvalAction,
  approvalComment,
  externalApprovalAvailable,
  externalApprovalStatus,
  externalApprovalRecipientName,
  externalApprovalRecipientEmail,
  externalApprovalRecipientType,
  externalApprovalDeliveryChannel,
  externalApprovalSaving,
  documents,
  externalApprovalDocumentIds,
  onDraftChange,
  onItemDraftChange,
  onNewItemDraftChange,
  onApprovalCommentChange,
  onExternalApprovalRecipientNameChange,
  onExternalApprovalDocumentIdsChange,
  onExternalApprovalRecipientEmailChange,
  onExternalApprovalRecipientTypeChange,
  onExternalApprovalDeliveryChannelChange,
  onSendExternalApproval,
  onRevokeExternalApproval,
  onSaveHeader,
  onDeleteHeader,
  onCreateInvoice,
  onCreateAct,
  onCreateContract,
  onPrint,
  onGenerateDocument,
  onSubmitApproval,
  onApprove,
  onRequestRevision,
  onAddItem,
  onUpdateItem,
  onDeleteItem,
}: {
  workOrder: ClientCaseWorkOrder | null;
  draft: WorkOrderHeaderDraft;
  itemDrafts: Record<number, WorkOrderItemDraft>;
  newItemDraft: WorkOrderItemDraft;
  responsibles: ActResponsible[];
  loading: boolean;
  savingHeader: boolean;
  deletingHeader: boolean;
  savingItem: boolean;
  deletingItemId: number | null;
  creatingInvoice: boolean;
  creatingAct: boolean;
  creatingContract: boolean;
  printing: boolean;
  generatingDocument: boolean;
  approvalAction: 'submit' | 'approve' | 'revision' | null;
  approvalComment: string;
  externalApprovalAvailable: boolean;
  externalApprovalStatus: ExternalWorkOrderApprovalStatus | null;
  externalApprovalRecipientName: string;
  externalApprovalRecipientEmail: string;
  externalApprovalRecipientType: 'client' | 'representative';
  externalApprovalDeliveryChannel: 'email' | 'email_and_max';
  externalApprovalSaving: boolean;
  documents: ClientCaseDocument[];
  externalApprovalDocumentIds: number[];
  onDraftChange: (draft: WorkOrderHeaderDraft) => void;
  onItemDraftChange: (itemId: number, draft: WorkOrderItemDraft) => void;
  onNewItemDraftChange: (draft: WorkOrderItemDraft) => void;
  onApprovalCommentChange: (value: string) => void;
  onExternalApprovalRecipientNameChange: (value: string) => void;
  onExternalApprovalDocumentIdsChange: (value: number[]) => void;
  onExternalApprovalRecipientEmailChange: (value: string) => void;
  onExternalApprovalRecipientTypeChange: (value: 'client' | 'representative') => void;
  onExternalApprovalDeliveryChannelChange: (value: 'email' | 'email_and_max') => void;
  onSendExternalApproval: () => void;
  onRevokeExternalApproval: () => void;
  onSaveHeader: () => void;
  onDeleteHeader: () => void;
  onCreateInvoice?: () => void;
  onCreateAct?: () => void;
  onCreateContract?: () => void;
  onPrint: () => void;
  onGenerateDocument: () => void;
  onSubmitApproval: () => void;
  onApprove: () => void;
  onRequestRevision: () => void;
  onAddItem: () => void;
  onUpdateItem: (item: ClientCaseWorkOrderItem) => void;
  onDeleteItem: (item: ClientCaseWorkOrderItem) => void;
}) {
  const [auditItemId, setAuditItemId] = useState<number | null>(null);
  const items = workOrder?.items ?? [];
  const hasWorkOrder = Boolean(workOrder?.id);
  const status = workOrder?.status ?? draft.status;
  const isWorkOrderLocked = status === 'pending_approval' || status === 'approved' || status === 'invoice_created';
  const canSubmitApproval = hasWorkOrder && items.length > 0 && (workOrder?.totalAmount ?? 0) > 0 && status !== 'pending_approval' && status !== 'approved' && status !== 'invoice_created';
  const canApprove = hasWorkOrder && status === 'pending_approval';
  const canRequestRevision = hasWorkOrder && (status === 'pending_approval' || status === 'approved');
  const canCreateInvoice = hasWorkOrder && status === 'approved' && items.length > 0 && (workOrder?.totalAmount ?? 0) > 0;
  const canCreateAct = hasWorkOrder && (status === 'approved' || status === 'invoice_created') && items.length > 0 && (workOrder?.totalAmount ?? 0) > 0;
  const canCreateContract = hasWorkOrder && (status === 'approved' || status === 'invoice_created') && items.length > 0 && (workOrder?.totalAmount ?? 0) > 0;
  const addDisabled = savingItem || !hasWorkOrder || isWorkOrderLocked || !newItemDraft.serviceName.trim();
  const availableExternalDocuments = documents
    .filter((document) => document.isLatestVersion
      && document.sizeBytes > 0
      && document.sizeBytes <= 25 * 1024 * 1024
      && canAttachToExternalWorkOrder(document))
    .sort((left, right) => left.originalFileName.localeCompare(right.originalFileName, 'ru'));
  const lockMessage = status === 'pending_approval'
    ? 'Заказ-наряд на согласовании: редактирование доступно после возврата на доработку.'
    : status === 'approved'
      ? 'Согласованный заказ-наряд нельзя редактировать. Можно создать счет, акт, договор или вернуть расчет на доработку.'
      : status === 'invoice_created'
        ? 'По заказ-наряду уже создан счет: редактирование заблокировано, можно создать акт или договор.'
        : '';

  const renderExecutorOptions = () => (
    <>
      <option value="">Не назначен</option>
      {responsibles.map((responsible) => (
        <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>
      ))}
    </>
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 p-4">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Заказ-наряд</h2>
          <div className="mt-1 text-sm text-slate-500">Предварительный расчет услуг по делу с исполнителями, сроками и итоговой суммой.</div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {hasWorkOrder ? (
            <button
              type="button"
              onClick={onPrint}
              disabled={printing}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-50">
              {printing ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />} Печать
            </button>
          ) : null}
          {hasWorkOrder ? (
            <button
              type="button"
              onClick={onGenerateDocument}
              disabled={generatingDocument}
              className="inline-flex items-center gap-2 rounded-lg border border-indigo-200 px-4 py-2 text-sm font-semibold text-indigo-700 hover:bg-indigo-50 disabled:opacity-50">
              {generatingDocument ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} В документы
            </button>
          ) : null}
          {hasWorkOrder ? (
            <button
              type="button"
              onClick={onSubmitApproval}
              disabled={!canSubmitApproval || approvalAction === 'submit'}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-4 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50 disabled:opacity-50">
              {approvalAction === 'submit' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} На согласование
            </button>
          ) : null}
          {canApprove ? (
              <button
                type="button"
                onClick={onApprove}
                disabled={approvalAction === 'approve'}
                className="inline-flex items-center gap-2 rounded-lg border border-emerald-200 px-4 py-2 text-sm font-semibold text-emerald-700 hover:bg-emerald-50 disabled:opacity-50">
                {approvalAction === 'approve' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} Согласовать
              </button>
          ) : null}
          {canRequestRevision ? (
            <button
              type="button"
              onClick={onRequestRevision}
              disabled={approvalAction === 'revision'}
              className="inline-flex items-center gap-2 rounded-lg border border-amber-200 px-4 py-2 text-sm font-semibold text-amber-700 hover:bg-amber-50 disabled:opacity-50">
              {approvalAction === 'revision' ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} На доработку
            </button>
          ) : null}
          {hasWorkOrder && onCreateInvoice ? (
            <button
              type="button"
              onClick={onCreateInvoice}
              disabled={!canCreateInvoice || creatingInvoice}
              className="inline-flex items-center gap-2 rounded-lg border border-emerald-200 px-4 py-2 text-sm font-semibold text-emerald-700 hover:bg-emerald-50 disabled:opacity-50">
              {creatingInvoice ? <Loader2 className="h-4 w-4 animate-spin" /> : <WalletCards className="h-4 w-4" />} Создать счет
            </button>
          ) : null}
          {hasWorkOrder && onCreateAct ? (
            <button
              type="button"
              onClick={onCreateAct}
              disabled={!canCreateAct || creatingAct}
              className="inline-flex items-center gap-2 rounded-lg border border-cyan-200 px-4 py-2 text-sm font-semibold text-cyan-700 hover:bg-cyan-50 disabled:opacity-50">
              {creatingAct ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileText className="h-4 w-4" />} Создать акт
            </button>
          ) : null}
          {hasWorkOrder && onCreateContract ? (
            <button
              type="button"
              onClick={onCreateContract}
              disabled={!canCreateContract || creatingContract}
              className="inline-flex items-center gap-2 rounded-lg border border-violet-200 px-4 py-2 text-sm font-semibold text-violet-700 hover:bg-violet-50 disabled:opacity-50">
              {creatingContract ? <Loader2 className="h-4 w-4 animate-spin" /> : <Briefcase className="h-4 w-4" />} Создать договор
            </button>
          ) : null}
          {hasWorkOrder ? (
            <button
              type="button"
              onClick={onDeleteHeader}
              disabled={deletingHeader || isWorkOrderLocked}
              className="inline-flex items-center gap-2 rounded-lg border border-rose-200 px-4 py-2 text-sm font-semibold text-rose-700 hover:bg-rose-50 disabled:opacity-50">
              {deletingHeader ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />} Удалить
            </button>
          ) : null}
          <button
            type="button"
            onClick={onSaveHeader}
            disabled={savingHeader || isWorkOrderLocked}
            className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50">
            {savingHeader ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} {hasWorkOrder ? 'Сохранить' : 'Создать'}
          </button>
        </div>
      </div>

      <div className="grid gap-4 md:grid-cols-3">
        <SummaryCard label="Статус" value={workOrder?.statusLabel || WORK_ORDER_STATUS_OPTIONS.find((item) => item.value === draft.status)?.label || 'Черновик'} />
        <SummaryCard label="Услуги" value={`${items.length} шт.`} />
        <SummaryCard label="Итого" value={formatMoney(workOrder?.totalAmount ?? 0)} tone={(workOrder?.totalAmount ?? 0) > 0 ? 'emerald' : 'slate'} />
      </div>

      {hasWorkOrder ? (
        <section className="rounded-xl border border-slate-200 p-4">
          <div className="grid gap-3 text-sm md:grid-cols-3">
            <InfoPill label="Отправил" value={workOrder?.approvalRequestedByUserName || '—'} />
            <InfoPill label="Отправлено" value={formatRuDateTime(workOrder?.approvalRequestedAt) || '—'} />
            <InfoPill label="Решение" value={workOrder?.approvalDecisionByUserName ? `${workOrder.approvalDecisionByUserName}, ${formatRuDateTime(workOrder.approvalDecisionAt)}` : '—'} />
          </div>
          {workOrder?.approvalComment ? (
            <div className="mt-3 rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-600">{workOrder.approvalComment}</div>
          ) : null}
          <label className="mt-3 block text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Комментарий к согласованию</span>
            <textarea
              value={approvalComment}
              onChange={(event) => onApprovalCommentChange(event.target.value)}
              rows={2}
              maxLength={1000}
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            />
          </label>
        </section>
      ) : null}

      {hasWorkOrder && externalApprovalAvailable ? (
        <section className="rounded-xl border border-blue-200 bg-blue-50/40 p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
                <Mail className="h-4 w-4 text-blue-600" /> Внешнее согласование с клиентом
              </div>
              <p className="mt-1 text-xs text-slate-500">
                Клиент получит защищённую ссылку на неизменяемую версию и шестизначный код по email.
              </p>
            </div>
            {externalApprovalStatus ? (
              <div className="rounded-lg bg-white px-3 py-2 text-xs text-slate-600 shadow-sm">
                <strong className="text-slate-900">Версия {externalApprovalStatus.version}</strong>
                <span className="mx-1">·</span>
                {externalApprovalStatus.status === 'pending' ? 'Отправлено'
                  : externalApprovalStatus.status === 'opened' ? 'Открыто клиентом'
                    : externalApprovalStatus.status === 'approved' ? 'Согласовано'
                      : externalApprovalStatus.status === 'revision_requested' ? 'Есть замечания'
                        : externalApprovalStatus.status === 'revoked' ? 'Отозвано'
                          : externalApprovalStatus.status === 'expired' ? 'Истекло'
                            : externalApprovalStatus.status === 'locked' ? 'Заблокировано'
                              : externalApprovalStatus.status}
              </div>
            ) : null}
          </div>

          <div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-[140px_1fr_1fr_170px_auto]">
            <select
              value={externalApprovalRecipientType}
              onChange={(event) => onExternalApprovalRecipientTypeChange(event.target.value as 'client' | 'representative')}
              className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
              <option value="client">Клиент</option>
              <option value="representative">Представитель</option>
            </select>
            <input
              value={externalApprovalRecipientName}
              onChange={(event) => onExternalApprovalRecipientNameChange(event.target.value)}
              maxLength={256}
              placeholder="ФИО получателя"
              className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            />
            <select
              value={externalApprovalDeliveryChannel}
              onChange={(event) => onExternalApprovalDeliveryChannelChange(event.target.value as 'email' | 'email_and_max')}
              className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
              <option value="email">Только email</option>
              <option value="email_and_max">Email + MAX</option>
            </select>
            <input
              type="email"
              value={externalApprovalRecipientEmail}
              onChange={(event) => onExternalApprovalRecipientEmailChange(event.target.value)}
              maxLength={320}
              placeholder="Email получателя"
              className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            />
            <button
              type="button"
              onClick={onSendExternalApproval}
              disabled={externalApprovalSaving || !items.length || (workOrder?.totalAmount ?? 0) <= 0}
              className="inline-flex h-10 items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
              {externalApprovalSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
              {externalApprovalStatus ? 'Новая версия' : 'Отправить'}
            </button>
          </div>

          {availableExternalDocuments.length ? (
            <fieldset className="mt-3 rounded-lg border border-blue-100 bg-white/80 p-3">
              <legend className="px-1 text-xs font-semibold uppercase tracking-[0.1em] text-slate-500">
                Документы клиенту · до 10
              </legend>
              <div className="mt-1 grid gap-2 md:grid-cols-2">
                {availableExternalDocuments.map((document) => {
                  const checked = externalApprovalDocumentIds.includes(document.id);
                  const limitReached = !checked && externalApprovalDocumentIds.length >= 10;
                  return (
                    <label key={document.id} className="flex min-w-0 items-start gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
                      <input
                        type="checkbox"
                        checked={checked}
                        disabled={externalApprovalSaving || limitReached}
                        onChange={(event) => onExternalApprovalDocumentIdsChange(
                          event.target.checked
                            ? [...externalApprovalDocumentIds, document.id]
                            : externalApprovalDocumentIds.filter((id) => id !== document.id),
                        )}
                        className="mt-0.5 h-4 w-4 rounded border-slate-300"
                      />
                      <span className="min-w-0">
                        <span className="block truncate font-medium">{document.originalFileName}</span>
                        <span className="block text-xs text-slate-400">версия {document.version} · {Math.max(1, Math.ceil(document.sizeBytes / 1024))} КБ</span>
                      </span>
                    </label>
                  );
                })}
              </div>
              <p className="mt-2 text-xs text-slate-500">
                Наружу попадут только отмеченные файлы. Внутренние документы по умолчанию не передаются.
              </p>
            </fieldset>
          ) : null}

          {externalApprovalStatus ? (
            <div className="mt-3 flex flex-wrap items-center justify-between gap-2 border-t border-blue-100 pt-3 text-xs text-slate-500">
              <span>
                {externalApprovalStatus.recipientName} · {externalApprovalStatus.recipientEmail} · до {formatRuDateTime(externalApprovalStatus.expiresAt)}
              </span>
              {(externalApprovalStatus.status === 'pending' || externalApprovalStatus.status === 'opened') ? (
                <button
                  type="button"
                  onClick={onRevokeExternalApproval}
                  disabled={externalApprovalSaving}
                  className="font-semibold text-rose-600 hover:text-rose-700 disabled:opacity-50">
                  Отозвать ссылку
                </button>
              ) : null}
            </div>
          ) : null}
        </section>
      ) : null}

      {loading ? <LoadingBlock label="Загрузка заказ-наряда..." /> : null}

      {lockMessage ? (
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm font-medium text-amber-800">
          {lockMessage}
        </div>
      ) : null}

      <section className="rounded-xl border border-slate-200 p-4">
        <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Параметры</h3>
        <div className="mt-4 grid gap-3 md:grid-cols-[1fr_180px_120px]">
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Название</span>
            <input
              value={draft.title}
              onChange={(event) => onDraftChange({ ...draft, title: event.target.value })}
              disabled={isWorkOrderLocked}
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
              placeholder="Предварительный расчет по делу"
            />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
            <SystemSelect
              value={draft.status}
              onChange={(event) => onDraftChange({ ...draft, status: event.target.value })}
              disabled={isWorkOrderLocked}
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
              {WORK_ORDER_STATUS_OPTIONS.map((status) => (
                <option key={status.value} value={status.value}>{status.label}</option>
              ))}
            </SystemSelect>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Валюта</span>
            <input
              value={draft.currency}
              maxLength={3}
              onChange={(event) => onDraftChange({ ...draft, currency: event.target.value.toUpperCase() })}
              disabled={isWorkOrderLocked}
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm uppercase focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            />
          </label>
        </div>
        <label className="mt-3 block text-sm font-medium text-slate-700">
          <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Комментарий</span>
          <textarea
            value={draft.notes}
            onChange={(event) => onDraftChange({ ...draft, notes: event.target.value })}
            disabled={isWorkOrderLocked}
            rows={3}
            className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
          />
        </label>
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Добавить услугу</h3>
        {!hasWorkOrder ? (
          <FinanceEmpty label="Сначала создайте заказ-наряд, затем добавьте услуги." />
        ) : (
          <div className="mt-4 grid gap-3 xl:grid-cols-[1.3fr_1fr_180px_140px_140px_auto]">
            <WorkOrderTextInput label="Услуга" value={newItemDraft.serviceName} onChange={(value) => onNewItemDraftChange({ ...newItemDraft, serviceName: value })} disabled={isWorkOrderLocked} />
            <WorkOrderTextInput label="Описание" value={newItemDraft.description} onChange={(value) => onNewItemDraftChange({ ...newItemDraft, description: value })} disabled={isWorkOrderLocked} />
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Исполнитель</span>
              <SystemSelect value={newItemDraft.executorUserId} onChange={(event) => onNewItemDraftChange({ ...newItemDraft, executorUserId: event.target.value })} disabled={isWorkOrderLocked} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                {renderExecutorOptions()}
              </SystemSelect>
            </label>
            <WorkOrderTextInput label="Срок" type="date" value={newItemDraft.dueDate} onChange={(value) => onNewItemDraftChange({ ...newItemDraft, dueDate: value })} disabled={isWorkOrderLocked} />
            <WorkOrderTextInput label="Стоимость" type="number" value={newItemDraft.unitPrice} onChange={(value) => onNewItemDraftChange({ ...newItemDraft, unitPrice: value })} disabled={isWorkOrderLocked} />
            <button type="button" onClick={onAddItem} disabled={addDisabled} className="inline-flex items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50 xl:self-end">
              {savingItem ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} Добавить
            </button>
          </div>
        )}
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Услуги</h3>
        {items.length === 0 ? <FinanceEmpty label="Услуги в заказ-наряде пока не добавлены." /> : null}
        <div className="mt-3 space-y-3">
          {items.map((item) => {
            const itemDraft = itemDrafts[item.id] ?? buildWorkOrderItemDraft(item);
            return (
              <div key={item.id} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                <div className="grid gap-3 xl:grid-cols-[1.2fr_1fr_180px_140px_120px_120px_auto]">
                  <WorkOrderTextInput label="Услуга" value={itemDraft.serviceName} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, serviceName: value })} disabled={isWorkOrderLocked} />
                  <WorkOrderTextInput label="Описание" value={itemDraft.description} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, description: value })} disabled={isWorkOrderLocked} />
                  <label className="text-sm font-medium text-slate-700">
                    <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Исполнитель</span>
                    <SystemSelect value={itemDraft.executorUserId} onChange={(event) => onItemDraftChange(item.id, { ...itemDraft, executorUserId: event.target.value })} disabled={isWorkOrderLocked} className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                      {renderExecutorOptions()}
                    </SystemSelect>
                  </label>
                  <WorkOrderTextInput label="Срок" type="date" value={itemDraft.dueDate} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, dueDate: value })} disabled={isWorkOrderLocked} />
                  <WorkOrderTextInput label="Кол-во" type="number" value={itemDraft.quantity} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, quantity: value })} disabled={isWorkOrderLocked} />
                  <WorkOrderTextInput label="Цена" type="number" value={itemDraft.unitPrice} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, unitPrice: value })} disabled={isWorkOrderLocked} />
                  <div className="flex items-end gap-2">
                    <button type="button" onClick={() => setAuditItemId((current) => (current === item.id ? null : item.id))} className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 text-slate-600 hover:bg-white" aria-label={`${auditItemId === item.id ? 'Скрыть историю услуги' : 'История услуги'} ${item.serviceName}`}>
                      <Clock3 className="h-4 w-4" />
                    </button>
                    <button type="button" onClick={() => onUpdateItem(item)} disabled={savingItem || isWorkOrderLocked} className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-blue-200 text-blue-700 hover:bg-blue-50 disabled:opacity-50" aria-label="Сохранить услугу">
                      {savingItem ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                    </button>
                    <button type="button" onClick={() => onDeleteItem(item)} disabled={deletingItemId === item.id || isWorkOrderLocked} className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-rose-200 text-rose-700 hover:bg-rose-50 disabled:opacity-50" aria-label="Удалить услугу">
                      {deletingItemId === item.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                    </button>
                  </div>
                </div>
                <div className="mt-2 flex flex-wrap gap-3 text-xs text-slate-500">
                  <span>Текущая сумма: <b>{formatMoney(item.totalAmount)}</b></span>
                  <span>Срок: {toRuDate(item.dueDate) || '—'}</span>
                  <span>Исполнитель: {item.executorName || 'не назначен'}</span>
                </div>
                {auditItemId === item.id ? (
                  <div className="mt-3">
                    <ObjectAuditHistoryCard
                      objectType="client-case-work-order-item"
                      objectId={item.id}
                      compact
                    />
                  </div>
                ) : null}
              </div>
            );
          })}
        </div>
      </section>

      <ObjectAuditHistoryCard
        objectType="client-case-work-order"
        objectId={workOrder?.id ?? null}
        compact
      />
    </div>
  );
}

function WorkOrderTextInput({
  label,
  value,
  onChange,
  type = 'text',
  disabled = false,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  type?: 'text' | 'date' | 'number';
  disabled?: boolean;
}) {
  const fieldClassName = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100';
  return (
    <label className="text-sm font-medium text-slate-700">
      <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">{label}</span>
      {type === 'date' ? (
        <SystemDateInput
          type="date"
          value={value}
          onChange={(event) => onChange(event.target.value)}
          disabled={disabled}
          aria-label={label}
          className={fieldClassName}
        />
      ) : (
        <input
          type={type}
          step={type === 'number' ? '0.01' : undefined}
          min={type === 'number' ? '0' : undefined}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          disabled={disabled}
          className={fieldClassName}
        />
      )}
    </label>
  );
}

function FinancePanel({
  caseItem,
  detail,
  loading,
  onOpenFinance,
  onCreateInvoice,
  onCreatePayment,
}: {
  caseItem: ClientCase;
  detail: ClientCaseFinanceDetail | null;
  loading: boolean;
  onOpenFinance?: (caseItem: ClientCase) => void;
  onCreateInvoice?: (caseItem: ClientCase) => void;
  onCreatePayment?: (caseItem: ClientCase) => void;
}) {
  const totals = detail?.totals ?? {
    feeAmount: caseItem.feeAmount ?? 0,
    incomeTotal: caseItem.incomeTotal ?? 0,
    incomePaid: caseItem.incomePaid ?? 0,
    incomeOutstanding: caseItem.incomeOutstanding ?? 0,
    incomeCount: caseItem.incomeCount ?? 0,
    expenseTotal: caseItem.expenseTotal ?? 0,
    expensePaid: caseItem.expensePaid ?? 0,
    expenseOutstanding: caseItem.expenseOutstanding ?? 0,
    expenseCount: caseItem.expenseCount ?? 0,
    plannedMargin: (caseItem.incomeTotal ?? 0) - (caseItem.expenseTotal ?? 0),
    paidMargin: (caseItem.incomePaid ?? 0) - (caseItem.expensePaid ?? 0),
    outstandingBalance: caseItem.incomeOutstanding ?? 0,
  };
  const invoices = detail?.invoices ?? [];
  const payments = detail?.payments ?? [];
  const contracts = detail?.contracts ?? [];
  const calendarPayments = [...payments].sort((left, right) => {
    const leftTime = Date.parse(left.plannedDate || left.date);
    const rightTime = Date.parse(right.plannedDate || right.date);
    return (Number.isFinite(leftTime) ? leftTime : 0) - (Number.isFinite(rightTime) ? rightTime : 0);
  });
  const paymentCalendarGroups = groupPaymentCalendar(calendarPayments);
  const calendarSummary = detail?.calendar;
  const paymentCalendarPlanTotal = calendarSummary?.planTotal ?? calendarPayments.reduce((sum, payment) => sum + payment.amount, 0);
  const paymentCalendarPaidTotal = calendarSummary?.paidTotal ?? calendarPayments.reduce((sum, payment) => sum + payment.paidAmount, 0);
  const paymentCalendarOutstandingTotal = calendarSummary?.outstandingTotal ?? calendarPayments.reduce((sum, payment) => sum + payment.outstandingAmount, 0);
  const paymentCalendarOverdueTotal = calendarSummary?.overdueTotal ?? calendarPayments
    .filter((payment) => getPaymentCalendarState(payment) === 'overdue')
    .reduce((sum, payment) => sum + payment.outstandingAmount, 0);
  const paymentCalendarNextDate = calendarSummary?.nextPaymentDate ?? calendarPayments
    .find((payment) => getPaymentCalendarState(payment) === 'planned')
    ?.plannedDate;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 p-4">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Финансы дела</h2>
          <div className="mt-1 text-sm text-slate-500">Счета, платежи и договоры подтягиваются по связям текущего дела.</div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {onCreateInvoice ? (
            <button
              type="button"
              onClick={() => onCreateInvoice(caseItem)}
              className="inline-flex items-center gap-2 rounded-lg border border-emerald-200 px-4 py-2 text-sm font-semibold text-emerald-700 hover:bg-emerald-50">
              <FilePlus2 className="h-4 w-4" /> Новый счет
            </button>
          ) : null}
          {onCreatePayment ? (
            <button
              type="button"
              onClick={() => onCreatePayment(caseItem)}
              className="inline-flex items-center gap-2 rounded-lg border border-cyan-200 px-4 py-2 text-sm font-semibold text-cyan-700 hover:bg-cyan-50">
              <WalletCards className="h-4 w-4" /> Новый платеж
            </button>
          ) : null}
          {onOpenFinance ? (
            <button
              type="button"
              onClick={() => onOpenFinance(caseItem)}
              className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-4 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50">
              <WalletCards className="h-4 w-4" /> Открыть финансовый журнал
            </button>
          ) : null}
        </div>
      </div>

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
        <SummaryCard label="Гонорар" value={formatMoney(totals.feeAmount)} />
        <SummaryCard label="Счета" value={`${formatMoney(totals.incomeTotal)} · ${totals.incomeCount} шт.`} />
        <SummaryCard label="Оплачено" value={formatMoney(totals.incomePaid)} tone="emerald" />
        <SummaryCard label="Долг" value={formatMoney(totals.outstandingBalance)} tone={totals.outstandingBalance > 0 ? 'rose' : 'emerald'} />
        <SummaryCard label="Расходы" value={`${formatMoney(totals.expenseTotal)} · ${totals.expenseCount} шт.`} />
        <SummaryCard label="Расходы оплачено" value={formatMoney(totals.expensePaid)} />
        <SummaryCard label="Расходы долг" value={formatMoney(totals.expenseOutstanding)} tone={totals.expenseOutstanding > 0 ? 'amber' : 'emerald'} />
        <SummaryCard label="Маржа" value={formatMoney(totals.paidMargin)} tone={totals.paidMargin >= 0 ? 'emerald' : 'rose'} />
      </div>

      {loading ? <LoadingBlock label="Загрузка финансов по делу..." /> : null}
      {!loading && !detail ? (
        <div className="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800">
          Не удалось загрузить детальные связи. Агрегаты выше показаны из карточки дела.
        </div>
      ) : null}

      <div className="grid gap-4 xl:grid-cols-2">
        <section className="rounded-xl border border-slate-200 p-4">
          <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Связанные договоры</h3>
          {contracts.length === 0 ? <FinanceEmpty label="Договоры по делу пока не найдены." /> : null}
          <div className="mt-3 space-y-2">
            {contracts.map((contract) => (
              <a key={contract.id} href={`/contracts/${contract.id}`} className="block rounded-lg border border-slate-100 bg-slate-50 p-3 hover:border-blue-200 hover:bg-blue-50">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="font-semibold text-blue-700">Договор №{contract.number}</div>
                    <div className="mt-1 text-sm text-slate-600">{contract.title || 'Без названия'}</div>
                  </div>
                  <FinanceBadge label={contract.status || '—'} />
                </div>
                <div className="mt-2 text-xs text-slate-500">{toRuDate(contract.date)} · {formatMoney(contract.amount)}</div>
              </a>
            ))}
          </div>
        </section>

        <section className="rounded-xl border border-slate-200 p-4">
          <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Счета</h3>
          {invoices.length === 0 ? <FinanceEmpty label="Счета по делу пока не найдены." /> : null}
          <div className="mt-3 space-y-2">
            {invoices.map((invoice) => (
              <a key={`${invoice.id}-${invoice.number}`} href={`/invoices/${invoice.id}`} className="block rounded-lg border border-slate-100 bg-slate-50 p-3 hover:border-blue-200 hover:bg-blue-50">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="font-semibold text-blue-700">Счет №{invoice.number}</div>
                    <div className="mt-1 text-xs text-slate-500">{toRuDate(invoice.date)}{invoice.contractNumber ? ` · договор ${invoice.contractNumber}` : ''}</div>
                  </div>
                  <FinanceBadge label={formatFinanceStatus(invoice.status)} />
                </div>
                <div className="mt-2 grid gap-2 text-sm text-slate-600 sm:grid-cols-3">
                  <span>Сумма: <b>{formatMoney(invoice.amount)}</b></span>
                  <span>Оплачено: <b>{formatMoney(invoice.paidAmount)}</b></span>
                  <span>Остаток: <b>{formatMoney(invoice.outstandingAmount)}</b></span>
                </div>
              </a>
            ))}
          </div>
        </section>
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <section className="rounded-xl border border-slate-200 p-4">
          <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Платежи и начисления</h3>
          {payments.length === 0 ? <FinanceEmpty label="Платежи по делу пока не найдены." /> : null}
          <div className="mt-3 space-y-2">
            {payments.slice(0, 12).map((payment) => (
              <a key={payment.id} href={`/payments/${payment.id}`} className="block rounded-lg border border-slate-100 bg-slate-50 p-3 hover:border-blue-200 hover:bg-blue-50">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="font-semibold text-slate-900">{formatDocumentKind(payment.documentKind)} · {formatFinanceType(payment.type)}</div>
                    <div className="mt-1 text-sm text-slate-600">{payment.description || payment.invoiceNumber || `Платеж #${payment.id}`}</div>
                  </div>
                  <FinanceBadge label={formatFinanceStatus(payment.status)} />
                </div>
                <div className="mt-2 text-xs text-slate-500">
                  {toRuDate(payment.date)} · {formatMoney(payment.amount)}
                  {payment.paymentSourceName ? ` · ${payment.paymentSourceName}` : ''}
                  {payment.paymentOrderNumber ? ` · документ ${payment.paymentOrderNumber}` : ''}
                </div>
              </a>
            ))}
          </div>
        </section>

        <section className="rounded-xl border border-slate-200 p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Календарь оплат</h3>
              <div className="mt-1 text-sm text-slate-500">Плановые и фактические оплаты по делу, сгруппированные по месяцам.</div>
            </div>
            <span className="rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-600">{calendarPayments.length} платежей</span>
          </div>
          <div className="mt-3 grid gap-2 sm:grid-cols-3">
            <FinanceMiniStat label="План оплат" value={formatMoney(paymentCalendarPlanTotal)} />
            <FinanceMiniStat label="Оплачено" value={formatMoney(paymentCalendarPaidTotal)} tone="emerald" />
            <FinanceMiniStat label="Остаток" value={formatMoney(paymentCalendarOutstandingTotal)} tone={paymentCalendarOutstandingTotal > 0 ? 'rose' : 'emerald'} />
          </div>
          <div className="mt-3 grid gap-2 sm:grid-cols-4">
            <FinanceMiniStat label="Следующая дата" value={toRuDate(paymentCalendarNextDate) || '—'} />
            <FinanceMiniStat label="Плановые" value={`${calendarSummary?.plannedCount ?? calendarPayments.filter((payment) => getPaymentCalendarState(payment) === 'planned').length}`} tone="cyan" />
            <FinanceMiniStat label="Просроченные" value={`${calendarSummary?.overdueCount ?? calendarPayments.filter((payment) => getPaymentCalendarState(payment) === 'overdue').length}`} tone={paymentCalendarOverdueTotal > 0 ? 'rose' : 'emerald'} />
            <FinanceMiniStat label="Просрочка" value={formatMoney(paymentCalendarOverdueTotal)} tone={paymentCalendarOverdueTotal > 0 ? 'rose' : 'emerald'} />
          </div>
          {calendarPayments.length === 0 ? <FinanceEmpty label="Плановых дат оплат пока нет." /> : null}
          <div className="mt-3 space-y-3">
            {paymentCalendarGroups.slice(0, 8).map((group) => (
              <div key={group.key} className="overflow-hidden rounded-lg border border-slate-100">
                <div className="flex flex-wrap items-center justify-between gap-2 bg-slate-50 px-3 py-2">
                  <div className="font-semibold text-slate-800">{group.label}</div>
                  <div className="flex flex-wrap gap-2 text-xs text-slate-500">
                    <span>План: {formatMoney(group.total)}</span>
                    <span>Оплачено: {formatMoney(group.paid)}</span>
                    <span>Остаток: {formatMoney(group.outstanding)}</span>
                    {group.overdueCount > 0 ? <span className="font-semibold text-rose-600">Просрочено: {group.overdueCount}</span> : null}
                  </div>
                </div>
                {group.items.map((payment) => {
                  const state = getPaymentCalendarState(payment);
                  return (
                    <div key={`calendar-${payment.id}`} className="grid gap-2 border-t border-slate-100 p-3 text-sm md:grid-cols-[120px_1fr_auto]">
                      <div>
                        <div className="font-semibold text-slate-900">{toRuDate(getPaymentCalendarDate(payment))}</div>
                        {payment.paidDate ? <div className="mt-1 text-xs text-slate-500">Факт: {toRuDate(payment.paidDate)}</div> : null}
                      </div>
                      <div className="min-w-0">
                        <div className="font-medium text-slate-700">{payment.description || payment.invoiceNumber || `Платеж #${payment.id}`}</div>
                        <div className="mt-1 text-xs text-slate-500">
                          {formatDocumentKind(payment.documentKind)} · {formatFinanceType(payment.type)}
                          {payment.invoiceNumber ? ` · счет №${payment.invoiceNumber}` : ''}
                          {payment.paymentSourceName ? ` · ${payment.paymentSourceName}` : ''}
                          {payment.paymentOrderNumber ? ` · документ ${payment.paymentOrderNumber}` : ''}
                        </div>
                      </div>
                      <div className="flex flex-col items-start gap-1 md:items-end">
                        <div className="font-semibold text-slate-900">{formatMoney(payment.amount)}</div>
                        {payment.outstandingAmount > 0 ? <div className="text-xs font-medium text-rose-600">остаток {formatMoney(payment.outstandingAmount)}</div> : null}
                        <PaymentCalendarBadge state={state} />
                      </div>
                    </div>
                  );
                })}
              </div>
            ))}
          </div>
        </section>
      </div>
    </div>
  );
}

function FinanceMiniStat({ label, value, tone = 'slate' }: { label: string; value: string; tone?: SummaryTone }) {
  const toneClass = tone === 'emerald'
    ? 'bg-emerald-50 text-emerald-700'
    : tone === 'rose'
      ? 'bg-rose-50 text-rose-700'
      : tone === 'amber'
        ? 'bg-amber-50 text-amber-700'
        : 'bg-slate-50 text-slate-700';

  return (
    <div className={`rounded-lg px-3 py-2 ${toneClass}`}>
      <div className="text-xs font-semibold uppercase tracking-[0.12em] opacity-70">{label}</div>
      <div className="mt-1 font-semibold">{value}</div>
    </div>
  );
}

function PaymentCalendarBadge({ state }: { state: PaymentCalendarState }) {
  return (
    <span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${PAYMENT_CALENDAR_CLASSES[state]}`}>
      {PAYMENT_CALENDAR_LABELS[state]}
    </span>
  );
}

function FinanceBadge({ label }: { label: string }) {
  return (
    <span className="shrink-0 rounded-full bg-blue-50 px-2.5 py-1 text-xs font-semibold text-blue-700">
      {label}
    </span>
  );
}

function FinanceEmpty({ label }: { label: string }) {
  return <div className="mt-3 rounded-lg border border-dashed border-slate-200 p-3 text-sm text-slate-500">{label}</div>;
}

function LoadingBlock({ label }: { label: string }) {
  return (
    <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
      <span className="inline-flex items-center gap-2"><Loader2 className="h-4 w-4 animate-spin" /> {label}</span>
    </div>
  );
}

function StageTransitionActions({
  prompt,
  canCreateTask,
  creatingTask,
  onCreateTask,
  onPrepareDeadline,
  onClose,
}: {
  prompt: StageTransitionPromptState;
  canCreateTask: boolean;
  creatingTask: boolean;
  onCreateTask: (template: CaseStageTaskTemplate) => void;
  onPrepareDeadline: (suggestion: CaseStageDeadlineSuggestion) => void;
  onClose: () => void;
}) {
  return (
    <section className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-950 shadow-sm">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="inline-flex items-center gap-2 rounded-full bg-white px-2.5 py-1 text-xs font-semibold uppercase tracking-[0.12em] text-amber-700">
              <Clock3 className="h-3.5 w-3.5" /> Этап дела изменён
            </span>
            <span className="text-xs text-amber-800">{prompt.previousStage} → {prompt.nextStage}</span>
          </div>
          <p className="mt-2 text-sm text-amber-900">
            Проверьте типовые действия по новому этапу: можно сразу создать задачи и подготовить процессуальные сроки.
          </p>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-amber-200 bg-white text-amber-700 hover:bg-amber-100"
          aria-label="Скрыть подсказки по этапу">
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="mt-3 grid gap-3 xl:grid-cols-2">
        <div className="rounded-lg border border-amber-200 bg-white p-3">
          <h3 className="text-xs font-semibold uppercase tracking-[0.12em] text-amber-700">Задачи</h3>
          <div className="mt-2 grid gap-2 md:grid-cols-2">
            {prompt.taskTemplates.map((template) => (
              <div key={template.key} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                <div className="font-semibold text-slate-900">{template.title}</div>
                <div className="mt-1 text-xs text-slate-600">{template.description}</div>
                {canCreateTask ? (
                  <button
                    type="button"
                    onClick={() => onCreateTask(template)}
                    disabled={creatingTask}
                    aria-label={`Создать задачу: ${template.title}`}
                    className="mt-3 inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-2 text-xs font-semibold text-white hover:bg-emerald-700 disabled:opacity-50">
                    {creatingTask ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <ClipboardPlus className="h-3.5 w-3.5" />} Создать задачу
                  </button>
                ) : null}
              </div>
            ))}
          </div>
        </div>

        <div className="rounded-lg border border-amber-200 bg-white p-3">
          <h3 className="text-xs font-semibold uppercase tracking-[0.12em] text-amber-700">Процессуальные сроки</h3>
          {prompt.deadlineSuggestions.length > 0 ? (
            <div className="mt-2 grid gap-2 md:grid-cols-2">
              {prompt.deadlineSuggestions.map((suggestion) => (
                <div key={suggestion.key} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                  <div className="font-semibold text-slate-900">{suggestion.title}</div>
                  <div className="mt-1 text-xs text-slate-600">{suggestion.description}</div>
                  <button
                    type="button"
                    onClick={() => onPrepareDeadline(suggestion)}
                    aria-label={`Подготовить срок: ${suggestion.title}`}
                    className="mt-3 inline-flex items-center gap-2 rounded-lg border border-blue-200 px-3 py-2 text-xs font-semibold text-blue-700 hover:bg-blue-50">
                    <Clock3 className="h-3.5 w-3.5" /> Подготовить срок
                  </button>
                </div>
              ))}
            </div>
          ) : (
            <div className="mt-2 rounded-lg border border-dashed border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
              Для этого этапа нет обязательной подсказки по судебному сроку. Если срок нужен, откройте вкладку «Суд» и добавьте его вручную.
            </div>
          )}
        </div>
      </div>
    </section>
  );
}

function SummaryCard({ label, value, tone = 'slate' }: { label: string; value: string; tone?: SummaryTone }) {
  const toneClasses = tone === 'emerald'
    ? {
        surface: 'border-emerald-200 border-l-emerald-500 bg-emerald-50/40',
        label: 'text-emerald-700',
        value: 'text-emerald-900',
      }
    : tone === 'rose'
      ? {
          surface: 'border-rose-200 border-l-rose-500 bg-rose-50/50',
          label: 'text-rose-700',
          value: 'text-rose-900',
        }
      : tone === 'amber'
        ? {
            surface: 'border-amber-200 border-l-amber-500 bg-amber-50/50',
            label: 'text-amber-700',
            value: 'text-amber-950',
          }
        : tone === 'cyan'
          ? {
              surface: 'border-cyan-200 border-l-cyan-500 bg-cyan-50/40',
              label: 'text-cyan-700',
              value: 'text-cyan-950',
            }
          : {
              surface: 'border-slate-200 border-l-slate-300 bg-white',
              label: 'text-slate-500',
              value: 'text-slate-900',
            };
  return (
    <div aria-label={`${label}: ${value}`} className={`min-w-0 rounded-xl border border-l-4 p-3 shadow-sm ${toneClasses.surface}`}>
      <div className={`text-[11px] font-semibold uppercase tracking-[0.12em] ${toneClasses.label}`}>{label}</div>
      <div className={`mt-1.5 break-words text-base font-bold leading-5 ${toneClasses.value}`}>{value}</div>
    </div>
  );
}

function InfoSection({ title, rows }: { title: string; rows: Array<[string, string]> }) {
  return (
    <section className="rounded-xl border border-slate-200 p-4">
      <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">{title}</h2>
      <dl className="mt-4 space-y-3">
        {rows.map(([label, value]) => (
          <div key={label} className="grid gap-1 text-sm sm:grid-cols-[160px_1fr]">
            <dt className="text-slate-500">{label}</dt>
            <dd className="font-medium text-slate-800">{value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function TextBlock({ title, value }: { title: string; value: string }) {
  return (
    <section className="rounded-xl border border-slate-200 p-4">
      <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">{title}</h2>
      <p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-slate-700">{value}</p>
    </section>
  );
}

function buildParticipantDirectoryOptions({
  sourceType,
  clients,
  legalEntities,
  contacts,
  relatedParties,
  search,
}: {
  sourceType: ParticipantSourceType;
  clients: Client[];
  legalEntities: LegalEntitySummary[];
  contacts: ContactDirectoryItemDto[];
  relatedParties: ClientCaseRelatedParty[];
  search: string;
}): ParticipantDirectoryOption[] {
  const linkedKeys = new Set(
    relatedParties
      .map((party) => {
        const sourceId = getRelatedPartySourceId(party);
        return sourceId ? `${party.sourceType}:${sourceId}` : null;
      })
      .filter((value): value is string => Boolean(value)),
  );

  const options = sourceType === 'client'
    ? clients
      .filter((client) => client.isActive !== false)
      .map((client) => ({
        key: `client:${client.id}`,
        sourceType: 'client' as const,
        sourceId: client.id,
        label: client.name || `Клиент #${client.id}`,
        description: [client.company, client.phone, client.email].filter(Boolean).join(' · '),
      }))
    : sourceType === 'legalEntity'
      ? legalEntities.map((entity) => ({
        key: `legalEntity:${entity.id}`,
        sourceType: 'legalEntity' as const,
        sourceId: entity.id,
        label: entity.shortName || entity.fullName || `Юрлицо #${entity.id}`,
        description: [entity.inn ? `ИНН ${entity.inn}` : null, entity.phone, entity.email].filter(Boolean).join(' · '),
      }))
      : contacts
        .filter((contact) => contact.sourceType === 'external' && contact.isActive !== false)
        .map((contact) => ({
          key: `externalContact:${contact.sourceId}`,
          sourceType: 'externalContact' as const,
          sourceId: contact.sourceId,
          label: contact.fullName || `Контакт #${contact.sourceId}`,
          description: [contact.organizationName, contact.phone, contact.email].filter(Boolean).join(' · '),
        }));

  const query = normalizeParticipantSearch(search);

  return options
    .filter((option) => !linkedKeys.has(option.key))
    .filter((option) => {
      if (!query) return true;
      return normalizeParticipantSearch(`${option.label} ${option.description ?? ''}`).includes(query);
    })
    .sort((a, b) => a.label.localeCompare(b.label, 'ru'));
}

function getRelatedPartySourceId(party: ClientCaseRelatedParty) {
  if (party.sourceId) return party.sourceId;
  if (party.sourceType === 'client') return party.clientId ?? null;
  if (party.sourceType === 'legalEntity') return party.legalEntityId ?? null;
  if (party.sourceType === 'externalContact') return party.externalContactId ?? null;
  return null;
}

function normalizeParticipantSearch(value: string) {
  return value.trim().toLocaleLowerCase('ru-RU');
}

function getRelatedPartySourceLabel(party: ClientCaseRelatedParty) {
  if (party.sourceType === 'client') return 'Физическое лицо';
  if (party.sourceType === 'legalEntity') return 'Юридическое лицо';
  return 'Контакт';
}

function getRelatedPartyContactLine(party: ClientCaseRelatedParty, showContacts: boolean) {
  const values = [
    party.phone ? (showContacts ? party.phone : maskSensitiveValue(party.phone)) : null,
    party.email ? (showContacts ? party.email : maskSensitiveValue(party.email, 6)) : null,
  ].filter(Boolean);

  return values.join(' · ');
}

function RelatedParties({
  parties,
  canRevealContacts,
  showArchived,
  onToggleArchived,
  onOpenClient,
  onOpenLegalEntity,
  onDelete,
  onRestore,
  deletingPartyId,
  restoringPartyId,
}: {
  parties: ClientCaseRelatedParty[];
  canRevealContacts: boolean;
  showArchived: boolean;
  onToggleArchived: () => void;
  onOpenClient?: (clientId: number) => void;
  onOpenLegalEntity?: (legalEntityId: number) => void;
  onDelete?: (party: ClientCaseRelatedParty) => void;
  onRestore?: (party: ClientCaseRelatedParty) => void;
  deletingPartyId?: number | null;
  restoringPartyId?: number | null;
}) {
  const [showContacts, setShowContacts] = useState(false);
  const [auditPartyId, setAuditPartyId] = useState<number | null>(null);
  const auditParty = parties.find((party) => party.id === auditPartyId) ?? null;
  const contactsVisible = canRevealContacts && showContacts;

  return (
    <section className="rounded-xl border border-slate-200 p-4">
      <button
        type="button"
        onClick={onToggleArchived}
        className="mb-3 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50">
        {showArchived ? 'Скрыть архив' : 'Показать архив'}
      </button>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Связанные лица</h2>
        {parties.some((party) => party.phone || party.email) ? (
          <button
            type="button"
            onClick={() => canRevealContacts && setShowContacts((value) => !value)}
            disabled={!canRevealContacts}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 disabled:cursor-not-allowed disabled:bg-slate-50 disabled:text-slate-400">
            {!canRevealContacts ? <LockKeyhole className="h-3.5 w-3.5" /> : null}
            {!canRevealContacts ? 'Контакты скрыты по роли' : contactsVisible ? 'Скрыть контакты' : 'Показать контакты'}
          </button>
        ) : null}
      </div>
      {parties.length === 0 ? <div className="mt-3 text-sm text-slate-500">Связанные лица пока не добавлены.</div> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {parties.map((party) => (
          <div key={party.id} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
            <div className="font-semibold text-slate-900">{party.displayName}</div>
            <div className="mt-1 text-sm text-slate-500">{party.role || 'Участник'}</div>
            <div className="mt-1 text-xs text-slate-400">{getRelatedPartySourceLabel(party)}</div>
            {party.isArchived ? (
              <div className="mt-2 space-y-1 rounded-lg border border-amber-100 bg-amber-50 px-3 py-2 text-xs text-amber-800">
                <div className="inline-flex rounded-full bg-white/70 px-2 py-1 font-semibold text-amber-700">
                  Архивная связь
                </div>
                <div>
                  Архивировано: {formatRuDateTime(party.archivedAt) || 'дата не указана'}
                  {party.archivedByUserName ? ` · ${party.archivedByUserName}` : ''}
                </div>
                {party.archiveReason ? <div>Причина: {party.archiveReason}</div> : null}
              </div>
            ) : null}
            {party.phone || party.email ? (
              <div className="mt-2 text-xs text-slate-500">{getRelatedPartyContactLine(party, contactsVisible)}</div>
            ) : null}
            {party.notes ? <div className="mt-2 text-xs text-slate-500">{party.notes}</div> : null}
            <button
              type="button"
              onClick={() => setAuditPartyId((current) => (current === party.id ? null : party.id))}
              aria-label={`${auditPartyId === party.id ? 'Скрыть историю' : 'История'} участника ${party.displayName}`}
              className="mt-3 rounded-md border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-white">
              {auditPartyId === party.id ? 'Скрыть историю' : 'История'}
            </button>
            {party.sourceType === 'client' && (party.clientId || party.sourceId) && onOpenClient ? (
              <button
                type="button"
                onClick={() => onOpenClient((party.clientId ?? party.sourceId)!)}
                aria-label={`Открыть участника ${party.displayName}`}
                className="mt-3 rounded-md border border-blue-100 px-3 py-1.5 text-xs font-semibold text-blue-700 hover:bg-blue-50">
                Открыть
              </button>
            ) : null}
            {party.sourceType === 'legalEntity' && (party.legalEntityId || party.sourceId) && onOpenLegalEntity ? (
              <button
                type="button"
                onClick={() => onOpenLegalEntity((party.legalEntityId ?? party.sourceId)!)}
                aria-label={`Открыть участника ${party.displayName}`}
                className="mt-3 rounded-md border border-blue-100 px-3 py-1.5 text-xs font-semibold text-blue-700 hover:bg-blue-50">
                Открыть
              </button>
            ) : null}
            {onRestore && party.isArchived ? (
              <button
                type="button"
                onClick={() => onRestore(party)}
                disabled={restoringPartyId === party.id}
                aria-label={`Восстановить связь с участником ${party.displayName}`}
                className="mt-3 inline-flex items-center gap-1.5 rounded-md border border-emerald-100 px-3 py-1.5 text-xs font-semibold text-emerald-700 hover:bg-emerald-50 disabled:opacity-50">
                {restoringPartyId === party.id ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
                Восстановить
              </button>
            ) : null}
            {onDelete && !party.isArchived ? (
              <button
                type="button"
                onClick={() => onDelete(party)}
                disabled={deletingPartyId === party.id}
                aria-label={`Удалить связь с участником ${party.displayName}`}
                className="mt-3 inline-flex items-center gap-1.5 rounded-md border border-rose-100 px-3 py-1.5 text-xs font-semibold text-rose-700 hover:bg-rose-50 disabled:opacity-50">
                {deletingPartyId === party.id ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Trash2 className="h-3.5 w-3.5" />}
                Удалить
              </button>
            ) : null}
          </div>
        ))}
      </div>
      {auditParty ? (
        <div className="mt-4">
          <ObjectAuditHistoryCard
            objectType="client-case-related-party"
            objectId={auditParty.id}
            compact
          />
        </div>
      ) : null}
    </section>
  );
}

function TaskList({
  tasks,
  allTasks,
  progress,
  filter,
  onFilterChange,
  onCreateTask,
  stageTaskTemplates,
  onCreateStageTask,
  creatingTask,
  canCreateTask,
}: {
  tasks: TaskJournalItem[];
  allTasks: TaskJournalItem[];
  progress: number;
  filter: CaseTaskFilter;
  onFilterChange: (filter: CaseTaskFilter) => void;
  onCreateTask: () => void;
  stageTaskTemplates: CaseStageTaskTemplate[];
  onCreateStageTask: (template: CaseStageTaskTemplate) => void;
  creatingTask: boolean;
  canCreateTask: boolean;
}) {
  const counts: Record<CaseTaskFilter, number> = {
    all: allTasks.length,
    active: allTasks.filter(isActiveTask).length,
    overdue: allTasks.filter(isOverdueTask).length,
    done: allTasks.filter((task) => task.status === 'done' || task.status === 'cancelled').length,
  };

  return (
    <div className="space-y-4">
      <div className="rounded-xl border border-slate-200 p-4">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
          <div>
            <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Связанные задачи</h2>
            <div className="mt-1 text-sm text-slate-500">Прогресс: {progress}%</div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {CASE_TASK_FILTERS.map((option) => (
              <button
                key={option.value}
                type="button"
                onClick={() => onFilterChange(option.value)}
                className={`rounded-lg px-3 py-1.5 text-sm font-semibold ${filter === option.value ? 'bg-slate-900 text-white' : 'bg-slate-100 text-slate-700 hover:bg-slate-200'}`}>
                {option.label} · {counts[option.value]}
              </button>
            ))}
            {canCreateTask ? (
              <button
                type="button"
                onClick={() => void onCreateTask()}
                disabled={creatingTask}
                className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-50">
                {creatingTask ? <Loader2 className="h-4 w-4 animate-spin" /> : <ClipboardPlus className="h-4 w-4" />} Создать
              </button>
            ) : null}
          </div>
        </div>
      </div>
      {canCreateTask && stageTaskTemplates.length > 0 ? (
        <div className="rounded-xl border border-blue-100 bg-blue-50/50 p-4">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
            <div>
              <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-blue-700">Подсказки по этапу</h3>
              <div className="mt-1 text-sm text-slate-600">Создайте типовую задачу с уже заполненными названием, описанием, приоритетом и связью с делом.</div>
            </div>
          </div>
          <div className="mt-3 grid gap-2 lg:grid-cols-2">
            {stageTaskTemplates.map((template) => (
              <div key={template.key} className="rounded-lg border border-blue-100 bg-white p-3">
                <div className="text-xs font-semibold uppercase tracking-[0.12em] text-blue-600">{template.stageLabel}</div>
                <div className="mt-1 font-semibold text-slate-900">{template.title}</div>
                <div className="mt-1 line-clamp-2 text-sm text-slate-600">{template.description}</div>
                <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
                  <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
                    {PRIORITY_LABELS[template.priority]}
                  </span>
                  <button
                    type="button"
                    onClick={() => void onCreateStageTask(template)}
                    disabled={creatingTask}
                    className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
                    {creatingTask ? <Loader2 className="h-4 w-4 animate-spin" /> : <ClipboardPlus className="h-4 w-4" />} Создать по шаблону
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      ) : null}
      {tasks.length === 0 ? <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">По выбранному фильтру задач нет.</div> : null}
      <div className="overflow-x-auto">
        <table className="w-full min-w-[760px] text-left text-sm">
          <thead className="bg-slate-50 text-xs uppercase tracking-[0.12em] text-slate-500">
            <tr>
              <th className="px-3 py-2">Задача</th>
              <th className="px-3 py-2">Исполнитель</th>
              <th className="px-3 py-2">Срок</th>
              <th className="px-3 py-2">Статус</th>
              <th className="px-3 py-2">Результат</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {tasks.map((task) => (
              <tr key={task.id}>
                <td className="px-3 py-3 font-semibold text-slate-900">{task.title}</td>
                <td className="px-3 py-3 text-slate-700">{task.assigneeName || '—'}</td>
                <td className="px-3 py-3 text-slate-700">{formatRuDateTime(task.dueAt) || '—'}</td>
                <td className="px-3 py-3 text-slate-700">{TASK_STATUS_LABELS[task.status] ?? task.status}</td>
                <td className="px-3 py-3 text-slate-700">{task.result || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

type CourtTimelineState = 'done' | 'today' | 'future' | 'unknown';
type CourtTimelineKind = 'hearing' | 'deadline' | 'milestone' | 'sync';

interface CourtTimelineEntry {
  id: string;
  label: string;
  value?: string | null;
  description?: string | null;
  kind: CourtTimelineKind;
  state: CourtTimelineState;
}

const COURT_TIMELINE_STATE_LABELS: Record<CourtTimelineState, string> = {
  done: 'Прошло',
  today: 'Сегодня',
  future: 'Предстоит',
  unknown: 'Без даты',
};

const COURT_TIMELINE_STATE_CLASSES: Record<CourtTimelineState, string> = {
  done: 'bg-slate-100 text-slate-600',
  today: 'bg-amber-50 text-amber-700',
  future: 'bg-cyan-50 text-cyan-700',
  unknown: 'bg-slate-50 text-slate-500',
};

function getCourtTimelineState(value?: string | null): CourtTimelineState {
  if (!value) {
    return 'unknown';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return 'unknown';
  }

  const day = new Date(date);
  day.setHours(0, 0, 0, 0);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const dayTime = day.getTime();
  const todayTime = today.getTime();

  if (dayTime === todayTime) {
    return 'today';
  }

  return dayTime < todayTime ? 'done' : 'future';
}

function buildCourtTimeline(caseItem: ClientCase, logs: ClientCaseCourtSyncLog[]): CourtTimelineEntry[] {
  const entries: CourtTimelineEntry[] = [
    {
      id: 'claim-filed',
      label: 'Подача заявления',
      value: caseItem.claimFiledAt,
      kind: 'milestone' as CourtTimelineKind,
      state: getCourtTimelineState(caseItem.claimFiledAt),
    },
    {
      id: 'next-hearing',
      label: 'Ближайшее заседание',
      value: caseItem.nextHearingAt,
      description: [caseItem.courtroom, caseItem.judgeName].filter(Boolean).join(' · '),
      kind: 'hearing' as CourtTimelineKind,
      state: getCourtTimelineState(caseItem.nextHearingAt),
    },
    {
      id: 'decision',
      label: 'Решение суда',
      value: caseItem.courtDecisionAt,
      kind: 'milestone' as CourtTimelineKind,
      state: getCourtTimelineState(caseItem.courtDecisionAt),
    },
    {
      id: 'appeal',
      label: 'Апелляция',
      value: caseItem.appealDeadlineAt,
      kind: 'deadline' as CourtTimelineKind,
      state: getCourtTimelineState(caseItem.appealDeadlineAt),
    },
    {
      id: 'cassation',
      label: 'Кассация',
      value: caseItem.cassationDeadlineAt,
      kind: 'deadline' as CourtTimelineKind,
      state: getCourtTimelineState(caseItem.cassationDeadlineAt),
    },
    {
      id: 'motion',
      label: 'Ходатайства',
      value: caseItem.motionDeadlineAt,
      kind: 'deadline' as CourtTimelineKind,
      state: getCourtTimelineState(caseItem.motionDeadlineAt),
    },
    {
      id: 'objection',
      label: 'Возражения',
      value: caseItem.objectionDeadlineAt,
      kind: 'deadline' as CourtTimelineKind,
      state: getCourtTimelineState(caseItem.objectionDeadlineAt),
    },
    ...logs.map((log) => ({
      id: `sync-${log.id}`,
      label: COURT_SYNC_STATUS_LABELS[log.status] ?? log.status,
      value: log.occurredAt,
      description: log.message,
      kind: 'sync' as CourtTimelineKind,
      state: getCourtTimelineState(log.occurredAt),
    })),
  ].filter((entry) => Boolean(entry.value) || entry.kind === 'sync');

  return entries.sort((left, right) => {
    const leftTime = Date.parse(left.value ?? '');
    const rightTime = Date.parse(right.value ?? '');
    const normalizedLeft = Number.isFinite(leftTime) ? leftTime : Number.MAX_SAFE_INTEGER;
    const normalizedRight = Number.isFinite(rightTime) ? rightTime : Number.MAX_SAFE_INTEGER;
    return normalizedLeft - normalizedRight;
  });
}

function CourtTimeline({ entries }: { entries: CourtTimelineEntry[] }) {
  if (entries.length === 0) {
    return (
      <div className="mt-3 rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
        Судебные даты и события по делу пока не заполнены.
      </div>
    );
  }

  return (
    <div className="mt-4 space-y-0">
      {entries.map((entry, index) => (
        <div key={entry.id} className="grid grid-cols-[28px_1fr] gap-3">
          <div className="relative flex justify-center">
            <span className={`mt-1 h-3 w-3 rounded-full border-2 border-white shadow ring-2 ${entry.state === 'future' ? 'bg-cyan-500 ring-cyan-100' : entry.state === 'today' ? 'bg-amber-500 ring-amber-100' : 'bg-slate-400 ring-slate-100'}`} />
            {index < entries.length - 1 ? <span className="absolute top-5 bottom-0 w-px bg-slate-200" /> : null}
          </div>
          <article className="pb-4">
            <div className="rounded-lg border border-slate-100 bg-white p-3 shadow-sm">
              <div className="flex flex-wrap items-center gap-2">
                <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${COURT_TIMELINE_STATE_CLASSES[entry.state]}`}>
                  {COURT_TIMELINE_STATE_LABELS[entry.state]}
                </span>
                <span className="font-semibold text-slate-900">{entry.label}</span>
                {entry.value ? <span className="text-xs text-slate-400">{formatRuDateTime(entry.value)}</span> : null}
              </div>
              {entry.description ? <p className="mt-2 whitespace-pre-wrap text-sm leading-6 text-slate-600">{entry.description}</p> : null}
            </div>
          </article>
        </div>
      ))}
    </div>
  );
}

export function CourtSourceProvenance({
  item,
}: {
  item: {
    sourceType?: string | null;
    sourceUrl?: string | null;
    sourceCheckedAt?: string | null;
    sourceConfidence?: string | null;
  };
}) {
  const sourceType = item.sourceType?.trim().toLowerCase();
  if ((!sourceType || sourceType === 'manual') && !item.sourceUrl && !item.sourceCheckedAt) {
    return null;
  }

  const sourceLabel = sourceType ? COURT_SOURCE_LABELS[sourceType] ?? 'Внешний источник' : 'Внешний источник';
  const confidenceLabel = item.sourceConfidence === 'official-source'
    ? 'официальный источник'
    : item.sourceConfidence === 'manual'
      ? 'внесено вручную'
      : item.sourceConfidence || 'достоверность не указана';
  let sourceHref: string | null = null;
  if (item.sourceUrl) {
    try {
      const parsed = new URL(item.sourceUrl);
      sourceHref = parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.toString() : null;
    } catch {
      sourceHref = null;
    }
  }

  return (
    <div className="mt-2 flex flex-wrap items-center gap-1.5 text-xs text-slate-500">
      <span className="rounded-full border border-cyan-200 bg-cyan-50 px-2 py-0.5 font-medium text-cyan-800">
        {sourceLabel}
      </span>
      <span>{confidenceLabel}</span>
      {item.sourceCheckedAt ? <span>· проверено {formatRuDateTime(item.sourceCheckedAt)}</span> : null}
      {sourceHref ? (
        <a href={sourceHref} target="_blank" rel="noreferrer" className="font-medium text-blue-600 hover:underline">
          Открыть источник
        </a>
      ) : null}
    </div>
  );
}

function CourtPanel({
  initialOverview,
  onOverviewChange,
  caseItem,
  courtDirectory,
  courtInstances,
  courtInstancesLoading,
  courtInstancesError,
  activeCourtInstance,
  courtInstanceDraft,
  courtComplaintDraft,
  editingCourtComplaint,
  courtEvents,
  courtDirectoryLoading,
  courtEventsLoading,
  courtEventsError,
  documents,
  courtParties,
  courtPartiesLoading,
  courtEventTypeDraft,
  courtEventTitleDraft,
  courtEventScheduledAtDraft,
  courtEventStatusDraft,
  courtEventCourtIdDraft,
  courtEventResultDraft,
  courtEventDocumentIdDraft,
  courtPartyNameDraft,
  courtPartySideDraft,
  courtPartyRoleDraft,
  courtPartyRepresentativeDraft,
  courtPartyNotesDraft,
  proceduralDeadlines,
  proceduralDeadlinesLoading,
  proceduralDeadlinesError,
  deadlineTypeDraft,
  deadlineTitleDraft,
  deadlineDueAtDraft,
  deadlineWarningDaysDraft,
  deadlineDocumentIdDraft,
  deadlineCourtEventIdDraft,
  deadlineNotesDraft,
  deadlineCalculationPreview,
  logs,
  logsLoading,
  courtSyncSearchResults,
  lastCourtSyncResult,
  savingCourtInstance,
  savingCourtComplaint,
  archivingCourtComplaintId,
  savingCourtEvent,
  savingCourtParty,
  archivingCourtPartyId,
  savingProceduralDeadline,
  calculatingProceduralDeadline,
  closingProceduralDeadlineId,
  onRunCourtSync,
  onRecordManualCourtCheck,
  syncingCourt,
  searchingCourtSync,
  bindingCourtSyncSource,
  onActiveCourtInstanceChange,
  onCourtInstanceDraftChange,
  onSearchCourtSyncCases,
  onBindCourtSyncCase,
  onCourtComplaintDraftChange,
  onSaveCourtInstance,
  onSaveCourtComplaint,
  onEditCourtComplaint,
  onCancelCourtComplaintEdit,
  onArchiveCourtComplaint,
  onCourtEventTypeChange,
  onCourtEventTitleChange,
  onCourtEventScheduledAtChange,
  onCourtEventStatusChange,
  onCourtEventCourtIdChange,
  onCourtEventResultChange,
  onCourtEventDocumentIdChange,
  onAddCourtEvent,
  onCourtPartyNameChange,
  onCourtPartySideChange,
  onCourtPartyRoleChange,
  onCourtPartyRepresentativeChange,
  onCourtPartyNotesChange,
  onAddCourtParty,
  onArchiveCourtParty,
  onDeadlineTypeChange,
  onDeadlineTitleChange,
  onDeadlineDueAtChange,
  onDeadlineWarningDaysChange,
  onDeadlineDocumentIdChange,
  onDeadlineCourtEventIdChange,
  onDeadlineNotesChange,
  onCalculateProceduralDeadline,
  onAddProceduralDeadline,
  onCloseProceduralDeadline,
}: {
  initialOverview: boolean;
  onOverviewChange: (value: boolean) => void;
  caseItem: ClientCase;
  courtDirectory: ClientCaseCourt[];
  courtInstances: ClientCaseCourtInstance[];
  courtInstancesLoading: boolean;
  courtInstancesError: string | null;
  activeCourtInstance: string;
  courtInstanceDraft: CourtInstanceDraft;
  courtComplaintDraft: CourtComplaintDraft;
  editingCourtComplaint: ClientCaseCourtComplaint | null;
  courtEvents: ClientCaseCourtEvent[];
  courtDirectoryLoading: boolean;
  courtEventsLoading: boolean;
  courtEventsError: string | null;
  documents: ClientCaseDocument[];
  courtParties: ClientCaseCourtParty[];
  courtPartiesLoading: boolean;
  courtEventTypeDraft: string;
  courtEventTitleDraft: string;
  courtEventScheduledAtDraft: string;
  courtEventStatusDraft: string;
  courtEventCourtIdDraft: string;
  courtEventResultDraft: string;
  courtEventDocumentIdDraft: string;
  courtPartyNameDraft: string;
  courtPartySideDraft: string;
  courtPartyRoleDraft: string;
  courtPartyRepresentativeDraft: string;
  courtPartyNotesDraft: string;
  proceduralDeadlines: ClientCaseProceduralDeadline[];
  proceduralDeadlinesLoading: boolean;
  proceduralDeadlinesError: string | null;
  deadlineTypeDraft: string;
  deadlineTitleDraft: string;
  deadlineDueAtDraft: string;
  deadlineWarningDaysDraft: string;
  deadlineDocumentIdDraft: string;
  deadlineCourtEventIdDraft: string;
  deadlineNotesDraft: string;
  deadlineCalculationPreview: ProceduralDeadlineCalculationResult | null;
  logs: ClientCaseCourtSyncLog[];
  logsLoading: boolean;
  courtSyncSearchResults: ClientCaseCourtSyncSearchResult[];
  lastCourtSyncResult: ClientCaseCourtSyncRun | null;
  savingCourtInstance: boolean;
  savingCourtComplaint: boolean;
  archivingCourtComplaintId: number | null;
  savingCourtEvent: boolean;
  savingCourtParty: boolean;
  archivingCourtPartyId: number | null;
  savingProceduralDeadline: boolean;
  calculatingProceduralDeadline: boolean;
  closingProceduralDeadlineId: number | null;
  onRunCourtSync: () => void;
  onRecordManualCourtCheck: () => void;
  syncingCourt: boolean;
  searchingCourtSync: boolean;
  bindingCourtSyncSource: boolean;
  onActiveCourtInstanceChange: (value: string) => void;
  onCourtInstanceDraftChange: (draft: CourtInstanceDraft) => void;
  onSearchCourtSyncCases: () => void;
  onBindCourtSyncCase: (result: ClientCaseCourtSyncSearchResult) => void;
  onCourtComplaintDraftChange: (draft: CourtComplaintDraft) => void;
  onSaveCourtInstance: () => void;
  onSaveCourtComplaint: () => void;
  onEditCourtComplaint: (complaint: ClientCaseCourtComplaint) => void;
  onCancelCourtComplaintEdit: () => void;
  onArchiveCourtComplaint: (complaint: ClientCaseCourtComplaint) => void;
  onCourtEventTypeChange: (value: string) => void;
  onCourtEventTitleChange: (value: string) => void;
  onCourtEventScheduledAtChange: (value: string) => void;
  onCourtEventStatusChange: (value: string) => void;
  onCourtEventCourtIdChange: (value: string) => void;
  onCourtEventResultChange: (value: string) => void;
  onCourtEventDocumentIdChange: (value: string) => void;
  onAddCourtEvent: () => void;
  onCourtPartyNameChange: (value: string) => void;
  onCourtPartySideChange: (value: string) => void;
  onCourtPartyRoleChange: (value: string) => void;
  onCourtPartyRepresentativeChange: (value: string) => void;
  onCourtPartyNotesChange: (value: string) => void;
  onAddCourtParty: () => void;
  onArchiveCourtParty: (party: ClientCaseCourtParty) => void;
  onDeadlineTypeChange: (value: string) => void;
  onDeadlineTitleChange: (value: string) => void;
  onDeadlineDueAtChange: (value: string) => void;
  onDeadlineWarningDaysChange: (value: string) => void;
  onDeadlineDocumentIdChange: (value: string) => void;
  onDeadlineCourtEventIdChange: (value: string) => void;
  onDeadlineNotesChange: (value: string) => void;
  onCalculateProceduralDeadline: () => void;
  onAddProceduralDeadline: () => void;
  onCloseProceduralDeadline: (deadline: ClientCaseProceduralDeadline) => void;
}) {
  const hasCourtBinding = Boolean(caseItem.courtName || caseItem.courtCaseNumber || caseItem.judicialUid || caseItem.courtUrl);
  const isPretrialCase = isPretrialCaseStage(caseItem);
  const deadlines = [
    ['Подача заявления', caseItem.claimFiledAt],
    ['Ближайшее заседание', caseItem.nextHearingAt],
    ['Решение суда', caseItem.courtDecisionAt],
    ['Апелляция', caseItem.appealDeadlineAt],
    ['Кассация', caseItem.cassationDeadlineAt],
    ['Ходатайства', caseItem.motionDeadlineAt],
    ['Возражения', caseItem.objectionDeadlineAt],
  ].filter(([, value]) => Boolean(value)) as Array<[string, string]>;
  const activeProceduralDeadlines = proceduralDeadlines.filter((item) => item.status !== 'closed' && item.status !== 'cancelled' && item.status !== 'archived');
  const inactiveProceduralDeadlines = proceduralDeadlines.filter((item) => item.status === 'closed' || item.status === 'cancelled' || item.status === 'archived');
  const courtTimeline = buildCourtTimeline(caseItem, logs);
  const selectedCourtInstance = courtInstances.find((item) => item.instance === activeCourtInstance);
  const selectedCourtInstanceMeta = COURT_INSTANCE_TABS.find((item) => item.value === activeCourtInstance) ?? COURT_INSTANCE_TABS[0];
  const selectedCourtEvents = courtEvents.filter((event) => event.courtInstance === activeCourtInstance);
  const selectedActiveCourtEvents = selectedCourtEvents.filter((event) => (event.movementStatus || '').trim().toLowerCase() !== 'archived');
  const selectedArchivedCourtEventsCount = selectedCourtEvents.length - selectedActiveCourtEvents.length;
  const courtDirectoryForInstance = courtDirectory.filter((court) => court.instance === activeCourtInstance || (activeCourtInstance === 'supreme' && court.instance === 'supervision'));
  const [courtSafeModeExpanded, setCourtSafeModeExpanded] = useState(false);
  const [courtEditMode, setCourtEditMode] = useState(!initialOverview);
  const changeCourtEditMode = (editing: boolean) => {
    setCourtEditMode(editing);
    onOverviewChange(!editing);
  };
  const hasCourtInstanceData = courtInstances.some((instance) => Boolean(
    instance.courtName
    || instance.courtCaseNumber
    || instance.judicialUid
    || instance.courtUrl
    || instance.judgeName
    || instance.courtroom
    || instance.secretaryName
    || instance.notes
    || (instance.complaints?.length ?? 0),
  ));
  const hasStoredCourtData = hasCourtBinding
    || hasCourtInstanceData
    || deadlines.length > 0
    || courtEvents.length > 0
    || proceduralDeadlines.length > 0
    || logs.length > 0
    || courtParties.length > 0;
  const processSummaryLoading = courtEventsLoading || courtInstancesLoading || proceduralDeadlinesLoading;
  const processSummaryErrors = [courtEventsError, courtInstancesError, proceduralDeadlinesError]
    .filter((message): message is string => Boolean(message));
  const showPretrialSafeMode = isPretrialCase
    && !hasStoredCourtData
    && !processSummaryLoading
    && processSummaryErrors.length === 0
    && !courtSafeModeExpanded;
  const courtSyncChangeGroups = [
    {
      key: 'new',
      title: 'Новые',
      items: lastCourtSyncResult?.newItems ?? [],
      className: 'border-emerald-200 bg-emerald-50 text-emerald-900',
    },
    {
      key: 'changed',
      title: 'Изменённые',
      items: lastCourtSyncResult?.changedItems ?? [],
      className: 'border-blue-200 bg-blue-50 text-blue-900',
    },
    {
      key: 'removed',
      title: 'Исчезли из источника',
      items: lastCourtSyncResult?.removedItems ?? [],
      className: 'border-slate-300 bg-slate-50 text-slate-800',
    },
    {
      key: 'conflict',
      title: 'Конфликты',
      items: lastCourtSyncResult?.conflictItems ?? [],
      className: 'border-amber-300 bg-amber-50 text-amber-950',
    },
  ].filter((group) => group.items.length > 0);

  if (showPretrialSafeMode) {
    return (
      <div className="space-y-4">
        <section className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-950">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
            <div className="min-w-0">
              <div className="inline-flex items-center gap-2 rounded-full bg-white px-2.5 py-1 text-xs font-semibold uppercase tracking-[0.12em] text-emerald-700">
                <Check className="h-3.5 w-3.5" /> Внесудебный режим
              </div>
              <h2 className="mt-3 text-lg font-semibold text-emerald-950">Судебные данные сейчас не обязательны</h2>
              <p className="mt-1 max-w-3xl text-sm text-emerald-900">
                Дело отмечено как внесудебное, поэтому CRM не просит заполнять суд, номер дела, инстанции, заседания и процессуальные сроки. Если дело перейдет в суд, судебный блок можно открыть вручную или сменить этап дела.
              </p>
            </div>
            <button
              type="button"
              onClick={() => setCourtSafeModeExpanded(true)}
              className="inline-flex items-center justify-center gap-2 rounded-lg border border-emerald-300 bg-white px-4 py-2 text-sm font-semibold text-emerald-800 hover:bg-emerald-100">
              <Gavel className="h-4 w-4" /> Заполнить судебный блок
            </button>
          </div>
        </section>
      </div>
    );
  }

  if (!courtEditMode) {
    const overviewInstance = selectedCourtInstance;
    const overviewEvents = selectedActiveCourtEvents
      .slice()
      .sort((left, right) => new Date(left.scheduledAt).getTime() - new Date(right.scheduledAt).getTime());
    const latestEvent = overviewEvents.at(-1);
    const processHistory = courtEvents
      .slice()
      .sort((left, right) => new Date(right.scheduledAt).getTime() - new Date(left.scheduledAt).getTime())
      .slice(0, 20);
    const populatedInstances = COURT_INSTANCE_TABS.filter((tab) => {
      const instanceData = courtInstances.find((item) => item.instance === tab.value);
      return Boolean(
        instanceData?.courtName
        || instanceData?.courtCaseNumber
        || instanceData?.courtUrl
        || courtEvents.some((event) => event.courtInstance === tab.value),
      );
    });
    const visibleInstances = populatedInstances.length > 0 ? populatedInstances : [selectedCourtInstanceMeta];
    const ownerName = caseItem.client?.name || caseItem.legalEntity?.shortName || caseItem.legalEntity?.fullName || 'Владелец не указан';
    const successfulSourceChecks = logs
      .filter((log) => (log.status || '').trim().toLowerCase() === 'external-sync-completed')
      .map((log) => log.occurredAt);
    if (lastCourtSyncResult?.status === 'external-sync-completed') {
      successfulSourceChecks.push(lastCourtSyncResult.checkedAt);
    }
    const processSummary = buildCourtCaseProcessSummary(
      caseItem,
      courtEvents,
      courtInstances,
      activeProceduralDeadlines,
      { successfulSourceChecks },
    );

    return (
      <div className="space-y-3">
        <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-2">
                <span className="rounded-full border border-blue-200 bg-blue-50 px-2.5 py-1 text-xs font-semibold text-blue-700">{CASE_STATUS_OPTIONS.find((option) => option.value === caseItem.status)?.label || caseItem.status}</span>
                <span className="rounded-full border border-violet-200 bg-violet-50 px-2.5 py-1 text-xs font-semibold text-violet-700">{selectedCourtInstanceMeta.label}</span>
                {caseItem.isUnderControl ? <span className="rounded-full border border-rose-200 bg-rose-50 px-2.5 py-1 text-xs font-semibold text-rose-700">На особом контроле</span> : null}
              </div>
              <h2 className="mt-2 text-lg font-bold text-slate-950">Сводка по судебному делу</h2>
              <p className="mt-0.5 text-sm text-slate-500">Главные реквизиты, инстанции и движение дела без лишних полей.</p>
            </div>
            <div className="flex flex-wrap gap-2">
              <button type="button" onClick={() => void onRunCourtSync()} disabled={syncingCourt} className="inline-flex items-center gap-2 rounded-lg border border-cyan-200 px-3 py-2 text-sm font-semibold text-cyan-700 hover:bg-cyan-50 disabled:opacity-50">{syncingCourt ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Проверить источник</button>
              <button type="button" onClick={() => changeCourtEditMode(true)} className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-3 py-2 text-sm font-semibold text-white hover:bg-slate-800"><Pencil className="h-4 w-4" /> Редактировать данные</button>
            </div>
          </div>
        </section>

        {lastCourtSyncResult?.message ? <div className={`flex items-start gap-2 rounded-xl border p-3 text-sm ${lastCourtSyncResult.status === 'external-sync-error' ? 'border-rose-200 bg-rose-50 text-rose-800' : 'border-emerald-200 bg-emerald-50 text-emerald-800'}`}><Check className="mt-0.5 h-4 w-4 shrink-0" /><span>{buildCourtSyncOverviewMessage(lastCourtSyncResult)}</span></div> : null}

        <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <h3 className="font-bold text-slate-900">Что происходит по делу</h3>
              <p className="mt-0.5 text-xs text-slate-500">CRM определяет состояние по официальному движению, жалобам и связанным инстанциям.</p>
            </div>
            <span
              className={`rounded-full px-2.5 py-1 text-xs font-semibold ${processSummaryErrors.length > 0 ? 'bg-amber-100 text-amber-800' : processSummaryLoading ? 'bg-blue-50 text-blue-700' : processSummary.sourceCheckedAt ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-600'}`}
              title={processSummary.sourceCheckedAt ? 'Дата последней подтверждённой проверки судебного источника' : 'Сводка построена по сведениям, сохранённым в карточке дела'}
            >
              {processSummaryErrors.length > 0
                ? 'Требуется обновление'
                : processSummaryLoading
                  ? 'Проверка данных'
                  : processSummary.sourceCheckedAt
                    ? `Источник проверен ${formatRuDateTime(processSummary.sourceCheckedAt)}`
                    : 'По данным карточки'}
            </span>
          </div>
          {processSummaryLoading ? (
            <div aria-label="Проверяем судебные данные" className="mt-3 flex items-start gap-3 rounded-lg border border-blue-200 bg-blue-50 p-3 text-blue-900">
              <Loader2 className="mt-0.5 h-4 w-4 shrink-0 animate-spin text-blue-600" />
              <div><div className="text-sm font-semibold">Проверяем судебные данные</div><p className="mt-0.5 text-xs text-blue-700">Выводы о решении, обжаловании и рисках появятся после загрузки движения, инстанций и сроков.</p></div>
            </div>
          ) : processSummaryErrors.length > 0 ? (
            <div role="alert" className="mt-3 rounded-lg border border-amber-200 bg-amber-50 p-3 text-amber-950">
              <div className="text-sm font-semibold">Автоматическая оценка недоступна</div>
              <div className="mt-1 text-xs leading-5 text-amber-800">{processSummaryErrors.join(' ')} Нажмите «Обновить» в верхней части карточки.</div>
            </div>
          ) : (
            <div className="mt-3 grid gap-2 md:grid-cols-2">
              {[
                ['Решение', processSummary.decision],
                ['Обжалование', processSummary.appeal],
                ['Следующее действие', processSummary.nextAction],
                ['Риск', processSummary.risk],
              ].map(([title, item]) => {
                const summaryItem = item as typeof processSummary.decision;
                return (
                  <div key={title as string} className={`rounded-lg border px-3 py-2.5 ${courtProcessSummaryClasses(summaryItem.tone)}`}>
                    <div className="text-[11px] font-semibold uppercase tracking-[0.1em] opacity-70">{title as string}</div>
                    <div className="mt-1 text-sm font-bold">{summaryItem.label}</div>
                    <div className="mt-1 text-xs leading-5 opacity-80">{summaryItem.detail}</div>
                    {summaryItem.date ? <div className="mt-1 text-xs font-semibold">{summaryItem.dateLabel || 'Дата'}: {formatCourtSchedule(summaryItem.date, summaryItem.allDay)}</div> : null}
                    <div className="mt-1.5 border-t border-current/10 pt-1.5 text-[11px] font-medium opacity-65">Основание: {summaryItem.basis}</div>
                  </div>
                );
              })}
            </div>
          )}
        </section>

        <div className="grid gap-3 2xl:grid-cols-[minmax(0,1fr)_280px]">
          <div className="min-w-0 space-y-3">
            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
              <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                {[
                  ['Владелец', ownerName],
                  ['Номер дела', overviewInstance?.courtCaseNumber || caseItem.courtCaseNumber || caseItem.caseNumber || '—'],
                  ['UID', overviewInstance?.judicialUid || caseItem.judicialUid || '—'],
                  ['Суд', overviewInstance?.courtName || caseItem.courtName || '—'],
                  ['Судья', overviewInstance?.judgeName || caseItem.judgeName || '—'],
                  ['Ближайшее заседание', formatRuDateTime(caseItem.nextHearingAt) || 'Не назначено'],
                  ['Истец / заявитель', caseItem.claimant || '—'],
                  ['Ответчик / должник', caseItem.defendant || '—'],
                  ['Текущий результат', latestEvent?.resultSummary || latestEvent?.movementStatusLabel || latestEvent?.movementStatus || caseItem.stage || '—'],
                ].map(([label, value]) => <div key={label} className="min-w-0 rounded-lg bg-slate-50 px-3 py-2"><div className="text-[11px] font-semibold uppercase tracking-[0.1em] text-slate-400">{label}</div><div className={`mt-1 break-words text-sm font-semibold text-slate-800 ${label === 'UID' ? 'font-mono text-xs text-cyan-800' : ''}`}>{value}</div></div>)}
              </div>
              {(overviewInstance?.courtUrl || caseItem.courtUrl) ? <a href={overviewInstance?.courtUrl || caseItem.courtUrl || '#'} target="_blank" rel="noreferrer" className="mt-3 inline-flex items-center gap-2 text-sm font-semibold text-blue-700 hover:underline"><ExternalLink className="h-4 w-4" /> Открыть официальную карточку суда</a> : null}
            </section>

            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
              <div className="flex flex-wrap items-center justify-between gap-2"><div><h3 className="font-bold text-slate-900">Инстанции и судебные карточки</h3><p className="mt-0.5 text-xs text-slate-500">Карточки одного дела объединяются по UID. Выберите инстанцию, чтобы увидеть её движение.</p></div><span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">{populatedInstances.length} заполнено</span></div>
              <div className="mt-3 flex gap-2 overflow-x-auto pb-1">{visibleInstances.map((tab) => {
                const instanceData = courtInstances.find((item) => item.instance === tab.value);
                return <button key={tab.value} type="button" onClick={() => onActiveCourtInstanceChange(tab.value)} className={`min-w-[170px] rounded-lg border px-3 py-2 text-left ${activeCourtInstance === tab.value ? 'border-blue-400 bg-blue-50 text-blue-800' : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'}`}><div className="text-sm font-bold">{tab.label}</div><div className="mt-0.5 truncate text-xs opacity-75">{instanceData?.courtCaseNumber || 'Номер не указан'}</div></button>;
              })}</div>
              <div className="mt-3 grid gap-2 rounded-lg border border-slate-200 bg-slate-50/60 p-3 sm:grid-cols-2"><div><div className="text-[11px] uppercase tracking-[0.1em] text-slate-400">Суд</div><div className="mt-1 text-sm font-semibold text-slate-800">{overviewInstance?.courtName || caseItem.courtName || 'Не указан'}</div></div><div><div className="text-[11px] uppercase tracking-[0.1em] text-slate-400">Номер карточки</div><div className="mt-1 text-sm font-semibold text-slate-800">{overviewInstance?.courtCaseNumber || caseItem.courtCaseNumber || 'Не указан'}</div></div><div><div className="text-[11px] uppercase tracking-[0.1em] text-slate-400">Судья</div><div className="mt-1 text-sm font-semibold text-slate-800">{overviewInstance?.judgeName || caseItem.judgeName || 'Не указан'}</div></div><div><div className="text-[11px] uppercase tracking-[0.1em] text-slate-400">Статус</div><div className="mt-1 text-sm font-semibold text-slate-800">{latestEvent?.movementStatusLabel || latestEvent?.movementStatus || caseItem.stage || 'Нет данных'}</div></div></div>
            </section>

            <section className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
              <div className="flex items-center justify-between gap-2 border-b border-slate-200 px-3 py-2.5"><div><h3 className="font-bold text-slate-900">Заседания и движения</h3><p className="text-xs text-slate-500">Только актуальные события выбранной инстанции</p></div><div className="flex flex-wrap items-center justify-end gap-2"><span className="text-xs font-semibold text-slate-500">Актуальных: {overviewEvents.length}</span>{selectedArchivedCourtEventsCount > 0 ? <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[11px] font-semibold text-slate-500">В истории: {selectedArchivedCourtEventsCount}</span> : null}</div></div>
              {courtEventsLoading ? <div className="flex items-center justify-center p-8"><Loader2 className="h-5 w-5 animate-spin text-blue-600" /></div> : courtEventsError ? <div role="alert" className="m-3 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">{courtEventsError}</div> : overviewEvents.length === 0 ? <div className="p-6 text-center text-sm text-slate-500">По этой инстанции событий пока нет.</div> : <>
                <div data-testid="court-event-cards" className="divide-y divide-slate-100 xl:hidden">
                  {overviewEvents.map((event) => (
                    <article key={event.id} className="p-3">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <time className="text-sm font-bold text-blue-700">{formatCourtEventSchedule(event) || 'Дата не указана'}</time>
                        <span className={`rounded-full border px-2 py-0.5 text-[11px] font-semibold ${courtMovementStatusClasses(event.movementStatus)}`}>{event.movementStatusLabel || event.movementStatus || 'Без статуса'}</span>
                      </div>
                      <h4 className="mt-1.5 text-sm font-bold text-slate-900">{event.title}</h4>
                      {event.resultSummary || event.description ? <p className="mt-1 text-xs leading-5 text-slate-600">{event.resultSummary || event.description}</p> : null}
                      {event.courtroom ? <div className="mt-1.5 text-xs font-semibold text-slate-500">Зал: {event.courtroom}</div> : null}
                    </article>
                  ))}
                </div>
                <div className="hidden overflow-x-auto xl:block"><table className="min-w-full text-left text-xs"><thead className="bg-slate-50 text-[11px] uppercase tracking-[0.08em] text-slate-500"><tr><th className="px-3 py-2">Дата и время</th><th className="px-3 py-2">Событие</th><th className="px-3 py-2">Статус</th><th className="px-3 py-2">Результат</th><th className="px-3 py-2">Зал</th></tr></thead><tbody className="divide-y divide-slate-100">{overviewEvents.map((event) => <tr key={event.id} className="align-top"><td className="whitespace-nowrap px-3 py-2.5 font-semibold text-slate-700">{formatCourtEventSchedule(event) || '—'}</td><td className="min-w-[220px] px-3 py-2.5 text-slate-800">{event.title}</td><td className="px-3 py-2.5 text-slate-600">{event.movementStatusLabel || event.movementStatus || '—'}</td><td className="min-w-[180px] px-3 py-2.5 text-slate-600">{event.resultSummary || event.description || '—'}</td><td className="px-3 py-2.5 text-slate-600">{event.courtroom || '—'}</td></tr>)}</tbody></table></div>
              </>}
            </section>

            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
              <div className="flex items-center justify-between gap-2">
                <div><h3 className="font-bold text-slate-900">Лента процессуальной истории</h3><p className="text-xs text-slate-500">Актуальные и архивные изменения всех инстанций в обратном хронологическом порядке</p></div>
                <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">{courtEvents.length}</span>
              </div>
              {processHistory.length === 0 ? (
                <div className="mt-3 rounded-lg bg-slate-50 p-4 text-sm text-slate-500">Процессуальная история появится после синхронизации или добавления события.</div>
              ) : (
                <ol className="mt-3 space-y-0">
                  {processHistory.map((event, index) => {
                    const isArchived = (event.movementStatus || '').trim().toLowerCase() === 'archived';
                    return (
                      <li key={event.id} className={`relative grid grid-cols-[16px_minmax(0,1fr)] gap-3 pb-3 ${isArchived ? 'opacity-65' : ''}`}>
                        <div className="relative flex justify-center">
                          <span className={`mt-1.5 h-2.5 w-2.5 rounded-full ring-4 ${isArchived ? 'bg-slate-400 ring-slate-100' : 'bg-blue-500 ring-blue-50'}`} />
                          {index < processHistory.length - 1 ? <span className="absolute bottom-[-4px] top-4 w-px bg-slate-200" /> : null}
                        </div>
                        <div>
                          <div className={`flex flex-wrap items-center gap-2 text-xs font-semibold ${isArchived ? 'text-slate-500' : 'text-blue-700'}`}>
                            <span>{formatCourtEventSchedule(event) || 'Дата не указана'} · {event.courtInstanceLabel}</span>
                            {isArchived ? <span className="rounded-full bg-slate-100 px-2 py-0.5 text-[10px] uppercase tracking-wide text-slate-600">Архив источника</span> : null}
                          </div>
                          <div className={`mt-0.5 text-sm font-semibold ${isArchived ? 'text-slate-600 line-through decoration-slate-300' : 'text-slate-800'}`}>{event.title}</div>
                          {event.resultSummary || event.movementStatusLabel ? <div className="mt-0.5 text-xs text-slate-500">{event.resultSummary || event.movementStatusLabel}</div> : null}
                        </div>
                      </li>
                    );
                  })}
                </ol>
              )}
            </section>
          </div>

          <aside className="space-y-3">
            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"><div className="flex items-center justify-between"><h3 className="font-bold text-slate-900">Участники</h3><span className="text-xs font-semibold text-slate-500">{courtParties.filter((party) => !party.isArchived).length}</span></div>{courtPartiesLoading ? <Loader2 className="mt-3 h-4 w-4 animate-spin text-blue-600" /> : <div className="mt-2 space-y-2">{courtParties.filter((party) => !party.isArchived).slice(0, 6).map((party) => <div key={party.id} className="rounded-lg bg-slate-50 px-2.5 py-2"><div className="text-sm font-semibold text-slate-800">{party.displayName}</div><div className="text-xs text-slate-500">{party.sideLabel || party.processRole || 'Участник'}</div></div>)}{courtParties.filter((party) => !party.isArchived).length === 0 ? <div className="text-sm text-slate-500">Участники ещё не загружены.</div> : null}</div>}</section>
            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"><div className="flex items-center justify-between"><h3 className="font-bold text-slate-900">Файлы</h3><span className="text-xs font-semibold text-slate-500">{documents.length}</span></div><div className="mt-2 space-y-2">{documents.slice(0, 5).map((document) => <div key={document.id} className="flex items-start gap-2 rounded-lg bg-slate-50 px-2.5 py-2"><FileText className="mt-0.5 h-4 w-4 shrink-0 text-blue-600" /><div className="min-w-0"><div className="truncate text-sm font-semibold text-slate-800">{document.originalFileName || 'Документ'}</div><div className="text-xs text-slate-500">{document.status || document.documentType || 'Файл дела'}</div></div></div>)}{documents.length === 0 ? <div className="text-sm text-slate-500">Файлов пока нет.</div> : null}</div></section>
            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"><div className="flex items-center justify-between"><h3 className="font-bold text-slate-900">Процессуальные сроки</h3><span className="text-xs font-semibold text-slate-500">{activeProceduralDeadlines.length}</span></div>{proceduralDeadlinesLoading ? <Loader2 className="mt-3 h-4 w-4 animate-spin text-blue-600" /> : <div className="mt-2 space-y-2">{activeProceduralDeadlines.slice(0, 5).map((deadline) => <div key={deadline.id} className={`rounded-lg border px-2.5 py-2 ${deadline.isOverdue ? 'border-rose-200 bg-rose-50' : deadline.isWarning ? 'border-amber-200 bg-amber-50' : 'border-slate-200 bg-slate-50'}`}><div className="text-sm font-semibold text-slate-800">{deadline.title}</div><div className="mt-0.5 text-xs text-slate-500">до {formatCourtSchedule(deadline.dueAt, deadline.allDay) || '—'}</div></div>)}{activeProceduralDeadlines.length === 0 ? <div className="text-sm text-slate-500">Активных сроков нет.</div> : null}</div>}</section>
            <section className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"><h3 className="font-bold text-slate-900">Работа по делу</h3><div className="mt-2 grid grid-cols-2 gap-2"><div className="rounded-lg bg-blue-50 p-2.5"><div className="text-[11px] text-blue-600">Активные задачи</div><div className="mt-1 text-xl font-bold text-blue-900">{caseItem.openTaskCount ?? 0}</div></div><div className="rounded-lg bg-rose-50 p-2.5"><div className="text-[11px] text-rose-600">Просрочено</div><div className="mt-1 text-xl font-bold text-rose-900">{caseItem.overdueTaskCount ?? 0}</div></div></div><p className="mt-2 text-xs text-slate-500">Файлы, задачи, участники, переписка и доступ клиента доступны в соседних вкладках карточки.</p></section>
          </aside>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between rounded-xl border border-blue-200 bg-blue-50 p-3"><div><div className="text-sm font-semibold text-blue-900">Режим редактирования судебных данных</div><div className="text-xs text-blue-700">Здесь доступны привязка источника, ручные события, стороны, жалобы и процессуальные сроки.</div></div><button type="button" onClick={() => changeCourtEditMode(false)} className="rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-100">Вернуться к обзору</button></div>
      <div className="flex flex-col gap-3 rounded-xl border border-slate-200 p-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Судебный блок</h2>
          <div className="mt-1 text-sm text-slate-500">
            {hasCourtBinding ? 'Суд, номер дела, источник и процессуальные сроки по карточке.' : 'Судебная привязка пока не настроена.'}
          </div>
          {caseItem.judicialUid ? (
            <div className="mt-2 inline-flex flex-wrap items-center gap-2 rounded-lg border border-cyan-200 bg-cyan-50 px-3 py-2 text-sm text-cyan-950">
              <span className="font-semibold uppercase tracking-[0.08em] text-cyan-700">UID дела</span>
              <span className="font-mono font-bold">{caseItem.judicialUid}</span>
              <span className="text-xs text-cyan-700">единый для всех инстанций</span>
            </div>
          ) : null}
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => void onRunCourtSync()}
            disabled={syncingCourt}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-cyan-200 px-4 py-2 text-sm font-medium text-cyan-700 hover:bg-cyan-50 disabled:opacity-50">
            {syncingCourt ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Проверить источник
          </button>
          <button
            type="button"
            onClick={() => void onRecordManualCourtCheck()}
            disabled={syncingCourt}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-emerald-200 px-4 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-50 disabled:opacity-50">
            {syncingCourt ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} Зафиксировать вручную
          </button>
        </div>
      </div>

      {lastCourtSyncResult?.status === 'external-sync-completed' ? (
        courtSyncChangeGroups.length > 0 ? (
          <section className="rounded-xl border border-slate-200 p-3">
            <div className="mb-3">
              <h2 className="text-sm font-semibold text-slate-900">Результат последней проверки</h2>
              <p className="mt-0.5 text-xs text-slate-500">Исчезнувшие и конфликтующие записи не удаляются и не перезаписываются автоматически.</p>
            </div>
            <div className="grid gap-2 md:grid-cols-2 xl:grid-cols-4">
              {courtSyncChangeGroups.map((group) => (
                <div key={group.key} className={`rounded-lg border p-3 ${group.className}`}>
                  <div className="text-xs font-semibold uppercase tracking-[0.08em]">{group.title} · {group.items.length}</div>
                  <ul className="mt-2 space-y-1 text-xs">
                    {group.items.slice(0, 5).map((item) => <li key={item} className="break-words">• {item}</li>)}
                    {group.items.length > 5 ? <li>Ещё: {group.items.length - 5}</li> : null}
                  </ul>
                </div>
              ))}
            </div>
          </section>
        ) : (
          <div className="rounded-xl border border-emerald-200 bg-emerald-50 p-3 text-sm font-medium text-emerald-800">
            Судебный источник проверен: новых изменений и конфликтов нет.
          </div>
        )
      ) : null}

      <section className="rounded-xl border border-slate-200 p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Поиск судебной карточки</h2>
            <div className="mt-1 text-sm text-slate-500">{selectedCourtInstanceMeta.label}</div>
          </div>
          <button
            type="button"
            onClick={() => void onSearchCourtSyncCases()}
            disabled={searchingCourtSync || bindingCourtSyncSource}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
            {searchingCourtSync ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />} Найти
          </button>
        </div>
        <div className="mt-3 grid gap-3 lg:grid-cols-[minmax(0,1.2fr)_minmax(200px,0.5fr)_minmax(240px,0.6fr)]">
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Ссылка на источник</span>
            <input
              value={courtInstanceDraft.courtUrl}
              onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, courtUrl: event.target.value })}
              placeholder="https://...sudrf.ru/..."
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm"
            />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Номер дела в суде</span>
            <input
              value={courtInstanceDraft.courtCaseNumber}
              onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, courtCaseNumber: event.target.value })}
              placeholder="33а-3390/2026"
              className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm"
            />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">UID судебного дела</span>
            <input
              value={courtInstanceDraft.judicialUid}
              onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, judicialUid: event.target.value })}
              placeholder="63RS0018-01-2025-003508-91"
              className="w-full rounded-lg border border-cyan-200 bg-cyan-50/40 px-3 py-2 font-mono text-sm"
            />
          </label>
        </div>
        {courtSyncSearchResults.length > 0 ? (
          <div className="mt-3 space-y-2">
            {courtSyncSearchResults.map((result) => (
              <div key={`${result.providerKey}-${result.sourceUrl}-${result.courtCaseNumber || result.judicialUid}`} className="rounded-lg border border-blue-100 bg-blue-50/40 p-3">
                <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-semibold text-slate-900">{result.title || result.courtName || result.courtCaseNumber || 'Судебная карточка'}</span>
                      <span className="rounded-full bg-white px-2 py-0.5 text-xs font-semibold text-blue-700">{result.instanceLabel}</span>
                    </div>
                    {result.summary ? <p className="mt-1 text-sm text-slate-600">{result.summary}</p> : null}
                    <div className="mt-2 flex flex-wrap gap-2 text-xs text-slate-500">
                      {result.courtCaseNumber ? <span>№ {result.courtCaseNumber}</span> : null}
                      {result.judicialUid ? <span>UID {result.judicialUid}</span> : null}
                      {result.sourceUrl ? <span className="max-w-full truncate">{result.sourceUrl}</span> : null}
                    </div>
                    {result.differences.length > 0 ? (
                      <div className={`mt-3 rounded-lg border p-3 ${result.requiresReview ? 'border-amber-200 bg-amber-50' : 'border-emerald-200 bg-emerald-50'}`}>
                        <div className={`text-xs font-semibold uppercase tracking-[0.08em] ${result.requiresReview ? 'text-amber-800' : 'text-emerald-800'}`}>
                          {result.requiresReview ? 'Требуется проверка изменений' : 'Будут заполнены пустые поля'}
                        </div>
                        <div className="mt-2 space-y-2">
                          {result.differences.map((difference) => (
                            <div key={difference.field} className="grid gap-1 text-xs sm:grid-cols-[minmax(8rem,0.7fr)_minmax(0,1fr)_auto_minmax(0,1fr)] sm:items-center">
                              <span className="font-semibold text-slate-700">{difference.label}</span>
                              <span className="break-words text-slate-500">{difference.currentValue || 'Не заполнено'}</span>
                              <span className="hidden text-slate-400 sm:inline">→</span>
                              <span className="break-words font-medium text-slate-900">{difference.sourceValue}</span>
                            </div>
                          ))}
                        </div>
                      </div>
                    ) : (
                      <div className="mt-3 text-xs font-medium text-emerald-700">Данные карточки совпадают с делом.</div>
                    )}
                  </div>
                  <button
                    type="button"
                    onClick={() => void onBindCourtSyncCase(result)}
                    disabled={bindingCourtSyncSource || !result.canFetch}
                    className="inline-flex shrink-0 items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
                    {bindingCourtSyncSource ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} Привязать
                  </button>
                </div>
              </div>
            ))}
          </div>
        ) : null}
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Инстанции судов</h2>
            <div className="mt-1 text-sm text-slate-500">{selectedCourtInstanceMeta.hint}</div>
          </div>
          {courtInstancesLoading ? <span className="inline-flex items-center gap-2 text-sm text-slate-500"><Loader2 className="h-4 w-4 animate-spin" /> Загрузка</span> : courtInstancesError ? <span role="alert" className="text-sm font-semibold text-amber-700">{courtInstancesError}</span> : null}
        </div>
        <div className="mt-4 grid gap-2 md:grid-cols-4">
          {COURT_INSTANCE_TABS.map((tab) => {
            const item = courtInstances.find((entry) => entry.instance === tab.value);
            return (
              <button
                key={tab.value}
                type="button"
                onClick={() => onActiveCourtInstanceChange(tab.value)}
                className={`rounded-lg border px-3 py-2 text-left text-sm transition ${activeCourtInstance === tab.value ? 'border-blue-500 bg-blue-50 text-blue-800' : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'}`}>
                <span className="block font-semibold">{tab.label}</span>
                <span className="mt-1 block truncate text-xs text-slate-500">{item?.courtCaseNumber || item?.courtName || 'Не заполнено'}</span>
                {item?.judicialUid ? <span className="mt-0.5 block truncate font-mono text-[11px] text-cyan-700">UID {item.judicialUid}</span> : null}
              </button>
            );
          })}
        </div>
        <div className="mt-4 grid gap-4 xl:grid-cols-[1.1fr_0.9fr]">
          <div className="rounded-lg border border-slate-100 bg-slate-50 p-3">
            <div className="grid gap-3 lg:grid-cols-2">
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Справочник суда</span>
                <SystemSelect
                  value={courtInstanceDraft.courtId}
                  onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, courtId: event.target.value })}
                  disabled={courtDirectoryLoading}
                  className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm disabled:bg-slate-50">
                  <option value="">{courtDirectoryLoading ? 'Загрузка...' : 'Без справочника'}</option>
                  {courtDirectoryForInstance.map((court) => (
                    <option key={court.id} value={court.id}>{court.name}</option>
                  ))}
                </SystemSelect>
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Суд</span>
                <input value={courtInstanceDraft.courtName} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, courtName: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Номер дела</span>
                <input value={courtInstanceDraft.courtCaseNumber} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, courtCaseNumber: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">UID дела</span>
                <input value={courtInstanceDraft.judicialUid} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, judicialUid: event.target.value })} className="w-full rounded-lg border border-cyan-200 bg-cyan-50/40 px-3 py-2 font-mono text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Судья</span>
                <input value={courtInstanceDraft.judgeName} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, judgeName: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Зал / кабинет</span>
                <input value={courtInstanceDraft.courtroom} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, courtroom: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Секретарь / контакты</span>
                <input value={courtInstanceDraft.secretaryName} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, secretaryName: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700 lg:col-span-2">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Ссылка на карточку дела</span>
                <input value={courtInstanceDraft.courtUrl} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, courtUrl: event.target.value })} placeholder="https://..." className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700 lg:col-span-2">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Заметки</span>
                <input value={courtInstanceDraft.notes} onChange={(event) => onCourtInstanceDraftChange({ ...courtInstanceDraft, notes: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
            </div>
            <div className="mt-3 flex flex-wrap items-center justify-between gap-3">
              {courtInstanceDraft.courtUrl ? (
                <a href={courtInstanceDraft.courtUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 break-all text-sm font-semibold text-blue-700 hover:underline">
                  <ExternalLink className="h-4 w-4" /> Открыть источник
                </a>
              ) : <span className="text-sm text-slate-500">Ссылка на эту инстанцию пока не указана.</span>}
              <button type="button" onClick={() => void onSaveCourtInstance()} disabled={savingCourtInstance} className="inline-flex items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50">
                {savingCourtInstance ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Сохранить инстанцию
              </button>
            </div>
          </div>
          <div className="rounded-lg border border-slate-100 bg-white p-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-sm font-semibold text-slate-800">Жалобы и частные жалобы</h3>
              <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-500">{selectedCourtInstance?.complaints.length ?? 0}</span>
            </div>
            <div className="mt-3 grid gap-3 md:grid-cols-2">
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Тип</span>
                <SystemSelect value={courtComplaintDraft.complaintType} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, complaintType: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
                  {COURT_COMPLAINT_TYPES.map((type) => <option key={type.value} value={type.value}>{type.label}</option>)}
                </SystemSelect>
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Подается в</span>
                <SystemSelect value={courtComplaintDraft.targetInstance} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, targetInstance: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
                  {COURT_INSTANCE_TABS.filter((tab) => tab.value !== 'first').map((tab) => <option key={tab.value} value={tab.value}>{tab.label}</option>)}
                </SystemSelect>
              </label>
              <label className="text-sm font-medium text-slate-700 md:col-span-2">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Название</span>
                <input value={courtComplaintDraft.title} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, title: event.target.value })} placeholder="Например, частная жалоба на определение суда" className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Дата подачи</span>
                <SystemDateInput type="date" value={courtComplaintDraft.filedAt} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, filedAt: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
              <label className="text-sm font-medium text-slate-700">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
                <SystemSelect value={courtComplaintDraft.status} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, status: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
                  {COURT_COMPLAINT_STATUSES.map((status) => <option key={status.value} value={status.value}>{status.label}</option>)}
                </SystemSelect>
              </label>
              <label className="text-sm font-medium text-slate-700 md:col-span-2">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Документ</span>
                <SystemSelect value={courtComplaintDraft.clientCaseDocumentId} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, clientCaseDocumentId: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
                  <option value="">Без документа</option>
                  {documents.map((document) => (
                    <option key={document.id} value={document.id}>{document.originalFileName || document.documentType || `Документ #${document.id}`}</option>
                  ))}
                </SystemSelect>
              </label>
              <label className="text-sm font-medium text-slate-700 md:col-span-2">
                <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Заметка</span>
                <input value={courtComplaintDraft.notes} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, notes: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
              </label>
            </div>
            <div className="mt-3 flex flex-wrap gap-2">
              <button type="button" onClick={() => void onSaveCourtComplaint()} disabled={savingCourtComplaint} className="inline-flex items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
                {savingCourtComplaint ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} {editingCourtComplaint ? 'Сохранить жалобу' : 'Добавить жалобу'}
              </button>
              {editingCourtComplaint ? (
                <button type="button" onClick={onCancelCourtComplaintEdit} className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50">
                  <X className="h-4 w-4" /> Отмена
                </button>
              ) : null}
            </div>
            <div className="mt-4 space-y-2">
              {(selectedCourtInstance?.complaints.length ?? 0) === 0 ? (
                <div className="rounded-lg border border-dashed border-slate-200 p-3 text-sm text-slate-500">Жалобы по этой инстанции пока не добавлены.</div>
              ) : selectedCourtInstance?.complaints.map((complaint) => (
                <div key={complaint.id} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div>
                      <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{complaint.complaintTypeLabel} · {complaint.statusLabel}</div>
                      <div className="mt-1 font-semibold text-slate-900">{complaint.title}</div>
                    </div>
                    <div className="flex gap-2">
                      <button type="button" onClick={() => onEditCourtComplaint(complaint)} className="inline-flex items-center gap-1 rounded-md border border-slate-200 px-2 py-1 text-xs font-semibold text-slate-600 hover:bg-white">
                        <Pencil className="h-3 w-3" /> Править
                      </button>
                      <button type="button" onClick={() => void onArchiveCourtComplaint(complaint)} disabled={archivingCourtComplaintId === complaint.id} className="inline-flex items-center gap-1 rounded-md border border-slate-200 px-2 py-1 text-xs font-semibold text-slate-600 hover:bg-white disabled:opacity-50">
                        {archivingCourtComplaintId === complaint.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Archive className="h-3 w-3" />} Архив
                      </button>
                    </div>
                  </div>
                  <div className="mt-2 text-sm text-slate-600">В инстанцию: {complaint.targetInstanceLabel}{complaint.filedAt ? ` · ${toRuDate(complaint.filedAt)}` : ''}</div>
                  {complaint.clientCaseDocumentId ? <div className="mt-1 text-sm text-slate-500">Документ #{complaint.clientCaseDocumentId}</div> : null}
                  {complaint.notes ? <div className="mt-2 text-sm text-slate-500">{complaint.notes}</div> : null}
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Судебная хронология</h2>
          <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-500">
            {formatRussianCount(courtTimeline.length, ['событие', 'события', 'событий'])}
          </span>
        </div>
        <CourtTimeline entries={courtTimeline} />
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Судебные события</h2>
          <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-500">
            {formatRussianCount(selectedCourtEvents.length, ['запись', 'записи', 'записей'])}
          </span>
        </div>
        <div className="mt-5 rounded-lg border border-slate-100 bg-slate-50 p-3">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <h3 className="text-sm font-semibold text-slate-800">Стороны процесса</h3>
            <span className="rounded-full bg-white px-2.5 py-1 text-xs font-semibold text-slate-500">
              {formatRussianCount(courtParties.length, ['активная', 'активные', 'активных'])}
            </span>
          </div>
          <div className="mt-3 grid gap-3 lg:grid-cols-2 2xl:grid-cols-[1fr_150px_160px_1fr_1fr_auto]">
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Сторона</span>
              <input value={courtPartyNameDraft} onChange={(event) => onCourtPartyNameChange(event.target.value)} placeholder="Например, ООО Ответчик" className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Позиция</span>
              <SystemSelect value={courtPartySideDraft} onChange={(event) => onCourtPartySideChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
                <option value="claimant">Истец</option>
                <option value="respondent">Ответчик</option>
                <option value="thirdParty">Третье лицо</option>
                <option value="applicant">Заявитель</option>
                <option value="other">Иная</option>
              </SystemSelect>
            </label>
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Роль</span>
              <input value={courtPartyRoleDraft} onChange={(event) => onCourtPartyRoleChange(event.target.value)} placeholder="Ответчик" className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Представитель</span>
              <input value={courtPartyRepresentativeDraft} onChange={(event) => onCourtPartyRepresentativeChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Заметка</span>
              <input value={courtPartyNotesDraft} onChange={(event) => onCourtPartyNotesChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
            </label>
            <button type="button" onClick={() => void onAddCourtParty()} disabled={savingCourtParty} className="inline-flex items-center justify-center gap-2 self-end rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50">
              {savingCourtParty ? <Loader2 className="h-4 w-4 animate-spin" /> : <UserRound className="h-4 w-4" />} Добавить
            </button>
          </div>
          {courtPartiesLoading ? (
            <LoadingBlock label="Загрузка сторон процесса..." />
          ) : courtParties.length === 0 ? (
            <div className="mt-3 rounded-lg border border-dashed border-slate-200 bg-white p-3 text-sm text-slate-500">
              Стороны процесса пока не добавлены.
            </div>
          ) : (
            <div className="mt-3 grid gap-2 md:grid-cols-2 xl:grid-cols-3">
              {courtParties.map((party) => (
                <div key={party.id} className="rounded-lg border border-slate-100 bg-white p-3">
                  <div className="flex items-start justify-between gap-2">
                    <div>
                      <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{party.sideLabel}</div>
                      <div className="mt-1 font-semibold text-slate-900">{party.displayName}</div>
                    </div>
                    <button type="button" onClick={() => void onArchiveCourtParty(party)} disabled={archivingCourtPartyId === party.id} className="inline-flex items-center gap-1 rounded-md border border-slate-200 px-2 py-1 text-xs font-semibold text-slate-600 hover:bg-slate-50 disabled:opacity-50">
                      {archivingCourtPartyId === party.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Archive className="h-3 w-3" />} Архив
                    </button>
                  </div>
                  <div className="mt-1 text-sm text-slate-600">{party.processRole || 'Участник процесса'}</div>
                  {party.representativeName ? <div className="mt-1 text-sm text-slate-500">Представитель: {party.representativeName}</div> : null}
                  {party.notes ? <div className="mt-2 text-sm text-slate-500">{party.notes}</div> : null}
                  <CourtSourceProvenance item={party} />
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="mt-4 grid gap-3 lg:grid-cols-2 2xl:grid-cols-[150px_1fr_180px_150px_1fr_1fr_1fr_auto]">
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Тип</span>
            <SystemSelect value={courtEventTypeDraft} onChange={(event) => onCourtEventTypeChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
              <option value="hearing">Заседание</option>
              <option value="decision">Решение</option>
              <option value="postponement">Перенос</option>
              <option value="suspension">Приостановление</option>
              <option value="movement">Движение дела</option>
            </SystemSelect>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Название</span>
            <input value={courtEventTitleDraft} onChange={(event) => onCourtEventTitleChange(event.target.value)} placeholder="Например, предварительное заседание" className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Дата и время</span>
            <SystemDateInput type="datetime-local" value={courtEventScheduledAtDraft} onChange={(event) => onCourtEventScheduledAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
            <SystemSelect value={courtEventStatusDraft} onChange={(event) => onCourtEventStatusChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
              <option value="scheduled">Назначено</option>
              <option value="held">Проведено</option>
              <option value="postponed">Отложено</option>
              <option value="suspended">Приостановлено</option>
              <option value="completed">Завершено</option>
              <option value="cancelled">Отменено</option>
              <option value="archived">В архиве</option>
            </SystemSelect>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Итог</span>
            <input value={courtEventResultDraft} onChange={(event) => onCourtEventResultChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Документ</span>
            <SystemSelect value={courtEventDocumentIdDraft} onChange={(event) => onCourtEventDocumentIdChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
              <option value="">Без документа</option>
              {documents.map((document) => (
                <option key={document.id} value={document.id}>
                  {document.originalFileName || document.documentType || `Документ #${document.id}`}
                </option>
              ))}
            </SystemSelect>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Суд</span>
            <SystemSelect value={courtEventCourtIdDraft} onChange={(event) => onCourtEventCourtIdChange(event.target.value)} disabled={courtDirectoryLoading} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm disabled:bg-slate-50">
              <option value="">{courtDirectoryLoading ? 'Загрузка...' : 'Из карточки'}</option>
              {courtDirectoryForInstance.map((court) => (
                <option key={court.id} value={court.id}>
                  {court.name}
                </option>
              ))}
            </SystemSelect>
          </label>
          <button type="button" onClick={() => void onAddCourtEvent()} disabled={savingCourtEvent} className="inline-flex items-center justify-center gap-2 self-end rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50">
            {savingCourtEvent ? <Loader2 className="h-4 w-4 animate-spin" /> : <Gavel className="h-4 w-4" />} Добавить
          </button>
        </div>
        {courtEventsLoading ? (
          <LoadingBlock label="Загрузка судебных событий..." />
        ) : courtEventsError ? (
          <div role="alert" className="mt-3 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
            {courtEventsError}
          </div>
        ) : selectedCourtEvents.length === 0 ? (
          <div className="mt-3 rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
            Судебные события пока не добавлены.
          </div>
        ) : (
          <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {selectedCourtEvents.map((event) => (
              <div key={event.id} className={`rounded-lg border p-3 ${event.isOverdue ? 'border-rose-200 bg-rose-50' : 'border-slate-100 bg-slate-50'}`}>
                <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{event.eventTypeLabel || event.eventType} · {event.movementStatusLabel || event.movementStatus}</div>
                <div className="mt-2 font-semibold text-slate-900">{event.title}</div>
                <div className="mt-1 text-sm text-slate-600">{formatCourtEventSchedule(event) || '—'} · {event.courtInstanceLabel}</div>
                {event.courtName ? <div className="mt-2 text-sm text-slate-500">{event.courtName}{event.courtCaseNumber ? ` · ${event.courtCaseNumber}` : ''}</div> : null}
                {event.clientCaseDocumentId ? <div className="mt-2 text-sm text-slate-500">Документ #{event.clientCaseDocumentId}</div> : null}
                {event.resultSummary ? <div className="mt-2 text-sm text-slate-500">{event.resultSummary}</div> : null}
                <CourtSourceProvenance item={event} />
              </div>
            ))}
          </div>
        )}
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Процессуальные сроки 2.0</h2>
          <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-500">
            {formatRussianCount(activeProceduralDeadlines.length, ['активный', 'активных', 'активных'])}
          </span>
        </div>
        <div className="mt-4 grid gap-3 lg:grid-cols-2 2xl:grid-cols-[160px_1fr_150px_120px_1fr_1fr_auto_auto]">
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Тип</span>
            <SystemSelect value={deadlineTypeDraft} onChange={(event) => onDeadlineTypeChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
              <option value="appeal">Апелляция</option>
              <option value="cassation">Кассация</option>
              <option value="motion">Ходатайство</option>
              <option value="objection">Возражения</option>
              <option value="document">Документ</option>
              <option value="manual">Ручной срок</option>
            </SystemSelect>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Название</span>
            <input value={deadlineTitleDraft} onChange={(event) => onDeadlineTitleChange(event.target.value)} placeholder="Например, подать жалобу" className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Дата</span>
            <SystemDateInput type="date" value={deadlineDueAtDraft} onChange={(event) => onDeadlineDueAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Предупр.</span>
            <input type="number" min="0" max="365" value={deadlineWarningDaysDraft} onChange={(event) => onDeadlineWarningDaysChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Заметка</span>
            <input value={deadlineNotesDraft} onChange={(event) => onDeadlineNotesChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Событие</span>
            <SystemSelect value={deadlineCourtEventIdDraft} onChange={(event) => onDeadlineCourtEventIdChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
              <option value="">Без связи</option>
              {courtEvents.map((event) => (
                <option key={event.id} value={event.id}>
                  {event.title || `Событие #${event.id}`}
                </option>
              ))}
            </SystemSelect>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Документ</span>
            <SystemSelect value={deadlineDocumentIdDraft} onChange={(event) => onDeadlineDocumentIdChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm">
              <option value="">Без документа</option>
              {documents.map((document) => (
                <option key={document.id} value={document.id}>
                  {document.originalFileName || document.documentType || `Документ #${document.id}`}
                </option>
              ))}
            </SystemSelect>
          </label>
          <button type="button" onClick={() => void onAddProceduralDeadline()} disabled={savingProceduralDeadline} className="inline-flex items-center justify-center gap-2 self-end rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50">
            {savingProceduralDeadline ? <Loader2 className="h-4 w-4 animate-spin" /> : <Clock3 className="h-4 w-4" />} Добавить
          </button>
          <button type="button" onClick={() => void onCalculateProceduralDeadline()} disabled={calculatingProceduralDeadline} className="inline-flex items-center justify-center gap-2 self-end rounded-lg border border-slate-200 px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-50">
            {calculatingProceduralDeadline ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Рассчитать
          </button>
        </div>
        {deadlineCalculationPreview ? (
          <div className="mt-3 rounded-lg border border-sky-200 bg-sky-50 p-3 text-sm text-sky-900">
            <div className="font-semibold">
              Расчет срока: {toRuDate(deadlineCalculationPreview.startAt) || '—'} → {toRuDate(deadlineCalculationPreview.deadlineAt) || '—'}
            </div>
            <div className="mt-1 text-sky-800">
              {deadlineCalculationPreview.explanation || 'Дата рассчитана по выбранному типу процессуального срока.'}
            </div>
            <div className="mt-2 flex flex-wrap gap-2 text-xs font-semibold text-sky-700">
              {deadlineCalculationPreview.rawDeadlineAt ? (
                <span className="rounded-full bg-white px-2 py-1">Базовая дата: {toRuDate(deadlineCalculationPreview.rawDeadlineAt)}</span>
              ) : null}
              <span className="rounded-full bg-white px-2 py-1">Пропущено нерабочих дней: {deadlineCalculationPreview.skippedNonWorkingDays ?? 0}</span>
              <span className="rounded-full bg-white px-2 py-1">Перенос: {deadlineCalculationPreview.rollForwardDays ?? 0} дн.</span>
            </div>
          </div>
        ) : null}
        {proceduralDeadlinesLoading ? (
          <LoadingBlock label="Загрузка процессуальных сроков..." />
        ) : proceduralDeadlinesError ? (
          <div role="alert" className="mt-3 rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
            {proceduralDeadlinesError} Нажмите «Обновить» в верхней части карточки.
          </div>
        ) : activeProceduralDeadlines.length === 0 ? (
          <div className="mt-3 rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
            Отдельные процессуальные сроки пока не добавлены.
          </div>
        ) : (
          <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {activeProceduralDeadlines.map((deadline) => (
              <div key={deadline.id} className={`rounded-lg border p-3 ${deadline.isOverdue ? 'border-rose-200 bg-rose-50' : deadline.isWarning ? 'border-amber-200 bg-amber-50' : 'border-slate-100 bg-slate-50'}`}>
                <div className="flex items-center justify-between gap-2">
                  <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{deadline.deadlineTypeLabel || deadline.deadlineType}</div>
                  <button type="button" onClick={() => void onCloseProceduralDeadline(deadline)} disabled={closingProceduralDeadlineId === deadline.id} className="inline-flex items-center gap-1 rounded-md border border-slate-200 px-2 py-1 text-xs font-semibold text-slate-600 hover:bg-white disabled:opacity-50">
                    {closingProceduralDeadlineId === deadline.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Check className="h-3 w-3" />} Закрыть
                  </button>
                </div>
                <div className="mt-2 font-semibold text-slate-900">{deadline.title}</div>
                <div className="mt-1 text-sm text-slate-600">{formatCourtSchedule(deadline.dueAt, deadline.allDay) || '—'} · {deadline.statusLabel}</div>
                {deadline.notes ? <div className="mt-2 text-sm text-slate-500">{deadline.notes}</div> : null}
                <CourtSourceProvenance item={deadline} />
              </div>
            ))}
          </div>
        )}
        {inactiveProceduralDeadlines.length > 0 ? (
          <div className="mt-4 text-sm text-slate-500">
            Закрыто и в архиве: {inactiveProceduralDeadlines.length}
          </div>
        ) : null}
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Процессуальные сроки</h2>
        {deadlines.length === 0 ? (
          <div className="mt-3 rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
            Сроки и заседания по делу пока не заполнены.
          </div>
        ) : (
          <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {deadlines.map(([label, value]) => (
              <div key={label} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                <div className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">{label}</div>
                <div className="mt-2 font-semibold text-slate-900">{toRuDate(value) || '—'}</div>
              </div>
            ))}
          </div>
        )}
      </section>

      <section className="rounded-xl border border-slate-200 p-4">
        <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Журнал судебных проверок</h2>
        {logsLoading ? (
          <LoadingBlock label="Загрузка журнала судебных проверок..." />
        ) : logs.length === 0 ? (
          <div className="mt-3 rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
            Проверок судебного источника по делу пока не было.
          </div>
        ) : (
          <div className="mt-4 space-y-3">
            {logs.map((log) => (
              <div key={log.id} className="rounded-lg border border-slate-100 bg-slate-50 p-3">
                <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
                  <span className="rounded-full bg-white px-2 py-1 font-semibold text-slate-700">
                    {COURT_SYNC_STATUS_LABELS[log.status] ?? log.status}
                  </span>
                  <span>{toRuDate(log.occurredAt) || 'Дата не определена'}</span>
                  {log.actorName ? <span>Инициатор: {log.actorName}</span> : null}
                </div>
                <div className="mt-2 text-sm text-slate-700">{log.message}</div>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

function DocumentList({
  documents,
  clientId,
  downloadingDocumentId,
  previewingDocumentId,
  sendingYandexDocumentId,
  uploadingDocumentVersionId,
  updatingDocumentId,
  deletingDocumentId,
  documentTypeOptions,
  documentStatusOptions,
  canEdit,
  canDelete,
  canSendMessenger,
  onPreview,
  onDownload,
  onSendToYandexDisk,
  onUploadVersion,
  onUpdate,
  onDelete,
}: {
  documents: ClientCaseDocument[];
  clientId: number | null;
  downloadingDocumentId: number | null;
  previewingDocumentId: number | null;
  sendingYandexDocumentId: number | null;
  uploadingDocumentVersionId: number | null;
  updatingDocumentId: number | null;
  deletingDocumentId: number | null;
  documentTypeOptions: string[];
  documentStatusOptions: string[];
  canEdit: boolean;
  canDelete: boolean;
  canSendMessenger: boolean;
  onPreview: (caseDocument: ClientCaseDocument) => void;
  onDownload: (caseDocument: ClientCaseDocument) => void;
  onSendToYandexDisk: (caseDocument: ClientCaseDocument) => void;
  onUploadVersion: (caseDocument: ClientCaseDocument, file: File | null | undefined) => void;
  onUpdate: (caseDocument: ClientCaseDocument, payload: { documentType: string; status: string }) => void;
  onDelete: (caseDocument: ClientCaseDocument) => void;
}) {
  const [searchTerm, setSearchTerm] = useState('');
  const [typeFilter, setTypeFilter] = useState('all');
  const [statusFilter, setStatusFilter] = useState('all');
  const [latestOnly, setLatestOnly] = useState(false);
  const [deliveryDocument, setDeliveryDocument] = useState<ClientCaseDocument | null>(null);
  const versionSummaries = useMemo(() => buildDocumentVersionSummaries(documents), [documents]);

  const documentTypes = useMemo(
    () => Array.from(new Set(documents.map((document) => document.documentType).filter(Boolean))).sort((a, b) => a.localeCompare(b, 'ru')),
    [documents],
  );
  const documentStatuses = useMemo(
    () => Array.from(new Set(documents.map((document) => document.status).filter(Boolean))).sort((a, b) => a.localeCompare(b, 'ru')),
    [documents],
  );
  const visibleDocuments = useMemo(() => {
    const query = normalizeDocumentFilter(searchTerm);
    return documents.filter((document) => {
      if (latestOnly && !document.isLatestVersion) return false;
      if (typeFilter !== 'all' && document.documentType !== typeFilter) return false;
      if (statusFilter !== 'all' && document.status !== statusFilter) return false;
      if (!query) return true;

      return [
        document.originalFileName,
        document.documentType,
        document.status,
        document.uploadedByName,
        document.documentKey,
        document.templateLabel,
      ].some((value) => normalizeDocumentFilter(value).includes(query));
    });
  }, [documents, latestOnly, searchTerm, statusFilter, typeFilter]);

  if (documents.length === 0) {
    return <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">Документы по делу пока не загружены.</div>;
  }

  return (
    <div className="space-y-3">
      <section className="rounded-xl border border-slate-200 bg-slate-50 p-4">
        <div className="grid gap-3 lg:grid-cols-2 2xl:grid-cols-[minmax(220px,1fr)_180px_180px_auto]">
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Поиск</span>
            <input
              aria-label="Поиск документов дела"
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
              placeholder="Файл, тип, статус, автор"
            />
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Тип</span>
            <SystemSelect
              aria-label="Фильтр документов по типу"
              value={typeFilter}
              onChange={(event) => setTypeFilter(event.target.value)}
              className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
              <option value="all">Все типы</option>
              {documentTypes.map((type) => (
                <option key={type} value={type}>{type}</option>
              ))}
            </SystemSelect>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
            <SystemSelect
              aria-label="Фильтр документов по статусу"
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value)}
              className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
              <option value="all">Все статусы</option>
              {documentStatuses.map((status) => (
                <option key={status} value={status}>{status}</option>
              ))}
            </SystemSelect>
          </label>
          <label className="flex items-end gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 lg:self-end">
            <SystemCheckbox
              type="checkbox"
              checked={latestOnly}
              onChange={(event) => setLatestOnly(event.target.checked)}
              className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
            />
            Только актуальные версии
          </label>
        </div>
        <div className="mt-3 text-xs text-slate-500">
          Показано {visibleDocuments.length} из {documents.length}
        </div>
      </section>

      {visibleDocuments.length === 0 ? (
        <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
          По текущим фильтрам документов нет.
        </div>
      ) : (
        <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {visibleDocuments.map((document) => (
            <DocumentCard
              key={document.id}
              document={document}
              versionSummary={versionSummaries.get(document.documentKey)}
              downloading={downloadingDocumentId === document.id}
              previewing={previewingDocumentId === document.id}
              sendingToYandex={sendingYandexDocumentId === document.id}
              uploadingVersion={uploadingDocumentVersionId === document.id}
              updating={updatingDocumentId === document.id}
              deleting={deletingDocumentId === document.id}
              documentTypeOptions={documentTypeOptions}
              documentStatusOptions={documentStatusOptions}
              canEdit={canEdit}
              canDelete={canDelete}
              onPreview={onPreview}
              onDownload={onDownload}
              onSendToYandexDisk={onSendToYandexDisk}
              onSendMessenger={clientId && canSendMessenger ? setDeliveryDocument : undefined}
              onUploadVersion={onUploadVersion}
              onUpdate={onUpdate}
              onDelete={onDelete}
            />
          ))}
        </div>
      )}
      {deliveryDocument && clientId ? (
        <MessengerDocumentDeliveryModal
          document={deliveryDocument}
          clientId={clientId}
          onClose={() => setDeliveryDocument(null)}
        />
      ) : null}
    </div>
  );
}

function normalizeDocumentFilter(value?: string | null) {
  return (value ?? '').trim().toLowerCase();
}

type DocumentVersionSummary = {
  count: number;
  latestVersion: number;
  versions: number[];
};

function buildDocumentVersionSummaries(documents: ClientCaseDocument[]) {
  const groups = new Map<string, ClientCaseDocument[]>();
  documents.forEach((document) => {
    const key = document.documentKey || `document-${document.id}`;
    const group = groups.get(key) ?? [];
    group.push(document);
    groups.set(key, group);
  });

  const result = new Map<string, DocumentVersionSummary>();
  groups.forEach((group, key) => {
    const versions = Array.from(new Set(group.map((document) => document.version)))
      .sort((a, b) => a - b);
    result.set(key, {
      count: versions.length,
      latestVersion: Math.max(...versions),
      versions,
    });
  });

  return result;
}

function DocumentCard({
  document,
  versionSummary,
  downloading,
  previewing,
  sendingToYandex,
  uploadingVersion,
  updating,
  deleting,
  documentTypeOptions,
  documentStatusOptions,
  canEdit,
  canDelete,
  onPreview,
  onDownload,
  onSendToYandexDisk,
  onSendMessenger,
  onUploadVersion,
  onUpdate,
  onDelete,
}: {
  document: ClientCaseDocument;
  versionSummary?: DocumentVersionSummary;
  downloading: boolean;
  previewing: boolean;
  sendingToYandex: boolean;
  uploadingVersion: boolean;
  updating: boolean;
  deleting: boolean;
  documentTypeOptions: string[];
  documentStatusOptions: string[];
  canEdit: boolean;
  canDelete: boolean;
  onPreview: (caseDocument: ClientCaseDocument) => void;
  onDownload: (caseDocument: ClientCaseDocument) => void;
  onSendToYandexDisk: (caseDocument: ClientCaseDocument) => void;
  onSendMessenger?: (caseDocument: ClientCaseDocument) => void;
  onUploadVersion: (caseDocument: ClientCaseDocument, file: File | null | undefined) => void;
  onUpdate: (caseDocument: ClientCaseDocument, payload: { documentType: string; status: string }) => void;
  onDelete: (caseDocument: ClientCaseDocument) => void;
}) {
  const [documentType, setDocumentType] = useState(document.documentType);
  const [status, setStatus] = useState(document.status);

  useEffect(() => {
    setDocumentType(document.documentType);
    setStatus(document.status);
  }, [document.documentType, document.status]);

  const documentTypeListId = `case-document-card-types-${document.id}`;
  const statusOptions = mergeDirectoryOptions([document.status], documentStatusOptions);
  const versionLabel = versionSummary && versionSummary.count > 1
    ? `История версий: ${versionSummary.versions.map((version) => `v${version}`).join(', ')} · последняя v${versionSummary.latestVersion}`
    : null;

  return (
    <div className="rounded-xl border border-slate-200 p-4">
      <div className="flex items-start gap-3">
        <FileText className="mt-1 h-5 w-5 text-blue-600" />
        <div className="min-w-0 flex-1">
          <div className="font-semibold text-slate-900">{document.originalFileName}</div>
          <div className="mt-1 text-sm text-slate-500">
            {document.documentType} · v{document.version} · {formatCaseDocumentStatus(document.status)}
          </div>
          <div className="mt-2 flex flex-wrap gap-2 text-xs">
            <span className="rounded-full bg-slate-100 px-2 py-0.5 text-slate-600">
              Версия {document.version}
            </span>
            {document.isLatestVersion ? (
              <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-emerald-700">Актуальная</span>
            ) : (
              <span className="rounded-full bg-amber-50 px-2 py-0.5 text-amber-700">Не актуальная версия</span>
            )}
          </div>
          {versionLabel ? (
            <div className="mt-2 text-xs text-slate-500">{versionLabel}</div>
          ) : null}
          {document.templateLabel ? (
            <div className="mt-1 text-xs text-blue-600">Шаблон: {document.templateLabel}</div>
          ) : null}
          <CourtSourceProvenance item={document} />
          <div className="mt-2 text-xs text-slate-400">
            {document.uploadedByName || '—'} · {formatRuDateTime(document.uploadedAt) || '—'}
          </div>
        </div>
      </div>
      <div className="mt-4 grid gap-2">
        <datalist id={documentTypeListId}>
          {documentTypeOptions.map((type) => (
            <option key={type} value={type} />
          ))}
        </datalist>
        <label className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">
          Тип
          <input
            aria-label={`Тип документа ${document.originalFileName}`}
            list={documentTypeListId}
            value={documentType}
            onChange={(event) => setDocumentType(event.target.value)}
            disabled={!canEdit}
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm normal-case tracking-normal text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:bg-slate-50 disabled:text-slate-500"
          />
        </label>
        <label className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">
          Статус
          <SystemSelect
            aria-label={`Статус документа ${document.originalFileName}`}
            value={status}
            onChange={(event) => setStatus(event.target.value)}
            disabled={!canEdit}
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm normal-case tracking-normal text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 disabled:bg-slate-50 disabled:text-slate-500"
          >
            {statusOptions.map((option) => (
              <option key={option} value={option}>{option}</option>
            ))}
          </SystemSelect>
        </label>
      </div>
      <div className="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
        {canEdit ? (
          <button
            type="button"
            onClick={() => onUpdate(document, { documentType, status })}
            disabled={updating || deleting}
            aria-label={`Сохранить документ ${document.originalFileName}`}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
            {updating ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Сохранить
          </button>
        ) : null}
        <button
          type="button"
          onClick={() => onPreview(document)}
          disabled={previewing || deleting}
          aria-label={`Предпросмотр документа ${document.originalFileName}`}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-cyan-200 px-3 py-2 text-sm font-medium text-cyan-700 hover:bg-cyan-50 disabled:opacity-50">
          {previewing ? <Loader2 className="h-4 w-4 animate-spin" /> : <Eye className="h-4 w-4" />} Просмотр
        </button>
        <button
          type="button"
          onClick={() => onDownload(document)}
          disabled={downloading || deleting}
          aria-label={`Скачать документ ${document.originalFileName}`}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50">
          {downloading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />} Скачать
        </button>
        <button
          type="button"
          onClick={() => onSendToYandexDisk(document)}
          disabled={sendingToYandex || deleting}
          aria-label={`Отправить документ ${document.originalFileName} на Яндекс.Диск`}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-sky-200 px-3 py-2 text-sm font-medium text-sky-700 hover:bg-sky-50 disabled:opacity-50">
          {sendingToYandex ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />} Я.Диск
        </button>
        {onSendMessenger ? (
          <button
            type="button"
            onClick={() => onSendMessenger(document)}
            disabled={deleting}
            aria-label={`Отправить документ ${document.originalFileName} в мессенджер`}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-violet-200 px-3 py-2 text-sm font-medium text-violet-700 hover:bg-violet-50 disabled:opacity-50">
            <Send className="h-4 w-4" /> Мессенджер
          </button>
        ) : null}
        {canEdit ? (
          <label
            className={`inline-flex items-center justify-center gap-2 rounded-lg border border-emerald-200 px-3 py-2 text-sm font-medium text-emerald-700 hover:bg-emerald-50 ${uploadingVersion || updating || deleting ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'}`}
          >
            {uploadingVersion ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCw className="h-4 w-4" />} Новая версия
            <input
              aria-label={`Загрузить новую версию документа ${document.originalFileName}`}
              type="file"
              className="sr-only"
              disabled={uploadingVersion || updating || deleting}
              onChange={(event) => {
                onUploadVersion(document, event.currentTarget.files?.[0]);
                event.currentTarget.value = '';
              }}
            />
          </label>
        ) : null}
        {canDelete ? (
          <button
            type="button"
            onClick={() => onDelete(document)}
            disabled={updating || deleting}
            aria-label={`Удалить документ ${document.originalFileName}`}
            className="inline-flex items-center justify-center gap-2 rounded-lg border border-red-200 px-3 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:opacity-50">
            {deleting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />} Удалить
          </button>
        ) : null}
      </div>
    </div>
  );
}

function canPreviewInline(contentType: string) {
  const normalized = contentType.toLowerCase();
  return normalized.startsWith('image/')
    || normalized.includes('pdf')
    || normalized.includes('html')
    || normalized.startsWith('text/');
}

function DocumentPreviewModal({
  preview,
  onClose,
  onDownload,
}: {
  preview: DocumentPreviewState;
  onClose: () => void;
  onDownload: () => void;
}) {
  const contentType = preview.contentType.toLowerCase();
  const isImage = contentType.startsWith('image/');
  const isHtml = contentType.includes('html') && preview.htmlContent !== undefined;
  const isText = contentType.startsWith('text/') && !contentType.includes('html') && preview.textContent !== undefined;
  const inlinePreview = canPreviewInline(contentType) && !contentType.startsWith('text/');

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/60 px-4 py-6">
      <section className="flex max-h-[92vh] w-full max-w-6xl flex-col overflow-hidden rounded-xl bg-white shadow-2xl">
        <div className="flex flex-wrap items-start justify-between gap-3 border-b border-slate-200 px-5 py-4">
          <div className="min-w-0">
            <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">Предпросмотр документа</div>
            <h2 className="mt-1 truncate text-lg font-semibold text-slate-900">{preview.fileName}</h2>
            <div className="mt-1 text-sm text-slate-500">
              {preview.document.documentType} · v{preview.document.version} · {preview.document.status}
            </div>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onDownload}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
              <Download className="h-4 w-4" /> Скачать
            </button>
            <button
              type="button"
              onClick={onClose}
              aria-label="Закрыть предпросмотр"
              className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-slate-200 text-slate-500 hover:bg-slate-50">
              <X className="h-4 w-4" />
            </button>
          </div>
        </div>
        <div className="min-h-0 flex-1 bg-slate-100 p-4">
          {isImage ? (
            <div className="flex h-full min-h-[480px] items-center justify-center overflow-auto rounded-lg bg-white">
              <img src={preview.url} alt={preview.fileName} className="max-h-full max-w-full object-contain" />
            </div>
          ) : isHtml ? (
            <iframe
              title={`Предпросмотр ${preview.fileName}`}
              srcDoc={preview.htmlContent}
              sandbox=""
              referrerPolicy="no-referrer"
              className="h-[70vh] w-full rounded-lg border border-slate-200 bg-white"
            />
          ) : isText ? (
            <pre className="h-[70vh] w-full overflow-auto whitespace-pre-wrap break-words rounded-lg border border-slate-200 bg-white p-4 text-sm leading-6 text-slate-800">
              {preview.textContent}
            </pre>
          ) : inlinePreview ? (
            <iframe
              title={`Предпросмотр ${preview.fileName}`}
              src={preview.url}
              className="h-[70vh] w-full rounded-lg border border-slate-200 bg-white"
            />
          ) : (
            <div className="flex min-h-[360px] flex-col items-center justify-center rounded-lg border border-dashed border-slate-300 bg-white p-6 text-center">
              <FileText className="h-10 w-10 text-slate-400" />
              <h3 className="mt-3 text-base font-semibold text-slate-900">Для этого формата доступно скачивание</h3>
              <p className="mt-2 max-w-md text-sm text-slate-500">
                Браузер не умеет надежно показывать этот файл внутри CRM. Скачайте документ и откройте его в профильной программе.
              </p>
              <button
                type="button"
                onClick={onDownload}
                className="mt-4 inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800">
                <Download className="h-4 w-4" /> Скачать документ
              </button>
            </div>
          )}
        </div>
      </section>
    </div>
  );
}
