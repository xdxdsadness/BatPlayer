using System.Collections.Generic;
using BatPlayer.Database;
using BatPlayer.Helpers;
using BatPlayer.Models;
using Xunit;

namespace BatPlayer.Tests.Helpers;

/// <summary>
/// Building runtime tracks from SoundCloud likes (Home/Favorites cards) and
/// matching them against the local library.
/// </summary>
public class SoundCloudRuntimeTracksTests
{
    private static SoundCloudLikeRow Like(string scId = "42", string title = "Midnight Drive",
        string artist = "Neon Fox", long durationMs = 150_000, string? artwork = null)
        => new()
        {
            ScId = scId,
            Title = title,
            Artist = artist,
            DurationMs = durationMs,
            ArtworkUrl = "https://i1.sndcdn.com/artworks-42-t500x500.jpg",
            PermalinkUrl = "https://soundcloud.com/neon-fox/midnight-drive",
            Streamable = true,
            LikedAt = "2026-09-01T00:00:00Z",
            SyncedAt = "2026-09-15T00:00:00Z",
            ArtworkLocalPath = artwork,
        };

    [Fact]
    public void BuildRuntimeTrack_MapsAllLikeFields()
    {
        var track = SoundCloudRuntimeTracks.BuildRuntimeTrack(
            Like(artwork: @"C:\cache\artworks_cache\42.jpg"), artworkLocalPath: @"C:\cache\artworks_cache\42.jpg", index: 3);

        Assert.Equal(-4, track.Id); // Id = -1 - index: no clash with local tracks
        Assert.Equal("42", track.ScId);
        Assert.Equal("Midnight Drive", track.Title);
        Assert.Equal("Neon Fox", track.Artist);
        Assert.Equal(150_000 * System.TimeSpan.TicksPerMillisecond, track.DurationTicks);
        Assert.Equal(@"C:\cache\artworks_cache\42.jpg", track.CoverCachePath);
        Assert.True(track.IsFavorite);      // likes
        Assert.True(track.IsAvailable);
        Assert.Equal(Track.SourceSoundCloud, track.Source);
        Assert.Equal(string.Empty, track.FilePath); // file resolved on click
    }

    [Fact]
    public void BuildRuntimeTrack_NegativeIdsAreUniquePerIndex()
    {
        var ids = new HashSet<long>();
        for (int i = 0; i < 5; i++)
        {
            var track = SoundCloudRuntimeTracks.BuildRuntimeTrack(Like(scId: i.ToString()), null, i);
            Assert.True(track.Id < 0);
            Assert.True(ids.Add(track.Id));
        }
    }

    [Fact]
    public void BuildRuntimeTrack_NullArtwork_KeepsCoverPathNull()
    {
        // Empty path → the card shows a placeholder (existing fallback).
        var track = SoundCloudRuntimeTracks.BuildRuntimeTrack(Like(), artworkLocalPath: null, index: 0);
        Assert.Null(track.CoverCachePath);
    }

    [Fact]
    public void BuildRuntimeTrack_CarriesLikedAtAsDateAdded()
    {
        // Like date becomes the card's DateAdded for unified "newest first" sorting,
        // so a fresh SC like sits above older tracks from other sources.
        var track = SoundCloudRuntimeTracks.BuildRuntimeTrack(Like(), null, index: 0);
        Assert.Equal(new System.DateTime(2026, 9, 1, 0, 0, 0, System.DateTimeKind.Utc), track.DateAdded);
    }

    [Fact]
    public void WithCachedFile_KeepsIdentity_AndSetsFilePath()
    {
        var runtime = SoundCloudRuntimeTracks.BuildRuntimeTrack(Like(), null, index: 7);
        var cached = SoundCloudRuntimeTracks.WithCachedFile(runtime, @"C:\cache\sc_cache\42.mp3");

        Assert.Equal(runtime.Id, cached.Id);       // repeat card click: pause/resume
        Assert.Equal(runtime.ScId, cached.ScId);
        Assert.Equal(runtime.Title, cached.Title);
        Assert.Equal(runtime.Artist, cached.Artist);
        Assert.Equal(runtime.DurationTicks, cached.DurationTicks);
        Assert.Equal(runtime.Source, cached.Source);
        Assert.Equal(runtime.DateAdded, cached.DateAdded); // like date preserved
        Assert.Equal(@"C:\cache\sc_cache\42.mp3", cached.FilePath);
    }

    [Fact]
    public void Like_MatchesLocalLibrary_ByArtistAndTitle()
    {
        // Same path LibraryViewModel uses on click: like row → FindLocalMatch.
        var local = new List<Track>
        {
            new() { Artist = "Other Artist", Title = "Midnight Drive" },
            new() { Artist = "Neon Fox", Title = "Other Song" },
            new() { Artist = "Neon Fox", Title = "Midnight Drive" },
        };

        var like = Like(); // "Neon Fox — Midnight Drive"
        var match = MatchHelper.FindLocalMatch(local, like.Artist, like.Title);

        Assert.NotNull(match);
        Assert.Equal("Neon Fox", match!.Artist);
        Assert.Equal("Midnight Drive", match.Title);
    }

