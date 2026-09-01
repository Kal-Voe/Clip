# Clip — Icon & Styling Uniformity Audit
_2026-09-01 · scope: `src/Clip.Shell`, `src/Clip.Watcher`, `assets/icons`_
_Living document. Findings are marked RESOLVED / CORRECTED / open as they land; two of
the original findings turned out to be wrong and say so where they sit._

## Verdict

**Original:** the colour system is good (a real token set, one dark theme). The icon
system is not a system — six unrelated icon families rendered at eight different sizes
with a fixed pen, so apparent line weight varied ~2x across the window and ~9x between
the smallest chrome icon and the largest item glyph.

**Now (through v1.10.0):** line weight is derived from display size and uniform; the two
big third-party icon sets are deleted; every file type gets its real Windows icon with a
drawn, labelled fallback where Windows has none. What is left is listed under Fix order
— chiefly two surviving SVGs (section 1), the WinForms tray menu and the stock
MessageBoxes (section 8).

Two findings in here were **wrong** and are corrected in place rather than deleted: the
"37 loose hexes" miscount (section 7) and the corner-radius scale (section 7). Both
would have caused damage if acted on literally.

---

## 1. Icon families — RESOLVED, and not the way this audit proposed

_Original finding: six unrelated icon families in one window. Two of them (the 54
per-extension svgrepo documents, and the 1024-grid extension-less fallback) are gone
as of v1.10.0. What follows is the state now._

| Family | Grid | Style | Where it shows |
|--------|------|-------|----------------|
| A — hand-drawn WPF vectors | 24 | outline, round caps/joins, one derived line weight | chrome (search, gear, plus, chevrons, expand), item glyphs (text/link/email/folder/image/file), **and the new labelled document fallback** |
| C — `file-icon-plaintext.svg` | 24 | outline-converted-to-fill, stroke 1.5 | every copied text clip |
| D — `file-icon-audio.svg` | 122.88 | solid filled music note | all audio files |
| W — Windows shell icons | n/a | full colour, whatever the app ships | **every registered file type**, not the ten that used to be hard-coded |

**The fork this audit posed was the wrong fork.** It offered "vendor Lucide" vs "draw
~12 category marks". Both were rejected, for a reason neither option accounted for:
any category scheme needs a hand-maintained extension-to-category list, and there are
more file types in the world than a list can hold. The bug that started it was
literally a gap in such a list — `ShouldUseWindowsFileIcon` had `vsd` and `vsdx` but
not `vsdm`, so a Visio macro file fell through to a generic glyph.

What shipped has no list in it at all, in two layers:

1. Ask Windows for **every** extension, through the existing `SourceAppIcons` resolver,
   LRU and async cold-miss swap. `ShouldUseWindowsFileIcon` is deleted.
2. When Windows returns its generic blank page — detected by resolving a deliberately
   unregistered extension once per size/DPI and byte-comparing — draw an outline
   document on the 24 grid with the extension printed on it. This goes through
   `RenderItemVectorIcon` and therefore `IconPen`, so it carries the normalised line
   weight rather than inventing a second one.

So the monochrome/colour split that this section originally called the worst offender
still exists, but it is now a **boundary with a meaning** — a real registered icon
where the system has one, our own mark where it doesn't — rather than an accident of
which ten extensions someone typed into a list.

**Still open, and smaller:** families C and D are the last two third-party SVGs and
neither matches family A. `file-icon-plaintext.svg` is stroke 1.5 on a 24 grid where
everything hand-drawn is now derived-weight; `file-icon-audio.svg` is a solid filled
note on a 122.88 grid. They cover the two most common non-file clips (copied text,
audio), so they are seen constantly. Redrawing those two on the 24 grid is a small,
list-free job and is the remaining icon-uniformity work.

**Load-bearing:** the drawn extension label is capped at 3 characters, measured not
guessed — at 4 the fit rule drops cap height below three real pixels on a 28px row, so
"VSDM" rendered visibly smaller than "PDF" next to it. Do not raise that cap without
re-measuring.

---

## 2. Line weight is not constant — RESOLVED (`19b09f3`, v1.9.0)

