import { ArrowLeft, Ban, Check, ChevronDown, Download, FilePlus2, Link2, List, Loader2, Pencil, Printer, Send, Trash2, UploadCloud, UserRound } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { useDocumentStatusSettings } from '../../hooks/useDocumentStatusSettings';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { useToast } from '../../hooks/useToast';
import { useTranslation } from '../../hooks/useTranslation';
import { apiService } from '../../services/api';
import type { Act, ActInput, ActResponsible, ActStatus, ActWorkflowStatus, Client, PaymentKind } from '../../types';
import { normalizeAct, normalizeActResponsibleArray, normalizeClientArray } from '../../utils/acts';
import { toDateInputValue } from '../../utils/dateUtils';
import { getDocumentStatusCodes, getDocumentStatusLabels } from '../../utils/documentStatusSettings';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { ClientStatusBadge } from '../Clients/ClientStatusBadge';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import { StatusChangeModal, type StatusChangeOption } from '../ui/StatusChangeModal';
import { ActModal } from './ActModal';

interface ActDetailPageProps {
  actId: number;
  onBack: () => void;
  onOpenClient?: (clientId: number) => void;
  onOpenInvoice?: (invoiceId: number, type?: PaymentKind) => void;
}

type DocumentFormat = 'word' | 'pdf';
type PageAction = 'send' | 'yandexDisk' | 'correction' | 'notSigned' | 'transferred' | 'signed' | 'cancel' | 'delete';

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

const ACT_WORKFLOW_STATUS_CLASSES: Record<ActWorkflowStatus, string> = {
  Draft: 'bg-slate-100 text-slate-700 border border-slate-200',
  NotSigned: 'bg-slate-100 text-slate-700 border border-slate-200',
  RequiresClientSignature: 'bg-amber-100 text-amber-800 border border-amber-200',
  OnApproval: 'bg-cyan-100 text-cyan-700 border border-cyan-200',
  Approved: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
  ApprovalRejected: 'bg-rose-100 text-rose-700 border border-rose-200',
  NeedsCorrection: 'bg-orange-100 text-orange-700 border border-orange-200',
  OnSigning: 'bg-violet-100 text-violet-700 border border-violet-200',
  Signed: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
  Cancelled: 'bg-rose-100 text-rose-700 border border-rose-200',
};

const ACT_WORKFLOW_STATUS_DESCRIPTIONS: Record<ActWorkflowStatus, string> = {
  Draft: 'Акт вернется в черновик, даты подписания будут очищены.',
  NotSigned: 'Акт будет отмечен как неподписанный.',
  RequiresClientSignature: 'Акт будет ожидать подпись клиента.',
  OnApproval: 'Акт будет отправлен на внутреннее согласование.',
  Approved: 'Акт будет отмечен как согласованный.',
  ApprovalRejected: 'В журнал попадет отказ в согласовании акта.',
  NeedsCorrection: 'Акт будет отмечен как требующий исправления.',
  OnSigning: 'Акт будет передан на подписание.',
  Signed: 'Акт будет отмечен как подписанный клиентом.',
  Cancelled: 'Акт будет аннулирован, будет заполнена дата аннулирования.',
};

function formatDate(value?: string | null): string {
  if (!value) return '—';
  const normalized = toDateInputValue(value);
  if (!normalized) return value;
  const [y, m, d] = normalized.split('-');
  return `${d}.${m}.${y}`;
}

