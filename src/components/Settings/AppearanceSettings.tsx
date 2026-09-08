import { Check, Moon, Palette, Save, Sun } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useAppTheme } from '../../contexts/AppThemeContext';
import { useToast } from '../../hooks/useToast';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import type { AppThemeKey } from '../../utils/appThemes';

export function AppearanceSettings() {
  const { themeKey, themes, loading, saving, setGlobalTheme } = useAppTheme();
  const [selectedThemeKey, setSelectedThemeKey] = useState<AppThemeKey>(themeKey);
  const toast = useToast();

  useEffect(() => {
    setSelectedThemeKey(themeKey);
  }, [themeKey]);

  const handleSave = async () => {
    try {
      await setGlobalTheme(selectedThemeKey);
      toast.success('Единая тема оформления сохранена для всех сотрудников.');
    } catch (error) {
      toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить тему оформления.'));
    }
  };

  return (
    <div className="space-y-3">
      <section className="rounded-3xl border border-slate-200 bg-white p-3 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-3 rounded-2xl border border-slate-200 bg-slate-50 p-3">
          <div className="flex min-w-0 items-start gap-3">
            <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-blue-100 text-blue-700">
              <Palette className="h-5 w-5" />
            </div>
            <div>
              <h4 className="text-base font-semibold text-slate-900">Единый стиль PayPlanner</h4>
              <p className="mt-1 max-w-3xl text-sm leading-5 text-slate-600">
                Выбранная тема применяется ко всей CRM и одинакова для всех сотрудников. Цвета статусов и предупреждений сохраняют своё смысловое значение.
              </p>
            </div>
          </div>
          <button
            type="button"
            onClick={handleSave}
            disabled={loading || saving || selectedThemeKey === themeKey}
            className="inline-flex min-h-10 items-center gap-2 rounded-xl bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Save className="h-4 w-4" />
            {saving ? 'Сохранение…' : 'Применить для всех'}
          </button>
        </div>

        <div className="mt-3 grid gap-3 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
          {themes.map((theme) => {
            const selected = selectedThemeKey === theme.key;
            const active = themeKey === theme.key;
            return (
              <button
                key={theme.key}
                type="button"
                aria-pressed={selected}
                onClick={() => setSelectedThemeKey(theme.key)}
                className={`group overflow-hidden rounded-2xl border-2 text-left shadow-sm transition hover:-translate-y-0.5 hover:shadow-md ${
                  selected ? 'border-blue-500 ring-4 ring-blue-100' : 'border-slate-200'
                }`}
              >
                <span
                  className="block p-3"
                  style={{ backgroundColor: theme.backgroundColor, color: theme.isDark ? '#f8fafc' : '#172033' }}
                >
                  <span className="flex items-center justify-between gap-2">
                    <span className="inline-flex items-center gap-2 text-sm font-bold">
                      {theme.isDark ? <Moon className="h-4 w-4" /> : <Sun className="h-4 w-4" />}
                      {theme.name}
                    </span>
                    {selected && (
                      <span className="flex h-6 w-6 items-center justify-center rounded-full text-white" style={{ backgroundColor: theme.primaryColor }}>
                        <Check className="h-4 w-4" />
                      </span>
                    )}
                  </span>
                  <span className="mt-3 grid grid-cols-[1fr_2fr] gap-2">
                    <span className="h-12 rounded-lg border border-black/10" style={{ backgroundColor: theme.primaryColor }} />
                    <span className="h-12 rounded-lg border border-black/10 shadow-sm" style={{ backgroundColor: theme.surfaceColor }} />
                  </span>
                </span>
                <span className="block bg-white p-3">
                  <span className="flex items-center justify-between gap-2 text-sm font-semibold text-slate-900">
                    <span>{theme.name}</span>
                    {active && <span className="rounded-full bg-emerald-100 px-2 py-0.5 text-[11px] text-emerald-800">Действует</span>}
                  </span>
                  <span className="mt-1 block text-xs leading-5 text-slate-600">{theme.description}</span>
                </span>
              </button>
            );
          })}
        </div>
      </section>
    </div>
  );
}
