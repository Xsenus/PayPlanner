import {
  AlertCircle,
  CheckCircle2,
  Download,
  FileText,
  Plus,
  Printer,
  RefreshCw,
  Save,
  Trash2,
} from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";

import { apiService } from "../../services/api";
import type {
  Act,
  Client,
  ClientCase,
  Contract,
  Invoice,
  LegalEntityDetail,
  LegalEntitySummary,
  Payment,
} from "../../types";
import { todayYMD, toRuDate } from "../../utils/dateUtils";
import { formatCurrencySmart } from "../../utils/formatters";
import {
  buildReconciliationRows,
  roundMoney,
} from "./clientReconciliationActUtils";

type ReconciliationStatus =
  | "Draft"
  | "SentToClient"
  | "Signed"
  | "Disagreements";

type MaybePaged<T> = T[] | { items?: T[] };

type DisagreementRow = {
  id: string;
  date: string;
  documentNumber: string;
  amount: string;
  note: string;
};

type ReconciliationDraft = {
  number: string;
  status: ReconciliationStatus;
  periodFrom: string;
  periodTo: string;
  caseId: string;
  contractId: string;
  includeInvoices: boolean;
  includeActs: boolean;
  includePayments: boolean;
  includeAdvances: boolean;
  noDisagreements: boolean;
  hasDisagreements: boolean;
  disagreements: DisagreementRow[];
  createdAt: string;
};

type DraftPatch<K extends keyof ReconciliationDraft> = Pick<
  ReconciliationDraft,
  K
>;

const STORAGE_PREFIX = "pp.client_reconciliation_act";

const STATUS_OPTIONS: Array<{ value: ReconciliationStatus; label: string }> = [
  { value: "Draft", label: "Черновик" },
  { value: "SentToClient", label: "Отправлен клиенту" },
  { value: "Signed", label: "Подписан" },
  { value: "Disagreements", label: "Разногласия" },
];

function createDefaultDraft(clientId: number): ReconciliationDraft {
  const today = todayYMD();
  const year = today.slice(0, 4);

  return {
    number: `АС-${clientId}-${year}`,
    status: "Draft",
    periodFrom: `${year}-01-01`,
    periodTo: today,
    caseId: "all",
    contractId: "all",
    includeInvoices: true,
    includeActs: true,
    includePayments: true,
    includeAdvances: true,
    noDisagreements: true,
    hasDisagreements: false,
    disagreements: [],
    createdAt: today,
  };
}

function storageKey(clientId: number) {
  return `${STORAGE_PREFIX}.${clientId}.v1`;
}

function extractItems<T>(response: MaybePaged<T> | null | undefined): T[] {
  if (!response) return [];
  if (Array.isArray(response)) return response;
  return Array.isArray(response.items) ? response.items : [];
}

function formatMoney(value: number) {
  return formatCurrencySmart(value).full;
}

function getPartyName(
  entity: LegalEntitySummary | LegalEntityDetail | null | undefined,
  fallback: string,
) {
  return entity?.fullName || entity?.shortName || fallback || "—";
}

function getPartyDetails(
  entity: LegalEntitySummary | LegalEntityDetail | null | undefined,
  fallbackName: string,
) {
  return [
    getPartyName(entity, fallbackName),
    entity?.inn ? `ИНН ${entity.inn}` : null,
    entity?.kpp ? `КПП ${entity.kpp}` : null,
    entity?.ogrn ? `ОГРН ${entity.ogrn}` : null,
    entity?.address || null,
    entity?.treasuryBankName ? `Банк ${entity.treasuryBankName}` : null,
    entity?.treasuryBankBik ? `БИК ${entity.treasuryBankBik}` : null,
    entity?.treasuryAccountNumber ? `р/с ${entity.treasuryAccountNumber}` : null,
  ].filter((value): value is string => Boolean(value));
}

function toCsvValue(value: string | number) {
  return `"${String(value).replace(/"/g, '""')}"`;
}

