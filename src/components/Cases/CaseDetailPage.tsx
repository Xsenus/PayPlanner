import { useEffect, useMemo, useState } from 'react';
import {
  Archive,
  ArrowLeft,
  Briefcase,
  Check,
  ClipboardPlus,
  Copy,
  Download,
  ExternalLink,
  Eye,
  FilePlus2,
  FileText,
  Loader2,
  Mail,
  MessageSquare,
  Phone,
  RefreshCw,
  Save,
  Send,
  Trash2,
  UserRound,
  WalletCards,
  X,
} from 'lucide-react';

import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import type {
  ActResponsible,
  Client,
  ClientCase,
  ClientCaseConversationEntry,
  ClientCaseCourtSyncLog,
  ClientCaseDocument,
  ClientCaseDocumentTemplate,
  ClientCaseFinanceDetail,
  ClientCaseRelatedParty,
  ClientCaseTimelineEntry,
  DocumentStatusEntity,
  LegalEntitySummary,
} from '../../types';
import { toRuDate } from '../../utils/dateUtils';
import { formatCurrencySmart } from '../../utils/formatters';
import type { TaskJournalItem } from '../../utils/taskJournalStore';

interface CaseDetailPageProps {
  caseId: number;
  onBack: () => void;
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onOpenLegalEntity?: (legalEntityId: number, caseId?: number) => void;
  onOpenFinance?: (caseItem: ClientCase) => void;
  onCreateInvoice?: (caseItem: ClientCase) => void;
  onCreatePayment?: (caseItem: ClientCase) => void;
}

type CaseDetailTab = 'overview' | 'participants' | 'tasks' | 'court' | 'finance' | 'documents' | 'conversation' | 'history';
type CaseTaskFilter = 'active' | 'all' | 'overdue' | 'done';
type SummaryTone = 'slate' | 'emerald' | 'amber' | 'rose' | 'cyan';
type ParticipantSourceType = 'client' | 'legalEntity' | 'externalContact';

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

const DETAIL_TABS: Array<{ key: CaseDetailTab; label: string }> = [
  { key: 'overview', label: 'Обзор' },
  { key: 'participants', label: 'Участники' },
  { key: 'tasks', label: 'Задачи' },
  { key: 'court', label: 'Суд' },
  { key: 'finance', label: 'Финансы' },
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
].map((template) => ({ ...template, heading: template.label }));

