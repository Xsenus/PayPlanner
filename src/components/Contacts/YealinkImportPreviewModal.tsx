import { SystemSelect } from '../ui/SystemSelect';
import { AlertTriangle, CheckCircle2, Loader2, X } from 'lucide-react';
import type {
  YealinkContactImportDecisionAction,
  YealinkContactImportDetailDto,
  YealinkContactImportResultDto,
} from '../../services/api';

type DecisionMap = Record<number, YealinkContactImportDecisionAction>;

interface YealinkImportPreviewModalProps {
  fileName: string;
  preview: YealinkContactImportResultDto;
  decisions: DecisionMap;
  importing: boolean;
  onChangeDecision: (rowId: number, action: YealinkContactImportDecisionAction) => void;
  onClose: () => void;
  onConfirm: () => void;
}

function isExisting(detail: YealinkContactImportDetailDto) {
  return detail.action === 'Existing' || detail.action === 'SkippedExisting';
}

function canCreate(detail: YealinkContactImportDetailDto) {
  return detail.action === 'WouldCreateExternal' || detail.action === 'WouldCreate';
}

function canImport(detail: YealinkContactImportDetailDto) {
  return isExisting(detail) || canCreate(detail);
}

function actionLabel(action: YealinkContactImportDecisionAction, detail: YealinkContactImportDetailDto) {
  if (isExisting(detail)) {
    return action === 'updateExisting' ? 'Обновить существующий' : 'Оставить как есть';
  }

  if (action === 'createClient') {
    return 'Создать клиента';
  }

  if (action === 'createExternal') {
    return 'Создать сторонний';
  }

  return 'Не переносить';
}

function sourceLabel(sourceType?: string | null) {
  if (sourceType === 'client') {
    return 'Клиент';
  }

  if (sourceType === 'external') {
    return 'Сторонний';
  }

  return 'Контакт';
}

