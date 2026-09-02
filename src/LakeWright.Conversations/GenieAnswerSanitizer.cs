using System.Text;
using System.Text.RegularExpressions;

namespace LakeWright.Conversations;

/// <summary>Removes active markup from a Genie answer before a host renders it.</summary>
/// <remarks>
/// Markdown links are reduced to their labels unless their HTTPS host appears in the optional
/// allow-list. HTML tags are always removed. This is deliberately a text hygiene helper rather
/// than an HTML sanitizer: applications should still encode the returned text for their renderer.
/// </remarks>
public sealed partial class GenieAnswerSanitizer
{
    private readonly HashSet<string> _allowedHosts;

    /// <summary>Creates a sanitizer with no allowed link hosts.</summary>
    public GenieAnswerSanitizer()
        : this([])
    {
    }

    /// <summary>Creates a sanitizer that retains HTTPS links to the supplied exact host names.</summary>
    public GenieAnswerSanitizer(IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in allowedHosts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            _allowedHosts.Add(host.Trim().TrimEnd('.'));
        }
    }

    /// <summary>Removes HTML and neutralizes markdown links in <paramref name="text"/>.</summary>
    public string? Sanitize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return NeutralizeMarkdownLinks(HtmlTag().Replace(text, string.Empty));
    }

    /// <summary>Returns an answer with its model text sanitized.</summary>
    public GenieAnswer Sanitize(GenieAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return answer with { Text = Sanitize(answer.Text) };
    }

    private string NeutralizeMarkdownLinks(string text)
    {
        var result = new StringBuilder(text.Length);
        var cursor = 0;

        while (cursor < text.Length)
        {
            var labelStart = text.IndexOf('[', cursor);
            if (labelStart < 0)
            {
                result.Append(text, cursor, text.Length - cursor);
                break;
            }

            var markerStart = labelStart > cursor && text[labelStart - 1] == '!'
                ? labelStart - 1
                : labelStart;
            result.Append(text, cursor, markerStart - cursor);

            var labelEnd = text.IndexOf(']', labelStart + 1);
            if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
            {
                result.Append(text, markerStart, labelStart - markerStart + 1);
                cursor = labelStart + 1;
                continue;
            }

            var destinationStart = labelEnd + 2;
            if (!TryReadDestination(text, destinationStart, out var destinationEnd, out var destination))
            {
                result.Append(text, markerStart, labelStart - markerStart + 1);
                cursor = labelStart + 1;
                continue;
            }

            var label = text[(labelStart + 1)..labelEnd];
            result.Append(IsAllowed(destination, out var allowedUri)
                ? $"[{label}]({allowedUri.AbsoluteUri})"
                : label);
            cursor = destinationEnd + 1;
        }

        return result.ToString();
    }

    private bool IsAllowed(string destination, out Uri allowedUri)
    {
        if (Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && _allowedHosts.Contains(uri.DnsSafeHost.TrimEnd('.')))
        {
            allowedUri = uri;
            return true;
        }

        allowedUri = null!;
        return false;
    }

    private static bool TryReadDestination(
        string text,
        int destinationStart,
        out int destinationEnd,
        out string destination)
    {
        if (destinationStart < text.Length && text[destinationStart] == '<')
        {
            var angleEnd = text.IndexOf('>', destinationStart + 1);
            if (angleEnd >= 0 && angleEnd + 1 < text.Length && text[angleEnd + 1] == ')')
            {
                destinationEnd = angleEnd + 1;
                destination = text[(destinationStart + 1)..angleEnd];
                return true;
            }
        }
        else
        {
            var depth = 1;
            for (var index = destinationStart; index < text.Length; index++)
            {
                if (text[index] == '\\' && index + 1 < text.Length)
                {
                    index++;
                    continue;
                }

                if (text[index] == '(')
                {
                    depth++;
                }
                else if (text[index] == ')' && --depth == 0)
                {
                    destinationEnd = index;
                    destination = text[destinationStart..index];
                    return true;
                }
            }
        }

        destinationEnd = default;
        destination = string.Empty;
        return false;
    }

    [GeneratedRegex(@"<\/?[A-Za-z][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

}
