using System.Text;

namespace LanternLauncher.Common;

/// <summary>
/// Modrinth titles are whatever the author typed, and a good number of packs carry a Chinese
/// name next to the English one. The interface is English, so the list shows the Latin part and
/// keeps the author's original on the tooltip and the project page.
/// </summary>
internal static class TitleText
{
    private static readonly (char Open, char Close)[] Brackets =
    {
        ('【', '】'), ('（', '）'), ('(', ')'), ('[', ']'), ('「', '」')
    };

    public static string For(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title ?? "";
        if (!HasCjk(title)) return title;

        // A bracketed Latin name is the author's own English title, so prefer it over
        // whatever is left after cutting the Chinese away.
        foreach (var (open, close) in Brackets)
        {
            var start = title.IndexOf(open);
            if (start < 0) continue;

            var end = title.IndexOf(close, start + 1);
            if (end < 0) continue;

            var inner = title[(start + 1)..end].Trim();
            if (inner.Length >= 3 && !HasCjk(inner)) return inner;
        }

        var latin = StripCjk(title);
        return latin.Length >= 2 ? latin : title;
    }

    private static bool HasCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') return true;
            if (c >= '\u3000' && c <= '\u303f') return true;
        }

        return false;
    }

    private static string StripCjk(string text)
    {
        var kept = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            var cjk = (c >= '\u4e00' && c <= '\u9fff') || (c >= '\u3000' && c <= '\u303f');
            kept.Append(cjk ? ' ' : c);
        }

        // Collapse the gaps the removal leaves and drop the separators that end up leading.
        var parts = kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', parts);
        return joined.Trim(' ', '-', ':', '|', '·', '/', ',');
    }
}
