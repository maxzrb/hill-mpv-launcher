using System.Collections;
using System.Net;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HillsMpvLauncher;

internal static class Program
{
    private const int ExitInputError = 20;
    private const int ExitMpvError = 22;
    private const int ExitFatalError = 90;

    public static int Main(string[] dotnetArgs)
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rawCommandLine = NativeMethods.GetRawCommandLine();
        var commandLineArgs = NativeMethods.ParseCommandLine(rawCommandLine);
        var suppliedArgs = commandLineArgs.Count > 0
            ? commandLineArgs.Skip(1).ToList()
            : dotnetArgs.ToList();
        var flags = WrapperFlags.Extract(suppliedArgs);

        var config = LauncherConfig.Load(baseDirectory);
        var logger = LauncherLogger.Create(config.ResolveLogDirectory(baseDirectory), config.Debug || flags.Debug);

        try
        {
            logger.Info("START", $"base_dir={Redactor.SafeLog(baseDirectory)}");
            logger.Info("START", $"config={Redactor.SafeLog(config.ConfigPath)} found={config.Found} debug={config.Debug || flags.Debug}");
            logger.Info("START", $"pid={Environment.ProcessId} started={DateTimeOffset.Now:O}");
            logger.Info("START", $"raw_command_line_sha256={Redactor.ShortHash(rawCommandLine)}");
            logger.Info("START", $"raw_command_line={Redactor.RedactCommandLine(rawCommandLine)}");
            logger.Info("PROCESS", $"current_directory={Redactor.SafeLog(NativeMethods.GetCurrentDirectory())}");
            LogParentProcess(logger);
            LogEnvironment(logger);
            LogArguments(logger, suppliedArgs);

            foreach (var warning in config.Warnings)
            {
                logger.Warn("CONFIG", warning);
            }

            if (flags.Help)
            {
                logger.Info("MODE", "help requested; no child process started");
                logger.Info("HELP", "--debug --dry-run --dump-args --help");
                return 0;
            }

            if (flags.DumpArgs)
            {
                logger.Info("MODE", "dump-args requested; no child process started");
                return 0;
            }

            var inspection = InvocationInspector.Inspect(flags.ForwardedArgs);
            LogInspection(logger, inspection, config);

            var plan = LaunchPlanner.Build(inspection, config, baseDirectory);
        LogPlan(logger, plan);

            if (!plan.CanLaunch)
            {
                logger.Error("INPUT", plan.FailureReason ?? "launcher input is not usable");
                return ExitInputError;
            }

            var commandLine = WindowsCommandLine.Build(plan.MpvPath, plan.MpvArguments);
            logger.Info("MPV", $"application={Redactor.SafeLog(plan.MpvPath)} working_directory={Redactor.SafeLog(plan.WorkingDirectory)}");
            logger.Info("MPV", $"command_line={Redactor.RedactText(commandLine)}");

            if (!WindowsCommandLine.TryValidateRoundTrip(plan.MpvPath, plan.MpvArguments, commandLine, out var roundTripError))
            {
                logger.Error("MPV", $"command_line_round_trip=false reason={Redactor.SafeLog(roundTripError)}");
                return ExitInputError;
            }

            logger.Info("MPV", "command_line_round_trip=true");

            if (flags.DryRun)
            {
                logger.Info("MODE", "dry-run requested; CreateProcessW was not called");
                return 0;
            }

            var exitCode = ProcessRunner.Run(plan, commandLine, logger);
            logger.Info("END", $"launcher_exit_code={exitCode}");
            return exitCode;
        }
        catch (Exception ex)
        {
            logger.Error("FATAL", $"{ex.GetType().Name}: {Redactor.SafeLog(ex.Message)}");
            logger.Debug("FATAL", Redactor.RedactText(ex.ToString()));
            return ExitFatalError;
        }
        finally
        {
            logger.Dispose();
        }
    }

    private static void LogArguments(LauncherLogger logger, IReadOnlyList<string> args)
    {
        logger.Info("ARGS", $"count={args.Count}");
        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];
            logger.Info("ARGS", $"argv[{index}] length={value.Length} sha256={Redactor.ShortHash(value)} value={Redactor.RedactText(value)}");
        }
    }

    private static void LogEnvironment(LauncherLogger logger)
    {
        var variables = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Select(entry => (Name: Convert.ToString(entry.Key) ?? string.Empty, Value: Convert.ToString(entry.Value) ?? string.Empty))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.Info("ENV", $"count={variables.Count}");
        foreach (var item in variables)
        {
            logger.Info("ENV", $"{Redactor.SafeLog(item.Name)}={Redactor.RedactEnvironmentValue(item.Name, item.Value)}");
        }
    }

    private static void LogParentProcess(LauncherLogger logger)
    {
        var parent = NativeMethods.GetParentProcess();
        if (parent is null)
        {
            logger.Warn("PROCESS", "parent_process=unavailable");
            return;
        }

        logger.Info("PROCESS", $"parent_pid={parent.ProcessId} parent_name={Redactor.SafeLog(parent.Name)} parent_path={Redactor.SafeLog(parent.Path ?? "unavailable")}");
    }

    private static void LogInspection(LauncherLogger logger, InvocationInspection inspection, LauncherConfig config)
    {
        logger.Info("INPUT", $"primary_index={inspection.PrimaryIndex} context_item_id={inspection.Context.HasItemId} context_media_source_id={inspection.Context.HasMediaSourceId} context_server={inspection.Context.HasServer} context_token={inspection.Context.HasToken}");
        logger.Info("INPUT", $"configured_emby_server={config.HasEmbyServer} configured_emby_token={config.HasEmbyToken}");

        foreach (var candidate in inspection.UrlCandidates)
        {
            logger.Info("URL", $"source=argv index={candidate.Index} auxiliary={candidate.IsAuxiliary} kind={candidate.Kind} emby={candidate.IsEmby} cdn115={candidate.Is115} {UrlDiagnostics.Describe(candidate.Value)}");
        }

        foreach (var candidate in inspection.EnvironmentUrlCandidates)
        {
            logger.Info("URL", $"source=env name={candidate.Source} kind={candidate.Kind} emby={candidate.IsEmby} cdn115={candidate.Is115} {UrlDiagnostics.Describe(candidate.Value)}");
        }

        if (inspection.UrlCandidates.Count == 0 && inspection.EnvironmentUrlCandidates.Count == 0)
        {
            logger.Warn("INPUT", "no HTTP(S) URL candidate found; local path or non-URL input may still be forwarded");
        }
    }

    private static void LogPlan(LauncherLogger logger, LaunchPlan plan)
    {
        logger.Info("PLAN", $"action={plan.Action} can_launch={plan.CanLaunch} primary_index={plan.PrimaryIndex} primary_before={Redactor.RedactText(plan.PrimaryBefore ?? "<none>")} selected={Redactor.RedactText(plan.SelectedUrl ?? "<none>")} selected_source={Redactor.SafeLog(plan.SelectedSource ?? "<none>")} resolver={Redactor.SafeLog(plan.ResolverDiagnostic ?? "<none>")}");
        logger.Info("PLAN", $"forwarded_argument_count={plan.MpvArguments.Count} extra_argument_count={plan.ExtraArgumentCount} exact_primary_forwarding={plan.ExactPrimaryForwarding}");
        logger.Info("PLAN", $"remote_startup_logo_safety_added={plan.RemoteStartupLogoSafetyAdded}");
        logger.Info("PLAN", $"remote_no_resume_safety_added={plan.RemoteNoResumeSafetyAdded}");
        if (plan.PrimaryBefore is not null)
        {
            logger.Info("URL", $"primary_before {UrlDiagnostics.Describe(plan.PrimaryBefore)}");
        }

        if (plan.SelectedUrl is not null)
        {
            logger.Info("URL", $"selected {UrlDiagnostics.Describe(plan.SelectedUrl)}");
        }
    }
}

internal sealed class LauncherConfig
{
    private readonly Dictionary<string, string> _values;

    private LauncherConfig(string baseDirectory, Dictionary<string, string> values, bool found, string configPath, List<string> warnings)
    {
        BaseDirectory = baseDirectory;
        _values = values;
        Found = found;
        ConfigPath = configPath;
        Warnings = warnings;

        Debug = ParseBool(Get("launcher.debug"), false);
        LogDirectory = Get("launcher.log_dir") ?? "logs";
        PreferEmbyUrl = ParseBool(Get("launcher.prefer_emby_url"), true);
        FallbackToOriginalUrl = ParseBool(Get("launcher.fallback_to_original_url"), true);
        MpvPath = Get("mpv.path") ?? Get("launcher.mpv_path");
        WorkingDirectory = Get("mpv.working_directory") ?? Get("launcher.working_directory");
        EmbyServer = Get("emby.server");
        EmbyDeviceId = Get("emby.device_id") ?? "HillsMpvLauncher";
        HillsDataDirectory = Get("hills.data_directory") ?? Get("resolver.hills_data_directory");
        var configuredToken = Get("emby.token");
        EmbyToken = !string.IsNullOrWhiteSpace(configuredToken)
            ? configuredToken
            : HillsCredentialReader.TryGetAccessToken(ResolveHillsDataDirectory(), EmbyServer);
        ResolveFromHillsCache = ParseBool(Get("hills.resolve_from_cache") ?? Get("resolver.resolve_from_hills_cache"), true);
        HillsResponseScanLimit = ParseInt(Get("hills.response_scan_limit"), 256, 16, 2048);
        HasEmbyServer = !string.IsNullOrWhiteSpace(EmbyServer);
        HasEmbyToken = !string.IsNullOrWhiteSpace(EmbyToken);
        ExtraArgs = ParseExtraArgs(Get("mpv.extra_args") ?? string.Empty, warnings);
    }

