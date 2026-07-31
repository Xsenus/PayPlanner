import type { TaskJournalItem } from '../utils/taskJournalStore';

const DATABASE_NAME = 'payplanner-offline-calendar';
const DATABASE_VERSION = 1;
const SNAPSHOT_STORE = 'snapshots';
const PREFERENCES_STORE = 'preferences';
const SNAPSHOT_TTL_MS = 7 * 24 * 60 * 60 * 1000;
const MAX_ITEMS = 1000;
const MAX_BYTES = 10 * 1024 * 1024;
const TEXT_ENCODER = typeof TextEncoder === 'undefined' ? null : new TextEncoder();

export interface OfflineCalendarSnapshot {
  schemaVersion: 1;
  userId: number;
  trustedDevice: boolean;
  syncedAt: string;
  expiresAt: string;
  items: TaskJournalItem[];
}

interface OfflineCalendarPreference {
  userId: number;
  trustedDevice: boolean;
}

function hasIndexedDb() {
  return typeof indexedDB !== 'undefined';
}

function requestResult<T>(request: IDBRequest<T>) {
  return new Promise<T>((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB request failed.'));
  });
}

function transactionDone(transaction: IDBTransaction) {
  return new Promise<void>((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB transaction failed.'));
    transaction.onabort = () => reject(transaction.error ?? new Error('IndexedDB transaction aborted.'));
  });
}

async function openDatabase() {
  if (!hasIndexedDb()) return null;

  const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
  request.onupgradeneeded = () => {
    const database = request.result;
    if (!database.objectStoreNames.contains(SNAPSHOT_STORE)) {
      database.createObjectStore(SNAPSHOT_STORE, { keyPath: 'userId' });
    }
    if (!database.objectStoreNames.contains(PREFERENCES_STORE)) {
      database.createObjectStore(PREFERENCES_STORE, { keyPath: 'userId' });
    }
  };
  return requestResult(request);
}

function safeName(value: string | null | undefined, fallback = '') {
  return value?.trim().slice(0, 240) || fallback;
}

function toMinimalItem(item: TaskJournalItem, trustedDevice: boolean): TaskJournalItem {
  return {
    id: item.id,
    isInternal: item.isInternal === true,
    objectName: trustedDevice ? safeName(item.objectName, 'Задача') : 'Задача',
    title: trustedDevice ? safeName(item.title, 'Задача') : 'Задача',
    description: '',
    assigneeUserId: item.assigneeUserId ?? null,
    assigneeName: trustedDevice ? safeName(item.assigneeName) : '',
    authorUserId: null,
    authorName: '',
    dueAt: item.dueAt,
    approvalDueAt: item.approvalDueAt ?? null,
    plannedDurationMinutes: Math.max(0, item.plannedDurationMinutes || 0),
    type: item.type,
    priority: item.priority,
    status: item.status,
    isUnderControl: item.isUnderControl === true,
    result: '',
    reminders: [],
    channels: [],
    tags: [],
    participants: [],
    activeDeadlineChangeRequests: [],
    createdAt: item.createdAt,
    updatedAt: item.updatedAt,
  };
}

function serializedSize(value: unknown) {
  const json = JSON.stringify(value);
  return TEXT_ENCODER ? TEXT_ENCODER.encode(json).byteLength : json.length * 2;
}

export function createOfflineCalendarSnapshot(
  userId: number,
  sourceItems: TaskJournalItem[],
  trustedDevice: boolean,
  now = new Date(),
): OfflineCalendarSnapshot {
  const items = sourceItems
    .filter((item) => Number.isFinite(item.id) && !Number.isNaN(Date.parse(item.dueAt)))
    .sort((left, right) => Math.abs(Date.parse(left.dueAt) - now.getTime()) - Math.abs(Date.parse(right.dueAt) - now.getTime()))
    .slice(0, MAX_ITEMS)
    .map((item) => toMinimalItem(item, trustedDevice));

  const snapshot: OfflineCalendarSnapshot = {
    schemaVersion: 1,
    userId,
    trustedDevice,
    syncedAt: now.toISOString(),
    expiresAt: new Date(now.getTime() + SNAPSHOT_TTL_MS).toISOString(),
    items,
  };

  while (snapshot.items.length > 0 && serializedSize(snapshot) > MAX_BYTES) {
    snapshot.items.pop();
  }

  return snapshot;
}

