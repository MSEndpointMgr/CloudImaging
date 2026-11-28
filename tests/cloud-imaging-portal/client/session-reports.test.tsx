import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

const { apiFetch } = vi.hoisted(() => ({ apiFetch: vi.fn() }));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch,
  apiFetchWithRetry: apiFetch,
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
  getApiAccessToken: vi.fn(async () => null),
}));

import ReportLocationStatisticsPage from '../../../src/cloud-imaging-portal/client/src/pages/ReportLocationStatisticsPage.tsx';
import ReportDeviceOutcomesPage from '../../../src/cloud-imaging-portal/client/src/pages/ReportSessionOutcomesPage.tsx';
import ReportFailureDetailPage from '../../../src/cloud-imaging-portal/client/src/pages/ReportFailureDetailPage.tsx';
import ReportAutopilotRegistrationsPage from '../../../src/cloud-imaging-portal/client/src/pages/ReportAutopilotRegistrationsPage.tsx';
import ReportImageInventoryPage from '../../../src/cloud-imaging-portal/client/src/pages/ReportImageInventoryPage.tsx';
import { resolvePreset } from '../../../src/cloud-imaging-portal/client/src/lib/dateRange.ts';
import type { SessionHistoryRecord } from '../../../src/cloud-imaging-portal/client/src/lib/locationStatistics.ts';
import type { AutopilotRequest } from '../../../src/cloud-imaging-portal/client/src/lib/autopilot.ts';

function session(id: string, finalState: string, locationId: string | null, minutes: number, serial = `SN-${id}`): SessionHistoryRecord {
  const names: Record<string, string> = { got: 'G\u00f6teborg', mal: 'Malm\u00f6' };
  // Registered half an hour before imaging started; an expired session never started imaging.
  const imagingStartedAt = finalState === 'SessionExpired' ? null : '2026-11-02T08:30:00.000Z';
  return {
    sessionId: id,
    finalState,
    deviceSerialNumber: serial,
    deviceManufacturer: 'HP',
    deviceModel: 'EliteBook 840 G11',
    locationId,
    locationName: locationId ? names[locationId] : null,
    failedStepName: finalState === 'SessionFailed' ? 'DownloadImage' : null,
    errorDetail: finalState === 'SessionFailed' ? 'The image download timed out.' : null,
    createdAt: '2026-11-02T08:00:00.000Z',
    imagingStartedAt,
    terminalAt: new Date(Date.parse(imagingStartedAt ?? '2026-11-02T08:00:00.000Z') + minutes * 60_000).toISOString(),
  };
}

const sessions = [
  session('s1', 'SessionCompleted', 'got', 20),
  session('s2', 'SessionCompleted', 'mal', 10),
  session('s3', 'SessionFailed', 'got', 10, 'SN-repeat'),
  session('s4', 'SessionFailed', 'got', 10, 'SN-repeat'),
  session('s5', 'SessionExpired', null, 10),
];

const previousSessions = [session('p1', 'SessionCompleted', 'got', 10), session('p2', 'SessionFailed', 'mal', 10)];

/** The current period gets `sessions`, the one before it `previousSessions`. */
function serveSessions(): void {
  const thisPeriod = new Date(`${resolvePreset('last30')!.from}T00:00:00Z`).toISOString();
  apiFetch.mockImplementation(async (url: string) => {
    const from = new URL(url, 'http://portal').searchParams.get('from');
    return new Response(JSON.stringify(from === thisPeriod ? sessions : previousSessions), { status: 200 });
  });
}

function renderPage(page: React.ReactElement): void {
  render(<MemoryRouter>{page}</MemoryRouter>);
}

/** The legend entries of the breakdown card that names `noun`. */
function legend(noun: string): string[] {
  const card = screen.getByText(noun).closest('div.rounded-xl') as HTMLElement;
  return [...card.querySelectorAll('li')].map(li => li.textContent ?? '');
}

/** The card of the key figure labelled `label`; the same words can also be a badge in the table. */
function figure(label: string): HTMLElement {
  const heading = screen.getAllByText(label).find(el => el.tagName === 'P' && el.classList.contains('text-xs'));
  return heading?.parentElement as HTMLElement;
}

