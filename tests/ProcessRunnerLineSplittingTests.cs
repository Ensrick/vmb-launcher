using System.IO;
using System.Text;
using VmbLauncher.Services;

namespace VmbLauncher.Tests;

/// <summary>
/// Tests targeting the specific log-corruption modes seen in the wild — bare <c>\r</c> in the
/// middle of a Stingray progress message, CRLF line endings, very small read chunks. The fix in
/// v0.2.3 stops treating bare <c>\r</c> as a line terminator and only splits on <c>\n</c>.
/// </summary>
public class ProcessRunnerLineSplittingTests
{
    private static async Task<List<string>> Drain(string content, CancellationToken ct = default)
    {
        var lines = new List<string>();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var reader = new StreamReader(ms, Encoding.UTF8);
        await ProcessRunner.ReadLinesAsync(reader, line => { lock (lines) lines.Add(line); }, ct);
        return lines;
    }

    [Fact]
    public async Task ReadLinesAsync_does_not_split_on_bare_CR()
    {
        // Real-world Stingray output that the OLD code split as "18:06:57.475 error:   [C" + "ompiler]".
        // The bare \r in the middle should be preserved, not treated as a line terminator.
        var input = "18:06:57.475 error:   [C\rompiler] Error compiling\n";
        var lines = await Drain(input);

        Assert.Single(lines);
        Assert.Equal("18:06:57.475 error:   [C\rompiler] Error compiling", lines[0]);
    }

    [Fact]
    public async Task ReadLinesAsync_strips_trailing_CR_for_CRLF()
    {
        var input = "first line\r\nsecond line\r\n";
        var lines = await Drain(input);

        Assert.Equal(2, lines.Count);
        Assert.Equal("first line", lines[0]);
        Assert.Equal("second line", lines[1]);
    }

    [Fact]
    public async Task ReadLinesAsync_handles_LF_only()
    {
        var input = "alpha\nbeta\ngamma\n";
        var lines = await Drain(input);

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines);
    }

    [Fact]
    public async Task ReadLinesAsync_handles_no_trailing_newline()
    {
        var input = "alpha\nbeta";
        var lines = await Drain(input);

        Assert.Equal(new[] { "alpha", "beta" }, lines);
    }

    [Fact]
    public async Task ReadLinesAsync_handles_empty_input()
    {
        var lines = await Drain("");
        Assert.Empty(lines);
    }

    [Fact]
    public async Task ReadLinesAsync_handles_bare_CR_at_end_of_line_before_LF()
    {
        // "line1\r\nline2\n" should still split into two clean lines.
        var input = "line1\r\nline2\n";
        var lines = await Drain(input);
        Assert.Equal(new[] { "line1", "line2" }, lines);
    }

    [Fact]
    public async Task ReadLinesAsync_progress_overwrites_collapse_into_one_line()
    {
        // Stingray-style progress: each step writes "...\r" with a final "\n". Our reader treats
        // the bare \r as a normal character, so we get ONE buffered line containing the full
        // progress sequence — better than treating each as a separate event.
        var input = "Compiling\rCompiling [10%]\rCompiling [50%]\rCompiling done\n";
        var lines = await Drain(input);

        Assert.Single(lines);
        Assert.Contains("Compiling done", lines[0]);
        // The full progress chain is in there too, separated by \r:
        Assert.Contains("Compiling [10%]", lines[0]);
    }

    [Fact]
    public async Task ReadLinesAsync_handles_chunk_split_in_middle_of_line()
    {
        // Force the reader to receive data in tiny chunks. We simulate that by reading from a
        // SlowStream wrapper that returns 1 byte at a time. Lines should still be correct.
        var bytes = Encoding.UTF8.GetBytes("hello world\nfoo bar\n");
        using var ms = new MemoryStream(bytes);
        using var slow = new SlowStream(ms, bytesPerRead: 1);
        using var reader = new StreamReader(slow);
        var lines = new List<string>();
        await ProcessRunner.ReadLinesAsync(reader, l => lines.Add(l), CancellationToken.None);
        Assert.Equal(new[] { "hello world", "foo bar" }, lines);
    }

    /// <summary>Stream wrapper that only returns N bytes per <see cref="Read"/> call to force chunked reads.</summary>
    private sealed class SlowStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _max;
        public SlowStream(Stream inner, int bytesPerRead) { _inner = inner; _max = bytesPerRead; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(_max, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, Math.Min(_max, count), ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer.Slice(0, Math.Min(_max, buffer.Length)), ct);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RunAsync_no_longer_splits_words_with_embedded_CR()
    {
        // End-to-end version: spawn cmd to print a string with embedded \r, verify the launcher
        // captures it as a single line. Before v0.2.3 this would split into two lines.
        var lines = new List<string>();
        // cmd's "echo" can print special chars if we pass them; simpler to use a script that
        // writes the literal byte sequence. powershell's Write-Host outputs without converting.
        var result = await ProcessRunner.RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-Command", "[System.Console]::Out.Write(\"alpha`rbeta`n\")" },
            null,
            l => { lock (lines) lines.Add(l); },
            null,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        // Should be one line containing both words separated by \r — not split.
        Assert.Contains(lines, l => l.Contains("alpha") && l.Contains("beta"));
        // Verify no line is just "alpha" or just "beta" on its own (which would indicate the split).
        Assert.DoesNotContain(lines, l => l == "alpha");
        Assert.DoesNotContain(lines, l => l == "beta");
    }
}
