import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ClientReconciliationAct } from "./ClientReconciliationAct";
import { buildReconciliationRows } from "./clientReconciliationActUtils";
import { apiService } from "../../services/api";
import type { Act, Client, Contract, Invoice, Payment } from "../../types";

vi.mock("../../services/api", () => ({
  apiService: {
    getCasesV1: vi.fn(),
    getContracts: vi.fn(),
    getInvoices: vi.fn(),
    getPaymentsV2: vi.fn(),
    getActs: vi.fn(),
    getCompanyProfileLegalEntity: vi.fn(),
  },
}));

const apiMock = vi.mocked(apiService);

function invoice(overrides: Partial<Invoice> = {}): Invoice {
  return {
    id: 10,
    number: "15",
    date: "2026-01-10",
    amount: 10000,
    type: "Income",
    status: "Pending",
    isPaid: false,
    clientId: 1,
    clientCaseId: 7,
    contractId: 5,
    createdAt: "2026-01-10T08:00:00Z",
    ...overrides,
  } as Invoice;
}

function payment(overrides: Partial<Payment> = {}): Payment {
  return {
    id: 20,
    date: "2026-01-12",
    plannedDate: "2026-01-12",
    amount: 3000,
    type: "Income",
    status: "Completed",
    description: "Оплата по счёту",
    isPaid: true,
    paidAmount: 3000,
    outstandingAmount: 0,
    hasPartialPayment: false,
    notes: "",
    systemNotes: "",
    rescheduleCount: 0,
    clientId: 1,
    clientCaseId: 7,
    contractId: 5,
    createdAt: "2026-01-12T08:00:00Z",
    ...overrides,
  } as Payment;
}

function act(overrides: Partial<Act> = {}): Act {
  return {
    id: 30,
    number: "4",
    date: "2026-01-20",
    amount: 2000,
    type: "Income",
    status: "Created",
    clientId: 1,
    contractId: 5,
    invoiceId: 10,
    createdAt: "2026-01-20T08:00:00Z",
    ...overrides,
  } as Act;
}

describe("buildReconciliationRows", () => {
  it("builds a running balance from invoices, acts and actual payments", () => {
    const rows = buildReconciliationRows({
      invoices: [invoice()],
      acts: [act()],
      payments: [payment()],
      periodFrom: "2026-01-01",
      periodTo: "2026-01-31",
      caseId: "7",
      contractId: "5",
      includeInvoices: true,
      includeActs: true,
      includePayments: true,
    });

    expect(rows.map((row) => row.documentType)).toEqual(["Счёт", "Платёж", "Акт"]);
    expect(rows.map((row) => row.balance)).toEqual([10000, 7000, 9000]);
  });

  it("does not include cancelled documents and unpaid planned payments", () => {
    const rows = buildReconciliationRows({
      invoices: [
        invoice(),
        invoice({
          id: 11,
          number: "16",
          status: "Cancelled",
          amount: 5000,
        }),
      ],
      acts: [],
      payments: [
        payment(),
        payment({
          id: 21,
          status: "Pending",
          isPaid: false,
          paidAmount: 0,
          amount: 4000,
        }),
      ],
      periodFrom: "2026-01-01",
      periodTo: "2026-01-31",
      caseId: "all",
      contractId: "all",
      includeInvoices: true,
      includeActs: true,
      includePayments: true,
    });

    expect(rows).toHaveLength(2);
    expect(rows.map((row) => row.sourceId)).toEqual([10, 20]);
  });

  it("uses the profile time zone when fallback dates are UTC instants", () => {
    const rows = buildReconciliationRows({
      invoices: [
        invoice({
          date: "",
          createdAt: "2026-01-09T21:30:00Z",
        }),
      ],
      acts: [
        act({
          date: "",
          createdAt: "2026-01-09T21:30:00Z",
        }),
      ],
      payments: [
        payment({
          date: "",
          paidDate: "2026-01-09T21:30:00Z",
          paymentDocumentDate: "2026-01-09T21:30:00Z",
        }),
      ],
      periodFrom: "2026-01-10",
      periodTo: "2026-01-10",
      caseId: "all",
      contractId: "all",
      includeInvoices: true,
      includeActs: true,
      includePayments: true,
      timeZoneId: "Europe/Samara",
    });

    expect(rows.map((row) => row.date)).toEqual(["2026-01-10", "2026-01-10", "2026-01-10"]);
    expect(rows.map((row) => row.sourceKind)).toEqual(["invoice", "act", "payment"]);
    expect(rows[0].description).toContain("10.01.2026");
    expect(rows[2].note).toContain("10.01.2026");
  });
});

