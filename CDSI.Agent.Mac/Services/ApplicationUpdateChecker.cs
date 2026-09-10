using System.Globalization;
using System.Net.Http.Headers;

namespace CDSI.Agent.Mac.Services;

public sealed record ApplicationUpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable);

public sealed class ApplicationUpdateChecker : IDisposable
{
    public const string VersionFileUrl =
        "https://raw.githubusercontent.com/cdsi-project/beacon-mac/main/VERSION";
    public const string ReleasesUrl =
        "https://github.com/cdsi-project/beacon-mac/releases";
    private const int MaximumVersionFileLength = 64;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ApplicationUpdateChecker()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _ownsHttpClient = true;
    }

    internal ApplicationUpdateChecker(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ApplicationUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var parsedCurrent = BeaconVersion.Parse(currentVersion);
        using var request = new HttpRequestMessage(HttpMethod.Get, VersionFileUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            "CDSI-Beacon",
            currentVersion));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumVersionFileLength)
        {
            throw new InvalidDataException("远端 VERSION 文件内容过长。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaximumVersionFileLength + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (count > MaximumVersionFileLength)
        {
            throw new InvalidDataException("远端 VERSION 文件内容过长。");
        }

        var parsedLatest = BeaconVersion.Parse(
            new string(buffer, 0, count).Trim().TrimStart('\uFEFF'));
        return new ApplicationUpdateCheckResult(
            parsedCurrent.ToString(),
            parsedLatest.ToString(),
            parsedLatest.CompareTo(parsedCurrent) > 0);
    }

    internal static int CompareVersions(string left, string right) =>
        BeaconVersion.Parse(left).CompareTo(BeaconVersion.Parse(right));

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private readonly record struct BeaconVersion(
        int Major,
        int Minor,
        int Revision,
        bool IsLegacy) : IComparable<BeaconVersion>
    {
        public static BeaconVersion Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new FormatException("版本号不能为空。");
            }

            var parts = value.Trim().Split('.');
            if (parts.Length == 3 && parts[2].Length == 2 &&
                TryParse(parts[0], out var major) &&
                TryParse(parts[1], out var minor) &&
                TryParse(parts[2], out var revision) && revision is >= 10 and <= 99)
            {
                return new BeaconVersion(major, minor, revision, false);
            }

            if (parts.Length == 2 && parts[0] == "0" && parts[1].Length == 3 &&
                TryParse(parts[1], out var legacy) && legacy is >= 100 and <= 206)
            {
                return new BeaconVersion(0, 0, legacy, true);
            }

            throw new FormatException($"版本号“{value.Trim()}”格式无效。");
        }

        public int CompareTo(BeaconVersion other)
        {
            if (IsLegacy != other.IsLegacy)
            {
                return IsLegacy ? -1 : 1;
            }

            var major = Major.CompareTo(other.Major);
            if (major != 0)
            {
                return major;
            }

            if (IsLegacy)
            {
                return Revision.CompareTo(other.Revision);
            }

            var minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Revision.CompareTo(other.Revision);
        }

        public override string ToString() => IsLegacy
            ? $"0.{Revision.ToString(CultureInfo.InvariantCulture)}"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Major}.{Minor}.{Revision:D2}");

        private static bool TryParse(string value, out int result)
        {
            result = 0;
            return value.Length > 0 &&
                value.All(character => character is >= '0' and <= '9') &&
                int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }
}
