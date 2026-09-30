import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { GlobalSearch } from '../Components/GlobalSearch';
import { Icon } from '../Components/Icon';
import { useAuth } from '../Hooks/AuthContext';
import { useShell } from '../Hooks/ShellContext';
import { getCsrfToken } from '../Services/api';
import { readableOn } from '../Themes/contrast';
import { useTheme, type ThemePreference } from '../Themes/ThemeProvider';

const themeOrder: ThemePreference[] = ['light', 'dark', 'system'];

export function TopBar({ onToggleNav }: { onToggleNav: () => void }) {
  const { me, updatePreferences } = useAuth();
  const { shell } = useShell();
  const { branding, mode, preference } = useTheme();
  const [open, setOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!menuRef.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  const productName = shell?.productName ?? branding?.productName ?? '';
  const logoKind = mode === 'dark' && branding?.hasLogoDark ? 'dark' : branding?.hasLogoLight || branding?.hasLogoDark ? (branding?.hasLogoLight ? 'light' : 'dark') : null;
  const nextTheme = themeOrder[(themeOrder.indexOf(preference) + 1) % themeOrder.length];

  return (
    <header className="topbar">
      <div className="topbar-left">
        <button className="btn btn-ghost topbar-menu" onClick={onToggleNav} aria-label="Toggle navigation"><Icon name="menu" /></button>
        {/* The logo and the name take you back to Home. */}
        <Link to="/" className="brand" aria-label={`${productName || 'Home'} - go to Home`} title="Go to Home">
          {logoKind
            ? <img className="logo" alt="" src={`/api/settings/personalization/logo/${logoKind}?v=${branding?.assetVersion ?? ''}`} />
            : <span className="logo-fallback" aria-hidden="true">{productName.slice(0, 1)}</span>}
          <span className="product-name">{productName}</span>
        </Link>
        {shell?.environmentLabel && (
          <span className={`env-badge env-${shell.environmentLabel.toLowerCase()}`}
            style={shell.environmentLabelColor ? { background: shell.environmentLabelColor, color: readableOn(shell.environmentLabelColor) } : undefined}>
            {shell.environmentLabel}
          </span>
        )}
      </div>

      <div className="topbar-center"><GlobalSearch /></div>

      <div className="topbar-right">
        <button className="btn btn-ghost" onClick={() => void updatePreferences({ theme: nextTheme })}
          aria-label={`Theme: ${preference}. Switch to ${nextTheme}`} title={`Theme: ${preference}`}>
          <Icon name={preference === 'system' ? 'system' : preference === 'dark' ? 'moon' : 'sun'} />
        </button>
        <div className="profile" ref={menuRef}>
          <button className="profile-button" onClick={() => setOpen((o) => !o)} aria-haspopup="menu" aria-expanded={open}>
            <span className="avatar" aria-hidden="true">{me?.initials}</span>
            <span className="profile-text">
              <span className="profile-name">{me?.displayName}</span>
              <span className="profile-role">{me?.roleName || 'No role'}</span>
            </span>
          </button>
          {open && me && (
            <div className="profile-menu" role="menu">
              <div className="profile-menu-head">
                <strong>{me.displayName}</strong>
                <span className="muted small">{me.email}</span>
                <span className="muted small">{me.roleName || 'No role assigned'}</span>
              </div>
              <div className="profile-menu-section">
                <span className="small muted">Theme</span>
                <div className="segmented" role="radiogroup" aria-label="Theme">
                  {themeOrder.map((t) => (
                    <button key={t} role="radio" aria-checked={preference === t} className={preference === t ? 'active' : ''}
                      onClick={() => void updatePreferences({ theme: t })}>{t}</button>
                  ))}
                </div>
              </div>
              <div className="profile-menu-section">
                <label className="check">
                  <input type="checkbox" checked={me.preferences.navCollapsed}
                    onChange={(e) => void updatePreferences({ navCollapsed: e.target.checked })} />
                  Collapse navigation
                </label>
              </div>
              {/* A real form post so the browser can follow the redirect to Okta's sign-out page. */}
              <form method="post" action="/api/auth/logout">
                <input type="hidden" name="__RequestVerificationToken" value={getCsrfToken()} />
                <button type="submit" className="btn btn-block" role="menuitem">Sign out</button>
              </form>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}
