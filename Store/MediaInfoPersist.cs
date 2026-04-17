using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace MediaInfoKeeper.Store
{
    internal static class MediaInfoPersist
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
                Plugin.AudioMetadataStore?.OverWriteToFile(item);
            }
        }
    }
}
