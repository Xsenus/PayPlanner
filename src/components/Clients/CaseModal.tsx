import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { X, Trash2 } from 'lucide-react';
import type { ClientCase } from '../../types';

interface CaseModalProps {
  isOpen: boolean;
  onClose: () => void;
  caseData: ClientCase; // для create передаём «пустой» кейс
  onSave: (patch: Partial<ClientCase>) => Promise<void> | void;
  onDelete?: () => Promise<void> | void;
}

type StatusValue = 'Open' | 'OnHold' | 'Closed';

const STATUS_OPTIONS: Array<{ value: StatusValue; label: string }> = [
  { value: 'Open', label: 'Открыто' },
  { value: 'OnHold', label: 'Приостановлено' },
  { value: 'Closed', label: 'Закрыто' },
];

export function CaseModal({ isOpen, onClose, caseData, onSave, onDelete }: CaseModalProps) {
  const [title, setTitle] = useState(caseData.title ?? '');
  const [description, setDescription] = useState(caseData.description ?? '');
  const [status, setStatus] = useState<StatusValue>((caseData.status as StatusValue) ?? 'Open');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setTitle(caseData.title ?? '');
    setDescription(caseData.description ?? '');
    setStatus((caseData.status as StatusValue) ?? 'Open');
    setError(null);
    setSaving(false);
  }, [caseData, isOpen]);

  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'enter') {
        void handleSave();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, title, description, status]);

  if (!isOpen) return null;

  const validate = (): string | null => {
    if (!title.trim()) return 'Введите название дела';
    return null;
  };

  const handleSave = async () => {
    const v = validate();
    if (v) {
      setError(v);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const patch: Partial<ClientCase> = {
        title: title.trim(),
        description: description.trim(),
        status,
      };
      await onSave(patch);
      onClose();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async () => {
    if (!onDelete) return;
    const ok = window.confirm('Удалить это дело? Действие необратимо.');
    if (!ok) return;
    setSaving(true);
    setError(null);
    try {
      await onDelete();
      onClose();
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const modal = (
    <div className="fixed inset-0 z-[1000] bg-black/30 flex items-center justify-center p-4">
      <div
        className="w-full max-w-xl bg-white rounded-2xl shadow-xl"
        role="dialog"
        aria-modal="true">
        <div className="flex items-center justify-between px-5 py-4 border-b">
          <h3 className="text-lg font-semibold">Дело</h3>
          <button
            type="button"
            onClick={onClose}
            className="p-2 rounded-lg hover:bg-gray-100"
            aria-label="Закрыть">
            <X size={18} />
          </button>
        </div>

        <div className="px-5 py-4 space-y-4">
          {error && (
            <div className="text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg px-3 py-2">
              {error}
            </div>
          )}

          <div>
            <label className="text-sm text-gray-600">
              Название <span className="text-red-500">*</span>
            </label>
            <input
              className="mt-1 w-full border rounded-lg px-3 py-2"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="Например: Соглашение №123"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Описание</label>
            <textarea
              className="mt-1 w-full border rounded-lg px-3 py-2"
              rows={3}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Короткое описание дела"
              disabled={saving}
            />
          </div>

          <div>
            <label className="text-sm text-gray-600">Статус</label>
            <select
              className="mt-1 w-full border rounded-lg px-3 py-2 bg-white"
              value={status}
              onChange={(e) => setStatus(e.target.value as StatusValue)}
              disabled={saving}>
              {STATUS_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="px-5 py-4 border-t flex items-center justify-between">
          {onDelete ? (
            <button
              type="button"
              onClick={handleDelete}
              className="inline-flex items-center gap-2 px-4 py-2 rounded-lg border border-red-300 text-red-600 hover:bg-red-50 disabled:opacity-60"
              disabled={saving}>
              <Trash2 size={16} /> Удалить
            </button>
          ) : (
            <span />
          )}

          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 rounded-lg border hover:bg-gray-50 disabled:opacity-60"
              disabled={saving}>
              Отмена
            </button>
            <button
              type="button"
              onClick={handleSave}
              className="px-4 py-2 rounded-lg bg-blue-600 text-white hover:bg-blue-700 disabled:opacity-60"
              disabled={saving}
              title="Ctrl/Cmd + Enter">
              Сохранить
            </button>
          </div>
        </div>
      </div>
    </div>
  );

  return createPortal(modal, document.body);
}
