import { Clock3, History, Shield, X } from 'lucide-react';
import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useAuth } from '../../contexts/AuthContext';
import { useRolePermissions } from '../../hooks/useRolePermissions';
import { getObjectAuditTypeLabel } from '../../utils/objectAudit';
import { AuditInfoCard } from './AuditInfoCard';
import { ObjectAuditHistoryCard } from './ObjectAuditHistoryCard';

type ObjectAuditModalProps = {
  open: boolean;
  onClose: () => void;
  objectType: string;
  objectId: number;
  createdAt?: string | null;
  createdByUserName?: string | null;
  updatedAt?: string | null;
  updatedByUserName?: string | null;
  isDeleted?: boolean | null;
  deletedAt?: string | null;
  deletedByUserName?: string | null;
  title?: string;
  subtitle?: string;
};

type ObjectAuditLauncherProps = Omit<ObjectAuditModalProps, 'open' | 'onClose'> & {
  buttonLabel?: string;
  triggerTitle?: string;
  triggerDescription?: string;
  className?: string;
  buttonClassName?: string;
};

export function ObjectAuditModal({
  open,
  onClose,
  objectType,
  objectId,
  createdAt,
  createdByUserName,
  updatedAt,
  updatedByUserName,
  isDeleted,
  deletedAt,
  deletedByUserName,
  title,
  subtitle,
}: ObjectAuditModalProps) {
  useEffect(() => {
    if (!open || typeof document === 'undefined') {
      return undefined;
    }

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose();
      }
    };

    document.addEventListener('keydown', handleKeyDown);

    return () => {
      document.body.style.overflow = previousOverflow;
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open, onClose]);

  if (!open || typeof document === 'undefined') {
    return null;
  }

  const objectTypeLabel = getObjectAuditTypeLabel(objectType);
  const resolvedTitle = title ?? `История объекта: ${objectTypeLabel}`;
  const resolvedSubtitle =
    subtitle ?? 'Здесь собраны базовые audit-данные и все изменения по выбранному объекту.';

  return createPortal(
    <div
      className="fixed inset-0 z-[2300] flex items-start justify-center bg-slate-950/55 p-3 sm:p-6 lg:items-center"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose();
        }
      }}
    >
      <div className="flex max-h-[92vh] w-full max-w-[1120px] flex-col overflow-hidden rounded-[32px] border border-slate-200 bg-white shadow-[0_30px_80px_rgba(15,23,42,0.28)]">
        <div className="border-b border-slate-200 bg-white px-5 py-5 sm:px-7">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0">
              <div className="inline-flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.24em] text-slate-400">
                <Shield className="h-4 w-4" />
                Аудит объекта
              </div>
              <h2 className="mt-3 text-2xl font-semibold text-slate-900 sm:text-[2rem]">{resolvedTitle}</h2>
              <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-500">{resolvedSubtitle}</p>
            </div>

            <button
              type="button"
              onClick={onClose}
              className="rounded-2xl border border-slate-200 p-3 text-slate-500 transition hover:bg-slate-50 hover:text-slate-700"
              aria-label="Закрыть аудит"
            >
              <X className="h-5 w-5" />
            </button>
          </div>
        </div>

        <div className="overflow-y-auto bg-slate-50/70 px-4 py-4 sm:px-6 sm:py-6">
          <div className="grid items-start gap-6 lg:grid-cols-[minmax(300px,360px),minmax(0,1fr)]">
            <aside className="space-y-4 lg:sticky lg:top-0">
              <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="flex items-start justify-between gap-4">
                  <div className="min-w-0">
                    <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
                      Объект
                    </div>
                    <div className="mt-2 break-words text-2xl font-semibold text-slate-900">{objectTypeLabel}</div>
                    <div className="mt-2 text-sm text-slate-500">ID: {objectId}</div>
                  </div>

                  <div className="rounded-2xl bg-blue-50 p-3 text-blue-600">
                    <History className="h-5 w-5" />
                  </div>
                </div>

                <div className="mt-4 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm leading-6 text-slate-600">
                  В отдельном окне удобнее просматривать полную историю объекта, не перегружая рабочую форму.
                </div>

                {(isDeleted || deletedAt || deletedByUserName) && (
                  <div className="mt-3 rounded-2xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm font-medium text-rose-700">
                    По объекту зафиксировано удаление в audit-истории.
                  </div>
                )}
              </div>

              <AuditInfoCard
                createdAt={createdAt}
                createdByUserName={createdByUserName}
                updatedAt={updatedAt}
                updatedByUserName={updatedByUserName}
                isDeleted={isDeleted}
                deletedAt={deletedAt}
                deletedByUserName={deletedByUserName}
                layout="stack"
              />

              <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm">
                <div className="flex items-start gap-3">
                  <div className="rounded-2xl bg-slate-100 p-2.5 text-slate-600">
                    <Clock3 className="h-4 w-4" />
                  </div>
                  <div>
                    <div className="text-sm font-semibold text-slate-900">Как читать аудит</div>
                    <p className="mt-1 text-sm leading-6 text-slate-500">
                      Слева показан паспорт объекта, справа — лента изменений. Каждое событие можно
                      просмотреть по полям без наложения элементов друг на друга.
                    </p>
                  </div>
                </div>
              </div>
            </aside>

            <div className="min-w-0">
              <ObjectAuditHistoryCard objectType={objectType} objectId={objectId} />
            </div>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}

export function ObjectAuditLauncher({
  objectType,
  objectId,
  createdAt,
  createdByUserName,
  updatedAt,
  updatedByUserName,
  isDeleted,
  deletedAt,
  deletedByUserName,
  title,
  subtitle,
  buttonLabel = 'История',
  triggerTitle = 'Аудит объекта',
  triggerDescription = 'Паспорт объекта и история изменений открываются в отдельном окне.',
  className = '',
  buttonClassName = '',
}: ObjectAuditLauncherProps) {
  const { user, isAdmin } = useAuth();
  const permissions = useRolePermissions(user?.role?.id);
  const [open, setOpen] = useState(false);
  const canViewAudit = isAdmin() || permissions.audit?.canView === true;

  if (!canViewAudit || !objectId || objectId <= 0) {
    return null;
  }

  return (
    <>
      <div className={`rounded-2xl border border-slate-200 bg-slate-50/80 px-4 py-3 ${className}`}>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="min-w-0">
            <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
              {triggerTitle}
            </div>
            <p className="mt-1 text-sm leading-6 text-slate-600">{triggerDescription}</p>
          </div>

          <button
            type="button"
            onClick={() => setOpen(true)}
            className={`inline-flex shrink-0 items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-100 ${buttonClassName}`}
          >
            <History className="h-4 w-4" />
            {buttonLabel}
          </button>
        </div>
      </div>

      <ObjectAuditModal
        open={open}
        onClose={() => setOpen(false)}
        objectType={objectType}
        objectId={objectId}
        createdAt={createdAt}
        createdByUserName={createdByUserName}
        updatedAt={updatedAt}
        updatedByUserName={updatedByUserName}
        isDeleted={isDeleted}
        deletedAt={deletedAt}
        deletedByUserName={deletedByUserName}
        title={title}
        subtitle={subtitle}
      />
    </>
  );
}
