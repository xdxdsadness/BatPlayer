using System;
using System.Numerics;
using System.Text;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Decoder for URLs of the form https://vk.ru/mp3/audio_api_unavailable.mp3?extra=&lt;tag1&gt;#&lt;tag2&gt;.
/// Port of the VK web player algorithm (core_spa: functions M, L and the j registry):
///   • tag1 — initial value (custom base64 with an alphabet where "0" sits at "O");
///   • tag2 — tab-separated function list; each entry is "name\varg1\varg2…", applied
///     from the END of the list to the start; the first value in each pair is the current result;
///   • v: reverse string; r: rotate chars over the doubled alphabet; s: shuffle by seed;
///   • i: shuffle by seed = vk.id ^ parseInt(arg); x: XOR chars with the argument's char.
/// Returns an http(s) mp3 URL, or the original string if decoding fails.
/// </summary>
public static class VkAudioUrlDecoder
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN0PQRSTUVWXYZO123456789+/=";

    public static string Decode(string unavailableUrl, long vkUserId)
    {
        try
        {
            var marker = "?extra=";
            var i = unavailableUrl.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return unavailableUrl;

            var tag = unavailableUrl[(i + marker.Length)..].Split('#');
            var value = VkBase64Decode(tag[0]);
            if (value == null) return unavailableUrl;

            var funcs = tag.Length > 1 && tag[1].Length > 0 ? VkBase64Decode(tag[1]) : string.Empty;
            if (funcs == null) return unavailableUrl;

            // functions are applied from the end of the list
            var specs = funcs.Split('\t');
            for (var f = specs.Length - 1; f >= 0; f--)
            {
                var parts = specs[f].Split('\v');
                var fname = parts[0];
                value = fname switch
                {
                    "v" => Reverse(value),
                    "r" => parts.Length > 1 ? Rotate(value, ToInt(parts[1])) : value,
                    "s" => parts.Length > 1 ? Shuffle(value, ToBigInt(parts[1])) : value,
                    "i" => parts.Length > 1 ? Shuffle(value, new BigInteger(vkUserId) ^ ToBigInt(parts[1])) : value,
                    "x" => parts.Length > 1 ? XorChars(value, parts[1]) : value,
                    _ => value
                };
            }

            return value.StartsWith("http", StringComparison.Ordinal) ? value : unavailableUrl;
        }
        catch (Exception)
        {
            return unavailableUrl; // decode failed — return as is; the track will be skipped
        }
    }

    // ===== Custom base64 =====

    private static string? VkBase64Decode(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length % 4 == 1) return null;

        var x = 0;
        var p = 0;
        var outChars = new StringBuilder();
        foreach (var ch in input)
        {
            var idx = Alphabet.IndexOf(ch);
            if (idx < 0) continue;

            if (p % 4 != 0)
                x = 64 * x + idx;
            else
                x = idx;

            // JS: (P++ % 4) — condition uses the old P, shift the new P
            if (p % 4 != 0)
                outChars.Append((char)(255 & (x >> ((-2 * (p + 1)) & 6))));

            p++;
        }
        return outChars.ToString();
    }

    // ===== Function registry =====

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    /// <summary>Rotates chars over the doubled alphabet (JS substr with a negative start
    /// counts from the string end — behavior replicated here).</summary>
    private static string Rotate(string s, int x)
    {
        var doubled = Alphabet + Alphabet;
        var chars = s.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var idx = doubled.IndexOf(chars[i]);
            if (idx < 0) continue;
            var pos = idx - x;
            if (pos < 0) pos += doubled.Length;
            chars[i] = pos < doubled.Length ? doubled[pos] : doubled[pos % doubled.Length];
        }
        return new string(chars);
    }

    /// <summary>Shuffle: permutes indices (LCG with seed, modulo the string length)
    /// and moves chars accordingly. The seed can be large (vk.id ^ code) — hence BigInteger.</summary>
    private static string Shuffle(string s, BigInteger seed)
    {
        var length = s.Length;
        if (length == 0 || seed == 0) return s;

        var chars = s.ToCharArray();
        var i = seed;
        if (i < 0) i = -i;

        for (var o = length - 1; o >= 0; o--)
        {
            i = ((length * (o + 1)) ^ (i + o)) % length;
            if (i < 0) i = -i;
            var idx = (int)i;
            (chars[idx], chars[o]) = (chars[o], chars[idx]);
        }
        return new string(chars);
    }

    private static string XorChars(string s, string key)
    {
        if (string.IsNullOrEmpty(key)) return s;
        var k = key[0];
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)(chars[i] ^ k);
        return new string(chars);
    }

    private static int ToInt(string s)
    {
        return int.TryParse(s, out var v) ? v : 0;
    }

    private static BigInteger ToBigInt(string s)
    {
        return BigInteger.TryParse(s, out var v) ? v : BigInteger.Zero;
    }
}
