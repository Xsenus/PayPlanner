import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { useActCreateRecovery, type ActSubmit } from './ActCreateRecovery';
import { createActGuarded, pendingActCreates } from '../../services/actCreateAttempts';
import type { Act, ActInput } from '../../types';

const key = '11111111-1111-4111-8111-111111111111';
const command: ActInput = { number: '', date: '2027-01-01', amount: 125, status: 'Created', clientId: 7, title: 'Исходная команда' };
const saved = { id: 91, number: '1', date: command.date, amount: 125, status: 'Created', clientId: 7 } as Act;
const missing = () => Object.assign(new Error('HTTP404'), { status: 404 });
function Harness({ onSubmit, scope = 'actor/source' }: { onSubmit: ActSubmit; scope?: string }) {
  const [amount, setAmount] = useState(125);
  const creation = useActCreateRecovery(true, scope, onSubmit, body => setAmount(body.amount));
  return <div>{creation.panel}<input aria-label="Сумма черновика" type="number" value={amount} onChange={event => setAmount(Number(event.target.value))} />
    <button disabled={creation.blocked || creation.busy} onClick={() => { void creation.submit({ ...command, amount }).catch(() => undefined); }}>Сохранить черновик</button></div>;
}
async function seedUnknown() {
  await expect(createActGuarded(command, { attemptKey: key }, {
    commit: vi.fn().mockRejectedValue(new Error('Потерян ответ')), lookup: vi.fn().mockRejectedValue(missing()),
  })).rejects.toThrow();
}
beforeEach(() => {
  localStorage.clear(); sessionStorage.clear();
  localStorage.setItem('pp_token', `synthetic.${btoa(JSON.stringify({ sub: '7' }))}.signature`);
  localStorage.setItem('pp.auth-session-epoch.v1', 'recovery-session');
});
afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); sessionStorage.clear(); });