describe("ClientReconciliationAct", () => {
  beforeEach(() => {
    window.localStorage.clear();
    apiMock.getCasesV1.mockResolvedValue([
      {
        id: 7,
        clientId: 1,
        title: "БАНКРОТСТВО",
        status: "Open",
        createdAt: "2026-01-01T00:00:00Z",
      },
    ]);
    apiMock.getContracts.mockResolvedValue({
      items: [
        {
          id: 5,
          number: "1-2026",
          date: "2026-01-05",
          billingMode: "Fixed",
          isOpenEnded: true,
          invoiceAmountTotal: 10000,
          actAmountTotal: 0,
          paymentAmountTotal: 0,
          clients: [],
          createdAt: "2026-01-05T00:00:00Z",
        } as Contract,
      ],
      total: 1,
      page: 1,
      pageSize: 500,
    });
    apiMock.getInvoices.mockResolvedValue({
      items: [invoice()],
      total: 1,
      page: 1,
      pageSize: 500,
    });
    apiMock.getPaymentsV2.mockResolvedValue({
      items: [payment()],
      total: 1,
      page: 1,
      pageSize: 500,
      summary: {
        income: 3000,
        expense: 0,
        balance: 3000,
        incomeCount: 1,
        expenseCount: 0,
        total: 1,
      },
    });
    apiMock.getActs.mockResolvedValue({
      items: [act()],
      total: 1,
      page: 1,
      pageSize: 500,
    });
    apiMock.getCompanyProfileLegalEntity.mockResolvedValue({
      id: 100,
      shortName: "ЭлитПраво",
      fullName: "ООО ЭлитПраво",
      inn: "1234567890",
      clientsCount: 0,
      clients: [],
    });
  });

  it("renders the reconciliation table with CRM operations", async () => {
    render(
      <ClientReconciliationAct
        clientId={1}
        clientName="ООО Ромашка"
        client={
          {
            id: 1,
            name: "ООО Ромашка",
            legalEntity: {
              id: 2,
              shortName: "ООО Ромашка",
              inn: "9876543210",
              clientsCount: 1,
            },
          } as Client
        }
      />,
    );

    await waitFor(() => expect(apiMock.getInvoices).toHaveBeenCalled());

    expect(screen.getByTestId("client-reconciliation-act")).toBeTruthy();
    expect(screen.getByText("Акт сверки")).toBeTruthy();
    expect(screen.getByTestId("reconciliation-accounting-table")).toBeTruthy();
    expect(screen.getAllByText("По данным ООО ЭлитПраво").length).toBeGreaterThan(0);
    expect(screen.getAllByText(/ООО Ромашка/).length).toBeGreaterThan(0);
    expect(screen.getAllByText("Сальдо начальное").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Обороты за период").length).toBeGreaterThan(0);
    expect(screen.getByText("Продажа (№15 от 10.01.2026)")).toBeTruthy();
    expect(screen.getAllByText("Оплата (от 12.01.2026)").length).toBeGreaterThan(0);
    expect(screen.getByText("Продажа (№4 от 20.01.2026)")).toBeTruthy();
    expect(screen.getByText(/задолженность ООО Ромашка перед ООО ЭлитПраво/)).toBeTruthy();
  });

  it("exports the accounting reconciliation act as Excel HTML", async () => {
    const createObjectUrl = vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:act");
    const revokeObjectUrl = vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    let downloadedFileName = "";
    const clickSpy = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(function click(this: HTMLAnchorElement) {
        downloadedFileName = this.download;
      });

    render(
      <ClientReconciliationAct
        clientId={1}
        clientName="ООО Ромашка"
        client={
          {
            id: 1,
            name: "ООО Ромашка",
            legalEntity: {
              id: 2,
              shortName: "ООО Ромашка",
              inn: "9876543210",
              clientsCount: 1,
            },
          } as Client
        }
      />,
    );

    await waitFor(() => expect(apiMock.getInvoices).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: /Excel/i }));

    expect(createObjectUrl).toHaveBeenCalled();
    const blob = createObjectUrl.mock.calls
      .map(([value]) => value)
      .find((value): value is Blob => value instanceof Blob && value.type === "application/vnd.ms-excel;charset=utf-8");
    if (!blob) {
      throw new Error("Excel export blob was not created");
    }
    expect(blob.type).toBe("application/vnd.ms-excel;charset=utf-8");
    const html = await blob.text();
    expect(html).toContain("По данным ООО ЭлитПраво");
    expect(html).toContain("Продажа (№15 от 10.01.2026)");
    expect(downloadedFileName).toMatch(/akt-sverki-.+\.xls/);

    clickSpy.mockRestore();
    createObjectUrl.mockRestore();
    revokeObjectUrl.mockRestore();
  });

  it("prints the reconciliation act with the fixed PDF layout", async () => {
    apiMock.getInvoices.mockResolvedValueOnce({
      items: [invoice({ amount: 10000 })],
      total: 1,
      page: 1,
      pageSize: 500,
    });
    apiMock.getPaymentsV2.mockResolvedValueOnce({
      items: [payment({ amount: 10000, paidAmount: 10000 })],
      total: 1,
      page: 1,
      pageSize: 500,
      summary: {
        income: 10000,
        expense: 0,
        balance: 10000,
        incomeCount: 1,
        expenseCount: 0,
        total: 1,
      },
    });
    apiMock.getActs.mockResolvedValueOnce({
      items: [],
      total: 0,
      page: 1,
      pageSize: 500,
    });

    render(
      <ClientReconciliationAct
        clientId={1}
        clientName="Петров Петр Петрович"
        client={
          {
            id: 1,
            name: "Петров Петр Петрович",
            legalEntity: {
              id: 2,
              shortName: "Петров Петр Петрович",
              inn: "9876543210",
              clientsCount: 1,
            },
          } as Client
        }
      />,
    );

    await waitFor(() => expect(apiMock.getInvoices).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: /PDF/i }));

    const printWindow = vi.mocked(window.open).mock.results.at(-1)?.value as Window;
    const writtenHtml = vi.mocked(printWindow.document.write).mock.calls[0][0] as string;
    expect(writtenHtml).toContain("@page { size: A4 landscape; margin: 0; }");
    expect(writtenHtml).toContain("width: 265mm");
    expect(writtenHtml).toContain("По данным ООО ЭлитПраво, руб.");
    expect(writtenHtml).toContain("Продажа (№15 от 10.01.2026)");
    expect(writtenHtml).toContain("10 000,00");
    expect(writtenHtml).toContain("<strong>на ");
    expect(writtenHtml).toContain("задолженности нет</strong>");
    expect(writtenHtml).toContain("(Петров П.П.)");
    expect(writtenHtml).toContain("(ООО ЭлитПраво)");
    expect(writtenHtml).not.toContain("(ООО Э.)");
    expect(writtenHtml).not.toContain("Покупка (№15 от 10.01.2026)");
  });
});
