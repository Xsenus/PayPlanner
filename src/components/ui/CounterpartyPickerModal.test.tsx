import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { Client, LegalEntitySummary } from '../../types';
import { CounterpartyPickerModal } from './CounterpartyPickerModal';

vi.mock('../../services/api', () => ({
  apiService: {
    getClientsV2: vi.fn(),
    getClients: vi.fn(),
    getLegalEntitiesPaged: vi.fn(),
    getLegalEntities: vi.fn(),
  },
}));

function createClient(overrides: Partial<Client> = {}): Client {
  return {
    id: 1,
    name: 'Alpha Client',
    email: 'alpha@example.test',
    phone: '100',
    company: '',
    address: '',
    notes: '',
    createdAt: '2026-05-14T00:00:00Z',
    isActive: true,
    ...overrides,
  };
}

function createLegalEntity(overrides: Partial<LegalEntitySummary> = {}): LegalEntitySummary {
  return {
    id: 11,
    shortName: 'Alpha Legal',
    inn: '6300000000',
    clientsCount: 0,
    createdAt: '2026-05-14T00:00:00Z',
    ...overrides,
  };
}

describe('CounterpartyPickerModal', () => {
  beforeEach(() => {
    vi.mocked(apiService.getClientsV2).mockReset();
    vi.mocked(apiService.getClients).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntities).mockReset();
  });

  it('reuses one client list during search and refreshes it on the next opening', async () => {
    vi.mocked(apiService.getClients).mockResolvedValue([createClient({ name: 'Alpha Client' }), createClient({ id: 2, name: 'Zeta Client' })]);
    const { rerender } = render(<CounterpartyPickerModal open onClose={vi.fn()} />);
    expect(await screen.findByText('Alpha Client')).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText(/Поиск по имени/), { target: { value: 'Zeta' } });
    await waitFor(() => expect(screen.queryByText('Alpha Client')).toBeNull());
    expect(screen.getByText('Zeta Client')).toBeTruthy();
    expect(apiService.getClients).toHaveBeenCalledTimes(1);
    rerender(<CounterpartyPickerModal open={false} onClose={vi.fn()} />);
    rerender(<CounterpartyPickerModal open onClose={vi.fn()} />);
    expect(await screen.findByText('Alpha Client')).toBeTruthy();
    expect(apiService.getClients).toHaveBeenCalledTimes(2);
  });

  it('closes with Escape and returns focus to the opener', async () => {
    vi.mocked(apiService.getClients).mockResolvedValue([]);
    function Harness() {
      const [open, setOpen] = useState(false);
      return <><button type="button" onClick={() => setOpen(true)}>Открыть выбор</button>
        <CounterpartyPickerModal open={open} onClose={() => setOpen(false)} /></>;
    }
    render(<Harness />);
    const opener = screen.getByRole('button', { name: 'Открыть выбор' });
    opener.focus();
    fireEvent.click(opener);
    const search = screen.getByPlaceholderText(/Поиск по имени/);
    await waitFor(() => expect(document.activeElement).toBe(search));
    fireEvent.keyDown(search, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(document.activeElement).toBe(opener);
  });

  it('keeps Tab and Shift Tab inside the selection dialog', async () => {
    vi.mocked(apiService.getClients).mockResolvedValue([]);
    render(<CounterpartyPickerModal open onClose={vi.fn()} />);
    const dialog = screen.getByRole('dialog');
    const controls = dialog.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled])');
    const first = controls[0];
    const last = controls[controls.length - 1];
    last.focus();
    fireEvent.keyDown(last, { key: 'Tab' });
    expect(document.activeElement).toBe(first);
    fireEvent.keyDown(first, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(last);
  });

  it('handles Escape without closing a parent dialog', () => {
    vi.mocked(apiService.getClients).mockResolvedValue([]);
    const parentKeyDown = vi.fn();
    const onClose = vi.fn();
    render(<div onKeyDown={parentKeyDown}><CounterpartyPickerModal open onClose={onClose} /></div>);
    fireEvent.keyDown(screen.getByPlaceholderText(/Поиск по имени/), { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(parentKeyDown).not.toHaveBeenCalled();
  });

  it('loads clients through the stable client list without calling the paged v2 endpoint', async () => {
    vi.mocked(apiService.getClients).mockResolvedValue([
      createClient({ id: 2, name: 'Zeta Client' }),
      createClient({ id: 1, name: 'Alpha Client' }),
      createClient({ id: 3, name: 'Inactive Client', isActive: false }),
    ]);

    render(<CounterpartyPickerModal open title="Pick client" onClose={vi.fn()} />);

    expect(await screen.findByText('Alpha Client')).toBeTruthy();
    expect(screen.getByText('Zeta Client')).toBeTruthy();
    expect(screen.queryByText('Inactive Client')).toBeNull();
    expect(screen.queryByText('Failed to fetch')).toBeNull();
    expect(apiService.getClientsV2).not.toHaveBeenCalled();
    expect(apiService.getClients).toHaveBeenCalledTimes(1);
  });

  it('loads legal entities through the stable list without calling the paged endpoint', async () => {
    vi.mocked(apiService.getLegalEntities).mockResolvedValue([
      createLegalEntity({ id: 12, shortName: 'Zeta Legal' }),
      createLegalEntity({ id: 11, shortName: 'Alpha Legal' }),
    ]);

    render(
      <CounterpartyPickerModal
        open
        title="Pick legal entity"
        initialScope="legalEntity"
        allowedScopes={['legalEntity']}
        onClose={vi.fn()}
      />,
    );

    expect(await screen.findByText('Alpha Legal')).toBeTruthy();
    expect(screen.getByText('Zeta Legal')).toBeTruthy();
    expect(screen.queryByText('Failed to fetch')).toBeNull();
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
    expect(apiService.getLegalEntities).toHaveBeenCalledTimes(1);
  });

  it('does not load clients or legal entities when all scopes are forbidden', async () => {
    render(
      <CounterpartyPickerModal
        open
        title="Pick no one"
        allowedScopes={[]}
        onClose={vi.fn()}
      />,
    );

    expect(await screen.findByText('Выбор контрагентов недоступен для вашей роли.')).toBeTruthy();
    expect(screen.getByText('Нет доступных типов контрагентов')).toBeTruthy();
    expect(apiService.getClients).not.toHaveBeenCalled();
    expect(apiService.getLegalEntities).not.toHaveBeenCalled();
    expect(apiService.getClientsV2).not.toHaveBeenCalled();
    expect(apiService.getLegalEntitiesPaged).not.toHaveBeenCalled();
  });
});
