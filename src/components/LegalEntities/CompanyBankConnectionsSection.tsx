import { RefreshCw } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';

import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { BankAccountItem, BankConnectionItem, BankStatementRun, BankSyncLogItem } from '../../types';
import { extractDisplayErrorMessage, isHttpErrorStatus } from '../../utils/errorUtils';

interface CompanyBankConnectionsSectionProps {
  companyId: number;
}

const PROVIDER_LABELS: Record<string, string> = { Tochka: 'Точка', Sber: 'Сбербанк' };

const CONNECTION_LABELS: Record<BankConnectionItem['status'], string> = {
  Active: 'Активно',
  TokenExpiring: 'Токен скоро истечёт',
  ReauthRequired: 'Требуется переавторизация',
  AuthError: 'Ошибка авторизации',
  Disconnected: 'Отключено',
};

const ACCOUNT_LABELS: Record<BankAccountItem['status'], string> = {
  Open: 'Открыт',
  Limited: 'Открыт (ограничен)',
  Preliminary: 'Предварительный',
  Closed: 'Закрыт',
  Unavailable: 'Недоступен',
  Pending: 'Открывается',
  NotFoundInBank: 'Не найден в банке',
};

const RUN_LABELS: Record<BankStatementRun['status'], string> = {
  Success: 'Успешно',
  Partial: 'Частично',
  Error: 'Ошибка',
};

const toDateInput = (date: Date) => date.toISOString().slice(0, 10);

/**
 * Банковские подключения собственной компании: список счетов из банка со статусами, настройки счёта,
 * загрузка счетов и выписок, журнал. Видно только администратору (сервер отдаёт данные только ему).
 */
