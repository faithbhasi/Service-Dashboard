import type { ThemeColors } from '../Services/types';
import { contrastRatio, isHexColor, readableOn } from './contrast';

export interface ContrastWarning { field: keyof ThemeColors; message: string }

/** WCAG AA (4.5:1) checks for every text and background pair the personalization colours produce. */
export function contrastWarnings(c: ThemeColors): ContrastWarning[] {
  const out: ContrastWarning[] = [];
  const check = (field: keyof ThemeColors, fg: string, bg: string, label: string) => {
    if (!isHexColor(fg) || !isHexColor(bg)) return;
    const ratio = contrastRatio(fg, bg);
    if (ratio < 4.5) out.push({ field, message: `${label} has a contrast of ${ratio.toFixed(1)}:1, below the WCAG AA minimum of 4.5:1.` });
  };
  check('navText', c.navText, c.navBackground, 'Navigation text on the navigation background');
  check('navText', c.navText, c.navSelected, 'Navigation text on the selected item');
  check('sectionHeader', c.sectionHeader, c.pageBackground, 'Section headers on the page background');
  check('sectionHeader', c.sectionHeader, c.cardBackground, 'Section headers on cards');
  check('primary', c.primary, c.cardBackground, 'Links in the primary colour on cards');
  if (isHexColor(c.primary)) check('primary', readableOn(c.primary), c.primary, 'Button text on the primary colour');
  check('success', c.success, c.cardBackground, 'Success text on cards');
  check('warning', c.warning, c.cardBackground, 'Warning text on cards');
  check('error', c.error, c.cardBackground, 'Error text on cards');
  return out;
}
