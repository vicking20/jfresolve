// JfResolvePopulator.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jfresolve.Configuration;
using Jfresolve.Utilities;
using Microsoft.Extensions.Logging;

namespace Jfresolve
{
    /// <summary>
    /// Populates Jellyfin library with TMDB results as STRM files.
    /// Respects configuration for paths, unreleased content, and buffer days.
    /// </summary>
    public class JfResolvePopulator : IDisposable
    {
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
        private const int AnimeGenreId = 16;

        private readonly PluginConfiguration _config;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="JfResolvePopulator"/> class.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="logger">Logger instance for logging messages.</param>
        public JfResolvePopulator(PluginConfiguration config, ILogger logger)
        {
            _config = config;
            _logger = logger;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Releases the resources used by this instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _httpClient?.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// Entry point: populate all configured libraries if enabled.
        /// Note: Library scanning is handled by the caller (JfresolveManager).
        /// This method only creates STRM files without triggering any Jellyfin refresh.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task PopulateLibrariesAsync()
        {
            if (!_config.EnableLibraryPopulation)
            {
                _logger.LogInformation("[POPULATE] Library population is disabled in configuration");
                return;
            }

            _logger.LogInformation("[POPULATE] Starting library population with {ItemsPerRequest} items per request", _config.ItemsPerRequest);

            try
            {
                // Populate movies
                if (!string.IsNullOrWhiteSpace(_config.MoviesLibraryPath))
                {
                    await PopulateMoviesAsync().ConfigureAwait(false);
                }

                // Populate shows (excludes anime if anime path exists)
                if (!string.IsNullOrWhiteSpace(_config.ShowsLibraryPath))
                {
                    await PopulateShowsAsync().ConfigureAwait(false);
                }

                _logger.LogInformation("[POPULATE] Library population completed successfully (without triggering library scan)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POPULATE] Error during library population");
            }
        }

        /// <summary>
        /// Populates the movies library.
        /// </summary>
        private async Task PopulateMoviesAsync()
        {
            _logger.LogInformation("[MOVIES] Starting movie population");

            try
            {
                var items = await FetchFromTmdbAsync("movie/popular", string.Empty).ConfigureAwait(false);
                await ProcessMoviesAsync(items, _config.MoviesLibraryPath, includeAnime: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MOVIES] Error populating movies");
            }
        }

        /// <summary>
        /// Populates the shows library (excludes anime if anime path is configured).
        /// </summary>
        private async Task PopulateShowsAsync()
        {
            _logger.LogInformation("[SHOWS] Starting show population");

            try
            {
                // If anime path is configured, exclude anime from shows
                string genreFilter = !string.IsNullOrWhiteSpace(_config.AnimeLibraryPath)
                    ? "&without_genres=16"
                    : string.Empty;

                var items = await FetchFromTmdbAsync("tv/popular", genreFilter).ConfigureAwait(false);
                await ProcessShowsAsync(items, _config.ShowsLibraryPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SHOWS] Error populating shows");
            }
        }

        /// <summary>
        /// Processes movies and saves them as STRM files.
        /// </summary>
        private async Task ProcessMoviesAsync(JsonElement[] items, string libraryPath, bool includeAnime = true)
        {
            int processedCount = 0;

            foreach (var item in items)
            {
                try
                {
                    if (processedCount >= _config.ItemsPerRequest)
                    {
                        _logger.LogInformation("[MOVIES] Reached items per request limit ({Limit})", _config.ItemsPerRequest);
                        break;
                    }

                    var releaseDateStr = GetProperty(item, "release_date");

                    // Check if we should skip unreleased content
                    if (!ShouldIncludeItem(releaseDateStr))
                    {
                        _logger.LogDebug("[MOVIES] Skipping unreleased: {Title}", GetProperty(item, "title"));
                        continue;
                    }

                    var movieId = GetProperty(item, "id");
                    var movieDetails = await GetMovieDetailsAsync(movieId).ConfigureAwait(false);

                    var title = GetProperty(movieDetails, "title");
                    var year = ExtractYear(releaseDateStr);
                    var imdbId = ExtractImdbId(movieDetails);

                    if (string.IsNullOrEmpty(imdbId))
                    {
                        _logger.LogWarning("[MOVIES] Movie '{Title}' has no IMDb ID, skipping", title);
                        continue;
                    }

                    // Check if movie is anime and should go to anime path
                    var genres = ExtractGenres(movieDetails);
                    bool isAnime = genres?.Contains(AnimeGenreId) ?? false;

                    if (isAnime && !string.IsNullOrWhiteSpace(_config.AnimeLibraryPath))
                    {
                        // Save anime movie to anime path instead
                        await SaveMovieAsync(title, year, imdbId, _config.AnimeLibraryPath).ConfigureAwait(false);
                    }
                    else if (!isAnime || includeAnime)
                    {
                        // Save regular movie
                        await SaveMovieAsync(title, year, imdbId, libraryPath).ConfigureAwait(false);
                    }

                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MOVIES] Error processing movie item");
                }
            }

            _logger.LogInformation("[MOVIES] Movie population completed ({Processed} items)", processedCount);
        }