    [Fact]
    public void Like_NoLocalMatch_ReturnsNull()
    {
        var local = new List<Track> { new() { Artist = "Neon Fox", Title = "Other Song" } };
        var like = Like();

        Assert.Null(MatchHelper.FindLocalMatch(local, like.Artist, like.Title));
    }

    // ===== NeedsFilePathResolve: decides whether AudioService opens the file as-is or calls the resolver =====

    [Fact]
    public void NeedsFilePathResolve_ScRuntimeCard_ReturnsTrue()
    {
        var track = SoundCloudRuntimeTracks.BuildRuntimeTrack(Like(), null, index: 0);
        Assert.True(SoundCloudRuntimeTracks.NeedsFilePathResolve(track));
    }

    [Fact]
    public void NeedsFilePathResolve_LocalTrack_ReturnsFalse()
    {
        var track = new Track { FilePath = @"C:\music\a.mp3", Source = Track.SourceLocal };
        Assert.False(SoundCloudRuntimeTracks.NeedsFilePathResolve(track));
    }

    [Fact]
    public void NeedsFilePathResolve_LocalTrackWithoutFile_ReturnsFalse()
    {
        // Local track without a file needs no resolver: Open() reports the error as before.
        var track = new Track { FilePath = string.Empty, Source = Track.SourceLocal };
        Assert.False(SoundCloudRuntimeTracks.NeedsFilePathResolve(track));
    }