    public string BaseDirectory { get; }
    public bool Found { get; }
    public string ConfigPath { get; }
    public List<string> Warnings { get; }
    public bool Debug { get; }
    public string LogDirectory { get; }
    public bool PreferEmbyUrl { get; }
    public bool FallbackToOriginalUrl { get; }
    public string? MpvPath { get; }
    public string? WorkingDirectory { get; }
    public string? EmbyServer { get; }
    public string? EmbyToken { get; }
    public string EmbyDeviceId { get; }
    public string? HillsDataDirectory { get; }
    public bool ResolveFromHillsCache { get; }
    public int HillsResponseScanLimit { get; }
    public bool HasEmbyServer { get; }
    public bool HasEmbyToken { get; }
    public List<string> ExtraArgs { get; }

    public static LauncherConfig Load(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, "launcher.ini");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        if (File.Exists(configPath))
        {
            try
            {
                var section = string.Empty;
                foreach (var rawLine in File.ReadAllLines(configPath, new UTF8Encoding(false)))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                    {
                        continue;
                    }

                    if (line.StartsWith('[') && line.EndsWith(']'))
                    {
                        section = line[1..^1].Trim();
                        continue;
                    }

                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        warnings.Add($"ignored malformed config line: {Redactor.RedactText(line)}");
                        continue;
                    }

                    var key = line[..separator].Trim();
                    var value = line[(separator + 1)..].Trim();
                    var fullKey = string.IsNullOrWhiteSpace(section) ? key : $"{section}.{key}";
                    values[fullKey] = value;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"could not read config: {ex.GetType().Name}: {Redactor.SafeLog(ex.Message)}");
            }
        }

        return new LauncherConfig(baseDirectory, values, File.Exists(configPath), configPath, warnings);
    }

    public string ResolveLogDirectory(string baseDirectory)
    {
        return Path.IsPathRooted(LogDirectory)
            ? LogDirectory
            : Path.GetFullPath(Path.Combine(baseDirectory, LogDirectory));
    }

    public string ResolveMpvPath(string baseDirectory)
    {
        var path = string.IsNullOrWhiteSpace(MpvPath) ? "mpv.exe" : MpvPath.Trim();
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
    }

    public string ResolveWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return NativeMethods.GetCurrentDirectory();
        }

        var path = Path.IsPathRooted(WorkingDirectory)
            ? WorkingDirectory
            : Path.GetFullPath(Path.Combine(BaseDirectory, WorkingDirectory));
        return Directory.Exists(path) ? path : NativeMethods.GetCurrentDirectory();
    }

    public string? ResolveHillsDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(HillsDataDirectory))
        {
            var configured = Path.IsPathRooted(HillsDataDirectory)
                ? HillsDataDirectory
                : Path.GetFullPath(Path.Combine(BaseDirectory, HillsDataDirectory));
            return Directory.Exists(configured) ? configured : null;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packagesDirectory = Path.Combine(localAppData, "Packages");
            if (!Directory.Exists(packagesDirectory))
            {
                return null;
            }

            return Directory.GetDirectories(packagesDirectory, "Mountains.HillsLite_*")
                .Select(packageDirectory => Path.Combine(packageDirectory, "LocalCache", "Roaming", "com.mountains", "Hills"))
                .Where(directory => File.Exists(Path.Combine(directory, "shared_preferences.json")))
                .OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, "shared_preferences.json")))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private string? Get(string key)
    {
        return _values.TryGetValue(key, out var value) ? value : null;
    }

    private static List<string> ParseExtraArgs(string value, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        try
        {
            var parsed = NativeMethods.ParseCommandLine($"launcher.exe {value}");
            return parsed.Skip(1).ToList();
        }
        catch (Exception ex)
        {
            warnings.Add($"could not parse mpv.extra_args: {ex.GetType().Name}: {Redactor.SafeLog(ex.Message)}");
            return new List<string>();
        }
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static int ParseInt(string? value, int fallback, int minimum, int maximum)
    {
        return int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : fallback;
    }
}

internal static class HillsCredentialReader
{
    public static string? TryGetAccessToken(string? hillsDataDirectory, string? embyServer)
    {
        if (string.IsNullOrWhiteSpace(hillsDataDirectory) || string.IsNullOrWhiteSpace(embyServer))
        {
            return null;
        }

        var databasePath = Path.Combine(hillsDataDirectory, "hills_database.sqlite");
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT server_url, authenticate_json FROM emby_servers WHERE is_active = 1 ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var serverUrl = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!AreSameServer(serverUrl, embyServer))
                {
                    continue;
                }

