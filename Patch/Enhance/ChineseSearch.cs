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
using System.Runtime.InteropServices;
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
        private static Type raw;
        private static MethodInfo sqlite3_enable_load_extension;
        private static FieldInfo sqlite3_db;
        private static MethodInfo createConnection;
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
        private static bool enhanceChineseSearchEnabled;
        private static bool enhanceChineseSearchRestoreEnabled;
        private static bool excludeOriginalTitleFromSearch;
        public static bool IsReady => isInitialized;

        private static ILogger logger;
        private static Harmony harmony;

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static string tokenizerPath;
        private static readonly Dictionary<string, Regex> patterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "imdb_name", new Regex(@"^nm\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "douban", new Regex(@"^douban(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "bangumi", new Regex(@"^(bgm(id)?|bangumi)=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        public static void Initialize(ILogger pluginLogger, EnhanceOptions options)
        {
            if (isInitialized)
            {
                Configure(
                    options?.EnhanceChineseSearch == true,
                    options?.EnhanceChineseSearchRestore == true,
                    options?.SearchScope,
                    options?.ExcludeOriginalTitleFromSearch == true);
                return;
            }

            lock (InitLock)
            {
                if (isInitialized)
                {
                    Configure(
                        options?.EnhanceChineseSearch == true,
                        options?.EnhanceChineseSearchRestore == true,
                        options?.SearchScope,
                        options?.ExcludeOriginalTitleFromSearch == true);
                    return;
                }

                logger = pluginLogger;
                harmony = new Harmony("mediainfokeeper.search");
                tokenizerPath = ResolveTokenizerPath();

                try
                {
                    var resolverVersion = Plugin.Instance?.AppHost?.ApplicationVersion ?? new Version(0, 0, 0, 0);
                    var sqlitePclEx = Assembly.Load("SQLitePCLRawEx.core");
                    raw = sqlitePclEx.GetType("SQLitePCLEx.raw");
                    var sqlite3Type = sqlitePclEx.GetType("SQLitePCLEx.sqlite3");
                    sqlite3_enable_load_extension = PatchMethodResolver.Resolve(
                        raw,
                        sqlitePclEx.GetName().Version,
                        new MethodSignatureProfile
                        {
                            Name = "sqlitepclex-raw-enable-load-extension-exact",
                            MethodName = "sqlite3_enable_load_extension",
                            BindingFlags = BindingFlags.Static | BindingFlags.Public,
                            ParameterTypes = sqlite3Type == null ? null : new[] { sqlite3Type, typeof(int) },
                            ReturnType = typeof(int),
                            IsStatic = true
                        },
                        logger,
                        "ChineseSearch.sqlite3_enable_load_extension");

                    sqlite3_db = typeof(SQLiteDatabaseConnection)
                        .GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);

                    var embySqlite = Assembly.Load("Emby.Sqlite");
                    var baseSqliteRepository = embySqlite.GetType("Emby.Sqlite.BaseSqliteRepository");

                    createConnection = PatchMethodResolver.Resolve(
                        baseSqliteRepository,
                        embySqlite.GetName().Version,
                        new MethodSignatureProfile
                        {
                            Name = "basesqliterepository-createconnection-exact",
                            MethodName = "CreateConnection",
                            BindingFlags = BindingFlags.NonPublic | BindingFlags.Instance,
                            ParameterTypes = new[] { typeof(bool), typeof(CancellationToken) },
                            ReturnType = typeof(IDatabaseConnection),
                            IsStatic = false
                        },
                        logger,
                        "ChineseSearch.BaseSqliteRepository.CreateConnection");
                    dbFilePath = baseSqliteRepository?.GetProperty(
                        "DbFilePath",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                    var sqliteItemRepository =
                        embyServerImplementationsAssembly.GetType(
                            "Emby.Server.Implementations.Data.SqliteItemRepository");
                    getJoinCommandText = PatchMethodResolver.Resolve(
                        sqliteItemRepository,
                        embyServerImplementationsAssembly.GetName().Version,
                        new MethodSignatureProfile
                        {
                            Name = "sqliteitemrepository-getjoincommandtext-exact",
                            MethodName = "GetJoinCommandText",
                            BindingFlags = BindingFlags.NonPublic | BindingFlags.Instance,
                            ParameterTypes = new[]
                            {
                                typeof(InternalItemsQuery),
                                typeof(List<KeyValuePair<string, string>>),
                                typeof(string)
                            },
                            ReturnType = typeof(StringBuilder),
                            IsStatic = false
                        },
                        logger,
                        "ChineseSearch.SqliteItemRepository.GetJoinCommandText");
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

                    if (createConnection == null || dbFilePath == null || getJoinCommandText == null ||
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

            Configure(
                options?.EnhanceChineseSearch == true,
                options?.EnhanceChineseSearchRestore == true,
                options?.SearchScope,
                options?.ExcludeOriginalTitleFromSearch == true);
        }

        public static void Configure(
            bool enableChineseSearch,
            bool enableChineseSearchRestore,
            string searchScope,
            bool excludeOriginalTitle)
        {
            enhanceChineseSearchEnabled = enableChineseSearch;
            enhanceChineseSearchRestoreEnabled = enableChineseSearchRestore;
            excludeOriginalTitleFromSearch = excludeOriginalTitle;
            UpdateSearchScope(searchScope);

            if (!isInitialized)
            {
                return;
            }

            if (EnsureTokenizerExists() && PatchCreateConnection())
            {
                return;
            }

            logger?.Warn("增强搜索初始化失败。");
            ResetOptions();
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
                case PlatformID.Win32NT:
                    return Path.Combine(basePath, "simple.dll");
                case PlatformID.Unix:
                    return Path.Combine(basePath, "libsimple");
                default:
                    return Path.Combine(basePath, "simple.dll");
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

        private static bool PatchCreateConnection()
        {
            try
            {
                if (isConnectionPatched || harmony == null || createConnection == null)
                {
                    return isConnectionPatched;
                }

                harmony.Patch(
                    createConnection,
                    postfix: new HarmonyMethod(typeof(ChineseSearch), nameof(CreateConnectionPostfix)));
                isConnectionPatched = true;
                return isConnectionPatched;
            }
            catch (Exception e)
            {
                logger?.Error("ChineseSearch patch CreateConnection failed: " + createConnection?.Name);
                logger?.Error(e.ToString());
                return false;
            }
        }

        private static void PatchPhase2(IDatabaseConnection connection)
        {
            const string ftsTableName = "fts_search9";
            var rebuildFtsResult = true;
            var patchSearchFunctionsResult = false;
            var shouldLogLoadSuccess = false;
            var simpleTokenizerLoaded = LoadTokenizerExtension(connection, false);

            try
            {
                CurrentTokenizerName = DetectCurrentTokenizer(connection, ftsTableName);
                logger?.Info($"增强搜索 - 当前分词器（处理前）：{CurrentTokenizerName}");
                var shouldEnhance = enhanceChineseSearchEnabled;
                var shouldRestore = enhanceChineseSearchRestoreEnabled;
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
                        logger?.Warn("增强搜索 - simple 分词器未加载成功，增强模式将尝试回退。");
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
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - 加载失败。");
                logger?.Warn("增强搜索 - 失败。");
                logger?.Warn(e.ToString());
            }

            var isEnhanceMode = enhanceChineseSearchEnabled;
            var hasUnknownTokenizer = string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal);
            var shouldResetOptions = (isEnhanceMode && !patchSearchFunctionsResult) || !rebuildFtsResult || hasUnknownTokenizer;
            if (shouldResetOptions)
            {
                logger?.Warn("增强搜索 - 加载失败。");
                ResetOptions();
            }
            else if (shouldLogLoadSuccess)
            {
                logger?.Info("增强搜索 - 加载成功。");
            }

            logger?.Info($"增强搜索 - 当前分词器（处理后）：{CurrentTokenizerName}");
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

        public static bool RebuildSearchIndex()
        {
            if (!isInitialized || createConnection == null)
            {
                logger?.Warn("增强搜索 - 跳过重建搜索索引：搜索模块未初始化。");
                return false;
            }

            var repository = Plugin.Instance?.ItemRepository;
            if (repository == null)
            {
                logger?.Warn("增强搜索 - 跳过重建搜索索引：条目仓库不可用。");
                return false;
            }

            IDatabaseConnection connection = null;
            try
            {
                connection = OpenLibraryConnection(repository);
                if (connection == null)
                {
                    logger?.Warn("增强搜索 - 跳过重建搜索索引：数据库连接不可用。");
                    return false;
                }

                const string ftsTableName = "fts_search9";
                CurrentTokenizerName = DetectCurrentTokenizer(connection, ftsTableName);
                logger?.Info($"增强搜索 - 重建前当前分词器：{CurrentTokenizerName}");

                var targetTokenizer = "unicode61 remove_diacritics 2";
                if (enhanceChineseSearchEnabled && LoadTokenizerExtension(connection, false))
                {
                    targetTokenizer = "simple";
                }

                var rebuildResult = RebuildFts(connection, ftsTableName, targetTokenizer);
                if (!rebuildResult)
                {
                    logger?.Warn("增强搜索 - 重建搜索索引失败。");
                    return false;
                }

                CurrentTokenizerName = targetTokenizer;
                logger?.Info("增强搜索 - 重建搜索索引成功。");
                logger?.Info($"增强搜索 - 重建后当前分词器：{CurrentTokenizerName}");
                return true;
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - 重建搜索索引失败。");
                logger?.Warn(e.ToString());
                return false;
            }
            finally
            {
                (connection as IDisposable)?.Dispose();
            }
        }

        private static IDatabaseConnection OpenLibraryConnection(object repository)
        {
            return createConnection?.Invoke(repository, new object[] { false, CancellationToken.None }) as IDatabaseConnection;
        }

        private static bool RebuildFts(IDatabaseConnection connection, string ftsTableName, string tokenizerName)
        {
            var populateQuery =
                $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                GetSearchColumnNormalization("Name") + ", " +
                GetSearchColumnNormalization("OriginalTitle") + ", " +
                GetSearchColumnNormalization("SeriesName") + ", " +
                GetSearchColumnNormalization(
                    "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                " from MediaItems";

            connection.BeginTransaction(TransactionMode.Deferred);
            try
            {
                connection.Execute($"DROP TABLE IF EXISTS {ftsTableName}");

                var createFtsTableQuery =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\", prefix='1 2 3 4')";
                connection.Execute(createFtsTableQuery);

                logger?.Info($"增强搜索 - 开始填充 {ftsTableName}");

                connection.Execute(populateQuery);
                connection.CommitTransaction();

                logger?.Info($"增强搜索 - 填充 {ftsTableName} 完成");

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
            return "replace(replace(replace(replace(" + columnName + ",'''',''),'.',''),'·',''),'-','')";
        }

        private static string NormalizeSearchTerm(string searchTerm)
        {
            return searchTerm?
                .Replace(".", string.Empty)
                .Replace("'", string.Empty)
                .Replace("·", string.Empty)
                .Replace("-", string.Empty);
        }

        private static bool EnsureTokenizerExists()
        {
            var resourceName = GetTokenizerResourceName();
            var expectedSha1 = GetExpectedSha1();

            if (string.IsNullOrWhiteSpace(tokenizerPath) ||
                string.IsNullOrWhiteSpace(resourceName) ||
                string.IsNullOrWhiteSpace(expectedSha1))
            {
                return false;
            }

            try
            {
                if (File.Exists(tokenizerPath))
                {
                    var existingSha1 = ComputeSha1(tokenizerPath);
                    if (string.Equals(existingSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
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
            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Resources.Tokenizer";
            var architecture = RuntimeInformation.OSArchitecture;
            string resourceName;

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    switch (architecture)
                    {
                        case Architecture.X64:
                            resourceName = $"{tokenizerNamespace}.win.x64.simple.dll";
                            break;
                        case Architecture.Arm64:
                            resourceName = $"{tokenizerNamespace}.win.arm64.simple.dll";
                            break;
                        default:
                            resourceName = null;
                            break;
                    }
                    break;
                case PlatformID.Unix:
                    switch (architecture)
                    {
                        case Architecture.X64:
                            resourceName = $"{tokenizerNamespace}.linux.x64.libsimple.so";
                            break;
                        case Architecture.Arm64:
                            resourceName = $"{tokenizerNamespace}.linux.arm64.libsimple.so";
                            break;
                        default:
                            resourceName = null;
                            break;
                    }
                    break;
                default:
                    resourceName = null;
                    break;
            }
            return resourceName;
        }

        private static string GetExpectedSha1()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (RuntimeInformation.OSArchitecture == Architecture.X64)
                {
                    return "338bb0915d6f4625b54f041bdeb6791b6e590c4e";
                }

                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                {
                    return "338bb0915d6f4625b54f041bdeb6791b6e590c4e";
                }

                return null;
            }

            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                if (RuntimeInformation.OSArchitecture == Architecture.X64)
                {
                    return "a6188af48c0fef201cb24dbebc65c4cf5b4ddf9b";
                }

                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                {
                    return "16d3b24f2a5d2f89c6ae56d412613f8379d1ad8a";
                }

                return null;
            }

            return null;
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
        private static void CreateConnectionPostfix(
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
                if (enhanceChineseSearchEnabled &&
                    string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                {
                    var replacement = excludeOriginalTitleFromSearch
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
                enhanceChineseSearchEnabled &&
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
                                .Trim('\"', '^', '$');
                        }

                        currentValue = NormalizeSearchTerm(currentValue);
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

            __result = NormalizeSearchTerm(searchTerm);
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
                            query.AnyProviderIdEquals = BuildProviderIdEquals(provider.Key, match);
                            query.SearchTerm = null;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(query.SearchTerm))
                {
                    LoadTokenizerExtension(db, false);
                }
            }

            return true;
        }

        private static List<KeyValuePair<string, string>> BuildProviderIdEquals(string providerKey, Match match)
        {
            switch (providerKey)
            {
                case "imdb":
                case "imdb_name":
                    return new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("imdb", match.Value)
                    };
                case "tmdb":
                case "tvdb":
                    return new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>(providerKey, match.Groups[2].Value)
                    };
                case "douban":
                    return new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("douban", match.Groups[2].Value),
                        new KeyValuePair<string, string>("doubanid", match.Groups[2].Value)
                    };
                case "bangumi":
                    return new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("bgm", match.Groups[3].Value),
                        new KeyValuePair<string, string>("bgmid", match.Groups[3].Value),
                        new KeyValuePair<string, string>("bangumi", match.Groups[3].Value)
                    };
                default:
                    return new List<KeyValuePair<string, string>>();
            }
        }
    }
}
