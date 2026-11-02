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
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { createPortal } from 'react-dom';
import { Download, ExternalLink, FileText, Link2, Plus, RotateCw, Trash2, Upload, X } from 'lucide-react';
import type { ActResponsible, ClientCase, ClientCaseActivity, ClientCaseDocument, ClientCaseDocumentTemplate, ClientCaseFinanceSummary, ClientCaseRelatedParty, LegalEntitySummary } from '../../types';
import { useAuth } from '../../contexts/AuthContext';
import { captureAuthSessionIdentity } from '../../services/authService';
import { useDictionarySnapshot } from '../../hooks/useDictionarySnapshot';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { useDialog } from '../../hooks/useDialog';
import { useModalFocusTrap } from '../../hooks/useModalFocusTrap';
import { useDocumentDeliveryOwner, type DocumentDeliveryRead } from '../../hooks/useDocumentDeliveryOwner';
import { awaitOwnedUiResult, StaleUiOperationError } from '../../utils/StaleUiOperationError';
import { apiService, type ContactDirectoryItemDto } from '../../services/api';
import { formatCurrencySmart } from '../../utils/formatters';
import { downloadBlob } from '../../utils/downloadBlob';
import { courtAmountToInput, hasInvalidCourtAmounts, parseCourtAmountInput } from '../../utils/courtAmounts';
import { formatDateTimeInTimeZone, fromTimeZoneDateTimeInput, parseInstant, toTimeZoneDateTimeInput } from '../../utils/timeZoneDate';
import { normalizeUserTimeZoneId, USER_TIME_ZONE_OPTIONS, USER_TIME_ZONE_SETTING_KEY } from '../../utils/timeZoneOptions';
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
  responsiblesLoading?: boolean;
  responsiblesError?: string | null;
  onRetryResponsibles?: () => void;
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

const EMPTY_CASE_DOCUMENTS: ClientCaseDocument[] = [];
const EMPTY_CASE_TASKS: TaskJournalItem[] = [];
const EMPTY_CASE_PARTIES: ClientCaseRelatedParty[] = [];
const EMPTY_CASE_CONTACTS: ContactDirectoryItemDto[] = [];
const EMPTY_CASE_ENTITIES: LegalEntitySummary[] = [];

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

function formatTaskDateTime(value: string | null | undefined, timeZoneId: string) {
  if (!value) return '';
  return formatDateTimeInTimeZone(value, timeZoneId);
}

function toDateInput(value?: string | null) {
  return value ? String(value).slice(0, 10) : '';
}

function toDateTimeInput(value: string | null | undefined, timeZoneId: string) {
  return toTimeZoneDateTimeInput(value, timeZoneId);
}

function fromDateInput(value: string) {
  return value || null;
}

