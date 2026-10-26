import { SystemCheckbox } from '../ui/SystemCheckbox';
import { SystemDateInput } from '../ui/SystemDateInput';
import { SystemSelect } from '../ui/SystemSelect';
import { AlertTriangle, ArrowLeft, Ban, Check, Download, FileText, FileX, GripVertical, Loader2, Mail, Pencil, Plus, Printer, Save, Send, Settings2, UploadCloud, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useDialog } from '../../hooks/useDialog';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { DocumentSellerPicker } from '../Documents/DocumentSellerPicker';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { apiService, type ContractRulesSettingsDto } from '../../services/api';
import type { Act, Contract, ContractInput, ContractLegalEntity, Invoice, Payment } from '../../types';
import {
  getContractBillingModeLabel,
  getContractEffectiveAmount,
  getContractExecutionMetrics,
  getContractReconciliationWarnings,
} from '../../utils/contracts';
import { fromInputToApiDate, todayYMD, toDateInputValue, toRuDate } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { useToast } from '../../hooks/useToast';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';
import { CounterpartyPickerModal, type CounterpartyKind, type CounterpartySelection } from '../ui/CounterpartyPickerModal';
import { MessengerDocumentDeliveryModal } from '../Cases/MessengerDocumentDeliveryModal';
import { ElectronicSignatureModal } from '../Documents/ElectronicSignatureModal';

interface Props {
  contractId: number;
  onBack: () => void;
  onOpenClient?: (clientId: number) => void;
  onOpenInvoiceEditor?: (invoiceId: number) => void;
  onOpenActEditor?: (actId: number) => void;
  onOpenMoneyEditor?: (paymentId: number) => void;
}

type Tab = 'clients' | 'invoices' | 'acts' | 'money';

type ClientTableColumnKey = 'name' | 'company' | 'comment';
type InvoiceTableColumnKey = 'number' | 'client' | 'date' | 'amount' | 'status';
type ActTableColumnKey = 'number' | 'client' | 'date' | 'amount' | 'status';
type PaymentTableColumnKey = 'id' | 'date' | 'client' | 'description' | 'amount';
type RelatedDocumentsScope = 'contract' | 'clients';

type TabListState<T> = {
  items: T[];
  total: number;
  loading: boolean;
  error: string | null;
};

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
const TABLE_STORAGE_PREFIX = 'pp.contract_detail_table';

type DetailTableColumn<K extends string> = {
  key: K;
  label: string;
  defaultWidth: number;
  defaultVisible?: boolean;
  align?: 'left' | 'right';
};

const CLIENT_TABLE_COLUMNS: DetailTableColumn<ClientTableColumnKey>[] = [
  { key: 'name', label: 'Контрагент', defaultWidth: 260 },
  { key: 'company', label: 'Компания / реквизиты', defaultWidth: 220 },
  { key: 'comment', label: 'Комментарий', defaultWidth: 320 },
];

const INVOICE_TABLE_COLUMNS: DetailTableColumn<InvoiceTableColumnKey>[] = [
  { key: 'number', label: 'Номер счета', defaultWidth: 180 },
  { key: 'client', label: 'Клиент', defaultWidth: 220 },
  { key: 'date', label: 'Дата', defaultWidth: 140 },
  { key: 'amount', label: 'Сумма', defaultWidth: 160, align: 'right' },
  { key: 'status', label: 'Статус', defaultWidth: 180 },
];

const ACT_TABLE_COLUMNS: DetailTableColumn<ActTableColumnKey>[] = [
  { key: 'number', label: 'Номер акта', defaultWidth: 180 },
  { key: 'client', label: 'Клиент', defaultWidth: 220 },
  { key: 'date', label: 'Дата', defaultWidth: 140 },
  { key: 'amount', label: 'Сумма', defaultWidth: 160, align: 'right' },
  { key: 'status', label: 'Статус', defaultWidth: 180 },
];

const PAYMENT_TABLE_COLUMNS: DetailTableColumn<PaymentTableColumnKey>[] = [
  { key: 'id', label: 'ID', defaultWidth: 120 },
  { key: 'date', label: 'Дата', defaultWidth: 140 },
  { key: 'client', label: 'Клиент', defaultWidth: 220 },
  { key: 'description', label: 'Описание', defaultWidth: 300 },
  { key: 'amount', label: 'Сумма', defaultWidth: 160, align: 'right' },
];

function mapVisibleColumns<K extends string>(
  columns: DetailTableColumn<K>[],
  orderedVisibleColumns: K[],
): DetailTableColumn<K>[] {
  const columnMap = new Map(columns.map((column) => [column.key, column]));
  return orderedVisibleColumns.flatMap((columnKey) => {
    const column = columnMap.get(columnKey);
    return column ? [column] : [];
  });
}

const tabLabels: Record<Tab, string> = {
  clients: 'Контрагенты',
  invoices: 'Счета',
  acts: 'Акты',
  money: 'Деньги',
};

function createTabListState<T>(): TabListState<T> {
  return {
    items: [],
    total: 0,
    loading: false,
    error: null,
  };
}

const formatDate = (value?: string | null) => (value ? (toRuDate(value) || value) : '—');
const formatAmount = (value?: number | null) =>
  typeof value === 'number' ? value.toLocaleString('ru-RU') : '—';

const formatAmountInput = (value?: number | null) => (typeof value === 'number' ? String(value) : '');

const contractStatusLabels: Record<NonNullable<Contract['status']>, string> = {
  Draft: 'Черновик',
  NotSigned: 'Не подписан',
  RequiresClientSignature: 'Требуется подпись клиента',
  OnApproval: 'На согласовании',
  Approved: 'Согласован',
  ApprovalRejected: 'Отказ в согласовании',
  NeedsCorrection: 'Требует исправления',
  OnSigning: 'На подписании',
  Concluded: 'Подписан клиентом',
  Terminated: 'Расторгнут',
  Cancelled: 'Аннулирован',
};

const cancellableContractStatuses: Array<NonNullable<Contract['status']>> = [
  'Draft',
  'NotSigned',
  'RequiresClientSignature',
  'OnApproval',
  'Approved',
  'ApprovalRejected',
  'NeedsCorrection',
  'OnSigning',
];

function parseAmountInput(value: string): number | null | undefined {
  const normalized = value.replace(/\s+/g, '').replace(',', '.').trim();
  if (!normalized) return null;

  const parsed = Number(normalized);
  if (Number.isNaN(parsed) || parsed < 0) return undefined;

  return parsed;
}

function getContractLegalEntityName(entity: Pick<ContractLegalEntity, 'id' | 'shortName' | 'fullName'>): string {
  return entity.shortName?.trim() || entity.fullName?.trim() || `Юрлицо #${entity.id}`;
}