_The table below is the state before the fix; `IconPen` now derives thickness from
display size, and the drawn extension label added in v1.10.0 goes through it too._

Every vector icon is drawn on a 24×24 grid with a **fixed** pen, then scaled by the
`Image` element. Actual on-screen stroke = `pen × displaySize / 24`:

| Icon | Pen | Shown at | **Real stroke** |
|------|-----|----------|-----------------|
| Filter chevrons (date/file/media) | 2.2 | 11 px | **1.01 px** |
| Settings dropdown chevron | 2.2 | 11 px | 1.01 px |
| New-snippet plus | 2.2 | 12 px | 1.10 px |
| Settings gear | 1.8 | 16 px | 1.20 px |
| Search lens | 2.2 | 14 px | 1.28 px |
| Expand arrows | 2.2 | 15 px | 1.38 px |
| Row item glyphs (text/audio) | 1.8 | 22 px | **1.65 px** |
| Row + preview header glyphs | 1.8 | 28 px | **2.10 px** |
| Empty-preview placeholder glyph | 1.8 | 128 px | **9.60 px** |

The gear reads noticeably heavier than the chevrons beside it; the row glyphs read
twice as heavy as the chrome; the 128 px placeholder is a blob. The `1.8` on the gear
is already an ad-hoc patch for exactly this problem — applied to one icon only.

Fix: derive pen thickness from display size so every icon lands on one target weight.

---

## 3. Duplicated icon code — RESOLVED (`19b09f3`, `f8cdef6`)

_Chevron deduped; the two pickers' shared app row extracted to
`MainWindow.AppRowContent` after it had already drifted. The pickers still share ~200
lines of chrome — deliberately left, see Fix order._

- `CreateDropdownChevronIcon()` (line ~14074) is a **byte-for-byte reimplementation** of
  `RenderChromeIcon(ChevronDown)` (line ~10494) — same three points, same 2.2 pen,
  same 24 grid, separate cache. Two implementations of one icon.
