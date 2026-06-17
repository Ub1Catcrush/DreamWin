# DreamWin Update Feature

This document describes the automatic update checking and installation feature added to DreamWin.

## Overview

The update feature allows DreamWin to:
- Check GitHub releases for new versions
- Compare versions to determine if an update is available
- Download the latest installer
- Launch the installer for user-guided installation
- Track update preferences and check history

## Architecture

### Components

#### UpdateService (`Services/UpdateService.cs`)

The core service responsible for update operations:

- **CheckForUpdatesAsync()** - Queries GitHub API for the latest release
  - Returns `GitHubRelease` object if a newer version is available
  - Filters out pre-releases unless explicitly enabled
  - Looks for `.exe` or `.msi` installer files in release assets

- **DownloadUpdateAsync()** - Downloads the installer from GitHub
  - Streams the download to avoid memory issues with large files
  - Reports download progress via `UpdateProgressChanged` event
  - Saves installer to `%AppData%/DreamWin/updates/` by default

- **InstallUpdateAsync()** - Launches the installer
  - Supports both `.exe` and `.msi` installers
  - For `.msi` files, uses `msiexec.exe /i /passive` for quiet installation
  - Does not wait for installer completion (allows app to be replaced while running)

- **PerformAutoUpdateAsync()** - Convenience method combining all steps

**Version Comparison**
- Supports semantic versioning (e.g., "1.0.0", "v1.0.0")
- Handles variable-length version strings
- Compares major.minor.patch components numerically

**Events**
- `UpdateAvailable` - Raised when a new version is found
- `UpdateProgressChanged` - Raised during checking/downloading with status messages
- `UpdateError` - Raised when an error occurs
- `UpdateCompleted` - Raised when update installation starts

### MainViewModel Updates

New observable properties:
- `UpdateStatus` - Displays current update operation status
- `UpdateAvailable` - Boolean indicating if an update is ready to install
- `LatestRelease` - Stores the `GitHubRelease` object

New relay commands:
- `CheckForUpdatesCommand` - Manually check for updates
- `InstallUpdateCommand` - Download and install available update

Auto-check on startup:
- Enabled if `CheckForUpdates` setting is true
- Only runs if 24+ hours have passed since last check
- Runs in background without blocking initialization

### AppSettings Model

New properties to track update preferences:
```csharp
public bool CheckForUpdates { get; set; } = true;          // Auto-check enabled
public bool IncludePrereleases { get; set; } = false;       // Include beta/alpha versions
public DateTime LastUpdateCheckTime { get; set; } = DateTime.MinValue;  // Track check frequency
public string? SkippedUpdateVersion { get; set; }           // For future "skip version" feature
```

### UI Components

#### Settings View Updates

New "UPDATES" section in `Views/SettingsView.xaml`:
- "Check for Updates" button - Manually trigger version check
- Update status display
- "Install Update" button - Visible only when update available
- Toggle: Auto-check for updates
- Toggle: Include pre-release versions
- Event handler saves settings when preferences change

## How It Works

### First-Time User
1. On app startup, if `CheckForUpdates` is enabled (default), the app checks GitHub
2. If an update is available and 24+ hours since last check, user sees status
3. User can click "Install Update" in Settings view
4. Installer downloads to temp folder
5. Installer launches, user completes installation
6. Upon installer completion, old app is replaced with new version

### Manual Check
1. User goes to Settings → Updates → "Check for Updates"
2. Status updates as check progresses
3. If update available, "Install Update" button appears
4. User can proceed with installation

### Preferences
- **Auto-check**: Disabled means GitHub is never queried (except manual checks)
- **Pre-releases**: When enabled, includes alpha/beta versions in update checks
- Preferences are saved to `%AppData%/DreamWin/settings.json`

## GitHub Integration

**Repository**: `https://github.com/Ub1Catcrush/DreamWin`

**API Endpoint**: `https://api.github.com/repos/Ub1Catcrush/DreamWin/releases`

**Release Assets Expected**:
- Should include `.exe` or `.msi` installer file
- Service automatically selects first installer found
- Release tag should follow semantic versioning (e.g., `v1.0.0`)

**Rate Limiting**: GitHub API allows 60 unauthenticated requests per hour

## Implementation Details

### Version Extraction
- Current version hardcoded in `App.xaml.cs` as `"1.0.0"`
- Should be updated to read from `DreamWin.csproj` version property or `version.properties` file
- Version used for comparison with GitHub releases

### Installer Selection
- Looks for first `.exe` or `.msi` file in release assets
- If no installer found, update is skipped
- Download happens to user's `%TEMP%` folder under `DreamWin/updates/`

### Error Handling
- All exceptions caught and reported via `UpdateError` event
- Errors logged to Debug output
- UI remains responsive on failure

## Future Enhancements

Potential improvements:
1. **Version Property Binding** - Read version from project file instead of hardcoding
2. **Skip Version** - Allow users to skip a specific version
3. **Scheduled Checks** - Run checks on a timer instead of just at startup
4. **Progress Dialog** - Show download progress in a modal
5. **Changelog Display** - Show release notes before installing
6. **Automatic Installation** - Install without user interaction (admin required)
7. **Rollback** - Keep previous version as backup
8. **Signed Releases** - Verify installer signature before running
9. **GitHub Authentication** - Use token to increase API rate limit
10. **Check in Background** - Periodic checks without blocking UI

## Testing

To test the update feature:

1. **Mock Release Check**:
   ```csharp
   var updateService = App.UpdateService;
   var release = await updateService.CheckForUpdatesAsync();
   ```

2. **Manual UI Test**:
   - Go to Settings → Updates
   - Click "Check for Updates"
   - Verify status messages update
   - Toggle checkboxes and verify settings save

3. **Check Settings Persistence**:
   ```
   %AppData%/DreamWin/settings.json
   ```
   Should contain update preferences and last check time

## API Usage Notes

- Single HttpClient instance reused for all requests
- User-Agent header set to "DreamWin-UpdateChecker"
- Streaming downloads prevent large file memory issues
- Async/await pattern used throughout for UI responsiveness
- All events are raised on the calling thread (respects UI thread requirements)

## Troubleshooting

**Update not found when available**:
- Check GitHub repo for published releases
- Verify release has `.exe` or `.msi` asset
- Check if running pre-release build with pre-release filter off

**Installer won't launch**:
- Verify download completed successfully
- Check Windows Defender hasn't quarantined installer
- Verify installer file isn't corrupted (check temp folder)

**Can't connect to GitHub**:
- Check internet connection
- Verify GitHub is accessible (not blocked by firewall/proxy)
- Check GitHub API status

## Files Modified

- `App.xaml.cs` - Added UpdateService initialization
- `ViewModels/MainViewModel.cs` - Added update commands and auto-check
- `Models/AppSettings.cs` - Added update preference properties
- `Views/SettingsView.xaml` - Added Updates UI section
- `Views/SettingsView.xaml.cs` - Added settings save on update preferences

## Files Created

- `Services/UpdateService.cs` - Main update service implementation
- `UPDATE_FEATURE.md` - This documentation file
