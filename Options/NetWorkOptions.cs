using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;
using System;
using System.Collections.Generic;

namespace MediaInfoKeeper.Options
{
    public class NetWorkOptions : EditableOptionsBase
    {
        public override string EditorTitle => "NetWork";

        [DisplayName("启用代理")]
        [Description("开启后所有 HttpClient 请求将走代理。")]
        public bool EnableProxyServer { get; set; } = false;

        [DisplayName("代理服务器地址")]
        [Description("示例：http://user:pass@127.0.0.1:7890 或 socks5://127.0.0.1:1080")]
        public string ProxyServerUrl { get; set; } = "http://127.0.0.1:7890";

        [DisplayName("忽略证书验证")]
        [Description("开启后忽略代理或远端证书错误。")]
        public bool IgnoreCertificateValidation { get; set; } = false;

        [DisplayName("写入环境变量")]
        [Description("同步写入 http_proxy/https_proxy/HTTP_PROXY/HTTPS_PROXY，便于 ffprobe 等外部进程访问需要代理的资源。")]
        public bool WriteProxyEnvVars { get; set; } = true;

        [DisplayName("启用压缩传输")]
        [Description("允许元数据服务器返回 gzip/deflate/br 压缩内容，并自动解压以减少网络流量。")]
        public bool EnableGzip { get; set; } = true;

        [DisplayName("自定义 TMDB API 域名")]
        [Description("默认 api.tmdb.org，留空使用系统默认 api.themoviedb.org")]
        public string AlternativeTmdbApiUrl { get; set; } = string.Empty;

        [DisplayName("自定义 TMDB 图像域名")]
        [Description("留空使用系统默认 image.tmdb.org")]
        public string AlternativeTmdbImageUrl { get; set; } = string.Empty;

        [DisplayName("自定义 TMDB API 密钥")]
        [Description("请自备 API 密钥，留空使用Emby默认。")]
        public string AlternativeTmdbApiKey { get; set; } = string.Empty;

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0)
            {
                return container;
            }

            var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.EditorItems)
            {
                var key = item.Name ?? item.Id;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!itemLookup.ContainsKey(key))
                {
                    itemLookup.Add(key, item);
                }
            }

            var groupedItems = new List<EditorBase>();
            var groupIndex = 0;

            void AddGroup(string title, string description = null, params string[] propertyNames)
            {
                var items = new List<EditorBase>();
                foreach (var propertyName in propertyNames)
                {
                    if (itemLookup.TryGetValue(propertyName, out var item))
                    {
                        items.Add(item);
                        itemLookup.Remove(propertyName);
                    }
                }

                if (items.Count == 0)
                {
                    return;
                }

                groupIndex++;
                var group = new EditorGroup(title, items.ToArray(), $"group{groupIndex}", root.Id, null)
                {
                    Description = description
                };
                groupedItems.Add(group);
            }


            AddGroup("Proxy","代理服务器相关设置",
                nameof(EnableProxyServer),
                nameof(ProxyServerUrl),
                nameof(IgnoreCertificateValidation),
                nameof(WriteProxyEnvVars),
                nameof(EnableGzip));

            AddGroup("TMDB 替换","替换 TMDB 请求域名，自建反代可参考这个项目：https://github.com/imaliang/tmdb-proxy",
                nameof(AlternativeTmdbApiUrl),
                nameof(AlternativeTmdbImageUrl),
                nameof(AlternativeTmdbApiKey));

            var remaining = new List<EditorBase>();
            foreach (var item in root.EditorItems)
            {
                var key = item.Name ?? item.Id;
                if (!string.IsNullOrEmpty(key) && itemLookup.ContainsKey(key))
                {
                    remaining.Add(item);
                    itemLookup.Remove(key);
                }
            }

            if (remaining.Count > 0)
            {
                groupIndex++;
                groupedItems.Add(new EditorGroup("未分组", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            if (groupedItems.Count > 0)
            {
                root.EditorItems = groupedItems.ToArray();
            }

            return container;
        }
    }
}