- `OpenWithWindow` and `ExcludedAppPickerWindow` are near-identical ~250-line copies
  (same 620×520, same radius 14, same 46/44/*/46 row layout, **two separate `IconCache`
  dictionaries**), each taking the palette as **11 positional brush arguments** instead
  of reading resources. Change one, forget the other.

---

## 4. Non-icons standing in for icons — RESOLVED (`19b09f3`, `d18c754`)

- The submenu arrow in the action menu is the **ASCII character `">"`** in a `TextBlock`
  (line ~5102). The app has a proper vector chevron 4000 lines away.
- `"●"` used as a glyph.

---

## 5. Dead icon assets — RESOLVED

Originally 15 of 70 files unreachable. All gone, plus far more than that:

- 6 never-referenced loose SVGs and 9 Office/PDF icons shadowed by
  `ShouldUseWindowsFileIcon` — deleted in `19b09f3`.
- The remaining 54 per-extension documents and the extension-less fallback — deleted
  in `80e0753`, obsoleted by the two-layer scheme in section 1.

`assets/icons` now holds **two** files: `file-icon-plaintext.svg` and
`file-icon-audio.svg`. Both are still reachable (`MainWindow.xaml.cs:11828`, `:12019`).
Section 1 has the case for redrawing them.

---

## 6. Packaging bug (latent) — RESOLVED (`19b09f3`)

_Still worth reading: the same trap applies to the two SVGs that remain._

`assets/icons/*.svg` was copied to output by **`Clip.Watcher.csproj` only**
(`Clip.Watcher.csproj:8`). `Clip.Shell` is the project that *consumes* them and has no
such `Content` include. It works today only because both land in the same output folder.
Build or publish the Shell alone and every file-type icon silently degrades to the
generic vector document glyph. `Clip.Shell.csproj` now has its own include.

---

## 7. Colour and shape tokens

**Good:** 18 named brushes in `MainWindow.xaml` + a mirrored `SettingsPalette` in code.
One dark theme, no light-mode leftovers.

**Problems:**
- **The XAML palette describes an app that does not exist.** `MainWindow.xaml`
  declares eighteen theme brushes; `ApplyTheme` runs from the constructor and
  `SetBrush` overwrites every one before the window paints. They were not even the
  same hue family — the XAML said warm greys with a magenta cast (`Bg #1F1E1F`,
  `Muted #8F898F`) against the neutral greys that ship (`#1A1A1A`, `#A3A3A3`).
  **Fixed** — values reconciled, ownership documented.
- **`SettingsWindow.WarmCaches` warmed nothing.** It pre-warmed the dropdown-icon
  cache with `#646464` and `#989898`; the cache is keyed by the installed `Muted`,
  which is `#A3A3A3`. Two entries nothing read, and the first real chevron still
  rendered cold. **Fixed** — derived from `MainWindow.MutedHex`.
- **`Muted` naming was inverted.** `Muted2` `#BBBBBB` was the *brightest* of the
  three. `Muted3` `#777777` was declared twice and read nowhere. **Fixed** —
  `Muted2` → `MutedBright`, `Muted3` deleted.
- The remaining hex literals are **not** stray tokens and should stay: syntax
  colours in `CodePreviewPage`, the `JankHarness` test values, the app tile cream.
  One is worth a look on its own merits — `MediaPreviewPage` accents the video
  player `#8ab4ff` blue while the app's accent is `#FF6363` red.
- Font sizes are fine: 11/12/13/15/16/18. The stray `8` was the pinned-row bullet
  and is gone.

**Corrected — the radius finding was half wrong.** The audit originally called nine
distinct radii arbitrary. Four of them are geometry, not style: `3` is half the 6px
scrollbar track, `7` is half the 14px toggle knob, and `11` is half both the 22px
ring and the 22px toggle track. Those are circles and pills and snapping them to a
scale would produce ovals. The one genuine inconsistency was the bordered input
field — radius 7 in five places, 6 in the sixth — plus a lone 10 on the settings
popup where every other panel is 8. **Both fixed**; the rest stays.

---

## 8. Off-brand surfaces

- **Tray menu** is a stock WinForms `ContextMenuStrip` — light grey Windows chrome,
  square corners, system font, hanging off a dark app (`Clip.Watcher/Program.cs`).
- **6 stock Win32 `MessageBox.Show` dialogs** (1 in Watcher, 5 in Shell).

Both are outside the palette entirely. Lower priority than the icons — they are modal
and brief — but they are the two places the illusion breaks completely.

---

## 9. Things that are already right (do not "fix" these)

- No icon font, no emoji-as-icon anywhere. Menus and settings nav are deliberately
  text-only and consistently so.
- The light cream app tile against a dark palette is a documented, deliberate choice.
- Round caps + round joins on every hand-drawn icon.
- Everything sits on a 24×24 grid.
- Scrollbars are uniform app-wide (6 px, `#717176`, radius 3).

---

## Fix order

1. ~~**Normalise line weight**~~ — done, shipped in v1.9.0 (`19b09f3`).
2. ~~**Dedupe the chevron**; replace the `">"` and `"●"` characters~~ — done
   (`19b09f3`, `d18c754`).
3. ~~**Delete the dead SVGs**; fix the Shell packaging include~~ — done (`19b09f3`).
   `DomainMonogram` deleted too (`f2c8b98`).
4. ~~**Collapse the file-type icon families**~~ — done, shipped in v1.10.0
   (`5e16cdb`, `80e0753`), by a different and better route than this audit proposed.
   See section 1.
5. ~~Palette, `Muted*` naming, radii~~ — done (`506c780`, `b8acc9b`, `92c5a35`),
   with the corrections recorded in section 7.
6. Re-skin the tray menu and replace the MessageBoxes — still open.
7. Deduplicate `OpenWithWindow` / `ExcludedAppPickerWindow` — still open.

### What replaced the step 4 fork

The fork is closed. Neither option was taken — see section 1 for what shipped and why
a category scheme was the wrong shape for the problem.

The residue is small: redraw `file-icon-plaintext.svg` and `file-icon-audio.svg` on the
24 grid so the last two third-party SVGs match the hand-drawn set. No list required,
two icons, and they are the two most-seen non-file marks in the app.
