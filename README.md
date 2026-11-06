# SWT

Native C# / WPF terminal emulator for Windows 11. Early working prototype, not yet
a replacement for Windows Terminal.

## Run

Requires the .NET 10 SDK and Windows x64.

```powershell
.\run.ps1
```

The launcher uses the local SDK at `%LOCALAPPDATA%\SWT\dotnet` when present,
otherwise `dotnet` from PATH. Alternatively, with .NET 10 on PATH:

```powershell
dotnet run --project .\src\SWT.Desktop
dotnet test .\SWT.slnx
```

PowerShell 7 is selected from PATH; Windows PowerShell is the explicit fallback.
No administrator permissions are needed.

## Appearance

First launch creates `%LOCALAPPDATA%\SWT\config.toml` without replacing an
existing file. Edit it while the app runs: valid appearance edits reload
automatically, including editor save-by-rename. Errors appear in a dialog
and preserve the previous settings. There is no bottom status bar.

```toml
version = 1

[window]
decorations = false
corner_radius = 8.0
background_opacity = 0.75
columns = 120
rows = 32

[font]
family = "Consolas"
size = 14.0

[terminal]
scrollback_lines = 10000

[colors]
foreground = "#D8DEE9"
background = "#20242C"
cursor = "#ECEFF4"
```

Omitted keys use defaults. The generated file includes the complete 16-color
`colors.ansi` palette and `[shell]` settings (`executable`, `arguments`,
`working_directory`). Shell executables accept an absolute path or a filename
on PATH. Relative executable paths are rejected. Arguments are an array of
individual Windows arguments, not a command line to split.

- Opacity: `0.0` to `1.0`, background only; text stays opaque.
- Decorations: app-drawn title bar and controls, not native OS decorations.
- Corner radius: `window.corner_radius`, 0 to 32 WPF device-independent pixels (default 0); live reload applies it.
- Columns: 20 to 500; rows: 5 to 200; initial dimensions only.
- Scrollback: `terminal.scrollback_lines`, 0 to 100,000 (default 10,000; 0 disables).
  Live reload applies a new limit, keeping the newest lines.
- Font size: 6 to 72 WPF device-independent pixels.
- Colors: `#RRGGBB`; ANSI palette: exactly 16 entries.
- Shell and initial dimensions apply next launch.
- Appearance changes are live; missing fonts are reported and fall back to Consolas.
- Unknown keys and incorrect types are errors.

The layered WPF window remains enabled even at opacity `1.0`; this lets opacity
change without recreating the HWND or restarting the shell.

## Controls

| Action | Shortcut |
|---|---|
| Reload configuration | Ctrl+Shift+R |
| Toggle/recover title bar | Ctrl+Shift+D |
| Move window without title bar | Alt+left-drag |
| Resize | Drag window edges/corners |
| Interrupt shell command | Ctrl+C |
| Paste | Ctrl+Shift+V |
| Right-click | Copy selection, or paste if none |
| Select text | Left-drag |
| Copy selection, or visible viewport if none | Ctrl+Shift+C |
| Scroll history | Mouse wheel (3 lines/notch) |
| Search history and current screen | Ctrl+Shift+F |
| Scroll history by page | Shift+PageUp / Shift+PageDown |
| Jump to oldest / live output | Shift+Home / Shift+End |
| Close | Alt+F4 or title-bar close |

Typing or pasting returns the view to live output. While scrolled back, new output
keeps the view anchored, a thumb on the right edge shows the position, and the
cursor is hidden. `ESC[3J` clears scrollback; `ESC[2J` clears only the screen.

Pastes containing newlines or control characters require confirmation. Paste is
limited to 65,536 characters in this prototype. When the application enables
bracketed paste (`ESC[?2004h`), pasted text is sent between `ESC[200~` and
`ESC[201~`; `ESC[?2004l` disables it. Confirmation and size limits still apply.
CRLF and LF line endings are normalized to CR in either mode.
When an application enables mouse tracking, unmodified right-clicks continue
to reach that application; hold Shift for local mouse selection or paste.

Search is case-insensitive across retained history and the current screen.
Soft-wrapped rows are joined without adding a newline; explicit line breaks use
LF. In literal mode, type `\n` to match a line break. Enable `.*` to use .NET regular
expressions (`\n`, `\d`, etc.); `^` and `$` match logical line boundaries. Use
`(?s)` if `.` should also match newlines. Zero-length matches are skipped.
Type a query and press Enter to find the newest match; Enter/Next then moves
forward, Shift+Enter/Previous moves backward, wrapping at either end.
The active match is highlighted and brought into view. Escape or Close dismisses
search without returning to live output. The alternate screen searches only its
own rows. Search text and navigation keys are not sent to the shell.
Invalid regex and timeouts appear in the search bar. Search snapshots are limited
to 8 Mi UTF-16 characters, with a 100 ms regex-operation timeout and a two-second
match-enumeration budget; exceeding a limit reports an error, not partial results.