function formatMoney(value?: number | null): string {
  return new Intl.NumberFormat('ru-RU', {
    style: 'currency',
    currency: 'RUB',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(Number(value ?? 0));
}

function downloadBlob(fileName: string, blob: Blob) {
  const href = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = href;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(href);
}

function openBlobInWindow(blob: Blob, targetWindow: Window | null, options?: { autoPrint?: boolean }) {
  const href = URL.createObjectURL(blob);
  const nextWindow = targetWindow ?? window.open('', '_blank', 'width=1180,height=900');
  if (nextWindow) {
    nextWindow.location.href = href;
  }

  const cleanup = () => URL.revokeObjectURL(href);
  if (options?.autoPrint && nextWindow) {
    nextWindow.onload = () => {
      nextWindow.focus();
      nextWindow.print();
      window.setTimeout(cleanup, 60_000);
    };
  } else {
    window.setTimeout(cleanup, 60_000);
  }
}

function getDocumentActionErrorMessage(error: unknown, fallback: string) {
  if (!(error instanceof Error)) {
    return fallback;
  }

  const message = error.message.trim();
  if (!message) {
    return fallback;
  }

  const separatorIndex = message.lastIndexOf(' - ');
  if (separatorIndex >= 0) {
    const detail = message.slice(separatorIndex + 3).trim();
    if (detail) {
      return detail;
    }
  }

  return message;
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

export function ActDetailPage({ actId, onBack, onOpenClient, onOpenInvoice }: ActDetailPageProps) {
  const { t } = useTranslation();
  const { user } = useAuth();
  const { confirm, prompt } = useDialog();
  const toast = useToast();
  const permissions = useRolePermissions(user?.role?.id);
  const canEdit = permissions.acts.canEdit;
  const canDelete = permissions.acts.canDelete;
  const canViewLinkedAccounts = permissions.accounts.canView;
  const canViewLinkedClients = permissions.clients.canView;
  const requiresFinancialActionReason = Boolean(user?.requiresFinancialActionReason);
  const documentStatusSettings = useDocumentStatusSettings();

  const [act, setAct] = useState<Act | null>(null);
  const [clients, setClients] = useState<Client[]>([]);
  const [responsibles, setResponsibles] = useState<ActResponsible[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [documentPreviewHtml, setDocumentPreviewHtml] = useState('');
  const [documentPreviewLoading, setDocumentPreviewLoading] = useState(false);
  const [documentPreviewError, setDocumentPreviewError] = useState<string | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const [editorSubmitting, setEditorSubmitting] = useState(false);
  const [editorError, setEditorError] = useState<string | null>(null);
  const [downloadMenuOpen, setDownloadMenuOpen] = useState(false);
  const [pageActionLoading, setPageActionLoading] = useState<PageAction | null>(null);
  const [statusChangeOpen, setStatusChangeOpen] = useState(false);
  const [statusChangeValue, setStatusChangeValue] = useState<ActWorkflowStatus>('Draft');
  const [statusChangeReason, setStatusChangeReason] = useState('');
  const [statusChangeSubmitting, setStatusChangeSubmitting] = useState(false);
  const [statusChangeError, setStatusChangeError] = useState<string | null>(null);

  const downloadMenuRef = useRef<HTMLDivElement | null>(null);

  const loadActBundle = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [targetResponse, clientsData, responsiblesData] = await Promise.all([
        apiService.getAct(actId),
        canViewLinkedClients ? apiService.getClients() : Promise.resolve([]),
        apiService.getActResponsibles(),
      ]);

      const target = normalizeAct(targetResponse);
      if (!target) {
        setAct(null);
        setError('Акт не найден.');
        return;
      }

      setAct(target);
      setClients(normalizeClientArray(clientsData));
      setResponsibles(normalizeActResponsibleArray(responsiblesData));
    } catch (loadError) {
      setAct(null);
      setError(extractDisplayErrorMessage(loadError, 'Не удалось загрузить данные акта.'));
    } finally {
      setLoading(false);
    }
  }, [actId, canViewLinkedClients]);

  useEffect(() => {
    void loadActBundle();
  }, [loadActBundle]);

  useEffect(() => {
    if (!act?.id) {
      setDocumentPreviewHtml('');
      setDocumentPreviewError(null);
      setDocumentPreviewLoading(false);
      return;
    }

    let active = true;
    const loadPreview = async () => {
      setDocumentPreviewLoading(true);
      setDocumentPreviewError(null);
      try {
        const blob = await apiService.downloadActDocument(act.id, 'html', {
          includeSignatureAndSeal: false,
        });
        const html = await blob.text();
        if (!active) return;
        setDocumentPreviewHtml(html);
      } catch (previewError) {
        if (!active) return;
        setDocumentPreviewHtml('');
        setDocumentPreviewError(
          extractDisplayErrorMessage(previewError, 'Не удалось загрузить печатную форму акта.'),
        );
      } finally {
        if (active) {
          setDocumentPreviewLoading(false);
        }
      }
    };

    void loadPreview();

    return () => {
      active = false;
    };
  }, [act?.id, act?.updatedAt, act?.auditUpdatedAt]);

  useEffect(() => {
    if (!downloadMenuOpen) return undefined;

    const handlePointerDown = (event: MouseEvent) => {
      if (!downloadMenuRef.current) return;
      if (!downloadMenuRef.current.contains(event.target as Node)) {
        setDownloadMenuOpen(false);
      }
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setDownloadMenuOpen(false);
      }
    };

    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [downloadMenuOpen]);

  const selectedClient = useMemo(
    () => clients.find((client) => client.id === act?.clientId) ?? null,
    [act?.clientId, clients],
  );

  const selectedResponsibleLabel = useMemo(() => {
    if (act?.responsibleName?.trim()) {
      return act.responsibleName;
    }

    if (!act?.responsibleId) {
      return t('actResponsibleNotSelected') ?? 'Не указан';
    }

    return responsibles.find((responsible) => responsible.id === act.responsibleId)?.fullName
      ?? `#${act.responsibleId}`;
  }, [act?.responsibleId, act?.responsibleName, responsibles, t]);

  const statusMeta: Record<ActStatus, { label: string; className: string }> = {
    Created: {
      label: t('actStatusCreated') ?? 'Создан',
      className: 'bg-slate-100 text-slate-700 border border-slate-200',
    },
    Transferred: {
      label: t('actStatusTransferred') ?? 'Передано',
      className: 'bg-amber-100 text-amber-800 border border-amber-200',
    },
    Signed: {
      label: t('actStatusSigned') ?? 'Подписан клиентом',
      className: 'bg-emerald-100 text-emerald-700 border border-emerald-200',
    },
    Terminated: {
      label: t('actStatusTerminated') ?? 'Аннулирован',
      className: 'bg-rose-100 text-rose-700 border border-rose-200',
    },
  };

  const workflowStatusLabels = useMemo(
    () => getDocumentStatusLabels(documentStatusSettings, 'act', DEFAULT_ACT_WORKFLOW_STATUS_LABELS),
    [documentStatusSettings],
  );

  const workflowStatusCodes = useMemo(
    () => getDocumentStatusCodes(documentStatusSettings, 'act', ACT_WORKFLOW_STATUS_ORDER),
    [documentStatusSettings],
  );

  const workflowStatusOptions: StatusChangeOption<ActWorkflowStatus>[] = useMemo(
    () => workflowStatusCodes.map((status) => ({
      value: status,
      label: workflowStatusLabels[status],
      description: ACT_WORKFLOW_STATUS_DESCRIPTIONS[status],
      className: ACT_WORKFLOW_STATUS_CLASSES[status],
    })),
    [workflowStatusCodes, workflowStatusLabels],
  );

  const typeMeta = useMemo(() => {
    if (act?.type === 'Income') {
      return {
        label: t('invoiceTypeIncomeShort') ?? 'Доходный',
        className: 'border border-emerald-200 bg-emerald-50 text-emerald-700',
      };
    }

    if (act?.type === 'Expense') {
      return {
        label: t('invoiceTypeExpenseShort') ?? 'Расходный',
        className: 'border border-rose-200 bg-rose-50 text-rose-700',
      };
    }

    return {
      label: 'Без типа',
      className: 'border border-slate-200 bg-slate-50 text-slate-600',
    };
  }, [act?.type, t]);
  const counterpartyLabel = useMemo(() => resolveCounterpartyLabel(act?.type), [act?.type]);

  const creationModeLabel = useMemo(() => {
    if (act?.creationMode === 'Full') return 'Полностью по счету';
    if (act?.creationMode === 'Partial') return 'Частично по счету';
    return 'Ручной акт';
  }, [act?.creationMode]);

  const lineItems = useMemo(() => (act?.lineItems ?? []).filter((item) => Boolean(item?.name || item?.sourceKey)), [act?.lineItems]);
  const currentStatusMeta = act ? (statusMeta[act.status] ?? statusMeta.Created) : statusMeta.Created;
  void currentStatusMeta;
  const currentWorkflowStatus = useMemo<ActWorkflowStatus>(() => {
    if (act?.workflowStatus) return act.workflowStatus;
    if (act?.status === 'Transferred') return 'OnSigning';
    if (act?.status === 'Signed') return 'Signed';
    if (act?.status === 'Terminated') return 'Cancelled';
    return 'Draft';
  }, [act?.status, act?.workflowStatus]);
  const currentWorkflowStatusLabel = workflowStatusLabels[currentWorkflowStatus] ?? workflowStatusLabels.Draft;
  const currentWorkflowStatusClass = ACT_WORKFLOW_STATUS_CLASSES[currentWorkflowStatus] ?? ACT_WORKFLOW_STATUS_CLASSES.Draft;
  const statusChangeTargetLabel = workflowStatusLabels[statusChangeValue] ?? statusChangeValue;
  const statusChangeSubject = act
    ? `Акт №${act.number || act.id} от ${formatDate(act.date)}`
    : 'Акт не выбран';
  const statusChangeConsequences = [
    `Статус документа изменится с «${currentWorkflowStatusLabel}» на «${statusChangeTargetLabel}».`,
    statusChangeValue === 'Cancelled'
      ? 'Основной статус акта станет аннулированным, будет заполнена дата аннулирования.'
      : 'Основной статус акта будет синхронизирован с выбранным статусом документа.',
    'Изменение сохранится сразу после подтверждения.',
  ];
  const printableAmount = formatMoney(act?.amount);
  const actLink = useMemo(() => {
    if (typeof window === 'undefined' || !act?.id) {
      return '';
    }

    const url = new URL(window.location.href);
    url.pathname = `/acts/${act.id}`;
    return `${url.origin}${url.pathname}${url.search}${url.hash}`;
  }, [act?.id]);

  const handlePrintDocument = async () => {
    if (!act?.id) return;

    let previewWindow: Window | null | undefined;
    try {
      previewWindow = window.open('', '_blank', 'width=1180,height=900');
      const blob = await apiService.downloadActDocument(act.id, 'pdf', {
        includeSignatureAndSeal: false,
      });
      openBlobInWindow(blob, previewWindow, { autoPrint: true });
    } catch (printError) {
      if (previewWindow && !previewWindow.closed) {
        previewWindow.close();
      }
      setError(getDocumentActionErrorMessage(printError, 'Не удалось открыть печатную форму акта.'));
    }
  };

  const handleDownloadDocument = async (
    format: DocumentFormat,
    includeSignatureAndSeal: boolean,
  ) => {
    if (!act?.id) return;

    try {
      const blob = await apiService.downloadActDocument(act.id, format, {
        includeSignatureAndSeal,
      });
      const extension = format === 'pdf' ? 'pdf' : 'doc';
      downloadBlob(`act-${act.number || act.id}.${extension}`, blob);
      setDownloadMenuOpen(false);
    } catch (downloadError) {
      setError(getDocumentActionErrorMessage(downloadError, 'Не удалось скачать документ акта.'));
    }
  };

  const handleUploadDocumentToYandexDisk = async () => {
    if (!act?.id) return;

    setPageActionLoading('yandexDisk');
    try {
      const result = await apiService.uploadActDocumentToYandexDisk(act.id, 'pdf', {
        includeSignatureAndSeal: false,
      });
      setError(null);
      toast.success(`Акт отправлен на Яндекс.Диск: ${result.path}.`, 'Я.Диск');
    } catch (uploadError) {
      setError(extractDisplayErrorMessage(uploadError, 'Не удалось отправить акт на Яндекс.Диск.'));
    } finally {
      setPageActionLoading(null);
    }
  };

  const handleActSubmit = async (payload: ActInput) => {
    if (!act) return;

    setEditorSubmitting(true);
    setEditorError(null);
    try {
      const updated = normalizeAct(await apiService.updateAct(act.id, payload));
      if (!updated) {
        throw new Error('Не удалось прочитать обновленный акт.');
      }

      setAct(updated);
      setEditorOpen(false);
      toast.success('Изменения по акту сохранены.');
    } catch (submitError) {
      const message = extractDisplayErrorMessage(submitError, 'Не удалось сохранить акт.');
      setEditorError(message);
      throw submitError;
    } finally {
      setEditorSubmitting(false);
    }
  };

  const handleStatusAction = useCallback(async (
    targetStatus: ActStatus,
    actionKey: Extract<PageAction, 'notSigned' | 'transferred' | 'signed'>,
    title: string,
    description: string,
    confirmLabel: string,
    successMessage: string,
  ) => {
    if (!act) return;

    const accepted = await confirm({
      title,
      description,
      confirmLabel,
      cancelLabel: 'Отмена',
      variant: 'primary',
    });
    if (!accepted) return;

    setPageActionLoading(actionKey);
    try {
      void targetStatus;
      const workflowStatus: ActWorkflowStatus =
        actionKey === 'transferred'
          ? 'OnSigning'
          : actionKey === 'signed'
            ? 'Signed'
            : 'NotSigned';
      const reason = requiresFinancialActionReason
        ? (await prompt({
            title: 'Причина смены статуса акта',
            label: 'Причина',
            multiline: true,
            confirmLabel: 'Продолжить',
            cancelLabel: 'Отмена',
          }))?.trim()
        : null;
      if (requiresFinancialActionReason && !reason) {
        toast.error('Причина обязательна и будет сохранена в журнале.');
        return;
      }

      const updated = normalizeAct(
        await apiService.updateActWorkflowStatus(act.id, workflowStatus, reason || null),
      );
      if (!updated) {
        throw new Error('Не удалось обновить статус акта.');
      }

      setAct(updated);
      setError(null);
      toast.success(successMessage);
    } catch (statusError) {
      setError(extractDisplayErrorMessage(statusError, 'Не удалось обновить статус акта.'));
    } finally {
      setPageActionLoading(null);
    }
  }, [act, confirm, prompt, requiresFinancialActionReason, toast]);

  const openStatusChange = useCallback(() => {
    if (!canEdit || !act) return;

    setStatusChangeValue(currentWorkflowStatus);
    setStatusChangeReason('');
    setStatusChangeError(null);
    setStatusChangeOpen(true);
  }, [act, canEdit, currentWorkflowStatus]);

  const closeStatusChange = useCallback(() => {
    if (statusChangeSubmitting) return;

    setStatusChangeOpen(false);
    setStatusChangeReason('');
    setStatusChangeError(null);
  }, [statusChangeSubmitting]);

  const submitStatusChange = useCallback(async () => {
    if (!canEdit || !act) return;

    try {
      setStatusChangeSubmitting(true);
      setStatusChangeError(null);
      const updated = normalizeAct(
        await apiService.updateActWorkflowStatus(act.id, statusChangeValue, statusChangeReason.trim() || null),
      );
      if (!updated) {
        throw new Error('Не удалось обновить статус акта.');
      }

      setAct(updated);
      setError(null);
      setStatusChangeOpen(false);
      setStatusChangeReason('');
      toast.success('Статус акта обновлен.');
    } catch (statusError) {
      setStatusChangeError(extractDisplayErrorMessage(statusError, 'Не удалось изменить статус акта.'));
    } finally {
      setStatusChangeSubmitting(false);
    }
  }, [act, canEdit, statusChangeReason, statusChangeValue, toast]);

  const handleDelete = useCallback(async () => {
    if (!act) return;

    const accepted = await confirm({
      title: 'Удалить акт',
      description: 'Удалить акт без возможности восстановления?',
      confirmLabel: 'Удалить',
      cancelLabel: 'Отмена',
      variant: 'danger',
    });
    if (!accepted) return;

    setPageActionLoading('delete');
    try {
      await apiService.deleteAct(act.id);
      toast.success('Акт удален.');
      onBack();
    } catch (deleteError) {
      setError(extractDisplayErrorMessage(deleteError, 'Не удалось удалить акт.'));
    } finally {
      setPageActionLoading(null);
    }
  }, [act, confirm, onBack, toast]);

  const handleCancelAct = useCallback(async () => {
    if (!act) return;

    const reason = await prompt({
      title: 'Аннулирование акта',
      label: 'Причина аннулирования',
      multiline: true,
      confirmLabel: 'Аннулировать',
      cancelLabel: 'Отмена',
    });
    if (!reason?.trim()) {
      toast.error('Для аннулирования акта нужна причина.');
      return;
    }

    setPageActionLoading('cancel');
    try {
      const updated = normalizeAct(await apiService.cancelAct(act.id, reason.trim()));
      if (!updated) {
        throw new Error('Не удалось прочитать аннулированный акт.');
      }

      setAct(updated);
      setError(null);
      toast.success('Акт аннулирован.');
    } catch (cancelError) {
      setError(extractDisplayErrorMessage(cancelError, 'Не удалось аннулировать акт.'));
    } finally {
      setPageActionLoading(null);
    }
  }, [act, prompt, toast]);

  const handleCreateCorrection = useCallback(async () => {
    if (!act) return;

    const accepted = await confirm({
      title: 'Создать корректировочный акт',
      description: `Будет создан черновик корректировочного акта на основании акта №${act.number || act.id}. Позиции, сумма, счет и связь с договором будут скопированы.`,
      confirmLabel: 'Создать корректировку',
      cancelLabel: 'Отмена',
      variant: 'primary',
    });
    if (!accepted) return;

    const reason = (await prompt({
      title: 'Корректировка акта',
      label: 'Причина корректировки',
      multiline: true,
      confirmLabel: 'Создать',
      cancelLabel: 'Отмена',
    }))?.trim() || null;

    setPageActionLoading('correction');
    try {
      const correction = normalizeAct(await apiService.createActCorrection(act.id, reason));
      if (!correction) {
        throw new Error('Не удалось прочитать корректировочный акт.');
      }

      setError(null);
      toast.success(`Создан корректировочный акт №${correction.number || correction.id}.`);
      await loadActBundle();
    } catch (correctionError) {
      setError(extractDisplayErrorMessage(correctionError, 'Не удалось создать корректировочный акт.'));
    } finally {
      setPageActionLoading(null);
    }
  }, [act, confirm, loadActBundle, prompt, toast]);

  const handleSend = useCallback(async () => {
    if (!act) return;

    const accepted = await confirm({
      title: 'Отправить акт',
      description: 'Сформировать документ WORD и отправить на email клиента?',
      confirmLabel: 'Отправить',
      cancelLabel: 'Отмена',
      variant: 'primary',
    });
    if (!accepted) return;

    setPageActionLoading('send');
    try {
      await apiService.sendActByEmail(act.id, {
        format: 'word',
        email: selectedClient?.email || undefined,
      });
      setError(null);
      toast.success(
        selectedClient?.email
          ? `Документ WORD отправлен на ${selectedClient.email}.`
          : 'Документ WORD отправлен клиенту по email.',
        'Акт отправлен',
      );
    } catch (sendError) {
      const message = extractDisplayErrorMessage(sendError, 'Не удалось выполнить отправку акта.');
      setError(message);
      toast.error(message);
    } finally {
      setPageActionLoading(null);
    }
  }, [act, confirm, selectedClient?.email, toast]);

  const notSignedDisabled = !canEdit || currentWorkflowStatus === 'NotSigned' || act?.status === 'Terminated';
  const transferredDisabled = !canEdit || currentWorkflowStatus === 'OnSigning' || act?.status === 'Signed' || act?.status === 'Terminated';
  const signedDisabled = !canEdit || currentWorkflowStatus === 'Signed' || act?.status === 'Terminated';
  const correctionDisabled = !canEdit || Boolean(act?.originalActId) || act?.status === 'Terminated';
  const cancelDisabled = !canEdit || act?.status === 'Terminated';
  const workflowStatusBadgeClass = 'inline-flex items-center rounded-full px-3 py-1 text-sm font-semibold transition';
  const renderWorkflowStatusControl = () => {
    const badge = (
      <span className={`${workflowStatusBadgeClass} ${currentWorkflowStatusClass}`}>
        {currentWorkflowStatusLabel}
      </span>
    );

    if (!canEdit) return badge;

    return (
      <button
        type="button"
        onClick={openStatusChange}
        className="inline-flex rounded-full focus:outline-none focus:ring-2 focus:ring-blue-200"
        title="Изменить статус документа"
        aria-label={`Изменить статус документа акта №${act?.number || act?.id || ''}`.trim()}
      >
        {badge}
      </button>
    );
  };

  if (act && editorOpen) {
    return (
      <div className="min-h-screen bg-gray-50">
        <ActModal
          open
          mode="edit"
          act={act}
          onClose={() => {
            if (editorSubmitting) return;
            setEditorError(null);
            setEditorOpen(false);
          }}
          onBack={() => {
            if (editorSubmitting) return;
            setEditorError(null);
            setEditorOpen(false);
          }}
          onSubmit={handleActSubmit}
          submitting={editorSubmitting}
          errorMessage={editorError}
          clients={clients}
          responsibles={responsibles}
          lookupsLoading={loading}
          presentation="page"
          onOpenInvoice={canViewLinkedAccounts ? onOpenInvoice : undefined}
          canViewInvoices={canViewLinkedAccounts}
        />
      </div>
    );
  }

  if (loading) {
    return (
      <div className="p-8">
        <div className="inline-flex items-center gap-3 rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-600 shadow-sm">
          <Loader2 className="h-4 w-4 animate-spin" />
          Загрузка акта...
        </div>
      </div>
    );
  }

  if (!act) {
    return (
      <div className="p-8">
        <div className="rounded-2xl border border-rose-200 bg-rose-50 p-5 text-sm text-rose-700">
          {error ?? 'Акт не найден.'}
        </div>
      </div>
    );
  }

  return (
    <div className="w-full px-4 py-6 sm:px-6 lg:px-8">
      <div className="relative w-full overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4">
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={onBack}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50"
            >
              <ArrowLeft className="h-4 w-4" />
              Назад
            </button>

            <div>
              <h2 className="text-xl font-semibold text-slate-900">
                Акт №{act.number || '—'} от {formatDate(act.date)}
              </h2>
              <p className="text-sm text-slate-500">
                Просмотр акта с действиями и документом для печати/отправки
              </p>
            </div>
          </div>

          <div className="flex items-center gap-2">
            {renderWorkflowStatusControl()}
            {canEdit ? (
              <button
                type="button"
                onClick={() => {
                  setEditorError(null);
                  setEditorOpen(true);
                }}
                className="inline-flex items-center rounded-lg border border-slate-200 bg-white p-2 text-slate-700 shadow-sm hover:bg-slate-50"
                aria-label="Редактировать акт"
                title="Редактировать акт"
              >
                <Pencil className="h-4 w-4" />
              </button>
            ) : null}
          </div>
        </div>

        <div className="space-y-4 bg-slate-100 px-4 py-4">
          {error ? (
            <div className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
              {error}
            </div>
          ) : null}

          <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
            <section className="rounded-xl border border-slate-300 bg-white p-3">
              <div className="mb-3 flex items-center justify-between gap-3 px-1">
                <div>
                  <h3 className="text-sm font-semibold text-slate-900">Печатный шаблон акта</h3>
                </div>
                {documentPreviewLoading ? (
                  <span className="inline-flex items-center gap-2 text-xs text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Загружаем шаблон...
                  </span>
                ) : null}
              </div>

              <div className="overflow-hidden rounded-xl border border-slate-200 bg-slate-50">
                {documentPreviewError ? (
                  <div className="flex min-h-[22rem] items-center justify-center px-6 text-center text-sm text-rose-600">
                    {documentPreviewError}
                  </div>
                ) : documentPreviewHtml ? (
                  <iframe
                    title={`Печатный шаблон акта ${act.number || ''}`.trim()}
                    srcDoc={documentPreviewHtml}
                    sandbox="allow-same-origin"
                    className="h-[72rem] w-full bg-white"
                  />
                ) : (
                  <div className="flex min-h-[22rem] items-center justify-center px-6 text-center text-sm text-slate-500">
                    Подготовка печатного шаблона...
                  </div>
                )}
              </div>
            </section>

            <aside className="space-y-3">
              <div className="rounded-lg border border-slate-300 bg-white p-3">
                <div className="flex items-center justify-between gap-3">
                  <span className="text-xl font-semibold">{printableAmount}</span>
                  <div className="flex items-center gap-2">
                    {renderWorkflowStatusControl()}
                  </div>
                </div>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm">
                <div className="mb-2 flex items-center gap-2 font-medium">
                  <Link2 className="h-4 w-4" />
                  Ссылка на акт
                </div>
                {actLink ? (
                  <a href={actLink} className="break-all text-blue-600 underline">
                    {actLink}
                  </a>
                ) : (
                  <span className="text-slate-500">—</span>
                )}
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm">
                <div className="mb-3 flex items-center gap-2 font-medium">
                  <List className="h-4 w-4" />
                  Документ и основание
                </div>
                <div className="space-y-3">
                  <div>
                    <div className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-500">Счет</div>
                    {act.invoiceId && act.invoiceNumber && canViewLinkedAccounts && onOpenInvoice ? (
                      <button
                        type="button"
                        onClick={() => onOpenInvoice(act.invoiceId as number, act.type ?? undefined)}
                        className="w-full rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-left transition hover:border-blue-200 hover:bg-blue-50/40"
                      >
                        <div className="font-medium text-blue-700 underline decoration-blue-300 underline-offset-2">
                          Счет №{act.invoiceNumber}
                        </div>
                        <div className="mt-1 text-xs text-slate-500">Открыть связанный счет</div>
                      </button>
                    ) : act.invoiceNumber ? (
                      <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-slate-700">
                        Счет №{act.invoiceNumber}
                      </div>
                    ) : (
                      <div className="rounded-lg border border-dashed border-slate-200 bg-slate-50 px-3 py-2 text-slate-500">
                        Связанный счет не указан.
                      </div>
                    )}
                  </div>

                  <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-3 text-sm leading-6">
                    {act.originalActId ? (
                      <p>
                        <span className="text-slate-500">Корректировка к акту:</span>{' '}
                        №{act.originalActNumber || act.originalActId}
                      </p>
                    ) : null}
                    {!act.originalActId && act.correctionActs?.length ? (
                      <div className="mb-2 rounded-lg border border-blue-100 bg-blue-50 px-3 py-2 leading-5">
                        <div className="text-xs font-semibold uppercase tracking-wide text-blue-700">
                          Корректировочные акты
                        </div>
                        <div className="mt-1 space-y-1">
                          {act.correctionActs.map((correction) => (
                            <div key={correction.id} className="flex items-center justify-between gap-3 text-sm">
                              <span className="font-medium text-blue-800">
                                №{correction.number || correction.id} от {formatDate(correction.date)}
                              </span>
                              <span className="shrink-0 text-blue-700">{formatMoney(correction.amount)}</span>
                            </div>
                          ))}
                        </div>
                      </div>
                    ) : null}
                    <p><span className="text-slate-500">Тип:</span> {typeMeta.label}</p>
                    <p><span className="text-slate-500">Режим:</span> {creationModeLabel}</p>
                    <p><span className="text-slate-500">Дата акта:</span> {formatDate(act.date)}</p>
                    <p><span className="text-slate-500">Ответственный:</span> {selectedResponsibleLabel}</p>
                    {act.cancelledAt || act.cancelledReason ? (
                      <p><span className="text-slate-500">Аннулирование:</span> {[formatDate(act.cancelledAt), act.cancelledReason?.trim()].filter(Boolean).join(' · ')}</p>
                    ) : null}
                    <p><span className="text-slate-500">Комментарий:</span> {act.comment?.trim() || '—'}</p>
                    <p><span className="text-slate-500">Позиции:</span> {lineItems.length}</p>
                  </div>
                </div>
              </div>

              <div className="rounded-lg border border-slate-300 bg-white p-3 text-sm leading-6">
                <div className="mb-3 flex items-center gap-2 font-medium">
                  <UserRound className="h-4 w-4" />
                  {counterpartyLabel}
                </div>
                <p>
                  <span className="text-slate-500">{counterpartyLabel}:</span>{' '}
                  {selectedClient && canViewLinkedClients && onOpenClient ? (
                    <button
                      type="button"
                      onClick={() => onOpenClient(selectedClient.id)}
                      className="text-blue-600 underline hover:text-blue-700"
                    >
                      {selectedClient.name}
                    </button>
                  ) : (
                    selectedClient?.name || act.clientName || '—'
                  )}
                </p>
                {selectedClient?.clientStatus ? (
                  <div className="mt-2">
                    <ClientStatusBadge status={selectedClient.clientStatus} />
                  </div>
                ) : null}
                <p className="mt-2"><span className="text-slate-500">Телефон:</span> {selectedClient?.phone || '—'}</p>
                <p><span className="text-slate-500">Email:</span> {selectedClient?.email || '—'}</p>
                <p><span className="text-slate-500">Адрес:</span> {selectedClient?.address || '—'}</p>
              </div>

              <ObjectAuditLauncher
                objectType="act"
                objectId={act.id}
                createdAt={act.createdAt}
                createdByUserName={act.auditCreatedByUserName}
                updatedAt={act.auditUpdatedAt}
                updatedByUserName={act.auditUpdatedByUserName}
                isDeleted={act.auditIsDeleted}
                deletedAt={act.auditDeletedAt}
                deletedByUserName={act.auditDeletedByUserName}
                triggerTitle="Аудит акта"
                triggerDescription="Паспорт акта и история изменений открываются в отдельном окне."
                title="История акта"
                className="rounded-lg border-slate-300 bg-white"
                buttonClassName="bg-slate-50"
              />
            </aside>
          </div>

          <div className={`grid gap-2 rounded-lg border border-slate-300 bg-white p-2 ${canDelete ? 'grid-cols-2 md:grid-cols-3 xl:grid-cols-9' : 'grid-cols-2 md:grid-cols-3 xl:grid-cols-8'}`}>
            <button
              type="button"
              onClick={() => void handleSend()}
              disabled={pageActionLoading === 'send'}
              className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {pageActionLoading === 'send' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
              Отправить
            </button>

            <button
              type="button"
              onClick={() => void handlePrintDocument()}
              className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm text-slate-700 transition hover:bg-slate-50"
            >
              <Printer className="h-4 w-4" />
              Печать
            </button>

            <button
              type="button"
              onClick={() => void handleUploadDocumentToYandexDisk()}
              disabled={pageActionLoading === 'yandexDisk'}
              className="inline-flex items-center justify-center gap-2 rounded border border-sky-200 px-3 py-2 text-sm text-sky-700 transition hover:bg-sky-50 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {pageActionLoading === 'yandexDisk' ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
              Я.Диск
            </button>

            <button
              type="button"
              onClick={() => void handleCreateCorrection()}
              disabled={correctionDisabled || pageActionLoading === 'correction'}
              className="inline-flex items-center justify-center gap-2 rounded border border-blue-200 px-3 py-2 text-sm text-blue-700 transition hover:bg-blue-50 disabled:cursor-not-allowed disabled:opacity-60"
              title={act.originalActId ? 'Корректировка создается только от исходного акта' : 'Создать корректировочный акт'}
            >
              {pageActionLoading === 'correction' ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />}
              Корректировка
            </button>

            <div className="relative" ref={downloadMenuRef}>
              <button
                type="button"
                onClick={() => setDownloadMenuOpen((prev) => !prev)}
                className="inline-flex w-full items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm text-slate-700 transition hover:bg-slate-50"
              >
                <Download className="h-4 w-4" />
                Скачать
                <ChevronDown className="h-4 w-4" />
              </button>
              {downloadMenuOpen ? (
                <div className="absolute bottom-full left-0 z-20 mb-2 w-full min-w-[20rem] rounded-xl border border-slate-200 bg-white p-1 shadow-lg">
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('pdf', false)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>PDF без подписи и печати</span>
                    <span className="text-xs text-slate-400">.pdf</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('pdf', true)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>PDF с подписью и печатью</span>
                    <span className="text-xs text-slate-400">.pdf</span>
                  </button>
                  <div className="my-1 border-t border-slate-100" />
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('word', false)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>Word без подписи и печати</span>
                    <span className="text-xs text-slate-400">.doc</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => void handleDownloadDocument('word', true)}
                    className="flex w-full items-center justify-between rounded-lg px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                  >
                    <span>Word с подписью и печатью</span>
                    <span className="text-xs text-slate-400">.doc</span>
                  </button>
                </div>
              ) : null}
            </div>

            <button
              type="button"
              onClick={() => void handleStatusAction(
                'Created',
                'notSigned',
                'Отметить акт неподписанным',
                'Подтвердите перевод акта в статус «Не подписан».',
                'Не подписан',
                'Статус акта обновлен: Не подписан.',
              )}
              disabled={notSignedDisabled || pageActionLoading === 'notSigned'}
              className={`inline-flex items-center justify-center gap-2 rounded border px-3 py-2 text-sm transition disabled:cursor-not-allowed disabled:opacity-60 ${
                currentWorkflowStatus === 'NotSigned'
                  ? 'border-slate-200 bg-slate-50 text-slate-700'
                  : 'border-slate-200 text-slate-700 hover:bg-slate-50'
              }`}
            >
              {pageActionLoading === 'notSigned' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Ban className="h-4 w-4" />}
              Не подписан
            </button>

            <button
              type="button"
              onClick={() => void handleStatusAction(
                'Transferred',
                'transferred',
                'Передать акт',
                'Подтвердите перевод акта в статус «Передано».',
                'Передан',
                'Статус акта обновлен: Передано.',
              )}
              disabled={transferredDisabled || pageActionLoading === 'transferred'}
              className={`inline-flex items-center justify-center gap-2 rounded border px-3 py-2 text-sm transition disabled:cursor-not-allowed disabled:opacity-60 ${
                act.status === 'Transferred'
                  ? 'border-amber-200 bg-amber-50 text-amber-700'
                  : 'border-slate-200 text-slate-700 hover:bg-slate-50'
              }`}
            >
              {pageActionLoading === 'transferred' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
              Передан
            </button>

            <button
              type="button"
              onClick={() => void handleStatusAction(
                'Signed',
                'signed',
                'Подписать акт',
                'Подтвердите перевод акта в статус «Подписан клиентом».',
                'Подписан клиентом',
                'Статус акта обновлен: Подписан клиентом.',
              )}
              disabled={signedDisabled || pageActionLoading === 'signed'}
              className={`inline-flex items-center justify-center gap-2 rounded border px-3 py-2 text-sm transition disabled:cursor-not-allowed disabled:opacity-60 ${
                act.status === 'Signed'
                  ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                  : 'border-slate-200 text-slate-700 hover:bg-slate-50'
              }`}
            >
              {pageActionLoading === 'signed' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
              Подписан клиентом
            </button>

            <button
              type="button"
              onClick={() => void handleCancelAct()}
              disabled={cancelDisabled || pageActionLoading === 'cancel'}
              className={`inline-flex items-center justify-center gap-2 rounded border px-3 py-2 text-sm transition disabled:cursor-not-allowed disabled:opacity-60 ${
                act.status === 'Terminated'
                  ? 'border-rose-200 bg-rose-50 text-rose-700'
                  : 'border-slate-200 text-slate-700 hover:border-rose-200 hover:bg-rose-50 hover:text-rose-700'
              }`}
            >
              {pageActionLoading === 'cancel' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Ban className="h-4 w-4" />}
              Аннулировать
            </button>

            {canDelete ? (
              <button
                type="button"
                onClick={() => void handleDelete()}
                disabled={pageActionLoading === 'delete'}
                className="inline-flex items-center justify-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm text-slate-700 transition hover:border-rose-200 hover:bg-rose-50 hover:text-rose-700 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {pageActionLoading === 'delete' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
                Удалить
              </button>
            ) : null}
          </div>
        </div>
      </div>
      <StatusChangeModal<ActWorkflowStatus>
        open={statusChangeOpen}
        title="Смена статуса документа"
        subject={statusChangeSubject}
        currentLabel={currentWorkflowStatusLabel}
        currentValue={currentWorkflowStatus}
        value={statusChangeValue}
        options={workflowStatusOptions}
        onChange={(value) => {
          setStatusChangeValue(value);
          if (statusChangeError) {
            setStatusChangeError(null);
          }
        }}
        reason={statusChangeReason}
        reasonRequired={requiresFinancialActionReason}
        onReasonChange={(value) => {
          setStatusChangeReason(value);
          if (statusChangeError) {
            setStatusChangeError(null);
          }
        }}
        consequences={statusChangeConsequences}
        onCancel={closeStatusChange}
        onConfirm={submitStatusChange}
        confirmLabel={`Изменить статус акта на «${statusChangeTargetLabel}»`}
        error={statusChangeError}
        loading={statusChangeSubmitting}
      />
    </div>
  );
}
