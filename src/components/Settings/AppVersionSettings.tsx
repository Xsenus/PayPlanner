import { useCallback, useEffect, useMemo, useState } from 'react';
import { History, Loader2, Plus, Save } from 'lucide-react';
import { apiService, type AppReleaseAdminDto, type AppReleaseItemDto, type SaveAppReleaseRequest } from '../../services/api';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

type ItemType = 'new' | 'improved' | 'fixed' | 'important';
type ItemDrafts = Record<ItemType, string>;

const ITEM_LABELS: Record<ItemType, string> = {
  new: 'Новое',
  improved: 'Улучшено',
  fixed: 'Исправлено',
  important: 'Важно',
};

const EMPTY_ITEMS: ItemDrafts = {
  new: '',
  improved: '',
  fixed: '',
  important: '',
};

type FormState = {
  id: number | null;
  releaseId: string;
  version: string;
  releasedAt: string;
  title: string;
  summary: string;
  isActive: boolean;
  items: ItemDrafts;
};

function todayInputDate() {
  return new Date().toISOString().slice(0, 10);
}

function emptyForm(): FormState {
  return {
    id: null,
    releaseId: '',
    version: '',
    releasedAt: todayInputDate(),
    title: '',
    summary: '',
    isActive: true,
    items: { ...EMPTY_ITEMS },
  };
}

function toInputDate(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value.slice(0, 10);
  }
  return date.toISOString().slice(0, 10);
}

function toForm(release: AppReleaseAdminDto): FormState {
  const grouped: ItemDrafts = { ...EMPTY_ITEMS };
  release.items.forEach((item) => {
    const type = (item.type in grouped ? item.type : 'improved') as ItemType;
    grouped[type] = [grouped[type], item.text].filter(Boolean).join('\n');
  });

  return {
    id: release.id,
    releaseId: release.releaseId,
    version: release.version,
    releasedAt: toInputDate(release.releasedAt),
    title: release.title,
    summary: release.summary,
    isActive: release.isActive,
    items: grouped,
  };
}

function splitItems(items: ItemDrafts): AppReleaseItemDto[] {
  return (Object.keys(items) as ItemType[]).flatMap((type) =>
    items[type]
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
      .map((text) => ({ type, text })),
  );
}

function toPayload(form: FormState): SaveAppReleaseRequest {
  return {
    releaseId: form.releaseId.trim(),
    version: form.version.trim(),
    releasedAt: `${form.releasedAt}T00:00:00`,
    title: form.title.trim(),
    summary: form.summary.trim(),
    isActive: form.isActive,
    items: splitItems(form.items),
  };
}

