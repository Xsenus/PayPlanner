import { useEffect, useMemo, useRef, useState } from 'react';

import { apiService } from '../../services/api';
import type { RequisitesIssue } from '../../types';
import { captureAuthSessionIdentity, isAuthSessionIdentityCurrent } from '../../services/authService';
import { extractDisplayErrorMessage } from '../../utils/errorUtils';

interface CompanyRequisitesCheckProps {
  companyId: number;
  /** Меняется после сохранения карточки, чтобы проверка выполнилась заново. */
  refreshKey?: string | number | null;
}

/** Замечания по реквизитам собственной компании: контрольные разряды ИНН, КПП, ОГРН, БИК и счетов. Сохранению не мешают. */
export function CompanyRequisitesCheck({ companyId, refreshKey }: CompanyRequisitesCheckProps) {
  const { epoch, actor, authenticated } = captureAuthSessionIdentity();
  const session = useMemo(() => ({ epoch, actor, authenticated }), [epoch, actor, authenticated]);
  const scope = JSON.stringify([companyId, refreshKey, session]);
  const scopeRef = useRef(scope);
  scopeRef.current = scope;
  const [attempt, setAttempt] = useState(0);
  const [result, setResult] = useState<{ scope: string; attempt: number; issues: RequisitesIssue[]; error: string | null } | null>(null);
  const currentResult = result?.scope === scope && result.attempt === attempt ? result : null;
  const issues = currentResult?.issues ?? [];

  useEffect(() => {
    const controller = new AbortController();
    const current = () => !controller.signal.aborted && isAuthSessionIdentityCurrent(session);
    if (!current()) return;
    setResult(null);
    apiService
      .getCompanyRequisitesCheck(companyId, controller.signal)
      .then((value) => {
        if (!current()) return;
        if (!value || value.legalEntityId !== companyId || !Array.isArray(value.issues)
          || !value.issues.every(issue => issue && typeof issue.field === 'string' && issue.field.trim() && typeof issue.message === 'string' && issue.message.trim()))
          throw new Error('Не удалось подтвердить результат проверки этой компании. Повторите проверку.');
        setResult({ scope, attempt, issues: value.issues, error: null });
      })
      .catch((error) => {
        if (current()) setResult({ scope, attempt, issues: [], error: extractDisplayErrorMessage(error, 'Не удалось проверить сохранённые реквизиты. Повторите проверку.') });
      });
    return () => controller.abort();
  }, [companyId, scope, attempt, session]);

  return (
    <div className={`mt-3 rounded-xl border px-3 py-2 text-sm ${issues.length ? 'border-amber-200 bg-amber-50 text-amber-900' : 'border-slate-200 bg-slate-50 text-slate-700'}`} data-testid="company-requisites-check">
      <div className="font-semibold">Проверка сохранённых реквизитов</div>
      {!currentResult ? <p role="status" className="mt-1">Проверяем сохранённые реквизиты…</p> : currentResult.error ? (
        <div role="alert" className="mt-1 text-rose-700">
          <p>{currentResult.error}</p>
          <button type="button" className="mt-1 underline" onClick={() => {
            if (scopeRef.current === scope && isAuthSessionIdentityCurrent(session)) setAttempt(value => value + 1);
          }}>Повторить проверку</button>
        </div>
      ) : issues.length === 0 ? <p role="status" className="mt-1">По проверяемым реквизитам замечаний нет.</p> : <ul className="mt-1 list-disc pl-5">
        {issues.map((issue, index) => (
          <li key={`${issue.field}-${index}`}>{issue.message}</li>
        ))}
      </ul>}
      <p className="mt-1 text-xs">Проверяются форматы и контрольные цифры в сохранённом профиле. Изменения в форме проверяются после сохранения; замечания не блокируют сохранение.</p>
    </div>
  );
}
