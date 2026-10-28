import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

const { auth } = vi.hoisted(() => ({
  auth: {
    isAdministrator: false,
    canUseImaging: true,
    canViewSetupProfiles: true,
    canManageLocations: false,
    canViewReports: false,
    canViewAutopilot: false,
    hasDashboardAccess: true,
    canViewDrivers: true,
    canManageDrivers: false,
  },
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/context/authContext.tsx', () => ({
  useAuth: () => auth,
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/context/brandingContext.tsx', () => ({
  useBranding: () => ({ branding: { applicationName: 'Cloud Imaging' }, logoUrl: null }),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/context/toastContext.tsx', () => ({
  useToast: () => ({ notify: vi.fn(), update: vi.fn(), dismiss: vi.fn() }),
}));

// The Models tab loads its data; an empty list keeps the shell tests about the tabs.
vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch: vi.fn(async () => new Response('[]', { status: 200 })),
  apiFetchWithRetry: vi.fn(async () => new Response('[]', { status: 200 })),
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
}));

import { Sidebar } from '../../../src/cloud-imaging-portal/client/src/components/Sidebar.tsx';
import DriversPage from '../../../src/cloud-imaging-portal/client/src/pages/DriversPage.tsx';

afterEach(() => {
  cleanup();
  auth.canViewDrivers = true;
});

function renderSidebar(): void {
  render(
    <MemoryRouter initialEntries={['/']}>
      <Sidebar />
    </MemoryRouter>,
  );
}

describe('Drivers shell', () => {
  it('lists Drivers between Recovery Images and Setup Profiles for a Technician', () => {
    renderSidebar();

    const links = screen.getAllByRole('link').map((link) => link.textContent?.trim());
    const drivers = links.indexOf('Drivers');
    expect(drivers).toBeGreaterThan(-1);
    expect(links[drivers - 1]).toBe('Recovery Images');
    expect(links[drivers + 1]).toBe('Setup Profiles');
  });

  it('hides Drivers from a role without driver access', () => {
    auth.canViewDrivers = false;

    renderSidebar();

    expect(screen.queryByRole('link', { name: 'Drivers' })).toBeNull();
  });

  it.each([
    ['models', 'Models'],
    ['rules', 'Rules'],
    ['sync', 'Sync'],
    ['downloads', 'Downloads'],
    ['storage', 'Storage'],
  ])('renders the five tabs and selects %s from ?tab=', (tab, label) => {
    render(
      <MemoryRouter initialEntries={[`/drivers?tab=${tab}`]}>
        <Routes>
          <Route path="/drivers" element={<DriversPage />} />
        </Routes>
      </MemoryRouter>,
    );

    const tablist = screen.getByRole('tablist');
    const tabs = within(tablist).getAllByRole('tab');
    expect(tabs.map((t) => t.textContent)).toEqual(['Models', 'Rules', 'Sync', 'Downloads', 'Storage']);
    expect(within(tablist).getByRole('tab', { name: label }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tabpanel')).toBeTruthy();
  });

  it('selects Models for an unknown tab', () => {
    render(
      <MemoryRouter initialEntries={['/drivers?tab=nope']}>
        <Routes>
          <Route path="/drivers" element={<DriversPage />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByRole('tab', { name: 'Models' }).getAttribute('aria-selected')).toBe('true');
  });
});
