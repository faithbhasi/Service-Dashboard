import { createBrowserRouter, Navigate, Outlet, RouterProvider } from 'react-router-dom';
import { AuthProvider, useAuth } from './Hooks/AuthContext';
import { AppLayout } from './Layouts/AppLayout';
import { HomePage } from './Pages/HomePage';
import { LoginPage } from './Pages/LoginPage';
import { NoAccessPage } from './Pages/NoAccessPage';
import { NotFoundPage } from './Pages/NotFoundPage';
import { Spinner } from './Components/ui';
import { ThemeProvider } from './Themes/ThemeProvider';

/** Everything except the login page needs a session. Users without a role only ever see "No access assigned". */
function RequireSession() {
  const { me, loading } = useAuth();
  if (loading) return <Spinner />;
  if (!me) return <Navigate to="/login" replace />;
  return <AppLayout />;
}

function RequireAccess() {
  const { me } = useAuth();
  if (!me?.hasAccess) return <NoAccessPage />;
  return <Outlet />;
}

function Root() {
  return <AuthProvider><Outlet /></AuthProvider>;
}

export const router = createBrowserRouter([
  {
    element: <Root />,
    children: [
      { path: '/login', element: <LoginPage /> },
      {
        element: <RequireSession />,
        children: [{
          element: <RequireAccess />,
          children: [
            { path: '/', element: <HomePage /> },
            { path: '*', element: <NotFoundPage /> },
          ],
        }],
      },
    ],
  },
]);

export default function App() {
  return <ThemeProvider><RouterProvider router={router} /></ThemeProvider>;
}
