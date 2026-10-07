using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BatPlayer.Services.Vk;
using Xunit;

namespace BatPlayer.Tests.Services;

/// <summary>
/// Tests for pure functions of the VK web-session layer: positional al_audio.php payload
/// parsing, VkTrackRow mapping, login-page (expired session) detection, load_section
/// pagination guard, userId extraction from HTML and the vk_auth.json file. No network.
/// </summary>
public class VkServiceTests
{
    // ===================== Positional al_audio payload =====================

    // Fixture mirrors a live al_audio.php (act=load_section) response: <!json> prefix,
    // payload/data wrapper, tracks as positional arrays ([2]=stream url, [3]=artist,
    // [4]=title, [5]=duration). Second track has a top-level image instead of nested
    // covers; third has no url. data carries total/nextOffset; decoy arrays inside.
    private const string AlAudioFixture = """
        <!json>{"payload":[{"code":0,"data":["load_section",[
          [456239017, 53992517, "https://psv4.vkuseraudio.net/s/v1/acq/a1/b1/t1.mp3?extra=abc",
           "Кино", "Звезда по имени Солнце", 232, 1, 0, 0, "", 0, 0, 0,
           ["https://sun9-1.userapi.com/c1/v1/a.jpg", "https://sun9-1.userapi.com/c1/v1/b.jpg"], 0],
          [456239018, 53992517, "https://psv4.vkuseraudio.net/s/v1/acq/a2/b2/t2.mp3?extra=def",
           "Кино", "Gruppa krovi", 278, 0, 0, "", 0, 0, 0, 0, 0,
           "https://sun9-2.userapi.com/c2/v2/cover.jpg"],
          [456239019, 53992517, "", "Кино", "Пачка сигарет", 262, 0, 0, "", 0, 0, 0, 0, 0, 0]
        ],{"total":3,"nextOffset":0}]},{"code":0,"data":["progress"]}]}
        """;

    private static AlAudioPayload ParseFixture() => PositionalAudioParser.ParsePayload(AlAudioFixture);

    [Fact]
    public void ParsePayload_ReadsTracksFromPositionalArrays()
    {
        var payload = ParseFixture();

        Assert.True(payload.Parsed);
        Assert.Equal(0, payload.ErrorCode);
        Assert.Equal(3, payload.ReportedTotal);
        Assert.Equal(0, payload.NextOffset);
        // Track without url filtered out at the candidate stage (no http string).
        Assert.Equal(2, payload.Tracks.Count);
    }

    [Fact]
    public void ParsePayload_MapsTrackFieldsFromKnownPositions()
    {
        var track = ParseFixture().Tracks[0];

        Assert.Equal(456239017, track.AudioId);
        Assert.Equal(53992517, track.OwnerId);
        // vk_id = "{owner_id}_{id}" — the format audio.get produced.
        Assert.Equal("53992517_456239017", track.VkId);
        // First non-http string is artist, second is title.
        Assert.Equal("Кино", track.Artist);
        Assert.Equal("Звезда по имени Солнце", track.Title);
        // duration is the first integer after title (seconds).
        Assert.Equal(232, track.DurationSec);
        // Stream is the first non-image http string; artwork is the nested .jpg.
        Assert.Equal("https://psv4.vkuseraudio.net/s/v1/acq/a1/b1/t1.mp3?extra=abc", track.StreamUrl);
        Assert.Equal("https://sun9-1.userapi.com/c1/v1/a.jpg", track.ArtworkUrl);
        Assert.True(track.IsPlayable);
    }

    [Fact]
    public void ParsePayload_TopLevelImageString_IsArtworkNotStream()
    {
        var track = ParseFixture().Tracks[1];

        // Top-level image is not a stream; .jpg classification works without nested
        // cover arrays too.
        Assert.Equal("https://sun9-2.userapi.com/c2/v2/cover.jpg", track.ArtworkUrl);
        Assert.Equal("https://psv4.vkuseraudio.net/s/v1/acq/a2/b2/t2.mp3?extra=def", track.StreamUrl);
        Assert.Equal("Gruppa krovi", track.Title);
        Assert.Equal(278, track.DurationSec);
    }

