using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnketaParser.Infrastructure.Sources;

internal sealed record DvachPost(long Number, long Parent, string Html, DateTimeOffset? PublishedAt);
internal sealed record DvachSnapshot(IReadOnlyList<DvachPost> Posts, bool Closed)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static DvachSnapshot Parse(ReadOnlySpan<byte> payload, DvachThread thread)
    {
        var document = JsonSerializer.Deserialize<ThreadDocument>(payload, Options);
        if (document is null || document.IsBoard || document.IsIndex || document.CurrentThread != thread.Number ||
            document.Board?.Id != thread.Board || document.Threads is not [{ Posts: { } posts }] ||
            document.IsClosed is not (0 or 1) || document.PostsCount < 0)
            throw new SourceFailure("invalid_thread", "parse");

        if (posts.Length != document.PostsCount)
            throw new SourceFailure("incomplete_snapshot", "parse");
        if (posts.Length == 0)
        {
            // Без OP форма ответа не доказывает полноту, даже если сервер прислал нулевые счётчики.
            if (document.MaxNum != 0)
                throw new SourceFailure("incomplete_snapshot", "parse");
            return new DvachSnapshot([], document.IsClosed == 1);
        }
        if (posts[0]?.Num != thread.Number || posts[^1]?.Num != document.MaxNum)
            throw new SourceFailure("incomplete_snapshot", "parse");

        List<DvachPost> result = new(posts.Length);
        long previous = 0;
        for (var index = 0; index < posts.Length; index++)
        {
            var post = posts[index];
            if (post is null || post.Num <= previous || post.Number != index + 1 ||
                post.Parent != (index == 0 ? 0 : thread.Number))
                throw new SourceFailure("incomplete_snapshot", "parse");
            if (post.Board != thread.Board || post.Comment is null || post.Comment.Contains('\0', StringComparison.Ordinal) ||
                post.Timestamp is < 0 or > 253402300799)
                throw new SourceFailure("invalid_post", "parse");

            result.Add(new DvachPost(post.Num, post.Parent, post.Comment,
                post.Timestamp is > 0 ? DateTimeOffset.FromUnixTimeSeconds(post.Timestamp.Value) : null));
            previous = post.Num;
        }
        return new DvachSnapshot(result, document.IsClosed == 1);
    }

    internal static string PostId(long number) => number.ToString(CultureInfo.InvariantCulture);

    private sealed class ThreadDocument
    {
        [JsonRequired] public BoardDocument? Board { get; set; }
        [JsonRequired] public long CurrentThread { get; set; }
        [JsonRequired] public bool IsBoard { get; set; }
        [JsonRequired] public bool IsIndex { get; set; }
        [JsonRequired] public int IsClosed { get; set; }
        [JsonRequired] public long MaxNum { get; set; }
        [JsonRequired] public int PostsCount { get; set; }
        [JsonRequired] public PostList?[]? Threads { get; set; }
    }

    private sealed class BoardDocument
    {
        public string? Id { get; set; }
    }

    private sealed class PostList
    {
        public PostDocument?[]? Posts { get; set; }
    }

    private sealed class PostDocument
    {
        [JsonRequired] public long Num { get; set; }
        [JsonRequired] public int Number { get; set; }
        [JsonRequired] public long Parent { get; set; }
        public long? Timestamp { get; set; }
        public string? Board { get; set; }
        public string? Comment { get; set; }
    }
}

internal sealed class SourceFailure(string category, string phase, int? httpStatus = null, DateTimeOffset? retryAt = null)
    : Exception(category)
{
    internal string Category { get; } = category;
    internal string Phase { get; } = phase;
    internal int? HttpStatus { get; } = httpStatus;
    internal DateTimeOffset? RetryAt { get; } = retryAt;
}