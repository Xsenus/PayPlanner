import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { documentCopyTestSession } from '../../test/documentCopySession';
import type { ClientCase, CaseAppealDecision } from '../../types';
import { CasePreHearingActions } from './CasePreHearingActions';
import { CaseAppealDecisionAction } from './CaseAppealDecisionAction';
import { useDocumentDeliveryOwner } from '../../hooks/useDocumentDeliveryOwner';

const auth = vi.hoisted(() => ({ id: 7, role: 4, zone: 'UTC' }));
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: auth.id, role: { id: auth.role } } }) }));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => auth.zone }));
vi.mock('../../services/api', () => ({ apiService: { createTaskJournalItem: vi.fn(), getCurrentCaseAppealDecision: vi.fn(), createCaseAppealTask: vi.fn() } }));
const deferred = <T,>() => { let resolve!: (value: T) => void; let reject!: (cause: Error) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const item = { id: 42, clientId: 6, legalEntityId: null, title: 'Дело', status: 'Open', responsibleUserId: 8, nextHearingAt: '2099-10-20T15:00:00Z', courtDecisionAt: '2026-09-20T00:00:00', appealDeadlineAt: '2099-10-20T00:00:00' } as ClientCase;
const decision = { id: 8, caseId: 42, choice: 'appeal', taskId: 91, decidedAt: '2026-09-24T00:00:00Z' } as CaseAppealDecision;
const task = { id: 91, clientCaseId: 42, clientId: 6, legalEntityId: null, assigneeUserId: 8 };
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); auth.id = 7; auth.role = 4; auth.zone = 'UTC'; vi.clearAllMocks(); vi.mocked(apiService.getCurrentCaseAppealDecision).mockReset().mockResolvedValue(null); vi.mocked(apiService.createTaskJournalItem).mockReset().mockResolvedValue(task as never); vi.mocked(apiService.createCaseAppealTask).mockReset().mockResolvedValue(decision); });
afterEach(() => { session.restore(); vi.useRealTimers(); });
const kinds = ['hearing', 'appeal'] as const;
type Kind = typeof kinds[number];
function Panel({ kind, current = item, allowed = true, saved = true, created, open }: { kind: Kind; current?: ClientCase; allowed?: boolean; saved?: boolean; created?: (id: number) => void; open?: (id: number) => void }) { return kind === 'hearing' ? <CasePreHearingActions caseItem={current} timeZoneId="UTC" canCreateTask={allowed} datesSaved={saved} onTaskCreated={created} onOpenTask={open} /> : <CaseAppealDecisionAction clientCase={current} canEdit={allowed} datesSaved={saved} onTaskCreated={created} onOpenTask={open} />; }
const createButton = (kind: Kind) => screen.findByRole('button', { name: kind === 'hearing' ? 'Запросить доверенность' : 'Обжаловать — создать задачу' });
function pendingWrite(kind: Kind) { const request = deferred<never>(); if (kind === 'hearing') vi.mocked(apiService.createTaskJournalItem).mockReturnValueOnce(request.promise); else vi.mocked(apiService.createCaseAppealTask).mockReturnValueOnce(request.promise); return request; }
const result = (kind: Kind) => (kind === 'hearing' ? task : decision) as never;

describe('case task action ownership', () => {
  it.each(kinds)('baseline %s discards an old write after switching case', async kind => {
    const pending = pendingWrite(kind); const created = vi.fn(); const open = vi.fn();
    const view = render(<Panel kind={kind} created={created} open={open} />);
    fireEvent.click(await createButton(kind));
    view.rerender(<Panel kind={kind} current={{ ...item, id: 43 }} created={created} open={open} />);
    await act(async () => pending.resolve(result(kind)));
    expect(created).not.toHaveBeenCalled(); expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull();
  });
  it.each(kinds)('baseline %s ignores a response after a new login before rendering', async kind => {
    const pending = pendingWrite(kind); const created = vi.fn(); render(<Panel kind={kind} created={created} />);
    fireEvent.click(await createButton(kind)); session.replace(); await act(async () => pending.resolve(result(kind)));
    expect(created).not.toHaveBeenCalled(); expect(screen.queryByText(/Задача создана|Решено обжаловать/)).toBeNull();
  });
  it('baseline appeal drops a loaded choice when editing permission changes', async () => {
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValueOnce(decision);
    const view = render(<Panel kind="appeal" open={vi.fn()} />); await screen.findByRole('button', { name: 'Открыть задачу' });
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockReturnValueOnce(new Promise(() => {}));
    view.rerender(<Panel kind="appeal" allowed={false} open={vi.fn()} />);
    expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull();
  });
});

