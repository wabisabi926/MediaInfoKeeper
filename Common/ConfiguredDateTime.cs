using System;

namespace MediaInfoKeeper.Common
{
    internal static class ConfiguredDateTime
    {
        private static readonly TimeZoneInfo ConfiguredTimeZone = ResolveTimeZone();

        public static TimeSpan Offset => ConfiguredTimeZone.GetUtcOffset(DateTime.UtcNow);

        public static string TimeZoneId => ConfiguredTimeZone.Id;

        public static DateTimeOffset NowOffset => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ConfiguredTimeZone);

        public static DateTime Now => NowOffset.DateTime;

        public static DateTime Today => Now.Date;

        public static DateTimeOffset ToConfiguredOffset(DateTimeOffset value)
        {
            return TimeZoneInfo.ConvertTime(value, ConfiguredTimeZone);
        }

        public static DateTimeOffset ToConfiguredOffset(DateTime value)
        {
            DateTimeOffset source;
            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    source = new DateTimeOffset(value, TimeSpan.Zero);
                    break;
                case DateTimeKind.Local:
                    source = new DateTimeOffset(value).ToUniversalTime();
                    break;
                default:
                    source = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
                    break;
            }

            return TimeZoneInfo.ConvertTime(source, ConfiguredTimeZone);
        }

        private static TimeZoneInfo ResolveTimeZone()
        {
            var tzFromEnv = Environment.GetEnvironmentVariable("TZ")?.Trim();
            if (!string.IsNullOrWhiteSpace(tzFromEnv))
            {
                var fromEnv = TryFindTimeZone(tzFromEnv);
                if (fromEnv != null)
                {
                    return fromEnv;
                }
            }

            var shanghai = TryFindTimeZone("Asia/Shanghai") ?? TryFindTimeZone("China Standard Time");
            if (shanghai != null)
            {
                return shanghai;
            }

            return TimeZoneInfo.CreateCustomTimeZone(
                "UTC+08",
                TimeSpan.FromHours(8),
                "UTC+08",
                "UTC+08");
        }

        private static TimeZoneInfo TryFindTimeZone(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                return null;
            }
            catch (InvalidTimeZoneException)
            {
                return null;
            }
        }
    }
}