export function AppVersionSettings() {
  const toast = useToast();
  const [releases, setReleases] = useState<AppReleaseAdminDto[]>([]);
  const [form, setForm] = useState<FormState>(() => emptyForm());
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const selectedRelease = useMemo(
    () => releases.find((release) => release.id === form.id) ?? null,
    [form.id, releases],
  );

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await apiService.getAdminAppReleases();
      setReleases(data);
      setForm((current) => {
        if (current.id) {
          const updated = data.find((release) => release.id === current.id);
          return updated ? toForm(updated) : current;
        }
        return data[0] ? toForm(data[0]) : current;
      });
    } catch (e) {
      const message = extractDisplayErrorMessage(e, 'Не удалось загрузить релизы приложения.');
      setError(message);
      toast.error(message, 'Версия приложения');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => {
    void load();
  }, [load]);

  const updateItemGroup = (type: ItemType, value: string) => {
    setForm((current) => ({
      ...current,
      items: {
        ...current.items,
        [type]: value,
      },
    }));
  };

  const save = async () => {
    const payload = toPayload(form);
    if (!payload.releaseId || !payload.version || !payload.title || payload.items.length === 0) {
      toast.error('Заполните releaseId, версию, название и хотя бы один пункт изменений.', 'Версия приложения');
      return;
    }

    setSaving(true);
    try {
      const saved = form.id
        ? await apiService.updateAdminAppRelease(form.id, payload)
        : await apiService.createAdminAppRelease(payload);
      toast.success('Релиз сохранён.', 'Версия приложения');
      setForm(toForm(saved));
      await load();
    } catch (e) {
      toast.error(extractDisplayErrorMessage(e, 'Не удалось сохранить релиз.'), 'Версия приложения');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="grid gap-6 xl:grid-cols-[320px_1fr]">
      <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
        <div className="flex items-center justify-between gap-3">
          <div>
            <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">Релизы</div>
            <h4 className="mt-1 text-lg font-semibold text-slate-900">История обновлений</h4>
          </div>
          <button
            type="button"
            onClick={() => setForm(emptyForm())}
            className="inline-flex h-9 w-9 items-center justify-center rounded-lg border border-slate-200 text-slate-600 transition hover:bg-slate-50"
            title="Новый релиз"
            aria-label="Новый релиз"
          >
            <Plus className="h-4 w-4" />
          </button>
        </div>

        {loading ? (
          <div className="mt-5 flex items-center gap-2 text-sm text-slate-500">
            <Loader2 className="h-4 w-4 animate-spin" />
            Загружаем релизы...
          </div>
        ) : null}

        {error ? <div className="mt-4 rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">{error}</div> : null}

        <div className="mt-4 space-y-2">
          {releases.map((release) => (
            <button
              key={release.id}
              type="button"
              onClick={() => setForm(toForm(release))}
              className={`w-full rounded-xl border px-3 py-3 text-left transition ${
                form.id === release.id
                  ? 'border-blue-200 bg-blue-50 text-blue-950'
                  : 'border-slate-200 bg-white text-slate-700 hover:bg-slate-50'
              }`}
            >
              <div className="flex items-center justify-between gap-2">
                <span className="text-sm font-semibold">Версия {release.version}</span>
                <span className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${release.isActive ? 'bg-emerald-100 text-emerald-700' : 'bg-slate-100 text-slate-500'}`}>
                  {release.isActive ? 'активен' : 'скрыт'}
                </span>
              </div>
              <div className="mt-1 truncate text-xs text-slate-500">{release.title}</div>
              <div className="mt-1 text-[11px] text-slate-400">{release.releaseId}</div>
            </button>
          ))}
          {releases.length === 0 && !loading ? (
            <div className="rounded-xl border border-dashed border-slate-300 px-3 py-6 text-center text-sm text-slate-500">
              Релизы ещё не добавлены.
            </div>
          ) : null}
        </div>
      </div>

      <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm">
        <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">
              <History className="h-4 w-4" />
              Что нового
            </div>
            <h4 className="mt-2 text-xl font-semibold text-slate-900">
              {form.id ? 'Редактирование релиза' : 'Новый релиз'}
            </h4>
            {selectedRelease ? (
              <p className="mt-1 text-sm text-slate-500">Источник: {selectedRelease.source}</p>
            ) : null}
          </div>
          <button
            type="button"
            onClick={() => void save()}
            disabled={saving}
            className="inline-flex items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Сохранить
          </button>
        </div>

        <div className="grid gap-4 md:grid-cols-2">
          <label className="block">
            <span className="text-sm font-medium text-slate-700">Release ID</span>
            <input
              value={form.releaseId}
              onChange={(event) => setForm((current) => ({ ...current, releaseId: event.target.value }))}
              placeholder="2026-05-21-accounting-v2"
              className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            />
          </label>
          <label className="block">
            <span className="text-sm font-medium text-slate-700">Версия</span>
            <input
              value={form.version}
              onChange={(event) => setForm((current) => ({ ...current, version: event.target.value }))}
              placeholder="1.12.0"
              className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            />
          </label>
          <label className="block">
            <span className="text-sm font-medium text-slate-700">Дата релиза</span>
            <input
              type="date"
              value={form.releasedAt}
              onChange={(event) => setForm((current) => ({ ...current, releasedAt: event.target.value }))}
              className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
            />
          </label>
          <label className="flex items-center gap-2 self-end rounded-lg border border-slate-200 px-3 py-2 text-sm text-slate-700">
            <input
              type="checkbox"
              checked={form.isActive}
              onChange={(event) => setForm((current) => ({ ...current, isActive: event.target.checked }))}
              className="h-4 w-4 rounded border-slate-300 text-blue-600"
            />
            Показывать пользователям
          </label>
        </div>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Название</span>
          <input
            value={form.title}
            onChange={(event) => setForm((current) => ({ ...current, title: event.target.value }))}
            placeholder="Обновление бухгалтерии"
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
          />
        </label>

        <label className="mt-4 block">
          <span className="text-sm font-medium text-slate-700">Краткое описание</span>
          <textarea
            value={form.summary}
            onChange={(event) => setForm((current) => ({ ...current, summary: event.target.value }))}
            rows={3}
            className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
          />
        </label>

        <div className="mt-5 grid gap-4 md:grid-cols-2">
          {(Object.keys(ITEM_LABELS) as ItemType[]).map((type) => (
            <label key={type} className="block">
              <span className="text-sm font-medium text-slate-700">{ITEM_LABELS[type]}</span>
              <textarea
                value={form.items[type]}
                onChange={(event) => updateItemGroup(type, event.target.value)}
                rows={5}
                placeholder="Один пункт на строку"
                className="mt-1 w-full rounded-lg border border-slate-200 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-100"
              />
            </label>
          ))}
        </div>
      </div>
    </div>
  );
}
