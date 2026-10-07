import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { CaseAppealDecisionAction } from '../Cases/CaseAppealDecisionAction';
import { CasePreHearingActions } from '../Cases/CasePreHearingActions';
import { CaseHearingPreparationChecklist } from '../Cases/CaseHearingPreparationChecklist';
import { CasePositionTemplatePicker } from '../Cases/CasePositionTemplatePicker';
import { CourtRecoveryPanel } from '../Cases/CourtRecoveryPanel';
import { ExecutiveWritDocumentUpload } from '../Cases/ExecutiveWritDocumentUpload';
import { CaseDocumentUploadStatusPanel } from '../Cases/CaseDocumentUploadStatusPanel';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { Download, ExternalLink, FileText, Link2, Plus, RotateCw, Trash2, Upload, X } from 'lucide-react';
import type { ActResponsible, ClientCase, ClientCaseActivity, ClientCaseDocument, ClientCaseDocumentTemplate, ClientCaseFinanceSummary, ClientCaseRelatedParty, LegalEntitySummary } from '../../types';
import { useDialog } from '../../hooks/useDialog';
import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';
import { courtAmountToInput, hasInvalidCourtAmounts, parseCourtAmountInput } from '../../utils/courtAmounts';
import { formatDateTimeInTimeZone, fromTimeZoneDateTimeInput, toTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';
import type { TaskJournalItem, TaskJournalPriority, TaskJournalStatus } from '../../utils/taskJournalStore';

interface CaseModalProps {
  isOpen: boolean;
  onClose: () => void;
  caseData: ClientCase; // для create передаём «пустой» кейс
  onSave: (patch: Partial<ClientCase>) => Promise<void> | void;
  onPositionReviewed?: (review: Pick<ClientCase, 'positionReviewedAt' | 'positionReviewedByUserId' | 'positionReviewedByName'>) => void;
  onNextHearingConfirmed?: (confirmation: Pick<ClientCase, 'nextHearingConfirmedForAt' | 'nextHearingConfirmedAt' | 'nextHearingConfirmedByUserId' | 'nextHearingConfirmedByName'>) => void;
  onDelete?: () => Promise<void> | void;
  responsibles?: ActResponsible[];
  overlayClassName?: string;
  allowEdit?: boolean;
  canViewFinance?: boolean;
  canUseContactRelatedParties?: boolean;
  canUseLegalEntityRelatedParties?: boolean;
  onOpenTask?: (taskId: number) => void;
  canCreateTasks?: boolean;
}

type StatusValue = 'Open' | 'OnHold' | 'Closed' | 'Lost';

const STATUS_OPTIONS: Array<{ value: StatusValue; label: string }> = [
  { value: 'Open', label: 'Открыто' },
  { value: 'OnHold', label: 'Приостановлено' },
  { value: 'Closed', label: 'Закрыто' },
  { value: 'Lost', label: 'Проиграно' },
];

const PRIORITY_OPTIONS = [
  { value: 'low', label: 'Низкий' },
  { value: 'normal', label: 'Обычный' },
  { value: 'high', label: 'Высокий' },
  { value: 'urgent', label: 'Критический' },
];

const DOCUMENT_TYPE_OPTIONS = [
  'Документ',
  'Иск',
  'Ходатайство',
  'Возражение',
  'Решение суда',
  'Исполнительный лист',
  'Переписка',
  'Доказательства',
  'Другое',
];

const DOCUMENT_STATUS_OPTIONS = [
  'Подготовлен',
  'Отправлен',
  'Получен',
  'Подписан',
  'На рассмотрении',
  'Архив',
];

const FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS: ClientCaseDocumentTemplate[] = [
  { key: 'case-summary', label: 'Справка по делу', documentType: 'Документ' },
  { key: 'claim', label: 'Исковое заявление', documentType: 'Иск' },
  { key: 'motion', label: 'Ходатайство', documentType: 'Ходатайство' },
  { key: 'objection', label: 'Возражение', documentType: 'Возражение' },
  { key: 'court-letter', label: 'Письмо в суд', documentType: 'Переписка' },
].map((template) => ({ ...template, heading: template.label }));

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

const CASE_TASK_STATUS_LABELS: Record<TaskJournalStatus, string> = {
  new: 'Новая',
  inProgress: 'В работе',
  waiting: 'Ожидает',
  onApproval: 'На согласовании',
  approved: 'Согласовано',
  revision: 'На доработке',
  overdueApproval: 'Просрочено на согласовании',
  done: 'Выполнена',
  cancelled: 'Отменена',
};

const CASE_TASK_PRIORITY_LABELS: Record<TaskJournalPriority, string> = {
  low: 'Низкий',
  normal: 'Обычный',
  high: 'Высокий',
  urgent: 'Критический',
};

function caseTaskStatusClassName(status: TaskJournalStatus) {
  switch (status) {
    case 'done':
      return 'bg-emerald-50 text-emerald-700 border-emerald-200';
    case 'cancelled':
      return 'bg-slate-50 text-slate-600 border-slate-200';
    case 'onApproval':
    case 'approved':
      return 'bg-violet-50 text-violet-700 border-violet-200';
    case 'revision':
    case 'waiting':
      return 'bg-amber-50 text-amber-700 border-amber-200';
    case 'overdueApproval':
      return 'bg-rose-50 text-rose-700 border-rose-200';
    case 'inProgress':
      return 'bg-blue-50 text-blue-700 border-blue-200';
    default:
      return 'bg-slate-50 text-slate-700 border-slate-200';
  }
}

function formatTaskDateTime(value?: string | null) {
  if (!value) return '';
  return formatDateTimeInTimeZone(value, activeCaseModalTimeZoneId);
}

function toDateInput(value?: string | null) {
  return value ? String(value).slice(0, 10) : '';
}

function toDateTimeInput(value?: string | null) {
  return toTimeZoneDateTimeInput(value, activeCaseModalTimeZoneId);
}

function fromDateInput(value: string) {
  return value || null;
}

function fromDateTimeInput(value: string) {
  return value ? fromTimeZoneDateTimeInput(value, activeCaseModalTimeZoneId) : null;
}

let activeCaseModalTimeZoneId = normalizeUserTimeZoneId();

function setActiveCaseModalTimeZoneId(value?: string | null) {
  activeCaseModalTimeZoneId = normalizeUserTimeZoneId(value);
}

function formatDateInput(year: number, monthIndex: number, day: number) {
  return `${year}-${String(monthIndex + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

function isFixedRussianHoliday(date: Date) {
  const month = date.getMonth() + 1;
  const day = date.getDate();
  return (month === 1 && day >= 1 && day <= 8)
    || (month === 2 && day === 23)
    || (month === 3 && day === 8)
    || (month === 5 && (day === 1 || day === 9))
    || (month === 6 && day === 12)
    || (month === 11 && day === 4);
}

function isRussianNonWorkingDay(date: Date) {
  return date.getDay() === 0 || date.getDay() === 6 || isFixedRussianHoliday(date);
}

function addMonthsToDateInput(value: string, monthsToAdd: number) {
  if (!value) return '';

  const [year, month, day] = value.split('-').map(Number);
  if (!year || !month || !day) return '';

  const sourceMonthIndex = month - 1;
  const targetMonthRaw = sourceMonthIndex + monthsToAdd;
  const targetYear = year + Math.floor(targetMonthRaw / 12);
  const targetMonthIndex = ((targetMonthRaw % 12) + 12) % 12;
  const daysInTargetMonth = new Date(targetYear, targetMonthIndex + 1, 0).getDate();
  let targetDate = new Date(targetYear, targetMonthIndex, Math.min(day, daysInTargetMonth));

  while (isRussianNonWorkingDay(targetDate)) {
    targetDate = new Date(targetDate.getFullYear(), targetDate.getMonth(), targetDate.getDate() + 1);
  }

  return formatDateInput(targetDate.getFullYear(), targetDate.getMonth(), targetDate.getDate());
}

function toAmount(value?: number | null) {
  return typeof value === 'number' && Number.isFinite(value) ? String(value) : '';
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return '0 Б';
  if (value < 1024) return `${value} Б`;
  const kb = value / 1024;
  if (kb < 1024) return `${kb.toFixed(kb >= 10 ? 0 : 1)} КБ`;
  const mb = kb / 1024;
  return `${mb.toFixed(mb >= 10 ? 0 : 1)} МБ`;
}

function formatDocumentDate(value?: string | null) {
  if (!value) return '';
  return formatDateTimeInTimeZone(value, activeCaseModalTimeZoneId);
}

function formatMoney(value?: number | null) {
  return formatCurrencySmart(value ?? 0).full;
}

function normalizeExternalUrl(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return '';
  return /^https?:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
}

function getDaysSince(value?: string | null) {
  if (!value) return null;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return null;
  return Math.max(0, Math.floor((Date.now() - date.getTime()) / 86_400_000));
}

function getRelatedPartySourceLabel(sourceType?: string | null) {
  switch (sourceType) {
    case 'client':
      return 'Физлицо';
    case 'externalContact':
      return 'Контакт';
    case 'legalEntity':
      return 'Организация';
    default:
      return 'Связь';
  }
}

function getContactOptionKey(item: ContactDirectoryItemDto) {
  return `${item.sourceType}:${item.sourceId}`;
}

export function CaseModal({
  isOpen,
  onClose,
  caseData,
  onSave,
  onPositionReviewed,
  onNextHearingConfirmed,
  onDelete,
  responsibles = [],
  overlayClassName,
  allowEdit = true,
  canViewFinance = true,
  canUseContactRelatedParties = true,
  canUseLegalEntityRelatedParties = true,
  onOpenTask,
  canCreateTasks = false,
}: CaseModalProps) {
  const { confirm, prompt } = useDialog();
  const [timeZoneId, setTimeZoneId] = useState(() => normalizeUserTimeZoneId());
  const [title, setTitle] = useState(caseData.title ?? '');
  const [description, setDescription] = useState(caseData.description ?? '');
  const [status, setStatus] = useState<StatusValue>((caseData.status as StatusValue) ?? 'Open');
  const [caseNumber, setCaseNumber] = useState(caseData.caseNumber ?? '');
  const [caseType, setCaseType] = useState(caseData.caseType ?? '');
  const [stage, setStage] = useState(caseData.stage ?? '');
  const [responsibleUserId, setResponsibleUserId] = useState(caseData.responsibleUserId ? String(caseData.responsibleUserId) : '');
  const [startDate, setStartDate] = useState(toDateInput(caseData.startDate));
  const [dueDate, setDueDate] = useState(toDateInput(caseData.dueDate));
  const [feeAmount, setFeeAmount] = useState(toAmount(caseData.feeAmount));
  const [courtClaimAmount, setCourtClaimAmount] = useState(courtAmountToInput(caseData.courtClaimAmount));
  const [courtAwardedAmount, setCourtAwardedAmount] = useState(courtAmountToInput(caseData.courtAwardedAmount));
  const [courtStateFeeAmount, setCourtStateFeeAmount] = useState(courtAmountToInput(caseData.courtStateFeeAmount));
  const [priority, setPriority] = useState(caseData.priority ?? 'normal');
  const [courtName, setCourtName] = useState(caseData.courtName ?? '');
  const [courtCaseNumber, setCourtCaseNumber] = useState(caseData.courtCaseNumber ?? '');
  const [courtUrl, setCourtUrl] = useState(caseData.courtUrl ?? '');
  const [nextHearingAt, setNextHearingAt] = useState(toDateTimeInput(caseData.nextHearingAt));
  const [claimant, setClaimant] = useState(caseData.claimant ?? '');
  const [claimantInn, setClaimantInn] = useState(caseData.claimantInn ?? '');
  const [claimantOgrn, setClaimantOgrn] = useState(caseData.claimantOgrn ?? '');
  const [ourCourtRole, setOurCourtRole] = useState(caseData.ourCourtRole ?? '');
  const [defendant, setDefendant] = useState(caseData.defendant ?? '');
  const [defendantInn, setDefendantInn] = useState(caseData.defendantInn ?? '');
  const [defendantOgrn, setDefendantOgrn] = useState(caseData.defendantOgrn ?? '');
  const [thirdParties, setThirdParties] = useState(caseData.thirdParties ?? '');
  const [representatives, setRepresentatives] = useState(caseData.representatives ?? '');
  const [judgeName, setJudgeName] = useState(caseData.judgeName ?? '');
  const [courtroom, setCourtroom] = useState(caseData.courtroom ?? '');
  const [courtSecretaryContacts, setCourtSecretaryContacts] = useState(caseData.courtSecretaryContacts ?? '');
  const [claimFiledAt, setClaimFiledAt] = useState(toDateInput(caseData.claimFiledAt));
  const [courtRegisteredAt, setCourtRegisteredAt] = useState(toDateInput(caseData.courtRegisteredAt));
  const [courtDecisionAt, setCourtDecisionAt] = useState(toDateInput(caseData.courtDecisionAt));
  const [courtDecisionEffectiveAt, setCourtDecisionEffectiveAt] = useState(toDateInput(caseData.courtDecisionEffectiveAt));
  const [executiveWritReceivedAt, setExecutiveWritReceivedAt] = useState(toDateInput(caseData.executiveWritReceivedAt));
  const [executiveWritSubmittedAt, setExecutiveWritSubmittedAt] = useState(toDateInput(caseData.executiveWritSubmittedAt));
  const [executiveWritSubmittedTo, setExecutiveWritSubmittedTo] = useState(caseData.executiveWritSubmittedTo ?? '');
  const [appealDeadlineAt, setAppealDeadlineAt] = useState(toDateInput(caseData.appealDeadlineAt));
  const [cassationDeadlineAt, setCassationDeadlineAt] = useState(toDateInput(caseData.cassationDeadlineAt));
  const [motionDeadlineAt, setMotionDeadlineAt] = useState(toDateInput(caseData.motionDeadlineAt));
  const [objectionDeadlineAt, setObjectionDeadlineAt] = useState(toDateInput(caseData.objectionDeadlineAt));
  const [documentsSummary, setDocumentsSummary] = useState(caseData.documentsSummary ?? '');
  const [eventHistory, setEventHistory] = useState(caseData.eventHistory ?? '');
  const [strategy, setStrategy] = useState(caseData.strategy ?? '');
  const [positionReview, setPositionReview] = useState({
    positionReviewedAt: caseData.positionReviewedAt ?? null,
    positionReviewedByUserId: caseData.positionReviewedByUserId ?? null,
    positionReviewedByName: caseData.positionReviewedByName ?? '',
  });
  const [reviewingPosition, setReviewingPosition] = useState(false);
  const [hearingConfirmation, setHearingConfirmation] = useState({
    nextHearingConfirmedForAt: caseData.nextHearingConfirmedForAt ?? null,
    nextHearingConfirmedAt: caseData.nextHearingConfirmedAt ?? null,
    nextHearingConfirmedByUserId: caseData.nextHearingConfirmedByUserId ?? null,
    nextHearingConfirmedByName: caseData.nextHearingConfirmedByName ?? '',
  });
  const [confirmingNextHearing, setConfirmingNextHearing] = useState(false);
  const [caseDocuments, setCaseDocuments] = useState<ClientCaseDocument[]>([]);
  const [documentsLoading, setDocumentsLoading] = useState(false);
  const [documentsSaving, setDocumentsSaving] = useState(false);
  const [uploadRevision, setUploadRevision] = useState(0);
  const [documentsError, setDocumentsError] = useState<string | null>(null);
  const [caseTasks, setCaseTasks] = useState<TaskJournalItem[]>([]);
  const [caseTasksLoading, setCaseTasksLoading] = useState(false);
  const [caseTasksError, setCaseTasksError] = useState<string | null>(null);
  const [financeSummary, setFinanceSummary] = useState<ClientCaseFinanceSummary | null>(null);
  const [financeLoading, setFinanceLoading] = useState(false);
  const [financeError, setFinanceError] = useState<string | null>(null);
  const [documentType, setDocumentType] = useState(DOCUMENT_TYPE_OPTIONS[0]);
  const [documentStatus, setDocumentStatus] = useState(DOCUMENT_STATUS_OPTIONS[0]);
  const [documentTemplates, setDocumentTemplates] = useState<ClientCaseDocumentTemplate[]>(FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS);
  const [documentTemplateKey, setDocumentTemplateKey] = useState(FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS[0].key);
  const availableDocumentTemplates = useMemo(
    () => filterCaseDocumentTemplatesByCaseType(documentTemplates, caseType),
    [documentTemplates, caseType],
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [activity, setActivity] = useState<ClientCaseActivity>({
    lastViewedAt: caseData.lastViewedAt ?? null,
    lastViewedByUserId: caseData.lastViewedByUserId ?? null,
    lastViewedByUserName: caseData.lastViewedByUserName ?? null,
    activityConfirmedAt: caseData.activityConfirmedAt ?? null,
    activityConfirmedByUserId: caseData.activityConfirmedByUserId ?? null,
    activityConfirmedByUserName: caseData.activityConfirmedByUserName ?? null,
    inactivityRemindersEnabled: caseData.inactivityRemindersEnabled ?? true,
    inactivityRemindersDisabledReason: caseData.inactivityRemindersDisabledReason ?? '',
    eventHistory: caseData.eventHistory ?? '',
  });
  const [activitySaving, setActivitySaving] = useState(false);
  const [relatedParties, setRelatedParties] = useState<ClientCaseRelatedParty[]>([]);
  const [relatedPartiesLoading, setRelatedPartiesLoading] = useState(false);
  const [relatedPartiesSaving, setRelatedPartiesSaving] = useState(false);
  const [relatedPartiesError, setRelatedPartiesError] = useState<string | null>(null);
  const [contactOptions, setContactOptions] = useState<ContactDirectoryItemDto[]>([]);
  const [legalEntityOptions, setLegalEntityOptions] = useState<LegalEntitySummary[]>([]);
  const [relatedPartyMode, setRelatedPartyMode] = useState<'contact' | 'legalEntity'>('contact');
  const [relatedPartySourceKey, setRelatedPartySourceKey] = useState('');
  const [relatedPartyLegalEntityId, setRelatedPartyLegalEntityId] = useState('');
  const [relatedPartyRole, setRelatedPartyRole] = useState('Участник');

  useEffect(() => {
    if (relatedPartyMode === 'contact' && !canUseContactRelatedParties && canUseLegalEntityRelatedParties) {
      setRelatedPartyMode('legalEntity');
      return;
    }

    if (relatedPartyMode === 'legalEntity' && !canUseLegalEntityRelatedParties && canUseContactRelatedParties) {
      setRelatedPartyMode('contact');
      return;
    }

    if (!canUseContactRelatedParties && !canUseLegalEntityRelatedParties) {
      setRelatedPartySourceKey('');
      setRelatedPartyLegalEntityId('');
    }
  }, [canUseContactRelatedParties, canUseLegalEntityRelatedParties, relatedPartyMode]);

  useEffect(() => {
    setActiveCaseModalTimeZoneId(timeZoneId);
  }, [timeZoneId]);

  useEffect(() => {
    if (!isOpen) return;
    let cancelled = false;
    const timeZoneRequest = typeof apiService.getUserSetting === 'function'
      ? apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY)
      : Promise.resolve(null);
    timeZoneRequest
      .then((response) => {
        if (cancelled) return;
        const nextTimeZoneId = normalizeUserTimeZoneId(response?.value);
        setActiveCaseModalTimeZoneId(nextTimeZoneId);
        setTimeZoneId(nextTimeZoneId);
        setNextHearingAt(toDateTimeInput(caseData.nextHearingAt));
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [caseData.nextHearingAt, isOpen]);

  useEffect(() => {
    setTitle(caseData.title ?? '');
    setDescription(caseData.description ?? '');
    setStatus((caseData.status as StatusValue) ?? 'Open');
    setCaseNumber(caseData.caseNumber ?? '');
    setCaseType(caseData.caseType ?? '');
    setStage(caseData.stage ?? '');
    setResponsibleUserId(caseData.responsibleUserId ? String(caseData.responsibleUserId) : '');
    setStartDate(toDateInput(caseData.startDate));
    setDueDate(toDateInput(caseData.dueDate));
    setFeeAmount(toAmount(caseData.feeAmount));
    setCourtClaimAmount(courtAmountToInput(caseData.courtClaimAmount));
    setCourtAwardedAmount(courtAmountToInput(caseData.courtAwardedAmount));
    setCourtStateFeeAmount(courtAmountToInput(caseData.courtStateFeeAmount));
    setPriority(caseData.priority ?? 'normal');
    setCourtName(caseData.courtName ?? '');
    setCourtCaseNumber(caseData.courtCaseNumber ?? '');
    setCourtUrl(caseData.courtUrl ?? '');
    setNextHearingAt(toDateTimeInput(caseData.nextHearingAt));
    setClaimant(caseData.claimant ?? '');
    setClaimantInn(caseData.claimantInn ?? '');
    setClaimantOgrn(caseData.claimantOgrn ?? '');
    setOurCourtRole(caseData.ourCourtRole ?? '');
    setDefendant(caseData.defendant ?? '');
    setDefendantInn(caseData.defendantInn ?? '');
    setDefendantOgrn(caseData.defendantOgrn ?? '');
    setThirdParties(caseData.thirdParties ?? '');
    setRepresentatives(caseData.representatives ?? '');
    setJudgeName(caseData.judgeName ?? '');
    setCourtroom(caseData.courtroom ?? '');
    setCourtSecretaryContacts(caseData.courtSecretaryContacts ?? '');
    setClaimFiledAt(toDateInput(caseData.claimFiledAt));
    setCourtRegisteredAt(toDateInput(caseData.courtRegisteredAt));
    setCourtDecisionAt(toDateInput(caseData.courtDecisionAt));
    setCourtDecisionEffectiveAt(toDateInput(caseData.courtDecisionEffectiveAt));
    setExecutiveWritReceivedAt(toDateInput(caseData.executiveWritReceivedAt));
    setExecutiveWritSubmittedAt(toDateInput(caseData.executiveWritSubmittedAt));
    setExecutiveWritSubmittedTo(caseData.executiveWritSubmittedTo ?? '');
    setAppealDeadlineAt(toDateInput(caseData.appealDeadlineAt));
    setCassationDeadlineAt(toDateInput(caseData.cassationDeadlineAt));
    setMotionDeadlineAt(toDateInput(caseData.motionDeadlineAt));
    setObjectionDeadlineAt(toDateInput(caseData.objectionDeadlineAt));
    setDocumentsSummary(caseData.documentsSummary ?? '');
    setEventHistory(caseData.eventHistory ?? '');
    setStrategy(caseData.strategy ?? '');
    setPositionReview({
      positionReviewedAt: caseData.positionReviewedAt ?? null,
      positionReviewedByUserId: caseData.positionReviewedByUserId ?? null,
      positionReviewedByName: caseData.positionReviewedByName ?? '',
    });
    setHearingConfirmation({
      nextHearingConfirmedForAt: caseData.nextHearingConfirmedForAt ?? null,
      nextHearingConfirmedAt: caseData.nextHearingConfirmedAt ?? null,
      nextHearingConfirmedByUserId: caseData.nextHearingConfirmedByUserId ?? null,
      nextHearingConfirmedByName: caseData.nextHearingConfirmedByName ?? '',
    });
    setDocumentsError(null);
    setCaseTasksError(null);
    setFinanceError(null);
    setDocumentType(DOCUMENT_TYPE_OPTIONS[0]);
    setDocumentStatus(DOCUMENT_STATUS_OPTIONS[0]);
    setDocumentTemplateKey(FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS[0].key);
    setActivity({
      lastViewedAt: caseData.lastViewedAt ?? null,
      lastViewedByUserId: caseData.lastViewedByUserId ?? null,
      lastViewedByUserName: caseData.lastViewedByUserName ?? null,
      activityConfirmedAt: caseData.activityConfirmedAt ?? null,
      activityConfirmedByUserId: caseData.activityConfirmedByUserId ?? null,
      activityConfirmedByUserName: caseData.activityConfirmedByUserName ?? null,
      inactivityRemindersEnabled: caseData.inactivityRemindersEnabled ?? true,
      inactivityRemindersDisabledReason: caseData.inactivityRemindersDisabledReason ?? '',
      eventHistory: caseData.eventHistory ?? '',
    });
    setActivitySaving(false);
    setRelatedPartiesError(null);
    setRelatedPartyMode('contact');
    setRelatedPartySourceKey('');
    setRelatedPartyLegalEntityId('');
    setRelatedPartyRole('Участник');
    setError(null);
    setSaving(false);
  }, [caseData, isOpen]);

  const refreshDocuments = useCallback(async () => {
    if (!isOpen || !caseData.id || caseData.id <= 0) {
      setCaseDocuments([]);
      setDocumentsLoading(false);
      return;
    }

    setDocumentsLoading(true);
    setDocumentsError(null);
    try {
      const documents = await apiService.getCaseDocuments(caseData.id);
      setCaseDocuments(documents);
    } catch (e: unknown) {
      setDocumentsError(e instanceof Error ? e.message : String(e));
    } finally {
      setDocumentsLoading(false);
    }
  }, [caseData.id, isOpen]);

  useEffect(() => {
    void refreshDocuments();
  }, [refreshDocuments]);

  useEffect(() => {
    if (!isOpen) return;

    let cancelled = false;
    apiService.getCaseDocumentTemplates()
      .then((items) => {
        if (cancelled) return;

        const templates = (items ?? []).filter((item) => item.key && item.label && item.documentType);
        const nextTemplates = templates.length > 0 ? templates : FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS;
        setDocumentTemplates(nextTemplates);
        setDocumentTemplateKey((current) => nextTemplates.some((item) => item.key === current) ? current : nextTemplates[0].key);
      })
      .catch(() => {
        if (cancelled) return;

        setDocumentTemplates(FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS);
        setDocumentTemplateKey((current) => FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS.some((item) => item.key === current)
          ? current
          : FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS[0].key);
      })

    return () => {
      cancelled = true;
    };
  }, [isOpen]);

  useEffect(() => {
    setDocumentTemplateKey((current) => (
      current && availableDocumentTemplates.some((item) => item.key === current)
        ? current
        : availableDocumentTemplates[0]?.key ?? ''
    ));
  }, [availableDocumentTemplates]);

  const refreshCaseTasks = useCallback(async () => {
    if (!isOpen || !caseData.id || caseData.id <= 0) {
      setCaseTasks([]);
      setCaseTasksLoading(false);
      return;
    }

    setCaseTasksLoading(true);
    setCaseTasksError(null);
    try {
      const result = await apiService.getTaskJournalItems({
        clientCaseId: caseData.id,
        page: 1,
        pageSize: 25,
      });
      setCaseTasks(result.items ?? []);
    } catch (e: unknown) {
      setCaseTasksError(e instanceof Error ? e.message : String(e));
    } finally {
      setCaseTasksLoading(false);
    }
  }, [caseData.id, isOpen]);

  useEffect(() => {
    void refreshCaseTasks();
  }, [refreshCaseTasks]);

  const refreshFinanceSummary = useCallback(async () => {
    if (!canViewFinance || !isOpen || !caseData.id || caseData.id <= 0) {
      setFinanceSummary(null);
      setFinanceLoading(false);
      return;
    }

    setFinanceLoading(true);
    setFinanceError(null);
    try {
      const summaries = caseData.legalEntityId
        ? await apiService.getLegalEntityCaseFinanceSummaries(caseData.legalEntityId)
        : caseData.clientId
          ? await apiService.getClientCaseFinanceSummaries(caseData.clientId)
          : [];
      setFinanceSummary(summaries.find((item) => item.caseId === caseData.id) ?? null);
    } catch (e: unknown) {
      setFinanceSummary(null);
      setFinanceError(e instanceof Error ? e.message : String(e));
    } finally {
      setFinanceLoading(false);
    }
  }, [canViewFinance, caseData.clientId, caseData.id, caseData.legalEntityId, isOpen]);

  useEffect(() => {
    void refreshFinanceSummary();
  }, [refreshFinanceSummary]);

  useEffect(() => {
    if (!isOpen || !caseData.id || caseData.id <= 0) return;

    let cancelled = false;
    apiService.markCaseActivity(caseData.id, { confirmed: false })
      .then((nextActivity) => {
        if (cancelled) return;
        setActivity(nextActivity);
        if (typeof nextActivity.eventHistory === 'string') {
          setEventHistory(nextActivity.eventHistory);
        }
      })
      .catch(() => {
        // Просмотр дела не должен блокировать работу с карточкой.
      });

    return () => {
      cancelled = true;
    };
  }, [caseData.id, isOpen]);

  const refreshRelatedParties = useCallback(async () => {
    if (!isOpen || !caseData.id || caseData.id <= 0) {
      setRelatedParties([]);
      setRelatedPartiesLoading(false);
      return;
    }

    setRelatedPartiesLoading(true);
    setRelatedPartiesError(null);
    try {
      const items = await apiService.getCaseRelatedParties(caseData.id);
      setRelatedParties(items);
    } catch (e: unknown) {
      setRelatedPartiesError(e instanceof Error ? e.message : String(e));
    } finally {
      setRelatedPartiesLoading(false);
    }
  }, [caseData.id, isOpen]);

  useEffect(() => {
    void refreshRelatedParties();
  }, [refreshRelatedParties]);

  useEffect(() => {
    if (!isOpen || !caseData.id || caseData.id <= 0) {
      setContactOptions([]);
      setLegalEntityOptions([]);
      return;
    }

    let cancelled = false;
    Promise.all([
      canUseContactRelatedParties ? apiService.getContacts({ scope: 'all' }) : Promise.resolve([]),
      canUseLegalEntityRelatedParties ? apiService.getLegalEntitiesPaged({ page: 1, pageSize: 100 }) : Promise.resolve({ items: [] }),
    ])
      .then(([contacts, legalEntities]) => {
        if (cancelled) return;
        setContactOptions(contacts.filter((item) => item.isActive).slice(0, 200));
        setLegalEntityOptions(legalEntities.items ?? []);
      })
      .catch(() => {
        if (cancelled) return;
        setContactOptions([]);
        setLegalEntityOptions([]);
      });

    return () => {
      cancelled = true;
    };
  }, [canUseContactRelatedParties, canUseLegalEntityRelatedParties, caseData.id, isOpen]);

  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'enter') {
        void handleSave();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, title, description, status]);

  const applyCalculatedProceduralDeadlines = (decisionDate = courtDecisionAt) => {
    if (!decisionDate) return;
    setAppealDeadlineAt(addMonthsToDateInput(decisionDate, 1));
    setCassationDeadlineAt(addMonthsToDateInput(decisionDate, 3));
  };

  const handleCourtDecisionChange = (value: string) => {
    const previousAutoAppealDeadline = addMonthsToDateInput(courtDecisionAt, 1);
    const previousAutoCassationDeadline = addMonthsToDateInput(courtDecisionAt, 3);
    const nextAutoAppealDeadline = addMonthsToDateInput(value, 1);
    const nextAutoCassationDeadline = addMonthsToDateInput(value, 3);

    setCourtDecisionAt(value);

    if (!appealDeadlineAt || appealDeadlineAt === previousAutoAppealDeadline) {
      setAppealDeadlineAt(nextAutoAppealDeadline);
    }

    if (!cassationDeadlineAt || cassationDeadlineAt === previousAutoCassationDeadline) {
      setCassationDeadlineAt(nextAutoCassationDeadline);
    }
  };

  if (!isOpen) return null;

  const validate = (): string | null => {
    if (!title.trim()) return 'Введите название дела';
    if (hasInvalidCourtAmounts(courtClaimAmount, courtAwardedAmount, courtStateFeeAmount)) return 'Судебные суммы укажите неотрицательным числом с точностью до копеек.';
    if (executiveWritSubmittedAt && (!executiveWritReceivedAt || executiveWritSubmittedAt < executiveWritReceivedAt)) return 'Дата подачи исполнительного листа должна быть не раньше даты получения.';
    if (executiveWritSubmittedAt && !executiveWritSubmittedTo) return 'Выберите, куда подан исполнительный лист.';
    return null;
  };

  const handleReviewPosition = async () => {
    if (!allowEdit || !caseData.id || !strategy.trim() || positionReview.positionReviewedAt) return;
    if (strategy.trim() !== (caseData.strategy ?? '').trim()) {
      setError('Сначала сохраните изменённую позицию по делу.');
      return;
    }
    setReviewingPosition(true);
    setError(null);
    try {
      const review = await apiService.reviewCasePosition(caseData.id);
      setPositionReview(review);
      onPositionReviewed?.(review);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось подтвердить проверку позиции.');
    } finally {
      setReviewingPosition(false);
    }
  };

  const handleConfirmNextHearing = async () => {
    if (!allowEdit || !caseData.id || !caseData.nextHearingAt) return;
    if (nextHearingAt !== toDateTimeInput(caseData.nextHearingAt)) {
      setError('Сначала сохраните изменённую дату следующего заседания.');
      return;
    }
    setConfirmingNextHearing(true);
    setError(null);
    try {
      const confirmation = await apiService.confirmNextHearing(caseData.id);
      setHearingConfirmation(confirmation);
      onNextHearingConfirmed?.(confirmation);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось подтвердить дату следующего заседания.');
    } finally {
      setConfirmingNextHearing(false);
    }
  };

  const handleSave = async () => {
    if (!allowEdit) {
      setError('Недостаточно прав для редактирования дела.');
      return;
    }

    const v = validate();
    if (v) {
      setError(v);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const patch: Partial<ClientCase> = {
        title: title.trim(),
        description: description.trim(),
        status,
        caseNumber: caseNumber.trim(),
        caseType: caseType.trim(),
        stage: stage.trim(),
        responsibleUserId: responsibleUserId ? Number(responsibleUserId) : null,
        startDate: fromDateInput(startDate),
        dueDate: fromDateInput(dueDate),
        ...(canViewFinance ? { feeAmount: Number(feeAmount.replace(',', '.')) || 0 } : {}),
        priority,
        courtName: courtName.trim(),
        courtClaimAmount: parseCourtAmountInput(courtClaimAmount),
        courtAwardedAmount: parseCourtAmountInput(courtAwardedAmount),
        courtStateFeeAmount: parseCourtAmountInput(courtStateFeeAmount),
        clearCourtClaimAmount: !courtClaimAmount.trim(),
        clearCourtAwardedAmount: !courtAwardedAmount.trim(),
        clearCourtStateFeeAmount: !courtStateFeeAmount.trim(),
        courtCaseNumber: courtCaseNumber.trim(),
        courtUrl: courtUrl.trim(),
        nextHearingAt: fromDateTimeInput(nextHearingAt),
        claimant: claimant.trim(),
        claimantInn: claimantInn.trim(),
        claimantOgrn: claimantOgrn.trim(),
        ourCourtRole,
        defendant: defendant.trim(),
        defendantInn: defendantInn.trim(),
        defendantOgrn: defendantOgrn.trim(),
        thirdParties: thirdParties.trim(),
        representatives: representatives.trim(),
        judgeName: judgeName.trim(),
        courtroom: courtroom.trim(),
        courtSecretaryContacts: courtSecretaryContacts.trim(),
        claimFiledAt: fromDateInput(claimFiledAt),
        courtRegisteredAt: fromDateInput(courtRegisteredAt),
        clearCourtRegisteredAt: !courtRegisteredAt,
        courtDecisionAt: fromDateInput(courtDecisionAt),
        courtDecisionEffectiveAt: fromDateInput(courtDecisionEffectiveAt),
        clearCourtDecisionEffectiveAt: !courtDecisionEffectiveAt,
        executiveWritReceivedAt: fromDateInput(executiveWritReceivedAt),
        clearExecutiveWritReceivedAt: !executiveWritReceivedAt,
        executiveWritSubmittedAt: fromDateInput(executiveWritSubmittedAt),
        executiveWritSubmittedTo: executiveWritSubmittedAt ? executiveWritSubmittedTo : '',
        clearExecutiveWritSubmission: !executiveWritSubmittedAt,
        appealDeadlineAt: fromDateInput(appealDeadlineAt),
        cassationDeadlineAt: fromDateInput(cassationDeadlineAt),
        motionDeadlineAt: fromDateInput(motionDeadlineAt),
        objectionDeadlineAt: fromDateInput(objectionDeadlineAt),
        documentsSummary: documentsSummary.trim(),
        eventHistory: eventHistory.trim(),
        strategy: strategy.trim(),
        inactivityRemindersEnabled: activity.inactivityRemindersEnabled,
        inactivityRemindersDisabledReason: activity.inactivityRemindersEnabled
          ? ''
          : activity.inactivityRemindersDisabledReason?.trim(),
      };
      await onSave(patch);
      onClose();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete) return;
    const ok = await confirm({ title: 'Подтверждение удаления', description: 'Удалить это дело? Действие необратимо.', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' });
    if (!ok) return;
    setSaving(true);
    setError(null);
    try {
      await onDelete();
      onClose();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const handleUploadDocument = async (fileList: FileList | null, documentKey?: string) => {
    if (!allowEdit) {
      setDocumentsError('Недостаточно прав для изменения файлов дела.');
      return;
    }

    const file = fileList?.[0];
    if (!file || !caseData.id || caseData.id <= 0) return;

    setDocumentsSaving(true);
    setDocumentsError(null);
    try {
      await apiService.uploadCaseDocument(caseData.id, file, {
        documentType,
        status: documentStatus,
        documentKey,
      });
      await refreshDocuments();
    } catch (e: unknown) {
      setDocumentsError(e instanceof Error ? e.message : String(e));
    } finally {
      setDocumentsSaving(false);
      setUploadRevision((value) => value + 1);
    }
  };

  const handleGenerateDocumentTemplate = async () => {
    if (!allowEdit) {
      setDocumentsError('Недостаточно прав для изменения файлов дела.');
      return;
    }

    if (!caseData.id || caseData.id <= 0) return;

    const template = availableDocumentTemplates.find((item) => item.key === documentTemplateKey)
      ?? availableDocumentTemplates[0];
    if (!template) {
      setDocumentsError('Нет доступных шаблонов документов для типа этого дела.');
      return;
    }

    setDocumentsSaving(true);
    setDocumentsError(null);
    try {
      await apiService.generateCaseDocumentTemplate(caseData.id, {
        templateKey: template.key,
        documentType: template.documentType,
        status: documentStatus,
      });
      await refreshDocuments();
    } catch (e: unknown) {
      setDocumentsError(e instanceof Error ? e.message : String(e));
    } finally {
      setDocumentsSaving(false);
      setUploadRevision((value) => value + 1);
    }
  };

  const handleDownloadDocument = async (document: ClientCaseDocument) => {
    if (!caseData.id || caseData.id <= 0) return;
    setDocumentsError(null);
    try {
      const result = await apiService.downloadCaseDocument(caseData.id, document.id);
      const url = URL.createObjectURL(result.blob);
      const link = window.document.createElement('a');
      link.href = url;
      link.download = result.fileName || document.originalFileName;
      window.document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    } catch (e: unknown) {
      setDocumentsError(e instanceof Error ? e.message : String(e));
    }
  };

  const handleDeleteDocument = async (document: ClientCaseDocument) => {
    if (!allowEdit) {
      setDocumentsError('Недостаточно прав для удаления файлов дела.');
      return;
    }

    if (!caseData.id || caseData.id <= 0) return;
    const ok = await confirm({
      title: 'Удаление файла',
      description: `Удалить файл «${document.originalFileName}»?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!ok) return;

    setDocumentsSaving(true);
    setDocumentsError(null);
    try {
      await apiService.deleteCaseDocument(caseData.id, document.id);
      await refreshDocuments();
    } catch (e: unknown) {
      setDocumentsError(e instanceof Error ? e.message : String(e));
    } finally {
      setDocumentsSaving(false);
    }
  };

  const handleUpdateDocument = async (document: ClientCaseDocument, patch: { documentType?: string; status?: string }) => {
    if (!allowEdit) {
      setDocumentsError('Недостаточно прав для изменения файлов дела.');
      return;
    }

    if (!caseData.id || caseData.id <= 0) return;

    setDocumentsSaving(true);
    setDocumentsError(null);
    try {
      await apiService.updateCaseDocument(caseData.id, document.id, patch);
      await refreshDocuments();
    } catch (e: unknown) {
      setDocumentsError(e instanceof Error ? e.message : String(e));
    } finally {
      setDocumentsSaving(false);
    }
  };

  const handleConfirmActivity = async () => {
    if (!caseData.id || caseData.id <= 0) return;

    setActivitySaving(true);
    setError(null);
    try {
      const nextActivity = await apiService.markCaseActivity(caseData.id, { confirmed: true });
      setActivity(nextActivity);
      if (typeof nextActivity.eventHistory === 'string') {
        setEventHistory(nextActivity.eventHistory);
      }
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setActivitySaving(false);
    }
  };

  const handleInactivityRemindersChange = async (enabled: boolean) => {
    if (enabled) {
      setActivity((current) => ({
        ...current,
        inactivityRemindersEnabled: true,
        inactivityRemindersDisabledReason: '',
      }));
      return;
    }

    const reason = await prompt({
      title: 'Выключить автоконтроль дела',
      label: 'Причина выключения',
      defaultValue: activity.inactivityRemindersDisabledReason ?? '',
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
    setActivity((current) => ({
      ...current,
      inactivityRemindersEnabled: false,
      inactivityRemindersDisabledReason: reason.trim(),
    }));
  };

  const handleAddRelatedParty = async () => {
    if (!allowEdit) {
      setRelatedPartiesError('Недостаточно прав для изменения связей дела.');
      return;
    }

    if (!canUseContactRelatedParties && !canUseLegalEntityRelatedParties) {
      setRelatedPartiesError('У роли нет доступа к справочникам клиентов и организаций.');
      return;
    }

    if (relatedPartyMode === 'contact' && !canUseContactRelatedParties) {
      setRelatedPartiesError('У роли нет доступа к справочнику клиентов и контактов.');
      return;
    }

    if (relatedPartyMode === 'legalEntity' && !canUseLegalEntityRelatedParties) {
      setRelatedPartiesError('У роли нет доступа к справочнику юридических лиц.');
      return;
    }

    if (!caseData.id || caseData.id <= 0) return;

    const source =
      relatedPartyMode === 'legalEntity'
        ? { sourceType: 'legalEntity', sourceId: Number(relatedPartyLegalEntityId) }
        : (() => {
            const [sourceType, sourceId] = relatedPartySourceKey.split(':');
            return {
              sourceType: sourceType === 'external' ? 'externalContact' : sourceType,
              sourceId: Number(sourceId),
            };
          })();

    if (!source.sourceType || !Number.isFinite(source.sourceId) || source.sourceId <= 0) {
      setRelatedPartiesError('Выберите связанное лицо или организацию из справочника.');
      return;
    }

    setRelatedPartiesSaving(true);
    setRelatedPartiesError(null);
    try {
      const created = await apiService.addCaseRelatedParty(caseData.id, {
        sourceType: source.sourceType,
        sourceId: source.sourceId,
        role: relatedPartyRole.trim() || 'Участник',
      });
      setRelatedParties((items) => [...items, created].sort((a, b) => a.displayName.localeCompare(b.displayName, 'ru')));
      setRelatedPartySourceKey('');
      setRelatedPartyLegalEntityId('');
    } catch (e: unknown) {
      setRelatedPartiesError(e instanceof Error ? e.message : String(e));
    } finally {
      setRelatedPartiesSaving(false);
    }
  };

  const handleDeleteRelatedParty = async (party: ClientCaseRelatedParty) => {
    if (!allowEdit) {
      setRelatedPartiesError('Недостаточно прав для изменения связей дела.');
      return;
    }

    if (!caseData.id || caseData.id <= 0) return;
    const ok = await confirm({
      title: 'Удаление связи',
      description: `Удалить связь «${party.displayName}» из дела?`,
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!ok) return;

    setRelatedPartiesSaving(true);
    setRelatedPartiesError(null);
    try {
      await apiService.deleteCaseRelatedParty(caseData.id, party.id);
      setRelatedParties((items) => items.filter((item) => item.id !== party.id));
    } catch (e: unknown) {
      setRelatedPartiesError(e instanceof Error ? e.message : String(e));
    } finally {
      setRelatedPartiesSaving(false);
    }
  };

  const normalizedCourtUrl = normalizeExternalUrl(courtUrl);
  const enteredFee = Number(feeAmount.replace(',', '.'));
  const contractAmount = Number.isFinite(enteredFee) ? enteredFee : caseData.feeAmount ?? 0;
  const incomeTotal = financeSummary?.incomeTotal ?? 0;
  const incomePaid = financeSummary?.incomePaid ?? 0;
  const incomeOutstanding = financeSummary?.incomeOutstanding ?? Math.max(0, incomeTotal - incomePaid);
  const daysSinceActivity = getDaysSince(activity.lastViewedAt ?? caseData.lastViewedAt);

  const modal = (
    <div className={`fixed inset-0 ${overlayClassName ?? 'z-[1000]'} bg-black/30 flex items-center justify-center p-4`}>
      <div
        className="w-full max-w-4xl bg-white rounded-2xl shadow-xl"
        role="dialog"
        aria-modal="true">
        <div className="flex items-center justify-between px-5 py-4 border-b">
          <h3 className="text-lg font-semibold">Дело</h3>
          <button
            type="button"
            onClick={onClose}
            className="p-2 rounded-lg hover:bg-gray-100"
            aria-label="Закрыть">
            <X size={18} />
          </button>
        </div>

        <div className="max-h-[72vh] overflow-y-auto px-5 py-4">
          {error && (
            <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg px-3 py-2">
              {error}
            </div>
          )}

          {caseData.id > 0 && (
            <div className="mb-4 flex flex-col gap-3 rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 md:flex-row md:items-center md:justify-between">
              <div className="text-sm text-slate-700">
                <div className="font-semibold text-slate-900">
                  Активность по делу
                  {daysSinceActivity !== null ? ` · ${daysSinceActivity} дн.` : ''}
                </div>
                <div className="mt-1 text-xs text-slate-500">
                  Последний просмотр: {formatDocumentDate(activity.lastViewedAt ?? caseData.lastViewedAt) || 'не фиксировался'}
                  {activity.lastViewedByUserName ? ` · ${activity.lastViewedByUserName}` : ''}
                </div>
                {activity.activityConfirmedAt ? (
                  <div className="mt-1 text-xs text-emerald-700">
                    Подтверждено: {formatDocumentDate(activity.activityConfirmedAt)}
                    {activity.activityConfirmedByUserName ? ` · ${activity.activityConfirmedByUserName}` : ''}
                  </div>
                ) : null}
              </div>
              <div className="flex flex-col gap-2 sm:items-end">
                <label className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700">
                  <SystemCheckbox
                    type="checkbox"
                    className="h-4 w-4 rounded border-slate-300"
                    checked={activity.inactivityRemindersEnabled}
                    onChange={(e) => void handleInactivityRemindersChange(e.target.checked)}
                    disabled={saving || activitySaving || !allowEdit}
                  />
                  Автоконтроль дела
                </label>
                {!activity.inactivityRemindersEnabled && activity.inactivityRemindersDisabledReason ? (
                  <div className="max-w-xs text-right text-xs text-amber-700">
                    Причина: {activity.inactivityRemindersDisabledReason}
                  </div>
                ) : null}
                <button
                  type="button"
                  onClick={() => void handleConfirmActivity()}
                  disabled={activitySaving || saving}
                  className="inline-flex items-center justify-center rounded-lg border border-emerald-200 bg-white px-3 py-2 text-sm font-semibold text-emerald-700 hover:bg-emerald-50 disabled:cursor-not-allowed disabled:opacity-60">
                  {activitySaving ? 'Фиксируем...' : 'Подтвердить активность'}
                </button>
              </div>
            </div>
          )}

          <div className="grid gap-4 md:grid-cols-2">
          <div>
            <label className="text-sm text-gray-600">
              Название <span className="text-red-500">*</span>
            </label>
            <input
              aria-label="Название дела"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="Например: Соглашение №123"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Номер дела</label>
            <input
              aria-label="Номер дела"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={caseNumber}
              onChange={(e) => setCaseNumber(e.target.value)}
              placeholder="Например: 2026-00123"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Тип дела</label>
            <input
              aria-label="Тип дела"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={caseType}
              onChange={(e) => setCaseType(e.target.value)}
              placeholder="Например: Банкротство"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Текущий этап</label>
            <input
              aria-label="Текущий этап"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={stage}
              onChange={(e) => setStage(e.target.value)}
              placeholder="Например: Подготовка заявления"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Ответственный</label>
            <SystemSelect
              aria-label="Ответственный"
              className="mt-1 w-full border rounded-lg px-3 py-2 bg-white"
              value={responsibleUserId}
              onChange={(e) => setResponsibleUserId(e.target.value)}
              disabled={saving}>
              <option value="">Ответственный владельца</option>
              {responsibles.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.fullName}
                </option>
              ))}
            </SystemSelect>
          </div>

          <div>
            <label className="text-sm text-gray-600">Приоритет</label>
            <SystemSelect
              aria-label="Приоритет"
              className="mt-1 w-full border rounded-lg px-3 py-2 bg-white"
              value={priority}
              onChange={(e) => setPriority(e.target.value)}
              disabled={saving}>
              {PRIORITY_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </SystemSelect>
          </div>

          <div>
            <label className="text-sm text-gray-600">Дата начала</label>
            <SystemDateInput
              aria-label="Дата начала"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={startDate}
              onChange={(e) => setStartDate(e.target.value)}
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Крайний срок</label>
            <SystemDateInput
              aria-label="Крайний срок"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={dueDate}
              onChange={(e) => setDueDate(e.target.value)}
              disabled={saving}
            />
          </div>

          {canViewFinance ? (
            <div>
              <label className="text-sm text-gray-600">Сумма гонорара</label>
              <input
                aria-label="Сумма гонорара"
                type="number"
                min="0"
                step="0.01"
                className="mt-1 w-full border rounded-lg px-3 py-2"
                value={feeAmount}
                onChange={(e) => setFeeAmount(e.target.value)}
                placeholder="0.00"
                disabled={saving}
              />
            </div>
          ) : null}

          <div>
            <label className="text-sm text-gray-600">Описание</label>
            <textarea
              aria-label="Описание"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              rows={3}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Короткое описание дела"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Статус</label>
            <SystemSelect
              aria-label="Статус"
              className="mt-1 w-full border rounded-lg px-3 py-2 bg-white"
              value={status}
              onChange={(e) => setStatus(e.target.value as StatusValue)}
              disabled={saving}>
              {STATUS_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </SystemSelect>
          </div>

          {canViewFinance ? (
            <div className="md:col-span-2 rounded-xl border border-emerald-100 bg-emerald-50/50 p-4">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div>
                  <h4 className="text-sm font-semibold text-emerald-900">Финансы по делу</h4>
                  <p className="mt-0.5 text-xs text-emerald-700/80">
                    Суммы берутся из привязанных счетов и платежей по текущему делу.
                  </p>
                </div>
                {financeLoading ? (
                  <span className="text-xs text-emerald-700">Обновляем...</span>
                ) : null}
              </div>
              {financeSummary?.hasCompletePaymentVisibility === false ? (
                <p className="mt-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900" role="note">
                  Показаны только доступные вам платежи. Суммы и долг по делу могут быть неполными.
                </p>
              ) : null}
              {financeError ? (
                <div className="mt-3 rounded-lg border border-red-200 bg-white px-3 py-2 text-sm text-red-600">
                  {financeError}
                </div>
              ) : (
                <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                  <div className="rounded-lg border border-emerald-100 bg-white px-3 py-2">
                    <div className="text-xs text-slate-500">Сумма договора</div>
                    <div className="mt-1 text-sm font-semibold text-slate-900">{formatMoney(contractAmount)}</div>
                  </div>
                  <div className="rounded-lg border border-emerald-100 bg-white px-3 py-2">
                    <div className="text-xs text-slate-500">Счета по делу</div>
                    <div className="mt-1 text-sm font-semibold text-slate-900">{formatMoney(incomeTotal)}</div>
                  </div>
                  <div className="rounded-lg border border-emerald-100 bg-white px-3 py-2">
                    <div className="text-xs text-slate-500">Оплачено</div>
                    <div className="mt-1 text-sm font-semibold text-emerald-700">{formatMoney(incomePaid)}</div>
                  </div>
                  <div className="rounded-lg border border-emerald-100 bg-white px-3 py-2">
                    <div className="text-xs text-slate-500">Долг клиента</div>
                    <div className={`mt-1 text-sm font-semibold ${incomeOutstanding > 0 ? 'text-rose-700' : 'text-slate-900'}`}>
                      {formatMoney(incomeOutstanding)}
                    </div>
                  </div>
                </div>
              )}
            </div>
          ) : null}

          <div className="md:col-span-2 mt-2 border-t pt-4">
            <h4 className="text-sm font-semibold text-gray-700 tracking-wide uppercase">Участники процесса</h4>
          </div>

          <div>
            <label className="text-sm text-gray-600">Наша роль в деле</label>
            <SystemSelect aria-label="Наша роль в деле" className="mt-1 w-full border rounded-lg px-3 py-2" value={ourCourtRole} onChange={(event) => setOurCourtRole(event.target.value as typeof ourCourtRole)} disabled={saving}>
              <option value="">Не указана</option><option value="claimant">Истец</option><option value="respondent">Ответчик</option><option value="third-party">Третье лицо</option>
            </SystemSelect>
          </div>

          <div>
            <label className="text-sm text-gray-600">Истец / заявитель</label>
            <input
              aria-label="Истец / заявитель"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={claimant}
              onChange={(e) => setClaimant(e.target.value)}
              placeholder="ФИО или наименование"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">ИНН истца</label>
            <input aria-label="ИНН истца" inputMode="numeric" maxLength={12} className="mt-1 w-full border rounded-lg px-3 py-2" value={claimantInn} onChange={(event) => setClaimantInn(event.target.value)} disabled={saving} />
          </div>

          <div>
            <label className="text-sm text-gray-600">ОГРН/ОГРНИП истца</label>
            <input aria-label="ОГРН/ОГРНИП истца" inputMode="numeric" maxLength={15} className="mt-1 w-full border rounded-lg px-3 py-2" value={claimantOgrn} onChange={(event) => setClaimantOgrn(event.target.value)} disabled={saving} />
          </div>

          <div>
            <label className="text-sm text-gray-600">Ответчик / должник</label>
            <input
              aria-label="Ответчик / должник"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={defendant}
              onChange={(e) => setDefendant(e.target.value)}
              placeholder="ФИО или наименование"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">ИНН ответчика</label>
            <input aria-label="ИНН ответчика" inputMode="numeric" maxLength={12} className="mt-1 w-full border rounded-lg px-3 py-2" value={defendantInn} onChange={(event) => setDefendantInn(event.target.value)} disabled={saving} />
          </div>

          <div>
            <label className="text-sm text-gray-600">ОГРН/ОГРНИП ответчика</label>
            <input aria-label="ОГРН/ОГРНИП ответчика" inputMode="numeric" maxLength={15} className="mt-1 w-full border rounded-lg px-3 py-2" value={defendantOgrn} onChange={(event) => setDefendantOgrn(event.target.value)} disabled={saving} />
          </div>

          <div>
            <label className="text-sm text-gray-600">Третьи лица</label>
            <textarea
              aria-label="Третьи лица"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              rows={3}
              value={thirdParties}
              onChange={(e) => setThirdParties(e.target.value)}
              placeholder="Участники через строку или запятую"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Представители</label>
            <textarea
              aria-label="Представители"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              rows={3}
              value={representatives}
              onChange={(e) => setRepresentatives(e.target.value)}
              placeholder="Юристы, доверенности, контакты"
              disabled={saving}
            />
          </div>

          <div className="md:col-span-2 grid gap-3 rounded-xl border border-slate-200 bg-slate-50/60 p-3 sm:grid-cols-3">
            <label className="text-sm text-gray-600">Сумма иска, ₽
              <input aria-label="Сумма иска" inputMode="decimal" value={courtClaimAmount} onChange={(event) => setCourtClaimAmount(event.target.value)} disabled={saving || !allowEdit} className="mt-1 w-full rounded-lg border px-3 py-2" placeholder="Не указана" />
            </label>
            <label className="text-sm text-gray-600">Присуждено судом, ₽
              <input aria-label="Присуждено судом" inputMode="decimal" value={courtAwardedAmount} onChange={(event) => setCourtAwardedAmount(event.target.value)} disabled={saving || !allowEdit} className="mt-1 w-full rounded-lg border px-3 py-2" placeholder="Не указано" />
            </label>
            <label className="text-sm text-gray-600">Госпошлина, ₽
              <input aria-label="Госпошлина по делу" inputMode="decimal" value={courtStateFeeAmount} onChange={(event) => setCourtStateFeeAmount(event.target.value)} disabled={saving || !allowEdit} className="mt-1 w-full rounded-lg border px-3 py-2" placeholder="Не указана" />
            </label>
            <p className="text-xs text-slate-500 sm:col-span-3">Судебные суммы заполняются вручную и не влияют на гонорар или счета клиента.</p>
          </div>
          {canViewFinance && caseData.id > 0 ? <div className="md:col-span-2"><CourtRecoveryPanel caseId={caseData.id} canEdit={allowEdit} /></div> : null}

          <div className="md:col-span-2 rounded-xl border border-slate-200 bg-slate-50/60 p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <h4 className="flex items-center gap-2 text-sm font-semibold text-slate-800">
                  <Link2 size={16} className="text-blue-600" /> Связанные лица и организации
                </h4>
                <p className="mt-0.5 text-xs text-slate-500">Добавляйте участников из справочника контактов, физлиц и юридических лиц.</p>
              </div>
              <span className="rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-slate-600">
                {relatedParties.length}
              </span>
            </div>

            {relatedPartiesError ? (
              <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
                {relatedPartiesError}
              </div>
            ) : null}

            {caseData.id > 0 ? (
              <div className="mt-3 grid gap-2 lg:grid-cols-[150px_minmax(220px,1fr)_160px_auto]">
                <SystemSelect
                  aria-label="Тип связанного участника"
                  className="rounded-lg border px-3 py-2 text-sm bg-white"
                  value={relatedPartyMode}
                  onChange={(e) => {
                    const nextMode = e.target.value as 'contact' | 'legalEntity';
                    setRelatedPartyMode(nextMode);
                    setRelatedPartySourceKey('');
                    setRelatedPartyLegalEntityId('');
                  }}
                  disabled={relatedPartiesSaving || saving || !allowEdit || (!canUseContactRelatedParties && !canUseLegalEntityRelatedParties)}>
                  {canUseContactRelatedParties ? <option value="contact">Лицо/контакт</option> : null}
                  {canUseLegalEntityRelatedParties ? <option value="legalEntity">Организация</option> : null}
                </SystemSelect>

                {!canUseContactRelatedParties && !canUseLegalEntityRelatedParties ? (
                  <div className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                    Справочники клиентов и организаций закрыты для вашей роли.
                  </div>
                ) : relatedPartyMode === 'legalEntity' ? (
                  <SystemSelect
                    aria-label="Связанная организация"
                    className="rounded-lg border px-3 py-2 text-sm bg-white"
                    value={relatedPartyLegalEntityId}
                    onChange={(e) => setRelatedPartyLegalEntityId(e.target.value)}
                    disabled={relatedPartiesSaving || saving || !allowEdit || !canUseLegalEntityRelatedParties}>
                    <option value="">Выберите организацию</option>
                    {legalEntityOptions.map((item) => (
                      <option key={item.id} value={item.id}>
                        {item.shortName || item.fullName || `Юрлицо #${item.id}`}
                      </option>
                    ))}
                  </SystemSelect>
                ) : (
                  <SystemSelect
                    aria-label="Связанное лицо"
                    className="rounded-lg border px-3 py-2 text-sm bg-white"
                    value={relatedPartySourceKey}
                    onChange={(e) => setRelatedPartySourceKey(e.target.value)}
                    disabled={relatedPartiesSaving || saving || !allowEdit || !canUseContactRelatedParties}>
                    <option value="">Выберите лицо или контакт</option>
                    {contactOptions.map((item) => (
                      <option key={getContactOptionKey(item)} value={getContactOptionKey(item)}>
                        {item.fullName}{item.organizationName ? ` · ${item.organizationName}` : ''}
                      </option>
                    ))}
                  </SystemSelect>
                )}

                <input
                  aria-label="Роль связанного участника"
                  className="rounded-lg border px-3 py-2 text-sm"
                  value={relatedPartyRole}
                  onChange={(e) => setRelatedPartyRole(e.target.value)}
                  placeholder="Роль"
                  disabled={relatedPartiesSaving || saving || !allowEdit || (!canUseContactRelatedParties && !canUseLegalEntityRelatedParties)}
                />
                <button
                  type="button"
                  className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 bg-white px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
                  onClick={() => void handleAddRelatedParty()}
                  disabled={relatedPartiesSaving || saving || !allowEdit || (!canUseContactRelatedParties && !canUseLegalEntityRelatedParties)}>
                  <Plus size={15} /> Добавить
                </button>
              </div>
            ) : (
              <div className="mt-3 rounded-lg border border-dashed border-slate-200 bg-white px-3 py-4 text-sm text-slate-500">
                Связи можно будет добавить после сохранения дела.
              </div>
            )}

            {relatedPartiesLoading ? (
              <div className="mt-3 rounded-lg border border-dashed border-slate-200 bg-white px-3 py-4 text-sm text-slate-500">
                Загружаем связи...
              </div>
            ) : relatedParties.length === 0 ? (
              <div className="mt-3 rounded-lg border border-dashed border-slate-200 bg-white px-3 py-4 text-sm text-slate-500">
                Связанные лица и организации пока не добавлены.
              </div>
            ) : (
              <div className="mt-3 grid gap-2">
                {relatedParties.map((party) => (
                  <div key={party.id} className="flex flex-col gap-3 rounded-lg border border-slate-200 bg-white px-3 py-3 md:flex-row md:items-start md:justify-between">
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2 text-sm font-semibold text-slate-900">
                        <span>{party.displayName}</span>
                        <span className="rounded-full bg-blue-50 px-2 py-0.5 text-xs font-semibold text-blue-700">{getRelatedPartySourceLabel(party.sourceType)}</span>
                        {party.role ? <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-600">{party.role}</span> : null}
                      </div>
                      <div className="mt-1 flex flex-wrap gap-2 text-xs text-slate-500">
                        {party.organizationName ? <span>{party.organizationName}</span> : null}
                        {party.phone ? <span>{party.phone}</span> : null}
                        {party.email ? <span>{party.email}</span> : null}
                      </div>
                    </div>
                    <button
                      type="button"
                      className="inline-flex items-center justify-center gap-1 rounded-lg border border-red-200 px-3 py-2 text-sm text-red-600 hover:bg-red-50 disabled:opacity-60"
                      onClick={() => void handleDeleteRelatedParty(party)}
                      disabled={relatedPartiesSaving || saving || !allowEdit}>
                      <Trash2 size={15} /> Удалить
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>

          <div className="md:col-span-2 mt-2 border-t pt-4">
            <h4 className="text-sm font-semibold text-gray-700 tracking-wide uppercase">Судебные данные</h4>
          </div>

          <div>
            <label className="text-sm text-gray-600">Суд</label>
            <input
              aria-label="Суд"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtName}
              onChange={(e) => setCourtName(e.target.value)}
              placeholder="Наименование суда"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Номер в суде</label>
            <input
              aria-label="Номер в суде"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtCaseNumber}
              onChange={(e) => setCourtCaseNumber(e.target.value)}
              placeholder="Например: А40-12345/2026"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Судья</label>
            <input
              aria-label="Судья"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={judgeName}
              onChange={(e) => setJudgeName(e.target.value)}
              placeholder="ФИО судьи"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Зал / кабинет</label>
            <input
              aria-label="Зал / кабинет"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtroom}
              onChange={(e) => setCourtroom(e.target.value)}
              placeholder="Например: зал 12"
              disabled={saving}
            />
          </div>

          <div className="md:col-span-2">
            <label className="text-sm text-gray-600">Контакты секретаря суда</label>
            <input
              aria-label="Контакты секретаря суда"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtSecretaryContacts}
              onChange={(e) => setCourtSecretaryContacts(e.target.value)}
              placeholder="Телефон, email, примечание"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Следующее заседание</label>
            <SystemDateInput
              aria-label="Следующее заседание"
              type="datetime-local"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={nextHearingAt}
              onChange={(e) => setNextHearingAt(e.target.value)}
              disabled={saving}
            />
            <p className="mt-1 text-xs text-gray-600">
              {!nextHearingAt
                ? 'Дата пока не назначена — ожидайте сведения суда или укажите её вручную.'
                : nextHearingAt !== toDateTimeInput(caseData.nextHearingAt)
                  ? 'Дата изменена: сначала сохраните дело, затем подтвердите её.'
                  : hearingConfirmation.nextHearingConfirmedAt && hearingConfirmation.nextHearingConfirmedForAt && Date.parse(hearingConfirmation.nextHearingConfirmedForAt) === Date.parse(caseData.nextHearingAt ?? '')
                    ? `Дата подтверждена ${formatDocumentDate(hearingConfirmation.nextHearingConfirmedAt)}${hearingConfirmation.nextHearingConfirmedByName ? ` · ${hearingConfirmation.nextHearingConfirmedByName}` : ''}. При переносе отметка сбросится.`
                    : 'Дата ещё не подтверждена сотрудником.'}
            </p>
            {caseData.id > 0 && allowEdit && caseData.status !== 'Closed' && caseData.status !== 'Lost' && caseData.nextHearingAt && Date.parse(caseData.nextHearingAt) > Date.now() && (!hearingConfirmation.nextHearingConfirmedAt || !hearingConfirmation.nextHearingConfirmedForAt || Date.parse(hearingConfirmation.nextHearingConfirmedForAt) !== Date.parse(caseData.nextHearingAt)) ? (
              <button type="button" onClick={() => void handleConfirmNextHearing()} disabled={saving || confirmingNextHearing || nextHearingAt !== toDateTimeInput(caseData.nextHearingAt)} className="mt-1 rounded-lg border border-blue-200 px-2.5 py-1.5 text-xs font-semibold text-blue-700 disabled:cursor-not-allowed disabled:opacity-50">
                {confirmingNextHearing ? 'Сохраняем…' : 'Подтвердить дату заседания'}
              </button>
            ) : null}
          </div>

          <div className="md:col-span-2">
            <CasePreHearingActions
              caseItem={caseData}
              timeZoneId={timeZoneId}
              canCreateTask={canCreateTasks}
              datesSaved={nextHearingAt === toDateTimeInput(caseData.nextHearingAt) && responsibleUserId === String(caseData.responsibleUserId ?? '')}
              onTaskCreated={() => void refreshCaseTasks()}
              onOpenTask={onOpenTask}
            />
          </div>

          <div className="md:col-span-2">
            <CaseHearingPreparationChecklist
              caseItem={caseData}
              timeZoneId={timeZoneId}
              canEdit={allowEdit}
              datesSaved={nextHearingAt === toDateTimeInput(caseData.nextHearingAt)}
              initialDocuments={caseDocuments}
            />
          </div>

          <div className="md:col-span-2">
            <div className="flex items-center justify-between gap-2">
              <label className="text-sm text-gray-600">Ссылка на карточку суда</label>
              {normalizedCourtUrl ? (
                <a
                  href={normalizedCourtUrl}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center gap-1 rounded-lg border border-blue-200 px-2.5 py-1 text-xs font-semibold text-blue-700 hover:bg-blue-50">
                  <ExternalLink size={14} /> Открыть
                </a>
              ) : null}
            </div>
            <input
              aria-label="Ссылка на карточку суда"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtUrl}
              onChange={(e) => setCourtUrl(e.target.value)}
              placeholder="https://"
              disabled={saving}
            />
          </div>

          <div className="md:col-span-2 mt-2 border-t pt-4 flex flex-wrap items-center justify-between gap-2">
            <h4 className="text-sm font-semibold text-gray-700 tracking-wide uppercase">Процессуальные сроки</h4>
            <button
              type="button"
              className="inline-flex items-center gap-1 rounded-lg border border-blue-200 px-3 py-1.5 text-xs font-semibold text-blue-700 hover:bg-blue-50 disabled:opacity-50"
              onClick={() => applyCalculatedProceduralDeadlines()}
              disabled={saving || !courtDecisionAt}>
              <RotateCw size={14} /> Рассчитать от решения
            </button>
          </div>

          <div>
            <label className="text-sm text-gray-600">Дата подачи заявления</label>
            <SystemDateInput
              aria-label="Дата подачи заявления"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={claimFiledAt}
              onChange={(e) => setClaimFiledAt(e.target.value)}
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Дата регистрации в суде</label>
            <SystemDateInput
              aria-label="Дата регистрации в суде"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtRegisteredAt}
              onChange={(e) => setCourtRegisteredAt(e.target.value)}
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Дата решения суда</label>
            <SystemDateInput
              aria-label="Дата решения суда"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtDecisionAt}
              onChange={(e) => handleCourtDecisionChange(e.target.value)}
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Решение вступило в силу</label>
            <SystemDateInput
              aria-label="Дата вступления решения в силу"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={courtDecisionEffectiveAt}
              onChange={(e) => setCourtDecisionEffectiveAt(e.target.value)}
              disabled={saving}
            />
            <p className="mt-1 text-xs text-gray-500">Укажите подтверждённую дату. CRM не рассчитывает её автоматически.</p>
          </div>

          <div>
            <label className="text-sm text-gray-600">Исполнительный лист получен</label>
            <SystemDateInput
              aria-label="Дата получения исполнительного листа"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={executiveWritReceivedAt}
              onChange={(e) => setExecutiveWritReceivedAt(e.target.value)}
              disabled={saving}
            />
            <div className="mt-2"><ExecutiveWritDocumentUpload caseId={caseData.id} canUpload={allowEdit} documents={caseDocuments} onUploaded={refreshDocuments} onAttemptSettled={() => setUploadRevision((value) => value + 1)} /></div>
          </div>

          <div className="rounded-lg border border-blue-100 bg-blue-50/40 p-3">
            <label className="text-sm text-gray-600">Исполнительный лист подан</label>
            <SystemDateInput aria-label="Дата подачи исполнительного листа" type="date" className="mt-1 w-full border rounded-lg px-3 py-2" value={executiveWritSubmittedAt} onChange={(event) => setExecutiveWritSubmittedAt(event.target.value)} disabled={saving || !allowEdit} />
            <label htmlFor="writ-submitted-to" className="mt-2 block text-sm text-gray-600">Куда подан</label>
            <select id="writ-submitted-to" aria-label="Куда подан исполнительный лист" className="mt-1 w-full border rounded-lg px-3 py-2" value={executiveWritSubmittedTo} onChange={(event) => setExecutiveWritSubmittedTo(event.target.value as 'fssp' | 'bank' | '')} disabled={saving || !allowEdit}>
              <option value="">Выберите адресата</option><option value="fssp">ФССП</option><option value="bank">Банк</option>
            </select>
            <p className="mt-1 text-xs text-gray-500">Отметка завершит задачу на подачу. Чтобы снять отметку, очистите дату и сохраните дело.</p>
          </div>

          <div>
            <label className="text-sm text-gray-600">Срок апелляции</label>
            <SystemDateInput
              aria-label="Срок апелляции"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={appealDeadlineAt}
              onChange={(e) => setAppealDeadlineAt(e.target.value)}
              disabled={saving}
            />
          </div>

          {caseData.id > 0 && caseData.courtDecisionAt && caseData.appealDeadlineAt ? (
            <div className="md:col-span-2">
              <CaseAppealDecisionAction
                clientCase={caseData}
                canEdit={allowEdit}
                datesSaved={courtDecisionAt === toDateInput(caseData.courtDecisionAt) && appealDeadlineAt === toDateInput(caseData.appealDeadlineAt)}
                onTaskCreated={() => void refreshCaseTasks()}
                onOpenTask={onOpenTask}
              />
            </div>
          ) : null}

          <div>
            <label className="text-sm text-gray-600">Срок кассации</label>
            <SystemDateInput
              aria-label="Срок кассации"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={cassationDeadlineAt}
              onChange={(e) => setCassationDeadlineAt(e.target.value)}
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Срок подачи ходатайств</label>
            <SystemDateInput
              aria-label="Срок подачи ходатайств"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={motionDeadlineAt}
              onChange={(e) => setMotionDeadlineAt(e.target.value)}
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Срок возражений</label>
            <SystemDateInput
              aria-label="Срок возражений"
              type="date"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={objectionDeadlineAt}
              onChange={(e) => setObjectionDeadlineAt(e.target.value)}
              disabled={saving}
            />
          </div>

          <div className="md:col-span-2 mt-2 border-t pt-4">
            <h4 className="text-sm font-semibold text-gray-700 tracking-wide uppercase">Документы, история и стратегия</h4>
          </div>

          <div className="md:col-span-2 rounded-xl border border-gray-200 bg-gray-50 p-4 space-y-3">
            {allowEdit && caseData.id > 0 ? <CaseDocumentUploadStatusPanel key={caseData.id} caseId={caseData.id} revision={uploadRevision}
              onPublished={async (signal) => {
                const documents = await apiService.getCaseDocuments(caseData.id, signal);
                if (!signal.aborted) setCaseDocuments(documents);
              }} /> : null}
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div>
                <div className="text-sm font-semibold text-gray-800">Файлы дела</div>
                <div className="text-xs text-gray-500">Тип, статус и история версий по каждому документу.</div>
              </div>
              {caseData.id > 0 && (
                <div className="grid gap-2 sm:grid-cols-[minmax(150px,1fr)_minmax(150px,1fr)_auto_minmax(170px,1fr)_auto]">
                  <SystemSelect
                    aria-label="Тип файла дела"
                    className="border rounded-lg px-3 py-2 bg-white text-sm"
                    value={documentType}
                    onChange={(e) => setDocumentType(e.target.value)}
                    disabled={documentsSaving || saving || !allowEdit}>
                    {DOCUMENT_TYPE_OPTIONS.map((item) => (
                      <option key={item} value={item}>{item}</option>
                    ))}
                  </SystemSelect>
                  <SystemSelect
                    aria-label="Статус файла дела"
                    className="border rounded-lg px-3 py-2 bg-white text-sm"
                    value={documentStatus}
                    onChange={(e) => setDocumentStatus(e.target.value)}
                    disabled={documentsSaving || saving || !allowEdit}>
                    {DOCUMENT_STATUS_OPTIONS.map((item) => (
                      <option key={item} value={item}>{item}</option>
                    ))}
                  </SystemSelect>
                  <label className={`inline-flex items-center justify-center gap-2 px-3 py-2 rounded-lg border border-blue-300 bg-white text-blue-700 text-sm hover:bg-blue-50 ${documentsSaving || saving || !allowEdit ? 'opacity-60 pointer-events-none' : 'cursor-pointer'}`}>
                    <Upload size={16} /> Загрузить
                    <input
                      aria-label="Загрузить файл дела"
                      type="file"
                      className="sr-only"
                      disabled={documentsSaving || saving || !allowEdit}
                      onChange={(e) => {
                        void handleUploadDocument(e.currentTarget.files);
                        e.currentTarget.value = '';
                      }}
                    />
                  </label>
                  <SystemSelect
                    aria-label="Шаблон документа дела"
                    className="border rounded-lg px-3 py-2 bg-white text-sm"
                    value={documentTemplateKey}
                    onChange={(e) => setDocumentTemplateKey(e.target.value)}
                    disabled={documentsSaving || saving || !allowEdit}>
                    {availableDocumentTemplates.map((item) => (
                      <option key={item.key} value={item.key}>{item.label}</option>
                    ))}
                  </SystemSelect>
                  <button
                    type="button"
                    className="inline-flex items-center justify-center gap-2 rounded-lg border border-emerald-300 bg-white px-3 py-2 text-sm font-semibold text-emerald-700 hover:bg-emerald-50 disabled:opacity-60"
                    onClick={() => void handleGenerateDocumentTemplate()}
                    disabled={documentsSaving || saving || !allowEdit || availableDocumentTemplates.length === 0}>
                    <FileText size={16} /> Сформировать
                  </button>
                </div>
              )}
            </div>

            {documentsError && (
              <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg px-3 py-2">
                {documentsError}
              </div>
            )}

            {caseData.id <= 0 ? (
              <div className="text-sm text-gray-500 bg-white border border-dashed rounded-lg px-3 py-4">
                Файлы можно будет добавить после сохранения дела.
              </div>
            ) : documentsLoading ? (
              <div className="text-sm text-gray-500 bg-white border border-dashed rounded-lg px-3 py-4">Загружаем файлы...</div>
            ) : caseDocuments.length === 0 ? (
              <div className="text-sm text-gray-500 bg-white border border-dashed rounded-lg px-3 py-4">Файлы по делу еще не добавлены.</div>
            ) : (
              <div className="space-y-2">
                {caseDocuments.map((document) => (
                  <div key={document.id} className="flex flex-col gap-3 rounded-lg border border-gray-200 bg-white px-3 py-3 md:flex-row md:items-center md:justify-between">
                    <div className="min-w-0 space-y-1">
                      <div className="flex flex-wrap items-center gap-2 text-sm font-medium text-gray-900">
                        <FileText size={16} className="text-blue-600" />
                        <span className="break-all">{document.originalFileName}</span>
                        <span className="rounded-full bg-gray-100 px-2 py-0.5 text-xs text-gray-600">v{document.version}</span>
                        {document.isLatestVersion && (
                          <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700">Актуальная</span>
                        )}
                      </div>
                      <div className="text-xs text-gray-500">
                        {document.documentType} · {formatBytes(document.sizeBytes)}
                        {document.uploadedAt ? ` · ${formatDocumentDate(document.uploadedAt)}` : ''}
                        {document.uploadedByName ? ` · ${document.uploadedByName}` : ''}
                      </div>
                    </div>
                    <div className="flex flex-wrap items-center gap-2">
                      <SystemSelect
                        aria-label={`Тип документа ${document.originalFileName}`}
                        className="rounded-lg border px-3 py-2 text-sm bg-white disabled:opacity-60"
                        value={document.documentType}
                        onChange={(e) => void handleUpdateDocument(document, { documentType: e.target.value })}
                        disabled={documentsSaving || saving || !allowEdit}>
                        {Array.from(new Set([document.documentType, ...DOCUMENT_TYPE_OPTIONS]))
                          .filter(Boolean)
                          .map((item) => (
                            <option key={item} value={item}>{item}</option>
                          ))}
                      </SystemSelect>
                      <SystemSelect
                        aria-label={`Статус документа ${document.originalFileName}`}
                        className="rounded-lg border px-3 py-2 text-sm bg-white disabled:opacity-60"
                        value={document.status}
                        onChange={(e) => void handleUpdateDocument(document, { status: e.target.value })}
                        disabled={documentsSaving || saving || !allowEdit}>
                        {Array.from(new Set([document.status, ...DOCUMENT_STATUS_OPTIONS]))
                          .filter(Boolean)
                          .map((item) => (
                            <option key={item} value={item}>{item}</option>
                          ))}
                      </SystemSelect>
                      <button
                        type="button"
                        className="inline-flex items-center gap-1 rounded-lg border px-3 py-2 text-sm hover:bg-gray-50 disabled:opacity-60"
                        onClick={() => void handleDownloadDocument(document)}
                        disabled={documentsSaving || saving}
                        aria-label={`Скачать ${document.originalFileName}`}>
                        <Download size={15} /> Скачать
                      </button>
                      <label className={`inline-flex items-center gap-1 rounded-lg border px-3 py-2 text-sm hover:bg-gray-50 ${documentsSaving || saving || !allowEdit ? 'opacity-60 pointer-events-none' : 'cursor-pointer'}`}>
                        <RotateCw size={15} /> Новая версия
                        <input
                          aria-label={`Загрузить новую версию ${document.originalFileName}`}
                          type="file"
                          className="sr-only"
                          disabled={documentsSaving || saving || !allowEdit}
                          onChange={(e) => {
                            void handleUploadDocument(e.currentTarget.files, document.documentKey);
                            e.currentTarget.value = '';
                          }}
                        />
                      </label>
                      <button
                        type="button"
                        className="inline-flex items-center gap-1 rounded-lg border border-red-200 px-3 py-2 text-sm text-red-600 hover:bg-red-50 disabled:opacity-60"
                        onClick={() => void handleDeleteDocument(document)}
                        disabled={documentsSaving || saving || !allowEdit}
                        aria-label={`Удалить ${document.originalFileName}`}>
                        <Trash2 size={15} /> Удалить
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>

          <div className="md:col-span-2">
            <div className="rounded-xl border border-slate-200 bg-slate-50/60 p-4">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div>
                  <h4 className="text-sm font-semibold text-slate-800">Задачи по делу</h4>
                  <p className="mt-0.5 text-xs text-slate-500">Приоритеты, сроки и текущие статусы связанных задач.</p>
                </div>
                <span className="rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs font-semibold text-slate-600">
                  {caseTasks.length}
                </span>
              </div>

              {caseTasksError ? (
                <div className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
                  {caseTasksError}
                </div>
              ) : caseTasksLoading ? (
                <div className="mt-3 rounded-lg border border-dashed border-slate-200 bg-white px-3 py-4 text-sm text-slate-500">
                  Загружаем задачи...
                </div>
              ) : caseTasks.length === 0 ? (
                <div className="mt-3 rounded-lg border border-dashed border-slate-200 bg-white px-3 py-4 text-sm text-slate-500">
                  По делу пока нет связанных задач.
                </div>
              ) : (
                <div className="mt-3 max-h-64 space-y-2 overflow-y-auto pr-1">
                  {caseTasks.map((task) => (
                    <article key={task.id} className="rounded-lg border border-slate-200 bg-white px-3 py-2">
                      <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="truncate text-sm font-semibold text-slate-900">{task.title}</span>
                            <span className={`rounded-full border px-2 py-0.5 text-xs font-semibold ${caseTaskStatusClassName(task.status)}`}>
                              {CASE_TASK_STATUS_LABELS[task.status] ?? task.status}
                            </span>
                          </div>
                          <div className="mt-1 flex flex-wrap gap-2 text-xs text-slate-500">
                            <span>Срок: {formatTaskDateTime(task.dueAt) || '—'}</span>
                            <span>Исполнитель: {task.assigneeName || '—'}</span>
                          </div>
                        </div>
                        <span className="shrink-0 rounded-full bg-amber-50 px-2.5 py-1 text-xs font-semibold text-amber-700 ring-1 ring-amber-200">
                          {CASE_TASK_PRIORITY_LABELS[task.priority] ?? task.priority}
                        </span>
                      </div>
                    </article>
                  ))}
                </div>
              )}
            </div>
          </div>

          <div className="md:col-span-2">
            <label className="text-sm text-gray-600">Документы по делу</label>
            <textarea
              aria-label="Документы по делу"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              rows={3}
              value={documentsSummary}
              onChange={(e) => setDocumentsSummary(e.target.value)}
              placeholder="Что собрано, чего не хватает, где хранится"
              disabled={saving}
            />
          </div>

          <div className="md:col-span-2">
            <label className="text-sm text-gray-600">История событий</label>
            <textarea
              aria-label="История событий"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              rows={4}
              value={eventHistory}
              onChange={(e) => setEventHistory(e.target.value)}
              placeholder="Ключевые события и процессуальные действия"
              disabled={saving}
            />
          </div>

          <div className="md:col-span-2">
            <label className="text-sm text-gray-600">Стратегия и план действий</label>
            <textarea
              aria-label="Стратегия и план действий"
              className="mt-1 w-full border rounded-lg px-3 py-2"
              rows={4}
              value={strategy}
              onChange={(e) => setStrategy(e.target.value)}
              placeholder="Позиция, риски, ближайшие шаги"
              disabled={saving || !allowEdit}
            />
            <CasePositionTemplatePicker text={strategy} onChange={setStrategy} disabled={!allowEdit || saving} />
            <p className="mt-1 text-xs text-gray-600">
              {strategy.trim() !== (caseData.strategy ?? '').trim()
                ? 'Позиция изменена: сохраните дело, прежняя отметка проверки будет сброшена.'
                : positionReview.positionReviewedAt
                  ? `Позиция проверена ${formatDocumentDate(positionReview.positionReviewedAt)}${positionReview.positionReviewedByName ? ` · ${positionReview.positionReviewedByName}` : ''}`
                  : 'Позиция ещё не подтверждена.'}
            </p>
            {caseData.id > 0 && allowEdit && caseData.status !== 'Closed' && caseData.status !== 'Lost' && !positionReview.positionReviewedAt ? (
              <button type="button" onClick={() => void handleReviewPosition()} disabled={saving || reviewingPosition || !strategy.trim() || strategy.trim() !== (caseData.strategy ?? '').trim()} className="mt-2 rounded-lg border border-blue-200 px-2.5 py-1.5 text-xs font-semibold text-blue-700 disabled:cursor-not-allowed disabled:opacity-50">
                {reviewingPosition ? 'Сохраняем…' : 'Подтвердить проверку позиции'}
              </button>
            ) : null}
          </div>
          </div>
        </div>

        <div className="px-5 py-4 border-t flex items-center justify-between">
          {onDelete ? (
            <button
              type="button"
              onClick={handleDelete}
              className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-red-300 text-red-600 hover:bg-red-50 disabled:opacity-60"
              disabled={saving || !allowEdit}>
              <Trash2 size={16} /> Удалить
            </button>
          ) : (
            <span />
          )}

          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 rounded-lg border hover:bg-gray-50 disabled:opacity-60"
              disabled={saving}>
              Отмена
            </button>
            <button
              type="button"
              onClick={handleSave}
              className="px-4 py-2 rounded-lg bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-60"
              disabled={saving || !allowEdit}
              title="Ctrl/Cmd + Enter">
              Сохранить
            </button>
          </div>
        </div>
      </div>
    </div>
  );

  return createPortal(modal, document.body);
}
