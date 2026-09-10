import { useEffect } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { Sidebar } from './Sidebar.tsx';
import { Header } from './Header.tsx';
import { UpdateAvailableBanner } from './UpdateAvailableBanner.tsx';
import { useBranding } from '../context/brandingContext.tsx';
import { resolveSectionTitle } from '../lib/routeTitles.ts';

/**
 * Top-level layout shell: fixed left sidebar + top header bar + scrollable main area.
 * Sidebar is present on all authenticated routes (FR-040).
 */
export function AppShell(): React.ReactElement {
  const { pathname } = useLocation();
  const { branding } = useBranding();
  const appName = branding.applicationName ?? 'Cloud Imaging';

  // Every route shared one static tab title, which made bookmarks, browser history and a row of
  // pinned tabs indistinguishable from each other. The tenant's application name stays in the
  // title so a browser session spanning several portals is still tellable apart.
  useEffect(() => {
    const section = resolveSectionTitle(pathname);
    document.title = section ? `${section} \u00b7 ${appName}` : appName;
  }, [pathname, appName]);

  return (
    <div className="flex h-screen overflow-hidden bg-background text-foreground">
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col overflow-hidden">
        <Header />
        <main className="flex-1 overflow-y-auto bg-muted/30 p-6">
          {/* 1440 rather than Tailwind's 7xl (1280). The tables here carry 6-7 columns of
              machine data; at 1280 the serial and error-detail columns were the ones giving up
              width, on displays that had hundreds of spare pixels sitting in the margins. */}
          <div className="mx-auto w-full max-w-[1440px] space-y-6">
            <UpdateAvailableBanner />
            <Outlet />
          </div>
        </main>
      </div>
    </div>
  );
}
