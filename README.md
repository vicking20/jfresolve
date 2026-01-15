<p align="center">
  <img src="https://raw.githubusercontent.com/vicking20/jfresolve/main/jfresolve.png" alt="Jfresolve Logo" width="128" height="128">
</p>

<h1 align="center">Jfresolve - Jellyfin Plugin</h1>

A Jellyfin plugin that integrates external streaming sources (Stremio addons) from a debrid provider with your Jellyfin library, enabling on-demand content discovery and streaming.

<p align="center">
  <a href="https://ko-fi.com/vicking20" target="_blank">
    <img src="https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me A Coffee">
  </a>
  <a href="https://discord.gg/hPz3qn72Ue" target="_blank">
    <img src="https://img.shields.io/badge/Chat%20on%20Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord">
  </a>
  <a href="https://raw.githubusercontent.com/vicking20/jfresolve/refs/heads/main/repository.json" target="_blank">
    <img src="https://img.shields.io/badge/Add%20to%20Jellyfin-13B5EA?style=for-the-badge&logo=jellyfin&logoColor=white" alt="Jellyfin Repo">
  </a>
</p>

Similar project: If you aren't interested in using the plugin, you can instead use the webapp: [**JF-Resolve**](https://github.com/vicking20/jf-resolve) directly with Jellyfin.

## Features

- **External Search Results**: Search TMDB and display results from external streaming sources.
- **Library Population**: Automatically populate your Jellyfin library with popular and trending content from TMDB.
- **Movie & Series Support**: Full support for movies, TV series, and anime.
- **Anime Categorization**: Optional dedicated anime library with automatic genre-based routing.
- **FFmpeg Tuning**: Configurable FFmpeg settings for better remote stream detection.
- **Scheduled Tasks**: Automate library population, series updates, and content purging.
- **Flexible Configuration**: Comprehensive settings for customization.
- **Preferred Quality Selection**: Choose your preferred stream quality (4K, 1080p, etc.) and the plugin will automatically select the best stream for you.
- **Failover System**: EXPERIMENTAL: Automatic retry for dead links with configurable time windows - prevents repeated failures on the same dead stream.

## Benefits

- **Efficient Library Management**: Simplify your media library with automated population and external search results.
- **Enhanced User Experience**: Discover new content directly through your Jellyfin UI.
- **Customizable Settings**: Tailor your plugin to your preferences with flexible configuration options.
- **Less dependency on the arr stack**: You can use jfresolve now without jellyseerr, radarr, sonarr, prowlarr,etc. Just your tmdb api key, debrid authentication, and some stremio streaming addon manifest link.
- **Smaller file footprint**: Media is not stored directly, media is streamed from the source, you dont need to have tons of storage to have a large library.

## Versions

### **jfresolve-10.11** (Current)
- **Target**: Jellyfin 10.11+
- **Framework**: .NET 9.0
- **Status**: Active development
- **Latest Release**: See [Releases](../../releases)

## Installation

1. Add the link to the plugin to your Jellyfin server's plugin repository: `https://raw.githubusercontent.com/<YOUR_USERNAME>/<YOUR_REPO>/main/repository.json`
   (Replace `<YOUR_USERNAME>` and `<YOUR_REPO>` with your own)
2. Install and configure your plugin. Tested with Torrentio, TorrentioRD, Aiostreams, MediaFusion. Your plugin needs to have your real debrid key setup.
3. During the first time configuration or after adding a new library path, after saving your settings, you should restart Jellyfin, then trigger a library refresh for changes to take effect.
4. If you have used an older version of Jfresolve older than 1.0.0.3, you need to uninstall the older version.
## Configuration

### Required Settings

- **TMDb API Key**: Get your free API key at [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api).
- **Jellyfin Base URL**: Your Jellyfin server URL (e.g., `http://127.0.0.1:8096`).
- **Addon Manifest URL**: Stremio addon manifest URL (required for streaming).
  **Sample Add-on url** ```stremio://torrentio.strem.fun/providers=yts,eztv,rarbg,1337x,thepiratebay,kickasstorrents,torrentgalaxy,magnetdl,horriblesubs,nyaasi,tokyotosho,anidex|qualityfilter=brremux,scr,cam|limit=1|debridoptions=nodownloadlinks,nocatalog|realdebrid=(input your real debrid key here with no brackets)/manifest.json```
  The sample add-on url can be used in your configuration, replace **(input your real debrid key here with no brackets)** with your real debrid key and paste into the plugin settings Addon link (Manifest JSON URL).
