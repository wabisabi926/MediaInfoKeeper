using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Services
{
    public class LyricsStore
    {
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public LyricsStore(IItemRepository itemRepository, IFileSystem fileSystem, IJsonSerializer jsonSerializer)
        {
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.jsonSerializer = jsonSerializer;
            this.logger = Plugin.Instance.Logger;
        }

        public List<EmbeddedLyricsSnapshot> ReadFromFile(BaseItem item)
        {
            var path = MediaInfoDocument.GetLyricsJsonPath(item);
            try
            {
                var list = this.jsonSerializer.DeserializeFromFile<List<EmbeddedLyricsSnapshot>>(path) ??
                           new List<EmbeddedLyricsSnapshot>();
                this.logger.Debug($"LyricsStore 从文件读取歌词: {(item.FileName ?? item.Path)} 条数={list.Count}");
                return list;
            }
            catch
            {
                return new List<EmbeddedLyricsSnapshot>();
            }
        }

        public bool HasInFile(BaseItem item)
        {
            var hasInFile = ReadFromFile(item).Count > 0;
            this.logger.Debug($"LyricsStore 检查文件是否包含歌词: {(item.FileName ?? item.Path)} 结果={hasInFile}");
            return hasInFile;
        }

        public bool WriteToFile(BaseItem item)
        {
            var lyrics = CreateForPersist(item);
            if (lyrics.Count == 0)
            {
                this.logger.Debug($"LyricsStore Json写入歌词跳过: {(item.FileName ?? item.Path)} 无内嵌歌词");
                return false;
            }

            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            if (!string.IsNullOrWhiteSpace(document.LyricsFileName) && this.fileSystem.FileExists(MediaInfoDocument.GetLyricsJsonPath(item)))
            {
                this.logger.Debug($"LyricsStore Json写入歌词跳过: {(item.FileName ?? item.Path)}");
                return false;
            }

            SaveLyricsFile(item, lyrics);
            document.LyricsFileName = MediaInfoDocument.GetLyricsFileName(item);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"LyricsStore Json写入歌词成功: {(item.FileName ?? item.Path)}");
            return true;
        }

        public void OverWriteToFile(BaseItem item)
        {
            var mediaInfoJsonPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var documents = ReadDocuments(mediaInfoJsonPath);
            var document = documents.FirstOrDefault() ?? new MediaInfoDocument();
            var lyrics = CreateForPersist(item);

            if (lyrics.Count == 0)
            {
                DeleteLyricsFile(item);
                document.LyricsFileName = null;
                SaveDocuments(documents, document, mediaInfoJsonPath);
                this.logger.Debug($"LyricsStore 覆盖Json歌词写入跳过: {(item.FileName ?? item.Path)} 无内嵌歌词");
                return;
            }

            SaveLyricsFile(item, lyrics);
            document.LyricsFileName = MediaInfoDocument.GetLyricsFileName(item);
            SaveDocuments(documents, document, mediaInfoJsonPath);
            this.logger.Debug($"LyricsStore 覆盖Json写入歌词成功: {(item.FileName ?? item.Path)}");
        }

        public MediaInfoDocument.MediaInfoRestoreResult ApplyToItem(BaseItem item)
        {
            if (item == null)
            {
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            var snapshots = ReadFromFile(item);
            if (snapshots.Count == 0)
            {
                this.logger.Debug($"LyricsStore 恢复歌词失败: {(item.FileName ?? item.Path)} sidecar 中无歌词");
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }

            var streams = item.GetMediaStreams().ToList();
            if (streams.Any(IsEmbeddedLyricsStream))
            {
                this.logger.Debug($"LyricsStore 恢复歌词跳过: {(item.FileName ?? item.Path)} 已存在歌词流");
                return MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists;
            }

            try
            {
                var nextIndex = streams.Count == 0 ? 0 : streams.Max(i => i.Index) + 1;
                foreach (var snapshot in snapshots)
                {
                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Lyrics" : snapshot.Title,
                        Codec = string.IsNullOrWhiteSpace(snapshot.Codec) ? "Lrc" : snapshot.Codec,
                        Language = snapshot.Language,
                        Extradata = snapshot.Content,
                        Index = nextIndex++
                    });
                }

                this.itemRepository.SaveMediaStreams(item.InternalId, streams, System.Threading.CancellationToken.None);
                item.UpdateToRepository(ItemUpdateType.MetadataImport);
                this.logger.Debug($"LyricsStore 恢复歌词完成: {(item.FileName ?? item.Path)}");
                return MediaInfoDocument.MediaInfoRestoreResult.Restored;
            }
            catch (Exception e)
            {
                this.logger.Error($"LyricsStore 恢复歌词失败: {(item.FileName ?? item.Path)}");
                this.logger.Error(e.Message);
                this.logger.Debug(e.StackTrace);
                return MediaInfoDocument.MediaInfoRestoreResult.Failed;
            }
        }

        private List<EmbeddedLyricsSnapshot> CreateForPersist(BaseItem item)
        {
            return item.GetMediaStreams()
                .Where(IsEmbeddedLyricsStream)
                .Select(stream => new EmbeddedLyricsSnapshot
                {
                    Title = stream.Title,
                    Language = stream.Language,
                    Codec = stream.Codec,
                    Content = stream.Extradata
                })
                .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Content))
                .ToList();
        }

        private static bool IsEmbeddedLyricsStream(MediaStream stream)
        {
            if (stream == null || stream.Type != MediaStreamType.Subtitle)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(stream.Extradata))
            {
                return string.Equals(stream.Title, "Lyrics", StringComparison.OrdinalIgnoreCase) ||
                       (stream.Codec?.IndexOf("lrc", StringComparison.OrdinalIgnoreCase) >= 0) ||
                       (stream.Codec?.IndexOf("lyric", StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return false;
        }

        private void SaveLyricsFile(BaseItem item, List<EmbeddedLyricsSnapshot> lyrics)
        {
            var lyricsPath = MediaInfoDocument.GetLyricsJsonPath(item);
            Directory.CreateDirectory(Path.GetDirectoryName(lyricsPath));
            this.jsonSerializer.SerializeToFile(lyrics, lyricsPath);
        }

        private void DeleteLyricsFile(BaseItem item)
        {
            var lyricsPath = MediaInfoDocument.GetLyricsJsonPath(item);
            if (this.fileSystem.FileExists(lyricsPath))
            {
                this.fileSystem.DeleteFile(lyricsPath);
                return;
            }

            if (File.Exists(lyricsPath))
            {
                File.Delete(lyricsPath);
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
    }
}
