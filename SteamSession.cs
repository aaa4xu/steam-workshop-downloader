using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;

/// <summary>
/// Manages a connected and authenticated Steam session with refresh-token caching.
/// Exposes SteamKit handlers needed by the depot downloader.
/// </summary>
internal sealed class SteamSession
{
    private readonly Options _options;
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _steamUser;
    private readonly SteamApps _steamApps;
    private readonly SteamContent _steamContent;
    private readonly SteamAuthentication _steamAuth;
    private readonly CancellationTokenSource _callbackCts = new();
    private Task? _callbackLoop;
    private readonly string _tokenPath;
    private AuthTokenCache? _tokenCache;
    private readonly ConsoleAuthenticator _authenticator;

    public SteamSession(Options options)
    {
        _options = options;
        _client = new SteamClient();
        _callbacks = new CallbackManager(_client);
        _steamUser = _client.GetHandler<SteamUser>() ?? throw new InvalidOperationException("SteamUser handler not available.");
        _steamApps = _client.GetHandler<SteamApps>() ?? throw new InvalidOperationException("SteamApps handler not available.");
        _steamContent = _client.GetHandler<SteamContent>() ?? throw new InvalidOperationException("SteamContent handler not available.");
        _steamAuth = _client.Authentication;

        _tokenPath = ResolveAuthCachePath(_options.AuthCachePath);

        LoadTokenCache();
        _authenticator = new ConsoleAuthenticator(_options);
    }

    public SteamClient Client => _client;
    public SteamApps Apps => _steamApps;
    public SteamContent Content => _steamContent;

