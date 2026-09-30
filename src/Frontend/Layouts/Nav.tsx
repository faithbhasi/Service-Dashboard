import { NavLink } from 'react-router-dom';
import { Icon, type IconName } from '../Components/Icon';
import { Tag } from '../Components/ui';
import { useAuth } from '../Hooks/AuthContext';
import { useShell } from '../Hooks/ShellContext';
import { Permissions } from '../Services/permissions';

interface Item {
  to: string; label: string; icon: IconName; requires: string[]; tag?: 'Active' | 'Read-Only';
  child?: boolean; moduleId?: string;
}

export function Nav({ collapsed }: { collapsed: boolean }) {
  const { canAny } = useAuth();
  const { moduleEnabled, shell } = useShell();
  const adDisabled = shell ? !moduleEnabled('ad') : false;
  const okta = shell?.modules.find((m) => m.id === 'okta');

  const items: Item[] = [
    { to: '/', label: 'Home', icon: 'home', requires: [] },
    { to: '/ad/users', label: 'Users', icon: 'users', requires: [Permissions.AdUsersRead], child: true, moduleId: 'ad' },
    { to: '/ad/computers', label: 'Computers', icon: 'computer', requires: [Permissions.AdComputersRead], child: true, moduleId: 'ad' },
    { to: '/ad/groups', label: 'Groups', icon: 'group', requires: [Permissions.AdGroupsRead], child: true, moduleId: 'ad' },
  ];
  const adVisible = items.filter((i) => i.moduleId === 'ad' && canAny(...i.requires));

  const showLogs = canAny(Permissions.LogsRead, Permissions.LogsReadOwn);
  const showAdmin = canAny(Permissions.AdminUsersManage, Permissions.AdminRolesManage);
  const showSettings = canAny(Permissions.SettingsRead, Permissions.SettingsManage, Permissions.SettingsPersonalizationManage);

  const link = (i: Item) => (
    <NavLink key={i.to} to={i.to} end={i.to === '/'} className={({ isActive }) => `nav-item ${i.child ? 'child' : ''} ${isActive ? 'active' : ''}`}
      title={collapsed ? i.label : undefined}>
      <Icon name={i.icon} />
      <span className="nav-label">{i.label}</span>
      {i.tag && !collapsed && <Tag>{i.tag}</Tag>}
    </NavLink>
  );

  return (
    <nav className={`nav ${collapsed ? 'collapsed' : ''}`} aria-label="Main">
      {link(items[0])}

      {adVisible.length > 0 && (
        <div className="nav-group">
          <div className={`nav-heading ${adDisabled ? 'disabled' : ''}`}>
            <Icon name="directory" /><span className="nav-label">Active Directory</span>
            {!collapsed && <Tag kind={adDisabled ? 'warning' : 'success'}>{adDisabled ? 'Disabled' : 'Active'}</Tag>}
          </div>
          {adDisabled
            ? adVisible.map((i) => (
                <span key={i.to} className="nav-item child disabled" aria-disabled="true"><Icon name={i.icon} /><span className="nav-label">{i.label}</span></span>
              ))
            : adVisible.map(link)}
        </div>
      )}

      <span className="nav-item disabled" aria-disabled="true" title="Coming Soon">
        <Icon name="cloud" /><span className="nav-label">Okta</span>
        {!collapsed && <Tag>{okta?.status ?? 'Coming Soon'}</Tag>}
      </span>

      {(showLogs || showAdmin || showSettings) && (
        <div className="nav-bottom">
          {showLogs && link({ to: '/logs', label: 'Activity and Logs', icon: 'logs', requires: [] })}
          {showAdmin && link({ to: '/admin', label: 'Users and Groups', icon: 'access', requires: [] })}
          {showSettings && link({ to: '/settings', label: 'Settings', icon: 'settings', requires: [] })}
        </div>
      )}
    </nav>
  );
}
