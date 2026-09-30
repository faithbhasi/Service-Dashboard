// Thin fetch wrapper: JSON in and out, anti-forgery header on writes, Problem Details errors.
// The session lives in an HttpOnly cookie, so nothing secret is ever kept in the browser.

export class ApiError extends Error {
  status: number;
  code?: string;
  correlationId?: string;
  /** The parsed response body, for endpoints (like AD changes) whose error body carries structured details. */
  data?: unknown;
  constructor(status: number, message: string, code?: string, correlationId?: string, data?: unknown) {
    super(message);
    this.status = status;
    this.code = code;
    this.correlationId = correlationId;
    this.data = data;
  }
}

let csrfToken = '';
let onUnauthorized: (() => void) | null = null;

export const setCsrfToken = (t: string) => { csrfToken = t; };
export const getCsrfToken = () => csrfToken;
export const setUnauthorizedHandler = (fn: (() => void) | null) => { onUnauthorized = fn; };

async function toError(res: Response): Promise<ApiError> {
  let message = `Request failed (${res.status})`;
  let code: string | undefined;
  let correlationId = res.headers.get('X-Correlation-ID') ?? undefined;
  let data: unknown;
  try {
    const p = await res.json();
    data = p;
    message = p.detail || p.title || p.message || message;
    code = p.code;
    correlationId = p.correlationId ?? correlationId;
    if (p.errors && typeof p.errors === 'object') {
      const first = Object.values(p.errors as Record<string, string[]>).flat()[0];
      if (first) message = first;
    }
  } catch { /* not JSON */ }
  return new ApiError(res.status, message, code, correlationId, data);
}

export async function api<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (method !== 'GET') headers['X-XSRF-TOKEN'] = csrfToken;
  const res = await fetch('/api' + path, {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401) onUnauthorized?.();
  if (!res.ok) throw await toError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const get = <T,>(path: string) => api<T>('GET', path);
export const post = <T,>(path: string, body?: unknown) => api<T>('POST', path, body ?? {});
export const put = <T,>(path: string, body?: unknown) => api<T>('PUT', path, body ?? {});
export const del = <T,>(path: string) => api<T>('DELETE', path);

export async function upload<T>(path: string, file: File): Promise<T> {
  const form = new FormData();
  form.append('file', file);
  const res = await fetch('/api' + path, {
    method: 'POST',
    headers: { 'X-XSRF-TOKEN': csrfToken },
    credentials: 'same-origin',
    body: form,
  });
  if (!res.ok) throw await toError(res);
  return (await res.json()) as T;
}

export function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') p.set(k, String(v));
  const s = p.toString();
  return s ? '?' + s : '';
}


/**
 * Downloads a file (CSV export) through fetch so a refusal, such as "too many rows", shows a message
 * instead of navigating away. The session cookie authorises it like any other request.
 */
export async function downloadFile(path: string, fallbackName = 'export.csv'): Promise<void> {
  const res = await fetch('/api' + path, { credentials: 'same-origin' });
  if (res.status === 401) onUnauthorized?.();
  if (!res.ok) throw await toError(res);
  const disposition = res.headers.get('Content-Disposition') ?? '';
  const name = /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? fallbackName;
  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
