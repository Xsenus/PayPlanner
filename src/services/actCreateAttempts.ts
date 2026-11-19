import type { Act, ActInput } from '../types';
import { API_BASE_URL } from './apiBaseUrl';
import { assertAuthSessionIdentity, captureAuthSessionIdentity, isAuthSessionIdentityCurrent, type AuthSessionIdentity } from './authService';
import { createClientRequestId } from '../utils/clientRequestId';

export const ACT_CREATE_ATTEMPTS_CHANGED = 'payplanner:act-create-attempts-changed';
const PREFIX = 'pp.act-create.v1:';

export interface ActCreateOptions {
  attemptKey: string;
  retry?: boolean;
  lookupOnly?: boolean;
}
export interface PendingActCreate {
  key: string;
  actor: string;
  apiScope: string;
  state: 'unknown' | 'editable' | 'confirmed';
  date: string;
  title: string;
  createdAt: string;
  actId?: number;
  amount?: number;
  creationMode?: ActInput['creationMode'];
  invoiceNumber?: string | null;
  invoiceId?: number | null;
  clientId?: number | null;
  legalEntityId?: number | null;
}
interface ActCreatePorts {
  commit: (command: ActInput, key: string) => Promise<Act>;
  lookup: (key: string) => Promise<Act>;
}
const receipts = new WeakMap<Act, { entry: PendingActCreate; recovered: boolean; session: AuthSessionIdentity }>();
const active = new Map<string, { job: Promise<Act>; command: string; session: AuthSessionIdentity }>();

function currentOwner() {
  const session = captureAuthSessionIdentity();
  if (!session.authenticated || !session.actor || !session.epoch) throw new Error('Войдите в систему перед сохранением акта.');
  return { session, actor: session.actor, apiScope: API_BASE_URL };
}
function storageKey(actor: string, apiScope: string, key: string) {
  return `${PREFIX}${encodeURIComponent(apiScope)}:${encodeURIComponent(actor)}:${key}`;
}
function notify() { window.dispatchEvent(new Event(ACT_CREATE_ATTEMPTS_CHANGED)); }
function verifiedWrite(key: string, value: string) {
  try {
    sessionStorage.setItem(key, value);
    if (sessionStorage.getItem(key) !== value) throw new Error('Storage verification failed');
  } catch {
    throw new Error('Не удалось сохранить попытку создания в браузере. Освободите место и повторите сохранение.');
  }
}
function save(entry: PendingActCreate, command?: ActInput) {
  const key = storageKey(entry.actor, entry.apiScope, entry.key);
  try {
    if (command) verifiedWrite(`${key}:command`, JSON.stringify(command));
    verifiedWrite(`${key}:meta`, JSON.stringify(entry));
  } catch (error) {
    // No transport is allowed before both records exist. Remove an unregistered draft fragment.
    try { if (!sessionStorage.getItem(`${key}:meta`)) sessionStorage.removeItem(`${key}:command`); }
    catch { /* Preserve the original storage failure. */ }
    throw error;
  }
  notify();
}
function read(key: string, actor: string, apiScope: string): PendingActCreate | null {
  const json = sessionStorage.getItem(`${storageKey(actor, apiScope, key)}:meta`);
  if (!json) return null;
  const entry = JSON.parse(json) as PendingActCreate;
  if (entry.key !== key || entry.actor !== actor || entry.apiScope !== apiScope
    || typeof entry.date !== 'string' || typeof entry.title !== 'string' || typeof entry.createdAt !== 'string'
    || !['unknown', 'editable', 'confirmed'].includes(entry.state)) throw new Error('Не удалось прочитать сохранённую попытку создания акта.');
  return entry;
}
export function pendingActCreates(): PendingActCreate[] {
  const owner = currentOwner();
  const prefix = storageKey(owner.actor, owner.apiScope, '');
  const entries: PendingActCreate[] = [];
  for (let index = 0; index < sessionStorage.length; index += 1) {
    const key = sessionStorage.key(index);
    if (!key?.startsWith(prefix) || !key.endsWith(':meta')) continue;
    const entry = read(key.slice(prefix.length, -5), owner.actor, owner.apiScope);
    if (entry) entries.push(entry);
  }
  return entries.sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}
