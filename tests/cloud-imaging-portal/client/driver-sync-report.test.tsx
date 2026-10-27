import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

const { apiFetch } = vi.hoisted(() => ({ apiFetch: vi.fn() }));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch,
  apiFetchWithRetry: apiFetch,
  extractErrorDetail: vi.fn(async (res: Response, fallback: string) => {
    try {
      const body = await res.json() as { detail?: string };
      return body.detail ?? fallback;
    } catch {
      return fallback;
    }
  }),
  getApiAccessToken: vi.fn(async () => null),
}));

import ReportDriverSyncPage from '../../../src/cloud-imaging-portal/client/src/pages/ReportDriverSyncPage.tsx';
import { changesPerWeek, filterSyncReport, groupItems, recurringFailures, syncReportTotals } from '../../../src/cloud-imaging-portal/client/src/lib/driverSyncReport.ts';
import type { DriverSyncReportRun, DriverSyncRunItem } from '../../../src/cloud-imaging-portal/client/src/lib/drivers.ts';
import { resolveSectionTitle } from '../../../src/cloud-imaging-portal/client/src/lib/routeTitles.ts';

const hpModel = '8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10';
const dellModel = '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed';

function item(overrides: Partial<DriverSyncRunItem>): DriverSyncRunItem {
  return { modelId: hpModel, modelName: 'HP EliteBook 840 G11', release: 'Windows11_24H2', lineageKey: 'sp100', componentName: 'Intel Wireless LAN Driver', oldVersion: '23.80.0.6', newVersion: '23.90.0.4', outcome: 'Updated', reason: null, at: '2026-10-24T02:12:00Z', vendor: 'Hp', category: 'Driver - Network', ...overrides };
}

