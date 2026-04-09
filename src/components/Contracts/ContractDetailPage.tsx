import { ArrowLeft, GripVertical, Loader2, Pencil, Plus, Save, Search, Settings2, X } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { usePersistedTableSettings } from '../../hooks/usePersistedTableSettings';
import { useDebouncedValue } from '../../hooks/useDebouncedValue';
import { apiService, type ContractRulesSettingsDto } from '../../services/api';
import type { Act, Client, Contract, ContractClient, ContractInput, Invoice, Payment } from '../../types';
import { fromInputToApiDate, toDateInputValue, toRuDate } from '../../utils/dateUtils';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { useToast } from '../../hooks/useToast';
import { TableColumnSettings } from '../ui/TableColumnSettings';
import { TablePagination } from '../ui/TablePagination';
import { ObjectAuditLauncher } from '../ui/ObjectAuditModal';

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

type TabListState<T> = {
  items: T[];
  total: number;
  loading: boolean;
  error: string | null;
};

const PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
const PICKER_PAGE_SIZE_OPTIONS = [10, 25, 50];
const TABLE_STORAGE_PREFIX = 'pp.contract_detail_table';

type DetailTableColumn<K extends string> = {
  key: K;
  label: string;
  defaultWidth: number;
  defaultVisible?: boolean;
  align?: 'left' | 'right';
};

