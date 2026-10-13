# Jellyfin Continue Watching Tweaks Plugin

A Jellyfin plugin that merges **Continue Watching** and **Next Up** into a single list, along with a number of configuration options.

Built on work from [italohdc/Jellyfin-NextUpMerge-Plugin.](https://github.com/italohdc/Jellyfin-NextUpMerge-Plugin).

## About this plugin

There's a reported issue on Infuse where the _Watching_ shelf will show TV Shows next episodes when using Plex, but not when using Jellyfin as a share. That happens because Plex returns a combined list of in-progress and next-up items from its API, while Jellyfin keeps them separate.

This plugin creates a middleware on Jellyfin that merges the _Continue Watching_ and _Next Up_ on a single endpoint, so Infuse shows both in its top shelf.

### More on the issue

This has been a known issue and requested feature on Firecore community:

- [Firecore Community - Jellyfin Merge Continue Watching and Up Next](https://community.firecore.com/t/jellyfin-merge-continue-watching-and-up-next/39898)
- [Firecore Community - Infuse, PMS and Jellyfin: “continue watching” only for PMS?](https://community.firecore.com/t/infuse-pms-and-jellyfin-continue-watching-only-for-pms/59923)

As this new feature is not yet implemented on Jellyfin or Infuse, this plugin is a **temporary workaround** to get the same experience as on Plex. As it is a PoC, Claude was used to help develop it.


## How to use this plugin

### Compatibility

- **Jellyfin 12**

### Option 1: Install in Jellyfin admin dashboard

1. Navigate to your Jellyfin admin dashboard
2. Open the Plugins page
3. Click "Manage Repositories" then "+ New Repository"
4. Enter the following:
  * Name: `Continue Watching`
  * URL: `https://raw.githubusercontent.com/dyancat/jellyfin-continue-watching-tweaks/refs/heads/main/manifest.json`
5. Go back to Plugins and filter to "All" or "Available"
6. Find and install the "Continue Watching Tweaks" plugin
7. Restart Jellyfin.
8. It should be configured to work by default, otherwise you can open the plugin settings and configure it as needed.

### Option 2: Install from pre-built releases

The compiled plugin files are available on the [releases page](https://github.com/dyancat/jellyfin-continue-watching-tweaks/releases). You need to download the latest release and extract its content into Jellyfin plugins folder.

In the end, for it to work, there must be a `.dll` file as in:
`plugins/ContinueWatchingTweaks_1.0.0.0/Jellyfin.Plugin.ContinueWatchingTweaks.dll`.

### Option 3: Build from source

#### Build `.dll` files from source

To build this plugin, you need [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) installed. The `build.sh` script will compile the plugin files into `dist/` folder.

```sh
# Install .NET 10 SDK
## macOS (Homebrew):
brew install --cask dotnet-sdk10

# Build plugin .dll files
./build.sh
```

#### Add plugin to Jellyfin

In your Jellyfin config directory, create a new folder `ContinueWatchingTweaks_1.0.0.0` and copy the content of `dist/` into it.

In the end, for it to work, there must be a `.dll` file as in:
`plugins/ContinueWatchingTweaks_1.0.0.0/Jellyfin.Plugin.ContinueWatchingTweaks.dll`.

Jellyfin must be restarted after copying the plugin files for it to be loaded.