    [Fact]
    public void ParseTrackElement_WithoutUrl_IsNotPlayable()
    {
        // Hidden/removed entries: valid array but empty url — IsPlayable=false,
        // never reaches the catalog (see ExtractRows).
        var track = ParseTrack("""[456239019, 53992517, "", "Кино", "Пачка сигарет", 262, 0, 0, "", 0, 0, 0, 0, 0, 0]""");

        Assert.NotNull(track);
        Assert.Null(track!.StreamUrl);
        Assert.False(track.IsPlayable);
        Assert.Equal("Пачка сигарет", track.Title);
    }

    [Fact]
    public void ParseTrackElement_ImageOnlyHttpString_IsNotAStream()
    {
        // Only http string is an image — cannot be a stream.
        var track = ParseTrack("""
            [456239020, 53992517, "https://sun9-3.userapi.com/x.jpg", "Кино", "Апрель", 200, 0, 0, "", 0, 0, 0, 0, 0, 0]
            """);

        Assert.NotNull(track);
        Assert.Null(track!.StreamUrl);
        Assert.False(track.IsPlayable);
        Assert.Equal("https://sun9-3.userapi.com/x.jpg", track.ArtworkUrl);
    }

    [Fact]
    public void ParseTrackElement_SingleMergedString_SplitsArtistTitle()
    {
        // VK sometimes merges "Artist - Title" — split on the first " - ".
        var track = ParseTrack("""[10, 20, "https://x/t.mp3", "Кино - Звезда", 232, 0, 0, 0, 0, 0, 0, 0]""");

        Assert.NotNull(track);
        Assert.Equal("Кино", track!.Artist);
        Assert.Equal("Звезда", track.Title);
    }

    [Fact]
    public void ParsePayload_GarbageInput_ReturnsEmptyWithoutThrow()
    {
        foreach (var garbage in new[] { "", "   ", "gateway timeout html", "<!json>{invalid", "{" })
        {
            var payload = PositionalAudioParser.ParsePayload(garbage);
            Assert.False(payload.Parsed);
            Assert.Empty(payload.Tracks);
        }
    }

    [Fact]
    public void ParsePayload_MultiChunk_BadSecondChunkDoesNotBreakFirst()
    {
        var body = "<!json>{\"payload\":[{\"code\":0,\"data\":[]}]}<!json>{broken";

        var payload = PositionalAudioParser.ParsePayload(body);

        Assert.True(payload.Parsed);
        Assert.Empty(payload.Tracks);
    }

    [Fact]
    public void ParsePayload_ErrorCodeFromPayload_IsReported()
    {
        var payload = PositionalAudioParser.ParsePayload(
            """<!json>{"payload":[{"code":22,"data":[]}]}""");

        Assert.True(payload.Parsed);
        Assert.Equal(22, payload.ErrorCode);
    }

    [Fact]
    public void IsTrackCandidate_FiltersDecoys()
    {
        // Array too short; http string shorter than 12; [0] not an int.
        Assert.False(PositionalAudioParser.IsTrackCandidate(ToJsonElement("""["https://x/t.mp3"]""")));
        Assert.False(PositionalAudioParser.IsTrackCandidate(ToJsonElement("""[1,2,3,4]""")));
        Assert.False(PositionalAudioParser.IsTrackCandidate(
            ToJsonElement("""["no-int", 555, "https://x/t.mp3", "", "", "", "", "", "", "", "", "", ""]""")));
        // Group owner (negative owner_id) is a valid track.
        Assert.True(PositionalAudioParser.IsTrackCandidate(
            ToJsonElement("""[456, -999, "https://x/t.mp3", "a", "t", 100, 0, 0, 0, 0, 0, 0, 0]""")));
    }

    // ===================== Login page detection =====================

    [Fact]
    public void IsLoginHtml_LoginPages_AreDetected()
    {
        Assert.True(PositionalAudioParser.IsLoginHtml("<html><body><form action=\"/login\">…"));
        Assert.True(PositionalAudioParser.IsLoginHtml("  <!DOCTYPE html><html><head><title>VK Login</title>"));
    }