export async function getOfflineCalendarTrustedDevice(userId: number) {
  const database = await openDatabase();
  if (!database) return false;
  try {
    const transaction = database.transaction(PREFERENCES_STORE, 'readonly');
    const done = transactionDone(transaction);
    const preference = await requestResult(
      transaction.objectStore(PREFERENCES_STORE).get(userId) as IDBRequest<OfflineCalendarPreference | undefined>,
    );
    await done;
    return preference?.trustedDevice === true;
  } finally {
    database.close();
  }
}

export async function setOfflineCalendarTrustedDevice(userId: number, trustedDevice: boolean) {
  const database = await openDatabase();
  if (!database) return;
  try {
    const transaction = database.transaction(PREFERENCES_STORE, 'readwrite');
    const done = transactionDone(transaction);
    transaction.objectStore(PREFERENCES_STORE).put({ userId, trustedDevice } satisfies OfflineCalendarPreference);
    await done;
  } finally {
    database.close();
  }
}

export async function saveOfflineCalendarSnapshot(
  userId: number,
  sourceItems: TaskJournalItem[],
  trustedDevice: boolean,
  now = new Date(),
) {
  const database = await openDatabase();
  if (!database) return null;
  const snapshot = createOfflineCalendarSnapshot(userId, sourceItems, trustedDevice, now);
  try {
    const transaction = database.transaction(SNAPSHOT_STORE, 'readwrite');
    const done = transactionDone(transaction);
    transaction.objectStore(SNAPSHOT_STORE).put(snapshot);
    await done;
    return snapshot;
  } finally {
    database.close();
  }
}

export async function loadOfflineCalendarSnapshot(userId: number, now = new Date()) {
  const database = await openDatabase();
  if (!database) return null;
  try {
    const transaction = database.transaction(SNAPSHOT_STORE, 'readwrite');
    const done = transactionDone(transaction);
    const store = transaction.objectStore(SNAPSHOT_STORE);
    const snapshot = await requestResult(
      store.get(userId) as IDBRequest<OfflineCalendarSnapshot | undefined>,
    );
    if (!snapshot || snapshot.schemaVersion !== 1 || Date.parse(snapshot.expiresAt) <= now.getTime()) {
      if (snapshot) store.delete(userId);
      await done;
      return null;
    }
    await done;
    return snapshot;
  } finally {
    database.close();
  }
}

export async function activateOfflineCalendarUser(userId: number) {
  const database = await openDatabase();
  if (!database) return;
  try {
    for (const storeName of [SNAPSHOT_STORE, PREFERENCES_STORE]) {
      const transaction = database.transaction(storeName, 'readwrite');
      const done = transactionDone(transaction);
      const store = transaction.objectStore(storeName);
      const keys = await requestResult(store.getAllKeys());
      keys.filter((key) => key !== userId).forEach((key) => store.delete(key));
      await done;
    }
  } finally {
    database.close();
  }
}

export async function clearOfflineCalendarSnapshot(userId: number) {
  const database = await openDatabase();
  if (!database) return;
  try {
    const transaction = database.transaction(SNAPSHOT_STORE, 'readwrite');
    const done = transactionDone(transaction);
    transaction.objectStore(SNAPSHOT_STORE).delete(userId);
    await done;
  } finally {
    database.close();
  }
}

export async function clearOfflineCalendarCache() {
  const database = await openDatabase();
  if (!database) return;
  try {
    const transaction = database.transaction([SNAPSHOT_STORE, PREFERENCES_STORE], 'readwrite');
    const done = transactionDone(transaction);
    transaction.objectStore(SNAPSHOT_STORE).clear();
    transaction.objectStore(PREFERENCES_STORE).clear();
    await done;
  } finally {
    database.close();
  }
}
