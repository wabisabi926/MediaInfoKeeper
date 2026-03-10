using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// Enhance NFO person parsing by importing thumb URL into PersonInfo.ImageUrl.
    /// </summary>
    public static class NfoMetadataEnhance
    {
        private static readonly object InitLock = new object();
        private static readonly AsyncLocal<string> PersonContent = new AsyncLocal<string>();

        private static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            ValidationType = ValidationType.None,
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true
        };

        private static readonly XmlWriterSettings WriterSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            CheckCharacters = false
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isHookInstalled;
        private static bool waitingForAssembly;
        private static Assembly nfoMetadataAssembly;
        private static MethodBase parserConstructor;
        private static MethodInfo getPersonFromXmlNode;

        public static bool IsReady => harmony != null && (waitingForAssembly || isHookInstalled);

        public static bool IsWaiting => waitingForAssembly && !isHookInstalled;

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock)
            {
                harmony ??= new Harmony("mediainfokeeper.nfometadataenhance");

                if (isHookInstalled)
                {
                    return;
                }

                if (TryGetLoadedAssembly("NfoMetadata", out var assembly))
                {
                    TryInstallHooks(assembly);
                    return;
                }

                if (!waitingForAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    waitingForAssembly = true;
                    PatchLog.Waiting(logger, nameof(NfoMetadataEnhance), "NfoMetadata", isEnabled);
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var assembly = args?.LoadedAssembly;
            if (assembly == null)
            {
                return;
            }

            if (!string.Equals(assembly.GetName().Name, "NfoMetadata", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (InitLock)
            {
                if (isHookInstalled)
                {
                    return;
                }

                TryInstallHooks(assembly);
                if (isHookInstalled)
                {
                    waitingForAssembly = false;
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                }
            }
        }

        private static bool TryGetLoadedAssembly(string assemblyName, out Assembly assembly)
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            return assembly != null;
        }

        private static void TryInstallHooks(Assembly assembly)
        {
            try
            {
                nfoMetadataAssembly = assembly;
                var version = assembly.GetName().Version;
                var parserGeneric = assembly.GetType("NfoMetadata.Parsers.BaseNfoParser`1", false);
                if (parserGeneric == null)
                {
                    PatchLog.InitFailed(logger, nameof(NfoMetadataEnhance), "BaseNfoParser`1 未找到");
                    return;
                }

                var parserVideoType = parserGeneric.MakeGenericType(typeof(Video));
                parserConstructor = parserVideoType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[]
                    {
                        typeof(ILogger),
                        typeof(IConfigurationManager),
                        typeof(IProviderManager),
                        typeof(IFileSystem)
                    },
                    null);

                getPersonFromXmlNode = PatchMethodResolver.Resolve(
                    parserVideoType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "base-nfo-parser-getpersonfromxmlnode",
                        MethodName = "GetPersonFromXmlNode",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(XmlReader) },
                        ReturnType = typeof(Task<PersonInfo>)
                    },
                    logger,
                    "NfoMetadataEnhance.BaseNfoParser.GetPersonFromXmlNode");

                if (parserConstructor == null || getPersonFromXmlNode == null)
                {
                    PatchLog.InitFailed(logger, nameof(NfoMetadataEnhance), "目标方法缺失");
                    return;
                }

                PatchLog.Patched(
                    logger,
                    nameof(NfoMetadataEnhance),
                    $"{parserVideoType.FullName}..ctor(ILogger,IConfigurationManager,IProviderManager,IFileSystem)",
                    version?.ToString());
                PatchLog.Patched(logger, nameof(NfoMetadataEnhance), getPersonFromXmlNode);

                harmony.Patch(
                    parserConstructor,
                    prefix: new HarmonyMethod(typeof(NfoMetadataEnhance), nameof(ParserConstructorPrefix)));
                harmony.Patch(
                    getPersonFromXmlNode,
                    prefix: new HarmonyMethod(typeof(NfoMetadataEnhance), nameof(GetPersonFromXmlNodePrefix)),
                    postfix: new HarmonyMethod(typeof(NfoMetadataEnhance), nameof(GetPersonFromXmlNodePostfix)));

                isHookInstalled = true;
                waitingForAssembly = false;
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(NfoMetadataEnhance), ex.Message);
                logger?.Error("NfoMetadataEnhance 初始化失败。");
                logger?.Error(ex.ToString());
            }
        }

        [HarmonyPrefix]
        private static bool ParserConstructorPrefix()
        {
            if (harmony == null || getPersonFromXmlNode == null)
            {
                return true;
            }

            harmony.Unpatch(getPersonFromXmlNode, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(getPersonFromXmlNode, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Patch(
                getPersonFromXmlNode,
                prefix: new HarmonyMethod(typeof(NfoMetadataEnhance), nameof(GetPersonFromXmlNodePrefix)),
                postfix: new HarmonyMethod(typeof(NfoMetadataEnhance), nameof(GetPersonFromXmlNodePostfix)));
            return true;
        }

        [HarmonyPrefix]
        private static bool GetPersonFromXmlNodePrefix(ref XmlReader reader)
        {
            if (!isEnabled)
            {
                PersonContent.Value = null;
                return true;
            }

            try
            {
                var content = ReadCurrentNodeContent(reader);
                PersonContent.Value = content;
                if (content != null)
                {
                    reader = XmlReader.Create(new StringReader(content), ReaderSettings);
                }
            }
            catch (Exception ex)
            {
                PersonContent.Value = null;
                logger?.Debug("NfoMetadataEnhance 读取人物节点失败: {0}", ex.Message);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void GetPersonFromXmlNodePostfix(Task<PersonInfo> __result)
        {
            if (!isEnabled || __result == null)
            {
                PersonContent.Value = null;
                return;
            }

            var personContent = PersonContent.Value;
            PersonContent.Value = null;

            if (string.IsNullOrWhiteSpace(personContent))
            {
                return;
            }

            _ = Task.Run(async () => await SetImageUrlAsync(__result, personContent).ConfigureAwait(false));
        }

        private static string ReadCurrentNodeContent(XmlReader reader)
        {
            if (reader == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
            using (var xmlWriter = XmlWriter.Create(writer, WriterSettings))
            {
                while (reader.Read())
                {
                    xmlWriter.WriteNode(reader, true);
                    if (reader.NodeType == XmlNodeType.EndElement)
                    {
                        break;
                    }
                }
            }

            return sb.ToString();
        }

        private static async Task SetImageUrlAsync(Task<PersonInfo> personInfoTask, string personContent)
        {
            try
            {
                var personInfo = await personInfoTask.ConfigureAwait(false);
                if (personInfo == null || string.IsNullOrWhiteSpace(personContent))
                {
                    return;
                }

                using (var reader = XmlReader.Create(new StringReader(personContent), ReaderSettings))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        if (!reader.IsStartElement("thumb"))
                        {
                            continue;
                        }

                        var thumb = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        if (IsValidHttpUrl(thumb))
                        {
                            personInfo.ImageUrl = thumb;
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("NfoMetadataEnhance 设置人物图片失败: {0}", ex.Message);
            }
        }

        private static bool IsValidHttpUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
