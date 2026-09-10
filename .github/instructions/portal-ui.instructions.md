---
applyTo: 'src/cloud-imaging-portal/client/**'
description: 'Design system rules for the Cloud Imaging portal frontend (tokens, typography, spacing, icons, contrast, primitives, accessibility).'
---

# Portal UI design system

Rules for `src/cloud-imaging-portal/client`. React 19 + Tailwind 3.4 (`darkMode: 'class'`) with
hand-rolled shadcn-style primitives in `src/components/ui/`. There is no component library to
defer to, so these conventions are the only thing keeping the surface coherent.

Every rule below has a reason. A rule without its reason gets "fixed" back by the next person, so
keep the reason when you move or restate one.

## 1. Theming: the primary-colour trap

`--color-primary` is **one RGB triple, shared by both light and dark themes, overridable per tenant
at runtime** (`context/brandingContext.tsx`, FR-038). There is no dark-mode variant and no control
over what a tenant sets.

- **Never use `text-primary` or `ring-primary` on a dark surface.** The default blue-600 on the
  dark sidebar `#0d1321` is **3.63:1 — fails WCAG AA**.
- Brand colour may carry **surface tint (`bg-primary/10`) and shape** (e.g. the sidebar's 3px
  `before:` rail). Text and focus rings use theme-fixed tokens: `text-sidebar-accent-foreground`,
  `ring-ring`.
- Brand/surface colours are RGB triples (not HSL) specifically so Tailwind's `<alpha-value>` works
  in `bg-primary/10`. Neutrals stay HSL.
- Never hardcode a hex value. Use the semantic token.

## 2. Typography

- Fonts are self-hosted via `@fontsource-variable/*`, imported in `main.tsx` **before**
  `./index.css`. **Never move to a CDN** — the server sets `helmet()` CSP and the portal must work
  on isolated/air-gapped networks.
- `fontFamily` and the full `fontSize` scale are pinned in `tailwind.config.ts`. Values equal the
  Tailwind defaults on purpose: pinning is documentation-as-code so an off-scale `text-[15px]`
  stands out in review. Stay on the scale.
- Tailwind 3.4 already pairs every font size with a 4px-multiple line-height. **Do not override
  line-heights.**
- **Weight vocabulary is 400 / 500 / 600 only.** No `font-bold` or heavier — 600 is the top of the
  scale for this UI.
- **Size vocabulary — 12px (`text-xs`) is reserved for:** badges, uppercase table headers,
  timestamps, and metadata. Everything else is `text-sm` or larger, including primary table data,
  **inline validation errors**, unsaved-changes notices, empty-state descriptions, and setting
  descriptions. Validation errors matter most: 12px destructive text is the easiest thing in the
  UI to miss.

## 3. Spacing

- 4px grid. **No 6px (`*-1.5`) or 10px (`*-2.5`)** steps in `gap` / `space-*` / `p*` / `m*`.
  `px-5` (20px) is on-grid — do not flag it.
- 2px optical nudges (`*-0.5`) are allowed, and **sizing** utilities (`h-1.5`, `w-3.5`) are out of
  scope for this rule.
- Regression check after any spacing change:
  `\b(gap|space-[xy]|p|px|py|pt|pb|pl|pr|m|mx|my|mt|mb|ml|mr)-(1\.5|2\.5|3\.5)\b`

**Icon/text vertical alignment:** `mt-0.5` centres a 16px icon in `text-sm`'s **20px** line box. In
a `text-xs` (16px) line box the same offset pushes the icon low. Match the offset to the line box,
or drop it.

## 4. Icons

- **Scale is 12 / 16 / 20 / 24** (plus 40 for the brand mark). An icon matches the step of the text
  beside it: 12px with `text-xs`, 16px with `text-sm`. No 11/13/14/18/22/28 one-offs, no
  `h-3.5 w-3.5`. (`size={18}` is grandfathered for sidebar nav only.)
- `Button` sets `[&_svg]:size-4` in its base class. **CSS width/height beats lucide's `size`
  presentation attribute**, so a `size={n}` prop on an icon inside `<Button>` is inert and
  misleading. Don't add one — let the button size it.
- Decorative icons need `aria-hidden="true"`.

## 5. Contrast — compute it, don't eyeball it

- Check the ratio against the **actual composite background**, not against white. Light
  `--muted-foreground` passes on white (4.76:1) but **failed on `bg-primary/10` (4.14:1)**, which is
  exactly where the sidebar puts it. It is now 42% lightness.
- `text-xs font-semibold` (table headers) is 12px bold and does **not** qualify for the WCAG
  large-text exemption. `TableHead` uses `text-foreground/75`, not muted. If `--table-header`
  lightness changes, recheck it.

