using System.Reflection;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    internal static class PatchLog
    {
        public static void Waiting(ILogger logger, string module, string dependency, bool enabled)
        {
            logger?.Info(
                "补丁等待：模块={0}，依赖={1}，启用={2}",
                module ?? "unknown",
                dependency ?? "unknown",
                enabled);
        }

        public static void InitFailed(ILogger logger, string module, string reason)
        {
            logger?.Warn(
                "补丁初始化失败：模块={0}，原因={1}",
                module ?? "unknown",
                string.IsNullOrWhiteSpace(reason) ? "unknown" : reason);
        }

        public static void ResolveHit(
            ILogger logger,
            string context,
            string level,
            string profile,
            string method,
            string dllVersion)
        {
            logger?.Debug(
                "补丁解析：模块={0}，级别={1}，配置={2}，dll版本={3}，命中={4}",
                context ?? "unknown",
                level ?? "unknown",
                profile ?? "unknown",
                dllVersion ?? "<unknown>",
                method ?? "unknown");
        }

        public static void ResolveFailed(ILogger logger, string context, string type, string asmVersion)
        {
            logger?.Warn(
                "补丁解析失败：模块={0}，类型={1}，dll版本={2}",
                context ?? "unknown",
                type ?? "<null>",
                asmVersion ?? "<unknown>");
        }

        public static void Patched(ILogger logger, string module, MethodInfo method)
        {
            if (method == null)
            {
                Patched(logger, module, "<null>", null);
                return;
            }

            var signature = string.Format(
                "{0}.{1}({2}) -> {3}",
                method.DeclaringType?.FullName ?? "<unknown>",
                method.Name,
                string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)),
                method.ReturnType?.Name ?? "<void>");
            var dllVersion = method.Module?.Assembly?.GetName()?.Version?.ToString();
            Patched(logger, module, signature, dllVersion);
        }

        public static void Patched(ILogger logger, string module, string method)
        {
            Patched(logger, module, method, null);
        }

        public static void Patched(ILogger logger, string module, string method, string dllVersion)
        {
            logger?.Debug(
                "补丁安装：模块={0}，dll版本={1}，方法={2}",
                module ?? "unknown",
                dllVersion ?? "<unknown>",
                method ?? "unknown");
        }

        public static void Candidates(ILogger logger, string module, string candidates)
        {
            logger?.Debug(
                "补丁候选：模块={0}，候选={1}",
                module ?? "unknown",
                candidates ?? string.Empty);
        }
    }
}
