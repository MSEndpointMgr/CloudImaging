import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar.tsx';
import { Header } from './Header.tsx';
import { UpdateAvailableBanner } from './UpdateAvailableBanner.tsx';

/**
 * Top-level layout shell: fixed left sidebar + top header bar + scrollable main area.
 * Sidebar is present on all authenticated routes (FR-040).
 */
export function AppShell(): React.ReactElement {
  return (
    <div className="flex h-screen overflow-hidden bg-background text-foreground">
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
        <Header />
        <main className="flex-1 overflow-y-auto bg-muted/30 p-6">
          <div className="mx-auto w-full max-w-7xl space-y-6">
            <UpdateAvailableBanner />
            <Outlet />
          </div>
        </main>
      </div>
    </div>
  );
}
