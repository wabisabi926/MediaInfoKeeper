using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public static class ProxyServer
    {
        private static readonly string[] BypassAddressList =
        {
            @"^10\.\d{1,3}\.\d{1,3}\.\d{1,3}$",
            @"^172\.(1[6-9]|2[0-9]|3[0-1])\.\d{1,3}\.\d{1,3}$",
            @"^192\.168\.\d{1,3}\.\d{1,3}$"
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo createHttpClientHandler;
        private static MethodInfo movieDbGetMovieDbResponse;
        private static bool isEnabled;
        private static bool isPatched;
        private static bool isMovieDbPatched;
        private static bool waitingForMovieDbAssembly;

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var applicationHost =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.ApplicationHost");
                createHttpClientHandler = applicationHost?.GetMethod("CreateHttpClientHandler",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (createHttpClientHandler == null)
                {
                    logger?.Warn("代理服务器初始化失败：CreateHttpClientHandler 未找到。");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.proxy");
                Patch();
                TryInstallMovieDbPatch();
            }
            catch (Exception e)
            {
                logger?.Error("代理服务器初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;

            if (harmony == null)
            {
                return;
            }

            ApplyProxyEnvironmentVariables();
            Patch();

            logger?.Info("代理服务器 " + (isEnabled ? "已启用" : "已禁用"));
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(createHttpClientHandler,
                postfix: new HarmonyMethod(typeof(ProxyServer), nameof(CreateHttpClientHandlerPostfix)));
            isPatched = true;
        }

        private static void TryInstallMovieDbPatch()
        {
            if (isMovieDbPatched || harmony == null)
            {
                return;
            }

            if (TryGetLoadedMovieDbAssembly(out var movieDbAssembly))
            {
                InstallMovieDbPatch(movieDbAssembly);
                return;
            }

            if (!waitingForMovieDbAssembly)
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                waitingForMovieDbAssembly = true;
            }
        }

        private static bool TryGetLoadedMovieDbAssembly(out Assembly assembly)
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase));
            return assembly != null;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var loadedAssembly = args?.LoadedAssembly;
            if (loadedAssembly == null ||
                !string.Equals(loadedAssembly.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            InstallMovieDbPatch(loadedAssembly);
        }

        private static void InstallMovieDbPatch(Assembly movieDbAssembly)
        {
            if (isMovieDbPatched || harmony == null || movieDbAssembly == null)
            {
                return;
            }

            var movieDbProviderBase = movieDbAssembly.GetType("MovieDb.MovieDbProviderBase", false);
            movieDbGetMovieDbResponse = movieDbProviderBase?.GetMethod(
                "GetMovieDbResponse",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (movieDbGetMovieDbResponse == null)
            {
                return;
            }

            harmony.Patch(
                movieDbGetMovieDbResponse,
                prefix: new HarmonyMethod(typeof(ProxyServer), nameof(GetMovieDbResponsePrefix)));
            isMovieDbPatched = true;

            if (waitingForMovieDbAssembly)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                waitingForMovieDbAssembly = false;
            }
        }

        [HarmonyPostfix]
        private static void CreateHttpClientHandlerPostfix(ref HttpMessageHandler __result)
        {
            var options = Plugin.Instance.Options.Proxy;
            if (options == null)
            {
                return;
            }

            var primaryHandler = __result;
            ApplyAutomaticDecompression(primaryHandler, options.EnableGzip);

            if (!isEnabled)
            {
                return;
            }

            if (!TryParseProxyUrl(options.ProxyServerUrl, out var proxyUri, out var credentials))
            {
                return;
            }

            var proxy = new WebProxy(proxyUri)
            {
                BypassProxyOnLocal = true,
                BypassList = BypassAddressList,
                Credentials = credentials
            };

            if (primaryHandler is HttpClientHandler httpClientHandler)
            {
                httpClientHandler.Proxy = proxy;
                httpClientHandler.UseProxy = true;
                if (options.IgnoreCertificateValidation)
                {
                    httpClientHandler.ServerCertificateCustomValidationCallback =
                        (httpRequestMessage, cert, chain, sslErrors) => true;
                }
            }
            else if (primaryHandler is SocketsHttpHandler socketsHttpHandler)
            {
                socketsHttpHandler.Proxy = proxy;
                socketsHttpHandler.UseProxy = true;
                if (options.IgnoreCertificateValidation)
                {
                    socketsHttpHandler.SslOptions.RemoteCertificateValidationCallback =
                        (sender, cert, chain, sslErrors) => true;
                }
            }

        }

        [HarmonyPrefix]
        private static void GetMovieDbResponsePrefix(object[] __args)
        {
            if (__args == null || __args.Length == 0 || __args[0] == null)
            {
                return;
            }

            var options = Plugin.Instance.Options.Proxy;
            if (options == null || !options.EnableAlternativeTmdb)
            {
                return;
            }

            var requestOptions = __args[0];
            var urlProperty = requestOptions.GetType().GetProperty("Url", BindingFlags.Instance | BindingFlags.Public);
            if (urlProperty == null || !urlProperty.CanRead || !urlProperty.CanWrite)
            {
                return;
            }

            var originalUrl = urlProperty.GetValue(requestOptions) as string;
            if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri))
            {
                return;
            }

            var rewritten = RewriteTmdbUri(uri, options);
            if (!ReferenceEquals(rewritten, uri) && rewritten != uri)
            {
                urlProperty.SetValue(requestOptions, rewritten.ToString());
            }
        }

        private static void ApplyAutomaticDecompression(HttpMessageHandler handler, bool enableGzip)
        {
            var methods = enableGzip
                ? DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                : DecompressionMethods.None;

            if (handler is HttpClientHandler httpClientHandler)
            {
                httpClientHandler.AutomaticDecompression = methods;
            }
            else if (handler is SocketsHttpHandler socketsHttpHandler)
            {
                socketsHttpHandler.AutomaticDecompression = methods;
            }
        }

        private static bool TryParseProxyUrl(string raw, out Uri proxyUri, out NetworkCredential credentials)
        {
            proxyUri = null;
            credentials = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            {
                logger?.Warn("代理服务器地址无效: {0}", raw);
                return false;
            }

            proxyUri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(new[] { ':' }, 2);
                if (!string.IsNullOrWhiteSpace(parts[0]))
                {
                    credentials = new NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
                }
            }

            return true;
        }

        private static void ApplyProxyEnvironmentVariables()
        {
            var options = Plugin.Instance.Options.Proxy;
            var proxyUrl = options?.ProxyServerUrl?.Trim() ?? string.Empty;
            var writeEnv = options?.WriteProxyEnvVars == true;

            if (isEnabled && writeEnv && !string.IsNullOrEmpty(proxyUrl))
            {
                Environment.SetEnvironmentVariable("http_proxy", proxyUrl);
                Environment.SetEnvironmentVariable("https_proxy", proxyUrl);
                Environment.SetEnvironmentVariable("HTTP_PROXY", proxyUrl);
                Environment.SetEnvironmentVariable("HTTPS_PROXY", proxyUrl);
                logger.Info($"设置代理环境变量 {proxyUrl}");
            }
        }

        private static Uri RewriteTmdbUri(Uri uri, Configuration.ProxyOptions options)
        {
            var replaced = uri;

            if (string.Equals(uri.Host, "api.themoviedb.org", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseDomainEndpoint(options.AlternativeTmdbApiUrl, out var altApiHost, out var altApiPort))
                {
                    replaced = ReplaceAuthority(replaced, altApiHost, altApiPort);
                }

                if (!string.IsNullOrWhiteSpace(options.AlternativeTmdbApiKey))
                {
                    replaced = ReplaceApiKey(replaced, options.AlternativeTmdbApiKey.Trim());
                }
            }
            else if (string.Equals(uri.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseDomainEndpoint(options.AlternativeTmdbImageUrl, out var altImageHost, out var altImagePort))
                {
                    replaced = ReplaceAuthority(replaced, altImageHost, altImagePort);
                }
            }

            return replaced;
        }

        private static bool TryParseDomainEndpoint(string raw, out string host, out int port)
        {
            host = null;
            port = -1;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var value = raw.Trim();

            if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
            {
                host = absoluteUri.Host;
                port = absoluteUri.IsDefaultPort ? -1 : absoluteUri.Port;
                return !string.IsNullOrWhiteSpace(host);
            }

            if (Uri.TryCreate("https://" + value, UriKind.Absolute, out var domainUri))
            {
                host = domainUri.Host;
                port = domainUri.IsDefaultPort ? -1 : domainUri.Port;
                return !string.IsNullOrWhiteSpace(host);
            }

            return false;
        }

        private static Uri ReplaceAuthority(Uri source, string host, int port)
        {
            var builder = new UriBuilder(source)
            {
                Host = host,
                Port = port
            };

            return builder.Uri;
        }

        private static Uri ReplaceApiKey(Uri source, string apiKey)
        {
            var builder = new UriBuilder(source);
            var query = builder.Query;
            if (query.StartsWith("?", StringComparison.Ordinal))
            {
                query = query.Substring(1);
            }

            var pairs = string.IsNullOrEmpty(query)
                ? Array.Empty<string>()
                : query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            var rewritten = false;
            for (var i = 0; i < pairs.Length; i++)
            {
                var segment = pairs[i];
                var index = segment.IndexOf('=');
                var key = index >= 0 ? segment.Substring(0, index) : segment;
                if (!string.Equals(key, "api_key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pairs[i] = "api_key=" + Uri.EscapeDataString(apiKey);
                rewritten = true;
            }

            if (!rewritten)
            {
                var expanded = new string[pairs.Length + 1];
                for (var i = 0; i < pairs.Length; i++)
                {
                    expanded[i] = pairs[i];
                }

                expanded[pairs.Length] = "api_key=" + Uri.EscapeDataString(apiKey);
                pairs = expanded;
            }

            builder.Query = string.Join("&", pairs);
            return builder.Uri;
        }
    }
}
