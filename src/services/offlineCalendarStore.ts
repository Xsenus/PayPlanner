import type { TaskJournalItem } from '../utils/taskJournalStore';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent, type AuthSessionIdentity } from './authService';

const DATABASE_NAME = 'payplanner-offline-calendar';
const DATABASE_VERSION = 1;
const SNAPSHOT_STORE = 'snapshots';
const PREFERENCES_STORE = 'preferences';
const SNAPSHOT_TTL_MS = 7 * 24 * 60 * 60 * 1000;
const MAX_ITEMS = 1000;
const MAX_BYTES = 10 * 1024 * 1024;
const TEXT_ENCODER = typeof TextEncoder === 'undefined' ? null : new TextEncoder();
const WRITE_GENERATION_KEY = 'payplanner-offline-calendar-write-generation';
export const OFFLINE_CALENDAR_OWNER_KEY = 'payplanner-offline-calendar-owner';
export const OFFLINE_CALENDAR_DENIED_KEY_PREFIX = 'payplanner-offline-calendar-denied:';
let writeEpoch = 0;
let activeSession: AuthSessionIdentity | null = null;
const deniedUsers = new Set<number>();
const denialEpochs = new Map<number, number>();
type WriteGeneration = { value: string; epoch: number };

// localStorage is shared by same-origin tabs; the value contains no user data.
function invalidateWrites() {
  writeEpoch += 1;
  try {
    const generation = `${Date.now()}-${Math.random()}-${Math.random()}`;
    localStorage.setItem(WRITE_GENERATION_KEY, generation);
    return { value: generation, epoch: writeEpoch };
  } catch {
    return null;
  }
}

function captureWriteGeneration() {
  try {
    const value = localStorage.getItem(WRITE_GENERATION_KEY);
    return value ? { value, epoch: writeEpoch } : invalidateWrites();
  } catch {
    return null;
  }
}

function isWriteGenerationCurrent(generation: WriteGeneration | null) {
  if (!generation || generation.epoch !== writeEpoch) return false;
  try { return localStorage.getItem(WRITE_GENERATION_KEY) === generation.value; }
  catch { return false; }
}

function isActiveOwner(userId: number) {
  try {
    return activeSession?.authenticated === true
      && isAuthSessionIdentityCurrent(activeSession)
      && localStorage.getItem(OFFLINE_CALENDAR_OWNER_KEY) === String(userId);
  }
  catch { return false; }
}

function isAccessDenied(userId: number) {
  if (deniedUsers.has(userId)) return true;
  try { return localStorage.getItem(`${OFFLINE_CALENDAR_DENIED_KEY_PREFIX}${userId}`) !== null; }
  catch { return true; }
}

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
  if (!isActiveOwner(userId)) return;
  const generation = invalidateWrites();
  if (!generation) throw new Error('Не удалось безопасно сохранить настройку офлайн-календаря.');
  const database = await openDatabase();
  if (!database) return;
  try {
    if (!isWriteGenerationCurrent(generation) || !isActiveOwner(userId)) return;
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
  const generation = captureWriteGeneration();
  if (!generation) throw new Error('Не удалось безопасно сохранить офлайн-календарь.');
  if (!isActiveOwner(userId) || isAccessDenied(userId)) return null;
  const database = await openDatabase();
  if (!database) return null;
  try {
    if (!isWriteGenerationCurrent(generation) || !isActiveOwner(userId) || isAccessDenied(userId)) return null;
    const snapshot = createOfflineCalendarSnapshot(userId, sourceItems, trustedDevice, now);
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
  if (isAccessDenied(userId)) return null;
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
    if (isAccessDenied(userId)) return null;
    return snapshot;
  } finally {
    database.close();
  }
}

export async function activateOfflineCalendarUser(userId: number, isCurrent: () => boolean = () => true) {
  if (!isCurrent()) return;
  let session: AuthSessionIdentity;
  try { session = captureAuthSessionIdentity(); } catch { return; }
  invalidateWrites();
  const database = await openDatabase();
  if (!database) return;
  try {
    for (const storeName of [SNAPSHOT_STORE, PREFERENCES_STORE]) {
      if (!isCurrent()) return;
      const transaction = database.transaction(storeName, 'readwrite');
      const done = transactionDone(transaction);
      const store = transaction.objectStore(storeName);
      const keys = await requestResult(store.getAllKeys());
      if (!isCurrent()) { transaction.abort(); await done.catch(() => undefined); return; }
      keys.filter((key) => key !== userId).forEach((key) => store.delete(key));
      await done;
    }
    if (isCurrent() && session.authenticated && isAuthSessionIdentityCurrent(session)) {
      try {
        localStorage.setItem(OFFLINE_CALENDAR_OWNER_KEY, String(userId));
        activeSession = session;
      } catch { /* Offline writes remain disabled. */ }
    }
  } finally {
    database.close();
  }
}

export async function clearOfflineCalendarSnapshot(userId: number) {
  invalidateWrites();
  const database = await openDatabase();
  if (!database) return false;
  try {
    const transaction = database.transaction(SNAPSHOT_STORE, 'readwrite');
    const done = transactionDone(transaction);
    transaction.objectStore(SNAPSHOT_STORE).delete(userId);
    await done;
    return true;
  } finally {
    database.close();
  }
}

export async function revokeOfflineCalendarSnapshot(userId: number) {
  deniedUsers.add(userId);
  denialEpochs.set(userId, (denialEpochs.get(userId) ?? 0) + 1);
  invalidateWrites();
  try { localStorage.setItem(`${OFFLINE_CALENDAR_DENIED_KEY_PREFIX}${userId}`, `${Date.now()}-${Math.random()}-${Math.random()}`); } catch { /* Current tab remains blocked. */ }
  try { await clearOfflineCalendarSnapshot(userId); } catch { /* Keep denial until the old snapshot can be removed. */ }
}

export async function restoreOfflineCalendarAccess(userId: number, isCurrent: () => boolean = () => true) {
  if (!isCurrent() || !isActiveOwner(userId)) return false;
  if (!isAccessDenied(userId)) return true;
  try {
    const epoch = denialEpochs.get(userId) ?? 0;
    const denial = localStorage.getItem(`${OFFLINE_CALENDAR_DENIED_KEY_PREFIX}${userId}`);
    if (!await clearOfflineCalendarSnapshot(userId) || !isCurrent() || !isActiveOwner(userId)) return false;
    if ((denialEpochs.get(userId) ?? 0) !== epoch || localStorage.getItem(`${OFFLINE_CALENDAR_DENIED_KEY_PREFIX}${userId}`) !== denial) return false;
    localStorage.removeItem(`${OFFLINE_CALENDAR_DENIED_KEY_PREFIX}${userId}`);
    deniedUsers.delete(userId);
    return true;
  } catch { return false; }
}

export async function clearOfflineCalendarCache(isCurrent: () => boolean = () => true) {
  if (!isCurrent()) return;
  invalidateWrites();
  const database = await openDatabase();
  if (!database) return;
  try {
    if (!isCurrent()) return;
    const transaction = database.transaction([SNAPSHOT_STORE, PREFERENCES_STORE], 'readwrite');
    const done = transactionDone(transaction);
    transaction.objectStore(SNAPSHOT_STORE).clear();
    transaction.objectStore(PREFERENCES_STORE).clear();
    await done;
    if (isCurrent()) {
      activeSession = null;
      try { localStorage.removeItem(OFFLINE_CALENDAR_OWNER_KEY); } catch { /* Offline writes remain guarded by generation. */ }
    }
  } finally {
    database.close();
  }
}
