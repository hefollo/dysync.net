namespace dy.net.model.dto
{
    /// <summary>
    /// 「我的抖音」信息流查询条件
    /// </summary>
    public class DouyinFeedRequestDto
    {
        /// <summary>
        /// 分类：favorite / collect / follow / mix / series，为空表示全部
        /// </summary>
        public string Type { get; set; }

        public string CookieId { get; set; }

        public string AuthorId { get; set; }

        /// <summary>
        /// 按标题或作者搜索
        /// </summary>
        public string Keyword { get; set; }

        /// <summary>
        /// 排序：latest（同步时间，默认）/ publish（发布时间）/ random（随机）
        /// </summary>
        public string Order { get; set; }

        /// <summary>
        /// 随机排序的种子。同一个种子翻页时顺序不变，换种子就是换一批
        /// </summary>
        public int Seed { get; set; }

        public int PageIndex { get; set; } = 1;

        public int PageSize { get; set; } = 24;
    }

    /// <summary>
    /// 信息流里的一条视频。只包含展示需要的字段，不暴露服务器上的文件路径
    /// </summary>
    public class DouyinFeedItemDto
    {
        public string Id { get; set; }

        public string Title { get; set; }

        public string Author { get; set; }

        public string AuthorId { get; set; }

        /// <summary>
        /// 分类，取值同 <see cref="DouyinFeedRequestDto.Type"/>
        /// </summary>
        public string Type { get; set; }

        public string TypeName { get; set; }

        /// <summary>
        /// 作者发布时间
        /// </summary>
        public DateTime CreateTime { get; set; }

        public DateTime SyncTime { get; set; }

        public string Resolution { get; set; }

        public long FileSize { get; set; }

        /// <summary>
        /// 是否由图文合成的视频
        /// </summary>
        public bool IsImagePost { get; set; }
    }

    /// <summary>
    /// 博主列表查询条件
    /// </summary>
    public class DouyinFeedAuthorRequestDto
    {
        public string CookieId { get; set; }

        /// <summary>
        /// 按博主名称搜索
        /// </summary>
        public string Keyword { get; set; }

        /// <summary>
        /// 排序：count（视频数，默认）/ latest（最近同步）/ name（名称）
        /// </summary>
        public string Order { get; set; }

        public int PageIndex { get; set; } = 1;

        public int PageSize { get; set; } = 36;
    }

    /// <summary>
    /// 博主列表里的一位博主
    /// </summary>
    public class DouyinFeedAuthorDto
    {
        public string AuthorId { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// 本地可播放的视频数
        /// </summary>
        public int Count { get; set; }

        public DateTime LatestSyncTime { get; set; }

        /// <summary>
        /// 该博主最近同步的一条视频，前端用它的头像和封面代表这位博主
        /// </summary>
        public string SampleVideoId { get; set; }
    }

    public class DouyinFeedAccountDto
    {
        public string Id { get; set; }

        public string Name { get; set; }
    }

    /// <summary>
    /// 「我的抖音」页头部的统计
    /// </summary>
    public class DouyinFeedSummaryDto
    {
        public List<DouyinFeedAccountDto> Accounts { get; set; } = new List<DouyinFeedAccountDto>();

        public int Total { get; set; }

        public int Favorite { get; set; }

        public int Collect { get; set; }

        public int Follow { get; set; }

        public int Mix { get; set; }

        public int Series { get; set; }

        /// <summary>
        /// 已同步视频涉及的作者数
        /// </summary>
        public int Authors { get; set; }
    }

    /// <summary>
    /// 信息流分类与 <see cref="VideoTypeEnum"/> 的对应关系
    /// </summary>
    public static class DouyinFeedTypes
    {
        public const string Favorite = "favorite";
        public const string Collect = "collect";
        public const string Follow = "follow";
        public const string Mix = "mix";
        public const string Series = "series";

        /// <summary>
        /// 分类包含的视频类型；分类为空或未知时返回 null，表示不过滤
        /// </summary>
        public static VideoTypeEnum[] ToVideoTypes(string type)
        {
            switch ((type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case Favorite:
                    return new[] { VideoTypeEnum.dy_favorite };
                case Collect:
                    // 默认收藏夹和自定义收藏夹都算收藏
                    return new[] { VideoTypeEnum.dy_collects, VideoTypeEnum.dy_custom_collect };
                case Follow:
                    return new[] { VideoTypeEnum.dy_follows };
                case Mix:
                    return new[] { VideoTypeEnum.dy_mix };
                case Series:
                    return new[] { VideoTypeEnum.dy_series };
                default:
                    return null;
            }
        }

        public static string FromVideoType(VideoTypeEnum type)
        {
            switch (type)
            {
                case VideoTypeEnum.dy_favorite:
                    return Favorite;
                case VideoTypeEnum.dy_collects:
                case VideoTypeEnum.dy_custom_collect:
                    return Collect;
                case VideoTypeEnum.dy_follows:
                    return Follow;
                case VideoTypeEnum.dy_mix:
                    return Mix;
                case VideoTypeEnum.dy_series:
                    return Series;
                default:
                    return string.Empty;
            }
        }

        public static string GetName(string type)
        {
            switch (type)
            {
                case Favorite:
                    return "喜欢";
                case Collect:
                    return "收藏";
                case Follow:
                    return "关注";
                case Mix:
                    return "合集";
                case Series:
                    return "短剧";
                default:
                    return "视频";
            }
        }

        /// <summary>
        /// 随机排序：先按 id 排好保证起点一致，再用种子洗牌。
        /// 同一个种子多次调用结果相同，翻页时不会重复也不会漏。
        /// </summary>
        public static List<string> Shuffle(IEnumerable<string> ids, int seed)
        {
            var list = ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var random = new Random(seed);
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
        }
    }
}
