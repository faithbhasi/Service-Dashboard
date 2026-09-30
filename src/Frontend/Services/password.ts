// Random password generation in the browser, so a generated password never travels anywhere until the operator confirms the reset.

const LOWER = 'abcdefghijkmnpqrstuvwxyz'; // no l, o (look-alikes)
const UPPER = 'ABCDEFGHJKLMNPQRSTUVWXYZ'; // no I, O
const DIGITS = '23456789';
const SYMBOLS = '!#$%&*+-=?@^_';

function randomInt(max: number): number {
  const limit = Math.floor(0x100000000 / max) * max; // rejection sampling: no modulo bias
  const buf = new Uint32Array(1);
  do { crypto.getRandomValues(buf); } while (buf[0] >= limit);
  return buf[0] % max;
}

export function generatePassword(length = 16): string {
  const len = Math.max(8, Math.min(128, Math.floor(length)));
  const all = LOWER + UPPER + DIGITS + SYMBOLS;
  const chars = [LOWER, UPPER, DIGITS, SYMBOLS].map((set) => set[randomInt(set.length)]);
  while (chars.length < len) chars.push(all[randomInt(all.length)]);
  for (let i = chars.length - 1; i > 0; i--) { // Fisher-Yates
    const j = randomInt(i + 1);
    [chars[i], chars[j]] = [chars[j], chars[i]];
  }
  return chars.join('');
}
