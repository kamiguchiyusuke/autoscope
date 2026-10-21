using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MT4BatchReporter;

// This controller only receives PIDs already verified as this run's MT4.
// It never reads edit controls, sends credentials, or presses a login/next button.
public sealed class StartupDialogCanceller(bool enabled, Action<string>? log = null)
{
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private readonly Dictionary<nint, (int pid, string title, long sent, int attempts)> pending = [];
    private static readonly HashSet<string> KnownTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "デモ口座の申請", "口座の申請", "口座開設", "Open an Account", "Open Account", "Opening an Account",
        "ログイン", "取引口座にログイン", "取引口座のログイン", "取引口座の認証", "認証",
        "Login", "Log in", "Authorization", "Account Login", "Login to Trade Account"
    };

    public void Poll(int processId)
    {
        if (!enabled || lifetime.Elapsed > TimeSpan.FromMinutes(2)) return;
        foreach (var entry in pending.ToArray())
            if (!IsWindow(entry.Key) || !IsWindowVisible(entry.Key))
            {
                log?.Invoke($"MT4 PID {entry.Value.pid}: 起動時の画面をキャンセル済み: {entry.Value.title}");
                pending.Remove(entry.Key);
            }
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId || !IsWindowVisible(window) || ClassName(window) != "#32770") return true;
            string title = Caption(window);
            nint cancel = GetDlgItem(window, 2); // Native IDCANCEL, never an inferred coordinate.
            if (cancel == 0 || ClassName(cancel) != "Button" || !IsWindowEnabled(cancel) || !IsWindowVisible(cancel)) return true;
            string button = Caption(cancel).Replace("&", "").Trim();
            if (!button.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase) && !button.StartsWith("キャンセル", StringComparison.Ordinal)) return true;
            if (!KnownTitles.Contains(title.Trim()) && !HasLoginLabels(window)) return true;
            if (pending.TryGetValue(window, out var previous) && (previous.attempts >= 3 || lifetime.ElapsedMilliseconds - previous.sent < 1000)) return true;
            // Recheck ownership immediately before posting the cancel command.
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId) return true;
            if (PostMessage(window, 0x0111, 2, cancel))
            {
                string safeTitle = Regex.Replace(title, @"\d{5,}", "[number]");
                int attempts = pending.TryGetValue(window, out previous) ? previous.attempts + 1 : 1;
                pending[window] = (processId, safeTitle, lifetime.ElapsedMilliseconds, attempts);
                if (attempts == 1) log?.Invoke($"MT4 PID {processId}: 起動時の口座案内・ログイン画面へキャンセルを要求: {safeTitle}");
            }
            return true;
        }, 0);
    }

    private static bool HasLoginLabels(nint window)
    {
        bool login = false, password = false; int edits = 0;
        EnumChildWindows(window, (child, _) =>
        {
            if (!IsWindowVisible(child)) return true;
            string kind = ClassName(child);
            if (kind == "Edit") { edits++; return true; } // Deliberately do not read its text.
            if (kind != "Static") return true;
            var label = new StringBuilder(128);
            SendMessageTimeout(child, 0x000D, (nuint)label.Capacity, label, 0x0002, 50, out nuint ignored);
            string text = label.ToString().Replace("&", "").Replace(":", "").Replace("：", "").Trim();
            login |= text.Equals("Login", StringComparison.OrdinalIgnoreCase) || text is "ログイン" or "ログインID";
            password |= text.Equals("Password", StringComparison.OrdinalIgnoreCase) || text == "パスワード";
            return true;
        }, 0);
        return edits >= 2 && login && password;
    }

    private static string ClassName(nint window) { var text = new StringBuilder(128); GetClassName(window, text, text.Capacity); return text.ToString(); }
    private static string Caption(nint window) { var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wparam, StringBuilder text, uint flags, uint timeout, out nuint result);
}
