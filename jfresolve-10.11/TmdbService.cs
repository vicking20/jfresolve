using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jfresolve;

public class TmdbService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbService> _log;
    private const string BaseUrl = "https://api.themoviedb.org/3";

    public TmdbService(IHttpClientFactory httpClientFactory, ILogger<TmdbService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<List<TmdbMovie>> SearchMoviesAsync(string query, string apiKey, bool includeAdult = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return new List<TmdbMovie>();
        }

        try
        {
            var url = $"{BaseUrl}/search/movie?api_key={apiKey}&query={Uri.EscapeDataString(query)}&include_adult={includeAdult.ToString().ToLower()}";

            _log.LogInformation("Searching TMDB movies: {Query}", query);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error: {StatusCode}", response.StatusCode);
                return new List<TmdbMovie>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbSearchResult<TmdbMovie>>(json);

            var movies = result?.Results ?? new List<TmdbMovie>();

            // Fetch IMDB IDs for each movie
            foreach (var movie in movies)
            {
                var externalIds = await GetExternalIdsAsync(movie.Id, "movie", apiKey);
                if (externalIds?.ImdbId != null)
                {
                    movie.ImdbId = externalIds.ImdbId;
                    _log.LogDebug("Fetched IMDB ID {ImdbId} for movie '{Title}'", movie.ImdbId, movie.Title);
                }
            }

            return movies;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to search TMDB movies");
            return new List<TmdbMovie>();
        }
    }

    public async Task<List<TmdbTvShow>> SearchTvShowsAsync(string query, string apiKey, bool includeAdult = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return new List<TmdbTvShow>();
        }

        try
        {
            var url = $"{BaseUrl}/search/tv?api_key={apiKey}&query={Uri.EscapeDataString(query)}&include_adult={includeAdult.ToString().ToLower()}";

            _log.LogInformation("Searching TMDB TV shows: {Query}", query);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error: {StatusCode}", response.StatusCode);
                return new List<TmdbTvShow>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbSearchResult<TmdbTvShow>>(json);

            var tvShows = result?.Results ?? new List<TmdbTvShow>();

            // Fetch IMDB IDs for each TV show
            foreach (var show in tvShows)
            {
                var externalIds = await GetExternalIdsAsync(show.Id, "tv", apiKey);
                if (externalIds?.ImdbId != null)
                {
                    show.ImdbId = externalIds.ImdbId;
                    _log.LogDebug("Fetched IMDB ID {ImdbId} for TV show '{Name}'", show.ImdbId, show.Name);
                }
            }

            return tvShows;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to search TMDB TV shows");
            return new List<TmdbTvShow>();
        }
    }

    // ============ DISCOVERY API METHODS (for Auto Library Population) ============

    /// <summary>
    /// Get trending movies (daily or weekly)
    /// </summary>
    public async Task<List<TmdbMovie>> GetTrendingMoviesAsync(string apiKey, string timeWindow = "day", bool includeAdult = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return new List<TmdbMovie>();
        }

        try
        {
            // timeWindow can be "day" or "week"
            var url = $"{BaseUrl}/trending/movie/{timeWindow}?api_key={apiKey}&include_adult={includeAdult.ToString().ToLower()}";

            _log.LogInformation("Fetching trending movies ({TimeWindow})", timeWindow);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error: {StatusCode}", response.StatusCode);
                return new List<TmdbMovie>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbSearchResult<TmdbMovie>>(json);

            var movies = result?.Results ?? new List<TmdbMovie>();

            // Fetch IMDB IDs for each movie
            foreach (var movie in movies)
            {
                var externalIds = await GetExternalIdsAsync(movie.Id, "movie", apiKey);
                if (externalIds?.ImdbId != null)
                {
                    movie.ImdbId = externalIds.ImdbId;
                    _log.LogDebug("Fetched IMDB ID {ImdbId} for trending movie '{Title}'", movie.ImdbId, movie.Title);
                }
            }

            return movies;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch trending movies");
            return new List<TmdbMovie>();
        }
    }

    /// <summary>
    /// Get trending TV shows (daily or weekly)
    /// </summary>
    public async Task<List<TmdbTvShow>> GetTrendingTvShowsAsync(string apiKey, string timeWindow = "day", bool includeAdult = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return new List<TmdbTvShow>();
        }

        try
        {
            // timeWindow can be "day" or "week"
            var url = $"{BaseUrl}/trending/tv/{timeWindow}?api_key={apiKey}&include_adult={includeAdult.ToString().ToLower()}";

            _log.LogInformation("Fetching trending TV shows ({TimeWindow})", timeWindow);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error: {StatusCode}", response.StatusCode);
                return new List<TmdbTvShow>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbSearchResult<TmdbTvShow>>(json);

            var tvShows = result?.Results ?? new List<TmdbTvShow>();

            // Fetch IMDB IDs for each TV show
            foreach (var tvShow in tvShows)
            {
                var externalIds = await GetExternalIdsAsync(tvShow.Id, "tv", apiKey);
                if (externalIds?.ImdbId != null)
                {
                    tvShow.ImdbId = externalIds.ImdbId;
                    _log.LogDebug("Fetched IMDB ID {ImdbId} for trending TV show '{Name}'", tvShow.ImdbId, tvShow.Name);
                }
            }

            return tvShows;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch trending TV shows");
            return new List<TmdbTvShow>();
        }
    }

    /// <summary>
    /// Get popular movies
    /// </summary>
    public async Task<List<TmdbMovie>> GetPopularMoviesAsync(string apiKey, bool includeAdult = false, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return new List<TmdbMovie>();
        }

        try
        {
            var url = $"{BaseUrl}/movie/popular?api_key={apiKey}&include_adult={includeAdult.ToString().ToLower()}&page={page}";

            _log.LogInformation("Fetching popular movies (page {Page})", page);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error: {StatusCode}", response.StatusCode);
                return new List<TmdbMovie>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbSearchResult<TmdbMovie>>(json);

            var movies = result?.Results ?? new List<TmdbMovie>();

            // Fetch IMDB IDs for each movie
            foreach (var movie in movies)
            {
                var externalIds = await GetExternalIdsAsync(movie.Id, "movie", apiKey);
                if (externalIds?.ImdbId != null)
                {
                    movie.ImdbId = externalIds.ImdbId;
                    _log.LogDebug("Fetched IMDB ID {ImdbId} for popular movie '{Title}'", movie.ImdbId, movie.Title);
                }
            }

            return movies;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch popular movies");
            return new List<TmdbMovie>();
        }
    }

    /// <summary>
    /// Get popular TV shows
    /// </summary>
    public async Task<List<TmdbTvShow>> GetPopularTvShowsAsync(string apiKey, bool includeAdult = false, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return new List<TmdbTvShow>();
        }

        try
        {
            var url = $"{BaseUrl}/tv/popular?api_key={apiKey}&include_adult={includeAdult.ToString().ToLower()}&page={page}";

            _log.LogInformation("Fetching popular TV shows (page {Page})", page);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error: {StatusCode}", response.StatusCode);
                return new List<TmdbTvShow>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbSearchResult<TmdbTvShow>>(json);

            var tvShows = result?.Results ?? new List<TmdbTvShow>();

            // Fetch IMDB IDs for each TV show
            foreach (var tvShow in tvShows)
            {
                var externalIds = await GetExternalIdsAsync(tvShow.Id, "tv", apiKey);
                if (externalIds?.ImdbId != null)
                {
                    tvShow.ImdbId = externalIds.ImdbId;
                    _log.LogDebug("Fetched IMDB ID {ImdbId} for popular TV show '{Name}'", tvShow.ImdbId, tvShow.Name);
                }
            }

            return tvShows;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch popular TV shows");
            return new List<TmdbTvShow>();
        }
    }

    public async Task<TmdbExternalIds?> GetExternalIdsAsync(int tmdbId, string mediaType, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return null;
        }

        try
        {
            // mediaType should be "movie" or "tv"
            var url = $"{BaseUrl}/{mediaType}/{tmdbId}/external_ids?api_key={apiKey}";

            _log.LogDebug("Fetching external IDs for TMDB {MediaType} ID: {TmdbId}", mediaType, tmdbId);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error fetching external IDs: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbExternalIds>(json);

            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch TMDB external IDs");
            return null;
        }
    }

    public async Task<TmdbTvDetails?> GetTvDetailsAsync(int tmdbId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return null;
        }

        try
        {
            var url = $"{BaseUrl}/tv/{tmdbId}?api_key={apiKey}";

            _log.LogDebug("Fetching TV details for TMDB ID: {TmdbId}", tmdbId);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error fetching TV details: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbTvDetails>(json);

            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch TMDB TV details");
            return null;
        }
    }

    public async Task<TmdbSeasonDetails?> GetSeasonDetailsAsync(int tmdbId, int seasonNumber, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("TMDB API key not configured");
            return null;
        }

        try
        {
            var url = $"{BaseUrl}/tv/{tmdbId}/season/{seasonNumber}?api_key={apiKey}";

            _log.LogDebug("Fetching season {SeasonNumber} details for TMDB ID: {TmdbId}", seasonNumber, tmdbId);

            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogError("TMDB API error fetching season details: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<TmdbSeasonDetails>(json);

            return result;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch TMDB season details");
            return null;
        }
    }
}

