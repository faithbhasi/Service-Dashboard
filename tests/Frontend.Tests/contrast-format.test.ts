import { contrastRatio, passesAA, readableOn, isHexColor } from '../../src/Frontend/Themes/contrast';
import { formatDate, formatDateTime } from '../../src/Frontend/Services/format';

describe('contrast helpers', () => {
  it('computes WCAG ratios', () => {
    expect(contrastRatio('#000000', '#ffffff')).toBeCloseTo(21, 0);
    expect(contrastRatio('#777777', '#777777')).toBeCloseTo(1, 5);
  });
  it('flags text that fails AA', () => {
    expect(passesAA('#777777', '#ffffff')).toBe(false); // 4.48:1
    expect(passesAA('#595959', '#ffffff')).toBe(true);
  });
  it('picks a readable text colour', () => {
    expect(readableOn('#ffffff')).toBe('#111827');
    expect(readableOn('#101820')).toBe('#ffffff');
  });
  it('validates hex input', () => {
    expect(isHexColor('#1f5fbf')).toBe(true);
    expect(isHexColor('#fff')).toBe(true);
    expect(isHexColor('blue')).toBe(false);
  });
});

describe('date formatting', () => {
  const iso = '2026-03-05T22:30:15Z';
  it('shows stored UTC in the configured time zone and format', () => {
    expect(formatDateTime(iso, { timeZone: 'UTC', dateFormat: 'yyyy-MM-dd' })).toBe('2026-03-05 22:30:15');
    expect(formatDateTime(iso, { timeZone: 'Australia/Sydney', dateFormat: 'dd/MM/yyyy' })).toBe('06/03/2026 09:30:15');
    expect(formatDate(iso, { timeZone: 'America/New_York', dateFormat: 'MM/dd/yyyy' })).toBe('03/05/2026');
  });
  it('returns an empty string for missing values', () => {
    expect(formatDate(null, { timeZone: 'UTC', dateFormat: 'yyyy-MM-dd' })).toBe('');
  });
});

import { startOfTodayIso, utcIsoToZonedLocal, zonedTimeToUtcIso } from '../../src/Frontend/Services/format';

describe('time zone conversion for log filters', () => {
  it('turns wall-clock time in a zone into the right UTC instant', () => {
    expect(zonedTimeToUtcIso('2026-03-05T09:30', 'Australia/Sydney')).toBe('2026-03-04T22:30:00.000Z');
    expect(zonedTimeToUtcIso('2026-07-01T12:00', 'America/New_York')).toBe('2026-07-01T16:00:00.000Z'); // daylight saving
    expect(zonedTimeToUtcIso('2026-01-01T12:00', 'UTC')).toBe('2026-01-01T12:00:00.000Z');
  });
  it('finds the start of today in the configured zone', () => {
    const now = new Date('2026-03-05T22:30:00Z'); // already 6 March in Sydney
    expect(startOfTodayIso('Australia/Sydney', now)).toBe('2026-03-05T13:00:00.000Z');
    expect(startOfTodayIso('UTC', now)).toBe('2026-03-05T00:00:00.000Z');
  });
  it('round-trips for datetime-local inputs', () => {
    expect(utcIsoToZonedLocal('2026-03-04T22:30:00.000Z', 'Australia/Sydney')).toBe('2026-03-05T09:30');
  });
});
