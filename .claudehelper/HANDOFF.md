# Clip — handoff

_Last updated 2026-08-28. **`main` is the trunk.** All work pushed, and **installed** — the copy in
`%APPDATA%\Programs\Clip` is this build._

## Native type, and the + moved onto the list (2026-08-31, v1.4.2)

**Fonts: stripped, not bundled.** The UI asked for Inter and JetBrains Mono and shipped neither, so
every machine -- including Isaiah's, checked with InstalledFontCollection -- was already rendering
Segoe UI Variable Text and Cascadia Mono. Bundling would have changed the look of the whole app for
him rather than restoring an intended one. Both fallbacks ship with Windows, so users already see
what he sees, and the native type suits an app whose whole aesthetic is now acrylic sitting beside
the system flyouts. The stacks name only what exists; nothing changed visually.

**New Snippet moved** from the footer's bottom right to a small muted + at the top right of the list
column, level with the TODAY header, on request -- it belongs with the list it adds to rather than
in the app chrome. It sits outside the ScrollViewer so it stays put while the list scrolls.

**Dropped a now-pointless concession:** the capture-paused badge used to hide the Paste & stay hint
because six hints plus two buttons overflowed the bar. With the footer down to two hints they both
fit, so nothing hides any more.

## Footer trimmed, transforms always shown, and the resize found (2026-08-31, v1.4.1)

Isaiah's round of feedback on 1.4.0, and one finding that was not Clip's fault.

**The stuck Shift was real and environmental.** Alt+V was intermittently opening Raycast instead of
Clip. `GetAsyncKeyState` showed **Right Shift physically and logically down** with nothing held, so
Alt+V was arriving as Alt+Shift+V and Raycast owns that. Clip was not the cause -- it was the only
thing noticing, `ReleaseStuckModifiers` had logged `released stuck modifiers 10` (0x10 = VK_SHIFT)
four times. Cleared by injecting key-ups for both shifts. If it recurs it is the keyboard or a
remote-desktop session latching the modifier, not the app.

**The palette resizing itself** was real. The position log showed the same monitor opening at
`win=800x520` and `win=1200x780`, and once `1800x1170` -- each time the previous *physical* size
adopted as the new *logical* one and inflated again by the next monitor's scale. WPF mishandles
WM_DPICHANGED for a layered window, and this window is layered for the acrylic. Since the palette
has exactly one size there is nothing to preserve: `PositionOnMouseScreen` now re-asserts
`PaletteDesignWidth/Height` on every open (leaving fullscreen and expanded-image alone, which own
the size while they are on). A test asserts the constants still match the XAML.

**The Transform submenu showed only three rows** because it hid every transform that would not
change the text -- on a tidy one-line URL, trim, join and extract-links are all no-ops. That made
the feature look half-missing. The five reshaping rows are now always listed so the menu is the
same shape every time; only "Copy links only" stays conditional, because its absence means
something (no links here). Labels are plainer too: "Trim spaces and blank lines", "Join into one
line", "Copy links only". The offered set is now the pure `MainWindow.TransformOffers`, tested.

**Footer** is down to Enter/Paste and Shift+Enter/Paste & stay, on request. Copy, Actions, Pin and
Shortcuts caps are gone.

**Export/Restore proven, not assumed** -- Isaiah rightly pushed back that this was shippable
without a live check. Ran the real `Export`/`Restore` against a copy of his 500-item store: 500
entries out, 500 back, item counts match, and a junk zip is refused. The only files that do not
come back are `history.index.json`, `history.keys.json` and `history.top.index.json`, which are
derived and rebuilt on load.

### Next steps

1. Watch whether the palette ever opens at the wrong size again; if it does, the DPI re-assert
   needs to move earlier than the placement call.
2. Still open: bundle Inter/JetBrains Mono, and the glass has no drop shadow.

## OCR text, transforms, snippets, paste-and-stay, backup (2026-08-31, v1.4.0)

Isaiah asked what was left worth doing. Perf was already done and measured, so this was features.

- **Copy Text** on image items finally surfaces the OCR that was already being computed and stored --
  it had only ever been reachable by searching. 129 of the 500 items in the live store had OCR text
  sitting unused. Gated by the pure `CanCopyOcrText` (kind + `ExtractTextFromImages` + engine
  available + non-blank), so it is absent rather than present-and-failing. It re-reads the item from
  the store first: the list row carries a length-capped summary, so copying off the row would hand
  over a truncated transcript.
- **Transform submenu** (upper/lower/title/trim/single-line/extract URLs) in `ClipboardTextTransforms`,
  pure and heavily tested. Entries that would be no-ops are hidden, which is why all six are
  evaluated eagerly at menu-open -- a known cost on very large text items, noted below.
- **New Snippet** as a footer `+` button. The item is not created until Save, so an abandoned edit
  leaves nothing behind.
- **Shift+Enter pastes without closing** and advances to the next row. The follow-on row is chosen
  *before* the paste, because pasting re-copies the item and the watcher floats it back to the top --
  the list reorders underneath. Stops at the end rather than wrapping.
- **Export / Restore** the history as a zip, in History settings, with a round-trip test. Restore
  writes to a temp location and swaps, and refuses a zip that is not a Clip export.

1147 tests. Verified on screen: Transform submenu present in the action menu, and the footer shows
"Shift+Enter Paste & stay" alongside the new `+`.

### Next steps

1. Worth Isaiah's eye: "Copy Text" on a screenshot, and Restore actually restoring.
2. Known cost: the Transform submenu runs all six transforms at menu-open so it can hide dead rows.
   Fine for ordinary clipboard text; on a multi-hundred-KB item it could hitch. The fix, if it is
   ever felt, is a size threshold above which all six are offered unconditionally.
3. Still open from the original audit: bundle Inter/JetBrains Mono (the UI names fonts it does not
   ship, so everyone sees Segoe fallbacks), and the glass has no drop shadow.

## Both bars drag, buttons included (2026-08-31, v1.3.6)

Isaiah: dragging should work from the buttons in the top bar too, and from the bottom bar, and from
the search field when it is empty — but a press in the search field with text in it is a selection.

Now a click-versus-drag rule, the same one a real title bar uses. The press is armed on the Shell's
**tunnel** handlers (`PreviewMouseLeftButtonDown`) and deliberately never marked handled, so it still
reaches whatever was under it and every chip and footer key behaves exactly as before. Only once the
pointer passes `SystemParameters.MinimumHorizontal/VerticalDragDistance` does it convert into a
window drag. That is why a button can be a grab handle without losing its click.

`BeginWindowDrag` drops **two** captures: `Mouse.Capture(null)` so the button the drag started on
does not stay stuck in its pressed visual (it will never see the button-up), and `ReleaseCapture()`
without which the OS non-client loop refuses to start.

The decision itself is `MainWindow.ShouldArmChromeDrag`, pure and unit-tested — including the case
its test caught: before the first layout every ActualHeight is 0, and `y >= 0 - 0` would have made
the entire window a grab handle.

Verified on the running app: drag from the All chip, a footer keycap, blank footer, the empty search
field and blank top bar all move the window; a click on the Text chip selects the filter and moves
the window 0,0.

## Always centred, and draggable by the top bar (2026-08-31, v1.3.5)

Isaiah: _"a lot of the time it does not open in the center"_, and he wants to drag it by blank space
in the top bar.

**Centring.** `PositionOnMouseScreen` centred against `GetWindowRect`, i.e. whatever physical size
the window happened to be at that instant. Since the app went Per-Monitor-V2 DPI aware (1.2.0), a
window landing on a differently-scaled monitor gets rescaled by Windows, and the measurement raced
it — the log has the same monitor centring against `win=1200x780` on one open and `win=800x520` on
the next, so half the time it centred the unscaled size and the palette sat low and right. Fixed by
deriving the size instead: DIP `Width`/`Height` (which never race) times the **target** monitor's
scale from `GetDpiForMonitor`, extracted into the testable `MainWindow.CenteredPlacement`. A
`DpiChanged` handler re-centres once if Windows rescales after the move.

**A trap worth remembering:** the first fix also passed that size to `SetWindowPos`. Do not. WPF
reads the new physical size back into `Width`/`Height` as DIPs, so on a 150% monitor an 800-DIP
palette set to 800 physical becomes 533 DIPs and shrinks again every open — the log went
`win=800x520` → `win=533x347`. `SetWindowPosNoSize` is load-bearing; the computed size is only for
working out where the top-left goes.

Verified: 9 opens across all three monitors, offset 0,0 every time, size stable at 1200x780.

**Dragging.** The handler existed but the header Grid is inset by `Margin="12,10"`, leaving that band
dead. Added a full-bleed drag surface behind the row — and the important part: its background is
**`#01000000`, not `Transparent`**. The palette is a layered window for the acrylic, and a layered
window is click-through wherever its alpha is zero, so the OS never delivers the click and WPF's
"Transparent is hit-testable" rule never gets a say. That is why blank top bar did nothing while the
one spot that dragged turned out to be the painted search field. One step of alpha is invisible over
the blur and makes the pixels belong to the window. The drag itself is now `WM_NCLBUTTONDOWN` with
`HTCAPTION` rather than `DragMove()`, which throws if the button is already up and is least reliable
on exactly this kind of non-activating topmost window.

Note for future testing: synthetic `mouse_event` drags are flaky here (two runs failed on opposite
spots while the handler fired on all of them). Trust the handler firing plus a real hand.

## The glass is real now — and how it was measured (2026-08-31, v1.3.1)

Isaiah, on the 1.2.x "glass": _"it did not look like glass, it just like light grey."_ He was right,
and the reason is worth keeping: **DWMWA_SYSTEMBACKDROP_TYPE never blurred anything.** It returns
`hr=0` and logs `active=True`, so every check said it worked — but it paints a flat sheet and never
samples what is behind the window. Everything built on top of that (lowering alphas, retinting,
restyling the header) was tuning the wrong dial, because the missing ingredient was the blur itself.

**How to settle this class of question in five minutes** (scripts in the scratchpad, worth rebuilding
if they are gone): put a hard RED|BLUE split behind the window and sample one scanline through it.
Sharp jump = no blur, only alpha. Gradual ramp = real blur. Flat single colour = something opaque.
That test is what produced every number below; do not trust a screenshot's vibe again.

| approach | result |
|---|---|
| DWMSBT_TRANSIENTWINDOW (what 1.2.x shipped) | flat `D3D3D3`, zero trace of red or blue — the "light grey" |
| + WS_CAPTION\|WS_THICKFRAME | unchanged, still flat |
| accent acrylic on a NON-layered window | nothing (window renders black; no alpha to composite into) |
| **AllowsTransparency + ACCENT_ENABLE_ACRYLICBLURBEHIND** | **`481313 … 3A1334 … 15158C` — tinted AND blended. Real acrylic.** |

That last one is what `window-vibrancy`'s `apply_acrylic` calls, i.e. what asyar uses on Windows.
**The objection that blocked it for days was wrong:** the audit said never use AllowsTransparency
because it kills ClearType — but this window sets `TextRenderingMode="Grayscale"` on purpose, so
there was never anything to lose.