describe('extended case task boundaries', () => {
  const changes = [
    { name: 'client', apply: (c: ClientCase) => ({ ...c, clientId: 9 }) },
    { name: 'organization', apply: (c: ClientCase) => ({ ...c, legalEntityId: 12 }) },
    { name: 'responsible', apply: (c: ClientCase) => ({ ...c, responsibleUserId: 11 }) },
    { name: 'hearing', apply: (c: ClientCase) => ({ ...c, nextHearingAt: '2099-11-20T15:00:00Z', courtDecisionAt: '2026-09-21T00:00:00' }) },
    { name: 'deadline', apply: (c: ClientCase) => ({ ...c, nextHearingAt: '2099-11-20T15:00:00Z', appealDeadlineAt: '2099-11-20T00:00:00' }) },
    { name: 'status', apply: (c: ClientCase) => ({ ...c, status: 'Closed' as const }) },
  ];
  it.each(kinds.flatMap(kind => changes.map(change => ({ kind, ...change }))))('$kind rejects a late write after $name changes', async ({ kind, apply }) => {
    const pending = pendingWrite(kind); const created = vi.fn(); const view = render(<Panel kind={kind} created={created} open={vi.fn()} />);
    fireEvent.click(await createButton(kind)); view.rerender(<Panel kind={kind} current={apply(item)} created={created} open={vi.fn()} />);
    await act(async () => pending.resolve(result(kind))); expect(created).not.toHaveBeenCalled(); expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull();
  });
  it.each(kinds.flatMap(kind => ['permission', 'unsaved', 'role', 'logout', 'unmount'].map(change => ({ kind, change }))))('$kind rejects late errors after $change', async ({ kind, change }) => {
    const pending = pendingWrite(kind); const created = vi.fn(); const view = render(<Panel kind={kind} created={created} />); fireEvent.click(await createButton(kind));
    if (change === 'role') auth.role = 5;
    if (change === 'logout') session.replace('logout');
    if (change === 'unmount') view.unmount(); else view.rerender(<Panel kind={kind} allowed={change !== 'permission'} saved={change !== 'unsaved'} created={created} />);
    await act(async () => pending.reject(new Error('Прежняя ошибка'))); expect(screen.queryByText(/Прежняя ошибка/)).toBeNull(); expect(created).not.toHaveBeenCalled();
  });
  it.each(kinds)('%s reserves synchronously and keeps the lock through the callback', async kind => {
    const pending = pendingWrite(kind); const callback = deferred<void>(); const created = vi.fn(() => callback.promise);
    render(<Panel kind={kind} created={created} open={vi.fn()} />); const button = await createButton(kind);
    act(() => { button.click(); button.click(); });
    expect(kind === 'hearing' ? apiService.createTaskJournalItem : apiService.createCaseAppealTask).toHaveBeenCalledOnce();
    await act(async () => pending.resolve(result(kind))); expect(created).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: 'Открыть задачу' }).hasAttribute('disabled')).toBe(true);
    if (kind === 'hearing') expect(screen.getByRole('button', { name: 'Запланировать командировку' }).hasAttribute('disabled')).toBe(true);
    await act(async () => callback.resolve()); await waitFor(() => expect(screen.getByRole('button', { name: 'Открыть задачу' }).hasAttribute('disabled')).toBe(false));
  });
  it.each(kinds)('%s does not open a stale task after login without a render', async kind => {
    const open = vi.fn(); render(<Panel kind={kind} open={open} />); fireEvent.click(await createButton(kind)); const button = await screen.findByRole('button', { name: 'Открыть задачу' });
    session.replace(); fireEvent.click(button); expect(open).not.toHaveBeenCalled();
  });
  it.each(kinds)('%s keeps a confirmed task when refreshing the parent fails', async kind => {
    render(<Panel kind={kind} created={() => Promise.reject(new Error('Список недоступен'))} open={vi.fn()} />); fireEvent.click(await createButton(kind));
    expect((await screen.findByRole('alert')).textContent).toContain('Список недоступен'); expect(screen.getByRole('button', { name: 'Открыть задачу' })).toBeTruthy();
    expect(kind === 'hearing' ? apiService.createTaskJournalItem : apiService.createCaseAppealTask).toHaveBeenCalledOnce();
  });
  it.each(kinds)('%s uses the latest callback without another read or write', async kind => {
    const pending = pendingWrite(kind); const old = vi.fn(); const current = vi.fn(); const open = vi.fn(); const view = render(<Panel kind={kind} created={old} open={open} />);
    fireEvent.click(await createButton(kind)); view.rerender(<Panel kind={kind} created={current} open={open} />); session.refresh();
    await act(async () => pending.resolve(result(kind))); expect(old).not.toHaveBeenCalled(); expect(current).toHaveBeenCalledExactlyOnceWith(91);
    expect(apiService.getCurrentCaseAppealDecision).toHaveBeenCalledTimes(kind === 'appeal' ? 1 : 0);
  });
  it.each(kinds)('%s rejects a response for another case', async kind => {
    if (kind === 'hearing') vi.mocked(apiService.createTaskJournalItem).mockResolvedValueOnce({ ...task, clientCaseId: 43 } as never);
    else vi.mocked(apiService.createCaseAppealTask).mockResolvedValueOnce({ ...decision, caseId: 43 });
    const created = vi.fn(); render(<Panel kind={kind} created={created} />); fireEvent.click(await createButton(kind));
    await screen.findByRole('alert'); expect(created).not.toHaveBeenCalled(); expect(screen.queryByText(/Задача создана|Решено обжаловать/)).toBeNull();
  });
  it.each(kinds)('%s does not dispatch a saved handler after login', async kind => {
    render(<Panel kind={kind} />); const button = await createButton(kind); session.replace(); fireEvent.click(button);
    expect(apiService.createTaskJournalItem).not.toHaveBeenCalled(); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
  });
  it('appeal aborts its old read and ignores a successful late choice', async () => {
    const pending = deferred<CaseAppealDecision | null>(); vi.mocked(apiService.getCurrentCaseAppealDecision).mockReturnValueOnce(pending.promise);
    const view = render(<Panel kind="appeal" open={vi.fn()} />); await waitFor(() => expect(apiService.getCurrentCaseAppealDecision).toHaveBeenCalledOnce());
    const signal = vi.mocked(apiService.getCurrentCaseAppealDecision).mock.calls[0][1]!.signal!;
    view.rerender(<Panel kind="appeal" current={{ ...item, courtDecisionAt: '2026-09-21T00:00:00' }} open={vi.fn()} />);
    await createButton('appeal'); expect(signal.aborted).toBe(true); await act(async () => pending.resolve(decision));
    expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull();
    expect(apiService.getCurrentCaseAppealDecision).toHaveBeenLastCalledWith(42, expect.objectContaining({ expectedCourtDecisionAt: '2026-09-21T00:00:00', expectedAppealDeadlineAt: item.appealDeadlineAt }));
  });
  it('appeal ignores a read after login before render and accepts no write', async () => {
    const pending = deferred<CaseAppealDecision | null>(); vi.mocked(apiService.getCurrentCaseAppealDecision).mockReturnValueOnce(pending.promise);
    render(<Panel kind="appeal" open={vi.fn()} />); await waitFor(() => expect(apiService.getCurrentCaseAppealDecision).toHaveBeenCalledOnce()); session.replace();
    await act(async () => pending.resolve(decision)); expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull(); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
  });
  it.each([401, 403, 409, 503])('appeal blocks creation after HTTP%s and retries the read explicitly', async status => {
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockRejectedValueOnce(Object.assign(new Error('Отказ проверки'), { status })); render(<Panel kind="appeal" />);
    await screen.findByRole('alert'); fireEvent.click(await createButton('appeal')); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить проверку' })); await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    fireEvent.click(await createButton('appeal')); await screen.findByText(/Решено обжаловать/); expect(apiService.getCurrentCaseAppealDecision).toHaveBeenCalledTimes(2); expect(apiService.createCaseAppealTask).toHaveBeenCalledOnce();
  });
  it('appeal checks the deadline again when a previously rendered button is pressed', async () => {
    vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date('2099-10-20T12:00:00Z'));
    render(<Panel kind="appeal" />); const button = await createButton('appeal'); vi.setSystemTime(new Date('2099-10-21T00:00:01Z')); fireEvent.click(button); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
  });
  it('hearing checks the hearing time again before dispatch', async () => {
    vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date('2099-10-20T14:59:59Z'));
    render(<Panel kind="hearing" />); const button = await createButton('hearing'); vi.setSystemTime(new Date('2099-10-20T15:00:01Z')); fireEvent.click(button); expect(apiService.createTaskJournalItem).not.toHaveBeenCalled();
  });
  it('hearing preserves the task payload and never enables contractor payment', async () => {
    render(<Panel kind="hearing" />); fireEvent.click(await screen.findByRole('button', { name: 'Запланировать командировку' })); await screen.findByText(/Задача создана/);
    expect(apiService.createTaskJournalItem).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ clientCaseId: 42, clientId: 6, assigneeUserId: 8, title: 'Организовать поездку на судебное заседание', contractorPaymentEnabled: false, contractorPaymentAmount: null, contractorPaymentDueDate: null, contractorPaymentOnResult: false }));
  });
  it.each(kinds)('%s shares a parent lock with sibling actions and respects synchronous close', async kind => {
    const pending = pendingWrite(kind); const created = vi.fn(); const sibling = vi.fn();
    function Parent() { const owner = useDocumentDeliveryOwner('task-parent'); return <><button onClick={() => { const operation = owner.begin('sibling'); if (operation) { sibling(); operation.finish(); } }}>Другая команда</button><button onClick={owner.invalidate}>Закрыть родителя</button>{kind === 'hearing' ? <CasePreHearingActions actionOwner={owner} caseItem={item} timeZoneId="UTC" canCreateTask onTaskCreated={created} /> : <CaseAppealDecisionAction actionOwner={owner} clientCase={item} canEdit onTaskCreated={created} />}</>; }
    render(<Parent />); fireEvent.click(await createButton(kind)); fireEvent.click(screen.getByRole('button', { name: 'Другая команда' })); expect(sibling).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть родителя' })); await act(async () => pending.resolve(result(kind))); expect(created).not.toHaveBeenCalled();
  });
});

