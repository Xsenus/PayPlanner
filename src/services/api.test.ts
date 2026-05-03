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
});