## 6. Use the primitives

Reach for `src/components/ui/` before writing markup. Where a primitive exists, hand-rolling a
second copy is how the two `role="switch"` implementations silently drifted apart (one had an
`aria-label`, one had none).

- **`Select`** — all dropdowns. `appearance-none` + a lucide chevron, so no select ever shows the
  OS-drawn triangle. Size variants mirror `Button` (`sm` = h-8, `default` = h-9) because selects sit
  beside buttons and a 4px delta reads as a bug. Uses `bg-background`, not `bg-transparent` — a
  native select needs an opaque computed background or the browser paints the popup with its canvas
  colour. Width goes on `wrapperClassName`, visuals on `className`.
- **`Button`** — `sm` is the standard portal size. Don't set `text-xs` on it; it inherits
  `text-sm`.
- **`CopyableId`** — any value an operator transcribes (serials, SHA-256 digests, thumbprints).
- **`RelativeTime`** — "how long ago". It shares one module-level 30s ticker across all instances,
  so a 200-row table costs one interval, not 200. Reports keep absolute `formatDateTime`, because an
  export needs the instant.
- **`Tooltip`** — supplementary hints only, never the sole source of information.
- **`Switch`** — the only `role="switch"`. `label` is required.
- **`EmptyState`** — pass an `action` when there is a meaningful next step; omit it when there
  isn't. An empty state that only says "nothing here" wastes the moment.

`index.css` declares `color-scheme: light` on `:root` and `color-scheme: dark` on `.dark`. This is
the **only** lever over UA-drawn chrome (select popups, scrollbars, spinners, file-picker buttons);
CSS cannot reach those internals. Don't remove it.

## 7. Accessibility

- **`title="..."` is not an accessible name.** It's unreliable in screen readers and unreachable by
  keyboard or touch. Every icon-only control needs an `aria-label`, plus a `Tooltip` when the action
  isn't self-evident.
- Focus rings are uniformly `focus-visible:ring-2 ring-ring ring-offset-2 ring-offset-background`.
  Never `focus:` (fires on mouse click) and never `ring-primary` (§1).
- Disclosure controls carry `aria-expanded`, and `aria-controls` pointing at the revealed region.
- `aria-describedby` must sit on the **focused element**, not on a wrapper — AT reads the
  description of what's focused, and a wrapper `<span>` is never focused.
- `body` sets global `select-none`. Anything an operator transcribes must opt back in
  (`TableCell` is `select-text`).

## 8. Feedback and state

- Every async surface needs **loading, empty, error, and success** states. Happy-path-only is
  incomplete.
- Loading placeholders should mirror the shape of the real content (`Skeleton` in table cells), not
  a centred spinner.
- Progress must read as progress. `animation['loading-bar']` stays ~1.4s; at 0.35s the fill
  travelled 350% of the track per cycle and read as an alarm.
- **Chrome must never gate on the network.** `BrandingProvider` seeds from `localStorage`
  synchronously so the correct mark paints in the first frame, and the sidebar/header/loading
  screens render their mark **unconditionally**. There used to be an `isLoaded` gate with an
  opacity fade — a failed `/api/branding` call never populates the cache, so the header sat blank
  and then faded in on *every* load, forever. Don't reintroduce a gate.
- Destructive actions go through `ConfirmImpactDialog` and must state the actual impact.

## 9. Navigation

- Nav IA is grouped: **Overview / Imaging / Insights / Administration**. The label is "Devices",
  not "Sessions" (the route is still `/sessions`).
- The decorative locked sidebar in `AccessDenied.tsx` **duplicates** the real sidebar's markup.
  Change both or they drift.
- Route titles live only in `lib/routeTitles.ts` (`resolveSectionTitle`, longest-prefix match on
  segment boundaries). `Header` renders it; `AppShell` writes `document.title`.
- Content max-width is `max-w-[1440px]`. The device and image tables carry 6–7 columns and were
  truncating on wider displays.

## 10. Before you call it done

```powershell
Set-Location src/cloud-imaging-portal/client
npm run build; npm run lint; npm run test:run
```

- **Trust `npm run build` (tsc)** over editor diagnostics, which go stale right after an edit. It
  catches unused-import leftovers (`TS6133`).
- Rendering tests are possible and expected for new primitives. Test files live outside the client
  package, so `vite.config.ts` `test.alias` pins `@testing-library/react`. There is **no
  `setupFiles`**, so `jest-dom` matchers are unavailable — assert on `textContent` /
  `getAttribute`.
- Source files use TS unicode **escapes**, not literal glyphs (`'\u2014'`, not `—`). Grepping for
  the glyph finds nothing.
