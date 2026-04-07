using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace MediaInfoKeeper.Services
{
    internal static class MediaInfoPersistService
    {
        public static void OverWritePersistedMedia(BaseItem item)
        {
            if (item == null)
            {
                return;
            }

            Plugin.MediaSourceInfoStore?.OverWriteToFile(item);
            Plugin.CoverStore?.OverWriteToFile(item);

            if (item is Video)
            {
                Plugin.ChaptersStore?.WriteToFile(item);
                return;
            }

            if (item is Audio)
            {
                Plugin.AudioMetadataStore?.OverWriteToFile(item);
            }
        }
    }
}
