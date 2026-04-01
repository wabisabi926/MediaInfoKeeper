using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Services
{
    public class CoverStore
    {
        private readonly ILibraryManager libraryManager;
        private readonly IFileSystem fileSystem;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public CoverStore(ILibraryManager libraryManager, IFileSystem fileSystem, IJsonSerializer jsonSerializer)
        {
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
            this.jsonSerializer = jsonSerializer;
            this.logger = Plugin.Instance.Logger;
        }

        public EmbeddedCoverSnapshot ReadFromFile(BaseItem item)
        {
            var snapshot = ReadDocuments(MediaInfoDocument.GetMediaInfoJsonPath(item)).FirstOrDefault()?.EmbeddedCover;
            this.logger.Debug($"CoverStore 从文件读取封面信息: {(item.FileName ?? item.Path)} 是否存在={snapshot != null}");
            return snapshot;
        }

        public bool HasInFile(BaseItem item)
        {
            var snapshot = ReadFromFile(item);
            var hasInFile = snapshot != null && this.fileSystem.FileExists(MediaInfoDocument.GetCoverPath(item));
            this.logger.Debug($"CoverStore 检查文件是否包含封面: {(item.FileName ?? item.Path)} 结果={hasInFile}");
            return hasInFile;
        }

        public bool WriteToFile(BaseItem item)
        {
            var source = ResolveSourceImage(item);
            if (source == null)
            {
                this.logger.Debug($"CoverStore Json写入封面跳过: {(item.FileName ?? item.Path)} 无主图");
                return false;
            }

            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            if (document.EmbeddedCover != null && this.fileSystem.FileExists(MediaInfoDocument.GetCoverPath(item)))
            {
                this.logger.Debug($"CoverStore Json写入封面跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            PersistCover(item, document, source);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"CoverStore Json写入封面成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        public void OverWriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            var source = ResolveSourceImage(item);

            if (source == null)
            {
                DeleteCoverFile(item);
                document.EmbeddedCover = null;
                SaveDocuments(documents, document, mediaInfoJsonPath);
                this.logger.Debug($"CoverStore 覆盖封面写入跳过: {(item.FileName ?? item.Path)} 无主图");
                return;
            }

            PersistCover(item, document, source);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"CoverStore 覆盖写入封面成功: {(item.FileName ?? item.Path)}");
        }

        public MediaInfoDocument.MediaInfoRestoreResult ApplyToItem(BaseItem item)
        {
            var snapshot = ReadFromFile(item);
            var coverPath = MediaInfoDocument.GetCoverPath(item);
            if (snapshot == null || !this.fileSystem.FileExists(coverPath))
            {
                this.logger.Debug($"CoverStore 恢复封面失败: {(item.FileName ?? item.Path)} sidecar 中无封面");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            if (item.HasImage(ImageType.Primary))
            {
                this.logger.Debug($"CoverStore 恢复封面跳过: {(item.FileName ?? item.Path)} 已存在主图");
                return MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists;
            }

            try
            {
                var libraryOptions = this.libraryManager.GetLibraryOptions(item);
                using (var stream = File.OpenRead(coverPath))
                {
                    Plugin.ProviderManager
                        .SaveImage(
                            item,
                            libraryOptions,
                            stream,
                            (snapshot.MimeType ?? "image/jpeg").AsMemory(),
                            snapshot.ImageType,
                            0,
                            Array.Empty<long>(),
                            new DirectoryService(this.logger, this.fileSystem),
                            true,
                            System.Threading.CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }

                item.UpdateToRepository(ItemUpdateType.ImageUpdate);
                this.logger.Debug($"CoverStore 恢复封面完成: {(item.FileName ?? item.Path)}");
                return MediaInfoDocument.MediaInfoRestoreResult.Restored;
            }
            catch (Exception e)
            {
                this.logger.Error($"CoverStore 恢复封面失败: {(item.FileName ?? item.Path)}");
                this.logger.Error(e.Message);
                this.logger.Debug(e.StackTrace);
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }
        }

        private void PersistCover(BaseItem item, MediaInfoDocument document, SourceImage source)
        {
            var coverPath = MediaInfoDocument.GetCoverPath(item);
            Directory.CreateDirectory(Path.GetDirectoryName(coverPath));
            this.fileSystem.CopyFile(source.Path, coverPath, true);
            document.EmbeddedCover = new EmbeddedCoverSnapshot
            {
                FileName = MediaInfoDocument.GetCoverFileName(item),
                MimeType = source.MimeType,
                ImageType = ImageType.Primary,
                IsEmbedded = true
            };
        }
        
        //音频可以继续往上拿专辑主图当封面 sidecar，视频不允许拿展示父级图来持久化
        private SourceImage ResolveSourceImage(BaseItem item)
        {
            var image = item.GetImageInfo(ImageType.Primary, 0);
            if (IsUsableImage(image))
            {
                return new SourceImage(image.Path, GetMimeType(image.Path));
            }

            if (item is not Audio)
            {
                return null;
            }

            var displayParentId = item.ImageDisplayParentId;
            if (displayParentId == 0 || displayParentId == item.InternalId)
            {
                return null;
            }

            var displayParent = this.libraryManager.GetItemById(displayParentId);
            var displayParentImage = displayParent?.GetImageInfo(ImageType.Primary, 0);
            if (!IsUsableImage(displayParentImage))
            {
                return null;
            }

            return new SourceImage(displayParentImage.Path, GetMimeType(displayParentImage.Path));
        }

        private bool IsUsableImage(ItemImageInfo image)
        {
            return image != null &&
                   !string.IsNullOrWhiteSpace(image.Path) &&
                   this.fileSystem.FileExists(image.Path);
        }

        private static string GetMimeType(string path)
        {
            var extension = Path.GetExtension(path)?.ToLowerInvariant();
            return extension switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
        }

        public void DeleteCoverFile(BaseItem item)
        {
            var coverPath = MediaInfoDocument.GetCoverPath(item);
            if (this.fileSystem.FileExists(coverPath))
            {
                this.fileSystem.DeleteFile(coverPath);
                return;
            }

            if (File.Exists(coverPath))
            {
                File.Delete(coverPath);
            }
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

        private sealed class SourceImage
        {
            public SourceImage(string path, string mimeType)
            {
                Path = path;
                MimeType = mimeType;
            }

            public string Path { get; }

            public string MimeType { get; }
        }
    }
}