    [Fact]
    public void NeedsFilePathResolve_ScCardWithResolvedFile_ReturnsFalse()
    {
        // Already-resolved card (repeat click/transition) opens as-is.
        var track = SoundCloudRuntimeTracks.WithCachedFile(
            SoundCloudRuntimeTracks.BuildRuntimeTrack(Like(), null, 0), @"C:\cache\sc_cache\42.mp3");
        Assert.False(SoundCloudRuntimeTracks.NeedsFilePathResolve(track));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NeedsFilePathResolve_ScCardWithoutScId_ReturnsFalse(string? scId)
    {
        var track = new Track { FilePath = string.Empty, Source = Track.SourceSoundCloud, ScId = scId! };
        Assert.False(SoundCloudRuntimeTracks.NeedsFilePathResolve(track));
    }

    [Fact]
    public void NeedsFilePathResolve_Null_ReturnsFalse()
    {
        Assert.False(SoundCloudRuntimeTracks.NeedsFilePathResolve(null));
    }

    [Fact]
    public void BuildRuntimeTrack_FromFields_MatchesLikeOverload()
    {
        // Overload for SoundCloud page cards (no SoundCloudLikeRow) — same Id/fields.
        var fromLike = SoundCloudRuntimeTracks.BuildRuntimeTrack(Like(artwork: @"C:\art\42.jpg"), @"C:\art\42.jpg", 2);
        var fromFields = SoundCloudRuntimeTracks.BuildRuntimeTrack(
            "42", "Midnight Drive", "Neon Fox", 150_000, @"C:\art\42.jpg", 2);

        Assert.Equal(fromLike.Id, fromFields.Id);
        Assert.Equal(fromLike.ScId, fromFields.ScId);
        Assert.Equal(fromLike.Title, fromFields.Title);
        Assert.Equal(fromLike.Artist, fromFields.Artist);
        Assert.Equal(fromLike.DurationTicks, fromFields.DurationTicks);
        Assert.Equal(fromLike.CoverCachePath, fromFields.CoverCachePath);
        Assert.Equal(fromLike.Source, fromFields.Source);
    }

    // ===== FilterWithSoundCloud: search over local tracks + SoundCloud likes =====

    [Fact]
    public void Filter_MatchesLikeByTitle_RuntimeCardAppendedAfterLocals()
    {
        var local = new List<Track>
        {
            new() { Title = "Midnight Drive", Artist = "Neon Fox", FilePath = @"C:\m\a.mp3" },
            new() { Title = "Unrelated", Artist = "Someone" },
        };
        var likes = new List<SoundCloudLikeRow> { Like() }; // "Neon Fox — Midnight Drive"

        var result = SoundCloudRuntimeTracks.FilterWithSoundCloud(local, likes, "Midnight", startIndex: 0);

        Assert.Equal(2, result.Count);
        Assert.Equal(Track.SourceLocal, result[0].Source);      // local matches first
        Assert.Equal(Track.SourceSoundCloud, result[1].Source); // SC match appended last
        Assert.Equal("42", result[1].ScId);
        Assert.Equal("Midnight Drive", result[1].Title);
        Assert.Equal(string.Empty, result[1].FilePath);         // file resolved on click
        Assert.Equal(-1, result[1].Id);                         // Id = -1 - index
    }

    [Fact]
    public void Filter_MatchesLikeByArtist_CaseInsensitive()
    {
        var result = SoundCloudRuntimeTracks.FilterWithSoundCloud(
            new List<Track>(), new List<SoundCloudLikeRow> { Like() }, "NEON fox", startIndex: 0);

        Assert.Single(result);
        Assert.Equal(Track.SourceSoundCloud, result[0].Source);
        Assert.Equal("Neon Fox", result[0].Artist);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmptyList()
    {
        var local = new List<Track> { new() { Title = "Alpha", Artist = "Beta" } };
        var result = SoundCloudRuntimeTracks.FilterWithSoundCloud(
            local, new List<SoundCloudLikeRow> { Like() }, "zzz-nothing", startIndex: 0);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Filter_EmptyQuery_ReturnsAllLocals_WithoutSoundCloudCards(string? query)
    {
        // Empty query = plain page list: SC likes on Home are added by LoadAsync.
        var local = new List<Track> { new() { Title = "Alpha" }, new() { Title = "Beta" } };
        var result = SoundCloudRuntimeTracks.FilterWithSoundCloud(
            local, new List<SoundCloudLikeRow> { Like() }, query, startIndex: 0);

        Assert.Equal(2, result.Count);
        Assert.All(result, t => Assert.Equal(Track.SourceLocal, t.Source));
    }

    [Fact]
    public void Filter_StartIndex_ShiftsRuntimeIds()
    {
        var likes = new List<SoundCloudLikeRow>
        {
            Like(scId: "7"),                          // "Neon Fox — Midnight Drive"
            Like(scId: "9", title: "Second Tune"),    // "Neon Fox — Second Tune"
        };

        // "e" appears in both "Midnight Drive"/"Neon Fox" and "Second Tune" — both likes match.
        var result = SoundCloudRuntimeTracks.FilterWithSoundCloud(
            new List<Track>(), likes, "e", startIndex: 5);

        Assert.Equal(2, result.Count);
        Assert.Equal(-6, result[0].Id); // Id = -1 - (startIndex + match number)
        Assert.Equal("7", result[0].ScId);
        Assert.Equal(-7, result[1].Id);
        Assert.Equal("9", result[1].ScId);
    }

    [Fact]
    public void Filter_LocalTracks_MatchTitleArtistAlbumGenre()
    {
        var local = new List<Track>
        {
            new() { Title = "AAA", Artist = "BBB", Album = "Cool Album" },
            new() { Title = "CCC", Artist = "DDD", Genre = "Jazz" },
            new() { Title = "EEE", Artist = "FFF" },
        };
        var noLikes = new List<SoundCloudLikeRow>();

        var byAlbum = SoundCloudRuntimeTracks.FilterWithSoundCloud(local, noLikes, "cool", startIndex: 0);
        Assert.Single(byAlbum);
        Assert.Equal("AAA", byAlbum[0].Title);

        var byGenre = SoundCloudRuntimeTracks.FilterWithSoundCloud(local, noLikes, "JAZZ", startIndex: 0);
        Assert.Single(byGenre);
        Assert.Equal("CCC", byGenre[0].Title);
    }

    [Fact]
    public void Filter_SoundCloudCard_DoesNotMatchAlbumOrGenre()
    {
        // Likes have no album/genre: match by title/artist only.
        var local = new List<Track> { new() { Title = "Neon Fox", Artist = "X", Album = "Midnight Drive" } };
        var likes = new List<SoundCloudLikeRow> { Like(artist: "Unrelated", title: "Unrelated") };

        var result = SoundCloudRuntimeTracks.FilterWithSoundCloud(
            local, likes, "Midnight Drive", startIndex: 0);

        Assert.Single(result);
        Assert.Equal(Track.SourceLocal, result[0].Source);
    }

    // ===== BuildScAppend: SC part of the library list by page filter =====
    // The repository returns likes ordered by liked_at DESC (newest first) — test
    // lists follow the same order.

    private static List<SoundCloudLikeRow> Likes(int count)
    {
        var likes = new List<SoundCloudLikeRow>();
        for (int i = 0; i < count; i++)
            likes.Add(Like(scId: (100 + i).ToString(), title: $"Track {i}"));
        return likes; // [0] is the newest (liked_at DESC)
    }

    [Fact]
    public void BuildScAppend_Played_TakesOnly50Newest()
    {
        var result = SoundCloudRuntimeTracks.BuildScAppend("Played", Likes(61));

        Assert.Equal(SoundCloudRuntimeTracks.RecentlyPlayedScLimit, result.Count);
        Assert.Equal("100", result[0].ScId); // first is the newest like
        Assert.Equal("149", result[49].ScId);
        Assert.All(result, t =>
        {
            Assert.Equal(Track.SourceSoundCloud, t.Source);
            Assert.Equal(string.Empty, t.FilePath); // file resolved on click
        });
    }

    [Fact]
    public void BuildScAppend_Played_FewerThan50_ReturnsAll()
    {
        var result = SoundCloudRuntimeTracks.BuildScAppend("Played", Likes(3));

        Assert.Equal(3, result.Count);
        Assert.Equal("100", result[0].ScId);
        Assert.Equal("102", result[2].ScId);
    }

    [Fact]
    public void BuildScAppend_Favorites_ReturnsAllLikes()
    {
        var result = SoundCloudRuntimeTracks.BuildScAppend("Favorites", Likes(61));

        Assert.Equal(61, result.Count);
        Assert.Equal("100", result[0].ScId);
        Assert.Equal("160", result[60].ScId);
    }

    [Fact]
    public void BuildScAppend_All_ReturnsAllLikes()
    {
        // Home: as before — all likes appended to the local list (by LoadAsync).
        var result = SoundCloudRuntimeTracks.BuildScAppend("All", Likes(2));

        Assert.Equal(2, result.Count);
        Assert.All(result, t => Assert.Equal(Track.SourceSoundCloud, t.Source));
    }

    [Theory]
    [InlineData("Recent")]
    public void BuildScAppend_Recent_ReturnsEmpty(string filterMode)
    {
        // "Recently added" is a local-files-only page.
        Assert.Empty(SoundCloudRuntimeTracks.BuildScAppend(filterMode, Likes(5)));
    }

    [Fact]
    public void BuildScAppend_EmptyLikes_ReturnsEmpty()
    {
        Assert.Empty(SoundCloudRuntimeTracks.BuildScAppend("All", new List<SoundCloudLikeRow>()));
        Assert.Empty(SoundCloudRuntimeTracks.BuildScAppend("Played", new List<SoundCloudLikeRow>()));
    }

    // ===== GetNextSoundCloudCandidate: prefetch candidate for the next queue track =====

    private static Track LocalTrack(string title) => new() { Title = title, FilePath = @"C:\m\" + title + ".mp3" };

    private static Track ScCard(string scId, bool resolved = false, bool available = true)
    {
        var track = SoundCloudRuntimeTracks.BuildRuntimeTrack(
            Like(scId: scId), artworkLocalPath: null, index: 0);
        track.IsAvailable = available;
        if (resolved) track.FilePath = @"C:\cache\sc_cache\" + scId + ".mp3";
        return track;
    }

    [Fact]
    public void GetNext_UnresolvedScAfterLocal_ReturnsIt()
    {
        // Mixed local/sc: an unresolved SC card follows a local track.
        var queue = new List<Track> { LocalTrack("a"), ScCard("42"), LocalTrack("b") };

        var next = SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(queue, currentIndex: 0);

        Assert.NotNull(next);
        Assert.Equal("42", next!.ScId);
    }

    [Fact]
    public void GetNext_NextIsLocal_ReturnsNull()
    {
        var queue = new List<Track> { ScCard("42"), LocalTrack("b") };

        Assert.Null(SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(queue, currentIndex: 0));
    }

    [Fact]
    public void GetNext_NextIsResolvedSc_ReturnsNull()
    {
        // File already resolved (replay) — nothing to download.
        var queue = new List<Track> { LocalTrack("a"), ScCard("42", resolved: true) };

        Assert.Null(SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(queue, currentIndex: 0));
    }

    [Fact]
    public void GetNext_NextIsKnownUnavailable_ReturnsNull()
    {
        // Auto-transition skips tracks that failed resolve this session — don't prefetch.
        var queue = new List<Track> { LocalTrack("a"), ScCard("42", available: false) };

        Assert.Null(SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(queue, currentIndex: 0));
    }

    [Fact]
    public void GetNext_CurrentAtLastElement_ReturnsNull()
    {
        var queue = new List<Track> { LocalTrack("a"), ScCard("42") };

        Assert.Null(SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(queue, currentIndex: 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    public void GetNext_CurrentIndexNegative_ReturnsNull(int currentIndex)
    {
        // Nothing to play — prefetch is not defined relative to the queue.
        var queue = new List<Track> { ScCard("42") };
        Assert.Null(SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(queue, currentIndex));
    }

    [Fact]
    public void GetNext_NullOrEmptyQueue_ReturnsNull()
    {
        Assert.Null(SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(null!, 0));
        Assert.Null(SoundCloudRuntimeTracks.GetNextSoundCloudCandidate(new List<Track>(), 0));
    }
}
