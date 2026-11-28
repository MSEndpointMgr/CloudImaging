import React from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { ReportSummary, type SummaryPart } from '../../../src/cloud-imaging-portal/client/src/components/ReportSummary.tsx';

const parts: SummaryPart[] = [
  { key: 'imported', label: 'Imported', count: 29, colorClassName: 'bg-emerald-600' },
  { key: 'tag', label: 'Group tag updated', count: 2, colorClassName: 'bg-emerald-600', inLegend: false },
  { key: 'rejected', label: 'Rejected', count: 6, colorClassName: 'bg-muted-foreground' },
  { key: 'expired', label: 'Expired', count: 0, colorClassName: 'bg-muted-foreground' },
];

function legend(): string[] {
  return [...document.querySelectorAll('li')].map(li => li.textContent ?? '');
}

function segments(): string[] {
  return [...document.querySelectorAll('div[aria-hidden="true"] > span')].map(span => (span as HTMLElement).style.flexGrow);
}

describe('Portal frontend: report summary', () => {
  afterEach(cleanup);

  it('breaks the total down in a bar and a legend with each part\'s share', () => {
    render(
      <ReportSummary
        loading={false}
        total={37}
        noun={['handled request', 'handled requests']}
        parts={parts}
        emptyText="No requests were handled in this period."
        figures={[
          { label: 'Average wait for approval', value: '2h 14m', detail: 'Longest 26h 5m' },
          { label: 'Group tag updated', value: '2', detail: '5% of handled requests' },
        ]}
      />,
    );

    expect(screen.getByText('37')).toBeTruthy();
    expect(screen.getByText('handled requests')).toBeTruthy();
    // A part a figure shows stays in the bar but leaves the legend; a part with none has no share.
    expect(legend()).toEqual(['Imported2978%', 'Rejected616%', 'Expired0']);
    expect(segments()).toEqual(['29', '2', '6']);
    expect(screen.getByText('2h 14m')).toBeTruthy();
    expect(screen.getByText('Longest 26h 5m').className).toContain('text-balance');
    expect(screen.getByText('5% of handled requests')).toBeTruthy();
  });

  it('puts the breakdown beside two figures from 1280px, and beside four only from 1536px', () => {
    const { container, rerender } = render(
      <ReportSummary loading={false} total={1} noun={['device', 'devices']} parts={parts} emptyText="-" figures={[{ label: 'A', value: '1' }, { label: 'B', value: '2' }]} />,
    );
    expect((container.firstChild as HTMLElement).className).toContain('xl:grid-cols-[minmax(max-content,2.6fr)_repeat(2,minmax(0,1fr))]');
    expect(screen.getByText('device')).toBeTruthy();

    rerender(
      <ReportSummary loading={false} total={2} noun={['device', 'devices']} parts={parts} emptyText="-" figures={['A', 'B', 'C', 'D'].map(label => ({ label, value: '1' }))} />,
    );
    expect((container.firstChild as HTMLElement).className).toContain('2xl:grid-cols-[minmax(max-content,2.6fr)_repeat(4,minmax(0,1fr))]');
  });

  it('says so when there is nothing to break down, and shows a missing figure as None', () => {
    render(
      <ReportSummary
        loading={false}
        total={0}
        noun={['device', 'devices']}
        parts={parts.map(part => ({ ...part, count: 0 }))}
        emptyText="No imaging sessions finished in this period."
        figures={[{ label: 'Success rate', value: null, detail: 'Previous 30 days: 87%' }, { label: 'Average duration', value: null }]}
      />,
    );

    expect(screen.getByText('No imaging sessions finished in this period.')).toBeTruthy();
    expect(legend()).toEqual([]);
    expect(segments()).toEqual([]);
    expect(document.querySelector('div[aria-hidden="true"]')?.className).toContain('bg-muted');
    const none = screen.getAllByText('None');
    expect(none).toHaveLength(2);
    expect(none[0].className).toContain('text-muted-foreground');
  });

  it('shows placeholders while the report loads', () => {
    render(
      <ReportSummary loading total={0} noun={['device', 'devices']} parts={parts} emptyText="Nothing yet" figures={[{ label: 'Success rate', value: '90%', detail: 'Previous 30 days: 87%' }]} />,
    );

    expect(screen.queryByText('Nothing yet')).toBeNull();
    expect(screen.queryByText('90%')).toBeNull();
    expect(screen.queryByText('Previous 30 days: 87%')).toBeNull();
    expect(screen.getByText('Success rate')).toBeTruthy();
  });
});
