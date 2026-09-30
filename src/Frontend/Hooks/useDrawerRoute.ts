import { useCallback } from 'react';
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';

/** Drawer state lives in the URL (/ad/users/:id?tab=...), so it survives refresh, can be linked to, and needs no page reload. */
export function useDrawerRoute(basePath: string, defaultTab: string) {
  const { id } = useParams();
  const navigate = useNavigate();
  const { search } = useLocation();
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') ?? defaultTab;

  const close = useCallback(() => {
    const p = new URLSearchParams(search);
    p.delete('tab');
    navigate(basePath + (p.toString() ? '?' + p.toString() : ''));
  }, [navigate, search, basePath]);

  const open = useCallback((targetId: string) => {
    const p = new URLSearchParams(search);
    p.delete('tab');
    navigate(`${basePath}/${targetId}` + (p.toString() ? '?' + p.toString() : ''));
  }, [navigate, search, basePath]);

  const setTab = useCallback((t: string) => {
    setParams((prev) => { const n = new URLSearchParams(prev); n.set('tab', t); return n; }, { replace: true });
  }, [setParams]);

  return { id, tab, setTab, close, open };
}
