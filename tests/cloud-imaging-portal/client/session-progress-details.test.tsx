import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { SessionProgressDetails } from '../../../src/cloud-imaging-portal/client/src/components/SessionProgressDetails.tsx';

describe('Portal frontend: session progress details', () => {
  afterEach(cleanup);

  it('renders the full pipeline and marks unreported stages pending', () => {
    render(<SessionProgressDetails overallPercent={0} currentStep={null} />);

    expect(screen.getByText('Prepare disk')).toBeTruthy();
    expect(screen.getByText('Download image')).toBeTruthy();
    expect(screen.getByText('Apply Windows image')).toBeTruthy();
    expect(screen.getByText('Configure boot')).toBeTruthy();
    expect(screen.getByText('Configure recovery')).toBeTruthy();
    expect(screen.getAllByText('Pending')).toHaveLength(5);
    expect(screen.getByText('Current stage: Waiting for first progress report')).toBeTruthy();
  });

  it('shows reported status, sub-progress, timestamps, and failure detail', () => {
    render(
      <SessionProgressDetails
        overallPercent={55}
        currentStep="ApplyImage"
        steps={[
          {
            stepName: 'FormatDisk',
            status: 'Completed',
            startedAt: '2026-09-11T08:00:00.000Z',
            completedAt: '2026-09-11T08:01:00.000Z',
          },
          { stepName: 'DownloadImage', status: 'InProgress', stepProgressPercent: 63 },
          { stepName: 'ApplyImage', status: 'Failed', errorDetail: 'DISM returned exit code 5.' },
        ]}
      />,
    );

    expect(screen.getByText('Current stage: Apply Windows image')).toBeTruthy();
    expect(screen.getByText('100%')).toBeTruthy();
    expect(screen.getByText('63%')).toBeTruthy();
    expect(screen.getByText('DISM returned exit code 5.')).toBeTruthy();
    expect(screen.getByText(/Started/)).toBeTruthy();
    expect(screen.getByText(/Finished/)).toBeTruthy();
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('55');
  });
});