**Corners, also measured, and the first fix was wrong.** The blur is painted across the whole window
rect and ignores WPF's per-pixel alpha, so a rounded Shell alone leaves four wedges of tint outside
the arc. The first attempt clipped with `SetWindowRgn` — it returns success and does nothing, because
regions are ignored on layered windows (verified: corner stayed square). What does work, contrary to
the usual advice, is **`DWMWA_WINDOW_CORNER_PREFERENCE` on the layered window** — it clips the acrylic
and antialiases the arc. So DWM is the clipper again and `ShellCornerRadius` is back to **8** to match
it; a wider radius only hangs the Shell's border stroke inside DWM's arc as a second mismatched one.

Verified on the running app, not in a spike: scanline through the real palette ramps
`311717 → 2C1824 → 1F1844 → 161657`, and the magnified top-left corner is a clean arc with desktop
outside and blur inside. 1062 tests green.

### Next steps

1. Tint strength is one byte: `PaletteBackdrop.TintAlpha` (0xA6). Lower = more see-through.
2. No drop shadow — DWM does not draw one here. If it reads detached, a 1px `Line` hairline on the
   Shell is the cheap fix (what the Win11 flyouts do); a real shadow needs a bigger window and is not
   worth it.
3. Light theme's tint is unverified.

## The glass corners showed two arcs (2026-08-28, released v1.2.1)

Isaiah, on the v1.2.0 palette: "the corners look bad." Magnified pixel capture of the live window
confirmed two nested corner arcs: DWM clips the window at its own DWMWCP_ROUND radius (8 DIP) while
the Shell border painted a 14px radius inside it. That mismatch existed before the acrylic work but
was invisible — the opaque window background painted out to the window edge, so only DWM's 8px
silhouette ever showed. Making the background transparent for the backdrop exposed both at once,
plus a bright halo where raw backdrop sat in the gap between the arcs.

There is no API to widen DWM's radius (only AllowsTransparency or SetWindowRgn, both of which
forfeit the backdrop), so the content matches the clip instead: `Shell.CornerRadius` is now 8, which
is also what the Windows 11 flyouts use. `MainWindow.ShellCornerRadius` holds the value for the
fullscreen-exit restore, and a test parses MainWindow.xaml and asserts the literal still matches the
constant — the two live in different files and would otherwise drift straight back into this bug.

1045 tests green. Verified against the running app by capturing the palette's corner pixels before
and after (DPI-aware capture; the palette opens on the cursor's monitor, so pin the cursor to the
primary screen first or the grab comes back black).

## Audit implemented end to end (2026-08-28, 19 commits, released v1.2.0)

Isaiah: "Start fixing/implementing... test as if you're trying to break it... don't stop until
it's done." A 10-agent workflow implemented every finding from `.claudehelper/AUDIT-2026-08-28.md`
(see the section below for the audit itself). Test suite grew 887 → **1044**, all green in Release.
Landed, in order: store hardening (atomic saves, corrupt-history quarantine + sidecar rebuild,
mutation lock, token-AND multi-word search) · updater WebView2 kill scoped to Clip's own children +
relaunch-on-failure · password-manager exclusion formats honored in both capture paths (shared
helper in Clip.Core) + watcher yields capture to the shell · Esc/Del hotkey aliases + selection
follows search + copy toast + hotkey-failure notification + footer hints + empty states · full
keyboard nav (arrows/Page/Home/End), Ctrl+1..9 quick paste, action-menu keyboard support, delete
undo (Ctrl+Z), pause-capture toggle · **crisp text (TextFormattingMode=Display), PerMonitorV2 DPI
manifest, acrylic glass backdrop (DWMWA_SYSTEMBACKDROP_TYPE=3, settings toggle, opaque fallback)**,
constant selection border (no jiggle), rasterized search icon · perf: LRU cache eviction,
off-thread row thumbnails, per-keystroke list reuse, conceal-time row/preview reclamation · two
adversarial agents added edge-case tests and fixed real bugs they exposed (WebView2 rejects
semi-transparent DefaultBackgroundColor — flattened at ToDrawingColor; unicode install paths;
same-ms quarantine collisions; corrupt settings no longer silently wipe ExcludedApps).

Shipped: published 1.2.0 locally, installed over `%APPDATA%\Programs\Clip`, restarted via
"Clip Autostart" task, smoke-verified the palette opens/renders (screenshot), pushed to main,
tagged v1.2.0 (release.yml builds installer + zips on tag).

### Next steps

1. Isaiah eyeballs the things only a human can judge: acrylic glass look (Settings has a
   "Translucent background" toggle), text crispness in Display mode, per-monitor DPI on the
   second monitor, selection highlight (no jiggle), keyboard-nav feel, empty states.
2. Not done, deliberately: bundling Inter/JetBrains Mono font files (needs a download decision);
   font stacks still name them and fall back to Segoe UI Variable / Cascadia on machines without.

## Full improvement audit + asyar rendering audit (2026-08-28, no code changes)

Isaiah asked for an audit-only pass: find improvements (styling, usability, features, stability,
performance) and have an agent audit https://github.com/Xoshbin/asyar to explain why it renders
crisper and how it does its translucent background. Six parallel auditors ran; full ranked report
with file:line evidence is in `.claudehelper/AUDIT-2026-08-28.md`.

Headlines: Esc is provably dead (default "Esc" gesture never parses — needs "Escape" alias in
`TryKey`); there is no arrow-key list navigation at all; Enter can paste a stale item filtered out
by search; capture ignores the password-manager clipboard exclusion formats (privacy hole, ~10
lines to fix in both capture paths); the crispness gap vs asyar/Raycast is WPF's
`Ideal`+`Grayscale` text mode (switch to `Display` at window level) plus missing PerMonitorV2 DPI
manifest; history.json writes are non-atomic with no corruption recovery and racing writers; the
updater force-kills every WebView2 process on the machine. asyar = Tauri 2 + Svelte in the OS webview,
transparency via the `window-vibrancy` crate (HudWindow material on macOS, acrylic→mica on Windows).

**CORRECTION (2026-08-31), and this one matters because it was stated here as a rule.** The audit's
"acrylic via `DWMWA_SYSTEMBACKDROP_TYPE`, never use `AllowsTransparency`" advice was wrong on both
halves, and following it is what shipped the 1.2.0-1.2.3 look Isaiah rejected as "not glass, just
light grey". Measured on his machine (26200, 150% DPI) against a RED|BLUE edge behind the window:
DWMSBT_TRANSIENTWINDOW returns hr=0 and paints a flat `D3D3D3` over the whole window, sampling
nothing behind it — it is inert here. The thing that actually blurs is
`SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND` on a **layered** window, which
is what `window-vibrancy`'s `apply_acrylic` — and therefore asyar — has been calling all along. The
ClearType objection is void for this window specifically: it sets
`TextOptions.TextRenderingMode="Grayscale"` deliberately, so there is no subpixel rendering to lose.
See `src/Clip.Shell/PaletteBackdrop.cs` for the scanline numbers.

### Next steps

1. Isaiah picks what to act on from `AUDIT-2026-08-28.md`. Suggested first batch (all small):
   Esc alias, selection-follows-search, `Display` text mode, PMv2 manifest, exclusion formats,
   atomic saves + corruption quarantine, multi-word search, copy toast, selection-border jiggle.
2. The acrylic look: backdrop Option A + brush alpha in `ApplyTheme` + bundle Inter — after the
   text/DPI fixes so glass and crispness are judged together.

## Split-pill filters always show their rectangle (2026-08-28, commit 87631f1, released v1.1.14)

Isaiah wanted the button+dropdown filter pills (All, Media, File) to always show the full
rectangle outline, not just when selected — unselected they collapsed to a bare divider.
One change in `SetFilterVisual` (MainWindow.xaml.cs): unselected shells now get a `Line2`
border instead of transparent; selected look unchanged (Selected fill + SelectedBorder).
XAML defaults for `MediaFilterShell`/`FilesFilterShell` updated to match so there's no
flash of borderless pills before the first `UpdateFilterVisuals` pass.

887 tests pass. Published locally as 1.1.14+87631f1, installed over `%APPDATA%\Programs\Clip`,
restarted via "Clip Autostart" task. Pushed to `main`. Released as v1.1.14 (tag on 87631f1,
CI green, installer + both zips attached, marked Latest).

### Next steps

1. Isaiah verifies: open Clip — all three dropdown pills (All, Media, File) show a full
   rectangle outline even when unselected; selected pill still gets the filled look.

## Clicking off the palette took two clicks (2026-08-18, commit f4e95c0, released v1.1.10)

Isaiah: "clicking off of Clip does not dismiss it on the first try." Outside-click dismissal
was a `DispatcherTimer` ticking every 50ms into `HideIfMousePressedOutsidePalette`, which read
`Forms.Control.MouseButtons` — a snapshot of what is *physically held right now*. A normal
click is held for less than a tick on a fast hand, and the timer runs at background priority so
it slips much further whenever the palette is building a preview. The press simply fell between
ticks; the second click happened to land on one.

Fix: a `WH_MOUSE_LL` low-level mouse hook, installed in `StartOutsideClickWatch` when the
palette opens and removed in `StopOutsideClickWatch` from `ConcealPalette`. The hook sees every
button-down regardless of duration. The hook proc itself does nothing but a message-id check and
a `Dispatcher.BeginInvoke` — a low-level hook runs on every mouse message for the whole desktop,
so any real work there stutters the pointer everywhere. The old poll remains as a fallback if
`SetWindowsHookEx` ever fails.

Note the palette still deliberately ignores `Deactivated` (RDP/RustDesk can't hold foreground —
see the comment in `OnDeactivated`). The hook is what makes outside-click work without it.