    [Fact]
    public void IsLoginHtml_JsonPayloadsAndJunk_AreNotLoginPages()
    {
        Assert.False(PositionalAudioParser.IsLoginHtml(AlAudioFixture));
        Assert.False(PositionalAudioParser.IsLoginHtml(""));
        Assert.False(PositionalAudioParser.IsLoginHtml("   "));
        Assert.False(PositionalAudioParser.IsLoginHtml("0")); // VK sometimes replies with a bare "0"
    }

    // ===================== Mapping to VkTrackRow =====================

    [Fact]
    public void ExtractRows_MapsVkTrackRow()
    {
        var rows = VkService.ExtractRows(ParseFixture().Tracks, "2026-09-16T00:00:00.0000000Z");

        // Unplayable (no url) entries are not written to the catalog.
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal("53992517_456239017", first.VkId);
        Assert.Equal("Звезда по имени Солнце", first.Title);
        Assert.Equal("Кино", first.Artist);
        // payload duration is seconds; DB stores milliseconds.
        Assert.Equal(232_000, first.DurationMs);
        Assert.Equal("https://sun9-1.userapi.com/c1/v1/a.jpg", first.ArtworkUrl);
        Assert.Equal("2026-09-16T00:00:00.0000000Z", first.SyncedAt);
        Assert.Null(first.ArtworkLocalPath);
    }

    [Fact]
    public void ExtractRows_EmptyInput_ReturnsEmpty()
        => Assert.Empty(VkService.ExtractRows(new List<ParsedWebAudio>(), "now"));

    [Fact]
    public void BuildVkId_CombinesOwnerAndId()
        => Assert.Equal("53992517_456239017", VkService.BuildVkId(53992517, 456239017));

    // ===================== load_section pagination =====================

    [Fact]
    public void NextAudioOffset_FullPage_Continues()
    {
        // Full page of 1000 out of 3500 — next offset 1000.
        Assert.Equal(1000, VkService.NextAudioOffset(0, VkService.PageSize, 3500, VkService.PageSize));
        Assert.Equal(2000, VkService.NextAudioOffset(1000, VkService.PageSize, 3500, VkService.PageSize));
    }

    [Fact]
    public void NextAudioOffset_ReachedReportedTotal_Stops()
    {
        // 3500 of 3500 — end despite the full page.
        Assert.Null(VkService.NextAudioOffset(3000, 500, 3500, 1000));
    }

    [Fact]
    public void NextAudioOffset_ShortPage_Stops()
    {
        // Page shorter than requested — last one, even if count disagrees.
        Assert.Null(VkService.NextAudioOffset(0, 999, 3500, 1000));
    }

    [Fact]
    public void NextAudioOffset_EmptyPage_Stops()
        => Assert.Null(VkService.NextAudioOffset(0, 0, 3500, 1000));

    [Fact]
    public void NextAudioOffset_ZeroTotal_StopsOnShortPage()
    {
        // Missing/zero total: decide by page size.
        Assert.Equal(1000, VkService.NextAudioOffset(0, 1000, 0, 1000));
        Assert.Null(VkService.NextAudioOffset(0, 400, 0, 1000));
    }

    [Fact]
    public void NextWebOffset_ExplicitNextForward_WinsOverCalculation()
    {
        // Explicit forward nextOffset from the payload wins: usually the whole library
        // arrives in one load_section (short page = stop), but VK sometimes hints
        // continuation itself.
        var payload = new AlAudioPayload(new List<ParsedWebAudio>(), NextOffset: 500,
            ReportedTotal: 0, ErrorCode: 0, Parsed: true);
        Assert.Equal(500, VkService.NextWebOffset(0, payload));
    }

    [Fact]
    public void NextWebOffset_ExplicitNextNotForward_FallsBackToCalculation()
    {
        // nextOffset=0 (no shift) — fall back to total/page-size math: 2 < 1000 → end.
        var payload = new AlAudioPayload(new List<ParsedWebAudio>(), NextOffset: 0,
            ReportedTotal: 2, ErrorCode: 0, Parsed: true);
        Assert.Null(VkService.NextWebOffset(0, payload));
    }

