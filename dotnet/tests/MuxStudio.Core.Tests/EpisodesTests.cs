namespace MuxStudio.Core.Tests;

/// <summary>§6.6 — Nhận diện tên tập (bản gộp dùng chung ba bước). Port <c>tests/test_episodes.py</c>.</summary>
public class EpisodesTests
{
    // (tên, season, tập, tập_cuối, có_season, tên_series)
    public static TheoryData<string, int, int, int?, bool, string> EpisodeCases() => new()
    {
        { "Show.S01E05.1080p.WEB-DL", 1, 5, null, true, "Show" },
        { "One Piece S21E1085 1080p", 21, 1085, null, true, "One Piece" },
        { "Show 1x05 720p", 1, 5, null, true, "Show" },
        { "One Piece - 0643", 0, 643, null, false, "One Piece" },
        { "[Judas] One Piece - 1085 [1080p]", 0, 1085, null, false, "One Piece" },
        { "Naruto EP 055", 0, 55, null, false, "Naruto" },
        { "One.Piece.S16E643.1080p", 16, 643, null, true, "One Piece" },
        { "Show.S01E05-E06.1080p", 1, 5, 6, true, "Show" },
        { "Show.S01E05E06", 1, 5, 6, true, "Show" },
        { "Show.S02E05.720p", 2, 5, null, true, "Show" },
        { "Show Season 2 Episode 5", 2, 5, null, true, "Show" },
        { "One Piece - 1085[1080p]", 0, 1085, null, false, "One Piece" },
        { "[SubsPlease] Frieren - 12 (1080p) [ABCD1234]", 0, 12, null, false, "Frieren" },
        { "Kimetsu - 05v2 [1080p]", 0, 5, null, false, "Kimetsu" },
        { "Phim Tap 12", 0, 12, null, false, "Phim" },
        { "Show.Ep.07.HDTV", 0, 7, null, false, "Show" },
        { "Doctor.Who.2005.S01E01.720p", 1, 1, null, true, "Doctor Who" },
        { "1923.S01E01.1080p", 1, 1, null, true, "1923" },
        { "[YTS.MX] Show (2019) S03E10 [1080p]", 3, 10, null, true, "Show" },
    };

    public static TheoryData<string> Movies() => new()
    {
        "1917 - 2019",
        "Blade Runner 2049",
        "Movie - 1080p",
        "Movie.2019.DDP5.1.x264",
        "28.Years.Later.2025.1080p.AMZN.WEB-DL.DDP5.1",
        "Avengers.Endgame.2019.2160p.BluRay.x265-GRP",
        "[YTS.MX] Dune Part Two (2024) [2160p]",
        "2001 A Space Odyssey",
        "Spider-Man.No.Way.Home.2021.1080p.E-AC3",
        "Epic.Movie.2007.720p",
    };

    [Theory]
    [MemberData(nameof(EpisodeCases))]
    public void Episode_names(string name, int season, int episode, int? end, bool hasSeason, string series)
    {
        var e = Episodes.ParseEpisode(name);
        Assert.NotNull(e);
        Assert.Equal((season, episode, end, hasSeason, true), (e.Season, e.Episode, e.EpisodeEnd, e.HasSeason, e.Confident));
        Assert.Equal(series, e.Series);
    }

    [Fact]
    public void Series_year()
    {
        Assert.Equal(2005, Episodes.ParseEpisode("Doctor.Who.2005.S01E01")!.Year);
        Assert.Equal(2019, Episodes.ParseEpisode("Show (2019) S03E10")!.Year);
    }

    [Theory]
    [MemberData(nameof(Movies))]
    public void Movies_are_not_episodes(string name)
    {
        Assert.Null(Episodes.ParseEpisode(name));
        Assert.False(Episodes.IsEpisode(name));
    }

    [Fact]
    public void Guess_only_when_asked_and_never_confident()
    {
        var g = Episodes.ParseEpisode("Movie.2019.DDP5.1.x264", allowGuess: true);
        Assert.NotNull(g);
        Assert.False(g.Confident);
        Assert.Equal(1, g.Episode);
        Assert.Null(Episodes.ParseEpisode("Movie.2019.DDP5.1.x264"));
    }

    [Theory]
    [InlineData("Show.S01E10", "Show.S01E01", true)]
    [InlineData("Show.S01E01", "Show.S02E01", true)]
    [InlineData("Show.S01E05", "Show.S01E05.720p.vi", false)]
    [InlineData("One Piece - 0643", "One Piece - 0644", true)] // SubFetch cũ: False (TĐ-02)
    [InlineData("One.Piece.S16E643", "One Piece - 0643", false)]
    [InlineData("Show.S01E05", "Show - 05", false)]
    [InlineData("Show.S01E05", "Movie (2019)", false)]
    [InlineData("Movie.2019.DDP5.1", "Movie.2019.DDP5.1", false)] // đoán "không chắc" không bao giờ chặn
    public void Episode_conflict(string a, string b, bool conflict)
    {
        Assert.Equal(conflict, Episodes.EpisodeConflict(a, b));
        Assert.Equal(conflict, Episodes.EpisodeConflict(b, a));
    }

    [Theory]
    [InlineData("Show.S01E05", "Show.S01E05", true)]
    [InlineData("Show.S01E05", "Show.S02E05", false)]
    [InlineData("One Piece - 0643", "[Judas] One Piece - 643 [1080p]", true)]
    [InlineData("One.Piece.S16E643", "One Piece - 0643", true)] // số tập >= 100
    [InlineData("Show.S01E05", "Show - 05", true)] // season 1
    [InlineData("Show.S02E05", "Show - 05", false)] // tập lẻ của season khác
    [InlineData("Show.S01E05", "Movie", false)]
    public void Same_episode(string a, string b, bool same) => Assert.Equal(same, Episodes.SameEpisodeNames(a, b));

    [Fact]
    public void Labels()
    {
        Assert.Equal("S01E05", Episodes.ParseEpisode("Show.S01E05")!.Label);
        Assert.Equal("S01E05", Episodes.ParseEpisode("Show.S01E05")!.RefLabel);
        Assert.Equal("E643", Episodes.ParseEpisode("One Piece - 0643")!.Label);
        Assert.Equal("tap #643", Episodes.ParseEpisode("One Piece - 0643")!.RefLabel);
        Assert.Equal("S01E05", Episodes.ParseEpisode("Show 1x05")!.Label);
    }

    [Fact]
    public void Filter_by_episode_before_trimming_candidates()
    {
        // §6.6: lọc theo số tập TRƯỚC rồi mới cắt danh sách — tên các tập gần như y hệt nhau.
        const string video = "[Judas] One Piece - 0647 [1080p]";
        var pool = Enumerable.Range(641, 12).Select(i => $"[Judas] One Piece - {i:0000} [1080p].vi").ToList();
        var vt = Naming.TitleTokens(video);
        var scores = pool.Select(p => Naming.TitleScore(vt, Naming.TitleTokens(p))).ToHashSet();
        Assert.Single(scores); // điểm tên không phân biệt được tập
        var kept = pool.Where(p => !Episodes.EpisodeConflict(video, p)).ToList();
        Assert.Equal(["[Judas] One Piece - 0647 [1080p].vi"], kept);
    }
}
