import { IDBFactory } from 'fake-indexeddb';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  activateOfflineCalendarUser,
  clearOfflineCalendarCache,
  createOfflineCalendarSnapshot,
  getOfflineCalendarTrustedDevice,
  loadOfflineCalendarSnapshot,
  saveOfflineCalendarSnapshot,
  setOfflineCalendarTrustedDevice,
} from './offlineCalendarStore';
import type { TaskJournalItem } from '../utils/taskJournalStore';

function task(id: number): TaskJournalItem {
  return {
    id,
    clientId: 25,
    clientName: 'Секретный клиент',
    clientCaseId: 50,
    clientCaseTitle: 'Секретное дело',
    objectName: `Объект ${id}`,
    title: `Задача ${id}`,
    description: 'Чувствительное описание',
    assigneeUserId: 7,
    assigneeName: 'Иванов Иван',
    authorUserId: 8,
    authorName: 'Петров Пётр',
    dueAt: new Date(Date.UTC(2026, 6, 31, 10, id % 60)).toISOString(),
    plannedDurationMinutes: 60,
    type: 'task',
    priority: 'normal',
    status: 'new',
    result: 'Чувствительный результат',
    reminders: ['before1h'],
    channels: ['email'],
    tags: ['тайна'],
    participants: [{ role: 'observer', displayName: 'Наблюдатель' }],
    createdAt: '2026-07-30T10:00:00.000Z',
    updatedAt: '2026-07-30T11:00:00.000Z',
  };
}