    [Fact]
    public void NextWebOffset_NoExplicitNext_UsesNextAudioOffset()
    {
        // Full page (1000 tracks) with total 3500 → next offset 1000.
        var fullPage = Enumerable.Repeat(
            new ParsedWebAudio { AudioId = 1, OwnerId = 1, StreamUrl = "https://x/t.mp3" }, 1000).ToList();
        var payload = new AlAudioPayload(fullPage, NextOffset: null,
            ReportedTotal: 3500, ErrorCode: 0, Parsed: true);
        Assert.Equal(1000, VkService.NextWebOffset(0, payload));
    }

    [Fact]
    public void MaxAudioPages_PaginationGuard_IsTen()
    {
        // load_section loop guard: at most 10 pages.
        Assert.Equal(10, VkService.MaxAudioPages);
    }

    // ===================== Web-session userId =====================

    [Fact]
    public void ExtractUserIdFromHtml_BootDataUid_Wins()
    {
        const string html = """<html><script>window.vk = {"uid":53992517,"section":"feed"};</script>…""";
        Assert.Equal("53992517", VkService.ExtractUserIdFromHtml(html));
    }

    [Fact]
    public void ExtractUserIdFromHtml_ViewerIdAndProfileLinks()
    {
        Assert.Equal("12345", VkService.ExtractUserIdFromHtml("""{"viewer_id":12345}"""));
        Assert.Equal("12345", VkService.ExtractUserIdFromHtml("…viewer_id=12345&…"));
        // Last resort: profile links.
        Assert.Equal("777", VkService.ExtractUserIdFromHtml("""<a href="/id777">Me</a>"""));
    }

    [Fact]
    public void ExtractUserIdFromHtml_ZeroUid_FallsThroughToNextPattern()
    {
        const string html = """{"uid":0} <a href="/id5">profile</a>""";
        Assert.Equal("5", VkService.ExtractUserIdFromHtml(html));
    }

    [Fact]
    public void ExtractUserIdFromHtml_NoMatch_ReturnsNull()
    {
        Assert.Null(VkService.ExtractUserIdFromHtml("<html><body>…no ids here…</body></html>"));
        Assert.Null(VkService.ExtractUserIdFromHtml(""));
        Assert.Null(VkService.ExtractUserIdFromHtml(null));
    }

    // ===================== vk_auth.json =====================

    [Fact]
    public async Task VkAuthService_SaveLoadDelete_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vk_auth_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new VkAuthService(path);
            Assert.False(store.Exists);

            store.Save(new VkAuthFile
            {
                CookieHeader = "remixsid=abc123; remixstid=xyz; remixuid=53992517",
                AccessToken = "legacy-oauth-token",
                UserId = "53992517",
                SavedAt = "2026-09-16T10:00:00.0000000Z",
                LastSyncedAtUtc = "2026-09-16T10:30:00.0000000Z"
            });
            Assert.True(store.Exists);

            var loaded = store.Load();
            Assert.Equal("remixsid=abc123; remixstid=xyz; remixuid=53992517", loaded.CookieHeader);
            // AccessToken is a legacy field, read for old-file compatibility.
            Assert.Equal("legacy-oauth-token", loaded.AccessToken);
            Assert.Equal("53992517", loaded.UserId);
            Assert.Equal("2026-09-16T10:00:00.0000000Z", loaded.SavedAt);
            Assert.Equal("2026-09-16T10:30:00.0000000Z", loaded.LastSyncedAtUtc);

            store.Delete();
            Assert.False(store.Exists);
            // Deleting a missing file does not throw.
            store.Delete();
        }
        finally
        {
            await Task.CompletedTask;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void VkAuthService_MissingFile_ReturnsEmpty()
    {
        var store = new VkAuthService(Path.Combine(Path.GetTempPath(), $"vk_auth_missing_{Guid.NewGuid():N}.json"));

        var file = store.Load();
        Assert.Null(file.CookieHeader);
        Assert.Null(file.AccessToken);
        Assert.Empty(file.UserId ?? string.Empty);
    }

    [Fact]
    public void VkAuthService_CorruptedFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vk_auth_bad_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json");
            var file = new VkAuthService(path).Load();
            Assert.Null(file.CookieHeader);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    // ===================== Helpers =====================

    private static JsonElement ToJsonElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static ParsedWebAudio? ParseTrack(string jsonArray)
        => PositionalAudioParser.ParseTrackElement(ToJsonElement(jsonArray));
}
