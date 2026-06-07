# Microsoft Store Package Downloader

> A Windows desktop utility that retrieves direct download links for Microsoft Store packages and handles the full download + installation workflow — no browser extension, no Python, no admin rights to download.

Built with **WinForms / .NET Framework 4.8** — runs on any Windows 10/11 machine without installing anything extra.


<img width="1136" height="743" alt="Screen2" src="https://github.com/user-attachments/assets/1ec993ea-c49e-4123-9c82-2629de585b84" />
<img width="1136" height="743" alt="Screen1" src="https://github.com/user-attachments/assets/8fff59fd-06bc-4a55-9157-2f4b7b56279d" />

---
## Features

### 📦 Package Retrieval
- Accepts any **Microsoft Store URL** (`apps.microsoft.com/detail/...`, `microsoft.com/en-us/p/...`) or a bare **Product ID** (e.g. `9NBLGGH4NNS1`)
- Queries [store.rg-adguard.net](https://store.rg-adguard.net) to obtain direct CDN download links
- Supports all release **rings**: Retail, Release Preview, Slow, Fast — selectable from the toolbar (defaults to Release Preview)
- Reads the real file size via HTTP `HEAD` for every package (rg-adguard reports placeholder sizes)
- Identifies and separates **main packages** from **framework dependencies**
- **Encrypted packages** (`.eappx`, `.eappxbundle`) are automatically de-preferred and greyed out when a plain equivalent exists

### 🔍 Smart Dependency Selection
- After listing packages, downloads and parses each bundle's embedded **`AppxManifest.xml`** / **`AppxBundleManifest.xml`** via targeted HTTP range requests — no full-file download needed
- Uses real `<PackageDependency>` declarations to auto-check exactly the right dependency variants
- Respects the active **architecture filter** when selecting dependency variants (e.g. filter = x64 → selects only x64 + neutral variants)
- Falls back to name/architecture heuristics when manifest data is unavailable
- Changing the **arch filter** or **ring** while a product is loaded automatically re-runs the full search

### 📋 Clipboard Monitor
- Runs silently in the background (toggleable; **on by default**)
- Every 800 ms, checks the clipboard for Microsoft Store URLs or bare Product IDs
- When a match is found:
  - Brings the window to front (restores from minimised if needed)
  - Fills the URL field automatically
  - **Triggers a search immediately** — no manual paste or click required
- Supports all Store URL formats:
  - `https://apps.microsoft.com/detail/9XXXXXXXX`
  - `https://www.microsoft.com/en-us/p/app-name/9XXXXXXXX`
  - Bare Product ID: `9XXXXXXXX`

### ⚡ Auto-Download
- Optional mode (requires Clipboard monitor to be enabled)
- When active, automatically starts downloading all checked packages as soon as the manifest reading phase completes — after dependency selection has been finalised
- Fully hands-free: copy a Store URL → window appears → search runs → dependencies resolved → downloads begin

### ⬇️ Downloads
- All files go to **`MSStorePackages\`** next to the `.exe` — no folder dialog, no prompts
- **Individual app folders** option: each app gets its own `MSStorePackages\<AppName>\` sub-folder
- **Skips already-downloaded** files (compares size on disk to reported file size)
- Parallel downloads with per-file progress (speed, percentage, status column)
- Cancel individual or all downloads
- Files written as `.tmp` then atomically renamed — no partial files left on failure

### 📝 Installer Script Generation
- Optionally generates a **PowerShell `.ps1` install script** alongside the downloaded files
- Script named after the app: `Install_<PackageName>.ps1`
- Enforced installation order:
  1. `.NET Native` runtime & framework
  2. `VCLibs` (Visual C++ redistributables)
  3. `WindowsAppRuntime`
  4. `UI.Xaml`
  5. Store engagement SDK
  6. Main application
- **Allow unsigned** option adds `-AllowUnsigned` flag (for developer/sideloaded packages)
- Run with: `PowerShell -ExecutionPolicy Bypass -File "Install_AppName.ps1"`

### 🎛️ UI & Settings
- Dark-themed resizable window
- Custom painted column header strip (themed to match the dark UI)
- Architecture filter and Ring selector in the toolbar — changing either re-runs search automatically
- Auto-selects newest version of each main package on load
- Colour-coded toolbar: green = download, red = cancel, blue = copy
- Colour-coded output log (Info / OK / Warning / Debug) with save-to-file
- **All checkbox states persist** across sessions via the Windows registry (`HKCU\Software\MSStoreDownloader`)
- Debug log checkbox to toggle verbose output without restarting

---

## Options Reference

| Checkbox | Default | Effect |
|---|---|---|
| Include dependencies | ✅ | Show and auto-select dependency packages |
| Clipboard monitor | ✅ | Watch clipboard; auto-search on Store URL copy |
| Auto-download | ☐ | After clipboard search completes, start downloads automatically *(requires Clipboard monitor)* |
| Create installer script | ☐ | Generate `Install_<AppName>.ps1` with downloads |
| Allow unsigned | ☐ | Add `-AllowUnsigned` to `Add-AppxPackage` calls |
| Individual app folders | ☐ | Download to `MSStorePackages\<AppName>\` sub-folder |
| Debug log | ☐ | Show verbose debug entries in the output log |

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
2. Paste a Store URL or Product ID and click **Search**  
   — or just copy a Store URL from any browser, the clipboard monitor auto-fills and searches
3. The grid populates; the newest version of each package is auto-checked
4. Manifests are read in the background; the correct dependencies are auto-checked when done
5. Click **Download Selected** — files go to `MSStorePackages\` next to the exe
6. Optionally check **Create installer script** before downloading to get a ready-to-run `.ps1`

### Fully automated workflow

1. Enable **Clipboard monitor** and **Auto-download**
2. Copy any Store URL from your browser
3. The downloader appears, searches, resolves dependencies, and starts downloading — entirely automatically

### Clipboard monitor workflow (semi-auto)

1. Browse `apps.microsoft.com` in any browser
2. Copy the URL of any app page (`Ctrl+C` on the address bar)
3. The downloader comes to front and searches automatically
4. Review the selection, then click Download

---

## Project Structure

```
MSStoreDownloader/
├── src/
│   ├── Program.cs          Entry point — STAThread, global exception handlers
│   ├── MainForm.cs         UI, grid, toolbar, clipboard monitor, installer generation
│   ├── Logger.cs           Thread-safe colour-coded RichTextBox logger (DebugEnabled toggle)
│   ├── PackageInfo.cs      Data models: PackageInfo, StoreQueryResult, ManifestDependency
│   ├── PackageGridRow.cs   ViewModel bridging PackageInfo ↔ DataGridView rows
│   ├── StoreClient.cs      HTTP: rg-adguard query, HTML parsing, manifest + size fetching
│   ├── ManifestReader.cs   ZIP range-reader for AppxManifest / AppxBundleManifest
│   └── DownloadManager.cs  Parallel downloader with progress events and cancellation
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
        ▼ GetPackages(productId, ring)
        │  – HTTP POST → store.rg-adguard.net/api/GetFiles
        │  – Pre-flight GET to seed session cookie (avoids 403)
        │  – Parses returned HTML table for download links
        │  – ring parameter: Retail / RP / Slow / Fast
        ▼ FetchMissingSizes()
        │  – HEAD request per package to get real Content-Length
        ▼ FetchManifests()
           – Reads AppxManifest.xml / AppxBundleManifest.xml via ZIP central directory
           – 2–3 targeted HTTP range requests per package (no full download)
           – For bundles: reads the bundle manifest to find sub-packages,
             then reads AppxManifest.xml from inside each sub-package (nested ZIP)
           – Populates ManifestDeps for accurate dependency auto-selection
```

### Manifest reading without full download

APPX/MSIX packages are ZIP files. The ZIP format stores a **Central Directory** at the end of the file. This tool:

1. `HEAD` → get file size
2. Fetch last 64 KB → find End-of-Central-Directory record (including ZIP64)
3. Fetch Central Directory → locate `AppxManifest.xml` entry and its byte offset
4. Fetch just that entry → decompress → parse `<PackageDependency>` XML

This works on 700 MB bundles in under a second. For bundles, it additionally reads the `AppxBundleManifest.xml` to find application sub-packages (handling both schema v1/2013 without `Type` attribute, and v4+/2016 with `Type="application"`), then reads the inner `AppxManifest.xml` from each sub-package as a nested ZIP-in-ZIP range request.

### Download pipeline

```
DownloadManager.StartDownload()
   – CancellationTokenSource per task, queued on ThreadPool
        │
        ▼ (background thread)
        – HttpWebRequest with 80 KB streaming buffer
        – Writes to <filename>.tmp
        – Progress events every 250 ms (speed + percentage)
        – On success: atomic .tmp → final rename (overwrites existing)
        – On cancel/error: .tmp deleted
        │
        ▼ BeginInvoke() → UI thread updates grid + status bar
```

### Registry storage

Settings stored at `HKCU\Software\MSStoreDownloader`:

| Value | Type | Default |
|---|---|---|
| `IncludeDeps` | DWORD | 1 |
| `ClipboardMonitor` | DWORD | 1 |
| `AutoDownload` | DWORD | 0 |
| `CreateInstaller` | DWORD | 0 |
| `AllowUnsigned` | DWORD | 0 |
| `IndivFolders` | DWORD | 0 |
| `DebugLog` | DWORD | 0 |

---

## Disclaimer

This tool uses a third-party community service ([store.rg-adguard.net](https://store.rg-adguard.net)) that reverse-engineers Microsoft's package delivery infrastructure. Download only packages you are licensed to use. The authors are not affiliated with Microsoft and take no responsibility for misuse.
