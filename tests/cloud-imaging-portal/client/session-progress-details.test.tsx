import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
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
});