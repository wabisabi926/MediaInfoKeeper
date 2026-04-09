using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class DownloadDanmuRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public DownloadDanmuRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems)
        {
            _expandToTargetItems = expandToTargetItems;
        }

        public async Task<MediaInfoMenuResponse> HandleAsync(DownloadDanmuRequest request)
        {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "no items";
                return response;
            }

            if (Plugin.Instance.Options.MainPage?.PlugginEnabled != true)
            {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "plugin disabled";
                return response;
            }

            if (Plugin.DanmuService?.IsEnabled != true)
            {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "danmu disabled";
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids)
                .Where(item => item is Episode || item is Movie)
                .ToList();
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "no supported items";
                return response;
            }

            foreach (var item in targetItems)
            {
                response.Processed++;
                try
                {
                    var result = await Plugin.DanmuService
                        .TryDownloadDanmuXmlAsync(item, CancellationToken.None, overwriteExisting: true)
                        .ConfigureAwait(false);
                    if (result)
                    {
                        response.Succeeded++;
                    }
                    else
                    {
                        response.Skipped++;
                    }
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Info($"弹幕下载: 失败 {item.FileName} {ex.Message}");
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = "ok";
            Plugin.Instance.Logger.Info(
                $"弹幕下载 result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }
    }
}
