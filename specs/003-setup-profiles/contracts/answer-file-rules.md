# Contract: Answer File Rules

**Feature**: [../spec.md](../spec.md) | **Research**: [../research.md](../research.md) R9

One rule set, implemented twice and pinned by one case file:

| Implementation | File | Runs |
|---|---|---|
| Server, authoritative | `src/CloudImaging.ImagingCoreApi/Services/AnswerFileValidator.cs` | Every create, save and restore in Core |
| Portal, live | `src/cloud-imaging-portal/client/src/lib/answerFileValidation.ts` | On every edit in the answer file dialog (debounced 250 ms) and on Upload file |
| Shared cases | `tests/fixtures/answer-file-cases.json` | Read by `tests/cloud-imaging-portal/client/answer-file-validation.test.ts` and `tests/CloudImaging.ImagingCoreApi.Tests/Services/AnswerFileValidatorTests.cs` |

A change to a rule, a message or a line rule must update both implementations and the case file in
the same change; the two test suites fail otherwise.

---

## Output

A list of problems, sorted by line, then by code; at most 50. Each problem:

```json
{ "code": "domainJoin", "line": 14, "message": "Domain join is not allowed, because it breaks Windows Autopilot. Remove the Microsoft-Windows-UnattendedJoin component." }
```

- `code`: stable machine identifier (camelCase word, never shown in the UI).
- `line`: 1-based line of the offending start tag (or of the parse error); 0 for whole-file problems.
- `message`: exactly the text below. The UI shows "Line 14: <message>" (or the message alone for line 0).

Server response on violation: 400 problem with `detail` "The answer file has N problems." (or
"The answer file has 1 problem.") and the `errors` array.

---

## Parsing

- XML 1.0, UTF-8. A leading BOM is accepted and ignored.
- No DTD processing and no external resolution (server: `DtdProcessing.Prohibit`,
  `XmlResolver = null`; portal: `saxes` with a `doctype` handler that records the problem).
- Comments, processing instructions and CDATA are ignored by every rule except well-formedness.
- Element and attribute names in rules are compared on the **local name, case-insensitive,
  regardless of namespace** (stricter than Windows setup, which is case-sensitive, so no
  spelling variant slips through).
- When the file is not well-formed or is too large, only that problem is returned; the other
  rules need a parsed document.

---

## Rules

| Code | Matches | Line | Message |
|---|---|---|---|
| `tooLarge` | More than 262,144 bytes of UTF-8 | 0 | The answer file is larger than 256 KB. Remove settings you do not need. |
| `notWellFormed` | Any XML well-formedness error | Line of the error | This is not well-formed XML. Check for a missing closing tag or an unescaped & or < on or before this line. |
| `dtdNotAllowed` | A `<!DOCTYPE ...>` declaration | Line of the declaration | Document type declarations are not allowed. Remove the DOCTYPE line. |
| `notAnswerFile` | Root element is not `unattend` in namespace `urn:schemas-microsoft-com:unattend` | Line of the root element | This is not an answer file. The root element must be unattend in the urn:schemas-microsoft-com:unattend namespace. |
| `windowsPePass` | `settings` element whose `pass` attribute is `windowsPE` | Line of `settings` | The windowsPE pass is not allowed, because Windows is already installed when this file is used. Remove this settings element. |
| `offlineServicingPass` | `settings` element whose `pass` attribute is `offlineServicing` | Line of `settings` | The offlineServicing pass is not allowed. Remove this settings element and use registry values or scripts instead. |
| `domainJoin` | `component` element whose `name` attribute is `Microsoft-Windows-UnattendedJoin` | Line of `component` | Domain join is not allowed, because it breaks Windows Autopilot. Remove the Microsoft-Windows-UnattendedJoin component. |
| `skipMachineOobe` | Element `SkipMachineOOBE` | Line of the element | SkipMachineOOBE is not allowed, because it skips Windows Autopilot. Remove this element. |
| `skipUserOobe` | Element `SkipUserOOBE` | Line of the element | SkipUserOOBE is not allowed, because it skips Windows Autopilot. Remove this element. |
| `autoLogon` | Element `AutoLogon` | Line of the element | AutoLogon is not allowed, because it signs in with a stored account. Remove the AutoLogon element. |
| `plainTextPassword` | (a) Element `PlainText` whose trimmed text is `true`; (b) an element whose local name is `Password` or ends with `Password` and that has a `Value` child but no `PlainText` child with trimmed text `false` | Line of the `PlainText` element (a) or of the password element (b) | Plain-text passwords are not allowed. Remove this password. |
| `runnerCommandPresent` | Element `Path` whose text contains `Invoke-CloudImagingSetup.ps1` | Line of `Path` | This file already contains the Cloud Imaging setup runner command. Remove that RunSynchronousCommand; Cloud Imaging adds it to each device. |
| `tooManySpecializeCommands` | A `RunSynchronous` element inside a specialize `Microsoft-Windows-Deployment` component with more than 499 `RunSynchronousCommand` children | Line of `RunSynchronous` | The specialize pass has more than 499 commands. Remove at least one, because Cloud Imaging adds its own. |

Notes:
- (b) of `plainTextPassword` closes the gap where Windows treats a password without `PlainText`
  as plain text. Windows SIM's "hide sensitive data" output (`PlainText` false) is accepted; it is
  an encoding, not encryption, which is why the script and answer file dialogs warn against secrets.
- A pass attribute is compared case-insensitively, so `WindowsPE` is refused too.
- `passes` shown on the profile page are the distinct `settings/@pass` values of a valid file, in
  document order.

---

## Case file format (`tests/fixtures/answer-file-cases.json`)

```json
[
  {
    "name": "domain join and windowsPE pass are both listed",
    "xml": "<?xml version=\"1.0\"?>\n<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">\n  <settings pass=\"windowsPE\">\n  </settings>\n  <settings pass=\"specialize\">\n    <component name=\"Microsoft-Windows-UnattendedJoin\"/>\n  </settings>\n</unattend>",
    "expected": [
      { "code": "windowsPePass", "line": 3 },
      { "code": "domainJoin", "line": 6 }
    ]
  }
]
```

`expected` lists code and line; both suites also assert that each `message` equals the table above
(the TypeScript and C# message constants are compared against a `messages` map at the top of the
case file). Required cases: one valid file with every allowed pass; one case per rule; every rule
at once; whole-file problems stopping evaluation; case variants (`windowspe`, `skipmachineoobe`);
an element in another namespace; CRLF and LF line endings giving the same lines; a BOM; a
256 KB boundary (exactly at and one byte over); three common well-formedness errors (unclosed
element, mismatched tag, bare `&`) with agreed lines.
