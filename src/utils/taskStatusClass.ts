// Shared presentation of the existing journal statuses; does not define transitions.
export function taskStatusClass(status: string): string {
  switch (status) {
    case 'done':
    case 'approved': return 'bg-emerald-50 text-emerald-700';
    case 'cancelled': return 'bg-slate-100 text-slate-500';
    case 'waiting': return 'bg-amber-50 text-amber-700';
    case 'onApproval': return 'bg-violet-50 text-violet-700';
    case 'revision': return 'bg-orange-50 text-orange-700';
    case 'overdueApproval': return 'bg-rose-50 text-rose-700';
    case 'inProgress': return 'bg-blue-50 text-blue-700';
    default: return 'bg-slate-50 text-slate-700';
  }
}
