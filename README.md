# Microsoft Store Package Downloader

> A Windows desktop utility that retrieves direct download links for Microsoft Store packages and handles the full download + installation workflow — no browser extension, no Python, no admin rights to download.

Built with **WinForms / .NET Framework 4.8** — runs on any Windows 10/11 machine without installing anything extra.

---

## Features

### 📦 Package Retrieval
- Accepts any **Microsoft Store URL** (`apps.microsoft.com/detail/...`, `microsoft.com/en-us/p/...`) or a bare **Product ID** (e.g. `9NBLGGH4NNS1`)
- Queries [store.rg-adguard.net](https://store.rg-adguard.net) to obtain direct CDN download links
- Reads the real file size via HTTP `HEAD` for every package (rg-adguard often reports placeholder sizes)
- Identifies and separates **main packages** from **framework dependencies**
- **Encrypted packages** (`.eappx`, `.eappxbundle`) are automatically de-preferred when a plain equivalent exists

### 🔍 Smart Dependency Selection
- After listing packages, downloads and parses each bundle's embedded **`AppxManifest.xml`** / **`AppxBundleManifest.xml`** via targeted HTTP range requests — no full-file download needed
- Uses real `<PackageDependency>` declarations to auto-check exactly the right dependency variants
- Respects the active **architecture filter** when selecting dependency variants (e.g. filter = x64 → selects x64 VCLibs, not arm64)
- Falls back to name/architecture heuristics when manifest data is unavailable

### ⬇️ Downloads
- All files go to **`MSStorePackages\`** next to the `.exe` — no folder dialog, no prompts
- **Individual app folders** option: each app gets its own `MSStorePackages\<AppName>\` sub-folder
- **Skip already-downloaded** files (compares size on disk to reported file size)
- Parallel downloads with per-file progress (speed, percentage, status column)
- Cancel individual downloads or all at once
- Files written as `.tmp` then atomically renamed — no partial files left on failure

### 📋 Clipboard Monitor
- Runs silently in the background (toggleable checkbox)
- Detects Microsoft Store URLs or Product IDs copied to the clipboard from any app (Edge, browser, etc.)
- **Automatically brings the window to front** and **triggers a search** — no manual paste needed

### 📝 Installer Script Generation
- Optionally generates a **PowerShell `.ps1` install script** alongside the downloaded files
- Script is named after the app: `Install_<PackageName>.ps1`
- Correct installation order is enforced:
  1. `.NET Native` runtime & framework
  2. `VCLibs` (Visual C++ redistributables)
  3. `WindowsAppRuntime`
  4. `UI.Xaml`
  5. Store engagement SDK
  6. Main application
- **Allow unsigned** option adds `-AllowUnsigned` flag for developer/sideloaded packages
- Run with: `PowerShell -ExecutionPolicy Bypass -File "Install_AppName.ps1"`

### 🎛️ UI
- Dark-themed resizable window
- Architecture filter (All / x64 / x86 / arm64 / arm / neutral) — **changing filter re-runs the search automatically**
- Auto-selects the newest version of each main package on load
- Colour-coded toolbar buttons
- Colour-coded output log with save-to-file
- Copy download URLs to clipboard

---

## Screenshots

> *Dark-themed grid showing main packages and auto-selected dependencies*

---

## Requirements

| | |
|---|---|
| **OS** | Windows 10 or Windows 11 |
| **Runtime** | .NET Framework 4.8 *(pre-installed on Windows 10 1903+)* |
| **To build** | .NET Framework 4.8 Developer Pack **or** Visual Studio 2019+ |
| **Dependencies** | None — standard .NET Framework assemblies only |

---

## Building

### Option A — One-click build script

```bat
build.bat
```

Automatically locates `csc.exe` and compiles all source files.  
Output: `bin\MSStoreDownloader.exe`

### Option B — Developer Command Prompt

```bat
csc /target:winexe /platform:anycpu /optimize+ /out:bin\MSStoreDownloader.exe ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll /r:System.Net.dll ^
    /r:System.Web.dll /r:System.Data.dll /r:Microsoft.CSharp.dll ^
    src\Program.cs src\Logger.cs src\PackageInfo.cs src\PackageGridRow.cs ^
    src\StoreClient.cs src\ManifestReader.cs src\DownloadManager.cs src\MainForm.cs
```

### Option C — Visual Studio

1. Create a new **Windows Forms App (.NET Framework)** project targeting **.NET 4.8**
2. Delete the auto-generated `Form1.cs`
3. Add all `src\*.cs` files to the project
4. Add a reference to **System.Web** *(Project → Add Reference → Assemblies)*
5. Build with `Ctrl+Shift+B`

---

## Usage

### Basic workflow

1. Run `MSStoreDownloader.exe`
2. Paste a Store URL or Product ID into the URL field and click **Search**
   - Or just copy a Store URL from your browser — the clipboard monitor will auto-fill and search
3. The grid populates with available packages; the newest version is auto-checked
4. After a few seconds, manifests are read and the correct dependencies are auto-checked
5. Click **Download Selected** — files go to `MSStorePackages\` next to the exe
6. Optionally check **Create installer script** before downloading to get a ready-to-run `.ps1`

### Clipboard Monitor workflow

1. Open `apps.microsoft.com` in Edge or any browser
2. Navigate to any app page
3. Copy the URL (`Ctrl+C` on the address bar)
4. MSStoreDownloader automatically comes to the front and starts searching — no switching needed

### Options reference

| Checkbox | Default | Effect |
|---|---|---|
| Include dependencies | ✅ | Show dependency packages in the grid |
| Clipboard monitor | ✅ | Auto-search when a Store URL is copied |
| Create installer script | ☐ | Generate `Install_<AppName>.ps1` with downloads |
| Allow unsigned | ☐ | Add `-AllowUnsigned` to `Add-AppxPackage` calls |
| Individual app folders | ☐ | Download to `MSStorePackages\<AppName>\` sub-folder |

---

## Project Structure

```
MSStoreDownloader/
├── src/
│   ├── Program.cs          Entry point — STAThread, global exception handlers
│   ├── MainForm.cs         All UI: input, grid, toolbar, log, clipboard monitor
│   ├── Logger.cs           Thread-safe colour-coded RichTextBox logger
│   ├── PackageInfo.cs      Data models: PackageInfo, StoreQueryResult, ManifestDependency
│   ├── PackageGridRow.cs   ViewModel bridging PackageInfo ↔ DataGridView rows
│   ├── StoreClient.cs      HTTP: rg-adguard query, HTML parsing, manifest fetching, size HEAD
│   ├── ManifestReader.cs   ZIP central-directory reader for AppxManifest / AppxBundleManifest
│   └── DownloadManager.cs  Multi-threaded downloader with progress events and cancellation
├── build.bat               One-click csc.exe build script
└── README.md               This file
```

---

## How It Works

### Package retrieval

Microsoft Store has no official public API for direct download URLs. This tool uses the community-maintained **[store.rg-adguard.net](https://store.rg-adguard.net)** service:

```
User input (URL or Product ID)
        │
        ▼ ResolveProductId()
        │  – regex-extracts 9XXXXXXXXXX ID from any Store URL format
        ▼ GetPackages()
        │  – HTTP POST → store.rg-adguard.net/api/GetFiles
        │  – Pre-flight GET to seed session cookie (avoids 403)
        │  – Parses returned HTML table for download links
        ▼ FetchMissingSizes()
        │  – HEAD request per package to get real Content-Length
        ▼ FetchManifests()
           – Reads AppxManifest.xml / AppxBundleManifest.xml via ZIP central directory
           – 2–3 targeted HTTP range requests per package (no full download)
           – Populates ManifestDeps for accurate dependency auto-selection
```

### Manifest reading without full download

APPX/MSIX packages are ZIP files. The ZIP format stores a **Central Directory** at the end of the file. This tool:

1. `HEAD` → get file size
2. Fetch last 64 KB → find End-of-Central-Directory record
3. Fetch Central Directory → locate `AppxManifest.xml` entry and its byte offset
4. Fetch just that entry → decompress → parse XML

This works on 700 MB bundles in under a second. For bundles, it additionally reads the `AppxBundleManifest.xml` to find application sub-packages, then reads the `AppxManifest.xml` from inside those sub-packages (nested ZIP-in-ZIP range requests).

### Download pipeline

```
DownloadManager.StartDownload()
   – CancellationTokenSource per task, queued on ThreadPool
        │
        ▼ (background thread)
        – HttpWebRequest with 80 KB streaming buffer
        – Writes to <filename>.tmp
        – Progress events every 250 ms (speed + percentage)
        – On success: atomic .tmp → final rename
        – On cancel/error: .tmp deleted
        │
        ▼ BeginInvoke() → UI thread updates grid + status bar
```

---

## Disclaimer

This tool uses a third-party community service ([store.rg-adguard.net](https://store.rg-adguard.net)) that reverse-engineers Microsoft's package delivery infrastructure. Download only packages you are licensed to use. The authors are not affiliated with Microsoft and take no responsibility for misuse.
