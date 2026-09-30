import { NavLink, Navigate, useParams } from 'react-router-dom';
import { PageGuard } from '../Components/PageGuard';
import { PageHeader } from '../Components/ui';
import { useAuth } from '../Hooks/AuthContext';
import { Permissions } from '../Services/permissions';
import { ActionPoliciesSection } from './settings/ActionPoliciesSection';
import { AdIntegrationSection } from './settings/AdIntegrationSection';
import { GeneralSection } from './settings/GeneralSection';
import { ModulesSection } from './settings/ModulesSection';
import { PersonalizationSection } from './settings/PersonalizationSection';

const SECTIONS = [
  { id: 'general', label: 'General', requires: [Permissions.SettingsRead, Permissions.SettingsManage], element: <GeneralSection /> },
  { id: 'ad', label: 'AD Integration', requires: [Permissions.SettingsRead, Permissions.SettingsManage], element: <AdIntegrationSection /> },
  { id: 'modules', label: 'Modules', requires: [Permissions.SettingsRead, Permissions.SettingsManage], element: <ModulesSection /> },
  { id: 'policies', label: 'Action Policies', requires: [Permissions.SettingsRead, Permissions.SettingsManage], element: <ActionPoliciesSection /> },
  { id: 'personalization', label: 'Personalization', requires: [Permissions.SettingsRead, Permissions.SettingsPersonalizationManage], element: <PersonalizationSection /> },
];

export function SettingsPage() {
  const { section } = useParams();
  const { canAny } = useAuth();
  const visible = SECTIONS.filter((s) => canAny(...s.requires));
  const current = visible.find((s) => s.id === section);
  if (!section && visible.length > 0) return <Navigate to={`/settings/${visible[0].id}`} replace />;

  return (
    <PageGuard page="settings" requires={[Permissions.SettingsRead, Permissions.SettingsManage, Permissions.SettingsPersonalizationManage]}>
      <PageHeader title="Settings" />
      <div className="split">
        <nav className="side-nav" aria-label="Settings sections">
          {visible.map((s) => <NavLink key={s.id} to={`/settings/${s.id}`} className={({ isActive }) => (isActive ? 'active' : '')}>{s.label}</NavLink>)}
        </nav>
        <div key={current?.id}>{current ? current.element : <p className="muted">Choose a section.</p>}</div>
      </div>
    </PageGuard>
  );
}
