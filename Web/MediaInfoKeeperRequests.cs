using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace MediaInfoKeeper.Web
{
    [Route("/{Web}/components/mediainfokeeper/mediainfokeeper.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class MediaInfoKeeperJsRequest
    {
        public string Web { get; set; }

        public string ResourceName { get; set; }
    }

    [Route("/{Web}/components/mediainfokeeper/ede.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class EdeJsRequest
    {
        public string Web { get; set; }
    }

    [Route("/{Web}/modules/shortcuts.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class ShortcutMenuRequest
    {
        public string Web { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ExtractMediaInfo", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ExtractMediaInfoRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/DeleteMediaInfoPersist", "POST")]
    [Authenticated(Roles = "Admin")]
    public class DeleteMediaInfoPersistRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ScanIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ScanIntroRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/SetIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SetIntroRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
        public long IntroStartTicks { get; set; }
        public long IntroEndTicks { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ClearIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearIntroRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/DebugMediaInfo", "GET")]
    [Authenticated(Roles = "Admin")]
    public class DebugMediaInfoRequest : IReturn<DebugMediaInfoResponse>
    {
        public long InternalId { get; set; }
    }

    public class MediaInfoMenuResponse
    {
        public int Total { get; set; }

        public int Processed { get; set; }

        public int Succeeded { get; set; }

        public int Failed { get; set; }

        public int Skipped { get; set; }

        public string Message { get; set; }
    }

    public class DebugMediaInfoResponse
    {
        public bool Found { get; set; }

        public string Message { get; set; }

        public DebugItemInfo Item { get; set; }

        public DebugFileInfo MediaInfoJson { get; set; }

        public DebugFileInfo LyricsJson { get; set; }

        public DebugBinaryFileInfo Cover { get; set; }
    }

    public class DebugItemInfo
    {
        public long InternalId { get; set; }

        public string Type { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public string FileName { get; set; }

        public string ContainingFolderPath { get; set; }

        public bool HasMediaInfo { get; set; }

        public bool HasCover { get; set; }

        public int MediaStreamCount { get; set; }

        public int AudioStreamCount { get; set; }

        public int VideoStreamCount { get; set; }

        public int SubtitleStreamCount { get; set; }

        public long? RunTimeTicks { get; set; }
    }

    public class DebugFileInfo
    {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public object Content { get; set; }
    }

    public class DebugBinaryFileInfo
    {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public long Length { get; set; }
    }

}
