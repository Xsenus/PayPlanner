import { documentCopyTestSession } from '../../test/documentCopySession';
import { afterEach as restoreAfterEach, beforeEach as setupBeforeEach } from 'vitest';
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: 4 } } }) }));
let session: ReturnType<typeof documentCopyTestSession>;
setupBeforeEach(() => { session = documentCopyTestSession(); });
restoreAfterEach(() => session.restore());
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { ClientCase } from '../../types';
import { CasePreHearingActions } from './CasePreHearingActions';

vi.mock('../../services/api', () => ({
  apiService: { createTaskJournalItem: vi.fn() },
}));

const caseItem = {
  id: 42,
  clientId: 6,
  title: 'Судебное дело',
  status: 'Open',
  responsibleUserId: 8,
  nextHearingAt: '2099-10-20T15:00:00Z',
} as ClientCase;

describe('CasePreHearingActions', () => {
  beforeEach(() => {
    vi.mocked(apiService.createTaskJournalItem).mockReset();
    vi.mocked(apiService.createTaskJournalItem).mockResolvedValue({ id: 91, clientCaseId: 42 } as never);
  });

  it('creates a linked task and offers direct navigation', async () => {
    const onTaskCreated = vi.fn();
    const onOpenTask = vi.fn();
    render(<CasePreHearingActions caseItem={caseItem} timeZoneId="Asia/Novosibirsk" canCreateTask onTaskCreated={onTaskCreated} onOpenTask={onOpenTask} />);

    fireEvent.click(screen.getByRole('button', { name: 'Запросить доверенность' }));

    await waitFor(() => expect(apiService.createTaskJournalItem).toHaveBeenCalledWith(expect.objectContaining({
      clientCaseId: 42,
      assigneeUserId: 8,
      title: 'Оформить доверенность для участия в заседании',
    })));
    expect(onTaskCreated).toHaveBeenCalledWith(91);
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть задачу' }));
    expect(onOpenTask).toHaveBeenCalledWith(91);
  });

  it('requires saved dates and a responsible, and hides actions without task access', () => {
    const view = render(<CasePreHearingActions caseItem={caseItem} timeZoneId="UTC" canCreateTask datesSaved={false} />);
    expect(screen.getByRole('button', { name: 'Запланировать командировку' }).hasAttribute('disabled')).toBe(true);
    expect(screen.getByText(/Сначала сохраните изменённую дату/)).toBeTruthy();

    view.rerender(<CasePreHearingActions caseItem={{ ...caseItem, responsibleUserId: null }} timeZoneId="UTC" canCreateTask />);
    expect(screen.getByText(/Сначала назначьте ответственного/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Запросить доверенность' }).hasAttribute('disabled')).toBe(true);

    view.rerender(<CasePreHearingActions caseItem={caseItem} timeZoneId="UTC" canCreateTask={false} />);
    expect(screen.queryByRole('button', { name: 'Запросить доверенность' })).toBeNull();
    expect(apiService.createTaskJournalItem).not.toHaveBeenCalled();
  });
});
