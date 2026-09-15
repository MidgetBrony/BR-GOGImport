using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GOGTestFramework;

public class SteamMeta
{
    public int PlayTimeMinutes { get; set; }
    public string Name { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string AppType { get; set; } = "";
    public string DetailedDescription { get; set; } = "";
    public string AboutTheGame { get; set; } = "";
    public string BoxArtUrlBase { get; set; } = "";
    public string FallbackHeaderUrl { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public List<string> Developers { get; set; } = new();
    public List<string> Publishers { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public List<string> ScreenshotUrls { get; set; } = new();
    public string Source { get; set; } = "";
    public int StoreId { get; set; }
    public string LaunchExePath { get; set; } = "";

    public int AppId { get; set; } = -1;
}
public class GogToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "";

    public DateTime SavedAt { get; set; }

    [JsonIgnore]
    public bool IsExpired =>
        DateTime.UtcNow >= SavedAt.AddSeconds(ExpiresIn - 60);
}

public class GogClient
{
    private readonly string _cacheFolder;
    private readonly string _tokenPath;
    private readonly string _ownedGamesPath;

    private const string ClientId =
        "46899977096215655";

    private const string ClientSecret =
        "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";

    private const string RedirectUri =
        "https://embed.gog.com/on_login_success?origin=client";

    private const string MetadataApiUrl =
        "https://boxroom-studio.hempton.us/api";

    private const int MetadataBatchSize = 250;

    private readonly HttpClient _client = new();

    public GogToken? Token { get; private set; }

    public GogClient()
    {
        _cacheFolder = Path.Combine(AppContext.BaseDirectory, "GOG");

        Directory.CreateDirectory(_cacheFolder);

        _tokenPath = Path.Combine(_cacheFolder, "token.json");
        _ownedGamesPath = Path.Combine(_cacheFolder, "owned.json");
    }

    private string _steamCachePath = "";
    public async Task InitializeAsync()
    {
        _steamCachePath = BoxroomCacheLocator.Find();
        Console.WriteLine($"Using BOXROOM cache: {_steamCachePath}");
        Console.WriteLine(OperatingSystem.IsLinux()
            ? "GOG games will launch through Heroic."
            : "GOG games will launch through GOG Galaxy.");
        if (!await LoadTokenAsync())
        {
            await LoginAsync();
            return;
        }

        if (Token!.IsExpired)
        {
            await RefreshTokenAsync();
        }
    }
    private const int StartingAppId = -1000000;

    public async Task ImportOwnedGamesAsync()
    {
        if (!File.Exists(_ownedGamesPath))
            throw new FileNotFoundException("owned.json not found.");

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(_ownedGamesPath));

        List<int> ownedIds = doc.RootElement
            .GetProperty("owned")
            .EnumerateArray()
            .Select(game => game.GetInt32())
            .Distinct()
            .ToList();

        Dictionary<int, int> existingImports = LoadExistingImports();
        await UpdateExistingGogLaunchPathsAsync(existingImports);

        List<int> pendingIds = ownedIds
            .Where(gogId => !existingImports.ContainsKey(gogId))
            .ToList();

        int alreadyImported = ownedIds.Count - pendingIds.Count;
        int completed = alreadyImported;
        int imported = 0;
        int missing = 0;
        int failed = 0;
        string? firstFailure = null;
        WriteProgress(completed, ownedIds.Count);

        foreach (int[] batch in pendingIds.Chunk(MetadataBatchSize))
        {
            IReadOnlyList<SteamMeta> games;
            try
            {
                games = await GetGamesFromApiAsync(batch);
            }
            catch (Exception ex)
            {
                failed += batch.Length;
                completed += batch.Length;
                firstFailure ??= ex.Message;
                WriteProgress(completed, ownedIds.Count);
                continue;
            }

            Dictionary<int, SteamMeta> gamesByStoreId = games
                .Where(game => game.StoreId > 0)
                .GroupBy(game => game.StoreId)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (int gogId in batch)
            {
                if (!gamesByStoreId.TryGetValue(gogId, out SteamMeta? game))
                {
                    missing++;
                    completed++;
                    WriteProgress(completed, ownedIds.Count);
                    continue;
                }

                try
                {
                    await ImportGameDataAsync(game, gogId);
                    imported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    firstFailure ??= ex.Message;
                }

                completed++;
                WriteProgress(completed, ownedIds.Count);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Complete: {imported} imported, {alreadyImported} already present, {missing} unavailable, {failed} failed.");
        if (firstFailure is not null)
            Console.WriteLine($"First error: {firstFailure}");

        await UpdateOwnedGamesIndexAsync();
    }

    private static void WriteProgress(int current, int total) =>
        Console.Write($"\rImporting GOG library: {current} / {total}   ");

    private static string GetLaunchUri(int gogId) =>
        OperatingSystem.IsLinux()
            ? $"heroic://launch/gog/{gogId}"
            : $"goggalaxy://openGameView/{gogId}";

    private int AllocateAppId()
    {
        var root = _steamCachePath;

        int next = StartingAppId;

        foreach (string dir in Directory.GetDirectories(root))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int appId))
                continue;

            if (appId <= next)
                next = appId - 1;
        }

        return next;
    }
    private async Task ExchangeCodeAsync(string code)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri
        };

        var response = await _client.PostAsync(
            "https://auth.gog.com/token",
            new FormUrlEncodedContent(values));

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync();
            throw new Exception(
                $"Token exchange failed ({(int)response.StatusCode}): {error}");
        }

        string json = await response.Content.ReadAsStringAsync();

        Token = JsonSerializer.Deserialize<GogToken>(json)
    ?? throw new Exception("Failed to deserialize token.");

        Token.SavedAt = DateTime.UtcNow;

        await SaveTokenAsync();

        Console.WriteLine("GOG sign-in complete.");
    }
    private async Task RefreshTokenAsync()
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = Token!.RefreshToken
        };

        var response = await _client.PostAsync(
           "https://auth.gog.com/token",
           new FormUrlEncodedContent(values));

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync();
            throw new Exception(
                $"Token exchange failed ({(int)response.StatusCode}): {error}");
        }

        string json = await response.Content.ReadAsStringAsync();

        Token = JsonSerializer.Deserialize<GogToken>(json)
    ?? throw new Exception("Failed to deserialize token.");

        Token.SavedAt = DateTime.UtcNow;

        await SaveTokenAsync();

        Console.WriteLine("GOG sign-in refreshed.");
    }
    private async Task SaveTokenAsync()
    {
        string json = JsonSerializer.Serialize(
            Token,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        await File.WriteAllTextAsync(_tokenPath, json);
    }
    private async Task<bool> LoadTokenAsync()
    {
        if (!File.Exists(_tokenPath))
            return false;

        string json = await File.ReadAllTextAsync(_tokenPath);

        Token = JsonSerializer.Deserialize<GogToken>(json);

        return Token != null;
    }

    public async Task LoginAsync()
    {
        string authUrl =
            $"https://auth.gog.com/auth" +
            $"?client_id={ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&response_type=code" +
            $"&layout=client2";

        Process.Start(new ProcessStartInfo
        {
            FileName = authUrl,
            UseShellExecute = true
        });

        Console.WriteLine();
        Console.WriteLine("Copy the FULL redirect URL from the browser address bar, including the code.");
        Console.Write("Paste the full redirect URL here: ");

        string redirect = Console.ReadLine()!;

        var uri = new Uri(redirect);

        var query =
            System.Web.HttpUtility.ParseQueryString(uri.Query);

        string? code = query["code"];

        if (string.IsNullOrWhiteSpace(code))
            throw new Exception("No authorization code found.");

        await ExchangeCodeAsync(code);
    }

    public async Task GetOwnedGamesAsync()
    {
        if (Token == null)
            throw new Exception("Not logged in.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://embed.gog.com/user/data/games");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", Token.AccessToken);
        using var response = await _client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync();
            throw new Exception(
                $"Failed to get owned games ({(int)response.StatusCode}): {error}");
        }


        string json = await response.Content.ReadAsStringAsync();

        await File.WriteAllTextAsync(_ownedGamesPath, json);

    }

    private async Task<bool> DownloadFileAsync(string url, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var response = await _client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(destination);

            await input.CopyToAsync(output);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task ImportGameAsync(int gogId)
    {
        int? existing = FindExistingImport(gogId);

        if (existing.HasValue)
            return;

        SteamMeta game = (await GetGamesFromApiAsync([gogId])).SingleOrDefault()
            ?? throw new Exception($"The metadata API did not return GOG game {gogId}.");

        await ImportGameDataAsync(game, gogId);
    }

    private async Task<IReadOnlyList<SteamMeta>> GetGamesFromApiAsync(IReadOnlyCollection<int> gogIds)
    {
        if (gogIds.Count == 0)
            return [];

        string requestJson = JsonSerializer.Serialize(new
        {
            source = "gog",
            ids = gogIds
        });

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(MetadataApiUrl, content);
        string json = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Metadata API failed ({(int)response.StatusCode}): {json}");

        ApiResponse result = JsonSerializer.Deserialize<ApiResponse>(json)
            ?? throw new Exception("Failed to deserialize API response.");

        if (!result.Success)
            throw new Exception(result.Message ?? "Metadata API returned failure.");

        List<SteamMeta> games = result.Games ?? [];
        if (result.Game is not null)
            games.Add(result.Game);

        return games;
    }

    private async Task ImportGameDataAsync(SteamMeta game, int gogId)
    {

        int appId = AllocateAppId();
        game.AppId = appId;
        game.AppType = "custom";
        game.Source = "gog";
        game.StoreId = gogId;
        game.LaunchExePath = GetLaunchUri(gogId);

        string folder = Path.Combine(_steamCachePath, appId.ToString());

        Directory.CreateDirectory(folder);

        await DownloadFileAsync(
            game.BoxArtUrlBase,
            Path.Combine(folder, "boxart.jpg"));

        for (int i = 0; i < game.ScreenshotUrls.Count; i++)
        {
            await DownloadFileAsync(
                game.ScreenshotUrls[i],
                Path.Combine(folder, $"screen_{i}.jpg"));
        }

        var helper = new MetaHelper
        {
            Type = "Custom",
            Platform = "gog",
            StoreId = gogId
        };

        await File.WriteAllTextAsync(
            Path.Combine(folder, "meta.json"),
            JsonSerializer.Serialize(game, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

        // Write the import marker last so an interrupted import is retried next time.
        await File.WriteAllTextAsync(
            Path.Combine(folder, "meta.helper.json"),
            JsonSerializer.Serialize(helper, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }

    private int? FindExistingImport(int gogId)
    {
        Dictionary<int, int> imports = LoadExistingImports();
        return imports.TryGetValue(gogId, out int appId) ? appId : null;
    }

    private Dictionary<int, int> LoadExistingImports()
    {
        Dictionary<int, int> imports = [];
        if (!Directory.Exists(_steamCachePath))
            return imports;

        foreach (string folder in Directory.GetDirectories(_steamCachePath))
        {
            string helperPath = Path.Combine(folder, "meta.helper.json");

            if (!File.Exists(helperPath))
                continue;

            try
            {
                var helper = JsonSerializer.Deserialize<MetaHelper>(
                    File.ReadAllText(helperPath));

                if (helper == null)
                    continue;

                if (!helper.Platform.Equals("gog", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (int.TryParse(Path.GetFileName(folder), out int appId))
                    imports.TryAdd(helper.StoreId, appId);
            }
            catch
            {
                // Ignore invalid helper files.
            }
        }

        return imports;
    }

    private async Task UpdateExistingGogLaunchPathsAsync(IReadOnlyDictionary<int, int> imports)
    {
        foreach ((int gogId, int appId) in imports)
        {
            string metaPath = Path.Combine(_steamCachePath, appId.ToString(), "meta.json");
            if (!File.Exists(metaPath))
                continue;

            try
            {
                JsonObject? meta = JsonNode.Parse(await File.ReadAllTextAsync(metaPath)) as JsonObject;
                if (meta is null)
                    continue;

                string launchUri = GetLaunchUri(gogId);
                bool alreadyCurrent =
                    string.Equals(meta["LaunchExePath"]?.GetValue<string>(), launchUri, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(meta["AppType"]?.GetValue<string>(), "custom", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(meta["Source"]?.GetValue<string>(), "gog", StringComparison.OrdinalIgnoreCase) &&
                    meta["StoreId"]?.GetValue<int>() == gogId;
                if (alreadyCurrent)
                    continue;

                meta["LaunchExePath"] = launchUri;
                meta["AppType"] = "custom";
                meta["Source"] = "gog";
                meta["StoreId"] = gogId;

                await File.WriteAllTextAsync(metaPath, meta.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            catch
            {
                // Leave an invalid existing entry unchanged and continue the import.
            }
        }
    }

    private async Task UpdateOwnedGamesIndexAsync()
    {
        string ownedGamesPath = Path.Combine(_steamCachePath, "owned_games.json");
        JsonObject root = File.Exists(ownedGamesPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(ownedGamesPath)) as JsonObject
                ?? throw new InvalidOperationException("BOXROOM's owned_games.json is invalid.")
            : new JsonObject();

        HashSet<int> appIds = [];
        if (root["AppIds"] is JsonArray existingIds)
        {
            foreach (JsonNode? node in existingIds)
            {
                if (node is JsonValue value && value.TryGetValue<int>(out int appId))
                    appIds.Add(appId);
            }
        }

        foreach (int appId in LoadExistingImports().Values)
            appIds.Add(appId);

        root["AppIds"] = new JsonArray(appIds.OrderBy(id => id)
            .Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        string temporaryPath = ownedGamesPath + ".gogimport.tmp";
        await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        }));
        File.Move(temporaryPath, ownedGamesPath, true);
    }
    public class ApiResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("Game")]
        public SteamMeta? Game { get; set; }

        [JsonPropertyName("Games")]
        public List<SteamMeta>? Games { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
    internal class MetaHelper
    {
        public string Type { get; set; } = "";
        public string Platform { get; set; } = "";
        public int StoreId { get; set; }
    }
}

internal static class BoxroomCacheLocator
{
    private const string CacheFolderName = "steam_cache_v2";
    private const string MarkerFileName = "owned_games.json";

    public static string Find()
    {
        string? configured = Environment.GetEnvironmentVariable("BOXROOM_CACHE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string configuredPath = Path.GetFullPath(configured);
            if (!File.Exists(Path.Combine(configuredPath, MarkerFileName)))
                throw new DirectoryNotFoundException(
                    $"BOXROOM_CACHE_PATH does not point to a BOXROOM cache containing {MarkerFileName}: {configuredPath}");

            return configuredPath;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..",
                "LocalLow",
                "NestedLoop",
                "BOXROOM",
                CacheFolderName));
        }

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Automatic BOXROOM cache discovery supports Windows and Linux.");

        List<string> matches = [];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AddIfBoxroomCache(matches, Path.Combine(
            home, ".config", "unity3d", "NestedLoop", "BOXROOM", CacheFolderName));

        foreach (string steamLibrary in FindSteamLibraries(home))
            FindProtonCaches(steamLibrary, matches);

        string? selected = matches
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, MarkerFileName)))
            .FirstOrDefault();

        if (selected is null)
        {
            throw new DirectoryNotFoundException(
                $"Could not find BOXROOM's {MarkerFileName} in the native Linux or Steam/Proton locations. " +
                "Run BOXROOM once, or set BOXROOM_CACHE_PATH to its steam_cache_v2 folder.");
        }

        return selected;
    }

    private static IEnumerable<string> FindSteamLibraries(string home)
    {
        string[] steamRoots =
        [
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
        ];

        HashSet<string> libraries = new(StringComparer.Ordinal);
        foreach (string root in steamRoots.Where(Directory.Exists))
        {
            libraries.Add(Path.GetFullPath(root));
            string libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
                continue;

            try
            {
                string vdf = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(vdf, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                {
                    string path = match.Groups["path"].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(path))
                        libraries.Add(Path.GetFullPath(path));
                }
            }
            catch (IOException)
            {
                // A known Steam root is still usable if its library list cannot be read.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue through the other user-accessible Steam installations.
            }
        }

        return libraries;
    }

    private static void FindProtonCaches(string steamLibrary, ICollection<string> matches)
    {
        string compatData = Path.Combine(steamLibrary, "steamapps", "compatdata");
        if (!Directory.Exists(compatData))
            return;

        try
        {
            foreach (string prefix in Directory.EnumerateDirectories(compatData))
            {
                string users = Path.Combine(prefix, "pfx", "drive_c", "users");
                if (!Directory.Exists(users))
                    continue;

                foreach (string user in Directory.EnumerateDirectories(users))
                {
                    AddIfBoxroomCache(matches, Path.Combine(
                        user, "AppData", "LocalLow", "NestedLoop", "BOXROOM", CacheFolderName));
                }
            }
        }
        catch (IOException)
        {
            // Ignore an unavailable Steam library and continue with the others.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore an unavailable Steam library and continue with the others.
        }
    }

    private static void AddIfBoxroomCache(ICollection<string> matches, string cachePath)
    {
        if (File.Exists(Path.Combine(cachePath, MarkerFileName)))
            matches.Add(Path.GetFullPath(cachePath));
    }
}