describe('additional task read validation', () => {
  it.each([
    { ...decision, caseId: 43 }, { ...decision, id: 0 }, { ...decision, taskId: -1 }, { ...decision, choice: 'decline' },
  ])('rejects an invalid recorded choice %# without task navigation', async value => {
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValueOnce(value as CaseAppealDecision);
    render(<Panel kind="appeal" open={vi.fn()} />); await screen.findByRole('alert'); expect(screen.queryByRole('button', { name: 'Открыть задачу' })).toBeNull(); fireEvent.click(await createButton('appeal')); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
  });
  it.each(kinds)('%s rejects a nonpositive task identifier', async kind => {
    if (kind === 'hearing') vi.mocked(apiService.createTaskJournalItem).mockResolvedValueOnce({ ...task, id: 0 } as never);
    else vi.mocked(apiService.createCaseAppealTask).mockResolvedValueOnce({ ...decision, taskId: 0 });
    const created = vi.fn(); render(<Panel kind={kind} created={created} />); fireEvent.click(await createButton(kind)); await screen.findByRole('alert'); expect(created).not.toHaveBeenCalled();
  });
  it('keeps an existing task readable without the right to edit the case', async () => {
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockResolvedValueOnce(decision); const open = vi.fn(); render(<Panel kind="appeal" allowed={false} open={open} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть задачу' })); expect(open).toHaveBeenCalledExactlyOnceWith(91); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
  });
  it.each(kinds)('%s sends no request after logout', async kind => {
    session.replace('logout'); render(<Panel kind={kind} />); expect(screen.queryByRole('button')).toBeNull(); expect(apiService.createTaskJournalItem).not.toHaveBeenCalled(); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled(); expect(apiService.getCurrentCaseAppealDecision).not.toHaveBeenCalled();
  });
  it('does not dispatch a saved retry after a new login', async () => {
    vi.mocked(apiService.getCurrentCaseAppealDecision).mockRejectedValueOnce(new Error('Ошибка проверки')); render(<Panel kind="appeal" />); const retry = await screen.findByRole('button', { name: 'Повторить проверку' }); session.replace(); fireEvent.click(retry); expect(apiService.getCurrentCaseAppealDecision).toHaveBeenCalledOnce();
  });
});

describe('appeal timezone', () => {
  it('keeps the same judicial decision read when the personal timezone finishes loading', async () => {
    const view = render(<Panel kind="appeal" />); await createButton('appeal'); auth.zone = 'Asia/Novosibirsk'; view.rerender(<Panel kind="appeal" />); await act(async () => {});
    expect(apiService.getCurrentCaseAppealDecision).toHaveBeenCalledOnce(); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled(); expect(await createButton('appeal')).toBeTruthy();
  });
  it('uses the current timezone for the deadline without rereading the same decision', async () => {
    vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date('2099-10-20T20:00:00Z'));
    const view = render(<Panel kind="appeal" />); await createButton('appeal'); auth.zone = 'Asia/Novosibirsk'; view.rerender(<Panel kind="appeal" />);
    expect(await screen.findByText(/Указанный срок обжалования уже прошёл/)).toBeTruthy(); expect(screen.queryByRole('button', { name: 'Обжаловать — создать задачу' })).toBeNull(); expect(apiService.getCurrentCaseAppealDecision).toHaveBeenCalledOnce(); expect(apiService.createCaseAppealTask).not.toHaveBeenCalled();
  });
});

describe('appeal omitted task write boundary',()=>{
 it('still requires a task ID after creating a new appeal task',async()=>{
  vi.mocked(apiService.createCaseAppealTask).mockResolvedValueOnce({id:8,caseId:42,choice:'appeal',decidedAt:'2026-09-24T00:00:00Z'} as never);
  const created=vi.fn();render(<Panel kind="appeal" created={created}/>);fireEvent.click(await createButton('appeal'));await screen.findByRole('alert');expect(created).not.toHaveBeenCalled();expect(screen.queryByText(/Решено обжаловать/)).toBeNull();
 });
});
