# disk-usage-win

A lightweight Windows disk-space GUI that scans a folder tree and shows which
folders use the most space.

The executable is `disk-usage-win.exe`; the window title remains **Fast Disk
Usage**. The application uses built-in Windows Forms and .NET Framework
components, with no third-party packages, installer, or NuGet dependencies.

## Requirements

- Windows 11
- The .NET Framework 4.x runtime to run the application.
- To build or run the regression tests, the Microsoft .NET Framework 4.x C#
  compiler, normally located at
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
  (`build.cmd` also checks the 32-bit Framework path)

## Build

1. Extract or clone the project to a local folder.
2. Double-click `build.cmd`, or run it from Command Prompt.
3. The script creates `disk-usage-win.exe` in the project folder.

No downloads, Visual Studio, NuGet, Git, Python, .NET SDK, or third-party DLLs
are required to build once the source and Framework compiler are available.
Administrator rights are not required.

## Run

Double-click `disk-usage-win.exe`, or run `run.cmd`.

Enter a folder path or use **Browse...**, then click **Scan** (or press Enter
in the path field). While scanning, the status bar displays an
animated progress indicator and live directory, file, byte, and error counts.
The folder tree, folder list, and treemap populate during the scan, with updates
roughly every half second. You can browse discovered folders and use **Up**
while scanning. Sizes and percentages reflect only data discovered so far;
unfinished folders are marked **partial** until their scan finishes.
The UI remains responsive, and scanning can be canceled. Cancellation keeps
the partial results visible. Finishing a scan preserves the folder you are viewing.

Select a folder in the tree, double-click a folder in the list or treemap to
drill into it, and use **Up** to return to its parent. Starting a new scan
clears the previous results and error details.

## Features and performance

- Uses native Win32 `FindFirstFileEx` / `FindNextFile` enumeration.
- Uses `FIND_FIRST_EX_LARGE_FETCH` where supported.
- Runs scans on a background thread to keep the GUI responsive.
- Stores directory-level scan data and a separate UI-owned directory model
  while scanning; individual files are not retained. Updates are coalesced
  so a busy UI does not accumulate a queue of redraws.
- Does not traverse directory reparse points, including junctions; these appear
  as skipped folders to avoid loops and unexpected traversal.
- Materializes TreeView children lazily.
- Performs no content hashing, thumbnail generation, or per-file visualization.
- Renders the treemap from discovered directory totals, including provisional
  sizes during a scan, without additional disk I/O.
- Shows a clickable **Errors: N** status item. Selecting it opens a non-modal
  error details window with the Windows error code, operation, message, and
  path. The window offers **Refresh**, **Copy All**, and **Save Log** controls.
- Retains up to 2,000 error details to keep scan memory use predictable; the
  total error count is still tracked.
- Provides a right-click **Open in File Explorer** action for folders in the
  tree and folder list. Right-clicking a folder selects it first. The synthetic
  `[files directly in this folder]` row has no folder target and no Explorer
  action.
- Includes a WinDirStat-style treemap below the folder list. The current
  folder's immediate subfolders are shown as rectangles proportional to their
  size, with files stored directly in the current folder shown as a separate
  block. Double-click a folder rectangle to drill into it; right-click one to
  open its folder in File Explorer. Hover over a rectangle to see its path and
  formatted size.
- Implements the treemap with built-in WinForms and `System.Drawing`; it does
  not retain file objects or perform a second disk scan.

## Interpreting results

- Sizes are logical file lengths reported by Windows, not allocated
  "size on disk" or drive free-space measurements. Compression, sparse files,
  and hard links can make these totals differ from actual storage usage.
- Individual files are not listed. Their sizes are combined into the
  `[files directly in this folder]` row and treemap block.
- Live sizes and percentages are provisional. A folder marked **partial**
  has not finished scanning; a canceled scan is not a complete inventory.
- Folders that cannot be fully enumerated are marked **partial / denied** in
  the folder list. Inspect **Errors: N** for details: scan completion does not
  imply that every folder was accessible.
- Access is limited to the current user's permissions. The application does
  not elevate or bypass access restrictions.

## Corporate-environment notes

- No installer or elevation request.
- No telemetry, downloads, or registry writes. Scanning a network folder still
  requires normal filesystem access to that location.
- No service, scheduled task, driver, shell extension, or automatic persistence.
- Error logs are written only when you choose **Save Log**.
- No raw-disk or NTFS MFT access.
- The source is a single `Program.cs` file and can be reviewed before
  compilation.

## Tests

Run `test.cmd` to compile and run the dependency-free scanner and WinForms
regression tests using the same Framework compiler lookup as `build.cmd`.
The tests open WinForms windows, so run them in an interactive Windows session.

Coverage includes live results before completion, final totals, snapshot
isolation, coalesced updates, navigation, selection preservation, cancellation,
concurrent worker/UI updates, newly discovered children in collapsed tree
branches, empty folders, and error reporting for a missing folder.
Temporary fixtures and the test executable are removed afterward.

## Source layout

- `Program.cs`: application startup, native scanner, live result snapshots,
  WinForms UI, treemap, and error details.
- `app.manifest`: requests normal user privileges and enables DPI awareness.
- `build.cmd`: compiles `disk-usage-win.exe`.
- `run.cmd`: launches the executable; asks you to build first if it is missing.
- `Tests.cs` and `test.cmd`: regression tests and their compile/run script.

## Version notes

### Current changes

- Renamed the build output and launcher target to `disk-usage-win.exe`;
  application window titles and the internal namespace remain unchanged.
- Added live tree, folder-list, and treemap updates during scanning, with
  provisional totals and navigation before completion.
- Coalesced worker snapshots into a UI-owned directory model; finishing a scan
  preserves navigation, and cancellation retains partial results.
- Added scanner and WinForms regression tests, including concurrent updates
  and discovery of children while a tree branch is collapsed.

### Earlier changes

- **v2:** Fixed a startup failure caused by assigning the split-pane divider
  before the form had its final width. Layout is deferred until the window is
  visible, and startup failures display an error dialog instead of failing
  silently.
- **v3:** Fixed a WinForms startup crash caused by `SplitContainer` minimum-size
  validation before the initial layout pass. No splitter minimum sizes are
  assigned during construction.
- **v4:** Added the clickable error-count status item and non-modal error
  details window. Error details are capped at 2,000 retained entries while the
  total count continues to be tracked.
- **v5:** Added the folder context menu with **Open in File Explorer** for
  folders in the tree and folder list.
- **v6:** Added the treemap for the current folder's immediate subfolders and
  directly stored files, with drill-down, Explorer context action, and
  path/size hover details.

## License

[MIT](LICENSE).