## Implemented

- ConPTY shell with UTF-8 streaming, ordered input, resize, exit reporting,
  and job-owned child-process cleanup.
- WPF cell rendering, cursor, basic ANSI cursor/erase commands, SGR attributes,
  16/256/RGB colors, and device-status replies.
- Separate main and alternate screens for DEC modes 47, 1047, and 1049; mode
  1049 restores the main cursor. Resizing updates both buffers without sending
  alternate-screen output into scrollback. Application cursor and keypad modes
  emit their corresponding VT key sequences.
- Bracketed paste with application-controlled DEC mode 2004.
- Insert/delete characters (`CSI @` / `CSI P`) within the current row, keeping
  the cursor fixed and shifted cell colors/attributes intact. Counts default
  to one and are limited to the remaining columns; new blanks use current
  attributes.
- Per-buffer scrolling margins (`CSI top;bottom r`), insert/delete lines
  (`CSI L` / `CSI M`), scroll up/down (`CSI S` / `CSI T`), and reverse index
  (`ESC M`). Margins protect surrounding rows; line edits and partial-region
  scrolling do not add history. Full-screen upward scrolling on the main
  buffer retains history. Height changes reset both buffers' margins to the
  full screen; width-only changes preserve them.
- Origin-relative cursor mode (`DEC ?6h` / `?6l`), maintained per buffer.
  Enabling/disabling homes the cursor; absolute row addressing and position
  replies become relative to the top margin while enabled, and cursor movement
  stays within the margins. Cursor save/restore also preserves origin mode.
- Basic Unicode cell layout: combining marks attach to the preceding cell;
  common CJK/Hangul/fullwidth characters occupy two cells. Copy and selection
  retain complete characters; overwrite, erase, character edits and resize
  remove split wide characters. A wide character wraps before a lone remaining
  column (or becomes U+FFFD in a one-column grid). Combining text is bounded to
  64 UTF-16 code units per cell; unattached leading marks are ignored.
- Built-in Block Elements (`U+2580-U+259F`): full/fractional blocks and quadrants
  fill cell-relative geometry instead of font glyphs, avoiding gaps between blocks.
  Shades use 25/50/75% foreground coverage over the configured background.
- Built-in Box Drawing for light/heavy and double-dash lines, mixed-weight
  corners and junctions, rounded corners, half-lines, double-line forms, and
  diagonals; unsupported diagonal mixed-weight forms fall back to the font.
- WPF IME text composition with an underlined pre-edit preview at the terminal
  cursor; only committed text is sent to the shell.
- Mouse reporting for DEC modes 9, 1000, 1002, and 1003, with SGR (1006) and
  legacy encoding. Mouse clicks, motion, modifiers, and wheel events are sent
  to applications when requested; otherwise the wheel scrolls terminal history.
- Bounded scrollback with wheel/keyboard navigation and width reflow of the main
  buffer and retained history. Soft wraps are reassembled; explicit line breaks,
  cell colors and Unicode cells are retained. The alternate screen remains a
  fixed grid so full-screen applications can redraw it.
- Literal/regex, case-insensitive search across retained scrollback and the current
  screen, including multi-row matches, wrapped next/previous navigation and
  active-match highlighting. Copying selections omits soft-wrap line breaks.
- Coalesced ConPTY output with 1 MiB backpressure; per-line cached drawings so a
  frame rebuilds only changed lines.
- Optional custom chrome, background transparency, live TOML appearance.
- Tests for parsing, rendering alpha, real shell I/O, Ctrl+C, and repeated
  session startup/shutdown.

## Known limitations

- **Scrollback/search:** history remains bounded by `terminal.scrollback_lines`;
  reflow can evict oldest rows when narrowing increases the row count. Alternate
  screen contents are resized without reflow. Search has the size/time limits
  described above. Resize clears selection and active search highlights;
  repeat the search to locate the match in the new layout. A scrolled-back
  viewport retains a clamped row offset, not an exact text anchor across reflow.
- **Unicode:** a basic CJK width policy, not a complete Unicode width table;
  ambiguous-width characters remain one cell. Complex scripts, emoji presentation,
  ZWJ sequences and full grapheme-cluster handling remain unsupported.