const CLIENT_TABLE_COLUMNS: DetailTableColumn<ClientTableColumnKey>[] = [
  { key: 'name', label: 'ФИО', defaultWidth: 260 },
  { key: 'company', label: 'Компания', defaultWidth: 220 },
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
  clients: 'Клиенты',
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

function parseAmountInput(value: string): number | null | undefined {
  const normalized = value.replace(/\s+/g, '').replace(',', '.').trim();
  if (!normalized) return null;

  const parsed = Number(normalized);
  if (Number.isNaN(parsed) || parsed < 0) return undefined;

  return parsed;
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
  const toast = useToast();
  const [contract, setContract] = useState<Contract | null>(null);
  const [contractRules, setContractRules] = useState<ContractRulesSettingsDto>({
    autoGenerateWhenEmpty: true,
    allowManualNumberEdit: true,
  });
  const [amountInput, setAmountInput] = useState('');
  const [activeTab, setActiveTab] = useState<Tab>('clients');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [editMode, setEditMode] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);

  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerSearch, setPickerSearch] = useState('');
  const debouncedPickerSearch = useDebouncedValue(pickerSearch.trim(), 250);
  const [pickerClients, setPickerClients] = useState<Client[]>([]);
  const [pickerLoading, setPickerLoading] = useState(false);
  const [pickerError, setPickerError] = useState<string | null>(null);
  const [pickerPage, setPickerPage] = useState(1);
  const [pickerPageSize, setPickerPageSize] = useState(PICKER_PAGE_SIZE_OPTIONS[0]);
  const [pickerTotal, setPickerTotal] = useState(0);

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

  const [actPage, setActPage] = useState(1);
  const [actState, setActState] = useState<TabListState<Act>>(() => createTabListState());

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
        setAmountInput(formatAmountInput(nextContract.amount));
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
  }, [contractClientIdsQuery]);

  useEffect(() => {
    setSettingsOpen(false);
  }, [activeTab]);

  useEffect(() => {
    setInvoicePage(1);
  }, [invoicePageSize]);

  useEffect(() => {
    setActPage(1);
  }, [actPageSize]);

  useEffect(() => {
    setPaymentPage(1);
  }, [paymentPageSize]);

  useEffect(() => {
    if (activeTab !== 'invoices') return;

    if (!contractClientIdsQuery) {
      setInvoiceState(createTabListState());
      return;
    }

    let cancel = false;

    const loadInvoices = async () => {
      setInvoiceState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const response = await apiService.getInvoices({
          clientIds: contractClientIdsQuery,
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
  }, [activeTab, contractClientIdsQuery, invoicePage, invoicePageSize]);

  useEffect(() => {
    if (activeTab !== 'acts') return;

    if (!contractClientIdsQuery) {
      setActState(createTabListState());
      return;
    }

    let cancel = false;

    const loadActs = async () => {
      setActState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const response = await apiService.getActs({
          clientIds: contractClientIdsQuery,
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
  }, [activeTab, contractClientIdsQuery, actPage, actPageSize]);

  useEffect(() => {
    if (activeTab !== 'money') return;

    if (!contractClientIdsQuery) {
      setPaymentState(createTabListState());
      return;
    }

    let cancel = false;

    const loadPayments = async () => {
      setPaymentState((prev) => ({ ...prev, loading: true, error: null }));
      try {
        const response = await apiService.getPaymentsV2({
          clientIds: contractClientIdsQuery,
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
  }, [activeTab, contractClientIdsQuery, paymentPage, paymentPageSize]);

  const selectedClientIds = useMemo(
    () => new Set((contract?.clients ?? []).map((item) => item.id)),
    [contract],
  );

  useEffect(() => {
    if (!pickerOpen) return;
    let cancel = false;

    const loadPickerClients = async () => {
      setPickerLoading(true);
      setPickerError(null);
      try {
        const response = await apiService.getClientsV2({
          search: debouncedPickerSearch || undefined,
          sortBy: 'name',
          sortDir: 'asc',
          page: pickerPage,
          pageSize: pickerPageSize,
        });

        if (cancel) return;

        setPickerClients(response.items ?? []);
        setPickerTotal(response.total ?? 0);
      } catch (error) {
        if (cancel) return;

        const message = extractDisplayErrorMessage(error, 'Не удалось загрузить клиентов');
        setPickerClients([]);
        setPickerTotal(0);
        setPickerError(message);
        toast.error(message, 'Ошибка загрузки');
      } finally {
        if (!cancel) {
          setPickerLoading(false);
        }
      }
    };

    void loadPickerClients();
    return () => {
      cancel = true;
    };
  }, [debouncedPickerSearch, pickerOpen, pickerPage, pickerPageSize, toast]);

  useEffect(() => {
    setPickerPage(1);
  }, [debouncedPickerSearch, pickerPageSize]);

  const availableClients = useMemo(
    () => pickerClients.filter((client) => !selectedClientIds.has(client.id)),
    [pickerClients, selectedClientIds],
  );

  const invoiceTotalPages = Math.max(1, Math.ceil(invoiceState.total / invoicePageSize || 1));
  const actTotalPages = Math.max(1, Math.ceil(actState.total / actPageSize || 1));
  const paymentTotalPages = Math.max(1, Math.ceil(paymentState.total / paymentPageSize || 1));
  const pickerTotalPages = Math.max(1, Math.ceil(pickerTotal / pickerPageSize || 1));

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

    const payload: ContractInput = {
      number: contract.number,
      date: contract.date,
      startDate: contract.startDate,
      title: contract.title,
      description: contract.description,
      amount: parsedAmount ?? undefined,
      validUntil: contract.validUntil,
      terminationDate: contract.terminationDate,
      clients: (contract.clients ?? []).map((client) => ({
        clientId: client.id,
        comment: client.comment ?? undefined,
      })),
      actIds: contract.actIds ?? [],
      invoiceIds: contract.invoiceIds ?? [],
      paymentIds: contract.paymentIds ?? [],
    };

    setSaving(true);
    try {
      const updatedContract = await apiService.updateContract(contract.id, payload);
      setContract(updatedContract);
      setAmountInput(formatAmountInput(updatedContract.amount));
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

  const addClient = (client: Client) => {
    setContract((prev) => {
      if (!prev) return prev;

      const nextClient: ContractClient = {
        id: client.id,
        name: client.name,
        company: client.company,
        clientStatus: client.clientStatus,
        clientStatusId: client.clientStatusId,
        comment: '',
      };

      return { ...prev, clients: [...prev.clients, nextClient] };
    });
  };

  if (loading || !contract) {
    return (
      <div className="p-8">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    );
  }

  return (
    <div className="p-6">
      <div className="mb-4 flex items-center justify-between">
        <button onClick={onBack} className="inline-flex items-center gap-2 rounded border px-3 py-2">
          <ArrowLeft className="h-4 w-4" />
          Назад
        </button>

        <div className="flex items-center gap-2">
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
          <input
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
          <input
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
          Действует до
          <input
            type="date"
            value={toDateInputValue(contract.validUntil) ?? ''}
            disabled={!editMode}
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
          <input
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
            disabled={!editMode}
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

      <div className="mb-4 grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-4">
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма договора</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(contract.amount)}</div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма счетов</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(contract.invoiceAmountTotal)}</div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма актов</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(contract.actAmountTotal)}</div>
        </div>
        <div className="rounded border bg-white p-4">
          <div className="text-xs uppercase tracking-wide text-slate-500">Сумма платежей</div>
          <div className="mt-2 text-xl font-semibold text-slate-900">{formatAmount(contract.paymentAmountTotal)}</div>
        </div>
      </div>

      <div className="mb-3 flex gap-2">
        {(Object.keys(tabLabels) as Tab[]).map((tab) => (
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
            <h3 className="font-semibold">Клиенты договора</h3>
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
                onClick={() => setPickerOpen(true)}
                disabled={!editMode}
                className="inline-flex items-center gap-2 rounded border border-emerald-300 bg-emerald-50 px-3 py-1.5 text-sm text-emerald-700 disabled:opacity-60"
              >
                <Plus className="h-4 w-4" />
                Добавить клиента
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
            <table className="min-w-full table-fixed divide-y text-sm">
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
                {(contract.clients ?? []).length === 0 ? (
                  <tr>
                    <td colSpan={visibleClientColumns.length + 1} className="px-3 py-6 text-center text-slate-500">
                      Клиенты не найдены
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
                            <button
                              className="text-blue-700 hover:underline"
                              onClick={() => onOpenClient?.(client.id)}
                            >
                              {client.name}
                            </button>
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
              </tbody>
            </table>
          </div>
        </div>
      )}

      {activeTab === 'invoices' && (
        <div className="rounded border bg-white">
          <div className="flex items-center justify-between border-b px-4 py-3">
            <h3 className="font-semibold">Счета договора</h3>
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
                    <select
                      value={invoicePageSize}
                      onChange={(event) => setInvoicePageSize(Number(event.target.value) || invoicePageSize)}
                      className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                    >
                      {PAGE_SIZE_OPTIONS.map((size) => (
                        <option key={size} value={size}>
                          {size}
                        </option>
                      ))}
                    </select>
                  </div>
                }
              />
            </div>
          ) : null}
          <div className="overflow-x-auto">
            <table className="min-w-full table-fixed divide-y text-sm">
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
                              onClick={() => onOpenInvoiceEditor?.(item.id)}
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
                        <input
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

      {activeTab === 'acts' && (
        <div className="rounded border bg-white">
          <div className="flex items-center justify-between border-b px-4 py-3">
            <h3 className="font-semibold">Акты договора</h3>
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
                    <select
                      value={actPageSize}
                      onChange={(event) => setActPageSize(Number(event.target.value) || actPageSize)}
                      className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                    >
                      {PAGE_SIZE_OPTIONS.map((size) => (
                        <option key={size} value={size}>
                          {size}
                        </option>
                      ))}
                    </select>
                  </div>
                }
              />
            </div>
          ) : null}
          <div className="overflow-x-auto">
            <table className="min-w-full table-fixed divide-y text-sm">
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
                              onClick={() => onOpenActEditor?.(item.id)}
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
                        <input
                          type="checkbox"
                          disabled={!editMode}
                          checked={(contract.actIds ?? []).includes(item.id)}
                          onChange={() => toggleId('actIds', item.id)}
                        />
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

      {activeTab === 'money' && (
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
                    <select
                      value={paymentPageSize}
                      onChange={(event) => setPaymentPageSize(Number(event.target.value) || paymentPageSize)}
                      className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-sm text-slate-700"
                    >
                      {PAGE_SIZE_OPTIONS.map((size) => (
                        <option key={size} value={size}>
                          {size}
                        </option>
                      ))}
                    </select>
                  </div>
                }
              />
            </div>
          ) : null}
          <div className="overflow-x-auto">
            <table className="min-w-full table-fixed divide-y text-sm">
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
                              onClick={() => onOpenMoneyEditor?.(item.id)}
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
                        <input
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

      {pickerOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="w-full max-w-3xl rounded-xl bg-white shadow-2xl">
            <div className="flex items-center justify-between border-b px-4 py-3">
              <h3 className="text-lg font-semibold">Выбор клиента</h3>
              <button className="rounded border px-2 py-1 text-sm" onClick={() => setPickerOpen(false)}>
                Закрыть
              </button>
            </div>
            <div className="p-4">
              <div className="relative mb-3">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                <input
                  type="search"
                  value={pickerSearch}
                  onChange={(event) => setPickerSearch(event.target.value)}
                  className="w-full rounded-lg border border-slate-200 py-2 pl-10 pr-3"
                  placeholder="Поиск клиента"
                />
              </div>

              {pickerError ? (
                <div className="mb-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                  {pickerError}
                </div>
              ) : null}

              <div className="max-h-80 overflow-auto rounded border">
                <table className="min-w-full table-fixed divide-y text-sm">
                  <colgroup>
                    <col style={{ width: '44%' }} />
                    <col style={{ width: '36%' }} />
                    <col style={{ width: '20%' }} />
                  </colgroup>
                  <thead className="bg-slate-50 text-xs uppercase text-slate-500">
                    <tr>
                      <th className="px-3 py-2 text-left">ФИО</th>
                      <th className="px-3 py-2 text-left">Компания</th>
                      <th className="px-3 py-2 text-right">Действие</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {pickerLoading ? (
                      <tr>
                        <td colSpan={3} className="px-3 py-8 text-center text-slate-500">
                          Загрузка...
                        </td>
                      </tr>
                    ) : availableClients.map((client) => (
                      <tr key={client.id}>
                        <td className="px-3 py-2">{client.name}</td>
                        <td className="px-3 py-2">{client.company || '—'}</td>
                        <td className="px-3 py-2 text-right">
                          <button
                            className="rounded border border-emerald-200 bg-emerald-50 px-2 py-1 text-xs text-emerald-700"
                            onClick={() => addClient(client)}
                          >
                            Добавить
                          </button>
                        </td>
                      </tr>
                    ))}
                    {!pickerLoading && availableClients.length === 0 && (
                      <tr>
                        <td colSpan={3} className="px-3 py-6 text-center text-slate-500">
                          Нет доступных клиентов
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
              <TablePagination
                page={pickerPage}
                totalPages={pickerTotalPages}
                totalItems={pickerTotal}
                pageSize={pickerPageSize}
                pageSizeOptions={PICKER_PAGE_SIZE_OPTIONS}
                onPageChange={setPickerPage}
                onPageSizeChange={setPickerPageSize}
              />
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
