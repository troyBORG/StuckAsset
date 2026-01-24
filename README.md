# StuckAsset

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod for [Resonite](https://resonite.com/) that automatically fixes stuck asset queues.

## What it does

This mod monitors asset gather jobs in Resonite and automatically handles jobs that get stuck. It helps prevent the asset queue from getting blocked by:

- **Detecting stuck jobs**: Monitors all asset gather jobs and identifies ones that have been running for too long
- **Smart skipping**: Skips stuck jobs instead of failing them permanently, allowing the queue to continue
- **Automatic retry**: Automatically retries skipped assets after a configurable delay
- **Cache management**: Optionally clears cache for persistently stuck assets (after multiple retry attempts)
- **Owner detection**: Attempts to detect when asset owners leave sessions (with safe fallbacks)
- **Background monitoring**: Continuously checks for stuck jobs without impacting performance

## Why it's needed

Sometimes asset downloads can get stuck due to:
- **User disconnections**: Someone uploads an asset but leaves before the transfer completes, leaving everyone stuck waiting
- **Network issues**: Downloads that hang indefinitely
- **Session problems**: Assets that can't be transferred for various reasons

When this happens, the asset queue can get blocked, preventing new assets from loading. This mod automatically detects and handles these stuck jobs so your asset loading can continue normally.

## How it works for everyone

**Important**: Each person needs to have this mod installed for it to work. The mod runs locally on each client/headless server and:

1. **Detects when asset owners leave**: When someone who uploaded an asset leaves the session, the mod can detect this and handle their pending asset jobs
2. **Fixes your local queue**: Each person's mod fixes their own stuck asset queue
3. **Works independently**: Even if only some people have the mod, those who do will have their queues fixed automatically

**This mod can't fix other people's queues; it only fixes the queue on the machine it runs on.** So if you're in a session where someone left mid-upload and everyone is stuck, anyone with this mod installed will have their queue automatically fixed. Others without the mod will need to wait for the timeout or restart.

## Installation

1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) if you haven't already.
2. Download the latest `StuckAsset.dll` from the [Releases](https://github.com/troyBORG/StuckAsset/releases) page.
3. Place `StuckAsset.dll` into your `rml_mods` folder. This folder should be at:
   - **Windows**: `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods`
   - **Linux**: `~/.steam/steam/steamapps/common/Resonite/rml_mods` or `~/.local/share/Steam/steamapps/common/Resonite/rml_mods`
   - You can create it if it's missing, or if you launch the game once with ResoniteModLoader installed it will create this folder for you.
4. Start the game. If you want to verify that the mod is working you can check your Resonite logs for:
   ```
   StuckAsset mod initialized - monitoring asset gather jobs for stuck states
   ```

## Configuration

The mod has extensive configuration options accessible through Resonite's mod configuration menu. All settings can be adjusted in-game without restarting.

### Timeout Settings

- **monitorIntervalSeconds** (default: 10s) - How often to check for stuck jobs
- **remoteTimeoutSeconds** (default: 240s / 4 minutes) - Timeout for remote asset downloads
- **localTimeoutSeconds** (default: 90s / 1.5 minutes) - Timeout for local/session asset transfers
- **noProgressTimeoutSeconds** (default: 60s) - Timeout for jobs with no progress. A job must exceed the main timeout AND show no progress for this duration to be considered stuck

### Retry Behavior

- **retryDelaySeconds** (default: 45s) - Delay before retrying a skipped asset
- **retryDelayOwnerLeftSeconds** (default: 300s / 5 minutes) - Delay before retrying when owner left
- **retryCheckIntervalSeconds** (default: 20s) - How often to check the retry queue
- **retryPriority** (default: 0.1) - Priority for retried assets (0.0-1.0, lower = less priority)

### Safety Controls

- **maxRetriesPerAsset** (default: 3) - Maximum retry attempts per asset before giving up
- **maxRetryQueueSize** (default: 250) - Maximum size of retry queue
- **cooldownPerAssetSeconds** (default: 120s / 2 minutes) - Minimum cooldown before same asset can be retried again

### Feature Toggles

- **enabled** (default: true) - Enable/disable the mod
- **clearCacheOnSkip** (default: false) - Clear asset cache when skipping (can cause re-downloads)
- **cancelJobOnSkip** (default: true) - Cancel jobs when skipping (vs failing them)
- **onlyAffectLocalAssets** (default: false) - Only process local:// assets (debug mode)

### Logging Options

- **logStuckDetections** (default: true) - Log when stuck jobs are detected
- **logRetries** (default: true) - Log when assets are retried
- **logCacheClears** (default: false) - Log when cache is cleared
- **logVerboseDebug** (default: false) - Enable verbose debug logging

### Statistics (Read-Only)

- **showStats** (default: true) - Show mod statistics in config
- **statsTotalDetected** - Total stuck jobs detected since startup
- **statsTotalSkipped** - Total jobs skipped
- **statsTotalRetried** - Total jobs retried
- **statsRetryQueueSize** - Current retry queue size
- **statsActiveJobs** - Current active asset jobs being monitored

## How it works

The mod uses a background monitoring task (not frame-based patches) to minimize performance impact:

1. **Background monitoring**: Runs continuously, checking all asset gather jobs at configurable intervals
2. **Progress-aware stuck detection**: 
   - Tracks bytes received for each job
   - Only marks jobs as stuck if they exceed timeout AND show no progress
   - Jobs that are slow but actively downloading are NOT considered stuck
3. **Smart skipping**: Cancels stuck jobs and adds them to a retry queue instead of failing them permanently
4. **Progressive cache clearing**: 
   - 1st skip → cancel only (no cache clear)
   - 2nd+ skip → cancel + clear cache + retry
5. **Retry management**: Processes retry queue with cooldowns and retry limits to prevent infinite loops
6. **Owner detection**: Attempts to detect when asset owners leave, with safe fallbacks to avoid false positives

## Building from source

1. Clone this repository:
   ```bash
   git clone https://github.com/troyBORG/StuckAsset.git
   cd StuckAsset
   ```

2. Build the project:
   ```bash
   dotnet build
   ```

3. The mod will automatically copy to your Resonite `rml_mods` folder if `CopyToMods` is enabled (default). Or manually copy `StuckAsset/bin/Debug/net10.0/StuckAsset.dll` to your `rml_mods` folder.

## Troubleshooting

### Mod not loading
- Make sure ResoniteModLoader is installed correctly
- Check that `StuckAsset.dll` is in the `rml_mods` folder (not a subfolder)
- Check Resonite logs for any error messages
- Verify the mod is enabled in config (`enabled` setting)

### Jobs still getting stuck
- The mod may need time to detect stuck jobs (check `monitorIntervalSeconds` setting)
- Very long-running legitimate downloads may be incorrectly flagged - adjust timeout values if needed
- Check the logs for cleanup messages to see if the mod is working
- Verify `enabled` is set to `true` in config

### False positives
If legitimate downloads are being cleaned up too early:
- Increase `remoteTimeoutSeconds` or `localTimeoutSeconds` in config
- Check `logStuckDetections` to see what's being detected
- Consider enabling `logVerboseDebug` for more information

### Too many retries
If assets are being retried too aggressively:
- Reduce `maxRetriesPerAsset` (default: 3)
- Increase `cooldownPerAssetSeconds` (default: 120s)
- Increase `retryDelaySeconds` (default: 45s)

### Cache being cleared too often
- Set `clearCacheOnSkip` to `false` (default)
- Cache will only be cleared after 2+ retry attempts even if enabled

## Performance

The mod is designed to be lightweight:
- Uses background tasks instead of frame-based patches
- Configurable monitoring intervals (default: 10 seconds)
- Minimal overhead when no stuck jobs are detected
- Statistics update every 10 seconds (not every frame)

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

## License

This mod is provided as-is. Feel free to use, modify, and distribute as needed.

## Credits

- Created by [troyBORG](https://github.com/troyBORG)
- Built with [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)
- Uses [Harmony](https://github.com/pardeike/Harmony) for patching

## Changelog

### v1.1.0
- Complete rewrite with extensive configuration options
- Removed double-monitoring (performance improvement)
- Added retry limits and safety controls
- Made cache clearing optional and progressive
- Added comprehensive logging options
- Improved owner-left detection with safe fallbacks
- All timers now configurable
- Added statistics tracking

### v1.0.0
- Initial release
- Basic stuck job detection and cleanup
- Automatic retry system
- Cache clearing for skipped assets