- **IME:** composition/pre-edit display and committed input are implemented;
  candidate-window placement still needs manual validation.
- **Accessibility:** no accessibility text provider for terminal contents yet.
- **OSC:** strings are discarded, including window-title, hyperlink and clipboard
  requests; they have no clipboard or process side effects.

### Full-screen terminal applications

This means terminal UIs such as Neovim, not maximizing the SWT window.
Support is partial, not absent: alternate buffers (DEC 47/1047/1049), cursor
save/restore (including DEC 1048), scrolling regions, line/character editing,
application cursor/keypad modes, bracketed paste and VT mouse reporting are
implemented. These features have automated tests, but do not certify every
application, version, configuration or plugin.

Concrete remaining gaps include:

- **Keyboard:** no explicit F1-F12 encoding or modifier-aware navigation sequences
  (for example Ctrl+Arrow and Shift+Arrow). Applications may not receive those
  shortcuts correctly.
- **Text attributes:** SGR supports colors and bold, but not italic, underline,
  reverse video, dim or strikethrough. Applications using reverse video for
  selection or menus can therefore display them incorrectly.
- **Legacy character sets:** DEC special-graphics designation is consumed but
  does not translate its characters into line-drawing glyphs. Unicode box drawing
  is a separate, implemented feature.
- **Additional protocols:** no focus-event reporting (DEC 1004), configurable tab
  stops, or complete xterm capability/query support.

Do not set a broad `TERM=xterm-256color` compatibility promise for this prototype;
256-color rendering alone does not imply full xterm input and control support.

### Future built-in graphics

These families still use font glyphs and may need cell-aligned rendering similar
to Block Elements. Box Drawing's cardinal lines, corners, junctions, arcs,
half-lines, double lines, and standard diagonals are drawn geometrically;
mixed-weight diagonal crossings use the configured font glyph.

| Family | Code points | Rendering requirement |
|---|---|---|
| Powerline separators | Selected private-use symbols, including `U+E0B0-U+E0BF` | Join triangles and curves to adjacent background segments; not every code point has a universal meaning. |
| Symbols for Legacy Computing | Selected graphics in `U+1FB00-U+1FBFF` | Sextants, mosaics, and other cell-relative shapes. |
| Legacy Computing Supplement | Selected graphics in `U+1CC00-U+1CEBF` | Additional mosaic and terminal graphics. |
| Braille Patterns | `U+2800-U+28FF` | Consistent 2-by-4 dot placement for terminal plots. |
| Progress bars and Git branch graphics | Selected Nerd Fonts/private-use symbols | Consistent fills, end caps, and connected branch strokes; inventory mappings before implementation. |

Coverage differs by terminal; these are candidates, not promises to replace every
symbol in each range. Reference implementations:
[Windows Terminal](https://github.com/microsoft/terminal/blob/main/src/renderer/atlas/BuiltinGlyphs.h),
[Alacritty](https://alacritty.org/config-alacritty.html#s33), and
[WezTerm](https://wezterm.org/config/lua/config/custom_block_glyphs.html)
(its progress/branch extensions are documented as nightly-only).
Emoji, combining marks, and wide CJK characters require separate width/shaping
work, not this geometric replacement.

### Rendering limitations

Custom chrome, mixed-DPI interaction, maximize bounds, and transparency performance
still need interactive validation. Offscreen alpha tests do not certify desktop
composition speed or key-to-paint latency. 
With per-line drawing caches, 120x32 UI-thread render work is p95 0.3-0.5 ms
(static or one new line per frame). The offscreen probe that adds software
bitmap rasterization measures 15-65 ms and is noisy; it is not an on-screen frame
time. The parser handles about 33 MiB/s of colored output into a full
10,000-line scrollback (i7-1165G7).

## Validation

For startup diagnostics, run `.\run.ps1 -Diagnostics`.
Each run writes a local JSONL file under `%LOCALAPPDATA%\SWT\logs`.
Logs record dimensions, cursor positions and numeric VT control sequences, not
commands or printable shell output. Logging stops with a warning at 10 MiB.
Controls split across output batches may not appear in the diagnostic summary.

```powershell
dotnet test .\SWT.slnx
.\run.ps1 -SmokeTest
```

The smoke check opens a real WPF window, starts PowerShell without a profile,
verifies a marker reaches the terminal screen, closes the session/window, and
returns a nonzero exit code on failure. It does not modify user configuration.

Tests run Windows-only ConPTY integration, 100 measured start/close cycles after
warmup, and post-GC native-handle plateau checks. They also render text over
opacity `0.0`, `0.5`, and `1.0` into a bitmap and check actual pixel alpha.