public class TmdbSearchResult<T>
{
    [JsonPropertyName("results")]
    public List<T> Results { get; set; } = new();

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

public class TmdbMovie
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("original_title")]
    public string OriginalTitle { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = string.Empty;

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("vote_average")]
    public float VoteAverage { get; set; }

    [JsonPropertyName("adult")]
    public bool Adult { get; set; }

    [JsonPropertyName("genre_ids")]
    public List<int> GenreIds { get; set; } = new List<int>();

    // IMDB ID fetched separately via external_ids endpoint
    public string? ImdbId { get; set; }

    /// <summary>
    /// Check if this movie is anime (genre ID 16 in TMDB)
    /// </summary>
    public bool IsAnime() => GenreIds.Contains(16);

    public string GetPosterUrl() =>
        string.IsNullOrWhiteSpace(PosterPath)
            ? "https://image.tmdb.org/t/p/w1280/2zmTngn1tYC1AvfnrFLhxeD82hz.jpg"
            : $"https://image.tmdb.org/t/p/w1280{PosterPath}";

    public string? GetBackdropUrl() =>
        string.IsNullOrWhiteSpace(BackdropPath)
            ? null
            : $"https://image.tmdb.org/t/p/w1280{BackdropPath}";

    public int? GetYear()
    {
        if (string.IsNullOrWhiteSpace(ReleaseDate)) return null;
        if (DateTime.TryParse(ReleaseDate, out var date))
            return date.Year;
        return null;
    }

    public DateTime? GetReleaseDateTime()
    {
        if (string.IsNullOrWhiteSpace(ReleaseDate)) return null;
        if (DateTime.TryParse(ReleaseDate, out var date))
            return date;
        return null;
    }
}

