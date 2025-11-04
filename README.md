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

## Features

- 🔍 **External Search Results**: Search TMDB and display results from external streaming sources
- 📚 **Library Population**: Automatically populate your Jellyfin library with popular content from TMDB
- 🎬 **Movie & Series Support**: Full support for movies, TV series, and anime
- 🎌 **Anime Categorization**: Optional dedicated anime library with automatic genre-based routing
- 🎨 **FFmpeg Tuning**: Configurable FFmpeg settings for better remote stream detection
- ⏰ **Scheduled Population**: Daily automatic library population at 3 AM UTC
- 🔧 **Flexible Configuration**: Comprehensive settings for customization

## Versions

### **jfresolve-10.11** (Current)
- **Target**: Jellyfin 10.11+
- **Framework**: .NET 9.0
- **Status**: Active development
- **Latest Release**: See [Releases](../../releases)

## Installation

1. Add the link to the plugin to your Jellyfin server's plugin repository: [https://raw.githubusercontent.com/vicking20/jfresolve/refs/heads/main/repository.json](https://raw.githubusercontent.com/vicking20/jfresolve/refs/heads/main/repository.json)
2. Install and configure your plugin. Tested with Torrentio, TorrentioRD, Aiostreams, MediaFusion. Your plugin needs to have your real debrid key setup.

## Configuration

### Required Settings

- **TMDb API Key**: Get your free API key at [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api)
- **Jellyfin Base URL**: Your Jellyfin server URL (e.g., `http://127.0.0.1:8096`)
- **Addon Link**: Stremio addon manifest URL (required for streaming)
- **Debrid Account**: Tested with Real Debrid, I cant guarantee others, but you can try. Debrid provider is configured in your stremio plugin settings.
- **Library Paths**: At least one library path (Movies or Shows)

### Optional Settings

- **Search Results**: Number of external results to show
- **Release Buffer Days**: Days to wait before showing new releases
- **Include Anime Path**: Separate library for anime content
- **Enable Library Population**: Automatic population of popular content at 3 AM UTC
- **Items Per Request**: How many items to fetch per population run
- **FFmpeg Customization**: Custom FFmpeg settings for stream detection

## Building from Source

### Prerequisites

- .NET 9.0 SDK

### Build Steps

```bash
cd jfresolve-10.11
dotnet build -c Release
```

Compiled DLL will be at:
- `jfresolve-10.11/bin/Release/net9.0/Jfresolve.dll`

## Architecture

### Core Components

- **Plugin.cs**: Main plugin entry point and configuration
- **ServiceRegistrator.cs**: Dependency injection setup
- **Provider/JfresolveProvider.cs**: TMDB search functionality
- **Provider/StrmFileGenerator.cs**: STRM file creation for library items
- **Populator/JfresolvePopulator.cs**: Library population logic
- **Manager/JfresolveManager.cs**: Scheduled population scheduler
- **Filters/**: MVC action filters for intercepting Jellyfin requests
- **Utilities/**: Helper classes for common operations

### Key Features

- **STRM Files**: Text files containing stream URLs for Jellyfin to play
- **Library Caching**: Metadata caching for search results
- **Route Filtering**: Intercepts and modifies Jellyfin's API responses
- **Scheduled Tasks**: 3 AM UTC daily population with configurable item counts

## Configuration Examples

### Anime Setup

If you want to separate anime content:

1. Create an anime library in Jellyfin
2. Set **Anime Library Path** in plugin config
3. Anime content (genre ID 16) will automatically route to this path
4. Setting up anime path in jellyfin should be mixed (movies & tv)

### Streaming

For streaming, you need to add a stremio addon (e.g., Torrentio):

1. Get your addon manifest URL
2. Add it to **Addon Link** field
3. Configure your torrent provider settings in the addon
4. Content will stream from your configured torrent provider
5. Note: Your addon needs to support a debrid provider

## Troubleshooting

### Plugin doesn't show in Dashboard

- Ensure correct version for your Jellyfin release
- Check plugin folder permissions
- Restart Jellyfin after installation

### Search returns no results

- Verify TMDb API key is valid
- Check internet connectivity
- Enable **Enable External Results** in config

### Library population not working

- Check TMDb API key configuration
- Verify library paths exist and are in Jellyfin
- Check logs for specific error messages
- Ensure at least one library path is configured

### Streams not playing

- Verify addon manifest URL is correct
- Check Jellyfin FFmpeg configuration
- Ensure Debrid is authorized in your plugin
- Check Jellyfin logs for stream resolution errors

## Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Commit changes
4. Push to branch
5. Open a Pull Request

## Acknowledgments

- This plugin was only possible after going through [Gelato](https://github.com/lostb1t/Gelato). Big thanks to [lostb1t](https://github.com/lostb1t)
- My old project [jf-resolve](https://github.com/vicking20/jf-resolve)
- Jellyfin project for the media server
- TMDB for metadata
- Stremio for addon ecosystem
- All users

## Support

- **Issues**: Report bugs on [GitHub Issues](../../issues)
- **Discussions**: Ask questions in [GitHub Discussions](../../discussions)
- **Jellyfin Forum**: Check the [Jellyfin Community](https://jellyfin.org/docs/general/community/)

## Issues

### Debrid, quality, provider settings

- Jfresolve just provides one result based on the settings you put in your stremio addon, If stream results are set to 4k for quality, then the first 4k item will be streamed, you can modify your add-on properly to give you qualities based on your internet speed and bandwidth.
- Every other setting related to quality and provider needs to be configured from your add-on
- Tip: You dont necessarily need to have stremio or a stremio account, you can for example test with [TorrentioRD](https://torrentio.strem.fun/configure)

### Search returns are slow

- Unfortunate, but this is a limitation of having to query an external provider and inserting the results into Jellyfin.
- You can reduce search results in configuration to make it slightly faster.
- Internet speed needs to be decent enough to handle multiple search requests.

---
