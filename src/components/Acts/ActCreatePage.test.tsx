import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { ActCreatePage } from './ActCreatePage';
import { apiService } from '../../services/api';
import type { Act, ActInput } from '../../types';
const state = vi.hoisted(() => ({ canCreate: true, success: vi.fn() }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: 1 } } }) }));
vi.mock('../../hooks/useRolePermissions', () => ({ useRolePermissions: () => ({ acts: { canCreate: state.canCreate }, accounts: { canView: true }, clients: { canView: true } }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: state.success }) }));
vi.mock('../../services/api', () => ({ apiService: { createAct: vi.fn(), getClients: vi.fn(), getActResponsibles: vi.fn() } }));
const key = '11111111-1111-4111-8111-111111111111';
const command: ActInput = { number: '', date: '2027-01-01', amount: 100, status: 'Created', clientId: 7 };
vi.mock('./ActModal', () => ({ ActModal: (props: { onSubmit: (body: ActInput, options: { attemptKey: string }) => Promise<unknown>; errorMessage?: string; submitting?: boolean }) => (
  <div><button disabled={props.submitting} onClick={() => { void props.onSubmit(command, { attemptKey: key }).catch(() => undefined); }}>Сохранить акт</button>{props.errorMessage && <p role="alert">{props.errorMessage}</p>}</div>
) }));
const saved = { id: 91, number: '1', date: '2027-01-01', title: 'Серверное название', amount: 100, status: 'Created', clientId: 7 } as Act;
beforeEach(() => {
  state.canCreate = true; state.success.mockReset();
  vi.mocked(apiService.createAct).mockReset();
  vi.mocked(apiService.getClients).mockResolvedValue([]);
  vi.mocked(apiService.getActResponsibles).mockResolvedValue([]);
  localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', 'page-session');
});
afterEach(() => { localStorage.clear(); });
it('forwards the durable attempt and adopts the canonical server number, date and title', async () => {
  vi.mocked(apiService.createAct).mockResolvedValue(saved);
  const onCreated = vi.fn(); render(<ActCreatePage onBack={vi.fn()} onCreated={onCreated} defaultClientId={7} />);
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить акт' }));
  await waitFor(() => expect(onCreated).toHaveBeenCalledWith(expect.objectContaining({ id: 91, number: '1', date: '2027-01-01', title: 'Серверное название' })));
  expect(apiService.createAct).toHaveBeenCalledWith(command, { attemptKey: key });
});
it.each(['session', 'permission', 'source'].flatMap(boundary => ['success', 'error'].map(outcome => [boundary, outcome])))('suppresses late %s/%s UI adoption', async (boundary, outcome) => {
  let resolve!: (act: Act) => void;
  let reject!: (error: Error) => void;
  vi.mocked(apiService.createAct).mockReturnValue(new Promise<Act>((done, failed) => { resolve = done; reject = failed; }));
  const onCreated = vi.fn(); const onBack = vi.fn();
  const view = render(<ActCreatePage onBack={onBack} onCreated={onCreated} defaultClientId={7} />);
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить акт' }));
  await waitFor(() => expect(apiService.createAct).toHaveBeenCalledOnce());
  if (boundary === 'session') localStorage.setItem('pp.auth-session-epoch.v1', 'replacement');
  if (boundary === 'permission') state.canCreate = false;
  if (boundary !== 'session') view.rerender(<ActCreatePage onBack={onBack} onCreated={onCreated} defaultClientId={boundary === 'source' ? 8 : 7} />);
  await act(async () => { if (outcome === 'success') resolve(saved); else reject(new Error('Старая ошибка')); });
  expect(onCreated).not.toHaveBeenCalled();
  expect(state.success).not.toHaveBeenCalled();
  expect(screen.queryByText('Старая ошибка')).toBeNull();
  if (boundary === 'source') expect(screen.getByRole('button', { name: 'Сохранить акт' })).toHaveProperty('disabled', false);
});
