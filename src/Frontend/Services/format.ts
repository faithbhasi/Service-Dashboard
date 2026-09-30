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

/** Milliseconds the given time zone is ahead of UTC at the given instant. */
export function zoneOffsetMs(date: Date, timeZone: string): number {
  const p = parts(date, timeZone);
  const asUtc = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour % 24, +p.minute, +p.second);
  return asUtc - Math.floor(date.getTime() / 1000) * 1000;
}

/** "2026-03-05T22:30" typed as wall-clock time in a time zone -> the UTC instant as an ISO string. */
export function zonedTimeToUtcIso(local: string, timeZone: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(local);
  if (!m) return '';
  const wall = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5]);
  // Two passes handle daylight-saving boundaries.
  let guess = wall - zoneOffsetMs(new Date(wall), timeZone);
  guess = wall - zoneOffsetMs(new Date(guess), timeZone);
  return new Date(guess).toISOString();
}

export function startOfTodayIso(timeZone: string, now = new Date()): string {
  const p = parts(now, timeZone);
  return zonedTimeToUtcIso(`${p.year}-${p.month}-${p.day}T00:00`, timeZone);
}

/** UTC instant -> "yyyy-MM-ddTHH:mm" wall-clock time in the zone (for datetime-local inputs). */
export function utcIsoToZonedLocal(iso: string, timeZone: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '';
  const p = parts(d, timeZone);
  return `${p.year}-${p.month}-${p.day}T${p.hour}:${p.minute}`;
}
