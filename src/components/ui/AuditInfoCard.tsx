interface AuditInfoCardProps {
  createdAt?: string | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserName?: string | null;
  isDeleted?: boolean | null;
  deletedAt?: string | null;
  deletedByUserName?: string | null;
  compact?: boolean;
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
    <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
      <div className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-400">{label}</div>
      {hasValue ? (
        <div className="mt-2 space-y-1">
          <div className="text-sm font-semibold text-slate-900">{userName || 'Неизвестно'}</div>
          <div className="text-xs text-slate-500">{formattedDate || 'Дата не указана'}</div>
        </div>
      ) : (
        <div className="mt-2 text-sm text-slate-500">{emptyText}</div>
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
}: AuditInfoCardProps) {
  return (
    <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-xs font-semibold uppercase tracking-[0.2em] text-slate-400">Паспорт объекта</div>
          <h3 className="mt-1 text-base font-semibold text-slate-900">История записи</h3>
        </div>
        <span className="rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-500">
          Базовый аудит
        </span>
      </div>
      <div className={`mt-4 grid gap-3 ${compact ? '' : 'md:grid-cols-2 xl:grid-cols-3'}`}>
        <AuditInfoRow
          label="Создал"
          userName={createdByUserName}
          date={createdAt}
          emptyText="Автор не сохранён для старых данных"
        />
        <AuditInfoRow
          label="Изменил"
          userName={updatedByUserName}
          date={updatedAt}
          emptyText="Изменений пока нет"
        />
        {(isDeleted || deletedAt || deletedByUserName) && (
          <AuditInfoRow
            label="Удалил"
            userName={deletedByUserName}
            date={deletedAt}
            emptyText="Удаление не зафиксировано"
          />
        )}
      </div>
    </section>
  );
}
