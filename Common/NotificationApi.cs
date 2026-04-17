using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Notifications;
using MediaInfoKeeper.Patch;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace MediaInfoKeeper.Common
{
    public sealed class NotificationApi
    {
        private readonly INotificationManager notificationManager;
        private readonly IUserManager userManager;
        private readonly ISessionManager sessionManager;

        public NotificationApi(
            INotificationManager notificationManager,
            IUserManager userManager,
            ISessionManager sessionManager)
        {
            this.notificationManager = notificationManager;
            this.userManager = userManager;
            this.sessionManager = sessionManager;
        }

        public void DeepDeleteSendNotification(BaseItem item, HashSet<string> mountPaths)
        {
            if (mountPaths == null || mountPaths.Count == 0)
            {
                return;
            }

            var paths = mountPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            var users = Plugin.LibraryService?.GetAllUsers() ?? Array.Empty<User>();
            foreach (var user in users)
            {
                var request = new NotificationRequest
                {
                    Title = Plugin.PluginName + " - 深度删除",
                    EventId = "deep.delete",
                    User = user,
                    Item = item,
                    Description = string.Format(
                        "Item Name:{0}{1}{0}{0}Item Path:{0}{2}{0}{0}Mount Paths:{0}{3}",
                        Environment.NewLine,
                        item?.Name ?? string.Empty,
                        item?.Path ?? string.Empty,
                        string.Join(Environment.NewLine, paths))
                };

                this.notificationManager.SendNotification(request);
            }
        }

        public int LibraryNewSendNotification(Series series, Episode episode, IReadOnlyCollection<User> users)
        {
            if (series == null || episode == null || users == null || users.Count == 0)
            {
                return 0;
            }

            var useSystemLibraryNew = Plugin.Instance?.Options?.Enhance?.TakeOverSystemLibraryNew == true;
            var eventId = useSystemLibraryNew ? "library.new" : "favorites.update";
            var sentCount = 0;
            using (useSystemLibraryNew ? NotificationSystem.BeginCustomLibraryNewScope() : null)
            {
                foreach (var user in users)
                {
                    var request = new NotificationRequest
                    {
                        Date = ConfiguredDateTime.NowOffset,
                        EventId = eventId,
                        User = user,
                        Item = episode,
                        Description = series.Name
                    };

                    this.notificationManager.SendNotification(request);
                    sentCount++;
                }
            }

            return sentCount;
        }

        public async Task IntroUpdateSendNotification(Episode episode, SessionInfo session, string introStartTime, string introEndTime)
        {
            if (episode == null || session == null)
            {
                return;
            }

            if (Plugin.Instance?.Options?.Enhance?.EnableNotificationEnhance != true)
            {
                return;
            }

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Plugin.PluginName,
                    Text = $"{episode.FindSeriesName()} - 片头标记已更新",
                    TimeoutMs = 500
                };
                await this.sessionManager
                    .SendMessageCommand(session.Id, session.Id, message, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var request = new NotificationRequest
            {
                Title = Plugin.PluginName,
                EventId = "introskip.update",
                User = this.userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description =
                    $"{episode.FindSeriesName()} - {episode.FindSeasonName()} - 片头标记已更新" +
                    $"{Environment.NewLine}{Environment.NewLine}片头开始: {introStartTime}" +
                    $"{Environment.NewLine}片头结束: {introEndTime}" +
                    $"{Environment.NewLine}{Environment.NewLine}by {session.UserName}"
            };

            this.notificationManager.SendNotification(request);
        }

        public async Task CreditsUpdateSendNotification(Episode episode, SessionInfo session, string creditsDuration)
        {
            if (episode == null || session == null)
            {
                return;
            }

            if (Plugin.Instance?.Options?.Enhance?.EnableNotificationEnhance != true)
            {
                return;
            }

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Plugin.PluginName,
                    Text = $"{episode.FindSeriesName()} - 片尾标记已更新",
                    TimeoutMs = 500
                };
                await this.sessionManager
                    .SendMessageCommand(session.Id, session.Id, message, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var request = new NotificationRequest
            {
                Title = Plugin.PluginName,
                EventId = "introskip.update",
                User = this.userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description =
                    $"{episode.FindSeriesName()} - {episode.FindSeasonName()} - 片尾标记已更新" +
                    $"{Environment.NewLine}{Environment.NewLine}片尾时长: {creditsDuration}" +
                    $"{Environment.NewLine}{Environment.NewLine}by {session.UserName}"
            };

            this.notificationManager.SendNotification(request);
        }

        private bool CanDisplayMessage(SessionInfo session)
        {
            return session?.SupportedCommands?.Contains("DisplayMessage") == true;
        }
    }
}
