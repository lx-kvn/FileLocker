using System.Diagnostics;

// FileLocker 自我更新用的協助行程：靜默安裝要求 FileLocker.exe 必須已經完全結束才能繼續
// （mac-style-windows-installer 的 process_running 檢查沒有旗標可以跳過），FileLocker 沒辦法
// 自己邊跑邊等自己的安裝完成，所以由這支獨立行程負責「等 FileLocker 結束 → 跑靜默安裝 →
// 依結果重啟 FileLocker」，跟 FileLocker.PasswordLockerNativeHost 是同一類「純轉接層、
// 手動驗證」的既有慣例，不含任何跟更新邏輯本身無關的東西。
//
// 命令列參數：<parentPid> <installerPath> <fileLockerExePath> <logPath>
// FileLocker.exe 的 MainWindow.HandleDownloadAndInstallUpdateRequestAsync 負責組出這幾個參數
// 並啟動這支程式，之後立刻自己 Shutdown()。

const int WaitForParentExitTimeoutSeconds = 30;
const string UpdateSucceededArgFlag = "--updated";
const string UpdateFailedArgFlag = "--update-failed";

if (args.Length < 4)
{
    return;
}

var parentPidText = args[0];
var installerPath = args[1];
var fileLockerExePath = args[2];
var logPath = args[3];

try
{
    if (int.TryParse(parentPidText, out var parentPid))
    {
        await WaitForProcessExitAsync(parentPid, TimeSpan.FromSeconds(WaitForParentExitTimeoutSeconds));
    }

    // UseShellExecute = true 是必要的：CreateProcess 不會觸發 UAC 提權，只有 ShellExecute 會
    // （見 mac-style-windows-installer 規格文件 §8.33）。FileLocker 裝在 Program Files，
    // 靜默只代表安裝程式自己的畫面不跳出來，系統層級的 UAC 同意畫面還是會跳一次，這是
    // Windows 本身的限制，不是這裡能繞過的。
    var installProcess = Process.Start(new ProcessStartInfo
    {
        FileName = installerPath,
        Arguments = $"/S /LOG=\"{logPath}\"",
        UseShellExecute = true
    });

    if (installProcess is null)
    {
        RelaunchFileLocker(fileLockerExePath, UpdateFailedArgFlag);
        return;
    }

    installProcess.WaitForExit();

    // 安裝工具只有 0（成功）／非 0（失敗）兩種 exit code，沒有針對個別失敗原因的細分代碼
    // （見規格文件 run_silent_install 的說明），失敗時安裝工具本身會自動回滾，舊檔案還在，
    // 這裡只要重啟舊版並讓 FileLocker 用 --update-failed 顯示提示即可，不需要自己解析
    // logPath 判斷失敗原因。
    RelaunchFileLocker(fileLockerExePath, installProcess.ExitCode == 0 ? UpdateSucceededArgFlag : UpdateFailedArgFlag);
}
catch (Exception)
{
    // 這裡刻意接最基底的 Exception——不管哪個步驟出錯，都要保底嘗試重啟 FileLocker，
    // 不能讓使用者卡在「更新到一半、什麼都沒有」的狀態，跟 PasswordLockerNativeHost
    // 既有的錯誤處理哲學一致（純轉接層程式不該把整個流程炸掉）。
    TryRelaunchFileLockerBestEffort(fileLockerExePath);
}

static async Task WaitForProcessExitAsync(int pid, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // 行程已經結束，GetProcessById 找不到對應 id 會丟這個例外。
        }
        await Task.Delay(250);
    }
    // 逾時就放棄等待，直接往下跑——FileLocker 遲遲不結束通常代表卡住了，硬跑安裝只會撞到
    // process_running 檢查失敗，但至少已經盡力等過，不讓這支協助行程無限期卡住。
}

static void RelaunchFileLocker(string fileLockerExePath, string argFlag)
{
    try
    {
        Process.Start(new ProcessStartInfo { FileName = fileLockerExePath, Arguments = argFlag, UseShellExecute = true });
    }
    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
    {
        TryRelaunchFileLockerBestEffort(fileLockerExePath);
    }
}

static void TryRelaunchFileLockerBestEffort(string fileLockerExePath)
{
    try
    {
        Process.Start(new ProcessStartInfo { FileName = fileLockerExePath, UseShellExecute = true });
    }
    catch (Exception)
    {
        // 真的什麼都做不了了——沒有 GUI 可以顯示錯誤，這支協助行程本身也快結束了，只能放棄。
    }
}
