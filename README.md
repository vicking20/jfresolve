<p align="center">
  <img src="https://raw.githubusercontent.com/vicking20/jfresolve/main/jfresolve.png" alt="Jfresolve Logo" width="128" height="128">
</p>

<h1 align="center">Jfresolve</h1>

Jellyfin plugin that adds movies and shows from TMDB to your library and streams them through a Stremio addon (Torrentio, AIOStreams, MediaFusion, etc.) with a debrid provider. Nothing is downloaded or stored.

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

Prefer a standalone web app instead of a plugin? See [jf-resolve](https://github.com/vicking20/jf-resolve).

## Features

- TMDB results in Jellyfin search; opening one adds it to the library
- Auto-population from TMDB trending, popular and top rated
- Movies, series and anime (optional separate anime library)
- Preferred quality, with optional extra quality versions per movie
- Optional `.strm` file output for third-party clients (Infuse, etc.)
- Stream resume when the debrid host drops the connection
- Failover to the next link when a stream is dead (experimental)
- Scheduled tasks for population, series updates and cleanup

## Requirements

- Jellyfin 10.11.6 or later
- TMDB API key ([get one here](https://www.themoviedb.org/settings/api))
- A Stremio addon manifest URL with your debrid key configured

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add:
   `https://raw.githubusercontent.com/vicking20/jfresolve/refs/heads/main/repository.json`
2. Install **Jfresolve** from the catalog and restart Jellyfin.
3. Create a Movies and/or Shows library pointing at an empty, writable folder.
4. Open the plugin settings, fill in the required fields and the library paths, save, restart Jellyfin, then scan the libraries once.

Upgrading from a version older than 1.0.0.3: uninstall the old version first.

## Configuration

### Required

| Setting | Notes |
| --- | --- |
| TMDB API Key | v3 key |
| Jellyfin Server URL | Address your **clients** can reach, e.g. `http://192.168.1.10:8096`. Not `localhost` unless every client runs on the server. |
| Addon Manifest URL | `stremio://` or `https://` URL ending in `/manifest.json` |
| Library paths | At least one Movies or Shows path. Must match the library folder path as Jellyfin sees it (inside the container for Docker). |

Example Torrentio URL (replace the key):

```
stremio://torrentio.strem.fun/providers=yts,eztv,rarbg,1337x,thepiratebay,kickasstorrents,torrentgalaxy,magnetdl,horriblesubs,nyaasi,tokyotosho,anidex|qualityfilter=brremux,scr,cam|limit=1|debridoptions=nodownloadlinks,nocatalog|realdebrid=YOUR_KEY/manifest.json
```

### Optional

| Setting | Notes |
| --- | --- |
| Enable Search Interception | Show TMDB results in Jellyfin search |
| Write .strm Files | Write `.strm` files instead of adding items to the database. Use this for Infuse and other clients that can't play the default items. Run **Clear All Jfresolve Items** before switching. |
| Preferred Quality | Auto picks the highest. Any other value is a ceiling: if it isn't available, the next lower quality is used. |
| Quality versions (4K / 1080p / 720p / Unknown) | Adds extra versions per movie. Does not affect which stream the main item plays. |
| Separate anime folder | Routes TMDB animation titles to the anime library |
| Auto population | Sources, items per run and exclusion list |
| Custom FFmpeg settings | Probe size and analyze duration for remote streams |
| Movie / Show failover | Try the next link when a stream fails. Grace period and window are in seconds. |

## Scheduled tasks

Under **Dashboard → Scheduled Tasks → Jfresolve**. None have a default schedule.

- **Populate Jfresolve Library**: adds titles from the enabled TMDB sources
- **Update Jfresolve Series**: adds new seasons and episodes
- **Clear All Jfresolve Items**: removes everything Jfresolve added, including `.strm` folders it wrote

## How it works

Each item's path is a plugin URL such as `/Plugins/Jfresolve/resolve/movie/tt1234567`. On playback the plugin asks the addon for streams, picks one by quality and proxies it to Jellyfin. In `.strm` mode the same URL is written into the file, so links never go stale.

## Troubleshooting

**Plugin shows as "Unsupported"**
Update Jellyfin to 10.11.6 or later, then reinstall the latest version.

**Populate task does nothing or fails to create items**
The library folder must exist, be writable and have been scanned at least once. Docker volumes mounted `:ro` won't work.

**Plays in the browser but not in Infuse or other apps**
Set Jellyfin Server URL to an address the client can reach, and enable **Write .strm Files**.

**403 errors**
Check the log line. `Addon returned 403` usually means the addon (or Cloudflare in front of it) is blocking your server's IP. `Stream host returned 403` comes from the debrid provider, often due to VPN or datacenter IPs.

**Wrong quality picked**
Set **Preferred Quality**. The quality version toggles only add extra versions. If the addon returns nothing at that quality, check the addon's own quality filter.

**4K playback stops after a few minutes**
Update to 1.0.0.14 or later, which resumes dropped connections.

## Building

```bash
cd jfresolve-10.11
dotnet build -c Release
```

Output: `jfresolve-10.11/bin/Release/net9.0/Jfresolve.dll`

## Credits

- [Gelato](https://github.com/lostb1t/Gelato) by lostb1t, which this plugin is based on
- [jf-resolve](https://github.com/vicking20/jf-resolve)
- Jellyfin, TMDB and the Stremio addon ecosystem

## Disclaimer

Educational project. Provided as-is; use at your own risk.