                var authenticateJson = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (TryReadAccessToken(authenticateJson, out var accessToken))
                {
                    return accessToken;
                }
            }
        }
        catch (SqliteException)
        {
            // Hills 可能正在写数据库；没有读到令牌时回退到配置中的显式 token。
        }
        catch (IOException)
        {
            // 数据库暂时不可读时不阻止原始播放流程。
        }
        catch (UnauthorizedAccessException)
        {
            // 数据库权限异常时不阻止原始播放流程。
        }

        return null;
    }

    private static bool AreSameServer(string left, string right)
    {
        if (!Uri.TryCreate(left.TrimEnd('/'), UriKind.Absolute, out var leftUri)
            || !Uri.TryCreate(right.TrimEnd('/'), UriKind.Absolute, out var rightUri))
        {
            return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase)
            && leftUri.Port == rightUri.Port
            && string.Equals(leftUri.AbsolutePath.TrimEnd('/'), rightUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadAccessToken(string authenticateJson, out string accessToken)
    {
        accessToken = string.Empty;
        if (string.IsNullOrWhiteSpace(authenticateJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(authenticateJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("AccessToken", out var token)
                || token.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            accessToken = token.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(accessToken);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class WrapperFlags
{
    private WrapperFlags(List<string> forwardedArgs, bool debug, bool dryRun, bool dumpArgs, bool help)
    {
        ForwardedArgs = forwardedArgs;
        Debug = debug;
        DryRun = dryRun;
        DumpArgs = dumpArgs;
        Help = help;
    }

    public List<string> ForwardedArgs { get; }
    public bool Debug { get; }
    public bool DryRun { get; }
    public bool DumpArgs { get; }
    public bool Help { get; }

    public static WrapperFlags Extract(IReadOnlyList<string> args)
    {
        var forwarded = new List<string>(args.Count);
        var debug = false;
        var dryRun = false;
        var dumpArgs = false;
        var help = false;

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--debug":
                    debug = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--dump-args":
                    dumpArgs = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;
                default:
                    forwarded.Add(arg);
                    break;
            }
        }

        return new WrapperFlags(forwarded, debug, dryRun, dumpArgs, help);
    }
}

internal sealed class InvocationInspection
{
    public required IReadOnlyList<string> Arguments { get; init; }
    public required List<UrlCandidate> UrlCandidates { get; init; }
    public required List<UrlCandidate> EnvironmentUrlCandidates { get; init; }
    public required ContextEvidence Context { get; init; }
    public int PrimaryIndex { get; init; } = -1;
}

internal sealed record UrlCandidate(int Index, string Value, string Source, bool IsAuxiliary, bool IsEmby, bool Is115, string Kind);

internal sealed record ContextEvidence(bool HasItemId, bool HasMediaSourceId, bool HasServer, bool HasToken);

internal static class InvocationInspector
{
    private static readonly HashSet<string> AuxiliaryOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--audio-file", "--sub-file", "--external-file", "--playlist", "--cover-art-file"
    };

    private static readonly HashSet<string> OptionsWithSeparateValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "--audio-file", "--sub-file", "--external-file", "--playlist", "--script", "--script-opts",
        "--log-file", "--input-ipc-server", "--http-header-fields", "--referrer", "--user-agent",
        "--http-proxy", "--force-media-title", "--start", "--audio-client-name", "--title"
    };

    public static InvocationInspection Inspect(IReadOnlyList<string> args)
    {
        var candidates = new List<UrlCandidate>();
        for (var index = 0; index < args.Count; index++)
        {
            if (!TryGetHttpUrl(args[index], out var url))
            {
                continue;
            }

            var auxiliary = IsAuxiliaryValue(args, index);
            candidates.Add(CreateCandidate(index, url, $"argv[{index}]", auxiliary));
        }

        var environmentCandidates = new List<UrlCandidate>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = Convert.ToString(entry.Key) ?? string.Empty;
            var value = Convert.ToString(entry.Value) ?? string.Empty;
            if (!Regex.IsMatch(name, "(?i)(emby|media|stream|source|video|url)"))
            {
                continue;
            }

            if (Regex.IsMatch(name, "(?i)(referrer|proxy|user[_-]?agent|header|cookie|subtitle|audio|script)"))
            {
                continue;
            }

            if (TryGetHttpUrl(value, out var url) && IsEmbyUrl(url))
            {
                environmentCandidates.Add(CreateCandidate(-1, url, name, false));
            }
        }

        var contextText = string.Join('\n', args.Where((_, index) => !IsAuxiliaryValue(args, index)));
        var context = new ContextEvidence(
            Regex.IsMatch(contextText, "(?i)(item[_-]?id|/videos/\\d+)"),
            Regex.IsMatch(contextText, "(?i)media[_-]?source[_-]?id"),
            candidates.Any(candidate => !candidate.IsAuxiliary && candidate.IsEmby)
                || environmentCandidates.Any(candidate => candidate.IsEmby)
                || Regex.IsMatch(contextText, "(?i)server[_-]?url"),
            Regex.IsMatch(contextText, "(?i)(api[_-]?key|access[_-]?token|x-emby-token|(?:^|[?&])token=)"));

        return new InvocationInspection
        {
            Arguments = args,
            UrlCandidates = candidates,
            EnvironmentUrlCandidates = environmentCandidates,
            Context = context,
            PrimaryIndex = FindPrimaryIndex(args)
        };
    }

    public static bool TryGetHttpUrl(string value, out string url)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = value;
            return true;
        }

        url = string.Empty;
        return false;
    }

    public static bool TryGetOptionValue(IReadOnlyList<string> args, string optionName, out string value)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            var prefix = optionName + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = argument[prefix.Length..];
                return !string.IsNullOrWhiteSpace(value);
            }

            if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                value = args[index + 1];
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = string.Empty;
        return false;
    }

    public static bool TryGetMediaTitleForUrl(
        IReadOnlyList<string> args,
        int urlIndex,
        out string value)
    {
        if (urlIndex < 0 || urlIndex >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        if (TryGetMediaBlockRange(args, urlIndex, out var startIndex, out var endIndex))
        {
            return TryGetOptionValueInRange(args, "--force-media-title", startIndex, endIndex, out value);
        }

        // 没有 --{ ... --} 媒体块时，兼容 Hills 以前的单媒体调用格式。
        return TryGetOptionValue(args, "--force-media-title", out value);
    }

    private static bool TryGetOptionValueInRange(
        IReadOnlyList<string> args,
        string optionName,
        int startIndex,
        int endIndex,
        out string value)
    {
        var prefix = optionName + "=";
        var start = Math.Max(0, startIndex);
        var end = Math.Min(args.Count - 1, endIndex);
        for (var index = start; index <= end; index++)
        {
            var argument = args[index];
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = argument[prefix.Length..];
                return !string.IsNullOrWhiteSpace(value);
            }

            if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase)
                && index + 1 <= end
                && !string.Equals(args[index + 1], "--}", StringComparison.Ordinal))
            {
                value = args[index + 1];
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetMediaBlockRange(
        IReadOnlyList<string> args,
        int urlIndex,
        out int startIndex,
        out int endIndex)
    {
        var openIndex = -1;
        for (var index = 0; index <= urlIndex; index++)
        {
            if (string.Equals(args[index], "--{", StringComparison.Ordinal))
            {
                openIndex = index;
            }
            else if (string.Equals(args[index], "--}", StringComparison.Ordinal))
            {
                openIndex = -1;
            }
        }

        if (openIndex < 0)
        {
            startIndex = 0;
            endIndex = -1;
            return false;
        }

        startIndex = openIndex + 1;
        endIndex = args.Count - 1;
        for (var index = urlIndex + 1; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--}", StringComparison.Ordinal))
            {
                endIndex = index - 1;
                break;
            }
        }

        return startIndex <= endIndex;
    }

    public static bool IsEmbyUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        var query = uri.Query;
        return path.Contains("/emby/", StringComparison.OrdinalIgnoreCase)
            || (path.Contains("/videos/", StringComparison.OrdinalIgnoreCase)
                && (query.Contains("MediaSourceId", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("PlaySessionId", StringComparison.OrdinalIgnoreCase)
                    || query.Contains("DeviceId", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Contains("emby", StringComparison.OrdinalIgnoreCase)));
    }

    public static bool Is115Url(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Host.Contains("115cdn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("115.com", StringComparison.OrdinalIgnoreCase));
    }

    public static string GetKind(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "invalid-http-url";
        }

        if (IsEmbyUrl(value))
        {
            return "emby-session";
        }

        if (Is115Url(value))
        {
            return "115-cdn";
        }

        return uri.Scheme + "-url";
    }

    private static UrlCandidate CreateCandidate(int index, string value, string source, bool auxiliary)
    {
        return new UrlCandidate(index, value, source, auxiliary, IsEmbyUrl(value), Is115Url(value), GetKind(value));
    }

    private static int FindPrimaryIndex(IReadOnlyList<string> args)
    {
        var afterSeparator = false;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!afterSeparator && arg == "--")
            {
                afterSeparator = true;
                continue;
            }

            if (!afterSeparator && arg.StartsWith("-", StringComparison.Ordinal) && arg != "-")
            {
                if (!arg.Contains('=') && OptionsWithSeparateValues.Contains(arg) && index + 1 < args.Count)
                {
                    index++;
                }

                continue;
            }

            if (IsAuxiliaryValue(args, index))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool IsAuxiliaryValue(IReadOnlyList<string> args, int index)
    {
        if (index <= 0)
        {
            return false;
        }

        var previous = args[index - 1];
        var separator = previous.IndexOf('=');
        if (separator >= 0)
        {
            return false;
        }

        return AuxiliaryOptions.Contains(previous) || OptionsWithSeparateValues.Contains(previous);
    }
}

internal sealed record ResolvedSessionUrl(string Url, string Source, string Diagnostic);
internal sealed record MediaMatch(int Score, string ItemId, string SourceId, string SourceName, string Origin, string ItemName = "");

internal static class HillsCacheResolver
{
    private static readonly object ApiMatchCacheLock = new();
    private static readonly Dictionary<string, (DateTimeOffset ExpiresAt, List<MediaMatch> Matches)> ApiMatchCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveSessionUrl(
        InvocationInspection inspection,
        LauncherConfig config,
        out ResolvedSessionUrl resolved,
        out string reason)
    {
        return TryResolveSessionUrl(
            inspection,
            config,
            inspection.PrimaryIndex,
            null,
            out resolved,
            out reason);
    }

    public static bool TryResolveSessionUrl(
        InvocationInspection inspection,
        LauncherConfig config,
        int mediaIndex,
        string? mediaTitleOverride,
        out ResolvedSessionUrl resolved,
        out string reason)
    {
        resolved = null!;

        if (!config.ResolveFromHillsCache)
        {
            reason = "hills-cache-resolver-disabled";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.EmbyServer))
        {
            reason = "emby.server-not-configured";
            return false;
        }

        if (mediaIndex < 0 || mediaIndex >= inspection.Arguments.Count)
        {
            reason = "media-index-invalid";
            return false;
        }

        var mediaTitle = mediaTitleOverride;
        if (string.IsNullOrWhiteSpace(mediaTitle)
            && !InvocationInspector.TryGetMediaTitleForUrl(inspection.Arguments, mediaIndex, out mediaTitle))
        {
            reason = "force-media-title-not-found";
            return false;
        }

        var hillsDataDirectory = config.ResolveHillsDataDirectory();
        if (hillsDataDirectory is null)
        {
            reason = "hills-data-directory-not-found";
            return false;
        }

        var mediaFileName = GetMediaFileName(inspection.Arguments[mediaIndex]);
        var matches = new List<MediaMatch>();
        var rememberReason = string.Empty;
        var preferencesPath = Path.Combine(hillsDataDirectory, "shared_preferences.json");
        CollectRememberTrackMatches(preferencesPath, mediaTitle, mediaFileName, matches, out rememberReason);

        MediaMatch? best = null;
        var bestReason = string.Empty;
        if (matches.Count > 0 && TrySelectBest(matches, out best, out bestReason) && best is not null && best.Score >= 1200)
        {
            return BuildResolvedUrl(config, best, out resolved, out reason);
        }

