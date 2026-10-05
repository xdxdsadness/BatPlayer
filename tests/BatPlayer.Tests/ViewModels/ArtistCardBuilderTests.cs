using System.Linq;
using BatPlayer.Database;
using BatPlayer.Models;
using BatPlayer.ViewModels;
using Xunit;

namespace BatPlayer.Tests.ViewModels;

/// <summary>
/// Группировка карточек «Исполнителей» из локальных треков + лайков SoundCloud
/// (ArtistCardBuilder — чистая функция, вынесена из ArtistsViewModel).
/// </summary>
public class ArtistCardBuilderTests
{
    private static Track Local(string artist, string title, string? cover = null)
        => new() { Artist = artist, Title = title, CoverCachePath = cover };

    private static SoundCloudLikeRow Like(string artist = "Neon Fox", string title = "Midnight Drive",
                                          string? artwork = null)
        => new()
        {
            ScId = "42",
            Title = title,
            Artist = artist,
            DurationMs = 150_000,
            ArtworkUrl = "https://i1.sndcdn.com/artworks-42-t500x500.jpg",
            PermalinkUrl = "https://soundcloud.com/neon-fox/midnight-drive",
            Streamable = true,
            SyncedAt = "2026-09-15T00:00:00Z",
            ArtworkLocalPath = artwork,
        };

    [Fact]
    public void LocalOnly_SingleArtist_GroupedAndHasScFalse()
    {
        var cards = ArtistCardBuilder.Build(new[]
        {
            Local("Neon Fox", "Alpha"),
            Local("Neon Fox", "Beta"),
        }, System.Array.Empty<SoundCloudLikeRow>());

        var card = Assert.Single(cards);
        Assert.Equal("neon fox", card.Key);
        Assert.Equal("Neon Fox", card.DisplayName); // первое увиденное написание
        Assert.Equal(2, card.TrackCount);
        Assert.False(card.HasSc);
    }

    [Fact]
    public void ScLikesMergeIntoLocalArtist_CountSummed_HasScTrue()
    {
        var cards = ArtistCardBuilder.Build(new[]
        {
            Local("Neon Fox", "Alpha"),
        }, new[] { Like(), Like(artist: "Neon Fox", title: "Remix") });

        var card = Assert.Single(cards);
        Assert.Equal("neon fox", card.Key);
        Assert.Equal(3, card.TrackCount); // 1 локальный + 2 лайка
        Assert.True(card.HasSc);
    }

    [Fact]
    public void ScOnlyArtist_GetsOwnCardWithCloud()
    {
        var cards = ArtistCardBuilder.Build(
            new[] { Local("Other Artist", "Song") },
            new[] { Like(artist: "Cloud Only", title: "Stream") });

        Assert.Equal(2, cards.Count);
        var cloudCard = cards.Single(c => c.Key == "cloud only");
        Assert.True(cloudCard.HasSc);
        Assert.Equal(1, cloudCard.TrackCount);
        var localCard = cards.Single(c => c.Key == "other artist");
        Assert.False(localCard.HasSc);
    }

    [Fact]
    public void Cover_LocalPreferred_FallsBackToScArtwork()
    {
        // У локальных треков обложки нет → берётся artwork лайка.
        var cards = ArtistCardBuilder.Build(new[]
        {
            Local("Neon Fox", "Alpha"),
            Local("Neon Fox", "Beta"),
        }, new[] { Like(artwork: @"C:\cache\artworks_cache\42.jpg") });

        var card = Assert.Single(cards);
        Assert.Equal(@"C:\cache\artworks_cache\42.jpg", card.CoverPath);
    }

    [Fact]
    public void Cover_LocalCoverWinsOverScArtwork()
    {
        var cards = ArtistCardBuilder.Build(new[]
        {
            Local("Neon Fox", "Alpha", cover: @"C:\cache\cover_cache\local.jpg"),
        }, new[] { Like(artwork: @"C:\cache\artworks_cache\42.jpg") });

        var card = Assert.Single(cards);
        Assert.Equal(@"C:\cache\cover_cache\local.jpg", card.CoverPath);
    }

    [Fact]
    public void UnknownArtists_GluedIntoOneGroup()
    {
        // ""-ключ (пустые/legacy-имена) — одна карточка «Неизвестный исполнитель»,
        // локальные и SC-лайки вместе.
        var cards = ArtistCardBuilder.Build(new[]
        {
            Local("Неизвестный исполнитель", "A"),
            Local("", "B"),
        }, new[] { Like(artist: "", title: "C") });

        var card = Assert.Single(cards);
        Assert.Equal(string.Empty, card.Key);
        Assert.Equal(3, card.TrackCount);
        Assert.True(card.HasSc);
    }

    [Fact]
    public void CardsSortedByDisplayName_CaseInsensitive()
    {
        var cards = ArtistCardBuilder.Build(new[]
        {
            Local("beta", "Song"),
            Local("Alpha", "Song"),
        }, System.Array.Empty<SoundCloudLikeRow>());

        Assert.Equal(new[] { "Alpha", "beta" }, cards.Select(c => c.DisplayName).ToArray());
    }

    [Fact]
    public void EmptyInputs_NoCards()
    {
        Assert.Empty(ArtistCardBuilder.Build(
            System.Array.Empty<Track>(), System.Array.Empty<SoundCloudLikeRow>()));
    }
}