Also committed the XAML `TextSelection` brush (#FF6363) that had been left uncommitted — the
runtime `SetBrush` pass already painted it, so this is the "edit both" half that was missing.

## CI flake: teardown file locks (2026-08-18, commit cee9994)

A release-gating CI run failed on `Dispose` deleting the temp folder while a sidecar json was
still open — Windows holds a handle briefly after the last writer closes (Defender/indexer).
All 36 teardowns now go through `tests/Clip.Tests/TestTemp.cs`, which retries 5x/50ms then gives
up quietly. Nothing to do with the product; it was just failing tests that had passed.

## The account rename left the updater pointing at a name we no longer own (2026-08-18, released v1.1.11)

The GitHub account was renamed IsaiahCalvo -> Kal-Voe. GitHub keeps a redirect from the old name,
so everything kept working and nothing looked broken — but that redirect only holds while nobody
else registers the old name. `ClipUpdateService.DefaultLatestReleaseUrl` was hardcoded to
`api.github.com/repos/IsaiahCalvo/Clip/releases/latest`, so if someone claimed that name and made
a repo called Clip, every installed copy would start reading their releases and offering their
installer as an update. Repointed the updater, the installer homepage and the README download
links at Kal-Voe, and shipped it as v1.1.11 so installed copies stop relying on the redirect.

## Next steps

- Verify in real use that one click outside now dismisses the palette (nothing automated covers
  this — it needs a real desktop click, and the offscreen test mode short-circuits the handler).
- v1.1.11 is live on GitHub (Kal-Voe/Clip) with installer + both zips. The local csproj default
  is bumped to 1.1.12 so a local build still outranks it.
- The git remote was redirecting; `origin` now points at `https://github.com/Kal-Voe/Clip.git`.

## Pasting a big photo killed Clip outright (2026-08-18, commit 1ea8427)

Isaiah: "tried pasting a big photo, tried to paste in Visio, it's frozen and I can't dismiss
it." Not a hang — the process was already dead. **There was nothing in ShellLog because
`Environment.FailFast` leaves nothing.** The stack only exists in the Windows Application
event log (Ids 1000 / 1001 / 1025, `Get-WinEvent -FilterHashtable @{LogName='Application'}`):

    Clip.exe 1.1.10 / PresentationCore.dll / exception 0x80131623
    System.Environment.FailFast("Unrecoverable system error.")
      System.Windows.DataObject.GetDataIntoOleStructsByTypeMedimHGlobal
      MS.Win32.UnsafeNativeMethods.OleFlushClipboard
      Clip.Shell.MainWindow.SetClipboard

Root cause: WPF's `Clipboard.SetImage` (and `SetFileDropList`) go through `DataObject` with
`copy: true`, which renders every format into an HGLOBAL and answers **any** failure with
`Environment.FailFast`. That is uncatchable — no exception, no dialog, no log, just a stale
window on screen that reads as frozen.

This is the *same* landmine that killed Clip on 2026-08-08. That fix (`Win32ClipboardWriter`)
only rerouted the **text** branch of `SetClipboard`; Image and Files were left on WPF.

Fix: extend `Win32ClipboardWriter` with `TrySetImage` (CF_DIBV5 + CF_DIB, both built here as
finished byte buffers, bottom-up, with a 512 MB pixel ceiling) and `TrySetFileDrop`
(CF_HDROP), and route both branches of `MainWindow.SetClipboard` through it. Failure is now a
`false` return → `ShellLog.Snapshot` + a toast ("That image is too big for the clipboard").
881 tests pass (4 new: DIB header/flip, DIBV5 masks/intent, HDROP layout, size refusal).
Published, installed, running.

**Verify:** Alt+V, pick the big photo, Enter — it pastes, or you get a toast. Either way Clip
is still alive afterwards. Note `Clipboard.SetDataObject(copy: false)` is *not* a fix for
images: the delayed-render path lands in the same `GetDataIntoOleStructs` FailFast when the
consumer asks for the bitmap.

**Note:** the working tree still carries an uncommitted experiment in `MainWindow.xaml` —
`TextSelection` brush `#414141` → `#FF6363` with `SelectionOpacity` `0.7` → `0.4`. It shipped
into the installed build with this publish. Keep or revert deliberately.

## Inline modals clipped to the search row (2026-08-18, commit 8228b8f)

Isaiah: "hit Edit and the text editor doesn't open properly." Log showed
`edit-text rendered hosted=True` firing repeatedly seconds apart — he kept pressing Edit
because nothing usable appeared. Root cause: the Edit Text / Rename / Open With inline
overlays are added to `RootGrid` (3 rows: 53px search, star list, 34px footer) with **no
`Grid.SetRowSpan`**, so they arrange into row 0 — the 53px search strip — and the overlay's
`ClipToBounds` slices the 420px modal to a sliver. The hosted settings overlay already did
it right (`SetRowSpan`). These overlays were born broken in the Command Palette parity
build-out (f0c4639); before that, the fallback `TextEditWindow` dialog path ran instead.

Fix: `Grid.SetRowSpan(overlay, 3)` at all three insertion sites (MainWindow.xaml.cs, next
to each `SetZIndex(overlay, 900)`). 877 tests pass, published, installed (Clip.dll
hash-verified), restarted via "Clip Autostart".

**Verify:** Alt+V, pick a text item, hit Edit — full 640x420 editor centered over a dimmed
palette. Same for Rename and Open With.

## Instant open via DWM cloaking (2026-08-18, commit 4cc4e69)

Isaiah: hotkey open shows "the frame of the app, then it hydrates" — wants Raycast-instant.
Root cause: `ConcealPalette()` called `Hide()`, which drops the WPF D3D surface, so every
re-show presented the bare HWND first and painted rows/preview after. The existing
`Opacity=0` pre-render trick was a no-op because the window is `AllowsTransparency=False`
(WPF ignores Window.Opacity without a layered window).

Fix (MainWindow.xaml.cs): conceal now **DWM-cloaks** the window (`DWMWA_CLOAK`, attr 13 —
`DwmSetWindowAttribute` was already imported). The window keeps WS_VISIBLE so WPF keeps its
surface alive and rendered; the compositor just stops presenting it. `ShowPalette` does
position/rows/layout while cloaked, then uncloaks at `DispatcherPriority.Loaded` (right
after the render pass) so the first presented frame is the finished one. Details:
- Focus: `Hide()` used to hand activation back as a side effect; conceal now calls
  `RestoreReturnFocus()` explicitly, only when the palette is the foreground window.
- `Hide()` kept as fallback if the cloak call fails (also covers the old secondary-monitor
  stale-black-surface DWM glitch note, which the cloak path makes moot).
- Window still moves off-screen while cloaked (cloak removes pixels, not the HWND — belt
  against an invisible topmost window swallowing hit-tests).
- `BenchWindowShown` now requires `!_windowCloaked`; WebView2 idle release keys off
  `_paletteOpen` (a cloaked window stays `IsVisible`).
- Deferred uncloak is guarded by `_paletteOpen` (fast-escape race).

Verified off-screen (`--open-bench --runs 5 --stages`): 5/5 clean, warm opens laid out by
~25ms, cond-shown median 78ms including the post-render reveal. 877 tests pass. Pushed,
installed (Clip.dll hash-verified), restarted via the "Clip Autostart" task.

**Verify:** Alt+V repeatedly — the palette should appear as one finished frame (no empty
frame, no list filling in after). Escape must return focus to the previous app.

## Split-pill unification + accent glow retired (2026-08-17, commit faf44f3)

Isaiah flagged two things: the Media split pill missed the border/fill that wraps All and
File when active (root cause: All/File had hand-attached shell MouseEnter glow handlers that
Media never got), and the app-wide blue "glow" looked immature. Root cause of the glow:
`ApplySystemAccentBrushes` poured the full-strength Windows accent into
`Selected`/`SelectedBorder`/`AccentSoft`, so every selected row, hover fill, and 1px border
in the app was accent-colored. Design reference he supplied: OriginUI's button-with-dropdown
(21st.dev) — one rounded container, flat halves, 1px seam, neutral fills.

Fix: shell `Border` now carries the whole selected look for every split pill (halves stay
transparent, painted only by `SetFilterVisual` — they cannot drift apart);
Selected/SelectedBorder/AccentSoft are now fixed neutrals in both themes; the Windows accent
survives only in `Accent` (search focus ring, toggles), `TextCursor`, and a new
`TextSelection` brush that all four text editors use (a neutral selection highlight would
have been invisible). Dead `FilterChevronButton` style deleted. Net −53 lines.

Verified off-screen by rendering the real MainWindow.xaml resources through a scratch
RenderTargetBitmap harness (no window shown): all three split pills identical per state,
dark + light. 877 tests pass. Pushed, installed (hash-verified all four exes), restarted.

**Verify:** Alt+V — active filter pill is neutral gray with a single border around BOTH the
label and the chevron, identical for All/Media/File; selected list row no longer has the
blue ring; text selection in previews/editors is still clearly visible.

### Follow-up (commit 1a59fc2): Raycast palette adopted. He asked to tone the focus ring and
cyan caret too and "match Raycast and their color palette". Accent + TextCursor are now
fixed Raycast red (#FF6363 dark / #D64545 light); ALL Windows-accent plumbing is deleted
(GetWindowsAccentColor/BlendColors/SetBrushColor/ApplySystemAccentBrushes) — the palette no
longer follows the system accent at all. Search box has NO focus ring anymore (border always
Line2; OnSearchFocusChanged deleted) since the palette autofocuses its only text field.
TextSelection is plain neutral (#414141/#C8C8C8). Red now appears in: caret, title hover,
settings toggles ON, active option labels. 877 tests pass, off-screen render verified,
pushed, installed (hash-verified), restarted.

### Follow-up (commit a28841d): settings UI audit. He flagged the app-icon picker clipping
(30px host for 32px of content) and off-center hotkey text (Padding 10,5,10,0). A sub-agent
audit of all five procedural windows found 16 defects; 14 fixed: dotted focus rectangles
killed app-wide (App.xaml implicit Button/TextBox styles, FocusVisualStyle=null with
BasedOn); theme change now REBUILDS the settings page (RefreshTheme(rebuildPage:true))
instead of the blanket repaint walker that erased state colors and missed popups — walker
deleted; OS highlight blue removed from all three app-picker ListBoxes via shared
MainWindow.PaletteListItemStyle; clear-history segment 1px overpaint, update-button clip,
hotkey error text clip ("Needs modifier"/"Invalid"), hotkey-help chip centering,
ActionOverDetailRow label alignment, app-icon hover on inactive button, sticky dropdown
hover outline, asymmetric search margins. Deliberately skipped: white toggle knob
(intentional convention), icon-warm hex literals. 877 tests pass, pushed, installed,
restarted.

**Next steps:** broader UI pass continues if he flags more. No release cut yet — hold until
the UI pass settles, then ship one release.

## Smaller window + info panel right-margin fix (2026-08-17, commit 2813af0)

Isaiah compared Clip to Raycast: window felt big, and the INFORMATION section had a visibly
bigger right margin than left. Root cause of the margin: any item with enough info rows to
scroll (every file item — 9 rows in a 132px viewport) made the ScrollViewer reserve a **17px
scrollbar gutter that rendered invisibly** (the window's implicit ThinScrollBar style never
reached inside the ScrollViewer template, so the default-width bar was reserved but drawn
transparent). Fix: `OverlayScrollViewer` template on `InfoScroll` — the 6px thin bar now
floats in the 24px outer margin (negative right margin) and content keeps full width, 24/24
symmetric. Window shrunk 880x560 → 800x520 (Raycast is ~750x475); the hosted settings
overlay (fixed 720x500) still fits, verified by film. 877 tests pass, `--open-test` clean,
built, pushed, **installed and restarted**.

His right-click-menu comparison was checked too: Clip's context menu already covers
everything Raycast's does (paste/copy/plain-paste/rename/pin+reorder/edit/append/open/
open-with/explorer/copy-path/share/save-as/delete). No change made there.

**Verify:** Alt+V — window is smaller; select a copied *file* and look at the INFORMATION
section: values reach the same margin on the right as labels do on the left, thin scrollbar
floats outside the text when it scrolls.

### Follow-up same day (commit b81f323): Isaiah rejected the always-visible thumb ("thick as
hell"). It now starts at opacity 0, fades in only on a real user scroll (offset change with
zero extent change, so selecting another item never flashes it), and fades out 800ms after
scrolling stops — `OnInfoScrollChanged` in MainWindow.xaml.cs. Not hit-testable. Installed
and restarted.

### Second follow-up (commit b47e5b0): he then said to open the real Raycast and copy it, so
that's what happened — with his screen-control permission, Raycast's clipboard window was
opened on screen (its window hides at layered alpha 0; the computer-use `key` tool could not
reach it, but `SendInput` Alt+Shift+V from PowerShell works — the repo's Measure-VsRaycast
trick). Measured: ~4px rounded pill hugging the panel edge, no track, visible only while
scrolling, fully gone ~1.5s after. Clip's bar is now a 4px full-bleed pill (own template, no
inner thumb margin, CornerRadius 2) tucked 5px from the window edge, same fade envelope, and
`RenderInfo` now calls `InfoScroll.ScrollToTop()` so the info section never opens mid-scroll.
Verified live on screen against the real Raycast. Installed and restarted.