const report: DriverSyncReportRun[] = [
  {
    run: {
      runId: 'r1', trigger: 'Scheduled', modelId: null, modelName: null, startedAt: '2026-10-24T02:00:00Z', finishedAt: '2026-10-24T02:41:00Z', result: 'PartlyFailed', startedBy: null,
      counts: { Updated: 2, New: 1, Failed: 2 },
      vendors: [{ vendor: 'Hp', processed: true, failureReason: null, newModels: 2 }, { vendor: 'Dell', processed: false, failureReason: 'The catalog could not be downloaded.', newModels: 0 }],
      modelsTotal: 12, modelsChecked: 12, downloadedComponents: 22, downloadedBytes: 2_100_000_000, newSets: 4,
    },
    items: [
      item({}),
      item({ lineageKey: 'sp101', componentName: 'Synaptics TouchPad Driver', oldVersion: null, newVersion: '19.5.35.7', outcome: 'New', category: 'Driver - Input' }),
      item({ modelId: dellModel, modelName: 'Dell Latitude 5450', lineageKey: 'd1', componentName: 'Intel Chipset Driver', oldVersion: '10.1.19444.8378', newVersion: '10.1.20266.8668', vendor: 'Dell', category: 'Chipset' }),
      item({ modelId: null, modelName: null, release: null, lineageKey: null, componentName: 'Realtek Audio Driver', oldVersion: null, newVersion: '6.0.9690.1', outcome: 'Failed', reason: 'The file did not match the vendor\'s hash.', vendor: 'Dell', category: null, at: '2026-10-24T02:31:00Z' }),
    ],
  },
  {
    run: {
      runId: 'r2', trigger: 'ModelCheck', modelId: hpModel, modelName: 'HP EliteBook 860 G11', startedAt: '2026-10-23T15:12:00Z', finishedAt: '2026-10-23T15:16:00Z', result: 'Completed', startedBy: 'megan.bowen@contoso.com',
      counts: { Updated: 1 }, vendors: [{ vendor: 'Hp', processed: true, failureReason: null, newModels: 0 }],
      modelsTotal: 1, modelsChecked: 1, downloadedComponents: 1, downloadedBytes: 100_000_000, newSets: 1,
    },
    items: [item({ modelName: 'HP EliteBook 860 G11', at: '2026-10-23T15:14:00Z' })],
  },
  {
    run: {
      runId: 'r3', trigger: 'Scheduled', modelId: null, modelName: null, startedAt: '2026-10-22T02:00:00Z', finishedAt: '2026-10-22T04:00:00Z', result: 'Failed', startedBy: null,
      counts: { Failed: 1 }, vendors: [], plannedVendors: ['Hp', 'Dell'],
    },
    items: [item({ modelId: null, modelName: null, release: null, lineageKey: null, componentName: null, oldVersion: null, newVersion: null, outcome: 'Failed', reason: 'The sync did not finish within 2 hours.', vendor: null, category: null, at: '2026-10-22T04:00:00Z' })],
  },
  {
    run: {
      runId: 'r4', trigger: 'Scheduled', modelId: null, modelName: null, startedAt: '2026-10-21T02:00:00Z', finishedAt: '2026-10-21T02:10:00Z', result: 'Completed', startedBy: null,
      counts: { Failed: 1 }, vendors: [{ vendor: 'Dell', processed: true, failureReason: null, newModels: 0 }],
    },
    items: [item({ modelId: null, modelName: null, release: null, lineageKey: null, componentName: 'Realtek Audio Driver', oldVersion: null, newVersion: '6.0.9690.1', outcome: 'Failed', reason: 'Failed 3 times. Imaging Core will try again at the next sync.', vendor: 'Dell', category: null, at: '2026-10-21T02:05:00Z' })],
  },
];

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('Driver Synchronization report: figures', () => {
  it('adds up runs, results, durations, changes, downloads and failures', () => {
    const totals = syncReportTotals(report);

    expect(totals).toMatchObject({ runs: 4, scheduled: 3, byHand: 1, finished: 4, completed: 2, partlyFailed: 1, failed: 1, updated: 3, fresh: 1, withdrawn: 0, changed: 4, downloadedComponents: 23, downloadedBytes: 2_200_000_000, componentFailures: 2, failedComponents: 1, catalogFailures: 3, otherFailures: 1 });
    expect(totals.longestMs).toBe(120 * 60_000);
    expect(totals.averageMs).toBe(((41 + 4 + 120 + 10) / 4) * 60_000);
  });

  it('groups changes by Monday-based week, empty weeks included', () => {
    const weeks = changesPerWeek(report, '2026-10-12', '2026-10-26');

    expect(weeks.map(w => w.week)).toEqual(['2026-10-12', '2026-10-19', '2026-10-26']);
    expect(weeks[1]).toEqual({ week: '2026-10-19', updated: 3, fresh: 1, withdrawn: 0, failed: 3 });
    expect(weeks[0].updated + weeks[2].updated).toBe(0);
  });

  it('lists recurring component failures by component and model, most first, with the latest reason', () => {
    expect(recurringFailures(report)).toEqual([
      { componentName: 'Realtek Audio Driver', modelName: null, failures: 2, lastReason: 'The file did not match the vendor\'s hash.', lastAt: '2026-10-24T02:31:00Z' },
    ]);
  });

  it('narrows by vendor, trigger and search, keeping only matching runs and items', () => {
    expect(filterSyncReport(report, { search: '', vendor: 'Dell', trigger: '' }).map(r => [r.run.runId, r.items.length, r.run.vendors.length])).toEqual([['r1', 2, 1], ['r3', 1, 0], ['r4', 1, 1]]);
    expect(filterSyncReport(report, { search: '', vendor: 'Dell', trigger: '' })[1].run.plannedVendors).toEqual(['Dell']);
    expect(filterSyncReport(report, { search: '', vendor: '', trigger: 'Check' }).map(r => r.run.runId)).toEqual(['r2']);
    expect(filterSyncReport(report, { search: 'touchpad', vendor: '', trigger: '' }).map(r => [r.run.runId, r.items.length])).toEqual([['r1', 1]]);
  });

  it('groups a run\'s items by model and release, with shared downloads apart and run failures left out', () => {
    expect(groupItems(report[0].items).map(g => [g.modelName, g.items.length])).toEqual([['HP EliteBook 840 G11', 2], ['Dell Latitude 5450', 1], [null, 1]]);
    expect(groupItems(report[2].items)).toEqual([]);
  });

  it('names the report in the app bar', () => {
    expect(resolveSectionTitle('/reports/driver-synchronization')).toBe('Driver Synchronization');
  });
});

