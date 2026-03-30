using System;
using System.IO;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Services
{
    public class MediaInfoDocument
    {
        [Flags]
        public enum MediaInfoRestoreResult
        {
            // 恢复成功
            Restored = 1,
            // 已经存在，无需恢复
            AlreadyExists = 0,
            // 恢复失败
            Failed = -1
        }

        private const string MediaInfoFileExtension = "-mediainfo.json";
        private const string CoverFileExtension = "-cover.jpg";
        private const string LyricsFileExtension = "-lyrics.json";

        public MediaSourceInfo MediaSourceInfo { get; set; }

        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();

        public AudioMetadataSnapshot AudioMetadata { get; set; }

        public EmbeddedCoverSnapshot EmbeddedCover { get; set; }

        public string LyricsFileName { get; set; }

        public static string GetMediaInfoJsonPath(BaseItem item)
        {
            return BuildSidecarPath(item, MediaInfoFileExtension);
        }

        public static string GetCoverPath(BaseItem item)
        {
            return BuildSidecarPath(item, CoverFileExtension);
        }

        public static string GetLyricsJsonPath(BaseItem item)
        {
            return BuildSidecarPath(item, LyricsFileExtension);
        }

        public static string GetCoverFileName(BaseItem item)
        {
            return item.FileNameWithoutExtension + CoverFileExtension;
        }

        public static string GetLyricsFileName(BaseItem item)
        {
            return item.FileNameWithoutExtension + LyricsFileExtension;
        }

        private static string BuildSidecarPath(BaseItem item, string extension)
        {
            var jsonRootFolder = Plugin.Instance.Options.MainPage.MediaInfoJsonRootFolder?.Trim();
            var fileName = item.FileNameWithoutExtension + extension;

            return !string.IsNullOrWhiteSpace(jsonRootFolder)
                ? Path.Combine(GetConfiguredRootFolder(item, jsonRootFolder), fileName)
                : Path.Combine(item.ContainingFolderPath, fileName);
        }

        private static string GetConfiguredRootFolder(BaseItem item, string jsonRootFolder)
        {
            if (item is Audio)
            {
                return Path.Combine(jsonRootFolder, "music");
            }

            return jsonRootFolder;
        }

        public static void DeleteMediaInfoJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            DeleteSidecar(item, directoryService, GetMediaInfoJsonPath(item), "JSON", source);
        }

        public static void DeleteLyricsJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            DeleteSidecar(item, directoryService, GetLyricsJsonPath(item), "歌词JSON", source);
        }

        public static void DeleteCover(BaseItem item, IDirectoryService directoryService, string source)
        {
            DeleteSidecar(item, directoryService, GetCoverPath(item), "封面", source);
        }

        private static void DeleteSidecar(BaseItem item, IDirectoryService directoryService, string path, string label, string source)
        {
            var logger = Plugin.Instance?.Logger;
            var fileSystem = Plugin.FileSystem;
            var file = directoryService.GetFile(path);

            if (file?.Exists is not true)
            {
                logger?.Info($"MediaInfoKeeper {source} 未找到{label}: {item.FileName ?? item.Path} {path}");
                return;
            }

            try
            {
                logger?.Info($"MediaInfoKeeper {source} 尝试删除{label}: {item.FileName ?? item.Path} {path}");
                fileSystem.DeleteFile(path);
            }
            catch (Exception e)
            {
                logger?.Error(e.Message);
                logger?.Debug(e.StackTrace);
            }
        }
    }

    public class AudioMetadataSnapshot
    {
        public string Name { get; set; }

        public string Album { get; set; }

        public string[] AlbumArtists { get; set; } = Array.Empty<string>();

        public string[] Artists { get; set; } = Array.Empty<string>();

        public string[] Genres { get; set; } = Array.Empty<string>();

        public int? IndexNumber { get; set; }

        public int? ParentIndexNumber { get; set; }

        public int? ProductionYear { get; set; }

        public Dictionary<string, string> ProviderIds { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class EmbeddedLyricsSnapshot
    {
        public string Title { get; set; }

        public string Language { get; set; }

        public string Codec { get; set; }

        public string Content { get; set; }
    }

    public class EmbeddedCoverSnapshot
    {
        public string FileName { get; set; }

        public string MimeType { get; set; }

        public ImageType ImageType { get; set; } = ImageType.Primary;

        public bool IsEmbedded { get; set; } = true;
    }
}
