import { useAuth } from '../Hooks/AuthContext';
import { useShell } from '../Hooks/ShellContext';

export function NoAccessPage() {
  const { me } = useAuth();
  const { shell } = useShell();
  const contact = shell?.supportContact || me?.supportContact;
  return (
    <div className="center-page">
      <h1>No access assigned</h1>
      <p>Your account ({me?.email}) has signed in, but it has not been given a role in this application yet.</p>
      <p className="muted">{contact ? <>Contact support: <strong>{contact}</strong></> : 'Contact your administrator to request access.'}</p>
    </div>
  );
}
