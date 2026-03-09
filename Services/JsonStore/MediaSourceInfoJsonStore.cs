using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Services
{
    public class MediaSourceInfoJsonStore
    {
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public MediaSourceInfoJsonStore(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IFileSystem fileSystem,
            IJsonSerializer jsonSerializer)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.jsonSerializer = jsonSerializer;
            this.logger = Plugin.Instance.Logger;
        }

        public MediaSourceInfo ReadFromFile(BaseItem item)
        {
            var mediaSourceInfo = ReadDocuments(MediaInfoJsonDocument.GetMediaInfoJsonPath(item)).FirstOrDefault()?.MediaSourceInfo;
            this.logger.Debug($"MediaSourceInfoJsonStore 从文件读取媒体源信息: {(item.FileName ?? item.Path)} 是否存在={mediaSourceInfo != null}");
            return mediaSourceInfo;
        }

        public bool HasInFile(BaseItem item)
        {
            var hasInFile = ReadFromFile(item) != null;
            this.logger.Debug($"MediaSourceInfoJsonStore 检查文件是否包含媒体源信息: {(item.FileName ?? item.Path)} 结果={hasInFile}");
            return hasInFile;
        }

        public async Task<MediaSourceInfo> ReadFromFileAsync(BaseItem item)
        {
            var mediaSourceInfo = (await ReadDocumentsAsync(MediaInfoJsonDocument.GetMediaInfoJsonPath(item)).ConfigureAwait(false))
                .FirstOrDefault()
                ?.MediaSourceInfo;
            this.logger.Debug($"MediaSourceInfoJsonStore 异步读取媒体源信息: {(item.FileName ?? item.Path)} 是否存在={mediaSourceInfo != null}");
            return mediaSourceInfo;
        }

        public bool WriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoJsonDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoJsonDocument();
            if (document.MediaSourceInfo != null)
            {
                this.logger.Info($"MediaSourceInfoJsonStore Json写入媒体源信息跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            document.MediaSourceInfo = CreateForPersist(item);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Info($"MediaSourceInfoJsonStore Json写入媒体源信息成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        public void OverWriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoJsonDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoJsonDocument();
            document.MediaSourceInfo = CreateForPersist(item);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Info($"MediaSourceInfoJsonStore 覆盖Json写入媒体源信息成功: {(item.FileName ?? item.Path)}");
        }

        public bool DeleteFromFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoJsonDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault();
            if (document?.MediaSourceInfo == null)
            {
                this.logger.Info($"MediaSourceInfoJsonStore 删除Json媒体源信息跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            document.MediaSourceInfo = null;

            if (document.Chapters == null || document.Chapters.Count == 0)
            {
                DeleteJsonFile(mediaInfoJsonPath);
                this.logger.Info($"MediaSourceInfoJsonStore 删除Json媒体源信息成功并删除文件: {(item.FileName ?? item.Path)}");
                return true;
            }

            this.jsonSerializer.SerializeToFile(documents, mediaInfoJsonPath);
            this.logger.Info($"MediaSourceInfoJsonStore 删除Json媒体源信息成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        public MediaInfoJsonDocument.MediaInfoRestoreResult ApplyToItem(BaseItem item)
        {
            if (item == null)
            {
                this.logger.Info("MediaSourceInfoJsonStore 恢复媒体源信息失败: 条目为空");
                return MediaInfoJsonDocument.MediaInfoRestoreResult.Failed;
            }

            if (Plugin.LibraryService?.HasMediaInfo(item) == true)
            {
                this.logger.Info($"MediaSourceInfoJsonStore 恢复媒体源信息跳过: {(item.FileName ?? item.Path)} 已存在媒体信息");
                return MediaInfoJsonDocument.MediaInfoRestoreResult.AlreadyExists;
            }

            var mediaSourceInfo = ReadFromFile(item);
            if (mediaSourceInfo?.RunTimeTicks.HasValue is not true)
            {
                this.logger.Info($"MediaSourceInfoJsonStore 恢复媒体源信息失败: {(item.FileName ?? item.Path)} JSON 中无有效媒体源信息");
                return MediaInfoJsonDocument.MediaInfoRestoreResult.Failed;
            }

            try
            {
                foreach (var subtitle in (mediaSourceInfo.MediaStreams ?? new List<MediaStream>()).Where(m =>
                             m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                             m.Protocol == MediaProtocol.File))
                {
                    subtitle.Path = System.IO.Path.Combine(item.ContainingFolderPath,
                        this.fileSystem.GetFileInfo(subtitle.Path).Name);
                }

                this.itemRepository.SaveMediaStreams(item.InternalId, mediaSourceInfo.MediaStreams ?? new List<MediaStream>(), CancellationToken.None);

                item.Size = mediaSourceInfo.Size.GetValueOrDefault();
                item.RunTimeTicks = mediaSourceInfo.RunTimeTicks;
                item.Container = mediaSourceInfo.Container;
                item.TotalBitrate = mediaSourceInfo.Bitrate.GetValueOrDefault();

                var videoStream = (mediaSourceInfo.MediaStreams ?? new List<MediaStream>())
                    .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                    .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                    .FirstOrDefault();

                if (videoStream != null)
                {
                    item.Width = videoStream.Width.GetValueOrDefault();
                    item.Height = videoStream.Height.GetValueOrDefault();
                }
                this.logger.Info($"MediaSourceInfoJsonStore 恢复媒体源信息到条目完成: {(item.FileName ?? item.Path)}");
                return MediaInfoJsonDocument.MediaInfoRestoreResult.Restored;
            }
            catch (Exception e)
            {
                this.logger.Error($"MediaSourceInfoJsonStore 恢复媒体源信息失败: {(item.FileName ?? item.Path)}");
                this.logger.Error(e.Message);
                this.logger.Debug(e.StackTrace);
                return MediaInfoJsonDocument.MediaInfoRestoreResult.Failed;
            }
        }

        private void SaveDocuments(List<MediaInfoJsonDocument> documents, MediaInfoJsonDocument document, string mediaInfoJsonPath)
        {
            if (documents.Count == 0)
            {
                documents.Add(document);
            }

            this.jsonSerializer.SerializeToFile(documents, mediaInfoJsonPath);
        }

        private MediaSourceInfo CreateForPersist(BaseItem item)
        {
            var options = this.libraryManager.GetLibraryOptions(item);
            var mediaSource = item.GetMediaSources(false, false, options).FirstOrDefault();
            if (mediaSource == null)
            {
                return null;
            }

            mediaSource.Id = null;
            mediaSource.ItemId = null;
            mediaSource.Path = null;

            foreach (var subtitle in mediaSource.MediaStreams.Where(m =>
                         m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                         m.Protocol == MediaProtocol.File))
            {
                subtitle.Path = this.fileSystem.GetFileInfo(subtitle.Path).Name;
            }

            return mediaSource;
        }

        private List<MediaInfoJsonDocument> ReadDocuments(string mediaInfoJsonPath)
        {
            try
            {
                return this.jsonSerializer.DeserializeFromFile<List<MediaInfoJsonDocument>>(mediaInfoJsonPath) ??
                       new List<MediaInfoJsonDocument>();
            }
            catch
            {
                return new List<MediaInfoJsonDocument>();
            }
        }

        private async Task<List<MediaInfoJsonDocument>> ReadDocumentsAsync(string mediaInfoJsonPath)
        {
            try
            {
                return await this.jsonSerializer
                    .DeserializeFromFileAsync<List<MediaInfoJsonDocument>>(mediaInfoJsonPath)
                    .ConfigureAwait(false) ?? new List<MediaInfoJsonDocument>();
            }
            catch
            {
                return new List<MediaInfoJsonDocument>();
            }
        }

        private void DeleteJsonFile(string mediaInfoJsonPath)
        {
            if (this.fileSystem.FileExists(mediaInfoJsonPath))
            {
                this.fileSystem.DeleteFile(mediaInfoJsonPath);
                return;
            }

            if (File.Exists(mediaInfoJsonPath))
            {
                File.Delete(mediaInfoJsonPath);
            }
        }
    }
}
