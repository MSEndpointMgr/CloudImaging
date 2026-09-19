/**
 * Route → section title. Shared by the app bar and the browser tab title so the two can never
 * disagree, and so adding a route to one without the other stops being possible - `/reports`
 * had already drifted, leaving its header falling through to the application name.
 */
const SECTION_TITLES: Record<string, string> = {
  '/': 'Dashboard',
  '/sessions': 'Devices',
  '/os-images': 'OS Images',
  '/boot-images': 'Boot Images',
  '/recovery-images': 'Recovery Images',
  '/reports': 'Reports',
  '/reports/device-outcomes': 'Device Outcomes',
  '/reports/location-statistics': 'Location Statistics',
  '/locations': 'Locations',
  '/branding': 'Branding',
  '/configuration': 'Configuration',
};

/**
 * Longest-prefix match, so nested routes (`/reports/session-outcomes`) inherit their section's
 * title. Matching is on segment boundaries — a bare `startsWith` would also claim `/reports-archive`
 * for the Reports section. `/` is excluded because every path starts with it.
 */
export function resolveSectionTitle(pathname: string): string | null {
  if (SECTION_TITLES[pathname]) return SECTION_TITLES[pathname];

  const match = Object.entries(SECTION_TITLES)
    .filter(([key]) => key !== '/' && pathname.startsWith(`${key}/`))
    .sort(([a], [b]) => b.length - a.length)[0];

  return match?.[1] ?? null;
}
