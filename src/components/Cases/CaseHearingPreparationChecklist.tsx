import { useEffect, useState } from 'react';

import { apiService } from '../../services/api';
import type { ClientCase, ClientCaseDocument, ClientCaseHearingPreparation } from '../../types';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';
import { formatDateTimeInTimeZone } from '../../utils/timeZoneDate';

type CheckKind = 'contract' | 'authority' | 'materials';

interface Props {
  caseItem: ClientCase;
  timeZoneId: string;
  canEdit: boolean;
  datesSaved?: boolean;
  initialDocuments?: ClientCaseDocument[];
}

const checks: Array<{ kind: CheckKind; title: string; hint: string; at: keyof ClientCaseHearingPreparation; by: keyof ClientCaseHearingPreparation }> = [
  { kind: 'contract', title: 'Договор и реквизиты', hint: 'Проверьте действующий договор и реквизиты сторон.', at: 'contractCheckedAt', by: 'contractCheckedByName' },
  { kind: 'authority', title: 'Полномочия представителя', hint: 'Убедитесь, что полномочия действуют на дату заседания.', at: 'authorityCheckedAt', by: 'authorityCheckedByName' },
  { kind: 'materials', title: 'Материалы к заседанию', hint: 'Привяжите подготовленные файлы из документов дела.', at: 'materialsCheckedAt', by: 'materialsCheckedByName' },
];