export function CompanyBankConnectionsSection({ companyId }: CompanyBankConnectionsSectionProps) {
  const toast = useToast();
  const [connections, setConnections] = useState<BankConnectionItem[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [loadedCompanyId, setLoadedCompanyId] = useState<number | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const loadSequence = useRef(0);
  const [busy, setBusy] = useState(false);
  const [period, setPeriod] = useState(() => {
    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 3600 * 1000);
    return { from: toDateInput(from), to: toDateInput(to) };
  });
  const [lastRun, setLastRun] = useState<(BankStatementRun & { connectionId: number }) | null>(null);
  const [logs, setLogs] = useState<{ connectionId: number; items: BankSyncLogItem[] } | null>(null);

  const load = useCallback(async () => {
    const sequence = ++loadSequence.current;
    try {
      const result = await apiService.getBankConnections(companyId);
      if (sequence !== loadSequence.current) return;
      setConnections(result);
      setLoadError(null);
    } catch (error) {
      if (sequence !== loadSequence.current) return;
      setConnections([]);
      setLoadError(isHttpErrorStatus(error, 403) || isHttpErrorStatus(error, 401) ? null : extractDisplayErrorMessage(error, 'Не удалось загрузить банковские подключения.'));
    } finally {
      if (sequence === loadSequence.current) {
        setLoaded(true);
        setLoadedCompanyId(companyId);
      }
    }
  }, [companyId]);

  useEffect(() => {
    void load();
    const sequence = loadSequence;
    return () => { sequence.current++; };
  }, [load]);

  const run = async (action: () => Promise<void>, failure: string) => {
    setBusy(true);
    try {
      await action();
      await load();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, failure), 'Банковские подключения');
    } finally {
      setBusy(false);
    }
  };

  if (!loaded || loadedCompanyId !== companyId || connections.length === 0) {
    return loadedCompanyId === companyId && loadError ? (
      <section className="mt-3 rounded-lg border border-slate-200 p-3">
        <p role="alert" className="text-sm text-red-700">{loadError}</p>
        <button type="button" className="mt-2 rounded-lg border px-2 py-1 text-sm" onClick={() => void load()}>Повторить загрузку</button>
      </section>
    ) : null;
  }

  const accountLabel = (accountId: number) => {
    for (const connection of connections) {
      const account = connection.accounts.find((item) => item.id === accountId);
      if (account) return `…${account.accountNumber.slice(-6)}`;
    }
    return String(accountId);
  };

  return (
    <section className="mt-4 rounded-2xl border border-slate-200 bg-white p-3" data-testid="company-bank-connections">
      <h3 className="text-base font-semibold text-slate-900">Банковские подключения</h3>

      {connections.map((connection) => (
        <div key={connection.id} className="mt-3 rounded-xl border border-slate-200 p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <span className="font-medium text-slate-900">{PROVIDER_LABELS[connection.provider] ?? connection.provider}</span>
              <span
                className={`ml-2 rounded-full px-2 py-0.5 text-xs ${connection.status === 'Active' ? 'bg-emerald-100 text-emerald-700' : 'bg-amber-100 text-amber-800'}`}
              >
                {CONNECTION_LABELS[connection.status] ?? connection.status}
              </span>
              {connection.lastError ? <p className="mt-1 text-xs text-rose-600">{connection.lastError}</p> : null}
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    const result = await apiService.syncBankConnectionAccounts(connection.id);
                    toast.success(`Счета обновлены: новых ${result.added}, изменённых ${result.updated}.`, 'Банковские подключения');
                  }, 'Не удалось обновить счета.')
                }
                className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2 py-1 text-sm text-slate-700 hover:bg-slate-50 disabled:opacity-50"
              >
                <RefreshCw className="h-4 w-4" />
                Обновить счета
              </button>
              <button
                type="button"
                disabled={busy}
                onClick={() =>
                  void apiService.getBankSyncLogs(connection.id).then((page) => setLogs({ connectionId: connection.id, items: page.items }))
                    .catch((error) => toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить журнал банка.'), 'Банковские подключения'))
                }
                className="rounded-lg border border-slate-200 px-2 py-1 text-sm text-slate-700 hover:bg-slate-50 disabled:opacity-50"
              >
                Журнал
              </button>
            </div>
          </div>

          <div className="mt-2 flex flex-wrap items-end gap-2 text-sm">
            <label className="flex flex-col gap-1">
              <span className="text-xs text-slate-500">С</span>
              <input
                type="date"
                value={period.from}
                onChange={(event) => setPeriod((prev) => ({ ...prev, from: event.target.value }))}
                className="rounded-lg border border-slate-300 px-2 py-1"
              />
            </label>
            <label className="flex flex-col gap-1">
              <span className="text-xs text-slate-500">По</span>
              <input
                type="date"
                value={period.to}
                onChange={(event) => setPeriod((prev) => ({ ...prev, to: event.target.value }))}
                className="rounded-lg border border-slate-300 px-2 py-1"
              />
            </label>
            <button
              type="button"
              disabled={busy || !period.from || !period.to}
              onClick={() =>
                void run(async () => {
                  const result = await apiService.syncBankConnectionStatements(connection.id, period);
                  setLastRun({ ...result, connectionId: connection.id });
                }, 'Не удалось загрузить выписки.')
              }
              className="rounded-lg bg-blue-600 px-3 py-1.5 text-white hover:bg-blue-700 disabled:opacity-50"
            >
              Загрузить выписки по всем счетам
            </button>
          </div>

          {lastRun?.connectionId === connection.id ? (
            <div className="mt-2 text-sm" data-testid="bank-statement-run">
              <p className="font-medium">
                {RUN_LABELS[lastRun.status]}
                {lastRun.status === 'Partial' ? ` (${lastRun.succeeded} из ${lastRun.total})` : ''}
              </p>
              <ul className="mt-1 space-y-0.5 text-slate-600">
                {lastRun.accounts.map((item) => (
                  <li key={item.accountId}>
                    Счёт {accountLabel(item.accountId)} — {item.error ? <span className="text-rose-600">{item.error}</span> : `загружено операций: ${item.inserted + item.updated}`}
                  </li>
                ))}
              </ul>
            </div>
          ) : null}

          <div className="mt-3 overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-200 text-sm">
              <thead className="bg-slate-50 text-left text-slate-600">
                <tr>
                  <th className="px-2 py-1 font-semibold">Счёт</th>
                  <th className="px-2 py-1 font-semibold">Статус</th>
                  <th className="px-2 py-1 font-semibold">По умолчанию</th>
                  <th className="px-2 py-1 font-semibold">Скрыт</th>
                  <th className="px-2 py-1 font-semibold">Выписки</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {connection.accounts.map((account) => {
                  const closed = account.status === 'Closed' || account.status === 'Preliminary';
                  return (
                    <tr key={account.id} className={closed ? 'text-slate-400' : undefined}>
                      <td className="px-2 py-1 font-mono">{account.accountNumber}</td>
                      <td className="px-2 py-1">
                        {ACCOUNT_LABELS[account.status] ?? account.status}
                        {account.status === 'Limited' ? <span className="ml-1 text-xs text-amber-700">(осторожно при разнесении)</span> : null}
                        {account.status === 'NotFoundInBank' ? <span className="ml-1 text-xs text-rose-600">проверьте вручную</span> : null}
                      </td>
                      {(['isDefault', 'isHidden', 'statementsEnabled'] as const).map((field) => (
                        <td key={field} className="px-2 py-1">
                          <input
                            type="checkbox"
                            aria-label={`${field === 'isDefault' ? 'По умолчанию' : field === 'isHidden' ? 'Скрыть счёт' : 'Загружать выписки'} ${account.accountNumber}`}
                            checked={account[field]}
                            disabled={busy || (field === 'isDefault' && account.status !== 'Open')}
                            onChange={(event) =>
                              void run(async () => {
                                await apiService.updateBankAccountSettings(account.id, { [field]: event.target.checked });
                              }, 'Не удалось изменить настройки счёта.')
                            }
                          />
                        </td>
                      ))}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {logs?.connectionId === connection.id ? (
            <ul className="mt-2 max-h-40 space-y-0.5 overflow-y-auto text-xs text-slate-600" data-testid="bank-sync-log">
              {logs.items.length === 0 ? <li>Записей пока нет.</li> : null}
              {logs.items.map((item) => (
                <li key={item.id}>
                  {new Date(item.createdAt).toLocaleString('ru-RU')} · {item.kind === 'accounts' ? 'счета' : `выписка ${item.accountId ? accountLabel(item.accountId) : ''}`} ·{' '}
                  {item.result === 'ok' ? `готово (${item.operationsCount})` : <span className="text-rose-600">{item.error ?? 'ошибка'}</span>}
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ))}
    </section>
  );
}
