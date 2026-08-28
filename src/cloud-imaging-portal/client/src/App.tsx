import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AppShell } from './components/AppShell.tsx';
import { AuthProvider, useAuth } from './context/authContext.tsx';
import { BrandingProvider } from './context/brandingContext.tsx';
import { ThemeProvider } from './context/themeContext.tsx';
import { ToastProvider } from './context/toastContext.tsx';
import { UserPreferencesProvider } from './context/userPreferencesContext.tsx';
import { ProtectedRoute } from './components/ProtectedRoute.tsx';

// Lazy page stubs. Each section is a placeholder until the feature pages are built
import { Suspense, lazy } from 'react';
const DashboardPage   = lazy(() => import('./pages/DashboardPage.tsx'));
const SessionsPage    = lazy(() => import('./pages/SessionsPage.tsx'));
const OsImagesPage    = lazy(() => import('./pages/OsImagesPage.tsx'));
const BootImagesPage  = lazy(() => import('./pages/BootImagesPage.tsx'));
const RecoveryImagesPage = lazy(() => import('./pages/RecoveryImagesPage.tsx'));
const BrandingPage    = lazy(() => import('./pages/BrandingPage.tsx'));
const DeploymentConfigPage = lazy(() => import('./pages/DeploymentConfigPage.tsx'));
const ReportsPage = lazy(() => import('./pages/ReportsPage.tsx'));
const ReportSessionOutcomesPage = lazy(() => import('./pages/ReportSessionOutcomesPage.tsx'));
const ReportImageInventoryPage = lazy(() => import('./pages/ReportImageInventoryPage.tsx'));
const ReportFailureDetailPage = lazy(() => import('./pages/ReportFailureDetailPage.tsx'));
const LocationsPage = lazy(() => import('./pages/LocationsPage.tsx'));

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, retry: 2 } },
});

/** Route guard for Administrator-only pages; non-admins are redirected to Sessions (FR-040a). */
function RequireAdmin({ children }: { children: React.ReactElement }): React.ReactElement {
  const { isAdministrator } = useAuth();
  return isAdministrator ? children : <Navigate to="/" replace />;
}

export default function App(): React.ReactElement {
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <AuthProvider>
            <BrandingProvider>
              <UserPreferencesProvider>
              <ToastProvider>
                <ProtectedRoute>
                  <Suspense fallback={<div className="p-6 text-muted-foreground">Loading…</div>}>
                    <Routes>
                      <Route element={<AppShell />}>
                        <Route index                   element={<DashboardPage />} />
                        <Route path="sessions"         element={<SessionsPage />} />
                        <Route path="os-images"        element={<OsImagesPage />} />
                        <Route path="boot-images"      element={<BootImagesPage />} />
                        <Route path="recovery-images"  element={<RecoveryImagesPage />} />
                        <Route path="branding"         element={<RequireAdmin><BrandingPage /></RequireAdmin>} />
                        <Route path="configuration"    element={<RequireAdmin><DeploymentConfigPage /></RequireAdmin>} />
                        <Route path="locations"                   element={<RequireAdmin><LocationsPage /></RequireAdmin>} />
                        <Route path="reports"                     element={<RequireAdmin><ReportsPage /></RequireAdmin>} />
                        <Route path="reports/session-outcomes"    element={<RequireAdmin><ReportSessionOutcomesPage /></RequireAdmin>} />
                        <Route path="reports/image-inventory"     element={<RequireAdmin><ReportImageInventoryPage /></RequireAdmin>} />
                        <Route path="reports/failures"            element={<RequireAdmin><ReportFailureDetailPage /></RequireAdmin>} />
                        <Route path="*"                element={<Navigate to="/" replace />} />
                      </Route>
                    </Routes>
                  </Suspense>
                </ProtectedRoute>
              </ToastProvider>
              </UserPreferencesProvider>
            </BrandingProvider>
          </AuthProvider>
        </BrowserRouter>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