export function CaseHearingPreparationChecklist({ caseItem, timeZoneId, canEdit, datesSaved = true, initialDocuments }: Props) {
  const [preparation, setPreparation] = useState<ClientCaseHearingPreparation | null>(null);
  const [documents, setDocuments] = useState<ClientCaseDocument[]>([]);
  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState<CheckKind | null>(null);
  const [error, setError] = useState<string | null>(null);
  const hearingAt = Date.parse(caseItem.nextHearingAt ?? '');
  const available = Boolean(caseItem.id) && Number.isFinite(hearingAt) && hearingAt > Date.now()
    && !['Closed', 'Lost'].includes(caseItem.status);

  useEffect(() => {
    if (initialDocuments) setDocuments(initialDocuments);
  }, [initialDocuments]);

  useEffect(() => {
    if (!available || !datesSaved) {
      setPreparation(null);
      setDocuments([]);
      setSelectedIds([]);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    Promise.all([apiService.getCaseHearingPreparation(caseItem.id), initialDocuments ? Promise.resolve(initialDocuments) : apiService.getCaseDocuments(caseItem.id)])
      .then(([current, files]) => {
        if (cancelled) return;
        setPreparation(current);
        setSelectedIds(current.documentIds);
        setDocuments(files);
      })
      .catch((cause) => {
        if (!cancelled) setError(extractDisplayErrorMessage(cause, 'Не удалось загрузить подготовку к заседанию.'));
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [available, caseItem.id, caseItem.nextHearingAt, datesSaved, initialDocuments]);

  if (!available) return null;

  const save = async (kind: CheckKind, checked: boolean) => {
    if (!preparation || saving || !datesSaved) return;
    setSaving(kind);
    setError(null);
    try {
      const updated = await apiService.updateCaseHearingPreparation(caseItem.id, {
        hearingAt: preparation.hearingAt,
        checkKind: kind,
        checked,
        ...(kind === 'materials' ? { documentIds: selectedIds } : {}),
      });
      setPreparation(updated);
      setSelectedIds(updated.documentIds);
    } catch (cause) {
      setError(extractDisplayErrorMessage(cause, 'Не удалось сохранить проверку.'));
    } finally {
      setSaving(null);
    }
  };

  const refreshDocuments = async () => {
    setError(null);
    try {
      setDocuments(await apiService.getCaseDocuments(caseItem.id));
    } catch (cause) {
      setError(extractDisplayErrorMessage(cause, 'Не удалось обновить список документов.'));
    }
  };

  const selectedChanged = preparation && (selectedIds.length !== preparation.documentIds.length
    || selectedIds.some((id) => !preparation.documentIds.includes(id)));

  return (
    <section className="rounded-xl border border-sky-200 bg-sky-50/50 p-3 text-sm text-slate-800">
      <h3 className="font-semibold">Проверка перед заседанием</h3>
      <p className="mt-1 text-xs text-slate-600">Отметки относятся только к заседанию {formatDateTimeInTimeZone(caseItem.nextHearingAt!, timeZoneId)}. После переноса даты старые отметки не считаются готовностью к новому заседанию.</p>
      {!datesSaved ? <p className="mt-2 text-xs text-amber-800">Сначала сохраните новую дату заседания и ответственного по делу.</p> : null}
      {loading ? <p className="mt-2 text-xs text-slate-500">Загружаем проверки…</p> : null}
      {preparation ? (
        <div className="mt-2 space-y-2">
          {checks.map(({ kind, title, hint, at, by }) => {
            const reviewedAt = preparation[at] as string | null;
            const reviewer = preparation[by] as string;
            return (
              <div key={kind} className="rounded-lg border border-sky-100 bg-white p-2">
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <div className="text-xs font-semibold">{title}</div>
                    <p className="text-xs text-slate-500">{hint}</p>
                    <p className={`mt-1 text-xs ${reviewedAt ? 'text-emerald-700' : 'text-amber-800'}`}>
                      {reviewedAt ? `Проверено ${formatDateTimeInTimeZone(reviewedAt, timeZoneId)}${reviewer ? ` · ${reviewer}` : ''}` : 'Не проверено'}
                    </p>
                  </div>
                  {canEdit ? <button type="button" onClick={() => void save(kind, !reviewedAt)} disabled={Boolean(saving) || !datesSaved || (kind === 'materials' && !reviewedAt && selectedIds.length === 0)} className="shrink-0 rounded-lg border border-sky-200 px-2.5 py-1 text-xs font-semibold text-sky-800 hover:bg-sky-50 disabled:cursor-not-allowed disabled:opacity-50">{saving === kind ? 'Сохраняем…' : reviewedAt ? 'Снять отметку' : 'Подтвердить'}</button> : null}
                </div>
                {kind === 'materials' ? (
                  <div className="mt-2 border-t border-sky-100 pt-2">
                    <div className="flex items-center justify-between gap-2"><span className="text-xs font-medium">Документы этого заседания</span><button type="button" onClick={() => void refreshDocuments()} className="text-xs text-blue-700 hover:underline">Обновить список</button></div>
                    {documents.length === 0 ? <p className="mt-1 text-xs text-slate-500">В деле пока нет загруженных документов. Добавьте файл в разделе «Документы».</p> : null}
                    <div className="mt-1 max-h-40 space-y-1 overflow-y-auto">
                      {documents.map((document) => (
                        <label key={document.id} className="flex items-center gap-2 text-xs">
                          <input type="checkbox" checked={selectedIds.includes(document.id)} disabled={!canEdit || Boolean(saving) || !datesSaved} onChange={() => setSelectedIds((current) => current.includes(document.id) ? current.filter((id) => id !== document.id) : [...current, document.id])} />
                          <span className="truncate" title={document.originalFileName}>{document.originalFileName}</span>
                        </label>
                      ))}
                    </div>
                    {canEdit && selectedChanged ? <button type="button" onClick={() => void save('materials', false)} disabled={Boolean(saving)} className="mt-2 rounded-lg border border-sky-200 px-2.5 py-1 text-xs font-semibold text-sky-800 disabled:opacity-50">Сохранить связи с документами</button> : null}
                  </div>
                ) : null}
              </div>
            );
          })}
        </div>
      ) : null}
      {error ? <p role="alert" className="mt-2 text-xs text-rose-700">{error}</p> : null}
    </section>
  );
}
