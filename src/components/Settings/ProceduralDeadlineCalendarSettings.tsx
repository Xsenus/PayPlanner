import { useCallback, useEffect, useMemo, useState } from 'react';
import { CalendarDays, Loader2, Plus, Save, Trash2 } from 'lucide-react';

import { apiService } from '../../services/api';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { toRuDate } from '../../utils/dateUtils';
import type { ProceduralDeadlineCalendarDate, ProceduralDeadlineCalendarSettings as ProceduralDeadlineCalendarSettingsDto } from '../../types';

type CalendarDateKind = 'working' | 'nonWorking';

function normalizeDateRows(rows: ProceduralDeadlineCalendarDate[]) {
  const map = new Map<string, ProceduralDeadlineCalendarDate>();
  rows.forEach((row) => {
    const date = row.date.trim().slice(0, 10);
    if (!date) return;
    map.set(date, {
      date,
      note: row.note?.trim() || null,
    });
  });

  return Array.from(map.values()).sort((left, right) => left.date.localeCompare(right.date));
}

function emptySettings(): ProceduralDeadlineCalendarSettingsDto {
  return {
    workingDates: [],
    nonWorkingDates: [],
    updatedAt: null,
  };
}

export function ProceduralDeadlineCalendarSettings() {
  const toast = useToast();
  const [settings, setSettings] = useState<ProceduralDeadlineCalendarSettingsDto>(emptySettings);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [draftKind, setDraftKind] = useState<CalendarDateKind>('nonWorking');
  const [draftDate, setDraftDate] = useState('');
  const [draftNote, setDraftNote] = useState('');
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await apiService.getProceduralDeadlineCalendarSettings();
      setSettings({
        workingDates: normalizeDateRows(response.workingDates ?? []),
        nonWorkingDates: normalizeDateRows(response.nonWorkingDates ?? []),
        updatedAt: response.updatedAt ?? null,
      });
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось загрузить производственный календарь.');
      setError(nextError);
      toast.error(nextError, 'Настройки');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    void load();
  }, [load]);

  const hasRows = settings.workingDates.length > 0 || settings.nonWorkingDates.length > 0;
  const updatedLabel = useMemo(() => toRuDate(settings.updatedAt) || '—', [settings.updatedAt]);

  const addDraftDate = () => {
    const date = draftDate.trim();
    if (!date) {
      setError('Укажите дату исключения производственного календаря.');
      return;
    }

    setError(null);
    setMessage(null);
    setSettings((current) => {
      const row = { date, note: draftNote.trim() || null };
      if (draftKind === 'working') {
        return {
          ...current,
          workingDates: normalizeDateRows([...current.workingDates, row]),
          nonWorkingDates: normalizeDateRows(current.nonWorkingDates.filter((item) => item.date !== date)),
        };
      }

      return {
        ...current,
        workingDates: normalizeDateRows(current.workingDates.filter((item) => item.date !== date)),
        nonWorkingDates: normalizeDateRows([...current.nonWorkingDates, row]),
      };
    });
    setDraftDate('');
    setDraftNote('');
  };

  const removeDate = (kind: CalendarDateKind, date: string) => {
    setSettings((current) => ({
      ...current,
      workingDates: kind === 'working'
        ? current.workingDates.filter((item) => item.date !== date)
        : current.workingDates,
      nonWorkingDates: kind === 'nonWorking'
        ? current.nonWorkingDates.filter((item) => item.date !== date)
        : current.nonWorkingDates,
    }));
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    setMessage(null);
    try {
      const response = await apiService.updateProceduralDeadlineCalendarSettings({
        workingDates: normalizeDateRows(settings.workingDates),
        nonWorkingDates: normalizeDateRows(settings.nonWorkingDates),
        updatedAt: settings.updatedAt ?? null,
      });
      setSettings({
        workingDates: normalizeDateRows(response.workingDates ?? []),
        nonWorkingDates: normalizeDateRows(response.nonWorkingDates ?? []),
        updatedAt: response.updatedAt ?? null,
      });
      setMessage('Производственный календарь сохранен.');
      toast.success('Производственный календарь сохранен.', 'Настройки');
    } catch (err) {
      const nextError = extractDisplayErrorMessage(err, 'Не удалось сохранить производственный календарь.');
      setError(nextError);
      toast.error(nextError, 'Настройки');
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h4 className="inline-flex items-center gap-2 text-xl font-semibold text-gray-900">
            <CalendarDays className="h-5 w-5 text-blue-600" />
            Производственный календарь сроков
          </h4>
          <p className="mt-1 text-sm text-gray-600">
            Дополнительные рабочие и нерабочие дни применяются при расчете процессуальных сроков в карточке дела.
          </p>
        </div>
        <div className="rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-500">
          Обновлено: {updatedLabel}
        </div>
      </div>

      {error ? <div className="mt-4 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{error}</div> : null}
      {message ? <div className="mt-4 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">{message}</div> : null}

      <div className="mt-5 grid gap-3 lg:grid-cols-[180px_170px_minmax(220px,1fr)_auto]">
        <label className="text-sm font-medium text-slate-700">
          <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Тип</span>
          <select
            aria-label="Тип исключения календаря"
            value={draftKind}
            onChange={(event) => setDraftKind(event.target.value as CalendarDateKind)}
            className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          >
            <option value="nonWorking">Нерабочий день</option>
            <option value="working">Рабочий день</option>
          </select>
        </label>
        <label className="text-sm font-medium text-slate-700">
          <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Дата</span>
          <input
            aria-label="Дата исключения календаря"
            type="date"
            value={draftDate}
            onChange={(event) => setDraftDate(event.target.value)}
            className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
          />
        </label>
        <label className="text-sm font-medium text-slate-700">
          <span className="mb-1 block text-xs uppercase tracking-[0.12em] text-slate-400">Комментарий</span>
          <input
            aria-label="Комментарий исключения календаря"
            value={draftNote}
            onChange={(event) => setDraftNote(event.target.value)}
            className="w-full rounded-lg border border-gray-300 px-3 py-2 text-sm"
            placeholder="Например, перенос выходного"
          />
        </label>
        <button
          type="button"
          onClick={addDraftDate}
          className="inline-flex items-center justify-center gap-2 rounded-lg border border-blue-200 px-4 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-50 lg:self-end"
        >
          <Plus className="h-4 w-4" />
          Добавить
        </button>
      </div>

      {loading ? (
        <div className="mt-5 inline-flex items-center gap-2 text-sm text-slate-500">
          <Loader2 className="h-4 w-4 animate-spin" />
          Загрузка календаря...
        </div>
      ) : !hasRows ? (
        <div className="mt-5 rounded-lg border border-dashed border-slate-200 p-4 text-sm text-slate-500">
          Исключения производственного календаря пока не добавлены.
        </div>
      ) : (
        <div className="mt-5 grid gap-4 lg:grid-cols-2">
          <CalendarDateList title="Дополнительные нерабочие дни" kind="nonWorking" rows={settings.nonWorkingDates} onRemove={removeDate} />
          <CalendarDateList title="Дополнительные рабочие дни" kind="working" rows={settings.workingDates} onRemove={removeDate} />
        </div>
      )}

      <div className="mt-5 flex justify-end">
        <button
          type="button"
          onClick={() => void save()}
          disabled={loading || saving}
          className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
        >
          {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
          {saving ? 'Сохранение...' : 'Сохранить календарь'}
        </button>
      </div>
    </section>
  );
}

function CalendarDateList({
  title,
  kind,
  rows,
  onRemove,
}: {
  title: string;
  kind: CalendarDateKind;
  rows: ProceduralDeadlineCalendarDate[];
  onRemove: (kind: CalendarDateKind, date: string) => void;
}) {
  return (
    <div className="rounded-lg border border-slate-200 p-4">
      <div className="text-sm font-semibold text-slate-900">{title}</div>
      {rows.length === 0 ? (
        <div className="mt-3 text-sm text-slate-500">Даты не добавлены.</div>
      ) : (
        <div className="mt-3 space-y-2">
          {rows.map((row) => (
            <div key={row.date} className="flex items-center justify-between gap-3 rounded-lg bg-slate-50 px-3 py-2">
              <div className="min-w-0">
                <div className="font-semibold text-slate-900">{toRuDate(row.date)}</div>
                {row.note ? <div className="text-sm text-slate-500">{row.note}</div> : null}
              </div>
              <button
                type="button"
                onClick={() => onRemove(kind, row.date)}
                aria-label={`Удалить исключение ${row.date}`}
                className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-rose-200 text-rose-600 hover:bg-rose-50"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
