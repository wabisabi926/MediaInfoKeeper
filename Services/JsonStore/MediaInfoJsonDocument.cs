using System;
using System.IO;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Services
{
    public class MediaInfoJsonDocument
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

        public MediaSourceInfo MediaSourceInfo { get; set; }

        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();

        public static string GetMediaInfoJsonPath(BaseItem item)
        {
            var jsonRootFolder = Plugin.Instance.Options.MainPage.MediaInfoJsonRootFolder?.Trim();
            var mediaInfoFileName = item.FileNameWithoutExtension + MediaInfoFileExtension;

            return !string.IsNullOrWhiteSpace(jsonRootFolder)
                ? Path.Combine(jsonRootFolder, mediaInfoFileName)
                : Path.Combine(item.ContainingFolderPath, mediaInfoFileName);
        }

        public static void DeleteMediaInfoJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            var logger = Plugin.Instance?.Logger;
            var fileSystem = Plugin.FileSystem;
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists is true)
            {
                try
                {
                    logger?.Info($"MediaInfoKeeper {source} 尝试删除: {item.FileName ?? item.Path} {mediaInfoJsonPath}");
                    fileSystem.DeleteFile(mediaInfoJsonPath);
                }
                catch (Exception e)
                {
                    logger?.Error(e.Message);
                    logger?.Debug(e.StackTrace);
                }
            }
            else
            {
                logger?.Info($"MediaInfoKeeper {source} 未找到JSON: {item.FileName ?? item.Path} {mediaInfoJsonPath}");
            }
        }
    }
}
