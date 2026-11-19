import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { ActNumberPreview } from './ActNumberPreview';
import { apiService } from '../../services/api';
vi.mock('../../services/api', () => ({ apiService: { getActNextNumber: vi.fn() } }));
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
beforeEach(() => {
  localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', 'preview-session');
  vi.mocked(apiService.getActNextNumber).mockReset();
});
afterEach(() => { localStorage.clear(); });
it('shows a readonly provisional number for the selected act year', async () => {
  vi.mocked(apiService.getActNextNumber).mockResolvedValue({ year: 2027, nextNumber: '1' });
  render(<ActNumberPreview date="2027-01-01" ownerScope="actor-source" />);
  await waitFor(() => expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', '1'));
  expect(screen.getByLabelText('Номер акта')).toHaveProperty('readOnly', true);
  expect(screen.getByText(/Предварительный номер/)).toBeTruthy();
  expect(apiService.getActNextNumber).toHaveBeenCalledWith('2027-01-01');
});
it('keeps the assignment placeholder while loading and after a preview failure', async () => {
  vi.mocked(apiService.getActNextNumber).mockRejectedValue(new Error('Unavailable'));
  render(<ActNumberPreview date="2027-01-01" ownerScope="actor-source" />);
  await act(async () => undefined);
  expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', 'Присваивается при сохранении');
});
it.each(['date', 'owner', 'session'])('suppresses a late preview after %s changes', async (kind) => {
  const old = deferred<{ year: number; nextNumber: string }>();
  vi.mocked(apiService.getActNextNumber).mockReturnValueOnce(old.promise).mockResolvedValue({ year: kind === 'date' ? 2028 : 2027, nextNumber: '2' });
  const view = render(<ActNumberPreview date="2027-01-01" ownerScope="old" />);
  if (kind === 'session') localStorage.setItem('pp.auth-session-epoch.v1', 'replacement');
  else view.rerender(<ActNumberPreview date={kind === 'date' ? '2028-01-01' : '2027-01-01'} ownerScope="new" />);
  await act(async () => { old.resolve({ year: 2027, nextNumber: '17' }); });
  expect((screen.getByLabelText('Номер акта') as HTMLInputElement).value).not.toBe('17');
  if (kind !== 'session') await waitFor(() => expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', '2'));
});
it('does not request a number while the form is closed', () => {
  render(<ActNumberPreview date="2027-01-01" ownerScope="closed" enabled={false} />);
  expect(apiService.getActNextNumber).not.toHaveBeenCalled();
});

it.each(['date', 'owner', 'enabled'])('refreshes the preview when returning to a previous %s', async (kind) => {
  const fresh = deferred<{ year: number; nextNumber: string }>();
  const away = deferred<{ year: number; nextNumber: string }>();
  const preview = vi.mocked(apiService.getActNextNumber);
  preview.mockResolvedValueOnce({ year: 2027, nextNumber: '6' });
  if (kind !== 'enabled') preview.mockReturnValueOnce(away.promise);
  preview.mockReturnValueOnce(fresh.promise);
  const view = render(<ActNumberPreview date="2027-01-01" ownerScope="original" />);
  await waitFor(() => expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', '6'));

  view.rerender(<ActNumberPreview date={kind === 'date' ? '2028-01-01' : '2027-01-01'}
    ownerScope={kind === 'owner' ? 'other' : 'original'} enabled={kind !== 'enabled'} />);
  expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', 'Присваивается при сохранении');

  view.rerender(<ActNumberPreview date="2027-01-01" ownerScope="original" />);
  expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', 'Присваивается при сохранении');
  expect(screen.queryByText(/Предварительный номер/)).toBeNull();
  expect(preview).toHaveBeenCalledTimes(kind === 'enabled' ? 2 : 3);
  await act(async () => { fresh.resolve({ year: 2027, nextNumber: '9' }); });
  await waitFor(() => expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', '9'));
  if (kind !== 'enabled') {
    await act(async () => { away.resolve({ year: kind === 'date' ? 2028 : 2027, nextNumber: '12' }); });
    expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', '9');
  }
});
it.each(['', '0', '-1', '01', '1.0', '+1', ' 2', '2 ', '１２', '1\n', '9223372036854775808', '99999999999999999999', 11, null])(
  'ignores an invalid next number %j', async (number) => {
    vi.mocked(apiService.getActNextNumber).mockResolvedValue({ year: 2027, nextNumber: number as unknown as string });
    render(<ActNumberPreview date="2027-01-01" ownerScope="boundary" />);
    await act(async () => undefined);
    expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', 'Присваивается при сохранении');
    expect(screen.queryByText(/Предварительный номер/)).toBeNull();
  },
);
it('preserves the maximum supported annual number without numeric rounding', async () => {
  vi.mocked(apiService.getActNextNumber).mockResolvedValue({ year: 2027, nextNumber: '9223372036854775807' });
  render(<ActNumberPreview date="2027-01-01" ownerScope="boundary" />);
  await waitFor(() => expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', '9223372036854775807'));
});
it('ignores a response for another year', async () => {
  vi.mocked(apiService.getActNextNumber).mockResolvedValue({ year: 2028, nextNumber: '7' });
  render(<ActNumberPreview date="2027-01-01" ownerScope="boundary" />);
  await act(async () => undefined);
  expect(screen.getByLabelText('Номер акта')).toHaveProperty('value', 'Присваивается при сохранении');
});
