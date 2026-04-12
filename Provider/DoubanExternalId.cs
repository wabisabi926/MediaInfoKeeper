using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Provider
{
    public class DoubanExternalId : IExternalId
    {
        private string subjectId;

        public string Name => "Douban";

        public string Key => StaticName;

        public string UrlFormatString => string.IsNullOrWhiteSpace(subjectId)
            ? "https://movie.douban.com/subject/{0}/"
            : $"https://movie.douban.com/subject/{subjectId}/";

        public bool Supports(IHasProviderIds item)
        {
            subjectId = item?.GetProviderId(StaticName)?.Trim();
            return item is Movie || item is Series;
        }

        public static string StaticName => "Douban";
    }
}
