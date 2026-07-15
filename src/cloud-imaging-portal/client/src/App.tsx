import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AppShell } from './components/AppShell.tsx';
import { AuthProvider } from './context/authContext.tsx';
import { ProtectedRoute } from './components/ProtectedRoute.tsx';

// Lazy page stubs — each section is a placeholder until the feature pages are built
import { Suspense, lazy } from 'react';
const SessionsPage    = lazy(() => import('./pages/SessionsPage.tsx'));
const OsImagesPage    = lazy(() => import('./pages/OsImagesPage.tsx'));
const BootImagesPage  = lazy(() => import('./pages/BootImagesPage.tsx'));
const BrandingPage    = lazy(() => import('./pages/BrandingPage.tsx'));
const ConfigurationPage = lazy(() => import('./pages/ConfigurationPage.tsx'));

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, retry: 2 } },
});

export default function App(): React.ReactElement {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <ProtectedRoute>
            <Suspense fallback={<div className="p-6 text-muted-foreground">Loading…</div>}>
              <Routes>
                <Route element={<AppShell />}>
                  <Route index                   element={<SessionsPage />} />
                  <Route path="os-images"        element={<OsImagesPage />} />
                  <Route path="boot-images"      element={<BootImagesPage />} />
                  <Route path="branding"         element={<BrandingPage />} />
                  <Route path="configuration"    element={<ConfigurationPage />} />
                  <Route path="*"                element={<Navigate to="/" replace />} />
                </Route>
              </Routes>
            </Suspense>
          </ProtectedRoute>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