        var responseReason = string.Empty;
        CollectResponseCacheMatches(
            Path.Combine(hillsDataDirectory, "cache", "response"),
            config.HillsResponseScanLimit,
            mediaTitle,
            mediaFileName,
            matches,
            out responseReason);

        if (matches.Count > 0
            && TrySelectBest(matches, out best, out bestReason)
            && best is not null)
        {
            return BuildResolvedUrl(config, best, out resolved, out reason);
        }

        // 缓存只保存了季信息或尚未保存下一集详情时，用 Hills 同一会话的令牌查询 Emby。
        matches.Clear();
        var apiReason = string.Empty;
        CollectEmbyApiMatches(inspection, config, mediaTitle, mediaFileName, matches, out apiReason);
        if (matches.Count == 0)
        {
            reason = $"rememberTracks={rememberReason};response_cache={responseReason};emby_api={apiReason}";
            return false;
        }

        if (!TrySelectBest(matches, out best, out bestReason) || best is null)
        {
            reason = bestReason;
            return false;
        }

        return BuildResolvedUrl(config, best, out resolved, out reason);
    }

    private static void CollectEmbyApiMatches(
        InvocationInspection inspection,
        LauncherConfig config,
        string mediaTitle,
        string mediaFileName,
        ICollection<MediaMatch> matches,
        out string reason)
    {
        reason = "not-run";
        if (string.IsNullOrWhiteSpace(config.EmbyToken))
        {
            reason = "emby-api-token-not-available";
            return;
        }

        if (!Uri.TryCreate(config.EmbyServer, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            reason = "emby.server-is-not-http-url";
            return;
        }

        var searchTerm = GetSeriesSearchTerm(mediaTitle);
        var cacheKey = config.EmbyServer.TrimEnd('/') + "|" + searchTerm;
        lock (ApiMatchCacheLock)
        {
            if (ApiMatchCache.TryGetValue(cacheKey, out var cached)
                && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                foreach (var match in cached.Matches)
                {
                    var score = ScoreMatch(mediaTitle, mediaFileName, match.ItemName, match.SourceName);
                    if (score > 0)
                    {
                        matches.Add(match with { Score = score });
                    }
                }

                reason = "cache-hit";
                return;
            }
        }

        var requestUri = new UriBuilder(serverUri)
        {
            Path = serverUri.AbsolutePath.TrimEnd('/') + "/emby/Items",
            Query = "SearchTerm=" + Uri.EscapeDataString(searchTerm)
                + "&Recursive=true&IncludeItemTypes=Episode%2CMovie"
                + "&Fields=MediaSources%2CParentId%2CSeriesId&Limit=200"
                + "&api_key=" + Uri.EscapeDataString(config.EmbyToken)
        }.Uri;

        try
        {
            using var handler = new HttpClientHandler();
            if (InvocationInspector.TryGetOptionValue(inspection.Arguments, "--http-proxy", out var proxy)
                && Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri))
            {
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HillsMpvLauncher/1.0");
            using var response = client.GetAsync(requestUri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                reason = $"http-{(int)response.StatusCode}";
                return;
            }

            var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(payload);
            var apiCandidates = new List<MediaMatch>();
            foreach (var item in EnumerateResponseItems(document.RootElement))
            {
                if (!TryGetStringProperty(item, out var itemId, "Id", "id")
                    || !item.TryGetProperty("MediaSources", out var mediaSources)
                    || mediaSources.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var itemName = TryGetStringProperty(item, out var parsedItemName, "Name", "name")
                    ? parsedItemName
                    : string.Empty;
                foreach (var mediaSource in mediaSources.EnumerateArray())
                {
                    if (!TryGetStringProperty(mediaSource, out var sourceId, "Id", "id")
                        || !TryGetStringProperty(mediaSource, out var sourceName, "Name", "name"))
                    {
                        continue;
                    }

                    apiCandidates.Add(new MediaMatch(0, itemId, sourceId, sourceName, "emby-api", itemName));
                }
            }

            foreach (var candidate in apiCandidates)
            {
                var score = ScoreMatch(mediaTitle, mediaFileName, candidate.ItemName, candidate.SourceName);
                if (score > 0)
                {
                    matches.Add(candidate with { Score = score });
                }
            }

            if (matches.Count > 0)
            {
                lock (ApiMatchCacheLock)
                {
                    ApiMatchCache[cacheKey] = (
                        DateTimeOffset.UtcNow.AddSeconds(30),
                        apiCandidates);
                }

                reason = "matched";
            }
            else
            {
                reason = "no-media-match";
            }
        }
        catch (TaskCanceledException)
        {
            reason = "timeout";
        }
        catch (HttpRequestException)
        {
            reason = "request-failed";
        }
        catch (JsonException)
        {
            reason = "response-json-invalid";
        }
        catch (InvalidOperationException)
        {
            reason = "request-invalid";
        }
    }

    private static string GetSeriesSearchTerm(string mediaTitle)
    {
        var match = Regex.Match(mediaTitle, "^(?<series>.+?)\\s+S\\d{1,2}E\\d{1,3}\\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["series"].Value.Trim() : mediaTitle.Trim();
    }

    private static void CollectRememberTrackMatches(
        string preferencesPath,
        string mediaTitle,
        string mediaFileName,
        ICollection<MediaMatch> matches,
        out string reason)
    {
        reason = "not-read";
        if (!File.Exists(preferencesPath))
        {
            reason = "hills-shared-preferences-not-found";
            return;
        }

        try
        {
            using var preferences = JsonDocument.Parse(File.ReadAllText(preferencesPath, new UTF8Encoding(false)));
            if (!TryGetStringProperty(preferences.RootElement, "flutter.rememberTracks", out var tracksJson))
            {
                reason = "hills-remember-tracks-not-found";
                return;
            }

            using var tracks = JsonDocument.Parse(tracksJson);
            if (tracks.RootElement.ValueKind != JsonValueKind.Array)
            {
                reason = "hills-remember-tracks-is-not-array";
                return;
            }

            foreach (var track in tracks.RootElement.EnumerateArray())
            {
                if (!TryGetStringProperty(track, "itemId", out var itemId)
                    || !TryGetStringProperty(track, "sourceId", out var sourceId)
                    || !TryGetStringProperty(track, "sourceName", out var sourceName))
                {
                    continue;
                }

                var score = ScoreMatch(mediaTitle, mediaFileName, string.Empty, sourceName);
                if (score > 0)
                {
                    matches.Add(new MediaMatch(score, itemId, sourceId, sourceName, "rememberTracks"));
                }
            }

            reason = matches.Count > 0 ? "matched" : "hills-remember-tracks-no-media-match";
        }
        catch (JsonException)
        {
            reason = "hills-shared-preferences-json-invalid";
        }
        catch (IOException)
        {
            reason = "hills-shared-preferences-read-failed";
        }
        catch (UnauthorizedAccessException)
        {
            reason = "hills-shared-preferences-access-denied";
        }
    }

    private static void CollectResponseCacheMatches(
        string responseDirectory,
        int scanLimit,
        string mediaTitle,
        string mediaFileName,
        ICollection<MediaMatch> matches,
        out string reason)
    {
        reason = "not-read";
        if (!Directory.Exists(responseDirectory))
        {
            reason = "hills-response-cache-not-found";
            return;
        }

        var scanned = 0;
        try
        {
            var files = new DirectoryInfo(responseDirectory)
                .EnumerateFiles("*.file", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(scanLimit);

            foreach (var file in files)
            {
                scanned++;
                try
                {
                    using var outer = JsonDocument.Parse(File.ReadAllText(file.FullName, new UTF8Encoding(false)));
                    if (!outer.RootElement.TryGetProperty("data", out var data)
                        || data.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var bytes = new byte[data.GetArrayLength()];
                    var byteIndex = 0;
                    foreach (var value in data.EnumerateArray())
                    {
                        bytes[byteIndex++] = value.GetByte();
                    }

                    using var payload = JsonDocument.Parse(new UTF8Encoding(false).GetString(bytes));
                    foreach (var item in EnumerateResponseItems(payload.RootElement))
                    {
                        if (!TryGetStringProperty(item, out var itemId, "Id", "id")
                            || !item.TryGetProperty("MediaSources", out var mediaSources)
                            || mediaSources.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var itemName = TryGetStringProperty(item, out var parsedItemName, "Name", "name")
                            ? parsedItemName
                            : string.Empty;
                        foreach (var mediaSource in mediaSources.EnumerateArray())
                        {
                            if (!TryGetStringProperty(mediaSource, out var sourceId, "Id", "id")
                                || !TryGetStringProperty(mediaSource, out var sourceName, "Name", "name"))
                            {
                                continue;
                            }

                            var score = ScoreMatch(mediaTitle, mediaFileName, itemName, sourceName);
                            if (score > 0)
                            {
                                matches.Add(new MediaMatch(score, itemId, sourceId, sourceName, "response-cache", itemName));
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // 响应缓存可能包含旧格式或未写完的文件，跳过即可。
                }
                catch (IOException)
                {
                    // Hills 可能正在写入缓存，单个文件不可读时继续扫描其他文件。
                }
                catch (UnauthorizedAccessException)
                {
                    // 单个缓存文件权限异常不应阻止原始 URL 回退。
                }
            }

            reason = scanned == 0 ? "hills-response-cache-empty" : "hills-response-cache-no-media-match";
        }
        catch (IOException)
        {
            reason = "hills-response-cache-read-failed";
        }
        catch (UnauthorizedAccessException)
        {
            reason = "hills-response-cache-access-denied";
        }
    }

    private static IEnumerable<JsonElement> EnumerateResponseItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
        }
    }

    private static bool TrySelectBest(
        IEnumerable<MediaMatch> matches,
        out MediaMatch? best,
        out string reason)
    {
        var distinct = matches
            .GroupBy(match => (match.ItemId, match.SourceId), StringTupleComparer.Instance)
            .Select(group => group.OrderByDescending(match => match.Score).First())
            .OrderByDescending(match => match.Score)
            .ToList();

        if (distinct.Count == 0)
        {
            best = null;
            reason = "no-reliable-media-match";
            return false;
        }

        if (distinct.Count > 1 && distinct[0].Score == distinct[1].Score)
        {
            best = null;
            reason = "ambiguous-media-match";
            return false;
        }

        best = distinct[0];
        reason = "selected";
        return true;
    }

    private static bool BuildResolvedUrl(
        LauncherConfig config,
        MediaMatch best,
        out ResolvedSessionUrl resolved,
        out string reason)
    {
        resolved = null!;
        if (!Uri.TryCreate(config.EmbyServer!.TrimEnd('/'), UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            reason = "emby.server-is-not-http-url";
            return false;
        }

        var videoItemId = GetVideoItemId(best);
        var sessionUrl = new StringBuilder(config.EmbyServer.TrimEnd('/'))
            .Append("/emby/videos/")
            .Append(Uri.EscapeDataString(videoItemId))
            .Append("/original.mkv?MediaSourceId=")
            .Append(Uri.EscapeDataString(best.SourceId))
            .Append("&DeviceId=")
            .Append(Uri.EscapeDataString(config.EmbyDeviceId))
            .ToString();

        if (!string.IsNullOrWhiteSpace(config.EmbyToken))
        {
            sessionUrl += "&api_key=" + Uri.EscapeDataString(config.EmbyToken);
        }

        resolved = new ResolvedSessionUrl(
            sessionUrl,
            $"hills-cache-{best.Origin}",
            $"match_score={best.Score} item_id={videoItemId} media_source_id={best.SourceId}");
        reason = "resolved";
        return true;
    }

    private static string GetVideoItemId(MediaMatch best)
    {
        const string mediaSourcePrefix = "mediasource_";
        if (best.SourceId.StartsWith(mediaSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = best.SourceId[mediaSourcePrefix.Length..];
            if (suffix.Length > 0 && suffix.All(char.IsDigit))
            {
                // Hills 的 rememberTracks 在剧集场景中保存的是季 ID；媒体源 ID 的数字部分才是具体视频 ID。
                return suffix;
            }
        }

        return best.ItemId;
    }

    private static int ScoreMatch(string mediaTitle, string mediaFileName, string itemName, string sourceName)
    {
        var normalizedTitle = Normalize(mediaTitle);
        var normalizedFileName = NormalizeMediaName(mediaFileName);
        var normalizedItemName = Normalize(itemName);
        var normalizedSourceName = NormalizeMediaName(sourceName);

        if (normalizedFileName.Length > 0 && normalizedFileName == normalizedSourceName)
        {
            return 1200;
        }

        if (normalizedFileName.Length > 0
            && normalizedSourceName.Length > 0
            && (normalizedFileName.StartsWith(normalizedSourceName + " ", StringComparison.OrdinalIgnoreCase)
                || normalizedSourceName.StartsWith(normalizedFileName + " ", StringComparison.OrdinalIgnoreCase)))
        {
            return 1100;
        }

        if (normalizedTitle.Length > 0 && normalizedSourceName == normalizedTitle)
        {
            return 1000;
        }

        if (normalizedTitle.Length > 0
            && normalizedSourceName.StartsWith(normalizedTitle + " ", StringComparison.OrdinalIgnoreCase))
        {
            return 900;
        }

        if (normalizedTitle.Length > 0 && normalizedItemName == normalizedTitle)
        {
            return 850;
        }

        if (normalizedTitle.Length > 0
            && normalizedSourceName.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
        {
            return 700;
        }

        return 0;
    }

    private static string NormalizeMediaName(string value)
    {
        var fileName = Path.GetFileName(value.Trim());
        foreach (var extension in new[] { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts" })
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                fileName = fileName[..^extension.Length];
                break;
            }
        }

        return Normalize(fileName);
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        return TryGetStringProperty(element, out value, propertyName);
    }

    private static bool TryGetStringProperty(JsonElement element, out string value, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.String)
                {
                    value = property.GetString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(value);
                }
            }
        }

        value = string.Empty;
        return false;
    }

    private static string GetMediaFileName(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(value.Trim(), "\\s+", " ").ToUpperInvariant();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ItemId, string SourceId)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string ItemId, string SourceId) x, (string ItemId, string SourceId) y)
        {
            return string.Equals(x.ItemId, y.ItemId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.SourceId, y.SourceId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string ItemId, string SourceId) obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ItemId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceId));
        }
    }
}

internal sealed class LaunchPlan
{
    public required string MpvPath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required List<string> MpvArguments { get; init; }
    public required string Action { get; init; }
    public required int PrimaryIndex { get; init; }
    public required string? PrimaryBefore { get; init; }
    public required string? SelectedUrl { get; init; }
    public required string? SelectedSource { get; init; }
    public required bool ExactPrimaryForwarding { get; init; }
    public required bool CanLaunch { get; init; }
    public required string? FailureReason { get; init; }
    public required int ExtraArgumentCount { get; init; }
    public required bool RemoteStartupLogoSafetyAdded { get; init; }
    public required bool RemoteNoResumeSafetyAdded { get; init; }
    public required string ResolverDiagnostic { get; init; }
}

internal static class LaunchPlanner
{
    public static LaunchPlan Build(InvocationInspection inspection, LauncherConfig config, string baseDirectory)
    {
        var forwarded = inspection.Arguments.ToList();
        var primaryIndex = inspection.PrimaryIndex;
        var primaryBefore = primaryIndex >= 0 && primaryIndex < forwarded.Count ? forwarded[primaryIndex] : null;
        var selected = inspection.UrlCandidates.FirstOrDefault(candidate => !candidate.IsAuxiliary && candidate.IsEmby)
            ?? inspection.EnvironmentUrlCandidates.FirstOrDefault(candidate => candidate.IsEmby);
        var action = "forward-original";
        var selectedUrl = primaryBefore;
        var selectedSource = primaryBefore is null ? null : "argv-primary";
        var canLaunch = true;
        string? failureReason = null;
        var resolverDiagnostic = "not-needed";

        if (primaryIndex < 0)
        {
            canLaunch = false;
            failureReason = "no primary media argument found";
            action = "reject-no-media";
        }
        else if (config.PreferEmbyUrl && selected is not null && !string.Equals(primaryBefore, selected.Value, StringComparison.Ordinal))
        {
            forwarded[primaryIndex] = selected.Value;
            selectedUrl = selected.Value;
            selectedSource = selected.Source;
            action = "replace-primary-with-emby";
        }
        else if (primaryBefore is not null && InvocationInspector.Is115Url(primaryBefore) && selected is null)
        {
            var mediaCandidates = inspection.UrlCandidates
                .Where(candidate => !candidate.IsAuxiliary && candidate.Is115)
                .OrderBy(candidate => candidate.Index)
                .ToList();
            var diagnostics = new List<string>();
            var resolvedCount = 0;
            ResolvedSessionUrl? primaryResolved = null;

            foreach (var candidate in mediaCandidates)
            {
                if (!config.PreferEmbyUrl)
                {
                    diagnostics.Add($"index={candidate.Index}:prefer-emby-disabled");
                    continue;
                }

                if (!InvocationInspector.TryGetMediaTitleForUrl(inspection.Arguments, candidate.Index, out var mediaTitle))
                {
                    diagnostics.Add($"index={candidate.Index}:force-media-title-not-found");
                    continue;
                }

                if (HillsCacheResolver.TryResolveSessionUrl(
                    inspection,
                    config,
                    candidate.Index,
                    mediaTitle,
                    out var resolved,
                    out var resolverReason))
                {
                    forwarded[candidate.Index] = resolved.Url;
                    resolvedCount++;
                    diagnostics.Add($"index={candidate.Index}:{resolved.Diagnostic}");
                    if (candidate.Index == primaryIndex)
                    {
                        primaryResolved = resolved;
                    }
                }
                else
                {
                    diagnostics.Add($"index={candidate.Index}:{resolverReason}");
                }
            }

            resolverDiagnostic = diagnostics.Count > 0
                ? string.Join(";", diagnostics)
                : "no-115-media-candidates";

            if (primaryResolved is not null)
            {
                selectedUrl = primaryResolved.Url;
                selectedSource = primaryResolved.Source;
                if (mediaCandidates.Count > 1)
                {
                    action = resolvedCount == mediaCandidates.Count
                        ? "replace-115-playlist-with-emby-sessions"
                        : "replace-115-playlist-with-emby-sessions-partial";
                }
                else
                {
                    action = "replace-115-with-emby-session";
                }

                if (resolvedCount < mediaCandidates.Count && !config.FallbackToOriginalUrl)
                {
                    canLaunch = false;
                    failureReason = "one or more 115 media blocks could not be resolved to an Emby session";
                }
            }
            else
            {
                selectedUrl = primaryBefore;
                selectedSource = "argv-primary-fallback";
                action = mediaCandidates.Count > 1
                    ? "fallback-original-115-playlist"
                    : "fallback-original-115-cdn";
                if (!config.FallbackToOriginalUrl)
                {
                    canLaunch = false;
                    failureReason = "only a 115 CDN URL was supplied and no reliable Emby context was found";
                }
            }
        }
        else if (primaryBefore is not null && InvocationInspector.IsEmbyUrl(primaryBefore))
        {
            selectedUrl = primaryBefore;
            selectedSource = "argv-primary-emby";
            action = "forward-emby-session";
        }
        else if (!config.PreferEmbyUrl && selected is not null)
        {
            action = "prefer-emby-disabled";
        }

        forwarded.AddRange(config.ExtraArgs);
        var remoteStartupLogoSafetyAdded = false;
        if (IsRemoteMediaInvocation(inspection, selectedUrl)
            && !HasStartupFormatLogoModeOverride(forwarded))
        {
            // 远程签名流不适合 startup-format-logos 的 ffmpeg 后瞻探测，避免额外 Range 请求触发误判损坏。
            forwarded.Add("--script-opts-append=startup_format_logos-mode=none");
            remoteStartupLogoSafetyAdded = true;
        }
        var remoteNoResumeSafetyAdded = false;
        if (IsRemoteMediaInvocation(inspection, selectedUrl)
            && !HasResumePlaybackOverride(forwarded))
        {
            // Hills 已传入当前集的 --start，不能再让 mpv 的 watch-later 恢复旧 playlist 位置。
            forwarded.Add("--no-resume-playback");
            remoteNoResumeSafetyAdded = true;
        }

        return new LaunchPlan
        {
            MpvPath = config.ResolveMpvPath(baseDirectory),
            WorkingDirectory = config.ResolveWorkingDirectory(),
            MpvArguments = forwarded,
            Action = action,
            PrimaryIndex = primaryIndex,
            PrimaryBefore = primaryBefore,
            SelectedUrl = selectedUrl,
            SelectedSource = selectedSource,
            ExactPrimaryForwarding = primaryBefore is null || selectedUrl is null || string.Equals(primaryBefore, selectedUrl, StringComparison.Ordinal),
            CanLaunch = canLaunch,
            FailureReason = failureReason,
            ExtraArgumentCount = config.ExtraArgs.Count,
            RemoteStartupLogoSafetyAdded = remoteStartupLogoSafetyAdded,
            RemoteNoResumeSafetyAdded = remoteNoResumeSafetyAdded,
            ResolverDiagnostic = resolverDiagnostic
        };
    }

    private static bool IsRemoteMediaInvocation(InvocationInspection inspection, string? selectedUrl)
    {
        if (selectedUrl is not null
            && Uri.TryCreate(selectedUrl, UriKind.Absolute, out var selectedUri)
            && (selectedUri.Scheme == Uri.UriSchemeHttp || selectedUri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        return inspection.UrlCandidates.Any(candidate =>
            candidate.Index == inspection.PrimaryIndex
            && !candidate.IsAuxiliary
            && (candidate.IsEmby || candidate.Is115));
    }

    private static bool HasStartupFormatLogoModeOverride(IEnumerable<string> arguments)
    {
        return arguments.Any(argument =>
            argument.Contains("startup_format_logos-mode=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasResumePlaybackOverride(IEnumerable<string> arguments)
    {
        return arguments.Any(argument =>
            string.Equals(argument, "--resume-playback", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--no-resume-playback", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class ProcessRunner
{
    private const int ExitMpvError = 22;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const int StartfUseStdHandles = 0x00000100;

    public static int Run(LaunchPlan plan, string commandLine, LauncherLogger logger)
    {
        if (!File.Exists(plan.MpvPath))
        {
            logger.Error("MPV", $"executable not found: {Redactor.SafeLog(plan.MpvPath)}");
            return ExitMpvError;
        }

        if (!Directory.Exists(plan.WorkingDirectory))
        {
            logger.Warn("MPV", $"working directory unavailable, using launcher current directory: {Redactor.SafeLog(plan.WorkingDirectory)}");
        }

        var stderrRead = IntPtr.Zero;
        var stderrWrite = IntPtr.Zero;
        var stdoutRead = IntPtr.Zero;
        var stdoutWrite = IntPtr.Zero;
        var childStdout = IntPtr.Zero;
        var childStdin = IntPtr.Zero;
        var processInfo = default(NativeMethods.PROCESS_INFORMATION);
        Task stderrTask = Task.CompletedTask;
        Task stdoutTask = Task.CompletedTask;
        var diagnostics = new MpvErrorClassifier();
        var created = false;
        try
        {
            var security = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
                InheritHandle = 1
            };

            if (!NativeMethods.CreatePipe(out stderrRead, out stderrWrite, ref security, 0))
            {
                var error = Marshal.GetLastWin32Error();
                logger.Error("MPV", $"CreatePipe(stderr) failed, error={error}");
                return ExitMpvError;
            }

            if (!NativeMethods.SetHandleInformation(stderrRead, NativeMethods.HandleFlagInherit, 0))
            {
                var error = Marshal.GetLastWin32Error();
                logger.Error("MPV", $"SetHandleInformation(stderr) failed, error={error}");
                return ExitMpvError;
            }

            var stdoutCaptureEnabled = false;
            if (!NativeMethods.CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0))
            {
                var error = Marshal.GetLastWin32Error();
                logger.Warn("MPV", $"CreatePipe(stdout) failed, error={error}; falling back to inherited stdout");
            }
            else if (!NativeMethods.SetHandleInformation(stdoutRead, NativeMethods.HandleFlagInherit, 0))
            {
                var error = Marshal.GetLastWin32Error();
                logger.Warn("MPV", $"SetHandleInformation(stdout) failed, error={error}; falling back to inherited stdout");
                NativeMethods.CloseHandle(stdoutRead);
                NativeMethods.CloseHandle(stdoutWrite);
                stdoutRead = IntPtr.Zero;
                stdoutWrite = IntPtr.Zero;
            }
            else
            {
                childStdout = stdoutWrite;
                stdoutWrite = IntPtr.Zero;
                stdoutCaptureEnabled = true;
                logger.Info("MPV", "stdout_forwarding=pipe-to-parent-standard-output");
            }

            if (!stdoutCaptureEnabled)
            {
                var parentStdout = NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle);
                childStdout = NativeMethods.DuplicateInheritableHandle(parentStdout);
                if (NativeMethods.IsInvalidHandle(childStdout))
                {
                    childStdout = NativeMethods.OpenNullHandle(NativeMethods.GenericWrite, ref security);
                    logger.Warn("MPV", "stdout_forwarding=unavailable; using NUL");
                }
                else
                {
                    logger.Info("MPV", "stdout_forwarding=parent-standard-output");
                }
            }

            childStdin = NativeMethods.OpenNullHandle(NativeMethods.GenericRead, ref security);
            if (NativeMethods.IsInvalidHandle(childStdin))
            {
                logger.Warn("MPV", "stdin_redirect=NUL unavailable; using inherited standard input");
                childStdin = NativeMethods.DuplicateInheritableHandle(NativeMethods.GetStdHandle(NativeMethods.StdInputHandle));
            }

            if (NativeMethods.IsInvalidHandle(childStdout)
                || NativeMethods.IsInvalidHandle(childStdin))
            {
                logger.Error("MPV", "standard handle preparation failed");
                return ExitMpvError;
            }

            var startupInfo = new NativeMethods.STARTUPINFO
            {
                cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                dwFlags = StartfUseStdHandles,
                hStdInput = childStdin,
                hStdOutput = childStdout,
                hStdError = stderrWrite
            };
            var mutableCommandLine = new StringBuilder(commandLine);
            created = NativeMethods.CreateProcessW(
                plan.MpvPath,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateUnicodeEnvironment | CreateNoWindow,
                IntPtr.Zero,
                Directory.Exists(plan.WorkingDirectory) ? plan.WorkingDirectory : null,
                ref startupInfo,
                out processInfo);

            if (!created)
            {
                var error = Marshal.GetLastWin32Error();
                logger.Error("MPV", $"CreateProcessW failed, error={error} application={Redactor.SafeLog(plan.MpvPath)}");
                return ExitMpvError;
            }

            logger.Info("MPV", $"started pid={processInfo.dwProcessId} reporter_stdout_passthrough=true stderr_capture=true");
            NativeMethods.CloseHandle(stderrWrite);
            stderrWrite = IntPtr.Zero;
            NativeMethods.CloseHandle(childStdout);
            childStdout = IntPtr.Zero;
            NativeMethods.CloseHandle(childStdin);
            childStdin = IntPtr.Zero;

            stderrTask = CaptureStderrAsync(stderrRead, logger, diagnostics);
            stderrRead = IntPtr.Zero;
            if (stdoutCaptureEnabled)
            {
                stdoutTask = ForwardStdoutAsync(stdoutRead, logger, diagnostics);
                stdoutRead = IntPtr.Zero;
            }

            var waitResult = NativeMethods.WaitForSingleObject(processInfo.hProcess, Infinite);
            if (waitResult == WaitFailed)
            {
                var error = Marshal.GetLastWin32Error();
                logger.Error("MPV", $"WaitForSingleObject failed, error={error}");
                return ExitMpvError;
            }

            if (!NativeMethods.GetExitCodeProcess(processInfo.hProcess, out var childCode))
            {
                var error = Marshal.GetLastWin32Error();
                logger.Error("MPV", $"GetExitCodeProcess failed, error={error}");
                return ExitMpvError;
            }

            var exitCode = childCode <= int.MaxValue ? (int)childCode : 1;
            logger.Info("MPV", $"child_exit_code={exitCode}");
            return exitCode;
        }
        finally
        {
            if (created)
            {
                try
                {
                    stderrTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.Warn("MPV", $"stderr_capture_failed={ex.GetType().Name}");
                }

                try
                {
                    stdoutTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.Warn("MPV", $"stdout_capture_failed={ex.GetType().Name}");
                }

                logger.Info("MPV", $"stderr_diagnostics={diagnostics.Summary}");
                NativeMethods.CloseHandle(processInfo.hThread);
                NativeMethods.CloseHandle(processInfo.hProcess);
            }

            CloseHandleIfValid(stderrRead);
            CloseHandleIfValid(stderrWrite);
            CloseHandleIfValid(stdoutRead);
            CloseHandleIfValid(stdoutWrite);
            CloseHandleIfValid(childStdout);
            CloseHandleIfValid(childStdin);
        }
    }

    private static async Task CaptureStderrAsync(IntPtr readHandle, LauncherLogger logger, MpvErrorClassifier diagnostics)
    {
        using var safeHandle = new SafeFileHandle(readHandle, ownsHandle: true);
        using var stream = new FileStream(safeHandle, FileAccess.Read, 4096, isAsync: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync() is { } line)
        {
            diagnostics.Observe(line);
            logger.Info("MPV-STDERR", Redactor.RedactText(line));
        }
    }

    private static async Task ForwardStdoutAsync(IntPtr readHandle, LauncherLogger logger, MpvErrorClassifier diagnostics)
    {
        using var safeHandle = new SafeFileHandle(readHandle, ownsHandle: true);
        using var stream = new FileStream(safeHandle, FileAccess.Read, 4096, isAsync: false);
        var output = Console.OpenStandardOutput();
        var buffer = new byte[4096];
        var pendingLine = new List<byte>();
        byte[]? lastTimePositionLine = null;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                pendingLine.Add(value);
                if (value == (byte)'\n')
                {
                    lastTimePositionLine = await ForwardStdoutLineAsync(
                        pendingLine,
                        output,
                        logger,
                        diagnostics,
                        lastTimePositionLine);
                    pendingLine.Clear();
                }
            }
        }

        if (pendingLine.Count > 0)
        {
            await ForwardStdoutLineAsync(
                pendingLine,
                output,
                logger,
                diagnostics,
                lastTimePositionLine);
        }
    }

    private static async Task<byte[]?> ForwardStdoutLineAsync(
        IReadOnlyCollection<byte> bytes,
        Stream output,
        LauncherLogger logger,
        MpvErrorClassifier diagnostics,
        byte[]? lastTimePositionLine)
    {
        var rawLine = bytes.ToArray();
        var line = new UTF8Encoding(false).GetString(rawLine).TrimEnd('\r', '\n');
        if (line.Length == 0)
        {
            await output.WriteAsync(rawLine.AsMemory());
            await output.FlushAsync();
            return lastTimePositionLine;
        }

        diagnostics.Observe(line);
        if (TryGetReporterEvent(line, out var eventName, out var reason))
        {
            if (string.Equals(eventName, "start-file", StringComparison.OrdinalIgnoreCase))
            {
                lastTimePositionLine = null;
            }
            else if (string.Equals(eventName, "time-pos", StringComparison.OrdinalIgnoreCase))
            {
                lastTimePositionLine = rawLine;
            }

            if (string.Equals(eventName, "end-file", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(reason, "eof", StringComparison.OrdinalIgnoreCase))
            {
                if (lastTimePositionLine is not null)
                {
                    await output.WriteAsync(lastTimePositionLine.AsMemory());
                    await output.FlushAsync();
                }

                logger.Info(
                    "HILLS-REPORTER",
                    $"suppressed_non_completion_end_file reason={Redactor.SafeLog(reason.Length == 0 ? "<unknown>" : reason)} last_time_pos_forwarded={lastTimePositionLine is not null}");
                return lastTimePositionLine;
            }

            await output.WriteAsync(rawLine.AsMemory());
            await output.FlushAsync();
            logger.Info("HILLS-REPORTER", Redactor.RedactText(line));
            return lastTimePositionLine;
        }

        await output.WriteAsync(rawLine.AsMemory());
        await output.FlushAsync();
        logger.Info("MPV-STDOUT", Redactor.RedactText(line));
        return lastTimePositionLine;
    }

    private static bool TryGetReporterEvent(string line, out string eventName, out string reason)
    {
        const string prefix = "HILLS_MPV_EVENT:";
        eventName = string.Empty;
        reason = string.Empty;

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed[prefix.Length..]);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("event", out var eventProperty)
                && eventProperty.ValueKind == JsonValueKind.String)
            {
                eventName = eventProperty.GetString() ?? string.Empty;
            }

            if (document.RootElement.TryGetProperty("reason", out var reasonProperty)
                && reasonProperty.ValueKind == JsonValueKind.String)
            {
                reason = reasonProperty.GetString() ?? string.Empty;
            }

            return eventName.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void CloseHandleIfValid(IntPtr handle)
    {
        if (!NativeMethods.IsInvalidHandle(handle))
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}

internal sealed class MpvErrorClassifier
{
    private readonly HashSet<string> _categories = new(StringComparer.Ordinal);

    public int LineCount { get; private set; }

    public string Summary => _categories.Count == 0
        ? "none"
        : string.Join(',', _categories.OrderBy(category => category, StringComparer.Ordinal));

    public void Observe(string line)
    {
        LineCount++;
        if (Regex.IsMatch(line, "(?i)\\b403\\b|invalid signature|signaturedoesnotmatch|accessdenied|forbidden"))
        {
            _categories.Add("http-403-or-signature");
        }

        if (Regex.IsMatch(line, "(?i)\\b(?:timeout|timed out|timedout)\\b|connection.*(?:timeout|timed out)|could not connect|network is unreachable|connection refused"))
        {
            _categories.Add("network-timeout-or-connect");
        }

        if (Regex.IsMatch(line, "(?i)decoder|decod(?:e|er)|codec|failed to initialize video|video output.*failed|unknown format|invalid data found"))
        {
            _categories.Add("decode-or-codec");
        }

        if (Regex.IsMatch(line, "(?i)failed to open|loading failed|error opening|http error"))
        {
            _categories.Add("open-or-loading-failure");
        }
    }
}

internal static class WindowsCommandLine
{
    public static string Build(string executablePath, IReadOnlyList<string> args)
    {
        var all = new List<string>(args.Count + 1) { executablePath };
        all.AddRange(args);
        return string.Join(' ', all.Select(Quote));
    }

    public static bool TryValidateRoundTrip(string executablePath, IReadOnlyList<string> args, string commandLine, out string error)
    {
        try
        {
            var expected = new List<string>(args.Count + 1) { executablePath };
            expected.AddRange(args);
            var actual = NativeMethods.ParseCommandLine(commandLine);
            if (actual.Count != expected.Count)
            {
                error = $"argument_count expected={expected.Count} actual={actual.Count}";
                return false;
            }

            for (var index = 0; index < expected.Count; index++)
            {
                if (!string.Equals(expected[index], actual[index], StringComparison.Ordinal))
                {
                    error = $"argument[{index}] differs expected_length={expected[index].Length} actual_length={actual[index].Length}";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"round-trip parser failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string Quote(string value)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("an argument contains a NUL character");
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}

internal static class UrlDiagnostics
{
    public static string Describe(string value)
    {
        var special = new[] { '&', '%', '+', '=', '?', '#', ';', ',', '"', '\'', '\\', ' ' }
            .Where(value.Contains)
            .Aggregate(new StringBuilder(), (builder, character) => builder.Append(character))
            .ToString();
        var malformedPercent = Regex.IsMatch(value, "%(?![0-9A-Fa-f]{2})");
        var validUri = Uri.TryCreate(value, UriKind.Absolute, out var uri);
        var host = validUri ? uri!.Host : "<invalid>";
        var hasRepeatedScheme = Regex.IsMatch(value, "(?i)https?://.*https?://");
        var type = validUri
            ? InvocationInspector.GetKind(value)
            : "invalid";

        return $"type={type} host={Redactor.SafeLog(host)} length={value.Length} sha256={Redactor.ShortHash(value)} special={Redactor.SafeLog(special.Length == 0 ? "<none>" : special)} malformed_percent={malformedPercent} repeated_scheme={hasRepeatedScheme} valid_uri={validUri}";
    }
}

internal static class Redactor
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string RedactText(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return RedactUrl(value);
        }

        var result = Regex.Replace(value, "(?i)https?://[^\\s\\\"']+", match => RedactUrl(match.Value));
        result = Regex.Replace(
            result,
            "(?i)(api[_-]?key|access[_-]?token|x-emby-token|token|sign(?:ature)?|cookie|password)=([^&\\s\\\"']+)",
            "$1=<redacted>");
        result = Regex.Replace(result, "(?i)([?&])(u|s)=([^&\\s\\\"']+)", "$1$2=<redacted>");
        result = Regex.Replace(result, "(?i)(x-emby-token|authorization|proxy-authorization|cookie|api[_-]?key|access[_-]?token)\\s*:\\s*[^;\\\"'\\r\\n]+", "$1: <redacted>");
        return SafeLog(result);
    }

    public static string RedactCommandLine(string value)
    {
        try
        {
            var args = NativeMethods.ParseCommandLine(value);
            return string.Join(" ", args.Select((argument, index) => index == 0 ? SafeLog(argument) : RedactText(argument)));
        }
        catch
        {
            return RedactText(value);
        }
    }

    public static string RedactUrl(string value)
    {
        var queryIndex = value.IndexOf('?');
        if (queryIndex < 0)
        {
            return RedactTextWithoutUrl(value);
        }

        var prefix = value[..queryIndex];
        var query = value[(queryIndex + 1)..];
        var is115 = InvocationInspector.Is115Url(value);
        var parts = query.Split('&', StringSplitOptions.None);
        for (var index = 0; index < parts.Length; index++)
        {
            var separator = parts[index].IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = parts[index][..separator];
            if (is115 || IsSensitiveKey(key))
            {
                parts[index] = key + "=<redacted>";
            }
        }

        return SafeLog(prefix + "?" + string.Join('&', parts));
    }

    public static string RedactEnvironmentValue(string name, string value)
    {
        if (IsSensitiveKey(name))
        {
            return $"<redacted length={value.Length}>";
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return RedactUrl(value);
        }

        if (value.Length > 512)
        {
            return $"<long length={value.Length} sha256={ShortHash(value)}>";
        }

        return RedactTextWithoutUrl(value);
    }

    public static string SafeLog(string value)
    {
        return value
            .Replace("\0", "\\0", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    public static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Utf8.GetBytes(value));
        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        return normalized is "s" or "u" or "token" or "sign" or "signature" or "apikey" or "accesstoken" or "xembytoken"
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("accesstoken", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal);
    }

    private static string RedactTextWithoutUrl(string value)
    {
        var result = Regex.Replace(
            value,
            "(?i)(api[_-]?key|access[_-]?token|x-emby-token|token|sign(?:ature)?|cookie|password)=([^&\\s\\\"']+)",
            "$1=<redacted>");
        result = Regex.Replace(result, "(?i)([?&])(u|s)=([^&\\s\\\"']+)", "$1$2=<redacted>");
        result = Regex.Replace(result, "(?i)(x-emby-token|authorization|proxy-authorization|cookie|api[_-]?key|access[_-]?token)\\s*:\\s*[^;\\\"'\\r\\n]+", "$1: <redacted>");
        return SafeLog(result);
    }
}

internal sealed class LauncherLogger : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly bool _debug;

    private LauncherLogger(string? path, bool debug)
    {
        _path = path;
        _debug = debug;
    }

    public static LauncherLogger Create(string directory, bool debug)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"launcher-{DateTime.Now:yyyyMMdd}.log");
            return new LauncherLogger(path, debug);
        }
        catch
        {
            return new LauncherLogger(null, debug);
        }
    }

    public void Info(string category, string message) => Write("INFO", category, message);
    public void Warn(string category, string message) => Write("WARN", category, message);
    public void Error(string category, string message) => Write("ERROR", category, message);

    public void Debug(string category, string message)
    {
        if (_debug)
        {
            Write("DEBUG", category, message);
        }
    }

    private void Write(string level, string category, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}][{level}][{category}] {Redactor.SafeLog(message)}";
        lock (_gate)
        {
            try
            {
                if (_path is not null)
                {
                    File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom);
                }
            }
            catch
            {
                // 日志故障不能阻止 launcher 继续尝试启动 mpv。
            }
        }
    }

    public void Dispose()
    {
    }
}

internal sealed record ParentProcessInfo(uint ProcessId, string Name, string? Path);

internal static class NativeMethods
{
    private const uint Th32csSnappProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal const int StdInputHandle = -10;
    internal const int StdOutputHandle = -11;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint HandleFlagInherit = 0x00000001;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    internal static bool IsInvalidHandle(IntPtr handle)
    {
        return handle == IntPtr.Zero || handle == InvalidHandleValue;
    }

    internal static IntPtr DuplicateInheritableHandle(IntPtr sourceHandle)
    {
        if (IsInvalidHandle(sourceHandle)
            || !DuplicateHandle(GetCurrentProcess(), sourceHandle, GetCurrentProcess(), out var targetHandle, 0, true, DuplicateSameAccess))
        {
            return IntPtr.Zero;
        }

        return targetHandle;
    }

    internal static IntPtr OpenNullHandle(uint desiredAccess, ref SECURITY_ATTRIBUTES securityAttributes)
    {
        var handle = CreateFileW(
            "NUL",
            desiredAccess,
            FileShareRead | FileShareWrite,
            ref securityAttributes,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        return IsInvalidHandle(handle) ? IntPtr.Zero : handle;
    }

    public static string GetRawCommandLine()
    {
        var pointer = GetCommandLineW();
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty;
    }

    public static List<string> ParseCommandLine(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(commandLine))
        {
            return result;
        }

        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CommandLineToArgvW failed, error={Marshal.GetLastWin32Error()}");
        }

        try
        {
            for (var index = 0; index < count; index++)
            {
                var argumentPointer = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(argumentPointer) ?? string.Empty);
            }
        }
        finally
        {
            LocalFree(pointer);
        }

        return result;
    }

    public static string GetCurrentDirectory()
    {
        var builder = new StringBuilder(32768);
        var length = GetCurrentDirectoryW((uint)builder.Capacity, builder);
        return length == 0 ? Environment.CurrentDirectory : builder.ToString();
    }

    public static ParentProcessInfo? GetParentProcess()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnappProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return null;
            }

            uint parentId = 0;
            string parentName = "unknown";
            do
            {
                if (entry.th32ProcessID == Environment.ProcessId)
                {
                    parentId = entry.th32ParentProcessID;
                    break;
                }
            }
            while (Process32NextW(snapshot, ref entry));

            if (parentId == 0)
            {
                return null;
            }

            var path = TryGetProcessPath(parentId);
            if (path is not null)
            {
                parentName = Path.GetFileName(path);
            }

            return new ParentProcessInfo(parentId, parentName, path);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string? TryGetProcessPath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder(32768);
            var size = builder.Capacity;
            return QueryFullProcessImageNameW(process, 0, builder, ref size)
                ? builder.ToString()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCommandLineW")]
    private static extern IntPtr GetCommandLineW();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCurrentDirectoryW(uint length, [Out] StringBuilder buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out IntPtr targetHandle,
        uint desiredAccess,
        bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SECURITY_ATTRIBUTES pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] StringBuilder imageName, ref int size);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(
        string? applicationName,
        [In, Out] StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