    public async Task ConnectAsync()
    {
        _callbackLoop = Task.Run(() => CallbackLoop(_callbackCts.Token));

        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connectedSub = _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult(true));
        using var disconnectedSub = _callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            if (!connectedTcs.Task.IsCompleted)
            {
                connectedTcs.TrySetException(new InvalidOperationException("Disconnected before connect completed."));
            }
        });

        Console.WriteLine("Connecting to Steam...");
        _client.Connect();
        await connectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine("Connected to Steam.");
    }

    public async Task LogOnAsync()
    {
        if (await TryLogOnWithCachedTokenAsync())
        {
            return;
        }

        if (_options.UseAnonymous)
        {
            var anonOk = await LogOnAnonymousAsync();
            if (anonOk)
            {
                return;
            }

            if (HasCredentials())
            {
                Console.WriteLine("Anonymous logon failed. Falling back to credential logon.");
            }
        }

        if (!HasCredentials())
        {
            throw new InvalidOperationException("Missing STEAM_USER / STEAM_PASS or --user / --pass.");
        }

        await LogOnWithCredentialsAsync();
    }

    public async Task LogOffAsync()
    {
        _steamUser.LogOff();
        _client.Disconnect();
        _callbackCts.Cancel();
        if (_callbackLoop != null)
        {
            await _callbackLoop;
        }
    }

    private bool HasCredentials()
        => !string.IsNullOrWhiteSpace(_options.Username) && !string.IsNullOrWhiteSpace(_options.Password);

    private async Task<bool> LogOnAnonymousAsync()
    {
        var logonTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var logonSub = _callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => logonTcs.TrySetResult(cb));

        var details = new SteamUser.AnonymousLogOnDetails
        {
            ClientOSType = EOSType.Windows10,
            ClientLanguage = "english"
        };

        Console.WriteLine("Logging in anonymously...");
        _steamUser.LogOnAnonymous(details);
        var result = await logonTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        if (result.Result == EResult.OK)
        {
            Console.WriteLine("Logged in anonymously.");
            return true;
        }

        Console.WriteLine($"Anonymous logon failed: {result.Result} ({result.ExtendedResult})");
        return false;
    }

    private async Task<bool> TryLogOnWithCachedTokenAsync()
    {
        if (_tokenCache == null ||
            string.IsNullOrWhiteSpace(_tokenCache.RefreshToken) ||
            string.IsNullOrWhiteSpace(_tokenCache.SteamId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_options.Username) &&
            !string.Equals(_options.Username, _tokenCache.Username, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ulong.TryParse(_tokenCache.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        // Refresh tokens may be revoked; clear the cache if logon fails so we can re-auth.
        Console.WriteLine("Trying cached refresh token...");
        var username = _options.Username ?? _tokenCache.Username;
        var loggedIn = await LogOnWithTokenAsync(username, _tokenCache.RefreshToken, TimeSpan.FromSeconds(30));
        if (!loggedIn)
        {
            ClearTokenCache();
        }
        return loggedIn;
    }

    private async Task LogOnWithCredentialsAsync()
    {
        var details = new AuthSessionDetails
        {
            Username = _options.Username,
            Password = _options.Password,
            DeviceFriendlyName = "steam-workshop-downloader",
            ClientOSType = EOSType.Windows10,
            IsPersistentSession = true,
            GuardData = _tokenCache?.GuardData,
            Authenticator = _authenticator
        };

        Console.WriteLine("Starting auth session...");
        var authSession = await _steamAuth.BeginAuthSessionViaCredentialsAsync(details);
        var pollResult = await authSession.PollingWaitForResultAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromMinutes(2));

        if (!string.IsNullOrWhiteSpace(pollResult.RefreshToken))
        {
            var guardData = string.IsNullOrWhiteSpace(pollResult.NewGuardData)
                ? _tokenCache?.GuardData
                : pollResult.NewGuardData;
            SaveTokenCache(_options.Username!, authSession.SteamID, pollResult.RefreshToken, guardData);
        }

        var loggedIn = await LogOnWithTokenAsync(pollResult.AccountName, pollResult.RefreshToken, TimeSpan.FromSeconds(60));
        if (!loggedIn)
        {
            throw new InvalidOperationException("Token logon failed after successful authentication.");
        }
    }

    private async Task<bool> LogOnWithTokenAsync(string username, string token, TimeSpan timeout)
    {
        var details = new SteamUser.LogOnDetails
        {
            Username = username,
            AccessToken = token,
            ShouldRememberPassword = true,
            ClientOSType = EOSType.Windows10
        };

        Console.WriteLine("Logging in with refresh token...");
        var result = await LogOnOnceAsync(details, timeout);
        if (result.Result == EResult.OK)
        {
            Console.WriteLine("Logged in.");
            return true;
        }

        Console.WriteLine($"Access-token logon failed: {result.Result} ({result.ExtendedResult})");
        return false;
    }

    private async Task<SteamUser.LoggedOnCallback> LogOnOnceAsync(SteamUser.LogOnDetails details, TimeSpan timeout)
    {
        var logonTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var logonSub = _callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => logonTcs.TrySetResult(cb));

        _steamUser.LogOn(details);
        return await logonTcs.Task.WaitAsync(timeout);
    }

    private void CallbackLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
        }
    }

    private void LoadTokenCache()
    {
        if (!File.Exists(_tokenPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_tokenPath);
            _tokenCache = JsonSerializer.Deserialize<AuthTokenCache>(json);
        }
        catch
        {
            _tokenCache = null;
        }
    }

    private void SaveTokenCache(string username, SteamID steamId, string refreshToken, string? guardData)
    {
        var cache = new AuthTokenCache
        {
            Username = username,
            SteamId = steamId.ConvertToUInt64().ToString(CultureInfo.InvariantCulture),
            RefreshToken = refreshToken,
            GuardData = guardData,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        var dir = Path.GetDirectoryName(_tokenPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(_tokenPath, json);
        _tokenCache = cache;
    }

    private void ClearTokenCache()
    {
        _tokenCache = null;
        if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }
    }

    private static string ResolveAuthCachePath(string? providedPath)
    {
        if (!string.IsNullOrWhiteSpace(providedPath))
        {
            var full = Path.GetFullPath(providedPath);
            if (Directory.Exists(full)
                || providedPath.EndsWith(Path.DirectorySeparatorChar)
                || providedPath.EndsWith(Path.AltDirectorySeparatorChar))
            {
                return Path.Combine(full, "auth.json");
            }

            return full;
        }

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return Path.GetFullPath("auth.json");
            }

            baseDir = Path.Combine(home, ".steam-workshop-downloader");
        }
        else
        {
            baseDir = Path.Combine(baseDir, "steam-workshop-downloader");
        }

        return Path.Combine(baseDir, "auth.json");
    }
}

/// <summary>
/// Serialized refresh-token cache stored on disk to skip repeated 2FA prompts.
/// </summary>
internal sealed class AuthTokenCache
{
    public string Username { get; set; } = string.Empty;
    public string SteamId { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string? GuardData { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// SteamKit authenticator that pulls 2FA codes from CLI/env or prompts the user.
/// </summary>
internal sealed class ConsoleAuthenticator : IAuthenticator
{
    private readonly Options _options;
    private bool _usedDeviceCode;
    private bool _usedEmailCode;

    public ConsoleAuthenticator(Options options)
    {
        _options = options;
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (!_usedDeviceCode && !string.IsNullOrWhiteSpace(_options.GuardCode))
        {
            _usedDeviceCode = true;
            return Task.FromResult(_options.GuardCode);
        }

        return Task.FromResult(Prompt("Steam Guard code"));
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        if (!_usedEmailCode && !string.IsNullOrWhiteSpace(_options.EmailCode))
        {
            _usedEmailCode = true;
            return Task.FromResult(_options.EmailCode);
        }

        return Task.FromResult(Prompt($"Email Steam Guard code ({email})"));
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        Console.WriteLine("Approve the sign-in request in the Steam Mobile app, then press Enter.");
        Console.ReadLine();
        return Task.FromResult(true);
    }

    private static string Prompt(string label)
    {
        Console.Write($"{label}: ");
        return Console.ReadLine() ?? string.Empty;
    }
}
