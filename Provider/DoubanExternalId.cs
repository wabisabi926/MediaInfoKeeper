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
        private const string DoubanAppDispatchUrlTemplate = "https://www.douban.com/doubanapp/dispatch?uri=/movie/{0}?from=mdouban&open=app";
        private string subjectId;

        public string Name => "Douban";

        public string Key => StaticName;

        public string UrlFormatString => string.IsNullOrWhiteSpace(subjectId)
            ? DoubanAppDispatchUrlTemplate
            : $"https://www.douban.com/doubanapp/dispatch?uri=/movie/{subjectId}?from=mdouban&open=app";

        public bool Supports(IHasProviderIds item)
        {
            subjectId = item?.GetProviderId(StaticName)?.Trim();
            return item is Movie || item is Series;
        }

        public static string StaticName => "Douban";
    }
}
