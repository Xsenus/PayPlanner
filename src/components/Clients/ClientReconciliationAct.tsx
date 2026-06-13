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

import { useUserTimeZone } from "../../hooks/useUserTimeZone";
import { apiService } from "../../services/api";
import type {
  Act,
  Client,
  ClientCase,
  ClientBalanceTransaction,
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
  type ReconciliationRow,
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

type ReconciliationParty = {
  name: string;
  nameWithInn: string;
  details: string[];
  signerName: string;
  signerPosition: string;
  signatureDataUrl?: string | null;
  sealDataUrl?: string | null;
};

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

function getPartySignerName(
  entity: LegalEntitySummary | LegalEntityDetail | null | undefined,
  fallbackName: string,
) {
  return entity?.director?.trim() || getPartyName(entity, fallbackName);
}

function getPartySignerPosition(
  entity: LegalEntitySummary | LegalEntityDetail | null | undefined,
  fallback: string,
) {
  if (entity?.organizationForm?.toLowerCase().includes("ип")) {
    return "Предприниматель";
  }

  return fallback;
}

function formatSignatureName(value: string) {
  const normalized = value.trim().replace(/\s+/g, " ");
  if (/^(?:ООО|АО|ПАО|ЗАО|ОАО|НКО|АНО|Фонд|Ассоциация)(?:\s|$)/i.test(normalized)) {
    return normalized;
  }

  const personName = normalized.replace(/^ИП\s+/i, "");
  const parts = personName.split(" ").filter(Boolean);
  if (parts.length < 2) {
    return personName;
  }

  const initials = parts
    .slice(1)
    .map((part) => {
      const letters = part.replace(/\./g, "");
      if (part.includes(".") && letters.length > 0 && letters.length <= 2) {
        return letters
          .split("")
          .map((letter) => `${letter.toUpperCase()}.`)
          .join("");
      }

      return part[0] ? `${part[0].toUpperCase()}.` : "";
    })
    .filter(Boolean)
    .join("");
  return initials ? `${parts[0]} ${initials}` : personName;
}

function createParty(
  entity: LegalEntitySummary | LegalEntityDetail | null | undefined,
  fallbackName: string,
  signerPosition: string,
): ReconciliationParty {
  const name = getPartyName(entity, fallbackName);
  const signerName = getPartySignerName(entity, fallbackName);

  return {
    name,
    nameWithInn: entity?.inn ? `${name} (ИНН ${entity.inn})` : name,
    details: getPartyDetails(entity, fallbackName),
    signerName: formatSignatureName(signerName),
    signerPosition: getPartySignerPosition(entity, signerPosition),
    signatureDataUrl: entity?.directorSignatureDataUrl ?? null,
    sealDataUrl: entity?.sealDataUrl ?? null,
  };
}

function formatAccountingMoney(value: number) {
  if (!value) return "";
  return value
    .toLocaleString("ru-RU", {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })
    .replace(/\u00a0/g, " ");
}

function formatAccountingMoneyText(value: number) {
  return `${value.toLocaleString("ru-RU", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })} руб.`;
}

function escapeHtml(value: string | number | null | undefined) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function getCleanDocumentNumber(row: ReconciliationRow) {
  const documentNumber = row.documentNumber.trim();
  if (row.sourceKind === "payment" && documentNumber.startsWith("ID ")) {
    return "";
  }

  return documentNumber;
}

function getOperationName(row: ReconciliationRow, side: "executor" | "client") {
  if (row.sourceKind === "payment") {
    return row.documentType === "Аванс" ? "Аванс" : "Оплата";
  }
  if (row.sourceKind === "balance") {
    return row.documentType;
  }

  const isExecutorSale = row.debit >= row.credit;
  if (side === "executor") {
    return isExecutorSale ? "Продажа" : "Покупка";
  }

  return isExecutorSale ? "Покупка" : "Продажа";
}

function getAccountingDocumentTitle(row: ReconciliationRow, side: "executor" | "client") {
  const number = getCleanDocumentNumber(row);
  const numberPart = number ? `${number} ` : "";
  return `${getOperationName(row, side)} (${numberPart}от ${toRuDate(row.date)})`;
}

function getBalanceSummary(
  balance: number,
  periodTo: string,
  executorName: string,
  clientName: string,
) {
  if (balance > 0) {
    return `на ${toRuDate(periodTo)} задолженность ${clientName} перед ${executorName} составляет ${formatAccountingMoneyText(balance)}`;
  }

  if (balance < 0) {
    return `на ${toRuDate(periodTo)} задолженность ${executorName} перед ${clientName} составляет ${formatAccountingMoneyText(Math.abs(balance))}`;
  }

  return `на ${toRuDate(periodTo)} задолженности нет`;
}

function renderBalanceSummaryHtml(
  balance: number,
  periodTo: string,
  executorName: string,
  clientName: string,
) {
  const summary = escapeHtml(getBalanceSummary(balance, periodTo, executorName, clientName));
  if (balance === 0) {
    return `<strong>${summary}</strong>`;
  }

  return summary
    .replace(" составляет ", " составляет <strong>")
    .replace(" руб.", " руб.</strong>");
}

function renderAssetImage(
  src: string | null | undefined,
  className: string,
  alt: string,
) {
  if (!src) return "";
  return `<img class="${className}" src="${escapeHtml(src)}" alt="${escapeHtml(alt)}" />`;
}

function buildReconciliationExcelHtml({
  draft,
  rows,
  totals,
  executor,
  client,
  counterpartyRows = "filled",
  fixedPrintLayout = false,
}: {
  draft: ReconciliationDraft;
  rows: ReconciliationRow[];
  totals: { debit: number; credit: number; balance: number };
  executor: ReconciliationParty;
  client: ReconciliationParty;
  counterpartyRows?: "filled" | "blank";
  fixedPrintLayout?: boolean;
}) {
  const showCounterpartyRows = counterpartyRows === "filled";
  const rowsHtml = rows
    .map(
      (row) => `
        <tr>
          <td>${escapeHtml(toRuDate(row.date))}</td>
          <td>${escapeHtml(getAccountingDocumentTitle(row, "executor"))}</td>
          <td class="amount">${escapeHtml(formatAccountingMoney(row.debit))}</td>
          <td class="amount">${escapeHtml(formatAccountingMoney(row.credit))}</td>
          <td>${showCounterpartyRows ? escapeHtml(toRuDate(row.date)) : ""}</td>
          <td>${showCounterpartyRows ? escapeHtml(getAccountingDocumentTitle(row, "client")) : ""}</td>
          <td class="amount"></td>
          <td class="amount"></td>
        </tr>`,
    )
    .join("");

  const balanceDebit = totals.balance > 0 ? totals.balance : 0;
  const balanceCredit = totals.balance < 0 ? Math.abs(totals.balance) : 0;

  return `\uFEFF<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <style>
    @page { size: A4 landscape; margin: 0; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: #fff;
      color: #333;
      font-family: Arial, "DejaVu Sans", sans-serif;
      font-size: 10pt;
      line-height: 1.18;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }
    .act-sheet {
      position: relative;
      width: ${fixedPrintLayout ? "265mm" : "100%"};
      min-height: ${fixedPrintLayout ? "190mm" : "auto"};
      margin: ${fixedPrintLayout ? "6mm auto 0" : "0"};
      padding: 0;
    }
    h1 {
      margin: 0 0 ${fixedPrintLayout ? "4mm" : "8px"};
      text-align: center;
      font-size: ${fixedPrintLayout ? "24pt" : "16pt"};
      line-height: 1.05;
      font-weight: 400;
    }
    .subtitle {
      width: 100%;
      margin: 0 auto ${fixedPrintLayout ? "8mm" : "14px"};
      text-align: center;
      white-space: pre-line;
      font-size: ${fixedPrintLayout ? "12pt" : "10pt"};
      line-height: 1.35;
    }
    .intro {
      margin: 0 0 ${fixedPrintLayout ? "4.4mm" : "14px"};
      white-space: normal;
      font-size: ${fixedPrintLayout ? "12pt" : "10pt"};
      line-height: 1.35;
    }
    .act-table { width: 100%; border-collapse: collapse; table-layout: fixed; }
    .act-table col.date { width: ${fixedPrintLayout ? "26mm" : "25mm"}; }
    .act-table col.doc { width: 53mm; }
    .act-table col.debit { width: ${fixedPrintLayout ? "26.5mm" : "26mm"}; }
    .act-table col.credit { width: ${fixedPrintLayout ? "26.5mm" : "26mm"}; }
    .act-table col.right-date { width: 27mm; }
    .act-table col.right-doc { width: 53mm; }
    .act-table col.right-debit { width: ${fixedPrintLayout ? "26.5mm" : "26mm"}; }
    .act-table col.right-credit { width: ${fixedPrintLayout ? "26.7mm" : "29mm"}; }
    .act-table th,
    .act-table td {
      border: 1px solid #c7c7c7;
      padding: ${fixedPrintLayout ? "1.15mm 2mm" : "5px"};
      height: ${fixedPrintLayout ? "5.3mm" : "auto"};
      vertical-align: middle;
      font-size: ${fixedPrintLayout ? "9pt" : "10pt"};
      line-height: ${fixedPrintLayout ? "1.12" : "1.18"};
    }
    ${fixedPrintLayout ? ".act-table td:nth-child(2), .act-table td:nth-child(6) { white-space: nowrap; }" : ""}
    .act-table th {
      text-align: center;
      font-weight: 400;
      background: #fff;
    }
    .act-table .party { text-align: left; font-weight: 400; }
    .amount { text-align: right; white-space: nowrap; }
    .total td, .balance-row td { font-weight: 700; }
    .summary {
      margin-top: ${fixedPrintLayout ? "5mm" : "18px"};
      width: 100%;
      border-collapse: collapse;
      table-layout: fixed;
    }
    .summary td {
      border: 0;
      padding: 0 0 ${fixedPrintLayout ? "1.5mm" : "4px"};
      width: 50%;
      font-size: ${fixedPrintLayout ? "10.5pt" : "10pt"};
      vertical-align: top;
    }
    .summary strong { font-weight: 700; }
    .sign {
      position: relative;
      margin-top: ${fixedPrintLayout ? "4mm" : "18px"};
      width: 100%;
      border-collapse: collapse;
      table-layout: fixed;
    }
    .sign td {
      position: relative;
      border: 0;
      width: 50%;
      height: ${fixedPrintLayout ? "30mm" : "76px"};
      padding: 0 3mm 0 0;
      vertical-align: top;
      font-size: ${fixedPrintLayout ? "10.5pt" : "10pt"};
    }
    .sign-line {
      position: absolute;
      left: 1mm;
      right: 9mm;
      bottom: ${fixedPrintLayout ? "8mm" : "22px"};
      border-bottom: 1px solid #777;
      height: 0;
    }
    .sign-name {
      position: absolute;
      right: 10mm;
      bottom: ${fixedPrintLayout ? "5.8mm" : "16px"};
      background: #fff;
      padding-left: 2mm;
    }
    .stamp-date {
      position: absolute;
      left: 1mm;
      bottom: 0;
    }
    .signature-img {
      position: absolute;
      left: 0;
      bottom: ${fixedPrintLayout ? "7mm" : "18px"};
      width: 30mm;
      max-height: 13mm;
      object-fit: contain;
      z-index: 2;
    }
    .seal-img {
      position: absolute;
      left: 46mm;
      bottom: ${fixedPrintLayout ? "2mm" : "4px"};
      width: 31mm;
      height: 31mm;
      object-fit: contain;
      z-index: 1;
    }
  </style>
</head>
<body>
<div class="act-sheet">
  <h1>Акт сверки</h1>
  <div class="subtitle">взаимных расчетов за период: ${escapeHtml(toRuDate(draft.periodFrom))} - ${escapeHtml(toRuDate(draft.periodTo))}
между ${escapeHtml(executor.nameWithInn)} и ${escapeHtml(client.nameWithInn)}</div>
  <div class="intro">Мы, нижеподписавшиеся, ${escapeHtml(executor.signerPosition)} ${escapeHtml(executor.name)}, с одной стороны, и ${escapeHtml(client.name)}, с другой стороны, составили настоящий акт сверки в том, что состояние взаимных расчетов по данным учета следующее:</div>
  <table class="act-table">
    <colgroup>
      <col class="date" />
      <col class="doc" />
      <col class="debit" />
      <col class="credit" />
      <col class="right-date" />
      <col class="right-doc" />
      <col class="right-debit" />
      <col class="right-credit" />
    </colgroup>
    <thead>
      <tr>
        <th class="party" colspan="4">По данным ${escapeHtml(executor.name)}, руб.</th>
        <th class="party" colspan="4">По данным ${escapeHtml(client.name)}, руб.</th>
      </tr>
      <tr>
        <th>Дата</th>
        <th>Документ</th>
        <th>Дебет</th>
        <th>Кредит</th>
        <th>Дата</th>
        <th>Документ</th>
        <th>Дебет</th>
        <th>Кредит</th>
      </tr>
    </thead>
    <tbody>
      <tr class="balance-row"><td colspan="2">Сальдо начальное</td><td></td><td></td><td colspan="2">Сальдо начальное</td><td></td><td></td></tr>
      ${rowsHtml || `<tr><td colspan="8" style="text-align:center">За выбранный период операций нет.</td></tr>`}
      <tr class="total">
        <td colspan="2">Обороты за период</td>
        <td class="amount">${escapeHtml(formatAccountingMoney(totals.debit))}</td>
        <td class="amount">${escapeHtml(formatAccountingMoney(totals.credit))}</td>
        <td colspan="2">Обороты за период</td>
        <td class="amount"></td>
        <td class="amount"></td>
      </tr>
      <tr class="balance-row">
        <td colspan="2">Сальдо конечное</td>
        <td class="amount">${escapeHtml(formatAccountingMoney(balanceDebit))}</td>
        <td class="amount">${escapeHtml(formatAccountingMoney(balanceCredit))}</td>
        <td colspan="2">Сальдо конечное</td>
        <td class="amount"></td>
        <td class="amount"></td>
      </tr>
    </tbody>
  </table>
  <table class="summary">
    <tr>
      <td>${fixedPrintLayout ? `По данным ${escapeHtml(executor.name)}` : `По данным ${escapeHtml(executor.name)} и ${escapeHtml(client.name)}`}</td>
      <td>${fixedPrintLayout ? "" : `По данным ${escapeHtml(executor.name)} и ${escapeHtml(client.name)}`}</td>
    </tr>
    <tr>
      <td>${renderBalanceSummaryHtml(totals.balance, draft.periodTo, executor.name, client.name)}</td>
      <td>${fixedPrintLayout ? "" : `на ${escapeHtml(toRuDate(draft.periodTo))}`}</td>
    </tr>
  </table>
  <table class="sign">
    <tr>
      <td>
        <div>От ${escapeHtml(executor.name)}</div>
        <div style="margin-top:2mm">${escapeHtml(executor.signerPosition)}</div>
        ${renderAssetImage(executor.signatureDataUrl, "signature-img", "Подпись")}
        ${renderAssetImage(executor.sealDataUrl, "seal-img", "Печать")}
        <div class="sign-line"></div>
        <div class="sign-name">(${escapeHtml(executor.signerName)})</div>
        <div class="stamp-date">М.П. ${escapeHtml(toRuDate(draft.createdAt))}г.</div>
      </td>
      <td>
        <div>От ${escapeHtml(client.name)}</div>
        <div class="sign-line"></div>
        <div class="sign-name">(${escapeHtml(client.signerName)})</div>
        <div class="stamp-date">М.П. ${escapeHtml(toRuDate(draft.createdAt))}г.</div>
      </td>
    </tr>
  </table>
</div>
</body>
</html>`;
}

function downloadText(filename: string, content: string, type = "text/plain;charset=utf-8") {
  const blob = new Blob([content], { type });
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
  const [balanceTransactions, setBalanceTransactions] = useState<ClientBalanceTransaction[]>([]);
  const [companyProfile, setCompanyProfile] = useState<LegalEntityDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [savedLabel, setSavedLabel] = useState<string | null>(null);
  const timeZoneId = useUserTimeZone();

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
        balanceTransactionsResponse,
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
        apiService.getClientBalanceTransactions(clientId),
        apiService.getCompanyProfileLegalEntity().catch(() => null),
      ]);

      setCases(extractItems<ClientCase>(casesResponse));
      setContracts(extractItems<Contract>(contractsResponse));
      setInvoices(extractItems<Invoice>(invoicesResponse));
      setPayments(extractItems<Payment>(paymentsResponse));
      setActs(extractItems<Act>(actsResponse));
      setBalanceTransactions(balanceTransactionsResponse);
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
        includeAdvances: draft.includeAdvances,
        balanceTransactions,
        timeZoneId,
      }),
    [
      acts,
      draft.caseId,
      draft.contractId,
      draft.includeActs,
      draft.includeAdvances,
      draft.includeInvoices,
      draft.includePayments,
      draft.periodFrom,
      draft.periodTo,
      invoices,
      balanceTransactions,
      payments,
      timeZoneId,
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
  const clientParty = createParty(client?.legalEntity, client?.name || clientName, "Клиент");
  const executorParty = createParty(companyProfile, "Организация CRM", "Руководитель");
  const statusLabel =
    STATUS_OPTIONS.find((item) => item.value === draft.status)?.label ?? draft.status;
  const balanceDirection =
    totals.balance > 0
      ? "в пользу исполнителя"
      : totals.balance < 0
      ? "в пользу клиента"
      : "расчёты закрыты";
  const finalBalanceDebit = totals.balance > 0 ? totals.balance : 0;
  const finalBalanceCredit = totals.balance < 0 ? Math.abs(totals.balance) : 0;
  const balanceSummary = getBalanceSummary(
    totals.balance,
    draft.periodTo,
    executorParty.name,
    clientParty.name,
  );

  const saveDraft = () => {
    window.localStorage.setItem(storageKey(clientId), JSON.stringify(draft));
    setSavedLabel(`Черновик сохранён ${new Date().toLocaleTimeString("ru-RU")}`);
  };

  const exportExcel = () => {
    const html = buildReconciliationExcelHtml({
      draft,
      rows,
      totals,
      executor: executorParty,
      client: clientParty,
      counterpartyRows: "filled",
      fixedPrintLayout: false,
    });

    downloadText(
      `akt-sverki-${draft.number || clientId}.xls`,
      html,
      "application/vnd.ms-excel;charset=utf-8",
    );
  };

  const printReconciliationAct = () => {
    const html = buildReconciliationExcelHtml({
      draft,
      rows,
      totals,
      executor: executorParty,
      client: clientParty,
      counterpartyRows: "blank",
      fixedPrintLayout: true,
    });
    const printWindow = window.open("", "_blank", "width=1100,height=900");

    if (!printWindow) {
      window.print();
      return;
    }

    printWindow.document.open();
    printWindow.document.write(html);
    printWindow.document.close();
    setTimeout(() => {
      printWindow.focus();
      printWindow.print();
    }, 100);
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
                    } as unknown as DraftPatch<keyof ReconciliationDraft>)
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
              onClick={exportExcel}
              className="inline-flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
            >
              <Download className="h-4 w-4" />
              Excel
            </button>
            <button
              type="button"
              onClick={printReconciliationAct}
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
        <div className="p-5">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="min-w-0">
              <h3 className="text-center text-xl font-semibold text-slate-950">
                Акт сверки
              </h3>
              <p className="mt-2 whitespace-pre-line text-sm leading-6 text-slate-700">
                взаимных расчетов за период: {toRuDate(draft.periodFrom) || "—"} -{" "}
                {toRuDate(draft.periodTo) || "—"}
                {"\n"}между {executorParty.nameWithInn}
                {"\n"}и {clientParty.nameWithInn}
              </p>
            </div>
            <span className="rounded-full border border-blue-200 bg-blue-50 px-3 py-1 text-xs font-semibold text-blue-700">
              {statusLabel}
            </span>
          </div>

          <p className="mt-4 text-sm leading-6 text-slate-700">
            Мы, нижеподписавшиеся, {executorParty.signerPosition}{" "}
            {executorParty.name}, с одной стороны, и {clientParty.name}, с другой
            стороны, составили настоящий акт сверки в том, что состояние взаимных
            расчетов по данным учета следующее:
          </p>

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

        <div className="overflow-x-auto px-5 pb-5">
          <table
            data-testid="reconciliation-accounting-table"
            className="min-w-[1080px] w-full border-collapse text-sm"
          >
            <thead className="text-slate-700">
              <tr className="bg-slate-50">
                <th
                  className="border border-slate-300 px-3 py-2 text-center font-semibold"
                  colSpan={4}
                >
                  По данным {executorParty.name}
                </th>
                <th
                  className="border border-slate-300 px-3 py-2 text-center font-semibold"
                  colSpan={4}
                >
                  По данным {clientParty.name}
                </th>
              </tr>
              <tr className="bg-slate-50 text-xs uppercase tracking-wide">
                <th className="border border-slate-300 px-3 py-2 text-left">Дата</th>
                <th className="border border-slate-300 px-3 py-2 text-left">Документ</th>
                <th className="border border-slate-300 px-3 py-2 text-right">Дебет</th>
                <th className="border border-slate-300 px-3 py-2 text-right">Кредит</th>
                <th className="border border-slate-300 px-3 py-2 text-left">Дата</th>
                <th className="border border-slate-300 px-3 py-2 text-left">Документ</th>
                <th className="border border-slate-300 px-3 py-2 text-right">Дебет</th>
                <th className="border border-slate-300 px-3 py-2 text-right">Кредит</th>
              </tr>
            </thead>
            <tbody className="bg-white text-slate-800">
              <tr>
                <td className="border border-slate-300 px-3 py-2" colSpan={4}>
                  Сальдо начальное
                </td>
                <td className="border border-slate-300 px-3 py-2" colSpan={4}>
                  Сальдо начальное
                </td>
              </tr>
              {loading ? (
                <tr>
                  <td colSpan={8} className="border border-slate-300 px-3 py-10 text-center text-slate-500">
                    Формируем акт сверки...
                  </td>
                </tr>
              ) : rows.length === 0 ? (
                <tr>
                  <td colSpan={8} className="border border-slate-300 px-3 py-10 text-center text-slate-500">
                    За выбранный период операций нет.
                  </td>
                </tr>
              ) : (
                rows.map((row) => (
                  <tr key={`${row.sourceKind}-${row.sourceId}`}>
                    <td className="whitespace-nowrap border border-slate-300 px-3 py-2">
                      {toRuDate(row.date)}
                    </td>
                    <td className="min-w-[230px] border border-slate-300 px-3 py-2">
                      {getAccountingDocumentTitle(row, "executor")}
                    </td>
                    <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right">
                      {formatAccountingMoney(row.debit)}
                    </td>
                    <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right">
                      {formatAccountingMoney(row.credit)}
                    </td>
                    <td className="whitespace-nowrap border border-slate-300 px-3 py-2">
                      {toRuDate(row.date)}
                    </td>
                    <td className="min-w-[230px] border border-slate-300 px-3 py-2">
                      {getAccountingDocumentTitle(row, "client")}
                    </td>
                    <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right" />
                    <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right" />
                  </tr>
                ))
              )}
              <tr className="bg-slate-50 font-semibold text-slate-950">
                <td className="border border-slate-300 px-3 py-2" colSpan={2}>
                  Обороты за период
                </td>
                <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right">
                  {formatAccountingMoney(totals.debit)}
                </td>
                <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right">
                  {formatAccountingMoney(totals.credit)}
                </td>
                <td className="border border-slate-300 px-3 py-2" colSpan={2}>
                  Обороты за период
                </td>
                <td className="border border-slate-300 px-3 py-2" />
                <td className="border border-slate-300 px-3 py-2" />
              </tr>
              <tr className="bg-slate-50 font-semibold text-slate-950">
                <td className="border border-slate-300 px-3 py-2" colSpan={2}>
                  Сальдо конечное
                </td>
                <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right">
                  {formatAccountingMoney(finalBalanceDebit)}
                </td>
                <td className="whitespace-nowrap border border-slate-300 px-3 py-2 text-right">
                  {formatAccountingMoney(finalBalanceCredit)}
                </td>
                <td className="border border-slate-300 px-3 py-2" colSpan={2}>
                  Сальдо конечное
                </td>
                <td className="border border-slate-300 px-3 py-2" />
                <td className="border border-slate-300 px-3 py-2" />
              </tr>
            </tbody>
          </table>
        </div>

        <div className="grid gap-6 border-t border-slate-200 px-5 py-4 text-sm text-slate-700 md:grid-cols-2">
          <div>
            <div className="font-medium text-slate-900">
              По данным {executorParty.name} и {clientParty.name}
            </div>
            <div className="mt-1">{balanceSummary}</div>
          </div>
          <div>
            <div className="font-medium text-slate-900">
              По данным {executorParty.name} и {clientParty.name}
            </div>
            <div className="mt-1">на {toRuDate(draft.periodTo)}</div>
          </div>
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

          <div className="grid gap-10 pt-4 text-sm text-slate-700 md:grid-cols-2">
            <div>
              <div className="font-medium text-slate-900">От {executorParty.name}</div>
              <div className="mt-6">{executorParty.signerPosition}</div>
              <div className="mt-8 h-px bg-slate-300" />
              <div className="mt-2">({executorParty.signerName})</div>
              <div className="mt-4 text-slate-500">М.П. {toRuDate(draft.createdAt)}г.</div>
            </div>
            <div>
              <div className="font-medium text-slate-900">От {clientParty.name}</div>
              <div className="mt-6">&nbsp;</div>
              <div className="mt-8 h-px bg-slate-300" />
              <div className="mt-2">({clientParty.signerName})</div>
              <div className="mt-4 text-slate-500">М.П. {toRuDate(draft.createdAt)}г.</div>
            </div>
          </div>

          <div className="sr-only">
            {executorParty.details.join("; ")} {clientParty.details.join("; ")}
          </div>
        </div>
      </section>
    </div>
  );
}