describe('act recovery form lifecycle', () => {
  it('distinguishes blank-title drafts by invoice, owner and amount using metadata only', async () => {
    const firstKey = '11111111-1111-4111-8111-111111111111';
    const secondKey = '22222222-2222-4222-8222-222222222222';
    const io = { commit: vi.fn().mockRejectedValue(new Error('Потерян ответ')), lookup: vi.fn().mockRejectedValue(missing()) };
    const first = { ...command, title: '', amount: 125, invoiceId: 618, invoiceNumber: 'INV-A', clientId: 7, legalEntityId: null };
    const second = { ...command, title: '', amount: 250, invoiceId: 619, invoiceNumber: 'INV-B', clientId: null, legalEntityId: 9 };
    await expect(createActGuarded(first, { attemptKey: firstKey }, io)).rejects.toThrow();
    await expect(createActGuarded(second, { attemptKey: secondKey }, io)).rejects.toThrow();
    const reads = vi.spyOn(Storage.prototype, 'getItem');
    const submit = vi.fn().mockResolvedValue(undefined);
    render(<Harness onSubmit={submit} />);
    const firstRow = screen.getByText(/Счёт №INV-A #618/).parentElement!.parentElement!;
    const secondRow = screen.getByText(/Счёт №INV-B #619/).parentElement!.parentElement!;
    expect(firstRow.textContent).toContain('Клиент #7');
    expect(firstRow.textContent).toMatch(/125/);
    expect(secondRow.textContent).toContain('Организация #9');
    expect(secondRow.textContent).toMatch(/250/);
    expect(reads.mock.calls.some(([name]) => name.endsWith(':command'))).toBe(false);
    fireEvent.click(within(secondRow).getByRole('button', { name: 'Повторить сохранение' }));
    await waitFor(() => expect(submit).toHaveBeenCalledWith(second, expect.objectContaining({ attemptKey: secondKey, retry: true })));
  });
  it('shows legacy v1 metadata without source fields and can check the same saved attempt', async () => {
    await seedUnknown();
    const metaKey = Object.keys(sessionStorage).find(name => name.endsWith(':meta'))!;
    const legacy = JSON.parse(sessionStorage.getItem(metaKey)!);
    for (const field of ['amount', 'creationMode', 'invoiceNumber', 'invoiceId', 'clientId', 'legalEntityId']) delete legacy[field];
    sessionStorage.setItem(metaKey, JSON.stringify(legacy));
    const submit = vi.fn().mockResolvedValue(undefined);
    render(<Harness onSubmit={submit} />);
    expect(screen.getByText(/Результат не подтверждён/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Проверить результат' }));
    await waitFor(() => expect(submit).toHaveBeenCalledWith(command, expect.objectContaining({ attemptKey: key, lookupOnly: true })));
  });
  it('labels a full-invoice server-calculated amount without presenting it as a zero act', async () => {
    const io = { commit: vi.fn().mockRejectedValue(new Error('Потерян ответ')), lookup: vi.fn().mockRejectedValue(missing()) };
    await expect(createActGuarded({ ...command, amount: 0, creationMode: 'Full', invoiceId: 618, invoiceNumber: 'INV-A' }, { attemptKey: key }, io)).rejects.toThrow();
    render(<Harness onSubmit={vi.fn().mockResolvedValue(undefined)} />);
    expect(screen.getByText(/Сумма по счёту/)).toBeTruthy();
    expect(screen.getByText(/Счёт №INV-A/).textContent).not.toMatch(/\b0[\s\u00a0]*₽/);
  });
  it('a transient metadata read failure requires rereading before ordinary creation', async () => {
    await seedUnknown();
    const original = Storage.prototype.getItem;
    let unreadable = true;
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, name) {
      if (this === sessionStorage && name.endsWith(':meta') && unreadable) throw new Error('Чтение временно недоступно');
      return original.call(this, name);
    });
    const io = { commit: vi.fn(), lookup: vi.fn().mockResolvedValue(saved) };
    render(<Harness onSubmit={(body, options) => createActGuarded(body, options, io)} />);
    expect(screen.getByRole('button', { name: 'Сохранить черновик' })).toHaveProperty('disabled', true);
    unreadable = false;
    fireEvent.click(screen.getByRole('button', { name: 'Повторить чтение попыток' }));
    fireEvent.click(screen.getByRole('button', { name: 'Проверить результат' }));
    await waitFor(() => expect(io.lookup).toHaveBeenCalledWith(key));
    expect(io.commit).not.toHaveBeenCalled();
  });
  it('reload offers lookup without sending another POST or rotating an unknown UUID', async () => {
    await seedUnknown();
    const io = { commit: vi.fn().mockResolvedValue(saved), lookup: vi.fn().mockRejectedValue(missing()) };
    const submit: ActSubmit = (body, options) => createActGuarded(body, options, io);
    render(<Harness onSubmit={submit} />);
    expect(screen.getByRole('button', { name: 'Сохранить черновик' })).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByRole('button', { name: 'Проверить результат' }));
    await screen.findByRole('alert');
    expect(io.commit).not.toHaveBeenCalled();
    expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]);
  });
  it('explicit retry reuses the persisted command even after unsaved fields change', async () => {
    await seedUnknown();
    const io = { commit: vi.fn().mockResolvedValue(saved), lookup: vi.fn().mockRejectedValue(missing()) };
    render(<Harness onSubmit={(body, options) => createActGuarded(body, options, io)} />);
    fireEvent.change(screen.getByLabelText('Сумма черновика'), { target: { value: '999' } });
    fireEvent.click(screen.getByRole('button', { name: 'Повторить сохранение' }));
    await waitFor(() => expect(io.commit).toHaveBeenCalledWith(command, key));
    await waitFor(() => expect(pendingActCreates()).toHaveLength(0));
  });
  it('checking a previously saved act adopts its canonical data and then clears its receipt', async () => {
    await seedUnknown();
    const io = { commit: vi.fn(), lookup: vi.fn().mockResolvedValue(saved) };
    const adopted = vi.fn();
    render(<Harness onSubmit={async (body, options) => { const act = await createActGuarded(body, options, io); adopted(act); return act; }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Проверить результат' }));
    await waitFor(() => expect(adopted).toHaveBeenCalledWith(saved));
    await waitFor(() => expect(pendingActCreates()).toHaveLength(0));
    expect(io.commit).not.toHaveBeenCalled();
  });
  it('an explicit new draft creates a separate UUID while retaining the inaccessible old attempt', async () => {
    await seedUnknown();
    const io = { commit: vi.fn().mockResolvedValue(saved), lookup: vi.fn().mockRejectedValue(missing()) };
    render(<Harness onSubmit={(body, options) => createActGuarded(body, options, io)} />);
    fireEvent.click(screen.getByRole('button', { name: 'Создать другой акт' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    await waitFor(() => expect(io.commit).toHaveBeenCalledOnce());
    expect(io.commit.mock.calls[0][1]).not.toBe(key);
    await waitFor(() => expect(pendingActCreates()).toMatchObject([{ key, state: 'unknown' }]));
  });
  it('validation leaves the draft editable and corrected submission keeps the same UUID', async () => {
    const io = { commit: vi.fn().mockRejectedValueOnce(Object.assign(new Error('Исправьте сумму'), { status: 400 })).mockResolvedValue(saved), lookup: vi.fn() };
    render(<Harness onSubmit={(body, options) => createActGuarded(body, options, io)} />);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    await screen.findByText('Исправьте сумму');
    const firstKey = io.commit.mock.calls[0][1];
    fireEvent.change(screen.getByLabelText('Сумма черновика'), { target: { value: '250' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    await waitFor(() => expect(io.commit).toHaveBeenLastCalledWith({ ...command, amount: 250 }, firstKey));
  });
  it('reload restores a validation-rejected draft for correction with its existing UUID', async () => {
    const io = { commit: vi.fn().mockRejectedValueOnce(Object.assign(new Error('Исправьте данные'), { status: 422 })).mockResolvedValue(saved), lookup: vi.fn() };
    await expect(createActGuarded({ ...command, amount: 250 }, { attemptKey: key }, io)).rejects.toThrow();
    render(<Harness onSubmit={(body, options) => createActGuarded(body, options, io)} />);
    fireEvent.click(screen.getByRole('button', { name: 'Исправить данные' }));
    expect(screen.getByLabelText('Сумма черновика')).toHaveProperty('value', '250');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    await waitFor(() => expect(io.commit).toHaveBeenLastCalledWith({ ...command, amount: 250 }, key));
  });
  it('late errors from an old source do not appear in a replacement form', async () => {
    let reject!: (error: Error) => void;
    const onSubmit = vi.fn().mockImplementation(() => new Promise<Act>((_done, failed) => { reject = failed; }));
    const view = render(<Harness onSubmit={onSubmit} scope="old-source" />);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    view.rerender(<Harness onSubmit={onSubmit} scope="new-source" />);
    await act(async () => { reject(new Error('Ошибка старой формы')); });
    expect(screen.queryByText('Ошибка старой формы')).toBeNull();
    expect(screen.getByRole('button', { name: 'Сохранить черновик' })).toHaveProperty('disabled', false);
  });
  it.each(['session', 'reopened-source'])('suppresses a late recovery failure after %s changes', async (boundary) => {
    await seedUnknown();
    let reject!: (error: Error) => void;
    const onSubmit = vi.fn().mockImplementation(() => new Promise<Act>((_done, failed) => { reject = failed; }));
    const view = render(<Harness onSubmit={onSubmit} scope="original" />);
    fireEvent.click(screen.getByRole('button', { name: 'Повторить сохранение' }));
    if (boundary === 'session') localStorage.setItem('pp.auth-session-epoch.v1', 'replacement');
    else {
      view.rerender(<Harness onSubmit={onSubmit} scope="other" />);
      view.rerender(<Harness onSubmit={onSubmit} scope="original" />);
    }
    await act(async () => { reject(new Error('Старая ошибка восстановления')); });
    expect(screen.queryByText('Старая ошибка восстановления')).toBeNull();
  });
  it('a former success cannot acknowledge its receipt or clear the reopened draft attempt', async () => {
    const replies = new Map<string, { resolve: (act: Act) => void; reject: (error: Error) => void }>();
    const io = { commit: vi.fn().mockImplementation((_body: ActInput, uploadKey: string) => new Promise<Act>((resolve, reject) => { replies.set(uploadKey, { resolve, reject }); })),
      lookup: vi.fn().mockRejectedValue(missing()) };
    const submit: ActSubmit = (body, options) => createActGuarded(body, options, io);
    const view = render(<Harness onSubmit={submit} scope="original" />);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    await waitFor(() => expect(io.commit).toHaveBeenCalledOnce());
    const oldKey = io.commit.mock.calls[0][1];
    view.rerender(<Harness onSubmit={submit} scope="closed" />);
    view.rerender(<Harness onSubmit={submit} scope="original" />);
    fireEvent.click(screen.getByRole('button', { name: 'Создать другой акт' }));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    await waitFor(() => expect(io.commit).toHaveBeenCalledTimes(2));
    const currentKey = io.commit.mock.calls[1][1];
    expect(currentKey).not.toBe(oldKey);
    await act(async () => { replies.get(oldKey)!.resolve(saved); });
    expect(pendingActCreates()).toEqual(expect.arrayContaining([expect.objectContaining({ key: oldKey, state: 'confirmed' }), expect.objectContaining({ key: currentKey, state: 'unknown' })]));
    await act(async () => { replies.get(currentKey)!.reject(Object.assign(new Error('Исправьте текущую сумму'), { status: 400 })); });
    io.commit.mockResolvedValueOnce({ ...saved, id: 92, number: '2' });
    fireEvent.change(screen.getByLabelText('Сумма черновика'), { target: { value: '250' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));
    await waitFor(() => expect(io.commit).toHaveBeenCalledTimes(3));
    expect(io.commit.mock.calls.map(call => call[1])).toEqual([oldKey, currentKey, currentKey]);
    await waitFor(() => expect(pendingActCreates()).toMatchObject([{ key: oldKey, state: 'confirmed' }]));
  });
});
