define(['connectionManager', 'globalize', 'loading', 'toast', 'confirm'], function (connectionManager, globalize, loading, toast, confirm) {
    const commandSourceState = {
        registered: false
    };

    function getSupportedItems(options) {
        const items = (options && options.items) ? options.items.filter(item => !!item) : [];
        if (!items.length) {
            return [];
        }

        const user = options && options.user;
        const users = options && options.users ? Object.values(options.users) : [];
        const firstUser = users.length ? users[0] : null;
        const hasAdminInfo = (user && user.Policy) || (firstUser && firstUser.Policy);
        const isAdmin = (user && user.Policy && user.Policy.IsAdministrator) ||
            (firstUser && firstUser.Policy && firstUser.Policy.IsAdministrator);

        if (hasAdminInfo && !isAdmin) {
            return [];
        }

        const supportedTypes = { Movie: true, Episode: true, Season: true, Series: true, Video: true };
        if (!items.every(item => supportedTypes[item.Type])) {
            return [];
        }

        return items;
    }

    function getCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '提取媒体信息'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '提取媒體信息' : 'Extract MediaInfo');
    }

    function getDeleteCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '删除媒体信息及片头'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '刪除媒體信息及片頭' : 'Delete MediaInfo + Persist');
    }

    function getScanIntroCommandName() {
        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        return locale === 'zh-cn'
            ? '扫描片头'
            : (['zh-hk', 'zh-tw'].includes(locale) ? '掃描片頭' : 'Scan Intro');
    }

    function getResultMessage(result, action) {
        const normalized = normalizeResult(result);
        const isDelete = action === 'delete';
        const isScanIntro = action === 'scan_intro';
        if (!result) {
            return (isDelete ? getDeleteCommandName() : (isScanIntro ? getScanIntroCommandName() : getCommandName())) + ' finished';
        }

        const locale = (globalize.getCurrentLocale() || '').toLowerCase();
        if (!normalized.hasStats) {
            if (locale === 'zh-cn') {
                return (isDelete ? '删除完成' : (isScanIntro ? '扫描完成' : '提取完成')) + '（返回体无统计字段，请看日志）';
            }
            if (['zh-hk', 'zh-tw'].includes(locale)) {
                return (isDelete ? '刪除完成' : (isScanIntro ? '掃描完成' : '提取完成')) + '（返回體無統計字段，請看日誌）';
            }
            return 'Completed (no stats in response, check server logs)';
        }

        if (locale === 'zh-cn') {
            const prefix = isDelete ? '删除完成' : (isScanIntro ? '扫描完成' : '提取完成');
            return prefix + `：成功 ${normalized.succeeded}，失败 ${normalized.failed}，跳过 ${normalized.skipped}`;
        }

        if (['zh-hk', 'zh-tw'].includes(locale)) {
            const prefix = isDelete ? '刪除完成' : (isScanIntro ? '掃描完成' : '提取完成');
            return prefix + `：成功 ${normalized.succeeded}，失敗 ${normalized.failed}，跳過 ${normalized.skipped}`;
        }

        return `Completed: success ${normalized.succeeded}, failed ${normalized.failed}, skipped ${normalized.skipped}`;
    }

    function tryParseJson(value) {
        if (typeof value !== 'string') {
            return null;
        }
        const normalized = value.replace(/^\uFEFF/, '').trim();
        if (!normalized) {
            return null;
        }
        try {
            return JSON.parse(normalized);
        } catch (_) {
            return null;
        }
    }

    function extractPayload(result) {
        if (result == null) {
            return {};
        }

        if (Array.isArray(result)) {
            if (!result.length) {
                return {};
            }
            for (const item of result) {
                const extractedItem = extractPayload(item);
                if (extractedItem && (
                    extractedItem.Succeeded != null || extractedItem.succeeded != null ||
                    extractedItem.Failed != null || extractedItem.failed != null ||
                    extractedItem.Skipped != null || extractedItem.skipped != null)) {
                    return extractedItem;
                }
            }
            return extractPayload(result[0]);
        }

        if (typeof result === 'string') {
            return tryParseJson(result) || {};
        }

        if (typeof result !== 'object') {
            return {};
        }

        if (result.Succeeded != null || result.succeeded != null ||
            result.Failed != null || result.failed != null ||
            result.Skipped != null || result.skipped != null) {
            return result;
        }

        const nestedCandidates = [
            result.responseJSON,
            result.data,
            result.response,
            result.result,
            result.body,
            result.content,
            result.Content,
            result.value
        ];

        for (const candidate of nestedCandidates) {
            if (candidate == null) {
                continue;
            }
            const extracted = extractPayload(candidate);
            if (extracted && (
                extracted.Succeeded != null || extracted.succeeded != null ||
                extracted.Failed != null || extracted.failed != null ||
                extracted.Skipped != null || extracted.skipped != null)) {
                return extracted;
            }
        }

        const textCandidates = [result.responseText, result.text, result.Text];
        for (const text of textCandidates) {
            const parsed = tryParseJson(text);
            if (parsed) {
                return extractPayload(parsed);
            }
        }

        return result;
    }

    function normalizeResult(result) {
        const payload = extractPayload(result);
        const succeededRaw = payload.Succeeded ?? payload.succeeded ?? payload.Success ?? payload.success ?? 0;
        const failedRaw = payload.Failed ?? payload.failed ?? payload.Error ?? payload.error ?? 0;
        const skippedRaw = payload.Skipped ?? payload.skipped ?? 0;
        const hasStats =
            payload.Succeeded != null || payload.succeeded != null ||
            payload.Success != null || payload.success != null ||
            payload.Failed != null || payload.failed != null ||
            payload.Skipped != null || payload.skipped != null;
        return {
            succeeded: Number.isFinite(Number(succeededRaw)) ? Number(succeededRaw) : 0,
            failed: Number.isFinite(Number(failedRaw)) ? Number(failedRaw) : 0,
            skipped: Number.isFinite(Number(skippedRaw)) ? Number(skippedRaw) : 0,
            hasStats: hasStats
        };
    }

    function getErrorMessage(action, err) {
        const isDelete = action === 'delete';
        const isScanIntro = action === 'scan_intro';
        const commandName = isDelete ? getDeleteCommandName() : (isScanIntro ? getScanIntroCommandName() : getCommandName());
        const detail = (err && (err.message || err.statusText || err.responseText)) ? ` (${err.message || err.statusText || err.responseText})` : '';
        return commandName + ' failed' + detail;
    }

    function postJson(apiClient, endpoint, body) {
        const url = apiClient.getUrl(endpoint);
        return apiClient.ajax({
            type: 'POST',
            url: url,
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            return result || {};
        });
    }

    const api = {
        extractMediaInfo: function (ids) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getCommandName();
            return confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: commandName,
                primary: 'cancel'
            }).then(function () {
                loading.show();
                const apiClient = connectionManager.currentApiClient();
                return postJson(apiClient, 'MediaInfoKeeper/Items/ExtractMediaInfo', { Ids: ids }).then(function (result) {
                    toast(getResultMessage(result, 'extract'));
                }).catch(function (err) {
                    toast(getErrorMessage('extract', err));
                }).finally(function () {
                    loading.hide();
                });
            });
        },

        deleteMediaInfoPersist: function (ids) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getDeleteCommandName();
            return confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: globalize.translate('Delete'),
                primary: 'cancel'
            }).then(function () {
                loading.show();
                const apiClient = connectionManager.currentApiClient();
                return postJson(apiClient, 'MediaInfoKeeper/Items/DeleteMediaInfoPersist', { Ids: ids }).then(function (result) {
                    toast(getResultMessage(result, 'delete'));
                }).catch(function (err) {
                    toast(getErrorMessage('delete', err));
                }).finally(function () {
                    loading.hide();
                });
            });
        },

        scanIntro: function (ids) {
            if (!ids || !ids.length) {
                return Promise.resolve();
            }

            const commandName = getScanIntroCommandName();
            return confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: commandName,
                primary: 'cancel'
            }).then(function () {
                loading.show();
                const apiClient = connectionManager.currentApiClient();
                return postJson(apiClient, 'MediaInfoKeeper/Items/ScanIntro', { Ids: ids }).then(function (result) {
                    toast(getResultMessage(result, 'scan_intro'));
                }).catch(function (err) {
                    toast(getErrorMessage('scan_intro', err));
                }).finally(function () {
                    loading.hide();
                });
            });
        }
    };

    function buildCommandSource() {
        return {
            getCommands: function (options) {
                const items = getSupportedItems(options);
                if (!items.length) {
                    return [];
                }

                const commands = [];
                commands.push({ name: getCommandName(), id: 'extract_media_info', icon: '4k' });

                const introSupportedTypes = { Episode: true, Season: true, Series: true };
                if (items.every(item => introSupportedTypes[item.Type])) {
                    commands.push({ name: getScanIntroCommandName(), id: 'scan_intro', icon: 'graphic_eq' });
                }

                commands.push({ name: getDeleteCommandName(), id: 'delete_media_info_persist', icon: 'delete_forever' });
                return commands;
            },
            executeCommand: function (command, items) {
                if (!items || !items.length) {
                    return;
                }

                const ids = items.map(item => item && item.Id).filter(Boolean);
                if (!ids.length) {
                    return;
                }

                if (command === 'extract_media_info') {
                    return api.extractMediaInfo(ids);
                }

                if (command === 'delete_media_info_persist') {
                    return api.deleteMediaInfoPersist(ids);
                }

                if (command === 'scan_intro') {
                    return api.scanIntro(ids);
                }
            }
        };
    }

    (function registerCommandSource(attempt) {
        const maxAttempts = 20;
        Emby.importModule('./modules/common/itemmanager/itemmanager.js')
            .then(itemmanager => {
                if (!itemmanager || typeof itemmanager.registerCommandSource !== 'function') {
                    throw new Error('itemmanager unavailable');
                }

                if (commandSourceState.registered) {
                    return;
                }

                itemmanager.registerCommandSource(buildCommandSource());
                commandSourceState.registered = true;
            })
            .catch(() => {
                if (attempt < maxAttempts) {
                    setTimeout(() => registerCommandSource(attempt + 1), 150);
                }
            });
    })(0);

    return api;
});
