# StuckAsset

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod for [Resonite](https://resonite.com/) that automatically fixes stuck asset queues.

## What it does

This mod monitors asset gather jobs in Resonite and automatically cleans up jobs that get stuck. It helps prevent the asset queue from getting blocked by:

- **Detecting stuck jobs**: Monitors all asset gather jobs and identifies ones that have been running for too long
- **Owner detection**: Automatically detects when an asset owner leaves the session and immediately fails their pending asset transfers
- **Automatic cleanup**: Fails stuck jobs gracefully so the queue can continue processing
- **Timeout handling**: 
  - Regular asset downloads: 5 minute timeout
  - Session/local asset transfers: 2 minute timeout (or immediately if owner leaves)
- **Background monitoring**: Continuously checks for stuck jobs every 10 seconds

## Why it's needed

Sometimes asset downloads can get stuck due to:
- **User disconnections**: Someone uploads an asset but leaves before the transfer completes, leaving everyone stuck waiting
- **Network issues**: Downloads that hang indefinitely
- **Session problems**: Assets that can't be transferred for various reasons

When this happens, the asset queue can get blocked, preventing new assets from loading. This mod automatically detects and cleans up these stuck jobs so your asset loading can continue normally.

## How it works for everyone

**Important**: Each person needs to have this mod installed for it to work. The mod runs locally on each client/headless server and:

1. **Detects when asset owners leave**: When someone who uploaded an asset leaves the session, the mod immediately detects this and fails their pending asset jobs
2. **Fixes your local queue**: Each person's mod fixes their own stuck asset queue
3. **Works independently**: Even if only some people have the mod, those who do will have their queues fixed automatically

So if you're in a session where someone left mid-upload and everyone is stuck, anyone with this mod installed will have their queue automatically fixed. Others without the mod will need to wait for the timeout or restart.

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

3. The mod will automatically copy to your Resonite `rml_mods` folder if `CopyToMods` is enabled (default). Or manually copy `ExampleMod/bin/Debug/net10.0/StuckAsset.dll` to your `rml_mods` folder.

## Configuration

The mod uses the following default timeouts (hardcoded in the source):
- **Stuck job timeout**: 5 minutes (300 seconds)
- **Session download timeout**: 2 minutes (120 seconds)  
- **Monitor interval**: 10 seconds

These values are optimized for most use cases, but can be adjusted in the source code if needed. To change them, edit the constants in `ExampleMod/ExampleMod.cs`:

```csharp
private static readonly float STUCK_JOB_TIMEOUT_SECONDS = 300f; // 5 minutes
private static readonly float MONITOR_INTERVAL_SECONDS = 10f; // Check every 10 seconds
private static readonly float SESSION_DOWNLOAD_TIMEOUT_SECONDS = 120f; // 2 minutes
```

## How it works

The mod uses Harmony patches to monitor the asset loading system:

1. **Background monitoring task**: Runs continuously, checking all asset gather jobs every 10 seconds
2. **AssetManager.Update patch**: Also checks for stuck jobs during normal asset manager updates
3. **Owner detection**: For `local://` assets, checks if the owner (identified by machine ID) is still in any world
4. **Automatic cleanup**: Uses reflection to call the private `Fail` method on stuck jobs, allowing the queue to continue

## Troubleshooting

### Mod not loading
- Make sure ResoniteModLoader is installed correctly
- Check that `StuckAsset.dll` is in the `rml_mods` folder (not a subfolder)
- Check Resonite logs for any error messages

### Jobs still getting stuck
- The mod may need time to detect stuck jobs (up to 10 seconds)
- Very long-running legitimate downloads may be incorrectly flagged - adjust timeouts if needed
- Check the logs for cleanup messages to see if the mod is working

### False positives
If legitimate downloads are being cleaned up too early, increase the timeout values in the source code and rebuild.

## Contributing

Contributions are welcome! Please feel free to submit issues or pull requests.

## License

This mod is provided as-is. Feel free to use, modify, and distribute as needed.

## Credits

- Created by [troyBORG](https://github.com/troyBORG)
- Built with [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader)
- Uses [Harmony](https://github.com/pardeike/Harmony) for patching
