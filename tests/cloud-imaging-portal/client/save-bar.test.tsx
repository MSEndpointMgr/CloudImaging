import { afterEach, describe, it, expect, vi } from 'vitest';
import { useState } from 'react';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { Link, RouterProvider, createMemoryRouter } from 'react-router-dom';
import { SaveBar } from '../../../src/cloud-imaging-portal/client/src/components/SaveBar.tsx';
import { describeLostChanges, describeUnsavedChanges } from '../../../src/cloud-imaging-portal/client/src/lib/unsavedChanges.ts';

describe('Portal frontend: unsaved changes text', () => {
  it('counts the changes and names the tabs they are on', () => {
    expect(describeUnsavedChanges([{ label: 'Require a group tag', scope: 'Autopilot' }])).toBe('1 unsaved change on Autopilot.');
    expect(describeUnsavedChanges([
      { label: 'Require a group tag', scope: 'Autopilot' },
      { label: 'Partition order', scope: 'Partitioning' },
      { label: 'Clock skew tolerance', scope: 'Security' },
    ])).toBe('3 unsaved changes on Autopilot, Partitioning and Security.');
    expect(describeUnsavedChanges([{ label: 'Targeting' }, { label: 'Scripts' }])).toBe('2 unsaved changes.');
  });

  it('names what would be lost, and shortens a long list', () => {
    expect(describeLostChanges([{ label: 'Require a group tag' }])).toBe('Require a group tag has not been saved.');
    expect(describeLostChanges([{ label: 'Targeting' }, { label: 'Scripts' }])).toBe('Targeting and Scripts have not been saved.');
    expect(describeLostChanges([{ label: 'A' }, { label: 'B' }, { label: 'C' }, { label: 'D' }, { label: 'E' }])).toBe('A, B and 3 more have not been saved.');
  });
});

describe('Portal frontend: leaving with unsaved changes', () => {
  afterEach(() => { cleanup(); });

  function Editor({ onSave, blockedReason = null }: { onSave: () => Promise<boolean>; blockedReason?: string | null }) {
    const [value, setValue] = useState('');
    return (
      <>
        <input aria-label="Setting" value={value} onChange={(e) => setValue(e.target.value)} />
        <Link to="/elsewhere">Elsewhere</Link>
        <SaveBar
          changes={value ? [{ label: 'Setting' }] : []}
          blockedReason={blockedReason}
          onDiscard={() => setValue('')}
          onSave={async () => { const saved = await onSave(); if (saved) setValue(''); return saved; }}
        />
      </>
    );
  }

  function renderEditor(onSave = vi.fn(async () => true), blockedReason: string | null = null) {
    const router = createMemoryRouter([
      { path: '/editor', element: <Editor onSave={onSave} blockedReason={blockedReason} /> },
      { path: '/elsewhere', element: <p>Somewhere else</p> },
    ], { initialEntries: ['/editor'] });
    render(<RouterProvider router={router} />);
    return { router, onSave };
  }

  it('lets a clean page go without asking', async () => {
    renderEditor();
    fireEvent.click(screen.getByText('Elsewhere'));
    expect(await screen.findByText('Somewhere else')).toBeTruthy();
  });

  it('asks before leaving, and Stay keeps the change', async () => {
    const { router } = renderEditor();
    fireEvent.change(screen.getByLabelText('Setting'), { target: { value: 'x' } });

    fireEvent.click(screen.getByText('Elsewhere'));
    expect(await screen.findByText('Leave without saving?')).toBeTruthy();
    expect(screen.getByText(/Setting has not been saved\./)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));
    expect(screen.queryByText('Leave without saving?')).toBeNull();
    expect(router.state.location.pathname).toBe('/editor');
    expect((screen.getByLabelText('Setting') as HTMLInputElement).value).toBe('x');
  });

  it('Discard drops the change and leaves', async () => {
    const { onSave } = renderEditor();
    fireEvent.change(screen.getByLabelText('Setting'), { target: { value: 'x' } });
    fireEvent.click(screen.getByText('Elsewhere'));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard' }));

    expect(await screen.findByText('Somewhere else')).toBeTruthy();
    expect(onSave).not.toHaveBeenCalled();
  });

  it('Save saves and then leaves, and stays when the save fails', async () => {
    const failing = vi.fn(async () => false);
    const { router } = renderEditor(failing);
    fireEvent.change(screen.getByLabelText('Setting'), { target: { value: 'x' } });
    fireEvent.click(screen.getByText('Elsewhere'));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await vi.waitFor(() => expect(failing).toHaveBeenCalled());
    await vi.waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(router.state.location.pathname).toBe('/editor');

    cleanup();
    renderEditor();
    fireEvent.change(screen.getByLabelText('Setting'), { target: { value: 'x' } });
    fireEvent.click(screen.getByText('Elsewhere'));
    const second = await screen.findByRole('dialog');
    fireEvent.click(within(second).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Somewhere else')).toBeTruthy();
  });

  it('holds Save in the dialog and says why when the change cannot be saved', async () => {
    renderEditor(vi.fn(async () => true), 'Choose at least one location or All devices.');
    fireEvent.change(screen.getByLabelText('Setting'), { target: { value: 'x' } });
    fireEvent.click(screen.getByText('Elsewhere'));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('Choose at least one location or All devices.');
    expect((within(dialog).getByRole('button', { name: 'Save' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
