import { useEffect, useState } from 'react';
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { Note, Spinner } from '../Components/ui';
import { useAuth } from '../Hooks/AuthContext';
import { get, post, setCsrfToken, ApiError } from '../Services/api';
import { useTheme } from '../Themes/ThemeProvider';

interface LoginConfig { devSignIn: boolean; productName: string; csrfToken: string }
interface DevUser { id: string; displayName: string; email: string; isEnabled: boolean; roles: string[] }

export function LoginPage() {
  const { me, loading, refresh } = useAuth();
  const { branding } = useTheme();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [config, setConfig] = useState<LoginConfig>();
  const [devUsers, setDevUsers] = useState<DevUser[]>([]);
  const [error, setError] = useState('');

  useEffect(() => {
    get<LoginConfig>('/auth/config').then(async (c) => {
      setConfig(c);
      setCsrfToken(c.csrfToken);
      if (c.devSignIn) setDevUsers(await get<DevUser[]>('/auth/dev-users'));
    }).catch(() => setError('The server could not be reached.'));
  }, []);

  if (loading) return <Spinner />;
  if (me) return <Navigate to="/" replace />;

  const urlError = params.get('error');
  const message = error
    || (urlError === 'disabled' ? 'Your access to this application has been disabled. Contact your administrator.'
      : urlError === 'failed' ? 'Sign-in could not be completed. Try again, or contact support and quote the time of the attempt.' : '');

  const devLogin = async (u: DevUser) => {
    setError('');
    try {
      await post('/auth/dev-login', { userId: u.id });
      await refresh();
      navigate('/', { replace: true });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Sign-in failed.');
    }
  };

  return (
    <div className="login">
      <div className="card login-card">
        <h1>{branding?.productName ?? config?.productName ?? 'Sign in'}</h1>
        {params.get('signedOut') && <Note kind="success">You have been signed out.</Note>}
        {message && <Note kind="error">{message}</Note>}
        {config && !config.devSignIn && (
          <>
            <p className="muted">You will be taken to Okta to sign in with your username and password, or single sign-on and MFA if your organisation uses them.</p>
            <a className="btn btn-primary btn-block" href="/api/auth/login">Sign in with Okta</a>
          </>
        )}
        {config?.devSignIn && (
          <>
            <Note kind="warning">Development sign-in. Pick a seeded user. This mode cannot be enabled outside Development.</Note>
            <ul className="dev-users">
              {devUsers.map((u) => (
                <li key={u.id}>
                  <button className="btn btn-block dev-user" onClick={() => void devLogin(u)}>
                    <strong>{u.displayName}</strong>
                    <span className="muted small">{u.isEnabled ? (u.roles.join(', ') || 'No role assigned') : 'Disabled in app'}</span>
                  </button>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </div>
  );
}
