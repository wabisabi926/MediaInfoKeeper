using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Provider
{
    public class MovieDbEpisodeGroupExternalId : IExternalId
    {
        private string tmdbId;
        private string episodeGroupId;

        public string Name => "MovieDb Episode Group";

        public string Key => StaticName;

        public string UrlFormatString
        {
            get
            {
                if (string.IsNullOrWhiteSpace(episodeGroupId))
                {
                    return string.IsNullOrWhiteSpace(tmdbId) ? null : $"https://www.themoviedb.org/tv/{tmdbId}/episode_group/{{0}}";
                }

                return episodeGroupId.StartsWith("http://") || episodeGroupId.StartsWith("https://")
                    ? episodeGroupId
                    : (string.IsNullOrWhiteSpace(tmdbId) ? null : $"https://www.themoviedb.org/tv/{tmdbId}/episode_group/{{0}}");
            }
        }

        public bool Supports(IHasProviderIds item)
        {
            tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            episodeGroupId = item.GetProviderId(StaticName);
            return item is Series;
        }

        public static string StaticName => "TmdbEg";
    }
}
