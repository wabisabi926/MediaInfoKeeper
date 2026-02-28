using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    public sealed class MethodSignatureProfile
    {
        public string Name { get; set; }

        public string MethodName { get; set; }

        public BindingFlags BindingFlags { get; set; } =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public Type[] ParameterTypes { get; set; }

        public Type ReturnType { get; set; }

        public bool? IsStatic { get; set; }

        public Func<MethodInfo, bool> Predicate { get; set; }
    }

    public static class VersionedMethodResolver
    {
        public static MethodInfo Resolve(
            Type type,
            Version assemblyVersion,
            IEnumerable<MethodSignatureProfile> exactProfiles,
            ILogger logger,
            string context)
        {
            var method = TryResolve(type, assemblyVersion, exactProfiles, "exact", logger, context);
            if (method != null)
            {
                return method;
            }

            PatchLog.ResolveFailed(
                logger,
                context ?? "MethodResolve",
                type?.FullName ?? "<null>",
                assemblyVersion?.ToString() ?? "<unknown>");
            return null;
        }

        private static MethodInfo TryResolve(
            Type type,
            Version assemblyVersion,
            IEnumerable<MethodSignatureProfile> profiles,
            string level,
            ILogger logger,
            string context)
        {
            if (type == null || profiles == null)
            {
                return null;
            }

            foreach (var profile in profiles.Where(p => p != null).ToArray())
            {
                var method = ResolveSingle(type, profile);
                if (method == null)
                {
                    continue;
                }

                PatchLog.ResolveHit(
                    logger,
                    context ?? "MethodResolve",
                    level,
                    profile.Name ?? profile.MethodName ?? "unknown",
                    BuildSignature(method),
                    assemblyVersion?.ToString() ?? "<unknown>");
                return method;
            }

            return null;
        }

        private static MethodInfo ResolveSingle(Type type, MethodSignatureProfile profile)
        {
            if (type == null || profile == null || string.IsNullOrWhiteSpace(profile.MethodName))
            {
                return null;
            }

            if (profile.ParameterTypes != null)
            {
                var method = type.GetMethod(
                    profile.MethodName,
                    profile.BindingFlags,
                    null,
                    profile.ParameterTypes,
                    null);

                if (IsMatch(method, profile))
                {
                    return method;
                }
            }

            return type.GetMethods(profile.BindingFlags)
                .Where(m => string.Equals(m.Name, profile.MethodName, StringComparison.Ordinal))
                .FirstOrDefault(m => IsMatch(m, profile));
        }

        private static bool IsMatch(MethodInfo method, MethodSignatureProfile profile)
        {
            if (method == null || profile == null)
            {
                return false;
            }

            if (profile.IsStatic.HasValue && method.IsStatic != profile.IsStatic.Value)
            {
                return false;
            }

            if (profile.ReturnType != null && method.ReturnType != profile.ReturnType)
            {
                return false;
            }

            if (profile.ParameterTypes != null)
            {
                var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
                if (parameters.Length != profile.ParameterTypes.Length)
                {
                    return false;
                }

                for (var i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i] != profile.ParameterTypes[i])
                    {
                        return false;
                    }
                }
            }

            return profile.Predicate == null || profile.Predicate(method);
        }

        private static string BuildSignature(MethodInfo method)
        {
            if (method == null)
            {
                return "<null>";
            }

            var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));
            return string.Format(
                "{0}.{1}({2}) -> {3}",
                method.DeclaringType?.FullName ?? "<unknown>",
                method.Name,
                parameters,
                method.ReturnType?.Name ?? "<void>");
        }
    }
}
