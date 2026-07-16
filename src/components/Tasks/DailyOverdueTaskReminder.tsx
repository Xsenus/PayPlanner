import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, ArrowRight, Clock3 } from 'lucide-react';

import { apiService } from '../../services/api';

type DailyOverdueTaskReminderProps = {
  userId: number;
  enabled: boolean;
  onOpenTasks: () => void;
};

const DISMISSAL_STORAGE_PREFIX = 'pp.tasks.daily-overdue-reminder';

function getLocalDateKey(date = new Date()) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

function getDismissalStorageKey(userId: number) {
  return `${DISMISSAL_STORAGE_PREFIX}.${userId}`;
}

export function DailyOverdueTaskReminder({ userId, enabled, onOpenTasks }: DailyOverdueTaskReminderProps) {
  const todayKey = useMemo(() => getLocalDateKey(), []);
  const [overdueCount, setOverdueCount] = useState(0);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!enabled) {
      setOpen(false);
      return;
    }

    try {
      if (window.localStorage.getItem(getDismissalStorageKey(userId)) === todayKey) {
        return;
      }
    } catch {
      // Напоминание остается доступным, даже если браузер запретил локальное хранилище.
    }

    let cancelled = false;
    void (async () => {
      try {
        const settings = await apiService.getTaskJournalSettings();
        if (cancelled || !settings.dailyOverdueReminderEnabled) {
          return;
        }

        const summary = await apiService.getTaskJournalOverdueSummary();
        if (cancelled || summary.count <= 0) {
          return;
        }

        setOverdueCount(summary.count);
        setOpen(true);
      } catch {
        // Сбой необязательного напоминания не должен мешать работе в CRM.
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [enabled, todayKey, userId]);

  const dismissForToday = () => {
    try {
      window.localStorage.setItem(getDismissalStorageKey(userId), todayKey);
    } catch {
      // Закрываем окно и без доступа к локальному хранилищу.
    }
    setOpen(false);
  };

  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-[75] flex items-center justify-center bg-slate-950/55 p-4 backdrop-blur-sm" role="presentation">
      <section
        role="dialog"
        aria-modal="true"
        aria-labelledby="daily-overdue-title"
        aria-describedby="daily-overdue-description"
        className="w-full max-w-lg overflow-hidden rounded-3xl border border-rose-200 bg-white shadow-2xl"
      >
        <div className="bg-gradient-to-br from-rose-600 to-orange-500 px-6 py-5 text-white">
          <div className="flex items-center gap-3">
            <span className="rounded-2xl bg-white/20 p-3"><AlertTriangle className="h-7 w-7" /></span>
            <div>
              <p className="text-xs font-bold uppercase tracking-[0.18em] text-rose-50">Ежедневный контроль</p>
              <h2 id="daily-overdue-title" className="mt-1 text-2xl font-extrabold">Внимание!</h2>
            </div>
          </div>
        </div>

        <div className="p-6 text-center">
          <Clock3 className="mx-auto h-10 w-10 text-rose-500" />
          <p id="daily-overdue-description" className="mt-4 text-lg font-semibold text-slate-900">
            У вас просрочено задач: <span className="text-3xl font-black text-rose-600">{overdueCount}</span>
          </p>
          <p className="mt-2 text-base font-bold uppercase tracking-wide text-slate-700">Срочно возьмите их на контроль</p>

          <div className="mt-6 grid gap-3 sm:grid-cols-2">
            <button
              type="button"
              onClick={() => {
                dismissForToday();
                onOpenTasks();
              }}
              className="inline-flex min-h-12 items-center justify-center gap-2 rounded-xl bg-rose-600 px-4 py-3 text-sm font-bold text-white transition hover:bg-rose-700 focus:outline-none focus:ring-2 focus:ring-rose-500 focus:ring-offset-2"
            >
              К задачам
              <ArrowRight className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={dismissForToday}
              className="min-h-12 rounded-xl border border-slate-300 bg-white px-4 py-3 text-sm font-bold text-slate-700 transition hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-slate-400 focus:ring-offset-2"
            >
              Закрыть
            </button>
          </div>
          <p className="mt-4 text-xs text-slate-500">После закрытия напоминание появится снова на следующий день.</p>
        </div>
      </section>
    </div>
  );
}
