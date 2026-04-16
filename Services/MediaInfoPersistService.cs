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

            if (item is Video)
            {
                Plugin.ChaptersStore?.OverWriteToFile(item);
                return;
            }

            if (item is Audio)
            {
                Plugin.CoverStore?.OverWriteToFile(item);
                Plugin.AudioMetadataStore?.OverWriteToFile(item);
            }
        }
    }
}