        /// <summary>
        /// Processes shows/anime and saves them as STRM files.
        /// Routes anime shows to anime path if configured, otherwise to regular shows path.
        /// </summary>
        private async Task ProcessShowsAsync(JsonElement[] items, string libraryPath)
        {
            int processedCount = 0;

            foreach (var item in items)
            {
                try
                {
                    if (processedCount >= _config.ItemsPerRequest)
                    {
                        _logger.LogInformation("[SHOWS] Reached items per request limit ({Limit})", _config.ItemsPerRequest);
                        break;
                    }

                    var releaseDateStr = GetProperty(item, "first_air_date");

                    // Check if we should skip unreleased content
                    if (!ShouldIncludeItem(releaseDateStr))
                    {
                        _logger.LogDebug("[SHOWS] Skipping unreleased: {Title}", GetProperty(item, "name"));
                        continue;
                    }

                    var showId = GetProperty(item, "id");
                    var showDetails = await GetShowDetailsAsync(showId).ConfigureAwait(false);

                    var title = GetProperty(showDetails, "name");
                    var year = ExtractYear(releaseDateStr);
                    var imdbId = ExtractImdbId(showDetails);

                    if (string.IsNullOrEmpty(imdbId))
                    {
                        _logger.LogWarning("[SHOWS] Show '{Title}' has no IMDb ID, skipping", title);
                        continue;
                    }

                    // Check if show is anime and anime path is configured
                    var genres = ExtractGenres(showDetails);
                    bool isAnime = genres?.Contains(AnimeGenreId) ?? false;

                    // Route anime to anime path if configured, otherwise save to the provided path
                    string targetPath = libraryPath;
                    if (isAnime && !string.IsNullOrWhiteSpace(_config.AnimeLibraryPath))
                    {
                        targetPath = _config.AnimeLibraryPath;
                        _logger.LogDebug("[SHOWS] Routing anime series '{Title}' to anime library path", title);
                    }

                    await SaveShowAsync(showDetails, title, year, imdbId, targetPath).ConfigureAwait(false);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SHOWS] Error processing show item");
                }
            }

            _logger.LogInformation("[SHOWS] Show population completed ({Processed} items)", processedCount);
        }

