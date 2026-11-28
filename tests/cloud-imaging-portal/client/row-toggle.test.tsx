import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { RowToggle, RowToggleHead } from '../../../src/cloud-imaging-portal/client/src/components/ui/row-toggle.tsx';

afterEach(cleanup);

function renderToggle(expanded: boolean, onToggle = vi.fn()): ReturnType<typeof render> {
  return render(
    <table><tbody><tr><td><RowToggle expanded={expanded} label="Show details of System BIOS" controls="bios-details" onToggle={onToggle} /></td></tr></tbody></table>,
  );
}

describe('Portal frontend: row toggle', () => {
  it('is a chevron that names its row, points at the details and reports whether they are open', () => {
    const onToggle = vi.fn();
    renderToggle(false, onToggle);
    const toggle = screen.getByRole('button', { name: 'Show details of System BIOS' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.getAttribute('aria-controls')).toBe('bios-details');
    expect(toggle.querySelector('svg')?.getAttribute('class')).toContain('lucide-chevron-right');

    fireEvent.click(toggle);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it('points down while the details are open', () => {
    renderToggle(true);
    const toggle = screen.getByRole('button');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.querySelector('svg')?.getAttribute('class')).toContain('lucide-chevron-down');
  });

  it('heads a column only as wide as the toggle and the left padding, wider only on request', () => {
    render(<table><thead><tr><RowToggleHead /><RowToggleHead className="w-14" /></tr></thead></table>);
    const [plain, wide] = screen.getAllByRole('columnheader');
    expect(plain.textContent).toBe('Details');
    expect(plain.className).toContain('w-9');
    expect(plain.className).toContain('pr-0');
    expect(wide.className).toContain('w-14');
    expect(wide.className).not.toContain('w-9');
  });
});
