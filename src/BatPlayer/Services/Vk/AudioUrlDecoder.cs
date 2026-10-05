using System;
using System.Numerics;
using System.Text;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Декодер ссылок вида https://vk.ru/mp3/audio_api_unavailable.mp3?extra=&lt;tag1&gt;#&lt;tag2&gt;.
/// Порт алгоритма из веб-плеера VK (core_spa: функции M, L и реестр j):
///   • tag1 — начальное значение (custom base64 с алфавитом, где "0" стоит на месте "O");
///   • tag2 — список функций через таб; каждая — "имя\vарг1\vарг2…", применяется
///     с КОНЦА списка к началу, первое значение в каждой паре — текущий результат;
///   • v: реверс строки; r: ротация символов по двойному алфавиту; s: тасование по seed;
///   • i: тасование по seed = vk.id ^ parseInt(аргумент); x: XOR символов с символом аргумента.
/// Возвращает http(s)-ссылку на mp3 или исходную строку, если декод не удался.
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

            // функции применяются с конца списка
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
            return unavailableUrl; // декод не удался — вернём как есть, трек будет пропущен
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

            // JS: (P++ % 4) — условие по старому P; сдвиг — по новому P
            if (p % 4 != 0)
                outChars.Append((char)(255 & (x >> ((-2 * (p + 1)) & 6))));

            p++;
        }
        return outChars.ToString();
    }

    // ===== Реестр функций =====

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    /// <summary>Ротация символов по двойному алфавиту (JS substr с отрицательным
    /// началом считает от конца строки — повторяем это поведение).</summary>
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

    /// <summary>Тасование: перестановка индексов (LCG с seed, модуль — длина строки)
    /// и перенос символов по ним. seed может быть большим (vk.id ^ код) — BigInteger.</summary>
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
