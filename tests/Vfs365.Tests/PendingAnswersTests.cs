using Vfs365.Drive.WinFsp;

namespace Vfs365.Tests;

/// <summary>
/// A request answered later (STATUS_PENDING) gets exactly one answer whatever throws (review 2026-10-30, front end 2): WinFsp never
/// cancels or times out a request a dispatcher thread took, so one left unanswered hung its caller and every access to its file.
/// The mounted volume can't be made to throw there, so the answering is tested on its own.
/// </summary>
public sealed class PendingAnswersTests
{
    const int UnexpectedIoError = unchecked((int)0xC00000E9);

    readonly List<string> log = [];
    readonly List<(int Status, uint Bytes)> sent = [];

    PendingAnswers Answers() => new(_ => UnexpectedIoError, line => log.Add(line));

    void Send(int status, uint bytes) => sent.Add((status, bytes));

    [Fact]
    public async Task A_write_whose_file_info_throws_is_still_answered_with_an_error()
    {
        // As AnswerWriteAsync: the write succeeded, then working out the file info throws (a timestamp before 1601 does)
        var written = new TaskCompletionSource<(int Status, uint Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answering = Answers().AnswerAsync(@"write \a.txt", new ValueTask<(int Status, uint Bytes)>(written.Task), result =>
        {
            _ = new DateTime(1500, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
            return result;
        }, Send);
        Assert.Empty(sent);

        written.SetResult((0, 4096));
        await answering;

        Assert.Equal([(UnexpectedIoError, 0u)], sent);
        Assert.Contains(log, line => line.StartsWith(@"error: answering write \a.txt: ArgumentOutOfRangeException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_result_that_throws_is_answered_with_an_error_without_filling()
    {
        var filled = false;
        await Answers().AnswerAsync(@"read \a.txt", Throws(), result =>
        {
            filled = true;
            return result;
        }, Send);

        Assert.False(filled);
        Assert.Equal([(UnexpectedIoError, 0u)], sent);

        static async ValueTask<(int Status, uint Bytes)> Throws()
        {
            await Task.Yield();
            throw new IOException("the staging file is gone");
        }
    }

    [Fact]
    public async Task A_copy_into_the_buffer_that_throws_answers_with_an_error_and_no_bytes()
    {
        await Answers().AnswerAsync(@"listing \big", ValueTask.FromResult((0, 512u)), _ => throw new ArgumentOutOfRangeException("length"), Send);

        Assert.Equal([(UnexpectedIoError, 0u)], sent);
    }

    [Fact]
    public async Task A_result_is_answered_once_as_filled()
    {
        await Answers().AnswerAsync(@"read \a.txt", ValueTask.FromResult((0, 512u)), result => (result.Status, 100u), Send);

        Assert.Equal([(0, 100u)], sent);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Nothing_is_answered_after_the_unmount()
    {
        var answers = Answers();
        var done = new TaskCompletionSource<(int Status, uint Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var filled = false;
        var answering = answers.AnswerAsync(@"read \a.txt", new ValueTask<(int Status, uint Bytes)>(done.Task), result =>
        {
            filled = true;
            return result;
        }, Send);

        answers.Stop();
        done.SetResult((0, 512));
        await answering;

        Assert.False(filled);
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_send_that_throws_is_logged_not_thrown()
    {
        await Answers().AnswerAsync(@"write \a.txt", ValueTask.FromResult((0, 1u)), result => result, (_, _) => throw new InvalidOperationException("gone"));

        Assert.Contains(log, line => line == @"error: answering write \a.txt: InvalidOperationException: gone");
    }
}
