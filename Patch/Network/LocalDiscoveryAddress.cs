using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.System;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 重写 HTTP 与 UDP 局域网发现响应中的服务器地址，支持自定义或阻断。
    /// </summary>
    public static class LocalDiscoveryAddress
    {
        private const string BlockedKeyword = "BLOCKED";
        private static readonly BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly AsyncLocal<string> CurrentUdpDiscoveryAddress = new AsyncLocal<string>();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isPatched;
        private static string configuredValue = string.Empty;

        private static MethodInfo getPublicSystemInfoWithToken;
        private static MethodInfo getPublicSystemInfoWithAddress;
        private static MethodInfo getSystemInfoWithToken;
        private static MethodInfo getSystemInfoWithAddress;
        private static MethodInfo respondToMessageMethod;
        private static MethodInfo sendMessageMethod;

        public static bool IsHttpReady =>
            isPatched &&
            getPublicSystemInfoWithToken != null &&
            getPublicSystemInfoWithAddress != null &&
            getSystemInfoWithToken != null &&
            getSystemInfoWithAddress != null;

        public static bool IsUdpBlockReady => isPatched && respondToMessageMethod != null;

        public static bool IsUdpRewriteReady => isPatched && respondToMessageMethod != null && sendMessageMethod != null;

        public static bool IsReady => IsHttpReady && IsUdpRewriteReady;

        public static bool IsConfiguredBehaviorReady
        {
            get
            {
                if (IsBlockedConfigured())
                {
                    return IsUdpBlockReady;
                }

                return !HasConfiguredValue() || (IsHttpReady && IsUdpRewriteReady);
            }
        }

        public static void Initialize(ILogger pluginLogger, string customAddress)
        {
            if (harmony != null)
            {
                Configure(customAddress);
                return;
            }

            logger = pluginLogger;
            configuredValue = NormalizeConfiguredValue(customAddress);

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var mediaBrowserControllerAssembly = Assembly.Load("MediaBrowser.Controller");

                var applicationHostType =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.ApplicationHost", false);
                if (applicationHostType == null)
                {
                    PatchLog.InitFailed(logger, nameof(LocalDiscoveryAddress), "ApplicationHost 未找到");
                    return;
                }

                var authorizationInfoType =
                    mediaBrowserControllerAssembly.GetType("MediaBrowser.Controller.Net.AuthorizationInfo", false);
                if (authorizationInfoType == null)
                {
                    PatchLog.InitFailed(logger, nameof(LocalDiscoveryAddress), "AuthorizationInfo 未找到");
                    return;
                }

                var implVersion = embyServerImplementationsAssembly.GetName().Version;
                getPublicSystemInfoWithToken = PatchMethodResolver.Resolve(
                    applicationHostType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "applicationhost-getpublicsysteminfo-token",
                        MethodName = "GetPublicSystemInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(CancellationToken) },
                        ReturnType = typeof(Task<PublicSystemInfo>)
                    },
                    logger,
                    "LocalDiscoveryAddress.GetPublicSystemInfo(CancellationToken)");
                getPublicSystemInfoWithAddress = PatchMethodResolver.Resolve(
                    applicationHostType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "applicationhost-getpublicsysteminfo-address-auth-token",
                        MethodName = "GetPublicSystemInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(IPAddress), authorizationInfoType, typeof(CancellationToken) },
                        ReturnType = typeof(Task<PublicSystemInfo>)
                    },
                    logger,
                    "LocalDiscoveryAddress.GetPublicSystemInfo(IPAddress,AuthorizationInfo,CancellationToken)");
                getSystemInfoWithToken = PatchMethodResolver.Resolve(
                    applicationHostType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "applicationhost-getsysteminfo-address-token",
                        MethodName = "GetSystemInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(IPAddress), typeof(CancellationToken) },
                        ReturnType = typeof(Task<SystemInfo>)
                    },
                    logger,
                    "LocalDiscoveryAddress.GetSystemInfo(IPAddress,CancellationToken)");
                getSystemInfoWithAddress = PatchMethodResolver.Resolve(
                    applicationHostType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "applicationhost-getsysteminfo-address-auth-token",
                        MethodName = "GetSystemInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(IPAddress), authorizationInfoType, typeof(CancellationToken) },
                        ReturnType = typeof(Task<SystemInfo>)
                    },
                    logger,
                    "LocalDiscoveryAddress.GetSystemInfo(IPAddress,AuthorizationInfo,CancellationToken)");

                var udpServerType =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Udp.UdpServer", false);
                if (udpServerType == null)
                {
                    PatchLog.InitFailed(logger, nameof(LocalDiscoveryAddress), "UdpServer 未找到");
                    return;
                }

                respondToMessageMethod = PatchMethodResolver.Resolve(
                    udpServerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "udpserver-respondtomessage-endpoint-encoding",
                        MethodName = "RespondToMessage",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(IPEndPoint), typeof(Encoding) },
                        ReturnType = typeof(Task)
                    },
                    logger,
                    "LocalDiscoveryAddress.RespondToMessage");
                sendMessageMethod = PatchMethodResolver.Resolve(
                    udpServerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "udpserver-sendmessage-string-endpoint-encoding",
                        MethodName = "SendMessage",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(string), typeof(IPEndPoint), typeof(Encoding) },
                        ReturnType = typeof(void)
                    },
                    logger,
                    "LocalDiscoveryAddress.SendMessage");

                harmony = new Harmony("mediainfokeeper.localdiscovery");
                Patch();
            }
            catch (Exception ex)
            {
                logger?.Error("本地发现地址补丁初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(string customAddress)
        {
            configuredValue = NormalizeConfiguredValue(customAddress);
        }

        public static string NormalizeConfiguredValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.Equals(normalized, BlockedKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return BlockedKeyword;
            }

            return normalized;
        }

        public static bool TryValidateConfiguredValue(string value, out string normalizedValue, out string error)
        {
            normalizedValue = NormalizeConfiguredValue(value);
            error = null;

            if (string.IsNullOrWhiteSpace(normalizedValue) || string.Equals(normalizedValue, BlockedKeyword, StringComparison.Ordinal))
            {
                return true;
            }

            if (!Uri.TryCreate(normalizedValue, UriKind.Absolute, out var uri))
            {
                error = "自定义本地发现地址必须是完整的 http(s)://host:port URL 或 BLOCKED。";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "自定义本地发现地址仅支持 http 或 https 协议。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "自定义本地发现地址必须包含主机名或 IP。";
                return false;
            }

            return true;
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            PatchIfAvailable(getPublicSystemInfoWithToken, nameof(GetPublicSystemInfoPostfix));
            PatchIfAvailable(getPublicSystemInfoWithAddress, nameof(GetPublicSystemInfoPostfix));
            PatchIfAvailable(getSystemInfoWithToken, nameof(GetSystemInfoPostfix));
            PatchIfAvailable(getSystemInfoWithAddress, nameof(GetSystemInfoPostfix));

            if (respondToMessageMethod != null)
            {
                harmony.Patch(
                    respondToMessageMethod,
                    prefix: new HarmonyMethod(typeof(LocalDiscoveryAddress), nameof(RespondToMessagePrefix)),
                    postfix: new HarmonyMethod(typeof(LocalDiscoveryAddress), nameof(RespondToMessagePostfix)));
                PatchLog.Patched(logger, nameof(LocalDiscoveryAddress), respondToMessageMethod);
            }

            if (sendMessageMethod != null)
            {
                harmony.Patch(
                    sendMessageMethod,
                    prefix: new HarmonyMethod(typeof(LocalDiscoveryAddress), nameof(SendMessagePrefix)));
                PatchLog.Patched(logger, nameof(LocalDiscoveryAddress), sendMessageMethod);
            }

            isPatched = true;
        }

        private static void PatchIfAvailable(MethodInfo method, string postfixName)
        {
            if (method == null)
            {
                return;
            }

            harmony.Patch(method, postfix: new HarmonyMethod(typeof(LocalDiscoveryAddress), postfixName));
            PatchLog.Patched(logger, nameof(LocalDiscoveryAddress), method);
        }

        [HarmonyPostfix]
        private static void GetPublicSystemInfoPostfix(ref Task<PublicSystemInfo> __result)
        {
            if (__result == null || !TryGetCustomDiscoveryAddress(out var address))
            {
                return;
            }

            var originalTask = __result;
            __result = originalTask.ContinueWith(task =>
                {
                    var info = task.GetAwaiter().GetResult();
                    ApplyDiscoveryAddress(info, address, "PublicSystemInfo");
                    return info;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPostfix]
        private static void GetSystemInfoPostfix(ref Task<SystemInfo> __result)
        {
            if (__result == null || !TryGetCustomDiscoveryAddress(out var address))
            {
                return;
            }

            var originalTask = __result;
            __result = originalTask.ContinueWith(task =>
                {
                    var info = task.GetAwaiter().GetResult();
                    ApplyDiscoveryAddress(info, address, "SystemInfo");
                    return info;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPrefix]
        private static bool RespondToMessagePrefix(ref Task __result, IPEndPoint remoteEndPoint)
        {
            CurrentUdpDiscoveryAddress.Value = null;

            if (!HasConfiguredValue())
            {
                return true;
            }

            if (IsBlockedConfigured())
            {
                logger?.Info(
                    "本地发现地址已设置为 BLOCKED，跳过 UDP 应答: {0}",
                    remoteEndPoint?.ToString() ?? "<unknown>");
                __result = Task.CompletedTask;
                return false;
            }

            if (TryGetCustomDiscoveryAddress(out var address))
            {
                CurrentUdpDiscoveryAddress.Value = address;
                logger?.Debug(
                    "本地发现地址 UDP 应答将改写为: {0} ({1})",
                    address,
                    remoteEndPoint?.ToString() ?? "<unknown>");
            }

            return true;
        }

        [HarmonyPostfix]
        private static void RespondToMessagePostfix(Task __result)
        {
            if (string.IsNullOrWhiteSpace(CurrentUdpDiscoveryAddress.Value))
            {
                return;
            }

            if (__result == null)
            {
                CurrentUdpDiscoveryAddress.Value = null;
                return;
            }

            __result.ContinueWith(
                _ => { CurrentUdpDiscoveryAddress.Value = null; },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPrefix]
        private static void SendMessagePrefix(ref string __0)
        {
            var configuredAddress = CurrentUdpDiscoveryAddress.Value;
            if (string.IsNullOrWhiteSpace(configuredAddress) || string.IsNullOrWhiteSpace(__0))
            {
                return;
            }

            if (!TryRewriteDiscoveryMessage(__0, configuredAddress, out var rewrittenMessage))
            {
                return;
            }

            if (!string.Equals(__0, rewrittenMessage, StringComparison.Ordinal))
            {
                logger?.Debug("UDP 发现响应已改写为自定义地址: {0}", configuredAddress);
                __0 = rewrittenMessage;
            }
        }

        private static void ApplyDiscoveryAddress(object info, string address, string source)
        {
            if (info == null || string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            var localAddressUpdated = TrySetMemberValue(info, "LocalAddress", address);
            var localAddressesUpdated = TrySetMemberValue(info, "LocalAddresses", new[] { address });

            logger?.Debug(
                "本地发现地址已应用到 {0}: address={1}, LocalAddressUpdated={2}, LocalAddressesUpdated={3}",
                source,
                address,
                localAddressUpdated,
                localAddressesUpdated);
        }

        private static bool TryRewriteDiscoveryMessage(string message, string address, out string rewrittenMessage)
        {
            rewrittenMessage = message;

            try
            {
                var root = JsonNode.Parse(message) as JsonObject;
                if (root == null)
                {
                    return false;
                }

                root["Address"] = address;
                rewrittenMessage = root.ToJsonString(new JsonSerializerOptions());
                return true;
            }
            catch (JsonException ex)
            {
                logger?.Debug("UDP 发现响应不是有效 JSON，跳过地址改写: {0}", ex.Message);
                return false;
            }
        }

        private static bool TrySetMemberValue(object instance, string memberName, object value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(memberName))
            {
                return false;
            }

            var type = instance.GetType();
            var property = type.GetProperty(memberName, InstanceFlags);
            if (property != null)
            {
                var setter = property.GetSetMethod(true);
                if (setter != null && property.PropertyType.IsInstanceOfType(value))
                {
                    setter.Invoke(instance, new[] { value });
                    return true;
                }
            }

            var backingField = type.GetField($"<{memberName}>k__BackingField", InstanceFlags);
            if (backingField != null && backingField.FieldType.IsInstanceOfType(value))
            {
                backingField.SetValue(instance, value);
                return true;
            }

            var directField = type.GetField(memberName, InstanceFlags);
            if (directField != null && directField.FieldType.IsInstanceOfType(value))
            {
                directField.SetValue(instance, value);
                return true;
            }

            return false;
        }

        private static bool TryGetCustomDiscoveryAddress(out string address)
        {
            address = null;
            if (!TryValidateConfiguredValue(configuredValue, out var normalizedValue, out _) ||
                string.IsNullOrWhiteSpace(normalizedValue) ||
                string.Equals(normalizedValue, BlockedKeyword, StringComparison.Ordinal))
            {
                return false;
            }

            address = normalizedValue;
            return true;
        }

        private static bool HasConfiguredValue()
        {
            return !string.IsNullOrWhiteSpace(configuredValue);
        }

        private static bool IsBlockedConfigured()
        {
            return string.Equals(configuredValue, BlockedKeyword, StringComparison.Ordinal);
        }
    }
}
