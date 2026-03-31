using System.Collections.Generic;
using Emby.Notifications;
using MediaBrowser.Controller;

namespace MediaInfoKeeper.Notification
{
    public class CustomNotifications : INotificationTypeFactory
    {
        public CustomNotifications(IServerApplicationHost appHost)
        {
        }

        public List<NotificationTypeInfo> GetNotificationTypes(string language)
        {
            var useSystemLibraryNew = Plugin.Instance?.Options?.Enhance?.TakeOverSystemLibraryNew == true;
            return new List<NotificationTypeInfo>
            {
                new NotificationTypeInfo
                {
                    Id = useSystemLibraryNew ? "library.new" : "favorites.update",
                    Name = "收藏剧集更新",
                    CategoryId = "mediainfo.keeper",
                    CategoryName = Plugin.PluginName
                },
                new NotificationTypeInfo
                {
                    Id = "deep.delete",
                    Name = "深度删除通知",
                    CategoryId = "mediainfo.keeper",
                    CategoryName = Plugin.PluginName
                },
                new NotificationTypeInfo
                {
                    Id = "introskip.update",
                    Name = "片头片尾打标更新",
                    CategoryId = "mediainfo.keeper",
                    CategoryName = Plugin.PluginName
                }
            };
        }
    }
}
