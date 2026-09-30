import { PageGuard } from '../Components/PageGuard';
import { PageHeader } from '../Components/ui';
import { useAuth } from '../Hooks/AuthContext';

export function HomePage() {
  const { me } = useAuth();
  return (
    <PageGuard page="home" requires={[]}>
      <PageHeader title="Home" subtitle={`Welcome, ${me?.displayName}.`} />
    </PageGuard>
  );
}
