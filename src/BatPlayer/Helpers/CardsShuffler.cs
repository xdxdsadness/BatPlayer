using System;
using System.Collections.ObjectModel;
using System.Linq;
using BatPlayer.Audio;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.Helpers;

/// <summary>Общая кнопка «Перемешать» для страниц с треками: случайный порядок
/// карточек; если играет трек из этого же списка — очередь плеера перестраивается
/// по новому порядку с играющим треком на его месте (воспроизведение не прерывается,
/// Next/Previous идут по перемешанной очереди). Играет не из этого списка — очередь
/// не трогается: перемешается порядок карточек для следующих запусков.</summary>
public static class CardsShuffler
{
    /// <param name="buildTrack">Runtime-трек карточки для очереди; null — карточка
    /// в очередь не попадает (непроиграбельная). i — позиция в перемешанном списке.</param>
    /// <param name="isSameTrack">Играет ли сейчас трек этой карточки (по источнику
    /// и платформенному id либо по Id локального).</param>
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