public class TmdbTvShow
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("original_name")]
    public string OriginalName { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = string.Empty;

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("vote_average")]
    public float VoteAverage { get; set; }

    [JsonPropertyName("genre_ids")]
    public List<int> GenreIds { get; set; } = new List<int>();

    // IMDB ID fetched separately via external_ids endpoint
    public string? ImdbId { get; set; }

    /// <summary>
    /// Check if this TV show is anime (genre ID 16 in TMDB)
    /// </summary>
    public bool IsAnime() => GenreIds.Contains(16);

    public string GetPosterUrl() =>
        string.IsNullOrWhiteSpace(PosterPath)
            ? "https://image.tmdb.org/t/p/w1280/2zmTngn1tYC1AvfnrFLhxeD82hz.jpg"
            : $"https://image.tmdb.org/t/p/w1280{PosterPath}";

    public string? GetBackdropUrl() =>
        string.IsNullOrWhiteSpace(BackdropPath)
            ? null
            : $"https://image.tmdb.org/t/p/w1280{BackdropPath}";

    public int? GetYear()
    {
        if (string.IsNullOrWhiteSpace(FirstAirDate)) return null;
        if (DateTime.TryParse(FirstAirDate, out var date))
            return date.Year;
        return null;
    }

    public DateTime? GetFirstAirDateTime()
    {
        if (string.IsNullOrWhiteSpace(FirstAirDate)) return null;
        if (DateTime.TryParse(FirstAirDate, out var date))
            return date;
        return null;
    }
}

public class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("freebase_mid")]
    public string? FreebaseMid { get; set; }

    [JsonPropertyName("freebase_id")]
    public string? FreebaseId { get; set; }

    [JsonPropertyName("tvrage_id")]
    public int? TvrageId { get; set; }

    [JsonPropertyName("wikidata_id")]
    public string? WikidataId { get; set; }

    [JsonPropertyName("facebook_id")]
    public string? FacebookId { get; set; }

    [JsonPropertyName("instagram_id")]
    public string? InstagramId { get; set; }

    [JsonPropertyName("twitter_id")]
    public string? TwitterId { get; set; }
}

public class TmdbTvDetails
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("number_of_seasons")]
    public int NumberOfSeasons { get; set; }

    [JsonPropertyName("number_of_episodes")]
    public int NumberOfEpisodes { get; set; }

    [JsonPropertyName("seasons")]
    public List<TmdbSeasonInfo> Seasons { get; set; } = new();

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("last_air_date")]
    public string? LastAirDate { get; set; }

    public DateTime? GetLastAirDateTime()
    {
        if (string.IsNullOrWhiteSpace(LastAirDate)) return null;
        if (DateTime.TryParse(LastAirDate, out var date))
            return date;
        return null;
    }

    public bool IsEnded()
    {
        // TMDB status values: "Returning Series", "Planned", "In Production", "Ended", "Canceled", "Pilot"
        return Status != null && (
            Status.Equals("Ended", StringComparison.OrdinalIgnoreCase) ||
            Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
        );
    }
}

public class TmdbSeasonInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    public DateTime? GetSeasonAirDateTime()
    {
        if (string.IsNullOrWhiteSpace(AirDate)) return null;
        if (DateTime.TryParse(AirDate, out var date))
            return date;
        return null;
    }
}

public class TmdbSeasonDetails
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("episodes")]
    public List<TmdbEpisode> Episodes { get; set; } = new();
}

public class TmdbEpisode
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }

    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }

    [JsonPropertyName("vote_average")]
    public float VoteAverage { get; set; }

    public DateTime? GetAirDateTime()
    {
        if (string.IsNullOrWhiteSpace(AirDate)) return null;
        if (DateTime.TryParse(AirDate, out var date))
            return date;
        return null;
    }

    public string? GetStillUrl() =>
        string.IsNullOrWhiteSpace(StillPath)
            ? null
            : $"https://image.tmdb.org/t/p/w1280{StillPath}";
}
