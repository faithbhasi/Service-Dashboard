// Dates are stored in UTC and shown in the time zone and date format chosen in Settings.

export interface FormatSettings { timeZone: string; dateFormat: string }

export const DATE_FORMATS = ['yyyy-MM-dd', 'dd/MM/yyyy', 'MM/dd/yyyy'] as const;

function parts(d: Date, timeZone: string) {
  let fmt: Intl.DateTimeFormat;
  try {
    fmt = new Intl.DateTimeFormat('en-GB', {
      timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    });
  } catch {
    fmt = new Intl.DateTimeFormat('en-GB', {
      year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    });
  }
  const map: Record<string, string> = {};
  for (const p of fmt.formatToParts(d)) map[p.type] = p.value;
  return map;
}

export function formatDate(iso: string | null | undefined, s: FormatSettings): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const p = parts(d, s.timeZone);
  return s.dateFormat.replace('yyyy', p.year).replace('MM', p.month).replace('dd', p.day);
}

export function formatDateTime(iso: string | null | undefined, s: FormatSettings): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const p = parts(d, s.timeZone);
  return `${formatDate(iso, s)} ${p.hour}:${p.minute}:${p.second}`;
}

export const defaultFormat: FormatSettings = { timeZone: 'UTC', dateFormat: 'yyyy-MM-dd' };
