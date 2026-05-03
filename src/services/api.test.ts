import { afterEach, describe, expect, it, vi } from 'vitest';

import { apiService, extractResponseErrorMessage } from './api';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('extractResponseErrorMessage', () => {
  it('prefers problem details detail field for API errors', async () => {
    const response = new Response(
      JSON.stringify({
        title: 'Service Unavailable',
        detail: 'На сервере не найден Chromium/Chrome для генерации PDF.',
      }),
      {
        status: 503,
        headers: {
          'Content-Type': 'application/problem+json',
        },
      },
    );

    await expect(extractResponseErrorMessage(response)).resolves.toBe(
      'На сервере не найден Chromium/Chrome для генерации PDF.',
    );
  });

  it('falls back to plain text when response is not json', async () => {
    const response = new Response('Короткая текстовая ошибка', {
      status: 400,
      headers: {
        'Content-Type': 'text/plain; charset=utf-8',
      },
    });

    await expect(extractResponseErrorMessage(response)).resolves.toBe('Короткая текстовая ошибка');
  });
});

describe('apiService reports', () => {
  it('loads cash flow report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      accountName: 'Основной банк',
      openingBalance: 4000,
      income: 6000,
      expense: 1000,
      netCashFlow: 5000,
      closingBalance: 9000,
      incomeCount: 1,
      expenseCount: 1,
      bySource: [],
      byAccount: [],
      days: [],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCashFlowReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
      accountName: 'Основной банк',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/cash-flow?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
    expect(String(url)).toContain('accountName=');
  });

  it('loads cash book report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      accountName: 'Основная касса',
      openingBalance: 15000,
      income: 30000,
      expense: 12000,
      closingBalance: 33000,
      incomingCount: 3,
      outgoingCount: 2,
      days: [],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCashBook({
      from: '2026-05-01',
      to: '2026-05-31',
      accountName: 'Основная касса',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/cash-orders/cashbook?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('accountName=');
  });

  it('loads money category report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      incomeTotal: 15000,
      expenseTotal: 4000,
      netTotal: 11000,
      income: [{ categoryName: 'Услуги', amount: 15000, count: 2, share: 100 }],
      expense: [{ categoryName: 'Налоги', amount: 4000, count: 1, share: 100 }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getMoneyCategoryReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/categories?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads counterparty report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 15000,
      expenseTotal: 2000,
      netTotal: 13000,
      counterparties: [{
        counterpartyName: 'ООО Ромашка',
        income: 15000,
        expense: 2000,
        netTotal: 13000,
        totalAmount: 17000,
        incomeCount: 2,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getCounterpartyReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/counterparties?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads project report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 12000,
      expenseTotal: 3000,
      netTotal: 9000,
      projects: [{
        projectName: 'Проект Альфа',
        income: 12000,
        expense: 3000,
        netTotal: 9000,
        totalAmount: 15000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getProjectReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/projects?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads direction report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 9000,
      expenseTotal: 2000,
      netTotal: 7000,
      directions: [{
        directionName: 'Консалтинг',
        income: 9000,
        expense: 2000,
        netTotal: 7000,
        totalAmount: 11000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getDirectionReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/directions?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads sales point report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 8000,
      expenseTotal: 1500,
      netTotal: 6500,
      salesPoints: [{
        salesPointName: 'Офис',
        income: 8000,
        expense: 1500,
        netTotal: 6500,
        totalAmount: 9500,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getSalesPointReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/sales-points?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads VAT report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeAmountTotal: 12000,
      incomeVatTotal: 2000,
      expenseAmountTotal: 6000,
      expenseVatTotal: 1000,
      vatBalance: 1000,
      rates: [{
        vatRate: 20,
        vatRateLabel: '20%',
        incomeBase: 10000,
        incomeVat: 2000,
        incomeGross: 12000,
        expenseBase: 5000,
        expenseVat: 1000,
        expenseGross: 6000,
        netVat: 1000,
        incomeCount: 1,
        expenseCount: 1,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getVatReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/vat?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads plan fact report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      planIncome: 10000,
      factIncome: 8000,
      incomeVariance: -2000,
      incomeCompletionPercent: 80,
      planExpense: 4000,
      factExpense: 5000,
      expenseVariance: 1000,
      expenseExecutionPercent: 125,
      planNetTotal: 6000,
      factNetTotal: 3000,
      netVariance: -3000,
      days: [],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPlanFactReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/plan-fact?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads advance report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 16000,
      expenseTotal: 4000,
      recognizedTotal: 10000,
      unrecognizedTotal: 10000,
      recognizedCount: 1,
      unrecognizedCount: 2,
      counterparties: [{
        counterpartyName: 'ООО Ромашка',
        incomeTotal: 16000,
        expenseTotal: 0,
        recognizedTotal: 10000,
        unrecognizedTotal: 6000,
        incomeCount: 2,
        expenseCount: 0,
        recognizedCount: 1,
        unrecognizedCount: 1,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getAdvanceReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/advances?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads payment priority report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 9000,
      expenseTotal: 3000,
      netTotal: 6000,
      priorities: [{
        priority: 1,
        priorityLabel: 'Высокий приоритет',
        income: 9000,
        expense: 3000,
        netTotal: 6000,
        totalAmount: 12000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaymentPriorityReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-priorities?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads paid account report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 7000,
      expenseTotal: 2000,
      netTotal: 5000,
      accounts: [{
        paidAccountName: 'Расчетный счет',
        income: 7000,
        expense: 2000,
        netTotal: 5000,
        totalAmount: 9000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaidAccountReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/paid-accounts?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads paid invoice report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 5000,
      expenseTotal: 2000,
      netTotal: 3000,
      paidAmountTotal: 6400,
      invoices: [{
        paidInvoiceNumber: 'INV-10',
        income: 5000,
        expense: 2000,
        netTotal: 3000,
        operationTotal: 7000,
        paidAmountTotal: 6400,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaidInvoiceReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/paid-invoices?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });

  it('loads payment order report with filters', async () => {
    const payload = {
      from: '2026-05-01T00:00:00',
      to: '2026-05-31T00:00:00',
      source: 'Bank',
      incomeTotal: 6000,
      expenseTotal: 2000,
      netTotal: 4000,
      orders: [{
        paymentOrderNumber: 'PO-1',
        income: 6000,
        expense: 2000,
        netTotal: 4000,
        totalAmount: 8000,
        incomeCount: 1,
        expenseCount: 1,
        share: 100,
      }],
    };
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(payload), {
      headers: { 'content-type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiService.getPaymentOrderReport({
      from: '2026-05-01',
      to: '2026-05-31',
      source: 'Bank',
    });

    expect(result).toEqual(payload);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/reports/payment-orders?');
    expect(String(url)).toContain('from=2026-05-01');
    expect(String(url)).toContain('to=2026-05-31');
    expect(String(url)).toContain('source=Bank');
  });
});