### Third follow-up (commit 48528ee): he called the 4px bar an ugly block vs Raycast's and
told Claude off for staying on his screen. One full-res `CopyFromScreen` capture of Raycast's
bar mid-scroll was pixel-scanned — 6px wide, core 113,113,118 (#717176), soft anti-aliased
pill — and everything after was done off-screen. Root cause of the ugliness: the window's
`UseLayoutRounding` snapped the small pill to hard whole-pixel edges; the bar template now
opts out of snapping/rounding, uses #717176 at width 6 / radius 3, and an offscreen
RenderTargetBitmap reproduces Raycast's exact core pixels. Installed and restarted.
**Rule reaffirmed: stay off his screen; one capture, then iterate off-screen.**

### Fourth follow-up (commit c7680b6) — THE ACTUAL ROOT CAUSE. He said it "still looks the
same big blocky thing" on the newest build (verified by hash he was running it). The blocky
bar was never the overlay pill — it was the **stock 17px Windows scrollbar in the text
preview**: window-level implicit ScrollBar styles and the per-dialog `Resources.Add` copies
NEVER reach scrollbars inside control templates (TextBox's PART_ContentHost above all), so
Clip's "thin scrollbar" styling had silently never applied anywhere templated. Fix: Raycast
pill as the app-level implicit style in App.xaml (app-level DOES penetrate templates);
TextPreview got an explicit TextBox template whose PART_ContentHost uses
`OverlayScrollViewerInset` (bar inside the pane edge — the outer-margin variant would be
clipped by the preview Border's ClipToBounds) with the same fade-on-scroll via the shared
`OverlayBarFader`; deleted the dead window ThinScrollBar style, the codegen
`ThinScrollBarStyle`, and its three dialog injections. 877 tests, `--open-test` clean, text
preview film-verified unchanged, installed (hash-verified) and restarted.

### Fifth follow-up (commit a41012b) — the 17px mechanism, finally. "Still there" after the
app-level style because the style's Width setter never stood a chance: the stock
ScrollViewer template sets the bar's width from SystemParameters scrollbar metrics as a
LOCAL value, and local beats style — so the restyled thumb just stretched into a 17px gray
block (that WAS the "big blocky thing", including in settings/dialogs where c7680b6's pill
style made it a filled gray block). Fix: `VerticalScrollBarWidthKey` /
`HorizontalScrollBarHeightKey` overridden to 6 in App.xaml. Proven with an offscreen
live-window control test (real Window at Left=-9000 + ContentRendered + RTB + pixel scan —
note a detached RTB render never draws scrollbars, false negative): before the override the
test rendered a 17px block, after it a 6px #717176 pill. Also injected ::-webkit-scrollbar
pill CSS into every WebView2 preview page (c7680b6/a41012b). Installed (hash-verified,
process 3:43 PM) and restarted.

## Second death + instrumentation (2026-08-18 ~2:20 PM)

Clip died AGAIN within ~13 minutes of the 2:02 PM restart. CORRECTION to the forensics
below: "no app-exit log line" proved nothing — ShellLog drops all Info lines unless trace is
on (`--debug-log` / `CLIP_SHELL_TRACE=1`); only Error/Snapshot lines always write. So
neither death is actually diagnosed yet. What IS ruled out: reboot/logoff (none), WER crash
(none), and palette-session self-exit (only applies with `--palette-session`/harness flags,
never to resident launches). SentinelOne is confirmed present (`C:\ProgramData\Sentinel`)
and remains suspect #1.

Instrumentation now armed for the next death:
- `setx CLIP_SHELL_TRACE 1` (user env) — every future Clip instance writes full logs.
- `%LOCALAPPDATA%\Clip\death-watch.ps1` runs hidden (mutex "Clip-death-watch", survives via
  nothing — relaunch manually after reboot if needed) and appends exact death timestamps to
  `%LOCALAPPDATA%\Clip\death-watch.log`.
- Watchdog verified END-TO-END live: fire-while-running = silent no-op (count stayed 1, no
  palette pop); kill-then-fire = revived in seconds. Worst case Clip is down 30 minutes.

Next session: read death-watch.log + shell.log tail around the death time. If deaths line
up with SentinelOne activity, the fix is an EDR exclusion or code-signing, not more code.

## Silent death + watchdog (2026-08-18, commit 285fda9)

Isaiah found Clip not running on 8/18. Forensics: no reboot (up since 8/13), no logoff, no
WER crash report, and no "app exit" line in shell.log (a clean exit always writes one, then
flushes) — so the process was hard-killed externally sometime after 5:34 PM on 8/17. Prime
suspect is SentinelOne/Capture Client, which this machine's memory shows killing unsigned
binaries without a trace (see the Codex diagnosis). The "Clip Autostart" logon task existed
and was healthy — it just never fired because there was no logon.

Fix: the task now has a second trigger repeating every 30 minutes, and its action passes
`--ensure-running`. In App.xaml.cs the duplicate-instance path exits BEFORE
TrySignalRichPalette when that flag is present, so a watchdog fire while Clip is up is a
silent no-op (verified live: duplicate exited, process count stayed 1, no "palette shown"
logged). Install-ClipStartup.ps1 creates both triggers + the flag for future installs.

Durable fix if the EDR keeps killing it: sign the binary or add an exclusion for
`%APPDATA%\Programs\Clip\Clip.exe` in the Capture Client console (needs IT/console access).
Note the duplicate instance cannot write shell.log (the main instance holds it), so
ensure-running fires leave no log line — absence of log there is normal.

**Litter noticed, not touched:** his real history has two dead pinned items from the 8/5
bench-fixture leak — `sample-video.mp4` / `sample-audio.wav` pointing into a deleted session
scratchpad. Every palette open logs a "file preview failed" for the selected one. They are
his (pinned) data, so they were left alone — unpinning/deleting them from the palette would
stop the noise.

## Paste crash fixed (2026-08-08, commit dacfb69)

Clicking a row to paste killed the whole app: `Clipboard.SetDataObject(copy: true)`
re-renders every format inside `OleFlushClipboard`, and WPF answers any failure there
with `Environment.FailFast` — uncatchable, process gone (Event Log 11:34, v1.1.10;
stack: `GetDataIntoOleStructsByTypeMedimHGlobal` → `FailFast`). The text/html/rtf
branch of `SetClipboard` now renders the bytes itself and hands Windows finished
HGLOBALs via `Clip.Core/Win32ClipboardWriter` (CF_UNICODETEXT + "HTML Format" UTF-8 +
"Rich Text Format" ANSI, owner hwnd passed so source attribution still sees Clip).
If the Win32 write fails after retries it falls back to WPF with `copy: false`, which
skips the flush. 877 tests pass (3 new for the byte encoders — deliberately not
touching the real clipboard). Built, pushed, installed, Clip restarted.

**Verify:** copy something formatted from Word/browser, paste it back out of Clip a
few times — the 1.1.10 build died on exactly that.

### Next steps
- Image and file paste still go through WPF (`SetImage`/`SetFileDropList`, which also
  flush). Same FailFast is theoretically reachable there; extend Win32ClipboardWriter
  if it ever fires.

## Raycast comparison research (2026-08-07, no code changes)

Compared Clip against Raycast's Clipboard History (macOS + Windows beta). Clip is ahead on
previews, paste verification/per-app overrides, CLI, and licensing. Gaps worth considering,
roughly by value:

1. **Respect Windows clipboard-exclusion formats** — password managers (1Password, Bitwarden,
   KeePass) set `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory`
   on the clipboard. Clip ignores these and records the secret unless the app is manually
   excluded. Raycast honors the macOS equivalent by default. Small change in the capture path,
   big privacy win.
2. Time-based retention option (Clip is count-only, 500) + bulk delete-by-time-window.
3. Fuzzy search (current search is case-insensitive substring across preview/text/OCR/paths).
4. Sequential paste (select several, paste in order).
5. Pause-capture / incognito toggle.
6. Code signing (already planned).

### Next steps
- If Isaiah wants any of the above, start with item 1 — it's the only real privacy hole.

## Five visual defects fixed (2026-08-07, commit dea8350)

All five reported 2026-08-06, all root-caused, fixed in one commit, 874 tests pass, `--open-test`
clean, installed and restarted:

- **Video bled through settings.** WebView2 is an HWND with its own airspace — the ZIndex-1000
  settings overlay never covered it. The browser pane is now collapsed (and its media paused) when
  hosted settings opens and restored on close. The reveal path (`RevealWhenLoadedAsync`) also
  refuses to show the browser while `_settingsOverlay` is up, because a theme change inside
  settings re-renders the preview and would have re-revealed it over the panel.
- **Settings corners missing their border.** A WPF Border does not clip its child to its rounded
  corners, so the square header/body painted over the 1px border along the curves. The content
  grid now carries a static rounded clip (panel is a fixed 720x500 everywhere).
- **Theme switch swapped list icons for "old" generic glyphs until restart.**
  `RefreshClipboardManagerIcons` re-rendered every row with `preferRichPreview: false` — the flat
  vector fallback — while rows are built with rich icons. Now refreshes with the same rich icons.
  Also `RenderFileSvg`'s raster cache was keyed without the theme color, so a switch kept serving
  the old theme's rendering; the key now includes `BrushHex("Muted2")`.
- **Double divider in the context menu for non-text items.** The base section ends with a
  separator and the Image/Files section began with one; when nothing lands between them they
  stacked. `ShowStyledMenu` now collapses consecutive (and leading) separators — fixed at the
  render layer so every menu path is covered.
- **"File" filter pill clipped + blank row in its dropdown.** The filter row had a fixed
  `Width="352"` but its content is ~357px, clipping the File pill's right edge — width removed,
  the panel sizes itself. Extensionless files keyed the dropdown on `""`, rendering a blank row
  (it sorted between Text and CSV); they now key as `other` / label "Other", and the filter
  round-trips because both the menu and `RenderItems` go through `FileKindKey`.

## What a user could see (2026-08-05, second pass)

Three complaints, all about what is on screen rather than what a stopwatch says:

- **The palette appeared empty and then filled in.** It became visible before the rows were loaded,
  so the search box, filters and preview painted with a blank list column beside them. Fixed by
  loading the rows *and calling `UpdateLayout`* before making the window opaque — adding rows to the
  tree is not the same as having them arranged, so without the layout pass a window shown in the
  same breath still paints an empty column for a frame. Costs ~12 ms; the first visible frame is now
  the finished window.
- **Images showed "Loading preview..." over a blown-up row thumbnail.** Preview decodes were never
  cached — `ShouldCacheBitmap` capped caching at 48 px row icons while previews decode at 900 px —
  so every look at an image re-read it from disk. Previews are now cached (12, kept apart from the
  256 row icons), a decoded picture is assigned on the spot, a new one decodes off-thread with the
  *previous* preview left up, and the images either side of the selection are decoded in the
  background. No placeholder for images at all.
- **Video/audio took over a second the first time.** The startup warm-up now selects the item the
  first open will land on, renders its preview and stands the browser up while the window is still
  hidden. First video preview 1308 → 346 ms, first code preview 1274 → 482 ms, reopens ~200–270 ms.

Verify this class of change by **looking**: `--film=<prefix>` photographs the window every ~16 ms
during an open, with the window's opacity in each filename (`RenderTargetBitmap` draws the tree
whatever the opacity is, so without that a frame captured while transparent looks like something the
user saw). `--dump-preview` does the same for the browser pane, which `--film` cannot capture.

Still open: the video controls auto-hide until hover by design (`.wrap.video-mode .bar`), which was
left alone — if they should be visible when the preview first appears, that is a one-line CSS change
in `MediaPreviewPage.cs`.

## Open latency (2026-08-05)

**Clip opens about twice as fast as Raycast. Cold open is 69–76% faster than it was; warm open is
6–29% faster.**

### Versus Raycast — measured

Pressing the real hotkey for both and watching each window appear, 12 runs each, interleaved,
against the **installed** build:

| | median | p95 |
|---|---|---|
| **Clip** | **100.5 ms** | 147.8 ms |
| Raycast | 108.5 ms | **126.8 ms** |

Neck and neck — Clip a little ahead on the median, a little behind on the tail. Clip is also doing
strictly more before it shows anything, since the window no longer appears until the list is in it.

**An earlier run here claimed 42 ms vs 87 ms. That was wrong** — the harness dismissed windows with
Escape, which never closed Clip's palette (a posted `WM_KEYDOWN` misses WPF's focused element, and a
real Escape goes to whatever has focus, never the palette while the lock screen is up). The palette
stayed open, so the next press toggled it *shut*, 2–3 runs in 10 recorded a miss, and the surviving
samples were skewed. Both apps are now dismissed with their own toggle hotkey, which is
focus-independent, and 12 of 12 runs register.

Identical treatment, and deliberately generous to Raycast: its window is stamped the moment it
flips from layered alpha 0 to 255 — when it *decides* to show — while Clip must additionally have
painted. Clip's own harness is stricter again, waiting for the search box to take focus, and still
lands around 94 ms warm. Clip wins on either reading.

```bash
pwsh -File tools/Measure-VsRaycast.ps1 -Runs 12
```

**The locked session did not block this after all.** Last night's writeup said key injection cannot
work with the lock screen up. Measured, it does: `SendInput` reports 4/4 and Clip's palette opens in
under 100 ms. The script now presses the key once and checks that something happened, rather than
inferring from `LogonUI` — assumption replaced with a probe.

Two harness bugs had to be fixed before the numbers meant anything, both worth remembering: both
hotkeys **toggle**, so pressing while the window is still up closes it and records nothing; and the
first fix for that re-sent Escape in a tight loop, posting thousands of messages a second into both
applications and moving the median from 40 ms to 92 ms on its own.

### The numbers

Five cold and ten warm samples per page, off screen, against a frozen fixture history of 146 items.
Cold is the first open in a fresh process; warm is a reopen with the list marked stale as though a
clip had arrived, which is why the palette is usually being opened at all.

| page | cold median | cold p95 | warm median | preview ready |
|---|---|---|---|---|
| palette | 518 → **163** | 534 → **174** | 100 → **100** | — |
| preview-text | 567 → **172** | 877 → **177** | 121 → **102** | 217 → **194** |
| preview-image | 583 → **160** | 619 → **172** | 112 → **96** | 397 → 354 |
| preview-code | 608 → **159** | 724 → **182** | 190 → **153** | 1028 → **176** |
| preview-html | 555 → **164** | 571 → **190** | 173 → **168** | 678 → **207** |
| preview-textfile | 533 → **159** | 688 → **166** | 117 → **103** | — |
| settings | 541 → **183** | 624 → **205** | 117 → **120** | 276 → **304** |

The preview column is the headline of the second pass: a code preview appears in 176 ms instead of
just over a second. Later rounds were taken on a busier machine than the baseline, so cold and warm
drifted a little; treat differences under ~20 ms as noise and see the warning below.

All milliseconds. Raw samples are in `.claudehelper/perf/{baseline,round1,round2,round3}.json`.

The p95 column is the bigger story than the median: preview-text's worst case went from 877ms to
165ms. Cold opens are now boringly consistent (145–180ms across every page and every sample),
where before they ranged 484–929ms.

### What "open" means here

The clock starts when the hotkey message arrives and stops when **a keystroke would be handled and
find the window usable**: shown and opaque, search box focused, a screenful of rows present. Rows
past the fold are deliberately not waited for — nobody can see them and deferring them is correct.

Readiness is checked at **Input** dispatcher priority, because that is where a key press sits in the
queue. This detail is worth about 60ms: checking at Background (the obvious first choice) waits
behind the deferred rows and the preview and reports the window as unusable while it would happily
accept typing.

### What got faster, and why

1. **Rows no longer render in one uninterruptible cascade.** Adding rows re-lays out, layout raises
   `ScrollChanged`, and the handler appended the next batch straight from that event. Because layout
   runs at Render priority — above Input — the whole 146-row list rendered without yielding, and the
   search box did not get focus until it finished: 377ms into a cold open. Batches now go back
   through the dispatcher, which is what batching was for.
2. **The list is rendered while the window is still hidden at startup.** The startup pre-render built
   the frame but left the list empty, so the first Alt+V paid for the query and for building every
   visible row. Rows are expensive on a cold process — the first file row measured **193ms** and the
   first image row **49ms**, because a row resolves its icon by asking the shell for the file type's
   icon or by decoding the picture itself. That was most of a cold open. This is the single biggest
   win.
3. **A preview already on screen is not rendered again.** Reopening re-rendered the selected item's
   preview unconditionally, on the stated grounds that concealing tears the WebView2 down — it does
   not, it starts a three-minute idle timer. So a reopen navigated to the page it was already
   showing. Reusing it takes a code preview from ~900–1240 ms to ~180 ms and an HTML one from
   ~480–1180 ms to ~135–205 ms, makes the open itself ~12 ms quicker (the navigation is no longer
   competing for the UI thread), and stops discarding a video's position on every reopen. Guarded on
   same item + browser alive *and visible* + the file's path/size/mtime unchanged, and cleared on
   theme switch since the generated pages bake the colours in.
4. **The first TextBox focus is paid at startup.** Focusing a TextBox the first time costs ~100ms
   while WPF brings up the text services behind it. It now happens during the one moment at startup
   when the window is really shown (off screen, about to be concealed). It has to be there: focusing
   a control in a *hidden* window returns immediately and initialises nothing — an earlier attempt
   measured `focusMs=0` and changed nothing.

### What did not get faster, and why

- **Warm open barely moved (100 → 94ms on the palette).** It never paid the costs above. What is
  left, measured: `Show()` itself ~19ms, ~39ms laying out the first screenful before the Input queue
  is reached, ~15ms in the focus call. The `Show()` cost is the deliberate `Hide()` in
  `ConcealPalette` — it exists to avoid a stale black surface (a DWM glitch), so it is not free to
  remove.

  **The ~39ms is reachable but needs eyes on it.** `ActivatePaletteWindow` is dispatched at Input
  priority, and layout runs at Render, which outranks it — so every open waits for the first
  screenful to lay out before the window is activated and the search box takes focus. Dispatching it
  above Render would cut roughly 39ms from both cold and warm. It was **not** done: activation calls
  `SetForegroundWindow`, and doing that before the first paint risks showing an unpainted window for
  a frame. That is a visual regression that cannot be judged from a number, and checking it means
  watching a real open on the real screen — which is not something to do while Isaiah is asleep or
  working. Worth trying with him watching.
- **Preview-ready for code and HTML is still 680–770ms.** The palette is interactive at ~145ms and
  the preview fills in after, so this is a separate problem from open latency. Partly diagnosed
  (`--open-bench --page=preview-code --runs=5 --stages` splits it):

  - **The first browser-backed preview in a process costs ~810ms creating the WebView2**
    (`code-view-created=221` → `code-webview-ready=1031` on run 0; instant on every run after).
    Pre-warming it at startup was **built, measured and rejected**: it does cut that first preview
    from 1210 ms to 457 ms, but an interleaved A/B (`tools/Compare-Variant.ps1`) measured it costing
    **+23 ms on every open**. A palette pays that dozens of times a day to save one wait once. The
    tax is in having the browser alive at all, not in when it starts, so the same applies from
    whenever the first browser-backed preview happens. Re-run the experiment any time — the tool is
    committed.
  - After that, ~70–543 ms **navigating**, plus time waiting to resume on a UI thread busy rendering
    rows. **An earlier claim in this file that the HTML build took 300–480 ms was wrong** — measured
    directly, building the page takes **1.5–21 ms** and the threadpool hand-off is **0 ms**. The
    apparent cost was the `await` waiting its turn back on the UI thread. So the lead is WebView2
    navigation and UI-thread contention, not the highlighter.
  - Chromium's occlusion throttling was suspected (the harness runs off screen) and **ruled out**:
    disabling it left preview timings unchanged and made the open worse, because an un-throttled
    browser competes for the UI thread.
  - Caveat on the number: the harness selects the item *after* the open, so a preview-page run
    renders two previews (the auto-selected first item, then the page's item). A user opening onto
    that item renders once. Treat `preview ready` as an upper bound.
- **Two attempts produced no gain and were reverted.** Dropping the first render batch to 8 to match
  the query limit *doubled* the open — date headers consume entries, so it built only 7 rows and the
  list waited for the next batch. Requesting a 1ms timer resolution to sharpen the poll did nothing,
  because the ~20ms it was aimed at is real dispatcher work, not timer granularity.

### Raycast — not measured, and why

The real goal was matching or beating Raycast, not the 50ms proxy. That number was **not obtained**:

- The session was locked all night. Synthetic keystrokes go to the input desktop, which while locked
  is the secure desktop, so neither app would have received them. Reporting silence as a slow
  application would have been worse than reporting nothing.
- Raycast's `raycast://` deeplink *does* work locked, but it costs **1438ms** end to end (384ms of
  that inside `ShellExecute` before the process is even reached). That is MSIX activation, not its
  hotkey path, so it is not a usable comparison — it would flatter Clip enormously.
- Its hotkey could not be driven by message instead. Raycast owns Alt+Shift+V through
  `RegisterHotKey` (confirmed: registering it returns `ERROR_HOTKEY_ALREADY_REGISTERED`), but
  posting `WM_HOTKEY` to every one of its windows and threads with ids 0–15, across both its
  processes, never opened it.

**Useful thing found while trying:** Raycast hides its window by keeping it mapped at layered
**alpha 0** and flipping it to 255 in one step, no fade. That is a precise, cheap, poll-able "it is
on screen now" signal, and it is what `Measure-VsRaycast.ps1` uses.

To take the measurement, on an unlocked session, keyboard free for ~30 seconds:

```bash
pwsh -File tools/Measure-VsRaycast.ps1 -Runs 10
```

It presses the real Alt+V and Alt+Shift+V and watches each window by its own hiding mechanism. The
comparison is deliberately generous to Raycast: its alpha flip is stamped when it *decides* to show,
while Clip must additionally have painted — and Clip's own harness is stricter still, waiting for
the search box to take focus. If Clip wins there, it wins while being judged more harshly.

### Re-running the harness

```bash
dotnet build Clip.sln -c Release
pwsh -File tools/New-BenchFixture.ps1              # once; -Force to rebuild
pwsh -File tools/Measure-OpenLatency.ps1 -Label whatever
```

**Look at the pane, do not just time it.** A blank preview is extremely fast, so any change that
skips work needs a picture, not a number:

```bash
Clip.exe --open-bench --page=preview-code --runs=3 --dump-preview=out.png
```

renders the pane straight from the browser (no display taken). It needs the occlusion flags — a
throttled browser has no frame to give, which is how a 0-byte PNG was produced — and must run before
the palette is concealed; both are handled when `--dump-preview` is passed.

Do not rebuild the fixture between an optimization and its re-measurement — that invalidates the
comparison. Everything runs off screen; nothing takes the display.

**This machine does not hold still, and it will lie to you.** Partway through the night OneDrive and
Adobe Desktop Service each took a whole core, and an *unchanged* build measured 60% slower than it
had an hour earlier — which was very nearly written up as a regression caused by a code change. The
round 4 numbers in `.claudehelper/perf/round4.json` were taken under that load and should not be
compared against round 3. For anything marginal use:

```bash
pwsh -File tools/Compare-Variant.ps1 -EnvVar SOME_FLAG -Page palette -Rounds 5
```

which alternates both arms A B A B within the same few minutes so whatever the machine is doing, it
does to both. Check `Get-Process | Sort CPU` before trusting any absolute number.

One test (of 874) failed once under that load and has passed every run since; it was not identified.
If it recurs, it is timing-sensitive rather than a real break.

### Two traps this work walked into

- **The fixture builder seeded 145 items into the real clipboard history** on its first run, because
  `CLIP_ROOT` silently did nothing: the shell, the watcher and the store each rebuilt
  `%LocalAppData%\Clip` themselves rather than asking `ClipStoragePaths`. All 145 were removed and
  Isaiah's 139 items were untouched, but the lesson stands: **prove a redirect seam before bulk
  writes**. The builder now writes one probe item and aborts if it lands in the wrong place.
  (`Environment.GetFolderPath(LocalApplicationData)` also ignores the `LOCALAPPDATA` env var — it
  asks the shell — so redirecting via the environment alone is impossible.)
- **Pre-warming the rows quietly broke selection.** With rows already present the first open skipped
  the reload, and the reload was what selected the first item — so the palette opened with nothing
  selected and a blank preview. The bench did not catch it (the palette page ignores selection);
  running the real shell through `--open-test` did. Fixed in b04e4e9. **Off-screen benchmarks measure
  time, not correctness — run `--open-test` and read the trace after any change to the open path.**

## Branches

The 2026-08-05 open-latency work was done on `perf/open-latency` and merged into `main`. Four
commits, each revertable on its own: the harness, the baseline, and one per optimization round.

There is one branch now. `main` was 59 commits behind while the real work sat on
`ui/grayscale-text-rendering`, which is why two worktree sessions picked bases and one picked
wrong. `main` was fast-forwarded onto that work, and `ui/grayscale-text-rendering`, both
`claude/*` fix branches and two long-dead `codex/*` branches were deleted after confirming each
had nothing `main` lacked. Cut new work from `main`.

## Where things stand

Picture-in-picture, video controls and audio controls are **done** and signed off.

- **Player stays in the browser.** A fully native mini window was built and **rejected** — it
  fixed the resize lag but lost the interface, and both decoders tried were worse than the
  browser's. Do not revisit without new information.
- **Resize jitter** is a 3–4px gap between frame and picture, flat across drag speeds. Accepted.
- **Word previews** open the cached PDF in the browser viewer — all pages, real text layer.
- **Office previews no longer close the user's PowerPoint.** COM hands PowerPoint callers the
  instance the user is already working in, and the preview used to hide it and `Quit()` it,
  discarding unsaved work with no prompt. Ownership is now decided at runtime from whether a
  process appeared, and `Visible`, `DisplayAlerts` and `Quit` all sit behind it.
- **The first Office preview after a reboot no longer times out.** One flat 25s budget had to
  cover both a cold COM start (measured 22.7–26.2s) and a warm export; it is now 120s cold /
  45s warm, with the flag set only where COM actually completed an export.
- **The Word flicker is fixed at its root** (commit d81f7f1, 2026-08-04, after a 30-agent audit
  of every preview path). `await Dispatcher.InvokeAsync(async ...)` completes at the lambda's
  FIRST await and discards the inner task, so `shown` in the pdf/office branches was always
  false — the raster fallback ran on top of every live viewer, and the orphaned reveal then
  resurrected a blank pane. `LoadFilePreviewAsync` is now flat (it already resumes on the UI
  thread), `RevealWhenLoadedAsync` only accepts its own navigation's completion (NavigationId +
  IsSuccess) and re-checks `_previewToken` before revealing, and the placeholder is swapped for
  the pane in one dispatcher frame. Every browser-backed preview (media included) goes through
  that reveal; the loading placeholder masks every transition. `BlankHtmlPreview` is gone — its
  blank navigation was what raced the reveals — replaced by a script that pauses `video`/`audio`
  when the pane hides or the palette conceals (also fixes Esc/pip leaving audio playing).
  Image decode, code highlighting and workbook HTML now build off the UI thread. Office exports
  are serialized per cache file, written to a temp name and moved into place, and a cache hit
  requires the PDF `%%EOF` / PNG `IEND` tail marker (verified against all 25 real cache files).
