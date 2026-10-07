using System;
using System.Collections.Generic;

namespace BatPlayer.Models;

public enum RepeatMode
{
    None = 0,
    RepeatOne = 1,
    RepeatAll = 2
}

public enum SortColumn
{
    Title,
    Artist,
    Album,
    Duration,
    DateAdded,
    LastPlayed
}

public enum SortDirection
{
    Ascending,
    Descending
}

public enum ViewMode
{
    List,
    Cards
}

/// <summary>
/// Player state persisted across runs.
/// </summary>
public sealed class PlaybackState
{
    public long? CurrentTrackId { get; set; }
    public long LastPositionTicks { get; set; }
    public int Volume { get; set; } = 70;
    public bool IsMuted { get; set; }
    public bool IsShuffle { get; set; }
    public RepeatMode RepeatMode { get; set; } = RepeatMode.None;
    public List<long> QueueTrackIds { get; set; } = new();
    public int QueueIndex { get; set; }
    public long? LastPlaylistId { get; set; }
}

/// <summary>EQ band type: Bell — peaking (has gain); LowCut/HighCut — low/high cut
/// filters with selectable slope (12/18/24/30/36/48 dB/oct, step 6); cuts have no gain.</summary>
public enum EqualizerBandType
{
    Bell = 0,
    LowCut = 1,
    HighCut = 2
}

/// <summary>
/// Equalizer band. Gain and Frequency are observable: the EQ graph drags nodes
/// (gain vertically, frequency horizontally), dB labels update with the slider,
/// and EqualizerViewModel applies values to the audio engine on change notifications.
/// </summary>
public sealed class EqualizerBand : System.ComponentModel.INotifyPropertyChanged
{
    public int Index { get; set; }

    private double _frequency;
    public double Frequency
    {
        get => _frequency;
        set
        {
            if (_frequency == value) return;
            _frequency = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Frequency)));
        }
    }

    private double _gain; // dB, -12..+12 (Bell only; cut filters have no gain)
    public double Gain
    {
        get => _gain;
        set
        {
            if (_gain == value) return;
            _gain = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Gain)));
        }
    }

    private EqualizerBandType _type = EqualizerBandType.Bell;
    public EqualizerBandType Type
    {
        get => _type;
        set
        {
            if (_type == value) return;
            _type = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Type)));
        }
    }

    private int _slopeDbOct = 12; // cut slope: 12/18/24/30/36/48 dB/oct
    public int SlopeDbOct
    {
        get => _slopeDbOct;
        set
        {
            if (_slopeDbOct == value) return;
            _slopeDbOct = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SlopeDbOct)));
        }
    }

    private double _q = 1.41; // Bell band width (0.3..8): higher Q = narrower boost
    public double Q
    {
        get => _q;
        set
        {
            if (_q == value) return;
            _q = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Q)));
        }
    }

    private bool _isSolo; // solo listen: only this band passes through
    public bool IsSolo
    {
        get => _isSolo;
        set
        {
            if (_isSolo == value) return;
            _isSolo = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSolo)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class EqualizerPreset
{
    public string Name { get; set; } = "Flat";
    public bool IsBuiltIn { get; set; } = true;
    public double PreGain { get; set; } // dB
    public List<EqualizerBand> Bands { get; set; } = new();

    // The DarkComboBox template does not apply DisplayMemberPath to the selected item
    // (SelectionBoxItem renders as-is) — without ToString the combo box showed the
    // type name "BatPlayer.Models.EqualizerPreset".
    public override string ToString() => Name;
}

public sealed class HistoryEntry
{
    public long TrackId { get; set; }
    public DateTime PlayedAt { get; set; }
    public int PlayCount { get; set; }
    public long LastPositionTicks { get; set; }
    public Track? Track { get; set; }
}