export function ContractDetailPage({
  contractId,
  onBack,
  onOpenClient,
  onOpenInvoiceEditor,
  onOpenActEditor,
  onOpenMoneyEditor,
}: Props) {
  const { user } = useAuth();
  const isAdmin = (user?.role?.name ?? '').toLowerCase() === 'admin';
  const toast = useToast();
  const { confirm, prompt } = useDialog();
  const rolePermissions = useRolePermissions(user?.role?.id);
  const canViewClients = rolePermissions.clients.canView;
  const canViewLegalEntities = rolePermissions.legalEntities.canView;
  const canViewInvoices = rolePermissions.accounts.canView;
  const canViewActs = rolePermissions.acts.canView;
  const canViewPayments = rolePermissions.payments.canView;
  const allowedCounterpartyScopes = useMemo<CounterpartyKind[]>(() => {
    const scopes: CounterpartyKind[] = [];
    if (canViewClients) scopes.push('client');
    if (canViewLegalEntities) scopes.push('legalEntity');
    return scopes;
  }, [canViewClients, canViewLegalEntities]);
  const canSelectCounterparties = allowedCounterpartyScopes.length > 0;
  const requiresFinancialActionReason = Boolean(user?.requiresFinancialActionReason);
  const [contract, setContract] = useState<Contract | null>(null);
  const [contractRules, setContractRules] = useState<ContractRulesSettingsDto>({
    autoGenerateWhenEmpty: true,
    allowManualNumberEdit: true,
  });
  const [amountInput, setAmountInput] = useState('');
  const [activeTab, setActiveTab] = useState<Tab>('clients');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [workflowSaving, setWorkflowSaving] = useState(false);
  const [documentSaving, setDocumentSaving] = useState(false);
  const [editMode, setEditMode] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [messengerDeliveryOpen, setMessengerDeliveryOpen] = useState(false);
  const [electronicSignatureOpen, setElectronicSignatureOpen] = useState(false);

  const [pickerOpen, setPickerOpen] = useState(false);
  const visibleTabs = useMemo(
    () =>
      (Object.keys(tabLabels) as Tab[]).filter((tab) => {
        if (tab === 'invoices') return canViewInvoices;
        if (tab === 'acts') return canViewActs;
        if (tab === 'money') return canViewPayments;
        return true;
      }),
    [canViewActs, canViewInvoices, canViewPayments],
  );

  const {
    columnOrder: clientColumnOrder,
    orderedVisibleColumns: clientOrderedVisibleColumns,
    visibleColumns: clientVisibleColumns,
    draggingColumn: draggingClientColumn,
    toggleColumn: toggleClientColumn,
    moveColumn: moveClientColumn,
    handleColumnDragStart: handleClientColumnDragStart,
    handleColumnDragOver: handleClientColumnDragOver,
    handleColumnDrop: handleClientColumnDrop,
    handleColumnDragEnd: handleClientColumnDragEnd,
    handleResizeStart: handleClientResizeStart,
    getColumnWidth: getClientColumnWidth,
    getTableWidth: getClientTableWidth,
    resetTableSettings: resetClientTableSettings,
  } = usePersistedTableSettings<ClientTableColumnKey>({
    storageKey: `${TABLE_STORAGE_PREFIX}.clients.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'contract_detail_clients_table',
    columns: CLIENT_TABLE_COLUMNS.map(({ key, defaultVisible, defaultWidth }) => ({
      key,
      defaultVisible,
      defaultWidth,
    })),
    defaultPageSize: PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 110,
  });

  const {
    columnOrder: invoiceColumnOrder,
    orderedVisibleColumns: invoiceOrderedVisibleColumns,
    visibleColumns: invoiceVisibleColumns,
    pageSize: invoicePageSize,
    setPageSize: setInvoicePageSize,
    draggingColumn: draggingInvoiceColumn,
    toggleColumn: toggleInvoiceColumn,
    moveColumn: moveInvoiceColumn,
    handleColumnDragStart: handleInvoiceColumnDragStart,
    handleColumnDragOver: handleInvoiceColumnDragOver,
    handleColumnDrop: handleInvoiceColumnDrop,
    handleColumnDragEnd: handleInvoiceColumnDragEnd,
    handleResizeStart: handleInvoiceResizeStart,
    getColumnWidth: getInvoiceColumnWidth,
    getTableWidth: getInvoiceTableWidth,
    resetTableSettings: resetInvoiceTableSettings,
  } = usePersistedTableSettings<InvoiceTableColumnKey>({
    storageKey: `${TABLE_STORAGE_PREFIX}.invoices.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'contract_detail_invoices_table',
    columns: INVOICE_TABLE_COLUMNS.map(({ key, defaultVisible, defaultWidth }) => ({
      key,
      defaultVisible,
      defaultWidth,
    })),
    defaultPageSize: PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 110,
  });

  const {
    columnOrder: actColumnOrder,
    orderedVisibleColumns: actOrderedVisibleColumns,
    visibleColumns: actVisibleColumns,
    pageSize: actPageSize,
    setPageSize: setActPageSize,
    draggingColumn: draggingActColumn,
    toggleColumn: toggleActColumn,
    moveColumn: moveActColumn,
    handleColumnDragStart: handleActColumnDragStart,
    handleColumnDragOver: handleActColumnDragOver,
    handleColumnDrop: handleActColumnDrop,
    handleColumnDragEnd: handleActColumnDragEnd,
    handleResizeStart: handleActResizeStart,
    getColumnWidth: getActColumnWidth,
    getTableWidth: getActTableWidth,
    resetTableSettings: resetActTableSettings,
  } = usePersistedTableSettings<ActTableColumnKey>({
    storageKey: `${TABLE_STORAGE_PREFIX}.acts.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'contract_detail_acts_table',
    columns: ACT_TABLE_COLUMNS.map(({ key, defaultVisible, defaultWidth }) => ({
      key,
      defaultVisible,
      defaultWidth,
    })),
    defaultPageSize: PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 110,
  });

  const {
    columnOrder: paymentColumnOrder,
    orderedVisibleColumns: paymentOrderedVisibleColumns,
    visibleColumns: paymentVisibleColumns,
    pageSize: paymentPageSize,
    setPageSize: setPaymentPageSize,
    draggingColumn: draggingPaymentColumn,
    toggleColumn: togglePaymentColumn,
    moveColumn: movePaymentColumn,
    handleColumnDragStart: handlePaymentColumnDragStart,
    handleColumnDragOver: handlePaymentColumnDragOver,
    handleColumnDrop: handlePaymentColumnDrop,
    handleColumnDragEnd: handlePaymentColumnDragEnd,
    handleResizeStart: handlePaymentResizeStart,
    getColumnWidth: getPaymentColumnWidth,
    getTableWidth: getPaymentTableWidth,
    resetTableSettings: resetPaymentTableSettings,
  } = usePersistedTableSettings<PaymentTableColumnKey>({
    storageKey: `${TABLE_STORAGE_PREFIX}.payments.${user?.id ?? 'guest'}.v1`,
    settingsKey: 'contract_detail_payments_table',
    columns: PAYMENT_TABLE_COLUMNS.map(({ key, defaultVisible, defaultWidth }) => ({
      key,
      defaultVisible,
      defaultWidth,
    })),
    defaultPageSize: PAGE_SIZE_OPTIONS[0],
    minColumnWidth: 110,
  });

  const [invoicePage, setInvoicePage] = useState(1);
  const [invoiceState, setInvoiceState] = useState<TabListState<Invoice>>(() => createTabListState());
  const [invoiceScope, setInvoiceScope] = useState<RelatedDocumentsScope>('contract');

  const [actPage, setActPage] = useState(1);
  const [actState, setActState] = useState<TabListState<Act>>(() => createTabListState());
  const [actScope, setActScope] = useState<RelatedDocumentsScope>('contract');

  const [paymentPage, setPaymentPage] = useState(1);
  const [paymentState, setPaymentState] = useState<TabListState<Payment>>(() => createTabListState());

  useEffect(() => {
    let cancel = false;

    const load = async () => {
      setLoading(true);
      try {
        const [nextContract, nextRules] = await Promise.all([
          apiService.getContract(contractId),
          apiService.getContractRulesSettings(),
        ]);

        if (cancel) return;

        setContract(nextContract);
        setContractRules(nextRules);
        setAmountInput(formatAmountInput(nextContract.billingMode === 'Fixed' ? nextContract.amount : null));
      } finally {
        if (!cancel) {
          setLoading(false);
        }
      }
    };

    void load();
    return () => {
      cancel = true;
    };
  }, [contractId]);

  const contractClientIds = useMemo(
    () => (contract?.clients ?? []).map((item) => item.id).filter((id) => Number.isFinite(id)),
    [contract],
  );

  const contractClientIdsQuery = useMemo(
    () => contractClientIds.join(','),
    [contractClientIds],
  );

  useEffect(() => {
    setInvoicePage(1);
    setActPage(1);
    setPaymentPage(1);
  }, [contract?.id, contractClientIdsQuery]);

  useEffect(() => {
    setSettingsOpen(false);
  }, [activeTab]);

  useEffect(() => {
    if (!visibleTabs.includes(activeTab)) {
      setActiveTab(visibleTabs[0] ?? 'clients');
    }
  }, [activeTab, visibleTabs]);

  useEffect(() => {
    setInvoicePage(1);
  }, [invoicePageSize]);

  useEffect(() => {
    setInvoicePage(1);
  }, [invoiceScope]);

  useEffect(() => {
    setActPage(1);
  }, [actPageSize]);

  useEffect(() => {
    setActPage(1);
  }, [actScope]);

  useEffect(() => {
    setPaymentPage(1);
  }, [paymentPageSize]);

  useEffect(() => {
    if (activeTab !== 'invoices' || !canViewInvoices) {
      setInvoiceState(createTabListState());
      return;
    }

    const contractId = contract?.id ?? null;
    if (invoiceScope === 'contract' ? !contractId : !contractClientIdsQuery) {
      setInvoiceState(createTabListState());
      return;
    }

    let cancel = false;

    const loadInvoices = async () => {
      setInvoiceState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const response = await apiService.getInvoices({
          ...(invoiceScope === 'contract'
            ? { contractId: contractId! }
            : { clientIds: contractClientIdsQuery }),
          page: invoicePage,
          pageSize: invoicePageSize,
          sortBy: 'date',
          sortDir: 'desc',
        });

        if (cancel) return;

        setInvoiceState({
          items: response.items ?? [],
          total: response.total ?? 0,
          loading: false,
          error: null,
        });
      } catch (error) {
        if (cancel) return;

        setInvoiceState({
          items: [],
          total: 0,
          loading: false,
          error: extractDisplayErrorMessage(error, 'Не удалось загрузить счета'),
        });
      }
    };

    void loadInvoices();
    return () => {
      cancel = true;
    };
  }, [activeTab, canViewInvoices, contract?.id, contractClientIdsQuery, invoicePage, invoicePageSize, invoiceScope]);

  useEffect(() => {
    if (activeTab !== 'acts' || !canViewActs) {
      setActState(createTabListState());
      return;
    }

    const contractId = contract?.id ?? null;
    if (actScope === 'contract' ? !contractId : !contractClientIdsQuery) {
      setActState(createTabListState());
      return;
    }

    let cancel = false;

    const loadActs = async () => {
      setActState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const response = await apiService.getActs({
          ...(actScope === 'contract'
            ? { contractId: contractId! }
            : { clientIds: contractClientIdsQuery }),
          page: actPage,
          pageSize: actPageSize,
          sortBy: 'date',
          sortDir: 'desc',
        });

        if (cancel) return;

        setActState({
          items: response.items ?? [],
          total: response.total ?? 0,
          loading: false,
          error: null,
        });
      } catch (error) {
        if (cancel) return;

        setActState({
          items: [],
          total: 0,
          loading: false,
          error: extractDisplayErrorMessage(error, 'Не удалось загрузить акты'),
        });
      }
    };

    void loadActs();
    return () => {
      cancel = true;
    };
  }, [activeTab, actPage, actPageSize, actScope, canViewActs, contract?.id, contractClientIdsQuery]);

  useEffect(() => {
    if (activeTab !== 'money' || !canViewPayments) {
      setPaymentState(createTabListState());
      return;
    }

    if (!contract?.id) {
      setPaymentState(createTabListState());
      return;
    }

    let cancel = false;

    const loadPayments = async () => {
      setPaymentState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const response = await apiService.getPaymentsV2({
          contractId: contract.id,
          excludeInvoiceDocuments: true,
          page: paymentPage,
          pageSize: paymentPageSize,
          sortBy: 'date',
          sortDir: 'desc',
        });

        if (cancel) return;

        setPaymentState({
          items: response.items ?? [],
          total: response.total ?? 0,
          loading: false,
          error: null,
        });
      } catch (error) {
        if (cancel) return;

        setPaymentState({
          items: [],
          total: 0,
          loading: false,
          error: extractDisplayErrorMessage(error, 'Не удалось загрузить платежи'),
        });
      }
    };

    void loadPayments();
    return () => {
      cancel = true;
    };
  }, [activeTab, canViewPayments, contract?.id, paymentPage, paymentPageSize]);

  const selectedPickerValue = useMemo<CounterpartySelection[]>(
    () => [
      ...(contract?.clients ?? []).map((client) => ({
        kind: 'client' as const,
        id: client.id,
        name: client.name,
        subtitle: client.company ?? null,
      })),
      ...(contract?.legalEntities ?? []).map((entity) => ({
        kind: 'legalEntity' as const,
        id: entity.id,
        name: getContractLegalEntityName(entity),
        subtitle:
          [entity.inn ? `ИНН ${entity.inn}` : '', entity.kpp ? `КПП ${entity.kpp}` : '']
            .filter(Boolean)
            .join(' · ') ||
          entity.fullName ||
          null,
      })),
    ],
    [contract?.clients, contract?.legalEntities],
  );

  const invoiceTotalPages = Math.max(1, Math.ceil(invoiceState.total / invoicePageSize || 1));
  const actTotalPages = Math.max(1, Math.ceil(actState.total / actPageSize || 1));
  const paymentTotalPages = Math.max(1, Math.ceil(paymentState.total / paymentPageSize || 1));

  const visibleClientColumns = useMemo(
    () => mapVisibleColumns(CLIENT_TABLE_COLUMNS, clientOrderedVisibleColumns),
    [clientOrderedVisibleColumns],
  );
  const visibleInvoiceColumns = useMemo(
    () => mapVisibleColumns(INVOICE_TABLE_COLUMNS, invoiceOrderedVisibleColumns),
    [invoiceOrderedVisibleColumns],
  );
  const visibleActColumns = useMemo(
    () => mapVisibleColumns(ACT_TABLE_COLUMNS, actOrderedVisibleColumns),
    [actOrderedVisibleColumns],
  );
  const visiblePaymentColumns = useMemo(
    () => mapVisibleColumns(PAYMENT_TABLE_COLUMNS, paymentOrderedVisibleColumns),
    [paymentOrderedVisibleColumns],
  );

  const save = async () => {
    if (!contract) return;

    const parsedAmount = parseAmountInput(amountInput);
    if (parsedAmount === undefined) {
      toast.error('Введите корректную сумму договора', 'Договоры');
      return;
    }

    if (contract.billingMode === 'Fixed' && parsedAmount === null) {
      toast.error('Укажите сумму договора', 'Договоры');
      return;
    }

    const payload: ContractInput = {
      number: contract.number,
      date: contract.date,
      startDate: contract.startDate,
      title: contract.title,
      billingMode: contract.billingMode,
      status: contract.status ?? 'Draft',
      isOpenEnded: contract.isOpenEnded,
      description: contract.description,
      amount: contract.billingMode === 'Fixed' ? (parsedAmount ?? undefined) : undefined,
      validUntil: contract.isOpenEnded ? undefined : contract.validUntil,
      terminationDate: contract.terminationDate,
      terminationReason: contract.terminationReason,
      terminationTerms: contract.terminationTerms,
      terminationFinancialTerms: contract.terminationFinancialTerms,
      cancelledAt: contract.cancelledAt,
      cancelledReason: contract.cancelledReason,
      clients: (contract.clients ?? []).map((client) => ({
        clientId: client.id,
        comment: client.comment ?? undefined,
      })),
      legalEntities: (contract.legalEntities ?? []).map((entity) => ({
        legalEntityId: entity.id,
        comment: entity.comment ?? undefined,
      })),
      actIds: contract.actIds ?? [],
      invoiceIds: contract.invoiceIds ?? [],
      paymentIds: contract.paymentIds ?? [],
    };

    setSaving(true);
    try {
      const updatedContract = await apiService.updateContract(contract.id, payload);
      setContract(updatedContract);
      setAmountInput(formatAmountInput(updatedContract.billingMode === 'Fixed' ? updatedContract.amount : null));
      setEditMode(false);
      toast.success('Договор сохранён.', 'Договоры');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить договор'), 'Договоры');
    } finally {
      setSaving(false);
    }
  };

  const toggleId = (key: 'actIds' | 'invoiceIds' | 'paymentIds', id: number) => {
    if (!editMode) return;

    setContract((prev) => {
      if (!prev) return prev;

      const current = new Set(prev[key] ?? []);
      if (current.has(id)) {
        current.delete(id);
      } else {
        current.add(id);
      }

      return { ...prev, [key]: [...current] };
    });
  };

  const handleApplyCounterparties = (selection: CounterpartySelection[]) => {
    setContract((prev) => {
      if (!prev) return prev;

      const previousClientComments = new Map((prev.clients ?? []).map((client) => [client.id, client.comment ?? '']));
      const previousLegalEntityComments = new Map(
        (prev.legalEntities ?? []).map((entity) => [entity.id, entity.comment ?? '']),
      );

      return {
        ...prev,
        clients: selection
          .filter((item) => item.kind === 'client')
          .map((item) => ({
            id: item.id,
            name: item.client?.name ?? item.name,
            company: item.client?.company ?? item.subtitle ?? null,
            clientStatus: item.client?.clientStatus ?? null,
            clientStatusId: item.client?.clientStatusId ?? null,
            comment: previousClientComments.get(item.id) ?? '',
          })),
        legalEntities: selection
          .filter((item) => item.kind === 'legalEntity')
          .map((item) => ({
            id: item.id,
            shortName: item.legalEntity?.shortName ?? item.name,
            fullName: item.legalEntity?.fullName ?? item.subtitle ?? null,
            inn: item.legalEntity?.inn ?? null,
            kpp: item.legalEntity?.kpp ?? null,
            comment: previousLegalEntityComments.get(item.id) ?? '',
          })),
      };
    });
  };

  const runWorkflowAction = async (action: 'markNotSigned' | 'sendToSigning' | 'markConcluded' | 'terminate' | 'cancel') => {
    if (!contract || workflowSaving) return;

    let terminatePayload:
      | { terminationDate: string; terms: string; reason?: string | null; financialTerms?: string | null }
      | null = null;
    let cancelReason: string | null = null;

    if (action === 'terminate') {
      const terminationDate = (await prompt({
        title: 'Дата расторжения договора',
        label: 'Дата расторжения',
        defaultValue: todayYMD(),
        inputType: 'date',
        confirmLabel: 'Продолжить',
        cancelLabel: 'Отмена',
      }))?.trim();
      if (!terminationDate) return;

      const terms = (await prompt({
        title: 'Условия расторжения договора',
        label: 'Условия расторжения',
        multiline: true,
        confirmLabel: 'Продолжить',
        cancelLabel: 'Отмена',
      }))?.trim();
      if (!terms) {
        toast.error('Условия расторжения обязательны.', 'Договоры');
        return;
      }

      const reason = (await prompt({
        title: 'Причина расторжения договора',
        label: requiresFinancialActionReason ? 'Причина расторжения' : 'Причина расторжения (необязательно)',
        multiline: true,
        confirmLabel: 'Продолжить',
        cancelLabel: 'Отмена',
      }))?.trim();
      if (requiresFinancialActionReason && !reason) {
        toast.error('Причина расторжения обязательна и будет сохранена в журнале.', 'Договоры');
        return;
      }

      let financialTerms: string | null = null;
      try {
        const terminationCheck = await apiService.getContractTerminationCheck(contract.id);
        if (terminationCheck.hasUnpaidInvoices) {
          const proceed = await confirm({
            title: 'Продолжить расторжение?',
            description: `По договору есть неоплаченные счета: ${terminationCheck.unpaidInvoiceCount} шт. на сумму ${formatAmount(terminationCheck.unpaidInvoiceAmount)} ₽.`,
            confirmLabel: 'Продолжить',
            cancelLabel: 'Отмена',
            variant: 'danger',
          });
          if (!proceed) return;

          financialTerms = (await prompt({
            title: 'Финансовые условия',
            label: 'Финансовые условия по неоплаченным счетам',
            multiline: true,
            confirmLabel: 'Продолжить',
            cancelLabel: 'Отмена',
          }))?.trim() || null;
          if (!financialTerms) {
            toast.error('Заполните финансовые условия, чтобы расторгнуть договор с неоплаченными счетами.', 'Договоры');
            return;
          }
        } else {
          financialTerms = (await prompt({
            title: 'Финансовые условия',
            label: 'Финансовые условия (необязательно)',
            multiline: true,
            confirmLabel: 'Продолжить',
            cancelLabel: 'Отмена',
          }))?.trim() || null;
        }
      } catch (error) {
        toast.error(extractDisplayErrorMessage(error, 'Не удалось проверить неоплаченные счета договора'), 'Договоры');
        return;
      }

      terminatePayload = {
        terminationDate,
        terms,
        reason: reason || null,
        financialTerms,
      };
    }

    if (action === 'cancel') {
      const reason = (await prompt({
        title: 'Аннулирование договора',
        label: 'Причина аннулирования',
        multiline: true,
        confirmLabel: 'Аннулировать',
        cancelLabel: 'Отмена',
      }))?.trim();
      if (!reason) {
        toast.error('Причина аннулирования обязательна.', 'Договоры');
        return;
      }

      cancelReason = reason;
    }

    setWorkflowSaving(true);
    try {
      let updatedContract: Contract;
      let successMessage: string;
      const requestStatusReason = async (label: string) => {
        if (!requiresFinancialActionReason) return null;

        const reason = (await prompt({
          title: 'Причина изменения',
          label: `Укажите причину: ${label}`,
          multiline: true,
          confirmLabel: 'Продолжить',
          cancelLabel: 'Отмена',
        }))?.trim();
        if (!reason) {
          toast.error('Причина обязательна и будет сохранена в журнале.', 'Договоры');
          return undefined;
        }

        return reason;
      };

      if (action === 'sendToSigning') {
        const reason = await requestStatusReason('смена статуса договора');
        if (reason === undefined) return;
        updatedContract = await apiService.updateContractStatus(contract.id, 'OnSigning', reason);
        successMessage = 'Договор отправлен на подписание.';
      } else if (action === 'markNotSigned') {
        const reason = await requestStatusReason('смена статуса договора');
        if (reason === undefined) return;
        updatedContract = await apiService.updateContractStatus(contract.id, 'NotSigned', reason);
        successMessage = 'Договор отмечен неподписанным.';
      } else if (action === 'markConcluded') {
        const reason = await requestStatusReason('смена статуса договора');
        if (reason === undefined) return;
        updatedContract = await apiService.updateContractStatus(contract.id, 'Concluded', reason);
        successMessage = 'Договор отмечен подписанным.';
      } else if (action === 'terminate' && terminatePayload) {
        updatedContract = await apiService.terminateContract(
          contract.id,
          terminatePayload.terminationDate,
          terminatePayload.terms,
          terminatePayload.reason,
          terminatePayload.financialTerms,
        );
        successMessage = 'Договор расторгнут.';
      } else if (action === 'cancel' && cancelReason) {
        updatedContract = await apiService.cancelContract(contract.id, cancelReason);
        successMessage = 'Договор аннулирован.';
      } else {
        return;
      }

      setContract(updatedContract);
      setAmountInput(formatAmountInput(updatedContract.billingMode === 'Fixed' ? updatedContract.amount : null));
      toast.success(successMessage, 'Статус договора обновлён');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось обновить статус договора'), 'Договоры');
    } finally {
      setWorkflowSaving(false);
    }
  };

  const saveContractDocumentBlob = (blob: Blob, format: 'word' | 'pdf', fileBase = 'contract') => {
    const extension = format === 'pdf' ? 'pdf' : 'doc';
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `${fileBase}-${contract?.number ?? contractId}.${extension}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  };

  const openContractDocumentPreview = async () => {
    if (!contract || documentSaving) return;

    setDocumentSaving(true);
    try {
      const blob = await apiService.downloadContractDocument(contract.id, 'html');
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank', 'noopener,noreferrer');
      window.setTimeout(() => URL.revokeObjectURL(url), 30_000);
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось открыть печатную форму договора'), 'Договоры');
    } finally {
      setDocumentSaving(false);
    }
  };

  const downloadTerminationAgreement = async (format: 'word' | 'pdf') => {
    if (!contract || documentSaving) return;

    setDocumentSaving(true);
    try {
      const blob = await apiService.downloadContractTerminationAgreement(contract.id, format);
      saveContractDocumentBlob(blob, format, 'contract-termination');
      toast.success(
        format === 'pdf' ? 'PDF соглашения о расторжении подготовлен.' : 'Соглашение о расторжении подготовлено.',
        'Договоры',
      );
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сформировать соглашение о расторжении'), 'Договоры');
    } finally {
      setDocumentSaving(false);
    }
  };

  const downloadContractDocument = async (format: 'word' | 'pdf') => {
    if (!contract || documentSaving) return;

    setDocumentSaving(true);
    try {
      const blob = await apiService.downloadContractDocument(contract.id, format);
      saveContractDocumentBlob(blob, format);
      toast.success(format === 'pdf' ? 'PDF договора подготовлен.' : 'Документ договора подготовлен.', 'Договоры');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сформировать договор'), 'Договоры');
    } finally {
      setDocumentSaving(false);
    }
  };

  const sendContractByEmail = async () => {
    if (!contract || documentSaving) return;

    const email = (await prompt({
      title: 'Отправить договор на email',
      label: 'Email получателя',
      inputType: 'email',
      confirmLabel: 'Отправить',
      cancelLabel: 'Отмена',
    }))?.trim();
    if (!email) return;

    setDocumentSaving(true);
    try {
      await apiService.sendContractByEmail(contract.id, { email, format: 'pdf' });
      toast.success('Договор отправлен на email.', 'Договоры');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось отправить договор'), 'Договоры');
    } finally {
      setDocumentSaving(false);
    }
  };

  const uploadContractToYandexDisk = async () => {
    if (!contract || documentSaving) return;

    setDocumentSaving(true);
    try {
      const result = await apiService.uploadContractDocumentToYandexDisk(contract.id, 'pdf');
      toast.success(`Договор отправлен на Яндекс.Диск: ${result.path}.`, 'Я.Диск');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось отправить договор на Яндекс.Диск'), 'Я.Диск');
    } finally {
      setDocumentSaving(false);
    }
  };

  const executionMetrics = getContractExecutionMetrics(contract ?? {
    billingMode: 'Fixed',
    amount: null,
    invoiceAmountTotal: 0,
  } as Contract);
  const effectiveAmount = getContractEffectiveAmount(contract ?? {
    billingMode: 'Fixed',
    amount: null,
    invoiceAmountTotal: 0,
  } as Contract);
  const reconciliationWarnings = getContractReconciliationWarnings(contract ?? {
    billingMode: 'Fixed',
    amount: null,
    invoiceAmountTotal: 0,
    actAmountTotal: 0,
  } as Contract);
  const isTerminated = contract?.status === 'Terminated';
  const isCancelled = contract?.status === 'Cancelled';
  const canCreateTerminationAgreement = Boolean(isTerminated && contract?.terminationDate && contract?.terminationTerms);
  const notSignedDisabled = workflowSaving || editMode || contract?.status === 'NotSigned' || isTerminated || isCancelled;
  const workflowDisabled = workflowSaving || editMode || isTerminated || isCancelled;
  const terminateDisabled = workflowSaving || editMode || isTerminated || isCancelled;
  const cancelDisabled =
    workflowSaving ||
    editMode ||
    !contract?.status ||
    !cancellableContractStatuses.includes(contract.status);

  if (loading || !contract) {
    return (
      <div className="p-8">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    );
  }

  return (
    <div className="p-6">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
        <button onClick={onBack} className="inline-flex items-center gap-2 rounded border px-3 py-2">
          <ArrowLeft className="h-4 w-4" />
          Назад
        </button>

        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => void openContractDocumentPreview()}
            disabled={documentSaving}
            className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm disabled:opacity-60"
          >
            {documentSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Printer className="h-4 w-4" />}
            Печать
          </button>
          <button
            type="button"
            onClick={() => void downloadContractDocument('word')}
            disabled={documentSaving}
            className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm disabled:opacity-60"
          >
            {documentSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
            Word
          </button>
          <button
            type="button"
            onClick={() => void downloadContractDocument('pdf')}
            disabled={documentSaving}
            className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm disabled:opacity-60"
          >
            {documentSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
            PDF
          </button>
          {canCreateTerminationAgreement && (
            <button
              type="button"
              onClick={() => void downloadTerminationAgreement('pdf')}
              disabled={documentSaving}
              className="inline-flex items-center gap-2 rounded border border-orange-200 px-3 py-2 text-sm text-orange-700 disabled:opacity-60"
            >
              {documentSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileText className="h-4 w-4" />}
              Соглашение PDF
            </button>
          )}
          <button
            type="button"
            onClick={() => void sendContractByEmail()}
            disabled={documentSaving}
            className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm disabled:opacity-60"
          >
            {documentSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Mail className="h-4 w-4" />}
            Email
          </button>
          {isAdmin && contract.clients.length > 0 ? (
            <button
              type="button"
              onClick={() => setMessengerDeliveryOpen(true)}
              disabled={documentSaving}
              className="inline-flex items-center gap-2 rounded border border-violet-200 px-3 py-2 text-sm text-violet-700 disabled:opacity-60"
            >
              <Send className="h-4 w-4" />
              Мессенджер
            </button>
          ) : null}
          {isAdmin && contract.clients.length > 0 ? (
            <button
              type="button"
              onClick={() => setElectronicSignatureOpen(true)}
              disabled={documentSaving}
              className="inline-flex items-center gap-2 rounded border border-blue-200 px-3 py-2 text-sm text-blue-700 disabled:opacity-60"
            >
              <FileText className="h-4 w-4" />
              Э-подпись
            </button>
          ) : null}
          <button
            type="button"
            onClick={() => void uploadContractToYandexDisk()}
            disabled={documentSaving}
            className="inline-flex items-center gap-2 rounded border border-sky-200 px-3 py-2 text-sm text-sky-700 disabled:opacity-60"
          >
            {documentSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
            Я.Диск
          </button>
          <button
            type="button"
            onClick={() => void runWorkflowAction('markNotSigned')}
            disabled={notSignedDisabled}
            className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm disabled:opacity-60"
          >
            {workflowSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Ban className="h-4 w-4" />}
            Не подписан
          </button>
          <button
            type="button"
            onClick={() => void runWorkflowAction('sendToSigning')}
            disabled={workflowDisabled}
            className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm disabled:opacity-60"
          >
            {workflowSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
            На подписание
          </button>
          <button
            type="button"
            onClick={() => void runWorkflowAction('markConcluded')}
            disabled={workflowDisabled}
            className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-2 text-sm disabled:opacity-60"
          >
            {workflowSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
            Подписан клиентом
          </button>
          <button
            type="button"
            onClick={() => void runWorkflowAction('terminate')}
            disabled={terminateDisabled}
            className="inline-flex items-center gap-2 rounded border border-orange-200 px-3 py-2 text-sm text-orange-700 disabled:opacity-60"
          >
            {workflowSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileX className="h-4 w-4" />}
            Расторгнуть
          </button>
          <button
            type="button"
            onClick={() => void runWorkflowAction('cancel')}
            disabled={cancelDisabled}
            className="inline-flex items-center gap-2 rounded border border-rose-200 px-3 py-2 text-sm text-rose-700 disabled:opacity-60"
          >
            {workflowSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Ban className="h-4 w-4" />}
            Аннулировать
          </button>
          {!editMode ? (
            <button
              onClick={() => setEditMode(true)}
              className="inline-flex items-center gap-2 rounded border px-3 py-2"
            >
              <Pencil className="h-4 w-4" />
              Редактировать
            </button>
          ) : (
            <button onClick={() => setEditMode(false)} className="rounded border px-3 py-2">
              Отменить редактирование
            </button>
          )}

          <button
            onClick={() => void save()}
            disabled={saving || !editMode}
            className="inline-flex items-center gap-2 rounded bg-emerald-600 px-4 py-2 text-white disabled:opacity-60"
          >
            {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Сохранить
          </button>
        </div>
      </div>

      <div className="mb-4">
        <ObjectAuditLauncher
          objectType="contract"
          objectId={contract.id}
          createdAt={contract.createdAt}
          createdByUserName={contract.auditCreatedByUserName}
          updatedAt={contract.auditUpdatedAt}
          updatedByUserName={contract.auditUpdatedByUserName}
          isDeleted={contract.auditIsDeleted}
          deletedAt={contract.auditDeletedAt}
          deletedByUserName={contract.auditDeletedByUserName}
          triggerTitle="Аудит договора"
          triggerDescription="Паспорт договора и история изменений открываются в отдельном окне."
          title="История договора"
        />
      </div>

      <div className="mb-4 grid grid-cols-1 gap-3 rounded border bg-white p-4 md:grid-cols-2 lg:grid-cols-4">
        <label className="text-sm">
          Номер договора
          <input
            value={contract.number}
            disabled={!editMode || !contractRules.allowManualNumberEdit}
            onChange={(event) =>
              setContract((prev) => (prev ? { ...prev, number: event.target.value } : prev))
            }
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>

        <label className="text-sm">
          Дата договора
          <SystemDateInput
            type="date"
            value={toDateInputValue(contract.date) ?? ''}
            disabled={!editMode}
            onChange={(event) =>
              setContract((prev) =>
                prev ? { ...prev, date: fromInputToApiDate(event.target.value) ?? prev.date } : prev,
              )
            }
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>

        <label className="text-sm">
          Дата начала действия
          <SystemDateInput
            type="date"
            value={toDateInputValue(contract.startDate) ?? ''}
            disabled={!editMode}
            onChange={(event) =>
              setContract((prev) =>
                prev ? { ...prev, startDate: fromInputToApiDate(event.target.value) } : prev,
              )
            }
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>

        <label className="text-sm">
          Режим договора
          <SystemSelect
            value={contract.billingMode}
            disabled={!editMode}
            onChange={(event) => {
              if (event.target.value === 'Actual') {
                setAmountInput('');
              }

              setContract((prev) =>
                prev
                  ? {
                    ...prev,
                    billingMode: event.target.value as Contract['billingMode'],
                    amount: event.target.value === 'Actual' ? null : prev.amount,
                  }
                  : prev,
              );
            }}
            className="mt-1 w-full rounded border px-2 py-1"
          >
            <option value="Fixed">Фиксированная сумма</option>
            <option value="Actual">Оплата по факту</option>
          </SystemSelect>
        </label>

        <label className="text-sm">
          Действует до
          <SystemDateInput
            type="date"
            value={toDateInputValue(contract.validUntil) ?? ''}
            disabled={!editMode || contract.isOpenEnded}
            onChange={(event) =>
              setContract((prev) =>
                prev ? { ...prev, validUntil: fromInputToApiDate(event.target.value) } : prev,
              )
            }
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>

        <label className="text-sm">
          Дата расторжения
          <SystemDateInput
            type="date"
            value={toDateInputValue(contract.terminationDate) ?? ''}
            disabled={!editMode}
            onChange={(event) =>
              setContract((prev) =>
                prev ? { ...prev, terminationDate: fromInputToApiDate(event.target.value) } : prev,
              )
            }
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>

        <label className="text-sm">
          Сумма договора
          <input
            value={amountInput}
            disabled={!editMode || contract.billingMode === 'Actual'}
            onChange={(event) => {
              const nextValue = event.target.value;
              setAmountInput(nextValue);

              const parsedAmount = parseAmountInput(nextValue);
              if (parsedAmount !== undefined) {
                setContract((prev) => (prev ? { ...prev, amount: parsedAmount } : prev));
              }
            }}
            className="mt-1 w-full rounded border px-2 py-1"
            placeholder="0,00"
          />
          {contract.billingMode === 'Actual' ? (
            <div className="mt-1 text-xs text-slate-500">Сумма считается по привязанным счетам.</div>
          ) : null}
        </label>

        <label className="flex items-center gap-3 rounded border px-3 py-2 text-sm md:col-span-2 lg:col-span-1">
          <SystemCheckbox
            type="checkbox"
            checked={contract.isOpenEnded}
            disabled={!editMode}
            onChange={(event) =>
              setContract((prev) =>
                prev
                  ? {
                    ...prev,
                    isOpenEnded: event.target.checked,
                    validUntil: event.target.checked ? null : prev.validUntil,
                  }
                  : prev,
              )
            }
            className="h-4 w-4 rounded border-slate-300 text-emerald-600 focus:ring-emerald-500"
          />
          <span>До исполнения обязательств</span>
        </label>

        <label className="text-sm md:col-span-2 lg:col-span-3">
          Описание
          <input
            value={contract.description ?? ''}
            disabled={!editMode}
            onChange={(event) =>
              setContract((prev) => (prev ? { ...prev, description: event.target.value } : prev))
            }
            className="mt-1 w-full rounded border px-2 py-1"
          />
        </label>
      </div>

      <div className="mb-4 grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-6">
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма договора</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(effectiveAmount)}</div>
          <div className="mt-1 text-xs text-slate-500">{getContractBillingModeLabel(contract.billingMode)}</div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма счетов в договоре</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(contract.invoiceAmountTotal)}</div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма актов</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(contract.actAmountTotal)}</div>
          <div className="mt-1 text-xs text-slate-500">Ручные акты и акты по счетам договора</div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма платежей</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(contract.paymentAmountTotal)}</div>
          <div className="mt-1 text-xs text-slate-500">По платежам, связанным с договором</div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Исполнение</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{executionMetrics.label}</div>
          <div className="mt-1 text-xs text-slate-500">
            {executionMetrics.percent !== null ? `${executionMetrics.percent}%` : 'Сумма считается по счетам в договоре'}
            {executionMetrics.delta !== null ? ` · Остаток ${formatAmount(executionMetrics.delta)}` : ''}
          </div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Статус документа</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">
            {contractStatusLabels[contract.status ?? 'Draft']}
          </div>
          <div className="mt-1 text-xs text-slate-500">
            Подписание: {formatDate(contract.sentForSigningAt)}
            {contract.concludedAt ? ` · заключён ${formatDate(contract.concludedAt)}` : ''}
            {contract.terminationDate ? ` · расторгнут ${formatDate(contract.terminationDate)}` : ''}
            {contract.cancelledAt ? ` · аннулирован ${formatDate(contract.cancelledAt)}` : ''}
            {contract.terminationTerms ? ` · ${contract.terminationTerms}` : ''}
            {contract.cancelledReason ? ` · ${contract.cancelledReason}` : ''}
          </div>
          <div className="mt-2 text-sm">
            <DocumentSellerPicker
              kind="contract"
              documentId={contract.id}
              companyId={contract.sellerCompanyId}
              branchId={contract.sellerBranchId}
              frozen={contract.sellerFrozen}
              snapshotInfo={contract.sellerSnapshotInfo}
              canEdit={rolePermissions.contracts.canEdit}
            />
          </div>
        </div>
      </div>

      {reconciliationWarnings.length > 0 ? (
        <div className="mb-4 flex gap-3 rounded border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <div className="space-y-1">
            {reconciliationWarnings.map((warning) => (
              <div key={warning.kind}>
                {warning.label}: {formatAmount(warning.amount)} · разница {formatAmount(warning.difference)}
              </div>
            ))}
          </div>
        </div>
      ) : null}

      <div className="mb-3 flex gap-2">
        {visibleTabs.map((tab) => (
          <button
            key={tab}
            onClick={() => setActiveTab(tab)}
            className={`rounded px-3 py-1 ${activeTab === tab ? 'bg-emerald-600 text-white' : 'border'}`}
          >
            {tabLabels[tab]}
          </button>
        ))}
      </div>

      {activeTab === 'clients' && (
        <div className="rounded border bg-white">
          <div className="flex items-center justify-between border-b px-4 py-3">
            <h3 className="font-semibold">Контрагенты договора</h3>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => setSettingsOpen((prev) => !prev)}
                className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-1.5 text-sm text-slate-700"
              >
                <Settings2 className="h-4 w-4" />
                {settingsOpen ? 'Скрыть настройки' : 'Настроить таблицу'}
              </button>
              <button
                onClick={() => {
                  if (canSelectCounterparties) {
                    setPickerOpen(true);
                  }
                }}
                disabled={!editMode || !canSelectCounterparties}
                className="inline-flex items-center gap-2 rounded border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 disabled:cursor-not-allowed disabled:opacity-60"
              >
                <Plus className="h-4 w-4" />
                Добавить контрагента
              </button>
            </div>
          </div>
          {settingsOpen ? (
            <div className="border-b border-slate-100 p-4">
              <TableColumnSettings
                columns={CLIENT_TABLE_COLUMNS.map((column) => ({
                  key: column.key,
                  label: column.label,
                }))}
                columnOrder={clientColumnOrder}
                visibleColumns={clientVisibleColumns}
                draggingColumn={draggingClientColumn}
                onToggleColumn={toggleClientColumn}
                onMoveColumn={moveClientColumn}
                onDragStart={handleClientColumnDragStart}
                onDragOver={handleClientColumnDragOver}
                onDrop={handleClientColumnDrop}
                onDragEnd={handleClientColumnDragEnd}
                onReset={resetClientTableSettings}
              />
            </div>
          ) : null}
          <div className="overflow-x-auto">
            <table
              className="min-w-full table-fixed divide-y text-sm"
              style={{ width: getClientTableWidth(110), minWidth: '100%' }}
            >
              <colgroup>
                {visibleClientColumns.map((column) => (
                  <col key={column.key} style={{ width: getClientColumnWidth(column.key) }} />
                ))}
                <col style={{ width: 110 }} />
              </colgroup>
              <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                <tr>
                  {visibleClientColumns.map((column) => (
                    <th
                      key={column.key}
                      style={{ width: getClientColumnWidth(column.key) }}
                      className={`relative px-3 py-2 text-left ${
                        draggingClientColumn === column.key ? 'bg-blue-50 text-blue-700' : ''
                      }`}
                      onDragOver={handleClientColumnDragOver}
                      onDrop={(event) => handleClientColumnDrop(column.key, event)}
                    >
                      <div
                        className="inline-flex min-w-0 select-none items-center gap-2"
                        draggable
                        onDragStart={(event) => handleClientColumnDragStart(column.key, event)}
                        onDragEnd={handleClientColumnDragEnd}
                        title="Перетащите, чтобы изменить порядок"
                      >
                        <GripVertical className="h-4 w-4 text-slate-400" />
                        <span className="truncate">{column.label}</span>
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${column.label}`}
                        className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                        onMouseDown={(event) => handleClientResizeStart(column.key, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-3 py-2 text-right">Действия</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {((contract.clients ?? []).length + (contract.legalEntities ?? []).length) === 0 ? (
                  <tr>
                    <td colSpan={visibleClientColumns.length + 1} className="px-3 py-6 text-center text-slate-500">
                      Контрагенты не найдены
                    </td>
                  </tr>
                ) : (
                  (contract.clients ?? []).map((client) => (
                    <tr key={client.id}>
                      {visibleClientColumns.map((column) => (
                        <td
                          key={column.key}
                          style={{ width: getClientColumnWidth(column.key) }}
                          className="max-w-0 overflow-hidden px-3 py-2 align-top"
                        >
                          {column.key === 'name' ? (
                            canViewClients && onOpenClient ? (
                            <button
                              className="text-blue-700 hover:underline"
                              onClick={() => onOpenClient(client.id)}
                            >
                              {client.name}
                            </button>
                            ) : (
                              <span>{client.name}</span>
                            )
                          ) : column.key === 'company' ? (
                            client.company || '—'
                          ) : (
                            <input
                              disabled={!editMode}
                              value={client.comment ?? ''}
                              onChange={(event) =>
                                setContract((prev) =>
                                  prev
                                    ? {
                                        ...prev,
                                        clients: prev.clients.map((item) =>
                                          item.id === client.id ? { ...item, comment: event.target.value } : item,
                                        ),
                                      }
                                    : prev,
                                )
                              }
                              className="w-full rounded border px-2 py-1"
                            />
                          )}
                        </td>
                      ))}
                      <td className="px-3 py-2 text-right">
                        <button
                          disabled={!editMode}
                          onClick={() =>
                            setContract((prev) =>
                              prev
                                ? { ...prev, clients: prev.clients.filter((item) => item.id !== client.id) }
                                : prev,
                            )
                          }
                          className="rounded border border-rose-200 p-1 text-rose-600 disabled:opacity-60"
                        >
                          <X className="h-4 w-4" />
                        </button>
                      </td>
                    </tr>
                  ))
                )}
                {(contract.legalEntities ?? []).map((entity) => (
                  <tr key={`legal:${entity.id}`}>
                    {visibleClientColumns.map((column) => (
                      <td
                        key={column.key}
                        style={{ width: getClientColumnWidth(column.key) }}
                        className="max-w-0 overflow-hidden px-3 py-2 align-top"
                      >
                        {column.key === 'name' ? (
                          <span className="font-medium text-slate-700">{entity.shortName || entity.fullName || `Юрлицо #${entity.id}`}</span>
                        ) : column.key === 'company' ? (
                          [entity.inn ? `ИНН ${entity.inn}` : '', entity.kpp ? `КПП ${entity.kpp}` : ''].filter(Boolean).join(', ') || entity.fullName || '—'
                        ) : (
                          <input
                            disabled={!editMode}
                            value={entity.comment ?? ''}
                            onChange={(event) =>
                              setContract((prev) =>
                                prev
                                  ? {
                                      ...prev,
                                      legalEntities: (prev.legalEntities ?? []).map((item) =>
                                        item.id === entity.id ? { ...item, comment: event.target.value } : item,
                                      ),
                                    }
                                  : prev,
                              )
                            }
                            className="w-full rounded border px-2 py-1"
                          />
                        )}
                      </td>
                    ))}
                    <td className="px-3 py-2 text-right">
                      <button
                        disabled={!editMode}
                        onClick={() =>
                          setContract((prev) =>
                            prev
                              ? { ...prev, legalEntities: (prev.legalEntities ?? []).filter((item) => item.id !== entity.id) }
                              : prev,
                          )
                        }
                        className="rounded border border-rose-200 p-1 text-rose-600 disabled:opacity-60"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {activeTab === 'invoices' && canViewInvoices && (
        <div className="rounded border bg-white">
          <div className="flex items-center justify-between border-b px-4 py-3">
            <div>
              <h3 className="font-semibold">
                {invoiceScope === 'contract' ? 'Счета в договоре' : 'Счета клиентов договора'}
              </h3>
              <p className="mt-1 text-sm text-slate-500">
                {invoiceScope === 'contract'
                  ? 'Сейчас показаны только счета, связанные с договором.'
                  : 'Сейчас показаны все счета клиентов договора. В верхнюю сумму входят только счета, связанные с договором.'}
              </p>
              <div className="mt-3 inline-flex rounded-lg border border-slate-200 bg-slate-50 p-1 text-sm">
                <button
                  type="button"
                  onClick={() => setInvoiceScope('contract')}
                  className={`rounded-md px-3 py-1.5 ${
                    invoiceScope === 'contract' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-600'
                  }`}
                >
                  Только в договоре
                </button>
                <button
                  type="button"
                  onClick={() => setInvoiceScope('clients')}
                  className={`rounded-md px-3 py-1.5 ${
                    invoiceScope === 'clients' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-600'
                  }`}
                >
                  Все счета клиентов
                </button>
              </div>
            </div>
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-1.5 text-sm text-slate-700"
            >
              <Settings2 className="h-4 w-4" />
              {settingsOpen ? 'Скрыть настройки' : 'Настроить таблицу'}
            </button>
          </div>
          {settingsOpen ? (
            <div className="border-b border-slate-100 p-4">
              <TableColumnSettings
                columns={INVOICE_TABLE_COLUMNS.map((column) => ({
                  key: column.key,
                  label: column.label,
                }))}
                columnOrder={invoiceColumnOrder}
                visibleColumns={invoiceVisibleColumns}
                draggingColumn={draggingInvoiceColumn}
                onToggleColumn={toggleInvoiceColumn}
                onMoveColumn={moveInvoiceColumn}
                onDragStart={handleInvoiceColumnDragStart}
                onDragOver={handleInvoiceColumnDragOver}
                onDrop={handleInvoiceColumnDrop}
                onDragEnd={handleInvoiceColumnDragEnd}
                onReset={resetInvoiceTableSettings}
                footer={
                  <div className="flex items-center gap-2 text-sm text-slate-600">
                    <span>Строк на странице</span>
                    <SystemSelect
                      value={invoicePageSize}
                      onChange={(event) => setInvoicePageSize(Number(event.target.value) || invoicePageSize)}
                      className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                    >
                      {PAGE_SIZE_OPTIONS.map((size) => (
                        <option key={size} value={size}>
                          {size}
                        </option>
                      ))}
                    </SystemSelect>
                  </div>
                }
              />
            </div>
          ) : null}
          <div className="overflow-x-auto">
            <table
              className="min-w-full table-fixed divide-y text-sm"
              style={{ width: getInvoiceTableWidth(120), minWidth: '100%' }}
            >
              <colgroup>
                {visibleInvoiceColumns.map((column) => (
                  <col key={column.key} style={{ width: getInvoiceColumnWidth(column.key) }} />
                ))}
                <col style={{ width: 120 }} />
              </colgroup>
              <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                <tr>
                  {visibleInvoiceColumns.map((column) => (
                    <th
                      key={column.key}
                      style={{ width: getInvoiceColumnWidth(column.key) }}
                      className={`relative px-3 py-2 ${
                        column.align === 'right' ? 'text-right' : 'text-left'
                      } ${draggingInvoiceColumn === column.key ? 'bg-blue-50 text-blue-700' : ''}`}
                      onDragOver={handleInvoiceColumnDragOver}
                      onDrop={(event) => handleInvoiceColumnDrop(column.key, event)}
                    >
                      <div
                        className={`inline-flex min-w-0 select-none items-center gap-2 ${
                          column.align === 'right' ? 'justify-end' : ''
                        }`}
                        draggable
                        onDragStart={(event) => handleInvoiceColumnDragStart(column.key, event)}
                        onDragEnd={handleInvoiceColumnDragEnd}
                        title="Перетащите, чтобы изменить порядок"
                      >
                        <GripVertical className="h-4 w-4 text-slate-400" />
                        <span className="truncate">{column.label}</span>
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${column.label}`}
                        className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                        onMouseDown={(event) => handleInvoiceResizeStart(column.key, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-3 py-2 text-right">В договоре</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {invoiceState.loading ? (
                  <tr>
                    <td colSpan={visibleInvoiceColumns.length + 1} className="px-3 py-8 text-center text-slate-500">
                      <span className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        Загрузка счетов...
                      </span>
                    </td>
                  </tr>
                ) : invoiceState.error ? (
                  <tr>
                    <td colSpan={visibleInvoiceColumns.length + 1} className="px-3 py-6 text-center text-rose-600">
                      {invoiceState.error}
                    </td>
                  </tr>
                ) : invoiceState.items.length === 0 ? (
                  <tr>
                    <td colSpan={visibleInvoiceColumns.length + 1} className="px-3 py-6 text-center text-slate-500">
                      Счета не найдены
                    </td>
                  </tr>
                ) : (
                  invoiceState.items.map((item) => (
                    <tr key={item.id}>
                      {visibleInvoiceColumns.map((column) => (
                        <td
                          key={column.key}
                          style={{ width: getInvoiceColumnWidth(column.key) }}
                          className={`max-w-0 overflow-hidden px-3 py-2 align-top ${
                            column.align === 'right' ? 'text-right' : ''
                          }`}
                        >
                          {column.key === 'number' ? (
                            <button
                              className="text-blue-700 hover:underline"
                              onClick={() => canViewInvoices && onOpenInvoiceEditor?.(item.id)}
                            >
                              {item.number}
                            </button>
                          ) : column.key === 'client' ? (
                            item.clientName || '—'
                          ) : column.key === 'date' ? (
                            formatDate(item.date)
                          ) : column.key === 'amount' ? (
                            formatAmount(item.amount)
                          ) : (
                            item.status
                          )}
                        </td>
                      ))}
                      <td className="px-3 py-2 text-right">
                        <SystemCheckbox
                          type="checkbox"
                          disabled={!editMode}
                          checked={(contract.invoiceIds ?? []).includes(item.id)}
                          onChange={() => toggleId('invoiceIds', item.id)}
                        />
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
          <TablePagination
            page={invoicePage}
            totalPages={invoiceTotalPages}
            totalItems={invoiceState.total}
            pageSize={invoicePageSize}
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            onPageChange={setInvoicePage}
            onPageSizeChange={setInvoicePageSize}
          />
        </div>
      )}

      {activeTab === 'acts' && canViewActs && (
        <div className="rounded border bg-white">
          <div className="flex items-center justify-between border-b px-4 py-3">
            <div>
              <h3 className="font-semibold">
                {actScope === 'contract' ? 'Акты в договоре' : 'Акты клиентов договора'}
              </h3>
              <p className="mt-1 text-sm text-slate-500">
                {actScope === 'contract'
                  ? 'Сейчас показаны акты, попавшие в договор напрямую или через счета договора.'
                  : 'Сейчас показаны все акты клиентов договора. Если акт связан со счетом договора, он считается включенным автоматически.'}
              </p>
              <div className="mt-3 inline-flex rounded-lg border border-slate-200 bg-slate-50 p-1 text-sm">
                <button
                  type="button"
                  onClick={() => setActScope('contract')}
                  className={`rounded-md px-3 py-1.5 ${
                    actScope === 'contract' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-600'
                  }`}
                >
                  Только в договоре
                </button>
                <button
                  type="button"
                  onClick={() => setActScope('clients')}
                  className={`rounded-md px-3 py-1.5 ${
                    actScope === 'clients' ? 'bg-white text-slate-900 shadow-sm' : 'text-slate-600'
                  }`}
                >
                  Все акты клиентов
                </button>
              </div>
            </div>
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-1.5 text-sm text-slate-700"
            >
              <Settings2 className="h-4 w-4" />
              {settingsOpen ? 'Скрыть настройки' : 'Настроить таблицу'}
            </button>
          </div>
          {settingsOpen ? (
            <div className="border-b border-slate-100 p-4">
              <TableColumnSettings
                columns={ACT_TABLE_COLUMNS.map((column) => ({
                  key: column.key,
                  label: column.label,
                }))}
                columnOrder={actColumnOrder}
                visibleColumns={actVisibleColumns}
                draggingColumn={draggingActColumn}
                onToggleColumn={toggleActColumn}
                onMoveColumn={moveActColumn}
                onDragStart={handleActColumnDragStart}
                onDragOver={handleActColumnDragOver}
                onDrop={handleActColumnDrop}
                onDragEnd={handleActColumnDragEnd}
                onReset={resetActTableSettings}
                footer={
                  <div className="flex items-center gap-2 text-sm text-slate-600">
                    <span>Строк на странице</span>
                    <SystemSelect
                      value={actPageSize}
                      onChange={(event) => setActPageSize(Number(event.target.value) || actPageSize)}
                      className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                    >
                      {PAGE_SIZE_OPTIONS.map((size) => (
                        <option key={size} value={size}>
                          {size}
                        </option>
                      ))}
                    </SystemSelect>
                  </div>
                }
              />
            </div>
          ) : null}
          <div className="overflow-x-auto">
            <table
              className="min-w-full table-fixed divide-y text-sm"
              style={{ width: getActTableWidth(120), minWidth: '100%' }}
            >
              <colgroup>
                {visibleActColumns.map((column) => (
                  <col key={column.key} style={{ width: getActColumnWidth(column.key) }} />
                ))}
                <col style={{ width: 120 }} />
              </colgroup>
              <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                <tr>
                  {visibleActColumns.map((column) => (
                    <th
                      key={column.key}
                      style={{ width: getActColumnWidth(column.key) }}
                      className={`relative px-3 py-2 ${
                        column.align === 'right' ? 'text-right' : 'text-left'
                      } ${draggingActColumn === column.key ? 'bg-blue-50 text-blue-700' : ''}`}
                      onDragOver={handleActColumnDragOver}
                      onDrop={(event) => handleActColumnDrop(column.key, event)}
                    >
                      <div
                        className={`inline-flex min-w-0 select-none items-center gap-2 ${
                          column.align === 'right' ? 'justify-end' : ''
                        }`}
                        draggable
                        onDragStart={(event) => handleActColumnDragStart(column.key, event)}
                        onDragEnd={handleActColumnDragEnd}
                        title="Перетащите, чтобы изменить порядок"
                      >
                        <GripVertical className="h-4 w-4 text-slate-400" />
                        <span className="truncate">{column.label}</span>
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${column.label}`}
                        className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                        onMouseDown={(event) => handleActResizeStart(column.key, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-3 py-2 text-right">В договоре</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {actState.loading ? (
                  <tr>
                    <td colSpan={visibleActColumns.length + 1} className="px-3 py-8 text-center text-slate-500">
                      <span className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        Загрузка актов...
                      </span>
                    </td>
                  </tr>
                ) : actState.error ? (
                  <tr>
                    <td colSpan={visibleActColumns.length + 1} className="px-3 py-6 text-center text-rose-600">
                      {actState.error}
                    </td>
                  </tr>
                ) : actState.items.length === 0 ? (
                  <tr>
                    <td colSpan={visibleActColumns.length + 1} className="px-3 py-6 text-center text-slate-500">
                      Акты не найдены
                    </td>
                  </tr>
                ) : (
                  actState.items.map((item) => (
                    <tr key={item.id}>
                      {visibleActColumns.map((column) => (
                        <td
                          key={column.key}
                          style={{ width: getActColumnWidth(column.key) }}
                          className={`max-w-0 overflow-hidden px-3 py-2 align-top ${
                            column.align === 'right' ? 'text-right' : ''
                          }`}
                        >
                          {column.key === 'number' ? (
                            <button
                              className="text-blue-700 hover:underline"
                              onClick={() => canViewActs && onOpenActEditor?.(item.id)}
                            >
                              {item.number}
                            </button>
                          ) : column.key === 'client' ? (
                            item.clientName || '—'
                          ) : column.key === 'date' ? (
                            formatDate(item.date)
                          ) : column.key === 'amount' ? (
                            formatAmount(item.amount)
                          ) : (
                            item.status
                          )}
                        </td>
                      ))}
                      <td className="px-3 py-2 text-right">
                        {(() => {
                          const linkedViaInvoice =
                            typeof item.invoiceId === 'number' && (contract.invoiceIds ?? []).includes(item.invoiceId);
                          const checked = linkedViaInvoice || (contract.actIds ?? []).includes(item.id);
                          return (
                            <SystemCheckbox
                              type="checkbox"
                              disabled={!editMode || linkedViaInvoice}
                              checked={checked}
                              onChange={() => toggleId('actIds', item.id)}
                            />
                          );
                        })()}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
          <TablePagination
            page={actPage}
            totalPages={actTotalPages}
            totalItems={actState.total}
            pageSize={actPageSize}
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            onPageChange={setActPage}
            onPageSizeChange={setActPageSize}
          />
        </div>
      )}

      {activeTab === 'money' && canViewPayments && (
        <div className="rounded border bg-white">
          <div className="flex items-center justify-between border-b px-4 py-3">
            <h3 className="font-semibold">Платежи договора</h3>
            <button
              type="button"
              onClick={() => setSettingsOpen((prev) => !prev)}
              className="inline-flex items-center gap-2 rounded border border-slate-200 px-3 py-1.5 text-sm text-slate-700"
            >
              <Settings2 className="h-4 w-4" />
              {settingsOpen ? 'Скрыть настройки' : 'Настроить таблицу'}
            </button>
          </div>
          <div className="border-b border-slate-100 px-4 py-2 text-sm text-slate-500">
            Здесь показаны только платежи, связанные с договором. Счета-документы остаются в отдельной вкладке счетов.
          </div>
          {settingsOpen ? (
            <div className="border-b border-slate-100 p-4">
              <TableColumnSettings
                columns={PAYMENT_TABLE_COLUMNS.map((column) => ({
                  key: column.key,
                  label: column.label,
                }))}
                columnOrder={paymentColumnOrder}
                visibleColumns={paymentVisibleColumns}
                draggingColumn={draggingPaymentColumn}
                onToggleColumn={togglePaymentColumn}
                onMoveColumn={movePaymentColumn}
                onDragStart={handlePaymentColumnDragStart}
                onDragOver={handlePaymentColumnDragOver}
                onDrop={handlePaymentColumnDrop}
                onDragEnd={handlePaymentColumnDragEnd}
                onReset={resetPaymentTableSettings}
                footer={
                  <div className="flex items-center gap-2 text-sm text-slate-600">
                    <span>Строк на странице</span>
                    <SystemSelect
                      value={paymentPageSize}
                      onChange={(event) => setPaymentPageSize(Number(event.target.value) || paymentPageSize)}
                      className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                    >
                      {PAGE_SIZE_OPTIONS.map((size) => (
                        <option key={size} value={size}>
                          {size}
                        </option>
                      ))}
                    </SystemSelect>
                  </div>
                }
              />
            </div>
          ) : null}
          <div className="overflow-x-auto">
            <table
              className="min-w-full table-fixed divide-y text-sm"
              style={{ width: getPaymentTableWidth(120), minWidth: '100%' }}
            >
              <colgroup>
                {visiblePaymentColumns.map((column) => (
                  <col key={column.key} style={{ width: getPaymentColumnWidth(column.key) }} />
                ))}
                <col style={{ width: 120 }} />
              </colgroup>
              <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                <tr>
                  {visiblePaymentColumns.map((column) => (
                    <th
                      key={column.key}
                      style={{ width: getPaymentColumnWidth(column.key) }}
                      className={`relative px-3 py-2 ${
                        column.align === 'right' ? 'text-right' : 'text-left'
                      } ${draggingPaymentColumn === column.key ? 'bg-blue-50 text-blue-700' : ''}`}
                      onDragOver={handlePaymentColumnDragOver}
                      onDrop={(event) => handlePaymentColumnDrop(column.key, event)}
                    >
                      <div
                        className={`inline-flex min-w-0 select-none items-center gap-2 ${
                          column.align === 'right' ? 'justify-end' : ''
                        }`}
                        draggable
                        onDragStart={(event) => handlePaymentColumnDragStart(column.key, event)}
                        onDragEnd={handlePaymentColumnDragEnd}
                        title="Перетащите, чтобы изменить порядок"
                      >
                        <GripVertical className="h-4 w-4 text-slate-400" />
                        <span className="truncate">{column.label}</span>
                      </div>
                      <div
                        role="separator"
                        aria-label={`Изменить ширину колонки ${column.label}`}
                        className="absolute right-0 top-0 h-full w-2 cursor-col-resize"
                        onMouseDown={(event) => handlePaymentResizeStart(column.key, event)}
                        onDragStart={(event) => event.preventDefault()}
                      />
                    </th>
                  ))}
                  <th className="px-3 py-2 text-right">В договоре</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {paymentState.loading ? (
                  <tr>
                    <td colSpan={visiblePaymentColumns.length + 1} className="px-3 py-8 text-center text-slate-500">
                      <span className="inline-flex items-center gap-2">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        Загрузка платежей...
                      </span>
                    </td>
                  </tr>
                ) : paymentState.error ? (
                  <tr>
                    <td colSpan={visiblePaymentColumns.length + 1} className="px-3 py-6 text-center text-rose-600">
                      {paymentState.error}
                    </td>
                  </tr>
                ) : paymentState.items.length === 0 ? (
                  <tr>
                    <td colSpan={visiblePaymentColumns.length + 1} className="px-3 py-6 text-center text-slate-500">
                      Платежи не найдены
                    </td>
                  </tr>
                ) : (
                  paymentState.items.map((item) => (
                    <tr key={item.id}>
                      {visiblePaymentColumns.map((column) => (
                        <td
                          key={column.key}
                          style={{ width: getPaymentColumnWidth(column.key) }}
                          className={`max-w-0 overflow-hidden px-3 py-2 align-top ${
                            column.align === 'right' ? 'text-right' : ''
                          }`}
                        >
                          {column.key === 'id' ? (
                            <button
                              className="text-blue-700 hover:underline"
                              onClick={() => canViewPayments && onOpenMoneyEditor?.(item.id)}
                            >
                              {item.id}
                            </button>
                          ) : column.key === 'date' ? (
                            formatDate(item.date)
                          ) : column.key === 'client' ? (
                            item.client?.name || '—'
                          ) : column.key === 'description' ? (
                            item.description || '—'
                          ) : (
                            formatAmount(item.amount)
                          )}
                        </td>
                      ))}
                      <td className="px-3 py-2 text-right">
                        <SystemCheckbox
                          type="checkbox"
                          disabled={!editMode}
                          checked={(contract.paymentIds ?? []).includes(item.id)}
                          onChange={() => toggleId('paymentIds', item.id)}
                        />
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
          <TablePagination
            page={paymentPage}
            totalPages={paymentTotalPages}
            totalItems={paymentState.total}
            pageSize={paymentPageSize}
            pageSizeOptions={PAGE_SIZE_OPTIONS}
            onPageChange={setPaymentPage}
            onPageSizeChange={setPaymentPageSize}
          />
        </div>
      )}

      <CounterpartyPickerModal
        open={pickerOpen}
        mode="multiple"
        title="Выбор контрагентов"
        selected={selectedPickerValue}
        allowedScopes={allowedCounterpartyScopes}
        onApply={handleApplyCounterparties}
        onClose={() => setPickerOpen(false)}
      />
      {messengerDeliveryOpen && contract.clients.length > 0 ? (
        <MessengerDocumentDeliveryModal
          clientId={contract.clients[0].id}
          clientOptions={contract.clients.map((client) => ({ id: client.id, name: client.name }))}
          document={{
            id: contract.id,
            originalFileName: `contract-${contract.number || contract.id}.pdf`,
            documentType: 'Договор',
            version: 1,
            sizeBytes: 0,
            sourceType: 'contract',
          }}
          preparePdf={() => apiService.downloadContractDocument(contract.id, 'pdf')}
          onClose={() => setMessengerDeliveryOpen(false)}
        />
      ) : null}
      {electronicSignatureOpen && contract.clients.length > 0 ? (
        <ElectronicSignatureModal
          clientId={contract.clients[0].id}
          documentId={contract.id}
          documentName={`Договор ${contract.number || contract.id}`}
          documentType="contract"
          preparePdf={() => apiService.downloadContractDocument(contract.id, 'pdf')}
          onClose={() => setElectronicSignatureOpen(false)}
        />
      ) : null}
    </div>
  );
}