describe('Driver Synchronization report: page', () => {
  function serve(body: unknown, status = 200): void {
    apiFetch.mockImplementation(async () => new Response(JSON.stringify(body), { status }));
  }

  function renderPage(): void {
    render(<MemoryRouter><ReportDriverSyncPage /></MemoryRouter>);
  }

  it('asks for the last 30 days and shows the tiles and one row per synchronization', async () => {
    serve(report);
    renderPage();

    expect(await screen.findByText('3 scheduled, 1 started by hand')).toBeTruthy();
    const url = String(apiFetch.mock.calls[0][0]);
    expect(url).toMatch(/^\/api\/drivers\/reports\/sync\?from=\d{4}-\d{2}-\d{2}T00%3A00%3A00\.000Z&to=\d{4}-\d{2}-\d{2}T23%3A59%3A59\.999Z$/);
    expect(screen.getByText('50%')).toBeTruthy();
    expect(screen.getByText('2.20 GB')).toBeTruthy();
    expect(screen.getByText('2 updated, 1 new, 1 failed')).toBeTruthy();
    expect(screen.getByText('HP EliteBook 860 G11: 1 updated')).toBeTruthy();
    expect(screen.getByText('The sync did not finish within 2 hours.')).toBeTruthy();
    expect(screen.getByText('Every model that uses it')).toBeTruthy();
  });

  it('opens a synchronization to its catalogs, downloads and changes grouped by model', async () => {
    serve(report);
    renderPage();

    fireEvent.click((await screen.findAllByRole('button', { name: /^Show the details of the synchronization started/ }))[0]);

    expect(screen.getByText('HP catalog')).toBeTruthy();
    expect(screen.getByText('Read, 2 new models')).toBeTruthy();
    expect(screen.getByLabelText('Failed: The catalog could not be downloaded.')).toBeTruthy();
    expect(screen.getByText('22 components, 2.10 GB')).toBeTruthy();
    expect(screen.getByText('12 of 12')).toBeTruthy();
    const hpGroup = screen.getByText('HP EliteBook 840 G11').closest('div.rounded-lg') as HTMLElement;
    expect(within(hpGroup).getByText('Windows 11, version 24H2')).toBeTruthy();
    expect(within(hpGroup).getByText('1 updated, 1 new')).toBeTruthy();
    expect(within(hpGroup).getByText('Driver - Input')).toBeTruthy();
    const downloads = screen.getByText('Downloads').closest('div.rounded-lg') as HTMLElement;
    expect(within(downloads).getByText('Dell components used by several models')).toBeTruthy();
    expect(within(downloads).getByLabelText('Failed: The file did not match the vendor\'s hash.')).toBeTruthy();
  });

  it('shows the catalogs a synchronization planned but never read', async () => {
    serve(report);
    renderPage();

    fireEvent.click((await screen.findAllByRole('button', { name: /^Show the details of the synchronization started/ }))[2]);

    expect(screen.getByText('HP catalog')).toBeTruthy();
    expect(screen.getByText('Dell catalog')).toBeTruthy();
    expect(screen.getAllByText('Not read')).toHaveLength(2);
  });

  it('names a failed catalog when a synchronization changed nothing, and exports nothing without changes', async () => {
    serve([{ run: { ...report[0].run, runId: 'r9', vendors: [{ vendor: 'Dell', processed: false, failureReason: 'The catalog could not be downloaded.', newModels: 0 }] }, items: [] }]);
    renderPage();

    expect(await screen.findByText('Dell catalog failed')).toBeTruthy();
    expect((screen.getByRole('button', { name: /Export CSV/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('the vendor filter shows a chip and narrows the rows', async () => {
    serve(report);
    renderPage();

    fireEvent.click(await screen.findByRole('combobox', { name: 'Vendor' }));
    fireEvent.click(screen.getByRole('option', { name: 'Dell' }));

    expect(await screen.findByRole('button', { name: /Vendor: Dell/ })).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('HP EliteBook 860 G11: 1 updated')).toBeNull());
  });

  it('shows the error with Try again, and an empty period says so', async () => {
    serve({ detail: 'Could not reach Imaging Core.' }, 503);
    renderPage();

    expect(await screen.findByText('Could not reach Imaging Core.')).toBeTruthy();
    serve([]);
    fireEvent.click(screen.getByRole('button', { name: /Try again/ }));

    expect(await screen.findByText('No synchronizations in this period')).toBeTruthy();
    expect(screen.getByText('No failures in this period')).toBeTruthy();
  });
});
