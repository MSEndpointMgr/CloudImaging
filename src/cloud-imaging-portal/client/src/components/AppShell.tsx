import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar.tsx';

/**
 * Top-level layout shell: fixed left sidebar + scrollable main area.
 * Sidebar is present on all authenticated routes (FR-040).
 */
export function AppShell(): React.ReactElement {
  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <main className="flex-1 overflow-y-auto bg-background p-6">
        <Outlet />
      </main>
    </div>
  );
}
