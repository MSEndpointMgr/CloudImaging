import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { CircleCheck, CircleX, RotateCw, Upload, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { CodeTextArea } from '../ui/code-text-area.tsx';
import { MAX_ANSWER_FILE_BYTES, problemHeading, problemText, validateAnswerFile, type AnswerFileProblem, type AnswerFileValidation } from '../../lib/answerFileValidation.ts';
import {
  ANSWER_FILE_NOT_UPLOADED_MESSAGE,
  SetupProfileApiError,
  answerFileEditorSizeLabel,
  uploadProgressLabel,
  uploadSetupProfileContent,
  utf8Length,
  type SetupAnswerFile,
} from '../../lib/setupProfiles.ts';

type UploadState = { kind: 'idle' } | { kind: 'uploading'; loaded: number; total: number } | { kind: 'failed'; message: string };

/**
 * Edit or upload the profile's answer file. Checked as you type with the same rules as Imaging
 * Core; Done stays disabled while anything is found, then uploads the text and shows any problem
 * Core still finds. Loaded on demand so the XML parser is only downloaded when someone opens it.
 */
export default function AnswerFileDialog({ answerFile, loadText, serverProblems, onClose, onApply, readOnly = false, upload = uploadSetupProfileContent }: {
  /** The current file; null to add one. */
  answerFile: SetupAnswerFile | null;
  /** Loads the text of the current file. */
  loadText: (sha256: string) => Promise<string>;
  /** Problems Imaging Core returned for the last save, shown until the text changes. */
  serverProblems?: AnswerFileProblem[];
  onClose: () => void;
  onApply: (answerFile: SetupAnswerFile, text: string) => void;
  /** Shows the file without editing, for viewers and earlier versions. */
  readOnly?: boolean;
  upload?: typeof uploadSetupProfileContent;
}): React.ReactElement {
  const existing = Boolean(answerFile?.contentSha256);
  const [text, setText] = useState<string | null>(existing ? null : '');
  const [original, setOriginal] = useState<string | null>(existing ? null : '');
  const [result, setResult] = useState<AnswerFileValidation>(() => validateAnswerFile(''));
  const [checked, setChecked] = useState<string | null>(existing ? null : '');
  const [shownServerProblems, setShownServerProblems] = useState(serverProblems ?? []);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [state, setState] = useState<UploadState>({ kind: 'idle' });
  const fileInput = useRef<HTMLInputElement>(null);
  const abort = useRef<AbortController | null>(null);

  useEffect(() => {
    if (!answerFile?.contentSha256) return;
    let live = true;
    loadText(answerFile.contentSha256)
      .then((value) => {
        if (!live) return;
        setText(value);
        setOriginal(value);
        setResult(validateAnswerFile(value));
        setChecked(value);
      })
      .catch((err: unknown) => { if (live) setLoadError(err instanceof Error ? err.message : 'Could not load the answer file.'); });
    return () => { live = false; };
  }, [answerFile, loadText]);

  useEffect(() => () => abort.current?.abort(), []);

  // Debounced, so typing in a large file stays responsive.
  useEffect(() => {
    if (text === null || text === checked) return;
    const timer = setTimeout(() => {
      setResult(validateAnswerFile(text));
      setChecked(text);
    }, 250);
    return () => clearTimeout(timer);
  }, [text, checked]);

  const change = (value: string) => {
    setText(value);
    setShownServerProblems([]);
    setUploadError(null);
    if (state.kind === 'failed') setState({ kind: 'idle' });
  };

  const readFile = async (file: File | undefined) => {
    if (!file) return;
    const bytes = new Uint8Array(await file.arrayBuffer());
    const bom = bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf ? 3 : 0;
    if (bytes.length - bom > MAX_ANSWER_FILE_BYTES) {
      setUploadError('The file is larger than 2 MB. Choose a smaller answer file.');
      return;
    }
    const value = new TextDecoder('utf-8').decode(bytes).replace(/\r\n/g, '\n');
    change(value);
    setResult(validateAnswerFile(value));
    setChecked(value);
  };

  const apply = async () => {
    if (text === null || !answerFileReady) return;
    if (text === original && answerFile) {
      onApply(answerFile, text);
      return;
    }
    const controller = new AbortController();
    abort.current = controller;
    setState({ kind: 'uploading', loaded: 0, total: utf8Length(text) });
    try {
      const uploaded = await upload('answerFile', text, (loaded, total) => setState({ kind: 'uploading', loaded, total }), controller.signal);
      if (uploaded.problems && uploaded.problems.length > 0) {
        setShownServerProblems(uploaded.problems);
        setState({ kind: 'idle' });
        return;
      }
      onApply({ contentSha256: uploaded.sha256, sizeBytes: uploaded.sizeBytes, passes: uploaded.passes ?? result.passes }, text);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const refused = err instanceof SetupProfileApiError && err.status >= 400 && err.status < 500;
      setState({ kind: 'failed', message: refused ? err.message : ANSWER_FILE_NOT_UPLOADED_MESSAGE });
    } finally {
      abort.current = null;
    }
  };

  const cancel = () => {
    abort.current?.abort();
    onClose();
  };

  const bytes = useMemo(() => (text === null ? answerFile?.sizeBytes ?? 0 : utf8Length(text)), [text, answerFile]);
  const pending = text !== checked;
  const uploading = state.kind === 'uploading';
  const problems = shownServerProblems.length > 0 ? shownServerProblems : result.problems;
  const answerFileReady = text !== null && !pending && problems.length === 0 && uploadError === null;

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="answer-file-title">
      <div className="flex max-h-full w-full max-w-3xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="answer-file-title" className="text-base font-semibold">{readOnly ? 'Answer file' : answerFile ? 'Edit answer file' : 'Add answer file'}</h2>
            <p className="text-sm text-muted-foreground">Used by Windows setup on every device that gets this profile.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={cancel} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-3 overflow-y-auto p-5">
          <div className="flex items-center gap-2">
            <label htmlFor="answer-file-content" className="flex-1 text-sm font-medium">unattend.xml</label>
            {!readOnly && (
              <>
                <Button variant="outline" disabled={uploading || text === null} onClick={() => fileInput.current?.click()}><Upload />Upload file</Button>
                <input ref={fileInput} type="file" accept=".xml" className="hidden" aria-hidden="true" tabIndex={-1}
                  onChange={(e) => { void readFile(e.target.files?.[0]); e.target.value = ''; }} />
              </>
            )}
          </div>
          <CodeTextArea id="answer-file-content" label="unattend.xml" value={text ?? ''} onChange={change} rows={14} readOnly={readOnly || uploading || text === null} invalid={problems.length > 0} describedBy="answer-file-size answer-file-status" />
          <p id="answer-file-size" className="text-xs text-muted-foreground">{answerFileEditorSizeLabel(bytes)}</p>

          <div id="answer-file-status" aria-live="polite">
            {loadError ? (
              <p className="text-sm text-destructive">{loadError}</p>
            ) : text === null ? (
              <p className="text-sm text-muted-foreground">Loading the answer file</p>
            ) : uploadError ? (
              <p className="text-sm text-destructive">{uploadError}</p>
            ) : problems.length > 0 ? (
              <div className="flex items-start gap-3 rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3">
                <CircleX className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
                <div className="min-w-0 space-y-2">
                  <p className="text-sm font-medium">{problemHeading(problems)}</p>
                  <ul className="list-disc space-y-1 pl-4">
                    {problems.map((p, i) => <li key={`${p.code}-${String(p.line)}-${String(i)}`} className="text-sm text-muted-foreground">{problemText(p)}</li>)}
                  </ul>
                </div>
              </div>
            ) : !pending && text.trim() ? (
              <p className="flex items-center gap-2 text-sm text-muted-foreground">
                <CircleCheck className="h-4 w-4 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
                Valid. Passes: {result.passes.join(', ') || 'none'}.
              </p>
            ) : null}
          </div>
        </div>

        <div className="flex items-center justify-between gap-4 border-t border-border px-5 py-3">
          <div className="min-w-0 flex-1">
            {state.kind === 'uploading' && (
              <div className="flex min-w-0 items-center gap-3">
                <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted" role="progressbar" aria-label="Upload progress" aria-valuemin={0} aria-valuemax={100}
                  aria-valuenow={state.total > 0 ? Math.round((state.loaded / state.total) * 100) : 0}>
                  <div className="h-full rounded-full bg-primary" style={{ width: `${String(state.total > 0 ? Math.round((state.loaded / state.total) * 100) : 0)}%` }} />
                </div>
                <span className="shrink-0 text-sm text-muted-foreground">{uploadProgressLabel(state.loaded, state.total)}</span>
              </div>
            )}
            {state.kind === 'failed' && <p role="alert" className="flex items-center gap-2 text-sm text-destructive"><CircleX className="h-4 w-4 shrink-0" aria-hidden="true" />{state.message}</p>}
          </div>
          <div className="flex shrink-0 gap-2">
            {readOnly ? (
              <Button variant="outline" onClick={onClose}>Close</Button>
            ) : (
              <>
                <Button variant="secondary" onClick={cancel}>Cancel</Button>
                {state.kind === 'failed'
                  ? <Button onClick={() => void apply()}><RotateCw />Retry</Button>
                  : <Button onClick={() => void apply()} disabled={!answerFileReady || uploading}>Done</Button>}
              </>
            )}
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
