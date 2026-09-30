import { useState } from 'react';
import { Icon } from '../Components/Icon';
import { useShell } from '../Hooks/ShellContext';

const key = (text: string) => 'banner-dismissed:' + text;

/** Full-width strip above the top bar. The server only sends it between its start and end times. */
export function Banner() {
  const { shell } = useShell();
  const banner = shell?.banner;
  const [, force] = useState(0);
  if (!banner) return null;

  let dismissed = false;
  try { dismissed = sessionStorage.getItem(key(banner.text)) === '1'; } catch { /* storage unavailable */ }
  if (dismissed) return null;

  const icon = banner.type === 'Information' ? 'info' : banner.type === 'Warning' ? 'warning' : 'wrench';
  return (
    <div className={`banner banner-${banner.type.toLowerCase()}`} role="status">
      <Icon name={icon} />
      <span className="banner-text">{banner.text}</span>
      {banner.dismissible && (
        <button className="btn btn-ghost btn-sm" aria-label="Dismiss banner"
          onClick={() => { try { sessionStorage.setItem(key(banner.text), '1'); } catch { /* ignore */ } force((n) => n + 1); }}>
          <Icon name="close" size={14} />
        </button>
      )}
    </div>
  );
}