- **Debrid Account**: Tested with Real Debrid, other providers can be tested. Debrid provider is configured in your stremio plugin settings.
- **Library Paths**: At least one library path (Movies or Shows).

### Optional Settings

- **Enable Search Interception**: When enabled, search queries will return results from external search provider.
- **Preferred Stream Quality**: Select preferred quality when multiple stream options are available. Auto will select the highest quality stream.
- **Search Result Limit**: Maximum number of results to return from TMDB searches.
- **Unreleased Buffer Days**: Number of days before official release date to consider content as "released".
- **Enable Separate Anime Folder**: When enabled, anime shows (TMDB genre ID 16) will be added to a separate anime folder instead of the main series folder.
- **Enable Auto Library Population**: Automatically populate your library with trending/popular/top rated content.
- **Items Per Run**: Maximum number of new items to add each time the population task runs.
- **Enable Custom FFmpeg Settings**: When disabled, Jellyfin's default FFmpeg settings will be used. Enable this to customize probe and analyze settings for better stream detection.
- **Enable Movie Failover**: Automatically retry failed movie streams with alternative quality versions.
- **Enable Show Failover**: Automatically retry failed TV show streams with alternative quality versions.
- **Failover Grace Period**: Time in seconds to wait before retrying a failed stream (prevents immediate retry spam).
- **Failover Window**: Time in seconds during which a failed stream won't be retried again (prevents continuous failures).

## Scheduled Tasks

Jfresolve comes with three scheduled tasks to automate your library management:

- **Populate Jfresolve Library**: Automatically populates your library with trending/popular content from TMDB.
- **Update Jfresolve Series**: Keeps your TV series up-to-date with new seasons/episodes.
- **Clear All Jfresolve Items**: Removes all items added by Jfresolve from your library.

To configure the scheduled tasks, go to **Dashboard → Scheduled Tasks** in your Jellyfin server.

## Releases on your Fork

To release a new version from your fork:

1. Update the version in `jfresolve-10.11/Jfresolve.csproj`.
2. Commit your changes.
3. Push a tag starting with `v` (e.g., `v1.0.0.6`).
   ```bash
   git tag v1.0.0.6
   git push origin v1.0.0.6
   ```
4. A GitHub Action will automatically:
   - Build the plugin.
   - Create a release with `jfresolve.zip`.
   - Update `repository.json` with the new version and your repository URLs.

## Building from Source (Manual)

### Prerequisites

- .NET 9.0 SDK

### Build Steps

```bash
cd jfresolve-10.11
dotnet build -c Release
```

Compiled DLL will be at:
- `jfresolve-10.11/bin/Release/net9.0/Jfresolve.dll`

## Troubleshooting

### Plugin doesn't show in Dashboard

- Ensure correct version for your Jellyfin release.
- Check plugin folder permissions.
- Restart Jellyfin after installation.

### Search returns no results

- Verify TMDb API key is valid.
- Check internet connectivity.
- Enable **Enable Search Interception** in config.

### Library population not working

- Check TMDb API key configuration.
- Verify library paths exist and are in Jellyfin.
- Check logs for specific error messages.
- Ensure at least one library path is configured.

### Streams not playing

- Verify addon manifest URL is correct.
- Check Jellyfin FFmpeg configuration.
- Ensure Debrid is authorized in your plugin.
- Check Jellyfin logs for stream resolution errors.

## Contributing

Contributions are welcome! Please:

1. Fork the repository.
2. Create a feature branch.
3. Commit changes.
4. Push to branch.
5. Open a Pull Request.

## Acknowledgments

- This plugin was only possible after going through [Gelato](https://github.com/lostb1t/Gelato). Big thanks to [lostb1t](https://github.com/lostb1t).
- My old project [jf-resolve](https://github.com/vicking20/jf-resolve).
- Jellyfin project for the media server.
- TMDB for metadata.
- Stremio for addon ecosystem.
- All users.

## Support

- **Issues**: Report bugs on [GitHub Issues](../../issues).
- **Discussions**: Ask questions in [GitHub Discussions](../../discussions).
- **Jellyfin Forum**: Check the [Jellyfin Community](https://jellyfin.org/docs/general/community/).

## Disclaimer

This project is intended for **educational purposes only**. It was developed to learn more about the c# language and understand how to inject custom results when a user does a search in Jellyfin using Dto's. While it was a fun experiment, it is provided as-is, and others are welcome to modify or use it for their own educational purposes at their risk.
