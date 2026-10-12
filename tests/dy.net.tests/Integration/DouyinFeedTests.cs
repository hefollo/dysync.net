using dy.net.model.dto;
using dy.net.model.entity;
using dy.net.repository;
using dy.net.service;
using Xunit;

namespace dy.net.tests.Integration;

public class DouyinFeedTests : SqliteTestBase
{
    private static readonly DateTime BaseTime = new DateTime(2026, 1, 1, 12, 0, 0);

    public DouyinFeedTests()
    {
        var videos = new List<DouyinVideo>();
        // 30 条收藏（v01..v30），同步时间递增
        for (var i = 1; i <= 30; i++)
            videos.Add(Make("v" + i.ToString("00"), VideoTypeEnum.dy_collects, i, "作者甲", "a1", "ck1"));

        videos.Add(Make("fav1", VideoTypeEnum.dy_favorite, 101, "作者乙", "a2", "ck1", "猫咪日常"));
        videos.Add(Make("fav2", VideoTypeEnum.dy_favorite, 102, "作者乙", "a2", "ck2"));
        videos.Add(Make("custom1", VideoTypeEnum.dy_custom_collect, 103, "作者丙", "a3", "ck1"));
        videos.Add(Make("follow1", VideoTypeEnum.dy_follows, 104, "猫咪博主", "a4", "ck1"));
        videos.Add(Make("mix1", VideoTypeEnum.dy_mix, 105, "作者丙", "a3", "ck1"));
        videos.Add(Make("series1", VideoTypeEnum.dy_series, 106, "作者丙", "a3", "ck1"));

        // 没有视频文件的记录不能出现在信息流里
        var imageOnly = Make("img1", VideoTypeEnum.dy_collects, 107, "作者甲", "a1", "ck1");
        imageOnly.VideoSavePath = "/app/collect/img1-poster.jpg";
        videos.Add(imageOnly);
        var noFile = Make("none1", VideoTypeEnum.dy_collects, 108, "作者甲", "a1", "ck1");
        noFile.VideoSavePath = null;
        videos.Add(noFile);

        Db.Insertable(videos).ExecuteCommand();
        Db.Insertable(new List<DouyinCookie>
        {
            new() { Id = "ck1", UserName = "账号一", Status = 1 },
            new() { Id = "ck2", UserName = "账号二", Status = 0 }
        }).ExecuteCommand();
    }

    private static DouyinVideo Make(
        string id, VideoTypeEnum type, int minutes, string author, string authorId, string cookieId, string title = null) => new()
    {
        Id = id,
        AwemeId = "aweme-" + id,
        VideoTitle = title ?? "标题" + id,
        Author = author,
        AuthorId = authorId,
        ViedoType = type,
        FileSize = 100,
        Resolution = "1080×1920",
        VideoSavePath = "/app/videos/" + id + ".mp4",
        SyncTime = BaseTime.AddMinutes(minutes),
        // 发布时间与同步时间顺序相反，用来区分两种排序
        CreateTime = BaseTime.AddMinutes(-minutes),
        CookieId = cookieId
    };

    private DouyinVideoService CreateService()
    {
        return new DouyinVideoService(
            new DouyinVideoRepository(Db),
            new DouyinCookieRepository(Db),
            Db,
            new DouyinVideoStatisticsService(Db));
    }

    [Fact]
    public async Task Feed_ExcludesRecordsWithoutVideoFile()
    {
        var (list, total) = await CreateService().GetFeedAsync(new DouyinFeedRequestDto { PageSize = 60 });

        Assert.Equal(36, total);
        Assert.Equal(36, list.Count);
        Assert.DoesNotContain(list, x => x.Id == "img1" || x.Id == "none1");
    }

