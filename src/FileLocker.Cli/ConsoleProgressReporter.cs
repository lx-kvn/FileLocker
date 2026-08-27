namespace FileLocker.Cli;

/// <summary>
/// 主控台的進度顯示：用 \r 覆寫同一行，顯示真實百分比。
///
/// 不使用 <see cref="System.Progress{T}"/>——它會把回呼排程到 SynchronizationContext，
/// 主控台程式沒有那個東西，回呼會被丟到執行緒集區並彼此交錯執行，用 \r 覆寫同一行的畫面
/// 會亂掉。這裡直接同步寫，回報順序就是實際處理順序。
///
/// 整數百分比沒變就不重畫：加解密是逐區塊回報的（預設 1 MB 一塊），大檔案會有上千次回報，
/// 每次都刷一遍主控台既無意義又拖慢速度。
/// </summary>
public sealed class ConsoleProgressReporter(string label) : IProgress<double>
{
    private int _lastRenderedPercent = -1;

    public void Report(double value)
    {
        var percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
        if (percent == _lastRenderedPercent)
        {
            return;
        }

        _lastRenderedPercent = percent;
        Console.Write($"\r{label} {percent}%");
    }

    /// <summary>用空白蓋掉整行再把游標拉回行首，不在輸出裡留下殘影字元。</summary>
    public void Clear() => Console.Write($"\r{new string(' ', label.Length + 6)}\r");
}