export function actCreateCommand(key: string): ActInput {
  const owner = currentOwner();
  const entry = read(key, owner.actor, owner.apiScope);
  const json = entry && sessionStorage.getItem(`${storageKey(owner.actor, owner.apiScope, key)}:command`);
  if (!json) throw new Error('Не удалось прочитать команду сохранения акта.');
  const command = JSON.parse(json) as ActInput;
  if (!command || typeof command.date !== 'string' || typeof command.amount !== 'number' || typeof command.status !== 'string') {
    throw new Error('Не удалось прочитать команду сохранения акта.');
  }
  return command;
}
export function newActCreateKey() { return createClientRequestId(); }
function statusOf(error: unknown) {
  return typeof error === 'object' && error !== null && 'status' in error ? Number(error.status) : null;
}
function canonicalCommand(payload: ActInput): ActInput {
  // Number is assigned by the server. JSON cloning freezes the dispatched business command.
  return JSON.parse(JSON.stringify({ ...payload, number: '' })) as ActInput;
}
export async function createActGuarded(payload: ActInput, options: ActCreateOptions | undefined, ports: ActCreatePorts): Promise<Act> {
  const owner = currentOwner();
  const key = options?.attemptKey ?? newActCreateKey();
  const identity = storageKey(owner.actor, owner.apiScope, key);
  const submitted = canonicalCommand(payload);
  const submittedJson = JSON.stringify(submitted);
  const existing = active.get(identity);
  if (existing && isAuthSessionIdentityCurrent(existing.session)) {
    const act = await existing.job;
    assertAuthSessionIdentity(owner.session);
    const receipt = receipts.get(act);
    if (receipt && submittedJson !== existing.command) receipt.recovered = true;
    return act;
  }
  const run = async () => {
    let entry = read(key, owner.actor, owner.apiScope);
    let command = submitted;
    let recovered = false;
    const accept = (act: Act) => {
      assertAuthSessionIdentity(owner.session);
      if (!Number.isSafeInteger(act?.id) || act.id <= 0 || !act.number) throw new Error('Не удалось прочитать сохранённый акт. Проверьте результат попытки.');
      entry = { ...entry!, state: 'confirmed', actId: act.id, date: act.date, title: act.title ?? '',
        amount: act.amount, creationMode: act.creationMode ?? entry!.creationMode,
        invoiceNumber: act.invoiceNumber, invoiceId: act.invoiceId, clientId: act.clientId, legalEntityId: act.legalEntityId };
      save(entry);
      receipts.set(act, { entry, recovered, session: owner.session });
      return act;
    };
    if (entry && entry.state !== 'editable') {
      command = actCreateCommand(key);
      try {
        const act = await ports.lookup(key);
        recovered = true;
        return accept(act);
      } catch (error) {
        assertAuthSessionIdentity(owner.session);
        if (statusOf(error) !== 404) throw error;
        if (options?.lookupOnly || entry.state === 'confirmed') throw new Error('Результат пока недоступен. Попытка сохранена. Повторите проверку с прежним номером попытки.');
        if (!options?.retry) throw new Error('Результат предыдущего сохранения неизвестен. Проверьте или повторите сохранённую попытку.');
      }
    } else if (options?.lookupOnly) {
      if (!entry) throw new Error('Сохранённая попытка не найдена в браузере.');
      const act = await ports.lookup(key);
      recovered = true;
      return accept(act);
    }
    assertAuthSessionIdentity(owner.session);
    entry = { key, actor: owner.actor, apiScope: owner.apiScope, state: 'unknown', date: command.date,
      title: command.title ?? '', createdAt: entry?.createdAt ?? new Date().toISOString(),
      amount: command.amount, creationMode: command.creationMode, invoiceNumber: command.invoiceNumber,
      invoiceId: command.invoiceId, clientId: command.clientId, legalEntityId: command.legalEntityId };
    save(entry, command);
    assertAuthSessionIdentity(owner.session);
    try {
      return accept(await ports.commit(command, key));
    } catch (error) {
      assertAuthSessionIdentity(owner.session);
      if (statusOf(error) === 400 || statusOf(error) === 422) {
        save({ ...entry, state: 'editable' });
        throw error;
      }
      // Lost acknowledgements and conflicts are resolved through the actor-bound receipt.
      try {
        const act = await ports.lookup(key);
        recovered = true;
        return accept(act);
      } catch {
        assertAuthSessionIdentity(owner.session);
        throw error;
      }
    }
  };
  const job = run();
  active.set(identity, { job, command: submittedJson, session: owner.session });
  try { return await job; } finally { if (active.get(identity)?.job === job) active.delete(identity); }
}
export function acknowledgeCreatedAct(act: Act): void {
  const receipt = receipts.get(act);
  if (!receipt || !isAuthSessionIdentityCurrent(receipt.session)) return;
  const owner = currentOwner();
  if (receipt.entry.actor !== owner.actor || receipt.entry.apiScope !== owner.apiScope) return;
  const key = storageKey(owner.actor, owner.apiScope, receipt.entry.key);
  const current = read(receipt.entry.key, owner.actor, owner.apiScope);
  if (current?.state !== 'confirmed' || current.actId !== act.id) return;
  sessionStorage.removeItem(`${key}:meta`);
  sessionStorage.removeItem(`${key}:command`);
  receipts.delete(act);
  notify();
}
export function actCreateSuccessMessage(act: Act): string {
  return receipts.get(act)?.recovered ? `Найден ранее сохранённый акт №${act.number}. Использованы его сохранённые данные.` : 'Акт создан.';
}