function downloadText(filename: string, content: string) {
  const blob = new Blob([content], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

export function ClientReconciliationAct({
  clientId,
  client,
  clientName,
}: {
  clientId: number;
  client: Client | null;
  clientName: string;
}) {
  const [draft, setDraft] = useState<ReconciliationDraft>(() => {
    const fallback = createDefaultDraft(clientId);
    try {
      const saved = window.localStorage.getItem(storageKey(clientId));
      if (!saved) return fallback;
      return { ...fallback, ...JSON.parse(saved) };
    } catch {
      return fallback;
    }
  });
  const [cases, setCases] = useState<ClientCase[]>([]);
  const [contracts, setContracts] = useState<Contract[]>([]);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [payments, setPayments] = useState<Payment[]>([]);
  const [acts, setActs] = useState<Act[]>([]);
  const [companyProfile, setCompanyProfile] = useState<LegalEntityDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [savedLabel, setSavedLabel] = useState<string | null>(null);

  const updateDraft = useCallback(
    <K extends keyof ReconciliationDraft>(patch: DraftPatch<K>) => {
      setDraft((current) => ({ ...current, ...patch }));
      setSavedLabel(null);
    },
    [],
  );

  const loadData = useCallback(async () => {
    setLoading(true);
    setLoadError(null);

    try {
      const [
        casesResponse,
        contractsResponse,
        invoicesResponse,
        paymentsResponse,
        actsResponse,
        companyProfileResponse,
      ] = await Promise.all([
        apiService.getCasesV1({
          clientId,
          sortBy: "title",
          sortDir: "asc",
        }),
        apiService.getContracts({
          clientId,
          page: 1,
          pageSize: 500,
          sortBy: "date",
          sortDir: "desc",
        }),
        apiService.getInvoices({
          clientId,
          page: 1,
          pageSize: 500,
          sortBy: "date",
          sortDir: "desc",
        }),
        apiService.getPaymentsV2({
          clientId,
          page: 1,
          pageSize: 500,
          sortBy: "date",
          sortDir: "desc",
        }),
        apiService.getActs({
          clientId,
          page: 1,
          pageSize: 500,
          sortBy: "date",
          sortDir: "desc",
        }),
        apiService.getCompanyProfileLegalEntity().catch(() => null),
      ]);

      setCases(extractItems<ClientCase>(casesResponse));
      setContracts(extractItems<Contract>(contractsResponse));
      setInvoices(extractItems<Invoice>(invoicesResponse));
      setPayments(extractItems<Payment>(paymentsResponse));
      setActs(extractItems<Act>(actsResponse));
      setCompanyProfile(companyProfileResponse);
    } catch (error) {
      setLoadError(
        error instanceof Error
          ? error.message
          : "Не удалось загрузить данные для акта сверки.",
      );
    } finally {
      setLoading(false);
    }
  }, [clientId]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  const rows = useMemo(
    () =>
      buildReconciliationRows({
        invoices,
        payments,
        acts,
        periodFrom: draft.periodFrom,
        periodTo: draft.periodTo,
        caseId: draft.caseId,
        contractId: draft.contractId,
        includeInvoices: draft.includeInvoices,
        includeActs: draft.includeActs,
        includePayments: draft.includePayments,
      }),
    [
      acts,
      draft.caseId,
      draft.contractId,
      draft.includeActs,
      draft.includeInvoices,
      draft.includePayments,
      draft.periodFrom,
      draft.periodTo,
      invoices,
      payments,
    ],
  );

  const totals = useMemo(
    () =>
      rows.reduce(
        (acc, row) => ({
          debit: roundMoney(acc.debit + row.debit),
          credit: roundMoney(acc.credit + row.credit),
          balance: row.balance,
        }),
        { debit: 0, credit: 0, balance: 0 },
      ),
    [rows],
  );

  const selectedCase = useMemo(
    () => cases.find((item) => String(item.id) === draft.caseId) ?? null,
    [cases, draft.caseId],
  );
  const selectedContract = useMemo(
    () => contracts.find((item) => String(item.id) === draft.contractId) ?? null,
    [contracts, draft.contractId],
  );
  const clientPartyDetails = getPartyDetails(client?.legalEntity, client?.name || clientName);
  const executorPartyDetails = getPartyDetails(companyProfile, "Организация CRM");
  const statusLabel =
    STATUS_OPTIONS.find((item) => item.value === draft.status)?.label ?? draft.status;
  const balanceDirection =
    totals.balance > 0
      ? "в пользу исполнителя"
      : totals.balance < 0
      ? "в пользу клиента"
      : "расчёты закрыты";

  const saveDraft = () => {
    window.localStorage.setItem(storageKey(clientId), JSON.stringify(draft));
    setSavedLabel(`Черновик сохранён ${new Date().toLocaleTimeString("ru-RU")}`);
  };

  const exportCsv = () => {
    const lines = [
      [`Акт сверки № ${draft.number}`, `от ${toRuDate(draft.createdAt)}`],
      [`Период`, `${toRuDate(draft.periodFrom)} - ${toRuDate(draft.periodTo)}`],
      [`Клиент`, clientPartyDetails.join(", ")],
      [
        "№ п/п",
        "Дата операции",
        "Номер документа",
        "Вид документа",
        "Содержание операции",
        "Дебет (руб.)",
        "Кредит (руб.)",
        "Сальдо (руб.)",
        "Примечание",
      ],
      ...rows.map((row) => [
        row.index,
        toRuDate(row.date),
        row.documentNumber,
        row.documentType,
        row.description,
        row.debit.toFixed(2),
        row.credit.toFixed(2),
        row.balance.toFixed(2),
        row.note,
      ]),
      ["", "", "", "", "Итого", totals.debit.toFixed(2), totals.credit.toFixed(2), totals.balance.toFixed(2), balanceDirection],
    ];

    const csv = `\uFEFF${lines
      .map((line) => line.map((value) => toCsvValue(value)).join(";"))
      .join("\r\n")}`;
    downloadText(`akt-sverki-${draft.number || clientId}.csv`, csv);
  };

  const addDisagreement = () => {
    updateDraft({
      hasDisagreements: true,
      noDisagreements: false,
      status: "Disagreements",
      disagreements: [
        ...draft.disagreements,
        {
          id: `${Date.now()}-${draft.disagreements.length}`,
          date: todayYMD(),
          documentNumber: "",
          amount: "",
          note: "",
        },
      ],
    });
  };

  const updateDisagreement = (
    rowId: string,
    patch: Partial<Omit<DisagreementRow, "id">>,
  ) => {
    updateDraft({
      disagreements: draft.disagreements.map((row) =>
        row.id === rowId ? { ...row, ...patch } : row,
      ),
    });
  };

  const removeDisagreement = (rowId: string) => {
    const nextRows = draft.disagreements.filter((row) => row.id !== rowId);
    updateDraft({
      disagreements: nextRows,
      hasDisagreements: nextRows.length > 0,
      noDisagreements: nextRows.length === 0 ? true : draft.noDisagreements,
    });
  };

  return (
    <div className="space-y-6" data-testid="client-reconciliation-act">
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(260px,360px)]">
        <div className="rounded-xl border border-slate-200 bg-slate-50 p-4">
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <label className="text-sm font-medium text-slate-700">
              Номер акта
              <input
                value={draft.number}
                onChange={(event) => updateDraft({ number: event.target.value })}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Статус
              <select
                value={draft.status}
                onChange={(event) =>
                  updateDraft({
                    status: event.target.value as ReconciliationStatus,
                  })
                }
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              >
                {STATUS_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="text-sm font-medium text-slate-700">
              Период с
              <input
                type="date"
                value={draft.periodFrom}
                onChange={(event) => updateDraft({ periodFrom: event.target.value })}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              />
            </label>
            <label className="text-sm font-medium text-slate-700">
              Период по
              <input
                type="date"
                value={draft.periodTo}
                onChange={(event) => updateDraft({ periodTo: event.target.value })}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              />
            </label>
          </div>

          <div className="mt-4 grid gap-4 md:grid-cols-2">
            <label className="text-sm font-medium text-slate-700">
              Дело
              <select
                value={draft.caseId}
                onChange={(event) => updateDraft({ caseId: event.target.value })}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              >
                <option value="all">Все дела</option>
                {cases.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.title}
                  </option>
                ))}
              </select>
            </label>
            <label className="text-sm font-medium text-slate-700">
              Договор
              <select
                value={draft.contractId}
                onChange={(event) => updateDraft({ contractId: event.target.value })}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 outline-none focus:border-blue-400 focus:ring-2 focus:ring-blue-100"
              >
                <option value="all">Все договоры</option>
                {contracts.map((item) => (
                  <option key={item.id} value={item.id}>
                    №{item.number} от {toRuDate(item.date)}
                  </option>
                ))}
              </select>
            </label>
          </div>

          <div className="mt-4 grid gap-3 md:grid-cols-2 xl:grid-cols-4">
            {[
              ["includeInvoices", "Счета"],
              ["includeActs", "Акты"],
              ["includePayments", "Оплаты"],
              ["includeAdvances", "Авансы и корректировки"],
            ].map(([key, label]) => (
              <label
                key={key}
                className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700"
              >
                <input
                  type="checkbox"
                  checked={Boolean(draft[key as keyof ReconciliationDraft])}
                  onChange={(event) =>
                    updateDraft({
                      [key]: event.target.checked,
                    } as DraftPatch<keyof ReconciliationDraft>)
                  }
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                {label}
              </label>
            ))}
          </div>
        </div>

        <div className="rounded-xl border border-slate-200 bg-white p-4">
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-900">
            <FileText className="h-4 w-4 text-blue-600" />
            Сводка
          </div>
          <dl className="mt-3 space-y-2 text-sm">
            <div className="flex justify-between gap-3">
              <dt className="text-slate-500">Дебет</dt>
              <dd className="font-semibold text-slate-900">{formatMoney(totals.debit)}</dd>
            </div>
            <div className="flex justify-between gap-3">
              <dt className="text-slate-500">Кредит</dt>
              <dd className="font-semibold text-slate-900">{formatMoney(totals.credit)}</dd>
            </div>
            <div className="flex justify-between gap-3 border-t border-slate-100 pt-2">
              <dt className="text-slate-500">Сальдо</dt>
              <dd className="text-right font-semibold text-slate-900">
                {formatMoney(Math.abs(totals.balance))}
                <span className="block text-xs font-normal text-slate-500">
                  {balanceDirection}
                </span>
              </dd>
            </div>
          </dl>
          <div className="mt-4 flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void loadData()}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
            >
              <RefreshCw className={loading ? "h-4 w-4 animate-spin" : "h-4 w-4"} />
              Обновить
            </button>
            <button
              type="button"
              onClick={saveDraft}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700"
            >
              <Save className="h-4 w-4" />
              Сохранить
            </button>
            <button
              type="button"
              onClick={exportCsv}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
            >
              <Download className="h-4 w-4" />
              Excel
            </button>
            <button
              type="button"
              onClick={() => window.print()}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
            >
              <Printer className="h-4 w-4" />
              PDF
            </button>
          </div>
          {savedLabel ? (
            <div className="mt-3 flex items-center gap-2 text-xs text-emerald-700">
              <CheckCircle2 className="h-4 w-4" />
              {savedLabel}
            </div>
          ) : null}
        </div>
      </div>

      {loadError ? (
        <div className="flex items-start gap-2 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
          <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
          {loadError}
        </div>
      ) : null}

      <section className="overflow-hidden rounded-xl border border-slate-200 bg-white">
        <div className="border-b border-slate-200 p-5">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <h3 className="text-lg font-semibold text-slate-950">
                Акт сверки взаиморасчётов № {draft.number || "—"} от{" "}
                {toRuDate(draft.createdAt)}
              </h3>
              <p className="mt-1 text-sm text-slate-500">
                Период: {toRuDate(draft.periodFrom) || "—"} -{" "}
                {toRuDate(draft.periodTo) || "—"}
              </p>
            </div>
            <span className="rounded-full border border-blue-200 bg-blue-50 px-3 py-1 text-xs font-semibold text-blue-700">
              {statusLabel}
            </span>
          </div>

          <div className="mt-4 grid gap-4 lg:grid-cols-2">
            <div className="rounded-lg bg-slate-50 p-3">
              <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                Исполнитель
              </div>
              <div className="mt-2 space-y-1 text-sm text-slate-700">
                {executorPartyDetails.map((line) => (
                  <div key={line}>{line}</div>
                ))}
              </div>
            </div>
            <div className="rounded-lg bg-slate-50 p-3">
              <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                Клиент
              </div>
              <div className="mt-2 space-y-1 text-sm text-slate-700">
                {clientPartyDetails.map((line) => (
                  <div key={line}>{line}</div>
                ))}
              </div>
            </div>
          </div>

          <div className="mt-4 grid gap-2 text-sm text-slate-600 md:grid-cols-2">
            <div>
              По договору:{" "}
              <span className="font-medium text-slate-900">
                {selectedContract
                  ? `№${selectedContract.number} от ${toRuDate(selectedContract.date)}`
                  : "Все договоры"}
              </span>
            </div>
            <div>
              По делу:{" "}
              <span className="font-medium text-slate-900">
                {selectedCase?.title ?? "Все дела"}
              </span>
            </div>
          </div>
        </div>

        <div className="overflow-x-auto">
          <table className="min-w-[1120px] w-full divide-y divide-slate-200 text-sm">
            <thead className="bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
              <tr>
                <th className="px-3 py-3 text-left">№ п/п</th>
                <th className="px-3 py-3 text-left">Дата операции</th>
                <th className="px-3 py-3 text-left">Номер документа</th>
                <th className="px-3 py-3 text-left">Вид документа</th>
                <th className="px-3 py-3 text-left">Содержание операции</th>
                <th className="px-3 py-3 text-right">Дебет (руб.)</th>
                <th className="px-3 py-3 text-right">Кредит (руб.)</th>
                <th className="px-3 py-3 text-right">Сальдо (руб.)</th>
                <th className="px-3 py-3 text-left">Примечание</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 bg-white text-slate-700">
              {loading ? (
                <tr>
                  <td colSpan={9} className="px-3 py-10 text-center text-slate-500">
                    Формируем акт сверки...
                  </td>
                </tr>
              ) : rows.length === 0 ? (
                <tr>
                  <td colSpan={9} className="px-3 py-10 text-center text-slate-500">
                    За выбранный период операций нет.
                  </td>
                </tr>
              ) : (
                rows.map((row) => (
                  <tr key={`${row.sourceKind}-${row.sourceId}`}>
                    <td className="whitespace-nowrap px-3 py-3">{row.index}</td>
                    <td className="whitespace-nowrap px-3 py-3">{toRuDate(row.date)}</td>
                    <td className="whitespace-nowrap px-3 py-3 font-medium text-blue-700">
                      {row.documentNumber}
                    </td>
                    <td className="whitespace-nowrap px-3 py-3">{row.documentType}</td>
                    <td className="min-w-[260px] px-3 py-3">{row.description || "—"}</td>
                    <td className="whitespace-nowrap px-3 py-3 text-right">
                      {row.debit ? formatMoney(row.debit) : "—"}
                    </td>
                    <td className="whitespace-nowrap px-3 py-3 text-right">
                      {row.credit ? formatMoney(row.credit) : "—"}
                    </td>
                    <td className="whitespace-nowrap px-3 py-3 text-right font-semibold">
                      {formatMoney(row.balance)}
                    </td>
                    <td className="min-w-[160px] px-3 py-3 text-slate-500">
                      {row.note || "—"}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
            <tfoot className="border-t border-slate-200 bg-slate-50 text-sm font-semibold text-slate-900">
              <tr>
                <td colSpan={5} className="px-3 py-3 text-right">
                  Итого
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-right">
                  {formatMoney(totals.debit)}
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-right">
                  {formatMoney(totals.credit)}
                </td>
                <td className="whitespace-nowrap px-3 py-3 text-right">
                  {formatMoney(totals.balance)}
                </td>
                <td className="px-3 py-3">{balanceDirection}</td>
              </tr>
            </tfoot>
          </table>
        </div>

        <div className="space-y-4 border-t border-slate-200 p-5">
          <div className="grid gap-3 md:grid-cols-2">
            <label className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700">
              <input
                type="checkbox"
                checked={draft.noDisagreements}
                onChange={(event) =>
                  updateDraft({
                    noDisagreements: event.target.checked,
                    hasDisagreements: event.target.checked
                      ? false
                      : draft.hasDisagreements,
                  })
                }
                className="h-4 w-4 rounded border-slate-300 text-blue-600"
              />
              Расчёты проверены, разногласий нет
            </label>
            <label className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700">
              <input
                type="checkbox"
                checked={draft.hasDisagreements}
                onChange={(event) =>
                  updateDraft({
                    hasDisagreements: event.target.checked,
                    noDisagreements: event.target.checked
                      ? false
                      : draft.noDisagreements,
                    status: event.target.checked ? "Disagreements" : draft.status,
                  })
                }
                className="h-4 w-4 rounded border-slate-300 text-blue-600"
              />
              Имеются разногласия
            </label>
          </div>

          {draft.hasDisagreements ? (
            <div className="rounded-lg border border-amber-200 bg-amber-50/50 p-3">
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <div className="text-sm font-semibold text-slate-900">
                  Таблица разногласий
                </div>
                <button
                  type="button"
                  onClick={addDisagreement}
                  className="inline-flex items-center gap-2 rounded-lg bg-amber-600 px-3 py-2 text-sm font-medium text-white hover:bg-amber-700"
                >
                  <Plus className="h-4 w-4" />
                  Добавить запись
                </button>
              </div>

              {draft.disagreements.length === 0 ? (
                <div className="rounded-lg border border-dashed border-amber-200 bg-white px-3 py-6 text-center text-sm text-slate-500">
                  Записей разногласий пока нет.
                </div>
              ) : (
                <div className="overflow-x-auto">
                  <table className="min-w-[780px] w-full text-sm">
                    <thead className="text-xs uppercase tracking-wide text-slate-500">
                      <tr>
                        <th className="px-2 py-2 text-left">Дата</th>
                        <th className="px-2 py-2 text-left">Номер документа</th>
                        <th className="px-2 py-2 text-left">Сумма</th>
                        <th className="px-2 py-2 text-left">Примечание</th>
                        <th className="px-2 py-2 text-right">Действия</th>
                      </tr>
                    </thead>
                    <tbody>
                      {draft.disagreements.map((row) => (
                        <tr key={row.id}>
                          <td className="px-2 py-2">
                            <input
                              type="date"
                              value={row.date}
                              onChange={(event) =>
                                updateDisagreement(row.id, { date: event.target.value })
                              }
                              className="w-full rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-sm"
                            />
                          </td>
                          <td className="px-2 py-2">
                            <input
                              value={row.documentNumber}
                              onChange={(event) =>
                                updateDisagreement(row.id, {
                                  documentNumber: event.target.value,
                                })
                              }
                              className="w-full rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-sm"
                            />
                          </td>
                          <td className="px-2 py-2">
                            <input
                              value={row.amount}
                              onChange={(event) =>
                                updateDisagreement(row.id, {
                                  amount: event.target.value,
                                })
                              }
                              className="w-full rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-sm"
                            />
                          </td>
                          <td className="px-2 py-2">
                            <input
                              value={row.note}
                              onChange={(event) =>
                                updateDisagreement(row.id, { note: event.target.value })
                              }
                              className="w-full rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-sm"
                            />
                          </td>
                          <td className="px-2 py-2 text-right">
                            <button
                              type="button"
                              onClick={() => removeDisagreement(row.id)}
                              className="inline-flex h-8 w-8 items-center justify-center rounded-full border border-rose-200 text-rose-600 hover:bg-rose-50"
                              aria-label="Удалить строку разногласий"
                            >
                              <Trash2 className="h-4 w-4" />
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          ) : null}

          <div className="grid gap-6 pt-4 text-sm text-slate-700 md:grid-cols-2">
            <div>
              <div className="mb-8 h-px bg-slate-200" />
              Исполнитель / подпись / дата
            </div>
            <div>
              <div className="mb-8 h-px bg-slate-200" />
              Клиент / подпись / дата
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}
