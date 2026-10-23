import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';

const api = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry: vi.fn(),
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
}));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => api);

import { SessionProgressDetails } from '../../../src/cloud-imaging-portal/client/src/components/SessionProgressDetails.tsx';

function progressBar(label: string): Element {
  const match = screen.getAllByRole('progressbar').find(bar => bar.getAttribute('aria-label') === label);
  if (!match) throw new Error(`No progressbar labelled "${label}"`);
  return match;
}

describe('Portal frontend: session progress details', () => {
  afterEach(cleanup);

  it('renders the full pipeline and shows no duration for unreported stages', () => {
    render(<SessionProgressDetails overallPercent={0} currentStep={null} />);

    expect(screen.getByText('Prepare disk')).toBeTruthy();
    expect(screen.getByText('Download image')).toBeTruthy();
    // The stepper abbreviates this one, because five labels share a single row.
    expect(screen.getByText('Apply image')).toBeTruthy();
    expect(screen.getByText('Configure boot')).toBeTruthy();
    expect(screen.getByText('Configure recovery')).toBeTruthy();

    expect(screen.getAllByText('\u2014')).toHaveLength(5);
    expect(screen.getByText('Waiting for the first progress report')).toBeTruthy();
  });

  it('shows elapsed time per step and the running step in the focus card', () => {
    render(
      <SessionProgressDetails
        overallPercent={24}
        currentStep="DownloadImage"
        steps={[
          {
            stepName: 'FormatDisk',
            status: 'Completed',
            startedAt: '2026-09-11T08:00:00.000Z',
            completedAt: '2026-09-11T08:01:04.000Z',
          },
          {
            stepName: 'DownloadImage',
            status: 'InProgress',
            stepProgressPercent: 34,
            startedAt: new Date(Date.now() - 252_000).toISOString(),
          },
        ]}
      />,
    );

    expect(screen.getByText('1m 04s')).toBeTruthy();
    expect(screen.getByText('4m 12s')).toBeTruthy();
    expect(screen.getByText(/Step 2 of 5/)).toBeTruthy();

    expect(progressBar('Overall deployment progress').getAttribute('aria-valuenow')).toBe('24');
    expect(progressBar('Download image progress').getAttribute('aria-valuenow')).toBe('34');
  });

  it('never renders a raw status enum', () => {
    render(
      <SessionProgressDetails
        overallPercent={24}
        currentStep="DownloadImage"
        steps={[{ stepName: 'DownloadImage', status: 'InProgress', stepProgressPercent: 34 }]}
      />,
    );

    expect(screen.queryByText('InProgress')).toBeNull();
  });

  it('hands the focus card to the failed step and drops its progress bar', () => {
    render(
      <SessionProgressDetails
        overallPercent={52}
        currentStep="ApplyImage"
        steps={[
          {
            stepName: 'FormatDisk',
            status: 'Completed',
            startedAt: '2026-09-11T08:00:00.000Z',
            completedAt: '2026-09-11T08:01:04.000Z',
          },
          {
            stepName: 'ApplyImage',
            status: 'Failed',
            stepProgressPercent: 40,
            startedAt: '2026-09-11T08:01:04.000Z',
            completedAt: '2026-09-11T08:12:42.000Z',
            errorDetail: 'DISM returned exit code 5.',
          },
        ]}
      />,
    );

    expect(screen.getByText('Apply Windows image failed')).toBeTruthy();
    expect(screen.getByText('DISM returned exit code 5.')).toBeTruthy();
    expect(screen.getByText(/Failed on Apply Windows image after 11m 38s/)).toBeTruthy();

    // A half-filled bar on a step that already failed implies it is still going.
    expect(screen.queryAllByRole('progressbar')).toHaveLength(1);
  });

  it('drops the focus card once every step is complete', () => {
    const steps = ['FormatDisk', 'DownloadImage', 'ApplyImage', 'ConfigureBoot', 'ApplyRecoveryImage'].map(
      stepName => ({
        stepName,
        status: 'Completed',
        startedAt: '2026-09-11T08:00:00.000Z',
        completedAt: '2026-09-11T08:00:52.000Z',
      }),
    );

    render(<SessionProgressDetails overallPercent={100} currentStep={null} steps={steps} />);

    expect(screen.getByText('All steps completed')).toBeTruthy();
    expect(screen.getAllByText('52s')).toHaveLength(5);
    expect(screen.queryAllByRole('progressbar')).toHaveLength(1);
  });

  describe('setup profiles', () => {
    const pin = { profileId: 'p1', name: 'Stockholm and Gothenburg', version: 8 };
    const done = (stepName: string, extra: Record<string, unknown> = {}) => ({
      stepName, status: 'Completed', startedAt: '2026-09-11T08:00:00.000Z', completedAt: '2026-09-11T08:00:52.000Z', ...extra,
    });

    it('keeps five columns without a setup profile', () => {
      const { container } = render(<SessionProgressDetails overallPercent={0} currentStep={null} />);

      expect(container.querySelectorAll('ol > li')).toHaveLength(5);
      expect(container.querySelector('ol')?.className).toContain('grid-cols-5');
      expect(screen.queryByText('Prepare setup')).toBeNull();
    });

    it('shows Prepare setup between Apply image and Configure boot when the session expects it', () => {
      const { container } = render(<SessionProgressDetails overallPercent={0} currentStep={null} setupProfile={pin} />);

      const labels = [...container.querySelectorAll('ol > li > p:first-of-type')].map((p) => p.textContent);
      expect(labels).toEqual(['Prepare disk', 'Download image', 'Apply image', 'Prepare setup', 'Configure boot', 'Configure recovery']);
      expect(container.querySelector('ol')?.className).toContain('grid-cols-6');
      expect(screen.getByText('Stockholm and Gothenburg, version 8')).toBeTruthy();
    });

    it('shows Prepare setup when reported, even without a pin', () => {
      render(<SessionProgressDetails overallPercent={60} currentStep="PrepareSetup" steps={[{ stepName: 'PrepareSetup', status: 'InProgress', stepProgressPercent: 40 }]} />);

      expect(screen.getByText('No setup profile')).toBeTruthy();
    });

    it('marks a step completed with warnings and summarizes the item counts', () => {
      const steps = ['FormatDisk', 'DownloadImage', 'ApplyImage', 'ConfigureBoot', 'ApplyRecoveryImage'].map((s) => done(s));
      steps.push(done('PrepareSetup', {
        warningCount: 1,
        itemCounts: [
          { kind: 'RegistryValue', outcome: 'Succeeded', count: 3 },
          { kind: 'WinPEScript', outcome: 'Failed', count: 1 },
          { kind: 'WinPEScript', outcome: 'Succeeded', count: 1 },
        ],
      }));

      render(<SessionProgressDetails overallPercent={100} currentStep={null} steps={steps} setupProfile={pin} sessionId="s1" terminal />);

      expect(screen.getAllByText('Completed with warnings').length).toBeGreaterThanOrEqual(1);
      expect(screen.getByText('3 registry values written. 1 of 2 WinPE scripts failed.')).toBeTruthy();
    });

    it('shows Not run for a finished session whose boot image never reported the step', () => {
      const steps = ['FormatDisk', 'DownloadImage', 'ApplyImage', 'ConfigureBoot', 'ApplyRecoveryImage'].map((s) => done(s));

      render(<SessionProgressDetails overallPercent={100} currentStep={null} steps={steps} setupProfile={pin} terminal />);

      const notRun = screen.getByText('Not run');
      fireEvent.focus(notRun);
      expect(screen.getByRole('tooltip').textContent).toBe("This device's boot image is older than Setup Profiles, so the profile was not applied. Update the boot image.");
    });

    it('lists the items with failures first and notes truncation', async () => {
      api.apiFetchWithRetry.mockResolvedValue(new Response(JSON.stringify([
        { index: 1, kind: 'WinPEScript', name: 'Enable Wake on LAN', outcome: 'Failed', reason: 'Exited with code 1.', exitCode: 1, durationMs: 8123 },
        { index: 0, kind: 'RegistryValue', name: 'Time zone', outcome: 'Succeeded' },
      ]), { status: 200 }));
      const steps = [done('PrepareSetup', { warningCount: 1, itemsTruncated: true, itemCounts: [{ kind: 'WinPEScript', outcome: 'Failed', count: 1 }] })];

      render(<SessionProgressDetails overallPercent={80} currentStep={null} steps={steps} setupProfile={pin} sessionId="s1" />);
      fireEvent.click(screen.getByRole('button', { name: 'Show items' }));

      const first = await screen.findByText('Enable Wake on LAN');
      expect(first.closest('li')?.textContent).toContain('Exit code 1');
      expect(api.apiFetchWithRetry).toHaveBeenCalledWith('/api/sessions/s1/steps/PrepareSetup/items', { credentials: 'include' });
      expect(screen.getByText('Showing the first 500 items.')).toBeTruthy();
      fireEvent.focus(screen.getByText('Failed'));
      expect(screen.getByRole('tooltip').textContent).toBe('Exited with code 1.');
    });
  });
});