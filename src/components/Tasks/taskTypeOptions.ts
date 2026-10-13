import { AlarmClock, Bell, ClipboardList, FileText, Phone, Scale, UsersRound, WalletCards } from 'lucide-react';
import type { TaskJournalType } from '../../utils/taskJournalStore';

export const TASK_TYPE_OPTIONS: Array<{ value: TaskJournalType; label: string; icon: typeof ClipboardList }> = [
  { value: 'task', label: 'Задача', icon: ClipboardList },
  { value: 'budget', label: 'Бюджет', icon: WalletCards },
  { value: 'contact', label: 'Связаться', icon: Phone },
  { value: 'document', label: 'Документ', icon: FileText },
  { value: 'reminder', label: 'Не забыть', icon: Bell },
  { value: 'control', label: 'Контроль', icon: AlarmClock },
  { value: 'courtHearing', label: 'Суд. заседание', icon: Scale },
  { value: 'meeting', label: 'Встреча', icon: UsersRound },
];
