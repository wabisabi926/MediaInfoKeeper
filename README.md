MediaInfoKeeper
===============

<p align="center">
  <img src="Resources/ThumbImage.png" alt="MediaInfoKeeper" width="320" />
</p>


功能
----------

- MediaInfo Keeper：将 MediaInfo/章节持久化为 JSON，入库恢复/提取并在刷新后恢复。
- IntroSkip：解锁 .strm 片头检测、入库扫描片头、播放行为打标，并保护片头/片尾标记。
- Search：中文模糊与拼音搜索增强，可设置搜索范围。
- MetaData：支持剧集元数据变动监听、TMDB/TVDB 使用中文别名，避免出现英文标题的情况。
- Proxy：全局 HttpClient 代理，支持忽略证书、写入代理环境变量，并支持 TMDB 反代域名替换。
- GitHub & Update：版本检查与插件自更新。

计划任务
--------

- 刷新媒体元数据：全局媒体库范围内，按“最近入库时间窗口（天）”筛选（0=不限制）刷新元数据（可选覆盖或补全），刷新后从 JSON 恢复媒体信息。
- 扫描片头：全局媒体库范围内，按入库时间倒序取最近 N 条（“最近入库媒体筛选数量”）的剧集执行片头检测。
- 提取媒体信息（最近入库）：计划任务媒体库范围内，按入库时间倒序取最近 N 条（“最近入库媒体筛选数量”）恢复/提取媒体信息并写入 JSON（已存在则恢复）。
- 恢复媒体信息：计划任务范围内条目，存在 JSON 则恢复，不存在则跳过。
- 备份媒体信息：计划任务范围内已存在 MediaInfo 的条目导出 JSON，无 MediaInfo 则跳过。
- 更新插件：更新插件至最新版本。

安装
----

- 已测试版本：`4.9.3.0` `4.9.1.90`
- 不支持：`4.8` 系列

1. 下载 `MediaInfoKeeper.dll`：<https://github.com/honue/MediaInfoKeeper/releases>
2. 放入 Emby 配置目录中的 `plugins` 目录。
3. 重启 Emby，在插件页面完成配置。

感谢
----

<https://github.com/sjtuross/StrmAssistant>

<https://github.com/xinjiawei/StrmAssistant_less>
