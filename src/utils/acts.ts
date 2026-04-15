import type { Act, ActLineItem, ActResponsible, ActStatus, ActsSummary, Client } from '../types';

const ACT_STATUSES: ActStatus[] = ['Created', 'Transferred', 'Signed', 'Terminated'];

const EMPTY_SUMMARY: ActsSummary = {
  created: { amount: 0, count: 0 },
  transferred: { amount: 0, count: 0 },
  signed: { amount: 0, count: 0 },
  terminated: { amount: 0, count: 0 },
  totalAmount: 0,
  totalCount: 0,
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function toFiniteNumber(value: unknown, fallback = 0): number {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function getItemsArray<T>(value: unknown): T[] {
  if (Array.isArray(value)) {
    return value.filter(Boolean) as T[];
  }

  if (isRecord(value) && Array.isArray(value.items)) {
    return value.items.filter(Boolean) as T[];
  }

  return [];
}

function normalizeBucket(value: unknown): { amount: number; count: number } {
  if (!isRecord(value)) {
    return { amount: 0, count: 0 };
  }

  return {
    amount: toFiniteNumber(value.amount),
    count: Math.max(0, Math.trunc(toFiniteNumber(value.count))),
  };
}

function isActStatus(value: unknown): value is ActStatus {
  return typeof value === 'string' && ACT_STATUSES.includes(value as ActStatus);
}

function normalizeActLineItem(value: unknown): ActLineItem | null {
  if (!isRecord(value)) {
    return null;
  }

  return {
    id: typeof value.id === 'number' ? value.id : null,
    invoiceLineItemId: typeof value.invoiceLineItemId === 'number' ? value.invoiceLineItemId : null,
    sourceKey: typeof value.sourceKey === 'string' ? value.sourceKey : '',
    sourceSortOrder: typeof value.sourceSortOrder === 'number' ? value.sourceSortOrder : null,
    name: typeof value.name === 'string' ? value.name : '',
    quantity: toFiniteNumber(value.quantity),
    unit: typeof value.unit === 'string' ? value.unit : '',
    price: toFiniteNumber(value.price),
  };
}

export function normalizeActsSummary(value: unknown): ActsSummary {
  if (!isRecord(value)) {
    return { ...EMPTY_SUMMARY };
  }

  return {
    created: normalizeBucket(value.created),
    transferred: normalizeBucket(value.transferred),
    signed: normalizeBucket(value.signed),
    terminated: normalizeBucket(value.terminated),
    totalAmount: toFiniteNumber(value.totalAmount),
    totalCount: Math.max(0, Math.trunc(toFiniteNumber(value.totalCount))),
  };
}

export function normalizeClientArray(value: unknown): Client[] {
  return getItemsArray<Client>(value);
}

export function normalizeActResponsibleArray(value: unknown): ActResponsible[] {
  return getItemsArray<ActResponsible>(value);
}

export function normalizeAct(value: unknown): Act | null {
  if (!isRecord(value)) {
    return null;
  }

  const lineItems = getItemsArray<unknown>(value.lineItems)
    .map((item) => normalizeActLineItem(item))
    .filter((item): item is ActLineItem => item !== null);

  return {
    ...(value as Act),
    status: isActStatus(value.status) ? value.status : 'Created',
    lineItems,
  };
}

export function normalizeActArray(value: unknown): Act[] {
  return getItemsArray<unknown>(value)
    .map((item) => normalizeAct(item))
    .filter((item): item is Act => item !== null);
}
