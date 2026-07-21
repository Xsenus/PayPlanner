import { SystemCheckbox } from '../ui/SystemCheckbox';
import {
  ALL_TASK_NOTIFICATION_DEFAULTS,
  DEFAULT_TASK_NOTIFICATION_DEFAULTS,
  normalizeTaskNotificationDefaults,
  TASK_CHANNEL_DEFAULT_OPTIONS,
  TASK_REMINDER_DEFAULT_OPTIONS,
  type TaskNotificationDefaults,
} from '../../utils/taskNotificationDefaults';

type TaskNotificationDefaultsPanelProps = {
  value: TaskNotificationDefaults;
  onChange: (value: TaskNotificationDefaults) => void;
  disabled?: boolean;
};

function toggleValue<T extends string>(values: T[], value: T) {
  return values.includes(value) ? values.filter((item) => item !== value) : [...values, value];
}

export function TaskNotificationDefaultsPanel({ value, onChange, disabled = false }: TaskNotificationDefaultsPanelProps) {
  const normalized = normalizeTaskNotificationDefaults(value);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          onClick={() => onChange(ALL_TASK_NOTIFICATION_DEFAULTS)}
          disabled={disabled}
          className="rounded-xl border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-semibold text-blue-700 transition hover:bg-blue-100 disabled:cursor-not-allowed disabled:opacity-60"
        >
          Включить все
        </button>
        <button
          type="button"
          onClick={() => onChange(DEFAULT_TASK_NOTIFICATION_DEFAULTS)}
          disabled={disabled}
          className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
        >
          По умолчанию
        </button>
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <div>
          <div className="text-sm font-semibold text-slate-700">Напоминания</div>
          <div className="mt-3 grid gap-2 sm:grid-cols-2">
            {TASK_REMINDER_DEFAULT_OPTIONS.map((option) => (
              <label key={option.value} className="flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700">
                <SystemCheckbox
                  type="checkbox"
                  checked={normalized.reminders.includes(option.value)}
                  disabled={disabled}
                  onChange={() => onChange({ ...normalized, reminders: toggleValue(normalized.reminders, option.value) })}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                {option.label}
              </label>
            ))}
          </div>
        </div>

        <div>
          <div className="text-sm font-semibold text-slate-700">Каналы уведомлений</div>
          <div className="mt-3 grid gap-2 sm:grid-cols-3">
            {TASK_CHANNEL_DEFAULT_OPTIONS.map((option) => (
              <label key={option.value} className="flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700">
                <SystemCheckbox
                  type="checkbox"
                  checked={normalized.channels.includes(option.value)}
                  disabled={disabled}
                  onChange={() => onChange({ ...normalized, channels: toggleValue(normalized.channels, option.value) })}
                  className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                {option.label}
              </label>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
