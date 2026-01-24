# StuckAsset

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod for [Resonite](https://resonite.com/) that fixes stuck asset queues.

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

1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Place `StuckAsset.dll` into your `rml_mods` folder. This folder should be at:
   - Windows: `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods`
   - Linux: `~/.steam/steam/steamapps/common/Resonite/rml_mods`
   - You can create it if it's missing, or if you launch the game once with ResoniteModLoader installed it will create this folder for you.
3. Start the game. If you want to verify that the mod is working you can check your Resonite logs for "StuckAsset mod initialized" message.

## Configuration

The mod uses the following default timeouts (hardcoded):
- **Stuck job timeout**: 5 minutes (300 seconds)
- **Session download timeout**: 2 minutes (120 seconds)  
- **Monitor interval**: 10 seconds

These values are optimized for most use cases, but can be adjusted in the source code if needed.

## Building from source

1. Clone this repository
2. Open the solution in your IDE
3. Build the project - it will automatically copy to your Resonite mods folder if `CopyToMods` is enabled
4. Or manually copy `ExampleMod/bin/Debug/net10.0/ExampleMod.dll` to your `rml_mods` folder and rename it to `StuckAsset.dll`
