using System.Text;
using NPEduConsole.Core;

internal static class TeacherPinCsvChecks
{
    internal static async Task VerifyAsync()
    {
        using var stream = new MemoryStream();
        var rows = new[] { new TeacherPinCsvRow("林,\"老师\"", "001007", "003401", "  =无表达式"),
            new TeacherPinCsvRow("\t名字", "local-b", "0076", "七班\r\n八班") };
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true))
            await TeacherPinCsv.WriteAsync(writer, "一中", "S1", rows, CancellationToken.None);
        string csv = Encoding.UTF8.GetString(stream.ToArray());
        const string expected = "\uFEFF学校,学校代码,姓名,短账号,初始PIN,本次教学空间\r\n" +
            "\"一中\",\"S1\",\"林,\"\"老师\"\"\",\"001007\",\"003401\",\"'  =无表达式\"\r\n" +
            "\"一中\",\"S1\",\"'\t名字\",\"local-b\",\"0076\",\"七班\r\n八班\"\r\n";
        if (csv != expected) throw new InvalidDataException("The CSV did not preserve the original text fields.");
        using var untouched = new StringWriter();
        await RejectedAsync(new TeacherPinCsvRow("林", "001007", "2023", ""));
        await RejectedAsync(new TeacherPinCsvRow("林", "local-c", "１２３４", ""));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await TeacherPinCsv.WriteAsync(untouched, "一中", "S1", rows, canceled.Token);
            throw new InvalidOperationException("Canceled export was accepted.");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        if (untouched.GetStringBuilder().Length != 0) throw new InvalidDataException("Rejected input produced output.");
        Console.WriteLine("Verified CSV quoting, formula handling, leading zeros, cancellation and invalid batches.");

        async Task RejectedAsync(TeacherPinCsvRow invalid)
        {
            try { await TeacherPinCsv.WriteAsync(untouched, "一中", "S1", [rows[0], invalid], CancellationToken.None); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("An invalid batch was accepted.");
        }
    }
}