function fromDateTimeInput(value: string, timeZoneId: string) {
  return value ? fromTimeZoneDateTimeInput(value, timeZoneId) : null;
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

function formatDocumentDate(value: string | null | undefined, timeZoneId: string) {
  if (!value) return '';
  return formatDateTimeInTimeZone(value, timeZoneId);
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
  responsiblesLoading = false,
  responsiblesError,
  onRetryResponsibles,
  overlayClassName,
  allowEdit = true,
  canViewFinance = true,
  canUseContactRelatedParties = true,
  canUseLegalEntityRelatedParties = true,
  onOpenTask,
  canCreateTasks = false,
}: CaseModalProps) {
  const { user } = useAuth();
  const { confirm, prompt } = useDialog();
  const caseActionOwner = useDocumentDeliveryOwner(JSON.stringify([isOpen, caseData.id, caseData.clientId, caseData.legalEntityId, allowEdit, canViewFinance, canUseContactRelatedParties, canUseLegalEntityRelatedParties, canCreateTasks, user?.id, user?.role?.id]));
  const { capture: captureCaseOwner } = caseActionOwner;
  const [readClosed, setReadClosed] = useState(false);
  const modalRef = useRef<HTMLDivElement>(null);
  const titleInputRef = useRef<HTMLInputElement>(null);
  const deleteConfirmation = useRef<AbortController | null>(null);
  const activityPrompt = useRef<AbortController | null>(null);
  const fileDownload = useRef<AbortController | null>(null);
  const currentCourtState = useRef({ strategy: caseData.strategy, hearing: caseData.nextHearingAt, status: caseData.status });
  currentCourtState.current = { strategy: caseData.strategy, hearing: caseData.nextHearingAt, status: caseData.status };
  const handleClose = () => {
    if (!isOpen || !caseActionOwner.capture()) return;
    caseActionOwner.invalidate(); setReadClosed(true); deleteConfirmation.current?.abort(); activityPrompt.current?.abort(); fileDownload.current?.abort(); onClose();
  };
  const trapFocus = useModalFocusTrap(modalRef, isOpen, handleClose, titleInputRef);
  useEffect(() => {
    return () => { deleteConfirmation.current?.abort(); activityPrompt.current?.abort(); fileDownload.current?.abort(); };
  }, [caseActionOwner.scope]);
  const fallbackTimeZone = useMemo(() => normalizeUserTimeZoneId(), []);
  const [timeZoneState, setTimeZoneState] = useState({ scope: caseActionOwner.scope, value: fallbackTimeZone });
  const timeZoneId = timeZoneState.scope === caseActionOwner.scope ? timeZoneState.value : fallbackTimeZone;
  const hearingEdited = useRef(false);
  const historyEdited = useRef(false);
  const activityEdited = useRef(false);
  const activityReadSequence = useRef(0);
  const documentReadSequence = useRef(0);
  const currentTimeZone = useRef(timeZoneId); currentTimeZone.current = timeZoneId;
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
  const [nextHearingAt, setNextHearingAt] = useState(toDateTimeInput(caseData.nextHearingAt, timeZoneId));
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
  const reviewingPosition = caseActionOwner.kind === 'review-position';
  const [hearingConfirmation, setHearingConfirmation] = useState({
    nextHearingConfirmedForAt: caseData.nextHearingConfirmedForAt ?? null,
    nextHearingConfirmedAt: caseData.nextHearingConfirmedAt ?? null,
    nextHearingConfirmedByUserId: caseData.nextHearingConfirmedByUserId ?? null,
    nextHearingConfirmedByName: caseData.nextHearingConfirmedByName ?? '',
  });
  const confirmingNextHearing = caseActionOwner.kind === 'confirm-hearing';
  const documentsSaving = caseActionOwner.kind?.startsWith('document:') ?? false;
  const [uploadRevision, setUploadRevision] = useState(0);
  const [documentActionError, setDocumentActionError] = useState<{ owner: DocumentDeliveryRead; message: string } | null>(null);
  const setDocumentsError = useCallback((message: string | null) => {
    const owner = captureCaseOwner(); if (owner) setDocumentActionError(message ? { owner, message } : null);
  }, [captureCaseOwner]);
  const [documentType, setDocumentType] = useState(DOCUMENT_TYPE_OPTIONS[0]);
  const [documentStatus, setDocumentStatus] = useState(DOCUMENT_STATUS_OPTIONS[0]);
  const [documentTemplateKey, setDocumentTemplateKey] = useState(FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS[0].key);
  const saving = caseActionOwner.busy;
  const [errorState, setErrorState] = useState<{ owner: DocumentDeliveryRead; message: string } | null>(null);
  const error = errorState?.owner.isCurrent() ? errorState.message : null;
  const setError = (message: string | null) => {
    const owner = caseActionOwner.capture(); if (!owner) return;
    setErrorState(message ? { owner, message } : null);
  };
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
  const activitySaving = caseActionOwner.kind === 'confirm-activity';
  const relatedPartiesSaving = caseActionOwner.kind?.startsWith('party:') ?? false;
  const [partyActionError, setPartyActionError] = useState<{ owner: DocumentDeliveryRead; message: string } | null>(null);
  const setRelatedPartiesError = useCallback((message: string | null) => {
    const owner = captureCaseOwner(); if (owner) setPartyActionError(message ? { owner, message } : null);
  }, [captureCaseOwner]);
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
    setReadClosed(false); hearingEdited.current = false; historyEdited.current = false; activityEdited.current = false;
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
    setNextHearingAt(toDateTimeInput(caseData.nextHearingAt, currentTimeZone.current));
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
    setRelatedPartiesError(null);
    setRelatedPartyMode('contact');
    setRelatedPartySourceKey('');
    setRelatedPartyLegalEntityId('');
    setRelatedPartyRole('Участник');
    setErrorState(null);
  }, [caseData, isOpen, caseActionOwner.scope, setDocumentsError, setRelatedPartiesError]);

  const readEnabled = isOpen && !readClosed && caseData.id > 0 && captureAuthSessionIdentity().authenticated;
  const readOwned = useCallback(<T,>(signal: AbortSignal, operation: () => Promise<T>) => {
    const owner = captureCaseOwner();
    if (!owner) return Promise.reject<T>(new StaleUiOperationError());
    return awaitOwnedUiResult(operation, () => owner.isCurrent() && !signal.aborted);
  }, [captureCaseOwner]);
  const validateDocumentRows = useCallback((rows: ClientCaseDocument[]) => {
    if (!Array.isArray(rows) || !rows.every(row => row && Number.isSafeInteger(row.id) && row.id > 0 && row.clientCaseId === caseData.id)
      || new Set(rows.map(row => row.id)).size !== rows.length) throw new Error('Не удалось подтвердить список файлов дела.');
    return rows;
  }, [caseData.id]);
  const loadDocuments = useCallback((signal: AbortSignal) => {
    const sequence = ++documentReadSequence.current;
    return readOwned(signal, async () => {
      const rows = await apiService.getCaseDocuments(caseData.id, signal);
      if (sequence !== documentReadSequence.current) throw new StaleUiOperationError();
      return validateDocumentRows(rows);
    });
  }, [caseData.id, readOwned, validateDocumentRows]);
  const documentRead = useDictionarySnapshot(loadDocuments, EMPTY_CASE_DOCUMENTS, { enabled: readEnabled, ownerScope: caseActionOwner.scope }, false);
  const caseDocuments = documentRead.value;
  const documentsLoading = documentRead.loading;
  const documentFailure = documentActionError?.owner.isCurrent() ? documentActionError.message : documentRead.error;
  const documentsError = documentFailure ? extractDisplayErrorMessage(documentFailure, 'Не удалось загрузить файлы дела.') : null;
  const refreshDocuments = documentRead.refresh;
  const updateDocuments = documentRead.updateValue;
  const setCaseDocuments = useCallback((rows: ClientCaseDocument[]) => {
    if (captureCaseOwner()) updateDocuments(() => rows);
  }, [captureCaseOwner, updateDocuments]);

  const publishDocumentRead = useCallback(async (signal: AbortSignal) => {
    const owner = captureCaseOwner(); if (!owner) throw new StaleUiOperationError();
    const sequence = ++documentReadSequence.current;
    const rows = await awaitOwnedUiResult(() => apiService.getCaseDocuments(caseData.id, signal),
      () => owner.isCurrent() && !signal.aborted && documentReadSequence.current === sequence);
    setCaseDocuments(validateDocumentRows(rows));
  }, [captureCaseOwner, caseData.id, setCaseDocuments, validateDocumentRows]);

  const loadTemplates = useCallback((signal: AbortSignal) => readOwned(signal, async () => {
    const rows = await apiService.getCaseDocumentTemplates({ signal });
    if (!Array.isArray(rows)) throw new Error('Не удалось загрузить шаблоны документов дела.');
    const templates = rows.filter(item => item && typeof item.key === 'string' && typeof item.label === 'string' && typeof item.documentType === 'string'
      && item.key && item.label && item.documentType);
    return templates.length ? templates : FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS;
  }), [readOwned]);
  const templateRead = useDictionarySnapshot(loadTemplates, FALLBACK_CASE_DOCUMENT_TEMPLATE_OPTIONS, { enabled: isOpen && !readClosed && captureAuthSessionIdentity().authenticated, ownerScope: caseActionOwner.scope }, false);
  const documentTemplates = templateRead.value;
  const availableDocumentTemplates = useMemo(
    () => filterCaseDocumentTemplatesByCaseType(documentTemplates, caseType),
    [documentTemplates, caseType],
  );

  useEffect(() => {
    setDocumentTemplateKey(current => current && availableDocumentTemplates.some(item => item.key === current) ? current : availableDocumentTemplates[0]?.key ?? '');
  }, [availableDocumentTemplates]);
  const loadTasks = useCallback((signal: AbortSignal) => readOwned(signal, async () => {
    const result = await apiService.getTaskJournalItems({ clientCaseId: caseData.id, page: 1, pageSize: 25 }, { signal });
    if (!result || !Array.isArray(result.items) || !result.items.every(row => row && Number.isSafeInteger(row.id) && row.id > 0)) throw new Error('Не удалось подтвердить список задач дела.');
    return result.items;
  }), [caseData.id, readOwned]);
  const taskRead = useDictionarySnapshot(loadTasks, EMPTY_CASE_TASKS, { enabled: readEnabled, ownerScope: caseActionOwner.scope }, false);
  const caseTasks = taskRead.value; const caseTasksLoading = taskRead.loading; const caseTasksError = taskRead.error ? extractDisplayErrorMessage(taskRead.error, 'Не удалось загрузить задачи дела.') : null;
  const refreshCaseTasks = taskRead.refresh;

  const loadFinance = useCallback((signal: AbortSignal) => readOwned(signal, async () => {
    const rows = caseData.legalEntityId ? await apiService.getLegalEntityCaseFinanceSummaries(caseData.legalEntityId, { signal })
      : caseData.clientId ? await apiService.getClientCaseFinanceSummaries(caseData.clientId, undefined, { signal }) : [];
    if (!Array.isArray(rows) || !rows.every(row => row && Number.isSafeInteger(row.caseId) && row.caseId > 0)) throw new Error('Не удалось подтвердить финансовые данные дела.');
    return rows.find(row => row.caseId === caseData.id) ?? null;
  }), [caseData.clientId, caseData.id, caseData.legalEntityId, readOwned]);
  const financeRead = useDictionarySnapshot<ClientCaseFinanceSummary | null>(loadFinance, null, { enabled: readEnabled && canViewFinance, ownerScope: caseActionOwner.scope }, false);
  const financeSummary = financeRead.value; const financeLoading = financeRead.loading; const financeError = financeRead.error ? extractDisplayErrorMessage(financeRead.error, 'Не удалось загрузить финансовые данные дела.') : null;

  const applyActivity = useCallback((next: ClientCaseActivity) => {
    if (!captureCaseOwner()) return;
    if (!next || typeof next !== 'object' || Array.isArray(next)) throw new Error('Не удалось подтвердить сведения об активности.');
    setActivity(previous => activityEdited.current ? { ...next,
      inactivityRemindersEnabled: previous.inactivityRemindersEnabled,
      inactivityRemindersDisabledReason: previous.inactivityRemindersDisabledReason } : next);
    if (!historyEdited.current && typeof next.eventHistory === 'string') setEventHistory(next.eventHistory);
  }, [captureCaseOwner]);

  useEffect(() => {
    if (!readEnabled) return;
    const owner = captureCaseOwner(); if (!owner) return;
    const tracker = activityReadSequence; const sequence = ++tracker.current;
    void awaitOwnedUiResult(() => apiService.markCaseActivity(caseData.id, { confirmed: false }), () => owner.isCurrent() && tracker.current === sequence)
      .then(applyActivity)
      .catch(() => undefined);
    return () => { tracker.current++; };
  }, [applyActivity, caseData.id, captureCaseOwner, readEnabled]);

  const loadParties = useCallback((signal: AbortSignal) => readOwned(signal, async () => {
    const rows = await apiService.getCaseRelatedParties(caseData.id, false, { signal });
    if (!Array.isArray(rows) || !rows.every(row => row && row.clientCaseId === caseData.id && Number.isSafeInteger(row.id) && row.id > 0)) throw new Error('Не удалось подтвердить связи дела.');
    return rows;
  }), [caseData.id, readOwned]);
  const partyRead = useDictionarySnapshot(loadParties, EMPTY_CASE_PARTIES, { enabled: readEnabled, ownerScope: caseActionOwner.scope }, false);
  const relatedParties = partyRead.value; const relatedPartiesLoading = partyRead.loading;
  const partyFailure = partyActionError?.owner.isCurrent() ? partyActionError.message : partyRead.error;
  const relatedPartiesError = partyFailure ? extractDisplayErrorMessage(partyFailure, 'Не удалось загрузить связи дела.') : null;
  const updateParties = partyRead.updateValue;
  const setRelatedParties = useCallback((update: (previous: ClientCaseRelatedParty[]) => ClientCaseRelatedParty[]) => {
    if (captureCaseOwner()) updateParties(update);
  }, [captureCaseOwner, updateParties]);

  const loadContacts = useCallback((signal: AbortSignal) => readOwned(signal, async () => {
    const rows = await apiService.getContacts({ scope: 'all' }, { signal });
    if (!Array.isArray(rows) || !rows.every(row => row && Number.isSafeInteger(row.sourceId) && row.sourceId > 0 && typeof row.fullName === 'string')) throw new Error('Не удалось загрузить участников дела.');
    return rows.filter(item => item.isActive).slice(0, 200);
  }), [readOwned]);
  const contactRead = useDictionarySnapshot(loadContacts, EMPTY_CASE_CONTACTS, { enabled: readEnabled && canUseContactRelatedParties, ownerScope: caseActionOwner.scope }, false);
  const contactOptions = contactRead.value;
  const loadEntities = useCallback((signal: AbortSignal) => readOwned(signal, async () => {
    const result = await apiService.getLegalEntitiesPaged({ page: 1, pageSize: 100 }, { signal });
    if (!result || !Array.isArray(result.items) || !result.items.every(row => row && Number.isSafeInteger(row.id) && row.id > 0 && typeof row.shortName === 'string')) throw new Error('Не удалось загрузить организации дела.');
    return result.items;
  }), [readOwned]);
  const entityRead = useDictionarySnapshot(loadEntities, EMPTY_CASE_ENTITIES, { enabled: readEnabled && canUseLegalEntityRelatedParties, ownerScope: caseActionOwner.scope }, false);
  const legalEntityOptions = entityRead.value;
  const actionSnapshots = useRef({ documents: documentRead, parties: partyRead, contacts: contactRead, entities: entityRead });
  actionSnapshots.current = { documents: documentRead, parties: partyRead, contacts: contactRead, entities: entityRead };
  const relatedDirectory = relatedPartyMode === 'legalEntity' ? entityRead : contactRead;
  const relatedDirectoryError = relatedDirectory.error ? extractDisplayErrorMessage(relatedDirectory.error, 'Не удалось загрузить справочник участников.') : null;


  const loadTimeZone = useCallback((signal: AbortSignal) => readOwned(signal, async () => {
    const result = typeof apiService.getUserSetting === 'function' ? await apiService.getUserSetting(USER_TIME_ZONE_SETTING_KEY, { signal }) : null;
    if (result !== null && (!result || result.key !== USER_TIME_ZONE_SETTING_KEY || typeof result.value !== 'string')) throw new Error('Не удалось подтвердить часовой пояс.');
    return normalizeUserTimeZoneId(result?.value);
  }), [readOwned]);
  const timeZoneRead = useDictionarySnapshot<string | null>(loadTimeZone, null, { enabled: isOpen && !readClosed && captureAuthSessionIdentity().authenticated, ownerScope: caseActionOwner.scope }, false);
  useEffect(() => {
    if (!timeZoneRead.value || hearingEdited.current) return;
    setTimeZoneState({ scope: caseActionOwner.scope, value: timeZoneRead.value });
    setNextHearingAt(toDateTimeInput(caseData.nextHearingAt, timeZoneRead.value));
  }, [caseActionOwner.scope, caseData.nextHearingAt, timeZoneRead.value]);

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
    if (!isOpen || !allowEdit || !caseActionOwner.capture() || caseData.id <= 0 || caseData.status === 'Closed' || caseData.status === 'Lost' || !strategy.trim() || positionReview.positionReviewedAt) return;
    if (strategy.trim() !== (caseData.strategy ?? '').trim()) {
      setError('Сначала сохраните изменённую позицию по делу.');
      return;
    }
    const operation = caseActionOwner.begin('review-position'); if (!operation) return;
    const isCurrent = () => operation.isCurrent() && currentCourtState.current.strategy === caseData.strategy && currentCourtState.current.status === caseData.status;
    setError(null);
    try {
      const review = await awaitOwnedUiResult(() => apiService.reviewCasePosition(caseData.id, caseData.strategy ?? ''), isCurrent);
      if (!review || typeof review.positionReviewedAt !== 'string' || !Number.isFinite(parseInstant(review.positionReviewedAt).getTime()) || typeof review.positionReviewedByName !== 'string') throw new Error('Не удалось подтвердить сведения о проверке позиции.');
      setPositionReview(review);
      onPositionReviewed?.(review);
    } catch (err) {
      if (isCurrent() && !(err instanceof StaleUiOperationError)) setError(extractDisplayErrorMessage(err, 'Не удалось подтвердить проверку позиции.'));
    } finally {
      operation.finish();
    }
  };

  const handleConfirmNextHearing = async () => {
    if (!isOpen || !allowEdit || !caseActionOwner.capture() || caseData.id <= 0 || !caseData.nextHearingAt || caseData.status === 'Closed' || caseData.status === 'Lost' || !(parseInstant(caseData.nextHearingAt).getTime() > Date.now())) return;
    if (nextHearingAt !== toDateTimeInput(caseData.nextHearingAt, timeZoneId)) {
      setError('Сначала сохраните изменённую дату следующего заседания.');
      return;
    }
    const operation = caseActionOwner.begin('confirm-hearing'); if (!operation) return;
    const isCurrent = () => operation.isCurrent() && currentCourtState.current.hearing === caseData.nextHearingAt && currentCourtState.current.status === caseData.status;
    setError(null);
    try {
      const confirmation = await awaitOwnedUiResult(() => apiService.confirmNextHearing(caseData.id, caseData.nextHearingAt!), isCurrent);
      if (!confirmation || typeof confirmation.nextHearingConfirmedAt !== 'string' || typeof confirmation.nextHearingConfirmedForAt !== 'string' || !Number.isFinite(parseInstant(confirmation.nextHearingConfirmedAt).getTime()) || typeof confirmation.nextHearingConfirmedByName !== 'string'
        || parseInstant(confirmation.nextHearingConfirmedForAt).getTime() !== parseInstant(caseData.nextHearingAt).getTime()) throw new Error('Дата заседания изменилась или не подтверждена. Обновите карточку дела.');
      setHearingConfirmation(confirmation);
      onNextHearingConfirmed?.(confirmation);
    } catch (err) {
      if (isCurrent() && !(err instanceof StaleUiOperationError)) setError(extractDisplayErrorMessage(err, 'Не удалось подтвердить дату следующего заседания.'));
    } finally {
      operation.finish();
    }
  };

  const handleSave = async () => {
    if (!isOpen || !caseActionOwner.capture()) return;
    if (!allowEdit) {
      setError('Недостаточно прав для редактирования дела.');
      return;
    }

    const v = validate();
    if (v) {
      setError(v);
      return;
    }
    const operation = caseActionOwner.begin('save'); if (!operation) return;
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
        nextHearingAt: fromDateTimeInput(nextHearingAt, timeZoneId),
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
      await awaitOwnedUiResult(() => Promise.resolve(onSave(patch)), operation.isCurrent);
      handleClose();
    } catch (e: unknown) {
      if (operation.isCurrent() && !(e instanceof StaleUiOperationError)) setError(e instanceof Error ? e.message : String(e));
    } finally {
      operation.finish();
    }
  };

  const handleDelete = async () => {
    if (!onDelete || !isOpen || !caseActionOwner.capture()) return;
    if (!allowEdit) { setError('Недостаточно прав для редактирования дела.'); return; }
    const operation = caseActionOwner.begin('delete'); if (!operation) return;
    const controller = new AbortController(); deleteConfirmation.current = controller;
    setError(null);
    try {
      const ok = await awaitOwnedUiResult(() => confirm({ signal: controller.signal, title: 'Подтверждение удаления', description: 'Удалить это дело? Действие необратимо.', confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }), operation.isCurrent);
      if (!ok) return;
      await awaitOwnedUiResult(() => Promise.resolve(onDelete()), operation.isCurrent);
      handleClose();
    } catch (e: unknown) {
      if (operation.isCurrent() && !(e instanceof StaleUiOperationError)) setError(e instanceof Error ? e.message : String(e));
    } finally {
      if (deleteConfirmation.current === controller) deleteConfirmation.current = null;
      operation.finish();
    }
  };

  const currentDocument = (row: ClientCaseDocument) => {
    const snapshot = actionSnapshots.current.documents;
    return !snapshot.loading && !snapshot.error && row.clientCaseId === caseData.id
      ? snapshot.value.find(item => item.id === row.id && item.clientCaseId === caseData.id && item.documentKey === row.documentKey && item.version === row.version) : undefined;
  };
  const requireCurrentDocument = (row: ClientCaseDocument) => {
    if (!currentDocument(row)) throw new Error('Список файлов изменился или недоступен. Обновите его и выберите файл заново.');
  };
  const runDocumentAction = async (kind: string, action: (isCurrent: () => boolean) => Promise<void>, uploadAttempt = false) => {
    const operation = caseActionOwner.begin(`document:${kind}`); if (!operation) return;
    setDocumentsError(null);
    try { await action(operation.isCurrent); }
    catch (error) {
      if (operation.isCurrent() && !(error instanceof StaleUiOperationError)) setDocumentsError(extractDisplayErrorMessage(error, 'Не удалось выполнить действие с файлом дела.'));
    } finally {
      if (uploadAttempt && operation.isCurrent()) setUploadRevision(value => value + 1);
      operation.finish();
    }
  };
  const canStartFileAction = (edit = true) => {
    if (!isOpen || !caseActionOwner.capture() || caseData.id <= 0) return false;
    if (edit && !allowEdit) { setDocumentsError('Недостаточно прав для изменения файлов дела.'); return false; }
    return true;
  };
  const handleUploadDocument = async (fileList: FileList | null, documentKey?: string) => {
    const file = fileList?.[0];
    if (!file || !canStartFileAction()) return;
    await runDocumentAction('upload', async isCurrent => {
      if (documentKey) {
        const snapshot = actionSnapshots.current.documents;
        if (snapshot.loading || snapshot.error || !snapshot.value.some(row => row.clientCaseId === caseData.id && row.documentKey === documentKey)) throw new Error('Список файлов изменился или недоступен. Обновите его и выберите файл заново.');
      }
      await awaitOwnedUiResult(() => apiService.uploadCaseDocument(caseData.id, file, { documentType, status: documentStatus, documentKey }), isCurrent);
      await awaitOwnedUiResult(refreshDocuments, isCurrent);
    }, true);
  };
  const handleGenerateDocumentTemplate = async () => {
    if (!canStartFileAction()) return;
    await runDocumentAction('generate', async isCurrent => {
      const template = availableDocumentTemplates.find(item => item.key === documentTemplateKey);
      if (!template) throw new Error('Выберите доступный шаблон документа для типа этого дела.');
      await awaitOwnedUiResult(() => apiService.generateCaseDocumentTemplate(caseData.id, { templateKey: template.key, documentType: template.documentType, status: documentStatus }), isCurrent);
      await awaitOwnedUiResult(refreshDocuments, isCurrent);
    }, true);
  };
  const handleDownloadDocument = async (document: ClientCaseDocument) => {
    if (!canStartFileAction(false)) return;
    await runDocumentAction('download', async isCurrent => {
      requireCurrentDocument(document);
      const controller = new AbortController(); fileDownload.current = controller;
      try {
        const result = await awaitOwnedUiResult(() => apiService.downloadCaseDocument(caseData.id, document.id, { signal: controller.signal }), isCurrent);
        requireCurrentDocument(document);
        downloadBlob(result.blob, result.fileName || document.originalFileName);
      } finally {
        if (fileDownload.current === controller) fileDownload.current = null;
      }
    });
  };
  const handleDeleteDocument = async (document: ClientCaseDocument) => {
    if (!canStartFileAction()) return;
    await runDocumentAction('delete', async isCurrent => {
      requireCurrentDocument(document);
      const controller = new AbortController(); deleteConfirmation.current = controller;
      try {
        const ok = await awaitOwnedUiResult(() => confirm({ signal: controller.signal, title: 'Удаление файла', description: `Удалить файл «${document.originalFileName}»?`, confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }), isCurrent);
        if (!ok) return;
        requireCurrentDocument(document);
        await awaitOwnedUiResult(() => apiService.deleteCaseDocument(caseData.id, document.id), isCurrent);
        await awaitOwnedUiResult(refreshDocuments, isCurrent);
      } finally {
        if (deleteConfirmation.current === controller) deleteConfirmation.current = null;
      }
    });
  };
  const handleUpdateDocument = async (document: ClientCaseDocument, patch: { documentType?: string; status?: string }) => {
    if (!canStartFileAction()) return;
    await runDocumentAction('update', async isCurrent => {
      requireCurrentDocument(document);
      await awaitOwnedUiResult(() => apiService.updateCaseDocument(caseData.id, document.id, patch), isCurrent);
      await awaitOwnedUiResult(refreshDocuments, isCurrent);
    });
  };

  const handleConfirmActivity = async () => {
    // Confirming a view is allowed with the existing view permission, including read-only cards.
    if (!isOpen || !caseActionOwner.capture() || caseData.id <= 0) return;
    const operation = caseActionOwner.begin('confirm-activity'); if (!operation) return;
    activityReadSequence.current++;
    setError(null);
    try {
      const nextActivity = await awaitOwnedUiResult(() => apiService.markCaseActivity(caseData.id, { confirmed: true }), operation.isCurrent);
      applyActivity(nextActivity);
    } catch (e: unknown) {
      if (operation.isCurrent() && !(e instanceof StaleUiOperationError)) setError(extractDisplayErrorMessage(e, 'Не удалось подтвердить активность по делу.'));
    } finally {
      operation.finish();
    }
  };

  const handleInactivityRemindersChange = async (enabled: boolean) => {
    if (!isOpen || !allowEdit || !caseActionOwner.capture()) return;
    const operation = caseActionOwner.begin('activity-reminders'); if (!operation) return;
    let controller: AbortController | null = null;
    try {
      if (enabled) {
        activityEdited.current = true;
        setActivity(current => ({ ...current, inactivityRemindersEnabled: true, inactivityRemindersDisabledReason: '' }));
        return;
      }
      controller = new AbortController(); activityPrompt.current = controller;
      const reason = await awaitOwnedUiResult(() => prompt({
        signal: controller!.signal,
        title: 'Выключить автоконтроль дела',
        label: 'Причина выключения',
        defaultValue: activity.inactivityRemindersDisabledReason ?? '',
        multiline: true,
        confirmLabel: 'Выключить',
        cancelLabel: 'Отмена',
      }), operation.isCurrent);
      if (reason === null) return;
      if (!reason.trim()) {
        setError('Укажите причину выключения автоконтроля.');
        return;
      }
      activityEdited.current = true;
      setError(null);
      setActivity(current => ({ ...current, inactivityRemindersEnabled: false, inactivityRemindersDisabledReason: reason.trim() }));
    } catch (e: unknown) {
      if (operation.isCurrent() && !(e instanceof StaleUiOperationError)) setError(extractDisplayErrorMessage(e, 'Не удалось изменить автоконтроль дела.'));
    } finally {
      if (activityPrompt.current === controller) activityPrompt.current = null;
      operation.finish();
    }
  };

  const runPartyAction = async (kind: string, action: (isCurrent: () => boolean) => Promise<void>) => {
    if (!isOpen || !caseActionOwner.capture() || caseData.id <= 0) return;
    if (!allowEdit) { setRelatedPartiesError('Недостаточно прав для изменения связей дела.'); return; }
    const operation = caseActionOwner.begin(`party:${kind}`); if (!operation) return;
    setRelatedPartiesError(null);
    try { await action(operation.isCurrent); }
    catch (error) {
      if (operation.isCurrent() && !(error instanceof StaleUiOperationError)) setRelatedPartiesError(extractDisplayErrorMessage(error, 'Не удалось изменить связи дела.'));
    } finally { operation.finish(); }
  };
  const handleRetryRelatedDirectory = () => runPartyAction('directory', async isCurrent => {
    if (relatedPartyMode === 'legalEntity' ? !canUseLegalEntityRelatedParties : !canUseContactRelatedParties) return;
    await awaitOwnedUiResult(relatedPartyMode === 'legalEntity' ? entityRead.refresh : contactRead.refresh, isCurrent);
  });
  const handleAddRelatedParty = () => runPartyAction('add', async isCurrent => {
    const snapshot = relatedPartyMode === 'legalEntity' ? actionSnapshots.current.entities : actionSnapshots.current.contacts;
    if (relatedPartyMode === 'legalEntity' ? !canUseLegalEntityRelatedParties : !canUseContactRelatedParties) throw new Error('У роли нет доступа к выбранному справочнику участников.');
    if (snapshot.loading || snapshot.error) throw new Error('Справочник участников ещё не подтверждён. Дождитесь загрузки или повторите её.');
    const contact = actionSnapshots.current.contacts.value.find(item => getContactOptionKey(item) === relatedPartySourceKey);
    const entity = actionSnapshots.current.entities.value.find(item => String(item.id) === relatedPartyLegalEntityId);
    const source = relatedPartyMode === 'legalEntity' ? entity && { sourceType: 'legalEntity', sourceId: entity.id }
      : contact && { sourceType: contact.sourceType === 'external' ? 'externalContact' : contact.sourceType === 'legal' ? 'legalEntity' : contact.sourceType, sourceId: contact.sourceId };
    if (!source) throw new Error('Выберите связанное лицо или организацию из текущего справочника.');
    const created = await awaitOwnedUiResult(() => apiService.addCaseRelatedParty(caseData.id, { ...source, role: relatedPartyRole.trim() || 'Участник' }), isCurrent);
    if (!created || !Number.isSafeInteger(created.id) || created.id <= 0 || created.clientCaseId !== caseData.id || created.sourceType !== source.sourceType || created.sourceId !== source.sourceId || typeof created.displayName !== 'string') throw new Error('Не удалось подтвердить добавленную связь. Обновите список связей перед повторным действием.');
    setRelatedParties(items => [...items.filter(item => item.id !== created.id), created].sort((a, b) => a.displayName.localeCompare(b.displayName, 'ru')));
    setRelatedPartySourceKey(''); setRelatedPartyLegalEntityId('');
  });
  const currentParty = (party: ClientCaseRelatedParty) => {
    const snapshot = actionSnapshots.current.parties;
    return !snapshot.loading && !snapshot.error && party.clientCaseId === caseData.id
      && snapshot.value.some(item => item.id === party.id && item.sourceType === party.sourceType && item.sourceId === party.sourceId);
  };
  const handleDeleteRelatedParty = (party: ClientCaseRelatedParty) => runPartyAction('delete', async isCurrent => {
    if (!currentParty(party)) throw new Error('Список связей изменился или недоступен. Обновите его и выберите участника заново.');
    const controller = new AbortController(); deleteConfirmation.current = controller;
    try {
      const ok = await awaitOwnedUiResult(() => confirm({ signal: controller.signal, title: 'Удаление связи', description: `Удалить связь «${party.displayName}» из дела?`, confirmLabel: 'Удалить', cancelLabel: 'Отмена', variant: 'danger' }), isCurrent);
      if (!ok) return;
      if (!currentParty(party)) throw new Error('Список связей изменился или недоступен. Обновите его и выберите участника заново.');
      await awaitOwnedUiResult(() => apiService.deleteCaseRelatedParty(caseData.id, party.id), isCurrent);
      setRelatedParties(items => items.filter(item => item.id !== party.id));
    } finally { if (deleteConfirmation.current === controller) deleteConfirmation.current = null; }
  });

  const normalizedCourtUrl = normalizeExternalUrl(courtUrl);
  const enteredFee = Number(feeAmount.replace(',', '.'));
  const contractAmount = Number.isFinite(enteredFee) ? enteredFee : caseData.feeAmount ?? 0;
  const incomeTotal = financeSummary?.incomeTotal ?? 0;
  const incomePaid = financeSummary?.incomePaid ?? 0;
  const incomeOutstanding = financeSummary?.incomeOutstanding ?? Math.max(0, incomeTotal - incomePaid);
  const daysSinceActivity = getDaysSince(activity.lastViewedAt ?? caseData.lastViewedAt);
  const ownsKeyboard = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.nativeEvent.isComposing || !(event.target instanceof Node) || !event.currentTarget.contains(event.target)) return false;
    const popups = document.querySelectorAll('[role="dialog"], [role="alertdialog"], [role="listbox"]');
    return popups[popups.length - 1] === event.currentTarget;
  };

  const modal = (
    <div className={`fixed inset-0 ${overlayClassName ?? 'z-[1000]'} bg-black/30 flex items-center justify-center p-4`}>
      <div
        ref={modalRef}
        className="w-full max-w-4xl bg-white rounded-2xl shadow-xl"
        role="dialog"
        aria-modal="true"
        onKeyDownCapture={(event) => {
          if (event.defaultPrevented || event.repeat || !ownsKeyboard(event)) return;
          if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'enter') {
            event.preventDefault(); event.stopPropagation(); void handleSave();
          }
        }}
        onKeyDown={(event) => {
          if (!event.defaultPrevented && ownsKeyboard(event)) trapFocus(event);
        }}>
        <div className="flex items-center justify-between px-5 py-4 border-b">
          <h3 className="text-lg font-semibold">Дело</h3>
          <button
            type="button"
            onClick={handleClose}
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
                  Последний просмотр: {formatDocumentDate(activity.lastViewedAt ?? caseData.lastViewedAt, timeZoneId) || 'не фиксировался'}
                  {activity.lastViewedByUserName ? ` · ${activity.lastViewedByUserName}` : ''}
                </div>
                {activity.activityConfirmedAt ? (
                  <div className="mt-1 text-xs text-emerald-700">
                    Подтверждено: {formatDocumentDate(activity.activityConfirmedAt, timeZoneId)}
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
              ref={titleInputRef}
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
              onChange={(e) => {
                if (!allowEdit || !caseActionOwner.capture() || saving || responsiblesLoading || responsiblesError
                  || (e.target.value && !responsibles.some(item => String(item.id) === e.target.value))) return;
                setResponsibleUserId(e.target.value);
              }}
              disabled={saving || responsiblesLoading || Boolean(responsiblesError)}>
              <option value="">Ответственный владельца</option>
              {responsibleUserId && !responsibles.some(item => String(item.id) === responsibleUserId) ? (
                <option value={responsibleUserId}>{caseData.responsibleUserId === Number(responsibleUserId) && caseData.responsibleUserName ? caseData.responsibleUserName : `Ответственный #${responsibleUserId}`}</option>
              ) : null}
              {responsibles.map((item) => (
                <option key={item.id} value={item.id}>
                  {item.fullName}
                </option>
              ))}
            </SystemSelect>
            {responsiblesLoading ? <p className="mt-1 text-xs text-slate-500">Загрузка ответственных...</p> : null}
            {responsiblesError ? <p role="alert" className="mt-1 text-xs text-rose-600">{responsiblesError}
              {onRetryResponsibles ? <button type="button" onClick={() => { if (isOpen && caseActionOwner.capture()) onRetryResponsibles(); }} disabled={responsiblesLoading} className="ml-2 underline">Повторить загрузку ответственных</button> : null}
            </p> : null}
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
          {canViewFinance && caseData.id > 0 ? <div className="min-w-0 md:col-span-2"><CourtRecoveryPanel actionOwner={caseActionOwner} caseItem={caseData} canView={canViewFinance} caseId={caseData.id} canEdit={allowEdit} /></div> : null}

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
                    onChange={(e) => { const value = e.target.value; if (!value || (!entityRead.loading && !entityRead.error && legalEntityOptions.some(item => String(item.id) === value))) setRelatedPartyLegalEntityId(value); else setRelatedPartiesError('Выберите организацию из текущего справочника.'); }}
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
                    onChange={(e) => { const value = e.target.value; if (!value || (!contactRead.loading && !contactRead.error && contactOptions.some(item => getContactOptionKey(item) === value))) setRelatedPartySourceKey(value); else setRelatedPartiesError('Выберите лицо или контакт из текущего справочника.'); }}
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
                  disabled={relatedPartiesSaving || saving || relatedDirectory.loading || !!relatedDirectory.error || !allowEdit || (!canUseContactRelatedParties && !canUseLegalEntityRelatedParties)}>
                  <Plus size={15} /> Добавить
                </button>
              </div>
            ) : (
              <div className="mt-3 rounded-lg border border-dashed border-slate-200 bg-white px-3 py-4 text-sm text-slate-500">
                Связи можно будет добавить после сохранения дела.
              </div>
            )}

            {caseData.id > 0 && allowEdit && (relatedPartyMode === 'legalEntity' ? canUseLegalEntityRelatedParties : canUseContactRelatedParties) && relatedDirectoryError ? (
              <div className="mt-2 flex flex-wrap items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                <span>{relatedDirectoryError}</span>
                <button type="button" className="max-w-full shrink-0 rounded border border-amber-300 px-2 py-1" disabled={saving || relatedDirectory.loading} onClick={() => void handleRetryRelatedDirectory()}>Повторить загрузку участников</button>
              </div>
            ) : null}

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
              onChange={(e) => { hearingEdited.current = true; setNextHearingAt(e.target.value); }}
              disabled={saving}
            />
            <p className="mt-1 text-xs text-slate-500">Часовой пояс даты: {USER_TIME_ZONE_OPTIONS.find(option => option.value === timeZoneId)?.label ?? timeZoneId}.
              {hearingEdited.current && timeZoneRead.value && timeZoneRead.value !== timeZoneId ? ' Введённое время остаётся в этом часовом поясе.' : ''}
            </p>
            <p className="mt-1 text-xs text-gray-600">
              {!nextHearingAt
                ? 'Дата пока не назначена — ожидайте сведения суда или укажите её вручную.'
                : nextHearingAt !== toDateTimeInput(caseData.nextHearingAt, timeZoneId)
                  ? 'Дата изменена: сначала сохраните дело, затем подтвердите её.'
                  : hearingConfirmation.nextHearingConfirmedAt && hearingConfirmation.nextHearingConfirmedForAt && parseInstant(hearingConfirmation.nextHearingConfirmedForAt).getTime() === parseInstant(caseData.nextHearingAt ?? '').getTime()
                    ? `Дата подтверждена ${formatDocumentDate(hearingConfirmation.nextHearingConfirmedAt, timeZoneId)}${hearingConfirmation.nextHearingConfirmedByName ? ` · ${hearingConfirmation.nextHearingConfirmedByName}` : ''}. При переносе отметка сбросится.`
                    : 'Дата ещё не подтверждена сотрудником.'}
            </p>
            {caseData.id > 0 && allowEdit && caseData.status !== 'Closed' && caseData.status !== 'Lost' && caseData.nextHearingAt && parseInstant(caseData.nextHearingAt).getTime() > Date.now() && (!hearingConfirmation.nextHearingConfirmedAt || !hearingConfirmation.nextHearingConfirmedForAt || parseInstant(hearingConfirmation.nextHearingConfirmedForAt).getTime() !== parseInstant(caseData.nextHearingAt).getTime()) ? (
              <button type="button" onClick={() => void handleConfirmNextHearing()} disabled={saving || confirmingNextHearing || nextHearingAt !== toDateTimeInput(caseData.nextHearingAt, timeZoneId)} className="mt-1 rounded-lg border border-blue-200 px-2.5 py-1.5 text-xs font-semibold text-blue-700 disabled:cursor-not-allowed disabled:opacity-50">
                {confirmingNextHearing ? 'Сохраняем…' : 'Подтвердить дату заседания'}
              </button>
            ) : null}
          </div>

          <div className="md:col-span-2">
            <CasePreHearingActions
              actionOwner={caseActionOwner}
              caseItem={caseData}
              timeZoneId={timeZoneId}
              canCreateTask={canCreateTasks}
              datesSaved={nextHearingAt === toDateTimeInput(caseData.nextHearingAt, timeZoneId) && responsibleUserId === String(caseData.responsibleUserId ?? '')}
              onTaskCreated={() => refreshCaseTasks()}
              onOpenTask={onOpenTask}
            />
          </div>

          <div className="md:col-span-2">
            <CaseHearingPreparationChecklist
              actionOwner={caseActionOwner}
              caseItem={caseData}
              timeZoneId={timeZoneId}
              canEdit={allowEdit}
              datesSaved={nextHearingAt === toDateTimeInput(caseData.nextHearingAt, timeZoneId) && responsibleUserId === (caseData.responsibleUserId ? String(caseData.responsibleUserId) : '')}
              initialDocuments={caseDocuments}
              initialDocumentsLoading={documentsLoading}
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
            <div className="mt-2"><ExecutiveWritDocumentUpload actionOwner={caseActionOwner} caseId={caseData.id} canUpload={allowEdit} documents={caseDocuments} onUploaded={refreshDocuments} onAttemptSettled={() => { if (captureCaseOwner()) setUploadRevision((value) => value + 1); }} /></div>
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
                actionOwner={caseActionOwner}
                clientCase={caseData}
                canEdit={allowEdit}
                datesSaved={courtDecisionAt === toDateInput(caseData.courtDecisionAt) && appealDeadlineAt === toDateInput(caseData.appealDeadlineAt)}
                onTaskCreated={() => refreshCaseTasks()}
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
            {allowEdit && !readClosed && caseData.id > 0 ? <CaseDocumentUploadStatusPanel key={caseActionOwner.scope} caseId={caseData.id} revision={uploadRevision}
              onPublished={publishDocumentRead} /> : null}
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
                        {document.uploadedAt ? ` · ${formatDocumentDate(document.uploadedAt, timeZoneId)}` : ''}
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
                            <span>Срок: {formatTaskDateTime(task.dueAt, timeZoneId) || '—'}</span>
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
              onChange={(e) => { historyEdited.current = true; setEventHistory(e.target.value); }}
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
            <CasePositionTemplatePicker actionOwner={caseActionOwner} caseId={caseData.id} text={strategy} onChange={setStrategy} disabled={!allowEdit} />
            <p className="mt-1 text-xs text-gray-600">
              {strategy.trim() !== (caseData.strategy ?? '').trim()
                ? 'Позиция изменена: сохраните дело, прежняя отметка проверки будет сброшена.'
                : positionReview.positionReviewedAt
                  ? `Позиция проверена ${formatDocumentDate(positionReview.positionReviewedAt, timeZoneId)}${positionReview.positionReviewedByName ? ` · ${positionReview.positionReviewedByName}` : ''}`
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
              onClick={handleClose}
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
