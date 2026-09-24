export function localChatDayKey(value: string, timeZone?: string) {
  const date = new Date(value);
  if (!timeZone) {
    const pad = (part: number) => String(part).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  }

  const parts = new Intl.DateTimeFormat('en', {
    year: 'numeric', month: '2-digit', day: '2-digit', timeZone,
  }).formatToParts(date);
  const get = (type: 'year' | 'month' | 'day') => parts.find((part) => part.type === type)?.value;
  return `${get('year')}-${get('month')}-${get('day')}`;
}
