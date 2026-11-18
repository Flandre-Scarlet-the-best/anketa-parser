using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

namespace AnketaParser.Infrastructure.Sources;

public sealed class DvachSettings
{
    public const string SectionName = "Dvach";

    public string? ThreadUrl { get; set; }
    public int PollIntervalSeconds { get; set; } = 300;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
}

internal sealed class DvachSettingsValidator : IValidateOptions<DvachSettings>
{
    public ValidateOptionsResult Validate(string? name, DvachSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> errors = [];
        if (!string.IsNullOrEmpty(settings.ThreadUrl) && DvachThread.Parse(settings.ThreadUrl) is null)
            errors.Add("Dvach:ThreadUrl должен быть HTTPS-ссылкой на тред 2ch.org без параметров и фрагмента.");
        if (settings.PollIntervalSeconds is < 60 or > 86400)
            errors.Add("Dvach:PollIntervalSeconds должен быть от 60 до 86400.");
        if (settings.RequestTimeoutSeconds is < 1 or > 120)
            errors.Add("Dvach:RequestTimeoutSeconds должен быть от 1 до 120.");
        if (settings.MaxResponseBytes is < 1024 or > 32 * 1024 * 1024)
            errors.Add("Dvach:MaxResponseBytes должен быть от 1024 до 33554432.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed partial record DvachThread(string Board, long Number)
{
    private const string Origin = "https://2ch.org";

    public string SourceKey => Url;
    public string FixtureKey => "fixture:" + SourceKey;
    public string Url => ThreadResource + ".html";
    internal Uri JsonUrl => new(ThreadResource + ".json");
    private string ThreadResource => string.Create(CultureInfo.InvariantCulture, $"{Origin}/{Board}/res/{Number}");

    public static DvachThread? Parse(string? url)
    {
        var match = ThreadPattern().Match(url ?? string.Empty);
        return match.Success && long.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? new DvachThread(match.Groups[1].Value, number)
            : null;
    }

    [GeneratedRegex(@"\Ahttps://2ch\.org/([a-z0-9]{1,16})/res/([1-9][0-9]{0,18})\.html\z", RegexOptions.CultureInvariant)]
    private static partial Regex ThreadPattern();
}