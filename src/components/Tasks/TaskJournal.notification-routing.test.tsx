import { setupTaskJournalTests, apiMock, createTask, renderJournal } from './TaskJournal.test-support';
import { fireEvent, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

describe('completed notification task route', () => {
  setupTaskJournalTests();

  it.each(['legacy', 'modern'] as const)('opens the exact completed notification task in the configured %s card', async taskCardWindowMode => {
    const task = createTask({ id: 505, title: 'Завершённая работа из уведомления', status: 'done', result: 'Документы подготовлены' });
    await renderJournal([], { withRoute: true, nextRouteTaskId: task.id, waitForTitle: null, settings: { taskCardWindowMode } });
    apiMock.getTaskJournalItem.mockResolvedValue(task);
    fireEvent.click(screen.getByRole('button', { name: 'Перейти в другую задачу' }));
    const card = await screen.findByTestId(`task-${taskCardWindowMode}-card-window`);
    expect(apiMock.getTaskJournalItem).toHaveBeenCalledWith(505);
    expect(within(card).getByDisplayValue('Завершённая работа из уведомления')).toBeTruthy();
  });
});
