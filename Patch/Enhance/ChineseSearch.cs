using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaInfoKeeper.Options;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 为 Emby 搜索加载中文分词能力，并增强检索词处理与匹配范围。
    /// </summary>
    public static class ChineseSearch
    {
        private static readonly Version Ver4830 = new Version("4.8.3.0");
        private static readonly Version Ver4900 = new Version("4.9.0.0");
        private static readonly Version Ver4937 = new Version("4.9.0.37");

        private static Version appVer;
        private static Type raw;
        private static MethodInfo sqlite3_enable_load_extension;
        private static FieldInfo sqlite3_db;
        private static readonly List<MethodInfo> createConnectionTargets = new List<MethodInfo>();
        private static PropertyInfo dbFilePath;
        private static MethodInfo getJoinCommandText;
        private static MethodInfo createSearchTerm;
        private static MethodInfo cacheIdsFromTextParams;

        private static readonly object InitLock = new object();
        private static readonly object PhaseLock = new object();
        private static readonly object TokenizerStateLock = new object();
        private static string[] includeItemTypes = Array.Empty<string>();
        private static readonly HashSet<int> tokenizerLoadedConnections = new HashSet<int>();
        private static bool isInitialized;
        private static bool isConnectionPatched;
        private static bool areSearchFunctionsPatched;
        private static bool patchPhase2Initialized;
        public static bool IsReady => isInitialized;

        private static ILogger logger;
        private static Harmony harmony;

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static string tokenizerPath;
        private static readonly Dictionary<string, Regex> patterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        public static void Initialize(ILogger pluginLogger, EnhanceOptions options)
        {
            if (isInitialized)
            {
                Configure(options);
                return;
            }

            lock (InitLock)
            {
                if (isInitialized)
                {
                    Configure(options);
                    return;
                }

                logger = pluginLogger;
                harmony = new Harmony("mediainfokeeper.search");
                appVer = Plugin.Instance?.AppHost?.ApplicationVersion ?? new Version(0, 0, 0, 0);
                tokenizerPath = ResolveTokenizerPath();

                try
                {
                    var resolverVersion = Plugin.Instance?.AppHost?.ApplicationVersion ?? new Version(0, 0, 0, 0);
                    var sqlitePclEx = Assembly.Load("SQLitePCLRawEx.core");
                    raw = sqlitePclEx.GetType("SQLitePCLEx.raw");
                    sqlite3_enable_load_extension = raw?.GetMethod(
                        "sqlite3_enable_load_extension",
                        BindingFlags.Static | BindingFlags.Public);

                    sqlite3_db = typeof(SQLiteDatabaseConnection)
                        .GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);

                    var embySqlite = Assembly.Load("Emby.Sqlite");
                    var baseSqliteRepository = embySqlite.GetType("Emby.Sqlite.BaseSqliteRepository");

                    createConnectionTargets.Clear();
                    AddCreateConnectionTarget(baseSqliteRepository?.GetMethod(
                        "CreateNewConnection",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[] { typeof(bool) },
                        null));
                    AddCreateConnectionTarget(baseSqliteRepository?.GetMethod(
                        "CreateConnection",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[] { typeof(bool), typeof(CancellationToken) },
                        null));
                    AddCreateConnectionTarget(baseSqliteRepository?.GetMethod(
                        "CreateConnection",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[] { typeof(bool) },
                        null));

                    dbFilePath = baseSqliteRepository?.GetProperty(
                        "DbFilePath",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                    var sqliteItemRepository =
                        embyServerImplementationsAssembly.GetType(
                            "Emby.Server.Implementations.Data.SqliteItemRepository");
                    getJoinCommandText = sqliteItemRepository?.GetMethod(
                        "GetJoinCommandText",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[]
                        {
                            typeof(InternalItemsQuery),
                            typeof(List<KeyValuePair<string, string>>),
                            typeof(string),
                            typeof(string),
                            typeof(bool)
                        },
                        null);
                    createSearchTerm = PatchMethodResolver.Resolve(
                        sqliteItemRepository,
                        resolverVersion,
                        new MethodSignatureProfile
                        {
                            Name = "sqliteitemrepository-createsearchterm-exact",
                            MethodName = "CreateSearchTerm",
                            BindingFlags = BindingFlags.NonPublic | BindingFlags.Static,
                            ParameterTypes = new[] { typeof(string), typeof(bool) },
                            ReturnType = typeof(string),
                            IsStatic = true
                        },
                        logger,
                        "ChineseSearch.CreateSearchTerm");
                    if (createSearchTerm == null)
                    {
                        LogMethodCandidates(sqliteItemRepository, "CreateSearchTerm");
                    }
                    cacheIdsFromTextParams = PatchMethodResolver.Resolve(
                        sqliteItemRepository,
                        resolverVersion,
                        new MethodSignatureProfile
                        {
                            Name = "sqliteitemrepository-cacheidsfromtextparams-exact",
                            MethodName = "CacheIdsFromTextParams",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            ParameterTypes = new[] { typeof(InternalItemsQuery), typeof(IDatabaseConnection) },
                            IsStatic = false
                        },
                        logger,
                        "ChineseSearch.CacheIdsFromTextParams");

                    if (createConnectionTargets.Count == 0 || dbFilePath == null || getJoinCommandText == null ||
                        cacheIdsFromTextParams == null || sqlite3_db == null ||
                        sqlite3_enable_load_extension == null)
                    {
                        PatchLog.InitFailed(logger, nameof(ChineseSearch), "缺少反射目标");
                        return;
                    }

                    isInitialized = true;
                }
                catch (Exception e)
                {
                    logger?.Error("增强搜索初始化失败。");
                    logger?.Error(e.ToString());
                }
            }

            Configure(options);
        }

        public static void Configure(EnhanceOptions options)
        {
            if (!isInitialized || options == null)
            {
                return;
            }

            if (appVer >= Ver4830)
            {
                PatchPhase1();
            }
            else
            {
                ResetOptions();
            }
        }

        public static void UpdateSearchScope(string currentScope)
        {
            var searchScope = currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                              Array.Empty<string>();

            var includeTypes = new List<string>();
            foreach (var scope in searchScope)
            {
                if (Enum.TryParse(scope, true, out EnhanceOptions.SearchItemType type))
                {
                    switch (type)
                    {
                        case EnhanceOptions.SearchItemType.Collection:
                            includeTypes.AddRange(new[] { nameof(BoxSet) });
                            break;
                        case EnhanceOptions.SearchItemType.Episode:
                            includeTypes.AddRange(new[] { nameof(Episode) });
                            break;
                        case EnhanceOptions.SearchItemType.LiveTv:
                            includeTypes.AddRange(new[] { nameof(LiveTvChannel), nameof(LiveTvProgram), "LiveTVSeries" });
                            break;
                        case EnhanceOptions.SearchItemType.Movie:
                            includeTypes.AddRange(new[] { nameof(Movie) });
                            break;
                        case EnhanceOptions.SearchItemType.Person:
                            includeTypes.AddRange(new[] { nameof(Person) });
                            break;
                        case EnhanceOptions.SearchItemType.Playlist:
                            includeTypes.AddRange(new[] { nameof(Playlist) });
                            break;
                        case EnhanceOptions.SearchItemType.Series:
                            includeTypes.AddRange(new[] { nameof(Series) });
                            break;
                        case EnhanceOptions.SearchItemType.Season:
                            includeTypes.AddRange(new[] { nameof(Season) });
                            break;
                        case EnhanceOptions.SearchItemType.Video:
                            includeTypes.AddRange(new[] { nameof(Video) });
                            break;
                    }
                }
            }

            includeItemTypes = includeTypes.ToArray();
        }

        public static string[] GetSearchScope()
        {
            return includeItemTypes;
        }

        private static string ResolveTokenizerPath()
        {
            var basePath = AppContext.BaseDirectory;
            try
            {
                var appHost = Plugin.Instance?.AppHost;
                if (appHost != null)
                {
                    var applicationPaths = appHost.Resolve<MediaBrowser.Common.Configuration.IApplicationPaths>();
                    if (applicationPaths != null)
                    {
                        basePath = applicationPaths.PluginsPath;
                    }
                }
            }
            catch
            {
                // Fall back to base directory when application paths are unavailable.
            }

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return Path.Combine(basePath, "simple.dll");
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return Path.Combine(basePath, "libsimple");
                default:
                    return Path.Combine(basePath, "simple.dll");
            }
        }

        private static bool CreateConnectionPostfixPlatform()
        {
            if (isConnectionPatched || harmony == null || createConnectionTargets.Count == 0)
            {
                return isConnectionPatched;
            }

            var patchedAny = false;
            foreach (var method in createConnectionTargets)
            {
                patchedAny |= PatchCreateConnection(method);
            }

            isConnectionPatched = patchedAny;
            return patchedAny;
        }

        private static void AddCreateConnectionTarget(MethodInfo method)
        {
            if (method == null || createConnectionTargets.Contains(method))
            {
                return;
            }

            createConnectionTargets.Add(method);
        }

        private static bool PatchCreateConnection(MethodInfo targetMethod)
        {
            try
            {
                var parameters = targetMethod.GetParameters();
                var postfixName = nameof(CreateConnectionPostfixBoolOnly);
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(bool) &&
                    parameters[1].ParameterType == typeof(CancellationToken))
                {
                    postfixName = nameof(CreateConnectionPostfixBoolWithCancellation);
                }
                else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
                {
                    postfixName = nameof(CreateConnectionPostfixBoolOnly);
                }

                var postfix = new HarmonyMethod(typeof(ChineseSearch), postfixName);
                harmony.Patch(targetMethod, postfix: postfix);
                return true;
            }
            catch (Exception e)
            {
                logger?.Error("ChineseSearch patch CreateConnection failed: " + targetMethod?.Name);
                logger?.Error(e.ToString());
                return false;
            }
        }

        private static void PatchPhase1()
        {
            if (EnsureTokenizerExists() && CreateConnectionPostfixPlatform())
            {
                return;
            }

            logger?.Warn("增强搜索 PatchPhase1 失败。");
            ResetOptions();
        }

        private static void PatchPhase2(IDatabaseConnection connection)
        {
            var ftsTableName = GetFtsTableName();
            var rebuildFtsResult = true;
            var patchSearchFunctionsResult = false;
            var shouldLogLoadSuccess = false;
            var simpleTokenizerLoaded = LoadTokenizerExtension(connection, false);

            try
            {
                CurrentTokenizerName = DetectCurrentTokenizer(connection, ftsTableName);
                logger?.Info($"EnhanceChineseSearch - Current tokenizer (before) is {CurrentTokenizerName}");
                var options = Plugin.Instance?.Options?.Enhance;
                if (options != null)
                {
                    var shouldEnhance = options.EnhanceChineseSearch;
                    var shouldRestore = options.EnhanceChineseSearchRestore;
                    var shouldAutoRestore = !shouldEnhance && !shouldRestore;

                    if (shouldRestore)
                    {
                        if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                        {
                            rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                        }

                        if (rebuildFtsResult)
                        {
                            CurrentTokenizerName = "unicode61 remove_diacritics 2";
                            logger?.Info("增强搜索 - 恢复成功");
                        }

                        ResetOptions();
                    }
                    else if (shouldEnhance)
                    {
                        if (!simpleTokenizerLoaded)
                        {
                            if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                            {
                                rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                            }

                            if (rebuildFtsResult)
                            {
                                CurrentTokenizerName = "unicode61 remove_diacritics 2";
                                logger?.Warn("增强搜索 - simple 分词器不可用，已自动回退到 unicode61");
                            }

                            ResetOptions();
                        }
                        else
                        {
                            if (!string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                            {
                                rebuildFtsResult = RebuildFts(connection, ftsTableName, "simple");
                            }

                            if (rebuildFtsResult)
                            {
                                CurrentTokenizerName = "simple";
                                patchSearchFunctionsResult = PatchSearchFunctions();
                                shouldLogLoadSuccess = patchSearchFunctionsResult;
                            }
                        }
                    }
                    else if (shouldAutoRestore && string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                    {
                        rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                        if (rebuildFtsResult)
                        {
                            CurrentTokenizerName = "unicode61 remove_diacritics 2";
                            logger?.Info("增强搜索 - 检测到历史 simple tokenizer，已自动还原");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger?.Warn("EnhanceChineseSearch - Load Failed");
                logger?.Warn("增强搜索 - PatchPhase2 失败。");
                logger?.Warn(e.ToString());
            }

            var optionsAfterPatch = Plugin.Instance?.Options?.Enhance;
            var isEnhanceMode = optionsAfterPatch?.EnhanceChineseSearch == true;
            var hasUnknownTokenizer = string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal);
            var shouldResetOptions = (isEnhanceMode && !patchSearchFunctionsResult) || !rebuildFtsResult || hasUnknownTokenizer;
            if (shouldResetOptions)
            {
                logger?.Warn("EnhanceChineseSearch - Load Failed");
                ResetOptions();
            }
            else if (shouldLogLoadSuccess)
            {
                logger?.Info("EnhanceChineseSearch - Load Success");
            }

            logger?.Info($"EnhanceChineseSearch - Current tokenizer (after) is {CurrentTokenizerName}");
        }

        private static string GetFtsTableName()
        {
            return appVer >= Ver4830 ? "fts_search9" : "fts_search8";
        }

        private static string DetectCurrentTokenizer(IDatabaseConnection connection, string ftsTableName)
        {
            var tokenizerCheckQuery = $@"
                SELECT 
                    CASE 
                        WHEN instr(sql, 'tokenize=""simple""') > 0 THEN 'simple'
                        WHEN instr(sql, 'tokenize=""unicode61 remove_diacritics 2""') > 0 THEN 'unicode61 remove_diacritics 2'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM 
                    sqlite_master 
                WHERE 
                    type = 'table' AND 
                    name = '{ftsTableName}';";

            using (var statement = connection.PrepareStatement(tokenizerCheckQuery))
            {
                if (statement.MoveNext())
                {
                    return statement.Current?.GetString(0) ?? "unknown";
                }
            }

            return "unknown";
        }

        private static bool RebuildFts(IDatabaseConnection connection, string ftsTableName, string tokenizerName)
        {
            string populateQuery;

            if (appVer < Ver4900)
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization("Album") +
                    " from MediaItems";
            }
            else
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization(
                        "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                    " from MediaItems";
            }

            connection.BeginTransaction(TransactionMode.Deferred);
            try
            {
                connection.Execute($"DROP TABLE IF EXISTS {ftsTableName}");

                var createFtsTableQuery =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\", prefix='1 2 3 4')";
                connection.Execute(createFtsTableQuery);

                logger?.Info($"EnhanceChineseSearch - Filling {ftsTableName} Start");

                connection.Execute(populateQuery);
                connection.CommitTransaction();

                logger?.Info($"EnhanceChineseSearch - Filling {ftsTableName} Complete");

                return true;
            }
            catch (Exception e)
            {
                connection.RollbackTransaction();
                logger?.Warn("增强搜索 - 重建 FTS 失败。");
                logger?.Warn(e.ToString());
            }

            return false;
        }

        private static string GetSearchColumnNormalization(string columnName)
        {
            return "replace(replace(" + columnName + ",'''',''),'.','')";
        }

        private static bool EnsureTokenizerExists()
        {
            var resourceName = GetTokenizerResourceName();
            var expectedSha1 = GetExpectedSha1();

            if (resourceName == null || expectedSha1 == null)
            {
                return false;
            }

            try
            {
                if (File.Exists(tokenizerPath))
                {
                    var existingSha1 = ComputeSha1(tokenizerPath);
                    if (expectedSha1.ContainsValue(existingSha1))
                    {
                        var highestVersion = expectedSha1.Keys.Max();
                        var highestSha1 = expectedSha1[highestVersion];

                        if (existingSha1 != highestSha1)
                        {
                            ExportTokenizer(resourceName);
                        }

                        return true;
                    }

                    return true;
                }

                ExportTokenizer(resourceName);
                return true;
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - 检查分词器失败。");
                logger?.Warn(e.ToString());
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    logger?.Warn("增强搜索 - 未找到分词器资源: " + resourceName);
                    return;
                }

                using (var fileStream = new FileStream(tokenizerPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        private static string GetTokenizerResourceName()
        {
            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Tokenizer";
            var winSimpleTokenizer = $"{tokenizerNamespace}.win.simple.dll";
            var linuxSimpleTokenizer = $"{tokenizerNamespace}.linux.libsimple.so";

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return winSimpleTokenizer;
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return linuxSimpleTokenizer;
                default:
                    return null;
            }
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "a83d90af9fb88e75a1ddf2436c8b67954c761c83" },
                        { new Version(0, 5, 0), "aed57350b46b51bb7d04321b7fe8e5e60b0cdbdc" },
                        { new Version(0, 5, 2), "338bb0915d6f4625b54f041bdeb6791b6e590c4e" },
                        { new Version(0, 5, 3), "338bb0915d6f4625b54f041bdeb6791b6e590c4e" }
                    };
                case PlatformID.Unix:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "f7fb8ba0b98e358dfaa87570dc3426ee7f00e1b6" },
                        { new Version(0, 5, 0), "8e36162f96c67d77c44b36093f31ae4d297b15c0" },
                        { new Version(0, 5, 2), "e89eeb7938894e4e8b284896285e7dc90da715bc" },
                        { new Version(0, 5, 3), "a6188af48c0fef201cb24dbebc65c4cf5b4ddf9b" }
                    };
                default:
                    return null;
            }
        }

        private static string ComputeSha1(string filePath)
        {
            using (var sha1 = SHA1.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha1.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void ResetOptions()
        {
            var options = Plugin.Instance?.OptionsStore?.GetOptions();
            if (options?.Enhance == null)
            {
                return;
            }

            options.Enhance.EnhanceChineseSearch = false;
            options.Enhance.EnhanceChineseSearchRestore = false;
            Plugin.Instance.OptionsStore.SetOptions(options);
        }

        private static bool PatchSearchFunctions()
        {
            if (areSearchFunctionsPatched || harmony == null)
            {
                return areSearchFunctionsPatched;
            }

            try
            {
                harmony.Patch(getJoinCommandText,
                    postfix: new HarmonyMethod(typeof(ChineseSearch), nameof(GetJoinCommandTextPostfix)));
                if (createSearchTerm != null)
                {
                    harmony.Patch(createSearchTerm,
                        prefix: new HarmonyMethod(typeof(ChineseSearch), nameof(CreateSearchTermPrefix)));
                }
                else
                {
                    logger?.Warn("增强搜索 - 未安装 CreateSearchTerm patch：目标方法未找到。");
                }
                harmony.Patch(cacheIdsFromTextParams,
                    prefix: new HarmonyMethod(typeof(ChineseSearch), nameof(CacheIdsFromTextParamsPrefix)));

                areSearchFunctionsPatched = true;
                return true;
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - 补丁搜索函数失败。");
                logger?.Warn(e.ToString());
            }

            return false;
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            return LoadTokenizerExtension(connection, true);
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection, bool logErrors)
        {
            if (connection == null)
            {
                return false;
            }

            var connectionKey = RuntimeHelpers.GetHashCode(connection);
            lock (TokenizerStateLock)
            {
                if (tokenizerLoadedConnections.Contains(connectionKey))
                {
                    return true;
                }
            }

            try
            {
                var db = sqlite3_db.GetValue(connection);
                sqlite3_enable_load_extension.Invoke(raw, new[] { db, 1 });
                connection.Execute("SELECT load_extension('" + tokenizerPath + "')");

                lock (TokenizerStateLock)
                {
                    tokenizerLoadedConnections.Add(connectionKey);
                }

                return true;
            }
            catch (SQLiteException ex)
            {
                if (logErrors)
                {
                    logger?.Error("增强搜索 - 加载扩展失败: " + ex.Message);
                    logger?.Error(ex.StackTrace);
                }
            }
            catch (Exception e)
            {
                if (logErrors)
                {
                    logger?.Warn("增强搜索 - 加载分词器失败。");
                    logger?.Warn(e.ToString());
                }
            }

            return false;
        }

        private static void HandleConnectionCreated(object repositoryInstance, bool isReadOnly, IDatabaseConnection connection)
        {
            var db = dbFilePath.GetValue(repositoryInstance) as string;
            if (db?.EndsWith("library.db", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            // Emby 4.9 连接池会持续创建/复用新连接，simple 扩展必须按连接加载。
            LoadTokenizerExtension(connection, false);

            if (isReadOnly || patchPhase2Initialized)
            {
                return;
            }

            lock (PhaseLock)
            {
                if (patchPhase2Initialized)
                {
                    return;
                }

                PatchPhase2(connection);
                patchPhase2Initialized = true;
            }
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixBoolOnly(
            object __instance,
            [HarmonyArgument("isReadOnly")] bool isReadOnly,
            ref IDatabaseConnection __result)
        {
            HandleConnectionCreated(__instance, isReadOnly, __result);
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixBoolWithCancellation(
            object __instance,
            [HarmonyArgument("isReadOnly")] bool isReadOnly,
            [HarmonyArgument("cancellationToken")] CancellationToken cancellationToken,
            ref IDatabaseConnection __result)
        {
            HandleConnectionCreated(__instance, isReadOnly, __result);
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(
            InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams,
            string mediaItemsTableQualifier,
            ref StringBuilder __result)
        {
            var sql = __result.ToString();
            var newSql = sql;

            var hasMatchParam =
                newSql.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Regex.IsMatch(newSql, @"\bmatch\b\s*\(?\s*@SearchTerm\b", RegexOptions.IgnoreCase);

            if (!string.IsNullOrEmpty(query.SearchTerm) && hasMatchParam)
            {
                var options = Plugin.Instance?.Options?.Enhance;
                if (options?.EnhanceChineseSearch == true &&
                    string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                {
                    var excludeOriginalTitle = options.ExcludeOriginalTitleFromSearch;
                    var replacement = excludeOriginalTitle
                        ? "match '-OriginalTitle:' || simple_query(@SearchTerm)"
                        : "match simple_query(@SearchTerm)";

                    newSql = Regex.Replace(
                        newSql,
                        @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                        replacement,
                        RegexOptions.IgnoreCase);
                }
            }

            if (!string.IsNullOrEmpty(query.Name) &&
                hasMatchParam &&
                Plugin.Instance?.Options?.Enhance?.EnhanceChineseSearch == true &&
                string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
            {
                newSql = Regex.Replace(
                    newSql,
                    @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                    "match 'Name:' || simple_query(@SearchTerm)",
                    RegexOptions.IgnoreCase);

                for (var i = 0; i < bindParams.Count; i++)
                {
                    var kvp = bindParams[i];
                    if (kvp.Key == "@SearchTerm")
                    {
                        var currentValue = kvp.Value;
                        if (currentValue.StartsWith("Name:", StringComparison.Ordinal))
                        {
                            currentValue = currentValue
                                .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                                .Trim('\"', '^', '$')
                                .Replace(".", string.Empty)
                                .Replace("'", string.Empty);
                        }

                        bindParams[i] = new KeyValuePair<string, string>(kvp.Key, currentValue);
                    }
                }
            }

            if (!string.Equals(sql, newSql, StringComparison.Ordinal))
            {
                __result.Clear().Append(newSql);
            }
        }

        [HarmonyPrefix]
        private static bool CreateSearchTermPrefix(object[] __args, ref string __result)
        {
            if (__args == null || __args.Length == 0 || !(__args[0] is string searchTerm))
            {
                return true;
            }

            __result = searchTerm.Replace(".", string.Empty).Replace("'", string.Empty);
            return false;
        }

        private static void LogMethodCandidates(Type type, string methodName)
        {
            try
            {
                var candidates = type?.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                  BindingFlags.NonPublic)
                    .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                    .Select(m =>
                        $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}) -> {m.ReturnType?.Name}");

                PatchLog.Candidates(
                    logger,
                    string.Format("{0}.{1}", type?.FullName ?? "<null>", methodName ?? "<null>"),
                    string.Join("; ", candidates ?? Enumerable.Empty<string>()));
            }
            catch (Exception e)
            {
                logger?.Debug(e.Message);
            }
        }

        [HarmonyPrefix]
        private static bool CacheIdsFromTextParamsPrefix(InternalItemsQuery query, IDatabaseConnection db)
        {
            if ((query.PersonTypes?.Length ?? 0) == 0)
            {
                var nameStartsWith = query.NameStartsWith;
                if (!string.IsNullOrEmpty(nameStartsWith))
                {
                    query.SearchTerm = nameStartsWith;
                    query.NameStartsWith = null;
                }

                var searchTerm = query.SearchTerm;
                if (query.IncludeItemTypes.Length == 0 && !string.IsNullOrEmpty(searchTerm))
                {
                    query.IncludeItemTypes = GetSearchScope();
                }

                if (!string.IsNullOrEmpty(searchTerm))
                {
                    foreach (var provider in patterns)
                    {
                        var match = provider.Value.Match(searchTerm.Trim());
                        if (match.Success)
                        {
                            var idValue = provider.Key == "imdb" ? match.Value : match.Groups[2].Value;

                            query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                            {
                                new KeyValuePair<string, string>(provider.Key, idValue)
                            };
                            query.SearchTerm = null;
                            break;
                        }
                    }
                }

                if (appVer >= Ver4937 && !string.IsNullOrEmpty(query.SearchTerm))
                {
                    LoadTokenizerExtension(db, false);
                }
            }

            return true;
        }
    }
}
