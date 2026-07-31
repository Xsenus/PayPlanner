import { IDBFactory } from 'fake-indexeddb';
import { beforeEach, describe, expect, it } from 'vitest';

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
  beforeEach(() => {
    Object.defineProperty(globalThis, 'indexedDB', {
      configurable: true,
      value: new IDBFactory(),
    });
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
});
