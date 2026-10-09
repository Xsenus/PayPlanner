import { useEffect, useState } from 'react';

import { apiService } from '../../services/api';
import type { RequisitesIssue } from '../../types';

interface CompanyRequisitesCheckProps {
  companyId: number;
  /** Меняется после сохранения карточки, чтобы проверка выполнилась заново. */
  refreshKey?: string | number | null;
}

/** Замечания по реквизитам собственной компании: контрольные разряды ИНН, КПП, ОГРН, БИК и счетов. Сохранению не мешают. */
export function CompanyRequisitesCheck({ companyId, refreshKey }: CompanyRequisitesCheckProps) {
  const [issues, setIssues] = useState<RequisitesIssue[]>([]);

  useEffect(() => {
    let cancelled = false;
    apiService
      .getCompanyRequisitesCheck(companyId)
      .then((result) => {
        if (!cancelled) setIssues(result.issues);
      })
      .catch(() => {
        if (!cancelled) setIssues([]);
      });
    return () => {
      cancelled = true;
    };
  }, [companyId, refreshKey]);

  if (issues.length === 0) {
    return null;
  }

  return (
    <div className="mt-3 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900" data-testid="company-requisites-check">
      <div className="font-semibold">Проверьте реквизиты компании</div>
      <ul className="mt-1 list-disc pl-5">
        {issues.map((issue, index) => (
          <li key={`${issue.field}-${index}`}>{issue.message}</li>
        ))}
      </ul>
    </div>
  );
}