- **Excel and Visio ownership is settled by experiment** (2026-08-04). With a COM-launched
  `Visible=false` instance already running, a second Clip-style `CoCreateInstance` landed in a
  new process both times — Excel user=33940 clip=10588, Visio user=55680 clip=41544; independent
  repro Excel 38336→47592, Visio 43628→63216 — so both read `owned=True` and the user's instance
  is untouched. Caveat: verified against COM-launched instances (which reproduce the attach
  case), not an interactively launched one; the runtime gate in `CreateComApplication` covers
  any future attach case regardless.
- **874 tests pass on `main`** (2026-08-04). A second push took Clip.Core to 98.84% of
  hand-written lines (3850/3895) by adding small internal test seams (ClipStoragePaths.RootOverride
  AsyncLocal redirects the whole %LocalAppData%\Clip tree; registry roots, launch and
  powershell-query hooks) plus 95 more tests. The 45 remaining Core lines are each analyzed:
  dead guards Utf8JsonReader can't reach, success-only launch returns (would open real apps),
  COM/WinRT branches that can't be forced, and race-only catches. Two dead private store
  overloads and other provably unreachable code were deleted outright. Earlier the same day, a
  coverage push added 364 tests
  across 35 new `*CoverageTests.cs` files: overall lines 33.8% → 42.0%, Clip.Core 77.8% → 87.0%,
  Clip.Shell 9.6% → 16.3%, Clip.Watcher 17.8% → 31.3%. Every pure-logic class is at or near
  100%; what remains uncovered is live-UI (MainWindow's 9.9k lines, App, PiP, JankHarness),
  network fetches, registry writes, Office COM and live-clipboard paths — those can't run
  hermetically, and 100% overall is not attainable via unit tests without refactoring seams
  into the product code. Two real finds fell out: the update checker's release-name fallback
  never bound (missing `[JsonPropertyName("name")]`, fixed), and `PdfPage.Size` returns DIPs
  (1/96") not points, with `RenderToStreamAsync` scaling output by display DPI — so PNG output
  size is machine-dependent (behavior kept, comment corrected).

## Verify off screen — never take over the display

Isaiah works on this machine and has escalated about this repeatedly.

```
Clip.exe --jank-test --shot=out.png --audio --show=speeds --w=550 --h=230   # picture of the player
Clip.exe --jank-test --steps=30 --step-px=16                                # resize smoothness
Clip.exe --open-test                                # one cold + one warm open, read the trace
Clip.exe --open-bench --page=palette --runs=10      # N opens, every sample + stage breakdown
```

`--open-test` is the correctness check (does it select, does the preview render); `--open-bench`
is the timing one. Use `tools/Measure-OpenLatency.ps1` rather than `--open-bench` directly unless
you want the raw stages — the script handles cold-vs-warm and the median/p95.

For Office work, drive real instances over COM with `Visible = $false` — that reproduces the
attach case without putting a window on screen.

## Next steps

1. **RESOLVED 2026-08-04 — `.xls` now reads natively.** Isaiah asked for it despite zero `.xls`
   ever appearing in history, so `ExcelWorkbookReader` parses the old binary format through
   ExcelDataReader (new package, read-only, no Excel process) into the same grid and tab strip
   the zip formats get, with the COM export kept as the fallback for a file that will not parse.
   Verified against a real xlExcel8 fixture written by Excel itself
   (`tests/Clip.Tests/Fixtures/legacy.xls` — dates, booleans, codepage text, two sheets).
   Only `.xlsb` still goes to Excel.
2. **RESOLVED 2026-08-04 — Excel and Visio ownership verified by experiment.** Both landed in a
   fresh process (`owned=True`); PIDs and the COM-launched caveat are in "Where things stand".
3. **The 120s cold budget is deliberately above the measurements**, because killing `WINWORD.EXE`
   leaves Word's binaries in the OS file cache, so every "cold" number is a floor rather than a
   worst case. If a real post-reboot preview is ever seen timing out, raise it; the debug log
   prints which budget was in force.
4. **RETIRED 2026-08-05 — the palette load figures here are superseded.** They were single runs of
   `--open-test` read off the trace, and the trace lines each start their own stopwatch, so they
   never added up to an open. See "Open latency" at the top for the replacement, which measures one
   clock end to end with a median and a p95. Left here only so the old numbers are not mistaken for
   current ones.

   Still open from that work, and now quantified: **preview-ready for code and HTML is 680–770ms**
   while the palette itself is interactive at ~145ms. Not root-caused; it is not WebView2 teardown
   (3-minute idle timer). Best next lead if open latency is revisited.
5. **Word and PowerPoint are only instant if the palette was open first.** They still need their
   application, which takes tens of seconds cold, so the export is done in the background while the
   palette is being read. A document copied and previewed within a few seconds of each other can
   still wait once. Doing the export at copy time instead would fix that at the cost of starting
   Office for documents nobody ever looks at, which was considered and rejected.

## Traps

- **Log lines mentioning `Clip.Tests` or `clip-thumb-` paths are test noise, not app failures.**
  One triage session chased "PDF preview skipped" errors that were the suite's corrupt-PDF
  fixtures. Since 8200d92 the tests redirect logging via `CLIP_LOG_ROOT` (module initializer in
  `TestLogRedirect.cs`), so new pollution can't happen — but older log history still has it.

- **The preview cache hides everything.** Results are keyed by path + mtime + size under
  `%LOCALAPPDATA%\Clip\document-previews`, so re-previewing the same document never touches COM.
  Copy the file to a fresh name for every A/B run or you will measure nothing.
- **Never wrap preview work in `await Dispatcher.InvokeAsync(async ...)`.** It returns at the
  lambda's first await and discards the inner task — results read too early, exceptions vanish.
  `LoadFilePreviewAsync` already resumes on the UI thread; write straight-line awaits with
  `if (token != _previewToken) return;` after each one.
- **The timeout is not a leak guard**, whatever the code comment implies. On timeout the STA
  thread is abandoned, not killed, so a genuine Office hang leaks regardless of the number. What
  the timeout controls is how long the pane waits before falling back.
- **Never round-trip a `.cs` file through PowerShell `Get-Content`/`Set-Content`** — it
  double-encodes the media player's button glyphs into mojibake. Use Edit/Write.
- **A clean auto-merge is not a correct merge.** Both Office branches were reported as
  conflict-free; the ownership branch's stale base meant git happily produced a tree that did not
  compile, and the failure was the only thing standing between that and Word and Excel silently
  keeping the ungated COM path.

---

## 2026-08-19 — Clicking a row dismissed the palette (second monitor only)

**Symptom Isaiah reported:** clicking a clipboard item closed Clip. Happened "when Chrome is open"
— really it was "when the palette opened on the monitor Chrome was on."

**Root cause:** `HideIfMousePressedOutsidePalette(int, int)` converted the click's screen point
with `PointFromScreen` and tested it against `Rect(0, 0, ActualWidth, ActualHeight)`. That
transform uses the DPI WPF believes the window is on, but `PositionOnMouseScreen` places the
window with raw Win32 pixels. On a differently-scaled second monitor the converted point landed
outside the window's own rect, so every click — including one on a row — read as outside.

Not caused by the WH_MOUSE_LL hook in f4e95c0; the hook only made the pre-existing bad hit test
fire on every click instead of occasionally.

**Evidence:** `%LOCALAPPDATA%\Clip\shell.log`, 11:04. Three opens at `851,-1236` (second screen),
each `selection changed reason=click` followed within 100ms by `palette concealed
reason=outside-click`. The same click at `360,174` on the primary worked and reached
`double-click-paste`.

**Fix:** 76f33fd — test the click against `GetWindowRect` in raw screen pixels, the space the
mouse hook already reports. The outside-click log line now carries the point and the rect.
Shipped as v1.1.12, installed over `%APPDATA%\Programs\Clip`.

**Not changed (already correct):** the clipboard fallback Isaiah asked for exists —
`PasteSelected` calls `SetClipboard` before it ever sends Ctrl+V, so a paste that lands nowhere
still leaves the item on the clipboard for a manual paste.

### Next steps

1. Isaiah verifies: open Clip over Chrome on the second monitor, single-click a row (should stay
   open and show context), double-click (should paste behind).
2. If a click still dismisses, read the new log line — it prints the click point and the window
   rect, so a remaining mismatch is visible without a repro.
3. Audit the rest of `MainWindow.xaml.cs` for other `PointFromScreen` uses that mix with Win32
   screen coordinates. `MousePointInExpandedViewport` is the obvious next suspect for the expanded
   image view on a second monitor.

### Correction (same day) — the first fix was not enough

76f33fd swapped `PointFromScreen` for `GetWindowRect` and the dismissal continued. The real
mismatch was the *click* point, not the window rect. `GetProcessDpiAwareness(Clip.exe)` returns
**1 (system-aware, not per-monitor)**, so Windows virtualizes coordinates on any monitor whose
scale differs from the primary's — his primary is 150%, the second screen 100%. Every
process-level API (`GetWindowRect`, `GetCursorPos`, `GetMonitorInfo`, WPF DIP transforms) answers
in that virtualized space; a `WH_MOUSE_LL` hook's `MSLLHOOKSTRUCT.pt` does not, it is raw physical
pixels. The log line proved it: `outside click at 696,-664 rect=851,-1236,2051,-456`, and
696 x 1.5 = 1044, on a row.

Fixed in 57855ee by calling `GetCursorPos` inside the hook proc and ignoring the struct's point.

**Standing rule for this codebase:** never mix a system-level coordinate source with a
process-level one. It looks fine on the primary monitor and breaks only on the second.

---

## 2026-08-19 — Paste missing some input fields (Google Earth search in Chrome)

**Why Clip could not see the problem:** every attempt logged `paste verify skipped ...
verified=True`. That is what `VerifyPasteOrRetry` returns when `CanVerifyPasteTarget` finds nothing
readable — so a completely blind paste was being reported as confirmed. Now logs
`paste verify unavailable`.

**Reference: how Raycast does it.** Read statically from
`C:\Program Files\WindowsApps\Raycast.Raycast_2.0.3.0_x64__qypenmj9wpt2a\Raycast\Raycast.UIAccess.exe`
(embedded manifest + literal strings). Full notes in memory: `raycast-windows-paste-mechanics.md`.

**Adopted (dce371a):**
1. `ForceActivateWindow` — Raycast's ladder (already-foreground → SetForegroundWindow →
   AttachThreadInput) with a `GetForegroundWindow` poll between rungs, capped at 100ms each.
   `SetForegroundWindow`'s bool return is worthless; Clip trusted it and typed immediately.
2. Scan codes in `KEYBDINPUT.wScan` via `MapVirtualKey`. Was 0. Raw-input consumers (3D/canvas
   apps, some browser-hosted editors) read the scan code and ignore a zero.
3. `ReleaseStuckModifiers` before the chord — Alt from Alt+V turns Ctrl+V into Ctrl+Alt+V.

**Not adopted:** Raycast's helper carries `uiAccess="true"`, which exempts it from UIPI and makes
SetForegroundWindow always win. Requires an Authenticode-signed binary in a secure path; Clip
installs to `%APPDATA%\Programs\Clip`. Packaging change, not a code change.

**Pre-existing brittleness worth revisiting:** `CouldNeedNoActivatePalette` /
`IsGoogleEarthSearchElement` are a hardcoded allowlist — window title must contain "Google Earth",
process must be chrome/msedge, and the focused UIA element name must match one of four strings
(including Flutter's `flt-text-editing` / `transparentTextEditing`). The log shows `noActivate`
flipping between True and False across consecutive opens on the same page, which is exactly the
"some input fields" flakiness. The no-activate palette is the right idea; the gate on it is not.

### Next steps

1. Isaiah verifies Google Earth search in Chrome. New log lines to read: `force activate failed
   target=... actual=...`, `released stuck modifiers ...`, `paste verify unavailable`.
2. If it still misses, the log now says which rung failed. If `force activate failed` appears,
   uiAccess/packaging is the real answer. If activation succeeded and the paste still did not
   land, the field is being torn down on blur — widen the no-activate path rather than the
   allowlist.

---

## 2026-08-19 (cont.) — uiAccess verdict, and two more fixes

**Should Clip adopt uiAccess? Not yet — and now there is a detector for when it should.**
It buys exactly one thing: pasting into windows at a higher integrity level (apps run as
administrator), where UIPI silently drops injected keys. Costs are an Authenticode signing
certificate and moving the install from `%APPDATA%\Programs\Clip` into Program Files, which means
an admin installer and a reworked updater. The log has never recorded a single foreground
failure, so it is unjustified today. `TargetRejectsSyntheticInput` (7ff03d4) now compares token
integrity levels before pasting and shows the "press Ctrl+V manually" toast instead of failing
silently. **If Isaiah starts seeing that toast often, that is the evidence that justifies uiAccess.**

**On verification generally:** `NotifyPasteFailed` had one caller — the branch that runs after
verification proves a paste failed. Verification needs a UIA element with a readable value, which
canvas/Flutter fields do not expose, so almost everything failed silently. Pastes into those
fields are genuinely unverifiable; Raycast does not verify them either (its "set and verify"
string is about the clipboard write, which Clip's `Win32ClipboardWriter` path already checks and
toasts on). The honest improvement was not more verification but not claiming success — done in
dce371a (`paste verify unavailable`) and 7ff03d4 (predict the one failure we can).

**Preview truncation (67025df).** Long items keep their text in an asset file with only a
truncated `Text` on the row; list loads do not hydrate it, so `TextPayload` fell through to
`item.Preview` — one line, 120 characters, literal "..." appended. That string was what the
preview pane rendered. No layout change was needed: the pane's TextBox already wraps, already has
`VerticalScrollBarVisibility="Auto"`, and sits in a bounded row. `FullTextPayload` hydrates via
`_store.GetItem` first, mirroring `ClipboardItemForPasteFormat`.

### Next steps

1. Isaiah verifies a long copied item now fills the preview pane and scrolls.
2. Watch for the "could not paste here" toast. Frequent → revisit uiAccess packaging.
3. Note: two tests flaked once on a full run (reflection stack, teardown file locks — the known
   issue from the earlier handoff) and passed on rerun. If that recurs, chase the teardown locks.

---

## 2026-08-19 (cont.) — correction: elevated pasting IS solvable without uiAccess

The earlier entry said uiAccess was the only route. That was wrong. UIPI blocks a **lower**-
integrity process from sending input to a **higher**-integrity window; equal-to-equal is fine. So
Clip running elevated pastes into elevated apps without any certificate or install relocation.

`Install-ClipStartup.ps1 -Elevated` (d4645df) registers the existing autostart task with
`-RunLevel Highest` instead of `Limited`, which starts Clip elevated at logon with **no UAC
prompt**. Opt-in, not default, because:
- Drag and drop from Explorer into Clip stops working — UIPI blocks that direction too.
- Anything Clip opens or launches inherits administrator.

Three ways to cross the line, in order of cost: run Clip elevated (one flag, real downsides) →
uiAccess helper (signing cert + Program Files install, no downsides) → UAC-bypass techniques
(malware territory, AV-flagged, breaks on patches — not on the table).

Toast rewritten to name the cause and both exits; it wraps at 420px and holds for six seconds
(`ShowToast` now takes an optional duration; default stays 2.4s).

### Next steps

1. If Isaiah wants elevated pasting: run in an admin shell —
   `powershell -ExecutionPolicy Bypass -File .\Install-ClipStartup.ps1 -Elevated`, then log out
   and back in. Reverting is the same command without `-Elevated`.
2. If he takes it, watch for the drag-and-drop regression; that is the one that will bite first.

---

## 2026-08-19 (cont.) — elevated paste helper, and why the toast was never seen

**The toast bug.** The integrity gate was correct all along — the log shows
`paste blocked by integrity target=12288 own=8192` (high vs medium) on every attempt. The message
was invisible because `NotifyPasteBlockedByElevation` ran ~37ms *after* `ConcealPalette("paste")`,
and the toast is a child of the palette window, so it was drawn at Opacity 0.
`UserNotificationRequested` (tray balloon via `_tray.ShowBalloonTip`) is not a fallback either —
Windows 11 suppresses NotifyIcon balloons. **Rule: nothing in the paste path may raise a toast
after the palette is concealed.** Note `ConcealPalette` also cloaks the window and parks it off
screen, so "just show it again" is not a two-line undo — restructure to not hide instead.

**Clip.Elevated.exe (108b888).** New project, manifested `requireAdministrator`, launched by
`ElevatedPasteHelper` through the `runas` verb on demand. One UAC prompt, then resident. Clip
itself stays medium-integrity, so drag-and-drop from Explorer keeps working and nothing Clip
launches inherits admin — the two costs of the `-Elevated` autostart flag, avoided.

Deliberate constraints on the helper, worth preserving:
- One command, `paste`, **no arguments**. It cannot be told which keys to send or which window to
  target. Worst case for someone who reaches the pipe is a Ctrl+V into whatever had focus. Do not
  add a general "send these keys" command, however convenient it looks.
- Pipe ACL'd to the owning user's SID only.
- Exits after 8 idle hours rather than sitting elevated indefinitely.
- `LaunchTimeout` is 10s and runs on the UI thread; it covers only post-approval startup, since
  `Process.Start` blocks on the elevation decision itself. Do not raise it — a long value here
  reads as a frozen palette.

Focus is restored **twice**: before the first attempt, and again after the UAC prompt, which takes
foreground and does not reliably hand it back.

**Not verified on hardware.** The manifest was confirmed embedded by reading RT_MANIFEST out of
the built exe, the wiring compiles and 884 tests pass, but the UAC path itself is untested —
testing it means raising a prompt on Isaiah's screen, which is off limits.

### Next steps

1. Isaiah pastes into an app running as administrator. Expect one UAC prompt, then the paste
   lands. New log lines: `paste selected ... via=elevated-helper`, or
   `elevated paste helper declined at the UAC prompt` / `never started listening`.
2. If declined, the palette should now stay up with the toast visible — that is the fix for the
   invisible-message bug, and it is worth confirming separately.
3. `-Elevated` on `Install-ClipStartup.ps1` is now the fallback rather than the recommendation;
   the helper avoids both of its downsides.

---

## 2026-08-19 (cont.) — helper removed; the toast is the shipped answer

Isaiah accepted the UAC prompt and the paste still did not land. Removed at 47e5510, at his call.
Files gone: `src/Clip.Elevated/`, `src/Clip.Shell/ElevatedPasteHelper.cs`, the solution entry, the
publish step, and the installed `Clip.Elevated.*`.

**Why it failed — do not rebuild it without solving this.** The log showed no
`via=elevated-helper`, no decline, and no "never started listening", which leaves only the one
unlogged path: `TrySendPaste` returning false *after* the pipe existed. That is the mandatory
integrity label. A named pipe created by a high-integrity process carries a high label with
no-write-up, so a medium-integrity client can see the pipe and cannot write to it; the write threw
UnauthorizedAccessException and was swallowed as "not listening yet".

Fixing it means the elevated server attaching a SACL with a **low** mandatory label so
lower-integrity clients can write — i.e. deliberately opening an elevated process to
medium-integrity input. That is the exact surface the one-command-no-arguments design existed to
contain, and it buys a single keystroke. Judged not worth it.

**Shipped behaviour:** integrity check runs before the palette is concealed, the palette stays up,
the toast reads "Clip cannot paste into elevated (administrator) run apps — press Ctrl+V". The
clipboard is set before any of it, so the manual paste always works.
`Install-ClipStartup.ps1 -Elevated` remains for anyone who would rather run Clip elevated.

### Next steps

Nothing outstanding on this thread. If elevated pasting comes back as a priority, the two live
options are the SACL fix above (cheap, widens an elevated attack surface) or a signed uiAccess
helper (clean, needs a certificate and a Program Files install).

---

## 2026-08-21 — Fresh palette on every open, and why Blip sharing died

**Isaiah reported three things:** the palette reopens scrolled to wherever it was left, the search
box keeps its old query, and sharing to Blip throws a Java error dialog.

### Search text and scroll offset (commit 3b066cb)

Neither was ever reset. `SearchBox.Text` is written in exactly one place
(`ScheduleDebugInitialSearch`) and cleared in none; `ListScroll` is only touched by the mouse-wheel
handler. So a query typed at 11:02 was still filtering the list at 14:40, and the list opened
mid-scroll.

Fixed in `ResetPaletteViewForNextOpen`, called from **`ConcealPalette`, not `ShowPalette`**. That
choice matters: `ShowPalette` has ~20 callers and most are a re-show inside one flow (back from the
settings overlay, from picture-in-picture, from the inline editors, after a paste). Concealing is
the one event that always separates one visit from the next.

Three parts, each with a reason:

- Clearing the text restarts the debounce via `OnSearchChanged`, so `_searchTimer.Stop()` follows it
  and `_itemsDirtySinceRender = true` makes the next open re-query — the rows still hold the
  filtered set.
- A **hand-picked** row also has to mark the rows dirty. `ShowPalette` only reloads when dirty, and
  the reload is what re-selects the top item; without this the list showed the top while the
  selection and preview sat on a row far below.
- An automatic selection with no search costs nothing: `--open-test` still reports `dirty=False` on
  both opens, so the warm-open fast path is untouched.

**Verified off screen.** `--open-test` now honours `--debug-search` (the open-test branch in
App.xaml.cs returns before the normal arg wiring, so the flag never reached it):

```bash
Clip.exe --open-test --debug-search=zzznotfoundzzz
```

Cold open applies the query → `render items reason=search rows=0/0`. Conceal. Warm open →
`render items reason=show-refresh rows=8/8` then `background-full-refresh rows=11/500`. The query is
gone and the full history is back.

### Blip sharing (same commit)

**Not Clip's arguments.** `--file` is correct — `LaunchArgs` in
`app/desktopApp-desktop-1.1.15.0.jar` is a Clikt command taking `--background`, `--peer`, `--file`
(multiple) and `--render-api`.

**Root cause is in Blip, and it is a landmine worth knowing about.** Blip is single-instance via
`com.github.iamcalledrob.singleinstance`. A second `blip.exe` hands its arguments to the running one
over a unix-domain socket at `%TEMP%\net.blip.desktop\ui.sock` and exits. The `.lock` file beside it
is held open for the life of the primary instance, so it survives; **the socket file is not, and
Windows temp cleanup deleted it out from under a Blip that had been up since 08-13.** From then on
every launch — Clip, Explorer, Start menu — found the lock (so it concluded it must be the second
instance), failed to connect to a socket that was gone, and died with

    java.lang.Exception: SingleInstance failure (...\net.blip.desktop\ui.sock)
    MainKt.exitOrReceiveFutureArguments(main.kt:200)

Proven by experiment, not inference: before, `ui.sock` was absent and `blip.exe --file X` left a
windowless zombie process; after killing Blip and relaunching it, `ui.sock` reappeared and **the
identical launch line opened Blip's share sheet with the file attached**. Blip on this machine was
restarted during the session, so sharing works right now.

Clip cannot repair another app's socket, so it detects the state instead:
`BlipShareLaunchPlan.IsRunningWithBrokenHandoff()` — Blip process running **and** the socket file
missing — and `ShareWithBlip` toasts "Blip can't receive shares until it's restarted." rather than
launching into a Java dialog that tells the user nothing. Checked before the payload is created, so
no temp share file is left behind.

887 tests pass (3 new, on the pure `IsRunningWithBrokenHandoff` overload). Published, installed over
`%APPDATA%\Programs\Clip` (all four exes hash-verified), restarted via the "Clip Autostart" task.
Pushed to `main`. No release cut.

### Trap: `windir` can be empty in an agent shell

Four `DomainMonogram` tests and `--open-test` itself failed with
`MS.Internal.FontCache.Util..cctor` → `UriFormatException`. Nothing to do with the app: WPF builds
the font-cache path from `%windir%`, and this session's shell had `windir` empty while `SystemRoot`
was set. Set `$env:windir = 'C:\Windows'` and all 887 tests pass. **Do not chase a WPF font-cache
UriFormatException as a code bug — check `windir` first.**

### Next steps

1. Isaiah verifies: Alt+V twice in a row — list starts at the top, search box empty, top item
   selected. Type a query, Escape, reopen — query gone.
2. Isaiah verifies Share → Blip. If the SingleInstance dialog ever appears again, the socket vanished
   a second time and the answer is to restart Blip. If the dialog appears *instead of* Clip's toast,
   the detection path is what to look at (`blip share blocked ... missing=True` in shell.log).
3. If temp cleanup keeps eating Blip's socket, the durable fix is on Blip's side (a socket path
   outside `%TEMP%`) — worth reporting upstream rather than working around further in Clip.

### Released as v1.1.13 (same day)

Cut a release for the 18 commits that had piled up since v1.1.12. The first release build **failed**,
on `PasteIntegrityGateTests.ReadsMediumIntegrityForThisProcess`: it hardcoded `0x2000` (medium
integrity), which is true on a dev desktop and false on a GitHub Actions runner, which is elevated
and therefore high (`0x3000`). The test was added during the elevated-paste work *after* v1.1.12, so
it had never been through a release build. Fixed at 8ebebef by deriving the expected level from
`WindowsPrincipal.IsInRole(Administrator)`; the assertion that matters — non-zero, meaning the SID
walk still works — is unchanged. Tag was force-moved onto the fix (no release had been published
against the old one).

**Lesson: a test that asserts something about its own host will pass locally forever and only fail
in a release build.** If a new test reads process/session/desktop state, ask what CI's answer is.

v1.1.13 is live at https://github.com/Kal-Voe/Clip/releases/tag/v1.1.13 with the installer and both
zips, marked Latest. `%APPDATA%\Programs\Clip` was reinstalled from the **released** zip rather than
the local publish, so the running copy is byte-identical to what is downloadable (all five exes
hash-verified, version 1.1.13+8ebebef).
