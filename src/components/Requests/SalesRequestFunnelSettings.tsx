import { useState } from 'react';

import { useToast } from '../../hooks/useToast';
import { apiService } from '../../services/api';
import type { SalesRequestStatusItem } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';

interface SalesRequestFunnelSettingsProps {
  statuses: SalesRequestStatusItem[];
  assignees: TaskJournalAssignee[];
  onChanged: () => void;
}

const FINAL_LABELS: Record<string, string> = { None: '', Success: ' · финал: успех', Failure: ' · финал: провал' };

type RuleDraft = { title: string; assigneeUserId: string; dueInMinutes: string };

/**
 * Настройка воронки заявок (только администратор): названия статусов, новые статусы и правила
 * «при входе в статус создать задачу». Изменения действуют на будущие переходы, созданные задачи не меняются.
 */
export function SalesRequestFunnelSettings({ statuses, assignees, onChanged }: SalesRequestFunnelSettingsProps) {
  const toast = useToast();
  const [openId, setOpenId] = useState<number | null>(null);
  const [rules, setRules] = useState<RuleDraft[]>([]);
  const [newTitle, setNewTitle] = useState('');
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<void>, failure: string) => {
    setBusy(true);
    try {
      await action();
      onChanged();
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, failure), 'Заявки');
    } finally {
      setBusy(false);
    }
  };

  const open = (status: SalesRequestStatusItem) => {
    setOpenId(status.id);
    setRules(status.rules.map((rule) => ({ title: rule.title, assigneeUserId: rule.assigneeUserId ? String(rule.assigneeUserId) : '', dueInMinutes: String(rule.dueInMinutes) })));
  };

  const updateRule = (index: number, patch: Partial<RuleDraft>) =>
    setRules((prev) => prev.map((rule, i) => (i === index ? { ...rule, ...patch } : rule)));

  const rulesValid = rules.every((rule) => rule.title.trim() !== '' && Number(rule.dueInMinutes) >= 1);

  return (
    <div className="rounded-2xl border border-slate-200 bg-white p-3" data-testid="sales-request-funnel-settings">
      <h2 className="font-semibold text-slate-900">Настройка воронки</h2>
      <p className="text-xs text-slate-500">Правила действуют на будущие переходы. Без исполнителя задача ставится на ответственного по заявке.</p>
      <ul className="mt-2 space-y-2 text-sm">
        {statuses.map((status) => (
          <li key={status.id} className="rounded-lg border border-slate-100 p-2">
            <div className="flex items-center justify-between gap-2">
              <button type="button" className="text-left font-medium text-slate-800 hover:underline" onClick={() => (openId === status.id ? setOpenId(null) : open(status))}>
                {status.title}
                <span className="text-xs font-normal text-slate-500">{FINAL_LABELS[status.finalKind]} · правил: {status.rules.length}</span>
              </button>
            </div>
            {openId === status.id ? (
              <div className="mt-2 space-y-2">
                {rules.map((rule, index) => (
                  <div key={index} className="flex flex-wrap items-center gap-2">
                    <input aria-label="Текст задачи правила" value={rule.title} onChange={(event) => updateRule(index, { title: event.target.value })} className="w-64 rounded-lg border border-slate-300 px-2 py-1" />
                    <select aria-label="Исполнитель правила" value={rule.assigneeUserId} onChange={(event) => updateRule(index, { assigneeUserId: event.target.value })} className="rounded-lg border border-slate-300 px-2 py-1">
                      <option value="">Ответственный по заявке</option>
                      {assignees.map((item) => (
                        <option key={item.id} value={item.id}>{item.name}</option>
                      ))}
                    </select>
                    <input aria-label="Срок в минутах" inputMode="numeric" value={rule.dueInMinutes} onChange={(event) => updateRule(index, { dueInMinutes: event.target.value })} className="w-24 rounded-lg border border-slate-300 px-2 py-1" />
                    <span className="text-xs text-slate-500">мин</span>
                    <button type="button" className="text-rose-600" onClick={() => setRules((prev) => prev.filter((_, i) => i !== index))}>Удалить</button>
                  </div>
                ))}
                <div className="flex gap-2">
                  <button type="button" className="rounded-lg border border-slate-200 px-2 py-1" onClick={() => setRules((prev) => [...prev, { title: '', assigneeUserId: '', dueInMinutes: '60' }])}>+ Правило</button>
                  <button
                    type="button"
                    disabled={busy || !rulesValid}
                    className="rounded-lg bg-blue-600 px-2 py-1 text-white disabled:opacity-50"
                    onClick={() =>
                      void run(async () => {
                        await apiService.replaceSalesRequestRules(
                          status.id,
                          rules.map((rule) => ({ title: rule.title.trim(), assigneeUserId: rule.assigneeUserId ? Number(rule.assigneeUserId) : null, dueInMinutes: Number(rule.dueInMinutes) })),
                        );
                        setOpenId(null);
                      }, 'Не удалось сохранить правила.')
                    }
                  >
                    Сохранить правила
                  </button>
                </div>
              </div>
            ) : null}
          </li>
        ))}
      </ul>
      <div className="mt-3 flex gap-2 text-sm">
        <input aria-label="Название нового статуса" placeholder="Новый статус воронки" value={newTitle} onChange={(event) => setNewTitle(event.target.value)} className="w-64 rounded-lg border border-slate-300 px-2 py-1" />
        <button
          type="button"
          disabled={busy || newTitle.trim() === ''}
          className="rounded-lg border border-slate-200 px-2 py-1 disabled:opacity-50"
          onClick={() =>
            void run(async () => {
              await apiService.saveSalesRequestStatus(null, { title: newTitle.trim() });
              setNewTitle('');
            }, 'Не удалось добавить статус.')
          }
        >
          Добавить статус
        </button>
      </div>
    </div>
  );
}