describe('offlineCalendarStore', () => {
  beforeEach(async () => {
    localStorage.setItem('pp_token', `test.${btoa(JSON.stringify({ sub: '7' }))}.test`);
    localStorage.setItem('pp.auth-session-epoch.v1', 'test-session');
    Object.defineProperty(globalThis, 'indexedDB', {
      configurable: true,
      value: new IDBFactory(),
    });
    await activateOfflineCalendarUser(7);
  });

  it('сохраняет только безопасные нейтральные данные для обычного устройства', () => {
    const snapshot = createOfflineCalendarSnapshot(7, [task(1)], false, new Date('2026-07-31T00:00:00.000Z'));

    expect(snapshot.expiresAt).toBe('2026-08-07T00:00:00.000Z');
    expect(snapshot.items[0]).toMatchObject({
      title: 'Задача',
      objectName: 'Задача',
      description: '',
      result: '',
      assigneeName: '',
      authorName: '',
      reminders: [],
      channels: [],
      tags: [],
      participants: [],
    });
    expect(JSON.stringify(snapshot)).not.toContain('Секрет');
    expect(JSON.stringify(snapshot)).not.toContain('Наблюдатель');
  });

  it('сохраняет названия только для явно доверенного устройства', () => {
    const snapshot = createOfflineCalendarSnapshot(7, [task(1)], true);

    expect(snapshot.items[0].title).toBe('Задача 1');
    expect(snapshot.items[0].objectName).toBe('Объект 1');
    expect(snapshot.items[0].assigneeName).toBe('Иванов Иван');
    expect(snapshot.items[0].description).toBe('');
    expect(snapshot.items[0].clientId).toBeUndefined();
    expect(snapshot.items[0].clientCaseId).toBeUndefined();
  });

  it('ограничивает снимок тысячей ближайших событий', () => {
    const snapshot = createOfflineCalendarSnapshot(
      7,
      Array.from({ length: 1200 }, (_, index) => task(index + 1)),
      false,
    );

    expect(snapshot.items).toHaveLength(1000);
  });

  it('сохраняет ближайшие задачи и за пределами текущей недели', () => {
    const now = new Date('2026-08-10T00:00:00.000Z');
    const earlier = { ...task(1), dueAt: '2026-07-01T00:00:00.000Z' };
    const nearer = { ...task(2), dueAt: '2026-08-20T00:00:00.000Z' };
    const snapshot = createOfflineCalendarSnapshot(7, [earlier, nearer], false, now);

    expect(snapshot.items.map((item) => item.id)).toEqual([2, 1]);
  });

  it('соблюдает ограничение размера в байтах и отбрасывает наиболее далёкие задачи', () => {
    const now = new Date('2026-08-10T00:00:00.000Z');
    const items = Array.from({ length: 11 }, (_, index) => ({
      ...task(index + 1),
      dueAt: new Date(now.getTime() + index * 60_000).toISOString(),
      status: 'я'.repeat(512 * 1024) as TaskJournalItem['status'],
    }));
    const snapshot = createOfflineCalendarSnapshot(7, items, false, now);

    expect(new TextEncoder().encode(JSON.stringify(snapshot)).byteLength).toBeLessThanOrEqual(10 * 1024 * 1024);
    expect(snapshot.items.map((item) => item.id)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9]);
  });

  it('при 1001 задаче оставляет тысячу ближайших к моменту сохранения', () => {
    const now = new Date('2026-08-10T00:00:00.000Z');
    const items = Array.from({ length: 1001 }, (_, index) => ({
      ...task(index + 1),
      dueAt: new Date(now.getTime() + (1000 - index) * 60_000).toISOString(),
    }));
    const snapshot = createOfflineCalendarSnapshot(7, items, false, now);

    expect(snapshot.items).toHaveLength(1000);
    expect(snapshot.items[0].id).toBe(1001);
    expect(snapshot.items.some((item) => item.id === 1)).toBe(false);
  });

  it('записывает, читает и удаляет просроченный снимок IndexedDB', async () => {
    await saveOfflineCalendarSnapshot(7, [task(1)], false, new Date('2026-07-31T00:00:00.000Z'));

    expect((await loadOfflineCalendarSnapshot(7, new Date('2026-08-06T23:59:00.000Z')))?.items).toHaveLength(1);
    expect(await loadOfflineCalendarSnapshot(7, new Date('2026-08-07T00:00:00.000Z'))).toBeNull();
  });

  it('изолирует пользователей и очищает прежний снимок при смене пользователя', async () => {
    await saveOfflineCalendarSnapshot(7, [task(1)], false);
    await setOfflineCalendarTrustedDevice(7, true);
    await activateOfflineCalendarUser(8);

    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    expect(await getOfflineCalendarTrustedDevice(7)).toBe(false);

    await saveOfflineCalendarSnapshot(8, [task(2)], false);
    await clearOfflineCalendarCache();
    expect(await loadOfflineCalendarSnapshot(8)).toBeNull();
  });

  it('не начинает отложенную очистку A после переключения на B во время открытия БД', async () => {
    await activateOfflineCalendarUser(8);
    await saveOfflineCalendarSnapshot(8, [task(2)], false);
    await setOfflineCalendarTrustedDevice(8, true);
    let current = true;
    const oldCleanup = clearOfflineCalendarCache(() => current);
    current = false;
    await oldCleanup;
    expect((await loadOfflineCalendarSnapshot(8))?.items).toHaveLength(1);
    expect(await getOfflineCalendarTrustedDevice(8)).toBe(true);
  });

  it('не восстанавливает снимок после выхода, если его сохранение задержалось при открытии БД', async () => {
    let release!: () => void;
    let opened!: () => void;
    const ready = new Promise<void>((resolve) => { opened = resolve; });
    const originalOpen = indexedDB.open.bind(indexedDB);
    const spy = vi.spyOn(indexedDB, 'open').mockImplementationOnce((name, version) => {
      const request = originalOpen(name, version);
      return new Proxy(request, {
        get(target, key) { return Reflect.get(target, key, target); },
        set(target, key, value) {
          if (key === 'onsuccess') {
            target.onsuccess = (event) => { release = () => value.call(target, event); opened(); };
            return true;
          }
          return Reflect.set(target, key, value, target);
        },
      });
    });
    try {
      const pending = saveOfflineCalendarSnapshot(7, [task(1)], true);
      await ready;
      await clearOfflineCalendarCache();
      release();
      await pending;
      expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    } finally {
      spy.mockRestore();
    }
  });

  it('не записывает старый доверенный снимок после отключения доверенного устройства', async () => {
    let release!: () => void;
    let opened!: () => void;
    const ready = new Promise<void>((resolve) => { opened = resolve; });
    const originalOpen = indexedDB.open.bind(indexedDB);
    const spy = vi.spyOn(indexedDB, 'open').mockImplementationOnce((name, version) => {
      const request = originalOpen(name, version);
      return new Proxy(request, {
        get(target, key) { return Reflect.get(target, key, target); },
        set(target, key, value) {
          if (key === 'onsuccess') {
            target.onsuccess = (event) => { release = () => value.call(target, event); opened(); };
            return true;
          }
          return Reflect.set(target, key, value, target);
        },
      });
    });
    try {
      const pending = saveOfflineCalendarSnapshot(7, [task(1)], true);
      await ready;
      await setOfflineCalendarTrustedDevice(7, false);
      await saveOfflineCalendarSnapshot(7, [task(2)], false);
      release();
      await pending;
      const snapshot = await loadOfflineCalendarSnapshot(7);
      expect(snapshot?.trustedDevice).toBe(false);
      expect(snapshot?.items.map((item) => item.id)).toEqual([2]);
      expect(snapshot?.items[0].title).toBe('Задача');
    } finally {
      spy.mockRestore();
    }
  });

  it('не восстанавливает признак доверенного устройства после очистки', async () => {
    let release!: () => void;
    let opened!: () => void;
    const ready = new Promise<void>((resolve) => { opened = resolve; });
    const originalOpen = indexedDB.open.bind(indexedDB);
    const spy = vi.spyOn(indexedDB, 'open').mockImplementationOnce((name, version) => {
      const request = originalOpen(name, version);
      return new Proxy(request, {
        get(target, key) { return Reflect.get(target, key, target); },
        set(target, key, value) {
          if (key === 'onsuccess') {
            target.onsuccess = (event) => { release = () => value.call(target, event); opened(); };
            return true;
          }
          return Reflect.set(target, key, value, target);
        },
      });
    });
    try {
      const pending = setOfflineCalendarTrustedDevice(7, true);
      await ready;
      await clearOfflineCalendarCache();
      release();
      await pending;
      expect(await getOfflineCalendarTrustedDevice(7)).toBe(false);
    } finally {
      spy.mockRestore();
    }
  });

  it('не сохраняет данные, если общая защита записи недоступна', async () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('storage denied'); });
    try {
      await expect(saveOfflineCalendarSnapshot(7, [task(1)], true)).rejects.toThrow('безопасно сохранить');
    } finally {
      spy.mockRestore();
    }
    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
  });

  it('не разрешает старой вкладке создать снимок после выхода или смены пользователя', async () => {
    await activateOfflineCalendarUser(7);
    await clearOfflineCalendarCache();
    await saveOfflineCalendarSnapshot(7, [task(1)], true);
    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    await activateOfflineCalendarUser(8);
    await saveOfflineCalendarSnapshot(7, [task(1)], true);
    await setOfflineCalendarTrustedDevice(7, true);
    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    expect(await getOfflineCalendarTrustedDevice(7)).toBe(false);
    await saveOfflineCalendarSnapshot(8, [task(2)], false);
    expect((await loadOfflineCalendarSnapshot(8))?.items.map((item) => item.id)).toEqual([2]);
  });

  it('не разрешает старой сессии запись после повторного входа того же пользователя', async () => {
    localStorage.setItem('pp.auth-session-epoch.v1', 'new-session');
    await saveOfflineCalendarSnapshot(7, [task(1)], true);
    await setOfflineCalendarTrustedDevice(7, true);
    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    expect(await getOfflineCalendarTrustedDevice(7)).toBe(false);
    await activateOfflineCalendarUser(7);
    await saveOfflineCalendarSnapshot(7, [task(2)], false);
    expect((await loadOfflineCalendarSnapshot(7))?.items.map((item) => item.id)).toEqual([2]);
  });

  it('сохраняет запрет чтения после отказа удаления и снимает его только после успешной очистки', async () => {
    const store = await import('./offlineCalendarStore');
    await saveOfflineCalendarSnapshot(7, [task(1)], true);
    const failOpen = vi.spyOn(indexedDB, 'open').mockImplementationOnce(() => { throw new Error('storage unavailable'); });
    await store.revokeOfflineCalendarSnapshot(7);
    failOpen.mockRestore();
    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    expect(await saveOfflineCalendarSnapshot(7, [task(2)], true)).toBeNull();
    const failRestore = vi.spyOn(indexedDB, 'open').mockImplementationOnce(() => { throw new Error('storage unavailable'); });
    expect(await store.restoreOfflineCalendarAccess(7)).toBe(false);
    failRestore.mockRestore();
    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    expect(await store.restoreOfflineCalendarAccess(7)).toBe(true);
    expect(await loadOfflineCalendarSnapshot(7)).toBeNull();
    await saveOfflineCalendarSnapshot(7, [task(3)], false);
    expect((await loadOfflineCalendarSnapshot(7))?.items.map((item) => item.id)).toEqual([3]);
  });

  it('не снимает новый запрет запоздавшим восстановлением доступа', async () => {
    const store = await import('./offlineCalendarStore');
    await store.revokeOfflineCalendarSnapshot(7);
    let release!: () => void;
    let opened!: () => void;
    const ready = new Promise<void>((resolve) => { opened = resolve; });
    const originalOpen = indexedDB.open.bind(indexedDB);
    const spy = vi.spyOn(indexedDB, 'open').mockImplementationOnce((name, version) => {
      const request = originalOpen(name, version);
      return new Proxy(request, {
        get(target, key) { return Reflect.get(target, key, target); },
        set(target, key, value) {
          if (key === 'onsuccess') {
            target.onsuccess = (event) => { release = () => value.call(target, event); opened(); };
            return true;
          }
          return Reflect.set(target, key, value, target);
        },
      });
    });
    try {
      const oldRestore = store.restoreOfflineCalendarAccess(7);
      await ready;
      await store.revokeOfflineCalendarSnapshot(7);
      release();
      expect(await oldRestore).toBe(false);
      expect(await saveOfflineCalendarSnapshot(7, [task(1)], true)).toBeNull();
      expect(await store.restoreOfflineCalendarAccess(7)).toBe(true);
    } finally {
      spy.mockRestore();
    }
  });

  it('прерывает старую activation A перед delete после await getAllKeys и сохраняет кеш B', async () => {
    await activateOfflineCalendarUser(8);
    await saveOfflineCalendarSnapshot(8, [task(2)], false);
    await setOfflineCalendarTrustedDevice(8, true);
    let checks = 0;
    await activateOfflineCalendarUser(7, () => ++checks === 1);
    expect(checks).toBe(2);
    expect((await loadOfflineCalendarSnapshot(8))?.items).toHaveLength(1);
    expect(await getOfflineCalendarTrustedDevice(8)).toBe(true);
  });
});
