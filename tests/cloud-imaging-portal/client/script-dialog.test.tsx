import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { ScriptDialog } from '../../../src/cloud-imaging-portal/client/src/components/setup-profiles/ScriptDialog.tsx';
import { SetupProfileApiError, type SetupScript } from '../../../src/cloud-imaging-portal/client/src/lib/setupProfiles.ts';

/**
 * The script dialog: Locations limited to the profile's targeting, Done uploads the text with
 * progress, too-large and failed uploads, loading an existing script, and the Script environment
 * dialog behind the info icon.
 */
const locations = [
  { locationId: 'osl', name: 'Oslo Office' },
  { locationId: 'ber', name: 'Bergen Office' },
];
const existing: SetupScript = {
  id: 's1', name: 'Nordic certificates', runs: 'WinPE', order: 3, timeoutMinutes: 5, sizeBytes: 13_002_342,
  locations: { all: false, locationIds: ['osl', 'gone'] }, contentSha256: 'a'.repeat(64),
  lastModifiedAt: '2026-10-22T10:00:00Z', lastModifiedBy: 'ops@contoso.com',
};
const done = () => screen.getByRole('button', { name: 'Done' }) as HTMLButtonElement;
const editor = () => screen.getByLabelText('PowerShell', { selector: 'textarea' });
const never = () => new Promise<string>(() => undefined);