    [Fact]
    public async Task Feed_DefaultOrderIsLatestSyncedFirst()
    {
        var (list, _) = await CreateService().GetFeedAsync(new DouyinFeedRequestDto { PageSize = 3 });

        Assert.Equal(new[] { "series1", "mix1", "follow1" }, list.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Feed_PublishOrderUsesCreateTime()
    {
        var (list, _) = await CreateService().GetFeedAsync(new DouyinFeedRequestDto { PageSize = 2, Order = "publish" });

        Assert.Equal(new[] { "v01", "v02" }, list.Select(x => x.Id).ToArray());
    }

    [Theory]
    [InlineData("favorite", 2)]
    [InlineData("collect", 31)]
    [InlineData("follow", 1)]
    [InlineData("mix", 1)]
    [InlineData("series", 1)]
    [InlineData("", 36)]
    [InlineData("unknown", 36)]
    public async Task Feed_FiltersByType(string type, int expected)
    {
        var (list, total) = await CreateService().GetFeedAsync(new DouyinFeedRequestDto { Type = type, PageSize = 60 });

        Assert.Equal(expected, total);
        Assert.Equal(expected, list.Count);
        if (expected < 36)
            Assert.All(list, x => Assert.Equal(type, x.Type));
    }

    [Fact]
    public async Task Feed_CollectIncludesCustomCollectFolders()
    {
        var (list, _) = await CreateService().GetFeedAsync(new DouyinFeedRequestDto { Type = "collect", PageSize = 60 });

        Assert.Contains(list, x => x.Id == "custom1" && x.TypeName == "收藏");
    }

    [Fact]
    public async Task Feed_FiltersByAccountAuthorAndKeyword()
    {
        var service = CreateService();

        var (byAccount, _) = await service.GetFeedAsync(new DouyinFeedRequestDto { CookieId = "ck2", PageSize = 60 });
        var (byAuthor, _) = await service.GetFeedAsync(new DouyinFeedRequestDto { AuthorId = "a3", PageSize = 60 });
        var (byKeyword, _) = await service.GetFeedAsync(new DouyinFeedRequestDto { Keyword = " 猫咪 ", PageSize = 60 });

        Assert.Equal(new[] { "fav2" }, byAccount.Select(x => x.Id).ToArray());
        Assert.Equal(new[] { "custom1", "mix1", "series1" }, byAuthor.Select(x => x.Id).OrderBy(x => x).ToArray());
        // 标题和作者名都参与搜索
        Assert.Equal(new[] { "fav1", "follow1" }, byKeyword.Select(x => x.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Feed_RandomOrderIsStableAcrossPagesForSameSeed()
    {
        var service = CreateService();
        var seen = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            var (list, total) = await service.GetFeedAsync(
                new DouyinFeedRequestDto { Order = "random", Seed = 42, PageIndex = page, PageSize = 10 });
            Assert.Equal(36, total);
            seen.AddRange(list.Select(x => x.Id));
        }

        // 翻完所有页：不重复、不遗漏
        Assert.Equal(36, seen.Count);
        Assert.Equal(36, seen.Distinct().Count());

        var (again, _) = await service.GetFeedAsync(
            new DouyinFeedRequestDto { Order = "random", Seed = 42, PageIndex = 1, PageSize = 10 });
        Assert.Equal(seen.Take(10).ToArray(), again.Select(x => x.Id).ToArray());

        var (otherSeed, _) = await service.GetFeedAsync(
            new DouyinFeedRequestDto { Order = "random", Seed = 43, PageIndex = 1, PageSize = 36 });
        Assert.NotEqual(seen.ToArray(), otherSeed.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Feed_ClampsPagingArguments()
    {
        var request = new DouyinFeedRequestDto { PageIndex = 0, PageSize = 1000 };

        var (list, _) = await CreateService().GetFeedAsync(request);

        Assert.Equal(1, request.PageIndex);
        Assert.Equal(60, request.PageSize);
        Assert.Equal(36, list.Count);
    }

    [Fact]
    public async Task Feed_DoesNotExposeFilePaths()
    {
        var properties = typeof(DouyinFeedItemDto).GetProperties().Select(x => x.Name).ToArray();

        Assert.DoesNotContain(properties, x => x.Contains("Path") || x.Contains("Url"));
    }

    [Fact]
    public async Task Summary_CountsPlayableVideosPerType()
    {
        var summary = await CreateService().GetFeedSummaryAsync(null);

        Assert.Equal(36, summary.Total);
        Assert.Equal(2, summary.Favorite);
        Assert.Equal(31, summary.Collect);
        Assert.Equal(1, summary.Follow);
        Assert.Equal(1, summary.Mix);
        Assert.Equal(1, summary.Series);
        Assert.Equal(4, summary.Authors);
        var accounts = summary.Accounts.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "ck1", "ck2" }, accounts.Select(x => x.Id).ToArray());
        Assert.Equal(new[] { "账号一", "账号二" }, accounts.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Summary_FiltersByAccount()
    {
        var summary = await CreateService().GetFeedSummaryAsync("ck2");

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Favorite);
        Assert.Equal(0, summary.Collect);
        Assert.Equal(1, summary.Authors);
    }

    [Fact]
    public async Task Summary_FiltersByAuthor()
    {
        var summary = await CreateService().GetFeedSummaryAsync(null, "a3");

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Collect);
        Assert.Equal(1, summary.Mix);
        Assert.Equal(1, summary.Series);
        Assert.Equal(0, summary.Favorite);
    }

    [Fact]
    public async Task Authors_GroupPlayableVideosAndSortByCount()
    {
        var (list, total) = await CreateService().GetFeedAuthorsAsync(new DouyinFeedAuthorRequestDto());

        Assert.Equal(4, total);
        Assert.Equal(new[] { "a1", "a3", "a2", "a4" }, list.Select(x => x.AuthorId).ToArray());
        // a1 另有两条没有视频文件的记录，不计入
        Assert.Equal(new[] { 30, 3, 2, 1 }, list.Select(x => x.Count).ToArray());
        Assert.Equal("作者甲", list[0].Name);
        Assert.Equal("v30", list[0].SampleVideoId);
    }

    [Fact]
    public async Task Authors_SortByLatestSync()
    {
        var (list, _) = await CreateService().GetFeedAuthorsAsync(new DouyinFeedAuthorRequestDto { Order = "latest" });

        Assert.Equal(new[] { "a3", "a4", "a2", "a1" }, list.Select(x => x.AuthorId).ToArray());
        Assert.Equal(BaseTime.AddMinutes(106), list[0].LatestSyncTime);
    }

    [Fact]
    public async Task Authors_FilterByKeywordAndAccount()
    {
        var service = CreateService();

        var (byKeyword, keywordTotal) = await service.GetFeedAuthorsAsync(new DouyinFeedAuthorRequestDto { Keyword = " 猫咪 " });
        var (byAccount, accountTotal) = await service.GetFeedAuthorsAsync(new DouyinFeedAuthorRequestDto { CookieId = "ck2" });

        Assert.Equal(1, keywordTotal);
        Assert.Equal("a4", byKeyword.Single().AuthorId);
        Assert.Equal(1, accountTotal);
        Assert.Equal("a2", byAccount.Single().AuthorId);
        Assert.Equal(1, byAccount.Single().Count);
    }

    [Fact]
    public async Task Authors_PageThroughResults()
    {
        var (list, total) = await CreateService().GetFeedAuthorsAsync(
            new DouyinFeedAuthorRequestDto { PageIndex = 2, PageSize = 3 });

        Assert.Equal(4, total);
        Assert.Equal(new[] { "a4" }, list.Select(x => x.AuthorId).ToArray());
    }

    [Fact]
    public void Shuffle_IsDeterministicAndKeepsAllIds()
    {
        var ids = Enumerable.Range(1, 50).Select(x => "id" + x).ToList();

        var first = DouyinFeedTypes.Shuffle(ids, 7);
        var second = DouyinFeedTypes.Shuffle(Enumerable.Reverse(ids), 7);

        Assert.Equal(first, second);
        Assert.Equal(ids.OrderBy(x => x), first.OrderBy(x => x));
        Assert.NotEqual(ids, first);
    }
}
