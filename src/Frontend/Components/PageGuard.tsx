import { useEffect, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../Hooks/AuthContext';
import { post } from '../Services/api';

/**
 * Wraps a page. Records the visit in the Application Access log and shows "Access denied" when the user lacks
 * every listed permission (for example after typing the URL). The API enforces access regardless of this.
 */
export function PageGuard({ page, requires, children }: { page: string; requires: string[]; children: ReactNode }) {
  const { canAny } = useAuth();
  const allowed = requires.length === 0 || canAny(...requires);

  useEffect(() => {
    post('/auth/access', { page }).catch(() => { /* logging must never break navigation */ });
  }, [page]);

  if (!allowed) return <AccessDenied />;
  return <>{children}</>;
}

export function AccessDenied() {
  return (
    <div className="center-page">
      <h1>Access denied</h1>
      <p className="muted">You do not have permission to open this page. This attempt has been logged.</p>
      <Link className="btn" to="/">Back to Home</Link>
    </div>
  );
}
