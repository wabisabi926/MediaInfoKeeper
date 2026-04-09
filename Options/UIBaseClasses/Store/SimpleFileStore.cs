namespace MediaInfoKeeper.Options.UIBaseClasses.Store
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using Emby.Web.GenericEdit;
    using MediaBrowser.Common;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Serialization;

    internal class SimpleFileStore<TOptionType> : SimpleContentStore<TOptionType>
        where TOptionType : EditableOptionsBase, new()
    {
        private static readonly HashSet<string> NonPersistentPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "EditorTitle",
            "EditorDescription",
            "FeatureRequiresPremiere",
            "IsNewItem",
            "LibraryList",
            "IsRemoteOverrideList",
            "SubsequentMarkerModeList",
            "SearchItemTypeList",
            "HidePersonOptionList",
            "FallbackLanguageList",
            "TvdbFallbackLanguageList",
            "UpdateChannelList",
            "ProjectUrl",
            "CurrentVersion",
            "LatestReleaseVersion",
            "ReleaseHistoryBody",
            "DebugMediaInfoUrl"
        };

        private readonly ILogger logger;
        private readonly string pluginFullName;
        private readonly object lockObj = new object();
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;
        private readonly string pluginconfigPath;
        private TOptionType options;

        public SimpleFileStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
        {
            this.logger = logger;
            this.pluginFullName = pluginFullName;
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.fileSystem = applicationHost.Resolve<IFileSystem>();

            var applicationPaths = applicationHost.Resolve<IApplicationPaths>();
            this.pluginconfigPath = applicationPaths.PluginConfigurationsPath;

            if (!this.fileSystem.DirectoryExists(this.pluginconfigPath))
            {
                this.fileSystem.CreateDirectory(this.pluginconfigPath);
            }

            this.OptionsFileName = string.Format("{0}.json", pluginFullName);
        }

        public event EventHandler<FileSavingEventArgs> FileSaving;

        public event EventHandler<FileSavedEventArgs> FileSaved;

        public virtual string OptionsFileName { get; }

        public string OptionsFilePath => Path.Combine(this.pluginconfigPath, this.OptionsFileName);

        public override TOptionType GetOptions()
        {
            lock (this.lockObj)
            {
                if (this.options == null)
                {
                    return this.ReloadOptions();
                }

                return this.options;
            }
        }

        public TOptionType LoadOptionsFromDisk()
        {
            lock (this.lockObj)
            {
                return this.LoadOptionsFromDiskCore() ?? new TOptionType();
            }
        }

        public TOptionType ReloadOptions()
        {
            lock (this.lockObj)
            {
                this.options = this.LoadOptionsFromDiskCore() ?? new TOptionType();
                return this.options ?? new TOptionType();
            }
        }

        private TOptionType LoadOptionsFromDiskCore()
        {
            var tempOptions = new TOptionType();

            try
            {
                if (!this.fileSystem.FileExists(this.OptionsFilePath))
                {
                    return tempOptions;
                }

                using (var stream = this.fileSystem.OpenRead(this.OptionsFilePath))
                {
                    var deserialized = tempOptions.DeserializeFromJsonStream(stream, this.jsonSerializer);
                    return deserialized as TOptionType ?? tempOptions;
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Error loading plugin options for {0} from {1}", ex, this.pluginFullName, this.OptionsFilePath);
                return tempOptions;
            }
        }

        public override void SetOptions(TOptionType newOptions)
        {
            SetOptionsInternal(newOptions, raiseEvents: true);
        }

        protected void SetOptionsSilently(TOptionType newOptions)
        {
            SetOptionsInternal(newOptions, raiseEvents: false);
        }

        private void SetOptionsInternal(TOptionType newOptions, bool raiseEvents)
        {
            if (newOptions == null)
            {
                throw new ArgumentNullException(nameof(newOptions));
            }

            if (raiseEvents)
            {
                var savingArgs = new FileSavingEventArgs(newOptions);
                this.FileSaving?.Invoke(this, savingArgs);

                if (savingArgs.Cancel)
                {
                    return;
                }
            }

            lock (this.lockObj)
            {
                using (var stream = this.fileSystem.GetFileStream(this.OptionsFilePath, FileOpenMode.Create, FileAccessMode.Write))
                {
                    WriteSanitizedOptions(stream, newOptions);
                }
            }

            lock (this.lockObj)
            {
                this.options = newOptions;
            }

            if (raiseEvents)
            {
                var savedArgs = new FileSavedEventArgs(newOptions);
                this.FileSaved?.Invoke(this, savedArgs);
            }
        }

        private void WriteSanitizedOptions(Stream destination, TOptionType options)
        {
            using var buffer = new MemoryStream();
            this.jsonSerializer.SerializeToStream(
                options,
                buffer,
                new MediaBrowser.Model.Serialization.JsonSerializerOptions { Indent = true });

            buffer.Position = 0;
            JsonNode rootNode = null;
            try
            {
                rootNode = JsonNode.Parse(buffer);
            }
            catch (Exception ex)
            {
                this.logger.Warn("无法解析配置 JSON，回退为原始序列化结果：{0}", ex.Message);
            }

            if (rootNode == null)
            {
                buffer.Position = 0;
                buffer.CopyTo(destination);
                return;
            }

            SanitizeJsonNode(rootNode);
            using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
            rootNode.WriteTo(writer);
            writer.Flush();
        }

        private static void SanitizeJsonNode(JsonNode node)
        {
            if (node is JsonObject jsonObject)
            {
                var propertyNames = new List<string>();
                foreach (var property in jsonObject)
                {
                    propertyNames.Add(property.Key);
                }

                foreach (var propertyName in propertyNames)
                {
                    if (NonPersistentPropertyNames.Contains(propertyName))
                    {
                        jsonObject.Remove(propertyName);
                        continue;
                    }

                    var childNode = jsonObject[propertyName];
                    if (childNode != null)
                    {
                        SanitizeJsonNode(childNode);
                    }
                }

                return;
            }

            if (node is JsonArray jsonArray)
            {
                foreach (var childNode in jsonArray)
                {
                    if (childNode != null)
                    {
                        SanitizeJsonNode(childNode);
                    }
                }
            }
        }
    }
}
