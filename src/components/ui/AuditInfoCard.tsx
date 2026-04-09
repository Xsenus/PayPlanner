interface AuditInfoCardProps {
  createdAt?: string | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserName?: string | null;
  isDeleted?: boolean | null;
  deletedAt?: string | null;
  deletedByUserName?: string | null;
  compact?: boolean;
  layout?: 'auto' | 'stack';
  className?: string;
}

interface AuditInfoRowProps {
  label: string;
  userName?: string | null;
  date?: string | null;
  emptyText: string;
}

function formatAuditDate(value?: string | null): string | null {
  if (!value) {
    return null;
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return null;
  }

  return date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function AuditInfoRow({ label, userName, date, emptyText }: AuditInfoRowProps) {
  const formattedDate = formatAuditDate(date);
  const hasValue = Boolean(userName || formattedDate);

  return (
    <div className="min-w-0 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-4">
      <div className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-400">{label}</div>
      {hasValue ? (
        <div className="mt-3 space-y-2">
          <div className="break-words text-sm font-semibold leading-6 text-slate-900">
            {userName || 'Неизвестно'}
          </div>
          <div className="text-xs leading-5 text-slate-500">{formattedDate || 'Дата не указана'}</div>
        </div>
      ) : (
        <div className="mt-3 text-sm leading-6 text-slate-500">{emptyText}</div>
      )}
    </div>
  );
}

export function AuditInfoCard({
  createdAt,
  createdByUserName,
  updatedAt,
  updatedByUserName,
  isDeleted,
  deletedAt,
  deletedByUserName,
  compact = false,
  layout = 'auto',
  className = '',
}: AuditInfoCardProps) {
  const gridTemplateColumns =
    layout === 'stack'
      ? 'minmax(0, 1fr)'
      : compact
        ? 'repeat(auto-fit, minmax(160px, 1fr))'
        : 'repeat(auto-fit, minmax(220px, 1fr))';

  return (
    <section className={`rounded-3xl border border-slate-200 bg-white p-5 shadow-sm ${className}`}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="text-xs font-semibold uppercase tracking-[0.2em] text-slate-400">Паспорт объекта</div>
          <h3 className="mt-2 text-lg font-semibold text-slate-900">Базовый аудит записи</h3>
          <p className="mt-1 text-sm text-slate-500">
            Кто создал объект, когда он обновлялся и были ли зафиксированы удаления.
          </p>
        </div>
        <span className="rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-500">
          Базовый аудит
        </span>
      </div>

      <div className="mt-5 grid gap-3" style={{ gridTemplateColumns }}>
        <AuditInfoRow
          label="Создал"
          userName={createdByUserName}
          date={createdAt}
          emptyText="Автор не сохранен для старых данных."
        />
        <AuditInfoRow
          label="Изменил"
          userName={updatedByUserName}
          date={updatedAt}
          emptyText="Изменений по объекту пока нет."
        />
        {(isDeleted || deletedAt || deletedByUserName) && (
          <AuditInfoRow
            label="Удалил"
            userName={deletedByUserName}
            date={deletedAt}
            emptyText="Удаление не зафиксировано."
          />
        )}
      </div>
    </section>
  );
}
