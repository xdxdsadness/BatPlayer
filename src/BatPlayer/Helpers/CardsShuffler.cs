using System;
using System.Collections.ObjectModel;
using System.Linq;
using BatPlayer.Audio;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.Helpers;

/// <summary>Shared Shuffle button for track pages: randomizes the card order; if a track
/// from the same list is playing, the player queue is rebuilt in the new order with the
/// playing track at its position (playback is not interrupted, Next/Previous follow the
/// shuffled queue). If playback is from another list, the queue is left alone: only the
/// card order is shuffled for future runs.</summary>
public static class CardsShuffler
{
    /// <param name="buildTrack">Runtime track of the card for the queue; null = the card
    /// is excluded (unplayable). i is the position in the shuffled list.</param>
    /// <param name="isSameTrack">Whether this card's track is currently playing (by source
    /// and platform id, or by local Id).</param>
    public static void Shuffle<T>(ObservableCollection<T> cards, AudioService audio,
        Func<T, int, Track?> buildTrack, Func<Track, T, bool> isSameTrack, string logContext)
    {
        if (cards.Count < 2) return;

        var playing = audio.CurrentTrack;
        var shuffled = cards.OrderBy(_ => Random.Shared.Next()).ToList();
        cards.Clear();
        foreach (var card in shuffled) cards.Add(card);

        if (playing == null) return;
        var pairs = shuffled.Select((c, i) => (Track: buildTrack(c, i), Card: c))
                            .Where(p => p.Track != null)
                            .ToList();
        var start = pairs.FindIndex(p => isSameTrack(playing, p.Card));
        if (start < 0) return;

        audio.SetQueue(pairs.Select(p => p.Track!), start);
        Logger.Info($"{logContext}: queue shuffled ({pairs.Count} tracks, playing #{start})");
    }
}
