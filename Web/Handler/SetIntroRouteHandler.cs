using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaInfoKeeper.Services.IntroSkip;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class SetIntroRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;
        private readonly IItemRepository _itemRepository;

        public SetIntroRouteHandler(
            Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems,
            ILibraryManager libraryManager,
            IItemRepository itemRepository)
        {
            _expandToTargetItems = expandToTargetItems;
            _itemRepository = itemRepository;
        }

        public MediaInfoMenuResponse Handle(SetIntroRequest request)
        {
            var response = new MediaInfoMenuResponse();
            var creditsStartTicks = request?.CreditsStartTicks;

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "no items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids).OfType<Episode>().ToList();
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "no supported items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            if (request.IntroStartTicks >= request.IntroEndTicks)
            {
                response.Message = "invalid intro range";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            if (creditsStartTicks.HasValue && creditsStartTicks.Value <= request.IntroEndTicks)
            {
                response.Message = "invalid credits range";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            foreach (var episode in targetItems)
            {
                response.Processed++;
                try
                {
                    var chapters = _itemRepository.GetChapters(episode);

                    IntroSkipChapterApi.ReplaceIntroMarkers(chapters, request.IntroStartTicks, request.IntroEndTicks);

                    if (creditsStartTicks.HasValue)
                    {
                        IntroSkipChapterApi.ReplaceCreditsMarker(chapters, creditsStartTicks.Value);
                    }

                    Patch.IntroMarkerProtect.SaveChapters(
                        _itemRepository,
                        episode,
                        chapters,
                        creditsStartTicks.HasValue
                            ? new[]
                            {
                                MediaBrowser.Model.Entities.MarkerType.IntroStart,
                                MediaBrowser.Model.Entities.MarkerType.IntroEnd,
                                MediaBrowser.Model.Entities.MarkerType.CreditsStart
                            }
                            : new[]
                            {
                                MediaBrowser.Model.Entities.MarkerType.IntroStart,
                                MediaBrowser.Model.Entities.MarkerType.IntroEnd
                            });

                    try
                    {
                        // 持久化片头片尾信息
                        Plugin.ChaptersStore.OverWriteToFile(episode);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance.Logger.Error($"ShortcutMenu 设置片头片尾后写入JSON失败: {episode.Path ?? episode.Name}");
                        Plugin.Instance.Logger.Error(ex.Message);
                        Plugin.Instance.Logger.Debug(ex.StackTrace);
                    }

                    response.Succeeded++;
                    Plugin.Instance.Logger.Info($"ShortcutMenu 设置片头片尾成功: {episode.Path ?? episode.Name}, IntroStart={request.IntroStartTicks}, IntroEnd={request.IntroEndTicks}, CreditsStart={(creditsStartTicks.HasValue ? creditsStartTicks.Value.ToString() : "unchanged")}");
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单设置片头片尾失败: {episode.Path ?? episode.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = response.Succeeded > 0 ? "set intro/credits succeeded" : "no intro/credits set";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }
    }
}