        /// <summary>
        /// Saves a movie as a STRM file.
        /// </summary>
        private async Task SaveMovieAsync(string title, string year, string imdbId, string libraryPath)
        {
            try
            {
                var yearInt = int.TryParse(year, out var y) ? y : (int?)null;
                var folderName = FileNameUtility.BuildMovieFolderName(title, yearInt);
                var movieFolder = Path.Combine(libraryPath, folderName);
                var strmFileName = FileNameUtility.BuildMovieStrmFileName(title, yearInt);
                var strmPath = Path.Combine(movieFolder, strmFileName);

                // Skip if already exists
                if (File.Exists(strmPath))
                {
                    _logger.LogDebug("[SAVE] Movie STRM already exists: {Path}", strmPath);
                    return;
                }

                Directory.CreateDirectory(movieFolder);

                var strmContent = UrlBuilder.BuildMovieResolverUrl(_config.JellyfinBaseUrl, imdbId);

                await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);
                _logger.LogInformation("[SAVE] Created movie STRM: {Title} ({Year})", title, year);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SAVE] Error saving movie '{Title}'", title);
            }
        }

        /// <summary>
        /// Saves a show/anime as STRM files for all episodes.
        /// </summary>
        private async Task SaveShowAsync(JsonElement showDetails, string title, string year, string imdbId, string libraryPath)
        {
            try
            {
                var yearInt = int.TryParse(year, out var y) ? y : (int?)null;
                var folderName = FileNameUtility.BuildSeriesFolderName(title, yearInt);
                var showFolder = Path.Combine(libraryPath, folderName);
                Directory.CreateDirectory(showFolder);

                var seasons = GetArrayProperty(showDetails, "seasons");
                int episodeCount = 0;

                foreach (var season in seasons)
                {
                    var seasonNum = int.Parse(GetProperty(season, "season_number"), CultureInfo.InvariantCulture);

                    // Skip season 0 if configured
                    if (seasonNum == 0 && !_config.IncludeSpecials)
                    {
                        _logger.LogDebug("[SAVE] Skipping Season 0 (Specials) for {Title}", title);
                        continue;
                    }

                    var seasonFolder = UrlBuilder.BuildSeasonPath(showFolder, seasonNum);
                    Directory.CreateDirectory(seasonFolder);

                    var episodesInSeason = int.Parse(GetProperty(season, "episode_count"), CultureInfo.InvariantCulture);

                    for (int ep = 1; ep <= episodesInSeason; ep++)
                    {
                        var strmFileName = FileNameUtility.BuildEpisodeStrmFileName(title, yearInt, seasonNum, ep);
                        var strmPath = Path.Combine(seasonFolder, strmFileName);

                        // Skip if already exists
                        if (File.Exists(strmPath))
                        {
                            _logger.LogDebug("[SAVE] Show STRM already exists: {Path}", strmPath);
                            continue;
                        }

                        var strmContent = UrlBuilder.BuildSeriesResolverUrl(_config.JellyfinBaseUrl, imdbId, seasonNum, ep);

                        await File.WriteAllTextAsync(strmPath, strmContent).ConfigureAwait(false);
                        episodeCount++;
                    }
                }

                _logger.LogInformation("[SAVE] Created show STRM files: {Title} ({Episodes} episodes)", title, episodeCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SAVE] Error saving show '{Title}'", title);
            }
        }

        /// <summary>
        /// Determines if an item should be included based on release date and configuration.
        /// </summary>
        private bool ShouldIncludeItem(string? releaseDateStr)
        {
            if (_config.IncludeUnreleased)
            {
                return true;
            }

            if (string.IsNullOrEmpty(releaseDateStr))
            {
                return false;
            }

            if (!DateTime.TryParse(releaseDateStr, out var releaseDate))
            {
                return false;
            }

            // Add buffer days if configured
            var bufferDate = DateTime.UtcNow.AddDays(_config.UnreleasedBufferDays);
            return releaseDate <= bufferDate;
        }

        /// <summary>
        /// Fetches items from TMDB API with pagination support.
        /// TMDB returns 20 results per page, so this fetches multiple pages if needed.
        /// </summary>
        private async Task<JsonElement[]> FetchFromTmdbAsync(string endpoint, string extraParams = "")
        {
            try
            {
                var includeAdult = _config.IncludeAdult ? "true" : "false";
                var allItems = new List<JsonElement>();
                int page = 1;
                const int resultsPerPage = 20; // TMDB API returns 20 items per page

                // Calculate how many pages we need
                int pagesNeeded = (_config.ItemsPerRequest + resultsPerPage - 1) / resultsPerPage;

                _logger.LogDebug("[TMDB] Fetching from: {Endpoint}, requesting {ItemsTotal} items across {PagesNeeded} pages",
                    endpoint, _config.ItemsPerRequest, pagesNeeded);

                while (allItems.Count < _config.ItemsPerRequest && page <= pagesNeeded)
                {
                    var url = $"{TmdbBaseUrl}/{endpoint}?api_key={_config.TmdbApiKey}&include_adult={includeAdult}&page={page}{extraParams}";

                    var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                    var result = JsonSerializer.Deserialize<JsonElement>(response);

                    if (!result.TryGetProperty("results", out var results))
                    {
                        _logger.LogWarning("[TMDB] No results in response from {Endpoint}", endpoint);
                        break;
                    }

                    // Add items from this page until we reach ItemsPerRequest
                    foreach (var item in results.EnumerateArray())
                    {
                        if (allItems.Count >= _config.ItemsPerRequest)
                            break;

                        allItems.Add(item);
                    }

                    // If we got fewer than 20 items, we've reached the last page
                    if (results.GetArrayLength() < resultsPerPage)
                    {
                        _logger.LogDebug("[TMDB] Reached last page ({Page}), got {ItemCount} items", page, results.GetArrayLength());
                        break;
                    }

                    page++;
                }

                _logger.LogDebug("[TMDB] Fetched {ItemCount} items from {Pages} pages", allItems.Count, page - 1);
                return allItems.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TMDB] Error fetching from {Endpoint}", endpoint);
                return Array.Empty<JsonElement>();
            }
        }

        /// <summary>
        /// Gets movie details including external IDs.
        /// </summary>
        private async Task<JsonElement> GetMovieDetailsAsync(string movieId)
        {
            try
            {
                var url = $"{TmdbBaseUrl}/movie/{movieId}?api_key={_config.TmdbApiKey}&append_to_response=external_ids";
                var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                return JsonSerializer.Deserialize<JsonElement>(response);
            }
            catch (Exception)
            {
                _logger.LogWarning("[TMDB] Error fetching movie details for {MovieId}", movieId);
                return default;
            }
        }

        /// <summary>
        /// Gets show details including external IDs and seasons.
        /// </summary>
        private async Task<JsonElement> GetShowDetailsAsync(string showId)
        {
            try
            {
                var url = $"{TmdbBaseUrl}/tv/{showId}?api_key={_config.TmdbApiKey}&append_to_response=external_ids";
                var response = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                return JsonSerializer.Deserialize<JsonElement>(response);
            }
            catch (Exception)
            {
                _logger.LogWarning("[TMDB] Error fetching show details for {ShowId}", showId);
                return default;
            }
        }

        /// <summary>
        /// Extracts IMDb ID from TMDB details.
        /// </summary>
        private static string? ExtractImdbId(JsonElement details)
        {
            try
            {
                if (details.TryGetProperty("external_ids", out var externalIds) &&
                    externalIds.TryGetProperty("imdb_id", out var imdbIdEl))
                {
                    return imdbIdEl.GetString();
                }
            }
            catch (Exception)
            {
                // Log if needed
            }

            return null;
        }

        /// <summary>
        /// Extracts genre IDs from TMDB details.
        /// </summary>
        private static IReadOnlyCollection<int>? ExtractGenres(JsonElement details)
        {
            try
            {
                if (details.TryGetProperty("genres", out var genresEl))
                {
                    var genres = new List<int>();
                    foreach (var genre in genresEl.EnumerateArray())
                    {
                        if (genre.TryGetProperty("id", out var idEl))
                        {
                            genres.Add(idEl.GetInt32());
                        }
                    }

                    return genres.Count > 0 ? genres.AsReadOnly() : null;
                }
            }
            catch (Exception)
            {
                // Log if needed
            }

            return null;
        }

        /// <summary>
        /// Extracts year from date string (YYYY-MM-DD format).
        /// </summary>
        private static string ExtractYear(string? dateStr)
        {
            if (string.IsNullOrEmpty(dateStr) || dateStr.Length < 4)
                return "Unknown";

            return dateStr.Substring(0, 4);
        }

        /// <summary>
        /// Gets a string property from a JSON element.
        /// </summary>
        private static string GetProperty(JsonElement element, string propertyName)
        {
            return JsonHelper.GetJsonString(element, propertyName);
        }

        /// <summary>
        /// Gets an array property from a JSON element.
        /// </summary>
        private static JsonElement[] GetArrayProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                var array = new JsonElement[prop.GetArrayLength()];
                int i = 0;
                foreach (var item in prop.EnumerateArray())
                {
                    array[i++] = item;
                }

                return array;
            }

            return Array.Empty<JsonElement>();
        }
    }
}