export function YealinkImportPreviewModal({
  fileName,
  preview,
  decisions,
  importing,
  onChangeDecision,
  onClose,
  onConfirm,
}: YealinkImportPreviewModalProps) {
  const importableDetails = preview.details.filter(canImport);
  const skippedDetails = preview.details.length - importableDetails.length;
  const decisionSummary = importableDetails.reduce(
    (acc, detail) => {
      const action = decisions[detail.rowId] ?? (isExisting(detail) ? 'skip' : 'createExternal');
      acc[action] += 1;
      return acc;
    },
    {
      skip: 0,
      createExternal: 0,
      createClient: 0,
      updateExisting: 0,
    } satisfies Record<YealinkContactImportDecisionAction, number>,
  );

  return (
    <div className="fixed inset-0 z-[1200] flex items-center justify-center bg-slate-950/60 p-4 backdrop-blur-sm">
      <div className="flex max-h-[92vh] w-full max-w-6xl flex-col overflow-hidden rounded-3xl bg-white shadow-2xl">
        <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-5">
          <div>
            <p className="text-xs font-semibold uppercase tracking-[0.2em] text-blue-500">Импорт Yealink XML</p>
            <h2 className="mt-1 text-2xl font-bold text-slate-900">Проверка контактов перед импортом</h2>
            <p className="mt-1 text-sm text-slate-500">
              Файл: <span className="font-medium text-slate-700">{fileName}</span>. Выберите, какие записи переносить и что
              делать с уже найденными контактами.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={importing}
            className="inline-flex h-10 w-10 items-center justify-center rounded-full border border-slate-200 text-slate-500 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
            aria-label="Закрыть окно импорта"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="overflow-y-auto px-6 py-5">
          <div className="grid gap-3 md:grid-cols-4">
            <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4">
              <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">В файле</p>
              <p className="mt-2 text-2xl font-bold text-slate-900">{preview.scanned}</p>
              <p className="mt-1 text-sm text-slate-500">с телефонами: {preview.withPhones}</p>
            </div>
            <div className="rounded-2xl border border-blue-100 bg-blue-50 p-4">
              <p className="text-xs font-semibold uppercase tracking-wide text-blue-600">Создать</p>
              <p className="mt-2 text-2xl font-bold text-blue-900">
                {decisionSummary.createExternal + decisionSummary.createClient}
              </p>
              <p className="mt-1 text-sm text-blue-700">
                сторонние: {decisionSummary.createExternal}, клиенты: {decisionSummary.createClient}
              </p>
            </div>
            <div className="rounded-2xl border border-emerald-100 bg-emerald-50 p-4">
              <p className="text-xs font-semibold uppercase tracking-wide text-emerald-600">Обновить</p>
              <p className="mt-2 text-2xl font-bold text-emerald-900">{decisionSummary.updateExisting}</p>
              <p className="mt-1 text-sm text-emerald-700">найденные контакты</p>
            </div>
            <div className="rounded-2xl border border-amber-100 bg-amber-50 p-4">
              <p className="text-xs font-semibold uppercase tracking-wide text-amber-600">Не переносить</p>
              <p className="mt-2 text-2xl font-bold text-amber-900">{decisionSummary.skip + skippedDetails}</p>
              <p className="mt-1 text-sm text-amber-700">включая дубли и записи без телефонов</p>
            </div>
          </div>

          <div className="mt-5 rounded-2xl border border-blue-100 bg-blue-50 px-4 py-3 text-sm text-blue-800">
            <div className="flex gap-2">
              <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" />
              <p>
                Новые записи по умолчанию создаются как сторонние контакты. Если запись уже найдена по телефону, безопасное
                действие по умолчанию - оставить её без изменений.
              </p>
            </div>
          </div>

          <div className="mt-5 overflow-hidden rounded-2xl border border-slate-200">
            <div className="max-h-[48vh] overflow-auto">
              <table className="min-w-full divide-y divide-slate-200 text-sm">
                <thead className="sticky top-0 z-10 bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
                  <tr>
                    <th className="px-4 py-3">Контакт из файла</th>
                    <th className="px-4 py-3">Телефоны</th>
                    <th className="px-4 py-3">Найдено в CRM</th>
                    <th className="px-4 py-3">Действие</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 bg-white">
                  {preview.details.map((detail) => {
                    const disabled = !canImport(detail);
                    const currentAction = decisions[detail.rowId] ?? (isExisting(detail) ? 'skip' : 'createExternal');
                    return (
                      <tr key={detail.rowId} className={disabled ? 'bg-slate-50/70 text-slate-400' : undefined}>
                        <td className="px-4 py-3 align-top">
                          <div className="font-semibold text-slate-900">{detail.name}</div>
                          <div className="mt-1 text-xs text-slate-500">{detail.group || 'Без группы'}</div>
                          {detail.message ? <div className="mt-2 text-xs text-slate-500">{detail.message}</div> : null}
                        </td>
                        <td className="px-4 py-3 align-top text-slate-700">
                          {detail.phones.length > 0 ? detail.phones.join(', ') : '-'}
                        </td>
                        <td className="px-4 py-3 align-top">
                          {detail.matchedSourceId ? (
                            <div className="rounded-xl border border-slate-200 bg-slate-50 px-3 py-2">
                              <div className="text-xs font-semibold uppercase tracking-wide text-slate-500">
                                {sourceLabel(detail.matchedSourceType)}
                              </div>
                              <div className="mt-1 font-medium text-slate-900">{detail.matchedName || 'Без названия'}</div>
                              <div className="mt-1 text-xs text-slate-500">
                                {detail.matchedPhones.length > 0 ? detail.matchedPhones.join(', ') : 'Телефон найден'}
                              </div>
                            </div>
                          ) : (
                            <span className="text-slate-400">Нет совпадений</span>
                          )}
                        </td>
                        <td className="px-4 py-3 align-top">
                          {disabled ? (
                            <div className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-xs font-medium text-slate-500">
                              <AlertTriangle className="h-4 w-4" />
                              Пропуск
                            </div>
                          ) : (
                            <SystemSelect
                              value={currentAction}
                              disabled={importing}
                              onChange={(event) => {
                                if (!importing) onChangeDecision(detail.rowId, event.target.value as YealinkContactImportDecisionAction);
                              }}
                              className="w-full rounded-xl border border-slate-300 bg-white px-3 py-2 text-sm text-slate-800 outline-none transition focus:border-blue-400 focus:ring-4 focus:ring-blue-100"
                            >
                              {isExisting(detail) ? (
                                <>
                                  <option value="skip">{actionLabel('skip', detail)}</option>
                                  <option value="updateExisting">{actionLabel('updateExisting', detail)}</option>
                                </>
                              ) : (
                                <>
                                  <option value="createExternal">{actionLabel('createExternal', detail)}</option>
                                  <option value="createClient">{actionLabel('createClient', detail)}</option>
                                  <option value="skip">{actionLabel('skip', detail)}</option>
                                </>
                              )}
                            </SystemSelect>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        </div>

        <div className="flex flex-col gap-3 border-t border-slate-200 bg-slate-50 px-6 py-4 sm:flex-row sm:justify-end">
          <button
            type="button"
            onClick={onClose}
            disabled={importing}
            className="rounded-xl border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-60"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={importing || importableDetails.length === 0}
            className="inline-flex items-center justify-center gap-2 rounded-xl bg-blue-600 px-5 py-2 text-sm font-semibold text-white transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {importing ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
            Импортировать выбранное
          </button>
        </div>
      </div>
    </div>
  );
}
