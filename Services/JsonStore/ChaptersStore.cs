using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.Services
{
    public class ChaptersStore
    {
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public ChaptersStore(IItemRepository itemRepository, IFileSystem fileSystem, IJsonSerializer jsonSerializer)
        {
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.jsonSerializer = jsonSerializer;
            this.logger = Plugin.Instance.Logger;
        }

        public List<ChapterInfo> ReadFromFile(BaseItem item)
        {
            var chapters = ReadDocuments(MediaInfoDocument.GetMediaInfoJsonPath(item)).FirstOrDefault()?.Chapters ??
                           new List<ChapterInfo>();
            this.logger.Debug($"ChaptersStore 从文件读取章节信息: {(item.FileName ?? item.Path)}");
            return chapters;
        }

        public bool HasInFile(BaseItem item)
        {
            var hasInFile = ReadFromFile(item).Count > 0;
            this.logger.Debug($"ChaptersStore 检查文件是否包含章节信息: {(item.FileName ?? item.Path)} 结果={hasInFile}");
            return hasInFile;
        }

        public bool WriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            if (document.Chapters != null && document.Chapters.Count > 0)
            {
                this.logger.Debug($"ChaptersStore Json写入章节信息跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            document.Chapters = CreateForPersist(item);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"ChaptersStore Json写入章节信息成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        public void OverWriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            document.Chapters = CreateForPersist(item);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"ChaptersStore 覆盖Json写入章节信息成功: {(item.FileName ?? item.Path)}");
        }

        private List<ChapterInfo> CreateForPersist(BaseItem item)
        {
            var chapters = this.itemRepository.GetChapters(item) ?? new List<ChapterInfo>();
            chapters = chapters
                .Where(chapter => chapter != null && chapter.MarkerType != MarkerType.Chapter)
                .ToList();

            foreach (var chapter in chapters)
            {
                chapter.ImageTag = null;
            }

            return chapters;
        }

        public MediaInfoDocument.MediaInfoRestoreResult ApplyToItem(BaseItem item)
        {
            if (item == null)
            {
                this.logger.Debug("ChaptersStore 恢复章节失败: 条目为空");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            var existingChapters = this.itemRepository.GetChapters(item) ?? new List<ChapterInfo>();
            if (existingChapters.Count > 0)
            {
                this.logger.Debug($"ChaptersStore 恢复章节跳过: {(item.FileName ?? item.Path)} 已存在章节信息");
                return MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists;
            }

            var chapters = ReadFromFile(item);
            if (chapters.Count == 0)
            {
                this.logger.Debug($"ChaptersStore 恢复章节失败: {(item.FileName ?? item.Path)} JSON 中无章节数据");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            try
            {
                using (IntroMarkerProtect.Allow(item.InternalId))
                {
                    this.itemRepository.SaveChapters(item.InternalId, true, chapters ?? new List<ChapterInfo>());
                }
                this.logger.Debug($"ChaptersStore 恢复章节到条目完成: {(item.FileName ?? item.Path)}");
                return MediaInfoDocument.MediaInfoRestoreResult.Restored;
            }
            catch (Exception e)
            {
                this.logger.Error($"ChaptersStore 恢复章节失败: {(item.FileName ?? item.Path)}");
                this.logger.Error(e.Message);
                this.logger.Debug(e.StackTrace);
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }
        }

        public bool DeleteFromFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault();
            if (document?.Chapters == null || document.Chapters.Count == 0)
            {
                this.logger.Info($"ChaptersStore 删除Json章节信息跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            document.Chapters.Clear();

            if (document.MediaSourceInfo == null)
            {
                DeleteJsonFile(mediaInfoJsonPath);
                this.logger.Info($"ChaptersStore 删除Json章节信息成功并删除文件: {(item.FileName ?? item.Path)}");
                return true;
            }

            this.jsonSerializer.SerializeToFile(documents, mediaInfoJsonPath);
            this.logger.Info($"ChaptersStore 删除Json章节信息成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        private void SaveDocuments(List<MediaInfoDocument> documents, MediaInfoDocument document, string mediaInfoJsonPath)
        {
            if (documents.Count == 0)
            {
                documents.Add(document);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(mediaInfoJsonPath));
            this.jsonSerializer.SerializeToFile(documents, mediaInfoJsonPath);
        }

        private bool TryRemoveMarkers(List<ChapterInfo> chapters, MarkerType[] markerTypes)
        {
            if (chapters == null || chapters.Count == 0)
            {
                return false;
            }

            if (markerTypes == null || markerTypes.Length == 0)
            {
                markerTypes = new[] { MarkerType.IntroStart, MarkerType.IntroEnd, MarkerType.CreditsStart };
            }

            return chapters.RemoveAll(c => markerTypes.Contains(c.MarkerType)) > 0;
        }

        private List<MediaInfoDocument> ReadDocuments(string mediaInfoJsonPath)
        {
            try
            {
                return this.jsonSerializer.DeserializeFromFile<List<MediaInfoDocument>>(mediaInfoJsonPath) ??
                       new List<MediaInfoDocument>();
            }
            catch
            {
                return new List<MediaInfoDocument>();
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
