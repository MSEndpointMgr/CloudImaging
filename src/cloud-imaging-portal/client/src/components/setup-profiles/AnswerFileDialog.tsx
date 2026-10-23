import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { CircleCheck, CircleX, Upload, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { CodeTextArea } from '../ui/code-text-area.tsx';
import { MAX_ANSWER_FILE_BYTES, problemHeading, problemText, validateAnswerFile, type AnswerFileProblem, type AnswerFileValidation } from '../../lib/answerFileValidation.ts';

/**
 * Edit or upload the profile's answer file. Checked as you type with the same rules as Imaging
 * Core; Done stays disabled while anything is found. Loaded on demand so the XML parser is only
 * downloaded when someone opens it.
 */
export default function AnswerFileDialog({ content, serverProblems, onClose, onApply }: {
  /** The current file; empty to add one. */
  content: string;
  /** Problems Imaging Core returned for the last save, shown until the text changes. */
  serverProblems?: AnswerFileProblem[];
  onClose: () => void;
  onApply: (content: string, passes: string[]) => void;
}): React.ReactElement {
  const [text, setText] = useState(content);
  const [result, setResult] = useState<AnswerFileValidation>(() => validateAnswerFile(content));
  const [checked, setChecked] = useState(text);
  const [shownServerProblems, setShownServerProblems] = useState(serverProblems ?? []);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  // Debounced, so typing in a large file stays responsive.
  useEffect(() => {
    if (text === checked) return;
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
  };

  const upload = async (file: File | undefined) => {
    if (!file) return;
    if (file.size > MAX_ANSWER_FILE_BYTES * 2) {
      setUploadError('The file is larger than 256 KB. Choose a smaller answer file.');
      return;
    }
    const value = (await file.text()).replace(/\r\n/g, '\n');
    change(value);
    setResult(validateAnswerFile(value));
    setChecked(value);
  };

  const pending = text !== checked;
  const problems = shownServerProblems.length > 0 ? shownServerProblems : result.problems;
  const canApply = !pending && problems.length === 0 && uploadError === null;

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="answer-file-title">
      <div className="flex max-h-full w-full max-w-3xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="answer-file-title" className="text-base font-semibold">{content ? 'Edit answer file' : 'Add answer file'}</h2>
            <p className="text-sm text-muted-foreground">Used by Windows setup on every device that gets this profile.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-3 overflow-y-auto p-5">
          <div className="flex items-center gap-2">
            <label htmlFor="answer-file-content" className="flex-1 text-sm font-medium">unattend.xml</label>
            <Button variant="outline" onClick={() => fileInput.current?.click()}><Upload />Upload file</Button>
            <input ref={fileInput} type="file" accept=".xml" className="hidden" aria-hidden="true" tabIndex={-1}
              onChange={(e) => { void upload(e.target.files?.[0]); e.target.value = ''; }} />
          </div>
          <CodeTextArea id="answer-file-content" label="unattend.xml" value={text} onChange={change} rows={14} invalid={problems.length > 0} describedBy="answer-file-status" />

          <div id="answer-file-status" aria-live="polite">
            {uploadError ? (
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

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button onClick={() => onApply(text, result.passes)} disabled={!canApply}>Done</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
