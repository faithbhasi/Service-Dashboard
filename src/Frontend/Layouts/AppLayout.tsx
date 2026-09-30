import { useEffect, useState } from 'react';
import { Outlet } from 'react-router-dom';
import { useAuth } from '../Hooks/AuthContext';
import { ShellProvider } from '../Hooks/ShellContext';
import { Banner } from './Banner';
import { Nav } from './Nav';
import { TopBar } from './TopBar';

function Frame() {
  const { me, updatePreferences } = useAuth();
  const [mobileOpen, setMobileOpen] = useState(false);
  const collapsed = me?.preferences.navCollapsed ?? false;

  useEffect(() => { document.body.classList.toggle('nav-open', mobileOpen); }, [mobileOpen]);

  return (
    <div className="app">
      <Banner />
      <TopBar onToggleNav={() => {
        if (window.matchMedia('(max-width: 800px)').matches) setMobileOpen((o) => !o);
        else void updatePreferences({ navCollapsed: !collapsed });
      }} />
      <div className="app-body" onClick={() => mobileOpen && setMobileOpen(false)}>
        <Nav collapsed={collapsed} />
        <main className="main"><Outlet /></main>
      </div>
    </div>
  );
}

export function AppLayout() {
  return <ShellProvider><Frame /></ShellProvider>;
}
