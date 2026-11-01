import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { StrictMode, useState } from 'react';
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

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((finish, fail) => { resolve = finish; reject = fail; });
  return { promise, resolve, reject };
}

describe('CounterpartyPickerModal', () => {
  beforeEach(() => {
    vi.mocked(apiService.getClientsV2).mockReset();
    vi.mocked(apiService.getClients).mockReset();
    vi.mocked(apiService.getLegalEntitiesPaged).mockReset();
    vi.mocked(apiService.getLegalEntities).mockReset();
  });

  it.each([
    ['client', 'close'], ['client', 'unmount'], ['client', 'forbid'],
    ['legalEntity', 'close'], ['legalEntity', 'unmount'], ['legalEntity', 'forbid'],
  ] as const)('request cancellation baseline: aborts %s on %s and ignores its late response', async (scope, change) => {
    const clients = deferred<Client[]>(); const companies = deferred<LegalEntitySummary[]>();
    vi.mocked(apiService.getClients).mockReturnValue(clients.promise);
    vi.mocked(apiService.getLegalEntities).mockReturnValue(companies.promise);
    const props = { open: true, initialScope: scope, allowedScopes: [scope], onClose: vi.fn() };
    const view = render(<CounterpartyPickerModal {...props} />);
    const signal = scope === 'client' ? vi.mocked(apiService.getClients).mock.calls[0][0]?.signal : vi.mocked(apiService.getLegalEntities).mock.calls[0][1]?.signal;
    expect(signal).toBeInstanceOf(AbortSignal); expect(signal!.aborted).toBe(false);
    if (change === 'close') view.rerender(<CounterpartyPickerModal {...props} open={false} />);
    if (change === 'unmount') view.unmount();
    if (change === 'forbid') view.rerender(<CounterpartyPickerModal {...props} allowedScopes={[]} />);
    expect(signal!.aborted).toBe(true);
    await act(async () => { clients.resolve([createClient({ name: 'Late hidden client' })]); companies.resolve([createLegalEntity({ shortName: 'Late hidden company' })]); });
    expect(screen.queryByText(/Late hidden/)).toBeNull();
  });

  it('request cancellation regression: pending client search keeps one live request and filters its eventual response', async () => {
    const pending = deferred<Client[]>(); vi.mocked(apiService.getClients).mockReturnValue(pending.promise);
    render(<CounterpartyPickerModal open onClose={vi.fn()} />);
    const signal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal;
    fireEvent.change(screen.getByPlaceholderText(/Поиск по имени/), { target: { value: 'Zeta' } });
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 300)); });
    expect(apiService.getClients).toHaveBeenCalledOnce(); expect(signal?.aborted).toBe(false);
    await act(async () => { pending.resolve([createClient(), createClient({ id: 2, name: 'Zeta Client' })]); });
    expect(await screen.findByText('Zeta Client')).toBeTruthy(); expect(screen.queryByText('Alpha Client')).toBeNull();
  });

  it.each(['success', 'failure'] as const)('request cancellation regression: superseded legal search cannot publish its late %s', async outcome => {
    const old = deferred<LegalEntitySummary[]>(); const current = deferred<LegalEntitySummary[]>();
    vi.mocked(apiService.getLegalEntities).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    render(<CounterpartyPickerModal open initialScope="legalEntity" onClose={vi.fn()} />);
    const oldSignal = vi.mocked(apiService.getLegalEntities).mock.calls[0][1]?.signal;
    fireEvent.change(screen.getByPlaceholderText(/Поиск по названию/), { target: { value: 'Zeta' } });
    await waitFor(() => expect(apiService.getLegalEntities).toHaveBeenCalledTimes(2));
    expect(oldSignal?.aborted).toBe(true);
    const currentCall = vi.mocked(apiService.getLegalEntities).mock.calls[1];
    expect(currentCall[0]).toEqual({ search: 'Zeta' }); expect(currentCall[1]?.signal?.aborted).toBe(false);
    await act(async () => { current.resolve([createLegalEntity({ shortName: 'Zeta Current' })]); });
    await act(async () => { if (outcome === 'success') old.resolve([createLegalEntity({ id: 12, shortName: 'Zeta Obsolete' })]); else old.reject(new Error('Obsolete legal failure')); });
    expect(screen.getByText('Zeta Current')).toBeTruthy(); expect(screen.queryByText(/Zeta Obsolete|Obsolete legal failure/)).toBeNull();
  });

  it.each(['success', 'failure'] as const)('request cancellation regression: restoring client access keeps the new cache after old %s', async outcome => {
    const old = deferred<Client[]>(); const current = deferred<Client[]>();
    vi.mocked(apiService.getClients).mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    const props = { open: true, allowedScopes: ['client'] as const, onClose: vi.fn() };
    const view = render(<CounterpartyPickerModal {...props} allowedScopes={['client']} />);
    const oldSignal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal;
    view.rerender(<CounterpartyPickerModal {...props} allowedScopes={[]} />);
    expect(oldSignal?.aborted).toBe(true);
    view.rerender(<CounterpartyPickerModal {...props} allowedScopes={['client']} />);
    expect(apiService.getClients).toHaveBeenCalledTimes(2);
    expect(vi.mocked(apiService.getClients).mock.calls[1][0]?.signal).not.toBe(oldSignal);
    await act(async () => { current.resolve([createClient({ name: 'New authorized client' })]); });
    await act(async () => { if (outcome === 'success') old.resolve([createClient({ id: 2, name: 'Obsolete client' })]); else old.reject(new Error('Obsolete client failure')); });
    expect(screen.getByText('New authorized client')).toBeTruthy(); expect(screen.queryByText(/Obsolete client/)).toBeNull();
    fireEvent.change(screen.getByPlaceholderText(/Поиск по имени/), { target: { value: 'New authorized' } });
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 300)); });
    expect(apiService.getClients).toHaveBeenCalledTimes(2);
    expect(screen.getByText('New authorized client')).toBeTruthy();
  });

  it('request cancellation regression: switching tabs retains the pending client cache and aborts the obsolete company read', async () => {
    const clients = deferred<Client[]>(); const companies = deferred<LegalEntitySummary[]>();
    vi.mocked(apiService.getClients).mockReturnValue(clients.promise); vi.mocked(apiService.getLegalEntities).mockReturnValue(companies.promise);
    render(<CounterpartyPickerModal open onClose={vi.fn()} />);
    const clientSignal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal;
    fireEvent.click(screen.getByRole('button', { name: 'Юридические лица' }));
    const companySignal = vi.mocked(apiService.getLegalEntities).mock.calls[0][1]?.signal;
    expect(clientSignal?.aborted).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Физические лица' }));
    expect(companySignal?.aborted).toBe(true); expect(apiService.getClients).toHaveBeenCalledOnce();
    await act(async () => { clients.resolve([createClient()]); companies.resolve([createLegalEntity()]); });
    expect(screen.getByText('Alpha Client')).toBeTruthy(); expect(screen.queryByText('Alpha Legal')).toBeNull();
  });

  it.each(['client', 'legalEntity'] as const)('request cancellation regression: StrictMode replaces the cancelled %s request and accepts its current response', async scope => {
    const requests: Array<{ signal: AbortSignal; resolve: (rows: never[]) => void }> = [];
    const request = (signal?: AbortSignal) => new Promise<never[]>((resolve, reject) => {
      if (!signal) throw new Error('Missing request signal');
      requests.push({ signal, resolve });
      signal.addEventListener('abort', () => reject(new DOMException('Cancelled', 'AbortError')), { once: true });
    });
    vi.mocked(apiService.getClients).mockImplementation(options => request(options?.signal));
    vi.mocked(apiService.getLegalEntities).mockImplementation((_params, options) => request(options?.signal));
    const view = render(<StrictMode><CounterpartyPickerModal open initialScope={scope} onClose={vi.fn()} /></StrictMode>);
    expect(requests).toHaveLength(2); expect(requests[0].signal.aborted).toBe(true); expect(requests[1].signal.aborted).toBe(false);
    await act(async () => { requests[1].resolve([]); });
    expect(screen.getByText('Контрагенты не найдены')).toBeTruthy(); expect(screen.queryByText('Cancelled')).toBeNull();
    view.unmount(); expect(requests[1].signal.aborted).toBe(true);
  });

  it('request cancellation regression: changing counterparty role reuses the pending client request', async () => {
    const pending = deferred<Client[]>(); vi.mocked(apiService.getClients).mockReturnValue(pending.promise);
    const props = { open: true, onClose: vi.fn() };
    const view = render(<CounterpartyPickerModal {...props} counterpartyRole="client" />);
    const signal = vi.mocked(apiService.getClients).mock.calls[0][0]?.signal;
    view.rerender(<CounterpartyPickerModal {...props} counterpartyRole="supplier" />);
    expect(apiService.getClients).toHaveBeenCalledOnce(); expect(signal?.aborted).toBe(false);
    await act(async () => { pending.resolve([createClient(), createClient({ id: 2, name: 'Dual role client', isClientAndSupplier: true })]); });
    expect(screen.getByText('Dual role client')).toBeTruthy(); expect(screen.queryByText('Alpha Client')).toBeNull();
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

  it.each(['client', 'legalEntity'] as const)('lookup display errors: hides technical details for %s', async scope => {
    const request = scope === 'client' ? apiService.getClients : apiService.getLegalEntities;
    vi.mocked(request).mockRejectedValueOnce(new Error('HTTP 403 at https://internal.example/api/private-company - private server details'));
    render(<CounterpartyPickerModal open initialScope={scope} allowedScopes={[scope]} onClose={vi.fn()} />);
    expect(await screen.findByText('Недостаточно прав для выполнения действия.')).toBeTruthy();
    expect(screen.queryByText(/internal\.example|private server details/)).toBeNull();
  });
  it.each(['client', 'legalEntity'] as const)('lookup display errors: explains an expired session for %s', async scope => {
    const request = scope === 'client' ? apiService.getClients : apiService.getLegalEntities;
    vi.mocked(request).mockRejectedValueOnce(new Error('HTTP 401 at https://internal.example/api/private-company'));
    render(<CounterpartyPickerModal open initialScope={scope} allowedScopes={[scope]} onClose={vi.fn()} />);
    expect(await screen.findByText('Сессия истекла. Войдите снова.')).toBeTruthy();
  });
  it.each(['client', 'legalEntity'] as const)('lookup display errors: gives a readable network failure for %s', async scope => {
    const request = scope === 'client' ? apiService.getClients : apiService.getLegalEntities;
    vi.mocked(request).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    render(<CounterpartyPickerModal open initialScope={scope} allowedScopes={[scope]} onClose={vi.fn()} />);
    expect(await screen.findByText('Не удалось загрузить контрагентов')).toBeTruthy();
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