describe('Portal frontend: script dialog', () => {
  afterEach(cleanup);

  it('offers all locations or selected ones, picking only from the profile\'s targeting', () => {
    render(<ScriptDialog others={[]} locations={locations} loadText={never} onClose={() => undefined} onApply={() => undefined} />);

    fireEvent.click(screen.getByLabelText('Locations'));
    fireEvent.click(screen.getByRole('option', { name: 'Selected locations' }));

    const picker = within(screen.getByRole('list', { name: 'Locations' }));
    expect(picker.getAllByRole('checkbox')).toHaveLength(2);
    expect(screen.getByText('0 of 2 selected')).toBeTruthy();
    expect(screen.getByText('Only locations this profile targets')).toBeTruthy();
  });

  it('uploads the text on Done with progress, then applies the uploaded reference', async () => {
    let finish: (value: { sha256: string; sizeBytes: number }) => void = () => undefined;
    const upload = vi.fn((_kind: string, _text: string, onProgress?: (loaded: number, total: number) => void) => {
      onProgress?.(10_276_045, 19_084_082);
      return new Promise<{ sha256: string; sizeBytes: number }>((resolve) => { finish = resolve; });
    });
    const onApply = vi.fn();
    render(<ScriptDialog others={[]} locations={locations} loadText={never} upload={upload} onClose={() => undefined} onApply={onApply} />);

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Wake on LAN' } });
    fireEvent.change(editor(), { target: { value: 'exit 0' } });
    fireEvent.click(done());

    expect(await screen.findByText('Uploading 9.8 of 18.2 MB')).toBeTruthy();
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('54');
    expect(done().disabled).toBe(true);
    expect(upload.mock.calls[0][0]).toBe('script');
    expect(upload.mock.calls[0][1]).toBe('exit 0');

    finish({ sha256: 'b'.repeat(64), sizeBytes: 6 });
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    expect(onApply.mock.calls[0][0]).toMatchObject({ name: 'Wake on LAN', contentSha256: 'b'.repeat(64), sizeBytes: 6, locations: { all: true, locationIds: [] } });
    expect(onApply.mock.calls[0][1]).toBe('exit 0');
  });

  it('keeps Done disabled with the size message for a script over 24 MB', () => {
    render(<ScriptDialog others={[]} locations={locations} loadText={never} onClose={() => undefined} onApply={() => undefined} />);

    fireEvent.change(editor(), { target: { value: 'x'.repeat(25_165_825) } });

    expect(screen.getByText('This script is 24.1 MB. A script can be at most 24 MB.')).toBeTruthy();
    expect(done().disabled).toBe(true);
  });

  it('says the script was not uploaded and offers Retry', async () => {
    const upload = vi.fn()
      .mockRejectedValueOnce(new SetupProfileApiError('The script was not uploaded.', 0))
      .mockResolvedValue({ sha256: 'c'.repeat(64), sizeBytes: 6 });
    const onApply = vi.fn();
    render(<ScriptDialog others={[]} locations={locations} loadText={never} upload={upload} onClose={() => undefined} onApply={onApply} />);
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Wake on LAN' } });
    fireEvent.change(editor(), { target: { value: 'exit 0' } });

    fireEvent.click(done());

    expect(await screen.findByText('The script was not uploaded.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Done' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
  });

  it('shows the loading line until an existing script\'s text arrives, and applies it unchanged without uploading', async () => {
    let arrive: (text: string) => void = () => undefined;
    const upload = vi.fn();
    const onApply = vi.fn();
    render(<ScriptDialog script={existing} others={[]} locations={locations} loadText={() => new Promise((resolve) => { arrive = resolve; })} upload={upload} onClose={() => undefined} onApply={onApply} />);

    expect(screen.getByText('Loading the script (12.4 MB)')).toBeTruthy();
    expect(done().disabled).toBe(true);
    arrive('Import-Certificate');

    expect(await screen.findByDisplayValue('Import-Certificate')).toBeTruthy();
    expect(screen.getByText(/^Last modified 2026-10-2\d \d\d:\d\d by ops@contoso\.com$/)).toBeTruthy();
    expect(screen.getByText('1 of 2 selected')).toBeTruthy();
    fireEvent.click(done());

    expect(upload).not.toHaveBeenCalled();
    expect(onApply.mock.calls[0][0].locations).toEqual({ all: false, locationIds: ['osl'] });
  });

  it('refuses selected locations with none chosen', async () => {
    render(<ScriptDialog script={{ ...existing, locations: { all: false, locationIds: [] } }} others={[]} locations={locations} loadText={() => Promise.resolve('exit 0')} onClose={() => undefined} onApply={() => undefined} />);
    await screen.findByDisplayValue('exit 0');

    fireEvent.click(done());

    expect(screen.getByText('Choose at least one location, or run the script at all locations of this profile.')).toBeTruthy();
  });

  it('opens the Script environment dialog from the info icon, with every row', () => {
    render(<ScriptDialog others={[]} locations={locations} loadText={never} onClose={() => undefined} onApply={() => undefined} />);

    expect(screen.getByText('Exit code 0 is success. Scripts cannot restart the device.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Script environment' }));

    const environment = within(screen.getByRole('dialog', { name: 'Script environment' }));
    expect(environment.getByText('What a script can rely on in each phase.')).toBeTruthy();
    expect(environment.getAllByRole('rowheader').map((h) => h.textContent)).toEqual(['When', 'Runs in', 'PowerShell', 'Installed Windows', 'Native variables', 'Registry', 'Network', 'Result', 'Timeout', 'Restarts', 'Secrets']);
    expect(environment.getByText('$env:CI_WINDOWS_DRIVE')).toBeTruthy();
    fireEvent.click(environment.getAllByRole('button', { name: 'Close' }).at(-1)!);
    expect(screen.queryByRole('dialog', { name: 'Script environment' })).toBeNull();
  });

  it('stops an upload on Cancel', async () => {
    let signal: AbortSignal | undefined;
    const upload = vi.fn((_k: string, _t: string, _p?: unknown, s?: AbortSignal) => { signal = s; return new Promise<never>(() => undefined); });
    const onClose = vi.fn();
    render(<ScriptDialog others={[]} locations={locations} loadText={never} upload={upload} onClose={onClose} onApply={() => undefined} />);
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Wake on LAN' } });
    fireEvent.change(editor(), { target: { value: 'exit 0' } });
    fireEvent.click(done());
    await waitFor(() => expect(upload).toHaveBeenCalled());

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(signal?.aborted).toBe(true);
    expect(onClose).toHaveBeenCalled();
  });
});