const FALLBACK_DOCUMENT_TYPE_OPTIONS = [
  'Карточка дела',
  'Иск',
  'Ходатайство',
  'Возражение',
  'Переписка',
  'Заявление',
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

interface CaseDetailViewState {
  activeTab?: CaseDetailTab;
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

function formatRuDateTime(value?: string | null) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return toRuDate(value);
  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
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

function getOwnerOrganization(caseItem: ClientCase) {
  return caseItem.client?.legalEntity?.shortName
    || caseItem.client?.legalEntity?.fullName
    || caseItem.client?.company
    || caseItem.legalEntity?.shortName
    || caseItem.legalEntity?.fullName
    || '—';
}

function maskSensitiveValue(value: string, visibleTail = 4) {
  const normalized = value.trim();
  if (!normalized) return '—';
  if (normalized.length <= visibleTail) return '••••';
  return `${'•'.repeat(Math.min(8, normalized.length - visibleTail))}${normalized.slice(-visibleTail)}`;
}

function getRiskLabel(caseItem: ClientCase) {
  const overdue = caseItem.overdueTaskCount ?? 0;
  if (overdue > 0) return `${overdue} просроч.`;
  if (caseItem.status === 'Closed') return 'Закрыто';
  return 'Без риска';
}

const DAY_MS = 24 * 60 * 60 * 1000;

function getDateTime(value?: string | null) {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isFinite(time) ? time : null;
}

function getLatestCaseActivityTime(caseItem: ClientCase) {
  const dates = [
    getDateTime(caseItem.activityConfirmedAt),
    getDateTime(caseItem.lastTaskAt),
    getDateTime(caseItem.lastViewedAt),
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
  const hasCourtBinding = Boolean(caseItem.courtName || caseItem.courtCaseNumber || caseItem.courtUrl);
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

export function CaseDetailPage({
  caseId,
  onBack,
  onOpenClient,
  onOpenLegalEntity,
  onOpenFinance,
  onCreateInvoice,
  onCreatePayment,
}: CaseDetailPageProps) {
  const initialViewState = readCaseDetailViewState(caseId);
  const [caseItem, setCaseItem] = useState<ClientCase | null>(null);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [tasks, setTasks] = useState<TaskJournalItem[]>([]);
  const [relatedParties, setRelatedParties] = useState<ClientCaseRelatedParty[]>([]);
  const [documents, setDocuments] = useState<ClientCaseDocument[]>([]);
  const [courtSyncLogs, setCourtSyncLogs] = useState<ClientCaseCourtSyncLog[]>([]);
  const [timeline, setTimeline] = useState<ClientCaseTimelineEntry[]>([]);
  const [conversation, setConversation] = useState<ClientCaseConversationEntry[]>([]);
  const [financeDetail, setFinanceDetail] = useState<ClientCaseFinanceDetail | null>(null);
  const [participantClients, setParticipantClients] = useState<Client[]>([]);
  const [participantLegalEntities, setParticipantLegalEntities] = useState<LegalEntitySummary[]>([]);
  const [participantContacts, setParticipantContacts] = useState<ContactDirectoryItemDto[]>([]);
  const [activeTab, setActiveTab] = useState<CaseDetailTab>(initialViewState.activeTab ?? 'overview');
  const [loading, setLoading] = useState(true);
  const [tasksLoading, setTasksLoading] = useState(false);
  const [participantsLoading, setParticipantsLoading] = useState(false);
  const [documentsLoading, setDocumentsLoading] = useState(false);
  const [courtSyncLogsLoading, setCourtSyncLogsLoading] = useState(false);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [conversationLoading, setConversationLoading] = useState(false);
  const [financeLoading, setFinanceLoading] = useState(false);
  const [participantDirectoryLoading, setParticipantDirectoryLoading] = useState(false);
  const [participantDirectoryError, setParticipantDirectoryError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);
  const [creatingTask, setCreatingTask] = useState(false);
  const [savingHeader, setSavingHeader] = useState(false);
  const [generatingDocument, setGeneratingDocument] = useState(false);
  const [uploadingDocument, setUploadingDocument] = useState(false);
  const [downloadingDocumentId, setDownloadingDocumentId] = useState<number | null>(null);
  const [previewingDocumentId, setPreviewingDocumentId] = useState<number | null>(null);
  const [documentPreview, setDocumentPreview] = useState<DocumentPreviewState | null>(null);
  const [updatingDocumentId, setUpdatingDocumentId] = useState<number | null>(null);
  const [deletingDocumentId, setDeletingDocumentId] = useState<number | null>(null);
  const [syncingCourt, setSyncingCourt] = useState(false);
  const [addingHistoryEntry, setAddingHistoryEntry] = useState(false);
  const [addingConversationEntry, setAddingConversationEntry] = useState(false);
  const [addingParticipant, setAddingParticipant] = useState(false);
  const [deletingParticipantId, setDeletingParticipantId] = useState<number | null>(null);
  const [copiedLink, setCopiedLink] = useState(false);
  const [statusDraft, setStatusDraft] = useState('Open');
  const [priorityDraft, setPriorityDraft] = useState('normal');
  const [stageDraft, setStageDraft] = useState('');
  const [responsibleDraft, setResponsibleDraft] = useState('');
  const [taskFilter, setTaskFilter] = useState<CaseTaskFilter>(initialViewState.taskFilter ?? 'active');
  const [participantSourceType, setParticipantSourceType] = useState<ParticipantSourceType>(initialViewState.participantSourceType ?? 'client');
  const [participantSourceId, setParticipantSourceId] = useState('');
  const [participantSearch, setParticipantSearch] = useState('');
  const [participantRole, setParticipantRole] = useState(initialViewState.participantRole ?? '');
  const [participantNotes, setParticipantNotes] = useState('');
  const [documentTypeDraft, setDocumentTypeDraft] = useState(initialViewState.documentTypeDraft ?? '');
  const [documentStatusDraft, setDocumentStatusDraft] = useState(initialViewState.documentStatusDraft ?? '');
  const [documentFile, setDocumentFile] = useState<File | null>(null);
  const [documentTemplates, setDocumentTemplates] = useState<ClientCaseDocumentTemplate[]>(FALLBACK_DOCUMENT_TEMPLATE_OPTIONS);
  const [documentTypeDirectoryOptions, setDocumentTypeDirectoryOptions] = useState<string[]>(FALLBACK_DOCUMENT_TYPE_OPTIONS);
  const [documentStatusDirectoryOptions, setDocumentStatusDirectoryOptions] = useState<DocumentStatusEntity[]>([]);
  const [documentTemplateKey, setDocumentTemplateKey] = useState(initialViewState.documentTemplateKey ?? FALLBACK_DOCUMENT_TEMPLATE_OPTIONS[0].key);
  const [historyDraft, setHistoryDraft] = useState('');
  const [conversationDraft, setConversationDraft] = useState('');

  const documentTypeOptions = useMemo(
    () => buildDocumentTypeOptions(documentTypeDirectoryOptions, documentTemplates, documents),
    [documentTypeDirectoryOptions, documentTemplates, documents],
  );
  const documentStatusOptions = useMemo(
    () => buildDocumentStatusOptions(documentStatusDirectoryOptions, documents),
    [documentStatusDirectoryOptions, documents],
  );
  const documentTypeListId = useMemo(() => `case-document-types-${caseId}`, [caseId]);

  useEffect(() => {
    const savedState = readCaseDetailViewState(caseId);
    setActiveTab(savedState.activeTab ?? 'overview');
    setTaskFilter(savedState.taskFilter ?? 'active');
    setParticipantSourceType(savedState.participantSourceType ?? 'client');
    setParticipantRole(savedState.participantRole ?? '');
    setDocumentTypeDraft(savedState.documentTypeDraft ?? '');
    setDocumentStatusDraft(savedState.documentStatusDraft ?? '');
    setDocumentTemplateKey(savedState.documentTemplateKey ?? FALLBACK_DOCUMENT_TEMPLATE_OPTIONS[0].key);
    setParticipantSourceId('');
    setParticipantSearch('');
    setDocumentFile(null);
  }, [caseId]);

  useEffect(() => {
    setParticipantSourceId('');
    setParticipantSearch('');
  }, [participantSourceType]);

  useEffect(() => {
    writeCaseDetailViewState(caseId, {
      activeTab,
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

    apiService.getCaseV2(caseId)
      .then((loadedCase) => {
        if (cancelled) return;
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
    if (activeTab !== 'tasks') return;

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
  }, [activeTab, caseId, reloadToken]);

  useEffect(() => {
    if (activeTab !== 'participants') return;

    let cancelled = false;
    setParticipantsLoading(true);
    apiService.getCaseRelatedParties(caseId)
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
  }, [activeTab, caseId, reloadToken]);

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
    if (activeTab !== 'documents') return;

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
  }, [activeTab, caseId, reloadToken]);

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
    if (activeTab !== 'finance') return;

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
  }, [activeTab, caseId, reloadToken]);

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
  }, [caseItem]);

  const filteredTasks = useMemo(() => {
    if (taskFilter === 'all') return tasks;
    if (taskFilter === 'done') return tasks.filter((task) => task.status === 'done' || task.status === 'cancelled');
    if (taskFilter === 'overdue') return tasks.filter(isOverdueTask);
    return tasks.filter(isActiveTask);
  }, [taskFilter, tasks]);

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

  const saveHeaderChanges = async () => {
    if (!caseItem) return;

    setSavingHeader(true);
    setError(null);
    try {
      await apiService.bulkUpdateCases({
        ids: [caseItem.id],
        status: statusDraft,
        priority: priorityDraft,
        stage: stageDraft.trim(),
        responsibleUserId: responsibleDraft ? Number(responsibleDraft) : undefined,
      });
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить параметры дела.');
    } finally {
      setSavingHeader(false);
    }
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

  const generateDocument = async () => {
    if (!caseItem) return;

    setGeneratingDocument(true);
    setError(null);
    try {
      const selectedTemplate = documentTemplates.find((item) => item.key === documentTemplateKey)
        ?? documentTemplates[0]
        ?? FALLBACK_DOCUMENT_TEMPLATE_OPTIONS[0];
      await apiService.generateCaseDocumentTemplate(caseItem.id, {
        templateKey: selectedTemplate.key,
        documentType: selectedTemplate.documentType,
        status: 'Подготовлен',
      });
      setActiveTab('documents');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сформировать документ по делу.');
    } finally {
      setGeneratingDocument(false);
    }
  };

  const uploadDocument = async () => {
    if (!caseItem) return;
    if (!documentFile) {
      setError('Выберите файл документа для загрузки.');
      return;
    }

    setUploadingDocument(true);
    setError(null);
    try {
      await apiService.uploadCaseDocument(caseItem.id, documentFile, {
        documentType: documentTypeDraft.trim() || undefined,
        status: documentStatusDraft.trim() || undefined,
      });
      setDocumentTypeDraft('');
      setDocumentStatusDraft('');
      setDocumentFile(null);
      setActiveTab('documents');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить документ по делу.');
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
      setDocumentPreview({
        document: caseDocument,
        url,
        contentType: result.blob.type || caseDocument.contentType || '',
        fileName: result.fileName || caseDocument.originalFileName || `case-document-${caseDocument.id}`,
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось открыть предпросмотр документа по делу.');
    } finally {
      setPreviewingDocumentId(null);
    }
  };

  const updateDocument = async (caseDocument: ClientCaseDocument, payload: { documentType: string; status: string }) => {
    if (!caseItem) return;

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

  const deleteDocument = async (caseDocument: ClientCaseDocument) => {
    if (!caseItem) return;
    if (!window.confirm(`Удалить документ «${caseDocument.originalFileName}»?`)) return;

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
    if (!caseItem) return;
    const text = conversationDraft.trim();
    if (!text) {
      setError('Введите текст сообщения по делу.');
      return;
    }

    setAddingConversationEntry(true);
    setError(null);
    setNotice(null);
    try {
      const items = await apiService.addCaseConversationEntry(caseItem.id, { text });
      setConversation(items ?? []);
      setConversationDraft('');
      setNotice('Сообщение добавлено в переписку по делу.');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить сообщение в переписку по делу.');
    } finally {
      setAddingConversationEntry(false);
    }
  };

  const runCourtSync = async () => {
    if (!caseItem) return;

    setSyncingCourt(true);
    setError(null);
    setNotice(null);
    try {
      const result = await apiService.runCaseCourtSync(caseItem.id);
      setNotice(result.message);
      setActiveTab('history');
      setReloadToken((value) => value + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось запустить проверку судебного источника.');
    } finally {
      setSyncingCourt(false);
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
    if (!window.confirm(`Удалить связь с участником «${party.displayName}»?`)) return;

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
    if (!caseItem) return;

    setCreatingTask(true);
    setError(null);
    try {
      await apiService.createTaskJournalCaseAutomationTask(caseItem.id);
      setReloadToken((value) => value + 1);
      setActiveTab('tasks');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать задачу по делу.');
    } finally {
      setCreatingTask(false);
    }
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
          <button type="button" onClick={onBack} className="mt-4 block rounded-lg border border-rose-200 bg-white px-4 py-2 text-sm font-semibold text-rose-700">
            Вернуться к реестру
          </button>
        </div>
      </div>
    );
  }

  const progress = caseItem.taskProgressPercent ?? 0;
  const overdueTasks = caseItem.overdueTaskCount ?? 0;
  const inactivitySummary = getInactivitySummary(caseItem);
  const courtSyncSummary = getCourtSyncSummary(caseItem);

  return (
    <div className="min-h-screen bg-slate-50 px-4 py-6 sm:px-6 lg:px-8">
      <div className="mx-auto max-w-[1500px] space-y-5">
        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
            <div>
              <button type="button" onClick={onBack} className="mb-4 inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <ArrowLeft className="h-4 w-4" /> Реестр дел
              </button>
              <div className="flex flex-wrap items-center gap-2">
                <span className="inline-flex items-center gap-2 rounded-full bg-blue-50 px-3 py-1 text-sm font-semibold text-blue-700">
                  <Briefcase className="h-4 w-4" /> {caseItem.caseNumber || `#${caseItem.id}`}
                </span>
                <span className="rounded-full bg-slate-100 px-3 py-1 text-sm font-medium text-slate-700">{STATUS_LABELS[caseItem.status] ?? caseItem.status}</span>
                <span className="rounded-full bg-amber-50 px-3 py-1 text-sm font-medium text-amber-700">{PRIORITY_LABELS[caseItem.priority ?? ''] ?? caseItem.priority ?? 'Обычный'}</span>
              </div>
              <h1 className="mt-3 text-3xl font-bold text-slate-900">{caseItem.title || 'Без названия'}</h1>
              <p className="mt-2 max-w-3xl text-sm text-slate-600">{caseItem.description || 'Описание по делу пока не заполнено.'}</p>
            </div>

            <div className="flex flex-wrap gap-2">
              <button type="button" onClick={() => setReloadToken((value) => value + 1)} className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <RefreshCw className="h-4 w-4" /> Обновить
              </button>
              <button type="button" onClick={() => void copyCaseLink()} className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                {copiedLink ? <Check className="h-4 w-4 text-emerald-600" /> : <Copy className="h-4 w-4" />} {copiedLink ? 'Ссылка скопирована' : 'Ссылка'}
              </button>
              <button type="button" onClick={() => void createTaskForCase()} disabled={creatingTask} className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-50">
                {creatingTask ? <Loader2 className="h-4 w-4 animate-spin" /> : <ClipboardPlus className="h-4 w-4" />} Создать задачу
              </button>
              <button type="button" onClick={() => void generateDocument()} disabled={generatingDocument} className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
                {generatingDocument ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} Документ
              </button>
              <button type="button" onClick={() => void runCourtSync()} disabled={syncingCourt} aria-label="Проверить суд по делу" className="inline-flex items-center gap-2 rounded-lg border border-cyan-200 px-4 py-2 text-sm font-medium text-cyan-700 hover:bg-cyan-50 disabled:opacity-50">
                {syncingCourt ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Суд
              </button>
              <button type="button" onClick={openOwner} className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50">
                <UserRound className="h-4 w-4" /> Клиент
              </button>
              <button type="button" onClick={() => onOpenFinance?.(caseItem)} disabled={!onOpenFinance} className="inline-flex items-center gap-2 rounded-lg border border-indigo-200 px-4 py-2 text-sm font-medium text-indigo-700 hover:bg-indigo-50 disabled:opacity-50">
                <WalletCards className="h-4 w-4" /> Финансы
              </button>
              <button type="button" onClick={() => void archiveCase()} disabled={savingHeader || caseItem.status === 'Closed'} className="inline-flex items-center gap-2 rounded-lg border border-amber-200 px-4 py-2 text-sm font-medium text-amber-700 hover:bg-amber-50 disabled:opacity-50">
                {savingHeader ? <Loader2 className="h-4 w-4 animate-spin" /> : <Archive className="h-4 w-4" />} Архив
              </button>
            </div>
          </div>
        </div>

        {error ? <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div> : null}
        {notice ? <div className="rounded-xl border border-cyan-200 bg-cyan-50 px-4 py-3 text-sm text-cyan-800">{notice}</div> : null}

        <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-[170px_1fr_170px_220px_auto]">
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
              <select
                aria-label="Статус дела"
                value={statusDraft}
                onChange={(event) => setStatusDraft(event.target.value)}
                className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                {CASE_STATUS_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Стадия</span>
              <input
                aria-label="Стадия дела"
                value={stageDraft}
                onChange={(event) => setStageDraft(event.target.value)}
                className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                placeholder="Например: судебное производство"
              />
            </label>
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Приоритет</span>
              <select
                aria-label="Приоритет дела"
                value={priorityDraft}
                onChange={(event) => setPriorityDraft(event.target.value)}
                className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                {CASE_PRIORITY_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </label>
            <label className="text-sm font-medium text-slate-700">
              <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Ответственный</span>
              <select
                aria-label="Ответственный по делу"
                value={responsibleDraft}
                onChange={(event) => setResponsibleDraft(event.target.value)}
                className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                <option value="">Не выбран</option>
                {responsibles.map((responsible) => (
                  <option key={responsible.id} value={responsible.id}>{responsible.fullName}</option>
                ))}
              </select>
            </label>
            <button
              type="button"
              onClick={() => void saveHeaderChanges()}
              disabled={savingHeader}
              className="inline-flex items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50 xl:self-end">
              {savingHeader ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Сохранить
            </button>
          </div>
        </div>

        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4 2xl:grid-cols-7">
          <SummaryCard label="Клиент" value={getOwnerName(caseItem)} />
          <SummaryCard label="Ответственный" value={getResponsibleName(caseItem)} />
          <SummaryCard label="Ближайший срок" value={toRuDate(caseItem.dueDate) || toRuDate(caseItem.nextHearingAt) || '—'} />
          <SummaryCard label="Просрочки" value={getRiskLabel(caseItem)} tone={overdueTasks > 0 ? 'rose' : 'emerald'} />
          <SummaryCard label="Оплачено / долг" value={`${formatMoney(caseItem.incomePaid)} / ${formatMoney(caseItem.incomeOutstanding)}`} />
          <SummaryCard label="Активность" value={inactivitySummary.value} tone={inactivitySummary.tone} />
          <SummaryCard label="Судебная проверка" value={courtSyncSummary.value} tone={courtSyncSummary.tone} />
        </div>

        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
          <div className="grid gap-5 xl:grid-cols-[300px_minmax(0,1fr)]">
            <CaseClientSidebar
              caseItem={caseItem}
              onOpenClient={onOpenClient}
              onOpenLegalEntity={onOpenLegalEntity}
              onOpenFinance={onOpenFinance}
              onCreateInvoice={onCreateInvoice}
              onCreatePayment={onCreatePayment}
            />
            <div className="min-w-0">
          <div className="mb-5 flex flex-wrap gap-2">
            {DETAIL_TABS.map((tab) => (
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
                ['Заседание', toRuDate(caseItem.nextHearingAt) || '—'],
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
                    <select
                      aria-label="Тип участника"
                      value={participantSourceType}
                      onChange={(event) => setParticipantSourceType(event.target.value as ParticipantSourceType)}
                      className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
                      <option value="client">Клиент</option>
                      <option value="legalEntity">Юрлицо</option>
                      <option value="externalContact">Контакт</option>
                    </select>
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
                      <select
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
                      </select>
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
                  onOpenClient={(clientId) => onOpenClient?.(clientId, caseItem.id)}
                  onOpenLegalEntity={(legalEntityId) => onOpenLegalEntity?.(legalEntityId, caseItem.id)}
                  onDelete={(party) => void deleteParticipant(party)}
                  deletingPartyId={deletingParticipantId}
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
                creatingTask={creatingTask}
              />
            )
          ) : null}

          {activeTab === 'court' ? (
            <CourtPanel
              caseItem={caseItem}
              logs={courtSyncLogs}
              logsLoading={courtSyncLogsLoading}
              onRunCourtSync={runCourtSync}
              syncingCourt={syncingCourt}
            />
          ) : null}

          {activeTab === 'finance' ? (
            <FinancePanel
              caseItem={caseItem}
              detail={financeDetail}
              loading={financeLoading}
              onOpenFinance={onOpenFinance}
              onCreateInvoice={onCreateInvoice}
              onCreatePayment={onCreatePayment}
            />
          ) : null}

          {activeTab === 'documents' ? (
            <div className="space-y-4">
              <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 p-4">
                <div>
                  <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Документы дела</h2>
                  <div className="mt-1 text-sm text-slate-500">Версии и шаблоны хранятся в истории документов дела.</div>
                </div>
                <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
                  <label className="text-sm font-medium text-slate-700">
                    <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Шаблон</span>
                    <select
                      aria-label="Шаблон документа дела"
                      value={documentTemplateKey}
                      onChange={(event) => setDocumentTemplateKey(event.target.value)}
                      className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100 sm:w-56">
                      {documentTemplates.map((template) => (
                        <option key={template.key} value={template.key}>{template.label}</option>
                      ))}
                    </select>
                  </label>
                  <button type="button" onClick={() => void generateDocument()} disabled={generatingDocument} className="inline-flex items-center gap-2 rounded-lg border border-blue-200 px-4 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
                    {generatingDocument ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} Сформировать
                  </button>
                </div>
              </div>
              <section className="rounded-xl border border-slate-200 p-4">
                <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Загрузить файл</h2>
                <datalist id={documentTypeListId}>
                  {documentTypeOptions.map((type) => (
                    <option key={type} value={type} />
                  ))}
                </datalist>
                <div className="mt-4 grid gap-3 md:grid-cols-[1fr_180px_180px_auto]">
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
                    <select
                      aria-label="Статус документа"
                      value={documentStatusDraft}
                      onChange={(event) => setDocumentStatusDraft(event.target.value)}
                      className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
                    >
                      <option value="">По умолчанию: Подготовлен</option>
                      {documentStatusOptions.map((status) => (
                        <option key={status} value={status}>{status}</option>
                      ))}
                    </select>
                  </label>
                  <button
                    type="button"
                    onClick={() => void uploadDocument()}
                    disabled={uploadingDocument}
                    className="inline-flex items-center justify-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-800 disabled:opacity-50 md:self-end">
                    {uploadingDocument ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />} Загрузить
                  </button>
                </div>
              </section>
              {documentsLoading ? (
                <LoadingBlock label="Загрузка документов дела..." />
              ) : (
                <DocumentList
                  documents={documents}
                  downloadingDocumentId={downloadingDocumentId}
                  previewingDocumentId={previewingDocumentId}
                  updatingDocumentId={updatingDocumentId}
                  deletingDocumentId={deletingDocumentId}
                  documentTypeOptions={documentTypeOptions}
                  documentStatusOptions={documentStatusOptions}
                  onPreview={(caseDocument) => void previewDocument(caseDocument)}
                  onDownload={(caseDocument) => void downloadDocument(caseDocument)}
                  onUpdate={(caseDocument, payload) => void updateDocument(caseDocument, payload)}
                  onDelete={(caseDocument) => void deleteDocument(caseDocument)}
                />
              )}
            </div>
          ) : null}

          {activeTab === 'conversation' ? (
            <div className="space-y-4">
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
              {conversationLoading ? (
                <LoadingBlock label="Загрузка переписки по делу..." />
              ) : conversation.length === 0 ? (
                <div className="rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">Внутренней переписки по делу пока нет.</div>
              ) : (
                <ConversationTimeline entries={conversation} />
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
        <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-500">{entries.length} записей</span>
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

function ConversationTimeline({ entries }: { entries: ClientCaseConversationEntry[] }) {
  return (
    <section className="rounded-xl border border-slate-200 p-4">
      <div className="mb-4 flex items-center justify-between gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Лента переписки</h2>
        <span className="rounded-full bg-blue-50 px-2.5 py-1 text-xs font-semibold text-blue-700">{entries.length} сообщений</span>
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
          </article>
        ))}
      </div>
    </section>
  );
}

function CaseClientSidebar({
  caseItem,
  onOpenClient,
  onOpenLegalEntity,
  onOpenFinance,
  onCreateInvoice,
  onCreatePayment,
}: {
  caseItem: ClientCase;
  onOpenClient?: (clientId: number, caseId?: number) => void;
  onOpenLegalEntity?: (legalEntityId: number, caseId?: number) => void;
  onOpenFinance?: (caseItem: ClientCase) => void;
  onCreateInvoice?: (caseItem: ClientCase) => void;
  onCreatePayment?: (caseItem: ClientCase) => void;
}) {
  const [showSensitive, setShowSensitive] = useState(false);
  const phone = getOwnerPhone(caseItem);
  const email = getOwnerEmail(caseItem);
  const legalEntityId = caseItem.legalEntityId ?? caseItem.client?.legalEntityId ?? caseItem.client?.legalEntity?.id ?? null;

  return (
    <aside className="self-start rounded-xl border border-slate-200 bg-slate-50 p-4 xl:sticky xl:top-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">Клиент по делу</div>
          <h2 className="mt-2 break-words text-lg font-semibold text-slate-900">{getOwnerName(caseItem)}</h2>
          <div className="mt-2 inline-flex rounded-full bg-emerald-50 px-2.5 py-1 text-xs font-semibold text-emerald-700">{getOwnerStatus(caseItem)}</div>
        </div>
        <button
          type="button"
          onClick={() => setShowSensitive((value) => !value)}
          className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-600 hover:bg-slate-100">
          {showSensitive ? 'Скрыть' : 'Показать'}
        </button>
      </div>

      <dl className="mt-4 space-y-3 text-sm">
        <ClientSidebarRow label="Телефон" value={showSensitive ? (phone || '—') : maskSensitiveValue(phone)} />
        <ClientSidebarRow label="Email" value={showSensitive ? (email || '—') : maskSensitiveValue(email, 6)} />
        <ClientSidebarRow label="Организация" value={getOwnerOrganization(caseItem)} />
        <ClientSidebarRow label="Ответственный" value={getResponsibleName(caseItem)} />
      </dl>

      <div className="mt-4 grid gap-2">
        {phone ? (
          <a href={`tel:${phone}`} className="inline-flex items-center justify-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100">
            <Phone className="h-4 w-4" /> Позвонить
          </a>
        ) : null}
        {email ? (
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
          <h3 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Календарь оплат</h3>
          {calendarPayments.length === 0 ? <FinanceEmpty label="Плановых дат оплат пока нет." /> : null}
          <div className="mt-3 overflow-hidden rounded-lg border border-slate-100">
            {calendarPayments.slice(0, 12).map((payment) => (
              <div key={`calendar-${payment.id}`} className="grid gap-2 border-b border-slate-100 p-3 text-sm last:border-b-0 md:grid-cols-[120px_1fr_auto]">
                <div className="font-semibold text-slate-900">{toRuDate(payment.plannedDate || payment.date)}</div>
                <div className="text-slate-600">{payment.description || payment.invoiceNumber || `Платеж #${payment.id}`}</div>
                <div className="text-right font-semibold text-slate-900">{formatMoney(payment.amount)}</div>
              </div>
            ))}
          </div>
        </section>
      </div>
    </div>
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

function SummaryCard({ label, value, tone = 'slate' }: { label: string; value: string; tone?: SummaryTone }) {
  const toneClass =
    tone === 'emerald'
      ? 'text-emerald-700'
      : tone === 'rose'
        ? 'text-rose-700'
        : tone === 'amber'
          ? 'text-amber-700'
          : tone === 'cyan'
            ? 'text-cyan-700'
            : 'text-slate-900';
  return (
    <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-400">{label}</div>
      <div className={`mt-2 text-lg font-bold ${toneClass}`}>{value}</div>
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
  onOpenClient,
  onOpenLegalEntity,
  onDelete,
  deletingPartyId,
}: {
  parties: ClientCaseRelatedParty[];
  onOpenClient?: (clientId: number) => void;
  onOpenLegalEntity?: (legalEntityId: number) => void;
  onDelete?: (party: ClientCaseRelatedParty) => void;
  deletingPartyId?: number | null;
}) {
  const [showContacts, setShowContacts] = useState(false);

  return (
    <section className="rounded-xl border border-slate-200 p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Связанные лица</h2>
        {parties.some((party) => party.phone || party.email) ? (
          <button
            type="button"
            onClick={() => setShowContacts((value) => !value)}
            className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50">
            {showContacts ? 'Скрыть контакты' : 'Показать контакты'}
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
            {party.phone || party.email ? (
              <div className="mt-2 text-xs text-slate-500">{getRelatedPartyContactLine(party, showContacts)}</div>
            ) : null}
            {party.notes ? <div className="mt-2 text-xs text-slate-500">{party.notes}</div> : null}
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
            {onDelete ? (
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
  creatingTask,
}: {
  tasks: TaskJournalItem[];
  allTasks: TaskJournalItem[];
  progress: number;
  filter: CaseTaskFilter;
  onFilterChange: (filter: CaseTaskFilter) => void;
  onCreateTask: () => void;
  creatingTask: boolean;
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
            <button
              type="button"
              onClick={() => void onCreateTask()}
              disabled={creatingTask}
              className="inline-flex items-center gap-2 rounded-lg bg-emerald-600 px-3 py-1.5 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-50">
              {creatingTask ? <Loader2 className="h-4 w-4 animate-spin" /> : <ClipboardPlus className="h-4 w-4" />} Создать
            </button>
          </div>
        </div>
      </div>
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
                <td className="px-3 py-3 text-slate-700">{toRuDate(task.dueAt) || '—'}</td>
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

function CourtPanel({
  caseItem,
  logs,
  logsLoading,
  onRunCourtSync,
  syncingCourt,
}: {
  caseItem: ClientCase;
  logs: ClientCaseCourtSyncLog[];
  logsLoading: boolean;
  onRunCourtSync: () => void;
  syncingCourt: boolean;
}) {
  const hasCourtBinding = Boolean(caseItem.courtName || caseItem.courtCaseNumber || caseItem.courtUrl);
  const deadlines = [
    ['Подача заявления', caseItem.claimFiledAt],
    ['Ближайшее заседание', caseItem.nextHearingAt],
    ['Решение суда', caseItem.courtDecisionAt],
    ['Апелляция', caseItem.appealDeadlineAt],
    ['Кассация', caseItem.cassationDeadlineAt],
    ['Ходатайства', caseItem.motionDeadlineAt],
    ['Возражения', caseItem.objectionDeadlineAt],
  ].filter(([, value]) => Boolean(value)) as Array<[string, string]>;

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 rounded-xl border border-slate-200 p-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Судебный блок</h2>
          <div className="mt-1 text-sm text-slate-500">
            {hasCourtBinding ? 'Суд, номер дела, источник и процессуальные сроки по карточке.' : 'Судебная привязка пока не настроена.'}
          </div>
        </div>
        <button
          type="button"
          onClick={() => void onRunCourtSync()}
          disabled={syncingCourt}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-cyan-200 px-4 py-2 text-sm font-medium text-cyan-700 hover:bg-cyan-50 disabled:opacity-50">
          {syncingCourt ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Проверить источник
        </button>
      </div>

      <div className="grid gap-4 lg:grid-cols-[1fr_1fr]">
        <InfoSection title="Карточка суда" rows={[
          ['Суд', caseItem.courtName || '—'],
          ['Номер дела', caseItem.courtCaseNumber || '—'],
          ['Судья', caseItem.judgeName || '—'],
          ['Зал / кабинет', caseItem.courtroom || '—'],
          ['Секретарь / контакты', caseItem.courtSecretaryContacts || '—'],
        ]} />
        <section className="rounded-xl border border-slate-200 p-4">
          <h2 className="text-sm font-semibold uppercase tracking-[0.14em] text-slate-500">Источник</h2>
          {caseItem.courtUrl ? (
            <a
              href={caseItem.courtUrl}
              target="_blank"
              rel="noreferrer"
              className="mt-4 inline-flex break-all text-sm font-semibold text-blue-700 hover:underline">
              {caseItem.courtUrl}
            </a>
          ) : (
            <div className="mt-4 rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
              Ссылка на первоисточник не указана. Ручная проверка запишет это в историю дела.
            </div>
          )}
        </section>
      </div>

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
  downloadingDocumentId,
  previewingDocumentId,
  updatingDocumentId,
  deletingDocumentId,
  documentTypeOptions,
  documentStatusOptions,
  onPreview,
  onDownload,
  onUpdate,
  onDelete,
}: {
  documents: ClientCaseDocument[];
  downloadingDocumentId: number | null;
  previewingDocumentId: number | null;
  updatingDocumentId: number | null;
  deletingDocumentId: number | null;
  documentTypeOptions: string[];
  documentStatusOptions: string[];
  onPreview: (caseDocument: ClientCaseDocument) => void;
  onDownload: (caseDocument: ClientCaseDocument) => void;
  onUpdate: (caseDocument: ClientCaseDocument, payload: { documentType: string; status: string }) => void;
  onDelete: (caseDocument: ClientCaseDocument) => void;
}) {
  const [searchTerm, setSearchTerm] = useState('');
  const [typeFilter, setTypeFilter] = useState('all');
  const [statusFilter, setStatusFilter] = useState('all');
  const [latestOnly, setLatestOnly] = useState(false);

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
        <div className="grid gap-3 lg:grid-cols-[minmax(220px,1fr)_180px_180px_auto]">
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
            <select
              aria-label="Фильтр документов по типу"
              value={typeFilter}
              onChange={(event) => setTypeFilter(event.target.value)}
              className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
              <option value="all">Все типы</option>
              {documentTypes.map((type) => (
                <option key={type} value={type}>{type}</option>
              ))}
            </select>
          </label>
          <label className="text-sm font-medium text-slate-700">
            <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Статус</span>
            <select
              aria-label="Фильтр документов по статусу"
              value={statusFilter}
              onChange={(event) => setStatusFilter(event.target.value)}
              className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100">
              <option value="all">Все статусы</option>
              {documentStatuses.map((status) => (
                <option key={status} value={status}>{status}</option>
              ))}
            </select>
          </label>
          <label className="flex items-end gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 lg:self-end">
            <input
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
              downloading={downloadingDocumentId === document.id}
              previewing={previewingDocumentId === document.id}
              updating={updatingDocumentId === document.id}
              deleting={deletingDocumentId === document.id}
              documentTypeOptions={documentTypeOptions}
              documentStatusOptions={documentStatusOptions}
              onPreview={onPreview}
              onDownload={onDownload}
              onUpdate={onUpdate}
              onDelete={onDelete}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function normalizeDocumentFilter(value?: string | null) {
  return (value ?? '').trim().toLowerCase();
}

function DocumentCard({
  document,
  downloading,
  previewing,
  updating,
  deleting,
  documentTypeOptions,
  documentStatusOptions,
  onPreview,
  onDownload,
  onUpdate,
  onDelete,
}: {
  document: ClientCaseDocument;
  downloading: boolean;
  previewing: boolean;
  updating: boolean;
  deleting: boolean;
  documentTypeOptions: string[];
  documentStatusOptions: string[];
  onPreview: (caseDocument: ClientCaseDocument) => void;
  onDownload: (caseDocument: ClientCaseDocument) => void;
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

  return (
    <div className="rounded-xl border border-slate-200 p-4">
      <div className="flex items-start gap-3">
        <FileText className="mt-1 h-5 w-5 text-blue-600" />
        <div className="min-w-0 flex-1">
          <div className="font-semibold text-slate-900">{document.originalFileName}</div>
          <div className="mt-1 text-sm text-slate-500">
            {document.documentType} · v{document.version} · {document.status}
          </div>
          {document.templateLabel ? (
            <div className="mt-1 text-xs text-blue-600">Шаблон: {document.templateLabel}</div>
          ) : null}
          <div className="mt-2 text-xs text-slate-400">
            {document.uploadedByName || '—'} · {toRuDate(document.uploadedAt) || '—'}
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
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm normal-case tracking-normal text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
          />
        </label>
        <label className="text-xs font-semibold uppercase tracking-[0.12em] text-slate-400">
          Статус
          <select
            aria-label={`Статус документа ${document.originalFileName}`}
            value={status}
            onChange={(event) => setStatus(event.target.value)}
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm normal-case tracking-normal text-slate-700 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
          >
            {statusOptions.map((option) => (
              <option key={option} value={option}>{option}</option>
            ))}
          </select>
        </label>
      </div>
      <div className="mt-4 grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
        <button
          type="button"
          onClick={() => onUpdate(document, { documentType, status })}
          disabled={updating || deleting}
          aria-label={`Сохранить документ ${document.originalFileName}`}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 px-3 py-2 text-sm font-medium text-blue-700 hover:bg-blue-50 disabled:opacity-50">
          {updating ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />} Сохранить
        </button>
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
          onClick={() => onDelete(document)}
          disabled={updating || deleting}
          aria-label={`Удалить документ ${document.originalFileName}`}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-red-200 px-3 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:opacity-50">
          {deleting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />} Удалить
        </button>
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
  const inlinePreview = canPreviewInline(contentType);

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
