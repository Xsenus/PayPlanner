import { describe, expect, it } from 'vitest';

import {
  normalizeAct,
  normalizeActArray,
  normalizeActResponsibleArray,
  normalizeActsSummary,
  normalizeClientArray,
} from './acts';

describe('acts utils', () => {
  it('fills missing summary buckets with zeros', () => {
    expect(
      normalizeActsSummary({
        created: { amount: 1500, count: 2 },
        totalAmount: 1500,
      }),
    ).toEqual({
      created: { amount: 1500, count: 2 },
      transferred: { amount: 0, count: 0 },
      signed: { amount: 0, count: 0 },
      terminated: { amount: 0, count: 0 },
      totalAmount: 1500,
      totalCount: 0,
    });
  });

  it('normalizes malformed act line items safely', () => {
    expect(
      normalizeAct({
        id: 42,
        number: '42',
        date: '2026-04-15',
        amount: 1000,
        status: 'Unknown',
        lineItems: [
          null,
          {
            id: 5,
            name: 'Услуга',
            quantity: '2',
            unit: 'шт',
            price: '500',
          },
        ],
      }),
    ).toMatchObject({
      id: 42,
      status: 'Created',
      lineItems: [
        {
          id: 5,
          name: 'Услуга',
          quantity: 2,
          unit: 'шт',
          price: 500,
        },
      ],
    });
  });

  it('extracts arrays from paged-like payloads', () => {
    expect(normalizeClientArray({ items: [{ id: 1, name: 'Клиент' }] })).toHaveLength(1);
    expect(normalizeActResponsibleArray({ items: [{ id: 2, fullName: 'Ответственный' }] })).toHaveLength(1);
    expect(
      normalizeActArray({
        items: [{ id: 7, number: '7', date: '2026-04-15', amount: 0, status: 'Created' }],
      }),
    ).toHaveLength(1);
  });
});
