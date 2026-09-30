// WCAG contrast helpers used for the Personalization contrast warnings and for choosing readable text colours.

export function hexToRgb(hex: string): [number, number, number] | null {
  const m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  let h = m[1];
  if (h.length === 3) h = h.split('').map((c) => c + c).join('');
  const n = parseInt(h, 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

export const isHexColor = (s: string) => hexToRgb(s) !== null;

function luminance([r, g, b]: [number, number, number]): number {
  const f = (v: number) => {
    const c = v / 255;
    return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}

export function contrastRatio(a: string, b: string): number {
  const ra = hexToRgb(a);
  const rb = hexToRgb(b);
  if (!ra || !rb) return 1;
  const la = luminance(ra);
  const lb = luminance(rb);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

/** WCAG AA: 4.5:1 for normal text. */
export const passesAA = (fg: string, bg: string, min = 4.5) => contrastRatio(fg, bg) >= min;

/** Black or white, whichever reads better on the given background. */
export function readableOn(bg: string): string {
  return contrastRatio('#000000', bg) >= contrastRatio('#ffffff', bg) ? '#111827' : '#ffffff';
}

export function mix(a: string, b: string, t: number): string {
  const ra = hexToRgb(a); const rb = hexToRgb(b);
  if (!ra || !rb) return a;
  const c = ra.map((v, i) => Math.round(v + (rb[i] - v) * t));
  return '#' + c.map((v) => v.toString(16).padStart(2, '0')).join('');
}
