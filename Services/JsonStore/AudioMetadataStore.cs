using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Services
{
    public class AudioMetadataStore
    {
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public AudioMetadataStore(IJsonSerializer jsonSerializer)
        {
            this.jsonSerializer = jsonSerializer;
            this.logger = Plugin.Instance.Logger;
        }

        public AudioMetadataSnapshot ReadFromFile(BaseItem item)
        {
            var snapshot = ReadDocuments(MediaInfoDocument.GetMediaInfoJsonPath(item)).FirstOrDefault()?.AudioMetadata;
            this.logger.Debug($"AudioMetadataStore 从文件读取音乐元数据: {(item.FileName ?? item.Path)} 是否存在={snapshot != null}");
            return snapshot;
        }

        public bool HasInFile(BaseItem item)
        {
            var hasInFile = ReadFromFile(item) != null;
            this.logger.Debug($"AudioMetadataStore 检查文件是否包含音乐元数据: {(item.FileName ?? item.Path)} 结果={hasInFile}");
            return hasInFile;
        }

        public bool WriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            if (document.AudioMetadata != null)
            {
                this.logger.Debug($"AudioMetadataStore Json写入音乐元数据跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            var snapshot = CreateForPersist(item);
            if (snapshot == null)
            {
                this.logger.Debug($"AudioMetadataStore Json写入音乐元数据跳过: {(item.FileName ?? item.Path)} 非音频或无有效数据");
                return false;
            }

            document.AudioMetadata = snapshot;
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"AudioMetadataStore Json写入音乐元数据成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        public void OverWriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            document.AudioMetadata = CreateForPersist(item);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"AudioMetadataStore 覆盖Json写入音乐元数据成功: {(item.FileName ?? item.Path)}");
        }

        public MediaInfoDocument.MediaInfoRestoreResult ApplyToItem(BaseItem item)
        {
            if (item is not Audio audio)
            {
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            var snapshot = ReadFromFile(item);
            if (snapshot == null)
            {
                this.logger.Debug($"AudioMetadataStore 恢复音乐元数据失败: {(item.FileName ?? item.Path)} JSON 中无音乐元数据");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(snapshot.Name))
                {
                    audio.Name = snapshot.Name;
                }

                audio.Album = snapshot.Album;
                audio.AlbumArtists = snapshot.AlbumArtists ?? Array.Empty<string>();
                audio.Artists = snapshot.Artists ?? Array.Empty<string>();
                audio.Genres = snapshot.Genres ?? Array.Empty<string>();
                audio.IndexNumber = snapshot.IndexNumber;
                audio.ParentIndexNumber = snapshot.ParentIndexNumber;
                audio.ProductionYear = snapshot.ProductionYear;
                audio.SetProviderIds(new ProviderIdDictionary(snapshot.ProviderIds ?? new Dictionary<string, string>()));
                audio.UpdateToRepository(ItemUpdateType.MetadataImport);

                this.logger.Debug($"AudioMetadataStore 恢复音乐元数据完成: {(item.FileName ?? item.Path)}");
                return MediaInfoDocument.MediaInfoRestoreResult.Restored;
            }
            catch (Exception e)
            {
                this.logger.Error($"AudioMetadataStore 恢复音乐元数据失败: {(item.FileName ?? item.Path)}");
                this.logger.Error(e.Message);
                this.logger.Debug(e.StackTrace);
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }
        }

        private static AudioMetadataSnapshot CreateForPersist(BaseItem item)
        {
            if (item is not Audio audio)
            {
                return null;
            }

            return new AudioMetadataSnapshot
            {
                Name = audio.Name,
                Album = audio.Album,
                AlbumArtists = audio.AlbumArtists ?? Array.Empty<string>(),
                Artists = audio.Artists ?? Array.Empty<string>(),
                Genres = audio.Genres ?? Array.Empty<string>(),
                IndexNumber = audio.IndexNumber,
                ParentIndexNumber = audio.ParentIndexNumber,
                ProductionYear = audio.ProductionYear,
                ProviderIds = new Dictionary<string, string>(audio.ProviderIds ?? new ProviderIdDictionary(), StringComparer.OrdinalIgnoreCase)
            };
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
    }
}
