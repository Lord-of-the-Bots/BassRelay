using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Xml.Linq;

namespace BassRelay.Services;

/// <summary>Plugin translations are embedded in its DLL and never alter the host's resources or culture.</summary>
public static class Localization
{
    private static readonly string[] Cultures = { "en", "de-DE", "fr-FR", "it", "ko-KR", "ru-RU", "zh-CN" };
    private static readonly IReadOnlyDictionary<string, LanguagePack> Packs = LoadPacks();
    private static LanguagePack _current = Packs["en"];

    public static string CurrentLanguage => Volatile.Read(ref _current).Culture.Name;

    public static string Text(string key, params object[] args)
    {
        var pack = Volatile.Read(ref _current);
        if (!pack.Strings.TryGetValue(key, out var text)) return key;
        return args.Length == 0 ? text : string.Format(pack.Culture, text, args);
    }

    public static bool IsText(string key, string value) =>
        Packs.Values.Any(pack => pack.Strings.TryGetValue(key, out var text) && text == value);

    public static void Apply(CultureInfo culture, ResourceDictionary? resources = null)
    {
        var pack = Resolve(culture);
        Volatile.Write(ref _current, pack);
        if (resources is not null)
            foreach (var entry in pack.Strings) resources[entry.Key] = entry.Value;
    }

    private static LanguagePack Resolve(CultureInfo culture)
    {
        if (Packs.TryGetValue(culture.Name, out var pack)) return pack;
        // Covers region aliases such as en-US and SimHub's zh-Hans-CN resource name.
        return Packs.Values.FirstOrDefault(candidate =>
            candidate.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName) ?? Packs["en"];
    }

    private static IReadOnlyDictionary<string, LanguagePack> LoadPacks()
    {
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in Cultures)
        {
            using var stream = typeof(Localization).Assembly.GetManifestResourceStream(
                "BassRelay.SimHub.Resources." + code + ".xml")
                ?? throw new InvalidOperationException("Missing translation resource: " + code);
            var document = XDocument.Load(stream);
            var strings = document.Root!.Elements("text").ToDictionary(
                item => (string)item.Attribute("key")!, item => item.Value, StringComparer.Ordinal);
            packs.Add(code, new LanguagePack(CultureInfo.GetCultureInfo(code), strings));
        }
        return packs;
    }

    private sealed class LanguagePack
    {
        public LanguagePack(CultureInfo culture, IReadOnlyDictionary<string, string> strings)
        {
            Culture = culture;
            Strings = strings;
        }

        public CultureInfo Culture { get; }
        public IReadOnlyDictionary<string, string> Strings { get; }
    }
}
