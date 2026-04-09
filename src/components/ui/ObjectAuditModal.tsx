import { History, Shield, X } from 'lucide-react';
import { useState } from 'react';
import { createPortal } from 'react-dom';
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
  if (!open || typeof document === 'undefined') {
    return null;
  }

  const objectTypeLabel = getObjectAuditTypeLabel(objectType);
  const resolvedTitle = title ?? `История объекта: ${objectTypeLabel}`;
  const resolvedSubtitle =
    subtitle ?? 'Паспорт объекта и все зафиксированные изменения открываются в отдельном окне.';

  return createPortal(
    <div className="fixed inset-0 z-[2300] flex items-center justify-center bg-slate-950/55 p-4">
      <div className="flex max-h-[90vh] w-full max-w-5xl flex-col overflow-hidden rounded-[28px] bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-5">
          <div className="min-w-0">
            <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.22em] text-slate-400">
              <Shield className="h-4 w-4" />
              Аудит объекта
            </div>
            <h2 className="mt-2 text-2xl font-semibold text-slate-900">{resolvedTitle}</h2>
            <p className="mt-2 max-w-3xl text-sm text-slate-500">{resolvedSubtitle}</p>
          </div>

          <button
            type="button"
            onClick={onClose}
            className="rounded-2xl border border-slate-200 p-2.5 text-slate-500 transition hover:bg-slate-50 hover:text-slate-700"
            aria-label="Закрыть аудит"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="overflow-y-auto px-6 py-5">
          <div className="grid gap-5 xl:grid-cols-[320px,minmax(0,1fr)]">
            <div className="space-y-4">
              <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-4">
                <div className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-400">
                  Объект
                </div>
                <div className="mt-2 text-lg font-semibold text-slate-900">{objectTypeLabel}</div>
                <div className="mt-1 text-sm text-slate-500">ID: {objectId}</div>
              </div>

              <AuditInfoCard
                createdAt={createdAt}
                createdByUserName={createdByUserName}
                updatedAt={updatedAt}
                updatedByUserName={updatedByUserName}
                isDeleted={isDeleted}
                deletedAt={deletedAt}
                deletedByUserName={deletedByUserName}
              />
            </div>

            <ObjectAuditHistoryCard objectType={objectType} objectId={objectId} />
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
  const [open, setOpen] = useState(false);

  if (!objectId || objectId <= 0) {
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
            <p className="mt-1 text-sm text-slate-600">{triggerDescription}</p>
          </div>

          <button
            type="button"
            onClick={() => setOpen(true)}
            className={`inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-slate-100 ${buttonClassName}`}
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