function choose(filter: string, ...options: string[]): void {
  const button = screen.getByRole('combobox', { name: filter });
  fireEvent.click(button);
  for (const option of options) fireEvent.click(screen.getByRole('option', { name: option }));
  fireEvent.click(button);
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('Location statistics report', () => {
  it('breaks the devices down by outcome with the success rate against the previous period', async () => {
    serveSessions();
    renderPage(<ReportLocationStatisticsPage />);

    await waitFor(() => expect(legend('devices')).toEqual(['Successful240%', 'Failed240%', 'Expired120%', 'Not authorized0']));
    expect(within(figure('Success rate')).getByText('40%')).toBeTruthy();
    expect(await within(figure('Success rate')).findByText('Previous 30 days: 50%')).toBeTruthy();
    // Imaging time only: the half hour before imaging started and the session that never started are left out.
    expect(within(figure('Average duration')).getByText('12m 30s')).toBeTruthy();
    expect(within(figure('Average duration')).getByText('Longest 20m 0s')).toBeTruthy();
    expect(screen.getAllByText('Successful').length).toBeGreaterThan(1);
  });

  it('takes several locations with a chip each, and outcomes narrow the table only', async () => {
    serveSessions();
    renderPage(<ReportLocationStatisticsPage />);
    await waitFor(() => expect(legend('devices')).toHaveLength(4));

    choose('Locations', 'G\u00f6teborg', 'Malm\u00f6');

    expect(screen.getByRole('combobox', { name: 'Locations' }).textContent).toBe('G\u00f6teborg, Malm\u00f6');
    expect(screen.getByRole('button', { name: /Location: G\u00f6teborg/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /Location: Malm\u00f6/ })).toBeTruthy();
    expect(legend('devices')).toEqual(['Successful250%', 'Failed250%', 'Expired0', 'Not authorized0']);
    expect(await within(figure('Success rate')).findByText('Previous 30 days: 50%')).toBeTruthy();

    choose('Outcomes', 'Failed');

    expect(screen.getByRole('button', { name: /Outcome: Failed/ })).toBeTruthy();
    expect(legend('devices')).toEqual(['Successful250%', 'Failed250%', 'Expired0', 'Not authorized0']);
    expect(screen.getAllByText('SN-repeat')).toHaveLength(2);
    expect(screen.queryByText('SN-s1')).toBeNull();
  });

  it('shows the load error with Try again instead of empty figures', async () => {
    apiFetch.mockImplementation(async () => new Response('{}', { status: 503 }));
    renderPage(<ReportLocationStatisticsPage />);

    expect(await screen.findByText('Could not load the location statistics')).toBeTruthy();
    expect(screen.queryByText('devices')).toBeNull();
    serveSessions();
    fireEvent.click(screen.getByRole('button', { name: /Try again/ }));

    await waitFor(() => expect(legend('devices')).toHaveLength(4));
  });
});

describe('Device outcomes report', () => {
  it('breaks the sessions down, adds the average duration and a Location column, and filters by location', async () => {
    serveSessions();
    renderPage(<ReportDeviceOutcomesPage />);

    await waitFor(() => expect(legend('sessions')).toEqual(['Completed240%', 'Failed240%', 'Expired120%', 'Not authorized0']));
    expect(await within(figure('Success rate')).findByText('Previous 30 days: 50%')).toBeTruthy();
    expect(within(figure('Average duration')).getByText('12m 30s')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Location' })).toBeTruthy();
    expect(screen.getByText('Unassigned')).toBeTruthy();
    const expired = screen.getByText('SN-s5').closest('tr') as HTMLElement;
    const cells = within(expired).getAllByRole('cell').map(cell => cell.textContent);
    expect([cells[4], cells[6]]).toEqual(['-', '-']);

    choose('Locations', 'Malm\u00f6');

    expect(screen.getByText('session')).toBeTruthy();
    expect(legend('session')).toEqual(['Completed1100%', 'Failed0', 'Expired0', 'Not authorized0']);
    expect(screen.queryByText('Unassigned')).toBeNull();
  });
});

describe('Failure detail report', () => {
  it('works out the failure rate and the devices affected from every session of the period', async () => {
    serveSessions();
    renderPage(<ReportFailureDetailPage />);

    await waitFor(() => expect(legend('failures')).toEqual(['Failed267%', 'Expired133%', 'Not authorized0']));
    expect(within(figure('Failure rate')).getByText('60%')).toBeTruthy();
    expect(within(figure('Failure rate')).getByText('3 of 5 sessions')).toBeTruthy();
    expect(within(figure('Devices affected')).getByText('2')).toBeTruthy();
    expect(within(figure('Devices affected')).getByText('1 failed more than once')).toBeTruthy();
    expect(screen.queryByText('SN-s1')).toBeNull();

    choose('Locations', 'G\u00f6teborg');

    expect(legend('failures')).toEqual(['Failed2100%', 'Expired0', 'Not authorized0']);
    expect(within(figure('Failure rate')).getByText('2 of 3 sessions')).toBeTruthy();
    expect(within(figure('Devices affected')).getByText('1')).toBeTruthy();
  });
});

describe('Autopilot registration history report', () => {
  function request(id: string, state: AutopilotRequest['state'], waitMinutes: number | null, locationId = 'got'): AutopilotRequest {
    const submittedAt = '2026-11-02T08:00:00.000Z';
    return {
      requestId: id, referenceCode: `REF-${id}`, serialNumber: `SN-${id}`, manufacturer: 'HP', model: 'EliteBook 840 G11', architecture: 'x64',
      locationId, locationName: locationId === 'got' ? 'G\u00f6teborg' : 'Malm\u00f6', state, submittedAt, updatedAt: submittedAt, expiresAt: submittedAt,
      decidedAt: waitMinutes === null ? null : new Date(Date.parse(submittedAt) + waitMinutes * 60_000).toISOString(), importAttempts: 1,
    };
  }

  it('leaves group tag updates out of the legend for a figure of their own, beside the wait for approval', async () => {
    apiFetch.mockImplementation(async () => new Response(JSON.stringify([
      request('r1', 'Imported', 120),
      request('r2', 'Imported', 60),
      request('r3', 'GroupTagUpdated', 180, 'mal'),
      request('r4', 'Rejected', 30),
      request('r5', 'Expired', null),
    ]), { status: 200 }));
    renderPage(<ReportAutopilotRegistrationsPage />);

    await waitFor(() => expect(legend('handled requests')).toEqual(['Imported240%', 'Already registered0', 'Rejected120%', 'Expired120%']));
    expect(within(figure('Average wait for approval')).getByText('1h 37m')).toBeTruthy();
    expect(within(figure('Average wait for approval')).getByText('Longest 3h 0m')).toBeTruthy();
    expect(within(figure('Group tag updated')).getByText('1')).toBeTruthy();
    expect(within(figure('Group tag updated')).getByText('20% of handled requests')).toBeTruthy();

    choose('Locations', 'Malm\u00f6');

    expect(legend('handled request')).toEqual(['Imported0', 'Already registered0', 'Rejected0', 'Expired0']);
    expect(within(figure('Group tag updated')).getByText('100% of handled requests')).toBeTruthy();
  });
});

describe('Image inventory report', () => {
  it('breaks the images down per catalog with their size, the total size and the oldest image', async () => {
    const gib = 1_073_741_824;
    apiFetch.mockImplementation(async (url: string) => {
      const body = url === '/api/images'
        ? [
          { imageId: 'o1', name: 'Windows 11 Enterprise', version: '24H2', sizeBytes: gib, uploadedAt: '2026-01-01T10:00:00.000Z', isInUse: true },
          { imageId: 'o2', name: 'Windows 11 Enterprise', version: '25H2', sizeBytes: 2 * gib, uploadedAt: '2026-06-01T10:00:00.000Z', isInUse: false },
        ]
        : url === '/api/boot-images'
          ? [{ bootImageId: 'b1', version: '10.0.26100.1', createdAt: '2026-05-01T10:00:00.000Z', sizeBytes: gib / 2, architecture: 'x64', isLatestPublished: true, isActive: true }]
          : [];
      return new Response(JSON.stringify(body), { status: 200 });
    });
    renderPage(<ReportImageInventoryPage />);

    await waitFor(() => expect(legend('images')).toEqual(['OS images23.00 GB', 'Boot images10.50 GB', 'Recovery images0']));
    expect(within(figure('Total size')).getByText('3.50 GB')).toBeTruthy();
    expect(within(figure('Total size')).getByText('Largest image 2.00 GB')).toBeTruthy();
    const days = Math.floor((Date.now() - Date.parse('2026-01-01T10:00:00.000Z')) / 86_400_000);
    expect(within(figure('Oldest image')).getByText(`${days} days`)).toBeTruthy();
    const uploaded = new Date('2026-01-01T10:00:00.000Z').toLocaleDateString(undefined, { dateStyle: 'medium' });
    expect(within(figure('Oldest image')).getByText(`OS image, uploaded ${uploaded}`)).toBeTruthy();
  });
});
